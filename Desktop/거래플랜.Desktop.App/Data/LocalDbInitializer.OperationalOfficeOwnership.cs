using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Data;

public static partial class LocalDbInitializer
{
    private static async Task BackfillOperationalOfficeOwnershipAsync(LocalDbContext db)
    {
        var customers = await db.Customers.IgnoreQueryFilters().ToListAsync();
        var customerLookup = customers.ToDictionary(customer => customer.Id);
        var invoices = await db.Invoices.IgnoreQueryFilters().ToListAsync();
        var invoiceLookup = invoices.ToDictionary(invoice => invoice.Id);
        var transactions = await db.Transactions.IgnoreQueryFilters().ToListAsync();
        var profiles = await db.RentalBillingProfiles.IgnoreQueryFilters().ToListAsync();
        var profileLookup = profiles.ToDictionary(profile => profile.Id);
        var assets = await db.RentalAssets.IgnoreQueryFilters().ToListAsync();
        var logs = await db.RentalBillingLogs.IgnoreQueryFilters().ToListAsync();

        var changed = false;
        var now = DateTime.UtcNow;

        foreach (var customer in customers)
        {
            var entityChanged = false;
            var desiredResponsibleOfficeCode = NormalizeOperationalResponsibleOfficeCode(
                customer.ResponsibleOfficeCode,
                customer.OfficeCode,
                DomainConstants.OfficeUsenet);
            var desiredOfficeCode = ResolveOperationalOwnerOfficeCode(
                customer.OfficeCode,
                desiredResponsibleOfficeCode,
                OfficeCodeCatalog.Shared);
            var desiredTenantCode = NormalizeOperationalTenantCode(
                customer.TenantCode,
                desiredOfficeCode,
                desiredResponsibleOfficeCode);

            entityChanged |= TryAssign(customer, entity => entity.ResponsibleOfficeCode, (entity, value) => entity.ResponsibleOfficeCode = value, desiredResponsibleOfficeCode);
            entityChanged |= TryAssign(customer, entity => entity.OfficeCode, (entity, value) => entity.OfficeCode = value, desiredOfficeCode);
            entityChanged |= TryAssign(customer, entity => entity.TenantCode, (entity, value) => entity.TenantCode = value, desiredTenantCode);
            changed |= MarkOperationalOwnershipChange(customer, entityChanged, now);
        }

        foreach (var invoice in invoices)
        {
            var entityChanged = false;
            customerLookup.TryGetValue(invoice.CustomerId, out var linkedCustomer);
            var desiredResponsibleOfficeCode = NormalizeOperationalResponsibleOfficeCode(
                invoice.ResponsibleOfficeCode,
                linkedCustomer?.ResponsibleOfficeCode,
                invoice.OfficeCode,
                linkedCustomer?.OfficeCode,
                DomainConstants.OfficeUsenet);
            var desiredOfficeCode = ResolveOperationalOwnerOfficeCode(
                invoice.OfficeCode,
                desiredResponsibleOfficeCode,
                linkedCustomer?.OfficeCode,
                DomainConstants.OfficeUsenet);
            var desiredTenantCode = NormalizeOperationalTenantCode(
                invoice.TenantCode,
                desiredOfficeCode,
                desiredResponsibleOfficeCode);

            entityChanged |= TryAssign(invoice, entity => entity.ResponsibleOfficeCode, (entity, value) => entity.ResponsibleOfficeCode = value, desiredResponsibleOfficeCode);
            entityChanged |= TryAssign(invoice, entity => entity.OfficeCode, (entity, value) => entity.OfficeCode = value, desiredOfficeCode);
            entityChanged |= TryAssign(invoice, entity => entity.TenantCode, (entity, value) => entity.TenantCode = value, desiredTenantCode);
            changed |= MarkOperationalOwnershipChange(invoice, entityChanged, now);
        }

        foreach (var transaction in transactions)
        {
            var entityChanged = false;
            customerLookup.TryGetValue(transaction.CustomerId, out var linkedCustomer);
            LocalInvoice? linkedInvoice = null;
            if (transaction.LinkedInvoiceId is Guid linkedInvoiceId && linkedInvoiceId != Guid.Empty)
                invoiceLookup.TryGetValue(linkedInvoiceId, out linkedInvoice);
            LocalRentalBillingProfile? linkedProfile = null;
            if (transaction.LinkedRentalBillingProfileId is Guid linkedProfileId && linkedProfileId != Guid.Empty)
                profileLookup.TryGetValue(linkedProfileId, out linkedProfile);

            var desiredResponsibleOfficeCode = NormalizeOperationalResponsibleOfficeCode(
                transaction.ResponsibleOfficeCode,
                linkedInvoice?.ResponsibleOfficeCode,
                linkedProfile?.ResponsibleOfficeCode,
                linkedCustomer?.ResponsibleOfficeCode,
                transaction.OfficeCode,
                DomainConstants.OfficeUsenet);
            var desiredOfficeCode = ResolveOperationalOwnerOfficeCode(
                transaction.OfficeCode,
                desiredResponsibleOfficeCode,
                linkedInvoice?.OfficeCode,
                linkedProfile?.OfficeCode,
                linkedCustomer?.OfficeCode,
                DomainConstants.OfficeUsenet);
            var desiredTenantCode = NormalizeOperationalTenantCode(
                transaction.TenantCode,
                desiredOfficeCode,
                desiredResponsibleOfficeCode);

            entityChanged |= TryAssign(transaction, entity => entity.ResponsibleOfficeCode, (entity, value) => entity.ResponsibleOfficeCode = value, desiredResponsibleOfficeCode);
            entityChanged |= TryAssign(transaction, entity => entity.OfficeCode, (entity, value) => entity.OfficeCode = value, desiredOfficeCode);
            entityChanged |= TryAssign(transaction, entity => entity.TenantCode, (entity, value) => entity.TenantCode = value, desiredTenantCode);
            changed |= MarkOperationalOwnershipChange(transaction, entityChanged, now);
        }

        foreach (var profile in profiles)
        {
            var entityChanged = false;
            LocalCustomer? linkedCustomer = null;
            if (profile.CustomerId is Guid linkedCustomerId && linkedCustomerId != Guid.Empty)
                customerLookup.TryGetValue(linkedCustomerId, out linkedCustomer);

            var desiredResponsibleOfficeCode = NormalizeOperationalResponsibleOfficeCode(
                profile.ResponsibleOfficeCode,
                linkedCustomer?.ResponsibleOfficeCode,
                profile.OfficeCode,
                profile.ManagementCompanyCode,
                linkedCustomer?.OfficeCode,
                DomainConstants.OfficeUsenet);
            var desiredOfficeCode = ResolveOperationalOwnerOfficeCode(
                profile.OfficeCode,
                desiredResponsibleOfficeCode,
                profile.ManagementCompanyCode,
                linkedCustomer?.OfficeCode,
                DomainConstants.OfficeUsenet);
            var desiredTenantCode = NormalizeOperationalTenantCode(
                profile.TenantCode,
                desiredOfficeCode,
                desiredResponsibleOfficeCode);

            entityChanged |= TryAssign(profile, entity => entity.ResponsibleOfficeCode, (entity, value) => entity.ResponsibleOfficeCode = value, desiredResponsibleOfficeCode);
            entityChanged |= TryAssign(profile, entity => entity.OfficeCode, (entity, value) => entity.OfficeCode = value, desiredOfficeCode);
            entityChanged |= TryAssign(profile, entity => entity.ManagementCompanyCode, (entity, value) => entity.ManagementCompanyCode = value, desiredOfficeCode);
            entityChanged |= TryAssign(profile, entity => entity.TenantCode, (entity, value) => entity.TenantCode = value, desiredTenantCode);
            changed |= MarkOperationalOwnershipChange(profile, entityChanged, now);
        }

        foreach (var asset in assets)
        {
            var entityChanged = false;
            LocalCustomer? linkedCustomer = null;
            if (asset.CustomerId is Guid linkedCustomerId && linkedCustomerId != Guid.Empty)
                customerLookup.TryGetValue(linkedCustomerId, out linkedCustomer);
            LocalRentalBillingProfile? linkedProfile = null;
            if (asset.BillingProfileId is Guid linkedProfileId && linkedProfileId != Guid.Empty)
                profileLookup.TryGetValue(linkedProfileId, out linkedProfile);

            var desiredResponsibleOfficeCode = NormalizeOperationalResponsibleOfficeCode(
                asset.ResponsibleOfficeCode,
                linkedProfile?.ResponsibleOfficeCode,
                linkedCustomer?.ResponsibleOfficeCode,
                asset.OfficeCode,
                asset.ManagementCompanyCode,
                linkedCustomer?.OfficeCode,
                DomainConstants.OfficeUsenet);
            var desiredOfficeCode = ResolveOperationalOwnerOfficeCode(
                asset.OfficeCode,
                desiredResponsibleOfficeCode,
                asset.ManagementCompanyCode,
                linkedProfile?.OfficeCode,
                linkedCustomer?.OfficeCode,
                DomainConstants.OfficeUsenet);
            var desiredTenantCode = NormalizeOperationalTenantCode(
                asset.TenantCode,
                desiredOfficeCode,
                desiredResponsibleOfficeCode);

            entityChanged |= TryAssign(asset, entity => entity.ResponsibleOfficeCode, (entity, value) => entity.ResponsibleOfficeCode = value, desiredResponsibleOfficeCode);
            entityChanged |= TryAssign(asset, entity => entity.OfficeCode, (entity, value) => entity.OfficeCode = value, desiredOfficeCode);
            entityChanged |= TryAssign(asset, entity => entity.ManagementCompanyCode, (entity, value) => entity.ManagementCompanyCode = value, desiredOfficeCode);
            entityChanged |= TryAssign(asset, entity => entity.TenantCode, (entity, value) => entity.TenantCode = value, desiredTenantCode);
            changed |= MarkOperationalOwnershipChange(asset, entityChanged, now);
        }

        foreach (var log in logs)
        {
            var entityChanged = false;
            profileLookup.TryGetValue(log.BillingProfileId, out var linkedProfile);

            var desiredResponsibleOfficeCode = NormalizeOperationalResponsibleOfficeCode(
                log.ResponsibleOfficeCode,
                linkedProfile?.ResponsibleOfficeCode,
                log.OfficeCode,
                DomainConstants.OfficeUsenet);
            var desiredOfficeCode = ResolveOperationalOwnerOfficeCode(
                log.OfficeCode,
                desiredResponsibleOfficeCode,
                linkedProfile?.OfficeCode,
                DomainConstants.OfficeUsenet);
            var desiredTenantCode = NormalizeOperationalTenantCode(
                log.TenantCode,
                desiredOfficeCode,
                desiredResponsibleOfficeCode);

            entityChanged |= TryAssign(log, entity => entity.ResponsibleOfficeCode, (entity, value) => entity.ResponsibleOfficeCode = value, desiredResponsibleOfficeCode);
            entityChanged |= TryAssign(log, entity => entity.OfficeCode, (entity, value) => entity.OfficeCode = value, desiredOfficeCode);
            entityChanged |= TryAssign(log, entity => entity.TenantCode, (entity, value) => entity.TenantCode = value, desiredTenantCode);
            changed |= MarkOperationalOwnershipChange(log, entityChanged, now);
        }

        if (changed)
            await db.SaveChangesAsync();
    }

