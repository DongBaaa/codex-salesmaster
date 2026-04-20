using 거래플랜.Desktop.App.Data;

namespace 거래플랜.Desktop.App.Services;

internal static class LocalEntityConcurrencyGuard
{
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
