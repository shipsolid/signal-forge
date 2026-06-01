namespace OrderApi.Models;

public class Order
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Status { get; set; } = "Created"; // Created, Processing, Completed, Failed
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
