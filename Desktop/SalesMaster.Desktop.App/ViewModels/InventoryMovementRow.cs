namespace SalesMaster.Desktop.App.ViewModels;

public sealed class InventoryMovementRow
{
    public DateOnly OccurredDate { get; init; }
    public string OfficeDisplay { get; init; } = string.Empty;
    public string WarehouseDisplay { get; init; } = string.Empty;
    public string MovementTypeDisplay { get; init; } = string.Empty;
    public decimal QuantityDelta { get; init; }
    public string Note { get; init; } = string.Empty;
    public string CreatedByUsername { get; init; } = string.Empty;
}
