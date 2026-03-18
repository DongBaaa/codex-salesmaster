using SalesMaster.Desktop.App.Data;
using SalesMaster.Shared.Contracts;

namespace SalesMaster.Desktop.App.ViewModels;

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
    public decimal PaidAmount { get; init; }
    public decimal BalanceAmount => TotalAmount - PaidAmount;
    public bool IsDirty { get; init; }

    public string DisplayNumber => string.IsNullOrEmpty(InvoiceNumber) ? LocalTempNumber : InvoiceNumber;
    public string InvoiceDateDisplay => InvoiceDate.ToString("yyyy/MM/dd");
    public string TaxInvoiceDisplay => VoucherType switch
    {
        VoucherType.Sales => "세금계산서",
        VoucherType.Purchase => "계산서",
        _ => "-"
    };

    public string VoucherTypeDisplay => VoucherType switch
    {
        VoucherType.Sales       => "매출",
        VoucherType.Purchase    => "매입",
        VoucherType.Procurement => "발주",
        VoucherType.Expense     => "경비",
        VoucherType.Collection  => "수금",
        _                       => VoucherType.ToString()
    };

    public static InvoiceListRow From(LocalInvoice inv, string customerName) => new()
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
        PaidAmount = inv.Payments.Where(p => !p.IsDeleted).Sum(p => p.Amount),
        IsDirty = inv.IsDirty
    };
}
