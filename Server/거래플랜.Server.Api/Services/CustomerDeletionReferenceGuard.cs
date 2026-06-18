using 거래플랜.Server.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace 거래플랜.Server.Api.Services;

public sealed record CustomerDeletionReferenceSummary(
    int ActiveInvoiceCount,
    int ActiveTransactionCount,
    int ActiveRentalProfileCount,
    int ActiveRentalAssetCount,
    int ActiveCurrentAssignmentHistoryCount)
{
    public bool HasActiveReferences =>
        ActiveInvoiceCount > 0 ||
        ActiveTransactionCount > 0 ||
        ActiveRentalProfileCount > 0 ||
        ActiveRentalAssetCount > 0 ||
        ActiveCurrentAssignmentHistoryCount > 0;
}

public static class CustomerDeletionReferenceGuard
{
    public const string ConflictCode = "customer_delete_blocked_by_active_references";

    public static async Task<CustomerDeletionReferenceSummary> GetActiveReferenceSummaryAsync(
        AppDbContext dbContext,
        Guid customerId,
        CancellationToken cancellationToken)
    {
        var activeInvoiceCount = await dbContext.Invoices
            .IgnoreQueryFilters()
            .CountAsync(current => current.CustomerId == customerId && !current.IsDeleted, cancellationToken);

        var activeTransactionCount = await dbContext.Transactions
            .IgnoreQueryFilters()
            .CountAsync(current => current.CustomerId == customerId && !current.IsDeleted, cancellationToken);

        var activeRentalProfileCount = await dbContext.RentalBillingProfiles
            .IgnoreQueryFilters()
            .CountAsync(current => current.CustomerId == customerId && !current.IsDeleted, cancellationToken);

        var activeRentalAssetCount = await dbContext.RentalAssets
            .IgnoreQueryFilters()
            .CountAsync(current => current.CustomerId == customerId && !current.IsDeleted, cancellationToken);

        var activeCurrentAssignmentHistoryCount = await dbContext.RentalAssetAssignmentHistories
            .IgnoreQueryFilters()
            .CountAsync(current => current.CustomerId == customerId && !current.IsDeleted && current.IsCurrent, cancellationToken);

        return new CustomerDeletionReferenceSummary(
            activeInvoiceCount,
            activeTransactionCount,
            activeRentalProfileCount,
            activeRentalAssetCount,
            activeCurrentAssignmentHistoryCount);
    }

    public static async Task<string?> BuildActiveReferenceBlockMessageAsync(
        AppDbContext dbContext,
        Guid customerId,
        CancellationToken cancellationToken)
    {
        var summary = await GetActiveReferenceSummaryAsync(dbContext, customerId, cancellationToken);
        return BuildActiveReferenceBlockMessage(summary);
    }

    public static string? BuildActiveReferenceBlockMessage(CustomerDeletionReferenceSummary summary)
    {
        if (!summary.HasActiveReferences)
            return null;

        var parts = new List<string>();
        AddPart(parts, "전표", summary.ActiveInvoiceCount);
        AddPart(parts, "거래내역", summary.ActiveTransactionCount);
        AddPart(parts, "렌탈 청구", summary.ActiveRentalProfileCount);
        AddPart(parts, "렌탈 자산", summary.ActiveRentalAssetCount);
        AddPart(parts, "현재 설치이력", summary.ActiveCurrentAssignmentHistoryCount);

        return $"연결된 활성 데이터({string.Join(", ", parts)})가 남아 있어 거래처를 삭제할 수 없습니다. 먼저 전표/거래내역/렌탈 연결을 다른 거래처로 옮기거나 삭제·해제한 뒤 다시 시도하세요.";
    }

    private static void AddPart(ICollection<string> parts, string label, int count)
    {
        if (count > 0)
            parts.Add($"{label} {count:N0}건");
    }
}
