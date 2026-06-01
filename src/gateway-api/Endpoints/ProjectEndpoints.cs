// ============================================================
// ProjectEndpoints — CRUD for Projects + order list proxy
// ============================================================
// Validates:
//   • EF Core + MySQL auto-instrumented spans (db.system=mysql, db.statement)
//   • Custom span attributes (project.id as semantic business attribute)
//   • gRPC server-streaming span: GetOrdersByProject returns a stream
//     of OrderResponse messages; the span stays open until the stream closes
//   • gateway.fanout custom span wrapping parallel downstream calls
//   • Downstream latency histogram with exemplars
// ============================================================

using GatewayApi.Data;
using GatewayApi.Models;
using GatewayApi.Telemetry;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Trace;
using System.Diagnostics;
using Microsoft.AspNetCore.Routing;

namespace GatewayApi.Endpoints;

public static class ProjectEndpoints
{
    public static void MapProjectEndpoints(this WebApplication app)
    {
        // TracingEndpointFilter applied to the group — ACA equivalent of TracingActionFilter.
        // Creates a child "endpoint.{method}" span under every route in this group.
        var group = app.MapGroup("/api/projects")
                       .AddEndpointFilter<TracingEndpointFilter>();

        group.MapGet("/", GetProjects);
        group.MapGet("/{id:int}", GetProject);
        group.MapPost("/", CreateProject);
        group.MapDelete("/{id:int}", DeleteProject);
        group.MapGet("/{id:int}/orders", GetOrdersByProject);
    }

    // ── GET /api/projects ────────────────────────────────────────────────────
    // OTel trace: ASP.NET Core span (HTTP GET /api/projects) → child EF Core
    // span (SELECT * FROM Projects) → child MySQL network span.
    // The EF Core instrumentation captures db.system=mysql, db.statement
    // (the SQL), db.name=gatewaydb.
    static async Task<IResult> GetProjects(AppDbContext db, ILogger<Program> logger)
    {
        using var activity = DiagnosticsConfig.ActivitySource.StartActivity("gateway.get_projects");
        var projects = await db.Projects.OrderByDescending(p => p.CreatedAt).ToListAsync();

        // Structured log with TraceId injected manually so Alloy's
        // loki.process stage can extract it as Loki structured metadata,
        // enabling "Logs for this span" from Grafana Explore.
        logger.LogInformation("Retrieved {Count} projects. TraceId: {TraceId}", projects.Count,
            Activity.Current?.TraceId.ToString());
        return Results.Ok(projects);
    }

    // ── GET /api/projects/{id} ───────────────────────────────────────────────
    // Demonstrates business-level span attributes: project.id is a domain
    // concept, not an HTTP attribute. Adding it makes traces searchable by
    // project in Jaeger's tag-based search.
    static async Task<IResult> GetProject(int id, AppDbContext db, ILogger<Program> logger)
    {
        using var activity = DiagnosticsConfig.ActivitySource.StartActivity("gateway.get_project");
        // Set domain attribute BEFORE the DB call so it appears on the span
        // even if an exception is thrown during the query.
        activity?.SetTag("project.id", id);

        var project = await db.Projects.FindAsync(id);
        if (project is null)
        {
            // Mark the span as ERROR so it's kept by the tail sampler's
            // "errors-always" policy and appears in Jaeger 100% of the time.
            activity?.SetStatus(ActivityStatusCode.Error, "Project not found");
            return Results.NotFound();
        }
        logger.LogInformation("Retrieved project {ProjectId}. TraceId: {TraceId}", id,
            Activity.Current?.TraceId.ToString());
        return Results.Ok(project);
    }

    // ── POST /api/projects ───────────────────────────────────────────────────
    // After SaveChangesAsync() EF Core has assigned the auto-increment Id.
    // We set project.id on the span after the write — this is intentional:
    // before the write the Id is 0 (not yet known).
    static async Task<IResult> CreateProject(Project project, AppDbContext db, ILogger<Program> logger)
    {
        using var activity = DiagnosticsConfig.ActivitySource.StartActivity("gateway.create_project");
        project.CreatedAt = DateTime.UtcNow;
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        activity?.SetTag("project.id", project.Id);
        logger.LogInformation("Created project {ProjectId} '{Name}'. TraceId: {TraceId}", project.Id, project.Name,
            Activity.Current?.TraceId.ToString());
        return Results.Created($"/api/projects/{project.Id}", project);
    }

