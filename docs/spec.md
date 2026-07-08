# spec.md — SignalForge: OTel Microservices Validation Lab

## 1. Purpose

A multi-service application (.NET, Python, Angular) deployed on local k3d, instrumented end-to-end
with OpenTelemetry and collected via **Grafana Alloy**. The goal is to validate every
instrumentation pattern — traces, metrics, logs, exemplars, span metrics, cross-language
propagation, sync + async communication, and frontend RUM — before rolling them into production
workloads.

Dual-mode export: local observability stack for offline dev, and remote-write to Grafana Cloud for
production-parity validation.

---

## 2. Architecture Overview

```
┌──────────────────────────────────────────────────────────────────────────────┐
│  k3d cluster: otel-lab                                                       │
│                                                                              │
│  ┌────────────────┐         ┌──────────────────┐        ┌────────────────┐  │
│  │  Angular SPA   │──HTTP──▶│  API Gateway/BFF │──gRPC─▶│ Order Service  │  │
│  │  (Faro RUM)    │         │  (.NET 8)        │        │ (.NET 8)       │  │
│  │  nginx:80      │         │  port:5000       │        │ port:5001      │  │
│  └────────────────┘         │  owns: MySQL 8   │        │ owns: Postgres │  │
│                             └──────┬───────────┘        └──────┬─────────┘  │
│                                    │                           │             │
│                                    │ HTTP (fan-out)            │ publish     │
│                                    ▼                           ▼             │
│                             ┌──────────────┐          ┌──────────────┐      │
│                             │ Notification │◀─consume─│  RabbitMQ    │      │
│                             │ Service      │          │  (broker)    │      │
│                             │ (Python/     │          └──────────────┘      │
│                             │  FastAPI)    │                                 │
│                             │ owns: Redis  │                                 │
│                             └──────────────┘                                 │
│                                                                              │
│  └──────────────────────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────────────────────────┐
│  k3d cluster: monitoring  (grafana/k8s-monitoring v3.8.4 Helm chart)         │
│                                                                              │
│  ┌─────────────────────────────────────────────────────────────────────┐     │
│  │  alloy-receiver (DaemonSet)                                          │     │
│  │  ┌─────────────┐   ┌──────────────┐   ┌──────────────────────────┐  │     │
│  │  │ OTLP        │   │ k8s attrs    │   │ Exporters:               │  │     │
│  │  │ Receiver    │──▶│ enrichment   │──▶│  ├─ Jaeger (local)       │  │     │
│  │  │ :4317 gRPC  │   │ + tail       │   │  ├─ Prometheus (local)   │  │     │
│  │  │ :4318 HTTP  │   │   sampling   │   │  ├─ Grafana Cloud Traces │  │     │
│  │  │ :12347 Faro │   └──────────────┘   │  ├─ Grafana Cloud Metrics│  │     │
│  │  └─────────────┘                      │  └─ Grafana Cloud Logs   │  │     │
│  │                   ┌──────────────┐    └──────────────────────────┘  │     │
│  │                   │ spanmetrics  │──▶ (RED metrics from traces)      │     │
│  │                   │ connector    │                                   │     │
│  │                   └──────────────┘                                   │     │
│  └─────────────────────────────────────────────────────────────────────┘     │
│                                                                              │
│  ┌──────────────────────────┐   ┌──────────────────────────────────────┐     │
│  │  alloy-logs (DaemonSet)  │   │  alloy-metrics (StatefulSet)         │     │
│  │  loki.source.kubernetes  │   │  kubelet, cAdvisor, node-exporter,   │     │
│  │  + trace_correlation     │   │  kube-state-metrics → Prometheus     │     │
│  │  → Loki                  │   └──────────────────────────────────────┘     │
│  └──────────────────────────┘                                                │
│                                                                              │
└──────────────────────────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────────────────────────┐
│  k3d cluster: otel-lab  (local observability backends)                       │
│                                                                              │
│  ┌──────────┐  ┌────────────┐  ┌──────┐  ┌─────────┐                       │
│  │  Jaeger  │  │ Prometheus │  │ Loki │  │ Grafana │                       │
│  │  :16686  │  │  :9090     │  │ :3100│  │  :3000  │                       │
│  └──────────┘  └────────────┘  └──────┘  └─────────┘                       │
└──────────────────────────────────────────────────────────────────────────────┘
```

---

## 3. Services

### 3.1 Angular SPA — `otel-frontend`

| Attribute | Value                                                |
| --------- | ---------------------------------------------------- |
| Framework | Angular 17+                                          |
| RUM SDK   | `@grafana/faro-web-sdk`, `@grafana/faro-web-tracing` |
| Hosting   | nginx container serving static build                 |
| Purpose   | Frontend RUM, browser-to-backend trace propagation   |

#### Pages & User Flows

| Page           | Route            | Backend calls                                           | OTel validation target                      |
| -------------- | ---------------- | ------------------------------------------------------- | ------------------------------------------- |
| Dashboard      | `/`              | GET `/api/projects`                                     | Faro → Gateway span linkage                 |
| Project detail | `/projects/:id`  | GET `/api/projects/:id`, GET `/api/projects/:id/orders` | Multi-fetch waterfall in traces             |
| Create order   | `/orders/new`    | POST `/api/orders`                                      | Full click-to-database trace                |
| Notifications  | `/notifications` | GET `/api/notifications`                                | Cross-language trace (Gateway → Python)     |
| Error page     | `/error-test`    | GET `/api/error`                                        | Frontend error capture + backend error span |

#### Faro Configuration

```typescript
initializeFaro({
  url: '<ALLOY_FARO_RECEIVER_URL>',   // or Grafana Cloud Faro endpoint
  app: {
    name: 'otel-frontend',
    version: '1.0.0',
    environment: 'local',
  },
  instrumentations: [
    ...getWebInstrumentations(),
    new TracingInstrumentation({
      instrumentationOptions: {
        propagateTraceHeaderCorsUrls: [/http:\/\/localhost/],
      },
    }),
  ],
});
```

