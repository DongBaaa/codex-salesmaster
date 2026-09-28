using System.Text.Json;
using System.Text.Json.Nodes;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Server.Api.Services;

internal static class RentalAssetAmountReadPolicy
{
    private static readonly string[] SalesFields = ["SalePrice", "MonthlyFee", "DepositText", "BlackOverageUnitPrice", "ColorOverageUnitPrice"];

    public static void Apply(RentalAssetDto dto, OfficeScopeService scope)
    {
        if (!scope.CanViewPurchaseAmounts()) dto.PurchasePrice = null;
        if (scope.CanViewSalesAmounts()) return;
        dto.SalePrice = dto.MonthlyFee = dto.BlackOverageUnitPrice = dto.ColorOverageUnitPrice = null;
        dto.DepositText = null;
    }

    public static void Apply(RentalAssetAssignmentHistoryDto dto, OfficeScopeService scope)
    {
        if (!scope.CanViewSalesAmounts()) dto.MonthlyFee = null;
    }

    public static void Apply(RentalBillingLogDto dto, OfficeScopeService scope)
    {
        if (!scope.CanViewSalesAmounts()) dto.BilledAmount = null;
    }

    // Redact response DTOs only; the original audit snapshots must remain intact.
    public static void ApplyConflicts(IEnumerable<ConflictLogDto> conflicts, OfficeScopeService scope)
    {
        var hideSales = !scope.CanViewSalesAmounts();
        var hidePurchase = !scope.CanViewPurchaseAmounts();
        foreach (var conflict in conflicts)
        {
            var asset = conflict.EntityName == "RentalAsset";
            var history = conflict.EntityName == "RentalAssetAssignmentHistory";
            var log = conflict.EntityName == "RentalBillingLog";
            if (!(asset && (hideSales || hidePurchase)) && !((history || log) && hideSales)) continue;
            var fields = asset
                ? (hideSales ? SalesFields : Array.Empty<string>()).Concat(hidePurchase ? ["PurchasePrice"] : Array.Empty<string>()).ToArray()
                : new[] { history ? "MonthlyFee" : "BilledAmount" };
            var flags = asset
                ? (hideSales ? new[] { "SalesAmountsHidden" } : Array.Empty<string>()).Concat(hidePurchase ? ["PurchaseAmountsHidden"] : Array.Empty<string>()).ToArray()
                : new[] { "AmountsHidden" };
            conflict.ClientJson = HideSnapshot(conflict.ClientJson, fields, flags);
            conflict.ServerJson = HideSnapshot(conflict.ServerJson, fields, flags);
        }
    }

    private static string HideSnapshot(string raw, string[] fields, string[] flags)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        try
        {
            if (JsonNode.Parse(raw) is not JsonObject row) return string.Empty;
            foreach (var field in fields) Replace(row, field, null);
            foreach (var flag in flags) Replace(row, flag, JsonValue.Create(true));
            return row.ToJsonString();
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException)
        {
            return string.Empty;
        }
    }

    private static void Replace(JsonObject row, string key, JsonNode? value)
    {
        foreach (var name in row.Select(p => p.Key).Where(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)).ToArray())
            row.Remove(name);
        row[key] = value;
    }
}
