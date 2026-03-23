using System.Collections.ObjectModel;
using GeoraePlan.Mobile.App.Models;
using GeoraePlan.Mobile.App.Services;
using Microsoft.Maui.ApplicationModel;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.ViewModels;

public enum CustomerDetailSection
{
    Summary = 0,
    Contracts = 1,
    Invoices = 2,
    Payments = 3
}

public sealed class CustomersViewModel : ObservableObject
{
    private readonly GeoraePlanApiClient _api;
    private readonly CustomerContractCacheStore _cacheStore;
    private readonly SyncCoordinator _syncCoordinator;

    private string _searchText = string.Empty;
    private string _statusMessage = "嫄곕옒泥섎? 遺덈윭?ㅼ꽭??";
    private bool _isBusy;
    private DateTime? _lastRefreshUtc;
    private CustomerDto? _selectedCustomer;
    private bool _isDetailBusy;
    private string _detailStatusMessage = "嫄곕옒泥섎? ?좏깮?섎㈃ ?곸꽭 ?뺣낫媛 ?쒖떆?⑸땲??";
    private CustomerDetailSection _selectedDetailSection = CustomerDetailSection.Summary;

    public CustomersViewModel(GeoraePlanApiClient api, CustomerContractCacheStore cacheStore, SyncCoordinator syncCoordinator)
    {
        _api = api;
        _cacheStore = cacheStore;
        _syncCoordinator = syncCoordinator;
        RefreshCommand = new AsyncCommand(RefreshAsync);
    }

    public ObservableCollection<CustomerDto> Customers { get; } = new();
    public ObservableCollection<InvoiceDto> SelectedCustomerInvoices { get; } = new();
    public ObservableCollection<CustomerContractDto> SelectedCustomerContracts { get; } = new();
    public ObservableCollection<CustomerPaymentHistoryRow> SelectedCustomerPayments { get; } = new();

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

    public CustomerDto? SelectedCustomer
    {
        get => _selectedCustomer;
        private set
        {
            if (SetProperty(ref _selectedCustomer, value))
            {
                OnPropertyChanged(nameof(SelectedCustomerName));
                OnPropertyChanged(nameof(SelectedCustomerPhone));
                OnPropertyChanged(nameof(SelectedCustomerNotes));
                OnPropertyChanged(nameof(HasSelectedCustomer));
            }
        }
    }

    public bool IsDetailBusy
    {
        get => _isDetailBusy;
        set => SetProperty(ref _isDetailBusy, value);
    }

    public string DetailStatusMessage
    {
        get => _detailStatusMessage;
        set => SetProperty(ref _detailStatusMessage, value);
    }

    public CustomerDetailSection SelectedDetailSection
    {
        get => _selectedDetailSection;
        private set
        {
            if (SetProperty(ref _selectedDetailSection, value))
            {
                OnPropertyChanged(nameof(ShowSummarySection));
                OnPropertyChanged(nameof(ShowContractsSection));
                OnPropertyChanged(nameof(ShowInvoicesSection));
                OnPropertyChanged(nameof(ShowPaymentsSection));
            }
        }
    }

    public bool HasSelectedCustomer => SelectedCustomer is not null;
    public bool HasSearchText => !string.IsNullOrWhiteSpace(SearchText);
    public string SelectedCustomerName => SelectedCustomer?.NameOriginal ?? string.Empty;
    public string SelectedCustomerPhone => string.IsNullOrWhiteSpace(SelectedCustomer?.Phone) ? "?깅줉????쒖쟾???놁쓬" : SelectedCustomer!.Phone;
    public string SelectedCustomerNotes => string.IsNullOrWhiteSpace(SelectedCustomer?.Notes) ? "?깅줉??硫붾え ?놁쓬" : SelectedCustomer!.Notes;
    public string SelectedCustomerSummaryCounts => $"?? {SelectedCustomerContracts.Count:N0}? ? ?? ?? {SelectedCustomerInvoices.Count:N0}? ? ?? ?? {SelectedCustomerPayments.Count:N0}?";
    public bool ShowSummarySection => SelectedDetailSection == CustomerDetailSection.Summary;
    public bool ShowContractsSection => SelectedDetailSection == CustomerDetailSection.Contracts;
    public bool ShowInvoicesSection => SelectedDetailSection == CustomerDetailSection.Invoices;
    public bool ShowPaymentsSection => SelectedDetailSection == CustomerDetailSection.Payments;
    public double ContractsSectionHeight => CalculateListHeight(SelectedCustomerContracts.Count, 64, 42, 2);
    public double InvoicesSectionHeight => CalculateListHeight(SelectedCustomerInvoices.Count, 78, 42, 2);
    public double PaymentsSectionHeight => CalculateListHeight(SelectedCustomerPayments.Count, 96, 42, 2);

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
            StatusMessage = "嫄곕옒泥섎? 議고쉶?섍퀬 ?덉뒿?덈떎.";
            await _syncCoordinator.RefreshIfServerChangedAsync("customers-refresh", TimeSpan.FromSeconds(5));

