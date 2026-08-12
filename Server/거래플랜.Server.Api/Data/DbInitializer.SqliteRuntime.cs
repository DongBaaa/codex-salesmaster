using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace 거래플랜.Server.Api.Data;

public static partial class DbInitializer
{
    private static async Task EnsureSqliteRuntimeJournalModeAsync(
        AppDbContext dbContext,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!dbContext.Database.IsSqlite())
            return;

        var connection = dbContext.Database.GetDbConnection();
        var closeConnection = connection.State != ConnectionState.Open;
        if (closeConnection)
            await dbContext.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            if (connection is not SqliteConnection sqliteConnection)
            {
                throw new InvalidOperationException(
                    $"Expected a SQLite connection, but received '{connection.GetType().FullName}'.");
            }

            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL;";
            var journalMode = Convert.ToString(
                    await command.ExecuteScalarAsync(cancellationToken))
                ?.Trim();
            if (!SqliteJournalModePolicy.SupportsConcurrentPullSnapshot(
                    sqliteConnection,
                    journalMode))
            {
                throw new InvalidOperationException(
                    "SQLite runtime requires WAL journal mode for file databases, " +
                    $"but received '{journalMode}'.");
            }

            logger.LogInformation(
                "SQLite runtime journal mode configured: {JournalMode}",
                journalMode);
        }
        finally
        {
            if (closeConnection)
                await dbContext.Database.CloseConnectionAsync();
        }
    }
}
