using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class RentalBillingTemplateEditorItem : ObservableObject
{
    private bool _suppressCalculatedAmountSync;

    public RentalBillingTemplateEditorItem()
    {
        IncludedAssetIds.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IncludedAssetCountDisplay));
    }

    [ObservableProperty] private Guid _itemId = Guid.NewGuid();
    [ObservableProperty] private Guid? _catalogItemId;
    [ObservableProperty] private string _displayItemName = string.Empty;
    [ObservableProperty] private string _billingLineMode = string.Empty;
    [ObservableProperty] private string _individualGroupingMode = "모델자동";
    [ObservableProperty] private string _specification = string.Empty;
    [ObservableProperty] private string _unit = string.Empty;
    [ObservableProperty] private string _materialNumber = string.Empty;
    [ObservableProperty] private Guid? _representativeAssetId;
    [ObservableProperty] private decimal _quantity = 1m;
    [ObservableProperty] private decimal? _unitPrice = 0m;
    [ObservableProperty] private decimal? _amount = 0m;
    [ObservableProperty] private string _note = string.Empty;
    [ObservableProperty] private string _invoiceItemNamePreview = string.Empty;
    [ObservableProperty] private string _includedAssetSummary = string.Empty;
    [ObservableProperty] private string _representativeAssetSummary = "대표자산 미지정";

    public ObservableCollection<Guid> IncludedAssetIds { get; } = new();

    public string IncludedAssetCountDisplay => $"{IncludedAssetIds.Count:N0}대";

    public bool AmountsHidden => !UnitPrice.HasValue || !Amount.HasValue;

    public decimal? EffectiveAmount => AmountsHidden ? null : CalculateLineAmount(Quantity, UnitPrice);

    public static decimal? CalculateLineAmount(decimal quantity, decimal? unitPrice)
        => unitPrice.HasValue ? Math.Max(0m, quantity <= 0m ? 1m : quantity) * Math.Max(0m, unitPrice.Value) : null;

    public static decimal? NormalizeAmount(decimal? value)
        => value.HasValue ? Math.Max(0m, value.Value) : null;

    public static decimal? OutstandingAmount(decimal? billed, decimal? settled)
        => billed.HasValue && settled.HasValue ? Math.Max(0m, billed.Value - settled.Value) : null;

    public static string FormatAmount(decimal? value)
        => value.HasValue ? $"{value.Value:N0}원" : "비공개";

    public static decimal? SumKnownAmounts(IEnumerable<RentalBillingTemplateEditorItem> items)
    {
        decimal total = 0m;
        foreach (var item in items)
        {
            if (!item.EffectiveAmount.HasValue) return null;
            total += item.EffectiveAmount.Value;
        }
        return total;
    }

    partial void OnQuantityChanged(decimal value) => SyncCalculatedAmount();

    partial void OnUnitPriceChanged(decimal? value)
    {
        SyncCalculatedAmount(reprice: true);
        OnPropertyChanged(nameof(AmountsHidden));
    }

    partial void OnAmountChanged(decimal? value)
    {
        OnPropertyChanged(nameof(EffectiveAmount));
        OnPropertyChanged(nameof(AmountsHidden));
    }

    public void NormalizeCalculatedAmount()
        => SyncCalculatedAmount();

    private void SyncCalculatedAmount(bool reprice = false)
    {
        if (_suppressCalculatedAmountSync)
            return;

        var calculatedAmount = !reprice && AmountsHidden ? null : CalculateLineAmount(Quantity, UnitPrice);
        if (Amount == calculatedAmount)
        {
            OnPropertyChanged(nameof(EffectiveAmount));
            return;
        }

        _suppressCalculatedAmountSync = true;
        try
        {
            Amount = calculatedAmount;
        }
        finally
        {
            _suppressCalculatedAmountSync = false;
        }

        OnPropertyChanged(nameof(EffectiveAmount));
    }
}

