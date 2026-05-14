using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class YeonsuDeliveryViewModel : ObservableObject
{
    private const string FeeRateSettingKey = "YeonsuDelivery.FeeRatePercent";
    private readonly LocalStateService _local;
    private readonly SessionState _session;
    private readonly UiDebouncer _filterDebouncer = new();
    private readonly List<LedgerSourceRow> _sourceRows = new();
    private bool _isInitializing;
    private bool _suppressFeeRateTextChanged;
    private decimal _feeRatePercentValue = 20m;

    public const string WarehouseOptionAll = "전체";
    public const string WarehouseOptionUsenet = "USENET 창고";
    public const string WarehouseOptionYeonsu = "YEONSU 창고";

    public const string ViewTargetAll = "전체";
    public const string ViewTargetSales = "매출";
    public const string ViewTargetPurchase = "매입";

    public const string EntryCategoryAll = "전체";
    public const string EntryCategorySale = "매출";
    public const string EntryCategoryRental = "렌탈료";
    public const string EntryCategoryPrepayment = "선결제";
    public const string EntryCategoryPurchase = "매입";

    public ObservableCollection<YeonsuDeliveryRow> Deliveries { get; } = new();

    public IReadOnlyList<string> WarehouseOptions { get; } =
    [
        WarehouseOptionAll,
        WarehouseOptionUsenet,
        WarehouseOptionYeonsu
    ];

    public IReadOnlyList<string> ViewTargetOptions { get; } =
    [
        ViewTargetAll,
        ViewTargetSales,
        ViewTargetPurchase
    ];

    public IReadOnlyList<string> EntryCategoryOptions { get; } =
    [
        EntryCategoryAll,
        EntryCategorySale,
        EntryCategoryRental,
        EntryCategoryPrepayment,
        EntryCategoryPurchase
    ];

    [ObservableProperty] private DateOnly _fromDate = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    [ObservableProperty] private DateOnly _toDate = DateOnly.FromDateTime(DateTime.Today);
    [ObservableProperty] private string _customerSearchText = string.Empty;
    [ObservableProperty] private string _selectedViewTarget = ViewTargetAll;
    [ObservableProperty] private string _selectedEntryCategory = EntryCategoryAll;
    [ObservableProperty] private string _selectedWarehouseOption = WarehouseOptionAll;
    [ObservableProperty] private string _feeRatePercentText = "20";
    [ObservableProperty] private YeonsuDeliveryRow? _selectedDelivery;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "조회 조건을 선택하세요.";
    [ObservableProperty] private int _summaryCount;
    [ObservableProperty] private decimal _summaryPurchaseAmount;
    [ObservableProperty] private decimal _summarySalesAmount;
    [ObservableProperty] private decimal _summaryProfitAmount;
    [ObservableProperty] private decimal _summaryFeeAmount;

    public YeonsuDeliveryViewModel(LocalStateService local, SessionState session)
    {
        _local = local;
        _session = session;
    }

    public async Task InitializeAsync()
    {
        _isInitializing = true;
        try
        {
            await LoadSavedFeeRateAsync();
            await LoadDeliveriesAsync();
        }
        finally
        {
            _isInitializing = false;
        }
    }

    partial void OnCustomerSearchTextChanged(string value)
    {
        if (_isInitializing || IsBusy)
            return;

        RebuildDeliveries();
    }

    partial void OnFromDateChanged(DateOnly value)
        => RequestLedgerReload();

    partial void OnToDateChanged(DateOnly value)
        => RequestLedgerReload();

    partial void OnSelectedViewTargetChanged(string value)
    {
        if (_isInitializing || IsBusy)
            return;

        if (NormalizeEntryCategoryForViewTarget())
            return;

        RebuildDeliveries();
    }

    partial void OnSelectedWarehouseOptionChanged(string value)
        => RequestLedgerReload();

    partial void OnSelectedEntryCategoryChanged(string value)
    {
        if (_isInitializing || IsBusy)
            return;

        if (string.Equals(SelectedViewTarget, ViewTargetPurchase, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(value, EntryCategoryAll, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(value, EntryCategoryPurchase, StringComparison.OrdinalIgnoreCase))
        {
            SelectedEntryCategory = EntryCategoryPurchase;
            return;
        }

        RebuildDeliveries();
    }

    partial void OnFeeRatePercentTextChanged(string value)
    {
        if (_isInitializing || _suppressFeeRateTextChanged)
            return;

        if (!TryParseFeeRate(value, out var normalizedFeeRate))
        {
            RestoreFeeRateText();
            StatusMessage = $"수수료율은 0~100 사이 숫자만 입력할 수 있습니다. 마지막 값 {FormatFeeRate(_feeRatePercentValue)}%로 복원했습니다.";
            return;
        }

        ApplyFeeRate(normalizedFeeRate, persist: true, rebuild: true);
    }

    private void RequestLedgerReload()
    {
        if (_isInitializing)
            return;

        _filterDebouncer.DebounceAsync(
            TimeSpan.FromMilliseconds(250),
            () => LoadDeliveriesAsync(),
            ex => StatusMessage = $"매입/매출 장부를 다시 불러오지 못했습니다. {ex.Message}");
    }

    [RelayCommand]
    private async Task LoadDeliveriesAsync()
    {
        if (FromDate > ToDate)
        {
            StatusMessage = "조회 시작일이 종료일보다 늦습니다.";
            return;
        }

        IsBusy = true;
        try
        {
            var warehouseCodeFilter = ResolveWarehouseCodeFilter(SelectedWarehouseOption);
            var accountOfficeCode = ResolveAccountOfficeCode();
            var invoices = await _local.GetSalesPurchaseLedgerInvoicesAsync(
                FromDate,
                ToDate,
                customerId: null,
                warehouseCode: warehouseCodeFilter,
                responsibleOfficeCode: accountOfficeCode);

            var customerMap = await _local.GetCustomerNameMapAsync(invoices.Select(invoice => invoice.CustomerId));
            var itemMap = await _local.GetItemMapAsync(
                invoices.SelectMany(invoice => invoice.Lines)
                    .Where(line => !line.IsDeleted && line.ItemId.HasValue)
                    .Select(line => line.ItemId!.Value));
            var costAllocationMap = (await _local.GetCostAllocationsForInvoicesAsync(invoices.Select(invoice => invoice.Id)))
                .GroupBy(allocation => allocation.SalesInvoiceLineId)
                .ToDictionary(group => group.Key, group => group.ToList());

            _sourceRows.Clear();
            foreach (var invoice in invoices)
            {
                var customerName = customerMap.TryGetValue(invoice.CustomerId, out var resolvedCustomerName) &&
                                   !string.IsNullOrWhiteSpace(resolvedCustomerName)
                    ? resolvedCustomerName
                    : "(거래처 미상)";

                var isPurchaseInvoice = invoice.VoucherType is VoucherType.Purchase or VoucherType.Procurement;
                foreach (var line in invoice.Lines
                             .Where(current => !current.IsDeleted)
                             .OrderBy(current => current.Id))
                {
                    itemMap.TryGetValue(line.ItemId ?? Guid.Empty, out var item);
                    costAllocationMap.TryGetValue(line.Id, out var lineAllocations);

                    var purchaseAmount = isPurchaseInvoice
                        ? CalculatePurchaseEntryAmount(line)
                        : CalculateSalesCostAmount(line, item, lineAllocations);
                    var salesAmount = isPurchaseInvoice
                        ? 0m
                        : Math.Round(line.LineAmount, 0, MidpointRounding.AwayFromZero);
                    var profitAmount = isPurchaseInvoice
                        ? 0m
                        : Math.Round(salesAmount - purchaseAmount, 0, MidpointRounding.AwayFromZero);

                    _sourceRows.Add(new LedgerSourceRow
                    {
                        InvoiceId = invoice.Id,
                        InvoiceLineId = line.Id,
                        TradeDate = invoice.InvoiceDate,
                        ViewTarget = isPurchaseInvoice ? ViewTargetPurchase : ViewTargetSales,
                        EntryCategory = ResolveEntryCategory(invoice, line),
                        CustomerName = customerName,
                        ModelName = ResolveModelName(line, item),
                        TradeDescription = ResolveTradeDescription(line),
                        PurchaseAmount = purchaseAmount,
                        SalesAmount = salesAmount,
                        ProfitAmount = profitAmount,
                        Note = invoice.Memo?.Trim() ?? string.Empty,
                        LastSavedAtUtc = invoice.LastSavedAtUtc
                    });
                }
            }

            RebuildDeliveries();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ResetFiltersAsync()
    {
        FromDate = new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1);
        ToDate = DateOnly.FromDateTime(DateTime.Today);
        CustomerSearchText = string.Empty;
        SelectedViewTarget = ViewTargetAll;
        SelectedEntryCategory = EntryCategoryAll;
        SelectedWarehouseOption = WarehouseOptionAll;
        ApplyFeeRate(20m, persist: true, rebuild: false);
        await LoadDeliveriesAsync();
    }

    private bool NormalizeEntryCategoryForViewTarget()
    {
        if (string.Equals(SelectedViewTarget, ViewTargetPurchase, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(SelectedEntryCategory, EntryCategoryAll, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(SelectedEntryCategory, EntryCategoryPurchase, StringComparison.OrdinalIgnoreCase))
        {
            SelectedEntryCategory = EntryCategoryPurchase;
            return true;
        }

        if (!string.Equals(SelectedViewTarget, ViewTargetPurchase, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(SelectedEntryCategory, EntryCategoryPurchase, StringComparison.OrdinalIgnoreCase))
        {
            SelectedEntryCategory = EntryCategoryAll;
            return true;
        }

        return false;
    }

    private void RebuildDeliveries()
    {
        IEnumerable<LedgerSourceRow> filtered = _sourceRows;
        var keyword = (CustomerSearchText ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            filtered = filtered.Where(row =>
                row.CustomerName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                row.TradeDescription.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                row.ModelName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                row.Note.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.Equals(SelectedViewTarget, ViewTargetAll, StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(row =>
                string.Equals(row.ViewTarget, SelectedViewTarget, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.Equals(SelectedEntryCategory, EntryCategoryAll, StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(row =>
                string.Equals(row.EntryCategory, SelectedEntryCategory, StringComparison.OrdinalIgnoreCase));
        }

        var rows = filtered
            .OrderByDescending(row => row.TradeDate)
            .ThenByDescending(row => row.LastSavedAtUtc)
            .ThenBy(row => row.InvoiceId)
            .ThenBy(row => row.InvoiceLineId)
            .ToList();

        Deliveries.Clear();
        foreach (var row in rows)
        {
            Deliveries.Add(new YeonsuDeliveryRow
            {
                InvoiceId = row.InvoiceId,
                InvoiceLineId = row.InvoiceLineId,
                TradeDate = row.TradeDate,
                EntryCategory = row.EntryCategory,
                CustomerName = row.CustomerName,
                ModelName = row.ModelName,
                TradeDescription = row.TradeDescription,
                PurchaseAmount = row.PurchaseAmount,
                SalesAmount = row.SalesAmount,
                ProfitAmount = row.ProfitAmount,
                FeeAmount = CalculateFeeAmount(row),
                Note = row.Note
            });
        }

        SummaryCount = Deliveries.Count;
        SummaryPurchaseAmount = Deliveries.Sum(row => row.PurchaseAmount);
        SummarySalesAmount = Deliveries.Sum(row => row.SalesAmount);
        SummaryProfitAmount = Deliveries.Sum(row => row.ProfitAmount);
        SummaryFeeAmount = Deliveries.Sum(row => row.FeeAmount);

        UpdateStatusMessage(rows);
    }

    private void UpdateStatusMessage(IReadOnlyCollection<LedgerSourceRow> rows)
    {
        if (rows.Count == 0)
        {
            StatusMessage = "조건에 맞는 매입/매출 장부 내역이 없습니다.";
            return;
        }

        var salesCount = rows.Count(row => string.Equals(row.ViewTarget, ViewTargetSales, StringComparison.OrdinalIgnoreCase));
        var purchaseCount = rows.Count(row => string.Equals(row.ViewTarget, ViewTargetPurchase, StringComparison.OrdinalIgnoreCase));

        StatusMessage = SelectedViewTarget switch
        {
            ViewTargetSales => $"매출 {salesCount:N0}건 / 수익금액 합계 {SummaryProfitAmount:N0}원 / 수수료 합계 {SummaryFeeAmount:N0}원",
            ViewTargetPurchase => $"매입 {purchaseCount:N0}건 / 매입금액 합계 {SummaryPurchaseAmount:N0}원",
            _ => $"매출 {salesCount:N0}건 / 매입 {purchaseCount:N0}건 / 수익금액 합계 {SummaryProfitAmount:N0}원 / 수수료 합계 {SummaryFeeAmount:N0}원"
        };
    }

    private async Task LoadSavedFeeRateAsync()
    {
        var savedValue = await _local.GetSettingAsync(BuildAccountScopedFeeRateSettingKey());
        if (!TryParseFeeRate(savedValue, out var feeRate))
            feeRate = 20m;

        ApplyFeeRate(feeRate, persist: false, rebuild: false);
    }

    private void ApplyFeeRate(decimal value, bool persist, bool rebuild)
    {
        _feeRatePercentValue = value;

        var formattedValue = FormatFeeRate(value);
        if (!string.Equals(FeeRatePercentText, formattedValue, StringComparison.Ordinal))
        {
            _suppressFeeRateTextChanged = true;
            FeeRatePercentText = formattedValue;
            _suppressFeeRateTextChanged = false;
        }

        if (persist)
        {
            UiTaskHelper.Forget(
                _local.SetSettingAsync(BuildAccountScopedFeeRateSettingKey(), value.ToString(CultureInfo.InvariantCulture)),
                "LEDGER",
                "매입/매출 장부 수수료율 저장",
                ex => AppLogger.Warn("LEDGER", $"수수료율 저장 실패: {ex.Message}"));
        }

        if (rebuild && !_isInitializing)
            RebuildDeliveries();
    }

    private void RestoreFeeRateText()
    {
        _suppressFeeRateTextChanged = true;
        FeeRatePercentText = FormatFeeRate(_feeRatePercentValue);
        _suppressFeeRateTextChanged = false;
    }

    private string BuildAccountScopedFeeRateSettingKey()
    {
        var username = (_session.User?.Username ?? "local").Trim().ToLowerInvariant();
        var databaseName = TenantScopeCatalog.GetDatabaseName(_session.SelectedBusinessDatabaseName);
        return $"{FeeRateSettingKey}.{databaseName}.{username}";
    }

    private string ResolveAccountOfficeCode()
        => OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(_session.OfficeCode, DomainConstants.OfficeYeonsu);

    private decimal CalculateFeeAmount(LedgerSourceRow row)
    {
        if (string.Equals(row.ViewTarget, ViewTargetPurchase, StringComparison.OrdinalIgnoreCase) ||
            row.ProfitAmount <= 0m)
        {
            return 0m;
        }

        return Math.Round(
            row.ProfitAmount * (_feeRatePercentValue / 100m),
            0,
            MidpointRounding.AwayFromZero);
    }

    private static decimal CalculateSalesCostAmount(
        LocalInvoiceLine line,
        LocalItem? item,
        IReadOnlyCollection<LocalCostAllocation>? lineAllocations)
    {
        if (lineAllocations is not null && lineAllocations.Count > 0)
        {
            return Math.Round(
                lineAllocations.Sum(allocation => allocation.CostAmount),
                0,
                MidpointRounding.AwayFromZero);
        }

        var quantity = Math.Abs(line.Quantity);
        var fallbackUnitCost = item?.PurchasePrice ?? 0m;
        return Math.Round(quantity * fallbackUnitCost, 0, MidpointRounding.AwayFromZero);
    }

    private static decimal CalculatePurchaseEntryAmount(LocalInvoiceLine line)
    {
        var lineAmount = Math.Round(Math.Abs(line.LineAmount), 0, MidpointRounding.AwayFromZero);
        if (lineAmount > 0m)
            return lineAmount;

        return Math.Round(Math.Abs(line.Quantity * line.UnitPrice), 0, MidpointRounding.AwayFromZero);
    }

    private static string ResolveModelName(LocalInvoiceLine line, LocalItem? item)
        => FirstNonEmpty(line.SpecificationOriginal, item?.SpecificationOriginal);

    private static string ResolveTradeDescription(LocalInvoiceLine line)
        => FirstNonEmpty(line.Remark, line.ItemNameOriginal, "(거래내용 없음)");

    private static string ResolveEntryCategory(LocalInvoice invoice, LocalInvoiceLine line)
    {
        if (invoice.VoucherType is VoucherType.Purchase or VoucherType.Procurement)
            return EntryCategoryPurchase;

        if (invoice.LinkedRentalBillingProfileId.HasValue ||
            invoice.LinkedRentalBillingRunId.HasValue ||
            line.RentalStartDate.HasValue ||
            line.RentalEndDate.HasValue)
        {
            return EntryCategoryRental;
        }

        if (ContainsPrepaymentKeyword(line.ItemNameOriginal) ||
            ContainsPrepaymentKeyword(line.Remark) ||
            ContainsPrepaymentKeyword(invoice.Memo))
        {
            return EntryCategoryPrepayment;
        }

        return EntryCategorySale;
    }

    private static bool ContainsPrepaymentKeyword(string? value)
        => !string.IsNullOrWhiteSpace(value) &&
           value.Contains("선결제", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseFeeRate(string? raw, out decimal value)
    {
        value = 20m;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var normalizedText = raw.Replace("%", string.Empty, StringComparison.Ordinal).Trim();
        if (!decimal.TryParse(normalizedText, NumberStyles.Number, CultureInfo.CurrentCulture, out var parsed) &&
            !decimal.TryParse(normalizedText, NumberStyles.Number, CultureInfo.InvariantCulture, out parsed))
        {
            return false;
        }

        if (parsed < 0m || parsed > 100m)
            return false;

        value = Math.Round(parsed, 2, MidpointRounding.AwayFromZero);
        return true;
    }

    private static string FormatFeeRate(decimal value)
        => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string ResolveWarehouseCodeFilter(string option)
    {
        return option switch
        {
            WarehouseOptionUsenet => DomainConstants.WarehouseUsenetMain,
            WarehouseOptionYeonsu => DomainConstants.WarehouseYeonsuMain,
            _ => string.Empty
        };
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return string.Empty;
    }

    private sealed class LedgerSourceRow
    {
        public Guid InvoiceId { get; init; }
        public Guid InvoiceLineId { get; init; }
        public DateOnly TradeDate { get; init; }
        public string ViewTarget { get; init; } = string.Empty;
        public string EntryCategory { get; init; } = string.Empty;
        public string CustomerName { get; init; } = string.Empty;
        public string ModelName { get; init; } = string.Empty;
        public string TradeDescription { get; init; } = string.Empty;
        public decimal PurchaseAmount { get; init; }
        public decimal SalesAmount { get; init; }
        public decimal ProfitAmount { get; init; }
        public string Note { get; init; } = string.Empty;
        public DateTime LastSavedAtUtc { get; init; }
    }
}
