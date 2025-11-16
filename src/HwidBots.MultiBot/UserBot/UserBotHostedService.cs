using HwidBots.Shared.Options;
using HwidBots.Shared.Services;
using HwidBots.UserBot.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Extensions.Polling;
using Telegram.Bot.Types.Enums;

namespace HwidBots.UserBot.Services;

public class UserBotHostedService : IHostedService
{
    private readonly ITelegramBotClient _botClient;
    private readonly UserBotUpdateHandler _updateHandler;
    private readonly DatabaseService _databaseService;
    private readonly IOptions<UserBotOptions> _botOptions;
    private readonly IOptions<BotCommonOptions> _commonOptions;
    private readonly ILogger<UserBotHostedService> _logger;

    private CancellationTokenSource? _cts;

    public UserBotHostedService(
        [FromKeyedServices("UserBot")] ITelegramBotClient botClient,
        UserBotUpdateHandler updateHandler,
        DatabaseService databaseService,
        IOptions<UserBotOptions> botOptions,
        IOptions<BotCommonOptions> commonOptions,
        ILogger<UserBotHostedService> logger)
    {
        _botClient = botClient;
        _updateHandler = updateHandler;
        _databaseService = databaseService;
        _botOptions = botOptions;
        _commonOptions = commonOptions;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!await _databaseService.CheckConnectionAsync(cancellationToken))
        {
            _logger.LogError("❌ Failed to connect to database. Please check your database configuration.");
            throw new InvalidOperationException("Database connection failed");
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = Array.Empty<UpdateType>()
        };

        _botClient.StartReceiving(
            _updateHandler.HandleUpdateAsync,
            _updateHandler.HandlePollingErrorAsync,
            receiverOptions,
            cancellationToken: _cts.Token);

        var me = await _botClient.GetMeAsync(cancellationToken);

        _logger.LogInformation("🤖 Main bot is starting... Username: @{Username}", me.Username);
        _logger.LogInformation(
            "Registered admins: {Admins}",
            string.Join(", ", _commonOptions.Value.AdminIds.Select(id => id.ToString())));
        _logger.LogInformation("Lifetime threshold: {Days} days", _botOptions.Value.LifetimeDaysThreshold);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is null)
        {
            return Task.CompletedTask;
        }

        _cts.Cancel();
        _cts.Dispose();
        _cts = null;
        _logger.LogInformation("Main bot stopped.");
        return Task.CompletedTask;
    }
}

