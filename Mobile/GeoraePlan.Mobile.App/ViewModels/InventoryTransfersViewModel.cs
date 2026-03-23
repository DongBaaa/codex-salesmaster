using System.Collections.ObjectModel;
using GeoraePlan.Mobile.App.Services;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.ViewModels;

public sealed class InventoryTransfersViewModel : ObservableObject
{
    private readonly JsonSyncStateStore _syncStateStore;
    private readonly SyncCoordinator _syncCoordinator;

    private string _searchText = string.Empty;
    private string _statusMessage = "재고이동 서버 동기화 데이터를 불러올 준비가 되었습니다.";
    private bool _isBusy;
    private DateTime? _lastRefreshUtc;
    private InventoryTransferDto? _selectedTransfer;

    public InventoryTransfersViewModel(JsonSyncStateStore syncStateStore, SyncCoordinator syncCoordinator)
    {
        _syncStateStore = syncStateStore;
        _syncCoordinator = syncCoordinator;
        RefreshCommand = new AsyncCommand(RefreshAsync);
        SyncNowCommand = new AsyncCommand(SyncNowAsync);
    }

    public ObservableCollection<InventoryTransferDto> Transfers { get; } = new();
    public ObservableCollection<InventoryTransferLineDto> SelectedTransferLines { get; } = new();

    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand SyncNowCommand { get; }

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

    public InventoryTransferDto? SelectedTransfer
    {
        get => _selectedTransfer;
        private set
        {
            if (!SetProperty(ref _selectedTransfer, value))
                return;

            OnPropertyChanged(nameof(HasSelectedTransfer));
            OnPropertyChanged(nameof(SelectedTransferTitle));
            OnPropertyChanged(nameof(SelectedTransferRoute));
            OnPropertyChanged(nameof(SelectedTransferMeta));
            OnPropertyChanged(nameof(SelectedTransferMemo));
            OnPropertyChanged(nameof(SelectedTransferRequestText));
            OnPropertyChanged(nameof(SelectedTransferReceiveText));
            OnPropertyChanged(nameof(SelectedTransferRejectText));
        }
    }

    public bool HasSelectedTransfer => SelectedTransfer is not null;

    public string SummaryText => Transfers.Count == 0
        ? "동기화된 재고이동 데이터가 없습니다."
        : $"서버 동기화 기준 재고이동 {Transfers.Count:N0}건";

    public string SelectedTransferTitle => SelectedTransfer is null
        ? "재고이동 상세"
        : string.IsNullOrWhiteSpace(SelectedTransfer.TransferNumber)
            ? "이동번호 없음"
            : SelectedTransfer.TransferNumber;

    public string SelectedTransferRoute => SelectedTransfer is null
        ? "창고 이동 경로가 없습니다."
        : $"{WarehouseDisplayNameResolver.Resolve(SelectedTransfer.FromWarehouseCode)} → {WarehouseDisplayNameResolver.Resolve(SelectedTransfer.ToWarehouseCode)}";

    public string SelectedTransferMeta => SelectedTransfer is null
        ? "상태 정보가 없습니다."
        : $"{SelectedTransfer.TransferDate:yyyy-MM-dd} · {Normalize(SelectedTransfer.TransferStatus, "수령대기")} · 품목 {SelectedTransfer.Lines?.Count ?? 0:N0}건";

    public string SelectedTransferMemo => SelectedTransfer is null
        ? "메모가 없습니다."
        : string.IsNullOrWhiteSpace(SelectedTransfer.Memo)
            ? "메모가 없습니다."
            : SelectedTransfer.Memo.Trim();

    public string SelectedTransferRequestText => SelectedTransfer is null
        ? string.Empty
        : $"요청: {Normalize(SelectedTransfer.RequestedByUsername, "미기록")} / {FormatDateTime(SelectedTransfer.RequestedAtUtc)}";

    public string SelectedTransferReceiveText => SelectedTransfer is null
        ? string.Empty
        : $"수령: {Normalize(SelectedTransfer.ReceivedByUsername, "미기록")} / {FormatDateTime(SelectedTransfer.ReceivedAtUtc)} / {Normalize(SelectedTransfer.ReceiveMemo, "메모 없음")}";

    public string SelectedTransferRejectText => SelectedTransfer is null
        ? string.Empty
        : $"반려사유: {Normalize(SelectedTransfer.RejectReason, "없음")}";

    public double TransferListHeight => CalculateListHeight(Transfers.Count, 92, 56, 5);
    public double SelectedTransferLinesHeight => CalculateListHeight(SelectedTransferLines.Count, 42, 40, 4);

