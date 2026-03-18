using System.Text.RegularExpressions;

namespace 거래플랜.Server.Api.Utilities;

public static partial class MatchKeyNormalizer
{
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var compact = RemoveBracketCharsRegex().Replace(value.Trim(), string.Empty);
        compact = RemoveSpecialCharsRegex().Replace(compact, string.Empty);
        compact = compact.Replace(" ", string.Empty, StringComparison.Ordinal);
        return compact.ToUpperInvariant();
    }

    [GeneratedRegex(@"[\(\)\[\]\{\}]")]
    private static partial Regex RemoveBracketCharsRegex();

    [GeneratedRegex(@"[^\w가-힣]")]
    private static partial Regex RemoveSpecialCharsRegex();
}
