using System.Collections.ObjectModel;
using GeoraePlan.Mobile.App.Services;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.ViewModels;

public sealed class ItemsViewModel : ObservableObject
{
    private const string UncategorizedName = "미분류";
    private static readonly string[] PreferredCategoryOrder =
    {
        "기타",
        "흑백프린터",
        "컬러프린터",
        "흑백복합기",
        "컬러복합기",
        "하드웨어",
        "소모품",
        "렌탈료",
        UncategorizedName
    };

    private readonly GeoraePlanApiClient _api;
    private readonly SessionStore _sessionStore;

    private string _searchText = string.Empty;
    private string _statusMessage = "품목분류를 선택하세요.";
    private bool _isBusy;
    private DateTime? _lastRefreshUtc;
    private ItemCategorySummaryDto? _selectedCategory;
    private ItemDto? _selectedItem;
    private string _sessionTenantCode = string.Empty;
    private string _sessionUsername = string.Empty;

    public ItemsViewModel(GeoraePlanApiClient api, SessionStore sessionStore)
    {
        _api = api;
        _sessionStore = sessionStore;
        RefreshCommand = new AsyncCommand(RefreshAsync);
    }

    public ObservableCollection<ItemCategorySummaryDto> ItemCategories { get; } = new();
    public ObservableCollection<ItemDto> Items { get; } = new();
    public ObservableCollection<ItemWarehouseStockDto> SelectedItemBranchStocks { get; } = new();

