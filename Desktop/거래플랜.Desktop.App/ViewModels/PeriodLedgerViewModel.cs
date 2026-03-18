using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class PeriodLedgerViewModel : ObservableObject
{
    private const string ExportPathSettingKey = "Export.Path";

    private readonly LocalStateService _local;
    private readonly PeriodLedgerAggregationService _aggregation;
    private readonly PeriodLedgerExcelExportService _exporter;
    private readonly SessionState _session;
    private List<LocalCustomer> _allCustomers = [];

    [ObservableProperty] private DateTime _fromDate = DateTime.Today;
    [ObservableProperty] private DateTime _toDate = DateTime.Today;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSelectCustomer))]
    [NotifyPropertyChangedFor(nameof(ScopeHintMessage))]
    private bool _isAllCustomers;
    [ObservableProperty] private string _selectedScopeOption = "개별업체집계";

    [ObservableProperty] private string _customerSearchText = string.Empty;
    [ObservableProperty] private LocalCustomer? _selectedCustomer;
    [ObservableProperty] private PeriodLedgerType _selectedLedgerType = PeriodLedgerType.SalesPurchase;
    [ObservableProperty] private bool _sortByCustomerName;
    [ObservableProperty] private bool _showProfit;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "집계 조건을 선택하세요.";
    [ObservableProperty] private string _lastExportPath = string.Empty;

    public ObservableCollection<LocalCustomer> Customers { get; } = [];
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
            var query = new PeriodLedgerQuery
            {
                From = DateOnly.FromDateTime(FromDate.Date),
                To = DateOnly.FromDateTime(ToDate.Date),
                LedgerType = SelectedLedgerType,
                Scope = IsAllCustomers ? PeriodLedgerScope.AllCustomers : PeriodLedgerScope.SingleCustomer,
                CustomerId = IsAllCustomers ? null : SelectedCustomer?.Id,
                SortByCustomerName = SortByCustomerName,
                IncludeProfit = ShowProfit
            };

            var progress = new Progress<string>(message => StatusMessage = message);
            var result = await _aggregation.BuildAsync(query, _session, progress);

            if (!string.IsNullOrWhiteSpace(result.ProfitWarningMessage))
            {
                StatusMessage = result.ProfitWarningMessage;
            }

            var exportPath = await _local.GetSettingAsync(ExportPathSettingKey) ?? string.Empty;
            var filePath = await _exporter.ExportAsync(result, exportPath, progress);
            LastExportPath = filePath;
            StatusMessage = $"완료: {Path.GetFileName(filePath)}";
        }
        catch (Exception ex)
        {
            AppLogger.Error("PeriodLedger", "자료기간별 집계 실패", ex);
            StatusMessage = $"오류: {ex.Message}";
            System.Windows.MessageBox.Show($"자료기간별 집계 중 오류가 발생했습니다.\n{ex.Message}", "오류", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RefreshCustomerList(IEnumerable<LocalCustomer> customers)
    {
        Customers.Clear();
        foreach (var c in customers.OrderBy(c => c.NameOriginal, StringComparer.CurrentCultureIgnoreCase))
            Customers.Add(c);
    }

    partial void OnSelectedLedgerTypeChanged(PeriodLedgerType value)
    {
        OnPropertyChanged(nameof(IsSalesPurchaseSelected));
        OnPropertyChanged(nameof(IsSalesSelected));
        OnPropertyChanged(nameof(IsPurchaseSelected));
        OnPropertyChanged(nameof(IsReceiptPaymentSelected));
        OnPropertyChanged(nameof(IsYeonsuDeliverySelected));
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
}



