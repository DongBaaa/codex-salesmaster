namespace 거래플랜.Shared.Contracts;

public readonly record struct RentalOperationalScope(
    string ResponsibleOfficeCode,
    string OwnerOfficeCode,
    string TenantCode);

public static class RentalScopeNormalizer
{
    public static RentalOperationalScope ResolveScope(
        string? tenantCode,
        string? officeCode,
        string? managementCompanyCode = null,
        string? responsibleOfficeCode = null,
        string? fallbackOfficeCode = null)
    {
        if (IsItworldScope(tenantCode, officeCode, managementCompanyCode, responsibleOfficeCode))
        {
            return new RentalOperationalScope(
                OfficeCodeCatalog.Itworld,
                OfficeCodeCatalog.Itworld,
                TenantScopeCatalog.Itworld);
        }

        if (IsYeonsuScope(officeCode, managementCompanyCode, responsibleOfficeCode))
        {
            return new RentalOperationalScope(
                OfficeCodeCatalog.Yeonsu,
                OfficeCodeCatalog.Usenet,
                TenantScopeCatalog.UsenetGroup);
        }

        var normalizedResponsibleOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeLoose(
            responsibleOfficeCode,
            officeCode,
            string.IsNullOrWhiteSpace(managementCompanyCode) ? fallbackOfficeCode : managementCompanyCode);
        var normalizedOwnerOfficeCode = OfficeCodeCatalog.ResolveOwningOfficeCode(
            officeCode,
            normalizedResponsibleOfficeCode,
            string.IsNullOrWhiteSpace(managementCompanyCode) ? fallbackOfficeCode : managementCompanyCode);
        var normalizedTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            tenantCode,
            normalizedOwnerOfficeCode,
            tenantCode,
            normalizedResponsibleOfficeCode);

        if (string.Equals(normalizedResponsibleOfficeCode, OfficeCodeCatalog.Itworld, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedOwnerOfficeCode, OfficeCodeCatalog.Itworld, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedTenantCode, TenantScopeCatalog.Itworld, StringComparison.OrdinalIgnoreCase))
        {
            return new RentalOperationalScope(
                OfficeCodeCatalog.Itworld,
                OfficeCodeCatalog.Itworld,
                TenantScopeCatalog.Itworld);
        }

        if (string.Equals(normalizedResponsibleOfficeCode, OfficeCodeCatalog.Yeonsu, StringComparison.OrdinalIgnoreCase))
        {
            return new RentalOperationalScope(
                OfficeCodeCatalog.Yeonsu,
                OfficeCodeCatalog.Usenet,
                TenantScopeCatalog.UsenetGroup);
        }

        return new RentalOperationalScope(
            normalizedResponsibleOfficeCode,
            normalizedOwnerOfficeCode,
            normalizedTenantCode);
    }

    public static string ResolveResponsibleOfficeCode(
        string? tenantCode,
        string? officeCode,
        string? managementCompanyCode = null,
        string? responsibleOfficeCode = null,
        string? fallbackOfficeCode = null)
        => ResolveScope(tenantCode, officeCode, managementCompanyCode, responsibleOfficeCode, fallbackOfficeCode).ResponsibleOfficeCode;

    public static string ResolveOwnerOfficeCode(
        string? tenantCode,
        string? officeCode,
        string? managementCompanyCode = null,
        string? responsibleOfficeCode = null,
        string? fallbackOfficeCode = null)
        => ResolveScope(tenantCode, officeCode, managementCompanyCode, responsibleOfficeCode, fallbackOfficeCode).OwnerOfficeCode;

    public static string ResolveTenantCode(
        string? tenantCode,
        string? officeCode,
        string? managementCompanyCode = null,
        string? responsibleOfficeCode = null,
        string? fallbackOfficeCode = null)
        => ResolveScope(tenantCode, officeCode, managementCompanyCode, responsibleOfficeCode, fallbackOfficeCode).TenantCode;

    private static bool IsItworldScope(
        string? tenantCode,
        string? officeCode,
        string? managementCompanyCode,
        string? responsibleOfficeCode)
        => MatchesTenant(tenantCode, TenantScopeCatalog.Itworld) ||
           MatchesOffice(officeCode, OfficeCodeCatalog.Itworld) ||
           MatchesOffice(managementCompanyCode, OfficeCodeCatalog.Itworld) ||
           MatchesOffice(responsibleOfficeCode, OfficeCodeCatalog.Itworld);

    private static bool IsYeonsuScope(
        string? officeCode,
        string? managementCompanyCode,
        string? responsibleOfficeCode)
        => MatchesOffice(responsibleOfficeCode, OfficeCodeCatalog.Yeonsu) ||
           MatchesOffice(officeCode, OfficeCodeCatalog.Yeonsu) ||
           MatchesOffice(managementCompanyCode, OfficeCodeCatalog.Yeonsu);

    private static bool MatchesTenant(string? candidate, string expectedTenantCode)
        => TenantScopeCatalog.TryNormalizeTenantCode(candidate, out var normalizedTenantCode) &&
           string.Equals(normalizedTenantCode, expectedTenantCode, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesOffice(string? candidate, string expectedOfficeCode)
        => OfficeCodeCatalog.TryNormalizeOfficeCode(candidate, out var normalizedOfficeCode) &&
           string.Equals(normalizedOfficeCode, expectedOfficeCode, StringComparison.OrdinalIgnoreCase);
}
