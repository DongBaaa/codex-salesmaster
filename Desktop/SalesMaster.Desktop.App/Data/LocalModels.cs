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

public sealed class LocalPrintTemplate : LocalSyncEntity
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
}

public sealed class LocalPrintTemplateVersion : LocalSyncEntity
{
    public Guid PrintTemplateId { get; set; }
    public int VersionNumber { get; set; }
    public string TemplateJson { get; set; } = string.Empty;
    public bool IsLocked { get; set; }
    public string CreatedByName { get; set; } = string.Empty;
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

/// <summary>
/// 고객/거래처 수금·지불 독립 전표 (인보이스에 귀속되지 않는 현금/카드/통장 거래)
/// </summary>
public sealed class LocalTransaction : LocalSyncEntity
{
    public Guid CustomerId { get; set; }
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
