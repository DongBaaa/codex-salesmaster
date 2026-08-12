using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class InventoryTransferViewModel : ObservableObject, IDisposable
{
    private readonly LocalStateService _local;
    private readonly SessionState _session;
    private readonly SemaphoreSlim _autoSaveGate = new(1, 1);
    private readonly UiAsyncRefreshCoalescer _externalStateRefresh;
    private readonly Dictionary<(Guid ItemId, string WarehouseCode), decimal> _warehouseStocks = new();
    private readonly Dictionary<string, string> _warehouseNames = new(StringComparer.OrdinalIgnoreCase);
    private List<LocalItem> _allItems = new();
    private bool _suppressTransferSelectionChanged;
    private bool _suppressLineSelectionChanged;
    private bool _deferExternalRefreshUntilIdle;
    private bool _isDisposed;
    private int _openTransferVersion;
    private string _baselineStateSignature = string.Empty;
    private long _transferRevision;
    private DateTime _transferUpdatedAtUtc;
    private readonly HashSet<Guid>
        _remoteTombstoneConflictTransferIds = [];
    private readonly HashSet<Guid>
        _remoteTombstoneConflictShadowTransferIds = [];
    private readonly Dictionary<Guid, string>
        _remoteTombstoneConflictSourceOfficeCodes = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSavedTransfer))]
    [NotifyPropertyChangedFor(nameof(CanReloadLatestTransfer))]
    [NotifyPropertyChangedFor(
        nameof(HasRemoteTombstoneConflictDraft))]
    [NotifyPropertyChangedFor(
        nameof(CanRecoverRemoteDeletedTransferAsNew))]
    [NotifyPropertyChangedFor(nameof(CanDeleteTransfer))]
    [NotifyPropertyChangedFor(nameof(CanEditReceiptDraft))]
    [NotifyPropertyChangedFor(nameof(CanEditTransferDraft))]
    [NotifyPropertyChangedFor(nameof(CanUpdateReceiptLine))]
    [NotifyPropertyChangedFor(nameof(CanUpdateLine))]
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
    [NotifyPropertyChangedFor(nameof(CanReloadLatestTransfer))]
    [NotifyPropertyChangedFor(
        nameof(CanRecoverRemoteDeletedTransferAsNew))]
    [NotifyPropertyChangedFor(nameof(CanEditSourceDraft))]
    [NotifyPropertyChangedFor(nameof(CanSaveTransfer))]
    [NotifyPropertyChangedFor(nameof(CanEditTransferDraft))]
    [NotifyPropertyChangedFor(nameof(CanAddLine))]
    [NotifyPropertyChangedFor(nameof(CanUpdateSourceLine))]
    [NotifyPropertyChangedFor(nameof(CanUpdateLine))]
    [NotifyPropertyChangedFor(nameof(CanDeleteLine))]
    private string _fromWarehouseCode = DomainConstants.WarehouseUsenetMain;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TransferRouteText))]
    [NotifyPropertyChangedFor(nameof(CanDeleteTransfer))]
    [NotifyPropertyChangedFor(nameof(CanEditReceiptDraft))]
    [NotifyPropertyChangedFor(nameof(CanEditTransferDraft))]
    [NotifyPropertyChangedFor(nameof(CanUpdateReceiptLine))]
    [NotifyPropertyChangedFor(nameof(CanUpdateLine))]
    [NotifyPropertyChangedFor(nameof(CanConfirmReceipt))]
    [NotifyPropertyChangedFor(nameof(CanRejectTransfer))]
    private string _toWarehouseCode = DomainConstants.WarehouseYeonsuMain;

    [ObservableProperty] private string _memo = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(CanRecoverRemoteDeletedTransferAsNew))]
    [NotifyPropertyChangedFor(nameof(CanEditSourceDraft))]
    [NotifyPropertyChangedFor(nameof(CanEditReceiptDraft))]
    [NotifyPropertyChangedFor(nameof(CanEditTransferDraft))]
    [NotifyPropertyChangedFor(nameof(CanUpdateSourceLine))]
    [NotifyPropertyChangedFor(nameof(CanUpdateReceiptLine))]
    [NotifyPropertyChangedFor(nameof(CanUpdateLine))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveTransfer))]
    [NotifyPropertyChangedFor(nameof(CanEditSourceDraft))]
    [NotifyPropertyChangedFor(nameof(CanEditReceiptDraft))]
    [NotifyPropertyChangedFor(nameof(CanEditTransferDraft))]
    [NotifyPropertyChangedFor(nameof(CanAddLine))]
    [NotifyPropertyChangedFor(nameof(CanUpdateSourceLine))]
    [NotifyPropertyChangedFor(nameof(CanUpdateReceiptLine))]
    [NotifyPropertyChangedFor(nameof(CanUpdateLine))]
    [NotifyPropertyChangedFor(nameof(CanDeleteLine))]
    [NotifyPropertyChangedFor(nameof(CanDeleteTransfer))]
    [NotifyPropertyChangedFor(nameof(CanConfirmReceipt))]
    [NotifyPropertyChangedFor(nameof(CanRejectTransfer))]
    private bool _isExternalTransferUnavailable;

    [ObservableProperty]
    private bool _hasExternalTransferConflict;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDeleteTransfer))]
    [NotifyPropertyChangedFor(nameof(CanEditSourceDraft))]
    [NotifyPropertyChangedFor(nameof(CanEditReceiptDraft))]
    [NotifyPropertyChangedFor(nameof(CanEditTransferDraft))]
    [NotifyPropertyChangedFor(nameof(CanSaveTransfer))]
    [NotifyPropertyChangedFor(nameof(CanAddLine))]
    [NotifyPropertyChangedFor(nameof(CanUpdateSourceLine))]
    [NotifyPropertyChangedFor(nameof(CanUpdateReceiptLine))]
    [NotifyPropertyChangedFor(nameof(CanUpdateLine))]
    [NotifyPropertyChangedFor(nameof(CanDeleteLine))]
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
    [NotifyPropertyChangedFor(nameof(CanUpdateSourceLine))]
    [NotifyPropertyChangedFor(nameof(CanUpdateReceiptLine))]
    [NotifyPropertyChangedFor(nameof(CanUpdateLine))]
    [NotifyPropertyChangedFor(nameof(CanDeleteLine))]
    private InventoryTransferLineEditModel? _selectedLine;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddLine))]
    [NotifyPropertyChangedFor(nameof(CanUpdateSourceLine))]
    [NotifyPropertyChangedFor(nameof(CanUpdateLine))]
    [NotifyPropertyChangedFor(nameof(AvailableStockText))]
    private LocalItem? _selectedInputItem;

    [ObservableProperty] private string _inputItemName = string.Empty;
    [ObservableProperty] private string _inputSpec = string.Empty;
    [ObservableProperty] private string _inputUnit = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddLine))]
    [NotifyPropertyChangedFor(nameof(CanUpdateSourceLine))]
    [NotifyPropertyChangedFor(nameof(CanUpdateLine))]
    private decimal _inputQty = 1m;

    [ObservableProperty] private string _inputRemark = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAddLine))]
    [NotifyPropertyChangedFor(nameof(CanUpdateReceiptLine))]
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
    internal long TransferRevision
    {
        get => _transferRevision;
        set => SetTransferRevision(value);
    }
    public bool IsAdmin => _session.HasAdministrativePrivileges;
    public bool HasSavedTransfer => TransferId != Guid.Empty;
    public bool IsInteractionEnabled => !IsBusy;
    public bool CanEditSourceDraft =>
        !IsBusy &&
        !IsExternalTransferUnavailable &&
        !IsFinalTransferStatus &&
        CanCurrentUserEditSource(FromWarehouseCode);
    public bool CanEditReceiptDraft =>
        !IsBusy &&
        !IsExternalTransferUnavailable &&
        HasSavedTransfer &&
        !IsFinalTransferStatus &&
        CanCurrentUserReceive &&
        !RequiresSourceSyncBeforeReceipt;
    public bool CanEditTransferDraft => CanEditSourceDraft || CanEditReceiptDraft;
    public bool CanSaveTransfer => CanEditSourceDraft;
    public bool CanReloadLatestTransfer =>
        !IsBusy &&
        HasSavedTransfer &&
        (!HasRemoteTombstoneConflictDraft ||
         CanCurrentUserResolveRemoteTombstoneConflict);
    public bool HasRemoteTombstoneConflictDraft =>
        HasSavedTransfer &&
        _remoteTombstoneConflictTransferIds.Contains(TransferId);
    public bool CanRecoverRemoteDeletedTransferAsNew =>
        !IsBusy &&
        HasRemoteTombstoneConflictDraft &&
        CanCurrentUserResolveRemoteTombstoneConflict;
    public bool CanDeleteTransfer => !IsBusy && !IsExternalTransferUnavailable && HasSavedTransfer && CanCurrentUserDelete;
    public bool CanConfirmReceipt => CanEditReceiptDraft;
    public bool CanRejectTransfer => CanEditReceiptDraft;
    public bool CanAddLine => CanEditSourceDraft && SelectedInputItem is not null && QuantityNumericContract.IsPositiveQuantity18Scale2(InputQty);
    public bool CanUpdateSourceLine => CanEditSourceDraft && SelectedLine is not null && SelectedInputItem is not null && QuantityNumericContract.IsPositiveQuantity18Scale2(InputQty);
    public bool CanUpdateReceiptLine => CanEditReceiptDraft && SelectedLine is not null && QuantityNumericContract.IsValidReceivedQuantity18Scale2(InputReceivedQty, SelectedLine.Quantity);
    public bool CanUpdateLine => CanUpdateSourceLine || CanUpdateReceiptLine;
    public bool CanDeleteLine => CanEditSourceDraft && SelectedLine is not null;
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
    private bool CanCurrentUserEditDeliveries =>
        _session.HasAdministrativePrivileges || _session.HasPermission(AppPermissionNames.DeliveryEdit);

    private bool CanCurrentUserEditSource(string? warehouseCode)
    {
        if (!CanCurrentUserEditDeliveries)
            return false;

        if (_session.HasAdministrativePrivileges)
            return true;

        var writableOfficeCodes =
            _local.GetWritableOfficeCodesForSession(_session);
        var sourceOfficeCode =
            ResolveOfficeCodeFromWarehouseCode(warehouseCode);
        return writableOfficeCodes.Contains(
            sourceOfficeCode,
            StringComparer.OrdinalIgnoreCase);
    }
    private bool CanCurrentUserResolveRemoteTombstoneConflict
    {
        get
        {
            if (!CanCurrentUserEditDeliveries)
                return false;

            if (_session.HasAdministrativePrivileges)
                return true;

            var writableOfficeCodes =
                _local.GetWritableOfficeCodesForSession(_session);
            var sourceOfficeCode =
                HasRemoteTombstoneConflictDraft &&
                _remoteTombstoneConflictSourceOfficeCodes.TryGetValue(
                    TransferId,
                    out var conflictSourceOfficeCode)
                    ? conflictSourceOfficeCode
                    : ResolveOfficeCodeFromWarehouseCode(
                        FromWarehouseCode);
            return writableOfficeCodes.Contains(
                sourceOfficeCode,
                StringComparer.OrdinalIgnoreCase);
        }
    }
    public bool CanCurrentUserReceive
    {
        get
        {
            if (!CanCurrentUserEditDeliveries)
                return false;

            if (_session.HasAdministrativePrivileges)
                return true;

            var destinationOfficeCode = ResolveOfficeCodeFromWarehouseCode(ToWarehouseCode);
            var userOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(_session.OfficeCode, DomainConstants.OfficeUsenet);
            return string.Equals(destinationOfficeCode, userOfficeCode, StringComparison.OrdinalIgnoreCase);
        }
    }

    private bool RequiresSourceSyncBeforeReceipt =>
        HasSavedTransfer &&
        _transferRevision <= 0 &&
        !IsFinalTransferStatus &&
        CanCurrentUserReceive &&
        !CanCurrentUserEditSource(FromWarehouseCode);
    public bool CanCurrentUserDelete
    {
        get
        {
            if (!CanCurrentUserEditDeliveries)
                return false;

            if (_session.HasAdministrativePrivileges)
                return true;

            var writableOfficeCodes = _local.GetWritableOfficeCodesForSession(_session);
            var sourceOfficeCode = ResolveOfficeCodeFromWarehouseCode(FromWarehouseCode);
            var destinationOfficeCode = ResolveOfficeCodeFromWarehouseCode(ToWarehouseCode);

            if (IsFinalTransferStatus)
                return writableOfficeCodes.Contains(sourceOfficeCode, StringComparer.OrdinalIgnoreCase) &&
                       writableOfficeCodes.Contains(destinationOfficeCode, StringComparer.OrdinalIgnoreCase);

            return writableOfficeCodes.Contains(sourceOfficeCode, StringComparer.OrdinalIgnoreCase);
        }
    }

    public InventoryTransferViewModel(LocalStateService local, SessionState session)
    {
        _local = local;
        _session = session;
        _externalStateRefresh = new UiAsyncRefreshCoalescer(
            HandleInventoryStateChangedAsync,
            task => UiTaskHelper.Forget(
                task,
                "TRANSFER",
                "열린 재고이동 화면 동기화",
                ex => StatusMessage = $"다른 PC의 재고이동 변경 내용을 다시 불러오지 못했습니다. {ex.Message}"));
        _local.InventoryStateChanged += HandleInventoryStateChanged;
        ResetEditBaseline();
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        _local.InventoryStateChanged -= HandleInventoryStateChanged;
        _externalStateRefresh.Dispose();
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
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
            return;

        if (!dispatcher.CheckAccess())
        {
            _ = dispatcher.InvokeAsync(QueueInventoryStateRefreshOnDispatcher);
            return;
        }

        QueueInventoryStateRefreshOnDispatcher();
    }

    private void QueueInventoryStateRefreshOnDispatcher()
    {
        if (_isDisposed)
            return;

        if (IsBusy)
        {
            _deferExternalRefreshUntilIdle = true;
            return;
        }

        _externalStateRefresh.Request();
    }

    internal async Task HandleInventoryStateChangedAsync()
    {
        if (_isDisposed)
            return;

        if (IsBusy)
        {
            _deferExternalRefreshUntilIdle = true;
            return;
        }

        IsBusy = true;
        try
        {
            var editorTransferId = TransferId;
            var editorOpenVersion = Volatile.Read(ref _openTransferVersion);
            var selectedTransferId = SelectedTransfer?.Id;

            await LoadLookupsAsync();
            if (_isDisposed)
                return;

            await RefreshWarehouseStocksAsync();
            if (_isDisposed)
                return;

            var refreshedTransfers =
                await LoadVisibleTransfersIncludingRemoteTombstoneDraftsAsync();
            if (_isDisposed)
                return;

            var sameEditor =
                TransferId == editorTransferId &&
                editorOpenVersion == Volatile.Read(ref _openTransferVersion);
            var selectedIdAfterAwait = SelectedTransfer?.Id ?? selectedTransferId;
            ReplaceTransfers(refreshedTransfers, selectedIdAfterAwait);

            if (!sameEditor)
                return;

            var currentSnapshot = CaptureEditSnapshot();
            var hasPendingChanges = HasPendingChanges;
            var latestTransfer = editorTransferId == Guid.Empty
                ? null
                : refreshedTransfers.FirstOrDefault(transfer => transfer.Id == editorTransferId);

            if (editorTransferId == Guid.Empty)
            {
                RebindSelectedInputItem(preserveDraft: hasPendingChanges);
                return;
            }

            if (_remoteTombstoneConflictShadowTransferIds.Contains(
                    editorTransferId))
            {
                IsExternalTransferUnavailable = true;
                HasExternalTransferConflict = true;
                ApplySnapshot(currentSnapshot, resetBaseline: false);
                RebindSelectedInputItem(preserveDraft: true);
                StatusMessage =
                    "서버에서 삭제된 재고이동 문서입니다. 로컬 초안은 안전하게 별도 보관했고 재고에서는 제외했습니다. 저장·삭제·수령·반려는 차단됩니다. ‘초안을 새 문서로 복구’하거나 ‘최신본 불러오기’로 초안을 폐기하세요.";
                return;
            }

            if (latestTransfer is null)
            {
                if (!hasPendingChanges)
                {
                    RemoveUnavailableTransfer(editorTransferId);
                    StatusMessage = "다른 PC에서 이 재고이동 문서가 삭제되었거나 현재 조회 권한에서 제외되어 새 문서 화면으로 전환했습니다.";
                    return;
                }

                IsExternalTransferUnavailable = true;
                HasExternalTransferConflict = true;
                ApplySnapshot(currentSnapshot, resetBaseline: false);
                SetSelectedTransfer(null);
                StatusMessage = "다른 PC에서 이 재고이동 문서가 삭제되었거나 현재 조회 권한에서 제외되었습니다. 미저장 내용은 보존했지만 문서가 다시 생기지 않도록 저장·삭제·수령·반려를 차단했습니다. ‘최신본 불러오기’로 임시 내용을 폐기하세요.";
                return;
            }

            var remoteChanged =
                latestTransfer.Revision != currentSnapshot.Revision ||
                latestTransfer.UpdatedAtUtc != _transferUpdatedAtUtc;
            var pendingDraftMatchesLatest =
                hasPendingChanges &&
                string.Equals(
                    BuildEditStateSignature(currentSnapshot),
                    BuildEditStateSignature(
                        CreateSnapshotFromTransfer(latestTransfer)),
                    StringComparison.Ordinal);

            if (hasPendingChanges && !pendingDraftMatchesLatest)
            {
                ApplySnapshot(currentSnapshot, resetBaseline: false);
                RebindSelectedInputItem(preserveDraft: true);

                if (remoteChanged)
                {
                    IsExternalTransferUnavailable = false;
                    HasExternalTransferConflict = true;
                    StatusMessage = "다른 PC에서 이 재고이동 문서의 최신 내용이 먼저 반영되었습니다. 미저장 내용과 기존 기준 버전은 그대로 보존했습니다. 저장하면 동시 수정 충돌로 보호되며, ‘최신본 불러오기’를 누르면 임시 내용을 폐기하고 최신 문서를 확인할 수 있습니다.";
                }

                return;
            }

            if (!remoteChanged && !hasPendingChanges)
            {
                RebindSelectedInputItem(preserveDraft: false);
                return;
            }

            ClearExternalTransferConflict();
            RunWithSuppressedTransferSelectionChanged(() =>
            {
                SelectedTransfer = Transfers.FirstOrDefault(
                    transfer => transfer.Id == latestTransfer.Id);
                ApplyTransferToEditor(latestTransfer);
            });
        }
        finally
        {
            if (!_isDisposed)
                IsBusy = false;
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

        var transfer =
            await _local.GetInventoryTransferAsync(
                transferId,
                _session);
        if (version != Volatile.Read(
                ref _openTransferVersion))
        {
            return;
        }

        var tombstoneConflict =
            await _local.GetInventoryTransferTombstoneConflictDraftAsync(
                transferId,
                _session);
        if (version != Volatile.Read(
                ref _openTransferVersion))
        {
            return;
        }

        if (tombstoneConflict is not null)
        {
            AddRemoteTombstoneConflictTransferId(
                transferId,
                ResolveOfficeCodeFromWarehouseCode(
                    tombstoneConflict.LocalDraft
                        .FromWarehouseCode));
            if (transfer is null)
            {
                transfer = tombstoneConflict.LocalDraft;
                _remoteTombstoneConflictShadowTransferIds.Add(
                    transferId);
            }
            else
            {
                _remoteTombstoneConflictShadowTransferIds.Remove(
                    transferId);
            }
        }
        else
        {
            RemoveRemoteTombstoneConflictTransferId(transferId);
            _remoteTombstoneConflictShadowTransferIds.Remove(
                transferId);
        }

        if (transfer is null)
        {
            RemoveUnavailableTransfer(transferId);
            StatusMessage = "선택한 재고이동 문서를 찾을 수 없습니다.";
            return;
        }

        RunWithSuppressedTransferSelectionChanged(() =>
        {
            var existing = Transfers.FirstOrDefault(
                current => current.Id == transfer.Id);
            if (existing is null)
            {
                Transfers.Insert(0, transfer);
            }
            else
            {
                Transfers[Transfers.IndexOf(existing)] = transfer;
            }

            SelectedTransfer = transfer;
            ApplyTransferToEditor(transfer);
        });
    }

    public async Task DeleteCurrentTransferAsync()
    {
        if (IsBusy)
        {
            StatusMessage = "재고이동 화면을 새로고침하는 중입니다. 잠시 후 다시 시도하세요.";
            return;
        }

        if (IsExternalTransferUnavailable)
        {
            StatusMessage = "다른 PC에서 삭제되었거나 조회 범위에서 제외된 문서는 삭제할 수 없습니다. ‘최신본 불러오기’로 임시 내용을 폐기하세요.";
            return;
        }

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
        if (IsBusy)
        {
            StatusMessage = "재고이동 화면을 새로고침하는 중입니다. 잠시 후 다시 시도하세요.";
            return;
        }

        if (!await TryAutoSaveCurrentEditAsync(refreshAfterSave: true))
            return;

        StartNewTransfer();
    }

    [RelayCommand]
    private void AddLine()
    {
        if (!CanEditSourceDraft)
        {
            StatusMessage = "출발지 담당자 또는 관리자만 이동 요청 품목을 추가할 수 있습니다.";
            return;
        }

        if (SelectedInputItem is null)
        {
            StatusMessage = "목록에서 이동 품목을 선택하세요.";
            return;
        }

        if (!QuantityNumericContract.IsPositiveQuantity18Scale2(InputQty))
        {
            StatusMessage = "요청수량은 0보다 크고 허용 범위 안에서 소수 둘째 자리까지 입력해야 합니다.";
            return;
        }

        var line = new InventoryTransferLineEditModel
        {
            ItemId = SelectedInputItem.Id,
            ItemName = InputItemName.Trim(),
            Specification = InputSpec.Trim(),
            Unit = InputUnit.Trim(),
            Quantity = InputQty,
            ReceivedQuantity = InputQty,
            Remark = InputRemark.Trim(),
            ReceiptRemark = string.Empty
        };

        Lines.Add(line);
        SelectedLine = line;
        StatusMessage = $"{Lines.Count:N0}개 품목을 이동 목록에 담았습니다.";
        ResetLineEditor(clearSelection: true);
    }

    [RelayCommand]
    private void UpdateSourceLine()
    {
        if (!CanEditSourceDraft)
        {
            StatusMessage = "출발지 담당자 또는 관리자만 이동 요청 품목을 수정할 수 있습니다.";
            return;
        }

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

        if (!QuantityNumericContract.IsPositiveQuantity18Scale2(InputQty))
        {
            StatusMessage = "요청수량은 0보다 크고 허용 범위 안에서 소수 둘째 자리까지 입력해야 합니다.";
            return;
        }

        SelectedLine.ItemId = SelectedInputItem.Id;
        SelectedLine.ItemName = InputItemName.Trim();
        SelectedLine.Specification = InputSpec.Trim();
        SelectedLine.Unit = InputUnit.Trim();
        SelectedLine.Quantity = InputQty;
        SelectedLine.ReceivedQuantity = InputQty;
        SelectedLine.Remark = InputRemark.Trim();
        SelectedLine.ReceiptRemark = string.Empty;
        InputReceivedQty = InputQty;
        InputReceiptRemark = string.Empty;
        StatusMessage = "선택한 이동 요청 품목을 수정했습니다.";
    }

    [RelayCommand]
    private void UpdateReceiptLine()
    {
        if (RequiresSourceSyncBeforeReceipt)
        {
            StatusMessage = "출발지에서 재고이동 요청을 먼저 서버에 동기화한 뒤 수령값을 입력하세요.";
            return;
        }

        if (!CanEditReceiptDraft)
        {
            StatusMessage = "도착지 담당자 또는 관리자만 수령수량과 수령메모를 수정할 수 있습니다.";
            return;
        }

        if (SelectedLine is null)
        {
            StatusMessage = "수령값을 수정할 이동 품목을 선택하세요.";
            return;
        }

        if (!QuantityNumericContract.IsValidReceivedQuantity18Scale2(
                InputReceivedQty,
                SelectedLine.Quantity))
        {
            StatusMessage = "수령수량은 0 이상, 요청수량 이하이고 허용 범위 안에서 소수 둘째 자리까지 입력해야 합니다.";
            return;
        }

        SelectedLine.ReceivedQuantity = InputReceivedQty;
        SelectedLine.ReceiptRemark = InputReceiptRemark.Trim();
        StatusMessage = "선택한 품목의 수령값을 적용했습니다.";
    }

    [RelayCommand]
    private void DeleteLine()
    {
        if (!CanEditSourceDraft)
        {
            StatusMessage = "출발지 담당자 또는 관리자만 이동 요청 품목을 삭제할 수 있습니다.";
            return;
        }

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
        if (!CanSaveTransfer)
        {
            StatusMessage = IsExternalTransferUnavailable
                ? "다른 PC에서 삭제되었거나 조회 범위에서 제외된 문서는 저장할 수 없습니다. ‘최신본 불러오기’로 임시 내용을 폐기하세요."
                : IsFinalTransferStatus
                    ? "수령확정 또는 반려된 문서는 수정할 수 없습니다."
                    : "출발지 담당자 또는 관리자만 재고이동 요청 문서를 저장할 수 있습니다.";
            return;
        }

        var snapshot = CaptureEditSnapshot();
        await SaveSnapshotAsync(
            snapshot,
            requestedSelectionId: snapshot.TransferId == Guid.Empty ? null : snapshot.TransferId,
            refreshAfterSave: true,
            successMessage: snapshot.TransferId == Guid.Empty ? "재고이동을 저장했습니다." : "재고이동을 수정했습니다.",
            showConflictDialog: true);
    }

    [RelayCommand]
    private async Task ReloadLatestTransfer()
    {
        if (!CanReloadLatestTransfer)
        {
            StatusMessage =
                HasRemoteTombstoneConflictDraft &&
                !CanCurrentUserResolveRemoteTombstoneConflict
                    ? "출발지 담당자 또는 관리자만 보관된 원격삭제 충돌 초안을 폐기할 수 있습니다."
                    : HasSavedTransfer
                        ? "재고이동 화면을 새로고침하는 중입니다. 잠시 후 다시 시도하세요."
                        : "최신본을 불러올 재고이동 문서를 먼저 선택하세요.";
            return;
        }

        if (HasRemoteTombstoneConflictDraft)
        {
            var pendingEditWarning = HasPendingChanges
                ? " 현재 화면의 저장하지 않은 편집 내용도 함께 폐기됩니다."
                : string.Empty;
            var confirmed = System.Windows.MessageBox.Show(
                "별도 보관된 로컬 초안과 해당 전송 대기 기록을 영구 폐기합니다." +
                pendingEditWarning +
                " 이후에는 복구할 수 없습니다. 서버 최신본만 유지하시겠습니까?",
                "보관 초안 폐기",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (confirmed != System.Windows.MessageBoxResult.Yes)
            {
                StatusMessage =
                    "보관 초안 폐기를 취소하여 로컬 초안과 전송 대기 기록을 그대로 유지했습니다.";
                return;
            }
        }
        else if (HasPendingChanges)
        {
            var confirmed = System.Windows.MessageBox.Show(
                "저장하지 않은 편집 내용은 폐기됩니다. 다른 PC에서 반영된 최신 재고이동 문서를 불러오시겠습니까?",
                "최신본 불러오기",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (confirmed != System.Windows.MessageBoxResult.Yes)
            {
                StatusMessage = "최신본 불러오기를 취소하여 미저장 내용을 그대로 유지했습니다.";
                return;
            }
        }

        await DiscardDraftAndReloadLatestTransferAsync();
    }

    internal async Task DiscardDraftAndReloadLatestTransferAsync()
    {
        var transferId = TransferId;
        if (transferId == Guid.Empty)
        {
            StatusMessage = "최신본을 불러올 재고이동 문서를 먼저 선택하세요.";
            return;
        }

        IsBusy = true;
        try
        {
            if (_remoteTombstoneConflictTransferIds.Contains(transferId))
            {
                var resolved =
                    await _local.ResolveInventoryTransferTombstoneConflictAsync(
                        transferId,
                        InventoryTransferTombstoneConflictPolicy
                            .DiscardedResolution,
                        _session);
                if (!resolved)
                {
                    StatusMessage =
                        "보관된 원격삭제 충돌 초안을 찾거나 폐기할 수 없습니다. 화면을 다시 열어 상태를 확인하세요.";
                    return;
                }

                RemoveRemoteTombstoneConflictTransferId(transferId);
                _remoteTombstoneConflictShadowTransferIds.Remove(
                    transferId);
            }

            await LoadLookupsAsync();
            if (_isDisposed)
                return;

            await RefreshWarehouseStocksAsync();
            if (_isDisposed)
                return;

            await RefreshTransfersAsync(transferId);
            if (_isDisposed)
                return;

            if (SelectedTransfer is null)
            {
                StartNewTransfer();
                StatusMessage = "다른 PC에서 삭제되었거나 현재 조회 권한에서 제외된 재고이동 문서의 임시 내용을 폐기하고 새 문서 화면으로 전환했습니다.";
                return;
            }

            await OpenTransferAsync(transferId);
            if (_isDisposed)
                return;

            ClearExternalTransferConflict();
            StatusMessage = $"재고이동 {TransferNumberDisplay} 최신 문서를 불러왔습니다.";
        }
        finally
        {
            if (!_isDisposed)
                IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RecoverRemoteDeletedTransferAsNew()
    {
        if (!CanRecoverRemoteDeletedTransferAsNew)
        {
            StatusMessage =
                HasRemoteTombstoneConflictDraft &&
                !CanCurrentUserResolveRemoteTombstoneConflict
                    ? "출발지 담당자 또는 관리자만 원격삭제 충돌 초안을 새 문서로 복구할 수 있습니다."
                    : "새 문서로 복구할 원격삭제 충돌 초안을 먼저 선택하세요.";
            return;
        }

        if (HasPendingChanges &&
            !IsExternalTransferUnavailable)
        {
            var confirmed = System.Windows.MessageBox.Show(
                "현재 서버 문서의 저장하지 않은 편집 내용은 폐기됩니다. 별도 보관된 로컬 초안을 새 문서로 복구하시겠습니까?",
                "보관 초안 복구",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (confirmed != System.Windows.MessageBoxResult.Yes)
            {
                StatusMessage =
                    "보관 초안 복구를 취소하여 현재 편집 내용을 그대로 유지했습니다.";
                return;
            }
        }

        var conflictTransferId = TransferId;
        await _autoSaveGate.WaitAsync();
        try
        {
            IsBusy = true;
            var result =
                await _local
                    .RecoverInventoryTransferTombstoneConflictAsNewAsync(
                        conflictTransferId,
                        _session);
            if (!result.Success)
            {
                StatusMessage =
                    $"새 문서 복구에 실패하여 원본 초안을 그대로 보관했습니다. {result.Message}";
                return;
            }

            RemoveRemoteTombstoneConflictTransferId(
                conflictTransferId);
            _remoteTombstoneConflictShadowTransferIds.Remove(
                conflictTransferId);
            await RefreshWarehouseStocksAsync();
            await RefreshTransfersAsync(result.EntityId);
            await OpenTransferAsync(result.EntityId);
            StatusMessage =
                $"원격에서 삭제된 초안을 새 재고이동 {TransferNumberDisplay} 문서로 복구해 저장했습니다.";
        }
        finally
        {
            _autoSaveGate.Release();
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ConfirmReceiptAsync()
    {
        if (IsExternalTransferUnavailable)
        {
            StatusMessage = "다른 PC에서 삭제되었거나 조회 범위에서 제외된 문서는 수령확정할 수 없습니다. ‘최신본 불러오기’로 임시 내용을 폐기하세요.";
            return;
        }

        if (RequiresSourceSyncBeforeReceipt)
        {
            StatusMessage = "출발지에서 재고이동 요청을 먼저 서버에 동기화한 뒤 수령확정하세요.";
            return;
        }

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

        var invalidReceiptLine = Lines.FirstOrDefault(line =>
            !QuantityNumericContract.IsValidReceivedQuantity18Scale2(
                line.ReceivedQuantity,
                line.Quantity));
        if (invalidReceiptLine is not null)
        {
            StatusMessage = $"{invalidReceiptLine.ItemName} 품목의 수령수량은 0 이상, 요청수량 이하이고 허용 범위 안에서 소수 둘째 자리까지 입력해야 합니다.";
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
        if (IsExternalTransferUnavailable)
        {
            StatusMessage = "다른 PC에서 삭제되었거나 조회 범위에서 제외된 문서는 반려할 수 없습니다. ‘최신본 불러오기’로 임시 내용을 폐기하세요.";
            return;
        }

        if (RequiresSourceSyncBeforeReceipt)
        {
            StatusMessage = "출발지에서 재고이동 요청을 먼저 서버에 동기화한 뒤 반려하세요.";
            return;
        }

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

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsInteractionEnabled));
        OnPropertyChanged(nameof(CanSaveTransfer));
        OnPropertyChanged(nameof(CanReloadLatestTransfer));
        OnPropertyChanged(nameof(CanAddLine));
        OnPropertyChanged(nameof(CanUpdateLine));
        OnPropertyChanged(nameof(CanDeleteLine));
        OnPropertyChanged(nameof(CanDeleteTransfer));
        OnPropertyChanged(nameof(CanConfirmReceipt));
        OnPropertyChanged(nameof(CanRejectTransfer));

        if (value || !_deferExternalRefreshUntilIdle || _isDisposed)
            return;

        _deferExternalRefreshUntilIdle = false;
        _externalStateRefresh.Request();
    }

    partial void OnSelectedTransferChanged(LocalInventoryTransfer? value)
    {
        if (_suppressTransferSelectionChanged || value is null)
            return;

        var version = Interlocked.Increment(ref _openTransferVersion);
        UiTaskHelper.Forget(
            () => OpenTransferAsync(value.Id, version),
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
            () => HandleSelectionAutoSaveAsync(snapshot, oldValue, newValue),
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
        var preservedFromWarehouseCode = FromWarehouseCode;
        var preservedToWarehouseCode = ToWarehouseCode;
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

        // A bound ComboBox clears SelectedValue while its ItemsSource is reset.
        // Keep the editor route stable across lookup refreshes so that the
        // temporary binding transition cannot become a false local draft.
        FromWarehouseCode = preservedFromWarehouseCode;
        ToWarehouseCode = preservedToWarehouseCode;
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
        var transfers =
            await LoadVisibleTransfersIncludingRemoteTombstoneDraftsAsync();
        ReplaceTransfers(transfers, selectedTransferId);
    }

    private async Task<List<LocalInventoryTransfer>>
        LoadVisibleTransfersIncludingRemoteTombstoneDraftsAsync()
    {
        var transfers = await _local.GetInventoryTransfersAsync(_session);
        var conflicts =
            await _local.GetInventoryTransferTombstoneConflictDraftsAsync(
                _session);

        var activeTransferIds = transfers
            .Select(transfer => transfer.Id)
            .ToHashSet();
        var shadowConflicts = conflicts
            .Where(conflict =>
                !activeTransferIds.Contains(conflict.TransferId))
            .ToList();
        ReplaceRemoteTombstoneConflictSourceOfficeCodes(
            conflicts);
        ReplaceRemoteTombstoneConflictTransferIds(
            conflicts.Select(conflict => conflict.TransferId));
        ReplaceRemoteTombstoneConflictShadowTransferIds(
            shadowConflicts.Select(conflict => conflict.TransferId));

        if (shadowConflicts.Count == 0)
            return transfers;

        return transfers
            .Concat(
                shadowConflicts.Select(
                    conflict => conflict.LocalDraft))
            .OrderByDescending(transfer => transfer.TransferDate)
            .ThenByDescending(transfer => transfer.UpdatedAtUtc)
            .ToList();
    }

    private void ReplaceTransfers(
        IReadOnlyCollection<LocalInventoryTransfer> transfers,
        Guid? selectedTransferId)
    {
        RunWithSuppressedTransferSelectionChanged(() =>
        {
            Transfers.Clear();
            foreach (var transfer in transfers)
                Transfers.Add(transfer);

            SelectedTransfer = selectedTransferId.HasValue &&
                               selectedTransferId.Value != Guid.Empty
                ? Transfers.FirstOrDefault(
                    transfer => transfer.Id == selectedTransferId.Value)
                : null;
        });
    }

    private void RebindSelectedInputItem(bool preserveDraft)
    {
        var selectedInputItemId = SelectedInputItem?.Id;
        if (!selectedInputItemId.HasValue)
            return;

        var refreshedItem = _allItems.FirstOrDefault(
            item => item.Id == selectedInputItemId.Value);
        if (refreshedItem is not null)
        {
            if (!preserveDraft)
                SelectedInputItem = refreshedItem;
            return;
        }

        if (!preserveDraft)
            InputAvailableStock = 0m;
    }

    private void ApplyTransferToEditor(LocalInventoryTransfer transfer)
    {
        ApplySnapshot(CreateSnapshotFromTransfer(transfer), resetBaseline: true);
        _transferUpdatedAtUtc = transfer.UpdatedAtUtc;
        if (_remoteTombstoneConflictShadowTransferIds.Contains(
                transfer.Id))
        {
            IsExternalTransferUnavailable = true;
            HasExternalTransferConflict = true;
            StatusMessage =
                CanCurrentUserResolveRemoteTombstoneConflict
                    ? "서버에서 삭제된 재고이동 문서의 로컬 초안입니다. 초안은 별도 보관되어 재고에는 반영되지 않으며, ‘초안을 새 문서로 복구’하거나 ‘최신본 불러오기’로 폐기할 수 있습니다."
                    : "서버에서 삭제된 재고이동 문서의 로컬 초안입니다. 초안은 별도 보관되어 재고에는 반영되지 않으며, 출발지 담당자 또는 관리자만 복구하거나 폐기할 수 있습니다.";
            return;
        }

        ClearExternalTransferConflict();
        StatusMessage = RequiresSourceSyncBeforeReceipt
            ? "출발지에서 이 재고이동 요청을 서버에 먼저 동기화해야 수령확정 또는 반려할 수 있습니다. 출발지 동기화 후 최신본을 다시 불러오세요."
            : _remoteTombstoneConflictTransferIds.Contains(
                transfer.Id)
            ? CanCurrentUserResolveRemoteTombstoneConflict
                ? $"재고이동 {TransferNumberDisplay} 서버 최신 문서를 불러왔습니다. 서버 복원 전에 충돌한 로컬 초안도 별도 보관 중이므로 복구하거나 폐기할 수 있습니다."
                : $"재고이동 {TransferNumberDisplay} 서버 최신 문서를 불러왔습니다. 서버 복원 전에 충돌한 로컬 초안은 별도 보관 중이며, 출발지 담당자 또는 관리자만 처리할 수 있습니다."
            : $"재고이동 {TransferNumberDisplay} 문서를 불러왔습니다.";
    }

    private void StartNewTransfer()
    {
        ClearExternalTransferConflict();
        ApplySnapshot(CreateNewTransferSnapshot(), resetBaseline: true);
        _transferUpdatedAtUtc = default;
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
        RunWithSuppressedTransferSelectionChanged(() =>
        {
            SelectedTransfer = transferId.HasValue &&
                               transferId.Value != Guid.Empty
                ? Transfers.FirstOrDefault(
                    transfer => transfer.Id == transferId.Value)
                : null;
        });
    }

    private void RemoveUnavailableTransfer(Guid transferId)
    {
        var resetEditor =
            TransferId == transferId ||
            SelectedTransfer?.Id == transferId;

        RunWithSuppressedTransferSelectionChanged(() =>
        {
            var staleTransfer = Transfers.FirstOrDefault(
                transfer => transfer.Id == transferId);
            if (staleTransfer is not null)
                Transfers.Remove(staleTransfer);

            if (SelectedTransfer?.Id == transferId)
                SelectedTransfer = null;
        });

        if (resetEditor)
            StartNewTransfer();
    }

    private void ClearExternalTransferConflict()
    {
        IsExternalTransferUnavailable = false;
        HasExternalTransferConflict = false;
    }

    private void ReplaceRemoteTombstoneConflictTransferIds(
        IEnumerable<Guid> transferIds)
    {
        _remoteTombstoneConflictTransferIds.Clear();
        foreach (var transferId in transferIds.Where(id => id != Guid.Empty))
            _remoteTombstoneConflictTransferIds.Add(transferId);

        OnPropertyChanged(
            nameof(HasRemoteTombstoneConflictDraft));
        OnPropertyChanged(
            nameof(CanRecoverRemoteDeletedTransferAsNew));
        OnPropertyChanged(
            nameof(CanReloadLatestTransfer));
    }

    private void ReplaceRemoteTombstoneConflictSourceOfficeCodes(
        IEnumerable<InventoryTransferTombstoneConflictDraft>
            conflicts)
    {
        _remoteTombstoneConflictSourceOfficeCodes.Clear();
        foreach (var conflict in conflicts)
        {
            _remoteTombstoneConflictSourceOfficeCodes[
                conflict.TransferId] =
                ResolveOfficeCodeFromWarehouseCode(
                    conflict.LocalDraft.FromWarehouseCode);
        }
    }

    private void ReplaceRemoteTombstoneConflictShadowTransferIds(
        IEnumerable<Guid> transferIds)
    {
        _remoteTombstoneConflictShadowTransferIds.Clear();
        foreach (var transferId in transferIds.Where(
                     id => id != Guid.Empty))
        {
            _remoteTombstoneConflictShadowTransferIds.Add(transferId);
        }
    }

    private void AddRemoteTombstoneConflictTransferId(
        Guid transferId,
        string sourceOfficeCode)
    {
        if (transferId == Guid.Empty)
            return;

        var normalizedSourceOfficeCode =
            OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(
                sourceOfficeCode,
                DomainConstants.OfficeUsenet);
        var sourceScopeChanged =
            !_remoteTombstoneConflictSourceOfficeCodes.TryGetValue(
                transferId,
                out var previousSourceOfficeCode) ||
            !string.Equals(
                previousSourceOfficeCode,
                normalizedSourceOfficeCode,
                StringComparison.OrdinalIgnoreCase);
        _remoteTombstoneConflictSourceOfficeCodes[transferId] =
            normalizedSourceOfficeCode;
        var added =
            _remoteTombstoneConflictTransferIds.Add(transferId);
        if (!added && !sourceScopeChanged)
            return;

        OnPropertyChanged(
            nameof(HasRemoteTombstoneConflictDraft));
        OnPropertyChanged(
            nameof(CanRecoverRemoteDeletedTransferAsNew));
        OnPropertyChanged(
            nameof(CanReloadLatestTransfer));
    }

    private void RemoveRemoteTombstoneConflictTransferId(Guid transferId)
    {
        _remoteTombstoneConflictSourceOfficeCodes.Remove(
            transferId);
        if (!_remoteTombstoneConflictTransferIds.Remove(transferId))
            return;

        OnPropertyChanged(
            nameof(HasRemoteTombstoneConflictDraft));
        OnPropertyChanged(
            nameof(CanRecoverRemoteDeletedTransferAsNew));
        OnPropertyChanged(
            nameof(CanReloadLatestTransfer));
    }

    private void RunWithSuppressedTransferSelectionChanged(Action action)
    {
        var wasSuppressed = _suppressTransferSelectionChanged;
        _suppressTransferSelectionChanged = true;
        try
        {
            action();
        }
        finally
        {
            _suppressTransferSelectionChanged = wasSuppressed;
        }
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
        if (IsBusy)
        {
            StatusMessage = "재고이동 화면을 새로고침하거나 다른 작업을 처리하는 중입니다. 잠시 후 다시 시도하세요.";
            return false;
        }

        if (IsExternalTransferUnavailable &&
            snapshot.TransferId != Guid.Empty &&
            snapshot.TransferId == TransferId)
        {
            StatusMessage = "다른 PC에서 삭제되었거나 조회 범위에서 제외된 문서는 저장하거나 자동저장할 수 없습니다. ‘최신본 불러오기’로 임시 내용을 폐기하세요.";
            return false;
        }

        if (IsFinalTransferStatusText(snapshot.TransferStatus))
        {
            StatusMessage = "수령확정 또는 반려된 문서는 저장하거나 자동저장할 수 없습니다.";
            return false;
        }

        if (!CanCurrentUserEditSource(snapshot.FromWarehouseCode))
        {
            StatusMessage = CanEditReceiptDraft
                ? "도착지 수령 입력은 ‘수령확정’ 또는 ‘반려’를 실행할 때만 반영됩니다. 출발지 문서로 자동저장하지 않았습니다."
                : "출발지 담당자 또는 관리자만 재고이동 요청 문서를 저장하거나 자동저장할 수 있습니다.";
            return false;
        }

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

        var invalidQuantityLine = materializedLines.FirstOrDefault(line =>
            !QuantityNumericContract.IsPositiveQuantity18Scale2(line.Quantity));
        if (invalidQuantityLine is not null)
        {
            validationMessage = "요청수량은 0보다 크고 허용 범위 안에서 소수 둘째 자리까지 입력해야 합니다.";
            return false;
        }

        var validLines = materializedLines
            .Where(line => line.ItemId.HasValue
                           && !string.IsNullOrWhiteSpace(line.ItemName)
                           && QuantityNumericContract.IsPositiveQuantity18Scale2(line.Quantity))
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
            InventoryTransferStatusNormalizer.Normalize(
                transfer.TransferStatus,
                transfer.ReceivedByUsername,
                transfer.ReceivedAtUtc,
                transfer.RejectedByUsername,
                transfer.RejectedAtUtc),
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

    private static InventoryTransferEditSnapshot
        CreateRecoveredAsNewSnapshot(
            InventoryTransferEditSnapshot snapshot)
    {
        Guid? selectedLineId = null;
        var lines = snapshot.Lines
            .Select(line =>
            {
                var newLineId = Guid.NewGuid();
                if (snapshot.SelectedLineId == line.Id)
                    selectedLineId = newLineId;
                return line with { Id = newLineId };
            })
            .ToList();

        return snapshot with
        {
            TransferId = Guid.Empty,
            Revision = 0,
            TransferNumber = string.Empty,
            TransferStatus = "수령대기",
            ReceiveMemo = string.Empty,
            RejectReason = string.Empty,
            Lines = lines,
            SelectedLineId = selectedLineId
        };
    }

    private void ApplySnapshot(InventoryTransferEditSnapshot snapshot, bool resetBaseline)
    {
        _suppressLineSelectionChanged = true;
        try
        {
            TransferId = snapshot.TransferId;
            TransferRevision = snapshot.Revision;
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

    private void SetTransferRevision(long revision)
    {
        if (_transferRevision == revision)
            return;

        _transferRevision = revision;
        OnPropertyChanged(nameof(CanEditReceiptDraft));
        OnPropertyChanged(nameof(CanEditTransferDraft));
        OnPropertyChanged(nameof(CanUpdateReceiptLine));
        OnPropertyChanged(nameof(CanUpdateLine));
        OnPropertyChanged(nameof(CanConfirmReceipt));
        OnPropertyChanged(nameof(CanRejectTransfer));
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
                .Append(':').Append(FormatEditStateDecimal(line.Quantity))
                .Append(':').Append(FormatEditStateDecimal(line.ReceivedQuantity))
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
                .Append(':').Append(FormatEditStateDecimal(snapshot.InputQty))
                .Append(':').Append(FormatEditStateDecimal(snapshot.InputReceivedQty))
                .Append(':').Append(snapshot.InputRemark ?? string.Empty)
                .Append(':').Append(snapshot.InputReceiptRemark ?? string.Empty);
        }

        return builder.ToString();
    }

    private static string FormatEditStateDecimal(decimal value)
        => value.ToString(
            "G29",
            System.Globalization.CultureInfo.InvariantCulture);

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

        if (!QuantityNumericContract.IsPositiveQuantity18Scale2(draftLine.Quantity))
        {
            validationMessage = "요청수량은 0보다 크고 허용 범위 안에서 소수 둘째 자리까지 입력해야 합니다.";
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
