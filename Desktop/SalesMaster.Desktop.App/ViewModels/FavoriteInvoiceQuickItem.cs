namespace SalesMaster.Desktop.App.ViewModels;

public sealed class FavoriteInvoiceQuickItem
{
    public required Guid InvoiceId { get; init; }
    public required string DisplayText { get; init; }
}
