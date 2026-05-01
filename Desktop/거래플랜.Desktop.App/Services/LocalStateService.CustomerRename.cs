using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

public sealed partial class LocalStateService
{
    private async Task SynchronizeLinkedRentalCustomerNamesForCustomerRenameAsync(
        LocalCustomer customer,
        string previousCustomerName,
        CancellationToken ct)
    {
        if (customer.Id == Guid.Empty)
            return;

        var newCustomerName = RentalCatalogValueNormalizer.NormalizeDisplayText(customer.NameOriginal);
        if (string.IsNullOrWhiteSpace(newCustomerName))
            return;

        var previousName = RentalCatalogValueNormalizer.NormalizeDisplayText(previousCustomerName);
        var now = DateTime.UtcNow;

        var profiles = await _db.RentalBillingProfiles
            .IgnoreQueryFilters()
            .Where(profile => !profile.IsDeleted && profile.CustomerId == customer.Id)
            .ToListAsync(ct);

        foreach (var profile in profiles)
        {
            if (!IsRentalScopeCompatibleWithCustomer(customer, ResolveRentalProfileOperationalScope(profile)))
                continue;

            if (!ShouldReplaceRenamedCustomerDisplay(profile.CustomerName, previousName))
                continue;

            if (string.Equals(profile.CustomerName, newCustomerName, StringComparison.Ordinal))
                continue;

            profile.CustomerName = newCustomerName;
            MarkRenamedLinkedRentalEntity(profile, now);
        }

        var assets = await _db.RentalAssets
            .IgnoreQueryFilters()
            .Where(asset => !asset.IsDeleted && asset.CustomerId == customer.Id)
            .ToListAsync(ct);

        foreach (var asset in assets)
        {
            if (!IsRentalScopeCompatibleWithCustomer(customer, ResolveRentalAssetOperationalScope(asset)))
                continue;

            var changed = false;
            if (ShouldReplaceRenamedCustomerDisplay(asset.CustomerName, previousName) &&
                !string.Equals(asset.CustomerName, newCustomerName, StringComparison.Ordinal))
            {
                asset.CustomerName = newCustomerName;
                changed = true;
            }

            if (ShouldReplaceRenamedCustomerDisplay(asset.CurrentCustomerName, previousName) &&
                !string.Equals(asset.CurrentCustomerName, newCustomerName, StringComparison.Ordinal))
            {
                asset.CurrentCustomerName = newCustomerName;
                changed = true;
            }

            if (changed)
                MarkRenamedLinkedRentalEntity(asset, now);
        }
    }

    private static bool AreCustomerDisplayNamesEquivalent(string? left, string? right)
        => string.Equals(
            NormalizeCustomerDisplayNameKey(left),
            NormalizeCustomerDisplayNameKey(right),
            StringComparison.OrdinalIgnoreCase);

    private static bool ShouldReplaceRenamedCustomerDisplay(string? currentValue, string previousCustomerName)
    {
        if (string.IsNullOrWhiteSpace(currentValue))
            return true;

        if (string.IsNullOrWhiteSpace(previousCustomerName))
            return false;

        return string.Equals(
            NormalizeCustomerDisplayNameKey(currentValue),
            NormalizeCustomerDisplayNameKey(previousCustomerName),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeCustomerDisplayNameKey(string? value)
        => RentalCatalogValueNormalizer.NormalizeLooseKey(value);

    private static bool IsRentalScopeCompatibleWithCustomer(LocalCustomer customer, RentalOperationalScope rentalScope)
    {
        var customerResponsibleOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(
            customer.ResponsibleOfficeCode,
            DomainConstants.OfficeUsenet);
        var customerOwnerOfficeCode = OfficeCodeCatalog.ResolveOwningOfficeCode(
            customer.OfficeCode,
            customerResponsibleOfficeCode,
            customerResponsibleOfficeCode);
        var customerTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            customer.TenantCode,
            customerOwnerOfficeCode,
            customer.TenantCode,
            customerResponsibleOfficeCode);

        return string.Equals(customerTenantCode, rentalScope.TenantCode, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(customerResponsibleOfficeCode, rentalScope.ResponsibleOfficeCode, StringComparison.OrdinalIgnoreCase);
    }

    private static RentalOperationalScope ResolveRentalProfileOperationalScope(LocalRentalBillingProfile profile)
        => RentalScopeNormalizer.ResolveScope(
            profile.TenantCode,
            profile.OfficeCode,
            profile.ManagementCompanyCode,
            profile.ResponsibleOfficeCode,
            OfficeCodeCatalog.Usenet);

    private static RentalOperationalScope ResolveRentalAssetOperationalScope(LocalRentalAsset asset)
        => RentalScopeNormalizer.ResolveScope(
            asset.TenantCode,
            asset.OfficeCode,
            asset.ManagementCompanyCode,
            asset.ResponsibleOfficeCode,
            OfficeCodeCatalog.Usenet);

    private static void MarkRenamedLinkedRentalEntity(ILocalSyncEntity entity, DateTime now)
    {
        entity.IsDirty = true;
        entity.UpdatedAtUtc = now;
    }
}
