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
| `OrderApi__Address`                    | Deployment env                                | Yes      | gRPC endpoint for order-api (`http://order-api:5002`) |
| `Services__NotificationSvc`            | Deployment env                                | Yes      | HTTP endpoint for notification-svc                    |
| `Cors__AllowedOrigins`                 | Deployment env                                | No       | CORS origins (default: `http://localhost:4200`)       |
| `OTEL_SERVICE_NAME`                    | Deployment env                                | Yes      | `gateway-api`                                         |
| `OTEL_EXPORTER_OTLP_ENDPOINT`          | Deployment env                                | Yes      | `http://grafana-k8s-alloy-receiver.monitoring:4317`   |

`AllowedHosts` in `appsettings.json` is set to
`gateway-api;gateway-api.otel-lab.svc.cluster.local;signal-forge.local;localhost;127.0.0.1` — not
wildcard. **The delimiter is `;`, not `,`.** ASP.NET Core's internal
`HostFilteringOptionsSetup.ParseHosts` splits the config string on `;` only; a comma-separated list
parses as a single, unmatched entry and every request 400s — including ones against hosts that look
like they're already in the list. This cost real debugging time (see git history around this
comment) before being traced to that one delimiter. That list covers every legitimate caller: the
two Service DNS forms (pod-to-pod), the TLS ingress hostname, and `localhost`/`127.0.0.1` for the
hostless dev ingress rule and direct `curl`/port-forward access. kubelet's liveness/readiness probes
would otherwise fail this check — they connect using the pod's own (ephemeral) IP as the `Host`
header, which can't be listed ahead of time. Pinning the probe's own `httpHeaders: [{name: Host,
value: gateway-api}]` was tried first and doesn't work — confirmed empirically against a live k3d
cluster, kubelet's Host override never reached ASP.NET Core's host filtering and every probe still
400'd. The fix that actually works: `MY_POD_IP` is injected via the Downward API (`fieldRef:
status.podIP`), and `Program.cs` appends it (both bare-IP and `IP:port` forms, `;`-joined) to the
configured `AllowedHosts` before `Build()` runs, so every pod allow-lists exactly its own IP at
startup. order-api has no external exposure at all, so its `AllowedHosts` is narrower:
`order-api;order-api.otel-lab.svc.cluster.local` (plus the same `MY_POD_IP` mechanism).

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

## Resilience

Both downstream clients — the gRPC channel to order-api and the `HttpClient` to notification-svc —
are wrapped in `.AddStandardResilienceHandler()` (Microsoft.Extensions.Http.Resilience), configured
with library defaults, not tuned in this repo:

```csharp
builder.Services.AddGrpcClient<OrderApi.Protos.OrderService.OrderServiceClient>(opts =>
{
    opts.Address = orderApiUri;
})
.AddStandardResilienceHandler();   // retry w/ exponential backoff, then circuit-breaks

builder.Services.AddHttpClient("notification-svc", client =>
{
    client.BaseAddress = notificationUri;
    client.Timeout = TimeSpan.FromSeconds(10);
})
.AddStandardResilienceHandler();
```

This means a transient order-api or notification-svc blip is retried automatically before it ever
surfaces to the browser; a sustained outage opens the circuit so gateway-api stops hammering a
downstream that's already down, rather than piling up threads waiting on it. The explicit 10s
`client.Timeout` on the notification-svc `HttpClient` is separate from — and tighter than — the
resilience handler's own per-attempt timeout.

See [docs/operations/resilience-patterns.md](../operations/resilience-patterns.md) for how this
fits alongside the other services' retry/backoff/circuit-breaker patterns, and what isn't covered
(no automated fault-injection test exercises this path today).

---

## Failure modes

| Scenario                                 | Behaviour                                 | Trace/log evidence                                                 |
| ---------------------------------------- | ----------------------------------------- | ------------------------------------------------------------------ |
| MySQL unavailable                        | Fail-fast at startup (`CrashLoopBackOff`) | Error in pod logs                                                  |
| order-api RpcException (any endpoint)    | Mapped per gRPC status — see table below, not a blanket 502 | Error span with `grpc.status_code`                  |
| notification-svc Content-Type unexpected | `InvalidOperationException` → 502 (falls through to the generic catch) | Error span with `exception.message`  |
| Invalid order input                      | `HTTP 422 ValidationProblem`              | No error span (client error, not server fault)                     |
| `/api/error` called                      | `InvalidOperationException` thrown        | Error span, `otel.status_code=ERROR`, `exception.stacktrace` event |
| `/api/slow` called                       | 2–5s `Task.Delay`                         | Slow span, always retained by tail sampling                        |

`CreateOrder`, `GetOrder`, and `GetOrdersByProject` all route `RpcException` through the shared
`GrpcErrorMapping.ToProblem()` extension
([Endpoints/GrpcErrorMapping.cs](../../src/gateway-api/Endpoints/GrpcErrorMapping.cs)) instead of
each catching `Exception` and returning a flat 502:

| gRPC status                                           | HTTP status | Detail relayed to client?                                       |
| ------------------------------------------------------ | ----------- | ------------------------------------------------------------------ |
| `InvalidArgument`, `FailedPrecondition`, `OutOfRange`  | 400         | Yes — order-api only sets these to caller-safe validation text |
| `Unauthenticated`                                      | 401         | Yes                                                                 |
| `PermissionDenied`                                     | 403         | Yes                                                                 |
| `NotFound`                                             | 404         | Yes                                                                 |
| `AlreadyExists`, `Aborted`                             | 409         | Yes                                                                 |
| `ResourceExhausted`                                    | 429         | Yes                                                                 |
| `Unimplemented`                                        | 501         | No — generic message                                                |
| `Unavailable`                                          | 503         | No — generic message                                                |
| `DeadlineExceeded`                                     | 504         | No — generic message                                                |
| `Internal`, `Unknown`, `DataLoss`, `Cancelled`         | 502         | No — generic message                                                |

A non-`RpcException` failure (e.g. a connection reset before any gRPC status was ever set) still
falls through to a flat 502.

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
