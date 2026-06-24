using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace 거래플랜.Server.Api.Services;

public static class SelectionOptionDeletionReferenceGuard
{
    public static async Task<string?> BuildBlockMessageAsync(
        AppDbContext dbContext,
        string entityName,
        string? optionName,
        CancellationToken cancellationToken)
        => entityName switch
        {
            nameof(PriceGradeOption) => await BuildPriceGradeOptionBlockMessageAsync(dbContext, optionName, cancellationToken),
            nameof(TradeTypeOption) => await BuildTradeTypeOptionBlockMessageAsync(dbContext, optionName, cancellationToken),
            nameof(ItemCategoryOption) => await BuildItemCategoryOptionBlockMessageAsync(dbContext, optionName, cancellationToken),
            _ => null
        };

    private static async Task<string?> BuildPriceGradeOptionBlockMessageAsync(
        AppDbContext dbContext,
        string? optionName,
        CancellationToken cancellationToken)
    {
        var normalizedName = NormalizeOptionName(optionName);
        if (string.IsNullOrWhiteSpace(normalizedName))
            return null;

        var customerCount = await CountMatchingValuesAsync(
            dbContext.Customers.IgnoreQueryFilters().Select(customer => customer.PriceGrade),
            normalizedName,
            cancellationToken);
        return customerCount > 0
            ? $"연결된 데이터(거래처 {customerCount:N0}건)가 남아 있어 가격등급을 삭제할 수 없습니다. 먼저 거래처의 가격등급을 다른 값으로 변경한 뒤 다시 시도하세요."
            : null;
    }

    private static async Task<string?> BuildTradeTypeOptionBlockMessageAsync(
        AppDbContext dbContext,
        string? optionName,
        CancellationToken cancellationToken)
    {
        var normalizedName = CustomerClassificationNormalizer.TryNormalizeTradeType(optionName, out var canonicalName)
            ? canonicalName
            : NormalizeOptionName(optionName);
        if (string.IsNullOrWhiteSpace(normalizedName))
            return null;

        if (CustomerClassificationNormalizer.TradeTypeDefinition.Find(normalizedName) is not null)
            return "거래구분 기준값은 시스템 고정값이라 삭제할 수 없습니다.";

        var customerCount = await CountMatchingValuesAsync(
            dbContext.Customers.IgnoreQueryFilters().Select(customer => customer.TradeType),
            normalizedName,
            cancellationToken);
        return customerCount > 0
            ? $"연결된 데이터(거래처 {customerCount:N0}건)가 남아 있어 거래구분을 삭제할 수 없습니다. 먼저 거래처의 거래구분을 다른 값으로 변경한 뒤 다시 시도하세요."
            : null;
    }

    private static async Task<string?> BuildItemCategoryOptionBlockMessageAsync(
        AppDbContext dbContext,
        string? optionName,
        CancellationToken cancellationToken)
    {
        var optionKey = RentalCatalogValueNormalizer.NormalizeLooseKey(optionName);
        if (string.IsNullOrWhiteSpace(optionKey))
            return null;

        var itemCount = await CountMatchingLooseKeysAsync(
            dbContext.Items.IgnoreQueryFilters().Select(item => item.CategoryName),
            optionKey,
            cancellationToken);
        var rentalAssetCount = await CountMatchingLooseKeysAsync(
            dbContext.RentalAssets.IgnoreQueryFilters().Select(asset => asset.ItemCategoryName),
            optionKey,
            cancellationToken);
        if (itemCount <= 0 && rentalAssetCount <= 0)
            return null;

        var parts = new List<string>();
        AddPart(parts, "품목", itemCount);
        AddPart(parts, "렌탈 자산", rentalAssetCount);
        return $"연결된 데이터({string.Join(", ", parts)})가 남아 있어 품목분류를 삭제할 수 없습니다. 먼저 품목/렌탈 자산의 품목분류를 다른 값으로 변경한 뒤 다시 시도하세요.";
    }

    private static async Task<int> CountMatchingValuesAsync(
        IQueryable<string> values,
        string normalizedName,
        CancellationToken cancellationToken)
    {
        var snapshot = await values.ToListAsync(cancellationToken);
        return snapshot.Count(value => string.Equals(
            NormalizeOptionName(value),
            normalizedName,
            StringComparison.CurrentCultureIgnoreCase));
    }

    private static async Task<int> CountMatchingLooseKeysAsync(
        IQueryable<string> values,
        string optionKey,
        CancellationToken cancellationToken)
    {
        var snapshot = await values.ToListAsync(cancellationToken);
        return snapshot.Count(value => string.Equals(
            RentalCatalogValueNormalizer.NormalizeLooseKey(value),
            optionKey,
            StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeOptionName(string? value)
        => (value ?? string.Empty).Trim();

    private static void AddPart(ICollection<string> parts, string label, int count)
    {
        if (count > 0)
            parts.Add($"{label} {count:N0}건");
    }
}
