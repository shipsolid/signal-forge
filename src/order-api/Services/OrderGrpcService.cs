// ============================================================
// OrderGrpcService — gRPC service implementation
// ============================================================
// Implements the three RPCs defined in orders.proto:
//   • CreateOrder  — unary: write DB + publish to RabbitMQ
//   • GetOrdersByProject — server-streaming: stream rows from DB
//   • GetOrder     — unary: single row lookup
//
// OTel trace shape for CreateOrder (the most important call):
//
//   gateway-api: orders.OrderService/CreateOrder (gRPC client, Kind=CLIENT)
//     └─ order-api: orders.OrderService/CreateOrder (gRPC server, Kind=SERVER)
//          └─ order-api: order.create (custom, Kind=INTERNAL)
//               ├─ order-api: db.postgresql (EF Core INSERT via Npgsql)
//               └─ order-api: order.publish (custom, Kind=PRODUCER)
//                    ┄┄(async via RabbitMQ)┄┄
//                    notification-svc: notification.process (Kind=CONSUMER)
//
// The gRPC server span is created automatically by AddAspNetCoreInstrumentation()
// which hooks into the gRPC middleware.  The rpc.system, rpc.service, rpc.method
// attributes are set by the ASP.NET Core + gRPC OTel integration.
// ============================================================

using System.Diagnostics;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using OrderApi.Data;
using OrderApi.Models;
using OrderApi.Protos;
using OrderApi.Telemetry;

namespace OrderApi.Services;

public class OrderGrpcService : Protos.OrderService.OrderServiceBase
{
    private const string OrderStatusCreated = "Created";

    private readonly AppDbContext _db;
    private readonly ILogger<OrderGrpcService> _logger;

    public OrderGrpcService(AppDbContext db, ILogger<OrderGrpcService> logger)
    {
        _db = db;
        _logger = logger;
    }

    // ── CreateOrder ──────────────────────────────────────────────────────────
    // Full span chain: gRPC server → order.create (custom) → DB write → publish.
    //
    // The Stopwatch here measures the time from start of the custom span to
    // just after the RabbitMQ publish.  This value is recorded on the
    // orders.processing.duration histogram and (because it runs inside a
    // sampled trace) carries an exemplar linking the metric point to this trace.
    public override async Task<CreateOrderResponse> CreateOrder(
        CreateOrderRequest request,
        ServerCallContext context)
    {
        if (request.ProjectId <= 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "ProjectId must be a positive integer."));
        if (request.Amount <= 0 || request.Amount > 999_999.99)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Amount must be between 0.01 and 999999.99."));
        if (string.IsNullOrWhiteSpace(request.Description) || request.Description.Length > 500)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Description is required and must be 500 characters or fewer."));

        var sw = Stopwatch.StartNew();

        // Custom span wrapping the full business operation (DB write + publish).
        // The gRPC server span is the PARENT; this is a CHILD.
        using var activity = DiagnosticsConfig.ActivitySource.StartActivity("order.create");
        activity?.SetTag("order.project_id", request.ProjectId);
        activity?.SetTag("order.amount", request.Amount);

        var order = new Order
        {
            ProjectId = request.ProjectId,
            Description = request.Description,
            Amount = (decimal)request.Amount,
            Status = OrderStatusCreated,
            CreatedAt = DateTime.UtcNow
        };

        // Write order + outbox message in a single SaveChanges call (one DB transaction).
        // If this pod crashes after this point, OutboxRelayWorker will pick up the
        // unprocessed OutboxMessage on restart — guaranteeing at-least-once delivery.
        _db.Orders.Add(order);
        var outboxMsg = new OutboxMessage
        {
            Order = order,
            // Capture traceparent now (inside the request span) so OutboxRelayWorker
            // can inject the original trace context into the RabbitMQ message headers,
            // even though it runs outside the request's Activity context.
            TraceParent = Activity.Current?.Id,
            CreatedAt = DateTime.UtcNow
        };
        _db.OutboxMessages.Add(outboxMsg);

