using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Services;

public static class WarehouseDisplayNameResolver
{
    public static string Resolve(string? warehouseCode)
    {
        var trimmed = warehouseCode?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return "미지정 창고";

        var normalized = OfficeCodeCatalog.NormalizeWarehouseCodeLoose(trimmed, trimmed, OfficeCodeCatalog.Usenet);
        return normalized switch
        {
            OfficeCodeCatalog.UsenetMainWarehouse => "USENET 창고",
            OfficeCodeCatalog.ItworldMainWarehouse => "ITWORLD 창고",
            OfficeCodeCatalog.YeonsuMainWarehouse => "YEONSU 창고",
            _ => trimmed
        };
    }
}
