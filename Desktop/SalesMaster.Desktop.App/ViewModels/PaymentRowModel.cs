using CommunityToolkit.Mvvm.ComponentModel;
using SalesMaster.Desktop.App.Data;

namespace SalesMaster.Desktop.App.ViewModels;

public sealed partial class PaymentRowModel : ObservableObject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InvoiceId { get; set; }

    [ObservableProperty] private DateOnly _paymentDate = DateOnly.FromDateTime(DateTime.Today);
    [ObservableProperty] private decimal _amount;
    [ObservableProperty] private string _note = string.Empty;

    public static PaymentRowModel FromLocal(LocalPayment p) => new()
    {
        Id = p.Id,
        InvoiceId = p.InvoiceId,
        PaymentDate = p.PaymentDate,
        Amount = p.Amount,
        Note = p.Note
    };

    public LocalPayment ToLocal() => new()
    {
        Id = Id,
        InvoiceId = InvoiceId,
        PaymentDate = PaymentDate,
        Amount = Amount,
        Note = Note
    };
}
