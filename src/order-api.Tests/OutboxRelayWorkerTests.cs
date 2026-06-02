using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OrderApi.Data;
using OrderApi.Messaging;
using OrderApi.Models;
using Xunit;

namespace OrderApi.Tests;

public class OutboxRelayWorkerTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static IServiceScopeFactory BuildScopeFactory(string dbName)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(opts => opts.UseInMemoryDatabase(dbName));
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static AppDbContext OpenDb(IServiceScopeFactory factory)
        => factory.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();

    private static OutboxRelayWorker BuildWorker(IServiceScopeFactory factory, IOrderPublisher publisher)
        => new OutboxRelayWorker(factory, publisher, NullLogger<OutboxRelayWorker>.Instance);

    // ── Pending messages are published and marked processed ──────────────────

    [Fact]
    public async Task DrainOutboxAsync_PendingMessage_PublishesAndSetsProcessedAt()
    {
        var factory = BuildScopeFactory(nameof(DrainOutboxAsync_PendingMessage_PublishesAndSetsProcessedAt));
        var db = OpenDb(factory);

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

        var saved = await OpenDb(factory).OutboxMessages.FindAsync(msg.Id);
        Assert.NotNull(saved!.ProcessedAt);
    }

    // ── No messages → publisher never called ─────────────────────────────────

    [Fact]
    public async Task DrainOutboxAsync_NoMessages_DoesNotPublish()
    {
        var factory = BuildScopeFactory(nameof(DrainOutboxAsync_NoMessages_DoesNotPublish));
        var publisher = new Mock<IOrderPublisher>();

        var worker = BuildWorker(factory, publisher.Object);
        await worker.DrainOutboxAsync(CancellationToken.None);

        publisher.Verify(p => p.PublishAsync(It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    // ── Already-processed messages are skipped ────────────────────────────────

    [Fact]
    public async Task DrainOutboxAsync_AlreadyProcessedMessage_IsSkipped()
    {
        var factory = BuildScopeFactory(nameof(DrainOutboxAsync_AlreadyProcessedMessage_IsSkipped));
        var db = OpenDb(factory);

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
        var factory = BuildScopeFactory(nameof(DrainOutboxAsync_PublisherThrows_LeavesMessageUnprocessed));
        var db = OpenDb(factory);

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

        var saved = await OpenDb(factory).OutboxMessages.FindAsync(msg.Id);
        Assert.Null(saved!.ProcessedAt);
    }

    // ── Payload includes correct order fields ─────────────────────────────────

    [Fact]
    public async Task DrainOutboxAsync_PendingMessage_PayloadContainsOrderId()
    {
        var factory = BuildScopeFactory(nameof(DrainOutboxAsync_PendingMessage_PayloadContainsOrderId));
        var db = OpenDb(factory);

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
        var factory = BuildScopeFactory(nameof(DrainOutboxAsync_WithTraceParent_ForwardsItToPublisher));
        var db = OpenDb(factory);

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
}
