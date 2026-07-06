using 거래플랜.Desktop.App.Data;
using System.Globalization;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.ViewModels;

/// <summary>
/// Lightweight row model for the invoice list DataGrid.
/// </summary>
public sealed class InvoiceListRow
{
    public Guid Id { get; init; }
    public Guid? TransactionId { get; init; }
    public bool IsTransactionRow { get; init; }
    public Guid VersionGroupId { get; init; }
    public Guid CustomerId { get; init; }
    public string InvoiceNumber { get; init; } = string.Empty;
    public string LocalTempNumber { get; init; } = string.Empty;
    public string TaxInvoiceNumber { get; init; } = string.Empty;
    public DateOnly InvoiceDate { get; init; }
    public string CustomerName { get; init; } = string.Empty;
    public string FirstItemSummary { get; init; } = string.Empty;
    public string PrimaryColumnText { get; init; } = string.Empty;
    public string ResponsibleOfficeCode { get; init; } = string.Empty;
    public Guid? LinkedRentalBillingProfileId { get; init; }
    public Guid? LinkedRentalBillingRunId { get; init; }
    public VoucherType VoucherType { get; init; }
    public decimal TotalAmount { get; init; }
    public decimal SupplyAmount { get; init; }
    public decimal VatAmount { get; init; }
    public string VatMode { get; init; } = InvoiceVatModes.Included;
    public decimal ReceiptAmount { get; init; }
    public decimal PaymentAmount { get; init; }
    public decimal? BalanceAmountOverride { get; init; }
    public decimal BalanceAmount => BalanceAmountOverride ?? (TotalAmount - (VoucherType == VoucherType.Purchase ? PaymentAmount : ReceiptAmount));
    public string? VoucherTypeDisplayOverride { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
    public bool IsRentalBillingInvoice =>
        LinkedRentalBillingProfileId is Guid profileId && profileId != Guid.Empty ||
        LinkedRentalBillingRunId is Guid runId && runId != Guid.Empty;
    public bool IsSettlementInvoice => VoucherType is VoucherType.Sales or VoucherType.Purchase;
    public bool IsBalanceCleared => IsSettlementInvoice && BalanceAmount == 0m;
    public bool TaxInvoiceIssued { get; init; }
    public bool PurchaseReceivingRequired { get; init; }
    public string PurchaseReceivingStatus { get; init; } = InvoiceReceivingStatuses.NotApplicable;
    public bool IsDirty { get; init; }
    public long Revision { get; init; }

    public string DisplayNumber => string.IsNullOrEmpty(InvoiceNumber) ? LocalTempNumber : InvoiceNumber;
    public Guid EffectiveVersionGroupId => VersionGroupId == Guid.Empty ? Id : VersionGroupId;
    public string InvoiceDateDisplay => InvoiceDate.ToString("yyyy/MM/dd");
    public string TaxInvoiceDisplay => TaxInvoiceIssued || !string.IsNullOrWhiteSpace(TaxInvoiceNumber)
        ? "발행"
        : string.Empty;
    public string SupplyAmountDisplay => IsTransactionRow ? string.Empty : FormatAmount(SupplyAmount);
    public string VatAmountDisplay => IsTransactionRow ? string.Empty : FormatAmount(VatAmount);
    public string TotalAmountDisplay => IsTransactionRow ? string.Empty : FormatAmount(TotalAmount);
    public string ReceiptAmountDisplay => IsTransactionRow && ReceiptAmount == 0m ? string.Empty : FormatAmount(ReceiptAmount);
    public string PaymentAmountDisplay => IsTransactionRow && PaymentAmount == 0m ? string.Empty : FormatAmount(PaymentAmount);
    public string BalanceAmountDisplay => IsTransactionRow && BalanceAmount == 0m ? string.Empty : FormatAmount(BalanceAmount);
    public string PurchaseReceivingDisplay => !IsTransactionRow && VoucherType == VoucherType.Purchase
        ? InvoiceReceivingStatuses.Normalize(PurchaseReceivingStatus, true, PurchaseReceivingRequired)
        : string.Empty;

    public string VoucherTypeDisplay
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(VoucherTypeDisplayOverride))
                return VoucherTypeDisplayOverride;

            var display = VoucherType switch
            {
                VoucherType.Sales       => "매출",
                VoucherType.Purchase    => "매입",
                VoucherType.Procurement => "발주",
                VoucherType.Expense     => "경비",
                VoucherType.Collection  => "수금",
                _                       => VoucherType.ToString()
            };

