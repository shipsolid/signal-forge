# Local Deployment

This guide sets up the full lab on a local machine using k3d. No cloud accounts required.

## Prerequisites

| Tool     | Min version | Install                                                                            |
| -------- | ----------- | ---------------------------------------------------------------------------------- |
| Docker   | 24+         | [docs.docker.com](https://docs.docker.com/get-docker/)                             |
| k3d      | v5+         | `curl -s https://raw.githubusercontent.com/k3d-io/k3d/main/install.sh \| bash`     |
| kubectl  | v1.28+      | [kubernetes.io/docs](https://kubernetes.io/docs/tasks/tools/)                      |
| helm     | v3.14+      | `curl https://raw.githubusercontent.com/helm/helm/main/scripts/get-helm-3 \| bash` |
| Python 3 | 3.9+        | system package                                                                     |

> **WSL2 note**: Port 80 is blocked on WSL2. The cluster maps host port 8080 → cluster port 80. All `localhost:80` references become `localhost:8080`.

---

## Quick start (one command)

```bash
make full-helm   # cluster + builds + k8s manifests + Helm monitoring stack
make validate    # smoke-test all endpoints
```

This runs the full cloud-mode deployment (Alloy exports to Grafana Cloud if credentials are present, otherwise operates in graceful-degradation mode with only local backends).

For an offline local-only environment:

```bash
make full-helm           # cluster + builds + app services
make deploy-local        # overwrite with local configmap + deploy Jaeger/Prom/Loki/Grafana
```

For `./deploy-local.sh`, the collector mode is now controlled directly in `conf.yml`:

```yaml
monitoring:
  mode: local   # or: cloud
```

When `mode: cloud`, `deploy-local.sh` generates the `grafana-cloud-secrets` Secret from the `grafana_cloud:` values in `conf.yml` before applying monitoring manifests.

---

## Step-by-step walkthrough

### 1. Create k3d cluster

```bash
make cluster-up
```

Creates the cluster and maps host ports:

| Host port | Cluster target             | Service                          |
| --------- | -------------------------- | -------------------------------- |
| `8080`    | port 80 on loadbalancer    | Frontend + API (Traefik ingress) |
| `16686`   | NodePort 30686 on server-0 | Jaeger UI                        |
| `3000`    | NodePort 30300 on server-0 | Grafana                          |
| `9090`    | NodePort 30090 on server-0 | Prometheus                       |
| `15672`   | NodePort 30672 on server-0 | RabbitMQ Management              |

**Corporate proxy (Zscaler) — automated:**
If `/usr/local/share/ca-certificates/zcert.crt` exists on the host, `cluster-up` automatically:

1. Copies the cert into `k3d-otel-lab-server-0` via `docker cp`
2. Installs it into `/usr/local/share/ca-certificates/zcert.crt` inside the node container and appends it to `/etc/ssl/certs/ca-certificates.crt`
3. Restarts `k3d-otel-lab-server-0` so k3s/containerd picks up the updated trust store for image pulls
4. Waits 8 seconds for k3s to stabilise
5. Reloads the nginx load balancer (`k3d-otel-lab-serverlb`) so `kubectl` remains responsive

This step is a no-op on machines without the cert file.

### 2. Build and import Docker images

```bash
make import   # = make build + k3d image import
```

Builds four images then imports them directly into k3d's internal registry — no external registry pull required at deploy time.

**Corporate proxy (Zscaler) — automated in `make build`:**
Before each `docker build`, the Makefile copies `zcert.crt` from the host into the service's build context directory, then removes it after the build. Each Dockerfile installs it with `RUN update-ca-certificates` before any network step (`dotnet restore`, `pip install`, `npm ci`). The cert is never committed — it is listed in `.gitignore`.

```text
Host: /usr/local/share/ca-certificates/zcert.crt
  → cp → src/gateway-api/zcert.crt       (temporary, deleted after build)
  → cp → src/order-api/zcert.crt         (temporary, deleted after build)
  → cp → src/notification-svc/zcert.crt  (temporary, deleted after build)
```

The Angular frontend build does not require the cert because `npm ci` runs inside the container using the same injected cert path.

### 3. Deploy infrastructure and applications

```bash
make deploy   # = make deploy-cloud (default)
```

Applies in order:

1. `k8s/infra/` (namespace, secrets)
2. `k8s/datastores/` (MySQL, PostgreSQL, Redis, RabbitMQ)
3. Waits for datastores: `kubectl -n otel-lab wait --for=condition=ready pod -l tier=datastore --timeout=180s`
4. `k8s/monitoring/grafana/` + `k8s/monitoring/grafana/grafana-cloud/` (Alloy configmap)
5. `k8s/app/` (all four application services)
6. `k8s/infra/ingress.yaml`

### 3a. Grafana Cloud knobs in `conf.yml`

`deploy-local.sh` reads these values and writes them into the in-cluster `grafana-cloud-secrets` Secret on every run:

```yaml
grafana_cloud:
  api_key: ""
  tempo:
    endpoint: ""
    user: ""
  mimir:
    endpoint: ""
    user: ""
  loki:
    endpoint: ""
    user: ""
  faro:
    endpoint: ""
    api_key: ""
```

Expected formats:

- `grafana_cloud.tempo.endpoint`: `tempo-prod-xx.grafana.net:443`
- `grafana_cloud.mimir.endpoint`: `https://<host>/api/v1/otlp`
- `grafana_cloud.loki.endpoint`: `https://<host>/loki/api/v1/push`
- `*.user`: Grafana Cloud numeric instance ID for that signal

### 4. Deploy Helm monitoring stack

```bash
make deploy-helm
```

Installs `grafana/k8s-monitoring` v3.8.4 chart into the `monitoring` namespace with local values (`k8s/monitoring/grafana-helm/values-local.yaml`). This creates:

- `alloy-receiver` (DaemonSet) — OTLP push receiver
- `alloy-logs` (DaemonSet) — pod log tailing
- `alloy-metrics` (StatefulSet) — infra metrics
- `alloy-singleton` (Deployment) — cluster events
- `alloy-profiles` (DaemonSet, disabled)

> **Important**: Application services send OTLP to `alloy-receiver` in the `monitoring` namespace. Without `make deploy-helm`, all traces and metrics are lost.

### 5. Verify deployment

```bash
make validate
```

Checks all services respond:

```bash
curl -s http://localhost:8080/healthz         # nginx
curl -s http://localhost:8080/api/projects    # gateway-api
curl -s http://localhost:16686                # Jaeger UI
curl -s http://localhost:9090/-/ready         # Prometheus
curl -s http://localhost:15672                # RabbitMQ Management
```

### 6. Generate traffic

```bash
make test   # applies k8s/loadtest/job.yaml (k6 load test)
```

The k6 script runs for 3 minutes (30s ramp-up → 2m sustained → 30s ramp-down) at up to 20 concurrent users. It creates projects and orders, reads notifications, and occasionally hits `/api/slow` and `/api/error`.

---

## Switching between local and cloud collector modes

### Switch to local (no cloud credentials needed)

```bash
make deploy-local
```

This:

1. Applies `k8s/monitoring/grafana/local/configmap.yaml` (Jaeger + Prometheus exporters)
2. Deploys Jaeger, Prometheus, Loki, Grafana into `otel-lab` namespace
3. Does NOT deploy cloud exporters

### Switch back to cloud

```bash
make deploy-cloud   # or: make deploy
```

This:

1. Applies `k8s/monitoring/grafana/grafana-cloud/configmap.yaml`
2. Does NOT deploy local backends (Jaeger, Prometheus, Loki, Grafana — these persist if previously deployed)
3. Restarts the Alloy DaemonSet to reload the configmap

---

## Accessing local backends (deploy-local mode)

| Service     | URL                      | Credentials   |
| ----------- | ------------------------ | ------------- |
| Angular SPA | `http://localhost:8080`  | —             |
| Grafana     | `http://localhost:3000`  | admin / admin |
| Jaeger      | `http://localhost:16686` | —             |
| Prometheus  | `http://localhost:9090`  | —             |
| RabbitMQ    | `http://localhost:15672` | guest / guest |

Alloy debug UI (pipeline graph, component status):

```bash
kubectl port-forward svc/grafana-k8s-alloy-receiver 12345 -n monitoring
open http://localhost:12345
```

---

## Tear down

```bash
make teardown        # delete otel-lab namespace (keeps cluster)
make teardown-helm   # uninstall Helm release, delete monitoring namespace
make cluster-down    # delete k3d cluster entirely
```

Full reset (rebuild from scratch):

```bash
make teardown && make teardown-helm && make cluster-down
make full-helm
```

---

## Common issues

### nginx LB stale IP after container restart

If `kubectl` commands hang after a Docker/WSL2 restart, the nginx load balancer has a cached stale IP. Reload it:

```bash
docker exec k3d-otel-lab-serverlb nginx -s reload
```

See memory note `feedback_k3d_nginx_lb.md`.

### Images not found in k3d

If pods show `ErrImagePull`, the images were not imported into k3d's internal registry:

```bash
make import   # re-import all images
```

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
