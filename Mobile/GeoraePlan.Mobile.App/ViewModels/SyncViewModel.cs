using GeoraePlan.Mobile.App.Services;

namespace GeoraePlan.Mobile.App.ViewModels;

public sealed class SyncViewModel : ObservableObject
{
    private readonly SyncCoordinator _syncCoordinator;

    private string _lastRevisionText = "0";
    private string _lastPullSummary = "-";
    private string _pendingText = "대기 전표 0건 / 대기 수금 0건 / 대기 첨부 0건";
    private string _statusMessage = "동기화 준비";
    private string _autoSyncText = "앱 활성 중에는 약 25초 간격으로 자동 동기화를 시도합니다.";
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
            : $"최근 오류: {state.LastError}";
    }

    public async Task PullAsync()
    {
        if (IsBusy)
            return;

        try
        {
            IsBusy = true;
            StatusMessage = "다운로드 동기화 중...";
            var state = await _syncCoordinator.PullAsync();
            ApplyState(state);
            StatusMessage = string.IsNullOrWhiteSpace(state.LastError)
                ? "다운로드 동기화 완료"
                : $"다운로드 오류: {state.LastError}";
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
            StatusMessage = "업로드 동기화 중...";
            var state = await _syncCoordinator.PushAsync();
            ApplyState(state);
            StatusMessage = string.IsNullOrWhiteSpace(state.LastError)
                ? "업로드 동기화 완료"
                : $"업로드 오류: {state.LastError}";
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
            StatusMessage = "권장 동기화(업로드 → 첨부 업로드 → 다운로드) 진행 중...";
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
        PendingText = $"대기 전표 {state.PendingInvoiceCount}건 / 대기 수금 {state.PendingPaymentCount}건 / 대기 첨부 {state.PendingPaymentAttachmentCount}건";
        LastPullSummary = $"최근 pull: 거래처 {state.LastPulledCustomerCount} / 품목 {state.LastPulledItemCount} / 전표 {state.LastPulledInvoiceCount} / 수금 {state.LastPulledPaymentCount}";

        if (state.PendingPaymentAttachmentCount > 0)
        {
            HasAttention = true;
            AttentionText = $"첨부 {state.PendingPaymentAttachmentCount:N0}건이 업로드 대기 중입니다. 네트워크 복구 후 자동 재시도됩니다.";
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
