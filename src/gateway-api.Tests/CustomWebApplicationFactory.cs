using GatewayApi.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using OrderApi.Protos;

namespace GatewayApi.Tests;

/// <summary>
/// Replaces MySQL with InMemory EF Core, and injects a mock gRPC OrderServiceClient
/// and a configurable mock IHttpClientFactory for HTTP isolation.
/// </summary>
public class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    public Mock<OrderService.OrderServiceClient> MockOrderClient { get; } = new();
    public Mock<IHttpClientFactory> MockHttpClientFactory { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // UseSetting injects values before top-level Program.cs statements run,
        // so the connection-string guard at startup sees a non-empty value.
        builder.UseSetting("ConnectionStrings:DefaultConnection", "Server=test;Database=test;User=test;Password=test");
        builder.UseSetting("OrderApi:Address", "http://localhost:5001");
        builder.UseSetting("NotificationSvc:Address", "http://localhost:8000");
        // appsettings.json's real AllowedHosts allow-list is scoped to actual
        // cluster/ingress hostnames — TestServer's in-memory Host header isn't
        // one of them. These endpoint tests aren't exercising host filtering
        // itself, so relax it here rather than coupling every test to it.
        builder.UseSetting("AllowedHosts", "*");

        builder.ConfigureServices(services =>
        {
            // Replace MySQL AppDbContext with InMemory.
            // DB name captured before the lambda so the same store is shared
            // across all scopes (HTTP handler scope + test GetDb() scope).
            var dbName = "gateway-test-" + Guid.NewGuid();
            var descriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
            if (descriptor != null) services.Remove(descriptor);

            services.AddDbContext<AppDbContext>(opts =>
                opts.UseInMemoryDatabase(dbName));

            // Replace gRPC client with mock
            var grpcDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(OrderService.OrderServiceClient));
            if (grpcDescriptor != null) services.Remove(grpcDescriptor);
            services.AddSingleton(MockOrderClient.Object);

            // Replace IHttpClientFactory with mock
            var httpFactoryDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(IHttpClientFactory));
            if (httpFactoryDescriptor != null) services.Remove(httpFactoryDescriptor);
            services.AddSingleton(MockHttpClientFactory.Object);
        });

        builder.UseEnvironment("Testing");
    }
}
