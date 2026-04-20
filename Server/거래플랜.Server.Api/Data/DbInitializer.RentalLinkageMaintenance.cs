using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Server.Api.Data;

public static partial class DbInitializer
{
    private const string CanonicalMfcL8900CategoryName = "A4컬러복합기";
    private const string CanonicalMfcL8900ItemName = "MFC-L8900CDW";
    private const string BillingStatusOnHold = "보류";
    private const string CompletionPending = "미완료";
    private static readonly JsonSerializerOptions RentalTemplateJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static async Task RepairRentalCustomerLinkageAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var customers = await dbContext.Customers.IgnoreQueryFilters()
            .Where(customer => !customer.IsDeleted)
            .ToListAsync(cancellationToken);
        var customerById = customers.ToDictionary(customer => customer.Id);

        var profiles = await dbContext.RentalBillingProfiles.IgnoreQueryFilters().ToListAsync(cancellationToken);
        var assets = await dbContext.RentalAssets.IgnoreQueryFilters().ToListAsync(cancellationToken);
        var billingLogs = await dbContext.RentalBillingLogs.IgnoreQueryFilters().ToListAsync(cancellationToken);

        await RepairMfcL8900CategoryAsync(dbContext, now, cancellationToken);

        foreach (var profile in profiles.Where(profile => !profile.IsDeleted))
        {
            var changed = false;
            var resolvedCustomerId = ResolveProfileCustomerId(profile, assets, customerById, customers);
            if (profile.CustomerId != resolvedCustomerId)
            {
                profile.CustomerId = resolvedCustomerId;
                changed = true;
            }

            if (changed)
                TouchTrackedEntity(profile, now);
        }

        var activeProfilesById = profiles
            .Where(profile => !profile.IsDeleted)
            .ToDictionary(profile => profile.Id);
        var activeProfileAssetCounts = assets
            .Where(asset => !asset.IsDeleted && asset.BillingProfileId.HasValue && asset.BillingProfileId.Value != Guid.Empty)
            .GroupBy(asset => asset.BillingProfileId!.Value)
            .ToDictionary(group => group.Key, group => group.Count());

