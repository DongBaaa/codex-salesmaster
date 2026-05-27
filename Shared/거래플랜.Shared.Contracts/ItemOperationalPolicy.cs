namespace 거래플랜.Shared.Contracts;

public static class ItemKinds
{
    public const string Consumable = "소모품";
    public const string Product = "일반상품";
    public const string Asset = "장비";
    public const string Billing = "청구항목";

    public static readonly IReadOnlyList<string> All =
    [
        Consumable,
        Product,
        Asset,
        Billing
    ];

    public static string Normalize(string? value, string? fallback = null)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            normalized = fallback ?? Product;

        return normalized switch
        {
            Consumable => Consumable,
            Product => Product,
            Asset => Asset,
            Billing => Billing,
            _ when string.Equals(normalized, "Consumable", StringComparison.OrdinalIgnoreCase) => Consumable,
            _ when string.Equals(normalized, "Product", StringComparison.OrdinalIgnoreCase) => Product,
            _ when string.Equals(normalized, "Goods", StringComparison.OrdinalIgnoreCase) => Product,
            _ when string.Equals(normalized, "Asset", StringComparison.OrdinalIgnoreCase) => Asset,
            _ when string.Equals(normalized, "Equipment", StringComparison.OrdinalIgnoreCase) => Asset,
            _ when string.Equals(normalized, "Billing", StringComparison.OrdinalIgnoreCase) => Billing,
            _ when string.Equals(normalized, "Service", StringComparison.OrdinalIgnoreCase) => Billing,
            _ => normalized
        };
    }
}

public static class ItemTrackingTypes
{
    public const string Stock = "재고";
    public const string Asset = "자산";
    public const string NonStock = "비재고";

    public static readonly IReadOnlyList<string> All =
    [
        Stock,
        Asset,
        NonStock
    ];

    public static string Normalize(string? value, string? fallback = null)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            normalized = fallback ?? Stock;

        return normalized switch
        {
            Stock => Stock,
            Asset => Asset,
            NonStock => NonStock,
            _ when string.Equals(normalized, "Stock", StringComparison.OrdinalIgnoreCase) => Stock,
            _ when string.Equals(normalized, "Inventory", StringComparison.OrdinalIgnoreCase) => Stock,
            _ when string.Equals(normalized, "Asset", StringComparison.OrdinalIgnoreCase) => Asset,
            _ when string.Equals(normalized, "Equipment", StringComparison.OrdinalIgnoreCase) => Asset,
            _ when string.Equals(normalized, "NonStock", StringComparison.OrdinalIgnoreCase) => NonStock,
            _ when string.Equals(normalized, "Non-Stock", StringComparison.OrdinalIgnoreCase) => NonStock,
            _ when string.Equals(normalized, "Non Stock", StringComparison.OrdinalIgnoreCase) => NonStock,
            _ when string.Equals(normalized, "Billing", StringComparison.OrdinalIgnoreCase) => NonStock,
            _ when string.Equals(normalized, "Service", StringComparison.OrdinalIgnoreCase) => NonStock,
            _ => normalized
        };
    }
}

public static class ItemOperationalPolicy
{
    public static string NormalizeItemKind(string? itemKind, string? trackingType, string? categoryName, bool isRental)
    {
        var normalized = (itemKind ?? string.Empty).Trim();
        var tracking = NormalizeTrackingType(trackingType, normalized, categoryName, isRental);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            normalized = ItemKinds.Normalize(normalized);
            if (normalized == ItemKinds.Product && tracking == ItemTrackingTypes.Asset)
                return ItemKinds.Asset;

            if (normalized == ItemKinds.Product && tracking == ItemTrackingTypes.NonStock)
                return ItemKinds.Billing;

            return normalized;
        }

        if (tracking == ItemTrackingTypes.Asset)
            return ItemKinds.Asset;

        if (tracking == ItemTrackingTypes.NonStock)
            return ItemKinds.Billing;

        return ItemKinds.Product;
    }

    public static string NormalizeTrackingType(string? trackingType, string? itemKind, string? categoryName, bool isRental)
    {
        var normalized = (trackingType ?? string.Empty).Trim();
        var normalizedItemKind = (itemKind ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            normalized = ItemTrackingTypes.Normalize(normalized);
            if (normalized == ItemTrackingTypes.Stock &&
                (string.Equals(normalizedItemKind, ItemKinds.Asset, StringComparison.OrdinalIgnoreCase) || isRental))
            {
                return ItemTrackingTypes.Asset;
            }

            if (normalized == ItemTrackingTypes.Stock &&
                (string.Equals(normalizedItemKind, ItemKinds.Billing, StringComparison.OrdinalIgnoreCase) || IsBillingCategory(categoryName)))
            {
                return ItemTrackingTypes.NonStock;
            }

            return normalized;
        }

        if (string.Equals(normalizedItemKind, ItemKinds.Asset, StringComparison.OrdinalIgnoreCase) || isRental)
            return ItemTrackingTypes.Asset;

        if (string.Equals(normalizedItemKind, ItemKinds.Billing, StringComparison.OrdinalIgnoreCase) || IsBillingCategory(categoryName))
            return ItemTrackingTypes.NonStock;

        return ItemTrackingTypes.Stock;
    }

    public static bool SupportsInventory(string? trackingType)
        => string.Equals(ItemTrackingTypes.Normalize(trackingType), ItemTrackingTypes.Stock, StringComparison.Ordinal);

    public static bool IsAsset(string? trackingType)
        => string.Equals(ItemTrackingTypes.Normalize(trackingType), ItemTrackingTypes.Asset, StringComparison.Ordinal);

    public static bool IsNonStock(string? trackingType)
        => string.Equals(ItemTrackingTypes.Normalize(trackingType), ItemTrackingTypes.NonStock, StringComparison.Ordinal);

    public static bool SupportsInvoiceLookup(string? trackingType)
    {
        var normalized = ItemTrackingTypes.Normalize(trackingType);
        return normalized == ItemTrackingTypes.Stock || normalized == ItemTrackingTypes.NonStock;
    }

    public static void NormalizeInPlace(
        ref string itemKind,
        ref string trackingType,
        string? categoryName,
        bool isRental)
    {
        trackingType = NormalizeTrackingType(trackingType, itemKind, categoryName, isRental);
        itemKind = NormalizeItemKind(itemKind, trackingType, categoryName, isRental);
    }

    public static bool IsBillingCategory(string? categoryName)
        => string.Equals((categoryName ?? string.Empty).Trim(), "렌탈료", StringComparison.OrdinalIgnoreCase);
}
