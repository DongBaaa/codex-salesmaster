using CommunityToolkit.Mvvm.ComponentModel;
using 거래플랜.Desktop.App.Data;
using System.Globalization;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.ViewModels;

/// <summary>
/// Editable row model for invoice line items in the DataGrid.
/// </summary>
public sealed partial class InvoiceLineEditModel : ObservableObject
{
    private bool _suppressLineAmountSync;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditAmounts))]
    [NotifyPropertyChangedFor(nameof(UnitPriceDisplay))]
    [NotifyPropertyChangedFor(nameof(LineAmountDisplay))]
    [NotifyPropertyChangedFor(nameof(EditableUnitPrice))]
    [NotifyPropertyChangedFor(nameof(EditableLineAmount))]
    private bool _amountsHidden;

    // Editor-only authorization projection; leave the cached source and draft numbers intact.
    private Func<bool>? _amountAccess;
    public bool EffectiveAmountsHidden => AmountsHidden || !(_amountAccess?.Invoke() ?? true);
    public void SetAmountAccess(Func<bool> canView)
    {
        _amountAccess = canView;
        RefreshAmountAccess();
    }
    public void RefreshAmountAccess()
    {
        OnPropertyChanged(nameof(EffectiveAmountsHidden));
        OnPropertyChanged(nameof(CanEditAmounts));
        OnPropertyChanged(nameof(UnitPriceDisplay));
        OnPropertyChanged(nameof(LineAmountDisplay));
        OnPropertyChanged(nameof(EditableUnitPrice));
        OnPropertyChanged(nameof(EditableLineAmount));
    }
    public bool CanEditAmounts => !EffectiveAmountsHidden;
    public string UnitPriceDisplay => EffectiveAmountsHidden ? "비공개" : UnitPrice.ToString("N0", CultureInfo.CurrentCulture);
    public string LineAmountDisplay => EffectiveAmountsHidden ? "비공개" : LineAmount.ToString("N0", CultureInfo.CurrentCulture);
    public decimal? EditableUnitPrice
    {
        get => EffectiveAmountsHidden ? null : UnitPrice;
        set { if (!EffectiveAmountsHidden && value.HasValue) UnitPrice = value.Value; }
    }
    public decimal? EditableLineAmount
    {
        get => EffectiveAmountsHidden ? null : LineAmount;
        set { if (!EffectiveAmountsHidden && value.HasValue) LineAmount = value.Value; }
    }

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ItemId { get; set; }
    public string ItemTrackingType { get; set; } = ItemTrackingTypes.Stock;
    public int OrderIndex { get; set; }
    [ObservableProperty] private int _rowNo;

    [ObservableProperty]
    private string _itemName = string.Empty;

    [ObservableProperty] private string _specification = string.Empty;
    [ObservableProperty] private string _unit = string.Empty;

    [ObservableProperty]
    private decimal _quantity = 1m;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnitPriceDisplay))]
    [NotifyPropertyChangedFor(nameof(EditableUnitPrice))]
    private decimal _unitPrice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LineAmountDisplay))]
    [NotifyPropertyChangedFor(nameof(EditableLineAmount))]
    private decimal _lineAmount;

    [ObservableProperty] private string _remark = string.Empty;
    [ObservableProperty] private string _serialNumber = string.Empty;
    [ObservableProperty] private string _materialNumber = string.Empty;
    [ObservableProperty] private string _installLocation = string.Empty;
    [ObservableProperty] private DateOnly? _rentalStartDate;
    [ObservableProperty] private DateOnly? _rentalEndDate;

    partial void OnQuantityChanged(decimal value)
    {
        // A quantity edited without price access needs a fresh server calculation,
        // even if the account regains price access before saving.
        if (EffectiveAmountsHidden) AmountsHidden = true;
        SyncLineAmountFromInputs();
    }

    partial void OnUnitPriceChanged(decimal value) => SyncLineAmountFromInputs();

    private void SyncLineAmountFromInputs()
    {
        if (_suppressLineAmountSync || EffectiveAmountsHidden)
            return;

        _suppressLineAmountSync = true;
        try
        {
            LineAmount = Math.Round(Quantity * UnitPrice, 0, MidpointRounding.AwayFromZero);
        }
        finally
        {
            _suppressLineAmountSync = false;
        }
    }

    public static InvoiceLineEditModel FromLocal(LocalInvoiceLine l) => new()
    {
        Id = l.Id,
        AmountsHidden = l.AmountsHidden,
        ItemId = l.ItemId,
        ItemName = l.ItemNameOriginal,
        Specification = l.SpecificationOriginal,
        Unit = l.Unit,
        Quantity = l.Quantity,
        UnitPrice = l.AmountsHidden ? 0m : l.UnitPrice,
        LineAmount = l.AmountsHidden ? 0m : l.LineAmount,
        Remark = l.Remark,
        SerialNumber = l.SerialNumber,
        MaterialNumber = l.MaterialNumber,
        InstallLocation = l.InstallLocation,
        RentalStartDate = l.RentalStartDate,
        RentalEndDate = l.RentalEndDate,
        OrderIndex = l.OrderIndex,
        ItemTrackingType = ItemTrackingTypes.Normalize(l.ItemTrackingType)
    };

    public static InvoiceLineEditModel FromLocal(LocalInvoiceLine line, bool forceAmountsHidden)
    {
        var model = FromLocal(line);
        if (forceAmountsHidden)
        {
            model.AmountsHidden = true;
            model.UnitPrice = 0m;
            model.LineAmount = 0m;
        }
        return model;
    }

    public LocalInvoiceLine ToLocal(Guid invoiceId) => new()
    {
        Id = Id,
        AmountsHidden = EffectiveAmountsHidden,
        InvoiceId = invoiceId,
        ItemId = ItemId,
        ItemNameOriginal = ItemName,
        SpecificationOriginal = Specification,
        Unit = Unit,
        Quantity = Quantity,
        UnitPrice = EffectiveAmountsHidden ? 0m : UnitPrice,
        LineAmount = EffectiveAmountsHidden ? 0m : LineAmount,
        Remark = Remark,
        SerialNumber = SerialNumber,
        MaterialNumber = MaterialNumber,
        InstallLocation = InstallLocation,
        RentalStartDate = RentalStartDate,
        RentalEndDate = RentalEndDate,
        OrderIndex = OrderIndex > 0 ? OrderIndex : RowNo,
        ItemTrackingType = ItemTrackingTypes.Normalize(ItemTrackingType)
    };
}