        foreach (var asset in assets.Where(asset => !asset.IsDeleted))
        {
            var changed = false;
            var assetCustomerKeys = BuildRentalCustomerKeys(
                asset.CustomerName,
                asset.CurrentCustomerName);
            var preferredResponsibleOfficeCode = ResolveRentalOperationalOfficeCode(
                asset.ResponsibleOfficeCode,
                asset.OfficeCode,
                asset.ManagementCompanyCode);
            var preferredOwnerOfficeCode = ResolveOperationalOwnerOfficeCode(
                asset.OfficeCode,
                preferredResponsibleOfficeCode,
                asset.ManagementCompanyCode,
                OfficeCodeCatalog.Usenet);
            var preferredTenantCode = NormalizeOperationalTenantCode(
                asset.TenantCode,
                preferredOwnerOfficeCode,
                preferredResponsibleOfficeCode);
            var normalizedInstallLocation = RentalCatalogValueNormalizer.NormalizeDisplayText(asset.InstallLocation);
            var normalizedInstallSiteName = RentalCatalogValueNormalizer.NormalizeDisplayText(asset.InstallSiteName);
            var canonicalInstallLocation = !string.IsNullOrWhiteSpace(normalizedInstallLocation)
                ? normalizedInstallLocation
                : normalizedInstallSiteName;

            if (!string.Equals(asset.InstallLocation, canonicalInstallLocation, StringComparison.Ordinal))
            {
                asset.InstallLocation = canonicalInstallLocation;
                changed = true;
            }

            if (!string.Equals(asset.InstallSiteName, canonicalInstallLocation, StringComparison.Ordinal))
            {
                asset.InstallSiteName = canonicalInstallLocation;
                changed = true;
            }

            if (asset.BillingProfileId.HasValue &&
                (!activeProfilesById.ContainsKey(asset.BillingProfileId.Value) || asset.BillingProfileId.Value == Guid.Empty))
            {
                UnregisterProfileAssetLink(activeProfileAssetCounts, asset.BillingProfileId.Value);
                asset.BillingProfileId = null;
                changed = true;
            }

            activeProfilesById.TryGetValue(asset.BillingProfileId ?? Guid.Empty, out var linkedProfile);
            if (linkedProfile is not null &&
                !ProfileMatchesAssetScope(
                    linkedProfile,
                    preferredTenantCode,
                    preferredResponsibleOfficeCode,
                    asset.CustomerId,
                    assetCustomerKeys))
            {
                UnregisterProfileAssetLink(activeProfileAssetCounts, linkedProfile.Id);
                asset.BillingProfileId = null;
                linkedProfile = null;
                changed = true;
            }

            var resolvedCustomerId = ResolveAssetCustomerId(asset, linkedProfile, customerById, customers);
            if (asset.CustomerId != resolvedCustomerId)
            {
                asset.CustomerId = resolvedCustomerId;
                changed = true;
            }

            if (asset.CustomerId.HasValue &&
                customerById.TryGetValue(asset.CustomerId.Value, out var linkedCustomer))
            {
                var resolvedResponsibleOfficeCode = ResolveCustomerRentalOfficeCode(linkedCustomer.ResponsibleOfficeCode);
                var resolvedOwnerOfficeCode = ResolveOperationalOwnerOfficeCode(
                    linkedCustomer.OfficeCode,
                    resolvedResponsibleOfficeCode,
                    linkedCustomer.OfficeCode);
                if (!string.IsNullOrWhiteSpace(resolvedResponsibleOfficeCode))
                {
                    if (!string.Equals(asset.ResponsibleOfficeCode, resolvedResponsibleOfficeCode, StringComparison.OrdinalIgnoreCase))
                    {
                        asset.ResponsibleOfficeCode = resolvedResponsibleOfficeCode;
                        changed = true;
                    }

                    if (!string.Equals(asset.OfficeCode, resolvedOwnerOfficeCode, StringComparison.OrdinalIgnoreCase))
                    {
                        asset.OfficeCode = resolvedOwnerOfficeCode;
                        changed = true;
                    }

                    if (!string.Equals(asset.ManagementCompanyCode, resolvedOwnerOfficeCode, StringComparison.OrdinalIgnoreCase))
                    {
                        asset.ManagementCompanyCode = resolvedOwnerOfficeCode;
                        changed = true;
                    }
                }

                if (string.IsNullOrWhiteSpace(asset.CustomerName))
                {
                    asset.CustomerName = RentalCatalogValueNormalizer.NormalizeDisplayText(linkedCustomer.NameOriginal);
                    changed = true;
                }

                if (string.IsNullOrWhiteSpace(asset.CurrentCustomerName))
                {
                    asset.CurrentCustomerName = RentalCatalogValueNormalizer.NormalizeDisplayText(linkedCustomer.NameOriginal);
                    changed = true;
                }

            }

            if (!asset.BillingProfileId.HasValue || asset.BillingProfileId.Value == Guid.Empty)
            {
                var resolvedBillingProfileId = ResolveAssetBillingProfileId(asset, profiles, activeProfileAssetCounts);
                if (resolvedBillingProfileId.HasValue && resolvedBillingProfileId.Value != Guid.Empty)
                {
                    asset.BillingProfileId = resolvedBillingProfileId.Value;
                    RegisterProfileAssetLink(activeProfileAssetCounts, resolvedBillingProfileId.Value);
                    changed = true;
                    activeProfilesById.TryGetValue(resolvedBillingProfileId.Value, out linkedProfile);
                }
            }

            if (linkedProfile is not null &&
                linkedProfile.CustomerId.HasValue &&
                linkedProfile.CustomerId.Value != Guid.Empty &&
                CustomerReferenceLooksValid(
                    linkedProfile.CustomerId,
                    customerById,
                    assetCustomerKeys,
                    preferredTenantCode,
                    preferredResponsibleOfficeCode) &&
                asset.CustomerId != linkedProfile.CustomerId)
            {
                asset.CustomerId = linkedProfile.CustomerId;
                changed = true;
            }

            if (linkedProfile is not null &&
                CustomerReferenceLooksValid(
                    linkedProfile.CustomerId,
                    customerById,
                    assetCustomerKeys,
                    preferredTenantCode,
                    preferredResponsibleOfficeCode) &&
                customerById.TryGetValue(linkedProfile.CustomerId ?? Guid.Empty, out var profileCustomer))
            {
                var resolvedResponsibleOfficeCode = ResolveCustomerRentalOfficeCode(profileCustomer.ResponsibleOfficeCode);
                var resolvedOwnerOfficeCode = ResolveOperationalOwnerOfficeCode(
                    profileCustomer.OfficeCode,
                    resolvedResponsibleOfficeCode,
                    profileCustomer.OfficeCode);
                if (!string.IsNullOrWhiteSpace(resolvedResponsibleOfficeCode))
                {
                    if (!string.Equals(asset.ResponsibleOfficeCode, resolvedResponsibleOfficeCode, StringComparison.OrdinalIgnoreCase))
                    {
                        asset.ResponsibleOfficeCode = resolvedResponsibleOfficeCode;
                        changed = true;
                    }

                    if (!string.Equals(asset.OfficeCode, resolvedOwnerOfficeCode, StringComparison.OrdinalIgnoreCase))
                    {
                        asset.OfficeCode = resolvedOwnerOfficeCode;
                        changed = true;
                    }

                    if (!string.Equals(asset.ManagementCompanyCode, resolvedOwnerOfficeCode, StringComparison.OrdinalIgnoreCase))
                    {
                        asset.ManagementCompanyCode = resolvedOwnerOfficeCode;
                        changed = true;
                    }
                }
            }

            if (changed)
                TouchTrackedEntity(asset, now);
        }

        var activeAssetsByProfileId = assets
            .Where(asset => !asset.IsDeleted && asset.BillingProfileId.HasValue && asset.BillingProfileId.Value != Guid.Empty)
            .GroupBy(asset => asset.BillingProfileId!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());

