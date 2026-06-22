using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;

namespace 거래플랜.Desktop.App.Services;

public sealed partial class LocalStateService
{
    public async Task<IReadOnlyList<SyncOutboxListItem>> GetSyncOutboxEntriesAsync(int take = 200, CancellationToken ct = default)
    {
        var normalizedTake = Math.Clamp(take, 20, 500);
        var rows = await _db.SyncOutboxEntries
            .AsNoTracking()
            .OrderByDescending(entry => entry.PreparedAtUtc)
            .Take(normalizedTake)
            .ToListAsync(ct);

        return rows
            .OrderBy(entry => GetOutboxStatusWeight(entry.Status))
            .ThenByDescending(entry => entry.PreparedAtUtc)
            .Select(ToSyncOutboxListItem)
            .ToList();
    }

    public async Task<IReadOnlyList<SyncOutboxListItem>> GetSyncOutboxEntriesAsync(
        SessionState session,
        int take = 200,
        CancellationToken ct = default)
    {
        if (session is null || !session.IsLoggedIn)
            return await GetSyncOutboxEntriesAsync(take, ct);

        var normalizedTake = Math.Clamp(take, 20, 500);
        var rows = await _db.SyncOutboxEntries
            .AsNoTracking()
            .OrderByDescending(entry => entry.PreparedAtUtc)
            .ToListAsync(ct);

        return rows
            .Where(entry => CanCurrentSessionAccessOutboxEntry(entry, session))
            .OrderBy(entry => GetOutboxStatusWeight(entry.Status))
            .ThenByDescending(entry => entry.PreparedAtUtc)
            .Take(normalizedTake)
            .Select(ToSyncOutboxListItem)
            .ToList();
    }

    public async Task<SyncOutboxSummary> GetSyncOutboxSummaryAsync(CancellationToken ct = default)
    {
        var totalCount = await _db.SyncOutboxEntries.AsNoTracking().CountAsync(ct);
        var acknowledgedCount = await _db.SyncOutboxEntries
            .AsNoTracking()
            .CountAsync(entry => entry.Status == "Acknowledged", ct);
        var failedCount = await _db.SyncOutboxEntries
            .AsNoTracking()
            .CountAsync(entry => entry.Status == "Failed", ct);
        var pendingCount = await _db.SyncOutboxEntries
            .AsNoTracking()
            .CountAsync(entry => entry.Status != "Acknowledged" && entry.Status != "Failed", ct);
        return new SyncOutboxSummary(totalCount, pendingCount, failedCount, acknowledgedCount);
    }

    public async Task<SyncOutboxSummary> GetSyncOutboxSummaryAsync(SessionState session, CancellationToken ct = default)
    {
        if (session is null || !session.IsLoggedIn)
            return await GetSyncOutboxSummaryAsync(ct);

        var rows = (await _db.SyncOutboxEntries
                .AsNoTracking()
                .ToListAsync(ct))
            .Where(entry => CanCurrentSessionAccessOutboxEntry(entry, session))
            .ToList();

        return new SyncOutboxSummary(
            rows.Count,
            rows.Count(entry => entry.Status != "Acknowledged" && entry.Status != "Failed"),
            rows.Count(entry => entry.Status == "Failed"),
            rows.Count(entry => entry.Status == "Acknowledged"));
    }

    public async Task<int> ResetSyncOutboxEntriesForRetryAsync(IEnumerable<Guid> entryIds, CancellationToken ct = default)
    {
        var ids = entryIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
            return 0;

        var rows = await _db.SyncOutboxEntries
            .Where(entry => ids.Contains(entry.Id) && entry.Status != "Acknowledged")
            .ToListAsync(ct);
        foreach (var row in rows)
        {
            row.Status = "Prepared";
            row.ErrorMessage = string.Empty;
            row.SentAtUtc = null;
            row.AcknowledgedAtUtc = null;
        }

        if (rows.Count == 0)
            return 0;

        await _db.SaveChangesAsync(ct);
        return rows.Count;
    }

    public async Task<int> ResetSyncOutboxEntriesForRetryAsync(
        IEnumerable<Guid> entryIds,
        SessionState session,
        CancellationToken ct = default)
    {
        if (session is null || !session.IsLoggedIn)
            return await ResetSyncOutboxEntriesForRetryAsync(entryIds, ct);

        var ids = entryIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
            return 0;

        var rows = (await _db.SyncOutboxEntries
                .Where(entry => ids.Contains(entry.Id) && entry.Status != "Acknowledged")
                .ToListAsync(ct))
            .Where(entry => CanCurrentSessionAccessOutboxEntry(entry, session))
            .ToList();
        foreach (var row in rows)
        {
            row.Status = "Prepared";
            row.ErrorMessage = string.Empty;
            row.SentAtUtc = null;
            row.AcknowledgedAtUtc = null;
        }

        if (rows.Count == 0)
            return 0;

        await _db.SaveChangesAsync(ct);
        return rows.Count;
    }

