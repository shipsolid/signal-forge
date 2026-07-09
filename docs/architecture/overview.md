# Architecture Overview

## System topology

```mermaid
flowchart TD
    subgraph otelns["Namespace: otel-lab"]
        spa["Angular SPA<br/>Faro RUM<br/>nginx :80"]
        gw["gateway-api<br/>.NET 8<br/>MySQL"]
        oa["order-api<br/>.NET 8 (gRPC)<br/>PostgreSQL"]
        ns["notification-svc<br/>Python<br/>Redis"]
        rmq["RabbitMQ<br/>3.13"]

        spa -- HTTP --> gw
        gw -- gRPC --> oa
        gw -- HTTP --> ns
        oa -- AMQP --> rmq
        rmq -- consume --> ns
    end

    subgraph monns["Namespace: monitoring (grafana/k8s-monitoring Helm v3.8.4)"]
        subgraph alloyrecv["alloy-receiver (DaemonSet)"]
            otlp["OTLP :4317/:4318"]
            faro["Faro :12347"]
            k8sattr["k8sattributes"]
            transform["env_label transform"]
            filterhz["filter(/healthz)"]
            spanm["spanmetrics connector<br/>(RED metrics)"]
            tail["tail_sampling<br/>(errors 100%, slow 100%, rest 25%)"]
            batchp["batch"]
            proc["processor"]

            otlp --> k8sattr
            faro --> k8sattr
            k8sattr --> transform
            transform -- traces --> filterhz --> spanm --> tail --> batchp
            transform -- metrics --> batchp
            transform -- logs --> proc
        end

        subgraph localb["Local"]
            jaegerL["Jaeger"]
            promL["Prometheus"]
            lokiL["Loki"]
        end
        subgraph cloudb["Grafana Cloud"]
            tempoC["Tempo"]
            mimirC["Mimir"]
            lokiC["Loki"]
        end

        batchp --> jaegerL
        batchp --> tempoC
        batchp --> promL
        batchp --> mimirC
        proc --> lokiL
        proc --> lokiC

        alogs["alloy-logs (DaemonSet)<br/>tails pod stdout → trace correlation → Loki"]
        amet["alloy-metrics (StatefulSet)<br/>kubelet/cAdvisor/KSM → Prometheus"]
        asing["alloy-singleton (Deployment)<br/>cluster events → Loki/Prometheus"]
    end

    otelns -- "All services: OTLP gRPC :4317" --> otlp
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

```mermaid
sequenceDiagram
    participant Browser as Browser (Faro)
    participant Gateway as gateway-api
    participant MySQL
    participant Order as order-api
    participant Postgres as PostgreSQL
    participant RabbitMQ
    participant Notif as notification-svc
    participant Redis

    Browser->>Gateway: HTTP request (traceparent in header)
    Note right of Gateway: HTTP server span
    Gateway->>MySQL: EF Core child: db.mysql
    Gateway->>Order: gRPC call (traceparent in metadata)
    Note right of Order: gRPC server span
    Order->>Postgres: EF Core child: db.postgresql
    Order-)RabbitMQ: AMQP publish (traceparent in message headers, async)
    RabbitMQ-)Notif: consume
    Note right of Notif: CONSUMER span (SpanLink to producer, same traceId)
    Notif->>Redis: Redis child: db.redis
    Notif->>Notif: send_email child span
    Gateway->>Notif: HTTP call (traceparent in header)
    Note right of Notif: HTTP server span
    Notif->>Redis: Redis child: db.redis
```

The RabbitMQ hop uses a **SpanLink** (not parent-child) because message processing is asynchronous and may involve retries. Both spans share the same `traceId`. In Jaeger this renders as a dashed arrow.

## Signal flow by type

### Traces

```mermaid
flowchart LR
    appsdk["App SDK"] -- OTLP gRPC --> alloyrecv["alloy-receiver"]
    alloyrecv --> k8sattr["k8sattributes<br/>(enrich with pod/namespace/node)"]
    k8sattr --> transform["transform<br/>(stamp deployment.environment)"]
    transform --> filterhz["filter<br/>(drop /healthz spans)"]
    filterhz --> spanm["spanmetrics connector<br/>(generate RED metrics — before sampling)"]
    spanm --> tail["tail_sampling<br/>(errors=100%, slow&gt;2s=100%, rest=25%)"]
    tail --> batchp["batch"]
    batchp --> dest["Jaeger (local) and/or<br/>Grafana Cloud Tempo"]
