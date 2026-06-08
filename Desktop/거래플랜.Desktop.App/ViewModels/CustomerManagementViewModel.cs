using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class CustomerManagementViewModel : ObservableObject
{
    private const string AllCategoriesOption = "전체";
    private const string AllOfficesOption = "전체";
    private const string UnassignedOfficeOption = "미지정";
    private const string SortByNameOption = "거래처명순";
    private const string SortByOfficeOption = "담당지점별 정렬";
    private const int ContractAlertWindowDays = 30;

    private readonly LocalStateService _local;
    private readonly SessionState _session;
    private readonly UiDebouncer _filterDebouncer = new();
    private readonly SemaphoreSlim _officeSaveLock = new(1, 1);
    private readonly List<EnvironmentCustomerRow> _allRows = new();
    private Dictionary<Guid, string> _categoryNames = new();
    private Dictionary<Guid, CustomerContractSummaryItem> _contractSummaryMap = new();

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _selectedCategoryFilter = AllCategoriesOption;
    [ObservableProperty] private string _selectedOfficeFilter = AllOfficesOption;
    [ObservableProperty] private string _selectedSortOption = SortByNameOption;
    [ObservableProperty] private EnvironmentCustomerRow? _selectedCustomer;
    [ObservableProperty] private string _statusMessage = "거래처 등록, 수정, 담당지점 변경을 관리합니다.";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private int _customersWithContractsCount;
    [ObservableProperty] private int _expiredContractCount;
    [ObservableProperty] private int _expiringSoonContractCount;
    [ObservableProperty] private string _contractAlertSummary = "계약서 알림이 없습니다.";

    public ObservableCollection<EnvironmentCustomerRow> Customers { get; } = new();
    public ObservableCollection<CustomerContractAlertItem> ContractAlerts { get; } = new();
    public ObservableCollection<string> OfficeCodes { get; } = new();
    public ObservableCollection<string> OfficeFilters { get; } = new();
    public ObservableCollection<string> CategoryFilters { get; } = new();
    public ObservableCollection<string> SortOptions { get; } = new();
    public bool CanEditAllResponsibleOffices => _session.HasAdministrativePrivileges;
    public bool HasContractAlerts => ContractAlerts.Count > 0;

    public CustomerManagementViewModel(LocalStateService local, SessionState session)
    {
        _local = local;
        _session = session;
        _selectedOfficeFilter = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(_session.OfficeCode, DomainConstants.OfficeUsenet);
        SortOptions.Add(SortByNameOption);
        SortOptions.Add(SortByOfficeOption);
    }

    public async Task InitializeAsync()
    {
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        IsBusy = true;
        try
        {
            await _local.EnsureCustomerCategoryIntegrityAsync();
            await ReloadOfficeCodesAsync();
            await ReloadCategoryFiltersAsync();

            var customers = _session.HasGlobalDataScope
                ? await _local.GetCustomersAsync()
                : await _local.GetCustomersAsync(_session);
            _contractSummaryMap = await _local.GetCustomerContractSummaryMapAsync(_session, ContractAlertWindowDays);
            var alertItems = await _local.GetCustomerContractAlertsAsync(_session, ContractAlertWindowDays);

            DetachRowHandlers();
            _allRows.Clear();
            _allRows.AddRange(customers.Select(customer => new EnvironmentCustomerRow(
                customer,
                ResolveCategoryName(customer.CategoryId, customer.TradeType),
                ResolveContractSummary(customer.Id))));
            AttachRowHandlers();
            ReloadOfficeFilters();
            RefreshContractAlertState(alertItems);
            ApplyFilter();
            StatusMessage = BuildStatusMessage();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveOfficeChangesAsync()
    {
        var changed = _allRows
            .Where(row => row.IsModified)
            .DistinctBy(row => row.Id)
            .ToList();
        if (changed.Count == 0)
        {
            StatusMessage = "변경한 담당지점이 없습니다.";
            return;
        }

        await SaveOfficeChangesCoreAsync(changed, false);
    }

    public Task SaveOfficeChangeAsync(EnvironmentCustomerRow row)
    {
        if (row is null)
            return Task.CompletedTask;

        return SaveOfficeChangesCoreAsync([row], true);
    }

    private async Task SaveOfficeChangesCoreAsync(
        IReadOnlyList<EnvironmentCustomerRow> rows,
        bool immediate)
    {
        var targets = rows
            .Where(row => row is not null)
            .DistinctBy(row => row.Id)
            .ToList();
        if (targets.Count == 0)
            return;

        await _officeSaveLock.WaitAsync();
        try
        {
            var pending = targets.Where(row => row.IsModified).ToList();
            if (pending.Count == 0)
            {
                if (!immediate)
                    StatusMessage = "변경한 담당지점이 없습니다.";
                return;
            }

            IsBusy = true;
            var grantedTemporaryAccess = false;
            var savedCount = 0;

            foreach (var row in pending)
            {
                row.ApplyToSource();
                var result = await _local.UpsertCustomerAsync(row.Source, _session);
                if (!result.Success)
                {
                    row.RestoreSavedOfficeCode();
                    StatusMessage = result.ConcurrencyConflict
                        ? $"{result.Message} 담당지점 선택은 이전 값으로 되돌렸습니다."
                        : result.Message;
                    return;
                }

                grantedTemporaryAccess |= result.GrantedTemporaryAccess;
                row.AcceptChanges();
                savedCount++;
            }

            ReloadOfficeFilters();
            ApplyFilter();

            var baseStatusMessage = immediate
                ? _session.HasAdministrativePrivileges
                    ? $"거래처 '{pending[0].NameOriginal}' 담당지점을 바로 저장했습니다."
                    : grantedTemporaryAccess
                        ? "거래처 담당지점을 저장했습니다. USENET 거래처는 당일만 계속 작업할 수 있습니다."
                        : $"거래처 '{pending[0].NameOriginal}' 담당지점을 바로 저장했습니다."
                : _session.HasAdministrativePrivileges
                    ? $"담당지점 변경 {savedCount:N0}건을 저장했습니다."
                    : grantedTemporaryAccess
                        ? "거래처를 저장했습니다. USENET 거래처는 당일만 계속 작업할 수 있습니다."
                        : $"담당지점 변경 {savedCount:N0}건을 저장했습니다.";

            var serverWriteResult = await _local.WaitForServerWriteWithTimeoutAsync(TimeSpan.FromSeconds(3));
            StatusMessage = LocalStateService.ComposeServerWriteStatusMessage(baseStatusMessage, serverWriteResult);
        }
        finally
        {
            IsBusy = false;
            _officeSaveLock.Release();
        }
    }

    [RelayCommand]
    private void Search()
    {
        ApplyFilter();
    }

    private async Task ReloadOfficeCodesAsync()
    {
        OfficeCodes.Clear();
        var offices = await _local.GetOfficesAsync();
        var readableOfficeCodes = _local.GetReadableOfficeCodesForSession(_session)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var officeCode in offices
                     .Select(office => office.Code)
                     .Where(code => readableOfficeCodes.Contains(code))
                     .Where(code => !string.IsNullOrWhiteSpace(code))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(code => code, StringComparer.OrdinalIgnoreCase))
        {
            OfficeCodes.Add(officeCode);
        }

        if (OfficeCodes.Count == 0)
        {
            foreach (var officeCode in _local.GetReadableOfficeCodesForSession(_session))
                OfficeCodes.Add(officeCode);
        }

        if (OfficeCodes.Count == 0)
        {
            OfficeCodes.Add(OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(_session.OfficeCode, DomainConstants.OfficeUsenet));
        }

        var orderedOfficeCodes = OfficeCodes
            .Select(officeCode => OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode, DomainConstants.OfficeUsenet))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(GetOfficeSortOrder)
            .ThenBy(officeCode => officeCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        OfficeCodes.Clear();
        foreach (var officeCode in orderedOfficeCodes)
            OfficeCodes.Add(officeCode);
    }

    private void ReloadOfficeFilters()
    {
        var selectedFilter = SelectedOfficeFilter;
        var filterItems = OfficeCodes
            .Select(officeCode => OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode, DomainConstants.OfficeUsenet))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(GetOfficeSortOrder)
            .ThenBy(officeCode => officeCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        OfficeFilters.Clear();
        OfficeFilters.Add(AllOfficesOption);
        foreach (var officeCode in filterItems)
            OfficeFilters.Add(officeCode);

        if (_allRows.Any(row => string.IsNullOrWhiteSpace(GetActiveOfficeCode(row))))
            OfficeFilters.Add(UnassignedOfficeOption);

        if (!OfficeFilters.Contains(selectedFilter, StringComparer.CurrentCultureIgnoreCase))
            selectedFilter = AllOfficesOption;

        SelectedOfficeFilter = selectedFilter;
    }

    private async Task ReloadCategoryFiltersAsync()
    {
        var categories = await _local.GetCategoriesAsync();

        _categoryNames = categories
            .Where(category => !string.IsNullOrWhiteSpace(category.Name))
            .GroupBy(category => category.Id)
            .ToDictionary(group => group.Key, group => group.First().Name);

        var filterItems = categories
            .Select(category => category.Name?.Trim() ?? string.Empty)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        CategoryFilters.Clear();
        CategoryFilters.Add(AllCategoriesOption);
        foreach (var item in filterItems)
            CategoryFilters.Add(item);

        if (!CategoryFilters.Contains(SelectedCategoryFilter, StringComparer.CurrentCultureIgnoreCase))
            SelectedCategoryFilter = AllCategoriesOption;
    }

    private void ApplyFilter()
    {
        var keyword = (SearchText ?? string.Empty).Trim();
        IEnumerable<EnvironmentCustomerRow> filtered = _allRows;

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            filtered = filtered.Where(row =>
                row.NameOriginal.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                row.BusinessNumber.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                row.CategoryName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                row.Phone.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                row.ContractStatusText.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                GetOfficeDisplayText(GetActiveOfficeCode(row)).Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.Equals(SelectedCategoryFilter, AllCategoriesOption, StringComparison.CurrentCultureIgnoreCase))
        {
            filtered = filtered.Where(row =>
                string.Equals(row.CategoryName, SelectedCategoryFilter, StringComparison.CurrentCultureIgnoreCase));
        }

        if (!string.Equals(SelectedOfficeFilter, AllOfficesOption, StringComparison.CurrentCultureIgnoreCase))
            filtered = filtered.Where(MatchesSelectedOfficeFilter);

        var list = ApplySorting(filtered).ToList();

        Customers.Clear();
        foreach (var row in list)
            Customers.Add(row);

        if (SelectedCustomer is not null && Customers.All(row => row.Id != SelectedCustomer.Id))
            SelectedCustomer = null;

        if (SelectedCustomer is null)
            SelectedCustomer = Customers.FirstOrDefault();
    }

    partial void OnSearchTextChanged(string value)
    {
        _filterDebouncer.Debounce(TimeSpan.FromMilliseconds(300), ApplyFilter);
    }

    partial void OnSelectedCategoryFilterChanged(string value)
    {
        _filterDebouncer.Debounce(TimeSpan.FromMilliseconds(200), ApplyFilter);
    }

    partial void OnSelectedOfficeFilterChanged(string value)
    {
        _filterDebouncer.Debounce(TimeSpan.FromMilliseconds(200), ApplyFilter);
    }

    partial void OnSelectedSortOptionChanged(string value)
    {
        _filterDebouncer.Debounce(TimeSpan.FromMilliseconds(150), ApplyFilter);
    }

    private string ResolveCategoryName(Guid? categoryId, string? tradeType)
    {
        if (!categoryId.HasValue || categoryId == Guid.Empty)
        {
            if (CustomerClassificationNormalizer.TryExtractCompositeCategoryAndTradeType(tradeType, out var inferredCategory, out _))
                return inferredCategory.Name;

            if (CustomerClassificationNormalizer.TryResolveCategory(tradeType, out inferredCategory))
                return inferredCategory.Name;

            return "-";
        }

        return _categoryNames.TryGetValue(categoryId.Value, out var categoryName) &&
               !string.IsNullOrWhiteSpace(categoryName)
            ? categoryName
            : "-";
    }

    private CustomerContractSummaryItem? ResolveContractSummary(Guid customerId)
        => _contractSummaryMap.TryGetValue(customerId, out var summary)
            ? summary
            : null;

    private void RefreshContractAlertState(IReadOnlyList<CustomerContractAlertItem> alertItems)
    {
        ContractAlerts.Clear();
        foreach (var item in alertItems)
            ContractAlerts.Add(item);

        OnPropertyChanged(nameof(HasContractAlerts));

        CustomersWithContractsCount = _contractSummaryMap.Values.Count(summary => summary.ContractCount > 0);
        ExpiredContractCount = _contractSummaryMap.Values.Count(summary => summary.HasExpiredContract);
        ExpiringSoonContractCount = _contractSummaryMap.Values.Count(summary => summary.ExpiringSoonCount > 0);
        ContractAlertSummary = ContractAlerts.Count == 0
            ? "계약서 만료 알림이 없습니다."
            : $"계약서 보유 거래처 {CustomersWithContractsCount:N0}곳 / 만료 계약 {ExpiredContractCount:N0}곳 / {ContractAlertWindowDays}일 내 만료 예정 {ExpiringSoonContractCount:N0}곳";
    }

    private string BuildStatusMessage()
    {
        var baseText = $"거래처 {_allRows.Count:N0}건을 불러왔습니다.";
        return ContractAlerts.Count == 0
            ? $"{baseText} 계약서 알림은 없습니다."
            : $"{baseText} 만료 계약 {ExpiredContractCount:N0}곳, {ContractAlertWindowDays}일 내 만료 예정 {ExpiringSoonContractCount:N0}곳입니다.";
    }

    private IEnumerable<EnvironmentCustomerRow> ApplySorting(IEnumerable<EnvironmentCustomerRow> rows)
    {
        if (string.Equals(SelectedSortOption, SortByOfficeOption, StringComparison.CurrentCultureIgnoreCase))
        {
            return rows
                .OrderBy(row => GetOfficeSortOrder(GetActiveOfficeCode(row)))
                .ThenBy(row => GetOfficeDisplayText(GetActiveOfficeCode(row)), StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(row => row.NameOriginal, StringComparer.CurrentCultureIgnoreCase);
        }

        return rows
            .OrderBy(row => row.NameOriginal, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(row => GetOfficeSortOrder(GetActiveOfficeCode(row)))
            .ThenBy(row => GetOfficeDisplayText(GetActiveOfficeCode(row)), StringComparer.CurrentCultureIgnoreCase);
    }

    private void AttachRowHandlers()
    {
        foreach (var row in _allRows)
            row.PropertyChanged += CustomerRow_PropertyChanged;
    }

    private void DetachRowHandlers()
    {
        foreach (var row in _allRows)
            row.PropertyChanged -= CustomerRow_PropertyChanged;
    }

    private void CustomerRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.PropertyName) &&
            !string.Equals(e.PropertyName, nameof(EnvironmentCustomerRow.ResponsibleOfficeCode), StringComparison.Ordinal))
            return;

        ReloadOfficeFilters();
        ApplyFilter();
    }

    private bool MatchesSelectedOfficeFilter(EnvironmentCustomerRow row)
    {
        if (string.Equals(SelectedOfficeFilter, UnassignedOfficeOption, StringComparison.CurrentCultureIgnoreCase))
            return string.IsNullOrWhiteSpace(GetActiveOfficeCode(row));

        var selectedOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(SelectedOfficeFilter, DomainConstants.OfficeUsenet);
        return string.Equals(GetActiveOfficeCode(row), selectedOfficeCode, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetActiveOfficeCode(EnvironmentCustomerRow row)
    {
        var currentOfficeCode = row.IsModified ? row.ResponsibleOfficeCode : row.Source.ResponsibleOfficeCode;
        if (OfficeCodeCatalog.TryNormalizeOfficeCode(currentOfficeCode, out var normalizedOfficeCode))
            return normalizedOfficeCode;

        return string.IsNullOrWhiteSpace(currentOfficeCode)
            ? string.Empty
            : currentOfficeCode.Trim();
    }

    private static string GetOfficeDisplayText(string? officeCode)
        => string.IsNullOrWhiteSpace(officeCode)
            ? UnassignedOfficeOption
            : OfficeCodeCatalog.GetOfficeDisplayName(OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode, DomainConstants.OfficeUsenet));

    private static int GetOfficeSortOrder(string? officeCode)
    {
        if (string.IsNullOrWhiteSpace(officeCode))
            return 99;

        return OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode, DomainConstants.OfficeUsenet) switch
        {
            var value when string.Equals(value, DomainConstants.OfficeUsenet, StringComparison.OrdinalIgnoreCase) => 0,
            var value when string.Equals(value, DomainConstants.OfficeItworld, StringComparison.OrdinalIgnoreCase) => 1,
            var value when string.Equals(value, DomainConstants.OfficeYeonsu, StringComparison.OrdinalIgnoreCase) => 2,
            _ => 99
        };
    }
}
