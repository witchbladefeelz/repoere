namespace HwidBots.Shared.Models;

public class PaymentRequest
{
    public int Id { get; set; }
    public long UserId { get; set; }
    public int Days { get; set; }
    public decimal Amount { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string Status { get; set; } = "pending"; // pending, approved, rejected
    public long? ApprovedBy { get; set; }
    public DateTime? ProcessedAt { get; set; }
}
