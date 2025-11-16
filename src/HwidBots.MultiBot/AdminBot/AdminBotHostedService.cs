using HwidBots.Shared.Options;
using HwidBots.Shared.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Extensions.Polling;
using Telegram.Bot.Types.Enums;

namespace HwidBots.AdminBot.Services;

public class AdminBotHostedService : IHostedService
{
    private readonly ITelegramBotClient _botClient;
    private readonly AdminBotUpdateHandler _updateHandler;
    private readonly DatabaseService _databaseService;
    private readonly IOptions<BotCommonOptions> _commonOptions;
    private readonly ILogger<AdminBotHostedService> _logger;

    private CancellationTokenSource? _cts;

    public AdminBotHostedService(
        [FromKeyedServices("AdminBot")] ITelegramBotClient botClient,
        AdminBotUpdateHandler updateHandler,
        DatabaseService databaseService,
        IOptions<BotCommonOptions> commonOptions,
        ILogger<AdminBotHostedService> logger)
    {
        _botClient = botClient;
        _updateHandler = updateHandler;
        _databaseService = databaseService;
        _commonOptions = commonOptions;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!await _databaseService.CheckConnectionAsync(cancellationToken))
        {
            _logger.LogError("❌ Failed to connect to database. Admin bot will not start.");
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
        _logger.LogInformation("🛠 Admin bot is starting... Username: @{Username}", me.Username);
        _logger.LogInformation("Admin IDs: {Admins}", string.Join(", ", _commonOptions.Value.AdminIds.Select(id => id.ToString())));
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
        _logger.LogInformation("Admin bot stopped.");
        return Task.CompletedTask;
    }
}

