# Usage

Command reference for installing, updating, and uninstalling the SignalForge lab, plus a full CLI
reference for every script and Make target in the repo.

**Prerequisites:** Docker 24+, k3d v5+, kubectl v1.28+, helm v3.14+, Python 3.9+. Azure CLI 2.50+ is
only needed if you use the optional AKV credential-fetch script — not required for a normal
install/update as long as `.env` is already populated.

---

## Install (first-time deploy)

```bash
./deploy-local.sh
```

Creates the k3d cluster (`otel-lab`), builds all 4 Docker images, applies manifests in dependency
order (`infra/` → `datastores/` → `monitoring/` → `app/` → `post/`), and installs the Helm
monitoring chart. Cold run: 5–15 min.

In `monitoring.mode: cloud` (the default), Grafana Cloud credentials come from the `.env` file named
by `monitoring.grafana_cloud.use_env` in [conf.yml](conf.yml) — `deploy-local.sh` sources it
directly, no extra step needed as long as `.env` is already populated. Fetching from Azure Key Vault
is a separate, optional, manual path for when you need to (re)populate `.env` — see
[Rotate Grafana Cloud credentials](#rotate-grafana-cloud-credentials) below. It is not part of the
install flow.

Verify the install:

```bash
./scripts/debug.sh      # mode-aware triage: pod state, Alloy exporter counters, remote-write probe
make validate            # curl-based smoke test of all endpoints
```

---

## Update

Pick the command that matches what changed — no need to rebuild or recreate anything you didn't
touch.

| What changed                                      | Command                                                     |
| ------------------------------------------------- | ----------------------------------------------------------- |
| App code (`src/*`)                                | `./deploy-local.sh` (rebuilds images + reapplies manifests) |
| K8s manifests / `conf.yml` only, images unchanged | `./deploy-local.sh --skip-build`                            |
| Nothing but need to reassert current state        | `./deploy-local.sh --skip-cluster --skip-build` (<1 min)    |
| Grafana Cloud credentials                         | see below                                                   |
| Local-mode Helm monitoring chart                  | `./deploy-local.sh --with-helm`                             |
| Using a non-default config file                   | `./deploy-local.sh -c <path-to-conf.yml>`                   |

`deploy-local.sh` is idempotent — re-running any variant is safe and is also the rollback path
(reapply the last-known-good `conf.yml` / manifests).

### Rotate Grafana Cloud credentials

Optional and manual — only needed if `.env` is missing or its Grafana Cloud values are stale.
Everyday installs/updates don't need this; `deploy-local.sh` already reads `.env` directly.

```bash
./scripts/fetch-grafana-cloud-conf-from-akv.sh --dry-run   # preview diff against AKV
./scripts/fetch-grafana-cloud-conf-from-akv.sh             # pull + write into .env in place (+ .env.bak)
./deploy-local.sh --skip-cluster --skip-build              # re-apply, no rebuild needed
```

Auth: existing `az login` session, or export `ARM_CLIENT_ID` + `ARM_CLIENT_SECRET` in the shell (no
`.env` loading for this script itself — it writes `.env`, it doesn't read from it for auth).

### Push updated SLO rules (cloud mode)

```bash
./scripts/push-slo-rules-to-mimir.sh --dry-run   # mimirtool rules diff, read-only
./scripts/push-slo-rules-to-mimir.sh             # load k8s/monitoring/slo-rules.yaml into the Ruler API
```

mode=local needs no manual step — `deploy-local.sh` loads the same rules file automatically.

---

## Uninstall

```bash
./deploy-local.sh --teardown       # deletes the entire k3d cluster (all namespaces, all state)
```

For a lighter-weight reset that keeps the cluster and datastores but drops the app namespace:

```bash
make teardown                      # deletes only the otel-lab namespace
```

There is no in-place "uninstall just the monitoring chart" path — re-run
`./deploy-local.sh --skip-cluster --skip-build` after flipping `monitoring.mode` or editing
`conf.yml` to change what gets installed on the next apply.

---

## CLI Reference

### `deploy-local.sh`

The sole deploy path. Reads every knob from [conf.yml](conf.yml).

| Flag                  | Effect                                                                                                |
| --------------------- | ----------------------------------------------------------------------------------------------------- |
| _(none)_              | Full deploy: cluster + builds + manifests + Helm                                                      |
| `--skip-cluster`      | Reuse the current k3d cluster / kube context (guarded — refuses to run against the wrong context)     |
| `--skip-build`        | Reuse images already loaded into the cluster                                                          |
| `--with-helm`         | Local mode only: also install `grafana/k8s-monitoring`. No-op in cloud mode (already mandatory there) |
| `--teardown`          | Delete the k3d cluster and exit                                                                       |
| `-c, --config <path>` | Use a config file other than `./conf.yml`                                                             |
| `-h, --help`          | Print usage                                                                                           |

### `scripts/`

| Script                                                                   | Purpose                                                                                                                                                                                                                                                                                                 |
| ------------------------------------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `debug.sh`                                                               | Mode-aware triage — pod state, Alloy exporter counters, remote-write reachability probe, alloy-receiver endpoint check                                                                                                                                                                                  |
| `fetch-grafana-cloud-conf-from-akv.sh [--dry-run\|--print\|--no-backup]` | Optional/manual. Pulls Grafana Cloud credentials from Azure Key Vault, writes them into the `.env` file named by `conf.yml`'s `monitoring.grafana_cloud.use_env` (preserving comments, `.bak` backup by default). `--dry-run` previews the diff; `--print` emits a YAML block for manual paste (legacy) |
| `push-slo-rules-to-mimir.sh [--dry-run]`                                 | Loads `k8s/monitoring/slo-rules.yaml` into Grafana Cloud Mimir's Ruler API via `mimirtool`. Cloud mode only                                                                                                                                                                                             |
| `smoke-test-conf-updater.sh`                                             | Offline regression test for the `.env` in-place updater used by the AKV fetch script — no cluster required                                                                                                                                                                                              |

### `Makefile` (legacy / secondary — prefer `deploy-local.sh` for deploy)

| Target                   | Description                                                                                 |
| ------------------------ | ------------------------------------------------------------------------------------------- |
| `make cluster-up`        | Create the k3d cluster with port mappings                                                   |
| `make cluster-down`      | Delete the k3d cluster                                                                      |
| `make build`             | Build all 4 Docker images locally                                                           |
| `make import`            | Build + import images into k3d                                                              |
| `make teardown`          | Delete the `otel-lab` namespace (cluster stays up)                                          |
| `make test`              | Run the k6 load test Job                                                                    |
| `make validate`          | Smoke-test all endpoints with curl                                                          |
| `make logs`              | Stream logs from all app pods                                                               |
| `make test-unit`         | Run all unit test suites (.NET + Python + frontend)                                         |
| `make secrets-fetch-akv` | Legacy — writes the Grafana Cloud K8s Secret directly from AKV, bypassing `deploy-local.sh` |
| `make secrets-apply`     | Legacy — applies credentials from `.env`                                                    |
| `make secrets-show`      | Print stored Secret values (API key redacted)                                               |

`make deploy` / `deploy-cloud` / `deploy-local` / `full` are retired — they print a redirect to
`./deploy-local.sh` and exit non-zero.

### Kustomize

```bash
kubectl kustomize k8s/base                  # render full stack
kubectl kustomize k8s/overlays/prod         # render prod overlay (replicas=6, required anti-affinity)
kubectl apply -k k8s/overlays/dev           # apply dev overlay
```

---

## Health check / triage

```bash
./scripts/debug.sh                # mode-aware: pod state, Alloy exporter counters, remote-write probe
make validate                     # curl-based smoke test of all endpoints
```

## Endpoints

| URL                               | Service                                                                                              | Notes                                                   |
| --------------------------------- | ---------------------------------------------------------------------------------------------------- | ------------------------------------------------------- |
| `http://localhost:8080`           | Angular SPA + gateway-api                                                                            | always available                                        |
| `https://signal-forge.local:8443` | Same, TLS                                                                                            | needs `security.tls.enabled: true` + `/etc/hosts` entry |
| `http://localhost:15672`          | RabbitMQ mgmt (guest/guest)                                                                          | always available                                        |
| `http://localhost:16686`          | Jaeger                                                                                               | local mode only                                         |
| `http://localhost:3000`           | Grafana (admin/admin)                                                                                | local mode only                                         |
| `http://localhost:9090`           | Prometheus                                                                                           | local mode only                                         |
| Alloy pipeline UI                 | `kubectl -n monitoring port-forward svc/grafana-k8s-alloy-receiver 12345` → `http://localhost:12345` | cloud mode                                              |
