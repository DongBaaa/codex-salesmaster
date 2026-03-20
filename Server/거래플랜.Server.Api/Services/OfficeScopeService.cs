using 거래플랜.Server.Api.Domain;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Server.Api.Services;

public sealed class OfficeScopeService
{
    private readonly ICurrentUserContext _currentUserContext;

    public OfficeScopeService(ICurrentUserContext currentUserContext)
    {
        _currentUserContext = currentUserContext;
    }

    public bool IsAdmin => _currentUserContext.IsAdmin;

    public string CurrentOfficeCode => OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(_currentUserContext.OfficeCode);

    public string CurrentWarehouseCode => OfficeCodeCatalog.GetMainWarehouseCode(CurrentOfficeCode);

    public bool CanReadOffice(string? officeCode)
    {
        if (IsAdmin)
            return true;

        var normalized = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(officeCode);
        return string.Equals(normalized, OfficeCodeCatalog.Shared, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, CurrentOfficeCode, StringComparison.OrdinalIgnoreCase);
    }

    public bool CanWriteOffice(string? officeCode)
    {
        if (IsAdmin)
            return true;

        var normalized = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(officeCode);
        return string.Equals(normalized, CurrentOfficeCode, StringComparison.OrdinalIgnoreCase);
    }

    public bool CanReadWarehouse(string? warehouseCode)
    {
        if (IsAdmin)
            return true;

        var normalized = OfficeCodeCatalog.NormalizeWarehouseCodeOrDefault(warehouseCode, CurrentOfficeCode, CurrentOfficeCode);
        return string.Equals(normalized, CurrentWarehouseCode, StringComparison.OrdinalIgnoreCase);
    }

    public bool CanWriteWarehouse(string? warehouseCode) => CanReadWarehouse(warehouseCode);

    public string ResolveScopeForCreate(string? requestedOfficeCode, string? fallbackOfficeCode = null)
    {
        if (IsAdmin)
        {
            return OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(
                requestedOfficeCode,
                fallbackOfficeCode ?? CurrentOfficeCode);
        }

        return CurrentOfficeCode;
    }

    public IQueryable<CustomerMaster> ApplyCustomerMasterScope(IQueryable<CustomerMaster> query)
    {
        if (IsAdmin)
            return query;

        var officeCode = CurrentOfficeCode;
        return query.Where(entity =>
            entity.OfficeCode == OfficeCodeCatalog.Shared ||
            entity.OfficeCode == officeCode);
    }

    public IQueryable<Customer> ApplyCustomerScope(IQueryable<Customer> query)
    {
        if (IsAdmin)
            return query;

        var officeCode = CurrentOfficeCode;
        return query.Where(entity =>
            entity.OfficeCode == OfficeCodeCatalog.Shared ||
            entity.OfficeCode == officeCode);
    }

    public IQueryable<CustomerContract> ApplyCustomerContractScope(IQueryable<CustomerContract> query)
    {
        if (IsAdmin)
            return query;

        var officeCode = CurrentOfficeCode;
        return query.Where(entity =>
            entity.Customer != null &&
            (entity.Customer.OfficeCode == OfficeCodeCatalog.Shared || entity.Customer.OfficeCode == officeCode));
    }

    public IQueryable<Item> ApplyItemScope(IQueryable<Item> query)
    {
        if (IsAdmin)
            return query;

        var officeCode = CurrentOfficeCode;
        return query.Where(entity =>
            entity.OfficeCode == OfficeCodeCatalog.Shared ||
            entity.OfficeCode == officeCode);
    }

    public IQueryable<Invoice> ApplyInvoiceScope(IQueryable<Invoice> query)
    {
        if (IsAdmin)
            return query;

        var officeCode = CurrentOfficeCode;
        return query.Where(entity =>
            entity.OfficeCode == OfficeCodeCatalog.Shared ||
            entity.OfficeCode == officeCode);
    }

    public IQueryable<Payment> ApplyPaymentScope(IQueryable<Payment> query)
    {
        if (IsAdmin)
            return query;

        var officeCode = CurrentOfficeCode;
        return query.Where(entity =>
            entity.Invoice != null &&
            (entity.Invoice.OfficeCode == OfficeCodeCatalog.Shared || entity.Invoice.OfficeCode == officeCode));
    }

    public IQueryable<ItemWarehouseStock> ApplyWarehouseScope(IQueryable<ItemWarehouseStock> query)
    {
        if (IsAdmin)
            return query;

        var warehouseCode = CurrentWarehouseCode;
        return query.Where(entity => entity.WarehouseCode == warehouseCode);
    }

    public IQueryable<ItemWarehouseStock> ApplyItemWarehouseStockScope(IQueryable<ItemWarehouseStock> query)
    {
        if (IsAdmin)
            return query;

        var officeCode = CurrentOfficeCode;
        var warehouseCode = CurrentWarehouseCode;
        return query.Where(entity =>
            entity.WarehouseCode == warehouseCode &&
            entity.Item != null &&
            (entity.Item.OfficeCode == OfficeCodeCatalog.Shared || entity.Item.OfficeCode == officeCode));
    }
}
