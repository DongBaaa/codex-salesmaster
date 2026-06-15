using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.ViewModels;

/// <summary>
/// Lightweight row model for the invoice list DataGrid.
/// </summary>
public sealed class InvoiceListRow
{
    public Guid Id { get; init; }
    public Guid VersionGroupId { get; init; }
    public Guid CustomerId { get; init; }
    public string InvoiceNumber { get; init; } = string.Empty;
    public string LocalTempNumber { get; init; } = string.Empty;
    public DateOnly InvoiceDate { get; init; }
    public string CustomerName { get; init; } = string.Empty;
    public string FirstItemSummary { get; init; } = string.Empty;
    public string PrimaryColumnText { get; init; } = string.Empty;
    public string ResponsibleOfficeCode { get; init; } = string.Empty;
    public VoucherType VoucherType { get; init; }
    public decimal TotalAmount { get; init; }
    public decimal SupplyAmount { get; init; }
    public decimal VatAmount { get; init; }
    public string VatMode { get; init; } = InvoiceVatModes.Included;
    public decimal ReceiptAmount { get; init; }
    public decimal PaymentAmount { get; init; }
    public decimal BalanceAmount => TotalAmount - (VoucherType == VoucherType.Purchase ? PaymentAmount : ReceiptAmount);
    public bool TaxInvoiceIssued { get; init; }
    public bool PurchaseReceivingRequired { get; init; }
    public string PurchaseReceivingStatus { get; init; } = InvoiceReceivingStatuses.NotApplicable;
    public bool IsDirty { get; init; }
    public long Revision { get; init; }

    public string DisplayNumber => string.IsNullOrEmpty(InvoiceNumber) ? LocalTempNumber : InvoiceNumber;
    public Guid EffectiveVersionGroupId => VersionGroupId == Guid.Empty ? Id : VersionGroupId;
    public string InvoiceDateDisplay => InvoiceDate.ToString("yyyy/MM/dd");
    public string TaxInvoiceDisplay => TaxInvoiceIssued ? "V" : string.Empty;
    public string PurchaseReceivingDisplay => VoucherType == VoucherType.Purchase
        ? InvoiceReceivingStatuses.Normalize(PurchaseReceivingStatus, true, PurchaseReceivingRequired)
        : string.Empty;

    public string VoucherTypeDisplay => VoucherType switch
    {
        VoucherType.Sales       => "매출",
        VoucherType.Purchase    => "매입",
        VoucherType.Procurement => "발주",
        VoucherType.Expense     => "경비",
        VoucherType.Collection  => "수금",
        _                       => VoucherType.ToString()
    };

    public static InvoiceListRow From(LocalInvoice inv, string customerName, bool showCustomerName)
    {
        var settledAmount = inv.Payments.Where(payment => !payment.IsDeleted).Sum(payment => payment.Amount);
        var firstItemSummary = BuildFirstItemSummary(inv);
        return new InvoiceListRow
        {
            Id = inv.Id,
            VersionGroupId = inv.VersionGroupId,
            CustomerId = inv.CustomerId,
            InvoiceNumber = inv.InvoiceNumber,
            LocalTempNumber = inv.LocalTempNumber,
            InvoiceDate = inv.InvoiceDate,
            CustomerName = customerName,
            FirstItemSummary = firstItemSummary,
            PrimaryColumnText = showCustomerName ? customerName : firstItemSummary,
            ResponsibleOfficeCode = inv.ResponsibleOfficeCode,
            VoucherType = inv.VoucherType,
            TotalAmount = inv.TotalAmount,
            SupplyAmount = inv.SupplyAmount,
            VatAmount = inv.VatAmount,
            VatMode = InvoiceVatModes.Normalize(inv.VatMode),
            ReceiptAmount = inv.VoucherType == VoucherType.Sales ? settledAmount : 0m,
            PaymentAmount = inv.VoucherType == VoucherType.Purchase ? settledAmount : 0m,
            TaxInvoiceIssued = inv.TaxInvoiceIssued,
            PurchaseReceivingRequired = inv.PurchaseReceivingRequired ||
                                        (inv.VoucherType == VoucherType.Purchase &&
                                         (InvoiceReceivingStatuses.IsConfirmed(inv.PurchaseReceivingStatus) ||
                                          string.IsNullOrWhiteSpace(inv.PurchaseReceivingStatus))),
            PurchaseReceivingStatus = InvoiceReceivingStatuses.Normalize(
                inv.PurchaseReceivingStatus,
                inv.VoucherType == VoucherType.Purchase,
                inv.PurchaseReceivingRequired ||
                (inv.VoucherType == VoucherType.Purchase &&
                 (InvoiceReceivingStatuses.IsConfirmed(inv.PurchaseReceivingStatus) ||
                  string.IsNullOrWhiteSpace(inv.PurchaseReceivingStatus)))),
            IsDirty = inv.IsDirty,
            Revision = inv.Revision
        };
    }

