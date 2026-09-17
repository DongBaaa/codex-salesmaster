using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;

namespace 거래플랜.Desktop.App.Services;

public sealed partial class LocalStateService
{
    public Task<OfficeMutationResult> RestoreItemAsync(Guid id, SessionState session, CancellationToken ct = default)
        => ExecuteInvoiceStockMutationAsync(() => RestoreItemCoreAsync(id, session, ct), result => result.Success, ct);

    public Task<OfficeMutationResult> PermanentlyDeleteItemAsync(Guid id, SessionState session, CancellationToken ct = default)
        => ExecuteInvoiceStockMutationAsync(() => PermanentlyDeleteItemCoreAsync(id, session, ct), result => result.Success, ct);

    private Task<OfficeMutationResult> RestoreInventoryTransferAsync(Guid id, SessionState session, CancellationToken ct)
        => ExecuteTransferMutationAsync(() => RestoreInventoryTransferCoreAsync(id, session, ct), ct);

    private Task RefreshInventoryDerivedStateAsync(InvoiceSaveContext context, CancellationToken ct)
        => ExecuteInvoiceStockMutationAsync(async () =>
        {
            await _db.ExecuteRuntimeMutationOperationAsync(() => RebuildInventorySnapshotsCoreAsync(context, ct, preserveAuthoritativeStock: true), ct);
            return true;
        }, result => result, ct);

    public Task<OfficeMutationResult> RestoreInvoiceAsync(Guid id, SessionState session, CancellationToken ct = default)
        => ExecuteInvoiceStockMutationAsync(() => RestoreInvoiceCoreAsync(id, session, ct), result => result.Success, ct);

    public Task<OfficeMutationResult> PermanentlyDeleteInvoiceAsync(Guid id, SessionState session, CancellationToken ct = default)
        => ExecuteInvoiceStockMutationAsync(() => PermanentlyDeleteInvoiceCoreAsync(id, session, ct), result => result.Success, ct);

    public Task<OfficeMutationResult> RestoreDeletedPaymentAsync(Guid id, SessionState session, CancellationToken ct = default)
        => ExecuteInvoiceStockMutationAsync(() => RestoreDeletedPaymentCoreAsync(id, session, ct), result => result.Success, ct);

    public Task<OfficeMutationResult> RestoreTransactionAsync(Guid id, SessionState session, CancellationToken ct = default)
        => ExecuteInvoiceStockMutationAsync(() => RestoreTransactionCoreAsync(id, session, ct), result => result.Success, ct);

    internal Task<InvoiceSaveResult> SaveInvoiceAsync(LocalInvoice invoice, InvoiceSaveContext context,
        SessionState? session, CancellationToken ct, bool skipRentalSettlementRecalculation)
        => ExecuteInvoiceStockMutationAsync(() => SaveInvoiceCoreAsync(invoice, context, session, ct, skipRentalSettlementRecalculation), result => result.Success, ct);

    public Task DeleteInvoiceAsync(Guid id, CancellationToken ct = default)
        => ExecuteInvoiceStockMutationAsync(async () => { await DeleteInvoiceCoreAsync(id, ct); return true; }, result => result, ct);

    internal Task<OfficeMutationResult> DeleteInvoiceAsync(Guid id, SessionState session, long? expectedRevision,
        CancellationToken ct, bool skipRentalSettlementRecalculation)
        => ExecuteInvoiceStockMutationAsync(() => DeleteInvoiceCoreAsync(id, session, expectedRevision, ct, skipRentalSettlementRecalculation), result => result.Success, ct);

    private async Task<T> ExecuteInvoiceStockMutationAsync<T>(Func<Task<T>> mutation, Func<T, bool> succeeded, CancellationToken ct)
    {
        if (_db.Database.CurrentTransaction is not null) return await mutation();
        await using var transaction = await _db.BeginRuntimeMutationTransactionAsync(ct);
        try
        {
            var result = await mutation();
            if (succeeded(result)) await transaction.CommitAsync(ct);
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

    private async Task<Dictionary<(Guid ItemId, string WarehouseCode), decimal>> GetInvoiceInventoryEffectsAsync(LocalInvoice anchor, CancellationToken ct)
    {
        // IDs alone are not a tenant/office boundary: use the existing exact version scope.
        var ids = (await LoadExactInvoiceVersionChainAsync(anchor, asNoTracking: true, ct)).Select(x => x.Id).ToList();
        var invoices = await _db.Invoices.IgnoreQueryFilters().AsNoTracking().Include(x => x.Lines)
            .Where(x => ids.Contains(x.Id) && x.IsConfirmed && !x.IsDeleted && x.IsLatestVersion).ToListAsync(ct);
        var tracking = await BuildItemTrackingMapAsync(ct);
        var total = new Dictionary<(Guid ItemId, string WarehouseCode), decimal>();
        foreach (var invoice in invoices)
        {
            var effects = BuildInvoiceStockDeltas(invoice, tracking)
                .ToDictionary(x => (x.Key.ItemId, x.Key.WarehouseCode), x => x.Value);
            await ExcludeEffectsBeforeInventoryResetAsync(effects, invoice.InvoiceDate,
                invoice.LastSavedAtUtc == default ? invoice.CreatedAtUtc : invoice.LastSavedAtUtc, ct);
            foreach (var effect in effects) total[effect.Key] = total.GetValueOrDefault(effect.Key) + effect.Value;
        }
        return total;
    }

    private async Task RebuildInventoryAfterInvoiceMutationAsync(LocalInvoice anchor,
        IReadOnlyDictionary<(Guid ItemId, string WarehouseCode), decimal> previousEffects,
        SessionState? session, InvoiceSaveContext context, CancellationToken ct)
    {
        var currentEffects = await GetInvoiceInventoryEffectsAsync(anchor, ct);
        await ApplyInventoryDocumentEffectsAsync(previousEffects, currentEffects, session, context, ct);
    }
}
