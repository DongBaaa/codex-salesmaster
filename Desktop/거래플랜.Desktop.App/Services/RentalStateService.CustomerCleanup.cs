using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

public sealed partial class RentalStateService
{
    public async Task<IReadOnlyList<RentalCustomerLinkCleanupRow>> GetRentalCustomerLinkCleanupRowsAsync(
        SessionState session,
        CancellationToken ct = default)
    {
        var offices = await GetOfficeMapAsync(ct);
        var customers = await BuildRentalCleanupCustomerQuery(session)
            .OrderBy(customer => customer.NameOriginal)
            .ToListAsync(ct);
        var writableCustomerIds = (await BuildRentalCleanupWritableCustomerQuery(session)
                .Select(customer => customer.Id)
                .ToListAsync(ct))
            .ToHashSet();
        var profiles = await ApplyBillingScope(_db.RentalBillingProfiles.AsNoTracking(), session)
            .Where(profile => !profile.IsDeleted)
            .OrderBy(profile => profile.CustomerName)
            .ThenBy(profile => profile.InstallSiteName)
            .ToListAsync(ct);
        var assets = await ApplyAssetScope(_db.RentalAssets.AsNoTracking(), session)
            .Where(asset => !asset.IsDeleted)
            .OrderBy(asset => asset.CurrentCustomerName)
            .ThenBy(asset => asset.ManagementNumber)
            .ToListAsync(ct);

        await NormalizeAssetCustomerDisplayNamesAsync(assets, ct);
        return BuildRentalCustomerLinkCleanupRows(
            profiles,
            assets,
            customers,
            offices,
            (profile, resolvedCustomer) =>
                CanNormalizeRentalProfileEntityScope(profile, session) &&
                writableCustomerIds.Contains(resolvedCustomer.Id),
            (asset, resolvedCustomer) =>
                CanNormalizeRentalAssetEntityScope(asset, session) &&
                writableCustomerIds.Contains(resolvedCustomer.Id));
    }

