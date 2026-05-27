using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Models;

public sealed class CustomerPaymentHistoryRow
{
    public Guid PaymentId { get; init; }
    public Guid InvoiceId { get; init; }
    public string InvoiceNumber { get; init; } = string.Empty;
    public DateOnly PaymentDate { get; init; }
    public decimal Amount { get; init; }
    public string Note { get; init; } = string.Empty;
    public int AttachmentCount { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
    public VoucherType VoucherType { get; init; } = VoucherType.Sales;

    public bool HasAttachments => AttachmentCount > 0;
    public string InvoiceDisplay => string.IsNullOrWhiteSpace(InvoiceNumber) ? "전표 미부여" : InvoiceNumber;
    public string AmountDisplay => $"{Amount:N0}원";
    public string ActionDisplay => VoucherType == VoucherType.Purchase ? "지급" : "수금";
    public string NoteDisplay => string.IsNullOrWhiteSpace(Note) ? "비고 없음" : Note;
    public string AttachmentSummary => AttachmentCount == 0 ? "첨부 없음" : $"첨부 {AttachmentCount:N0}건";

    public static CustomerPaymentHistoryRow From(InvoiceDto invoice, PaymentDto payment)
    {
        var invoiceNumber = string.IsNullOrWhiteSpace(invoice.InvoiceNumber)
            ? invoice.LocalTempNumber
            : invoice.InvoiceNumber;

        return new CustomerPaymentHistoryRow
        {
            PaymentId = payment.Id,
            InvoiceId = invoice.Id,
            InvoiceNumber = invoiceNumber,
            VoucherType = invoice.VoucherType,
            PaymentDate = payment.PaymentDate,
            Amount = payment.Amount,
            Note = payment.Note,
            AttachmentCount = payment.Attachments?.Count ?? 0,
            UpdatedAtUtc = payment.UpdatedAtUtc
        };
    }
}
