CLUSTER        := otel-lab
NAMESPACE      := otel-lab
HELM_NAMESPACE := monitoring
HELM_RELEASE   := grafana-k8s
HELM_CHART     := grafana/k8s-monitoring
HELM_VERSION   := 3.8.4
IMAGES         := otel-frontend gateway-api order-api notification-svc

.PHONY: cluster-up cluster-down build import deploy deploy-cloud deploy-local teardown test logs full validate \
        helm-repo helm-render deploy-helm teardown-helm full-helm \
        secrets-fetch-akv secrets-apply secrets-show \
        test-unit test-dotnet test-python test-frontend

cluster-up:
	k3d cluster create $(CLUSTER) \
	  -p "8080:80@loadbalancer" \
	  -p "16686:30686@server:0" \
	  -p "3000:30300@server:0" \
	  -p "9090:30090@server:0" \
	  -p "15672:30672@server:0"
	@# ── Corporate CA injection (Zscaler) ──────────────────────────────────────
	@# Inject the corporate CA into the k3d server node so k3s can pull images
	@# from external registries (e.g. ghcr.io) and validate TLS through Zscaler.
	@# No-op when /usr/local/share/ca-certificates/zcert.crt is absent (non-corporate).
	@if [ -f /usr/local/share/ca-certificates/zcert.crt ]; then \
	  echo "Injecting corporate CA cert into k3d-$(CLUSTER)-server-0 ..."; \
	  docker cp /usr/local/share/ca-certificates/zcert.crt \
	    k3d-$(CLUSTER)-server-0:/tmp/zcert.crt; \
	  docker exec k3d-$(CLUSTER)-server-0 sh -c \
	    "cat /tmp/zcert.crt >> /etc/ssl/certs/ca-certificates.crt && rm /tmp/zcert.crt"; \
	  echo "Cert injected. Waiting 8s for k3s to stabilise ..."; \
	  sleep 8; \
	  docker exec k3d-$(CLUSTER)-serverlb nginx -s reload; \
	  echo "nginx LB reloaded — kubectl should be responsive."; \
	else \
	  echo "No zcert.crt found — skipping corporate CA injection."; \
	fi

cluster-down:
	k3d cluster delete $(CLUSTER)

build:
	# Copy corporate CA (Zscaler) into each build context so `npm ci` (Node),
	# `dotnet restore` (NuGet), and `pip install` (PyPI) can reach registries
	# through Zscaler's TLS interception. Empty placeholder is staged on
	# non-corporate machines so the Dockerfile COPY never fails.
	# Cert is cleaned up after each build so it is never committed.
	@if [ -f /usr/local/share/ca-certificates/zcert.crt ]; then \
	  cp /usr/local/share/ca-certificates/zcert.crt ./src/frontend/zcert.crt; \
	else \
	  : > ./src/frontend/zcert.crt; \
	fi
	# FARO_API_KEY is forwarded when set so the webpack plugin uploads source maps.
	# Leave unset for local builds; set from AKV in CI before running make import.
	docker build --network=host -t otel-frontend:local \
	  $$([ -n "$$FARO_API_KEY" ] && echo "--build-arg FARO_API_KEY=$$FARO_API_KEY") \
	  ./src/frontend
	rm -f ./src/frontend/zcert.crt
	cp /usr/local/share/ca-certificates/zcert.crt ./src/gateway-api/zcert.crt
	docker build --network=host -t gateway-api:local     ./src/gateway-api
	rm ./src/gateway-api/zcert.crt
	cp /usr/local/share/ca-certificates/zcert.crt ./src/order-api/zcert.crt
	docker build --network=host -t order-api:local       ./src/order-api
	rm ./src/order-api/zcert.crt
	cp /usr/local/share/ca-certificates/zcert.crt ./src/notification-svc/zcert.crt
	docker build --network=host -t notification-svc:local ./src/notification-svc
	rm ./src/notification-svc/zcert.crt

import: build
	k3d image import $(addsuffix :local,$(IMAGES)) -c $(CLUSTER)

deploy: deploy-cloud