Key signals from Faro: page load timing, route change spans, fetch/XHR spans (propagating
`traceparent`), JavaScript errors, Web Vitals (LCP, FID, CLS).

---

### 3.2 API Gateway / BFF — `gateway-api` (.NET 8)

| Attribute      | Value                                                        |
| -------------- | ------------------------------------------------------------ |
| Framework      | .NET 8 Minimal API                                           |
| Database       | MySQL 8.0 (owns `Projects` aggregate)                        |
| ORM            | EF Core 8 + Pomelo MySQL                                     |
| Comms outbound | gRPC → Order Service, HTTP → Notification Service            |
| Role           | Receives all frontend calls, fans out to downstream services |

#### Endpoints

| Method | Route                       | Downstream call                        | OTel target                                  |
| ------ | --------------------------- | -------------------------------------- | -------------------------------------------- |
| GET    | `/api/projects`             | — (local DB)                           | EF Core + MySQL spans                        |
| GET    | `/api/projects/{id}`        | — (local DB)                           | Span attributes (project.id)                 |
| POST   | `/api/projects`             | — (local DB)                           | Write span, transaction trace                |
| DELETE | `/api/projects/{id}`        | — (local DB)                           | Cascade delete, error scenario               |
| POST   | `/api/orders`               | gRPC → OrderService.CreateOrder        | gRPC client span propagation                 |
| GET    | `/api/projects/{id}/orders` | gRPC → OrderService.GetOrdersByProject | gRPC server-streaming span                   |
| GET    | `/api/notifications`        | HTTP → Notification Service            | HTTP client span, cross-language propagation |
| GET    | `/api/slow`                 | — (artificial 2-5 s delay)             | Latency histogram, exemplar validation       |
| GET    | `/api/error`                | — (always throws)                      | Error span, exception event recording        |
| GET    | `/healthz`                  | —                                      | Health-check exclusion in Alloy              |

#### Domain Model

```csharp
public class Project
{
    public int Id { get; set; }
    public string Name { get; set; }
    public string Owner { get; set; }
    public DateTime CreatedAt { get; set; }
}
```

#### OTel Packages

```xml
<PackageReference Include="OpenTelemetry.Extensions.Hosting" />
<PackageReference Include="OpenTelemetry.Instrumentation.AspNetCore" />
<PackageReference Include="OpenTelemetry.Instrumentation.Http" />
<PackageReference Include="OpenTelemetry.Instrumentation.GrpcNetClient" />
<PackageReference Include="OpenTelemetry.Instrumentation.EntityFrameworkCore" />
<PackageReference Include="OpenTelemetry.Instrumentation.MySqlData" />
<PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" />
```

#### Custom Instrumentation

| Signal | Name                          | Type          | Description                                          |
| ------ | ----------------------------- | ------------- | ---------------------------------------------------- |
| Trace  | `gateway.fanout`              | Span          | Wraps parallel downstream calls                      |
| Metric | `gateway.requests.inflight`   | UpDownCounter | Concurrent request gauge                             |
| Metric | `gateway.downstream.duration` | Histogram     | Per-downstream-service call latency (with exemplars) |
| Log    | structured `ILogger`          | Log record    | Correlated via TraceId/SpanId                        |

---

### 3.3 Order Service — `order-api` (.NET 8)

| Attribute      | Value                                                    |
| -------------- | -------------------------------------------------------- |
| Framework      | .NET 8 (gRPC server + minimal API for health)            |
| Database       | PostgreSQL 16                                            |
| ORM            | EF Core 8 + Npgsql                                       |
| Message broker | RabbitMQ (publisher)                                     |
| Role           | Order CRUD, publishes `order.created` events to RabbitMQ |

#### gRPC Service Definition

```protobuf
syntax = "proto3";

package orders;

service OrderService {
  rpc CreateOrder (CreateOrderRequest) returns (CreateOrderResponse);
  rpc GetOrdersByProject (GetOrdersByProjectRequest) returns (stream OrderResponse);
  rpc GetOrder (GetOrderRequest) returns (OrderResponse);
}

message CreateOrderRequest {
  int32 project_id = 1;
  string description = 2;
  double amount = 3;
}

message CreateOrderResponse {
  int32 order_id = 1;
  string status = 2;
}

message GetOrdersByProjectRequest {
  int32 project_id = 1;
}

message GetOrderRequest {
  int32 order_id = 1;
}

message OrderResponse {
  int32 id = 1;
  int32 project_id = 2;
  string description = 3;
  double amount = 4;
  string status = 5;
  string created_at = 6;
}
```

#### Domain Model

```csharp
public class Order
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public string Description { get; set; }
    public decimal Amount { get; set; }
    public string Status { get; set; }  // Created, Processing, Completed, Failed
    public DateTime CreatedAt { get; set; }
}
```

#### RabbitMQ Publishing

On `CreateOrder`, after DB write, publish to exchange `orders` with routing key `order.created`:

```json
{
  "order_id": 42,
  "project_id": 7,
  "description": "Server rack provisioning",
  "amount": 4500.00,
  "created_at": "2026-04-14T10:30:00Z"
}
```

**Trace propagation**: Inject W3C `traceparent` into RabbitMQ message headers using
`OpenTelemetry.Instrumentation.RabbitMQ` (or manual `TextMapPropagator` injection into
`IBasicProperties.Headers`). This is the critical async propagation validation point.

#### OTel Packages

```xml
<PackageReference Include="OpenTelemetry.Extensions.Hosting" />
<PackageReference Include="OpenTelemetry.Instrumentation.AspNetCore" />
<PackageReference Include="OpenTelemetry.Instrumentation.GrpcCore" />
<PackageReference Include="OpenTelemetry.Instrumentation.EntityFrameworkCore" />
<PackageReference Include="Npgsql.OpenTelemetry" />
<PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" />
```

#### Custom Instrumentation

