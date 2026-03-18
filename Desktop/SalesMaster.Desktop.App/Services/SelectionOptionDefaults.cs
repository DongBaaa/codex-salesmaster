namespace SalesMaster.Desktop.App.Services;

public static class SelectionOptionDefaults
{
    public const string PriceSourceSales = "Sales";
    public const string PriceSourceA = "A";
    public const string PriceSourceB = "B";
    public const string PriceSourceC = "C";
    public const string PriceSourceRetail = "Retail";

    public sealed record ItemCategoryDefinition(
        Guid Id,
        string Name,
        int SortOrder,
        bool IsSystemDefault = false);

    public sealed record PriceGradeDefinition(
        Guid Id,
        string Name,
        string PriceSource,
        int SortOrder,
        bool IsSystemDefault = false);

    public sealed record TradeTypeDefinition(
        Guid Id,
        string Name,
        bool AllowsSales,
        bool AllowsPurchase,
        int SortOrder,
        bool IsSystemDefault = false);

    public static IReadOnlyList<PriceGradeDefinition> DefaultPriceGrades { get; } =
    [
        new(Guid.Parse("1b5ea4f8-ff61-4fc6-ac79-175e2125cba0"), "매출단가", PriceSourceSales, 0),
        new(Guid.Parse("c8a868c6-3f8d-4e29-a2c9-ec00d68f20a1"), "A_단가 적용", PriceSourceA, 10),
        new(Guid.Parse("b1af9d5e-33e1-4e4c-bf0c-2fb437d4f1c6"), "B_단가 적용", PriceSourceB, 20),
        new(Guid.Parse("8aa3856d-3133-4b38-b7f3-ce83cb2fe82d"), "C_단가 적용", PriceSourceC, 30),
        new(Guid.Parse("2e99b0b8-7f53-4dbc-a3c8-0dce274235a6"), "소매단가", PriceSourceRetail, 40)
    ];

    public static IReadOnlyList<TradeTypeDefinition> DefaultTradeTypes { get; } =
    [
        new(Guid.Parse("8ce85079-4f9f-49a1-bcd2-dbc653f54025"), CustomerTradeTypes.Sales, true, false, 0),
        new(Guid.Parse("4ab67a47-1b4e-4f17-8b3c-761023c2c3e3"), CustomerTradeTypes.Purchase, false, true, 10),
        new(Guid.Parse("9c305d74-3dd4-4fff-9679-dbd4dd6fdb49"), CustomerTradeTypes.SalesAndPurchase, true, true, 20)
    ];

    public static IReadOnlyList<ItemCategoryDefinition> DefaultItemCategories { get; } =
    [
        new(Guid.Parse("93b57e9c-718b-4681-9eb0-dadfc1b1032d"), "기타", 0)
    ];

    public static string NormalizePriceSource(string? source)
    {
        return (source ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "A" => PriceSourceA,
            "B" => PriceSourceB,
            "C" => PriceSourceC,
            "RETAIL" or "소매" => PriceSourceRetail,
            _ => PriceSourceSales
        };
    }

    public static string GetPriceSourceDisplayName(string? source)
        => NormalizePriceSource(source) switch
        {
            PriceSourceA => "A단가",
            PriceSourceB => "B단가",
            PriceSourceC => "C단가",
            PriceSourceRetail => "소매단가",
            _ => "매출단가"
        };

    public static string NormalizeItemCategoryName(string? name)
        => (name ?? string.Empty).Trim();
}
