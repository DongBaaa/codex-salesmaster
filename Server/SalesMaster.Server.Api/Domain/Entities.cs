using SalesMaster.Shared.Contracts;

namespace SalesMaster.Server.Api.Domain;

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
}

public sealed class Customer : TrackedEntity
{
    public Guid? CustomerMasterId { get; set; }
    public CustomerMaster? CustomerMaster { get; set; }
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
    public ICollection<Invoice> Invoices { get; set; } = new List<Invoice>();
}

public sealed class Item : TrackedEntity
{
    public string NameOriginal { get; set; } = string.Empty;
    public string NameMatchKey { get; set; } = string.Empty;
    public string SpecificationOriginal { get; set; } = string.Empty;
    public string SpecificationMatchKey { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
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
