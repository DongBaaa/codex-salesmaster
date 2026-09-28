using 거래플랜.Shared.Contracts;
using System.ComponentModel;

namespace GeoraePlan.Mobile.App.Models;

public sealed class InvoiceLineDraftItem : INotifyPropertyChanged
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ItemId { get; set; }
    public string CategoryName { get; set; } = string.Empty;
    public string ItemNameOriginal { get; set; } = string.Empty;
    public string SpecificationOriginal { get; set; } = string.Empty;
    public string Unit { get; set; } = "EA";
    public decimal Quantity { get; set; } = 1m;
    public decimal UnitPrice { get; set; }
    private bool _amountsHidden;
    public event PropertyChangedEventHandler? PropertyChanged;
    public bool AmountsHidden
    {
        get => _amountsHidden;
        set
        {
            if (_amountsHidden == value) return;
            _amountsHidden = value;
            PropertyChanged?.Invoke(this, new(nameof(AmountsHidden)));
            PropertyChanged?.Invoke(this, new(nameof(PriceSummary)));
            PropertyChanged?.Invoke(this, new(nameof(AmountSummary)));
        }
    }
    public string Remark { get; set; } = string.Empty;
    public string MaterialNumber { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public string InstallLocation { get; set; } = string.Empty;
    public DateOnly? RentalStartDate { get; set; }
    public DateOnly? RentalEndDate { get; set; }
    public int OrderIndex { get; set; }
    public decimal LineAmount => Math.Round(Quantity * UnitPrice, 2, MidpointRounding.AwayFromZero);
    public string PriceSummary => AmountsHidden ? $"수량 {Quantity:N0} / 단가 비공개" : $"수량 {Quantity:N0} / 단가 {UnitPrice:N0}원";
    public string AmountSummary => AmountsHidden ? "합계 비공개" : $"합계 {LineAmount:N0}원";
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

    public static InvoiceLineDraftItem FromItem(ItemDto item, decimal quantity = 1m, bool amountsHidden = false, bool purchase = false)
    {
        amountsHidden |= purchase ? item.PurchaseAmountsHidden : item.SalesAmountsHidden;
        var unitPrice = purchase ? item.PurchasePrice : item.SalePrice > 0m
            ? item.SalePrice
            : item.RetailPrice > 0m
                ? item.RetailPrice
                : item.PurchasePrice > 0m
                    ? item.PurchasePrice
                    : 0m;

        return new InvoiceLineDraftItem
        {
            ItemId = item.Id == Guid.Empty ? null : item.Id,
            CategoryName = item.CategoryName,
            ItemNameOriginal = item.NameOriginal,
            SpecificationOriginal = item.SpecificationOriginal,
            Unit = string.IsNullOrWhiteSpace(item.Unit) ? "EA" : item.Unit,
            Quantity = quantity <= 0m ? 1m : quantity,
            UnitPrice = amountsHidden ? 0m : DisclosedAmount.Require(unitPrice),
            AmountsHidden = amountsHidden,
            Remark = string.Empty,
            MaterialNumber = item.MaterialNumber,
            SerialNumber = item.SerialNumber,
            InstallLocation = item.InstallLocation,
            RentalStartDate = item.RentalStartDate,
            RentalEndDate = item.RentalEndDate
        };
    }

    public static InvoiceLineDraftItem FromEditor(ItemDto item, InvoiceLineDraftItem? existing,
        decimal quantity, decimal unitPrice, string remark, bool amountsHidden, bool purchase = false)
    {
        var draft = FromItem(item, quantity, amountsHidden, purchase);
        if (existing is not null)
        {
            draft.Id = existing.Id;
            draft.OrderIndex = existing.OrderIndex;
            if (draft.ItemId == existing.ItemId)
            {
                // Quantity/remark edits retain the invoice snapshot used by server price matching.
                draft.CategoryName = existing.CategoryName;
                draft.ItemNameOriginal = existing.ItemNameOriginal;
                draft.SpecificationOriginal = existing.SpecificationOriginal;
                draft.Unit = existing.Unit;
                draft.MaterialNumber = existing.MaterialNumber;
                draft.SerialNumber = existing.SerialNumber;
                draft.InstallLocation = existing.InstallLocation;
                draft.RentalStartDate = existing.RentalStartDate;
                draft.RentalEndDate = existing.RentalEndDate;
                draft.AmountsHidden |= existing.AmountsHidden;
            }
        }
        draft.UnitPrice = draft.AmountsHidden ? 0m : unitPrice;
        draft.Remark = remark.Trim();
        return draft;
    }

    public static InvoiceLineDraftItem FromDto(InvoiceLineDto line, bool forceHideAmounts = false)
        => new()
        {
            Id = line.Id == Guid.Empty ? Guid.NewGuid() : line.Id,
            ItemId = line.ItemId,
            CategoryName = string.Empty,
            ItemNameOriginal = line.ItemNameOriginal,
            SpecificationOriginal = line.SpecificationOriginal,
            Unit = string.IsNullOrWhiteSpace(line.Unit) ? "EA" : line.Unit,
            Quantity = line.Quantity <= 0m ? 1m : line.Quantity,
            AmountsHidden = line.AmountsHidden || forceHideAmounts,
            UnitPrice = line.AmountsHidden || forceHideAmounts ? 0m : DisclosedAmount.Require(line.UnitPrice),
            Remark = line.Remark,
            MaterialNumber = line.MaterialNumber,
            SerialNumber = line.SerialNumber,
            InstallLocation = line.InstallLocation,
            RentalStartDate = line.RentalStartDate,
            RentalEndDate = line.RentalEndDate,
            OrderIndex = line.OrderIndex
        };

    public InvoiceLineDto ToDto(Guid invoiceId, bool forceHideAmounts = false)
        => new()
        {
            Id = Id,
            InvoiceId = invoiceId,
            ItemId = ItemId,
            ItemNameOriginal = ItemNameOriginal,
            SpecificationOriginal = SpecificationOriginal,
            Unit = Unit,
            Quantity = Quantity,
            UnitPrice = AmountsHidden || forceHideAmounts ? null : UnitPrice,
            LineAmount = AmountsHidden || forceHideAmounts ? null : LineAmount,
            Remark = Remark,
            MaterialNumber = MaterialNumber,
            SerialNumber = SerialNumber,
            InstallLocation = InstallLocation,
            RentalStartDate = RentalStartDate,
            RentalEndDate = RentalEndDate,
            OrderIndex = OrderIndex
        };
}
