using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

public sealed record LoginScopeRegistrationResult(
    bool ScopeChanged,
    bool HadPendingSyncChanges,
    string Message);

public sealed partial class LocalStateService
{
    private const string LastLoginScopeKey = "Login.LastScopeKey";
    private const string LastLoginScopeUsernameKey = "Login.LastScopeUsername";
    private const string LastLoginScopeTenantKey = "Login.LastScopeTenantCode";
    private const string LastLoginScopeOfficeKey = "Login.LastScopeOfficeCode";
    private const string LastLoginScopeTypeKey = "Login.LastScopeType";

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

    public async Task<LoginScopeRegistrationResult> RegisterLoginScopeAsync(
        SessionState session,
        CancellationToken ct = default)
    {
        if (session is null || !session.IsLoggedIn)
        {
            return new LoginScopeRegistrationResult(
                ScopeChanged: false,
                HadPendingSyncChanges: false,
                Message: "로그인 세션이 없어 계정 범위 등록을 건너뜁니다.");
        }

        var username = NormalizeLoginScopePart(session.User?.Username, "unknown");
        var tenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            session.TenantCode,
            session.OfficeCode);
        var officeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(session.OfficeCode);
        var scopeType = TenantScopeCatalog.NormalizeScopeTypeOrDefault(session.ScopeType);
        var currentScopeKey = BuildLoginScopeKey(username, tenantCode, officeCode, scopeType);
        var previousScopeKey = await GetSettingAsync(LastLoginScopeKey, ct);

        var scopeChanged = !string.IsNullOrWhiteSpace(previousScopeKey) &&
                           !string.Equals(previousScopeKey, currentScopeKey, StringComparison.OrdinalIgnoreCase);
        var hadPendingSyncChanges = false;

        if (scopeChanged)
        {
            hadPendingSyncChanges = await HasPendingSyncChangesAsync(ct);

            foreach (var key in ServerMirrorStateSettingKeys)
            {
                await DeleteSettingAsync(key, ct);
            }

            await MarkServerMirrorRefreshRequiredAsync(ct);
        }

        await SetSettingAsync(LastLoginScopeKey, currentScopeKey, ct);
        await SetSettingAsync(LastLoginScopeUsernameKey, username, ct);
        await SetSettingAsync(LastLoginScopeTenantKey, tenantCode, ct);
        await SetSettingAsync(LastLoginScopeOfficeKey, officeCode, ct);
        await SetSettingAsync(LastLoginScopeTypeKey, scopeType, ct);

        var message = scopeChanged
            ? hadPendingSyncChanges
                ? "로그인 계정/범위가 변경되어 동기화 기준을 초기화했습니다. 미동기화 변경은 보존했으며 해당 계정으로 다시 로그인해 동기화할 수 있습니다."
                : "로그인 계정/범위가 변경되어 현재 계정 기준으로 서버 캐시를 다시 구성합니다."
            : "로그인 계정/범위가 이전과 동일합니다.";

