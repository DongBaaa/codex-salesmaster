using Microsoft.EntityFrameworkCore;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Server.Api.Data;

public static partial class DbInitializer
{
    private static async Task EnsureOperationalResponsibleOfficeColumnsAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        await EnsureColumnAsync(dbContext, "Customers", "ResponsibleOfficeCode", $"TEXT NOT NULL DEFAULT '{OfficeCodeCatalog.Usenet}'", $"text NOT NULL DEFAULT '{OfficeCodeCatalog.Usenet}'", cancellationToken);
        await EnsureColumnAsync(dbContext, "Invoices", "ResponsibleOfficeCode", $"TEXT NOT NULL DEFAULT '{OfficeCodeCatalog.Usenet}'", $"text NOT NULL DEFAULT '{OfficeCodeCatalog.Usenet}'", cancellationToken);
        await EnsureColumnAsync(dbContext, "Transactions", "ResponsibleOfficeCode", $"TEXT NOT NULL DEFAULT '{OfficeCodeCatalog.Usenet}'", $"text NOT NULL DEFAULT '{OfficeCodeCatalog.Usenet}'", cancellationToken);
        await EnsureColumnAsync(dbContext, "RentalBillingProfiles", "ResponsibleOfficeCode", $"TEXT NOT NULL DEFAULT '{OfficeCodeCatalog.Usenet}'", $"text NOT NULL DEFAULT '{OfficeCodeCatalog.Usenet}'", cancellationToken);
        await EnsureColumnAsync(dbContext, "RentalAssets", "ResponsibleOfficeCode", $"TEXT NOT NULL DEFAULT '{OfficeCodeCatalog.Usenet}'", $"text NOT NULL DEFAULT '{OfficeCodeCatalog.Usenet}'", cancellationToken);
        await EnsureColumnAsync(dbContext, "RentalBillingLogs", "ResponsibleOfficeCode", $"TEXT NOT NULL DEFAULT '{OfficeCodeCatalog.Usenet}'", $"text NOT NULL DEFAULT '{OfficeCodeCatalog.Usenet}'", cancellationToken);

