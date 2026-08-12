using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Tools.SyncDiag;

internal sealed record IsolatedSeedRetryRentalAssetReconcileResult(
    int UnlinkedAssets,
    int ClosedAssignmentHistories,
    int RemovedStaleOutbox);

internal static class IsolatedSeedRetryRentalAssetReconciler
{
    internal const string AssignmentChangeReason =
        "격리 테스트 시드 레거시 청구제외 연결 정리";

    private const string BillingEligibilityExcluded = "청구제외";

    internal static async Task<IsolatedSeedRetryRentalAssetReconcileResult> ReconcileAsync(
        LocalDbContext db,
        DateTime nowUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (nowUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("The retry reconciliation timestamp must be UTC.", nameof(nowUtc));

        var candidates = await db.RentalAssets
            .IgnoreQueryFilters()
            .Where(asset =>
                !asset.IsDeleted &&
                asset.IsDirty &&
                asset.BillingProfileId.HasValue &&
                asset.BillingProfileId.Value != Guid.Empty)
            .OrderBy(asset => asset.Id)
            .ToListAsync(ct);
        if (candidates.Count == 0)
            return new IsolatedSeedRetryRentalAssetReconcileResult(
                0,
                0,
                0);

        var profileIds = candidates
            .Select(asset => asset.BillingProfileId!.Value)
            .Distinct()
            .ToList();
        var profilesById = await db.RentalBillingProfiles
            .IgnoreQueryFilters()
            .Where(profile => profileIds.Contains(profile.Id))
            .ToDictionaryAsync(profile => profile.Id, ct);
        var candidateIds = candidates
            .Select(asset => asset.Id)
            .ToList();
        var historiesByAssetId = (await db.RentalAssetAssignmentHistories
                .IgnoreQueryFilters()
                .Where(history => candidateIds.Contains(history.AssetId))
                .ToListAsync(ct))
            .GroupBy(history => history.AssetId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var changedAssetIds = new List<Guid>();
        var changedHistoryIds = new List<Guid>();
        foreach (var asset in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsExactExcludedZeroFeeCandidate(asset) ||
                !asset.BillingProfileId.HasValue ||
                !profilesById.TryGetValue(asset.BillingProfileId.Value, out var profile) ||
                !IsStableMatchingProfile(asset, profile) ||
                !HasExactMissingExplicitCoverage(profile.BillingTemplateJson, asset.Id))
            {
                continue;
            }

            historiesByAssetId.TryGetValue(asset.Id, out var assetHistories);
            var currentHistories = (assetHistories ?? [])
                .Where(history => !history.IsDeleted && history.IsCurrent)
                .ToList();
            if (currentHistories.Count != 1)
                continue;

            var currentHistory = currentHistories[0];
            if (!IsExactCurrentHistory(asset, profile, currentHistory, nowUtc))
                continue;

            var previousBillingProfileId = asset.BillingProfileId.Value;
            asset.LastCustomerName = NormalizeFirstNonEmpty(
                asset.CurrentCustomerName,
                asset.CustomerName,
                asset.LastCustomerName);
            asset.LastInstallLocation = NormalizeFirstNonEmpty(
                asset.InstallLocation,
                asset.InstallSiteName,
                asset.LastInstallLocation);
            asset.LastBillingProfileId = previousBillingProfileId;
            asset.LastBillingProfileDisplay = BuildProfileDisplay(profile);
            asset.LastAssignmentClearedAtUtc = nowUtc;
            asset.BillingProfileId = null;
            asset.CustomerId = null;
            asset.IsDirty = true;
            asset.UpdatedAtUtc = nowUtc;

            currentHistory.IsCurrent = false;
            currentHistory.UnlinkedAtUtc = nowUtc;
            currentHistory.ChangeReason = AssignmentChangeReason;
            currentHistory.IsDirty = true;
            currentHistory.UpdatedAtUtc = nowUtc;

            changedAssetIds.Add(asset.Id);
            changedHistoryIds.Add(currentHistory.Id);
        }

        if (changedAssetIds.Count == 0)
            return new IsolatedSeedRetryRentalAssetReconcileResult(
                0,
                0,
                0);

        await db.SaveChangesAsync(ct);
        var removedStaleOutbox = await db.SyncOutboxEntries
            .Where(entry =>
                entry.Status != "Acknowledged" &&
                ((entry.EntityName == nameof(LocalRentalAsset) &&
                  changedAssetIds.Contains(entry.EntityId)) ||
                 (entry.EntityName == nameof(LocalRentalAssetAssignmentHistory) &&
                  changedHistoryIds.Contains(entry.EntityId))))
            .ExecuteDeleteAsync(ct);

        return new IsolatedSeedRetryRentalAssetReconcileResult(
            changedAssetIds.Count,
            changedHistoryIds.Count,
            removedStaleOutbox);
    }

    private static bool IsExactExcludedZeroFeeCandidate(LocalRentalAsset asset)
        => asset.Revision > 0 &&
           asset.MonthlyFee == 0m &&
           string.Equals(
               RentalCatalogValueNormalizer.NormalizeDisplayText(
                   asset.BillingEligibilityStatus),
               BillingEligibilityExcluded,
               StringComparison.Ordinal);

    private static bool IsStableMatchingProfile(
        LocalRentalAsset asset,
        LocalRentalBillingProfile profile)
        => !profile.IsDeleted &&
           profile.IsActive &&
           !profile.IsDirty &&
           profile.Revision > 0 &&
           asset.CustomerId.HasValue &&
           asset.CustomerId.Value != Guid.Empty &&
           profile.CustomerId == asset.CustomerId &&
           SameNonEmptyScope(asset.TenantCode, profile.TenantCode) &&
           SameNonEmptyScope(asset.OfficeCode, profile.OfficeCode) &&
           SameNonEmptyScope(
               asset.ResponsibleOfficeCode,
               profile.ResponsibleOfficeCode);

    private static bool HasExactMissingExplicitCoverage(
        string? billingTemplateJson,
        Guid assetId)
    {
        if (!RentalBillingTemplateAssetCoverageRules.TryGetExplicitIncludedAssetIds(
                billingTemplateJson,
                out var explicitAssetIds,
                out var hasDuplicateReferences) ||
            hasDuplicateReferences ||
            explicitAssetIds.Count == 0)
        {
            return false;
        }

        return RentalBillingTemplateAssetCoverageRules.Evaluate(
                billingTemplateJson,
                assetId) ==
            RentalBillingTemplateAssetCoverage.MissingFromExplicitCoverage;
    }

    private static bool IsExactCurrentHistory(
        LocalRentalAsset asset,
        LocalRentalBillingProfile profile,
        LocalRentalAssetAssignmentHistory history,
        DateTime nowUtc)
        => history.IsDirty &&
           history.Revision > 0 &&
           history.BillingProfileId == profile.Id &&
           history.CustomerId == asset.CustomerId &&
           history.LinkedAtUtc != default &&
           history.LinkedAtUtc < nowUtc &&
           !history.UnlinkedAtUtc.HasValue &&
           SameNonEmptyScope(history.TenantCode, asset.TenantCode) &&
           SameNonEmptyScope(
               history.ResponsibleOfficeCode,
               asset.ResponsibleOfficeCode);

    private static bool SameNonEmptyScope(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left) &&
           !string.IsNullOrWhiteSpace(right) &&
           string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeFirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            var normalized = RentalCatalogValueNormalizer.NormalizeDisplayText(value);
            if (!string.IsNullOrWhiteSpace(normalized))
                return normalized;
        }

        return string.Empty;
    }

    private static string BuildProfileDisplay(LocalRentalBillingProfile profile)
    {
        var customerName = RentalCatalogValueNormalizer.NormalizeDisplayText(
            profile.CustomerName);
        var itemName = RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(
            profile.ItemName);
        if (!string.IsNullOrWhiteSpace(customerName) &&
            !string.IsNullOrWhiteSpace(itemName))
        {
            return $"{customerName} · {itemName}";
        }

        return string.IsNullOrWhiteSpace(customerName)
            ? itemName
            : customerName;
    }
}
