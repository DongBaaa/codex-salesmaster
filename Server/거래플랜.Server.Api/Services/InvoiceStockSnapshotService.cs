using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace 거래플랜.Server.Api.Services;

public sealed class InvoiceStockSnapshotService
{
    private readonly AppDbContext _dbContext;
    private readonly RevisionClock _revisionClock;

    public InvoiceStockSnapshotService(AppDbContext dbContext, RevisionClock revisionClock)
    {
        _dbContext = dbContext;
        _revisionClock = revisionClock;
    }

    public async Task<IReadOnlyDictionary<InvoiceStockKey, decimal>> BuildInvoiceStockDeltasAsync(
        Invoice? invoice,
        CancellationToken cancellationToken = default)
    {
        var deltas = new Dictionary<InvoiceStockKey, decimal>();
        if (invoice is null || invoice.IsDeleted || !invoice.IsLatestVersion)
            return deltas;
        if (invoice.VoucherType is not (VoucherType.Sales or VoucherType.Purchase or VoucherType.Procurement))
            return deltas;

        var candidateItemIds = invoice.Lines
            .Where(line => !line.IsDeleted && line.ItemId.HasValue && line.ItemId.Value != Guid.Empty && line.Quantity != 0m)
            .Select(line => line.ItemId!.Value)
            .Distinct()
            .ToList();
        if (candidateItemIds.Count == 0)
            return deltas;

        var itemTrackingMap = await _dbContext.Items
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item => candidateItemIds.Contains(item.Id) && !item.IsDeleted)
            .Select(item => new { item.Id, item.TrackingType })
            .ToDictionaryAsync(item => item.Id, item => item.TrackingType, cancellationToken);

        var warehouseCode = ResolveInvoiceWarehouseCode(invoice);
        foreach (var line in invoice.Lines.Where(line => !line.IsDeleted && line.ItemId.HasValue && line.ItemId.Value != Guid.Empty && line.Quantity != 0m))
        {
            var itemId = line.ItemId!.Value;
            if (!itemTrackingMap.TryGetValue(itemId, out var itemTrackingType) ||
                !ItemOperationalPolicy.SupportsInventory(itemTrackingType))
            {
                continue;
            }

            var lineTrackingType = ItemTrackingTypes.Normalize(
                line.ItemTrackingType,
                itemId == Guid.Empty ? ItemTrackingTypes.NonStock : itemTrackingType);
            if (!ItemOperationalPolicy.SupportsInventory(lineTrackingType))
                continue;

            var quantity = Math.Abs(line.Quantity);
            var signedQuantity = invoice.VoucherType == VoucherType.Sales ? -quantity : quantity;
            if (signedQuantity == 0m)
                continue;

            var key = new InvoiceStockKey(itemId, warehouseCode);
            deltas[key] = deltas.TryGetValue(key, out var current)
                ? current + signedQuantity
                : signedQuantity;
        }

        return deltas;
    }

    public async Task ApplyInvoiceStockDeltaDifferenceAsync(
        IReadOnlyDictionary<InvoiceStockKey, decimal> previous,
        IReadOnlyDictionary<InvoiceStockKey, decimal> current,
        CancellationToken cancellationToken = default)
    {
        var keys = previous.Keys.Concat(current.Keys).Distinct().ToList();
        if (keys.Count == 0)
            return;

        var itemIds = keys.Select(key => key.ItemId).Distinct().ToList();
        var stocks = await _dbContext.ItemWarehouseStocks
            .Where(stock => itemIds.Contains(stock.ItemId))
            .ToListAsync(cancellationToken);
        var items = await _dbContext.Items
            .IgnoreQueryFilters()
            .Where(item => itemIds.Contains(item.Id) && !item.IsDeleted)
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var itemDeltaTotals = new Dictionary<Guid, decimal>();
        var now = DateTime.UtcNow;

        foreach (var key in keys)
        {
            previous.TryGetValue(key, out var previousQuantity);
            current.TryGetValue(key, out var currentQuantity);
            var delta = currentQuantity - previousQuantity;
            if (delta == 0m)
                continue;

            if (!items.ContainsKey(key.ItemId))
                continue;

            var stock = stocks.FirstOrDefault(row =>
                row.ItemId == key.ItemId &&
                string.Equals(row.WarehouseCode, key.WarehouseCode, StringComparison.OrdinalIgnoreCase));
            if (stock is null)
            {
                stock = new ItemWarehouseStock
                {
                    ItemId = key.ItemId,
                    WarehouseCode = key.WarehouseCode,
                    Quantity = 0m,
                    UpdatedAtUtc = now,
                    Revision = _revisionClock.NextRevision()
                };
                _dbContext.ItemWarehouseStocks.Add(stock);
                stocks.Add(stock);
            }

            stock.Quantity += delta;
            stock.UpdatedAtUtc = now;
            stock.Revision = _revisionClock.NextRevision();
            itemDeltaTotals[key.ItemId] = itemDeltaTotals.TryGetValue(key.ItemId, out var itemDelta)
                ? itemDelta + delta
                : delta;
        }

        foreach (var (itemId, delta) in itemDeltaTotals)
        {
            if (!items.TryGetValue(itemId, out var item))
                continue;

            item.CurrentStock += delta;
            item.UpdatedAtUtc = now;
        }
    }

    private static string ResolveInvoiceWarehouseCode(Invoice invoice)
        => OfficeCodeCatalog.NormalizeWarehouseCodeOrDefault(
            invoice.SourceWarehouseCode,
            invoice.ResponsibleOfficeCode,
            invoice.OfficeCode);

    public readonly record struct InvoiceStockKey(Guid ItemId, string WarehouseCode);
}
