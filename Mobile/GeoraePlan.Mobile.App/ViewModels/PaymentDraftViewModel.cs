using System.Collections.ObjectModel;
using GeoraePlan.Mobile.App.Models;
using GeoraePlan.Mobile.App.Services;
using Microsoft.Maui.ApplicationModel;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.ViewModels;

public sealed class PaymentDraftViewModel : ObservableObject
{
    private readonly GeoraePlanApiClient _api;
    private readonly SyncCoordinator _syncCoordinator;
    private readonly MobileRefreshCoordinator _refreshCoordinator;
    private readonly PaymentAttachmentDraftStore _attachmentStore;

    private InvoiceDto? _selectedInvoice;
    private DateTime _paymentDate = DateTime.Today;
    private string _amountText = "0";
    private string _note = string.Empty;
    private string _statusMessage = "전표를 선택하고 수금 정보를 입력하세요.";
    private bool _isBusy;

    public PaymentDraftViewModel(
        GeoraePlanApiClient api,
        SyncCoordinator syncCoordinator,
        MobileRefreshCoordinator refreshCoordinator,
        PaymentAttachmentDraftStore attachmentStore)
    {
        _api = api;
        _syncCoordinator = syncCoordinator;
        _refreshCoordinator = refreshCoordinator;
        _attachmentStore = attachmentStore;
        LoadCommand = new AsyncCommand(LoadAsync);
        SaveDraftCommand = new AsyncCommand(SaveDraftAsync);
    }

    public event Func<Task>? SavedSuccessfully;

    public ObservableCollection<InvoiceDto> Invoices { get; } = new();
    public ObservableCollection<PendingPaymentAttachmentRecord> Attachments { get; } = new();

    public InvoiceDto? SelectedInvoice
    {
        get => _selectedInvoice;
        set => SetProperty(ref _selectedInvoice, value);
    }

    public DateTime PaymentDate
    {
        get => _paymentDate;
        set => SetProperty(ref _paymentDate, value);
    }

    public string AmountText
    {
        get => _amountText;
        set => SetProperty(ref _amountText, value);
    }

    public string Note
    {
        get => _note;
        set => SetProperty(ref _note, value);
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

    public string AttachmentSummary => Attachments.Count == 0
        ? "첨부 없음"
        : $"첨부 {Attachments.Count:N0}건";

    public AsyncCommand LoadCommand { get; }
    public AsyncCommand SaveDraftCommand { get; }

    public async Task LoadAsync()
    {
        if (IsBusy || Invoices.Count > 0)
            return;

        try
        {
            IsBusy = true;
            StatusMessage = "전표 목록을 불러오고 있습니다.";
            await _syncCoordinator.RefreshIfServerChangedAsync("payment-draft-load", TimeSpan.FromSeconds(5));

            var invoices = await _api.GetInvoicesAsync(null);
            Invoices.Clear();
            foreach (var invoice in invoices.OrderByDescending(x => x.InvoiceDate))
                Invoices.Add(invoice);

            StatusMessage = $"전표 {Invoices.Count:N0}건";
        }
        catch (Exception ex)
        {
            StatusMessage = $"전표 목록 불러오기 실패: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task AddPdfAttachmentAsync()
    {
        try
        {
            var file = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "PDF 파일 선택",
                FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    [DevicePlatform.Android] = ["application/pdf"],
                    [DevicePlatform.WinUI] = [".pdf"],
                    [DevicePlatform.iOS] = ["com.adobe.pdf"],
                    [DevicePlatform.MacCatalyst] = ["pdf"]
                })
            });

            if (file is null)
                return;

            var attachment = await _attachmentStore.ImportAsync(file, "PDF", "수금 첨부", CancellationToken.None);
            Attachments.Add(attachment);
            OnPropertyChanged(nameof(AttachmentSummary));
            StatusMessage = $"첨부 추가: {attachment.FileName}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"PDF 첨부 실패: {ex.Message}";
        }
    }

    public async Task CaptureAttachmentAsync()
    {
        try
        {
            if (!MediaPicker.Default.IsCaptureSupported)
            {
                StatusMessage = "현재 기기에서는 카메라 촬영을 지원하지 않습니다.";
                return;
            }

            var photo = await MediaPicker.Default.CapturePhotoAsync(new MediaPickerOptions
            {
                Title = "수금 내역 촬영"
            });

            if (photo is null)
                return;

            var attachment = await _attachmentStore.ImportAsync(photo, "카메라", "수금 첨부", CancellationToken.None);
            Attachments.Add(attachment);
            OnPropertyChanged(nameof(AttachmentSummary));
            StatusMessage = $"촬영 이미지 첨부: {attachment.FileName}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"카메라 첨부 실패: {ex.Message}";
        }
    }

    public async Task OpenAttachmentAsync(PendingPaymentAttachmentRecord attachment)
    {
        if (attachment is null || string.IsNullOrWhiteSpace(attachment.StoredPath) || !File.Exists(attachment.StoredPath))
        {
            StatusMessage = "첨부 파일을 찾을 수 없습니다.";
            return;
        }

        try
        {
            await Launcher.Default.OpenAsync(new OpenFileRequest(attachment.FileName, new ReadOnlyFile(attachment.StoredPath)));
            StatusMessage = $"첨부 파일 열기: {attachment.FileName}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"첨부 파일 열기 실패: {ex.Message}";
        }
    }

    public async Task RemoveAttachmentAsync(PendingPaymentAttachmentRecord attachment)
    {
        Attachments.Remove(attachment);
        await _attachmentStore.RemoveAsync(attachment, CancellationToken.None);
        OnPropertyChanged(nameof(AttachmentSummary));
        StatusMessage = $"첨부 삭제: {attachment.FileName}";
    }

    public async Task SaveDraftAsync()
    {
        if (IsBusy)
            return;

        if (SelectedInvoice is null)
        {
            StatusMessage = "전표를 선택하세요.";
            return;
        }

        if (!decimal.TryParse(AmountText, out var amount) || amount <= 0)
        {
            StatusMessage = "금액을 올바르게 입력하세요.";
            return;
        }

        var now = DateTime.UtcNow;
        var payment = new PaymentDto
        {
            Id = Guid.NewGuid(),
            InvoiceId = SelectedInvoice.Id,
            PaymentDate = DateOnly.FromDateTime(PaymentDate),
            Amount = amount,
            Note = Note.Trim(),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Revision = 0,
            IsDeleted = false
        };

        try
        {
            IsBusy = true;
            StatusMessage = "수금 정보를 저장하고 있습니다.";

            foreach (var attachment in Attachments)
                attachment.PaymentId = payment.Id;

            var state = await _syncCoordinator.SavePaymentImmediatelyAsync(payment, Attachments);
            if (state.PendingPaymentCount == 0)
            {
                _refreshCoordinator.MarkInvoicesChanged();
                StatusMessage = state.PendingPaymentAttachmentCount == 0
                    ? string.IsNullOrWhiteSpace(state.LastError)
                        ? "수금 저장 및 서버 반영 완료"
                        : $"수금 저장 완료 / 최신 데이터 새로고침 대기: {state.LastError}"
                    : $"수금 저장 완료 / 첨부 {state.PendingPaymentAttachmentCount:N0}건은 네트워크 복구 후 자동 업로드됩니다.";

                if (SavedSuccessfully is not null)
                    await SavedSuccessfully.Invoke();
            }
            else
            {
                StatusMessage = $"수금 저장 완료(동기화/첨부 대기): {state.LastError}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"수금 저장 실패: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
