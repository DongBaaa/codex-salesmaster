using CommunityToolkit.Mvvm.ComponentModel;
using SalesMaster.Desktop.App.Data;

namespace SalesMaster.Desktop.App.ViewModels;

public sealed partial class InventoryTransferLineEditModel : ObservableObject
{
    [ObservableProperty] private Guid? _itemId;
    [ObservableProperty] private string _itemName = string.Empty;
    [ObservableProperty] private string _specification = string.Empty;
    [ObservableProperty] private string _unit = string.Empty;
    [ObservableProperty] private decimal _quantity = 1m;
    [ObservableProperty] private string _remark = string.Empty;

    public LocalInventoryTransferLine ToLocal(Guid transferId) => new()
    {
        Id = Guid.NewGuid(),
        TransferId = transferId,
        ItemId = ItemId,
        ItemNameOriginal = ItemName ?? string.Empty,
        SpecificationOriginal = Specification ?? string.Empty,
        Unit = Unit ?? string.Empty,
        Quantity = Quantity,
        Remark = Remark ?? string.Empty,
        IsDeleted = false
    };

    public static InventoryTransferLineEditModel FromLocal(LocalInventoryTransferLine line) => new()
    {
        ItemId = line.ItemId,
        ItemName = line.ItemNameOriginal ?? string.Empty,
        Specification = line.SpecificationOriginal ?? string.Empty,
        Unit = line.Unit ?? string.Empty,
        Quantity = line.Quantity,
        Remark = line.Remark ?? string.Empty
    };
}
