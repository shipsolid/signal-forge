# SignalForge — OTel Microservices Validation Lab

> End-to-end OpenTelemetry instrumentation lab across .NET 8, Python/FastAPI, and Angular 17, deployed on k3d with Helm-managed Grafana Alloy agents as the collector stack.

**What it validates:** traces (5-hop cross-language), span metrics, exemplars, async trace propagation via RabbitMQ, frontend RUM with Faro, log-to-trace correlation via Loki, and tail-based sampling.

Work outside-in, purpose → design → implementation:

| Step | File                           | Why                                                                                  |
| ---- | ------------------------------ | ------------------------------------------------------------------------------------ |
| 1    | docs/spec.md                   | The "what" — all services, patterns to validate, the validation checklist at §11     |
| 2    | docs/architecture/overview.md  | Topology diagram, signal flow per type, port map                                     |
| 3    | docs/architecture/decisions.md | 10 ADRs that explain the non-obvious "why" (most important before touching anything) |
| 4    | conf.yml                       | The single control file — every knob deploy-local.sh reads                           |
| 5    | docs/observability/pipeline.md | Alloy River config stage-by-stage; the heart of the lab                              |
| 6    | src/order-api/                 | Richest service: gRPC, Outbox, RabbitMQ publish with W3C traceparent injection       |
| 7    | src/notification-svc/          | Python consumer, SpanLink, cross-language async propagation                          |
| 8    | src/gateway-api/               | .NET BFF, exemplars, UpDownCounter, fan-out pattern                                  |
| 9    | src/frontend/                  | Angular + Faro RUM — browser-to-backend trace propagation                            |
| 10   | k8s/                           | Manifests: infra/ → datastores/ → app/ → monitoring/                                 |

For ops understanding: deploy-local.sh → scripts/debug.sh → .github/workflows/ci.yml.

---

## Purpose

SignalForge exists to provide a portable, reproducible environment for validating OpenTelemetry instrumentation patterns across multiple runtimes and communication protocols. It is not a toy: it models production-grade concerns — tail-based sampling, async context propagation, exemplar plumbing, SLO recording rules, and supply-chain controls — in a self-contained k3d cluster that any engineer can spin up on a laptop.

The lab is consumed by engineers who need to test instrumentation changes before they land on production clusters, and by anyone building familiarity with the Grafana Alloy / k8s-monitoring Helm chart. It is a reference implementation, not a template — copy patterns from it, but do not fork it as application scaffolding.

It lives here rather than inside the main monorepo because it has its own `k3d` cluster lifecycle, separate image builds, and Grafana Cloud credentials that are scoped to a dev stack and should not bleed into production pipelines.

---

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

All services push OTLP to `alloy-receiver` (Helm-managed DaemonSet, `monitoring` namespace):

```text
App SDK ──OTLP gRPC :4317──► alloy-receiver
  → k8sattributes enrichment
  → transform (stamp deployment.environment)
  → filter (drop /healthz spans)
  ├── spanmetrics connector  → RED metrics (before sampling)
  └── tail_sampling          → errors=100%, slow>2s=100%, rest=25%
       └── batch → Tempo (cloud) | Jaeger (local)

alloy-logs (DaemonSet)  → pod stdout tailing → trace correlation → Loki
alloy-metrics (StatefulSet) → kubelet/cAdvisor/KSM → Mimir | Prometheus
```

The single most important configuration knob is `monitoring.mode` in [conf.yml](conf.yml):

| `monitoring.mode` | Alloy destinations                              | In-cluster backends                            |
| ----------------- | ----------------------------------------------- | ---------------------------------------------- |
| `cloud` (default) | Grafana Cloud Tempo / Mimir / Loki              | none                                           |
| `local`           | In-cluster Jaeger / Prometheus / Loki / Grafana | Jaeger :16686, Prometheus :9090, Grafana :3000 |

The two modes are mutually exclusive — there is no dual-export. Any doc saying otherwise is stale.

In **cloud mode**, the chart's Alloy agents are the entire pipeline — no in-cluster Jaeger / Prometheus / Loki / Grafana are deployed. In **local mode**, a parallel bespoke Alloy DaemonSet in [k8s/monitoring/grafana/](k8s/monitoring/grafana/) exports to in-cluster backends; the Helm chart is still installed and still serves as the app OTLP ingress, but its destinations point at the in-cluster services.

