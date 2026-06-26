using System.Linq;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Data;

public static partial class LocalDbInitializer
{
    private const string CanonicalMfcL8900CategoryName = "A4컬러복합기";
    private const string CanonicalMfcL8900ItemName = "MFC-L8900CDW";
    private static async Task RepairRentalCustomerLinkageAsync(LocalDbContext db)
    {
        var now = DateTime.UtcNow;
        var customers = await db.Customers.IgnoreQueryFilters()
            .Where(customer => !customer.IsDeleted)
            .ToListAsync();
        var customerById = customers.ToDictionary(customer => customer.Id);

        var profiles = await db.RentalBillingProfiles.IgnoreQueryFilters().ToListAsync();
        var assets = await db.RentalAssets.IgnoreQueryFilters().ToListAsync();
        var billingLogs = await db.RentalBillingLogs.IgnoreQueryFilters().ToListAsync();

        await RepairMfcL8900CategoryAsync(db, now);

        foreach (var profile in profiles.Where(profile => !profile.IsDeleted))
        {
            if (NormalizeBillingProfileScope(profile))
                MarkStartupMaintenanceChange(profile, now);
        }

        foreach (var asset in assets.Where(asset => !asset.IsDeleted))
        {
            if (NormalizeRentalAssetScope(asset))
                MarkStartupMaintenanceChange(asset, now);
        }

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
                MarkStartupMaintenanceChange(profile, now);
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
                DomainConstants.OfficeUsenet);
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

