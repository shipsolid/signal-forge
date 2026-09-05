// ============================================================
// OrderPublisher — RabbitMQ PRODUCER with W3C trace propagation
// ============================================================
//
// THIS IS THE MOST CRITICAL INSTRUMENTATION POINT IN THE LAB.
//
// It implements the OTel "Messaging" semantic conventions for
// async trace propagation. The trace started by the Angular browser
// (via Faro) must survive crossing the RabbitMQ message boundary so
// that the notification-svc CONSUMER span appears in the SAME trace
// as the original HTTP request — even though the consumer runs
// asynchronously, potentially much later, in a different service
// written in a different language (Python).
//
// How it works:
//   1. A PRODUCER span is started with ActivityKind.Producer.
//      Kind=Producer is an OTel convention for message-sending spans;
//      it signals to Jaeger/Tempo that this span has an async child.
//   2. The W3C traceparent is written into RabbitMQ message headers.
//      The value comes from the OutboxMessage.TraceParent field, which
//      was captured from Activity.Current?.Id when the order was saved.
//      This preserves the original browser→gateway→order-api trace
//      context across the async gap between order creation and delivery.
//   3. On the consumer side (notification-svc/app/consumer.py),
//      TextMapPropagator.extract() reads these same headers,
//      reconstructs the SpanContext, and creates a CONSUMER span
//      linked to it (same traceId, different spanId, linked via
//      the SpanLink mechanism to preserve the async relationship).
//
// What you see in Jaeger — ONE trace, start to finish:
//
//   gateway-api: HTTP POST /api/orders
//     └─ gateway-api: gateway.fanout
//          └─ order-api: orders.OrderService/CreateOrder (gRPC server)
//               └─ order-api: order.create   ← Activity.Current?.Id captured
//                                                here into OutboxMessage.TraceParent
//                    └─ order-api: outbox.relay   ← started later, by
//                    │                              OutboxRelayWorker's poll,
//                    │                              parented to the traceparent
//                    │                              above (see that class) —
//                    │                              same trace ID, not a new one
//                    └─ order-api: order.publish  ← PRODUCER span, THIS method;
//                         │                          child of outbox.relay, so
//                         │                          also same trace ID
//                         ┄┄┄(async via RabbitMQ)┄┄┄
//                         notification-svc: notification.process  ← CONSUMER
//                              │                                      span
//                              ├─ notification-svc: redis GET (dedup check)
//                              ├─ notification-svc: redis HSET (store)
//                              └─ notification-svc: notification.send_email
//
// Both async hops (order.create → outbox.relay, and RabbitMQ → notification.process)
// carry an explicit ActivityLink/SpanLink back to the span whose traceparent they
// were built from, in addition to sharing that span's trace ID — the link makes the
// async boundary visually distinct in Jaeger (dashed reference), while the shared
// trace ID means a single trace query surfaces the whole chain. See
// OutboxRelayWorker.PublishAndMarkAsync for the parent-context parsing and its
// comment for why this mirrors, rather than replaces, ADR-002's link-not-parent
// reasoning for the RabbitMQ→notification-svc hop specifically.
//
// Validation target: spec checklist item "Async propagation (critical)":
//   order-api PRODUCER span → RabbitMQ → notification-svc CONSUMER span
//   share the same trace, linked via message headers. This now holds for both
//   the CreateOrder→publish hop and the RabbitMQ→notification-svc hop.
// ============================================================

using System.Diagnostics;
using System.Text;
using OpenTelemetry.Trace;
using RabbitMQ.Client;
using OrderApi.Telemetry;

namespace OrderApi.Messaging;

public class OrderPublisher : IOrderPublisher, IDisposable
{
    private readonly IConnection _connection;
    private readonly IModel _channel;
    private readonly ILogger<OrderPublisher> _logger;

    private const string Exchange = "orders";
    private const string RoutingKey = "order.created";