`alloy-logs` tails pod stdout/stderr with trace-id correlation. `alloy-metrics` scrapes cluster infra metrics. See [docs/observability/pipeline.md](docs/observability/pipeline.md) for the full signal flow and [docs/OTEL-PATTERNS.md](docs/OTEL-PATTERNS.md) for per-runtime instrumentation choices.

### Alloy roles (Helm release, `monitoring` namespace)

| Alloy role        | Kind        | Responsibility                     | Destination (cloud) | Destination (local)      |
| ----------------- | ----------- | ---------------------------------- | ------------------- | ------------------------ |
| `alloy-metrics`   | StatefulSet | Scrapes cluster infra metrics      | Grafana Cloud Mimir | in-cluster Prometheus    |
| `alloy-singleton` | Deployment  | Cluster events, kube-state-metrics | Cloud Mimir + Loki  | in-cluster Prom + Loki   |
| `alloy-logs`      | DaemonSet   | Pod + node log tailing             | Grafana Cloud Loki  | in-cluster Loki          |
| `alloy-receiver`  | DaemonSet   | OTLP push receiver (app telemetry) | Cloud Tempo + Mimir | in-cluster Jaeger + Prom |
| `alloy-profiles`  | DaemonSet   | Disabled — no Pyroscope            | —                   | —                        |

Values: [values-local.yaml](k8s/monitoring/grafana-helm/values-local.yaml) or [values-cloud.yaml.tmpl](k8s/monitoring/grafana-helm/values-cloud.yaml.tmpl) (rendered at deploy time from conf.yml).

### Trace propagation

A single "Create Order" click produces a 5-hop trace across three runtimes. The RabbitMQ hop uses a SpanLink (not parent-child) because message processing is async; both spans share the same `traceId` and appear as a dashed arrow in Jaeger.

```text
Browser (Faro)  →  gateway-api  →  order-api  →  RabbitMQ (SpanLink)  →  notification-svc
                                  ↓                                       ↓
                              PostgreSQL                               Redis
```

See [docs/architecture/overview.md](docs/architecture/overview.md) for the full signal flow diagrams.

---

## Ownership Boundary

| Dimension       | Detail                                                             |
| --------------- | ------------------------------------------------------------------ |
| Team            | Personal lab / portfolio (Amit Singh)                              |
| Primary owner   | Amit Singh — [amit.singh@mccain.com](mailto:amit.singh@mccain.com) |
| On-call         | None — lab environment, no production SLA                          |
| Escalation path | GitHub issues on this repo                                         |

This component does not own anything in shared infrastructure. It creates and manages its own k3d cluster (`otel-lab`) and its own Kubernetes namespace (`otel-lab`). The only external dependency with shared ownership is the Grafana Cloud stack (`mccaindev.grafana.net`) and the Azure Key Vault (`mf-cc-dt-azrsrp-prd-kv`) — those are McCain platform resources and are consumed read-only by this lab.

The lab does not own the Grafana Cloud instance, the AKV vault, or any network resources outside the k3d cluster. Changes to Grafana Cloud credentials are fetched from AKV; they are never committed as live values.

---

## Deployment Model

| Environment            | Method              | Trigger                       | Target                                |
| ---------------------- | ------------------- | ----------------------------- | ------------------------------------- |
| local (lab)            | `./deploy-local.sh` | manual                        | k3d cluster `otel-lab` on localhost   |
| CI (build + test only) | GitHub Actions      | push / PR / workflow_dispatch | no cluster — tests + image scans only |

There is no staging or production deployment of this lab. The k3d cluster is ephemeral and local.

```bash
# Full deploy: cluster + Docker builds + manifests + Helm (5-15 min cold)
./deploy-local.sh

# Subsequent iteration, manifests only (<1 min)
./deploy-local.sh --skip-cluster --skip-build

# Local mode: also install the Helm monitoring chart
./deploy-local.sh --with-helm

# Rollback: re-run the deploy — it is idempotent
./deploy-local.sh --skip-cluster --skip-build

# Teardown: delete the k3d cluster entirely
./deploy-local.sh --teardown
```

### Two deployment tools — do not mix them

