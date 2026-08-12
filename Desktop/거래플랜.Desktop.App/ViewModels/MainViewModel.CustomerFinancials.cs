using System.Diagnostics;
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
        CancellationTokenSource? previousCts;
        Task previewTask;
        lock (_customerFinancialPreviewTaskGate)
        {
            if (_shutdownBackgroundWorkCancellationRequested)
                return;

            previousCts = _customerFinancialPreviewCts;
            var currentCts = new CancellationTokenSource();
            previewTask = RunQueuedCustomerFinancialPreviewAsync(
                _customerFinancialPreviewTask,
                customer,
                version,
                currentCts);
            _customerFinancialPreviewCts = currentCts;
            _customerFinancialPreviewTask = previewTask;
            previousCts?.Cancel();
        }

        UiTaskHelper.Forget(
            previewTask,
            "MAIN",
            "거래처 재무 요약 갱신",
            ex =>
            {
                if (IsCurrentCustomerFinancialPreview(version))
                    AppLogger.Warn("MAIN", $"거래처 재무 요약 갱신 실패: {ex.Message}");
            });
    }

    private async Task RunQueuedCustomerFinancialPreviewAsync(
        Task previousTask,
        LocalCustomer? customer,
        int version,
        CancellationTokenSource currentCts)
    {
        try
        {
            try
            {
                await previousTask;
            }
            catch (OperationCanceledException)
            {
                // 교체된 이전 미리보기의 취소는 새 조회를 막지 않습니다.
            }
            catch (Exception)
            {
                // 이전 작업의 오류는 해당 UiTaskHelper가 이미 관찰합니다.
            }

            await Task.Yield();
            currentCts.Token.ThrowIfCancellationRequested();
            await RefreshCustomerFinancialPreviewAsync(
                customer,
                version,
                currentCts.Token);
        }
        finally
        {
            lock (_customerFinancialPreviewTaskGate)
            {
                if (ReferenceEquals(_customerFinancialPreviewCts, currentCts))
                    _customerFinancialPreviewCts = null;
            }
            currentCts.Dispose();
        }
    }

    public Task RefreshSelectedCustomerFinancialPreviewAsync(CancellationToken ct = default)
        => RefreshSelectedCustomerFinancialPreviewAsync(ct, dataGateAlreadyHeld: false);

    private async Task RefreshSelectedCustomerFinancialPreviewAsync(
        CancellationToken ct,
        bool dataGateAlreadyHeld)
    {
        var dataGateEntered = false;
        try
        {
            if (!dataGateAlreadyHeld)
            {
                await _customerInlineDataGate.WaitAsync(ct);
                dataGateEntered = true;
            }

            if (SelectedCustomerFilter is not null)
            {
                await RefreshCustomerFinancialPreviewAsync(
                    SelectedCustomerFilter,
                    ct,
                    dataGateAlreadyHeld: true);
                return;
            }

            if (SelectedInvoiceRow is not null)
            {
                var customer = _allCustomers.FirstOrDefault(current => current.Id == SelectedInvoiceRow.CustomerId)
                    ?? await _local.GetCustomerAsync(SelectedInvoiceRow.CustomerId, _session, ct);
                await RefreshCustomerFinancialPreviewAsync(
                    customer,
                    ct,
                    dataGateAlreadyHeld: true);
                return;
            }

            await RefreshCustomerFinancialPreviewAsync(
                null,
                ct,
                dataGateAlreadyHeld: true);
        }
        finally
        {
            if (dataGateEntered)
                _customerInlineDataGate.Release();
        }
    }

    public async Task RefreshAfterFinancialTransactionChangedAsync(Guid? fallbackCustomerId = null)
    {
        await ReloadInvoiceListAsync();

        if (SelectedCustomerFilter is not null || SelectedInvoiceRow is not null || !fallbackCustomerId.HasValue)
            return;

        await _customerInlineDataGate.WaitAsync();
        try
        {
            var customer = await _local.GetCustomerAsync(fallbackCustomerId.Value, _session);
            await RefreshCustomerFinancialPreviewAsync(
                customer,
                CancellationToken.None,
                dataGateAlreadyHeld: true);
        }
        finally
        {
            _customerInlineDataGate.Release();
        }
    }

    private Task RefreshCustomerFinancialPreviewAsync(
        LocalCustomer? customer,
        CancellationToken ct = default,
        bool dataGateAlreadyHeld = false)
        => RefreshCustomerFinancialPreviewAsync(
            customer,
            Interlocked.Increment(ref _customerFinancialPreviewVersion),
            ct,
            dataGateAlreadyHeld);

    private async Task RefreshCustomerFinancialPreviewAsync(
        LocalCustomer? customer,
        int version,
        CancellationToken ct = default,
        bool dataGateAlreadyHeld = false)
    {
        ct.ThrowIfCancellationRequested();
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

        var dataGateEntered = false;
        try
        {
            if (!dataGateAlreadyHeld)
            {
                await _customerInlineDataGate.WaitAsync(ct);
                dataGateEntered = true;
            }

            await RefreshCustomerFinancialPreviewCoreAsync(customer, version, ct);
        }
        finally
        {
            if (dataGateEntered)
                _customerInlineDataGate.Release();
        }
    }

    private async Task RefreshCustomerFinancialPreviewCoreAsync(
        LocalCustomer customer,
        int version,
        CancellationToken ct)
    {
        var previewKey = new InvoiceLedgerCacheKey(customer.Id, From: null, To: null);
        var previewLoadStopwatch = Stopwatch.StartNew();
        var (summary, summaryCacheHit) = await _invoiceLedgerCache.GetCustomerFinancialSummaryAsync(
            customer.Id,
            forceReload: false,
            () => _local.GetCustomerFinancialSummaryAsync(customer.Id, _session, ct));
        var (invoices, invoiceCacheHit) = await _invoiceLedgerCache.GetInvoiceSummariesAsync(
            previewKey,
            forceReload: false,
            () => _local.GetInvoiceListSummariesAsync(
                from: null,
                to: null,
                customerId: customer.Id,
                session: _session,
                ct: ct));
        ct.ThrowIfCancellationRequested();
        previewLoadStopwatch.Stop();
        OperationTiming.LogIfSlow(
            "MAIN",
            "Customer financial preview load",
            previewLoadStopwatch.Elapsed,
            $"{previewKey.ToOperationDetail()}, summaryCache={FormatCacheState(summaryCacheHit)}, invoiceCache={FormatCacheState(invoiceCacheHit)}, invoices={invoices.Count:N0}",
            infoThreshold: DetailedInvoiceTimingInfoThreshold,
            warningThreshold: DetailedInvoiceTimingWarningThreshold);
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
