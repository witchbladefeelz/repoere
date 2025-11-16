using HwidBots.AdminBot.Options;
using HwidBots.AdminBot.Services;
using HwidBots.Shared.Options;
using HwidBots.Shared.Services;
using HwidBots.UserBot.Options;
using HwidBots.UserBot.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((context, config) =>
    {
        config.SetBasePath(AppContext.BaseDirectory);
        config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
        config.AddEnvironmentVariables("HWID_");
    })
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddConsole();
    })
    .ConfigureServices((context, services) =>
    {
        var configuration = context.Configuration;

        services.Configure<BotDatabaseOptions>(configuration.GetSection(BotDatabaseOptions.SectionName));
        services.Configure<BotCommonOptions>(configuration.GetSection(BotCommonOptions.SectionName));
        services.Configure<UserBotOptions>(configuration.GetSection(UserBotOptions.SectionName));
        services.Configure<AdminBotOptions>(configuration.GetSection(AdminBotOptions.SectionName));

        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<BotDatabaseOptions>>().Value;
            var logger = sp.GetRequiredService<ILogger<DatabaseService>>();
            return new DatabaseService(options, logger);
        });

        services.AddSingleton<ILicenseRepository, LicenseRepository>();

        services.AddHttpClient<CryptoRateService>();
        services.AddSingleton<CryptoRateService>();

        services.AddSingleton<UserSessionStore>();
        services.AddSingleton<UserBotUpdateHandler>();
        services.AddSingleton<AdminSessionStore>();
        services.AddSingleton<AdminBotUpdateHandler>();

        services.AddKeyedSingleton<ITelegramBotClient>("UserBot", (sp, _) =>
        {
            var options = sp.GetRequiredService<IOptions<UserBotOptions>>().Value;
            if (string.IsNullOrWhiteSpace(options.BotToken))
            {
                throw new InvalidOperationException(
                    "User bot token is not configured. Set UserBot:BotToken in appsettings.json or HWID_UserBot__BotToken environment variable.");
            }

            return new TelegramBotClient(options.BotToken);
        });

        services.AddKeyedSingleton<ITelegramBotClient>("AdminBot", (sp, _) =>
        {
            var options = sp.GetRequiredService<IOptions<AdminBotOptions>>().Value;
            if (string.IsNullOrWhiteSpace(options.BotToken))
            {
                throw new InvalidOperationException(
                    "Admin bot token is not configured. Set AdminBot:BotToken in appsettings.json or HWID_AdminBot__BotToken environment variable.");
            }

            return new TelegramBotClient(options.BotToken);
        });

        services.AddHostedService<UserBotHostedService>();
        services.AddHostedService<AdminBotHostedService>();
    })
    .Build();

await host.RunAsync();
