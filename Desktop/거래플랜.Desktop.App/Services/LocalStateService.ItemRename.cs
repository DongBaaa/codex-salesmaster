using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

public sealed partial class LocalStateService
{
    private async Task SynchronizeLinkedRentalAssetItemMetadataForItemSaveAsync(
        LocalItem item,
        string previousItemName,
        string previousCategoryName,
        CancellationToken ct)
    {
        if (item.Id == Guid.Empty)
            return;

        var newItemName = RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(item.NameOriginal);
        var newCategoryName = SelectionOptionDefaults.NormalizeItemCategoryName(item.CategoryName);
        if (string.IsNullOrWhiteSpace(newItemName) && string.IsNullOrWhiteSpace(newCategoryName))
            return;

        var previousName = RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(previousItemName);
        var previousCategory = SelectionOptionDefaults.NormalizeItemCategoryName(previousCategoryName);
        var now = DateTime.UtcNow;

        var assets = await _db.RentalAssets
            .IgnoreQueryFilters()
            .Where(asset => !asset.IsDeleted && asset.ItemId == item.Id)
            .ToListAsync(ct);

        foreach (var asset in assets)
        {
            if (!IsRentalScopeCompatibleWithItem(item, ResolveRentalAssetOperationalScope(asset)))
                continue;

            var changed = false;

            if (!string.IsNullOrWhiteSpace(newItemName) &&
                ShouldReplaceRenamedItemDisplay(asset.ItemName, previousName) &&
                !string.Equals(asset.ItemName, newItemName, StringComparison.Ordinal))
            {
                asset.ItemName = newItemName;
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(newCategoryName) &&
                ShouldReplaceRenamedItemDisplay(asset.ItemCategoryName, previousCategory) &&
                !string.Equals(asset.ItemCategoryName, newCategoryName, StringComparison.Ordinal))
            {
                asset.ItemCategoryName = newCategoryName;
                changed = true;
            }

            if (changed)
                MarkRenamedLinkedRentalEntity(asset, now);
        }
    }

    private static bool ShouldReplaceRenamedItemDisplay(string? currentValue, string previousValue)
    {
        if (string.IsNullOrWhiteSpace(currentValue))
            return true;

        if (string.IsNullOrWhiteSpace(previousValue))
            return false;

        return string.Equals(
            RentalCatalogValueNormalizer.NormalizeLooseKey(currentValue),
            RentalCatalogValueNormalizer.NormalizeLooseKey(previousValue),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRentalScopeCompatibleWithItem(LocalItem item, RentalOperationalScope rentalScope)
    {
        var itemOfficeCode = NormalizeOfficeScope(item.OfficeCode, OfficeCodeCatalog.Shared);
        var itemTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            item.TenantCode,
            itemOfficeCode,
            item.TenantCode,
            itemOfficeCode);

        if (!string.Equals(itemTenantCode, rentalScope.TenantCode, StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.Equals(itemOfficeCode, OfficeCodeCatalog.Shared, StringComparison.OrdinalIgnoreCase))
            return true;

        return string.Equals(itemOfficeCode, rentalScope.ResponsibleOfficeCode, StringComparison.OrdinalIgnoreCase);
    }
}
