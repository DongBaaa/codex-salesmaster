using 거래플랜.Desktop.App.Data;

namespace 거래플랜.Desktop.App.Services;

// UI-only projection: undisclosed amounts are absent, never a numeric placeholder.
public sealed record PaymentHistoryRow
{
    public Guid Id { get; init; }
    public long Revision { get; init; }
    public DateOnly TransactionDate { get; init; }
    public string TransactionKindDisplay { get; init; } = string.Empty;
    public string LinkedInvoiceNumber { get; init; } = string.Empty;
    public string Note { get; init; } = string.Empty;
    public string Memo { get; init; } = string.Empty;
    public Guid? LinkedInvoiceId { get; init; }
    public Guid? LinkedRentalBillingProfileId { get; init; }
    public Guid? LinkedRentalBillingRunId { get; init; }
    public decimal? ReceiptTotal { get; init; }
    public decimal? PaymentTotal { get; init; }
    public decimal? SettlementAmount { get; init; }
    public bool AmountsHidden => !ReceiptTotal.HasValue || !PaymentTotal.HasValue || !SettlementAmount.HasValue;
    public string ReceiptTotalDisplay => ReceiptTotal?.ToString("N0") ?? "비공개";
    public string PaymentTotalDisplay => PaymentTotal?.ToString("N0") ?? "비공개";

    internal static PaymentHistoryRow From(LocalTransaction transaction, bool visible) => new()
    {
        Id = transaction.Id, Revision = transaction.Revision, TransactionDate = transaction.TransactionDate,
        TransactionKindDisplay = transaction.TransactionKindDisplay, LinkedInvoiceNumber = transaction.LinkedInvoiceNumber,
        Note = transaction.Note, Memo = transaction.Memo, LinkedInvoiceId = transaction.LinkedInvoiceId,
        LinkedRentalBillingProfileId = transaction.LinkedRentalBillingProfileId,
        LinkedRentalBillingRunId = transaction.LinkedRentalBillingRunId,
        ReceiptTotal = visible ? transaction.ReceiptTotal : null,
        PaymentTotal = visible ? transaction.PaymentTotal : null,
        SettlementAmount = visible ? transaction.SettlementAmount : null
    };
}