# Deploy with Grafana Cloud as the collector backend.
# Requires grafana-cloud-secrets to be populated first: make secrets-fetch-akv
deploy-cloud:
	kubectl apply -f k8s/infra/namespace.yaml
	kubectl apply -f k8s/infra/secrets.yaml
	kubectl apply -f k8s/datastores/mysql/ -f k8s/datastores/postgres/ -f k8s/datastores/redis/ -f k8s/datastores/rabbitmq/
	kubectl -n $(NAMESPACE) wait --for=condition=ready pod -l tier=datastore --timeout=180s
	kubectl apply -f k8s/monitoring/grafana/ -f k8s/monitoring/grafana/grafana-cloud/
	kubectl apply -f k8s/app/gateway/ -f k8s/app/order/ -f k8s/app/notification/ -f k8s/app/frontend/
	kubectl apply -f k8s/infra/ingress.yaml

# Deploy with local backends (Jaeger, Prometheus, Loki, Grafana) as the collector backend.
# No cloud credentials required.
deploy-local:
	kubectl apply -f k8s/infra/namespace.yaml
	kubectl apply -f k8s/infra/secrets.yaml
	kubectl apply -f k8s/datastores/mysql/ -f k8s/datastores/postgres/ -f k8s/datastores/redis/ -f k8s/datastores/rabbitmq/
	kubectl -n $(NAMESPACE) wait --for=condition=ready pod -l tier=datastore --timeout=180s
	kubectl apply -f k8s/monitoring/grafana/ -f k8s/monitoring/grafana/local/ -f k8s/monitoring/local/jaeger/ -f k8s/monitoring/local/prometheus/ -f k8s/monitoring/local/loki/
	kubectl apply -f k8s/monitoring/local/grafana/
	kubectl apply -f k8s/app/gateway/ -f k8s/app/order/ -f k8s/app/notification/ -f k8s/app/frontend/
	kubectl apply -f k8s/infra/ingress.yaml

teardown:
	kubectl delete namespace $(NAMESPACE) --ignore-not-found

# Run all unit/integration tests locally — no cluster required.
test-unit: test-dotnet test-python test-frontend
	@echo ""
	@echo "All unit tests passed."

# Run .NET xUnit test suites (order-api and gateway-api).
test-dotnet:
	dotnet test src/order-api.Tests/order-api.Tests.csproj
	dotnet test src/gateway-api.Tests/gateway-api.Tests.csproj

# Run Python pytest suite for notification-svc.
# Creates a local venv on first run; safe to re-run.
test-python:
	@if [ ! -d src/notification-svc/.venv ]; then \
	  python3 -m venv src/notification-svc/.venv; \
	  src/notification-svc/.venv/bin/pip install -q -r src/notification-svc/requirements-test.txt; \
	fi
	cd src/notification-svc && .venv/bin/python -m pytest tests/ -v

# Run Angular Jest tests.
# Installs jest-preset-angular to /tmp/ng-test-deps on first run (avoids touching root-owned node_modules).
test-frontend:
	@if [ ! -d /tmp/ng-test-deps/node_modules/jest-preset-angular ]; then \
	  npm install --prefix /tmp/ng-test-deps \
	    jest jest-environment-jsdom jest-preset-angular \
	    @types/jest typescript --legacy-peer-deps --silent; \
	fi
	cd src/frontend && \
	  NODE_PATH=/tmp/ng-test-deps/node_modules \
	  /tmp/ng-test-deps/node_modules/.bin/jest --config jest.config.js

# Apply load-test manifests to the running cluster (requires a live cluster).
test:
	kubectl apply -f k8s/loadtest/

logs:
	kubectl -n $(NAMESPACE) logs -l tier=app -f --prefix --max-log-requests=10

# =============================================================================
# Grafana Cloud credentials — sourced from Azure Key Vault.
#
# Preferred workflow (no manual credential handling):
#   make secrets-fetch-akv   # pulls grafana-mccaindev-* from AKV, applies to cluster
#
# Manual fallback (fill .env first):
#   cp .env.example .env && vi .env
#   make secrets-apply
#
# Verify:
#   make secrets-show
# =============================================================================

