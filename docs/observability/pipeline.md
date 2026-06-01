# Observability Pipeline

Grafana Alloy is the collector for all signals. The pipeline runs in `alloy-receiver` (a DaemonSet in the `monitoring` namespace, managed by the `grafana/k8s-monitoring` Helm chart).

Two configmaps implement the same logical pipeline for different export destinations:

- `k8s/monitoring/grafana/grafana-cloud/configmap.yaml` — cloud exporters (default)
- `k8s/monitoring/grafana/local/configmap.yaml` — local backends

---

## Pipeline stages

### Stage 1: Receivers

**OTLP receiver** — accepts push from all services:

```river
otelcol.receiver.otlp "default" {
  grpc { endpoint = "0.0.0.0:4317" }
  http { endpoint = "0.0.0.0:4318" }
  output {
    traces  = [otelcol.processor.k8sattributes.default.input]
    metrics = [otelcol.processor.k8sattributes.default.input]
    logs    = [otelcol.processor.k8sattributes.default.input]
  }
}
```

**Faro receiver** — accepts browser RUM from Angular SPA:

```river
faro.receiver "frontend" {
  server {
    listen_address       = "0.0.0.0"
    listen_port          = 12347
    cors_allowed_origins = ["*"]
  }
  output {
    traces = [otelcol.processor.k8sattributes.default.input]
    logs   = [loki.write.cloud.receiver]   // logs bypass OTel pipeline → direct to Loki
  }
}
```

Note: Faro logs go directly to `loki.write` (bypassing the OTel batch processor) because they are already structured for Loki and do not need OTLP processing.

---

### Stage 2: K8s attribute enrichment

```river
otelcol.processor.k8sattributes "default" {
  extract {
    metadata = [
      "k8s.namespace.name",
      "k8s.deployment.name",
      "k8s.pod.name",
      "k8s.node.name",
      "k8s.container.name",
    ]
    label {
      from      = "pod"
      key_regex = "app\\.kubernetes\\.io/.*"
    }
  }
  pod_association {
    source { from = "connection" }   // resolves pod from OTLP connection source IP
  }
}
```

Attributes added to every span and metric point (regardless of which service sent it):

- `k8s.namespace.name`, `k8s.pod.name`, `k8s.deployment.name`, `k8s.node.name`, `k8s.container.name`
- Any pod label matching `app.kubernetes.io/*` (e.g. `app.kubernetes.io/name=gateway-api`)

Requires ClusterRole with `get/list/watch` on `pods` and `nodes`. Defined in `k8s/monitoring/grafana/rbac.yaml`.

---

### Stage 3: Environment label

```river
otelcol.processor.transform "env_label" {
  error_mode = "ignore"
  trace_statements {
    context    = "resource"
    statements = [
      "set(attributes[\"deployment.environment\"], \"signal-forge-dev\") where attributes[\"deployment.environment\"] == nil"
    ]
  }
  // same for metric_statements and log_statements
}
```

Stamps `deployment.environment = signal-forge-dev` as a resource attribute on any signal that doesn't already have it. This enables environment-based filtering in Grafana without requiring every service to set the attribute explicitly.

---

### Stage 4: Health-check filter (traces only)

```river
otelcol.processor.filter "healthz" {
  error_mode = "ignore"
  traces {
    span = [
      "attributes[\"http.route\"] == \"/healthz\"",
      "attributes[\"url.path\"] == \"/healthz\"",
    ]
  }
  output {
    traces = [
      otelcol.connector.spanmetrics.default.input,
      otelcol.processor.tail_sampling.default.input,
    ]
  }
}
```

Drops `/healthz` spans before they reach spanmetrics or the sampler. K8s liveness probes fire every 15 seconds across all pods — without this filter they would dominate trace count and span metrics.

Belt-and-suspenders design: the SDK-level filter (`opts.Filter = ctx => ctx.Request.Path != "/healthz"`) prevents spans from being created at all for .NET services. The collector filter catches anything that slips through from Python or future services.

---

### Stage 5: Span metrics connector

```river
otelcol.connector.spanmetrics "default" {
  dimension { name = "http.method" }
  dimension { name = "http.route" }
  dimension { name = "http.status_code" }
  dimension { name = "rpc.method" }
  dimension { name = "rpc.service" }
  dimension { name = "messaging.operation" }
  histogram {
    explicit {
      buckets = ["5ms", "10ms", "25ms", "50ms", "100ms", "250ms", "500ms", "1s", "2.5s", "5s", "10s"]
    }
  }
  exemplars { enabled = true }
  output {
    metrics = [otelcol.processor.batch.default.input]
  }
}
```

Generates RED (Rate / Error / Duration) metrics from **all** spans, before tail sampling. Produces:

- `traces_spanmetrics_calls_total` — rate counter
- `traces_spanmetrics_latency_bucket` — latency histogram

See [Tail-Based Sampling](sampling.md) for why this runs before sampling.

---

### Stage 6: Tail sampling

```river
otelcol.processor.tail_sampling "default" {
  decision_wait               = "10s"
  num_traces                  = 1000
  expected_new_traces_per_sec = 100

  policy { name = "errors-always";  type = "status_code";    status_code   { status_codes = ["ERROR"] } }
  policy { name = "slow-requests";  type = "latency";        latency       { threshold_ms = 2000 } }
  policy { name = "probabilistic-rest"; type = "probabilistic"; probabilistic { sampling_percentage = 25 } }
  output { traces = [otelcol.processor.batch.default.input] }
}
```

See [Tail-Based Sampling](sampling.md) for policy rationale and validation approach.

---

### Stage 7: Batch processor

