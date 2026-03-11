namespace SalesMaster.Desktop.App.Services;

public static class DomainConstants
{
    public const string DefaultOfficeUznet = "UZNET";
    public const string DefaultOfficeYeonsu = "YEONSU";
    public const string DefaultWarehouseUznetMain = "UZNET_MAIN";
    public const string DefaultWarehouseYeonsuMain = "YEONSU_MAIN";

    public static string OfficeUznet { get; private set; } = DefaultOfficeUznet;
    public static string OfficeYeonsu { get; private set; } = DefaultOfficeYeonsu;
    public static string WarehouseUznetMain { get; private set; } = DefaultWarehouseUznetMain;
    public static string WarehouseYeonsuMain { get; private set; } = DefaultWarehouseYeonsuMain;

    public const string RoleAdmin = "admin";
    public const string RoleUser = "user";

    public static void ConfigureSystemOffices(
        string? officeUznet,
        string? officeYeonsu,
        string? warehouseUznetMain = null,
        string? warehouseYeonsuMain = null)
    {
        OfficeUznet = NormalizeCode(officeUznet, DefaultOfficeUznet);
        OfficeYeonsu = NormalizeCode(officeYeonsu, DefaultOfficeYeonsu);
        WarehouseUznetMain = NormalizeCode(warehouseUznetMain, DefaultWarehouseUznetMain);
        WarehouseYeonsuMain = NormalizeCode(warehouseYeonsuMain, DefaultWarehouseYeonsuMain);
    }

    public static void ResetSystemOffices()
        => ConfigureSystemOffices(
            DefaultOfficeUznet,
            DefaultOfficeYeonsu,
            DefaultWarehouseUznetMain,
            DefaultWarehouseYeonsuMain);

    public static bool IsAdminRole(string? role)
        => string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase)
           || string.Equals(role, RoleAdmin, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeCode(string? value, string fallback)
    {
        var normalized = (value ?? string.Empty).Trim().ToUpperInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }
}
