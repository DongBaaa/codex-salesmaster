using System.Collections.ObjectModel;
using System.Collections.Specialized;
using GeoraePlan.Mobile.App.Models;
using GeoraePlan.Mobile.App.Services;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.ViewModels;

public sealed class ItemsViewModel : ObservableObject
{
    private const string UncategorizedName = "미분류";

    private readonly GeoraePlanApiClient _api;
    private readonly RecentItemSelectionStore _recentItemSelectionStore;
    private readonly SessionStore _sessionStore;

    private readonly List<RecentItemSelectionRecord> _recentSelections = new();

    private string _searchText = string.Empty;
    private string _statusMessage = "품목분류를 선택하세요.";
    private string _lineQuantityText = "1";
    private string _lineUnitPriceText = "0";
    private string _lineRemark = string.Empty;
    private bool _isBusy;
    private bool _isItemEntrySheetVisible;
    private DateTime? _lastRefreshUtc;
    private ItemCategorySummaryDto? _selectedCategory;
    private ItemDto? _selectedItem;
    private string _sessionTenantCode = string.Empty;
    private string _sessionUsername = string.Empty;

    public ItemsViewModel(
        GeoraePlanApiClient api,
        RecentItemSelectionStore recentItemSelectionStore,
        SessionStore sessionStore)
    {
        _api = api;
        _recentItemSelectionStore = recentItemSelectionStore;
        _sessionStore = sessionStore;

        RefreshCommand = new AsyncCommand(RefreshAsync);

        DraftLines.CollectionChanged += HandleDraftLinesChanged;
        VisibleRecentItems.CollectionChanged += HandleVisibleRecentItemsChanged;
    }

