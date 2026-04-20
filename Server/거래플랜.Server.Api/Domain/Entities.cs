using 거래플랜.Shared.Contracts;

namespace 거래플랜.Server.Api.Domain;

public interface ITrackedEntity
{
    Guid Id { get; set; }
    bool IsDeleted { get; set; }
    DateTime CreatedAtUtc { get; set; }
    DateTime UpdatedAtUtc { get; set; }
    long Revision { get; set; }
}

public abstract class TrackedEntity : ITrackedEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool IsDeleted { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public long Revision { get; set; }
}

public sealed class UserAccount : TrackedEntity
{
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Role { get; set; } = "User";
    public string TenantCode { get; set; } = TenantScopeCatalog.UsenetGroup;
    public string OfficeCode { get; set; } = OfficeCodeCatalog.Usenet;
    public string ScopeType { get; set; } = TenantScopeCatalog.ScopeOfficeOnly;
    public bool IsActive { get; set; } = true;
    public ICollection<UserPermission> Permissions { get; set; } = new List<UserPermission>();
}

public sealed class UserPermission
{
    public Guid UserId { get; set; }
    public string Permission { get; set; } = string.Empty;
    public UserAccount? User { get; set; }
}

public sealed class CompanyProfile : TrackedEntity
{
    public string ProfileName { get; set; } = string.Empty;
    public string OfficeCode { get; set; } = OfficeCodeCatalog.Usenet;
    public string TradeName { get; set; } = string.Empty;
    public string Representative { get; set; } = string.Empty;
    public string BusinessNumber { get; set; } = string.Empty;
    public string BusinessType { get; set; } = string.Empty;
    public string BusinessItem { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string ContactNumber { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string BankAccountText { get; set; } = string.Empty;
    public byte[]? StampImage { get; set; }
    public bool IsDefaultForOffice { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class TenantDefinition : TrackedEntity
{
    public string TenantCode { get; set; } = TenantScopeCatalog.UsenetGroup;
    public string DisplayName { get; set; } = string.Empty;
    public string StorageMode { get; set; } = TenantScopeCatalog.StorageSharedDatabase;
    public string Description { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}

public sealed class TenantOfficeDefinition : TrackedEntity
{
    public string TenantCode { get; set; } = TenantScopeCatalog.UsenetGroup;
    public string OfficeCode { get; set; } = OfficeCodeCatalog.Usenet;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsHeadOffice { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class DataSharingPolicy : TrackedEntity
{
    public string SourceTenantCode { get; set; } = TenantScopeCatalog.UsenetGroup;
    public string SourceOfficeCode { get; set; } = OfficeCodeCatalog.Yeonsu;
    public string TargetTenantCode { get; set; } = TenantScopeCatalog.UsenetGroup;
    public string TargetOfficeCode { get; set; } = OfficeCodeCatalog.Usenet;
    public bool ShareCustomers { get; set; } = true;
    public bool ShareItems { get; set; }
    public bool ShareInvoices { get; set; } = true;
    public bool SharePayments { get; set; } = true;
    public bool ShareContracts { get; set; } = true;
    public bool ShareReports { get; set; } = true;
    public bool ShareRentals { get; set; } = true;
    public bool ShareDeliveries { get; set; } = true;
    public bool AllowTargetWrite { get; set; }
    public string Note { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}

public sealed class Unit : TrackedEntity
{
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}

public sealed class CustomerCategory : TrackedEntity
{
    public string Name { get; set; } = string.Empty;
    public bool IsSystemDefault { get; set; }
}

public sealed class PriceGradeOption : TrackedEntity
{
    public string Name { get; set; } = string.Empty;
    public string PriceSource { get; set; } = "Sales";
    public int SortOrder { get; set; }
    public bool IsSystemDefault { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class TradeTypeOption : TrackedEntity
{
    public string Name { get; set; } = string.Empty;
    public bool AllowsSales { get; set; } = true;
    public bool AllowsPurchase { get; set; }
    public int SortOrder { get; set; }
    public bool IsSystemDefault { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class ItemCategoryOption : TrackedEntity
{
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsSystemDefault { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class CustomerMaster : TrackedEntity
{
    public string NameOriginal { get; set; } = string.Empty;
    public string NameMatchKey { get; set; } = string.Empty;
    public Guid? CategoryId { get; set; }
    public CustomerCategory? Category { get; set; }
    public string TenantCode { get; set; } = TenantScopeCatalog.UsenetGroup;
    public string OfficeCode { get; set; } = OfficeCodeCatalog.Shared;
}

public sealed class Customer : TrackedEntity
{
    public Guid? CustomerMasterId { get; set; }
    public CustomerMaster? CustomerMaster { get; set; }
    public string TenantCode { get; set; } = TenantScopeCatalog.UsenetGroup;
    public string OfficeCode { get; set; } = OfficeCodeCatalog.Shared;
    public string ResponsibleOfficeCode { get; set; } = OfficeCodeCatalog.Usenet;
    public string NameOriginal { get; set; } = string.Empty;
    public string NameMatchKey { get; set; } = string.Empty;
    public Guid? CategoryId { get; set; }
    public CustomerCategory? Category { get; set; }
    public string TradeType { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public string ContactPerson { get; set; } = string.Empty;
    public string Representative { get; set; } = string.Empty;
    public string BusinessNumber { get; set; } = string.Empty;
    public string BusinessType { get; set; } = string.Empty;
    public string BusinessItem { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public ICollection<CustomerContract> Contracts { get; set; } = new List<CustomerContract>();
    public ICollection<Invoice> Invoices { get; set; } = new List<Invoice>();
}

public sealed class CustomerContract : TrackedEntity
{
    public Guid CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public string ContractType { get; set; } = "거래계약서";
    public string FileName { get; set; } = string.Empty;
    public string MimeType { get; set; } = "application/pdf";
    public long FileSize { get; set; }
    public string FileHash { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateOnly? SignedDate { get; set; }
    public DateOnly? ExpireDate { get; set; }
    public bool IsPrimary { get; set; }
    public string UploadedByUsername { get; set; } = string.Empty;
    public DateTime UploadedAtUtc { get; set; } = DateTime.UtcNow;
    public string StoragePath { get; set; } = string.Empty;
    public byte[] FileContent { get; set; } = [];
}

public sealed class Item : TrackedEntity
{
    public string TenantCode { get; set; } = TenantScopeCatalog.UsenetGroup;
    public string OfficeCode { get; set; } = OfficeCodeCatalog.Shared;
    public string NameOriginal { get; set; } = string.Empty;
    public string NameMatchKey { get; set; } = string.Empty;
    public string SpecificationOriginal { get; set; } = string.Empty;
    public string SpecificationMatchKey { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;
    public string ItemKind { get; set; } = ItemKinds.Product;
    public string TrackingType { get; set; } = ItemTrackingTypes.Stock;
    public string Unit { get; set; } = string.Empty;
    public decimal CurrentStock { get; set; }
    public decimal SafetyStock { get; set; }
    public decimal PurchasePrice { get; set; }
    public decimal SalePrice { get; set; }
    public decimal RetailPrice { get; set; }
    public decimal PriceGradeA { get; set; }
    public decimal PriceGradeB { get; set; }
    public decimal PriceGradeC { get; set; }
    public string SimpleMemo { get; set; } = string.Empty;
    public bool IsRental { get; set; }
    public bool IsSale { get; set; }
    public string SerialNumber { get; set; } = string.Empty;
    public string MaterialNumber { get; set; } = string.Empty;
    public string InstallLocation { get; set; } = string.Empty;
    public DateOnly? RentalStartDate { get; set; }
    public DateOnly? RentalEndDate { get; set; }
    public string Notes { get; set; } = string.Empty;
}

public sealed class Invoice : TrackedEntity
{
    public Guid CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public string TenantCode { get; set; } = TenantScopeCatalog.UsenetGroup;
    public string OfficeCode { get; set; } = OfficeCodeCatalog.Shared;
    public string ResponsibleOfficeCode { get; set; } = OfficeCodeCatalog.Usenet;
    public string InvoiceNumber { get; set; } = string.Empty;
    public string LocalTempNumber { get; set; } = string.Empty;
    public Guid? LinkedRentalBillingProfileId { get; set; }
    public Guid? LinkedRentalBillingRunId { get; set; }
    public Guid VersionGroupId { get; set; }
    public int VersionNumber { get; set; } = 1;
    public Guid? PreviousVersionId { get; set; }
    public bool IsLatestVersion { get; set; } = true;
    public VoucherType VoucherType { get; set; }
    public string SourceWarehouseCode { get; set; } = OfficeCodeCatalog.UsenetMainWarehouse;
    public DateOnly InvoiceDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public decimal TotalAmount { get; set; }
    public decimal SupplyAmount { get; set; }
    public decimal VatAmount { get; set; }
    public bool TaxInvoiceIssued { get; set; }
    public string Memo { get; set; } = string.Empty;
    public ICollection<InvoiceLine> Lines { get; set; } = new List<InvoiceLine>();
    public ICollection<Payment> Payments { get; set; } = new List<Payment>();
}

public sealed class InvoiceLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }
    public Guid? ItemId { get; set; }
    public string ItemNameOriginal { get; set; } = string.Empty;
    public string SpecificationOriginal { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public decimal Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }
    public decimal LineAmount { get; set; }
    public string Remark { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public string MaterialNumber { get; set; } = string.Empty;
    public string InstallLocation { get; set; } = string.Empty;
    public DateOnly? RentalStartDate { get; set; }
    public DateOnly? RentalEndDate { get; set; }
    public string ItemTrackingType { get; set; } = ItemTrackingTypes.Stock;
    public bool IsDeleted { get; set; }
}

public sealed class Payment : TrackedEntity
{
    public Guid InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }
    public DateOnly PaymentDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public decimal Amount { get; set; }
    public string Note { get; set; } = string.Empty;
    public ICollection<PaymentAttachment> Attachments { get; set; } = new List<PaymentAttachment>();
}

public sealed class PaymentAttachment : TrackedEntity
{
    public Guid PaymentId { get; set; }
    public Payment? Payment { get; set; }
    public string AttachmentType { get; set; } = "내역첨부";
    public string FileName { get; set; } = string.Empty;
    public string MimeType { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string FileHash { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime UploadedAtUtc { get; set; } = DateTime.UtcNow;
    public string StoragePath { get; set; } = string.Empty;
    public byte[] FileContent { get; set; } = [];
}

public sealed class ItemWarehouseStock
{
    public Guid ItemId { get; set; }
    public Item? Item { get; set; }
    public string WarehouseCode { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class TransactionRecord : TrackedEntity
{
    public Guid CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public string TenantCode { get; set; } = TenantScopeCatalog.UsenetGroup;
    public string OfficeCode { get; set; } = OfficeCodeCatalog.Shared;
    public string ResponsibleOfficeCode { get; set; } = OfficeCodeCatalog.Usenet;
    public DateOnly TransactionDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public string TransactionKind { get; set; } = string.Empty;
    public Guid? LinkedInvoiceId { get; set; }
    public string LinkedInvoiceNumber { get; set; } = string.Empty;
    public Guid? LinkedRentalBillingProfileId { get; set; }
    public Guid? LinkedRentalBillingRunId { get; set; }
    public decimal SettlementAmount { get; set; }
    public decimal AdvanceDelta { get; set; }
    public decimal PrepaidDelta { get; set; }
    public decimal CashReceipt { get; set; }
    public decimal CardReceipt { get; set; }
    public decimal BankReceipt { get; set; }
    public decimal DiscountApplied { get; set; }
    public decimal ReceiptTotal { get; set; }
    public decimal CashPayment { get; set; }
    public decimal CardPayment { get; set; }
    public decimal BankPayment { get; set; }
    public decimal DiscountReceived { get; set; }
    public decimal PaymentTotal { get; set; }
    public string Note { get; set; } = string.Empty;
    public string Memo { get; set; } = string.Empty;
    public ICollection<TransactionAttachment> Attachments { get; set; } = new List<TransactionAttachment>();
}

public sealed class TransactionAttachment : TrackedEntity
{
    public Guid TransactionId { get; set; }
    public TransactionRecord? Transaction { get; set; }
    public string AttachmentType { get; set; } = "기타";
    public string FileName { get; set; } = string.Empty;
    public string MimeType { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string FileHash { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string UploadedByUsername { get; set; } = string.Empty;
    public DateTime UploadedAtUtc { get; set; } = DateTime.UtcNow;
    public string VerificationStatus { get; set; } = "미확인";
    public string VerifiedByUsername { get; set; } = string.Empty;
    public DateTime? VerifiedAtUtc { get; set; }
    public string VerificationMemo { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public string StoragePath { get; set; } = string.Empty;
    public byte[] FileContent { get; set; } = [];
}

public sealed class InventoryTransfer : TrackedEntity
{
    public string TenantCode { get; set; } = TenantScopeCatalog.UsenetGroup;
    public string SourceOfficeCode { get; set; } = OfficeCodeCatalog.Usenet;
    public string TargetOfficeCode { get; set; } = OfficeCodeCatalog.Yeonsu;
    public string TransferNumber { get; set; } = string.Empty;
    public DateOnly TransferDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public string FromWarehouseCode { get; set; } = string.Empty;
    public string ToWarehouseCode { get; set; } = string.Empty;
    public string Memo { get; set; } = string.Empty;
    public string CreatedByUsername { get; set; } = string.Empty;
    public string LastSavedByUsername { get; set; } = string.Empty;
    public DateTime LastSavedAtUtc { get; set; } = DateTime.UtcNow;
    public string TransferStatus { get; set; } = "수령대기";
    public string RequestedByUsername { get; set; } = string.Empty;
    public DateTime? RequestedAtUtc { get; set; }
    public string ReceivedByUsername { get; set; } = string.Empty;
    public DateTime? ReceivedAtUtc { get; set; }
    public string ReceiveMemo { get; set; } = string.Empty;
    public string ReceiveEvidencePath { get; set; } = string.Empty;
    public string RejectedByUsername { get; set; } = string.Empty;
    public DateTime? RejectedAtUtc { get; set; }
    public string RejectReason { get; set; } = string.Empty;
    public string LastStatusChangedByUsername { get; set; } = string.Empty;
    public DateTime? LastStatusChangedAtUtc { get; set; }
    public ICollection<InventoryTransferLine> Lines { get; set; } = new List<InventoryTransferLine>();
}

public sealed class InventoryTransferLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TransferId { get; set; }
    public InventoryTransfer? Transfer { get; set; }
    public Guid? ItemId { get; set; }
    public string ItemNameOriginal { get; set; } = string.Empty;
    public string SpecificationOriginal { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal? ReceivedQuantity { get; set; }
    public decimal? QuantityDifference { get; set; }
    public string Remark { get; set; } = string.Empty;
    public string ReceiptRemark { get; set; } = string.Empty;
    public bool IsDeleted { get; set; }
}

public sealed class RentalManagementCompany : TrackedEntity
{
    public string TenantCode { get; set; } = TenantScopeCatalog.UsenetGroup;
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsSystemDefault { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class RentalBillingProfile : TrackedEntity
{
    public string TenantCode { get; set; } = TenantScopeCatalog.UsenetGroup;
    public string OfficeCode { get; set; } = OfficeCodeCatalog.Shared;
    public string ResponsibleOfficeCode { get; set; } = OfficeCodeCatalog.Usenet;
    public string ProfileKey { get; set; } = string.Empty;
    public Guid? CustomerId { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public string BusinessNumber { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public string BillingType { get; set; } = "묶음";
    public string InstallSiteName { get; set; } = string.Empty;
    public string BillingAdvanceMode { get; set; } = "후불";
    public string ManagementCompanyCode { get; set; } = string.Empty;
    public string BillingMethod { get; set; } = string.Empty;
    public string BillingStatus { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public int BillingDay { get; set; } = 25;
    public string BillingDayMode { get; set; } = RentalBillingScheduleRules.BillingDayModeFixedDay;
    public int BillingCycleMonths { get; set; } = 1;
    public int BillingAnchorMonth { get; set; } = 3;
    public string DocumentIssueMode { get; set; } = RentalBillingScheduleRules.DocumentIssueModeSameAsDueDate;
    public int DocumentLeadDays { get; set; }
    public decimal MonthlyAmount { get; set; }
    public decimal DepositAmount { get; set; }
    public string SubmissionDocuments { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public DateOnly? BillingAnchorDate { get; set; }
    public DateOnly? BillingStartDate { get; set; }
    public DateOnly? ContractDate { get; set; }
    public DateOnly? ContractStartDate { get; set; }
    public DateOnly? ContractEndDate { get; set; }
    public DateOnly? LastBilledDate { get; set; }
    public string SettlementStatus { get; set; } = string.Empty;
    public string CompletionStatus { get; set; } = string.Empty;
    public decimal SettledAmount { get; set; }
    public decimal OutstandingAmount { get; set; }
    public bool RequiresFollowUp { get; set; }
    public DateOnly? LastSettledDate { get; set; }
    public string BillingTemplateJson { get; set; } = "[]";
    public string BillingRunsJson { get; set; } = "[]";
    public bool IsActive { get; set; } = true;
}

public sealed class RentalAsset : TrackedEntity
{
    public string TenantCode { get; set; } = TenantScopeCatalog.UsenetGroup;
    public string OfficeCode { get; set; } = OfficeCodeCatalog.Shared;
    public string ResponsibleOfficeCode { get; set; } = OfficeCodeCatalog.Usenet;
    public string AssetKey { get; set; } = string.Empty;
    public Guid? CustomerId { get; set; }
    public Guid? ItemId { get; set; }
    public Guid? BillingProfileId { get; set; }
    public string LastCustomerName { get; set; } = string.Empty;
    public string LastInstallLocation { get; set; } = string.Empty;
    public Guid? LastBillingProfileId { get; set; }
    public string LastBillingProfileDisplay { get; set; } = string.Empty;
    public DateTime? LastAssignmentClearedAtUtc { get; set; }
    public string ManagementId { get; set; } = string.Empty;
    public string ManagementNumber { get; set; } = string.Empty;
    public string ManagementCompanyCode { get; set; } = string.Empty;
    public string CurrentLocation { get; set; } = string.Empty;
    public string CurrentCustomerName { get; set; } = string.Empty;
    public string InstallSiteName { get; set; } = string.Empty;
    public string BillingEligibilityStatus { get; set; } = string.Empty;
    public string BillingExclusionReason { get; set; } = string.Empty;
    public string ItemCategoryName { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public string MachineNumber { get; set; } = string.Empty;
    public string PurchaseVendor { get; set; } = string.Empty;
    public DateOnly? PurchaseDate { get; set; }
    public DateOnly? DisposalDate { get; set; }
    public decimal PurchasePrice { get; set; }
    public decimal SalePrice { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public string InstallLocation { get; set; } = string.Empty;
    public string DepositText { get; set; } = string.Empty;
    public decimal MonthlyFee { get; set; }
    public int ContractMonths { get; set; }
    public DateOnly? ContractDate { get; set; }
    public DateOnly? InstallDate { get; set; }
    public DateOnly? ContractStartDate { get; set; }
    public DateOnly? RentalEndDate { get; set; }
    public string FreeSupplyItems { get; set; } = string.Empty;
    public string PaidSupplyItems { get; set; } = string.Empty;
    public string AssetStatus { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
}

public sealed class RentalBillingLog : TrackedEntity
{
    public string TenantCode { get; set; } = TenantScopeCatalog.UsenetGroup;
    public string OfficeCode { get; set; } = OfficeCodeCatalog.Shared;
    public string ResponsibleOfficeCode { get; set; } = OfficeCodeCatalog.Usenet;
    public Guid BillingProfileId { get; set; }
    public string BillingYearMonth { get; set; } = string.Empty;
    public DateOnly ScheduledDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public DateOnly? ProcessedDate { get; set; }
    public string ProcessedByUsername { get; set; } = string.Empty;
    public string Status { get; set; } = "예정";
    public decimal BilledAmount { get; set; }
    public string Note { get; set; } = string.Empty;
}
public sealed class AuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? UserId { get; set; }
    public string Username { get; set; } = "system";
    public string EntityName { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string BeforeJson { get; set; } = string.Empty;
    public string AfterJson { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class ConflictLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? UserId { get; set; }
    public string Username { get; set; } = "system";
    public string EntityName { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string ClientJson { get; set; } = string.Empty;
    public string ServerJson { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string Status { get; set; } = "Open";
    public DateTime? ResolvedAtUtc { get; set; }
    public string ResolutionNote { get; set; } = string.Empty;
}

public sealed class RecycleBinPurgeRecord : TrackedEntity
{
    public string Kind { get; set; } = string.Empty;
    public Guid EntityId { get; set; }
    public string TenantCode { get; set; } = TenantScopeCatalog.UsenetGroup;
    public string OfficeCode { get; set; } = OfficeCodeCatalog.Shared;
    public DateTime PurgedAtUtc { get; set; } = DateTime.UtcNow;
}