    public async Task<RentalCustomerLinkCleanupResult> NormalizeRentalCustomerLinksAsync(
        SessionState session,
        CancellationToken ct = default)
    {
        if (!CanEditRentalSettings(session))
            throw new InvalidOperationException("권한이 없어 렌탈 거래처명 정리를 실행할 수 없습니다.");

        var offices = await GetOfficeMapAsync(ct);
        var customers = await BuildRentalCleanupWritableCustomerQuery(session)
            .OrderBy(customer => customer.NameOriginal)
            .ToListAsync(ct);
        var customerById = customers.ToDictionary(customer => customer.Id);
        var visibleProfiles = await ApplyBillingScope(_db.RentalBillingProfiles.IgnoreQueryFilters(), session)
            .Where(profile => !profile.IsDeleted)
            .OrderBy(profile => profile.CustomerName)
            .ThenBy(profile => profile.InstallSiteName)
            .ToListAsync(ct);
        var visibleAssets = await ApplyAssetScope(_db.RentalAssets.IgnoreQueryFilters(), session)
            .Where(asset => !asset.IsDeleted)
            .OrderBy(asset => asset.CurrentCustomerName)
            .ThenBy(asset => asset.ManagementNumber)
            .ToListAsync(ct);
        var profiles = visibleProfiles
            .Where(profile => CanNormalizeRentalProfileEntityScope(profile, session))
            .ToList();
        var assets = visibleAssets
            .Where(asset => CanNormalizeRentalAssetEntityScope(asset, session))
            .ToList();

        await NormalizeAssetCustomerDisplayNamesAsync(assets, ct);
        var reviewRows = BuildRentalCustomerLinkCleanupRows(profiles, assets, customers, offices);
        var profileById = profiles.ToDictionary(profile => profile.Id);
        var assetsByProfileId = assets
            .Where(asset => asset.BillingProfileId.HasValue && asset.BillingProfileId.Value != Guid.Empty)
            .GroupBy(asset => asset.BillingProfileId!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());

        var now = DateTime.UtcNow;
        var updatedProfileCount = 0;
        var updatedAssetCount = 0;
        var linkedCustomerCount = 0;

        foreach (var profile in profiles)
        {
            var changed = false;
            var resolvedCustomer = ResolveRentalCleanupCustomer(
                customers,
                customerById,
                profile.CustomerId,
                profile.BusinessNumber,
                profile.ResponsibleOfficeCode,
                profile.CustomerName,
                profile.InstallSiteName);

            if (resolvedCustomer is not null)
            {
                var normalizedCustomerName = RentalCatalogValueNormalizer.NormalizeDisplayText(resolvedCustomer.NameOriginal);
                if (profile.CustomerId != resolvedCustomer.Id)
                {
                    profile.CustomerId = resolvedCustomer.Id;
                    changed = true;
                }

                if (!string.Equals(profile.CustomerName, normalizedCustomerName, StringComparison.Ordinal))
                {
                    profile.CustomerName = normalizedCustomerName;
                    changed = true;
                }

                var resolvedOfficeCode = ResolveCustomerRentalOfficeCode(resolvedCustomer.ResponsibleOfficeCode);
                if (!string.IsNullOrWhiteSpace(resolvedOfficeCode) &&
                    !string.Equals(profile.ResponsibleOfficeCode, resolvedOfficeCode, StringComparison.OrdinalIgnoreCase))
                {
                    profile.ResponsibleOfficeCode = resolvedOfficeCode;
                    changed = true;
                }

                if (!string.IsNullOrWhiteSpace(resolvedOfficeCode) &&
                    !string.Equals(profile.ManagementCompanyCode, resolvedOfficeCode, StringComparison.OrdinalIgnoreCase))
                {
                    profile.ManagementCompanyCode = resolvedOfficeCode;
                    changed = true;
                }

                linkedCustomerCount++;
            }

            var normalizedInstallSiteName = RentalCatalogValueNormalizer.NormalizeDisplayText(profile.InstallSiteName);
            if (string.IsNullOrWhiteSpace(normalizedInstallSiteName) &&
                assetsByProfileId.TryGetValue(profile.Id, out var profileAssets))
            {
                var assetLocations = profileAssets
                    .Select(asset => RentalCatalogValueNormalizer.NormalizeDisplayText(string.IsNullOrWhiteSpace(asset.InstallLocation) ? asset.InstallSiteName : asset.InstallLocation))
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (assetLocations.Count == 1)
                    normalizedInstallSiteName = assetLocations[0];
            }

            if (string.IsNullOrWhiteSpace(normalizedInstallSiteName) && !string.IsNullOrWhiteSpace(profile.CustomerName))
                normalizedInstallSiteName = RentalCatalogValueNormalizer.NormalizeDisplayText(profile.CustomerName);

            if (!string.Equals(profile.InstallSiteName, normalizedInstallSiteName, StringComparison.Ordinal))
            {
                profile.InstallSiteName = normalizedInstallSiteName;
                changed = true;
            }

            if (!changed)
                continue;

            profile.UpdatedAtUtc = now;
            profile.IsDirty = true;
            updatedProfileCount++;
        }

        foreach (var asset in assets)
        {
            var changed = false;
            LocalRentalBillingProfile? linkedProfile = null;
            if (asset.BillingProfileId.HasValue && asset.BillingProfileId.Value != Guid.Empty)
            {
                if (!profileById.TryGetValue(asset.BillingProfileId.Value, out linkedProfile))
                {
                    asset.BillingProfileId = null;
                    changed = true;
                }
            }

            var resolvedCustomer = ResolveRentalCleanupCustomer(
                customers,
                customerById,
                linkedProfile?.CustomerId ?? asset.CustomerId,
                linkedProfile?.BusinessNumber,
                linkedProfile?.ResponsibleOfficeCode ?? asset.ResponsibleOfficeCode,
                linkedProfile?.CustomerName,
                asset.CurrentCustomerName,
                asset.CustomerName);

            if (resolvedCustomer is not null)
            {
                var normalizedCustomerName = RentalCatalogValueNormalizer.NormalizeDisplayText(resolvedCustomer.NameOriginal);
                if (asset.CustomerId != resolvedCustomer.Id)
                {
                    asset.CustomerId = resolvedCustomer.Id;
                    changed = true;
                }

                if (!string.Equals(asset.CustomerName, normalizedCustomerName, StringComparison.Ordinal))
                {
                    asset.CustomerName = normalizedCustomerName;
                    changed = true;
                }

                if (!string.Equals(asset.CurrentCustomerName, normalizedCustomerName, StringComparison.Ordinal))
                {
                    asset.CurrentCustomerName = normalizedCustomerName;
                    changed = true;
                }

                var resolvedOfficeCode = ResolveCustomerRentalOfficeCode(resolvedCustomer.ResponsibleOfficeCode);
                if (!string.IsNullOrWhiteSpace(resolvedOfficeCode) &&
                    !string.Equals(asset.ResponsibleOfficeCode, resolvedOfficeCode, StringComparison.OrdinalIgnoreCase))
                {
                    asset.ResponsibleOfficeCode = resolvedOfficeCode;
                    changed = true;
                }

                if (!string.IsNullOrWhiteSpace(resolvedOfficeCode) &&
                    !string.Equals(asset.ManagementCompanyCode, resolvedOfficeCode, StringComparison.OrdinalIgnoreCase))
                {
                    asset.ManagementCompanyCode = resolvedOfficeCode;
                    changed = true;
                }

                linkedCustomerCount++;
            }

            var normalizedInstallLocation = RentalCatalogValueNormalizer.NormalizeDisplayText(asset.InstallLocation);
            if (!string.Equals(asset.InstallLocation, normalizedInstallLocation, StringComparison.Ordinal))
            {
                asset.InstallLocation = normalizedInstallLocation;
                changed = true;
            }

            var normalizedInstallSiteName = string.IsNullOrWhiteSpace(normalizedInstallLocation)
                ? RentalCatalogValueNormalizer.NormalizeDisplayText(asset.InstallSiteName)
                : normalizedInstallLocation;
            if (!string.Equals(asset.InstallSiteName, normalizedInstallSiteName, StringComparison.Ordinal))
            {
                asset.InstallSiteName = normalizedInstallSiteName;
                changed = true;
            }

            if (!changed)
                continue;

            asset.UpdatedAtUtc = now;
            asset.IsDirty = true;
            updatedAssetCount++;
        }

        if (updatedProfileCount > 0 || updatedAssetCount > 0)
            await _db.SaveChangesAsync(ct);

        return new RentalCustomerLinkCleanupResult
        {
            ScannedProfileCount = profiles.Count,
            ScannedAssetCount = assets.Count,
            ReviewItemCount = reviewRows.Count,
            UpdatedProfileCount = updatedProfileCount,
            UpdatedAssetCount = updatedAssetCount,
            LinkedCustomerCount = linkedCustomerCount
        };
    }