| Signal | Name                         | Type                  | Description                                  |
| ------ | ---------------------------- | --------------------- | -------------------------------------------- |
| Trace  | `order.create`               | Span                  | Wraps full create flow (DB + publish)        |
| Trace  | `order.publish`              | Span (kind: PRODUCER) | RabbitMQ publish with linked trace context   |
| Metric | `orders.created.total`       | Counter               | Per-project order creation count             |
| Metric | `orders.amount.total`        | Counter (double)      | Running total order value                    |
| Metric | `orders.processing.duration` | Histogram             | Time from create to publish (with exemplars) |

---

### 3.4 Notification Service — `notification-svc` (Python / FastAPI)

| Attribute      | Value                                                                                       |
| -------------- | ------------------------------------------------------------------------------------------- |
| Framework      | Python 3.12, FastAPI + Uvicorn                                                              |
| Database       | Redis 7 (notification state + dedup)                                                        |
| Message broker | RabbitMQ (consumer)                                                                         |
| Role           | Consumes `order.created` events, stores notification records, exposes notification list API |

#### Endpoints

| Method | Route                 | Purpose                              | OTel target                |
| ------ | --------------------- | ------------------------------------ | -------------------------- |
| GET    | `/notifications`      | List recent notifications from Redis | Redis span instrumentation |
| GET    | `/notifications/{id}` | Get single notification              | Span attributes            |
| GET    | `/healthz`            | Health check                         | Health-check exclusion     |

#### RabbitMQ Consumer

Listens on queue `notifications` bound to `orders` exchange with routing key `order.created`.

**Trace propagation**: Extract `traceparent` from message headers using
`opentelemetry-instrumentation-pika` or manual `TraceContextTextMapPropagator.extract()`. Create a
`CONSUMER` span linked to the producer span. This validates **cross-language async propagation**
(.NET producer → Python consumer).

#### Processing Logic

```python
async def handle_order_created(message: OrderCreatedEvent):
    # 1. Check Redis for dedup (idempotency key: order_id)
    # 2. Format notification
    # 3. Store in Redis (HSET notifications:{id} ...)
    # 4. Mock email send (sleep 100-500ms, custom span)
    # 5. Publish metric
```

#### OTel Packages

```
opentelemetry-api
opentelemetry-sdk
opentelemetry-exporter-otlp-proto-grpc
opentelemetry-instrumentation-fastapi
opentelemetry-instrumentation-redis
opentelemetry-instrumentation-pika
opentelemetry-instrumentation-logging
```

#### Custom Instrumentation

| Signal | Name                                | Type                  | Description                                        |
| ------ | ----------------------------------- | --------------------- | -------------------------------------------------- |
| Trace  | `notification.process`              | Span (kind: CONSUMER) | Full event processing with linked producer context |
| Trace  | `notification.send_email`           | Span                  | Mock email send with artificial latency            |
| Metric | `notifications.processed.total`     | Counter               | Labeled by `status` (success/failed/duplicate)     |
| Metric | `notifications.processing.duration` | Histogram             | End-to-end from consume to completion              |
| Metric | `notifications.email.send.duration` | Histogram             | Mock email latency                                 |

---

## 4. Infrastructure Components

### 4.1 Databases

| Database      | Version          | Owner            | K8s kind                               | Storage |
| ------------- | ---------------- | ---------------- | -------------------------------------- | ------- |
| MySQL 8.0     | `mysql:8.0`      | gateway-api      | StatefulSet + PVC                      | 1Gi     |
| PostgreSQL 16 | `postgres:16`    | order-api        | StatefulSet + PVC                      | 1Gi     |
| Redis 7       | `redis:7-alpine` | notification-svc | Deployment (ephemeral is fine for lab) | —       |

### 4.2 RabbitMQ

| Attribute | Value                                        |
| --------- | -------------------------------------------- |
| Image     | `rabbitmq:3.13-management`                   |
| K8s kind  | StatefulSet + PVC                            |
| Ports     | 5672 (AMQP), 15672 (Management UI, NodePort) |
| Exchange  | `orders` (topic)                             |
| Queue     | `notifications` (bound to `order.created`)   |

Management UI exposed for visual validation of message flow.

### 4.3 Local Observability Backends

| Component           | Image                           | K8s kind                | Exposed port   |
| ------------------- | ------------------------------- | ----------------------- | -------------- |
| Jaeger (all-in-one) | `jaegertracing/all-in-one:1.55` | Deployment              | NodePort 16686 |
| Prometheus          | `prom/prometheus:v2.51.0`       | Deployment + ConfigMap  | NodePort 9090  |
| Loki                | `grafana/loki:3.0.0`            | StatefulSet + ConfigMap | ClusterIP 3100 |
| Grafana             | `grafana/grafana:11.0.0`        | Deployment              | NodePort 3000  |

Grafana is pre-provisioned with datasources: Jaeger (traces), Prometheus (metrics), Loki (logs).

---

## 5. Grafana Alloy Configuration

Alloy is deployed via the `grafana/k8s-monitoring` Helm chart (v3.8.4) in the `monitoring`
namespace. Five specialised roles replace the single general-purpose hand-rolled DaemonSet
(`k8s/alloy/` — kept as reference, not deployed).

The River configuration below represents the **logical pipeline** that `alloy-receiver` implements
(the actual Helm-generated config is equivalent but auto-generated). It is preserved here as a
readable reference for the OTel pipeline design.

### 5.1 Alloy River Config — Full Pipeline

