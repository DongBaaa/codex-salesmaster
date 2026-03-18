using System.Collections.Generic;
using System.Linq;
using SalesMaster.Desktop.App.Data;
using SalesMaster.Desktop.App.Services;
using SalesMaster.Shared.Contracts;

namespace SalesMaster.Desktop.App.ViewModels;

public sealed class InventoryItemRow
{
    public InventoryItemRow(
        LocalItem source,
        IReadOnlyDictionary<string, decimal> officeQuantities,
        string? selectedOfficeCode)
    {
        Source = source;
        _officeQuantities = new Dictionary<string, decimal>(officeQuantities, StringComparer.OrdinalIgnoreCase);
        UsenetQuantity = GetOfficeQuantity(DomainConstants.OfficeUsenet);
        ItworldQuantity = GetOfficeQuantity(DomainConstants.OfficeItworld);
        YeonsuQuantity = GetOfficeQuantity(DomainConstants.OfficeYeonsu);
        DisplayedQuantity = GetOfficeQuantity(selectedOfficeCode);
    }

    private readonly IReadOnlyDictionary<string, decimal> _officeQuantities;

    public LocalItem Source { get; }
    public Guid Id => Source.Id;
    public string NameOriginal => Source.NameOriginal;
    public string SpecificationOriginal => Source.SpecificationOriginal;
    public string CategoryName => Source.CategoryName;
    public decimal UsenetQuantity { get; }
    public decimal ItworldQuantity { get; }
    public decimal YeonsuQuantity { get; }
    public decimal DisplayedQuantity { get; }
    public decimal TotalQuantity => _officeQuantities.Values.Sum();

    public decimal GetOfficeQuantity(string? officeCode)
    {
        var normalizedOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode, DomainConstants.OfficeUsenet);
        return _officeQuantities.TryGetValue(normalizedOfficeCode, out var quantity)
            ? quantity
            : 0m;
    }
}