# Pull grafana-mccaindev-* secrets from Azure Key Vault and apply directly as
# the grafana-cloud-secrets K8s Secret.  SP credentials are read from .env.
# Endpoint paths are adjusted to match the Alloy exporter format:
#   Tempo:  strip https://, append :443       (OTLP gRPC — no URL scheme)
#   Mimir:  append /api/v1/otlp               (OTLP HTTP)
#   Loki:   append /loki/api/v1/push          (Loki HTTP push)
secrets-fetch-akv:
	@test -f .env || (echo "ERROR: .env not found. Run: cp .env.example .env and fill in ARM_* fields" && exit 1)
	@set -a; . ./.env; set +a; \
	az login --service-principal \
	  --username "$${ARM_CLIENT_ID}" \
	  --password "$${ARM_CLIENT_SECRET}" \
	  --tenant  "$${ARM_TENANT_ID}" \
	  --output none && \
	KV="$${Azure_KeyVault}" && \
	API_KEY=$$(az keyvault secret show --vault-name "$$KV" --name grafana-mccaindev-alloy-writer-mccaindev-token --query value -o tsv) && \
	TEMPO_HOST=$$(az keyvault secret show --vault-name "$$KV" --name grafana-mccaindev-cloud-tempo-endpoint  --query value -o tsv | sed 's|https://||') && \
	TEMPO_USER=$$(az keyvault secret show --vault-name "$$KV" --name grafana-mccaindev-cloud-tempo-username  --query value -o tsv) && \
	MIMIR_BASE=$$(az keyvault secret show --vault-name "$$KV" --name grafana-mccaindev-cloud-mimir-endpoint  --query value -o tsv) && \
	MIMIR_USER=$$(az keyvault secret show --vault-name "$$KV" --name grafana-mccaindev-cloud-mimir-username  --query value -o tsv) && \
	LOKI_BASE=$$(az keyvault secret show  --vault-name "$$KV" --name grafana-mccaindev-cloud-loki-endpoint   --query value -o tsv) && \
	LOKI_USER=$$(az keyvault secret show  --vault-name "$$KV" --name grafana-mccaindev-cloud-loki-username   --query value -o tsv) && \
	FARO_URL=$$(az keyvault secret show   --vault-name "$$KV" --name grafana-mccaindev-faro-signal-forge-collection-url    --query value -o tsv) && \
	FARO_KEY=$$(az keyvault secret show   --vault-name "$$KV" --name grafana-mccaindev-faro-signal-forge-sourcemap-token    --query value -o tsv) && \
	kubectl create secret generic grafana-cloud-secrets \
	  --namespace $(NAMESPACE) \
	  --from-literal=GRAFANA_CLOUD_API_KEY="$$API_KEY" \
	  --from-literal=GRAFANA_CLOUD_TEMPO_ENDPOINT="$${TEMPO_HOST}:443" \
	  --from-literal=GRAFANA_CLOUD_TEMPO_USER="$$TEMPO_USER" \
	  --from-literal=GRAFANA_CLOUD_MIMIR_ENDPOINT="$${MIMIR_BASE}/api/v1/otlp" \
	  --from-literal=GRAFANA_CLOUD_MIMIR_USER="$$MIMIR_USER" \
	  --from-literal=GRAFANA_CLOUD_LOKI_ENDPOINT="$${LOKI_BASE}/loki/api/v1/push" \
	  --from-literal=GRAFANA_CLOUD_LOKI_USER="$$LOKI_USER" \
	  --from-literal=FARO_COLLECTOR_URL="$$FARO_URL" \
	  --from-literal=FARO_API_KEY="$$FARO_KEY" \
	  --dry-run=client -o yaml | kubectl apply -f - && \
	MODE=akv \
	API_KEY="$$API_KEY" \
	TEMPO_HOST="$$TEMPO_HOST" TEMPO_USER="$$TEMPO_USER" \
	MIMIR_BASE="$$MIMIR_BASE" MIMIR_USER="$$MIMIR_USER" \
	LOKI_BASE="$$LOKI_BASE"   LOKI_USER="$$LOKI_USER" \
	python3 k8s/monitoring/grafana-helm/gen-cloud-overlay.py > /tmp/gc-overlay.yaml && \
	helm upgrade --install $(HELM_RELEASE) $(HELM_CHART) \
	  --version $(HELM_VERSION) \
	  -n $(HELM_NAMESPACE) \
	  -f k8s/monitoring/grafana-helm/values-local.yaml \
	  -f /tmp/gc-overlay.yaml && \
	rm -f /tmp/gc-overlay.yaml
	@echo "Credentials applied and Helm release updated with Grafana Cloud destinations."
	@echo "Rolling frontend to pick up updated FARO_URL..."
	kubectl rollout restart deployment/otel-frontend -n $(NAMESPACE)
	kubectl rollout status deployment/otel-frontend -n $(NAMESPACE) --timeout=60s

