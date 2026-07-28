// ============================================================
// gateway-api — API Gateway / BFF
// ============================================================
// This service is the single entry-point for the Angular SPA.
// It owns the MySQL "Projects" aggregate and fans-out to:
//   • order-api via gRPC (server-streaming for order lists)
//   • notification-svc via plain HTTP
//
// OTel instrumentation validated here:
//   • ASP.NET Core automatic HTTP server spans
//   • EF Core → MySQL database spans (with statement text)
//   • gRPC client spans (with rpc.method / rpc.service attributes)
//   • HTTP client spans (for notification-svc proxy)
//   • Custom ActivitySource for business-logic spans
//   • UpDownCounter for in-flight requests (gauge)
//   • Histogram for per-downstream latency WITH exemplars
//   • Exemplar filter (TraceBased) → links metrics to traces
//   • JSON-structured stdout logging → Alloy log-tailing pipeline
// ============================================================

using GatewayApi.Data;
using GatewayApi.Endpoints;
using GatewayApi.Telemetry;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

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
    var podEntries = $"{podIp};{podIp}:5000";
    builder.Configuration["AllowedHosts"] = string.IsNullOrEmpty(allowedHosts) ? podEntries : $"{allowedHosts};{podEntries}";
}

// ── Database ─────────────────────────────────────────────────────────────────
// EF Core + Pomelo MySQL driver.
// Connection string sourced from appsettings.json or env override:
//   ConnectionStrings__DefaultConnection (K8s Deployment env var pattern)
var connStr = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connStr))
    throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required but not configured.");
// EnableRetryOnFailure covers transient connection blips mid-operation at the ORM
// layer. It does NOT cover ServerVersion.AutoDetect's own startup connection above —
// that still throws immediately if MySQL isn't reachable yet at boot.
builder.Services.AddDbContext<AppDbContext>(opts =>
    opts.UseMySql(connStr, ServerVersion.AutoDetect(connStr), mySqlOpts => mySqlOpts.EnableRetryOnFailure()));

// ── gRPC client → order-api ──────────────────────────────────────────────────
// Grpc.Net.Client wraps the generated stub with DI-managed channel lifecycle.
// The OTel gRPC client instrumentation adds rpc.system, rpc.service,
// rpc.method attributes and propagates the W3C traceparent via gRPC metadata
// automatically when AddGrpcClientInstrumentation() is called below.
var orderApiAddress = builder.Configuration["OrderApi:Address"] ?? "http://order-api:5002";
if (!Uri.TryCreate(orderApiAddress, UriKind.Absolute, out var orderApiUri) ||
    (orderApiUri.Scheme != "http" && orderApiUri.Scheme != "https"))
    throw new InvalidOperationException($"OrderApi:Address is not a valid absolute URI: '{orderApiAddress}'");
builder.Services.AddGrpcClient<OrderApi.Protos.OrderService.OrderServiceClient>(opts =>
{
    opts.Address = orderApiUri;
})
// Standard resilience pipeline: 3 retries with exponential back-off + circuit breaker.
// AddGrpcClient<T>() returns IHttpClientBuilder so the same HTTP resilience extension applies.
.AddStandardResilienceHandler();

// ── HTTP client → notification-svc ───────────────────────────────────────────
// Named client with base address set. The OTel HTTP client instrumentation
// adds http.url, http.method, http.status_code attributes and propagates
// W3C traceparent automatically.
var notificationAddress = builder.Configuration["NotificationSvc:Address"] ?? "http://notification-svc:8000";
if (!Uri.TryCreate(notificationAddress, UriKind.Absolute, out var notificationUri) ||
    (notificationUri.Scheme != "http" && notificationUri.Scheme != "https"))
    throw new InvalidOperationException($"NotificationSvc:Address is not a valid absolute URI: '{notificationAddress}'");
builder.Services.AddHttpClient("notification-svc", client =>
{
    client.BaseAddress = notificationUri;
    client.Timeout = TimeSpan.FromSeconds(10);
})
// Standard resilience pipeline: retries transient HTTP errors and open-circuits on
// sustained failures so a dead notification-svc doesn't saturate the thread pool.
.AddStandardResilienceHandler();

