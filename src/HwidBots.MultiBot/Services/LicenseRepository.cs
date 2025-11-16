using HwidBots.Shared.Models;

namespace HwidBots.Shared.Services;

public class LicenseRepository : ILicenseRepository
{
    private readonly DatabaseService _database;

    public LicenseRepository(DatabaseService database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task InsertSubscriptionKeyAsync(long userId, string key, int days, DateTime? expiresAt = null, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO subscriptions (id, subscription_key, days, expires_at)
            VALUES (@UserId, @Key, @Days, @ExpiresAt)
            """;

        await _database.ExecuteAsync(sql, new { UserId = userId, Key = key, Days = days, ExpiresAt = expiresAt }, cancellationToken);
    }

    public Task UpsertSubscriptionKeyAsync(long userId, string key, int days, DateTime? expiresAt = null, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO subscriptions (id, subscription_key, days, expires_at)
            VALUES (@UserId, @Key, @Days, @ExpiresAt)
            ON DUPLICATE KEY UPDATE
                id = VALUES(id),
                days = VALUES(days),
                expires_at = VALUES(expires_at)
            """;

        return _database.ExecuteAsync(sql, new { UserId = userId, Key = key, Days = days, ExpiresAt = expiresAt }, cancellationToken);
    }

    public Task<IReadOnlyList<SubscriptionKeyRecord>> GetActiveKeysAsync(long userId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT subscription_key AS SubscriptionKey,
                   days AS Days,
                   expires_at AS ExpiresAt
            FROM subscriptions
            WHERE id = @UserId
              AND (expires_at IS NULL OR expires_at > NOW())
            ORDER BY subscription_key
            """;

        return _database.QueryAsync<SubscriptionKeyRecord>(sql, new { UserId = userId }, cancellationToken);
    }

    public async Task<SubscriptionKeyStats> GetSubscriptionKeyStatsAsync(long userId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                COUNT(*) AS TotalKeys,
                CAST(COALESCE(SUM(days), 0) AS SIGNED) AS TotalDays
            FROM subscriptions
            WHERE id = @UserId
            """;

        var stats = await _database.QuerySingleOrDefaultAsync<SubscriptionKeyStats>(sql, new { UserId = userId }, cancellationToken);
        return stats ?? new SubscriptionKeyStats(0, 0);
    }

    public Task<IReadOnlyList<UserLicenseRecord>> GetLicensesAsync(long userId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id AS Id,
                   hwid AS Hwid,
                   subscription AS Subscription,
                   banned AS Banned,
                   last_key AS LastKey
            FROM users
            WHERE id = @UserId
            ORDER BY subscription DESC
            """;

        return _database.QueryAsync<UserLicenseRecord>(sql, new { UserId = userId }, cancellationToken);
    }

    public Task<UserLicenseRecord?> GetLicenseAsync(long userId, string hwid, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id AS Id,
                   hwid AS Hwid,
                   subscription AS Subscription,
                   banned AS Banned,
                   last_key AS LastKey
            FROM users
            WHERE id = @UserId AND hwid = @Hwid
            LIMIT 1
            """;

        return _database.QuerySingleOrDefaultAsync<UserLicenseRecord>(sql, new { UserId = userId, Hwid = hwid }, cancellationToken);
    }

    public async Task<bool> HasPendingResetRequestAsync(long userId, string hwid, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT 1
            FROM hwid_reset_requests
            WHERE user_id = @UserId
              AND hwid = @Hwid
              AND status = 'pending'
            LIMIT 1
            """;

        var result = await _database.QuerySingleOrDefaultAsync<int?>(sql, new { UserId = userId, Hwid = hwid }, cancellationToken);
        return result.HasValue;
    }

    public async Task<HwidResetRequest> InsertResetRequestAsync(long userId, string hwid, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO hwid_reset_requests (user_id, hwid, status)
            VALUES (@UserId, @Hwid, 'pending')
            """;

        await _database.ExecuteAsync(sql, new { UserId = userId, Hwid = hwid }, cancellationToken);

        const string selectSql = """
            SELECT
                id AS Id,
                user_id AS UserId,
                hwid AS Hwid,
                status AS Status,
                created_at AS CreatedAt
            FROM hwid_reset_requests
            WHERE user_id = @UserId
              AND hwid = @Hwid
            ORDER BY id DESC
            LIMIT 1
            """;

        var request = await _database.QuerySingleOrDefaultAsync<HwidResetRequest>(selectSql, new { UserId = userId, Hwid = hwid }, cancellationToken);
        return request ?? throw new InvalidOperationException("Failed to load reset request after insert.");
    }

    public Task<HwidResetRequest?> GetResetRequestByIdAsync(int requestId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                id AS Id,
                user_id AS UserId,
                hwid AS Hwid,
                status AS Status,
                created_at AS CreatedAt
            FROM hwid_reset_requests
            WHERE id = @RequestId
            LIMIT 1
            """;

        return _database.QuerySingleOrDefaultAsync<HwidResetRequest>(sql, new { RequestId = requestId }, cancellationToken);
    }

    public Task<IReadOnlyList<HwidResetRequest>> GetPendingResetRequestsAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                id AS Id,
                user_id AS UserId,
                hwid AS Hwid,
                status AS Status,
                created_at AS CreatedAt
            FROM hwid_reset_requests
            WHERE status = 'pending'
            ORDER BY created_at ASC
            """;

        return _database.QueryAsync<HwidResetRequest>(sql, cancellationToken: cancellationToken);
    }

    public async Task<bool> UpdateResetRequestStatusAsync(int requestId, string expectedStatus, string newStatus, CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE hwid_reset_requests
            SET status = @NewStatus
            WHERE id = @RequestId
              AND status = @ExpectedStatus
            """;

        var affected = await _database.ExecuteAsync(sql, new { RequestId = requestId, ExpectedStatus = expectedStatus, NewStatus = newStatus }, cancellationToken);
        return affected > 0;
    }

    public async Task<SystemStats> GetSystemStatsAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                COUNT(*) AS TotalUsers,
                COUNT(DISTINCT id) AS UniqueUsers,
                COALESCE(SUM(CASE WHEN subscription > NOW() THEN 1 ELSE 0 END), 0) AS ActiveSubscriptions,
                COALESCE(SUM(CASE WHEN banned = 1 THEN 1 ELSE 0 END), 0) AS BannedUsers
            FROM users
            """;

        var stats = await _database.QuerySingleOrDefaultAsync<SystemStats>(sql, cancellationToken: cancellationToken);
        return stats ?? new SystemStats(0, 0, 0, 0);
    }

    public async Task<KeyStats> GetKeyStatsAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                COUNT(*) AS TotalKeys,
                CAST(COALESCE(SUM(days), 0) AS SIGNED) AS TotalDaysSold,
                COUNT(DISTINCT id) AS UniqueKeyOwners
            FROM subscriptions
            """;

        var stats = await _database.QuerySingleOrDefaultAsync<KeyStats>(sql, cancellationToken: cancellationToken);
        return stats ?? new KeyStats(0, 0, 0);
    }

    public async Task<bool> BanUserByUserIdAsync(long userId, CancellationToken cancellationToken = default)
    {
        const string sql = "UPDATE users SET banned = 1 WHERE id = @UserId";
        var affected = await _database.ExecuteAsync(sql, new { UserId = userId }, cancellationToken);
        return affected > 0;
    }

    public async Task<bool> BanUserByHwidAsync(string hwid, CancellationToken cancellationToken = default)
    {
        const string sql = "UPDATE users SET banned = 1 WHERE hwid = @Hwid";
        var affected = await _database.ExecuteAsync(sql, new { Hwid = hwid }, cancellationToken);
        return affected > 0;
    }

    public async Task<bool> UnbanUserByUserIdAsync(long userId, CancellationToken cancellationToken = default)
    {
        const string sql = "UPDATE users SET banned = 0 WHERE id = @UserId";
        var affected = await _database.ExecuteAsync(sql, new { UserId = userId }, cancellationToken);
        return affected > 0;
    }

    public async Task<bool> UnbanUserByHwidAsync(string hwid, CancellationToken cancellationToken = default)
    {
        const string sql = "UPDATE users SET banned = 0 WHERE hwid = @Hwid";
        var affected = await _database.ExecuteAsync(sql, new { Hwid = hwid }, cancellationToken);
        return affected > 0;
    }

    public async Task<bool> AddDaysByUserIdAsync(long userId, int days, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO users (id, hwid, subscription, banned)
            VALUES (@UserId, NULL, DATE_ADD(NOW(), INTERVAL @Days DAY), 0)
            ON DUPLICATE KEY UPDATE
                subscription = DATE_ADD(IF(subscription > NOW(), subscription, NOW()), INTERVAL @Days DAY)
            """;

        var affected = await _database.ExecuteAsync(sql, new { Days = days, UserId = userId }, cancellationToken);
        return affected > 0;
    }

