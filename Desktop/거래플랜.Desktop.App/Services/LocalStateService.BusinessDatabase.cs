using System.IO;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Shared.Contracts;
using 거래플랜.Desktop.App.Data;

namespace 거래플랜.Desktop.App.Services;

public sealed partial class LocalStateService
{
    private const string ReplacementCharacterText = "\uFFFD";

    private static readonly string[] ServerMirrorStateSettingKeys =
    [
        "LastSyncRevision",
        "Sync.LastSuccessAt",
        "Sync.LastError"
    ];

    private static readonly string[] BusinessScopedSettingKeys =
    [
        "LastSyncRevision",
        "Sync.LastSuccessAt",
        "Sync.LastError",
        "InvoiceFilter.From",
        "InvoiceFilter.To",
        "InvoiceFilter.CustomerName",
        "InvoiceFilter.VoucherType",
        "InvoiceFilter.MinAmount",
        "InvoiceFilter.MaxAmount",
        "InvoiceFavorites.Ids"
    ];

    public async Task<bool> HasPendingSyncChangesAsync(CancellationToken ct = default)
    {
        if (_db.ChangeTracker.Entries<ILocalSyncEntity>().Any(entry => entry.Entity.IsDirty))
            return true;

        return await _db.CompanyProfiles.IgnoreQueryFilters().AnyAsync(entity => entity.IsDirty, ct)
               || await _db.Units.IgnoreQueryFilters().AnyAsync(entity => entity.IsDirty, ct)
               || await _db.CustomerCategories.IgnoreQueryFilters().AnyAsync(entity => entity.IsDirty, ct)
               || await _db.PriceGradeOptions.IgnoreQueryFilters().AnyAsync(entity => entity.IsDirty, ct)
               || await _db.TradeTypeOptions.IgnoreQueryFilters().AnyAsync(entity => entity.IsDirty, ct)
               || await _db.ItemCategoryOptions.IgnoreQueryFilters().AnyAsync(entity => entity.IsDirty, ct)
               || await _db.CustomerMasters.IgnoreQueryFilters().AnyAsync(entity => entity.IsDirty, ct)
               || await _db.Customers.IgnoreQueryFilters().AnyAsync(entity => entity.IsDirty, ct)
               || await _db.CustomerContracts.IgnoreQueryFilters().AnyAsync(entity => entity.IsDirty, ct)
               || await _db.Items.IgnoreQueryFilters().AnyAsync(entity => entity.IsDirty, ct)
               || await _db.Transactions.IgnoreQueryFilters().AnyAsync(entity => entity.IsDirty, ct)
               || await _db.TransactionAttachments.IgnoreQueryFilters().AnyAsync(entity => entity.IsDirty, ct)
               || await _db.InventoryTransfers.IgnoreQueryFilters().AnyAsync(entity => entity.IsDirty, ct)
               || await _db.RentalManagementCompanies.IgnoreQueryFilters().AnyAsync(entity => entity.IsDirty, ct)
               || await _db.RentalBillingProfiles.IgnoreQueryFilters().AnyAsync(entity => entity.IsDirty, ct)
               || await _db.RentalAssets.IgnoreQueryFilters().AnyAsync(entity => entity.IsDirty, ct)
               || await _db.RentalBillingLogs.IgnoreQueryFilters().AnyAsync(entity => entity.IsDirty, ct)
               || await _db.Invoices.IgnoreQueryFilters().AnyAsync(entity => entity.IsDirty, ct)
               || await _db.Payments.IgnoreQueryFilters().AnyAsync(entity => entity.IsDirty, ct)
               || await _db.SyncOutboxEntries.AnyAsync(entry => entry.Status != "Acknowledged", ct);
    }

    public async Task<bool> HasVisibleBusinessCacheAsync(SessionState session, CancellationToken ct = default)
    {
        if (session is null || !session.IsLoggedIn)
            return false;

        if (await HasVisiblePrimaryWorkCacheAsync(session, ct))
            return true;

        if (await ApplyItemScope(_db.Items.AsNoTracking(), session).AnyAsync(ct))
            return true;

        if (await ApplyRentalCustomerScope(_db.Customers.AsNoTracking(), session).AnyAsync(ct))
            return true;

        if (await ApplyRentalItemScope(_db.Items.AsNoTracking(), session).AnyAsync(ct))
            return true;

        return false;
    }

    public async Task<bool> HasVisiblePrimaryWorkCacheAsync(SessionState session, CancellationToken ct = default)
    {
        if (session is null || !session.IsLoggedIn)
            return false;

        if (await ApplyCustomerScope(_db.Customers.AsNoTracking(), session).AnyAsync(ct))
            return true;

        return await ApplyInvoiceScope(_db.Invoices.AsNoTracking(), session).AnyAsync(ct);
    }

