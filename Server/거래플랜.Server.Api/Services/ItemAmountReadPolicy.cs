using System.Text.Json;
using System.Text.Json.Nodes;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Server.Api.Services;

// Mutate response copies only; stored prices, mutation receipts and audit JSON stay intact.
internal static class ItemAmountReadPolicy
{
    public static ItemDto Apply(ItemDto dto, OfficeScopeService scope)
    {
        if (!scope.CanViewPurchaseAmounts()) dto.PurchasePrice = null;
        if (!scope.CanViewSalesAmounts())
            dto.SalePrice = dto.RetailPrice = dto.PriceGradeA = dto.PriceGradeB = dto.PriceGradeC = null;
        return dto;
    }

    public static ItemPriceGradeDto Apply(ItemPriceGradeDto dto, OfficeScopeService scope)
    {
        if (!scope.CanViewSalesAmounts()) dto.UnitPrice = null;
        return dto;
    }

    public static void ApplyConflicts(IEnumerable<ConflictLogDto> conflicts, OfficeScopeService scope)
    {
        var purchaseHidden = !scope.CanViewPurchaseAmounts();
        var salesHidden = !scope.CanViewSalesAmounts();
        if (!purchaseHidden && !salesHidden) return;
        foreach (var conflict in conflicts)
        {
            var grade = conflict.EntityName == "ItemPriceGrade";
            if (conflict.EntityName != "Item" && !grade || grade && !salesHidden) continue;
            conflict.ClientJson = HideSnapshot(conflict.ClientJson, grade, purchaseHidden, salesHidden);
            conflict.ServerJson = HideSnapshot(conflict.ServerJson, grade, purchaseHidden, salesHidden);
        }
    }

    private static string HideSnapshot(string raw, bool grade, bool purchaseHidden, bool salesHidden)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        try
        {
            if (JsonNode.Parse(raw) is not JsonObject json) return string.Empty;
            HideMoney(json, grade, purchaseHidden, salesHidden);
            if (grade)
            {
                SetMissingMoneyNull(json, "UnitPrice");
                SetHidden(json, "AmountsHidden");
            }
            else
            {
                if (purchaseHidden)
                {
                    SetMissingMoneyNull(json, "PurchasePrice");
                    SetHidden(json, "PurchaseAmountsHidden");
                }
                if (salesHidden)
                {
                    foreach (var field in SalesPrices) SetMissingMoneyNull(json, field);
                    SetHidden(json, "SalesAmountsHidden");
                }
            }
            return json.ToJsonString();
        }
        catch (JsonException) { return string.Empty; }
    }

    private static void SetMissingMoneyNull(JsonObject json, string name)
    {
        // Omitted legacy decimal properties deserialize as known zero. When access
        // is denied, the response must instead carry an explicit unknown value.
        if (!json.Any(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase))) json[name] = null;
    }

    private static void SetHidden(JsonObject json, string name)
    {
        var keys = json.Select(p => p.Key).Where(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (keys.Length == 0) json[name] = true;
        else foreach (var key in keys) json[key] = true;
    }

    private static readonly HashSet<string> SalesPrices = new(StringComparer.OrdinalIgnoreCase)
        { "SalePrice", "RetailPrice", "PriceGradeA", "PriceGradeB", "PriceGradeC" };

    private static void HideMoney(JsonNode? node, bool grade, bool purchaseHidden, bool salesHidden)
    {
        if (node is JsonObject obj)
            foreach (var key in obj.Select(p => p.Key).ToArray())
                if (grade ? string.Equals(key, "UnitPrice", StringComparison.OrdinalIgnoreCase)
                    : purchaseHidden && string.Equals(key, "PurchasePrice", StringComparison.OrdinalIgnoreCase)
                      || salesHidden && SalesPrices.Contains(key)) obj[key] = null;
                else HideMoney(obj[key], grade, purchaseHidden, salesHidden);
        else if (node is JsonArray array)
            foreach (var child in array) HideMoney(child, grade, purchaseHidden, salesHidden);
    }
}
