namespace HwidBots.Shared.Models;

public record SubscriptionKeyWithOwner(
    long UserId,
    string SubscriptionKey,
    int Days,
    DateTime? ExpiresAt);
