namespace 거래플랜.Desktop.App.Services;

public sealed class CustomerContractSummaryItem
{
    public Guid CustomerId { get; init; }
    public int ContractCount { get; init; }
    public int RegisteredFileCount { get; init; }
    public int DraftCount => Math.Max(0, ContractCount - RegisteredFileCount);
    public int MissingExpireDateCount { get; init; }
    public DateOnly? NearestExpireDate { get; init; }
    public int ExpiringSoonCount { get; init; }
    public bool HasExpiredContract { get; init; }
}

public static class CustomerContractSummaryFormatter
{
    public static string Format(
        IEnumerable<CustomerContractSummaryItem> summaries,
        IReadOnlyCollection<CustomerContractAlertItem> alerts,
        int alertWindowDays)
    {
        var rows = summaries.ToList();
        if (rows.Sum(row => row.ContractCount) == 0)
            return "등록된 계약 정보가 없습니다.";

        var parts = new List<string> { $"PDF 등록 {rows.Count(row => row.RegisteredFileCount > 0):N0}곳" };
        var drafts = rows.Sum(row => row.DraftCount);
        var undated = rows.Sum(row => row.MissingExpireDateCount);
        if (drafts > 0)
            parts.Add($"초안 {drafts:N0}건");
        if (undated > 0)
            parts.Add($"만료일 미입력 {undated:N0}건");
        if (alerts.Count > 0)
        {
            parts.Add($"만료 {alerts.Count(alert => alert.DaysRemaining < 0):N0}건");
            parts.Add($"{Math.Max(alertWindowDays, 0):N0}일 내 {alerts.Count(alert => alert.DaysRemaining >= 0):N0}건");
        }
        else if (undated == 0)
        {
            parts.Add("임박 알림 없음");
        }
        return string.Join(" · ", parts);
    }
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
