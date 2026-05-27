using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class PeriodLedgerViewModel : ObservableObject
{
    private readonly LocalStateService _local;
    private readonly PeriodLedgerAggregationService _aggregation;
    private readonly PeriodLedgerExcelExportService _exporter;
    private readonly SessionState _session;
    private List<LocalCustomer> _allCustomers = [];
    private PeriodLedgerBuildResult? _currentResult;
    private CancellationTokenSource? _ledgerSearchRefreshCts;

    [ObservableProperty] private DateTime _fromDate = DateTime.Today;
    [ObservableProperty] private DateTime _toDate = DateTime.Today;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSelectCustomer))]
    [NotifyPropertyChangedFor(nameof(ScopeHintMessage))]
    private bool _isAllCustomers;
    [ObservableProperty] private string _selectedScopeOption = "개별업체집계";

    [ObservableProperty] private string _customerSearchText = string.Empty;
    [ObservableProperty] private string _ledgerSearchText = string.Empty;
    [ObservableProperty] private LocalCustomer? _selectedCustomer;
    [ObservableProperty] private PeriodLedgerType _selectedLedgerType = PeriodLedgerType.SalesPurchase;
    [ObservableProperty] private bool _sortByCustomerName;
    [ObservableProperty] private bool _showProfit;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "집계 조건을 선택하세요.";
    [ObservableProperty] private string _lastExportPath = string.Empty;
    [ObservableProperty] private PeriodLedgerDisplayRow? _selectedLedgerRow;
    [ObservableProperty] private PeriodLedgerDetailDisplayRow? _selectedLedgerItem;
    [ObservableProperty] private bool _hasLedgerRows;
    [ObservableProperty] private string _detailHintText = "거래를 선택하면 상세 품목이 표시됩니다.";
    [ObservableProperty] private string _summaryTradeAmountText = "0";
    [ObservableProperty] private string _summaryReceiptAmountText = "0";
    [ObservableProperty] private string _summaryPaymentAmountText = "0";
    [ObservableProperty] private string _summaryRunningBalanceText = "0";
    [ObservableProperty] private string _summaryCountText = "0건";
    [ObservableProperty] private string _summaryProfitText = "-";

    public ObservableCollection<LocalCustomer> Customers { get; } = [];
    public ObservableCollection<PeriodLedgerDisplayRow> LedgerRows { get; } = [];
    public ObservableCollection<PeriodLedgerDetailDisplayRow> SelectedLedgerItems { get; } = [];
    public IReadOnlyList<string> ScopeOptions { get; } = ["개별업체집계", "전체업체집계"];

    public bool CanSelectCustomer => !IsAllCustomers;
    public string ScopeHintMessage => IsAllCustomers
        ? "전체업체집계(거래처 선택 무시)"
        : "개별업체집계(거래처 선택 필수)";
    public bool IsSalesPurchaseSelected
    {
        get => SelectedLedgerType == PeriodLedgerType.SalesPurchase;
        set
        {
            if (!value) return;
            SelectedLedgerType = PeriodLedgerType.SalesPurchase;
        }
    }
    public bool IsSalesSelected
    {
        get => SelectedLedgerType == PeriodLedgerType.SalesOnly;
        set
        {
            if (!value) return;
            SelectedLedgerType = PeriodLedgerType.SalesOnly;
        }
    }
    public bool IsPurchaseSelected
    {
        get => SelectedLedgerType == PeriodLedgerType.PurchaseOnly;
        set
        {
            if (!value) return;
            SelectedLedgerType = PeriodLedgerType.PurchaseOnly;
        }
    }
    public bool IsReceiptPaymentSelected
    {
        get => SelectedLedgerType == PeriodLedgerType.ReceiptPayment;
        set
        {
            if (!value) return;
            SelectedLedgerType = PeriodLedgerType.ReceiptPayment;
        }
    }
    public bool IsYeonsuDeliverySelected
    {
        get => SelectedLedgerType == PeriodLedgerType.YeonsuDelivery;
        set
        {
            if (!value) return;
            SelectedLedgerType = PeriodLedgerType.YeonsuDelivery;
        }
    }

    public PeriodLedgerViewModel(
        LocalStateService local,
        PeriodLedgerAggregationService aggregation,
        PeriodLedgerExcelExportService exporter,
        SessionState session)
    {
        _local = local;
        _aggregation = aggregation;
        _exporter = exporter;
        _session = session;

        ApplyCurrentMonth();
    }

    public async Task InitializeAsync()
    {
        _allCustomers = await _local.GetCustomersAsync(_session);
        RefreshCustomerList(_allCustomers);
        SelectedCustomer = Customers.FirstOrDefault();
    }

    [RelayCommand]
    private void SetPreviousMonth()
    {
        var baseDate = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-1);
        FromDate = new DateTime(baseDate.Year, baseDate.Month, 1);
        ToDate = FromDate.AddMonths(1).AddDays(-1);
    }

    [RelayCommand]
    private void SetCurrentMonth()
        => ApplyCurrentMonth();

    private void ApplyCurrentMonth()
    {
        FromDate = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        ToDate = DateTime.Today;
    }

    [RelayCommand]
    private void SearchCustomer()
    {
        if (string.IsNullOrWhiteSpace(CustomerSearchText))
        {
            RefreshCustomerList(_allCustomers);
            SelectedCustomer = Customers.FirstOrDefault();
            return;
        }

        var keyword = CustomerSearchText.Trim();
        var filtered = _allCustomers
            .Where(c => c.NameOriginal.Contains(keyword, StringComparison.CurrentCultureIgnoreCase))
            .ToList();

        RefreshCustomerList(filtered);
        SelectedCustomer = Customers.FirstOrDefault();
    }

    [RelayCommand]
    private async Task StartAggregationAsync()
    {
        if (IsBusy)
            return;

        _ledgerSearchRefreshCts?.Cancel();

        if (ToDate.Date < FromDate.Date)
        {
            System.Windows.MessageBox.Show("조회 종료일은 시작일보다 빠를 수 없습니다.", "알림", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        if (!IsAllCustomers && SelectedCustomer is null)
        {
            System.Windows.MessageBox.Show("개별업체집계에서는 거래처 선택이 필요합니다.", "알림", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        IsBusy = true;
        StatusMessage = "조회 중...";

        try
        {
            var query = BuildCurrentQuery();

            var progress = new Progress<string>(message => StatusMessage = message);
            var result = await _aggregation.BuildAsync(query, _session, progress);
            ApplyResult(result);

            if (!string.IsNullOrWhiteSpace(result.ProfitWarningMessage))
            {
                StatusMessage = result.ProfitWarningMessage;
            }

            var filePath = await _exporter.ExportAsync(result, AppPaths.UserDownloadsDir, progress);
            LastExportPath = filePath;
            StatusMessage = $"완료: {Path.GetFileName(filePath)}";
        }
        catch (Exception ex)
        {
            AppLogger.Error("PeriodLedger", "기간별 집계 실패", ex);
            StatusMessage = $"오류: {ex.Message}";
            System.Windows.MessageBox.Show($"기간별 집계 중 오류가 발생했습니다.\n{ex.Message}", "오류", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<OfficeMutationResult> UpdateSelectedLedgerMemoAsync(string memo, CancellationToken ct = default)
    {
        var row = SelectedLedgerRow;
        if (row is null || !row.CanEditMemo)
            return OfficeMutationResult.Denied("메모를 수정할 수 있는 원본 행을 선택하세요.");

        var result = row.MemoSource switch
        {
            PeriodLedgerMemoSource.Transaction when row.TransactionId is Guid transactionId =>
                await _local.UpdateTransactionLedgerMemoAsync(transactionId, memo, _session, ct),
            PeriodLedgerMemoSource.Payment when row.PaymentId is Guid paymentId =>
                await _local.UpdatePaymentLedgerMemoAsync(paymentId, memo, _session, ct),
            PeriodLedgerMemoSource.Invoice when row.InvoiceId is Guid invoiceId =>
                await _local.UpdateInvoiceLedgerMemoAsync(invoiceId, memo, _session, ct),
            _ => OfficeMutationResult.Denied("메모를 수정할 수 있는 원본 행을 선택하세요.")
        };

        if (result.Success)
        {
            foreach (var ledgerRow in LedgerRows.Where(r => r.HasSameMemoTarget(row)))
                ledgerRow.Note = memo ?? string.Empty;
            StatusMessage = "메모를 저장했습니다.";
        }
        else
        {
            StatusMessage = result.Message;
        }

        return result;
    }

    public async Task<OfficeMutationResult> UpdateSelectedItemMemoAsync(string itemMemo, CancellationToken ct = default)
    {
        var row = SelectedLedgerRow;
        var item = SelectedLedgerItem;
        if (row?.InvoiceId is not Guid invoiceId || item is null || item.LineId == Guid.Empty)
            return OfficeMutationResult.Denied("품목비고를 수정할 품목 행을 선택하세요.");

        var result = await _local.UpdateInvoiceLineLedgerMemoAsync(invoiceId, item.LineId, itemMemo, _session, ct);
        if (result.Success)
        {
            foreach (var ledgerItem in LedgerRows.SelectMany(r => r.Items).Where(i => i.LineId == item.LineId))
                ledgerItem.ItemNote = itemMemo ?? string.Empty;
            StatusMessage = "품목비고를 저장했습니다.";
        }
        else
        {
            StatusMessage = result.Message;
        }

        return result;
    }

    private PeriodLedgerQuery BuildCurrentQuery()
        => new()
        {
            From = DateOnly.FromDateTime(FromDate.Date),
            To = DateOnly.FromDateTime(ToDate.Date),
            LedgerType = SelectedLedgerType,
            Scope = IsAllCustomers ? PeriodLedgerScope.AllCustomers : PeriodLedgerScope.SingleCustomer,
            CustomerId = IsAllCustomers ? null : SelectedCustomer?.Id,
            SortByCustomerName = SortByCustomerName,
            IncludeProfit = ShowProfit,
            SearchText = LedgerSearchText
        };

    private void ApplyResult(PeriodLedgerBuildResult result)
    {
        _currentResult = result;
        LedgerRows.Clear();
        SelectedLedgerItems.Clear();
        SelectedLedgerRow = null;
        SelectedLedgerItem = null;

        var sequence = 0;
        if (result.Blocks.Count > 0)
        {
            foreach (var block in result.Blocks)
            {
                foreach (var row in block.Rows.Where(r => !r.IsSubTotal))
                {
                    LedgerRows.Add(PeriodLedgerDisplayRow.FromLedgerRow(++sequence, block.CustomerName, row));
                }
            }
        }
        else if (result.PaymentRows.Count > 0)
        {
            foreach (var row in result.PaymentRows)
                LedgerRows.Add(PeriodLedgerDisplayRow.FromPaymentRow(row));
        }
        else if (result.YeonsuDeliveryRows.Count > 0)
        {
            foreach (var row in result.YeonsuDeliveryRows)
                LedgerRows.Add(PeriodLedgerDisplayRow.FromYeonsuDeliveryRow(row));
        }

        HasLedgerRows = LedgerRows.Count > 0;
        SelectedLedgerRow = LedgerRows.FirstOrDefault();
        RefreshSelectedLedgerItems(SelectedLedgerRow);
        UpdateSummaryTexts(result, LedgerRows.Count);
    }

    private void RefreshSelectedLedgerItems(PeriodLedgerDisplayRow? row)
    {
        SelectedLedgerItems.Clear();
        SelectedLedgerItem = null;

        if (row is null)
        {
            DetailHintText = "거래를 선택하면 상세 품목이 표시됩니다.";
            return;
        }

        foreach (var item in row.Items)
            SelectedLedgerItems.Add(item);

        if (SelectedLedgerItems.Count == 0)
        {
            DetailHintText = "선택한 거래에 표시할 품목 상세가 없습니다.";
            return;
        }

        SelectedLedgerItem = SelectedLedgerItems.FirstOrDefault();
        DetailHintText = "품목비고는 상세 품목의 '품목비고' 칸을 더블클릭해서 수정할 수 있습니다.";
    }

    private void UpdateSummaryTexts(PeriodLedgerBuildResult result, int rowCount)
    {
        SummaryTradeAmountText = FormatAmount(result.Totals.TradeAmount);
        SummaryReceiptAmountText = FormatAmount(result.Totals.ReceiptAmount);
        SummaryPaymentAmountText = FormatAmount(result.Totals.PaymentAmount);
        SummaryRunningBalanceText = FormatAmount(result.Totals.RunningBalance);
        SummaryProfitText = result.Totals.ProfitAmount.HasValue ? FormatAmount(result.Totals.ProfitAmount.Value) : "-";
        SummaryCountText = $"{rowCount:N0}건";
    }

    private void RefreshCustomerList(IEnumerable<LocalCustomer> customers)
    {
        Customers.Clear();
        foreach (var c in customers.OrderBy(c => c.NameOriginal, StringComparer.CurrentCultureIgnoreCase))
            Customers.Add(c);
    }

    private static string FormatAmount(decimal amount)
        => amount.ToString("#,##0", CultureInfo.CurrentCulture);

    partial void OnSelectedLedgerTypeChanged(PeriodLedgerType value)
    {
        OnPropertyChanged(nameof(IsSalesPurchaseSelected));
        OnPropertyChanged(nameof(IsSalesSelected));
        OnPropertyChanged(nameof(IsPurchaseSelected));
        OnPropertyChanged(nameof(IsReceiptPaymentSelected));
        OnPropertyChanged(nameof(IsYeonsuDeliverySelected));
    }

    partial void OnCustomerSearchTextChanged(string value)
        => SearchCustomer();

    partial void OnLedgerSearchTextChanged(string value)
    {
        if (_currentResult is not null)
            ScheduleLedgerSearchRefresh();
    }

    partial void OnSelectedScopeOptionChanged(string value)
    {
        IsAllCustomers = string.Equals(value, "전체업체집계", StringComparison.OrdinalIgnoreCase);
    }

    partial void OnIsAllCustomersChanged(bool value)
    {
        var option = value ? "전체업체집계" : "개별업체집계";
        if (!string.Equals(SelectedScopeOption, option, StringComparison.Ordinal))
            SelectedScopeOption = option;
    }

    partial void OnSelectedLedgerRowChanged(PeriodLedgerDisplayRow? value)
        => RefreshSelectedLedgerItems(value);

    private void ScheduleLedgerSearchRefresh()
    {
        if (IsBusy)
            return;

        _ledgerSearchRefreshCts?.Cancel();
        var cts = new CancellationTokenSource();
        _ledgerSearchRefreshCts = cts;

        _ = RefreshLedgerSearchAsync(cts);
    }

    private async Task RefreshLedgerSearchAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(250, cts.Token);

            if (IsBusy || cts.IsCancellationRequested)
                return;

            var query = BuildCurrentQuery();
            var result = await _aggregation.BuildAsync(query, _session, progress: null, cts.Token);

            if (cts.IsCancellationRequested)
                return;

            ApplyResult(result);
            StatusMessage = string.IsNullOrWhiteSpace(LedgerSearchText)
                ? $"검색 해제: {LedgerRows.Count:N0}건"
                : $"검색 결과: {LedgerRows.Count:N0}건";
        }
        catch (OperationCanceledException)
        {
            // 검색어 연속 입력 중 이전 조회는 취소합니다.
        }
        catch (Exception ex)
        {
            AppLogger.Warn("PeriodLedger", $"실시간 검색 갱신 실패: {ex.Message}");
            StatusMessage = $"검색 갱신 오류: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_ledgerSearchRefreshCts, cts))
                _ledgerSearchRefreshCts = null;

            cts.Dispose();
        }
    }
}

public sealed partial class PeriodLedgerDisplayRow : ObservableObject
{
    public int No { get; init; }
    public Guid? InvoiceId { get; init; }
    public Guid? PaymentId { get; init; }
    public Guid? TransactionId { get; init; }
    public PeriodLedgerMemoSource MemoSource { get; init; } = PeriodLedgerMemoSource.None;
    public DateOnly Date { get; init; }
    public string DateText => Date == default ? string.Empty : Date.ToString("yyyy-MM-dd");
    public string CustomerName { get; init; } = string.Empty;
    public string Division { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public decimal TradeAmount { get; init; }
    public decimal ReceiptAmount { get; init; }
    public decimal PaymentAmount { get; init; }
    public decimal RunningBalance { get; init; }
    public decimal ReceivableBalance { get; init; }
    public IReadOnlyList<PeriodLedgerDetailDisplayRow> Items { get; init; } = Array.Empty<PeriodLedgerDetailDisplayRow>();

    [ObservableProperty] private string _note = string.Empty;

    public string NotePreview => PreviewText(Note);
    public string DivisionBackground => ResolveDivisionBackground(Division);
    public string DivisionForeground => ResolveDivisionForeground(Division);
    public bool CanEditMemo => MemoSource switch
    {
        PeriodLedgerMemoSource.Invoice => InvoiceId.HasValue,
        PeriodLedgerMemoSource.Payment => PaymentId.HasValue,
        PeriodLedgerMemoSource.Transaction => TransactionId.HasValue,
        _ => false
    };

    public bool HasSameMemoTarget(PeriodLedgerDisplayRow other)
    {
        if (MemoSource != other.MemoSource)
            return false;

        return MemoSource switch
        {
            PeriodLedgerMemoSource.Invoice => InvoiceId.HasValue && InvoiceId == other.InvoiceId,
            PeriodLedgerMemoSource.Payment => PaymentId.HasValue && PaymentId == other.PaymentId,
            PeriodLedgerMemoSource.Transaction => TransactionId.HasValue && TransactionId == other.TransactionId,
            _ => false
        };
    }

    public static PeriodLedgerDisplayRow FromLedgerRow(int no, string customerName, PeriodLedgerRow row)
        => new()
        {
            No = no,
            InvoiceId = row.InvoiceId,
            PaymentId = row.PaymentId,
            TransactionId = row.TransactionId,
            MemoSource = row.MemoSource,
            Date = row.Date,
            CustomerName = customerName,
            Division = row.Division,
            Summary = row.Summary,
            TradeAmount = row.TradeAmount,
            ReceiptAmount = row.ReceiptAmount,
            PaymentAmount = row.PaymentAmount,
            RunningBalance = row.RunningBalance,
            ReceivableBalance = row.ReceivableBalance,
            Note = row.Note,
            Items = row.Items.Select(item => PeriodLedgerDetailDisplayRow.FromItemRow(row.InvoiceId, item)).ToList()
        };

    public static PeriodLedgerDisplayRow FromPaymentRow(PeriodLedgerPaymentRow row)
        => new()
        {
            No = row.No,
            InvoiceId = row.InvoiceId,
            PaymentId = row.PaymentId,
            TransactionId = row.TransactionId,
            MemoSource = row.MemoSource,
            Date = row.Date,
            CustomerName = row.CustomerName,
            Division = row.Division,
            Summary = row.Summary,
            TradeAmount = row.TradeAmount,
            ReceiptAmount = row.ReceiptAmount,
            PaymentAmount = row.PaymentAmount,
            RunningBalance = row.RunningBalance,
            ReceivableBalance = row.ReceivableBalance,
            Note = row.Note,
            Items = Array.Empty<PeriodLedgerDetailDisplayRow>()
        };

    public static PeriodLedgerDisplayRow FromYeonsuDeliveryRow(PeriodLedgerYeonsuDeliveryRow row)
        => new()
        {
            No = row.No,
            InvoiceId = row.InvoiceId == Guid.Empty ? null : row.InvoiceId,
            PaymentId = null,
            TransactionId = null,
            MemoSource = row.InvoiceId == Guid.Empty ? PeriodLedgerMemoSource.None : PeriodLedgerMemoSource.Invoice,
            Date = row.DeliveryDate,
            CustomerName = row.CustomerName,
            Division = "납품",
            Summary = row.ItemSummary,
            TradeAmount = row.TotalAmount,
            ReceiptAmount = 0m,
            PaymentAmount = 0m,
            RunningBalance = row.TotalAmount,
            ReceivableBalance = row.TotalAmount,
            Note = row.Note,
            Items = Array.Empty<PeriodLedgerDetailDisplayRow>()
        };

    private static string PreviewText(string? value, int maxLength = 28)
    {
        var normalized = (value ?? string.Empty).ReplaceLineEndings(" ").Trim();
        if (normalized.Length <= maxLength)
            return normalized;
        return normalized[..maxLength] + "...";
    }

    private static string ResolveDivisionBackground(string division)
    {
        if (division.Contains("판매", StringComparison.OrdinalIgnoreCase) || division.Contains("매출", StringComparison.OrdinalIgnoreCase))
            return "#DFF3E5";
        if (division.Contains("구매", StringComparison.OrdinalIgnoreCase) || division.Contains("매입", StringComparison.OrdinalIgnoreCase))
            return "#DDEEFF";
        if (division.Contains("수금", StringComparison.OrdinalIgnoreCase))
            return "#DFF3E5";
        if (division.Contains("지급", StringComparison.OrdinalIgnoreCase) || division.Contains("지불", StringComparison.OrdinalIgnoreCase))
            return "#FFE9D6";
        return "#E8EDF5";
    }

    private static string ResolveDivisionForeground(string division)
    {
        if (division.Contains("지급", StringComparison.OrdinalIgnoreCase) || division.Contains("지불", StringComparison.OrdinalIgnoreCase))
            return "#8A4A00";
        if (division.Contains("구매", StringComparison.OrdinalIgnoreCase) || division.Contains("매입", StringComparison.OrdinalIgnoreCase))
            return "#235A9F";
        return "#1E6B35";
    }

    partial void OnNoteChanged(string value)
        => OnPropertyChanged(nameof(NotePreview));
}

public sealed partial class PeriodLedgerDetailDisplayRow : ObservableObject
{
    public Guid? InvoiceId { get; init; }
    public Guid LineId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public string Specification { get; init; } = string.Empty;
    public decimal Quantity { get; init; }
    public decimal UnitPrice { get; init; }
    public decimal SupplyAmount { get; init; }
    public decimal VatAmount { get; init; }
    public decimal LineAmount { get; init; }

    [ObservableProperty] private string _itemNote = string.Empty;

    public string ItemNotePreview => PreviewText(ItemNote);

    public static PeriodLedgerDetailDisplayRow FromItemRow(Guid? invoiceId, PeriodLedgerItemRow item)
        => new()
        {
            InvoiceId = invoiceId,
            LineId = item.LineId,
            ItemName = item.ItemName,
            Specification = item.Specification,
            Quantity = item.Quantity,
            UnitPrice = item.UnitPrice,
            SupplyAmount = item.SupplyAmount,
            VatAmount = item.VatAmount,
            LineAmount = item.LineAmount,
            ItemNote = item.ItemNote
        };

    private static string PreviewText(string? value, int maxLength = 30)
    {
        var normalized = (value ?? string.Empty).ReplaceLineEndings(" ").Trim();
        if (normalized.Length <= maxLength)
            return normalized;
        return normalized[..maxLength] + "...";
    }

    partial void OnItemNoteChanged(string value)
        => OnPropertyChanged(nameof(ItemNotePreview));
}
