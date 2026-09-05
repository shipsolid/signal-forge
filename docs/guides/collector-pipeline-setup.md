---
title: "Guide: Collector & Pipeline Setup"
description: "Step-by-step: stand up a Grafana Alloy + grafana/k8s-monitoring Helm chart pipeline that receives OTLP traces/metrics/logs from your services and exports to Grafana Cloud or a self-hosted backend."
tags: ["ShipSolid", "Signal Forge", "Observability", "Guides"]
updated: 2026-07-30
zettelId: "202607301400-02"
relations:
  - slug: projects/app-signal-forge/guides/README
    kind: related
  - slug: projects/app-signal-forge/observability/pipeline
    kind: depends_on
  - slug: projects/app-signal-forge/deployment/grafana-cloud
    kind: depends_on
  - slug: projects/app-signal-forge/deployment/helm
    kind: depends_on
---

## Guide: Collector & Pipeline Setup

Do this first. Every app-side guide ([[dotnet-instrumentation|.NET]],
[[python-instrumentation|Python]], [[frontend-rum-instrumentation|Frontend]]) assumes there's a live
OTLP endpoint to send signals to. This guide stands that endpoint up.

For the full stage-by-stage pipeline internals this is based on, see
[[pipeline|Observability Pipeline]]. For the credential model, see
[[grafana-cloud|Grafana Cloud Deployment]].

### Step 1 — Choose your backend: Grafana Cloud or self-hosted

These are **mutually exclusive**, not combinable. Pick one:

- **Grafana Cloud** (recommended for a fast start) — Alloy exports to managed Tempo/Mimir/Loki. No
  backend infrastructure to run yourself.
- **Self-hosted** — Alloy exports to your own Jaeger/Prometheus/Loki/Grafana. Gives you tail-based
  sampling and a span-metrics-before-sampling pipeline (see Step 10) that the Grafana Cloud chart
  path doesn't have.

There is no dual-export mode. Decide once; every app service's `OTEL_EXPORTER_OTLP_ENDPOINT` always
points at the same in-cluster Alloy receiver regardless of which backend Alloy itself exports to —
switching backends later is a values-file change, not an app-code change.

### Step 2 — Install the `grafana/k8s-monitoring` Helm chart

```bash
helm repo add grafana https://grafana.github.io/helm-charts
helm repo update
```

Pin a chart version explicitly (this project pins `3.8.4`) and keep the version pinned in your own
config source of truth, not just the `helm install` command line — you'll re-run this on every
deploy.

Maintain two values files, selected by your backend choice:

- `values-cloud.yaml` — for Grafana Cloud
- `values-local.yaml` — for self-hosted backends

```bash
helm upgrade --install grafana-k8s grafana/k8s-monitoring \
  --version 3.8.4 \
  --namespace monitoring --create-namespace \
  -f values-cloud.yaml   # or values-local.yaml
```

### Step 3 — Configure the OTLP receiver

In your values file, enable `applicationObservability` with an OTLP receiver on both transports —
this is the **single ingestion point** for every backend service's traces, metrics, and logs:

```yaml
applicationObservability:
  enabled: true
  receivers:
    otlp:
      grpc:
        enabled: true
        port: 4317
      http:
        enabled: true
        port: 4318
```

Every app service will point `OTEL_EXPORTER_OTLP_ENDPOINT` at:

```
http://<helm-release-name>-alloy-receiver.<monitoring-namespace>.svc.cluster.local:4317
```

e.g. `http://grafana-k8s-alloy-receiver.monitoring.svc.cluster.local:4317` for a release named
`grafana-k8s` in namespace `monitoring`. If the chart isn't installed yet (or the release/namespace
names don't match), apps will send OTLP to a DNS name nothing answers on — telemetry silently drops
with no app-side error. Verify this endpoint resolves and is listening before debugging anything
upstream of it.

### Step 4 — Grafana Cloud destinations (skip if self-hosting)

Grafana Cloud issues a **separate numeric instance ID per signal type** — Tempo, Mimir, and Loki
each have their own — authenticated with one shared **access-policy token**. Two gotchas that cost
real debugging time in this project:

1. **Token type matters.** Grafana Cloud has two token shapes:

   - `glsa_...` — an **organization service-account token**. Authenticates against the Grafana
     frontend (`<org>.grafana.net`). **Rejected with HTTP 401 by the Tempo/Mimir/Loki data-plane
     ingestion endpoints.**
   - `glc_...` — an **access-policy token** scoped to `metrics:write logs:write traces:write`. This
     is the one Alloy needs for pushing data. Create it under your Grafana Cloud org's Access
     Policies, not under Service Accounts.

