using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrderApi.Messaging;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;
using Xunit;

namespace OrderApi.Tests;

// Backed by a real RabbitMQ container. Every other test that touches
// OrderPublisher (OutboxRelayWorkerTests) mocks IOrderPublisher entirely — the
// actual publish/header-encoding logic, self-labeled "the most critical
// instrumentation point in the lab," has never run against a real broker
// before this.
public class RabbitMqFixture : IAsyncLifetime
{
    // Matches k8s/datastores/rabbitmq/statefulset.yaml's pinned version.
    // WithUsername/WithPassword explicit — the builder's own defaults are a
    // randomly generated username/password, not guest/guest.
    public readonly RabbitMqContainer Container = new RabbitMqBuilder("rabbitmq:3.13.7-management")
        .WithUsername("guest")
        .WithPassword("guest")
        .Build();

    public Task InitializeAsync() => Container.StartAsync();
    public Task DisposeAsync() => Container.DisposeAsync().AsTask();
}

public class OrderPublisherTests : IClassFixture<RabbitMqFixture>
{
    private readonly RabbitMqFixture _fixture;

    public OrderPublisherTests(RabbitMqFixture fixture)
    {
        _fixture = fixture;
    }

    private OrderPublisher CreatePublisher()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RabbitMQ:Host"] = _fixture.Container.Hostname,
                ["RabbitMQ:Port"] = _fixture.Container.GetMappedPublicPort(5672).ToString(),
                ["RabbitMQ:User"] = "guest",
                ["RabbitMQ:Password"] = "guest",
            })
            .Build();
        return new OrderPublisher(config, NullLogger<OrderPublisher>.Instance);
    }

    [Fact]
    public async Task PublishAsync_RealBroker_MessageArrivesWithTraceParentHeaderIntact()
    {
        using var publisher = CreatePublisher();

        // Bind a temp queue to the same exchange/routing key OrderPublisher
        // publishes to, so the assertions below read back exactly what hit the
        // wire — not what the code merely intended to send.
        var factory = new ConnectionFactory
        {
            HostName = _fixture.Container.Hostname,
            Port = _fixture.Container.GetMappedPublicPort(5672),
            UserName = "guest",
            Password = "guest",
        };
        using var connection = factory.CreateConnection();
        using var channel = connection.CreateModel();
        channel.ExchangeDeclare("orders", ExchangeType.Topic, durable: true);
        var queueName = channel.QueueDeclare().QueueName;
        channel.QueueBind(queueName, "orders", "order.created");

        const string traceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
        const string payload = """{"order_id":1,"project_id":1}""";
        await publisher.PublishAsync(payload, traceParent);

        BasicGetResult? result = null;
        for (var i = 0; i < 50 && result is null; i++)
        {
            result = channel.BasicGet(queueName, autoAck: true);
            if (result is null) await Task.Delay(100);
        }

        Assert.NotNull(result);
        Assert.Equal(payload, Encoding.UTF8.GetString(result!.Body.ToArray()));
        Assert.True(result.BasicProperties.Persistent);
        Assert.Equal("3600000", result.BasicProperties.Expiration);
        var headerBytes = Assert.IsType<byte[]>(result.BasicProperties.Headers!["traceparent"]);
        Assert.Equal(traceParent, Encoding.UTF8.GetString(headerBytes));
    }

    [Fact]
    public async Task PublishAsync_NoTraceParent_PublishesWithoutTraceParentHeader()
    {
        using var publisher = CreatePublisher();

        var factory = new ConnectionFactory
        {
            HostName = _fixture.Container.Hostname,
            Port = _fixture.Container.GetMappedPublicPort(5672),
            UserName = "guest",
            Password = "guest",
        };
        using var connection = factory.CreateConnection();
        using var channel = connection.CreateModel();
        channel.ExchangeDeclare("orders", ExchangeType.Topic, durable: true);
        var queueName = channel.QueueDeclare().QueueName;
        channel.QueueBind(queueName, "orders", "order.created");

        await publisher.PublishAsync("""{"order_id":2}""", traceParent: null);

        BasicGetResult? result = null;
        for (var i = 0; i < 50 && result is null; i++)
        {
            result = channel.BasicGet(queueName, autoAck: true);
            if (result is null) await Task.Delay(100);
        }

        Assert.NotNull(result);
        Assert.False(result!.BasicProperties.Headers?.ContainsKey("traceparent") ?? false);
    }
}
