namespace 거래플랜.Desktop.App.Data;

public sealed class LocalSyncOutboxEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string MutationId { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string EntityName { get; set; } = string.Empty;
    public Guid EntityId { get; set; }
    public long ExpectedRevision { get; set; }
    public string TenantCode { get; set; } = string.Empty;
    public string OfficeCode { get; set; } = string.Empty;
    public string ResponsibleOfficeCode { get; set; } = string.Empty;
    public string Status { get; set; } = "Prepared";
    public string ErrorMessage { get; set; } = string.Empty;
    public DateTime PreparedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? SentAtUtc { get; set; }
    public DateTime? AcknowledgedAtUtc { get; set; }
}
