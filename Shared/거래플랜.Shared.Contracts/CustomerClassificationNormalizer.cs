using System;
using System.Collections.Generic;
using System.Linq;

namespace 거래플랜.Shared.Contracts;

public static class CustomerClassificationNormalizer
{
    public const string Sales = "매출";
    public const string Purchase = "매입";
    public const string SalesAndPurchase = "매출/매입";

    private static readonly IReadOnlyDictionary<string, string> _tradeTypeAliasMap =
        new Dictionary<string, string>(StringComparer.CurrentCultureIgnoreCase)
        {
            [Sales] = Sales,
            ["판매"] = Sales,
            ["매출처"] = Sales,
            [Purchase] = Purchase,
            ["매입처"] = Purchase,
            [SalesAndPurchase] = SalesAndPurchase,
            ["판매/매입"] = SalesAndPurchase,
            ["매출매입"] = SalesAndPurchase,
            ["매입/매출"] = SalesAndPurchase
        };

    public static IReadOnlyList<string> AllowedTradeTypes { get; } =
    [
        Sales,
        Purchase,
        SalesAndPurchase
    ];

    public static bool IsAllowedTradeType(string? value)
        => TryNormalizeTradeType(value, out _);

    public static string NormalizeTradeTypeOrDefault(string? value, string defaultValue = Sales)
        => TryNormalizeTradeType(value, out var normalized) ? normalized : defaultValue;

    public static bool TryNormalizeTradeType(string? value, out string normalizedTradeType)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalizedTradeType = Sales;
            return true;
        }

        if (TryNormalizeSimpleTradeType(normalized, out normalizedTradeType))
            return true;

        if (TryExtractCompositeCategoryAndTradeType(normalized, out _, out normalizedTradeType))
            return true;

        normalizedTradeType = string.Empty;
        return false;
    }

    public static bool TryExtractCompositeCategoryAndTradeType(
        string? value,
        out DefaultCustomerCategoryDefinition category,
        out string normalizedTradeType)
    {
        category = default!;
        normalizedTradeType = string.Empty;

        var normalized = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        var slashIndex = normalized.IndexOf('/');
        if (slashIndex <= 0 || slashIndex >= normalized.Length - 1)
            return false;

        var categoryName = normalized[..slashIndex].Trim();
        var tradeTypePart = normalized[(slashIndex + 1)..].Trim();

        if (!DefaultCustomerCategories.TryGetByName(categoryName, out category))
            return false;

        if (!TryNormalizeSimpleTradeType(tradeTypePart, out normalizedTradeType))
        {
            category = default!;
            normalizedTradeType = string.Empty;
            return false;
        }

        return true;
    }

    public static bool TryResolveCategory(string? value, out DefaultCustomerCategoryDefinition category)
        => DefaultCustomerCategories.TryGetByName(value, out category);

    public static IReadOnlyList<TradeTypeDefinition> GetCanonicalTradeTypes()
        => TradeTypeDefinition.All;

    private static bool TryNormalizeSimpleTradeType(string? value, out string normalizedTradeType)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (_tradeTypeAliasMap.TryGetValue(normalized, out var canonical))
        {
            normalizedTradeType = canonical;
            return true;
        }

        normalizedTradeType = string.Empty;
        return false;
    }

    public sealed record TradeTypeDefinition(
        string Name,
        bool AllowsSales,
        bool AllowsPurchase,
        int SortOrder)
    {
        public static IReadOnlyList<TradeTypeDefinition> All { get; } =
        [
            new(Sales, true, false, 0),
            new(Purchase, false, true, 10),
            new(SalesAndPurchase, true, true, 20)
        ];

        public static TradeTypeDefinition? Find(string? name)
            => All.FirstOrDefault(definition =>
                string.Equals(definition.Name, name, StringComparison.CurrentCultureIgnoreCase));
    }
}
