# Architecture Overview

## System topology

```
┌────────────────────────────────────────────────────────────────────────────────┐
│  Namespace: otel-lab                                                           │
│                                                                                │
│  ┌────────────────┐  HTTP   ┌──────────────────┐  gRPC   ┌──────────────────┐ │
│  │  Angular SPA   │────────▶│  gateway-api     │────────▶│  order-api       │ │
│  │  Faro RUM      │         │  .NET 8          │         │  .NET 8 (gRPC)   │ │
│  │  nginx :80     │         │  MySQL           │         │  PostgreSQL       │ │
│  └────────────────┘         └────────┬─────────┘         └────────┬─────────┘ │
│                                      │ HTTP                        │ AMQP      │
│                                      ▼                             ▼           │
│                             ┌──────────────────┐  ◀─consume─ ┌──────────────┐ │
│                             │  notification-   │             │  RabbitMQ    │ │
│                             │  svc (Python)    │             │  3.13        │ │
│                             │  Redis           │             └──────────────┘ │
│                             └──────────────────┘                              │
│                                                                                │
│  All services ──OTLP gRPC──────────────────────────────────────────────────► │
└──────────────────────────────────────────────────────────────────────────────┬┘
                                                                               │
                                                                    OTLP :4317 │
┌──────────────────────────────────────────────────────────────────────────────▼┐
│  Namespace: monitoring  (grafana/k8s-monitoring Helm v3.8.4)                  │
│                                                                                │
│  ┌────────────────────────────────────────────────────────────────────────┐   │
│  │  alloy-receiver  (DaemonSet)                                           │   │
│  │                                                                        │   │
│  │  OTLP :4317/:4318 ──▶ k8sattributes ──▶ env_label transform          │   │
│  │  Faro :12347    ──┘              │                                     │   │
│  │                                  ├── traces ──▶ filter(/healthz)      │   │
│  │                                  │                    │               │   │
│  │                                  │         ┌──────────┴──────────┐   │   │
│  │                                  │     spanmetrics          tail_sampling│   │
│  │                                  │     connector            (errors 100%│   │
│  │                                  │     (RED metrics)        slow 100%  │   │
│  │                                  │         │                rest 25%)  │   │
│  │                                  │         └──────────┬──────────┘   │   │
│  │                                  ├── metrics ─────────▶ batch        │   │
│  │                                  └── logs   ─────────▶ processor     │   │
│  │                                                         │             │   │
│  │                                               ┌─────────┼──────────┐ │   │
│  │                                           Jaeger    Prometheus   Loki│   │
│  │                                           (local)   (local)    (local│   │
│  │                                                                   or  │   │
│  │                                           Tempo     Mimir        Loki │   │
│  │                                           (cloud)   (cloud)   (cloud) │   │
│  └────────────────────────────────────────────────────────────────────────┘   │
│                                                                                │
│  alloy-logs  (DaemonSet)   — tails pod stdout → trace correlation → Loki     │
│  alloy-metrics (StatefulSet) — kubelet/cAdvisor/KSM → Prometheus             │
│  alloy-singleton (Deployment) — cluster events → Loki/Prometheus             │
└────────────────────────────────────────────────────────────────────────────────┘
```

## Service inventory

| Service            | Runtime             | Role                                 | Owns                         |
| ------------------ | ------------------- | ------------------------------------ | ---------------------------- |
| `otel-frontend`    | Angular 17 + nginx  | Browser SPA, Faro RUM                | —                            |
| `gateway-api`      | .NET 8 Minimal API  | BFF — receives all browser calls     | MySQL 8 (projects)           |
| `order-api`        | .NET 8 gRPC         | Order CRUD + async events            | PostgreSQL 16 (orders)       |
| `notification-svc` | Python 3.12 FastAPI | RabbitMQ consumer, dedup, mock email | Redis 7 (notification state) |

## Communication patterns

| From        | To               | Protocol                        | OTel propagation                                         |
| ----------- | ---------------- | ------------------------------- | -------------------------------------------------------- |
| Browser     | gateway-api      | HTTP/JSON                       | Faro injects `traceparent` header                        |
| gateway-api | order-api        | gRPC (unary + server-streaming) | Auto-injected in gRPC metadata                           |
| gateway-api | notification-svc | HTTP/JSON                       | Auto-injected via `HttpClient` instrumentation           |
| order-api   | RabbitMQ         | AMQP 0-9-1                      | Manual `TextMapPropagator.Inject()` into message headers |
| RabbitMQ    | notification-svc | AMQP 0-9-1                      | Manual `TraceContextTextMapPropagator.extract()`         |

