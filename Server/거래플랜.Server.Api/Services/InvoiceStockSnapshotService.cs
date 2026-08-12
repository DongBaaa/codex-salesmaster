using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace 거래플랜.Server.Api.Services;

public sealed class InvoiceStockSnapshotService
{
    private readonly AppDbContext _dbContext;

    public InvoiceStockSnapshotService(AppDbContext dbContext, RevisionClock revisionClock)
    {
        _dbContext = dbContext;
        ArgumentNullException.ThrowIfNull(revisionClock);
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
        if (invoice.VoucherType == VoucherType.Purchase &&
            !InvoiceReceivingStatuses.IsConfirmed(invoice.PurchaseReceivingStatus))
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
                !ItemOperationalPolicy.SupportsInventory(itemTrackingType) ||
                !ItemOperationalPolicy.SupportsInventory(line.ItemTrackingType))
            {
                continue;
            }

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

    public async Task<IReadOnlyDictionary<InvoiceStockKey, decimal>> BuildInventoryTransferStockDeltasAsync(
        InventoryTransfer? transfer,
        CancellationToken cancellationToken = default)
    {
        var deltas = new Dictionary<InvoiceStockKey, decimal>();
        if (transfer is null || transfer.IsDeleted)
            return deltas;

        var normalizedStatus = InventoryTransferStatusNormalizer.Normalize(
            transfer.TransferStatus,
            transfer.ReceivedByUsername,
            transfer.ReceivedAtUtc,
            transfer.RejectedByUsername,
            transfer.RejectedAtUtc);
        if (string.Equals(normalizedStatus, InventoryTransferStatusNormalizer.Rejected, StringComparison.Ordinal))
            return deltas;

        var fromWarehouseCode = OfficeCodeCatalog.NormalizeWarehouseCodeOrDefault(
            transfer.FromWarehouseCode,
            transfer.SourceOfficeCode,
            transfer.SourceOfficeCode);
        var toWarehouseCode = OfficeCodeCatalog.NormalizeWarehouseCodeOrDefault(
            transfer.ToWarehouseCode,
            transfer.TargetOfficeCode,
            transfer.TargetOfficeCode);
        if (string.Equals(fromWarehouseCode, toWarehouseCode, StringComparison.OrdinalIgnoreCase))
            return deltas;

        var candidateItemIds = transfer.Lines
            .Where(line => !line.IsDeleted && line.ItemId.HasValue && line.ItemId.Value != Guid.Empty && line.Quantity > 0m)
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

        var isReceived = string.Equals(normalizedStatus, InventoryTransferStatusNormalizer.Received, StringComparison.Ordinal);
        foreach (var line in transfer.Lines.Where(line => !line.IsDeleted && line.ItemId.HasValue && line.ItemId.Value != Guid.Empty && line.Quantity > 0m))
        {
            var itemId = line.ItemId!.Value;
            if (!itemTrackingMap.TryGetValue(itemId, out var itemTrackingType) ||
                !ItemOperationalPolicy.SupportsInventory(itemTrackingType))
            {
                continue;
            }

            var transferQuantity = line.Quantity;
            AddStockDelta(deltas, new InvoiceStockKey(itemId, fromWarehouseCode), -transferQuantity);
            if (!isReceived)
                continue;

            var receivedQuantity = Math.Min(transferQuantity, Math.Max(0m, line.ReceivedQuantity ?? line.Quantity));
            if (receivedQuantity > 0m)
                AddStockDelta(deltas, new InvoiceStockKey(itemId, toWarehouseCode), receivedQuantity);
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

            var trackedStockEntries = _dbContext.ChangeTracker
                .Entries<ItemWarehouseStock>()
                .Where(entry =>
                    entry.Entity.ItemId == key.ItemId &&
                    string.Equals(
                        entry.Entity.WarehouseCode,
                        key.WarehouseCode,
                        StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (trackedStockEntries.Any(entry => entry.State == EntityState.Deleted))
            {
                throw new InvalidOperationException(
                    "Cannot apply a stock snapshot delta while a matching warehouse stock is pending deletion.");
            }

            var matchingStocks = stocks
                .Where(row =>
                    row.ItemId == key.ItemId &&
                    string.Equals(row.WarehouseCode, key.WarehouseCode, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var trackedStock in trackedStockEntries.Select(entry => entry.Entity))
            {
                if (matchingStocks.All(stock => !ReferenceEquals(stock, trackedStock)))
                    matchingStocks.Add(trackedStock);
            }

            if (matchingStocks.Count > 1)
            {
                throw new InvalidOperationException(
                    "Cannot apply a stock snapshot delta because multiple physical warehouse stocks match the same logical key.");
            }

            var stock = matchingStocks.SingleOrDefault();
            if (stock is null)
            {
                stock = new ItemWarehouseStock
                {
                    ItemId = key.ItemId,
                    WarehouseCode = key.WarehouseCode,
                    Quantity = 0m,
                    UpdatedAtUtc = now
                };
                _dbContext.ItemWarehouseStocks.Add(stock);
                stocks.Add(stock);
            }

            stock.Quantity += delta;
            stock.UpdatedAtUtc = now;
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

    public Task<IReadOnlyList<InvoiceStockShortage>> FindStockShortagesAsync(
        IReadOnlyDictionary<InvoiceStockKey, decimal> previous,
        IReadOnlyDictionary<InvoiceStockKey, decimal> current,
        CancellationToken cancellationToken = default)
        => FindStockShortagesAsync(
            previous,
            current,
            new Dictionary<InvoiceStockKey, decimal>(),
            cancellationToken);

    public async Task<IReadOnlyList<InvoiceStockShortage>> FindStockShortagesAsync(
        IReadOnlyDictionary<InvoiceStockKey, decimal> previous,
        IReadOnlyDictionary<InvoiceStockKey, decimal> current,
        IReadOnlyDictionary<InvoiceStockKey, decimal> currentQuantityOverrides,
        CancellationToken cancellationToken = default)
    {
        var keys = previous.Keys.Concat(current.Keys).Distinct().ToList();
        if (keys.Count == 0)
            return [];

        var appliedDeltas = keys
            .Select(key =>
            {
                previous.TryGetValue(key, out var previousQuantity);
                current.TryGetValue(key, out var currentQuantity);
                return new
                {
                    Key = key,
                    Delta = currentQuantity - previousQuantity
                };
            })
            .Where(row => row.Delta < 0m)
            .ToList();
        if (appliedDeltas.Count == 0)
            return [];

        var itemIds = appliedDeltas.Select(row => row.Key.ItemId).Distinct().ToList();
        var items = await _dbContext.Items
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item => itemIds.Contains(item.Id) && !item.IsDeleted)
            .Select(item => new
            {
                item.Id,
                item.NameOriginal,
                item.SpecificationOriginal,
                item.TrackingType
            })
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var stocks = await _dbContext.ItemWarehouseStocks
            .Where(stock => itemIds.Contains(stock.ItemId))
            .ToListAsync(cancellationToken);
        stocks.RemoveAll(stock => _dbContext.Entry(stock).State == EntityState.Deleted);
        foreach (var localStock in _dbContext.ItemWarehouseStocks.Local.Where(stock => itemIds.Contains(stock.ItemId)))
        {
            if (stocks.All(stock =>
                    stock.ItemId != localStock.ItemId ||
                    !string.Equals(stock.WarehouseCode, localStock.WarehouseCode, StringComparison.OrdinalIgnoreCase)))
            {
                stocks.Add(localStock);
            }
        }

        var shortages = new List<InvoiceStockShortage>();
        foreach (var row in appliedDeltas)
        {
            if (!items.TryGetValue(row.Key.ItemId, out var item) ||
                !ItemOperationalPolicy.SupportsInventory(item.TrackingType))
            {
                continue;
            }

            var currentQuantity =
                currentQuantityOverrides.TryGetValue(
                    row.Key,
                    out var currentQuantityOverride)
                    ? currentQuantityOverride
                    : stocks
                        .Where(stock =>
                            stock.ItemId == row.Key.ItemId &&
                            string.Equals(stock.WarehouseCode, row.Key.WarehouseCode, StringComparison.OrdinalIgnoreCase))
                        .Select(stock => stock.Quantity)
                        .DefaultIfEmpty(0m)
                        .Sum();
            var finalQuantity = currentQuantity + row.Delta;
            if (finalQuantity >= 0m)
                continue;

            shortages.Add(new InvoiceStockShortage(
                row.Key.ItemId,
                row.Key.WarehouseCode,
                item.NameOriginal,
                item.SpecificationOriginal,
                currentQuantity,
                Math.Abs(row.Delta),
                Math.Abs(finalQuantity)));
        }

        return shortages;
    }

    public static string FormatStockShortageMessage(IReadOnlyList<InvoiceStockShortage> shortages)
    {
        if (shortages.Count == 0)
            return string.Empty;

        var firstRows = shortages.Take(3).Select(shortage =>
            $"{shortage.ItemName} / 창고 {shortage.WarehouseCode} / 현재 {shortage.CurrentQuantity:N0} / 차감 {shortage.RequestedDecrease:N0} / 부족 {shortage.ShortageQuantity:N0}");
        var suffix = shortages.Count > 3 ? $" 외 {shortages.Count - 3:N0}건" : string.Empty;
        return "재고가 부족하여 판매/전표 변경을 저장할 수 없습니다. " + string.Join("; ", firstRows) + suffix;
    }

    private static string ResolveInvoiceWarehouseCode(Invoice invoice)
        => OfficeCodeCatalog.NormalizeWarehouseCodeOrDefault(
            invoice.SourceWarehouseCode,
            invoice.ResponsibleOfficeCode,
            invoice.OfficeCode);

    private static void AddStockDelta(
        IDictionary<InvoiceStockKey, decimal> deltas,
        InvoiceStockKey key,
        decimal quantity)
    {
        if (quantity == 0m)
            return;

        deltas[key] = deltas.TryGetValue(key, out var current)
            ? current + quantity
            : quantity;
    }

    public readonly record struct InvoiceStockKey(Guid ItemId, string WarehouseCode);

    public sealed record InvoiceStockShortage(
        Guid ItemId,
        string WarehouseCode,
        string ItemName,
        string Specification,
        decimal CurrentQuantity,
        decimal RequestedDecrease,
        decimal ShortageQuantity);
}
