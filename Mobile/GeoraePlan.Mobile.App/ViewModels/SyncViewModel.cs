using GeoraePlan.Mobile.App.Services;

namespace GeoraePlan.Mobile.App.ViewModels;

public sealed class SyncViewModel : ObservableObject
{
    private readonly SyncCoordinator _syncCoordinator;

    private string _lastRevisionText = "0";
    private string _lastPullSummary = "-";
    private string _pendingText = "저장 대기: 거래처 0건 / 품목 0건 / 전표 0건 / 수금·지급 0건 / 첨부 0건";
    private string _statusMessage = "동기화 상태를 확인할 준비가 되었습니다.";
    private string _autoSyncText = "저장하면 서버에 바로 올리고, 화면 진입/복귀 시 최신 변경만 확인합니다. 문제가 있을 때만 아래 수동 동기화를 사용하세요.";
    private string _attentionText = string.Empty;
    private bool _hasAttention;
    private bool _isBusy;

    public SyncViewModel(SyncCoordinator syncCoordinator)
    {
        _syncCoordinator = syncCoordinator;
        RefreshCommand = new AsyncCommand(RefreshAsync);
        PullCommand = new AsyncCommand(PullAsync);
        PushCommand = new AsyncCommand(PushAsync);
        SyncNowCommand = new AsyncCommand(SyncNowAsync);
    }

    public string LastRevisionText
    {
        get => _lastRevisionText;
        set => SetProperty(ref _lastRevisionText, value);
    }

    public string LastPullSummary
    {
        get => _lastPullSummary;
        set => SetProperty(ref _lastPullSummary, value);
    }

    public string PendingText
    {
        get => _pendingText;
        set => SetProperty(ref _pendingText, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public string AutoSyncText
    {
        get => _autoSyncText;
        set => SetProperty(ref _autoSyncText, value);
    }

    public string AttentionText
    {
        get => _attentionText;
        set => SetProperty(ref _attentionText, value);
    }

    public bool HasAttention
    {
        get => _hasAttention;
        set => SetProperty(ref _hasAttention, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand PullCommand { get; }
    public AsyncCommand PushCommand { get; }
    public AsyncCommand SyncNowCommand { get; }

    public async Task RefreshAsync()
    {
        var state = await _syncCoordinator.LoadAsync();
        ApplyState(state);
        StatusMessage = string.IsNullOrWhiteSpace(state.LastError)
            ? "동기화 상태를 불러왔습니다."
            : $"최근 동기화 오류: {state.LastError}";
    }

    public async Task PullAsync()
    {
        if (IsBusy)
            return;

        try
        {
            IsBusy = true;
            StatusMessage = "서버에서 최신 데이터를 받는 중입니다.";
            var state = await _syncCoordinator.PullAsync();
            ApplyState(state);
            StatusMessage = string.IsNullOrWhiteSpace(state.LastError)
                ? "서버 데이터 받기 완료"
                : $"서버 데이터 받기 오류: {state.LastError}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task PushAsync()
    {
        if (IsBusy)
            return;

        try
        {
            IsBusy = true;
            StatusMessage = "내 기기 저장 내용을 서버에 올리는 중입니다.";
            var state = await _syncCoordinator.PushAsync();
            ApplyState(state);
            StatusMessage = string.IsNullOrWhiteSpace(state.LastError)
                ? "서버 올리기 완료"
                : $"서버 올리기 오류: {state.LastError}";
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
            StatusMessage = "권장 동기화(내 변경 올리기 → 첨부 올리기 → 서버 최신 받기) 진행 중입니다.";
            var state = await _syncCoordinator.SynchronizeNowAsync();
            ApplyState(state);
            StatusMessage = string.IsNullOrWhiteSpace(state.LastError)
                ? "권장 동기화 완료"
                : $"동기화 대기/오류: {state.LastError}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyState(Models.MobileSyncState state)
    {
        LastRevisionText = state.LastRevision.ToString("N0");
        PendingText =
            $"저장 대기: 거래처 {state.PendingCustomerCount}건 / 품목 {state.PendingItemCount}건 / 전표 {state.PendingInvoiceCount}건 / 수금·지급 {state.PendingPaymentCount}건 / 첨부 {state.PendingPaymentAttachmentCount}건"
            + $" / 거래 {state.PendingTransactionCount}건 / 거래첨부 {state.PendingTransactionAttachmentCount}건 / 재고이동 {state.PendingInventoryTransferCount}건"
            + $" / 렌탈 {state.PendingRentalBillingProfileCount + state.PendingRentalAssetCount + state.PendingRentalBillingLogCount}건";
        LastPullSummary =
            $"마지막 서버 받기: 거래처 {state.LastPulledCustomerCount} / 품목 {state.LastPulledItemCount} / 전표 {state.LastPulledInvoiceCount} / 수금·지급 {state.LastPulledPaymentCount}"
            + $" / 거래 {state.LastPulledTransactionCount} / 거래첨부 {state.LastPulledTransactionAttachmentCount}"
            + $" / 재고이동 {state.LastPulledInventoryTransferCount} / 렌탈 {state.LastPulledRentalBillingProfileCount + state.LastPulledRentalAssetCount + state.LastPulledRentalBillingLogCount}";

        if (state.PendingCustomerCount > 0 || state.PendingItemCount > 0)
        {
            HasAttention = true;
            AttentionText = $"거래처 {state.PendingCustomerCount:N0}건 / 품목 {state.PendingItemCount:N0}건이 서버 올리기 대기 중입니다. 네트워크가 복구되면 자동으로 다시 올립니다.";
        }
        else if (state.PendingPaymentAttachmentCount > 0)
        {
            HasAttention = true;
            AttentionText = $"첨부 {state.PendingPaymentAttachmentCount:N0}건이 서버 올리기 대기 중입니다. 네트워크가 복구되면 자동으로 다시 올립니다.";
        }
        else if (!string.IsNullOrWhiteSpace(state.LastError))
        {
            HasAttention = true;
            AttentionText = $"최근 동기화 오류: {state.LastError}";
        }
        else
        {
            HasAttention = false;
            AttentionText = string.Empty;
        }
    }
}