    // ── DELETE /api/projects/{id} ────────────────────────────────────────────
    static async Task<IResult> DeleteProject(int id, AppDbContext db, ILogger<Program> logger)
    {
        using var activity = DiagnosticsConfig.ActivitySource.StartActivity("gateway.delete_project");
        activity?.SetTag("project.id", id);

        var project = await db.Projects.FindAsync(id);
        if (project is null)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "Project not found");
            return Results.NotFound();
        }
        db.Projects.Remove(project);
        await db.SaveChangesAsync();
        logger.LogInformation("Deleted project {ProjectId}. TraceId: {TraceId}", id,
            Activity.Current?.TraceId.ToString());
        return Results.NoContent();
    }

    // ── GET /api/projects/{id}/orders ────────────────────────────────────────
    // KEY VALIDATION POINT: gRPC server-streaming + downstream latency histogram.
    //
    // The gRPC call is wrapped in a "gateway.fanout" custom span. This span
    // starts BEFORE the gRPC call, so the gRPC client span becomes a child of
    // it. In Jaeger the waterfall shows:
    //
    //   gateway-api: HTTP GET /api/projects/{id}/orders
    //     └─ gateway-api: gateway.fanout
    //          └─ gateway-api: orders.OrderService/GetOrdersByProject (gRPC client)
    //               └─ order-api: orders.OrderService/GetOrdersByProject (gRPC server)
    //                    └─ order-api: db.postgresql (EF Core SELECT)
    //
    // The AddGrpcClientInstrumentation() call in Program.cs handles trace
    // propagation automatically: it injects traceparent into the gRPC metadata
    // (`:path`, `:authority` headers) so the server-side span becomes a child.
    static async Task<IResult> GetOrdersByProject(
        int id,
        OrderApi.Protos.OrderService.OrderServiceClient orderClient,
        ILogger<Program> logger)
    {
        // ActivityKind.Client: this span initiates an outbound gRPC call to order-api.
        using var fanout = DiagnosticsConfig.ActivitySource.StartActivity("gateway.fanout", ActivityKind.Client);
        fanout?.SetTag("project.id", id);

        var sw = Stopwatch.StartNew();
        try
        {
            var request = new OrderApi.Protos.GetOrdersByProjectRequest { ProjectId = id };
            var orders = new List<object>();

            // ReadAllAsync() streams responses one-by-one.
            // The gRPC client span remains open for the full stream duration.
            using var call = orderClient.GetOrdersByProject(request);
            await foreach (var order in call.ResponseStream.ReadAllAsync())
            {
                orders.Add(new
                {
                    order.Id,
                    order.ProjectId,
                    order.Description,
                    order.Amount,
                    order.Status,
                    order.CreatedAt
                });
            }

            sw.Stop();
            // Record duration with dimension labels. Because this runs inside
            // the "gateway.fanout" span (which is sampled), the ExemplarFilter
            // will attach traceId + spanId to this histogram observation.
            DiagnosticsConfig.DownstreamDuration.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("downstream", "order-api"),
                new KeyValuePair<string, object?>("operation", "GetOrdersByProject"));

            logger.LogInformation("Retrieved {Count} orders for project {ProjectId}. TraceId: {TraceId}",
                orders.Count, id, Activity.Current?.TraceId.ToString());
            return Results.Ok(orders);
        }
        catch (Exception ex)
        {
            sw.Stop();
            // SetStatus + RecordException is the OTel convention for error spans.
            // RecordException creates a span event with exception.type,
            // exception.message, exception.stacktrace attributes.
            fanout?.SetStatus(ActivityStatusCode.Error, ex.Message);
            fanout?.RecordException(ex);
            logger.LogError(ex, "Failed to get orders for project {ProjectId}. TraceId: {TraceId}", id,
                Activity.Current?.TraceId.ToString());
            return Results.Problem("Failed to retrieve orders", statusCode: 502);
        }
    }
}
