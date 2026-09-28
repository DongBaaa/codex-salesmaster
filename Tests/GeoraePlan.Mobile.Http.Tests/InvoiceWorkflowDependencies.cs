global using GeoraePlan.Mobile.App.Services;
using GeoraePlan.Mobile.App.Models;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Services;

// Native PDF sharing and native alerts are not used by the saved-invoice workflow.
public sealed class MobileInvoicePdfExportService
{
    public Task<string> ExportAndShareAsync(InvoiceDto invoice, MobileInvoicePrintOptions options)
        => throw new NotSupportedException("PDF export is outside this HTTP workflow test.");
}
public static class MobileErrorHandler
{
    public static Task ShowAlertAsync(string title, string message)
        => throw new InvalidOperationException($"Unexpected native alert: {title}: {message}");
}