        return new LoginScopeRegistrationResult(scopeChanged, hadPendingSyncChanges, message);
    }

    private static string BuildLoginScopeKey(string username, string tenantCode, string officeCode, string scopeType)
        => string.Join(
            "|",
            NormalizeLoginScopePart(username, "unknown"),
            TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(tenantCode, officeCode),
            OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode),
            TenantScopeCatalog.NormalizeScopeTypeOrDefault(scopeType));

    private static string NormalizeLoginScopePart(string? value, string fallback)
    {
        var normalized = (value ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

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
        if (!await CanRunGlobalInventoryMaintenanceForCurrentIntegrityScopeAsync(session, ct))
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
        if (!await CanRunGlobalInventoryMaintenanceForCurrentIntegrityScopeAsync(session, ct))
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

        await NormalizeNonInventoryItemsAfterSnapshotRepairAsync(ct);

        var deletedItemsWithStockResidue = await _db.Items
            .IgnoreQueryFilters()
            .Where(item => item.IsDeleted && item.CurrentStock != 0m)
            .ToListAsync(ct);
        if (deletedItemsWithStockResidue.Count > 0)
        {
            var now = DateTime.UtcNow;
            foreach (var item in deletedItemsWithStockResidue)
            {
                item.CurrentStock = 0m;
                item.IsDirty = true;
                item.UpdatedAtUtc = now;
            }

            await _db.SaveChangesAsync(ct);
        }

        return new InventoryIntegrityRepairResult(crossTenantTransferIds.Count, true);
    }

    private async Task<int> NormalizeNonInventoryItemsAfterSnapshotRepairAsync(CancellationToken ct)
    {
        var items = await _db.Items
            .IgnoreQueryFilters()
            .Where(item => !item.IsDeleted)
            .ToListAsync(ct);
        if (items.Count == 0)
            return 0;

        var now = DateTime.UtcNow;
        var changedCount = 0;
        foreach (var item in items)
        {
            var normalizedTrackingType = ItemOperationalPolicy.NormalizeTrackingType(
                item.TrackingType,
                item.ItemKind,
                item.CategoryName,
                item.IsRental);
            if (ItemOperationalPolicy.SupportsInventory(normalizedTrackingType))
                continue;

            var normalizedItemKind = ItemOperationalPolicy.NormalizeItemKind(
                item.ItemKind,
                normalizedTrackingType,
                item.CategoryName,
                item.IsRental);
            var expectedIsRental = string.Equals(
                normalizedTrackingType,
                ItemTrackingTypes.Asset,
                StringComparison.Ordinal);
            var expectedIsSale = !expectedIsRental;
            var changed = false;

            if (!string.Equals(item.TrackingType, normalizedTrackingType, StringComparison.Ordinal))
            {
                item.TrackingType = normalizedTrackingType;
                changed = true;
            }

            if (!string.Equals(item.ItemKind, normalizedItemKind, StringComparison.Ordinal))
            {
                item.ItemKind = normalizedItemKind;
                changed = true;
            }

            if (item.IsRental != expectedIsRental)
            {
                item.IsRental = expectedIsRental;
                changed = true;
            }

            if (item.IsSale != expectedIsSale)
            {
                item.IsSale = expectedIsSale;
                changed = true;
            }

            if (item.CurrentStock != 0m)
            {
                item.CurrentStock = 0m;
                changed = true;
            }

            if (item.SafetyStock != 0m)
            {
                item.SafetyStock = 0m;
                changed = true;
            }

            if (!changed)
                continue;

            item.IsDirty = true;
            item.UpdatedAtUtc = now;
            changedCount++;
        }

        if (changedCount > 0)
            await _db.SaveChangesAsync(ct);

        return changedCount;
    }

    private async Task<bool> CanRunGlobalInventoryMaintenanceForCurrentIntegrityScopeAsync(
        SessionState? session,
        CancellationToken ct)
    {
        if (!CanResetInventoryValue(session) || session is null || !session.IsLoggedIn)
            return false;

        var integrityTenantCode = ResolveIntegrityTenantCode(session);
        var integrityOfficeCodes = ResolveIntegrityOfficeCodes(session, integrityTenantCode);
        var integrityWarehouseCodes = ResolveIntegrityWarehouseCodes(integrityOfficeCodes)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (integrityWarehouseCodes.Count == 0)
            return false;

        var totalItemCount = await _db.Items
            .IgnoreQueryFilters()
            .CountAsync(ct);
        var scopedItemCount = await ApplyIntegrityItemScope(
                _db.Items.IgnoreQueryFilters(),
                integrityTenantCode,
                integrityOfficeCodes)
            .CountAsync(ct);
        if (totalItemCount != scopedItemCount)
            return false;

        var invoiceWarehouseRows = await _db.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(invoice => !invoice.IsDeleted && invoice.IsLatestVersion && invoice.IsConfirmed)
            .Select(invoice => new
            {
                invoice.SourceWarehouseCode,
                invoice.ResponsibleOfficeCode,
                invoice.OfficeCode
            })
            .ToListAsync(ct);
        if (invoiceWarehouseRows.Any(row =>
                !integrityWarehouseCodes.Contains(NormalizeWarehouseCode(
                    row.SourceWarehouseCode,
                    row.ResponsibleOfficeCode,
                    row.OfficeCode))))
        {
            return false;
        }

        var transferWarehouseRows = await _db.InventoryTransfers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(transfer => !transfer.IsDeleted)
            .Select(transfer => new
            {
                transfer.FromWarehouseCode,
                transfer.ToWarehouseCode
            })
            .ToListAsync(ct);
        if (transferWarehouseRows.Any(row =>
                !integrityWarehouseCodes.Contains(NormalizeWarehouseCode(
                    row.FromWarehouseCode,
                    DomainConstants.OfficeUsenet,
                    DomainConstants.OfficeUsenet)) ||
                !integrityWarehouseCodes.Contains(NormalizeWarehouseCode(
                    row.ToWarehouseCode,
                    DomainConstants.OfficeYeonsu,
                    DomainConstants.OfficeYeonsu))))
        {
            return false;
        }

        var inventoryWarehouseCodes = new List<string>();
        inventoryWarehouseCodes.AddRange(await _db.ItemWarehouseStocks
            .AsNoTracking()
            .Select(stock => stock.WarehouseCode)
            .ToListAsync(ct));
        inventoryWarehouseCodes.AddRange(await _db.InventoryMovements
            .AsNoTracking()
            .Select(movement => movement.WarehouseCode)
            .ToListAsync(ct));
        inventoryWarehouseCodes.AddRange(await _db.StockLayers
            .AsNoTracking()
            .Select(layer => layer.WarehouseCode)
            .ToListAsync(ct));
        inventoryWarehouseCodes.AddRange(await _db.CostAllocations
            .AsNoTracking()
            .Select(allocation => allocation.WarehouseCode)
            .ToListAsync(ct));
        inventoryWarehouseCodes.AddRange(await _db.SerialLedgers
            .AsNoTracking()
            .Select(ledger => ledger.WarehouseCode)
            .ToListAsync(ct));

        return inventoryWarehouseCodes.All(warehouseCode =>
            integrityWarehouseCodes.Contains(NormalizeWarehouseCode(
                warehouseCode,
                ResolveOfficeCodeFromWarehouseCode(warehouseCode),
                DomainConstants.OfficeUsenet)));
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
