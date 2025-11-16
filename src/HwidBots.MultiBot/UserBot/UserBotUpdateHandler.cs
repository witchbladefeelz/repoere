using System.IO.Compression;
using System.Text;
using HwidBots.Shared.Models;
using HwidBots.Shared.Options;
using HwidBots.Shared.Services;
using HwidBots.UserBot.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace HwidBots.UserBot.Services;

public class UserBotUpdateHandler
{
    private readonly ILicenseRepository _repository;
    private readonly BotCommonOptions _commonOptions;
    private readonly UserBotOptions _options;
    private readonly UserSessionStore _sessionStore;
    private readonly ILogger<UserBotUpdateHandler> _logger;
    private readonly ITelegramBotClient _adminBotClient;
    private readonly CryptoRateService _cryptoRateService;

    private const string ViewKeysButton = "🔑 View My Keys";
    private const string ProfileButton = "👤 Profile";
    private const string PurchaseButton = "💳 Purchase Subscription";
    private const string DownloadButton = "📥 Download";
    private const string SupportButton = "💬 Support";

    public UserBotUpdateHandler(
        ILicenseRepository repository,
        IOptions<BotCommonOptions> commonOptions,
        IOptions<UserBotOptions> options,
        UserSessionStore sessionStore,
        ILogger<UserBotUpdateHandler> logger,
        [FromKeyedServices("AdminBot")] ITelegramBotClient adminBotClient,
        CryptoRateService cryptoRateService)
    {
        _repository = repository;
        _commonOptions = commonOptions.Value;
        _options = options.Value;
        _sessionStore = sessionStore;
        _logger = logger;
        _adminBotClient = adminBotClient;
        _cryptoRateService = cryptoRateService;
    }

    public async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        try
        {
            // Check if user has access (only specific users in allowed group or private chat)
            long userId = update.Message?.From?.Id ?? update.CallbackQuery?.From?.Id ?? 0;
            long chatId = update.Message?.Chat.Id ?? update.CallbackQuery?.Message?.Chat.Id ?? 0;

            // Allow only specific user (8123918703) in group -1003486935434 or any user in private chat
            const long allowedGroupId = -1003486935434;
            const long allowedUserId = 8123918703;

            if (chatId == allowedGroupId)
            {
                // In the allowed group, only specific user can interact
                if (userId != allowedUserId)
                {
                    _logger.LogWarning("Unauthorized user {UserId} tried to access bot in group {GroupId}", userId, chatId);
                    return;
                }
            }
            else if (chatId < 0)
            {
                // Other groups are not allowed
                _logger.LogWarning("Bot used in unauthorized group {GroupId} by user {UserId}", chatId, userId);
                return;
            }
            // Private chats (chatId > 0) are allowed for everyone

            switch (update.Type)
            {
                case UpdateType.Message when update.Message is not null:
                    await HandleMessageAsync(botClient, update.Message, cancellationToken);
                    break;
                case UpdateType.CallbackQuery when update.CallbackQuery is not null:
                    await HandleCallbackAsync(botClient, update.CallbackQuery, cancellationToken);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process update {@Update}", update);
        }
    }

    public Task HandlePollingErrorAsync(ITelegramBotClient _, Exception exception, CancellationToken __)
    {
        var errorMessage = exception switch
        {
            ApiRequestException apiEx => $"Telegram API Error [{apiEx.ErrorCode}]: {apiEx.Message}",
            _ => exception.Message
        };

        _logger.LogError(exception, "Polling error: {Message}", errorMessage);
        return Task.CompletedTask;
    }

    private async Task HandleMessageAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        if (message.Text is null)
        {
            return;
        }

        var text = message.Text.Trim();

        // Determine where to send response (group -> DM, private -> same chat)
        var responseChat = message.Chat.Type == Telegram.Bot.Types.Enums.ChatType.Private
            ? message.Chat.Id
            : message.From!.Id;

        // Check if user is awaiting input for key transfer
        var awaitingInput = _sessionStore.GetAwaitingInput(message.From!.Id);
        if (!string.IsNullOrEmpty(awaitingInput) && awaitingInput.StartsWith("transfer_key_"))
        {
            await HandleTransferKeyInputAsync(botClient, message, awaitingInput, cancellationToken);
            return;
        }

        if (text.StartsWith("/"))
        {
            await HandleCommandAsync(botClient, message, text, responseChat, cancellationToken);
            return;
        }

