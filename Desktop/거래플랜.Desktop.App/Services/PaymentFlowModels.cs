namespace 거래플랜.Desktop.App.Services;

public sealed class InvoiceSettlementSummary
{
    public decimal? InvoiceTotal { get; init; }
    public decimal? SettledAmount { get; init; }
    public decimal? RemainingAmount { get; init; }
}

public sealed class RentalSettlementSummary
{
    public decimal? BilledAmount { get; init; }
    public decimal? SettledAmount { get; init; }
    public decimal? OutstandingAmount { get; init; }
    public string BillingStatus { get; init; } = "비공개";
    public string SettlementStatus { get; init; } = "비공개";
    public string CompletionStatus { get; init; } = "비공개";
}

public sealed class CustomerFinancialSummary
{
    public decimal? AdvanceBalance { get; init; }
    public decimal? ReceivableAmount { get; init; }
    public decimal? PayableAmount { get; init; }
    public decimal? PrepaymentAmount { get; init; }
    public decimal? PrepaidAmount { get; init; }
}
