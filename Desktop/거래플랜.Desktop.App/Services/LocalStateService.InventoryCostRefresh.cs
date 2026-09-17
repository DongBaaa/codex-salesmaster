using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using 거래플랜.Shared.Contracts;
using 거래플랜.Desktop.App.Data;

namespace 거래플랜.Desktop.App.Services;

public sealed partial class LocalStateService
{
    private const string InventoryCostInputSignatureKey = "InventoryCostInputs.v1";

    // Called inside the pull transaction, after all dependent entities and purges.
    // This cache is local: it must not write stock back to the server.
    internal async Task RefreshInventoryCostAfterPullAsync(bool force, CancellationToken ct)
    {
        if (_db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Inventory cost refresh requires the pull transaction.");

        var signature = await ComputeInventoryCostInputSignatureAsync(ct);
        var previousSignature = await _db.Settings.AsNoTracking()
            .Where(setting => setting.Key == InventoryCostInputSignatureKey)
            .Select(setting => setting.Value).FirstOrDefaultAsync(ct);
        if (!force && string.Equals(previousSignature, signature, StringComparison.Ordinal))
            return;

        await RebuildInventorySnapshotsCoreAsync(new InvoiceSaveContext
        {
            Username = "sync-cost-cache",
            Role = DomainConstants.RoleAdmin,
            OfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(_session.OfficeCode, DomainConstants.OfficeUsenet)
        }, ct, preserveAuthoritativeStock: true);

        // Reset movements can normalize their derived delta during calculation.
        var refreshedSignature = await ComputeInventoryCostInputSignatureAsync(ct);
        // This private cache key is saved inside the existing pull transaction.
        // Avoid re-reading a key already checked above solely to upsert its hash.
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Settings" ("Key", "Value")
            VALUES ({InventoryCostInputSignatureKey}, {refreshedSignature})
            ON CONFLICT("Key") DO UPDATE SET "Value" = excluded."Value"
            WHERE "Settings"."Value" <> excluded."Value";
            """, ct);
    }

    private async Task<int> PersistDerivedInvoiceCostStatusAsync(CancellationToken ct)
    {
        if (_db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Derived cost status requires the pull transaction.");

        _db.ChangeTracker.DetectChanges();
        var changed = _db.ChangeTracker.Entries<LocalInvoice>()
            .Where(entry => entry.Property(invoice => invoice.CostStatus).IsModified)
            .ToList();
        foreach (var group in changed.GroupBy(entry => entry.Entity.CostStatus, StringComparer.Ordinal))
        {
            var ids = group.Select(entry => entry.Entity.Id).ToList();
            await _db.Invoices.IgnoreQueryFilters()
                .Where(invoice => ids.Contains(invoice.Id))
                .ExecuteUpdateAsync(setters => setters.SetProperty(invoice => invoice.CostStatus, group.Key), ct);
        }

        // This derived cache is already saved in the same pull transaction.
        // Accept only CostStatus so the dirty-graph guard cannot mistake a
        // recalculation for a fresh user edit and advance UpdatedAtUtc.
        foreach (var entry in changed)
        {
            var property = entry.Property(invoice => invoice.CostStatus);
            property.OriginalValue = property.CurrentValue;
            property.IsModified = false;
        }
        return changed.Count;
    }

    private async Task<string> ComputeInventoryCostInputSignatureAsync(CancellationToken ct)
    {
        // Include chronology, upstream receipts, transfers, tracking and manual
        // adjustments. A sales invoice's own CostStatus cannot detect these changes.
        string[] queries =
        [
            "SELECT Id,TenantCode,OfficeCode,ResponsibleOfficeCode,VersionGroupId,VersionNumber,IsLatestVersion,IsDeleted,IsConfirmed,VoucherType,SourceWarehouseCode,InvoiceDate,CreatedAtUtc,LastSavedAtUtc,PurchaseReceivingStatus FROM Invoices ORDER BY Id",
            "SELECT Id,InvoiceId,ItemId,Quantity,UnitPrice,LineAmount,ItemTrackingType,SerialNumber,IsDeleted,OrderIndex FROM InvoiceLines ORDER BY Id",
            "SELECT Id,TenantCode,OfficeCode,IsDeleted,TrackingType,ItemKind,CategoryName,IsRental FROM Items ORDER BY Id",
            "SELECT Id,IsDeleted,TransferDate,CreatedAtUtc,LastSavedAtUtc,FromWarehouseCode,ToWarehouseCode,TransferStatus FROM InventoryTransfers ORDER BY Id",
            "SELECT Id,TransferId,ItemId,Quantity,ReceivedQuantity,IsDeleted FROM InventoryTransferLines ORDER BY Id",
            "SELECT Id,ItemId,WarehouseCode,MovementType,QuantityDelta,UnitCost,OccurredDate,CreatedAtUtc,IsActive FROM InventoryMovements WHERE MovementType IN ('StockAdjustmentManual','StockResetToZero') ORDER BY Id"
        ];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(
            OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(_session.OfficeCode, DomainConstants.OfficeUsenet)));
        var connection = _db.Database.GetDbConnection();
        foreach (var query in queries)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(query));
            await using var command = connection.CreateCommand();
            command.Transaction = _db.Database.CurrentTransaction!.GetDbTransaction();
            command.CommandText = query;
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var row = new object?[reader.FieldCount];
                for (var index = 0; index < row.Length; index++)
                    row[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);
                hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(row));
                hash.AppendData(new byte[] { 10 });
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
