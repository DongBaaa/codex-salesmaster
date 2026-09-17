using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;

namespace 거래플랜.Desktop.App.Services;

public sealed partial class LocalStateService
{
    private sealed record DeletedItemInventoryBaseline(Guid ItemId, string TenantCode, string OfficeCode,
        decimal CurrentStock, List<LocalItemWarehouseStock> Stocks);

    private static string DeletedItemInventoryKey(Guid itemId) => $"Inventory.DeletedItemBaseline.{itemId:D}";

    public Task DeleteItemAsync(Guid id, long? expectedRevision = null, CancellationToken ct = default)
        => ExecuteInvoiceStockMutationAsync(async () =>
        {
            await DeleteItemCoreAsync(id, expectedRevision, ct);
            return true;
        }, result => result, ct);

    public Task<OfficeMutationResult> DeleteItemAsync(Guid id, SessionState session, long? expectedRevision = null, CancellationToken ct = default)
        => ExecuteInvoiceStockMutationAsync(() => DeleteItemCoreAsync(id, session, expectedRevision, ct), result => result.Success, ct);

    private async Task PreserveDeletedItemInventoryAsync(LocalItem item, CancellationToken ct)
    {
        var stocks = await _db.ItemWarehouseStocks.AsNoTracking().Where(x => x.ItemId == item.Id).ToListAsync(ct);
        var key = DeletedItemInventoryKey(item.Id);
        var setting = await _db.Settings.SingleOrDefaultAsync(x => x.Key == key, ct);
        if (setting is null)
        {
            setting = new LocalSetting { Key = key };
            _db.Settings.Add(setting);
        }
        setting.Value = JsonSerializer.Serialize(new DeletedItemInventoryBaseline(
            item.Id, item.TenantCode, item.OfficeCode, item.CurrentStock, stocks));
        // The caller saves this baseline and the deletion in the same transaction.
    }

    private async Task RestoreDeletedItemInventoryAsync(LocalItem item, CancellationToken ct)
    {
        var key = DeletedItemInventoryKey(item.Id);
        var setting = await _db.Settings.SingleOrDefaultAsync(x => x.Key == key, ct);
        if (setting is null) return;
        var baseline = JsonSerializer.Deserialize<DeletedItemInventoryBaseline>(setting.Value)
            ?? throw new InvalidOperationException("삭제 전 품목 재고 보존 정보를 읽을 수 없습니다.");
        if (baseline.ItemId != item.Id || baseline.TenantCode != item.TenantCode || baseline.OfficeCode != item.OfficeCode ||
            baseline.Stocks is null || baseline.Stocks.Any(x => x.ItemId != item.Id) ||
            baseline.Stocks.Select(x => x.WarehouseCode).Distinct(StringComparer.OrdinalIgnoreCase).Count() != baseline.Stocks.Count)
            throw new InvalidOperationException("삭제 전 품목 재고 보존 정보의 범위가 일치하지 않습니다.");

        // A newer server snapshot takes precedence. Otherwise restore the exact
        // deleted baseline, including revisions, without replaying partial history.
        if (!await _db.ItemWarehouseStocks.AnyAsync(x => x.ItemId == item.Id, ct))
        {
            _db.ItemWarehouseStocks.AddRange(baseline.Stocks);
            item.CurrentStock = baseline.CurrentStock;
        }
        _db.Settings.Remove(setting);
    }

    private async Task RemoveDeletedItemInventoryAsync(Guid itemId, CancellationToken ct)
    {
        var key = DeletedItemInventoryKey(itemId);
        var setting = await _db.Settings.SingleOrDefaultAsync(x => x.Key == key, ct);
        if (setting is not null) _db.Settings.Remove(setting);
    }
}
