using System.Collections.Generic;
using System.Linq;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.ViewModels;

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
