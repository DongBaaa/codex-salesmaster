using System.Collections.ObjectModel;
using GeoraePlan.Mobile.App.Services;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.ViewModels;

public sealed class PaymentDraftViewModel : ObservableObject
{
    private readonly GeoraePlanApiClient _api;
    private readonly SyncCoordinator _syncCoordinator;

    private InvoiceDto? _selectedInvoice;
    private DateTime _paymentDate = DateTime.Today;
    private string _amountText = "0";
    private string _note = string.Empty;
    private string _statusMessage = "전표를 선택하고 수금 정보를 입력하세요.";
    private bool _isBusy;

    public PaymentDraftViewModel(GeoraePlanApiClient api, SyncCoordinator syncCoordinator)
    {
        _api = api;
        _syncCoordinator = syncCoordinator;
        LoadCommand = new AsyncCommand(LoadAsync);
        SaveDraftCommand = new AsyncCommand(SaveDraftAsync);
    }

    public ObservableCollection<InvoiceDto> Invoices { get; } = new();

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

    public AsyncCommand LoadCommand { get; }
    public AsyncCommand SaveDraftCommand { get; }

    public async Task LoadAsync()
    {
        if (IsBusy || Invoices.Count > 0)
            return;

        try
        {
            IsBusy = true;
            StatusMessage = "전표 목록 로딩 중...";

            var invoices = await _api.GetInvoicesAsync(null);
            Invoices.Clear();
            foreach (var invoice in invoices.OrderByDescending(x => x.InvoiceDate))
                Invoices.Add(invoice);

            StatusMessage = $"전표 {Invoices.Count}건 로드 완료";
        }
        catch (Exception ex)
        {
            StatusMessage = $"전표 목록 로드 실패: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
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
            StatusMessage = "수금 임시저장 중...";
            await _syncCoordinator.QueuePaymentDraftAsync(payment);
            var state = await _syncCoordinator.PushAsync();

            StatusMessage = string.IsNullOrWhiteSpace(state.LastError)
                ? "수금 저장 및 서버 반영 완료"
                : $"임시저장 완료(대기열 보관): {state.LastError}";
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
