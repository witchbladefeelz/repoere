using Microsoft.Extensions.Logging;

namespace HwidBots.AdminBot.Services;

public class AdminSessionStore
{
    private readonly ConcurrentDictionary<long, string> _pendingActions = new();
    private readonly ILogger<AdminSessionStore> _logger;

    public AdminSessionStore(ILogger<AdminSessionStore> logger)
    {
        _logger = logger;
    }

    public void SetPendingAction(long userId, string action)
    {
        _pendingActions[userId] = action;
        _logger.LogInformation("Set pending action for {UserId}: {Action}", userId, action);
    }

    public bool TryGetPendingAction(long userId, out string? action)
    {
        var result = _pendingActions.TryGetValue(userId, out action);
        _logger.LogInformation("Get pending action for {UserId}: {Found} - {Action}", userId, result, action ?? "null");
        return result;
    }

    public void ClearPendingAction(long userId)
    {
        _pendingActions.TryRemove(userId, out _);
        _logger.LogInformation("Cleared pending action for {UserId}", userId);
    }
}
