namespace 거래플랜.Desktop.App.Services;

public sealed class InvoiceSettlementSummary
{
    public decimal InvoiceTotal { get; init; }
    public decimal SettledAmount { get; init; }
    public decimal RemainingAmount { get; init; }
}

public sealed class RentalSettlementSummary
{
    public decimal BilledAmount { get; init; }
    public decimal SettledAmount { get; init; }
    public decimal OutstandingAmount { get; init; }
    public string BillingStatus { get; init; } = PaymentFlowConstants.BillingStatusPlanned;
    public string SettlementStatus { get; init; } = PaymentFlowConstants.SettlementStatusUnpaid;
    public string CompletionStatus { get; init; } = PaymentFlowConstants.CompletionPending;
}

public sealed class CustomerFinancialSummary
{
    public decimal AdvanceBalance { get; init; }
    public decimal ReceivableAmount { get; init; }
    public decimal PayableAmount { get; init; }
    public decimal PrepaymentAmount { get; init; }
}