    public async Task<bool> AddDaysByHwidAsync(string hwid, int days, CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE users
            SET subscription = DATE_ADD(IF(subscription > NOW(), subscription, NOW()), INTERVAL @Days DAY)
            WHERE hwid = @Hwid
            """;

        var affected = await _database.ExecuteAsync(sql, new { Days = days, Hwid = hwid }, cancellationToken);
        return affected > 0;
    }

    public async Task<UserLicenseRecord?> DeleteLicenseAsync(string hwid, CancellationToken cancellationToken = default)
    {
        const string lookupSql = """
            SELECT id AS Id,
                   hwid AS Hwid,
                   subscription AS Subscription,
                   banned AS Banned,
                   last_key AS LastKey
            FROM users
            WHERE hwid = @Hwid
            LIMIT 1
            """;

        var record = await _database.QuerySingleOrDefaultAsync<UserLicenseRecord>(lookupSql, new { Hwid = hwid }, cancellationToken);
        if (record is null)
        {
            return null;
        }

        const string deleteSql = "DELETE FROM users WHERE hwid = @Hwid";
        await _database.ExecuteAsync(deleteSql, new { Hwid = hwid }, cancellationToken);
        return record;
    }

    public Task<IReadOnlyList<UserSummary>> GetUserSummariesAsync(int limit, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                id AS Id,
                CAST(COUNT(*) AS SIGNED) AS HwidCount,
                MAX(subscription) AS LatestSubscription,
                CAST(COALESCE(SUM(CASE WHEN banned = 1 THEN 1 ELSE 0 END), 0) AS SIGNED) AS BannedCount
            FROM users
            GROUP BY id
            ORDER BY LatestSubscription DESC
            LIMIT @Limit
            """;

        return _database.QueryAsync<UserSummary>(sql, new { Limit = limit }, cancellationToken);
    }

