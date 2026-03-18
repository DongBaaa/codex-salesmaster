using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;

namespace 거래플랜.Desktop.App.Services;

/// <summary>
/// Tracks recently used customer/item selections for quick re-selection.
/// </summary>
public sealed class RecentSelectionService
{
    private readonly LocalDbContext _db;
    private const int MaxRecent = 20;

    public RecentSelectionService(LocalDbContext db) => _db = db;

    public async Task RecordAsync(string entityType, Guid entityId, string displayText, CancellationToken ct = default)
    {
        var existing = await _db.RecentSelections
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.EntityType == entityType && r.EntityId == entityId, ct);

        if (existing is not null)
        {
            existing.LastUsedAtUtc = DateTime.UtcNow;
            existing.DisplayText = displayText;
        }
        else
        {
            _db.RecentSelections.Add(new LocalRecentSelection
            {
                EntityType = entityType,
                EntityId = entityId,
                DisplayText = displayText
            });

            // Trim to MaxRecent
            var count = await _db.RecentSelections
                .IgnoreQueryFilters()
                .Where(r => r.EntityType == entityType && !r.IsFavorite)
                .CountAsync(ct);

            if (count > MaxRecent)
            {
                var oldest = await _db.RecentSelections
                    .IgnoreQueryFilters()
                    .Where(r => r.EntityType == entityType && !r.IsFavorite)
                    .OrderBy(r => r.LastUsedAtUtc)
                    .FirstAsync(ct);
                _db.RecentSelections.Remove(oldest);
            }
        }

        await _db.SaveChangesAsync(ct);
    }

    public Task<List<LocalRecentSelection>> GetRecentAsync(string entityType, CancellationToken ct = default)
        => _db.RecentSelections
            .IgnoreQueryFilters()
            .Where(r => r.EntityType == entityType)
            .OrderByDescending(r => r.IsFavorite)
            .ThenByDescending(r => r.LastUsedAtUtc)
            .Take(MaxRecent)
            .ToListAsync(ct);
}