        foreach (var profile in profiles.Where(profile => !profile.IsDeleted))
        {
            var changed = false;
            var resolvedCustomerId = ResolveProfileCustomerId(profile, assets, customerById, customers);
            if (profile.CustomerId != resolvedCustomerId)
            {
                profile.CustomerId = resolvedCustomerId;
                changed = true;
            }

            if (profile.CustomerId.HasValue &&
                customerById.TryGetValue(profile.CustomerId.Value, out var linkedCustomer))
            {
                var normalizedMasterName = RentalCatalogValueNormalizer.NormalizeDisplayText(linkedCustomer.NameOriginal);
                if (!string.Equals(profile.CustomerName, normalizedMasterName, StringComparison.Ordinal))
                {
                    profile.CustomerName = normalizedMasterName;
                    changed = true;
                }

                var resolvedResponsibleOfficeCode = ResolveCustomerRentalOfficeCode(linkedCustomer.ResponsibleOfficeCode);
                var resolvedOwnerOfficeCode = ResolveOperationalOwnerOfficeCode(
                    linkedCustomer.OfficeCode,
                    resolvedResponsibleOfficeCode,
                    linkedCustomer.OfficeCode);
                if (!string.IsNullOrWhiteSpace(resolvedResponsibleOfficeCode))
                {
                    if (!string.Equals(profile.ResponsibleOfficeCode, resolvedResponsibleOfficeCode, StringComparison.OrdinalIgnoreCase))
                    {
                        profile.ResponsibleOfficeCode = resolvedResponsibleOfficeCode;
                        changed = true;
                    }

                    if (!string.Equals(profile.OfficeCode, resolvedOwnerOfficeCode, StringComparison.OrdinalIgnoreCase))
                    {
                        profile.OfficeCode = resolvedOwnerOfficeCode;
                        changed = true;
                    }

                    if (!string.Equals(profile.ManagementCompanyCode, resolvedOwnerOfficeCode, StringComparison.OrdinalIgnoreCase))
                    {
                        profile.ManagementCompanyCode = resolvedOwnerOfficeCode;
                        changed = true;
                    }
                }
            }

            var normalizedInstallSiteName = RentalCatalogValueNormalizer.NormalizeDisplayText(profile.InstallSiteName);
            if (string.IsNullOrWhiteSpace(normalizedInstallSiteName) &&
                activeAssetsByProfileId.TryGetValue(profile.Id, out var profileLinkedAssets))
            {
                var assetInstallLocations = profileLinkedAssets
                    .Select(asset => RentalCatalogValueNormalizer.NormalizeDisplayText(asset.InstallLocation))
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (assetInstallLocations.Count == 1)
                    normalizedInstallSiteName = assetInstallLocations[0];
            }

            if (string.IsNullOrWhiteSpace(normalizedInstallSiteName))
                normalizedInstallSiteName = RentalCatalogValueNormalizer.NormalizeDisplayText(profile.CustomerName);

            if (!string.Equals(profile.InstallSiteName, normalizedInstallSiteName, StringComparison.Ordinal))
            {
                profile.InstallSiteName = normalizedInstallSiteName;
                changed = true;
            }

            activeAssetsByProfileId.TryGetValue(profile.Id, out var linkedAssets);
            var hasLinkedAssets = linkedAssets is not null && linkedAssets.Count > 0;
            if (hasLinkedAssets &&
                TryBackfillBillingTemplate(profile, linkedAssets!, out var backfilledTemplateJson))
            {
                profile.BillingTemplateJson = backfilledTemplateJson;
                changed = true;
            }

            if (!hasLinkedAssets)
            {
                var normalizedBillingStatus = (profile.BillingStatus ?? string.Empty).Trim();
                if (!string.Equals(normalizedBillingStatus, BillingStatusOnHold, StringComparison.Ordinal))
                {
                    profile.BillingStatus = BillingStatusOnHold;
                    changed = true;
                }

                var normalizedCompletionStatus = (profile.CompletionStatus ?? string.Empty).Trim();
                if (!string.Equals(normalizedCompletionStatus, CompletionPending, StringComparison.Ordinal))
                {
                    profile.CompletionStatus = CompletionPending;
                    changed = true;
                }

                if (!profile.RequiresFollowUp)
                {
                    profile.RequiresFollowUp = true;
                    changed = true;
                }

            }

            if (changed)
                TouchTrackedEntity(profile, now);
        }

        var activeProfilesByIdAfterRepair = profiles
            .Where(profile => !profile.IsDeleted)
            .ToDictionary(profile => profile.Id);

