using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace 거래플랜.Server.Api.Services;

public sealed class InventoryLedgerService
{
    private readonly AppDbContext _dbContext;

    public InventoryLedgerService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task RebuildAsync(CancellationToken cancellationToken = default)
    {
        var invoices = await _dbContext.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(invoice => invoice.Lines)
            .Where(invoice => !invoice.IsDeleted && invoice.IsLatestVersion)
            .OrderBy(invoice => invoice.InvoiceDate)
            .ThenBy(invoice => invoice.UpdatedAtUtc)
            .ToListAsync(cancellationToken);

        var transfers = await _dbContext.InventoryTransfers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(transfer => transfer.Lines)
            .Where(transfer => !transfer.IsDeleted)
            .OrderBy(transfer => transfer.TransferDate)
            .ThenBy(transfer => transfer.UpdatedAtUtc)
            .ToListAsync(cancellationToken);
        var transferItemIds = transfers
            .SelectMany(transfer => transfer.Lines)
            .Where(line => !line.IsDeleted && line.ItemId.HasValue && line.ItemId.Value != Guid.Empty)
            .Select(line => line.ItemId!.Value)
            .Distinct()
            .ToList();
        var transferItemTrackingMap = transferItemIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _dbContext.Items
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(item => transferItemIds.Contains(item.Id) && !item.IsDeleted)
                .Select(item => new { item.Id, item.TrackingType })
                .ToDictionaryAsync(item => item.Id, item => item.TrackingType, cancellationToken);

        await _dbContext.InventoryLedgerEntries.ExecuteDeleteAsync(cancellationToken);

        var entries = new List<InventoryLedgerEntry>();
        foreach (var invoice in invoices)
        {
            if (invoice.VoucherType == VoucherType.Purchase &&
                !InvoiceReceivingStatuses.IsConfirmed(invoice.PurchaseReceivingStatus))
            {
                continue;
            }

            var warehouseCode = ResolveInvoiceWarehouseCode(invoice);
            foreach (var line in invoice.Lines.Where(line => !line.IsDeleted && line.ItemId.HasValue && line.ItemId.Value != Guid.Empty && line.Quantity != 0))
            {
                if (!IsInventoryRelevant(invoice.VoucherType, line.ItemTrackingType))
                    continue;

                var quantityDelta = invoice.VoucherType switch
                {
                    VoucherType.Sales => -line.Quantity,
                    VoucherType.Purchase => line.Quantity,
                    VoucherType.Procurement => line.Quantity,
                    _ => 0m
                };

                if (quantityDelta == 0m)
                    continue;

                entries.Add(new InventoryLedgerEntry
                {
                    Id = Guid.NewGuid(),
                    TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(invoice.TenantCode, invoice.OfficeCode),
                    OfficeCode = OfficeCodeCatalog.ResolveOwningOfficeCode(invoice.OfficeCode, invoice.ResponsibleOfficeCode, invoice.OfficeCode),
                    ItemId = line.ItemId!.Value,
                    WarehouseCode = warehouseCode,
                    SourceType = $"Invoice:{invoice.VoucherType}",
                    SourceDocumentId = invoice.Id,
                    SourceLineId = line.Id,
                    QuantityDelta = quantityDelta,
                    OccurredDate = invoice.InvoiceDate,
                    Note = invoice.InvoiceNumber,
                    CreatedAtUtc = invoice.UpdatedAtUtc == default ? DateTime.UtcNow : invoice.UpdatedAtUtc
                });
            }
        }

        foreach (var transfer in transfers)
        {
            var normalizedStatus = InventoryTransferStatusNormalizer.Normalize(
                transfer.TransferStatus,
                transfer.ReceivedByUsername,
                transfer.ReceivedAtUtc,
                transfer.RejectedByUsername,
                transfer.RejectedAtUtc);
            if (string.Equals(normalizedStatus, InventoryTransferStatusNormalizer.Rejected, StringComparison.Ordinal))
                continue;

            var isReceived = string.Equals(normalizedStatus, InventoryTransferStatusNormalizer.Received, StringComparison.Ordinal);
            var fromWarehouse = OfficeCodeCatalog.NormalizeWarehouseCodeOrDefault(transfer.FromWarehouseCode, transfer.SourceOfficeCode, transfer.SourceOfficeCode);
            var toWarehouse = OfficeCodeCatalog.NormalizeWarehouseCodeOrDefault(transfer.ToWarehouseCode, transfer.TargetOfficeCode, transfer.TargetOfficeCode);
            if (string.Equals(fromWarehouse, toWarehouse, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var line in transfer.Lines.Where(line => !line.IsDeleted && line.ItemId.HasValue && line.ItemId.Value != Guid.Empty))
            {
                if (!transferItemTrackingMap.TryGetValue(line.ItemId!.Value, out var trackingType) ||
                    !ItemOperationalPolicy.SupportsInventory(trackingType))
                {
                    continue;
                }

                var quantity = Math.Abs(line.Quantity);
                if (quantity == 0m)
                    continue;

                entries.Add(new InventoryLedgerEntry
                {
                    Id = Guid.NewGuid(),
                    TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(transfer.TenantCode, transfer.SourceOfficeCode),
                    OfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(transfer.SourceOfficeCode),
                    ItemId = line.ItemId.Value,
                    WarehouseCode = fromWarehouse,
                    SourceType = "InventoryTransfer:Out",
                    SourceDocumentId = transfer.Id,
                    SourceLineId = line.Id,
                    QuantityDelta = -quantity,
                    OccurredDate = transfer.TransferDate,
                    Note = transfer.TransferNumber,
                    CreatedAtUtc = transfer.UpdatedAtUtc == default ? DateTime.UtcNow : transfer.UpdatedAtUtc
                });

                if (!isReceived)
                    continue;

                var receivedQuantity = Math.Min(quantity, Math.Max(0m, line.ReceivedQuantity ?? line.Quantity));
                if (receivedQuantity == 0m)
                    continue;

                entries.Add(new InventoryLedgerEntry
                {
                    Id = Guid.NewGuid(),
                    TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(transfer.TenantCode, transfer.TargetOfficeCode),
                    OfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(transfer.TargetOfficeCode),
                    ItemId = line.ItemId.Value,
                    WarehouseCode = toWarehouse,
                    SourceType = "InventoryTransfer:In",
                    SourceDocumentId = transfer.Id,
                    SourceLineId = line.Id,
                    QuantityDelta = receivedQuantity,
                    OccurredDate = transfer.TransferDate,
                    Note = transfer.TransferNumber,
                    CreatedAtUtc = transfer.UpdatedAtUtc == default ? DateTime.UtcNow : transfer.UpdatedAtUtc
                });
            }
        }

        if (entries.Count > 0)
            await _dbContext.InventoryLedgerEntries.AddRangeAsync(entries, cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private static bool IsInventoryRelevant(VoucherType voucherType, string? trackingType)
    {
        var normalizedTrackingType = ItemTrackingTypes.Normalize(trackingType);
        if (!string.Equals(normalizedTrackingType, ItemTrackingTypes.Stock, StringComparison.OrdinalIgnoreCase))
            return false;

        return voucherType is VoucherType.Sales or VoucherType.Purchase or VoucherType.Procurement;
    }

    private static string ResolveInvoiceWarehouseCode(Invoice invoice)
        => OfficeCodeCatalog.NormalizeWarehouseCodeOrDefault(
            invoice.SourceWarehouseCode,
            invoice.ResponsibleOfficeCode,
            invoice.OfficeCode);
}
