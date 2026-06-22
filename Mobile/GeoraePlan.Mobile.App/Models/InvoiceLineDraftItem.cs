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
    public int OrderIndex { get; set; }
    public decimal LineAmount => Math.Round(Quantity * UnitPrice, 2, MidpointRounding.AwayFromZero);
    public string IdentitySummary
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(SpecificationOriginal))
                parts.Add($"규격 {SpecificationOriginal.Trim()}");
            if (!string.IsNullOrWhiteSpace(MaterialNumber))
                parts.Add($"자재 {MaterialNumber.Trim()}");
            if (!string.IsNullOrWhiteSpace(SerialNumber))
                parts.Add($"S/N {SerialNumber.Trim()}");

            return parts.Count == 0 ? "규격/자재번호 없음" : string.Join(" · ", parts);
        }
    }

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
            Remark = string.Empty,
            MaterialNumber = item.MaterialNumber,
            SerialNumber = item.SerialNumber,
            InstallLocation = item.InstallLocation,
            RentalStartDate = item.RentalStartDate,
            RentalEndDate = item.RentalEndDate
        };
    }

    public static InvoiceLineDraftItem FromDto(InvoiceLineDto line)
        => new()
        {
            Id = line.Id == Guid.Empty ? Guid.NewGuid() : line.Id,
            ItemId = line.ItemId,
            CategoryName = string.Empty,
            ItemNameOriginal = line.ItemNameOriginal,
            SpecificationOriginal = line.SpecificationOriginal,
            Unit = string.IsNullOrWhiteSpace(line.Unit) ? "EA" : line.Unit,
            Quantity = line.Quantity <= 0m ? 1m : line.Quantity,
            UnitPrice = line.UnitPrice,
            Remark = line.Remark,
            MaterialNumber = line.MaterialNumber,
            SerialNumber = line.SerialNumber,
            InstallLocation = line.InstallLocation,
            RentalStartDate = line.RentalStartDate,
            RentalEndDate = line.RentalEndDate,
            OrderIndex = line.OrderIndex
        };

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
            RentalEndDate = RentalEndDate,
            OrderIndex = OrderIndex
        };
}
