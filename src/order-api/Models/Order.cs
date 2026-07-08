namespace OrderApi.Models;

public class Order
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Status { get; set; } = "Created"; // Created, Processing, Completed, Failed
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Client-generated key (one per logical CreateOrder attempt, stable across resilience
    // retries). Unique-indexed so a retried write after a connection reset is detected as a
    // duplicate instead of inserting a second order. Null for legacy/direct callers that don't
    // send one — SQL unique indexes never compare two NULLs as equal, so any number of
    // no-key orders can coexist without a filtered-index workaround.
    public string? IdempotencyKey { get; set; }
}
