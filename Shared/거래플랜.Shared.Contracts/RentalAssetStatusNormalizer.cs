namespace 거래플랜.Shared.Contracts;

public static class RentalAssetStatusNormalizer
{
    public const string Active = "임대진행중";
    public const string Warehouse = "창고";
    public const string Maintenance = "점검중";
    public const string Sold = "판매";
    public const string Disposed = "폐기";
    public const string UnknownInstallLocation = "설치처 불명";

    private static readonly string[] WarehouseAliasValues =
    [
        "미배정",
        "대기",
        "회수"
    ];

    public static string Normalize(string? assetStatus)
    {
        var status = (assetStatus ?? string.Empty).Trim();
        return IsWarehouseAlias(status) ? Warehouse : status;
    }

    public static bool IsWarehouseAlias(string? assetStatus)
    {
        var status = (assetStatus ?? string.Empty).Trim();
        return WarehouseAliasValues.Any(alias => string.Equals(alias, status, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsWarehouse(string? assetStatus)
        => string.Equals(Normalize(assetStatus), Warehouse, StringComparison.OrdinalIgnoreCase);

    public static bool IsDisposed(string? assetStatus)
        => string.Equals(Normalize(assetStatus), Disposed, StringComparison.OrdinalIgnoreCase);

    public static bool IsNonOperating(string? assetStatus)
        => IsWarehouse(assetStatus) || IsDisposed(assetStatus);

    public static IReadOnlyList<string> ExpandForFilter(string? assetStatus)
    {
        var normalized = Normalize(assetStatus);
        if (string.IsNullOrWhiteSpace(normalized))
            return [string.Empty];

        if (!string.Equals(normalized, Warehouse, StringComparison.OrdinalIgnoreCase))
            return [normalized];

        return [Warehouse, .. WarehouseAliasValues];
    }
}
