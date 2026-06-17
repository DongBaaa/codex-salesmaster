using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class InventoryTransferViewModel : ObservableObject
{
    private readonly LocalStateService _local;
    private readonly SessionState _session;
    private readonly SemaphoreSlim _autoSaveGate = new(1, 1);
    private readonly Dictionary<(Guid ItemId, string WarehouseCode), decimal> _warehouseStocks = new();
    private readonly Dictionary<string, string> _warehouseNames = new(StringComparer.OrdinalIgnoreCase);
    private List<LocalItem> _allItems = new();
    private bool _suppressTransferSelectionChanged;
    private bool _suppressLineSelectionChanged;
    private bool _isInventoryRefreshInProgress;
    private int _openTransferVersion;
    private string _baselineStateSignature = string.Empty;
    private long _transferRevision;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSavedTransfer))]
    [NotifyPropertyChangedFor(nameof(CanDeleteTransfer))]
    [NotifyPropertyChangedFor(nameof(CanConfirmReceipt))]
    [NotifyPropertyChangedFor(nameof(CanRejectTransfer))]
    private Guid _transferId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TransferNumberDisplay))]
    private string _transferNumber = string.Empty;

    [ObservableProperty] private DateOnly _transferDate = DateOnly.FromDateTime(DateTime.Today);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TransferRouteText))]
    [NotifyPropertyChangedFor(nameof(CanDeleteTransfer))]
    private string _fromWarehouseCode = DomainConstants.WarehouseUsenetMain;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TransferRouteText))]
    [NotifyPropertyChangedFor(nameof(CanDeleteTransfer))]
    [NotifyPropertyChangedFor(nameof(CanConfirmReceipt))]
    [NotifyPropertyChangedFor(nameof(CanRejectTransfer))]
    private string _toWarehouseCode = DomainConstants.WarehouseYeonsuMain;

    [ObservableProperty] private string _memo = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDeleteTransfer))]
    [NotifyPropertyChangedFor(nameof(CanConfirmReceipt))]
    [NotifyPropertyChangedFor(nameof(CanRejectTransfer))]
    private string _transferStatus = "수령대기";
    [ObservableProperty] private string _receiveMemo = string.Empty;
    [ObservableProperty] private string _rejectReason = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _statusMessage = "여러 품목을 한 번에 입력해 재고이동 문서를 저장하고, 도착지에서 수령확정하면 입고가 반영됩니다.";

    [ObservableProperty] private LocalInventoryTransfer? _selectedTransfer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUpdateLine))]
    [NotifyPropertyChangedFor(nameof(CanDeleteLine))]
    private InventoryTransferLineEditModel? _selectedLine;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddLine))]
    [NotifyPropertyChangedFor(nameof(CanUpdateLine))]
    [NotifyPropertyChangedFor(nameof(AvailableStockText))]
    private LocalItem? _selectedInputItem;

    [ObservableProperty] private string _inputItemName = string.Empty;
    [ObservableProperty] private string _inputSpec = string.Empty;
    [ObservableProperty] private string _inputUnit = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddLine))]
    [NotifyPropertyChangedFor(nameof(CanUpdateLine))]
    private decimal _inputQty = 1m;

    [ObservableProperty] private string _inputRemark = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddLine))]
    [NotifyPropertyChangedFor(nameof(CanUpdateLine))]
    private decimal _inputReceivedQty = 1m;
    [ObservableProperty] private string _inputReceiptRemark = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvailableStockText))]
    private decimal _inputAvailableStock;

    public ObservableCollection<LocalWarehouse> Warehouses { get; } = new();
    public ObservableCollection<LocalInventoryTransfer> Transfers { get; } = new();
    public ObservableCollection<InventoryTransferLineEditModel> Lines { get; } = new();

    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusMessage);
    public bool IsAdmin => _session.HasAdministrativePrivileges;
    public bool HasSavedTransfer => TransferId != Guid.Empty;
    public bool CanDeleteTransfer => HasSavedTransfer && CanCurrentUserDelete;
    public bool CanConfirmReceipt => HasSavedTransfer && !IsFinalTransferStatus && CanCurrentUserReceive;
    public bool CanRejectTransfer => HasSavedTransfer && !IsFinalTransferStatus && CanCurrentUserReceive;
    public bool CanAddLine => !IsFinalTransferStatus && SelectedInputItem is not null && InputQty > 0m;
    public bool CanUpdateLine => !IsFinalTransferStatus && SelectedLine is not null && CanAddLine;
    public bool CanDeleteLine => !IsFinalTransferStatus && SelectedLine is not null;
    public bool HasPendingChanges => !string.Equals(_baselineStateSignature, BuildEditStateSignature(CaptureEditSnapshot()), StringComparison.Ordinal);
    public bool HasMeaningfulDraftContentForClose => HasMeaningfulDraftContent(CaptureEditSnapshot());
    public string TransferNumberDisplay => string.IsNullOrWhiteSpace(TransferNumber) ? "(저장 시 자동생성)" : TransferNumber;
    public string TransferRouteText => $"{ResolveWarehouseName(FromWarehouseCode)} → {ResolveWarehouseName(ToWarehouseCode)}";
    public string AvailableStockText => SelectedInputItem is null
        ? "이동 품목을 선택하세요."
        : $"출발창고 현재고 {InputAvailableStock:N0} {InputUnit}".TrimEnd();
    public bool IsFinalTransferStatus =>
        string.Equals(TransferStatus, "수령확정", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(TransferStatus, "반려", StringComparison.OrdinalIgnoreCase);
    public bool CanCurrentUserReceive
    {
        get
        {
            if (_session.HasAdministrativePrivileges)
                return true;

            var destinationOfficeCode = ResolveOfficeCodeFromWarehouseCode(ToWarehouseCode);
            var userOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(_session.OfficeCode, DomainConstants.OfficeUsenet);
            return string.Equals(destinationOfficeCode, userOfficeCode, StringComparison.OrdinalIgnoreCase);
        }
    }
    public bool CanCurrentUserDelete
    {
        get
        {
            if (_session.HasAdministrativePrivileges)
                return true;

            var writableOfficeCodes = _local.GetWritableOfficeCodesForSession(_session);
            var sourceOfficeCode = ResolveOfficeCodeFromWarehouseCode(FromWarehouseCode);
            var destinationOfficeCode = ResolveOfficeCodeFromWarehouseCode(ToWarehouseCode);

            if (IsFinalTransferStatus)
                return writableOfficeCodes.Contains(destinationOfficeCode, StringComparer.OrdinalIgnoreCase);

            return writableOfficeCodes.Contains(sourceOfficeCode, StringComparer.OrdinalIgnoreCase) ||
                   writableOfficeCodes.Contains(destinationOfficeCode, StringComparer.OrdinalIgnoreCase);
        }
    }

    public InventoryTransferViewModel(LocalStateService local, SessionState session)
    {
        _local = local;
        _session = session;
        _local.InventoryStateChanged += HandleInventoryStateChanged;
        ResetEditBaseline();
    }

    public async Task LoadAsync(LocalInventoryTransfer? transfer = null)
    {
        IsBusy = true;
        try
        {
            await LoadLookupsAsync();
            await RefreshWarehouseStocksAsync();
            await RefreshTransfersAsync(transfer?.Id);

            if (transfer?.Id is Guid transferId && transferId != Guid.Empty)
                await OpenTransferAsync(transferId);
            else
                StartNewTransfer();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void HandleInventoryStateChanged(object? sender, EventArgs e)
    {
        if (_isInventoryRefreshInProgress || IsBusy)
            return;

        UiTaskHelper.Forget(HandleInventoryStateChangedAsync(), "UI", "재고이동 화면 재고 상태 새로고침");
    }

    private async Task HandleInventoryStateChangedAsync()
    {
        if (_isInventoryRefreshInProgress || IsBusy)
            return;

        _isInventoryRefreshInProgress = true;
        try
        {
            var selectedInputItemId = SelectedInputItem?.Id;
            await LoadLookupsAsync();
            await RefreshWarehouseStocksAsync();

            if (selectedInputItemId.HasValue)
            {
                var refreshedItem = _allItems.FirstOrDefault(item => item.Id == selectedInputItemId.Value);
                if (refreshedItem is null)
                    ResetLineEditor(clearSelection: false);
                else
                    ApplyInputItem(refreshedItem);
            }
        }
        finally
        {
            _isInventoryRefreshInProgress = false;
        }
    }

    public List<LocalItem> FindItemsForQuickInput(string keyword, int maxCount = 300)
    {
        var text = (keyword ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(text))
            return _allItems.Take(maxCount).ToList();

        return _allItems
            .Where(item =>
                item.NameOriginal.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                item.SpecificationOriginal.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                item.MaterialNumber.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                item.SerialNumber.Contains(text, StringComparison.OrdinalIgnoreCase))
            .Take(maxCount)
            .ToList();
    }

    public void ApplyInputItem(LocalItem item)
    {
        SelectedInputItem = item;
        InputItemName = item.NameOriginal;
        InputSpec = item.SpecificationOriginal;
        InputUnit = item.Unit;
        if (InputQty <= 0m)
            InputQty = 1m;

        UpdateAvailableStock();
    }

    public string BuildItemLookupDescription(LocalItem item)
    {
        var quantity = GetWarehouseStock(item.Id, FromWarehouseCode);
        return $"{item.SpecificationOriginal} | {item.Unit} | 현재고 {quantity:N0}";
    }

    public Task OpenTransferAsync(Guid transferId)
        => OpenTransferAsync(transferId, Interlocked.Increment(ref _openTransferVersion));

    private async Task OpenTransferAsync(Guid transferId, int version)
    {
        if (transferId == Guid.Empty)
        {
            if (version != Volatile.Read(ref _openTransferVersion))
                return;

            StartNewTransfer();
            return;
        }

        var transfer = await _local.GetInventoryTransferAsync(transferId, _session);
        if (version != Volatile.Read(ref _openTransferVersion))
            return;

        if (transfer is null)
        {
            StatusMessage = "선택한 재고이동 문서를 찾을 수 없습니다.";
            return;
        }

        ApplyTransferToEditor(transfer);
        SetSelectedTransfer(transfer.Id);
    }

    public async Task DeleteCurrentTransferAsync()
    {
        if (TransferId == Guid.Empty)
        {
            StatusMessage = "삭제할 재고이동 문서를 먼저 선택하세요.";
            return;
        }

        IsBusy = true;
        try
        {
            var targetTransferId = TransferId;
            var result = await _local.DeleteInventoryTransferAsync(targetTransferId, _session, _transferRevision);
            if (!result.Success)
            {
                StatusMessage = result.Message;
                if (result.ConcurrencyConflict)
                {
                    await RefreshWarehouseStocksAsync();
                    await RefreshTransfersAsync();
                    await OpenTransferAsync(targetTransferId);
                    System.Windows.MessageBox.Show(
                        result.Message,
                        "동시 수정 충돌",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                }

                return;
            }

            await RefreshWarehouseStocksAsync();
            await RefreshTransfersAsync();
            StartNewTransfer();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task NewTransfer()
    {
        if (!await TryAutoSaveCurrentEditAsync(refreshAfterSave: true))
            return;

        StartNewTransfer();
    }

    [RelayCommand]
    private void AddLine()
    {
        if (SelectedInputItem is null)
        {
            StatusMessage = "목록에서 이동 품목을 선택하세요.";
            return;
        }

        if (InputQty <= 0m)
        {
            StatusMessage = "이동 수량은 0보다 커야 합니다.";
            return;
        }

        var line = new InventoryTransferLineEditModel
        {
            ItemId = SelectedInputItem.Id,
            ItemName = InputItemName.Trim(),
            Specification = InputSpec.Trim(),
            Unit = InputUnit.Trim(),
            Quantity = InputQty,
            ReceivedQuantity = InputReceivedQty <= 0m ? InputQty : InputReceivedQty,
            Remark = InputRemark.Trim(),
            ReceiptRemark = InputReceiptRemark.Trim()
        };

        Lines.Add(line);
        SelectedLine = line;
        StatusMessage = $"{Lines.Count:N0}개 품목을 이동 목록에 담았습니다.";
        ResetLineEditor(clearSelection: true);
    }

    [RelayCommand]
    private void UpdateLine()
    {
        if (SelectedLine is null)
        {
            StatusMessage = "수정할 이동 품목을 선택하세요.";
            return;
        }

        if (SelectedInputItem is null)
        {
            StatusMessage = "목록에서 이동 품목을 다시 선택하세요.";
            return;
        }

        if (InputQty <= 0m)
        {
            StatusMessage = "이동 수량은 0보다 커야 합니다.";
            return;
        }

        SelectedLine.ItemId = SelectedInputItem.Id;
        SelectedLine.ItemName = InputItemName.Trim();
        SelectedLine.Specification = InputSpec.Trim();
        SelectedLine.Unit = InputUnit.Trim();
        SelectedLine.Quantity = InputQty;
        SelectedLine.ReceivedQuantity = InputReceivedQty <= 0m ? InputQty : InputReceivedQty;
        SelectedLine.Remark = InputRemark.Trim();
        SelectedLine.ReceiptRemark = InputReceiptRemark.Trim();
        StatusMessage = "선택한 이동 품목을 수정했습니다.";
    }

    [RelayCommand]
    private void DeleteLine()
    {
        if (SelectedLine is null)
        {
            StatusMessage = "삭제할 이동 품목을 선택하세요.";
            return;
        }

        var removedName = SelectedLine.ItemName;
        Lines.Remove(SelectedLine);
        SelectedLine = null;
        ResetLineEditor(clearSelection: true);
        StatusMessage = $"{removedName} 품목을 이동 목록에서 삭제했습니다.";
    }

    [RelayCommand]
    private async Task SaveTransferAsync()
    {
        var snapshot = CaptureEditSnapshot();
        await SaveSnapshotAsync(
            snapshot,
            requestedSelectionId: snapshot.TransferId == Guid.Empty ? null : snapshot.TransferId,
            refreshAfterSave: true,
            successMessage: snapshot.TransferId == Guid.Empty ? "재고이동을 저장했습니다." : "재고이동을 수정했습니다.",
            showConflictDialog: true);
    }

    [RelayCommand]
    private async Task ConfirmReceiptAsync()
    {
        if (!CanConfirmReceipt)
        {
            StatusMessage = "도착지 담당자 또는 관리자만 수령확정할 수 있습니다.";
            return;
        }

        if (Lines.Count == 0)
        {
            StatusMessage = "수령확정할 이동 품목이 없습니다.";
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _local.ConfirmInventoryTransferReceiptAsync(
                TransferId,
                Lines.Select(line => line.ToLocal(TransferId)).ToList(),
                ReceiveMemo,
                _session,
                expectedRevision: _transferRevision);
            StatusMessage = result.Message;
            if (!result.Success)
            {
                if (result.ConcurrencyConflict)
                {
                    await RefreshTransfersAsync(TransferId);
                    await OpenTransferAsync(TransferId);
                    System.Windows.MessageBox.Show(
                        result.Message,
                        "동시 수정 충돌",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                }

                return;
            }

            await RefreshTransfersAsync(result.EntityId);
            await OpenTransferAsync(result.EntityId);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RejectTransferAsync()
    {
        if (!CanRejectTransfer)
        {
            StatusMessage = "도착지 담당자 또는 관리자만 재고이동을 반려할 수 있습니다.";
            return;
        }

        if (string.IsNullOrWhiteSpace(RejectReason))
        {
            StatusMessage = "반려 사유를 입력하세요.";
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _local.RejectInventoryTransferAsync(TransferId, RejectReason, _session, expectedRevision: _transferRevision);
            StatusMessage = result.Message;
            if (!result.Success)
            {
                if (result.ConcurrencyConflict)
                {
                    await RefreshTransfersAsync(TransferId);
                    await OpenTransferAsync(TransferId);
                    System.Windows.MessageBox.Show(
                        result.Message,
                        "동시 수정 충돌",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                }

                return;
            }

            await RefreshTransfersAsync(result.EntityId);
            await OpenTransferAsync(result.EntityId);
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnFromWarehouseCodeChanged(string value)
    {
        UpdateAvailableStock();
        OnPropertyChanged(nameof(TransferRouteText));
        OnPropertyChanged(nameof(CanDeleteTransfer));
    }

    partial void OnToWarehouseCodeChanged(string value)
    {
        OnPropertyChanged(nameof(TransferRouteText));
        OnPropertyChanged(nameof(CanDeleteTransfer));
        OnPropertyChanged(nameof(CanConfirmReceipt));
        OnPropertyChanged(nameof(CanRejectTransfer));
    }

    partial void OnTransferStatusChanged(string value)
    {
        OnPropertyChanged(nameof(IsFinalTransferStatus));
        OnPropertyChanged(nameof(CanAddLine));
        OnPropertyChanged(nameof(CanUpdateLine));
        OnPropertyChanged(nameof(CanDeleteLine));
        OnPropertyChanged(nameof(CanDeleteTransfer));
        OnPropertyChanged(nameof(CanConfirmReceipt));
        OnPropertyChanged(nameof(CanRejectTransfer));
    }

    partial void OnSelectedInputItemChanged(LocalItem? value)
    {
        UpdateAvailableStock();
        OnPropertyChanged(nameof(CanAddLine));
        OnPropertyChanged(nameof(CanUpdateLine));
        OnPropertyChanged(nameof(AvailableStockText));
    }

    partial void OnInputQtyChanged(decimal value)
    {
        if (InputReceivedQty <= 0m)
            InputReceivedQty = value;
        OnPropertyChanged(nameof(CanAddLine));
        OnPropertyChanged(nameof(CanUpdateLine));
    }

    partial void OnInputReceivedQtyChanged(decimal value)
    {
        OnPropertyChanged(nameof(CanAddLine));
        OnPropertyChanged(nameof(CanUpdateLine));
    }

    partial void OnInputUnitChanged(string value)
    {
        OnPropertyChanged(nameof(AvailableStockText));
    }

    partial void OnInputAvailableStockChanged(decimal value)
    {
        OnPropertyChanged(nameof(AvailableStockText));
    }

    partial void OnSelectedTransferChanged(LocalInventoryTransfer? value)
    {
        if (_suppressTransferSelectionChanged || value is null)
            return;

        var version = Interlocked.Increment(ref _openTransferVersion);
        UiTaskHelper.Forget(
            OpenTransferAsync(value.Id, version),
            "TRANSFER",
            "재고이동 상세 열기",
            ex =>
            {
                if (version == Volatile.Read(ref _openTransferVersion))
                    StatusMessage = $"재고이동 상세를 열지 못했습니다. {ex.Message}";
            });
    }

    partial void OnSelectedTransferChanging(LocalInventoryTransfer? oldValue, LocalInventoryTransfer? newValue)
    {
        if (_suppressTransferSelectionChanged || ReferenceEquals(oldValue, newValue))
            return;

        if (!TryCaptureAutoSaveSnapshot(out var snapshot))
            return;

        UiTaskHelper.Forget(
            HandleSelectionAutoSaveAsync(snapshot, oldValue, newValue),
            "TRANSFER",
            "재고이동 선택 변경 자동저장",
            ex => StatusMessage = $"재고이동 자동저장 중 오류가 발생했습니다. {ex.Message}");
    }

    partial void OnSelectedLineChanged(InventoryTransferLineEditModel? value)
    {
        if (_suppressLineSelectionChanged)
            return;

        if (value is null)
        {
            OnPropertyChanged(nameof(CanUpdateLine));
            OnPropertyChanged(nameof(CanDeleteLine));
            return;
        }

        var matchedItem = value.ItemId.HasValue
            ? _allItems.FirstOrDefault(item => item.Id == value.ItemId.Value)
            : null;

        if (matchedItem is not null)
            ApplyInputItem(matchedItem);
        else
        {
            SelectedInputItem = null;
            InputItemName = value.ItemName;
            InputSpec = value.Specification;
            InputUnit = value.Unit;
        }

        InputQty = value.Quantity;
        InputReceivedQty = value.ReceivedQuantity;
        InputRemark = value.Remark;
        InputReceiptRemark = value.ReceiptRemark;
        UpdateAvailableStock();
        OnPropertyChanged(nameof(CanUpdateLine));
        OnPropertyChanged(nameof(CanDeleteLine));
    }

    private async Task LoadLookupsAsync()
    {
        _allItems = await _local.GetItemsForInventoryTransferAsync(_session);

        var warehouses = await _local.GetWarehousesForInventoryTransferAsync(_session);
        Warehouses.Clear();
        _warehouseNames.Clear();

        foreach (var warehouse in warehouses
                     .Where(current => current.IsActive)
                     .OrderBy(current => current.OfficeCode, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(current => current.Name, StringComparer.OrdinalIgnoreCase))
        {
            Warehouses.Add(warehouse);
            _warehouseNames[NormalizeCode(warehouse.Code)] = warehouse.Name;
        }
    }

    private async Task RefreshWarehouseStocksAsync()
    {
        _warehouseStocks.Clear();
        foreach (var stock in await _local.GetItemWarehouseStocksForInventoryTransferAsync(_session))
        {
            var key = (stock.ItemId, NormalizeCode(stock.WarehouseCode));
            _warehouseStocks[key] = stock.Quantity;
        }

        UpdateAvailableStock();
    }

    private async Task RefreshTransfersAsync(Guid? selectedTransferId = null)
    {
        var transfers = await _local.GetInventoryTransfersAsync(_session);
        Transfers.Clear();
        foreach (var transfer in transfers)
            Transfers.Add(transfer);

        SetSelectedTransfer(selectedTransferId);
    }

    private void ApplyTransferToEditor(LocalInventoryTransfer transfer)
    {
        ApplySnapshot(CreateSnapshotFromTransfer(transfer), resetBaseline: true);
        StatusMessage = $"재고이동 {TransferNumberDisplay} 문서를 불러왔습니다.";
    }

    private void StartNewTransfer()
    {
        ApplySnapshot(CreateNewTransferSnapshot(), resetBaseline: true);
        SetSelectedTransfer(null);
        StatusMessage = BuildNewTransferStatusMessage();
    }

    private string BuildNewTransferStatusMessage()
    {
        if (Warehouses.Count == 0)
            return "현재 업체에서 사용할 수 있는 재고이동 창고가 없습니다.";

        if (Warehouses.Count == 1)
            return "현재 업체에서 사용할 수 있는 내부 재고이동 창고가 1개뿐이라 저장할 수 없습니다.";

        return "새 내부 재고이동 문서를 작성하세요.";
    }

    private void ResetLineEditor(bool clearSelection)
    {
        if (clearSelection)
            SelectedLine = null;

        SelectedInputItem = null;
        InputItemName = string.Empty;
        InputSpec = string.Empty;
        InputUnit = string.Empty;
        InputQty = 1m;
        InputRemark = string.Empty;
        InputReceivedQty = 1m;
        InputReceiptRemark = string.Empty;
        InputAvailableStock = 0m;
    }

    private string DetermineDefaultFromWarehouseCode()
    {
        if (Warehouses.Count == 0)
            return OfficeCodeCatalog.GetMainWarehouseCode(_session.OfficeCode);

        var preferredOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(_session.OfficeCode, DomainConstants.OfficeUsenet);

        return Warehouses.FirstOrDefault(warehouse =>
                   string.Equals(warehouse.OfficeCode, preferredOfficeCode, StringComparison.OrdinalIgnoreCase))?.Code
               ?? Warehouses.First().Code;
    }

    private string DetermineDefaultToWarehouseCode(string fromWarehouseCode)
    {
        var fromWarehouse = Warehouses.FirstOrDefault(warehouse =>
            string.Equals(warehouse.Code, fromWarehouseCode, StringComparison.OrdinalIgnoreCase));

        if (fromWarehouse is not null)
        {
            var oppositeWarehouse = Warehouses.FirstOrDefault(warehouse =>
                !string.Equals(warehouse.Code, fromWarehouseCode, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(
                    OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(warehouse.OfficeCode, DomainConstants.OfficeUsenet),
                    OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(fromWarehouse.OfficeCode, DomainConstants.OfficeUsenet),
                    StringComparison.OrdinalIgnoreCase));
            if (oppositeWarehouse is not null)
                return oppositeWarehouse.Code;
        }

        return Warehouses.FirstOrDefault(warehouse =>
                   !string.Equals(warehouse.Code, fromWarehouseCode, StringComparison.OrdinalIgnoreCase))?.Code
               ?? fromWarehouseCode;
    }

    private void UpdateAvailableStock()
    {
        InputAvailableStock = SelectedInputItem is null
            ? 0m
            : GetWarehouseStock(SelectedInputItem.Id, FromWarehouseCode);
    }

    private decimal GetWarehouseStock(Guid itemId, string warehouseCode)
    {
        var key = (itemId, NormalizeCode(warehouseCode));
        return _warehouseStocks.TryGetValue(key, out var quantity) ? quantity : 0m;
    }

    private string ResolveWarehouseName(string? warehouseCode)
    {
        var normalized = NormalizeCode(warehouseCode);
        if (_warehouseNames.TryGetValue(normalized, out var name))
            return name;

        return string.IsNullOrWhiteSpace(normalized) ? "-" : normalized;
    }

    private void SetSelectedTransfer(Guid? transferId)
    {
        _suppressTransferSelectionChanged = true;
        SelectedTransfer = transferId.HasValue && transferId.Value != Guid.Empty
            ? Transfers.FirstOrDefault(transfer => transfer.Id == transferId.Value)
            : null;
        _suppressTransferSelectionChanged = false;
    }

    private static string NormalizeCode(string? code)
        => OfficeCodeCatalog.NormalizeWarehouseCodeLoose(code);

    private static string ResolveOfficeCodeFromWarehouseCode(string? warehouseCode)
    {
        var normalizedWarehouseCode = OfficeCodeCatalog.NormalizeWarehouseCodeLoose(warehouseCode);
        return normalizedWarehouseCode switch
        {
            var value when string.Equals(value, DomainConstants.WarehouseItworldMain, StringComparison.OrdinalIgnoreCase) => DomainConstants.OfficeItworld,
            var value when string.Equals(value, DomainConstants.WarehouseYeonsuMain, StringComparison.OrdinalIgnoreCase) => DomainConstants.OfficeYeonsu,
            _ => DomainConstants.OfficeUsenet
        };
    }

    public async Task<bool> TryAutoSaveOnCloseAsync()
        => await TryAutoSaveCurrentEditAsync(refreshAfterSave: false);

    private async Task<bool> HandleSelectionAutoSaveAsync(
        InventoryTransferEditSnapshot snapshot,
        LocalInventoryTransfer? previousSelection,
        LocalInventoryTransfer? requestedSelection)
    {
        var saved = await SaveSnapshotAsync(
            snapshot,
            requestedSelectionId: requestedSelection?.Id,
            refreshAfterSave: true,
            successMessage: "재고이동 문서를 자동 저장했습니다.",
            showConflictDialog: false);

        if (saved)
            return true;

        RestoreEditSnapshot(previousSelection, snapshot);
        StatusMessage = string.IsNullOrWhiteSpace(StatusMessage)
            ? "자동저장에 실패해 기존 편집 내용을 유지했습니다."
            : $"{StatusMessage} 기존 편집 내용은 유지했습니다.";
        return false;
    }

    private async Task<bool> TryAutoSaveCurrentEditAsync(bool refreshAfterSave)
    {
        if (!TryCaptureAutoSaveSnapshot(out var snapshot))
            return true;

        return await SaveSnapshotAsync(
            snapshot,
            requestedSelectionId: SelectedTransfer?.Id,
            refreshAfterSave: refreshAfterSave,
            successMessage: "재고이동 문서를 자동 저장했습니다.",
            showConflictDialog: false);
    }

    private bool TryCaptureAutoSaveSnapshot(out InventoryTransferEditSnapshot snapshot)
    {
        snapshot = CaptureEditSnapshot();
        return HasPendingChanges && HasMeaningfulDraftContent(snapshot);
    }

    private async Task<bool> SaveSnapshotAsync(
        InventoryTransferEditSnapshot snapshot,
        Guid? requestedSelectionId,
        bool refreshAfterSave,
        string successMessage,
        bool showConflictDialog)
    {
        await _autoSaveGate.WaitAsync();
        try
        {
            if (!TryBuildTransferForSave(snapshot, out var transfer, out var validationMessage))
            {
                StatusMessage = validationMessage;
                return false;
            }

            IsBusy = true;
            try
            {
                var result = await _local.SaveInventoryTransferAsync(transfer, _session);
                if (!result.Success)
                {
                    StatusMessage = result.Message;
                    if (result.ConcurrencyConflict && showConflictDialog)
                    {
                        System.Windows.MessageBox.Show(
                            result.Message,
                            "동시 수정 충돌",
                            System.Windows.MessageBoxButton.OK,
                            System.Windows.MessageBoxImage.Warning);
                    }

                    return false;
                }

                if (refreshAfterSave)
                {
                    await RefreshWarehouseStocksAsync();
                    await RefreshTransfersAsync();
                    var reopenId = requestedSelectionId.HasValue && requestedSelectionId.Value != Guid.Empty
                        ? requestedSelectionId.Value
                        : result.EntityId;
                    await OpenTransferAsync(reopenId);
                }

                StatusMessage = successMessage;
                return true;
            }
            finally
            {
                IsBusy = false;
            }
        }
        finally
        {
            _autoSaveGate.Release();
        }
    }

    private bool TryBuildTransferForSave(
        InventoryTransferEditSnapshot snapshot,
        out LocalInventoryTransfer transfer,
        out string validationMessage)
    {
        transfer = new LocalInventoryTransfer();
        validationMessage = string.Empty;

        if (IsFinalTransferStatusText(snapshot.TransferStatus))
        {
            validationMessage = "수령확정 또는 반려된 문서는 수정할 수 없습니다.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(snapshot.FromWarehouseCode) || string.IsNullOrWhiteSpace(snapshot.ToWarehouseCode))
        {
            validationMessage = "출발창고와 도착창고를 모두 선택하세요.";
            return false;
        }

        if (string.Equals(snapshot.FromWarehouseCode, snapshot.ToWarehouseCode, StringComparison.OrdinalIgnoreCase))
        {
            validationMessage = "출발창고와 도착창고는 서로 달라야 합니다.";
            return false;
        }

        var materializedLines = snapshot.Lines
            .Select(CloneLineSnapshot)
            .ToList();

        var selectedLineIndex = snapshot.SelectedLineId.HasValue
            ? materializedLines.FindIndex(line => line.Id == snapshot.SelectedLineId.Value)
            : -1;
        var referenceLine = selectedLineIndex >= 0 ? materializedLines[selectedLineIndex] : null;
        var draftState = EvaluateLineDraft(snapshot, referenceLine, out var draftLine, out validationMessage);
        switch (draftState)
        {
            case LineDraftState.Invalid:
                return false;
            case LineDraftState.Valid when draftLine is not null && selectedLineIndex >= 0:
                materializedLines[selectedLineIndex] = draftLine;
                break;
            case LineDraftState.Valid when draftLine is not null:
                materializedLines.Add(draftLine);
                break;
        }

        var validLines = materializedLines
            .Where(line => line.ItemId.HasValue
                           && !string.IsNullOrWhiteSpace(line.ItemName)
                           && line.Quantity > 0m)
            .ToList();

        if (validLines.Count == 0)
        {
            validationMessage = "이동 품목을 1개 이상 입력하세요.";
            return false;
        }

        var transferId = snapshot.TransferId == Guid.Empty ? Guid.NewGuid() : snapshot.TransferId;
        transfer = new LocalInventoryTransfer
        {
            Id = transferId,
            Revision = snapshot.Revision,
            TransferNumber = snapshot.TransferNumber,
            TransferDate = snapshot.TransferDate,
            FromWarehouseCode = snapshot.FromWarehouseCode,
            ToWarehouseCode = snapshot.ToWarehouseCode,
            Memo = snapshot.Memo.Trim(),
            TransferStatus = snapshot.TransferStatus,
            ReceiveMemo = snapshot.ReceiveMemo.Trim(),
            RejectReason = snapshot.RejectReason.Trim(),
            Lines = validLines.Select(line => line.ToLocal(transferId)).ToList()
        };
        return true;
    }

    private void RestoreEditSnapshot(LocalInventoryTransfer? previousSelection, InventoryTransferEditSnapshot snapshot)
    {
        Interlocked.Increment(ref _openTransferVersion);
        SetSelectedTransfer(previousSelection?.Id);
        ApplySnapshot(snapshot, resetBaseline: false);
    }

    private InventoryTransferEditSnapshot CaptureEditSnapshot()
        => new(
            TransferId,
            _transferRevision,
            TransferNumber,
            TransferDate,
            FromWarehouseCode,
            ToWarehouseCode,
            Memo,
            TransferStatus,
            ReceiveMemo,
            RejectReason,
            Lines.Select(static line => InventoryTransferLineSnapshot.FromEditModel(line)).ToList(),
            SelectedLine?.Id,
            SelectedInputItem?.Id,
            InputItemName,
            InputSpec,
            InputUnit,
            InputQty,
            InputRemark,
            InputReceivedQty,
            InputReceiptRemark);

    private InventoryTransferEditSnapshot CreateSnapshotFromTransfer(LocalInventoryTransfer transfer)
        => new(
            transfer.Id,
            transfer.Revision,
            transfer.TransferNumber ?? string.Empty,
            transfer.TransferDate,
            transfer.FromWarehouseCode ?? string.Empty,
            transfer.ToWarehouseCode ?? string.Empty,
            transfer.Memo ?? string.Empty,
            string.IsNullOrWhiteSpace(transfer.TransferStatus) ? "수령대기" : transfer.TransferStatus,
            transfer.ReceiveMemo ?? string.Empty,
            transfer.RejectReason ?? string.Empty,
            transfer.Lines
                .Where(current => !current.IsDeleted)
                .Select(InventoryTransferLineSnapshot.FromLocal)
                .ToList(),
            null,
            null,
            string.Empty,
            string.Empty,
            string.Empty,
            1m,
            string.Empty,
            1m,
            string.Empty);

    private InventoryTransferEditSnapshot CreateNewTransferSnapshot()
    {
        var fromWarehouseCode = DetermineDefaultFromWarehouseCode();
        return new InventoryTransferEditSnapshot(
            Guid.Empty,
            0,
            string.Empty,
            DateOnly.FromDateTime(DateTime.Today),
            fromWarehouseCode,
            DetermineDefaultToWarehouseCode(fromWarehouseCode),
            string.Empty,
            "수령대기",
            string.Empty,
            string.Empty,
            [],
            null,
            null,
            string.Empty,
            string.Empty,
            string.Empty,
            1m,
            string.Empty,
            1m,
            string.Empty);
    }

    private void ApplySnapshot(InventoryTransferEditSnapshot snapshot, bool resetBaseline)
    {
        _suppressLineSelectionChanged = true;
        try
        {
            TransferId = snapshot.TransferId;
            _transferRevision = snapshot.Revision;
            TransferNumber = snapshot.TransferNumber;
            TransferDate = snapshot.TransferDate;
            FromWarehouseCode = snapshot.FromWarehouseCode;
            ToWarehouseCode = snapshot.ToWarehouseCode;
            Memo = snapshot.Memo;
            TransferStatus = snapshot.TransferStatus;
            ReceiveMemo = snapshot.ReceiveMemo;
            RejectReason = snapshot.RejectReason;

            Lines.Clear();
            foreach (var line in snapshot.Lines.Select(line => line.ToEditModel()))
                Lines.Add(line);

            SelectedLine = snapshot.SelectedLineId.HasValue
                ? Lines.FirstOrDefault(line => line.Id == snapshot.SelectedLineId.Value)
                : null;
            SelectedInputItem = snapshot.SelectedInputItemId.HasValue
                ? _allItems.FirstOrDefault(item => item.Id == snapshot.SelectedInputItemId.Value)
                : null;
            InputItemName = snapshot.InputItemName;
            InputSpec = snapshot.InputSpec;
            InputUnit = snapshot.InputUnit;
            InputQty = snapshot.InputQty;
            InputRemark = snapshot.InputRemark;
            InputReceivedQty = snapshot.InputReceivedQty;
            InputReceiptRemark = snapshot.InputReceiptRemark;
        }
        finally
        {
            _suppressLineSelectionChanged = false;
        }

        UpdateAvailableStock();
        OnPropertyChanged(nameof(CanAddLine));
        OnPropertyChanged(nameof(CanUpdateLine));
        OnPropertyChanged(nameof(CanDeleteLine));
        OnPropertyChanged(nameof(AvailableStockText));

        if (resetBaseline)
            ResetEditBaseline();
    }

    private void ResetEditBaseline()
        => _baselineStateSignature = BuildEditStateSignature(CaptureEditSnapshot());

    private string BuildEditStateSignature(InventoryTransferEditSnapshot snapshot)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append(snapshot.TransferId.ToString("D"))
            .Append('|').Append(snapshot.TransferNumber ?? string.Empty)
            .Append('|').Append(snapshot.TransferDate.ToString("yyyy-MM-dd"))
            .Append('|').Append(snapshot.FromWarehouseCode ?? string.Empty)
            .Append('|').Append(snapshot.ToWarehouseCode ?? string.Empty)
            .Append('|').Append(snapshot.Memo ?? string.Empty)
            .Append('|').Append(snapshot.TransferStatus ?? string.Empty)
            .Append('|').Append(snapshot.ReceiveMemo ?? string.Empty)
            .Append('|').Append(snapshot.RejectReason ?? string.Empty);

        var materializedLines = snapshot.Lines
            .Select(CloneLineSnapshot)
            .ToList();
        var selectedLineIndex = snapshot.SelectedLineId.HasValue
            ? materializedLines.FindIndex(line => line.Id == snapshot.SelectedLineId.Value)
            : -1;
        var referenceLine = selectedLineIndex >= 0 ? materializedLines[selectedLineIndex] : null;
        var draftState = EvaluateLineDraft(snapshot, referenceLine, out var draftLine, out _);
        if (draftState == LineDraftState.Valid && draftLine is not null)
        {
            if (selectedLineIndex >= 0)
                materializedLines[selectedLineIndex] = draftLine;
            else
                materializedLines.Add(draftLine);
        }

        foreach (var line in materializedLines)
        {
            builder.Append('|').Append(line.Id.ToString("D"))
                .Append(':').Append(line.ItemId?.ToString("D") ?? string.Empty)
                .Append(':').Append(line.ItemName ?? string.Empty)
                .Append(':').Append(line.Specification ?? string.Empty)
                .Append(':').Append(line.Unit ?? string.Empty)
                .Append(':').Append(line.Quantity)
                .Append(':').Append(line.ReceivedQuantity)
                .Append(':').Append(line.Remark ?? string.Empty)
                .Append(':').Append(line.ReceiptRemark ?? string.Empty);
        }

        if (draftState == LineDraftState.Invalid)
        {
            builder.Append("|draft-invalid:")
                .Append(snapshot.SelectedLineId?.ToString("D") ?? string.Empty)
                .Append(':').Append(snapshot.SelectedInputItemId?.ToString("D") ?? string.Empty)
                .Append(':').Append(snapshot.InputItemName ?? string.Empty)
                .Append(':').Append(snapshot.InputSpec ?? string.Empty)
                .Append(':').Append(snapshot.InputUnit ?? string.Empty)
                .Append(':').Append(snapshot.InputQty)
                .Append(':').Append(snapshot.InputReceivedQty)
                .Append(':').Append(snapshot.InputRemark ?? string.Empty)
                .Append(':').Append(snapshot.InputReceiptRemark ?? string.Empty);
        }

        return builder.ToString();
    }

    private bool HasMeaningfulDraftContent(InventoryTransferEditSnapshot snapshot)
    {
        var empty = CreateNewTransferSnapshot();
        return !string.Equals(snapshot.TransferNumber, empty.TransferNumber, StringComparison.Ordinal)
               || snapshot.TransferDate != empty.TransferDate
               || !string.Equals(snapshot.FromWarehouseCode, empty.FromWarehouseCode, StringComparison.OrdinalIgnoreCase)
               || !string.Equals(snapshot.ToWarehouseCode, empty.ToWarehouseCode, StringComparison.OrdinalIgnoreCase)
               || !string.IsNullOrWhiteSpace(snapshot.Memo)
               || !string.IsNullOrWhiteSpace(snapshot.ReceiveMemo)
               || !string.IsNullOrWhiteSpace(snapshot.RejectReason)
               || snapshot.Lines.Count > 0
               || HasAnyMeaningfulLineEditorInput(snapshot);
    }

    private static InventoryTransferLineSnapshot CloneLineSnapshot(InventoryTransferLineSnapshot line)
        => new(
            line.Id,
            line.ItemId,
            line.ItemName,
            line.Specification,
            line.Unit,
            line.Quantity,
            line.ReceivedQuantity,
            line.Remark,
            line.ReceiptRemark);

    private static bool IsFinalTransferStatusText(string? status)
        => string.Equals(status, "수령확정", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "반려", StringComparison.OrdinalIgnoreCase);

    private static bool HasAnyMeaningfulLineEditorInput(InventoryTransferEditSnapshot snapshot)
        => snapshot.SelectedInputItemId.HasValue
           || !string.IsNullOrWhiteSpace(snapshot.InputItemName)
           || !string.IsNullOrWhiteSpace(snapshot.InputSpec)
           || !string.IsNullOrWhiteSpace(snapshot.InputUnit)
           || !string.IsNullOrWhiteSpace(snapshot.InputRemark)
           || !string.IsNullOrWhiteSpace(snapshot.InputReceiptRemark)
           || snapshot.InputQty != 1m
           || snapshot.InputReceivedQty != 1m;

    private static LineDraftState EvaluateLineDraft(
        InventoryTransferEditSnapshot snapshot,
        InventoryTransferLineSnapshot? referenceLine,
        out InventoryTransferLineSnapshot? draftLine,
        out string validationMessage)
    {
        draftLine = null;
        validationMessage = string.Empty;

        var hasMeaningfulInput = HasAnyMeaningfulLineEditorInput(snapshot);
        if (referenceLine is null && !hasMeaningfulInput)
            return LineDraftState.None;

        var resolvedItemId = snapshot.SelectedInputItemId ?? referenceLine?.ItemId;
        var normalizedQuantity = snapshot.InputQty;
        var normalizedReceivedQuantity = snapshot.InputReceivedQty <= 0m
            ? normalizedQuantity
            : snapshot.InputReceivedQty;

        draftLine = new InventoryTransferLineSnapshot(
            referenceLine?.Id ?? Guid.NewGuid(),
            resolvedItemId,
            snapshot.InputItemName.Trim(),
            snapshot.InputSpec.Trim(),
            snapshot.InputUnit.Trim(),
            normalizedQuantity,
            normalizedReceivedQuantity,
            snapshot.InputRemark.Trim(),
            snapshot.InputReceiptRemark.Trim());

        if (referenceLine is not null && draftLine.Equals(referenceLine))
            return LineDraftState.None;

        if (!hasMeaningfulInput && referenceLine is null)
            return LineDraftState.None;

        if (!draftLine.ItemId.HasValue)
        {
            validationMessage = "목록에서 이동 품목을 선택하세요.";
            return LineDraftState.Invalid;
        }

        if (string.IsNullOrWhiteSpace(draftLine.ItemName))
        {
            validationMessage = "이동 품목명을 입력하세요.";
            return LineDraftState.Invalid;
        }

        if (draftLine.Quantity <= 0m)
        {
            validationMessage = "이동 수량은 0보다 커야 합니다.";
            return LineDraftState.Invalid;
        }

        return LineDraftState.Valid;
    }

    private enum LineDraftState
    {
        None,
        Valid,
        Invalid
    }

    private sealed record InventoryTransferEditSnapshot(
        Guid TransferId,
        long Revision,
        string TransferNumber,
        DateOnly TransferDate,
        string FromWarehouseCode,
        string ToWarehouseCode,
        string Memo,
        string TransferStatus,
        string ReceiveMemo,
        string RejectReason,
        IReadOnlyList<InventoryTransferLineSnapshot> Lines,
        Guid? SelectedLineId,
        Guid? SelectedInputItemId,
        string InputItemName,
        string InputSpec,
        string InputUnit,
        decimal InputQty,
        string InputRemark,
        decimal InputReceivedQty,
        string InputReceiptRemark);

    private sealed record InventoryTransferLineSnapshot(
        Guid Id,
        Guid? ItemId,
        string ItemName,
        string Specification,
        string Unit,
        decimal Quantity,
        decimal ReceivedQuantity,
        string Remark,
        string ReceiptRemark)
    {
        public static InventoryTransferLineSnapshot FromEditModel(InventoryTransferLineEditModel line)
            => new(
                line.Id,
                line.ItemId,
                line.ItemName ?? string.Empty,
                line.Specification ?? string.Empty,
                line.Unit ?? string.Empty,
                line.Quantity,
                line.ReceivedQuantity,
                line.Remark ?? string.Empty,
                line.ReceiptRemark ?? string.Empty);

        public static InventoryTransferLineSnapshot FromLocal(LocalInventoryTransferLine line)
            => new(
                line.Id,
                line.ItemId,
                line.ItemNameOriginal ?? string.Empty,
                line.SpecificationOriginal ?? string.Empty,
                line.Unit ?? string.Empty,
                line.Quantity,
                line.ReceivedQuantity ?? line.Quantity,
                line.Remark ?? string.Empty,
                line.ReceiptRemark ?? string.Empty);

        public InventoryTransferLineEditModel ToEditModel()
            => new()
            {
                Id = Id,
                ItemId = ItemId,
                ItemName = ItemName,
                Specification = Specification,
                Unit = Unit,
                Quantity = Quantity,
                ReceivedQuantity = ReceivedQuantity,
                Remark = Remark,
                ReceiptRemark = ReceiptRemark
            };

        public LocalInventoryTransferLine ToLocal(Guid transferId)
            => new()
            {
                Id = Id == Guid.Empty ? Guid.NewGuid() : Id,
                TransferId = transferId,
                ItemId = ItemId,
                ItemNameOriginal = ItemName ?? string.Empty,
                SpecificationOriginal = Specification ?? string.Empty,
                Unit = Unit ?? string.Empty,
                Quantity = Quantity,
                ReceivedQuantity = ReceivedQuantity,
                QuantityDifference = ReceivedQuantity - Quantity,
                Remark = Remark ?? string.Empty,
                ReceiptRemark = ReceiptRemark ?? string.Empty,
                IsDeleted = false
            };
    }
}