public sealed partial class RentalBillingAssetOption : ObservableObject
{
    [ObservableProperty] private Guid _assetId;
    [ObservableProperty] private Guid? _customerId;
    [ObservableProperty] private Guid? _billingProfileId;
    [ObservableProperty] private string _managementNumber = string.Empty;
    [ObservableProperty] private string _itemName = string.Empty;
    [ObservableProperty] private string _itemCategoryName = string.Empty;
    [ObservableProperty] private string _manufacturer = string.Empty;
    [ObservableProperty] private string _machineNumber = string.Empty;
    [ObservableProperty] private string _purchaseVendor = string.Empty;
    [ObservableProperty] private decimal? _purchasePrice = 0m;
    [ObservableProperty] private decimal? _salePrice = 0m;
    [ObservableProperty] private string _currentCustomerName = string.Empty;
    [ObservableProperty] private string _targetCustomerName = string.Empty;
    [ObservableProperty] private string _installLocation = string.Empty;
    [ObservableProperty] private string _assetStatus = string.Empty;
    [ObservableProperty] private string _billingEligibilityStatus = string.Empty;
    [ObservableProperty] private string _billingExclusionReason = string.Empty;
    [ObservableProperty] private string _currentBillingProfileDisplay = string.Empty;
    [ObservableProperty] private string _responsibleOfficeName = string.Empty;
    [ObservableProperty] private string _managementCompanyName = string.Empty;
    [ObservableProperty] private string _assetScopeDisplay = string.Empty;
    [ObservableProperty] private bool _isOutsideCurrentOffice;
    [ObservableProperty] private string _notes = string.Empty;
    [ObservableProperty] private string _depositText = string.Empty;
    [ObservableProperty] private decimal? _monthlyFee = 0m;
    [ObservableProperty] private int _contractMonths;
    [ObservableProperty] private DateTime? _contractDate;
    [ObservableProperty] private DateTime? _contractStartDate;
    [ObservableProperty] private DateTime? _rentalEndDate;
    [ObservableProperty] private DateTime? _purchaseDate;
    [ObservableProperty] private DateTime? _disposalDate;
    [ObservableProperty] private DateTime? _installDate;
    [ObservableProperty] private string _freeSupplyItems = string.Empty;
    [ObservableProperty] private string _paidSupplyItems = string.Empty;
    [ObservableProperty] private bool _isLinkedToCurrentProfile;
    [ObservableProperty] private bool _isLinkedToAnotherProfile;
    [ObservableProperty] private bool _isReferenceOnly;
    [ObservableProperty] private bool _isRepresentativeAsset;
    [ObservableProperty] private bool _isSelected;

    [ObservableProperty] private bool _salesAmountsReadOnly;
    [ObservableProperty] private bool _purchaseAmountsReadOnly;

    public bool CanEditInLinkDialog => IsSelected && !IsReferenceOnly;
    public string LinkModeDisplay => IsReferenceOnly
        ? "참조 전용"
        : IsLinkedToAnotherProfile
            ? "다른 청구에서 이동"
            : "자산 원본 연동";
    public string PurchaseDateDisplay => PurchaseDate?.ToString("yyyy-MM-dd") ?? string.Empty;
    public string InstallDateDisplay => InstallDate?.ToString("yyyy-MM-dd") ?? string.Empty;
    public string ContractDateDisplay => ContractDate?.ToString("yyyy-MM-dd") ?? string.Empty;
    public string RentalEndDateDisplay => RentalEndDate?.ToString("yyyy-MM-dd") ?? string.Empty;

    partial void OnPurchaseDateChanged(DateTime? value)
        => OnPropertyChanged(nameof(PurchaseDateDisplay));

    partial void OnInstallDateChanged(DateTime? value)
        => OnPropertyChanged(nameof(InstallDateDisplay));

    partial void OnContractDateChanged(DateTime? value)
        => OnPropertyChanged(nameof(ContractDateDisplay));

    partial void OnRentalEndDateChanged(DateTime? value)
        => OnPropertyChanged(nameof(RentalEndDateDisplay));

    partial void OnIsSelectedChanged(bool value)
        => OnPropertyChanged(nameof(CanEditInLinkDialog));

    partial void OnIsReferenceOnlyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEditInLinkDialog));
        OnPropertyChanged(nameof(LinkModeDisplay));
    }

    partial void OnIsLinkedToAnotherProfileChanged(bool value)
        => OnPropertyChanged(nameof(LinkModeDisplay));
}
