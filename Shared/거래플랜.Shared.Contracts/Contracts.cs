using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace 거래플랜.Shared.Contracts;

 [JsonConverter(typeof(VoucherTypeJsonConverter))]
public enum VoucherType
{
    Sales = 0,
    Purchase = 1,
    Procurement = 2,
    Expense = 3,
    Collection = 4
}

public sealed class VoucherTypeJsonConverter : JsonConverter<VoucherType>
{
    public override VoucherType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            var numeric = reader.GetInt32();
            if (Enum.IsDefined(typeof(VoucherType), numeric))
                return (VoucherType)numeric;

            throw new JsonException($"Unknown VoucherType numeric value: {numeric}");
        }

        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("VoucherType must be a string or number.");

        var raw = reader.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
            throw new JsonException("VoucherType cannot be empty.");

        if (int.TryParse(raw, out var numericString) && Enum.IsDefined(typeof(VoucherType), numericString))
            return (VoucherType)numericString;

        if (TryMap(raw, out var value))
            return value;

        throw new JsonException($"Unknown VoucherType value: {raw}");
    }

    public override void Write(Utf8JsonWriter writer, VoucherType value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            VoucherType.Sales => nameof(VoucherType.Sales),
            VoucherType.Purchase => nameof(VoucherType.Purchase),
            VoucherType.Procurement => nameof(VoucherType.Procurement),
            VoucherType.Expense => nameof(VoucherType.Expense),
            VoucherType.Collection => nameof(VoucherType.Collection),
            _ => nameof(VoucherType.Sales)
        });
    }

    private static bool TryMap(string raw, out VoucherType value)
    {
        switch (raw.Trim().ToUpperInvariant())
        {
            case "SALES":
            case "SALE":
            case "매출":
            case "판매":
                value = VoucherType.Sales;
                return true;
            case "PURCHASE":
            case "매입":
            case "구매":
                value = VoucherType.Purchase;
                return true;
            case "PROCUREMENT":
            case "발주":
                value = VoucherType.Procurement;
                return true;
            case "EXPENSE":
            case "경비":
                value = VoucherType.Expense;
                return true;
            case "COLLECTION":
            case "수금":
                value = VoucherType.Collection;
                return true;
            default:
                value = default;
                return false;
        }
    }
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
    public string TenantCode { get; set; } = TenantScopeCatalog.UsenetGroup;
    public string OfficeCode { get; set; } = string.Empty;
    public string ScopeType { get; set; } = TenantScopeCatalog.ScopeOfficeOnly;
    public List<string> Permissions { get; set; } = new();
}

public sealed class UserAccountDto : SyncEntityDto
{
    public string Username { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string TenantCode { get; set; } = TenantScopeCatalog.UsenetGroup;
    public string OfficeCode { get; set; } = string.Empty;
    public string ScopeType { get; set; } = TenantScopeCatalog.ScopeOfficeOnly;
    public bool IsActive { get; set; }
    public List<string> Permissions { get; set; } = new();
}

public sealed class UpdateUserPermissionsRequest
{
    public List<string> Permissions { get; set; } = new();
}

public sealed class CreateUserRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string TenantCode { get; set; } = TenantScopeCatalog.UsenetGroup;
    public string OfficeCode { get; set; } = string.Empty;
    public string ScopeType { get; set; } = TenantScopeCatalog.ScopeOfficeOnly;
    public bool IsActive { get; set; } = true;
    public List<string> Permissions { get; set; } = new();
}

