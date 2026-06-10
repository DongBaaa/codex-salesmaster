using 거래플랜.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace 거래플랜.Server.Api.Data;

public static partial class DbInitializer
{
    private const string BillingEligibilityExcluded = "청구제외";

    private static async Task NormalizeRentalAssetStatusCatalogAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var assets = await dbContext.RentalAssets
            .IgnoreQueryFilters()
            .Where(asset =>
                asset.AssetStatus == "미배정" ||
                asset.AssetStatus == "대기" ||
                asset.AssetStatus == "회수" ||
                asset.AssetStatus == "창고" ||
                asset.AssetStatus == "설치중")
            .ToListAsync(cancellationToken);

        if (assets.Count == 0)
            return;

        var now = DateTime.UtcNow;
        foreach (var asset in assets)
        {
            var normalizedStatus = RentalAssetStatusNormalizer.Normalize(asset.AssetStatus);
            if (string.Equals(normalizedStatus, "설치중", StringComparison.OrdinalIgnoreCase))
                normalizedStatus = RentalAssetStatusNormalizer.Active;

            var changed = false;

            if (!string.Equals(asset.AssetStatus, normalizedStatus, StringComparison.Ordinal))
            {
                asset.AssetStatus = normalizedStatus;
                changed = true;
            }

            if (RentalAssetStatusNormalizer.IsNonOperating(normalizedStatus))
            {
                if (!string.Equals(asset.BillingEligibilityStatus, BillingEligibilityExcluded, StringComparison.OrdinalIgnoreCase))
                {
                    asset.BillingEligibilityStatus = BillingEligibilityExcluded;
                    changed = true;
                }

                if (string.IsNullOrWhiteSpace(asset.BillingExclusionReason) ||
                    asset.BillingExclusionReason.Trim().StartsWith("자산상태:", StringComparison.OrdinalIgnoreCase))
                {
                    var normalizedExclusionReason = $"자산상태: {normalizedStatus}";
                    if (!string.Equals(asset.BillingExclusionReason, normalizedExclusionReason, StringComparison.Ordinal))
                    {
                        asset.BillingExclusionReason = normalizedExclusionReason;
                        changed = true;
                    }
                }
            }

            if (changed)
                asset.UpdatedAtUtc = now;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
