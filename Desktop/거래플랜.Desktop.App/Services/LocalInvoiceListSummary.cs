using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

public sealed class LocalInvoiceListSummary
{
    public Guid Id { get; set; }
    public Guid VersionGroupId { get; set; }
    public Guid CustomerId { get; set; }
    public string ResponsibleOfficeCode { get; set; } = string.Empty;
    public Guid? LinkedRentalBillingProfileId { get; set; }
    public Guid? LinkedRentalBillingRunId { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public string LocalTempNumber { get; set; } = string.Empty;
    public string TaxInvoiceNumber { get; set; } = string.Empty;
    public DateOnly InvoiceDate { get; set; }
    public VoucherType VoucherType { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal SupplyAmount { get; set; }
    public decimal VatAmount { get; set; }
    public string VatMode { get; set; } = InvoiceVatModes.Included;
    public decimal SettledAmount { get; set; }
    public bool TaxInvoiceIssued { get; set; }
    public bool PurchaseReceivingRequired { get; set; }
    public string PurchaseReceivingStatus { get; set; } = InvoiceReceivingStatuses.NotApplicable;
    public bool IsDirty { get; set; }
    public long Revision { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime LastSavedAtUtc { get; set; }
    public int VersionNumber { get; set; }
    public string FirstItemSummary { get; set; } = "(품목 없음)";
}
