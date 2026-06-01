namespace OrderApi.Models;

/// <summary>
/// Outbox record written atomically with the parent Order in a single SaveChanges call.
/// OutboxRelayWorker reads unprocessed rows, publishes to RabbitMQ, then marks ProcessedAt.
/// Storing the traceparent here preserves the original request's trace context across the
/// async boundary so notification-svc CONSUMER spans link back to the right browser trace.
/// </summary>
public class OutboxMessage
{
    public int Id { get; set; }

    /// <summary>Navigation property — EF Core resolves the FK after SaveChanges.</summary>
    public Order Order { get; set; } = null!;

    /// <summary>W3C traceparent captured from Activity.Current at write time.</summary>
    public string? TraceParent { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>Null until OutboxRelayWorker has successfully published to RabbitMQ.</summary>
    public DateTime? ProcessedAt { get; set; }
}