    public async Task<bool> HasLikelyCorruptedPrimaryWorkCacheAsync(SessionState session, CancellationToken ct = default)
    {
        if (session is null || !session.IsLoggedIn)
            return false;

        var customerQuery = ApplyCustomerScope(_db.Customers.AsNoTracking(), session)
            .Where(customer =>
                string.IsNullOrEmpty(customer.NameOriginal) ||
                string.IsNullOrEmpty(customer.TradeType) ||
                string.IsNullOrEmpty(customer.ResponsibleOfficeCode) ||
                customer.NameOriginal.Contains(ReplacementCharacterText) ||
                customer.TradeType.Contains(ReplacementCharacterText) ||
                customer.ResponsibleOfficeCode.Contains(ReplacementCharacterText));

        if (await customerQuery.AnyAsync(ct))
            return true;

        var itemQuery = ApplyItemScope(_db.Items.AsNoTracking(), session)
            .Where(item =>
                string.IsNullOrEmpty(item.NameOriginal) ||
                string.IsNullOrEmpty(item.TrackingType) ||
                item.NameOriginal.Contains(ReplacementCharacterText) ||
                item.SpecificationOriginal.Contains(ReplacementCharacterText) ||
                item.TrackingType.Contains(ReplacementCharacterText));

        if (await itemQuery.AnyAsync(ct))
            return true;

        return await ApplyInvoiceScope(_db.Invoices.AsNoTracking(), session)
            .AnyAsync(invoice =>
                invoice.VoucherType != VoucherType.Sales &&
                invoice.VoucherType != VoucherType.Purchase &&
                invoice.VoucherType != VoucherType.Procurement &&
                invoice.VoucherType != VoucherType.Expense &&
                invoice.VoucherType != VoucherType.Collection,
                ct);
    }

    public async Task ResetSharedMirrorCacheAsync(CancellationToken ct = default)
    {
        _db.ChangeTracker.Clear();
        _officeAccess.ClearSessionAccess(_session);

        foreach (var attachment in await _db.TransactionAttachments.IgnoreQueryFilters()
                     .Where(current => !string.IsNullOrWhiteSpace(current.StoredPath))
                     .Select(current => current.StoredPath)
                     .ToListAsync(ct))
        {
            if (string.IsNullOrWhiteSpace(attachment) || !File.Exists(attachment))
                continue;

            try
            {
                File.Delete(attachment);
            }
            catch
            {
                // ignore local cached attachment cleanup failure
            }
        }

        await _db.Payments.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.InvoiceLineSerials.ExecuteDeleteAsync(ct);
        await _db.InventoryMovements.ExecuteDeleteAsync(ct);
        await _db.CostAllocations.ExecuteDeleteAsync(ct);
        await _db.StockLayers.ExecuteDeleteAsync(ct);
        await _db.SerialLedgers.ExecuteDeleteAsync(ct);
        await _db.InventoryTransferLines.ExecuteDeleteAsync(ct);
        await _db.InventoryTransfers.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.TransactionAttachments.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.Transactions.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.CustomerContracts.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.InvoiceLines.ExecuteDeleteAsync(ct);
        await _db.Invoices.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.ItemWarehouseStocks.ExecuteDeleteAsync(ct);
        await _db.RentalBillingLogs.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.RentalAssets.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.RentalBillingProfiles.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.RentalManagementCompanies.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.Customers.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.CustomerMasters.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.Items.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.CompanyProfiles.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.Units.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.CustomerCategories.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.PriceGradeOptions.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.TradeTypeOptions.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.ItemCategoryOptions.IgnoreQueryFilters().ExecuteDeleteAsync(ct);

        foreach (var key in ServerMirrorStateSettingKeys)
        {
            var setting = await _db.Settings.FindAsync([key], ct);
            if (setting is not null)
                _db.Settings.Remove(setting);
        }

        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
    }

    public async Task ResetBusinessDataCacheAsync(SessionState session, CancellationToken ct = default)
    {
        _officeAccess.ClearSessionAccess(session);

        await _db.TransactionAttachments.ExecuteDeleteAsync(ct);
        await _db.Transactions.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.CustomerContracts.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.Payments.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.InvoiceLineSerials.ExecuteDeleteAsync(ct);
        await _db.InventoryMovements.ExecuteDeleteAsync(ct);
        await _db.CostAllocations.ExecuteDeleteAsync(ct);
        await _db.StockLayers.ExecuteDeleteAsync(ct);
        await _db.SerialLedgers.ExecuteDeleteAsync(ct);
        await _db.InventoryTransferLines.ExecuteDeleteAsync(ct);
        await _db.InventoryTransfers.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.InvoiceLines.ExecuteDeleteAsync(ct);
        await _db.Invoices.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.ItemWarehouseStocks.ExecuteDeleteAsync(ct);
        await _db.RentalBillingLogs.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.RentalAssets.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.RentalBillingProfiles.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.RentalManagementCompanies.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.Customers.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.CustomerMasters.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.Items.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.CompanyProfiles.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.Units.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.CustomerCategories.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.PriceGradeOptions.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.TradeTypeOptions.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.ItemCategoryOptions.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.Offices.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.Warehouses.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await _db.RecentSelections.ExecuteDeleteAsync(ct);
        await _db.AttachmentSelections.ExecuteDeleteAsync(ct);
        await _db.AuditLogs.ExecuteDeleteAsync(ct);

        foreach (var key in BusinessScopedSettingKeys)
        {
            var setting = await _db.Settings.FindAsync([key], ct);
            if (setting is not null)
                _db.Settings.Remove(setting);
        }

        await _db.SaveChangesAsync(ct);
    }
}
