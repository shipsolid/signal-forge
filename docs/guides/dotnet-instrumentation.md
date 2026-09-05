---
title: "Guide: .NET Instrumentation"
description: "Step-by-step: instrument an ASP.NET Core / gRPC .NET 8 service with OpenTelemetry — SDK wiring, custom spans and metrics, and RabbitMQ producer-side async trace propagation via the outbox pattern."
tags: ["ShipSolid", "Signal Forge", "Observability", "Guides", ".NET"]
updated: 2026-07-30
zettelId: "202607301400-03"
relations:
  - slug: projects/app-signal-forge/guides/README
    kind: related
  - slug: projects/app-signal-forge/guides/collector-pipeline-setup
    kind: depends_on
  - slug: projects/app-signal-forge/observability/otel-contracts
    kind: depends_on
  - slug: projects/app-signal-forge/architecture/adrs/adr-spanlink-for-async-rabbitmq
    kind: depends_on
---

## Guide: .NET Instrumentation

Prerequisite: [[collector-pipeline-setup|Collector & Pipeline Setup]] — you need a live OTLP
endpoint before any of this produces visible output.

This guide covers the pattern used across this project's two .NET 8 services: an ASP.NET Core
minimal-API HTTP service and a gRPC service, including the harder case — propagating trace context
across an asynchronous RabbitMQ hop using the outbox pattern. For the exact span/metric names this
produces, see [[otel-contracts|OTel Signal Contracts]].

### Step 1 — Install NuGet packages

```xml
<PackageReference Include="OpenTelemetry.Extensions.Hosting" Version="1.9.0" />
<PackageReference Include="OpenTelemetry.Instrumentation.AspNetCore" Version="1.9.0" />
<PackageReference Include="OpenTelemetry.Instrumentation.Http" Version="1.9.0" />
<PackageReference Include="OpenTelemetry.Instrumentation.GrpcNetClient" Version="1.9.0-beta.1" />
<PackageReference Include="OpenTelemetry.Instrumentation.EntityFrameworkCore" Version="1.0.0-beta.12" />
<PackageReference Include="OpenTelemetry.Instrumentation.Runtime" Version="1.9.0" />
<PackageReference Include="OpenTelemetry.Instrumentation.Process" Version="0.5.0-beta.6" />
<PackageReference Include="OpenTelemetry.Exporter.OpenTelemetryProtocol" Version="1.9.0" />
```

Only include `Instrumentation.Http`/`Instrumentation.GrpcNetClient` on services that make outbound
HTTP/gRPC calls, and `Instrumentation.EntityFrameworkCore` (plus your provider's own diagnostics
source, e.g. `Npgsql`) on services with a database. If your DB driver ships its own `ActivitySource`
rather than an official OTel instrumentation package — as Npgsql 8.x does, since it removed the
older `Npgsql.OpenTelemetry` package — register it directly by name (Step 3) rather than looking for
a NuGet package that no longer exists.

Note what's deliberately **absent**: no `OpenTelemetry.Exporter.Console` in production code, and no
OTLP log exporter package — logs are handled differently (Step 4).

### Step 2 — Centralize your ActivitySource and Meter

One static class per service. Name the `ActivitySource` and `Meter` identically to the service name
— this makes both land under the same `service_name` in your backend with no extra join logic:

```csharp
// Telemetry/DiagnosticsConfig.cs
public static class DiagnosticsConfig
{
    public const string ServiceName = "order-api";   // must match OTEL_SERVICE_NAME exactly — see Step 5
    public static readonly ActivitySource ActivitySource = new(ServiceName);
    public static readonly Meter Meter = new(ServiceName);

    public static readonly Counter<long> OrdersCreated =
        Meter.CreateCounter<long>("orders.created.total", unit: "{order}",
            description: "Total orders successfully created");

    public static readonly Histogram<double> ProcessingDuration =
        Meter.CreateHistogram<double>("orders.processing.duration", unit: "ms",
            description: "Time from request received to DB write commit");
}
```

