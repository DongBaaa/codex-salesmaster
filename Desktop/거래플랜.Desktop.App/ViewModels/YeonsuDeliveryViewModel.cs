using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class YeonsuDeliveryViewModel : ObservableObject
{
    private readonly LocalStateService _local;
    private readonly SessionState _session;
    private readonly List<LocalCustomer> _allOfficeCustomers = new();
    private int _reloadForOfficeVersion;

    public const string WarehouseOptionAll = "전체";
    public const string WarehouseOptionUsenet = "USENET 창고";
    public const string WarehouseOptionYeonsu = "YEONSU 창고";

    public ObservableCollection<DisplayOption> OfficeOptions { get; } = new();
    public ObservableCollection<LocalCustomer> Customers { get; } = new();
    public ObservableCollection<YeonsuDeliveryRow> Deliveries { get; } = new();
    public IReadOnlyList<string> WarehouseOptions { get; } =
    [
        WarehouseOptionAll,
        WarehouseOptionUsenet,
        WarehouseOptionYeonsu
    ];

    [ObservableProperty] private DateOnly _fromDate = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    [ObservableProperty] private DateOnly _toDate = DateOnly.FromDateTime(DateTime.Today);
    [ObservableProperty] private string _selectedOfficeCode = DomainConstants.OfficeYeonsu;
    [ObservableProperty] private string _customerSearchText = string.Empty;
    [ObservableProperty] private LocalCustomer? _selectedCustomer;
    [ObservableProperty] private string _selectedWarehouseOption = WarehouseOptionAll;
    [ObservableProperty] private YeonsuDeliveryRow? _selectedDelivery;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "조회 조건을 선택하세요.";

    public YeonsuDeliveryViewModel(LocalStateService local, SessionState session)
    {
        _local = local;
        _session = session;
    }

    public async Task InitializeAsync()
    {
        await LoadOfficeOptionsAsync();
        await LoadCustomersAsync();
        await LoadDeliveriesAsync();
    }

    partial void OnSelectedOfficeCodeChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || OfficeOptions.Count == 0)
            return;

        var version = Interlocked.Increment(ref _reloadForOfficeVersion);
        UiTaskHelper.Forget(
            ReloadForOfficeAsync(version),
            "DELIVERY",
            "연수구 납품 화면 지점 재조회",
            ex =>
            {
                if (version == Volatile.Read(ref _reloadForOfficeVersion))
                    StatusMessage = $"지점 변경 후 납품 화면을 다시 불러오지 못했습니다. {ex.Message}";
            });
    }

    partial void OnCustomerSearchTextChanged(string value)
    {
        ApplyCustomerFilter();
    }

    [RelayCommand]
    private void SearchCustomer()
    {
        var keyword = (CustomerSearchText ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(keyword))
            return;

        var target = Customers.FirstOrDefault(customer =>
            customer.NameOriginal.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        if (target is not null)
            SelectedCustomer = target;
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
            var invoices = await _local.GetYeonsuDeliveryInvoicesAsync(
                FromDate,
                ToDate,
                SelectedCustomer?.Id,
                warehouseCodeFilter,
                SelectedOfficeCode,
                _session);

            var customerMap = _allOfficeCustomers.ToDictionary(customer => customer.Id, customer => customer);
            Deliveries.Clear();

            foreach (var invoice in invoices)
            {
                customerMap.TryGetValue(invoice.CustomerId, out var customer);
                Deliveries.Add(new YeonsuDeliveryRow
                {
                    InvoiceId = invoice.Id,
                    CustomerId = invoice.CustomerId,
                    DeliveryDate = invoice.InvoiceDate,
                    CustomerName = customer?.NameOriginal ?? "(거래처 미상)",
                    ItemSummary = BuildItemSummary(invoice),
                    TotalAmount = invoice.TotalAmount,
                    WarehouseCode = invoice.SourceWarehouseCode,
                    WarehouseDisplay = ToWarehouseDisplay(invoice.SourceWarehouseCode),
                    Memo = invoice.Memo ?? string.Empty,
                    LastSavedBy = invoice.LastSavedByUsername,
                    LastSavedAtUtc = invoice.LastSavedAtUtc,
                    VersionNumber = invoice.VersionNumber
                });
            }

            var officeDisplay = ResolveOfficeDisplayName(SelectedOfficeCode);
            StatusMessage = Deliveries.Count == 0
                ? $"조건에 맞는 {officeDisplay} 납품 전표가 없습니다."
                : $"{officeDisplay} 납품 전표 {Deliveries.Count:N0}건을 조회했습니다.";
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
        SelectedCustomer = null;
        SelectedWarehouseOption = WarehouseOptionAll;
        if (!string.IsNullOrWhiteSpace(_session.OfficeCode) && !_session.HasGlobalDataScope)
            SelectedOfficeCode = _session.OfficeCode;
        await LoadDeliveriesAsync();
    }

    private async Task LoadOfficeOptionsAsync()
    {
        var offices = await _local.GetOfficesAsync();
        var readableOfficeCodes = GetReadableOfficeCodes();
        OfficeOptions.Clear();
        foreach (var office in offices.OrderBy(current => current.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (!_session.HasGlobalDataScope &&
                !readableOfficeCodes.Contains(office.Code))
            {
                continue;
            }

            OfficeOptions.Add(new DisplayOption
            {
                Value = office.Code,
                DisplayName = office.Name
            });
        }

        if (OfficeOptions.Count == 0)
        {
            OfficeOptions.Add(new DisplayOption
            {
                Value = string.IsNullOrWhiteSpace(_session.OfficeCode) ? DomainConstants.OfficeYeonsu : OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(_session.OfficeCode, DomainConstants.OfficeYeonsu),
                DisplayName = OfficeCodeCatalog.GetOfficeDisplayName(string.IsNullOrWhiteSpace(_session.OfficeCode) ? DomainConstants.OfficeYeonsu : _session.OfficeCode)
            });
        }

        if (_session.HasGlobalDataScope)
        {
            SelectedOfficeCode = OfficeOptions.FirstOrDefault(option => string.Equals(option.Value, SelectedOfficeCode, StringComparison.OrdinalIgnoreCase))?.Value
                ?? OfficeOptions.First().Value;
        }
        else if (string.Equals(_session.ScopeType, TenantScopeCatalog.ScopeTenantAll, StringComparison.OrdinalIgnoreCase))
        {
            SelectedOfficeCode = OfficeOptions.FirstOrDefault(option => string.Equals(option.Value, _session.OfficeCode, StringComparison.OrdinalIgnoreCase))?.Value
                ?? OfficeOptions.First().Value;
        }
        else
        {
            SelectedOfficeCode = OfficeOptions.First().Value;
        }
    }

    private HashSet<string> GetReadableOfficeCodes()
    {
        if (_session.HasGlobalDataScope)
            return OfficeCodeCatalog.All.ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (string.Equals(_session.ScopeType, TenantScopeCatalog.ScopeTenantAll, StringComparison.OrdinalIgnoreCase))
        {
            return TenantScopeCatalog.GetOfficeCodesForTenant(_session.TenantCode)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(_session.OfficeCode, DomainConstants.OfficeYeonsu)
        };
    }

    private async Task LoadCustomersAsync()
    {
        var customers = await _local.GetCustomersAsync(_session);
        _allOfficeCustomers.Clear();
        _allOfficeCustomers.AddRange(customers
            .Where(customer => string.Equals(
                customer.ResponsibleOfficeCode?.Trim(),
                SelectedOfficeCode,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(customer => customer.NameOriginal, StringComparer.CurrentCultureIgnoreCase));

        ApplyCustomerFilter();
    }

    private void ApplyCustomerFilter()
    {
        var keyword = (CustomerSearchText ?? string.Empty).Trim();
        IEnumerable<LocalCustomer> filtered = _allOfficeCustomers;
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            filtered = filtered.Where(customer =>
                customer.NameOriginal.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                customer.BusinessNumber.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                customer.Phone.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        var filteredList = filtered.ToList();
        Customers.Clear();
        foreach (var customer in filteredList)
            Customers.Add(customer);

        if (SelectedCustomer is not null && filteredList.All(customer => customer.Id != SelectedCustomer.Id))
            SelectedCustomer = null;
    }

    private static string BuildItemSummary(LocalInvoice invoice)
    {
        var lines = invoice.Lines
            .Where(line => !line.IsDeleted && !string.IsNullOrWhiteSpace(line.ItemNameOriginal))
            .ToList();

        if (lines.Count == 0)
            return "(품목 없음)";

        if (lines.Count == 1)
            return lines[0].ItemNameOriginal;

        return $"{lines[0].ItemNameOriginal} 외 {lines.Count - 1}건";
    }

    private async Task ReloadForOfficeAsync(int version)
    {
        if (IsBusy)
            return;

        await LoadCustomersAsync();
        if (version != Volatile.Read(ref _reloadForOfficeVersion))
            return;

        await LoadDeliveriesAsync();
    }

    private string ResolveOfficeDisplayName(string? officeCode)
        => OfficeOptions.FirstOrDefault(option => string.Equals(option.Value, officeCode, StringComparison.OrdinalIgnoreCase))?.DisplayName
           ?? (string.IsNullOrWhiteSpace(officeCode) ? "담당지점" : officeCode);

    private static string ResolveWarehouseCodeFilter(string option)
    {
        return option switch
        {
            WarehouseOptionUsenet => DomainConstants.WarehouseUsenetMain,
            WarehouseOptionYeonsu => DomainConstants.WarehouseYeonsuMain,
            _ => string.Empty
        };
    }

    private static string ToWarehouseDisplay(string warehouseCode)
    {
        var code = (warehouseCode ?? string.Empty).Trim().ToUpperInvariant();
        if (string.Equals(code, DomainConstants.WarehouseUsenetMain, StringComparison.OrdinalIgnoreCase))
            return "USENET 창고";

        if (string.Equals(code, DomainConstants.WarehouseYeonsuMain, StringComparison.OrdinalIgnoreCase))
            return "YEONSU 창고";

        return string.IsNullOrWhiteSpace(code) ? "-" : code;
    }
}



