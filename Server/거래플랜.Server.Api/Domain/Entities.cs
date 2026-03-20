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
    public string NameOriginal { get; set; } = string.Empty;
    public string NameMatchKey { get; set; } = string.Empty;
    public Guid? CategoryId { get; set; }
    public CustomerCategory? Category { get; set; }
    public string TradeType { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public string ContactPerson { get; set; } = string.Empty;
    public string BusinessNumber { get; set; } = string.Empty;
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
    public string InvoiceNumber { get; set; } = string.Empty;
    public string LocalTempNumber { get; set; } = string.Empty;
    public VoucherType VoucherType { get; set; }
    public DateOnly InvoiceDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public decimal TotalAmount { get; set; }
    public decimal SupplyAmount { get; set; }
    public decimal VatAmount { get; set; }
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
}
