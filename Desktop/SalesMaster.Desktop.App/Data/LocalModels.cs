using SalesMaster.Desktop.App.Services;
using SalesMaster.Shared.Contracts;

namespace SalesMaster.Desktop.App.Data;

public interface ILocalSyncEntity
{
    Guid Id { get; set; }
    bool IsDeleted { get; set; }
    DateTime CreatedAtUtc { get; set; }
    DateTime UpdatedAtUtc { get; set; }
    long Revision { get; set; }
    bool IsDirty { get; set; }
}

public abstract class LocalSyncEntity : ILocalSyncEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool IsDeleted { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public long Revision { get; set; }
    public bool IsDirty { get; set; } = true;
}

public sealed class LocalCompanyProfile : LocalSyncEntity
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

public sealed class LocalUnit : LocalSyncEntity
{
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}

public sealed class LocalCustomerCategory : LocalSyncEntity
{
    public string Name { get; set; } = string.Empty;
    public bool IsSystemDefault { get; set; }
}

public sealed class LocalCustomerMaster : LocalSyncEntity
{
    public string NameOriginal { get; set; } = string.Empty;
    public string NameMatchKey { get; set; } = string.Empty;
    public Guid? CategoryId { get; set; }
}

public sealed class LocalCustomer : LocalSyncEntity
{
    public Guid? CustomerMasterId { get; set; }
    public string NameOriginal { get; set; } = string.Empty;
    public string NameMatchKey { get; set; } = string.Empty;
    public Guid? CategoryId { get; set; }
    public string Department { get; set; } = string.Empty;
    public string ContactPerson { get; set; } = string.Empty;
    public string BusinessNumber { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string DetailAddress { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string MobilePhone { get; set; } = string.Empty;
    public string FaxNumber { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string HomePage { get; set; } = string.Empty;
    public string Representative { get; set; } = string.Empty;
    public string BusinessType { get; set; } = string.Empty;
    public string BusinessItem { get; set; } = string.Empty;
    public string Recipient { get; set; } = string.Empty;
    public string PriceGrade { get; set; } = "매출단가";
    public string Notes { get; set; } = string.Empty;
    public string ResponsibleOfficeCode { get; set; } = DomainConstants.OfficeUznet;
}

public sealed class LocalItem : LocalSyncEntity
{
    public string NameOriginal { get; set; } = string.Empty;
    public string NameMatchKey { get; set; } = string.Empty;
    public string SpecificationOriginal { get; set; } = string.Empty;
    public string SpecificationMatchKey { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public decimal BoxQuantity { get; set; }
    public string StorageLocation { get; set; } = string.Empty;
    public decimal CurrentStock { get; set; }
    public decimal SafetyStock { get; set; }
    public decimal PurchasePrice { get; set; }
    public decimal SalePrice { get; set; }
    public decimal RetailPrice { get; set; }
    public decimal PriceGradeA { get; set; }
    public decimal PriceGradeB { get; set; }
    public decimal PriceGradeC { get; set; }
    public DateOnly? LastPurchaseDate { get; set; }
    public DateOnly? LastSaleDate { get; set; }
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

public sealed class LocalInvoice : LocalSyncEntity
{
    public Guid CustomerId { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public string LocalTempNumber { get; set; } = string.Empty;
    public VoucherType VoucherType { get; set; }
    public DateOnly InvoiceDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public decimal TotalAmount { get; set; }
    public decimal SupplyAmount { get; set; }
    public decimal VatAmount { get; set; }
    public string Memo { get; set; } = string.Empty;
    public string ResponsibleOfficeCode { get; set; } = DomainConstants.OfficeUznet;
    public string SourceWarehouseCode { get; set; } = DomainConstants.WarehouseUznetMain;
    public Guid? DeliveryGroupId { get; set; }
    public Guid? ParentInvoiceId { get; set; }
    public Guid VersionGroupId { get; set; }
    public int VersionNumber { get; set; } = 1;
    public Guid? PreviousVersionId { get; set; }
    public bool IsLatestVersion { get; set; } = true;
    public bool IsConfirmed { get; set; } = true;
    public string CreatedByUsername { get; set; } = string.Empty;
    public string LastSavedByUsername { get; set; } = string.Empty;
    public DateTime LastSavedAtUtc { get; set; } = DateTime.UtcNow;
    public string ConcurrencyStamp { get; set; } = Guid.NewGuid().ToString("N");
    public string CostStatus { get; set; } = "Pending";
    public ICollection<LocalInvoiceLine> Lines { get; set; } = new List<LocalInvoiceLine>();
    public ICollection<LocalPayment> Payments { get; set; } = new List<LocalPayment>();
}

public sealed class LocalInvoiceLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InvoiceId { get; set; }
    public LocalInvoice? Invoice { get; set; }
    public Guid? ItemId { get; set; }
    public string ItemNameOriginal { get; set; } = string.Empty;
    public string SpecificationOriginal { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public decimal Quantity { get; set; } = 1m;
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

public sealed class LocalPayment : LocalSyncEntity
{
    public Guid InvoiceId { get; set; }
    public LocalInvoice? Invoice { get; set; }
    public DateOnly PaymentDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public decimal Amount { get; set; }
    public string Note { get; set; } = string.Empty;
}

public sealed class LocalSetting
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

public sealed class LocalRecentSelection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string EntityType { get; set; } = string.Empty;
    public Guid EntityId { get; set; }
    public string DisplayText { get; set; } = string.Empty;
    public DateTime LastUsedAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsFavorite { get; set; }
}

public sealed class LocalAttachmentSelection
{
    public string CustomerKey { get; set; } = string.Empty;
    public string DocCode { get; set; } = string.Empty;
    public bool IsChecked { get; set; }
    public int? OrderIndex { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class LocalOffice : LocalSyncEntity
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsHeadOffice { get; set; }
}

public sealed class LocalWarehouse : LocalSyncEntity
{
    public Guid OfficeId { get; set; }
    public string OfficeCode { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}

public sealed class LocalInvoiceLineSerial
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InvoiceId { get; set; }
    public Guid InvoiceLineId { get; set; }
    public Guid? ItemId { get; set; }
    public string SerialNumber { get; set; } = string.Empty;
}

public sealed class LocalInventoryMovement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? InvoiceId { get; set; }
    public Guid? InvoiceLineId { get; set; }
    public Guid? ItemId { get; set; }
    public string WarehouseCode { get; set; } = string.Empty;
    public string MovementType { get; set; } = string.Empty;
    public decimal QuantityDelta { get; set; }
    public decimal UnitCost { get; set; }
    public decimal Amount { get; set; }
    public DateOnly OccurredDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public bool IsSettledCost { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public string Note { get; set; } = string.Empty;
    public string CreatedByUsername { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class LocalStockLayer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ItemId { get; set; }
    public string WarehouseCode { get; set; } = string.Empty;
    public Guid? SourceInvoiceId { get; set; }
    public Guid? SourceInvoiceLineId { get; set; }
    public DateOnly ReceiptDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public decimal UnitCost { get; set; }
    public decimal OriginalQuantity { get; set; }
    public decimal RemainingQuantity { get; set; }
    public bool IsNegativePlaceholder { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class LocalCostAllocation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SalesInvoiceId { get; set; }
    public Guid SalesInvoiceLineId { get; set; }
    public Guid? PurchaseInvoiceId { get; set; }
    public Guid? PurchaseInvoiceLineId { get; set; }
    public string WarehouseCode { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }
    public decimal CostAmount { get; set; }
    public bool IsUnsettled { get; set; }
    public string Note { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class LocalItemWarehouseStock
{
    public Guid ItemId { get; set; }
    public string WarehouseCode { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class LocalSerialLedger
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string SerialNumber { get; set; } = string.Empty;
    public Guid? ItemId { get; set; }
    public string WarehouseCode { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public Guid? SourcePurchaseInvoiceId { get; set; }
    public Guid? SourceSalesInvoiceId { get; set; }
    public Guid? LastInvoiceId { get; set; }
    public string LastMovementType { get; set; } = string.Empty;
    public string Memo { get; set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class LocalAuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string EntityName { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string OfficeCode { get; set; } = string.Empty;
    public string BeforeJson { get; set; } = string.Empty;
    public string AfterJson { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 고객/거래처 수금·지불 독립 전표 (인보이스에 귀속되지 않는 현금/카드/통장 거래)
/// </summary>
public sealed class LocalTransaction : LocalSyncEntity
{
    public Guid CustomerId { get; set; }
    public string ResponsibleOfficeCode { get; set; } = DomainConstants.OfficeUznet;
    public DateOnly TransactionDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);

    // 수금처리내역
    public decimal CashReceipt { get; set; }
    public decimal CardReceipt { get; set; }
    public decimal BankReceipt { get; set; }
    public decimal DiscountApplied { get; set; }   // D.C 해움
    public decimal ReceiptTotal { get; set; }       // 수금결제 합계

    // 지불처리내역
    public decimal CashPayment { get; set; }
    public decimal CardPayment { get; set; }
    public decimal BankPayment { get; set; }
    public decimal DiscountReceived { get; set; }   // D.C 받음
    public decimal PaymentTotal { get; set; }       // 지불결제 합계

    public string Note { get; set; } = string.Empty;  // 거래내용
    public string Memo { get; set; } = string.Empty;  // 전표메모
}
