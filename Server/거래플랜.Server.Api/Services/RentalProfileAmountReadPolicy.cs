using System.Text.Json;
using System.Text.Json.Nodes;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Server.Api.Services;

internal static class RentalProfileAmountReadPolicy
{
    private static readonly string[] ScalarMoney = ["MonthlyAmount", "DepositAmount", "SettledAmount", "OutstandingAmount"];

    public static void Apply(RentalBillingProfileDto dto, OfficeScopeService scope)
    {
        if (scope.CanViewSalesAmounts()) return;
        dto.MonthlyAmount = dto.DepositAmount = dto.SettledAmount = dto.OutstandingAmount = null;
        dto.BillingTemplateJson = HideRows(dto.BillingTemplateJson, false);
        dto.BillingRunsJson = HideRows(dto.BillingRunsJson, true);
    }

    // Response copies only. Stored conflicts remain intact for authorized audit.
    public static void ApplyConflicts(IEnumerable<ConflictLogDto> conflicts, OfficeScopeService scope)
    {
        if (scope.CanViewSalesAmounts()) return;
        foreach (var conflict in conflicts.Where(c => c.EntityName == "RentalBillingProfile"))
        {
            conflict.ClientJson = HideSnapshot(conflict.ClientJson);
            conflict.ServerJson = HideSnapshot(conflict.ServerJson);
        }
    }

    private static string HideSnapshot(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        try
        {
            if (JsonNode.Parse(raw) is not JsonObject row) return string.Empty;
            foreach (var key in ScalarMoney) Replace(row, key, null);
            Replace(row, "AmountsHidden", JsonValue.Create(true));
            foreach (var key in new[] { "BillingTemplateJson", "BillingRunsJson" })
            {
                foreach (var name in row.Select(p => p.Key).Where(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)).ToArray())
                    row[name] = HideRows(row[name]?.GetValue<string>() ?? "[]", key == "BillingRunsJson");
            }
            return row.ToJsonString();
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException)
        {
            return string.Empty;
        }
    }

    private static string HideRows(string raw, bool runs)
    {
        try
        {
            if (JsonNode.Parse(raw) is not JsonArray array) return "[]";
            foreach (var node in array)
            {
                if (node is not JsonObject row) return "[]";
                foreach (var key in runs ? new[] { "BilledAmount", "SettledAmount" } : new[] { "UnitPrice", "Amount" })
                    Replace(row, key, null);
                if (runs)
                {
                    foreach (var name in row.Select(p => p.Key).Where(k => string.Equals(k, "Items", StringComparison.OrdinalIgnoreCase)).ToArray())
                        row[name] = JsonNode.Parse(HideRows(row[name]?.ToJsonString() ?? "[]", false));
                }
            }
            return array.ToJsonString();
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException)
        {
            return "[]";
        }
    }

    private static void Replace(JsonObject row, string key, JsonNode? value)
    {
        var names = row.Select(p => p.Key).Where(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var name in names) row.Remove(name);
        row[key] = value;
    }
}