    private static string NormalizeOperationalResponsibleOfficeCode(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (OfficeCodeCatalog.TryNormalize(candidate, out var normalizedOfficeCode))
                return normalizedOfficeCode;
        }

        return DomainConstants.OfficeUsenet;
    }

    private static string ResolveOperationalOwnerOfficeCode(
        string? ownerOfficeCode,
        string? responsibleOfficeCode = null,
        params string?[] fallbackCandidates)
    {
        foreach (var fallbackCandidate in fallbackCandidates)
        {
            if (string.IsNullOrWhiteSpace(fallbackCandidate))
                continue;

            return OfficeCodeCatalog.ResolveOwningOfficeCode(ownerOfficeCode, responsibleOfficeCode, fallbackCandidate);
        }

        return OfficeCodeCatalog.ResolveOwningOfficeCode(ownerOfficeCode, responsibleOfficeCode, DomainConstants.OfficeUsenet);
    }

    private static string NormalizeOperationalTenantCode(
        string? tenantCode,
        string? ownerOfficeCode,
        string? responsibleOfficeCode = null)
        => TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            tenantCode,
            ResolveOperationalOwnerOfficeCode(ownerOfficeCode, responsibleOfficeCode),
            tenantCode,
            responsibleOfficeCode);

    private static bool TryAssign<TEntity>(
        TEntity entity,
        Func<TEntity, string> getter,
        Action<TEntity, string> setter,
        string desiredValue)
    {
        if (string.Equals(getter(entity), desiredValue, StringComparison.OrdinalIgnoreCase))
            return false;

        setter(entity, desiredValue);
        return true;
    }

    private static bool MarkOperationalOwnershipChange<TEntity>(
        TEntity entity,
        bool entityChanged,
        DateTime now)
        where TEntity : class, ILocalSyncEntity
    {
        if (!entityChanged)
            return false;

        entity.IsDirty = true;
        entity.UpdatedAtUtc = now;
        return true;
    }
}
