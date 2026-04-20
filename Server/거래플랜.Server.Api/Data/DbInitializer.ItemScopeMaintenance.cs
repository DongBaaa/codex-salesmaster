using Microsoft.EntityFrameworkCore;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Server.Api.Data;

public static partial class DbInitializer
{
    private static async Task BackfillItemScopeFieldsAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var items = await dbContext.Items.IgnoreQueryFilters().ToListAsync(cancellationToken);
        if (items.Count == 0)
            return;

        var rentalOfficeMap = (await dbContext.RentalAssets.IgnoreQueryFilters()
                .Where(asset => !asset.IsDeleted && asset.ItemId.HasValue)
                .Select(asset => new
                {
                    ItemId = asset.ItemId!.Value,
                    asset.OfficeCode,
                    asset.ResponsibleOfficeCode,
                    asset.ManagementCompanyCode
                })
                .ToListAsync(cancellationToken))
            .Select(asset => new
            {
                asset.ItemId,
                OfficeCode = ResolveOperationalOwnerOfficeCode(
                    asset.OfficeCode,
                    asset.ResponsibleOfficeCode,
                    asset.ManagementCompanyCode,
                    OfficeCodeCatalog.Usenet)
            })
            .ToList();

        var rentalOfficeLookup = rentalOfficeMap
            .GroupBy(entry => entry.ItemId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(entry => entry.OfficeCode)
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList());

        var warehouseOfficeMap = await dbContext.ItemWarehouseStocks
            .AsNoTracking()
            .Select(stock => new
            {
                stock.ItemId,
                stock.WarehouseCode
            })
            .ToListAsync(cancellationToken);

        var warehouseOfficeLookup = warehouseOfficeMap
            .Select(stock => new
            {
                stock.ItemId,
                OfficeCode = ResolveOfficeCodeFromWarehouseCode(stock.WarehouseCode)
            })
            .Where(entry => !string.IsNullOrWhiteSpace(entry.OfficeCode))
            .GroupBy(entry => entry.ItemId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(entry => entry.OfficeCode)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList());

        var invoiceOfficeMap = (await (
                from line in dbContext.InvoiceLines.IgnoreQueryFilters().AsNoTracking()
                join invoice in dbContext.Invoices.IgnoreQueryFilters().AsNoTracking() on line.InvoiceId equals invoice.Id
                where !line.IsDeleted && !invoice.IsDeleted && line.ItemId.HasValue
                select new
                {
                    ItemId = line.ItemId!.Value,
                    invoice.OfficeCode,
                    invoice.ResponsibleOfficeCode
                })
            .ToListAsync(cancellationToken))
            .Select(entry => new
            {
                entry.ItemId,
                OfficeCode = OfficeCodeCatalog.ResolveOwningOfficeCode(
                    entry.OfficeCode,
                    entry.ResponsibleOfficeCode,
                    OfficeCodeCatalog.Shared)
            })
            .ToList();

        var invoiceOfficeLookup = invoiceOfficeMap
            .Where(entry => !string.IsNullOrWhiteSpace(entry.OfficeCode))
            .GroupBy(entry => entry.ItemId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(entry => entry.OfficeCode)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList());

        var changed = false;
        foreach (var item in items)
        {
            var scopeInference = ItemScopeInference.Analyze(
                item.OfficeCode,
                item.TenantCode,
                rentalOfficeLookup.TryGetValue(item.Id, out var rentalOfficeCodes) ? rentalOfficeCodes : [],
                warehouseOfficeLookup.TryGetValue(item.Id, out var warehouseOfficeCodes) ? warehouseOfficeCodes : [],
                invoiceOfficeLookup.TryGetValue(item.Id, out var invoiceOfficeCodes) ? invoiceOfficeCodes : []);

            changed |= TryAssign(item, entity => entity.OfficeCode, (entity, value) => entity.OfficeCode = value, scopeInference.DesiredOfficeCode);
            changed |= TryAssign(item, entity => entity.TenantCode, (entity, value) => entity.TenantCode = value, scopeInference.DesiredTenantCode);
        }

        if (changed)
            await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string ResolveOfficeCodeFromWarehouseCode(string? warehouseCode)
    {
        var normalizedWarehouseCode = OfficeCodeCatalog.NormalizeWarehouseCodeLoose(warehouseCode);
        return normalizedWarehouseCode switch
        {
            OfficeCodeCatalog.ItworldMainWarehouse => OfficeCodeCatalog.Itworld,
            OfficeCodeCatalog.YeonsuMainWarehouse => OfficeCodeCatalog.Yeonsu,
            _ => OfficeCodeCatalog.Usenet
        };
    }
}
