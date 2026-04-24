using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

public sealed record MissingItemMasterRepairResult(
    int CreatedCount,
    int RecoveredDeletedCount,
    int SkippedOutOfScopeCount,
    int UnresolvedReferenceCount,
    IReadOnlyList<string> RepairedItemNames)
{
    public int RepairedCount => CreatedCount + RecoveredDeletedCount;
    public bool HasChanges => RepairedCount > 0;
}

public sealed partial class LocalStateService
{
    public async Task<MissingItemMasterRepairResult> RepairMissingItemMastersFromOperationalReferencesAsync(
        SessionState session,
        CancellationToken ct = default)
    {
        if (!CanEditItems(session))
            return new MissingItemMasterRepairResult(0, 0, 0, 0, []);

        var activeItemIds = (await _db.Items
                .IgnoreQueryFilters()
                .Where(item => !item.IsDeleted)
                .Select(item => item.Id)
                .ToListAsync(ct))
            .ToHashSet();

        var deletedItemIds = (await _db.Items
                .IgnoreQueryFilters()
                .Where(item => item.IsDeleted)
                .Select(item => item.Id)
                .ToListAsync(ct))
            .ToHashSet();

        var invoiceRows = await (
                from line in _db.InvoiceLines.IgnoreQueryFilters().AsNoTracking()
                join invoice in _db.Invoices.IgnoreQueryFilters().AsNoTracking()
                    on line.InvoiceId equals invoice.Id
                where !line.IsDeleted &&
                      !invoice.IsDeleted &&
                      line.ItemId.HasValue &&
                      line.ItemId.Value != Guid.Empty &&
                      !string.IsNullOrWhiteSpace(line.ItemNameOriginal)
                select new InvoiceItemReferenceRow(
                    line.ItemId!.Value,
                    line.ItemNameOriginal,
                    line.SpecificationOriginal,
                    line.Unit,
                    line.ItemTrackingType,
                    line.UnitPrice,
                    invoice.VoucherType,
                    invoice.InvoiceDate,
                    invoice.TenantCode,
                    invoice.ResponsibleOfficeCode,
                    invoice.OfficeCode))
            .ToListAsync(ct);

        var transferRows = await (
                from line in _db.InventoryTransferLines.IgnoreQueryFilters().AsNoTracking()
                join transfer in _db.InventoryTransfers.IgnoreQueryFilters().AsNoTracking()
                    on line.TransferId equals transfer.Id
                where !line.IsDeleted &&
                      !transfer.IsDeleted &&
                      line.ItemId.HasValue &&
                      line.ItemId.Value != Guid.Empty &&
                      !string.IsNullOrWhiteSpace(line.ItemNameOriginal)
                select new TransferItemReferenceRow(
                    line.ItemId!.Value,
                    line.ItemNameOriginal,
                    line.SpecificationOriginal,
                    line.Unit,
                    transfer.FromWarehouseCode,
                    transfer.TransferDate))
            .ToListAsync(ct);

        var referencedItemIds = invoiceRows.Select(row => row.ItemId).ToHashSet();
        foreach (var itemId in transferRows.Select(row => row.ItemId))
            referencedItemIds.Add(itemId);

        foreach (var itemId in await _db.ItemWarehouseStocks.AsNoTracking().Select(stock => stock.ItemId).ToListAsync(ct))
            referencedItemIds.Add(itemId);

        foreach (var itemId in await _db.InventoryMovements.AsNoTracking()
                     .Where(movement => movement.ItemId.HasValue && movement.ItemId.Value != Guid.Empty)
                     .Select(movement => movement.ItemId!.Value)
                     .ToListAsync(ct))
        {
            referencedItemIds.Add(itemId);
        }

        foreach (var itemId in await _db.StockLayers.AsNoTracking()
                     .Where(layer => layer.ItemId.HasValue && layer.ItemId.Value != Guid.Empty)
                     .Select(layer => layer.ItemId!.Value)
                     .ToListAsync(ct))
        {
            referencedItemIds.Add(itemId);
        }

        foreach (var itemId in await _db.InvoiceLineSerials.AsNoTracking()
                     .Where(serial => serial.ItemId.HasValue && serial.ItemId.Value != Guid.Empty)
                     .Select(serial => serial.ItemId!.Value)
                     .ToListAsync(ct))
        {
            referencedItemIds.Add(itemId);
        }

        foreach (var itemId in await _db.SerialLedgers.AsNoTracking()
                     .Where(ledger => ledger.ItemId.HasValue && ledger.ItemId.Value != Guid.Empty)
                     .Select(ledger => ledger.ItemId!.Value)
                     .ToListAsync(ct))
        {
            referencedItemIds.Add(itemId);
        }

        var candidates = invoiceRows
            .Where(row => !activeItemIds.Contains(row.ItemId))
            .GroupBy(row => row.ItemId)
            .Select(BuildCandidateFromInvoiceRows)
            .ToDictionary(candidate => candidate.ItemId);

        foreach (var transferCandidate in transferRows
                     .Where(row => !activeItemIds.Contains(row.ItemId) && !candidates.ContainsKey(row.ItemId))
                     .GroupBy(row => row.ItemId)
                     .Select(BuildCandidateFromTransferRows))
        {
            candidates[transferCandidate.ItemId] = transferCandidate;
        }

        var stockTotals = await _db.ItemWarehouseStocks
            .AsNoTracking()
            .GroupBy(stock => stock.ItemId)
            .Select(group => new { ItemId = group.Key, Quantity = group.Sum(stock => stock.Quantity) })
            .ToDictionaryAsync(row => row.ItemId, row => row.Quantity, ct);

        var movementTotals = await _db.InventoryMovements
            .AsNoTracking()
            .Where(movement => movement.ItemId.HasValue)
            .GroupBy(movement => movement.ItemId!.Value)
            .Select(group => new { ItemId = group.Key, Quantity = group.Sum(movement => movement.QuantityDelta) })
            .ToDictionaryAsync(row => row.ItemId, row => row.Quantity, ct);

        var missingReferenceIds = referencedItemIds
            .Where(itemId => !activeItemIds.Contains(itemId))
            .ToList();
        var unresolvedReferenceCount = missingReferenceIds.Count(itemId => !candidates.ContainsKey(itemId));

        var createdCount = 0;
        var recoveredDeletedCount = 0;
        var skippedOutOfScopeCount = 0;
        var repairedNames = new List<string>();

        foreach (var itemId in missingReferenceIds.Where(candidates.ContainsKey).Distinct())
        {
            var candidate = candidates[itemId];
            var currentStock = stockTotals.TryGetValue(itemId, out var stockQuantity)
                ? stockQuantity
                : movementTotals.GetValueOrDefault(itemId);

            var item = BuildRecoveredItem(candidate, currentStock);

            try
            {
                await UpsertItemAsync(item, session, candidate.OfficeCode, ct);
            }
            catch (UnauthorizedAccessException)
            {
                skippedOutOfScopeCount++;
                continue;
            }

            activeItemIds.Add(itemId);
            if (deletedItemIds.Contains(itemId))
                recoveredDeletedCount++;
            else
                createdCount++;

            repairedNames.Add(item.NameOriginal);
        }

        return new MissingItemMasterRepairResult(
            createdCount,
            recoveredDeletedCount,
            skippedOutOfScopeCount,
            unresolvedReferenceCount,
            repairedNames
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .Take(20)
                .ToList());
    }

