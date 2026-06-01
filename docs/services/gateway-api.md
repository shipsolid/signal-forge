# Service: gateway-api

**Role**: API Gateway / BFF (Backend for Frontend). Receives all browser HTTP calls, fans out to downstream services, owns the MySQL `projects` aggregate.

**Runtime**: .NET 8 Minimal API
**Port**: 5000 (cluster-internal), exposed via Traefik ingress at `/api/*`
**Replicas**: 2

---

## Endpoints

| Method   | Route                       | Description               | Downstream                             |
| -------- | --------------------------- | ------------------------- | -------------------------------------- |
| `GET`    | `/api/projects`             | List all projects         | MySQL (EF Core)                        |
| `GET`    | `/api/projects/{id}`        | Get project by ID         | MySQL                                  |
| `POST`   | `/api/projects`             | Create project            | MySQL                                  |
| `DELETE` | `/api/projects/{id}`        | Delete project            | MySQL                                  |
| `POST`   | `/api/orders`               | Create an order           | gRPC → order-api                       |
| `GET`    | `/api/projects/{id}/orders` | List orders for a project | gRPC streaming → order-api             |
| `GET`    | `/api/notifications`        | List recent notifications | HTTP → notification-svc                |
| `GET`    | `/api/slow`                 | Artificial 2–5s delay     | — (local sleep)                        |
| `GET`    | `/api/error`                | Always returns 500        | — (throws `InvalidOperationException`) |
| `GET`    | `/healthz`                  | Liveness/readiness probe  | —                                      |

### Input validation (POST /api/orders)

Validated in `OrderEndpoints.cs` before any downstream call:

| Field         | Rule                        |
| ------------- | --------------------------- |
| `projectId`   | > 0                         |
| `amount`      | > 0 and ≤ 999,999.99        |
| `description` | Non-empty, ≤ 500 characters |

Returns `HTTP 422 Unprocessable Entity` with `ValidationProblem` details on failure.

---

## Domain model

```csharp
public class Project
{
    public int Id { get; set; }
    public string Name { get; set; }
    public string Owner { get; set; }
    public DateTime CreatedAt { get; set; }
}
```

Database: MySQL 8, managed via EF Core 8 (Pomelo provider). Migrations run via `dotnet ef database update`.

---

## Configuration

All configuration is via environment variables injected from Kubernetes secrets and ConfigMaps.

| Variable                               | Source                                        | Required | Purpose                                               |
| -------------------------------------- | --------------------------------------------- | -------- | ----------------------------------------------------- |
| `ConnectionStrings__DefaultConnection` | `db-secrets` Secret (`GATEWAY_DB_CONNECTION`) | **Yes**  | MySQL connection string                               |
| `Services__OrderApi`                   | Deployment env                                | Yes      | gRPC endpoint for order-api (`http://order-api:5001`) |
| `Services__NotificationSvc`            | Deployment env                                | Yes      | HTTP endpoint for notification-svc                    |
| `Cors__AllowedOrigins`                 | Deployment env                                | No       | CORS origins (default: `http://localhost:4200`)       |
| `OTEL_SERVICE_NAME`                    | Deployment env                                | Yes      | `gateway-api`                                         |
| `OTEL_EXPORTER_OTLP_ENDPOINT`          | Deployment env                                | Yes      | `http://grafana-k8s-alloy-receiver.monitoring:4317`   |

`AllowedHosts` in `appsettings.json` is set to `gateway-api,gateway-api.otel-lab,localhost,127.0.0.1` — not wildcard.

Fail-fast: if `ConnectionStrings__DefaultConnection` is empty at startup, the process throws `InvalidOperationException` immediately. The pod enters `CrashLoopBackOff` and the error is visible in `kubectl describe pod`.

---

## OTel instrumentation

### Packages

