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
    private Guid? _editingLineId;
    private MobileOfficeOption? _selectedInvoiceOffice;
    private string _sessionOfficeCode = OfficeCodeCatalog.Usenet;

    public InvoiceDraftViewModel(
        GeoraePlanApiClient api,
        SyncCoordinator syncCoordinator,
        MobileRefreshCoordinator refreshCoordinator,
        SessionStore sessionStore)
    {
        _api = api;
        _syncCoordinator = syncCoordinator;
        _refreshCoordinator = refreshCoordinator;
        _sessionStore = sessionStore;
        LoadCommand = new AsyncCommand(LoadAsync);
        SaveDraftCommand = new AsyncCommand(SaveDraftAsync);

        CustomerSearchResults.CollectionChanged += HandleCollectionChanged;
        ItemSearchResults.CollectionChanged += HandleCollectionChanged;
        SelectedItemBranchStocks.CollectionChanged += HandleCollectionChanged;
        LineItems.CollectionChanged += HandleCollectionChanged;
    }

    public event Func<Task>? SavedSuccessfully;

    public ObservableCollection<CustomerDto> CustomerSearchResults { get; } = new();
    public ObservableCollection<ItemCategorySummaryDto> ItemCategories { get; } = new();
    public ObservableCollection<ItemDto> ItemSearchResults { get; } = new();
    public ObservableCollection<ItemWarehouseStockDto> SelectedItemBranchStocks { get; } = new();
    public ObservableCollection<InvoiceLineDraftItem> LineItems { get; } = new();
    public ObservableCollection<MobileOfficeOption> InvoiceOfficeOptions { get; } = new();

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
        set
        {
            if (SetProperty(ref _selectedCategory, value))
            {
                ItemSearchResults.Clear();
                SelectedItem = null;
                SelectedItemBranchStocks.Clear();
                OnPropertyChanged(nameof(SelectedCategorySummary));
                OnPropertyChanged(nameof(HasSelectedItem));
                if (value is not null)
                    StatusMessage = $"{value.Name} 분류에서 품목을 찾으세요.";
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
                OnPropertyChanged(nameof(SelectedItemSummary));
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

    public bool HasSelectedCustomer => SelectedCustomer is not null;
    public bool HasSelectedItem => SelectedItem is not null;
    public bool CanChooseInvoiceOffice => InvoiceOfficeOptions.Count > 1;
    public string SelectedInvoiceOfficeCode => SelectedInvoiceOffice?.Code ?? _sessionOfficeCode;
    public string SelectedInvoiceOfficeSummary => SelectedInvoiceOffice is null
        ? "전표 소속을 선택하세요."
        : $"선택 소속: {SelectedInvoiceOffice.DisplayName}";
    public string SelectedCustomerSummary => SelectedCustomer is null
        ? "거래처를 아직 선택하지 않았습니다."
        : $"선택 거래처: {SelectedCustomer.NameOriginal}";
    public string SelectedCategorySummary => SelectedCategory is null
        ? "품목분류를 선택하세요."
        : $"선택 분류: {SelectedCategory.Name} ({SelectedCategory.ItemCount:N0}건)";
    public string SelectedItemSummary => SelectedItem is null
        ? "품목을 선택하세요."
        : $"{SelectedItem.NameOriginal} / {SelectedItem.SpecificationOriginal}";
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
    public string LineActionText => _editingLineId.HasValue ? "선택 품목 수정" : "품목 추가";
    public string DraftSummary => LineItems.Count == 0
        ? "추가된 품목이 없습니다."
        : $"품목 {LineItems.Count:N0}건 / 합계 {LineItems.Sum(x => x.LineAmount):N0}원";
    public double CustomerSearchResultsHeight => CalculateListHeight(CustomerSearchResults.Count, 56, 42, 2);
    public double ItemSearchResultsHeight => CalculateListHeight(ItemSearchResults.Count, 60, 42, 2);
    public double SelectedItemBranchStocksHeight => CalculateListHeight(SelectedItemBranchStocks.Count, 30, 36, 3);
    public double LineItemsHeight => CalculateListHeight(LineItems.Count, 74, 42, 2);

    public AsyncCommand LoadCommand { get; }
    public AsyncCommand SaveDraftCommand { get; }

    public async Task LoadAsync()
    {
        if (IsBusy || _isLoaded)
            return;

        try
        {
            IsBusy = true;
            InitializeOfficeOptions();
            StatusMessage = "전표 작성을 위한 분류 정보를 불러오고 있습니다.";
            await _syncCoordinator.TryBackgroundSyncAsync("invoice-draft-load", TimeSpan.FromSeconds(30));

            var categories = await _api.GetItemCategoriesAsync();
            ItemCategories.Clear();
            foreach (var category in categories.OrderBy(x => x.Name))
                ItemCategories.Add(category);

            _isLoaded = true;
            StatusMessage = ItemCategories.Count == 0
                ? "등록된 품목분류가 없습니다."
                : $"품목분류 {ItemCategories.Count:N0}건";
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
                : $"거래처 검색 결과 {customers.Count:N0}건";
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
        StatusMessage = $"{customer.NameOriginal} 거래처가 선택되었습니다.";
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
            StatusMessage = "선택한 분류에서 품목을 찾고 있습니다.";
            var items = await _api.GetItemsAsync(ItemSearchText, SelectedCategory.Name);
            ItemSearchResults.Clear();
            foreach (var item in items.OrderBy(x => x.NameOriginal))
                ItemSearchResults.Add(item);

            StatusMessage = items.Count == 0
                ? "조건에 맞는 품목이 없습니다."
                : $"품목 검색 결과 {items.Count:N0}건";
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

    public async Task SelectItemAsync(ItemDto item)
    {
        SelectedItem = item;
        LineQuantityText = "1";
        LineUnitPriceText = ResolveDefaultUnitPrice(item).ToString("0.##");
        LineRemark = string.IsNullOrWhiteSpace(item.SimpleMemo) ? item.Notes : item.SimpleMemo;
        _editingLineId = null;
        OnPropertyChanged(nameof(LineActionText));

        SelectedItemBranchStocks.Clear();
        try
        {
            var detail = await _api.GetItemDetailAsync(item.Id);
            if (detail?.Item is not null)
                SelectedItem = detail.Item;

            foreach (var stock in detail?.BranchStocks ?? [])
                SelectedItemBranchStocks.Add(stock);
        }
        catch
        {
            SelectedItemBranchStocks.Add(new ItemWarehouseStockDto
            {
                ItemId = item.Id,
                WarehouseCode = "전체",
                Quantity = item.CurrentStock,
                UpdatedAtUtc = DateTime.UtcNow
            });
        }

        StatusMessage = $"{item.NameOriginal} 품목이 선택되었습니다.";
    }

    public Task AddOrUpdateLineAsync()
    {
        if (SelectedItem is null)
        {
            StatusMessage = "추가할 품목을 먼저 선택하세요.";
            return Task.CompletedTask;
        }

        if (!decimal.TryParse(LineQuantityText, out var quantity) || quantity <= 0m)
        {
            StatusMessage = "수량을 올바르게 입력하세요.";
            return Task.CompletedTask;
        }

        if (!decimal.TryParse(LineUnitPriceText, out var unitPrice) || unitPrice < 0m)
        {
            StatusMessage = "단가를 올바르게 입력하세요.";
            return Task.CompletedTask;
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
            StatusMessage = "전표 품목을 수정했습니다.";
        }
        else
        {
            LineItems.Add(draft);
            StatusMessage = "전표 품목을 추가했습니다.";
        }

        _editingLineId = null;
        LineQuantityText = "1";
        LineUnitPriceText = ResolveDefaultUnitPrice(SelectedItem).ToString("0.##");
        LineRemark = string.Empty;
        OnPropertyChanged(nameof(LineActionText));
        OnPropertyChanged(nameof(DraftSummary));
        return Task.CompletedTask;
    }

    public Task EditLineAsync(InvoiceLineDraftItem line)
    {
        _editingLineId = line.Id;
        LineQuantityText = line.Quantity.ToString("0.##");
        LineUnitPriceText = line.UnitPrice.ToString("0.##");
        LineRemark = line.Remark;
        OnPropertyChanged(nameof(LineActionText));
        StatusMessage = $"{line.ItemNameOriginal} 품목을 수정 중입니다.";
        return Task.CompletedTask;
    }

    public Task RemoveLineAsync(InvoiceLineDraftItem line)
    {
        LineItems.Remove(line);
        if (_editingLineId == line.Id)
        {
            _editingLineId = null;
            OnPropertyChanged(nameof(LineActionText));
        }

        OnPropertyChanged(nameof(DraftSummary));
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
            await _syncCoordinator.QueueInvoiceDraftAsync(invoice);
            var state = await _syncCoordinator.SynchronizeNowAsync();

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
                StatusMessage = $"전표 저장 완료(동기화 대기): {state.LastError}";
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

    private void InitializeOfficeOptions()
    {
        if (InvoiceOfficeOptions.Count > 0)
            return;

        var snapshot = _sessionStore.GetSnapshot();
        _sessionOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(snapshot.OfficeCode, OfficeCodeCatalog.Usenet);

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
