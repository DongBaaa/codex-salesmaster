using 거래플랜.Shared.Contracts;

namespace 거래플랜.Server.Api.Domain;

public sealed class ProcessedSyncMutation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string MutationId { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string EntityName { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public long ExpectedRevision { get; set; }
    public DateTime ProcessedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class InventoryLedgerEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantCode { get; set; } = TenantScopeCatalog.UsenetGroup;
    public string OfficeCode { get; set; } = OfficeCodeCatalog.Shared;
    public Guid ItemId { get; set; }
    public string WarehouseCode { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public Guid SourceDocumentId { get; set; }
    public Guid? SourceLineId { get; set; }
    public decimal QuantityDelta { get; set; }
    public DateOnly OccurredDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public string Note { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class RentalAssetAssignmentHistory : TrackedEntity
{
    public Guid AssetId { get; set; }
    public Guid? BillingProfileId { get; set; }
    public Guid? CustomerId { get; set; }
    public string TenantCode { get; set; } = TenantScopeCatalog.UsenetGroup;
    public string OfficeCode { get; set; } = OfficeCodeCatalog.Shared;
    public string ResponsibleOfficeCode { get; set; } = OfficeCodeCatalog.Usenet;
    public string CustomerName { get; set; } = string.Empty;
    public string InstallLocation { get; set; } = string.Empty;
    public string BillingProfileDisplay { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public string MachineNumber { get; set; } = string.Empty;
    public string ManagementNumber { get; set; } = string.Empty;
    public decimal MonthlyFee { get; set; }
    public DateOnly? ContractStartDate { get; set; }
    public DateOnly? ContractEndDate { get; set; }
    public string ChangeReason { get; set; } = string.Empty;
    public bool IsCurrent { get; set; } = true;
    public DateTime LinkedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UnlinkedAtUtc { get; set; }
}

public sealed class ActiveEditSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AppSessionId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string OfficeCode { get; set; } = string.Empty;
    public string TenantCode { get; set; } = TenantScopeCatalog.UsenetGroup;
    public string ScreenName { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string EntityDisplayName { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public DateTime OpenedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastHeartbeatUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAtUtc { get; set; } = DateTime.UtcNow.AddMinutes(2);
}

