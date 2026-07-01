namespace 거래플랜.Desktop.App.Services;

public sealed class LocalInvoiceDashboardMetrics
{
    public decimal MonthlySales { get; set; }
    public decimal PreviousMonthlySales { get; set; }
    public int MonthlyInvoiceCount { get; set; }
    public decimal Receivable { get; set; }
    public decimal Payable { get; set; }
}
