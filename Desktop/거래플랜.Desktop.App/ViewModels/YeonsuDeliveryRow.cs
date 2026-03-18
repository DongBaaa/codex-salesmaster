namespace 거래플랜.Desktop.App.ViewModels;

public sealed class YeonsuDeliveryRow
{
    public Guid InvoiceId { get; init; }
    public Guid CustomerId { get; init; }
    public DateOnly DeliveryDate { get; init; }
    public string DeliveryDateDisplay => DeliveryDate.ToString("yyyy-MM-dd");
    public string CustomerName { get; init; } = string.Empty;
    public string ItemSummary { get; init; } = string.Empty;
    public decimal TotalAmount { get; init; }
    public string WarehouseCode { get; init; } = string.Empty;
    public string WarehouseDisplay { get; init; } = string.Empty;
    public string Memo { get; init; } = string.Empty;
    public string LastSavedBy { get; init; } = string.Empty;
    public DateTime LastSavedAtUtc { get; init; }
    public string LastSavedAtDisplay => LastSavedAtUtc == default
        ? string.Empty
        : LastSavedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public int VersionNumber { get; init; }
}
