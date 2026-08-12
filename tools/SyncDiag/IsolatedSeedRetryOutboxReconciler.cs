using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;

namespace GeoraePlan.Tools.SyncDiag;

internal static class IsolatedSeedRetryOutboxReconciler
{
    internal static async Task<int> SupersedeUniqueSentOutboxForDirtyEntitiesAsync<TEntity>(
        LocalDbContext db,
        DateTime supersedeAtUtc,
        CancellationToken ct = default)
        where TEntity : class, ILocalSyncEntity
    {
        if (supersedeAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Supersede time must be UTC.", nameof(supersedeAtUtc));
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("An active database transaction is required.");

        var entityName = typeof(TEntity).Name;
        var sentRows = await db.SyncOutboxEntries
            .AsNoTracking()
            .Where(entry =>
                entry.EntityName == entityName &&
                entry.Status == "Sent")
            .ToListAsync(ct);
        var singleRows = sentRows
            .Where(IsStructurallyComplete)
            .GroupBy(entry => entry.EntityId)
            .Where(group => group.Key != Guid.Empty && group.Count() == 1)
            .Select(group => group.Single())
            .ToList();
        if (singleRows.Count == 0)
            return 0;

        var candidateIds = singleRows
            .Select(entry => entry.EntityId)
            .ToList();
        var dirtyEntities = await db.Set<TEntity>()
            .IgnoreQueryFilters()
            .Where(entity =>
                candidateIds.Contains(entity.Id) &&
                entity.IsDirty)
            .ToDictionaryAsync(entity => entity.Id, ct);

        var removed = 0;
        foreach (var row in singleRows)
        {
            if (!dirtyEntities.TryGetValue(row.EntityId, out var entity))
                continue;

            var deleted = await DeleteExactOutboxRowAsync(db, row, ct);
            if (deleted != 1)
                throw new InvalidOperationException("The exact sent outbox receipt changed during supersession.");

            var currentUpdatedAtUtc = NormalizeMutationUtc(entity.UpdatedAtUtc);
            entity.UpdatedAtUtc = supersedeAtUtc > currentUpdatedAtUtc
                ? supersedeAtUtc
                : currentUpdatedAtUtc < DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc)
                    ? currentUpdatedAtUtc.AddTicks(1)
                    : throw new InvalidOperationException("The dirty entity timestamp cannot be advanced.");
            entity.IsDirty = true;
            removed++;
        }

        if (removed > 0)
            await db.SaveChangesAsync(ct);

        return removed;
    }

    internal static async Task<int> RemoveExactFailedOutboxForDirtyEntitiesAsync<TEntity>(
        LocalDbContext db,
        CancellationToken ct = default)
        where TEntity : class, ILocalSyncEntity
    {
        var entityName = typeof(TEntity).Name;
        var failedRows = await db.SyncOutboxEntries
            .AsNoTracking()
            .Where(entry =>
                entry.EntityName == entityName &&
                entry.Status == "Failed")
            .ToListAsync(ct);
        var singleRows = failedRows
            .Where(IsStructurallyComplete)
            .GroupBy(entry => entry.EntityId)
            .Where(group => group.Key != Guid.Empty && group.Count() == 1)
            .Select(group => group.Single())
            .ToList();
        if (singleRows.Count == 0)
            return 0;

        var candidateIds = singleRows
            .Select(entry => entry.EntityId)
            .ToList();
        var dirtyEntities = await db.Set<TEntity>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(entity =>
                candidateIds.Contains(entity.Id) &&
                entity.IsDirty)
            .ToDictionaryAsync(entity => entity.Id, ct);

        var removed = 0;
        foreach (var row in singleRows)
        {
            if (!dirtyEntities.TryGetValue(row.EntityId, out var entity) ||
                row.ExpectedRevision != entity.Revision ||
                !string.Equals(
                    row.MutationId,
                    BuildExpectedMutationId(row.DeviceId, entityName, entity),
                    StringComparison.Ordinal))
            {
                continue;
            }

            removed += await DeleteExactOutboxRowAsync(db, row, ct);
        }

        return removed;
    }

    internal static string BuildExpectedMutationId(
        string deviceId,
        string entityName,
        ILocalSyncEntity entity)
    {
        var updatedAtTicks = NormalizeMutationUtc(entity.UpdatedAtUtc).Ticks;
        return $"{deviceId}:{entityName}:{entity.Id:N}:{entity.Revision}:" +
               $"{updatedAtTicks}:{(entity.IsDeleted ? 1 : 0)}";
    }

    internal static async Task<int> RemoveCleanOutboxAsync<TEntity>(
        LocalDbContext db)
        where TEntity : class, ILocalSyncEntity
    {
        var entityName = typeof(TEntity).Name;
        var pendingEntityIds = await db.SyncOutboxEntries
            .Where(entry =>
                entry.EntityName == entityName &&
                entry.Status != "Acknowledged")
            .Select(entry => entry.EntityId)
            .Distinct()
            .ToListAsync();
        if (pendingEntityIds.Count == 0)
            return 0;

        var cleanEntityIds = await db.Set<TEntity>()
            .IgnoreQueryFilters()
            .Where(entity =>
                pendingEntityIds.Contains(entity.Id) &&
                !entity.IsDirty)
            .Select(entity => entity.Id)
            .ToListAsync();
        if (cleanEntityIds.Count == 0)
            return 0;

        return await db.SyncOutboxEntries
            .Where(entry =>
                entry.EntityName == entityName &&
                entry.Status != "Acknowledged" &&
                cleanEntityIds.Contains(entry.EntityId))
            .ExecuteDeleteAsync();
    }

    private static bool IsStructurallyComplete(LocalSyncOutboxEntry entry)
        => entry.Id != Guid.Empty &&
           entry.EntityId != Guid.Empty &&
           entry.SessionId != Guid.Empty &&
           entry.UserId != Guid.Empty &&
           entry.ExpectedRevision >= 0 &&
           !string.IsNullOrWhiteSpace(entry.MutationId) &&
           !string.IsNullOrWhiteSpace(entry.DeviceId) &&
           !string.IsNullOrWhiteSpace(entry.BusinessDatabaseName) &&
           !string.IsNullOrWhiteSpace(entry.TenantCode) &&
           !string.IsNullOrWhiteSpace(entry.OfficeCode) &&
           !string.IsNullOrWhiteSpace(entry.ResponsibleOfficeCode);

    private static async Task<int> DeleteExactOutboxRowAsync(
        LocalDbContext db,
        LocalSyncOutboxEntry row,
        CancellationToken ct)
        => await db.SyncOutboxEntries
            .Where(entry =>
                entry.Id == row.Id &&
                entry.EntityName == row.EntityName &&
                entry.EntityId == row.EntityId &&
                entry.Status == row.Status &&
                entry.MutationId == row.MutationId &&
                entry.DeviceId == row.DeviceId &&
                entry.ExpectedRevision == row.ExpectedRevision &&
                entry.BusinessDatabaseName == row.BusinessDatabaseName &&
                entry.TenantCode == row.TenantCode &&
                entry.OfficeCode == row.OfficeCode &&
                entry.ResponsibleOfficeCode == row.ResponsibleOfficeCode &&
                entry.SessionId == row.SessionId &&
                entry.UserId == row.UserId)
            .ExecuteDeleteAsync(ct);

    private static DateTime NormalizeMutationUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
}