    private static MissingItemReferenceCandidate BuildCandidateFromInvoiceRows(
        IGrouping<Guid, InvoiceItemReferenceRow> group)
    {
        var rows = group.ToList();
        var latestRow = rows
            .OrderByDescending(row => row.InvoiceDate)
            .ThenByDescending(row => row.UnitPrice)
            .First();
        var normalizedTrackingType = ItemTrackingTypes.Normalize(PickMostCommon(rows.Select(row => row.TrackingType)));
        var preferredOfficeCode = PickMostCommon(rows.Select(row => row.ResponsibleOfficeCode))
                                  ?? PickMostCommon(rows.Select(row => row.OfficeCode))
                                  ?? DomainConstants.OfficeUsenet;
        var officeCode = NormalizeOfficeCode(preferredOfficeCode, DomainConstants.OfficeUsenet);
        var tenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            PickMostCommon(rows.Select(row => row.TenantCode)),
            officeCode);

        var lastPurchaseRow = rows
            .Where(row => row.VoucherType is VoucherType.Purchase or VoucherType.Procurement && row.UnitPrice > 0)
            .OrderByDescending(row => row.InvoiceDate)
            .FirstOrDefault();
        var lastSaleRow = rows
            .Where(row => row.VoucherType == VoucherType.Sales && row.UnitPrice > 0)
            .OrderByDescending(row => row.InvoiceDate)
            .FirstOrDefault();

