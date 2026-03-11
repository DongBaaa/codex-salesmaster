using SalesMaster.Desktop.App.Data;
using SalesMaster.Desktop.App.Services;

namespace SalesMaster.Desktop.App.ViewModels;

public sealed class InventoryItemRow
{
    public InventoryItemRow(
        LocalItem source,
        decimal uznetQuantity,
        decimal yeonsuQuantity,
        string? selectedOfficeCode)
    {
        Source = source;
        UznetQuantity = uznetQuantity;
        YeonsuQuantity = yeonsuQuantity;
        DisplayedQuantity = GetOfficeQuantity(selectedOfficeCode);
    }

    public LocalItem Source { get; }
    public Guid Id => Source.Id;
    public string NameOriginal => Source.NameOriginal;
    public string SpecificationOriginal => Source.SpecificationOriginal;
    public string CategoryName => Source.CategoryName;
    public decimal UznetQuantity { get; }
    public decimal YeonsuQuantity { get; }
    public decimal DisplayedQuantity { get; }
    public decimal TotalQuantity => UznetQuantity + YeonsuQuantity;

    public decimal GetOfficeQuantity(string? officeCode)
        => string.Equals(officeCode, DomainConstants.OfficeYeonsu, StringComparison.OrdinalIgnoreCase)
            ? YeonsuQuantity
            : UznetQuantity;
}