// ── CORS ──────────────────────────────────────────────────────────────────────
// Needed for the Angular dev server (localhost:4200) to call localhost:5000.
// In k3d, CORS is not an issue because all traffic goes through the Traefik
// ingress (same origin), but it's left on for local `ng serve` dev workflow.
var corsOrigins = (builder.Configuration["Cors:AllowedOrigins"] ?? "http://localhost:4200")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
builder.Services.AddCors(opts =>
    opts.AddDefaultPolicy(p => p
        .WithOrigins(corsOrigins)
        // Restrict to the HTTP methods the Angular SPA actually uses.
        .WithMethods("GET", "POST", "DELETE")
        // Content-Type is required for POST bodies; traceparent is injected by Faro.
        .WithHeaders("Content-Type", "traceparent", "tracestate")));

// ── OpenTelemetry ─────────────────────────────────────────────────────────────
// ResourceBuilder provides the service identity that appears on every span,
// metric, and log record. OTEL_RESOURCE_ATTRIBUTES env var is picked up by
// AddEnvironmentVariableDetector() and merges deployment.environment,
// service.namespace, and service.version without code changes per environment.
var resourceBuilder = ResourceBuilder.CreateDefault()
    .AddService(DiagnosticsConfig.ServiceName)
    .AddTelemetrySdk()
    .AddEnvironmentVariableDetector();

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .SetResourceBuilder(resourceBuilder)

        // Auto-instrument every incoming HTTP request.
        // Filter removes /healthz from traces — Alloy also does this at the
        // collector level, but this stops the span being created at all,
        // reducing export noise and SDK overhead.
        .AddAspNetCoreInstrumentation(opts =>
        {
            opts.Filter = ctx => ctx.Request.Path != "/healthz";
            // RecordException=true attaches exception.type, exception.message,
            // exception.stacktrace as span events on any unhandled exception,
            // making the /api/error endpoint trivially observable in Jaeger.
            opts.RecordException = true;

            // EnrichWithHttpRequest — ACA guideline: capture client identity and
            // route context on the root HTTP span.
            // In a real multi-tenant deployment, plant.id would be extracted from a
            // custom request header (e.g. X-Plant-Id) or JWT claim here.
            opts.EnrichWithHttpRequest = (activity, request) =>
            {
                // Client identity
                activity.SetTag("http.user_agent",
                    TruncateForSpanTag(request.Headers.UserAgent.ToString()));
                activity.SetTag("net.peer.ip",
                    request.HttpContext.Connection.RemoteIpAddress?.ToString());

                // Route template — populated after routing middleware resolves the endpoint.
                var routeData = request.HttpContext.GetRouteData();
                if (routeData?.Values.TryGetValue("action", out var action) == true)
                    activity.SetTag("http.route.action", action?.ToString());

                // Plant ID — read from custom header injected by the API gateway / WAF.
                // Leave empty in the lab; real services set this from X-Plant-Id or JWT.
                // Both this header and User-Agent above are fully client-controlled and
                // unauthenticated — truncated before landing on a span to bound
                // trace-storage cost / tag-index abuse from an arbitrarily long value.
                var plantId = request.Headers["X-Plant-Id"].FirstOrDefault();
                if (!string.IsNullOrEmpty(plantId))
                    activity.SetTag("plant.id", TruncateForSpanTag(plantId));
            };
        })

        // Auto-instrument every outbound HttpClient call.
        // This covers the proxy to notification-svc.
        .AddHttpClientInstrumentation()

        // Auto-instrument gRPC stubs (Grpc.Net.Client).
        // Adds rpc.system="grpc", rpc.service="orders.OrderService",
        // rpc.method="CreateOrder" etc. as span attributes.
        .AddGrpcClientInstrumentation()

        // Auto-instrument every EF Core command. SetDbStatementForText
        // captures the SQL (parameterized) as db.statement, essential for
        // diagnosing slow queries.
        .AddEntityFrameworkCoreInstrumentation(opts => opts.SetDbStatementForText = true)

        // Register our custom ActivitySource so manually created spans
        // (gateway.fanout, gateway.get_projects, etc.) are captured.
        .AddSource(DiagnosticsConfig.ServiceName)

        // Export to Alloy via OTLP/gRPC. Endpoint and protocol come from:
        //   OTEL_EXPORTER_OTLP_ENDPOINT (set in k8s Deployment env)
        //   OTEL_EXPORTER_OTLP_PROTOCOL (= grpc)
        .AddOtlpExporter())

    .WithMetrics(metrics => metrics
        .SetResourceBuilder(resourceBuilder)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        // Runtime metrics: GC, thread pool, heap — useful for correlating
        // latency spikes with GC pauses or thread starvation.
        .AddRuntimeInstrumentation()
        // Process metrics: CPU time, virtual/working-set memory, file descriptors.
        // ACA guideline: AddProcessInstrumentation() captures OS-level process health
        // alongside .NET runtime metrics, giving a complete picture in Grafana.
        .AddProcessInstrumentation()
        // Expose our custom UpDownCounter and Histogram instruments.
        .AddMeter(DiagnosticsConfig.ServiceName)

        // TraceBased exemplar filter: controlled via env var to avoid the
        // OTel .NET experimental SDK API dependency at compile time.
        // Set OTEL_METRICS_EXEMPLAR_FILTER=trace_based in the K8s Deployment
        // to link histogram data points to active trace spans.

        .AddOtlpExporter());

