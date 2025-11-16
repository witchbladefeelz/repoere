using HwidBots.Shared.Options;
using Microsoft.Extensions.Logging;

namespace HwidBots.Shared.Services;

public class DatabaseService
{
    private readonly string _connectionString;
    private readonly ILogger<DatabaseService>? _logger;

    public DatabaseService(BotDatabaseOptions options, ILogger<DatabaseService>? logger = null)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        _connectionString = options.BuildConnectionString();
        _logger = logger;
    }

    private MySqlConnection CreateConnection() => new(_connectionString);

    public async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        var command = new CommandDefinition(sql, parameters, cancellationToken: cancellationToken);
        var result = await connection.QueryAsync<T>(command);
        return result.AsList();
    }

    public async Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        var command = new CommandDefinition(sql, parameters, cancellationToken: cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<T>(command);
    }

    public async Task<int> ExecuteAsync(string sql, object? parameters = null, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        var command = new CommandDefinition(sql, parameters, cancellationToken: cancellationToken);
        return await connection.ExecuteAsync(command);
    }

    public async Task<T> ExecuteScalarAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        var command = new CommandDefinition(sql, parameters, cancellationToken: cancellationToken);
        return await connection.ExecuteScalarAsync<T>(command);
    }

    public async Task<bool> CheckConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await connection.CloseAsync();
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to open database connection");
            return false;
        }
    }
}

