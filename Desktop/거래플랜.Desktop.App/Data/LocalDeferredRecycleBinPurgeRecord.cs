namespace 거래플랜.Desktop.App.Data;

public sealed class LocalDeferredRecycleBinPurgeRecord
{
    public Guid Id { get; set; }
    public string BusinessDatabaseName { get; set; } = string.Empty;
    public string TenantCode { get; set; } = string.Empty;
    public string OfficeCode { get; set; } = string.Empty;
    public string ResponsibleOfficeCode { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public Guid EntityId { get; set; }
    public long Revision { get; set; }
    public DateTime PurgedAtUtc { get; set; }
    public int AttemptCount { get; set; }
    public DateTime? LastAttemptedAtUtc { get; set; }
    public string LastErrorMessage { get; set; } = string.Empty;
    public DateTime? NextAttemptAtUtc { get; set; }
    public DateTime? AppliedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