| Tool                              | Config source               | Credentials                                                         | Use                                                                                               |
| --------------------------------- | --------------------------- | ------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------- |
| **`./deploy-local.sh`** (primary) | [conf.yml](conf.yml)        | `./scripts/fetch-grafana-cloud-conf-from-akv.sh` → updates conf.yml | Recommended for all new work                                                                      |
| `Makefile` (legacy)               | `.env` + hand-edited values | `make secrets-fetch-akv` → writes Secret directly                   | Reference only — `make secrets-fetch-akv` writes a stale Mimir URL that breaks cloud-mode metrics |

`make secrets-fetch-akv` is a live footgun: it writes `GRAFANA_CLOUD_MIMIR_ENDPOINT=.../api/v1/otlp` into the Secret, but the chart expects `.../api/prom/push`. Do not run it after switching to the script-based flow.

### Credentials (Grafana Cloud, cloud mode only)

```bash
# Preview credential diff against AKV
./scripts/fetch-grafana-cloud-conf-from-akv.sh --dry-run

# Pull and write into conf.yml (creates conf.yml.bak)
./scripts/fetch-grafana-cloud-conf-from-akv.sh

# Re-deploy with new credentials (no cluster rebuild)
./deploy-local.sh --skip-cluster --skip-build
```

Auth: `az login` first, or export `ARM_CLIENT_ID` + `ARM_CLIENT_SECRET` in the shell. See [docs/deployment/grafana-cloud.md](docs/deployment/grafana-cloud.md) for the full credential model and rotation procedure.

### Helm upgrade invocation (used by deploy-local.sh, cloud mode)

```bash
helm upgrade --install grafana-k8s grafana/k8s-monitoring \
  --version 3.8.4 \
  --namespace monitoring --create-namespace \
  --values k8s/monitoring/grafana-helm/values-cloud.yaml \
  --wait --timeout 5m
```

The values file is rendered from [values-cloud.yaml.tmpl](k8s/monitoring/grafana-helm/values-cloud.yaml.tmpl) using credentials from `conf.yml`. Do not edit the rendered file — edit the template or `conf.yml`.

### Kustomize overlays

```bash
kubectl kustomize k8s/base                  # render full stack
kubectl kustomize k8s/overlays/prod         # render prod overlay (replicas=6, required anti-affinity)
kubectl apply -k k8s/overlays/dev           # apply dev overlay
```

---

## Dependencies

| Dependency                                    | Type     | Required                          | Notes                                                                                                                                |
| --------------------------------------------- | -------- | --------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------ |
| Docker 24+                                    | tooling  | yes                               | Image builds + k3d node images                                                                                                       |
| k3d v5+                                       | tooling  | yes                               | Local Kubernetes cluster                                                                                                             |
| kubectl v1.28+                                | tooling  | yes                               | Manifest apply                                                                                                                       |
| helm v3.14+                                   | tooling  | yes                               | `grafana/k8s-monitoring` chart install                                                                                               |
| Python 3.9+                                   | tooling  | yes                               | `deploy-local.sh` uses Python to parse conf.yml, render templates, run scripts                                                       |
| Azure CLI 2.50+                               | tooling  | cloud mode only                   | `./scripts/fetch-grafana-cloud-conf-from-akv.sh` — not needed if credentials are already in conf.yml                                 |
| Grafana Cloud stack (`mccaindev.grafana.net`) | upstream | cloud mode only                   | Tempo, Mimir, Loki endpoints; credentials in AKV                                                                                     |
| Azure Key Vault (`mf-cc-dt-azrsrp-prd-kv`)    | upstream | cloud mode only                   | Stores Grafana Cloud API key + endpoint URLs                                                                                         |
| Zscaler CA (`zcert.crt`)                      | infra    | corporate networks only           | Staged into Docker builds; empty placeholder used on non-corporate machines — Dockerfiles' `COPY zcert.crt` will not fail without it |
| `grafana/k8s-monitoring` Helm chart v3.8.4    | infra    | cloud mode (auto-installed)       | Pulled at deploy time; no local vendored copy                                                                                        |
| cert-manager v1.18.2 (jetstack chart)         | infra    | when `security.tls.enabled: true` | Installs into `cert-manager` namespace; skip by setting `security.tls.enabled: false`                                                |

**Version pins that must not drift:**