    public AsyncCommand RefreshCommand { get; }

    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value);
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
            OnPropertyChanged(nameof(SelectedItemTitle));
            OnPropertyChanged(nameof(SelectedItemSpecification));
            OnPropertyChanged(nameof(SelectedItemPriceSummary));
            OnPropertyChanged(nameof(SelectedItemStockSummary));
            OnPropertyChanged(nameof(SelectedItemMemo));
        }
    }

    public bool HasSelectedCategory => SelectedCategory is not null;
    public bool IsCategoryChooserVisible => !HasSelectedCategory;
    public bool HasSelectedItem => SelectedItem is not null;

    public string SelectedCategoryHeader => SelectedCategory is null
        ? "선택된 분류 없음"
        : $"선택된 분류: {NormalizeCategoryName(SelectedCategory.Name)}";

    public string SelectedCategorySummary => SelectedCategory is null
        ? "품목분류를 먼저 선택하세요."
        : $"현재 분류 품목 {Items.Count:N0}건";

    public string SelectedItemTitle => SelectedItem?.NameOriginal ?? "품목 상세";

    public string SelectedItemSpecification => SelectedItem is null
        ? "규격 정보 없음"
        : string.IsNullOrWhiteSpace(SelectedItem.SpecificationOriginal)
            ? "규격 정보 없음"
            : $"규격: {SelectedItem.SpecificationOriginal}";

    public string SelectedItemPriceSummary => SelectedItem is null
        ? "기본 단가 정보 없음"
        : $"매입 {SelectedItem.PurchasePrice:N0}원 / 판매 {SelectedItem.SalePrice:N0}원 / 소매 {SelectedItem.RetailPrice:N0}원";

    public string SelectedItemStockSummary => SelectedItem is null
        ? "재고 정보 없음"
        : $"현재재고 {SelectedItem.CurrentStock:N0} / 안전재고 {SelectedItem.SafetyStock:N0}";

    public string SelectedItemMemo => SelectedItem is null
        ? "메모 없음"
        : string.IsNullOrWhiteSpace(SelectedItem.SimpleMemo)
            ? string.IsNullOrWhiteSpace(SelectedItem.Notes) ? "메모 없음" : SelectedItem.Notes
            : SelectedItem.SimpleMemo;

    public double ItemListHeight => CalculateListHeight(Items.Count, 72, 48, 7);
    public double SelectedItemBranchStocksHeight => CalculateListHeight(SelectedItemBranchStocks.Count, 34, 36, 4);

    public bool NeedsRefresh(TimeSpan maxAge)
        => !_lastRefreshUtc.HasValue || DateTime.UtcNow - _lastRefreshUtc.Value >= maxAge;

    public Task PrepareForEntryAsync()
        => RefreshInternalAsync(preserveSelectedCategory: false);

    public async Task RefreshAsync()
        => await RefreshInternalAsync(preserveSelectedCategory: true);

    private async Task RefreshInternalAsync(bool preserveSelectedCategory)
    {
        if (IsBusy)
            return;

        try
        {
            IsBusy = true;
            EnsureSessionContext();

            StatusMessage = "품목분류를 불러오고 있습니다.";
            var categories = await _api.GetItemCategoriesAsync();
            ReplaceCategories(categories);

            if (!preserveSelectedCategory || SelectedCategory is null)
            {
                SearchText = string.Empty;
                SelectedCategory = null;
                Items.Clear();
                ClearSelectedItem();
                StatusMessage = ItemCategories.Count == 0
                    ? "등록된 품목분류가 없습니다."
                    : "품목분류를 먼저 선택하세요.";
            }
            else
            {
                var matchedCategory = FindCategoryByName(SelectedCategory.Name);
                if (matchedCategory is null)
                {
                    ClearSelectedCategory();
                    StatusMessage = "선택된 분류를 찾지 못했습니다. 분류를 다시 선택하세요.";
                }
                else
                {
                    SelectedCategory = matchedCategory;
                    await SearchItemsCoreAsync();
                }
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

        SelectedCategory = new ItemCategorySummaryDto
        {
            Name = NormalizeCategoryName(category.Name),
            ItemCount = category.ItemCount
        };
        ClearSelectedItem();
        await SearchItemsAsync();
    }

    public void ClearSelectedCategory()
    {
        SelectedCategory = null;
        SearchText = string.Empty;
        Items.Clear();
        ClearSelectedItem();
        StatusMessage = "품목분류를 다시 선택하세요.";
        OnPropertyChanged(nameof(ItemListHeight));
    }

    public bool TryNavigateBackOneStep()
    {
        if (HasSelectedItem)
        {
            ClearSelectedItem();
            StatusMessage = "품목 목록으로 돌아왔습니다.";
            return true;
        }

        if (HasSelectedCategory)
        {
            ClearSelectedCategory();
            return true;
        }

        return false;
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

    public async Task SelectItemAsync(ItemDto item)
    {
        if (item is null)
            return;

        try
        {
            IsBusy = true;
            StatusMessage = $"{item.NameOriginal} 품목 정보를 불러오고 있습니다.";

            ItemDetailDto? detail = null;
            if (item.Id != Guid.Empty)
                detail = await _api.GetItemDetailAsync(item.Id);

            var selected = detail?.Item ?? item;
            selected.CategoryName = NormalizeCategoryName(selected.CategoryName);
            SelectedItem = selected;

            SelectedItemBranchStocks.Clear();
            var branchStocks = detail?.BranchStocks ?? new List<ItemWarehouseStockDto>();
            foreach (var stock in branchStocks)
                SelectedItemBranchStocks.Add(stock);

            if (SelectedItemBranchStocks.Count == 0 && selected.Id != Guid.Empty)
            {
                SelectedItemBranchStocks.Add(new ItemWarehouseStockDto
                {
                    ItemId = selected.Id,
                    WarehouseCode = "전체",
                    Quantity = selected.CurrentStock,
                    UpdatedAtUtc = DateTime.UtcNow
                });
            }

            OnPropertyChanged(nameof(SelectedItemBranchStocksHeight));
            StatusMessage = $"{selected.NameOriginal} 품목을 선택했습니다.";
        }
        catch (Exception ex)
        {
            SelectedItem = item;
            SelectedItemBranchStocks.Clear();
            OnPropertyChanged(nameof(SelectedItemBranchStocksHeight));
            StatusMessage = $"품목 상세 조회 실패: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
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

        if (SelectedItem is not null && Items.All(item => item.Id != SelectedItem.Id))
            ClearSelectedItem();

        StatusMessage = items.Count == 0
            ? "현재 분류에 표시할 품목이 없습니다."
            : $"{normalizedCategory} 분류 품목 {items.Count:N0}건";
        OnPropertyChanged(nameof(ItemListHeight));
        OnPropertyChanged(nameof(SelectedCategorySummary));
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
            .OrderBy(GetCategorySortKey)
            .ThenBy(category => category.Name, StringComparer.CurrentCultureIgnoreCase)
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

        SearchText = string.Empty;
        SelectedCategory = null;
        Items.Clear();
        ClearSelectedItem();
    }

    private void ClearSelectedItem()
    {
        SelectedItem = null;
        SelectedItemBranchStocks.Clear();
        OnPropertyChanged(nameof(SelectedItemBranchStocksHeight));
    }

    private ItemCategorySummaryDto? FindCategoryByName(string? categoryName)
        => ItemCategories.FirstOrDefault(category => CategoryEquals(category.Name, categoryName));

    private static double CalculateListHeight(int count, double rowHeight, double emptyHeight, int maxVisibleRows)
    {
        if (count <= 0)
            return emptyHeight;

        var rows = Math.Min(count, maxVisibleRows);
        return rowHeight * rows;
    }

    private static string NormalizeCategoryName(string? categoryName)
    {
        if (string.IsNullOrWhiteSpace(categoryName))
            return UncategorizedName;

        var normalized = categoryName.Trim();
        if (normalized.Length == 0)
            return UncategorizedName;

        if (normalized.All(ch => ch == '?' || ch == '�'))
            return UncategorizedName;

        return normalized;
    }

    private static string BuildCategoryQueryValue(string categoryName)
        => string.Equals(NormalizeCategoryName(categoryName), UncategorizedName, StringComparison.OrdinalIgnoreCase)
            ? UncategorizedName
            : NormalizeCategoryName(categoryName);

    private static bool CategoryEquals(string? left, string? right)
        => string.Equals(NormalizeCategoryName(left), NormalizeCategoryName(right), StringComparison.OrdinalIgnoreCase);

    private static string GetCategorySortKey(ItemCategorySummaryDto category)
    {
        var normalized = NormalizeCategoryName(category.Name);
        var preferredIndex = Array.FindIndex(
            PreferredCategoryOrder,
            item => string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase));

        return preferredIndex >= 0
            ? preferredIndex.ToString("D2")
            : $"90-{normalized}";
    }
}
