# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## The one load-bearing knob

Every meaningful behavioural switch flows from **`monitoring.mode`** in [conf.yml](conf.yml):

- `cloud` (default) — the `grafana/k8s-monitoring` Helm chart's Alloy agents ship traces/metrics/logs to Grafana Cloud Tempo/Mimir/Loki. **No in-cluster Jaeger/Prometheus/Loki/Grafana are deployed.**
- `local` — a bespoke Alloy DaemonSet in [k8s/monitoring/grafana/](k8s/monitoring/grafana/) exports to in-cluster backends under [k8s/monitoring/local/](k8s/monitoring/local/). The Helm chart is still installed (apps target `grafana-k8s-alloy-receiver` regardless); only its destinations change.

The modes are mutually exclusive. There is **no dual-export** — any doc or code comment that says otherwise is stale. `http://localhost:3000` / `:16686` / `:9090` only exist in local mode.

## Two parallel deployment tools — don't mix them

| Tool                               | Source of truth              | Credentials flow                                                                                                                                                                                                                                                        |
| ---------------------------------- | ----------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| **`./deploy-local.sh`** (primary)  | [conf.yml](conf.yml)          | `monitoring.grafana_cloud.use_env` picks the source: `true` → sources `.env` (repo root); `false` → [scripts/fetch-grafana-cloud-conf-from-akv.sh](scripts/fetch-grafana-cloud-conf-from-akv.sh) updates conf.yml in place, which deploy-local.sh then reads directly |
| `Makefile` (legacy)                | `.env` + hand-edited values   | `make secrets-fetch-akv` → writes Secret directly                                                                                                                                                                                                                      |

**`make secrets-fetch-akv` is a live footgun.** It writes `GRAFANA_CLOUD_MIMIR_ENDPOINT=.../api/v1/otlp` into the Secret. The current cloud destination ([values-cloud.yaml.tmpl](k8s/monitoring/grafana-helm/values-cloud.yaml.tmpl)) uses Prometheus remote_write and expects `.../api/prom/push`. Running the make target after the script-based refactor will silently break cloud-mode metrics. Prefer the script.

Both tools still work for the base flow. The README's §"Two deployment tools" table is the canonical explanation.

## conf.yml is the single source of truth

[`deploy-local.sh`](deploy-local.sh) reads [`conf.yml`](conf.yml) for every knob:

- `cluster.{name, namespace, ports}` — k3d cluster create + end-of-deploy banner (filtered by `ports[].mode`)
- `images.builds[]` — Docker builds, supports `build_args_from_env` (shell wins) and `build_args_from_conf` (conf.yml fallback, e.g. `FARO_API_KEY`)
- `manifests.{infra, datastores, app, post}` and `monitoring.manifests.{local, cloud}` — apply stages
- `monitoring.grafana_cloud.*` — materialised into the Secret named by `monitoring.secret_name`; URLs also substituted into `values-cloud.yaml.tmpl`
- `monitoring.grafana_cloud.akv.*` — consumed only by the AKV fetch script
- `monitoring.helm.values_file_by_mode.{local, cloud}` — mode-selected values file; `.tmpl` suffix triggers render
- `monitoring.deployment_environment` — stamped on every signal (Helm `extraLabels`, app env via ConfigMap)
- `security.tls.{enabled, hostname, cert_manager}` — gates cert-manager install
- `observability.slo_rules.{enabled, manifest}` — gates PrometheusRule apply

Changing a URL / user / key in `monitoring.grafana_cloud` and re-running `./deploy-local.sh --skip-cluster --skip-build` is sufficient to propagate — no other files need editing.

## deploy-local.sh safety checks (fail fast)