```river
otelcol.processor.batch "default" {
  timeout         = "5s"
  send_batch_size = 1024
}
```

Buffers up to 1024 signals or 5 seconds, whichever comes first, before flushing to exporters. Reduces exporter connections and amortises network round trips.

---

### Stage 8: Exporters

#### Cloud mode (`grafana-cloud/configmap.yaml`)

```river
// Traces → Grafana Cloud Tempo (OTLP gRPC, :443)
otelcol.exporter.otlp "grafana_cloud_traces" {
  client {
    endpoint = env("GRAFANA_CLOUD_TEMPO_ENDPOINT")   // host:443, no https://
    auth     = otelcol.auth.basic.grafana_cloud_tempo.handler
  }
}

// Metrics → Grafana Cloud Mimir (OTLP HTTP)
otelcol.exporter.otlphttp "grafana_cloud_metrics" {
  client {
    endpoint = env("GRAFANA_CLOUD_MIMIR_ENDPOINT")   // https://host/api/v1/otlp
    auth     = otelcol.auth.basic.grafana_cloud_mimir.handler
  }
}

// OTLP logs → Grafana Cloud Loki (OTLP HTTP)
otelcol.exporter.otlphttp "grafana_cloud_logs" {
  client {
    endpoint = env("GRAFANA_CLOUD_LOKI_ENDPOINT")    // https://host/loki/api/v1/push
    auth     = otelcol.auth.basic.grafana_cloud_loki.handler
  }
}

// Separate auth blocks — each signal type has its own instance ID
otelcol.auth.basic "grafana_cloud_tempo"  { username = env("GRAFANA_CLOUD_TEMPO_USER");  password = env("GRAFANA_CLOUD_API_KEY") }
otelcol.auth.basic "grafana_cloud_mimir" { username = env("GRAFANA_CLOUD_MIMIR_USER");  password = env("GRAFANA_CLOUD_API_KEY") }
otelcol.auth.basic "grafana_cloud_loki"  { username = env("GRAFANA_CLOUD_LOKI_USER");   password = env("GRAFANA_CLOUD_API_KEY") }
```

#### Local mode (`local/configmap.yaml`)

```river
// Traces → Jaeger (OTLP gRPC, insecure)
otelcol.exporter.otlp "jaeger_local" {
  client {
    endpoint = "jaeger.otel-lab.svc.cluster.local:4317"
    tls { insecure = true }
  }
}

// Metrics → Prometheus (remote-write)
otelcol.exporter.prometheus "local" {
  forward_to = [prometheus.remote_write.local.receiver]
}
prometheus.remote_write "local" {
  endpoint {
    url = "http://prometheus.otel-lab.svc.cluster.local:9090/api/v1/write"
  }
}
```

---

### Log tailing pipeline (both modes)

This pipeline runs independently from the OTLP pipeline. It tails pod stdout from the `otel-lab` namespace.

```river
discovery.kubernetes "pods" {
  role = "pod"
  namespaces { names = ["otel-lab"] }
}

discovery.relabel "pod_logs" {
  targets = discovery.kubernetes.pods.targets
  rule { source_labels = ["__meta_kubernetes_namespace"]; target_label = "namespace" }
  rule { source_labels = ["__meta_kubernetes_pod_name"];  target_label = "pod" }
  rule { source_labels = ["__meta_kubernetes_pod_container_name"]; target_label = "container" }
  rule { source_labels = ["__meta_kubernetes_pod_label_app"];      target_label = "app" }
}

loki.source.kubernetes "pod_logs" {
  targets    = discovery.relabel.pod_logs.output
  forward_to = [loki.process.trace_correlation.receiver]
}
```

The `trace_correlation` processor extracts trace IDs from JSON log lines of both .NET and Python services:

```river
loki.process "trace_correlation" {
  stage.json {
    expressions = {
      dotnet_trace = "TraceId",      // .NET field name
      dotnet_span  = "SpanId",
      dotnet_level = "Level",
      python_trace = "otelTraceID",  // Python field name
      python_span  = "otelSpanID",
      python_level = "levelname",
    }
  }
  stage.template { source = "trace_id"; template = "{{ if .dotnet_trace }}{{ .dotnet_trace }}{{ else }}{{ .python_trace }}{{ end }}" }
  stage.template { source = "span_id";  template = "{{ if .dotnet_span }}{{ .dotnet_span }}{{ else }}{{ .python_span }}{{ end }}" }
  stage.template { source = "level";    template = "{{ if .dotnet_level }}{{ .dotnet_level }}{{ else }}{{ .python_level }}{{ end }}" }
  stage.labels           { values = { level = "" } }
  stage.structured_metadata { values = { trace_id = "trace_id", span_id = "span_id" } }
  forward_to = [loki.write.cloud.receiver]
}
```

`trace_id` and `span_id` are stored as Loki **structured metadata** (not stream labels) because trace IDs are high-cardinality — using them as stream labels would cause label explosion in Loki.

Grafana's Jaeger datasource `tracesToLogsV2` config queries Loki for `{trace_id="<id>"}` to provide "Logs for this span" in the trace view.

---

## Full pipeline data flow

```
OTLP gRPC :4317 ──┐
OTLP HTTP :4318 ──┤── k8sattributes ── env_label ──┬── traces ── filter ──┬── spanmetrics ── batch ── exporters
Faro HTTP :12347 ─┘   (pod metadata)  (env stamp)  │                      └── tail_sampling ─┘
                                                    ├── metrics ──────────── batch ── exporters
                                                    └── logs ────────────── batch ── exporters

Pod stdout ── loki.source.kubernetes ── trace_correlation ── loki.write ── Loki
```
