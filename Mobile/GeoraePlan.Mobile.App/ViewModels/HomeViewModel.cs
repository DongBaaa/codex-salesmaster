using GeoraePlan.Mobile.App.Services;

namespace GeoraePlan.Mobile.App.ViewModels;

public sealed class HomeViewModel : ObservableObject
{
    private readonly SessionStore _sessionStore;
    private readonly JsonSyncStateStore _syncStateStore;

    private string _displayName = "거래플랜";
    private string _roleText = "로그인이 필요합니다.";
    private string _lastSyncText = "아직 동기화 기록이 없습니다.";
    private string _statusMessage = "모바일 거래플랜 준비 완료";
    private string _autoSyncText = "로그인 후 자동 동기화가 시작됩니다.";
    private string _pendingNoticeText = string.Empty;
    private bool _hasPendingNotice;

    public HomeViewModel(SessionStore sessionStore, JsonSyncStateStore syncStateStore)
    {
        _sessionStore = sessionStore;
        _syncStateStore = syncStateStore;
        RefreshCommand = new AsyncCommand(RefreshAsync);
    }

    public string DisplayName
    {
        get => _displayName;
        set => SetProperty(ref _displayName, value);
    }

    public string RoleText
    {
        get => _roleText;
        set => SetProperty(ref _roleText, value);
    }

    public string LastSyncText
    {
        get => _lastSyncText;
        set => SetProperty(ref _lastSyncText, value);
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

    public string PendingNoticeText
    {
        get => _pendingNoticeText;
        set => SetProperty(ref _pendingNoticeText, value);
    }

    public bool HasPendingNotice
    {
        get => _hasPendingNotice;
        set => SetProperty(ref _hasPendingNotice, value);
    }

    public AsyncCommand RefreshCommand { get; }

    public async Task RefreshAsync()
    {
        var session = _sessionStore.GetSnapshot();
        DisplayName = session.IsAuthenticated
            ? $"{session.Username} 님"
            : "로그인이 필요합니다.";
        RoleText = session.IsAuthenticated
            ? $"권한: {session.Role}"
            : "권한 정보 없음";
        AutoSyncText = session.IsAuthenticated
            ? "저장하면 서버에 바로 올리고, 화면 진입/복귀 시 최신 변경만 확인합니다."
            : "로그인 후 자동 동기화가 시작됩니다.";

        var sync = await _syncStateStore.LoadAsync();
        LastSyncText = sync.LastSuccessUtc.HasValue
            ? $"마지막 성공 동기화: {sync.LastSuccessUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
            : "아직 동기화 기록이 없습니다.";

        if (sync.PendingServerMutationCount == 0 && sync.PendingPaymentAttachmentCount > 0)
        {
            HasPendingNotice = true;
            PendingNoticeText = $"첨부 {sync.PendingPaymentAttachmentCount:N0}건 업로드 대기 중입니다. 네트워크 복구 후 자동 재시도됩니다.";
        }
        else if (sync.PendingTotalCount > 0)
        {
            HasPendingNotice = true;
            PendingNoticeText =
                $"설정 {sync.PendingSettingCount:N0}건 / 거래처기준 {sync.PendingCustomerMasterCount:N0}건 / 거래처 {sync.PendingCustomerCount:N0}건 / 계약 {sync.PendingCustomerContractCount:N0}건"
                + $" / 품목 {sync.PendingItemCount:N0}건 / 재고 {sync.PendingItemWarehouseStockCount:N0}건 / 전표 {sync.PendingInvoiceCount:N0}건 / 수금·지급 {sync.PendingPaymentCount:N0}건 / 첨부 {sync.PendingPaymentAttachmentCount:N0}건"
                + $" / 거래 {sync.PendingTransactionCount:N0}건 / 거래첨부 {sync.PendingTransactionAttachmentCount:N0}건 / 재고이동 {sync.PendingInventoryTransferCount:N0}건"
                + $" / 렌탈관리 {sync.PendingRentalManagementCompanyCount:N0}건 / 렌탈 {sync.PendingRentalBillingProfileCount + sync.PendingRentalAssetCount + sync.PendingRentalAssetAssignmentHistoryCount + sync.PendingRentalBillingLogCount:N0}건 업로드 대기 중입니다.";
        }
        else
        {
            HasPendingNotice = false;
            PendingNoticeText = string.Empty;
        }

        StatusMessage = string.IsNullOrWhiteSpace(sync.LastError)
            ? "운영 서버와 자동 동기화 준비됨 · 재고이동/렌탈은 모바일 조회 전용"
            : $"최근 동기화 주의: {sync.LastError}";
    }
}
