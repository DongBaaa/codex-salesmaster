using System.Collections.ObjectModel;
using GeoraePlan.Mobile.App.Services;
using Microsoft.Maui.ApplicationModel;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.ViewModels;

public sealed class PaymentAttachmentsViewModel : ObservableObject
{
    private readonly GeoraePlanApiClient _api;

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

    public async Task InitializeAsync(Guid paymentId, string titleText)
    {
        _paymentId = paymentId;
        TitleText = string.IsNullOrWhiteSpace(titleText) ? "수금/지급 첨부" : titleText.Trim();
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

            Attachments.Clear();
            foreach (var attachment in attachments)
                Attachments.Add(attachment);

            StatusMessage = attachments.Count == 0
                ? "등록된 첨부가 없습니다."
                : $"첨부 {attachments.Count:N0}건";
        }
        catch (Exception ex)
        {
            StatusMessage = $"첨부 조회 실패: {ex.Message}";
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
            await Launcher.Default.OpenAsync(new OpenFileRequest(
                attachment.FileName,
                new ReadOnlyFile(path)));
            StatusMessage = "첨부 파일을 열었습니다.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"첨부 열기 실패: {ex.Message}";
        }
    }
}
