// ============================================================
// OutboxRelayWorker — drains the outbox table to RabbitMQ
// ============================================================
//
// The outbox pattern guarantees at-least-once delivery even if:
//   • The RabbitMQ broker is temporarily unavailable when CreateOrder runs.
//   • The order-api pod crashes between the DB write and the publish.
//
// Atomicity guarantee:
//   OrderGrpcService writes Order + OutboxMessage in a single SaveChanges
//   call (one DB transaction).  If the pod crashes, EnsureCreated() on
//   restart leaves the OutboxMessage unprocessed; this worker picks it up.
//
// Idempotency:
//   RabbitMQ message headers carry the original traceparent so the
//   notification-svc dedup key (order_id) prevents duplicate notifications
//   even if this worker publishes the same message twice.
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

        // Process in batches of 100. ORDER BY CreatedAt ensures FIFO delivery.
        var pending = await db.OutboxMessages
            .Include(m => m.Order)
            .Where(m => m.ProcessedAt == null)
            .OrderBy(m => m.CreatedAt)
            .Take(100)
            .ToListAsync(ct);

        if (pending.Count == 0)
            return;

        _logger.LogInformation("OutboxRelayWorker found {Count} pending message(s)", pending.Count);

        foreach (var msg in pending)
        {
            await PublishAndMarkAsync(db, msg, ct);
        }
    }

    private async Task PublishAndMarkAsync(AppDbContext db, OutboxMessage msg, CancellationToken ct)
    {
        using var activity = DiagnosticsConfig.ActivitySource.StartActivity("outbox.relay", ActivityKind.Internal);
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

            _logger.LogInformation(
                "Relayed outbox message {MessageId} for order {OrderId}",
                msg.Id, msg.Order.Id);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.RecordException(ex);
            _logger.LogError(ex,
                "Failed to relay outbox message {MessageId} for order {OrderId}; will retry next poll",
                msg.Id, msg.Order.Id);
            // Leave ProcessedAt null — the message will be retried on the next poll.
        }
    }
}
