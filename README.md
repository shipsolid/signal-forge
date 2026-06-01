# SignalForge — OTel Microservices Validation Lab

End-to-end OpenTelemetry instrumentation lab across .NET 8, Python/FastAPI, and Angular 17, deployed on k3d with Helm-managed Grafana Alloy agents as the collector stack.

**What it validates:** traces (5-hop cross-language), span metrics, exemplars, async trace propagation via RabbitMQ, frontend RUM with Faro, log-to-trace correlation via Loki, and tail-based sampling.

## Two deployment tools (pick one)

| Tool                    | Primary config                                           | Credentials flow                                                             | Status                                                                                                                                                                                                                   |
| ----------------------- | -------------------------------------------------------- | ---------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| **`./deploy-local.sh`** | [conf.yml](conf.yml)                                     | `./scripts/fetch-grafana-cloud-conf-from-akv.sh` → updates conf.yml in place | **Recommended.** Context-guard (refuses non-k3d contexts), NodePort drift-check, secret-key contract validator, kustomize-aware apply.                                                                                   |
| `make …` (Makefile)     | `.env` + `k8s/monitoring/grafana-helm/values-local.yaml` | `make secrets-fetch-akv` → writes Secret directly                            | Legacy. Still works for the base flow, but `make secrets-fetch-akv` writes the old Mimir URL (`/api/v1/otlp`) which is **incompatible** with the current cloud destination (Prometheus remote_write). Don't mix the two. |

Everything below defaults to `./deploy-local.sh`. Makefile targets are listed in §"Make Targets Reference" for reference only.

## Prerequisites