# Read .env manually and apply (fallback when AKV is not accessible).
# .env must have the pre-formatted values (see .env.example).
secrets-apply:
	@test -f .env || (echo "ERROR: .env not found. Run: cp .env.example .env" && exit 1)
	@set -a; . ./.env; set +a; \
	kubectl create secret generic grafana-cloud-secrets \
	  --namespace $(NAMESPACE) \
	  --from-literal=GRAFANA_CLOUD_API_KEY="$${GRAFANA_CLOUD_API_KEY}" \
	  --from-literal=GRAFANA_CLOUD_TEMPO_ENDPOINT="$${GRAFANA_CLOUD_TEMPO_ENDPOINT}" \
	  --from-literal=GRAFANA_CLOUD_TEMPO_USER="$${GRAFANA_CLOUD_TEMPO_USER}" \
	  --from-literal=GRAFANA_CLOUD_MIMIR_ENDPOINT="$${GRAFANA_CLOUD_MIMIR_ENDPOINT}" \
	  --from-literal=GRAFANA_CLOUD_MIMIR_USER="$${GRAFANA_CLOUD_MIMIR_USER}" \
	  --from-literal=GRAFANA_CLOUD_LOKI_ENDPOINT="$${GRAFANA_CLOUD_LOKI_ENDPOINT}" \
	  --from-literal=GRAFANA_CLOUD_LOKI_USER="$${GRAFANA_CLOUD_LOKI_USER}" \
	  --from-literal=FARO_COLLECTOR_URL="$${FARO_COLLECTOR_URL}" \
	  --from-literal=FARO_API_KEY="$${FARO_API_KEY}" \
	  --dry-run=client -o yaml | kubectl apply -f - && \
	MODE=env \
	GRAFANA_CLOUD_API_KEY="$${GRAFANA_CLOUD_API_KEY}" \
	GRAFANA_CLOUD_TEMPO_ENDPOINT="$${GRAFANA_CLOUD_TEMPO_ENDPOINT}" GRAFANA_CLOUD_TEMPO_USER="$${GRAFANA_CLOUD_TEMPO_USER}" \
	GRAFANA_CLOUD_MIMIR_ENDPOINT="$${GRAFANA_CLOUD_MIMIR_ENDPOINT}" GRAFANA_CLOUD_MIMIR_USER="$${GRAFANA_CLOUD_MIMIR_USER}" \
	GRAFANA_CLOUD_LOKI_ENDPOINT="$${GRAFANA_CLOUD_LOKI_ENDPOINT}"   GRAFANA_CLOUD_LOKI_USER="$${GRAFANA_CLOUD_LOKI_USER}" \
	python3 k8s/monitoring/grafana-helm/gen-cloud-overlay.py > /tmp/gc-overlay.yaml && \
	helm upgrade --install $(HELM_RELEASE) $(HELM_CHART) \
	  --version $(HELM_VERSION) \
	  -n $(HELM_NAMESPACE) \
	  -f k8s/monitoring/grafana-helm/values-local.yaml \
	  -f /tmp/gc-overlay.yaml && \
	rm -f /tmp/gc-overlay.yaml
	@echo "Credentials applied and Helm release updated with Grafana Cloud destinations."
	@echo "Rolling frontend to pick up updated FARO_URL..."
	kubectl rollout restart deployment/otel-frontend -n $(NAMESPACE)
	kubectl rollout status deployment/otel-frontend -n $(NAMESPACE) --timeout=60s

# Show the currently stored secret values (base64-decoded, API key redacted).
secrets-show:
	@echo "=== grafana-cloud-secrets ==="
	@kubectl get secret grafana-cloud-secrets -n $(NAMESPACE) -o json 2>/dev/null \
	  | python3 -c 'import json,sys,base64; \
	    REDACT={"GRAFANA_CLOUD_API_KEY","FARO_API_KEY"}; \
	    s=json.load(sys.stdin)["data"]; \
	    [print(f"  {k}: {v[:4]+\"****\"+v[-4:] if k in REDACT and len(v)>8 else v or \"(empty)\"}") \
	     for k,v in {k:base64.b64decode(v).decode() if v else "" for k,v in s.items()}.items()]' \
	  || echo "  Secret not found — run: make secrets-fetch-akv"

validate:
	@echo "=== Checking endpoints ==="
	curl -sf http://localhost:8080/api/projects | python3 -m json.tool
	@echo "=== Frontend ===" && curl -sfI http://localhost:8080 | head -5
	@echo "=== Jaeger ===" && curl -sfI http://localhost:16686 | head -3
	@echo "=== Grafana ===" && curl -sfI http://localhost:3000 | head -3
	@echo "=== Prometheus ===" && curl -sfI http://localhost:9090 | head -3
	@echo "=== RabbitMQ ===" && curl -sfI http://localhost:15672 | head -3

