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
    private MobilePaymentMethodOption? _selectedPaymentMethod;
    private DateTime _paymentDate = DateTime.Today;
    private string _amountText = "0";
    private string _note = string.Empty;
    private string _statusMessage = "1단계 전표 선택 → 2단계 수금/지급 방식·금액 확인 → 마지막 저장 순서로 입력하세요.";
    private bool _isBusy;
    private InvoiceDto? _initialInvoice;

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
        RefreshPaymentMethodOptions();
    }

    public event Func<Task>? SavedSuccessfully;

    public ObservableCollection<InvoiceDto> Invoices { get; } = new();
    public ObservableCollection<PendingPaymentAttachmentRecord> Attachments { get; } = new();
    public ObservableCollection<MobilePaymentMethodOption> PaymentMethodOptions { get; } = new();

    public InvoiceDto? SelectedInvoice
    {
        get => _selectedInvoice;
        set
        {
            if (!SetProperty(ref _selectedInvoice, value))
                return;

            OnPropertyChanged(nameof(PaymentActionText));
            OnPropertyChanged(nameof(PageTitleText));
            OnPropertyChanged(nameof(AmountPlaceholderText));
            OnPropertyChanged(nameof(PaymentDateLabelText));
            OnPropertyChanged(nameof(SaveButtonText));
            OnPropertyChanged(nameof(AttachmentSectionTitle));
            OnPropertyChanged(nameof(SelectedInvoiceSummary));
            OnPropertyChanged(nameof(PaymentMethodLabelText));
            OnPropertyChanged(nameof(PaymentMethodHelpText));
            RefreshPaymentMethodOptions();

            var outstanding = CalculateOutstandingAmount(value);
            if (outstanding > 0m && (string.IsNullOrWhiteSpace(AmountText) || AmountText == "0"))
                AmountText = outstanding.ToString("0.##");
        }
    }

    public MobilePaymentMethodOption? SelectedPaymentMethod
    {
        get => _selectedPaymentMethod;
        set => SetProperty(ref _selectedPaymentMethod, value);
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

    public string PaymentActionText => SelectedInvoice?.VoucherType == VoucherType.Purchase ? "지급" : "수금";
    public string PageTitleText => SelectedInvoice is null ? "수금/지급 입력" : $"{PaymentActionText} 입력";
    public string AmountPlaceholderText => SelectedInvoice is null ? "수금/지급 금액" : $"{PaymentActionText} 금액";
    public string PaymentDateLabelText => SelectedInvoice is null ? "수금/지급일자" : $"{PaymentActionText}일자";
    public string PaymentMethodLabelText => SelectedInvoice?.VoucherType == VoucherType.Purchase ? "지급방식" : "수금방식";
    public string PaymentMethodHelpText => SelectedInvoice?.VoucherType == VoucherType.Purchase
        ? "PC 구매 전표와 동일하게 지급 금액 칸에 반영됩니다."
        : "PC 판매 전표와 동일하게 수금 금액 칸에 반영됩니다.";
    public string SaveButtonText => SelectedInvoice is null ? "마지막 단계 · 수금/지급 저장" : $"마지막 단계 · {PaymentActionText} 저장";
    public string AttachmentSectionTitle => SelectedInvoice is null ? "증빙 첨부" : $"{PaymentActionText} 증빙 첨부";
    public string SelectedInvoiceSummary
    {
        get
        {
            if (SelectedInvoice is null)
                return "전표를 먼저 선택하세요.";

            var kind = SelectedInvoice.VoucherType == VoucherType.Purchase ? "구매(매입)" : "판매(매출)";
            var paid = SelectedInvoice.Payments?.Where(payment => !payment.IsDeleted).Sum(payment => payment.Amount) ?? 0m;
            var outstandingLabel = SelectedInvoice.VoucherType == VoucherType.Purchase ? "미지급금" : "미수금";
            return $"{kind} · {SelectedInvoice.CustomerName} · 합계 {SelectedInvoice.TotalAmount:N0}원 · {outstandingLabel} {Math.Max(0m, SelectedInvoice.TotalAmount - paid):N0}원";
        }
    }

    public AsyncCommand LoadCommand { get; }
    public AsyncCommand SaveDraftCommand { get; }

    public void ConfigureInitialInvoice(InvoiceDto invoice)
    {
        _initialInvoice = invoice;
        if (Invoices.Count > 0)
            ApplyInitialInvoice();
    }

    public async Task LoadAsync()
    {
        if (IsBusy)
            return;

        if (Invoices.Count > 0)
        {
            ApplyInitialInvoice();
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "수금/지급할 전표 목록을 불러오고 있습니다.";
            _ = RefreshSyncSnapshotInBackgroundAsync();

            var invoices = await _api.GetInvoicesAsync(null);
            Invoices.Clear();
            foreach (var invoice in invoices.OrderByDescending(x => x.InvoiceDate))
                Invoices.Add(invoice);

            ApplyInitialInvoice();

            StatusMessage = $"전표 {Invoices.Count:N0}건을 불러왔습니다. 수금/지급할 전표를 먼저 선택하세요.";
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

    private async Task RefreshSyncSnapshotInBackgroundAsync()
    {
        try
        {
            await _syncCoordinator.RefreshIfServerChangedAsync("payment-draft-load-bg", TimeSpan.FromSeconds(15));
        }
        catch
        {
            // 수금/지급 화면 진입과 전표 조회를 막지 않기 위한 백그라운드 보강 동기화입니다.
        }
    }

    private void ApplyInitialInvoice()
    {
        if (_initialInvoice is null)
            return;

        var matched = Invoices.FirstOrDefault(invoice => invoice.Id == _initialInvoice.Id);
        if (matched is null)
        {
            matched = _initialInvoice;
            Invoices.Insert(0, matched);
        }

        SelectedInvoice = matched;
        StatusMessage = $"{PaymentActionText}할 전표가 선택되었습니다. 방식과 금액을 확인한 뒤 마지막 저장 버튼을 누르세요.";
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

            var attachment = await _attachmentStore.ImportAsync(file, "PDF", $"{PaymentActionText} 첨부", CancellationToken.None);
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
                Title = $"{PaymentActionText} 내역 촬영"
            });

            if (photo is null)
                return;

            var attachment = await _attachmentStore.ImportAsync(photo, "카메라", $"{PaymentActionText} 첨부", CancellationToken.None);
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

        var selectedPaymentMethod = SelectedPaymentMethod;
        if (selectedPaymentMethod is null)
        {
            StatusMessage = $"{PaymentMethodLabelText}을 선택하세요.";
            return;
        }

        if (!decimal.TryParse(AmountText, out var amount) || amount <= 0)
        {
            StatusMessage = "금액을 올바르게 입력하세요.";
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "최신 전표 잔액을 확인하고 있습니다.";

            var latestInvoice = await RefreshSelectedInvoiceForSaveAsync(SelectedInvoice);
            if (latestInvoice is null)
            {
                StatusMessage = "선택한 전표가 최신 데이터에서 확인되지 않습니다. 전표 목록을 다시 조회한 뒤 시도하세요.";
                _refreshCoordinator.MarkInvoicesChanged();
                return;
            }

            var outstandingAmount = CalculateOutstandingAmount(latestInvoice);
            if (amount > outstandingAmount)
            {
                StatusMessage = $"입력 금액이 최신 잔액보다 {(amount - outstandingAmount):N0}원 많습니다. 금액을 다시 확인하세요.";
                _refreshCoordinator.MarkInvoicesChanged();
                return;
            }

            var now = DateTime.UtcNow;
            var paymentId = Guid.NewGuid();
            var paymentNote = BuildPaymentNote(selectedPaymentMethod, Note);
            var payment = new PaymentDto
            {
                Id = paymentId,
                InvoiceId = latestInvoice.Id,
                PaymentDate = DateOnly.FromDateTime(PaymentDate),
                Amount = amount,
                Note = paymentNote,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                Revision = 0,
                IsDeleted = false
            };
            var linkedTransaction = BuildLinkedTransaction(
                paymentId,
                latestInvoice,
                selectedPaymentMethod,
                amount,
                DateOnly.FromDateTime(PaymentDate),
                paymentNote,
                now);

            StatusMessage = $"{PaymentActionText} 정보를 {selectedPaymentMethod.DisplayName}으로 저장하고 있습니다.";

            foreach (var attachment in Attachments)
                attachment.PaymentId = payment.Id;

            var state = await _syncCoordinator.SavePaymentImmediatelyAsync(payment, Attachments, linkedTransaction);
            if (SyncCoordinator.IsConcurrencyConflictState(state))
            {
                _refreshCoordinator.MarkInvoicesChanged();
                StatusMessage = $"{PaymentActionText}이 저장되지 않았습니다. {state.LastError}";
                return;
            }

            if (state.PendingPaymentCount == 0)
            {
                _refreshCoordinator.MarkInvoicesChanged();
                StatusMessage = state.PendingPaymentAttachmentCount == 0
                    ? string.IsNullOrWhiteSpace(state.LastError)
                        ? $"{PaymentActionText} 저장 및 서버 반영 완료"
                        : $"{PaymentActionText} 저장 완료 / 최신 데이터 새로고침 대기: {state.LastError}"
                    : $"{PaymentActionText} 저장 완료 / 첨부 {state.PendingPaymentAttachmentCount:N0}건은 네트워크 복구 후 자동 업로드됩니다.";

                if (SavedSuccessfully is not null)
                    await SavedSuccessfully.Invoke();
            }
            else
            {
                StatusMessage = $"{PaymentActionText} 저장 완료(동기화/첨부 대기): {state.LastError}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"{PaymentActionText} 저장 실패: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<InvoiceDto?> RefreshSelectedInvoiceForSaveAsync(InvoiceDto invoice)
    {
        var latest = await _api.GetInvoiceByIdAsync(invoice.Id);
        if (latest is null || latest.IsDeleted)
            return null;

        ReplaceInvoiceSnapshot(latest);
        return latest;
    }

    private void ReplaceInvoiceSnapshot(InvoiceDto latest)
    {
        for (var index = 0; index < Invoices.Count; index++)
        {
            if (Invoices[index].Id != latest.Id)
                continue;

            Invoices[index] = latest;
            SelectedInvoice = latest;
            return;
        }

        Invoices.Insert(0, latest);
        SelectedInvoice = latest;
    }

    private static decimal CalculateOutstandingAmount(InvoiceDto? invoice)
    {
        if (invoice is null)
            return 0m;

        var paid = invoice.Payments?.Where(payment => !payment.IsDeleted).Sum(payment => payment.Amount) ?? 0m;
        return Math.Max(0m, invoice.TotalAmount - paid);
    }

    private void RefreshPaymentMethodOptions()
    {
        var preferredBucket = SelectedPaymentMethod?.BucketKey;
        var isPurchase = SelectedInvoice?.VoucherType == VoucherType.Purchase;
        PaymentMethodOptions.Clear();
        foreach (var option in MobilePaymentMethodOption.CreateOptions(isPurchase))
            PaymentMethodOptions.Add(option);

        SelectedPaymentMethod =
            PaymentMethodOptions.FirstOrDefault(option => string.Equals(option.BucketKey, preferredBucket, StringComparison.OrdinalIgnoreCase)) ??
            PaymentMethodOptions.FirstOrDefault(option => option.BucketKey == MobilePaymentMethodOption.BucketBank) ??
            PaymentMethodOptions.FirstOrDefault();
    }

    private static string BuildPaymentNote(MobilePaymentMethodOption method, string note)
    {
        var trimmedNote = note.Trim();
        return string.IsNullOrWhiteSpace(trimmedNote)
            ? method.DisplayName
            : $"{method.DisplayName} · {trimmedNote}";
    }

    private static TransactionDto BuildLinkedTransaction(
        Guid paymentId,
        InvoiceDto invoice,
        MobilePaymentMethodOption method,
        decimal amount,
        DateOnly paymentDate,
        string note,
        DateTime now)
    {
        var transaction = new TransactionDto
        {
            Id = paymentId,
            CustomerId = invoice.CustomerId,
            TenantCode = invoice.TenantCode,
            OfficeCode = invoice.OfficeCode,
            ResponsibleOfficeCode = invoice.ResponsibleOfficeCode,
            TransactionDate = paymentDate,
            TransactionKind = method.TransactionKind,
            LinkedInvoiceId = invoice.Id,
            LinkedInvoiceNumber = ResolveInvoiceNumber(invoice),
            SettlementAmount = amount,
            Note = note,
            Memo = string.Empty,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Revision = 0,
            IsDeleted = false
        };

        if (method.IsPurchase)
        {
            transaction.PaymentTotal = amount;
            if (method.BucketKey == MobilePaymentMethodOption.BucketCash)
                transaction.CashPayment = amount;
            else if (method.BucketKey == MobilePaymentMethodOption.BucketCard)
                transaction.CardPayment = amount;
            else
                transaction.BankPayment = amount;
        }
        else
        {
            transaction.ReceiptTotal = amount;
            if (method.BucketKey == MobilePaymentMethodOption.BucketCash)
                transaction.CashReceipt = amount;
            else if (method.BucketKey == MobilePaymentMethodOption.BucketCard)
                transaction.CardReceipt = amount;
            else
                transaction.BankReceipt = amount;
        }

        return transaction;
    }

    private static string ResolveInvoiceNumber(InvoiceDto invoice)
        => !string.IsNullOrWhiteSpace(invoice.InvoiceNumber)
            ? invoice.InvoiceNumber
            : invoice.LocalTempNumber;
}
