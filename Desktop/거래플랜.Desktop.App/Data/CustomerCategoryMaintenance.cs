using Microsoft.EntityFrameworkCore;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Data;

internal static class CustomerCategoryMaintenance
{
    public static async Task NormalizeAsync(LocalDbContext db, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var categories = await db.CustomerCategories.IgnoreQueryFilters().ToListAsync(ct);

        var activeCategories = categories
            .Where(category => !category.IsDeleted)
            .Where(category => !string.IsNullOrWhiteSpace(category.Name))
            .ToList();

        foreach (var category in activeCategories)
        {
            var normalizedName = DefaultCustomerCategories.NormalizeName(category.Name);
            TouchCanonicalCategory(category, normalizedName, isSystemDefault: false, now);
        }

        var groups = activeCategories
            .GroupBy(category => DefaultCustomerCategories.NormalizeName(category.Name), StringComparer.CurrentCultureIgnoreCase)
            .Where(group => group.Count() > 1)
            .ToList();

        foreach (var group in groups)
        {
            var canonical = ResolveCanonicalCategory(group);
            TouchCanonicalCategory(canonical, DefaultCustomerCategories.NormalizeName(canonical.Name), isSystemDefault: false, now);

            var duplicateIds = group
                .Where(category => category.Id != canonical.Id)
                .Select(category => category.Id)
                .Distinct()
                .ToList();

            if (duplicateIds.Count == 0)
                continue;

            var duplicateIdSet = duplicateIds.ToHashSet();

            var customers = await db.Customers.IgnoreQueryFilters()
                .Where(customer => customer.CategoryId.HasValue && duplicateIdSet.Contains(customer.CategoryId.Value))
                .ToListAsync(ct);
            foreach (var customer in customers)
            {
                customer.CategoryId = canonical.Id;
                PreserveDirtyState(customer, now);
            }

            var customerMasters = await db.CustomerMasters.IgnoreQueryFilters()
                .Where(customer => customer.CategoryId.HasValue && duplicateIdSet.Contains(customer.CategoryId.Value))
                .ToListAsync(ct);
            foreach (var customerMaster in customerMasters)
            {
                customerMaster.CategoryId = canonical.Id;
                PreserveDirtyState(customerMaster, now);
            }

            foreach (var duplicate in group.Where(category => category.Id != canonical.Id))
            {
                if (duplicate.IsDeleted)
                    continue;

                duplicate.IsDeleted = true;
                PreserveDirtyState(duplicate, now);
            }
        }

        if (db.ChangeTracker.HasChanges())
            await db.SaveChangesAsync(ct);
    }

    private static LocalCustomerCategory ResolveCanonicalCategory(IGrouping<string, LocalCustomerCategory> group)
        => group
            .OrderBy(category => category.CreatedAtUtc)
            .ThenBy(category => category.Id)
            .First();

    private static void TouchCanonicalCategory(
        LocalCustomerCategory category,
        string normalizedName,
        bool isSystemDefault,
        DateTime now)
    {
        var changed = false;

        if (!string.Equals(category.Name, normalizedName, StringComparison.CurrentCulture))
        {
            category.Name = normalizedName;
            changed = true;
        }

        if (category.IsDeleted)
        {
            category.IsDeleted = false;
            changed = true;
        }

        if (category.IsSystemDefault != isSystemDefault)
        {
            category.IsSystemDefault = isSystemDefault;
            changed = true;
        }

        if (changed)
        {
            PreserveDirtyState(category, now);
        }
    }

    private static void PreserveDirtyState(ILocalSyncEntity entity, DateTime updatedAtUtc)
    {
        entity.UpdatedAtUtc = updatedAtUtc;
    }
}
