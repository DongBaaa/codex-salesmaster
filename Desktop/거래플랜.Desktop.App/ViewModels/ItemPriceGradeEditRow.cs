using CommunityToolkit.Mvvm.ComponentModel;
using 거래플랜.Shared.Contracts;
using 거래플랜.Desktop.App.Services;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class ItemPriceGradeEditRow : ObservableObject
{
    public ItemPriceGradeEditRow(
        Guid id,
        Guid priceGradeOptionId,
        string priceGradeName,
        string priceSource,
        int sortOrder,
        decimal unitPrice,
        bool isActive = true)
    {
        Id = id;
        PriceGradeOptionId = priceGradeOptionId;
        PriceGradeName = priceGradeName;
        PriceSource = SelectionOptionDefaults.NormalizePriceSource(priceSource);
        SortOrder = sortOrder;
        UnitPrice = unitPrice;
        IsActive = isActive;
    }

    public Guid Id { get; }
    public Guid PriceGradeOptionId { get; }
    public string PriceGradeName { get; }
    public string PriceSource { get; }
    public int SortOrder { get; }
    public bool IsActive { get; }
    public string PriceSourceDisplay => SelectionOptionDefaults.GetPriceSourceDisplayName(PriceSource);

    [ObservableProperty]
    private decimal _unitPrice;
}
