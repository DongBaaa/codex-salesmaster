using Microsoft.Data.Sqlite;

namespace 거래플랜.Server.Api.Data;

internal static class SqliteJournalModePolicy
{
    internal static bool SupportsConcurrentPullSnapshot(
        SqliteConnection connection,
        string? journalMode)
    {
        if (string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
            return true;

        var connectionOptions =
            new SqliteConnectionStringBuilder(connection.ConnectionString);
        return string.Equals(journalMode, "memory", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   connectionOptions.DataSource,
                   ":memory:",
                   StringComparison.OrdinalIgnoreCase);
    }
}
