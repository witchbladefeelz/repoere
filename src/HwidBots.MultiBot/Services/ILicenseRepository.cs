using HwidBots.Shared.Models;

namespace HwidBots.Shared.Services;

public interface ILicenseRepository
{
    Task InsertSubscriptionKeyAsync(long userId, string key, int days, DateTime? expiresAt = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SubscriptionKeyRecord>> GetActiveKeysAsync(long userId, CancellationToken cancellationToken = default);
    Task<SubscriptionKeyStats> GetSubscriptionKeyStatsAsync(long userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<UserLicenseRecord>> GetLicensesAsync(long userId, CancellationToken cancellationToken = default);
    Task<UserLicenseRecord?> GetLicenseAsync(long userId, string hwid, CancellationToken cancellationToken = default);
    Task<bool> HasPendingResetRequestAsync(long userId, string hwid, CancellationToken cancellationToken = default);
    Task<HwidResetRequest> InsertResetRequestAsync(long userId, string hwid, CancellationToken cancellationToken = default);
    Task<HwidResetRequest?> GetResetRequestByIdAsync(int requestId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<HwidResetRequest>> GetPendingResetRequestsAsync(CancellationToken cancellationToken = default);
    Task<bool> UpdateResetRequestStatusAsync(int requestId, string expectedStatus, string newStatus, CancellationToken cancellationToken = default);
    Task UpsertSubscriptionKeyAsync(long userId, string key, int days, DateTime? expiresAt = null, CancellationToken cancellationToken = default);

    Task<SystemStats> GetSystemStatsAsync(CancellationToken cancellationToken = default);
    Task<KeyStats> GetKeyStatsAsync(CancellationToken cancellationToken = default);

    Task<bool> BanUserByUserIdAsync(long userId, CancellationToken cancellationToken = default);
    Task<bool> BanUserByHwidAsync(string hwid, CancellationToken cancellationToken = default);
    Task<bool> UnbanUserByUserIdAsync(long userId, CancellationToken cancellationToken = default);
    Task<bool> UnbanUserByHwidAsync(string hwid, CancellationToken cancellationToken = default);

    Task<bool> AddDaysByUserIdAsync(long userId, int days, CancellationToken cancellationToken = default);
    Task<bool> AddDaysByHwidAsync(string hwid, int days, CancellationToken cancellationToken = default);

    Task<bool> DeleteKeyAsync(string key, CancellationToken cancellationToken = default);
    Task<int> DeleteAllKeysByUserIdAsync(long userId, CancellationToken cancellationToken = default);
    Task<bool> ExtendSubscriptionKeyAsync(string key, int days, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<UserSubscriptionRecord>> GetActiveSubscriptionsAsync(long userId, CancellationToken cancellationToken = default);
    Task<bool> ExtendSubscriptionByUserIdAsync(long userId, int days, CancellationToken cancellationToken = default);
    Task<bool> ExtendSubscriptionKeyByUserIdAsync(long userId, int days, CancellationToken cancellationToken = default);

    // Payment requests
    Task<PaymentRequest> CreatePaymentRequestAsync(long userId, int days, decimal amount, string productName, CancellationToken cancellationToken = default);
    Task<PaymentRequest?> GetPaymentRequestAsync(int requestId, CancellationToken cancellationToken = default);
    Task<bool> ApprovePaymentRequestAsync(int requestId, long adminId, CancellationToken cancellationToken = default);
    Task<bool> RejectPaymentRequestAsync(int requestId, long adminId, CancellationToken cancellationToken = default);

    // Key transfer
    Task<bool> TransferKeyAsync(string key, long fromUserId, long toUserId, CancellationToken cancellationToken = default);

    Task<UserLicenseRecord?> DeleteLicenseAsync(string hwid, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UserSummary>> GetUserSummariesAsync(int limit, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<KeyHolderSummary>> GetKeyHolderSummariesAsync(int limit, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UserLicenseRecord>> GetLicensesByHwidAsync(string hwid, CancellationToken cancellationToken = default);

    // Admin action logging
    Task LogAdminActionAsync(long adminId, string actionType, string? targetType, string? targetId, string? details, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AdminActionLog>> GetAdminActionLogsAsync(long? adminId = null, string? actionType = null, int limit = 100, CancellationToken cancellationToken = default);

    // Key management
    Task<bool> DeleteSubscriptionKeyAsync(string key, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SubscriptionKeyRecord>> GetAllKeysByUserIdAsync(long userId, CancellationToken cancellationToken = default);

    // Advanced search
    Task<IReadOnlyList<UserLicenseRecord>> SearchUsersByHwidAsync(string hwidPattern, int limit = 50, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<UserLicenseRecord>> GetExpiredLicensesAsync(int limit = 100, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<UserLicenseRecord>> GetBannedLicensesAsync(int limit = 100, CancellationToken cancellationToken = default);

    // Get all data
    Task<IReadOnlyList<SubscriptionKeyWithOwner>> GetAllKeysWithOwnersAsync(int limit = 100, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<UserLicenseRecord>> GetAllUsersAsync(int limit = 100, CancellationToken cancellationToken = default);

    // Bulk operations
    Task<int> AddDaysToAllUsersAsync(int days, CancellationToken cancellationToken = default);

    // Product versions and updates
    Task<long> InsertProductVersionAsync(string version, string fileId, string fileName, long fileSize, string? updateLog, long uploadedBy, CancellationToken cancellationToken = default);
    Task<ProductVersion?> GetLatestProductVersionAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProductVersion>> GetProductVersionHistoryAsync(int limit = 10, CancellationToken cancellationToken = default);
    Task<bool> MarkVersionAsLatestAsync(long versionId, CancellationToken cancellationToken = default);

    // Update notifications
    Task InsertUpdateNotificationAsync(long versionId, long userId, CancellationToken cancellationToken = default);
    Task<bool> MarkUpdateAsDownloadedAsync(long versionId, long userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<long>> GetActiveUserIdsAsync(CancellationToken cancellationToken = default);
    Task<bool> HasActiveSubscriptionOrKeysAsync(long userId, CancellationToken cancellationToken = default);
}