- `grafana/k8s-monitoring` is pinned to `3.8.4` in `conf.yml`. Upgrading requires re-validating all Alloy role names and values schema — the chart has breaking changes between minor versions.
- `.NET 8.0` in Dockerfiles — do not bump to .NET 9 without re-testing the OTel SDK compatibility matrix.

---

## Operational Model

**Health check:**

```bash
# Mode-aware triage: pod state, Alloy exporter counters, remote-write probe
./scripts/debug.sh

# Verify all service endpoints are responding
make validate
```

**Logs:**

| Service                 | How to access                                                                                |
| ----------------------- | -------------------------------------------------------------------------------------------- |
| Any app pod             | `kubectl -n otel-lab logs deploy/<service-name>`                                             |
| Alloy receiver          | `kubectl -n monitoring logs daemonset/grafana-k8s-alloy-receiver`                            |
| Loki query (local mode) | `{namespace="otel-lab"}` in Grafana Explore or via Loki API on `:3100`                       |
| Loki query (cloud mode) | `{namespace="otel-lab", deployment_environment="signal-forge-dev"}` in Grafana Cloud Explore |

All services write structured JSON logs. Alloy's `alloy-logs` DaemonSet extracts `TraceId`/`SpanId` fields and attaches them as Loki structured metadata, enabling "Logs for this span" in Grafana.

**Metrics / dashboards:**

- Span-derived RED metrics: `traces_spanmetrics_calls_total{service_name}`, `traces_spanmetrics_duration_milliseconds_bucket{service_name}`
- Cluster infra: standard kubelet/cAdvisor/KSM metrics scraped by `alloy-metrics`
- Alloy pipeline UI: `kubectl -n monitoring port-forward svc/grafana-k8s-alloy-receiver 12345` → `http://localhost:12345`

**Alerts:**

SLO rules live in [k8s/monitoring/slo-rules.yaml](k8s/monitoring/slo-rules.yaml) (disabled by default — set `observability.slo_rules.enabled: true` in conf.yml and ensure the `prometheusrules.monitoring.coreos.com` CRD is present).

| Alert                              | Severity | Trigger                                           |
| ---------------------------------- | -------- | ------------------------------------------------- |
| `SignalForgeAvailabilityFastBurn`  | page     | error_ratio > 7.2% in 5m AND 30m windows          |
| `SignalForgeAvailabilitySlowBurn`  | ticket   | error_ratio > 3% in 30m AND 6h windows            |
| `SignalForgeGatewayLatencyHigh`    | ticket   | gateway-api p99 > 500ms for 10m                   |
| `SignalForgeDownstreamLatencyHigh` | ticket   | order-api or notification-svc p99 > 300ms for 10m |
| `AlloyReceiverDown`                | page     | `up == 0` for alloy-receiver for 5m               |
| `DatastoreDown`                    | page     | any datastore pod not Ready for 3m                |

**Runbook:** [docs/operations/runbooks.md](docs/operations/runbooks.md) — covers no-traces, missing metrics, async propagation failures, log correlation gaps, exemplar troubleshooting, Grafana Cloud export errors.

---

## Quick Start

```bash
# Full deploy: cluster + builds + apply + Helm
./deploy-local.sh

# Subsequent iteration (manifests only):
./deploy-local.sh --skip-cluster --skip-build

# Triage after a deploy:
./scripts/debug.sh

# Tear down:
./deploy-local.sh --teardown
```

First run: ~5-15 minutes (4 Docker builds + k3d create + cert-manager + Helm rollout). `--skip-cluster --skip-build` runs complete in <1 min.

### Prerequisites