// ── Logging ───────────────────────────────────────────────────────────────────
// OTEL_LOGS_EXPORTER=none means we deliberately do NOT send logs via OTLP.
// Instead, services write structured JSON to stdout, which Alloy's
// loki.source.kubernetes pipeline tails, extracts TraceId/SpanId, and
// ships to Loki with structured metadata for trace correlation.
// This mirrors what you'd do at scale (log shipping via agent, not SDK).
builder.Logging.AddOpenTelemetry(logging =>
{
    logging.SetResourceBuilder(resourceBuilder);
    // IncludeFormattedMessage puts the final log string in the body field.
    logging.IncludeFormattedMessage = true;
    // IncludeScopes propagates structured scope values (e.g. RequestId from ASP.NET).
    logging.IncludeScopes = true;
    // ParseStateValues — ACA guideline: parse ILogger structured parameters into
    // individual OTel log record attributes instead of keeping them as a single
    // formatted string.  This makes {ProjectId}, {Count}, etc. queryable as
    // Loki structured metadata labels after Alloy extracts them.
    logging.ParseStateValues = true;
});
// JSON console output so Alloy can parse TraceId/SpanId from the log lines.
builder.Logging.AddJsonConsole();

// Caps a client-controlled header value before it lands on a span attribute.
static string? TruncateForSpanTag(string? value, int maxLength = 256) =>
    value is null || value.Length <= maxLength ? value : value[..maxLength];

var app = builder.Build();

app.UseCors();

// Run EF Core migrations / ensure schema on startup.
// In production you'd use a separate migration job, but this removes the
// "chicken and egg" ordering dependency in the k3d lab.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

// ── In-flight request counter ─────────────────────────────────────────────────
// An UpDownCounter incremented on request entry and decremented on exit.
// Unlike ASP.NET's built-in http.server.active_requests, this one is scoped
// to our ActivitySource so it lands in our custom meter and we control its name.
// /healthz is excluded — kubelet probe traffic shouldn't inflate the gauge.
const string HealthPath = "/healthz";
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path != HealthPath)
        DiagnosticsConfig.InflightRequests.Add(1);
    try
    {
        await next(ctx);
    }
    finally
    {
        if (ctx.Request.Path != HealthPath)
            DiagnosticsConfig.InflightRequests.Add(-1);
    }
});

app.MapProjectEndpoints();
app.MapOrderEndpoints();

app.Run();

// Required for WebApplicationFactory<Program> in integration tests.
public partial class Program { }
