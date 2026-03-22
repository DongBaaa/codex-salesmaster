using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace 거래플랜.Server.Api.Services;

public sealed class OfficeScopeService
{
    private enum DataArea
    {
        General,
        Customers,
        Items,
        Invoices,
        Payments,
        Contracts,
        Reports,
        Rentals,
        Deliveries
    }

    private readonly ICurrentUserContext _currentUserContext;
    private readonly AppDbContext _dbContext;
    private IReadOnlyList<DataSharingPolicy>? _activePolicies;

    public OfficeScopeService(ICurrentUserContext currentUserContext, AppDbContext dbContext)
    {
        _currentUserContext = currentUserContext;
        _dbContext = dbContext;
    }

    public bool IsAdmin => _currentUserContext.IsAdmin;
    public bool HasGlobalDataScope =>
        IsAdmin && string.Equals(CurrentScopeType, TenantScopeCatalog.ScopeAdmin, StringComparison.OrdinalIgnoreCase);

    public string CurrentTenantCode => TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
        _currentUserContext.TenantCode,
        _currentUserContext.OfficeCode);

    public string CurrentOfficeCode => OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(_currentUserContext.OfficeCode);

    public string CurrentScopeType => TenantScopeCatalog.NormalizeScopeTypeOrDefault(
        _currentUserContext.ScopeType,
        IsAdmin ? TenantScopeCatalog.ScopeAdmin : TenantScopeCatalog.ScopeOfficeOnly);

    public string CurrentWarehouseCode => OfficeCodeCatalog.GetMainWarehouseCode(CurrentOfficeCode);

    public IReadOnlyList<string> ReadableOfficeCodes => ResolveReadableOfficeCodes(DataArea.General);

    public IReadOnlyList<string> WritableOfficeCodes => ResolveWritableOfficeCodes(DataArea.General);

    public bool CanReadOffice(string? officeCode, string? tenantCode = null)
        => CanReadOffice(officeCode, tenantCode, DataArea.General);

    public bool CanWriteOffice(string? officeCode, string? tenantCode = null)
        => CanWriteOffice(officeCode, tenantCode, DataArea.General);

    public bool CanReadWarehouse(string? warehouseCode, string? officeCode = null)
        => CanReadWarehouse(warehouseCode, officeCode, DataArea.Items);

    public bool CanWriteWarehouse(string? warehouseCode, string? officeCode = null)
        => CanWriteWarehouse(warehouseCode, officeCode, DataArea.Items);

    public string ResolveTenantForCreate(string? requestedTenantCode, string? requestedOfficeCode, string? fallbackTenantCode = null, string? fallbackOfficeCode = null)
    {
        if (HasGlobalDataScope)
        {
            return TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
                requestedTenantCode,
                requestedOfficeCode,
                fallbackTenantCode,
                fallbackOfficeCode);
        }

        return CurrentTenantCode;
    }

    public string ResolveScopeForCreate(string? requestedOfficeCode, string? fallbackOfficeCode = null)
    {
        if (HasGlobalDataScope)
        {
            return OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(
                requestedOfficeCode,
                fallbackOfficeCode ?? CurrentOfficeCode);
        }

        if (string.Equals(CurrentScopeType, TenantScopeCatalog.ScopeTenantAll, StringComparison.OrdinalIgnoreCase) &&
            OfficeCodeCatalog.TryNormalizeOfficeCode(requestedOfficeCode, out var requestedOffice) &&
            ResolveWritableOfficeCodes(DataArea.General).Contains(requestedOffice, StringComparer.OrdinalIgnoreCase))
        {
            return requestedOffice;
        }

        return CurrentOfficeCode;
    }

    public IQueryable<CustomerMaster> ApplyCustomerMasterScope(IQueryable<CustomerMaster> query)
    {
        if (HasGlobalDataScope)
            return query;

        var tenantCode = CurrentTenantCode;
        var readableOffices = ResolveReadableOfficeCodes(DataArea.Customers);
        return query.Where(entity =>
            entity.TenantCode == tenantCode &&
            (entity.OfficeCode == OfficeCodeCatalog.Shared || readableOffices.Contains(entity.OfficeCode)));
    }

    public IQueryable<Customer> ApplyCustomerScope(IQueryable<Customer> query)
    {
        if (HasGlobalDataScope)
            return query;

        var tenantCode = CurrentTenantCode;
        var readableOffices = ResolveReadableOfficeCodes(DataArea.Customers);
        return query.Where(entity =>
            entity.TenantCode == tenantCode &&
            (entity.OfficeCode == OfficeCodeCatalog.Shared || readableOffices.Contains(entity.OfficeCode)));
    }

    public IQueryable<CustomerContract> ApplyCustomerContractScope(IQueryable<CustomerContract> query)
    {
        if (HasGlobalDataScope)
            return query;

        var tenantCode = CurrentTenantCode;
        var readableOffices = ResolveReadableOfficeCodes(DataArea.Contracts);
        return query.Where(entity =>
            entity.Customer != null &&
            entity.Customer.TenantCode == tenantCode &&
            (entity.Customer.OfficeCode == OfficeCodeCatalog.Shared || readableOffices.Contains(entity.Customer.OfficeCode)));
    }

    public IQueryable<Item> ApplyItemScope(IQueryable<Item> query)
    {
        if (HasGlobalDataScope)
            return query;

        var tenantCode = CurrentTenantCode;
        var readableOffices = ResolveReadableOfficeCodes(DataArea.Items);
        return query.Where(entity =>
            entity.TenantCode == tenantCode &&
            (entity.OfficeCode == OfficeCodeCatalog.Shared || readableOffices.Contains(entity.OfficeCode)));
    }

    public IQueryable<Invoice> ApplyInvoiceScope(IQueryable<Invoice> query)
    {
        if (HasGlobalDataScope)
            return query;

        var tenantCode = CurrentTenantCode;
        var readableOffices = ResolveReadableOfficeCodes(DataArea.Invoices);
        return query.Where(entity =>
            entity.TenantCode == tenantCode &&
            (entity.OfficeCode == OfficeCodeCatalog.Shared || readableOffices.Contains(entity.OfficeCode)));
    }

    public IQueryable<Payment> ApplyPaymentScope(IQueryable<Payment> query)
    {
        if (HasGlobalDataScope)
            return query;

        var tenantCode = CurrentTenantCode;
        var readableOffices = ResolveReadableOfficeCodes(DataArea.Payments);
        return query.Where(entity =>
            entity.Invoice != null &&
            entity.Invoice.TenantCode == tenantCode &&
            (entity.Invoice.OfficeCode == OfficeCodeCatalog.Shared || readableOffices.Contains(entity.Invoice.OfficeCode)));
    }

    public IQueryable<TransactionRecord> ApplyTransactionScope(IQueryable<TransactionRecord> query)
    {
        if (HasGlobalDataScope)
            return query;

        var tenantCode = CurrentTenantCode;
        var readableOffices = ResolveReadableOfficeCodes(DataArea.Payments);
        return query.Where(entity =>
            entity.TenantCode == tenantCode &&
            (entity.OfficeCode == OfficeCodeCatalog.Shared || readableOffices.Contains(entity.OfficeCode)));
    }

    public IQueryable<TransactionAttachment> ApplyTransactionAttachmentScope(IQueryable<TransactionAttachment> query)
    {
        if (HasGlobalDataScope)
            return query;

        var tenantCode = CurrentTenantCode;
        var readableOffices = ResolveReadableOfficeCodes(DataArea.Payments);
        return query.Where(entity =>
            entity.Transaction != null &&
            entity.Transaction.TenantCode == tenantCode &&
            (entity.Transaction.OfficeCode == OfficeCodeCatalog.Shared || readableOffices.Contains(entity.Transaction.OfficeCode)));
    }

    public IQueryable<InventoryTransfer> ApplyInventoryTransferScope(IQueryable<InventoryTransfer> query)
    {
        if (HasGlobalDataScope)
            return query;

        var tenantCode = CurrentTenantCode;
        var readableOffices = ResolveReadableOfficeCodes(DataArea.Deliveries);
        return query.Where(entity =>
            entity.TenantCode == tenantCode &&
            (readableOffices.Contains(entity.SourceOfficeCode) || readableOffices.Contains(entity.TargetOfficeCode)));
    }

    public IQueryable<RentalManagementCompany> ApplyRentalManagementCompanyScope(IQueryable<RentalManagementCompany> query)
    {
        if (HasGlobalDataScope)
            return query;

        var tenantCode = CurrentTenantCode;
        return query.Where(entity => entity.TenantCode == tenantCode);
    }

    public IQueryable<RentalBillingProfile> ApplyRentalBillingProfileScope(IQueryable<RentalBillingProfile> query)
    {
        if (HasGlobalDataScope)
            return query;

        var tenantCode = CurrentTenantCode;
        var readableOffices = ResolveReadableOfficeCodes(DataArea.Rentals);
        return query.Where(entity =>
            entity.TenantCode == tenantCode &&
            (entity.OfficeCode == OfficeCodeCatalog.Shared || readableOffices.Contains(entity.OfficeCode)));
    }

    public IQueryable<RentalAsset> ApplyRentalAssetScope(IQueryable<RentalAsset> query)
    {
        if (HasGlobalDataScope)
            return query;

        var tenantCode = CurrentTenantCode;
        var readableOffices = ResolveReadableOfficeCodes(DataArea.Rentals);
        return query.Where(entity =>
            entity.TenantCode == tenantCode &&
            (entity.OfficeCode == OfficeCodeCatalog.Shared || readableOffices.Contains(entity.OfficeCode)));
    }

    public IQueryable<RentalBillingLog> ApplyRentalBillingLogScope(IQueryable<RentalBillingLog> query)
    {
        if (HasGlobalDataScope)
            return query;

        var tenantCode = CurrentTenantCode;
        var readableOffices = ResolveReadableOfficeCodes(DataArea.Rentals);
        return query.Where(entity =>
            entity.TenantCode == tenantCode &&
            (entity.OfficeCode == OfficeCodeCatalog.Shared || readableOffices.Contains(entity.OfficeCode)));
    }

    public IQueryable<ItemWarehouseStock> ApplyWarehouseScope(IQueryable<ItemWarehouseStock> query)
    {
        if (HasGlobalDataScope)
            return query;

        var readableWarehouses = ResolveReadableOfficeCodes(DataArea.Items)
            .Select(OfficeCodeCatalog.GetMainWarehouseCode)
            .Distinct()
            .ToList();
        return query.Where(entity => readableWarehouses.Contains(entity.WarehouseCode));
    }

    public IQueryable<ItemWarehouseStock> ApplyItemWarehouseStockScope(IQueryable<ItemWarehouseStock> query)
    {
        if (HasGlobalDataScope)
            return query;

        var tenantCode = CurrentTenantCode;
        var readableWarehouses = ResolveReadableOfficeCodes(DataArea.Items)
            .Select(OfficeCodeCatalog.GetMainWarehouseCode)
            .Distinct()
            .ToList();
        var readableOffices = ResolveReadableOfficeCodes(DataArea.Items);
        return query.Where(entity =>
            readableWarehouses.Contains(entity.WarehouseCode) &&
            entity.Item != null &&
            entity.Item.TenantCode == tenantCode &&
            (entity.Item.OfficeCode == OfficeCodeCatalog.Shared || readableOffices.Contains(entity.Item.OfficeCode)));
    }

    public bool CanReadOfficeForCustomers(string? officeCode, string? tenantCode = null)
        => CanReadOffice(officeCode, tenantCode, DataArea.Customers);

    public bool CanWriteOfficeForCustomers(string? officeCode, string? tenantCode = null)
        => CanWriteOffice(officeCode, tenantCode, DataArea.Customers);

    public bool CanReadOfficeForItems(string? officeCode, string? tenantCode = null)
        => CanReadOffice(officeCode, tenantCode, DataArea.Items);

    public bool CanWriteOfficeForItems(string? officeCode, string? tenantCode = null)
        => CanWriteOffice(officeCode, tenantCode, DataArea.Items);

    public bool CanReadOfficeForInvoices(string? officeCode, string? tenantCode = null)
        => CanReadOffice(officeCode, tenantCode, DataArea.Invoices);

    public bool CanWriteOfficeForInvoices(string? officeCode, string? tenantCode = null)
        => CanWriteOffice(officeCode, tenantCode, DataArea.Invoices);

    public bool CanReadOfficeForPayments(string? officeCode, string? tenantCode = null)
        => CanReadOffice(officeCode, tenantCode, DataArea.Payments);

    public bool CanWriteOfficeForPayments(string? officeCode, string? tenantCode = null)
        => CanWriteOffice(officeCode, tenantCode, DataArea.Payments);

    public bool CanReadOfficeForContracts(string? officeCode, string? tenantCode = null)
        => CanReadOffice(officeCode, tenantCode, DataArea.Contracts);

    public bool CanWriteOfficeForContracts(string? officeCode, string? tenantCode = null)
        => CanWriteOffice(officeCode, tenantCode, DataArea.Contracts);

    public bool CanReadOfficeForReports(string? officeCode, string? tenantCode = null)
        => CanReadOffice(officeCode, tenantCode, DataArea.Reports);

    public bool CanReadOfficeForRentals(string? officeCode, string? tenantCode = null)
        => CanReadOffice(officeCode, tenantCode, DataArea.Rentals);

    public bool CanWriteOfficeForRentals(string? officeCode, string? tenantCode = null)
        => CanWriteOffice(officeCode, tenantCode, DataArea.Rentals);

    public bool CanReadOfficeForDeliveries(string? officeCode, string? tenantCode = null)
        => CanReadOffice(officeCode, tenantCode, DataArea.Deliveries);

    public bool CanWriteOfficeForDeliveries(string? officeCode, string? tenantCode = null)
        => CanWriteOffice(officeCode, tenantCode, DataArea.Deliveries);

    private bool CanReadOffice(string? officeCode, string? tenantCode, DataArea area)
    {
        if (HasGlobalDataScope)
            return true;

        var normalizedTenant = ResolveEntityTenantCode(tenantCode, officeCode);
        if (!string.Equals(normalizedTenant, CurrentTenantCode, StringComparison.OrdinalIgnoreCase))
            return false;

        var normalizedOffice = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(officeCode);
        if (string.Equals(normalizedOffice, OfficeCodeCatalog.Shared, StringComparison.OrdinalIgnoreCase))
            return true;

        return ResolveReadableOfficeCodes(area).Contains(normalizedOffice, StringComparer.OrdinalIgnoreCase);
    }

    private bool CanWriteOffice(string? officeCode, string? tenantCode, DataArea area)
    {
        if (HasGlobalDataScope)
            return true;

        var normalizedTenant = ResolveEntityTenantCode(tenantCode, officeCode);
        if (!string.Equals(normalizedTenant, CurrentTenantCode, StringComparison.OrdinalIgnoreCase))
            return false;

        var normalizedOffice = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(officeCode);
        if (string.Equals(normalizedOffice, OfficeCodeCatalog.Shared, StringComparison.OrdinalIgnoreCase))
            return string.Equals(CurrentScopeType, TenantScopeCatalog.ScopeTenantAll, StringComparison.OrdinalIgnoreCase);

        return ResolveWritableOfficeCodes(area).Contains(normalizedOffice, StringComparer.OrdinalIgnoreCase);
    }

    private bool CanReadWarehouse(string? warehouseCode, string? officeCode, DataArea area)
    {
        if (HasGlobalDataScope)
            return true;

        var normalizedWarehouse = OfficeCodeCatalog.NormalizeWarehouseCodeOrDefault(warehouseCode, officeCode, CurrentOfficeCode);
        var readableWarehouses = ResolveReadableOfficeCodes(area)
            .Select(OfficeCodeCatalog.GetMainWarehouseCode)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return readableWarehouses.Contains(normalizedWarehouse, StringComparer.OrdinalIgnoreCase);
    }

    private bool CanWriteWarehouse(string? warehouseCode, string? officeCode, DataArea area)
    {
        if (HasGlobalDataScope)
            return true;

        var normalizedWarehouse = OfficeCodeCatalog.NormalizeWarehouseCodeOrDefault(warehouseCode, officeCode, CurrentOfficeCode);
        var writableWarehouses = ResolveWritableOfficeCodes(area)
            .Select(OfficeCodeCatalog.GetMainWarehouseCode)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return writableWarehouses.Contains(normalizedWarehouse, StringComparer.OrdinalIgnoreCase);
    }

    private string ResolveEntityTenantCode(string? tenantCode, string? officeCode)
        => TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(tenantCode, officeCode, CurrentTenantCode, CurrentOfficeCode);

    private IReadOnlyList<string> ResolveReadableOfficeCodes(DataArea area)
    {
        if (HasGlobalDataScope)
            return OfficeCodeCatalog.All;

        if (string.Equals(CurrentScopeType, TenantScopeCatalog.ScopeOfficeOnly, StringComparison.OrdinalIgnoreCase))
            return [CurrentOfficeCode];

        var readable = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            CurrentOfficeCode
        };

        foreach (var policy in GetActivePolicies())
        {
            if (!string.Equals(policy.TargetTenantCode, CurrentTenantCode, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.Equals(policy.TargetOfficeCode, CurrentOfficeCode, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!IsPolicyEnabled(policy, area))
                continue;

            readable.Add(OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(policy.SourceOfficeCode));
        }

        return readable.ToList();
    }

    private IReadOnlyList<string> ResolveWritableOfficeCodes(DataArea area)
    {
        if (HasGlobalDataScope)
            return OfficeCodeCatalog.All;

        var writable = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            CurrentOfficeCode
        };

        if (!string.Equals(CurrentScopeType, TenantScopeCatalog.ScopeTenantAll, StringComparison.OrdinalIgnoreCase))
            return writable.ToList();

        foreach (var policy in GetActivePolicies())
        {
            if (!policy.AllowTargetWrite)
                continue;

            if (!string.Equals(policy.TargetTenantCode, CurrentTenantCode, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.Equals(policy.TargetOfficeCode, CurrentOfficeCode, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!IsPolicyEnabled(policy, area))
                continue;

            writable.Add(OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(policy.SourceOfficeCode));
        }

        return writable.ToList();
    }

    private IReadOnlyList<DataSharingPolicy> GetActivePolicies()
    {
        if (_activePolicies is not null)
            return _activePolicies;

        _activePolicies = _dbContext.DataSharingPolicies.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(policy => !policy.IsDeleted && policy.IsActive)
            .ToList();

        return _activePolicies;
    }

    private static bool IsPolicyEnabled(DataSharingPolicy policy, DataArea area)
        => area switch
        {
            DataArea.Customers => policy.ShareCustomers,
            DataArea.Items => policy.ShareItems,
            DataArea.Invoices => policy.ShareInvoices,
            DataArea.Payments => policy.SharePayments,
            DataArea.Contracts => policy.ShareContracts,
            DataArea.Reports => policy.ShareReports,
            DataArea.Rentals => policy.ShareRentals,
            DataArea.Deliveries => policy.ShareDeliveries,
            _ => policy.ShareCustomers || policy.ShareItems || policy.ShareInvoices || policy.SharePayments || policy.ShareContracts || policy.ShareReports || policy.ShareRentals || policy.ShareDeliveries
        };
}
