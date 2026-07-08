using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OrderApi.Data;
using OrderApi.Messaging;
using OrderApi.Models;
using OrderApi.Telemetry;
using Testcontainers.PostgreSql;
using Xunit;

namespace OrderApi.Tests;

// Backed by a real PostgreSQL container, not EF Core's InMemory provider.
// InMemory doesn't support transactions or raw SQL, and this worker's
// multi-replica-safe claiming (FOR UPDATE SKIP LOCKED, see
// OutboxRelayWorker.PublishAndMarkAsync) needs both — there's no portable
// substitute that still exercises the real locking behaviour.
public class PostgresFixture : IAsyncLifetime
{
    // Matches k8s/datastores/postgres/statefulset.yaml's pinned version.
    public readonly PostgreSqlContainer Container = new PostgreSqlBuilder("postgres:16.4")
        .WithDatabase("outbox_test")
        .WithUsername("test")
        .WithPassword("test")
        .Build();

    public Task InitializeAsync() => Container.StartAsync();
    public Task DisposeAsync() => Container.DisposeAsync().AsTask();
}

public class OutboxRelayWorkerTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    public OutboxRelayWorkerTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    // Fresh schema per test so tests don't see each other's rows, while still
    // reusing one running container (and its startup cost) across the class.
    public async Task InitializeAsync()
    {
        await using var db = OpenDb();
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── Helpers ──────────────────────────────────────────────────────────────

    private IServiceScopeFactory BuildScopeFactory()
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(opts => opts.UseNpgsql(_fixture.Container.GetConnectionString()));
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private AppDbContext OpenDb()
        => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_fixture.Container.GetConnectionString())
            .Options);

    private static OutboxRelayWorker BuildWorker(IServiceScopeFactory factory, IOrderPublisher publisher)
        => new(factory, publisher, NullLogger<OutboxRelayWorker>.Instance);

    // ── Pending messages are published and marked processed ──────────────────

    [Fact]
    public async Task DrainOutboxAsync_PendingMessage_PublishesAndSetsProcessedAt()
    {
        var factory = BuildScopeFactory();
        await using var db = OpenDb();

        var order = new Order { ProjectId = 1, Description = "Widget", Amount = 10m };
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        var msg = new OutboxMessage { Order = order, CreatedAt = DateTime.UtcNow };
        db.OutboxMessages.Add(msg);
        await db.SaveChangesAsync();

        var publisher = new Mock<IOrderPublisher>();
        publisher.Setup(p => p.PublishAsync(It.IsAny<string>(), It.IsAny<string?>()))
                 .Returns(Task.CompletedTask);

        var worker = BuildWorker(factory, publisher.Object);
        await worker.DrainOutboxAsync(CancellationToken.None);

        publisher.Verify(p => p.PublishAsync(It.IsAny<string>(), It.IsAny<string?>()), Times.Once);

        await using var verifyDb = OpenDb();
        var saved = await verifyDb.OutboxMessages.FindAsync(msg.Id);
        Assert.NotNull(saved!.ProcessedAt);
    }

    // ── No messages → publisher never called ─────────────────────────────────

    [Fact]
    public async Task DrainOutboxAsync_NoMessages_DoesNotPublish()
    {
        var factory = BuildScopeFactory();
        var publisher = new Mock<IOrderPublisher>();

        var worker = BuildWorker(factory, publisher.Object);
        await worker.DrainOutboxAsync(CancellationToken.None);

        publisher.Verify(p => p.PublishAsync(It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    // ── Already-processed messages are skipped ────────────────────────────────

    [Fact]
    public async Task DrainOutboxAsync_AlreadyProcessedMessage_IsSkipped()
    {
        var factory = BuildScopeFactory();
        await using var db = OpenDb();

        var order = new Order { ProjectId = 1, Description = "Old", Amount = 5m };
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        var msg = new OutboxMessage
        {
            Order = order,
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            ProcessedAt = DateTime.UtcNow.AddMinutes(-9),
        };
        db.OutboxMessages.Add(msg);
        await db.SaveChangesAsync();

        var publisher = new Mock<IOrderPublisher>();
        var worker = BuildWorker(factory, publisher.Object);
        await worker.DrainOutboxAsync(CancellationToken.None);

        publisher.Verify(p => p.PublishAsync(It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    // ── Publisher failure leaves ProcessedAt null (will retry next poll) ─────

    [Fact]
    public async Task DrainOutboxAsync_PublisherThrows_LeavesMessageUnprocessed()
    {
        var factory = BuildScopeFactory();
        await using var db = OpenDb();

        var order = new Order { ProjectId = 2, Description = "Fail me", Amount = 1m };
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        var msg = new OutboxMessage { Order = order, CreatedAt = DateTime.UtcNow };
        db.OutboxMessages.Add(msg);
        await db.SaveChangesAsync();

        var publisher = new Mock<IOrderPublisher>();
        publisher.Setup(p => p.PublishAsync(It.IsAny<string>(), It.IsAny<string?>()))
                 .ThrowsAsync(new Exception("RabbitMQ down"));

        var worker = BuildWorker(factory, publisher.Object);
        await worker.DrainOutboxAsync(CancellationToken.None);

        await using var verifyDb = OpenDb();
        var saved = await verifyDb.OutboxMessages.FindAsync(msg.Id);
        Assert.Null(saved!.ProcessedAt);
    }

    // ── Payload includes correct order fields ─────────────────────────────────

    [Fact]
    public async Task DrainOutboxAsync_PendingMessage_PayloadContainsOrderId()
    {
        var factory = BuildScopeFactory();
        await using var db = OpenDb();

        var order = new Order { ProjectId = 7, Description = "Payload check", Amount = 42.50m };
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        db.OutboxMessages.Add(new OutboxMessage { Order = order, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        string? capturedPayload = null;
        var publisher = new Mock<IOrderPublisher>();
        publisher.Setup(p => p.PublishAsync(It.IsAny<string>(), It.IsAny<string?>()))
                 .Callback<string, string?>((payload, _) => capturedPayload = payload)
                 .Returns(Task.CompletedTask);

        var worker = BuildWorker(factory, publisher.Object);
        await worker.DrainOutboxAsync(CancellationToken.None);

        Assert.NotNull(capturedPayload);
        Assert.Contains($"\"order_id\":{order.Id}", capturedPayload);
        Assert.Contains("\"project_id\":7", capturedPayload);
    }

    // ── TraceParent is forwarded to publisher ─────────────────────────────────

    [Fact]
    public async Task DrainOutboxAsync_WithTraceParent_ForwardsItToPublisher()
    {
        var factory = BuildScopeFactory();
        await using var db = OpenDb();

        var order = new Order { ProjectId = 1, Description = "Trace test", Amount = 5m };
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        const string expectedTraceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
        db.OutboxMessages.Add(new OutboxMessage
        {
            Order = order,
            TraceParent = expectedTraceParent,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        string? capturedTraceParent = null;
        var publisher = new Mock<IOrderPublisher>();
        publisher.Setup(p => p.PublishAsync(It.IsAny<string>(), It.IsAny<string?>()))
                 .Callback<string, string?>((_, tp) => capturedTraceParent = tp)
                 .Returns(Task.CompletedTask);

        var worker = BuildWorker(factory, publisher.Object);
        await worker.DrainOutboxAsync(CancellationToken.None);

        Assert.Equal(expectedTraceParent, capturedTraceParent);
    }

    // ── outbox.relay shares the original request's trace ID ──────────────────
    // Regression test for the fix described in OutboxRelayWorker.PublishAndMarkAsync
    // and OrderPublisher.cs's header comment: outbox.relay (and therefore its
    // child order.publish) must land in the SAME trace as the original request,
    // not a new, disconnected one, so a single Jaeger trace query surfaces
    // order.create -> outbox.relay -> order.publish -> notification.process.
    [Fact]
    public async Task DrainOutboxAsync_OutboxRelaySpan_SharesTraceIdWithOriginalRequest()
    {
        var factory = BuildScopeFactory();
        await using var db = OpenDb();

        var order = new Order { ProjectId = 1, Description = "Trace linkage test", Amount = 5m };
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        const string traceId = "4bf92f3577b34da6a3ce929d0e0e4736";
        const string traceParent = $"00-{traceId}-00f067aa0ba902b7-01";
        db.OutboxMessages.Add(new OutboxMessage
        {
            Order = order,
            TraceParent = traceParent,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var publisher = new Mock<IOrderPublisher>();
        publisher.Setup(p => p.PublishAsync(It.IsAny<string>(), It.IsAny<string?>()))
                 .Returns(Task.CompletedTask);

        string? observedTraceId = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == DiagnosticsConfig.ServiceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = a =>
            {
                if (a.OperationName == "outbox.relay")
                    observedTraceId = a.TraceId.ToString();
            },
        };
        ActivitySource.AddActivityListener(listener);

        var worker = BuildWorker(factory, publisher.Object);
        await worker.DrainOutboxAsync(CancellationToken.None);

        Assert.Equal(traceId, observedTraceId);
    }

    // ── Multi-replica race: two workers polling concurrently must not both ───
    // ── publish the same message ──────────────────────────────────────────────

    [Fact]
    public async Task DrainOutboxAsync_TwoConcurrentReplicas_OnlyOnePublishesEachMessage()
    {
        // Two independent scope factories against the same database, standing
        // in for two order-api pods polling the same OutboxMessages table.
        var factoryA = BuildScopeFactory();
        var factoryB = BuildScopeFactory();

        await using (var db = OpenDb())
        {
            for (var i = 0; i < 10; i++)
            {
                var order = new Order { ProjectId = 1, Description = $"Order {i}", Amount = 1m };
                db.Orders.Add(order);
                db.OutboxMessages.Add(new OutboxMessage { Order = order, CreatedAt = DateTime.UtcNow });
            }
            await db.SaveChangesAsync();
        }

        var publishedOrderIds = new System.Collections.Concurrent.ConcurrentBag<int>();

        IOrderPublisher MakeSlowPublisher()
        {
            var mock = new Mock<IOrderPublisher>();
            mock.Setup(p => p.PublishAsync(It.IsAny<string>(), It.IsAny<string?>()))
                .Returns<string, string?>(async (payload, _) =>
                {
                    // Widen the window between claiming a row and committing it,
                    // so a real race (not just a lucky interleave) is exercised.
                    await Task.Delay(50);
                    var orderId = System.Text.Json.JsonDocument.Parse(payload).RootElement.GetProperty("order_id").GetInt32();
                    publishedOrderIds.Add(orderId);
                });
            return mock.Object;
        }

        var workerA = BuildWorker(factoryA, MakeSlowPublisher());
        var workerB = BuildWorker(factoryB, MakeSlowPublisher());

        await Task.WhenAll(
            workerA.DrainOutboxAsync(CancellationToken.None),
            workerB.DrainOutboxAsync(CancellationToken.None));

        // The real assertion: each order was published exactly once, total, across
        // both replicas — not zero (both skipped it) and not two (both won the race).
        Assert.Equal(10, publishedOrderIds.Count);
        Assert.Equal(10, publishedOrderIds.Distinct().Count());

        await using var verifyDb = OpenDb();
        var processedCount = await verifyDb.OutboxMessages.CountAsync(m => m.ProcessedAt != null);
        Assert.Equal(10, processedCount);
    }
}
