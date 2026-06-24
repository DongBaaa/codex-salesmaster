using System.Collections.ObjectModel;
using System.Collections.Specialized;
using GeoraePlan.Mobile.App.Models;
using GeoraePlan.Mobile.App.Services;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.ViewModels;

public sealed class InvoiceDraftViewModel : ObservableObject
{
    private readonly GeoraePlanApiClient _api;
    private readonly SyncCoordinator _syncCoordinator;
    private readonly MobileRefreshCoordinator _refreshCoordinator;
    private readonly SessionStore _sessionStore;
    private readonly RecentItemSelectionStore _recentItemSelectionStore;
    private readonly MobileInvoicePdfExportService _pdfExportService;
    private readonly List<RecentItemSelectionRecord> _recentSelections = new();
    private bool _hasLoadedRecentSelections;
    private int _recentSelectionReloadVersion;
    private bool _isRefreshingSourceWarehouseOptions;

    private CustomerDto? _selectedCustomer;
    private ItemCategorySummaryDto? _selectedCategory;
    private ItemDto? _selectedItem;
    private InvoiceDto? _editingInvoice;
    private DateTime _invoiceDate = DateTime.Today;
    private string _customerSearchText = string.Empty;
    private string _itemSearchText = string.Empty;
    private string _lineQuantityText = "1";
    private string _lineUnitPriceText = "0";
    private string _lineRemark = string.Empty;
    private string _memo = string.Empty;
    private string _statusMessage = "1단계 거래처 선택 → 2단계 품목 추가 → 마지막 저장 카드에서 전표 저장 순서로 입력하세요.";
    private bool _isBusy;
    private bool _isLoaded;
    private bool _isItemEntrySheetVisible;
    private bool _isCategoryChooserExpanded;
    private VoucherType _voucherType = VoucherType.Sales;
    private Guid? _editingLineId;
    private MobileOfficeOption? _selectedInvoiceOffice;
    private MobileWarehouseOption? _selectedSourceWarehouse;
    private bool _isVatNone;
    private readonly Dictionary<string, string> _priceGradeSourceMap = new(StringComparer.CurrentCultureIgnoreCase);
    private string _sessionOfficeCode = OfficeCodeCatalog.Usenet;
    private string _sessionTenantCode = TenantScopeCatalog.UsenetGroup;
    private string _sessionUsername = string.Empty;
    private bool _printStatementDocument = true;
    private bool _printEstimateDocument;
    private bool _printPaymentClaimDocument;
    private bool _printDate = true;
    private bool _printUnitPrice = true;

    public InvoiceDraftViewModel(
        GeoraePlanApiClient api,
        SyncCoordinator syncCoordinator,
        MobileRefreshCoordinator refreshCoordinator,
        SessionStore sessionStore,
        RecentItemSelectionStore recentItemSelectionStore,
        MobileInvoicePdfExportService pdfExportService)
    {
        _api = api;
        _syncCoordinator = syncCoordinator;
        _refreshCoordinator = refreshCoordinator;
        _sessionStore = sessionStore;
        _recentItemSelectionStore = recentItemSelectionStore;
        _pdfExportService = pdfExportService;
        LoadCommand = new AsyncCommand(LoadAsync);
        SaveDraftCommand = new AsyncCommand(SaveDraftAsync);
        ExportPdfCommand = new AsyncCommand(ExportPdfAsync);

        CustomerSearchResults.CollectionChanged += HandleCollectionChanged;
        ItemSearchResults.CollectionChanged += HandleCollectionChanged;
        SelectedItemBranchStocks.CollectionChanged += HandleCollectionChanged;
        LineItems.CollectionChanged += HandleCollectionChanged;
        VisibleRecentItems.CollectionChanged += HandleCollectionChanged;
    }

    public event Func<Task>? SavedSuccessfully;

    public ObservableCollection<CustomerDto> CustomerSearchResults { get; } = new();
    public ObservableCollection<ItemCategorySummaryDto> ItemCategories { get; } = new();
    public ObservableCollection<ItemDto> ItemSearchResults { get; } = new();
    public ObservableCollection<ItemWarehouseStockDto> SelectedItemBranchStocks { get; } = new();
    public ObservableCollection<InvoiceLineDraftItem> LineItems { get; } = new();
    public ObservableCollection<MobileOfficeOption> InvoiceOfficeOptions { get; } = new();
    public ObservableCollection<MobileWarehouseOption> SourceWarehouseOptions { get; } = new();
    public ObservableCollection<RecentItemSelectionRecord> VisibleRecentItems { get; } = new();

    public VoucherType VoucherType
    {
        get => _voucherType;
        set
        {
            var normalized = value switch
            {
                VoucherType.Purchase => VoucherType.Purchase,
                VoucherType.Procurement => VoucherType.Procurement,
                _ => VoucherType.Sales
            };
            if (!SetProperty(ref _voucherType, normalized))
                return;

            OnPropertyChanged(nameof(IsPurchaseDocument));
            OnPropertyChanged(nameof(IsProcurementDocument));
            OnPropertyChanged(nameof(IsPurchaseLikeDocument));
            OnPropertyChanged(nameof(IsSalesDocument));
            OnPropertyChanged(nameof(PageTitleText));
            OnPropertyChanged(nameof(DocumentKindText));
            OnPropertyChanged(nameof(CustomerSearchSectionTitle));
            OnPropertyChanged(nameof(CustomerNameLabelText));
            OnPropertyChanged(nameof(WarehouseLabelText));
            OnPropertyChanged(nameof(WarehousePickerTitleText));
            OnPropertyChanged(nameof(SelectedSourceWarehouseSummary));
            OnPropertyChanged(nameof(DocumentSaveSectionTitle));
            OnPropertyChanged(nameof(SaveButtonText));
            OnPropertyChanged(nameof(VatSummary));
            OnPropertyChanged(nameof(SelectedItemPriceSummary));

            if (SelectedItem is not null && !_editingLineId.HasValue)
                LineUnitPriceText = ResolveDefaultUnitPrice(SelectedItem).ToString("0.##");
        }
    }

    public CustomerDto? SelectedCustomer
    {
        get => _selectedCustomer;
        private set
        {
            if (SetProperty(ref _selectedCustomer, value))
            {
                OnPropertyChanged(nameof(SelectedCustomerSummary));
                OnPropertyChanged(nameof(HasSelectedCustomer));
            }
        }
    }

    public ItemCategorySummaryDto? SelectedCategory
    {
        get => _selectedCategory;
        private set
        {
            if (SetProperty(ref _selectedCategory, value))
            {
                OnPropertyChanged(nameof(HasSelectedCategory));
                OnPropertyChanged(nameof(IsCategoryChooserVisible));
                OnPropertyChanged(nameof(SelectedCategoryHeader));
                OnPropertyChanged(nameof(SelectedCategorySummary));
                OnPropertyChanged(nameof(ItemListCaptionText));
                RefreshVisibleRecentItems();
            }
        }
    }

    public ItemDto? SelectedItem
    {
        get => _selectedItem;
        private set
        {
            if (SetProperty(ref _selectedItem, value))
            {
                OnPropertyChanged(nameof(HasSelectedItem));
                OnPropertyChanged(nameof(SelectedItemSheetTitle));
                OnPropertyChanged(nameof(SelectedItemSheetSpecification));
                OnPropertyChanged(nameof(SelectedItemIdentitySummary));
                OnPropertyChanged(nameof(SelectedItemPriceSummary));
                OnPropertyChanged(nameof(SelectedItemMemo));
                OnPropertyChanged(nameof(SelectedItemStockSummary));
            }
        }
    }

    public MobileOfficeOption? SelectedInvoiceOffice
    {
        get => _selectedInvoiceOffice;
        set
        {
            var previousOfficeCode = SelectedInvoiceOfficeCode;
            var previousWarehouseCode = SelectedSourceWarehouseCode;
            if (SetProperty(ref _selectedInvoiceOffice, value))
            {
                OnPropertyChanged(nameof(SelectedInvoiceOfficeCode));
                OnPropertyChanged(nameof(SelectedInvoiceOfficeSummary));
                RefreshSourceWarehouseOptions();
                HandleInvoiceScopeChanged(previousOfficeCode, previousWarehouseCode);
            }
        }
    }

    public MobileWarehouseOption? SelectedSourceWarehouse
    {
        get => _selectedSourceWarehouse;
        set
        {
            var previousWarehouseCode = SelectedSourceWarehouseCode;
            if (SetProperty(ref _selectedSourceWarehouse, value))
            {
                OnPropertyChanged(nameof(SelectedSourceWarehouseCode));
                OnPropertyChanged(nameof(SelectedSourceWarehouseSummary));
                if (!_isRefreshingSourceWarehouseOptions)
                    HandleInvoiceScopeChanged(SelectedInvoiceOfficeCode, previousWarehouseCode);
            }
        }
    }

    public bool IsVatNone
    {
        get => _isVatNone;
        set
        {
            if (SetProperty(ref _isVatNone, value))
                OnPropertyChanged(nameof(VatSummary));
        }
    }

    public DateTime InvoiceDate
    {
        get => _invoiceDate;
        set => SetProperty(ref _invoiceDate, value);
    }

    public string CustomerSearchText
    {
        get => _customerSearchText;
        set => SetProperty(ref _customerSearchText, value);
    }

