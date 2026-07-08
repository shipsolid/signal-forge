# SLOs & burn-rate alerts

What objectives signal-forge commits to, how they're measured, and how the alerts on them are structured. Rules as code live in [k8s/monitoring/slo-rules.yaml](../../k8s/monitoring/slo-rules.yaml).

## Published SLOs

| Service          | SLI                                | Objective    | Window          |
| ---------------- | ---------------------------------- | ------------ | --------------- |
| gateway-api      | non-5xx responses / total requests | **99.5%**    | rolling 30 days |
| gateway-api      | p99 request latency                | **< 500 ms** | rolling 5m      |
| order-api        | non-5xx responses / total requests | 99.5%        | rolling 30 days |
| order-api        | p99 request latency                | **< 300 ms** | rolling 5m      |
| notification-svc | non-5xx / total                    | 99.5%        | 30 days         |
| notification-svc | p99 latency                        | < 300 ms     | 5m              |

Error budget for a 99.5% / 30-day objective = **0.5%** of requests = ~3.6 hours/month of "unavailable" (in the error-budget sense).

## Where the numbers come from

All SLIs are computed from metrics the OTel Collector **spanmetrics** connector generates from app traces:

- `traces_spanmetrics_calls_total{service_name, status_code}` — one counter per RPC
- `traces_spanmetrics_duration_milliseconds_bucket{service_name, le}` — Prometheus histogram buckets for latency

Because spanmetrics runs inside the Alloy receiver (monitoring ns), these metrics are produced regardless of whether an app explicitly emits its own request counters. They're also more accurate than app-side instrumentation for cross-service SLOs (trace-driven → covers every hop uniformly).

Recording rules in `slo-rules.yaml` pre-compute:

```
sli:requests:rate5m      = sum(rate(...calls_total[5m]))     by (service_name)
sli:errors:rate5m        = sum(rate(...calls_total{status_code="STATUS_CODE_ERROR"}[5m])) by (service_name)
sli:error_ratio:rate5m   = sli:errors:rate5m / clamp_min(sli:requests:rate5m, 1e-9)
sli:latency_p99:rate5m   = histogram_quantile(0.99, sum(rate(...duration_bucket[5m])) by (le, service_name))
```

The `clamp_min(..., 1e-9)` avoids 0/0 during idle windows; the result is vanishingly small and never triggers an alert.

Rates are also computed at 30m and 6h windows — those feed the multi-window burn alerts.

## Multi-window burn-rate alerts

Following the Google SRE workbook approach: compute the rate at which the error budget is burning, over two windows simultaneously, and alert when both windows cross their threshold. This gives you:

- **Fast-burn (page)** — 14.4× budget burn rate. At this rate, 2% of the 30-day budget is consumed in 1 hour. Requires a short window (5m) to be current AND a longer window (30m) to confirm it's not a transient spike.
- **Slow-burn (ticket)** — 6× budget burn rate. 5% of the budget in 6 hours. Detected by 30m + 6h windows both exceeding the threshold.

Threshold math for a 99.5% SLO: target error budget = 0.005, so the fast-burn alert fires when `error_ratio > 14.4 × 0.005 = 0.072` (= 7.2% errors) in BOTH windows.

### Alert summary

| Alert                              | Severity | `for` | Trigger                                   |
| ---------------------------------- | -------- | ----- | ----------------------------------------- |
| `SignalForgeAvailabilityFastBurn`  | `page`   | 2m    | error_ratio > 7.2% in 5m AND 30m          |
| `SignalForgeAvailabilitySlowBurn`  | `ticket` | 15m   | error_ratio > 3% in 30m AND 6h            |
| `SignalForgeGatewayLatencyHigh`    | `ticket` | 10m   | gateway p99 > 500ms                       |
| `SignalForgeDownstreamLatencyHigh` | `ticket` | 10m   | order-api or notification-svc p99 > 300ms |
| `AlloyReceiverDown`                | `page`   | 5m    | `up` == 0 for alloy-receiver              |
| `DatastoreDown`                    | `page`   | 3m    | any datastore pod not Ready               |

