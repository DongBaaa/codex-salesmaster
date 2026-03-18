using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SalesMaster.Desktop.App.Data;
using SalesMaster.Desktop.App.Services;
using SalesMaster.Shared.Contracts;

namespace SalesMaster.Desktop.App.ViewModels;

public sealed partial class InventoryTransferViewModel : ObservableObject
{
    private readonly LocalStateService _local;
    private readonly SessionState _session;
    private readonly Dictionary<(Guid ItemId, string WarehouseCode), decimal> _warehouseStocks = new();
    private readonly Dictionary<string, string> _warehouseNames = new(StringComparer.OrdinalIgnoreCase);
    private List<LocalItem> _allItems = new();
    private bool _suppressTransferSelectionChanged;
    private bool _isInventoryRefreshInProgress;

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
    private string _fromWarehouseCode = DomainConstants.WarehouseUsenetMain;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TransferRouteText))]
    [NotifyPropertyChangedFor(nameof(CanConfirmReceipt))]
    [NotifyPropertyChangedFor(nameof(CanRejectTransfer))]
    private string _toWarehouseCode = DomainConstants.WarehouseYeonsuMain;

    [ObservableProperty] private string _memo = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty]
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
    public bool IsAdmin => _session.IsAdmin;
    public bool HasSavedTransfer => TransferId != Guid.Empty;
    public bool CanDeleteTransfer => HasSavedTransfer;
    public bool CanConfirmReceipt => HasSavedTransfer && !IsFinalTransferStatus && CanCurrentUserReceive;
    public bool CanRejectTransfer => HasSavedTransfer && !IsFinalTransferStatus && CanCurrentUserReceive;
    public bool CanAddLine => !IsFinalTransferStatus && SelectedInputItem is not null && InputQty > 0m;
    public bool CanUpdateLine => !IsFinalTransferStatus && SelectedLine is not null && CanAddLine;
    public bool CanDeleteLine => !IsFinalTransferStatus && SelectedLine is not null;
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
            if (_session.IsAdmin)
                return true;

            var destinationOfficeCode = ResolveOfficeCodeFromWarehouseCode(ToWarehouseCode);
            var userOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(_session.OfficeCode, DomainConstants.OfficeUsenet);
            return string.Equals(destinationOfficeCode, userOfficeCode, StringComparison.OrdinalIgnoreCase);
        }
    }

    public InventoryTransferViewModel(LocalStateService local, SessionState session)
    {
        _local = local;
        _session = session;
        _local.InventoryStateChanged += HandleInventoryStateChanged;
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

    private async void HandleInventoryStateChanged(object? sender, EventArgs e)
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
        catch
        {
            // keep current editor state if background refresh fails
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

    public async Task OpenTransferAsync(Guid transferId)
    {
        if (transferId == Guid.Empty)
        {
            StartNewTransfer();
            return;
        }

        var transfer = await _local.GetInventoryTransferAsync(transferId);
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
            var result = await _local.DeleteInventoryTransferAsync(TransferId, _session);
            StatusMessage = result.Message;
            if (!result.Success)
                return;

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
    private void NewTransfer()
    {
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
        if (IsFinalTransferStatus)
        {
            StatusMessage = "수령확정 또는 반려된 문서는 수정할 수 없습니다.";
            return;
        }

        if (string.IsNullOrWhiteSpace(FromWarehouseCode) || string.IsNullOrWhiteSpace(ToWarehouseCode))
        {
            StatusMessage = "출발창고와 도착창고를 모두 선택하세요.";
            return;
        }

        if (string.Equals(FromWarehouseCode, ToWarehouseCode, StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage = "출발창고와 도착창고는 서로 달라야 합니다.";
            return;
        }

        if (Lines.Count == 0)
        {
            StatusMessage = "이동 품목을 1개 이상 입력하세요.";
            return;
        }

        IsBusy = true;
        try
        {
            var transferId = TransferId == Guid.Empty ? Guid.NewGuid() : TransferId;
            var transfer = new LocalInventoryTransfer
            {
                Id = transferId,
                TransferNumber = TransferNumber,
                TransferDate = TransferDate,
                FromWarehouseCode = FromWarehouseCode,
                ToWarehouseCode = ToWarehouseCode,
                Memo = Memo?.Trim() ?? string.Empty,
                Lines = Lines.Select(line => line.ToLocal(transferId)).ToList()
            };

            var result = await _local.SaveInventoryTransferAsync(transfer, _session);
            StatusMessage = result.Message;
            if (!result.Success)
                return;

            await RefreshWarehouseStocksAsync();
            await RefreshTransfersAsync(result.EntityId);
            await OpenTransferAsync(result.EntityId);
        }
        finally
        {
            IsBusy = false;
        }
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
                _session);
            StatusMessage = result.Message;
            if (!result.Success)
                return;

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
            var result = await _local.RejectInventoryTransferAsync(TransferId, RejectReason, _session);
            StatusMessage = result.Message;
            if (!result.Success)
                return;

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
    }

    partial void OnToWarehouseCodeChanged(string value)
    {
        OnPropertyChanged(nameof(TransferRouteText));
        OnPropertyChanged(nameof(CanConfirmReceipt));
        OnPropertyChanged(nameof(CanRejectTransfer));
    }

    partial void OnTransferStatusChanged(string value)
    {
        OnPropertyChanged(nameof(IsFinalTransferStatus));
        OnPropertyChanged(nameof(CanAddLine));
        OnPropertyChanged(nameof(CanUpdateLine));
        OnPropertyChanged(nameof(CanDeleteLine));
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

        _ = OpenTransferAsync(value.Id);
    }

    partial void OnSelectedLineChanged(InventoryTransferLineEditModel? value)
    {
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
        _allItems = await _local.GetItemsAsync();

        var warehouses = await _local.GetWarehousesAsync();
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
        foreach (var stock in await _local.GetItemWarehouseStocksAsync())
        {
            var key = (stock.ItemId, NormalizeCode(stock.WarehouseCode));
            _warehouseStocks[key] = stock.Quantity;
        }

        UpdateAvailableStock();
    }

    private async Task RefreshTransfersAsync(Guid? selectedTransferId = null)
    {
        var transfers = await _local.GetInventoryTransfersAsync();
        Transfers.Clear();
        foreach (var transfer in transfers)
            Transfers.Add(transfer);

        SetSelectedTransfer(selectedTransferId);
    }

    private void ApplyTransferToEditor(LocalInventoryTransfer transfer)
    {
        TransferId = transfer.Id;
        TransferNumber = transfer.TransferNumber ?? string.Empty;
        TransferDate = transfer.TransferDate;
        FromWarehouseCode = transfer.FromWarehouseCode ?? string.Empty;
        ToWarehouseCode = transfer.ToWarehouseCode ?? string.Empty;
        Memo = transfer.Memo ?? string.Empty;
        TransferStatus = string.IsNullOrWhiteSpace(transfer.TransferStatus) ? "수령대기" : transfer.TransferStatus;
        ReceiveMemo = transfer.ReceiveMemo ?? string.Empty;
        RejectReason = transfer.RejectReason ?? string.Empty;

        Lines.Clear();
        foreach (var line in transfer.Lines.Where(current => !current.IsDeleted))
            Lines.Add(InventoryTransferLineEditModel.FromLocal(line));

        SelectedLine = null;
        ResetLineEditor(clearSelection: true);
        StatusMessage = $"재고이동 {TransferNumberDisplay} 문서를 불러왔습니다.";
    }

    private void StartNewTransfer()
    {
        TransferId = Guid.Empty;
        TransferNumber = string.Empty;
        TransferDate = DateOnly.FromDateTime(DateTime.Today);
        FromWarehouseCode = DetermineDefaultFromWarehouseCode();
        ToWarehouseCode = DetermineDefaultToWarehouseCode(FromWarehouseCode);
        Memo = string.Empty;
        TransferStatus = "수령대기";
        ReceiveMemo = string.Empty;
        RejectReason = string.Empty;
        Lines.Clear();
        SelectedLine = null;
        ResetLineEditor(clearSelection: true);
        SetSelectedTransfer(null);
        StatusMessage = "새 내부 재고이동 문서를 작성하세요.";
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
}
