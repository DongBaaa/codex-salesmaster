using Microsoft.EntityFrameworkCore;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Server.Api.Data;

public static partial class DbInitializer
{
    private static async Task NormalizeUnitCatalogAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var units = await dbContext.Units.IgnoreQueryFilters()
            .OrderBy(current => current.CreatedAtUtc)
            .ThenBy(current => current.Name)
            .ToListAsync(cancellationToken);

        var trackedUnits = dbContext.Units.Local
            .Where(current => units.All(existing => existing.Id != current.Id))
            .ToList();
        if (trackedUnits.Count > 0)
            units.AddRange(trackedUnits);

        foreach (var canonicalName in UnitCatalogNormalizer.CanonicalDefaults)
        {
            if (units.Any(current => !current.IsDeleted && current.IsActive && string.Equals(UnitCatalogNormalizer.Normalize(current.Name), canonicalName, StringComparison.Ordinal)))
                continue;

            var created = new Unit
            {
                Name = canonicalName,
                IsActive = true,
                IsDeleted = false
            };
            dbContext.Units.Add(created);
            units.Add(created);
        }

        var groups = units
            .Where(current => !current.IsDeleted && current.IsActive)
            .GroupBy(current => UnitCatalogNormalizer.Normalize(current.Name), StringComparer.Ordinal)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .ToList();

        foreach (var group in groups)
        {
            var canonicalName = group.Key;
            var canonical = group
                .OrderByDescending(current => string.Equals(current.Name, canonicalName, StringComparison.Ordinal))
                .ThenBy(current => current.CreatedAtUtc)
                .ThenBy(current => current.Id)
                .First();

            canonical.Name = canonicalName;
            foreach (var duplicate in group.Where(current => current.Id != canonical.Id))
                dbContext.Units.Remove(duplicate);
        }

        foreach (var item in await dbContext.Items.IgnoreQueryFilters().ToListAsync(cancellationToken))
            item.Unit = UnitCatalogNormalizer.Normalize(item.Unit);

        foreach (var line in await dbContext.InvoiceLines.ToListAsync(cancellationToken))
            line.Unit = UnitCatalogNormalizer.Normalize(line.Unit);

