using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Services;

/// <summary>Display and edit access only; server prices remain authoritative.</summary>
public readonly record struct MobileItemAmountAccess(bool Purchase, bool Sales, bool Edit)
{
    public static MobileItemAmountAccess Capture(bool authenticated, string role,
        IEnumerable<string> permissions, bool canEditItems)
    {
        if (!authenticated) return default;
        var admin = string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase);
        var grants = new HashSet<string>(permissions, StringComparer.OrdinalIgnoreCase);
        return new(admin || grants.Contains("Amount.ViewPurchase"),
            admin || grants.Contains("Amount.ViewSales"), canEditItems);
    }

    public string Summary(ItemDto item)
        => $"매입 {Money(item.PurchasePrice, Purchase)} / 판매 {Money(item.SalePrice, Sales)} / 소매 {Money(item.RetailPrice, Sales)}";

    public string ListSummary(ItemDto item)
    {
        // Never use an undisclosed purchase price as the sales fallback.
        if (Sales && item.SalesAmountsHidden) return "기본 단가 비공개";
        decimal? price = Sales && item.SalePrice > 0m ? item.SalePrice
            : Sales && item.RetailPrice > 0m ? item.RetailPrice
            : Purchase ? item.PurchasePrice : Sales ? 0m : null;
        return price is null ? "기본 단가 비공개"
            : price > 0 ? $"기본 단가 {price:N0}원" : "기본 단가 미등록";
    }

    public void ApplyEditedPrices(ItemDto target, ItemDto? source,
        decimal purchase, decimal sale, decimal retail)
    {
        target.PurchasePrice = Purchase && Edit && source?.PurchaseAmountsHidden != true ? purchase : source is null ? 0m : source.PurchasePrice;
        target.SalePrice = Sales && Edit && source?.SalesAmountsHidden != true ? sale : source is null ? 0m : source.SalePrice;
        target.RetailPrice = Sales && Edit && source?.SalesAmountsHidden != true ? retail : source is null ? 0m : source.RetailPrice;
        // The mobile basic editor has no grade-price controls.
        target.PriceGradeA = source is null ? 0m : source.PriceGradeA;
        target.PriceGradeB = source is null ? 0m : source.PriceGradeB;
        target.PriceGradeC = source is null ? 0m : source.PriceGradeC;
    }

    private static string Money(decimal? value, bool visible) => visible && value.HasValue ? $"{value:N0}원" : "비공개";
}
