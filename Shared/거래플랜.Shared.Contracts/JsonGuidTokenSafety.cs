using System.Globalization;
using System.Text;

namespace 거래플랜.Shared.Contracts;

/// <summary>
/// Conservatively detects exact GUID tokens when structured JSON parsing has
/// already determined that a payload is malformed or unsupported. The result
/// is a fail-closed guard only and must never be used as a source for rewriting
/// the original payload.
/// </summary>
public static class JsonGuidTokenSafety
{
    public static bool ContainsExactGuidToken(string? value, Guid target)
        => target != Guid.Empty &&
           ContainsExactGuidToken(value, [target]);

    public static bool ContainsExactGuidToken(
        string? value,
        IEnumerable<Guid>? targets)
    {
        if (string.IsNullOrEmpty(value) || targets is null)
            return false;

        var decodedValue = DecodeJsonUnicodeEscapes(value);
        foreach (var target in targets)
        {
            if (target == Guid.Empty)
                continue;

            if (ContainsExactGuidToken(decodedValue, target, "D") ||
                ContainsExactGuidToken(decodedValue, target, "N") ||
                ContainsExactGuidToken(decodedValue, target, "B") ||
                ContainsExactGuidToken(decodedValue, target, "P") ||
                ContainsExactGuidToken(decodedValue, target, "X") ||
                ContainsWhitespaceTolerantXGuidToken(decodedValue, target))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsExactGuidToken(
        string value,
        Guid target,
        string format)
    {
        var candidate = target.ToString(format);
        var searchIndex = 0;
        while (searchIndex < value.Length)
        {
            var candidateIndex = value.IndexOf(
                candidate,
                searchIndex,
                StringComparison.OrdinalIgnoreCase);
            if (candidateIndex < 0)
                return false;

            var candidateEnd = candidateIndex + candidate.Length;
            var hasValidStartBoundary = candidateIndex == 0 ||
                !IsGuidTokenContinuation(value[candidateIndex - 1]);
            var hasValidEndBoundary = candidateEnd == value.Length ||
                !IsGuidTokenContinuation(value[candidateEnd]);
            if (hasValidStartBoundary && hasValidEndBoundary)
                return true;

            searchIndex = candidateIndex + 1;
        }

        return false;
    }

    private static bool ContainsWhitespaceTolerantXGuidToken(
        string value,
        Guid target)
    {
        var candidate = target.ToString("X");
        var searchIndex = 0;
        while (searchIndex < value.Length)
        {
            var candidateIndex = value.IndexOf('{', searchIndex);
            if (candidateIndex < 0)
                return false;

            var hasValidStartBoundary = candidateIndex == 0 ||
                !IsGuidTokenContinuation(value[candidateIndex - 1]);
            var valueIndex = candidateIndex;
            var candidateOffset = 0;
            while (hasValidStartBoundary &&
                   valueIndex < value.Length &&
                   candidateOffset < candidate.Length)
            {
                if (CharsEqualIgnoreCase(value[valueIndex], candidate[candidateOffset]))
                {
                    valueIndex++;
                    candidateOffset++;
                    continue;
                }

                if (char.IsWhiteSpace(value[valueIndex]) &&
                    IsWhitespaceAllowedBeforeXCharacter(candidate, candidateOffset))
                {
                    valueIndex++;
                    continue;
                }

                break;
            }

            if (candidateOffset == candidate.Length)
            {
                var hasValidEndBoundary = valueIndex == value.Length ||
                    !IsGuidTokenContinuation(value[valueIndex]);
                if (hasValidEndBoundary)
                    return true;
            }

            searchIndex = candidateIndex + 1;
        }

        return false;
    }

    private static bool IsWhitespaceAllowedBeforeXCharacter(
        string candidate,
        int candidateOffset)
    {
        if (candidateOffset >= candidate.Length)
            return false;

        return IsXStructuralCharacter(candidate[candidateOffset]) ||
               candidateOffset > 0 &&
               IsXStructuralCharacter(candidate[candidateOffset - 1]);
    }

    private static bool IsXStructuralCharacter(char value)
        => value is '{' or '}' or ',';

    private static bool CharsEqualIgnoreCase(char left, char right)
        => left == right ||
           char.ToUpperInvariant(left) == char.ToUpperInvariant(right);

    private static bool IsGuidTokenContinuation(char value)
        => char.IsLetterOrDigit(value) ||
           value is '-' or '_' or '{' or '}' or '(' or ')';

    private static string DecodeJsonUnicodeEscapes(string value)
    {
        if (value.IndexOf("\\u", StringComparison.OrdinalIgnoreCase) < 0)
            return value;

        var decoded = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '\\' &&
                index + 5 < value.Length &&
                (value[index + 1] == 'u' || value[index + 1] == 'U') &&
                !IsEscapedCharacter(value, index) &&
                ushort.TryParse(
                    value.AsSpan(index + 2, 4),
                    NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture,
                    out var unicodeValue))
            {
                decoded.Append((char)unicodeValue);
                index += 5;
                continue;
            }

            decoded.Append(value[index]);
        }

        return decoded.ToString();
    }

    private static bool IsEscapedCharacter(string value, int index)
    {
        var precedingBackslashes = 0;
        for (var current = index - 1; current >= 0 && value[current] == '\\'; current--)
            precedingBackslashes++;
        return precedingBackslashes % 2 != 0;
    }
}