        foreach (var line in await dbContext.InventoryTransferLines.IgnoreQueryFilters().ToListAsync(cancellationToken))
            line.Unit = UnitCatalogNormalizer.Normalize(line.Unit);
    }

    private static async Task NormalizeInventoryTransferIntegrityAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var transfers = await dbContext.InventoryTransfers.IgnoreQueryFilters().ToListAsync(cancellationToken);
        foreach (var transfer in transfers)
        {
            transfer.TransferStatus = InventoryTransferStatusNormalizer.Normalize(
                transfer.TransferStatus,
                transfer.ReceivedByUsername,
                transfer.ReceivedAtUtc,
                transfer.RejectedByUsername,
                transfer.RejectedAtUtc);
        }

        var deletedTransferIds = transfers.Where(current => current.IsDeleted).Select(current => current.Id).ToHashSet();
        var allTransferIds = transfers.Select(current => current.Id).ToHashSet();
        var existingItemIds = (await dbContext.Items.IgnoreQueryFilters()
                .Select(current => current.Id)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        foreach (var line in await dbContext.InventoryTransferLines.IgnoreQueryFilters().ToListAsync(cancellationToken))
        {
            if (!allTransferIds.Contains(line.TransferId) || deletedTransferIds.Contains(line.TransferId))
            {
                line.IsDeleted = true;
                continue;
            }

            if (line.ItemId.HasValue && !existingItemIds.Contains(line.ItemId.Value))
                line.ItemId = null;
        }
    }

    private static async Task PurgeDeletedInventoryTransferDataAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var deletedTransferIds = await dbContext.InventoryTransfers.IgnoreQueryFilters()
            .Where(current => current.IsDeleted)
            .Select(current => current.Id)
            .ToListAsync(cancellationToken);

        var orphanOrDeletedLines = await dbContext.InventoryTransferLines.IgnoreQueryFilters()
            .Where(current => deletedTransferIds.Contains(current.TransferId) || current.IsDeleted)
            .ToListAsync(cancellationToken);
        if (orphanOrDeletedLines.Count > 0)
            dbContext.InventoryTransferLines.RemoveRange(orphanOrDeletedLines);

        var deletedTransfers = await dbContext.InventoryTransfers.IgnoreQueryFilters()
            .Where(current => current.IsDeleted)
            .ToListAsync(cancellationToken);
        if (deletedTransfers.Count > 0)
            dbContext.InventoryTransfers.RemoveRange(deletedTransfers);
    }

    private static async Task RepairDeletedCustomerRentalProfileLinksAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var profiles = await dbContext.RentalBillingProfiles.IgnoreQueryFilters()
            .Where(current => !current.IsDeleted && current.CustomerId.HasValue && current.CustomerId.Value != Guid.Empty)
            .ToListAsync(cancellationToken);
        if (profiles.Count == 0)
            return;

        var customerIds = profiles.Select(current => current.CustomerId!.Value).Distinct().ToList();
        var customers = await dbContext.Customers.IgnoreQueryFilters()
            .Where(current => customerIds.Contains(current.Id))
            .ToDictionaryAsync(current => current.Id, cancellationToken);

        var activeCustomers = await dbContext.Customers.IgnoreQueryFilters()
            .Where(current => !current.IsDeleted)
            .ToListAsync(cancellationToken);

        foreach (var profile in profiles)
        {
            if (!customers.TryGetValue(profile.CustomerId!.Value, out var currentCustomer) || !currentCustomer.IsDeleted)
                continue;

            var replacement = ResolveActiveCustomerReplacement(activeCustomers, currentCustomer, profile.TenantCode, profile.BusinessNumber);
            if (replacement is null)
                continue;

            profile.CustomerId = replacement.Id;
            profile.CustomerName = replacement.NameOriginal;
            profile.BusinessNumber = replacement.BusinessNumber;
        }
    }

    private static Customer? ResolveActiveCustomerReplacement(
        IReadOnlyCollection<Customer> activeCustomers,
        Customer deletedCustomer,
        string? profileTenantCode,
        string? profileBusinessNumber)
    {
        var deletedName = (deletedCustomer.NameOriginal ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(deletedName))
            return null;

        var deletedTenant = TenantScopeCatalog.NormalizeTenantCodeOrDefault(profileTenantCode, deletedCustomer.TenantCode);
        var deletedBusinessNumber = (deletedCustomer.BusinessNumber ?? string.Empty).Trim();
        var profileBusiness = (profileBusinessNumber ?? string.Empty).Trim();

        var candidates = activeCustomers
            .Where(current => current.Id != deletedCustomer.Id)
            .Where(current => string.Equals((current.NameOriginal ?? string.Empty).Trim(), deletedName, StringComparison.Ordinal))
            .Where(current => string.Equals(TenantScopeCatalog.NormalizeTenantCodeOrDefault(current.TenantCode), deletedTenant, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count > 1 && !string.IsNullOrWhiteSpace(deletedBusinessNumber))
            candidates = candidates
                .Where(current => string.Equals((current.BusinessNumber ?? string.Empty).Trim(), deletedBusinessNumber, StringComparison.Ordinal))
                .ToList();

        if (candidates.Count > 1 && !string.IsNullOrWhiteSpace(profileBusiness))
            candidates = candidates
                .Where(current => string.Equals((current.BusinessNumber ?? string.Empty).Trim(), profileBusiness, StringComparison.Ordinal))
                .ToList();

        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static async Task CleanupDeletedInvoiceChainAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var deletedCustomerIds = (await dbContext.Customers.IgnoreQueryFilters()
                .Where(current => current.IsDeleted)
                .Select(current => current.Id)
                .ToListAsync(cancellationToken))
            .ToHashSet();
        var deletedInvoiceIds = (await dbContext.Invoices.IgnoreQueryFilters()
                .Where(current => current.IsDeleted)
                .Select(current => current.Id)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        var deletedTransactionIds = new HashSet<Guid>();
        foreach (var transaction in await dbContext.Transactions.IgnoreQueryFilters()
                     .Where(current => !current.IsDeleted)
                     .ToListAsync(cancellationToken))
        {
            var linkedInvoiceDeleted = transaction.LinkedInvoiceId.HasValue && deletedInvoiceIds.Contains(transaction.LinkedInvoiceId.Value);
            if (!deletedCustomerIds.Contains(transaction.CustomerId) && !linkedInvoiceDeleted)
                continue;

            transaction.IsDeleted = true;
            deletedTransactionIds.Add(transaction.Id);
        }

        foreach (var payment in await dbContext.Payments.IgnoreQueryFilters()
                     .Where(current => !current.IsDeleted)
                     .ToListAsync(cancellationToken))
        {
            if (deletedInvoiceIds.Contains(payment.InvoiceId))
                payment.IsDeleted = true;
        }

        foreach (var line in await dbContext.InvoiceLines
                     .Where(current => !current.IsDeleted && deletedInvoiceIds.Contains(current.InvoiceId))
                     .ToListAsync(cancellationToken))
        {
            line.IsDeleted = true;
        }

        foreach (var attachment in await dbContext.TransactionAttachments.IgnoreQueryFilters()
                     .Where(current => !current.IsDeleted && deletedTransactionIds.Contains(current.TransactionId))
                     .ToListAsync(cancellationToken))
        {
            attachment.IsDeleted = true;
        }
    }

    private static async Task EnsureUnitsUniqueIndexAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var providerName = dbContext.Database.ProviderName ?? string.Empty;
        if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "CREATE UNIQUE INDEX IF NOT EXISTS \"UX_Units_Name_Active\" ON \"Units\" ((UPPER(BTRIM(\"Name\")))) WHERE BTRIM(COALESCE(\"Name\", '')) <> '' AND COALESCE(\"IsDeleted\", false) = false AND COALESCE(\"IsActive\", true) = true;",
                cancellationToken);
            return;
        }

        await dbContext.Database.ExecuteSqlRawAsync(
            "CREATE UNIQUE INDEX IF NOT EXISTS \"UX_Units_Name_Active\" ON \"Units\" (UPPER(TRIM(\"Name\"))) WHERE COALESCE(TRIM(\"Name\"), '') <> '' AND COALESCE(\"IsDeleted\", 0) = 0 AND COALESCE(\"IsActive\", 1) = 1;",
            cancellationToken);
    }
}
