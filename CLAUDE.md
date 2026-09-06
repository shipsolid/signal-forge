# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this
repository.

## The one load-bearing knob

Every meaningful behavioural switch flows from **`monitoring.mode`** in [conf.yml](conf.yml):

- `cloud` (default) — the `grafana/k8s-monitoring` Helm chart's Alloy agents ship
  traces/metrics/logs to Grafana Cloud Tempo/Mimir/Loki. **No in-cluster
  Jaeger/Prometheus/Loki/Grafana are deployed.**
- `local` — a bespoke Alloy DaemonSet in [k8s/monitoring/grafana/](k8s/monitoring/grafana/) exports
  to in-cluster backends under [k8s/monitoring/local/](k8s/monitoring/local/). Helm is optional in
  this mode and is installed only with `--with-helm`; cloud mode always uses the Helm receiver.

The modes are mutually exclusive. There is **no dual-export** — any doc or code comment that says
otherwise is stale. `http://localhost:3000` / `:16686` / `:9090` only exist in local mode.

## Two parallel deployment tools — don't mix them

| Tool                              | Source of truth             | Credentials flow                                                                                                                                                                                                                                                      |
| --------------------------------- | --------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **`./deploy-local.sh`** (primary) | [conf.yml](conf.yml)        | `monitoring.grafana_cloud.use_env` names a required env file (path relative to conf.yml's directory unless absolute) that deploy-local.sh sources directly; [scripts/fetch-grafana-cloud-conf-from-akv.sh](scripts/fetch-grafana-cloud-conf-from-akv.sh) updates that same file in place from AKV — there is no conf.yml-fields fallback |
| `Makefile` (legacy)               | `.env` + hand-edited values | `make secrets-fetch-akv` → writes Secret directly                                                                                                                                                                                                                     |

**`make secrets-fetch-akv` used to be a live footgun**, writing
`GRAFANA_CLOUD_MIMIR_ENDPOINT=.../api/v1/otlp` into the Secret while the cloud destination
([values-cloud.yaml.tmpl](k8s/monitoring/grafana-helm/values-cloud.yaml.tmpl)) speaks Prometheus
remote_write and expects `.../api/prom/push`. Fixed — the target now writes `/api/prom/push` too,
matching the script-based path. Still prefer the script
(`./scripts/fetch-grafana-cloud-conf-from-akv.sh`) as the primary flow; the Makefile target remains
a secondary/legacy path, not the canonical one.

Both tools still work for the base flow. The README's §"Two deployment tools" table is the canonical
explanation.

## conf.yml is the single source of truth

[`deploy-local.sh`](deploy-local.sh) reads [`conf.yml`](conf.yml) for every knob:

- `cluster.{name, namespace, ports}` — k3d cluster create + end-of-deploy banner (filtered by
  `ports[].mode`)
- `images.builds[]` — Docker builds, supports `build_args_from_env` (shell wins) and
  `build_args_from_conf` (conf.yml fallback, e.g. `FARO_API_KEY`)
- `manifests.{infra, datastores, app, post}` and `monitoring.manifests.{local, cloud}` — apply
  stages
- `monitoring.grafana_cloud.*` — materialised into the Secret named by `monitoring.secret_name`;
  URLs also substituted into `values-cloud.yaml.tmpl`
- `monitoring.grafana_cloud.akv.*` — consumed only by the AKV fetch script
- `monitoring.helm.values_file_by_mode.{local, cloud}` — mode-selected values file; `.tmpl` suffix
  triggers render
- `monitoring.deployment_environment` — stamped on every signal (Helm `extraLabels`, app env via
  ConfigMap)
- `security.tls.{enabled, hostname, cert_manager}` — gates cert-manager install
- `observability.slo_rules.{enabled, manifest}` — gates local SLO rule loading (and optional
  kube-prometheus-stack wrapping when that operator exists)

Changing a URL / user / key in `monitoring.grafana_cloud` and re-running
`./deploy-local.sh --skip-cluster --skip-build` is sufficient to propagate — no other files need
editing.

## deploy-local.sh safety checks (fail fast)

1. **Context guard** — `assert_k3d_context` refuses to proceed unless
   `kubectl config current-context == k3d-${cluster.name}` AND the cluster exists in
   `k3d cluster list`. Exists because `az login` can silently switch the active context; running
   `--skip-cluster` against AKS has happened.
2. **NodePort drift** — `check_nodeport_drift` parses every Service manifest under `k8s/` and
   refuses to deploy unless every `nodePort:` value is declared in `cluster.ports[].target`.
3. **Secret-key contract** — `validate_secret_keys` runs before `helm upgrade`: reads every
   `usernameKey`/`passwordKey`/`tokenKey` from the rendered values file, asserts each exists in the
   Secret. Rename on either side is caught before agents return 401s.

If any of these trip, investigate rather than bypass. The guards exist because of real incidents.

## Kustomize layout — files stay in place

Every subdirectory referenced by `deploy-local.sh`'s apply stages has its own `kustomization.yaml`
(tiny — lists local resources only). `apply_stage` then uses `kubectl apply -k <dir>` when a
`kustomization.yaml` is present, `-f <dir>` otherwise.

- [k8s/base/kustomization.yaml](k8s/base/kustomization.yaml) — aggregates every component via
  `../infra`, `../app/*`, `../datastores/*`. ArgoCD / Flux entrypoint.
- [k8s/overlays/{dev,staging,prod}/](k8s/overlays/) — reference `../../base` and patch replica
  counts, ingress hostnames, anti-affinity (soft→required in prod).

**The source files did not move** when Kustomize was added. They're still in their original
directories. Don't try to refactor them into `base/`; the sub-kustomization pattern exists
specifically to avoid that churn.

## Non-root UIDs must match the image

| Image                                 | UID  | Notes                                                                                                                                                                                                                                                                                                                                                                                                                                                         |
| ------------------------------------- | ---- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `mcr.microsoft.com/dotnet/aspnet:8.0` | 1654 | Built-in `app` user (since .NET 8 GA). **Don't `groupadd`/`useradd` your own** — it collides. Use `USER 1654:1654` + `COPY --chown=1654:1654`.                                                                                                                                                                                                                                                                                                                |
| `python:3.12-slim`                    | 1000 | Create the user explicitly (debian base has no `app`).                                                                                                                                                                                                                                                                                                                                                                                                        |
| `nginxinc/nginx-unprivileged:alpine`  | 101  | Binds 8080, not 80 — Service `targetPort: 8080`, LB host port 8080 maps to this.                                                                                                                                                                                                                                                                                                                                                                              |
| `mysql/postgres/redis/rabbitmq`       | 999  | Image default. `fsGroup: 999` on the pod; rabbitmq additionally needs `fsGroupChangePolicy: OnRootMismatch` (otherwise fsGroup widens `.erlang.cookie` and Erlang auth refuses to start). Postgres needs `PGDATA: /var/lib/postgresql/data/pgdata` — on k3d's local-path provisioner the PVC mount root is owned by root on the host, so non-root `initdb` can't chmod it; placing the cluster in a subdirectory postgres creates + owns sidesteps the issue. |

The K8s `securityContext.runAsUser` **must** match the Dockerfile's `USER`. `runAsNonRoot: true`
refuses mismatches at schedule time.

Frontend is the documented exception to `readOnlyRootFilesystem: true` — see
[Container hardening](https://shipsolid.github.io/signal-forge/infrastructure/hardening/) for why.

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

./scripts/push-slo-rules-to-mimir.sh --dry-run   # mimirtool rules diff — read-only, mode=cloud only
./scripts/push-slo-rules-to-mimir.sh             # mimirtool rules load — pushes k8s/monitoring/slo-rules.yaml
```

Auth: either `az login` first, or export `ARM_CLIENT_ID` + `ARM_CLIENT_SECRET` in the shell (no
`.env` loading).

`push-slo-rules-to-mimir.sh` is the mode=cloud counterpart to `observability.slo_rules` — see
[SLOs & burn-rate alerts](https://shipsolid.github.io/signal-forge/observability/slos/#where-the-alerts-are-evaluated).
mode=local needs no manual step; `deploy-local.sh` loads the same rules file automatically.

### Tests / CI

Local:

```bash
dotnet test src/order-api.Tests/order-api.Tests.csproj --configuration Release
dotnet test src/gateway-api.Tests/gateway-api.Tests.csproj --configuration Release
python -m pytest src/notification-svc/tests/ -v --tb=short
cd src/frontend && npm ci --legacy-peer-deps && npx jest --config jest.config.js
```

Note the Testcontainers dependency: `order-api.Tests`' `OutboxRelayWorkerTests` starts a real
`postgres:16.4` container, so `dotnet test src/order-api.Tests/...` requires a running Docker
daemon.

CI ([.github/workflows/ci.yml](.github/workflows/ci.yml)) runs Gitleaks,
repository policy checks, .NET/Python/frontend tests, protobuf-contract
validation, CodeQL, dependency analysis, Trivy IaC/container scans, and
observability-as-policy validation. Only a successful trusted `main` run then
builds each service image once, generates a CycloneDX SBOM, signs/attests the
registry digest with keyless Cosign, and publishes the immutable release
manifest consumed by CD.

### Pre-commit hooks

One-time local setup:

```bash
make hooks-install
# equivalent to: pip install pre-commit && pre-commit install
```

After that, hooks run automatically on every `git commit`. To run the same checks CI runs, on
demand, against the whole tree:

```bash
pre-commit run --all-files
```

Covers: gitleaks (secret scanning, reuses root `.gitleaks.toml`), `ruff format`/`ruff check` for
`src/notification-svc/`, `prettier --check` for `src/frontend/`, `dotnet format --verify-no-changes`
per .NET project, `yamllint` for `k8s/**/*.yaml` + `conf.yml`, a `kubectl kustomize` build check for
`k8s/base` and every `k8s/overlays/*`, and standard hygiene (trailing whitespace, EOF newline,
merge-conflict markers, large-file guard, YAML/JSON/TOML syntax). ESLint for the frontend and
Ruff's full lint rule catalogue are intentionally deferred — see `.pre-commit-config.yaml`'s header
comment for why.

CI runs `pre-commit run --all-files` in its own blocking job for matching pull
requests, pushes to `main`, and manual dispatch. Image publication remains
main-only after every blocking job succeeds.

### Kustomize

```bash
kubectl kustomize k8s/base                  # render full stack
kubectl kustomize k8s/overlays/prod         # render prod (replicas=6, required anti-affinity, prod host)
kubectl apply -k k8s/overlays/dev           # apply dev overlay
```

### Endpoints (after deploy)

- `http://localhost:8080` — Frontend + `/api/*` → gateway-api (always)
- `http://localhost:15672` — RabbitMQ Management, signalforge/guest (always)
- `http://localhost:3000 | :16686 | :9090` — Grafana / Jaeger / Prometheus (local mode only)
- `https://signal-forge.local:8443` — TLS frontend (requires `security.tls.enabled` + `/etc/hosts`)
- Helm chart's alloy-receiver UI:
  `kubectl -n monitoring port-forward svc/grafana-k8s-alloy-receiver 12345` →
  `http://localhost:12345`

## Environmental gotchas

- **Zscaler CA.** [deploy-local.sh](deploy-local.sh) passes `zcert.crt` to dependency-restore steps
  as an optional BuildKit secret and separately injects it into the k3d server node's trust store.
  CI and non-corporate builds omit the secret; the CA is never copied into a build context or image.
  After node cert injection, the k3d nginx LB caches the old server IP — deploy-local.sh calls
  `nginx -s reload` automatically.
- **`.env` is tracked, treated public.** Per the monorepo's convention, `.env` is committed as
  "learning-lab scaffolding" with placeholder values. **Rotate anything real before committing.**
  `.gitignore` has negations for this pattern.
- **Rendered Helm values directories are tracked.** `k8s/monitoring/grafana-helm/generated/` is a
  committed snapshot, not derived at build time.
- **Vendored chart.** The `grafana/k8s-monitoring` version is pinned in `conf.yml` and pulled at
  deploy time — there is no local vendored copy in this directory.

## Docs map

Full docs for this app live in **`docs/`** in this repo — that tree is the single source of truth.
It is published to GitHub Pages at <https://shipsolid.github.io/signal-forge/> by the shared
Starlight engine in **`shipsolid/docs-site`** (its reusable
`.github/workflows/build-deploy.yml@main`), invoked from
[`.github/workflows/docs.yml`](.github/workflows/docs.yml). Per-repo settings — title, sidebar,
cross-repo `[[wiki-link]]` resolution — live in [`docs-site.yaml`](docs-site.yaml). This repo holds
**no Astro code**; `docs/` is the only docs content tree. The engine renders `docs/**` plus the
top-level `README.md`; a `[[wiki-link]]` pass and a broken-link check run at build time.

- **Canonical source** — `docs/` (start at `docs/README.md` for the full index). Read pages
  directly with the `Read` tool regardless of your current working directory. The most load-bearing
  pages are:
  - `infrastructure/hardening.md` — per-image UID table, Dockerfile conventions, frontend readOnly
    exception
  - `infrastructure/kustomize.md` — base + overlays layout, how deploy-local.sh consumes it
  - `infrastructure/datastore-ha.md` — operator migration (CNPG / MySQL Operator / RabbitMQ
    Operator / Redis Sentinel) when graduating beyond single-replica
  - `operations/networking.md` — NetworkPolicy model + kube-router enforcement model + cert-manager flow
  - `operations/supply-chain.md` — CI scan/SBOM/sign
  - `observability/slos.md` — SLI recording rules + multi-window burn alerts
  - `deployment/grafana-cloud.md` — full AKV → conf.yml → Secret credential model
  - `architecture/adrs/` — 10 ADRs for every non-obvious design choice
