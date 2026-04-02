using Microsoft.EntityFrameworkCore;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Server.Api.Data;

public static partial class DbInitializer
{
    private const string NoLinkedAssetFollowUpNote = "연결 자산 없음";
    private const string CanonicalMfcL8900CategoryName = "A4컬러복합기";
    private const string CanonicalMfcL8900ItemName = "MFC-L8900CDW";
    private const string BillingStatusOnHold = "보류";
    private const string CompletionPending = "미완료";

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

        foreach (var asset in assets.Where(asset => !asset.IsDeleted))
        {
            var changed = false;
            var assetCustomerKeys = BuildRentalCustomerKeys(
                asset.CustomerName,
                asset.CurrentCustomerName,
                asset.BillToCustomerName);
            if (asset.BillingProfileId.HasValue &&
                (!activeProfilesById.ContainsKey(asset.BillingProfileId.Value) || asset.BillingProfileId.Value == Guid.Empty))
            {
                asset.BillingProfileId = null;
                changed = true;
            }

            activeProfilesById.TryGetValue(asset.BillingProfileId ?? Guid.Empty, out var linkedProfile);

            var resolvedCustomerId = ResolveAssetCustomerId(asset, linkedProfile, customerById, customers);
            if (asset.CustomerId != resolvedCustomerId)
            {
                asset.CustomerId = resolvedCustomerId;
                changed = true;
            }

            if (asset.CustomerId.HasValue &&
                customerById.TryGetValue(asset.CustomerId.Value, out var linkedCustomer))
            {
                var resolvedOfficeCode = ResolveCustomerRentalOfficeCode(linkedCustomer.OfficeCode);
                if (!string.IsNullOrWhiteSpace(resolvedOfficeCode))
                {
                    if (!string.Equals(asset.OfficeCode, resolvedOfficeCode, StringComparison.OrdinalIgnoreCase))
                    {
                        asset.OfficeCode = resolvedOfficeCode;
                        changed = true;
                    }

                    if (!string.Equals(asset.ManagementCompanyCode, resolvedOfficeCode, StringComparison.OrdinalIgnoreCase))
                    {
                        asset.ManagementCompanyCode = resolvedOfficeCode;
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

                if (string.IsNullOrWhiteSpace(asset.BillToCustomerName))
                {
                    asset.BillToCustomerName = RentalCatalogValueNormalizer.NormalizeDisplayText(linkedCustomer.NameOriginal);
                    changed = true;
                }
            }

            if (!asset.BillingProfileId.HasValue || asset.BillingProfileId.Value == Guid.Empty)
            {
                var resolvedBillingProfileId = ResolveAssetBillingProfileId(asset, profiles);
                if (resolvedBillingProfileId.HasValue && resolvedBillingProfileId.Value != Guid.Empty)
                {
                    asset.BillingProfileId = resolvedBillingProfileId.Value;
                    changed = true;
                    activeProfilesById.TryGetValue(resolvedBillingProfileId.Value, out linkedProfile);
                }
            }

            if (linkedProfile is not null &&
                linkedProfile.CustomerId.HasValue &&
                linkedProfile.CustomerId.Value != Guid.Empty &&
                CustomerReferenceLooksValid(linkedProfile.CustomerId, customerById, assetCustomerKeys) &&
                asset.CustomerId != linkedProfile.CustomerId)
            {
                asset.CustomerId = linkedProfile.CustomerId;
                changed = true;
            }

            if (linkedProfile is not null &&
                CustomerReferenceLooksValid(linkedProfile.CustomerId, customerById, assetCustomerKeys) &&
                customerById.TryGetValue(linkedProfile.CustomerId ?? Guid.Empty, out var profileCustomer))
            {
                var resolvedOfficeCode = ResolveCustomerRentalOfficeCode(profileCustomer.OfficeCode);
                if (!string.IsNullOrWhiteSpace(resolvedOfficeCode))
                {
                    if (!string.Equals(asset.OfficeCode, resolvedOfficeCode, StringComparison.OrdinalIgnoreCase))
                    {
                        asset.OfficeCode = resolvedOfficeCode;
                        changed = true;
                    }

                    if (!string.Equals(asset.ManagementCompanyCode, resolvedOfficeCode, StringComparison.OrdinalIgnoreCase))
                    {
                        asset.ManagementCompanyCode = resolvedOfficeCode;
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

                if (!string.Equals(profile.RealCustomerName, normalizedMasterName, StringComparison.Ordinal))
                {
                    profile.RealCustomerName = normalizedMasterName;
                    changed = true;
                }

                if (!string.Equals(profile.BillToCustomerName, normalizedMasterName, StringComparison.Ordinal))
                {
                    profile.BillToCustomerName = normalizedMasterName;
                    changed = true;
                }

                var resolvedOfficeCode = ResolveCustomerRentalOfficeCode(linkedCustomer.OfficeCode);
                if (!string.IsNullOrWhiteSpace(resolvedOfficeCode))
                {
                    if (!string.Equals(profile.OfficeCode, resolvedOfficeCode, StringComparison.OrdinalIgnoreCase))
                    {
                        profile.OfficeCode = resolvedOfficeCode;
                        changed = true;
                    }

                    if (!string.Equals(profile.ManagementCompanyCode, resolvedOfficeCode, StringComparison.OrdinalIgnoreCase))
                    {
                        profile.ManagementCompanyCode = resolvedOfficeCode;
                        changed = true;
                    }
                }
            }

            var hasLinkedAssets = activeAssetsByProfileId.ContainsKey(profile.Id);
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

                var followUpNote = AppendFollowUpNote(profile.FollowUpNote, NoLinkedAssetFollowUpNote);
                if (!string.Equals(profile.FollowUpNote, followUpNote, StringComparison.Ordinal))
                {
                    profile.FollowUpNote = followUpNote;
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
            if (!activeProfilesByIdAfterRepair.TryGetValue(log.BillingProfileId, out var profile))
                continue;

            if (string.Equals(log.OfficeCode, profile.OfficeCode, StringComparison.OrdinalIgnoreCase))
                continue;

            log.OfficeCode = profile.OfficeCode;
            TouchTrackedEntity(log, now);
        }
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
            profile.CustomerName,
            profile.RealCustomerName,
            profile.BillToCustomerName);

        if (CustomerReferenceLooksValid(profile.CustomerId, customerById, candidateKeys))
            return profile.CustomerId;

        var linkedAssetCustomerIds = assets
            .Where(asset => !asset.IsDeleted && asset.BillingProfileId == profile.Id)
            .Select(asset => asset.CustomerId)
            .Where(customerId => CustomerReferenceLooksValid(customerId, customerById, candidateKeys))
            .Select(customerId => customerId!.Value)
            .Distinct()
            .ToList();
        if (linkedAssetCustomerIds.Count == 1)
            return linkedAssetCustomerIds[0];

        return TryResolveRentalCustomerByNames(
                   customers,
                   profile.BusinessNumber,
                   candidateKeys,
                   out var resolvedCustomerId)
               || TryResolveBillingProfileCustomer(customers, profile, out resolvedCustomerId)
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
            asset.CurrentCustomerName,
            asset.BillToCustomerName);

        if (linkedProfile is not null &&
            CustomerReferenceLooksValid(linkedProfile.CustomerId, customerById, candidateKeys))
            return linkedProfile.CustomerId;

        if (CustomerReferenceLooksValid(asset.CustomerId, customerById, candidateKeys))
            return asset.CustomerId;

        return TryResolveRentalCustomerByNames(
                   customers,
                   null,
                   candidateKeys,
                   out var resolvedCustomerId)
               || TryResolveAssetCustomer(customers, asset, out resolvedCustomerId)
            ? resolvedCustomerId
            : null;
    }

    private static Guid? ResolveAssetBillingProfileId(
        RentalAsset asset,
        IReadOnlyCollection<RentalBillingProfile> profiles)
    {
        if (!asset.CustomerId.HasValue || asset.CustomerId.Value == Guid.Empty)
            return null;

        var customerProfiles = profiles
            .Where(profile => !profile.IsDeleted && profile.CustomerId == asset.CustomerId)
            .ToList();
        if (customerProfiles.Count == 0)
            return null;

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

        return null;
    }

    private static bool TryResolveAssetCustomer(
        IReadOnlyCollection<Customer> customers,
        RentalAsset asset,
        out Guid customerId)
    {
        customerId = Guid.Empty;
        var candidateKeys = BuildRentalCustomerKeys(
            asset.CustomerName,
            asset.CurrentCustomerName,
            asset.BillToCustomerName);
        return TryResolveRentalCustomerByNames(customers, null, candidateKeys, out customerId);
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
        out Guid customerId)
    {
        customerId = Guid.Empty;
        var normalizedBusinessNumber = NormalizeBusinessNumber(businessNumber);

        var scopedCustomers = customers.AsEnumerable();
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
        IReadOnlyCollection<string> candidateKeys)
        => HasValidCustomerId(customerId, customerById) &&
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

    private static bool HasValidCustomerId(Guid? customerId, IReadOnlyDictionary<Guid, Customer> customerById)
        => customerId.HasValue &&
           customerId.Value != Guid.Empty &&
           customerById.ContainsKey(customerId.Value);

    private static string ResolveCustomerRentalOfficeCode(string? officeCode)
        => OfficeCodeCatalog.NormalizeOfficeCodeLoose(officeCode, null, OfficeCodeCatalog.Usenet);

    private static string AppendFollowUpNote(string? currentNote, string token)
    {
        var trimmedCurrent = (currentNote ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(token))
            return trimmedCurrent;

        if (trimmedCurrent.Contains(token, StringComparison.OrdinalIgnoreCase))
            return trimmedCurrent;

        return string.IsNullOrWhiteSpace(trimmedCurrent)
            ? token
            : $"{trimmedCurrent} / {token}";
    }
}