    public ObservableCollection<ItemCategorySummaryDto> ItemCategories { get; } = new();
    public ObservableCollection<ItemDto> Items { get; } = new();
    public ObservableCollection<ItemWarehouseStockDto> SelectedItemBranchStocks { get; } = new();
    public ObservableCollection<RecentItemSelectionRecord> VisibleRecentItems { get; } = new();
    public ObservableCollection<InvoiceLineDraftItem> DraftLines { get; } = new();

    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value);
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

    public ItemCategorySummaryDto? SelectedCategory
    {
        get => _selectedCategory;
        private set
        {
            if (!SetProperty(ref _selectedCategory, value))
                return;

            OnPropertyChanged(nameof(HasSelectedCategory));
            OnPropertyChanged(nameof(IsCategoryChooserVisible));
            OnPropertyChanged(nameof(SelectedCategoryHeader));
            OnPropertyChanged(nameof(SelectedCategorySummary));
            RefreshVisibleRecentItems();
        }
    }

    public ItemDto? SelectedItem
    {
        get => _selectedItem;
        private set
        {
            if (!SetProperty(ref _selectedItem, value))
                return;

            OnPropertyChanged(nameof(HasSelectedItem));
            OnPropertyChanged(nameof(SelectedItemSheetTitle));
            OnPropertyChanged(nameof(SelectedItemSheetSpecification));
            OnPropertyChanged(nameof(SelectedItemPriceSummary));
            OnPropertyChanged(nameof(SelectedItemMemo));
            OnPropertyChanged(nameof(SelectedItemStockSummary));
        }
    }

    public bool HasSelectedCategory => SelectedCategory is not null;
    public bool IsCategoryChooserVisible => !HasSelectedCategory;
    public bool HasVisibleRecentItems => VisibleRecentItems.Count > 0;
    public bool HasSelectedItem => SelectedItem is not null;
    public bool HasDraftLines => DraftLines.Count > 0;

    public string SelectedCategoryHeader => SelectedCategory is null
        ? "선택된 분류 없음"
        : $"선택된 분류: {NormalizeCategoryName(SelectedCategory.Name)}";

    public string SelectedCategorySummary => SelectedCategory is null
        ? "품목분류를 먼저 선택하세요."
        : $"현재 분류 품목 {Items.Count:N0}건";

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
        : string.IsNullOrWhiteSpace(SelectedItem.SimpleMemo)
            ? "메모 없음"
            : SelectedItem.SimpleMemo;

    public string SelectedItemStockSummary => SelectedItem is null
        ? "재고 정보 없음"
        : $"현재재고 {SelectedItem.CurrentStock:N0} / 안전재고 {SelectedItem.SafetyStock:N0}";

    public string DraftSummary => DraftLines.Count == 0
        ? "전표 목록이 비어 있습니다."
        : $"전표 목록 {DraftLines.Count:N0}건 / 합계 {DraftLines.Sum(x => x.LineAmount):N0}원";

    public double ItemListHeight => CalculateListHeight(Items.Count, 68, 48, 5);
    public double DraftLinesHeight => CalculateListHeight(DraftLines.Count, 80, 48, 4);
    public double SelectedItemBranchStocksHeight => CalculateListHeight(SelectedItemBranchStocks.Count, 32, 36, 3);

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
            EnsureSessionContext();
            await LoadRecentSelectionsAsync();

            StatusMessage = "품목분류를 불러오고 있습니다.";
            var categories = await _api.GetItemCategoriesAsync();
            ReplaceCategories(categories);

            if (SelectedCategory is not null)
            {
                var matchedCategory = FindCategoryByName(SelectedCategory.Name);
                SelectedCategory = matchedCategory;
                if (SelectedCategory is not null)
                    await SearchItemsCoreAsync();
                else
                {
                    Items.Clear();
                    StatusMessage = "품목분류를 다시 선택하세요.";
                }
            }
            else
            {
                Items.Clear();
                StatusMessage = ItemCategories.Count == 0
                    ? "등록된 품목분류가 없습니다."
                    : "품목분류를 선택한 뒤 품목을 연속으로 추가하세요.";
            }

            _lastRefreshUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            StatusMessage = $"품목 화면 초기화 실패: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SelectCategoryAsync(ItemCategorySummaryDto category, bool resetSearch = true)
    {
        if (category is null)
            return;

        if (resetSearch)
            SearchText = string.Empty;

        SelectedCategory = category;
        ResetSheetState(clearCategory: false);
        await SearchItemsAsync();
    }

    public void ClearSelectedCategory()
    {
        SelectedCategory = null;
        SearchText = string.Empty;
        Items.Clear();
        ResetSheetState(clearCategory: false);
        StatusMessage = "품목분류를 다시 선택하세요.";
        OnPropertyChanged(nameof(ItemListHeight));
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
            await SearchItemsCoreAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"품목 조회 실패: {ex.Message}";
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

        var normalizedCategory = NormalizeCategoryName(recent.CategoryName);
        if (SelectedCategory is null || !CategoryEquals(SelectedCategory.Name, normalizedCategory))
        {
            var category = FindCategoryByName(normalizedCategory);
            if (category is not null)
                await SelectCategoryAsync(category);
        }

        var matched = Items.FirstOrDefault(item => item.Id == recent.ItemId);
        if (matched is null)
        {
            var categoryQuery = BuildCategoryQueryValue(normalizedCategory);
            var candidates = await _api.GetItemsAsync(recent.ItemNameOriginal, categoryQuery);
            matched = candidates.FirstOrDefault(item => item.Id == recent.ItemId)
                      ?? candidates.FirstOrDefault(item => NameAndSpecEquals(item, recent));
        }

        if (matched is null)
        {
            StatusMessage = $"{recent.ItemNameOriginal} 품목을 찾지 못했습니다.";
            return;
        }

        await OpenItemEntrySheetAsync(matched, recordRecent: true);
    }

    public Task CancelItemEntryAsync()
    {
        ResetSheetState(clearCategory: false);
        return Task.CompletedTask;
    }

    public async Task AddDraftLineAsync()
    {
        if (SelectedItem is null)
        {
            StatusMessage = "품목을 먼저 선택하세요.";
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

        var line = InvoiceLineDraftItem.FromItem(SelectedItem, quantity);
        line.UnitPrice = unitPrice;
        line.Remark = LineRemark.Trim();
        line.CategoryName = NormalizeCategoryName(SelectedCategory?.Name ?? SelectedItem.CategoryName);
        DraftLines.Add(line);

        await RecordRecentSelectionAsync(SelectedItem);
        ResetSheetState(clearCategory: false);
        StatusMessage = $"{line.ItemNameOriginal} 품목을 전표 목록에 추가했습니다. 같은 분류에서 계속 선택하세요.";
    }

    public Task RemoveDraftLineAsync(InvoiceLineDraftItem line)
    {
        DraftLines.Remove(line);
        StatusMessage = $"{line.ItemNameOriginal} 품목을 전표 목록에서 제거했습니다.";
        return Task.CompletedTask;
    }

    private async Task SearchItemsCoreAsync()
    {
        if (SelectedCategory is null)
            return;

        var normalizedCategory = NormalizeCategoryName(SelectedCategory.Name);
        StatusMessage = $"{normalizedCategory} 분류 품목을 조회하고 있습니다.";
        var items = await _api.GetItemsAsync(SearchText, BuildCategoryQueryValue(normalizedCategory));

        Items.Clear();
        foreach (var item in items.OrderBy(item => item.NameOriginal))
        {
            item.CategoryName = NormalizeCategoryName(item.CategoryName);
            Items.Add(item);
        }

        StatusMessage = items.Count == 0
            ? "현재 분류에서 표시할 품목이 없습니다."
            : $"{normalizedCategory} 분류 품목 {items.Count:N0}건";
        OnPropertyChanged(nameof(ItemListHeight));
        OnPropertyChanged(nameof(SelectedCategorySummary));
        RefreshVisibleRecentItems();
    }

    private async Task OpenItemEntrySheetAsync(ItemDto item, bool recordRecent)
    {
        if (item is null)
            return;

        ItemDetailDto? detail = null;
        if (item.Id != Guid.Empty)
        {
            try
            {
                detail = await _api.GetItemDetailAsync(item.Id);
            }
            catch
            {
                detail = null;
            }
        }

        var selected = detail?.Item ?? item;
        selected.CategoryName = NormalizeCategoryName(selected.CategoryName);
        SelectedItem = selected;

        SelectedItemBranchStocks.Clear();
        if (detail?.BranchStocks?.Count > 0)
        {
            foreach (var stock in detail.BranchStocks)
                SelectedItemBranchStocks.Add(stock);
        }

        LineQuantityText = "1";
        LineUnitPriceText = ResolveDefaultUnitPrice(selected).ToString("0.##");
        LineRemark = selected.SimpleMemo ?? string.Empty;
        IsItemEntrySheetVisible = true;
        OnPropertyChanged(nameof(SelectedItemBranchStocksHeight));

        if (recordRecent)
            await RecordRecentSelectionAsync(selected);
    }

    private async Task LoadRecentSelectionsAsync()
    {
        var loaded = await _recentItemSelectionStore.LoadAsync(_sessionTenantCode, _sessionUsername);
        _recentSelections.Clear();
        _recentSelections.AddRange(loaded);
        RefreshVisibleRecentItems();
    }

    private async Task RecordRecentSelectionAsync(ItemDto item)
    {
        var normalizedCategory = NormalizeCategoryName(item.CategoryName);
        _recentSelections.RemoveAll(record => record.ItemId == item.Id);
        _recentSelections.Insert(0, new RecentItemSelectionRecord
        {
            ItemId = item.Id,
            CategoryName = normalizedCategory,
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
        var currentCategory = NormalizeCategoryName(SelectedCategory?.Name);
        var ordered = _recentSelections
            .OrderByDescending(record => CategoryEquals(currentCategory, record.CategoryName))
            .ThenByDescending(record => record.SelectedAtUtc)
            .Take(5)
            .ToList();

        VisibleRecentItems.Clear();
        foreach (var record in ordered)
            VisibleRecentItems.Add(record);
    }

    private void ReplaceCategories(IEnumerable<ItemCategorySummaryDto> categories)
    {
        var normalized = categories
            .Select(category => new ItemCategorySummaryDto
            {
                Name = NormalizeCategoryName(category.Name),
                ItemCount = category.ItemCount
            })
            .GroupBy(category => category.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ItemCategorySummaryDto
            {
                Name = group.Key,
                ItemCount = group.Sum(item => item.ItemCount)
            })
            .OrderBy(category => category.Name == UncategorizedName ? "zzz" : category.Name)
            .ToList();

        ItemCategories.Clear();
        foreach (var category in normalized)
            ItemCategories.Add(category);
    }

    private void EnsureSessionContext()
    {
        var snapshot = _sessionStore.GetSnapshot();
        var tenantCode = string.IsNullOrWhiteSpace(snapshot.TenantCode) ? "default" : snapshot.TenantCode.Trim().ToUpperInvariant();
        var username = string.IsNullOrWhiteSpace(snapshot.Username) ? "anonymous" : snapshot.Username.Trim().ToLowerInvariant();

        var changed = !string.Equals(_sessionTenantCode, tenantCode, StringComparison.OrdinalIgnoreCase) ||
                      !string.Equals(_sessionUsername, username, StringComparison.OrdinalIgnoreCase);

        _sessionTenantCode = tenantCode;
        _sessionUsername = username;

        if (!changed)
            return;

        _recentSelections.Clear();
        VisibleRecentItems.Clear();
        DraftLines.Clear();
        SelectedCategory = null;
        Items.Clear();
        ResetSheetState(clearCategory: false);
    }

    private void ResetSheetState(bool clearCategory)
    {
        SelectedItem = null;
        SelectedItemBranchStocks.Clear();
        LineQuantityText = "1";
        LineUnitPriceText = "0";
        LineRemark = string.Empty;
        IsItemEntrySheetVisible = false;
        OnPropertyChanged(nameof(SelectedItemBranchStocksHeight));

        if (clearCategory)
        {
            SelectedCategory = null;
            SearchText = string.Empty;
        }
    }

    private ItemCategorySummaryDto? FindCategoryByName(string? categoryName)
        => ItemCategories.FirstOrDefault(category => CategoryEquals(category.Name, categoryName));

    private void HandleDraftLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasDraftLines));
        OnPropertyChanged(nameof(DraftSummary));
        OnPropertyChanged(nameof(DraftLinesHeight));
    }

    private void HandleVisibleRecentItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => OnPropertyChanged(nameof(HasVisibleRecentItems));

    private static double CalculateListHeight(int count, double rowHeight, double emptyHeight, int maxVisibleRows)
    {
        if (count <= 0)
            return emptyHeight;

        var rows = Math.Min(count, maxVisibleRows);
        return rowHeight * rows;
    }

    private static decimal ResolveDefaultUnitPrice(ItemDto item)
    {
        if (item.SalePrice > 0m)
            return item.SalePrice;
        if (item.RetailPrice > 0m)
            return item.RetailPrice;
        if (item.PurchasePrice > 0m)
            return item.PurchasePrice;
        return 0m;
    }

    private static string NormalizeCategoryName(string? categoryName)
        => string.IsNullOrWhiteSpace(categoryName)
            ? UncategorizedName
            : categoryName.Trim();

    private static string BuildCategoryQueryValue(string categoryName)
        => string.Equals(NormalizeCategoryName(categoryName), UncategorizedName, StringComparison.OrdinalIgnoreCase)
            ? UncategorizedName
            : NormalizeCategoryName(categoryName);

    private static bool CategoryEquals(string? left, string? right)
        => string.Equals(NormalizeCategoryName(left), NormalizeCategoryName(right), StringComparison.OrdinalIgnoreCase);

    private static bool NameAndSpecEquals(ItemDto item, RecentItemSelectionRecord record)
        => string.Equals(item.NameOriginal?.Trim(), record.ItemNameOriginal?.Trim(), StringComparison.OrdinalIgnoreCase) &&
           string.Equals(item.SpecificationOriginal?.Trim(), record.SpecificationOriginal?.Trim(), StringComparison.OrdinalIgnoreCase);
}
