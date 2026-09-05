---
title: "Helm Monitoring Stack"
description: "How the grafana/k8s-monitoring Helm chart deploys Alloy roles for collecting and exporting telemetry, and why it replaced the legacy Makefile flow."
tags: ["ShipSolid", "Signal Forge", "Deployment"]
updated: 2026-07-10
zettelId: "202607091847-15"
relations:
  - slug: projects/app-signal-forge/architecture/adrs/adr-helm-managed-alloy-stack
    kind: depends_on
  - slug: projects/app-signal-forge/deployment/local
    kind: related
  - slug: projects/app-signal-forge/deployment/grafana-cloud
    kind: related
  - slug: observability/reference/prometheus
    kind: related
---

## Helm Monitoring Stack

The observability collector stack uses the `grafana/k8s-monitoring` Helm chart (v3.8.4). It deploys
up to five specialised Grafana Alloy roles into the `monitoring` namespace. **`./deploy-local.sh` is
the only supported way to install it** — see [[local|local.md]] and
[[grafana-cloud|grafana-cloud.md]]. The Makefile-driven `make deploy-helm` / `make helm-render` /
`make deploy-helm-cloud` flow described in earlier versions of this doc has been retired: it was a
second, parallel Helm-values pipeline (Jinja2-templated, `k8s/monitoring/grafana-helm/render.py` +
`config.yaml.j2`) that hardcoded real production Grafana Cloud stack IDs and AKS namespace names
left over from a copy-paste, and it duplicated what `deploy-local.sh` already does from `conf.yml`
directly. Those files are deleted; `make deploy-helm*`/`make helm-render`/`make full-helm` now fail
with a message pointing at `./deploy-local.sh`.

---

## Why Helm instead of raw manifests

The hand-rolled Alloy DaemonSet (`k8s/monitoring/grafana/local/`) is the local-mode alternative —
see [[local|local.md]]. Running both the Helm chart and the hand-rolled DaemonSet against the same
cluster causes duplicate spans, version mismatches, and CrashLoopBackOff from River config
incompatibilities, so `deploy-local.sh` only ever installs one collector path per `monitoring.mode`.
The Helm chart manages RBAC, ServiceAccounts, versioned upgrades, and multi-role coordination. See
[[projects/app-signal-forge/architecture/adrs/adr-helm-managed-alloy-stack|ADR-004]].

---

## Alloy roles

| Role              | Kind        | Collects                                   | Sends to                                              |
| ----------------- | ----------- | ------------------------------------------ | ----------------------------------------------------- |
| `alloy-receiver`  | DaemonSet   | OTLP push (:4317/:4318), Faro RUM (:12347) | Jaeger / Grafana Cloud Tempo                          |
| `alloy-logs`      | DaemonSet   | Pod stdout/stderr, node journal            | Loki / Grafana Cloud Loki                             |
| `alloy-metrics`   | StatefulSet | kubelet, cAdvisor, KSM, node-exporter      | Prometheus / Grafana Cloud Mimir |
| `alloy-singleton` | Deployment  | Cluster events, KSM API                    | Loki + Prometheus / cloud                             |
| `alloy-profiles`  | DaemonSet   | Continuous profiling (Pyroscope)           | **Disabled locally**                                  |

---

## Values files

`deploy-local.sh` selects the values file via `monitoring.helm.values_file_by_mode.<mode>` in
`conf.yml`:

- **local mode** — `k8s/monitoring/grafana-helm/values-local.yaml`, destinations point at in-cluster
  Jaeger/Prometheus/Loki.
- **cloud mode** — `k8s/monitoring/grafana-helm/values-cloud.yaml.tmpl`, rendered by
  `deploy-local.sh` with `${...}` placeholders substituted from `conf.yml`'s
  `monitoring.grafana_cloud` block; destinations point at Grafana Cloud Mimir/Loki/Tempo.

| Setting                     | Local value                              | Cloud value   | Reason                                    |
| --------------------------- | ---------------------------------------- | ------------- | ----------------------------------------- |
| `destinations`              | In-cluster services (otel-lab namespace) | Grafana Cloud | No cloud for offline dev                  |
| `opencost.enabled`          | `false`                                  | `false`       | No cloud billing APIs wired up in the lab |
| `kepler.enabled`            | `false`                                  | `false`       | eBPF energy metrics unreliable on WSL2/VM |
| `alloy-profiles.enabled`    | `false`                                  | `false`       | No Pyroscope backend in either mode       |
| `prometheusOperatorObjects` | disabled                                 | disabled      | No Prometheus-Operator CRDs in the lab    |
| `remoteConfig.enabled`      | `false` (all agents)                     | `false`       | Prevents Fleet Management override        |

`k8s/monitoring/grafana-helm/gen-cloud-overlay.py` is a separate, still-live script used only by the
legacy `make secrets-fetch-akv`/`make secrets-apply` targets (see
[[grafana-cloud|grafana-cloud.md]]) — it is not part of the `deploy-local.sh` path.

---

## Deploy

```bash
./deploy-local.sh                              # full: cluster + builds + apply + helm install
./deploy-local.sh --skip-cluster --skip-build  # manifests + helm only, <1 min
./deploy-local.sh --with-helm                  # local mode: install the Helm chart too (unconditional in cloud mode)
```

Watch pods come up:

```bash
kubectl get pods -n monitoring -w
```

Active roles should reach `Running` within 60-90 seconds.

Tear down:

```bash
./deploy-local.sh --teardown
```

---

## Alloy receiver endpoint

Application services send OTLP to:

```
http://grafana-k8s-alloy-receiver.monitoring.svc.cluster.local:4317
```

This is the ClusterIP DNS name for the `alloy-receiver` DaemonSet's Service. It is set in all
application Deployment env vars as `OTEL_EXPORTER_OTLP_ENDPOINT`. If the Helm chart isn't installed
(e.g. local mode run without `--with-helm`), applications send to this endpoint but nothing listens
— all telemetry is silently lost. `./scripts/debug.sh` checks this reachability.

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

`alloy-metrics` supports automatic scraping of any pod with these annotations (no ServiceMonitor
required):

```yaml
metadata:
  annotations:
    k8s.grafana.com/scrape: "true"
    k8s.grafana.com/metrics.portNumber: "8080"   # adjust to actual metrics port
```

Add these to any application pod template to have its Prometheus metrics endpoint scraped
automatically.

---

## Chart version pinning

The chart version is pinned in `conf.yml`'s `monitoring.helm.version` (currently 3.8.4). To upgrade:

1. Update `monitoring.helm.version` in `conf.yml`.
2. Review the chart changelog for breaking changes.
3. Re-run `./deploy-local.sh --skip-cluster --skip-build` — Helm performs a rolling upgrade.
4. Verify all active roles come up and telemetry continues to flow.
