using System.Windows;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class MainViewModel
{
    private void HandleBusinessDatabaseChanged(object? sender, EventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            dispatcher.Invoke(RefreshCurrentUserDisplay);
        else
            RefreshCurrentUserDisplay();
    }

    private void RefreshCurrentUserDisplay()
    {
        var username = _session.User?.Username ?? "guest";
        var role = _session.User?.Role ?? "Unknown";
        var officeCode = _session.OfficeCode;
        var businessDatabaseLabel = _session.SelectedBusinessDatabaseLabel;
        var offlineTag = _session.IsOfflineMode ? " [오프라인]" : string.Empty;
        CurrentUserDisplay = $"{username} ({role}/{officeCode}) | 업체DB: {businessDatabaseLabel}{offlineTag}";
    }

    public async Task ReloadForBusinessDatabaseChangeAsync()
    {
        ClearBusinessDatabaseScopedUiState();
        RefreshCurrentUserDisplay();

        if (!_session.IsOfflineMode)
            await _sync.TrySyncAsync();

        await LoadCustomersAsync();
        await LoadInvoiceFilterSettingsAsync();
        await LoadInvoiceListAsync();
        await LoadCompanyProfileAsync();
    }

    private void ClearBusinessDatabaseScopedUiState()
    {
        _allCustomers.Clear();
        FilteredCustomers.Clear();
        InvoiceRows.Clear();
        FavoriteInvoices.Clear();
        PreviewLines.Clear();
        PaymentRows.Clear();

        SelectedCustomerFilter = null;
        SelectedInvoiceRow = null;
        SelectedFavoriteInvoice = null;
        PaymentInvoice = null;
        StatementInvoice = null;

        PreviewCustomerName = string.Empty;
        PreviewCustomerBizNumber = string.Empty;
        PreviewCustomerPhone = string.Empty;
        PreviewCustomerAddress = string.Empty;
        PreviewCustomerNotes = string.Empty;
        PreviewCustomerDepartment = string.Empty;
        PreviewCustomerContactPerson = string.Empty;
        PreviewCustomerContract = null;
        PreviewCustomerAdvanceBalance = 0m;
        PreviewCustomerReceivableBalance = 0m;
        PreviewCustomerPayableBalance = 0m;
        PreviewCustomerPrepaymentBalance = 0m;
        PreviewSupplyAmount = 0m;
        PreviewVatAmount = 0m;
        PreviewTotalAmount = 0m;
        PaymentTotalPaid = 0m;
        PaymentBalance = 0m;

        FilterCustomerName = string.Empty;
    }
}