            return IsRentalBillingInvoice ? $"{display}(청구서)" : display;
        }
    }

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
            TaxInvoiceNumber = inv.TaxInvoiceNumber,
            InvoiceDate = inv.InvoiceDate,
            CustomerName = customerName,
            FirstItemSummary = firstItemSummary,
            PrimaryColumnText = showCustomerName ? customerName : firstItemSummary,
            ResponsibleOfficeCode = inv.ResponsibleOfficeCode,
            LinkedRentalBillingProfileId = inv.LinkedRentalBillingProfileId,
            LinkedRentalBillingRunId = inv.LinkedRentalBillingRunId,
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
            Revision = inv.Revision,
            UpdatedAtUtc = inv.UpdatedAtUtc
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
            TaxInvoiceNumber = summary.TaxInvoiceNumber,
            InvoiceDate = summary.InvoiceDate,
            CustomerName = customerName,
            FirstItemSummary = firstItemSummary,
            PrimaryColumnText = showCustomerName ? customerName : firstItemSummary,
            ResponsibleOfficeCode = summary.ResponsibleOfficeCode,
            LinkedRentalBillingProfileId = summary.LinkedRentalBillingProfileId,
            LinkedRentalBillingRunId = summary.LinkedRentalBillingRunId,
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
            Revision = summary.Revision,
            UpdatedAtUtc = summary.UpdatedAtUtc
        };
    }

    public static InvoiceListRow From(LocalTransaction transaction, string customerName, bool showCustomerName)
    {
        var isPayment = transaction.PaymentTotal > 0m && transaction.ReceiptTotal <= 0m;
        var amount = isPayment ? transaction.PaymentTotal : transaction.ReceiptTotal;
        var entryText = isPayment ? "지불 입력" : "수금 입력";
        var primaryText = showCustomerName
            ? (string.IsNullOrWhiteSpace(customerName) ? entryText : $"{customerName} · {entryText}")
            : entryText;

        return new InvoiceListRow
        {
            Id = transaction.Id,
            TransactionId = transaction.Id,
            IsTransactionRow = true,
            VersionGroupId = transaction.Id,
            CustomerId = transaction.CustomerId,
            InvoiceNumber = string.Empty,
            LocalTempNumber = string.Empty,
            TaxInvoiceNumber = string.Empty,
            InvoiceDate = transaction.TransactionDate,
            CustomerName = customerName,
            FirstItemSummary = entryText,
            PrimaryColumnText = primaryText,
            ResponsibleOfficeCode = transaction.ResponsibleOfficeCode,
            LinkedRentalBillingProfileId = transaction.LinkedRentalBillingProfileId,
            LinkedRentalBillingRunId = transaction.LinkedRentalBillingRunId,
            VoucherType = VoucherType.Collection,
            TotalAmount = 0m,
            SupplyAmount = 0m,
            VatAmount = 0m,
            ReceiptAmount = isPayment ? 0m : transaction.ReceiptTotal,
            PaymentAmount = isPayment ? transaction.PaymentTotal : 0m,
            BalanceAmountOverride = amount,
            VoucherTypeDisplayOverride = ResolveTransactionMethodDisplay(transaction, isPayment),
            IsDirty = transaction.IsDirty,
            Revision = transaction.Revision,
            UpdatedAtUtc = transaction.UpdatedAtUtc
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

    private static string ResolveTransactionMethodDisplay(LocalTransaction transaction, bool isPayment)
    {
        if (isPayment)
        {
            var labels = new List<string>(3);
            if (transaction.CashPayment > 0m)
                labels.Add("현금지급");
            if (transaction.CardPayment > 0m)
                labels.Add("카드지급");
            if (transaction.BankPayment > 0m)
                labels.Add("통장지급");

            return labels.Count switch
            {
                0 => "지급",
                1 => labels[0],
                _ => "혼합지급"
            };
        }

        var receiptLabels = new List<string>(3);
        if (transaction.CashReceipt > 0m)
            receiptLabels.Add("현금수금");
        if (transaction.CardReceipt > 0m)
            receiptLabels.Add("카드수금");
        if (transaction.BankReceipt > 0m)
            receiptLabels.Add("통장수금");

        return receiptLabels.Count switch
        {
            0 => "수금",
            1 => receiptLabels[0],
            _ => "혼합수금"
        };
    }

    private static string FormatAmount(decimal amount)
        => amount.ToString("N0", CultureInfo.CurrentCulture);
}
