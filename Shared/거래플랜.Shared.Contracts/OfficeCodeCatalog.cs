namespace 거래플랜.Shared.Contracts;

public static class OfficeCodeCatalog
{
    public static readonly Guid UsenetDefaultCompanyProfileId = Guid.Parse("b76377a1-6386-469e-9fd4-6c4bea0d9a00");
    public static readonly Guid ItworldDefaultCompanyProfileId = Guid.Parse("f717ca61-ce49-4cee-b07f-bb2b44be82a5");
    public static readonly Guid YeonsuDefaultCompanyProfileId = Guid.Parse("ecae0836-37ef-4f27-a35c-77ae46ed9d96");

    public const string Shared = "ALL";
    public const string Usenet = "USENET";
    public const string Itworld = "ITWORLD";
    public const string Yeonsu = "YEONSU";
    public const string UsenetMainWarehouse = "USENET_MAIN";
    public const string ItworldMainWarehouse = "ITWORLD_MAIN";
    public const string YeonsuMainWarehouse = "YEONSU_MAIN";

    public static IReadOnlyList<string> All { get; } =
    [
        Usenet,
        Itworld,
        Yeonsu
    ];

    public static IReadOnlyList<string> AllScopes { get; } =
    [
        Shared,
        Usenet,
        Itworld,
        Yeonsu
    ];

    public static bool IsCanonical(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToUpperInvariant();
        return normalized is Usenet or Itworld or Yeonsu;
    }

    public static bool IsCanonicalOfficeCode(string? value)
        => IsCanonical(value);

    public static bool IsSharedOfficeCode(string? value)
        => string.Equals((value ?? string.Empty).Trim(), Shared, StringComparison.OrdinalIgnoreCase) ||
           string.Equals((value ?? string.Empty).Trim(), "공용", StringComparison.OrdinalIgnoreCase) ||
           string.Equals((value ?? string.Empty).Trim(), "전체", StringComparison.OrdinalIgnoreCase) ||
           string.Equals((value ?? string.Empty).Trim(), "shared", StringComparison.OrdinalIgnoreCase);