**Cardinality rule for custom metrics**: never add a dimension that grows unboundedly (e.g.
`project_id`, `user_id`, `order_id`) as a metric label/tag. If you need per-entity drill-down, put
the ID on a **span attribute** instead (Step 6) and rely on trace-based exemplars (see
[[collector-pipeline-setup#Step 8 — Exemplars|Collector setup, Step 8]]) to jump from an aggregate
metric to a specific trace carrying that ID.

### Step 3 — Wire OpenTelemetry in `Program.cs`

```csharp
var resourceBuilder = ResourceBuilder.CreateDefault()
    .AddService(DiagnosticsConfig.ServiceName)
    .AddTelemetrySdk()
    .AddEnvironmentVariableDetector();   // picks up OTEL_RESOURCE_ATTRIBUTES / OTEL_SERVICE_NAME from env

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .SetResourceBuilder(resourceBuilder)
        .AddAspNetCoreInstrumentation(opts =>
        {
            opts.Filter = ctx => ctx.Request.Path != "/healthz";   // no span at all for health checks
            opts.RecordException = true;
            opts.EnrichWithHttpRequest = (activity, request) =>
            {
                activity.SetTag("net.peer.ip", request.HttpContext.Connection.RemoteIpAddress?.ToString());
                activity.SetTag("http.user_agent", Truncate(request.Headers.UserAgent.ToString(), 256));
            };
        })
        .AddHttpClientInstrumentation()          // if this service calls other HTTP services
        .AddGrpcClientInstrumentation()          // if this service calls other gRPC services
        .AddEntityFrameworkCoreInstrumentation(opts => opts.SetDbStatementForText = true)
        .AddSource("Npgsql")                      // driver-emitted ActivitySource name — adjust per DB driver
        .AddSource(DiagnosticsConfig.ServiceName)  // your own custom spans
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .SetResourceBuilder(resourceBuilder)
        .AddAspNetCoreInstrumentation()
        .AddRuntimeInstrumentation()
        .AddProcessInstrumentation()
        .AddMeter(DiagnosticsConfig.ServiceName)
        .AddOtlpExporter());
```

Truncate any client-controlled header value before setting it as a span tag (as in
`EnrichWithHttpRequest` above) — otherwise a malicious or misbehaving caller can inflate your
backend's tag/index storage with an unbounded string.

`.AddOtlpExporter()` here takes **no arguments** — endpoint, protocol, and headers all come from
standard `OTEL_EXPORTER_OTLP_*` environment variables (Step 5). This is what lets the exact same
binary run against a local dev collector, a Testcontainers-based test collector, and your production
Grafana Cloud pipeline with zero code changes.

### Step 4 — Structured JSON logging, correlated but not OTLP-exported

```csharp
builder.Logging.AddOpenTelemetry(logging =>
{
    logging.SetResourceBuilder(resourceBuilder);
    logging.IncludeFormattedMessage = true;
    logging.IncludeScopes = true;
    logging.ParseStateValues = true;   // turns {ProjectId} template params into structured attributes
});
builder.Logging.AddJsonConsole();      // this is what actually ships — stdout, picked up by node-level tailing
```

With `OTEL_LOGS_EXPORTER=none` (Step 5), the OTel logging provider never attaches an OTLP exporter —
only the JSON console sink does. This mirrors production log-shipping architecture (agent-based, not
per-process), matches
[[collector-pipeline-setup#Step 6 — Log-to-trace correlation|Collector Setup Step 6]], and means
your collector's log-tailing pipeline — not your application — is responsible for extracting
`TraceId`/`SpanId` from these JSON lines.

`ParseStateValues=true` gives OTel-formatted log records their trace/span IDs automatically, but the
**visible correlation ID in a plain-text log message is a convention you write by hand**, not
something the SDK injects for free:

```csharp
logger.LogInformation("Retrieved {Count} projects. TraceId: {TraceId}",
    projects.Count, Activity.Current?.TraceId.ToString());
```

Do this at your log call sites, not just at the collector — the raw log line itself should carry the
trace ID readably, even before it reaches your log backend.

### Step 5 — Environment variables, and the one gotcha to watch

```
OTEL_SERVICE_NAME=order-api
OTEL_EXPORTER_OTLP_ENDPOINT=http://grafana-k8s-alloy-receiver.monitoring.svc.cluster.local:4317
OTEL_EXPORTER_OTLP_PROTOCOL=grpc
OTEL_LOGS_EXPORTER=none
OTEL_METRICS_EXEMPLAR_FILTER=trace_based
OTEL_RESOURCE_ATTRIBUTES=service.namespace=my-app,service.version=1.0.0,deployment.environment=production
```

Get the `deployment.environment` value from the same single source across every service — see
[[collector-pipeline-setup#Step 7 — Keep deployment_environment consistent across signals|Collector Setup Step 7]]
for why this specific attribute needs extra care: it's re-derived independently by the collector for
each signal type, and if this service's env var and the collector's config ever disagree, Grafana
Cloud's Application Observability views can silently mis-file or split this service across
environments.

**Gotcha**: `OTEL_SERVICE_NAME` (env) and `DiagnosticsConfig.ServiceName` (code, via
`.AddService()`) are **two separate sources of truth for the same value**, and the env var wins if
they ever disagree — `.AddEnvironmentVariableDetector()` applies after `.AddService()`. Nothing
enforces they stay in sync; a rename on one side and not the other silently produces a service that
reports one name in resource attributes but was compiled expecting another. Keep them matched by
convention (a comment next to each, or a shared constant sourced from your CI/CD pipeline) rather
than assuming one will fail loudly if they drift.

There is no `OTEL_TRACES_SAMPLER` set here — sampling is a **collector-side, tail-based** decision
(see
[[collector-pipeline-setup#Step 11 — Known gap: no sampling in the chart-only path|Collector Setup Step 11]]),
not a head-based decision in the SDK. Every span this service creates is exported; what gets _kept_
downstream is the collector's call.

### Step 6 — Custom spans

Start spans on your `ActivitySource`, set tags **before** the operation that might throw (so the tag
survives even if the span ends in error), and use `ActivityKind` deliberately:

```csharp
using var activity = DiagnosticsConfig.ActivitySource.StartActivity("order.create", ActivityKind.Internal);
activity?.SetTag("order.project_id", request.ProjectId);
activity?.SetTag("order.amount", request.Amount);

try
{
    var order = await _db.Orders.AddAsync(new Order { ... });
    await _db.SaveChangesAsync();
    activity?.SetTag("order.id", order.Id);
}
catch (Exception ex)
{
    activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
    activity?.RecordException(ex);
    throw;
}
```

Use `SetStatus(ActivityStatusCode.Error, ...)` explicitly for _business_ errors that don't throw (a
404, a validation failure) — auto-instrumentation only marks a span as an error when an exception
actually propagates through it.

### Step 7 — Cross-service header propagation beyond W3C trace context

`AddHttpClientInstrumentation()`/`AddGrpcClientInstrumentation()` automatically inject/extract the
W3C `traceparent` header — you don't write any code for that. But **no other header propagates
automatically**. If your system has a custom cross-cutting header (a tenant ID, a request-scoped
correlation value beyond the trace ID), you must forward it by hand at each transport boundary:

```csharp
// A header arriving over HTTP does not automatically appear in an outbound gRPC call's metadata.
internal static class GrpcCallContextExtensions
{
    public static Grpc.Core.Metadata WithTenantId(this HttpContext httpContext)
    {
        var metadata = new Grpc.Core.Metadata();
        var tenantId = httpContext.Request.Headers["X-Tenant-Id"].FirstOrDefault();
        if (!string.IsNullOrEmpty(tenantId)) metadata.Add("X-Tenant-Id", tenantId);
        return metadata;
    }
}

// call site
var response = await orderClient.CreateOrderAsync(request, httpContext.WithTenantId());
```

The receiving service's own `EnrichWithHttpRequest`/gRPC interceptor then reads that same header
name back off its own incoming context and sets it as a span tag, so it flows through as
observability context even though it isn't part of the W3C trace standard.

### Step 8 — Async propagation across RabbitMQ (producer side, outbox pattern)

This is the hardest part of the whole pattern, and the part worth the most care. The scenario: your
HTTP/gRPC handler writes something to a database and needs to publish an event about it to RabbitMQ
— but you don't want publish success or failure to affect the caller's response, and you don't want
a slow/unavailable broker to block or fail the request.

**The outbox pattern separates "durably record the intent to publish" from "actually publish."**

1. **In the original request handler**, inside the _same database transaction_ as your business
   write, also write an outbox row capturing the current trace context:

   ```csharp
   using var activity = DiagnosticsConfig.ActivitySource.StartActivity("order.create", ActivityKind.Internal);
   // ... business logic, order written ...
   _db.OutboxMessages.Add(new OutboxMessage
   {
       Payload = JsonSerializer.Serialize(orderCreatedEvent),
       TraceParent = Activity.Current?.Id,   // capture now, while it's still valid
       ProcessedAt = null,
   });
   await _db.SaveChangesAsync();   // order + outbox row commit atomically
   ```

   The request returns as soon as this commits — publishing hasn't happened yet, and its outcome
   can't affect this response.

2. **A background worker** polls for unprocessed outbox rows and publishes them later, running in a
   completely different execution context — there is no `Activity.Current` connecting back to the
   original request anymore. Reconstruct the original trace context manually from the stored value:

   ```csharp
   var parentContext = default(ActivityContext);
   var links = Array.Empty<ActivityLink>();

   if (!string.IsNullOrEmpty(msg.TraceParent) &&
       ActivityContext.TryParse(msg.TraceParent, traceState: null, isRemote: true, out var parsedContext))
   {
       parentContext = parsedContext;
       links = new[] { new ActivityLink(parsedContext) };
   }

   using var activity = DiagnosticsConfig.ActivitySource.StartActivity(
       "outbox.relay", ActivityKind.Internal, parentContext, links: links);
   ```

   `ActivityContext.TryParse(traceparent, traceState: null, isRemote: true, out ctx)` is the key API
   for resuming a W3C trace context outside the flow that created it. Passing it as both
   `parentContext` **and** as an `ActivityLink` means this span shares the original `traceId` while
   also carrying an explicit link back to where it came from — useful when a message gets retried
   and produces multiple relay attempts for one original request.

3. **Inject into the RabbitMQ message headers** using the _stored_ traceparent value, not
   `Activity.Current` (which at this point belongs to `outbox.relay`'s own, different span, not the
   original request):

   ```csharp
   using var publishActivity = DiagnosticsConfig.ActivitySource.StartActivity(
       "order.publish", ActivityKind.Producer);
   publishActivity?.SetTag("messaging.system", "rabbitmq");
   publishActivity?.SetTag("messaging.destination", "orders");
   publishActivity?.SetTag("messaging.rabbitmq.routing_key", "order.created");

   var props = channel.CreateBasicProperties();
   props.Headers = new Dictionary<string, object>();
   if (!string.IsNullOrEmpty(msg.TraceParent))
       props.Headers["traceparent"] = Encoding.UTF8.GetBytes(msg.TraceParent);   // bytes — see below

   channel.BasicPublish(exchange: "orders", routingKey: "order.created",
       basicProperties: props, body: Encoding.UTF8.GetBytes(msg.Payload));
   ```

   **Write the header value as UTF-8 bytes, not a string.** Most non-.NET AMQP client libraries
   (e.g. Python's `pika`) deliver header values as raw bytes; writing bytes on the publish side
   avoids a decode mismatch on the consumer side. See the consumer half of this exchange in the
   [[python-instrumentation#Step 8 — RabbitMQ consumer: manual propagation|Python guide, Step 8]].

4. On successful publish, mark the outbox row processed. On failure, leave it unprocessed and record
   the exception on the `outbox.relay` span — it gets retried on the worker's next poll, and this
   failure never touches the original request's trace or response.

### Step 9 — Test the propagation logic without a collector

For fast, deterministic unit tests of the context-reconstruction logic in Step 8, use a bare
`System.Diagnostics.ActivityListener` — no OTel SDK or exporter needed:

```csharp
string? observedTraceId = null;
using var listener = new ActivityListener
{
    ShouldListenTo = source => source.Name == DiagnosticsConfig.ServiceName,
    Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
    ActivityStarted = a => { if (a.OperationName == "outbox.relay") observedTraceId = a.TraceId.ToString(); },
};
ActivitySource.AddActivityListener(listener);

// ... run the relay worker against a stored TraceParent ...

Assert.Equal(expectedTraceId, observedTraceId);
```

Reserve a full Testcontainers-based test (a real broker, a real trace backend, real consumer
container) for one comprehensive end-to-end regression test of this exact propagation path — it's
expensive to run but catches the class of bug where the pieces work in isolation but the trace still
ends up disconnected in practice.

### Step 10 — Verify

1. Call an instrumented endpoint and confirm a span appears in your trace backend with the
   `ActivityKind` and tags you expect.
2. Confirm resolved env vars in a running pod:

   ```bash
   kubectl exec deploy/<your-service> -- env | grep OTEL
   ```

3. For the async path: publish an order-creation request, then search your trace backend for a trace
   containing all of `order.create`, `outbox.relay`, and `order.publish` sharing one `traceId` —
   this confirms Step 8's context reconstruction actually worked end to end, not just in a unit
   test.

Next: [[python-instrumentation|Python Instrumentation]] for the consumer side of this exact RabbitMQ
hop, or [[frontend-rum-instrumentation|Frontend RUM Instrumentation]].
