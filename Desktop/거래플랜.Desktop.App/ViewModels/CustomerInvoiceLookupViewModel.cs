using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class CustomerInvoiceLookupViewModel : ObservableObject
{
    private readonly LocalStateService _local;
    private readonly SessionState _session;
    private readonly UiDebouncer _customerFilterDebouncer = new();
    private readonly UiDebouncer _invoiceReloadDebouncer = new();
    private readonly SemaphoreSlim _invoiceLoadGate = new(1, 1);
    private readonly Dictionary<Guid, string> _customerNameById = new();
    private List<LocalCustomer> _allCustomers = new();
    private CancellationTokenSource? _invoiceLoadCts;
    private int _previewVersion;
    private int _customerSummaryVersion;
    private bool _suppressInvoiceReload;

    public CustomerInvoiceLookupViewModel(LocalStateService local, SessionState session)
    {
        _local = local;
        _session = session;
    }

    public ObservableCollection<LocalCustomer> FilteredCustomers { get; } = new ResettableObservableCollection<LocalCustomer>();
    public ObservableCollection<InvoiceListRow> InvoiceRows { get; } = new ResettableObservableCollection<InvoiceListRow>();
    public ObservableCollection<InvoiceLineEditModel> PreviewLines { get; } = new ResettableObservableCollection<InvoiceLineEditModel>();

    [ObservableProperty] private string _customerFilterText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedCustomer))]
    [NotifyPropertyChangedFor(nameof(InvoicePrimaryColumnHeader))]
    private LocalCustomer? _selectedCustomer;

    [ObservableProperty] private InvoiceListRow? _selectedInvoiceRow;
    [ObservableProperty] private DateOnly? _filterFrom;
    [ObservableProperty] private DateOnly? _filterTo;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "거래처를 검색하거나 선택해 거래내역을 조회하세요.";

    [ObservableProperty] private decimal _previewSupplyAmount;
    [ObservableProperty] private decimal _previewVatAmount;
    [ObservableProperty] private decimal _previewTotalAmount;

    [ObservableProperty] private string _previewCustomerName = string.Empty;
    [ObservableProperty] private string _previewCustomerBizNumber = string.Empty;
    [ObservableProperty] private string _previewCustomerPhone = string.Empty;
    [ObservableProperty] private string _previewCustomerContactPerson = string.Empty;
    [ObservableProperty] private string _previewCustomerAddress = string.Empty;
    [ObservableProperty] private string _previewCustomerNotes = string.Empty;
    [ObservableProperty] private decimal _previewCustomerAdvanceBalance;
    [ObservableProperty] private decimal _previewCustomerReceivableBalance;
    [ObservableProperty] private decimal _previewCustomerPayableBalance;
    [ObservableProperty] private decimal _previewCustomerPrepaymentBalance;
    [ObservableProperty] private string _previewLatestRentalInvoiceDateText = "-";
    [ObservableProperty] private string _previewLatestRentalItemSummary = "최근 렌탈 청구가 없습니다.";
    [ObservableProperty] private decimal _previewLatestRentalInvoiceAmount;
    [ObservableProperty] private decimal _previewRentalOutstandingAmount;

    public bool HasSelectedCustomer => SelectedCustomer is not null;
    public string InvoicePrimaryColumnHeader => HasSelectedCustomer ? "거래내역" : "거래처";

    public async Task LoadAsync(Guid? initialCustomerId = null, string? initialCustomerSearch = null)
    {
        IsBusy = true;
        StatusText = "거래처와 거래내역을 불러오는 중입니다.";
        _suppressInvoiceReload = true;
        try
        {
            _allCustomers = await _local.GetCustomersAsync(_session);
            _customerNameById.Clear();
            foreach (var customer in _allCustomers.Where(customer => customer.Id != Guid.Empty))
                _customerNameById[customer.Id] = customer.NameOriginal;

            CustomerFilterText = initialCustomerSearch?.Trim() ?? string.Empty;
            ApplyCustomerFilter();

            SelectedCustomer = initialCustomerId.HasValue
                ? _allCustomers.FirstOrDefault(customer => customer.Id == initialCustomerId.Value)
                : null;

            if (SelectedCustomer is null && !string.IsNullOrWhiteSpace(CustomerFilterText) && FilteredCustomers.Count == 1)
                SelectedCustomer = FilteredCustomers[0];

            ApplyCustomerInfo(SelectedCustomer);
        }
        finally
        {
            _suppressInvoiceReload = false;
        }

        await LoadInvoiceRowsAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await LoadInvoiceRowsAsync();
    }

    [RelayCommand]
    private async Task ShowAllAsync()
    {
        _suppressInvoiceReload = true;
        try
        {
            CustomerFilterText = string.Empty;
            SelectedCustomer = null;
            ApplyCustomerFilter();
            ApplyCustomerInfo(null);
        }
        finally
        {
            _suppressInvoiceReload = false;
        }

        await LoadInvoiceRowsAsync();
    }

    [RelayCommand]
    private async Task ClearDateRangeAsync()
    {
        _suppressInvoiceReload = true;
        try
        {
            FilterFrom = null;
            FilterTo = null;
        }
        finally
        {
            _suppressInvoiceReload = false;
        }

        await LoadInvoiceRowsAsync();
    }

    partial void OnCustomerFilterTextChanged(string value)
        => _customerFilterDebouncer.Debounce(TimeSpan.FromMilliseconds(120), ApplyCustomerFilter);

    partial void OnSelectedCustomerChanged(LocalCustomer? value)
    {
        ApplyCustomerInfo(value);
        if (!_suppressInvoiceReload)
            RequestInvoiceReload();
    }

    partial void OnSelectedInvoiceRowChanged(InvoiceListRow? value)
        => RequestLoadPreview(value);

    partial void OnFilterFromChanged(DateOnly? value) => RequestInvoiceReload();
    partial void OnFilterToChanged(DateOnly? value) => RequestInvoiceReload();

    private void RequestInvoiceReload()
    {
        if (_suppressInvoiceReload)
            return;

        _invoiceReloadDebouncer.DebounceAsync(
            TimeSpan.FromMilliseconds(120),
            LoadInvoiceRowsAsync,
            ex => AppLogger.Warn("UI", $"거래내역 조회창 재조회 실패: {ex.Message}"));
    }

    private async Task LoadInvoiceRowsAsync()
    {
        _invoiceLoadCts?.Cancel();
        var loadCts = new CancellationTokenSource();
        _invoiceLoadCts = loadCts;
        var ct = loadCts.Token;
        var gateEntered = false;
        var previousId = SelectedInvoiceRow?.Id;
        var previousVersionGroupId = SelectedInvoiceRow?.EffectiveVersionGroupId;

        try
        {
            await _invoiceLoadGate.WaitAsync(ct);
            gateEntered = true;
            if (!IsCurrentInvoiceLoad(loadCts))
                return;

            IsBusy = true;
            StatusText = "거래내역을 조회하는 중입니다.";

            var (from, to) = ResolveDateRange();
            var selectedCustomerId = SelectedCustomer?.Id;
            var invoiceList = await _local.GetInvoiceListSummariesAsync(from, to, selectedCustomerId, _session, ct);
            var transactions = await _local.GetStandaloneTransactionsForLedgerAsync(from, to, selectedCustomerId, _session, ct);
            if (!IsCurrentInvoiceLoad(loadCts))
                return;

            var customerMap = await BuildCustomerNameMapAsync(
                invoiceList.Select(invoice => invoice.CustomerId)
                    .Concat(transactions.Select(transaction => transaction.CustomerId)),
                ct);
            if (!IsCurrentInvoiceLoad(loadCts))
                return;

            var showCustomerName = selectedCustomerId is null;
            var invoiceRows = invoiceList.Select(invoice =>
            {
                var customerName = customerMap.TryGetValue(invoice.CustomerId, out var name) ? name : "(미지정)";
                return InvoiceListRow.From(invoice, customerName, showCustomerName);
            });
            var transactionRows = transactions.Select(transaction =>
            {
                var customerName = customerMap.TryGetValue(transaction.CustomerId, out var name) ? name : "(미지정)";
                return InvoiceListRow.From(transaction, customerName, showCustomerName);
            });

            var rows = invoiceRows
                .Concat(transactionRows)
                .OrderByDescending(row => row.InvoiceDate)
                .ThenByDescending(row => row.UpdatedAtUtc)
                .ThenByDescending(row => row.DisplayNumber)
                .ToList();

            InvoiceRows.ReplaceWith(rows);
            RestoreSelection(previousId, previousVersionGroupId);

            if (SelectedCustomer is not null)
                await RefreshCustomerSummaryAsync(SelectedCustomer);
            else if (SelectedInvoiceRow is null)
                await RefreshCustomerSummaryAsync(null);

            StatusText = BuildStatusText(rows.Count, from, to);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            if (gateEntered)
                _invoiceLoadGate.Release();

            if (ReferenceEquals(_invoiceLoadCts, loadCts))
                _invoiceLoadCts = null;

            loadCts.Dispose();
            if (_invoiceLoadCts is null)
                IsBusy = false;
        }
    }

    private bool IsCurrentInvoiceLoad(CancellationTokenSource loadCts)
        => ReferenceEquals(_invoiceLoadCts, loadCts) && !loadCts.IsCancellationRequested;

    private (DateOnly? From, DateOnly? To) ResolveDateRange()
    {
        if (FilterFrom.HasValue && FilterTo.HasValue && FilterFrom.Value > FilterTo.Value)
            return (FilterTo.Value, FilterFrom.Value);

        return (FilterFrom, FilterTo);
    }

    private string BuildStatusText(int count, DateOnly? from, DateOnly? to)
    {
        var customerText = SelectedCustomer is null
            ? "전체 거래처"
            : SelectedCustomer.NameOriginal;
        var periodText = (from, to) switch
        {
            ({ } f, { } t) => $"{f:yyyy/MM/dd}~{t:yyyy/MM/dd}",
            ({ } f, null) => $"{f:yyyy/MM/dd} 이후",
            (null, { } t) => $"{t:yyyy/MM/dd} 이전",
            _ => "전체 기간"
        };

        return $"{customerText} / {periodText} / {count:N0}건";
    }

    private void RestoreSelection(Guid? previousId, Guid? previousVersionGroupId)
    {
        if (previousId.HasValue)
        {
            var refreshed = InvoiceRows.FirstOrDefault(row => row.Id == previousId.Value);
            if (refreshed is not null)
            {
                SelectedInvoiceRow = refreshed;
                return;
            }
        }

        if (previousVersionGroupId.HasValue && previousVersionGroupId.Value != Guid.Empty)
        {
            var refreshed = InvoiceRows.FirstOrDefault(row => row.EffectiveVersionGroupId == previousVersionGroupId.Value);
            if (refreshed is not null)
            {
                SelectedInvoiceRow = refreshed;
                return;
            }
        }

        SelectedInvoiceRow = InvoiceRows.FirstOrDefault();
    }

    private async Task<Dictionary<Guid, string>> BuildCustomerNameMapAsync(IEnumerable<Guid> customerIds, CancellationToken ct)
    {
        var ids = customerIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        var map = new Dictionary<Guid, string>(ids.Count);
        foreach (var id in ids)
        {
            if (_customerNameById.TryGetValue(id, out var cachedName))
                map[id] = cachedName;
        }

        var missing = ids.Where(id => !map.ContainsKey(id)).ToList();
        if (missing.Count == 0)
            return map;

        var missingMap = await _local.GetCustomerNameMapAsync(missing, ct);
        foreach (var pair in missingMap)
        {
            map[pair.Key] = pair.Value;
            _customerNameById[pair.Key] = pair.Value;
        }

        return map;
    }

    private void ApplyCustomerFilter()
    {
        var text = CustomerFilterText.Trim();
        var filtered = string.IsNullOrWhiteSpace(text)
            ? _allCustomers
            : _allCustomers.Where(customer => MatchesCustomerQuickFilter(customer, text));
        FilteredCustomers.ReplaceWith(filtered);
    }

    private static bool MatchesCustomerQuickFilter(LocalCustomer customer, string rawText)
    {
        var tokens = rawText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Length == 0 || tokens.All(token => ContainsAnyCustomerField(customer, token));
    }

    private static bool ContainsAnyCustomerField(LocalCustomer customer, string token)
        => ContainsText(customer.NameOriginal, token)
           || ContainsText(customer.BusinessNumber, token)
           || ContainsText(customer.Phone, token)
           || ContainsText(customer.MobilePhone, token)
           || ContainsText(customer.ContactPerson, token)
           || ContainsText(customer.Department, token)
           || ContainsText(customer.TradeType, token)
           || ContainsText(customer.PriceGrade, token)
           || ContainsText(customer.ResponsibleOfficeCode, token)
           || ContainsText(customer.Address, token)
           || ContainsText(customer.DetailAddress, token)
           || ContainsText(customer.Notes, token);

    private static bool ContainsText(string? value, string token)
        => !string.IsNullOrWhiteSpace(value) && value.Contains(token, StringComparison.OrdinalIgnoreCase);

    private void RequestLoadPreview(InvoiceListRow? row)
    {
        var version = Interlocked.Increment(ref _previewVersion);
        UiTaskHelper.Forget(
            LoadPreviewAsync(row, version),
            "UI",
            "거래내역 조회창 전표 미리보기",
            ex =>
            {
                if (version == Volatile.Read(ref _previewVersion))
                    AppLogger.Warn("UI", $"거래내역 조회창 전표 미리보기 실패: {ex.Message}");
            });
    }

    private async Task LoadPreviewAsync(InvoiceListRow? row, int version)
    {
        if (version != Volatile.Read(ref _previewVersion))
            return;

        PreviewLines.Clear();
        PreviewSupplyAmount = 0m;
        PreviewVatAmount = 0m;
        PreviewTotalAmount = 0m;

        if (row is null)
        {
            if (SelectedCustomer is null)
                await RefreshCustomerSummaryAsync(null);
            return;
        }

        if (row.IsTransactionRow)
        {
            if (SelectedCustomer is null)
                await LoadCustomerInfoFromRowAsync(row, version);
            return;
        }

        var invoice = await _local.GetLatestInvoiceVersionAsync(row.Id, _session);
        if (version != Volatile.Read(ref _previewVersion))
            return;

        if (invoice is null)
        {
            if (SelectedCustomer is null)
                await RefreshCustomerSummaryAsync(null);
            return;
        }

        var lines = invoice.Lines
            .Where(line => !line.IsDeleted)
            .OrderBy(line => line.OrderIndex > 0 ? line.OrderIndex : int.MaxValue)
            .ThenBy(line => line.Id)
            .Select(InvoiceLineEditModel.FromLocal)
            .ToList();
        PreviewLines.ReplaceWith(lines);
        PreviewSupplyAmount = invoice.SupplyAmount;
        PreviewVatAmount = invoice.VatAmount;
        PreviewTotalAmount = invoice.TotalAmount;

        if (SelectedCustomer is null)
            await LoadCustomerInfoFromRowAsync(row, version);
    }

    private async Task LoadCustomerInfoFromRowAsync(InvoiceListRow row, int previewVersion)
    {
        var customer = _allCustomers.FirstOrDefault(current => current.Id == row.CustomerId)
            ?? await _local.GetCustomerAsync(row.CustomerId, _session);
        if (previewVersion != Volatile.Read(ref _previewVersion))
            return;

        await RefreshCustomerSummaryAsync(customer);
    }

    private void ApplyCustomerInfo(LocalCustomer? customer)
        => UiTaskHelper.Forget(
            RefreshCustomerSummaryAsync(customer),
            "UI",
            "거래내역 조회창 거래처 요약",
            ex => AppLogger.Warn("UI", $"거래내역 조회창 거래처 요약 실패: {ex.Message}"));

    private async Task RefreshCustomerSummaryAsync(LocalCustomer? customer)
    {
        var version = Interlocked.Increment(ref _customerSummaryVersion);
        if (customer is null)
        {
            if (version != Volatile.Read(ref _customerSummaryVersion))
                return;

            PreviewCustomerName = string.Empty;
            PreviewCustomerBizNumber = string.Empty;
            PreviewCustomerPhone = string.Empty;
            PreviewCustomerContactPerson = string.Empty;
            PreviewCustomerAddress = string.Empty;
            PreviewCustomerNotes = string.Empty;
            PreviewCustomerAdvanceBalance = 0m;
            PreviewCustomerReceivableBalance = 0m;
            PreviewCustomerPayableBalance = 0m;
            PreviewCustomerPrepaymentBalance = 0m;
            ResetRentalInvoicePreviewSummary();
            return;
        }

        var summary = await _local.GetCustomerFinancialSummaryAsync(customer.Id, _session);
        var invoices = await _local.GetInvoiceListSummariesAsync(from: null, to: null, customerId: customer.Id, session: _session);
        if (version != Volatile.Read(ref _customerSummaryVersion))
            return;

        PreviewCustomerName = customer.NameOriginal;
        PreviewCustomerBizNumber = customer.BusinessNumber;
        PreviewCustomerPhone = customer.Phone;
        PreviewCustomerContactPerson = customer.ContactPerson;
        PreviewCustomerAddress = string.Join(' ', new[] { customer.Address, customer.DetailAddress }.Where(value => !string.IsNullOrWhiteSpace(value)));
        PreviewCustomerNotes = customer.Notes;
        PreviewCustomerAdvanceBalance = summary.AdvanceBalance;
        PreviewCustomerReceivableBalance = summary.ReceivableAmount;
        PreviewCustomerPayableBalance = summary.PayableAmount;
        PreviewCustomerPrepaymentBalance = summary.PrepaidAmount;
        ApplyRentalInvoicePreviewSummary(invoices);
    }

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
        PreviewRentalOutstandingAmount = rentalInvoices.Sum(invoice => Math.Max(0m, invoice.TotalAmount - invoice.SettledAmount));
    }
}
