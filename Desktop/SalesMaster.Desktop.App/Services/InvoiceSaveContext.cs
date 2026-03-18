namespace SalesMaster.Desktop.App.Services;

public sealed class InvoiceSaveContext
{
    public string Username { get; init; } = string.Empty;
    public string Role { get; init; } = string.Empty;
    public string OfficeCode { get; init; } = DomainConstants.OfficeUsenet;
    public bool ForceOverride { get; init; }
    public string? ExpectedConcurrencyStamp { get; init; }
}

