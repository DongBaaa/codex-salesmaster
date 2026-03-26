using CommunityToolkit.Mvvm.ComponentModel;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class MainViewModel
{
    [ObservableProperty] private decimal _previewCustomerAdvanceBalance;
    [ObservableProperty] private decimal _previewCustomerReceivableBalance;
    [ObservableProperty] private decimal _previewCustomerPrepaymentBalance;

    private void RequestRefreshCustomerFinancialPreview(LocalCustomer? customer)
    {
        var version = Interlocked.Increment(ref _customerFinancialPreviewVersion);
        UiTaskHelper.Forget(
            RefreshCustomerFinancialPreviewAsync(customer, version),
            "MAIN",
            "거래처 재무 요약 갱신",
            ex =>
            {
                if (IsCurrentCustomerFinancialPreview(version))
                    AppLogger.Warn("MAIN", $"거래처 재무 요약 갱신 실패: {ex.Message}");
            });
    }

    private Task RefreshCustomerFinancialPreviewAsync(LocalCustomer? customer)
        => RefreshCustomerFinancialPreviewAsync(customer, Interlocked.Increment(ref _customerFinancialPreviewVersion));

    private async Task RefreshCustomerFinancialPreviewAsync(LocalCustomer? customer, int version)
    {
        if (customer is null)
        {
            if (!IsCurrentCustomerFinancialPreview(version))
                return;

            PreviewCustomerAdvanceBalance = 0m;
            PreviewCustomerReceivableBalance = 0m;
            PreviewCustomerPrepaymentBalance = 0m;
            return;
        }

        var summary = await _local.GetCustomerFinancialSummaryAsync(customer.Id, _session);
        if (!IsCurrentCustomerFinancialPreview(version))
            return;

        PreviewCustomerAdvanceBalance = summary.AdvanceBalance;
        PreviewCustomerReceivableBalance = summary.ReceivableAmount;
        PreviewCustomerPrepaymentBalance = summary.PrepaymentAmount;
    }

    private bool IsCurrentCustomerFinancialPreview(int version)
        => version == Volatile.Read(ref _customerFinancialPreviewVersion);
}
