using 거래플랜.Desktop.App.Services;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class InventoryViewModel
{
    private readonly Guid _inventorySessionId;
    private readonly Guid? _inventoryUserId;
    private readonly string _inventoryDatabase;
    private readonly string _inventoryOffice;
    private readonly string _inventoryTenant;
    private readonly string _inventoryScopeType;
    private readonly string? _inventoryRole;
    private readonly bool _inventoryGlobalScope;
    private bool _editPurchaseAmountsHidden;
    private bool _editSalesAmountsHidden;

    public bool IsEditorOwnerCurrent => !_isDisposed && _session.IsLoggedIn
        && _session.SessionId == _inventorySessionId && _session.User?.UserId == _inventoryUserId
        && string.Equals(_session.SelectedBusinessDatabaseName, _inventoryDatabase, StringComparison.OrdinalIgnoreCase)
        && string.Equals(_session.OfficeCode, _inventoryOffice, StringComparison.OrdinalIgnoreCase)
        && string.Equals(_session.TenantCode, _inventoryTenant, StringComparison.OrdinalIgnoreCase)
        && string.Equals(_session.ScopeType, _inventoryScopeType, StringComparison.OrdinalIgnoreCase)
        && string.Equals(_session.User?.Role, _inventoryRole, StringComparison.OrdinalIgnoreCase)
        && _session.HasGlobalDataScope == _inventoryGlobalScope;
    public bool CanViewPurchasePrices => IsEditorOwnerCurrent && !_editPurchaseAmountsHidden && _session.HasPermission(AppPermissionNames.AmountViewPurchase);
    public bool CanViewSalesPrices => IsEditorOwnerCurrent && !_editSalesAmountsHidden && _session.HasPermission(AppPermissionNames.AmountViewSales);
    public bool IsPurchasePriceReadOnly => !CanViewPurchasePrices || !CanSaveItems;
    public bool IsSalesPriceReadOnly => !CanViewSalesPrices || !CanSaveItems;

    public decimal? EditablePurchasePrice
    {
        get => CanViewPurchasePrices ? EditPurchasePrice : null;
        set { if (!IsPurchasePriceReadOnly && value.HasValue) EditPurchasePrice = value.Value; }
    }
    public decimal? EditableSalePrice
    {
        get => CanViewSalesPrices ? EditSalePrice : null;
        set { if (!IsSalesPriceReadOnly && value.HasValue) EditSalePrice = value.Value; }
    }
    public decimal? EditableRetailPrice
    {
        get => CanViewSalesPrices ? EditRetailPrice : null;
        set { if (!IsSalesPriceReadOnly && value.HasValue) EditRetailPrice = value.Value; }
    }

    private void RefreshInventoryAmountAccess()
    {
        OnPropertyChanged(nameof(IsEditorOwnerCurrent));
        OnPropertyChanged(nameof(CanViewPurchasePrices));
        OnPropertyChanged(nameof(CanViewSalesPrices));
        OnPropertyChanged(nameof(IsPurchasePriceReadOnly));
        OnPropertyChanged(nameof(IsSalesPriceReadOnly));
        OnPropertyChanged(nameof(EditablePurchasePrice));
        OnPropertyChanged(nameof(EditableSalePrice));
        OnPropertyChanged(nameof(EditableRetailPrice));
        OnPropertyChanged(nameof(AssetValue));
        OnPropertyChanged(nameof(CanSaveItems));
        OnPropertyChanged(nameof(CanDeleteSelectedItem));
        SaveItemCommand.NotifyCanExecuteChanged();
        DeleteItemCommand.NotifyCanExecuteChanged();
        foreach (var row in PriceGradeRows) row.RefreshAmountAccess();
    }

    partial void OnEditSalePriceChanged(decimal value) => OnPropertyChanged(nameof(EditableSalePrice));
    partial void OnEditRetailPriceChanged(decimal value) => OnPropertyChanged(nameof(EditableRetailPrice));

    private void ApplyRefreshedPriceAvailability(InventoryItemRow row)
    {
        if (IsNew || EditId != row.Id) return;
        // Keep an unsaved draft, but never retain visibility when a newer pull
        // no longer discloses a price. Only a full form load can disclose it again.
        _editPurchaseAmountsHidden |= row.Source.PurchaseAmountsHidden;
        _editSalesAmountsHidden |= row.Source.SalesAmountsHidden;
        _itemPriceGradesByItemId.TryGetValue(row.Id, out var grades);
        foreach (var grade in PriceGradeRows)
            if (_editSalesAmountsHidden || grades?.Any(current => current.PriceGradeOptionId == grade.PriceGradeOptionId && current.AmountsHidden) == true)
                grade.MarkAmountsHidden();
        RefreshInventoryAmountAccess();
    }
}