        foreach (var log in billingLogs.Where(log => !log.IsDeleted))
        {
            var changed = false;
            if (!activeProfilesByIdAfterRepair.TryGetValue(log.BillingProfileId, out var profile))
            {
                if (changed)
                    TouchTrackedEntity(log, now);

                continue;
            }

            if (!string.Equals(log.ResponsibleOfficeCode, profile.ResponsibleOfficeCode, StringComparison.OrdinalIgnoreCase))
            {
                log.ResponsibleOfficeCode = profile.ResponsibleOfficeCode;
                changed = true;
            }

            if (!string.Equals(log.OfficeCode, profile.OfficeCode, StringComparison.OrdinalIgnoreCase))
            {
                log.OfficeCode = profile.OfficeCode;
                changed = true;
            }

            if (changed)
                TouchTrackedEntity(log, now);
        }
    }

    private static bool TryBackfillBillingTemplate(
        RentalBillingProfile profile,
        IReadOnlyList<RentalAsset> linkedAssets,
        out string backfilledTemplateJson)
    {
        backfilledTemplateJson = profile.BillingTemplateJson ?? string.Empty;
        if (linkedAssets.Count == 0 || !NeedsBillingTemplateBackfill(profile.BillingTemplateJson))
            return false;

        var includedAssetIds = linkedAssets
            .Select(asset => asset.Id)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (includedAssetIds.Count == 0)
            return false;

        List<RentalBillingTemplateBackfillItem>? items = null;
        if (!string.IsNullOrWhiteSpace(profile.BillingTemplateJson))
        {
            try
            {
                items = JsonSerializer.Deserialize<List<RentalBillingTemplateBackfillItem>>(profile.BillingTemplateJson, RentalTemplateJsonOptions);
            }
            catch
            {
                items = null;
            }
        }

        if (items is null || items.Count == 0)
        {
            items =
            [
                new RentalBillingTemplateBackfillItem
                {
                    DisplayItemName = string.IsNullOrWhiteSpace(profile.ItemName) ? "렌탈 임대료" : profile.ItemName,
                    BillingLineMode = string.IsNullOrWhiteSpace(profile.BillingType) ? "묶음" : profile.BillingType,
                    Quantity = 1m,
                    UnitPrice = Math.Max(0m, profile.MonthlyAmount),
                    Amount = Math.Max(0m, profile.MonthlyAmount),
                    IncludedAssetIds = includedAssetIds
                }
            ];
        }
        else if (items.Count == 1 && (items[0].IncludedAssetIds is null || items[0].IncludedAssetIds.Count == 0))
        {
            items[0].IncludedAssetIds = includedAssetIds;
        }
        else
        {
            return false;
        }

        var serialized = JsonSerializer.Serialize(items, RentalTemplateJsonOptions);
        if (string.Equals(serialized, profile.BillingTemplateJson ?? string.Empty, StringComparison.Ordinal))
            return false;

        backfilledTemplateJson = serialized;
        return true;
    }

    private static bool NeedsBillingTemplateBackfill(string? billingTemplateJson)
    {
        if (string.IsNullOrWhiteSpace(billingTemplateJson))
            return true;

        try
        {
            var items = JsonSerializer.Deserialize<List<RentalBillingTemplateBackfillItem>>(billingTemplateJson, RentalTemplateJsonOptions);
            if (items is null || items.Count == 0)
                return true;

            return items.Count == 1 &&
                   (items[0].IncludedAssetIds is null || items[0].IncludedAssetIds.Count == 0);
        }
        catch
        {
            return true;
        }
    }

    private sealed class RentalBillingTemplateBackfillItem
    {
        public Guid ItemId { get; set; } = Guid.NewGuid();
        public string DisplayItemName { get; set; } = string.Empty;
        public string BillingLineMode { get; set; } = string.Empty;
        public decimal Quantity { get; set; } = 1m;
        public decimal UnitPrice { get; set; }
        public decimal Amount { get; set; }
        public string Note { get; set; } = string.Empty;
        public List<Guid> IncludedAssetIds { get; set; } = new();
    }

    private static async Task RepairMfcL8900CategoryAsync(
        AppDbContext dbContext,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var items = await dbContext.Items.IgnoreQueryFilters()
            .Where(item => !item.IsDeleted)
            .Where(item => item.NameOriginal == CanonicalMfcL8900ItemName)
            .ToListAsync(cancellationToken);
        if (items.Count == 0)
            return;

        foreach (var item in items)
        {
            if (string.Equals(item.CategoryName, CanonicalMfcL8900CategoryName, StringComparison.Ordinal))
                continue;

            item.CategoryName = CanonicalMfcL8900CategoryName;
            TouchTrackedEntity(item, now);
        }
    }

    private static Guid? ResolveProfileCustomerId(
        RentalBillingProfile profile,
        IReadOnlyCollection<RentalAsset> assets,
        IReadOnlyDictionary<Guid, Customer> customerById,
        IReadOnlyCollection<Customer> customers)
    {
        var candidateKeys = BuildRentalCustomerKeys(
            profile.CustomerName);
        var preferredResponsibleOfficeCode = ResolveRentalOperationalOfficeCode(
            profile.ResponsibleOfficeCode,
            profile.OfficeCode,
            profile.ManagementCompanyCode);
        var preferredOwnerOfficeCode = ResolveOperationalOwnerOfficeCode(
            profile.OfficeCode,
            preferredResponsibleOfficeCode,
            profile.ManagementCompanyCode,
            OfficeCodeCatalog.Usenet);
        var preferredTenantCode = NormalizeOperationalTenantCode(
            profile.TenantCode,
            preferredOwnerOfficeCode,
            preferredResponsibleOfficeCode);

        if (CustomerReferenceLooksValid(
                profile.CustomerId,
                customerById,
                candidateKeys,
                preferredTenantCode,
                preferredResponsibleOfficeCode))
            return profile.CustomerId;

        var linkedAssetCustomerIds = assets
            .Where(asset => !asset.IsDeleted && asset.BillingProfileId == profile.Id)
            .Select(asset => asset.CustomerId)
            .Where(customerId => CustomerReferenceLooksValid(
                customerId,
                customerById,
                candidateKeys,
                preferredTenantCode,
                preferredResponsibleOfficeCode))
            .Select(customerId => customerId!.Value)
            .Distinct()
            .ToList();
        if (linkedAssetCustomerIds.Count == 1)
            return linkedAssetCustomerIds[0];

        return TryResolveRentalCustomerByNames(
                   customers,
                   profile.BusinessNumber,
                   candidateKeys,
                   preferredTenantCode,
                   preferredResponsibleOfficeCode,
                   out var resolvedCustomerId)
               || TryResolveBillingProfileCustomer(
                   customers,
                   profile,
                   preferredTenantCode,
                   preferredResponsibleOfficeCode,
                   out resolvedCustomerId)
            ? resolvedCustomerId
            : null;
    }

    private static Guid? ResolveAssetCustomerId(
        RentalAsset asset,
        RentalBillingProfile? linkedProfile,
        IReadOnlyDictionary<Guid, Customer> customerById,
        IReadOnlyCollection<Customer> customers)
    {
        var candidateKeys = BuildRentalCustomerKeys(
            asset.CustomerName,
            asset.CurrentCustomerName);
        var preferredResponsibleOfficeCode = ResolveRentalOperationalOfficeCode(
            asset.ResponsibleOfficeCode,
            asset.OfficeCode,
            asset.ManagementCompanyCode);
        var preferredOwnerOfficeCode = ResolveOperationalOwnerOfficeCode(
            asset.OfficeCode,
            preferredResponsibleOfficeCode,
            asset.ManagementCompanyCode,
            OfficeCodeCatalog.Usenet);
        var preferredTenantCode = NormalizeOperationalTenantCode(
            asset.TenantCode,
            preferredOwnerOfficeCode,
            preferredResponsibleOfficeCode);

        if (linkedProfile is not null &&
            CustomerReferenceLooksValid(
                linkedProfile.CustomerId,
                customerById,
                candidateKeys,
                preferredTenantCode,
                preferredResponsibleOfficeCode))
            return linkedProfile.CustomerId;

        if (CustomerReferenceLooksValid(
                asset.CustomerId,
                customerById,
                candidateKeys,
                preferredTenantCode,
                preferredResponsibleOfficeCode))
            return asset.CustomerId;

        return TryResolveRentalCustomerByNames(
                   customers,
                   null,
                   candidateKeys,
                   preferredTenantCode,
                   preferredResponsibleOfficeCode,
                   out var resolvedCustomerId)
               || TryResolveAssetCustomer(
                   customers,
                   asset,
                   preferredTenantCode,
                   preferredResponsibleOfficeCode,
                   out resolvedCustomerId)
            ? resolvedCustomerId
            : null;
    }

    private static Guid? ResolveAssetBillingProfileId(
        RentalAsset asset,
        IReadOnlyCollection<RentalBillingProfile> profiles,
        IReadOnlyDictionary<Guid, int> activeProfileAssetCounts)
    {
        var assetCustomerKeys = BuildRentalCustomerKeys(
            asset.CustomerName,
            asset.CurrentCustomerName);
        var customerProfiles = profiles
            .Where(profile => !profile.IsDeleted)
            .Where(profile => ProfileMatchesAssetCustomer(profile, asset.CustomerId, assetCustomerKeys))
            .ToList();
        if (customerProfiles.Count == 0)
            return null;

        var officeScopedProfiles = FilterProfilesByAssetOffice(customerProfiles, asset);
        if (officeScopedProfiles.Count > 0)
            customerProfiles = officeScopedProfiles;
        if (customerProfiles.Count == 1)
            return customerProfiles[0].Id;

        var normalizedItemKey = RentalCatalogValueNormalizer.NormalizeLooseKey(asset.ItemName);
        var siteKeys = BuildRentalSiteKeys(asset.InstallLocation, asset.InstallSiteName);

        if (!string.IsNullOrWhiteSpace(normalizedItemKey))
        {
            var itemMatches = customerProfiles
                .Where(profile => ProfileMatchesAssetItem(profile, normalizedItemKey))
                .ToList();

            if (siteKeys.Count > 0)
            {
                var strictMatches = itemMatches
                    .Where(profile => ProfileMatchesAssetSite(profile, siteKeys))
                    .ToList();
                if (strictMatches.Count == 1)
                    return strictMatches[0].Id;
            }

            if (itemMatches.Count == 1)
                return itemMatches[0].Id;
        }

        if (siteKeys.Count > 0)
        {
            var siteMatches = customerProfiles
                .Where(profile => ProfileMatchesAssetSite(profile, siteKeys))
                .ToList();
            if (siteMatches.Count == 1)
                return siteMatches[0].Id;
        }

        return SelectPreferredBillingProfile(customerProfiles, activeProfileAssetCounts, siteKeys);
    }

    private static bool TryResolveAssetCustomer(
        IReadOnlyCollection<Customer> customers,
        RentalAsset asset,
        string? preferredTenantCode,
        string? preferredResponsibleOfficeCode,
        out Guid customerId)
    {
        customerId = Guid.Empty;
        var candidateKeys = BuildRentalCustomerKeys(
            asset.CustomerName,
            asset.CurrentCustomerName);
        return TryResolveRentalCustomerByNames(
            customers,
            null,
            candidateKeys,
            preferredTenantCode,
            preferredResponsibleOfficeCode,
            out customerId);
    }

    private static HashSet<string> BuildRentalSiteKeys(params string?[] values)
        => values
            .Select(RentalCatalogValueNormalizer.NormalizeLooseKey)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static List<string> BuildRentalCustomerKeys(params string?[] values)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            foreach (var variant in EnumerateRentalNameVariants(value))
            {
                var normalized = RentalCatalogValueNormalizer.NormalizeLooseKey(variant);
                if (!string.IsNullOrWhiteSpace(normalized))
                    keys.Add(normalized);
            }
        }

        return [.. keys];
    }

    private static IEnumerable<string> EnumerateRentalNameVariants(string? value)
    {
        var display = RentalCatalogValueNormalizer.NormalizeDisplayText(value);
        if (string.IsNullOrWhiteSpace(display))
            yield break;

        yield return display;

        var openBracket = display.IndexOf('[');
        var closeBracket = openBracket >= 0 ? display.IndexOf(']', openBracket + 1) : -1;
        if (openBracket < 0 || closeBracket <= openBracket)
            yield break;

        var prefix = openBracket == 0
            ? display[(openBracket + 1)..closeBracket].Trim()
            : display[..openBracket].Trim();
        var suffix = openBracket == 0
            ? display[(closeBracket + 1)..].Trim()
            : display[(openBracket + 1)..closeBracket].Trim();

        if (string.IsNullOrWhiteSpace(prefix) || string.IsNullOrWhiteSpace(suffix))
            yield break;

        yield return prefix + suffix;
        yield return suffix + prefix;
    }

    private static bool TryResolveRentalCustomerByNames(
        IReadOnlyCollection<Customer> customers,
        string? businessNumber,
        IReadOnlyCollection<string> candidateKeys,
        string? preferredTenantCode,
        string? preferredResponsibleOfficeCode,
        out Guid customerId)
    {
        customerId = Guid.Empty;
        var normalizedBusinessNumber = NormalizeBusinessNumber(businessNumber);

        var scopedCustomers = FilterCustomersByOperationalScope(
            customers,
            preferredTenantCode,
            preferredResponsibleOfficeCode);
        if (!string.IsNullOrWhiteSpace(normalizedBusinessNumber))
        {
            var businessMatches = scopedCustomers
                .Where(customer => NormalizeBusinessNumber(customer.BusinessNumber) == normalizedBusinessNumber)
                .ToList();
            if (businessMatches.Count == 1 && (candidateKeys.Count == 0 || CustomerMatchesRentalNames(businessMatches[0], candidateKeys)))
            {
                customerId = businessMatches[0].Id;
                return true;
            }

            scopedCustomers = businessMatches;
        }

        if (candidateKeys.Count == 0)
            return false;

        var nameMatches = scopedCustomers
            .Where(customer => CustomerMatchesRentalNames(customer, candidateKeys))
            .Select(customer => customer.Id)
            .Distinct()
            .ToList();
        if (nameMatches.Count != 1)
            return false;

        customerId = nameMatches[0];
        return true;
    }

    private static bool CustomerReferenceLooksValid(
        Guid? customerId,
        IReadOnlyDictionary<Guid, Customer> customerById,
        IReadOnlyCollection<string> candidateKeys,
        string? preferredTenantCode,
        string? preferredResponsibleOfficeCode)
        => HasValidCustomerId(customerId, customerById) &&
           MatchesOperationalCustomerScope(customerById[customerId!.Value], preferredTenantCode, preferredResponsibleOfficeCode) &&
           (candidateKeys.Count == 0 || CustomerMatchesRentalNames(customerById[customerId!.Value], candidateKeys));

    private static bool CustomerMatchesRentalNames(Customer customer, IReadOnlyCollection<string> candidateKeys)
    {
        if (candidateKeys.Count == 0)
            return true;

        var customerKeys = BuildRentalCustomerKeys(customer.NameOriginal);
        return customerKeys.Any(customerKey =>
            candidateKeys.Any(candidateKey =>
                !string.IsNullOrWhiteSpace(customerKey) &&
                !string.IsNullOrWhiteSpace(candidateKey) &&
                string.Equals(customerKey, candidateKey, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool ProfileMatchesAssetItem(RentalBillingProfile profile, string normalizedItemKey)
    {
        var profileItemKey = RentalCatalogValueNormalizer.NormalizeLooseKey(profile.ItemName);
        if (string.IsNullOrWhiteSpace(profileItemKey) || string.IsNullOrWhiteSpace(normalizedItemKey))
            return false;

        return string.Equals(profileItemKey, normalizedItemKey, StringComparison.OrdinalIgnoreCase)
               || profileItemKey.Contains(normalizedItemKey, StringComparison.OrdinalIgnoreCase)
               || normalizedItemKey.Contains(profileItemKey, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ProfileMatchesAssetSite(RentalBillingProfile profile, IReadOnlyCollection<string> siteKeys)
    {
        if (siteKeys.Count == 0)
            return false;

        var profileSiteKey = RentalCatalogValueNormalizer.NormalizeLooseKey(profile.InstallSiteName);
        return !string.IsNullOrWhiteSpace(profileSiteKey) &&
               siteKeys.Contains(profileSiteKey, StringComparer.OrdinalIgnoreCase);
    }

    private static List<RentalBillingProfile> FilterProfilesByAssetOffice(
        IReadOnlyCollection<RentalBillingProfile> profiles,
        RentalAsset asset)
    {
        var normalizedOfficeCode = ResolveRentalOperationalOfficeCode(
            string.IsNullOrWhiteSpace(asset.ResponsibleOfficeCode)
                ? asset.ManagementCompanyCode
                : asset.ResponsibleOfficeCode,
            asset.OfficeCode,
            asset.ManagementCompanyCode);
        var normalizedTenantCode = NormalizeOperationalTenantCode(
            asset.TenantCode,
            ResolveOperationalOwnerOfficeCode(asset.OfficeCode, normalizedOfficeCode, asset.ManagementCompanyCode, OfficeCodeCatalog.Usenet),
            normalizedOfficeCode);
        if (string.IsNullOrWhiteSpace(normalizedOfficeCode))
            return [];

        var exactScopeMatches = profiles
            .Where(profile => ProfileMatchesAssetScope(profile, normalizedTenantCode, normalizedOfficeCode, asset.CustomerId, []))
            .ToList();
        if (exactScopeMatches.Count > 0)
            return exactScopeMatches;

        return profiles
            .Where(profile =>
                string.Equals(
                    ResolveRentalOperationalOfficeCode(
                        string.IsNullOrWhiteSpace(profile.ResponsibleOfficeCode)
                            ? profile.ManagementCompanyCode
                            : profile.ResponsibleOfficeCode,
                        profile.OfficeCode,
                        profile.ManagementCompanyCode),
                    normalizedOfficeCode,
                    StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static bool ProfileMatchesAssetCustomer(
        RentalBillingProfile profile,
        Guid? assetCustomerId,
        IReadOnlyCollection<string> assetCustomerKeys)
    {
        if (assetCustomerId.HasValue &&
            assetCustomerId.Value != Guid.Empty &&
            profile.CustomerId == assetCustomerId)
        {
            return true;
        }

        return ProfileMatchesRentalNames(profile, assetCustomerKeys);
    }

    private static bool ProfileMatchesRentalNames(
        RentalBillingProfile profile,
        IReadOnlyCollection<string> candidateKeys)
    {
        if (candidateKeys.Count == 0)
            return false;

        var profileKeys = BuildRentalCustomerKeys(profile.CustomerName);
        return profileKeys.Any(profileKey =>
            candidateKeys.Any(candidateKey =>
                !string.IsNullOrWhiteSpace(profileKey) &&
                !string.IsNullOrWhiteSpace(candidateKey) &&
                string.Equals(profileKey, candidateKey, StringComparison.OrdinalIgnoreCase)));
    }

    private static Guid? SelectPreferredBillingProfile(
        IReadOnlyList<RentalBillingProfile> profiles,
        IReadOnlyDictionary<Guid, int> activeProfileAssetCounts,
        IReadOnlyCollection<string> siteKeys)
    {
        if (profiles.Count == 0)
            return null;
        if (profiles.Count == 1)
            return profiles[0].Id;

        var rankedProfiles = profiles
            .Select(profile => new
            {
                Profile = profile,
                SiteMatch = siteKeys.Count > 0 && ProfileMatchesAssetSite(profile, siteKeys) ? 1 : 0,
                LinkedAssetCount = activeProfileAssetCounts.TryGetValue(profile.Id, out var count) ? count : 0,
                HasInstallSite = string.IsNullOrWhiteSpace(RentalCatalogValueNormalizer.NormalizeDisplayText(profile.InstallSiteName)) ? 0 : 1
            })
            .OrderByDescending(entry => entry.SiteMatch)
            .ThenByDescending(entry => entry.LinkedAssetCount)
            .ThenByDescending(entry => entry.HasInstallSite)
            .ThenBy(entry => entry.Profile.InstallSiteName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(entry => entry.Profile.Id)
            .ToList();

        var best = rankedProfiles[0];
        var bestCount = rankedProfiles.Count(entry =>
            entry.SiteMatch == best.SiteMatch &&
            entry.LinkedAssetCount == best.LinkedAssetCount &&
            entry.HasInstallSite == best.HasInstallSite);

        return bestCount == 1 ? best.Profile.Id : null;
    }

    private static void RegisterProfileAssetLink(IDictionary<Guid, int> activeProfileAssetCounts, Guid profileId)
    {
        if (profileId == Guid.Empty)
            return;

        activeProfileAssetCounts[profileId] = activeProfileAssetCounts.TryGetValue(profileId, out var currentCount)
            ? currentCount + 1
            : 1;
    }

    private static void UnregisterProfileAssetLink(IDictionary<Guid, int> activeProfileAssetCounts, Guid profileId)
    {
        if (profileId == Guid.Empty)
            return;

        if (!activeProfileAssetCounts.TryGetValue(profileId, out var currentCount))
            return;

        if (currentCount <= 1)
        {
            activeProfileAssetCounts.Remove(profileId);
            return;
        }

        activeProfileAssetCounts[profileId] = currentCount - 1;
    }

    private static IEnumerable<Customer> FilterCustomersByOperationalScope(
        IReadOnlyCollection<Customer> customers,
        string? preferredTenantCode,
        string? preferredResponsibleOfficeCode)
    {
        var exactScopeMatches = customers
            .Where(customer => MatchesOperationalCustomerScope(customer, preferredTenantCode, preferredResponsibleOfficeCode))
            .ToList();
        if (exactScopeMatches.Count > 0)
            return exactScopeMatches;

        if (!string.IsNullOrWhiteSpace(preferredTenantCode))
        {
            var tenantMatches = customers
                .Where(customer =>
                    string.Equals(
                        NormalizeOperationalTenantCode(customer.TenantCode, customer.OfficeCode, customer.ResponsibleOfficeCode),
                        preferredTenantCode,
                        StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (tenantMatches.Count > 0)
                return tenantMatches;
        }

        return customers;
    }

    private static bool MatchesOperationalCustomerScope(
        Customer customer,
        string? preferredTenantCode,
        string? preferredResponsibleOfficeCode)
    {
        var customerTenantCode = NormalizeOperationalTenantCode(customer.TenantCode, customer.OfficeCode, customer.ResponsibleOfficeCode);
        if (!string.IsNullOrWhiteSpace(preferredTenantCode) &&
            !string.Equals(customerTenantCode, preferredTenantCode, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(preferredResponsibleOfficeCode))
            return true;

        var customerResponsibleOfficeCode = ResolveRentalOperationalOfficeCode(
            customer.ResponsibleOfficeCode,
            customer.OfficeCode);
        return string.Equals(customerResponsibleOfficeCode, preferredResponsibleOfficeCode, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ProfileMatchesAssetScope(
        RentalBillingProfile profile,
        string? preferredTenantCode,
        string? preferredResponsibleOfficeCode,
        Guid? assetCustomerId,
        IReadOnlyCollection<string> assetCustomerKeys)
        => MatchesOperationalTenantAndOfficeScope(profile.TenantCode, profile.OfficeCode, profile.ResponsibleOfficeCode, preferredTenantCode, preferredResponsibleOfficeCode) &&
           ProfileMatchesAssetCustomer(profile, assetCustomerId, assetCustomerKeys);

    private static bool MatchesOperationalTenantAndOfficeScope(
        string? tenantCode,
        string? ownerOfficeCode,
        string? responsibleOfficeCode,
        string? preferredTenantCode,
        string? preferredResponsibleOfficeCode)
    {
        var normalizedOfficeCode = ResolveRentalOperationalOfficeCode(responsibleOfficeCode, ownerOfficeCode);
        var normalizedTenantCode = NormalizeOperationalTenantCode(tenantCode, ownerOfficeCode, normalizedOfficeCode);

        return (string.IsNullOrWhiteSpace(preferredTenantCode) ||
                string.Equals(normalizedTenantCode, preferredTenantCode, StringComparison.OrdinalIgnoreCase)) &&
               (string.IsNullOrWhiteSpace(preferredResponsibleOfficeCode) ||
                string.Equals(normalizedOfficeCode, preferredResponsibleOfficeCode, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasValidCustomerId(Guid? customerId, IReadOnlyDictionary<Guid, Customer> customerById)
        => customerId.HasValue &&
           customerId.Value != Guid.Empty &&
           customerById.ContainsKey(customerId.Value);

    private static string ResolveRentalOperationalOfficeCode(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (OfficeCodeCatalog.TryNormalize(candidate, out var normalizedOfficeCode))
                return normalizedOfficeCode;
        }

        return OfficeCodeCatalog.Usenet;
    }

    private static bool TryResolveBillingProfileCustomer(
        IReadOnlyCollection<Customer> customers,
        RentalBillingProfile profile,
        string? preferredTenantCode,
        string? preferredResponsibleOfficeCode,
        out Guid customerId)
    {
        customerId = Guid.Empty;
        var candidateKeys = BuildRentalCustomerKeys(profile.CustomerName);
        return TryResolveRentalCustomerByNames(
            customers,
            profile.BusinessNumber,
            candidateKeys,
            preferredTenantCode,
            preferredResponsibleOfficeCode,
            out customerId);
    }

    private static string ResolveCustomerRentalOfficeCode(string? officeCode)
        => OfficeCodeCatalog.NormalizeOfficeCodeLoose(officeCode, null, OfficeCodeCatalog.Usenet);

}
