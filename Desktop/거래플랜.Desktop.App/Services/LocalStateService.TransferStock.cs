using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

public sealed partial class LocalStateService
{
    public Task<OfficeMutationResult> SaveInventoryTransferAsync(LocalInventoryTransfer transfer, SessionState session, CancellationToken ct = default)
        => ExecuteTransferMutationAsync(() => SaveInventoryTransferCoreAsync(transfer, session, ct), ct);

    public Task<OfficeMutationResult> ConfirmInventoryTransferReceiptAsync(Guid transferId, IEnumerable<LocalInventoryTransferLine> receivedLines,
        string? receiveMemo, SessionState session, CancellationToken ct = default, long? expectedRevision = null)
        => ExecuteTransferMutationAsync(() => ConfirmInventoryTransferReceiptCoreAsync(transferId, receivedLines, receiveMemo, session, ct, expectedRevision), ct);

    public Task<OfficeMutationResult> DeleteInventoryTransferAsync(Guid transferId, SessionState session, long? expectedRevision = null, CancellationToken ct = default)
        => ExecuteTransferMutationAsync(() => DeleteInventoryTransferCoreAsync(transferId, session, expectedRevision, ct), ct);

    public Task<OfficeMutationResult> RejectInventoryTransferAsync(Guid transferId, string rejectReason, SessionState session, CancellationToken ct = default, long? expectedRevision = null)
        => ExecuteTransferMutationAsync(() => RejectInventoryTransferCoreAsync(transferId, rejectReason, session, ct, expectedRevision), ct);

    private async Task<OfficeMutationResult> ExecuteTransferMutationAsync(Func<Task<OfficeMutationResult>> mutation, CancellationToken ct)
    {
        if (_db.Database.CurrentTransaction is not null) return await mutation();
        await using var transaction = await _db.BeginRuntimeMutationTransactionAsync(ct);
        try
        {
            var result = await mutation();
            if (result.Success) await transaction.CommitAsync(ct);
            else await transaction.RollbackAsync(CancellationToken.None);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _db.ChangeTracker.Clear();
            throw;
        }
    }

    private async Task<Dictionary<(Guid ItemId, string WarehouseCode), decimal>> GetTransferInventoryEffectsAsync(
        Guid transferId, CancellationToken ct)
    {
        var transfer = await _db.InventoryTransfers.IgnoreQueryFilters().AsNoTracking()
            .Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == transferId, ct);
        if (transfer is not null) transfer.TransferStatus = NormalizeInventoryTransferStatus(transfer);
        var tracking = await BuildItemTrackingMapAsync(ct);
        var effects = BuildTransferStockDeltas(transfer, tracking)
            .ToDictionary(x => (x.Key.ItemId, x.Key.WarehouseCode), x => x.Value);
        if (transfer is not null)
            await ExcludeEffectsBeforeInventoryResetAsync(effects, transfer.TransferDate,
                transfer.LastSavedAtUtc == default ? transfer.CreatedAtUtc : transfer.LastSavedAtUtc, ct);
        return effects;
    }

    private async Task ExcludeEffectsBeforeInventoryResetAsync(Dictionary<(Guid ItemId, string WarehouseCode), decimal> effects,
        DateOnly documentDate, DateTime documentSavedAt, CancellationToken ct)
    {
        if (effects.Count == 0) return;
        var itemIds = effects.Keys.Select(x => x.ItemId).Distinct().ToList();
        var resets = await _db.InventoryMovements.AsNoTracking().Where(x => x.IsActive && x.ItemId.HasValue
            && itemIds.Contains(x.ItemId.Value) && x.MovementType == InventoryResetToZeroMovementType
            && (x.OccurredDate > documentDate || (x.OccurredDate == documentDate && x.CreatedAtUtc >= documentSavedAt)))
            .Select(x => new { x.ItemId, x.WarehouseCode }).ToListAsync(ct);
        // A reset later in the same inventory timeline supersedes prior documents.
        foreach (var reset in resets)
        {
            var warehouse = NormalizeWarehouseCode(reset.WarehouseCode,
                ResolveOfficeCodeFromWarehouseCode(reset.WarehouseCode), DomainConstants.OfficeUsenet);
            effects.Remove((reset.ItemId!.Value, warehouse));
        }
    }

    private async Task RebuildInventoryAfterTransferMutationAsync(
        Guid transferId,
        IReadOnlyDictionary<(Guid ItemId, string WarehouseCode), decimal> previousEffects,
        SessionState session,
        InvoiceSaveContext context,
        CancellationToken ct)
    {
        var currentEffects = await GetTransferInventoryEffectsAsync(transferId, ct);
        await ApplyInventoryDocumentEffectsAsync(previousEffects, currentEffects, session, context, ct);
    }

    private Task ApplyInventoryDocumentEffectsAsync(
        IReadOnlyDictionary<(Guid ItemId, string WarehouseCode), decimal> previousEffects,
        IReadOnlyDictionary<(Guid ItemId, string WarehouseCode), decimal> currentEffects,
        SessionState? session, InvoiceSaveContext context, CancellationToken ct)
        => _db.ExecuteRuntimeMutationOperationAsync(async () =>
        {
            var changedItemIds = new HashSet<Guid>();
            // The local document history can be partial. Keep the server stock baseline
            // and apply only this document's delta; never send an item-master edit for it.
            foreach (var key in previousEffects.Keys.Union(currentEffects.Keys))
            {
                var delta = currentEffects.GetValueOrDefault(key) - previousEffects.GetValueOrDefault(key);
                if (delta == 0m) continue;
                var stock = await _db.ItemWarehouseStocks.SingleOrDefaultAsync(x => x.ItemId == key.ItemId && x.WarehouseCode == key.WarehouseCode, ct);
                if (stock is null)
                {
                    // Do not invent a source warehouse snapshot on a destination-only client.
                    if (session is not null && !CanWriteOfficeScope(session, ResolveOfficeCodeFromWarehouseCode(key.WarehouseCode))) continue;
                    stock = new LocalItemWarehouseStock { ItemId = key.ItemId, WarehouseCode = key.WarehouseCode };
                    _db.ItemWarehouseStocks.Add(stock);
                }
                stock.Quantity += delta;
                stock.UpdatedAtUtc = DateTime.UtcNow;
                changedItemIds.Add(key.ItemId);
            }
            await _db.SaveChangesAsync(ct);
            foreach (var itemId in changedItemIds)
            {
                var item = await _db.Items.AsNoTracking().SingleOrDefaultAsync(x => x.Id == itemId, ct);
                if (item is null || !CanWriteItemScope(item, session ?? _session)) continue;
                var quantities = await _db.ItemWarehouseStocks.Where(x => x.ItemId == itemId).Select(x => x.Quantity).ToListAsync(ct);
                var total = quantities.Sum();
                // Refresh the owned item's local display cache without creating a master
                // mutation or touching its revision/timestamp. Received masters stay untouched.
                await _db.Items.Where(x => x.Id == itemId)
                    .ExecuteUpdateAsync(update => update.SetProperty(x => x.CurrentStock, total), ct);
            }
            await RebuildInventorySnapshotsCoreAsync(context, ct, preserveAuthoritativeStock: true);
            RaiseInventoryStateChanged();
        }, ct);
}