            var importedManagementNumber = ExtractImportedAssetNoteValue(asset.Notes, "원본 관리번호");
            if (!string.IsNullOrWhiteSpace(importedManagementNumber) &&
                !string.Equals(asset.ManagementNumber, importedManagementNumber, StringComparison.OrdinalIgnoreCase) &&
                !assets.Any(other =>
                    other.Id != asset.Id &&
                    string.Equals((other.ManagementNumber ?? string.Empty).Trim(), importedManagementNumber, StringComparison.OrdinalIgnoreCase)))
            {
                asset.ManagementNumber = importedManagementNumber;
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
                var linkedCustomerMatchesScope = MatchesOperationalCustomerScope(
                    linkedCustomer,
                    preferredTenantCode,
                    preferredResponsibleOfficeCode) ||
                    CustomerMatchesRentalNames(linkedCustomer, assetCustomerKeys);
                if (linkedCustomerMatchesScope)
                {
                    var normalizedMasterName = RentalCatalogValueNormalizer.NormalizeDisplayText(linkedCustomer.NameOriginal);
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

                    if (!string.Equals(asset.CustomerName, normalizedMasterName, StringComparison.Ordinal))
                    {
                        asset.CustomerName = normalizedMasterName;
                        changed = true;
                    }

                    if (!string.Equals(asset.CurrentCustomerName, normalizedMasterName, StringComparison.Ordinal))
                    {
                        asset.CurrentCustomerName = normalizedMasterName;
                        changed = true;
                    }
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

            if (linkedProfile is not null)
            {
                var normalizedProfileCustomerName = RentalCatalogValueNormalizer.NormalizeDisplayText(linkedProfile.CustomerName);
                if (!string.IsNullOrWhiteSpace(normalizedProfileCustomerName))
                {
                    if (!string.Equals(asset.CustomerName, normalizedProfileCustomerName, StringComparison.Ordinal))
                    {
                        asset.CustomerName = normalizedProfileCustomerName;
                        changed = true;
                    }

                    if (!string.Equals(asset.CurrentCustomerName, normalizedProfileCustomerName, StringComparison.Ordinal))
                    {
                        asset.CurrentCustomerName = normalizedProfileCustomerName;
                        changed = true;
                    }
                }
            }

            if (changed)
                MarkStartupMaintenanceChange(asset, now);
        }

        var activeAssetsByProfileId = assets
            .Where(asset => !asset.IsDeleted && asset.BillingProfileId.HasValue && asset.BillingProfileId.Value != Guid.Empty)
            .GroupBy(asset => asset.BillingProfileId!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());
        var rentalStateService = new RentalStateService(db);

        foreach (var profile in profiles.Where(profile => !profile.IsDeleted))
        {
            var changed = false;
            var resolvedCustomerId = ResolveProfileCustomerId(profile, assets, customerById, customers);
            var preferredResponsibleOfficeCode = ResolveRentalOperationalOfficeCode(
                profile.ResponsibleOfficeCode,
                profile.OfficeCode,
                profile.ManagementCompanyCode);
            var preferredOwnerOfficeCode = ResolveOperationalOwnerOfficeCode(
                profile.OfficeCode,
                preferredResponsibleOfficeCode,
                profile.ManagementCompanyCode,
                DomainConstants.OfficeUsenet);
            var preferredTenantCode = NormalizeOperationalTenantCode(
                profile.TenantCode,
                preferredOwnerOfficeCode,
                preferredResponsibleOfficeCode);
            if (profile.CustomerId != resolvedCustomerId)
            {
                profile.CustomerId = resolvedCustomerId;
                changed = true;
            }

            if (profile.CustomerId.HasValue &&
                customerById.TryGetValue(profile.CustomerId.Value, out var linkedCustomer))
            {
                var profileCustomerKeys = BuildRentalCustomerKeys(profile.CustomerName);
                var linkedCustomerMatchesScope = MatchesOperationalCustomerScope(
                    linkedCustomer,
                    preferredTenantCode,
                    preferredResponsibleOfficeCode) ||
                    CustomerMatchesRentalNames(linkedCustomer, profileCustomerKeys) ||
                    ProfileHasUniqueLinkedAssetCustomer(profile, assets, customerById, linkedCustomer.Id);
                if (linkedCustomerMatchesScope)
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
            var billingTemplateAssets = ResolveBillingTemplateAssets(profile, assets, rentalStateService);
            if (hasLinkedAssets)
            {
                var billingTemplateAssetsById = billingTemplateAssets
                    .Where(asset => asset.Id != Guid.Empty)
                    .GroupBy(asset => asset.Id)
                    .ToDictionary(group => group.Key, group => group.First());
                foreach (var linkedAsset in linkedAssets!)
                {
                    if (linkedAsset.Id != Guid.Empty)
                        billingTemplateAssetsById[linkedAsset.Id] = linkedAsset;
                }

                billingTemplateAssets = billingTemplateAssetsById.Values.ToList();
            }

            if (billingTemplateAssets.Count > 0 &&
                TryNormalizeBillingTemplateFromLinkedAssets(profile, billingTemplateAssets, rentalStateService, out var normalizedTemplateJson, out var normalizedMonthlyAmount))
            {
                if (!string.Equals(profile.BillingTemplateJson ?? string.Empty, normalizedTemplateJson, StringComparison.Ordinal))
                {
                    profile.BillingTemplateJson = normalizedTemplateJson;
                    changed = true;
                }

                if (profile.MonthlyAmount != normalizedMonthlyAmount)
                {
                    profile.MonthlyAmount = normalizedMonthlyAmount;
                    changed = true;
                }
            }

            if (!hasLinkedAssets)
            {
                var normalizedBillingStatus = PaymentFlowConstants.NormalizeBillingStatus(profile.BillingStatus);
                if (!string.Equals(normalizedBillingStatus, PaymentFlowConstants.BillingStatusOnHold, StringComparison.Ordinal))
                {
                    profile.BillingStatus = PaymentFlowConstants.BillingStatusOnHold;
                    changed = true;
                }

                var normalizedCompletionStatus = PaymentFlowConstants.NormalizeCompletionStatus(profile.CompletionStatus);
                if (!string.Equals(normalizedCompletionStatus, PaymentFlowConstants.CompletionPending, StringComparison.Ordinal))
                {
                    profile.CompletionStatus = PaymentFlowConstants.CompletionPending;
                    changed = true;
                }

                if (!profile.RequiresFollowUp)
                {
                    profile.RequiresFollowUp = true;
                    changed = true;
                }

            }

            if (changed)
                MarkStartupMaintenanceChange(profile, now);
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
                    MarkStartupMaintenanceChange(log, now);

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

            if (!string.Equals(log.TenantCode, profile.TenantCode, StringComparison.OrdinalIgnoreCase))
            {
                log.TenantCode = profile.TenantCode;
                changed = true;
            }

            if (changed)
                MarkStartupMaintenanceChange(log, now);
        }
    }

    private static List<LocalRentalAsset> ResolveBillingTemplateAssets(
        LocalRentalBillingProfile profile,
        IReadOnlyCollection<LocalRentalAsset> assets,
        RentalStateService rentalStateService)
    {
        var templateItems = rentalStateService.GetBillingTemplateItems(profile, Array.Empty<LocalRentalAsset>());
        if (templateItems.Count == 0)
            return [];

        var includedIds = templateItems
            .SelectMany(item => item.IncludedAssetIds ?? [])
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (includedIds.Count == 0)
            return [];

        var assetMap = assets
            .Where(asset => asset.Id != Guid.Empty && !asset.IsDeleted)
            .GroupBy(asset => asset.Id)
            .ToDictionary(group => group.Key, group => group.First());

        return includedIds
            .Where(assetMap.ContainsKey)
            .Select(id => assetMap[id])
            .ToList();
    }

    private static bool TryNormalizeBillingTemplateFromLinkedAssets(
        LocalRentalBillingProfile profile,
        IReadOnlyList<LocalRentalAsset> linkedAssets,
        RentalStateService rentalStateService,
        out string normalizedTemplateJson,
        out decimal normalizedMonthlyAmount)
    {
        normalizedTemplateJson = profile.BillingTemplateJson ?? string.Empty;
        normalizedMonthlyAmount = Math.Max(0m, profile.MonthlyAmount);
        if (linkedAssets.Count == 0)
            return false;

        var linkedAssetMap = linkedAssets
            .Where(asset => asset.Id != Guid.Empty && !asset.IsDeleted)
            .GroupBy(asset => asset.Id)
            .ToDictionary(group => group.Key, group => group.First());
        var linkedAssetIds = linkedAssetMap.Keys
            .OrderBy(id => id)
            .ToList();
        if (linkedAssetIds.Count == 0)
            return false;

        var rawTemplateItems = ParseStartupBillingTemplateItems(profile.BillingTemplateJson);
        var hasExplicitIncludedAssetIds = rawTemplateItems?.Any(item =>
            (item.IncludedAssetIds ?? []).Any(id => id != Guid.Empty)) == true;

        var templateItems = rentalStateService.GetBillingTemplateItems(profile, linkedAssets);
        if (templateItems.Count == 0)
            return false;

        if (!hasExplicitIncludedAssetIds && templateItems.Count == 1)
        {
            var currentIds = (templateItems[0].IncludedAssetIds ?? [])
                .Where(id => id != Guid.Empty)
                .Distinct()
                .OrderBy(id => id)
                .ToList();
            if (!linkedAssetIds.SequenceEqual(currentIds))
                templateItems[0].IncludedAssetIds = linkedAssetIds;
        }

        var changed = false;
        foreach (var templateItem in templateItems)
        {
            var includedAssetIds = (templateItem.IncludedAssetIds ?? [])
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();
            if (includedAssetIds.Count == 0)
                continue;

            var itemAssets = includedAssetIds
                .Where(linkedAssetMap.ContainsKey)
                .Select(id => linkedAssetMap[id])
                .ToList();
            if (itemAssets.Count == 0)
                continue;

            var totalMonthlyFee = itemAssets.Sum(asset => Math.Max(0m, asset.MonthlyFee));
            if (totalMonthlyFee <= 0m)
                continue;

            var distinctPositiveFees = itemAssets
                .Select(asset => Math.Max(0m, asset.MonthlyFee))
                .Where(amount => amount > 0m)
                .Distinct()
                .ToList();
            var lineMode = FirstNonEmptyForStartup(templateItem.BillingLineMode, profile.BillingType);
            var shouldBundle = itemAssets.Count == 1 ||
                               string.Equals(lineMode, "\uBB36\uC74C", StringComparison.OrdinalIgnoreCase) ||
                               distinctPositiveFees.Count != 1;
            var quantity = shouldBundle ? 1m : itemAssets.Count;
            var unitPrice = shouldBundle ? totalMonthlyFee : distinctPositiveFees[0];
            var amount = CalculateStartupTemplateLineAmount(quantity, unitPrice);

            if (templateItem.Quantity != quantity ||
                templateItem.UnitPrice != unitPrice ||
                templateItem.Amount != amount)
            {
                templateItem.Quantity = quantity;
                templateItem.UnitPrice = unitPrice;
                templateItem.Amount = amount;
                changed = true;
            }
        }

        normalizedMonthlyAmount = templateItems.Sum(ResolveStartupTemplateMonthlyAmount);
        var serialized = rentalStateService.SerializeBillingTemplateItems(templateItems);
        if (!string.Equals(serialized, profile.BillingTemplateJson ?? string.Empty, StringComparison.Ordinal))
            changed = true;

        normalizedTemplateJson = serialized;
        return changed || profile.MonthlyAmount != normalizedMonthlyAmount;
    }

    private static List<RentalBillingTemplateItemModel>? ParseStartupBillingTemplateItems(string? billingTemplateJson)
    {
        if (string.IsNullOrWhiteSpace(billingTemplateJson))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<RentalBillingTemplateItemModel>>(billingTemplateJson) ?? [];
        }
        catch
        {
            return null;
        }
    }

