using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

public sealed partial class LocalStateService
{
    public async Task<bool> DeleteSettingAsync(string key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;

        var setting = await _db.Settings.FindAsync([key], ct);
        if (setting is null)
            return false;

        _db.Settings.Remove(setting);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<int> DeleteSettingsByPrefixAsync(string keyPrefix, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(keyPrefix))
            return 0;

        var settings = await _db.Settings
            .Where(setting => EF.Functions.Like(setting.Key, keyPrefix + "%"))
            .ToListAsync(ct);

        if (settings.Count == 0)
            return 0;

        _db.Settings.RemoveRange(settings);
        await _db.SaveChangesAsync(ct);
        return settings.Count;
    }

    public async Task<int> NormalizeSharedOptionIdCasingAsync(CancellationToken ct = default)
    {
        var providerName = _db.Database.ProviderName ?? string.Empty;
        if (!providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            return 0;

        var normalizedCount = 0;
        foreach (var tableName in SharedOptionTables)
            normalizedCount += await NormalizeGuidTextColumnToUpperAsync(tableName, "Id", ct);

        if (normalizedCount > 0)
            _db.ChangeTracker.Clear();

        return normalizedCount;
    }

    public Task MarkServerMirrorRefreshRequiredAsync(CancellationToken ct = default)
        => SetSettingAsync(PendingMirrorRefreshSettingKey, "1", ct);

    public async Task<bool> IsServerMirrorRefreshRequiredAsync(CancellationToken ct = default)
        => string.Equals(await GetSettingAsync(PendingMirrorRefreshSettingKey, ct), "1", StringComparison.Ordinal);

    public Task ClearServerMirrorRefreshRequiredAsync(CancellationToken ct = default)
        => DeleteSettingAsync(PendingMirrorRefreshSettingKey, ct);

    private Task<int> NormalizeGuidTextColumnToUpperAsync(string tableName, string columnName, CancellationToken ct)
    {
        var sql =
            "UPDATE \"" + tableName + "\" " +
            "SET \"" + columnName + "\" = UPPER(\"" + columnName + "\") " +
            "WHERE \"" + columnName + "\" IS NOT NULL " +
            "AND LENGTH(\"" + columnName + "\") = 36 " +
            "AND SUBSTR(\"" + columnName + "\", 9, 1) = '-' " +
            "AND SUBSTR(\"" + columnName + "\", 14, 1) = '-' " +
            "AND SUBSTR(\"" + columnName + "\", 19, 1) = '-' " +
            "AND SUBSTR(\"" + columnName + "\", 24, 1) = '-' " +
            "AND \"" + columnName + "\" <> UPPER(\"" + columnName + "\");";

        return _db.Database.ExecuteSqlRawAsync(sql, ct);
    }

    public async Task<bool> RebuildInventorySnapshotsForIntegrityAsync(SessionState session, CancellationToken ct = default)
    {
        if (!CanResetInventoryValue(session))
            return false;

        await RebuildInventorySnapshotsAsync(
            new InvoiceSaveContext
            {
                Username = session.User?.Username ?? "system",
                Role = session.User?.Role ?? string.Empty,
                OfficeCode = session.OfficeCode
            },
            ct);

        return true;
    }

    public async Task<InventoryIntegrityRepairResult> RepairInventoryIntegrityForStartupAsync(SessionState session, CancellationToken ct = default)
    {
        if (!CanResetInventoryValue(session))
            return new InventoryIntegrityRepairResult(0, false);

        var crossTenantTransferIds = (await _db.InventoryTransfers
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(transfer => !transfer.IsDeleted)
                .Select(transfer => new
                {
                    transfer.Id,
                    transfer.FromWarehouseCode,
                    transfer.ToWarehouseCode
                })
                .ToListAsync(ct))
            .Where(transfer => IsCrossTenantInventoryTransferRoute(
                transfer.FromWarehouseCode,
                transfer.ToWarehouseCode))
            .Select(transfer => transfer.Id)
            .Distinct()
            .ToList();

        if (crossTenantTransferIds.Count > 0)
        {
            var transferIdSet = crossTenantTransferIds.ToHashSet();
            var lines = await _db.InventoryTransferLines
                .IgnoreQueryFilters()
                .Where(line => transferIdSet.Contains(line.TransferId))
                .ToListAsync(ct);
            if (lines.Count > 0)
                _db.InventoryTransferLines.RemoveRange(lines);

            var transfers = await _db.InventoryTransfers
                .IgnoreQueryFilters()
                .Where(transfer => transferIdSet.Contains(transfer.Id))
                .ToListAsync(ct);
            if (transfers.Count > 0)
                _db.InventoryTransfers.RemoveRange(transfers);

            await _db.SaveChangesAsync(ct);
        }

        await RebuildInventorySnapshotsAsync(
            new InvoiceSaveContext
            {
                Username = session.User?.Username ?? "system",
                Role = session.User?.Role ?? string.Empty,
                OfficeCode = session.OfficeCode
            },
            ct);

        return new InventoryIntegrityRepairResult(crossTenantTransferIds.Count, true);
    }

    public async Task<SyncOutboxRecoveryResult> RecoverStaleSyncOutboxEntriesAsync(CancellationToken ct = default)
    {
        var staleSentCutoffUtc = DateTime.UtcNow - StaleSyncOutboxSentThreshold;
        var staleSentEntries = await _db.SyncOutboxEntries
            .Where(entry =>
                entry.Status == "Sent" &&
                entry.SentAtUtc.HasValue &&
                entry.SentAtUtc.Value <= staleSentCutoffUtc)
            .ToListAsync(ct);

        foreach (var entry in staleSentEntries)
        {
            entry.Status = "Prepared";
            entry.ErrorMessage = string.Empty;
            entry.SentAtUtc = null;
            entry.AcknowledgedAtUtc = null;
        }

        if (staleSentEntries.Count > 0)
            await _db.SaveChangesAsync(ct);

        var failedCount = await _db.SyncOutboxEntries
            .AsNoTracking()
            .CountAsync(entry => entry.Status == "Failed", ct);
        var pendingCount = await _db.SyncOutboxEntries
            .AsNoTracking()
            .CountAsync(entry => entry.Status != "Acknowledged", ct);

        return new SyncOutboxRecoveryResult(
            staleSentEntries.Count,
            failedCount,
            pendingCount);
    }
}