## Trace propagation map

A single "Create Order" click produces a trace spanning five hops and three runtimes:

```
Browser span (Faro)
  │  traceparent in HTTP header
  ▼
gateway-api: HTTP server span
  │  EF Core child: db.mysql
  │
  │─── gRPC call (traceparent in metadata)
  │    ▼
  │    order-api: gRPC server span
  │      │  EF Core child: db.postgresql
  │      │
  │      │  ── AMQP publish (traceparent in message headers) ──▶ (async)
  │      │                                                         │
  │      │                                        notification-svc: CONSUMER span
  │      │                                          │  (SpanLink to producer, same traceId)
  │      │                                          │  Redis child: db.redis
  │      │                                          └─ send_email child span
  │
  │─── HTTP call (traceparent in header)
       ▼
       notification-svc: HTTP server span
         │  Redis child: db.redis
```

The RabbitMQ hop uses a **SpanLink** (not parent-child) because message processing is asynchronous and may involve retries. Both spans share the same `traceId`. In Jaeger this renders as a dashed arrow.

## Signal flow by type

### Traces

```
App SDK ──OTLP gRPC──▶ alloy-receiver
  → k8sattributes (enrich with pod/namespace/node)
  → transform (stamp deployment.environment)
  → filter (drop /healthz spans)
  → spanmetrics connector (generate RED metrics — before sampling)
  → tail_sampling (errors=100%, slow>2s=100%, rest=25%)
  → batch
  → Jaeger (local) and/or Grafana Cloud Tempo
```

### Metrics

```
App SDK ──OTLP gRPC──▶ alloy-receiver
  → k8sattributes + transform
  → batch
  → prometheus.remote_write → Prometheus (local)
       and/or OTLP HTTP → Grafana Cloud Mimir

alloy-metrics (StatefulSet)
  → prometheus.scrape (kubelet, cAdvisor, kube-state-metrics, node-exporter)
  → Prometheus (local) and/or Grafana Cloud Mimir
```

### Logs

```
Pod stdout (JSON) ──▶ alloy-logs (node-level tailing)
  → loki.source.kubernetes
  → loki.process: stage.json (extract TraceId/SpanId)
  → loki.process: stage.structured_metadata (attach trace_id/span_id)
  → loki.write → Loki (local) or Grafana Cloud Loki
```

Note: `OTEL_LOGS_EXPORTER=none` is set on all services. Logs travel via node-level tailing, not OTLP push. This is the production pattern for high-volume log shipping.

### Browser RUM

```
Angular SPA ──HTTP──▶ alloy-receiver faro.receiver :12347
  → traces → k8sattributes pipeline (same as above)
  → logs   → loki.write (directly, bypassing OTel pipeline)
```

## Deployment modes

| Mode            | Command                                        | Backends                                     | Use case                                  |
| --------------- | ---------------------------------------------- | -------------------------------------------- | ----------------------------------------- |
| Local (default) | `make full-helm`                               | Jaeger, Prometheus, Loki, Grafana in-cluster | Default — no cloud credentials needed     |
| Cloud (opt-in)  | `make secrets-fetch-akv` then `make full-helm` | Grafana Cloud Tempo/Mimir/Loki               | End-to-end validation with remote storage |

The Alloy collector configmap is separate per mode:

- Cloud: `k8s/monitoring/grafana/grafana-cloud/configmap.yaml`
- Local: `k8s/monitoring/grafana/local/configmap.yaml`

`make deploy` is an alias for `make deploy-cloud`.

## Port map (after `make full-helm`)

| URL                                                                                                  | Service                                 |
| ---------------------------------------------------------------------------------------------------- | --------------------------------------- |
| `http://localhost:8080`                                                                              | Angular SPA + API (via Traefik ingress) |
| `http://localhost:16686`                                                                             | Jaeger UI                               |
| `http://localhost:3000`                                                                              | Grafana (admin/admin)                   |
| `http://localhost:9090`                                                                              | Prometheus                              |
| `http://localhost:15672`                                                                             | RabbitMQ Management (guest/guest)       |
| `kubectl port-forward svc/grafana-k8s-alloy-receiver 12345 -n monitoring` → `http://localhost:12345` | Alloy pipeline debug UI                 |
