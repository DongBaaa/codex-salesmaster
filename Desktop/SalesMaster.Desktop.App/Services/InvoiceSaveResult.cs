namespace SalesMaster.Desktop.App.Services;

public sealed class InvoiceSaveResult
{
    public bool Success { get; init; }
    public bool ConcurrencyConflict { get; init; }
    public bool PermissionDenied { get; init; }
    public bool NotFound { get; init; }
    public Guid SavedInvoiceId { get; init; }
    public string SavedConcurrencyStamp { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;

    public static InvoiceSaveResult Ok(Guid invoiceId, string stamp, string message = "") => new()
    {
        Success = true,
        SavedInvoiceId = invoiceId,
        SavedConcurrencyStamp = stamp,
        Message = message
    };

    public static InvoiceSaveResult Conflict(string message) => new()
    {
        Success = false,
        ConcurrencyConflict = true,
        Message = message
    };

    public static InvoiceSaveResult Denied(string message) => new()
    {
        Success = false,
        PermissionDenied = true,
        Message = message
    };

    public static InvoiceSaveResult Missing(string message) => new()
    {
        Success = false,
        NotFound = true,
        Message = message
    };
}