```xml
<PackageReference Include="OpenTelemetry.Extensions.Hosting" />
<PackageReference Include="OpenTelemetry.Instrumentation.AspNetCore" />
<PackageReference Include="OpenTelemetry.Instrumentation.Http" />
<PackageReference Include="OpenTelemetry.Instrumentation.GrpcNetClient" />
<PackageReference Include="OpenTelemetry.Instrumentation.EntityFrameworkCore" />
<PackageReference Include="OpenTelemetry.Instrumentation.MySqlData" />
<PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" />
```

### Auto-instrumented signals

| Span                       | Created by                              | Key attributes                                  |
| -------------------------- | --------------------------------------- | ----------------------------------------------- |
| `HTTP {method} {route}`    | `AddAspNetCoreInstrumentation`          | `http.method`, `http.route`, `http.status_code` |
| `HTTP {method}` (outbound) | `AddHttpClientInstrumentation`          | `http.url`, `peer.service`                      |
| gRPC client call           | `AddGrpcClientInstrumentation`          | `rpc.method`, `rpc.service`, `rpc.system=grpc`  |
| `SELECT`/`INSERT` etc      | `AddEntityFrameworkCoreInstrumentation` | `db.system=mysql`, `db.statement`               |

Health-check spans are excluded at the SDK level:

```csharp
.AddAspNetCoreInstrumentation(opts => {
    opts.Filter = ctx => ctx.Request.Path != "/healthz";
})
```

### Custom instrumentation (`DiagnosticsConfig.cs`)

| Instrument                    | Type          | Prometheus name                  | Description                                               |
| ----------------------------- | ------------- | -------------------------------- | --------------------------------------------------------- |
| `gateway.requests.inflight`   | UpDownCounter | `gateway_requests_inflight`      | Concurrent in-flight requests (decremented on response)   |
| `gateway.downstream.duration` | Histogram     | `gateway_downstream_duration_ms` | Per-downstream-service call latency, labeled by `service` |

The `gateway.fanout` span (`ActivityKind.Internal`) wraps the parallel downstream calls under a single parent so the trace waterfall shows the fan-out structure.

Exemplars are enabled via `OTEL_METRICS_EXEMPLAR_FILTER=trace_based` in the Deployment env. When `gateway.downstream.duration` is recorded while inside a sampled span, the SDK attaches `{traceId, spanId}` as an exemplar on the histogram bucket.

---

## Failure modes

| Scenario                                 | Behaviour                                 | Trace/log evidence                                                 |
| ---------------------------------------- | ----------------------------------------- | ------------------------------------------------------------------ |
| MySQL unavailable                        | Fail-fast at startup (`CrashLoopBackOff`) | Error in pod logs                                                  |
| order-api unreachable                    | gRPC `StatusCode.Unavailable` → HTTP 502  | Error span with `grpc.status_code`                                 |
| notification-svc Content-Type unexpected | `InvalidOperationException` → 500         | Error span with `exception.message`                                |
| Invalid order input                      | `HTTP 422 ValidationProblem`              | No error span (client error, not server fault)                     |
| `/api/error` called                      | `InvalidOperationException` thrown        | Error span, `otel.status_code=ERROR`, `exception.stacktrace` event |
| `/api/slow` called                       | 2–5s `Task.Delay`                         | Slow span, always retained by tail sampling                        |

---

## CORS

```csharp
builder.Services.AddCors(opts => opts.AddDefaultPolicy(policy =>
    policy.WithOrigins(corsOrigins)
          .AllowAnyMethod()
          .AllowAnyHeader()));
```

`corsOrigins` is read from `Cors:AllowedOrigins` config. Default: `http://localhost:4200`. In Kubernetes set via Deployment env var. Wildcard `*` is not allowed.

---

## Health probes

```yaml
livenessProbe:
  httpGet:
    path: /healthz
    port: 5000
  initialDelaySeconds: 30
  periodSeconds: 15
  timeoutSeconds: 5
  failureThreshold: 3
readinessProbe:
  httpGet:
    path: /healthz
    port: 5000
  initialDelaySeconds: 10
  periodSeconds: 10
```
