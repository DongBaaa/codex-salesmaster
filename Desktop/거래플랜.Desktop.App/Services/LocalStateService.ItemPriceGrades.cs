using 거래플랜.Desktop.App.Data;
using 거래플랜.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace 거래플랜.Desktop.App.Services;

public sealed partial class LocalStateService
{
    public async Task<List<LocalItemPriceGrade>> GetItemPriceGradesAsync(SessionState session, CancellationToken ct = default)
    {
        var itemIds = ApplyItemScope(_db.Items.AsNoTracking(), session).Select(item => item.Id);
        return await _db.ItemPriceGrades
            .AsNoTracking()
            .Where(price => itemIds.Contains(price.ItemId) && price.IsActive)
            .OrderBy(price => price.PriceGradeName)
            .ThenBy(price => price.ItemId)
            .ToListAsync(ct);
    }

    public async Task<List<LocalItemPriceGrade>> GetItemPriceGradesForItemAsync(Guid itemId, CancellationToken ct = default)
    {
        if (itemId == Guid.Empty)
            return new List<LocalItemPriceGrade>();

        return await _db.ItemPriceGrades
            .AsNoTracking()
            .Where(price => price.ItemId == itemId && price.IsActive)
            .OrderBy(price => price.PriceGradeName)
            .ToListAsync(ct);
    }

    public async Task<List<LocalItemPriceGrade>> GetDirtyItemPriceGradesForSyncAsync(SessionState session, CancellationToken ct = default)
    {
        if (!CanEditItems(session))
            return new List<LocalItemPriceGrade>();

        var rows = await _db.ItemPriceGrades
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(price => price.IsDirty)
            .ToListAsync(ct);
        if (rows.Count == 0)
            return rows;

        if (CanWriteAllScopedData(session))
            return rows;

        var itemIds = rows.Select(row => row.ItemId).Distinct().ToArray();
        var items = await _db.Items
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item => itemIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, ct);

        return rows
            .Where(row => items.TryGetValue(row.ItemId, out var item) && CanWriteItemScope(item, session))
            .ToList();
    }

    public async Task SaveItemPriceGradesForItemAsync(Guid itemId, IEnumerable<LocalItemPriceGrade>? priceGrades, CancellationToken ct = default)
    {
        if (itemId == Guid.Empty || priceGrades is null)
            return;

        var normalizedRows = priceGrades
            .Where(row => row.PriceGradeOptionId != Guid.Empty)
            .Select(row => new LocalItemPriceGrade
            {
                Id = row.Id == Guid.Empty ? Guid.NewGuid() : row.Id,
                ItemId = itemId,
                PriceGradeOptionId = row.PriceGradeOptionId,
                PriceGradeName = (row.PriceGradeName ?? string.Empty).Trim(),
                UnitPrice = row.UnitPrice,
                IsActive = row.IsActive,
                Revision = row.Revision,
                CreatedAtUtc = row.CreatedAtUtc,
                UpdatedAtUtc = row.UpdatedAtUtc,
                IsDeleted = row.IsDeleted
            })
            .GroupBy(row => row.PriceGradeOptionId)
            .Select(group => group.Last())
            .ToList();

        if (normalizedRows.Any(row => row.UnitPrice < 0m))
            throw new InvalidOperationException("등급단가는 0 이상으로 입력하세요.");

        var optionIds = normalizedRows.Select(row => row.PriceGradeOptionId).Distinct().ToArray();
        var optionNames = await _db.PriceGradeOptions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(option => optionIds.Contains(option.Id))
            .ToDictionaryAsync(option => option.Id, option => (option.Name ?? string.Empty).Trim(), ct);

        var existingRows = await _db.ItemPriceGrades
            .IgnoreQueryFilters()
            .Where(row => row.ItemId == itemId)
            .ToListAsync(ct);
        var existingByOption = existingRows.ToDictionary(row => row.PriceGradeOptionId);
        var now = DateTime.UtcNow;
        var incomingOptionIds = normalizedRows.Select(row => row.PriceGradeOptionId).ToHashSet();

        foreach (var incoming in normalizedRows)
        {
            var priceGradeName = optionNames.TryGetValue(incoming.PriceGradeOptionId, out var optionName) && !string.IsNullOrWhiteSpace(optionName)
                ? optionName
                : incoming.PriceGradeName;
            if (string.IsNullOrWhiteSpace(priceGradeName))
                continue;

            if (!existingByOption.TryGetValue(incoming.PriceGradeOptionId, out var existing))
            {
                _db.ItemPriceGrades.Add(new LocalItemPriceGrade
                {
                    Id = incoming.Id == Guid.Empty ? Guid.NewGuid() : incoming.Id,
                    ItemId = itemId,
                    PriceGradeOptionId = incoming.PriceGradeOptionId,
                    PriceGradeName = priceGradeName,
                    UnitPrice = incoming.UnitPrice,
                    IsActive = incoming.IsActive,
                    IsDeleted = false,
                    IsDirty = true,
                    CreatedAtUtc = incoming.CreatedAtUtc == default ? now : incoming.CreatedAtUtc,
                    UpdatedAtUtc = now
                });
                continue;
            }

            existing.PriceGradeName = priceGradeName;
            existing.UnitPrice = incoming.UnitPrice;
            existing.IsActive = incoming.IsActive;
            existing.IsDeleted = false;
            existing.IsDirty = true;
            existing.UpdatedAtUtc = now;
        }

        foreach (var stale in existingRows.Where(row => !incomingOptionIds.Contains(row.PriceGradeOptionId)))
        {
            if (stale.IsDeleted)
                continue;

            stale.IsDeleted = true;
            stale.IsActive = false;
            stale.IsDirty = true;
            stale.UpdatedAtUtc = now;
        }

        await _db.SaveChangesAsync(ct);
    }

    private async Task MarkItemPriceGradesDeletedAsync(Guid itemId, DateTime now, CancellationToken ct)
    {
        var rows = await _db.ItemPriceGrades
            .IgnoreQueryFilters()
            .Where(row => row.ItemId == itemId && !row.IsDeleted)
            .ToListAsync(ct);

        foreach (var row in rows)
        {
            row.IsDeleted = true;
            row.IsActive = false;
            row.IsDirty = true;
            row.UpdatedAtUtc = now;
        }
    }
}
