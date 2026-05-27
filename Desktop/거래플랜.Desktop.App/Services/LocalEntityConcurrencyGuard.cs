using 거래플랜.Desktop.App.Data;
using Microsoft.EntityFrameworkCore;

namespace 거래플랜.Desktop.App.Services;

internal static class LocalEntityConcurrencyGuard
{
    private static readonly TimeSpan AcknowledgedMutationRebaseTolerance = TimeSpan.FromMinutes(2);

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

        if (acknowledged?.AcknowledgedAtUtc is not DateTime acknowledgedAtUtc)
            return;

        // The local row can be refreshed by the server pull after the same PC's
        // previous save. In that case the editor still carries the old baseline
        // revision, but the clean local row already has the acknowledged server
        // revision. Rebase only when the server timestamp is close to the local
        // acknowledgement so real later edits from another PC still surface as
        // conflicts.
        if (existing.UpdatedAtUtc <= acknowledgedAtUtc.Add(AcknowledgedMutationRebaseTolerance))
            candidate.Revision = existing.Revision;
    }

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
            conflictMessage = BuildConflictMessage(entityDisplayName, candidate.Revision, existing.Revision);
            return false;
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
