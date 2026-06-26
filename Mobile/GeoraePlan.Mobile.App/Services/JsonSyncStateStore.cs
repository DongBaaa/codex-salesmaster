using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GeoraePlan.Mobile.App.Models;
using Microsoft.Data.Sqlite;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Services;

public sealed class JsonSyncStateStore
{
    private const string StateTableName = "mobile_sync_states";

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
        InitializeSqliteProvider();
    }

    private string LegacyFilePath => Path.Combine(FileSystem.AppDataDirectory, "mobile-sync-state.json");
    private string StateDirectory => Path.Combine(FileSystem.AppDataDirectory, "sync-states");
    private string DatabasePath => Path.Combine(StateDirectory, "mobile-sync-state.db");
    private string LegacyQuarantineDirectory => Path.Combine(StateDirectory, "legacy-unassigned");
    private string FilePath => ResolveScopedFilePath();
    private string StateKey => ResolveScopedStateKey();

    public async Task<MobileSyncState> LoadAsync(CancellationToken ct = default)
    {
        var filePath = FilePath;
        var stateKey = StateKey;
        var databasePath = DatabasePath;
        var fileLock = GetFileLock(databasePath);
        await fileLock.WaitAsync(ct);
        try
        {
            try
            {
                await EnsureDatabaseAsync(databasePath, ct);
                var storedState = await TryLoadStateFromDatabaseAsync(databasePath, stateKey, ct);
                if (storedState is not null)
                {
                    storedState.Normalize();
                    ApplyCurrentOwner(storedState);
                    return storedState;
                }

                var fresh = new MobileSyncState();
                fresh.Normalize();
                ApplyCurrentOwner(fresh);
                await TryRecoverOrQuarantineLegacyStateAsync(stateKey, filePath, fresh, ct);
                return fresh;
            }
            catch (Exception ex) when (IsSqliteStorageException(ex))
            {
                MobileAppLogger.Warn("SYNC", $"SQLite 모바일 동기화 상태를 읽지 못해 JSON 백업 저장소로 전환합니다: {ex.Message}");
                return await LoadFromJsonFileAsync(filePath, ct);
            }
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
        var databasePath = DatabasePath;
        var stateKey = StateKey;
        var fileLock = GetFileLock(databasePath);
        await fileLock.WaitAsync(ct);
        try
        {
            try
            {
                await EnsureDatabaseAsync(databasePath, ct);
                await SaveStateToDatabaseAsync(databasePath, stateKey, state, ct);
                await TrySaveJsonBackupAsync(filePath, state, ct);
            }
            catch (Exception ex) when (IsSqliteStorageException(ex))
            {
                MobileAppLogger.Warn("SYNC", $"SQLite 모바일 동기화 상태 저장에 실패해 JSON 백업 저장소로 전환합니다: {ex.Message}");
                await SaveToJsonFileAsync(filePath, state, ct);
            }
        }
        finally
        {
            fileLock.Release();
        }
    }

    private async Task<MobileSyncState> LoadFromJsonFileAsync(string filePath, CancellationToken ct)
    {
        if (!File.Exists(filePath))
        {
            var fresh = new MobileSyncState();
            fresh.Normalize();
            ApplyCurrentOwner(fresh);
            await TryRecoverLegacyJsonOnlyStateAsync(filePath, fresh, ct);
            return fresh;
        }

        await using var stream = File.OpenRead(filePath);
        var state = await JsonSerializer.DeserializeAsync<MobileSyncState>(stream, _jsonOptions, ct)
                    ?? new MobileSyncState();

        state.Normalize();
        ApplyCurrentOwner(state);
        return state;
    }

    private async Task SaveToJsonFileAsync(string filePath, MobileSyncState state, CancellationToken ct)
    {
        state.Normalize();
        ApplyCurrentOwner(state);
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

    private async Task TrySaveJsonBackupAsync(string filePath, MobileSyncState state, CancellationToken ct)
    {
        try
        {
            await SaveToJsonFileAsync(filePath, state, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MobileAppLogger.Warn("SYNC", $"SQLite 저장은 완료됐지만 JSON 백업 저장에는 실패했습니다: {ex.Message}");
        }
    }

    private async Task EnsureDatabaseAsync(string databasePath, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = CreateConnection(databasePath);
        await connection.OpenAsync(ct);

        await ExecuteNonQueryAsync(connection, "PRAGMA journal_mode=WAL;", ct);
        await ExecuteNonQueryAsync(connection, "PRAGMA synchronous=NORMAL;", ct);
        await ExecuteNonQueryAsync(
            connection,
            $"""
            CREATE TABLE IF NOT EXISTS {StateTableName} (
                state_key TEXT PRIMARY KEY,
                owner_username TEXT NOT NULL DEFAULT '',
                owner_tenant_code TEXT NOT NULL DEFAULT '',
                owner_office_code TEXT NOT NULL DEFAULT '',
                json_payload TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            """,
            ct);
        await ExecuteNonQueryAsync(
            connection,
            $"CREATE INDEX IF NOT EXISTS IX_{StateTableName}_owner ON {StateTableName} (owner_username, owner_tenant_code, owner_office_code);",
            ct);
    }

    private async Task<MobileSyncState?> TryLoadStateFromDatabaseAsync(
        string databasePath,
        string stateKey,
        CancellationToken ct)
    {
        await using var connection = CreateConnection(databasePath);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT json_payload FROM {StateTableName} WHERE state_key = $stateKey LIMIT 1;";
        command.Parameters.AddWithValue("$stateKey", stateKey);

        var payload = await command.ExecuteScalarAsync(ct) as string;
        if (string.IsNullOrWhiteSpace(payload))
            return null;

        return JsonSerializer.Deserialize<MobileSyncState>(payload, _jsonOptions) ?? new MobileSyncState();
    }

    private async Task SaveStateToDatabaseAsync(
        string databasePath,
        string stateKey,
        MobileSyncState state,
        CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(state, _jsonOptions);
        await using var connection = CreateConnection(databasePath);
        await connection.OpenAsync(ct);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT OR REPLACE INTO {StateTableName} (
                state_key,
                owner_username,
                owner_tenant_code,
                owner_office_code,
                json_payload,
                updated_utc
            ) VALUES (
                $stateKey,
                $ownerUsername,
                $ownerTenantCode,
                $ownerOfficeCode,
                $jsonPayload,
                $updatedUtc
            );
            """;
        command.Parameters.AddWithValue("$stateKey", stateKey);
        command.Parameters.AddWithValue("$ownerUsername", state.OwnerUsername ?? string.Empty);
        command.Parameters.AddWithValue("$ownerTenantCode", state.OwnerTenantCode ?? string.Empty);
        command.Parameters.AddWithValue("$ownerOfficeCode", state.OwnerOfficeCode ?? string.Empty);
        command.Parameters.AddWithValue("$jsonPayload", payload);
        command.Parameters.AddWithValue("$updatedUtc", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string commandText, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static SqliteConnection CreateConnection(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };
        return new SqliteConnection(builder.ToString());
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
        var scopeKey = BuildOwnerScopeKey(username, tenantCode, officeCode);
        var hash = HashScopeKey(scopeKey);
        var readableName = $"{SanitizeFilePart(tenantCode)}-{SanitizeFilePart(officeCode)}-{SanitizeFilePart(username)}";

        return Path.Combine(StateDirectory, $"{readableName}-{hash}.json");
    }

    private string ResolveScopedStateKey()
    {
        var snapshot = _sessionStore.GetSnapshot();
        if (!snapshot.IsAuthenticated ||
            string.IsNullOrWhiteSpace(snapshot.Username) ||
            string.IsNullOrWhiteSpace(snapshot.TenantCode) ||
            string.IsNullOrWhiteSpace(snapshot.OfficeCode))
        {
            return "legacy";
        }

        var tenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(snapshot.TenantCode, snapshot.OfficeCode);
        var officeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(snapshot.OfficeCode);
        var username = snapshot.Username.Trim();
        return BuildOwnerScopeKey(username, tenantCode, officeCode);
    }

    private async Task TryRecoverOrQuarantineLegacyStateAsync(
        string stateKey,
        string scopedFilePath,
        MobileSyncState freshState,
        CancellationToken ct)
    {
        if (File.Exists(scopedFilePath))
        {
            var scopedState = await TryReadJsonStateAsync(scopedFilePath, ct);
            if (scopedState is not null && (string.Equals(scopedFilePath, LegacyFilePath, StringComparison.OrdinalIgnoreCase) || BelongsToCurrentOwner(scopedState)))
            {
                scopedState.Normalize();
                ApplyCurrentOwner(scopedState);
                await SaveStateToDatabaseAsync(DatabasePath, stateKey, scopedState, ct);
                CopyState(scopedState, freshState);
                MobileAppLogger.Info("SYNC", $"기존 JSON 모바일 동기화 상태를 SQLite로 이전했습니다: {Path.GetFileName(scopedFilePath)}");
                return;
            }
        }

        if (!File.Exists(LegacyFilePath) ||
            string.Equals(scopedFilePath, LegacyFilePath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var legacyState = await TryReadJsonStateAsync(LegacyFilePath, ct);
        if (legacyState is null)
            return;

        legacyState.Normalize();
        if (BelongsToCurrentOwner(legacyState))
        {
            ApplyCurrentOwner(legacyState);
            await SaveStateToDatabaseAsync(DatabasePath, stateKey, legacyState, ct);
            CopyState(legacyState, freshState);
            MobileAppLogger.Info("SYNC", "기존 미분리 모바일 동기화 상태를 현재 계정 SQLite 상태로 이전했습니다.");
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

    private async Task TryRecoverLegacyJsonOnlyStateAsync(
        string scopedFilePath,
        MobileSyncState freshState,
        CancellationToken ct)
    {
        if (!File.Exists(LegacyFilePath) ||
            string.Equals(scopedFilePath, LegacyFilePath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var legacyState = await TryReadJsonStateAsync(LegacyFilePath, ct);
        if (legacyState is null)
            return;

        legacyState.Normalize();
        if (BelongsToCurrentOwner(legacyState))
        {
            ApplyCurrentOwner(legacyState);
            Directory.CreateDirectory(Path.GetDirectoryName(scopedFilePath)!);
            await SaveToJsonFileAsync(scopedFilePath, legacyState, ct);
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

    private async Task<MobileSyncState?> TryReadJsonStateAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<MobileSyncState>(stream, _jsonOptions, ct);
        }
        catch (Exception ex)
        {
            MobileAppLogger.Warn("SYNC", $"기존 모바일 동기화 상태 파일을 읽지 못했습니다: {ex.Message}");
            return null;
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
        return (state.PendingPush.CompanyProfiles?.Count ?? 0) > 0 ||
               (state.PendingPush.Units?.Count ?? 0) > 0 ||
               (state.PendingPush.CustomerCategories?.Count ?? 0) > 0 ||
               (state.PendingPush.PriceGradeOptions?.Count ?? 0) > 0 ||
               (state.PendingPush.TradeTypeOptions?.Count ?? 0) > 0 ||
               (state.PendingPush.ItemCategoryOptions?.Count ?? 0) > 0 ||
               (state.PendingPush.CustomerMasters?.Count ?? 0) > 0 ||
               state.PendingInvoiceCount > 0 ||
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

    private static string BuildOwnerScopeKey(string username, string tenantCode, string officeCode)
        => $"{username}|{tenantCode}|{officeCode}".ToUpperInvariant();

    private static string HashScopeKey(string scopeKey)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scopeKey)))
            .ToLowerInvariant()[..12];

    private static SemaphoreSlim GetFileLock(string filePath)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        return FileLocks.GetOrAdd(normalizedPath, _ => new SemaphoreSlim(1, 1));
    }

    private static bool IsSqliteStorageException(Exception ex)
        => ex is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException;

    private static void InitializeSqliteProvider()
    {
        try
        {
            SQLitePCL.Batteries_V2.Init();
        }
        catch (Exception ex)
        {
            MobileAppLogger.Warn("SYNC", $"SQLite provider 초기화 경고: {ex.Message}");
        }
    }
}