1. **Context guard** — `assert_k3d_context` refuses to proceed unless `kubectl config current-context == k3d-${cluster.name}` AND the cluster exists in `k3d cluster list`. Exists because `az login` can silently switch the active context; running `--skip-cluster` against AKS has happened.
2. **NodePort drift** — `check_nodeport_drift` parses every Service manifest under `k8s/` and refuses to deploy unless every `nodePort:` value is declared in `cluster.ports[].target`.
3. **Secret-key contract** — `validate_secret_keys` runs before `helm upgrade`: reads every `usernameKey`/`passwordKey`/`tokenKey` from the rendered values file, asserts each exists in the Secret. Rename on either side is caught before agents return 401s.

If any of these trip, investigate rather than bypass. The guards exist because of real incidents.

## Kustomize layout — files stay in place

Every subdirectory referenced by `deploy-local.sh`'s apply stages has its own `kustomization.yaml` (tiny — lists local resources only). `apply_stage` then uses `kubectl apply -k <dir>` when a `kustomization.yaml` is present, `-f <dir>` otherwise.

- [k8s/base/kustomization.yaml](k8s/base/kustomization.yaml) — aggregates every component via `../infra`, `../app/*`, `../datastores/*`. ArgoCD / Flux entrypoint.
- [k8s/overlays/{dev,staging,prod}/](k8s/overlays/) — reference `../../base` and patch replica counts, ingress hostnames, anti-affinity (soft→required in prod).

**The source files did not move** when Kustomize was added. They're still in their original directories. Don't try to refactor them into `base/`; the sub-kustomization pattern exists specifically to avoid that churn.

## Non-root UIDs must match the image

| Image                                 | UID  | Notes                                                                                                                                                                                                                                                                                                                                                                                                                                                         |
| ------------------------------------- | ---- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `mcr.microsoft.com/dotnet/aspnet:8.0` | 1654 | Built-in `app` user (since .NET 8 GA). **Don't `groupadd`/`useradd` your own** — it collides. Use `USER 1654:1654` + `COPY --chown=1654:1654`.                                                                                                                                                                                                                                                                                                                |
| `python:3.12-slim`                    | 1000 | Create the user explicitly (debian base has no `app`).                                                                                                                                                                                                                                                                                                                                                                                                        |
| `nginxinc/nginx-unprivileged:alpine`  | 101  | Binds 8080, not 80 — Service `targetPort: 8080`, LB host port 8080 maps to this.                                                                                                                                                                                                                                                                                                                                                                              |
| `mysql/postgres/redis/rabbitmq`       | 999  | Image default. `fsGroup: 999` on the pod; rabbitmq additionally needs `fsGroupChangePolicy: OnRootMismatch` (otherwise fsGroup widens `.erlang.cookie` and Erlang auth refuses to start). Postgres needs `PGDATA: /var/lib/postgresql/data/pgdata` — on k3d's local-path provisioner the PVC mount root is owned by root on the host, so non-root `initdb` can't chmod it; placing the cluster in a subdirectory postgres creates + owns sidesteps the issue. |

The K8s `securityContext.runAsUser` **must** match the Dockerfile's `USER`. `runAsNonRoot: true` refuses mismatches at schedule time.

Frontend is the documented exception to `readOnlyRootFilesystem: true` — see [docs/infrastructure/hardening.md](docs/infrastructure/hardening.md) for why.

## Commands

### Deploy / iterate

```bash
./deploy-local.sh                           # full: cluster + builds + apply + helm install (5-15 min cold)
./deploy-local.sh --skip-cluster --skip-build   # manifests-only (<1 min)
./deploy-local.sh --with-helm               # local mode: also install the Helm chart (ignored in cloud mode)
./deploy-local.sh --teardown                # delete the k3d cluster
```

### Triage

```bash
./scripts/debug.sh                          # mode-aware — conf.yml values, pod state, Alloy exporter counters,
                                            # remote-write reachability probe, alloy-receiver endpoint check
./scripts/smoke-test-conf-updater.sh        # offline regression test for the conf.yml in-place updater
```

### Credentials