public sealed class UpdateUserRequest
{
    public string Username { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string TenantCode { get; set; } = TenantScopeCatalog.UsenetGroup;
    public string OfficeCode { get; set; } = string.Empty;
    public string ScopeType { get; set; } = TenantScopeCatalog.ScopeOfficeOnly;
    public bool IsActive { get; set; }
    public List<string> Permissions { get; set; } = new();
}

public sealed class UpdateUserPasswordRequest
{
    public string Password { get; set; } = string.Empty;
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

public sealed class PriceGradeOptionDto : SyncEntityDto
{
    public string Name { get; set; } = string.Empty;
    public string PriceSource { get; set; } = "Sales";
    public int SortOrder { get; set; }
    public bool IsSystemDefault { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class TradeTypeOptionDto : SyncEntityDto
{
    public string Name { get; set; } = string.Empty;
    public bool AllowsSales { get; set; } = true;
    public bool AllowsPurchase { get; set; }
    public int SortOrder { get; set; }
    public bool IsSystemDefault { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class ItemCategoryOptionDto : SyncEntityDto
{
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsSystemDefault { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class CustomerMasterDto : SyncEntityDto
{
    public string NameOriginal { get; set; } = string.Empty;
    public string NameMatchKey { get; set; } = string.Empty;
    public Guid? CategoryId { get; set; }
    public string TenantCode { get; set; } = TenantScopeCatalog.UsenetGroup;
    public string OfficeCode { get; set; } = OfficeCodeCatalog.Shared;
}

public sealed class CustomerDto : SyncEntityDto
{
    public Guid? CustomerMasterId { get; set; }
    public string TenantCode { get; set; } = TenantScopeCatalog.UsenetGroup;
    public string OfficeCode { get; set; } = OfficeCodeCatalog.Shared;
    public string NameOriginal { get; set; } = string.Empty;
    public string NameMatchKey { get; set; } = string.Empty;
    public Guid? CategoryId { get; set; }
    public string TradeType { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public string ContactPerson { get; set; } = string.Empty;
    public string BusinessNumber { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
}

public sealed class CustomerContractDto : SyncEntityDto
{
    public Guid CustomerId { get; set; }
    public string ContractType { get; set; } = string.Empty;
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

public sealed class ItemDto : SyncEntityDto
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

public sealed class InvoiceDto : SyncEntityDto
{
    public Guid CustomerId { get; set; }
    public string TenantCode { get; set; } = TenantScopeCatalog.UsenetGroup;
    public string OfficeCode { get; set; } = OfficeCodeCatalog.Shared;
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
    public List<PaymentAttachmentDto> Attachments { get; set; } = new();
}

public sealed class PaymentAttachmentDto : SyncEntityDto
{
    public Guid PaymentId { get; set; }
    public string AttachmentType { get; set; } = "내역첨부";
    public string FileName { get; set; } = string.Empty;
    public string MimeType { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string FileHash { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime UploadedAtUtc { get; set; } = DateTime.UtcNow;
    public byte[] FileContent { get; set; } = [];
}

public sealed class TransactionDto : SyncEntityDto
{
    public Guid CustomerId { get; set; }
    public string TenantCode { get; set; } = TenantScopeCatalog.UsenetGroup;
    public string OfficeCode { get; set; } = OfficeCodeCatalog.Shared;
    public DateOnly TransactionDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public string TransactionKind { get; set; } = string.Empty;
    public Guid? LinkedInvoiceId { get; set; }
    public string LinkedInvoiceNumber { get; set; } = string.Empty;
    public Guid? LinkedRentalBillingProfileId { get; set; }
    public decimal SettlementAmount { get; set; }
    public decimal AdvanceDelta { get; set; }
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
}

public sealed class TransactionAttachmentDto : SyncEntityDto
{
    public Guid TransactionId { get; set; }
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
    public byte[] FileContent { get; set; } = [];
}

public sealed class InventoryTransferDto : SyncEntityDto
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
    public List<InventoryTransferLineDto> Lines { get; set; } = new();
}

public sealed class InventoryTransferLineDto
{
    public Guid Id { get; set; }
    public Guid TransferId { get; set; }
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

public sealed class RentalManagementCompanyDto : SyncEntityDto
{
    public string TenantCode { get; set; } = TenantScopeCatalog.UsenetGroup;
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsSystemDefault { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class RentalBillingProfileDto : SyncEntityDto
{
    public string TenantCode { get; set; } = TenantScopeCatalog.UsenetGroup;
    public string OfficeCode { get; set; } = OfficeCodeCatalog.Shared;
    public string ProfileKey { get; set; } = string.Empty;
    public Guid? CustomerId { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public string BusinessNumber { get; set; } = string.Empty;
    public string RealCustomerName { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public string ManagementCompanyCode { get; set; } = string.Empty;
    public string BillingMethod { get; set; } = string.Empty;
    public string PaymentMethod { get; set; } = string.Empty;
    public string BillingStatus { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public int BillingDay { get; set; } = 25;
    public int BillingCycleMonths { get; set; } = 1;
    public decimal MonthlyAmount { get; set; }
    public decimal DepositAmount { get; set; }
    public string SubmissionDocuments { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public DateOnly? BillingAnchorDate { get; set; }
    public DateOnly? ContractDate { get; set; }
    public DateOnly? ContractStartDate { get; set; }
    public DateOnly? ContractEndDate { get; set; }
    public DateOnly? LastBilledDate { get; set; }
    public string SettlementStatus { get; set; } = string.Empty;
    public string CompletionStatus { get; set; } = string.Empty;
    public decimal SettledAmount { get; set; }
    public decimal OutstandingAmount { get; set; }
    public bool RequiresFollowUp { get; set; }
    public string FollowUpNote { get; set; } = string.Empty;
    public DateOnly? LastSettledDate { get; set; }
    public string AssignedUsername { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}

public sealed class RentalAssetDto : SyncEntityDto
{
    public string TenantCode { get; set; } = TenantScopeCatalog.UsenetGroup;
    public string OfficeCode { get; set; } = OfficeCodeCatalog.Shared;
    public string AssetKey { get; set; } = string.Empty;
    public Guid? CustomerId { get; set; }
    public Guid? ItemId { get; set; }
    public Guid? BillingProfileId { get; set; }
    public string ManagementId { get; set; } = string.Empty;
    public string ManagementNumber { get; set; } = string.Empty;
    public string ManagementCompanyCode { get; set; } = string.Empty;
    public string CurrentLocation { get; set; } = string.Empty;
    public string ProductCategory { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
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
    public string AssignedUsername { get; set; } = string.Empty;
    public string AssetStatus { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
}

public sealed class RentalBillingLogDto : SyncEntityDto
{
    public string TenantCode { get; set; } = TenantScopeCatalog.UsenetGroup;
    public string OfficeCode { get; set; } = OfficeCodeCatalog.Shared;
    public Guid BillingProfileId { get; set; }
    public string BillingYearMonth { get; set; } = string.Empty;
    public DateOnly ScheduledDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public DateOnly? ProcessedDate { get; set; }
    public string ProcessedByUsername { get; set; } = string.Empty;
    public string Status { get; set; } = "예정";
    public decimal BilledAmount { get; set; }
    public string Note { get; set; } = string.Empty;
    public string AssignedUsername { get; set; } = string.Empty;
}

public sealed class ItemWarehouseStockDto
{
    public Guid ItemId { get; set; }
    public string WarehouseCode { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class CustomerDetailDto
{
    public CustomerDto Customer { get; set; } = new();
    public List<InvoiceDto> RecentInvoices { get; set; } = new();
}

public sealed class ItemCategorySummaryDto
{
    public string Name { get; set; } = string.Empty;
    public int ItemCount { get; set; }
}

public sealed class ItemDetailDto
{
    public ItemDto Item { get; set; } = new();
    public List<ItemWarehouseStockDto> BranchStocks { get; set; } = new();
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
    public List<PriceGradeOptionDto> PriceGradeOptions { get; set; } = new();
    public List<TradeTypeOptionDto> TradeTypeOptions { get; set; } = new();
    public List<ItemCategoryOptionDto> ItemCategoryOptions { get; set; } = new();
    public List<CustomerMasterDto> CustomerMasters { get; set; } = new();
    public List<CustomerDto> Customers { get; set; } = new();
    public List<CustomerContractDto> CustomerContracts { get; set; } = new();
    public List<ItemDto> Items { get; set; } = new();
    public List<ItemWarehouseStockDto> ItemWarehouseStocks { get; set; } = new();
    public List<TransactionDto> Transactions { get; set; } = new();
    public List<TransactionAttachmentDto> TransactionAttachments { get; set; } = new();
    public List<InventoryTransferDto> InventoryTransfers { get; set; } = new();
    public List<RentalManagementCompanyDto> RentalManagementCompanies { get; set; } = new();
    public List<RentalBillingProfileDto> RentalBillingProfiles { get; set; } = new();
    public List<RentalAssetDto> RentalAssets { get; set; } = new();
    public List<RentalBillingLogDto> RentalBillingLogs { get; set; } = new();
    public List<InvoiceDto> Invoices { get; set; } = new();
    public List<PaymentDto> Payments { get; set; } = new();
}

public sealed class SyncPushRequest
{
    public string DeviceId { get; set; } = string.Empty;
    public List<CompanyProfileDto> CompanyProfiles { get; set; } = new();
    public List<UnitDto> Units { get; set; } = new();
    public List<CustomerCategoryDto> CustomerCategories { get; set; } = new();
    public List<PriceGradeOptionDto> PriceGradeOptions { get; set; } = new();
    public List<TradeTypeOptionDto> TradeTypeOptions { get; set; } = new();
    public List<ItemCategoryOptionDto> ItemCategoryOptions { get; set; } = new();
    public List<CustomerMasterDto> CustomerMasters { get; set; } = new();
    public List<CustomerDto> Customers { get; set; } = new();
    public List<CustomerContractDto> CustomerContracts { get; set; } = new();
    public List<ItemDto> Items { get; set; } = new();
    public List<ItemWarehouseStockDto> ItemWarehouseStocks { get; set; } = new();
    public List<TransactionDto> Transactions { get; set; } = new();
    public List<TransactionAttachmentDto> TransactionAttachments { get; set; } = new();
    public List<InventoryTransferDto> InventoryTransfers { get; set; } = new();
    public List<RentalManagementCompanyDto> RentalManagementCompanies { get; set; } = new();
    public List<RentalBillingProfileDto> RentalBillingProfiles { get; set; } = new();
    public List<RentalAssetDto> RentalAssets { get; set; } = new();
    public List<RentalBillingLogDto> RentalBillingLogs { get; set; } = new();
    public List<InvoiceDto> Invoices { get; set; } = new();
    public List<PaymentDto> Payments { get; set; } = new();
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

public sealed class SyncStatusDto
{
    public long CurrentServerRevision { get; set; }
    public DateTime ServerUtc { get; set; } = DateTime.UtcNow;
}

public sealed class RecycleBinEntryDto
{
    public Guid EntityId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string KindText { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public DateTime DeletedAtUtc { get; set; }
}

public sealed class RecycleBinMutationTargetDto
{
    public Guid EntityId { get; set; }
    public string Kind { get; set; } = string.Empty;
}

public sealed class RecycleBinMutationRequest
{
    public List<RecycleBinMutationTargetDto> Items { get; set; } = new();
}

public sealed class RecycleBinMutationResultDto
{
    public int RequestedCount { get; set; }
    public int SucceededCount { get; set; }
    public List<string> Messages { get; set; } = new();
}

public sealed class TenantDefinitionDto : SyncEntityDto
{
    public string TenantCode { get; set; } = TenantScopeCatalog.UsenetGroup;
    public string DisplayName { get; set; } = string.Empty;
    public string StorageMode { get; set; } = TenantScopeCatalog.StorageSharedDatabase;
    public string Description { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}

public sealed class TenantOfficeDefinitionDto : SyncEntityDto
{
    public string TenantCode { get; set; } = TenantScopeCatalog.UsenetGroup;
    public string OfficeCode { get; set; } = OfficeCodeCatalog.Usenet;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsHeadOffice { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class DataSharingPolicyDto : SyncEntityDto
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

public sealed class UpsertDataSharingPolicyRequest
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
    public bool IsActive { get; set; } = true;
    public string Note { get; set; } = string.Empty;
}

public sealed class TenantConfigurationSnapshotDto
{
    public List<TenantDefinitionDto> Tenants { get; set; } = new();
    public List<TenantOfficeDefinitionDto> Offices { get; set; } = new();
    public List<DataSharingPolicyDto> SharingPolicies { get; set; } = new();
}

public sealed class UpdateTenantDefinitionRequest
{
    public string DisplayName { get; set; } = string.Empty;
    public string StorageMode { get; set; } = TenantScopeCatalog.StorageSharedDatabase;
    public string Description { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}

public sealed class UpdateTenantOfficeDefinitionRequest
{
    public string DisplayName { get; set; } = string.Empty;
    public bool IsHeadOffice { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class AppUpdateManifestDto
{
    public string Channel { get; set; } = "stable";
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public AppUpdatePackageDto? Desktop { get; set; }
    public AppUpdatePackageDto? Android { get; set; }
}

public sealed class AppUpdatePackageDto
{
    public string Platform { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public bool Mandatory { get; set; }
    public string PackageUrl { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string Notes { get; set; } = string.Empty;
    public DateTime ReleasedAtUtc { get; set; } = DateTime.UtcNow;
}