```

### Metrics

```mermaid
flowchart LR
    appsdk2["App SDK"] -- OTLP gRPC --> alloyrecv2["alloy-receiver"]
    alloyrecv2 --> kt2["k8sattributes + transform"]
    kt2 --> batch2["batch"]
    batch2 -- prometheus.remote_write --> prom2["Prometheus (local)"]
    batch2 -- OTLP HTTP --> mimir2["Grafana Cloud Mimir"]

    amet2["alloy-metrics (StatefulSet)"] --> scrape2["prometheus.scrape<br/>(kubelet, cAdvisor, kube-state-metrics, node-exporter)"]
    scrape2 --> prom2
    scrape2 --> mimir2
```

### Logs

```mermaid
flowchart LR
    podstdout["Pod stdout (JSON)"] --> alloylogs3["alloy-logs<br/>(node-level tailing)"]
    alloylogs3 --> lokisource3["loki.source.kubernetes"]
    lokisource3 --> stagejson3["loki.process: stage.json<br/>(extract TraceId/SpanId)"]
    stagejson3 --> stagemeta3["loki.process: stage.structured_metadata<br/>(attach trace_id/span_id)"]
    stagemeta3 --> lokiwrite3["loki.write"]
    lokiwrite3 --> dest3["Loki (local) or<br/>Grafana Cloud Loki"]
```

Note: `OTEL_LOGS_EXPORTER=none` is set on all services. Logs travel via node-level tailing, not OTLP push. This is the production pattern for high-volume log shipping.

### Browser RUM

```mermaid
flowchart LR
    spa4["Angular SPA"] -- HTTP --> faro4["alloy-receiver<br/>faro.receiver :12347"]
    faro4 -- traces --> k8s4["k8sattributes pipeline<br/>(same as above)"]
    faro4 -- logs --> lokiwrite4["loki.write<br/>(directly, bypassing OTel pipeline)"]
```

## Deployment modes

| Mode            | Command                                                                    | Backends                                     | Use case                                  |
| --------------- | -------------------------------------------------------------------------- | -------------------------------------------- | ----------------------------------------- |
| Local (default) | `./deploy-local.sh`                                                        | Jaeger, Prometheus, Loki, Grafana in-cluster | Default — no cloud credentials needed     |
| Cloud (opt-in)  | `./scripts/fetch-grafana-cloud-conf-from-akv.sh` then `./deploy-local.sh`  | Grafana Cloud Tempo/Mimir/Loki               | End-to-end validation with remote storage |

The Alloy collector configuration is separate per mode:

- Cloud: `k8s/monitoring/grafana-helm/values-cloud.yaml.tmpl` (Helm values rendered by `deploy-local.sh`)
- Local: `k8s/monitoring/grafana/local/configmap.yaml` (hand-rolled DaemonSet — reference artifact)

See [CLAUDE.md](../../CLAUDE.md) for the full command reference and safety checks built into `deploy-local.sh`.

## Port map (after `./deploy-local.sh`)

| URL                                                                                                  | Service                                 |
| ---------------------------------------------------------------------------------------------------- | --------------------------------------- |
| `http://localhost:8080`                                                                              | Angular SPA + API (via Traefik ingress) |
| `http://localhost:16686`                                                                             | Jaeger UI                               |
| `http://localhost:3000`                                                                              | Grafana (admin/admin)                   |
| `http://localhost:9090`                                                                              | Prometheus                              |
| `http://localhost:15672`                                                                             | RabbitMQ Management (signalforge/guest) |
| `kubectl port-forward svc/grafana-k8s-alloy-receiver 12345 -n monitoring` → `http://localhost:12345` | Alloy pipeline debug UI                 |
