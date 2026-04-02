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

    private static async Task DropLegacyRentalAssignedUsernameIndexesAsync(LocalDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("DROP INDEX IF EXISTS \"IX_RentalBillingProfiles_Assignee\";");
        await db.Database.ExecuteSqlRawAsync("DROP INDEX IF EXISTS \"IX_RentalAssets_Assignee\";");
        await db.Database.ExecuteSqlRawAsync("DROP INDEX IF EXISTS \"IX_RentalBillingLogs_AssigneeDate\";");
    }
}
