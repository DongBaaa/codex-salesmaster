using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

public static class CustomerTradeTypes
{
    public const string Sales = CustomerClassificationNormalizer.Sales;
    public const string Purchase = CustomerClassificationNormalizer.Purchase;
    public const string SalesAndPurchase = CustomerClassificationNormalizer.SalesAndPurchase;

    public static IReadOnlyList<string> All { get; } = CustomerClassificationNormalizer.AllowedTradeTypes;

    public static string Normalize(string? value)
        => CustomerClassificationNormalizer.NormalizeTradeTypeOrDefault(value);

    public static bool TryNormalize(string? value, out string normalizedTradeType)
        => CustomerClassificationNormalizer.TryNormalizeTradeType(value, out normalizedTradeType);

    public static bool AllowsSales(string? tradeType)
    {
        var normalized = Normalize(tradeType);
        return normalized is Sales or SalesAndPurchase;
    }

    public static bool AllowsPurchase(string? tradeType)
    {
        var normalized = Normalize(tradeType);
        return normalized is Purchase or SalesAndPurchase;
    }
}
