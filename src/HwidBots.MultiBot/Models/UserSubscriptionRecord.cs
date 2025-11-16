namespace HwidBots.Shared.Models;

public class UserSubscriptionRecord
{
    public long UserId { get; set; }
    public string? Hwid { get; set; }
    public DateTime? SubscriptionExpiry { get; set; }
    public bool HasHwid { get; set; }
    public string Source { get; set; } = "users"; // "users" or "subscriptions"
}