### Why two-window + `for` clause

The `for: 2m` on the fast-burn alert is not redundant with the multi-window AND — it's a scheduling dampener. Without it, a single scrape interval of bad data could fire. 2m is short enough to still page within the SLO's fast-burn target (we want to know within ~10m that the budget is on fire) and long enough to absorb individual scrape failures.

The slow-burn alert has `for: 15m` because its whole purpose is non-urgent — 15 minutes of delay is cheap and kills noise.

## Where the alerts are evaluated

`k8s/monitoring/slo-rules.yaml` is a bare Prometheus/Mimir rule file (native `groups:` format,
not a `PrometheusRule` CRD) — one file, no Prometheus-Operator dependency, consumed differently
per `monitoring.mode`:

| Mode    | Evaluator                     | How rules land |
| ------- | ------------------------------ | -------------- |
| `local` | In-cluster vanilla Prometheus | Automatic. `deploy-local.sh`'s `apply_local_slo_rules()` generates a `prometheus-slo-rules` ConfigMap from this file and Prometheus loads it via `rule_files:` — no kube-prometheus-stack needed. |
| `cloud` | Grafana Cloud Mimir (Ruler) | Manual. Run `./scripts/push-slo-rules-to-mimir.sh` (wraps `mimirtool rules load`) — Grafana Cloud Mimir doesn't consume this file on its own, and `deploy-local.sh` won't push to a live account automatically. Use `--dry-run` for a read-only `mimirtool rules diff` first. |

If a cluster *does* have kube-prometheus-stack installed, `deploy-local.sh`'s `apply_slo_rules()`
wraps the same file's `groups:` into a `PrometheusRule` on the fly (no separate CRD-shaped copy
is maintained anywhere).

`observability.slo_rules.enabled` in conf.yml (default `true`) is the master switch for both the
local ConfigMap load and the kube-prometheus-stack fallback; it doesn't gate the cloud script,
which you run explicitly when you want rules live in Grafana Cloud.

## Runbook links

Every alert has a `runbook_url` annotation pointing at `https://example.com/runbooks/...` as a placeholder. Real runbook content would live in:

- [docs/operations/runbooks.md](../operations/runbooks.md) — update with a section per alert name
- Or a Wiki / Notion / PagerDuty runbook field, replacing the URL in the `PrometheusRule` annotations.

## Tuning

- **Bump targets**: edit the threshold values in `slo-rules.yaml`. The `(14.4 * 0.005)` and `(6 * 0.005)` forms make the relationship explicit — change `0.005` to `0.001` for a 99.9% SLO, or `0.01` for 99%.
- **Different sample windows**: the recording rules produce 5m, 30m, and 6h ratios. To add a 1h window, add a new `- record:` entry and a new alert that combines 1h with another window.
- **Per-service objectives**: the current alerts aggregate across all services. To split, add `service_name` to the alert label and duplicate the rules per service, or write per-service alerts that reference `service_name=~"gateway-api"`.

## What this doesn't cover

- **Synthetic monitoring**. The k6 load-test Job in [k8s/loadtest/](../../k8s/loadtest) is manual. For continuous synthetic traffic, convert to a `CronJob` that runs every 5 minutes and points at `/healthz`.
- **Client-perceived SLOs**. Frontend RUM (Faro) is ingested but not turned into SLIs. A real client-latency SLO would use Faro's `web_vitals_lcp_seconds` or a custom `page_load_total`.
- **Multi-window burn for latency**. Latency alerts are single-window (5m > threshold for 10m). A multi-window burn for latency is possible (see [Google SRE workbook]), but we picked single-window for simplicity — latency deserves different tuning than availability.

[Google SRE workbook]: https://sre.google/workbook/alerting-on-slos/
