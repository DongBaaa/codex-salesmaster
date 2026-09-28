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
        bool isActive = true,
        bool amountsHidden = false)
    {
        Id = id;
        PriceGradeOptionId = priceGradeOptionId;
        PriceGradeName = priceGradeName;
        PriceSource = SelectionOptionDefaults.NormalizePriceSource(priceSource);
        SortOrder = sortOrder;
        UnitPrice = unitPrice;
        IsActive = isActive;
        AmountsHidden = amountsHidden;
    }

    public Guid Id { get; }
    public Guid PriceGradeOptionId { get; }
    public string PriceGradeName { get; }
    public string PriceSource { get; }
    public int SortOrder { get; }
    public bool IsActive { get; }
    public bool AmountsHidden { get; private set; }
    public string PriceSourceDisplay => SelectionOptionDefaults.GetPriceSourceDisplayName(PriceSource);

    [ObservableProperty]
    private decimal _unitPrice;

    private Func<bool>? _canViewPrice;
    private Func<bool>? _canEditPrice;
    public bool IsPriceReadOnly => AmountsHidden || _canViewPrice?.Invoke() != true || _canEditPrice?.Invoke() != true;
    public decimal? EditableUnitPrice
    {
        get => !AmountsHidden && _canViewPrice?.Invoke() == true ? UnitPrice : null;
        set { if (!IsPriceReadOnly && value.HasValue) UnitPrice = value.Value; }
    }

    internal void SetAmountAccess(Func<bool> canViewPrice, Func<bool> canEditPrice)
    {
        _canViewPrice = canViewPrice;
        _canEditPrice = canEditPrice;
        RefreshAmountAccess();
    }

    internal void MarkAmountsHidden()
    {
        AmountsHidden = true;
        RefreshAmountAccess();
    }

    internal void RefreshAmountAccess()
    {
        OnPropertyChanged(nameof(EditableUnitPrice));
        OnPropertyChanged(nameof(IsPriceReadOnly));
    }
    partial void OnUnitPriceChanged(decimal value) => RefreshAmountAccess();
}
