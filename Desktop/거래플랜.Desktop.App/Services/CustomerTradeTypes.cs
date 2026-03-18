namespace 거래플랜.Desktop.App.Services;

public static class CustomerTradeTypes
{
    public const string Sales = "매출";
    public const string Purchase = "매입";
    public const string SalesAndPurchase = "매출/매입";

    public static IReadOnlyList<string> All { get; } =
    [
        Sales,
        Purchase,
        SalesAndPurchase
    ];

    public static string Normalize(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return Sales;

        if (string.Equals(normalized, Sales, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "판매", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "매출처", StringComparison.OrdinalIgnoreCase))
        {
            return Sales;
        }

        if (string.Equals(normalized, Purchase, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "매입처", StringComparison.OrdinalIgnoreCase))
        {
            return Purchase;
        }

        if (string.Equals(normalized, SalesAndPurchase, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "판매/매입", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "매출매입", StringComparison.OrdinalIgnoreCase))
        {
            return SalesAndPurchase;
        }

        return normalized;
    }

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
