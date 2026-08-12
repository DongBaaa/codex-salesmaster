using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class JsonGuidTokenSafetyTests
{
    private static readonly Guid Target =
        Guid.Parse("91a10000-0000-0000-0000-000000000003");

    [Theory]
    [InlineData("D")]
    [InlineData("N")]
    [InlineData("B")]
    [InlineData("P")]
    [InlineData("X")]
    public void ContainsExactGuidToken_DetectsCanonicalAndUnicodeEscapedFormats(
        string format)
    {
        var canonical = Target.ToString(format);
        var encoded = EncodeAsJsonUnicodeEscapes(Target.ToString(format));
        var rawMalformed = $"[{{\"CatalogItemId\":\"{canonical}\",\"Broken\":]";
        var malformed = $"[{{\"CatalogItemId\":\"{encoded}\",\"Broken\":]";

        Assert.True(JsonGuidTokenSafety.ContainsExactGuidToken(rawMalformed, Target));
        Assert.True(JsonGuidTokenSafety.ContainsExactGuidToken(malformed, Target));
        Assert.True(JsonGuidTokenSafety.ContainsExactGuidToken(
            malformed,
            new[] { Guid.NewGuid(), Target }));
    }

    [Fact]
    public void ContainsExactGuidToken_DoesNotDecodeEscapedBackslashUnicodeLiteral()
    {
        var encoded = EncodeAsJsonUnicodeEscapes(Target.ToString("D"));
        var escapedBackslashLiteral = encoded.Replace(
            "\\u",
            "\\\\u",
            StringComparison.Ordinal);

        Assert.False(JsonGuidTokenSafety.ContainsExactGuidToken(
            escapedBackslashLiteral,
            Target));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ContainsExactGuidToken_DetectsGuidTryParseCompatibleWhitespaceX(
        bool unicodeEscaped)
    {
        var whitespaceX = CreateWhitespaceX();
        var token = unicodeEscaped
            ? EncodeAsJsonUnicodeEscapes(whitespaceX)
            : whitespaceX;
        var unsupported = $"[{{\"CatalogItemId\":\"{token}\",\"Broken\":]";

        Assert.True(JsonGuidTokenSafety.ContainsExactGuidToken(unsupported, Target));
    }

    [Fact]
    public void ContainsExactGuidToken_DoesNotDecodeDoubleEscapedWhitespaceXLiteral()
    {
        var encoded = EncodeAsJsonUnicodeEscapes(CreateWhitespaceX());
        var doubleEscapedLiteral = encoded.Replace(
            "\\u",
            "\\\\u",
            StringComparison.Ordinal);

        Assert.False(JsonGuidTokenSafety.ContainsExactGuidToken(
            doubleEscapedLiteral,
            Target));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ContainsExactGuidToken_RejectsExtendedWhitespaceXToken(
        bool unicodeEscaped)
    {
        var whitespaceX = CreateWhitespaceX();
        var token = unicodeEscaped
            ? EncodeAsJsonUnicodeEscapes(whitespaceX)
            : whitespaceX;

        Assert.False(JsonGuidTokenSafety.ContainsExactGuidToken(
            $"f{token}a",
            Target));
    }

    [Theory]
    [InlineData("prefixed-d")]
    [InlineData("extended-n")]
    public void ContainsExactGuidToken_RequiresExactTokenBoundaries(string shape)
    {
        var value = shape switch
        {
            "prefixed-d" => $"archive-{{{Target:D}}}-old",
            "extended-n" => $"f{Target:N}a",
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };

        Assert.False(JsonGuidTokenSafety.ContainsExactGuidToken(value, Target));
    }

    private static string EncodeAsJsonUnicodeEscapes(string value)
        => string.Concat(value.Select(character => $"\\u{(int)character:x4}"));

    private static string CreateWhitespaceX()
        => Target.ToString("X")
            .Replace("{", "{ ", StringComparison.Ordinal)
            .Replace("}", " }", StringComparison.Ordinal)
            .Replace(",", " , ", StringComparison.Ordinal);
}
