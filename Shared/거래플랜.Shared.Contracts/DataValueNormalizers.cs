namespace 거래플랜.Shared.Contracts;

public static class UnitCatalogNormalizer
{
    public const string DefaultEach = "EA";
    public const string DefaultSet = "SET";
    public const string DefaultMachine = "대";
    public const string DefaultPiece = "개";
    public const string DefaultBox = "박스";

    public static readonly string[] CanonicalDefaults =
    [
        DefaultEach,
        DefaultSet,
        DefaultMachine,
        DefaultPiece,
        DefaultBox
    ];

    public static string Normalize(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return string.Empty;

        return trimmed.ToUpperInvariant() switch
        {
            "EA" => DefaultEach,
            "SET" => DefaultSet,
            "대" => DefaultMachine,
            "개" => DefaultPiece,
            "박스" => DefaultBox,
            _ => trimmed
        };
    }
}

public static class InventoryTransferStatusNormalizer
{
    public const string Pending = "수령대기";
    public const string Received = "수령확정";
    public const string Rejected = "반려";

    public static string Normalize(
        string? status,
        string? receivedByUsername = null,
        DateTime? receivedAtUtc = null,
        string? rejectedByUsername = null,
        DateTime? rejectedAtUtc = null)
    {
        var trimmed = (status ?? string.Empty).Trim();
        if (string.Equals(trimmed, Pending, StringComparison.Ordinal))
            return Pending;
        if (string.Equals(trimmed, Received, StringComparison.Ordinal))
            return Received;
        if (string.Equals(trimmed, Rejected, StringComparison.Ordinal))
            return Rejected;

        if (!string.IsNullOrWhiteSpace(rejectedByUsername) || rejectedAtUtc.HasValue)
            return Rejected;
        if (!string.IsNullOrWhiteSpace(receivedByUsername) || receivedAtUtc.HasValue)
            return Received;

        return Pending;
    }
}
