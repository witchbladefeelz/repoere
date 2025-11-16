namespace HwidBots.Shared.Models;

public record AdminActionLog(
    long Id,
    long AdminId,
    string ActionType,
    string? TargetType,
    string? TargetId,
    string? Details,
    DateTime CreatedAt
);