```river
// ============================================================
// RECEIVERS
// ============================================================

// OTLP receiver for application telemetry (traces, metrics, logs)
otelcol.receiver.otlp "default" {
  grpc {
    endpoint = "0.0.0.0:4317"
  }
  http {
    endpoint = "0.0.0.0:4318"
  }
  output {
    traces  = [otelcol.processor.k8sattributes.default.input]
    metrics = [otelcol.processor.k8sattributes.default.input]
    logs    = [otelcol.processor.k8sattributes.default.input]
  }
}

// Faro receiver for frontend RUM data
faro.receiver "frontend" {
  server {
    listen_address = "0.0.0.0"
    listen_port    = 12347
    cors_allowed_origins = ["*"]
  }
  output {
    traces = [otelcol.processor.k8sattributes.default.input]
    logs   = [loki.write.local.receiver]
  }
}


// ============================================================
// PROCESSORS
// ============================================================

// K8s attributes enrichment
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
    source { from = "connection" }
  }
  output {
    traces  = [otelcol.processor.filter.healthz.input]
    metrics = [otelcol.processor.batch.default.input]
    logs    = [otelcol.processor.batch.default.input]
  }
}

// Filter out health-check spans
otelcol.processor.filter "healthz" {
  error_mode = "ignore"
  traces {
    span {
      - 'attributes["http.route"] == "/healthz"'
      - 'attributes["url.path"] == "/healthz"'
    }
  }
  output {
    traces = [
      otelcol.connector.spanmetrics.default.input,
      otelcol.processor.tail_sampling.default.input,
    ]
  }
}

// Tail-based sampling
otelcol.processor.tail_sampling "default" {
  decision_wait               = "10s"
  num_traces                  = 1000
  expected_new_traces_per_sec = 100

  policy {
    name = "errors-always"
    type = "status_code"
    status_code {
      status_codes = ["ERROR"]
    }
  }
  policy {
    name = "slow-requests"
    type = "latency"
    latency {
      threshold_ms = 2000
    }
  }
  policy {
    name = "probabilistic-rest"
    type = "probabilistic"
    probabilistic {
      sampling_percentage = 25
    }
  }

  output {
    traces = [otelcol.processor.batch.default.input]
  }
}

// Span metrics connector — auto-generate RED metrics from traces
otelcol.connector.spanmetrics "default" {
  dimension {
    name = "http.method"
  }
  dimension {
    name = "http.route"
  }
  dimension {
    name = "http.status_code"
  }
  dimension {
    name = "rpc.method"
  }
  dimension {
    name = "rpc.service"
  }
  dimension {
    name = "messaging.operation"
  }
  histogram {
    explicit {
      buckets = ["5ms", "10ms", "25ms", "50ms", "100ms", "250ms", "500ms", "1s", "2.5s", "5s", "10s"]
    }
  }
  exemplars {
    enabled = true
  }
  output {
    metrics = [otelcol.processor.batch.default.input]
  }
}

// Batch processor
otelcol.processor.batch "default" {
  timeout          = "5s"
  send_batch_size  = 1024
  output {
    traces  = [otelcol.exporter.otlp.jaeger_local.input, otelcol.exporter.otlp.grafana_cloud_traces.input]
    metrics = [otelcol.exporter.prometheus.local.input, otelcol.exporter.otlphttp.grafana_cloud_metrics.input]
    logs    = [otelcol.exporter.otlphttp.grafana_cloud_logs.input]
  }
}


// ============================================================
// EXPORTERS — LOCAL
// ============================================================

otelcol.exporter.otlp "jaeger_local" {
  client {
    endpoint = "jaeger.otel-lab.svc.cluster.local:4317"
    tls { insecure = true }
  }
}

otelcol.exporter.prometheus "local" {
  forward_to = [prometheus.remote_write.local.receiver]
}

prometheus.remote_write "local" {
  endpoint {
    url = "http://prometheus.otel-lab.svc.cluster.local:9090/api/v1/write"
  }
}


// ============================================================
// EXPORTERS — GRAFANA CLOUD (toggle via env vars)
// ============================================================

otelcol.exporter.otlp "grafana_cloud_traces" {
  client {
    endpoint = env("GRAFANA_CLOUD_TEMPO_ENDPOINT")
    auth     = otelcol.auth.basic.grafana_cloud.handler
  }
}

otelcol.exporter.otlphttp "grafana_cloud_metrics" {
  client {
    endpoint = env("GRAFANA_CLOUD_MIMIR_ENDPOINT")
    auth     = otelcol.auth.basic.grafana_cloud.handler
  }
}

otelcol.exporter.otlphttp "grafana_cloud_logs" {
  client {
    endpoint = env("GRAFANA_CLOUD_LOKI_ENDPOINT")
    auth     = otelcol.auth.basic.grafana_cloud.handler
  }
}

otelcol.auth.basic "grafana_cloud" {
  username = env("GRAFANA_CLOUD_USER")
  password = env("GRAFANA_CLOUD_API_KEY")
}


// ============================================================
// LOG TAILING — Container stdout → Loki (log-to-trace correlation)
// ============================================================

discovery.kubernetes "pods" {
  role = "pod"
  namespaces {
    names = ["otel-lab"]
  }
}

discovery.relabel "pod_logs" {
  targets = discovery.kubernetes.pods.targets
  rule {
    source_labels = ["__meta_kubernetes_namespace"]
    target_label  = "namespace"
  }
  rule {
    source_labels = ["__meta_kubernetes_pod_name"]
    target_label  = "pod"
  }
  rule {
    source_labels = ["__meta_kubernetes_pod_container_name"]
    target_label  = "container"
  }
  rule {
    source_labels = ["__meta_kubernetes_pod_label_app"]
    target_label  = "app"
  }
}

loki.source.kubernetes "pod_logs" {
  targets    = discovery.relabel.pod_logs.output
  forward_to = [loki.process.trace_correlation.receiver]
}

loki.process "trace_correlation" {
  // Extract traceID from structured JSON logs
  stage.json {
    expressions = {
      trace_id = "TraceId",
      span_id  = "SpanId",
      level    = "Level",
    }
  }
  stage.labels {
    values = {
      level = "",
    }
  }
  stage.structured_metadata {
    values = {
      trace_id = "trace_id",
      span_id  = "span_id",
    }
  }
  forward_to = [loki.write.local.receiver]
}

loki.write "local" {
  endpoint {
    url = "http://loki.otel-lab.svc.cluster.local:3100/loki/api/v1/push"
  }
}
```

### 5.2 Alloy DaemonSet RBAC

Alloy needs a `ClusterRole` with read access to pods, nodes, and namespaces for `k8sattributes`
enrichment and `loki.source.kubernetes` log tailing. The ServiceAccount, ClusterRole, and
ClusterRoleBinding are in `k8s/alloy/rbac.yaml`.

