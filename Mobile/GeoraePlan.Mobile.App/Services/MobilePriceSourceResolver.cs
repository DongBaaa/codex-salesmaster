using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Services;

public static class MobilePriceSourceResolver
{
    public const string PriceSourceSales = "Sales";
    public const string PriceSourceA = "A";
    public const string PriceSourceB = "B";
    public const string PriceSourceC = "C";
    public const string PriceSourceRetail = "Retail";

    public static string NormalizePriceSource(string? source)
        => (source ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "A" => PriceSourceA,
            "B" => PriceSourceB,
            "C" => PriceSourceC,
            "RETAIL" or "소매" => PriceSourceRetail,
            _ => PriceSourceSales
        };

    public static string ResolveLegacyPriceSource(string? priceGrade)
    {
        var grade = (priceGrade ?? string.Empty).Trim().ToUpperInvariant();
        if (grade.StartsWith("A", StringComparison.Ordinal)) return PriceSourceA;
        if (grade.StartsWith("B", StringComparison.Ordinal)) return PriceSourceB;
        if (grade.StartsWith("C", StringComparison.Ordinal)) return PriceSourceC;
        if (grade.Contains("소매", StringComparison.OrdinalIgnoreCase)) return PriceSourceRetail;
        return PriceSourceSales;
    }

    public static decimal ResolveSalesUnitPrice(ItemDto item, string? customerPriceGrade, IReadOnlyDictionary<string, string> priceGradeSourceMap)
    {
        var grade = (customerPriceGrade ?? string.Empty).Trim();
        var priceSource = !string.IsNullOrWhiteSpace(grade) && priceGradeSourceMap.TryGetValue(grade, out var configuredSource)
            ? configuredSource
            : ResolveLegacyPriceSource(grade);

        return NormalizePriceSource(priceSource) switch
        {
            PriceSourceA when item.PriceGradeA > 0m => item.PriceGradeA,
            PriceSourceB when item.PriceGradeB > 0m => item.PriceGradeB,
            PriceSourceC when item.PriceGradeC > 0m => item.PriceGradeC,
            PriceSourceRetail when item.RetailPrice > 0m => item.RetailPrice,
            _ => item.SalePrice > 0m
                ? item.SalePrice
                : item.RetailPrice > 0m
                    ? item.RetailPrice
                    : 0m
        };
    }
}
