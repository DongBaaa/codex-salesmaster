using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Data;

public static partial class LocalDbInitializer
{
    private static async Task NormalizeUnitCatalogAsync(LocalDbContext db)
    {
        var now = DateTime.UtcNow;
        var units = await db.Units.IgnoreQueryFilters()
            .OrderBy(current => current.CreatedAtUtc)
            .ThenBy(current => current.Name)
            .ToListAsync();

        foreach (var canonicalName in UnitCatalogNormalizer.CanonicalDefaults)
        {
            if (units.Any(current => !current.IsDeleted && current.IsActive && string.Equals(UnitCatalogNormalizer.Normalize(current.Name), canonicalName, StringComparison.Ordinal)))
                continue;

            var created = new LocalUnit
            {
                Name = canonicalName,
                IsActive = true,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                Revision = 0,
                IsDeleted = false,
                IsDirty = false
            };
            db.Units.Add(created);
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

            if (!string.Equals(canonical.Name, canonicalName, StringComparison.Ordinal))
            {
                canonical.Name = canonicalName;
                PreserveDirtyStateForStartupMaintenance(canonical, now);
            }

            foreach (var duplicate in group.Where(current => current.Id != canonical.Id))
                db.Units.Remove(duplicate);
        }

        foreach (var item in await db.Items.IgnoreQueryFilters().ToListAsync())
        {
            var normalized = UnitCatalogNormalizer.Normalize(item.Unit);
            if (string.Equals(item.Unit, normalized, StringComparison.Ordinal))
                continue;

            item.Unit = normalized;
            PreserveDirtyStateForStartupMaintenance(item, now);
        }

        foreach (var line in await db.InvoiceLines.ToListAsync())
        {
            var normalized = UnitCatalogNormalizer.Normalize(line.Unit);
            if (!string.Equals(line.Unit, normalized, StringComparison.Ordinal))
                line.Unit = normalized;
        }

        foreach (var line in await db.InventoryTransferLines.IgnoreQueryFilters().ToListAsync())
        {
            var normalized = UnitCatalogNormalizer.Normalize(line.Unit);
            if (!string.Equals(line.Unit, normalized, StringComparison.Ordinal))
                line.Unit = normalized;
        }
    }

    private static async Task NormalizeInventoryTransferIntegrityAsync(LocalDbContext db)
    {
        var now = DateTime.UtcNow;
        var transfers = await db.InventoryTransfers.IgnoreQueryFilters().ToListAsync();
        foreach (var transfer in transfers)
        {
            var normalizedStatus = InventoryTransferStatusNormalizer.Normalize(
                transfer.TransferStatus,
                transfer.ReceivedByUsername,
                transfer.ReceivedAtUtc,
                transfer.RejectedByUsername,
                transfer.RejectedAtUtc);

            if (string.Equals(transfer.TransferStatus, normalizedStatus, StringComparison.Ordinal))
                continue;

            transfer.TransferStatus = normalizedStatus;
            PreserveDirtyStateForStartupMaintenance(transfer, now);
        }

        var deletedTransferIds = transfers
            .Where(current => current.IsDeleted)
            .Select(current => current.Id)
            .ToHashSet();
        var allTransferIds = transfers.Select(current => current.Id).ToHashSet();
        var existingItemIds = (await db.Items.IgnoreQueryFilters()
                .Select(current => current.Id)
                .ToListAsync())
            .ToHashSet();

        foreach (var line in await db.InventoryTransferLines.IgnoreQueryFilters().ToListAsync())
        {
            if (!allTransferIds.Contains(line.TransferId) || deletedTransferIds.Contains(line.TransferId))
            {
                if (!line.IsDeleted)
                    line.IsDeleted = true;
                continue;
            }

            if (line.ItemId.HasValue && !existingItemIds.Contains(line.ItemId.Value))
                line.ItemId = null;
        }
    }

    private static async Task PurgeDeletedInventoryTransferDataAsync(LocalDbContext db)
    {
        var deletedTransferIds = await db.InventoryTransfers.IgnoreQueryFilters()
            .Where(current => current.IsDeleted)
            .Select(current => current.Id)
            .ToListAsync();

        var orphanOrDeletedLines = await db.InventoryTransferLines.IgnoreQueryFilters()
            .Where(current => deletedTransferIds.Contains(current.TransferId) || current.IsDeleted)
            .ToListAsync();
        if (orphanOrDeletedLines.Count > 0)
            db.InventoryTransferLines.RemoveRange(orphanOrDeletedLines);

        var deletedTransfers = await db.InventoryTransfers.IgnoreQueryFilters()
            .Where(current => current.IsDeleted)
            .ToListAsync();
        if (deletedTransfers.Count > 0)
            db.InventoryTransfers.RemoveRange(deletedTransfers);
    }