### 5.3 Exemplar Configuration

Exemplars require configuration at both ends:

**Application side (.NET and Python)** — Enable exemplars via env var on each Deployment:

```yaml
env:
  - name: OTEL_METRICS_EXEMPLAR_FILTER
    value: trace_based
```

> `AddExemplarFilter(ExemplarFilterType.TraceBased)` was removed — it requires opting into OTel .NET
> experimental APIs (SDK 1.9.x) and is not resolvable at compile time without unstable package
> references. The env var is equivalent.

**Alloy side** — Exemplars flow through the spanmetrics connector (`exemplars.enabled = true`
already set above) and are preserved through OTLP export. The Prometheus remote-write exporter
forwards exemplars natively.

**Grafana side** — Dashboard panels must enable "Exemplars" toggle and configure a Tempo/Jaeger
datasource as the trace link target.

---

## 6. Communication Patterns & Trace Propagation Map

```
  Browser (Faro)
      │
      │  traceparent header (W3C) via fetch
      ▼
  ┌─────────────────────┐
  │  gateway-api (.NET)  │  ◄── Span: HTTP Server
  │                      │
  │  ┌─EF Core───MySQL─┐│  ◄── Span: db.mysql (child)
  │  └──────────────────┘│
  │                      │
  │  ──gRPC call──────── │──────────────────────┐
  │                      │  traceparent in       │
  │  ──HTTP call──────── │───┐  gRPC metadata    │
  └─────────────────────┘   │                    │
                             │                    ▼
                             │  ┌──────────────────────────┐
                             │  │  order-api (.NET)         │ ◄── Span: gRPC Server
                             │  │                           │
                             │  │  ┌─EF Core──Postgres─┐   │ ◄── Span: db.postgresql
                             │  │  └───────────────────┘   │
                             │  │                           │
                             │  │  ──RabbitMQ publish────   │ ◄── Span: PRODUCER
                             │  │    (traceparent in        │     (inject headers)
                             │  │     message headers)      │
                             │  └──────────────────────────┘
                             │                    │
                             ▼                    │  async (message queue)
  ┌──────────────────────────┐                    │
  │  notification-svc (Py)   │◄───────────────────┘
  │                          │ ◄── Span: CONSUMER (extract + link)
  │  ┌─Redis─┐               │ ◄── Span: redis (child)
  │  └───────┘               │
  │  ┌─mock email send─┐     │ ◄── Span: notification.send_email
  │  └─────────────────┘     │
  └──────────────────────────┘
```

**Propagation protocol**: W3C TraceContext (`traceparent` / `tracestate`) everywhere — HTTP headers,
gRPC metadata, and RabbitMQ message headers.

A single "Create Order" user click should produce a trace spanning: **Browser → Gateway (.NET) →
Order Service (.NET, gRPC) → RabbitMQ → Notification Service (Python)** — 5 hops, 3 runtimes, 2
communication paradigms (sync + async) in one trace.

---

## 7. Environment Variables & OTel Resource Attributes

Each service sets these via its Deployment spec:

```yaml
env:
  - name: OTEL_SERVICE_NAME
    value: "<service-name>"
  - name: OTEL_RESOURCE_ATTRIBUTES
    value: "service.namespace=otel-lab,service.version=1.0.0,deployment.environment=local"
  - name: OTEL_EXPORTER_OTLP_ENDPOINT
    value: "http://grafana-k8s-alloy-receiver.monitoring.svc.cluster.local:4317"
  - name: OTEL_EXPORTER_OTLP_PROTOCOL
    value: "grpc"
  - name: OTEL_LOGS_EXPORTER
    value: "none"
```

`OTEL_LOGS_EXPORTER=none` is intentional — we validate the **log tailing pattern** (app writes
structured JSON to stdout → Alloy tails → injects trace correlation → ships to Loki) rather than
direct OTLP log export. This mirrors production behavior at scale.

---

## 8. Kubernetes Manifests

All manifests in `k8s/`, plain YAML (no Helm).

### 8.1 Component Matrix

| Directory               | Kind(s)                                                                        | Notes                                                                                                     |
| ----------------------- | ------------------------------------------------------------------------------ | --------------------------------------------------------------------------------------------------------- |
| `k8s/namespace.yaml`    | Namespace                                                                      | `otel-lab`                                                                                                |
| `k8s/secrets.yaml`      | Secret                                                                         | DB passwords, RabbitMQ creds, Grafana Cloud API key                                                       |
| `k8s/mysql/`            | StatefulSet, PVC, Service, ConfigMap (init SQL)                                | ClusterIP on 3306                                                                                         |
| `k8s/postgres/`         | StatefulSet, PVC, Service, ConfigMap (init SQL)                                | ClusterIP on 5432                                                                                         |
| `k8s/redis/`            | Deployment, Service                                                            | ClusterIP on 6379                                                                                         |
| `k8s/rabbitmq/`         | StatefulSet, PVC, Service (AMQP + mgmt)                                        | NodePort 15672 for mgmt UI                                                                                |
| `k8s/alloy/`            | DaemonSet, ConfigMap, ServiceAccount, ClusterRole, ClusterRoleBinding, Service | **Reference only — not deployed.** Superseded by Helm-managed `alloy-receiver` in `monitoring` namespace. |
| `k8s/app/gateway/`      | Deployment, Service                                                            | 2 replicas, ClusterIP on 5000                                                                             |
| `k8s/app/order/`        | Deployment, Service                                                            | 2 replicas, ClusterIP on 5001                                                                             |
| `k8s/app/notification/` | Deployment, Service                                                            | 2 replicas, ClusterIP on 8000                                                                             |
| `k8s/app/frontend/`     | Deployment, Service                                                            | nginx, ClusterIP on 80                                                                                    |
| `k8s/ingress.yaml`      | Ingress (Traefik, k3d default)                                                 | Routes `/` → frontend, `/api/*` → gateway                                                                 |
| `k8s/jaeger/`           | Deployment, Service (NodePort 16686)                                           | Local traces backend                                                                                      |
| `k8s/prometheus/`       | Deployment, ConfigMap, Service (NodePort 9090)                                 | Scrapes Alloy + self                                                                                      |
| `k8s/loki/`             | StatefulSet, ConfigMap, Service                                                | Local logs backend                                                                                        |
| `k8s/grafana/`          | Deployment, ConfigMap (datasources + dashboards), Service (NodePort 3000)      | Pre-provisioned                                                                                           |
| `k8s/loadtest/`         | Job (k6)                                                                       | Generates representative traffic                                                                          |

