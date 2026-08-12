using System.Collections.ObjectModel;
using GeoraePlan.Mobile.App.Services;
using Microsoft.Maui.ApplicationModel;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.ViewModels;

public sealed class PaymentAttachmentsViewModel : ObservableObject
{
    private readonly GeoraePlanApiClient _api;
    private readonly SessionStore _sessionStore;
    private readonly MobileOwnerOperationGate _ownerOperations;
    private IReadOnlyList<PaymentAttachmentDto> _fallbackAttachments = [];

    private Guid _paymentId;
    private MobileSessionOwner? _contextOwner;
    private string _titleText = "수금/지급 첨부";
    private string _statusMessage = "첨부 파일을 불러오세요.";
    private bool _isBusy;

    public PaymentAttachmentsViewModel(
        GeoraePlanApiClient api,
        SessionStore sessionStore)
    {
        _api = api;
        _sessionStore = sessionStore;
        _ownerOperations =
            new MobileOwnerOperationGate(sessionStore);
        RefreshCommand = new AsyncCommand(RefreshAsync);
    }

    public ObservableCollection<PaymentAttachmentDto> Attachments { get; } = new();

    public string TitleText
    {
        get => _titleText;
        set => SetProperty(ref _titleText, value);
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

    public bool EnsureContextOwnerCurrent()
    {
        var owner = _ownerOperations.EnsureCurrentOwner(
            ResetForOwner);
        IsBusy = _ownerOperations.IsBusy;
        if (IsContextOwner(owner) &&
            _ownerOperations.IsCurrent(owner))
            return true;

        ResetForOwner();
        StatusMessage =
            "로그인 사용자가 변경되었습니다. 이전 화면으로 돌아가 수금/지급 내역을 다시 선택해 주세요.";
        return false;
    }

    public async Task InitializeAsync(
        Guid paymentId,
        string titleText,
        IEnumerable<PaymentAttachmentDto>? fallbackAttachments = null)
        => await InitializeAsync(
            paymentId,
            titleText,
            _sessionStore.CaptureOwner(),
            fallbackAttachments);

    public async Task InitializeAsync(
        Guid paymentId,
        string titleText,
        MobileSessionOwner contextOwner,
        IEnumerable<PaymentAttachmentDto>? fallbackAttachments = null)
    {
        ArgumentNullException.ThrowIfNull(contextOwner);
        using (await _sessionStore
                   .AcquireOwnerCommitLeaseAsync(
                       contextOwner))
        {
            _ownerOperations.EnsureCurrentOwner(
                ResetForOwner);
            _contextOwner = contextOwner;
            _paymentId = paymentId;
            TitleText =
                string.IsNullOrWhiteSpace(titleText)
                    ? "수금/지급 첨부"
                    : titleText.Trim();
            _fallbackAttachments =
                NormalizeFallbackAttachments(
                    fallbackAttachments);
            if (_fallbackAttachments.Count > 0)
            {
                ReplaceAttachments(
                    _fallbackAttachments);
                StatusMessage =
                    $"상세 화면 기준 첨부 {_fallbackAttachments.Count:N0}건을 표시합니다. 새로고침으로 서버 최신 목록을 확인하세요.";
            }
        }

        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_paymentId == Guid.Empty)
            return;

        var operation = _ownerOperations.TryBegin(
            ResetForOwner,
            deferRefreshWhenBusy: true);
        IsBusy = _ownerOperations.IsBusy;
        if (operation is null)
            return;

        var runDeferredRefresh = false;
        try
        {
            if (!IsContextOwner(operation.Owner))
            {
                if (_ownerOperations.CanCommit(operation))
                {
                    StatusMessage =
                        "로그인 사용자가 변경되었습니다. 이전 화면으로 돌아가 수금/지급 내역을 다시 선택해 주세요.";
                }
                return;
            }

            StatusMessage = "첨부 파일을 불러오고 있습니다.";
            var attachments =
                await _api.GetPaymentAttachmentsAsync(
                    _paymentId,
                    operation.Owner);
            if (!_ownerOperations.CanCommit(operation))
                return;

            ReplaceAttachments(attachments);

            StatusMessage = attachments.Count == 0
                ? "등록된 첨부가 없습니다."
                : $"첨부 {attachments.Count:N0}건";
        }
        catch (Exception ex)
        {
            if (!_ownerOperations.CanCommit(operation))
                return;

            if (Attachments.Count > 0)
            {
                StatusMessage = $"서버 최신 첨부 조회 실패: {ex.Message} / 상세 화면 기준 첨부 {Attachments.Count:N0}건을 표시합니다.";
            }
            else if (_fallbackAttachments.Count > 0)
            {
                ReplaceAttachments(_fallbackAttachments);
                StatusMessage = $"서버 최신 첨부 조회 실패: {ex.Message} / 상세 화면 기준 첨부 {_fallbackAttachments.Count:N0}건을 표시합니다.";
            }
            else
            {
                StatusMessage = $"첨부 조회 실패: {ex.Message}";
            }
        }
        finally
        {
            runDeferredRefresh = _ownerOperations.Complete(
                operation,
                ResetForOwner);
            IsBusy = _ownerOperations.IsBusy;
            if (runDeferredRefresh)
                await RefreshAsync();
        }
    }

