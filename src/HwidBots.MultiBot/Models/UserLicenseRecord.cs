namespace HwidBots.Shared.Models;

public record UserLicenseRecord
{
    public long Id { get; init; }
    public string Hwid { get; init; } = string.Empty;
    public DateTime Subscription { get; init; }
    public bool Banned { get; init; }
    public string? LastKey { get; init; }
}