---

## 9. Local Setup Flow

```bash
# 1. Create k3d cluster with port mappings
k3d cluster create otel-lab \
  -p "8080:80@loadbalancer"  \   # Ingress (frontend + API) — port 80 blocked on WSL2
  -p "16686:30686@server:0"  \   # Jaeger UI
  -p "3000:30300@server:0"   \   # Grafana
  -p "9090:30090@server:0"   \   # Prometheus
  -p "15672:30672@server:0"      # RabbitMQ Management

# 2. Build & import images
docker build -t otel-frontend:local    ./src/frontend
docker build -t gateway-api:local      ./src/gateway-api
docker build -t order-api:local        ./src/order-api
docker build -t notification-svc:local ./src/notification-svc

k3d image import \
  otel-frontend:local gateway-api:local \
  order-api:local notification-svc:local \
  -c otel-lab

# 3. Deploy infrastructure
kubectl apply -f k8s/namespace.yaml
kubectl apply -f k8s/secrets.yaml
kubectl apply -f k8s/mysql/
kubectl apply -f k8s/postgres/
kubectl apply -f k8s/redis/
kubectl apply -f k8s/rabbitmq/

# 4. Wait for data stores
kubectl -n otel-lab wait --for=condition=ready pod -l tier=datastore --timeout=180s

# 5. Deploy local observability backends
kubectl apply -f k8s/jaeger/
kubectl apply -f k8s/prometheus/
kubectl apply -f k8s/loki/
kubectl apply -f k8s/grafana/

# Deploy Helm-managed Alloy stack (monitoring namespace) — required for OTLP collection
# (./deploy-local.sh does all of the above in one command, driven by conf.yml)
./deploy-local.sh --skip-cluster --skip-build

# 6. Deploy application
kubectl apply -f k8s/app/gateway/
kubectl apply -f k8s/app/order/
kubectl apply -f k8s/app/notification/
kubectl apply -f k8s/app/frontend/
kubectl apply -f k8s/ingress.yaml

# 7. Run DB migrations (init-containers or one-off jobs)
kubectl -n otel-lab exec deploy/gateway-api -- dotnet ef database update
kubectl -n otel-lab exec deploy/order-api -- dotnet ef database update

# 8. Validate
curl http://localhost:8080/api/projects
open http://localhost:8080       # Angular frontend
open http://localhost:16686      # Jaeger
open http://localhost:3000       # Grafana
open http://localhost:15672      # RabbitMQ

# 9. Load test
kubectl apply -f k8s/loadtest/
```

### 9.1 Grafana Cloud Mode

To enable dual export, populate the Secret with your Grafana Cloud credentials:

```yaml
# k8s/secrets.yaml (add these)
GRAFANA_CLOUD_USER: "<base64-encoded-instance-id>"
GRAFANA_CLOUD_API_KEY: "<base64-encoded-api-key>"
GRAFANA_CLOUD_TEMPO_ENDPOINT: "<base64-encoded-tempo-otlp-endpoint>"
GRAFANA_CLOUD_MIMIR_ENDPOINT: "<base64-encoded-mimir-otlp-endpoint>"
GRAFANA_CLOUD_LOKI_ENDPOINT: "<base64-encoded-loki-otlp-endpoint>"
```

When env vars are empty/unset, the Grafana Cloud exporters fail silently and only local backends
receive data. No config change needed to toggle.

---

## 10. Load Test Script (k6)

```javascript
import http from 'k6/http';
import { check, sleep } from 'k6';

export const options = {
  stages: [
    { duration: '30s', target: 5 },
    { duration: '2m',  target: 20 },
    { duration: '30s', target: 0 },
  ],
};

const BASE = 'http://gateway-api.otel-lab.svc.cluster.local:5000';

export default function () {
  // Happy path: create project → create order → check notifications
  const project = http.post(`${BASE}/api/projects`, JSON.stringify({
    name: `Project-${Date.now()}`,
    owner: 'k6-user',
  }), { headers: { 'Content-Type': 'application/json' } });
  check(project, { 'project created': (r) => r.status === 201 });

  const projectId = JSON.parse(project.body).id;

  const order = http.post(`${BASE}/api/orders`, JSON.stringify({
    projectId: projectId,
    description: 'Load test order',
    amount: Math.random() * 10000,
  }), { headers: { 'Content-Type': 'application/json' } });
  check(order, { 'order created': (r) => r.status === 201 });

  sleep(1); // Let async processing happen

  const notifications = http.get(`${BASE}/api/notifications`);
  check(notifications, { 'notifications ok': (r) => r.status === 200 });

  // Read paths
  http.get(`${BASE}/api/projects`);
  http.get(`${BASE}/api/projects/${projectId}`);
  http.get(`${BASE}/api/projects/${projectId}/orders`);

  // Edge cases (10% of iterations)
  if (Math.random() < 0.1) {
    http.get(`${BASE}/api/slow`);
  }
  if (Math.random() < 0.05) {
    http.get(`${BASE}/api/error`);
  }

  sleep(0.5);
}
```

---

## 11. Validation Checklist

### 11.1 Trace Propagation (Jaeger / Tempo)

- [ ] **Frontend → Backend**: Faro-generated browser span links to `gateway-api` HTTP server span
      (same `traceId`)
- [ ] **HTTP propagation**: `gateway-api` → `notification-svc` HTTP call has parent-child span
      relationship
