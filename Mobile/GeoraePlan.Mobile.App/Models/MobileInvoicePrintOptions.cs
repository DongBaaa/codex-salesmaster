namespace GeoraePlan.Mobile.App.Models;

public sealed class MobileInvoicePrintOptions
{
    public bool PrintStatementDocument { get; set; } = true;
    public bool PrintEstimateDocument { get; set; }
    public bool PrintPaymentClaimDocument { get; set; }
    public bool PrintDate { get; set; } = true;
    public bool PrintUnitPrice { get; set; } = true;

    public bool HasAnyDocument =>
        PrintStatementDocument ||
        PrintEstimateDocument ||
        PrintPaymentClaimDocument;
}
