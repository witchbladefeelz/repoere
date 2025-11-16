namespace HwidBots.Shared.Models;

public record UpdateNotification(
    long Id,
    long VersionId,
    long UserId,
    DateTime NotifiedAt,
    bool Downloaded,
    DateTime? DownloadedAt);
