using CommunityToolkit.Mvvm.ComponentModel;
using SalesMaster.Desktop.App.Data;

namespace SalesMaster.Desktop.App.ViewModels;

public sealed partial class InventoryTransferLineEditModel : ObservableObject
{
    [ObservableProperty] private Guid _id;
    [ObservableProperty] private Guid? _itemId;
    [ObservableProperty] private string _itemName = string.Empty;
    [ObservableProperty] private string _specification = string.Empty;
    [ObservableProperty] private string _unit = string.Empty;
    [ObservableProperty] private decimal _quantity = 1m;
    [ObservableProperty] private decimal _receivedQuantity = 1m;
    [ObservableProperty] private string _remark = string.Empty;
    [ObservableProperty] private string _receiptRemark = string.Empty;

    public decimal QuantityDifference => ReceivedQuantity - Quantity;

    partial void OnQuantityChanged(decimal value) => OnPropertyChanged(nameof(QuantityDifference));
    partial void OnReceivedQuantityChanged(decimal value) => OnPropertyChanged(nameof(QuantityDifference));

    public LocalInventoryTransferLine ToLocal(Guid transferId) => new()
    {
        Id = Id == Guid.Empty ? Guid.NewGuid() : Id,
        TransferId = transferId,
        ItemId = ItemId,
        ItemNameOriginal = ItemName ?? string.Empty,
        SpecificationOriginal = Specification ?? string.Empty,
        Unit = Unit ?? string.Empty,
        Quantity = Quantity,
        ReceivedQuantity = ReceivedQuantity,
        QuantityDifference = QuantityDifference,
        Remark = Remark ?? string.Empty,
        ReceiptRemark = ReceiptRemark ?? string.Empty,
        IsDeleted = false
    };

    public static InventoryTransferLineEditModel FromLocal(LocalInventoryTransferLine line) => new()
    {
        Id = line.Id,
        ItemId = line.ItemId,
        ItemName = line.ItemNameOriginal ?? string.Empty,
        Specification = line.SpecificationOriginal ?? string.Empty,
        Unit = line.Unit ?? string.Empty,
        Quantity = line.Quantity,
        ReceivedQuantity = line.ReceivedQuantity ?? line.Quantity,
        Remark = line.Remark ?? string.Empty,
        ReceiptRemark = line.ReceiptRemark ?? string.Empty
    };
}
