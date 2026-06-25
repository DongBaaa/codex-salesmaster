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
    private async Task SynchronizeLinkedRentalCustomerInfoAsync(
        LocalCustomer customer,
        CancellationToken ct)
    {
        if (customer.Id == Guid.Empty)
            return;

        var customerName = RentalCatalogValueNormalizer.NormalizeDisplayText(customer.NameOriginal);
        if (string.IsNullOrWhiteSpace(customerName))
            return;

        var businessNumber = (customer.BusinessNumber ?? string.Empty).Trim();
        var email = (customer.Email ?? string.Empty).Trim();
        var customerScope = ResolveCustomerRentalOperationalScope(customer);
        var now = DateTime.UtcNow;

        var profiles = await _db.RentalBillingProfiles
            .IgnoreQueryFilters()
            .Where(profile => !profile.IsDeleted && profile.CustomerId == customer.Id)
            .ToListAsync(ct);

        foreach (var profile in profiles)
        {
            var changed = false;
            changed |= SetIfDifferent(profile.CustomerName, customerName, value => profile.CustomerName = value);
            changed |= SetIfDifferent(profile.BusinessNumber, businessNumber, value => profile.BusinessNumber = value);
            changed |= SetIfDifferent(profile.Email, email, value => profile.Email = value);
            changed |= SetIfDifferent(profile.ResponsibleOfficeCode, customerScope.ResponsibleOfficeCode, value => profile.ResponsibleOfficeCode = value);
            changed |= SetIfDifferent(profile.OfficeCode, customerScope.OwnerOfficeCode, value => profile.OfficeCode = value);
            changed |= SetIfDifferent(profile.ManagementCompanyCode, customerScope.OwnerOfficeCode, value => profile.ManagementCompanyCode = value);
            changed |= SetIfDifferent(profile.TenantCode, customerScope.TenantCode, value => profile.TenantCode = value);
            if (changed)
                MarkLinkedRentalCustomerEntity(profile, now);
        }

        var assets = await _db.RentalAssets
            .IgnoreQueryFilters()
            .Where(asset => !asset.IsDeleted && asset.CustomerId == customer.Id)
            .ToListAsync(ct);

        foreach (var asset in assets)
        {
            var changed = false;
            changed |= SetIfDifferent(asset.CustomerName, customerName, value => asset.CustomerName = value);
            changed |= SetIfDifferent(asset.CurrentCustomerName, customerName, value => asset.CurrentCustomerName = value);
            changed |= SetIfDifferent(asset.ResponsibleOfficeCode, customerScope.ResponsibleOfficeCode, value => asset.ResponsibleOfficeCode = value);
            changed |= SetIfDifferent(asset.OfficeCode, customerScope.OwnerOfficeCode, value => asset.OfficeCode = value);
            changed |= SetIfDifferent(asset.ManagementCompanyCode, customerScope.OwnerOfficeCode, value => asset.ManagementCompanyCode = value);
            changed |= SetIfDifferent(asset.TenantCode, customerScope.TenantCode, value => asset.TenantCode = value);
            if (changed)
                MarkLinkedRentalCustomerEntity(asset, now);
        }
    }

    private static RentalOperationalScope ResolveCustomerRentalOperationalScope(LocalCustomer customer)
        => RentalScopeNormalizer.ResolveScope(
            customer.TenantCode,
            customer.OfficeCode,
            null,
            customer.ResponsibleOfficeCode,
            customer.OfficeCode);

    private static RentalOperationalScope ResolveRentalAssetOperationalScope(LocalRentalAsset asset)
        => RentalScopeNormalizer.ResolveScope(
            asset.TenantCode,
            asset.OfficeCode,
            asset.ManagementCompanyCode,
            asset.ResponsibleOfficeCode,
            OfficeCodeCatalog.Usenet);

    private static bool SetIfDifferent(string? currentValue, string desiredValue, Action<string> assign)
    {
        desiredValue ??= string.Empty;
        if (string.Equals(currentValue ?? string.Empty, desiredValue, StringComparison.Ordinal))
            return false;

        assign(desiredValue);
        return true;
    }

    private static void MarkLinkedRentalCustomerEntity(ILocalSyncEntity entity, DateTime now)
    {
        entity.IsDirty = true;
        entity.UpdatedAtUtc = now;
    }

    private static void MarkRenamedLinkedRentalEntity(ILocalSyncEntity entity, DateTime now)
        => MarkLinkedRentalCustomerEntity(entity, now);
}