        // SaveChangesAsync emits an EF Core span (child of order.create) that
        // contains the INSERT SQL and its execution time.
        await _db.SaveChangesAsync(context.CancellationToken);

        // Set the generated ID on the span after the DB write assigns it.
        activity?.SetTag("order.id", order.Id);

        sw.Stop();

        // Record the processing duration as a histogram observation.
        // project_id is a dimension so you can analyse per-project latency.
        DiagnosticsConfig.ProcessingDuration.Record(sw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("project_id", request.ProjectId));

        // Increment the orders counter and running total.
        // These counters are additive — calling rate() on them in PromQL
        // gives you orders per second per project.
        DiagnosticsConfig.OrdersCreated.Add(1,
            new KeyValuePair<string, object?>("project_id", request.ProjectId));
        DiagnosticsConfig.OrdersAmount.Add(request.Amount,
            new KeyValuePair<string, object?>("project_id", request.ProjectId));

        _logger.LogInformation(
            "Created order {OrderId} for project {ProjectId}, amount {Amount}. TraceId: {TraceId}",
            order.Id, order.ProjectId, order.Amount, Activity.Current?.TraceId.ToString());

        return new CreateOrderResponse { OrderId = order.Id, Status = order.Status };
    }

    // ── GetOrdersByProject ───────────────────────────────────────────────────
    // Server-streaming RPC: writes each order as a separate gRPC message.
    //
    // The single gRPC server span stays open for the full streaming duration.
    // EF Core loads all rows into memory first (ToListAsync), then we stream
    // them.  In production with large result sets you'd use AsAsyncEnumerable()
    // to avoid buffering, but for the lab this is clear and simple.
    public override async Task GetOrdersByProject(
        GetOrdersByProjectRequest request,
        IServerStreamWriter<OrderResponse> responseStream,
        ServerCallContext context)
    {
        using var activity = DiagnosticsConfig.ActivitySource.StartActivity("order.get_by_project");
        activity?.SetTag("order.project_id", request.ProjectId);

        var orders = _db.Orders
            .Where(o => o.ProjectId == request.ProjectId)
            .OrderByDescending(o => o.CreatedAt)
            .AsAsyncEnumerable();

        int count = 0;
        await foreach (var order in orders.WithCancellation(context.CancellationToken))
        {
            await responseStream.WriteAsync(MapToResponse(order), context.CancellationToken);
            count++;
        }

        _logger.LogInformation(
            "Streamed {Count} orders for project {ProjectId}. TraceId: {TraceId}",
            count, request.ProjectId, Activity.Current?.TraceId.ToString());
    }

    // ── GetOrder ─────────────────────────────────────────────────────────────
    // Simple unary lookup.  Throws RpcException with StatusCode.NotFound if
    // the order does not exist — the gRPC server middleware converts this to
    // a gRPC status code and also records it on the server span as an error.
    public override async Task<OrderResponse> GetOrder(GetOrderRequest request, ServerCallContext context)
    {
        using var activity = DiagnosticsConfig.ActivitySource.StartActivity("order.get");
        activity?.SetTag("order.id", request.OrderId);

        var order = await _db.Orders.FindAsync(new object[] { request.OrderId }, context.CancellationToken);
        if (order is null)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "Order not found");
            throw new RpcException(new Status(StatusCode.NotFound, $"Order {request.OrderId} not found"));
        }
        return MapToResponse(order);
    }

    // ── Mapping helper ────────────────────────────────────────────────────────
    private static OrderResponse MapToResponse(Order o) => new()
    {
        Id = o.Id,
        ProjectId = o.ProjectId,
        Description = o.Description,
        Amount = (double)o.Amount,
        Status = o.Status,
        CreatedAt = o.CreatedAt.ToString("O")
    };
}
