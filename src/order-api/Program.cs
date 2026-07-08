// ============================================================
// order-api — gRPC Order Service
// ============================================================
// This service owns the PostgreSQL "Orders" aggregate and exposes
// a gRPC server.  It is called by gateway-api and publishes events
// to RabbitMQ for the notification-svc to consume.
//
// OTel instrumentation validated here:
//   • gRPC server spans (rpc.system, rpc.service, rpc.method attributes)
//   • EF Core → PostgreSQL spans via Npgsql OTel instrumentation
//   • Custom ActivitySource for order.create / order.publish spans
//   • ActivityKind.Producer span on RabbitMQ publish with W3C traceparent
//     injected into message headers (see OrderPublisher.cs for detail)
//   • Counter instruments: orders.created.total, orders.amount.total
//   • Histogram with exemplars: orders.processing.duration
//   • JSON-structured stdout logging → Alloy log-tailing pipeline
// ============================================================

using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OrderApi.Data;
using OrderApi.Messaging;
using OrderApi.Services;
using OrderApi.Telemetry;

var builder = WebApplication.CreateBuilder(args);

// kubelet's HTTP probes connect using the pod's own IP as the Host header —
// AllowedHosts (appsettings.json) can't list that ahead of time since pod IPs
// are ephemeral and only known once scheduled. MY_POD_IP is injected via the
// K8s Downward API (fieldRef: status.podIP); append it here so probes pass
// without widening AllowedHosts to "*". Must happen before Build() — the host
// filtering middleware's options are bound from configuration at that point.
// Both the bare IP and IP:port form are required — confirmed empirically
// against a live k3d cluster (HostFilteringMiddleware debug log) that the
// probe's Host header includes the port ("10.42.0.45:5000") and a bare-IP
// allow-list entry does NOT match it despite ASP.NET Core's docs describing
// bare entries as port-agnostic; that appears to hold for hostnames but not
// for IP-literal entries.
var podIp = Environment.GetEnvironmentVariable("MY_POD_IP");
if (!string.IsNullOrEmpty(podIp))
{
    // HostFilteringOptionsSetup splits AllowedHosts on ';', not ',' — confirmed
    // by reading Microsoft.AspNetCore.Hosting's internal ParseHosts. A comma
    // here silently produces a single unmatched entry and every request 400s,
    // including ones against hosts already in the list.
    var allowedHosts = builder.Configuration["AllowedHosts"];
    var podEntries = $"{podIp};{podIp}:5001";
    builder.Configuration["AllowedHosts"] = string.IsNullOrEmpty(allowedHosts) ? podEntries : $"{allowedHosts};{podEntries}";
}

// ── Database ─────────────────────────────────────────────────────────────────
// Npgsql is the PostgreSQL driver.  UseNpgsql() configures EF Core to use it.
// The connection string format for Npgsql:
//   Host=<host>;Database=<db>;Username=<user>;Password=<pw>
// This differs from Pomelo MySQL's semicolon-separated key=value format.
var connStr = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connStr))
    throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required but not configured.");
// EnableRetryOnFailure covers a transient connection blip mid-operation at the
// ORM layer — fail-fast at startup (above) already handles a missing connection
// string; this handles a connection that drops after the app is already running.
builder.Services.AddDbContext<AppDbContext>(opts =>
    opts.UseNpgsql(connStr, npgsqlOpts => npgsqlOpts.EnableRetryOnFailure()));

// ── gRPC server ───────────────────────────────────────────────────────────────
// AddGrpc() registers the gRPC middleware stack (HTTP/2 framing, Protobuf
// serialisation, deadline propagation).  The actual service class is
// registered below with MapGrpcService<OrderGrpcService>().
//
// Two dedicated ports, not one shared port:
//   5001 — HTTP/1.1 only. Serves /healthz for kubelet's liveness/readiness
//          probes, which speak plain HTTP/1.1 with no ALPN and no upgrade —
//          they cannot negotiate HTTP/2 no matter how the endpoint is configured.
//   5002 — HTTP/2 only (h2c, cleartext prior-knowledge). Serves gRPC exclusively.
//
// This used to be one port with Kestrel's default mixed Http1AndHttp2 protocol
// selection, on the assumption (stated in this comment previously) that ASP.NET
// Core enables working h2c automatically for that combination without TLS.
// That assumption was wrong: confirmed empirically (both via a live k3d
// cluster and a bare `dotnet run`, no Docker/k8s involved) that a non-TLS
// Http1AndHttp2 endpoint logs "HTTP/2 requires TLS application protocol
// negotiation" and silently downgrades every connection to HTTP/1.1 —
// including gRPC clients sending the HTTP/2 prior-knowledge preface, which
// then fail with an HTTP_1_1_REQUIRED error. Splitting gRPC onto its own
// HTTP/2-only port is the standard fix for gRPC+REST coexisting on cleartext
// Kestrel without TLS. gateway-api's OrderApi:Address now points at 5002.
builder.Services.AddGrpc();
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(5001, lo => lo.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1);
    options.ListenAnyIP(5002, lo => lo.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2);
});

