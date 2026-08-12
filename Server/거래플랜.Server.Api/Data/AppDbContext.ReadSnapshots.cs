using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace 거래플랜.Server.Api.Data;

public sealed partial class AppDbContext
{
    internal async Task<ConsistentReadSnapshot> BeginConsistentReadSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Database.IsRelational())
            return ConsistentReadSnapshot.Empty();

        if (Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException(
                "A consistent read snapshot cannot start inside an existing database transaction.");
        }

        if (Database.IsSqlite())
            return await BeginSqliteReadSnapshotAsync(cancellationToken);

        var providerName = Database.ProviderName ?? string.Empty;
        if (!providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"Consistent read snapshots are not configured for provider '{providerName}'.");
        }

        var transaction = await Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken);
        return new ConsistentReadSnapshot(
            Database,
            transaction,
            providerTransaction: null,
            closeConnection: false);
    }

    private async Task<ConsistentReadSnapshot> BeginSqliteReadSnapshotAsync(
        CancellationToken cancellationToken)
    {
        var connection = Database.GetDbConnection();
        var closeConnection = connection.State != ConnectionState.Open;
        if (closeConnection)
            await Database.OpenConnectionAsync(cancellationToken);

        try
        {
            if (connection is not SqliteConnection sqliteConnection)
            {
                throw new InvalidOperationException(
                    $"Expected a SQLite connection, but received '{connection.GetType().FullName}'.");
            }

            var journalMode = await ReadSqliteJournalModeAsync(
                sqliteConnection,
                cancellationToken);
            if (!SqliteJournalModePolicy.SupportsConcurrentPullSnapshot(
                    sqliteConnection,
                    journalMode))
            {
                throw new InvalidOperationException(
                    "SQLite sync pull requires WAL journal mode so a consistent read " +
                    $"snapshot does not block concurrent writer commits. Actual mode: '{journalMode}'.");
            }

            DbTransaction? providerTransaction = null;
            try
            {
                providerTransaction = sqliteConnection.BeginTransaction(
                    IsolationLevel.Serializable,
                    deferred: true);
                var contextTransaction = await Database.UseTransactionAsync(
                    providerTransaction,
                    cancellationToken);
                if (contextTransaction is null)
                {
                    throw new InvalidOperationException(
                        "Entity Framework did not attach the SQLite read transaction.");
                }

                return new ConsistentReadSnapshot(
                    Database,
                    contextTransaction,
                    providerTransaction,
                    closeConnection);
            }
            catch
            {
                if (providerTransaction is not null)
                    await providerTransaction.DisposeAsync();
                throw;
            }
        }
        catch
        {
            if (closeConnection)
                await Database.CloseConnectionAsync();
            throw;
        }
    }

    private static async Task<string> ReadSqliteJournalModeAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken))
            ?.Trim() ?? string.Empty;
    }
}

internal sealed class ConsistentReadSnapshot : IAsyncDisposable
{
    private readonly DatabaseFacade? _database;
    private readonly IDbContextTransaction? _contextTransaction;
    private readonly DbTransaction? _providerTransaction;
    private readonly bool _closeConnection;
    private bool _committed;

    internal ConsistentReadSnapshot(
        DatabaseFacade database,
        IDbContextTransaction contextTransaction,
        DbTransaction? providerTransaction,
        bool closeConnection)
    {
        _database = database;
        _contextTransaction = contextTransaction;
        _providerTransaction = providerTransaction;
        _closeConnection = closeConnection;
    }

    private ConsistentReadSnapshot()
    {
    }

    internal static ConsistentReadSnapshot Empty()
        => new();

    internal async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        if (_committed || _contextTransaction is null)
            return;

        await _contextTransaction.CommitAsync(cancellationToken);
        _committed = true;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_contextTransaction is not null)
                await _contextTransaction.DisposeAsync();
        }
        finally
        {
            try
            {
                if (_providerTransaction is not null)
                    await _providerTransaction.DisposeAsync();
            }
            finally
            {
                if (_closeConnection && _database is not null)
                    await _database.CloseConnectionAsync();
            }
        }
    }
}