    public bool NeedsRefresh(TimeSpan maxAge)
        => !_lastRefreshUtc.HasValue || DateTime.UtcNow - _lastRefreshUtc.Value >= maxAge;

    public async Task RefreshAsync()
    {
        if (IsBusy)
            return;

        try
        {
            IsBusy = true;
            StatusMessage = "재고이동 서버 동기화 데이터를 확인하고 있습니다.";
            await _syncCoordinator.RefreshIfServerChangedAsync("inventory-transfers-page", TimeSpan.FromSeconds(5));
            await LoadFromStateAsync(preserveSelection: true);
        }
        catch (Exception ex)
        {
            StatusMessage = $"재고이동 화면 초기화 실패: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SyncNowAsync()
    {
        if (IsBusy)
            return;

        try
        {
            IsBusy = true;
            StatusMessage = "재고이동 데이터를 서버와 동기화하는 중입니다.";
            var state = await _syncCoordinator.SynchronizeNowAsync();
            if (!string.IsNullOrWhiteSpace(state.LastError))
                StatusMessage = $"동기화 주의: {state.LastError}";

            await LoadFromStateAsync(preserveSelection: true);
        }
        catch (Exception ex)
        {
            StatusMessage = $"재고이동 동기화 실패: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public Task SelectTransferAsync(InventoryTransferDto transfer)
    {
        SelectedTransfer = transfer;
        SelectedTransferLines.Clear();
        foreach (var line in transfer.Lines?.OrderBy(x => x.ItemNameOriginal).ToList() ?? new List<InventoryTransferLineDto>())
            SelectedTransferLines.Add(line);

        OnPropertyChanged(nameof(SelectedTransferLinesHeight));
        StatusMessage = $"{SelectedTransferTitle} 이동내역을 확인 중입니다.";
        return Task.CompletedTask;
    }

    private async Task LoadFromStateAsync(bool preserveSelection)
    {
        var state = await _syncStateStore.LoadAsync();
        state.Normalize();

        var selectedId = preserveSelection ? SelectedTransfer?.Id : null;
        var filtered = state.SyncedInventoryTransfers
            .Where(MatchesSearch)
            .OrderByDescending(x => x.TransferDate)
            .ThenByDescending(x => x.UpdatedAtUtc)
            .ThenByDescending(x => x.TransferNumber)
            .ToList();

        Transfers.Clear();
        foreach (var transfer in filtered)
            Transfers.Add(transfer);

        if (selectedId.HasValue)
        {
            var matched = filtered.FirstOrDefault(x => x.Id == selectedId.Value);
            if (matched is not null)
                await SelectTransferAsync(matched);
            else
                ClearSelection();
        }
        else
        {
            ClearSelection();
        }

        _lastRefreshUtc = DateTime.UtcNow;
        StatusMessage = filtered.Count == 0
            ? "동기화된 재고이동 데이터가 없습니다."
            : $"재고이동 {filtered.Count:N0}건을 불러왔습니다.";
        OnPropertyChanged(nameof(TransferListHeight));
        OnPropertyChanged(nameof(SummaryText));
    }

    private bool MatchesSearch(InventoryTransferDto transfer)
    {
        if (string.IsNullOrWhiteSpace(SearchText))
            return true;

        var q = SearchText.Trim();
        return Contains(transfer.TransferNumber, q)
               || Contains(transfer.Memo, q)
               || Contains(transfer.TransferStatus, q)
               || Contains(transfer.RequestedByUsername, q)
               || Contains(transfer.ReceivedByUsername, q)
               || Contains(WarehouseDisplayNameResolver.Resolve(transfer.FromWarehouseCode), q)
               || Contains(WarehouseDisplayNameResolver.Resolve(transfer.ToWarehouseCode), q)
               || (transfer.Lines?.Any(line =>
                       Contains(line.ItemNameOriginal, q)
                       || Contains(line.SpecificationOriginal, q)
                       || Contains(line.Remark, q)) ?? false);
    }

    private void ClearSelection()
    {
        SelectedTransfer = null;
        SelectedTransferLines.Clear();
        OnPropertyChanged(nameof(SelectedTransferLinesHeight));
    }

    private static bool Contains(string? source, string query)
        => !string.IsNullOrWhiteSpace(source) &&
           source.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    private static string Normalize(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string FormatDateTime(DateTime? value)
        => value.HasValue ? value.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "미기록";

    private static double CalculateListHeight(int count, double rowHeight, double emptyHeight, int maxVisibleRows)
    {
        if (count <= 0)
            return emptyHeight;

        var visibleRows = Math.Min(count, maxVisibleRows);
        return (visibleRows * rowHeight) + Math.Max(0, visibleRows - 1) * 8;
    }
}
