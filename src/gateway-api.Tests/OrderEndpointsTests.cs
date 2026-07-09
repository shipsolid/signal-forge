using System.Net;
using System.Net.Http.Json;
using Xunit;
using System.Text;
using System.Text.Json;
using Moq;
using OrderApi.Protos;

namespace GatewayApi.Tests;

public class OrderEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly CustomWebApplicationFactory _factory;

    public OrderEndpointsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // ── POST /api/orders — validation ─────────────────────────────────────────

    [Fact]
    public async Task CreateOrder_ZeroProjectId_Returns422()
    {
        var resp = await _client.PostAsJsonAsync("/api/orders",
            new { projectId = 0, description = "Test", amount = 10.0 });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task CreateOrder_NegativeProjectId_Returns422()
    {
        var resp = await _client.PostAsJsonAsync("/api/orders",
            new { projectId = -1, description = "Test", amount = 10.0 });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    [InlineData(1_000_000.0)]
    public async Task CreateOrder_InvalidAmount_Returns422(double amount)
    {
        var resp = await _client.PostAsJsonAsync("/api/orders",
            new { projectId = 1, description = "Test", amount });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task CreateOrder_EmptyDescription_Returns422()
    {
        var resp = await _client.PostAsJsonAsync("/api/orders",
            new { projectId = 1, description = "", amount = 10.0 });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task CreateOrder_DescriptionOver500Chars_Returns422()
    {
        var resp = await _client.PostAsJsonAsync("/api/orders",
            new { projectId = 1, description = new string('x', 501), amount = 10.0 });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ── POST /api/orders — happy path ─────────────────────────────────────────

    [Fact]
    public async Task CreateOrder_Valid_Returns201WithOrderId()
    {
        _factory.MockOrderClient
            .Setup(c => c.CreateOrderAsync(
                It.IsAny<CreateOrderRequest>(),
                It.IsAny<Grpc.Core.Metadata>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .Returns(new Grpc.Core.AsyncUnaryCall<CreateOrderResponse>(
                Task.FromResult(new CreateOrderResponse { OrderId = 42, Status = "Created" }),
                Task.FromResult(new Grpc.Core.Metadata()),
                () => Grpc.Core.Status.DefaultSuccess,
                () => new Grpc.Core.Metadata(),
                () => { }));

        var resp = await _client.PostAsJsonAsync("/api/orders",
            new { projectId = 1, description = "Widget", amount = 49.99 });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(42, body.GetProperty("id").GetInt32());
        Assert.Equal("Created", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task CreateOrder_GrpcFails_Returns502()
    {
        _factory.MockOrderClient
            .Setup(c => c.CreateOrderAsync(
                It.IsAny<CreateOrderRequest>(),
                It.IsAny<Grpc.Core.Metadata>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .Throws(new Grpc.Core.RpcException(
                new Grpc.Core.Status(Grpc.Core.StatusCode.Internal, "DB error")));

        var resp = await _client.PostAsJsonAsync("/api/orders",
            new { projectId = 1, description = "Widget", amount = 10.0 });

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
    }

    [Fact]
    public async Task CreateOrder_GrpcInvalidArgument_Returns400()
    {
        // order-api can reject a request gateway-side validation let through
        // (e.g. a project that fails a downstream-only check) — this used to
        // collapse into the same 502 as a genuine outage.
        _factory.MockOrderClient
            .Setup(c => c.CreateOrderAsync(
                It.IsAny<CreateOrderRequest>(),
                It.IsAny<Grpc.Core.Metadata>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .Throws(new Grpc.Core.RpcException(
                new Grpc.Core.Status(Grpc.Core.StatusCode.InvalidArgument, "ProjectId must be a positive integer.")));

        var resp = await _client.PostAsJsonAsync("/api/orders",
            new { projectId = 1, description = "Widget", amount = 10.0 });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ── GET /api/orders/{id} ──────────────────────────────────────────────────

    [Fact]
    public async Task GetOrder_ExistingId_Returns200()
    {
        _factory.MockOrderClient
            .Setup(c => c.GetOrderAsync(
                It.IsAny<GetOrderRequest>(),
                It.IsAny<Grpc.Core.Metadata>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .Returns(new Grpc.Core.AsyncUnaryCall<OrderResponse>(
                Task.FromResult(new OrderResponse
                {
                    Id = 7,
                    ProjectId = 1,
                    Description = "Widget",
                    Amount = 49.99,
                    Status = "Created",
                    CreatedAt = "2026-01-15T10:30:00Z"
                }),
                Task.FromResult(new Grpc.Core.Metadata()),
                () => Grpc.Core.Status.DefaultSuccess,
                () => new Grpc.Core.Metadata(),
                () => { }));

        var resp = await _client.GetAsync("/api/orders/7");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(7, body.GetProperty("id").GetInt32());
        Assert.Equal("Widget", body.GetProperty("description").GetString());
    }

    [Fact]
    public async Task GetOrder_NonExistentId_Returns404()
    {
        _factory.MockOrderClient
            .Setup(c => c.GetOrderAsync(
                It.IsAny<GetOrderRequest>(),
                It.IsAny<Grpc.Core.Metadata>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .Throws(new Grpc.Core.RpcException(
                new Grpc.Core.Status(Grpc.Core.StatusCode.NotFound, "Order 999 not found")));

        var resp = await _client.GetAsync("/api/orders/999");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task GetOrder_GrpcUnavailable_Returns503()
    {
        _factory.MockOrderClient
            .Setup(c => c.GetOrderAsync(
                It.IsAny<GetOrderRequest>(),
                It.IsAny<Grpc.Core.Metadata>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .Throws(new Grpc.Core.RpcException(
                new Grpc.Core.Status(Grpc.Core.StatusCode.Unavailable, "down")));

        var resp = await _client.GetAsync("/api/orders/7");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
    }

    // ── GET /api/notifications ────────────────────────────────────────────────

    [Fact]
    public async Task GetNotifications_DownstreamSucceeds_Returns200()
    {
        var notificationsJson = """[{"id":"notif-1","order_id":"1","message":"Test"}]""";
        var mockHttpClient = new HttpClient(new FakeHttpHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(notificationsJson, Encoding.UTF8, "application/json")
            }))
        { BaseAddress = new Uri("http://notification-svc") };

        _factory.MockHttpClientFactory
            .Setup(f => f.CreateClient("notification-svc"))
            .Returns(mockHttpClient);

        var resp = await _client.GetAsync("/api/notifications");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task GetNotifications_DownstreamFails_Returns502()
    {
        var mockHttpClient = new HttpClient(new FakeHttpHandler(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)))
        { BaseAddress = new Uri("http://notification-svc") };

        _factory.MockHttpClientFactory
            .Setup(f => f.CreateClient("notification-svc"))
            .Returns(mockHttpClient);

        var resp = await _client.GetAsync("/api/notifications");
        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
    }

    [Fact]
    public async Task GetNotifications_DownstreamReturns4xx_PassesStatusThrough()
    {
        // A well-formed 4xx from notification-svc is a real client-facing status,
        // not a connectivity/5xx failure — it shouldn't collapse into 502 either.
        var mockHttpClient = new HttpClient(new FakeHttpHandler(
            new HttpResponseMessage(HttpStatusCode.BadRequest)))
        { BaseAddress = new Uri("http://notification-svc") };

        _factory.MockHttpClientFactory
            .Setup(f => f.CreateClient("notification-svc"))
            .Returns(mockHttpClient);

        var resp = await _client.GetAsync("/api/notifications");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ── GET /healthz ──────────────────────────────────────────────────────────

    [Fact]
    public async Task HealthCheck_Returns200()
    {
        var resp = await _client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("healthy", body.GetProperty("status").GetString());
    }
}

/// <summary>Minimal DelegatingHandler that returns a canned HttpResponseMessage.</summary>
internal sealed class FakeHttpHandler(HttpResponseMessage response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(response);
}