    public Task<IReadOnlyList<KeyHolderSummary>> GetKeyHolderSummariesAsync(int limit, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                s.id AS Id,
                CAST(COUNT(*) AS SIGNED) AS KeyCount,
                CAST(COALESCE(SUM(s.days), 0) AS SIGNED) AS TotalDays,
                MAX(s.subscription_key) AS SampleKey
            FROM subscriptions s
            LEFT JOIN users u ON s.id = u.id
            WHERE u.id IS NULL
            GROUP BY s.id
            ORDER BY s.id DESC
            LIMIT @Limit
            """;

        return _database.QueryAsync<KeyHolderSummary>(sql, new { Limit = limit }, cancellationToken);
    }

    public Task<IReadOnlyList<UserLicenseRecord>> GetLicensesByHwidAsync(string hwid, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id AS Id,
                   hwid AS Hwid,
                   subscription AS Subscription,
                   banned AS Banned,
                   last_key AS LastKey
            FROM users
            WHERE hwid = @Hwid
            """;

        return _database.QueryAsync<UserLicenseRecord>(sql, new { Hwid = hwid }, cancellationToken);
    }

    public async Task LogAdminActionAsync(long adminId, string actionType, string? targetType, string? targetId, string? details, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO admin_action_logs (admin_id, action_type, target_type, target_id, details)
            VALUES (@AdminId, @ActionType, @TargetType, @TargetId, @Details)
            """;

        await _database.ExecuteAsync(sql, new
        {
            AdminId = adminId,
            ActionType = actionType,
            TargetType = targetType,
            TargetId = targetId,
            Details = details
        }, cancellationToken);
    }

    public Task<IReadOnlyList<AdminActionLog>> GetAdminActionLogsAsync(long? adminId = null, string? actionType = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        var conditions = new List<string> { "1=1" };
        var parameters = new Dictionary<string, object?> { { "Limit", limit } };

        if (adminId.HasValue)
        {
            conditions.Add("admin_id = @AdminId");
            parameters["AdminId"] = adminId.Value;
        }

        if (!string.IsNullOrWhiteSpace(actionType))
        {
            conditions.Add("action_type = @ActionType");
            parameters["ActionType"] = actionType;
        }

        var whereClause = string.Join(" AND ", conditions);
        var sql = $"""
            SELECT
                id AS Id,
                admin_id AS AdminId,
                action_type AS ActionType,
                target_type AS TargetType,
                target_id AS TargetId,
                details AS Details,
                created_at AS CreatedAt
            FROM admin_action_logs
            WHERE {whereClause}
            ORDER BY created_at DESC
            LIMIT @Limit
            """;

        return _database.QueryAsync<AdminActionLog>(sql, parameters, cancellationToken);
    }

    public async Task<bool> DeleteSubscriptionKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        const string sql = "DELETE FROM subscriptions WHERE subscription_key = @Key";
        var affected = await _database.ExecuteAsync(sql, new { Key = key }, cancellationToken);
        return affected > 0;
    }

    public Task<IReadOnlyList<SubscriptionKeyRecord>> GetAllKeysByUserIdAsync(long userId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT subscription_key AS SubscriptionKey,
                   days AS Days,
                   expires_at AS ExpiresAt
            FROM subscriptions
            WHERE id = @UserId
            ORDER BY subscription_key
            """;

        return _database.QueryAsync<SubscriptionKeyRecord>(sql, new { UserId = userId }, cancellationToken);
    }