```bash
./scripts/fetch-grafana-cloud-conf-from-akv.sh --dry-run   # preview diff against AKV
./scripts/fetch-grafana-cloud-conf-from-akv.sh             # apply → writes conf.yml + conf.yml.bak
./scripts/fetch-grafana-cloud-conf-from-akv.sh --print     # legacy: emit YAML block for manual paste
```

Auth: either `az login` first, or export `ARM_CLIENT_ID` + `ARM_CLIENT_SECRET` in the shell (no `.env` loading).

### Tests / CI

Local:

```bash
dotnet test src/order-api.Tests/order-api.Tests.csproj --configuration Release
dotnet test src/gateway-api.Tests/gateway-api.Tests.csproj --configuration Release
python -m pytest src/notification-svc/tests/ -v --tb=short
# Frontend: npm ci in src/frontend, then jest via /tmp/ng-test-deps hack — see .github/workflows/ci.yml
```

CI ([.github/workflows/ci.yml](.github/workflows/ci.yml)) runs all four test stacks + `pip-audit` + `dotnet list package --vulnerable` + Trivy scan (HIGH/CRITICAL, fixed-only) + Syft SBOM (CycloneDX) + cosign keyless sign on `main` push.

### Kustomize

```bash
kubectl kustomize k8s/base                  # render full stack
kubectl kustomize k8s/overlays/prod         # render prod (replicas=6, required anti-affinity, prod host)
kubectl apply -k k8s/overlays/dev           # apply dev overlay
```

### Endpoints (after deploy)

- `http://localhost:8080` — Frontend + `/api/*` → gateway-api (always)
- `http://localhost:15672` — RabbitMQ Management, guest/guest (always)
- `http://localhost:3000 | :16686 | :9090` — Grafana / Jaeger / Prometheus (local mode only)
- `https://signal-forge.local:8443` — TLS frontend (requires `security.tls.enabled` + `/etc/hosts`)
- Helm chart's alloy-receiver UI: `kubectl -n monitoring port-forward svc/grafana-k8s-alloy-receiver 12345` → `http://localhost:12345`

## Environmental gotchas

- **Zscaler CA.** [deploy-local.sh](deploy-local.sh) stages `zcert.crt` into each Docker build context and injects it into the k3d server node's trust store. Empty placeholder is always staged so Dockerfiles' `COPY zcert.crt` doesn't break on non-corporate machines. After cert injection, the k3d nginx LB caches the old server IP — deploy-local.sh calls `nginx -s reload` automatically.
- **`.env` is tracked, treated public.** Per the monorepo's convention, `.env` is committed as "learning-lab scaffolding" with placeholder values. **Rotate anything real before committing.** `.gitignore` has negations for this pattern.
- **Rendered Helm values directories are tracked.** `k8s/monitoring/grafana-helm/generated/` is a committed snapshot, not derived at build time.
- **Vendored chart.** `grafana/k8s-monitoring` v3.8.4 is pulled at deploy time — no local vendored copy in this directory.

## Docs map

See [docs/README.md](docs/README.md) for the index. The most load-bearing pages:

- [docs/infrastructure/hardening.md](docs/infrastructure/hardening.md) — per-image UID table, Dockerfile conventions, frontend readOnly exception
- [docs/infrastructure/kustomize.md](docs/infrastructure/kustomize.md) — base + overlays layout, how deploy-local.sh consumes it
- [docs/infrastructure/datastore-ha.md](docs/infrastructure/datastore-ha.md) — operator migration (CNPG / MySQL Operator / RabbitMQ Operator / Redis Sentinel) when graduating beyond single-replica
- [docs/operations/networking.md](docs/operations/networking.md) — NetworkPolicy model + flannel caveat + cert-manager flow
- [docs/operations/supply-chain.md](docs/operations/supply-chain.md) — CI scan/SBOM/sign
- [docs/observability/slos.md](docs/observability/slos.md) — SLI recording rules + multi-window burn alerts
- [docs/deployment/grafana-cloud.md](docs/deployment/grafana-cloud.md) — full AKV → conf.yml → Secret credential model
