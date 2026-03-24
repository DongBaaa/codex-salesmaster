using System.Text.RegularExpressions;

namespace 거래플랜.Shared.Contracts;

public static class RentalCatalogValueNormalizer
{
    private static readonly Regex CollapseWhitespaceRegex = new("\\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RemoveBracketCharsRegex = new(@"[\(\)\[\]\{\}]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RemoveSpecialCharsRegex = new(@"[^\w가-힣]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string NormalizeDisplayText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return CollapseWhitespaceRegex.Replace(value.Trim(), " ");
    }

    public static string NormalizeCategoryDisplayName(string? value)
        => NormalizeDisplayText(value);

    public static string NormalizeItemNameDisplayName(string? value)
        => NormalizeDisplayText(value);

    public static string NormalizeLooseKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var compact = RemoveBracketCharsRegex.Replace(value.Trim(), string.Empty);
        compact = RemoveSpecialCharsRegex.Replace(compact, string.Empty);
        compact = compact.Replace(" ", string.Empty, StringComparison.Ordinal);
        return compact.ToUpperInvariant();
    }
}
