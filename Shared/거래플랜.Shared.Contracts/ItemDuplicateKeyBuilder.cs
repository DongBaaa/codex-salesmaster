using System.Text.RegularExpressions;

namespace 거래플랜.Shared.Contracts;

public static class ItemDuplicateKeyBuilder
{
    private static readonly Regex ComparableWhitespaceRegex = new(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string BuildScopedItemNameMatchKey(
        string? tenantCode,
        string? officeCode,
        string? normalizedNameKey,
        string? rawName)
    {
        var resolvedNameKey = NormalizeLooseValue(normalizedNameKey, rawName);
        if (string.IsNullOrWhiteSpace(resolvedNameKey))
            return string.Empty;

        return string.Join('|',
            NormalizeScopeValue(tenantCode),
            NormalizeScopeValue(officeCode),
            resolvedNameKey);
    }

    public static string BuildScopedItemDescriptorConflictKey(
        string? tenantCode,
        string? officeCode,
        string? normalizedNameKey,
        string? rawName,
        string? normalizedSpecificationKey,
        string? rawSpecification,
        string? categoryName,
        string? itemKind,
        string? trackingType,
        bool isRental)
    {
        var descriptorKey = BuildItemDescriptorConflictKey(
            normalizedNameKey,
            rawName,
            normalizedSpecificationKey,
            rawSpecification,
            categoryName,
            itemKind,
            trackingType,
            isRental);

        if (string.IsNullOrWhiteSpace(descriptorKey))
            return string.Empty;

        return string.Join('|',
            NormalizeScopeValue(tenantCode),
            NormalizeScopeValue(officeCode),
            descriptorKey);
    }

    public static string BuildItemDescriptorConflictKey(
        string? normalizedNameKey,
        string? rawName,
        string? normalizedSpecificationKey,
        string? rawSpecification,
        string? categoryName,
        string? itemKind,
        string? trackingType,
        bool isRental)
    {
        var resolvedNameKey = NormalizeConflictDisplayValue(rawName, normalizedNameKey);
        if (string.IsNullOrWhiteSpace(resolvedNameKey))
            return string.Empty;

        var normalizedTrackingType = ItemOperationalPolicy.NormalizeTrackingType(
            trackingType,
            itemKind,
            categoryName,
            isRental);
        var normalizedItemKind = ItemOperationalPolicy.NormalizeItemKind(
            itemKind,
            trackingType,
            categoryName,
            isRental);

        return string.Join('|', new[]
        {
            resolvedNameKey,
            NormalizeConflictDisplayValue(rawSpecification, normalizedSpecificationKey),
            RentalCatalogValueNormalizer.NormalizeLooseKey(categoryName),
            normalizedItemKind.Trim().ToUpperInvariant(),
            normalizedTrackingType.Trim().ToUpperInvariant()
        });
    }

    private static string NormalizeLooseValue(string? normalizedValue, string? fallbackValue)
        => RentalCatalogValueNormalizer.NormalizeLooseKey(
            string.IsNullOrWhiteSpace(normalizedValue)
                ? fallbackValue
                : normalizedValue);

    private static string NormalizeConflictDisplayValue(string? rawValue, string? fallbackValue)
    {
        if (!string.IsNullOrWhiteSpace(rawValue))
            return NormalizeComparableDisplayValue(rawValue);

        return NormalizeLooseValue(fallbackValue, null);
    }

    private static string NormalizeComparableDisplayValue(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return string.Empty;

        var collapsedWhitespace = ComparableWhitespaceRegex.Replace(trimmed, " ");
        return collapsedWhitespace.ToUpperInvariant();
    }

    private static string NormalizeScopeValue(string? value)
        => (value ?? string.Empty).Trim().ToUpperInvariant();
}