    public Task<IReadOnlyList<UserLicenseRecord>> SearchUsersByHwidAsync(string hwidPattern, int limit = 50, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id AS Id,
                   hwid AS Hwid,
                   subscription AS Subscription,
                   banned AS Banned,
                   last_key AS LastKey
            FROM users
            WHERE hwid LIKE @Pattern
            ORDER BY subscription DESC
            LIMIT @Limit
            """;

        return _database.QueryAsync<UserLicenseRecord>(sql, new { Pattern = $"%{hwidPattern}%", Limit = limit }, cancellationToken);
    }

    public Task<IReadOnlyList<UserLicenseRecord>> GetExpiredLicensesAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id AS Id,
                   hwid AS Hwid,
                   subscription AS Subscription,
                   banned AS Banned,
                   last_key AS LastKey
            FROM users
            WHERE subscription <= NOW()
            ORDER BY subscription DESC
            LIMIT @Limit
            """;

        return _database.QueryAsync<UserLicenseRecord>(sql, new { Limit = limit }, cancellationToken);
    }

    public Task<IReadOnlyList<UserLicenseRecord>> GetBannedLicensesAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id AS Id,
                   hwid AS Hwid,
                   subscription AS Subscription,
                   banned AS Banned,
                   last_key AS LastKey
            FROM users
            WHERE banned = 1
            ORDER BY subscription DESC
            LIMIT @Limit
            """;

        return _database.QueryAsync<UserLicenseRecord>(sql, new { Limit = limit }, cancellationToken);
    }

    public Task<IReadOnlyList<SubscriptionKeyWithOwner>> GetAllKeysWithOwnersAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id AS UserId,
                   subscription_key AS SubscriptionKey,
                   days AS Days,
                   expires_at AS ExpiresAt
            FROM subscriptions
            ORDER BY expires_at DESC, subscription_key
            LIMIT @Limit
            """;

