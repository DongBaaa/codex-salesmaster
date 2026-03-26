using System.Collections.ObjectModel;
using 거래플랜.Shared.Contracts;
using GeoraePlan.Mobile.App.Services;

namespace GeoraePlan.Mobile.App.ViewModels;

public sealed class InvoicesViewModel : ObservableObject
{
    private readonly GeoraePlanApiClient _api;

    private string _searchText = string.Empty;
    private string _statusMessage = "전표를 불러오세요.";
    private bool _isBusy;
    private DateTime? _lastRefreshUtc;
    private InvoiceDto? _selectedInvoice;

    public InvoicesViewModel(GeoraePlanApiClient api)
    {
        _api = api;
        RefreshCommand = new AsyncCommand(RefreshAsync);
    }

    public ObservableCollection<InvoiceListItem> Invoices { get; } = new();
    public ObservableCollection<InvoiceLineDto> SelectedInvoiceLines { get; } = new();
    public ObservableCollection<PaymentDto> SelectedInvoicePayments { get; } = new();

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value))
                return;

            OnPropertyChanged(nameof(HasSearchText));
        }
    }

    public bool HasSearchText => !string.IsNullOrWhiteSpace(SearchText);

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    public InvoiceDto? SelectedInvoice
    {
        get => _selectedInvoice;
        private set
        {
            if (!SetProperty(ref _selectedInvoice, value))
                return;

            OnPropertyChanged(nameof(HasSelectedInvoice));
            OnPropertyChanged(nameof(SelectedInvoiceCustomerName));
            OnPropertyChanged(nameof(SelectedInvoiceNumberDisplay));
            OnPropertyChanged(nameof(SelectedInvoiceDateDisplay));
            OnPropertyChanged(nameof(SelectedInvoiceAmountSummary));
            OnPropertyChanged(nameof(SelectedInvoiceMemo));
            OnPropertyChanged(nameof(SelectedInvoicePaymentSummary));
        }
    }

    public bool HasSelectedInvoice => SelectedInvoice is not null;
    public string SelectedInvoiceCustomerName => string.IsNullOrWhiteSpace(SelectedInvoice?.CustomerName) ? "거래처 정보 없음" : SelectedInvoice!.CustomerName;
    public string SelectedInvoiceNumberDisplay => $"전표번호 {SelectedInvoice?.InvoiceNumber ?? "-"}";
    public string SelectedInvoiceDateDisplay => SelectedInvoice is null ? "작성일자 정보 없음" : $"작성일자 {SelectedInvoice.InvoiceDate:yyyy-MM-dd}";
    public string SelectedInvoiceAmountSummary => SelectedInvoice is null
        ? "금액 정보 없음"
        : $"공급가 {SelectedInvoice.SupplyAmount:N0}원 · 부가세 {SelectedInvoice.VatAmount:N0}원 · 합계 {SelectedInvoice.TotalAmount:N0}원";
    public string SelectedInvoiceMemo => string.IsNullOrWhiteSpace(SelectedInvoice?.Memo) ? "메모 없음" : SelectedInvoice!.Memo;
    public string SelectedInvoicePaymentSummary
    {
        get
        {
            if (SelectedInvoice is null)
                return "정산 정보 없음";

            var paid = SelectedInvoice.Payments.Sum(payment => payment.Amount);
            var isPurchase = SelectedInvoice.VoucherType == VoucherType.Purchase;
            if (SelectedInvoice.Payments.Count == 0)
            {
                var missingLabel = isPurchase ? "미지불" : "미수금";
                var settlementLabel = isPurchase ? "지불 없음" : "수금 없음";
                return $"{settlementLabel} · {missingLabel} {Math.Max(0m, SelectedInvoice.TotalAmount):N0}원";
            }

            var outstanding = Math.Max(0m, SelectedInvoice.TotalAmount - paid);
            var summaryLabel = isPurchase ? "지불" : "수금";
            var outstandingLabel = isPurchase ? "미지불" : "미수금";
            return $"{summaryLabel} {SelectedInvoice.Payments.Count:N0}건 · 누적 {paid:N0}원 · {outstandingLabel} {outstanding:N0}원";
        }
    }

    public double SelectedInvoiceLinesHeight => CalculateListHeight(SelectedInvoiceLines.Count, 88, 48, 4);
    public double SelectedInvoicePaymentsHeight => CalculateListHeight(SelectedInvoicePayments.Count, 72, 42, 3);

    public AsyncCommand RefreshCommand { get; }

    public bool NeedsRefresh(TimeSpan maxAge)
        => !_lastRefreshUtc.HasValue || DateTime.UtcNow - _lastRefreshUtc.Value >= maxAge;

    public async Task RefreshAsync()
    {
        if (IsBusy)
            return;

        try
        {
            IsBusy = true;
            StatusMessage = "전표를 조회하고 있습니다.";
            var result = await _api.GetInvoicesAsync(SearchText);
            ReplaceInvoices(result);

            _lastRefreshUtc = DateTime.UtcNow;
            StatusMessage = $"전표 {Invoices.Count:N0}건";

            if (SelectedInvoice is not null)
            {
                var selectedRow = Invoices.FirstOrDefault(invoice => invoice.Id == SelectedInvoice.Id);
                if (selectedRow is not null)
                    await SelectInvoiceAsync(selectedRow);
                else
                    ClearSelectedInvoice();
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"전표 조회 실패: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SelectInvoiceAsync(InvoiceListItem invoice)
    {
        if (invoice is null)
            return;

        try
        {
            IsBusy = true;
            StatusMessage = $"{invoice.CustomerDisplayName} 전표 상세를 불러오고 있습니다.";

            var detail = invoice.Id != Guid.Empty
                ? await _api.GetInvoiceByIdAsync(invoice.Id)
                : null;

            var selected = detail ?? invoice.Invoice;
            if (string.IsNullOrWhiteSpace(selected.CustomerName))
                selected.CustomerName = invoice.CustomerDisplayName;

            SelectedInvoice = selected;
            ReplaceSelectedInvoiceLines(selected.Lines);
            ReplaceSelectedInvoicePayments(selected.Payments);
            StatusMessage = $"{SelectedInvoiceCustomerName} 전표 상세를 확인하세요.";
        }
        catch (Exception ex)
        {
            var fallbackInvoice = invoice.Invoice;
            if (string.IsNullOrWhiteSpace(fallbackInvoice.CustomerName))
                fallbackInvoice.CustomerName = invoice.CustomerDisplayName;

            SelectedInvoice = fallbackInvoice;
            ReplaceSelectedInvoiceLines(fallbackInvoice.Lines);
            ReplaceSelectedInvoicePayments(fallbackInvoice.Payments);
            StatusMessage = $"전표 상세 조회 실패: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void ClearSelectedInvoice()
    {
        SelectedInvoice = null;
        ReplaceSelectedInvoiceLines(Array.Empty<InvoiceLineDto>());
        ReplaceSelectedInvoicePayments(Array.Empty<PaymentDto>());
    }

    public bool TryNavigateBackOneStep()
    {
        if (!HasSelectedInvoice)
            return false;

        ClearSelectedInvoice();
        StatusMessage = $"{Invoices.Count:N0}건 전표 목록으로 돌아왔습니다.";
        return true;
    }

    public void ClearSearch()
        => SearchText = string.Empty;

    private void ReplaceInvoices(IEnumerable<InvoiceDto> invoices)
    {
        Invoices.Clear();

        foreach (var invoice in invoices.OrderByDescending(item => item.InvoiceDate).ThenByDescending(item => item.UpdatedAtUtc))
        {
            var customerName = string.IsNullOrWhiteSpace(invoice.CustomerName) ? "거래처 정보 없음" : invoice.CustomerName.Trim();
            Invoices.Add(new InvoiceListItem(invoice, customerName));
        }
    }

    private void ReplaceSelectedInvoiceLines(IEnumerable<InvoiceLineDto>? lines)
    {
        SelectedInvoiceLines.Clear();
        foreach (var line in (lines ?? Enumerable.Empty<InvoiceLineDto>()).Where(line => !line.IsDeleted))
            SelectedInvoiceLines.Add(line);

        OnPropertyChanged(nameof(SelectedInvoiceLinesHeight));
    }

    private void ReplaceSelectedInvoicePayments(IEnumerable<PaymentDto>? payments)
    {
        SelectedInvoicePayments.Clear();
        foreach (var payment in (payments ?? Enumerable.Empty<PaymentDto>()).Where(payment => !payment.IsDeleted).OrderByDescending(payment => payment.PaymentDate))
            SelectedInvoicePayments.Add(payment);

        OnPropertyChanged(nameof(SelectedInvoicePaymentsHeight));
        OnPropertyChanged(nameof(SelectedInvoicePaymentSummary));
    }

    private static double CalculateListHeight(int count, double rowHeight, double emptyHeight, int maxVisibleRows)
    {
        if (count <= 0)
            return emptyHeight;

        var visibleRows = Math.Min(count, maxVisibleRows);
        return (visibleRows * rowHeight) + Math.Max(0, visibleRows - 1) * 6;
    }
}

public sealed class InvoiceListItem
{
    public InvoiceListItem(InvoiceDto invoice, string customerDisplayName)
    {
        Invoice = invoice;
        CustomerDisplayName = customerDisplayName;
    }

    public Guid Id => Invoice.Id;
    public InvoiceDto Invoice { get; }
    public string CustomerDisplayName { get; }
    public string InvoiceNumberDisplay => string.IsNullOrWhiteSpace(Invoice.InvoiceNumber) ? "미정" : Invoice.InvoiceNumber;
    public string DateDisplay => Invoice.InvoiceDate == default ? "작성일자 미정" : Invoice.InvoiceDate.ToString("yyyy-MM-dd");
    public string AmountDisplay => $"{Invoice.TotalAmount:N0}원";
    public string MemoDisplay => string.IsNullOrWhiteSpace(Invoice.Memo) ? "메모 없음" : Invoice.Memo;
    public string PaymentDisplay
    {
        get
        {
            var paidAmount = Invoice.Payments?.Where(payment => !payment.IsDeleted).Sum(payment => payment.Amount) ?? 0m;
            if (paidAmount <= 0m)
                return Invoice.VoucherType == VoucherType.Purchase ? "지불 없음" : "수금 없음";

            return Invoice.VoucherType == VoucherType.Purchase
                ? $"지불 {paidAmount:N0}원"
                : $"수금 {paidAmount:N0}원";
        }
    }
}
