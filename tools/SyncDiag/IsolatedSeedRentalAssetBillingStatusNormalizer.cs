using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Tools.SyncDiag;

internal static class IsolatedSeedRentalAssetBillingStatusNormalizer
{
    internal const string BillingEligibilityUnconfirmed = "미확인";

    private const string TargetTenantCode = TenantScopeCatalog.UsenetGroup;
    private const string TargetResponsibleOfficeCode = OfficeCodeCatalog.Usenet;

    internal static async Task<int> NormalizeAsync(
        LocalDbContext db,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        await using var ownedTransaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;

        var candidates = (await db.RentalAssets
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(asset =>
                    !asset.IsDeleted &&
                    !asset.IsDirty &&
                    asset.Revision > 0 &&
                    !asset.BillingProfileId.HasValue &&
                    asset.MonthlyFee == 0m &&
                    asset.TenantCode == TargetTenantCode &&
                    asset.ResponsibleOfficeCode == TargetResponsibleOfficeCode)
                .OrderBy(asset => asset.Id)
                .ToListAsync(ct))
            .Where(asset =>
                !RentalAssetStatusRules.IsNonOperating(asset.AssetStatus) &&
                string.IsNullOrWhiteSpace(
                    RentalCatalogValueNormalizer.NormalizeDisplayText(
                        asset.BillingEligibilityStatus)) &&
                string.IsNullOrWhiteSpace(
                    RentalCatalogValueNormalizer.NormalizeDisplayText(
                        asset.BillingExclusionReason)))
            .ToList();
        if (candidates.Count == 0)
            return 0;

        var candidateIds = candidates.Select(asset => asset.Id).ToList();
        var activeTemplates = await db.RentalBillingProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(profile => !profile.IsDeleted && profile.IsActive)
            .Select(profile => profile.BillingTemplateJson)
            .ToListAsync(ct);
        var explicitlyReferencedAssetIds = new HashSet<Guid>();
        foreach (var templateJson in activeTemplates)
        {
            if (!RentalBillingTemplateAssetCoverageRules.TryGetExplicitIncludedAssetIds(
                    templateJson,
                    out var explicitAssetIds,
                    out _))
            {
                return 0;
            }

            explicitlyReferencedAssetIds.UnionWith(explicitAssetIds);
        }

        var outboxByAssetId = (await db.SyncOutboxEntries
                .AsNoTracking()
                .Where(entry =>
                    candidateIds.Contains(entry.EntityId) &&
                    (entry.EntityName == nameof(LocalRentalAsset) ||
                     entry.EntityName == "RentalAsset"))
                .ToListAsync(ct))
            .GroupBy(entry => entry.EntityId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var safeCandidates = candidates
            .Where(asset => !explicitlyReferencedAssetIds.Contains(asset.Id))
            .Where(asset =>
                !outboxByAssetId.TryGetValue(asset.Id, out var assetOutbox) ||
                (assetOutbox.All(entry => string.Equals(
                     entry.Status,
                     "Acknowledged",
                     StringComparison.OrdinalIgnoreCase)) &&
                 assetOutbox.All(entry => entry.ExpectedRevision == asset.Revision)))
            .ToList();
        if (safeCandidates.Count == 0)
            return 0;

        var normalizedCount = 0;
        foreach (var candidate in safeCandidates)
        {
            ct.ThrowIfCancellationRequested();
            normalizedCount += await db.RentalAssets
                .IgnoreQueryFilters()
                .Where(asset =>
                    asset.Id == candidate.Id &&
                    asset.Revision == candidate.Revision &&
                    asset.AssetStatus == candidate.AssetStatus &&
                    !asset.IsDeleted &&
                    !asset.IsDirty &&
                    asset.Revision > 0 &&
                    !asset.BillingProfileId.HasValue &&
                    asset.MonthlyFee == 0m &&
                    asset.TenantCode == TargetTenantCode &&
                    asset.ResponsibleOfficeCode == TargetResponsibleOfficeCode &&
                    (asset.BillingEligibilityStatus == null ||
                     asset.BillingEligibilityStatus.Trim() == string.Empty) &&
                    (asset.BillingExclusionReason == null ||
                     asset.BillingExclusionReason.Trim() == string.Empty))
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        asset => asset.BillingEligibilityStatus,
                        BillingEligibilityUnconfirmed),
                    ct);
        }

        if (ownedTransaction is not null)
            await ownedTransaction.CommitAsync(ct);
        return normalizedCount;
    }
}
