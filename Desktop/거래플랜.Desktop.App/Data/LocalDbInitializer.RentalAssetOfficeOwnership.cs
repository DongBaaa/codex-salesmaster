using Microsoft.EntityFrameworkCore;

namespace 거래플랜.Desktop.App.Data;

public static partial class LocalDbInitializer
{
    private static async Task NormalizeRentalAssetOfficeOwnershipAsync(LocalDbContext db)
    {
        var now = DateTime.UtcNow;
        await ClearLegacyRentalAssignedUsernamesAsync(db, now);
    }

    private static async Task<int> ClearLegacyRentalAssignedUsernamesAsync(LocalDbContext db, DateTime now)
    {
        if (!await HasLegacyAssignedUsernameColumnAsync(db, "RentalBillingProfiles")
            && !await HasLegacyAssignedUsernameColumnAsync(db, "RentalAssets")
            && !await HasLegacyAssignedUsernameColumnAsync(db, "RentalBillingLogs"))
        {
            return 0;
        }

        var changed = 0;
        if (await HasLegacyAssignedUsernameColumnAsync(db, "RentalBillingProfiles"))
        {
            changed += await db.Database.ExecuteSqlInterpolatedAsync($@"UPDATE ""RentalBillingProfiles""
SET ""AssignedUsername"" = '', ""UpdatedAtUtc"" = {now}, ""IsDirty"" = 1
WHERE ""AssignedUsername"" <> '';");
        }

        if (await HasLegacyAssignedUsernameColumnAsync(db, "RentalAssets"))
        {
            changed += await db.Database.ExecuteSqlInterpolatedAsync($@"UPDATE ""RentalAssets""
SET ""AssignedUsername"" = '', ""UpdatedAtUtc"" = {now}, ""IsDirty"" = 1
WHERE ""AssignedUsername"" <> '';");
        }

        if (await HasLegacyAssignedUsernameColumnAsync(db, "RentalBillingLogs"))
        {
            changed += await db.Database.ExecuteSqlInterpolatedAsync($@"UPDATE ""RentalBillingLogs""
SET ""AssignedUsername"" = '', ""UpdatedAtUtc"" = {now}, ""IsDirty"" = 1
WHERE ""AssignedUsername"" <> '';");
        }

        return changed;
    }

    private static async Task<bool> HasLegacyAssignedUsernameColumnAsync(LocalDbContext db, string tableName)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        if (shouldClose)
            await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{tableName}\")";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (string.Equals(reader[1]?.ToString(), "AssignedUsername", StringComparison.Ordinal))
                return true;
        }

        if (shouldClose)
            await connection.CloseAsync();
        return false;
    }

    private static async Task DropLegacyRentalAssignedUsernameIndexesAsync(LocalDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("DROP INDEX IF EXISTS \"IX_RentalBillingProfiles_Assignee\";");
        await db.Database.ExecuteSqlRawAsync("DROP INDEX IF EXISTS \"IX_RentalAssets_Assignee\";");
        await db.Database.ExecuteSqlRawAsync("DROP INDEX IF EXISTS \"IX_RentalBillingLogs_AssigneeDate\";");
    }
}
