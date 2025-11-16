namespace HwidBots.UserBot.Services;

public class UserSessionStore
{
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<string, string>> _resetTokens = new();
    private readonly ConcurrentDictionary<long, string> _awaitingInput = new();

    public void SetResetTokens(long userId, IDictionary<string, string> tokens)
    {
        var userTokens = new ConcurrentDictionary<string, string>(tokens);
        _resetTokens.AddOrUpdate(userId, userTokens, (_, _) => userTokens);
    }

    public bool TryConsumeResetToken(long userId, string token, out string hwid)
    {
        hwid = string.Empty;

        if (!_resetTokens.TryGetValue(userId, out var userTokens))
        {
            return false;
        }

        if (!userTokens.TryRemove(token, out hwid))
        {
            return false;
        }

        if (userTokens.IsEmpty)
        {
            _resetTokens.TryRemove(userId, out _);
        }

        return true;
    }

    public void ClearResetTokens(long userId)
    {
        _resetTokens.TryRemove(userId, out _);
    }

    public void SetAwaitingInput(long userId, string inputType)
    {
        _awaitingInput.AddOrUpdate(userId, inputType, (_, _) => inputType);
    }

    public string? GetAwaitingInput(long userId)
    {
        return _awaitingInput.TryGetValue(userId, out var inputType) ? inputType : null;
    }

    public void ClearAwaitingInput(long userId)
    {
        _awaitingInput.TryRemove(userId, out _);
    }
}
