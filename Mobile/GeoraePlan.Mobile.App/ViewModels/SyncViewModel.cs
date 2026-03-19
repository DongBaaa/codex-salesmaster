using GeoraePlan.Mobile.App.Services;

namespace GeoraePlan.Mobile.App.ViewModels;

public sealed class SyncViewModel : ObservableObject
{
    private readonly SyncCoordinator _syncCoordinator;

    private string _lastRevisionText = "0";
    private string _lastPullSummary = "-";
    private string _pendingText = "대기중 0건";
    private string _statusMessage = "수동 동기화 준비";
    private bool _isBusy;

    public SyncViewModel(SyncCoordinator syncCoordinator)
    {
        _syncCoordinator = syncCoordinator;
        RefreshCommand = new AsyncCommand(RefreshAsync);
        PullCommand = new AsyncCommand(PullAsync);
        PushCommand = new AsyncCommand(PushAsync);
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

    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand PullCommand { get; }
    public AsyncCommand PushCommand { get; }

    public async Task RefreshAsync()
    {
        var state = await _syncCoordinator.LoadAsync();
        ApplyState(state);
        StatusMessage = string.IsNullOrWhiteSpace(state.LastError)
            ? "수동 동기화 준비"
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

    private void ApplyState(Models.MobileSyncState state)
    {
        LastRevisionText = state.LastRevision.ToString("N0");
        PendingText = $"대기 전표 {state.PendingInvoiceCount}건 / 대기 수금 {state.PendingPaymentCount}건";
        LastPullSummary = $"최근 pull: 거래처 {state.LastPulledCustomerCount} / 품목 {state.LastPulledItemCount} / 전표 {state.LastPulledInvoiceCount} / 수금 {state.LastPulledPaymentCount}";
    }
}
