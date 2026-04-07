using Microsoft.EntityFrameworkCore;

namespace 거래플랜.Server.Api.Data;

public static partial class DbInitializer
{
    private static async Task NormalizeRentalAssetOfficeOwnershipAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await ClearLegacyRentalAssignedUsernamesAsync(dbContext, now, cancellationToken);
    }

    private static async Task<int> ClearLegacyRentalAssignedUsernamesAsync(
        AppDbContext dbContext,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var hasProfileColumn = await HasLegacyAssignedUsernameColumnAsync(dbContext, "RentalBillingProfiles", cancellationToken);
        var hasAssetColumn = await HasLegacyAssignedUsernameColumnAsync(dbContext, "RentalAssets", cancellationToken);
        var hasLogColumn = await HasLegacyAssignedUsernameColumnAsync(dbContext, "RentalBillingLogs", cancellationToken);
        if (!hasProfileColumn && !hasAssetColumn && !hasLogColumn)
            return 0;

        var changed = 0;
        if (hasProfileColumn)
        {
            changed += await dbContext.Database.ExecuteSqlInterpolatedAsync($@"UPDATE ""RentalBillingProfiles""
SET ""AssignedUsername"" = '', ""UpdatedAtUtc"" = {now}
WHERE ""AssignedUsername"" <> '';", cancellationToken);
        }

        if (hasAssetColumn)
        {
            changed += await dbContext.Database.ExecuteSqlInterpolatedAsync($@"UPDATE ""RentalAssets""
SET ""AssignedUsername"" = '', ""UpdatedAtUtc"" = {now}
WHERE ""AssignedUsername"" <> '';", cancellationToken);
        }

        if (hasLogColumn)
        {
            changed += await dbContext.Database.ExecuteSqlInterpolatedAsync($@"UPDATE ""RentalBillingLogs""
SET ""AssignedUsername"" = '', ""UpdatedAtUtc"" = {now}
WHERE ""AssignedUsername"" <> '';", cancellationToken);
        }

        return changed;
    }

    private static Task<bool> HasLegacyAssignedUsernameColumnAsync(
        AppDbContext dbContext,
        string tableName,
        CancellationToken cancellationToken)
    {
        return HasColumnAsync(dbContext, tableName, "AssignedUsername", cancellationToken);
    }
}