            var result = await _api.GetCustomersAsync(SearchText);
            ReplaceCustomers(result);

            if (string.IsNullOrWhiteSpace(SearchText))
                await _cacheStore.SaveCustomersAsync(result);

            _lastRefreshUtc = DateTime.UtcNow;
            StatusMessage = $"??? {Customers.Count:N0}?";

            if (SelectedCustomer is not null)
            {
                var updatedSelection = Customers.FirstOrDefault(customer => customer.Id == SelectedCustomer.Id);
                if (updatedSelection is not null)
                    await SelectCustomerAsync(updatedSelection);
                else
                    ClearSelectedCustomer();
            }
        }
        catch (Exception ex)
        {
            var cached = await LoadCachedCustomersAsync();
            if (cached.Count > 0)
            {
                ReplaceCustomers(cached);
                _lastRefreshUtc = DateTime.UtcNow;
                StatusMessage = $"?쒕쾭 ?곌껐 ?ㅽ뙣: {ex.Message} / 罹먯떆 {Customers.Count:N0}嫄??쒖떆";
            }
            else
            {
                StatusMessage = $"嫄곕옒泥?議고쉶 ?ㅽ뙣: {ex.Message}";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SelectCustomerAsync(CustomerDto customer)
    {
        if (customer is null)
            return;

        SelectedCustomer = customer;
        SelectedDetailSection = CustomerDetailSection.Summary;
        ReplaceInvoices(Array.Empty<InvoiceDto>());
        ReplaceContracts(Array.Empty<CustomerContractDto>());
        ReplacePayments(Array.Empty<CustomerPaymentHistoryRow>());
        IsDetailBusy = true;
        DetailStatusMessage = "嫄곕옒泥??곸꽭 ?뺣낫瑜?遺덈윭?ㅺ퀬 ?덉뒿?덈떎.";

        Exception? detailError = null;
        Exception? contractError = null;
        CustomerDetailDto? detail = null;
        IReadOnlyList<CustomerContractDto> contracts = Array.Empty<CustomerContractDto>();

        try
        {
            try
            {
                detail = await _api.GetCustomerDetailAsync(customer.Id);
            }
            catch (Exception ex)
            {
                detailError = ex;
            }

            try
            {
                var contractResult = await _api.GetCustomerContractsAsync(customer.Id);
                contracts = contractResult;
                await _cacheStore.SaveContractsAsync(customer.Id, contractResult);
            }
            catch (Exception ex)
            {
                contractError = ex;
                contracts = await _cacheStore.LoadContractsAsync(customer.Id);
            }

            SelectedCustomer = detail?.Customer ?? customer;
            ReplaceInvoices(detail?.RecentInvoices ?? Enumerable.Empty<InvoiceDto>());
            ReplaceContracts(contracts);
            ReplacePayments(BuildPaymentRows(detail?.RecentInvoices ?? Enumerable.Empty<InvoiceDto>()));

            if (detailError is null && contractError is null)
            {
                DetailStatusMessage = SelectedCustomerSummaryCounts;
            }
            else if (detailError is not null && contractError is null)
            {
                DetailStatusMessage = $"嫄곕옒?댁뿭? ?ㅼ쓬 ?숆린?????ㅼ떆 遺덈윭?듬땲?? / {SelectedCustomerSummaryCounts}";
            }
            else if (detailError is null && contractError is not null)
            {
                DetailStatusMessage = $"怨꾩빟?쒕뒗 罹먯떆瑜??쒖떆?⑸땲?? / {SelectedCustomerSummaryCounts}";
            }
            else
            {
                DetailStatusMessage = $"?곸꽭 議고쉶 ?ㅽ뙣: {detailError?.Message ?? contractError?.Message}";
            }
        }
        finally
        {
            IsDetailBusy = false;
        }
    }

    public void ShowSummaryTab() => SelectedDetailSection = CustomerDetailSection.Summary;
    public void ShowContractsTab() => SelectedDetailSection = CustomerDetailSection.Contracts;
    public void ShowInvoicesTab() => SelectedDetailSection = CustomerDetailSection.Invoices;
    public void ShowPaymentsTab() => SelectedDetailSection = CustomerDetailSection.Payments;

    public void ClearSelectedCustomer()
    {
        SelectedCustomer = null;
        SelectedDetailSection = CustomerDetailSection.Summary;
        ReplaceInvoices(Array.Empty<InvoiceDto>());
        ReplaceContracts(Array.Empty<CustomerContractDto>());
        ReplacePayments(Array.Empty<CustomerPaymentHistoryRow>());
        IsDetailBusy = false;
        DetailStatusMessage = "嫄곕옒泥섎? ?좏깮?섎㈃ ?곸꽭 ?뺣낫媛 ?쒖떆?⑸땲??";
    }

    public void ClearSearch()
        => SearchText = string.Empty;

    public async Task OpenContractAsync(CustomerContractDto? contract)
    {
        if (contract is null || SelectedCustomer is null)
        {
            DetailStatusMessage = "??怨꾩빟?쒕? ?좏깮?섏꽭??";
            return;
        }

        try
        {
            var path = await _cacheStore.EnsureCachedPdfAsync(SelectedCustomer.Id, contract);
            if (string.IsNullOrWhiteSpace(path))
            {
                DetailStatusMessage = "怨꾩빟??PDF 罹먯떆媛 ?놁뒿?덈떎. ?덈줈怨좎묠 ???ㅼ떆 ?쒕룄?섏꽭??";
                return;
            }

            await Launcher.Default.OpenAsync(new OpenFileRequest(contract.FileName, new ReadOnlyFile(path)));
            DetailStatusMessage = $"怨꾩빟???닿린 ?꾨즺: {contract.FileName}";
        }
        catch (Exception ex)
        {
            DetailStatusMessage = $"怨꾩빟???닿린 ?ㅽ뙣: {ex.Message}";
        }
    }

    private void ReplaceCustomers(IEnumerable<CustomerDto> customers)
    {
        Customers.Clear();
        foreach (var customer in customers)
            Customers.Add(customer);
    }

    private void ReplaceInvoices(IEnumerable<InvoiceDto> invoices)
    {
        SelectedCustomerInvoices.Clear();
        foreach (var invoice in invoices)
            SelectedCustomerInvoices.Add(invoice);

        OnPropertyChanged(nameof(SelectedCustomerSummaryCounts));
        OnPropertyChanged(nameof(InvoicesSectionHeight));
    }

    private void ReplaceContracts(IEnumerable<CustomerContractDto> contracts)
    {
        SelectedCustomerContracts.Clear();
        foreach (var contract in contracts)
            SelectedCustomerContracts.Add(contract);

        OnPropertyChanged(nameof(SelectedCustomerSummaryCounts));
        OnPropertyChanged(nameof(ContractsSectionHeight));
    }

    private void ReplacePayments(IEnumerable<CustomerPaymentHistoryRow> payments)
    {
        SelectedCustomerPayments.Clear();
        foreach (var payment in payments)
            SelectedCustomerPayments.Add(payment);

        OnPropertyChanged(nameof(SelectedCustomerSummaryCounts));
        OnPropertyChanged(nameof(PaymentsSectionHeight));
    }

    private static double CalculateListHeight(int count, double rowHeight, double emptyHeight, int maxVisibleRows)
    {
        if (count <= 0)
            return emptyHeight;

        var visibleRows = Math.Min(count, maxVisibleRows);
        return (visibleRows * rowHeight) + Math.Max(0, visibleRows - 1) * 6;
    }

    private static IReadOnlyList<CustomerPaymentHistoryRow> BuildPaymentRows(IEnumerable<InvoiceDto> invoices)
        => invoices
            .SelectMany(invoice => (invoice.Payments ?? Enumerable.Empty<PaymentDto>()).Select(payment => CustomerPaymentHistoryRow.From(invoice, payment)))
            .OrderByDescending(payment => payment.PaymentDate)
            .ThenByDescending(payment => payment.UpdatedAtUtc)
            .ToList();

    private async Task<List<CustomerDto>> LoadCachedCustomersAsync()
    {
        var cached = (await _cacheStore.LoadCustomersAsync()).ToList();
        var searchText = SearchText?.Trim();
        if (string.IsNullOrWhiteSpace(searchText))
            return cached;

        return cached
            .Where(customer =>
                (customer.NameOriginal?.Contains(searchText, StringComparison.CurrentCultureIgnoreCase) ?? false) ||
                (customer.Phone?.Contains(searchText, StringComparison.CurrentCultureIgnoreCase) ?? false) ||
                (customer.BusinessNumber?.Contains(searchText, StringComparison.CurrentCultureIgnoreCase) ?? false))
            .ToList();
    }
}
