using System.Collections.Concurrent;
using System.Text.Json;
using GeoraePlan.Mobile.App.Models;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Services;

public sealed class JsonSyncStateStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly SessionStore _sessionStore;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public JsonSyncStateStore(SessionStore sessionStore)
    {
        _sessionStore = sessionStore;
    }

    private string LegacyFilePath => Path.Combine(FileSystem.AppDataDirectory, "mobile-sync-state.json");
    private string StateDirectory => Path.Combine(FileSystem.AppDataDirectory, "sync-states");
    private string LegacyQuarantineDirectory => Path.Combine(StateDirectory, "legacy-unassigned");
    private string FilePath => ResolveScopedFilePath();

    public async Task<MobileSyncState> LoadAsync(CancellationToken ct = default)
    {
        var filePath = FilePath;
        var fileLock = GetFileLock(filePath);
        await fileLock.WaitAsync(ct);
        try
        {
            if (!File.Exists(filePath))
            {
                var fresh = new MobileSyncState();
                fresh.Normalize();
                ApplyCurrentOwner(fresh);
                await TryRecoverOrQuarantineLegacyStateAsync(filePath, fresh, ct);
                return fresh;
            }

            await using var stream = File.OpenRead(filePath);
            var state = await JsonSerializer.DeserializeAsync<MobileSyncState>(stream, _jsonOptions, ct)
                        ?? new MobileSyncState();

            state.Normalize();
            ApplyCurrentOwner(state);
            return state;
        }
        finally
        {
            fileLock.Release();
        }
    }

    public async Task SaveAsync(MobileSyncState state, CancellationToken ct = default)
    {
        state.Normalize();
        ApplyCurrentOwner(state);
        var filePath = FilePath;
        var fileLock = GetFileLock(filePath);
        await fileLock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

            var tempPath = $"{filePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var stream = new FileStream(
                                 tempPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 bufferSize: 16 * 1024,
                                 useAsync: true))
                {
                    await JsonSerializer.SerializeAsync(stream, state, _jsonOptions, ct);
                    await stream.FlushAsync(ct);
                }

                File.Move(tempPath, filePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
        }
        finally
        {
            fileLock.Release();
        }
    }

    private string ResolveScopedFilePath()
    {
        var snapshot = _sessionStore.GetSnapshot();
        if (!snapshot.IsAuthenticated ||
            string.IsNullOrWhiteSpace(snapshot.Username) ||
            string.IsNullOrWhiteSpace(snapshot.TenantCode) ||
            string.IsNullOrWhiteSpace(snapshot.OfficeCode))
        {
            return LegacyFilePath;
        }

        var tenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(snapshot.TenantCode, snapshot.OfficeCode);
        var officeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(snapshot.OfficeCode);
        var username = snapshot.Username.Trim();
        var scopeKey = $"{username}|{tenantCode}|{officeCode}".ToUpperInvariant();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(scopeKey)))
            .ToLowerInvariant()[..12];
        var readableName = $"{SanitizeFilePart(tenantCode)}-{SanitizeFilePart(officeCode)}-{SanitizeFilePart(username)}";

        return Path.Combine(StateDirectory, $"{readableName}-{hash}.json");
    }

    private async Task TryRecoverOrQuarantineLegacyStateAsync(
        string scopedFilePath,
        MobileSyncState freshState,
        CancellationToken ct)
    {
        if (!File.Exists(LegacyFilePath) ||
            string.Equals(scopedFilePath, LegacyFilePath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        MobileSyncState? legacyState;
        try
        {
            await using var stream = File.OpenRead(LegacyFilePath);
            legacyState = await JsonSerializer.DeserializeAsync<MobileSyncState>(stream, _jsonOptions, ct);
        }
        catch (Exception ex)
        {
            MobileAppLogger.Warn("SYNC", $"기존 모바일 동기화 상태 파일을 읽지 못했습니다: {ex.Message}");
            return;
        }

        if (legacyState is null)
            return;

        legacyState.Normalize();
        if (BelongsToCurrentOwner(legacyState))
        {
            ApplyCurrentOwner(legacyState);
            Directory.CreateDirectory(Path.GetDirectoryName(scopedFilePath)!);
            await using var target = File.Create(scopedFilePath);
            await JsonSerializer.SerializeAsync(target, legacyState, _jsonOptions, ct);
            CopyState(legacyState, freshState);
            return;
        }

        if (!HasPendingPayload(legacyState))
            return;

        Directory.CreateDirectory(LegacyQuarantineDirectory);
        var quarantinePath = Path.Combine(
            LegacyQuarantineDirectory,
            $"mobile-sync-state-{DateTime.UtcNow:yyyyMMddHHmmss}.json");

        try
        {
            File.Move(LegacyFilePath, quarantinePath, overwrite: false);
            freshState.LastError = "이전 모바일 미전송 초안이 계정 정보 없이 발견되어 자동 반영하지 않고 격리했습니다. 다른 계정 자료와 섞이지 않도록 설정/동기화 화면에서 확인 후 필요 시 다시 입력하세요.";
            MobileAppLogger.Warn("SYNC", $"계정 없는 기존 모바일 동기화 상태 파일을 격리했습니다: {quarantinePath}");
        }
        catch (Exception ex)
        {
            freshState.LastError = "이전 모바일 미전송 초안이 계정 정보 없이 발견되었습니다. 다른 계정 자료와 섞일 수 있어 자동 반영하지 않았습니다.";
            MobileAppLogger.Warn("SYNC", $"기존 모바일 동기화 상태 파일 격리 실패: {ex.Message}");
        }
    }

    private bool BelongsToCurrentOwner(MobileSyncState state)
    {
        var snapshot = _sessionStore.GetSnapshot();
        if (!snapshot.IsAuthenticated ||
            string.IsNullOrWhiteSpace(state.OwnerUsername) ||
            string.IsNullOrWhiteSpace(state.OwnerTenantCode) ||
            string.IsNullOrWhiteSpace(state.OwnerOfficeCode))
        {
            return false;
        }

        var tenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(snapshot.TenantCode, snapshot.OfficeCode);
        var officeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(snapshot.OfficeCode);
        return string.Equals(state.OwnerUsername, snapshot.Username, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(state.OwnerTenantCode, tenantCode, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(state.OwnerOfficeCode, officeCode, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasPendingPayload(MobileSyncState state)
    {
        state.Normalize();
        return state.PendingInvoiceCount > 0 ||
               state.PendingPaymentCount > 0 ||
               state.PendingTransactionCount > 0 ||
               state.PendingTransactionAttachmentCount > 0 ||
               state.PendingInventoryTransferCount > 0 ||
               state.PendingRentalManagementCompanyCount > 0 ||
               state.PendingRentalBillingProfileCount > 0 ||
               state.PendingRentalAssetCount > 0 ||
               state.PendingRentalAssetAssignmentHistoryCount > 0 ||
               state.PendingRentalBillingLogCount > 0 ||
               state.PendingPaymentAttachmentCount > 0 ||
               (state.PendingPush.Customers?.Count ?? 0) > 0 ||
               (state.PendingPush.CustomerContracts?.Count ?? 0) > 0 ||
               (state.PendingPush.Items?.Count ?? 0) > 0 ||
               (state.PendingPush.ItemWarehouseStocks?.Count ?? 0) > 0;
    }

    private static void CopyState(MobileSyncState source, MobileSyncState target)
    {
        target.DeviceId = source.DeviceId;
        target.OwnerUsername = source.OwnerUsername;
        target.OwnerTenantCode = source.OwnerTenantCode;
        target.OwnerOfficeCode = source.OwnerOfficeCode;
        target.LastRevision = source.LastRevision;
        target.LastSuccessUtc = source.LastSuccessUtc;
        target.LastAttemptUtc = source.LastAttemptUtc;
        target.LastBackgroundSyncUtc = source.LastBackgroundSyncUtc;
        target.LastError = source.LastError;
        target.ConsecutiveFailureCount = source.ConsecutiveFailureCount;
        target.LastPulledCustomerCount = source.LastPulledCustomerCount;
        target.LastPulledItemCount = source.LastPulledItemCount;
        target.LastPulledItemWarehouseStockCount = source.LastPulledItemWarehouseStockCount;
        target.LastPulledPriceGradeOptionCount = source.LastPulledPriceGradeOptionCount;
        target.LastPulledInvoiceCount = source.LastPulledInvoiceCount;
        target.LastPulledPaymentCount = source.LastPulledPaymentCount;
        target.LastPulledTransactionCount = source.LastPulledTransactionCount;
        target.LastPulledTransactionAttachmentCount = source.LastPulledTransactionAttachmentCount;
        target.LastPulledInventoryTransferCount = source.LastPulledInventoryTransferCount;
        target.LastPulledRentalManagementCompanyCount = source.LastPulledRentalManagementCompanyCount;
        target.LastPulledRentalBillingProfileCount = source.LastPulledRentalBillingProfileCount;
        target.LastPulledRentalAssetCount = source.LastPulledRentalAssetCount;
        target.LastPulledRentalAssetAssignmentHistoryCount = source.LastPulledRentalAssetAssignmentHistoryCount;
        target.LastPulledRentalBillingLogCount = source.LastPulledRentalBillingLogCount;
        target.SyncedCustomers = source.SyncedCustomers;
        target.SyncedItems = source.SyncedItems;
        target.SyncedItemWarehouseStocks = source.SyncedItemWarehouseStocks;
        target.SyncedInvoices = source.SyncedInvoices;
        target.SyncedPayments = source.SyncedPayments;
        target.SyncedTransactions = source.SyncedTransactions;
        target.SyncedTransactionAttachments = source.SyncedTransactionAttachments;
        target.SyncedInventoryTransfers = source.SyncedInventoryTransfers;
        target.SyncedRentalManagementCompanies = source.SyncedRentalManagementCompanies;
        target.SyncedRentalBillingProfiles = source.SyncedRentalBillingProfiles;
        target.SyncedRentalAssets = source.SyncedRentalAssets;
        target.SyncedRentalAssetAssignmentHistories = source.SyncedRentalAssetAssignmentHistories;
        target.SyncedRentalBillingLogs = source.SyncedRentalBillingLogs;
        target.SyncedPriceGradeOptions = source.SyncedPriceGradeOptions;
        target.PendingPush = source.PendingPush;
        target.PendingPaymentAttachments = source.PendingPaymentAttachments;
    }

    private void ApplyCurrentOwner(MobileSyncState state)
    {
        var snapshot = _sessionStore.GetSnapshot();
        if (!snapshot.IsAuthenticated)
            return;

        state.OwnerUsername = snapshot.Username?.Trim() ?? string.Empty;
        state.OwnerTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(snapshot.TenantCode, snapshot.OfficeCode);
        state.OwnerOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(snapshot.OfficeCode);
    }

    private static string SanitizeFilePart(string? value)
    {
        var source = string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
        var chars = source.Select(ch =>
                char.IsLetterOrDigit(ch) ||
                ch is '-' or '_' or '.'
                    ? ch
                    : '_')
            .ToArray();

        var sanitized = new string(chars).Trim('_');
        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
    }

    private static SemaphoreSlim GetFileLock(string filePath)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        return FileLocks.GetOrAdd(normalizedPath, _ => new SemaphoreSlim(1, 1));
    }
}
