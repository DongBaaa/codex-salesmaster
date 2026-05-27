using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

public sealed record ItemVendorPurchasePriceRow(
    Guid ItemId,
    Guid VendorCustomerId,
    string VendorName,
    string VendorTradeType,
    decimal UnitPrice,
    DateOnly LastPurchaseDate,
    string Unit,
    string InvoiceNumber);

public sealed partial class LocalStateService
{
    public async Task<IReadOnlyList<ItemVendorPurchasePriceRow>> GetItemVendorPurchasePricesAsync(
        Guid itemId,
        SessionState session,
        CancellationToken ct = default)
    {
        if (itemId == Guid.Empty)
            return [];

        var tenantCode = ResolveCurrentTenantCode(session);
        var readableOfficeCodes = GetReadableOfficeCodes(session);
        if (readableOfficeCodes.Count == 0)
            return [];

        var rows = await QueryPurchasePriceRows(tenantCode, readableOfficeCodes, itemId, null)
            .ToListAsync(ct);

        return rows
            .GroupBy(row => row.VendorCustomerId)
            .Select(group => group
                .OrderByDescending(row => row.LastPurchaseDate)
                .ThenByDescending(row => row.LastSavedAtUtc)
                .ThenBy(row => row.VendorName, StringComparer.CurrentCultureIgnoreCase)
                .First())
            .OrderByDescending(row => row.LastPurchaseDate)
            .ThenBy(row => row.VendorName, StringComparer.CurrentCultureIgnoreCase)
            .Select(row => row.ToResult())
            .ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, decimal>> GetLatestPurchasePriceByItemForCustomerAsync(
        Guid customerId,
        SessionState session,
        CancellationToken ct = default)
    {
        if (customerId == Guid.Empty)
            return new Dictionary<Guid, decimal>();

        var tenantCode = ResolveCurrentTenantCode(session);
        var readableOfficeCodes = GetReadableOfficeCodes(session);
        if (readableOfficeCodes.Count == 0)
            return new Dictionary<Guid, decimal>();

        var rows = await QueryPurchasePriceRows(tenantCode, readableOfficeCodes, null, customerId)
            .ToListAsync(ct);

        return rows
            .GroupBy(row => row.ItemId)
            .Select(group => group
                .OrderByDescending(row => row.LastPurchaseDate)
                .ThenByDescending(row => row.LastSavedAtUtc)
                .First())
            .Where(row => row.UnitPrice > 0m)
            .ToDictionary(row => row.ItemId, row => row.UnitPrice);
    }

    private IQueryable<PurchasePriceQueryRow> QueryPurchasePriceRows(
        string tenantCode,
        HashSet<string> readableOfficeCodes,
        Guid? itemId,
        Guid? customerId)
    {
        var query =
            from invoice in _db.Invoices.IgnoreQueryFilters().AsNoTracking()
            join line in _db.InvoiceLines.IgnoreQueryFilters().AsNoTracking()
                on invoice.Id equals line.InvoiceId
            join customer in _db.Customers.IgnoreQueryFilters().AsNoTracking()
                on invoice.CustomerId equals customer.Id
            where !invoice.IsDeleted
                  && invoice.IsLatestVersion
                  && invoice.IsConfirmed
                  && invoice.VoucherType == VoucherType.Purchase
                  && !line.IsDeleted
                  && line.ItemId.HasValue
                  && line.ItemId.Value != Guid.Empty
                  && (!itemId.HasValue || line.ItemId.Value == itemId.Value)
                  && (!customerId.HasValue || invoice.CustomerId == customerId.Value)
                  && line.UnitPrice > 0m
                  && !customer.IsDeleted
                  && invoice.TenantCode == tenantCode
                  && (invoice.OfficeCode == OfficeCodeCatalog.Shared
                      || readableOfficeCodes.Contains(invoice.OfficeCode)
                      || readableOfficeCodes.Contains(invoice.ResponsibleOfficeCode))
            select new PurchasePriceQueryRow(
                line.ItemId!.Value,
                invoice.CustomerId,
                customer.NameOriginal,
                customer.TradeType,
                line.UnitPrice,
                invoice.InvoiceDate,
                line.Unit,
                invoice.InvoiceNumber == string.Empty ? invoice.LocalTempNumber : invoice.InvoiceNumber,
                invoice.LastSavedAtUtc == default ? invoice.UpdatedAtUtc : invoice.LastSavedAtUtc);

        return query;
    }

    private sealed record PurchasePriceQueryRow(
        Guid ItemId,
        Guid VendorCustomerId,
        string VendorName,
        string VendorTradeType,
        decimal UnitPrice,
        DateOnly LastPurchaseDate,
        string Unit,
        string InvoiceNumber,
        DateTime LastSavedAtUtc)
    {
        public ItemVendorPurchasePriceRow ToResult()
            => new(
                ItemId,
                VendorCustomerId,
                VendorName?.Trim() ?? string.Empty,
                CustomerTradeTypes.Normalize(VendorTradeType),
                UnitPrice,
                LastPurchaseDate,
                Unit?.Trim() ?? string.Empty,
                InvoiceNumber?.Trim() ?? string.Empty);
    }
}
