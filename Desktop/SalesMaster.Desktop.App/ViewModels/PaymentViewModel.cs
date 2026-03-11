using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SalesMaster.Desktop.App.Data;
using SalesMaster.Desktop.App.Services;

namespace SalesMaster.Desktop.App.ViewModels;

public sealed partial class PaymentViewModel : ObservableObject
{
    private readonly LocalStateService _local;
    private readonly SessionState _session;
    private List<LocalCustomer> _allCustomers = new();

    [ObservableProperty] private LocalCustomer? _selectedCustomer;
    [ObservableProperty] private string _customerName = "거래처 선택";
    [ObservableProperty] private string _customerPhone = "-";
    [ObservableProperty] private string _customerCategory = "-";
    [ObservableProperty] private string _customerDepartment = "-";
    [ObservableProperty] private string _customerContactPerson = "-";

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

    public PaymentViewModel(LocalStateService local, SessionState session)
    {
        _local = local;
        _session = session;
    }

    public async Task LoadAsync(LocalCustomer? preselect = null)
    {
        _allCustomers = await _local.GetCustomersAsync(_session);
        if (preselect is not null)
            SetCustomer(preselect);
        else
            ResetCustomerDisplay();

        NewEntry();
    }

    public LocalStateService LocalStateService => _local;
    public SessionState SessionState => _session;
    public List<LocalCustomer> GetAllCustomers() => _allCustomers;

    public async Task ReloadCustomersAsync()
    {
        _allCustomers = await _local.GetCustomersAsync(_session);
    }

    public void SetCustomer(LocalCustomer c)
    {
        SelectedCustomer = c;
        CustomerName = c.NameOriginal;
        CustomerPhone = string.IsNullOrWhiteSpace(c.Phone) ? "-" : c.Phone;
        CustomerCategory = string.IsNullOrWhiteSpace(c.PriceGrade) ? "-" : c.PriceGrade;
        CustomerDepartment = string.IsNullOrWhiteSpace(c.Department) ? "-" : c.Department;
        CustomerContactPerson = string.IsNullOrWhiteSpace(c.ContactPerson) ? "-" : c.ContactPerson;
        _ = LoadHistoryAsync(c.Id);
    }

    private async Task LoadHistoryAsync(Guid customerId)
    {
        var list = await _local.GetTransactionsAsync(customerId, _session);
        History.Clear();
        foreach (var t in list
                     .OrderByDescending(t => t.TransactionDate)
                     .ThenByDescending(t => t.UpdatedAtUtc))
            History.Add(t);
    }

    private void ResetCustomerDisplay()
    {
        CustomerName = "거래처 선택";
        CustomerPhone = "-";
        CustomerCategory = "-";
        CustomerDepartment = "-";
        CustomerContactPerson = "-";
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

        var result = await _local.SaveTransactionAsync(t, _session);
        if (!result.Success)
        {
            StatusMessage = result.Message;
            return;
        }

        await LoadHistoryAsync(SelectedCustomer.Id);
        NewEntry();
        StatusMessage = result.Message;
    }
}