    public OrderPublisher(IConfiguration config, ILogger<OrderPublisher> logger)
    {
        _logger = logger;

        var host = config["RabbitMQ:Host"]
            ?? throw new InvalidOperationException("RabbitMQ:Host is required.");
        var portStr = config["RabbitMQ:Port"] ?? "5672";
        var user = config["RabbitMQ:User"]
            ?? throw new InvalidOperationException("RabbitMQ:User is required.");
        var password = config["RabbitMQ:Password"]
            ?? throw new InvalidOperationException("RabbitMQ:Password is required.");

        if (!int.TryParse(portStr, out var port) || port < 1 || port > 65535)
            throw new InvalidOperationException($"RabbitMQ:Port is not a valid port number: '{portStr}'");

        try
        {
            var factory = new ConnectionFactory
            {
                HostName = host,
                Port = port,
                UserName = user,
                Password = password,
            };
            _connection = factory.CreateConnection();
            _channel = _connection.CreateModel();

            // Declare the exchange idempotently on startup.
            // The topic exchange lets us route by routing key pattern,
            // e.g. "order.*" to catch all order events in future.
            _channel.ExchangeDeclare(Exchange, ExchangeType.Topic, durable: true);

            _logger.LogInformation("OrderPublisher connected to RabbitMQ at {Host}:{Port}", host, port);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Failed to connect to RabbitMQ at {Host}:{Port}", host, port);
            throw;
        }
    }

    /// <summary>
    /// Publishes <paramref name="jsonPayload"/> to the orders exchange.
    /// The <paramref name="traceParent"/> is written verbatim into the
    /// "traceparent" RabbitMQ header so the notification-svc consumer can
    /// extract it and link its CONSUMER span back to the original request trace.
    /// </summary>
    public Task PublishAsync(string jsonPayload, string? traceParent)
    {
        // ── PRODUCER span ────────────────────────────────────────────────────
        // ActivityKind.Producer tells the OTel SDK this span initiates an
        // async operation. Jaeger renders it with a dashed line to the
        // consumer, making the async relationship visually clear.
        using var activity = DiagnosticsConfig.ActivitySource.StartActivity(
            "order.publish",
            ActivityKind.Producer);

        // OTel Messaging semantic conventions (semconv 1.24):
        //   messaging.system         — identifies the broker type
        //   messaging.destination    — exchange name (where message is sent TO)
        //   messaging.rabbitmq.routing_key — routing key for binding matching
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.destination", Exchange);
        activity?.SetTag("messaging.destination_kind", "exchange");
        activity?.SetTag("messaging.rabbitmq.routing_key", RoutingKey);

        try
        {
            var props = _channel.CreateBasicProperties();
            props.Persistent = true; // survives broker restart
            props.Expiration = "3600000"; // 1h TTL — prevents unbounded queue growth if consumer is down
            props.Headers = new Dictionary<string, object>();

            // ── W3C traceparent injection ─────────────────────────────────────
            // We write the traceparent from the OutboxMessage directly rather
            // than using Propagators.Inject() from Activity.Current, because:
            //   • We're running in OutboxRelayWorker's background context, not
            //     the original request context.
            //   • The stored traceParent IS the original request's context,
            //     saved atomically when the Order was written to the DB.
            // This ensures the notification-svc CONSUMER span is linked to
            // the browser→gateway→order-api trace, not to the relay worker.
            if (!string.IsNullOrEmpty(traceParent))
                props.Headers["traceparent"] = Encoding.UTF8.GetBytes(traceParent);

            _channel.BasicPublish(
                exchange: Exchange,
                routingKey: RoutingKey,
                basicProperties: props,
                body: Encoding.UTF8.GetBytes(jsonPayload));

            _logger.LogInformation(
                "Published order.created. TraceParent: {TraceParent}",
                traceParent ?? "(none)");
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
            _logger.LogError(ex, "Failed to publish order.created to RabbitMQ");
            throw;
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
    }
}
