using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SalesMaster.Desktop.App.Data;
using SalesMaster.Desktop.App.Services;

namespace SalesMaster.Desktop.App.ViewModels;

public sealed partial class PaymentViewModel : ObservableObject
{
    private readonly LocalStateService _local;
    private List<LocalCustomer> _allCustomers = new();

    [ObservableProperty] private LocalCustomer? _selectedCustomer;
    [ObservableProperty] private string _customerName = string.Empty;
    [ObservableProperty] private string _customerPhone = string.Empty;
    [ObservableProperty] private string _customerCategory = string.Empty;

    [ObservableProperty] private DateOnly _receiptDate = DateOnly.FromDateTime(DateTime.Today);
    [ObservableProperty] private decimal _cashReceipt;
    [ObservableProperty] private decimal _cardReceipt;
    [ObservableProperty] private decimal _bankReceipt;
    [ObservableProperty] private decimal _discountApplied;
    [ObservableProperty] private decimal _receiptTotal;

    [ObservableProperty] private DateOnly _paymentDate = DateOnly.FromDateTime(DateTime.Today);
    [ObservableProperty] private decimal _cashPayment;
    [ObservableProperty] private decimal _cardPayment;
    [ObservableProperty] private decimal _bankPayment;
    [ObservableProperty] private decimal _discountReceived;
    [ObservableProperty] private decimal _paymentTotal;

    [ObservableProperty] private string _note = string.Empty;
    [ObservableProperty] private string _memo = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public ObservableCollection<LocalTransaction> History { get; } = new();

    public PaymentViewModel(LocalStateService local)
    {
        _local = local;
    }

    public async Task LoadAsync(LocalCustomer? preselect = null)
    {
        _allCustomers = await _local.GetCustomersAsync();
        if (preselect is not null)
            SetCustomer(preselect);

        NewEntry();
    }

    public LocalStateService LocalStateService => _local;
    public List<LocalCustomer> GetAllCustomers() => _allCustomers;

    public async Task ReloadCustomersAsync()
    {
        _allCustomers = await _local.GetCustomersAsync();
    }

    public void SetCustomer(LocalCustomer c)
    {
        SelectedCustomer = c;
        CustomerName = c.NameOriginal;
        CustomerPhone = c.Phone;
        _ = LoadHistoryAsync(c.Id);
    }

    private async Task LoadHistoryAsync(Guid customerId)
    {
        var list = await _local.GetTransactionsAsync(customerId);
        History.Clear();
        foreach (var t in list)
            History.Add(t);
    }

    public void NewEntry()
    {
        ReceiptDate = PaymentDate = DateOnly.FromDateTime(DateTime.Today);
        CashReceipt = CardReceipt = BankReceipt = DiscountApplied = ReceiptTotal = 0;
        CashPayment = CardPayment = BankPayment = DiscountReceived = PaymentTotal = 0;
        Note = Memo = string.Empty;
    }

    partial void OnCashReceiptChanged(decimal value) => RecalcReceipt();
    partial void OnCardReceiptChanged(decimal value) => RecalcReceipt();
    partial void OnBankReceiptChanged(decimal value) => RecalcReceipt();
    partial void OnDiscountAppliedChanged(decimal value) => RecalcReceipt();

    private void RecalcReceipt() =>
        ReceiptTotal = CashReceipt + CardReceipt + BankReceipt - DiscountApplied;

    partial void OnCashPaymentChanged(decimal value) => RecalcPayment();
    partial void OnCardPaymentChanged(decimal value) => RecalcPayment();
    partial void OnBankPaymentChanged(decimal value) => RecalcPayment();
    partial void OnDiscountReceivedChanged(decimal value) => RecalcPayment();

    private void RecalcPayment() =>
        PaymentTotal = CashPayment + CardPayment + BankPayment - DiscountReceived;

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (SelectedCustomer is null)
        {
            StatusMessage = "거래처를 선택하세요.";
            return;
        }

        if (ReceiptTotal < 0 || PaymentTotal < 0)
        {
            StatusMessage = "수금결제/지불결제는 0 이상이어야 합니다.";
            return;
        }

        if (ReceiptTotal == 0 && PaymentTotal == 0)
        {
            StatusMessage = "수금 또는 지급 금액을 입력하세요.";
            return;
        }

        if (ReceiptTotal > 0 && PaymentTotal > 0)
        {
            StatusMessage = "수금과 지급이 동시에 입력되었습니다. 금액을 다시 확인하세요.";
            return;
        }

        var t = new LocalTransaction
        {
            CustomerId = SelectedCustomer.Id,
            TransactionDate = ReceiptDate,
            CashReceipt = CashReceipt,
            CardReceipt = CardReceipt,
            BankReceipt = BankReceipt,
            DiscountApplied = DiscountApplied,
            ReceiptTotal = ReceiptTotal,
            CashPayment = CashPayment,
            CardPayment = CardPayment,
            BankPayment = BankPayment,
            DiscountReceived = DiscountReceived,
            PaymentTotal = PaymentTotal,
            Note = Note,
            Memo = Memo,
        };

        await _local.SaveTransactionAsync(t);
        await LoadHistoryAsync(SelectedCustomer.Id);
        NewEntry();
        StatusMessage = "저장되었습니다.";
    }
}
