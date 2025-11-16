namespace HwidBots.Shared.Models;

public record UserSummary(long Id, long HwidCount, DateTime? LatestSubscription, long BannedCount);

