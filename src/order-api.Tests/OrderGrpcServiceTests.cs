using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using OrderApi.Data;
using OrderApi.Models;
using OrderApi.Protos;
using OrderApi.Services;

namespace OrderApi.Tests;

public class OrderGrpcServiceTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static AppDbContext BuildDb(string name)
    {
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return new AppDbContext(opts);
    }

    private static OrderGrpcService BuildService(AppDbContext db)
    {
        return new OrderGrpcService(db, NullLogger<OrderGrpcService>.Instance);
    }

    // ── CreateOrder — validation ──────────────────────────────────────────────

    [Fact]
    public async Task CreateOrder_ZeroProjectId_ThrowsInvalidArgument()
    {
        var svc = BuildService(BuildDb(nameof(CreateOrder_ZeroProjectId_ThrowsInvalidArgument)));
        var req = new CreateOrderRequest { ProjectId = 0, Description = "Test", Amount = 10 };
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            svc.CreateOrder(req, TestServerCallContext.Create()));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        Assert.Contains("ProjectId", ex.Status.Detail);
    }

    [Fact]
    public async Task CreateOrder_NegativeProjectId_ThrowsInvalidArgument()
    {
        var svc = BuildService(BuildDb(nameof(CreateOrder_NegativeProjectId_ThrowsInvalidArgument)));
        var req = new CreateOrderRequest { ProjectId = -1, Description = "Test", Amount = 10 };
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            svc.CreateOrder(req, TestServerCallContext.Create()));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1_000_000)]
    public async Task CreateOrder_InvalidAmount_ThrowsInvalidArgument(double amount)
    {
        var svc = BuildService(BuildDb($"amount_{amount}"));
        var req = new CreateOrderRequest { ProjectId = 1, Description = "Test", Amount = amount };
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            svc.CreateOrder(req, TestServerCallContext.Create()));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        Assert.Contains("Amount", ex.Status.Detail);
    }

    [Fact]
    public async Task CreateOrder_EmptyDescription_ThrowsInvalidArgument()
    {
        var svc = BuildService(BuildDb(nameof(CreateOrder_EmptyDescription_ThrowsInvalidArgument)));
        var req = new CreateOrderRequest { ProjectId = 1, Description = "", Amount = 10 };
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            svc.CreateOrder(req, TestServerCallContext.Create()));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        Assert.Contains("Description", ex.Status.Detail);
    }

    [Fact]
    public async Task CreateOrder_DescriptionOver500Chars_ThrowsInvalidArgument()
    {
        var svc = BuildService(BuildDb(nameof(CreateOrder_DescriptionOver500Chars_ThrowsInvalidArgument)));
        var req = new CreateOrderRequest { ProjectId = 1, Description = new string('x', 501), Amount = 10 };
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            svc.CreateOrder(req, TestServerCallContext.Create()));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    // ── CreateOrder — happy path ──────────────────────────────────────────────

    [Fact]
    public async Task CreateOrder_Valid_PersistsOrderAndReturnsId()
    {
        var db = BuildDb(nameof(CreateOrder_Valid_PersistsOrderAndReturnsId));
        var svc = BuildService(db);
        var req = new CreateOrderRequest { ProjectId = 3, Description = "Widget", Amount = 49.99 };

        var resp = await svc.CreateOrder(req, TestServerCallContext.Create());

        Assert.True(resp.OrderId > 0);
        Assert.Equal("Created", resp.Status);

        var saved = await db.Orders.FindAsync(resp.OrderId);
        Assert.NotNull(saved);
        Assert.Equal(3, saved.ProjectId);
        Assert.Equal("Widget", saved.Description);
        Assert.Equal(49.99m, saved.Amount, precision: 2);
    }

    [Fact]
    public async Task CreateOrder_Valid_WritesOutboxEntry()
    {
        var db = BuildDb(nameof(CreateOrder_Valid_WritesOutboxEntry));
        var svc = BuildService(db);
        var req = new CreateOrderRequest { ProjectId = 1, Description = "Gadget", Amount = 100 };

        var resp = await svc.CreateOrder(req, TestServerCallContext.Create());

        // Verify an unprocessed OutboxMessage was written in the same transaction.
        var outbox = await db.OutboxMessages.Include(m => m.Order).SingleAsync();
        Assert.Equal(resp.OrderId, outbox.Order.Id);
        Assert.Null(outbox.ProcessedAt);
    }

    [Fact]
    public async Task CreateOrder_BoundaryAmount_001_Succeeds()
    {
        var db = BuildDb(nameof(CreateOrder_BoundaryAmount_001_Succeeds));
        var svc = BuildService(db);
        var req = new CreateOrderRequest { ProjectId = 1, Description = "Min amount", Amount = 0.01 };

        var resp = await svc.CreateOrder(req, TestServerCallContext.Create());
        Assert.True(resp.OrderId > 0);
    }

    [Fact]
    public async Task CreateOrder_BoundaryAmount_Max_Succeeds()
    {
        var db = BuildDb(nameof(CreateOrder_BoundaryAmount_Max_Succeeds));
        var svc = BuildService(db);
        var req = new CreateOrderRequest { ProjectId = 1, Description = "Max amount", Amount = 999_999.99 };

        var resp = await svc.CreateOrder(req, TestServerCallContext.Create());
        Assert.True(resp.OrderId > 0);
    }

    // ── CreateOrder — idempotency key ────────────────────────────────────────

    [Fact]
    public async Task CreateOrder_RepeatedIdempotencyKey_ReturnsOriginalOrder()
    {
        var db = BuildDb(nameof(CreateOrder_RepeatedIdempotencyKey_ReturnsOriginalOrder));
        var svc = BuildService(db);
        var req = new CreateOrderRequest
        {
            ProjectId = 1,
            Description = "Retry me",
            Amount = 15,
            IdempotencyKey = "retry-key-1"
        };

        var first = await svc.CreateOrder(req, TestServerCallContext.Create());
        // Second attempt with the same key simulates a resilience-handler retry after the
        // first attempt already committed — must replay, not duplicate.
        var second = await svc.CreateOrder(req, TestServerCallContext.Create());

        Assert.Equal(first.OrderId, second.OrderId);
        Assert.Equal(1, await db.Orders.CountAsync());
    }

    [Fact]
    public async Task CreateOrder_DifferentIdempotencyKeys_CreatesSeparateOrders()
    {
        var db = BuildDb(nameof(CreateOrder_DifferentIdempotencyKeys_CreatesSeparateOrders));
        var svc = BuildService(db);

        var first = await svc.CreateOrder(
            new CreateOrderRequest { ProjectId = 1, Description = "One", Amount = 15, IdempotencyKey = "key-a" },
            TestServerCallContext.Create());
        var second = await svc.CreateOrder(
            new CreateOrderRequest { ProjectId = 1, Description = "Two", Amount = 15, IdempotencyKey = "key-b" },
            TestServerCallContext.Create());

        Assert.NotEqual(first.OrderId, second.OrderId);
        Assert.Equal(2, await db.Orders.CountAsync());
    }

    [Fact]
    public async Task CreateOrder_NoIdempotencyKey_AllowsMultipleOrders()
    {
        // Legacy/direct callers that omit the key (proto3 default "") must not collide with
        // each other via the unique index — each maps to a null IdempotencyKey.
        var db = BuildDb(nameof(CreateOrder_NoIdempotencyKey_AllowsMultipleOrders));
        var svc = BuildService(db);

        var first = await svc.CreateOrder(
            new CreateOrderRequest { ProjectId = 1, Description = "One", Amount = 15 },
            TestServerCallContext.Create());
        var second = await svc.CreateOrder(
            new CreateOrderRequest { ProjectId = 1, Description = "Two", Amount = 15 },
            TestServerCallContext.Create());

        Assert.NotEqual(first.OrderId, second.OrderId);
        Assert.Equal(2, await db.Orders.CountAsync());
    }

    // ── GetOrder ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetOrder_ExistingId_ReturnsOrder()
    {
        var db = BuildDb(nameof(GetOrder_ExistingId_ReturnsOrder));
        db.Orders.Add(new Models.Order { ProjectId = 1, Description = "Test", Amount = 25m, Status = "Created" });
        await db.SaveChangesAsync();
        var orderId = db.Orders.First().Id;

        var svc = BuildService(db);
        var resp = await svc.GetOrder(new GetOrderRequest { OrderId = orderId }, TestServerCallContext.Create());

        Assert.Equal(orderId, resp.Id);
        Assert.Equal("Test", resp.Description);
        Assert.Equal(25.0, resp.Amount, precision: 2);
    }

    [Fact]
    public async Task GetOrder_NonExistentId_ThrowsNotFound()
    {
        var svc = BuildService(BuildDb(nameof(GetOrder_NonExistentId_ThrowsNotFound)));
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            svc.GetOrder(new GetOrderRequest { OrderId = 9999 }, TestServerCallContext.Create()));
        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }

    // ── GetOrdersByProject (server-streaming) ─────────────────────────────────

    [Fact]
    public async Task GetOrdersByProject_ReturnsAllMatchingOrders()
    {
        var db = BuildDb(nameof(GetOrdersByProject_ReturnsAllMatchingOrders));
        db.Orders.AddRange(
            new Models.Order { ProjectId = 5, Description = "A", Amount = 10m, Status = "Created" },
            new Models.Order { ProjectId = 5, Description = "B", Amount = 20m, Status = "Created" },
            new Models.Order { ProjectId = 9, Description = "Other", Amount = 5m, Status = "Created" }
        );
        await db.SaveChangesAsync();

        var svc = BuildService(db);
        var stream = new FakeServerStreamWriter<OrderResponse>();
        var req = new GetOrdersByProjectRequest { ProjectId = 5 };

        await svc.GetOrdersByProject(req, stream, TestServerCallContext.Create());

        Assert.Equal(2, stream.Written.Count);
        Assert.All(stream.Written, o => Assert.Equal(5, o.ProjectId));
    }

    [Fact]
    public async Task GetOrdersByProject_NoOrders_ReturnsEmptyStream()
    {
        var svc = BuildService(BuildDb(nameof(GetOrdersByProject_NoOrders_ReturnsEmptyStream)));
        var stream = new FakeServerStreamWriter<OrderResponse>();

        await svc.GetOrdersByProject(
            new GetOrdersByProjectRequest { ProjectId = 999 },
            stream,
            TestServerCallContext.Create());

        Assert.Empty(stream.Written);
    }
}