    private static decimal ResolveStartupTemplateMonthlyAmount(RentalBillingTemplateItemModel item)
    {
        var quantity = item.Quantity <= 0m ? 1m : item.Quantity;
        var unitPrice = Math.Max(0m, item.UnitPrice);
        var calculated = CalculateStartupTemplateLineAmount(quantity, unitPrice);
        return calculated > 0m ? calculated : Math.Max(0m, item.Amount);
    }

    private static decimal CalculateStartupTemplateLineAmount(decimal quantity, decimal unitPrice)
        => Math.Max(0m, quantity) * Math.Max(0m, unitPrice);

    private static string FirstNonEmptyForStartup(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return string.Empty;
    }

    private static async Task RepairMfcL8900CategoryAsync(LocalDbContext db, DateTime now)
    {
        var items = await db.Items.IgnoreQueryFilters()
            .Where(item => !item.IsDeleted)
            .Where(item => item.NameOriginal == CanonicalMfcL8900ItemName)
            .ToListAsync();
        if (items.Count == 0)
            return;

        var categoryChanged = false;
        foreach (var item in items)
        {
            if (string.Equals(item.CategoryName, CanonicalMfcL8900CategoryName, StringComparison.Ordinal))
                continue;

            item.CategoryName = CanonicalMfcL8900CategoryName;
            MarkStartupMaintenanceChange(item, now);
            categoryChanged = true;
        }

        if (categoryChanged || items.Count > 1)
            await MergeDuplicateItemsAsync(db);
    }

