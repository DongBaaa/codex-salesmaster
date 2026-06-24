using System.Security.Cryptography;

namespace 거래플랜.Server.Api.Utilities;

public static class FileContentIntegrityVerifier
{
    public static bool HasExpectedIntegrity(byte[]? content, long expectedSize, string? expectedHash)
    {
        content ??= [];

        if (expectedSize > 0 && content.LongLength != expectedSize)
            return false;

        if (!IsSha256Hex(expectedHash))
            return expectedSize <= 0 || content.LongLength == expectedSize;

        var actualHash = Convert.ToHexString(SHA256.HashData(content));
        return string.Equals(actualHash, expectedHash!.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static byte[] SelectVerifiedOrEmpty(byte[]? preferredContent, byte[]? fallbackContent, long expectedSize, string? expectedHash)
    {
        if (HasExpectedIntegrity(preferredContent, expectedSize, expectedHash))
            return preferredContent ?? [];

        if (HasExpectedIntegrity(fallbackContent, expectedSize, expectedHash))
            return fallbackContent ?? [];

        return [];
    }

    private static bool IsSha256Hex(string? value)
    {
        var trimmed = value?.Trim();
        return trimmed is { Length: 64 } && trimmed.All(Uri.IsHexDigit);
    }
}
