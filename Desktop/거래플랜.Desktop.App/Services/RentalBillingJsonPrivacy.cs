using System.Text.Json;
using System.Text.Json.Nodes;

namespace 거래플랜.Desktop.App.Services;

/// <summary>Redacts a copy of structured billing data without deleting its business metadata.</summary>
internal static class RentalBillingJsonPrivacy
{
    public static string HideTemplateAmounts(string raw) => Hide(raw, runs: false);
    public static string HideRunAmounts(string raw) => Hide(raw, runs: true);

    private static string Hide(string raw, bool runs)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        try
        {
            if (JsonNode.Parse(raw) is not JsonArray array)
                throw new JsonException("Billing data must be an array.");
            foreach (var entry in array)
            {
                if (entry is not JsonObject obj)
                    throw new JsonException("Billing entries must be objects.");
                if (!runs)
                {
                    HideItem(obj);
                    continue;
                }
                SetNull(obj, "BilledAmount");
                SetNull(obj, "SettledAmount");
                foreach (var key in obj.Select(pair => pair.Key).Where(key => string.Equals(key, "Items", StringComparison.OrdinalIgnoreCase)).ToArray())
                {
                    if (obj[key] is null) continue;
                    if (obj[key] is not JsonArray items) throw new JsonException("Run items must be an array.");
                    foreach (var item in items)
                    {
                        if (item is not JsonObject itemObject) throw new JsonException("Run items must be objects.");
                        HideItem(itemObject);
                    }
                }
            }
            return array.ToJsonString();
        }
        catch (JsonException ex)
        {
            // Do not emit the original financial payload or replace it with an empty
            // history. The caller must resolve malformed data before sending it.
            throw new InvalidOperationException("렌탈 청구 JSON을 안전하게 비공개 처리할 수 없어 전송을 중단했습니다. 원본은 보존됩니다.", ex);
        }
    }

    private static void HideItem(JsonObject obj)
    {
        SetNull(obj, "UnitPrice");
        SetNull(obj, "Amount");
    }

    private static void SetNull(JsonObject obj, string name)
    {
        var keys = obj.Select(pair => pair.Key).Where(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (keys.Length == 0) obj[name] = null;
        else foreach (var key in keys) obj[key] = null;
    }
}
