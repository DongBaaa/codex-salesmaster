using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class SalesViewModel
{
    private FinancialAmountVisibility.AccessKey _financialAccess;
    private readonly Guid _editorSessionId;
    private readonly Guid? _editorUserId;
    private readonly string _editorDatabase;
    private readonly string _editorOffice;
    private bool _inputAmountsHidden;
    private bool _inputUsesCatalogPrice;
    private bool _suppressInputItemSelection;

    public bool IsEditorOwnerCurrent => _session.IsLoggedIn
        && _session.SessionId == _editorSessionId
        && _session.User?.UserId == _editorUserId
        && string.Equals(_session.SelectedBusinessDatabaseName, _editorDatabase, StringComparison.OrdinalIgnoreCase)
        && string.Equals(_session.OfficeCode, _editorOffice, StringComparison.OrdinalIgnoreCase);

    public bool CanViewInvoiceAmounts => IsEditorOwnerCurrent && (VoucherType switch
    {
        VoucherType.Sales or VoucherType.Collection => _session.HasPermission(AppPermissionNames.AmountViewSales),
        VoucherType.Purchase or VoucherType.Procurement or VoucherType.Expense => _session.HasPermission(AppPermissionNames.AmountViewPurchase),
        _ => false
    });
    public bool CanViewCatalogSalesAmounts => IsEditorOwnerCurrent && _session.HasPermission(AppPermissionNames.AmountViewSales);
    public bool CanEditInputAmounts => !AmountsHidden && !_inputAmountsHidden && SelectedLine?.EffectiveAmountsHidden != true;
    public decimal? EditableInputUnitPrice
    {
        get => CanEditInputAmounts ? InputUnitPrice : null;
        set { if (CanEditInputAmounts && value.HasValue) InputUnitPrice = value.Value; }
    }
    public decimal? EditableInputLineAmount
    {
        get => CanEditInputAmounts ? InputLineAmount : null;
        set { if (CanEditInputAmounts && value.HasValue) InputLineAmount = value.Value; }
    }
    public string CustomerBalanceDisplay => AmountsHidden ? "비공개" : CustomerBalance?.ToString("N0") ?? "비공개";
    public string CustomerAdvanceBalanceDisplay => AmountsHidden ? "비공개" : CustomerAdvanceBalance?.ToString("N0") ?? "비공개";

    private void NotifyInputAmountAccess()
    {
        OnPropertyChanged(nameof(CanEditInputAmounts));
        OnPropertyChanged(nameof(EditableInputUnitPrice));
        OnPropertyChanged(nameof(EditableInputLineAmount));
    }

    private void ApplyInputCatalogPrice(Data.LocalItem item)
    {
        var price = ResolveUnitPrice(item);
        _inputUsesCatalogPrice = true;
        _inputAmountsHidden = !price.HasValue;
        InputUnitPrice = price ?? 0m;
        if (_inputAmountsHidden) InputLineAmount = 0m;
        else RecalcInputAmount();
        NotifyInputAmountAccess();
    }

    private void RefreshInputCatalogAvailability()
    {
        // A passive catalog/permission refresh may close disclosure, but must
        // not overwrite a pending price, quantity or memo or reopen stale data.
        if (_inputUsesCatalogPrice && SelectedInputItem is not null && !ResolveUnitPrice(SelectedInputItem).HasValue)
            _inputAmountsHidden = true;
        NotifyInputAmountAccess();
    }

    private void SetInputItemWithoutRepricing(Data.LocalItem? item)
    {
        _suppressInputItemSelection = true;
        try { SelectedInputItem = item; }
        finally { _suppressInputItemSelection = false; }
    }

    private void SessionAccessChanged(object? sender, EventArgs e)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            _ = dispatcher.InvokeAsync(RefreshAmountAccess);
        else
            RefreshAmountAccess();
    }

    private void RefreshAmountAccess()
    {
        if (_disposed) return;
        var access = FinancialAmountVisibility.CaptureAccess(_session);
        if (access != _financialAccess)
        {
            _financialAccess = access;
            Interlocked.Increment(ref _customerPurchasePriceLoadVersion);
            _customerPurchasePriceByItem.Clear();
            _customerPurchasePriceCustomerId = null;
            Interlocked.Increment(ref _paymentSummaryLoadVersion);
            CustomerBalance = CustomerAdvanceBalance = null;
            PaymentSummaryAdvanceText = "잔액 비공개";
            PaymentSummaryContextText = "전표금액 비공개";
            PaymentSummaryDetailText = "수금/지급 누계 및 잔액 비공개";
        }
        foreach (var line in Lines) line.RefreshAmountAccess();
        RefreshInputCatalogAvailability();
        OnPropertyChanged(nameof(IsEditorOwnerCurrent));
        OnPropertyChanged(nameof(CanViewInvoiceAmounts));
        OnPropertyChanged(nameof(CanViewCatalogSalesAmounts));
        RecalcTotals();
    }
}
