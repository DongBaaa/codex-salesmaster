namespace 거래플랜.Shared.Contracts;

public static class InvoiceVatModes
{
    public const string Included = "Included";
    public const string None = "None";

    public static string Normalize(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Length == 0)
            return Included;

        if (string.Equals(normalized, None, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "NoVat", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "VatNone", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "VatExempt", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "NoTax", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "부가세없음", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized.Replace(" ", string.Empty), "부가세없음", StringComparison.OrdinalIgnoreCase))
        {
            return None;
        }

        return Included;
    }

    public static bool IsNone(string? value)
        => string.Equals(Normalize(value), None, StringComparison.Ordinal);

    public static (decimal SupplyAmount, decimal VatAmount, decimal TotalAmount) CalculateTotals(
        IEnumerable<decimal> lineAmounts,
        string? vatMode)
    {
        var totalAmount = lineAmounts.Sum();
        if (IsNone(vatMode))
            return (totalAmount, 0m, totalAmount);

        var supplyAmount = Math.Round(totalAmount / 1.1m, 0, MidpointRounding.AwayFromZero);
        var vatAmount = totalAmount - supplyAmount;
        return (supplyAmount, vatAmount, totalAmount);
    }

    public static (decimal SupplyAmount, decimal VatAmount) SplitLineAmount(decimal lineAmount, string? vatMode)
    {
        if (IsNone(vatMode))
            return (lineAmount, 0m);

        var supplyAmount = Math.Round(lineAmount / 1.1m, 0, MidpointRounding.AwayFromZero);
        return (supplyAmount, lineAmount - supplyAmount);
    }
}
