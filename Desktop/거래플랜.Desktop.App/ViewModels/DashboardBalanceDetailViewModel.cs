using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class DashboardBalanceDetailViewModel : ObservableObject
{
    private readonly LocalStateService _local;
    private readonly SessionState _session;
    private readonly VoucherType _voucherType;
    private readonly Func<Task>? _afterPaymentSavedAsync;

    public DashboardBalanceDetailViewModel(
        LocalStateService local,
        SessionState session,
        VoucherType voucherType,
        string title,
        string subtitle,
        string balanceKindText,
        string accentBrush,
        Func<Task>? afterPaymentSavedAsync = null)
    {
        _local = local;
        _session = session;
        _voucherType = voucherType;
        _afterPaymentSavedAsync = afterPaymentSavedAsync;
        Title = title;
        Subtitle = subtitle;
        BalanceKindText = balanceKindText;
        AccentBrush = accentBrush;
        PaymentKindText = voucherType == VoucherType.Purchase ? "지급" : "수금";
        ProcessActionText = $"{PaymentKindText} 등록";
        ProcessFullActionText = $"잔액 전액 {PaymentKindText}";
        StatusMessage = $"{BalanceKindText} 내역을 불러오는 중입니다.";
    }

    public string Title { get; }
    public string Subtitle { get; }
    public string BalanceKindText { get; }
    public string AccentBrush { get; }
    public string PaymentKindText { get; }
    public string ProcessActionText { get; }
    public string ProcessFullActionText { get; }
    public ObservableCollection<DashboardBalanceDetailRow> Rows { get; } = new();

    [ObservableProperty] private DashboardBalanceDetailRow? _selectedRow;
    [ObservableProperty] private decimal _totalAmount;
    [ObservableProperty] private int _customerCount;
    [ObservableProperty] private int _invoiceCount;
    [ObservableProperty] private DateTime? _processDate = DateTime.Today;
    [ObservableProperty] private string _processAmountText = string.Empty;
    [ObservableProperty] private string _processNote = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isBusy;

    public bool HasRows => Rows.Count > 0;
    public bool HasSelectedRow => SelectedRow is not null;
    public bool HasCheckedRows => Rows.Any(row => row.IsBatchSelected);
    public string TotalAmountText => $"{TotalAmount:N0}원";
    public string CustomerCountText => $"{CustomerCount:N0}곳";
    public string InvoiceCountText => $"{InvoiceCount:N0}건";
    public string CheckedRowsSummaryText
    {
        get
        {
            var checkedRows = GetCheckedRows();
            if (checkedRows.Count == 0)
                return "체크한 전표가 없습니다. 여러 건을 처리하려면 왼쪽 표의 처리 칸을 체크하세요.";

            var customerCount = checkedRows.Select(row => row.CustomerId).Distinct().Count();
            var customerText = customerCount == 1 ? checkedRows[0].CustomerName : $"거래처 {customerCount:N0}곳";
            return $"체크 {checkedRows.Count:N0}건 / {customerText} / 합계 {checkedRows.Sum(row => row.BalanceAmount):N0}원";
        }
    }
    public string SummaryText => HasRows
        ? $"{BalanceKindText} {TotalAmount:N0}원 · 거래처 {CustomerCount:N0}곳 · 전표 {InvoiceCount:N0}건"
        : $"{BalanceKindText}이 남은 전표가 없습니다.";

    partial void OnSelectedRowChanged(DashboardBalanceDetailRow? value)
    {
        OnPropertyChanged(nameof(HasSelectedRow));
        if (value is null)
        {
            ProcessAmountText = string.Empty;
            ProcessNote = string.Empty;
            return;
        }

        ProcessAmountText = value.BalanceAmount.ToString("N0", CultureInfo.CurrentCulture);
        ProcessNote = $"{PaymentKindText} 처리 - {value.InvoiceNumberDisplay}";
        StatusMessage = $"{value.CustomerName} / {value.InvoiceNumberDisplay} 잔액 {value.BalanceAmount:N0}원을 선택했습니다.";
    }

    partial void OnTotalAmountChanged(decimal value)
    {
        OnPropertyChanged(nameof(TotalAmountText));
        OnPropertyChanged(nameof(SummaryText));
    }

    partial void OnCustomerCountChanged(int value)
    {
        OnPropertyChanged(nameof(CustomerCountText));
        OnPropertyChanged(nameof(SummaryText));
    }

    partial void OnInvoiceCountChanged(int value)
    {
        OnPropertyChanged(nameof(InvoiceCountText));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(HasRows));
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        IsBusy = true;
        try
        {
            var rows = await LoadRowsAsync(ct);
            ReplaceRows(rows);
            StatusMessage = SummaryText;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ProcessSelectedBalanceAsync()
        => await SaveSelectedPaymentAsync(useFullBalance: false);

    [RelayCommand]
    private async Task ProcessSelectedFullBalanceAsync()
        => await SaveSelectedPaymentAsync(useFullBalance: true);

    [RelayCommand]
    private void FillCheckedBalanceAmount()
    {
        var checkedRows = GetCheckedRows();
        if (!TryValidateCheckedRows(checkedRows))
            return;

        var total = checkedRows.Sum(row => row.BalanceAmount);
        ProcessAmountText = total.ToString("N0", CultureInfo.CurrentCulture);
        ProcessNote = $"{checkedRows[0].CustomerName} {PaymentKindText} 일괄 처리 {checkedRows.Count:N0}건";
        StatusMessage = $"체크한 전표 {checkedRows.Count:N0}건의 합계 {total:N0}원을 처리금액에 입력했습니다.";
    }

    [RelayCommand]
    private async Task ProcessCheckedBalancesAsync()
    {
        var checkedRows = GetCheckedRows()
            .OrderBy(row => row.InvoiceDate)
            .ThenBy(row => row.InvoiceNumberDisplay, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (!TryValidateCheckedRows(checkedRows))
            return;

        var totalBalance = checkedRows.Sum(row => row.BalanceAmount);
        var amount = totalBalance;
        if (!string.IsNullOrWhiteSpace(ProcessAmountText) && !TryParseAmount(ProcessAmountText, out amount))
        {
            StatusMessage = "일괄 처리금액을 숫자로 입력하세요.";
            return;
        }

        amount = decimal.Round(amount, 0, MidpointRounding.AwayFromZero);
        if (amount <= 0m)
        {
            StatusMessage = "일괄 처리금액은 0보다 커야 합니다.";
            return;
        }

        if (amount > totalBalance)
        {
            StatusMessage = $"일괄 처리금액이 체크 전표 잔액보다 {amount - totalBalance:N0}원 많습니다.";
            return;
        }

        IsBusy = true;
        StatusMessage = $"체크 전표 {checkedRows.Count:N0}건 {PaymentKindText} 일괄 저장 중입니다.";
        try
        {
            var transactionDate = DateOnly.FromDateTime(ProcessDate ?? DateTime.Today);
            var baseNote = BuildBatchBaseNote(checkedRows, amount);
            var batchMemo = $"dashboard-balance-batch:{Guid.NewGuid():N}";
            var remaining = amount;
            var transactions = new List<LocalTransaction>();
            foreach (var row in checkedRows)
            {
                if (remaining <= 0m)
                    break;

                var allocatedAmount = Math.Min(row.BalanceAmount, remaining);
                if (allocatedAmount <= 0m)
                    continue;

                transactions.Add(BuildSettlementTransaction(
                    row,
                    allocatedAmount,
                    transactionDate,
                    $"{baseNote} - {row.InvoiceNumberDisplay}",
                    batchMemo));
                remaining -= allocatedAmount;
            }

            var result = await _local.SaveTransactionsAsync(transactions, _session);
            if (!result.Success)
            {
                StatusMessage = string.IsNullOrWhiteSpace(result.Message)
                    ? $"일괄 {PaymentKindText} 저장에 실패했습니다."
                    : result.Message;
                if (result.ConcurrencyConflict)
                    await RefreshAsync();
                return;
            }

            var serverWriteResult = await _local.WaitForServerWriteWithTimeoutAsync(TimeSpan.FromSeconds(3));
            await RefreshAsync();
            if (_afterPaymentSavedAsync is not null)
                await _afterPaymentSavedAsync();

            StatusMessage = LocalStateService.ComposeServerWriteStatusMessage(
                $"{PaymentKindText} {amount:N0}원이 체크 전표 {transactions.Count:N0}건에 배분 저장되었습니다.",
                serverWriteResult);
        }
        catch (Exception ex)
        {
            StatusMessage = $"일괄 {PaymentKindText} 저장 실패: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SaveSelectedPaymentAsync(bool useFullBalance)
    {
        var row = SelectedRow;
        if (row is null)
        {
            StatusMessage = "처리할 전표를 먼저 선택하세요.";
            return;
        }

        var amount = row.BalanceAmount;
        if (!useFullBalance && !TryParseAmount(ProcessAmountText, out amount))
        {
            StatusMessage = "처리금액을 숫자로 입력하세요.";
            return;
        }

        amount = decimal.Round(amount, 0, MidpointRounding.AwayFromZero);
        if (amount <= 0m)
        {
            StatusMessage = "처리금액은 0보다 커야 합니다.";
            return;
        }

        if (amount > row.BalanceAmount)
        {
            StatusMessage = $"처리금액이 선택 전표 잔액보다 {amount - row.BalanceAmount:N0}원 많습니다.";
            return;
        }

        IsBusy = true;
        StatusMessage = $"{PaymentKindText} 저장 중입니다.";
        try
        {
            var transaction = BuildSettlementTransaction(
                row,
                amount,
                DateOnly.FromDateTime(ProcessDate ?? DateTime.Today),
                NormalizeNote(ProcessNote, row),
                memo: string.Empty);
            var result = await _local.SaveTransactionAsync(transaction, _session);
            if (!result.Success)
            {
                StatusMessage = string.IsNullOrWhiteSpace(result.Message)
                    ? $"{PaymentKindText} 저장에 실패했습니다."
                    : result.Message;
                if (result.ConcurrencyConflict)
                    await RefreshAsync();
                return;
            }

            var serverWriteResult = await _local.WaitForServerWriteWithTimeoutAsync(TimeSpan.FromSeconds(3));
            await RefreshAsync();
            if (_afterPaymentSavedAsync is not null)
                await _afterPaymentSavedAsync();

            StatusMessage = LocalStateService.ComposeServerWriteStatusMessage(
                $"{PaymentKindText} {amount:N0}원이 거래내역과 전표 잔액에 함께 저장되었습니다.",
                serverWriteResult);
        }
        catch (Exception ex)
        {
            StatusMessage = $"{PaymentKindText} 저장 실패: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<IReadOnlyList<DashboardBalanceDetailRow>> LoadRowsAsync(CancellationToken ct)
    {
        var invoices = await _local.GetInvoiceListSummariesAsync(
            from: null,
            to: null,
            customerId: null,
            session: _session,
            ct: ct);
        var candidateInvoices = invoices
            .Where(invoice => invoice.VoucherType == _voucherType
                              && Math.Max(0m, invoice.TotalAmount - invoice.SettledAmount) > 0m)
            .ToList();
        var customerIds = candidateInvoices
            .Select(invoice => invoice.CustomerId)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        var customerMap = await _local.GetCustomerNameMapAsync(customerIds, ct);
        return DashboardBalanceDetailBuilder.BuildRows(candidateInvoices, customerMap, _voucherType);
    }

    private void ReplaceRows(IReadOnlyList<DashboardBalanceDetailRow> rows)
    {
        var previousInvoiceId = SelectedRow?.InvoiceId;
        foreach (var oldRow in Rows)
            oldRow.PropertyChanged -= OnRowPropertyChanged;

        Rows.Clear();
        foreach (var row in rows)
        {
            row.PropertyChanged += OnRowPropertyChanged;
            Rows.Add(row);
        }

        TotalAmount = rows.Sum(row => row.BalanceAmount);
        CustomerCount = rows.Select(row => row.CustomerId).Distinct().Count();
        InvoiceCount = rows.Count;
        SelectedRow = previousInvoiceId.HasValue
            ? Rows.FirstOrDefault(row => row.InvoiceId == previousInvoiceId.Value)
            : null;
        if (SelectedRow is null && Rows.Count > 0)
            SelectedRow = Rows[0];
        NotifyBatchSelectionChanged();
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DashboardBalanceDetailRow.IsBatchSelected))
            NotifyBatchSelectionChanged();
    }

    private void NotifyBatchSelectionChanged()
    {
        OnPropertyChanged(nameof(HasCheckedRows));
        OnPropertyChanged(nameof(CheckedRowsSummaryText));
    }

    private List<DashboardBalanceDetailRow> GetCheckedRows()
        => Rows.Where(row => row.IsBatchSelected).ToList();

    private bool TryValidateCheckedRows(IReadOnlyList<DashboardBalanceDetailRow> checkedRows)
    {
        if (checkedRows.Count == 0)
        {
            StatusMessage = "일괄 처리할 전표의 처리 칸을 먼저 체크하세요.";
            return false;
        }

        if (checkedRows.Select(row => row.CustomerId).Distinct().Count() > 1)
        {
            StatusMessage = "일괄 수금/지급은 같은 거래처 전표끼리만 처리할 수 있습니다.";
            return false;
        }

        return true;
    }

    private LocalTransaction BuildSettlementTransaction(
        DashboardBalanceDetailRow row,
        decimal amount,
        DateOnly transactionDate,
        string note,
        string memo)
    {
        var transaction = new LocalTransaction
        {
            Id = Guid.NewGuid(),
            CustomerId = row.CustomerId,
            TransactionDate = transactionDate,
            TransactionKind = ResolveInvoiceSettlementKind(_voucherType),
            LinkedInvoiceId = row.InvoiceId,
            LinkedInvoiceNumber = row.InvoiceNumberDisplay,
            SettlementAmount = amount,
            Note = note,
            Memo = memo,
            ResponsibleOfficeCode = row.ResponsibleOfficeCode
        };

        if (_voucherType == VoucherType.Purchase)
        {
            transaction.BankPayment = amount;
            transaction.PaymentTotal = amount;
        }
        else
        {
            transaction.BankReceipt = amount;
            transaction.ReceiptTotal = amount;
        }

        return transaction;
    }

    private static string ResolveInvoiceSettlementKind(VoucherType voucherType)
        => voucherType is VoucherType.Purchase or VoucherType.Procurement
            ? PaymentFlowConstants.TransactionKindInvoicePayment
            : PaymentFlowConstants.TransactionKindInvoiceReceipt;

    private string BuildBatchBaseNote(IReadOnlyList<DashboardBalanceDetailRow> checkedRows, decimal amount)
    {
        var trimmed = (ProcessNote ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(trimmed))
            return trimmed;

        var customerName = checkedRows[0].CustomerName;
        return $"{customerName} {PaymentKindText} 일괄 처리 {checkedRows.Count:N0}건 / {amount:N0}원";
    }

    private string NormalizeNote(string note, DashboardBalanceDetailRow row)
    {
        var trimmed = (note ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(trimmed)
            ? $"{PaymentKindText} 처리 - {row.InvoiceNumberDisplay}"
            : trimmed;
    }

    private static bool TryParseAmount(string text, out decimal amount)
    {
        var normalized = (text ?? string.Empty)
            .Replace("원", string.Empty, StringComparison.Ordinal)
            .Replace(",", string.Empty, StringComparison.Ordinal)
            .Trim();
        return decimal.TryParse(
            normalized,
            NumberStyles.Number,
            CultureInfo.CurrentCulture,
            out amount);
    }
}

public sealed partial class DashboardBalanceDetailRow : ObservableObject
{
    [ObservableProperty] private bool _isBatchSelected;

    public Guid CustomerId { get; init; }
    public string CustomerName { get; init; } = string.Empty;
    public decimal CustomerBalance { get; init; }
    public Guid InvoiceId { get; init; }
    public string InvoiceNumberDisplay { get; init; } = string.Empty;
    public DateOnly InvoiceDate { get; init; }
    public string InvoiceDateDisplay => InvoiceDate.ToString("yyyy/MM/dd");
    public VoucherType VoucherType { get; init; }
    public string VoucherTypeDisplay => VoucherType switch
    {
        VoucherType.Sales => "매출",
        VoucherType.Purchase => "매입",
        VoucherType.Procurement => "발주",
        VoucherType.Expense => "경비",
        VoucherType.Collection => "수금",
        _ => VoucherType.ToString()
    };
    public string FirstItemSummary { get; init; } = string.Empty;
    public decimal TotalAmount { get; init; }
    public decimal SettledAmount { get; init; }
    public decimal BalanceAmount { get; init; }
    public string ResponsibleOfficeCode { get; init; } = string.Empty;
}