    public string ItemSearchText
    {
        get => _itemSearchText;
        set => SetProperty(ref _itemSearchText, value);
    }

    public string LineQuantityText
    {
        get => _lineQuantityText;
        set => SetProperty(ref _lineQuantityText, value);
    }

    public string LineUnitPriceText
    {
        get => _lineUnitPriceText;
        set => SetProperty(ref _lineUnitPriceText, value);
    }

    public string LineRemark
    {
        get => _lineRemark;
        set => SetProperty(ref _lineRemark, value);
    }

    public string Memo
    {
        get => _memo;
        set => SetProperty(ref _memo, value);
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

    public bool CanCreateInvoices => _sessionStore.GetSnapshot().CanCreateInvoices;

    public bool IsItemEntrySheetVisible
    {
        get => _isItemEntrySheetVisible;
        private set => SetProperty(ref _isItemEntrySheetVisible, value);
    }

    public bool IsCategoryChooserExpanded
    {
        get => _isCategoryChooserExpanded;
        private set
        {
            if (SetProperty(ref _isCategoryChooserExpanded, value))
                OnPropertyChanged(nameof(IsCategoryChooserVisible));
        }
    }

    public bool PrintStatementDocument
    {
        get => _printStatementDocument;
        set => SetProperty(ref _printStatementDocument, value);
    }

    public bool PrintEstimateDocument
    {
        get => _printEstimateDocument;
        set => SetProperty(ref _printEstimateDocument, value);
    }

    public bool PrintPaymentClaimDocument
    {
        get => _printPaymentClaimDocument;
        set => SetProperty(ref _printPaymentClaimDocument, value);
    }

    public bool PrintDate
    {
        get => _printDate;
        set => SetProperty(ref _printDate, value);
    }

    public bool PrintUnitPrice
    {
        get => _printUnitPrice;
        set => SetProperty(ref _printUnitPrice, value);
    }

    public bool IsEditMode => _editingInvoice is not null;
    public bool HasSelectedCustomer => SelectedCustomer is not null;
    public bool HasSelectedCategory => SelectedCategory is not null;
    public bool IsCategoryChooserVisible => IsCategoryChooserExpanded;
    public bool HasSelectedItem => SelectedItem is not null;
    public bool IsPurchaseDocument => VoucherType == VoucherType.Purchase;
    public bool IsProcurementDocument => VoucherType == VoucherType.Procurement;
    public bool IsPurchaseLikeDocument => MobileVoucherTypeRules.IsPaymentVoucher(VoucherType);
    public bool IsSalesDocument => VoucherType == VoucherType.Sales;
    public bool HasVisibleRecentItems => VisibleRecentItems.Count > 0;
    public bool HasItemSearchResults => ItemSearchResults.Count > 0;
    public bool CanChooseInvoiceOffice => InvoiceOfficeOptions.Count > 1;
    public bool CanChooseSourceWarehouse => SourceWarehouseOptions.Count > 1;
    public string PageTitleText => (IsEditMode, VoucherType) switch
    {
        (true, VoucherType.Purchase) => "구매(매입) 전표 수정",
        (true, VoucherType.Procurement) => "발주 전표 수정",
        (false, VoucherType.Purchase) => "구매(매입) 작성",
        (false, VoucherType.Procurement) => "발주 작성",
        _ => IsEditMode ? "판매(매출) 전표 수정" : "판매(매출) 작성"
    };
    public string DocumentKindText => VoucherType switch
    {
        VoucherType.Purchase => "구매(매입)",
        VoucherType.Procurement => "발주",
        _ => "판매(매출)"
    };
    public string CustomerSearchSectionTitle => IsPurchaseLikeDocument ? "1단계 · 거래처 찾기" : "1단계 · 고객/거래처 찾기";
    public string CustomerNameLabelText => IsPurchaseLikeDocument ? "거래처" : "고객/거래처";
    public string WarehouseLabelText => IsPurchaseLikeDocument ? "입고창고" : "출고창고";
    public string WarehousePickerTitleText => $"{WarehouseLabelText} 선택";
    public string DocumentSaveSectionTitle => (IsEditMode, VoucherType) switch
    {
        (true, VoucherType.Purchase) => "마지막 단계 · 구매 전표 수정 저장",
        (true, VoucherType.Procurement) => "마지막 단계 · 발주 전표 수정 저장",
        (false, VoucherType.Purchase) => "마지막 단계 · 구매 전표 저장",
        (false, VoucherType.Procurement) => "마지막 단계 · 발주 전표 저장",
        _ => IsEditMode ? "마지막 단계 · 판매 전표 수정 저장" : "마지막 단계 · 판매 전표 저장"
    };
    public string SaveButtonText => (IsEditMode, VoucherType) switch
    {
        (true, VoucherType.Purchase) => "구매 전표 수정 저장",
        (true, VoucherType.Procurement) => "발주 전표 수정 저장",
        (false, VoucherType.Purchase) => "구매 전표 저장",
        (false, VoucherType.Procurement) => "발주 전표 저장",
        _ => IsEditMode ? "판매 전표 수정 저장" : "판매 전표 저장"
    };
    public string SelectedInvoiceOfficeCode => SelectedInvoiceOffice?.Code ?? _sessionOfficeCode;
    public string SelectedSourceWarehouseCode => SelectedSourceWarehouse?.Code ?? OfficeCodeCatalog.GetMainWarehouseCode(SelectedInvoiceOfficeCode);
    public string SelectedInvoiceOfficeSummary => SelectedInvoiceOffice is null
        ? "전표 담당지점을 선택하세요."
        : $"선택 담당지점: {SelectedInvoiceOffice.DisplayName}";
    public string SelectedSourceWarehouseSummary => SelectedSourceWarehouse is null
        ? $"{WarehouseLabelText}를 선택하세요."
        : $"{WarehouseLabelText}: {SelectedSourceWarehouse.DisplayName}";
    public string SelectedCustomerSummary => SelectedCustomer is null
        ? "거래처를 아직 선택하지 않았습니다."
        : $"선택 거래처: {SelectedCustomer.NameOriginal}";
    public string SelectedCategoryHeader => SelectedCategory is null
        ? "품목분류를 선택하세요."
        : $"선택 분류: {SelectedCategory.Name}";
    public string SelectedCategorySummary => SelectedCategory is null
        ? ItemSearchResults.Count == 0 ? "품명/규격으로 바로 검색하거나 품목찾기에서 분류를 선택하세요." : $"전체 검색 품목 {ItemSearchResults.Count:N0}건"
        : $"해당 분류 품목 {ItemSearchResults.Count:N0}건";
    public string ItemListCaptionText => SelectedCategory is null
        ? $"검색 품목 {ItemSearchResults.Count:N0}건"
        : SelectedCategorySummary;
    public string SelectedItemSheetTitle => SelectedItem is null
        ? "선택 품목"
        : $"선택 품목: {SelectedItem.NameOriginal}";
    public string SelectedItemSheetSpecification => SelectedItem is null
        ? "규격 정보 없음"
        : string.IsNullOrWhiteSpace(SelectedItem.SpecificationOriginal)
            ? "규격 정보 없음"
            : $"규격: {SelectedItem.SpecificationOriginal}";
    public string SelectedItemIdentitySummary => BuildItemIdentitySummary(SelectedItem);
    public string SelectedItemPriceSummary => SelectedItem is null
        ? "단가 정보 없음"
        : IsPurchaseLikeDocument
            ? $"매입 기준 {SelectedItem.PurchasePrice:N0}원 / 판매 {SelectedItem.SalePrice:N0}원 / 소매 {SelectedItem.RetailPrice:N0}원"
            : $"판매 기준 {ResolveDefaultUnitPrice(SelectedItem):N0}원 / 매입 {SelectedItem.PurchasePrice:N0}원 / 소매 {SelectedItem.RetailPrice:N0}원";
    public string SelectedItemMemo => SelectedItem is null
        ? "전표 비고는 직접 입력하세요."
        : "품목 메모는 전표 비고에 자동 입력하지 않습니다.";
    public string SelectedItemStockSummary => SelectedItem is null
        ? "재고 정보 없음"
        : $"현재재고 {SelectedItem.CurrentStock:N0} / 안전재고 {SelectedItem.SafetyStock:N0}";
    public string LineActionText => _editingLineId.HasValue ? "품목 수정" : "품목 추가";
    public string DraftSummary => LineItems.Count == 0
        ? "추가된 품목이 없습니다."
        : $"총 {LineItems.Count:N0}건 / 합계 {LineItems.Sum(x => x.LineAmount):N0}원";
    public string VatSummary
    {
        get
        {
            var totals = CalculateTotals(LineItems.Select(line => line.LineAmount));
            var modeText = IsVatNone ? "부가세 없음" : "부가세 포함";
            return $"{modeText} / 공급가 {totals.SupplyAmount:N0}원 / 부가세 {totals.VatAmount:N0}원 / 합계 {totals.TotalAmount:N0}원";
        }
    }
    public double CustomerSearchResultsHeight => CalculateListHeight(CustomerSearchResults.Count, 56, 42, 2);
    public double ItemSearchResultsHeight => CalculateListHeight(ItemSearchResults.Count, 112, 48, 4);
    public double SelectedItemBranchStocksHeight => CalculateListHeight(SelectedItemBranchStocks.Count, 32, 40, 4);
    public double LineItemsHeight => CalculateListHeight(LineItems.Count, 74, 42, 3);

    public AsyncCommand LoadCommand { get; }
    public AsyncCommand SaveDraftCommand { get; }
    public AsyncCommand ExportPdfCommand { get; }

    public void ConfigureVoucherType(VoucherType voucherType)
        => VoucherType = voucherType;

    public async Task LoadExistingInvoiceAsync(InvoiceDto invoice)
    {
        if (invoice is null)
            return;

        await LoadAsync();

        if (!MobileSessionScopeFilter.CanAccessInvoice(_sessionStore.GetSnapshot(), invoice))
        {
            StatusMessage = "선택한 전표는 현재 로그인 담당지점/업체 범위 밖입니다.";
            return;
        }

        _editingInvoice = invoice;
        OnPropertyChanged(nameof(IsEditMode));
        ConfigureVoucherType(invoice.VoucherType);
        InvoiceDate = invoice.InvoiceDate == default
            ? DateTime.Today
            : invoice.InvoiceDate.ToDateTime(TimeOnly.MinValue);
        Memo = invoice.Memo;
        IsVatNone = string.Equals(InvoiceVatModes.Normalize(invoice.VatMode), InvoiceVatModes.None, StringComparison.OrdinalIgnoreCase);

        var customer = invoice.CustomerId == Guid.Empty ? null : await _api.GetCustomerByIdAsync(invoice.CustomerId);
        SelectedCustomer = customer ?? new CustomerDto
        {
            Id = invoice.CustomerId,
            NameOriginal = string.IsNullOrWhiteSpace(invoice.CustomerName) ? "거래처 정보 없음" : invoice.CustomerName,
            ResponsibleOfficeCode = invoice.ResponsibleOfficeCode,
            OfficeCode = invoice.OfficeCode,
            TenantCode = invoice.TenantCode
        };
        CustomerSearchText = SelectedCustomer.NameOriginal;

        SelectInvoiceOfficeByCode(invoice.ResponsibleOfficeCode, invoice.OfficeCode);
        SelectSourceWarehouseByCode(invoice.SourceWarehouseCode);

        LineItems.Clear();
        foreach (var line in invoice.Lines
                     .Where(line => !line.IsDeleted)
                     .OrderBy(line => line.OrderIndex > 0 ? line.OrderIndex : int.MaxValue)
                     .ThenBy(line => line.Id))
            LineItems.Add(InvoiceLineDraftItem.FromDto(line));

        OnPropertyChanged(nameof(PageTitleText));
        OnPropertyChanged(nameof(DocumentSaveSectionTitle));
        OnPropertyChanged(nameof(SaveButtonText));
        OnPropertyChanged(nameof(DraftSummary));
        OnPropertyChanged(nameof(VatSummary));
        StatusMessage = $"{DocumentKindText} 전표를 수정 중입니다. 필요한 항목을 바꾼 뒤 마지막 저장 카드에서 저장하세요.";
    }

    public async Task LoadAsync()
    {
        if (IsBusy)
            return;

        if (!CanCreateInvoices)
        {
            StatusMessage = "권한이 없어 전표를 작성/수정할 수 없습니다.";
            return;
        }

        try
        {
            IsBusy = true;
            InitializeOfficeOptions();
            var syncState = await _syncCoordinator.LoadAsync();
            _ = RefreshSyncSnapshotInBackgroundAsync();
            RefreshPriceGradeSourceMap(syncState.SyncedPriceGradeOptions);
            await LoadRecentSelectionsAsync();

            if (_isLoaded)
                return;

            StatusMessage = $"{DocumentKindText} 전표 작성에 필요한 분류 정보를 불러오고 있습니다.";
            var categories = await _api.GetItemCategoriesAsync();
            ItemCategories.Clear();
            foreach (var category in categories.OrderBy(x => x.Name))
                ItemCategories.Add(category);

            _isLoaded = true;
            StatusMessage = ItemCategories.Count == 0
                ? "등록된 품목분류가 없습니다."
                : "품명/규격 검색으로 품목을 찾거나 품목찾기 버튼에서 분류를 선택하세요.";
        }
        catch (Exception ex)
        {
            if (MobileRetryableNetworkFailure.IsRetryable(ex) &&
                await TryLoadItemCategoriesFromSyncedStateAsync($"기초 목록 불러오기 실패: {ex.Message}"))
            {
                return;
            }

            ItemCategories.Clear();
            ClearSelectedCategory();
            StatusMessage = $"기초 목록 불러오기 실패: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshSyncSnapshotInBackgroundAsync()
    {
        try
        {
            await _syncCoordinator.RefreshIfServerChangedAsync("invoice-draft-load-bg", TimeSpan.FromSeconds(15));
        }
        catch
        {
            // 전표 작성 화면 진입과 검색을 막지 않기 위한 백그라운드 보강 동기화입니다.
        }
    }

    private async Task<bool> TryLoadItemCategoriesFromSyncedStateAsync(string reason)
    {
        var state = await _syncCoordinator.LoadAsync();
        state.Normalize();

        var syncedItems = GetActiveSyncedItems(state).ToList();
        if (syncedItems.Count == 0)
            return false;

        ItemCategories.Clear();
        foreach (var category in BuildCategorySummaries(syncedItems))
            ItemCategories.Add(category);

        StatusMessage = ItemCategories.Count == 0
            ? $"{reason} / 동기화 캐시에 표시할 품목분류가 없습니다."
            : $"{reason} / 동기화 캐시 품목분류 {ItemCategories.Count:N0}개를 표시합니다.";
        return ItemCategories.Count > 0;
    }

    public async Task SearchCustomersAsync()
    {
        if (IsBusy)
            return;

        var keyword = CustomerSearchText.Trim();
        if (string.IsNullOrWhiteSpace(keyword))
        {
            StatusMessage = "거래처 검색어를 입력하세요.";
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "거래처를 찾고 있습니다.";
            var customers = FilterCustomersForSelectedInvoiceOfficeScope(await _api.GetCustomersAsync(keyword)).ToList();
            CustomerSearchResults.Clear();
            foreach (var customer in customers)
                CustomerSearchResults.Add(customer);

            StatusMessage = customers.Count == 0
                ? "조건에 맞는 거래처가 없습니다."
                : $"검색 결과 {customers.Count:N0}건";
        }
        catch (Exception ex)
        {
            if (MobileRetryableNetworkFailure.IsRetryable(ex) &&
                await TrySearchCustomersFromSyncedStateAsync(keyword, $"거래처 검색 실패: {ex.Message}"))
            {
                return;
            }

            CustomerSearchResults.Clear();
            StatusMessage = $"거래처 검색 실패: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public Task SelectCustomerAsync(CustomerDto customer)
    {
        SelectInvoiceOfficeByCustomerScope(customer);
        if (!IsCustomerInSelectedInvoiceOfficeScope(customer))
        {
            SelectedCustomer = null;
            StatusMessage = "선택한 거래처는 현재 전표 담당지점 범위 밖입니다. 담당지점 또는 거래처를 다시 선택하세요.";
            return Task.CompletedTask;
        }

        SelectedCustomer = customer;
        StatusMessage = $"{customer.NameOriginal} {CustomerNameLabelText}를 선택했습니다.";
        return Task.CompletedTask;
    }

    private async Task<bool> TrySearchCustomersFromSyncedStateAsync(string keyword, string reason)
    {
        var state = await _syncCoordinator.LoadAsync();
        state.Normalize();

        var customers = GetActiveSyncedCustomers(state)
            .Where(IsCustomerInSelectedInvoiceOfficeScope)
            .Where(customer => MatchesCustomer(customer, keyword))
            .OrderBy(customer => customer.NameOriginal, StringComparer.CurrentCultureIgnoreCase)
            .Take(50)
            .ToList();

        if (customers.Count == 0)
            return false;

        CustomerSearchResults.Clear();
        foreach (var customer in customers)
            CustomerSearchResults.Add(customer);

        StatusMessage = $"{reason} / 동기화 캐시 거래처 검색결과 {customers.Count:N0}건";
        return true;
    }

    public async Task PreselectCustomerAsync(Guid customerId, string customerName)
    {
        CustomerDto? customer = null;
        try
        {
            customer = await _api.GetCustomerByIdAsync(customerId);
            if (customer is null && !string.IsNullOrWhiteSpace(customerName))
            {
                var candidates = await _api.GetCustomersAsync(customerName);
                customer = candidates.FirstOrDefault(candidate => candidate.Id == customerId)
                    ?? candidates.FirstOrDefault();
            }
        }
        catch (Exception ex)
        {
            if (MobileRetryableNetworkFailure.IsRetryable(ex) &&
                await TryPreselectCustomerFromSyncedStateAsync(customerId, customerName, $"거래처 사전 선택 실패: {ex.Message}"))
            {
                return;
            }

            SelectedCustomer = null;
            StatusMessage = $"거래처 사전 선택 실패: {ex.Message}";
            return;
        }

        if (customer is null)
        {
            SelectedCustomer = null;
            StatusMessage = "거래처를 서버에서 찾지 못했습니다. 삭제되었거나 현재 권한/담당지점 범위 밖일 수 있습니다.";
            return;
        }

        SelectInvoiceOfficeByCustomerScope(customer);
        if (!IsCustomerInSelectedInvoiceOfficeScope(customer))
        {
            SelectedCustomer = null;
            StatusMessage = "선택한 거래처는 현재 전표 담당지점 범위 밖입니다. 담당지점 또는 거래처를 다시 선택하세요.";
            return;
        }

        SelectedCustomer = customer;
        CustomerSearchText = customer.NameOriginal;
        StatusMessage = $"{customer.NameOriginal} {CustomerNameLabelText} 기준입니다. 품목을 추가한 뒤 마지막 저장 카드에서 {DocumentKindText} 전표를 저장하세요.";
    }

    private async Task<bool> TryPreselectCustomerFromSyncedStateAsync(Guid customerId, string customerName, string reason)
    {
        var state = await _syncCoordinator.LoadAsync();
        state.Normalize();

        var customers = GetActiveSyncedCustomers(state).ToList();
        var customer = customers.FirstOrDefault(candidate => candidate.Id == customerId)
                       ?? (!string.IsNullOrWhiteSpace(customerName)
                           ? customers.FirstOrDefault(candidate => MatchesCustomer(candidate, customerName))
                           : null);

        if (customer is null)
            return false;

        SelectInvoiceOfficeByCustomerScope(customer);
        if (!IsCustomerInSelectedInvoiceOfficeScope(customer))
        {
            SelectedCustomer = null;
            StatusMessage = $"{reason} / 동기화 캐시 거래처가 현재 전표 담당지점 범위 밖이라 선택하지 않았습니다.";
            return true;
        }

        SelectedCustomer = customer;
        CustomerSearchText = customer.NameOriginal;
        StatusMessage = $"{reason} / 동기화 캐시 기준으로 {customer.NameOriginal} {CustomerNameLabelText}를 선택했습니다. 품목을 추가한 뒤 마지막 저장 카드에서 {DocumentKindText} 전표를 저장하세요.";
        return true;
    }

    public async Task SelectCategoryAsync(ItemCategorySummaryDto category, bool resetSearch = true)
    {
        if (category is null)
            return;

        if (resetSearch)
            ItemSearchText = string.Empty;

        SelectedCategory = category;
        IsCategoryChooserExpanded = false;
        ResetItemSelection(clearCategory: false);
        await SearchItemsAsync();
    }

    public void ToggleCategoryChooser()
    {
        IsCategoryChooserExpanded = !IsCategoryChooserExpanded;
        StatusMessage = IsCategoryChooserExpanded
            ? "품목분류를 선택하거나 품명/규격 검색으로 바로 찾을 수 있습니다."
            : "품명/규격 검색으로 품목을 찾으세요.";
    }

    public void ClearSelectedCategory()
    {
        SelectedCategory = null;
        ResetItemSelection(clearCategory: false);
        StatusMessage = "분류 조건을 해제했습니다. 품명/규격으로 전체 검색할 수 있습니다.";
    }

    public bool TryNavigateBackOneStep()
    {
        if (IsItemEntrySheetVisible || HasSelectedItem)
        {
            ResetItemSelection(clearCategory: false);
            StatusMessage = SelectedCategory is null
                ? "품목 선택 단계로 돌아왔습니다."
                : $"{SelectedCategory.Name} 분류 품목 목록으로 돌아왔습니다.";
            return true;
        }

        if (HasSelectedCategory)
        {
            ClearSelectedCategory();
            return true;
        }

        if (IsCategoryChooserExpanded)
        {
            IsCategoryChooserExpanded = false;
            return true;
        }

        return false;
    }

    public async Task SearchItemsAsync()
    {
        if (IsBusy)
            return;

        var keyword = ItemSearchText.Trim();
        if (SelectedCategory is null && string.IsNullOrWhiteSpace(keyword))
        {
            StatusMessage = "품명/규격 검색어를 입력하거나 품목찾기 버튼으로 분류를 선택하세요.";
            return;
        }

        try
        {
            IsBusy = true;
            var scopeText = SelectedCategory?.Name ?? "전체";
            StatusMessage = $"{scopeText} 품목을 조회하고 있습니다.";
            var items = FilterItemsForSelectedInvoiceOfficeScope(await _api.GetItemsAsync(keyword, SelectedCategory?.Name)).ToList();
            ItemSearchResults.Clear();
            foreach (var item in items.OrderBy(x => x.NameOriginal))
                ItemSearchResults.Add(item);

            StatusMessage = items.Count == 0
                ? "조건에 맞는 품목이 없습니다."
                : $"{scopeText} 품목 {items.Count:N0}건";
            OnPropertyChanged(nameof(SelectedCategorySummary));
            OnPropertyChanged(nameof(ItemListCaptionText));
        }
        catch (Exception ex)
        {
            if (MobileRetryableNetworkFailure.IsRetryable(ex) &&
                await TrySearchItemsFromSyncedStateAsync(keyword, $"품목 검색 실패: {ex.Message}"))
            {
                return;
            }

            ItemSearchResults.Clear();
            ResetItemSelection(clearCategory: false);
            StatusMessage = $"품목 검색 실패: {ex.Message}";
            OnPropertyChanged(nameof(SelectedCategorySummary));
            OnPropertyChanged(nameof(ItemListCaptionText));
        }
        finally
        {
            IsBusy = false;
        }
    }

    public Task SelectItemAsync(ItemDto item)
        => OpenItemEntrySheetAsync(item, recordRecent: true);

    private async Task<bool> TrySearchItemsFromSyncedStateAsync(string keyword, string reason)
    {
        var state = await _syncCoordinator.LoadAsync();
        state.Normalize();

        var syncedItems = GetActiveSyncedItems(state).ToList();
        if (syncedItems.Count == 0)
            return false;

        var filtered = syncedItems
            .Where(IsItemInSelectedInvoiceOfficeScope)
            .Where(item => SelectedCategory is null || CategoryEquals(item.CategoryName, SelectedCategory.Name))
            .Where(item => string.IsNullOrWhiteSpace(keyword) || MatchesItem(item, keyword))
            .OrderBy(item => item.NameOriginal, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        ItemSearchResults.Clear();
        foreach (var item in filtered)
            ItemSearchResults.Add(item);

        var scopeText = SelectedCategory?.Name ?? "전체";
        StatusMessage = filtered.Count == 0
            ? $"{reason} / 동기화 캐시 기준 조건에 맞는 품목이 없습니다."
            : $"{reason} / 동기화 캐시 {scopeText} 품목 {filtered.Count:N0}건";
        OnPropertyChanged(nameof(SelectedCategorySummary));
        OnPropertyChanged(nameof(ItemListCaptionText));
        return true;
    }

    public async Task SelectRecentItemAsync(RecentItemSelectionRecord recent)
    {
        if (recent is null)
            return;

        if (SelectedCategory is null || !CategoryEquals(SelectedCategory.Name, recent.CategoryName))
        {
            var category = FindCategoryByName(recent.CategoryName);
            if (category is not null)
                await SelectCategoryAsync(category);
        }

        var matched = ItemSearchResults.FirstOrDefault(item => item.Id == recent.ItemId)
                      ?? new ItemDto
                      {
                          Id = recent.ItemId,
                          CategoryName = recent.CategoryName,
                          NameOriginal = recent.ItemNameOriginal,
                          SpecificationOriginal = recent.SpecificationOriginal,
                          MaterialNumber = recent.MaterialNumber
                      };

        await OpenItemEntrySheetAsync(matched, recordRecent: true, requireResolvedActiveItem: true);
    }

    public async Task CancelItemEntryAsync()
    {
        ResetItemSelection(clearCategory: false);
        await Task.CompletedTask;
    }

    public async Task AddOrUpdateLineAsync()
    {
        if (SelectedItem is null)
        {
            StatusMessage = "추가할 품목을 먼저 선택하세요.";
            return;
        }

        if (!decimal.TryParse(LineQuantityText, out var quantity) || quantity <= 0m)
        {
            StatusMessage = "수량을 올바르게 입력하세요.";
            return;
        }

        if (!decimal.TryParse(LineUnitPriceText, out var unitPrice) || unitPrice < 0m)
        {
            StatusMessage = "단가를 올바르게 입력하세요.";
            return;
        }

        var draft = InvoiceLineDraftItem.FromItem(SelectedItem, quantity);
        draft.UnitPrice = unitPrice;
        draft.Remark = LineRemark.Trim();
        draft.CategoryName = SelectedCategory?.Name ?? SelectedItem.CategoryName;

        if (_editingLineId.HasValue)
        {
            var existing = LineItems.FirstOrDefault(line => line.Id == _editingLineId.Value);
            if (existing is not null)
            {
                var index = LineItems.IndexOf(existing);
                draft.Id = existing.Id;
                LineItems[index] = draft;
            }

            StatusMessage = "전표 품목을 수정했습니다. 더 추가하거나 마지막 저장 카드에서 전표를 저장하세요.";
        }
        else
        {
            LineItems.Add(draft);
            StatusMessage = "전표 품목을 추가했습니다. 더 추가하거나 마지막 저장 카드에서 전표를 저장하세요.";
        }

        await RecordRecentSelectionAsync(SelectedItem);
        ResetItemSelection(clearCategory: false);
        OnPropertyChanged(nameof(DraftSummary));
        OnPropertyChanged(nameof(VatSummary));
    }

    public async Task EditLineAsync(InvoiceLineDraftItem line)
    {
        if (!string.IsNullOrWhiteSpace(line.CategoryName))
        {
            var category = FindCategoryByName(line.CategoryName);
            if (category is not null && (SelectedCategory is null || !CategoryEquals(SelectedCategory.Name, category.Name)))
                await SelectCategoryAsync(category);
        }

        ItemDto item;
        if (line.ItemId.HasValue)
        {
            item = ItemSearchResults.FirstOrDefault(candidate => candidate.Id == line.ItemId.Value)
                   ?? new ItemDto
                   {
                       Id = line.ItemId.Value,
                       CategoryName = line.CategoryName,
                       NameOriginal = line.ItemNameOriginal,
                       SpecificationOriginal = line.SpecificationOriginal,
                       MaterialNumber = line.MaterialNumber,
                       SerialNumber = line.SerialNumber,
                       Unit = line.Unit,
                       SalePrice = line.UnitPrice,
                       RetailPrice = line.UnitPrice,
                       PurchasePrice = line.UnitPrice,
                       SimpleMemo = line.Remark
                   };
        }
        else
        {
            item = new ItemDto
            {
                CategoryName = line.CategoryName,
                NameOriginal = line.ItemNameOriginal,
                SpecificationOriginal = line.SpecificationOriginal,
                MaterialNumber = line.MaterialNumber,
                SerialNumber = line.SerialNumber,
                Unit = line.Unit,
                SalePrice = line.UnitPrice,
                RetailPrice = line.UnitPrice,
                PurchasePrice = line.UnitPrice,
                SimpleMemo = line.Remark
            };
        }

        _editingLineId = line.Id;
        await OpenItemEntrySheetAsync(item, recordRecent: false);
        LineQuantityText = line.Quantity.ToString("0.##");
        LineUnitPriceText = line.UnitPrice.ToString("0.##");
        LineRemark = line.Remark;
        OnPropertyChanged(nameof(LineActionText));
        StatusMessage = $"{line.ItemNameOriginal} 품목을 수정 중입니다.";
    }

    public Task RemoveLineAsync(InvoiceLineDraftItem line)
    {
        LineItems.Remove(line);
        if (_editingLineId == line.Id)
            ResetItemSelection(clearCategory: false);

        OnPropertyChanged(nameof(DraftSummary));
        OnPropertyChanged(nameof(VatSummary));
        StatusMessage = $"{line.ItemNameOriginal} 품목을 목록에서 제거했습니다.";
        return Task.CompletedTask;
    }

    public async Task SaveDraftAsync()
    {
        if (IsBusy)
            return;

        if (!CanCreateInvoices)
        {
            StatusMessage = "권한이 없어 전표를 저장할 수 없습니다.";
            return;
        }

        if (SelectedCustomer is null)
        {
            StatusMessage = "거래처를 먼저 선택하세요.";
            return;
        }

        if (LineItems.Count == 0)
        {
            StatusMessage = "전표에 추가할 품목을 하나 이상 입력하세요.";
            return;
        }

        var isEditMode = _editingInvoice is not null;
        var invoice = BuildCurrentInvoiceDto(forSave: true);
        if (!CanSaveCurrentInvoiceScope(invoice))
            return;

        try
        {
            IsBusy = true;
            StatusMessage = $"{DocumentKindText} 전표를 {(isEditMode ? "수정 저장" : "저장")}하고 있습니다.";
            var state = await _syncCoordinator.SaveInvoiceImmediatelyAsync(invoice);

            if (SyncCoordinator.IsConcurrencyConflictState(state))
            {
                _refreshCoordinator.MarkInvoicesChanged();
                StatusMessage = $"{DocumentKindText} 전표가 저장되지 않았습니다. {state.LastError}";
                return;
            }

            if (state.PendingInvoiceCount == 0 &&
                SyncCoordinator.IsFailedImmediateSaveWithoutServerAcceptance(state))
            {
                StatusMessage = $"{DocumentKindText} 전표가 저장되지 않았습니다. {state.LastError}";
                return;
            }

            if (state.PendingInvoiceCount == 0)
            {
                _refreshCoordinator.MarkInvoicesChanged();
                StatusMessage = string.IsNullOrWhiteSpace(state.LastError)
                        ? $"{DocumentKindText} 전표 {(isEditMode ? "수정 저장" : "저장")} 및 서버 반영 완료"
                        : $"{DocumentKindText} 전표 {(isEditMode ? "수정 저장" : "저장")} 완료 / 최신 데이터 새로고침 대기: {state.LastError}";

                if (SavedSuccessfully is not null)
                    await SavedSuccessfully.Invoke();
            }
            else
            {
                StatusMessage = $"{DocumentKindText} 전표 저장 완료(오프라인/재시도 대기): {state.LastError}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"{DocumentKindText} 전표 저장 실패: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ExportPdfAsync()
    {
        if (IsBusy)
            return;

        if (SelectedCustomer is null)
        {
            StatusMessage = "PDF로 저장할 거래처를 먼저 선택하세요.";
            return;
        }

        if (LineItems.Count == 0)
        {
            StatusMessage = "PDF로 저장할 품목을 하나 이상 입력하세요.";
            return;
        }

        var options = new MobileInvoicePrintOptions
        {
            PrintStatementDocument = PrintStatementDocument,
            PrintEstimateDocument = PrintEstimateDocument,
            PrintPaymentClaimDocument = PrintPaymentClaimDocument,
            PrintDate = PrintDate,
            PrintUnitPrice = PrintUnitPrice
        };

        if (!options.HasAnyDocument)
        {
            StatusMessage = "PDF로 저장할 출력 서류를 하나 이상 선택하세요.";
            return;
        }

        try
        {
            IsBusy = true;
            var invoice = BuildCurrentInvoiceDto(forSave: false);
            StatusMessage = "선택한 출력 옵션으로 PDF를 만들고 있습니다.";
            var path = await _pdfExportService.ExportAndShareAsync(invoice, options);
            StatusMessage = $"PDF 저장 완료: {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"PDF 저장 실패: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private InvoiceDto BuildCurrentInvoiceDto(bool forSave)
    {
        var now = DateTime.UtcNow;
        var invoiceId = _editingInvoice?.Id ?? Guid.NewGuid();
        var lines = LineItems.Select((line, index) =>
        {
            line.OrderIndex = index + 1;
            return line.ToDto(invoiceId);
        }).ToList();
        var totals = CalculateTotals(lines.Select(line => line.LineAmount));

        return new InvoiceDto
        {
            Id = invoiceId,
            CustomerId = SelectedCustomer?.Id ?? Guid.Empty,
            CustomerName = SelectedCustomer?.NameOriginal ?? string.Empty,
            TenantCode = _editingInvoice?.TenantCode ?? _sessionTenantCode,
            OfficeCode = SelectedInvoiceOfficeCode,
            ResponsibleOfficeCode = SelectedInvoiceOfficeCode,
            InvoiceNumber = _editingInvoice?.InvoiceNumber ?? string.Empty,
            LocalTempNumber = string.IsNullOrWhiteSpace(_editingInvoice?.LocalTempNumber)
                ? $"M-{(IsPurchaseLikeDocument ? "P" : "S")}-{DateTime.Now:yyyyMMdd-HHmmss}"
                : _editingInvoice!.LocalTempNumber,
            LinkedRentalBillingProfileId = _editingInvoice?.LinkedRentalBillingProfileId,
            LinkedRentalBillingRunId = _editingInvoice?.LinkedRentalBillingRunId,
            VersionGroupId = _editingInvoice?.VersionGroupId ?? Guid.Empty,
            VersionNumber = _editingInvoice?.VersionNumber ?? 1,
            PreviousVersionId = _editingInvoice?.PreviousVersionId,
            IsLatestVersion = _editingInvoice?.IsLatestVersion ?? true,
            VoucherType = this.VoucherType,
            InvoiceDate = DateOnly.FromDateTime(InvoiceDate),
            SourceWarehouseCode = SelectedSourceWarehouseCode,
            TotalAmount = totals.TotalAmount,
            SupplyAmount = totals.SupplyAmount,
            VatAmount = totals.VatAmount,
            VatMode = IsVatNone ? InvoiceVatModes.None : InvoiceVatModes.Included,
            TaxInvoiceIssued = _editingInvoice?.TaxInvoiceIssued ?? false,
            PurchaseReceivingRequired = IsPurchaseDocument,
            PurchaseReceivingStatus = InvoiceReceivingStatuses.Normalize(
                _editingInvoice?.PurchaseReceivingStatus ?? string.Empty,
                IsPurchaseDocument,
                IsPurchaseDocument),
            PurchaseReceivedAtUtc = _editingInvoice?.PurchaseReceivedAtUtc,
            PurchaseReceivedByUsername = _editingInvoice?.PurchaseReceivedByUsername ?? string.Empty,
            PurchaseReceivingOfficeCode = _editingInvoice?.PurchaseReceivingOfficeCode ?? string.Empty,
            PurchaseReceivingWarehouseCode = _editingInvoice?.PurchaseReceivingWarehouseCode ?? string.Empty,
            PurchaseReceivingMemo = _editingInvoice?.PurchaseReceivingMemo ?? string.Empty,
            Memo = Memo.Trim(),
            CreatedAtUtc = _editingInvoice?.CreatedAtUtc == default ? now : _editingInvoice?.CreatedAtUtc ?? now,
            UpdatedAtUtc = forSave ? now : _editingInvoice?.UpdatedAtUtc ?? now,
            Revision = _editingInvoice?.Revision ?? 0,
            ExpectedRevision = _editingInvoice?.Revision ?? 0,
            MutationId = forSave ? BuildMutationId("invoice", invoiceId) : _editingInvoice?.MutationId ?? string.Empty,
            MutationCreatedAtUtc = forSave ? now : _editingInvoice?.MutationCreatedAtUtc,
            IsDeleted = false,
            Lines = lines,
            Payments = _editingInvoice?.Payments ?? new List<PaymentDto>()
        };
    }

    private bool CanSaveCurrentInvoiceScope(InvoiceDto invoice)
    {
        var snapshot = _sessionStore.GetSnapshot();
        if (!MobileSessionScopeFilter.CanAccessInvoice(snapshot, invoice))
        {
            StatusMessage = "전표 담당지점이 현재 로그인 담당지점/업체 범위 밖입니다.";
            return false;
        }

        if (SelectedCustomer is not null && !MobileSessionScopeFilter.CanAccessCustomer(snapshot, SelectedCustomer))
        {
            StatusMessage = "선택한 거래처가 현재 로그인 담당지점/업체 범위 밖입니다.";
            return false;
        }

        if (SelectedCustomer is not null && !IsCustomerInSelectedInvoiceOfficeScope(SelectedCustomer))
        {
            StatusMessage = "선택한 거래처가 전표 담당지점 범위 밖입니다. 거래처를 다시 선택하세요.";
            return false;
        }

        return true;
    }

    private async Task OpenItemEntrySheetAsync(ItemDto item, bool recordRecent, bool requireResolvedActiveItem = false)
    {
        SelectedItem = item;
        _editingLineId = recordRecent ? null : _editingLineId;
        OnPropertyChanged(nameof(LineActionText));
        LineQuantityText = "1";
        LineUnitPriceText = ResolveDefaultUnitPrice(item).ToString("0.##");
        LineRemark = string.Empty;
        SelectedItemBranchStocks.Clear();
        var usedSyncedFallback = false;
        var resolvedActiveItem = !requireResolvedActiveItem && item.Id != Guid.Empty && !item.IsDeleted;

        try
        {
            var detail = item.Id == Guid.Empty ? null : await _api.GetItemDetailAsync(item.Id);
            if (detail?.Item is not null)
            {
                if (detail.Item.IsDeleted)
                {
                    if (requireResolvedActiveItem)
                        await RejectUnresolvedRecentItemAsync(item);
                    else
                        RejectUnavailableItemSelection(item, new InvalidOperationException("서버에서 삭제된 품목입니다."));
                    return;
                }

                SelectedItem = detail.Item;
                LineUnitPriceText = ResolveDefaultUnitPrice(detail.Item).ToString("0.##");
                resolvedActiveItem = true;
            }

            PopulateSelectedSourceWarehouseStocks(detail?.BranchStocks, SelectedItem);
        }
        catch (Exception ex)
        {
            if (!MobileRetryableNetworkFailure.IsRetryable(ex))
            {
                if (requireResolvedActiveItem)
                    await RejectUnresolvedRecentItemAsync(item);
                else
                    RejectUnavailableItemSelection(item, ex);
                return;
            }

            usedSyncedFallback = await TryOpenItemEntrySheetFromSyncedStateAsync(
                item,
                $"품목 상세 조회 실패: {ex.Message}",
                requireActiveSyncedItem: requireResolvedActiveItem);
            if (!usedSyncedFallback && requireResolvedActiveItem)
            {
                await RejectUnresolvedRecentItemAsync(item);
                return;
            }

            if (!usedSyncedFallback && item.Id != Guid.Empty)
                AddFallbackWholeStockRow(item);
        }

        if (requireResolvedActiveItem && !resolvedActiveItem && !usedSyncedFallback)
        {
            await RejectUnresolvedRecentItemAsync(item);
            return;
        }

        if (SelectedItem is not null && !IsItemInSelectedInvoiceOfficeScope(SelectedItem))
        {
            var rejectedName = SelectedItem.NameOriginal;
            ResetItemSelection(clearCategory: false);
            StatusMessage = $"{rejectedName} 품목은 현재 전표 담당지점 범위에 없습니다. 담당지점 또는 품목을 다시 선택하세요.";
            return;
        }

        if (recordRecent && SelectedItem is not null)
            await RecordRecentSelectionAsync(SelectedItem);

        IsItemEntrySheetVisible = true;
        var selectedName = SelectedItem?.NameOriginal ?? item.NameOriginal;
        StatusMessage = usedSyncedFallback
            ? $"{selectedName} 품목을 동기화 캐시 기준으로 선택했습니다. 수량과 단가를 입력한 뒤 품목 추가를 누르세요."
            : $"{selectedName} 품목을 선택했습니다. 수량과 단가를 입력한 뒤 품목 추가를 누르세요.";
    }

    private async Task<bool> TryOpenItemEntrySheetFromSyncedStateAsync(
        ItemDto item,
        string reason,
        bool requireActiveSyncedItem = false)
    {
        if (item.Id == Guid.Empty)
            return false;

        var state = await _syncCoordinator.LoadAsync();
        state.Normalize();

        var syncedItem = state.SyncedItems
            .Where(candidate => !candidate.IsDeleted)
            .FirstOrDefault(candidate => candidate.Id == item.Id);
        var selected = syncedItem ?? item;
        var branchStocks = state.SyncedItemWarehouseStocks
            .Where(stock => stock.ItemId == item.Id)
            .ToList();

        if (requireActiveSyncedItem && syncedItem is null)
            return false;

        if (syncedItem is null && branchStocks.Count == 0)
            return false;

        SelectedItem = selected;
        LineUnitPriceText = ResolveDefaultUnitPrice(selected).ToString("0.##");
        SelectedItemBranchStocks.Clear();
        PopulateSelectedSourceWarehouseStocks(branchStocks, selected);

        StatusMessage = $"{reason} / 동기화 캐시 기준으로 {selected.NameOriginal} 품목을 표시합니다.";
        return true;
    }

    private async Task RejectUnresolvedRecentItemAsync(ItemDto item)
    {
        var itemName = string.IsNullOrWhiteSpace(item.NameOriginal) ? "최근 선택 품목" : item.NameOriginal.Trim();
        ResetItemSelection(clearCategory: false);
        await RemoveRecentSelectionAsync(item.Id);
        StatusMessage = $"{itemName} 품목은 삭제되었거나 현재 권한/담당지점 범위 밖입니다. 최신 동기화 후 품목을 다시 선택하세요.";
    }

    private void RejectUnavailableItemSelection(ItemDto item, Exception ex)
    {
        var itemName = string.IsNullOrWhiteSpace(item.NameOriginal) ? "선택한 품목" : item.NameOriginal.Trim();
        ResetItemSelection(clearCategory: false);
        StatusMessage = $"{itemName} 품목 상세를 사용할 수 없습니다. 삭제되었거나 현재 권한/담당지점 범위 밖일 수 있습니다. ({ex.Message})";
    }

    private async Task RemoveRecentSelectionAsync(Guid itemId)
    {
        if (itemId == Guid.Empty)
            return;

        var removed = _recentSelections.RemoveAll(entry => entry.ItemId == itemId) > 0;
        if (!removed)
            return;

        await _recentItemSelectionStore.SaveAsync(_sessionTenantCode, BuildRecentSelectionScopeCode(), _sessionUsername, _recentSelections);
        RefreshVisibleRecentItems();
    }

    private void AddFallbackWholeStockRow(ItemDto item, decimal? quantity = null)
    {
        if (item.Id == Guid.Empty)
            return;

        SelectedItemBranchStocks.Add(new ItemWarehouseStockDto
        {
            ItemId = item.Id,
            WarehouseCode = SelectedSourceWarehouseCode,
            Quantity = quantity ?? item.CurrentStock,
            UpdatedAtUtc = item.UpdatedAtUtc == default ? DateTime.UtcNow : item.UpdatedAtUtc
        });
    }

    private void ResetItemSelection(bool clearCategory)
    {
        _editingLineId = null;
        SelectedItem = null;
        SelectedItemBranchStocks.Clear();
        IsItemEntrySheetVisible = false;
        LineQuantityText = "1";
        LineUnitPriceText = "0";
        LineRemark = string.Empty;
        OnPropertyChanged(nameof(LineActionText));

        if (clearCategory)
            SelectedCategory = null;
    }

    private void InitializeOfficeOptions()
    {
        if (InvoiceOfficeOptions.Count > 0)
            return;

        var snapshot = _sessionStore.GetSnapshot();
        _sessionOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(snapshot.OfficeCode, OfficeCodeCatalog.Usenet);
        _sessionTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(snapshot.TenantCode, _sessionOfficeCode);
        _sessionUsername = snapshot.Username ?? string.Empty;

        var options = ResolveWritableInvoiceOfficeCodes(snapshot, _sessionOfficeCode, _sessionTenantCode)
            .Select(code => OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(code, _sessionOfficeCode))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (options.Count == 0)
            options.Add(_sessionOfficeCode);

        InvoiceOfficeOptions.Clear();
        foreach (var code in options)
        {
            InvoiceOfficeOptions.Add(new MobileOfficeOption
            {
                Code = code,
                DisplayName = GetOfficeDisplayName(code)
            });
        }

        SelectedInvoiceOffice = InvoiceOfficeOptions.FirstOrDefault(option => string.Equals(option.Code, _sessionOfficeCode, StringComparison.OrdinalIgnoreCase))
            ?? InvoiceOfficeOptions.FirstOrDefault();
        OnPropertyChanged(nameof(CanChooseInvoiceOffice));
        OnPropertyChanged(nameof(SelectedInvoiceOfficeSummary));
        RefreshSourceWarehouseOptions();
    }

    private static IReadOnlyList<string> ResolveWritableInvoiceOfficeCodes(
        SessionSnapshot snapshot,
        string sessionOfficeCode,
        string sessionTenantCode)
    {
        var scopeType = TenantScopeCatalog.NormalizeScopeTypeOrDefault(
            snapshot.ScopeType,
            snapshot.IsAdmin ? TenantScopeCatalog.ScopeAdmin : TenantScopeCatalog.ScopeOfficeOnly);

        if (snapshot.IsAdmin && string.Equals(scopeType, TenantScopeCatalog.ScopeAdmin, StringComparison.OrdinalIgnoreCase))
            return OfficeCodeCatalog.All;

        if (string.Equals(scopeType, TenantScopeCatalog.ScopeTenantAll, StringComparison.OrdinalIgnoreCase))
            return TenantScopeCatalog.GetOfficeCodesForTenant(sessionTenantCode);

        return [sessionOfficeCode];
    }

    private void RefreshSourceWarehouseOptions()
    {
        var previousCode = SelectedSourceWarehouse?.Code;
        var officeCode = SelectedInvoiceOfficeCode;
        var warehouseCodes = new List<string>();

        if (OfficeCodeCatalog.IsSharedOfficeCode(officeCode))
        {
            foreach (var code in OfficeCodeCatalog.All)
                warehouseCodes.Add(OfficeCodeCatalog.GetMainWarehouseCode(code));
        }
        else
        {
            warehouseCodes.Add(OfficeCodeCatalog.GetMainWarehouseCode(officeCode));
        }

        SourceWarehouseOptions.Clear();
        foreach (var code in warehouseCodes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            SourceWarehouseOptions.Add(new MobileWarehouseOption
            {
                Code = code,
                DisplayName = WarehouseDisplayNameResolver.Resolve(code)
            });
        }

        _isRefreshingSourceWarehouseOptions = true;
        try
        {
            SelectedSourceWarehouse = SourceWarehouseOptions.FirstOrDefault(option => string.Equals(option.Code, previousCode, StringComparison.OrdinalIgnoreCase))
                ?? SourceWarehouseOptions.FirstOrDefault();
        }
        finally
        {
            _isRefreshingSourceWarehouseOptions = false;
        }
        OnPropertyChanged(nameof(CanChooseSourceWarehouse));
        OnPropertyChanged(nameof(SelectedSourceWarehouseSummary));
    }

    private void SelectInvoiceOfficeByCode(params string?[] officeCodes)
    {
        InitializeOfficeOptions();
        foreach (var code in officeCodes.Where(code => !string.IsNullOrWhiteSpace(code)))
        {
            var match = InvoiceOfficeOptions.FirstOrDefault(option =>
                string.Equals(option.Code, code, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                SelectedInvoiceOffice = match;
                return;
            }
        }
    }

    private void SelectSourceWarehouseByCode(string? warehouseCode)
    {
        if (string.IsNullOrWhiteSpace(warehouseCode))
            return;

        RefreshSourceWarehouseOptions();
        var match = SourceWarehouseOptions.FirstOrDefault(option =>
            string.Equals(option.Code, warehouseCode, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
            SelectedSourceWarehouse = match;
    }

    private void SelectInvoiceOfficeByCustomerScope(CustomerDto customer)
    {
        InitializeOfficeOptions();
        foreach (var officeCode in new[] { customer.ResponsibleOfficeCode, customer.OfficeCode })
        {
            if (!OfficeCodeCatalog.TryNormalizeScope(officeCode, out var normalizedOfficeCode))
                continue;

            var match = InvoiceOfficeOptions.FirstOrDefault(option =>
                string.Equals(option.Code, normalizedOfficeCode, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                SelectedInvoiceOffice = match;
                return;
            }
        }
    }

    private void HandleInvoiceScopeChanged(string previousOfficeCode, string previousWarehouseCode)
    {
        var officeChanged = !string.Equals(previousOfficeCode, SelectedInvoiceOfficeCode, StringComparison.OrdinalIgnoreCase);
        var warehouseChanged = !string.Equals(previousWarehouseCode, SelectedSourceWarehouseCode, StringComparison.OrdinalIgnoreCase);
        if (!officeChanged && !warehouseChanged)
            return;

        ItemSearchResults.Clear();
        ResetItemSelection(clearCategory: false);
        ClearSelectedCustomerIfOutOfSelectedInvoiceOfficeScope();
        QueueRecentSelectionsReloadForCurrentScope();
    }

    private void ClearSelectedCustomerIfOutOfSelectedInvoiceOfficeScope()
    {
        if (SelectedCustomer is null || IsCustomerInSelectedInvoiceOfficeScope(SelectedCustomer))
            return;

        SelectedCustomer = null;
        CustomerSearchText = string.Empty;
        CustomerSearchResults.Clear();
        StatusMessage = "전표 담당지점이 변경되어 기존 거래처 선택을 해제했습니다. 새 담당지점 범위에서 다시 선택하세요.";
    }

    private IEnumerable<CustomerDto> FilterCustomersForSelectedInvoiceOfficeScope(IEnumerable<CustomerDto> customers)
        => customers.Where(IsCustomerInSelectedInvoiceOfficeScope);

    private bool IsCustomerInSelectedInvoiceOfficeScope(CustomerDto customer)
    {
        if (OfficeCodeCatalog.IsSharedOfficeCode(SelectedInvoiceOfficeCode))
            return true;

        if (!OfficeCodeCatalog.TryNormalize(SelectedInvoiceOfficeCode, out var selectedOfficeCode))
            return false;

        if (OfficeCodeCatalog.TryNormalize(customer.ResponsibleOfficeCode, out var responsibleOfficeCode))
            return string.Equals(responsibleOfficeCode, selectedOfficeCode, StringComparison.OrdinalIgnoreCase);

        return OfficeCodeCatalog.TryNormalize(customer.OfficeCode, out var owningOfficeCode) &&
               string.Equals(owningOfficeCode, selectedOfficeCode, StringComparison.OrdinalIgnoreCase);
    }

    private IEnumerable<ItemDto> FilterItemsForSelectedInvoiceOfficeScope(IEnumerable<ItemDto> items)
        => items.Where(IsItemInSelectedInvoiceOfficeScope);

    private bool IsItemInSelectedInvoiceOfficeScope(ItemDto item)
    {
        if (OfficeCodeCatalog.IsSharedOfficeCode(SelectedInvoiceOfficeCode))
            return true;

        if (OfficeCodeCatalog.IsSharedOfficeCode(item.OfficeCode))
            return true;

        return OfficeCodeCatalog.TryNormalize(SelectedInvoiceOfficeCode, out var selectedOfficeCode) &&
               OfficeCodeCatalog.TryNormalize(item.OfficeCode, out var itemOfficeCode) &&
               string.Equals(itemOfficeCode, selectedOfficeCode, StringComparison.OrdinalIgnoreCase);
    }

    private void PopulateSelectedSourceWarehouseStocks(IEnumerable<ItemWarehouseStockDto>? branchStocks, ItemDto item)
    {
        var stockList = (branchStocks ?? []).ToList();
        var selectedWarehouseStocks = stockList
            .Where(IsStockInSelectedSourceWarehouse)
            .OrderBy(stock => stock.WarehouseCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        SelectedItemBranchStocks.Clear();
        foreach (var stock in selectedWarehouseStocks)
            SelectedItemBranchStocks.Add(stock);

        if (SelectedItemBranchStocks.Count == 0)
            AddFallbackWholeStockRow(item, stockList.Count == 0 ? item.CurrentStock : 0m);
    }

    private bool IsStockInSelectedSourceWarehouse(ItemWarehouseStockDto stock)
    {
        var selectedWarehouseCode = OfficeCodeCatalog.NormalizeWarehouseCodeOrDefault(
            SelectedSourceWarehouseCode,
            SelectedInvoiceOfficeCode);
        var stockWarehouseCode = OfficeCodeCatalog.NormalizeWarehouseCodeLoose(
            stock.WarehouseCode,
            SelectedInvoiceOfficeCode,
            SelectedInvoiceOfficeCode);
        return string.Equals(stockWarehouseCode, selectedWarehouseCode, StringComparison.OrdinalIgnoreCase);
    }

    private string BuildRecentSelectionScopeCode()
    {
        var officeCode = string.IsNullOrWhiteSpace(SelectedInvoiceOfficeCode)
            ? _sessionOfficeCode
            : SelectedInvoiceOfficeCode.Trim().ToUpperInvariant();
        var warehouseCode = string.IsNullOrWhiteSpace(SelectedSourceWarehouseCode)
            ? OfficeCodeCatalog.GetMainWarehouseCode(officeCode)
            : SelectedSourceWarehouseCode.Trim().ToUpperInvariant();
        return $"{officeCode}:{warehouseCode}";
    }

    private static string BuildMutationId(string entityName, Guid entityId)
        => $"mobile:{entityName}:{entityId:N}:{Guid.NewGuid():N}";

    private void QueueRecentSelectionsReloadForCurrentScope()
    {
        if (!_hasLoadedRecentSelections)
            return;

        var reloadVersion = ++_recentSelectionReloadVersion;
        var recentScopeCode = BuildRecentSelectionScopeCode();
        _recentSelections.Clear();
        RefreshVisibleRecentItems();
        _ = ReloadRecentSelectionsForCurrentScopeAsync(reloadVersion, recentScopeCode);
    }

    private async Task ReloadRecentSelectionsForCurrentScopeAsync(int reloadVersion, string recentScopeCode)
    {
        try
        {
            var records = await _recentItemSelectionStore.LoadAsync(_sessionTenantCode, recentScopeCode, _sessionUsername);
            if (reloadVersion != _recentSelectionReloadVersion ||
                !string.Equals(recentScopeCode, BuildRecentSelectionScopeCode(), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _recentSelections.Clear();
            _recentSelections.AddRange(records);
            RefreshVisibleRecentItems();
        }
        catch (Exception ex) when (reloadVersion == _recentSelectionReloadVersion)
        {
            StatusMessage = $"최근 선택 품목을 불러오지 못했습니다: {ex.Message}";
        }
    }

    private void RefreshPriceGradeSourceMap(IEnumerable<PriceGradeOptionDto>? options)
    {
        _priceGradeSourceMap.Clear();
        foreach (var option in options ?? [])
        {
            if (option.IsDeleted || string.IsNullOrWhiteSpace(option.Name))
                continue;

            _priceGradeSourceMap[option.Name.Trim()] = MobilePriceSourceResolver.NormalizePriceSource(option.PriceSource);
        }
    }

    private async Task LoadRecentSelectionsAsync()
    {
        var snapshot = _sessionStore.GetSnapshot();
        _sessionTenantCode = string.IsNullOrWhiteSpace(snapshot.TenantCode) ? TenantScopeCatalog.UsenetGroup : snapshot.TenantCode;
        _sessionOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(snapshot.OfficeCode, OfficeCodeCatalog.Usenet);
        _sessionUsername = snapshot.Username ?? string.Empty;

        var reloadVersion = ++_recentSelectionReloadVersion;
        var recentScopeCode = BuildRecentSelectionScopeCode();
        _recentSelections.Clear();
        var records = await _recentItemSelectionStore.LoadAsync(_sessionTenantCode, recentScopeCode, _sessionUsername);
        if (reloadVersion != _recentSelectionReloadVersion ||
            !string.Equals(recentScopeCode, BuildRecentSelectionScopeCode(), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _hasLoadedRecentSelections = true;
        _recentSelections.AddRange(records);
        RefreshVisibleRecentItems();
    }

    private async Task RecordRecentSelectionAsync(ItemDto item)
    {
        if (item.Id == Guid.Empty)
            return;

        _recentSelections.RemoveAll(entry => entry.ItemId == item.Id);
        _recentSelections.Insert(0, new RecentItemSelectionRecord
        {
            ItemId = item.Id,
            CategoryName = item.CategoryName,
            ItemNameOriginal = item.NameOriginal,
            SpecificationOriginal = item.SpecificationOriginal,
            MaterialNumber = item.MaterialNumber,
            SelectedAtUtc = DateTime.UtcNow
        });

        if (_recentSelections.Count > 5)
            _recentSelections.RemoveRange(5, _recentSelections.Count - 5);

        await _recentItemSelectionStore.SaveAsync(_sessionTenantCode, BuildRecentSelectionScopeCode(), _sessionUsername, _recentSelections);
        RefreshVisibleRecentItems();
    }

    private void RefreshVisibleRecentItems()
    {
        var ordered = _recentSelections
            .OrderBy(record => SelectedCategory is not null && CategoryEquals(record.CategoryName, SelectedCategory.Name) ? 0 : 1)
            .ThenByDescending(record => record.SelectedAtUtc)
            .Take(5)
            .ToList();

        VisibleRecentItems.Clear();
        foreach (var record in ordered)
            VisibleRecentItems.Add(record);

        OnPropertyChanged(nameof(HasVisibleRecentItems));
    }

    private ItemCategorySummaryDto? FindCategoryByName(string? categoryName)
    {
        if (string.IsNullOrWhiteSpace(categoryName))
            return ItemCategories.FirstOrDefault(category => string.Equals(category.Name, "미분류", StringComparison.OrdinalIgnoreCase));

        return ItemCategories.FirstOrDefault(category => CategoryEquals(category.Name, categoryName));
    }

    private static IEnumerable<CustomerDto> GetActiveSyncedCustomers(Models.MobileSyncState state)
        => state.SyncedCustomers.Where(customer => !customer.IsDeleted);

    private static IEnumerable<ItemDto> GetActiveSyncedItems(Models.MobileSyncState state)
        => state.SyncedItems
            .Where(item => !item.IsDeleted)
            .Select(item =>
            {
                item.CategoryName = NormalizeCategoryName(item.CategoryName);
                return item;
            });

    private static IReadOnlyList<ItemCategorySummaryDto> BuildCategorySummaries(IEnumerable<ItemDto> items)
        => items
            .GroupBy(item => NormalizeCategoryName(item.CategoryName), StringComparer.OrdinalIgnoreCase)
            .Select(group => new ItemCategorySummaryDto
            {
                Name = group.Key,
                ItemCount = group.Count()
            })
            .OrderBy(category => category.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    private static string BuildItemIdentitySummary(ItemDto? item)
    {
        if (item is null)
            return "품목 식별 정보 없음";

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(item.CategoryName))
            parts.Add($"분류 {item.CategoryName.Trim()}");
        if (!string.IsNullOrWhiteSpace(item.ItemKind))
            parts.Add($"구분 {item.ItemKind.Trim()}");
        if (!string.IsNullOrWhiteSpace(item.TrackingType))
            parts.Add($"재고방식 {item.TrackingType.Trim()}");
        if (!string.IsNullOrWhiteSpace(item.MaterialNumber))
            parts.Add($"자재 {item.MaterialNumber.Trim()}");
        if (!string.IsNullOrWhiteSpace(item.SerialNumber))
            parts.Add($"S/N {item.SerialNumber.Trim()}");

        return parts.Count == 0 ? "품목 식별 정보 없음" : string.Join(" · ", parts);
    }

    private static bool MatchesItem(ItemDto item, string query)
        => Contains(item.NameOriginal, query)
           || Contains(item.NameMatchKey, query)
           || Contains(item.SpecificationOriginal, query)
           || Contains(item.SpecificationMatchKey, query)
           || Contains(item.MaterialNumber, query)
           || Contains(item.SerialNumber, query)
           || Contains(item.CategoryName, query);

    private static bool MatchesCustomer(CustomerDto customer, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;

        return Contains(customer.NameOriginal, query)
               || Contains(customer.NameMatchKey, query)
               || Contains(customer.Phone, query)
               || Contains(customer.MobilePhone, query)
               || Contains(customer.BusinessNumber, query)
               || Contains(customer.Representative, query)
               || Contains(customer.ContactPerson, query)
               || Contains(customer.Department, query)
               || Contains(customer.PriceGrade, query);
    }

    private static bool Contains(string? source, string query)
        => !string.IsNullOrWhiteSpace(source) &&
           source.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    private static string NormalizeCategoryName(string? categoryName)
        => string.IsNullOrWhiteSpace(categoryName) ? "미분류" : categoryName.Trim();

    private static bool CategoryEquals(string? left, string? right)
    {
        var normalizedLeft = NormalizeCategoryName(left);
        var normalizedRight = NormalizeCategoryName(right);
        return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetOfficeDisplayName(string code)
        => OfficeCodeCatalog.IsSharedOfficeCode(code)
            ? "공용"
            : OfficeCodeCatalog.GetOfficeDisplayName(code);

    private void HandleCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(CustomerSearchResultsHeight));
        OnPropertyChanged(nameof(ItemSearchResultsHeight));
        OnPropertyChanged(nameof(SelectedItemBranchStocksHeight));
        OnPropertyChanged(nameof(LineItemsHeight));
        OnPropertyChanged(nameof(DraftSummary));
        OnPropertyChanged(nameof(VatSummary));
        OnPropertyChanged(nameof(HasVisibleRecentItems));
        OnPropertyChanged(nameof(HasItemSearchResults));
        OnPropertyChanged(nameof(SelectedCategorySummary));
        OnPropertyChanged(nameof(ItemListCaptionText));
    }

    private static double CalculateListHeight(int count, double rowHeight, double emptyHeight, int maxVisibleRows)
    {
        if (count <= 0)
            return emptyHeight;

        var visibleRows = Math.Min(count, maxVisibleRows);
        return (visibleRows * rowHeight) + Math.Max(0, visibleRows - 1) * 6;
    }

    private decimal ResolveDefaultUnitPrice(ItemDto item)
        => IsPurchaseLikeDocument
            ? item.PurchasePrice
            : MobilePriceSourceResolver.ResolveSalesUnitPrice(item, SelectedCustomer?.PriceGrade, _priceGradeSourceMap);

    private (decimal SupplyAmount, decimal VatAmount, decimal TotalAmount) CalculateTotals(IEnumerable<decimal> lineAmounts)
        => InvoiceVatModes.CalculateTotals(
            lineAmounts,
            IsVatNone ? InvoiceVatModes.None : InvoiceVatModes.Included);
}
