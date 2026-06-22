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
    public bool HasAdministrativeWriteAccess => _currentUserContext.IsAdmin || _currentUserContext.IsGodMode;
    public bool HasGlobalDataScope =>
        IsAdmin && string.Equals(CurrentScopeType, TenantScopeCatalog.ScopeAdmin, StringComparison.OrdinalIgnoreCase);
    private bool HasAdministrativeRentalScope => _currentUserContext.IsAdmin || _currentUserContext.IsGodMode;
    private bool HasRentalWideReadScope =>
        HasAdministrativeRentalScope ||
        _currentUserContext.HasPermission(Security.PermissionNames.RentalViewAll) ||
        _currentUserContext.HasPermission(Security.PermissionNames.RentalEditAll);
    private bool HasRentalWideWriteScope =>
        HasAdministrativeRentalScope ||
        _currentUserContext.HasPermission(Security.PermissionNames.RentalEditAll);
    private bool HasDeliveryWideReadScope =>
        HasAdministrativeWriteAccess ||
        _currentUserContext.HasPermission(Security.PermissionNames.DeliveryViewAll);

    public bool CanEditCustomers()
        => HasAdministrativeWriteAccess || _currentUserContext.HasPermission(Security.PermissionNames.CustomerEdit);

    public bool CanEditItems()
        => HasAdministrativeWriteAccess || _currentUserContext.HasPermission(Security.PermissionNames.ItemEdit);

    public bool CanEditInvoices()
        => HasAdministrativeWriteAccess || _currentUserContext.HasPermission(Security.PermissionNames.InvoiceEdit);

    public bool CanEditPayments()
        => HasAdministrativeWriteAccess || _currentUserContext.HasPermission(Security.PermissionNames.PaymentEdit);

    public bool CanResetInventory()
        => HasAdministrativeWriteAccess || _currentUserContext.HasPermission(Security.PermissionNames.InventoryReset);

    public bool CanEditRentalProfiles()
        => HasAdministrativeRentalScope ||
           _currentUserContext.HasPermission(Security.PermissionNames.RentalProfileEdit) ||
           _currentUserContext.HasPermission(Security.PermissionNames.RentalEditAll);

    public bool CanEditRentalAssets()
        => HasAdministrativeRentalScope ||
           _currentUserContext.HasPermission(Security.PermissionNames.RentalAssetEdit) ||
           _currentUserContext.HasPermission(Security.PermissionNames.RentalEditAll);

    public bool CanEditDeliveries()
        => HasAdministrativeWriteAccess || _currentUserContext.HasPermission(Security.PermissionNames.DeliveryEdit);

    public string CurrentTenantCode => TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
        _currentUserContext.TenantCode,
        _currentUserContext.OfficeCode);

    public string CurrentOfficeCode => OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(_currentUserContext.OfficeCode);

    public string CurrentScopeType => TenantScopeCatalog.NormalizeScopeTypeOrDefault(
        _currentUserContext.ScopeType,
        TenantScopeCatalog.ScopeOfficeOnly);

    public string CurrentWarehouseCode => OfficeCodeCatalog.GetMainWarehouseCode(CurrentOfficeCode);

    public IReadOnlyList<string> ReadableOfficeCodes => ResolveReadableOfficeCodes(DataArea.General);

    public IReadOnlyList<string> WritableOfficeCodes => ResolveWritableOfficeCodes(DataArea.General);

    public ScopeMatrixSnapshotDto BuildCurrentScopeMatrix()
    {
        var rows = new[]
        {
            BuildScopeMatrixArea(DataArea.General),
            BuildScopeMatrixArea(DataArea.Customers),
            BuildScopeMatrixArea(DataArea.Items),
            BuildScopeMatrixArea(DataArea.Invoices),
            BuildScopeMatrixArea(DataArea.Payments),
            BuildScopeMatrixArea(DataArea.Contracts),
            BuildScopeMatrixArea(DataArea.Reports),
            BuildScopeMatrixArea(DataArea.Rentals),
            BuildScopeMatrixArea(DataArea.Deliveries)
        };

        return new ScopeMatrixSnapshotDto
        {
            GeneratedAtUtc = DateTime.UtcNow,
            Username = _currentUserContext.Username,
            TenantCode = CurrentTenantCode,
            OfficeCode = CurrentOfficeCode,
            ScopeType = CurrentScopeType,
            IsAdmin = IsAdmin,
            HasAdministrativeWriteAccess = HasAdministrativeWriteAccess,
            HasGlobalDataScope = HasGlobalDataScope,
            Areas = rows.ToList()
        };
    }

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

    public string ResolveCustomerResponsibleScopeForCreate(string? requestedOfficeCode, string? fallbackOfficeCode = null)
        => ResolveOperationalOfficeForCreate(requestedOfficeCode, fallbackOfficeCode, DataArea.Customers);

    public string ResolveInvoiceResponsibleScopeForCreate(string? requestedOfficeCode, string? fallbackOfficeCode = null)
        => ResolveOperationalOfficeForCreate(requestedOfficeCode, fallbackOfficeCode, DataArea.Invoices);

    public string ResolvePaymentResponsibleScopeForCreate(string? requestedOfficeCode, string? fallbackOfficeCode = null)
        => ResolveOperationalOfficeForCreate(requestedOfficeCode, fallbackOfficeCode, DataArea.Payments);

    public string ResolveRentalResponsibleScopeForCreate(string? requestedOfficeCode, string? fallbackOfficeCode = null)
        => ResolveOperationalOfficeForCreate(requestedOfficeCode, fallbackOfficeCode, DataArea.Rentals);

    public string ResolveOwningOfficeForOperationalScope(
        string? requestedOwnerOfficeCode,
        string? responsibleOfficeCode = null,
        string? fallbackOwnerOfficeCode = null)
        => OfficeCodeCatalog.ResolveOwningOfficeCode(
            requestedOwnerOfficeCode,
            responsibleOfficeCode,
            fallbackOwnerOfficeCode ?? CurrentOfficeCode);

    public string ResolveTenantForRentalCreate(
        string? requestedTenantCode,
        string? requestedOfficeCode,
        string? fallbackTenantCode = null,
        string? fallbackOfficeCode = null)
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

    public string ResolveScopeForRentalCreate(string? requestedOfficeCode, string? fallbackOfficeCode = null)
    {
        if (HasGlobalDataScope)
        {
            return OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(
                requestedOfficeCode,
                fallbackOfficeCode ?? CurrentOfficeCode);
        }

        var writableOffices = ResolveWritableOfficeCodes(DataArea.Rentals);
        if (OfficeCodeCatalog.TryNormalizeOfficeCode(requestedOfficeCode, out var requestedOffice) &&
            writableOffices.Contains(requestedOffice, StringComparer.OrdinalIgnoreCase))
        {
            return requestedOffice;
        }

        if (OfficeCodeCatalog.TryNormalizeOfficeCode(fallbackOfficeCode, out var fallbackOffice) &&
            writableOffices.Contains(fallbackOffice, StringComparer.OrdinalIgnoreCase))
        {
            return fallbackOffice;
        }

        return writableOffices.FirstOrDefault()
               ?? CurrentOfficeCode;
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

    public IQueryable<CompanyProfile> ApplyCompanyProfileScope(IQueryable<CompanyProfile> query)
    {
        if (HasGlobalDataScope)
            return query;

        var readableOffices = ResolveReadableOfficeCodes(DataArea.General);
        return query.Where(entity => readableOffices.Contains(entity.OfficeCode));
    }

    public IQueryable<Customer> ApplyCustomerScope(IQueryable<Customer> query)
    {
        if (HasGlobalDataScope)
            return query;

        var tenantCode = CurrentTenantCode;
        var readableOffices = ResolveReadableOfficeCodes(DataArea.Customers);
        var tenantOffices = TenantScopeCatalog.GetNormalizedOfficeCodesForTenant(tenantCode);
        return query.Where(entity =>
            entity.TenantCode == tenantCode &&
            ((entity.ResponsibleOfficeCode == OfficeCodeCatalog.Shared &&
              (entity.OfficeCode == OfficeCodeCatalog.Shared ||
               entity.OfficeCode == null ||
               entity.OfficeCode == string.Empty ||
               tenantOffices.Contains(entity.OfficeCode))) ||
             readableOffices.Contains(entity.ResponsibleOfficeCode) ||
             ((entity.ResponsibleOfficeCode == null || entity.ResponsibleOfficeCode == string.Empty) &&
              readableOffices.Contains(entity.OfficeCode))));
    }

    public IQueryable<Customer> ApplySyncCustomerScope(IQueryable<Customer> query)
    {
        if (HasGlobalDataScope)
            return query;

        return ApplyCustomerScope(query);
    }

    public IQueryable<CustomerContract> ApplyCustomerContractScope(IQueryable<CustomerContract> query)
    {
        if (HasGlobalDataScope)
            return query;

        var tenantCode = CurrentTenantCode;
        var readableOffices = ResolveReadableOfficeCodes(DataArea.Contracts);
        var tenantOffices = TenantScopeCatalog.GetNormalizedOfficeCodesForTenant(tenantCode);
        return query.Where(entity =>
            entity.Customer != null &&
            entity.Customer.TenantCode == tenantCode &&
            ((entity.Customer.ResponsibleOfficeCode == OfficeCodeCatalog.Shared &&
              (entity.Customer.OfficeCode == OfficeCodeCatalog.Shared ||
               entity.Customer.OfficeCode == null ||
               entity.Customer.OfficeCode == string.Empty ||
               tenantOffices.Contains(entity.Customer.OfficeCode))) ||
             readableOffices.Contains(entity.Customer.ResponsibleOfficeCode) ||
             ((entity.Customer.ResponsibleOfficeCode == null || entity.Customer.ResponsibleOfficeCode == string.Empty) &&
              readableOffices.Contains(entity.Customer.OfficeCode))));
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

    public IQueryable<Item> ApplySyncItemScope(IQueryable<Item> query)
    {
        if (HasGlobalDataScope)
            return query;

        return ApplyItemScope(query);
    }

    public IQueryable<Invoice> ApplyInvoiceScope(IQueryable<Invoice> query)
    {
        if (HasGlobalDataScope)
            return query;

        var tenantCode = CurrentTenantCode;
        var readableOffices = ResolveReadableOfficeCodes(DataArea.Invoices);
        var tenantOffices = TenantScopeCatalog.GetNormalizedOfficeCodesForTenant(tenantCode);
        return query.Where(entity =>
            entity.TenantCode == tenantCode &&
            ((entity.ResponsibleOfficeCode == OfficeCodeCatalog.Shared &&
              (entity.OfficeCode == OfficeCodeCatalog.Shared ||
               entity.OfficeCode == null ||
               entity.OfficeCode == string.Empty ||
               tenantOffices.Contains(entity.OfficeCode))) ||
             readableOffices.Contains(entity.ResponsibleOfficeCode) ||
             ((entity.ResponsibleOfficeCode == null || entity.ResponsibleOfficeCode == string.Empty) &&
              readableOffices.Contains(entity.OfficeCode))));
    }

    public IQueryable<Invoice> ApplySyncInvoiceScope(IQueryable<Invoice> query)
    {
        if (HasGlobalDataScope || HasDeliveryWideReadScope)
            return query;

        return ApplyInvoiceScope(query);
    }

    public IQueryable<Payment> ApplyPaymentScope(IQueryable<Payment> query)
    {
        if (HasGlobalDataScope)
            return query;

        var tenantCode = CurrentTenantCode;
        var readableOffices = ResolveReadableOfficeCodes(DataArea.Payments);
        var tenantOffices = TenantScopeCatalog.GetNormalizedOfficeCodesForTenant(tenantCode);
        return query.Where(entity =>
            entity.Invoice != null &&
            entity.Invoice.TenantCode == tenantCode &&
            ((entity.Invoice.ResponsibleOfficeCode == OfficeCodeCatalog.Shared &&
              (entity.Invoice.OfficeCode == OfficeCodeCatalog.Shared ||
               entity.Invoice.OfficeCode == null ||
               entity.Invoice.OfficeCode == string.Empty ||
               tenantOffices.Contains(entity.Invoice.OfficeCode))) ||
             readableOffices.Contains(entity.Invoice.ResponsibleOfficeCode) ||
             ((entity.Invoice.ResponsibleOfficeCode == null || entity.Invoice.ResponsibleOfficeCode == string.Empty) &&
              readableOffices.Contains(entity.Invoice.OfficeCode))));
    }

    public IQueryable<TransactionRecord> ApplyTransactionScope(IQueryable<TransactionRecord> query)
    {
        if (HasGlobalDataScope)
            return query;

        var tenantCode = CurrentTenantCode;
        var readableOffices = ResolveReadableOfficeCodes(DataArea.Payments);
        var tenantOffices = TenantScopeCatalog.GetNormalizedOfficeCodesForTenant(tenantCode);
        return query.Where(entity =>
            entity.TenantCode == tenantCode &&
            ((entity.ResponsibleOfficeCode == OfficeCodeCatalog.Shared &&
              (entity.OfficeCode == OfficeCodeCatalog.Shared ||
               entity.OfficeCode == null ||
               entity.OfficeCode == string.Empty ||
               tenantOffices.Contains(entity.OfficeCode))) ||
             readableOffices.Contains(entity.ResponsibleOfficeCode) ||
             ((entity.ResponsibleOfficeCode == null || entity.ResponsibleOfficeCode == string.Empty) &&
              readableOffices.Contains(entity.OfficeCode))));
    }

    public IQueryable<TransactionAttachment> ApplyTransactionAttachmentScope(IQueryable<TransactionAttachment> query)
    {
        if (HasGlobalDataScope)
            return query;

        var tenantCode = CurrentTenantCode;
        var readableOffices = ResolveReadableOfficeCodes(DataArea.Payments);
        var tenantOffices = TenantScopeCatalog.GetNormalizedOfficeCodesForTenant(tenantCode);
        return query.Where(entity =>
            entity.Transaction != null &&
            entity.Transaction.TenantCode == tenantCode &&
            ((entity.Transaction.ResponsibleOfficeCode == OfficeCodeCatalog.Shared &&
              (entity.Transaction.OfficeCode == OfficeCodeCatalog.Shared ||
               entity.Transaction.OfficeCode == null ||
               entity.Transaction.OfficeCode == string.Empty ||
               tenantOffices.Contains(entity.Transaction.OfficeCode))) ||
             readableOffices.Contains(entity.Transaction.ResponsibleOfficeCode) ||
             ((entity.Transaction.ResponsibleOfficeCode == null || entity.Transaction.ResponsibleOfficeCode == string.Empty) &&
              readableOffices.Contains(entity.Transaction.OfficeCode))));
    }

    public IQueryable<InventoryTransfer> ApplyInventoryTransferScope(IQueryable<InventoryTransfer> query)
    {
        if (HasGlobalDataScope)
            return query;

        var tenantCode = CurrentTenantCode;
        var tenantOffices = TenantScopeCatalog.GetNormalizedOfficeCodesForTenant(tenantCode);
        if (HasDeliveryWideReadScope)
        {
            return query.Where(entity =>
                entity.TenantCode == tenantCode &&
                tenantOffices.Contains(entity.SourceOfficeCode) &&
                tenantOffices.Contains(entity.TargetOfficeCode));
        }

        var readableOffices = ResolveReadableOfficeCodes(DataArea.Deliveries);
        return query.Where(entity =>
            entity.TenantCode == tenantCode &&
            tenantOffices.Contains(entity.SourceOfficeCode) &&
            tenantOffices.Contains(entity.TargetOfficeCode) &&
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
        if (HasGlobalDataScope || HasAdministrativeRentalScope)
            return query;

        var tenantCode = CurrentTenantCode;
        var readableOffices = ResolveReadableOfficeCodes(DataArea.Rentals);
        var tenantOffices = TenantScopeCatalog.GetNormalizedOfficeCodesForTenant(tenantCode);
        return query.Where(entity =>
            entity.TenantCode == tenantCode &&
            ((entity.ResponsibleOfficeCode == OfficeCodeCatalog.Shared &&
              (entity.OfficeCode == OfficeCodeCatalog.Shared ||
               entity.OfficeCode == null ||
               entity.OfficeCode == string.Empty ||
               tenantOffices.Contains(entity.OfficeCode))) ||
             readableOffices.Contains(entity.ResponsibleOfficeCode) ||
             ((entity.ResponsibleOfficeCode == null || entity.ResponsibleOfficeCode == string.Empty) &&
              readableOffices.Contains(entity.OfficeCode))));
    }

    public IQueryable<RentalAsset> ApplyRentalAssetScope(IQueryable<RentalAsset> query)
    {
        if (HasGlobalDataScope || HasAdministrativeRentalScope)
            return query;

        var tenantCode = CurrentTenantCode;
        var readableOffices = ResolveReadableOfficeCodes(DataArea.Rentals);
        var tenantOffices = TenantScopeCatalog.GetNormalizedOfficeCodesForTenant(tenantCode);
        return query.Where(entity =>
            entity.TenantCode == tenantCode &&
            ((entity.ResponsibleOfficeCode == OfficeCodeCatalog.Shared &&
              (entity.OfficeCode == OfficeCodeCatalog.Shared ||
               entity.OfficeCode == null ||
               entity.OfficeCode == string.Empty ||
               tenantOffices.Contains(entity.OfficeCode))) ||
             readableOffices.Contains(entity.ResponsibleOfficeCode) ||
             ((entity.ResponsibleOfficeCode == null || entity.ResponsibleOfficeCode == string.Empty) &&
              readableOffices.Contains(entity.OfficeCode))));
    }

    public IQueryable<RentalAssetAssignmentHistory> ApplyRentalAssignmentHistoryScope(IQueryable<RentalAssetAssignmentHistory> query)
    {
        if (HasGlobalDataScope || HasAdministrativeRentalScope)
            return query;

        var tenantCode = CurrentTenantCode;
        var readableOffices = ResolveReadableOfficeCodes(DataArea.Rentals);
        var tenantOffices = TenantScopeCatalog.GetNormalizedOfficeCodesForTenant(tenantCode);
        return query.Where(entity =>
            entity.TenantCode == tenantCode &&
            ((entity.ResponsibleOfficeCode == OfficeCodeCatalog.Shared &&
              (entity.OfficeCode == OfficeCodeCatalog.Shared ||
               entity.OfficeCode == null ||
               entity.OfficeCode == string.Empty ||
               tenantOffices.Contains(entity.OfficeCode))) ||
             readableOffices.Contains(entity.ResponsibleOfficeCode) ||
             ((entity.ResponsibleOfficeCode == null || entity.ResponsibleOfficeCode == string.Empty) &&
              readableOffices.Contains(entity.OfficeCode))));
    }

    public IQueryable<RentalBillingLog> ApplyRentalBillingLogScope(IQueryable<RentalBillingLog> query)
    {
        if (HasGlobalDataScope || HasAdministrativeRentalScope)
            return query;

        var tenantCode = CurrentTenantCode;
        var readableOffices = ResolveReadableOfficeCodes(DataArea.Rentals);
        var tenantOffices = TenantScopeCatalog.GetNormalizedOfficeCodesForTenant(tenantCode);
        return query.Where(entity =>
            entity.TenantCode == tenantCode &&
            ((entity.ResponsibleOfficeCode == OfficeCodeCatalog.Shared &&
              (entity.OfficeCode == OfficeCodeCatalog.Shared ||
               entity.OfficeCode == null ||
               entity.OfficeCode == string.Empty ||
               tenantOffices.Contains(entity.OfficeCode))) ||
             readableOffices.Contains(entity.ResponsibleOfficeCode) ||
             ((entity.ResponsibleOfficeCode == null || entity.ResponsibleOfficeCode == string.Empty) &&
              readableOffices.Contains(entity.OfficeCode))));
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
            return query.Where(entity => entity.Item != null && !entity.Item.IsDeleted);

        var tenantCode = CurrentTenantCode;
        var readableWarehouses = ResolveReadableOfficeCodes(DataArea.Items)
            .Select(OfficeCodeCatalog.GetMainWarehouseCode)
            .Distinct()
            .ToList();
        var readableOffices = ResolveReadableOfficeCodes(DataArea.Items);
        return query.Where(entity =>
            readableWarehouses.Contains(entity.WarehouseCode) &&
            entity.Item != null &&
            !entity.Item.IsDeleted &&
            entity.Item.TenantCode == tenantCode &&
            (entity.Item.OfficeCode == OfficeCodeCatalog.Shared || readableOffices.Contains(entity.Item.OfficeCode)));
    }

    public bool CanReadOfficeForCustomers(string? officeCode, string? tenantCode = null, string? fallbackOfficeCode = null)
        => CanReadOffice(officeCode, tenantCode, DataArea.Customers, fallbackOfficeCode);

    public bool CanWriteOfficeForCustomers(string? officeCode, string? tenantCode = null, string? fallbackOfficeCode = null)
        => CanWriteOffice(officeCode, tenantCode, DataArea.Customers, fallbackOfficeCode);

    public bool CanReadOfficeForItems(string? officeCode, string? tenantCode = null, string? fallbackOfficeCode = null)
        => CanReadOffice(officeCode, tenantCode, DataArea.Items, fallbackOfficeCode);

    public bool CanWriteOfficeForItems(string? officeCode, string? tenantCode = null, string? fallbackOfficeCode = null)
        => CanWriteOffice(officeCode, tenantCode, DataArea.Items, fallbackOfficeCode);

    public bool CanReadOfficeForInvoices(string? officeCode, string? tenantCode = null, string? fallbackOfficeCode = null)
        => CanReadOffice(officeCode, tenantCode, DataArea.Invoices, fallbackOfficeCode);

    public bool CanWriteOfficeForInvoices(string? officeCode, string? tenantCode = null, string? fallbackOfficeCode = null)
        => CanWriteOffice(officeCode, tenantCode, DataArea.Invoices, fallbackOfficeCode);

    public bool CanReadOfficeForPayments(string? officeCode, string? tenantCode = null, string? fallbackOfficeCode = null)
        => CanReadOffice(officeCode, tenantCode, DataArea.Payments, fallbackOfficeCode);

    public bool CanWriteOfficeForPayments(string? officeCode, string? tenantCode = null, string? fallbackOfficeCode = null)
        => CanWriteOffice(officeCode, tenantCode, DataArea.Payments, fallbackOfficeCode);

    public bool CanReadOfficeForContracts(string? officeCode, string? tenantCode = null, string? fallbackOfficeCode = null)
        => CanReadOffice(officeCode, tenantCode, DataArea.Contracts, fallbackOfficeCode);

    public bool CanWriteOfficeForContracts(string? officeCode, string? tenantCode = null, string? fallbackOfficeCode = null)
        => CanWriteOffice(officeCode, tenantCode, DataArea.Contracts, fallbackOfficeCode);

    public bool CanReadOfficeForReports(string? officeCode, string? tenantCode = null, string? fallbackOfficeCode = null)
        => CanReadOffice(officeCode, tenantCode, DataArea.Reports, fallbackOfficeCode);

    public bool CanReadOfficeForRentals(string? officeCode, string? tenantCode = null, string? fallbackOfficeCode = null)
        => CanReadOffice(officeCode, tenantCode, DataArea.Rentals, fallbackOfficeCode);

    public bool CanWriteOfficeForRentals(string? officeCode, string? tenantCode = null, string? fallbackOfficeCode = null)
        => CanWriteOffice(officeCode, tenantCode, DataArea.Rentals, fallbackOfficeCode);

    public bool CanReadOfficeForDeliveries(string? officeCode, string? tenantCode = null, string? fallbackOfficeCode = null)
        => HasDeliveryWideReadScope || CanReadOffice(officeCode, tenantCode, DataArea.Deliveries, fallbackOfficeCode);

    public bool CanWriteOfficeForDeliveries(string? officeCode, string? tenantCode = null, string? fallbackOfficeCode = null)
        => CanWriteOffice(officeCode, tenantCode, DataArea.Deliveries, fallbackOfficeCode);

    public bool CanReadInventoryTransferRoute(string? sourceOfficeCode, string? targetOfficeCode, string? tenantCode)
    {
        if (HasGlobalDataScope)
            return true;

        if (!IsInventoryTransferRouteCompatibleWithTenant(sourceOfficeCode, targetOfficeCode, tenantCode))
            return false;

        if (!string.Equals(TenantScopeCatalog.NormalizeTenantCodeOrDefault(tenantCode), CurrentTenantCode, StringComparison.OrdinalIgnoreCase))
            return false;

        return HasDeliveryWideReadScope ||
               CanReadOfficeForDeliveries(sourceOfficeCode, tenantCode) ||
               CanReadOfficeForDeliveries(targetOfficeCode, tenantCode);
    }

    public bool CanWriteInventoryTransferRoute(string? sourceOfficeCode, string? targetOfficeCode, string? tenantCode)
    {
        if (HasGlobalDataScope)
            return true;

        if (!IsInventoryTransferRouteCompatibleWithTenant(sourceOfficeCode, targetOfficeCode, tenantCode))
            return false;

        if (!string.Equals(TenantScopeCatalog.NormalizeTenantCodeOrDefault(tenantCode), CurrentTenantCode, StringComparison.OrdinalIgnoreCase))
            return false;

        return CanWriteOfficeForDeliveries(sourceOfficeCode, tenantCode) ||
               CanWriteOfficeForDeliveries(targetOfficeCode, tenantCode);
    }

    public bool CanReadOfficeForCompanyProfiles(string? officeCode)
        => CanReadOffice(officeCode, null, DataArea.General);

    public bool CanWriteOfficeForCompanyProfiles(string? officeCode)
        => CanWriteOffice(officeCode, null, DataArea.General);

    public async Task<bool> HasAdministrativeWriteAccessAsync(CancellationToken cancellationToken = default)
    {
        if (HasAdministrativeWriteAccess)
            return true;

        if (_currentUserContext.UserId is not Guid userId)
            return false;

        var user = await _dbContext.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == userId, cancellationToken);
        if (user is null)
            return false;

        if (string.Equals(user.Role, "Admin", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private bool CanReadOffice(string? officeCode, string? tenantCode, DataArea area, string? fallbackOfficeCode = null)
    {
        if (HasGlobalDataScope)
            return true;

        if (!IsExplicitTenantCompatibleWithAccessOffice(tenantCode, officeCode, fallbackOfficeCode))
            return false;

        var normalizedTenant = ResolveEntityTenantCode(tenantCode, officeCode, fallbackOfficeCode);
        if (!string.Equals(normalizedTenant, CurrentTenantCode, StringComparison.OrdinalIgnoreCase))
            return false;

        foreach (var normalizedOffice in ResolveOfficeScopesForAccess(officeCode, fallbackOfficeCode))
        {
            if (string.Equals(normalizedOffice, OfficeCodeCatalog.Shared, StringComparison.OrdinalIgnoreCase))
                return true;

            if (ResolveReadableOfficeCodes(area).Contains(normalizedOffice, StringComparer.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private bool CanWriteOffice(string? officeCode, string? tenantCode, DataArea area, string? fallbackOfficeCode = null)
    {
        if (HasGlobalDataScope)
            return true;

        if (!IsExplicitTenantCompatibleWithAccessOffice(tenantCode, officeCode, fallbackOfficeCode))
            return false;

        var normalizedTenant = ResolveEntityTenantCode(tenantCode, officeCode, fallbackOfficeCode);
        if (!string.Equals(normalizedTenant, CurrentTenantCode, StringComparison.OrdinalIgnoreCase))
            return false;

        foreach (var normalizedOffice in ResolveOfficeScopesForAccess(officeCode, fallbackOfficeCode))
        {
            if (string.Equals(normalizedOffice, OfficeCodeCatalog.Shared, StringComparison.OrdinalIgnoreCase))
                return string.Equals(CurrentScopeType, TenantScopeCatalog.ScopeTenantAll, StringComparison.OrdinalIgnoreCase);

            if (ResolveWritableOfficeCodes(area).Contains(normalizedOffice, StringComparer.OrdinalIgnoreCase))
                return true;
        }

        return false;
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

    private string ResolveEntityTenantCode(string? tenantCode, string? officeCode, string? fallbackOfficeCode = null)
    {
        var tenantOfficeCode = ResolveTenantOfficeCodeForAccess(officeCode, fallbackOfficeCode);
        if (!string.IsNullOrWhiteSpace(tenantOfficeCode))
        {
            return TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
                tenantCode,
                tenantOfficeCode,
                CurrentTenantCode,
                CurrentOfficeCode);
        }

        return TenantScopeCatalog.NormalizeTenantCodeOrDefault(tenantCode, CurrentTenantCode);
    }

    private static bool IsExplicitTenantCompatibleWithAccessOffice(
        string? tenantCode,
        string? officeCode,
        string? fallbackOfficeCode = null)
    {
        if (!TenantScopeCatalog.TryNormalizeTenantCode(tenantCode, out var normalizedTenantCode))
            return true;

        if (OfficeCodeCatalog.TryNormalizeOfficeCode(officeCode, out var normalizedOfficeCode))
            return TenantScopeCatalog.TenantContainsOffice(normalizedTenantCode, normalizedOfficeCode);

        if (OfficeCodeCatalog.IsSharedOfficeCode(officeCode))
        {
            if (OfficeCodeCatalog.TryNormalizeOfficeCode(fallbackOfficeCode, out var normalizedFallbackOfficeCode))
                return TenantScopeCatalog.TenantContainsOffice(normalizedTenantCode, normalizedFallbackOfficeCode);

            return true;
        }

        if (OfficeCodeCatalog.TryNormalizeOfficeCode(fallbackOfficeCode, out var fallbackOffice))
            return TenantScopeCatalog.TenantContainsOffice(normalizedTenantCode, fallbackOffice);

        return true;
    }

    private static IEnumerable<string> ResolveOfficeScopesForAccess(string? officeCode, string? fallbackOfficeCode = null)
    {
        if (OfficeCodeCatalog.TryNormalizeScope(officeCode, out var normalizedOffice))
        {
            yield return normalizedOffice;
            yield break;
        }

        if (OfficeCodeCatalog.TryNormalizeScope(fallbackOfficeCode, out var normalizedFallback))
        {
            yield return normalizedFallback;
        }
    }

    private static string? ResolveTenantOfficeCodeForAccess(string? officeCode, string? fallbackOfficeCode = null)
    {
        if (OfficeCodeCatalog.TryNormalizeOfficeCode(officeCode, out var normalizedOffice))
            return normalizedOffice;

        if (OfficeCodeCatalog.TryNormalizeOfficeCode(fallbackOfficeCode, out var normalizedFallback))
            return normalizedFallback;

        return null;
    }

    private static bool IsInventoryTransferRouteCompatibleWithTenant(
        string? sourceOfficeCode,
        string? targetOfficeCode,
        string? tenantCode)
    {
        if (!TenantScopeCatalog.TryNormalizeTenantCode(tenantCode, out var normalizedTenantCode))
            return false;

        return TenantScopeCatalog.TenantContainsOffice(normalizedTenantCode, sourceOfficeCode) &&
               TenantScopeCatalog.TenantContainsOffice(normalizedTenantCode, targetOfficeCode);
    }

    private string ResolveOperationalOfficeForCreate(string? requestedOfficeCode, string? fallbackOfficeCode, DataArea area)
    {
        if (HasGlobalDataScope)
        {
            return OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(
                requestedOfficeCode,
                fallbackOfficeCode ?? CurrentOfficeCode);
        }

        var writableOffices = ResolveWritableOfficeCodes(area);
        if (OfficeCodeCatalog.TryNormalizeOfficeCode(requestedOfficeCode, out var requestedOffice) &&
            writableOffices.Contains(requestedOffice, StringComparer.OrdinalIgnoreCase))
        {
            return requestedOffice;
        }

        if (OfficeCodeCatalog.TryNormalizeOfficeCode(fallbackOfficeCode, out var fallbackOffice) &&
            writableOffices.Contains(fallbackOffice, StringComparer.OrdinalIgnoreCase))
        {
            return fallbackOffice;
        }

        return writableOffices.FirstOrDefault()
               ?? CurrentOfficeCode;
    }

    private IReadOnlyList<string> ResolveReadableOfficeCodes(DataArea area)
    {
        if (HasGlobalDataScope)
            return OfficeCodeCatalog.All;

        if (area == DataArea.Rentals && HasAdministrativeRentalScope)
            return OfficeCodeCatalog.All;

        if (area == DataArea.Rentals && HasRentalWideReadScope)
        {
            return TenantScopeCatalog.ResolveScopedOfficeCodes(
                    CurrentOfficeCode,
                    CurrentTenantCode,
                    CurrentScopeType,
                    hasGlobalScope: false,
                    hasTenantScope: true)
                .ToList();
        }

        if (string.Equals(CurrentScopeType, TenantScopeCatalog.ScopeTenantAll, StringComparison.OrdinalIgnoreCase))
        {
            return TenantScopeCatalog.ResolveScopedOfficeCodes(
                    CurrentOfficeCode,
                    CurrentTenantCode,
                    CurrentScopeType,
                    hasGlobalScope: false,
                    hasTenantScope: true)
                .ToList();
        }

        var readable = TenantScopeCatalog.ResolveScopedOfficeCodes(
            CurrentOfficeCode,
            CurrentTenantCode,
            TenantScopeCatalog.ScopeOfficeOnly,
            hasGlobalScope: false,
            hasTenantScope: false);

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

        if (area == DataArea.Rentals && HasRentalWideWriteScope)
        {
            return TenantScopeCatalog.ResolveScopedOfficeCodes(
                    CurrentOfficeCode,
                    CurrentTenantCode,
                    CurrentScopeType,
                    hasGlobalScope: false,
                    hasTenantScope: true)
                .ToList();
        }

        if (string.Equals(CurrentScopeType, TenantScopeCatalog.ScopeTenantAll, StringComparison.OrdinalIgnoreCase))
        {
            return TenantScopeCatalog.ResolveScopedOfficeCodes(
                    CurrentOfficeCode,
                    CurrentTenantCode,
                    CurrentScopeType,
                    hasGlobalScope: false,
                    hasTenantScope: true)
                .ToList();
        }

        var writable = TenantScopeCatalog.ResolveScopedOfficeCodes(
            CurrentOfficeCode,
            CurrentTenantCode,
            TenantScopeCatalog.ScopeOfficeOnly,
            hasGlobalScope: false,
            hasTenantScope: false);

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

    private ScopeMatrixAreaDto BuildScopeMatrixArea(DataArea area)
    {
        var readable = ResolveReadableOfficeCodes(area)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var writable = ResolveWritableOfficeCodes(area)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ScopeMatrixAreaDto
        {
            AreaCode = GetAreaCode(area),
            AreaDisplayName = GetAreaDisplayName(area),
            ReadableOfficeCodes = readable,
            WritableOfficeCodes = writable,
            Note = BuildAreaNote(area, readable, writable)
        };
    }

    private static string GetAreaCode(DataArea area)
        => area switch
        {
            DataArea.General => "general",
            DataArea.Customers => "customers",
            DataArea.Items => "items",
            DataArea.Invoices => "invoices",
            DataArea.Payments => "payments",
            DataArea.Contracts => "contracts",
            DataArea.Reports => "reports",
            DataArea.Rentals => "rentals",
            DataArea.Deliveries => "deliveries",
            _ => area.ToString().ToLowerInvariant()
        };

    private static string GetAreaDisplayName(DataArea area)
        => area switch
        {
            DataArea.General => "기본 범위",
            DataArea.Customers => "거래처",
            DataArea.Items => "품목/재고",
            DataArea.Invoices => "판매/구매",
            DataArea.Payments => "수금/지급",
            DataArea.Contracts => "계약서",
            DataArea.Reports => "집계/리포트",
            DataArea.Rentals => "렌탈",
            DataArea.Deliveries => "납품/배송",
            _ => area.ToString()
        };

    private string BuildAreaNote(DataArea area, IReadOnlyCollection<string> readable, IReadOnlyCollection<string> writable)
    {
        if (HasGlobalDataScope)
            return "관리자 전역 범위입니다.";

        if (area == DataArea.Rentals && HasAdministrativeRentalScope)
            return "렌탈 관리자 범위입니다.";

        if (area == DataArea.Rentals && HasRentalWideReadScope && HasRentalWideWriteScope)
            return "렌탈 전체 권한으로 업체 전체 범위를 사용합니다.";

        if (area == DataArea.Rentals && HasRentalWideReadScope)
            return "렌탈 전체 조회 권한으로 업체 전체 범위를 사용합니다.";

        if (area == DataArea.Deliveries && HasDeliveryWideReadScope)
            return "납품 전체 조회 권한으로 상위 범위를 사용합니다.";

        if (string.Equals(CurrentScopeType, TenantScopeCatalog.ScopeTenantAll, StringComparison.OrdinalIgnoreCase))
            return "업체 전체 범위입니다.";

        var sharedReadable = readable
            .Where(code => !string.Equals(code, CurrentOfficeCode, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var sharedWritable = writable
            .Where(code => !string.Equals(code, CurrentOfficeCode, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (sharedReadable.Count == 0 && sharedWritable.Count == 0)
            return "현재 지점 기준 범위입니다.";

        if (sharedReadable.Count > 0 && sharedWritable.Count == 0)
            return $"연동 정책으로 읽기만 확장됨: {string.Join(", ", sharedReadable)}";

        if (sharedWritable.Count > 0)
            return $"연동 정책으로 쓰기 가능 지점 포함: {string.Join(", ", sharedWritable)}";

        return string.Empty;
    }
}
