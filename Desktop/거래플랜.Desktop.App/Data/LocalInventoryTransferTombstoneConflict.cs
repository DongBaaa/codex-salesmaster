namespace 거래플랜.Desktop.App.Data;

public sealed class LocalInventoryTransferTombstoneConflict
{
    public Guid TransferId { get; set; }
    public string BusinessDatabaseName { get; set; } = string.Empty;
    public string TenantCode { get; set; } = string.Empty;
    public string SourceOfficeCode { get; set; } = string.Empty;
    public string TargetOfficeCode { get; set; } = string.Empty;
    public string LocalSnapshotJson { get; set; } = string.Empty;
    public string ServerTombstoneJson { get; set; } = string.Empty;
    public string OutboxMutationIdsJson { get; set; } = string.Empty;
    public string ArchivedReceiveEvidencePath { get; set; } = string.Empty;
    public long LocalRevision { get; set; }
    public long ServerRevision { get; set; }
    public DateTime ServerUpdatedAtUtc { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime DetectedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }
    public string Resolution { get; set; } = string.Empty;
    public Guid? RecoveredTransferId { get; set; }
}