    private static Guid? ResolveProfileCustomerId(
        LocalRentalBillingProfile profile,
        IReadOnlyCollection<LocalRentalAsset> assets,
        IReadOnlyDictionary<Guid, LocalCustomer> customerById,
        IReadOnlyCollection<LocalCustomer> customers)
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
            DomainConstants.OfficeUsenet);
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

        if (TryResolveProfileCustomerFromLinkedAssets(profile, assets, customerById, out var linkedAssetResolvedCustomerId))
            return linkedAssetResolvedCustomerId;

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
        LocalRentalAsset asset,
        LocalRentalBillingProfile? linkedProfile,
        IReadOnlyDictionary<Guid, LocalCustomer> customerById,
        IReadOnlyCollection<LocalCustomer> customers)
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
            DomainConstants.OfficeUsenet);
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
        LocalRentalAsset asset,
        IReadOnlyCollection<LocalRentalBillingProfile> profiles,
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

    private static List<LocalRentalBillingProfile> FilterProfilesByAssetOffice(
        IReadOnlyCollection<LocalRentalBillingProfile> profiles,
        LocalRentalAsset asset)
    {
        var normalizedOfficeCode = ResolveRentalOperationalOfficeCode(
            string.IsNullOrWhiteSpace(asset.ResponsibleOfficeCode)
                ? asset.ManagementCompanyCode
                : asset.ResponsibleOfficeCode,
            asset.OfficeCode,
            asset.ManagementCompanyCode);
        var normalizedTenantCode = NormalizeOperationalTenantCode(
            asset.TenantCode,
            ResolveOperationalOwnerOfficeCode(asset.OfficeCode, normalizedOfficeCode, asset.ManagementCompanyCode, DomainConstants.OfficeUsenet),
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
        LocalRentalBillingProfile profile,
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
        LocalRentalBillingProfile profile,
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
        IReadOnlyList<LocalRentalBillingProfile> profiles,
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

        if (bestCount > 1)
        {
            var duplicateEquivalentProfiles = profiles
                .Select(profile => new
                {
                    Profile = profile,
                    SiteKey = RentalCatalogValueNormalizer.NormalizeLooseKey(profile.InstallSiteName),
                    CycleMonths = profile.BillingCycleMonths,
                    OfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeLoose(
                        string.IsNullOrWhiteSpace(profile.ResponsibleOfficeCode)
                            ? profile.ManagementCompanyCode
                            : profile.ResponsibleOfficeCode,
                        null,
                        string.Empty)
                })
                .ToList();

            if (duplicateEquivalentProfiles
                .Select(entry => $"{entry.SiteKey}|{entry.CycleMonths}|{entry.OfficeCode}")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() == 1)
            {
                return duplicateEquivalentProfiles
                    .OrderByDescending(entry => activeProfileAssetCounts.TryGetValue(entry.Profile.Id, out var count) ? count : 0)
                    .ThenBy(entry => entry.Profile.Id)
                    .Select(entry => (Guid?)entry.Profile.Id)
                    .FirstOrDefault();
            }
        }

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

    private static bool TryResolveProfileCustomerFromLinkedAssets(
        LocalRentalBillingProfile profile,
        IReadOnlyCollection<LocalRentalAsset> assets,
        IReadOnlyDictionary<Guid, LocalCustomer> customerById,
        out Guid customerId)
    {
        customerId = Guid.Empty;
        var linkedAssets = ResolveProfileLinkedOrTemplateAssets(profile, assets);
        var customerIds = linkedAssets
            .Select(asset => asset.CustomerId)
            .Where(customerId => HasValidCustomerId(customerId, customerById))
            .Select(customerId => customerId!.Value)
            .Distinct()
            .ToList();
        if (customerIds.Count != 1)
            return false;

        var candidateCustomerId = customerIds[0];
        if (!LinkedAssetsMatchCustomerScope(linkedAssets, customerById[candidateCustomerId], candidateCustomerId))
            return false;

        customerId = candidateCustomerId;
        return true;
    }

    private static bool ProfileHasUniqueLinkedAssetCustomer(
        LocalRentalBillingProfile profile,
        IReadOnlyCollection<LocalRentalAsset> assets,
        IReadOnlyDictionary<Guid, LocalCustomer> customerById,
        Guid customerId)
        => TryResolveProfileCustomerFromLinkedAssets(profile, assets, customerById, out var resolvedCustomerId) &&
           resolvedCustomerId == customerId;

    private static List<LocalRentalAsset> ResolveProfileLinkedOrTemplateAssets(
        LocalRentalBillingProfile profile,
        IReadOnlyCollection<LocalRentalAsset> assets)
    {
        var templateAssetIds = ParseStartupBillingTemplateItems(profile.BillingTemplateJson)
            ?.SelectMany(item => item.IncludedAssetIds ?? [])
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToHashSet() ?? [];

        return assets
            .Where(asset => !asset.IsDeleted && asset.Id != Guid.Empty)
            .Where(asset => asset.BillingProfileId == profile.Id || templateAssetIds.Contains(asset.Id))
            .GroupBy(asset => asset.Id)
            .Select(group => group.First())
            .ToList();
    }

    private static bool LinkedAssetsMatchCustomerScope(
        IReadOnlyCollection<LocalRentalAsset> linkedAssets,
        LocalCustomer customer,
        Guid customerId)
    {
        var matchingAssets = linkedAssets
            .Where(asset => asset.CustomerId == customerId)
            .ToList();
        if (matchingAssets.Count == 0)
            return false;

        return matchingAssets.All(asset =>
        {
            var assetResponsibleOfficeCode = ResolveRentalOperationalOfficeCode(
                asset.ResponsibleOfficeCode,
                asset.OfficeCode,
                asset.ManagementCompanyCode);
            var assetOwnerOfficeCode = ResolveOperationalOwnerOfficeCode(
                asset.OfficeCode,
                assetResponsibleOfficeCode,
                asset.ManagementCompanyCode,
                OfficeCodeCatalog.Usenet);
            var assetTenantCode = NormalizeOperationalTenantCode(
                asset.TenantCode,
                assetOwnerOfficeCode,
                assetResponsibleOfficeCode);
            return MatchesOperationalCustomerScope(customer, assetTenantCode, assetResponsibleOfficeCode);
        });
    }

    private static bool TryResolveAssetCustomer(
        IReadOnlyCollection<LocalCustomer> customers,
        LocalRentalAsset asset,
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

        foreach (var alias in EnumerateRentalPublicOfficeAliases(prefix, suffix))
            yield return alias;
    }

    private static IEnumerable<string> EnumerateRentalPublicOfficeAliases(string prefix, string suffix)
    {
        var normalizedPrefix = RentalCatalogValueNormalizer.NormalizeLooseKey(prefix);
        var normalizedSuffix = RentalCatalogValueNormalizer.NormalizeDisplayText(suffix);
        if (string.IsNullOrWhiteSpace(normalizedPrefix) || string.IsNullOrWhiteSpace(normalizedSuffix))
            yield break;

        if (string.Equals(normalizedPrefix, RentalCatalogValueNormalizer.NormalizeLooseKey("\uC0C1\uC218\uB3C4\uC0AC\uC5C5\uC18C"), StringComparison.OrdinalIgnoreCase))
        {
            yield return "\uC0C1\uC218\uB3C4\uC0AC\uC5C5\uBCF8\uBD80" + normalizedSuffix;
            yield return "[\uC0C1\uC218\uB3C4\uC0AC\uC5C5\uBCF8\uBD80]" + normalizedSuffix;
        }

        if (string.Equals(normalizedPrefix, RentalCatalogValueNormalizer.NormalizeLooseKey("\uC5F0\uC218\uAD6C"), StringComparison.OrdinalIgnoreCase))
        {
            var departmentName = TryExtractPublicHealthCenterDepartmentName(normalizedSuffix);
            if (!string.IsNullOrWhiteSpace(departmentName))
            {
                yield return "\uC5F0\uC218\uAD6C\uCCAD[" + departmentName + "]";
                yield return "\uC5F0\uC218\uAD6C\uCCAD" + departmentName;
            }
        }
    }

    private static string TryExtractPublicHealthCenterDepartmentName(string value)
    {
        var normalized = RentalCatalogValueNormalizer.NormalizeDisplayText(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        const string healthCenterPrefix = "\uBCF4\uAC74\uC18C";
        if (!normalized.StartsWith(healthCenterPrefix, StringComparison.Ordinal))
            return string.Empty;

        var departmentName = normalized[healthCenterPrefix.Length..].Trim(' ', '-', '/', '\\');
        return string.IsNullOrWhiteSpace(departmentName)
            ? string.Empty
            : departmentName;
    }

    private static bool TryResolveRentalCustomerByNames(
        IReadOnlyCollection<LocalCustomer> customers,
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
            preferredResponsibleOfficeCode).ToList();
        if (TryResolveRentalCustomerFromCandidates(
                scopedCustomers,
                normalizedBusinessNumber,
                candidateKeys,
                out customerId))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(preferredTenantCode))
        {
            var tenantCustomers = customers
                .Where(customer =>
                    string.Equals(
                        NormalizeOperationalTenantCode(customer.TenantCode, customer.OfficeCode, customer.ResponsibleOfficeCode),
                        preferredTenantCode,
                        StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (tenantCustomers.Count > 0 &&
                !tenantCustomers.Select(customer => customer.Id).OrderBy(id => id)
                    .SequenceEqual(scopedCustomers.Select(customer => customer.Id).OrderBy(id => id)) &&
                TryResolveRentalCustomerFromCandidates(
                    tenantCustomers,
                    normalizedBusinessNumber,
                    candidateKeys,
                    out customerId))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveRentalCustomerFromCandidates(
        IReadOnlyCollection<LocalCustomer> candidates,
        string normalizedBusinessNumber,
        IReadOnlyCollection<string> candidateKeys,
        out Guid customerId)
    {
        customerId = Guid.Empty;
        var scopedCustomers = candidates.ToList();
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
        IReadOnlyDictionary<Guid, LocalCustomer> customerById,
        IReadOnlyCollection<string> candidateKeys,
        string? preferredTenantCode,
        string? preferredResponsibleOfficeCode)
        => HasValidCustomerId(customerId, customerById) &&
           MatchesOperationalCustomerScope(customerById[customerId!.Value], preferredTenantCode, preferredResponsibleOfficeCode) &&
           (candidateKeys.Count == 0 || CustomerMatchesRentalNames(customerById[customerId!.Value], candidateKeys));

    private static bool CustomerMatchesRentalNames(LocalCustomer customer, IReadOnlyCollection<string> candidateKeys)
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

    private static bool ProfileMatchesAssetItem(LocalRentalBillingProfile profile, string normalizedItemKey)
    {
        var profileItemKey = RentalCatalogValueNormalizer.NormalizeLooseKey(profile.ItemName);
        if (string.IsNullOrWhiteSpace(profileItemKey) || string.IsNullOrWhiteSpace(normalizedItemKey))
            return false;

        return string.Equals(profileItemKey, normalizedItemKey, StringComparison.OrdinalIgnoreCase)
               || profileItemKey.Contains(normalizedItemKey, StringComparison.OrdinalIgnoreCase)
               || normalizedItemKey.Contains(profileItemKey, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ProfileMatchesAssetSite(LocalRentalBillingProfile profile, IReadOnlyCollection<string> siteKeys)
    {
        if (siteKeys.Count == 0)
            return false;

        var profileSiteKey = RentalCatalogValueNormalizer.NormalizeLooseKey(profile.InstallSiteName);
        return !string.IsNullOrWhiteSpace(profileSiteKey) &&
               siteKeys.Contains(profileSiteKey, StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<LocalCustomer> FilterCustomersByOperationalScope(
        IReadOnlyCollection<LocalCustomer> customers,
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
        LocalCustomer customer,
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
        LocalRentalBillingProfile profile,
        string? preferredTenantCode,
        string? preferredResponsibleOfficeCode,
        Guid? assetCustomerId,
        IReadOnlyCollection<string> assetCustomerKeys)
        => MatchesOperationalTenantAndOfficeScope(profile.TenantCode, profile.OfficeCode, profile.ResponsibleOfficeCode, preferredTenantCode, preferredResponsibleOfficeCode) &&
           ProfileMatchesAssetCustomer(profile, assetCustomerId, assetCustomerKeys);

    private static bool NormalizeBillingProfileScope(LocalRentalBillingProfile profile)
    {
        var canonicalScope = RentalScopeNormalizer.ResolveScope(
            profile.TenantCode,
            profile.OfficeCode,
            profile.ManagementCompanyCode,
            profile.ResponsibleOfficeCode,
            DomainConstants.OfficeUsenet);
        var changed = false;

        if (RequiresExactTenantCode(profile.TenantCode, canonicalScope.TenantCode))
        {
            profile.TenantCode = canonicalScope.TenantCode;
            changed = true;
        }

        if (RequiresExactOfficeCode(profile.OfficeCode, canonicalScope.OwnerOfficeCode))
        {
            profile.OfficeCode = canonicalScope.OwnerOfficeCode;
            changed = true;
        }

        if (RequiresExactOfficeCode(profile.ManagementCompanyCode, canonicalScope.OwnerOfficeCode))
        {
            profile.ManagementCompanyCode = canonicalScope.OwnerOfficeCode;
            changed = true;
        }

        if (RequiresExactOfficeCode(profile.ResponsibleOfficeCode, canonicalScope.ResponsibleOfficeCode))
        {
            profile.ResponsibleOfficeCode = canonicalScope.ResponsibleOfficeCode;
            changed = true;
        }

        return changed;
    }

    private static bool NormalizeRentalAssetScope(LocalRentalAsset asset)
    {
        var canonicalScope = RentalScopeNormalizer.ResolveScope(
            asset.TenantCode,
            asset.OfficeCode,
            asset.ManagementCompanyCode,
            asset.ResponsibleOfficeCode,
            DomainConstants.OfficeUsenet);
        var changed = false;

        if (RequiresExactTenantCode(asset.TenantCode, canonicalScope.TenantCode))
        {
            asset.TenantCode = canonicalScope.TenantCode;
            changed = true;
        }

        if (RequiresExactOfficeCode(asset.OfficeCode, canonicalScope.OwnerOfficeCode))
        {
            asset.OfficeCode = canonicalScope.OwnerOfficeCode;
            changed = true;
        }

        if (RequiresExactOfficeCode(asset.ManagementCompanyCode, canonicalScope.OwnerOfficeCode))
        {
            asset.ManagementCompanyCode = canonicalScope.OwnerOfficeCode;
            changed = true;
        }

        if (RequiresExactOfficeCode(asset.ResponsibleOfficeCode, canonicalScope.ResponsibleOfficeCode))
        {
            asset.ResponsibleOfficeCode = canonicalScope.ResponsibleOfficeCode;
            changed = true;
        }

        return changed;
    }

    private static bool RequiresExactOfficeCode(string? currentOfficeCode, string expectedOfficeCode)
        => !OfficeCodeCatalog.TryNormalizeOfficeCode(currentOfficeCode, out var normalizedOfficeCode) ||
           !string.Equals(normalizedOfficeCode, expectedOfficeCode, StringComparison.OrdinalIgnoreCase) ||
           !string.Equals((currentOfficeCode ?? string.Empty).Trim(), expectedOfficeCode, StringComparison.OrdinalIgnoreCase);

    private static bool RequiresExactTenantCode(string? currentTenantCode, string expectedTenantCode)
        => !TenantScopeCatalog.TryNormalizeTenantCode(currentTenantCode, out var normalizedTenantCode) ||
           !string.Equals(normalizedTenantCode, expectedTenantCode, StringComparison.OrdinalIgnoreCase) ||
           !string.Equals((currentTenantCode ?? string.Empty).Trim(), expectedTenantCode, StringComparison.OrdinalIgnoreCase);

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

    private static bool HasValidCustomerId(Guid? customerId, IReadOnlyDictionary<Guid, LocalCustomer> customerById)
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

        return DomainConstants.OfficeUsenet;
    }

    private static bool TryResolveBillingProfileCustomer(
        IReadOnlyCollection<LocalCustomer> customers,
        LocalRentalBillingProfile profile,
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

    private static string ResolveCustomerRentalOfficeCode(string? responsibleOfficeCode)
        => OfficeCodeCatalog.NormalizeOfficeCodeLoose(responsibleOfficeCode, null, DomainConstants.DefaultOfficeUsenet);

    private static void MarkStartupMaintenanceChange(ILocalSyncEntity entity, DateTime updatedAtUtc)
    {
        entity.UpdatedAtUtc = updatedAtUtc;
        // 시작 보정은 로컬 캐시의 표시/연결 품질을 정리하기 위한 작업이다.
        // 조회 전용으로 내려온 타 계정(예: USENET 로그인 중 ITWORLD 렌탈 자산)까지
        // 새 서버 반영 대기 항목으로 만들면 한 계정만 로그인한 PC에서 동기화가 계속 보류된다.
        // 이미 사용자가 수정해 dirty 상태인 항목은 그대로 보존하고, 깨끗한 항목은 dirty로 승격하지 않는다.
    }

    private static string ExtractImportedAssetNoteValue(string? notes, string label)
    {
        if (string.IsNullOrWhiteSpace(notes) || string.IsNullOrWhiteSpace(label))
            return string.Empty;

        var normalizedNotes = notes
            .Replace('\r', ' ')
            .Replace('\n', ' ');
        var labelIndex = normalizedNotes.IndexOf(label, StringComparison.OrdinalIgnoreCase);
        if (labelIndex < 0)
            return string.Empty;

        var colonIndex = normalizedNotes.IndexOf(':', labelIndex);
        if (colonIndex < 0)
            return string.Empty;

        var tail = normalizedNotes[(colonIndex + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(tail))
            return string.Empty;

        if (label.Contains("관리번호", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var token in tail.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (LooksLikeManagementNumber(token))
                    return token;
            }

            return string.Empty;
        }

        return tail.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
    }

    private static bool LooksLikeManagementNumber(string token)
        => !string.IsNullOrWhiteSpace(token) &&
           token.Length == 8 &&
           token[4] == '-' &&
           token[..4].All(char.IsDigit) &&
           token[5..].All(char.IsDigit);
}
