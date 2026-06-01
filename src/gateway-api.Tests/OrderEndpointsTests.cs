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

    // ── GET /api/notifications ────────────────────────────────────────────────

    [Fact]
    public async Task GetNotifications_DownstreamSucceeds_Returns200()
    {
        var notificationsJson = """[{"id":"notif-1","order_id":"1","message":"Test"}]""";
        var mockHttpClient = new HttpClient(new FakeHttpHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(notificationsJson, Encoding.UTF8, "application/json")
            })) { BaseAddress = new Uri("http://notification-svc") };

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
