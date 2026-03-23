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
    private readonly List<RecentItemSelectionRecord> _recentSelections = new();

    private CustomerDto? _selectedCustomer;
    private ItemCategorySummaryDto? _selectedCategory;
    private ItemDto? _selectedItem;
    private DateTime _invoiceDate = DateTime.Today;
    private string _customerSearchText = string.Empty;
    private string _itemSearchText = string.Empty;
    private string _lineQuantityText = "1";
    private string _lineUnitPriceText = "0";
    private string _lineRemark = string.Empty;
    private string _memo = string.Empty;
    private string _statusMessage = "거래처를 찾고 품목을 추가한 뒤 전표를 저장하세요.";
    private bool _isBusy;
    private bool _isLoaded;
    private bool _isItemEntrySheetVisible;
    private Guid? _editingLineId;
    private MobileOfficeOption? _selectedInvoiceOffice;
    private string _sessionOfficeCode = OfficeCodeCatalog.Usenet;
    private string _sessionTenantCode = TenantScopeCatalog.UsenetGroup;
    private string _sessionUsername = string.Empty;

    public InvoiceDraftViewModel(
        GeoraePlanApiClient api,
        SyncCoordinator syncCoordinator,
        MobileRefreshCoordinator refreshCoordinator,
        SessionStore sessionStore,
        RecentItemSelectionStore recentItemSelectionStore)
    {
        _api = api;
        _syncCoordinator = syncCoordinator;
        _refreshCoordinator = refreshCoordinator;
        _sessionStore = sessionStore;
        _recentItemSelectionStore = recentItemSelectionStore;
        LoadCommand = new AsyncCommand(LoadAsync);
        SaveDraftCommand = new AsyncCommand(SaveDraftAsync);

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
    public ObservableCollection<RecentItemSelectionRecord> VisibleRecentItems { get; } = new();

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
            if (SetProperty(ref _selectedInvoiceOffice, value))
            {
                OnPropertyChanged(nameof(SelectedInvoiceOfficeCode));
                OnPropertyChanged(nameof(SelectedInvoiceOfficeSummary));
            }
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

    public bool IsItemEntrySheetVisible
    {
        get => _isItemEntrySheetVisible;
        private set => SetProperty(ref _isItemEntrySheetVisible, value);
    }

    public bool HasSelectedCustomer => SelectedCustomer is not null;
    public bool HasSelectedCategory => SelectedCategory is not null;
    public bool IsCategoryChooserVisible => !HasSelectedCategory;
    public bool HasSelectedItem => SelectedItem is not null;
    public bool HasVisibleRecentItems => VisibleRecentItems.Count > 0;
    public bool CanChooseInvoiceOffice => InvoiceOfficeOptions.Count > 1;
    public string SelectedInvoiceOfficeCode => SelectedInvoiceOffice?.Code ?? _sessionOfficeCode;
    public string SelectedInvoiceOfficeSummary => SelectedInvoiceOffice is null
        ? "전표 담당지점을 선택하세요."
        : $"선택 담당지점: {SelectedInvoiceOffice.DisplayName}";
    public string SelectedCustomerSummary => SelectedCustomer is null
        ? "거래처를 아직 선택하지 않았습니다."
        : $"선택 거래처: {SelectedCustomer.NameOriginal}";
    public string SelectedCategoryHeader => SelectedCategory is null
        ? "품목분류를 선택하세요."
        : $"선택 분류: {SelectedCategory.Name}";
    public string SelectedCategorySummary => SelectedCategory is null
        ? "품목분류를 먼저 선택하세요."
        : $"해당 분류 품목 {ItemSearchResults.Count:N0}건";
    public string SelectedItemSheetTitle => SelectedItem is null
        ? "선택 품목"
        : $"선택 품목: {SelectedItem.NameOriginal}";
    public string SelectedItemSheetSpecification => SelectedItem is null
        ? "규격 정보 없음"
        : string.IsNullOrWhiteSpace(SelectedItem.SpecificationOriginal)
            ? "규격 정보 없음"
            : $"규격: {SelectedItem.SpecificationOriginal}";
    public string SelectedItemPriceSummary => SelectedItem is null
        ? "단가 정보 없음"
        : $"매입 {SelectedItem.PurchasePrice:N0} / 판매 {SelectedItem.SalePrice:N0} / 소매 {SelectedItem.RetailPrice:N0}";
    public string SelectedItemMemo => SelectedItem is null
        ? "메모 없음"
        : string.IsNullOrWhiteSpace(SelectedItem.SimpleMemo) && string.IsNullOrWhiteSpace(SelectedItem.Notes)
            ? "메모 없음"
            : string.IsNullOrWhiteSpace(SelectedItem.SimpleMemo)
                ? SelectedItem.Notes
                : SelectedItem.SimpleMemo;
    public string SelectedItemStockSummary => SelectedItem is null
        ? "재고 정보 없음"
        : $"현재재고 {SelectedItem.CurrentStock:N0} / 안전재고 {SelectedItem.SafetyStock:N0}";
    public string LineActionText => _editingLineId.HasValue ? "품목 수정" : "품목 추가";
    public string DraftSummary => LineItems.Count == 0
        ? "추가된 품목이 없습니다."
        : $"총 {LineItems.Count:N0}건 / 합계 {LineItems.Sum(x => x.LineAmount):N0}원";
    public double CustomerSearchResultsHeight => CalculateListHeight(CustomerSearchResults.Count, 56, 42, 2);
    public double ItemSearchResultsHeight => CalculateListHeight(ItemSearchResults.Count, 64, 48, 5);
    public double SelectedItemBranchStocksHeight => CalculateListHeight(SelectedItemBranchStocks.Count, 32, 40, 4);
    public double LineItemsHeight => CalculateListHeight(LineItems.Count, 74, 42, 3);

    public AsyncCommand LoadCommand { get; }
    public AsyncCommand SaveDraftCommand { get; }

    public async Task LoadAsync()
    {
        if (IsBusy)
            return;

        try
        {
            IsBusy = true;
            InitializeOfficeOptions();
            await LoadRecentSelectionsAsync();

            if (_isLoaded)
                return;

            StatusMessage = "전표 작성에 필요한 분류 정보를 불러오고 있습니다.";
            var categories = await _api.GetItemCategoriesAsync();
            ItemCategories.Clear();
            foreach (var category in categories.OrderBy(x => x.Name))
                ItemCategories.Add(category);

            _isLoaded = true;
            StatusMessage = ItemCategories.Count == 0
                ? "등록된 품목분류가 없습니다."
                : "품목분류를 선택한 뒤 품목을 연속해서 추가하세요.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"기초 목록 불러오기 실패: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
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
            var customers = await _api.GetCustomersAsync(keyword);
            CustomerSearchResults.Clear();
            foreach (var customer in customers)
                CustomerSearchResults.Add(customer);

            StatusMessage = customers.Count == 0
                ? "조건에 맞는 거래처가 없습니다."
                : $"검색 결과 {customers.Count:N0}건";
        }
        catch (Exception ex)
        {
            StatusMessage = $"거래처 검색 실패: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public Task SelectCustomerAsync(CustomerDto customer)
    {
        SelectedCustomer = customer;
        StatusMessage = $"{customer.NameOriginal} 거래처를 선택했습니다.";
        return Task.CompletedTask;
    }

    public async Task PreselectCustomerAsync(Guid customerId, string customerName)
    {
        var customer = await _api.GetCustomerByIdAsync(customerId);
        if (customer is null && !string.IsNullOrWhiteSpace(customerName))
        {
            var candidates = await _api.GetCustomersAsync(customerName);
            customer = candidates.FirstOrDefault(candidate => candidate.Id == customerId)
                ?? candidates.FirstOrDefault();
        }

        if (customer is null)
            return;

        SelectedCustomer = customer;
        CustomerSearchText = customer.NameOriginal;
        StatusMessage = $"{customer.NameOriginal} 거래처 기준으로 전표를 입력하세요.";
    }

    public async Task SelectCategoryAsync(ItemCategorySummaryDto category, bool resetSearch = true)
    {
        if (category is null)
            return;

        if (resetSearch)
            ItemSearchText = string.Empty;

        SelectedCategory = category;
        ResetItemSelection(clearCategory: false);
        await SearchItemsAsync();
    }

    public void ClearSelectedCategory()
    {
        SelectedCategory = null;
        ItemSearchText = string.Empty;
        ItemSearchResults.Clear();
        ResetItemSelection(clearCategory: false);
        StatusMessage = "품목분류를 다시 선택하세요.";
    }

    public async Task SearchItemsAsync()
    {
        if (IsBusy)
            return;

        if (SelectedCategory is null)
        {
            StatusMessage = "품목분류를 먼저 선택하세요.";
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = $"{SelectedCategory.Name} 분류 품목을 조회하고 있습니다.";
            var items = await _api.GetItemsAsync(ItemSearchText, SelectedCategory.Name);
            ItemSearchResults.Clear();
            foreach (var item in items.OrderBy(x => x.NameOriginal))
                ItemSearchResults.Add(item);

            StatusMessage = items.Count == 0
                ? "현재 분류에서 조건에 맞는 품목이 없습니다."
                : $"{SelectedCategory.Name} 품목 {items.Count:N0}건";
            OnPropertyChanged(nameof(SelectedCategorySummary));
        }
        catch (Exception ex)
        {
            StatusMessage = $"품목 검색 실패: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public Task SelectItemAsync(ItemDto item)
        => OpenItemEntrySheetAsync(item, recordRecent: true);

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
                          SpecificationOriginal = recent.SpecificationOriginal
                      };

        await OpenItemEntrySheetAsync(matched, recordRecent: true);
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

            StatusMessage = "전표 품목을 수정했습니다. 같은 분류에서 다음 품목을 계속 추가하세요.";
        }
        else
        {
            LineItems.Add(draft);
            StatusMessage = "전표 품목을 추가했습니다. 같은 분류에서 다음 품목을 계속 선택하세요.";
        }

        await RecordRecentSelectionAsync(SelectedItem);
        ResetItemSelection(clearCategory: false);
        OnPropertyChanged(nameof(DraftSummary));
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
        StatusMessage = $"{line.ItemNameOriginal} 품목을 목록에서 제거했습니다.";
        return Task.CompletedTask;
    }

    public async Task SaveDraftAsync()
    {
        if (IsBusy)
            return;

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

        var now = DateTime.UtcNow;
        var invoiceId = Guid.NewGuid();
        var lines = LineItems.Select(line => line.ToDto(invoiceId)).ToList();
        var total = lines.Sum(line => line.LineAmount);

        var invoice = new InvoiceDto
        {
            Id = invoiceId,
            CustomerId = SelectedCustomer.Id,
            OfficeCode = SelectedInvoiceOfficeCode,
            InvoiceNumber = string.Empty,
            LocalTempNumber = $"M-{DateTime.Now:yyyyMMdd-HHmmss}",
            VoucherType = VoucherType.Sales,
            InvoiceDate = DateOnly.FromDateTime(InvoiceDate),
            TotalAmount = total,
            SupplyAmount = total,
            VatAmount = 0,
            Memo = Memo.Trim(),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Revision = 0,
            IsDeleted = false,
            Lines = lines
        };

        try
        {
            IsBusy = true;
            StatusMessage = "전표를 저장하고 있습니다.";
            var state = await _syncCoordinator.SaveInvoiceImmediatelyAsync(invoice);

            if (state.PendingInvoiceCount == 0)
            {
                _refreshCoordinator.MarkInvoicesChanged();
                StatusMessage = string.IsNullOrWhiteSpace(state.LastError)
                    ? "전표 저장 및 서버 반영 완료"
                    : $"전표 저장 완료 / 최신 데이터 새로고침 대기: {state.LastError}";

                if (SavedSuccessfully is not null)
                    await SavedSuccessfully.Invoke();
            }
            else
            {
                StatusMessage = $"전표 저장 완료(오프라인/재시도 대기): {state.LastError}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"전표 저장 실패: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task OpenItemEntrySheetAsync(ItemDto item, bool recordRecent)
    {
        SelectedItem = item;
        _editingLineId = recordRecent ? null : _editingLineId;
        OnPropertyChanged(nameof(LineActionText));
        LineQuantityText = "1";
        LineUnitPriceText = ResolveDefaultUnitPrice(item).ToString("0.##");
        LineRemark = string.IsNullOrWhiteSpace(item.SimpleMemo) ? item.Notes : item.SimpleMemo;
        SelectedItemBranchStocks.Clear();

        try
        {
            var detail = item.Id == Guid.Empty ? null : await _api.GetItemDetailAsync(item.Id);
            if (detail?.Item is not null)
            {
                SelectedItem = detail.Item;
                LineUnitPriceText = ResolveDefaultUnitPrice(detail.Item).ToString("0.##");
                if (string.IsNullOrWhiteSpace(LineRemark))
                    LineRemark = string.IsNullOrWhiteSpace(detail.Item.SimpleMemo) ? detail.Item.Notes : detail.Item.SimpleMemo;
            }

            foreach (var stock in detail?.BranchStocks ?? [])
                SelectedItemBranchStocks.Add(stock);
        }
        catch
        {
            if (item.Id != Guid.Empty)
            {
                SelectedItemBranchStocks.Add(new ItemWarehouseStockDto
                {
                    ItemId = item.Id,
                    WarehouseCode = "전체",
                    Quantity = item.CurrentStock,
                    UpdatedAtUtc = DateTime.UtcNow
                });
            }
        }

        if (recordRecent && SelectedItem is not null)
            await RecordRecentSelectionAsync(SelectedItem);

        IsItemEntrySheetVisible = true;
        StatusMessage = $"{item.NameOriginal} 품목을 선택했습니다. 수량과 단가를 입력하세요.";
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
        _sessionTenantCode = string.IsNullOrWhiteSpace(snapshot.TenantCode) ? TenantScopeCatalog.UsenetGroup : snapshot.TenantCode;
        _sessionUsername = snapshot.Username ?? string.Empty;

        var options = new List<string>();
        if (snapshot.IsAdmin)
        {
            options.Add(_sessionOfficeCode);
            foreach (var code in OfficeCodeCatalog.AllScopes)
            {
                if (!options.Contains(code, StringComparer.OrdinalIgnoreCase))
                    options.Add(code);
            }
        }
        else
        {
            options.Add(_sessionOfficeCode);
        }

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
    }

    private async Task LoadRecentSelectionsAsync()
    {
        var snapshot = _sessionStore.GetSnapshot();
        _sessionTenantCode = string.IsNullOrWhiteSpace(snapshot.TenantCode) ? TenantScopeCatalog.UsenetGroup : snapshot.TenantCode;
        _sessionUsername = snapshot.Username ?? string.Empty;

        _recentSelections.Clear();
        var records = await _recentItemSelectionStore.LoadAsync(_sessionTenantCode, _sessionUsername);
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
            SelectedAtUtc = DateTime.UtcNow
        });

        if (_recentSelections.Count > 5)
            _recentSelections.RemoveRange(5, _recentSelections.Count - 5);

        await _recentItemSelectionStore.SaveAsync(_sessionTenantCode, _sessionUsername, _recentSelections);
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

    private static bool CategoryEquals(string? left, string? right)
    {
        var normalizedLeft = string.IsNullOrWhiteSpace(left) ? "미분류" : left.Trim();
        var normalizedRight = string.IsNullOrWhiteSpace(right) ? "미분류" : right.Trim();
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
        OnPropertyChanged(nameof(HasVisibleRecentItems));
        OnPropertyChanged(nameof(SelectedCategorySummary));
    }

    private static double CalculateListHeight(int count, double rowHeight, double emptyHeight, int maxVisibleRows)
    {
        if (count <= 0)
            return emptyHeight;

        var visibleRows = Math.Min(count, maxVisibleRows);
        return (visibleRows * rowHeight) + Math.Max(0, visibleRows - 1) * 6;
    }

    private static decimal ResolveDefaultUnitPrice(ItemDto item)
        => item.SalePrice > 0m
            ? item.SalePrice
            : item.RetailPrice > 0m
                ? item.RetailPrice
                : item.PurchasePrice > 0m
                    ? item.PurchasePrice
                    : 0m;
}
