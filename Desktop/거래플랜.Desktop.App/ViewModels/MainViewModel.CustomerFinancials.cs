using CommunityToolkit.Mvvm.ComponentModel;
using 거래플랜.Desktop.App.Data;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class MainViewModel
{
    [ObservableProperty] private decimal _previewCustomerAdvanceBalance;
    [ObservableProperty] private decimal _previewCustomerReceivableBalance;
    [ObservableProperty] private decimal _previewCustomerPrepaymentBalance;

    private async Task RefreshCustomerFinancialPreviewAsync(LocalCustomer? customer)
    {
        if (customer is null)
        {
            PreviewCustomerAdvanceBalance = 0m;
            PreviewCustomerReceivableBalance = 0m;
            PreviewCustomerPrepaymentBalance = 0m;
            return;
        }

        var summary = await _local.GetCustomerFinancialSummaryAsync(customer.Id, _session);
        PreviewCustomerAdvanceBalance = summary.AdvanceBalance;
        PreviewCustomerReceivableBalance = summary.ReceivableAmount;
        PreviewCustomerPrepaymentBalance = summary.PrepaymentAmount;
    }
}
