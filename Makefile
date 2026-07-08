# ./deploy-local.sh is the sole deploy path (cluster + builds + manifests + Helm,
# driven entirely by conf.yml). This Makefile only builds images, runs tests, and
# fetches/applies Grafana Cloud credentials — it no longer deploys anything itself.
# The deploy/deploy-cloud/deploy-local/full/helm-repo/helm-render/deploy-helm/
# deploy-helm-cloud/teardown-helm/full-helm targets that used to live here were
# retired: three independent, partially-broken Helm-values pipelines (this
# Makefile's Jinja2-based one, this Makefile's legacy kubectl-apply one, and
# deploy-local.sh's) is two too many for one responsibility, and deploy-cloud's
# `k8s/monitoring/grafana/grafana-cloud/` target directory didn't even exist.
CLUSTER        := otel-lab
NAMESPACE      := otel-lab
HELM_NAMESPACE := monitoring
HELM_RELEASE   := grafana-k8s
HELM_CHART     := grafana/k8s-monitoring
HELM_VERSION   := 3.8.4
IMAGES         := otel-frontend gateway-api order-api notification-svc

.PHONY: cluster-up cluster-down build import teardown test logs validate \
        secrets-fetch-akv secrets-apply secrets-show \
        test-unit test-dotnet test-python test-frontend \
        deploy deploy-cloud deploy-local full

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

# Explicit stubs for the retired deploy targets: without these, `make deploy-local`
# falls through to GNU Make's built-in `%: %.sh` suffix rule (deploy-local.sh exists
# on disk) and silently creates a useless `deploy-local` copy instead of erroring —
# a confusing failure mode for anyone still muscle-memory typing the old command.
deploy deploy-cloud deploy-local full:
	@echo "make $@ was retired — deploy with ./deploy-local.sh instead (see CLAUDE.md)." >&2
	@exit 1

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
#   make secrets-fetch-akv   # pulls grafana-example-org-* from AKV, applies to cluster
#
# Manual fallback (fill .env first):
#   cp .env.example .env && vi .env
#   make secrets-apply
#
# Verify:
#   make secrets-show
# =============================================================================

# Pull grafana-example-org-* secrets from Azure Key Vault and apply directly as
# the grafana-cloud-secrets K8s Secret.  SP credentials are read from .env.
# Endpoint paths are adjusted to match the Alloy exporter format:
#   Tempo:  strip https://, append :443       (OTLP gRPC — no URL scheme)
#   Mimir:  append /api/prom/push             (Prometheus remote_write, not OTLP HTTP —
#                                               matches values-cloud.yaml.tmpl; a prior
#                                               version of this target wrote /api/v1/otlp,
#                                               which the live chart-based pipeline doesn't
#                                               speak — see docs/deployment/grafana-cloud.md)
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
	API_KEY=$$(az keyvault secret show --vault-name "$$KV" --name grafana-example-org-alloy-writer-example-org-token --query value -o tsv) && \
	TEMPO_HOST=$$(az keyvault secret show --vault-name "$$KV" --name grafana-example-org-cloud-tempo-endpoint  --query value -o tsv | sed 's|https://||') && \
	TEMPO_USER=$$(az keyvault secret show --vault-name "$$KV" --name grafana-example-org-cloud-tempo-username  --query value -o tsv) && \
	MIMIR_BASE=$$(az keyvault secret show --vault-name "$$KV" --name grafana-example-org-cloud-mimir-endpoint  --query value -o tsv) && \
	MIMIR_USER=$$(az keyvault secret show --vault-name "$$KV" --name grafana-example-org-cloud-mimir-username  --query value -o tsv) && \
	LOKI_BASE=$$(az keyvault secret show  --vault-name "$$KV" --name grafana-example-org-cloud-loki-endpoint   --query value -o tsv) && \
	LOKI_USER=$$(az keyvault secret show  --vault-name "$$KV" --name grafana-example-org-cloud-loki-username   --query value -o tsv) && \
	FARO_URL=$$(az keyvault secret show   --vault-name "$$KV" --name grafana-example-org-faro-signal-forge-collection-url    --query value -o tsv) && \
	FARO_KEY=$$(az keyvault secret show   --vault-name "$$KV" --name grafana-example-org-faro-signal-forge-sourcemap-token    --query value -o tsv) && \
	kubectl create secret generic grafana-cloud-secrets \
	  --namespace $(NAMESPACE) \
	  --from-literal=GRAFANA_CLOUD_API_KEY="$$API_KEY" \
	  --from-literal=GRAFANA_CLOUD_TEMPO_ENDPOINT="$${TEMPO_HOST}:443" \
	  --from-literal=GRAFANA_CLOUD_TEMPO_USER="$$TEMPO_USER" \
	  --from-literal=GRAFANA_CLOUD_MIMIR_ENDPOINT="$${MIMIR_BASE}/api/prom/push" \
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

