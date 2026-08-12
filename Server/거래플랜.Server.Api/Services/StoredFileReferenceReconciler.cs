using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace 거래플랜.Server.Api.Services;

public interface IStoredFileReferenceReconciler
{
    Task DeleteUnreferencedAsync(
        IEnumerable<string> candidatePaths,
        CancellationToken cancellationToken = default);

    Task<PaymentAttachment?> FindPaymentAttachmentAsync(
        Guid attachmentId,
        CancellationToken cancellationToken = default);
}

public enum StoredFileReconcileOutcome
{
    Completed,
    LeaseDeferred,
    LookupInconclusive,
    DeletionIncomplete
}

internal sealed class PreserveAllStoredFileReferenceReconciler : IStoredFileReferenceReconciler
{
    public static PreserveAllStoredFileReferenceReconciler Instance { get; } = new();

    private PreserveAllStoredFileReferenceReconciler()
    {
    }

    public Task DeleteUnreferencedAsync(
        IEnumerable<string> candidatePaths,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<PaymentAttachment?> FindPaymentAttachmentAsync(
        Guid attachmentId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<PaymentAttachment?>(null);
}

public sealed class StoredFileReferenceReconciler(
    IServiceScopeFactory serviceScopeFactory,
    ICentralFileStorage fileStorage,
    ITenantDatabaseConnectionResolver connectionResolver,
    RevisionClock revisionClock,
    ILogger<StoredFileReferenceReconciler>? logger = null) : IStoredFileReferenceReconciler
{
    private static readonly StringComparer StoredPathComparer =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    public async Task DeleteUnreferencedAsync(
        IEnumerable<string> candidatePaths,
        CancellationToken cancellationToken = default)
    {
        _ = await DeleteUnreferencedWithOutcomeAsync(
            candidatePaths,
            cancellationToken);
    }

    public async Task<StoredFileReconcileOutcome>
        DeleteUnreferencedWithOutcomeAsync(
            IEnumerable<string> candidatePaths,
            CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var paths = candidatePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim())
            .Distinct(StoredPathComparer)
            .ToList();
        if (paths.Count == 0)
            return StoredFileReconcileOutcome.Completed;

        StoredFileDeletionLease? deletionLease;
        try
        {
            deletionLease = StoredFileDeletionLease.TryAcquireShared(fileStorage.RootPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(
                "Stored-file cleanup was skipped because the deletion lease could not be acquired. errorType={ErrorType}",
                ex.GetType().Name);
            return StoredFileReconcileOutcome.LeaseDeferred;
        }

        if (deletionLease is null)
        {
            // The host backup owns the exclusive lease. Skipping physical
            // cleanup is safe and keeps API responses from waiting on a
            // potentially long database-and-file backup window.
            logger?.LogInformation(
                "Stored-file cleanup was deferred because a backup owns the exclusive deletion lease.");
            return StoredFileReconcileOutcome.LeaseDeferred;
        }

        using (deletionLease)
        {
            HashSet<string> referencedPaths;
            try
            {
                referencedPaths = new HashSet<string>(StoredPathComparer);
                foreach (var connectionInfo in GetDistinctPhysicalConnections())
                {
                    await using var scope = serviceScopeFactory.CreateAsyncScope();
                    var currentUser = scope.ServiceProvider.GetRequiredService<ICurrentUserContext>();
                    await using var dbContext = CreateDbContext(connectionInfo, currentUser);

                    await AddReferencedPathsAsync(
                        dbContext.CustomerContracts
                            .IgnoreQueryFilters()
                            .AsNoTracking()
                            .Where(contract => contract.StoragePath != null)
                            .Select(contract => contract.StoragePath!),
                        paths,
                        referencedPaths,
                        cancellationToken);
                    await AddReferencedPathsAsync(
                        dbContext.TransactionAttachments
                            .IgnoreQueryFilters()
                            .AsNoTracking()
                            .Where(attachment => attachment.StoragePath != null)
                            .Select(attachment => attachment.StoragePath!),
                        paths,
                        referencedPaths,
                        cancellationToken);
                    await AddReferencedPathsAsync(
                        dbContext.PaymentAttachments
                            .IgnoreQueryFilters()
                            .AsNoTracking()
                            .Where(attachment => attachment.StoragePath != null)
                            .Select(attachment => attachment.StoragePath!),
                        paths,
                        referencedPaths,
                        cancellationToken);
                    await AddReferencedPathsAsync(
                        dbContext.InventoryTransfers
                            .IgnoreQueryFilters()
                            .AsNoTracking()
                            .Where(transfer => transfer.ReceiveEvidencePath != null)
                            .Select(transfer => transfer.ReceiveEvidencePath!),
                        paths,
                        referencedPaths,
                        cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A cleanup decision is safe only when every independent reference
                // lookup succeeded. Preserve every candidate on an inconclusive read.
                logger?.LogWarning(
                    "Stored-file cleanup was skipped because a cross-database reference lookup was inconclusive. errorType={ErrorType}",
                    ex.GetType().Name);
                return StoredFileReconcileOutcome.LookupInconclusive;
            }

            var deletionIncomplete = false;
            foreach (var path in paths.Where(
                         path => !referencedPaths.Contains(path)))
            {
                try
                {
                    fileStorage.DeleteIfExists(path);
                    var inspection = fileStorage.Inspect(path);
                    if (inspection.Exists ||
                        !inspection.IsSafePath ||
                        (!string.IsNullOrWhiteSpace(inspection.Error) &&
                         !string.Equals(
                             inspection.Error,
                             "stored_file_not_found",
                             StringComparison.Ordinal)))
                    {
                        deletionIncomplete = true;
                    }
                }
                catch
                {
                    // Best-effort cleanup must never replace the original operation result.
                    deletionIncomplete = true;
                }
            }

            if (deletionIncomplete)
                return StoredFileReconcileOutcome.DeletionIncomplete;
        }

        return StoredFileReconcileOutcome.Completed;
    }

    private static async Task AddReferencedPathsAsync(
        IQueryable<string> storedPaths,
        IReadOnlyCollection<string> candidatePaths,
        HashSet<string> referencedPaths,
        CancellationToken cancellationToken)
    {
        List<string> matches;
        if (OperatingSystem.IsWindows())
        {
            var foldedCandidates = candidatePaths
                .Select(path => path.ToUpperInvariant())
                .Distinct(StringComparer.Ordinal)
                .ToList();
            matches = await storedPaths
                .Where(path => foldedCandidates.Contains(path.ToUpper()))
                .ToListAsync(cancellationToken);
        }
        else
        {
            var exactCandidates = candidatePaths.ToList();
            matches = await storedPaths
                .Where(path => exactCandidates.Contains(path))
                .ToListAsync(cancellationToken);
        }

        foreach (var path in matches)
        {
            if (candidatePaths.Contains(path, StoredPathComparer))
                referencedPaths.Add(path);
        }
    }

    public async Task<PaymentAttachment?> FindPaymentAttachmentAsync(
        Guid attachmentId,
        CancellationToken cancellationToken = default)
    {
        if (attachmentId == Guid.Empty)
            return null;

        try
        {
            await using var scope = serviceScopeFactory.CreateAsyncScope();
            var currentUser = scope.ServiceProvider.GetRequiredService<ICurrentUserContext>();
            await using var dbContext = CreateDbContext(connectionResolver.ResolveCurrent(), currentUser);
            return await dbContext.PaymentAttachments
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    attachment => attachment.Id == attachmentId,
                    cancellationToken);
        }
        catch
        {
            // A failed independent read cannot prove that the transaction lost.
            return null;
        }
    }

    private IReadOnlyList<TenantDatabaseConnectionInfo> GetDistinctPhysicalConnections()
    {
        var configuredConnections = new List<TenantDatabaseConnectionInfo>
        {
            connectionResolver.ResolveCentral()
        };
        configuredConnections.AddRange(connectionResolver.GetDedicatedBusinessConnections());

        var distinctConnections = new Dictionary<string, TenantDatabaseConnectionInfo>(
            StringComparer.Ordinal);
        foreach (var connectionInfo in configuredConnections)
        {
            var identity = PhysicalDatabaseIdentity.FromConnectionInfo(connectionInfo);
            distinctConnections.TryAdd(identity, connectionInfo);
        }

        return distinctConnections.Values.ToList();
    }

    private AppDbContext CreateDbContext(
        TenantDatabaseConnectionInfo connectionInfo,
        ICurrentUserContext currentUser)
    {
        if (string.IsNullOrWhiteSpace(connectionInfo.ConnectionString))
            throw new InvalidOperationException("Database connection string is empty.");

        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        if (connectionInfo.UseSqlite)
        {
            optionsBuilder.UseSqlite(
                connectionInfo.ConnectionString,
                sqliteOptions => sqliteOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery));
        }
        else
        {
            optionsBuilder.UseNpgsql(
                connectionInfo.ConnectionString,
                npgsqlOptions => npgsqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery));
        }

        return new AppDbContext(optionsBuilder.Options, currentUser, revisionClock);
    }

}
