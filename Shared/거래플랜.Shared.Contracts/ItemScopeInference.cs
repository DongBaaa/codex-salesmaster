namespace 거래플랜.Shared.Contracts;

public sealed record ItemScopeInferenceResult(
    string DesiredOfficeCode,
    string DesiredTenantCode,
    bool CanAutoResolveTenant,
    IReadOnlyList<string> RentalOfficeCodes,
    IReadOnlyList<string> WarehouseOfficeCodes,
    IReadOnlyList<string> InvoiceOfficeCodes,
    IReadOnlyList<string> EvidenceTenantCodes)
{
    public bool HasEvidence =>
        RentalOfficeCodes.Count > 0 ||
        WarehouseOfficeCodes.Count > 0 ||
        InvoiceOfficeCodes.Count > 0;

    public bool HasCrossTenantEvidence => EvidenceTenantCodes.Count > 1;
}

public static class ItemScopeInference
{
    public static ItemScopeInferenceResult Analyze(
        string? currentOfficeCode,
        string? currentTenantCode,
        IEnumerable<string?>? rentalOfficeCodes = null,
        IEnumerable<string?>? warehouseOfficeCodes = null,
        IEnumerable<string?>? invoiceOfficeCodes = null)
    {
        var normalizedRentalOfficeCodes = NormalizeCanonicalOfficeCodes(rentalOfficeCodes);
        var normalizedWarehouseOfficeCodes = NormalizeCanonicalOfficeCodes(warehouseOfficeCodes);
        var normalizedInvoiceOfficeCodes = NormalizeCanonicalOfficeCodes(invoiceOfficeCodes);

        var desiredOfficeCode = ResolveDesiredOfficeCode(
            currentOfficeCode,
            normalizedRentalOfficeCodes,
            normalizedWarehouseOfficeCodes,
            normalizedInvoiceOfficeCodes);

        var evidenceTenantCodes = normalizedRentalOfficeCodes
            .Concat(normalizedWarehouseOfficeCodes)
            .Concat(normalizedInvoiceOfficeCodes)
            .Select(TenantScopeCatalog.GetTenantCodeForOffice)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(GetTenantSortOrder)
            .ThenBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (OfficeCodeCatalog.TryNormalizeOfficeCode(desiredOfficeCode, out var canonicalOfficeCode))
        {
            return new ItemScopeInferenceResult(
                canonicalOfficeCode,
                TenantScopeCatalog.GetTenantCodeForOffice(canonicalOfficeCode),
                true,
                normalizedRentalOfficeCodes,
                normalizedWarehouseOfficeCodes,
                normalizedInvoiceOfficeCodes,
                evidenceTenantCodes);
        }

        if (evidenceTenantCodes.Count == 1)
        {
            return new ItemScopeInferenceResult(
                OfficeCodeCatalog.Shared,
                evidenceTenantCodes[0],
                true,
                normalizedRentalOfficeCodes,
                normalizedWarehouseOfficeCodes,
                normalizedInvoiceOfficeCodes,
                evidenceTenantCodes);
        }

        return new ItemScopeInferenceResult(
            desiredOfficeCode,
            TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
                currentTenantCode,
                desiredOfficeCode,
                currentTenantCode),
            false,
            normalizedRentalOfficeCodes,
            normalizedWarehouseOfficeCodes,
            normalizedInvoiceOfficeCodes,
            evidenceTenantCodes);
    }

    private static List<string> NormalizeCanonicalOfficeCodes(IEnumerable<string?>? officeCodes)
    {
        if (officeCodes is null)
            return [];

        return officeCodes
            .Select(officeCode => OfficeCodeCatalog.TryNormalizeOfficeCode(officeCode, out var normalizedOfficeCode)
                ? normalizedOfficeCode
                : string.Empty)
            .Where(officeCode => !string.IsNullOrWhiteSpace(officeCode))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(GetOfficeSortOrder)
            .ThenBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string ResolveDesiredOfficeCode(
        string? currentOfficeCode,
        IReadOnlyList<string> rentalOfficeCodes,
        IReadOnlyList<string> warehouseOfficeCodes,
        IReadOnlyList<string> invoiceOfficeCodes)
    {
        var evidenceOfficeCodes = rentalOfficeCodes
            .Concat(warehouseOfficeCodes)
            .Concat(invoiceOfficeCodes)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(GetOfficeSortOrder)
            .ThenBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (evidenceOfficeCodes.Count > 0)
            return evidenceOfficeCodes.Count == 1 ? evidenceOfficeCodes[0] : OfficeCodeCatalog.Shared;

        return OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(currentOfficeCode, OfficeCodeCatalog.Shared);
    }

    private static int GetOfficeSortOrder(string officeCode)
        => officeCode.ToUpperInvariant() switch
        {
            OfficeCodeCatalog.Usenet => 0,
            OfficeCodeCatalog.Itworld => 1,
            OfficeCodeCatalog.Yeonsu => 2,
            _ => 99
        };

    private static int GetTenantSortOrder(string tenantCode)
        => tenantCode.ToUpperInvariant() switch
        {
            TenantScopeCatalog.UsenetGroup => 0,
            TenantScopeCatalog.Itworld => 1,
            _ => 99
        };
}
