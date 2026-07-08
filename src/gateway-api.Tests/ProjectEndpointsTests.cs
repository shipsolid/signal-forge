using System.Net;
using System.Net.Http.Json;
using GatewayApi.Data;
using Xunit;
using GatewayApi.Models;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using OrderApi.Protos;

namespace GatewayApi.Tests;

public class ProjectEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly CustomWebApplicationFactory _factory;

    public ProjectEndpointsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private AppDbContext GetDb()
    {
        var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AppDbContext>();
    }

    // ── GET /api/projects ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetProjects_EmptyDb_Returns200WithEmptyArray()
    {
        var resp = await _client.GetAsync("/api/projects");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<List<Project>>();
        Assert.NotNull(body);
    }

    [Fact]
    public async Task GetProjects_WithData_ReturnsAllProjects()
    {
        var db = GetDb();
        db.Projects.AddRange(
            new Project { Name = "Alpha", Owner = "Alice", CreatedAt = DateTime.UtcNow },
            new Project { Name = "Beta", Owner = "Bob", CreatedAt = DateTime.UtcNow }
        );
        await db.SaveChangesAsync();

        var resp = await _client.GetAsync("/api/projects");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<List<Project>>();
        Assert.NotNull(body);
        Assert.True(body.Count >= 2);
    }

    // ── GET /api/projects/{id} ────────────────────────────────────────────────

    [Fact]
    public async Task GetProject_ExistingId_Returns200()
    {
        var db = GetDb();
        var project = new Project { Name = "Gamma", Owner = "Carol", CreatedAt = DateTime.UtcNow };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        var resp = await _client.GetAsync($"/api/projects/{project.Id}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<Project>();
        Assert.NotNull(body);
        Assert.Equal("Gamma", body.Name);
    }

    [Fact]
    public async Task GetProject_NonExistentId_Returns404()
    {
        var resp = await _client.GetAsync("/api/projects/99999");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── POST /api/projects ────────────────────────────────────────────────────

    [Fact]
    public async Task CreateProject_ValidPayload_Returns201WithId()
    {
        var resp = await _client.PostAsJsonAsync("/api/projects",
            new { name = "Delta", owner = "Dave" });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<Project>();
        Assert.NotNull(body);
        Assert.True(body.Id > 0);
        Assert.Equal("Delta", body.Name);
    }

    [Fact]
    public async Task CreateProject_Persists_InDatabase()
    {
        await _client.PostAsJsonAsync("/api/projects",
            new { name = "Epsilon", owner = "Eve" });

        var db = GetDb();
        Assert.True(db.Projects.Any(p => p.Name == "Epsilon"));
    }

    // ── DELETE /api/projects/{id} ─────────────────────────────────────────────

    [Fact]
    public async Task DeleteProject_ExistingId_Returns204()
    {
        var db = GetDb();
        var project = new Project { Name = "Zeta", Owner = "Zara", CreatedAt = DateTime.UtcNow };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        var resp = await _client.DeleteAsync($"/api/projects/{project.Id}");
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    [Fact]
    public async Task DeleteProject_NonExistentId_Returns404()
    {
        var resp = await _client.DeleteAsync("/api/projects/99998");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── GET /api/projects/{id}/orders (gRPC proxy) ────────────────────────────

    [Fact]
    public async Task GetOrdersByProject_GrpcSucceeds_Returns200()
    {
        // Arrange: seed project + configure mock gRPC response
        var db = GetDb();
        var project = new Project { Name = "Eta", Owner = "Ethan", CreatedAt = DateTime.UtcNow };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        var mockStream = new Mock<Grpc.Core.IAsyncStreamReader<OrderResponse>>();
        mockStream.SetupSequence(s => s.MoveNext(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true)
            .ReturnsAsync(false);
        mockStream.Setup(s => s.Current).Returns(new OrderResponse
        {
            Id = 1, ProjectId = project.Id, Description = "Widget",
            Amount = 10.0, Status = "Created", CreatedAt = DateTime.UtcNow.ToString("O")
        });

        var call = new Grpc.Core.AsyncServerStreamingCall<OrderResponse>(
            mockStream.Object,
            Task.FromResult(new Grpc.Core.Metadata()),
            () => Grpc.Core.Status.DefaultSuccess,
            () => new Grpc.Core.Metadata(),
            () => { });

        _factory.MockOrderClient
            .Setup(c => c.GetOrdersByProject(
                It.IsAny<GetOrdersByProjectRequest>(),
                It.IsAny<Grpc.Core.Metadata>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .Returns(call);

        var resp = await _client.GetAsync($"/api/projects/{project.Id}/orders");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task GetOrdersByProject_GrpcUnavailable_Returns503()
    {
        var db = GetDb();
        var project = new Project { Name = "Theta", Owner = "Thor", CreatedAt = DateTime.UtcNow };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        _factory.MockOrderClient
            .Setup(c => c.GetOrdersByProject(
                It.IsAny<GetOrdersByProjectRequest>(),
                It.IsAny<Grpc.Core.Metadata>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .Throws(new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.Unavailable, "down")));

        var resp = await _client.GetAsync($"/api/projects/{project.Id}/orders");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
    }

    [Fact]
    public async Task GetOrdersByProject_GrpcInvalidArgument_Returns400()
    {
        var db = GetDb();
        var project = new Project { Name = "Iota", Owner = "Ivy", CreatedAt = DateTime.UtcNow };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        _factory.MockOrderClient
            .Setup(c => c.GetOrdersByProject(
                It.IsAny<GetOrdersByProjectRequest>(),
                It.IsAny<Grpc.Core.Metadata>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .Throws(new Grpc.Core.RpcException(
                new Grpc.Core.Status(Grpc.Core.StatusCode.InvalidArgument, "ProjectId must be a positive integer.")));

        var resp = await _client.GetAsync($"/api/projects/{project.Id}/orders");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task GetOrdersByProject_GrpcInternal_Returns502()
    {
        var db = GetDb();
        var project = new Project { Name = "Kappa", Owner = "Kai", CreatedAt = DateTime.UtcNow };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        _factory.MockOrderClient
            .Setup(c => c.GetOrdersByProject(
                It.IsAny<GetOrdersByProjectRequest>(),
                It.IsAny<Grpc.Core.Metadata>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .Throws(new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.Internal, "DB error")));

        var resp = await _client.GetAsync($"/api/projects/{project.Id}/orders");
        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
    }
}