- [ ] **gRPC propagation**: `gateway-api` → `order-api` gRPC call has parent-child span
      relationship, with `rpc.method` and `rpc.service` attributes
- [ ] **Async propagation (critical)**: `order-api` PRODUCER span → RabbitMQ → `notification-svc`
      CONSUMER span share the same trace, linked via message headers
- [ ] **Full trace**: Single "Create Order" from Angular shows spans across Browser → Gateway →
      Order → RabbitMQ → Notification (5 hops)
- [ ] **Error spans**: `/api/error` produces `otel.status_code = ERROR` with `exception.message` and
      `exception.stacktrace` events
- [ ] **Health-check exclusion**: No `/healthz` spans appear in Jaeger

### 11.2 Span Metrics (Alloy → Prometheus/Mimir)

- [ ] `traces_spanmetrics_latency_bucket` histogram present with `service.name`, `span.name`,
      `http.method`, `http.route` labels
- [ ] `traces_spanmetrics_calls_total` counter present
- [ ] gRPC spans produce span metrics with `rpc.method` and `rpc.service` dimensions
- [ ] RabbitMQ spans produce span metrics with `messaging.operation` dimension

### 11.3 Application Metrics

- [ ] `http_server_request_duration_seconds` present for both .NET services
- [ ] `http_server_request_duration_seconds` present for Python service (FastAPI)
- [ ] `orders.created.total` counter increments
- [ ] `notifications.processed.total` counter increments with correct status labels
- [ ] `gateway.downstream.duration` histogram present

### 11.4 Exemplars

- [ ] Click a spike on `http_server_request_duration_seconds` histogram panel in Grafana → exemplar
      dots visible
- [ ] Clicking an exemplar opens the linked trace in Jaeger/Tempo
- [ ] Span metrics histograms also carry exemplars

### 11.5 K8s Attributes Enrichment

- [ ] Every span and metric has `k8s.pod.name`, `k8s.namespace.name`, `k8s.deployment.name`,
      `k8s.node.name`
- [ ] Labels from `app.kubernetes.io/*` pod labels are attached

### 11.6 Logs & Trace Correlation

- [ ] Application logs in Loki contain `trace_id` and `span_id` as structured metadata
- [ ] In Grafana, selecting a trace → "Logs for this span" shows correlated log lines
- [ ] Log severity levels map correctly: `Information`/`INFO`, `Warning`/`WARN`, `Error`/`ERROR`
- [ ] Python and .NET logs both correlate correctly

### 11.7 Tail Sampling

- [ ] All error traces are retained (100% of `/api/error` calls appear in Jaeger)
- [ ] All slow traces (>2s) are retained (`/api/slow` calls always appear)
- [ ] Normal traces appear at roughly 25% rate (verify over sustained k6 load)

### 11.8 Frontend RUM (Faro)

- [ ] `otel-frontend` appears as a service in Grafana Cloud Frontend or local Faro data
- [ ] Web Vitals (LCP, FID, CLS) are reported
- [ ] JavaScript errors are captured with stack traces
- [ ] Route change navigation spans are recorded

### 11.9 Grafana Cloud (if enabled)

- [ ] Traces appear in Grafana Cloud Tempo
- [ ] Metrics appear in Grafana Cloud Mimir (including span metrics)
- [ ] Logs appear in Grafana Cloud Loki with trace correlation
- [ ] Exemplar links work cross-datasource in Grafana Cloud

### 11.10 Resilience / Negative Scenarios

- [ ] Kill MySQL pod → gateway-api 500s → error spans + logs recorded correctly
- [ ] Kill RabbitMQ pod → order-api publish fails → error span with exception, no message loss after
      recovery
- [ ] Restart `alloy-receiver` DaemonSet
      (`kubectl rollout restart daemonset/grafana-k8s-alloy-receiver -n monitoring`) → data gap
      limited to batch window (~5s), no OOM
- [ ] Scale notification-svc to 0 → messages queue up in RabbitMQ → resume processing on scale-up
- [ ] Scale order-api to 3 replicas → trace propagation works from all pods

---

## 12. Repo Structure