full: cluster-up import deploy-cloud
	@echo ""
	@echo "Lab is up! Collector → Grafana Cloud (run make secrets-fetch-akv first)"
	@echo "  Frontend:   http://localhost:8080"
	@echo "  Grafana:    http://localhost:3000  (admin/admin)"
	@echo "  Jaeger:     http://localhost:16686"
	@echo "  Prometheus: http://localhost:9090"
	@echo "  RabbitMQ:   http://localhost:15672 (guest/guest)"

# =============================================================================
# Helm-based cluster monitoring (grafana/k8s-monitoring v$(HELM_VERSION))
# Incorporates the production-grade Alloy collector pipeline from
# f-observability/09-grafana-k8s, adapted for the local k3d stack.
#
# Deployment modes:
#   make deploy-helm          — local k3d (Prometheus + Loki + Jaeger as backends)
#   GC_API_TOKEN=<t> make helm-render && make deploy-helm-cloud
#                             — dual-export: local + Grafana Cloud
#
# Five Alloy collector roles deployed:
#   alloy-metrics    (StatefulSet)  — infra metrics scraping
#   alloy-singleton  (Deployment)   — cluster-scoped collection (events, KSM)
#   alloy-logs       (DaemonSet)    — pod + node log tailing
#   alloy-receiver   (DaemonSet)    — OTLP push receiver from apps
#   alloy-profiles   (DaemonSet)    — disabled (no local Pyroscope)
# =============================================================================

# Add Grafana Helm repo (idempotent — safe to run multiple times).
helm-repo:
	helm repo add grafana https://grafana.github.io/helm-charts
	helm repo update

# Render Jinja2 template → Helm values for all clusters in k8s/monitoring/grafana-helm/values.yaml.
# Requires: pip install jinja2 pyyaml
# For cloud export set GC_API_TOKEN first; for local-only the token is optional.
helm-render:
	python3 k8s/monitoring/grafana-helm/render.py

# Deploy using local values (no Grafana Cloud credentials needed).
# Prerequisites: make deploy must have run first so Prometheus/Loki/Jaeger exist.
deploy-helm: helm-repo
	helm upgrade --install $(HELM_RELEASE) $(HELM_CHART) \
	  --version $(HELM_VERSION) \
	  -n $(HELM_NAMESPACE) --create-namespace \
	  -f k8s/monitoring/grafana-helm/values-local.yaml
	@echo ""
	@echo "Helm monitoring stack deployed to namespace: $(HELM_NAMESPACE)"
	@echo "Check status: kubectl get pods -n $(HELM_NAMESPACE)"
	@echo ""
	@echo "Alloy agents:"
	@echo "  alloy-metrics   — scraping cluster infra metrics → Prometheus"
	@echo "  alloy-singleton — cluster events, kube-state-metrics → Loki/Prometheus"
	@echo "  alloy-logs      — pod + node log tailing → Loki"
	@echo "  alloy-receiver  — OTLP push receiver (port 4317/4318) → Jaeger"

# Deploy using rendered cloud values (requires helm-render to have run first).
# GC_API_TOKEN must have been set when running helm-render.
deploy-helm-cloud:
	helm upgrade --install $(HELM_RELEASE) $(HELM_CHART) \
	  --version $(HELM_VERSION) \
	  -n $(HELM_NAMESPACE) --create-namespace \
	  -f k8s/monitoring/grafana-helm/generated/signal-forge-local-otel-lab.yml

# Remove the Helm monitoring stack (leaves otel-lab app namespace intact).
teardown-helm:
	helm uninstall $(HELM_RELEASE) -n $(HELM_NAMESPACE) --ignore-not-found
	kubectl delete namespace $(HELM_NAMESPACE) --ignore-not-found

# Full lab + Helm monitoring in one command.
full-helm: cluster-up import deploy deploy-helm
	@echo ""
	@echo "Lab is up with full Helm-managed monitoring!"
	@echo "  Frontend:   http://localhost:8080"
	@echo "  Grafana:    http://localhost:3000  (admin/admin)"
	@echo "  Jaeger:     http://localhost:16686"
	@echo "  Prometheus: http://localhost:9090"
	@echo "  RabbitMQ:   http://localhost:15672 (guest/guest)"
	@echo ""
	@echo "Helm monitoring: kubectl get pods -n $(HELM_NAMESPACE)"
