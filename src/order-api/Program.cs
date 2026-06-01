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

// ── Database ─────────────────────────────────────────────────────────────────
// Npgsql is the PostgreSQL driver.  UseNpgsql() configures EF Core to use it.
// The connection string format for Npgsql:
//   Host=<host>;Database=<db>;Username=<user>;Password=<pw>
// This differs from Pomelo MySQL's semicolon-separated key=value format.
var connStr = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connStr))
    throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required but not configured.");
builder.Services.AddDbContext<AppDbContext>(opts =>
    opts.UseNpgsql(connStr));

// ── gRPC server ───────────────────────────────────────────────────────────────
// AddGrpc() registers the gRPC middleware stack (HTTP/2 framing, Protobuf
// serialisation, deadline propagation).  The actual service class is
// registered below with MapGrpcService<OrderGrpcService>().
//
// Important: gRPC requires HTTP/2.  ASP.NET Core enables HTTP/2 by default
// on non-TLS listeners when ASPNETCORE_URLS uses "http://" without a port
// configured for HTTPS.  In the K8s Deployment we set:
//   ASPNETCORE_URLS=http://+:5001
// which configures HTTP/1.1 + HTTP/2 cleartext (h2c) on port 5001.
// gateway-api calls this with http:// so Grpc.Net.Client uses h2c automatically.
builder.Services.AddGrpc();

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
                    request.Headers.UserAgent.ToString());

                // Plant ID — forwarded by gateway-api from the original browser request.
                var plantId = request.Headers["X-Plant-Id"].FirstOrDefault();
                if (!string.IsNullOrEmpty(plantId))
                    activity.SetTag("plant.id", plantId);
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