    public async Task<int> ResetAllPendingSyncOutboxEntriesForRetryAsync(CancellationToken ct = default)
    {
        var rows = await _db.SyncOutboxEntries
            .Where(entry => entry.Status != "Acknowledged")
            .ToListAsync(ct);
        foreach (var row in rows)
        {
            row.Status = "Prepared";
            row.ErrorMessage = string.Empty;
            row.SentAtUtc = null;
            row.AcknowledgedAtUtc = null;
        }

        if (rows.Count == 0)
            return 0;

        await _db.SaveChangesAsync(ct);
        return rows.Count;
    }

    public async Task<int> ResetAllPendingSyncOutboxEntriesForRetryAsync(
        SessionState session,
        CancellationToken ct = default)
    {
        if (session is null || !session.IsLoggedIn)
            return await ResetAllPendingSyncOutboxEntriesForRetryAsync(ct);

        var rows = (await _db.SyncOutboxEntries
                .Where(entry => entry.Status != "Acknowledged")
                .ToListAsync(ct))
            .Where(entry => CanCurrentSessionAccessOutboxEntry(entry, session))
            .ToList();
        foreach (var row in rows)
        {
            row.Status = "Prepared";
            row.ErrorMessage = string.Empty;
            row.SentAtUtc = null;
            row.AcknowledgedAtUtc = null;
        }

        if (rows.Count == 0)
            return 0;

        await _db.SaveChangesAsync(ct);
        return rows.Count;
    }

    public async Task<int> ClearAcknowledgedSyncOutboxEntriesAsync(CancellationToken ct = default)
    {
        var rows = await _db.SyncOutboxEntries
            .Where(entry => entry.Status == "Acknowledged")
            .ToListAsync(ct);
        if (rows.Count == 0)
            return 0;

        _db.SyncOutboxEntries.RemoveRange(rows);
        await _db.SaveChangesAsync(ct);
        return rows.Count;
    }

    public async Task<int> ClearAcknowledgedSyncOutboxEntriesAsync(
        SessionState session,
        CancellationToken ct = default)
    {
        if (session is null || !session.IsLoggedIn)
            return await ClearAcknowledgedSyncOutboxEntriesAsync(ct);

        var rows = (await _db.SyncOutboxEntries
                .Where(entry => entry.Status == "Acknowledged")
                .ToListAsync(ct))
            .Where(entry => CanCurrentSessionAccessOutboxEntry(entry, session))
            .ToList();
        if (rows.Count == 0)
            return 0;

        _db.SyncOutboxEntries.RemoveRange(rows);
        await _db.SaveChangesAsync(ct);
        return rows.Count;
    }

    internal async Task<int> MarkSyncOutboxFailedAsync(IEnumerable<string> mutationIds, string? errorMessage, CancellationToken ct = default)
    {
        var ids = mutationIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ids.Count == 0)
            return 0;

        var rows = await _db.SyncOutboxEntries
            .Where(entry => ids.Contains(entry.MutationId))
            .ToListAsync(ct);
        if (rows.Count == 0)
            return 0;

        var normalizedError = NormalizeOutboxErrorMessage(errorMessage);
        foreach (var row in rows)
        {
            row.Status = "Failed";
            row.ErrorMessage = normalizedError;
            row.AcknowledgedAtUtc = null;
        }

        await _db.SaveChangesAsync(ct);
        return rows.Count;
    }

    private static SyncOutboxListItem ToSyncOutboxListItem(LocalSyncOutboxEntry entry)
        => new()
        {
            Id = entry.Id,
            MutationId = entry.MutationId,
            DeviceId = entry.DeviceId,
            EntityName = entry.EntityName,
            EntityId = entry.EntityId,
            ExpectedRevision = entry.ExpectedRevision,
            TenantCode = entry.TenantCode,
            OfficeCode = entry.OfficeCode,
            ResponsibleOfficeCode = entry.ResponsibleOfficeCode,
            Status = entry.Status,
            ErrorMessage = entry.ErrorMessage,
            PreparedAtUtc = entry.PreparedAtUtc,
            SentAtUtc = entry.SentAtUtc,
            AcknowledgedAtUtc = entry.AcknowledgedAtUtc
        };

    private static int GetOutboxStatusWeight(string? status)
        => string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase) ? 0
            : string.Equals(status, "Prepared", StringComparison.OrdinalIgnoreCase) ? 1
            : string.Equals(status, "Sent", StringComparison.OrdinalIgnoreCase) ? 2
            : string.Equals(status, "Acknowledged", StringComparison.OrdinalIgnoreCase) ? 3
            : 4;

    private static bool CanCurrentSessionAccessOutboxEntry(LocalSyncOutboxEntry entry, SessionState session)
    {
        var scope = ResolveScope(
            string.IsNullOrWhiteSpace(entry.ResponsibleOfficeCode)
                ? entry.OfficeCode
                : entry.ResponsibleOfficeCode,
            entry.TenantCode);
        var bucket = new PendingSyncBucket(
            scope.ScopeKey,
            scope.ScopeDisplayName,
            "동기화 전송 확인",
            1);
        return IsCurrentSessionPendingBucket(bucket, session);
    }

    private static string NormalizeOutboxErrorMessage(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return "동기화 확인이 필요한 상태가 발생했습니다.";

        return normalized.Length <= 500
            ? normalized
            : normalized[..500];
    }
}
