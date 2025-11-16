using System.Text;
using HwidBots.AdminBot.Options;
using HwidBots.Shared.Models;
using HwidBots.Shared.Options;
using HwidBots.Shared.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace HwidBots.AdminBot.Services;

public class AdminBotUpdateHandler
{
    private readonly ILicenseRepository _repository;
    private readonly BotCommonOptions _commonOptions;
    private readonly AdminSessionStore _sessionStore;
    private readonly ILogger<AdminBotUpdateHandler> _logger;
    private readonly ITelegramBotClient _userBotClient;

    private const string MainMenuText = "Choose an action:";

    public AdminBotUpdateHandler(
        ILicenseRepository repository,
        IOptions<BotCommonOptions> commonOptions,
        AdminSessionStore sessionStore,
        ILogger<AdminBotUpdateHandler> logger,
        [FromKeyedServices("UserBot")] ITelegramBotClient userBotClient)
    {
        _repository = repository;
        _commonOptions = commonOptions.Value;
        _sessionStore = sessionStore;
        _logger = logger;
        _userBotClient = userBotClient;
    }

    public async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        try
        {
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
            _logger.LogError(ex, "Failed to process admin update {@Update}", update);
        }
    }

    public Task HandlePollingErrorAsync(ITelegramBotClient _, Exception exception, CancellationToken __)
    {
        var message = exception switch
        {
            ApiRequestException apiEx => $"Telegram API Error [{apiEx.ErrorCode}]: {apiEx.Message}",
            _ => exception.Message
        };

        _logger.LogError(exception, "Admin polling error: {Message}", message);
        return Task.CompletedTask;
    }

    private async Task HandleMessageAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        // Check if user is admin
        var userId = message.From?.Id ?? 0;
        if (!IsAdmin(userId))
        {
            return; // Silently ignore non-admin messages
        }

        // Handle document upload
        if (message.Document is not null)
        {
            _logger.LogInformation("AdminBot received document from chat {ChatId}, filename: {FileName}",
                message.Chat.Id, message.Document.FileName);
            await HandleDocumentUploadAsync(botClient, message, cancellationToken);
            return;
        }

        if (string.IsNullOrWhiteSpace(message.Text))
        {
            return;
        }

        var text = message.Text.Trim();
        var chatId = message.Chat.Id;

        // Check if admin is awaiting input for a pending action
        var isAwaitingInput = _sessionStore.TryGetPendingAction(chatId, out _);

        // In groups, only respond to commands (starting with /) OR if awaiting input
        if (message.Chat.Type != Telegram.Bot.Types.Enums.ChatType.Private)
        {
            if (!isAwaitingInput && (string.IsNullOrEmpty(text) || !text.StartsWith("/")))
            {
                return; // Ignore non-command messages in groups (unless awaiting input)
            }
        }

        if (text.StartsWith("/"))
        {
            await HandleCommandAsync(botClient, message, text, cancellationToken);
            return;
        }

        await HandlePendingActionAsync(botClient, message, text, cancellationToken);
    }

    private async Task HandleCommandAsync(ITelegramBotClient botClient, Message message, string commandText, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        var parts = commandText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var command = parts[0];
        var args = parts.Skip(1).ToArray();

        switch (command)
        {
            case "/start":
                _sessionStore.ClearPendingAction(chatId);
                await botClient.SendTextMessageAsync(
                    chatId,
                    "🛠 *Admin Control Bot*\n\nUse /admin to open the control panel.",
                    ParseMode.Markdown,
                    cancellationToken: cancellationToken);
                break;

            case "/admin":
                _sessionStore.ClearPendingAction(chatId);
                await botClient.SendTextMessageAsync(
                    chatId,
                    """
                    🛠 *Admin Control Panel*

                    Use the buttons below to manage the system.
                    For actions that require parameters, the bot will ask you to enter them.
                    """,
                    ParseMode.Markdown,
                    replyMarkup: BuildMainMenu(),
                    cancellationToken: cancellationToken);
                break;

            case "/stats":
                await SendStatsAsync(botClient, chatId, cancellationToken);
                break;

            case "/users":
                await SendUsersListAsync(botClient, chatId, cancellationToken);
                break;

            case "/user":
                if (args.Length == 0)
                {
                    await botClient.SendTextMessageAsync(chatId, "Usage: /user <user_id> or /user hwid <hwid>", cancellationToken: cancellationToken);
                    return;
                }
                await HandleUserLookupAsync(botClient, chatId, args, cancellationToken);
                break;

            case "/ban":
                if (args.Length == 0)
                {
                    await botClient.SendTextMessageAsync(chatId, "Usage: /ban <user_id> or /ban hwid <hwid>", cancellationToken: cancellationToken);
                    return;
                }
                await HandleBanAsync(botClient, chatId, args, cancellationToken);
                break;

            case "/unban":
                if (args.Length == 0)
                {
                    await botClient.SendTextMessageAsync(chatId, "Usage: /unban <user_id> or /unban hwid <hwid>", cancellationToken: cancellationToken);
                    return;
                }
                await HandleUnbanAsync(botClient, chatId, args, cancellationToken);
                break;

            case "/adddays":
                if (args.Length < 2)
                {
                    await botClient.SendTextMessageAsync(chatId, "Usage: /adddays <user_id> <days> or /adddays hwid <hwid> <days>", cancellationToken: cancellationToken);
                    return;
                }
                await HandleAddDaysAsync(botClient, chatId, args, cancellationToken);
                break;

            case "/deletehwid":
                if (args.Length < 1)
                {
                    await botClient.SendTextMessageAsync(chatId, "Usage: /deletehwid <hwid>", cancellationToken: cancellationToken);
                    return;
                }
                await HandleDeleteHwidAsync(botClient, chatId, args[0], cancellationToken);
                break;

            case "/resetrequests":
            case "/resets":
                await HandlePendingResetsAsync(botClient, chatId, cancellationToken);
                break;

            case "/keys":
                if (args.Length == 0)
                {
                    await botClient.SendTextMessageAsync(chatId, "Usage: /keys <user_id>", cancellationToken: cancellationToken);
                    return;
                }
                if (!long.TryParse(args[0], out var keysUserId))
                {
                    await botClient.SendTextMessageAsync(chatId, "Invalid user ID.", cancellationToken: cancellationToken);
                    return;
                }
                await HandleViewUserKeysAsync(botClient, chatId, keysUserId, cancellationToken);
                break;

            case "/deletekey":
                if (args.Length < 1)
                {
                    await botClient.SendTextMessageAsync(chatId, "Usage: /deletekey <key>", cancellationToken: cancellationToken);
                    return;
                }
                await HandleDeleteKeyAsync(botClient, chatId, args[0], cancellationToken);
                break;

            case "/search":
                if (args.Length < 1)
                {
                    await botClient.SendTextMessageAsync(chatId, "Usage: /search <hwid_pattern>", cancellationToken: cancellationToken);
                    return;
                }
                await HandleSearchHwidAsync(botClient, chatId, args[0], cancellationToken);
                break;

            case "/expired":
                await HandleExpiredLicensesAsync(botClient, chatId, cancellationToken);
                break;

            case "/banned":
                await HandleBannedLicensesAsync(botClient, chatId, cancellationToken);
                break;

            case "/logs":
                await HandleViewLogsAsync(botClient, chatId, args, cancellationToken);
                break;

            case "/allusers":
                await HandleAllUsersAsync(botClient, chatId, cancellationToken);
                break;

            case "/allkeys":
                await HandleAllKeysAsync(botClient, chatId, cancellationToken);
                break;

            case "/adddaysall":
                if (args.Length < 1)
                {
                    await botClient.SendTextMessageAsync(chatId, "Usage: /adddaysall <days>", cancellationToken: cancellationToken);
                    return;
                }
                if (!int.TryParse(args[0], out var daysToAdd))
                {
                    await botClient.SendTextMessageAsync(chatId, "Invalid number of days.", cancellationToken: cancellationToken);
                    return;
                }
                await HandleAddDaysToAllAsync(botClient, chatId, daysToAdd, cancellationToken);
                break;

            case "/upload":
                if (args.Length > 0)
                {
                    // Version provided in command
                    var version = args[0];
                    _sessionStore.SetPendingAction(chatId, $"upload_version:{version}");
                    await botClient.SendTextMessageAsync(
                        chatId,
                        "Now send the update log (changelog) for this version, or type 'skip' to skip:",
                        cancellationToken: cancellationToken);
                }
                else
                {
                    await HandleUploadRequestAsync(botClient, chatId, cancellationToken);
                }
                break;

            case "/versions":
                await HandleVersionHistoryAsync(botClient, chatId, cancellationToken);
                break;

            default:
                await botClient.SendTextMessageAsync(chatId, "⚠️ Unknown command. Use /start to see the menu.", cancellationToken: cancellationToken);
                break;
        }
    }

    private async Task HandlePendingActionAsync(ITelegramBotClient botClient, Message message, string text, CancellationToken cancellationToken)
    {
        if (!_sessionStore.TryGetPendingAction(message.Chat.Id, out var action) || string.IsNullOrEmpty(action))
        {
            // Ignore text messages when no pending action
            return;
        }

        if (text.Equals("cancel", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            _sessionStore.ClearPendingAction(message.Chat.Id);
            await botClient.SendTextMessageAsync(
                message.Chat.Id,
                "❌ Action cancelled.",
                cancellationToken: cancellationToken);
            return;
        }

        try
        {
            switch (action)
            {
                case "user":
                    await HandleUserLookupAsync(botClient, message.Chat.Id, ParseActionArgs(text, action), cancellationToken);
                    break;
                case "ban":
                    await HandleBanAsync(botClient, message.Chat.Id, ParseActionArgs(text, action), cancellationToken);
                    break;
                case "unban":
                    await HandleUnbanAsync(botClient, message.Chat.Id, ParseActionArgs(text, action), cancellationToken);
                    break;
                case "adddays":
                    await HandleAddDaysAsync(botClient, message.Chat.Id, ParseActionArgs(text, action), cancellationToken);
                    break;
                case "deletehwid":
                    var args = ParseActionArgs(text, action);
                    await HandleDeleteHwidAsync(botClient, message.Chat.Id, args[0], cancellationToken);
                    break;
                case "keys":
                    var keysArgs = ParseActionArgs(text, action);
                    if (keysArgs.Length == 0 || !long.TryParse(keysArgs[0], out var keysUserId))
                    {
                        await botClient.SendTextMessageAsync(message.Chat.Id, "Please provide a valid user ID.", cancellationToken: cancellationToken);
                        return;
                    }
                    await HandleViewUserKeysAsync(botClient, message.Chat.Id, keysUserId, cancellationToken);
                    break;
                case "search":
                    var searchArgs = ParseActionArgs(text, action);
                    if (searchArgs.Length == 0)
                    {
                        await botClient.SendTextMessageAsync(message.Chat.Id, "Please provide a HWID pattern to search.", cancellationToken: cancellationToken);
                        return;
                    }
                    await HandleSearchHwidAsync(botClient, message.Chat.Id, searchArgs[0], cancellationToken);
                    break;
                case "adddaysall":
                    if (!int.TryParse(text.Trim(), out var daysToAdd) || daysToAdd <= 0)
                    {
                        await botClient.SendTextMessageAsync(message.Chat.Id, "Please provide a valid positive number of days.", cancellationToken: cancellationToken);
                        return;
                    }
                    await HandleAddDaysToAllAsync(botClient, message.Chat.Id, daysToAdd, cancellationToken);
                    break;
                case "deletekey":
                    var keyToDelete = text.Trim().ToUpper();
                    if (string.IsNullOrEmpty(keyToDelete) || !keyToDelete.StartsWith("KEY"))
                    {
                        await botClient.SendTextMessageAsync(message.Chat.Id, "Please provide a valid subscription key (e.g., KEY123ABC456DEF789).", cancellationToken: cancellationToken);
                        return;
                    }
                    await HandleDeleteKeyAsync(botClient, message.Chat.Id, keyToDelete, cancellationToken);
                    break;
                case "deleteallkeys":
                    if (!long.TryParse(text.Trim(), out var deleteKeysUserId) || deleteKeysUserId <= 0)
                    {
                        await botClient.SendTextMessageAsync(message.Chat.Id, "Please provide a valid user ID.", cancellationToken: cancellationToken);
                        return;
                    }
                    await HandleDeleteAllKeysAsync(botClient, message.Chat.Id, deleteKeysUserId, cancellationToken);
                    break;
                case "createkey":
                    var createKeyArgs = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (createKeyArgs.Length != 2 || !long.TryParse(createKeyArgs[0], out var targetUserId) || !int.TryParse(createKeyArgs[1], out var keyDays) || keyDays <= 0)
                    {
                        await botClient.SendTextMessageAsync(message.Chat.Id, "Please provide valid format: `<user_id> <days>`\n\nExample: `8400449655 30`", ParseMode.Markdown, cancellationToken: cancellationToken);
                        return;
                    }
                    await HandleCreateKeyAsync(botClient, message.Chat.Id, targetUserId, keyDays, cancellationToken);
                    break;
                case "upload_version":
                    _sessionStore.SetPendingAction(message.Chat.Id, $"upload_version:{text.Trim()}");
                    await botClient.SendTextMessageAsync(
                        message.Chat.Id,
                        "Now send the update log (changelog) for this version, or type 'skip' to skip:",
                        cancellationToken: cancellationToken);
                    return;
                case var uploadAction when uploadAction.StartsWith("upload_version:"):
                    var version = uploadAction.Replace("upload_version:", "");
                    var changelog = text.Trim().Equals("skip", StringComparison.OrdinalIgnoreCase) ? null : text.Trim();
                    var changelogStr = changelog ?? "NONE";
                    _sessionStore.SetPendingAction(message.Chat.Id, $"upload_file:{version}:{changelogStr}");
                    await botClient.SendTextMessageAsync(
                        message.Chat.Id,
                        "📎 *Now send the .7z archive as a document*\n\n" +
                        "⚠️ Make sure the archive is *WITHOUT password*\n" +
                        "Each user will receive a unique password automatically.",
                        ParseMode.Markdown,
                        cancellationToken: cancellationToken);
                    _logger.LogInformation("Returning from upload_version handler, pending action should remain");
                    return;
                default:
                    await botClient.SendTextMessageAsync(message.Chat.Id, "Unknown action.", cancellationToken: cancellationToken);
                    break;
            }
        }
        catch (FormatException ex)
        {
            await botClient.SendTextMessageAsync(
                message.Chat.Id,
                $"❌ {ex.Message}",
                cancellationToken: cancellationToken);
            _sessionStore.ClearPendingAction(message.Chat.Id);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process admin action {Action}", action);
            await botClient.SendTextMessageAsync(
                message.Chat.Id,
                $"❌ Error: {ex.Message}",
                cancellationToken: cancellationToken);
            _sessionStore.ClearPendingAction(message.Chat.Id);
            return;
        }

        // Clear pending action only for completed actions (not upload_version or upload_file)
        _logger.LogInformation("Clearing pending action after completed action: {Action}", action);
        _sessionStore.ClearPendingAction(message.Chat.Id);

        await botClient.SendTextMessageAsync(
            message.Chat.Id,
            MainMenuText,
            replyMarkup: BuildMainMenu(),
            cancellationToken: cancellationToken);
    }

    private async Task HandleCallbackAsync(ITelegramBotClient botClient, CallbackQuery query, CancellationToken cancellationToken)
    {
        if (!IsAdmin(query.From.Id))
        {
            await botClient.AnswerCallbackQueryAsync(query.Id, "Access denied.", showAlert: true, cancellationToken: cancellationToken);
            return;
        }

        if (!string.IsNullOrEmpty(query.Data) &&
            query.Data.StartsWith("reset_admin|", StringComparison.OrdinalIgnoreCase))
        {
            await HandleResetRequestCallbackAsync(botClient, query, cancellationToken);
            return;
        }

        if (!string.IsNullOrEmpty(query.Data) &&
            query.Data.StartsWith("confirm_adddaysall_", StringComparison.OrdinalIgnoreCase))
        {
            await HandleConfirmAddDaysToAllAsync(botClient, query, cancellationToken);
            return;
        }

        if (!string.IsNullOrEmpty(query.Data) && query.Data == "cancel_adddaysall")
        {
            await botClient.AnswerCallbackQueryAsync(query.Id, "Action cancelled.", cancellationToken: cancellationToken);
            await botClient.EditMessageTextAsync(
                query.Message!.Chat.Id,
                query.Message.MessageId,
                "❌ Action cancelled. No changes were made.",
                cancellationToken: cancellationToken);
            return;
        }

        if (!string.IsNullOrEmpty(query.Data) &&
            query.Data.StartsWith("payment_approve_", StringComparison.OrdinalIgnoreCase))
        {
            await HandlePaymentApproveAsync(botClient, query, cancellationToken);
            return;
        }

        if (!string.IsNullOrEmpty(query.Data) &&
            query.Data.StartsWith("payment_reject_", StringComparison.OrdinalIgnoreCase))
        {
            await HandlePaymentRejectAsync(botClient, query, cancellationToken);
            return;
        }

        await botClient.AnswerCallbackQueryAsync(query.Id, cancellationToken: cancellationToken);

        switch (query.Data)
        {
            case "back_to_menu":
                await SendMainMenuAsync(botClient, query.Message!.Chat.Id, cancellationToken);
                break;
            case "menu_stats":
                await SendStatsAsync(botClient, query.Message!.Chat.Id, cancellationToken);
                break;
            case "menu_users":
                await SendUsersListAsync(botClient, query.Message!.Chat.Id, cancellationToken);
                break;
            case "menu_allusers":
                await HandleAllUsersAsync(botClient, query.Message!.Chat.Id, cancellationToken);
                break;
            case "menu_allkeys":
                await HandleAllKeysAsync(botClient, query.Message!.Chat.Id, cancellationToken);
                break;
            case "menu_resets":
                await HandlePendingResetsAsync(botClient, query.Message!.Chat.Id, cancellationToken);
                break;
            case "menu_expired":
                await HandleExpiredLicensesAsync(botClient, query.Message!.Chat.Id, cancellationToken);
                break;
            case "menu_banned":
                await HandleBannedLicensesAsync(botClient, query.Message!.Chat.Id, cancellationToken);
                break;
            case "menu_logs":
                await HandleViewLogsAsync(botClient, query.Message!.Chat.Id, Array.Empty<string>(), cancellationToken);
                break;
            case "menu_upload":
                await HandleUploadRequestAsync(botClient, query.Message!.Chat.Id, cancellationToken);
                break;
            case "menu_versions":
                await HandleVersionHistoryAsync(botClient, query.Message!.Chat.Id, cancellationToken);
                break;
            case "menu_user":
            case "menu_ban":
            case "menu_unban":
            case "menu_adddays":
            case "menu_adddaysall":
            case "menu_deletehwid":
            case "menu_keys":
            case "menu_search":
            case "menu_deletekey":
            case "menu_deleteallkeys":
            case "menu_createkey":
                await PromptForActionAsync(botClient, query.Message!, query.Data!, cancellationToken);
                break;
            default:
                await botClient.AnswerCallbackQueryAsync(query.Id, "Unknown action", cancellationToken: cancellationToken);
                break;
        }
    }

    private async Task HandleResetRequestCallbackAsync(ITelegramBotClient botClient, CallbackQuery query, CancellationToken cancellationToken)
    {
        if (query.Message is null || string.IsNullOrEmpty(query.Data))
        {
            return;
        }

        var parts = query.Data.Split('|', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3)
        {
            await botClient.AnswerCallbackQueryAsync(query.Id, "Invalid payload.", showAlert: true, cancellationToken: cancellationToken);
            return;
        }

        var action = parts[1];
        if (!int.TryParse(parts[2], out var requestId))
        {
            await botClient.AnswerCallbackQueryAsync(query.Id, "Invalid request ID.", showAlert: true, cancellationToken: cancellationToken);
            return;
        }

        var request = await _repository.GetResetRequestByIdAsync(requestId, cancellationToken);
        if (request is null)
        {
            await botClient.AnswerCallbackQueryAsync(query.Id, "Request not found.", showAlert: true, cancellationToken: cancellationToken);
            await botClient.EditMessageReplyMarkupAsync(query.Message.Chat.Id, query.Message.MessageId, replyMarkup: null, cancellationToken: cancellationToken);
            return;
        }

        if (!string.Equals(request.Status, "pending", StringComparison.OrdinalIgnoreCase))
        {
            await botClient.AnswerCallbackQueryAsync(query.Id, "Request already processed.", showAlert: true, cancellationToken: cancellationToken);
            await botClient.EditMessageReplyMarkupAsync(query.Message.Chat.Id, query.Message.MessageId, replyMarkup: null, cancellationToken: cancellationToken);
            return;
        }

        switch (action.ToLowerInvariant())
        {
            case "approve":
                await ApproveResetRequestAsync(botClient, query, request, cancellationToken);
                break;
            case "reject":
                await RejectResetRequestAsync(botClient, query, request, cancellationToken);
                break;
            default:
                await botClient.AnswerCallbackQueryAsync(query.Id, "Unknown action.", showAlert: true, cancellationToken: cancellationToken);
                break;
        }
    }

    private async Task ApproveResetRequestAsync(ITelegramBotClient botClient, CallbackQuery query, HwidResetRequest request, CancellationToken cancellationToken)
    {
        if (!await _repository.UpdateResetRequestStatusAsync(request.Id, "pending", "approved", cancellationToken))
        {
            await botClient.AnswerCallbackQueryAsync(query.Id, "Request already processed.", showAlert: true, cancellationToken: cancellationToken);
            await botClient.EditMessageReplyMarkupAsync(query.Message!.Chat.Id, query.Message.MessageId, replyMarkup: null, cancellationToken: cancellationToken);
            return;
        }

        var record = await _repository.DeleteLicenseAsync(request.Hwid, cancellationToken);
        string? issuedKey = null;
        int? issuedKeyDays = null;
        var reusedExistingKey = false;
        DateTime? subscriptionUtc = null;

        if (record is not null)
        {
            var now = DateTime.UtcNow;
            var subscriptionValue = DateTime.SpecifyKind(record.Subscription, DateTimeKind.Utc);
            subscriptionUtc = subscriptionValue;

            if (subscriptionValue > now)
            {
                var remaining = subscriptionValue - now;
                var days = Math.Max(1, (int)Math.Ceiling(remaining.TotalDays));

                var candidate = !string.IsNullOrWhiteSpace(record.LastKey)
                    ? record.LastKey!
                    : SubscriptionKeyGenerator.Generate();

                try
                {
                    await _repository.UpsertSubscriptionKeyAsync(request.UserId, candidate, days, subscriptionValue, cancellationToken);
                    issuedKey = candidate;
                    issuedKeyDays = days;
                    reusedExistingKey = !string.IsNullOrWhiteSpace(record.LastKey);
                }
                catch (MySqlException ex)
                {
                    _logger.LogError(ex, "Failed to re-issue subscription key for user {UserId} (HWID {Hwid}).", request.UserId, request.Hwid);
                }
            }
        }

        var adminMessage = new StringBuilder()
            .AppendLine("📨 *HWID Reset Request*")
            .AppendLine()
            .AppendLine($"👤 User ID: `{request.UserId}`")
            .AppendLine($"💻 HWID: `{request.Hwid}`")
            .AppendLine($"🕒 Requested: {request.CreatedAt:yyyy-MM-dd HH:mm:ss} UTC")
            .AppendLine()
            .Append("✅ Request approved. ");

        if (record is null)
        {
            adminMessage.Append("HWID was not found in the database.");
        }
        else if (issuedKey is null)
        {
            adminMessage.Append("HWID entry removed, but no replacement key could be issued (subscription expired or generation failed).");
        }
        else
        {
            adminMessage.Append(
                    reusedExistingKey
                        ? "HWID entry removed and original activation key restored."
                        : "HWID entry removed and new activation key issued.")
                        .AppendLine()
                        .AppendLine()
                        .AppendLine($"🔑 *Key:* `{issuedKey}`")
                        .AppendLine($"📅 Valid for: {issuedKeyDays} day(s)");

            if (subscriptionUtc is not null && subscriptionUtc > DateTime.UtcNow)
            {
                adminMessage.Append($"\n📅 Expires: {subscriptionUtc:yyyy-MM-dd HH:mm} UTC");
            }
        }

        await botClient.EditMessageTextAsync(
            query.Message!.Chat.Id,
            query.Message.MessageId,
            adminMessage.ToString(),
            ParseMode.Markdown,
            cancellationToken: cancellationToken);

        await botClient.AnswerCallbackQueryAsync(query.Id, "Reset request approved.", cancellationToken: cancellationToken);

        try
        {
            if (issuedKey is not null && issuedKeyDays.HasValue)
            {
                var userMessage = new StringBuilder()
                    .AppendLine("✅ *HWID Reset Approved*")
                    .AppendLine()
                    .AppendLine($"💻 HWID: `{request.Hwid}`")
                    .AppendLine("Your previous HWID binding has been removed.")
                    .AppendLine()
                    .AppendLine(reusedExistingKey
                        ? "🔑 *Activation Key Re-enabled:*"
                        : "🔑 *New Activation Key:*")
                    .AppendLine($"`{issuedKey}`")
                    .AppendLine()
                    .AppendLine($"📅 Valid for: {issuedKeyDays.Value} day(s)")
                    .AppendLine(subscriptionUtc is not null
                        ? $"📅 Expires: {subscriptionUtc:yyyy-MM-dd HH:mm} UTC"
                        : "📅 Expires: (not available)")
                    .AppendLine()
                    .AppendLine("Use this key in your application together with the new HWID to restore access.");

                await botClient.SendTextMessageAsync(
                    request.UserId,
                    userMessage.ToString(),
                    ParseMode.Markdown,
                    cancellationToken: cancellationToken);
            }
            else
            {
                await botClient.SendTextMessageAsync(
                    request.UserId,
                    $"✅ Your HWID reset request for `{request.Hwid}` has been approved.\n\nThe previous subscription appears to be expired, so no new key was issued. If you believe this is an error, please contact support.",
                    ParseMode.Markdown,
                    cancellationToken: cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to notify user {UserId} about reset approval.", request.UserId);
        }

        var logDetails = issuedKey is not null
            ? $"Key: {issuedKey}, Days: {issuedKeyDays}, Reused: {reusedExistingKey}"
            : "No key issued";
        await LogActionAsync(query.From.Id, "approve_reset", "reset_request", request.Id.ToString(),
            $"User {request.UserId}, HWID {request.Hwid}, {logDetails}", cancellationToken);
    }

    private async Task RejectResetRequestAsync(ITelegramBotClient botClient, CallbackQuery query, HwidResetRequest request, CancellationToken cancellationToken)
    {
        if (!await _repository.UpdateResetRequestStatusAsync(request.Id, "pending", "rejected", cancellationToken))
        {
            await botClient.AnswerCallbackQueryAsync(query.Id, "Request already processed.", showAlert: true, cancellationToken: cancellationToken);
            await botClient.EditMessageReplyMarkupAsync(query.Message!.Chat.Id, query.Message.MessageId, replyMarkup: null, cancellationToken: cancellationToken);
            return;
        }

        var adminMessage = new StringBuilder()
            .AppendLine("📨 *HWID Reset Request*")
            .AppendLine()
            .AppendLine($"👤 User ID: `{request.UserId}`")
            .AppendLine($"💻 HWID: `{request.Hwid}`")
            .AppendLine($"🕒 Requested: {request.CreatedAt:yyyy-MM-dd HH:mm:ss} UTC")
            .AppendLine()
            .Append("🚫 Request rejected.");

        await botClient.EditMessageTextAsync(
            query.Message!.Chat.Id,
            query.Message.MessageId,
            adminMessage.ToString(),
            ParseMode.Markdown,
            cancellationToken: cancellationToken);

        await botClient.AnswerCallbackQueryAsync(query.Id, "Reset request rejected.", cancellationToken: cancellationToken);

        try
        {
            await botClient.SendTextMessageAsync(
                request.UserId,
                $"❌ Your HWID reset request for `{request.Hwid}` has been rejected. Contact support if you need further assistance.",
                ParseMode.Markdown,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to notify user {UserId} about reset rejection.", request.UserId);
        }

        await LogActionAsync(query.From.Id, "reject_reset", "reset_request", request.Id.ToString(),
            $"User {request.UserId}, HWID {request.Hwid}", cancellationToken);
    }

    private async Task PromptForActionAsync(ITelegramBotClient botClient, Message message, string menuAction, CancellationToken cancellationToken)
    {
        var action = menuAction switch
        {
            "menu_user" => "user",
            "menu_ban" => "ban",
            "menu_unban" => "unban",
            "menu_adddays" => "adddays",
            "menu_adddaysall" => "adddaysall",
            "menu_deletehwid" => "deletehwid",
            "menu_keys" => "keys",
            "menu_search" => "search",
            "menu_deletekey" => "deletekey",
            "menu_deleteallkeys" => "deleteallkeys",
            "menu_createkey" => "createkey",
            _ => string.Empty
        };

        if (string.IsNullOrEmpty(action))
        {
            await SendMainMenuAsync(botClient, message.Chat.Id, cancellationToken);
            return;
        }

        _sessionStore.SetPendingAction(message.Chat.Id, action);

        var prompt = action switch
        {
            "user" => "Enter a user ID or `hwid <HWID>`.",
            "ban" => "Provide a user ID or `hwid <HWID>` to ban.",
            "unban" => "Provide a user ID or `hwid <HWID>` to unban.",
            "adddays" => "Enter `<user_id> <days>` or `hwid <HWID> <days>`.",
            "adddaysall" => "⚠️ *WARNING: This will add days to ALL users!*\n\nEnter the number of days to add to all users' subscriptions:",
            "deletehwid" => "Enter the HWID you want to delete.",
            "keys" => "Enter a user ID to view their keys.",
            "search" => "Enter a HWID pattern to search (supports partial matches).",
            "deletekey" => "Enter the subscription key you want to delete (e.g., KEY123ABC456DEF789):",
            "deleteallkeys" => "⚠️ *WARNING: This will delete ALL keys for a user!*\n\nEnter the user ID whose keys you want to delete:",
            "createkey" => "Enter `<user_id> <days>` to create a subscription key.\n\nExample: `8400449655 30` (creates 30-day key for user)",
            _ => "Provide the required data."
        };

        await botClient.SendTextMessageAsync(
            message.Chat.Id,
            prompt,
            ParseMode.Markdown,
            cancellationToken: cancellationToken);
    }

    private async Task SendMainMenuAsync(ITelegramBotClient botClient, long chatId, CancellationToken cancellationToken)
    {
        await botClient.SendTextMessageAsync(
            chatId,
            MainMenuText,
            replyMarkup: BuildMainMenu(),
            cancellationToken: cancellationToken);
    }

    private static InlineKeyboardMarkup BuildBackButton() => new(new[]
    {
        new[] { InlineKeyboardButton.WithCallbackData("🔙 Back to Menu", "back_to_menu") }
    });

    private async Task SendStatsAsync(ITelegramBotClient botClient, long chatId, CancellationToken cancellationToken)
    {
        try
        {
            var usersTask = _repository.GetSystemStatsAsync(cancellationToken);
            var keysTask = _repository.GetKeyStatsAsync(cancellationToken);
            await Task.WhenAll(usersTask, keysTask);

            var users = usersTask.Result;
            var keys = keysTask.Result;

            var message = new StringBuilder()
                .AppendLine("📊 *System Statistics*\n")
                .AppendLine("👥 *Users:*")
                .AppendLine($"   Total HWIDs: {users.TotalUsers}")
                .AppendLine($"   Unique Users (with subscriptions): {users.UniqueUsers}")
                .AppendLine($"   Active Subscriptions: {(int)users.ActiveSubscriptions}")
                .AppendLine($"   Banned Users: {(int)users.BannedUsers}\n")
                .AppendLine("🔑 *Keys:*")
                .AppendLine($"   Active Keys: {keys.TotalKeys}")
                .AppendLine($"   Total Days Sold: {keys.TotalDaysSold}")
                .Append($"   Unique Key Owners: {keys.UniqueKeyOwners}")
                .ToString();

            await botClient.SendTextMessageAsync(
                chatId,
                message,
                ParseMode.Markdown,
                replyMarkup: BuildBackButton(),
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve statistics");
            await botClient.SendTextMessageAsync(chatId, $"❌ Error: {ex.Message}", cancellationToken: cancellationToken);
        }
    }

    private async Task SendUsersListAsync(ITelegramBotClient botClient, long chatId, CancellationToken cancellationToken)
    {
        try
        {
            var activatedTask = _repository.GetUserSummariesAsync(25, cancellationToken);
            var keyHoldersTask = _repository.GetKeyHolderSummariesAsync(25, cancellationToken);
            await Task.WhenAll(activatedTask, keyHoldersTask);

            var activated = activatedTask.Result;
            var keyHolders = keyHoldersTask.Result;

            if (activated.Count == 0 && keyHolders.Count == 0)
            {
                await botClient.SendTextMessageAsync(chatId, "❌ No users found.", cancellationToken: cancellationToken);
                return;
            }

            var builder = new StringBuilder("👥 *Users List*\n\n");

            if (activated.Count > 0)
            {
                builder.AppendLine($"*Activated Users ({activated.Count}):*\n");
                for (var i = 0; i < activated.Count; i++)
                {
                    var user = activated[i];
                    builder.AppendLine($"*{i + 1}.* User ID: `{user.Id}`")
                           .AppendLine($"   HWIDs: {user.HwidCount}")
                           .AppendLine($"   Latest: {user.LatestSubscription:yyyy-MM-dd HH:mm:ss}")
                           .AppendLine($"   Banned: {user.BannedCount}\n");
                }
            }

            if (keyHolders.Count > 0)
            {
                builder.AppendLine(activated.Count > 0
                    ? $"\n*Users with Keys (Not Activated) ({keyHolders.Count}):*\n"
                    : $"*Users with Keys (Not Activated) ({keyHolders.Count}):*\n");

                for (var i = 0; i < keyHolders.Count; i++)
                {
                    var user = keyHolders[i];
                    builder.AppendLine($"*{i + 1}.* User ID: `{user.Id}`")
                           .AppendLine($"   Keys: {user.KeyCount}")
                           .AppendLine($"   Total Days: {user.TotalDays}")
                           .AppendLine("   Status: ⏳ Not activated\n");
                }
            }

            await botClient.SendTextMessageAsync(chatId, builder.ToString(), ParseMode.Markdown, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list users");
            await botClient.SendTextMessageAsync(chatId, $"❌ Error: {ex.Message}", cancellationToken: cancellationToken);
        }
    }

    private async Task HandleUserLookupAsync(ITelegramBotClient botClient, long chatId, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<UserLicenseRecord> records;

            if (args.Count > 1 && args[0].Equals("hwid", StringComparison.OrdinalIgnoreCase))
            {
                var hwid = args[1];
                records = await _repository.GetLicensesByHwidAsync(hwid, cancellationToken);
            }
            else
            {
                if (!long.TryParse(args[0], out var userId))
                {
                    throw new FormatException("Provide a valid numeric user ID.");
                }

                records = await _repository.GetLicensesAsync(userId, cancellationToken);
            }

            if (records.Count == 0)
            {
                await botClient.SendTextMessageAsync(chatId, "❌ User not found.", cancellationToken: cancellationToken);
                return;
            }

            var now = DateTime.UtcNow;
            var builder = new StringBuilder("👤 *User Information*\n\n");

            foreach (var record in records)
            {
                var status = record.Subscription > now
                    ? $"✅ Active ({(record.Subscription - now).Days} days left)"
                    : "❌ Expired";

                builder.AppendLine($"🆔 *User ID:* `{record.Id}`")
                       .AppendLine($"💻 *HWID:* `{record.Hwid}`")
                       .AppendLine($"📅 *Subscription:* {record.Subscription:yyyy-MM-dd HH:mm:ss}")
                       .AppendLine($"📊 *Status:* {status}")
                       .AppendLine($"🔒 *Banned:* {(record.Banned ? "Yes" : "No")}\n");
            }

            await botClient.SendTextMessageAsync(chatId, builder.ToString(), ParseMode.Markdown, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not FormatException)
        {
            _logger.LogError(ex, "Error getting user info");
            await botClient.SendTextMessageAsync(chatId, $"❌ Error: {ex.Message}", cancellationToken: cancellationToken);
        }
    }

    private async Task HandleBanAsync(ITelegramBotClient botClient, long chatId, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        bool result;
        string message;
        string? targetId = null;
        string? targetType = null;

        if (args.Count > 1 && args[0].Equals("hwid", StringComparison.OrdinalIgnoreCase))
        {
            var hwid = args[1];
            targetId = hwid;
            targetType = "hwid";
            result = await _repository.BanUserByHwidAsync(hwid, cancellationToken);
            message = result
                ? $"✅ HWID `{hwid}` has been banned."
                : $"❌ HWID `{hwid}` not found.";
        }
        else
        {
            if (!long.TryParse(args[0], out var userId))
            {
                throw new FormatException("Provide a valid numeric user ID.");
            }
            targetId = userId.ToString();
            targetType = "user";

            result = await _repository.BanUserByUserIdAsync(userId, cancellationToken);
            message = result
                ? $"✅ User `{userId}` has been banned."
                : $"❌ User `{userId}` not found.";
        }

        await botClient.SendTextMessageAsync(chatId, message, ParseMode.Markdown, cancellationToken: cancellationToken);
        await LogActionAsync(chatId, "ban", targetType, targetId, result ? "Success" : "Not found", cancellationToken);
    }

    private async Task HandleUnbanAsync(ITelegramBotClient botClient, long chatId, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        bool result;
        string message;
        string? targetId = null;
        string? targetType = null;

        if (args.Count > 1 && args[0].Equals("hwid", StringComparison.OrdinalIgnoreCase))
        {
            var hwid = args[1];
            targetId = hwid;
            targetType = "hwid";
            result = await _repository.UnbanUserByHwidAsync(hwid, cancellationToken);
            message = result
                ? $"✅ HWID `{hwid}` has been unbanned."
                : $"❌ HWID `{hwid}` not found.";
        }
        else
        {
            if (!long.TryParse(args[0], out var userId))
            {
                throw new FormatException("Provide a valid numeric user ID.");
            }
            targetId = userId.ToString();
            targetType = "user";
            result = await _repository.UnbanUserByUserIdAsync(userId, cancellationToken);
            message = result
                ? $"✅ User `{userId}` has been unbanned."
                : $"❌ User `{userId}` not found.";
        }

        await botClient.SendTextMessageAsync(chatId, message, ParseMode.Markdown, cancellationToken: cancellationToken);
        await LogActionAsync(chatId, "unban", targetType, targetId, result ? "Success" : "Not found", cancellationToken);
    }

    private async Task HandleAddDaysAsync(ITelegramBotClient botClient, long chatId, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        bool result;
        string message;
        string? targetId = null;
        string? targetType = null;
        int days = 0;

        if (args.Count > 2 && args[0].Equals("hwid", StringComparison.OrdinalIgnoreCase))
        {
            var hwid = args[1];
            targetId = hwid;
            targetType = "hwid";
            if (!int.TryParse(args[2], out days))
            {
                throw new FormatException("Provide a valid number of days.");
            }

            result = await _repository.AddDaysByHwidAsync(hwid, days, cancellationToken);
            message = result
                ? $"✅ Added {days} days to HWID `{hwid}`"
                : $"❌ HWID `{hwid}` not found.";
        }
        else
        {
            if (!long.TryParse(args[0], out var userId))
            {
                throw new FormatException("Provide a valid numeric user ID.");
            }
            targetId = userId.ToString();
            targetType = "user";

            if (!int.TryParse(args[1], out days))
            {
                throw new FormatException("Provide a valid number of days.");
            }

            result = await _repository.AddDaysByUserIdAsync(userId, days, cancellationToken);
            message = result
                ? $"✅ Added {days} days to user `{userId}`"
                : $"❌ User `{userId}` not found.";
        }

        await botClient.SendTextMessageAsync(chatId, message, ParseMode.Markdown, cancellationToken: cancellationToken);
        await LogActionAsync(chatId, "add_days", targetType, targetId, $"Added {days} days - {(result ? "Success" : "Not found")}", cancellationToken);
    }

    private async Task HandleDeleteHwidAsync(ITelegramBotClient botClient, long chatId, string hwid, CancellationToken cancellationToken)
    {
        try
        {
            var record = await _repository.DeleteLicenseAsync(hwid, cancellationToken);
            if (record is null)
            {
                await botClient.SendTextMessageAsync(chatId, $"❌ HWID `{hwid}` not found.", ParseMode.Markdown, cancellationToken: cancellationToken);
                await LogActionAsync(chatId, "delete_hwid", "hwid", hwid, "Not found", cancellationToken);
                return;
            }

            await botClient.SendTextMessageAsync(
                chatId,
                $"✅ License for HWID `{record.Hwid}` (User `{record.Id}`) has been removed.",
                ParseMode.Markdown,
                cancellationToken: cancellationToken);
            await LogActionAsync(chatId, "delete_hwid", "hwid", hwid, $"User {record.Id} - Success", cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting license");
            await botClient.SendTextMessageAsync(chatId, $"❌ Error: {ex.Message}", cancellationToken: cancellationToken);
        }
    }

    private async Task HandleDeleteKeyAsync(ITelegramBotClient botClient, long chatId, string key, CancellationToken cancellationToken)
    {
        try
        {
            var deleted = await _repository.DeleteKeyAsync(key, cancellationToken);
            if (!deleted)
            {
                await botClient.SendTextMessageAsync(chatId, $"❌ Key `{key}` not found.", ParseMode.Markdown, cancellationToken: cancellationToken);
                await LogActionAsync(chatId, "delete_key", "key", key, "Not found", cancellationToken);
                return;
            }

            await botClient.SendTextMessageAsync(
                chatId,
                $"✅ Key `{key}` has been deleted successfully.",
                ParseMode.Markdown,
                cancellationToken: cancellationToken);
            await LogActionAsync(chatId, "delete_key", "key", key, "Success", cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting key");
            await botClient.SendTextMessageAsync(chatId, $"❌ Error: {ex.Message}", cancellationToken: cancellationToken);
        }
    }

    private async Task HandleDeleteAllKeysAsync(ITelegramBotClient botClient, long chatId, long userId, CancellationToken cancellationToken)
    {
        try
        {
            var deletedCount = await _repository.DeleteAllKeysByUserIdAsync(userId, cancellationToken);
            if (deletedCount == 0)
            {
                await botClient.SendTextMessageAsync(chatId, $"❌ No keys found for user `{userId}`.", ParseMode.Markdown, cancellationToken: cancellationToken);
                await LogActionAsync(chatId, "delete_all_keys", "user", userId.ToString(), "No keys found", cancellationToken);
                return;
            }

            await botClient.SendTextMessageAsync(
                chatId,
                $"✅ Deleted {deletedCount} key(s) for user `{userId}`.",
                ParseMode.Markdown,
                cancellationToken: cancellationToken);
            await LogActionAsync(chatId, "delete_all_keys", "user", userId.ToString(), $"Deleted {deletedCount} keys", cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting all keys for user");
            await botClient.SendTextMessageAsync(chatId, $"❌ Error: {ex.Message}", cancellationToken: cancellationToken);
        }
    }

    private async Task HandleCreateKeyAsync(ITelegramBotClient botClient, long chatId, long targetUserId, int days, CancellationToken cancellationToken)
    {
        try
        {
            // Generate key
            var key = SubscriptionKeyGenerator.Generate();

            // Save key to database
            await _repository.InsertSubscriptionKeyAsync(targetUserId, key, days, expiresAt: null, cancellationToken);

            // Send confirmation to admin
            var durationText = days >= 99999 ? "Lifetime" : $"{days} days";
            await botClient.SendTextMessageAsync(
                chatId,
                $"✅ Key created successfully!\n\n" +
                $"👤 User ID: `{targetUserId}`\n" +
                $"🔑 Key: `{key}`\n" +
                $"📅 Duration: {durationText}\n\n" +
                $"The user will be notified in their DM.",
                ParseMode.Markdown,
                cancellationToken: cancellationToken);

            // Send notification to user via UserBot
            try
            {
                await _userBotClient.SendTextMessageAsync(
                    targetUserId,
                    $"🎉 *You received a new subscription key!*\n\n" +
                    $"🔑 *Key:* `{key}`\n" +
                    $"📅 *Duration:* {durationText}\n\n" +
                    $"*How to use:*\n" +
                    $"1. Enter this key in your application together with your HWID.\n" +
                    $"2. The key can be used only once.\n" +
                    $"3. The key is deleted immediately after successful activation.\n\n" +
                    $"Thank you! 💫",
                    ParseMode.Markdown,
                    cancellationToken: cancellationToken);

                _logger.LogInformation("Sent key notification to user {UserId}", targetUserId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send key notification to user {UserId}", targetUserId);
                await botClient.SendTextMessageAsync(
                    chatId,
                    $"⚠️ Key created but failed to notify user (they may have blocked the bot).",
                    cancellationToken: cancellationToken);
            }

            await LogActionAsync(chatId, "create_key", "user", targetUserId.ToString(), $"Created {days}-day key: {key}", cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating key for user {UserId}", targetUserId);
            await botClient.SendTextMessageAsync(chatId, $"❌ Error: {ex.Message}", cancellationToken: cancellationToken);
        }
    }

    private bool IsAdmin(long userId) => _commonOptions.AdminIds.Contains(userId);

    private async Task LogActionAsync(long adminId, string actionType, string? targetType, string? targetId, string? details, CancellationToken cancellationToken)
    {
        try
        {
            await _repository.LogAdminActionAsync(adminId, actionType, targetType, targetId, details, cancellationToken);
            _logger.LogInformation("Admin {AdminId} performed action {ActionType} on {TargetType} {TargetId}: {Details}",
                adminId, actionType, targetType ?? "N/A", targetId ?? "N/A", details ?? "N/A");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to log admin action {ActionType} for admin {AdminId}", actionType, adminId);
        }
    }

    private async Task HandlePendingResetsAsync(ITelegramBotClient botClient, long chatId, CancellationToken cancellationToken)
    {
        try
        {
            var requests = await _repository.GetPendingResetRequestsAsync(cancellationToken);

            if (requests.Count == 0)
            {
                await botClient.SendTextMessageAsync(chatId, "✅ No pending reset requests.", cancellationToken: cancellationToken);
                await LogActionAsync(chatId, "view_resets", null, null, "No pending requests", cancellationToken);
                return;
            }

            var builder = new StringBuilder($"📋 *Pending Reset Requests ({requests.Count}):*\n\n");

            for (var i = 0; i < requests.Count && i < 20; i++)
            {
                var req = requests[i];
                builder.AppendLine($"*{i + 1}.* Request ID: `{req.Id}`")
                       .AppendLine($"   👤 User: `{req.UserId}`")
                       .AppendLine($"   💻 HWID: `{req.Hwid}`")
                       .AppendLine($"   🕒 Created: {req.CreatedAt:yyyy-MM-dd HH:mm:ss} UTC\n");
            }

            if (requests.Count > 20)
            {
                builder.AppendLine($"_... and {requests.Count - 20} more_");
            }

            await botClient.SendTextMessageAsync(chatId, builder.ToString(), ParseMode.Markdown, cancellationToken: cancellationToken);
            await LogActionAsync(chatId, "view_resets", null, null, $"Viewed {requests.Count} pending requests", cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load pending reset requests");
            await botClient.SendTextMessageAsync(chatId, $"❌ Error: {ex.Message}", cancellationToken: cancellationToken);
        }
    }

    private async Task HandleViewUserKeysAsync(ITelegramBotClient botClient, long chatId, long userId, CancellationToken cancellationToken)
    {
        try
        {
            var keys = await _repository.GetAllKeysByUserIdAsync(userId, cancellationToken);

            if (keys.Count == 0)
            {
                await botClient.SendTextMessageAsync(chatId, $"❌ User `{userId}` has no keys.", ParseMode.Markdown, cancellationToken: cancellationToken);
                await LogActionAsync(chatId, "view_keys", "user", userId.ToString(), "No keys found", cancellationToken);
                return;
            }

            var builder = new StringBuilder($"🔑 *Keys for User `{userId}` ({keys.Count}):*\n\n");
            var now = DateTime.UtcNow;

            foreach (var key in keys)
            {
                string status;
                if (key.ExpiresAt is null)
                {
                    status = "⏳ Not activated";
                }
                else if (key.ExpiresAt > now)
                {
                    var remaining = key.ExpiresAt.Value - now;
                    status = $"✅ Active ({remaining.Days}d {remaining.Hours}h left)";
                }
                else
                {
                    status = "❌ Expired";
                }

                builder.AppendLine($"🔑 `{key.SubscriptionKey}`")
                       .AppendLine($"   📅 Days: {key.Days}")
                       .AppendLine($"   📊 Status: {status}");

                if (key.ExpiresAt.HasValue)
                {
                    builder.AppendLine($"   ⏰ Expires: {key.ExpiresAt:yyyy-MM-dd HH:mm:ss} UTC");
                }
                builder.AppendLine();
            }

            await botClient.SendTextMessageAsync(chatId, builder.ToString(), ParseMode.Markdown, cancellationToken: cancellationToken);
            await LogActionAsync(chatId, "view_keys", "user", userId.ToString(), $"Viewed {keys.Count} keys", cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load keys for user {UserId}", userId);
            await botClient.SendTextMessageAsync(chatId, $"❌ Error: {ex.Message}", cancellationToken: cancellationToken);
        }
    }

    private async Task HandleSearchHwidAsync(ITelegramBotClient botClient, long chatId, string pattern, CancellationToken cancellationToken)
    {
        try
        {
            var results = await _repository.SearchUsersByHwidAsync(pattern, 50, cancellationToken);

            if (results.Count == 0)
            {
                await botClient.SendTextMessageAsync(chatId, $"❌ No users found matching pattern `{pattern}`.", ParseMode.Markdown, cancellationToken: cancellationToken);
                await LogActionAsync(chatId, "search_hwid", "hwid", pattern, "No results", cancellationToken);
                return;
            }

            var builder = new StringBuilder($"🔍 *Search Results for `{pattern}` ({results.Count}):*\n\n");
            var now = DateTime.UtcNow;

            for (var i = 0; i < results.Count && i < 20; i++)
            {
                var record = results[i];
                var status = record.Subscription > now
                    ? $"✅ Active ({(record.Subscription - now).Days}d left)"
                    : "❌ Expired";

                builder.AppendLine($"*{i + 1}.* User ID: `{record.Id}`")
                       .AppendLine($"   💻 HWID: `{record.Hwid}`")
                       .AppendLine($"   📅 Expires: {record.Subscription:yyyy-MM-dd HH:mm:ss}")
                       .AppendLine($"   📊 Status: {status}")
                       .AppendLine($"   🔒 Banned: {(record.Banned ? "Yes" : "No")}\n");
            }

            if (results.Count > 20)
            {
                builder.AppendLine($"_... and {results.Count - 20} more_");
            }

            await botClient.SendTextMessageAsync(chatId, builder.ToString(), ParseMode.Markdown, cancellationToken: cancellationToken);
            await LogActionAsync(chatId, "search_hwid", "hwid", pattern, $"Found {results.Count} results", cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search HWID pattern {Pattern}", pattern);
            await botClient.SendTextMessageAsync(chatId, $"❌ Error: {ex.Message}", cancellationToken: cancellationToken);
        }
    }

    private async Task HandleExpiredLicensesAsync(ITelegramBotClient botClient, long chatId, CancellationToken cancellationToken)
    {
        try
        {
            var expired = await _repository.GetExpiredLicensesAsync(100, cancellationToken);

            if (expired.Count == 0)
            {
                await botClient.SendTextMessageAsync(chatId, "✅ No expired licenses found.", cancellationToken: cancellationToken);
                await LogActionAsync(chatId, "view_expired", null, null, "No expired licenses", cancellationToken);
                return;
            }

            var builder = new StringBuilder($"⏰ *Expired Licenses ({expired.Count}):*\n\n");

            for (var i = 0; i < expired.Count && i < 20; i++)
            {
                var record = expired[i];
                builder.AppendLine($"*{i + 1}.* User ID: `{record.Id}`")
                       .AppendLine($"   💻 HWID: `{record.Hwid}`")
                       .AppendLine($"   📅 Expired: {record.Subscription:yyyy-MM-dd HH:mm:ss}")
                       .AppendLine($"   🔒 Banned: {(record.Banned ? "Yes" : "No")}\n");
            }

            if (expired.Count > 20)
            {
                builder.AppendLine($"_... and {expired.Count - 20} more_");
            }

            await botClient.SendTextMessageAsync(chatId, builder.ToString(), ParseMode.Markdown, cancellationToken: cancellationToken);
            await LogActionAsync(chatId, "view_expired", null, null, $"Viewed {expired.Count} expired licenses", cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load expired licenses");
            await botClient.SendTextMessageAsync(chatId, $"❌ Error: {ex.Message}", cancellationToken: cancellationToken);
        }
    }

    private async Task HandleBannedLicensesAsync(ITelegramBotClient botClient, long chatId, CancellationToken cancellationToken)
    {
        try
        {
            var banned = await _repository.GetBannedLicensesAsync(100, cancellationToken);

            if (banned.Count == 0)
            {
                await botClient.SendTextMessageAsync(chatId, "✅ No banned licenses found.", cancellationToken: cancellationToken);
                await LogActionAsync(chatId, "view_banned", null, null, "No banned licenses", cancellationToken);
                return;
            }

            var builder = new StringBuilder($"🚫 *Banned Licenses ({banned.Count}):*\n\n");
            var now = DateTime.UtcNow;

            for (var i = 0; i < banned.Count && i < 20; i++)
            {
                var record = banned[i];
                var status = record.Subscription > now ? "Active" : "Expired";

                builder.AppendLine($"*{i + 1}.* User ID: `{record.Id}`")
                       .AppendLine($"   💻 HWID: `{record.Hwid}`")
                       .AppendLine($"   📅 Subscription: {record.Subscription:yyyy-MM-dd HH:mm:ss}")
                       .AppendLine($"   📊 Status: {status}\n");
            }

            if (banned.Count > 20)
            {
                builder.AppendLine($"_... and {banned.Count - 20} more_");
            }

            await botClient.SendTextMessageAsync(chatId, builder.ToString(), ParseMode.Markdown, cancellationToken: cancellationToken);
            await LogActionAsync(chatId, "view_banned", null, null, $"Viewed {banned.Count} banned licenses", cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load banned licenses");
            await botClient.SendTextMessageAsync(chatId, $"❌ Error: {ex.Message}", cancellationToken: cancellationToken);
        }
    }

    private async Task HandleViewLogsAsync(ITelegramBotClient botClient, long chatId, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        try
        {
            long? filterAdminId = null;
            string? filterActionType = null;
            var limit = 50;

            if (args.Count > 0)
            {
                if (long.TryParse(args[0], out var adminId))
                {
                    filterAdminId = adminId;
                }
                else if (args[0].StartsWith("action:", StringComparison.OrdinalIgnoreCase))
                {
                    filterActionType = args[0].Substring(7);
                }
            }

            var logs = await _repository.GetAdminActionLogsAsync(filterAdminId, filterActionType, limit, cancellationToken);

            if (logs.Count == 0)
            {
                await botClient.SendTextMessageAsync(chatId, "📜 No action logs found.", cancellationToken: cancellationToken);
                return;
            }

            var builder = new StringBuilder($"📜 *Admin Action Logs ({logs.Count}):*\n\n");

            for (var i = 0; i < logs.Count && i < 20; i++)
            {
                var log = logs[i];
                builder.AppendLine($"*{i + 1}.* {log.ActionType}")
                       .AppendLine($"   👤 Admin: `{log.AdminId}`")
                       .AppendLine($"   🎯 Target: {log.TargetType ?? "N/A"} `{log.TargetId ?? "N/A"}`")
                       .AppendLine($"   🕒 {log.CreatedAt:yyyy-MM-dd HH:mm:ss} UTC");

                if (!string.IsNullOrWhiteSpace(log.Details))
                {
                    var details = log.Details.Length > 50 ? log.Details[..50] + "..." : log.Details;
                    builder.AppendLine($"   📝 {details}");
                }
                builder.AppendLine();
            }

            if (logs.Count > 20)
            {
                builder.AppendLine($"_... and {logs.Count - 20} more_");
            }

            await botClient.SendTextMessageAsync(chatId, builder.ToString(), ParseMode.Markdown, cancellationToken: cancellationToken);
            await LogActionAsync(chatId, "view_logs", null, null, $"Viewed {logs.Count} logs", cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load admin action logs");
            await botClient.SendTextMessageAsync(chatId, $"❌ Error: {ex.Message}", cancellationToken: cancellationToken);
        }
    }

    private static InlineKeyboardMarkup BuildMainMenu() => new(new[]
    {
        new[]
        {
            InlineKeyboardButton.WithCallbackData("📊 Statistics", "menu_stats"),
            InlineKeyboardButton.WithCallbackData("👥 All Users", "menu_allusers")
        },
        new[]
        {
            InlineKeyboardButton.WithCallbackData("🔑 All Keys", "menu_allkeys"),
            InlineKeyboardButton.WithCallbackData("📋 Reset Requests", "menu_resets")
        },
        new[]
        {
            InlineKeyboardButton.WithCallbackData("� Upload Update", "menu_upload"),
            InlineKeyboardButton.WithCallbackData("📚 Version History", "menu_versions")
        },
        new[]
        {
            InlineKeyboardButton.WithCallbackData("�� Lookup User", "menu_user"),
            InlineKeyboardButton.WithCallbackData("🔍 Search HWID", "menu_search")
        },
        new[]
        {
            InlineKeyboardButton.WithCallbackData("⛔ Ban", "menu_ban"),
            InlineKeyboardButton.WithCallbackData("✅ Unban", "menu_unban")
        },
        new[]
        {
            InlineKeyboardButton.WithCallbackData("➕ Add Days", "menu_adddays"),
            InlineKeyboardButton.WithCallbackData("🎁 Add Days to All", "menu_adddaysall")
        },
        new[]
        {
            InlineKeyboardButton.WithCallbackData("🎫 Create Key", "menu_createkey"),
            InlineKeyboardButton.WithCallbackData("🗑 Delete HWID", "menu_deletehwid")
        },
        new[]
        {
            InlineKeyboardButton.WithCallbackData("🗑 Delete Key", "menu_deletekey"),
            InlineKeyboardButton.WithCallbackData("🗑 Delete All Keys", "menu_deleteallkeys")
        },
        new[]
        {
            InlineKeyboardButton.WithCallbackData("⏰ Expired", "menu_expired"),
            InlineKeyboardButton.WithCallbackData("🚫 Banned", "menu_banned")
        },
        new[]
        {
            InlineKeyboardButton.WithCallbackData("📜 Action Logs", "menu_logs")
        }
    });

    private static string[] ParseActionArgs(string text, string expected)
    {
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            throw new FormatException("Please provide the required data.");
        }

        return expected switch
        {
            "ban" or "unban" => ParseBanArgs(tokens),
            "adddays" => ParseAddDaysArgs(tokens),
            "deletehwid" => tokens.Length == 1
                ? tokens
                : throw new FormatException("Provide only the HWID."),
            "user" => ParseUserArgs(tokens),
            _ => tokens
        };
    }

    private static string[] ParseBanArgs(IReadOnlyList<string> tokens)
    {
        if (tokens[0].Equals("hwid", StringComparison.OrdinalIgnoreCase))
        {
            if (tokens.Count < 2)
            {
                throw new FormatException("Provide the HWID after the keyword.");
            }

            return new[] { "hwid", tokens[1] };
        }

        return new[] { tokens[0] };
    }

    private static string[] ParseAddDaysArgs(IReadOnlyList<string> tokens)
    {
        if (tokens[0].Equals("hwid", StringComparison.OrdinalIgnoreCase))
        {
            if (tokens.Count < 3)
            {
                throw new FormatException("Use the format: hwid <HWID> <days>");
            }

            return new[] { "hwid", tokens[1], tokens[2] };
        }

        if (tokens.Count < 2)
        {
            throw new FormatException("Use the format: <user_id> <days>");
        }

        return new[] { tokens[0], tokens[1] };
    }

    private static string[] ParseUserArgs(IReadOnlyList<string> tokens)
    {
        if (tokens[0].Equals("hwid", StringComparison.OrdinalIgnoreCase))
        {
            if (tokens.Count < 2)
            {
                throw new FormatException("Provide the HWID after the keyword.");
            }

            return new[] { "hwid", tokens[1] };
        }

        return new[] { tokens[0] };
    }

    private async Task HandleAllUsersAsync(ITelegramBotClient botClient, long chatId, CancellationToken cancellationToken)
    {
        try
        {
            var users = await _repository.GetAllUsersAsync(100, cancellationToken);

            if (users.Count == 0)
            {
                await botClient.SendTextMessageAsync(chatId, "❌ No users found in the database.", cancellationToken: cancellationToken);
                return;
            }

            var builder = new StringBuilder($"👥 *All Users ({users.Count}):*\n\n");
            var now = DateTime.UtcNow;

            for (var i = 0; i < users.Count && i < 50; i++)
            {
                var user = users[i];
                var subscriptionUtc = DateTime.SpecifyKind(user.Subscription, DateTimeKind.Utc);
                var status = subscriptionUtc > now
                    ? $"✅ Active ({(subscriptionUtc - now).Days}d left)"
                    : "❌ Expired";

                builder.AppendLine($"*{i + 1}.* User ID: `{user.Id}`")
                       .AppendLine($"   💻 HWID: `{user.Hwid}`")
                       .AppendLine($"   📅 Expires: {user.Subscription:yyyy-MM-dd HH:mm:ss}")
                       .AppendLine($"   📊 Status: {status}")
                       .AppendLine($"   🔒 Banned: {(user.Banned ? "Yes" : "No")}\n");
            }

            if (users.Count > 50)
            {
                builder.AppendLine($"_... and {users.Count - 50} more. Use filters to narrow down._");
            }

            await botClient.SendTextMessageAsync(chatId, builder.ToString(), ParseMode.Markdown, replyMarkup: BuildBackButton(), cancellationToken: cancellationToken);
            await LogActionAsync(chatId, "view_all_users", null, null, $"Viewed {users.Count} users", cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load all users");
            await botClient.SendTextMessageAsync(chatId, $"❌ Error: {ex.Message}", cancellationToken: cancellationToken);
        }
    }

    private async Task HandleAllKeysAsync(ITelegramBotClient botClient, long chatId, CancellationToken cancellationToken)
    {
        try
        {
            var keys = await _repository.GetAllKeysWithOwnersAsync(100, cancellationToken);

            if (keys.Count == 0)
            {
                await botClient.SendTextMessageAsync(chatId, "❌ No subscription keys found in the database.", cancellationToken: cancellationToken);
                return;
            }

            var builder = new StringBuilder($"🔑 *All Subscription Keys ({keys.Count}):*\n\n");
            var now = DateTime.UtcNow;

            for (var i = 0; i < keys.Count && i < 50; i++)
            {
                var key = keys[i];
                string validityText;

                if (key.ExpiresAt is null)
                {
                    validityText = "Never expires (not activated yet)";
                }
                else if (key.ExpiresAt > now)
                {
                    var remaining = key.ExpiresAt.Value - now;
                    var remainingDays = (int)Math.Ceiling(remaining.TotalDays);
                    validityText = $"✅ Valid ({remainingDays} days left, expires {key.ExpiresAt:yyyy-MM-dd HH:mm} UTC)";
                }
                else
                {
                    validityText = $"❌ Expired on {key.ExpiresAt:yyyy-MM-dd HH:mm} UTC";
                }

                builder.AppendLine($"*{i + 1}.* `{key.SubscriptionKey}`")
                       .AppendLine($"   👤 Owner ID: `{key.UserId}`")
                       .AppendLine($"   📅 Duration: {key.Days} day(s)")
                       .AppendLine($"   ⏰ Status: {validityText}\n");
            }

            if (keys.Count > 50)
            {
                builder.AppendLine($"_... and {keys.Count - 50} more keys._");
            }

            await botClient.SendTextMessageAsync(chatId, builder.ToString(), ParseMode.Markdown, replyMarkup: BuildBackButton(), cancellationToken: cancellationToken);
            await LogActionAsync(chatId, "view_all_keys", null, null, $"Viewed {keys.Count} keys", cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load all keys");
            await botClient.SendTextMessageAsync(chatId, $"❌ Error: {ex.Message}", cancellationToken: cancellationToken);
        }
    }

    private async Task HandleAddDaysToAllAsync(ITelegramBotClient botClient, long chatId, int days, CancellationToken cancellationToken)
    {
        try
        {
            var confirmMessage = $"⚠️ *CONFIRMATION REQUIRED*\n\n" +
                               $"You are about to add *{days} day(s)* to ALL users' subscriptions.\n\n" +
                               $"This action will affect ALL users in the database.\n\n" +
                               $"Are you sure you want to proceed?";

            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("✅ Yes, Add Days", $"confirm_adddaysall_{days}"),
                    InlineKeyboardButton.WithCallbackData("❌ Cancel", "cancel_adddaysall")
                }
            });

            await botClient.SendTextMessageAsync(
                chatId,
                confirmMessage,
                ParseMode.Markdown,
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to prompt for add days to all confirmation");
            await botClient.SendTextMessageAsync(chatId, $"❌ Error: {ex.Message}", cancellationToken: cancellationToken);
        }
    }

    private async Task HandleConfirmAddDaysToAllAsync(ITelegramBotClient botClient, CallbackQuery query, CancellationToken cancellationToken)
    {
        if (query.Data is null || query.Message is null)
        {
            return;
        }

        var daysStr = query.Data.Replace("confirm_adddaysall_", "");
        if (!int.TryParse(daysStr, out var days))
        {
            await botClient.AnswerCallbackQueryAsync(query.Id, "Invalid data.", showAlert: true, cancellationToken: cancellationToken);
            return;
        }

        try
        {
            await botClient.EditMessageTextAsync(
                query.Message.Chat.Id,
                query.Message.MessageId,
                "⏳ Processing... Adding days to all users...",
                cancellationToken: cancellationToken);

            var affectedCount = await _repository.AddDaysToAllUsersAsync(days, cancellationToken);

            var resultMessage = $"✅ *Success!*\n\n" +
                              $"Added *{days} day(s)* to *{affectedCount} user(s)*.\n\n" +
                              $"All subscriptions have been extended.";

            await botClient.EditMessageTextAsync(
                query.Message.Chat.Id,
                query.Message.MessageId,
                resultMessage,
                ParseMode.Markdown,
                cancellationToken: cancellationToken);

            await botClient.AnswerCallbackQueryAsync(query.Id, $"Added {days} days to {affectedCount} users!", cancellationToken: cancellationToken);

            await LogActionAsync(query.From.Id, "add_days_all", "users", "all",
                $"Added {days} days to {affectedCount} users", cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add days to all users");
            await botClient.EditMessageTextAsync(
                query.Message.Chat.Id,
                query.Message.MessageId,
                $"❌ Error: {ex.Message}",
                cancellationToken: cancellationToken);
            await botClient.AnswerCallbackQueryAsync(query.Id, "Failed to add days.", showAlert: true, cancellationToken: cancellationToken);
        }
    }

    private async Task HandleUploadRequestAsync(ITelegramBotClient botClient, long chatId, CancellationToken cancellationToken)
    {
        _sessionStore.SetPendingAction(chatId, "upload_version");
        await botClient.SendTextMessageAsync(
            chatId,
            "📦 *Upload New Version*\n\n" +
            "⚠️ *Important:* Upload the file as a *.7z archive WITHOUT password*\n" +
            "The system will automatically generate unique passwords for each user.\n\n" +
            "Please enter the version number (e.g., 1.0.0, 2.5.1):",
            ParseMode.Markdown,
            cancellationToken: cancellationToken);
    }

    private async Task HandleDocumentUploadAsync(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        if (!_sessionStore.TryGetPendingAction(message.Chat.Id, out var action) || action is null)
        {
            _logger.LogWarning("Document upload attempted without pending action for chat {ChatId}", message.Chat.Id);
            await botClient.SendTextMessageAsync(
                message.Chat.Id,
                "❌ Please use /upload command first to start the upload process.",
                cancellationToken: cancellationToken);
            return;
        }

        _logger.LogInformation("Processing document upload with action: {Action}", action);

        // Handle file upload for both upload_version:X and upload_file:X:Y states
        string version;
        string? changelog = null;

        if (action.StartsWith("upload_version:"))
        {
            // User sent file after entering version, skip changelog
            version = action.Replace("upload_version:", "");
            changelog = null;
        }
        else if (action.StartsWith("upload_file:"))
        {
            var parts = action.Split(':', 3);
            if (parts.Length < 3)
            {
                await botClient.SendTextMessageAsync(
                    message.Chat.Id,
                    "❌ Invalid upload state. Please start over with /upload.",
                    cancellationToken: cancellationToken);
                _sessionStore.ClearPendingAction(message.Chat.Id);
                return;
            }
            version = parts[1];
            changelog = parts[2] == "NONE" ? null : parts[2];
        }
        else
        {
            await botClient.SendTextMessageAsync(
                message.Chat.Id,
                "❌ Please use /upload command first to start the upload process.",
                cancellationToken: cancellationToken);
            return;
        }

        var document = message.Document!;

        try
        {
            await botClient.SendTextMessageAsync(
                message.Chat.Id,
                "⏳ Processing upload...",
                cancellationToken: cancellationToken);

            // Download file to server
            var uploadsDir = Path.Combine(Directory.GetCurrentDirectory(), "uploads");
            Directory.CreateDirectory(uploadsDir);

            var fileExtension = Path.GetExtension(document.FileName ?? ".7z");
            var localFileName = $"{version}_{Guid.NewGuid()}{fileExtension}";
            var localFilePath = Path.Combine(uploadsDir, localFileName);

            var file = await botClient.GetFileAsync(document.FileId, cancellationToken);
            await using (var fileStream = System.IO.File.Create(localFilePath))
            {
                await botClient.DownloadFileAsync(file.FilePath!, fileStream, cancellationToken);
            }

            _logger.LogInformation("Downloaded file to {FilePath}", localFilePath);

            // Save version to database with local file path
            var versionId = await _repository.InsertProductVersionAsync(
                version,
                localFilePath, // Save local path instead of file_id
                document.FileName ?? "update.7z",
                document.FileSize ?? 0,
                changelog,
                message.Chat.Id,
                cancellationToken);

            // Get active users
            var activeUserIds = await _repository.GetActiveUserIdsAsync(cancellationToken);

            // Send notifications to all active users
            var notificationTasks = new List<Task>();
            foreach (var userId in activeUserIds)
            {
                notificationTasks.Add(SendUpdateNotificationToUserAsync(botClient, userId, version, changelog, versionId, cancellationToken));
            }

            await Task.WhenAll(notificationTasks);

            var successMessage = $"✅ *Version {version} uploaded successfully!*\n\n" +
                               $"📄 File: `{document.FileName}`\n" +
                               $"📦 Size: {FormatFileSize(document.FileSize ?? 0)}\n" +
                               $"👥 Notified: {activeUserIds.Count} active user(s)";

            await botClient.SendTextMessageAsync(
                message.Chat.Id,
                successMessage,
                ParseMode.Markdown,
                replyMarkup: BuildBackButton(),
                cancellationToken: cancellationToken);

            await LogActionAsync(message.Chat.Id, "upload_version", "version", version,
                $"Uploaded {document.FileName}, notified {activeUserIds.Count} users", cancellationToken);

            _sessionStore.ClearPendingAction(message.Chat.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process file upload");
            await botClient.SendTextMessageAsync(
                message.Chat.Id,
                $"❌ Error: {ex.Message}",
                cancellationToken: cancellationToken);
        }
    }

    private async Task SendUpdateNotificationToUserAsync(ITelegramBotClient botClient, long userId, string version, string? changelog, long versionId, CancellationToken cancellationToken)
    {
        try
        {
            var message = $"🎉 *New Update Available!*\n\n" +
                         $"📦 *Version:* {version}\n\n";

            if (!string.IsNullOrEmpty(changelog))
            {
                message += $"📝 *What's New:*\n{changelog}\n\n";
            }

            message += "Click the button below to download the latest version!";

            // Create inline keyboard with Download Now button
            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("📥 Download Now", "download_update")
                }
            });

            // Send via UserBot so user can click the button
            await _userBotClient.SendTextMessageAsync(
                userId,
                message,
                ParseMode.Markdown,
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);

            await _repository.InsertUpdateNotificationAsync(versionId, userId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send update notification to user {UserId}", userId);
        }
    }

    private async Task HandleVersionHistoryAsync(ITelegramBotClient botClient, long chatId, CancellationToken cancellationToken)
    {
        try
        {
            var versions = await _repository.GetProductVersionHistoryAsync(10, cancellationToken);

            if (versions.Count == 0)
            {
                await botClient.SendTextMessageAsync(
                    chatId,
                    "❌ No versions found.",
                    replyMarkup: BuildBackButton(),
                    cancellationToken: cancellationToken);
                return;
            }

            var builder = new StringBuilder($"📚 *Version History ({versions.Count}):*\n\n");

            foreach (var ver in versions)
            {
                var badge = ver.IsLatest ? "🟢 LATEST" : "⚪";
                builder.AppendLine($"{badge} *Version {ver.Version}*")
                       .AppendLine($"   📄 File: `{ver.FileName}`")
                       .AppendLine($"   📦 Size: {FormatFileSize(ver.FileSize)}")
                       .AppendLine($"   📅 Uploaded: {ver.CreatedAt:yyyy-MM-dd HH:mm}");

                if (!string.IsNullOrEmpty(ver.UpdateLog))
                {
                    var shortLog = ver.UpdateLog.Length > 100
                        ? ver.UpdateLog.Substring(0, 100) + "..."
                        : ver.UpdateLog;
                    builder.AppendLine($"   📝 Log: {shortLog}");
                }

                builder.AppendLine();
            }

            await botClient.SendTextMessageAsync(
                chatId,
                builder.ToString(),
                ParseMode.Markdown,
                cancellationToken: cancellationToken);

            await LogActionAsync(chatId, "view_versions", null, null, $"Viewed {versions.Count} versions", cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load version history");
            await botClient.SendTextMessageAsync(chatId, $"❌ Error: {ex.Message}", cancellationToken: cancellationToken);
        }
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

    private async Task HandlePaymentApproveAsync(ITelegramBotClient botClient, CallbackQuery query, CancellationToken cancellationToken)
    {
        await botClient.AnswerCallbackQueryAsync(query.Id, "⏳ Processing...", cancellationToken: cancellationToken);

        // Parse: payment_approve_{requestId}
        var requestIdStr = query.Data!["payment_approve_".Length..];
        if (!int.TryParse(requestIdStr, out var requestId))
        {
            await botClient.EditMessageTextAsync(
                query.Message!.Chat.Id,
                query.Message.MessageId,
                "❌ Invalid request ID.",
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

            if (paymentRequest.Status != "pending")
            {
                await botClient.EditMessageTextAsync(
                    query.Message!.Chat.Id,
                    query.Message.MessageId,
                    $"❌ Payment request already {paymentRequest.Status}.",
                    cancellationToken: cancellationToken);
                return;
            }

            // Approve payment
            var approved = await _repository.ApprovePaymentRequestAsync(requestId, query.From.Id, cancellationToken);
            if (!approved)
            {
                await botClient.EditMessageTextAsync(
                    query.Message!.Chat.Id,
                    query.Message.MessageId,
                    "❌ Failed to approve payment.",
                    cancellationToken: cancellationToken);
                return;
            }

            // Create subscription key
            var key = SubscriptionKeyGenerator.Generate();
            await _repository.InsertSubscriptionKeyAsync(paymentRequest.UserId, key, paymentRequest.Days, expiresAt: null, cancellationToken);

            // Update admin message
            await botClient.EditMessageTextAsync(
                query.Message!.Chat.Id,
                query.Message.MessageId,
                $"""
                ✅ *Payment Approved*

                👤 User ID: `{paymentRequest.UserId}`
                📦 Product: {paymentRequest.ProductName}
                💵 Amount: ${paymentRequest.Amount:F2}
                🔑 Key: `{key}`

                ✅ Approved by: {query.From.FirstName}
                ⏰ Time: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC
                """,
                ParseMode.Markdown,
                cancellationToken: cancellationToken);

            // Notify user
            await NotifyUserAboutApprovalAsync(paymentRequest.UserId, key, paymentRequest.Days, paymentRequest.ProductName, cancellationToken);

            await LogActionAsync(query.From.Id, "payment_approve", "payment_request", requestId.ToString(),
                $"Approved payment for user {paymentRequest.UserId}, issued key {key}", cancellationToken);

            _logger.LogInformation("Admin {AdminId} approved payment request {RequestId} for user {UserId}",
                query.From.Id, requestId, paymentRequest.UserId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error approving payment");
            await botClient.EditMessageTextAsync(
                query.Message!.Chat.Id,
                query.Message.MessageId,
                $"❌ Error: {ex.Message}",
                cancellationToken: cancellationToken);
        }
    }

    private async Task HandlePaymentRejectAsync(ITelegramBotClient botClient, CallbackQuery query, CancellationToken cancellationToken)
    {
        await botClient.AnswerCallbackQueryAsync(query.Id, "⏳ Processing...", cancellationToken: cancellationToken);

        // Parse: payment_reject_{requestId}
        var requestIdStr = query.Data!["payment_reject_".Length..];
        if (!int.TryParse(requestIdStr, out var requestId))
        {
            await botClient.EditMessageTextAsync(
                query.Message!.Chat.Id,
                query.Message.MessageId,
                "❌ Invalid request ID.",
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

            if (paymentRequest.Status != "pending")
            {
                await botClient.EditMessageTextAsync(
                    query.Message!.Chat.Id,
                    query.Message.MessageId,
                    $"❌ Payment request already {paymentRequest.Status}.",
                    cancellationToken: cancellationToken);
                return;
            }

            // Reject payment
            var rejected = await _repository.RejectPaymentRequestAsync(requestId, query.From.Id, cancellationToken);
            if (!rejected)
            {
                await botClient.EditMessageTextAsync(
                    query.Message!.Chat.Id,
                    query.Message.MessageId,
                    "❌ Failed to reject payment.",
                    cancellationToken: cancellationToken);
                return;
            }

            // Update admin message
            await botClient.EditMessageTextAsync(
                query.Message!.Chat.Id,
                query.Message.MessageId,
                $"""
                ❌ *Payment Rejected*

                👤 User ID: `{paymentRequest.UserId}`
                📦 Product: {paymentRequest.ProductName}
                💵 Amount: ${paymentRequest.Amount:F2}

                ❌ Rejected by: {query.From.FirstName}
                ⏰ Time: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC
                """,
                ParseMode.Markdown,
                cancellationToken: cancellationToken);

            // Notify user
            await NotifyUserAboutRejectionAsync(paymentRequest.UserId, paymentRequest.ProductName, cancellationToken);

            await LogActionAsync(query.From.Id, "payment_reject", "payment_request", requestId.ToString(),
                $"Rejected payment for user {paymentRequest.UserId}", cancellationToken);

            _logger.LogInformation("Admin {AdminId} rejected payment request {RequestId} for user {UserId}",
                query.From.Id, requestId, paymentRequest.UserId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error rejecting payment");
            await botClient.EditMessageTextAsync(
                query.Message!.Chat.Id,
                query.Message.MessageId,
                $"❌ Error: {ex.Message}",
                cancellationToken: cancellationToken);
        }
    }

    private async Task NotifyUserAboutApprovalAsync(long userId, string key, int days, string productName, CancellationToken cancellationToken)
    {
        try
        {
            var durationText = days >= 99999 ? "Lifetime (Forever)" : $"{days} days";

            var message = $"""
                🎉 *Payment Approved!*

                Your payment has been verified and approved!

                📦 **Product:** {productName}
                📅 **Duration:** {durationText}
                🔑 **Subscription Key:** `{key}`
                👤 **Your Chat ID:** `{userId}`

                *How to use:*
                1. Enter this key in your application together with your HWID.
                2. The key can be used only once.
                3. The key is deleted immediately after successful activation.

                Thank you for your purchase! 💫
                """;

            // Send to user
            await _userBotClient.SendTextMessageAsync(
                userId,
                message,
                ParseMode.Markdown,
                cancellationToken: cancellationToken);

            // Duplicate to admin group
            if (_commonOptions.AdminGroupId.HasValue)
            {
                var groupMessage = $"""
                    ✅ *Payment Approved - User Notified*

                    👤 **User ID:** `{userId}`
                    📦 **Product:** {productName}
                    📅 **Duration:** {durationText}
                    🔑 **Key:** `{key}`
                    """;

                try
                {
                    await _userBotClient.SendTextMessageAsync(
                        _commonOptions.AdminGroupId.Value,
                        groupMessage,
                        ParseMode.Markdown,
                        cancellationToken: cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send approval notification to admin group");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to notify user {UserId} about payment approval", userId);
        }
    }

    private async Task NotifyUserAboutRejectionAsync(long userId, string productName, CancellationToken cancellationToken)
    {
        try
        {
            var message = $"""
                ❌ *Payment Not Verified*

                Unfortunately, we couldn't verify your payment for {productName}.

                Please contact our support team for assistance:
                {_commonOptions.SupportContact}

                If you believe this is an error, please provide your payment proof to support.
                """;

            // Send to user
            await _userBotClient.SendTextMessageAsync(
                userId,
                message,
                ParseMode.Markdown,
                cancellationToken: cancellationToken);

            // Duplicate to admin group
            if (_commonOptions.AdminGroupId.HasValue)
            {
                var groupMessage = $"""
                    ❌ *Payment Rejected - User Notified*

                    👤 **User ID:** `{userId}`
                    📦 **Product:** {productName}

                    User has been notified about rejection.
                    """;

                try
                {
                    await _userBotClient.SendTextMessageAsync(
                        _commonOptions.AdminGroupId.Value,
                        groupMessage,
                        ParseMode.Markdown,
                        cancellationToken: cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send rejection notification to admin group");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to notify user {UserId} about payment rejection", userId);
        }
    }
}
