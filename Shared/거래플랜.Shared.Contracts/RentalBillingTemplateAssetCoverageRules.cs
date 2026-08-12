using System.Text.Json;

namespace 거래플랜.Shared.Contracts;

public enum RentalBillingTemplateAssetCoverage
{
    NoExplicitCoverage,
    UniqueReference,
    MissingFromExplicitCoverage,
    AmbiguousReference,
    MalformedTemplate
}

public static class RentalBillingTemplateAssetCoverageRules
{
    public const string ExplicitCoverageConflictMessage =
        "선택한 청구 프로필의 명시적 자산 구성에 이 렌탈 자산이 정확히 한 번 포함되어 있지 않습니다. 청구관리에서 자산 포함 항목을 확인한 뒤 다시 저장하세요.";

    public static bool TryGetExplicitIncludedAssetIds(
        string? billingTemplateJson,
        out IReadOnlyList<Guid> assetIds,
        out bool hasDuplicateReferences)
    {
        var parsedAssetIds = new List<Guid>();
        var uniqueAssetIds = new HashSet<Guid>();
        hasDuplicateReferences = false;
        if (string.IsNullOrWhiteSpace(billingTemplateJson))
        {
            assetIds = parsedAssetIds;
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(billingTemplateJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                assetIds = Array.Empty<Guid>();
                return false;
            }

            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    assetIds = Array.Empty<Guid>();
                    return false;
                }

                JsonProperty? includedAssetIds = null;
                foreach (var property in item.EnumerateObject())
                {
                    if (!string.Equals(property.Name, "IncludedAssetIds", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (includedAssetIds.HasValue)
                    {
                        assetIds = Array.Empty<Guid>();
                        return false;
                    }

                    includedAssetIds = property;
                }

                if (!includedAssetIds.HasValue)
                    continue;
                if (includedAssetIds.Value.Value.ValueKind == JsonValueKind.Null)
                    continue;
                if (includedAssetIds.Value.Value.ValueKind != JsonValueKind.Array)
                {
                    assetIds = Array.Empty<Guid>();
                    return false;
                }

                foreach (var value in includedAssetIds.Value.Value.EnumerateArray())
                {
                    if (value.ValueKind != JsonValueKind.String || !value.TryGetGuid(out var includedAssetId))
                    {
                        assetIds = Array.Empty<Guid>();
                        return false;
                    }
                    if (includedAssetId == Guid.Empty)
                        continue;

                    parsedAssetIds.Add(includedAssetId);
                    if (!uniqueAssetIds.Add(includedAssetId))
                        hasDuplicateReferences = true;
                }
            }

            assetIds = parsedAssetIds;
            return true;
        }
        catch (JsonException)
        {
            assetIds = Array.Empty<Guid>();
            return false;
        }
    }

    public static RentalBillingTemplateAssetCoverage Evaluate(string? billingTemplateJson, Guid assetId)
    {
        if (!TryGetExplicitIncludedAssetIds(
                billingTemplateJson,
                out var assetIds,
                out _))
        {
            return RentalBillingTemplateAssetCoverage.MalformedTemplate;
        }

        if (assetIds.Count == 0)
            return RentalBillingTemplateAssetCoverage.NoExplicitCoverage;

        var matchingReferenceCount = assetIds.Count(includedAssetId => includedAssetId == assetId);
        if (matchingReferenceCount == 0)
            return RentalBillingTemplateAssetCoverage.MissingFromExplicitCoverage;
        if (matchingReferenceCount == 1)
            return RentalBillingTemplateAssetCoverage.UniqueReference;
        return RentalBillingTemplateAssetCoverage.AmbiguousReference;
    }

    public static bool AllowsLink(string? billingTemplateJson, Guid assetId)
        => Evaluate(billingTemplateJson, assetId) is
            RentalBillingTemplateAssetCoverage.NoExplicitCoverage or
            RentalBillingTemplateAssetCoverage.UniqueReference;
}
