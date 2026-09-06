// ============================================================
// OrderGrpcService — gRPC service implementation
// ============================================================
// Implements the three RPCs defined in orders.proto:
//   • CreateOrder  — unary: atomically write Order + OutboxMessage to PostgreSQL
//   • GetOrdersByProject — server-streaming: stream rows from DB
//   • GetOrder     — unary: single row lookup
//
// OTel trace shape for CreateOrder (the most important call):
//
//   gateway-api: orders.OrderService/CreateOrder (gRPC client, Kind=CLIENT)
//     └─ order-api: orders.OrderService/CreateOrder (gRPC server, Kind=SERVER)
//          └─ order-api: order.create (custom, Kind=INTERNAL)
//               └─ order-api: db.postgresql (EF Core INSERT of Order + OutboxMessage
//                             in one atomic SaveChangesAsync unit)
//
// The RabbitMQ publish occurs later when OutboxRelayWorker polls the committed
// row. It restores OutboxMessage.TraceParent as the parent context for
// outbox.relay and order.publish, then also adds an ActivityLink to represent
// the asynchronous boundary. As a result, a trace query can show
// order.create -> outbox.relay -> order.publish -> notification processing,
// while retry attempts remain separately observable sibling spans. See
// OutboxRelayWorker.cs for the at-least-once and retry trade-offs.
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
using OrderContracts;

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
    // Full span chain: gRPC server → order.create (custom) → DB write only.
    // Publish is NOT part of this call anymore (see the file header comment
    // and OutboxRelayWorker.cs) — CreateOrder returns once the Order +
    // OutboxMessage row are committed.
    //
    // The Stopwatch here measures the time from start of the custom span to
    // just after the DB write commits.  This value is recorded on the
    // orders.processing.duration histogram and (because it runs inside a
    // sampled trace) carries an exemplar linking the metric point to this trace.
    public override async Task<CreateOrderResponse> CreateOrder(
        CreateOrderRequest request,
        ServerCallContext context)
    {
        if (request.ProjectId <= 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "ProjectId must be a positive integer."));
        if (request.Amount <= 0 || request.Amount > OrderLimits.MaxAmount)
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"Amount must be between 0.01 and {OrderLimits.MaxAmount}."));
        if (string.IsNullOrWhiteSpace(request.Description) || request.Description.Length > OrderLimits.MaxDescriptionLength)
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"Description is required and must be {OrderLimits.MaxDescriptionLength} characters or fewer."));

        var sw = Stopwatch.StartNew();

        // Custom span wraps the synchronous business operation: Order + outbox
        // persistence. RabbitMQ delivery is deliberately deferred to the relay
        // so broker availability cannot split a committed order from its event.
        using var activity = DiagnosticsConfig.ActivitySource.StartActivity("order.create");
        activity?.SetTag("order.project_id", request.ProjectId);
        activity?.SetTag("order.amount", request.Amount);

        // Empty proto string ("unset") maps to null so legacy/direct callers that don't send a
        // key never collide with each other under the unique index below (see AppDbContext).
        var idempotencyKey = string.IsNullOrEmpty(request.IdempotencyKey) ? null : request.IdempotencyKey;

        if (idempotencyKey is not null)
        {
            // Fast path: a resilience-handler retry after the first attempt already committed
            // (e.g. client saw a connection reset post-commit) replays the same key. Checking
            // first covers the common sequential-retry case directly; the unique index +
            // catch below is the defense-in-depth backstop for a genuine concurrent race.
            var replay = await _db.Orders.FirstOrDefaultAsync(
                o => o.IdempotencyKey == idempotencyKey, context.CancellationToken);
            if (replay is not null)
            {
                _logger.LogInformation(
                    "CreateOrder replay detected for idempotency key {IdempotencyKey} — returning existing order {OrderId}.",
                    idempotencyKey, replay.Id);
                return new CreateOrderResponse { OrderId = replay.Id, Status = replay.Status };
            }
        }

        var order = new Order
        {
            ProjectId = request.ProjectId,
            Description = request.Description,
            Amount = (decimal)request.Amount,
            Status = OrderStatusCreated,
            CreatedAt = DateTime.UtcNow,
            IdempotencyKey = idempotencyKey
        };

        // Persist Order + OutboxMessage in one atomic SaveChangesAsync unit.
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

        try
        {
            // SaveChangesAsync emits an EF Core span (child of order.create) that
            // contains the INSERT SQL and its execution time.
            await _db.SaveChangesAsync(context.CancellationToken);
        }
        catch (DbUpdateException) when (idempotencyKey is not null)
        {
            // A resilience-handler retry landed here after the first attempt already
            // committed (e.g. the client saw a connection reset post-commit). Detach the
            // entities this attempt tried to add and replay the original result instead of
            // creating a duplicate order.
            _db.Entry(order).State = EntityState.Detached;
            _db.Entry(outboxMsg).State = EntityState.Detached;

            var existing = await _db.Orders.SingleAsync(
                o => o.IdempotencyKey == idempotencyKey, context.CancellationToken);
            _logger.LogInformation(
                "CreateOrder replay detected for idempotency key {IdempotencyKey} — returning existing order {OrderId}.",
                idempotencyKey, existing.Id);
            return new CreateOrderResponse { OrderId = existing.Id, Status = existing.Status };
        }

        // Set the generated ID on the span after the DB write assigns it.
        activity?.SetTag("order.id", order.Id);

        sw.Stop();

        // Record the processing duration as a histogram observation.
        // No project_id dimension — see DiagnosticsConfig.cs for why. Per-project
        // drill-down goes through the order.project_id span attribute (set above)
        // and its trace-based exemplar on this histogram instead.
        DiagnosticsConfig.ProcessingDuration.Record(sw.Elapsed.TotalMilliseconds);

        // Increment the orders counter and running total.
        DiagnosticsConfig.OrdersCreated.Add(1);
        DiagnosticsConfig.OrdersAmount.Add(request.Amount);

        _logger.LogInformation(
            "Created order {OrderId} for project {ProjectId}, amount {Amount}. TraceId: {TraceId}",
            order.Id, order.ProjectId, order.Amount, Activity.Current?.TraceId.ToString());

        return new CreateOrderResponse { OrderId = order.Id, Status = order.Status };
    }

    // ── GetOrdersByProject ───────────────────────────────────────────────────
    // Server-streaming RPC: writes each order as a separate gRPC message.
    //
    // The single gRPC server span stays open for the full streaming duration.
    // Rows are streamed directly from the PostgreSQL cursor via AsAsyncEnumerable()
    // below — one row fetched, written to the stream, then the next — so memory
    // usage is O(1) regardless of result set size (see ADR-010).
    public override async Task GetOrdersByProject(
        GetOrdersByProjectRequest request,
        IServerStreamWriter<OrderResponse> responseStream,
        ServerCallContext context)
    {
        if (request.ProjectId <= 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "ProjectId must be a positive integer."));

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
