using Microsoft.EntityFrameworkCore;
using SalesMaster.Shared.Contracts;

namespace SalesMaster.Desktop.App.Data;

internal static class CustomerCategoryMaintenance
{
    public static async Task NormalizeAsync(LocalDbContext db, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var categories = await db.CustomerCategories.IgnoreQueryFilters().ToListAsync(ct);

        foreach (var definition in DefaultCustomerCategories.All)
        {
            var canonical = categories.FirstOrDefault(category => category.Id == definition.Id);
            if (canonical is null)
            {
                canonical = new LocalCustomerCategory
                {
                    Id = definition.Id,
                    Name = definition.Name,
                    IsSystemDefault = true,
                    IsDeleted = false,
                    IsDirty = true,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                };

                db.CustomerCategories.Add(canonical);
                categories.Add(canonical);
            }
            else
            {
                var canonicalName = string.IsNullOrWhiteSpace(canonical.Name)
                    ? definition.Name
                    : DefaultCustomerCategories.NormalizeName(canonical.Name);
                TouchCanonicalCategory(canonical, canonicalName, isSystemDefault: true, now);
            }
        }

        var groups = categories
            .Where(category => !string.IsNullOrWhiteSpace(category.Name))
            .GroupBy(category => DefaultCustomerCategories.NormalizeName(category.Name), StringComparer.CurrentCultureIgnoreCase)
            .Where(group => group.Count() > 1)
            .ToList();

        foreach (var group in groups)
        {
            var canonical = ResolveCanonicalCategory(group);
            TouchCanonicalCategory(canonical, DefaultCustomerCategories.NormalizeName(canonical.Name), canonical.IsSystemDefault, now);

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
                customer.IsDirty = true;
                customer.UpdatedAtUtc = now;
            }

            var customerMasters = await db.CustomerMasters.IgnoreQueryFilters()
                .Where(customer => customer.CategoryId.HasValue && duplicateIdSet.Contains(customer.CategoryId.Value))
                .ToListAsync(ct);
            foreach (var customerMaster in customerMasters)
            {
                customerMaster.CategoryId = canonical.Id;
                customerMaster.IsDirty = true;
                customerMaster.UpdatedAtUtc = now;
            }

            foreach (var duplicate in group.Where(category => category.Id != canonical.Id))
            {
                if (duplicate.IsDeleted)
                    continue;

                duplicate.IsDeleted = true;
                duplicate.IsDirty = true;
                duplicate.UpdatedAtUtc = now;
            }
        }

        if (db.ChangeTracker.HasChanges())
            await db.SaveChangesAsync(ct);
    }

    private static LocalCustomerCategory ResolveCanonicalCategory(IGrouping<string, LocalCustomerCategory> group)
    {
        if (DefaultCustomerCategories.TryGetByName(group.Key, out var definition))
        {
            var fixedCategory = group.FirstOrDefault(category => category.Id == definition.Id);
            if (fixedCategory is not null)
                return fixedCategory;
        }

        return group
            .OrderBy(category => category.IsDeleted)
            .ThenByDescending(category => category.IsSystemDefault)
            .ThenBy(category => category.CreatedAtUtc)
            .ThenBy(category => category.Id)
            .First();
    }

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

        if (isSystemDefault && !category.IsSystemDefault)
        {
            category.IsSystemDefault = true;
            changed = true;
        }

        if (changed)
        {
            category.IsDirty = true;
            category.UpdatedAtUtc = now;
        }
    }
}
