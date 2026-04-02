using Microsoft.EntityFrameworkCore;

namespace 거래플랜.Desktop.App.Data;

public static partial class LocalDbInitializer
{
    private static async Task NormalizeRentalAssetOfficeOwnershipAsync(LocalDbContext db)
    {
        var now = DateTime.UtcNow;
        var assets = await db.RentalAssets.IgnoreQueryFilters().ToListAsync();
        foreach (var asset in assets)
        {
            var changed = false;
            if (!string.IsNullOrWhiteSpace(asset.AssignedUsername))
            {
                asset.AssignedUsername = string.Empty;
                changed = true;
            }

            if (changed)
                PreserveDirtyStateForStartupMaintenance(asset, now);
        }
    }
}
