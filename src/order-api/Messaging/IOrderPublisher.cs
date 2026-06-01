namespace OrderApi.Messaging;

public interface IOrderPublisher
{
    /// <summary>
    /// Publishes <paramref name="jsonPayload"/> to RabbitMQ with the W3C
    /// <paramref name="traceParent"/> injected into message headers.
    /// Called by OutboxRelayWorker — not by OrderGrpcService directly.
    /// </summary>
    Task PublishAsync(string jsonPayload, string? traceParent);
}
