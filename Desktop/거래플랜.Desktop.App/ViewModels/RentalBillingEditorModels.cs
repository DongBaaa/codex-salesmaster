using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class RentalBillingTemplateEditorItem : ObservableObject
{
    private bool _suppressCalculatedAmountSync;

    [ObservableProperty] private Guid _itemId = Guid.NewGuid();
    [ObservableProperty] private string _displayItemName = string.Empty;
    [ObservableProperty] private string _billingLineMode = string.Empty;
    [ObservableProperty] private decimal _quantity = 1m;
    [ObservableProperty] private decimal _unitPrice;
    [ObservableProperty] private decimal _amount;
    [ObservableProperty] private string _note = string.Empty;
    [ObservableProperty] private string _includedAssetSummary = string.Empty;

    public ObservableCollection<Guid> IncludedAssetIds { get; } = new();

    public decimal EffectiveAmount => CalculateLineAmount(Quantity, UnitPrice);

    public static decimal CalculateLineAmount(decimal quantity, decimal unitPrice)
        => Math.Max(0m, quantity <= 0m ? 1m : quantity) * Math.Max(0m, unitPrice);

    partial void OnQuantityChanged(decimal value) => SyncCalculatedAmount();

    partial void OnUnitPriceChanged(decimal value) => SyncCalculatedAmount();

    partial void OnAmountChanged(decimal value) => OnPropertyChanged(nameof(EffectiveAmount));

    public void NormalizeCalculatedAmount()
        => SyncCalculatedAmount();

    private void SyncCalculatedAmount()
    {
        if (_suppressCalculatedAmountSync)
            return;

        var calculatedAmount = CalculateLineAmount(Quantity, UnitPrice);
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
    [ObservableProperty] private string _machineNumber = string.Empty;
    [ObservableProperty] private string _currentCustomerName = string.Empty;
    [ObservableProperty] private string _targetCustomerName = string.Empty;
    [ObservableProperty] private string _installLocation = string.Empty;
    [ObservableProperty] private string _assetStatus = string.Empty;
    [ObservableProperty] private string _billingEligibilityStatus = string.Empty;
    [ObservableProperty] private string _currentBillingProfileDisplay = string.Empty;
    [ObservableProperty] private string _responsibleOfficeName = string.Empty;
    [ObservableProperty] private string _managementCompanyName = string.Empty;
    [ObservableProperty] private string _assetScopeDisplay = string.Empty;
    [ObservableProperty] private bool _isOutsideCurrentOffice;
    [ObservableProperty] private string _notes = string.Empty;
    [ObservableProperty] private decimal _monthlyFee;
    [ObservableProperty] private DateTime? _contractStartDate;
    [ObservableProperty] private DateTime? _purchaseDate;
    [ObservableProperty] private DateTime? _installDate;
    [ObservableProperty] private bool _isLinkedToCurrentProfile;
    [ObservableProperty] private bool _isLinkedToAnotherProfile;
    [ObservableProperty] private bool _isSelected;

    public string PurchaseDateDisplay => PurchaseDate?.ToString("yyyy-MM-dd") ?? string.Empty;
    public string InstallDateDisplay => InstallDate?.ToString("yyyy-MM-dd") ?? string.Empty;

    partial void OnPurchaseDateChanged(DateTime? value)
        => OnPropertyChanged(nameof(PurchaseDateDisplay));

    partial void OnInstallDateChanged(DateTime? value)
        => OnPropertyChanged(nameof(InstallDateDisplay));
}