        foreach (var sql in new[]
                 {
                     "CREATE INDEX IF NOT EXISTS \"IX_Customers_ResponsibleOfficeCode\" ON \"Customers\" (\"ResponsibleOfficeCode\");",
                     "CREATE INDEX IF NOT EXISTS \"IX_Invoices_ResponsibleOfficeCode\" ON \"Invoices\" (\"ResponsibleOfficeCode\");",
                     "CREATE INDEX IF NOT EXISTS \"IX_Transactions_ResponsibleOfficeCode\" ON \"Transactions\" (\"ResponsibleOfficeCode\");",
                     "CREATE INDEX IF NOT EXISTS \"IX_RentalBillingProfiles_ResponsibleOfficeCode\" ON \"RentalBillingProfiles\" (\"ResponsibleOfficeCode\");",
                     "CREATE INDEX IF NOT EXISTS \"IX_RentalAssets_ResponsibleOfficeCode\" ON \"RentalAssets\" (\"ResponsibleOfficeCode\");",
                     "CREATE INDEX IF NOT EXISTS \"IX_RentalBillingLogs_ResponsibleOfficeCode\" ON \"RentalBillingLogs\" (\"ResponsibleOfficeCode\");"
                 })
        {
            try
            {
                await dbContext.Database.ExecuteSqlRawAsync(sql, cancellationToken);
            }
            catch
            {
            }
        }
    }

    private static async Task BackfillOperationalOfficeOwnershipAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var customers = await dbContext.Customers.IgnoreQueryFilters().ToListAsync(cancellationToken);
        var customerLookup = customers.ToDictionary(customer => customer.Id);
        var invoices = await dbContext.Invoices.IgnoreQueryFilters().ToListAsync(cancellationToken);
        var invoiceLookup = invoices.ToDictionary(invoice => invoice.Id);
        var transactions = await dbContext.Transactions.IgnoreQueryFilters().ToListAsync(cancellationToken);
        var profiles = await dbContext.RentalBillingProfiles.IgnoreQueryFilters().ToListAsync(cancellationToken);
        var profileLookup = profiles.ToDictionary(profile => profile.Id);
        var assets = await dbContext.RentalAssets.IgnoreQueryFilters().ToListAsync(cancellationToken);
        var logs = await dbContext.RentalBillingLogs.IgnoreQueryFilters().ToListAsync(cancellationToken);

        var changed = false;

        foreach (var customer in customers)
        {
            var desiredResponsibleOfficeCode = NormalizeOperationalResponsibleOfficeCode(
                customer.ResponsibleOfficeCode,
                customer.OfficeCode,
                OfficeCodeCatalog.Usenet);
            var desiredOfficeCode = ResolveOperationalOwnerOfficeCode(
                customer.OfficeCode,
                desiredResponsibleOfficeCode,
                OfficeCodeCatalog.Shared);
            var desiredTenantCode = NormalizeOperationalTenantCode(
                customer.TenantCode,
                desiredOfficeCode,
                desiredResponsibleOfficeCode);

            changed |= TryAssign(customer, entity => entity.ResponsibleOfficeCode, (entity, value) => entity.ResponsibleOfficeCode = value, desiredResponsibleOfficeCode);
            changed |= TryAssign(customer, entity => entity.OfficeCode, (entity, value) => entity.OfficeCode = value, desiredOfficeCode);
            changed |= TryAssign(customer, entity => entity.TenantCode, (entity, value) => entity.TenantCode = value, desiredTenantCode);
        }

        foreach (var invoice in invoices)
        {
            customerLookup.TryGetValue(invoice.CustomerId, out var linkedCustomer);
            var desiredResponsibleOfficeCode = NormalizeOperationalResponsibleOfficeCode(
                invoice.ResponsibleOfficeCode,
                linkedCustomer?.ResponsibleOfficeCode,
                invoice.OfficeCode,
                linkedCustomer?.OfficeCode,
                OfficeCodeCatalog.Usenet);
            var desiredOfficeCode = ResolveOperationalOwnerOfficeCode(
                invoice.OfficeCode,
                desiredResponsibleOfficeCode,
                linkedCustomer?.OfficeCode,
                OfficeCodeCatalog.Usenet);
            var desiredTenantCode = NormalizeOperationalTenantCode(
                invoice.TenantCode,
                desiredOfficeCode,
                desiredResponsibleOfficeCode);

            changed |= TryAssign(invoice, entity => entity.ResponsibleOfficeCode, (entity, value) => entity.ResponsibleOfficeCode = value, desiredResponsibleOfficeCode);
            changed |= TryAssign(invoice, entity => entity.OfficeCode, (entity, value) => entity.OfficeCode = value, desiredOfficeCode);
            changed |= TryAssign(invoice, entity => entity.TenantCode, (entity, value) => entity.TenantCode = value, desiredTenantCode);
        }

        foreach (var transaction in transactions)
        {
            customerLookup.TryGetValue(transaction.CustomerId, out var linkedCustomer);
            Invoice? linkedInvoice = null;
            if (transaction.LinkedInvoiceId is Guid linkedInvoiceId && linkedInvoiceId != Guid.Empty)
                invoiceLookup.TryGetValue(linkedInvoiceId, out linkedInvoice);
            RentalBillingProfile? linkedProfile = null;
            if (transaction.LinkedRentalBillingProfileId is Guid linkedProfileId && linkedProfileId != Guid.Empty)
                profileLookup.TryGetValue(linkedProfileId, out linkedProfile);

            var desiredResponsibleOfficeCode = NormalizeOperationalResponsibleOfficeCode(
                transaction.ResponsibleOfficeCode,
                linkedInvoice?.ResponsibleOfficeCode,
                linkedProfile?.ResponsibleOfficeCode,
                linkedCustomer?.ResponsibleOfficeCode,
                transaction.OfficeCode,
                OfficeCodeCatalog.Usenet);
            var desiredOfficeCode = ResolveOperationalOwnerOfficeCode(
                transaction.OfficeCode,
                desiredResponsibleOfficeCode,
                linkedInvoice?.OfficeCode,
                linkedProfile?.OfficeCode,
                linkedCustomer?.OfficeCode,
                OfficeCodeCatalog.Usenet);
            var desiredTenantCode = NormalizeOperationalTenantCode(
                transaction.TenantCode,
                desiredOfficeCode,
                desiredResponsibleOfficeCode);

            changed |= TryAssign(transaction, entity => entity.ResponsibleOfficeCode, (entity, value) => entity.ResponsibleOfficeCode = value, desiredResponsibleOfficeCode);
            changed |= TryAssign(transaction, entity => entity.OfficeCode, (entity, value) => entity.OfficeCode = value, desiredOfficeCode);
            changed |= TryAssign(transaction, entity => entity.TenantCode, (entity, value) => entity.TenantCode = value, desiredTenantCode);
        }

        foreach (var profile in profiles)
        {
            Customer? linkedCustomer = null;
            if (profile.CustomerId is Guid linkedCustomerId && linkedCustomerId != Guid.Empty)
                customerLookup.TryGetValue(linkedCustomerId, out linkedCustomer);

            var desiredResponsibleOfficeCode = NormalizeOperationalResponsibleOfficeCode(
                profile.ResponsibleOfficeCode,
                linkedCustomer?.ResponsibleOfficeCode,
                profile.OfficeCode,
                profile.ManagementCompanyCode,
                linkedCustomer?.OfficeCode,
                OfficeCodeCatalog.Usenet);
            var desiredOfficeCode = ResolveOperationalOwnerOfficeCode(
                profile.OfficeCode,
                desiredResponsibleOfficeCode,
                profile.ManagementCompanyCode,
                linkedCustomer?.OfficeCode,
                OfficeCodeCatalog.Usenet);
            var desiredTenantCode = NormalizeOperationalTenantCode(
                profile.TenantCode,
                desiredOfficeCode,
                desiredResponsibleOfficeCode);

            changed |= TryAssign(profile, entity => entity.ResponsibleOfficeCode, (entity, value) => entity.ResponsibleOfficeCode = value, desiredResponsibleOfficeCode);
            changed |= TryAssign(profile, entity => entity.OfficeCode, (entity, value) => entity.OfficeCode = value, desiredOfficeCode);
            changed |= TryAssign(profile, entity => entity.ManagementCompanyCode, (entity, value) => entity.ManagementCompanyCode = value, desiredOfficeCode);
            changed |= TryAssign(profile, entity => entity.TenantCode, (entity, value) => entity.TenantCode = value, desiredTenantCode);
        }

        foreach (var asset in assets)
        {
            Customer? linkedCustomer = null;
            if (asset.CustomerId is Guid linkedCustomerId && linkedCustomerId != Guid.Empty)
                customerLookup.TryGetValue(linkedCustomerId, out linkedCustomer);
            RentalBillingProfile? linkedProfile = null;
            if (asset.BillingProfileId is Guid linkedProfileId && linkedProfileId != Guid.Empty)
                profileLookup.TryGetValue(linkedProfileId, out linkedProfile);

            var desiredResponsibleOfficeCode = NormalizeOperationalResponsibleOfficeCode(
                asset.ResponsibleOfficeCode,
                linkedProfile?.ResponsibleOfficeCode,
                linkedCustomer?.ResponsibleOfficeCode,
                asset.OfficeCode,
                asset.ManagementCompanyCode,
                linkedCustomer?.OfficeCode,
                OfficeCodeCatalog.Usenet);
            var desiredOfficeCode = ResolveOperationalOwnerOfficeCode(
                asset.OfficeCode,
                desiredResponsibleOfficeCode,
                asset.ManagementCompanyCode,
                linkedProfile?.OfficeCode,
                linkedCustomer?.OfficeCode,
                OfficeCodeCatalog.Usenet);
            var desiredTenantCode = NormalizeOperationalTenantCode(
                asset.TenantCode,
                desiredOfficeCode,
                desiredResponsibleOfficeCode);

            changed |= TryAssign(asset, entity => entity.ResponsibleOfficeCode, (entity, value) => entity.ResponsibleOfficeCode = value, desiredResponsibleOfficeCode);
            changed |= TryAssign(asset, entity => entity.OfficeCode, (entity, value) => entity.OfficeCode = value, desiredOfficeCode);
            changed |= TryAssign(asset, entity => entity.ManagementCompanyCode, (entity, value) => entity.ManagementCompanyCode = value, desiredOfficeCode);
            changed |= TryAssign(asset, entity => entity.TenantCode, (entity, value) => entity.TenantCode = value, desiredTenantCode);
        }

        foreach (var log in logs)
        {
            profileLookup.TryGetValue(log.BillingProfileId, out var linkedProfile);

            var desiredResponsibleOfficeCode = NormalizeOperationalResponsibleOfficeCode(
                log.ResponsibleOfficeCode,
                linkedProfile?.ResponsibleOfficeCode,
                log.OfficeCode,
                OfficeCodeCatalog.Usenet);
            var desiredOfficeCode = ResolveOperationalOwnerOfficeCode(
                log.OfficeCode,
                desiredResponsibleOfficeCode,
                linkedProfile?.OfficeCode,
                OfficeCodeCatalog.Usenet);
            var desiredTenantCode = NormalizeOperationalTenantCode(
                log.TenantCode,
                desiredOfficeCode,
                desiredResponsibleOfficeCode);

            changed |= TryAssign(log, entity => entity.ResponsibleOfficeCode, (entity, value) => entity.ResponsibleOfficeCode = value, desiredResponsibleOfficeCode);
            changed |= TryAssign(log, entity => entity.OfficeCode, (entity, value) => entity.OfficeCode = value, desiredOfficeCode);
            changed |= TryAssign(log, entity => entity.TenantCode, (entity, value) => entity.TenantCode = value, desiredTenantCode);
        }

        if (changed)
            await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string NormalizeOperationalResponsibleOfficeCode(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (OfficeCodeCatalog.TryNormalize(candidate, out var normalizedOfficeCode))
                return normalizedOfficeCode;
        }

        return OfficeCodeCatalog.Usenet;
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

        return OfficeCodeCatalog.ResolveOwningOfficeCode(ownerOfficeCode, responsibleOfficeCode, OfficeCodeCatalog.Usenet);
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
}
