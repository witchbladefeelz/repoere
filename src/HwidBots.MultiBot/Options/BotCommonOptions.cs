namespace HwidBots.Shared.Options;

public class BotCommonOptions
{
    public const string SectionName = "Bot";

    public long[] AdminIds { get; set; } = Array.Empty<long>();
    public long? AdminGroupId { get; set; }
    public string SupportContact { get; set; } = "@support";
    public string PaymentDetails { get; set; } = "Payment details not configured";
    public Dictionary<int, decimal> Prices { get; set; } = new()
    {
        { 30, 10.00m },
        { 90, 25.00m },
        { 180, 45.00m },
        { 99999, 100.00m }
    };
}