        return _database.QueryAsync<SubscriptionKeyWithOwner>(sql, new { Limit = limit }, cancellationToken);
    }

    public Task<IReadOnlyList<UserLicenseRecord>> GetAllUsersAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id AS Id,
                   hwid AS Hwid,
                   subscription AS Subscription,
                   banned AS Banned,
                   last_key AS LastKey
            FROM users
            ORDER BY subscription DESC
            LIMIT @Limit
            """;

        return _database.QueryAsync<UserLicenseRecord>(sql, new { Limit = limit }, cancellationToken);
    }

    public async Task<int> AddDaysToAllUsersAsync(int days, CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE users
            SET subscription = DATE_ADD(IF(subscription > NOW(), subscription, NOW()), INTERVAL @Days DAY)
            """;

        var affected = await _database.ExecuteAsync(sql, new { Days = days }, cancellationToken);
        return affected;
    }

    public async Task<long> InsertProductVersionAsync(string version, string fileId, string fileName, long fileSize, string? updateLog, long uploadedBy, CancellationToken cancellationToken = default)
    {
        const string updateOldVersions = """
            UPDATE product_versions
            SET is_latest = FALSE
            WHERE is_latest = TRUE
            """;

        const string insertVersion = """
            INSERT INTO product_versions (version, file_id, file_name, file_size, update_log, uploaded_by, is_latest)
            VALUES (@Version, @FileId, @FileName, @FileSize, @UpdateLog, @UploadedBy, TRUE)
            """;

        await _database.ExecuteAsync(updateOldVersions, cancellationToken: cancellationToken);
        await _database.ExecuteAsync(insertVersion, new
        {
            Version = version,
            FileId = fileId,
            FileName = fileName,
            FileSize = fileSize,
            UpdateLog = updateLog,
            UploadedBy = uploadedBy
        }, cancellationToken);

        const string getLastId = "SELECT LAST_INSERT_ID()";
        var versionId = await _database.ExecuteScalarAsync<long>(getLastId, cancellationToken: cancellationToken);
        return versionId;
    }

    public Task<ProductVersion?> GetLatestProductVersionAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id AS Id,
                   version AS Version,
                   file_id AS FileId,
                   file_name AS FileName,
                   file_size AS FileSize,
                   update_log AS UpdateLog,
                   uploaded_by AS UploadedBy,
                   created_at AS CreatedAt,
                   is_latest AS IsLatest
            FROM product_versions
            WHERE is_latest = TRUE
            LIMIT 1
            """;

        return _database.QuerySingleOrDefaultAsync<ProductVersion>(sql, cancellationToken: cancellationToken);
    }

    public Task<IReadOnlyList<ProductVersion>> GetProductVersionHistoryAsync(int limit = 10, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id AS Id,
                   version AS Version,
                   file_id AS FileId,
                   file_name AS FileName,
                   file_size AS FileSize,
                   update_log AS UpdateLog,
                   uploaded_by AS UploadedBy,
                   created_at AS CreatedAt,
                   is_latest AS IsLatest
            FROM product_versions
            ORDER BY created_at DESC
            LIMIT @Limit
            """;

        return _database.QueryAsync<ProductVersion>(sql, new { Limit = limit }, cancellationToken);
    }

    public async Task<bool> MarkVersionAsLatestAsync(long versionId, CancellationToken cancellationToken = default)
    {
        const string updateOld = "UPDATE product_versions SET is_latest = FALSE WHERE is_latest = TRUE";
        const string updateNew = "UPDATE product_versions SET is_latest = TRUE WHERE id = @VersionId";

        await _database.ExecuteAsync(updateOld, cancellationToken: cancellationToken);
        var affected = await _database.ExecuteAsync(updateNew, new { VersionId = versionId }, cancellationToken);
        return affected > 0;
    }

    public async Task InsertUpdateNotificationAsync(long versionId, long userId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO update_notifications (version_id, user_id)
            VALUES (@VersionId, @UserId)
            ON DUPLICATE KEY UPDATE version_id = @VersionId
            """;

        await _database.ExecuteAsync(sql, new { VersionId = versionId, UserId = userId }, cancellationToken);
    }

    public async Task<bool> MarkUpdateAsDownloadedAsync(long versionId, long userId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE update_notifications
            SET downloaded = TRUE, downloaded_at = NOW()
            WHERE version_id = @VersionId AND user_id = @UserId
            """;

        var affected = await _database.ExecuteAsync(sql, new { VersionId = versionId, UserId = userId }, cancellationToken);
        return affected > 0;
    }

    public Task<IReadOnlyList<long>> GetActiveUserIdsAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT DISTINCT u.id
            FROM users u
            LEFT JOIN subscriptions s ON u.id = s.id
            WHERE (u.subscription > NOW() OR (s.expires_at IS NULL OR s.expires_at > NOW()))
              AND u.banned = 0
            """;

        return _database.QueryAsync<long>(sql, cancellationToken: cancellationToken);
    }

    public async Task<bool> HasActiveSubscriptionOrKeysAsync(long userId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
              CASE
                WHEN EXISTS (
                  SELECT 1 FROM users u
                  WHERE u.id = @UserId AND u.banned = 1
                ) THEN 0
                WHEN EXISTS (
                  SELECT 1 FROM users u
                  WHERE u.id = @UserId AND u.subscription IS NOT NULL
                ) THEN 1
                WHEN EXISTS (
                  SELECT 1 FROM subscriptions s
                  WHERE s.id = @UserId
                ) THEN 1
                ELSE 0
              END
            """;

        var hasActive = await _database.ExecuteScalarAsync<bool>(sql, new { UserId = userId }, cancellationToken);
        return hasActive;
    }

    public async Task<bool> DeleteKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        const string sql = """
            DELETE FROM subscriptions
            WHERE subscription_key = @Key
            """;

        var affected = await _database.ExecuteAsync(sql, new { Key = key }, cancellationToken);
        return affected > 0;
    }

    public async Task<int> DeleteAllKeysByUserIdAsync(long userId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            DELETE FROM subscriptions
            WHERE id = @UserId
            """;

        var affected = await _database.ExecuteAsync(sql, new { UserId = userId }, cancellationToken);
        return affected;
    }

    public async Task<bool> ExtendSubscriptionKeyAsync(string key, int days, CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE subscriptions
            SET
                days = days + @Days,
                expires_at = CASE
                    WHEN expires_at IS NULL THEN DATE_ADD(NOW(), INTERVAL @Days DAY)
                    WHEN expires_at > NOW() THEN DATE_ADD(expires_at, INTERVAL @Days DAY)
                    ELSE DATE_ADD(NOW(), INTERVAL @Days DAY)
                END
            WHERE subscription_key = @Key
            """;

        var affected = await _database.ExecuteAsync(sql, new { Key = key, Days = days }, cancellationToken);
        return affected > 0;
    }

    public async Task<IReadOnlyList<UserSubscriptionRecord>> GetActiveSubscriptionsAsync(long userId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                id as UserId,
                hwid as Hwid,
                subscription as SubscriptionExpiry,
                CASE WHEN hwid IS NOT NULL THEN 1 ELSE 0 END as HasHwid,
                'users' as Source
            FROM users
            WHERE id = @UserId
              AND subscription IS NOT NULL
              AND subscription > NOW()
              AND banned = 0

            UNION

            SELECT
                id as UserId,
                NULL as Hwid,
                expires_at as SubscriptionExpiry,
                0 as HasHwid,
                'subscriptions' as Source
            FROM subscriptions
            WHERE id = @UserId
              AND expires_at IS NOT NULL
              AND expires_at > NOW()
            """;

        var result = await _database.QueryAsync<UserSubscriptionRecord>(sql, new { UserId = userId }, cancellationToken);
        return result.ToList();
    }

    public async Task<bool> ExtendSubscriptionByUserIdAsync(long userId, int days, CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE users
            SET subscription = DATE_ADD(IF(subscription > NOW(), subscription, NOW()), INTERVAL @Days DAY)
            WHERE id = @UserId
              AND subscription IS NOT NULL
            """;

        var affected = await _database.ExecuteAsync(sql, new { UserId = userId, Days = days }, cancellationToken);
        return affected > 0;
    }

    public async Task<bool> ExtendSubscriptionKeyByUserIdAsync(long userId, int days, CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE subscriptions
            SET
                days = days + @Days,
                expires_at = DATE_ADD(IF(expires_at > NOW(), expires_at, NOW()), INTERVAL @Days DAY)
            WHERE id = @UserId
              AND expires_at IS NOT NULL
            LIMIT 1
            """;

        var affected = await _database.ExecuteAsync(sql, new { UserId = userId, Days = days }, cancellationToken);
        return affected > 0;
    }

    public async Task<PaymentRequest> CreatePaymentRequestAsync(long userId, int days, decimal amount, string productName, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO payment_requests (user_id, days, amount, product_name, created_at, status)
            VALUES (@UserId, @Days, @Amount, @ProductName, NOW(), 'pending');
            SELECT LAST_INSERT_ID();
            """;

        var id = await _database.ExecuteScalarAsync<int>(sql, new { UserId = userId, Days = days, Amount = amount, ProductName = productName }, cancellationToken);

        return new PaymentRequest
        {
            Id = id,
            UserId = userId,
            Days = days,
            Amount = amount,
            ProductName = productName,
            CreatedAt = DateTime.UtcNow,
            Status = "pending"
        };
    }

    public async Task<PaymentRequest?> GetPaymentRequestAsync(int requestId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id as Id, user_id as UserId, days as Days, amount as Amount,
                   product_name as ProductName, created_at as CreatedAt, status as Status,
                   approved_by as ApprovedBy, processed_at as ProcessedAt
            FROM payment_requests
            WHERE id = @RequestId
            """;

        return await _database.QuerySingleOrDefaultAsync<PaymentRequest>(sql, new { RequestId = requestId }, cancellationToken);
    }

    public async Task<bool> ApprovePaymentRequestAsync(int requestId, long adminId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE payment_requests
            SET status = 'approved', approved_by = @AdminId, processed_at = NOW()
            WHERE id = @RequestId AND status = 'pending'
            """;

        var affected = await _database.ExecuteAsync(sql, new { RequestId = requestId, AdminId = adminId }, cancellationToken);
        return affected > 0;
    }

    public async Task<bool> RejectPaymentRequestAsync(int requestId, long adminId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE payment_requests
            SET status = 'rejected', approved_by = @AdminId, processed_at = NOW()
            WHERE id = @RequestId AND status = 'pending'
            """;

        var affected = await _database.ExecuteAsync(sql, new { RequestId = requestId, AdminId = adminId }, cancellationToken);
        return affected > 0;
    }

    public async Task<bool> TransferKeyAsync(string key, long fromUserId, long toUserId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE subscriptions
            SET id = @ToUserId
            WHERE subscription_key = @Key AND id = @FromUserId
            """;

        var affected = await _database.ExecuteAsync(sql, new { Key = key, FromUserId = fromUserId, ToUserId = toUserId }, cancellationToken);
        return affected > 0;
    }
}
