using System.Collections.Concurrent;
using System.Text;
using 거래플랜.Server.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace 거래플랜.Server.Api.Utilities;

// Serializes stock snapshot operations and idempotent sync mutation batches per database.
// This scope intentionally does not impose a minimum stock quantity.
public sealed class InventoryMutationTransactionScope : IAsyncDisposable
{
    private const int AdvisoryLockNamespace = 0x47504C4E; // "GPLN"
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProcessLocks = new(StringComparer.Ordinal);

    private readonly IDbContextTransaction _transaction;
    private readonly SemaphoreSlim? _processLock;
    private bool _disposed;

    private InventoryMutationTransactionScope(
        IDbContextTransaction transaction,
        SemaphoreSlim? processLock)
    {
        _transaction = transaction;
        _processLock = processLock;
    }

    public static async Task<InventoryMutationTransactionScope> BeginAsync(
        AppDbContext dbContext,
        bool serializeInventoryMutations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        SemaphoreSlim? processLock = null;
        var processLockHeld = false;
        IDbContextTransaction? transaction = null;
        try
        {
            if (serializeInventoryMutations)
            {
                var lockIdentity = BuildLockIdentity(dbContext);
                processLock = ProcessLocks.GetOrAdd(lockIdentity, static _ => new SemaphoreSlim(1, 1));
                await processLock.WaitAsync(cancellationToken);
                processLockHeld = true;
            }

            transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

            if (serializeInventoryMutations && IsNpgsql(dbContext))
            {
                var databaseName = dbContext.Database.GetDbConnection().Database;
                var databaseKey = ComputeStableKey(databaseName);
                await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_xact_lock({AdvisoryLockNamespace}, {databaseKey});",
                    cancellationToken);
            }

            return new InventoryMutationTransactionScope(transaction, processLock);
        }
        catch
        {
            try
            {
                if (transaction is not null)
                    await transaction.DisposeAsync();
            }
            finally
            {
                if (processLockHeld)
                    processLock?.Release();
            }
            throw;
        }
    }

    public Task CommitAsync(CancellationToken cancellationToken = default)
        => _transaction.CommitAsync(cancellationToken);

    public Task RollbackAsync(CancellationToken cancellationToken = default)
        => _transaction.RollbackAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        try
        {
            await _transaction.DisposeAsync();
        }
        finally
        {
            _processLock?.Release();
        }
    }

    private static bool IsNpgsql(AppDbContext dbContext)
        => string.Equals(
            dbContext.Database.ProviderName,
            "Npgsql.EntityFrameworkCore.PostgreSQL",
            StringComparison.Ordinal);

    private static string BuildLockIdentity(AppDbContext dbContext)
    {
        if (!dbContext.Database.IsRelational())
            return dbContext.Database.ProviderName ?? "non-relational";

        var connection = dbContext.Database.GetDbConnection();
        return string.Join('|', dbContext.Database.ProviderName, connection.DataSource, connection.Database);
    }

    private static int ComputeStableKey(string? value)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;
        var hash = offsetBasis;

        foreach (var character in Encoding.UTF8.GetBytes(value ?? string.Empty))
        {
            hash ^= character;
            hash *= prime;
        }

        return unchecked((int)hash);
    }
}
