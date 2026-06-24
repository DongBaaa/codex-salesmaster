using System.Collections.ObjectModel;
using GeoraePlan.Mobile.App.Models;
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
    private readonly JsonSyncStateStore _syncStateStore;
    private readonly SyncCoordinator _syncCoordinator;

    private string _searchText = string.Empty;
    private string _statusMessage = "품목분류를 선택하세요.";
    private bool _isBusy;
    private DateTime? _lastRefreshUtc;
    private ItemCategorySummaryDto? _selectedCategory;
    private ItemDto? _selectedItem;
    private string _sessionTenantCode = string.Empty;
    private string _sessionUsername = string.Empty;

    public ItemsViewModel(
        GeoraePlanApiClient api,
        SessionStore sessionStore,
        JsonSyncStateStore syncStateStore,
        SyncCoordinator syncCoordinator)
    {
        _api = api;
        _sessionStore = sessionStore;
        _syncStateStore = syncStateStore;
        _syncCoordinator = syncCoordinator;
        RefreshCommand = new AsyncCommand(RefreshAsync);
    }

    public ObservableCollection<ItemCategorySummaryDto> ItemCategories { get; } = new();
    public ObservableCollection<ItemDto> Items { get; } = new();
    public ObservableCollection<ItemWarehouseStockDto> SelectedItemBranchStocks { get; } = new();

    public AsyncCommand RefreshCommand { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value))
                return;

            OnPropertyChanged(nameof(HasSearchText));
            OnPropertyChanged(nameof(IsCategoryChooserVisible));
            OnPropertyChanged(nameof(CanShowItemList));
            OnPropertyChanged(nameof(SelectedCategorySummary));
            OnPropertyChanged(nameof(ItemListLabelText));
        }
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
            OnPropertyChanged(nameof(CanShowItemList));
            OnPropertyChanged(nameof(ItemListLabelText));
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
            OnPropertyChanged(nameof(SelectedItemIdentitySummary));
            OnPropertyChanged(nameof(SelectedItemPriceSummary));
            OnPropertyChanged(nameof(SelectedItemStockSummary));
            OnPropertyChanged(nameof(SelectedItemMemo));
        }
    }

    public bool HasSelectedCategory => SelectedCategory is not null;
    public bool HasSearchText => !string.IsNullOrWhiteSpace(SearchText);
    public bool CanShowItemList => HasSelectedCategory || HasSearchText;
    public bool IsCategoryChooserVisible => !HasSelectedCategory && !HasSearchText;
    public bool HasSelectedItem => SelectedItem is not null;

    public string SelectedCategoryHeader => SelectedCategory is null
        ? "선택된 분류 없음"
        : $"선택된 분류: {NormalizeCategoryName(SelectedCategory.Name)}";

    public string SelectedCategorySummary
    {
        get
        {
            if (SelectedCategory is not null)
                return HasSearchText
                    ? $"현재 분류 검색결과 {Items.Count:N0}건"
                    : $"현재 분류 품목 {Items.Count:N0}건";

            return HasSearchText
                ? $"전체 품목 검색결과 {Items.Count:N0}건"
                : "품목분류를 선택하거나 품명/규격으로 검색하세요.";
        }
    }

    public string ItemListLabelText => SelectedCategory is null
        ? "품목 검색 결과"
        : "분류 품목 목록";

    public string SelectedItemTitle => SelectedItem?.NameOriginal ?? "품목 상세";

    public string SelectedItemSpecification => SelectedItem is null
        ? "규격 정보 없음"
        : string.IsNullOrWhiteSpace(SelectedItem.SpecificationOriginal)
            ? "규격 정보 없음"
            : $"규격: {SelectedItem.SpecificationOriginal}";

    public string SelectedItemIdentitySummary => BuildItemIdentitySummary(SelectedItem);

    public string SelectedItemPriceSummary => SelectedItem is null
        ? "기본 단가 정보 없음"
        : $"매입 {SelectedItem.PurchasePrice:N0}원 / 판매 {SelectedItem.SalePrice:N0}원 / 소매 {SelectedItem.RetailPrice:N0}원";

    public string SelectedItemStockSummary => SelectedItem is null
        ? "재고 정보 없음"
        : $"현재재고 {SelectedItem.CurrentStock:N0} / 안전재고 {SelectedItem.SafetyStock:N0}";

    public string SelectedItemMemo => SelectedItem is null
        ? "메모 없음"
        : string.IsNullOrWhiteSpace(SelectedItem.SimpleMemo)
            ? "메모 없음"
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
            await _syncCoordinator.RefreshIfServerChangedAsync("items-refresh", TimeSpan.FromSeconds(5));
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
                    : "품목분류를 선택하거나 품명/규격으로 검색하세요.";
            }
            else
            {
                var matchedCategory = FindCategoryByName(SelectedCategory.Name);
                if (matchedCategory is null)
                {
                    ClearSelectedCategory();
                    StatusMessage = "선택한 분류를 찾지 못했습니다. 분류를 다시 선택하세요.";
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
            if (MobileRetryableNetworkFailure.IsRetryable(ex) &&
                await TryLoadCategoriesFromSyncedStateAsync(preserveSelectedCategory, $"품목 화면 초기화 실패: {ex.Message}"))
            {
                return;
            }

            ClearAllItemDisplay();
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
        StatusMessage = "품목분류를 선택하거나 품명/규격으로 검색하세요.";
        OnPropertyChanged(nameof(ItemListHeight));
        OnPropertyChanged(nameof(SelectedCategorySummary));
    }

    private void ClearAllItemDisplay()
    {
        ItemCategories.Clear();
        SelectedCategory = null;
        SearchText = string.Empty;
        Items.Clear();
        ClearSelectedItem();
        OnPropertyChanged(nameof(ItemListHeight));
        OnPropertyChanged(nameof(SelectedCategorySummary));
        OnPropertyChanged(nameof(CanShowItemList));
        OnPropertyChanged(nameof(ItemListLabelText));
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

        if (SelectedCategory is null && !HasSearchText)
        {
            Items.Clear();
            ClearSelectedItem();
            StatusMessage = "검색어를 입력하거나 품목분류를 선택하세요.";
            OnPropertyChanged(nameof(ItemListHeight));
            OnPropertyChanged(nameof(SelectedCategorySummary));
            OnPropertyChanged(nameof(CanShowItemList));
            return;
        }

        try
        {
            IsBusy = true;
            await SearchItemsCoreAsync();
        }
        catch (Exception ex)
        {
            if (MobileRetryableNetworkFailure.IsRetryable(ex) &&
                await TrySearchItemsFromSyncedStateAsync($"품목 조회 실패: {ex.Message}"))
            {
                return;
            }

            Items.Clear();
            ClearSelectedItem();
            StatusMessage = $"품목 조회 실패: {ex.Message}";
            OnPropertyChanged(nameof(ItemListHeight));
            OnPropertyChanged(nameof(SelectedCategorySummary));
            OnPropertyChanged(nameof(CanShowItemList));
            OnPropertyChanged(nameof(ItemListLabelText));
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ClearSearchAsync()
    {
        if (IsBusy)
            return;

        SearchText = string.Empty;
        if (SelectedCategory is null)
        {
            Items.Clear();
            ClearSelectedItem();
            StatusMessage = "품목분류를 선택하거나 품명/규격으로 검색하세요.";
            OnPropertyChanged(nameof(ItemListHeight));
            OnPropertyChanged(nameof(SelectedCategorySummary));
            OnPropertyChanged(nameof(CanShowItemList));
            return;
        }

        await SearchItemsAsync();
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
            if (!MobileSessionScopeFilter.CanAccessItem(_sessionStore.GetSnapshot(), selected))
            {
                ClearSelectedItem();
                StatusMessage = $"{item.NameOriginal} 품목은 현재 로그인 담당지점/업체 범위 밖입니다.";
                return;
            }

            selected.CategoryName = NormalizeCategoryName(selected.CategoryName);
            PopulateSelectedItem(selected, FilterBranchStocksForCurrentScope(detail?.BranchStocks ?? []));
            StatusMessage = $"{selected.NameOriginal} 품목을 선택했습니다.";
        }
        catch (Exception ex)
        {
            if (!MobileRetryableNetworkFailure.IsRetryable(ex))
            {
                ClearSelectedItem();
                StatusMessage = $"{item.NameOriginal} 품목 상세를 사용할 수 없습니다. 삭제되었거나 현재 권한/담당지점 범위 밖일 수 있습니다. ({ex.Message})";
                return;
            }

            if (await TrySelectItemFromSyncedStateAsync(item, $"품목 상세 조회 실패: {ex.Message}"))
                return;

            PopulateSelectedItem(item, []);
            StatusMessage = $"품목 상세 조회 실패: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void RemoveDeletedItemFromCurrentView(Guid itemId)
    {
        if (itemId == Guid.Empty)
            return;

        var removed = Items.FirstOrDefault(item => item.Id == itemId);
        if (removed is not null)
            Items.Remove(removed);

        var selectedWasRemoved = SelectedItem?.Id == itemId;
        if (selectedWasRemoved)
            ClearSelectedItem();

        if (removed is null && !selectedWasRemoved)
            return;

        StatusMessage = removed is null
            ? "삭제 대기 품목을 현재 화면에서 숨겼습니다. 동기화 화면에서 서버 반영 상태를 확인하세요."
            : $"{removed.NameOriginal} 품목 삭제 대기 중입니다. 동기화 화면에서 서버 반영 상태를 확인하세요.";
        OnPropertyChanged(nameof(ItemListHeight));
        OnPropertyChanged(nameof(SelectedCategorySummary));
        OnPropertyChanged(nameof(CanShowItemList));
        OnPropertyChanged(nameof(ItemListLabelText));
    }

    private async Task SearchItemsCoreAsync()
    {
        if (SelectedCategory is null && !HasSearchText)
            return;

        var normalizedCategory = SelectedCategory is null
            ? string.Empty
            : NormalizeCategoryName(SelectedCategory.Name);
        var trimmedSearch = SearchText.Trim();
        var categoryQueryValue = string.IsNullOrWhiteSpace(normalizedCategory)
            ? string.Empty
            : BuildCategoryQueryValue(normalizedCategory);

        StatusMessage = SelectedCategory is null
            ? $"전체 품목에서 '{trimmedSearch}' 검색 중입니다."
            : $"{normalizedCategory} 분류 품목을 조회하고 있습니다.";
        var snapshot = _sessionStore.GetSnapshot();
        var items = (await _api.GetItemsAsync(trimmedSearch, categoryQueryValue))
            .Where(item => MobileSessionScopeFilter.CanAccessItem(snapshot, item))
            .ToList();

        Items.Clear();
        foreach (var item in items.OrderBy(item => item.NameOriginal))
        {
            item.CategoryName = NormalizeCategoryName(item.CategoryName);
            Items.Add(item);
        }

        if (SelectedItem is not null && Items.All(item => item.Id != SelectedItem.Id))
            ClearSelectedItem();

        StatusMessage = items.Count == 0
            ? SelectedCategory is null
                ? "검색 결과가 없습니다. 품명, 규격, 자재번호를 다시 확인하세요."
                : "현재 분류에 표시할 품목이 없습니다."
            : SelectedCategory is null
                ? $"전체 품목 검색결과 {items.Count:N0}건"
                : $"{normalizedCategory} 분류 품목 {items.Count:N0}건";
        OnPropertyChanged(nameof(ItemListHeight));
        OnPropertyChanged(nameof(SelectedCategorySummary));
        OnPropertyChanged(nameof(CanShowItemList));
        OnPropertyChanged(nameof(ItemListLabelText));
    }

    private async Task<bool> TryLoadCategoriesFromSyncedStateAsync(bool preserveSelectedCategory, string reason)
    {
        var state = await _syncStateStore.LoadAsync();
        state.Normalize();

        var syncedItems = GetActiveSyncedItems(state).ToList();
        if (syncedItems.Count == 0)
            return false;

        ReplaceCategories(BuildCategorySummaries(syncedItems));

        if (!preserveSelectedCategory || SelectedCategory is null)
        {
            if (HasSearchText)
                await TrySearchItemsFromSyncedStateAsync(reason);
            else
            {
                SelectedCategory = null;
                Items.Clear();
                ClearSelectedItem();
                StatusMessage = $"{reason} / 동기화 캐시 품목분류 {ItemCategories.Count:N0}개를 표시합니다.";
                OnPropertyChanged(nameof(ItemListHeight));
                OnPropertyChanged(nameof(SelectedCategorySummary));
                OnPropertyChanged(nameof(CanShowItemList));
                OnPropertyChanged(nameof(ItemListLabelText));
            }
        }
        else
        {
            var matchedCategory = FindCategoryByName(SelectedCategory.Name);
            if (matchedCategory is null)
            {
                ClearSelectedCategory();
                StatusMessage = $"{reason} / 동기화 캐시에 선택 분류가 없어 분류를 다시 선택하세요.";
            }
            else
            {
                SelectedCategory = matchedCategory;
                await TrySearchItemsFromSyncedStateAsync(reason);
            }
        }

        _lastRefreshUtc = DateTime.UtcNow;
        return true;
    }

    private async Task<bool> TrySearchItemsFromSyncedStateAsync(string reason)
    {
        var state = await _syncStateStore.LoadAsync();
        state.Normalize();

        var syncedItems = GetActiveSyncedItems(state).ToList();
        if (syncedItems.Count == 0)
            return false;

        var normalizedCategory = SelectedCategory is null
            ? string.Empty
            : NormalizeCategoryName(SelectedCategory.Name);
        var trimmedSearch = SearchText.Trim();

        var filtered = syncedItems
            .Where(item => SelectedCategory is null || CategoryEquals(item.CategoryName, normalizedCategory))
            .Where(item => string.IsNullOrWhiteSpace(trimmedSearch) || MatchesItem(item, trimmedSearch))
            .OrderBy(item => item.NameOriginal, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        Items.Clear();
        foreach (var item in filtered)
        {
            item.CategoryName = NormalizeCategoryName(item.CategoryName);
            Items.Add(item);
        }

        if (SelectedItem is not null && Items.All(item => item.Id != SelectedItem.Id))
            ClearSelectedItem();

        StatusMessage = filtered.Count == 0
            ? $"{reason} / 동기화 캐시 기준 검색 결과가 없습니다."
            : SelectedCategory is null
                ? $"{reason} / 동기화 캐시 전체 품목 검색결과 {filtered.Count:N0}건"
                : $"{reason} / 동기화 캐시 {normalizedCategory} 분류 품목 {filtered.Count:N0}건";
        OnPropertyChanged(nameof(ItemListHeight));
        OnPropertyChanged(nameof(SelectedCategorySummary));
        OnPropertyChanged(nameof(CanShowItemList));
        OnPropertyChanged(nameof(ItemListLabelText));
        return true;
    }

    private async Task<bool> TrySelectItemFromSyncedStateAsync(ItemDto item, string reason)
    {
        if (item.Id == Guid.Empty)
            return false;

        var state = await _syncStateStore.LoadAsync();
        state.Normalize();

        var selected = state.SyncedItems
            .Where(candidate => !candidate.IsDeleted)
            .Where(candidate => MobileSessionScopeFilter.CanAccessItem(_sessionStore.GetSnapshot(), candidate))
            .FirstOrDefault(candidate => candidate.Id == item.Id) ?? item;
        if (!MobileSessionScopeFilter.CanAccessItem(_sessionStore.GetSnapshot(), selected))
            return false;

        var branchStocks = state.SyncedItemWarehouseStocks
            .Where(stock => stock.ItemId == item.Id)
            .Where(stock => MobileSessionScopeFilter.CanAccessWarehouse(_sessionStore.GetSnapshot(), stock.WarehouseCode))
            .OrderBy(stock => stock.WarehouseCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (selected == item && branchStocks.Count == 0)
            return false;

        PopulateSelectedItem(selected, branchStocks);
        StatusMessage = $"{reason} / 동기화 캐시 기준으로 {selected.NameOriginal} 품목을 표시합니다.";
        return true;
    }

    private void PopulateSelectedItem(ItemDto selected, IEnumerable<ItemWarehouseStockDto> branchStocks)
    {
        selected.CategoryName = NormalizeCategoryName(selected.CategoryName);
        SelectedItem = selected;

        SelectedItemBranchStocks.Clear();
        foreach (var stock in branchStocks)
            SelectedItemBranchStocks.Add(stock);

        if (SelectedItemBranchStocks.Count == 0 && selected.Id != Guid.Empty)
        {
            SelectedItemBranchStocks.Add(new ItemWarehouseStockDto
            {
                ItemId = selected.Id,
                WarehouseCode = "전체",
                Quantity = selected.CurrentStock,
                UpdatedAtUtc = selected.UpdatedAtUtc == default ? DateTime.UtcNow : selected.UpdatedAtUtc
            });
        }

        OnPropertyChanged(nameof(SelectedItemBranchStocksHeight));
    }

    private IEnumerable<ItemDto> GetActiveSyncedItems(MobileSyncState state)
        => state.SyncedItems
            .Where(item => !item.IsDeleted)
            .Where(item => MobileSessionScopeFilter.CanAccessItem(_sessionStore.GetSnapshot(), item))
            .Select(item =>
            {
                item.CategoryName = NormalizeCategoryName(item.CategoryName);
                return item;
            });

    private IEnumerable<ItemWarehouseStockDto> FilterBranchStocksForCurrentScope(IEnumerable<ItemWarehouseStockDto> branchStocks)
    {
        var snapshot = _sessionStore.GetSnapshot();
        return branchStocks.Where(stock => MobileSessionScopeFilter.CanAccessWarehouse(snapshot, stock.WarehouseCode));
    }

    private static IReadOnlyList<ItemCategorySummaryDto> BuildCategorySummaries(IEnumerable<ItemDto> items)
        => items
            .GroupBy(item => NormalizeCategoryName(item.CategoryName), StringComparer.OrdinalIgnoreCase)
            .Select(group => new ItemCategorySummaryDto
            {
                Name = group.Key,
                ItemCount = group.Count()
            })
            .OrderBy(GetCategorySortKey)
            .ThenBy(category => category.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    private static bool MatchesItem(ItemDto item, string query)
        => Contains(item.NameOriginal, query)
           || Contains(item.NameMatchKey, query)
           || Contains(item.SpecificationOriginal, query)
           || Contains(item.SpecificationMatchKey, query)
           || Contains(item.MaterialNumber, query)
           || Contains(item.SerialNumber, query)
           || Contains(item.CategoryName, query);

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

    private static bool Contains(string? source, string query)
        => !string.IsNullOrWhiteSpace(source) &&
           source.Contains(query, StringComparison.CurrentCultureIgnoreCase);

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

        if (normalized.All(ch => ch == '?' || ch == '\uFFFD'))
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
