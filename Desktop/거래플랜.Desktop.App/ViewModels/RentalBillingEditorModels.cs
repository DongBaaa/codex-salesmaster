using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class RentalBillingTemplateEditorItem : ObservableObject
{
    [ObservableProperty] private Guid _itemId = Guid.NewGuid();
    [ObservableProperty] private string _displayItemName = string.Empty;
    [ObservableProperty] private string _billingLineMode = string.Empty;
    [ObservableProperty] private decimal _quantity = 1m;
    [ObservableProperty] private decimal _unitPrice;
    [ObservableProperty] private decimal _amount;
    [ObservableProperty] private string _note = string.Empty;
    [ObservableProperty] private string _includedAssetSummary = string.Empty;

    public ObservableCollection<Guid> IncludedAssetIds { get; } = new();

    public decimal EffectiveAmount => Amount > 0m
        ? Amount
        : Math.Max(0m, Quantity <= 0m ? 1m : Quantity) * Math.Max(0m, UnitPrice);
}

public sealed partial class RentalBillingAssetOption : ObservableObject
{
    [ObservableProperty] private Guid _assetId;
    [ObservableProperty] private string _managementNumber = string.Empty;
    [ObservableProperty] private string _itemName = string.Empty;
    [ObservableProperty] private string _machineNumber = string.Empty;
    [ObservableProperty] private string _currentCustomerName = string.Empty;
    [ObservableProperty] private string _billToCustomerName = string.Empty;
    [ObservableProperty] private string _installLocation = string.Empty;
    [ObservableProperty] private string _assetStatus = string.Empty;
    [ObservableProperty] private string _billingEligibilityStatus = string.Empty;
    [ObservableProperty] private decimal _monthlyFee;
    [ObservableProperty] private bool _isSelected;
}
