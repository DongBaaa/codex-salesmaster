using System.Data;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Server.Api.Services;

namespace 거래플랜.Server.Api.Data;

public static partial class DbInitializer
{
    private static async Task<long> PrepareCommittedRevisionStatesBeforeRepairsAsync(
        AppDbContext centralDbContext,
        IReadOnlyCollection<TenantDatabaseConnectionInfo> dedicatedBusinessConnections,
        CancellationToken cancellationToken)
    {
        var localFloors = new List<long>
        {
            await PrepareCommittedRevisionStateBeforeRepairsAsync(
                centralDbContext,
                cancellationToken)
        };

        foreach (var connectionInfo in dedicatedBusinessConnections)
        {
            await using var tenantDbContext = CreateDbContext(
                connectionInfo,
                new RevisionClock());
            localFloors.Add(await PrepareCommittedRevisionStateBeforeRepairsAsync(
                tenantDbContext,
                cancellationToken));
        }

        var commonFloor = localFloors.Max();
        await centralDbContext.AdvanceCommittedRevisionFloorAsync(
            commonFloor,
            cancellationToken);
        foreach (var connectionInfo in dedicatedBusinessConnections)
        {
            await using var tenantDbContext = CreateDbContext(
                connectionInfo,
                new RevisionClock());
            await tenantDbContext.AdvanceCommittedRevisionFloorAsync(
                commonFloor,
                cancellationToken);
        }

        return commonFloor;
    }

    private static async Task<long> PrepareCommittedRevisionStateBeforeRepairsAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        await EnsureDatabaseSchemaAsync(dbContext, cancellationToken);
        await EnsureSyncRevisionStateSchemaAsync(dbContext, cancellationToken);
        return await SeedCommittedRevisionStateFromAvailableSchemaAsync(
            dbContext,
            cancellationToken);
    }

    private static async Task<long> SeedCommittedRevisionStateFromAvailableSchemaAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var availableMaximum = await GetAvailableMaxRevisionAsync(
            dbContext,
            cancellationToken);
        var committedRevision = await dbContext.GetCommittedRevisionAsync(cancellationToken);
        var localFloor = Math.Max(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Math.Max(availableMaximum, committedRevision));
        await dbContext.AdvanceCommittedRevisionFloorAsync(
            localFloor,
            cancellationToken);
        return localFloor;
    }

    private static async Task<long> GetAvailableMaxRevisionAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        var closeConnection = connection.State != ConnectionState.Open;
        if (closeConnection)
            await connection.OpenAsync(cancellationToken);

        try
        {
            var revisionTables = new List<string>();
            await using (var tableCommand = connection.CreateCommand())
            {
                tableCommand.CommandText = dbContext.Database.IsSqlite()
                    ? """
                      SELECT DISTINCT schema_table.name
                      FROM sqlite_master AS schema_table,
                           pragma_table_info(schema_table.name) AS schema_column
                      WHERE schema_table.type = 'table'
                        AND schema_column.name = 'Revision';
                      """
                    : """
                      SELECT DISTINCT table_name
                      FROM information_schema.columns
                      WHERE table_schema = current_schema()
                        AND column_name = 'Revision';
                      """;
                await using var reader = await tableCommand.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var tableName = reader.GetString(0);
                    if (SqlIdentifierPattern.IsMatch(tableName))
                        revisionTables.Add(tableName);
                }
            }

            var maximum = 0L;
            foreach (var tableName in revisionTables)
            {
                await using var maximumCommand = connection.CreateCommand();
                maximumCommand.CommandText =
                    $"""SELECT COALESCE(MAX("Revision"), 0) FROM "{tableName}";""";
                var result = await maximumCommand.ExecuteScalarAsync(cancellationToken);
                if (result is not null and not DBNull)
                    maximum = Math.Max(maximum, Convert.ToInt64(result));
            }

            return maximum;
        }
        finally
        {
            if (closeConnection)
                await connection.CloseAsync();
        }
    }

    private static async Task EnsureSyncRevisionStateSchemaAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var sql = dbContext.Database.IsSqlite()
            ? """
              CREATE TABLE IF NOT EXISTS "SyncRevisionStates" (
                  "Id" INTEGER NOT NULL CONSTRAINT "PK_SyncRevisionStates" PRIMARY KEY,
                  "CurrentRevision" INTEGER NOT NULL DEFAULT 0
              );
              """
            : """
              CREATE TABLE IF NOT EXISTS "SyncRevisionStates" (
                  "Id" integer NOT NULL PRIMARY KEY,
                  "CurrentRevision" bigint NOT NULL DEFAULT 0
              );
              """;
        await dbContext.Database.ExecuteSqlRawAsync(sql, cancellationToken);
    }

    private static async Task<long> InitializeCommittedRevisionStatesAsync(
        AppDbContext centralDbContext,
        IEnumerable<TenantDatabaseConnectionInfo> dedicatedBusinessConnections,
        CancellationToken cancellationToken)
    {
        var connections = dedicatedBusinessConnections.ToList();
        var maximums = new List<long>
        {
            Math.Max(
                await GetMaxRevisionAsync(centralDbContext, cancellationToken),
                await centralDbContext.GetCommittedRevisionAsync(cancellationToken))
        };

        foreach (var connectionInfo in connections)
        {
            await using var tenantDbContext = CreateDbContext(
                connectionInfo,
                new RevisionClock());
            maximums.Add(Math.Max(
                await GetMaxRevisionAsync(tenantDbContext, cancellationToken),
                await tenantDbContext.GetCommittedRevisionAsync(cancellationToken)));
        }

        // The legacy singleton clock was shared by every physical database, so a
        // client cursor can legitimately be higher than the maximum row revision
        // in its own database. Seed every database to one common cutover floor,
        // then let each database advance independently.
        var cutoverFloor = Math.Max(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            maximums.Max());
        await centralDbContext.AdvanceCommittedRevisionFloorAsync(
            cutoverFloor,
            cancellationToken);

        foreach (var connectionInfo in connections)
        {
            await using var tenantDbContext = CreateDbContext(
                connectionInfo,
                new RevisionClock());
            await tenantDbContext.AdvanceCommittedRevisionFloorAsync(
                cutoverFloor,
                cancellationToken);
        }

        return cutoverFloor;
    }
}
