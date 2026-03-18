using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

public static class DomainConstants
{
    public const string DefaultOfficeUsenet = OfficeCodeCatalog.Usenet;
    public const string DefaultOfficeItworld = OfficeCodeCatalog.Itworld;
    public const string DefaultOfficeYeonsu = OfficeCodeCatalog.Yeonsu;
    public const string DefaultWarehouseUsenetMain = OfficeCodeCatalog.UsenetMainWarehouse;
    public const string DefaultWarehouseItworldMain = OfficeCodeCatalog.ItworldMainWarehouse;
    public const string DefaultWarehouseYeonsuMain = OfficeCodeCatalog.YeonsuMainWarehouse;

    public static string OfficeUsenet { get; private set; } = DefaultOfficeUsenet;
    public static string OfficeItworld { get; private set; } = DefaultOfficeItworld;
    public static string OfficeYeonsu { get; private set; } = DefaultOfficeYeonsu;
    public static string WarehouseUsenetMain { get; private set; } = DefaultWarehouseUsenetMain;
    public static string WarehouseItworldMain { get; private set; } = DefaultWarehouseItworldMain;
    public static string WarehouseYeonsuMain { get; private set; } = DefaultWarehouseYeonsuMain;

    public const string RoleAdmin = "admin";
    public const string RoleUser = "user";

    public static void ConfigureSystemOffices(
        string? officeUsenet,
        string? officeYeonsu,
        string? warehouseUsenetMain = null,
        string? warehouseYeonsuMain = null,
        string? officeItworld = null,
        string? warehouseItworldMain = null)
    {
        OfficeUsenet = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeUsenet, DefaultOfficeUsenet);
        OfficeItworld = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeItworld, DefaultOfficeItworld);
        OfficeYeonsu = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeYeonsu, DefaultOfficeYeonsu);
        WarehouseUsenetMain = OfficeCodeCatalog.NormalizeWarehouseCodeOrDefault(warehouseUsenetMain, OfficeUsenet, DefaultOfficeUsenet);
        WarehouseItworldMain = OfficeCodeCatalog.NormalizeWarehouseCodeOrDefault(warehouseItworldMain, OfficeItworld, DefaultOfficeItworld);
        WarehouseYeonsuMain = OfficeCodeCatalog.NormalizeWarehouseCodeOrDefault(warehouseYeonsuMain, OfficeYeonsu, DefaultOfficeYeonsu);
    }

    public static void ResetSystemOffices()
        => ConfigureSystemOffices(
            DefaultOfficeUsenet,
            DefaultOfficeYeonsu,
            DefaultWarehouseUsenetMain,
            DefaultWarehouseYeonsuMain,
            DefaultOfficeItworld,
            DefaultWarehouseItworldMain);

    public static bool IsAdminRole(string? role)
        => string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase)
           || string.Equals(role, RoleAdmin, StringComparison.OrdinalIgnoreCase);

}

