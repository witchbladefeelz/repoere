namespace HwidBots.Shared.Options;

public class BotDatabaseOptions
{
    public const string SectionName = "Database";

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 3306;
    public string Database { get; set; } = "syntara";
    public string User { get; set; } = "root";
    public string Password { get; set; } = string.Empty;
    public bool UseSsl { get; set; } = false;

    public string BuildConnectionString()
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = Host,
            Port = (uint)Port,
            Database = Database,
            UserID = User,
            Password = Password,
            SslMode = UseSsl ? MySqlSslMode.Required : MySqlSslMode.None,
            AllowUserVariables = true,
            AllowPublicKeyRetrieval = true
        };

        return builder.ConnectionString;
    }
}

