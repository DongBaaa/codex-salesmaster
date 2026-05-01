using 거래플랜.Desktop.App.Data;

namespace 거래플랜.Desktop.App.Services;

public enum PeriodLedgerType
{
    SalesPurchase = 0,
    SalesOnly = 1,
    PurchaseOnly = 2,
    ReceiptPayment = 3,
    YeonsuDelivery = 4
}

public enum PeriodLedgerScope
{
    SingleCustomer = 0,
    AllCustomers = 1
}

public enum PeriodLedgerMemoSource
{
    None = 0,
    Invoice = 1,
    Payment = 2,
    Transaction = 3
}

public sealed class PeriodLedgerQuery
{
    public DateOnly From { get; init; }
    public DateOnly To { get; init; }
    public PeriodLedgerType LedgerType { get; init; }
    public PeriodLedgerScope Scope { get; init; } = PeriodLedgerScope.SingleCustomer;
    public Guid? CustomerId { get; init; }
    public bool SortByCustomerName { get; init; }
    public bool IncludeProfit { get; init; }
    public string SearchText { get; init; } = string.Empty;
}

public sealed class PeriodLedgerBuildResult
{
    public required PeriodLedgerQuery Query { get; init; }
    public required string Title { get; init; }
    public required string ScopeLabel { get; init; }
    public required IReadOnlyList<PeriodLedgerCustomerBlock> Blocks { get; init; }
    public required IReadOnlyList<PeriodLedgerPaymentRow> PaymentRows { get; init; }
    public required IReadOnlyList<PeriodLedgerYeonsuDeliveryRow> YeonsuDeliveryRows { get; init; }
    public required PeriodLedgerTotals Totals { get; init; }
    public string? ProfitWarningMessage { get; init; }
}

public sealed class PeriodLedgerYeonsuDeliveryRow
{
    public Guid InvoiceId { get; init; }
    public int No { get; init; }
    public required DateOnly DeliveryDate { get; init; }
    public required string CustomerName { get; init; }
    public required string ItemSummary { get; init; }
    public decimal TotalAmount { get; init; }
    public required string WarehouseName { get; init; }
    public required string Note { get; init; }
    public required string LastSavedBy { get; init; }
    public DateTime LastSavedAtUtc { get; init; }
}

public sealed class PeriodLedgerCustomerBlock
{
    public required Guid CustomerId { get; init; }
    public required string CustomerName { get; init; }
    public required IReadOnlyList<PeriodLedgerRow> Rows { get; init; }
    public required PeriodLedgerTotals Totals { get; init; }
    public DateOnly? LatestDate { get; init; }
}

public sealed class PeriodLedgerRow
{
    public required DateOnly Date { get; init; }
    public required string Division { get; init; }
    public required string Summary { get; init; }
    public decimal TradeAmount { get; init; }
    public decimal ReceiptAmount { get; init; }
    public decimal PaymentAmount { get; init; }
    public decimal RunningBalance { get; init; }
    public decimal ReceivableBalance { get; init; }
    public decimal? ProfitAmount { get; init; }
    public required string Note { get; init; }
    public bool IsInvoiceSummary { get; init; }
    public bool IsSubTotal { get; init; }
    public Guid? InvoiceId { get; init; }
    public Guid? PaymentId { get; init; }
    public Guid? TransactionId { get; init; }
    public PeriodLedgerMemoSource MemoSource { get; init; } = PeriodLedgerMemoSource.None;
    public decimal? SubTotalQuantity { get; init; }
    public decimal? SubTotalAmount { get; init; }
    public decimal? SubTotalVat { get; init; }
    public required IReadOnlyList<PeriodLedgerItemRow> Items { get; init; }
}

public sealed class PeriodLedgerItemRow
{
    public Guid LineId { get; init; }
    public required string ItemName { get; init; }
    public required string Specification { get; init; }
    public decimal Quantity { get; init; }
    public decimal UnitPrice { get; init; }
    public decimal SupplyAmount => LineAmount - VatAmount;
    public decimal LineAmount { get; init; }
    public decimal VatAmount { get; init; }
    public required string ItemNote { get; init; }
}

public sealed class PeriodLedgerPaymentRow
{
    public int No { get; init; }
    public required DateOnly Date { get; init; }
    public required string Division { get; init; }
    public required string Summary { get; init; }
    public decimal TradeAmount { get; init; }
    public decimal ReceiptAmount { get; init; }
    public decimal PaymentAmount { get; init; }
    public decimal RunningBalance { get; init; }
    public decimal ReceivableBalance { get; init; }
    public required string CustomerName { get; init; }
    public required string Note { get; init; }
    public Guid? InvoiceId { get; init; }
    public Guid? PaymentId { get; init; }
    public Guid? TransactionId { get; init; }
    public PeriodLedgerMemoSource MemoSource { get; init; } = PeriodLedgerMemoSource.None;
}

public sealed class PeriodLedgerTotals
{
    public decimal TradeAmount { get; init; }
    public decimal ReceiptAmount { get; init; }
    public decimal PaymentAmount { get; init; }
    public decimal RunningBalance { get; init; }
    public decimal ReceivableBalance { get; init; }
    public decimal? ProfitAmount { get; init; }
}

internal sealed class PeriodLedgerRawEvent
{
    public required Guid CustomerId { get; init; }
    public required string CustomerName { get; init; }
    public required DateOnly Date { get; init; }
    public required string Division { get; init; }
    public required string Summary { get; init; }
    public decimal TradeAmount { get; init; }
    public decimal ReceiptAmount { get; init; }
    public decimal PaymentAmount { get; init; }
    public decimal? ProfitAmount { get; init; }
    public required string Note { get; init; }
    public bool IsInvoiceSummary { get; init; }
    public Guid? InvoiceId { get; init; }
    public Guid? PaymentId { get; init; }
    public Guid? TransactionId { get; init; }
    public PeriodLedgerMemoSource MemoSource { get; init; } = PeriodLedgerMemoSource.None;
    public required IReadOnlyList<PeriodLedgerItemRow> Items { get; init; }
}

internal sealed class PeriodPaymentEvent
{
    public required Guid CustomerId { get; init; }
    public required string CustomerName { get; init; }
    public required DateOnly Date { get; init; }
    public required string Division { get; init; }
    public required string Summary { get; init; }
    public decimal TradeAmount { get; init; }
    public decimal ReceiptAmount { get; init; }
    public decimal PaymentAmount { get; init; }
    public required string Note { get; init; }
    public required string DedupKey { get; init; }
    public int Priority { get; init; }
    public Guid? InvoiceId { get; init; }
    public Guid? PaymentId { get; init; }
    public Guid? TransactionId { get; init; }
    public PeriodLedgerMemoSource MemoSource { get; init; } = PeriodLedgerMemoSource.None;
}

internal sealed class PeriodProfitContext
{
    public required Dictionary<string, decimal> PurchaseAverageByItemKey { get; init; }
    public bool MissingCostDataFound { get; set; }
}
