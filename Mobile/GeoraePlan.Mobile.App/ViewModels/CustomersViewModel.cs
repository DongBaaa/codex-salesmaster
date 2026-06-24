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
    Payments = 3,
    Rentals = 4
}

public sealed class CustomersViewModel : ObservableObject
{
    private readonly GeoraePlanApiClient _api;
    private readonly CustomerContractCacheStore _cacheStore;
    private readonly SyncCoordinator _syncCoordinator;
    private readonly SessionStore _sessionStore;

    private string _searchText = string.Empty;
    private string _statusMessage = "거래처를 불러오세요.";
    private bool _isBusy;
    private DateTime? _lastRefreshUtc;
    private CustomerDto? _selectedCustomer;
    private bool _isDetailBusy;
    private string _detailStatusMessage = "거래처를 선택하면 상세 정보가 표시됩니다.";
    private CustomerDetailSection _selectedDetailSection = CustomerDetailSection.Summary;

    public CustomersViewModel(
        GeoraePlanApiClient api,
        CustomerContractCacheStore cacheStore,
        SyncCoordinator syncCoordinator,
        SessionStore sessionStore)
    {
        _api = api;
        _cacheStore = cacheStore;
        _syncCoordinator = syncCoordinator;
        _sessionStore = sessionStore;
        RefreshCommand = new AsyncCommand(RefreshAsync);
    }

    public ObservableCollection<CustomerDto> Customers { get; } = new();
    public ObservableCollection<InvoiceDto> SelectedCustomerInvoices { get; } = new();
    public ObservableCollection<CustomerContractDto> SelectedCustomerContracts { get; } = new();
    public ObservableCollection<CustomerPaymentHistoryRow> SelectedCustomerPayments { get; } = new();
    public ObservableCollection<CustomerRentalLinkRow> SelectedCustomerRentals { get; } = new();

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
            if (!SetProperty(ref _selectedCustomer, value))
                return;

            OnPropertyChanged(nameof(SelectedCustomerName));
            OnPropertyChanged(nameof(SelectedCustomerPhone));
            OnPropertyChanged(nameof(SelectedCustomerNotes));
            OnPropertyChanged(nameof(HasSelectedCustomer));
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
            if (!SetProperty(ref _selectedDetailSection, value))
                return;

