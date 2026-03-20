namespace 거래플랜.Shared.Contracts;

public static class TenantScopeCatalog
{
    public const string UsenetGroup = "USENET_GROUP";
    public const string Itworld = "ITWORLD";

    public const string ScopeOfficeOnly = "OfficeOnly";
    public const string ScopeTenantAll = "TenantAll";
    public const string ScopeAdmin = "Admin";

    public const string StorageSharedDatabase = "SharedBusinessDatabase";
    public const string StorageDedicatedDatabase = "DedicatedBusinessDatabase";

    public static IReadOnlyList<string> AllTenants { get; } =
    [
        UsenetGroup,
        Itworld
    ];

    public static IReadOnlyList<string> AllScopeTypes { get; } =
    [
        ScopeOfficeOnly,
        ScopeTenantAll,
        ScopeAdmin
    ];

    public static IReadOnlyList<string> AllStorageModes { get; } =
    [
        StorageSharedDatabase,
        StorageDedicatedDatabase
    ];

    public static bool TryNormalizeTenantCode(string? value, out string canonical)
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
            case UsenetGroup:
            case "USENET":
            case "UZNET":
            case "YEONSU":
                canonical = UsenetGroup;
                return true;
            case Itworld:
                canonical = Itworld;
                return true;
        }

        if (trimmed.Contains("유즈넷", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("연수", StringComparison.OrdinalIgnoreCase))
        {
            canonical = UsenetGroup;
            return true;
        }

        if (trimmed.Contains("아이티월드", StringComparison.OrdinalIgnoreCase))
        {
            canonical = Itworld;
            return true;
        }

        canonical = string.Empty;
        return false;
    }

    public static string NormalizeTenantCodeOrDefault(string? value, string? fallback = null)
    {
        if (TryNormalizeTenantCode(value, out var canonical))
            return canonical;

        if (TryNormalizeTenantCode(fallback, out canonical))
            return canonical;

        return UsenetGroup;
    }

    public static bool TryNormalizeScopeType(string? value, out string canonical)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            canonical = string.Empty;
            return false;
        }

        switch (trimmed.ToUpperInvariant())
        {
            case "OFFICEONLY":
            case "OFFICE_ONLY":
            case "OFFICE":
            case "지점":
            case "지점전용":
                canonical = ScopeOfficeOnly;
                return true;
            case "TENANTALL":
            case "TENANT_ALL":
            case "TENANT":
            case "COMPANY":
            case "업체전체":
            case "업체":
                canonical = ScopeTenantAll;
                return true;
            case "ADMIN":
            case "관리자":
                canonical = ScopeAdmin;
                return true;
        }

        canonical = string.Empty;
        return false;
    }

    public static string NormalizeScopeTypeOrDefault(string? value, string? fallback = null)
    {
        if (TryNormalizeScopeType(value, out var canonical))
            return canonical;

        if (TryNormalizeScopeType(fallback, out canonical))
            return canonical;

        return ScopeOfficeOnly;
    }

    public static string GetTenantCodeForOffice(string? officeCode)
        => OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode) switch
        {
            OfficeCodeCatalog.Itworld => Itworld,
            _ => UsenetGroup
        };

    public static string NormalizeTenantCodeForOfficeOrDefault(
        string? tenantCode,
        string? officeCode,
        string? fallbackTenantCode = null,
        string? fallbackOfficeCode = null)
    {
        if (!string.IsNullOrWhiteSpace(officeCode))
            return GetTenantCodeForOffice(officeCode);

        if (!string.IsNullOrWhiteSpace(fallbackOfficeCode))
            return GetTenantCodeForOffice(fallbackOfficeCode);

        return NormalizeTenantCodeOrDefault(tenantCode, fallbackTenantCode);
    }

    public static bool TenantContainsOffice(string? tenantCode, string? officeCode)
    {
        var normalizedTenant = NormalizeTenantCodeOrDefault(tenantCode);
        var normalizedOffice = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode);
        return string.Equals(GetTenantCodeForOffice(normalizedOffice), normalizedTenant, StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<string> GetOfficeCodesForTenant(string? tenantCode)
    {
        var normalizedTenant = NormalizeTenantCodeOrDefault(tenantCode);
        return normalizedTenant switch
        {
            Itworld => [OfficeCodeCatalog.Itworld],
            _ => [OfficeCodeCatalog.Usenet, OfficeCodeCatalog.Yeonsu]
        };
    }

    public static string GetTenantDisplayName(string? tenantCode)
        => NormalizeTenantCodeOrDefault(tenantCode) switch
        {
            Itworld => "ITWORLD",
            _ => "USENET / 연수구"
        };

    public static string GetDatabaseName(string? tenantCodeOrDatabaseName)
    {
        var trimmed = (tenantCodeOrDatabaseName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return "USENET";

        if (string.Equals(trimmed, "USENET", StringComparison.OrdinalIgnoreCase))
            return "USENET";

        if (string.Equals(trimmed, Itworld, StringComparison.OrdinalIgnoreCase))
            return "ITWORLD";

        if (string.Equals(trimmed, UsenetGroup, StringComparison.OrdinalIgnoreCase))
            return "USENET";

        if (TryNormalizeTenantCode(trimmed, out var canonical))
        {
            return canonical switch
            {
                Itworld => "ITWORLD",
                _ => "USENET"
            };
        }

        return trimmed.ToUpperInvariant();
    }

    public static string GetBusinessDatabaseDisplayName(string? tenantCodeOrDatabaseName)
    {
        var normalizedDatabaseName = GetDatabaseName(tenantCodeOrDatabaseName);
        return normalizedDatabaseName switch
        {
            "ITWORLD" => "아이티월드",
            "USENET" => "유즈넷",
            _ => normalizedDatabaseName
        };
    }

    public static string FormatBusinessDatabaseLabel(string? displayName, string? tenantCodeOrDatabaseName)
    {
        var databaseName = GetDatabaseName(tenantCodeOrDatabaseName);
        var safeDisplayName = string.IsNullOrWhiteSpace(displayName)
            ? GetBusinessDatabaseDisplayName(databaseName)
            : displayName.Trim();

        return $"{safeDisplayName}({databaseName})";
    }

    public static string GetScopeDisplayName(string? scopeType)
        => NormalizeScopeTypeOrDefault(scopeType) switch
        {
            ScopeAdmin => "관리자",
            ScopeTenantAll => "업체 전체",
            _ => "지점 전용"
        };

    public static string NormalizeStorageModeOrDefault(string? storageMode, string? fallback = null)
    {
        static string? Normalize(string? value)
        {
            var trimmed = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                return null;

            return trimmed.ToUpperInvariant() switch
            {
                "SHAREDBUSINESSDATABASE" or "SHARED" or "GROUP" => StorageSharedDatabase,
                "DEDICATEDBUSINESSDATABASE" or "DEDICATED" or "ISOLATED" => StorageDedicatedDatabase,
                _ => null
            };
        }

        return Normalize(storageMode)
               ?? Normalize(fallback)
               ?? StorageSharedDatabase;
    }

    public static string GetStorageModeDisplayName(string? storageMode)
        => NormalizeStorageModeOrDefault(storageMode) switch
        {
            StorageDedicatedDatabase => "별도 업무 DB",
            _ => "공용 업무 DB"
        };
}