2. **Endpoint URL format needs adjustment** before Alloy will accept it — the raw URL shown in the
   Grafana Cloud portal is not what the exporter config wants:

   | Signal                                    | Raw portal URL                                | Required format                                                       |
   | ----------------------------------------- | --------------------------------------------- | --------------------------------------------------------------------- |
   | Traces (OTLP gRPC → Tempo)                | `https://tempo-prod-NN-....grafana.net`       | Strip `https://`, append `:443` — `tempo-prod-NN-....grafana.net:443` |
   | Metrics (Prometheus remote_write → Mimir) | `https://prometheus-....grafana.net/api/prom` | Append `/push` — `.../api/prom/push`                                  |
   | Logs (Loki push)                          | `https://logs-prod-....grafana.net`           | Append `/loki/api/v1/push`                                            |

   Metrics go over **Prometheus remote_write, not OTLP HTTP** — the chart's destination
   `type: prometheus` converts OTLP metrics to Prometheus format internally before shipping, giving
   one ingestion path for both app-emitted and infra-scraped metrics.

Values file shape (`values-cloud.yaml`):

```yaml
destinations:
  - name: grafana-cloud-metrics
    type: prometheus
    url: https://prometheus-....grafana.net/api/prom/push
    auth:
      type: basic
      username: "<Mimir instance ID>"
      password:
        secretRef: { name: grafana-cloud-secrets, key: GRAFANA_CLOUD_API_KEY }
  - name: grafana-cloud-logs
    type: loki
    url: https://logs-....grafana.net/loki/api/v1/push
    auth:
      type: basic
      username: "<Loki instance ID>"
      password:
        secretRef: { name: grafana-cloud-secrets, key: GRAFANA_CLOUD_API_KEY }
  - name: grafana-cloud-traces
    type: otlp
    url: tempo-....grafana.net:443
    auth:
      type: basic
      username: "<Tempo instance ID>"
      password:
        secretRef: { name: grafana-cloud-secrets, key: GRAFANA_CLOUD_API_KEY }
```

### Step 5 — Store credentials as a Kubernetes Secret, with graceful degradation

```bash
kubectl create secret generic grafana-cloud-secrets -n monitoring \
  --from-literal=GRAFANA_CLOUD_API_KEY=glc_xxxxx \
  --from-literal=GRAFANA_CLOUD_TEMPO_USER=<your Tempo instance ID> \
  --from-literal=GRAFANA_CLOUD_MIMIR_USER=<your Mimir instance ID> \
  --from-literal=GRAFANA_CLOUD_LOKI_USER=<your Loki instance ID>
```

Reference each key via `secretKeyRef` with `optional: true` in the values file rather than requiring
the Secret to exist:

```yaml
auth:
  password:
    secretRef:
      name: grafana-cloud-secrets
      key: GRAFANA_CLOUD_API_KEY
      optional: true
```

This means Alloy pods start even if the Secret is missing or partially filled — a missing credential
shows up as an export error in the Alloy logs (`endpoint is empty`, `401`), not a crashed pod. Don't
skip this: it's the difference between "traces aren't showing up, let me check the logs" and "the
whole collector is in CrashLoopBackOff because someone hasn't rotated a secret yet."

Recommend storing the source-of-truth credentials in a proper secrets manager (Azure Key Vault, AWS
Secrets Manager, HashiCorp Vault) and scripting the fetch-into-Secret step — hand-editing a
`kubectl create secret` command with real tokens doesn't scale past one engineer.

### Step 6 — Log-to-trace correlation

Do **not** export logs via OTLP from your application services. Set this on every app:

```
OTEL_LOGS_EXPORTER=none
```