    public static bool TryNormalize(string? value, out string canonical)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            canonical = string.Empty;
            return false;
        }

        var upper = trimmed.ToUpperInvariant();
        switch (upper)
        {
            case Usenet:
            case "UZNET":
                canonical = Usenet;
                return true;
            case Itworld:
                canonical = Itworld;
                return true;
            case Yeonsu:
                canonical = Yeonsu;
                return true;
        }

        if (string.Equals(trimmed, "유즈넷", StringComparison.OrdinalIgnoreCase))
        {
            canonical = Usenet;
            return true;
        }

        if (string.Equals(trimmed, "아이티월드", StringComparison.OrdinalIgnoreCase))
        {
            canonical = Itworld;
            return true;
        }

        if (string.Equals(trimmed, "연수구", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "연수구 사무실", StringComparison.OrdinalIgnoreCase))
        {
            canonical = Yeonsu;
            return true;
        }

        canonical = string.Empty;
        return false;
    }

    public static bool TryNormalizeScope(string? value, out string canonical)
    {
        if (IsSharedOfficeCode(value))
        {
            canonical = Shared;
            return true;
        }

        return TryNormalize(value, out canonical);
    }

    public static bool TryNormalizeOfficeCode(string? value, out string canonical)
        => TryNormalize(value, out canonical);

    public static string NormalizeOrDefault(string? value, string? fallback = null)
    {
        if (TryNormalize(value, out var canonical))
            return canonical;

        if (TryNormalize(fallback, out canonical))
            return canonical;

        return Usenet;
    }

    public static string NormalizeOfficeCodeOrDefault(string? value, string? fallback = null)
        => NormalizeOrDefault(value, fallback);

    public static string NormalizeOfficeScopeOrDefault(string? value, string? fallback = null)
    {
        if (TryNormalizeScope(value, out var canonical))
            return canonical;

        if (TryNormalizeScope(fallback, out canonical))
            return canonical;

        return Shared;
    }

    public static string ResolveOwningOfficeCode(
        string? ownerOfficeCode,
        string? responsibleOfficeCode = null,
        string? fallbackOwnerOfficeCode = null)
    {
        static string ConvertChildOfficeToOwner(string officeCode)
            => string.Equals(officeCode, Yeonsu, StringComparison.OrdinalIgnoreCase)
                ? Usenet
                : officeCode;

        if (TryNormalizeScope(ownerOfficeCode, out var normalizedOwner))
        {
            if (string.Equals(normalizedOwner, Shared, StringComparison.OrdinalIgnoreCase))
                return Shared;

            return ConvertChildOfficeToOwner(normalizedOwner);
        }

        if (TryNormalize(responsibleOfficeCode, out var normalizedResponsible))
            return ConvertChildOfficeToOwner(normalizedResponsible);

        if (TryNormalizeScope(fallbackOwnerOfficeCode, out normalizedOwner))
        {
            if (string.Equals(normalizedOwner, Shared, StringComparison.OrdinalIgnoreCase))
                return Shared;

            return ConvertChildOfficeToOwner(normalizedOwner);
        }

        return Usenet;
    }

    public static string NormalizeLoose(string? primary, string? secondary = null, string? fallback = null)
    {
        foreach (var candidate in new[] { primary, secondary, fallback })
        {
            if (TryNormalize(candidate, out var canonical))
                return canonical;

            var trimmed = (candidate ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                continue;

            if (trimmed.Contains("유즈넷", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("UZNET", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("USENET", StringComparison.OrdinalIgnoreCase))
                return Usenet;

            if (trimmed.Contains("아이티월드", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("ITWORLD", StringComparison.OrdinalIgnoreCase))
                return Itworld;

            if (trimmed.Contains("연수", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("YEONSU", StringComparison.OrdinalIgnoreCase))
                return Yeonsu;
        }

        return NormalizeOrDefault(fallback, Usenet);
    }

    public static string NormalizeOfficeCodeLoose(string? primary, string? secondary = null, string? fallback = null)
        => NormalizeLoose(primary, secondary, fallback);

    public static string GetOfficeDisplayName(string? officeCode)
        => NormalizeOrDefault(officeCode, Usenet) switch
        {
            Itworld => Itworld,
            Yeonsu => Yeonsu,
            _ => Usenet
        };

    public static Guid GetDefaultCompanyProfileId(string? officeCode)
        => NormalizeOrDefault(officeCode, Usenet) switch
        {
            Itworld => ItworldDefaultCompanyProfileId,
            Yeonsu => YeonsuDefaultCompanyProfileId,
            _ => UsenetDefaultCompanyProfileId
        };

    public static string GetMainWarehouseCode(string? officeCode)
        => NormalizeOrDefault(officeCode, Usenet) switch
        {
            Itworld => ItworldMainWarehouse,
            Yeonsu => YeonsuMainWarehouse,
            _ => UsenetMainWarehouse
        };

    public static string NormalizeWarehouseCodeOrDefault(string? warehouseCode, string? officeCode, string? fallbackOfficeCode = null)
    {
        var normalized = (warehouseCode ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
            return GetMainWarehouseCode(NormalizeOrDefault(officeCode, fallbackOfficeCode));

        return NormalizeWarehouseCodeLoose(normalized, officeCode, fallbackOfficeCode);
    }

    public static string NormalizeWarehouseCodeLoose(string? warehouseCode, string? officeCode = null, string? fallbackOfficeCode = null)
    {
        var normalized = (warehouseCode ?? string.Empty).Trim().ToUpperInvariant();
        var office = NormalizeLoose(officeCode, warehouseCode, fallbackOfficeCode);
        if (string.IsNullOrWhiteSpace(normalized))
            return GetMainWarehouseCode(office);

        if (normalized is UsenetMainWarehouse or ItworldMainWarehouse or YeonsuMainWarehouse)
            return normalized;

        if (normalized.Contains("UZNET", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("USENET", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("유즈넷", StringComparison.OrdinalIgnoreCase))
            return UsenetMainWarehouse;

        if (normalized.Contains("ITWORLD", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("아이티월드", StringComparison.OrdinalIgnoreCase))
            return ItworldMainWarehouse;

        if (normalized.Contains("YEONSU", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("연수", StringComparison.OrdinalIgnoreCase))
            return YeonsuMainWarehouse;

        return GetMainWarehouseCode(office);
    }
}
