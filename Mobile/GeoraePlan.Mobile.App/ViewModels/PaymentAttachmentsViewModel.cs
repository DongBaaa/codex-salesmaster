using System.Collections.ObjectModel;
using GeoraePlan.Mobile.App.Services;
using Microsoft.Maui.ApplicationModel;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.ViewModels;

public sealed class PaymentAttachmentsViewModel : ObservableObject
{
    private readonly GeoraePlanApiClient _api;
    private IReadOnlyList<PaymentAttachmentDto> _fallbackAttachments = [];

    private Guid _paymentId;
    private string _titleText = "수금/지급 첨부";
    private string _statusMessage = "첨부 파일을 불러오세요.";
    private bool _isBusy;

    public PaymentAttachmentsViewModel(GeoraePlanApiClient api)
    {
        _api = api;
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

    public async Task InitializeAsync(
        Guid paymentId,
        string titleText,
        IEnumerable<PaymentAttachmentDto>? fallbackAttachments = null)
    {
        _paymentId = paymentId;
        TitleText = string.IsNullOrWhiteSpace(titleText) ? "수금/지급 첨부" : titleText.Trim();
        _fallbackAttachments = NormalizeFallbackAttachments(fallbackAttachments);
        if (_fallbackAttachments.Count > 0)
        {
            ReplaceAttachments(_fallbackAttachments);
            StatusMessage = $"상세 화면 기준 첨부 {_fallbackAttachments.Count:N0}건을 표시합니다. 새로고침으로 서버 최신 목록을 확인하세요.";
        }

        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (IsBusy || _paymentId == Guid.Empty)
            return;

        try
        {
            IsBusy = true;
            StatusMessage = "첨부 파일을 불러오고 있습니다.";
            var attachments = await _api.GetPaymentAttachmentsAsync(_paymentId);

            ReplaceAttachments(attachments);

            StatusMessage = attachments.Count == 0
                ? "등록된 첨부가 없습니다."
                : $"첨부 {attachments.Count:N0}건";
        }
        catch (Exception ex)
        {
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
            IsBusy = false;
        }
    }

    public async Task OpenAttachmentAsync(PaymentAttachmentDto attachment)
    {
        if (attachment is null)
            return;

        try
        {
            var path = await _api.DownloadPaymentAttachmentAsync(attachment);
            if (!File.Exists(path))
            {
                StatusMessage = "첨부 파일을 내려받았지만 로컬 파일을 찾지 못했습니다. 다시 시도해 주세요.";
                return;
            }

            var opened = await Launcher.Default.OpenAsync(new OpenFileRequest(
                attachment.FileName,
                new ReadOnlyFile(path)));
            StatusMessage = opened
                ? "첨부 파일을 열었습니다."
                : "첨부 파일은 내려받았지만 이 기기에서 열 수 있는 앱을 찾지 못했습니다. PDF/이미지 뷰어를 설치한 뒤 다시 시도하세요.";
        }
        catch (Exception ex)
        {
            StatusMessage = IsNoViewerAvailable(ex)
                ? "첨부 파일은 내려받았지만 이 기기에서 열 수 있는 앱을 찾지 못했습니다. PDF/이미지 뷰어를 설치한 뒤 다시 시도하세요."
                : $"첨부 열기 실패: {ex.Message}";
        }
    }

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

    private static IReadOnlyList<PaymentAttachmentDto> NormalizeFallbackAttachments(IEnumerable<PaymentAttachmentDto>? attachments)
        => attachments?
            .Where(attachment => attachment is not null && !attachment.IsDeleted && attachment.Id != Guid.Empty)
            .GroupBy(attachment => attachment.Id)
            .Select(group => group.First())
            .OrderByDescending(attachment => attachment.UploadedAtUtc)
            .ToList() ?? [];
}
