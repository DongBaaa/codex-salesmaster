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
    public string ProfileName { get; set; } = string.Empty;
    public string OfficeCode { get; set; } = DomainConstants.OfficeUsenet;
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

public sealed class LocalPriceGradeOption : LocalSyncEntity
{
    public string Name { get; set; } = string.Empty;
    public string PriceSource { get; set; } = SelectionOptionDefaults.PriceSourceSales;
    public int SortOrder { get; set; }
    public bool IsSystemDefault { get; set; }
    public bool IsActive { get; set; } = true;
    public string PriceSourceDisplay => SelectionOptionDefaults.GetPriceSourceDisplayName(PriceSource);
}

public sealed class LocalTradeTypeOption : LocalSyncEntity
{
    public string Name { get; set; } = string.Empty;
    public bool AllowsSales { get; set; } = true;
    public bool AllowsPurchase { get; set; }
    public int SortOrder { get; set; }
    public bool IsSystemDefault { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class LocalItemCategoryOption : LocalSyncEntity
{
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsSystemDefault { get; set; }
    public bool IsActive { get; set; } = true;
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
    public string TradeType { get; set; } = CustomerTradeTypes.Sales;
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
    public string ResponsibleOfficeCode { get; set; } = DomainConstants.OfficeUsenet;
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
    public string ResponsibleOfficeCode { get; set; } = DomainConstants.OfficeUsenet;
    public string SourceWarehouseCode { get; set; } = DomainConstants.WarehouseUsenetMain;
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

public sealed class LocalInventoryTransfer : LocalSyncEntity
{
    public string TransferNumber { get; set; } = string.Empty;
    public DateOnly TransferDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public string FromWarehouseCode { get; set; } = DomainConstants.WarehouseUsenetMain;
    public string ToWarehouseCode { get; set; } = DomainConstants.WarehouseYeonsuMain;
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
    public ICollection<LocalInventoryTransferLine> Lines { get; set; } = new List<LocalInventoryTransferLine>();
}

public sealed class LocalInventoryTransferLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TransferId { get; set; }
    public LocalInventoryTransfer? Transfer { get; set; }
    public Guid? ItemId { get; set; }
    public string ItemNameOriginal { get; set; } = string.Empty;
    public string SpecificationOriginal { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public decimal Quantity { get; set; } = 1m;
    public decimal? ReceivedQuantity { get; set; }
    public decimal? QuantityDifference { get; set; }
    public string Remark { get; set; } = string.Empty;
    public string ReceiptRemark { get; set; } = string.Empty;
    public bool IsDeleted { get; set; }
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
    public string ResponsibleOfficeCode { get; set; } = DomainConstants.OfficeUsenet;
    public DateOnly TransactionDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public string TransactionKind { get; set; } = PaymentFlowConstants.TransactionKindReceipt;
    public Guid? LinkedInvoiceId { get; set; }
    public string LinkedInvoiceNumber { get; set; } = string.Empty;
    public Guid? LinkedRentalBillingProfileId { get; set; }
    public decimal SettlementAmount { get; set; }
    public decimal AdvanceDelta { get; set; }

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
    public ICollection<LocalTransactionAttachment> Attachments { get; set; } = new List<LocalTransactionAttachment>();
}

public sealed class LocalTransactionAttachment : LocalSyncEntity
{
    public Guid TransactionId { get; set; }
    public LocalTransaction? Transaction { get; set; }
    public string AttachmentType { get; set; } = "기타";
    public string FileName { get; set; } = string.Empty;
    public string StoredFileName { get; set; } = string.Empty;
    public string StoredPath { get; set; } = string.Empty;
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
}

public sealed class LocalRentalManagementCompany : LocalSyncEntity
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsSystemDefault { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class LocalRentalBillingProfile : LocalSyncEntity
{
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
    public string SettlementStatus { get; set; } = PaymentFlowConstants.SettlementStatusUnpaid;
    public string CompletionStatus { get; set; } = PaymentFlowConstants.CompletionPending;
    public decimal SettledAmount { get; set; }
    public decimal OutstandingAmount { get; set; }
    public bool RequiresFollowUp { get; set; }
    public string FollowUpNote { get; set; } = string.Empty;
    public DateOnly? LastSettledDate { get; set; }
    public string ResponsibleOfficeCode { get; set; } = DomainConstants.OfficeUsenet;
    public string AssignedUsername { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}

public sealed class LocalRentalAsset : LocalSyncEntity
{
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
    public string ResponsibleOfficeCode { get; set; } = DomainConstants.OfficeUsenet;
    public string AssignedUsername { get; set; } = string.Empty;
    public string AssetStatus { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
}

public sealed class LocalRentalBillingLog : LocalSyncEntity
{
    public Guid BillingProfileId { get; set; }
    public string BillingYearMonth { get; set; } = string.Empty;
    public DateOnly ScheduledDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public DateOnly? ProcessedDate { get; set; }
    public string ProcessedByUsername { get; set; } = string.Empty;
    public string Status { get; set; } = "예정";
    public decimal BilledAmount { get; set; }
    public string Note { get; set; } = string.Empty;
    public string ResponsibleOfficeCode { get; set; } = DomainConstants.OfficeUsenet;
    public string AssignedUsername { get; set; } = string.Empty;
}

