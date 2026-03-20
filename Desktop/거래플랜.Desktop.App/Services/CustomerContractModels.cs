namespace 거래플랜.Desktop.App.Services;

public sealed class CustomerContractSummaryItem
{
    public Guid CustomerId { get; init; }
    public int ContractCount { get; init; }
    public DateOnly? NearestExpireDate { get; init; }
    public int ExpiringSoonCount { get; init; }
    public bool HasExpiredContract { get; init; }
}

public sealed class CustomerContractAlertItem
{
    public Guid CustomerId { get; init; }
    public Guid ContractId { get; init; }
    public string CustomerName { get; init; } = string.Empty;
    public string ContractType { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public DateOnly ExpireDate { get; init; }
    public int DaysRemaining { get; init; }
    public string AlertLevel { get; init; } = string.Empty;
    public string AlertText { get; init; } = string.Empty;
}
