namespace 거래플랜.Desktop.App.Printing;

public sealed class InvoicePrintModel
{
    public Guid InvoiceId { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public DateOnly InvoiceDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public string VoucherType { get; set; } = string.Empty;

    public string SupplierBusinessNumber { get; set; } = string.Empty;
    public string SupplierName { get; set; } = string.Empty;
    public string SupplierRepresentative { get; set; } = string.Empty;
    public string SupplierPhone { get; set; } = string.Empty;
    public string SupplierAddress { get; set; } = string.Empty;

    public string BuyerBusinessNumber { get; set; } = string.Empty;
    public string BuyerName { get; set; } = string.Empty;
    public string BuyerRepresentative { get; set; } = string.Empty;
    public string BuyerPhone { get; set; } = string.Empty;
    public string BuyerAddress { get; set; } = string.Empty;

    public string ManagerName { get; set; } = string.Empty;
    public string Memo { get; set; } = string.Empty;
    public string DocumentTitle { get; set; } = string.Empty;
    public string FooterText { get; set; } = string.Empty;
    public string BankAccountText { get; set; } = string.Empty;
    public byte[]? SupplierStampImage { get; set; }

    public bool PrintWithDate { get; set; } = true;
    public bool PrintWithPrice { get; set; } = true;

    public decimal SupplyAmount { get; set; }
    public decimal VatAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal BalanceAmount { get; set; }

    public List<InvoicePrintLineModel> Lines { get; set; } = new();
}

public sealed class InvoicePrintLineModel
{
    public int No { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public string Specification { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Amount { get; set; }
    public string Remark { get; set; } = string.Empty;
}