    public async Task OpenAttachmentAsync(PaymentAttachmentDto attachment)
    {
        if (attachment is null)
            return;

        var operation = _ownerOperations.TryBegin(
            ResetForOwner,
            deferRefreshWhenBusy: false);
        IsBusy = _ownerOperations.IsBusy;
        if (operation is null)
            return;

        try
        {
            if (!IsContextOwner(operation.Owner))
            {
                if (_ownerOperations.CanCommit(operation))
                {
                    StatusMessage =
                        "로그인 사용자가 변경되었습니다. 이전 화면으로 돌아가 수금/지급 내역을 다시 선택해 주세요.";
                }
                return;
            }

            var path =
                await _api.DownloadPaymentAttachmentAsync(
                    attachment,
                    operation.Owner);
            if (!_ownerOperations.CanCommit(operation))
                return;

            if (!File.Exists(path))
            {
                StatusMessage = "첨부 파일을 내려받았지만 로컬 파일을 찾지 못했습니다. 다시 시도해 주세요.";
                return;
            }

            if (!_ownerOperations.CanCommit(operation))
                return;
            var opened = await Launcher.Default.OpenAsync(new OpenFileRequest(
                attachment.FileName,
                new ReadOnlyFile(path)));
            if (_ownerOperations.CanCommit(operation))
            {
                StatusMessage = opened
                    ? "첨부 파일을 열었습니다."
                    : "첨부 파일은 내려받았지만 이 기기에서 열 수 있는 앱을 찾지 못했습니다. PDF/이미지 뷰어를 설치한 뒤 다시 시도하세요.";
            }
        }
        catch (Exception ex)
        {
            if (_ownerOperations.CanCommit(operation))
            {
                StatusMessage = IsNoViewerAvailable(ex)
                    ? "첨부 파일은 내려받았지만 이 기기에서 열 수 있는 앱을 찾지 못했습니다. PDF/이미지 뷰어를 설치한 뒤 다시 시도하세요."
                    : $"첨부 열기 실패: {ex.Message}";
            }
        }
        finally
        {
            _ownerOperations.Complete(
                operation,
                ResetForOwner);
            IsBusy = _ownerOperations.IsBusy;
        }
    }

    private bool IsContextOwner(
        MobileSessionOwner owner)
        => _contextOwner is not null &&
           _contextOwner.IsAuthenticated ==
           owner.IsAuthenticated &&
           _contextOwner.HasSameLogicalOwner(owner) &&
           string.Equals(
               _contextOwner.SessionGeneration,
               owner.SessionGeneration,
               StringComparison.Ordinal);

    private static bool IsNoViewerAvailable(Exception ex)
    {
        var message = ex.Message ?? string.Empty;
        return message.Contains("No Activity found", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("ActivityNotFound", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("no application", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("no app", StringComparison.OrdinalIgnoreCase);
    }

    private void ReplaceAttachments(IEnumerable<PaymentAttachmentDto> attachments)
    {
        Attachments.Clear();
        foreach (var attachment in attachments.Where(attachment => attachment is not null && !attachment.IsDeleted))
            Attachments.Add(attachment);
    }

    private void ResetForOwner()
    {
        Attachments.Clear();
        _fallbackAttachments = [];
        TitleText = "수금/지급 첨부";
        StatusMessage =
            "로그인 사용자가 변경되었습니다. 수금/지급 내역을 다시 선택해 주세요.";
    }

    private static IReadOnlyList<PaymentAttachmentDto> NormalizeFallbackAttachments(IEnumerable<PaymentAttachmentDto>? attachments)
        => attachments?
            .Where(attachment => attachment is not null && !attachment.IsDeleted && attachment.Id != Guid.Empty)
            .GroupBy(attachment => attachment.Id)
            .Select(group => group.First())
            .OrderByDescending(attachment => attachment.UploadedAtUtc)
            .ToList() ?? [];
}