        switch (text)
        {
            case ViewKeysButton:
                await SendActiveKeysAsync(botClient, responseChat, cancellationToken);
                break;
            case ProfileButton:
                await SendProfileAsync(botClient, responseChat, cancellationToken);
                break;
            case PurchaseButton:
                await SendPurchaseOptionsAsync(botClient, responseChat, cancellationToken);
                break;
            case DownloadButton:
                await HandleDownloadRequestAsync(botClient, responseChat, cancellationToken);
                break;
            case SupportButton:
                await SendSupportInfoAsync(botClient, responseChat, cancellationToken);
                break;
        }
    }

    private async Task HandleCommandAsync(ITelegramBotClient botClient, Message message, string command, long responseChat, CancellationToken cancellationToken)
    {
        var userId = message.From!.Id;

        switch (command.Split(' ')[0])
        {
            case "/start":
                var hasActive = await _repository.HasActiveSubscriptionOrKeysAsync(userId, cancellationToken);
                await botClient.SendTextMessageAsync(
                    responseChat,
                    """
                    🤖 *Subscription Sales Bot*

                    This bot handles subscription key generation for successful purchases.

                    Choose an option below:
                    """,
                    ParseMode.Markdown,
                    replyMarkup: BuildMainKeyboard(hasActive),
                    cancellationToken: cancellationToken);
                break;

            case "/purchase":
                await HandlePurchaseCommandAsync(botClient, message, responseChat, cancellationToken);
                break;

            case "/mykeys":
                await SendActiveKeysAsync(botClient, responseChat, cancellationToken);
                break;

            case "/profile":
                await SendProfileAsync(botClient, responseChat, cancellationToken);
                break;

            default:
                await botClient.SendTextMessageAsync(
                    responseChat,
                    "⚠️ Unknown command. Use /start to see available options.",
                    cancellationToken: cancellationToken);
                break;
        }
    }

    private async Task HandlePurchaseCommandAsync(ITelegramBotClient botClient, Message message, long responseChat, CancellationToken cancellationToken)
    {
        var parts = message.Text?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
        if (parts.Length != 2 || !int.TryParse(parts[1], out var days) || days <= 0)
        {
            await botClient.SendTextMessageAsync(
                responseChat,
                "Usage: /purchase <days>",
                cancellationToken: cancellationToken);
            return;
        }

        await ProcessPurchaseAsync(botClient, message.Chat.Id, message.Chat.Id, days, $"{days}-Day Subscription", cancellationToken);
    }

    private async Task HandleCallbackAsync(ITelegramBotClient botClient, CallbackQuery callbackQuery, CancellationToken cancellationToken)
    {
        if (callbackQuery.Data is null)
        {
            return;
        }

        _logger.LogInformation("Received callback: {CallbackData}", callbackQuery.Data);

        // Check specific purchase modes first before generic purchase_ handler
        if (callbackQuery.Data == "purchase_mode_extend")
        {
            await botClient.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);
            await ShowUserKeysForExtensionAsync(botClient, callbackQuery.Message!.Chat.Id, callbackQuery.Message.MessageId, cancellationToken);
            return;
        }

        if (callbackQuery.Data == "purchase_mode_new")
        {
            await botClient.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);
            await botClient.EditMessageTextAsync(
                callbackQuery.Message!.Chat.Id,
                callbackQuery.Message.MessageId,
                """
                💳 *Purchase New Subscription*

                Choose subscription duration:
                """,
                ParseMode.Markdown,
                replyMarkup: BuildPurchaseKeyboard("new"),
                cancellationToken: cancellationToken);
            return;
        }

        if (callbackQuery.Data.StartsWith("purchase_", StringComparison.OrdinalIgnoreCase))
        {
            await HandlePurchaseCallbackAsync(botClient, callbackQuery, cancellationToken);
            return;
        }

        if (callbackQuery.Data.StartsWith("reset_hwid|", StringComparison.OrdinalIgnoreCase))
        {
            await HandleResetRequestAsync(botClient, callbackQuery, cancellationToken);
            return;
        }

        if (callbackQuery.Data == "download_update")
        {
            await botClient.AnswerCallbackQueryAsync(callbackQuery.Id, "⏳ Preparing download...", cancellationToken: cancellationToken);
            await HandleDownloadRequestAsync(botClient, callbackQuery.Message!.Chat.Id, cancellationToken);
            return;
        }

        if (callbackQuery.Data.StartsWith("extend_key_", StringComparison.OrdinalIgnoreCase))
        {
            await HandleExtendKeySelectionAsync(botClient, callbackQuery, cancellationToken);
            return;
        }

        if (callbackQuery.Data.StartsWith("extend_sub_", StringComparison.OrdinalIgnoreCase) && !callbackQuery.Data.Contains("_confirm_") && !callbackQuery.Data.Contains("_final_"))
        {
            await HandleExtendSubscriptionSelectionAsync(botClient, callbackQuery, cancellationToken);
            return;
        }

        if (callbackQuery.Data.StartsWith("extend_confirm_", StringComparison.OrdinalIgnoreCase))
        {
            await HandleExtendConfirmAsync(botClient, callbackQuery, cancellationToken);
            return;
        }

        if (callbackQuery.Data.StartsWith("extend_sub_confirm_", StringComparison.OrdinalIgnoreCase))
        {
            await HandleExtendSubscriptionConfirmAsync(botClient, callbackQuery, cancellationToken);
            return;
        }

        if (callbackQuery.Data.StartsWith("extend_sub_final_", StringComparison.OrdinalIgnoreCase))
        {
            await HandleExtendSubscriptionFinalAsync(botClient, callbackQuery, cancellationToken);
            return;
        }

        if (callbackQuery.Data.StartsWith("payment_confirm_", StringComparison.OrdinalIgnoreCase))
        {
            await HandlePaymentConfirmAsync(botClient, callbackQuery, cancellationToken);
            return;
        }

        if (callbackQuery.Data.StartsWith("extend_final_", StringComparison.OrdinalIgnoreCase))
        {
            await HandleExtendFinalAsync(botClient, callbackQuery, cancellationToken);
            return;
        }

        if (callbackQuery.Data.StartsWith("manage_key_", StringComparison.OrdinalIgnoreCase))
        {
            await HandleManageKeyAsync(botClient, callbackQuery, cancellationToken);
            return;
        }

        if (callbackQuery.Data.StartsWith("transfer_key_", StringComparison.OrdinalIgnoreCase))
        {
            await HandleTransferKeyRequestAsync(botClient, callbackQuery, cancellationToken);
            return;
        }

        if (callbackQuery.Data.StartsWith("use_key_select_", StringComparison.OrdinalIgnoreCase))
        {
            await HandleUseKeySelectAsync(botClient, callbackQuery, cancellationToken);
            return;
        }

        if (callbackQuery.Data.StartsWith("use_key_", StringComparison.OrdinalIgnoreCase))
        {
            await HandleUseKeyAsync(botClient, callbackQuery, cancellationToken);
            return;
        }

        if (callbackQuery.Data == "keys_back")
        {
            await botClient.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);
            await botClient.DeleteMessageAsync(callbackQuery.Message!.Chat.Id, callbackQuery.Message.MessageId, cancellationToken);
            return;
        }

        if (callbackQuery.Data == "purchase_back")
        {
            await botClient.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);
            await botClient.DeleteMessageAsync(callbackQuery.Message!.Chat.Id, callbackQuery.Message.MessageId, cancellationToken);
            await SendPurchaseOptionsAsync(botClient, callbackQuery.Message.Chat.Id, cancellationToken);
            return;
        }
    }

    private async Task HandlePurchaseCallbackAsync(ITelegramBotClient botClient, CallbackQuery query, CancellationToken cancellationToken)
    {
        await botClient.AnswerCallbackQueryAsync(query.Id, cancellationToken: cancellationToken);

        var payload = query.Data![9..];
        int days;
        string productName;

        if (string.Equals(payload, "lifetime", StringComparison.OrdinalIgnoreCase))
        {
            days = _options.LifetimeDaysThreshold;
            productName = "Lifetime Subscription";
        }
        else if (!int.TryParse(payload, out days))
        {
            await botClient.EditMessageTextAsync(
                query.Message!.Chat.Id,
                query.Message.MessageId,
                "❌ Invalid subscription duration",
                cancellationToken: cancellationToken);
            return;
        }
        else
        {
            productName = $"{days}-Day Subscription";
        }

        await botClient.EditMessageTextAsync(
            query.Message!.Chat.Id,
            query.Message.MessageId,
            "⏳ Processing your purchase...",
            cancellationToken: cancellationToken);

        await ProcessPurchaseAsync(botClient, query.Message.Chat.Id, query.From.Id, days, productName, cancellationToken, query.Message.MessageId);
    }

    private async Task ProcessPurchaseAsync(
        ITelegramBotClient botClient,
        long chatId,
        long userId,
        int days,
        string productName,
        CancellationToken cancellationToken,
        int? messageIdToEdit = null)
    {
        try
        {
            // Get price for this duration
            var amount = _commonOptions.Prices.TryGetValue(days, out var price) ? price : 0m;

            // Create payment request
            var paymentRequest = await _repository.CreatePaymentRequestAsync(userId, days, amount, productName, cancellationToken);

            var durationText = days >= _options.LifetimeDaysThreshold ? "Lifetime (Forever)" : $"{days} days";

            // Get real-time crypto rates
            var cryptoAmounts = await _cryptoRateService.CalculateCryptoAmountsAsync(amount, cancellationToken);

            var messageBuilder = new StringBuilder()
                .AppendLine("💳 *Payment Information*")
                .AppendLine()
                .AppendLine($"📦 **Product:** {productName}")
                .AppendLine($"📅 **Duration:** {durationText}")
                .AppendLine($"💰 **Amount:** ${amount:F2} USD")
                .AppendLine()
                .AppendLine("*Payment Methods (Real-time rates):*")
                .AppendLine()
                .AppendLine($"💵 **USDT (TRC20):** ≈ {cryptoAmounts.usdt:F2} USDT")
                .AppendLine("`TJENGDvcggQdpwk9DyjHm7KkbULVkQgaN7`")
                .AppendLine()
                .AppendLine($"💎 **Toncoin:** ≈ {cryptoAmounts.ton:F4} TON")
                .AppendLine("`UQCB5PeJpBOX0KGTRw8hryP74NPIenR3MlB7pjdEIlw0Wtj0`")
                .AppendLine()
                .AppendLine($"₿ **Bitcoin:** ≈ {cryptoAmounts.btc:F8} BTC")
                .AppendLine("`bc1qzxqknkrqls93tnmjxg69ne4tpdamzpyfvhsc63`")
                .AppendLine()
                .AppendLine($"🔷 **Ethereum Classic:** ≈ {cryptoAmounts.etc:F4} ETC")
                .AppendLine("`0xE2e1aD74A700E920da6fbD555fF7d46010535E17`")
                .AppendLine()
                .AppendLine($"📞 **Support:** {_commonOptions.SupportContact}")
                .AppendLine()
                .AppendLine("⚠️ *After payment, click the button below to notify us.*");

            var text = messageBuilder.ToString();

            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("✅ I have paid", $"payment_confirm_{paymentRequest.Id}")
                }
            });

            if (messageIdToEdit.HasValue)
            {
                await botClient.EditMessageTextAsync(
                    chatId,
                    messageIdToEdit.Value,
                    text,
                    ParseMode.Markdown,
                    replyMarkup: keyboard,
                    cancellationToken: cancellationToken);
            }
            else
            {
                await botClient.SendTextMessageAsync(
                    chatId,
                    text,
                    ParseMode.Markdown,
                    replyMarkup: keyboard,
                    cancellationToken: cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process purchase for user {UserId}", userId);
            var errorMessage = $"❌ Error processing your purchase: {ex.Message}";

            if (messageIdToEdit.HasValue)
            {
                await botClient.EditMessageTextAsync(
                    chatId,
                    messageIdToEdit.Value,
                    errorMessage,
                    cancellationToken: cancellationToken);
            }
            else
            {
                await botClient.SendTextMessageAsync(
                    chatId,
                    errorMessage,
                    cancellationToken: cancellationToken);
            }
        }
    }

    private async Task<string> CreateSubscriptionKeyAsync(long userId, int days, CancellationToken cancellationToken)
    {
        var key = SubscriptionKeyGenerator.Generate();
        // Don't set expires_at on creation - it will be set on first activation
        await _repository.InsertSubscriptionKeyAsync(userId, key, days, expiresAt: null, cancellationToken);
        _logger.LogInformation("Created key {Key} for user {UserId} with {Days} days", key, userId, days);
        return key;
    }

    private async Task SendActiveKeysAsync(ITelegramBotClient botClient, long chatId, CancellationToken cancellationToken)
    {
        try
        {
            var keys = await _repository.GetActiveKeysAsync(chatId, cancellationToken);
            if (keys.Count == 0)
            {
                await botClient.SendTextMessageAsync(
                    chatId,
                    "❌ You don't have any active subscription keys.",
                    cancellationToken: cancellationToken);
                return;
            }

            var now = DateTime.UtcNow;
            var builder = new StringBuilder("📋 *Your Active Subscription Keys:*\n\n");

            var keyButtons = new List<InlineKeyboardButton[]>();
            int index = 0;

            foreach (var key in keys)
            {
                string validityText;
                if (key.ExpiresAt is null)
                {
                    validityText = "Lifetime";
                }
                else if (key.ExpiresAt > now)
                {
                    var remaining = key.ExpiresAt.Value - now;
                    var remainingDays = (int)Math.Ceiling(remaining.TotalDays);
                    validityText = $"{remainingDays} day(s) left (expires {key.ExpiresAt:yyyy-MM-dd HH:mm} UTC)";
                }
                else
                {
                    validityText = $"Expired on {key.ExpiresAt:yyyy-MM-dd HH:mm} UTC";
                }

                builder.AppendLine($"{index + 1}. `{key.SubscriptionKey}` — {key.Days} day(s) | {validityText}");

                // Add button for each key
                keyButtons.Add(new[]
                {
                    InlineKeyboardButton.WithCallbackData($"🔑 Manage Key #{index + 1}", $"manage_key_{index}")
                });

                index++;
            }

            builder.AppendLine("\n💡 *Select a key to manage it*");

            // Add management buttons at the bottom
            keyButtons.Add(new[]
            {
                InlineKeyboardButton.WithCallbackData("🔙 Back to Menu", "keys_back")
            });

            var keyboard = new InlineKeyboardMarkup(keyButtons);

            await botClient.SendTextMessageAsync(
                chatId,
                builder.ToString(),
                ParseMode.Markdown,
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load keys for user {UserId}", chatId);
            await botClient.SendTextMessageAsync(
                chatId,
                $"❌ Database error: {ex.Message}",
                cancellationToken: cancellationToken);
        }
    }

    private async Task SendProfileAsync(ITelegramBotClient botClient, long chatId, CancellationToken cancellationToken)
    {
        try
        {
            var licensesTask = _repository.GetLicensesAsync(chatId, cancellationToken);
            var keysTask = _repository.GetActiveKeysAsync(chatId, cancellationToken);
            var statsTask = _repository.GetSubscriptionKeyStatsAsync(chatId, cancellationToken);

            await Task.WhenAll(licensesTask, statsTask, keysTask);

            var licenses = licensesTask.Result;
            var keyStats = statsTask.Result;
            var keys = keysTask.Result;

            var builder = new StringBuilder()
                .AppendLine("👤 *Your Profile*\n")
                .AppendLine($"🆔 *Your ID:* `{chatId}`")
                .AppendLine($"🔑 *Active Keys:* {keyStats.TotalKeys}")
                .AppendLine($"📊 *Total Days Purchased:* {keyStats.TotalDays}")
                .AppendLine();

            if (keys.Count > 0)
            {
                builder.AppendLine("🔑 *Available Activation Keys:*");
                var nowForKeys = DateTime.UtcNow;

                foreach (var key in keys)
                {
                    string validityText;
                    if (key.ExpiresAt is null)
                    {
                        validityText = "Lifetime";
                    }
                    else if (key.ExpiresAt > nowForKeys)
                    {
                        var remaining = key.ExpiresAt.Value - nowForKeys;
                        var remainingDays = (int)Math.Ceiling(remaining.TotalDays);
                        validityText = $"{remainingDays} day(s) left (expires {key.ExpiresAt:yyyy-MM-dd HH:mm} UTC)";
                    }
                    else
                    {
                        validityText = $"Expired on {key.ExpiresAt:yyyy-MM-dd HH:mm} UTC";
                    }

                    builder.AppendLine($"   • `{key.SubscriptionKey}` — {key.Days} day(s) | {validityText}");
                }
                builder.AppendLine();
            }

            if (licenses.Count == 0)
            {
                builder.AppendLine("❌ *No HWID registered yet.*\n")
                       .AppendLine("To activate subscription:")
                       .AppendLine("1. Get a key with /purchase")
                       .AppendLine("2. Use it in your application with HWID");

                await botClient.SendTextMessageAsync(
                    chatId,
                    builder.ToString(),
                    ParseMode.Markdown,
                    cancellationToken: cancellationToken);

                _sessionStore.ClearResetTokens(chatId);
                return;
            }

            var activeTokens = new Dictionary<string, string>();
            builder.AppendLine($"💻 *Registered HWIDs ({licenses.Count}):*\n");

            var now = DateTime.UtcNow;
            var idx = 1;

            foreach (var license in licenses)
            {
                var subscriptionUtc = DateTime.SpecifyKind(license.Subscription, DateTimeKind.Utc);
                var diff = subscriptionUtc - now;
                string status;

                if (diff > TimeSpan.Zero)
                {
                    var daysLeft = (int)diff.TotalDays;
                    var hoursLeft = (int)(diff - TimeSpan.FromDays(daysLeft)).TotalHours;
                    status = $"✅ Active ({daysLeft}d {hoursLeft}h left)";

                    if (!license.Banned)
                    {
                        var token = Guid.NewGuid().ToString("N")[..10];
                        activeTokens[token] = license.Hwid;
                    }
                }
                else
                {
                    status = "❌ Expired";
                }

                var bannedStatus = license.Banned ? "🚫 Banned" : "✅ Active";

                builder.AppendLine($"*{idx}. HWID:* `{license.Hwid}`")
                       .AppendLine($"   📅 Expires: {license.Subscription:yyyy-MM-dd HH:mm:ss}")
                       .AppendLine($"   📊 Status: {status}")
                       .AppendLine($"   🔒 Ban: {bannedStatus}\n");

                idx++;
            }

            IReplyMarkup? replyMarkup = null;

            if (activeTokens.Count > 0)
            {
                var buttons = activeTokens
                    .Select((pair, index) =>
                    {
                        var displayHwid = pair.Value.Length <= 18 ? pair.Value : $"{pair.Value[..15]}...";
                        return new[]
                        {
                            InlineKeyboardButton.WithCallbackData(
                                $"🔁 Reset HWID #{index + 1} ({displayHwid})",
                                $"reset_hwid|{pair.Key}")
                        };
                    })
                    .ToArray();

                replyMarkup = new InlineKeyboardMarkup(buttons);
                builder.AppendLine("🔁 *Request HWID Reset:* use the buttons below to submit a request.");
                _sessionStore.SetResetTokens(chatId, activeTokens);
            }
            else
            {
                _sessionStore.ClearResetTokens(chatId);
            }

            await botClient.SendTextMessageAsync(
                chatId,
                builder.ToString(),
                ParseMode.Markdown,
                replyMarkup: replyMarkup,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load profile for user {UserId}", chatId);
            await botClient.SendTextMessageAsync(
                chatId,
                $"❌ Database error: {ex.Message}",
                cancellationToken: cancellationToken);
        }
    }

    private async Task HandleResetRequestAsync(ITelegramBotClient botClient, CallbackQuery query, CancellationToken cancellationToken)
    {
        if (query.Data is null)
        {
            return;
        }

        var parts = query.Data.Split('|', 2);
        if (parts.Length != 2)
        {
            await botClient.AnswerCallbackQueryAsync(
                query.Id,
                "Invalid request.",
                showAlert: true,
                cancellationToken: cancellationToken);
            return;
        }

        var token = parts[1];
        if (!_sessionStore.TryConsumeResetToken(query.From.Id, token, out var hwid))
        {
            await botClient.AnswerCallbackQueryAsync(
                query.Id,
                "This button is no longer valid. Open your profile again.",
                showAlert: true,
                cancellationToken: cancellationToken);
            return;
        }

        try
        {
            var record = await _repository.GetLicenseAsync(query.From.Id, hwid, cancellationToken);
            if (record is null)
            {
                await botClient.AnswerCallbackQueryAsync(
                    query.Id,
                    "HWID not found.",
                    showAlert: true,
                    cancellationToken: cancellationToken);
                return;
            }

            if (record.Banned)
            {
                await botClient.AnswerCallbackQueryAsync(
                    query.Id,
                    "This HWID is banned.",
                    showAlert: true,
                    cancellationToken: cancellationToken);
                return;
            }

            var now = DateTime.UtcNow;
            if (record.Subscription <= now)
            {
                await botClient.AnswerCallbackQueryAsync(
                    query.Id,
                    "The subscription for this HWID has expired.",
                    showAlert: true,
                    cancellationToken: cancellationToken);
                return;
            }

            if (await _repository.HasPendingResetRequestAsync(query.From.Id, hwid, cancellationToken))
            {
                await botClient.AnswerCallbackQueryAsync(
                    query.Id,
                    "A reset request is already pending.",
                    showAlert: true,
                    cancellationToken: cancellationToken);
                return;
            }

            var request = await _repository.InsertResetRequestAsync(query.From.Id, hwid, cancellationToken);

            await botClient.AnswerCallbackQueryAsync(
                query.Id,
                "Request submitted.",
                cancellationToken: cancellationToken);

            await botClient.SendTextMessageAsync(
                query.Message!.Chat.Id,
                $"✅ HWID reset request for `{hwid}` has been submitted. Please wait for confirmation.",
                ParseMode.Markdown,
                cancellationToken: cancellationToken);

            await NotifyAdminsAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle reset request for user {UserId} ({Hwid})", query.From.Id, hwid);
            await botClient.AnswerCallbackQueryAsync(
                query.Id,
                "Failed to process the request.",
                showAlert: true,
                cancellationToken: cancellationToken);
        }
    }

    private async Task NotifyAdminsAsync(HwidResetRequest request, CancellationToken cancellationToken)
    {
        if (_commonOptions.AdminIds.Length == 0)
        {
            return;
        }

        var message = new StringBuilder()
            .AppendLine("📨 *HWID Reset Request*\n")
            .AppendLine($"🆔 Request ID: `{request.Id}`")
            .AppendLine($"👤 User ID: `{request.UserId}`")
            .AppendLine($"💻 HWID: `{request.Hwid}`")
            .Append($"🕒 Time: {request.CreatedAt:yyyy-MM-dd HH:mm:ss} UTC")
            .ToString();

        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("✅ Approve", $"reset_admin|approve|{request.Id}"),
                InlineKeyboardButton.WithCallbackData("🚫 Reject", $"reset_admin|reject|{request.Id}")
            }
        });

        foreach (var adminId in _commonOptions.AdminIds)
        {
            try
            {
                await _adminBotClient.SendTextMessageAsync(
                    adminId,
                    message,
                    ParseMode.Markdown,
                    replyMarkup: keyboard,
                    cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to notify admin {AdminId}", adminId);
            }
        }
    }

    private static IReplyMarkup BuildMainKeyboard(bool hasActiveSubscription = false)
    {
        var buttons = new List<KeyboardButton[]>
        {
            new KeyboardButton[] { ViewKeysButton, ProfileButton }
        };

        if (hasActiveSubscription)
        {
            buttons.Add(new KeyboardButton[] { DownloadButton, PurchaseButton });
        }
        else
        {
            buttons.Add(new KeyboardButton[] { PurchaseButton });
        }

        buttons.Add(new KeyboardButton[] { SupportButton });

        return new ReplyKeyboardMarkup(buttons)
        {
            ResizeKeyboard = true
        };
    }

    private static InlineKeyboardMarkup BuildPurchaseKeyboard(string mode)
    {
        var prefix = mode == "extend" ? "extend_duration_" : "purchase_";

        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("🔸 30 Days", $"{prefix}30"),
                InlineKeyboardButton.WithCallbackData("🔶 90 Days ⭐", $"{prefix}90")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("🔷 180 Days 💎", $"{prefix}180"),
                InlineKeyboardButton.WithCallbackData("♾️ Lifetime 🚀", $"{prefix}lifetime")
            }
        });
    }

    private async Task SendPurchaseOptionsAsync(ITelegramBotClient botClient, long chatId, CancellationToken cancellationToken)
    {
        // Check if user has active subscriptions (from users table)
        var activeSubscriptions = await _repository.GetActiveSubscriptionsAsync(chatId, cancellationToken);

        if (activeSubscriptions.Count > 0)
        {
            // User has active subscription - show choice between Extend and Buy New
            var sub = activeSubscriptions[0];
            var daysLeft = Math.Max(0, (sub.SubscriptionExpiry!.Value - DateTime.UtcNow).Days);
            var hwidStatus = sub.HasHwid ? $"🖥️ HWID: {sub.Hwid![..8]}..." : "🆓 No HWID";

            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("🔄 Extend Subscription", "purchase_mode_extend")
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("🆕 Buy New Key", "purchase_mode_new")
                }
            });

            await botClient.SendTextMessageAsync(
                chatId,
                $"""
                💳 *Purchase Subscription*

                📊 *Your Status:*
                ✅ Active subscription
                {hwidStatus}
                ⏱️ {daysLeft} days left

                Choose an option:
                🔄 *Extend* - Add days to your active subscription
                🆕 *Buy New* - Get a new subscription key
                """,
                ParseMode.Markdown,
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);
        }
        else
        {
            // No active subscription - directly show purchase options
            await botClient.SendTextMessageAsync(
                chatId,
                """
                💳 *Purchase Subscription*

                Choose subscription duration:

                Select one of the options below:
                """,
                ParseMode.Markdown,
                replyMarkup: BuildPurchaseKeyboard("new"),
                cancellationToken: cancellationToken);
        }
    }

    private async Task HandleDownloadRequestAsync(ITelegramBotClient botClient, long chatId, CancellationToken cancellationToken)
    {
        try
        {
            // Check if user has active subscription or keys
            var hasActive = await _repository.HasActiveSubscriptionOrKeysAsync(chatId, cancellationToken);
            _logger.LogInformation("User {UserId} download check: hasActive={HasActive}", chatId, hasActive);

            if (!hasActive)
            {
                // Update keyboard to hide Download button
                await botClient.SendTextMessageAsync(
                    chatId,
                    "❌ You need an active subscription or valid key to download the product.\n\n" +
                    "Please purchase a subscription first using the 💳 Purchase button.",
                    replyMarkup: BuildMainKeyboard(hasActiveSubscription: false),
                    cancellationToken: cancellationToken);
                return;
            }

            // Get latest version
            var latestVersion = await _repository.GetLatestProductVersionAsync(cancellationToken);

            if (latestVersion is null)
            {
                await botClient.SendTextMessageAsync(
                    chatId,
                    "❌ No product version available yet. Please contact support.",
                    cancellationToken: cancellationToken);
                return;
            }

            // Mark as downloaded
            await _repository.MarkUpdateAsDownloadedAsync(latestVersion.Id, chatId, cancellationToken);

            // Generate encrypted filename and password with timestamp
            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            var dataToEncrypt = $"{chatId}_{timestamp}";
            var encryptedName = XorEncrypt(chatId.ToString(), "koharuxf");
            var password = Base58Encode(XorEncrypt(dataToEncrypt, "koharuxf"));
            var newFileName = $"{encryptedName}.zip";

            // Check if file exists on server
            if (!System.IO.File.Exists(latestVersion.FileId))
            {
                _logger.LogError("File not found on server");
                await botClient.SendTextMessageAsync(
                    chatId,
                    "❌ File not found on server. Please contact support.",
                    cancellationToken: cancellationToken);
                return;
            }

            // Create temp directory for processing
            var tempDir = Path.Combine(Path.GetTempPath(), "hwid_bot", Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);
            var outputFilePath = Path.Combine(tempDir, newFileName);

            try
            {
                // Create password-protected zip archive using DotNetZip
                using (var zip = new Ionic.Zip.ZipFile())
                {
                    zip.Password = password;
                    zip.Encryption = Ionic.Zip.EncryptionAlgorithm.WinZipAes256;

                    var originalFileName = Path.GetFileName(latestVersion.FileId);
                    zip.AddFile(latestVersion.FileId, "").FileName = originalFileName;

                    zip.Save(outputFilePath);
                }

                _logger.LogInformation("Created password-protected archive for user {UserId}", chatId);

                // Send file with encrypted name and password info
                var caption = $"📦 *Version {latestVersion.Version}*\n\n" +
                             $"🔐 *Your Archive Password:*\n`{password}`\n\n" +
                             $"📄 File: `{newFileName}`\n" +
                             $"📦 Size: {FormatFileSize(latestVersion.FileSize)}\n" +
                             $"📅 Released: {latestVersion.CreatedAt:yyyy-MM-dd HH:mm}";

                if (!string.IsNullOrEmpty(latestVersion.UpdateLog))
                {
                    caption += $"\n\n📝 *What's New:*\n{latestVersion.UpdateLog}";
                }

                caption += "\n\n⚠️ *Important:* Keep your password safe! You'll need it to extract the archive.";

                await using (var fileStream = System.IO.File.OpenRead(outputFilePath))
                {
                    var inputFile = new Telegram.Bot.Types.InputFiles.InputOnlineFile(fileStream, newFileName);
                    await botClient.SendDocumentAsync(
                        chatId,
                        document: inputFile,
                        caption: caption,
                        parseMode: ParseMode.Markdown,
                        cancellationToken: cancellationToken);
                }

                _logger.LogInformation("User {UserId} downloaded version {Version} with password {Password}",
                    chatId, latestVersion.Version, password);
            }
            finally
            {
                // Cleanup temp files
                try
                {
                    if (Directory.Exists(tempDir))
                        Directory.Delete(tempDir, true);
                }
                catch (Exception cleanupEx)
                {
                    _logger.LogWarning(cleanupEx, "Failed to cleanup temp directory");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle download request for user {UserId}. Error: {ErrorMessage}", chatId, ex.Message);
            await botClient.SendTextMessageAsync(
                chatId,
                $"❌ Failed to download file: {ex.Message}\n\nPlease contact support.",
                cancellationToken: cancellationToken);
        }
    }

    private static string XorEncrypt(string text, string key)
    {
        var result = new char[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            result[i] = (char)(text[i] ^ key[i % key.Length]);
        }
        return Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(result)).ToLower();
    }

    private static string Base58Encode(string hex)
    {
        const string alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
        var bytes = Convert.FromHexString(hex);
        var value = System.Numerics.BigInteger.Zero;

        foreach (var b in bytes)
        {
            value = value * 256 + b;
        }

        var result = new System.Text.StringBuilder();
        while (value > 0)
        {
            var remainder = (int)(value % 58);
            value /= 58;
            result.Insert(0, alphabet[remainder]);
        }

        // Add leading '1' for leading zero bytes
        foreach (var b in bytes)
        {
            if (b == 0)
                result.Insert(0, '1');
            else
                break;
        }

        return result.Length > 0 ? result.ToString() : "1";
    }

    private static string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }

    private async Task ShowUserKeysForExtensionAsync(ITelegramBotClient botClient, long chatId, int messageId, CancellationToken cancellationToken)
    {
        _logger.LogInformation("ShowUserKeysForExtensionAsync called for user {UserId}", chatId);

        // Get active subscriptions from users table
        var activeSubscriptions = await _repository.GetActiveSubscriptionsAsync(chatId, cancellationToken);

        _logger.LogInformation("Found {Count} active subscriptions for user {UserId}", activeSubscriptions.Count, chatId);

        if (activeSubscriptions.Count == 0)
        {
            await botClient.EditMessageTextAsync(
                chatId,
                messageId,
                """
                *No Active Subscriptions*

                You don't have any active subscriptions to extend.

                *Tip:* You can only extend active subscriptions (with or without HWID).
                Use *Buy New Key* to get a new subscription.
                """,
                ParseMode.Markdown,
                replyMarkup: new InlineKeyboardMarkup(new[]
                {
                    new[] { InlineKeyboardButton.WithCallbackData("Back", "purchase_back") }
                }),
                cancellationToken: cancellationToken);
            return;
        }

        var messageText = new StringBuilder();
        messageText.AppendLine("*Select Subscription to Extend*\n");
        messageText.AppendLine("Choose which subscription you want to add days to:\n");

        var buttons = new List<InlineKeyboardButton[]>();

        for (int i = 0; i < activeSubscriptions.Count; i++)
        {
            var sub = activeSubscriptions[i];
            var daysLeft = Math.Max(0, (sub.SubscriptionExpiry!.Value - DateTime.UtcNow).Days);
            var daysText = $"⏱️ {daysLeft} days left";

            var hwidText = sub.HasHwid ? $"🖥️ HWID: {sub.Hwid![..8]}..." : "🆓 No HWID";
            var statusEmoji = sub.SubscriptionExpiry.Value > DateTime.UtcNow ? "✅" : "⚠️";
            var sourceEmoji = sub.Source == "users" ? "👤" : "🔑";

            buttons.Add(new[]
            {
                InlineKeyboardButton.WithCallbackData(
                    $"{statusEmoji} {sourceEmoji} {hwidText} - {daysText}",
                    $"extend_sub_{sub.Source}_{sub.UserId}_{i}")
            });
        }

        buttons.Add(new[]
        {
            InlineKeyboardButton.WithCallbackData("🔙 Back", "purchase_back")
        });

        var keyboard = new InlineKeyboardMarkup(buttons);

        await botClient.EditMessageTextAsync(
            chatId,
            messageId,
            messageText.ToString(),
            ParseMode.Markdown,
            replyMarkup: keyboard,
            cancellationToken: cancellationToken);
    }

    private async Task HandleExtendKeySelectionAsync(ITelegramBotClient botClient, CallbackQuery query, CancellationToken cancellationToken)
    {
        await botClient.AnswerCallbackQueryAsync(query.Id, cancellationToken: cancellationToken);

        var selectedKey = query.Data!["extend_key_".Length..];

        // Store selected key in session or pass it through callback data
        await botClient.EditMessageTextAsync(
            query.Message!.Chat.Id,
            query.Message.MessageId,
            $"""
            🔄 *Extend Subscription*

            🔑 Selected Key: `{selectedKey[..8]}...{selectedKey[^3..]}`

            Choose how many days to add:
            """,
            ParseMode.Markdown,
            replyMarkup: BuildExtendDurationKeyboard(selectedKey),
            cancellationToken: cancellationToken);
    }

    private async Task HandleExtendSubscriptionSelectionAsync(ITelegramBotClient botClient, CallbackQuery query, CancellationToken cancellationToken)
    {
        await botClient.AnswerCallbackQueryAsync(query.Id, cancellationToken: cancellationToken);

        // Parse: extend_sub_{source}_{userId}_{index}
        var parts = query.Data!.Split('_');
        if (parts.Length < 5)
        {
            await botClient.EditMessageTextAsync(
                query.Message!.Chat.Id,
                query.Message.MessageId,
                "❌ Invalid request.",
                cancellationToken: cancellationToken);
            return;
        }

        var source = parts[2]; // "users" or "subscriptions"
        var userId = parts[3];
        var index = parts[4];

        await botClient.EditMessageTextAsync(
            query.Message!.Chat.Id,
            query.Message.MessageId,
            """
            🔄 *Extend Subscription*

            Choose how many days to add:
            """,
            ParseMode.Markdown,
            replyMarkup: BuildExtendSubscriptionDurationKeyboard(source, userId, index),
            cancellationToken: cancellationToken);
    }

    private static InlineKeyboardMarkup BuildExtendSubscriptionDurationKeyboard(string source, string userId, string index)
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("🔸 +30 Days", $"extend_sub_confirm_{source}_{userId}_{index}_30"),
                InlineKeyboardButton.WithCallbackData("🔶 +90 Days ⭐", $"extend_sub_confirm_{source}_{userId}_{index}_90")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("🔷 +180 Days 💎", $"extend_sub_confirm_{source}_{userId}_{index}_180"),
                InlineKeyboardButton.WithCallbackData("♾️ Lifetime 🚀", $"extend_sub_confirm_{source}_{userId}_{index}_99999")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("🔙 Back", "purchase_mode_extend")
            }
        });
    }

    private static InlineKeyboardMarkup BuildExtendDurationKeyboard(string key)
    {
        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("🔸 +30 Days", $"extend_confirm_{key}_30"),
                InlineKeyboardButton.WithCallbackData("🔶 +90 Days ⭐", $"extend_confirm_{key}_90")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("🔷 +180 Days 💎", $"extend_confirm_{key}_180"),
                InlineKeyboardButton.WithCallbackData("♾️ Lifetime 🚀", $"extend_confirm_{key}_99999")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("🔙 Back", "purchase_mode_extend")
            }
        });
    }

    private async Task HandleExtendDurationSelectionAsync(ITelegramBotClient botClient, CallbackQuery query, CancellationToken cancellationToken)
    {
        // This handles the old extend_duration_ format, redirect to purchase
        await botClient.AnswerCallbackQueryAsync(query.Id, cancellationToken: cancellationToken);

        var payload = query.Data!["extend_duration_".Length..];
        int days;
        string productName;

        if (string.Equals(payload, "lifetime", StringComparison.OrdinalIgnoreCase))
        {
            days = _options.LifetimeDaysThreshold;
            productName = "Lifetime Subscription";
        }
        else if (!int.TryParse(payload, out days))
        {
            await botClient.EditMessageTextAsync(
                query.Message!.Chat.Id,
                query.Message.MessageId,
                "❌ Invalid subscription duration",
                cancellationToken: cancellationToken);
            return;
        }
        else
        {
            productName = $"{days}-Day Subscription";
        }

        await botClient.EditMessageTextAsync(
            query.Message!.Chat.Id,
            query.Message.MessageId,
            "⏳ Processing your purchase...",
            cancellationToken: cancellationToken);

        await ProcessPurchaseAsync(botClient, query.Message.Chat.Id, query.From.Id, days, productName, cancellationToken);
    }

    private async Task HandleExtendConfirmAsync(ITelegramBotClient botClient, CallbackQuery query, CancellationToken cancellationToken)
    {
        await botClient.AnswerCallbackQueryAsync(query.Id, cancellationToken: cancellationToken);

        // Parse: extend_confirm_{key}_{days}
        var parts = query.Data!.Split('_', 4);
        if (parts.Length != 4)
        {
            await botClient.EditMessageTextAsync(
                query.Message!.Chat.Id,
                query.Message.MessageId,
                "❌ Invalid request format.",
                cancellationToken: cancellationToken);
            return;
        }

        var key = parts[2];
        if (!int.TryParse(parts[3], out var days))
        {
            await botClient.EditMessageTextAsync(
                query.Message!.Chat.Id,
                query.Message.MessageId,
                "❌ Invalid duration.",
                cancellationToken: cancellationToken);
            return;
        }

        try
        {
            // Get current key info
            var keys = await _repository.GetActiveKeysAsync(query.Message!.Chat.Id, cancellationToken);
            var currentKey = keys.FirstOrDefault(k => k.SubscriptionKey == key);

            if (currentKey == null)
            {
                await botClient.EditMessageTextAsync(
                    query.Message.Chat.Id,
                    query.Message.MessageId,
                    "❌ Key not found.",
                    cancellationToken: cancellationToken);
                return;
            }

            // Calculate new expiry date
            DateTime newExpiryDate;
            if (currentKey.ExpiresAt.HasValue && currentKey.ExpiresAt.Value > DateTime.UtcNow)
            {
                // Add to existing expiry
                newExpiryDate = currentKey.ExpiresAt.Value.AddDays(days);
            }
            else
            {
                // Start from now
                newExpiryDate = DateTime.UtcNow.AddDays(days);
            }

            var durationText = days >= _options.LifetimeDaysThreshold ? "Lifetime" : $"{days} days";
            var keyPreview = $"{key[..8]}...{key[^3..]}";

            // Show confirmation
            var confirmMessage = $"""
                ✅ *Confirm Extension*

                🔑 Key: `{keyPreview}`
                📦 Add: {durationText}

                📅 Current expiry: {(currentKey.ExpiresAt.HasValue ? currentKey.ExpiresAt.Value.ToString("yyyy-MM-dd HH:mm") : "Not activated")}
                ➕ New expiry: {newExpiryDate:yyyy-MM-dd HH:mm} UTC

                Confirm purchase?
                """;

            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("✅ Confirm", $"extend_final_{key}_{days}"),
                    InlineKeyboardButton.WithCallbackData("❌ Cancel", "purchase_back")
                }
            });

            await botClient.EditMessageTextAsync(
                query.Message.Chat.Id,
                query.Message.MessageId,
                confirmMessage,
                ParseMode.Markdown,
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error showing extension confirmation");
            await botClient.EditMessageTextAsync(
                query.Message!.Chat.Id,
                query.Message.MessageId,
                $"❌ Error: {ex.Message}",
                cancellationToken: cancellationToken);
        }
    }

    private async Task HandleExtendFinalAsync(ITelegramBotClient botClient, CallbackQuery query, CancellationToken cancellationToken)
    {
        await botClient.AnswerCallbackQueryAsync(query.Id, "⏳ Processing...", cancellationToken: cancellationToken);

        // Parse: extend_final_{key}_{days}
        var parts = query.Data!.Split('_', 4);
        if (parts.Length != 4)
        {
            await botClient.EditMessageTextAsync(
                query.Message!.Chat.Id,
                query.Message.MessageId,
                "❌ Invalid request format.",
                cancellationToken: cancellationToken);
            return;
        }

        var key = parts[2];
        if (!int.TryParse(parts[3], out var days))
        {
            await botClient.EditMessageTextAsync(
                query.Message!.Chat.Id,
                query.Message.MessageId,
                "❌ Invalid duration.",
                cancellationToken: cancellationToken);
            return;
        }

        try
        {
            // Extend the key
            var success = await _repository.ExtendSubscriptionKeyAsync(key, days, cancellationToken);

            if (!success)
            {
                await botClient.EditMessageTextAsync(
                    query.Message!.Chat.Id,
                    query.Message.MessageId,
                    "❌ Failed to extend subscription. Key may not exist.",
                    cancellationToken: cancellationToken);
                return;
            }

            var durationText = days >= _options.LifetimeDaysThreshold ? "Lifetime" : $"{days} days";
            var keyPreview = $"{key[..8]}...{key[^3..]}";

            await botClient.EditMessageTextAsync(
                query.Message!.Chat.Id,
                query.Message.MessageId,
                $"""
                🎉 *Subscription Extended!*

                🔑 Key: `{keyPreview}`
                ➕ Added: {durationText}

                ✅ Your subscription has been successfully extended!

                Use /profile to view your updated subscription details.
                """,
                ParseMode.Markdown,
                cancellationToken: cancellationToken);

            _logger.LogInformation("User {UserId} extended key {Key} by {Days} days", query.Message.Chat.Id, key, days);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extending subscription");
            await botClient.EditMessageTextAsync(
                query.Message!.Chat.Id,
                query.Message.MessageId,
                $"❌ Error: {ex.Message}",
                cancellationToken: cancellationToken);
        }
    }

    private async Task HandleExtendSubscriptionConfirmAsync(ITelegramBotClient botClient, CallbackQuery query, CancellationToken cancellationToken)
    {
        await botClient.AnswerCallbackQueryAsync(query.Id, cancellationToken: cancellationToken);

        // Parse: extend_sub_confirm_{source}_{userId}_{index}_{days}
        var parts = query.Data!.Split('_');
        if (parts.Length < 6)
        {
            await botClient.EditMessageTextAsync(
                query.Message!.Chat.Id,
                query.Message.MessageId,
                "❌ Invalid request format.",
                cancellationToken: cancellationToken);
            return;
        }

        var source = parts[3]; // "users" or "subscriptions"
        if (!long.TryParse(parts[4], out var userId) || !int.TryParse(parts[5], out var index) || !int.TryParse(parts[6], out var days))
        {
            await botClient.EditMessageTextAsync(
                query.Message!.Chat.Id,
                query.Message.MessageId,
                "❌ Invalid parameters.",
                cancellationToken: cancellationToken);
            return;
        }

        try
        {
            // Get current subscription info
            var subscriptions = await _repository.GetActiveSubscriptionsAsync(userId, cancellationToken);
            if (index >= subscriptions.Count)
            {
                await botClient.EditMessageTextAsync(
                    query.Message.Chat.Id,
                    query.Message.MessageId,
                    "❌ Subscription not found.",
                    cancellationToken: cancellationToken);
                return;
            }

            var currentSub = subscriptions[index];

            // Calculate new expiry date
            DateTime newExpiryDate;
            if (currentSub.SubscriptionExpiry!.Value > DateTime.UtcNow)
            {
                newExpiryDate = currentSub.SubscriptionExpiry.Value.AddDays(days);
            }
            else
            {
                newExpiryDate = DateTime.UtcNow.AddDays(days);
            }

            var durationText = days >= _options.LifetimeDaysThreshold ? "Lifetime" : $"{days} days";
            var hwidText = currentSub.HasHwid ? $"🖥️ HWID: {currentSub.Hwid![..8]}..." : "🆓 No HWID";
            var sourceEmoji = currentSub.Source == "users" ? "👤" : "🔑";

            // Show confirmation
            var confirmMessage = $"""
                ✅ *Confirm Extension*

                {sourceEmoji} {hwidText}
                📦 Add: {durationText}

                📅 Current expiry: {currentSub.SubscriptionExpiry.Value:yyyy-MM-dd HH:mm}
                ➕ New expiry: {newExpiryDate:yyyy-MM-dd HH:mm} UTC

                Confirm purchase?
                """;

            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("✅ Confirm", $"extend_sub_final_{source}_{userId}_{index}_{days}"),
                    InlineKeyboardButton.WithCallbackData("❌ Cancel", "purchase_back")
                }
            });

            await botClient.EditMessageTextAsync(
                query.Message.Chat.Id,
                query.Message.MessageId,
                confirmMessage,
                ParseMode.Markdown,
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error showing subscription extension confirmation");
            await botClient.EditMessageTextAsync(
                query.Message!.Chat.Id,
                query.Message.MessageId,
                $"❌ Error: {ex.Message}",
                cancellationToken: cancellationToken);
        }
    }

    private async Task HandleExtendSubscriptionFinalAsync(ITelegramBotClient botClient, CallbackQuery query, CancellationToken cancellationToken)
    {
        await botClient.AnswerCallbackQueryAsync(query.Id, "⏳ Processing...", cancellationToken: cancellationToken);

        // Parse: extend_sub_final_{source}_{userId}_{index}_{days}
        var parts = query.Data!.Split('_');
        if (parts.Length < 7)
        {
            await botClient.EditMessageTextAsync(
                query.Message!.Chat.Id,
                query.Message.MessageId,
                "❌ Invalid request format.",
                cancellationToken: cancellationToken);
            return;
        }

        var source = parts[3]; // "users" or "subscriptions"
        if (!long.TryParse(parts[4], out var userId) || !int.TryParse(parts[5], out var index) || !int.TryParse(parts[6], out var days))
        {
            await botClient.EditMessageTextAsync(
                query.Message!.Chat.Id,
                query.Message.MessageId,
                "❌ Invalid parameters.",
                cancellationToken: cancellationToken);
            return;
        }

        try
        {
            bool success;
            if (source == "users")
            {
                // Extend subscription in users table
                success = await _repository.ExtendSubscriptionByUserIdAsync(userId, days, cancellationToken);
            }
            else
            {
                // Extend key in subscriptions table
                success = await _repository.ExtendSubscriptionKeyByUserIdAsync(userId, days, cancellationToken);
            }

            if (!success)
            {
                await botClient.EditMessageTextAsync(
                    query.Message!.Chat.Id,
                    query.Message.MessageId,
                    "❌ Failed to extend subscription.",
                    cancellationToken: cancellationToken);
                return;
            }

            var durationText = days >= _options.LifetimeDaysThreshold ? "Lifetime" : $"{days} days";

            await botClient.EditMessageTextAsync(
                query.Message!.Chat.Id,
                query.Message.MessageId,
                $"""
                🎉 *Subscription Extended!*

                ➕ Added: {durationText}

                ✅ Your subscription has been successfully extended!

                Use /profile to view your updated subscription details.
                """,
                ParseMode.Markdown,
                cancellationToken: cancellationToken);

            _logger.LogInformation("User {UserId} extended {Source} subscription by {Days} days", userId, source, days);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extending subscription");
            await botClient.EditMessageTextAsync(
                query.Message!.Chat.Id,
                query.Message.MessageId,
                $"❌ Error: {ex.Message}",
                cancellationToken: cancellationToken);
        }
    }

    private async Task SendSupportInfoAsync(ITelegramBotClient botClient, long chatId, CancellationToken cancellationToken)
    {
        var message = $"""
            💬 *Support Information*

            If you have any questions or issues, please contact our support team:

            📞 Contact: {_commonOptions.SupportContact}

            We're here to help! 🤝
            """;

        await botClient.SendTextMessageAsync(
            chatId,
            message,
            ParseMode.Markdown,
            cancellationToken: cancellationToken);
    }

    private async Task HandlePaymentConfirmAsync(ITelegramBotClient botClient, CallbackQuery query, CancellationToken cancellationToken)
    {
        await botClient.AnswerCallbackQueryAsync(query.Id, "⏳ Sending to admin...", cancellationToken: cancellationToken);

        // Parse: payment_confirm_{requestId}
        var requestIdStr = query.Data!["payment_confirm_".Length..];
        if (!int.TryParse(requestIdStr, out var requestId))
        {
            await botClient.EditMessageTextAsync(
                query.Message!.Chat.Id,
                query.Message.MessageId,
                "❌ Invalid request.",
                cancellationToken: cancellationToken);
            return;
        }

        try
        {
            var paymentRequest = await _repository.GetPaymentRequestAsync(requestId, cancellationToken);
            if (paymentRequest == null)
            {
                await botClient.EditMessageTextAsync(
                    query.Message!.Chat.Id,
                    query.Message.MessageId,
                    "❌ Payment request not found.",
                    cancellationToken: cancellationToken);
                return;
            }

            // Update user message
            await botClient.EditMessageTextAsync(
                query.Message!.Chat.Id,
                query.Message.MessageId,
                """
                ✅ *Payment confirmation sent!*

                Your payment confirmation has been sent to our admin team.
                You will receive your subscription key once the payment is verified.

                ⏳ Please wait for admin approval...
                """,
                ParseMode.Markdown,
                cancellationToken: cancellationToken);

            // Send to admin
            await NotifyAdminAboutPaymentAsync(paymentRequest, cancellationToken);

            _logger.LogInformation("User {UserId} confirmed payment for request {RequestId}", query.From.Id, requestId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling payment confirmation");
            await botClient.EditMessageTextAsync(
                query.Message!.Chat.Id,
                query.Message.MessageId,
                $"❌ Error: {ex.Message}",
                cancellationToken: cancellationToken);
        }
    }

    private async Task NotifyAdminAboutPaymentAsync(PaymentRequest request, CancellationToken cancellationToken)
    {
        var durationText = request.Days >= _options.LifetimeDaysThreshold ? "Lifetime" : $"{request.Days} days";

        var message = $"""
            💰 *New Payment Confirmation*

            👤 **User ID:** `{request.UserId}`
            📦 **Product:** {request.ProductName}
            📅 **Duration:** {durationText}
            💵 **Amount:** ${request.Amount:F2}
            🆔 **Request ID:** `{request.Id}`

            ⏰ **Time:** {request.CreatedAt:yyyy-MM-dd HH:mm} UTC

            Please verify the payment and approve or reject:
            """;

        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("✅ Approve", $"payment_approve_{request.Id}"),
                InlineKeyboardButton.WithCallbackData("❌ Reject", $"payment_reject_{request.Id}")
            }
        });

        // Send to admin group if configured
        if (_commonOptions.AdminGroupId.HasValue)
        {
            try
            {
                await _adminBotClient.SendTextMessageAsync(
                    _commonOptions.AdminGroupId.Value,
                    message,
                    ParseMode.Markdown,
                    replyMarkup: keyboard,
                    cancellationToken: cancellationToken);

                _logger.LogInformation("Sent payment notification to admin group {GroupId}", _commonOptions.AdminGroupId.Value);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to notify admin group {GroupId} about payment request {RequestId}",
                    _commonOptions.AdminGroupId.Value, request.Id);
            }
        }

        // Send to individual admins
        foreach (var adminId in _commonOptions.AdminIds)
        {
            try
            {
                await _adminBotClient.SendTextMessageAsync(
                    adminId,
                    message,
                    ParseMode.Markdown,
                    replyMarkup: keyboard,
                    cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to notify admin {AdminId} about payment request {RequestId}", adminId, request.Id);
            }
        }
    }

    private async Task HandleManageKeyAsync(ITelegramBotClient botClient, CallbackQuery query, CancellationToken cancellationToken)
    {
        await botClient.AnswerCallbackQueryAsync(query.Id, cancellationToken: cancellationToken);

        // Parse: manage_key_{index}
        var indexStr = query.Data!["manage_key_".Length..];
        if (!int.TryParse(indexStr, out var index))
        {
            await botClient.EditMessageTextAsync(
                query.Message!.Chat.Id,
                query.Message.MessageId,
                "❌ Invalid key selection.",
                cancellationToken: cancellationToken);
            return;
        }

        try
        {
            var keys = await _repository.GetActiveKeysAsync(query.From.Id, cancellationToken);
            if (index >= keys.Count)
            {
                await botClient.EditMessageTextAsync(
                    query.Message!.Chat.Id,
                    query.Message.MessageId,
                    "❌ Key not found.",
                    cancellationToken: cancellationToken);
                return;
            }

            var key = keys[index];
            var now = DateTime.UtcNow;
            string validityText;

            if (key.ExpiresAt is null)
            {
                validityText = "Lifetime";
            }
            else if (key.ExpiresAt > now)
            {
                var remaining = key.ExpiresAt.Value - now;
                var remainingDays = (int)Math.Ceiling(remaining.TotalDays);
                validityText = $"{remainingDays} day(s) left (expires {key.ExpiresAt:yyyy-MM-dd HH:mm} UTC)";
            }
            else
            {
                validityText = $"Expired on {key.ExpiresAt:yyyy-MM-dd HH:mm} UTC";
            }

            var message = $"""
                🔑 *Key Management*

                **Key:** `{key.SubscriptionKey}`
                **Duration:** {key.Days} day(s)
                **Status:** {validityText}

                What would you like to do with this key?
                """;

            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("📤 Transfer to User", $"transfer_key_{index}")
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("✅ Use for Subscription", $"use_key_select_{index}")
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("🔙 Back", "keys_back")
                }
            });

            await botClient.EditMessageTextAsync(
                query.Message!.Chat.Id,
                query.Message.MessageId,
                message,
                ParseMode.Markdown,
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error managing key");
            await botClient.EditMessageTextAsync(
                query.Message!.Chat.Id,
                query.Message.MessageId,
                $"❌ Error: {ex.Message}",
                cancellationToken: cancellationToken);
        }
    }

    private async Task HandleTransferKeyRequestAsync(ITelegramBotClient botClient, CallbackQuery query, CancellationToken cancellationToken)
    {
        await botClient.AnswerCallbackQueryAsync(query.Id, cancellationToken: cancellationToken);

        // Parse: transfer_key_{index}
        var indexStr = query.Data!["transfer_key_".Length..];
        if (!int.TryParse(indexStr, out var index))
        {
            await botClient.EditMessageTextAsync(
                query.Message!.Chat.Id,
                query.Message.MessageId,
                "❌ Invalid key selection.",
                cancellationToken: cancellationToken);
            return;
        }

        // Store the key index in session for the next message
        _sessionStore.SetAwaitingInput(query.From.Id, $"transfer_key_{index}");

        await botClient.EditMessageTextAsync(
            query.Message!.Chat.Id,
            query.Message.MessageId,
            """
            📤 *Transfer Key*

            Please send the User ID of the recipient.

            Example: `123456789`

            Send /cancel to cancel the transfer.
            """,
            ParseMode.Markdown,
            cancellationToken: cancellationToken);
    }

    private async Task HandleUseKeySelectAsync(ITelegramBotClient botClient, CallbackQuery query, CancellationToken cancellationToken)
    {
        await botClient.AnswerCallbackQueryAsync(query.Id, cancellationToken: cancellationToken);

        // Parse: use_key_select_{index}
        var indexStr = query.Data!["use_key_select_".Length..];
        if (!int.TryParse(indexStr, out var index))
        {
            await botClient.EditMessageTextAsync(
                query.Message!.Chat.Id,
                query.Message.MessageId,
                "❌ Invalid key selection.",
                cancellationToken: cancellationToken);
            return;
        }

        try
        {
            // Get user's active subscriptions
            var subscriptions = await _repository.GetActiveSubscriptionsAsync(query.From.Id, cancellationToken);

            if (subscriptions.Count == 0)
            {
                // No existing subscriptions, create new one
                await botClient.EditMessageTextAsync(
                    query.Message!.Chat.Id,
                    query.Message.MessageId,
                    """
                    📋 *Select Subscription Type*

                    You don't have any active subscriptions yet.
                    This key will create a new subscription for you.
                    """,
                    ParseMode.Markdown,
                    replyMarkup: new InlineKeyboardMarkup(new[]
                    {
                        new[]
                        {
                            InlineKeyboardButton.WithCallbackData("✅ Create New Subscription", $"use_key_new_{index}")
                        },
                        new[]
                        {
                            InlineKeyboardButton.WithCallbackData("🔙 Back", $"manage_key_{index}")
                        }
                    }),
                    cancellationToken: cancellationToken);
                return;
            }

            // Show list of subscriptions to extend
            var buttons = new List<InlineKeyboardButton[]>();
            int subIndex = 0;

            foreach (var sub in subscriptions)
            {
                var sourceEmoji = sub.Source == "users" ? "👤" : "🔑";
                var hwidText = sub.HasHwid ? $"HWID: {sub.Hwid![..8]}..." : "No HWID";
                var expiryText = sub.SubscriptionExpiry.HasValue
                    ? $"Expires: {sub.SubscriptionExpiry.Value:yyyy-MM-dd}"
                    : "Lifetime";

                buttons.Add(new[]
                {
                    InlineKeyboardButton.WithCallbackData(
                        $"{sourceEmoji} {hwidText} - {expiryText}",
                        $"use_key_{sub.Source}_{subIndex}_{index}")
                });
                subIndex++;
            }

            buttons.Add(new[]
            {
                InlineKeyboardButton.WithCallbackData("🔙 Back", $"manage_key_{index}")
            });

            await botClient.EditMessageTextAsync(
                query.Message!.Chat.Id,
                query.Message.MessageId,
                """
                📋 *Select Subscription to Extend*

                Choose which subscription you want to extend with this key:
                """,
                ParseMode.Markdown,
                replyMarkup: new InlineKeyboardMarkup(buttons),
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error selecting subscription for key use");
            await botClient.EditMessageTextAsync(
                query.Message!.Chat.Id,
                query.Message.MessageId,
                $"❌ Error: {ex.Message}",
                cancellationToken: cancellationToken);
        }
    }

    private async Task HandleUseKeyAsync(ITelegramBotClient botClient, CallbackQuery query, CancellationToken cancellationToken)
    {
        await botClient.AnswerCallbackQueryAsync(query.Id, "⏳ Processing...", cancellationToken: cancellationToken);

        // Parse: use_key_{source}_{subIndex}_{keyIndex} or use_key_new_{keyIndex}
        var parts = query.Data!.Split('_');
        if (parts.Length < 3)
        {
            await botClient.EditMessageTextAsync(
                query.Message!.Chat.Id,
                query.Message.MessageId,
                "❌ Invalid request format.",
                cancellationToken: cancellationToken);
            return;
        }

        try
        {
            int keyIndex;
            bool isNewSubscription = parts[2] == "new";

            if (isNewSubscription)
            {
                // use_key_new_{keyIndex}
                if (!int.TryParse(parts[3], out keyIndex))
                {
                    await botClient.EditMessageTextAsync(
                        query.Message!.Chat.Id,
                        query.Message.MessageId,
                        "❌ Invalid key index.",
                        cancellationToken: cancellationToken);
                    return;
                }
            }
            else
            {
                // use_key_{source}_{subIndex}_{keyIndex}
                if (parts.Length != 5 || !int.TryParse(parts[4], out keyIndex))
                {
                    await botClient.EditMessageTextAsync(
                        query.Message!.Chat.Id,
                        query.Message.MessageId,
                        "❌ Invalid request format.",
                        cancellationToken: cancellationToken);
                    return;
                }
            }

            var keys = await _repository.GetActiveKeysAsync(query.From.Id, cancellationToken);
            if (keyIndex >= keys.Count)
            {
                await botClient.EditMessageTextAsync(
                    query.Message!.Chat.Id,
                    query.Message.MessageId,
                    "❌ Key not found.",
                    cancellationToken: cancellationToken);
                return;
            }

            var key = keys[keyIndex];

            bool success;
            if (isNewSubscription)
            {
                // Create new subscription
                success = await _repository.AddDaysByUserIdAsync(query.From.Id, key.Days, cancellationToken);
            }
            else
            {
                // Extend existing subscription
                var source = parts[2];
                if (source == "users")
                {
                    success = await _repository.ExtendSubscriptionByUserIdAsync(query.From.Id, key.Days, cancellationToken);
                }
                else
                {
                    success = await _repository.ExtendSubscriptionKeyByUserIdAsync(query.From.Id, key.Days, cancellationToken);
                }
            }

            if (!success)
            {
                await botClient.EditMessageTextAsync(
                    query.Message!.Chat.Id,
                    query.Message.MessageId,
                    "❌ Failed to apply key to your subscription.",
                    cancellationToken: cancellationToken);
                return;
            }

            // Delete the key after use
            await _repository.DeleteKeyAsync(key.SubscriptionKey, cancellationToken);

            var durationText = key.Days >= _options.LifetimeDaysThreshold ? "Lifetime" : $"{key.Days} days";

            await botClient.EditMessageTextAsync(
                query.Message!.Chat.Id,
                query.Message.MessageId,
                $"""
                ✅ *Key Applied Successfully!*

                🔑 Key: `{key.SubscriptionKey[..8]}...{key.SubscriptionKey[^3..]}`
                ➕ Added: {durationText}

                Your subscription has been extended!
                Use /profile to view your updated subscription.
                """,
                ParseMode.Markdown,
                cancellationToken: cancellationToken);

            _logger.LogInformation("User {UserId} used key {Key} for their subscription", query.From.Id, key.SubscriptionKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error using key");
            await botClient.EditMessageTextAsync(
                query.Message!.Chat.Id,
                query.Message.MessageId,
                $"❌ Error: {ex.Message}",
                cancellationToken: cancellationToken);
        }
    }

    private async Task HandleTransferKeyInputAsync(ITelegramBotClient botClient, Message message, string awaitingInput, CancellationToken cancellationToken)
    {
        // Clear awaiting input
        _sessionStore.ClearAwaitingInput(message.From!.Id);

        // Parse: transfer_key_{index}
        var indexStr = awaitingInput["transfer_key_".Length..];
        if (!int.TryParse(indexStr, out var index))
        {
            await botClient.SendTextMessageAsync(
                message.Chat.Id,
                "❌ Invalid key selection.",
                cancellationToken: cancellationToken);
            return;
        }

        // Parse recipient user ID
        if (!long.TryParse(message.Text!.Trim(), out var recipientId))
        {
            await botClient.SendTextMessageAsync(
                message.Chat.Id,
                "❌ Invalid User ID. Please provide a valid numeric User ID.",
                cancellationToken: cancellationToken);
            return;
        }

        try
        {
            var keys = await _repository.GetActiveKeysAsync(message.From.Id, cancellationToken);
            if (index >= keys.Count)
            {
                await botClient.SendTextMessageAsync(
                    message.Chat.Id,
                    "❌ Key not found.",
                    cancellationToken: cancellationToken);
                return;
            }

            var key = keys[index];

            // Transfer key to recipient
            var success = await _repository.TransferKeyAsync(key.SubscriptionKey, message.From.Id, recipientId, cancellationToken);
            if (!success)
            {
                await botClient.SendTextMessageAsync(
                    message.Chat.Id,
                    "❌ Failed to transfer key. Please try again.",
                    cancellationToken: cancellationToken);
                return;
            }

            var durationText = key.Days >= _options.LifetimeDaysThreshold ? "Lifetime" : $"{key.Days} days";

            // Notify sender
            await botClient.SendTextMessageAsync(
                message.Chat.Id,
                $"""
                ✅ *Key Transferred Successfully!*

                🔑 Key: `{key.SubscriptionKey[..8]}...{key.SubscriptionKey[^3..]}`
                📤 Transferred to: `{recipientId}`
                📅 Duration: {durationText}

                The recipient has been notified.
                """,
                ParseMode.Markdown,
                cancellationToken: cancellationToken);

            // Notify recipient
            try
            {
                await botClient.SendTextMessageAsync(
                    recipientId,
                    $"""
                    🎁 *You received a subscription key!*

                    👤 From User: `{message.From.Id}`
                    🔑 Key: `{key.SubscriptionKey}`
                    📅 Duration: {durationText}

                    You can now use this key to activate or extend your subscription!
                    Use /mykeys to view your keys.
                    """,
                    ParseMode.Markdown,
                    cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to notify recipient {RecipientId} about key transfer", recipientId);
            }

            _logger.LogInformation("User {SenderId} transferred key {Key} to user {RecipientId}", message.From.Id, key.SubscriptionKey, recipientId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error transferring key");
            await botClient.SendTextMessageAsync(
                message.Chat.Id,
                $"❌ Error: {ex.Message}",
                cancellationToken: cancellationToken);
        }
    }
}
