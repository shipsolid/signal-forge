# Helm Monitoring Stack

The observability collector stack uses the `grafana/k8s-monitoring` Helm chart (v3.8.4). It deploys five specialised Grafana Alloy roles into the `monitoring` namespace.

---

## Why Helm instead of raw manifests

The hand-rolled Alloy DaemonSet (`k8s/monitoring/grafana/`) is kept as a reference artifact but is **not deployed**. Running both caused:

- Duplicate spans in Jaeger and Prometheus
- Version mismatches between the pinned image and Helm chart expectations
- CrashLoopBackOff from River config incompatibilities

The Helm chart manages RBAC, ServiceAccounts, versioned upgrades, and multi-role coordination. See [ADR-004](../architecture/decisions.md#adr-004-helm-managed-alloy-stack-grafanaks-monitoring).

---

## Alloy roles

| Role              | Kind        | Collects                                   | Sends to                         |
| ----------------- | ----------- | ------------------------------------------ | -------------------------------- |
| `alloy-receiver`  | DaemonSet   | OTLP push (:4317/:4318), Faro RUM (:12347) | Jaeger / Grafana Cloud Tempo     |
| `alloy-logs`      | DaemonSet   | Pod stdout/stderr, node journal            | Loki / Grafana Cloud Loki        |
| `alloy-metrics`   | StatefulSet | kubelet, cAdvisor, KSM, node-exporter      | Prometheus / Grafana Cloud Mimir |
| `alloy-singleton` | Deployment  | Cluster events, KSM API                    | Loki + Prometheus / cloud        |
| `alloy-profiles`  | DaemonSet   | Continuous profiling (Pyroscope)           | **Disabled locally**             |

---

## Values file (`k8s/monitoring/grafana-helm/values-local.yaml`)

Key local overrides compared to the production `09-grafana-k8s` config:

| Setting                     | Local value                              | Production value | Reason                                    |
| --------------------------- | ---------------------------------------- | ---------------- | ----------------------------------------- |
| `destinations`              | In-cluster services (otel-lab namespace) | Grafana Cloud    | No cloud for offline dev                  |
| `opencost.enabled`          | `false`                                  | `true`           | No cloud billing APIs                     |
| `kepler.enabled`            | `false`                                  | `true`           | eBPF energy metrics unreliable on WSL2/VM |
| `alloy-profiles.enabled`    | `false`                                  | `true`           | No local Pyroscope                        |
| `prometheusOperatorObjects` | disabled                                 | enabled          | No CRDs installed in k3d                  |
| `remoteConfig.enabled`      | `false` (all agents)                     | `true`           | Prevents Fleet Management override        |

---

## Deploy

### Add Helm repo (once)

```bash
make helm-repo
# = helm repo add grafana https://grafana.github.io/helm-charts && helm repo update
```

### Install / upgrade

```bash
make deploy-helm
# = helm upgrade --install grafana-k8s-monitoring grafana/k8s-monitoring \
#     --version 3.8.4 \
#     --namespace monitoring --create-namespace \
#     -f k8s/monitoring/grafana-helm/values-local.yaml
```

### Watch pods come up

```bash
kubectl get pods -n monitoring -w
```

All four active roles should reach `Running` within 60-90 seconds.

### Tear down

```bash
make teardown-helm
# = helm uninstall grafana-k8s-monitoring -n monitoring && kubectl delete namespace monitoring
```

---

## Alloy receiver endpoint

Application services send OTLP to:

```
http://grafana-k8s-alloy-receiver.monitoring.svc.cluster.local:4317
```

This is the ClusterIP DNS name for the `alloy-receiver` DaemonSet's Service. It is set in all application Deployment env vars as `OTEL_EXPORTER_OTLP_ENDPOINT`.

If `make full` is run without `make deploy-helm`, applications send to this endpoint but nothing listens — all telemetry is lost. Always run `make full-helm` or follow `make full` with `make deploy-helm`.

---

## Alloy debug UI

The alloy-receiver exposes a debug UI with:

- Pipeline graph (visualise all components and connections)
- Component status (healthy / degraded)
- Live trace inspection

```bash
kubectl port-forward svc/grafana-k8s-alloy-receiver 12345 -n monitoring
open http://localhost:12345
```

---

## Annotation-based metrics scraping

`alloy-metrics` supports automatic scraping of any pod with these annotations (no ServiceMonitor required):

```yaml
metadata:
  annotations:
    k8s.grafana.com/scrape: "true"
    k8s.grafana.com/metrics.portNumber: "8080"   # adjust to actual metrics port
```

Add these to any application pod template to have its Prometheus metrics endpoint scraped automatically.

---

## Cloud overlay (optional)

For production-parity testing with Grafana Cloud destinations:

```bash
# Render Jinja2 template with cloud credentials
make helm-render

# Deploy with cloud values
make deploy-helm-cloud
# = helm upgrade --install ... -f k8s/monitoring/grafana-helm/values-local.yaml -f k8s/monitoring/grafana-helm/generated/values-cloud.yaml
```

The cloud overlay (`k8s/monitoring/grafana-helm/generated/`) is git-ignored. It is generated from `k8s/monitoring/grafana-helm/config.yaml.j2` by `k8s/monitoring/grafana-helm/render.py`.

---

## Chart version pinning

The chart is pinned to v3.8.4 in the Makefile. To upgrade:

1. Update `HELM_CHART_VERSION` in the Makefile
2. Review the chart changelog for breaking changes
3. Run `make deploy-helm` — Helm performs a rolling upgrade
4. Verify all five roles come up and telemetry continues to flow
