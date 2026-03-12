using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SalesMaster.Desktop.App.Data;
using SalesMaster.Desktop.App.Services;

namespace SalesMaster.Desktop.App.ViewModels;

public sealed partial class YeonsuDeliveryViewModel : ObservableObject
{
    private readonly LocalStateService _local;
    private readonly SessionState _session;
    private readonly List<LocalCustomer> _allOfficeCustomers = new();

    public const string WarehouseOptionAll = "전체";
    public const string WarehouseOptionUznet = "유즈넷 창고";
    public const string WarehouseOptionYeonsu = "연수구 창고";

    public ObservableCollection<DisplayOption> OfficeOptions { get; } = new();
    public ObservableCollection<LocalCustomer> Customers { get; } = new();
    public ObservableCollection<YeonsuDeliveryRow> Deliveries { get; } = new();
    public IReadOnlyList<string> WarehouseOptions { get; } =
    [
        WarehouseOptionAll,
        WarehouseOptionUznet,
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

        _ = ReloadForOfficeAsync();
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
        if (!string.IsNullOrWhiteSpace(_session.OfficeCode) && !_session.IsAdmin)
            SelectedOfficeCode = _session.OfficeCode;
        await LoadDeliveriesAsync();
    }

    private async Task LoadOfficeOptionsAsync()
    {
        var offices = await _local.GetOfficesAsync();
        OfficeOptions.Clear();
        foreach (var office in offices.OrderBy(current => current.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (!_session.IsAdmin &&
                !string.Equals(office.Code, _session.OfficeCode, StringComparison.OrdinalIgnoreCase))
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
                Value = string.IsNullOrWhiteSpace(_session.OfficeCode) ? DomainConstants.OfficeYeonsu : _session.OfficeCode,
                DisplayName = string.IsNullOrWhiteSpace(_session.OfficeCode) ? "연수구" : _session.OfficeCode
            });
        }

        if (_session.IsAdmin)
        {
            SelectedOfficeCode = OfficeOptions.FirstOrDefault(option => string.Equals(option.Value, SelectedOfficeCode, StringComparison.OrdinalIgnoreCase))?.Value
                ?? OfficeOptions.First().Value;
        }
        else
        {
            SelectedOfficeCode = OfficeOptions.First().Value;
        }
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

    private async Task ReloadForOfficeAsync()
    {
        if (IsBusy)
            return;

        await LoadCustomersAsync();
        await LoadDeliveriesAsync();
    }

    private string ResolveOfficeDisplayName(string? officeCode)
        => OfficeOptions.FirstOrDefault(option => string.Equals(option.Value, officeCode, StringComparison.OrdinalIgnoreCase))?.DisplayName
           ?? (string.IsNullOrWhiteSpace(officeCode) ? "담당지점" : officeCode);

    private static string ResolveWarehouseCodeFilter(string option)
    {
        return option switch
        {
            WarehouseOptionUznet => DomainConstants.WarehouseUznetMain,
            WarehouseOptionYeonsu => DomainConstants.WarehouseYeonsuMain,
            _ => string.Empty
        };
    }

    private static string ToWarehouseDisplay(string warehouseCode)
    {
        var code = (warehouseCode ?? string.Empty).Trim().ToUpperInvariant();
        if (string.Equals(code, DomainConstants.WarehouseUznetMain, StringComparison.OrdinalIgnoreCase))
            return "유즈넷 창고";

        if (string.Equals(code, DomainConstants.WarehouseYeonsuMain, StringComparison.OrdinalIgnoreCase))
            return "연수구 창고";

        return string.IsNullOrWhiteSpace(code) ? "-" : code;
    }
}