Instead, have applications write structured JSON to stdout, and let a node-level log-tailing agent
(the chart's `podLogs` feature, or your own `loki.source.kubernetes` pipeline) pick it up. This is a
deliberate choice, not a limitation — see
[[adr-log-tailing-not-otlp-export|ADR-001: Log tailing, not OTLP export]]. Reasons: log volume
spikes don't consume application process resources under an OTLP log exporter; and shipping logs via
a node-level agent is closer to how you'd operate this at real production scale.

The log-tailing stage must extract the trace ID and span ID from each JSON log line and promote them
to Loki as **structured metadata**, not stream labels:

```river
stage.json {
  expressions = {
    trace_id = "TraceId",       // whatever field name your logging integration emits
    span_id  = "SpanId",
  }
}
stage.structured_metadata {
  values = { trace_id = "trace_id", span_id = "span_id" }
}
```

**Why structured metadata and not a stream label:** Loki stream labels must stay low-cardinality
(namespace, pod, level). A trace ID has the cardinality of your total trace volume — using it as a
label fragments the log stream into effectively one stream per log line and destroys Loki's
compression. Structured metadata is queryable (`{trace_id="<id>"}`) without creating new streams.

If you're instrumenting more than one language, your logging libraries will very likely use
different field names for the same concept (this project's .NET services emit `TraceId`/`SpanId`;
its Python service emits `otelTraceID`/`otelSpanID`) — either normalize field names across languages
in your logging setup, or coalesce both names in the extraction stage. See
[[correlation|Log-to-Trace Correlation]] for a worked example of the coalesce approach.

### Step 7 — Keep deployment_environment consistent across signals

This is not optional if you want Grafana Cloud's Application Observability views (service list,
service graph, per-environment filtering) to work correctly — they all key off the
`deployment_environment` label/attribute, and it has to agree across traces, metrics, and logs for a
service to show up correctly filtered, rather than fragmenting into multiple apparent "environments"
or silently vanishing from a filtered view.

**Pick one canonical value at your deploy-config level** (this project uses a single
`monitoring.deployment_environment` value in `conf.yml`) and template it into every place below —
never let a service's env var and the collector's destination config be edited independently.

