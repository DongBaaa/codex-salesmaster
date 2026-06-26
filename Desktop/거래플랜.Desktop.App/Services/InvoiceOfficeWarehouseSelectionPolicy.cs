using 거래플랜.Desktop.App.Data;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

public static class InvoiceOfficeWarehouseSelectionPolicy
{
    public static string ResolveSelectableOfficeCode(
        string? requestedOfficeCode,
        IEnumerable<LocalOffice> selectableOffices,
        string? fallbackOfficeCode)
    {
        var selectableOfficeCodes = selectableOffices
            .Select(office => office.Code)
            .Where(code => OfficeCodeCatalog.TryNormalizeOfficeCode(code, out _))
            .Select(code => OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(code, DomainConstants.OfficeUsenet))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (TryResolveFromSelection(requestedOfficeCode, selectableOfficeCodes, out var requested))
            return requested;

        if (TryResolveFromSelection(fallbackOfficeCode, selectableOfficeCodes, out var fallback))
            return fallback;

        return selectableOfficeCodes.FirstOrDefault()
               ?? OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(fallbackOfficeCode, DomainConstants.OfficeUsenet);
    }

    public static IReadOnlyList<LocalWarehouse> FilterWarehousesForOffice(
        IEnumerable<LocalWarehouse> warehouses,
        string? officeCode)
    {
        var normalizedOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(
            officeCode,
            DomainConstants.OfficeUsenet);

        return warehouses
            .Where(warehouse => IsWarehouseForOffice(warehouse, normalizedOfficeCode))
            .OrderBy(warehouse => warehouse.Name)
            .ThenBy(warehouse => warehouse.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string ResolveWarehouseCode(
        string? requestedWarehouseCode,
        string? officeCode,
        IEnumerable<LocalWarehouse> selectableWarehouses)
    {
        var normalizedOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(
            officeCode,
            DomainConstants.OfficeUsenet);
        var warehouseOptions = FilterWarehousesForOffice(selectableWarehouses, normalizedOfficeCode);
        var normalizedRequestedWarehouseCode = (requestedWarehouseCode ?? string.Empty).Trim().ToUpperInvariant();

        if (!string.IsNullOrWhiteSpace(normalizedRequestedWarehouseCode) &&
            warehouseOptions.Any(warehouse =>
                string.Equals(warehouse.Code, normalizedRequestedWarehouseCode, StringComparison.OrdinalIgnoreCase)))
        {
            return normalizedRequestedWarehouseCode;
        }

        return warehouseOptions.FirstOrDefault()?.Code
               ?? OfficeCodeCatalog.GetMainWarehouseCode(normalizedOfficeCode);
    }

    private static bool IsWarehouseForOffice(LocalWarehouse warehouse, string normalizedOfficeCode)
        => string.Equals(
            OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(warehouse.OfficeCode, DomainConstants.OfficeUsenet),
            normalizedOfficeCode,
            StringComparison.OrdinalIgnoreCase);

    private static bool TryResolveFromSelection(
        string? requestedOfficeCode,
        IReadOnlyCollection<string> selectableOfficeCodes,
        out string resolvedOfficeCode)
    {
        resolvedOfficeCode = string.Empty;
        if (!OfficeCodeCatalog.TryNormalizeOfficeCode(requestedOfficeCode, out var normalizedOfficeCode))
            return false;

        if (!selectableOfficeCodes.Contains(normalizedOfficeCode, StringComparer.OrdinalIgnoreCase))
            return false;

        resolvedOfficeCode = normalizedOfficeCode;
        return true;
    }
}
