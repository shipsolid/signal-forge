using GatewayApi.Telemetry;
using Grpc.Core;
using OpenTelemetry.Trace;
using OrderApi.Protos;
using OrderContracts;
using System.Diagnostics;
using OrderServiceClient = OrderApi.Protos.OrderService.OrderServiceClient;

namespace GatewayApi.Endpoints;

public static class OrderEndpoints
{
    public static void MapOrderEndpoints(this WebApplication app)
    {
        // TracingEndpointFilter — ACA TracingActionFilter equivalent for Minimal API.
        // Applied individually (no group here) so /healthz is excluded from tracing.
        app.MapPost("/api/orders", CreateOrder).AddEndpointFilter<TracingEndpointFilter>();
        app.MapGet("/api/orders/{id:int}", GetOrder).AddEndpointFilter<TracingEndpointFilter>();
        app.MapGet("/api/notifications", GetNotifications).AddEndpointFilter<TracingEndpointFilter>();
        app.MapGet("/api/slow", SlowEndpoint).AddEndpointFilter<TracingEndpointFilter>();
        app.MapGet("/api/error", ErrorEndpoint).AddEndpointFilter<TracingEndpointFilter>();
        app.MapGet("/healthz", HealthCheck);
    }

    static async Task<IResult> CreateOrder(
        CreateOrderDto dto,
        OrderServiceClient orderClient,
        HttpContext httpContext,
        ILogger<Program> logger)
    {
        if (dto.ProjectId <= 0)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            { ["projectId"] = ["ProjectId must be a positive integer."] });
        if (dto.Amount <= 0 || dto.Amount > OrderLimits.MaxAmount)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            { ["amount"] = [$"Amount must be between 0.01 and {OrderLimits.MaxAmount}."] });
        if (string.IsNullOrWhiteSpace(dto.Description) || dto.Description.Length > OrderLimits.MaxDescriptionLength)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            { ["description"] = [$"Description is required and must be {OrderLimits.MaxDescriptionLength} characters or fewer."] });

        // This is a business-operation wrapper around the generated gRPC client
        // span, not a replacement for it. ActivityKind.Client makes the gateway
        // fanout visible as outbound work while the instrumentation supplies the
        // protocol-specific rpc.* attributes on its nested span.
        using var activity = DiagnosticsConfig.ActivitySource.StartActivity("gateway.fanout", ActivityKind.Client);
        activity?.SetTag("order.project_id", dto.ProjectId);

        // Generated once per logical request, before the resilience-wrapped call below, so a
        // Polly retry after a connection reset (server already committed the write) replays
        // the same key instead of minting a new order-api row for the same client intent.
        var idempotencyKey = Guid.NewGuid().ToString("N");

        var sw = Stopwatch.StartNew();
        try
        {
            var response = await orderClient.CreateOrderAsync(new CreateOrderRequest
            {
                ProjectId = dto.ProjectId,
                Description = dto.Description,
                Amount = dto.Amount,
                IdempotencyKey = idempotencyKey
            }, httpContext.PlantIdMetadata());

            sw.Stop();
            DiagnosticsConfig.DownstreamDuration.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("downstream", "order-api"),
                new KeyValuePair<string, object?>("operation", "CreateOrder"));

            activity?.SetTag("order.id", response.OrderId);
            logger.LogInformation("Created order {OrderId} for project {ProjectId}. TraceId: {TraceId}",
                response.OrderId, dto.ProjectId, Activity.Current?.TraceId.ToString());

            return Results.Created($"/api/orders/{response.OrderId}", new
            {
                id = response.OrderId,
                status = response.Status
            });
        }
        catch (RpcException ex)
        {
            sw.Stop();
            activity?.SetStatus(ActivityStatusCode.Error, ex.Status.Detail);
            activity?.AddException(ex);
            logger.LogError(ex, "order-api rejected CreateOrder for project {ProjectId} with {GrpcStatus}. TraceId: {TraceId}",
                dto.ProjectId, ex.StatusCode, Activity.Current?.TraceId.ToString());
            return ex.ToProblem("Failed to create order");
        }
        catch (Exception ex)
        {
            sw.Stop();
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
            logger.LogError(ex, "Failed to create order for project {ProjectId}. TraceId: {TraceId}",
                dto.ProjectId, Activity.Current?.TraceId.ToString());
            return Results.Problem("Failed to create order", statusCode: 502);
        }
    }

    // Completes the passthrough CreateOrder's `Location: /api/orders/{id}` header points at —
    // GetOrder is fully implemented and tested on order-api but had no REST caller until now.
    static async Task<IResult> GetOrder(
        int id,
        OrderServiceClient orderClient,
        HttpContext httpContext,
        ILogger<Program> logger)
    {
        using var activity = DiagnosticsConfig.ActivitySource.StartActivity("gateway.fanout", ActivityKind.Client);
        activity?.SetTag("order.id", id);

        try
        {
            var response = await orderClient.GetOrderAsync(new GetOrderRequest { OrderId = id }, httpContext.PlantIdMetadata());
            return Results.Ok(new
            {
                id = response.Id,
                projectId = response.ProjectId,
                description = response.Description,
                amount = response.Amount,
                status = response.Status,
                createdAt = response.CreatedAt
            });
        }
        catch (RpcException ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Status.Detail);
            activity?.AddException(ex);
            logger.LogError(ex, "order-api rejected GetOrder {OrderId} with {GrpcStatus}. TraceId: {TraceId}",
                id, ex.StatusCode, Activity.Current?.TraceId.ToString());
            return ex.ToProblem("Failed to retrieve order");
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
            logger.LogError(ex, "Failed to get order {OrderId}. TraceId: {TraceId}",
                id, Activity.Current?.TraceId.ToString());
            return Results.Problem("Failed to retrieve order", statusCode: 502);
        }
    }

    static async Task<IResult> GetNotifications(
        IHttpClientFactory httpClientFactory,
        ILogger<Program> logger)
    {
        // ActivityKind.Client: this span initiates an outbound HTTP call to notification-svc.
        using var activity = DiagnosticsConfig.ActivitySource.StartActivity("gateway.fanout", ActivityKind.Client);

        var sw = Stopwatch.StartNew();
        try
        {
            var client = httpClientFactory.CreateClient("notification-svc");
            var response = await client.GetAsync("/notifications");
            response.EnsureSuccessStatusCode();

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType != "application/json")
                throw new InvalidOperationException(
                    $"Unexpected Content-Type from notification-svc: '{contentType}'");

            // Guard against unexpectedly large responses from notification-svc.
            // 1 MB is well above what 100 notification records should ever produce.
            const long MaxBodyBytes = 1 * 1024 * 1024;
            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength.HasValue && contentLength.Value > MaxBodyBytes)
                throw new InvalidOperationException(
                    $"Response from notification-svc exceeds size limit ({contentLength.Value} bytes)");

            var body = await response.Content.ReadAsStringAsync();
            if (body.Length > MaxBodyBytes)
                throw new InvalidOperationException(
                    $"Response from notification-svc exceeds size limit ({body.Length} chars)");

            sw.Stop();
            DiagnosticsConfig.DownstreamDuration.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("downstream", "notification-svc"),
                new KeyValuePair<string, object?>("operation", "GetNotifications"));

            logger.LogInformation("Fetched notifications. TraceId: {TraceId}", Activity.Current?.TraceId.ToString());
            return Results.Content(body, "application/json");
        }
        // notification-svc returned a well-formed 4xx (not a connectivity/5xx failure) —
        // that's a real client-facing status, not a "bad gateway" condition.
        catch (HttpRequestException ex) when (ex.StatusCode is { } code && (int)code is >= 400 and < 500)
        {
            sw.Stop();
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
            logger.LogError(ex, "notification-svc returned {StatusCode} while fetching notifications. TraceId: {TraceId}",
                (int)code, Activity.Current?.TraceId.ToString());
            return Results.Problem("Failed to retrieve notifications", statusCode: (int)code);
        }
        catch (Exception ex)
        {
            sw.Stop();
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
            logger.LogError(ex, "Failed to get notifications. TraceId: {TraceId}", Activity.Current?.TraceId.ToString());
            return Results.Problem("Failed to retrieve notifications", statusCode: 502);
        }
    }

    static async Task<IResult> SlowEndpoint(ILogger<Program> logger)
    {
        using var activity = DiagnosticsConfig.ActivitySource.StartActivity("gateway.slow");
        var delayMs = Random.Shared.Next(2000, 5001);
        activity?.SetTag("delay.ms", delayMs);
        await Task.Delay(delayMs);
        logger.LogInformation("Slow endpoint completed after {DelayMs}ms. TraceId: {TraceId}",
            delayMs, Activity.Current?.TraceId.ToString());
        return Results.Ok(new { delay_ms = delayMs, message = "Slow response" });
    }

    static IResult ErrorEndpoint(ILogger<Program> logger)
    {
        using var activity = DiagnosticsConfig.ActivitySource.StartActivity("gateway.error");
        var ex = new InvalidOperationException("Intentional error for OTel validation");
        activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        activity?.AddException(ex);
        logger.LogError(ex, "Error endpoint triggered intentionally. TraceId: {TraceId}",
            Activity.Current?.TraceId.ToString());
        throw ex;
    }

    static IResult HealthCheck() => Results.Ok(new { status = "healthy" });
}

// Amount is double to mirror orders.proto's wire type — see that file's comment
// for why this is an accepted lab-scale tradeoff rather than an oversight.
public record CreateOrderDto(int ProjectId, string Description, double Amount);
