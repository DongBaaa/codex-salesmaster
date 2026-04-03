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

    private static async Task<bool> HasLegacyAssignedUsernameColumnAsync(
        AppDbContext dbContext,
        string tableName,
        CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        if (shouldClose)
            await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT EXISTS (
                                  SELECT 1
                                  FROM information_schema.columns
                                  WHERE table_schema = current_schema()
                                    AND table_name = @tableName
                                    AND column_name = 'AssignedUsername')
                              """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@tableName";
        parameter.Value = tableName;
        command.Parameters.Add(parameter);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        if (shouldClose)
            await connection.CloseAsync();
        return scalar is true || (scalar is bool boolValue && boolValue);
    }
}
