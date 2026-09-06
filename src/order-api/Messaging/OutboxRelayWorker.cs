// ============================================================
// OutboxRelayWorker — drains the outbox table to RabbitMQ
// ============================================================
//
// The outbox pattern guarantees at-least-once delivery even if:
//   • The RabbitMQ broker is temporarily unavailable when CreateOrder runs.
//   • The order-api pod crashes between the DB write and the publish.
//
// Atomicity guarantee:
//   OrderGrpcService writes Order + OutboxMessage in one atomic SaveChangesAsync
//   unit. The provider owns the transaction details; if the pod crashes,
//   EnsureCreated() on
//   restart leaves the OutboxMessage unprocessed; this worker picks it up.
//
// Idempotency:
//   RabbitMQ message headers carry the original traceparent so the
//   notification-svc dedup key (order_id) prevents duplicate notifications
//   even if this worker publishes the same message twice.
//
// Multi-replica safety:
//   order-api runs 2+ replicas, all polling independently. Each row is
//   claimed via `SELECT ... FOR UPDATE SKIP LOCKED` in its own transaction
//   (see PublishAndMarkAsync) so two replicas can never publish the same
//   row concurrently — the second one's SELECT just skips it. This is
//   defense-in-depth on top of, not a replacement for, the notification-svc
//   dedup above.
//
// Poll interval: 5 seconds — acceptable for a lab. In production use
// PostgreSQL LISTEN/NOTIFY or a trigger-based wake-up for lower latency.
// ============================================================

using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Trace;
using OrderApi.Data;
using OrderApi.Models;
using OrderApi.Telemetry;

namespace OrderApi.Messaging;

public class OutboxRelayWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOrderPublisher _publisher;
    private readonly ILogger<OutboxRelayWorker> _logger;

    public OutboxRelayWorker(
        IServiceScopeFactory scopeFactory,
        IOrderPublisher publisher,
        ILogger<OutboxRelayWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _publisher = publisher;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OutboxRelayWorker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DrainOutboxAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OutboxRelayWorker poll failed, retrying in {Interval}s",
                    PollInterval.TotalSeconds);
            }

            await Task.Delay(PollInterval, stoppingToken);
        }

        _logger.LogInformation("OutboxRelayWorker stopped");
    }

    internal async Task DrainOutboxAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Unlocked ID-only lookup, just to size the batch and fix FIFO iteration order.
        // The actual row lock (FOR UPDATE SKIP LOCKED) happens per-message in
        // PublishAndMarkAsync below — see that method for why.
        var pendingIds = await db.OutboxMessages
            .Where(m => m.ProcessedAt == null)
            .OrderBy(m => m.CreatedAt)
            .Select(m => m.Id)
            .Take(100)
            .ToListAsync(ct);

        if (pendingIds.Count == 0)
            return;

        _logger.LogInformation("OutboxRelayWorker found {Count} pending message(s)", pendingIds.Count);

        foreach (var id in pendingIds)
        {
            await PublishAndMarkAsync(db, id, ct);
        }
    }

    private async Task PublishAndMarkAsync(AppDbContext db, int outboxMessageId, CancellationToken ct)
    {
        // EnableRetryOnFailure() (Program.cs) registers a retrying execution
        // strategy, and EF Core refuses a bare Database.BeginTransactionAsync()
        // under one — "does not support user-initiated transactions" — unless
        // it's run through that strategy's own ExecuteAsync, so a transient
        // retry can safely re-open the transaction from scratch. Wrapping the
        // whole method (including the RabbitMQ publish below) means a retry
        // could in theory publish twice if a transient DB failure hits between
        // the publish and the commit; that's the same "publishes the same
        // message twice" scenario this class's own header comment already
        // calls out as tolerated, via notification-svc's order_id dedup key —
        // not a new risk this introduces.
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            // order-api runs 2+ replicas, each polling independently on the same 5s interval.
            // Without a lock, two replicas can both select the same unprocessed row in the
            // same window and publish it twice. FOR UPDATE SKIP LOCKED closes that race: if
            // another replica already has this row locked (mid-publish, in its own
            // transaction below), this SELECT returns nothing here instead of blocking or
            // double-publishing — the row is simply left for whichever replica holds the
            // lock to finish (or for the next poll, if that replica's attempt fails and
            // rolls back). One transaction per message, not one per batch, so a single
            // failure never blocks or rolls back the rest of the batch — same isolation the
            // prior per-message SaveChanges already gave us, just now also lock-safe across
            // replicas.
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            var msg = await db.OutboxMessages
                .FromSqlInterpolated($@"
                SELECT * FROM ""OutboxMessages""
                WHERE ""Id"" = {outboxMessageId} AND ""ProcessedAt"" IS NULL
                FOR UPDATE SKIP LOCKED")
                .Include(m => m.Order)
                .SingleOrDefaultAsync(ct);

            if (msg is null)
            {
                // Already locked by another replica this cycle, or already processed.
                await tx.RollbackAsync(ct);
                return;
            }

            // Link outbox.relay back to the original request trace (msg.TraceParent,
            // captured from Activity.Current?.Id when the order was written — see
            // OrderGrpcService.cs). Same ADR-002 reasoning as notification-svc's
            // consumer span applies here symmetrically: a failed publish retries on
            // a later poll, so each attempt becomes its own sibling child of the
            // original span rather than a single, ever-reused parent claimed by
            // multiple attempts. Passing the parsed context as parentContext gives
            // this span (and order.publish, its child) the same trace ID as the
            // original request, so a single Jaeger trace query surfaces the whole
            // order.create -> outbox.relay -> order.publish -> notification.process
            // chain; the additional ActivityLink mirrors the consumer side's own
            // SpanLink so the async hop still renders as a dashed reference in Jaeger.
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
            activity?.SetTag("outbox.message_id", msg.Id);
            activity?.SetTag("order.id", msg.Order.Id);

            try
            {
                var payload = JsonSerializer.Serialize(new
                {
                    order_id = msg.Order.Id,
                    project_id = msg.Order.ProjectId,
                    description = msg.Order.Description,
                    amount = (double)msg.Order.Amount,
                    created_at = msg.Order.CreatedAt.ToString("O")
                });

                await _publisher.PublishAsync(payload, msg.TraceParent);

                msg.ProcessedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                _logger.LogInformation(
                    "Relayed outbox message {MessageId} for order {OrderId}",
                    msg.Id, msg.Order.Id);
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity?.AddException(ex);
                _logger.LogError(ex,
                    "Failed to relay outbox message {MessageId} for order {OrderId}; will retry next poll",
                    msg.Id, msg.Order.Id);
                // Rollback releases the row lock and leaves ProcessedAt null — retried next poll.
                await tx.RollbackAsync(ct);
            }
        });
    }
}
