using 거래플랜.Desktop.App.Data;
using Microsoft.EntityFrameworkCore;

namespace 거래플랜.Desktop.App.Services;

internal static class LocalEntityConcurrencyGuard
{
    public static async Task<TEntity?> ReloadTrackedEntityAsync<TEntity>(
        LocalDbContext db,
        TEntity? existing,
        CancellationToken ct = default)
        where TEntity : class, ILocalSyncEntity
    {
        if (existing is null)
            return null;

        var entry = db.Entry(existing);
        if (entry.State == EntityState.Detached)
            return existing;

        await entry.ReloadAsync(ct);
        return entry.State == EntityState.Detached ? null : existing;
    }

    public static async Task TryRebaseCandidateRevisionFromAcknowledgedLocalMutationAsync<TEntity>(
        LocalDbContext db,
        TEntity candidate,
        TEntity? existing,
        CancellationToken ct = default)
        where TEntity : class, ILocalSyncEntity
    {
        if (existing is null ||
            existing.IsDirty ||
            candidate.Revision <= 0 ||
            existing.Revision <= 0 ||
            candidate.Revision == existing.Revision ||
            candidate.Revision > existing.Revision)
        {
            return;
        }

        var acknowledged = await db.SyncOutboxEntries
            .AsNoTracking()
            .Where(entry =>
                entry.EntityName == typeof(TEntity).Name &&
                entry.EntityId == candidate.Id &&
                entry.ExpectedRevision == candidate.Revision &&
                entry.Status == "Acknowledged" &&
                entry.AcknowledgedAtUtc.HasValue)
            .OrderByDescending(entry => entry.AcknowledgedAtUtc)
            .FirstOrDefaultAsync(ct);

        if (acknowledged is null ||
            acknowledged.AcceptedRevision <= 0 ||
            acknowledged.AcceptedUpdatedAtUtc is not DateTime acceptedUpdatedAtUtc)
            return;

        // The local row can be refreshed by the server pull after the same PC's
        // previous save. In that case the editor still carries the old baseline
        // revision. Rebase only when both the revision and server timestamp are
        // the exact accepted identity recorded for that mutation. A later edit
        // from another PC must keep surfacing as a conflict even when it arrives
        // immediately after this PC's acknowledgement.
        if (existing.Revision == acknowledged.AcceptedRevision &&
            NormalizeUtc(existing.UpdatedAtUtc) == NormalizeUtc(acceptedUpdatedAtUtc))
            candidate.Revision = existing.Revision;
    }

    private static DateTime NormalizeUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => value
        };

    public static bool TryPrepareForSave<TEntity>(
        TEntity candidate,
        TEntity? existing,
        string entityDisplayName,
        DateTime now,
        out string conflictMessage)
        where TEntity : class, ILocalSyncEntity
    {
        if (existing is not null &&
            candidate.Revision > 0 &&
            existing.Revision > 0 &&
            candidate.Revision != existing.Revision)
        {
            if (candidate.Revision > existing.Revision && !existing.IsDirty)
            {
                candidate.Revision = existing.Revision;
            }
            else
            {
                conflictMessage = BuildConflictMessage(entityDisplayName, candidate.Revision, existing.Revision);
                return false;
            }
        }

        candidate.CreatedAtUtc = existing?.CreatedAtUtc ?? (candidate.CreatedAtUtc == default ? now : candidate.CreatedAtUtc);
        candidate.UpdatedAtUtc = now;
        candidate.Revision = existing?.Revision ?? Math.Max(0, candidate.Revision);
        candidate.IsDirty = true;
        conflictMessage = string.Empty;
        return true;
    }

    public static bool TryEnsureDeleteAllowed<TEntity>(
        TEntity? existing,
        long? expectedRevision,
        string entityDisplayName,
        out string conflictMessage)
        where TEntity : class, ILocalSyncEntity
        => TryEnsureOperationAllowed(existing, expectedRevision, entityDisplayName, out conflictMessage);

    public static bool TryEnsureOperationAllowed<TEntity>(
        TEntity? existing,
        long? expectedRevision,
        string entityDisplayName,
        out string conflictMessage)
        where TEntity : class, ILocalSyncEntity
    {
        if (existing is not null &&
            expectedRevision.HasValue &&
            expectedRevision.Value > 0 &&
            existing.Revision > 0 &&
            existing.Revision != expectedRevision.Value)
        {
            conflictMessage = BuildConflictMessage(entityDisplayName, expectedRevision, existing.Revision);
            return false;
        }

        conflictMessage = string.Empty;
        return true;
    }

    public static string BuildConflictMessage(string entityDisplayName, long? expectedRevision = null, long? currentRevision = null)
        => ConcurrencyConflictFormatter.BuildMessage(entityDisplayName, expectedRevision, currentRevision);
}
