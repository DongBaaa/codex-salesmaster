using CommunityToolkit.Mvvm.ComponentModel;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class MainViewModel
{
    [ObservableProperty] private decimal _previewCustomerAdvanceBalance;
    [ObservableProperty] private decimal _previewCustomerReceivableBalance;
    [ObservableProperty] private decimal _previewCustomerPayableBalance;
    [ObservableProperty] private decimal _previewCustomerPrepaymentBalance;
    [ObservableProperty] private string _previewLatestRentalInvoiceDateText = "-";
    [ObservableProperty] private string _previewLatestRentalItemSummary = "최근 렌탈 청구가 없습니다.";
    [ObservableProperty] private decimal _previewLatestRentalInvoiceAmount;
    [ObservableProperty] private decimal _previewRentalOutstandingAmount;

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

    public async Task RefreshSelectedCustomerFinancialPreviewAsync()
    {
        if (SelectedCustomerFilter is not null)
        {
            await RefreshCustomerFinancialPreviewAsync(SelectedCustomerFilter);
            return;
        }

        if (SelectedInvoiceRow is not null)
        {
            var customer = _allCustomers.FirstOrDefault(current => current.Id == SelectedInvoiceRow.CustomerId)
                ?? await _local.GetCustomerAsync(SelectedInvoiceRow.CustomerId, _session);
            await RefreshCustomerFinancialPreviewAsync(customer);
            return;
        }

        await RefreshCustomerFinancialPreviewAsync(null);
    }

    public async Task RefreshAfterFinancialTransactionChangedAsync(Guid? fallbackCustomerId = null)
    {
        await LoadInvoiceListAsync();

        if (SelectedCustomerFilter is not null || SelectedInvoiceRow is not null || !fallbackCustomerId.HasValue)
            return;

        var customer = await _local.GetCustomerAsync(fallbackCustomerId.Value, _session);
        await RefreshCustomerFinancialPreviewAsync(customer);
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
            PreviewCustomerPayableBalance = 0m;
            PreviewCustomerPrepaymentBalance = 0m;
            ResetRentalInvoicePreviewSummary();
            return;
        }

        var summary = await _local.GetCustomerFinancialSummaryAsync(customer.Id, _session);
        var invoices = await _local.GetInvoiceListSummariesAsync(from: null, to: null, customerId: customer.Id, session: _session);
        if (!IsCurrentCustomerFinancialPreview(version))
            return;

        PreviewCustomerAdvanceBalance = summary.AdvanceBalance;
        PreviewCustomerReceivableBalance = summary.ReceivableAmount;
        PreviewCustomerPayableBalance = summary.PayableAmount;
        PreviewCustomerPrepaymentBalance = summary.PrepaidAmount;
        ApplyRentalInvoicePreviewSummary(invoices);
    }

    private bool IsCurrentCustomerFinancialPreview(int version)
        => version == Volatile.Read(ref _customerFinancialPreviewVersion);

    private void ResetRentalInvoicePreviewSummary()
    {
        PreviewLatestRentalInvoiceDateText = "-";
        PreviewLatestRentalItemSummary = "최근 렌탈 청구가 없습니다.";
        PreviewLatestRentalInvoiceAmount = 0m;
        PreviewRentalOutstandingAmount = 0m;
    }

    private void ApplyRentalInvoicePreviewSummary(IReadOnlyCollection<LocalInvoiceListSummary> invoices)
    {
        var rentalInvoices = invoices
            .Where(invoice => invoice.LinkedRentalBillingProfileId.HasValue && invoice.VoucherType == VoucherType.Sales)
            .OrderByDescending(invoice => invoice.InvoiceDate)
            .ThenByDescending(invoice => string.IsNullOrWhiteSpace(invoice.InvoiceNumber) ? invoice.LocalTempNumber : invoice.InvoiceNumber)
            .ToList();
        if (rentalInvoices.Count == 0)
        {
            ResetRentalInvoicePreviewSummary();
            return;
        }

        var latestInvoice = rentalInvoices[0];
        PreviewLatestRentalInvoiceDateText = latestInvoice.InvoiceDate.ToString("yyyy/MM/dd");
        PreviewLatestRentalItemSummary = latestInvoice.FirstItemSummary;
        PreviewLatestRentalInvoiceAmount = latestInvoice.TotalAmount;
        PreviewRentalOutstandingAmount = rentalInvoices.Sum(invoice =>
            Math.Max(
                0m,
                invoice.TotalAmount - invoice.SettledAmount));
    }
}
