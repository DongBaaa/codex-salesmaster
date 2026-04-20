namespace 거래플랜.Shared.Contracts;

public sealed record DefaultUnitDefinition(Guid Id, string Name);

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

    public static IReadOnlyList<DefaultUnitDefinition> CanonicalDefinitions { get; } =
    [
        new(Guid.Parse("920747d5-a3f3-4a17-9981-38c43944ef25"), DefaultEach),
        new(Guid.Parse("d1dd3e3e-33b8-4fde-b55d-2730bded73cd"), DefaultSet),
        new(Guid.Parse("2afae70b-4f6a-491f-979e-2e64cf760043"), DefaultMachine),
        new(Guid.Parse("4b40cf84-ac7b-4292-b2dd-b1106f7a7857"), DefaultPiece),
        new(Guid.Parse("db9f045f-e897-4617-b8fe-93c6505f84e6"), DefaultBox)
    ];

    public static bool TryGetCanonicalDefinition(string? value, out DefaultUnitDefinition definition)
    {
        var normalized = Normalize(value);
        definition = CanonicalDefinitions.FirstOrDefault(current =>
            string.Equals(current.Name, normalized, StringComparison.Ordinal))
            ?? default!;

        return definition is not null;
    }

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
