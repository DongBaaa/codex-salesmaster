using Microsoft.EntityFrameworkCore;

namespace 거래플랜.Server.Api.Data;

public static partial class DbInitializer
{
    private static async Task NormalizeRentalAssetOfficeOwnershipAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var assets = await dbContext.RentalAssets.IgnoreQueryFilters().ToListAsync(cancellationToken);
        foreach (var asset in assets)
        {
            if (string.IsNullOrWhiteSpace(asset.AssignedUsername))
                continue;

            asset.AssignedUsername = string.Empty;
            asset.UpdatedAtUtc = now;
        }
    }
}
