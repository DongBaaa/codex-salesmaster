using 거래플랜.Server.Api.Data;
using 거래플랜.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace 거래플랜.Server.Api.Services;

public sealed record UnitDeletionReferenceSummary(
    int ItemCount,
    int InvoiceLineCount,
    int InventoryTransferLineCount)
{
    public bool HasReferences => ItemCount > 0 || InvoiceLineCount > 0 || InventoryTransferLineCount > 0;
}

public static class UnitDeletionReferenceGuard
{
    public const string ConflictCode = "unit_delete_blocked_by_references";

    public static async Task<UnitDeletionReferenceSummary> GetReferenceSummaryAsync(
        AppDbContext dbContext,
        string? unitName,
        CancellationToken cancellationToken)
    {
        var normalizedUnit = UnitCatalogNormalizer.Normalize(unitName);
        if (string.IsNullOrWhiteSpace(normalizedUnit))
            return new UnitDeletionReferenceSummary(0, 0, 0);

        var itemCount = await CountMatchingUnitsAsync(
            dbContext.Items.IgnoreQueryFilters().Select(item => item.Unit),
            normalizedUnit,
            cancellationToken);
        var invoiceLineCount = await CountMatchingUnitsAsync(
            dbContext.InvoiceLines.IgnoreQueryFilters().Select(line => line.Unit),
            normalizedUnit,
            cancellationToken);
        var inventoryTransferLineCount = await CountMatchingUnitsAsync(
            dbContext.InventoryTransferLines.IgnoreQueryFilters().Select(line => line.Unit),
            normalizedUnit,
            cancellationToken);

        return new UnitDeletionReferenceSummary(itemCount, invoiceLineCount, inventoryTransferLineCount);
    }

    public static async Task<string?> BuildReferenceBlockMessageAsync(
        AppDbContext dbContext,
        string? unitName,
        CancellationToken cancellationToken)
    {
        var summary = await GetReferenceSummaryAsync(dbContext, unitName, cancellationToken);
        return BuildReferenceBlockMessage(summary);
    }

    public static string? BuildReferenceBlockMessage(UnitDeletionReferenceSummary summary)
    {
        if (!summary.HasReferences)
            return null;

        var parts = new List<string>();
        AddPart(parts, "품목", summary.ItemCount);
        AddPart(parts, "전표 라인", summary.InvoiceLineCount);
        AddPart(parts, "재고이동 라인", summary.InventoryTransferLineCount);

        return $"연결된 데이터({string.Join(", ", parts)})가 남아 있어 단위를 삭제할 수 없습니다. 먼저 품목/전표/재고이동 라인의 단위를 다른 값으로 변경한 뒤 다시 시도하세요.";
    }

    private static async Task<int> CountMatchingUnitsAsync(
        IQueryable<string> values,
        string normalizedUnit,
        CancellationToken cancellationToken)
    {
        var snapshot = await values.ToListAsync(cancellationToken);
        return snapshot.Count(value => string.Equals(
            UnitCatalogNormalizer.Normalize(value),
            normalizedUnit,
            StringComparison.Ordinal));
    }

    private static void AddPart(ICollection<string> parts, string label, int count)
    {
        if (count > 0)
            parts.Add($"{label} {count:N0}건");
    }
}
