namespace HwidBots.UserBot.Options;

public class UserBotOptions
{
    public const string SectionName = "UserBot";

    public string BotToken { get; set; } = string.Empty;
    public int LifetimeDaysThreshold { get; set; } = 99999;
}

