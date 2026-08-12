using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Utilities;
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
        if (_dbContext.Database.CurrentTransaction is not null)
        {
            await RebuildCoreAsync(cancellationToken);
            return;
        }

        await using var transaction = await InventoryMutationTransactionScope.BeginAsync(
            _dbContext,
            serializeInventoryMutations: true,
            cancellationToken);
        await RebuildCoreAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task RebuildCoreAsync(CancellationToken cancellationToken)
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
        var itemIds = invoices
            .SelectMany(invoice => invoice.Lines)
            .Where(line => !line.IsDeleted && line.ItemId.HasValue && line.ItemId.Value != Guid.Empty)
            .Select(line => line.ItemId!.Value)
            .Concat(transfers
                .SelectMany(transfer => transfer.Lines)
                .Where(line => !line.IsDeleted && line.ItemId.HasValue && line.ItemId.Value != Guid.Empty)
                .Select(line => line.ItemId!.Value))
            .Distinct()
            .ToList();
        var itemTrackingMap = itemIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _dbContext.Items
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(item => itemIds.Contains(item.Id) && !item.IsDeleted)
                .Select(item => new { item.Id, item.TrackingType })
                .ToDictionaryAsync(item => item.Id, item => item.TrackingType, cancellationToken);

        var entries = new List<InventoryLedgerEntry>();
        foreach (var invoice in invoices)
        {
            if (invoice.VoucherType is not (VoucherType.Sales or VoucherType.Purchase or VoucherType.Procurement))
                continue;
            if (invoice.VoucherType == VoucherType.Purchase &&
                !InvoiceReceivingStatuses.IsConfirmed(invoice.PurchaseReceivingStatus))
            {
                continue;
            }

            var warehouseCode = ResolveInvoiceWarehouseCode(invoice);
            foreach (var line in invoice.Lines.Where(line =>
                         !line.IsDeleted &&
                         line.ItemId.HasValue &&
                         line.ItemId.Value != Guid.Empty &&
                         line.Quantity != 0m))
            {
                if (!itemTrackingMap.TryGetValue(line.ItemId!.Value, out var trackingType) ||
                    !ItemOperationalPolicy.SupportsInventory(trackingType) ||
                    !ItemOperationalPolicy.SupportsInventory(line.ItemTrackingType))
                {
                    continue;
                }

                var quantity = Math.Abs(line.Quantity);
                var quantityDelta = invoice.VoucherType switch
                {
                    VoucherType.Sales => -quantity,
                    VoucherType.Purchase => quantity,
                    VoucherType.Procurement => quantity,
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

            foreach (var line in transfer.Lines.Where(line =>
                         !line.IsDeleted &&
                         line.ItemId.HasValue &&
                         line.ItemId.Value != Guid.Empty &&
                         line.Quantity > 0m))
            {
                if (!itemTrackingMap.TryGetValue(line.ItemId!.Value, out var trackingType) ||
                    !ItemOperationalPolicy.SupportsInventory(trackingType))
                {
                    continue;
                }

                var quantity = line.Quantity;

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

        var existingEntries = await _dbContext.InventoryLedgerEntries
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        if (HaveSameSemanticEntries(existingEntries, entries))
            return;

        foreach (var trackedEntry in _dbContext.ChangeTracker.Entries<InventoryLedgerEntry>().ToList())
            trackedEntry.State = EntityState.Detached;

        await _dbContext.InventoryLedgerEntries.ExecuteDeleteAsync(cancellationToken);

        if (entries.Count > 0)
            await _dbContext.InventoryLedgerEntries.AddRangeAsync(entries, cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private static bool HaveSameSemanticEntries(
        IReadOnlyCollection<InventoryLedgerEntry> existingEntries,
        IReadOnlyCollection<InventoryLedgerEntry> desiredEntries)
    {
        if (existingEntries.Count != desiredEntries.Count)
            return false;

        var existingCounts = existingEntries
            .GroupBy(ToSemanticKey)
            .ToDictionary(group => group.Key, group => group.Count());
        var desiredCounts = desiredEntries
            .GroupBy(ToSemanticKey)
            .ToDictionary(group => group.Key, group => group.Count());

        return existingCounts.Count == desiredCounts.Count &&
               existingCounts.All(pair =>
                   desiredCounts.TryGetValue(pair.Key, out var desiredCount) &&
                   desiredCount == pair.Value);
    }

    private static InventoryLedgerSemanticKey ToSemanticKey(InventoryLedgerEntry entry)
        => new(
            entry.TenantCode ?? string.Empty,
            entry.OfficeCode ?? string.Empty,
            entry.ItemId,
            entry.WarehouseCode ?? string.Empty,
            entry.SourceType ?? string.Empty,
            entry.SourceDocumentId,
            entry.SourceLineId,
            entry.QuantityDelta,
            entry.OccurredDate,
            entry.Note ?? string.Empty);

    private readonly record struct InventoryLedgerSemanticKey(
        string TenantCode,
        string OfficeCode,
        Guid ItemId,
        string WarehouseCode,
        string SourceType,
        Guid SourceDocumentId,
        Guid? SourceLineId,
        decimal QuantityDelta,
        DateOnly OccurredDate,
        string Note);

    private static string ResolveInvoiceWarehouseCode(Invoice invoice)
        => OfficeCodeCatalog.NormalizeWarehouseCodeOrDefault(
            invoice.SourceWarehouseCode,
            invoice.ResponsibleOfficeCode,
            invoice.OfficeCode);
}
