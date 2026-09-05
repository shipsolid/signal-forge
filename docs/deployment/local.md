---
title: "Local Deployment"
description: "Step-by-step guide to deploying the full lab locally on k3d via deploy-local.sh, including cluster setup and mode switching."
tags: ["ShipSolid", "Signal Forge", "Deployment"]
updated: 2026-07-10
zettelId: "202607091847-16"
relations:
  - slug: projects/app-signal-forge/deployment/grafana-cloud
    kind: related
  - slug: projects/app-signal-forge/deployment/helm
    kind: related
  - slug: observability/reference/jaeger
    kind: related
  - slug: observability/reference/prometheus
    kind: related
---

## Local Deployment

This guide sets up the full lab on a local machine using k3d. No cloud accounts required.
`./deploy-local.sh` is the sole deploy path — the Makefile only builds images, runs tests, and
fetches/applies Grafana Cloud credentials (see [[grafana-cloud|grafana-cloud.md]]).

## Prerequisites

| Tool     | Min version | Install                                                                            |
| -------- | ----------- | ---------------------------------------------------------------------------------- |
| Docker   | 24+         | [docs.docker.com](https://docs.docker.com/get-docker/)                             |
| k3d      | v5+         | `curl -s https://raw.githubusercontent.com/k3d-io/k3d/main/install.sh \| bash`     |
| kubectl  | v1.28+      | [kubernetes.io/docs](https://kubernetes.io/docs/tasks/tools/)                      |
| helm     | v3.14+      | `curl https://raw.githubusercontent.com/helm/helm/main/scripts/get-helm-3 \| bash` |
| Python 3 | 3.9+        | system package                                                                     |
| yq       | v4+         | `deploy-local.sh` reads `conf.yml` through it                                      |

> **WSL2 note**: Port 80 is blocked on WSL2. The cluster maps host port 8080 → cluster port 80. All
> `localhost:80` references become `localhost:8080`.

---

## Quick start (one command)

```bash
./deploy-local.sh    # cluster + builds + manifests + Helm install (5-15 min cold)
```

The collector mode is controlled entirely by `conf.yml`:

```yaml
monitoring:
  mode: cloud   # or: local
```

- `mode: cloud` (default) — the `grafana/k8s-monitoring` Helm chart's Alloy agents ship to Grafana
  Cloud Tempo/Mimir/Loki. `deploy-local.sh` renders `values-cloud.yaml.tmpl` from
  `monitoring.grafana_cloud` in `conf.yml` and installs the chart unconditionally.
- `mode: local` — a bespoke Alloy DaemonSet under `k8s/monitoring/grafana/local/` exports to
  in-cluster Jaeger/Prometheus/Loki/Grafana. Pass `--with-helm` to also install the Helm chart in
  this mode (it's otherwise optional locally).

For subsequent iterations once the cluster exists:

```bash
./deploy-local.sh --skip-cluster --skip-build   # manifests-only, <1 min
```

See [CLAUDE.md](https://github.com/shipsolid/signal-forge/blob/main/CLAUDE.md) for the full flag
list and the safety checks `deploy-local.sh` runs before every apply (k3d context guard, NodePort
drift check, Secret-key contract validation).

---

## Step-by-step walkthrough

### 1. Cluster creation

`deploy-local.sh` creates the k3d cluster and maps host ports read from `conf.yml`'s
`cluster.ports[]` (filtered by `ports[].mode` — local-only ports like Jaeger/Prometheus/Grafana are
skipped entirely in cloud mode):

| Host port | Cluster target             | Service                          | Mode       |
| --------- | -------------------------- | -------------------------------- | ---------- |
| `8080`    | port 80 on loadbalancer    | Frontend + API (Traefik ingress) | always     |
| `16686`   | NodePort 30686 on server-0 | Jaeger UI                        | local only |
| `3000`    | NodePort 30300 on server-0 | Grafana                          | local only |
| `9090`    | NodePort 30090 on server-0 | Prometheus                       | local only |
| `15672`   | NodePort 30672 on server-0 | RabbitMQ Management              | always     |

**Corporate proxy (Zscaler) — automated:** if `/usr/local/share/ca-certificates/zcert.crt` exists on
the host, `deploy-local.sh` stages it into each Docker build context and injects it into the k3d
server node's trust store, then reloads the k3d nginx load balancer. No-op on non-corporate machines
(an empty placeholder is staged so `COPY zcert.crt` in each Dockerfile never fails).

### 2. Build and import Docker images

Builds all four images (`docker build --network=host`) then `k3d image import`s them directly — no
external registry pull required at deploy time. `FARO_API_KEY`, when set in the shell, is forwarded
as a build arg to the frontend build so the webpack plugin can upload source maps.

### 3. Apply manifests

`deploy-local.sh` applies, in order (per `conf.yml`'s `manifests.{infra,datastores,app,post}`):

1. `k8s/infra/` (namespace, secrets, PDBs, network policies) — one `kubectl apply -f` per file
2. `k8s/datastores/{mysql,postgres,redis,rabbitmq}/` — waits for
   `kubectl -n otel-lab wait --for=condition=ready pod -l tier=datastore --timeout=180s`
3. Monitoring manifests for the active `monitoring.mode` (local DaemonSet, or nothing — cloud mode
   is entirely Helm-managed)
4. `k8s/app/{gateway,order,notification,frontend}/`
5. Post-stage manifests (ingress, cert-manager issuer when `security.tls.enabled`)

### 4. Grafana Cloud knobs in `conf.yml`

`deploy-local.sh` sources the env file named by `monitoring.grafana_cloud.use_env` and writes its
nine `GRAFANA_CLOUD_*`/`FARO_*` credentials into the in-cluster `grafana-cloud-secrets` Secret — see
[[grafana-cloud|grafana-cloud.md]] for the full model.
`scripts/fetch-grafana-cloud-conf-from-akv.sh` populates that same env file from Azure Key Vault;
there's no separate conf.yml-fields path:

```yaml
grafana_cloud:
  use_env: ".env"
```

### 5. Helm monitoring stack

`deploy-local.sh` installs `grafana/k8s-monitoring` (version pinned in `conf.yml`'s
`monitoring.helm.version`) using the values file selected by
`monitoring.helm.values_file_by_mode.<mode>` — unconditionally in cloud mode, only when
`--with-helm` is passed in local mode. See [[projects/app-signal-forge/deployment/helm|helm.md]] for
the Alloy role breakdown.

> **Important**: application services send OTLP to `alloy-receiver` in the `monitoring` namespace.
> If the Helm chart isn't installed, traces and metrics are silently lost — `./scripts/debug.sh`
> checks this reachability.

### 6. Verify deployment

```bash
./scripts/debug.sh   # mode-aware: conf.yml values, pod state, Alloy exporter counters,
                      # remote-write reachability probe, alloy-receiver endpoint check
curl -s http://localhost:8080/healthz
curl -s http://localhost:8080/api/projects
```

### 7. Generate traffic

```bash
kubectl apply -f k8s/loadtest/
```

The k6 script runs for 3 minutes (30s ramp-up → 2m sustained → 30s ramp-down) at up to 20 concurrent
users. It creates projects and orders, reads notifications, and occasionally hits `/api/slow` and
`/api/error`.

---

## Switching between local and cloud collector modes

Edit `monitoring.mode` in `conf.yml`, then re-run:

```bash
./deploy-local.sh --skip-cluster --skip-build
```

- `local`: applies `k8s/monitoring/grafana/local/` (hand-rolled Alloy DaemonSet exporting to
  in-cluster Jaeger/Prometheus/Loki), plus `k8s/monitoring/local/` backends. Pass `--with-helm` to
  also install the Helm chart.
- `cloud`: the Helm chart's Alloy agents export to Grafana Cloud. No in-cluster
  Jaeger/Prometheus/Loki/Grafana are deployed. There is no dual-export — switching modes changes the
  destination, it doesn't add one.

---

## Accessing local backends (`monitoring.mode: local`)

| Service                         | URL                      | Credentials   |
| ------------------------------- | ------------------------ | ------------- |
| Angular SPA                     | `http://localhost:8080`  | —             |
| Grafana                         | `http://localhost:3000`  | admin / admin |
| Jaeger         | `http://localhost:16686` | —             |
| Prometheus | `http://localhost:9090`  | —             |
| RabbitMQ                        | `http://localhost:15672` | guest / guest |

Alloy debug UI (pipeline graph, component status) — both modes, once the Helm chart is installed:

```bash
kubectl port-forward svc/grafana-k8s-alloy-receiver 12345 -n monitoring
open http://localhost:12345
```

---

## Tear down

```bash
./deploy-local.sh --teardown   # delete the k3d cluster entirely
```

---

## Common issues

### nginx LB stale IP after container restart

If `kubectl` commands hang after a Docker/WSL2 restart, the nginx load balancer has a cached stale
IP. Reload it:

```bash
docker exec k3d-otel-lab-serverlb nginx -s reload
```

`deploy-local.sh` does this automatically after Zscaler cert injection, but a Docker Desktop restart
between runs can reintroduce it.

### Images not found in k3d

If pods show `ErrImagePull`, the images were not imported into k3d's internal registry — re-run
`./deploy-local.sh` without `--skip-build`.

### Datastores not ready

If app pods crash on startup with DB connection errors, the datastores may not be ready yet:

```bash
kubectl -n otel-lab get pods -l tier=datastore
kubectl -n otel-lab wait --for=condition=ready pod -l tier=datastore --timeout=180s
kubectl -n otel-lab rollout restart deployment/gateway-api deployment/order-api
```

### Alloy not receiving OTLP

Check the receiver is running and the endpoint is correct:

```bash
kubectl -n monitoring get pods -l app.kubernetes.io/component=alloy-receiver
kubectl -n otel-lab exec deploy/gateway-api -- env | grep OTEL_EXPORTER_OTLP
# Should be: http://grafana-k8s-alloy-receiver.monitoring.svc.cluster.local:4317
```

Or run `./scripts/debug.sh`, which checks this reachability automatically.