| Tool      | Min version | Install                                                                                                                                                                                 |
| --------- | ----------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Docker    | 24+         | [docs.docker.com](https://docs.docker.com/get-docker/)                                                                                                                                  |
| k3d       | v5+         | `curl -s https://raw.githubusercontent.com/k3d-io/k3d/main/install.sh \| bash`                                                                                                          |
| kubectl   | v1.28+      | [kubernetes.io/docs](https://kubernetes.io/docs/tasks/tools/)                                                                                                                           |
| helm      | v3.14+      | `curl https://raw.githubusercontent.com/helm/helm/main/scripts/get-helm-3 \| bash`                                                                                                      |
| Azure CLI | 2.50+       | `curl -sL https://aka.ms/InstallAzureCLIDeb \| sudo bash` — needed only if you pull Grafana Cloud credentials from Azure Key Vault via `./scripts/fetch-grafana-cloud-conf-from-akv.sh` |
| Python 3  | 3.9+        | system package — parses [conf.yml](conf.yml), renders the helm values template, drives the debug / smoke-test scripts                                                                   |

## Quick Start

```bash
# Cluster + builds + apply + Helm install (monitoring.mode read from conf.yml):
./deploy-local.sh

# Later iteration, manifests-only (cluster and images already in place):
./deploy-local.sh --skip-cluster --skip-build

# Triage after a deploy:
./scripts/debug.sh

# Tear down everything:
./deploy-local.sh --teardown
```

On first run `./deploy-local.sh` takes ~5-15 minutes (Docker builds of 4 images + k3d create + cert-manager install + Helm release rollout). Subsequent `--skip-cluster --skip-build` runs complete in <1 min.

### Deployment modes

The single most important knob is `monitoring.mode` in [conf.yml](conf.yml):

| `monitoring.mode` | Observability pipeline                                               | In-cluster backends                                             |
| ----------------- | -------------------------------------------------------------------- | --------------------------------------------------------------- |
| `cloud` (default) | `grafana/k8s-monitoring` Helm chart → Grafana Cloud Tempo/Mimir/Loki | none (the Helm chart's Alloy agents _are_ the pipeline)         |
| `local`           | Bespoke Alloy DaemonSet → in-cluster Jaeger/Prometheus/Loki/Grafana  | Jaeger :16686, Prometheus :9090, Grafana :3000, Loki (internal) |

Switch modes by editing `monitoring.mode` in conf.yml and redeploying. The Helm release is still installed in local mode — apps target `grafana-k8s-alloy-receiver` regardless — but its destinations point at the in-cluster services.

### Alloy roles (Helm release in ns/monitoring)

| Alloy role        | Kind        | Responsibility                     | Destination (cloud)                   | Destination (local)                        |
| ----------------- | ----------- | ---------------------------------- | ------------------------------------- | ------------------------------------------ |
| `alloy-metrics`   | StatefulSet | Scrapes cluster infra metrics      | Grafana Cloud Mimir                   | in-cluster Prometheus                      |
| `alloy-singleton` | Deployment  | Cluster events, kube-state-metrics | Cloud Mimir + Loki                    | in-cluster Prom + Loki                     |
| `alloy-logs`      | DaemonSet   | Pod + node log tailing             | Grafana Cloud Loki                    | in-cluster Loki                            |
| `alloy-receiver`  | DaemonSet   | OTLP push receiver (app telemetry) | Cloud Tempo (traces), Mimir (metrics) | in-cluster Jaeger (traces), Prom (metrics) |
| `alloy-profiles`  | DaemonSet   | Disabled (no local Pyroscope)      | —                                     | —                                          |

Values: [values-local.yaml](k8s/monitoring/grafana-helm/values-local.yaml) (local mode) or [values-cloud.yaml.tmpl](k8s/monitoring/grafana-helm/values-cloud.yaml.tmpl) (cloud mode — rendered at deploy time from conf.yml).

## Endpoints

**Always available** (both modes):

| URL                               | Service                   | Notes                                                                                                           |
| --------------------------------- | ------------------------- | --------------------------------------------------------------------------------------------------------------- |
| `http://localhost:8080`           | Angular SPA + Gateway API | `/api/*` routes to gateway, `/` to frontend                                                                     |
| `https://signal-forge.local:8443` | Same, via TLS             | Requires `security.tls.enabled: true` + `/etc/hosts` entry — see [networking.md](docs/operations/networking.md) |
| `http://localhost:15672`          | RabbitMQ Management       | guest/guest                                                                                                     |

**Only in `monitoring.mode: local`:**

| URL                      | Service    | Notes       |
| ------------------------ | ---------- | ----------- |
| `http://localhost:16686` | Jaeger UI  |             |
| `http://localhost:3000`  | Grafana    | admin/admin |
| `http://localhost:9090`  | Prometheus |             |

**Only in `monitoring.mode: cloud`:**

| URL                                                             | Service           | Notes                                           |
| --------------------------------------------------------------- | ----------------- | ----------------------------------------------- |
| Your Grafana Cloud stack (e.g. `https://mccaindev.grafana.net`) | Grafana + Explore | Traces in Tempo, metrics in Mimir, logs in Loki |

## Architecture

```text
Browser (Faro RUM)
  └─► gateway-api (.NET 8, :5000)
        ├─► MySQL 8 (EF Core)
        ├─► order-api (.NET 8, gRPC :5001)
        │     ├─► PostgreSQL 16 (Npgsql)
        │     └─► RabbitMQ → notification-svc (Python, :8000)
        │                          └─► Redis 7
        └─► notification-svc (HTTP)
```

All services export OTLP → `alloy-receiver` (Helm-managed DaemonSet in the `monitoring` namespace) which:

- Enriches spans with k8s attributes
- Filters `/healthz` spans
- Applies tail-sampling (errors=100%, slow>2s=100%, rest=25%)
- Generates span metrics via spanmetrics connector (RED metrics)

Where the signals then land depends on `monitoring.mode` in [conf.yml](conf.yml):

- **cloud (default):** the chart's Alloy agents ship → Grafana Cloud Tempo / Mimir / Loki. No in-cluster backends are deployed.
- **local:** a parallel bespoke Alloy DaemonSet in [k8s/monitoring/grafana/](k8s/monitoring/grafana/) exports to in-cluster Jaeger / Prometheus / Loki / Grafana. The Helm chart is still installed and still serves as the app OTLP ingress; only its destinations change.

`alloy-logs` (DaemonSet) tails pod stdout/stderr with trace-id correlation.
`alloy-metrics` (StatefulSet) scrapes cluster infra metrics.

The two modes are mutually exclusive — there is no dual-export today. See [observability/pipeline.md](docs/observability/pipeline.md) for the full signal flow and [OTEL-PATTERNS.md](docs/OTEL-PATTERNS.md) for the instrumentation choices on the application side.

## Repository Layout

```text
11-signal-forge/
├── src/                        # Application source code
│   ├── gateway-api/            # .NET 8 Minimal API — BFF, MySQL, gRPC client
│   ├── order-api/              # .NET 8 gRPC — PostgreSQL, RabbitMQ publisher
│   ├── notification-svc/       # Python FastAPI — RabbitMQ consumer, Redis
│   ├── frontend/               # Angular 17 SPA — Faro RUM, nginx
│   └── proto/                  # Shared gRPC protobuf definitions
│
├── k8s/                        # Kubernetes manifests
│   ├── base/                   # Kustomize base (ArgoCD / Flux entrypoint)
│   ├── overlays/{dev,staging,prod}/  # env-specific Kustomize overlays
│   ├── infra/                  # namespace, secrets, PDB, NetworkPolicies, ingress, cert-manager issuer
│   ├── app/                    # Application service deployments (with per-dir kustomization.yaml)
│   ├── datastores/             # MySQL, PostgreSQL, Redis, RabbitMQ (with per-dir kustomization.yaml)
│   ├── monitoring/
│   │   ├── slo-rules.yaml      #   PrometheusRule (SLOs + burn-rate alerts)
│   │   ├── grafana/            #   Bespoke Alloy DaemonSet (applied only in local mode)
│   │   │   └── local/          #     River config for local backends
│   │   ├── grafana-helm/       #   grafana/k8s-monitoring Helm values (local + cloud template)
│   │   └── local/              #   Local k3d backends: Jaeger, Prometheus, Loki, Grafana
│   └── loadtest/               # k6 load test Job
│
├── conf.yml                    # Single source of truth for every knob deploy-local.sh consumes
├── deploy-local.sh             # Stand up signal-forge on local k3d (idempotent)
├── scripts/                    # fetch-grafana-cloud-conf-from-akv.sh, debug.sh, smoke tests
├── Makefile                    # Cluster lifecycle, builds, deployments, secrets
└── docs/
    ├── spec.md                 # OTel validation test scenarios and checklist
    └── OTEL-PATTERNS.md        # Instrumentation patterns and best practices
```

> **Deploy order within `k8s/`:** `infra/` → `app-env ConfigMap` → `grafana-cloud-secrets` → `cert-manager` → `datastores/` → `monitoring/` → `app/` → `post` (ingress) — driven by `deploy-local.sh` with context-guard and NodePort drift-check.

## Production readiness

For any step beyond the local lab, these docs describe the controls already baked into the manifests and what still needs your attention:

- [Container hardening](docs/infrastructure/hardening.md) — securityContext, non-root UIDs, digest-pinned base images
- [Kustomize layout](docs/infrastructure/kustomize.md) — base + overlays for dev/staging/prod
- [Reliability](docs/operations/reliability.md) — PodDisruptionBudgets, pod anti-affinity, graceful shutdown
- [Networking & TLS](docs/operations/networking.md) — NetworkPolicies, cert-manager, flannel caveat
- [Supply-chain security](docs/operations/supply-chain.md) — CI Trivy scan, Syft SBOM, cosign keyless signing
- [SLOs & burn-rate alerts](docs/observability/slos.md) — `PrometheusRule` manifest with multi-window burn thresholds
- [Datastore HA migration](docs/infrastructure/datastore-ha.md) — CloudNativePG / RabbitMQ Operator / Redis Sentinel paths when graduating beyond single-replica

## Grafana Cloud Mode

Set `monitoring.mode: cloud` in [conf.yml](conf.yml) and the Helm chart's Alloy agents ship every signal to Grafana Cloud Tempo / Mimir / Loki. In this mode the in-cluster Jaeger / Prometheus / Loki / Grafana are not deployed — the cloud backends are the only sink.

Credentials live in Azure Key Vault. The fetch script pulls them, writes them into [conf.yml](conf.yml) in place (preserving comments), and then `./deploy-local.sh` materialises them into a Kubernetes Secret that the chart's destinations reference by name.

### Credentials

Credentials are stored in **Azure Key Vault** (`mf-cc-dt-azrsrp-prd-kv`) under the `grafana-mccaindev-*` secret prefix. The fetch script writes them into [conf.yml](conf.yml) in place (preserving comments) at `monitoring.grafana_cloud.*`; `deploy-local.sh` then materialises them into the `grafana-cloud-secrets` Kubernetes Secret.

| AKV secret name                                  | conf.yml key                              | Notes                                                                        |
| ------------------------------------------------ | ----------------------------------------- | ---------------------------------------------------------------------------- |
| `grafana-mccaindev-alloy-writer-mccaindev-token` | `monitoring.grafana_cloud.api_key`        | `glc_` access-policy token — scopes: `metrics:write logs:write traces:write` |
| `grafana-mccaindev-cloud-tempo-endpoint`         | `monitoring.grafana_cloud.tempo.endpoint` | host only → `:443` suffix added by fetch script                              |
| `grafana-mccaindev-cloud-tempo-username`         | `monitoring.grafana_cloud.tempo.user`     | Tempo instance ID                                                            |
| `grafana-mccaindev-cloud-mimir-endpoint`         | `monitoring.grafana_cloud.mimir.endpoint` | base URL → `/push` suffix added if missing (Prometheus remote_write)         |
| `grafana-mccaindev-cloud-mimir-username`         | `monitoring.grafana_cloud.mimir.user`     | Mimir instance ID                                                            |
| `grafana-mccaindev-cloud-loki-endpoint`          | `monitoring.grafana_cloud.loki.endpoint`  | base URL → `/loki/api/v1/push` suffix added if missing                       |
| `grafana-mccaindev-cloud-loki-username`          | `monitoring.grafana_cloud.loki.user`      | Loki instance ID                                                             |
| `grafana-mccaindev-faro-api-endpoint`            | `monitoring.grafana_cloud.faro.endpoint`  | frontend Faro collector (runtime env)                                        |
| `grafana-mccaindev-faro-sourcemap-token`         | `monitoring.grafana_cloud.faro.api_key`   | webpack source-map upload (build arg)                                        |

### Setup

```bash
# Azure auth: either an existing `az login` session, or export ARM_CLIENT_ID +
# ARM_CLIENT_SECRET in your shell (no .env loading).

# AKV coordinates live in conf.yml → monitoring.grafana_cloud.akv.{tenant_id,
#   subscription_id, resource_group, vault_name}. Edit these if they change.

# Pull every Grafana Cloud secret and update conf.yml in place:
./scripts/fetch-grafana-cloud-conf-from-akv.sh             # writes conf.yml + conf.yml.bak
./scripts/fetch-grafana-cloud-conf-from-akv.sh --dry-run   # preview diff only

# Re-apply (no cluster rebuild):
./deploy-local.sh --skip-cluster --skip-build
```

See [docs/deployment/grafana-cloud.md](docs/deployment/grafana-cloud.md) for the full credential model and rotation procedure.

## Make Targets Reference

The Makefile predates `./deploy-local.sh` and lives alongside it. Targets below still work, but the flow is **not** kept in sync with the `conf.yml` refactor — specifically, `make secrets-fetch-akv` writes a stale Mimir endpoint. Prefer the equivalent `./deploy-local.sh` / `./scripts/*` commands in the "equivalent" column when available.

### Cluster lifecycle

| Target              | Description                                    | Equivalent                                                          |
| ------------------- | ---------------------------------------------- | ------------------------------------------------------------------- |
| `make cluster-up`   | Create k3d cluster with port mappings          | `./deploy-local.sh` (builds + deploys too)                          |
| `make cluster-down` | Delete k3d cluster                             | `./deploy-local.sh --teardown`                                      |
| `make build`        | Build all 4 Docker images locally              | implicit in `./deploy-local.sh`                                     |
| `make import`       | Build + import images into k3d                 | implicit                                                            |
| `make deploy`       | Apply all k8s manifests                        | `./deploy-local.sh --skip-cluster --skip-build`                     |
| `make teardown`     | Delete the `otel-lab` namespace                | (use `./deploy-local.sh --teardown` to drop the whole cluster)      |
| `make full`         | `cluster-up` + `import` + `deploy` in one step | `./deploy-local.sh`                                                 |
| `make full-helm`    | `full` + `deploy-helm` in one step             | `./deploy-local.sh` (Helm install is unconditional in `cloud` mode) |

### Testing & ops

| Target                                            | Description                                                                             |
| ------------------------------------------------- | --------------------------------------------------------------------------------------- |
| `make test`                                       | Run k6 load test Job (generates realistic traffic)                                      |
| `make validate`                                   | Smoke-test all endpoints with curl                                                      |
| `make logs`                                       | Stream logs from all app pods                                                           |
| _(script)_ `./scripts/debug.sh`                   | Mode-aware triage — pod state, Alloy exporter counters, remote-write reachability probe |
| _(script)_ `./scripts/smoke-test-conf-updater.sh` | Offline regression test for the conf.yml in-place updater                               |

### Grafana Cloud credentials (Azure Key Vault)

> ⚠️ **`make secrets-fetch-akv` is out of sync with the current cloud destination.** It writes `GRAFANA_CLOUD_MIMIR_ENDPOINT=.../api/v1/otlp` into the Secret, but the chart's cloud destination uses Prometheus remote_write and expects `.../api/prom/push`. Running it will break cloud-mode metrics. Use the script-based flow instead.

| Path                                                 | Description                                                                                                                                                                                |
| ---------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| **`./scripts/fetch-grafana-cloud-conf-from-akv.sh`** | **Preferred.** Pulls AKV secrets, writes them into `conf.yml` in place (preserving comments), supports `--dry-run`. Auth via existing `az login` or shell-exported `ARM_CLIENT_ID/SECRET`. |
| `make secrets-fetch-akv`                             | Legacy — writes K8s Secret directly with stale Mimir URL.                                                                                                                                  |
| `make secrets-apply`                                 | Legacy — applies credentials from `.env`.                                                                                                                                                  |
| `make secrets-show`                                  | Print stored Secret values (API key redacted). Still accurate.                                                                                                                             |

### Helm monitoring

| Target                   | Description                                                                                                                                      |
| ------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------ |
| `make helm-repo`         | Add and update `grafana` Helm repo                                                                                                               |
| `make helm-render`       | Render [config.yaml.j2](k8s/monitoring/grafana-helm/config.yaml.j2) → `k8s/monitoring/grafana-helm/generated/` via Jinja2 (multi-cluster render) |
| `make deploy-helm`       | Install/upgrade `grafana/k8s-monitoring` with local values                                                                                       |
| `make deploy-helm-cloud` | Install using cloud-rendered values (after `helm-render`)                                                                                        |
| `make teardown-helm`     | Uninstall Helm release, delete `monitoring` namespace                                                                                            |

Note: `./deploy-local.sh` handles the Helm install inline and renders [values-cloud.yaml.tmpl](k8s/monitoring/grafana-helm/values-cloud.yaml.tmpl) directly from `conf.yml`. No separate `helm-render` step is needed when using the script.

## Observability UIs

**Always available** (both modes):

| UI          | URL                                                                                                     | Notes                                              |
| ----------- | ------------------------------------------------------------------------------------------------------- | -------------------------------------------------- |
| Angular SPA | `http://localhost:8080`                                                                                 | Frontend entry point                               |
| RabbitMQ    | `http://localhost:15672`                                                                                | guest / guest — inspect queues and message headers |
| Alloy UI    | `kubectl -n monitoring port-forward svc/grafana-k8s-alloy-receiver 12345` then `http://localhost:12345` | Pipeline graph, component status, debug traces     |

**Only in `monitoring.mode: local`:**

| UI         | URL                      | Notes                       |
| ---------- | ------------------------ | --------------------------- |
| Grafana    | `http://localhost:3000`  | admin / admin               |
| Jaeger     | `http://localhost:16686` | Trace search and waterfall  |
| Prometheus | `http://localhost:9090`  | Metric explorer + exemplars |

**Only in `monitoring.mode: cloud`:** your Grafana Cloud stack's Explore + dashboards (e.g. `https://mccaindev.grafana.net/explore`).

## Services

| Service          | Stack              | Port (cluster)              | DB         | Role                            |
| ---------------- | ------------------ | --------------------------- | ---------- | ------------------------------- |
| otel-frontend    | Angular 17 + nginx | 80 (host: 8080 via ingress) | —          | SPA + Faro RUM                  |
| gateway-api      | .NET 8 Minimal API | 5000                        | MySQL      | BFF, gRPC client                |
| order-api        | .NET 8 gRPC        | 5001                        | PostgreSQL | Order CRUD + RabbitMQ publisher |
| notification-svc | Python/FastAPI     | 8000                        | Redis      | RabbitMQ consumer + REST        |

## OTel Validation Checklist

See [docs/spec.md](docs/spec.md) Section 11 for the complete checklist covering:

- Trace propagation (HTTP, gRPC, RabbitMQ async)
- Span metrics (RED) + exemplars
- K8s attribute enrichment
- Log-to-trace correlation
- Tail sampling rates
- Frontend RUM (Faro)
- Resilience / negative scenarios
