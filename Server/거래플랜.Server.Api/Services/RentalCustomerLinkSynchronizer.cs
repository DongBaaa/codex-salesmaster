using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace 거래플랜.Server.Api.Services;

internal static class RentalCustomerLinkSynchronizer
{
    public static async Task SynchronizeAsync(AppDbContext dbContext, Customer customer, CancellationToken cancellationToken)
    {
        if (customer.Id == Guid.Empty || customer.IsDeleted)
            return;

        var customerName = RentalCatalogValueNormalizer.NormalizeDisplayText(customer.NameOriginal);
        if (string.IsNullOrWhiteSpace(customerName))
            return;

        var businessNumber = customer.BusinessNumber?.Trim() ?? string.Empty;
        var email = customer.Email?.Trim() ?? string.Empty;
        var customerScope = RentalScopeNormalizer.ResolveScope(
            customer.TenantCode,
            customer.OfficeCode,
            null,
            customer.ResponsibleOfficeCode,
            customer.OfficeCode);

        var profiles = await dbContext.RentalBillingProfiles
            .IgnoreQueryFilters()
            .Where(profile => !profile.IsDeleted && profile.CustomerId == customer.Id)
            .ToListAsync(cancellationToken);

        foreach (var profile in profiles)
        {
            SetIfDifferent(profile.CustomerName, customerName, value => profile.CustomerName = value);
            SetIfDifferent(profile.BusinessNumber, businessNumber, value => profile.BusinessNumber = value);
            SetIfDifferent(profile.Email, email, value => profile.Email = value);
            SetIfDifferent(profile.ResponsibleOfficeCode, customerScope.ResponsibleOfficeCode, value => profile.ResponsibleOfficeCode = value);
            SetIfDifferent(profile.OfficeCode, customerScope.OwnerOfficeCode, value => profile.OfficeCode = value);
            SetIfDifferent(profile.ManagementCompanyCode, customerScope.OwnerOfficeCode, value => profile.ManagementCompanyCode = value);
            SetIfDifferent(profile.TenantCode, customerScope.TenantCode, value => profile.TenantCode = value);
        }

        var assets = await dbContext.RentalAssets
            .IgnoreQueryFilters()
            .Where(asset => !asset.IsDeleted && asset.CustomerId == customer.Id)
            .ToListAsync(cancellationToken);

        foreach (var asset in assets)
        {
            SetIfDifferent(asset.CustomerName, customerName, value => asset.CustomerName = value);
            SetIfDifferent(asset.CurrentCustomerName, customerName, value => asset.CurrentCustomerName = value);
            SetIfDifferent(asset.ResponsibleOfficeCode, customerScope.ResponsibleOfficeCode, value => asset.ResponsibleOfficeCode = value);
            SetIfDifferent(asset.OfficeCode, customerScope.OwnerOfficeCode, value => asset.OfficeCode = value);
            SetIfDifferent(asset.ManagementCompanyCode, customerScope.OwnerOfficeCode, value => asset.ManagementCompanyCode = value);
            SetIfDifferent(asset.TenantCode, customerScope.TenantCode, value => asset.TenantCode = value);
        }
    }

    private static void SetIfDifferent(string? currentValue, string desiredValue, Action<string> assign)
    {
        desiredValue ??= string.Empty;
        if (string.Equals(currentValue ?? string.Empty, desiredValue, StringComparison.Ordinal))
            return;

        assign(desiredValue);
    }
}
