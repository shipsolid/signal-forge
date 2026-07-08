using System.Net.Http.Json;
using System.Text.Json;
using Grpc.Net.Client;
using OrderApi.Protos;
using Xunit;

namespace IntegrationTests;

// Full cross-language, real-broker validation of the 5-hop trace:
//   Browser (simulated: this test calls gRPC directly) -> gateway-api (skipped;
//   the gRPC call below goes straight to order-api, since gateway-api's own
//   fan-out is already covered by gateway-api.Tests' mocked-gRPC suite) ->
//   order-api (gRPC CreateOrder, real Postgres) -> RabbitMQ (real broker) ->
//   notification-svc (real Python consumer, real Redis) -> Jaeger (real
//   OTLP collector + query API).
//
// This is the test the review's "5-hop trace propagation" finding asked for,
// built after discovering (while designing it) that OutboxRelayWorker's
// outbox.relay/order.publish spans were landing in their own disconnected
// trace — see OrderPublisher.cs's header comment and
// OutboxRelayWorker.PublishAndMarkAsync for the fix this test also verifies.
//
// Not part of the fast default suite — needs Docker to build two real service
// images and run five containers. See docs/testing.md for how to run this
// explicitly; expect it to take real time (image builds + container startups).
[Trait("Category", "Integration")]
[Collection("CrossLanguageTrace")]
public class CrossLanguageTraceIntegrationTests
{
    private readonly CrossLanguageTraceFixture _fixture;

    public CrossLanguageTraceIntegrationTests(CrossLanguageTraceFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task CreateOrder_FlowsThroughRabbitMqToNotificationSvc_AndTraceLinksAllHops()
    {
        // ── 1. Create the order via a real gRPC call to a real order-api ────────
        // Plaintext (h2c) gRPC support for this bare GrpcChannel.ForAddress is
        // enabled via the Http2UnencryptedSupport RuntimeHostConfigurationOption
        // in integration-tests.csproj (see that file's comment for why) —
        // gateway-api's own client never needs this because Grpc.Net.ClientFactory's
        // AddGrpcClient<T>() enables it internally.
        using var channel = GrpcChannel.ForAddress(_fixture.OrderApiGrpcAddress);
        var client = new OrderService.OrderServiceClient(channel);

        var createResponse = await client.CreateOrderAsync(new CreateOrderRequest
        {
            ProjectId = 1,
            Description = "cross-language trace integration test",
            Amount = 42.50,
            IdempotencyKey = Guid.NewGuid().ToString(),
        });

        Assert.Equal("Created", createResponse.Status);
        var orderId = createResponse.OrderId;

        // ── 2. Poll notification-svc until the message has flowed through
        //        RabbitMQ and been persisted to Redis by the real Python
        //        consumer (proves the message actually flowed, not just that
        //        order-api accepted the request) ─────────────────────────────
        using var http = new HttpClient { BaseAddress = new Uri(_fixture.NotificationSvcAddress) };

        JsonElement? notification = null;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var notifications = await http.GetFromJsonAsync<JsonElement>("/notifications");
            foreach (var n in notifications.EnumerateArray())
            {
                if (n.TryGetProperty("order_id", out var idProp) &&
                    idProp.GetString() == orderId.ToString())
                {
                    notification = n;
                    break;
                }
            }

            if (notification is not null)
                break;

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        if (notification is null)
        {
            var orderApiLogs = await _fixture.GetOrderApiLogsAsync();
            var notificationSvcLogs = await _fixture.GetNotificationSvcLogsAsync();
            throw new Xunit.Sdk.XunitException(
                $"notification-svc never recorded a notification for order {orderId} within 30s — message did not flow through RabbitMQ.\n" +
                $"--- order-api logs (tail) ---\n{Tail(orderApiLogs, 4000)}\n" +
                $"--- notification-svc logs (tail) ---\n{Tail(notificationSvcLogs, 4000)}");
        }

        Assert.True(notification!.Value.TryGetProperty("trace_id", out var traceIdProp),
            "notification record has no trace_id field");
        var traceId = traceIdProp.GetString();
        Assert.False(string.IsNullOrEmpty(traceId));

        // ── 3. Query Jaeger for that trace and assert every hop is present ──────
        // Polling for the trace to *exist* isn't enough: order.create's own
        // spans land almost immediately, but outbox.relay/order.publish only
        // appear after OutboxRelayWorker's next 5s poll cycle, plus whatever
        // delay the OTel batch span processor / OTLP export adds on top —
        // Jaeger can and does return a real, valid, but still-incomplete trace
        // in the meantime. Keep polling until every expected span shows up
        // (or the deadline expires), not just until the trace ID resolves.
        using var jaegerHttp = new HttpClient { BaseAddress = new Uri(_fixture.JaegerQueryAddress) };

        var expectedOperations = new[]
        {
            "order.create", "outbox.relay", "order.publish", "notification.process",
        };
        HashSet<string?> operationNames = new();
        deadline = DateTime.UtcNow.AddSeconds(40);
        while (DateTime.UtcNow < deadline)
        {
            var tracesDoc = await jaegerHttp.GetFromJsonAsync<JsonElement>($"/api/traces/{traceId}");
            if (tracesDoc.TryGetProperty("data", out var data) && data.GetArrayLength() > 0)
            {
                operationNames = data[0].GetProperty("spans").EnumerateArray()
                    .Select(s => s.GetProperty("operationName").GetString())
                    .ToHashSet();

                if (expectedOperations.All(operationNames.Contains))
                    break;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        Assert.True(expectedOperations.All(operationNames.Contains),
            $"Jaeger trace {traceId} never accumulated all expected spans within 40s. " +
            $"Expected: [{string.Join(", ", expectedOperations)}]. Found: [{string.Join(", ", operationNames)}].");

        // The fix under test: order.publish (and its parent, outbox.relay) must
        // share this trace, not live in a separate, disconnected one.
        Assert.Contains("outbox.relay", operationNames);
        Assert.Contains("order.publish", operationNames);
        Assert.Contains("notification.process", operationNames);
        Assert.Contains("order.create", operationNames);
    }

    private static string Tail(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[^maxChars..];
}
