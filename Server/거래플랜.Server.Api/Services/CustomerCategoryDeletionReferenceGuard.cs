using 거래플랜.Server.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace 거래플랜.Server.Api.Services;

public sealed record CustomerCategoryDeletionReferenceSummary(
    int CustomerCount,
    int CustomerMasterCount)
{
    public bool HasReferences => CustomerCount > 0 || CustomerMasterCount > 0;
}

public static class CustomerCategoryDeletionReferenceGuard
{
    public const string ConflictCode = "customer_category_delete_blocked_by_references";

    public static async Task<CustomerCategoryDeletionReferenceSummary> GetReferenceSummaryAsync(
        AppDbContext dbContext,
        Guid categoryId,
        CancellationToken cancellationToken)
    {
        var customerCount = await dbContext.Customers
            .IgnoreQueryFilters()
            .CountAsync(customer => customer.CategoryId == categoryId, cancellationToken);

        var customerMasterCount = await dbContext.CustomerMasters
            .IgnoreQueryFilters()
            .CountAsync(customer => customer.CategoryId == categoryId, cancellationToken);

        return new CustomerCategoryDeletionReferenceSummary(customerCount, customerMasterCount);
    }

    public static async Task<string?> BuildReferenceBlockMessageAsync(
        AppDbContext dbContext,
        Guid categoryId,
        CancellationToken cancellationToken)
    {
        var summary = await GetReferenceSummaryAsync(dbContext, categoryId, cancellationToken);
        return BuildReferenceBlockMessage(summary);
    }

    public static string? BuildReferenceBlockMessage(CustomerCategoryDeletionReferenceSummary summary)
    {
        if (!summary.HasReferences)
            return null;

        var parts = new List<string>();
        AddPart(parts, "거래처", summary.CustomerCount);
        AddPart(parts, "거래처 원장", summary.CustomerMasterCount);

        return $"연결된 데이터({string.Join(", ", parts)})가 남아 있어 거래처분류를 삭제할 수 없습니다. 먼저 거래처/거래처 원장의 분류를 다른 값으로 변경한 뒤 다시 시도하세요.";
    }

    private static void AddPart(ICollection<string> parts, string label, int count)
    {
        if (count > 0)
            parts.Add($"{label} {count:N0}건");
    }
}