        return new MissingItemReferenceCandidate(
            group.Key,
            PickMostCommon(rows.Select(row => row.ItemName)) ?? latestRow.ItemName,
            PickMostCommon(rows.Select(row => row.Specification)) ?? latestRow.Specification,
            PickMostCommon(rows.Select(row => row.Unit)) ?? latestRow.Unit,
            normalizedTrackingType,
            ResolveItemKind(normalizedTrackingType),
            InferRecoveredItemCategoryName(PickMostCommon(rows.Select(row => row.ItemName)) ?? latestRow.ItemName),
            officeCode,
            tenantCode,
            lastPurchaseRow?.UnitPrice ?? 0m,
            lastSaleRow?.UnitPrice ?? 0m,
            0m,
            lastPurchaseRow?.InvoiceDate,
            lastSaleRow?.InvoiceDate);
    }

    private static MissingItemReferenceCandidate BuildCandidateFromTransferRows(
        IGrouping<Guid, TransferItemReferenceRow> group)
    {
        var rows = group.ToList();
        var latestRow = rows
            .OrderByDescending(row => row.TransferDate)
            .First();
        var officeCode = ResolveOfficeCodeFromWarehouseCode(latestRow.FromWarehouseCode);
        var tenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(null, officeCode);

        return new MissingItemReferenceCandidate(
            group.Key,
            PickMostCommon(rows.Select(row => row.ItemName)) ?? latestRow.ItemName,
            PickMostCommon(rows.Select(row => row.Specification)) ?? latestRow.Specification,
            PickMostCommon(rows.Select(row => row.Unit)) ?? latestRow.Unit,
            ItemTrackingTypes.Stock,
            ItemKinds.Product,
            InferRecoveredItemCategoryName(PickMostCommon(rows.Select(row => row.ItemName)) ?? latestRow.ItemName),
            officeCode,
            tenantCode,
            0m,
            0m,
            0m,
            null,
            null);
    }

    private static LocalItem BuildRecoveredItem(MissingItemReferenceCandidate candidate, decimal currentStock)
    {
        var normalizedName = RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(candidate.ItemName);
        var normalizedSpec = RentalCatalogValueNormalizer.NormalizeDisplayText(candidate.Specification);
        var normalizedTrackingType = ItemTrackingTypes.Normalize(candidate.TrackingType);

        return new LocalItem
        {
            Id = candidate.ItemId,
            TenantCode = candidate.TenantCode,
            OfficeCode = candidate.OfficeCode,
            NameOriginal = normalizedName,
            NameMatchKey = RentalCatalogValueNormalizer.NormalizeLooseKey(normalizedName),
            SpecificationOriginal = normalizedSpec,
            SpecificationMatchKey = RentalCatalogValueNormalizer.NormalizeLooseKey(normalizedSpec),
            CategoryName = candidate.CategoryName,
            ItemKind = candidate.ItemKind,
            TrackingType = normalizedTrackingType,
            Unit = RentalCatalogValueNormalizer.NormalizeDisplayText(candidate.Unit),
            CurrentStock = ItemOperationalPolicy.SupportsInventory(normalizedTrackingType) ? currentStock : 0m,
            PurchasePrice = candidate.PurchasePrice,
            SalePrice = candidate.SalePrice,
            RetailPrice = candidate.RetailPrice,
            LastPurchaseDate = candidate.LastPurchaseDate,
            LastSaleDate = candidate.LastSaleDate,
            SimpleMemo = "전표/재고 참조는 있으나 품목 마스터가 없어 자동 복구된 품목입니다.",
            IsSale = !string.Equals(normalizedTrackingType, ItemTrackingTypes.Asset, StringComparison.Ordinal),
            IsRental = string.Equals(normalizedTrackingType, ItemTrackingTypes.Asset, StringComparison.Ordinal)
        };
    }

    private static string? PickMostCommon(IEnumerable<string?> values)
        => values
            .Select(value => RentalCatalogValueNormalizer.NormalizeDisplayText(value))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(value => value, StringComparer.CurrentCultureIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => group.Key)
            .FirstOrDefault();

    private static string ResolveItemKind(string trackingType)
        => ItemTrackingTypes.Normalize(trackingType) switch
        {
            ItemTrackingTypes.Asset => ItemKinds.Asset,
            ItemTrackingTypes.NonStock => ItemKinds.Billing,
            _ => ItemKinds.Product
        };

    private static string InferRecoveredItemCategoryName(string? itemName)
    {
        var normalizedName = RentalCatalogValueNormalizer.NormalizeLooseKey(itemName);
        if (normalizedName.Contains("토너", StringComparison.Ordinal) ||
            normalizedName.Contains("드럼", StringComparison.Ordinal) ||
            normalizedName.Contains("잉크", StringComparison.Ordinal) ||
            normalizedName.Contains("카트리지", StringComparison.Ordinal) ||
            normalizedName.Contains("폐토너", StringComparison.Ordinal))
        {
            return "소모품";
        }

        return "기타";
    }

    private sealed record InvoiceItemReferenceRow(
        Guid ItemId,
        string ItemName,
        string Specification,
        string Unit,
        string TrackingType,
        decimal UnitPrice,
        VoucherType VoucherType,
        DateOnly InvoiceDate,
        string TenantCode,
        string ResponsibleOfficeCode,
        string OfficeCode);

    private sealed record TransferItemReferenceRow(
        Guid ItemId,
        string ItemName,
        string Specification,
        string Unit,
        string FromWarehouseCode,
        DateOnly TransferDate);

    private sealed record MissingItemReferenceCandidate(
        Guid ItemId,
        string ItemName,
        string Specification,
        string Unit,
        string TrackingType,
        string ItemKind,
        string CategoryName,
        string OfficeCode,
        string TenantCode,
        decimal PurchasePrice,
        decimal SalePrice,
        decimal RetailPrice,
        DateOnly? LastPurchaseDate,
        DateOnly? LastSaleDate);
}
