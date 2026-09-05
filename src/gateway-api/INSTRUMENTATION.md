# gateway-api — OpenTelemetry Instrumentation

`gateway-api` is the single entry point for the Angular SPA: it owns the MySQL `Projects` aggregate
and fans out to `order-api` over gRPC and to `notification-svc` over plain HTTP. Every "Create
Order" click funnels through this service, which makes it the one place in SignalForge where a
request has to stay coherent across two different downstream protocols at once.

This doc covers the instrumentation **as it exists today** — it is already fully implemented, not a
proposal — and closes with one concrete gap worth fixing.

## Why

`gateway-api` carries the richest custom-span/metric surface in the repo because it's the
convergence point: a single inbound request produces a trace that must stay linked through an
HTTP-instrumented call (`notification-svc`) _and_ a gRPC-instrumented call (`order-api`) — two
different auto-propagation mechanisms exercised from the same handler. Auto-instrumentation alone
gets you HTTP/gRPC/EF Core spans; the custom spans and metrics exist specifically to make the
fan-out itself observable (which downstream call was slow, how many requests are in flight, where
time went before/after the gRPC hop).

The design choices below aren't ad hoc — they follow standing repo-wide decisions recorded as ADRs:

| Decision                                                                       | ADR     | Why it matters here                                                                                                                                          |
| ------------------------------------------------------------------------------ | ------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| Logs ship via stdout tailing, not OTLP                                         | ADR-001 | Log delivery doesn't depend on the OTLP exporter's health/backpressure — a stalled Alloy connection can't block request logging.                             |
| K8s attributes (`k8s.pod.name`, etc.) are added by the collector, not app code | ADR-009 | Keeps `Program.cs` Kubernetes-agnostic — no dependency on the Downward API for span enrichment.                                                              |
| `spanmetrics` connector runs _before_ `tail_sampling`                          | ADR-003 | RED metrics (used by the `SignalForgeGatewayLatencyHigh` burn-rate alert) reflect 100% of traffic, not just the ~25% of traces actually sampled for storage. |
| Single Helm-managed Alloy topology                                             | ADR-004 | One collector pipeline, avoiding duplicate-span problems from a second hand-rolled agent.                                                                    |

Business payoff: a slow `/api/projects/{id}/orders` call can be traced from browser click through
gRPC server-streaming to the Postgres query on `order-api`'s side, the exact downstream call at
fault is visible as an exemplar dot on `gateway.downstream.duration`, and none of that depends on
the app knowing it's running in Kubernetes.

## How — current implementation

### Packages (`gateway-api.csproj:11-33`)

| Package                                             | Version                                                                   |
| --------------------------------------------------- | ------------------------------------------------------------------------- |
| `OpenTelemetry`                                     | 1.9.0                                                                     |
| `OpenTelemetry.Extensions.Hosting`                  | 1.9.0                                                                     |
| `OpenTelemetry.Instrumentation.AspNetCore`          | 1.9.0                                                                     |
| `OpenTelemetry.Instrumentation.Http`                | 1.9.0                                                                     |
| `OpenTelemetry.Instrumentation.GrpcNetClient`       | 1.9.0-beta.1                                                              |
| `OpenTelemetry.Instrumentation.EntityFrameworkCore` | 1.0.0-beta.12                                                             |
| `OpenTelemetry.Instrumentation.Runtime`             | 1.9.0                                                                     |
| `OpenTelemetry.Instrumentation.Process`             | 0.5.0-beta.6                                                              |
| `OpenTelemetry.Exporter.OpenTelemetryProtocol`      | 1.9.0                                                                     |
| `Pomelo.EntityFrameworkCore.MySql`                  | 8.0.2 (pulls `MySqlConnector` transitively — no direct package reference) |

### SDK bootstrap (`Program.cs:117-235`)

Resource identity (`Program.cs:122-125`):

```csharp
var resourceBuilder = ResourceBuilder.CreateDefault()
    .AddService(DiagnosticsConfig.ServiceName)   // "gateway-api"
    .AddTelemetrySdk()
    .AddEnvironmentVariableDetector();            // picks up OTEL_RESOURCE_ATTRIBUTES
```

**Tracing** (`Program.cs:128-192`): `AddAspNetCoreInstrumentation` (excludes `/healthz`,
`RecordException = true`, and an `EnrichWithHttpRequest` callback that tags `http.user_agent`,
`net.peer.ip`, `http.route.action`, and `plant.id` — each value truncated to 256 chars since they're
client-controlled, `Program.cs:150-167, 238-239`) → `AddHttpClientInstrumentation` →
`AddGrpcClientInstrumentation` →
`AddEntityFrameworkCoreInstrumentation(opts => opts.SetDbStatementForText = true)` →
`AddSource(DiagnosticsConfig.ServiceName)` (registers the custom spans below) → `AddOtlpExporter()`.