```
otel-microservices-lab/
├── src/
│   ├── frontend/                    # Angular SPA
│   │   ├── src/
│   │   │   ├── app/
│   │   │   │   ├── pages/           # Dashboard, ProjectDetail, Orders, Notifications, ErrorTest
│   │   │   │   ├── services/        # API client services
│   │   │   │   └── telemetry/
│   │   │   │       └── faro.ts      # Faro initialization
│   │   │   ├── environments/
│   │   │   └── main.ts
│   │   ├── nginx.conf               # SPA routing + proxy to gateway
│   │   ├── Dockerfile
│   │   └── package.json
│   │
│   ├── gateway-api/                 # .NET 8 — API Gateway / BFF
│   │   ├── Program.cs
│   │   ├── Models/
│   │   ├── Data/AppDbContext.cs
│   │   ├── Endpoints/
│   │   ├── Protos/orders.proto      # shared proto
│   │   ├── Telemetry/DiagnosticsConfig.cs
│   │   ├── Dockerfile
│   │   └── gateway-api.csproj
│   │
│   ├── order-api/                   # .NET 8 — gRPC Order Service
│   │   ├── Program.cs
│   │   ├── Models/
│   │   ├── Data/AppDbContext.cs
│   │   ├── Services/OrderGrpcService.cs
│   │   ├── Messaging/OrderPublisher.cs
│   │   ├── Protos/orders.proto
│   │   ├── Telemetry/DiagnosticsConfig.cs
│   │   ├── Dockerfile
│   │   └── order-api.csproj
│   │
│   ├── notification-svc/            # Python FastAPI
│   │   ├── app/
│   │   │   ├── main.py              # FastAPI app + OTel setup
│   │   │   ├── consumer.py          # RabbitMQ consumer with trace extraction
│   │   │   ├── models.py
│   │   │   ├── redis_client.py
│   │   │   └── telemetry.py         # Custom instruments
│   │   ├── requirements.txt
│   │   └── Dockerfile
│   │
│   └── proto/                       # Shared proto definitions (copied at build)
│       └── orders.proto
│
├── k8s/
│   ├── namespace.yaml
│   ├── secrets.yaml
│   ├── mysql/
│   │   ├── statefulset.yaml
│   │   ├── service.yaml
│   │   └── init-configmap.yaml
│   ├── postgres/
│   │   ├── statefulset.yaml
│   │   ├── service.yaml
│   │   └── init-configmap.yaml
│   ├── redis/
│   │   ├── deployment.yaml
│   │   └── service.yaml
│   ├── rabbitmq/
│   │   ├── statefulset.yaml
│   │   └── service.yaml
│   ├── alloy/
│   │   ├── daemonset.yaml
│   │   ├── configmap.yaml           # River config from Section 5
│   │   ├── rbac.yaml
│   │   └── service.yaml
│   ├── app/
│   │   ├── gateway/
│   │   │   ├── deployment.yaml
│   │   │   └── service.yaml
│   │   ├── order/
│   │   │   ├── deployment.yaml
│   │   │   └── service.yaml
│   │   ├── notification/
│   │   │   ├── deployment.yaml
│   │   │   └── service.yaml
│   │   └── frontend/
│   │       ├── deployment.yaml
│   │       └── service.yaml
│   ├── ingress.yaml
│   ├── jaeger/
│   ├── prometheus/
│   ├── loki/
│   ├── grafana/
│   │   ├── deployment.yaml
│   │   ├── service.yaml
│   │   └── provisioning/
│   │       ├── datasources.yaml     # Jaeger, Prometheus, Loki pre-configured
│   │       └── dashboards/
│   │           ├── service-overview.json
│   │           └── trace-analysis.json
│   └── loadtest/
│       ├── job.yaml
│       └── script.js
│
├── spec.md                          # ← this file
├── Makefile                         # Build, deploy, teardown shortcuts
└── README.md
```

---

## 13. Makefile (Developer Shortcuts)

`./deploy-local.sh` is the sole deploy path (cluster + builds + manifests + Helm, driven by
`conf.yml`) — see [CLAUDE.md](../CLAUDE.md). The [`Makefile`](../Makefile) at the project root no
longer deploys anything; it only builds images, runs tests, and fetches/applies Grafana Cloud
credentials. Its `deploy`/`deploy-cloud`/`deploy-local`/`full` targets exist only as stubs that
print a redirect to `./deploy-local.sh` and exit non-zero. Key targets:

| Target                   | Description                                                                                                                   |
| ------------------------ | ----------------------------------------------------------------------------------------------------------------------------- |
| `make cluster-up`        | Create k3d cluster with port mappings (8080→80, 16686, 3000, 9090, 15672); injects corporate CA cert into k3d node if present |
| `make cluster-down`      | Delete k3d cluster                                                                                                            |
| `make build`             | Build all 4 Docker images; injects corporate CA cert into each build context                                                  |
| `make import`            | `build` + import images into k3d                                                                                              |
| `make teardown`          | Delete `otel-lab` namespace                                                                                                   |
| `make validate`          | Smoke-test all endpoints with curl                                                                                            |
| `make test`              | Run k6 load-test Job                                                                                                          |
| `make logs`              | Stream logs from all app pods                                                                                                 |
| `make secrets-fetch-akv` | Pull Grafana Cloud credentials from Azure Key Vault, apply as K8s Secret, upgrade Helm with cloud destinations                |
| `make secrets-apply`     | Apply credentials from `.env` manually (AKV fallback)                                                                         |
| `make secrets-show`      | Print the currently stored Grafana Cloud secret values (API key redacted)                                                     |
| `make secrets-show`      | Print stored secret values (API keys redacted)                                                                                |

---

## 14. Pre-provisioned Grafana Dashboards

Two dashboards auto-provisioned via ConfigMap:

### 14.1 Service Overview Dashboard

| Panel                        | Query source                                                      | Validates               |
| ---------------------------- | ----------------------------------------------------------------- | ----------------------- |
| Request rate by service      | `traces_spanmetrics_calls_total`                                  | Span metrics connector  |
| P50/P95/P99 latency by route | `traces_spanmetrics_latency_bucket`                               | Span metrics histograms |
| Error rate by service        | `traces_spanmetrics_calls_total{status_code="STATUS_CODE_ERROR"}` | Error tracking          |
| Inflight requests (gateway)  | `gateway_requests_inflight`                                       | UpDownCounter           |
| Orders created               | `orders_created_total`                                            | Custom counter          |
| Notifications processed      | `notifications_processed_total`                                   | Cross-language metrics  |
| Exemplar scatterplot         | `http_server_request_duration_seconds` with exemplars toggle      | Exemplar → trace link   |

### 14.2 Trace Analysis Dashboard

| Panel                  | Query source                                                    | Validates                |
| ---------------------- | --------------------------------------------------------------- | ------------------------ |
| Trace search           | Jaeger/Tempo datasource                                         | End-to-end trace view    |
| Service map            | Tempo service graph                                             | Auto-discovered topology |
| Trace-to-logs          | Loki datasource with `trace_id` filter                          | Log correlation          |
| Sampling effectiveness | Compare `traces_spanmetrics_calls_total` vs sampled trace count | Tail sampling validation |

---

## 15. Out of Scope for v1 (Future Extensions)

- **OpenTelemetry Operator** for auto-injection (replace SDK-based setup).
- **Kafka** as an alternate broker for higher-throughput async validation.
- **Second Python service** (e.g., ML inference) with GPU metrics.
- **Service mesh** (Linkerd/Istio) sidecar telemetry alongside OTel SDK telemetry.
- **Continuous profiling** via Pyroscope integration in Alloy.
- **SLO dashboards** using Sloth or Pyrra, driven by span metrics.
- **Synthetic monitoring** via Grafana Cloud k6 checks against the lab endpoints.
- **Grafana Tempo** replacing Jaeger locally (closer to your Grafana Cloud stack).
