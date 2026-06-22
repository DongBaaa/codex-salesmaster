using 거래플랜.Server.Api.Data;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace 거래플랜.Server.Api.Services;

public sealed record ItemDeletionReferenceSummary(
    int ActiveInvoiceLineCount,
    int ActiveInventoryTransferLineCount,
    int ActiveRentalAssetCount,
    int ActiveRentalBillingProfileCount)
{
    public bool HasActiveReferences =>
        ActiveInvoiceLineCount > 0 ||
        ActiveInventoryTransferLineCount > 0 ||
        ActiveRentalAssetCount > 0 ||
        ActiveRentalBillingProfileCount > 0;
}

public static class ItemDeletionReferenceGuard
{
    public const string ConflictCode = "item_delete_blocked_by_active_references";

    public static async Task<ItemDeletionReferenceSummary> GetActiveReferenceSummaryAsync(
        AppDbContext dbContext,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        var activeInvoiceLineCount = await (
                from line in dbContext.InvoiceLines.IgnoreQueryFilters()
                join invoice in dbContext.Invoices.IgnoreQueryFilters()
                    on line.InvoiceId equals invoice.Id
                where line.ItemId == itemId &&
                      !line.IsDeleted &&
                      !invoice.IsDeleted
                select line.Id)
            .CountAsync(cancellationToken);

        var activeInventoryTransferLineCount = await (
                from line in dbContext.InventoryTransferLines.IgnoreQueryFilters()
                join transfer in dbContext.InventoryTransfers.IgnoreQueryFilters()
                    on line.TransferId equals transfer.Id
                where line.ItemId == itemId &&
                      !line.IsDeleted &&
                      !transfer.IsDeleted
                select line.Id)
            .CountAsync(cancellationToken);

        var activeRentalAssetCount = await dbContext.RentalAssets
            .IgnoreQueryFilters()
            .CountAsync(asset => asset.ItemId == itemId && !asset.IsDeleted, cancellationToken);

        var rentalBillingTemplates = await dbContext.RentalBillingProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(profile => !profile.IsDeleted && profile.IsActive && !string.IsNullOrWhiteSpace(profile.BillingTemplateJson))
            .Select(profile => profile.BillingTemplateJson)
            .ToListAsync(cancellationToken);
        var activeRentalBillingProfileCount = rentalBillingTemplates
            .Count(templateJson => BillingTemplateReferencesItem(templateJson, itemId));

        return new ItemDeletionReferenceSummary(
            activeInvoiceLineCount,
            activeInventoryTransferLineCount,
            activeRentalAssetCount,
            activeRentalBillingProfileCount);
    }

    public static bool BillingTemplateReferencesItem(string? templateJson, Guid itemId)
    {
        if (itemId == Guid.Empty || string.IsNullOrWhiteSpace(templateJson))
            return false;

        try
        {
            using var document = JsonDocument.Parse(templateJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (TryGetGuidProperty(element, "ItemId", out var foundItemId) ||
                    TryGetGuidProperty(element, "itemId", out foundItemId))
                {
                    if (foundItemId == itemId)
                        return true;
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    public static async Task<string?> BuildActiveReferenceBlockMessageAsync(
        AppDbContext dbContext,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        var summary = await GetActiveReferenceSummaryAsync(dbContext, itemId, cancellationToken);
        return BuildActiveReferenceBlockMessage(summary);
    }

    public static string? BuildActiveReferenceBlockMessage(ItemDeletionReferenceSummary summary)
    {
        if (!summary.HasActiveReferences)
            return null;

        var parts = new List<string>();
        AddPart(parts, "전표 라인", summary.ActiveInvoiceLineCount);
        AddPart(parts, "재고이동 라인", summary.ActiveInventoryTransferLineCount);
        AddPart(parts, "렌탈 자산", summary.ActiveRentalAssetCount);
        AddPart(parts, "렌탈 청구프로필", summary.ActiveRentalBillingProfileCount);

        return $"연결된 활성 업무 데이터({string.Join(", ", parts)})가 남아 있어 품목을 삭제할 수 없습니다. 먼저 전표/재고이동/렌탈 자산 연결을 다른 품목으로 옮기거나 삭제·해제한 뒤 다시 시도하세요.";
    }

    private static bool TryGetGuidProperty(JsonElement element, string propertyName, out Guid value)
    {
        value = Guid.Empty;
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.String &&
            Guid.TryParse(property.GetString(), out var parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    private static void AddPart(ICollection<string> parts, string label, int count)
    {
        if (count > 0)
            parts.Add($"{label} {count:N0}건");
    }

}
