using 거래플랜.Desktop.App.Data;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.ViewModels;

/// <summary>
/// Lightweight row model for the invoice list DataGrid.
/// </summary>
public sealed class InvoiceListRow
{
    public Guid Id { get; init; }
    public string InvoiceNumber { get; init; } = string.Empty;
    public string LocalTempNumber { get; init; } = string.Empty;
    public DateOnly InvoiceDate { get; init; }
    public string CustomerName { get; init; } = string.Empty;
    public VoucherType VoucherType { get; init; }
    public decimal TotalAmount { get; init; }
    public decimal SupplyAmount { get; init; }
    public decimal VatAmount { get; init; }
    public decimal ReceiptAmount { get; init; }
    public decimal PaymentAmount { get; init; }
    public decimal BalanceAmount => TotalAmount - (VoucherType == VoucherType.Purchase ? PaymentAmount : ReceiptAmount);
    public bool TaxInvoiceIssued { get; init; }
    public bool IsDirty { get; init; }

    public string DisplayNumber => string.IsNullOrEmpty(InvoiceNumber) ? LocalTempNumber : InvoiceNumber;
    public string InvoiceDateDisplay => InvoiceDate.ToString("yyyy/MM/dd");
    public string TaxInvoiceDisplay => TaxInvoiceIssued ? "발행완료" : string.Empty;

    public string VoucherTypeDisplay => VoucherType switch
    {
        VoucherType.Sales       => "매출",
        VoucherType.Purchase    => "매입",
        VoucherType.Procurement => "발주",
        VoucherType.Expense     => "경비",
        VoucherType.Collection  => "수금",
        _                       => VoucherType.ToString()
    };

    public static InvoiceListRow From(LocalInvoice inv, string customerName)
    {
        var settledAmount = inv.Payments.Where(payment => !payment.IsDeleted).Sum(payment => payment.Amount);
        return new InvoiceListRow
        {
            Id = inv.Id,
            InvoiceNumber = inv.InvoiceNumber,
            LocalTempNumber = inv.LocalTempNumber,
            InvoiceDate = inv.InvoiceDate,
            CustomerName = customerName,
            VoucherType = inv.VoucherType,
            TotalAmount = inv.TotalAmount,
            SupplyAmount = inv.SupplyAmount,
            VatAmount = inv.VatAmount,
            ReceiptAmount = inv.VoucherType == VoucherType.Sales ? settledAmount : 0m,
            PaymentAmount = inv.VoucherType == VoucherType.Purchase ? settledAmount : 0m,
            TaxInvoiceIssued = inv.TaxInvoiceIssued,
            IsDirty = inv.IsDirty
        };
    }
}