The reason this needs active attention at all: traces and metrics leave your app carrying
`deployment.environment` as a proper OTel resource attribute (Step 4/5 of each
[[projects/app-signal-forge/guides/readme|app guide]]'s env vars), but logs never do —
`OTEL_LOGS_EXPORTER=none` (Step 6) means logs never pass through the OTel SDK's resource pipeline in
the first place. So the collector ends up using a **different mechanism per signal type**, and if
you're on the Grafana Cloud destinations (`values-cloud.yaml.tmpl`), those mechanisms don't behave
the same way:

| Signal  | Mechanism on the Grafana Cloud destination                                                                                                                                                                                                          | Behavior                                                                                                                                                                                                          |
| ------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Metrics | `extraLabels: { deployment_environment: ... }` on the `grafana-cloud-metrics` destination                                                                                                                                                           | Unconditional — stamps this value on every metric series sent to Mimir, on top of whatever `resourceToTelemetryConversion: true` already converted from the app's own `deployment.environment` resource attribute |
| Logs    | `extraLabels: { deployment_environment: ... }` on the `grafana-cloud-logs` destination                                                                                                                                                              | Unconditional — same idea, stamps every log stream sent to Loki                                                                                                                                                   |
| Traces  | **`extraLabels` is a documented no-op on `otlp`-type destinations in this chart version** — instead, an explicit OTTL statement on the destination's `processors.transform.traces.resource`: `set(attributes["deployment_environment"], "<value>")` | Unconditional, **and only sets the underscore key** — `deployment.environment` (the semconv dot form) is never written here                                                                                       |

Three things your team needs to adhere to, specifically because of that table:

1. **Don't try to configure `extraLabels` on the traces destination and expect it to work.** It's
   silently accepted by the chart's values schema but has no effect on `otlp`-type destinations. Use
   the `processors.transform` OTTL block shown above instead.
2. **Standardize on `deployment_environment` (underscore), not `deployment.environment` (dot), for
   any dashboard, alert rule, or saved Explore query that filters or groups by environment —
   including trace queries.** Metrics and logs get both forms in play (app-derived dot form,
   converted or stamped to underscore); traces in Grafana Cloud mode only ever get the underscore
   form written by the collector. A query built against the dot form will work against metrics/logs
   (where the SDK's own resource attribute is still present pre-conversion in some query paths) but
   silently return nothing for traces.
3. **Treat the collector's `extraLabels`/OTTL value as authoritative, not the app's.** All three
   Grafana Cloud destinations apply their stamp unconditionally — there's no `where ... == nil`
   guard anywhere in this chart's destination config, unlike the hand-authored self-hosted pipeline
   below. If an app's `OTEL_RESOURCE_ATTRIBUTES` ever drifts from the destination config's value
   (e.g. someone updates one without the other), the collector's value wins for every signal that
   reaches Grafana Cloud — so keep both templated from the same source rather than relying on this
   override behavior to paper over drift.

If you're self-hosting instead (`values-local.yaml`/a hand-authored River pipeline), the equivalent
mechanism is a `transform` processor applied to the OTLP pipeline (traces and metrics) plus a
`loki.write` `external_labels` block (logs) — and it's worth noting this project's own self-hosted
pipeline behaves slightly differently from its Grafana Cloud one on this exact point: the OTLP
`transform` processor there uses `where attributes["deployment.environment"] == nil`, i.e. it
_defers to the app's value if present_ rather than overwriting it, and it sets **both** the dot and
underscore forms on trace spans (since Jaeger performs no dot-to-underscore sanitization the way the
Prometheus exporter does for metrics). If you maintain both a self-hosted and a Grafana Cloud path,
decide deliberately which override behavior you want and make both paths match — don't let it be an
accident of which destination type happened to support `extraLabels`.

```river
// Self-hosted equivalent — conditional, defers to the app's own resource attribute if set
otelcol.processor.transform "env_label" {
  error_mode = "ignore"
  trace_statements {
    context    = "resource"
    statements = [
      "set(attributes[\"deployment.environment\"], \"${DEPLOYMENT_ENVIRONMENT}\") where attributes[\"deployment.environment\"] == nil",
      "set(attributes[\"deployment_environment\"], \"${DEPLOYMENT_ENVIRONMENT}\") where attributes[\"deployment_environment\"] == nil",
    ]
  }
  metric_statements {
    context    = "resource"
    statements = ["set(attributes[\"deployment.environment\"], \"${DEPLOYMENT_ENVIRONMENT}\") where attributes[\"deployment.environment\"] == nil"]
  }
}

// loki.write external_labels — logs never carry the OTel resource attribute at all,
// so this is the only mechanism that reaches them, and it's unconditional by nature.
loki.write "default" {
  endpoint { url = "http://loki.example.svc.cluster.local:3100/loki/api/v1/push" }
  external_labels = {
    deployment_environment = "${DEPLOYMENT_ENVIRONMENT}",
  }
}
```

### Step 8 — Exemplars

Exemplars link a histogram bucket observation to the specific trace that produced it — the "click a
metric spike, land on the trace" workflow. Four things all have to be true simultaneously:

1. **On every app service**, set the env var `OTEL_METRICS_EXEMPLAR_FILTER=trace_based`. Prefer the
   env var over an SDK-level call (e.g. .NET's `AddExemplarFilter(ExemplarFilterType.TraceBased)`) —
   in the OTel .NET SDK this API sits behind an experimental flag requiring
   `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` and unstable-package imports. The env var achieves
   the same effect without a compile-time dependency on an unstable API, and it's portable across
   languages.
2. If you're generating span-metrics (Step 11, self-hosted only), set `exemplars { enabled = true }`
   on the `spanmetrics` connector.
3. If self-hosting Prometheus, start it with `--enable-feature=exemplar-storage` — without this flag
   exemplars are silently accepted and then dropped, with no error anywhere.
4. In each Grafana dashboard panel querying a histogram, enable the "Exemplars" toggle and add a
   "Data link" pointing at your trace backend (Tempo/Jaeger datasource).

Grafana Cloud Mimir stores exemplars natively over the OTLP path with no additional collector
configuration once (1) is set.

### Step 9 — Frontend RUM ingestion is a separate concern from OTLP

This is the single most commonly conflated piece of this pipeline, including in this project's own
earlier documentation — be precise about it.

Browser RUM data (from [[frontend-rum-instrumentation|Grafana Faro]]) does **not** flow through the
`applicationObservability` OTLP receiver configured in Step 3. It needs its own path:

- **If using Grafana Cloud**: the browser sends RUM data **directly** to Grafana Cloud's managed
  Faro Collector endpoint (a URL you get from registering a Faro app in the Grafana Cloud portal).
  This traffic never touches your cluster or your Alloy instance at all — it's a browser → internet
  call. The only thing you need in-cluster is an egress `NetworkPolicy` on your frontend pods
  allowing outbound HTTPS.
- **If self-hosting**: you need a _separate_ Alloy component with a `faro.receiver` block on its own
  port (this project uses `12347`), distinct from the OTLP receiver on 4317/4318:

  ```river
  faro.receiver "frontend" {
    server {
      listen_address       = "0.0.0.0"
      listen_port          = 12347
      cors_allowed_origins = ["*"]
    }
    output {
      traces = [otelcol.processor.batch.default.input]
      logs   = [loki.write.default.receiver]   // Faro logs are already Loki-shaped; skip the OTel batch stage for them
    }
  }
  ```

  This can live on the same Alloy instance as your OTLP receiver (a different `faro.receiver` block,
  same config file) or a wholly separate one — but it is never the same receiver component as the
  OTLP one, and the `grafana/k8s-monitoring` chart's `applicationObservability` feature does not
  expose a Faro receiver option at all as of chart `3.8.4`. If you need in-cluster Faro ingestion on
  that chart, you'll be running a hand-authored Alloy config alongside it, not configuring the chart
  for it.

### Step 10 — Network policy

Whatever your CNI, allow egress from application pods to the Alloy receiver's pods/service on
4317/4318:

```yaml
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata:
  name: allow-app-to-alloy-receiver
spec:
  podSelector: { matchLabels: { tier: app } }
  policyTypes: [Egress]
  egress:
    - to:
        - namespaceSelector: { matchLabels: { kubernetes.io/metadata.name: monitoring } }
          podSelector: { matchLabels: { app.kubernetes.io/name: alloy-receiver } }
      ports:
        - { protocol: TCP, port: 4317 }
        - { protocol: TCP, port: 4318 }
```

If using Grafana Cloud RUM (Step 9), also allow frontend pods broad HTTPS egress (443) to the
internet — Faro's collector endpoint isn't a fixed, allowlistable IP range.

### Step 11 — Known gap: no sampling in the chart-only path

The `grafana/k8s-monitoring` chart's `applicationObservability` feature (as of `3.8.4`) has **no
tail-sampling or span-metrics-before-sampling processor** anywhere in its values schema. If you rely
on the chart alone, every trace your services emit ships to your backend at 100% volume — fine at
low traffic, a real cost and cardinality concern at scale.

If you need tail-based sampling (keep 100% of errors and slow requests, sample the rest at, say,
25%), you need a hand-authored Alloy River pipeline instead of (or as an addition alongside) the
chart's fixed pipeline:

```river
otelcol.connector.spanmetrics "default" {
  dimension { name = "http.method" }
  dimension { name = "http.route" }
  dimension { name = "http.status_code" }
  histogram { explicit { buckets = ["5ms","10ms","25ms","50ms","100ms","250ms","500ms","1s","2.5s","5s","10s"] } }
  exemplars { enabled = true }
  output { metrics = [otelcol.processor.batch.default.input] }
}

otelcol.processor.tail_sampling "default" {
  decision_wait               = "10s"
  num_traces                  = 1000
  expected_new_traces_per_sec = 100

  policy { name = "errors-always"; type = "status_code"; status_code { status_codes = ["ERROR"] } }
  policy { name = "slow-requests"; type = "latency"; latency { threshold_ms = 2000 } }
  policy { name = "probabilistic-rest"; type = "probabilistic"; probabilistic { sampling_percentage = 25 } }

  output { traces = [otelcol.processor.batch.default.input] }
}
```

Run `spanmetrics` **before** `tail_sampling` in the pipeline — if it ran after, your RED metrics
would only reflect the sampled fraction of traffic, silently understating your real request volume.
Set `decision_wait` to at least your p99 trace duration (including any async legs, like a message
queue consumer) so a trace isn't evaluated for sampling before all its spans have arrived.

### Step 12 — Verify

1. Confirm the receiver is listening:

   ```bash
   kubectl -n monitoring get pods -l app.kubernetes.io/name=alloy-receiver
   kubectl -n monitoring logs daemonset/<release>-alloy-receiver --tail=50
   ```

2. Confirm an app can reach it:

   ```bash
   kubectl -n <app-namespace> exec deploy/<any-app> -- env | grep OTEL_EXPORTER_OTLP_ENDPOINT
   ```

3. Generate a request against any instrumented endpoint and check for successful export in the Alloy
   logs:

   ```bash
   kubectl -n monitoring logs daemonset/<release>-alloy-receiver --tail=100 | grep -Ei "grafana_cloud|export|error"
   ```

   Expect lines like `msg="successfully exported"`. `endpoint is empty` means the Secret from Step 5
   isn't populated or wasn't picked up yet — restart the Alloy pods after fixing the Secret.

4. In Grafana: Explore → your trace datasource → search by `service.name` for the app you just
   exercised. Confirm the trace, its metrics (via `traces_spanmetrics_calls_total` if you did Step
   11, or your standard HTTP server metrics otherwise), and its logs (query `{trace_id="<traceId>"}`
   in Loki) all correlate to the same request.
5. If using Grafana Cloud, open Application Observability → Service Inventory and confirm the
   service appears with the expected environment filter applied — this is the concrete, visible
   proof that Step 7's `deployment_environment` consistency actually landed correctly across all
   three signals. A service that appears under the wrong environment, appears twice under two
   different environment values, or has some signals present and others missing from a filtered view
   is a `deployment_environment` mismatch, not a connectivity problem — go back to Step 7 rather
   than re-checking credentials or network policy.

Once this is live, move on to instrumenting your services: [[dotnet-instrumentation|.NET]] ·
[[python-instrumentation|Python]] · [[frontend-rum-instrumentation|Frontend]].
