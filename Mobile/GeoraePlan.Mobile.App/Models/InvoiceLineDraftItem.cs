using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Models;

public sealed class InvoiceLineDraftItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ItemId { get; set; }
    public string CategoryName { get; set; } = string.Empty;
    public string ItemNameOriginal { get; set; } = string.Empty;
    public string SpecificationOriginal { get; set; } = string.Empty;
    public string Unit { get; set; } = "EA";
    public decimal Quantity { get; set; } = 1m;
    public decimal UnitPrice { get; set; }
    public string Remark { get; set; } = string.Empty;
    public string MaterialNumber { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public string InstallLocation { get; set; } = string.Empty;
    public DateOnly? RentalStartDate { get; set; }
    public DateOnly? RentalEndDate { get; set; }
    public decimal LineAmount => Math.Round(Quantity * UnitPrice, 2, MidpointRounding.AwayFromZero);

    public static InvoiceLineDraftItem FromItem(ItemDto item, decimal quantity = 1m)
    {
        var unitPrice = item.SalePrice > 0m
            ? item.SalePrice
            : item.RetailPrice > 0m
                ? item.RetailPrice
                : item.PurchasePrice > 0m
                    ? item.PurchasePrice
                    : 0m;

        return new InvoiceLineDraftItem
        {
            ItemId = item.Id,
            CategoryName = item.CategoryName,
            ItemNameOriginal = item.NameOriginal,
            SpecificationOriginal = item.SpecificationOriginal,
            Unit = string.IsNullOrWhiteSpace(item.Unit) ? "EA" : item.Unit,
            Quantity = quantity <= 0m ? 1m : quantity,
            UnitPrice = unitPrice,
            Remark = item.SimpleMemo,
            MaterialNumber = item.MaterialNumber,
            SerialNumber = item.SerialNumber,
            InstallLocation = item.InstallLocation,
            RentalStartDate = item.RentalStartDate,
            RentalEndDate = item.RentalEndDate
        };
    }

    public InvoiceLineDto ToDto(Guid invoiceId)
        => new()
        {
            Id = Id,
            InvoiceId = invoiceId,
            ItemId = ItemId,
            ItemNameOriginal = ItemNameOriginal,
            SpecificationOriginal = SpecificationOriginal,
            Unit = Unit,
            Quantity = Quantity,
            UnitPrice = UnitPrice,
            LineAmount = LineAmount,
            Remark = Remark,
            MaterialNumber = MaterialNumber,
            SerialNumber = SerialNumber,
            InstallLocation = InstallLocation,
            RentalStartDate = RentalStartDate,
            RentalEndDate = RentalEndDate
        };
}
