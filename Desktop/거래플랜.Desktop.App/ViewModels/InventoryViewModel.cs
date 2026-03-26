using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class InventoryViewModel : ObservableObject
{
    private readonly LocalStateService _local;
    private readonly SessionState _session;
    private readonly Dictionary<Guid, Dictionary<string, decimal>> _itemOfficeQuantities = new();
    private readonly Dictionary<string, string> _warehouseOfficeCodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _warehouseDisplayNames = new(StringComparer.OrdinalIgnoreCase);
    private List<LocalItem> _allItems = new();
    private bool _isInventoryRefreshInProgress;
    private int _selectedItemMovementLoadVersion;

    public ObservableCollection<InventoryItemRow> FilteredItems { get; } = new();
    public ObservableCollection<InventoryMovementRow> SelectedItemMovements { get; } = new();
    public ObservableCollection<LocalItemCategoryOption> ItemCategoryOptions { get; } = new();
    public IReadOnlyList<string> TrackingTypeFilterOptions { get; } = ["전체", ItemTrackingTypes.Stock, ItemTrackingTypes.Asset, ItemTrackingTypes.NonStock];
    public IReadOnlyList<string> ItemKindOptions { get; } = ItemKinds.All;
    public IReadOnlyList<string> TrackingTypeOptions { get; } = ItemTrackingTypes.All;

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _selectedTrackingTypeFilter = ItemTrackingTypes.Stock;
    [ObservableProperty] private InventoryItemRow? _selectedItem;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private string _selectedOfficeCode;
    [ObservableProperty] private decimal _usenetTotalQuantity;
    [ObservableProperty] private decimal _itworldTotalQuantity;
    [ObservableProperty] private decimal _yeonsuTotalQuantity;

    [ObservableProperty] private Guid _editId = Guid.NewGuid();
    [ObservableProperty] private string _editName = string.Empty;
    [ObservableProperty] private string _editCategoryName = string.Empty;
    [ObservableProperty] private string _editItemKind = ItemKinds.Product;
    [ObservableProperty] private string _editTrackingType = ItemTrackingTypes.Stock;
    [ObservableProperty] private string _editSpec = string.Empty;
    [ObservableProperty] private string _editUnit = string.Empty;
    [ObservableProperty] private decimal _editBoxQty;
    [ObservableProperty] private string _editStorageLocation = string.Empty;
    [ObservableProperty] private decimal _editUsenetStock;
    [ObservableProperty] private decimal _editItworldStock;
    [ObservableProperty] private decimal _editYeonsuStock;
    [ObservableProperty] private decimal _editSelectedOfficeStock;
    [ObservableProperty] private decimal _editTotalStock;
    [ObservableProperty] private decimal _editSafetyStock;
    [ObservableProperty] private decimal _editPurchasePrice;
    [ObservableProperty] private decimal _editSalePrice;
    [ObservableProperty] private decimal _editRetailPrice;
    [ObservableProperty] private decimal _editPriceA;
    [ObservableProperty] private decimal _editPriceB;
    [ObservableProperty] private decimal _editPriceC;
    [ObservableProperty] private DateOnly? _editLastPurchaseDate;
    [ObservableProperty] private DateOnly? _editLastSaleDate;
    [ObservableProperty] private string _editSimpleMemo = string.Empty;
    [ObservableProperty] private bool _editIsSale = true;
    [ObservableProperty] private bool _editIsRental;

    [ObservableProperty] private string _statusMessage = "품목은 재고/자산/비재고 청구항목으로 구분되며 재고 방식이 '재고'인 품목만 수량을 계산합니다.";
    [ObservableProperty] private bool _isNew = true;

    public bool IsAdmin => _session.HasAdministrativePrivileges;
    public bool CanSwitchOfficeTabs => _session.HasGlobalDataScope;
    public string SelectedOfficeDisplay => OfficeCodeCatalog.GetOfficeDisplayName(SelectedOfficeCode);
    public string SelectedOfficeStockLabel => $"{SelectedOfficeDisplay} 재고";
    public string InventoryScopeMessage => _session.HasGlobalDataScope
        ? $"{SelectedOfficeDisplay} 재고를 보는 중입니다."
        : $"{SelectedOfficeDisplay} 재고만 조회할 수 있습니다.";
    public string TransferGuideMessage => IsAdmin
        ? "상단 재고이동 버튼으로 지점간 이동을 입력하고 아래 이동 내역에서 출고/입고를 확인하세요."
        : "아래 이동 내역에서 자동이동 출고/입고를 확인하세요.";
    public string UsenetTabText => $"USENET 재고 ({UsenetTotalQuantity:N0})";
    public string ItworldTabText => $"ITWORLD 재고 ({ItworldTotalQuantity:N0})";
    public string YeonsuTabText => $"YEONSU 재고 ({YeonsuTotalQuantity:N0})";
    public decimal BoxCurrentStock => EditBoxQty > 0 ? Math.Floor(EditSelectedOfficeStock / EditBoxQty) : 0;
    public decimal AssetValue => EditSelectedOfficeStock * EditPurchasePrice;
    public decimal ShortageStock => EditSelectedOfficeStock < EditSafetyStock ? EditSafetyStock - EditSelectedOfficeStock : 0;
    public bool IsInventoryTrackedItem => ItemOperationalPolicy.SupportsInventory(EditTrackingType);
    public string TrackingTypeGuideText => EditTrackingType switch
    {
        ItemTrackingTypes.Asset => "자산: 렌탈 자산/설치현황에서 개별 장비로 관리하고 재고 수량은 반영하지 않습니다.",
        ItemTrackingTypes.NonStock => "비재고: 렌탈료·설치비·관리비 같은 청구용 항목으로 전표에만 반영하고 재고 수량은 차감하지 않습니다.",
        _ => "재고: 입출고/재고이동/원가 계산 대상 품목입니다."
    };
    public LocalStateService LocalStateService => _local;
    public SessionState SessionState => _session;

    public InventoryViewModel(LocalStateService local, SessionState session)
    {
        _local = local;
        _session = session;
        _selectedOfficeCode = ResolveDefaultOfficeCode(session);
        _local.InventoryStateChanged += HandleInventoryStateChanged;
    }

    public async Task LoadAsync()
    {
        await RefreshInventoryScreenAsync(reloadCategories: true);
    }

    public async Task ReloadItemCategoryOptionsAsync()
    {
        ItemCategoryOptions.Clear();
        foreach (var option in await _local.GetItemCategoryOptionsAsync())
            ItemCategoryOptions.Add(option);
    }

    private async Task RefreshInventoryScreenAsync(bool reloadCategories)
    {
        var selectedItemId = SelectedItem?.Id;

        if (reloadCategories)
            await ReloadItemCategoryOptionsAsync();

        _allItems = _session.HasGlobalDataScope
            ? await _local.GetItemsAsync()
            : await _local.GetItemsAsync(_session);
        await LoadInventoryStateAsync();
        ApplyFilter();

        if (selectedItemId.HasValue)
            SelectedItem = FilteredItems.FirstOrDefault(row => row.Id == selectedItemId.Value);

        if (selectedItemId.HasValue && SelectedItem is null)
            NewItem();

        if (SelectedItem is null && string.IsNullOrWhiteSpace(EditCategoryName))
            EditCategoryName = ItemCategoryOptions.FirstOrDefault()?.Name ?? string.Empty;
    }

    private void HandleInventoryStateChanged(object? sender, EventArgs e)
    {
        if (_isInventoryRefreshInProgress)
            return;

        UiTaskHelper.Forget(HandleInventoryStateChangedAsync(), "UI", "재고관리 화면 재고 상태 새로고침");
    }

    private async Task HandleInventoryStateChangedAsync()
    {
        if (_isInventoryRefreshInProgress)
            return;

        _isInventoryRefreshInProgress = true;
        try
        {
            await RefreshInventoryScreenAsync(reloadCategories: false);
        }
        finally
        {
            _isInventoryRefreshInProgress = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanShowUsenetOffice))]
    private void ShowUsenetOffice()
    {
        SelectedOfficeCode = DomainConstants.OfficeUsenet;
    }

    private bool CanShowUsenetOffice()
        => IsAdmin && !string.Equals(SelectedOfficeCode, DomainConstants.OfficeUsenet, StringComparison.OrdinalIgnoreCase);

    [RelayCommand(CanExecute = nameof(CanShowItworldOffice))]
    private void ShowItworldOffice()
    {
        SelectedOfficeCode = DomainConstants.OfficeItworld;
    }

    private bool CanShowItworldOffice()
        => IsAdmin && !string.Equals(SelectedOfficeCode, DomainConstants.OfficeItworld, StringComparison.OrdinalIgnoreCase);

    [RelayCommand(CanExecute = nameof(CanShowYeonsuOffice))]
    private void ShowYeonsuOffice()
    {
        SelectedOfficeCode = DomainConstants.OfficeYeonsu;
    }

    private bool CanShowYeonsuOffice()
        => IsAdmin && !string.Equals(SelectedOfficeCode, DomainConstants.OfficeYeonsu, StringComparison.OrdinalIgnoreCase);

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnSelectedTrackingTypeFilterChanged(string value) => ApplyFilter();

    partial void OnSelectedItemChanged(InventoryItemRow? value)
    {
        if (value is null)
        {
            ClearDetailForm();
            SelectedItemMovements.Clear();
            return;
        }

        LoadFormFromItem(value);
        RequestLoadSelectedItemMovements(value.Id);
    }

    partial void OnSelectedOfficeCodeChanged(string value)
    {
        OnPropertyChanged(nameof(SelectedOfficeDisplay));
        OnPropertyChanged(nameof(SelectedOfficeStockLabel));
        OnPropertyChanged(nameof(InventoryScopeMessage));
        OnPropertyChanged(nameof(BoxCurrentStock));
        OnPropertyChanged(nameof(AssetValue));
        OnPropertyChanged(nameof(ShortageStock));
        ShowUsenetOfficeCommand.NotifyCanExecuteChanged();
        ShowItworldOfficeCommand.NotifyCanExecuteChanged();
        ShowYeonsuOfficeCommand.NotifyCanExecuteChanged();
        ApplyFilter();
    }

    partial void OnEditItemKindChanged(string value)
    {
        var normalizedItemKind = ItemKinds.Normalize(value);
        if (!string.Equals(value, normalizedItemKind, StringComparison.Ordinal))
        {
            EditItemKind = normalizedItemKind;
            return;
        }

        var desiredTrackingType = normalizedItemKind switch
        {
            ItemKinds.Asset => ItemTrackingTypes.Asset,
            ItemKinds.Billing => ItemTrackingTypes.NonStock,
            _ => ItemTrackingTypes.Stock
        };

        if (!string.Equals(EditTrackingType, desiredTrackingType, StringComparison.Ordinal))
            EditTrackingType = desiredTrackingType;
    }

    partial void OnEditTrackingTypeChanged(string value)
    {
        var normalizedTrackingType = ItemTrackingTypes.Normalize(value);
        if (!string.Equals(value, normalizedTrackingType, StringComparison.Ordinal))
        {
            EditTrackingType = normalizedTrackingType;
            return;
        }

        var desiredItemKind = normalizedTrackingType switch
        {
            ItemTrackingTypes.Asset => ItemKinds.Asset,
            ItemTrackingTypes.NonStock => ItemKinds.Billing,
            _ when string.Equals(EditItemKind, ItemKinds.Consumable, StringComparison.Ordinal) => ItemKinds.Consumable,
            _ => ItemKinds.Product
        };

        if (!string.Equals(EditItemKind, desiredItemKind, StringComparison.Ordinal))
            EditItemKind = desiredItemKind;

        if (!ItemOperationalPolicy.SupportsInventory(normalizedTrackingType))
        {
            EditSafetyStock = 0m;
            EditUsenetStock = 0m;
            EditItworldStock = 0m;
            EditYeonsuStock = 0m;
            EditSelectedOfficeStock = 0m;
            EditTotalStock = 0m;
        }

        OnPropertyChanged(nameof(IsInventoryTrackedItem));
        OnPropertyChanged(nameof(TrackingTypeGuideText));
        OnPropertyChanged(nameof(BoxCurrentStock));
        OnPropertyChanged(nameof(AssetValue));
        OnPropertyChanged(nameof(ShortageStock));
    }

    partial void OnUsenetTotalQuantityChanged(decimal value) => OnPropertyChanged(nameof(UsenetTabText));
    partial void OnItworldTotalQuantityChanged(decimal value) => OnPropertyChanged(nameof(ItworldTabText));
    partial void OnYeonsuTotalQuantityChanged(decimal value) => OnPropertyChanged(nameof(YeonsuTabText));

    [RelayCommand]
    private void NewItem()
    {
        IsNew = true;
        SelectedItem = null;
        ClearDetailForm();
        StatusMessage = "신규 품목 정보를 입력하세요.";
    }

    [RelayCommand]
    private async Task SaveItemAsync()
    {
        if (!_session.HasAdministrativePrivileges)
        {
            StatusMessage = "관리자 또는 god 권한 계정만 품목을 저장할 수 있습니다.";
            return;
        }

        if (!await ValidateBeforeSaveAsync())
            return;

        var normalizedName = RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(EditName);
        var normalizedSpec = RentalCatalogValueNormalizer.NormalizeDisplayText(EditSpec);

        var item = new LocalItem
        {
            Id = EditId,
            NameOriginal = normalizedName,
            NameMatchKey = RentalCatalogValueNormalizer.NormalizeLooseKey(normalizedName),
            CategoryName = SelectionOptionDefaults.NormalizeItemCategoryName(EditCategoryName),
            ItemKind = ItemKinds.Normalize(EditItemKind),
            TrackingType = ItemTrackingTypes.Normalize(EditTrackingType),
            SpecificationOriginal = normalizedSpec,
            SpecificationMatchKey = RentalCatalogValueNormalizer.NormalizeLooseKey(normalizedSpec),
            Unit = EditUnit,
            BoxQuantity = EditBoxQty,
            StorageLocation = EditStorageLocation,
            CurrentStock = EditTotalStock,
            SafetyStock = EditSafetyStock,
            PurchasePrice = EditPurchasePrice,
            SalePrice = EditSalePrice,
            RetailPrice = EditRetailPrice,
            PriceGradeA = EditPriceA,
            PriceGradeB = EditPriceB,
            PriceGradeC = EditPriceC,
            LastPurchaseDate = EditLastPurchaseDate,
            LastSaleDate = EditLastSaleDate,
            SimpleMemo = EditSimpleMemo,
            IsSale = !string.Equals(ItemTrackingTypes.Normalize(EditTrackingType), ItemTrackingTypes.Asset, StringComparison.Ordinal),
            IsRental = string.Equals(ItemTrackingTypes.Normalize(EditTrackingType), ItemTrackingTypes.Asset, StringComparison.Ordinal),
        };

        await _local.UpsertItemAsync(item, SelectedOfficeCode);
        await LoadAsync();
        var serverWriteResult = await _local.WaitForServerWriteWithTimeoutAsync(TimeSpan.FromSeconds(3));

        SelectedItem = FilteredItems.FirstOrDefault(row => row.Id == EditId);
        StatusMessage = LocalStateService.ComposeServerWriteStatusMessage(
            "품목 정보를 저장했습니다. 재고 수량은 지점별 계산값으로 유지됩니다.",
            serverWriteResult);
        IsNew = false;
    }

    [RelayCommand]
    private async Task DeleteItemAsync()
    {
        if (!_session.HasAdministrativePrivileges)
        {
            StatusMessage = "관리자 또는 god 권한 계정만 품목을 삭제할 수 있습니다.";
            return;
        }

        if (SelectedItem is null)
            return;

        await _local.DeleteItemAsync(SelectedItem.Id);
        await LoadAsync();
        NewItem();
        var serverWriteResult = await _local.WaitForServerWriteWithTimeoutAsync(TimeSpan.FromSeconds(3));
        StatusMessage = LocalStateService.ComposeServerWriteStatusMessage("품목을 삭제했습니다.", serverWriteResult);
    }

    private async Task LoadInventoryStateAsync()
    {
        var warehouses = await _local.GetWarehousesAsync();
        var stocks = await _local.GetItemWarehouseStocksAsync();

        _warehouseOfficeCodes.Clear();
        _warehouseDisplayNames.Clear();
        foreach (var warehouse in warehouses)
        {
            var warehouseCode = (warehouse.Code ?? string.Empty).Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(warehouseCode))
                continue;

            _warehouseOfficeCodes[warehouseCode] = NormalizeOfficeCode(warehouse.OfficeCode);
            _warehouseDisplayNames[warehouseCode] = string.IsNullOrWhiteSpace(warehouse.Name)
                ? warehouseCode
                : warehouse.Name.Trim();
        }

        _itemOfficeQuantities.Clear();
        UsenetTotalQuantity = 0m;
        ItworldTotalQuantity = 0m;
        YeonsuTotalQuantity = 0m;

        if (stocks.Count == 0)
        {
            foreach (var item in _allItems.Where(item => item.CurrentStock != 0m))
            {
                _itemOfficeQuantities[item.Id] = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
                {
                    [DomainConstants.OfficeUsenet] = item.CurrentStock
                };
                UsenetTotalQuantity += item.CurrentStock;
            }

            if (_allItems.Any(item => item.CurrentStock != 0m))
            {
                StatusMessage = "창고별 재고 스냅샷이 없어 기존 전체 재고를 USENET 기준으로 표시합니다.";
            }

            return;
        }

        foreach (var stock in stocks)
        {
            if (!_itemOfficeQuantities.TryGetValue(stock.ItemId, out var officeQuantities))
            {
                officeQuantities = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
                _itemOfficeQuantities[stock.ItemId] = officeQuantities;
            }

            var officeCode = ResolveOfficeCodeFromWarehouse(stock.WarehouseCode);
            officeQuantities[officeCode] = officeQuantities.TryGetValue(officeCode, out var currentQuantity)
                ? currentQuantity + stock.Quantity
                : stock.Quantity;

                if (string.Equals(officeCode, DomainConstants.OfficeItworld, StringComparison.OrdinalIgnoreCase))
                    ItworldTotalQuantity += stock.Quantity;
                else if (string.Equals(officeCode, DomainConstants.OfficeYeonsu, StringComparison.OrdinalIgnoreCase))
                    YeonsuTotalQuantity += stock.Quantity;
                else
                    UsenetTotalQuantity += stock.Quantity;
        }
    }

    private void ApplyFilter()
    {
        var selectedItemId = SelectedItem?.Id;
        var keyword = (SearchText ?? string.Empty).Trim();
        var trackingFilter = (SelectedTrackingTypeFilter ?? string.Empty).Trim();

        IEnumerable<LocalItem> filtered = _allItems;
        if (!string.IsNullOrWhiteSpace(trackingFilter) &&
            !string.Equals(trackingFilter, "전체", StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(item =>
                string.Equals(
                    ItemTrackingTypes.Normalize(item.TrackingType),
                    ItemTrackingTypes.Normalize(trackingFilter),
                    StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            filtered = filtered.Where(item =>
                item.NameOriginal.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                item.CategoryName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                item.SpecificationOriginal.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                ItemOperationalPolicy.NormalizeItemKind(item.ItemKind, item.TrackingType, item.CategoryName, item.IsRental)
                    .Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                ItemOperationalPolicy.NormalizeTrackingType(item.TrackingType, item.ItemKind, item.CategoryName, item.IsRental)
                    .Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        var rows = filtered
            .OrderBy(item => ItemOperationalPolicy.NormalizeTrackingType(item.TrackingType, item.ItemKind, item.CategoryName, item.IsRental), StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.NameOriginal, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.SpecificationOriginal, StringComparer.CurrentCultureIgnoreCase)
            .Select(BuildRow)
            .ToList();

        FilteredItems.Clear();
        foreach (var row in rows)
            FilteredItems.Add(row);

        TotalCount = FilteredItems.Count;
        if (selectedItemId.HasValue)
            SelectedItem = FilteredItems.FirstOrDefault(row => row.Id == selectedItemId.Value);

        SelectedItem ??= FilteredItems.FirstOrDefault();
    }

    private InventoryItemRow BuildRow(LocalItem item)
    {
        var officeQuantities = _itemOfficeQuantities.TryGetValue(item.Id, out var quantities)
            ? quantities
            : new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        return new InventoryItemRow(
            item,
            officeQuantities,
            SelectedOfficeCode);
    }

    private void LoadFormFromItem(InventoryItemRow row)
    {
        var item = row.Source;

        IsNew = false;
        EditId = item.Id;
        EditName = item.NameOriginal;
        EditCategoryName = item.CategoryName;
        EditItemKind = ItemOperationalPolicy.NormalizeItemKind(item.ItemKind, item.TrackingType, item.CategoryName, item.IsRental);
        EditTrackingType = ItemOperationalPolicy.NormalizeTrackingType(item.TrackingType, item.ItemKind, item.CategoryName, item.IsRental);
        EditSpec = item.SpecificationOriginal;
        EditUnit = item.Unit;
        EditBoxQty = item.BoxQuantity;
        EditStorageLocation = item.StorageLocation;
        EditUsenetStock = row.UsenetQuantity;
        EditItworldStock = row.ItworldQuantity;
        EditYeonsuStock = row.YeonsuQuantity;
        EditSelectedOfficeStock = row.GetOfficeQuantity(SelectedOfficeCode);
        EditTotalStock = row.TotalQuantity;
        EditSafetyStock = item.SafetyStock;
        EditPurchasePrice = item.PurchasePrice;
        EditSalePrice = item.SalePrice;
        EditRetailPrice = item.RetailPrice;
        EditPriceA = item.PriceGradeA;
        EditPriceB = item.PriceGradeB;
        EditPriceC = item.PriceGradeC;
        EditLastPurchaseDate = item.LastPurchaseDate;
        EditLastSaleDate = item.LastSaleDate;
        EditSimpleMemo = item.SimpleMemo;
        EditIsSale = item.IsSale;
        EditIsRental = item.IsRental;

        OnPropertyChanged(nameof(BoxCurrentStock));
        OnPropertyChanged(nameof(AssetValue));
        OnPropertyChanged(nameof(ShortageStock));
    }

    private void ClearDetailForm()
    {
        IsNew = true;
        EditId = Guid.NewGuid();
        EditName = string.Empty;
        EditCategoryName = ItemCategoryOptions.FirstOrDefault()?.Name ?? string.Empty;
        EditItemKind = ItemKinds.Product;
        EditTrackingType = ItemTrackingTypes.Stock;
        EditSpec = string.Empty;
        EditUnit = string.Empty;
        EditBoxQty = 0m;
        EditStorageLocation = string.Empty;
        EditUsenetStock = 0m;
        EditItworldStock = 0m;
        EditYeonsuStock = 0m;
        EditSelectedOfficeStock = 0m;
        EditTotalStock = 0m;
        EditSafetyStock = 0m;
        EditPurchasePrice = 0m;
        EditSalePrice = 0m;
        EditRetailPrice = 0m;
        EditPriceA = 0m;
        EditPriceB = 0m;
        EditPriceC = 0m;
        EditLastPurchaseDate = null;
        EditLastSaleDate = null;
        EditSimpleMemo = string.Empty;
        EditIsSale = true;
        EditIsRental = false;

        OnPropertyChanged(nameof(BoxCurrentStock));
        OnPropertyChanged(nameof(AssetValue));
        OnPropertyChanged(nameof(ShortageStock));
    }

    private void RequestLoadSelectedItemMovements(Guid itemId)
    {
        var version = Interlocked.Increment(ref _selectedItemMovementLoadVersion);
        UiTaskHelper.Forget(
            LoadSelectedItemMovementsAsync(itemId, version),
            "INVENTORY",
            "선택 품목 이동내역 조회",
            ex =>
            {
                if (IsCurrentSelectedItemMovementLoad(version))
                    StatusMessage = $"재고 이동내역을 불러오지 못했습니다. {ex.Message}";
            });
    }

    private async Task LoadSelectedItemMovementsAsync(Guid itemId, int version)
    {
        if (!IsCurrentSelectedItemMovementLoad(version))
            return;

        SelectedItemMovements.Clear();
        if (itemId == Guid.Empty)
            return;

        var movements = await _local.GetInventoryMovementsAsync(itemId);
        if (!IsCurrentSelectedItemMovementLoad(version))
            return;

        IEnumerable<LocalInventoryMovement> filtered = movements;

        if (!IsAdmin)
        {
            filtered = filtered.Where(movement =>
                string.Equals(
                    ResolveOfficeCodeFromWarehouse(movement.WarehouseCode),
                    SelectedOfficeCode,
                    StringComparison.OrdinalIgnoreCase));
        }

        foreach (var movement in filtered)
        {
            SelectedItemMovements.Add(new InventoryMovementRow
            {
                OccurredDate = movement.OccurredDate,
                OfficeDisplay = ToOfficeDisplay(ResolveOfficeCodeFromWarehouse(movement.WarehouseCode)),
                WarehouseDisplay = ResolveWarehouseDisplayName(movement.WarehouseCode),
                MovementTypeDisplay = ToMovementTypeDisplay(movement.MovementType),
                QuantityDelta = movement.QuantityDelta,
                Note = movement.Note ?? string.Empty,
                CreatedByUsername = movement.CreatedByUsername ?? string.Empty
            });
        }
    }

    private bool IsCurrentSelectedItemMovementLoad(int version)
        => version == Volatile.Read(ref _selectedItemMovementLoadVersion);

    private async Task<bool> ValidateBeforeSaveAsync()
    {
        if (string.IsNullOrWhiteSpace(EditName))
        {
            StatusMessage = "품명을 입력하세요.";
            return false;
        }

        if (EditSafetyStock < 0 || EditBoxQty < 0 ||
            EditPurchasePrice < 0 || EditSalePrice < 0 || EditRetailPrice < 0 ||
            EditPriceA < 0 || EditPriceB < 0 || EditPriceC < 0)
        {
            StatusMessage = "재고 기준값과 단가 값은 0 이상으로 입력하세요.";
            return false;
        }

        var normalizedName = RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(EditName);
        var normalizedSpec = RentalCatalogValueNormalizer.NormalizeDisplayText(EditSpec);
        var normalizedNameKey = RentalCatalogValueNormalizer.NormalizeLooseKey(normalizedName);
        var normalizedSpecKey = RentalCatalogValueNormalizer.NormalizeLooseKey(normalizedSpec);
        var normalizedTrackingType = ItemTrackingTypes.Normalize(EditTrackingType);
        var allItems = await _local.GetItemsAsync();
        var duplicated = normalizedTrackingType == ItemTrackingTypes.Asset
            ? false
            : allItems.Any(item =>
                item.Id != EditId &&
                string.Equals(
                    ItemTrackingTypes.Normalize(item.TrackingType),
                    normalizedTrackingType,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(RentalCatalogValueNormalizer.NormalizeLooseKey(item.NameOriginal), normalizedNameKey, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(RentalCatalogValueNormalizer.NormalizeLooseKey(item.SpecificationOriginal), normalizedSpecKey, StringComparison.OrdinalIgnoreCase));

        if (duplicated)
        {
            StatusMessage = "동일한 품명/규격/재고방식 조합이 이미 존재합니다.";
            return false;
        }

        return true;
    }

    partial void OnEditBoxQtyChanged(decimal value) => OnPropertyChanged(nameof(BoxCurrentStock));
    partial void OnEditPurchasePriceChanged(decimal value) => OnPropertyChanged(nameof(AssetValue));
    partial void OnEditSelectedOfficeStockChanged(decimal value) => OnPropertyChanged(nameof(BoxCurrentStock));
    partial void OnEditTotalStockChanged(decimal value)
    {
        OnPropertyChanged(nameof(AssetValue));
        OnPropertyChanged(nameof(ShortageStock));
    }

    partial void OnEditSafetyStockChanged(decimal value) => OnPropertyChanged(nameof(ShortageStock));

    private decimal GetOfficeQuantity(Guid itemId, string officeCode)
        => _itemOfficeQuantities.TryGetValue(itemId, out var officeQuantities) &&
           officeQuantities.TryGetValue(officeCode, out var quantity)
            ? quantity
            : 0m;

    private string ResolveOfficeCodeFromWarehouse(string? warehouseCode)
    {
        var normalizedWarehouseCode = OfficeCodeCatalog.NormalizeWarehouseCodeLoose(warehouseCode);
        if (_warehouseOfficeCodes.TryGetValue(normalizedWarehouseCode, out var officeCode))
            return OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode, DomainConstants.OfficeUsenet);

        return normalizedWarehouseCode switch
        {
            var code when string.Equals(code, DomainConstants.WarehouseItworldMain, StringComparison.OrdinalIgnoreCase) => DomainConstants.OfficeItworld,
            var code when string.Equals(code, DomainConstants.WarehouseYeonsuMain, StringComparison.OrdinalIgnoreCase) => DomainConstants.OfficeYeonsu,
            _ => DomainConstants.OfficeUsenet
        };
    }

    private string ResolveWarehouseDisplayName(string? warehouseCode)
    {
        var normalizedWarehouseCode = (warehouseCode ?? string.Empty).Trim().ToUpperInvariant();
        if (_warehouseDisplayNames.TryGetValue(normalizedWarehouseCode, out var displayName))
            return displayName;

        if (string.Equals(normalizedWarehouseCode, DomainConstants.WarehouseItworldMain, StringComparison.OrdinalIgnoreCase))
            return "ITWORLD 창고";

        if (string.Equals(normalizedWarehouseCode, DomainConstants.WarehouseYeonsuMain, StringComparison.OrdinalIgnoreCase))
            return "YEONSU 창고";

        if (string.Equals(normalizedWarehouseCode, DomainConstants.WarehouseUsenetMain, StringComparison.OrdinalIgnoreCase))
            return "USENET 창고";

        return string.IsNullOrWhiteSpace(normalizedWarehouseCode) ? "-" : normalizedWarehouseCode;
    }

    private static string ResolveDefaultOfficeCode(SessionState session)
        => OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(session.OfficeCode, DomainConstants.OfficeUsenet);

    private static string NormalizeOfficeCode(string? officeCode)
        => OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode, DomainConstants.OfficeUsenet);

    private static string ToOfficeDisplay(string officeCode)
        => OfficeCodeCatalog.GetOfficeDisplayName(officeCode);

    private static string ToMovementTypeDisplay(string? movementType)
    {
        return (movementType ?? string.Empty).Trim() switch
        {
            "PurchaseIn" => "입고",
            "SalesOut" => "출고",
            "TransferOutAuto" => "자동이동 출고",
            "TransferInAuto" => "자동이동 입고",
            "TransferOutManual" => "재고이동 출고",
            "TransferInManual" => "재고이동 입고",
            _ => string.IsNullOrWhiteSpace(movementType) ? "-" : movementType
        };
    }
}