    public static InvoiceListRow From(LocalInvoiceListSummary summary, string customerName, bool showCustomerName)
    {
        var firstItemSummary = string.IsNullOrWhiteSpace(summary.FirstItemSummary)
            ? "(품목 없음)"
            : summary.FirstItemSummary;
        return new InvoiceListRow
        {
            Id = summary.Id,
            VersionGroupId = summary.VersionGroupId,
            CustomerId = summary.CustomerId,
            InvoiceNumber = summary.InvoiceNumber,
            LocalTempNumber = summary.LocalTempNumber,
            InvoiceDate = summary.InvoiceDate,
            CustomerName = customerName,
            FirstItemSummary = firstItemSummary,
            PrimaryColumnText = showCustomerName ? customerName : firstItemSummary,
            ResponsibleOfficeCode = summary.ResponsibleOfficeCode,
            VoucherType = summary.VoucherType,
            TotalAmount = summary.TotalAmount,
            SupplyAmount = summary.SupplyAmount,
            VatAmount = summary.VatAmount,
            VatMode = InvoiceVatModes.Normalize(summary.VatMode),
            ReceiptAmount = summary.VoucherType == VoucherType.Sales ? summary.SettledAmount : 0m,
            PaymentAmount = summary.VoucherType == VoucherType.Purchase ? summary.SettledAmount : 0m,
            TaxInvoiceIssued = summary.TaxInvoiceIssued,
            PurchaseReceivingRequired = summary.PurchaseReceivingRequired ||
                                        (summary.VoucherType == VoucherType.Purchase &&
                                         (InvoiceReceivingStatuses.IsConfirmed(summary.PurchaseReceivingStatus) ||
                                          string.IsNullOrWhiteSpace(summary.PurchaseReceivingStatus))),
            PurchaseReceivingStatus = InvoiceReceivingStatuses.Normalize(
                summary.PurchaseReceivingStatus,
                summary.VoucherType == VoucherType.Purchase,
                summary.PurchaseReceivingRequired ||
                (summary.VoucherType == VoucherType.Purchase &&
                 (InvoiceReceivingStatuses.IsConfirmed(summary.PurchaseReceivingStatus) ||
                  string.IsNullOrWhiteSpace(summary.PurchaseReceivingStatus)))),
            IsDirty = summary.IsDirty,
            Revision = summary.Revision
        };
    }

    public static string BuildFirstItemSummary(LocalInvoice invoice)
    {
        var activeLines = invoice.Lines
            .Where(line => !line.IsDeleted)
            .ToList();
        if (activeLines.Count == 0)
            return "(품목 없음)";

        var firstLabel = activeLines
            .Select(line => string.IsNullOrWhiteSpace(line.ItemNameOriginal) ? line.Remark : line.ItemNameOriginal)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?.Trim();
        if (string.IsNullOrWhiteSpace(firstLabel))
            firstLabel = "(품목 없음)";

        return activeLines.Count == 1
            ? firstLabel
            : $"{firstLabel} 외 {activeLines.Count - 1}건";
    }
}
