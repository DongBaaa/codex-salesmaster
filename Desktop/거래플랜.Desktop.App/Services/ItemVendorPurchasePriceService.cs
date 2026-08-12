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

public sealed record ItemConfirmedInvoiceDates(
    DateOnly? LastPurchaseDate,
    DateOnly? LastSaleDate);

public sealed partial class LocalStateService
{
    public async Task<ItemConfirmedInvoiceDates> GetItemConfirmedInvoiceDatesAsync(
        Guid itemId,
        SessionState session,
        CancellationToken ct = default)
    {
        var scope = await ResolveReadableItemInvoiceHistoryScopeAsync(itemId, session, ct);
        if (scope is null)
            return new ItemConfirmedInvoiceDates(null, null);

        var rows = await (
                from invoice in _db.Invoices.IgnoreQueryFilters().AsNoTracking()
                join line in _db.InvoiceLines.IgnoreQueryFilters().AsNoTracking()
                    on invoice.Id equals line.InvoiceId
                where !invoice.IsDeleted
                      && invoice.IsLatestVersion
                      && invoice.IsConfirmed
                      && (invoice.VoucherType == VoucherType.Purchase
                          || invoice.VoucherType == VoucherType.Sales)
                      && !line.IsDeleted
                      && line.ItemId == itemId
                      && invoice.TenantCode == scope.TenantCode
                      && (invoice.OfficeCode == OfficeCodeCatalog.Shared
                          || scope.ReadableOfficeCodes.Contains(invoice.OfficeCode)
                          || scope.ReadableOfficeCodes.Contains(invoice.ResponsibleOfficeCode))
                group invoice by invoice.VoucherType
                into invoiceGroup
                select new ItemInvoiceDateQueryRow(
                    invoiceGroup.Key,
                    invoiceGroup.Max(invoice => invoice.InvoiceDate)))
            .Take(2)
            .ToListAsync(ct);

        return new ItemConfirmedInvoiceDates(
            rows.FirstOrDefault(row => row.VoucherType == VoucherType.Purchase)?.InvoiceDate,
            rows.FirstOrDefault(row => row.VoucherType == VoucherType.Sales)?.InvoiceDate);
    }

    public async Task<IReadOnlyList<ItemVendorPurchasePriceRow>> GetItemVendorPurchasePricesAsync(
        Guid itemId,
        SessionState session,
        CancellationToken ct = default)
    {
        var scope = await ResolveReadableItemInvoiceHistoryScopeAsync(itemId, session, ct);
        if (scope is null)
            return [];

        var rows = await QueryPurchasePriceRows(
                scope.TenantCode,
                scope.ReadableOfficeCodes,
                itemId,
                null)
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

    private async Task<ItemInvoiceHistoryScope?> ResolveReadableItemInvoiceHistoryScopeAsync(
        Guid itemId,
        SessionState session,
        CancellationToken ct)
    {
        if (itemId == Guid.Empty)
            return null;

        var item = await _db.Items
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == itemId && !candidate.IsDeleted,
                ct);
        if (item is null || !CanReadItemScope(item, session))
            return null;

        var tenantCode = session.HasGlobalDataScope
            ? TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
                item.TenantCode,
                item.OfficeCode,
                session.TenantCode,
                session.OfficeCode)
            : ResolveCurrentTenantCode(session);
        var readableOfficeCodes = session.HasGlobalDataScope
            ? TenantScopeCatalog.GetNormalizedOfficeCodesForTenant(tenantCode)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : GetReadableOfficeCodes(session);
        if (readableOfficeCodes.Count == 0)
            return null;

        return new ItemInvoiceHistoryScope(tenantCode, readableOfficeCodes);
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

    private sealed record ItemInvoiceDateQueryRow(
        VoucherType VoucherType,
        DateOnly InvoiceDate);

    private sealed record ItemInvoiceHistoryScope(
        string TenantCode,
        HashSet<string> ReadableOfficeCodes);
}
