namespace HwidBots.Shared.Models;

public record HwidResetRequest(int Id, long UserId, string Hwid, string Status, DateTime CreatedAt);