**Metrics** (`Program.cs:194-213`): AspNetCore + HttpClient + Runtime + Process instrumentation,
`AddMeter(DiagnosticsConfig.ServiceName)` for the custom instruments, `AddOtlpExporter()`. Exemplars
are enabled via the `OTEL_METRICS_EXEMPLAR_FILTER=trace_based` **env var**, deliberately not in code
— the comment at `Program.cs:208-212` notes this avoids depending on an experimental SDK API at
compile time.

**Logging** (`Program.cs:215-235`): `builder.Logging.AddOpenTelemetry(...)` sets
`IncludeFormattedMessage`, `IncludeScopes`, `ParseStateValues`, but `OTEL_LOGS_EXPORTER=none` (set
in the shared ConfigMap, see below) means this provider never actually ships anything via OTLP.
`builder.Logging.AddJsonConsole()` is what actually reaches Alloy — structured JSON on stdout,
tailed and correlated by trace ID at the collector (ADR-001).

### Custom instrumentation

| Signal | Name                                                                                            | Type                                | Where                                                                                                      | Notes                                                                                                                             |
| ------ | ----------------------------------------------------------------------------------------------- | ----------------------------------- | ---------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------- |
| Trace  | `gateway.fanout`                                                                                | Span (`Internal` or `Client`)       | `ProjectEndpoints.cs:142`, `OrderEndpoints.cs:43,105,144`                                                  | Wraps each downstream call; tags `project.id` / `order.project_id` / `order.id`                                                   |
| Trace  | `gateway.get_projects` / `get_project` / `create_project` / `delete_project` / `slow` / `error` | Span                                | `ProjectEndpoints.cs`, `OrderEndpoints.cs`                                                                 | One per handler                                                                                                                   |
| Trace  | `endpoint.{method}`                                                                             | Span                                | `TracingEndpointFilter.cs:43`                                                                              | Child of the ASP.NET Core root span; tags `http.route.template`, `http.method`, `http.target`                                     |
| Metric | `gateway.requests.inflight`                                                                     | `UpDownCounter<long>` (`{request}`) | `DiagnosticsConfig.cs:53-57`, incremented/decremented in `Program.cs:260-273`                              | Excludes `/healthz`                                                                                                               |
| Metric | `gateway.downstream.duration`                                                                   | `Histogram<double>` (`ms`)          | `DiagnosticsConfig.cs:74-78`, recorded in `ProjectEndpoints.cs:181-183`, `OrderEndpoints.cs:63-65,172-174` | Labels `downstream` (`order-api`/`notification-svc`), `operation`; carries exemplars                                              |
| Log    | Structured `ILogger` → JSON console                                                             | Log record                          | every handler                                                                                              | Message templates interpolate `Activity.Current?.TraceId` manually, since the OTel logging provider isn't the thing shipping logs |

`DiagnosticsConfig` (`Telemetry/DiagnosticsConfig.cs`) centralizes the `ActivitySource` and `Meter`
— both named `"gateway-api"`, matching `OTEL_SERVICE_NAME`, so manual spans and auto-instrumented
spans appear under the same instrumentation scope in Tempo/Jaeger.

### Propagation

- **Inbound**: `AddAspNetCoreInstrumentation` auto-extracts the W3C `traceparent` header as the root
  span's parent. In practice this is where a Grafana Faro browser span becomes the trace root — the
  frontend's `TracingInstrumentation` injects `traceparent`/`tracestate` on every fetch to
  gateway-api's base URL, and gateway-api's CORS policy explicitly allow-lists both headers
  (`Program.cs:107-115`).
- **Outbound to `order-api` (gRPC)**: `AddGrpcClientInstrumentation` auto-injects
  `traceparent`/`tracestate` into gRPC metadata — no manual code needed.
- **Outbound to `notification-svc` (HTTP)**: `AddHttpClientInstrumentation` auto-injects the same
  headers into the outbound `HttpClient` request.
- **The one hand-written exception**: `X-Plant-Id` is a business/tenant identity header, unrelated
  to trace context, that does **not** auto-forward into gRPC calls the way HTTP headers do into HTTP
  calls. `GrpcCallContextExtensions.PlantIdMetadata()` (`Endpoints/GrpcCallContext.cs:9-16`)
  manually copies it from the inbound request into outbound gRPC metadata so `order-api`'s own
  `EnrichWithHttpRequest` callback can tag it on its spans too.

### Deployment wiring

Env vars come from two places, both consumed by `k8s/app/gateway/deployment.yaml`:

- Shared ConfigMap `signal-forge-app-env` (`k8s/infra/app-env.yaml.tmpl:23-29`, rendered from
  `conf.yml` by `deploy-local.sh`, also consumed by `order-api` and `notification-svc` so the three
  Deployments can't drift):
  - `OTEL_EXPORTER_OTLP_ENDPOINT=http://grafana-k8s-alloy-receiver.monitoring.svc.cluster.local:4317`
  - `OTEL_EXPORTER_OTLP_PROTOCOL=grpc`
  - `OTEL_LOGS_EXPORTER=none`
  - `OTEL_METRICS_EXEMPLAR_FILTER=trace_based`
  - `OTEL_RESOURCE_ATTRIBUTES=service.namespace=...,service.version=1.0.0,deployment.environment=...`
- Deployment-specific (`k8s/app/gateway/deployment.yaml:84-85`): `OTEL_SERVICE_NAME=gateway-api` —
  this **overrides** the code's `AddService(DiagnosticsConfig.ServiceName)` call, and a comment at
  that line flags the two must be kept in sync manually.

### Where the signals land

`alloy-receiver` (OTLP gRPC :4317) → `k8sattributes` processor enriches with pod/namespace metadata
(ADR-009 — done at the collector, not in-app) → `transform` stamps `deployment.environment` →
`filter` drops any surviving `/healthz` spans → `spanmetrics` connector derives RED metrics from
100% of traffic (ADR-003, before sampling) → `tail_sampling` (errors 100%, requests >2s 100%,
everything else 25%) → Tempo (cloud mode) / Jaeger (local mode).

## What changes are needed

### 1. Register MySqlConnector's native ActivitySource (concrete, low-risk)

`order-api` explicitly registers `.AddSource("Npgsql")` (`order-api/Program.cs:150`) because Npgsql
8.x emits its own driver-level ActivitySource and no longer ships the old `AddNpgsql()` OTel helper.
`gateway-api` has no equivalent call for its MySQL driver.

This isn't a guess — the built `MySqlConnector.dll` (`bin/Release/net8.0/`) was inspected directly
and contains an `ActivitySourceHelper` plus the literal string `"MySqlConnector"`, confirming
MySqlConnector emits its own native ActivitySource, exactly parallel to Npgsql's case, that
`gateway-api` currently isn't capturing.

**Fix**: add one line next to the existing custom-source registration in `Program.cs`:

```csharp
.AddSource("MySqlConnector")
.AddSource(DiagnosticsConfig.ServiceName)   // Program.cs:187 — existing
```

`AddEntityFrameworkCoreInstrumentation` (`Program.cs:183`) already covers EF Core-level spans,
including the SQL statement text. This addition surfaces the lower-level driver spans MySqlConnector
emits itself (connection-pool acquisition, protocol-level activity) that EF Core's instrumentation
doesn't produce — additive, not a replacement, and safe to add without touching anything else.

### 2. Frontend/backend `deployment.environment` naming asymmetry (observation, not a gateway-api change)

Grafana Faro's `app.environment` on the frontend is `'local'`/`'production'` (driven by Angular's
own build flag, `src/frontend/src/app/telemetry/faro.ts`), while every backend span carries
`deployment_environment=signal-forge-dev` (from `conf.yml`'s `monitoring.deployment_environment`).
Same trace, two different environment-attribute names _and_ values on either side of the
browser/backend boundary. Not something to fix in `gateway-api`'s own code — flagging it here as a
cross-cutting follow-up since it surfaced while tracing gateway-api's propagation path.

## References

- [`Program.cs`](Program.cs), [`Telemetry/DiagnosticsConfig.cs`](Telemetry/DiagnosticsConfig.cs),
  [`Telemetry/TracingEndpointFilter.cs`](Telemetry/TracingEndpointFilter.cs),
  [`Endpoints/GrpcCallContext.cs`](Endpoints/GrpcCallContext.cs),
  [`Endpoints/ProjectEndpoints.cs`](Endpoints/ProjectEndpoints.cs),
  [`Endpoints/OrderEndpoints.cs`](Endpoints/OrderEndpoints.cs),
  [`gateway-api.csproj`](gateway-api.csproj)
- [`../../k8s/app/gateway/deployment.yaml`](../../k8s/app/gateway/deployment.yaml),
  [`../../k8s/infra/app-env.yaml.tmpl`](../../k8s/infra/app-env.yaml.tmpl)
- ADRs (canonical source: `architecture/adrs/` on the
  [platform notes site](https://shipsolid.github.io/signal-forge/architecture/adrs/)):
  ADR-001 (log tailing, not OTLP export), ADR-003 (spanmetrics before tail sampling), ADR-004
  (Helm-managed Alloy stack), ADR-009 (k8s attribute enrichment at the collector)
