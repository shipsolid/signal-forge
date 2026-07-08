# Log-to-Trace Correlation

> **Local vs. cloud mode implementation.** The pipeline below (`loki.process "trace_correlation"` as
> a named River component) is local mode's hand-authored implementation
> (`k8s/monitoring/grafana/local/configmap.yaml`). Cloud mode achieves the same outcome — trace_id
> and span_id as Loki structured metadata — through a different mechanism: the same JSON-extraction
> and template logic is injected via the `grafana/k8s-monitoring` Helm chart's
> `podLogs.extraLogProcessingStages` hook in
> [`values-cloud.yaml.tmpl`](../../k8s/monitoring/grafana-helm/values-cloud.yaml.tmpl), since the
> chart doesn't expose named custom components the way a hand-rolled config does. See
> [pipeline.md's Cloud mode pipeline section](pipeline.md#cloud-mode-pipeline) for that mechanism.
> The field names, coalesce logic, and structured-metadata rationale below apply to both.

## Architecture

Logs are not shipped via OTLP. Applications write structured JSON to stdout; `alloy-logs` (a
DaemonSet) tails pod stdout at the node level and extracts trace IDs for Loki structured metadata.

```
.NET service → stdout (JSON, TraceId/SpanId fields)
Python service → stdout (JSON, otelTraceID/otelSpanID fields)
                              │
              alloy-logs DaemonSet (node-level)
                   loki.source.kubernetes
                              │
                   loki.process "trace_correlation"
                     stage.json (extract fields)
                     stage.template (normalise names)
                     stage.structured_metadata (attach)
                              │
                   loki.write → Loki
                              │
              Grafana "Logs for this span"
                   query: {trace_id="<id>"}
```

## Why node-level tailing instead of OTLP log push

See [ADR-001](../architecture/decisions.md#adr-001-log-tailing-instead-of-otlp-log-export). Short
version: production-parity, simpler application code, independent reliability.

## Log formats

### .NET (gateway-api, order-api)

Serilog with JSON formatter. OTel `LoggingInstrumentation` injects `TraceId` and `SpanId`
automatically:

```json
{
  "Timestamp": "2026-04-14T10:30:01.234Z",
  "Level": "Information",
  "MessageTemplate": "Processed order {OrderId}",
  "TraceId": "4bf92f3577b34da6a3ce929d0e0e4736",
  "SpanId": "00f067aa0ba902b7",
  "Properties": {
    "OrderId": 42,
    "RequestId": "...",
    "RequestPath": "/api/orders"
  }
}
```

### Python (notification-svc)

`python-json-logger` with `opentelemetry-instrumentation-logging`. Field names differ:

```json
{
  "asctime": "2026-04-14T10:30:01.234Z",
  "levelname": "INFO",
  "message": "Processed order.created event",
  "otelTraceID": "4bf92f3577b34da6a3ce929d0e0e4736",
  "otelSpanID": "00f067aa0ba902b7",
  "otelServiceName": "notification-svc"
}
```

## Alloy River config — trace_correlation stage

The pipeline handles both field naming conventions:

```river
loki.process "trace_correlation" {
  stage.json {
    expressions = {
      dotnet_trace = "TraceId",
      dotnet_span  = "SpanId",
      dotnet_level = "Level",
      python_trace = "otelTraceID",
      python_span  = "otelSpanID",
      python_level = "levelname",
    }
  }

  // Coalesce: use .NET field if present, else Python field
  stage.template {
    source   = "trace_id"
    template = "{{ if .dotnet_trace }}{{ .dotnet_trace }}{{ else }}{{ .python_trace }}{{ end }}"
  }
  stage.template {
    source   = "span_id"
    template = "{{ if .dotnet_span }}{{ .dotnet_span }}{{ else }}{{ .python_span }}{{ end }}"
  }
  stage.template {
    source   = "level"
    template = "{{ if .dotnet_level }}{{ .dotnet_level }}{{ else }}{{ .python_level }}{{ end }}"
  }

  // level as a stream label (low cardinality — INFO/WARN/ERROR/DEBUG)
  stage.labels {
    values = { level = "" }
  }

  // trace_id/span_id as structured metadata (high cardinality — not stream labels)
  stage.structured_metadata {
    values = {
      trace_id = "trace_id",
      span_id  = "span_id",
    }
  }

  forward_to = [loki.write.cloud.receiver]
}
```

### Why structured metadata, not stream labels

Loki stream labels must be low-cardinality (namespace, pod, container, app, level). `trace_id` has
the same cardinality as the number of traces — millions per day. Using it as a stream label would:

- Fragment the log stream into billions of per-trace streams
- Destroy Loki's compression efficiency
- Break chunk creation (each chunk would contain one log line)

Structured metadata is indexed differently — it is queryable with `{trace_id="<id>"}` but does not
create new streams.

## Grafana configuration for "Logs for this span"

In the Jaeger/Tempo datasource settings:

```yaml
tracesToLogsV2:
  datasourceUid: loki
  filterByTraceID: true
  filterBySpanID: false
  customQuery: false
  tags:
    - key: k8s.pod.name
      value: pod
```

When viewing a trace in Grafana, clicking "Logs for this span" runs:

```logql
{namespace="otel-lab"} | trace_id = "<traceId>"
```

This works because `trace_id` is stored as Loki structured metadata, which supports label-filter
queries.

## Verifying correlation works

```bash
# 1. Make a request that generates a trace
curl -s http://localhost:8080/api/orders -X POST \
  -H "Content-Type: application/json" \
  -d '{"projectId":1,"description":"test","amount":100}'

# 2. Find the trace in Jaeger
open http://localhost:16686
# Copy the traceId from the URL

# 3. Query Loki directly for that trace
kubectl port-forward svc/loki 3100 -n otel-lab
curl "http://localhost:3100/loki/api/v1/query_range" \
  --data-urlencode 'query={namespace="otel-lab"} | trace_id = "<traceId>"'

# 4. In Grafana: Explore → Jaeger → trace → "Logs for this span" button
```

## Troubleshooting

### trace_id empty in Loki

1. Confirm the application writes JSON to stdout, not plain text:

   ```bash
   kubectl -n otel-lab logs deploy/gateway-api --tail=5
   # Should be JSON, not: "info: Processed order 42"
   ```

2. Check Alloy log pipeline is running:

   ```bash
   kubectl -n monitoring logs daemonset/grafana-k8s-alloy-logs | grep -i loki
   ```

3. Check the `stage.json` field names match. .NET uses `TraceId`; Python uses `otelTraceID`. The
   coalesce template handles both but fails if neither field is present.

4. Verify `structured_metadata` is enabled in your Loki version. This requires Loki 2.9+ with
   `limits_config.allow_structured_metadata: true`.

### Level label missing

Check the Python service `levelname` field exists. If using a custom log formatter that renames this
field, update `python_level` in the `stage.json` expressions to match.

### .NET and Python trace IDs don't match format

Both runtimes produce 32-character lowercase hex trace IDs (W3C TraceContext format). If a service
uses a different format, the Loki query will not find matching logs.