| Tool      | Min version | Install                                                                            |
| --------- | ----------- | ---------------------------------------------------------------------------------- |
| Docker    | 24+         | [docs.docker.com](https://docs.docker.com/get-docker/)                             |
| k3d       | v5+         | `curl -s https://raw.githubusercontent.com/k3d-io/k3d/main/install.sh \| bash`     |
| kubectl   | v1.28+      | [kubernetes.io/docs](https://kubernetes.io/docs/tasks/tools/)                      |
| helm      | v3.14+      | `curl https://raw.githubusercontent.com/helm/helm/main/scripts/get-helm-3 \| bash` |
| Azure CLI | 2.50+       | needed only for Grafana Cloud credential fetch via AKV                             |
| Python 3  | 3.9+        | system package — drives deploy-local.sh                                            |

### Endpoints (after deploy)

**Always available:**

| URL                               | Service                   | Notes                                                      |
| --------------------------------- | ------------------------- | ---------------------------------------------------------- |
| `http://localhost:8080`           | Angular SPA + Gateway API | `/api/*` → gateway, `/` → frontend                         |
| `https://signal-forge.local:8443` | Same, TLS                 | Requires `security.tls.enabled: true` + `/etc/hosts` entry |
| `http://localhost:15672`          | RabbitMQ Management       | guest/guest                                                |

**Local mode only:**

| URL                      | Service    | Credentials |
| ------------------------ | ---------- | ----------- |
| `http://localhost:16686` | Jaeger UI  | —           |
| `http://localhost:3000`  | Grafana    | admin/admin |
| `http://localhost:9090`  | Prometheus | —           |

**Cloud mode only:** your Grafana Cloud stack (e.g. `https://mccaindev.grafana.net`) — Explore for Tempo/Mimir/Loki.

---

## Repository Layout

```text
signal-forge/
├── src/                        # Application source
│   ├── gateway-api/            # .NET 8 Minimal API — BFF, MySQL, gRPC client
│   ├── order-api/              # .NET 8 gRPC — PostgreSQL, RabbitMQ publisher
│   ├── notification-svc/       # Python FastAPI — RabbitMQ consumer, Redis
│   ├── frontend/               # Angular 17 SPA — Faro RUM, nginx
│   └── proto/                  # Shared gRPC protobuf definitions
│
├── k8s/
│   ├── base/                   # Kustomize base (ArgoCD / Flux entrypoint)
│   ├── overlays/{dev,staging,prod}/
│   ├── infra/                  # namespace, secrets, PDB, NetworkPolicies, ingress, cert-manager issuer
│   ├── app/                    # Application Deployments + Services
│   ├── datastores/             # MySQL, PostgreSQL, Redis, RabbitMQ
│   ├── monitoring/
│   │   ├── slo-rules.yaml      #   PrometheusRule (SLOs + burn-rate alerts)
│   │   ├── grafana/            #   Bespoke Alloy DaemonSet (local mode only)
│   │   ├── grafana-helm/       #   grafana/k8s-monitoring Helm values
│   │   └── local/              #   In-cluster backends: Jaeger, Prometheus, Loki, Grafana
│   └── loadtest/               # k6 load test Job
│
├── conf.yml                    # Single source of truth for all deploy-local.sh knobs
├── deploy-local.sh             # Idempotent local deploy script
├── scripts/                    # AKV fetch, debug, smoke tests
├── Makefile                    # Legacy lifecycle targets
└── docs/
    ├── spec.md                 # OTel validation test scenarios and checklist
    ├── OTEL-PATTERNS.md        # Instrumentation patterns per runtime
    ├── architecture/           # System topology, decisions
    ├── observability/          # SLOs, pipeline, sampling, exemplars, correlation
    ├── infrastructure/         # Hardening, Kustomize, datastores, HA paths
    └── operations/             # Networking, runbooks, supply chain, reliability
```

Deploy order within `k8s/`: `infra/` → `app-env ConfigMap` → `grafana-cloud-secrets` → cert-manager → `datastores/` → `monitoring/` → `app/` → post (ingress). Driven by `deploy-local.sh` with context-guard and NodePort drift-check.

---

## Services

| Service          | Stack              | Port (cluster)              | DB         | Role                            |
| ---------------- | ------------------ | --------------------------- | ---------- | ------------------------------- |
| otel-frontend    | Angular 17 + nginx | 80 (host: 8080 via ingress) | —          | SPA + Faro RUM                  |
| gateway-api      | .NET 8 Minimal API | 5000                        | MySQL      | BFF, gRPC client                |
| order-api        | .NET 8 gRPC        | 5001                        | PostgreSQL | Order CRUD + RabbitMQ publisher |
| notification-svc | Python/FastAPI     | 8000                        | Redis      | RabbitMQ consumer + REST        |

---

## Testing & CI

```bash
# .NET
dotnet test src/order-api.Tests/order-api.Tests.csproj --configuration Release
dotnet test src/gateway-api.Tests/gateway-api.Tests.csproj --configuration Release

# Python
python -m pytest src/notification-svc/tests/ -v --tb=short

# Frontend (requires npm ci in src/frontend first)
# See .github/workflows/ci.yml for the /tmp/ng-test-deps workaround
```

CI ([.github/workflows/ci.yml](.github/workflows/ci.yml)) runs on `workflow_dispatch` (push/PR triggers are commented out — path-scoped to this sub-directory for monorepo use):

- `.NET` + Python + Angular unit tests
- `pip-audit` + `dotnet list package --vulnerable` for known CVEs
- Trivy image scan (HIGH/CRITICAL, fixed-only) for all four images
- Syft SBOM generation (CycloneDX JSON)
- cosign keyless signing on `main` push (OIDC — no long-lived secrets)

---

## Grafana Cloud Mode

Set `monitoring.mode: cloud` in [conf.yml](conf.yml) and the Helm chart's Alloy agents ship every signal to Grafana Cloud Tempo / Mimir / Loki. The in-cluster Jaeger / Prometheus / Loki / Grafana are not deployed — the cloud backends are the only sink.

Credentials live in Azure Key Vault. The fetch script pulls them, writes them into [conf.yml](conf.yml) in place (preserving comments), and then `./deploy-local.sh` materialises them into the `grafana-cloud-secrets` Kubernetes Secret that the chart's destinations reference by name.

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

### Credentials

Credentials are stored in **Azure Key Vault** (`mf-cc-dt-azrsrp-prd-kv`) under the `grafana-mccaindev-*` secret prefix. The fetch script writes them into [conf.yml](conf.yml) at `monitoring.grafana_cloud.*`; `deploy-local.sh` materialises them into the `grafana-cloud-secrets` Kubernetes Secret.

| AKV secret name                                  | conf.yml key                              | Notes                                                                        |
| ------------------------------------------------ | ----------------------------------------- | ---------------------------------------------------------------------------- |
| `grafana-mccaindev-alloy-writer-mccaindev-token` | `monitoring.grafana_cloud.api_key`        | `glc_` access-policy token — scopes: `metrics:write logs:write traces:write` |
| `grafana-mccaindev-cloud-tempo-endpoint`         | `monitoring.grafana_cloud.tempo.endpoint` | host only — `:443` suffix added by fetch script                              |
| `grafana-mccaindev-cloud-tempo-username`         | `monitoring.grafana_cloud.tempo.user`     | Tempo instance ID                                                            |
| `grafana-mccaindev-cloud-mimir-endpoint`         | `monitoring.grafana_cloud.mimir.endpoint` | base URL → `/push` suffix added if missing                                   |
| `grafana-mccaindev-cloud-mimir-username`         | `monitoring.grafana_cloud.mimir.user`     | Mimir instance ID                                                            |
| `grafana-mccaindev-cloud-loki-endpoint`          | `monitoring.grafana_cloud.loki.endpoint`  | base URL → `/loki/api/v1/push` suffix added if missing                       |
| `grafana-mccaindev-cloud-loki-username`          | `monitoring.grafana_cloud.loki.user`      | Loki instance ID                                                             |
| `grafana-mccaindev-faro-api-endpoint`            | `monitoring.grafana_cloud.faro.endpoint`  | browser Faro collector (runtime env)                                         |
| `grafana-mccaindev-faro-sourcemap-token`         | `monitoring.grafana_cloud.faro.api_key`   | webpack source-map upload (build arg)                                        |

---

## Production Readiness Controls

For any step beyond local lab, the following controls are already implemented:

- [Container hardening](docs/infrastructure/hardening.md) — non-root UIDs per image, `readOnlyRootFilesystem`, securityContext
- [Kustomize layout](docs/infrastructure/kustomize.md) — base + overlays for dev/staging/prod
- [Reliability](docs/operations/reliability.md) — PodDisruptionBudgets, pod anti-affinity, graceful shutdown
- [Networking & TLS](docs/operations/networking.md) — NetworkPolicies, cert-manager, flannel caveat
- [Supply-chain security](docs/operations/supply-chain.md) — Trivy scan, Syft SBOM, cosign keyless signing
- [SLOs & burn-rate alerts](docs/observability/slos.md) — `PrometheusRule` with multi-window burn thresholds
- [Datastore HA migration](docs/infrastructure/datastore-ha.md) — CloudNativePG / RabbitMQ Operator / Redis Sentinel paths

---

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

---

## Make Targets Reference

The Makefile predates `./deploy-local.sh` and lives alongside it. Targets below still work, but the flow is **not** kept in sync with the `conf.yml` refactor. Prefer the equivalent `./deploy-local.sh` / `./scripts/*` commands where available.

### Cluster lifecycle

| Target              | Description                                    | Equivalent                                                          |
| ------------------- | ---------------------------------------------- | ------------------------------------------------------------------- |
| `make cluster-up`   | Create k3d cluster with port mappings          | `./deploy-local.sh` (builds + deploys too)                          |
| `make cluster-down` | Delete k3d cluster                             | `./deploy-local.sh --teardown`                                      |
| `make build`        | Build all 4 Docker images locally              | implicit in `./deploy-local.sh`                                     |
| `make import`       | Build + import images into k3d                 | implicit                                                            |
| `make deploy`       | Apply all k8s manifests                        | `./deploy-local.sh --skip-cluster --skip-build`                     |
| `make teardown`     | Delete the `otel-lab` namespace                | use `./deploy-local.sh --teardown` to drop the whole cluster        |
| `make full`         | `cluster-up` + `import` + `deploy` in one step | `./deploy-local.sh`                                                 |
| `make full-helm`    | `full` + `deploy-helm` in one step             | `./deploy-local.sh` (Helm install is unconditional in `cloud` mode) |

### Testing & ops

| Target                                 | Description                                                                             |
| -------------------------------------- | --------------------------------------------------------------------------------------- |
| `make test`                            | Run k6 load test Job (generates realistic traffic)                                      |
| `make validate`                        | Smoke-test all endpoints with curl                                                      |
| `make logs`                            | Stream logs from all app pods                                                           |
| `./scripts/debug.sh`                   | Mode-aware triage — pod state, Alloy exporter counters, remote-write reachability probe |
| `./scripts/smoke-test-conf-updater.sh` | Offline regression test for the conf.yml in-place updater                               |

### Grafana Cloud credentials (Azure Key Vault)

> **`make secrets-fetch-akv` is out of sync with the current cloud destination.** It writes `GRAFANA_CLOUD_MIMIR_ENDPOINT=.../api/v1/otlp` into the Secret, but the chart's cloud destination uses Prometheus remote_write and expects `.../api/prom/push`. Running it will break cloud-mode metrics. Use the script-based flow instead.

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

---

## OTel Validation Checklist

See [docs/spec.md](docs/spec.md) for the full test scenarios, including:

- Trace propagation (HTTP, gRPC, RabbitMQ async)
- Span metrics (RED) + exemplars
- K8s attribute enrichment via `k8sattributes` processor
- Log-to-trace correlation via Loki structured metadata
- Tail sampling rates (verify 25% sampling of normal traffic)
- Frontend RUM (Faro) session and page view spans
- Resilience / negative scenarios (datastore down, consumer crash)

---

## Roadmap

| Phase | Item                                                               | Status   | Target                                                      |
| ----- | ------------------------------------------------------------------ | -------- | ----------------------------------------------------------- |
| 1     | Core 4-service OTel stack with cloud + local mode                  | done     | —                                                           |
| 1     | Kustomize overlays (dev/staging/prod)                              | done     | —                                                           |
| 1     | CI pipeline: tests + Trivy + SBOM + cosign                         | done     | —                                                           |
| 1     | SLO recording rules + multi-window burn alerts                     | done     | —                                                           |
| 1     | Tail-based sampling + spanmetrics                                  | done     | —                                                           |
| 2     | Faro RUM source-map upload + error grouping                        | active   | TBD — depends on Faro source-map token in AKV               |
| 2     | SLO rules enabled by default (`slo_rules.enabled: true`)           | planned  | Requires Prometheus Operator CRD install in deploy-local.sh |
| 3     | k6 CronJob for continuous synthetic traffic                        | planned  | Required for client-perceived SLO validation                |
| 3     | Pyroscope continuous profiling (alloy-profiles enable)             | planned  | Blocked on in-cluster Pyroscope backend                     |
| 3     | Datastore HA operators (CNPG / RabbitMQ Operator / Redis Sentinel) | deferred | See docs/infrastructure/datastore-ha.md                     |
