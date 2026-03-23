using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Printing;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.Views;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly LocalStateService _local;
    private readonly SyncService _sync;
    private readonly BackupService _backup;
    private readonly RentalStateService _rental;
    private readonly SessionState _session;
    private readonly IPrintService _invoicePrintService = new WpfInvoicePrintService();
    private static readonly JsonSerializerOptions PrintModelJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly LegacyDataMigrationService _legacyMigrationService;
    private const string LegacySourceDbPathSettingKey = "LegacyMigration.SourceDbPath";
    private const string LegacyCustomerExcelPathSettingKey = "LegacyMigration.CustomerExcelPath";
    private const string LegacyItemExcelPathSettingKey = "LegacyMigration.ItemExcelPath";

    // ?? Status bar ?????????????????????????????????????????????????????????
    [ObservableProperty] private string _syncStatus = "동기화 대기";
    [ObservableProperty] private string _currentUserDisplay = string.Empty;

    // ?? Tabs ???????????????????????????????????????????????????????????????
    [ObservableProperty] private int _selectedTabIndex;

    // Dashboard card metrics
    [ObservableProperty] private decimal _dashboardMonthlySales;
    [ObservableProperty] private decimal _dashboardReceivable;
    [ObservableProperty] private int _dashboardCustomerCount;
    [ObservableProperty] private int _dashboardSafetyStockAlerts;
    [ObservableProperty] private int _dashboardMonthlyInvoiceCount;
    [ObservableProperty] private decimal _dashboardMonthlyAverageSales;
    [ObservableProperty] private decimal _dashboardSalesTrendPercent;
    [ObservableProperty] private int _dashboardRentalDueTodayCount;
    [ObservableProperty] private int _dashboardRentalUpcomingCount;
    [ObservableProperty] private int _dashboardRentalOverdueCount;
    [ObservableProperty] private string _rentalAlertPopupMessage = string.Empty;

    // ?? ?꾪몴 紐⑸줉 ?? Left panel (嫄곕옒泥??꾪꽣) ??????????????????????????????
    private List<LocalCustomer> _allCustomers = new();
    public ObservableCollection<LocalCustomer> FilteredCustomers { get; } = new();
    [ObservableProperty] private string _customerFilterText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedCustomer))]
    private LocalCustomer? _selectedCustomerFilter;
    public bool HasSelectedCustomer => SelectedCustomerFilter is not null;

    // ?? 嫄곕옒泥??몃씪???몄쭛 (?곗륫 ?⑤꼸) ??????????????????????????????????????
    private bool _suppressCustomerSave;
    [ObservableProperty] private string _editCustBizNumber = string.Empty;
    [ObservableProperty] private string _editCustPhone = string.Empty;
    [ObservableProperty] private string _editCustDept = string.Empty;
    [ObservableProperty] private string _editCustContactPerson = string.Empty;
    [ObservableProperty] private string _editCustAddress = string.Empty;
    [ObservableProperty] private string _editCustNotes = string.Empty;

    partial void OnEditCustBizNumberChanged(string value) => _ = AutoSaveCustomerAsync();
    partial void OnEditCustPhoneChanged(string value) => _ = AutoSaveCustomerAsync();
    partial void OnEditCustDeptChanged(string value) => _ = AutoSaveCustomerAsync();
    partial void OnEditCustContactPersonChanged(string value) => _ = AutoSaveCustomerAsync();
    partial void OnEditCustAddressChanged(string value) => _ = AutoSaveCustomerAsync();
    partial void OnEditCustNotesChanged(string value) => _ = AutoSaveCustomerAsync();

    private async Task AutoSaveCustomerAsync()
    {
        if (_suppressCustomerSave) return;
        var customer = SelectedCustomerFilter;
        if (customer is null) return;
        customer.BusinessNumber = EditCustBizNumber;
        customer.Phone = EditCustPhone;
        customer.Department = EditCustDept;
        customer.ContactPerson = EditCustContactPerson;
        customer.Address = EditCustAddress;
        customer.Notes = EditCustNotes;
        customer.NameMatchKey = customer.NameOriginal.ToUpperInvariant();
        var result = await _local.UpsertCustomerAsync(customer, _session);
        if (!result.Success)
            AppLogger.Warn("AUTOSAVE", $"Customer inline auto-save failed for '{customer.NameOriginal}'. {result.Message}");
        else
            await _local.WaitForServerWriteAsync();
    }

    // ?? ?꾪몴 紐⑸줉 ?? Bottom panel (?좏깮???꾪몴 ?쇱씤 誘몃━蹂닿린) ???????????????
    public ObservableCollection<InvoiceLineEditModel> PreviewLines { get; } = new();
    [ObservableProperty] private decimal _previewSupplyAmount;
    [ObservableProperty] private decimal _previewVatAmount;
    [ObservableProperty] private decimal _previewTotalAmount;

    // ?? ?꾪몴 紐⑸줉 ?? Right panel (嫄곕옒泥??뺣낫 誘몃━蹂닿린) ?????????????????????
    [ObservableProperty] private string _previewCustomerName = string.Empty;
    [ObservableProperty] private string _previewCustomerBizNumber = string.Empty;
    [ObservableProperty] private string _previewCustomerPhone = string.Empty;
    [ObservableProperty] private string _previewCustomerAddress = string.Empty;
    [ObservableProperty] private string _previewCustomerNotes = string.Empty;
    [ObservableProperty] private string _previewCustomerDepartment = string.Empty;
    [ObservableProperty] private string _previewCustomerContactPerson = string.Empty;

    // ?? Invoice List (?꾪몴 紐⑸줉) ????????????????????????????????????????????
    public ObservableCollection<InvoiceListRow> InvoiceRows { get; } = new();
    public ObservableCollection<FavoriteInvoiceQuickItem> FavoriteInvoices { get; } = new();
    [ObservableProperty] private InvoiceListRow? _selectedInvoiceRow;
    [ObservableProperty] private FavoriteInvoiceQuickItem? _selectedFavoriteInvoice;
    [ObservableProperty] private DateOnly _filterFrom = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    [ObservableProperty] private DateOnly _filterTo = DateOnly.FromDateTime(DateTime.Today);
    [ObservableProperty] private string _filterCustomerName = string.Empty;
    [ObservableProperty] private string _selectedVoucherTypeFilter = "전체";
    [ObservableProperty] private string _filterMinAmountText = string.Empty;
    [ObservableProperty] private string _filterMaxAmountText = string.Empty;
    public IReadOnlyList<string> VoucherTypeFilterOptions { get; } = ["전체", "매출", "매입", "발주", "경비", "수금"];
    private bool _suppressFilterAutoSave;
    private const string InvoiceFilterFromSettingKey = "InvoiceFilter.From";
    private const string InvoiceFilterToSettingKey = "InvoiceFilter.To";
    private const string InvoiceFilterCustomerSettingKey = "InvoiceFilter.CustomerName";
    private const string InvoiceFilterVoucherTypeSettingKey = "InvoiceFilter.VoucherType";
    private const string InvoiceFilterOfficeCodeSettingKey = "InvoiceFilter.OfficeCode";
    private const string InvoiceFilterMinAmountSettingKey = "InvoiceFilter.MinAmount";
    private const string InvoiceFilterMaxAmountSettingKey = "InvoiceFilter.MaxAmount";
    private const string FavoriteInvoiceIdsSettingKey = "InvoiceFavorites.Ids";

    // ?? Invoice Editor (?꾪몴 ?묒꽦) ??????????????????????????????????????????
    [ObservableProperty] private Guid _editInvoiceId = Guid.NewGuid();
    [ObservableProperty] private LocalCustomer? _editCustomer;
    [ObservableProperty] private string _editCustomerName = string.Empty;
    [ObservableProperty] private DateOnly _editInvoiceDate = DateOnly.FromDateTime(DateTime.Today);
    [ObservableProperty] private VoucherType _editVoucherType = VoucherType.Sales;
    [ObservableProperty] private string _editMemo = string.Empty;
    [ObservableProperty] private decimal _editTotalAmount;
    [ObservableProperty] private decimal _editSupplyAmount;
    [ObservableProperty] private decimal _editVatAmount;
    private string _editConcurrencyStamp = string.Empty;
    public ObservableCollection<InvoiceLineEditModel> EditLines { get; } = new();
    public Array VoucherTypes => Enum.GetValues<VoucherType>();

    // ?? Payment Tab (?섍툑 ?낅젰) ????????????????????????????????????????????
    [ObservableProperty] private InvoiceListRow? _paymentInvoice;
    public ObservableCollection<PaymentRowModel> PaymentRows { get; } = new();
    [ObservableProperty] private decimal _paymentTotalPaid;
    [ObservableProperty] private decimal _paymentBalance;

    // ?? Statement tab (嫄곕옒紐낆꽭?? ?????????????????????????????????????????
    [ObservableProperty] private InvoiceListRow? _statementInvoice;

    // ?? Company settings (?뚯궗 ?ㅼ젙) ??????????????????????????????????????
    [ObservableProperty] private string _companyTradeName = string.Empty;
    [ObservableProperty] private string _companyRepresentative = string.Empty;
    [ObservableProperty] private string _companyBusinessNumber = string.Empty;
    [ObservableProperty] private string _companyBusinessType = string.Empty;
    [ObservableProperty] private string _companyBusinessItem = string.Empty;
    [ObservableProperty] private string _companyAddress = string.Empty;
    [ObservableProperty] private string _companyContactNumber = string.Empty;
    [ObservableProperty] private string _companyEmail = string.Empty;
    [ObservableProperty] private string _companyBankAccountText = string.Empty;
    [ObservableProperty] private byte[]? _companyStampImage;
    [ObservableProperty] private string _companyStampImagePath = "(?놁쓬)";
    [ObservableProperty] private string _legacySourceDbPath = string.Empty;
    [ObservableProperty] private string _legacyCustomerExcelPath = string.Empty;
    [ObservableProperty] private string _legacyItemExcelPath = string.Empty;
    [ObservableProperty] private string _legacyMigrationStatus = "원본 데이터 추출/가져오기 대기";
    private Guid _companyProfileId = Guid.NewGuid();

    public MainViewModel(
        LocalStateService local,
        SyncService sync,
        BackupService backup,
        RentalStateService rental,
        SessionState session)
    {
        _local = local;
        _sync = sync;
        _backup = backup;
        _rental = rental;
        _session = session;
        _legacyMigrationService = new LegacyDataMigrationService(local);

        _sync.SyncStatusChanged += HandleSyncStatusChanged;
        _session.BusinessDatabaseChanged += HandleBusinessDatabaseChanged;
        RefreshCurrentUserDisplay();
    }

    private void HandleSyncStatusChanged(string status)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            dispatcher.Invoke(() => SyncStatus = status);
        else
            SyncStatus = status;

        AppLogger.Info("SYNC-UI", status);
    }

    public async Task LoadAsync()
    {
        await LoadCustomersAsync();
        await LoadInvoiceFilterSettingsAsync();
        await LoadInvoiceListAsync();
        await LoadCompanyProfileAsync();
        await LoadLegacyMigrationSettingsAsync();
        if (_session.IsOfflineMode)
            SyncStatus = "오프라인 모드에서는 자동 동기화를 진행하지 않습니다.";
    }

    public async Task RunPostLoginSyncAsync()
    {
        if (_session.IsOfflineMode)
        {
            SyncStatus = "로그인 후 서버 동기화를 진행하지 못했습니다.";
            return;
        }

        var dirtyBefore = await _local.CountDirtyAsync();
        SyncStatus = "로그인 후 서버 동기화 중...";

        var syncOk = await _sync.TrySyncAsync();
        if (syncOk)
        {
            if (await _local.CountDirtyAsync() == 0)
                await _sync.RefreshSharedMirrorFromServerAsync();
            await ReloadAfterPassiveSyncAsync();
            SyncStatus = $"로그인 후 서버 동기화 완료 {DateTime.Now:HH:mm:ss}";
            return;
        }

        var dirtyAfter = await _local.CountDirtyAsync();
        if (dirtyBefore > 0 || dirtyAfter > 0)
        {
            var backupOk = await _backup.BackupNowAsync();
            AppLogger.Warn(
                "APP",
                $"Post-login auto sync failed with {dirtyAfter} dirty rows. Auto-backup {(backupOk ? "succeeded" : "failed")}.");
        }
    }

    // ?? Customer Filter (Left Panel) ???????????????????????????????????????
    private async Task LoadCustomersAsync()
    {
        _allCustomers = await _local.GetCustomersAsync(_session);
        DashboardCustomerCount = _allCustomers.Count;
        ApplyCustomerFilter();
    }

    private void ApplyCustomerFilter()
    {
        var text = CustomerFilterText.Trim();
        FilteredCustomers.Clear();
        var filtered = string.IsNullOrEmpty(text)
            ? _allCustomers
            : _allCustomers.Where(c => c.NameOriginal.Contains(text, StringComparison.OrdinalIgnoreCase));
        foreach (var c in filtered)
            FilteredCustomers.Add(c);
    }

    partial void OnCustomerFilterTextChanged(string value) => ApplyCustomerFilter();
    partial void OnSelectedCustomerFilterChanged(LocalCustomer? value)
    {
        _suppressCustomerSave = true;
        try
        {
            PreviewCustomerName = value?.NameOriginal ?? string.Empty;
            EditCustBizNumber = value?.BusinessNumber ?? string.Empty;
            EditCustPhone = value?.Phone ?? string.Empty;
            EditCustDept = value?.Department ?? string.Empty;
            EditCustContactPerson = value?.ContactPerson ?? string.Empty;
            EditCustAddress = value?.Address ?? string.Empty;
            EditCustNotes = value?.Notes ?? string.Empty;
        }
        finally { _suppressCustomerSave = false; }

        _ = RefreshCustomerFinancialPreviewAsync(value);
        HandleInvoiceFilterChanged();
    }

    partial void OnFilterFromChanged(DateOnly value) => HandleInvoiceFilterChanged();
    partial void OnFilterToChanged(DateOnly value) => HandleInvoiceFilterChanged();
    partial void OnFilterCustomerNameChanged(string value) => HandleInvoiceFilterChanged();
    partial void OnSelectedVoucherTypeFilterChanged(string value) => HandleInvoiceFilterChanged();
    partial void OnFilterMinAmountTextChanged(string value) => HandleInvoiceFilterChanged();
    partial void OnFilterMaxAmountTextChanged(string value) => HandleInvoiceFilterChanged();

    [RelayCommand]
    private async Task ResetInvoiceFiltersAsync()
    {
        _suppressFilterAutoSave = true;
        FilterFrom = new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1);
        FilterTo = DateOnly.FromDateTime(DateTime.Today);
        FilterCustomerName = string.Empty;
        SelectedVoucherTypeFilter = "전체";
        SelectedInvoiceOfficeFilterCode = GetDefaultInvoiceOfficeFilterCode();
        FilterMinAmountText = string.Empty;
        FilterMaxAmountText = string.Empty;
        SelectedCustomerFilter = null;
        _suppressFilterAutoSave = false;

        await PersistInvoiceFiltersAsync();
        await LoadInvoiceListAsync();
    }

    [RelayCommand]
    private void ClearCustomerFilter()
    {
        SelectedCustomerFilter = null;
    }

    [RelayCommand]
    private void SelectRecentInvoice()
    {
        if (InvoiceRows.Count == 0)
            return;

        SelectedInvoiceRow = InvoiceRows[0];
    }

    [RelayCommand]
    private async Task ToggleInvoiceFavoriteAsync()
    {
        if (SelectedInvoiceRow is null)
        {
            System.Windows.MessageBox.Show("즐겨찾기에 등록할 전표를 선택하세요.", "알림", System.Windows.MessageBoxButton.OK);
            return;
        }

        var ids = await GetFavoriteInvoiceIdsAsync();
        if (ids.Contains(SelectedInvoiceRow.Id))
            ids.Remove(SelectedInvoiceRow.Id);
        else
            ids.Insert(0, SelectedInvoiceRow.Id);

        await SaveFavoriteInvoiceIdsAsync(ids);
        await LoadInvoiceFavoritesAsync();
    }

    [RelayCommand]
    private async Task OpenFavoriteInvoiceAsync()
    {
        if (SelectedFavoriteInvoice is null)
        {
            System.Windows.MessageBox.Show("이동할 즐겨찾기 전표를 선택하세요.", "알림", System.Windows.MessageBoxButton.OK);
            return;
        }

        var targetId = SelectedFavoriteInvoice.InvoiceId;
        var targetRow = InvoiceRows.FirstOrDefault(r => r.Id == targetId);

        if (targetRow is null)
        {
            var invoice = await _local.GetInvoiceAsync(targetId, _session);
            if (invoice is null)
            {
                System.Windows.MessageBox.Show("선택한 즐겨찾기 전표를 찾을 수 없습니다.", "알림", System.Windows.MessageBoxButton.OK);
                return;
            }

            _suppressFilterAutoSave = true;
            SelectedCustomerFilter = _allCustomers.FirstOrDefault(c => c.Id == invoice.CustomerId);
            FilterCustomerName = string.Empty;
            SelectedVoucherTypeFilter = "전체";
            FilterMinAmountText = string.Empty;
            FilterMaxAmountText = string.Empty;
            FilterFrom = new DateOnly(invoice.InvoiceDate.Year, invoice.InvoiceDate.Month, 1);
            FilterTo = new DateOnly(invoice.InvoiceDate.Year, invoice.InvoiceDate.Month, DateTime.DaysInMonth(invoice.InvoiceDate.Year, invoice.InvoiceDate.Month));
            _suppressFilterAutoSave = false;

            await PersistInvoiceFiltersAsync();
            await LoadInvoiceListAsync();
            targetRow = InvoiceRows.FirstOrDefault(r => r.Id == targetId);
        }

        if (targetRow is null)
        {
            System.Windows.MessageBox.Show("즐겨찾기 전표를 현재 목록에서 찾지 못했습니다.", "알림", System.Windows.MessageBoxButton.OK);
            return;
        }

        SelectedTabIndex = 0;
        SelectedInvoiceRow = targetRow;
    }

    // ?? Invoice Preview (on selection) ?????????????????????????????????????
    partial void OnSelectedInvoiceRowChanged(InvoiceListRow? value)
        => _ = LoadPreviewAsync(value);

    private async Task LoadPreviewAsync(InvoiceListRow? row)
    {
        PreviewLines.Clear();
        PreviewTotalAmount = 0;
        PreviewSupplyAmount = 0;
        PreviewVatAmount = 0;

        if (row is null)
        {
            if (SelectedCustomerFilter is null)
                await RefreshCustomerFinancialPreviewAsync(null);
            return;
        }

        var inv = await _local.GetInvoiceAsync(row.Id, _session);
        if (inv is null)
        {
            if (SelectedCustomerFilter is null)
                await RefreshCustomerFinancialPreviewAsync(null);
            return;
        }

        foreach (var line in inv.Lines.Where(l => !l.IsDeleted))
            PreviewLines.Add(InvoiceLineEditModel.FromLocal(line));

        PreviewTotalAmount = inv.TotalAmount;
        PreviewSupplyAmount = inv.SupplyAmount;
        PreviewVatAmount = inv.VatAmount;

        // 醫뚯륫 嫄곕옒泥섍? ?좏깮?섏? ?딆? 寃쎌슦?먮쭔 ?곗륫 ?⑤꼸 怨좉컼 ?뺣낫 ?낅뜲?댄듃
        if (SelectedCustomerFilter is null)
        {
            var customer = _allCustomers.FirstOrDefault(c => c.Id == inv.CustomerId)
                ?? await _local.GetCustomerAsync(inv.CustomerId);
            if (customer is not null)
            {
                PreviewCustomerName = customer.NameOriginal;
                _suppressCustomerSave = true;
                try
                {
                    EditCustBizNumber = customer.BusinessNumber;
                    EditCustPhone = customer.Phone;
                    EditCustDept = customer.Department;
                    EditCustContactPerson = customer.ContactPerson;
                    EditCustAddress = customer.Address;
                    EditCustNotes = customer.Notes;
                }
                finally { _suppressCustomerSave = false; }

                await RefreshCustomerFinancialPreviewAsync(customer);
            }
            else
            {
                await RefreshCustomerFinancialPreviewAsync(null);
            }
        }
    }

    // ?? Invoice List ??????????????????????????????????????????????????????
    [RelayCommand]
    private async Task LoadInvoiceListAsync()
    {
        Guid? customerId = SelectedCustomerFilter?.Id;
        var invoices = await _local.GetInvoicesAsync(FilterFrom, FilterTo, customerId, _session);
        var customerMap = await _local.GetCustomerNameMapAsync(invoices.Select(invoice => invoice.CustomerId));
        IEnumerable<LocalInvoice> filteredInvoices = invoices;

        filteredInvoices = filteredInvoices.Where(MatchesSelectedInvoiceOffice);

        if (!string.IsNullOrWhiteSpace(FilterCustomerName))
        {
            var needle = FilterCustomerName.Trim();
            filteredInvoices = filteredInvoices.Where(inv =>
            {
                var name = customerMap.TryGetValue(inv.CustomerId, out var n) ? n : string.Empty;
                return name.Contains(needle, StringComparison.OrdinalIgnoreCase);
            });
        }

        if (!string.Equals(SelectedVoucherTypeFilter, "전체", StringComparison.OrdinalIgnoreCase))
        {
            var selectedType = SelectedVoucherTypeFilter switch
            {
                "매출" => VoucherType.Sales,
                "매입" => VoucherType.Purchase,
                "발주" => VoucherType.Procurement,
                "경비" => VoucherType.Expense,
                "수금" => VoucherType.Collection,
                _ => (VoucherType?)null
            };

            if (selectedType is { } type)
                filteredInvoices = filteredInvoices.Where(inv => inv.VoucherType == type);
        }

        var minAmount = ParseAmountFilter(FilterMinAmountText);
        var maxAmount = ParseAmountFilter(FilterMaxAmountText);
        if (minAmount.HasValue)
            filteredInvoices = filteredInvoices.Where(inv => inv.TotalAmount >= minAmount.Value);
        if (maxAmount.HasValue)
            filteredInvoices = filteredInvoices.Where(inv => inv.TotalAmount <= maxAmount.Value);

        var finalInvoices = filteredInvoices
            .OrderByDescending(i => i.InvoiceDate)
            .ThenByDescending(i => i.InvoiceNumber)
            .ToList();

        InvoiceRows.Clear();
        foreach (var inv in finalInvoices)
        {
            var custName = customerMap.TryGetValue(inv.CustomerId, out var n) ? n : "(미지정)";
            InvoiceRows.Add(InvoiceListRow.From(inv, custName));
        }

        await RefreshDashboardMetricsAsync();
        await LoadInvoiceFavoritesAsync();
    }

    private async Task RefreshDashboardMetricsAsync(IEnumerable<LocalInvoice>? invoices = null)
    {
        var sourceInvoices = invoices?.ToList()
            ?? await _local.GetInvoicesAsync(from: null, to: null, customerId: null, session: _session);
        var now = DateOnly.FromDateTime(DateTime.Today);
        var prevMonthDate = now.AddMonths(-1);

        var monthlySales = sourceInvoices
            .Where(i => i.VoucherType == VoucherType.Sales
                     && i.InvoiceDate.Year == now.Year
                     && i.InvoiceDate.Month == now.Month)
            .Sum(i => i.TotalAmount);

        var previousMonthlySales = sourceInvoices
            .Where(i => i.VoucherType == VoucherType.Sales
                     && i.InvoiceDate.Year == prevMonthDate.Year
                     && i.InvoiceDate.Month == prevMonthDate.Month)
            .Sum(i => i.TotalAmount);

        var monthlyInvoiceCount = sourceInvoices.Count(i =>
            i.InvoiceDate.Year == now.Year && i.InvoiceDate.Month == now.Month);

        DashboardMonthlySales = monthlySales;
        DashboardMonthlyInvoiceCount = monthlyInvoiceCount;
        DashboardMonthlyAverageSales = monthlyInvoiceCount == 0
            ? 0
            : Math.Round(monthlySales / monthlyInvoiceCount, 0, MidpointRounding.AwayFromZero);
        DashboardSalesTrendPercent = previousMonthlySales == 0
            ? (monthlySales > 0 ? 100m : 0m)
            : Math.Round(((monthlySales - previousMonthlySales) / previousMonthlySales) * 100m, 1, MidpointRounding.AwayFromZero);

        DashboardReceivable = sourceInvoices.Sum(i =>
            i.TotalAmount - i.Payments.Where(p => !p.IsDeleted).Sum(p => p.Amount));

        var items = await _local.GetItemsAsync();
        DashboardSafetyStockAlerts = items.Count(i =>
            i.SafetyStock > 0 && i.CurrentStock <= i.SafetyStock);
        DashboardCustomerCount = _allCustomers.Count;

        var rentalSummary = await _rental.GetDashboardSummaryAsync(_session, now);
        DashboardRentalDueTodayCount = rentalSummary.DueTodayCount;
        DashboardRentalUpcomingCount = rentalSummary.UpcomingCount;
        DashboardRentalOverdueCount = rentalSummary.OverdueCount;
        RentalAlertPopupMessage = rentalSummary.AlertPopupMessage;

        await RefreshContractDashboardAsync();
        await RefreshRecycleBinDashboardAsync();
    }

    private void HandleInvoiceFilterChanged()
    {
        if (_suppressFilterAutoSave)
            return;

        _ = ApplyInvoiceFiltersAsync();
    }

    private async Task ApplyInvoiceFiltersAsync()
    {
        await PersistInvoiceFiltersAsync();
        await LoadInvoiceListAsync();
    }

    private async Task PersistInvoiceFiltersAsync()
    {
        await _local.SetSettingAsync(BuildAccountScopedInvoiceFilterKey(InvoiceFilterFromSettingKey), FilterFrom.ToString("yyyy-MM-dd"));
        await _local.SetSettingAsync(BuildAccountScopedInvoiceFilterKey(InvoiceFilterToSettingKey), FilterTo.ToString("yyyy-MM-dd"));
        await _local.SetSettingAsync(BuildAccountScopedInvoiceFilterKey(InvoiceFilterCustomerSettingKey), FilterCustomerName ?? string.Empty);
        await _local.SetSettingAsync(BuildAccountScopedInvoiceFilterKey(InvoiceFilterVoucherTypeSettingKey), SelectedVoucherTypeFilter ?? "전체");
        await _local.SetSettingAsync(BuildAccountScopedInvoiceFilterKey(InvoiceFilterOfficeCodeSettingKey), SelectedInvoiceOfficeFilterCode ?? GetDefaultInvoiceOfficeFilterCode());
        await _local.SetSettingAsync(BuildAccountScopedInvoiceFilterKey(InvoiceFilterMinAmountSettingKey), FilterMinAmountText ?? string.Empty);
        await _local.SetSettingAsync(BuildAccountScopedInvoiceFilterKey(InvoiceFilterMaxAmountSettingKey), FilterMaxAmountText ?? string.Empty);
    }

    private async Task LoadInvoiceFilterSettingsAsync()
    {
        _suppressFilterAutoSave = true;

        InitializeInvoiceOfficeFilterOptions();

        var fromValue = await _local.GetSettingAsync(BuildAccountScopedInvoiceFilterKey(InvoiceFilterFromSettingKey));
        var toValue = await _local.GetSettingAsync(BuildAccountScopedInvoiceFilterKey(InvoiceFilterToSettingKey));
        var customerNameValue = await _local.GetSettingAsync(BuildAccountScopedInvoiceFilterKey(InvoiceFilterCustomerSettingKey));
        var voucherTypeValue = await _local.GetSettingAsync(BuildAccountScopedInvoiceFilterKey(InvoiceFilterVoucherTypeSettingKey));
        var officeCodeValue = await _local.GetSettingAsync(BuildAccountScopedInvoiceFilterKey(InvoiceFilterOfficeCodeSettingKey));
        var minAmountValue = await _local.GetSettingAsync(BuildAccountScopedInvoiceFilterKey(InvoiceFilterMinAmountSettingKey));
        var maxAmountValue = await _local.GetSettingAsync(BuildAccountScopedInvoiceFilterKey(InvoiceFilterMaxAmountSettingKey));

        if (DateOnly.TryParse(fromValue, out var parsedFrom))
            FilterFrom = parsedFrom;
        if (DateOnly.TryParse(toValue, out var parsedTo))
            FilterTo = parsedTo;

        FilterCustomerName = customerNameValue ?? string.Empty;
        SelectedVoucherTypeFilter = VoucherTypeFilterOptions.Contains(voucherTypeValue ?? string.Empty)
            ? voucherTypeValue!
            : "전체";
        var normalizedOfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(officeCodeValue, GetDefaultInvoiceOfficeFilterCode());
        SelectedInvoiceOfficeFilterCode = InvoiceOfficeFilterOptions.Any(option =>
            string.Equals(option.Code, normalizedOfficeCode, StringComparison.OrdinalIgnoreCase))
            ? normalizedOfficeCode
            : GetDefaultInvoiceOfficeFilterCode();
        FilterMinAmountText = minAmountValue ?? string.Empty;
        FilterMaxAmountText = maxAmountValue ?? string.Empty;

        _suppressFilterAutoSave = false;
    }

    private static decimal? ParseAmountFilter(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var normalized = raw.Replace(",", string.Empty, StringComparison.Ordinal).Trim();
        if (decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.CurrentCulture, out var value))
            return value;
        if (decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out value))
            return value;

        return null;
    }

    private async Task<List<Guid>> GetFavoriteInvoiceIdsAsync()
    {
        var raw = await _local.GetSettingAsync(BuildAccountScopedInvoiceFilterKey(FavoriteInvoiceIdsSettingKey));
        if (string.IsNullOrWhiteSpace(raw))
            return new List<Guid>();

        var ids = new List<Guid>();
        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Guid.TryParse(token, out var id))
                continue;
            if (!ids.Contains(id))
                ids.Add(id);
        }

        return ids;
    }

    private Task SaveFavoriteInvoiceIdsAsync(IEnumerable<Guid> ids)
    {
        var payload = string.Join(',', ids.Select(id => id.ToString("D")));
        return _local.SetSettingAsync(BuildAccountScopedInvoiceFilterKey(FavoriteInvoiceIdsSettingKey), payload);
    }

    private async Task LoadInvoiceFavoritesAsync()
    {
        var selectedId = SelectedFavoriteInvoice?.InvoiceId;
        var ids = await GetFavoriteInvoiceIdsAsync();
        var allInvoices = await _local.GetInvoicesAsync(from: null, to: null, customerId: null, session: _session);
        var invoiceMap = allInvoices.ToDictionary(i => i.Id);
        var customerMap = await _local.GetCustomerNameMapAsync(allInvoices.Select(invoice => invoice.CustomerId));

        FavoriteInvoices.Clear();
        foreach (var id in ids)
        {
            if (!invoiceMap.TryGetValue(id, out var invoice))
                continue;

            var customerName = customerMap.TryGetValue(invoice.CustomerId, out var n) ? n : "(미지정)";
            var display = $"{invoice.InvoiceDate:yyyy/MM/dd}  {customerName}  {invoice.TotalAmount:N0}원";

            FavoriteInvoices.Add(new FavoriteInvoiceQuickItem
            {
                InvoiceId = id,
                DisplayText = display
            });
        }

        if (FavoriteInvoices.Count != ids.Count)
            await SaveFavoriteInvoiceIdsAsync(FavoriteInvoices.Select(f => f.InvoiceId));

        SelectedFavoriteInvoice = selectedId.HasValue
            ? FavoriteInvoices.FirstOrDefault(f => f.InvoiceId == selectedId.Value)
            : FavoriteInvoices.FirstOrDefault();
    }

    [RelayCommand]
    private void NewInvoice()
    {
        EditInvoiceId = Guid.NewGuid();
        _editConcurrencyStamp = string.Empty;
        EditCustomer = null;
        EditCustomerName = string.Empty;
        EditInvoiceDate = DateOnly.FromDateTime(DateTime.Today);
        EditVoucherType = VoucherType.Sales;
        EditMemo = string.Empty;
        EditTotalAmount = 0;
        EditSupplyAmount = 0;
        EditVatAmount = 0;
        EditLines.Clear();
        AddNewLine();
    }

    [RelayCommand]
    private async Task EditInvoiceAsync()
    {
        if (SelectedInvoiceRow is null) return;
        var inv = await _local.GetInvoiceAsync(SelectedInvoiceRow.Id, _session);
        if (inv is null) return;

        EditInvoiceId = inv.Id;
        _editConcurrencyStamp = inv.ConcurrencyStamp;
        EditInvoiceDate = inv.InvoiceDate;
        EditVoucherType = inv.VoucherType;
        EditMemo = inv.Memo;
        EditTotalAmount = inv.TotalAmount;
        EditSupplyAmount = inv.SupplyAmount;
        EditVatAmount = inv.VatAmount;

        EditCustomer = _allCustomers.FirstOrDefault(c => c.Id == inv.CustomerId)
            ?? await _local.GetCustomerAsync(inv.CustomerId);
        EditCustomerName = EditCustomer?.NameOriginal ?? string.Empty;

        EditLines.Clear();
        foreach (var line in inv.Lines.Where(l => !l.IsDeleted))
            EditLines.Add(InvoiceLineEditModel.FromLocal(line));
    }

    [RelayCommand]
    private async Task SaveInvoiceAsync()
    {
        if (EditCustomer is null)
        {
            System.Windows.MessageBox.Show("嫄곕옒泥섎? ?좏깮?섏꽭??", "알림", System.Windows.MessageBoxButton.OK);
            return;
        }

        var lines = EditLines.Where(l => !string.IsNullOrWhiteSpace(l.ItemName)).ToList();
        var inv = new LocalInvoice
        {
            Id = EditInvoiceId,
            CustomerId = EditCustomer.Id,
            InvoiceDate = EditInvoiceDate,
            VoucherType = EditVoucherType,
            Memo = EditMemo,
            Lines = lines.Select(l => l.ToLocal(EditInvoiceId)).ToList()
        };

        var saveContext = new InvoiceSaveContext
        {
            Username = _session.User?.Username ?? "local-user",
            Role = _session.User?.Role ?? DomainConstants.RoleUser,
            OfficeCode = _session.OfficeCode,
            ForceOverride = false,
            ExpectedConcurrencyStamp = string.IsNullOrWhiteSpace(_editConcurrencyStamp)
                ? null
                : _editConcurrencyStamp
        };

        var saveResult = await _local.SaveInvoiceAsync(inv, saveContext, _session);
        if (!saveResult.Success)
        {
            System.Windows.MessageBox.Show(
                saveResult.Message,
                saveResult.ConcurrencyConflict
                    ? "동시 수정 충돌"
                    : saveResult.PermissionDenied ? "권한 없음" : "저장 실패",
                System.Windows.MessageBoxButton.OK,
                saveResult.ConcurrencyConflict || saveResult.PermissionDenied
                    ? System.Windows.MessageBoxImage.Warning
                    : System.Windows.MessageBoxImage.Error);
            return;
        }

        await _local.WaitForServerWriteAsync();
        _editConcurrencyStamp = saveResult.SavedConcurrencyStamp;
        await LoadInvoiceListAsync();
        System.Windows.MessageBox.Show("저장되었습니다.", "알림", System.Windows.MessageBoxButton.OK);
    }

    [RelayCommand]
    private async Task DeleteInvoiceAsync()
    {
        if (SelectedInvoiceRow is null) return;
        var confirm = System.Windows.MessageBox.Show(
            "선택한 전표를 삭제하시겠습니까?", "삭제 확인",
            System.Windows.MessageBoxButton.YesNo);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        var result = await _local.DeleteInvoiceAsync(SelectedInvoiceRow.Id, _session);
        if (!result.Success)
        {
            System.Windows.MessageBox.Show(
                result.Message,
                result.PermissionDenied ? "권한 없음" : "삭제 실패",
                System.Windows.MessageBoxButton.OK,
                result.PermissionDenied
                    ? System.Windows.MessageBoxImage.Warning
                    : System.Windows.MessageBoxImage.Error);
            return;
        }

        await _local.WaitForServerWriteAsync();
        await LoadInvoiceListAsync();
    }

    // ?? Lines ??????????????????????????????????????????????????????????????
    [RelayCommand]
    private void AddNewLine()
    {
        EditLines.Add(new InvoiceLineEditModel());
        RecalcTotals();
    }

    [RelayCommand]
    private void RemoveLine(InvoiceLineEditModel? line)
    {
        if (line is null) return;
        EditLines.Remove(line);
        RecalcTotals();
    }

    public void RecalcTotals()
    {
        EditTotalAmount = EditLines.Sum(l => l.LineAmount);
        EditSupplyAmount = Math.Round(EditTotalAmount / 1.1m, 0, MidpointRounding.AwayFromZero);
        EditVatAmount = EditTotalAmount - EditSupplyAmount;
    }

    // ?? Payments ??????????????????????????????????????????????????????????
    [RelayCommand]
    private async Task LoadPaymentsAsync()
    {
        if (SelectedInvoiceRow is null) return;
        PaymentInvoice = SelectedInvoiceRow;

        var inv = await _local.GetInvoiceAsync(SelectedInvoiceRow.Id, _session);
        if (inv is null) return;

        PaymentRows.Clear();
        foreach (var p in inv.Payments.Where(p => !p.IsDeleted))
            PaymentRows.Add(PaymentRowModel.FromLocal(p));

        RecalcPaymentTotals(inv);
        SelectedTabIndex = 1; // ?섍툑 ?낅젰 ??(?꾪몴?묒꽦 ???쒓굅 ??
    }

    [RelayCommand]
    private void AddPaymentRow()
    {
        if (PaymentInvoice is null) return;
        PaymentRows.Add(new PaymentRowModel { InvoiceId = PaymentInvoice.Id });
    }

    [RelayCommand]
    private async Task SavePaymentsAsync()
    {
        if (PaymentInvoice is null) return;

        if (PaymentRows.Any(row => row.Amount < 0))
        {
            System.Windows.MessageBox.Show("수금 금액은 0 이상으로 입력하세요.", "알림", System.Windows.MessageBoxButton.OK);
            return;
        }

        var targetInvoice = await _local.GetInvoiceAsync(PaymentInvoice.Id, _session);
        if (targetInvoice is null)
            return;

        var inputTotal = PaymentRows.Sum(row => row.Amount);
        if (inputTotal > targetInvoice.TotalAmount)
        {
            var proceed = System.Windows.MessageBox.Show(
                "입력한 수금 합계가 전표 합계를 초과합니다. 계속 저장할까요?",
                "수금 검증",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (proceed != System.Windows.MessageBoxResult.Yes)
                return;
        }

        foreach (var row in PaymentRows)
        {
            if (row.Amount == 0) continue;
            row.InvoiceId = PaymentInvoice.Id;
            var result = await _local.SavePaymentAsync(row.ToLocal(), _session);
            if (!result.Success)
            {
                System.Windows.MessageBox.Show(
                    result.Message,
                    result.PermissionDenied ? "권한 없음" : "저장 실패",
                    System.Windows.MessageBoxButton.OK,
                    result.PermissionDenied
                        ? System.Windows.MessageBoxImage.Warning
                        : System.Windows.MessageBoxImage.Error);
                return;
            }

            await _local.WaitForServerWriteAsync();
        }

        var inv = await _local.GetInvoiceAsync(PaymentInvoice.Id, _session);
        if (inv is not null) RecalcPaymentTotals(inv);
        await LoadInvoiceListAsync();
        System.Windows.MessageBox.Show("수금이 저장되었습니다.", "알림", System.Windows.MessageBoxButton.OK);
    }

    private void RecalcPaymentTotals(LocalInvoice inv)
    {
        PaymentTotalPaid = PaymentRows.Sum(p => p.Amount);
        PaymentBalance = inv.TotalAmount - PaymentTotalPaid;
    }

    // ?? Statement Print (F9) ?????????????????????????????????????????????
    [RelayCommand]
    private async Task PrintStatementAsync()
    {
        try
        {
            var target = StatementInvoice ?? SelectedInvoiceRow;
            if (target is null)
            {
                System.Windows.MessageBox.Show("출력할 전표를 선택하세요.", "알림", System.Windows.MessageBoxButton.OK);
                return;
            }

            var inv = await _local.GetInvoiceAsync(target.Id, _session);
            var company = await _local.GetCompanyProfileAsync(_session);

            if (inv is null || company is null)
            {
                System.Windows.MessageBox.Show("전표 또는 회사 정보가 없습니다.", "오류", System.Windows.MessageBoxButton.OK);
                return;
            }

            var customer = _allCustomers.FirstOrDefault(c => c.Id == inv.CustomerId)
                ?? await _local.GetCustomerAsync(inv.CustomerId);
            if (customer is null)
            {
                System.Windows.MessageBox.Show("거래처 정보를 찾을 수 없습니다.", "오류", System.Windows.MessageBoxButton.OK);
                return;
            }

            var printModel = await LoadOrCreateInvoicePrintModelAsync(
                inv,
                customer,
                company,
                printWithDate: true,
                printWithPrice: true);
            var previewDocument = _invoicePrintService.BuildFixedDocument(printModel);
            var printDocumentName = inv.VoucherType switch
            {
                VoucherType.Purchase => "매입 명세서",
                VoucherType.Procurement => string.IsNullOrWhiteSpace(printModel.DocumentTitle) ? "발주서" : printModel.DocumentTitle,
                _ => "거래명세서"
            };
            var previewViewModel = new PrintPreviewViewModel(
                previewDocument,
                _invoicePrintService,
                $"{printDocumentName}_{inv.InvoiceDate:yyyyMMdd}_{customer.NameOriginal}");
            var previewWindow = new PrintPreviewWindow(previewViewModel)
            {
                Owner = GetActiveWindow()
            };

            previewWindow.ShowDialog();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"전표 인쇄 중 오류가 발생했습니다.\n{ex.Message}",
                "오류",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    private async Task<InvoicePrintModel> LoadOrCreateInvoicePrintModelAsync(
        LocalInvoice invoice,
        LocalCustomer customer,
        LocalCompanyProfile company,
        bool printWithDate,
        bool printWithPrice)
    {
        var payload = await _local.GetInvoicePrintPayloadAsync(invoice.Id);
        if (!string.IsNullOrWhiteSpace(payload))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<InvoicePrintModel>(payload, PrintModelJsonOptions);
                if (saved is not null)
                {
                    saved.InvoiceId = invoice.Id;
                    saved.PrintWithDate = printWithDate;
                    saved.PrintWithPrice = printWithPrice;
                    if (invoice.VoucherType == VoucherType.Procurement)
                        saved.DocumentTitle = saved.DocumentTitle is "납품서" or "의뢰서" ? saved.DocumentTitle : "발주서";

                    if (saved.Lines.Count == 0)
                    {
                        saved.Lines = _invoicePrintService
                            .CreateDefaultModel(invoice, customer, company, printWithDate, printWithPrice)
                            .Lines;
                    }

                    return saved;
                }
            }
            catch
            {
                // Corrupted payload falls back to default model.
            }
        }

        var model = _invoicePrintService.CreateDefaultModel(invoice, customer, company, printWithDate, printWithPrice);
        if (invoice.VoucherType == VoucherType.Procurement)
            model.DocumentTitle = model.DocumentTitle is "납품서" or "의뢰서" ? model.DocumentTitle : "발주서";
        return model;
    }

    private static Window? GetActiveWindow()
    {
        return Application.Current?.Windows
            .OfType<Window>()
            .FirstOrDefault(window => window.IsActive);
    }
// ?? Company Settings ??????????????????????????????????????????????????
    private async Task LoadCompanyProfileAsync()
    {
        var profile = await _local.GetCompanyProfileAsync(_session);
        if (profile is null) return;

        _companyProfileId = profile.Id;
        CompanyTradeName = profile.TradeName;
        CompanyRepresentative = profile.Representative;
        CompanyBusinessNumber = profile.BusinessNumber;
        CompanyBusinessType = profile.BusinessType;
        CompanyBusinessItem = profile.BusinessItem;
        CompanyAddress = profile.Address;
        CompanyContactNumber = profile.ContactNumber;
        CompanyEmail = profile.Email;
        CompanyBankAccountText = profile.BankAccountText;
        CompanyStampImage = profile.StampImage;
        CompanyStampImagePath = profile.StampImage is { Length: > 0 } ? "(?대?吏 ?덉쓬)" : "(?놁쓬)";
    }

    [RelayCommand]
    private async Task SaveCompanyProfileAsync()
    {
        if (!_session.HasPermission("CompanyProfile.Edit")
            && _session.User?.Role != "Admin")
        {
            System.Windows.MessageBox.Show("권한이 없습니다.", "오류", System.Windows.MessageBoxButton.OK);
            return;
        }

        var profile = new LocalCompanyProfile
        {
            Id = _companyProfileId,
            TradeName = CompanyTradeName,
            Representative = CompanyRepresentative,
            BusinessNumber = CompanyBusinessNumber,
            BusinessType = CompanyBusinessType,
            BusinessItem = CompanyBusinessItem,
            Address = CompanyAddress,
            ContactNumber = CompanyContactNumber,
            Email = CompanyEmail,
            BankAccountText = CompanyBankAccountText,
            StampImage = CompanyStampImage
        };

        await _local.SaveCompanyProfileAsync(profile);
        System.Windows.MessageBox.Show("회사 정보가 저장되었습니다.", "알림", System.Windows.MessageBoxButton.OK);
    }

    [RelayCommand]
    private void SelectStampImage()
    {
        var dlg = new OpenFileDialog
        {
            Title = "吏곸씤 ?대?吏 ?좏깮",
            Filter = "?대?吏 ?뚯씪|*.png;*.jpg;*.jpeg;*.bmp"
        };
        if (dlg.ShowDialog() != true) return;
        CompanyStampImage = File.ReadAllBytes(dlg.FileName);
        CompanyStampImagePath = "(?대?吏 ?덉쓬)";
    }

    [RelayCommand]
    private void ClearStampImage()
    {
        CompanyStampImage = null;
        CompanyStampImagePath = "(?놁쓬)";
    }

    private async Task LoadLegacyMigrationSettingsAsync()
    {
        var defaultDb = GetDefaultLegacySourceDbPath();
        var defaultCustomerExcel = Path.Combine(AppContext.BaseDirectory, "거래처 목록.xlsx");
        var defaultItemExcel = Path.Combine(AppContext.BaseDirectory, "제품 목록.xlsx");

        LegacySourceDbPath = await _local.GetSettingAsync(LegacySourceDbPathSettingKey) ?? defaultDb;
        LegacyCustomerExcelPath = await _local.GetSettingAsync(LegacyCustomerExcelPathSettingKey) ?? defaultCustomerExcel;
        LegacyItemExcelPath = await _local.GetSettingAsync(LegacyItemExcelPathSettingKey) ?? defaultItemExcel;

        if (string.IsNullOrWhiteSpace(LegacySourceDbPath))
            LegacySourceDbPath = defaultDb;
        if (string.IsNullOrWhiteSpace(LegacyCustomerExcelPath))
            LegacyCustomerExcelPath = defaultCustomerExcel;
        if (string.IsNullOrWhiteSpace(LegacyItemExcelPath))
            LegacyItemExcelPath = defaultItemExcel;
    }

    private async Task PersistLegacyMigrationSettingsAsync()
    {
        await _local.SetSettingAsync(LegacySourceDbPathSettingKey, LegacySourceDbPath ?? string.Empty);
        await _local.SetSettingAsync(LegacyCustomerExcelPathSettingKey, LegacyCustomerExcelPath ?? string.Empty);
        await _local.SetSettingAsync(LegacyItemExcelPathSettingKey, LegacyItemExcelPath ?? string.Empty);
    }

    private static string GetDefaultLegacySourceDbPath()
    {
        var candidate = @"C:\LegacyVendor\LegacySalesApp\DATA\SALE_ACE_DATA.FDB";
        if (File.Exists(candidate))
            return candidate;
        return string.Empty;
    }

    [RelayCommand]
    private async Task SelectLegacySourceDbPathAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "외부 레거시 DB(FDB) 선택",
            Filter = "Firebird DB|*.fdb|모든 파일|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() != true)
            return;

        LegacySourceDbPath = dialog.FileName;
        await PersistLegacyMigrationSettingsAsync();
    }

    [RelayCommand]
    private async Task SelectLegacyCustomerExcelPathAsync()
    {
        var initialDirectory = Path.GetDirectoryName(LegacyCustomerExcelPath);
        if (string.IsNullOrWhiteSpace(initialDirectory) || !Directory.Exists(initialDirectory))
            initialDirectory = AppContext.BaseDirectory;

        var dialog = new SaveFileDialog
        {
            Title = "거래처 추출 엑셀 경로 선택",
            Filter = "Excel 파일|*.xlsx",
            AddExtension = true,
            DefaultExt = ".xlsx",
            FileName = "거래처 목록.xlsx",
            InitialDirectory = initialDirectory
        };

        if (dialog.ShowDialog() != true)
            return;

        LegacyCustomerExcelPath = dialog.FileName;
        await PersistLegacyMigrationSettingsAsync();
    }

    [RelayCommand]
    private async Task SelectLegacyItemExcelPathAsync()
    {
        var initialDirectory = Path.GetDirectoryName(LegacyItemExcelPath);
        if (string.IsNullOrWhiteSpace(initialDirectory) || !Directory.Exists(initialDirectory))
            initialDirectory = AppContext.BaseDirectory;

        var dialog = new SaveFileDialog
        {
            Title = "제품 추출 엑셀 경로 선택",
            Filter = "Excel 파일|*.xlsx",
            AddExtension = true,
            DefaultExt = ".xlsx",
            FileName = "제품 목록.xlsx",
            InitialDirectory = initialDirectory
        };

        if (dialog.ShowDialog() != true)
            return;

        LegacyItemExcelPath = dialog.FileName;
        await PersistLegacyMigrationSettingsAsync();
    }

    [RelayCommand]
    private async Task ExportLegacyDataAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(LegacySourceDbPath) || !File.Exists(LegacySourceDbPath))
            {
                MessageBox.Show("외부 레거시 DB 경로를 먼저 확인하세요.", "알림", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (string.IsNullOrWhiteSpace(LegacyCustomerExcelPath) || string.IsNullOrWhiteSpace(LegacyItemExcelPath))
            {
                MessageBox.Show("거래처/제품 엑셀 경로를 먼저 지정하세요.", "알림", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            LegacyMigrationStatus = "외부 레거시 데이터를 엑셀로 추출 중...";
            var result = await _legacyMigrationService.ExportFromOriginalAsync(
                LegacySourceDbPath,
                LegacyCustomerExcelPath,
                LegacyItemExcelPath);

            await PersistLegacyMigrationSettingsAsync();

            LegacyMigrationStatus = $"추출 완료: 거래처 {result.CustomerCount:N0}건, 제품 {result.ItemCount:N0}건";
            MessageBox.Show(
                $"추출 완료\n거래처: {result.CustomerCount:N0}건\n제품: {result.ItemCount:N0}건\n\n{result.CustomerExcelPath}\n{result.ItemExcelPath}",
                "데이터 추출",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            LegacyMigrationStatus = $"추출 실패: {ex.Message}";
            MessageBox.Show($"데이터 추출 중 오류가 발생했습니다.\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task ImportLegacyExcelDataAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(LegacyCustomerExcelPath) || !File.Exists(LegacyCustomerExcelPath))
            {
                MessageBox.Show("거래처 엑셀 파일 경로를 확인하세요.", "알림", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (string.IsNullOrWhiteSpace(LegacyItemExcelPath) || !File.Exists(LegacyItemExcelPath))
            {
                MessageBox.Show("제품 엑셀 파일 경로를 확인하세요.", "알림", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            LegacyMigrationStatus = "엑셀 데이터를 거래플랜으로 가져오는 중...";
            var result = await _legacyMigrationService.ImportFromExcelAsync(
                LegacyCustomerExcelPath,
                LegacyItemExcelPath);

            await PersistLegacyMigrationSettingsAsync();
            await LoadCustomersAsync();
            await LoadInvoiceListAsync();

            LegacyMigrationStatus =
                $"가져오기 완료: 거래처 +{result.CreatedCustomers:N0}/수정 {result.UpdatedCustomers:N0}, " +
                $"제품 +{result.CreatedItems:N0}/수정 {result.UpdatedItems:N0}";

            MessageBox.Show(
                $"가져오기 완료\n" +
                $"거래처: 신규 {result.CreatedCustomers:N0}, 수정 {result.UpdatedCustomers:N0}, 건너뜀 {result.SkippedCustomers:N0}\n" +
                $"제품: 신규 {result.CreatedItems:N0}, 수정 {result.UpdatedItems:N0}, 건너뜀 {result.SkippedItems:N0}",
                "데이터 가져오기",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            LegacyMigrationStatus = $"가져오기 실패: {ex.Message}";
            MessageBox.Show($"데이터 가져오기 중 오류가 발생했습니다.\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task ExportAndImportLegacyDataAsync()
    {
        await ExportLegacyDataAsync();
        if (!LegacyMigrationStatus.StartsWith("추출 완료", StringComparison.Ordinal))
            return;
        await ImportLegacyExcelDataAsync();
    }

    // ?? Refresh Customers (嫄곕옒泥??깅줉/?섏젙 ??媛깆떊) ??????????????????????
    [RelayCommand]
    public async Task RefreshCustomersAsync()
    {
        await LoadCustomersAsync();
        await LoadInvoiceListAsync();
    }

    // ?? Sync ??????????????????????????????????????????????????????????????
    public async Task ReloadAfterPassiveSyncAsync()
    {
        await LoadCustomersAsync();
        await LoadInvoiceListAsync();
    }

    [RelayCommand]
    private async Task ForceSyncAsync()
    {
        await _sync.TrySyncAsync();
        if (await _local.CountDirtyAsync() == 0)
            await _sync.RefreshSharedMirrorFromServerAsync();
        await LoadCustomersAsync();
        await LoadInvoiceListAsync();
    }

    // ?? Backup ???????????????????????????????????????????????????????????
    [RelayCommand]
    private async Task BackupNowAsync()
    {
        var ok = await _backup.BackupNowAsync();
        System.Windows.MessageBox.Show(
            ok ? "백업이 완료되었습니다." : "백업 중 오류가 발생했습니다.",
            "백업", System.Windows.MessageBoxButton.OK);
    }
}
