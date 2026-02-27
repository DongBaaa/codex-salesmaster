using System;
using System.Collections.Generic;

namespace SalesMaster.Shared.Contracts;

public enum VoucherType
{
    Sales = 0,
    Purchase = 1,
    Procurement = 2,
    Expense = 3,
    Collection = 4
}

public sealed class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public sealed class LoginResponse
{
    public string Token { get; set; } = string.Empty;
    public string AccessToken { get => Token; set => Token = value; }
    public DateTime ExpiresAtUtc { get; set; }
    public UserSessionDto User { get; set; } = new();
}

public sealed class UserSessionDto
{
    public Guid UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public List<string> Permissions { get; set; } = new();
}

public sealed class UserAccountDto : SyncEntityDto
{
    public string Username { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public List<string> Permissions { get; set; } = new();
}

public sealed class UpdateUserPermissionsRequest
{
    public List<string> Permissions { get; set; } = new();
}

public abstract class SyncEntityDto
{
    public Guid Id { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public long Revision { get; set; }
}

public sealed class CompanyProfileDto : SyncEntityDto
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

public sealed class UnitDto : SyncEntityDto
{
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}

public sealed class CustomerCategoryDto : SyncEntityDto
{
    public string Name { get; set; } = string.Empty;
    public bool IsSystemDefault { get; set; }
}

public sealed class CustomerMasterDto : SyncEntityDto
{
    public string NameOriginal { get; set; } = string.Empty;
    public string NameMatchKey { get; set; } = string.Empty;
    public Guid? CategoryId { get; set; }
}

public sealed class CustomerDto : SyncEntityDto
{
    public Guid? CustomerMasterId { get; set; }
    public string NameOriginal { get; set; } = string.Empty;
    public string NameMatchKey { get; set; } = string.Empty;
    public Guid? CategoryId { get; set; }
    public string Department { get; set; } = string.Empty;
    public string ContactPerson { get; set; } = string.Empty;
    public string BusinessNumber { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
}

public sealed class ItemDto : SyncEntityDto
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

public sealed class InvoiceDto : SyncEntityDto
{
    public Guid CustomerId { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public string LocalTempNumber { get; set; } = string.Empty;
    public VoucherType VoucherType { get; set; }
    public DateOnly InvoiceDate { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal SupplyAmount { get; set; }
    public decimal VatAmount { get; set; }
    public string Memo { get; set; } = string.Empty;
    public List<InvoiceLineDto> Lines { get; set; } = new();
    public List<PaymentDto> Payments { get; set; } = new();
}

public sealed class InvoiceLineDto
{
    public Guid Id { get; set; }
    public Guid InvoiceId { get; set; }
    public Guid? ItemId { get; set; }
    public string ItemNameOriginal { get; set; } = string.Empty;
    public string SpecificationOriginal { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
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

public sealed class PaymentDto : SyncEntityDto
{
    public Guid InvoiceId { get; set; }
    public DateOnly PaymentDate { get; set; }
    public decimal Amount { get; set; }
    public string Note { get; set; } = string.Empty;
}

public sealed class PrintTemplateDto : SyncEntityDto
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
}

public sealed class PrintTemplateVersionDto : SyncEntityDto
{
    public Guid PrintTemplateId { get; set; }
    public int VersionNumber { get; set; }
    public string TemplateJson { get; set; } = string.Empty;
    public bool IsLocked { get; set; }
    public string CreatedByName { get; set; } = string.Empty;
}

public sealed class AuditLogDto
{
    public Guid Id { get; set; }
    public Guid? UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string EntityName { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string BeforeJson { get; set; } = string.Empty;
    public string AfterJson { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class ConflictLogDto
{
    public Guid Id { get; set; }
    public Guid? UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string EntityName { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string ClientJson { get; set; } = string.Empty;
    public string ServerJson { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class SyncPullResponse
{
    public long CurrentServerRevision { get; set; }
    public long LatestRevision => CurrentServerRevision;
    public List<CompanyProfileDto> CompanyProfiles { get; set; } = new();
    public List<UnitDto> Units { get; set; } = new();
    public List<CustomerCategoryDto> CustomerCategories { get; set; } = new();
    public List<CustomerMasterDto> CustomerMasters { get; set; } = new();
    public List<CustomerDto> Customers { get; set; } = new();
    public List<ItemDto> Items { get; set; } = new();
    public List<InvoiceDto> Invoices { get; set; } = new();
    public List<PaymentDto> Payments { get; set; } = new();
    public List<PrintTemplateDto> PrintTemplates { get; set; } = new();
    public List<PrintTemplateVersionDto> PrintTemplateVersions { get; set; } = new();
}

public sealed class SyncPushRequest
{
    public string DeviceId { get; set; } = string.Empty;
    public List<CompanyProfileDto> CompanyProfiles { get; set; } = new();
    public List<UnitDto> Units { get; set; } = new();
    public List<CustomerCategoryDto> CustomerCategories { get; set; } = new();
    public List<CustomerMasterDto> CustomerMasters { get; set; } = new();
    public List<CustomerDto> Customers { get; set; } = new();
    public List<ItemDto> Items { get; set; } = new();
    public List<InvoiceDto> Invoices { get; set; } = new();
    public List<PaymentDto> Payments { get; set; } = new();
    public List<PrintTemplateDto> PrintTemplates { get; set; } = new();
    public List<PrintTemplateVersionDto> PrintTemplateVersions { get; set; } = new();
}

public sealed class SyncPushResult
{
    public int AcceptedCount { get; set; }
    public int ConflictCount { get; set; }
    public long CurrentServerRevision { get; set; }
    public List<ConflictLogDto> Conflicts { get; set; } = new();
    /// <summary>Key = local invoice Id, Value = assigned server InvoiceNumber.</summary>
    public Dictionary<Guid, string> AssignedInvoiceNumbers { get; set; } = new();
}