            OnPropertyChanged(nameof(ShowSummarySection));
            OnPropertyChanged(nameof(ShowContractsSection));
            OnPropertyChanged(nameof(ShowInvoicesSection));
            OnPropertyChanged(nameof(ShowPaymentsSection));
            OnPropertyChanged(nameof(ShowRentalsSection));
        }
    }

    public bool HasSelectedCustomer => SelectedCustomer is not null;
    public bool HasSearchText => !string.IsNullOrWhiteSpace(SearchText);
    public string SelectedCustomerName => SelectedCustomer?.NameOriginal ?? string.Empty;
    public string SelectedCustomerPhone => string.IsNullOrWhiteSpace(SelectedCustomer?.Phone) ? "등록된 전화번호 없음" : SelectedCustomer!.Phone;
    public string SelectedCustomerNotes => string.IsNullOrWhiteSpace(SelectedCustomer?.Notes) ? "등록된 메모 없음" : SelectedCustomer!.Notes;
    public string SelectedCustomerSummaryCounts => $"계약 {SelectedCustomerContracts.Count:N0}건 · 거래내역 {SelectedCustomerInvoices.Count:N0}건 · 수금/지급 {SelectedCustomerPayments.Count:N0}건 · 렌탈 {SelectedCustomerRentals.Count:N0}건";
    public bool ShowSummarySection => SelectedDetailSection == CustomerDetailSection.Summary;
    public bool ShowContractsSection => SelectedDetailSection == CustomerDetailSection.Contracts;
    public bool ShowInvoicesSection => SelectedDetailSection == CustomerDetailSection.Invoices;
    public bool ShowPaymentsSection => SelectedDetailSection == CustomerDetailSection.Payments;
    public bool ShowRentalsSection => SelectedDetailSection == CustomerDetailSection.Rentals;
    public double ContractsSectionHeight => CalculateListHeight(SelectedCustomerContracts.Count, 64, 42, 2);
    public double InvoicesSectionHeight => CalculateListHeight(SelectedCustomerInvoices.Count, 78, 42, 2);
    public double PaymentsSectionHeight => CalculateListHeight(SelectedCustomerPayments.Count, 96, 42, 2);
    public double RentalsSectionHeight => CalculateListHeight(SelectedCustomerRentals.Count, 104, 42, 3);

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
            StatusMessage = "거래처를 조회하고 있습니다.";
            await _syncCoordinator.RefreshIfServerChangedAsync("customers-refresh", TimeSpan.FromSeconds(5));

            var result = await _api.GetCustomersAsync(SearchText);
            ReplaceCustomers(result);

            if (string.IsNullOrWhiteSpace(SearchText))
                await _cacheStore.SaveCustomersAsync(result);

            _lastRefreshUtc = DateTime.UtcNow;
            StatusMessage = $"거래처 {Customers.Count:N0}건";

            if (SelectedCustomer is null)
                return;

            var updatedSelection = Customers.FirstOrDefault(customer => customer.Id == SelectedCustomer.Id);
            if (updatedSelection is not null)
                await SelectCustomerAsync(updatedSelection);
            else
                ClearSelectedCustomer();
        }
        catch (Exception ex)
        {
            if (MobileRetryableNetworkFailure.IsRetryable(ex))
            {
                var cached = await LoadCachedCustomersAsync();
                if (cached.Count > 0)
                {
                    ReplaceCustomers(cached);
                    _lastRefreshUtc = DateTime.UtcNow;
                    StatusMessage = $"서버 연결 실패: {ex.Message} / 캐시 {Customers.Count:N0}건 표시";
                    return;
                }
            }

            Customers.Clear();
            ClearSelectedCustomer();
            StatusMessage = $"거래처 조회 실패: {ex.Message}";
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

        if (!MobileSessionScopeFilter.CanAccessCustomer(_sessionStore.GetSnapshot(), customer))
        {
            ClearSelectedCustomer();
            StatusMessage = "선택한 거래처는 현재 로그인 담당지점/업체 범위 밖입니다.";
            DetailStatusMessage = StatusMessage;
            return;
        }

        SelectedCustomer = customer;
        SelectedDetailSection = CustomerDetailSection.Summary;
        ReplaceInvoices(Array.Empty<InvoiceDto>());
        ReplaceContracts(Array.Empty<CustomerContractDto>());
        ReplacePayments(Array.Empty<CustomerPaymentHistoryRow>());
        ReplaceRentals(Array.Empty<CustomerRentalLinkRow>());
        IsDetailBusy = true;
        DetailStatusMessage = "거래처 상세 정보를 불러오고 있습니다.";

        Exception? detailError = null;
        Exception? contractError = null;
        Exception? rentalError = null;
        CustomerDetailDto? detail = null;
        IReadOnlyList<CustomerContractDto> contracts = Array.Empty<CustomerContractDto>();
        IReadOnlyList<CustomerRentalLinkRow> rentals = Array.Empty<CustomerRentalLinkRow>();
        MobileSyncState? syncState = null;

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

            if (detailError is not null && !MobileRetryableNetworkFailure.IsRetryable(detailError))
            {
                ClearSelectedCustomer();
                DetailStatusMessage = $"거래처 상세를 사용할 수 없습니다. 삭제되었거나 현재 권한/담당지점 범위 밖일 수 있습니다. ({detailError.Message})";
                StatusMessage = DetailStatusMessage;
                return;
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
                if (MobileRetryableNetworkFailure.IsRetryable(ex))
                    contracts = await _cacheStore.LoadContractsAsync(customer.Id);
            }

            var displayCustomer = detail?.Customer ?? customer;

            try
            {
                syncState = await _syncCoordinator.LoadAsync();
                rentals = BuildCustomerRentalRows(displayCustomer, syncState);
            }
            catch (Exception ex)
            {
                rentalError = ex;
            }

            var invoices = detail?.RecentInvoices ?? BuildCustomerInvoicesFromSyncedState(displayCustomer, syncState);
            var payments = detail is null
                ? BuildCustomerPaymentRowsFromSyncedState(displayCustomer, invoices, syncState)
                : BuildPaymentRows(detail);

            SelectedCustomer = displayCustomer;
            ReplaceInvoices(invoices);
            ReplaceContracts(contracts);
            ReplacePayments(payments);
            ReplaceRentals(rentals);

            var warnings = new List<string>();
            if (detailError is not null)
                warnings.Add(SelectedCustomerInvoices.Count > 0 || SelectedCustomerPayments.Count > 0
                    ? "거래내역은 동기화 캐시를 표시합니다."
                    : "거래내역은 다음 동기화 때 다시 불러옵니다.");
            if (contractError is not null)
                warnings.Add(MobileRetryableNetworkFailure.IsRetryable(contractError)
                    ? "계약서는 캐시를 표시합니다."
                    : "계약서는 현재 권한/삭제 상태를 확인할 수 없어 표시하지 않습니다.");
            if (rentalError is not null)
                warnings.Add("렌탈 연결은 동기화 후 다시 표시합니다.");

            if (warnings.Count == 0)
            {
                DetailStatusMessage = SelectedCustomerSummaryCounts;
            }
            else
            {
                DetailStatusMessage = $"{string.Join(" / ", warnings)} / {SelectedCustomerSummaryCounts}";
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
    public void ShowRentalsTab() => SelectedDetailSection = CustomerDetailSection.Rentals;

    public void ClearSelectedCustomer()
    {
        SelectedCustomer = null;
        SelectedDetailSection = CustomerDetailSection.Summary;
        ReplaceInvoices(Array.Empty<InvoiceDto>());
        ReplaceContracts(Array.Empty<CustomerContractDto>());
        ReplacePayments(Array.Empty<CustomerPaymentHistoryRow>());
        ReplaceRentals(Array.Empty<CustomerRentalLinkRow>());
        IsDetailBusy = false;
        DetailStatusMessage = "거래처를 선택하면 상세 정보가 표시됩니다.";
    }

    public async Task RemoveDeletedCustomerFromCurrentViewAsync(Guid customerId)
    {
        if (customerId == Guid.Empty)
            return;

        var removed = Customers.FirstOrDefault(customer => customer.Id == customerId);
        if (removed is not null)
            Customers.Remove(removed);

        var selectedWasRemoved = SelectedCustomer?.Id == customerId;
        if (selectedWasRemoved)
            ClearSelectedCustomer();

        try
        {
            var cached = (await _cacheStore.LoadCustomersAsync())
                .Where(customer => customer.Id != customerId)
                .ToList();
            await _cacheStore.SaveCustomersAsync(cached);
        }
        catch
        {
            // Cache cleanup must not block the visible offline-delete result.
        }

        if (removed is null && !selectedWasRemoved)
            return;

        StatusMessage = removed is null
            ? "삭제 대기 거래처를 현재 화면에서 숨겼습니다. 동기화 화면에서 서버 반영 상태를 확인하세요."
            : $"{removed.NameOriginal} 거래처 삭제 대기 중입니다. 동기화 화면에서 서버 반영 상태를 확인하세요.";
    }

    public bool TryNavigateBackOneStep()
    {
        if (!HasSelectedCustomer)
            return false;

        ClearSelectedCustomer();
        return true;
    }

    public void ClearSearch()
        => SearchText = string.Empty;

    public async Task OpenContractAsync(CustomerContractDto? contract)
    {
        if (contract is null || SelectedCustomer is null)
        {
            DetailStatusMessage = "계약서를 선택하세요.";
            return;
        }

        try
        {
            var path = await _cacheStore.EnsureCachedPdfAsync(SelectedCustomer.Id, contract);
            if (string.IsNullOrWhiteSpace(path))
            {
                DetailStatusMessage = "계약서 PDF 캐시가 없습니다. 네트워크 연결 후 다시 시도하세요.";
                return;
            }

            await Launcher.Default.OpenAsync(new OpenFileRequest(contract.FileName, new ReadOnlyFile(path)));
            DetailStatusMessage = $"계약서 열기 완료: {contract.FileName}";
        }
        catch (Exception ex)
        {
            DetailStatusMessage = $"계약서 열기 실패: {ex.Message}";
        }
    }

    private void ReplaceCustomers(IEnumerable<CustomerDto> customers)
    {
        var snapshot = _sessionStore.GetSnapshot();
        Customers.Clear();
        foreach (var customer in customers.Where(customer => MobileSessionScopeFilter.CanAccessCustomer(snapshot, customer)))
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

    private void ReplaceRentals(IEnumerable<CustomerRentalLinkRow> rentals)
    {
        SelectedCustomerRentals.Clear();
        foreach (var rental in rentals)
            SelectedCustomerRentals.Add(rental);

        OnPropertyChanged(nameof(SelectedCustomerSummaryCounts));
        OnPropertyChanged(nameof(RentalsSectionHeight));
    }

    private static double CalculateListHeight(int count, double rowHeight, double emptyHeight, int maxVisibleRows)
    {
        if (count <= 0)
            return emptyHeight;

        var visibleRows = Math.Min(count, maxVisibleRows);
        return (visibleRows * rowHeight) + Math.Max(0, visibleRows - 1) * 6;
    }

    private static IReadOnlyList<CustomerPaymentHistoryRow> BuildPaymentRows(CustomerDetailDto? detail)
    {
        var recentPayments = detail?.RecentPayments ?? [];
        if (recentPayments.Count > 0)
        {
            return recentPayments
                .Select(CustomerPaymentHistoryRow.From)
                .OrderByDescending(payment => payment.PaymentDate)
                .ThenByDescending(payment => payment.UpdatedAtUtc)
                .ToList();
        }

        return BuildPaymentRowsFromInvoices(detail?.RecentInvoices ?? Enumerable.Empty<InvoiceDto>());
    }

    private static IReadOnlyList<CustomerPaymentHistoryRow> BuildPaymentRowsFromInvoices(IEnumerable<InvoiceDto> invoices)
        => invoices
            .SelectMany(invoice => (invoice.Payments ?? Enumerable.Empty<PaymentDto>()).Select(payment => CustomerPaymentHistoryRow.From(invoice, payment)))
            .OrderByDescending(payment => payment.PaymentDate)
            .ThenByDescending(payment => payment.UpdatedAtUtc)
            .ToList();

    private IReadOnlyList<InvoiceDto> BuildCustomerInvoicesFromSyncedState(CustomerDto customer, MobileSyncState? state)
    {
        if (state is null || customer.Id == Guid.Empty)
            return [];

        state.Normalize();
        var snapshot = _sessionStore.GetSnapshot();
        return state.SyncedInvoices
            .Where(invoice => !invoice.IsDeleted &&
                              invoice.CustomerId == customer.Id &&
                              MobileSessionScopeFilter.CanAccessInvoice(snapshot, invoice))
            .OrderByDescending(invoice => invoice.InvoiceDate)
            .ThenByDescending(invoice => invoice.UpdatedAtUtc)
            .ThenByDescending(invoice => invoice.CreatedAtUtc)
            .Take(50)
            .ToList();
    }

    private IReadOnlyList<CustomerPaymentHistoryRow> BuildCustomerPaymentRowsFromSyncedState(
        CustomerDto customer,
        IEnumerable<InvoiceDto> fallbackInvoices,
        MobileSyncState? state)
    {
        if (state is null || customer.Id == Guid.Empty)
            return BuildPaymentRowsFromInvoices(fallbackInvoices);

        state.Normalize();
        var snapshot = _sessionStore.GetSnapshot();
        var invoiceMap = state.SyncedInvoices
            .Concat(fallbackInvoices)
            .Where(invoice => !invoice.IsDeleted &&
                              invoice.CustomerId == customer.Id &&
                              invoice.Id != Guid.Empty &&
                              MobileSessionScopeFilter.CanAccessInvoice(snapshot, invoice))
            .GroupBy(invoice => invoice.Id)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(invoice => invoice.Revision)
                    .ThenByDescending(invoice => invoice.UpdatedAtUtc)
                    .First(),
                EqualityComparer<Guid>.Default);

        if (invoiceMap.Count == 0)
            return [];

        var rows = new List<CustomerPaymentHistoryRow>();
        foreach (var invoice in invoiceMap.Values)
        {
            foreach (var payment in invoice.Payments?.Where(payment => !payment.IsDeleted) ?? Enumerable.Empty<PaymentDto>())
                rows.Add(CustomerPaymentHistoryRow.From(invoice, payment));
        }

        foreach (var payment in state.SyncedPayments.Where(payment => !payment.IsDeleted))
        {
            if (invoiceMap.TryGetValue(payment.InvoiceId, out var invoice))
                rows.Add(CustomerPaymentHistoryRow.From(invoice, payment));
        }

        return rows
            .GroupBy(row => row.PaymentId)
            .Select(group => group
                .OrderByDescending(row => row.UpdatedAtUtc)
                .ThenByDescending(row => row.PaymentDate)
                .First())
            .OrderByDescending(row => row.PaymentDate)
            .ThenByDescending(row => row.UpdatedAtUtc)
            .Take(50)
            .ToList();
    }

    private IReadOnlyList<CustomerRentalLinkRow> BuildCustomerRentalRows(CustomerDto customer, MobileSyncState state)
    {
        state.Normalize();
        var snapshot = _sessionStore.GetSnapshot();

        var profileMap = state.SyncedRentalBillingProfiles
            .Where(profile => !profile.IsDeleted &&
                              profile.Id != Guid.Empty &&
                              MobileSessionScopeFilter.CanAccessRentalBillingProfile(snapshot, profile))
            .GroupBy(profile => profile.Id)
            .ToDictionary(group => group.Key, group => group.First(), EqualityComparer<Guid>.Default);
        var assetMap = state.SyncedRentalAssets
            .Where(asset => !asset.IsDeleted &&
                            asset.Id != Guid.Empty &&
                            MobileSessionScopeFilter.CanAccessRentalAsset(snapshot, asset))
            .GroupBy(asset => asset.Id)
            .ToDictionary(group => group.Key, group => group.First(), EqualityComparer<Guid>.Default);
        var matchContext = BuildCustomerRentalMatchContext(customer, state, snapshot);

        var rows = new List<CustomerRentalLinkRow>();
        foreach (var profile in state.SyncedRentalBillingProfiles.Where(profile =>
                     !profile.IsDeleted &&
                     MobileSessionScopeFilter.CanAccessRentalBillingProfile(snapshot, profile)))
        {
            if (MatchesSelectedCustomer(matchContext, profile.CustomerId, profile.BusinessNumber, profile.CustomerName))
                rows.Add(CustomerRentalLinkRow.FromProfile(profile));
        }

        foreach (var asset in state.SyncedRentalAssets.Where(asset =>
                     !asset.IsDeleted &&
                     MobileSessionScopeFilter.CanAccessRentalAsset(snapshot, asset)))
        {
            profileMap.TryGetValue(asset.BillingProfileId ?? Guid.Empty, out var profile);
            if (MatchesSelectedCustomer(
                    matchContext,
                    asset.CustomerId,
                    profile?.BusinessNumber,
                    asset.CustomerName,
                    asset.CurrentCustomerName,
                    asset.LastCustomerName,
                    profile?.CustomerName))
            {
                rows.Add(CustomerRentalLinkRow.FromAsset(asset, profile));
            }
        }

        foreach (var history in state.SyncedRentalAssetAssignmentHistories.Where(history =>
                     !history.IsDeleted &&
                     MobileSessionScopeFilter.CanAccessRentalAssetAssignmentHistory(snapshot, history)))
        {
            profileMap.TryGetValue(history.BillingProfileId ?? Guid.Empty, out var profile);
            assetMap.TryGetValue(history.AssetId, out var asset);

            if (MatchesSelectedCustomer(
                    matchContext,
                    history.CustomerId,
                    profile?.BusinessNumber,
                    history.CustomerName,
                    profile?.CustomerName,
                    asset?.CustomerName,
                    asset?.CurrentCustomerName,
                    asset?.LastCustomerName))
            {
                rows.Add(CustomerRentalLinkRow.FromAssignmentHistory(history, profile, asset));
            }
        }

        return rows
            .GroupBy(row => row.UniqueKey, StringComparer.Ordinal)
            .Select(group => group
                .OrderBy(row => row.SourcePriority)
                .ThenByDescending(row => row.SortDate)
                .First())
            .OrderByDescending(row => row.SortDate)
            .ThenBy(row => row.SourcePriority)
            .ThenBy(row => row.Title, StringComparer.CurrentCulture)
            .ToList();
    }

    private static CustomerRentalMatchContext BuildCustomerRentalMatchContext(
        CustomerDto customer,
        MobileSyncState state,
        SessionSnapshot snapshot)
    {
        var scopedCustomers = state.SyncedCustomers
            .Where(candidate => !candidate.IsDeleted &&
                                candidate.Id != Guid.Empty &&
                                MobileSessionScopeFilter.CanAccessCustomer(snapshot, candidate))
            .GroupBy(candidate => candidate.Id)
            .Select(group => group
                .OrderByDescending(candidate => candidate.Revision)
                .ThenByDescending(candidate => candidate.UpdatedAtUtc)
                .First())
            .ToList();

        if (customer.Id != Guid.Empty && scopedCustomers.All(candidate => candidate.Id != customer.Id))
            scopedCustomers.Add(customer);

        return new CustomerRentalMatchContext
        {
            Customer = customer,
            UniqueSelectedBusinessNumberKeys = BuildUniqueSelectedBusinessNumberKeys(customer, scopedCustomers),
            UniqueSelectedNameKeys = BuildUniqueSelectedNameKeys(customer, scopedCustomers)
        };
    }

    private static HashSet<string> BuildUniqueSelectedBusinessNumberKeys(
        CustomerDto customer,
        IEnumerable<CustomerDto> scopedCustomers)
        => scopedCustomers
            .Select(candidate => (CustomerId: candidate.Id, Key: NormalizeBusinessNumber(candidate.BusinessNumber)))
            .Where(entry => entry.CustomerId != Guid.Empty && !string.IsNullOrWhiteSpace(entry.Key))
            .GroupBy(entry => entry.Key, StringComparer.Ordinal)
            .Where(group =>
            {
                var customerIds = group.Select(entry => entry.CustomerId).Distinct().ToList();
                return customerIds.Count == 1 && customerIds[0] == customer.Id;
            })
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> BuildUniqueSelectedNameKeys(
        CustomerDto customer,
        IEnumerable<CustomerDto> scopedCustomers)
        => scopedCustomers
            .SelectMany(candidate => EnumerateCustomerNameKeys(candidate)
                .Select(key => (CustomerId: candidate.Id, Key: key)))
            .Where(entry => entry.CustomerId != Guid.Empty && !string.IsNullOrWhiteSpace(entry.Key))
            .GroupBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Where(group =>
            {
                var customerIds = group.Select(entry => entry.CustomerId).Distinct().ToList();
                return customerIds.Count == 1 && customerIds[0] == customer.Id;
            })
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> EnumerateCustomerNameKeys(CustomerDto customer)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddCustomerNameKey(keys, customer.NameOriginal);
        AddCustomerNameKey(keys, customer.NameMatchKey);
        return keys;
    }

    private static bool MatchesSelectedCustomer(
        CustomerRentalMatchContext context,
        Guid? candidateCustomerId,
        string? candidateBusinessNumber,
        params string?[] candidateNames)
    {
        var customer = context.Customer;
        if (candidateCustomerId.HasValue && candidateCustomerId.Value != Guid.Empty)
            return customer.Id != Guid.Empty && candidateCustomerId.Value == customer.Id;

        var candidateBusinessNumberKey = NormalizeBusinessNumber(candidateBusinessNumber);
        if (!string.IsNullOrWhiteSpace(candidateBusinessNumberKey) &&
            context.UniqueSelectedBusinessNumberKeys.Contains(candidateBusinessNumberKey))
            return true;

        return candidateNames.Any(name =>
        {
            var key = NormalizeNameKey(name);
            return !string.IsNullOrWhiteSpace(key) &&
                   context.UniqueSelectedNameKeys.Contains(key);
        });
    }

    private sealed class CustomerRentalMatchContext
    {
        public required CustomerDto Customer { get; init; }
        public HashSet<string> UniqueSelectedBusinessNumberKeys { get; init; } = new(StringComparer.Ordinal);
        public HashSet<string> UniqueSelectedNameKeys { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static void AddCustomerNameKey(HashSet<string> keys, string? value)
    {
        var key = NormalizeNameKey(value);
        if (!string.IsNullOrWhiteSpace(key))
            keys.Add(key);
    }

    private static string NormalizeBusinessNumber(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value.Where(char.IsDigit).ToArray());

    private static string NormalizeNameKey(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : string.Concat(value.Trim().Where(ch => !char.IsWhiteSpace(ch)));

    private async Task<List<CustomerDto>> LoadCachedCustomersAsync()
    {
        var snapshot = _sessionStore.GetSnapshot();
        var cached = (await _cacheStore.LoadCustomersAsync()).ToList();
        cached = cached
            .Where(customer => MobileSessionScopeFilter.CanAccessCustomer(snapshot, customer))
            .ToList();
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

public sealed class CustomerRentalLinkRow
{
    public string UniqueKey { get; init; } = string.Empty;
    public string SourceLabel { get; init; } = string.Empty;
    public int SourcePriority { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public string Meta { get; init; } = string.Empty;
    public string Note { get; init; } = string.Empty;
    public DateTime SortDate { get; init; }

    public static CustomerRentalLinkRow FromProfile(RentalBillingProfileDto profile)
    {
        var sortDate = ToSortDate(profile.LastBilledDate, profile.UpdatedAtUtc);
        var itemName = FirstText(profile.ItemName, "품목 미지정");
        var profileKey = FirstText(profile.ProfileKey, "프로필 미지정");
        var status = FirstText(profile.BillingStatus, profile.CompletionStatus, "상태 미지정");

        return new CustomerRentalLinkRow
        {
            UniqueKey = $"profile:{profile.Id:D}",
            SourceLabel = "청구프로필",
            SourcePriority = 1,
            Title = $"청구프로필 · {profileKey}",
            Subtitle = $"{itemName} · {FirstText(profile.BillingType, "청구유형 미지정")} · {status}",
            Meta = $"월 {profile.MonthlyAmount:N0}원 / 미수 {profile.OutstandingAmount:N0}원 / 지점 {ResolveOffice(profile.ResponsibleOfficeCode, profile.OfficeCode)}",
            Note = $"계약 {FormatDate(profile.ContractStartDate)}~{FormatDate(profile.ContractEndDate)} / 최종청구 {FormatDate(profile.LastBilledDate)} / {FirstText(profile.InstallSiteName, "설치지 미지정")}",
            SortDate = sortDate
        };
    }

    public static CustomerRentalLinkRow FromAsset(RentalAssetDto asset, RentalBillingProfileDto? profile)
    {
        var sortDate = ToSortDate(asset.InstallDate ?? asset.ContractStartDate ?? asset.PurchaseDate, asset.UpdatedAtUtc);
        var managementNumber = FirstText(asset.ManagementNumber, asset.ManagementId, asset.AssetKey, "관리번호 미지정");
        var machineNumber = FirstText(asset.MachineNumber, "기계번호 미지정");
        var itemName = FirstText(asset.ItemName, profile?.ItemName, "품목 미지정");
        var status = FirstText(asset.AssetStatus, asset.BillingEligibilityStatus, "상태 미지정");
        var location = FirstText(asset.InstallLocation, asset.CurrentLocation, asset.LastInstallLocation, "위치 미지정");

        return new CustomerRentalLinkRow
        {
            UniqueKey = $"asset:{asset.Id:D}",
            SourceLabel = "렌탈자산",
            SourcePriority = 2,
            Title = $"렌탈자산 · {itemName}",
            Subtitle = $"관리번호 {managementNumber} / 기계번호 {machineNumber}",
            Meta = $"{status} / 월 {asset.MonthlyFee:N0}원 / 지점 {ResolveOffice(asset.ResponsibleOfficeCode, asset.OfficeCode)}",
            Note = $"청구프로필 {FirstText(profile?.ProfileKey, asset.LastBillingProfileDisplay, "미연결")} / 설치 {FormatDate(asset.InstallDate)} / {location}",
            SortDate = sortDate
        };
    }

    public static CustomerRentalLinkRow FromAssignmentHistory(
        RentalAssetAssignmentHistoryDto history,
        RentalBillingProfileDto? profile,
        RentalAssetDto? asset)
    {
        var itemName = FirstText(history.ItemName, profile?.ItemName, asset?.ItemName, "품목 미지정");
        var managementNumber = FirstText(history.ManagementNumber, asset?.ManagementNumber, asset?.AssetKey, "관리번호 미지정");
        var machineNumber = FirstText(history.MachineNumber, asset?.MachineNumber, "기계번호 미지정");
        var profileKey = FirstText(history.BillingProfileDisplay, profile?.ProfileKey, asset?.LastBillingProfileDisplay, "프로필 미지정");
        var location = FirstText(history.InstallLocation, asset?.InstallLocation, asset?.CurrentLocation, "설치 위치 미지정");
        var currentLabel = history.IsCurrent ? "현재" : "과거";
        var sortDate = history.LinkedAtUtc == default ? DateTime.MinValue : history.LinkedAtUtc;

        return new CustomerRentalLinkRow
        {
            UniqueKey = $"assignment:{history.Id:D}",
            SourceLabel = "설치이력",
            SourcePriority = 3,
            Title = $"설치이력 · {currentLabel} · {itemName}",
            Subtitle = $"관리번호 {managementNumber} / 기계번호 {machineNumber}",
            Meta = $"연결 {FormatDateTime(history.LinkedAtUtc)} / 해제 {FormatDateTime(history.UnlinkedAtUtc)} / 월 {history.MonthlyFee:N0}원",
            Note = $"{FirstText(history.ChangeReason, "변경사유 미기재")} / {profileKey} / {location}",
            SortDate = sortDate
        };
    }

    private static DateTime ToSortDate(DateOnly? date, DateTime fallback)
        => date.HasValue
            ? date.Value.ToDateTime(TimeOnly.MinValue)
            : fallback == default
                ? DateTime.MinValue
                : fallback;

    private static string ResolveOffice(string? responsibleOfficeCode, string? ownerOfficeCode)
        => !string.IsNullOrWhiteSpace(responsibleOfficeCode)
            ? responsibleOfficeCode.Trim()
            : FirstText(ownerOfficeCode, "미지정");

    private static string FirstText(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string FormatDate(DateOnly? value)
        => value.HasValue ? value.Value.ToString("yyyy-MM-dd") : "미정";

    private static string FormatDateTime(DateTime? value)
    {
        if (!value.HasValue || value.Value == default)
            return "미정";

        var dateTime = value.Value.Kind == DateTimeKind.Utc
            ? value.Value.ToLocalTime()
            : value.Value;
        return dateTime.ToString("yyyy-MM-dd HH:mm");
    }
}