// ── Messaging ─────────────────────────────────────────────────────────────────
// OrderPublisher is a singleton (one shared RabbitMQ connection / channel).
// IDisposable is handled by the DI container on app shutdown.
//
// OutboxRelayWorker polls the outbox table every 5s and publishes pending
// messages to RabbitMQ.  Registering as a hosted service means it starts
// automatically with the app and is cancelled gracefully on SIGTERM.
builder.Services.AddSingleton<IOrderPublisher, OrderPublisher>();
builder.Services.AddHostedService<OutboxRelayWorker>();

// ── OpenTelemetry ─────────────────────────────────────────────────────────────
var resourceBuilder = ResourceBuilder.CreateDefault()
    .AddService(DiagnosticsConfig.ServiceName)
    .AddTelemetrySdk()
    .AddEnvironmentVariableDetector();

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .SetResourceBuilder(resourceBuilder)
        .AddAspNetCoreInstrumentation(opts =>
        {
            // /healthz probes from kubelet produce no traces.
            opts.Filter = ctx => ctx.Request.Path != "/healthz";
            opts.RecordException = true;

            // EnrichWithHttpRequest — ACA guideline: capture caller identity on
            // every gRPC server span.  order-api is an internal service called only
            // by gateway-api, but capturing peer.ip and user-agent lets you confirm
            // in Jaeger exactly which gateway pod issued the call.
            opts.EnrichWithHttpRequest = (activity, request) =>
            {
                activity.SetTag("net.peer.ip",
                    request.HttpContext.Connection.RemoteIpAddress?.ToString());
                activity.SetTag("http.user_agent",
                    TruncateForSpanTag(request.Headers.UserAgent.ToString()));

                // Plant ID — forwarded by gateway-api from the original browser request
                // (see GrpcCallContextExtensions.PlantIdMetadata in gateway-api). Both this
                // header and User-Agent above are fully client-controlled and unauthenticated
                // — truncated before landing on a span to bound trace-storage cost / tag-index
                // abuse from an arbitrarily long value.
                var plantId = request.Headers["X-Plant-Id"].FirstOrDefault();
                if (!string.IsNullOrEmpty(plantId))
                    activity.SetTag("plant.id", TruncateForSpanTag(plantId));
            };
        })
        // EF Core auto-instrumentation captures every SQL command sent to PostgreSQL.
        // SetDbStatementForText=true adds db.statement with the actual SQL.
        .AddEntityFrameworkCoreInstrumentation(opts => opts.SetDbStatementForText = true)
        // Npgsql 8.x built-in tracing: register the "Npgsql" ActivitySource directly.
        // In Npgsql 8.x the Npgsql.OpenTelemetry package AddNpgsql() API was removed;
        // the driver emits spans under the "Npgsql" source automatically.
        .AddSource("Npgsql")
        // Register the custom ActivitySource used in OrderGrpcService and
        // OrderPublisher for business-logic spans.
        .AddSource(DiagnosticsConfig.ServiceName)
        .AddOtlpExporter())

    .WithMetrics(metrics => metrics
        .SetResourceBuilder(resourceBuilder)
        .AddAspNetCoreInstrumentation()
        .AddRuntimeInstrumentation()
        // Process metrics: CPU time, virtual/working-set memory, file descriptors.
        // ACA guideline: AddProcessInstrumentation() for OS-level process health metrics.
        .AddProcessInstrumentation()
        .AddMeter(DiagnosticsConfig.ServiceName)
        // TraceBased exemplar filter set via OTEL_METRICS_EXEMPLAR_FILTER env var
        // (see K8s deployment) to avoid the OTel .NET experimental SDK API dependency.
        .AddOtlpExporter());

// ── Logging ───────────────────────────────────────────────────────────────────
builder.Logging.AddOpenTelemetry(logging =>
{
    logging.SetResourceBuilder(resourceBuilder);
    logging.IncludeFormattedMessage = true;
    logging.IncludeScopes = true;
    // ParseStateValues — ACA guideline: emit structured log parameters as
    // individual OTel log record attributes (OrderId, ProjectId, Amount, etc.)
    // so Alloy can index them as Loki structured metadata.
    logging.ParseStateValues = true;
});
// JSON-structured console output consumed by Alloy's log-tailing pipeline.
// Fields written: Timestamp, Level, MessageTemplate, TraceId, SpanId, etc.
builder.Logging.AddJsonConsole();

// Caps a client-controlled header value before it lands on a span attribute.
static string? TruncateForSpanTag(string? value, int maxLength = 256) =>
    value is null || value.Length <= maxLength ? value : value[..maxLength];

var app = builder.Build();

// Ensure DB schema exists on startup. EnsureCreated() creates tables based
// on the EF Core model if they don't exist. It does NOT run migrations — for
// a lab this is fine; use Migrate() or a separate migration job in production.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

// Register the gRPC service implementation.
app.MapGrpcService<OrderGrpcService>();

// Health endpoint for kubelet readinessProbe.
// Excluded from traces by the filter above to keep Jaeger clean.
app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }));

app.Run();
