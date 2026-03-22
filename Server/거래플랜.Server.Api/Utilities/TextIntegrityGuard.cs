namespace 거래플랜.Server.Api.Utilities;

public static class TextIntegrityGuard
{
    public static bool LooksLossy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var meaningfulCount = 0;
        var lossyCount = 0;

        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch))
                continue;

            meaningfulCount++;
            if (ch is '?' or '\uFFFD')
                lossyCount++;
        }

        if (meaningfulCount == 0 || lossyCount == 0)
            return false;

        if (value.Contains("(?)", StringComparison.Ordinal))
            return true;

        return lossyCount >= 2 && (double)lossyCount / meaningfulCount >= 0.25d;
    }

    public static string PreferExistingIfIncomingLooksLossy(string? existing, string? incoming)
    {
        if (string.IsNullOrWhiteSpace(incoming))
            return incoming ?? string.Empty;

        if (LooksLossy(incoming) && !LooksLossy(existing) && !string.IsNullOrWhiteSpace(existing))
            return existing;

        return incoming;
    }
}