    private static async Task RepairDeletedCustomerRentalProfileLinksAsync(LocalDbContext db)
    {
        var now = DateTime.UtcNow;
        var profiles = await db.RentalBillingProfiles.IgnoreQueryFilters()
            .Where(current => !current.IsDeleted && current.CustomerId.HasValue && current.CustomerId.Value != Guid.Empty)
            .ToListAsync();
        if (profiles.Count == 0)
            return;

        var customerIds = profiles.Select(current => current.CustomerId!.Value).Distinct().ToList();
        var customers = await db.Customers.IgnoreQueryFilters()
            .Where(current => customerIds.Contains(current.Id))
            .ToDictionaryAsync(current => current.Id);

        var activeCustomers = await db.Customers.IgnoreQueryFilters()
            .Where(current => !current.IsDeleted)
            .ToListAsync();

        foreach (var profile in profiles)
        {
            if (!customers.TryGetValue(profile.CustomerId!.Value, out var currentCustomer) || !currentCustomer.IsDeleted)
                continue;

            var replacement = ResolveActiveCustomerReplacement(
                activeCustomers,
                currentCustomer,
                TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
                    null,
                    profile.ResponsibleOfficeCode,
                    currentCustomer.TenantCode,
                    currentCustomer.ResponsibleOfficeCode),
                profile.BusinessNumber);
            if (replacement is null)
                continue;

            profile.CustomerId = replacement.Id;
            profile.CustomerName = replacement.NameOriginal;
            profile.BusinessNumber = replacement.BusinessNumber;
            PreserveDirtyStateForStartupMaintenance(profile, now);
        }
    }

    private static LocalCustomer? ResolveActiveCustomerReplacement(
        IReadOnlyCollection<LocalCustomer> activeCustomers,
        LocalCustomer deletedCustomer,
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

    private static async Task CleanupDeletedInvoiceChainAsync(LocalDbContext db)
    {
        var now = DateTime.UtcNow;
        var deletedCustomerIds = (await db.Customers.IgnoreQueryFilters()
                .Where(current => current.IsDeleted)
                .Select(current => current.Id)
                .ToListAsync())
            .ToHashSet();
        var deletedInvoiceIds = (await db.Invoices.IgnoreQueryFilters()
                .Where(current => current.IsDeleted)
                .Select(current => current.Id)
                .ToListAsync())
            .ToHashSet();

        var deletedTransactionIds = new HashSet<Guid>();
        foreach (var transaction in await db.Transactions.IgnoreQueryFilters()
                     .Where(current => !current.IsDeleted)
                     .ToListAsync())
        {
            var linkedInvoiceDeleted = transaction.LinkedInvoiceId.HasValue && deletedInvoiceIds.Contains(transaction.LinkedInvoiceId.Value);
            if (!deletedCustomerIds.Contains(transaction.CustomerId) && !linkedInvoiceDeleted)
                continue;

            transaction.IsDeleted = true;
            PreserveDirtyStateForStartupMaintenance(transaction, now);
            deletedTransactionIds.Add(transaction.Id);
        }

        foreach (var payment in await db.Payments.IgnoreQueryFilters()
                     .Where(current => !current.IsDeleted)
                     .ToListAsync())
        {
            if (!deletedInvoiceIds.Contains(payment.InvoiceId))
                continue;

            payment.IsDeleted = true;
            PreserveDirtyStateForStartupMaintenance(payment, now);
        }

        var deletedInvoiceLineIds = new HashSet<Guid>();
        foreach (var line in await db.InvoiceLines
                     .Where(current => !current.IsDeleted && deletedInvoiceIds.Contains(current.InvoiceId))
                     .ToListAsync())
        {
            line.IsDeleted = true;
            deletedInvoiceLineIds.Add(line.Id);
        }

        foreach (var attachment in await db.TransactionAttachments.IgnoreQueryFilters()
                     .Where(current => !current.IsDeleted && deletedTransactionIds.Contains(current.TransactionId))
                     .ToListAsync())
        {
            attachment.IsDeleted = true;
            PreserveDirtyStateForStartupMaintenance(attachment, now);
        }

        var orphanSerials = await db.InvoiceLineSerials
            .Where(current => deletedInvoiceIds.Contains(current.InvoiceId) || deletedInvoiceLineIds.Contains(current.InvoiceLineId))
            .ToListAsync();
        if (orphanSerials.Count > 0)
            db.InvoiceLineSerials.RemoveRange(orphanSerials);
    }
}
