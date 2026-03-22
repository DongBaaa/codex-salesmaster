using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class CustomerManagementViewModel : ObservableObject
{
    private const string AllCategoriesOption = "전체";
    private const int ContractAlertWindowDays = 30;

    private readonly LocalStateService _local;
    private readonly SessionState _session;
    private readonly List<EnvironmentCustomerRow> _allRows = new();
    private Dictionary<Guid, string> _categoryNames = new();
    private Dictionary<Guid, CustomerContractSummaryItem> _contractSummaryMap = new();

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _selectedCategoryFilter = AllCategoriesOption;
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
    public ObservableCollection<string> CategoryFilters { get; } = new();
    public bool CanEditAllResponsibleOffices => _session.HasAdministrativePrivileges;
    public bool HasContractAlerts => ContractAlerts.Count > 0;

    public CustomerManagementViewModel(LocalStateService local, SessionState session)
    {
        _local = local;
        _session = session;
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

            _allRows.Clear();
            _allRows.AddRange(customers.Select(customer => new EnvironmentCustomerRow(
                customer,
                ResolveCategoryName(customer.CategoryId),
                ResolveContractSummary(customer.Id))));
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
        if (IsBusy)
            return;

        var changed = _allRows.Where(row => row.IsModified).ToList();
        if (changed.Count == 0)
        {
            StatusMessage = "변경한 담당지점이 없습니다.";
            return;
        }

        IsBusy = true;
        try
        {
            var grantedTemporaryAccess = false;
            foreach (var row in changed)
            {
                row.ApplyToSource();

                if (_session.HasAdministrativePrivileges)
                {
                    await _local.UpsertCustomerAsync(row.Source);
                    await _local.WaitForServerWriteAsync();
                }
                else
                {
                    var result = await _local.UpsertCustomerAsync(row.Source, _session);
                    if (!result.Success)
                    {
                        StatusMessage = result.Message;
                        return;
                    }

                    await _local.WaitForServerWriteAsync();
                    grantedTemporaryAccess |= result.GrantedTemporaryAccess;
                }

                row.AcceptChanges();
            }

            StatusMessage = _session.HasAdministrativePrivileges
                ? $"담당지점 변경 {changed.Count:N0}건을 저장했습니다."
                : grantedTemporaryAccess
                    ? "거래처를 저장했습니다. USENET 거래처는 당일만 계속 작업할 수 있습니다."
                    : $"담당지점 변경 {changed.Count:N0}건을 저장했습니다.";

            ApplyFilter();
        }
        finally
        {
            IsBusy = false;
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
        foreach (var officeCode in offices
                     .Select(office => office.Code)
                     .Where(code => !string.IsNullOrWhiteSpace(code))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(code => code, StringComparer.OrdinalIgnoreCase))
        {
            OfficeCodes.Add(officeCode);
        }

        if (OfficeCodes.Count == 0)
        {
            OfficeCodes.Add(DomainConstants.OfficeUsenet);
            OfficeCodes.Add(DomainConstants.OfficeItworld);
            OfficeCodes.Add(DomainConstants.OfficeYeonsu);
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

        if (!CategoryFilters.Contains(SelectedCategoryFilter))
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
                row.ContractStatusText.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.Equals(SelectedCategoryFilter, AllCategoriesOption, StringComparison.CurrentCultureIgnoreCase))
        {
            filtered = filtered.Where(row =>
                string.Equals(row.CategoryName, SelectedCategoryFilter, StringComparison.CurrentCultureIgnoreCase));
        }

        var list = filtered
            .OrderBy(row => row.NameOriginal, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

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
        ApplyFilter();
    }

    partial void OnSelectedCategoryFilterChanged(string value)
    {
        ApplyFilter();
    }

    private string ResolveCategoryName(Guid? categoryId)
    {
        if (!categoryId.HasValue)
            return "-";

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

    private static int GetOfficeSortOrder(string? officeCode)
        => OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode, DomainConstants.OfficeUsenet) switch
        {
            var value when string.Equals(value, DomainConstants.OfficeUsenet, StringComparison.OrdinalIgnoreCase) => 0,
            var value when string.Equals(value, DomainConstants.OfficeItworld, StringComparison.OrdinalIgnoreCase) => 1,
            var value when string.Equals(value, DomainConstants.OfficeYeonsu, StringComparison.OrdinalIgnoreCase) => 2,
            _ => 99
        };
}