    private IReadOnlyList<RentalCustomerLinkCleanupRow> BuildRentalCustomerLinkCleanupRows(
        IReadOnlyList<LocalRentalBillingProfile> profiles,
        IReadOnlyList<LocalRentalAsset> assets,
        IReadOnlyList<LocalCustomer> customers,
        IReadOnlyDictionary<string, string> offices,
        Func<LocalRentalBillingProfile, LocalCustomer, bool>? canAutoNormalizeProfile = null,
        Func<LocalRentalAsset, LocalCustomer, bool>? canAutoNormalizeAsset = null)
    {
        var customerById = customers.ToDictionary(customer => customer.Id);
        var customerNameMap = customers.ToDictionary(
            customer => customer.Id,
            customer => RentalCatalogValueNormalizer.NormalizeDisplayText(customer.NameOriginal));
        var profileById = profiles.ToDictionary(profile => profile.Id);
        var rows = new List<RentalCustomerLinkCleanupRow>();

        foreach (var profile in profiles)
        {
            var currentCustomerName = ResolveBillingProfileCustomerDisplayName(profile, customerNameMap);
            var resolvedCustomer = ResolveRentalCleanupCustomer(
                customers,
                customerById,
                profile.CustomerId,
                profile.BusinessNumber,
                profile.ResponsibleOfficeCode,
                profile.CustomerName,
                profile.InstallSiteName);

            var issues = new List<string>();
            if (resolvedCustomer is null)
            {
                if (!string.IsNullOrWhiteSpace(currentCustomerName) || !string.IsNullOrWhiteSpace(profile.BusinessNumber))
                    issues.Add("메인 거래처 미연결");
            }
            else
            {
                var normalizedMasterName = RentalCatalogValueNormalizer.NormalizeDisplayText(resolvedCustomer.NameOriginal);
                if (profile.CustomerId != resolvedCustomer.Id)
                    issues.Add("거래처 연결 누락/불일치");
                if (!string.Equals(currentCustomerName, normalizedMasterName, StringComparison.Ordinal))
                    issues.Add("거래처명 불일치");

                var resolvedOfficeCode = ResolveCustomerRentalOfficeCode(resolvedCustomer.ResponsibleOfficeCode);
                if (!string.IsNullOrWhiteSpace(resolvedOfficeCode) &&
                    (!string.Equals(profile.ResponsibleOfficeCode, resolvedOfficeCode, StringComparison.OrdinalIgnoreCase) ||
                     !string.Equals(profile.ManagementCompanyCode, resolvedOfficeCode, StringComparison.OrdinalIgnoreCase)))
                {
                    issues.Add("담당지점 불일치");
                }
            }

            if (issues.Count == 0)
                continue;

            var canAutoNormalize = resolvedCustomer is not null &&
                (canAutoNormalizeProfile?.Invoke(profile, resolvedCustomer) ?? true);
            rows.Add(new RentalCustomerLinkCleanupRow
            {
                EntityType = "청구프로필",
                EntityId = profile.Id,
                ResponsibleOfficeName = ResolveOfficeDisplayName(profile.ResponsibleOfficeCode, profile.ManagementCompanyCode, offices),
                CurrentCustomerName = currentCustomerName,
                MasterCustomerName = resolvedCustomer?.NameOriginal ?? string.Empty,
                BusinessNumber = profile.BusinessNumber,
                ItemName = profile.ItemName,
                InstallLocation = profile.InstallSiteName,
                LinkedProfileDisplay = BuildBillingProfileDisplayName(profile, customerNameMap),
                IssueSummary = string.Join(" / ", issues),
                SuggestedAction = BuildRentalCleanupSuggestedAction(
                    resolvedCustomer is not null,
                    canAutoNormalize,
                    "메인 거래처명/담당지점 동기화"),
                CanAutoNormalize = canAutoNormalize
            });
        }

        foreach (var asset in assets)
        {
            profileById.TryGetValue(asset.BillingProfileId ?? Guid.Empty, out var linkedProfile);
            var currentCustomerName = ResolvePrimaryAssetCustomerName(asset);
            var resolvedCustomer = ResolveRentalCleanupCustomer(
                customers,
                customerById,
                linkedProfile?.CustomerId ?? asset.CustomerId,
                linkedProfile?.BusinessNumber,
                linkedProfile?.ResponsibleOfficeCode ?? asset.ResponsibleOfficeCode,
                linkedProfile?.CustomerName,
                asset.CurrentCustomerName,
                asset.CustomerName);

            var issues = new List<string>();
            if (asset.BillingProfileId.HasValue && asset.BillingProfileId.Value != Guid.Empty && linkedProfile is null)
                issues.Add("연결 청구프로필 없음");

            if (resolvedCustomer is null)
            {
                if (!string.IsNullOrWhiteSpace(currentCustomerName) || asset.CustomerId.HasValue)
                    issues.Add("메인 거래처 미연결");
            }
            else
            {
                var normalizedMasterName = RentalCatalogValueNormalizer.NormalizeDisplayText(resolvedCustomer.NameOriginal);
                if (asset.CustomerId != resolvedCustomer.Id)
                    issues.Add("거래처 연결 누락/불일치");
                if (!string.Equals(currentCustomerName, normalizedMasterName, StringComparison.Ordinal) ||
                    !string.Equals(RentalCatalogValueNormalizer.NormalizeDisplayText(asset.CustomerName), normalizedMasterName, StringComparison.Ordinal))
                {
                    issues.Add("거래처명 불일치");
                }

                var resolvedOfficeCode = ResolveCustomerRentalOfficeCode(resolvedCustomer.ResponsibleOfficeCode);
                if (!string.IsNullOrWhiteSpace(resolvedOfficeCode) &&
                    (!string.Equals(asset.ResponsibleOfficeCode, resolvedOfficeCode, StringComparison.OrdinalIgnoreCase) ||
                     !string.Equals(asset.ManagementCompanyCode, resolvedOfficeCode, StringComparison.OrdinalIgnoreCase)))
                {
                    issues.Add("담당지점 불일치");
                }
            }

            if (linkedProfile is not null &&
                linkedProfile.CustomerId.HasValue &&
                linkedProfile.CustomerId.Value != Guid.Empty &&
                resolvedCustomer is not null &&
                linkedProfile.CustomerId != resolvedCustomer.Id)
            {
                issues.Add("청구프로필 거래처와 불일치");
            }

            if (issues.Count == 0)
                continue;

            var canAutoNormalize = resolvedCustomer is not null &&
                (canAutoNormalizeAsset?.Invoke(asset, resolvedCustomer) ?? true);
            rows.Add(new RentalCustomerLinkCleanupRow
            {
                EntityType = "설치자산",
                EntityId = asset.Id,
                ResponsibleOfficeName = ResolveOfficeDisplayName(asset.ResponsibleOfficeCode, asset.ManagementCompanyCode, offices),
                CurrentCustomerName = currentCustomerName,
                MasterCustomerName = resolvedCustomer?.NameOriginal ?? string.Empty,
                BusinessNumber = string.Empty,
                ItemName = asset.ItemName,
                InstallLocation = string.IsNullOrWhiteSpace(asset.InstallLocation) ? asset.InstallSiteName : asset.InstallLocation,
                LinkedProfileDisplay = linkedProfile is null ? "미연결" : BuildBillingProfileDisplayName(linkedProfile, customerNameMap),
                IssueSummary = string.Join(" / ", issues),
                SuggestedAction = BuildRentalCleanupSuggestedAction(
                    resolvedCustomer is not null,
                    canAutoNormalize,
                    "메인 거래처명/설치 자산 거래처 동기화"),
                CanAutoNormalize = canAutoNormalize
            });
        }

        return rows
            .OrderBy(row => row.CanAutoNormalize ? 0 : 1)
            .ThenBy(row => row.EntityType, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(row => row.ResponsibleOfficeName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(row => row.CurrentCustomerName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(row => row.ItemName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string BuildRentalCleanupSuggestedAction(
        bool hasResolvedCustomer,
        bool canAutoNormalize,
        string autoNormalizeAction)
    {
        if (!hasResolvedCustomer)
            return "수동 거래처 연결 필요";

        return canAutoNormalize
            ? autoNormalizeAction
            : "권한 범위 밖 - 권한 있는 계정에서 정리";
    }

    private IQueryable<LocalCustomer> BuildRentalCleanupCustomerQuery(SessionState session)
    {
        var query = _db.Customers.AsNoTracking().Where(customer => !customer.IsDeleted);
        var currentTenantCode = ResolveCurrentRentalTenantCode(session);
        if (CanAdministrativelyViewAllRental(session))
            return query;

        if (CanViewAllRental(session))
            return query.Where(customer => customer.TenantCode == currentTenantCode);

        var readableOfficeCodes = GetReadableOfficeCodes(session);
        return query.Where(customer =>
            customer.TenantCode == currentTenantCode &&
            readableOfficeCodes.Contains(customer.ResponsibleOfficeCode));
    }

    private IQueryable<LocalCustomer> BuildRentalCleanupWritableCustomerQuery(SessionState session)
    {
        var query = _db.Customers.AsNoTracking().Where(customer => !customer.IsDeleted);
        if (CanAdministrativelyViewAllRental(session))
            return query;

        var currentTenantCode = ResolveCurrentRentalTenantCode(session);
        if (CanEditAllRental(session))
            return query.Where(customer => customer.TenantCode == currentTenantCode);

        var writableOfficeCodes = GetWritableRentalOfficeCodes(session);
        return query.Where(customer =>
            customer.TenantCode == currentTenantCode &&
            (writableOfficeCodes.Contains(customer.ResponsibleOfficeCode) ||
             writableOfficeCodes.Contains(customer.OfficeCode)));
    }

    private bool CanNormalizeRentalProfileEntityScope(LocalRentalBillingProfile profile, SessionState session)
        => CanEditRentalSettings(session) && CanEditRentalEntityScope(
            profile.TenantCode,
            profile.OfficeCode,
            profile.ManagementCompanyCode,
            profile.ResponsibleOfficeCode,
            session,
            officeCode => CanNormalizeRentalOfficeScope(officeCode, session));

    private bool CanNormalizeRentalAssetEntityScope(LocalRentalAsset asset, SessionState session)
        => CanEditRentalSettings(session) && CanEditRentalEntityScope(
            asset.TenantCode,
            asset.OfficeCode,
            asset.ManagementCompanyCode,
            asset.ResponsibleOfficeCode,
            session,
            officeCode => CanNormalizeRentalOfficeScope(officeCode, session));

    private bool CanNormalizeRentalOfficeScope(string? officeCode, SessionState session)
    {
        if (!CanEditRentalSettings(session))
            return false;
        if (CanEditAllRental(session) || session.HasGlobalDataScope)
            return true;

        return GetWritableRentalOfficeCodes(session).Contains(NormalizeOfficeCode(officeCode, DomainConstants.OfficeUsenet));
    }

    private static LocalCustomer? ResolveRentalCleanupCustomer(
        IReadOnlyList<LocalCustomer> customers,
        IReadOnlyDictionary<Guid, LocalCustomer> customerById,
        Guid? currentCustomerId,
        string? businessNumber,
        string? preferredOfficeCode,
        params string?[] candidateNames)
    {
        var candidateKeys = BuildRentalCleanupNameKeys(candidateNames);
        var normalizedPreferredOfficeCode = ResolveCustomerRentalOfficeCode(preferredOfficeCode);
        if (currentCustomerId.HasValue &&
            currentCustomerId.Value != Guid.Empty &&
            customerById.TryGetValue(currentCustomerId.Value, out var linkedCustomer))
        {
            var normalizedBusinessNumber = NormalizeRentalCleanupBusinessNumber(businessNumber);
            if ((string.IsNullOrWhiteSpace(normalizedBusinessNumber) ||
                 NormalizeRentalCleanupBusinessNumber(linkedCustomer.BusinessNumber) == normalizedBusinessNumber) &&
                (candidateKeys.Count == 0 || CustomerMatchesRentalCleanupNames(linkedCustomer, candidateKeys)))
            {
                return linkedCustomer;
            }
        }

        var normalizedRequestedBusinessNumber = NormalizeRentalCleanupBusinessNumber(businessNumber);
        if (!string.IsNullOrWhiteSpace(normalizedRequestedBusinessNumber))
        {
            var businessMatches = customers
                .Where(customer => NormalizeRentalCleanupBusinessNumber(customer.BusinessNumber) == normalizedRequestedBusinessNumber)
                .ToList();
            if (!string.IsNullOrWhiteSpace(normalizedPreferredOfficeCode) && businessMatches.Count > 1)
            {
                var officeBusinessMatches = PreferCustomerMatchesByOffice(
                    businessMatches,
                    normalizedPreferredOfficeCode,
                    customer => customer.OfficeCode,
                    customer => customer.ResponsibleOfficeCode);
                if (officeBusinessMatches.Count == 1)
                    return officeBusinessMatches[0];
                if (officeBusinessMatches.Count > 1)
                    businessMatches = officeBusinessMatches;
            }
            if (businessMatches.Count == 1)
                return businessMatches[0];
            if (businessMatches.Count > 1 && candidateKeys.Count > 0)
            {
                var namedBusinessMatches = businessMatches
                    .Where(customer => CustomerMatchesRentalCleanupNames(customer, candidateKeys))
                    .ToList();
                if (namedBusinessMatches.Count == 1)
                    return namedBusinessMatches[0];
            }
        }

        if (candidateKeys.Count == 0)
            return null;

        var nameMatches = customers
            .Where(customer => CustomerMatchesRentalCleanupNames(customer, candidateKeys))
            .ToList();
        if (!string.IsNullOrWhiteSpace(normalizedPreferredOfficeCode) && nameMatches.Count > 1)
        {
            var officeNameMatches = PreferCustomerMatchesByOffice(
                nameMatches,
                normalizedPreferredOfficeCode,
                customer => customer.OfficeCode,
                customer => customer.ResponsibleOfficeCode);
            if (officeNameMatches.Count == 1)
                return officeNameMatches[0];
            if (officeNameMatches.Count > 1)
                nameMatches = officeNameMatches;
        }
        return nameMatches.Count == 1 ? nameMatches[0] : null;
    }

    private static HashSet<string> BuildRentalCleanupNameKeys(params string?[] values)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

            foreach (var candidate in BuildWorkbookCustomerNameCandidates(value))
            {
                var normalized = RentalCatalogValueNormalizer.NormalizeLooseKey(candidate);
                if (!string.IsNullOrWhiteSpace(normalized))
                    keys.Add(normalized);
            }
        }

        return keys;
    }

    private static bool CustomerMatchesRentalCleanupNames(LocalCustomer customer, IReadOnlyCollection<string> candidateKeys)
    {
        if (candidateKeys.Count == 0)
            return false;

        var normalizedName = RentalCatalogValueNormalizer.NormalizeLooseKey(customer.NameOriginal);
        if (!string.IsNullOrWhiteSpace(normalizedName) && candidateKeys.Contains(normalizedName))
            return true;

        var normalizedMatchKey = RentalCatalogValueNormalizer.NormalizeLooseKey(
            string.IsNullOrWhiteSpace(customer.NameMatchKey) ? customer.NameOriginal : customer.NameMatchKey);
        return !string.IsNullOrWhiteSpace(normalizedMatchKey) && candidateKeys.Contains(normalizedMatchKey);
    }

    private static string NormalizeRentalCleanupBusinessNumber(string? businessNumber)
        => string.IsNullOrWhiteSpace(businessNumber)
            ? string.Empty
            : new string(businessNumber.Where(char.IsDigit).ToArray());

    private static string ResolveCustomerRentalOfficeCode(string? responsibleOfficeCode)
        => OfficeCodeCatalog.NormalizeOfficeCodeLoose(responsibleOfficeCode, null, DomainConstants.DefaultOfficeUsenet);
}
