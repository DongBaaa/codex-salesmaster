using System.Collections.ObjectModel;
using GeoraePlan.Mobile.App.Services;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.ViewModels;

public sealed class InvoiceDraftViewModel : ObservableObject
{
    private readonly GeoraePlanApiClient _api;
    private readonly SyncCoordinator _syncCoordinator;

    private CustomerDto? _selectedCustomer;
    private ItemDto? _selectedItem;
    private DateTime _invoiceDate = DateTime.Today;
    private string _quantityText = "1";
    private string _unitPriceText = "0";
    private string _memo = string.Empty;
    private string _statusMessage = "거래처/품목을 선택한 뒤 임시저장하세요.";
    private bool _isBusy;

    public InvoiceDraftViewModel(GeoraePlanApiClient api, SyncCoordinator syncCoordinator)
    {
        _api = api;
        _syncCoordinator = syncCoordinator;
        LoadCommand = new AsyncCommand(LoadAsync);
        SaveDraftCommand = new AsyncCommand(SaveDraftAsync);
    }

    public ObservableCollection<CustomerDto> Customers { get; } = new();
    public ObservableCollection<ItemDto> Items { get; } = new();

    public CustomerDto? SelectedCustomer
    {
        get => _selectedCustomer;
        set => SetProperty(ref _selectedCustomer, value);
    }

    public ItemDto? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (SetProperty(ref _selectedItem, value) && value is not null && string.IsNullOrWhiteSpace(Memo))
            {
                Memo = value.Notes;
            }
        }
    }

    public DateTime InvoiceDate
    {
        get => _invoiceDate;
        set => SetProperty(ref _invoiceDate, value);
    }

    public string QuantityText
    {
        get => _quantityText;
        set => SetProperty(ref _quantityText, value);
    }

    public string UnitPriceText
    {
        get => _unitPriceText;
        set => SetProperty(ref _unitPriceText, value);
    }

    public string Memo
    {
        get => _memo;
        set => SetProperty(ref _memo, value);
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
        if (IsBusy || (Customers.Count > 0 && Items.Count > 0))
            return;

        try
        {
            IsBusy = true;
            StatusMessage = "기초 목록 로딩 중...";

            var customers = await _api.GetCustomersAsync(null);
            var items = await _api.GetItemsAsync(null);

            Customers.Clear();
            foreach (var customer in customers.OrderBy(x => x.NameOriginal))
                Customers.Add(customer);

            Items.Clear();
            foreach (var item in items.OrderBy(x => x.NameOriginal))
                Items.Add(item);

            StatusMessage = $"거래처 {Customers.Count} / 품목 {Items.Count} 로드 완료";
        }
        catch (Exception ex)
        {
            StatusMessage = $"목록 로드 실패: {ex.Message}";
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

        if (SelectedCustomer is null)
        {
            StatusMessage = "거래처를 선택하세요.";
            return;
        }

        if (!decimal.TryParse(QuantityText, out var quantity) || quantity <= 0)
        {
            StatusMessage = "수량을 올바르게 입력하세요.";
            return;
        }

        if (!decimal.TryParse(UnitPriceText, out var unitPrice) || unitPrice < 0)
        {
            StatusMessage = "단가를 올바르게 입력하세요.";
            return;
        }

        var now = DateTime.UtcNow;
        var invoiceId = Guid.NewGuid();
        var total = quantity * unitPrice;
        var selectedItem = SelectedItem;

        var invoice = new InvoiceDto
        {
            Id = invoiceId,
            CustomerId = SelectedCustomer.Id,
            InvoiceNumber = string.Empty,
            LocalTempNumber = $"M-{DateTime.Now:yyyyMMdd-HHmmss}",
            VoucherType = VoucherType.Sales,
            InvoiceDate = DateOnly.FromDateTime(InvoiceDate),
            TotalAmount = total,
            SupplyAmount = total,
            VatAmount = 0,
            Memo = Memo.Trim(),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Revision = 0,
            IsDeleted = false,
            Lines =
            [
                new InvoiceLineDto
                {
                    Id = Guid.NewGuid(),
                    InvoiceId = invoiceId,
                    ItemId = selectedItem?.Id,
                    ItemNameOriginal = selectedItem?.NameOriginal ?? "직접입력 품목",
                    SpecificationOriginal = selectedItem?.SpecificationOriginal ?? string.Empty,
                    Unit = selectedItem?.Unit ?? "EA",
                    Quantity = quantity,
                    UnitPrice = unitPrice,
                    LineAmount = total,
                    Remark = Memo.Trim()
                }
            ]
        };

        try
        {
            IsBusy = true;
            StatusMessage = "전표 임시저장 중...";
            await _syncCoordinator.QueueInvoiceDraftAsync(invoice);
            var state = await _syncCoordinator.PushAsync();

            StatusMessage = string.IsNullOrWhiteSpace(state.LastError)
                ? "전표 저장 및 서버 반영 완료"
                : $"임시저장 완료(대기열 보관): {state.LastError}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"전표 저장 실패: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
