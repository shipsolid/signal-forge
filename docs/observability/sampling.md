# Tail-Based Sampling

## What it is

Tail-based sampling makes the keep/drop decision **after** all spans of a trace have arrived, not when the first span is received. This allows the sampler to inspect the entire trace — including outcome (error/success) and total duration — before deciding.

Contrast with head-based sampling (e.g. probabilistic sampling in the SDK): the decision is made at the first span, before the trace outcome is known. Head-based sampling cannot guarantee 100% retention of error traces.

## Configuration

```river
otelcol.processor.tail_sampling "default" {
  decision_wait               = "10s"
  num_traces                  = 1000
  expected_new_traces_per_sec = 100

  policy {
    name = "errors-always"
    type = "status_code"
    status_code { status_codes = ["ERROR"] }
  }
  policy {
    name = "slow-requests"
    type = "latency"
    latency { threshold_ms = 2000 }
  }
  policy {
    name = "probabilistic-rest"
    type = "probabilistic"
    probabilistic { sampling_percentage = 25 }
  }

  output { traces = [otelcol.processor.batch.default.input] }
}
```

## Policies

Policies are evaluated in declaration order. The first matching policy determines the outcome.

| Policy               | Type            | Rule                                      | Keep rate |
| -------------------- | --------------- | ----------------------------------------- | --------- |
| `errors-always`      | `status_code`   | Any span in trace has `STATUS_CODE_ERROR` | **100%**  |
| `slow-requests`      | `latency`       | Trace end-to-end duration > 2000ms        | **100%**  |
| `probabilistic-rest` | `probabilistic` | All remaining traces                      | **25%**   |

### Why these thresholds

- **2s for slow requests**: The `/api/slow` endpoint introduces a 2–5s delay; any latency spike worth investigating exceeds 2s. p99 of normal requests is well under 500ms.
- **25% for normal traffic**: Sufficient for representative tail latency percentile calculation at k6 load test volumes. In production, tune based on trace storage cost and cardinality needs.
- **10s decision wait**: Accommodates the async RabbitMQ leg. The notification-svc CONSUMER span arrives up to several seconds after the gateway-api request completes. Without a 10s window, the trace could be evaluated while still incomplete, potentially missing the error status on the consumer span.

## Relationship to span metrics

Span metrics run **before** tail sampling on purpose:

```
filter(healthz) → spanmetrics  ← sees 100% of traffic
               ↘
                 tail_sampling  ← 25% + errors + slow
                             ↓
                           batch → exporters
```

If spanmetrics ran after sampling:

- `traces_spanmetrics_calls_total` would read 25% of actual traffic
- Rate calculations in Grafana dashboards and SLO expressions would be wrong
- Error rate would only count errors that happened to be sampled (all of them, since errors-always=100%) but normal traffic baseline would be underrepresented

See [ADR-003](../architecture/decisions.md#adr-003-span-metrics-generated-before-tail-sampling).

## Decision window trade-off

`decision_wait = 10s` means Alloy holds all spans for a trace in memory for up to 10 seconds before making a sampling decision. This introduces:

- **Up to 10s delay** between a request completing and its trace appearing in Jaeger/Tempo.
- **Memory overhead**: `num_traces = 1000` traces in buffer × average span size. Fine for the lab; for production size this based on trace volume.

For production: set `decision_wait` to at least your p99 trace duration (including async legs). Measure this using the span metrics `traces_spanmetrics_latency_bucket` p99 before enabling tail sampling.

## Validating sampling rates

With the k6 load test running (`make test`):

### Verify 100% error capture

```bash
# Call the error endpoint 10 times
for i in $(seq 1 10); do curl -s http://localhost:8080/api/error; done

# In Jaeger: search service=gateway-api, tags: error=true
# All 10 should appear.
```

### Verify 100% slow capture

```bash
# Call the slow endpoint 5 times
for i in $(seq 1 5); do curl -s http://localhost:8080/api/slow; done

# In Jaeger: search service=gateway-api, min duration=2s
# All 5 should appear.
```

### Verify ~25% normal traffic

```bash
# Run k6 for 1 minute to generate baseline
kubectl apply -f k8s/loadtest/

# Compare span metrics (all traffic) vs sampled traces
# In Prometheus:
rate(traces_spanmetrics_calls_total{service_name="gateway-api",http_route="/api/projects"}[5m])

# In Jaeger: count traces for the same route over the same window
# Expected: Jaeger count ≈ 25% of span metrics count
```

## PromQL reference

```promql
# Total request rate (pre-sampling, from span metrics)
sum by (service_name) (
  rate(traces_spanmetrics_calls_total[1m])
)

# Error rate
sum by (service_name) (
  rate(traces_spanmetrics_calls_total{status_code="STATUS_CODE_ERROR"}[1m])
) / sum by (service_name) (
  rate(traces_spanmetrics_calls_total[1m])
)

# P95 latency per route
histogram_quantile(0.95,
  sum by (le, http_route) (
    rate(traces_spanmetrics_latency_bucket{service_name="gateway-api"}[5m])
  )
)
```

## Production tuning checklist

| Parameter                     | Current | Production guidance                                                   |
| ----------------------------- | ------- | --------------------------------------------------------------------- |
| `decision_wait`               | 10s     | ≥ p99 trace duration (measure first)                                  |
| `num_traces`                  | 1000    | Set to (expected_new_traces_per_sec × decision_wait × 2) for headroom |
| `expected_new_traces_per_sec` | 100     | Set to observed peak TPS                                              |
| `probabilistic_percentage`    | 25      | Lower for cost, higher for debugging fidelity                         |
| Error policy                  | 100%    | Keep at 100% — non-negotiable for SLOs                                |
| Slow threshold                | 2000ms  | Align with your SLO latency budget                                    |
