using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class InventoryViewModel : ObservableObject, IDisposable
{
    private readonly LocalStateService _local;
    private readonly SessionState _session;
    private readonly UiDebouncer _filterDebouncer = new();
    private readonly SemaphoreSlim _autoSaveGate = new(1, 1);
    private readonly Dictionary<Guid, Dictionary<string, decimal>> _itemOfficeQuantities = new();
    private readonly Dictionary<string, string> _warehouseOfficeCodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _warehouseDisplayNames = new(StringComparer.OrdinalIgnoreCase);
    private List<LocalItem> _allItems = new();
    private bool _isInventoryRefreshInProgress;
    private bool _suppressSelectionAutoSave;
    private int _suppressInventoryStateRefresh;
    private int _selectedItemMovementLoadVersion;
    private int _selectedItemVendorPriceLoadVersion;
    private string _baselineStateSignature = string.Empty;
    private long _editRevision;
    private string _editOfficeCode = DomainConstants.OfficeUsenet;
    private string _editTenantCode = TenantScopeCatalog.UsenetGroup;
    private bool _isDisposed;

    public ObservableCollection<InventoryItemRow> FilteredItems { get; } = new();
    public ObservableCollection<InventoryMovementRow> SelectedItemMovements { get; } = new();
    public ObservableCollection<ItemVendorPurchasePriceRow> SelectedItemVendorPurchasePrices { get; } = new();
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
    [ObservableProperty] private bool _keepEnteredValuesForNextNewItem;

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
    public bool CanDeleteSelectedItem =>
        SelectedItem is not null &&
        (_session.HasAdministrativePrivileges || _session.HasPermission(AppPermissionNames.ItemEdit)) &&
        _local.CanWriteItemScope(SelectedItem.Source, _session);
    private bool CanSaveItems => _session.HasAdministrativePrivileges || _session.HasPermission(AppPermissionNames.ItemEdit);
    public decimal BoxCurrentStock => EditBoxQty > 0 ? Math.Floor(EditSelectedOfficeStock / EditBoxQty) : 0;
    public decimal AssetValue => EditSelectedOfficeStock * EditPurchasePrice;
    public decimal ShortageStock => EditSelectedOfficeStock < EditSafetyStock ? EditSafetyStock - EditSelectedOfficeStock : 0;
    public bool IsInventoryTrackedItem => ItemOperationalPolicy.SupportsInventory(EditTrackingType);
    public bool HasPendingChanges => !string.Equals(_baselineStateSignature, BuildEditStateSignature(CaptureEditSnapshot()), StringComparison.Ordinal);
    public bool HasMeaningfulDraftContentForClose => HasMeaningfulDraftContent(CaptureEditSnapshot());
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
        ApplyDraftScopeForNewItem();
        _local.InventoryStateChanged += HandleInventoryStateChanged;
        ResetEditBaseline();
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        _local.InventoryStateChanged -= HandleInventoryStateChanged;
        _filterDebouncer.Dispose();
    }

    public async Task LoadAsync()
    {
        var repairResult = await _local.RepairMissingItemMastersFromOperationalReferencesAsync(_session);
        await RefreshInventoryScreenAsync(reloadCategories: true);
        if (repairResult.HasChanges)
        {
            var preview = repairResult.RepairedItemNames.Count > 0
                ? $" ({string.Join(", ", repairResult.RepairedItemNames.Take(4))}{(repairResult.RepairedItemNames.Count > 4 ? " 외" : string.Empty)})"
                : string.Empty;
            StatusMessage = $"전표/재고 참조만 남아 있던 품목 마스터 {repairResult.RepairedCount:N0}건을 복구했습니다{preview}. 동기화 시 중앙 서버에 반영됩니다.";
        }
    }

    public async Task LoadAndSelectItemAsync(Guid itemId)
    {
        await LoadAsync();

        var targetItem = _allItems.FirstOrDefault(item => item.Id == itemId);
        if (targetItem is not null)
        {
            SelectedTrackingTypeFilter = "전체";
            SearchText = targetItem.NameOriginal ?? string.Empty;
            ApplyFilter();
        }

        if (!FilteredItems.Any(row => row.Id == itemId))
        {
            SearchText = string.Empty;
            SelectedTrackingTypeFilter = "전체";
            ApplyFilter();
        }

        SelectItemWithoutAutoSave(itemId);
        StatusMessage = SelectedItem is null
            ? "문제 품목을 현재 계정 범위에서 찾지 못했습니다. 담당지점/권한 또는 동기화 상태를 확인하세요."
            : $"문제 품목 '{SelectedItem.NameOriginal}'을 열었습니다. 현재고와 창고별 재고/이동 내역을 확인하세요.";
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

        _allItems = await _local.GetItemsAsync(_session);
        await LoadInventoryStateAsync();
        ApplyFilter();

        if (selectedItemId.HasValue)
            SelectedItem = FilteredItems.FirstOrDefault(row => row.Id == selectedItemId.Value);

        if (selectedItemId.HasValue && SelectedItem is null)
            ResetForNewItem();

        if (SelectedItem is null && string.IsNullOrWhiteSpace(EditCategoryName))
            EditCategoryName = ItemCategoryOptions.FirstOrDefault()?.Name ?? string.Empty;
    }

    private void HandleInventoryStateChanged(object? sender, EventArgs e)
    {
        if (_isDisposed || _isInventoryRefreshInProgress || Volatile.Read(ref _suppressInventoryStateRefresh) > 0)
            return;

        UiTaskHelper.Forget(HandleInventoryStateChangedAsync(), "UI", "재고관리 화면 재고 상태 새로고침");
    }

    private async Task HandleInventoryStateChangedAsync()
    {
        if (_isDisposed || _isInventoryRefreshInProgress)
            return;

        _isInventoryRefreshInProgress = true;
        try
        {
            if (!_isDisposed)
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

    partial void OnSearchTextChanged(string value)
    {
        if (_isDisposed)
            return;

        _filterDebouncer.Debounce(TimeSpan.FromMilliseconds(300), ApplyFilter);
    }

    partial void OnSelectedTrackingTypeFilterChanged(string value)
    {
        if (_isDisposed)
            return;

        _filterDebouncer.Debounce(TimeSpan.FromMilliseconds(200), ApplyFilter);
    }

    partial void OnSelectedItemChanging(InventoryItemRow? oldValue, InventoryItemRow? newValue)
    {
        if (_suppressSelectionAutoSave || ReferenceEquals(oldValue, newValue))
            return;

        if (!TryCaptureAutoSaveSnapshot(out var snapshot))
            return;

        UiTaskHelper.Forget(
            HandleSelectionAutoSaveAsync(snapshot, oldValue, newValue),
            "INVENTORY",
            "품목 선택 변경 자동저장",
            ex => StatusMessage = $"품목 자동저장 중 오류가 발생했습니다. {ex.Message}");
    }

    partial void OnSelectedItemChanged(InventoryItemRow? value)
    {
        DeleteItemCommand.NotifyCanExecuteChanged();

        if (value is null)
        {
            ClearDetailForm();
            SelectedItemMovements.Clear();
            SelectedItemVendorPurchasePrices.Clear();
            return;
        }

        LoadFormFromItem(value);
        RequestLoadSelectedItemMovements(value.Id);
        RequestLoadSelectedItemVendorPurchasePrices(value.Id);
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

        if (IsNew && SelectedItem is null)
            ApplyDraftScopeForNewItem();

        if (!_isDisposed)
            _filterDebouncer.Debounce(TimeSpan.FromMilliseconds(150), ApplyFilter);
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
    private async Task NewItem()
    {
        var templateSnapshot = CaptureEditSnapshot();
        if (!await TryAutoSaveCurrentEditAsync(waitForServerWrite: false, refreshAfterSave: true))
            return;

        if (KeepEnteredValuesForNextNewItem && HasMeaningfulDraftContent(templateSnapshot))
        {
            PrepareRepeatedNewItemRegistration(
                templateSnapshot,
                "같은 상품명 계속입력: 현재 입력값을 유지한 채 새 품목 ID로 전환했습니다. 품명/규격을 조정한 뒤 저장하세요.");
            return;
        }

        ResetForNewItem("신규 품목 정보를 입력하세요. 입력칸을 비운 새 품목으로 저장됩니다.");
    }

    public void PrepareNewItemRegistration(string? initialItemName, string? statusMessage = null)
    {
        ResetForNewItem(statusMessage ?? "신규 품목 정보를 입력하세요.");
        if (!string.IsNullOrWhiteSpace(initialItemName))
            EditName = initialItemName.Trim();
    }

    [RelayCommand]
    private async Task SaveItemAsync()
    {
        var snapshot = CaptureEditSnapshot();
        if (!await SaveSnapshotAsync(
                snapshot,
                preserveSelectionItemId: snapshot.EditId,
                waitForServerWrite: true,
                refreshAfterSave: true,
                successMessage: "품목 정보를 저장했습니다. 재고 수량은 지점별 계산값으로 유지됩니다.",
                permissionDeniedMessage: "현재 계정은 품목을 저장할 권한이 없습니다. 관리자 계정으로 로그인하거나 관리자에게 저장을 요청하세요.",
                showConflictDialog: true,
                retryWithLatestRevisionOnConflict: false))
        {
            return;
        }

        if (KeepEnteredValuesForNextNewItem)
        {
            PrepareRepeatedNewItemRegistration(
                snapshot,
                "품목 정보를 저장했습니다. 같은 상품명 계속입력 상태라 현재 입력값을 유지하고 다음 신규 품목 입력을 준비했습니다.");
            return;
        }

        IsNew = false;
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelectedItem))]
    private async Task DeleteItemAsync()
    {
        if (SelectedItem is null)
            return;

        var deleteResult = await _local.DeleteItemAsync(SelectedItem.Id, _session, SelectedItem.Source.Revision);
        if (!deleteResult.Success)
        {
            StatusMessage = deleteResult.Message;
            if (deleteResult.ConcurrencyConflict)
            {
                System.Windows.MessageBox.Show(
                    deleteResult.Message,
                    "동시 수정 충돌",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
            }

            return;
        }

        await LoadAsync();
        ResetForNewItem();
        var serverWriteResult = await _local.WaitForServerWriteWithTimeoutAsync(TimeSpan.FromSeconds(3));
        StatusMessage = LocalStateService.ComposeServerWriteStatusMessage("품목을 삭제했습니다.", serverWriteResult);
    }

    public async Task<OfficeMutationResult> ResetSelectedInventoryValueAsync()
    {
        if (!_session.HasAdministrativePrivileges && !_session.HasPermission(AppPermissionNames.InventoryReset))
        {
            var deniedMessage = "현재 계정은 재고 초기화를 실행할 권한이 없습니다. 관리자 계정으로 로그인하거나 관리자에게 요청하세요.";
            StatusMessage = deniedMessage;
            return OfficeMutationResult.Denied(deniedMessage);
        }

        if (SelectedItem is null)
        {
            const string missingMessage = "재고를 초기화할 품목을 먼저 선택하세요.";
            StatusMessage = missingMessage;
            return OfficeMutationResult.Missing(missingMessage);
        }

        var selectedItemId = SelectedItem.Id;
        _isInventoryRefreshInProgress = true;
        try
        {
            var result = await _local.ResetItemInventoryValueAsync(selectedItemId, _session);
            if (!result.Success)
            {
                StatusMessage = result.Message;
                return result;
            }

            await RefreshInventoryScreenAsync(reloadCategories: false);
            SelectItemWithoutAutoSave(selectedItemId);

            if (SelectedItem is null)
                ResetForNewItem();

            ResetEditBaseline();
            StatusMessage = result.Message;
            return result;
        }
        finally
        {
            _isInventoryRefreshInProgress = false;
        }
    }

    public async Task<bool> TryAutoSaveOnCloseAsync()
        => await TryAutoSaveCurrentEditAsync(waitForServerWrite: false, refreshAfterSave: false);

    private async Task<bool> HandleSelectionAutoSaveAsync(
        InventoryEditSnapshot snapshot,
        InventoryItemRow? previousSelection,
        InventoryItemRow? requestedSelection)
    {
        var saved = await SaveSnapshotAsync(
            snapshot,
            preserveSelectionItemId: requestedSelection?.Id,
            waitForServerWrite: false,
            refreshAfterSave: true,
            successMessage: "품목 정보를 자동 저장했습니다.",
            permissionDeniedMessage: "현재 계정은 품목을 자동 저장할 권한이 없습니다.",
            showConflictDialog: false,
            retryWithLatestRevisionOnConflict: false);

        if (saved)
            return true;

        RestoreEditSnapshot(previousSelection, snapshot);
        StatusMessage = string.IsNullOrWhiteSpace(StatusMessage)
            ? "자동저장에 실패해 기존 편집 내용을 유지했습니다."
            : $"{StatusMessage} 기존 편집 내용은 유지했습니다.";
        return false;
    }

    private async Task<bool> TryAutoSaveCurrentEditAsync(bool waitForServerWrite, bool refreshAfterSave)
    {
        if (!TryCaptureAutoSaveSnapshot(out var snapshot))
            return true;

        return await SaveSnapshotAsync(
            snapshot,
            preserveSelectionItemId: SelectedItem?.Id,
            waitForServerWrite: waitForServerWrite,
            refreshAfterSave: refreshAfterSave,
            successMessage: "품목 정보를 자동 저장했습니다.",
            permissionDeniedMessage: "현재 계정은 품목을 자동 저장할 권한이 없습니다.",
            showConflictDialog: false,
            retryWithLatestRevisionOnConflict: false);
    }

    private bool TryCaptureAutoSaveSnapshot(out InventoryEditSnapshot snapshot)
    {
        snapshot = CaptureEditSnapshot();
        return CanSaveItems
               && HasPendingChanges
               && HasMeaningfulDraftContent(snapshot);
    }

    private async Task<bool> SaveSnapshotAsync(
        InventoryEditSnapshot snapshot,
        Guid? preserveSelectionItemId,
        bool waitForServerWrite,
        bool refreshAfterSave,
        string successMessage,
        string permissionDeniedMessage,
        bool showConflictDialog,
        bool retryWithLatestRevisionOnConflict)
    {
        await _autoSaveGate.WaitAsync();
        try
        {
            if (!CanSaveItems)
            {
                StatusMessage = permissionDeniedMessage;
                return false;
            }

            if (!await ValidateBeforeSaveAsync(snapshot))
                return false;

            try
            {
                await SaveItemSnapshotToLocalAsync(snapshot);
            }
            catch (InvalidOperationException ex) when (retryWithLatestRevisionOnConflict && IsItemRevisionConflict(ex.Message))
            {
                var latestItem = await _local.GetItemAsync(snapshot.EditId);
                if (latestItem is null)
                {
                    StatusMessage = "품목 최신값을 다시 확인하는 중 해당 품목을 찾을 수 없습니다. 새로고침 후 다시 시도하세요.";
                    return false;
                }

                var retrySnapshot = snapshot with { EditRevision = latestItem.Revision };
                try
                {
                    await SaveItemSnapshotToLocalAsync(retrySnapshot);
                    snapshot = retrySnapshot;
                    successMessage = "품목 최신값을 확인한 뒤 현재 수정 내용을 저장했습니다.";
                }
                catch (UnauthorizedAccessException retryUnauthorized)
                {
                    StatusMessage = retryUnauthorized.Message;
                    return false;
                }
                catch (InvalidOperationException retryConflict)
                {
                    StatusMessage = retryConflict.Message;
                    if (showConflictDialog)
                    {
                        System.Windows.MessageBox.Show(
                            retryConflict.Message,
                            "동시 수정 충돌",
                            System.Windows.MessageBoxButton.OK,
                            System.Windows.MessageBoxImage.Warning);
                    }

                    return false;
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                StatusMessage = ex.Message;
                return false;
            }
            catch (InvalidOperationException ex)
            {
                StatusMessage = ex.Message;
                if (showConflictDialog)
                {
                    System.Windows.MessageBox.Show(
                        ex.Message,
                        "동시 수정 충돌",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                }

                return false;
            }

            if (refreshAfterSave)
            {
                var selectionIdBeforeRefresh = preserveSelectionItemId ?? SelectedItem?.Id;
                _suppressSelectionAutoSave = true;
                try
                {
                    await RefreshInventoryScreenAsync(reloadCategories: false);
                    if (selectionIdBeforeRefresh.HasValue)
                        SelectedItem = FilteredItems.FirstOrDefault(row => row.Id == selectionIdBeforeRefresh.Value);
                }
                finally
                {
                    _suppressSelectionAutoSave = false;
                }
            }

            if (waitForServerWrite)
            {
                var serverWriteResult = await _local.WaitForServerWriteWithTimeoutAsync(TimeSpan.FromSeconds(3));
                StatusMessage = LocalStateService.ComposeServerWriteStatusMessage(successMessage, serverWriteResult);
            }
            else
            {
                StatusMessage = successMessage;
            }

            return true;
        }
        finally
        {
            _autoSaveGate.Release();
        }
    }

    private async Task SaveItemSnapshotToLocalAsync(InventoryEditSnapshot snapshot)
    {
        Interlocked.Increment(ref _suppressInventoryStateRefresh);
        try
        {
            await _local.UpsertItemAsync(BuildItem(snapshot), _session, snapshot.PreferredOfficeCode);
        }
        finally
        {
            Interlocked.Decrement(ref _suppressInventoryStateRefresh);
        }
    }

    private static bool IsItemRevisionConflict(string? message)
        => !string.IsNullOrWhiteSpace(message)
           && message.Contains("해당 품목", StringComparison.Ordinal)
           && message.Contains("최신값을 다시 불러온 뒤", StringComparison.Ordinal);

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
        _editRevision = item.Revision;
        _editOfficeCode = NormalizeOfficeCode(item.OfficeCode);
        _editTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            item.TenantCode,
            item.OfficeCode,
            _session.TenantCode,
            _session.OfficeCode);
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
        ResetEditBaseline();
    }

    private void ClearDetailForm()
    {
        IsNew = true;
        _editRevision = 0;
        ApplyDraftScopeForNewItem();
        EditId = Guid.NewGuid();
        EditName = string.Empty;
        EditCategoryName = string.Empty;
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
        ResetEditBaseline();
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

    private void RequestLoadSelectedItemVendorPurchasePrices(Guid itemId)
    {
        var version = Interlocked.Increment(ref _selectedItemVendorPriceLoadVersion);
        UiTaskHelper.Forget(
            LoadSelectedItemVendorPurchasePricesAsync(itemId, version),
            "INVENTORY",
            "선택 품목 매입처별 단가 조회",
            ex =>
            {
                if (IsCurrentSelectedItemVendorPriceLoad(version))
                    StatusMessage = $"매입처별 최근 구매단가를 불러오지 못했습니다. {ex.Message}";
            });
    }

    private async Task LoadSelectedItemVendorPurchasePricesAsync(Guid itemId, int version)
    {
        if (!IsCurrentSelectedItemVendorPriceLoad(version))
            return;

        SelectedItemVendorPurchasePrices.Clear();
        if (itemId == Guid.Empty)
            return;

        var rows = await _local.GetItemVendorPurchasePricesAsync(itemId, _session);
        if (!IsCurrentSelectedItemVendorPriceLoad(version))
            return;

        foreach (var row in rows)
            SelectedItemVendorPurchasePrices.Add(row);
    }

    private bool IsCurrentSelectedItemVendorPriceLoad(int version)
        => version == Volatile.Read(ref _selectedItemVendorPriceLoadVersion);

    private async Task<bool> ValidateBeforeSaveAsync(InventoryEditSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.EditName))
        {
            StatusMessage = "품명을 입력하세요.";
            return false;
        }

        if (snapshot.EditSafetyStock < 0 || snapshot.EditBoxQty < 0 ||
            snapshot.EditPurchasePrice < 0 || snapshot.EditSalePrice < 0 || snapshot.EditRetailPrice < 0 ||
            snapshot.EditPriceA < 0 || snapshot.EditPriceB < 0 || snapshot.EditPriceC < 0)
        {
            StatusMessage = "재고 기준값과 단가 값은 0 이상으로 입력하세요.";
            return false;
        }

        var normalizedName = RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(snapshot.EditName);
        var normalizedSpec = RentalCatalogValueNormalizer.NormalizeDisplayText(snapshot.EditSpec);
        var normalizedNameKey = RentalCatalogValueNormalizer.NormalizeLooseKey(normalizedName);
        var normalizedSpecKey = RentalCatalogValueNormalizer.NormalizeLooseKey(normalizedSpec);
        var normalizedTrackingType = ItemTrackingTypes.Normalize(snapshot.EditTrackingType);
        var allItems = await _local.GetItemsAsync(_session);
        var duplicated = normalizedTrackingType == ItemTrackingTypes.Asset
            ? false
            : allItems.Any(item =>
                item.Id != snapshot.EditId &&
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
    partial void OnEditSelectedOfficeStockChanged(decimal value)
    {
        OnPropertyChanged(nameof(BoxCurrentStock));
        OnPropertyChanged(nameof(AssetValue));
        OnPropertyChanged(nameof(ShortageStock));
    }
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

    private void ApplyDraftScopeForNewItem()
    {
        _editOfficeCode = NormalizeOfficeCode(SelectedOfficeCode);
        _editTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            _session.TenantCode,
            _editOfficeCode,
            _session.TenantCode,
            _session.OfficeCode);
    }

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

    private void ResetForNewItem(string? statusMessage = null)
    {
        _suppressSelectionAutoSave = true;
        try
        {
            if (SelectedItem is not null)
                SelectedItem = null;
            else
                ClearDetailForm();
        }
        finally
        {
            _suppressSelectionAutoSave = false;
        }

        SelectedItemMovements.Clear();
        SelectedItemVendorPurchasePrices.Clear();
        if (!string.IsNullOrWhiteSpace(statusMessage))
            StatusMessage = statusMessage;
    }

    private void PrepareRepeatedNewItemRegistration(InventoryEditSnapshot sourceSnapshot, string statusMessage)
    {
        var targetOfficeCode = NormalizeOfficeCode(SelectedOfficeCode);
        var targetTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            _session.TenantCode,
            targetOfficeCode,
            _session.TenantCode,
            _session.OfficeCode);
        var repeatedSnapshot = sourceSnapshot with
        {
            EditId = Guid.NewGuid(),
            EditRevision = 0,
            EditUsenetStock = 0m,
            EditItworldStock = 0m,
            EditYeonsuStock = 0m,
            EditSelectedOfficeStock = 0m,
            EditTotalStock = 0m,
            EditLastPurchaseDate = null,
            EditLastSaleDate = null,
            EditOfficeCode = targetOfficeCode,
            EditTenantCode = targetTenantCode,
            PreferredOfficeCode = SelectedOfficeCode,
            IsNew = true
        };

        _suppressSelectionAutoSave = true;
        try
        {
            SelectedItem = null;
        }
        finally
        {
            _suppressSelectionAutoSave = false;
        }

        SelectedItemMovements.Clear();
        SelectedItemVendorPurchasePrices.Clear();
        ApplySnapshot(repeatedSnapshot, resetBaseline: true);
        StatusMessage = statusMessage;
    }

    private void SelectItemWithoutAutoSave(Guid itemId)
    {
        _suppressSelectionAutoSave = true;
        try
        {
            SelectedItem = FilteredItems.FirstOrDefault(row => row.Id == itemId);
        }
        finally
        {
            _suppressSelectionAutoSave = false;
        }
    }

    private void RestoreEditSnapshot(InventoryItemRow? previousSelection, InventoryEditSnapshot snapshot)
    {
        _suppressSelectionAutoSave = true;
        try
        {
            SelectedItem = previousSelection;
        }
        finally
        {
            _suppressSelectionAutoSave = false;
        }

        ApplySnapshot(snapshot, resetBaseline: false);
        if (previousSelection is null)
        {
            SelectedItemMovements.Clear();
            SelectedItemVendorPurchasePrices.Clear();
        }
    }

    private void ApplySnapshot(InventoryEditSnapshot snapshot, bool resetBaseline)
    {
        IsNew = snapshot.IsNew;
        EditId = snapshot.EditId;
        _editRevision = snapshot.EditRevision;
        _editOfficeCode = NormalizeOfficeCode(snapshot.EditOfficeCode);
        _editTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            snapshot.EditTenantCode,
            snapshot.EditOfficeCode,
            _session.TenantCode,
            _session.OfficeCode);
        EditName = snapshot.EditName;
        EditCategoryName = snapshot.EditCategoryName;
        EditItemKind = snapshot.EditItemKind;
        EditTrackingType = snapshot.EditTrackingType;
        EditSpec = snapshot.EditSpec;
        EditUnit = snapshot.EditUnit;
        EditBoxQty = snapshot.EditBoxQty;
        EditStorageLocation = snapshot.EditStorageLocation;
        EditUsenetStock = snapshot.EditUsenetStock;
        EditItworldStock = snapshot.EditItworldStock;
        EditYeonsuStock = snapshot.EditYeonsuStock;
        EditSelectedOfficeStock = snapshot.EditSelectedOfficeStock;
        EditTotalStock = snapshot.EditTotalStock;
        EditSafetyStock = snapshot.EditSafetyStock;
        EditPurchasePrice = snapshot.EditPurchasePrice;
        EditSalePrice = snapshot.EditSalePrice;
        EditRetailPrice = snapshot.EditRetailPrice;
        EditPriceA = snapshot.EditPriceA;
        EditPriceB = snapshot.EditPriceB;
        EditPriceC = snapshot.EditPriceC;
        EditLastPurchaseDate = snapshot.EditLastPurchaseDate;
        EditLastSaleDate = snapshot.EditLastSaleDate;
        EditSimpleMemo = snapshot.EditSimpleMemo;
        EditIsSale = snapshot.EditIsSale;
        EditIsRental = snapshot.EditIsRental;

        OnPropertyChanged(nameof(IsInventoryTrackedItem));
        OnPropertyChanged(nameof(TrackingTypeGuideText));
        OnPropertyChanged(nameof(BoxCurrentStock));
        OnPropertyChanged(nameof(AssetValue));
        OnPropertyChanged(nameof(ShortageStock));

        if (resetBaseline)
            ResetEditBaseline();
    }

    private InventoryEditSnapshot CaptureEditSnapshot()
        => new(
            EditId,
            _editRevision,
            EditName,
            EditCategoryName,
            EditItemKind,
            EditTrackingType,
            EditSpec,
            EditUnit,
            EditBoxQty,
            EditStorageLocation,
            EditUsenetStock,
            EditItworldStock,
            EditYeonsuStock,
            EditSelectedOfficeStock,
            EditTotalStock,
            EditSafetyStock,
            EditPurchasePrice,
            EditSalePrice,
            EditRetailPrice,
            EditPriceA,
            EditPriceB,
            EditPriceC,
            EditLastPurchaseDate,
            EditLastSaleDate,
            EditSimpleMemo,
            EditIsSale,
            EditIsRental,
            _editOfficeCode,
            _editTenantCode,
            SelectedOfficeCode,
            IsNew);

    private static bool HasMeaningfulDraftContent(InventoryEditSnapshot snapshot)
        => !string.IsNullOrWhiteSpace(snapshot.EditName)
           || !string.IsNullOrWhiteSpace(snapshot.EditCategoryName)
           || !string.IsNullOrWhiteSpace(snapshot.EditSpec)
           || !string.IsNullOrWhiteSpace(snapshot.EditUnit)
           || !string.IsNullOrWhiteSpace(snapshot.EditStorageLocation)
           || !string.IsNullOrWhiteSpace(snapshot.EditSimpleMemo)
           || snapshot.EditBoxQty != 0m
           || snapshot.EditSafetyStock != 0m
           || snapshot.EditPurchasePrice != 0m
           || snapshot.EditSalePrice != 0m
           || snapshot.EditRetailPrice != 0m
           || snapshot.EditPriceA != 0m
           || snapshot.EditPriceB != 0m
           || snapshot.EditPriceC != 0m
           || snapshot.EditLastPurchaseDate.HasValue
           || snapshot.EditLastSaleDate.HasValue;

    private LocalItem BuildItem(InventoryEditSnapshot snapshot)
    {
        var normalizedName = RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(snapshot.EditName);
        var normalizedSpec = RentalCatalogValueNormalizer.NormalizeDisplayText(snapshot.EditSpec);
        var normalizedTrackingType = ItemTrackingTypes.Normalize(snapshot.EditTrackingType);

        return new LocalItem
        {
            Id = snapshot.EditId,
            Revision = snapshot.EditRevision,
            NameOriginal = normalizedName,
            NameMatchKey = RentalCatalogValueNormalizer.NormalizeLooseKey(normalizedName),
            CategoryName = SelectionOptionDefaults.NormalizeItemCategoryName(snapshot.EditCategoryName),
            ItemKind = ItemKinds.Normalize(snapshot.EditItemKind),
            TrackingType = normalizedTrackingType,
            SpecificationOriginal = normalizedSpec,
            SpecificationMatchKey = RentalCatalogValueNormalizer.NormalizeLooseKey(normalizedSpec),
            Unit = snapshot.EditUnit,
            BoxQuantity = snapshot.EditBoxQty,
            StorageLocation = snapshot.EditStorageLocation,
            CurrentStock = snapshot.EditTotalStock,
            SafetyStock = snapshot.EditSafetyStock,
            PurchasePrice = snapshot.EditPurchasePrice,
            SalePrice = snapshot.EditSalePrice,
            RetailPrice = snapshot.EditRetailPrice,
            PriceGradeA = snapshot.EditPriceA,
            PriceGradeB = snapshot.EditPriceB,
            PriceGradeC = snapshot.EditPriceC,
            LastPurchaseDate = snapshot.EditLastPurchaseDate,
            LastSaleDate = snapshot.EditLastSaleDate,
            SimpleMemo = snapshot.EditSimpleMemo,
            OfficeCode = snapshot.EditOfficeCode,
            TenantCode = snapshot.EditTenantCode,
            IsSale = !string.Equals(normalizedTrackingType, ItemTrackingTypes.Asset, StringComparison.Ordinal),
            IsRental = string.Equals(normalizedTrackingType, ItemTrackingTypes.Asset, StringComparison.Ordinal)
        };
    }

    private void ResetEditBaseline()
        => _baselineStateSignature = BuildEditStateSignature(CaptureEditSnapshot());

    private static string BuildEditStateSignature(InventoryEditSnapshot snapshot)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append(snapshot.EditId.ToString("D"))
            .Append('|').Append(snapshot.EditName ?? string.Empty)
            .Append('|').Append(snapshot.EditCategoryName ?? string.Empty)
            .Append('|').Append(snapshot.EditItemKind ?? string.Empty)
            .Append('|').Append(snapshot.EditTrackingType ?? string.Empty)
            .Append('|').Append(snapshot.EditSpec ?? string.Empty)
            .Append('|').Append(snapshot.EditUnit ?? string.Empty)
            .Append('|').Append(snapshot.EditBoxQty)
            .Append('|').Append(snapshot.EditStorageLocation ?? string.Empty)
            .Append('|').Append(snapshot.EditUsenetStock)
            .Append('|').Append(snapshot.EditItworldStock)
            .Append('|').Append(snapshot.EditYeonsuStock)
            .Append('|').Append(snapshot.EditSelectedOfficeStock)
            .Append('|').Append(snapshot.EditTotalStock)
            .Append('|').Append(snapshot.EditSafetyStock)
            .Append('|').Append(snapshot.EditPurchasePrice)
            .Append('|').Append(snapshot.EditSalePrice)
            .Append('|').Append(snapshot.EditRetailPrice)
            .Append('|').Append(snapshot.EditPriceA)
            .Append('|').Append(snapshot.EditPriceB)
            .Append('|').Append(snapshot.EditPriceC)
            .Append('|').Append(snapshot.EditLastPurchaseDate?.ToString("yyyy-MM-dd") ?? string.Empty)
            .Append('|').Append(snapshot.EditLastSaleDate?.ToString("yyyy-MM-dd") ?? string.Empty)
            .Append('|').Append(snapshot.EditSimpleMemo ?? string.Empty)
            .Append('|').Append(snapshot.EditIsSale)
            .Append('|').Append(snapshot.EditIsRental)
            .Append('|').Append(snapshot.IsNew);
        return builder.ToString();
    }

    private sealed record InventoryEditSnapshot(
        Guid EditId,
        long EditRevision,
        string EditName,
        string EditCategoryName,
        string EditItemKind,
        string EditTrackingType,
        string EditSpec,
        string EditUnit,
        decimal EditBoxQty,
        string EditStorageLocation,
        decimal EditUsenetStock,
        decimal EditItworldStock,
        decimal EditYeonsuStock,
        decimal EditSelectedOfficeStock,
        decimal EditTotalStock,
        decimal EditSafetyStock,
        decimal EditPurchasePrice,
        decimal EditSalePrice,
        decimal EditRetailPrice,
        decimal EditPriceA,
        decimal EditPriceB,
        decimal EditPriceC,
        DateOnly? EditLastPurchaseDate,
        DateOnly? EditLastSaleDate,
        string EditSimpleMemo,
        bool EditIsSale,
        bool EditIsRental,
        string EditOfficeCode,
        string EditTenantCode,
        string PreferredOfficeCode,
        bool IsNew);
}
