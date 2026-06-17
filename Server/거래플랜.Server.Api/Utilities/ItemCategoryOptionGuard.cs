using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace 거래플랜.Server.Api.Utilities;

public static class ItemCategoryOptionGuard
{
    public static async Task<string> EnsureActiveOptionAsync(
        AppDbContext dbContext,
        string? categoryName,
        CancellationToken cancellationToken)
    {
        var ensured = await EnsureActiveOptionsAsync(dbContext, [categoryName], cancellationToken);
        var normalizedKey = RentalCatalogValueNormalizer.NormalizeLooseKey(
            RentalCatalogValueNormalizer.NormalizeCategoryDisplayName(categoryName));
        return string.IsNullOrWhiteSpace(normalizedKey) || !ensured.TryGetValue(normalizedKey, out var canonicalName)
            ? string.Empty
            : canonicalName;
    }

    public static async Task<IReadOnlyDictionary<string, string>> EnsureActiveOptionsAsync(
        AppDbContext dbContext,
        IEnumerable<string?> categoryNames,
        CancellationToken cancellationToken)
    {
        var options = await dbContext.ItemCategoryOptions
            .IgnoreQueryFilters()
            .ToListAsync(cancellationToken);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var normalizedName in categoryNames
                     .Select(RentalCatalogValueNormalizer.NormalizeCategoryDisplayName)
                     .Where(name => !string.IsNullOrWhiteSpace(name) && !IsInvalidCategoryName(name))
                     .Distinct(StringComparer.CurrentCultureIgnoreCase))
        {
            var normalizedKey = RentalCatalogValueNormalizer.NormalizeLooseKey(normalizedName);
            if (string.IsNullOrWhiteSpace(normalizedKey) || result.ContainsKey(normalizedKey))
                continue;

            result[normalizedKey] = EnsureActiveOption(dbContext, options, normalizedName);
        }

        return result;
    }

    private static string EnsureActiveOption(
        AppDbContext dbContext,
        List<ItemCategoryOption> options,
        string normalizedName)
    {
        var normalizedKey = RentalCatalogValueNormalizer.NormalizeLooseKey(normalizedName);
        var existing = options.FirstOrDefault(option =>
            string.Equals(
                RentalCatalogValueNormalizer.NormalizeLooseKey(option.Name),
                normalizedKey,
                StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            var nextSortOrder = options.Count == 0
                ? 0
                : options.Max(option => option.SortOrder) + 10;
            var created = new ItemCategoryOption
            {
                Id = Guid.NewGuid(),
                Name = normalizedName,
                SortOrder = nextSortOrder,
                IsSystemDefault = false,
                IsActive = true,
                IsDeleted = false
            };
            dbContext.ItemCategoryOptions.Add(created);
            options.Add(created);
            return normalizedName;
        }

        var canonicalName = RentalCatalogValueNormalizer.NormalizeCategoryDisplayName(existing.Name);
        if (string.IsNullOrWhiteSpace(canonicalName))
            canonicalName = normalizedName;

        existing.Name = canonicalName;
        existing.IsActive = true;
        existing.IsDeleted = false;
        return canonicalName;
    }

    private static bool IsInvalidCategoryName(string value)
        => value.Trim().All(ch => ch == '?' || ch == '\uFFFD');
}
