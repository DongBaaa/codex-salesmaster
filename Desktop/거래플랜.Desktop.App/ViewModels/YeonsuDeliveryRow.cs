namespace 거래플랜.Desktop.App.ViewModels;

public sealed class YeonsuDeliveryRow
{
    public Guid InvoiceId { get; init; }
    public Guid InvoiceLineId { get; init; }
    public DateOnly TradeDate { get; init; }
    public string TradeDateDisplay => TradeDate.ToString("yyyy-MM-dd");
    public string EntryCategory { get; init; } = string.Empty;
    public string CustomerName { get; init; } = string.Empty;
    public string ModelName { get; init; } = string.Empty;
    public string TradeDescription { get; init; } = string.Empty;
    public decimal PurchaseAmount { get; init; }
    public decimal SalesAmount { get; init; }
    public decimal ProfitAmount { get; init; }
    public decimal FeeAmount { get; init; }
    public string Note { get; init; } = string.Empty;
}
