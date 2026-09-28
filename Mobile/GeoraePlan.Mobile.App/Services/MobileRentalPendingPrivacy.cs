using System.Text.Json;
using System.Text.Json.Nodes;
using GeoraePlan.Mobile.App.Models;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Services;

/// <summary>Apply on a candidate state, then persist before sending any changed payload.</summary>
internal static class MobileRentalPendingPrivacy
{
    public static bool HasPending(MobileSyncState state)
        => state.PendingPush.RentalBillingProfiles.Count > 0 || state.PendingPush.RentalAssets.Count > 0 ||
           state.PendingPush.RentalAssetAssignmentHistories.Count > 0 || state.PendingPush.RentalBillingLogs.Count > 0;

    public static bool Apply(MobileSyncState state, SessionSnapshot session)
    {
        var sales = session.IsAuthenticated && (session.IsAdmin || session.HasPermission("Amount.ViewSales"));
        var purchase = session.IsAuthenticated && (session.IsAdmin || session.HasPermission("Amount.ViewPurchase"));
        var changed = false;
        foreach (var profile in state.PendingPush.RentalBillingProfiles)
        {
            var hidden = state.SyncedRentalBillingProfiles.Any(x => x.Id == profile.Id && x.AmountsHidden);
            if (sales && !hidden && !profile.AmountsHidden) continue;
            changed |= Rewrite(profile, () =>
            {
                profile.MonthlyAmount = profile.DepositAmount = profile.SettledAmount = profile.OutstandingAmount = null;
                profile.BillingTemplateJson = HideRows(profile.BillingTemplateJson, false);
                profile.BillingRunsJson = HideRows(profile.BillingRunsJson, true);
            });
        }
        foreach (var asset in state.PendingPush.RentalAssets)
        {
            var synced = state.SyncedRentalAssets.FirstOrDefault(x => x.Id == asset.Id);
            changed |= Rewrite(asset, () =>
            {
                if (!purchase || synced?.PurchaseAmountsHidden == true) asset.PurchasePrice = null;
                if (!sales || asset.SalesAmountsHidden || synced?.SalesAmountsHidden == true)
                {
                    asset.SalePrice = asset.MonthlyFee = asset.BlackOverageUnitPrice = asset.ColorOverageUnitPrice = null;
                    asset.DepositText = null;
                }
            });
        }
        foreach (var history in state.PendingPush.RentalAssetAssignmentHistories)
            if (!sales || state.SyncedRentalAssetAssignmentHistories.Any(x => x.Id == history.Id && x.AmountsHidden))
                changed |= Rewrite(history, () => history.MonthlyFee = null);
        foreach (var log in state.PendingPush.RentalBillingLogs)
            if (!sales || state.SyncedRentalBillingLogs.Any(x => x.Id == log.Id && x.AmountsHidden))
                changed |= Rewrite(log, () => log.BilledAmount = null);
        return changed;
    }

    private static bool Rewrite<T>(T row, Action change) where T : SyncEntityDto
    {
        var before = JsonSerializer.Serialize(row);
        change();
        if (before == JsonSerializer.Serialize(row)) return false;
        row.MutationId = Guid.NewGuid().ToString("N");
        row.MutationCreatedAtUtc = DateTime.UtcNow;
        return true;
    }

    private static string HideRows(string raw, bool runs)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "[]";
        try
        {
            if (JsonNode.Parse(raw) is not JsonArray rows) throw new JsonException();
            foreach (var node in rows)
            {
                if (node is not JsonObject row) throw new JsonException();
                foreach (var key in runs ? new[] { "BilledAmount", "SettledAmount" } : new[] { "UnitPrice", "Amount" })
                {
                    foreach (var name in row.Select(x => x.Key).Where(x => x.Equals(key, StringComparison.OrdinalIgnoreCase)).ToArray())
                        row.Remove(name);
                    row[key] = null;
                }
                if (runs)
                    foreach (var name in row.Select(x => x.Key).Where(x => x.Equals("Items", StringComparison.OrdinalIgnoreCase)).ToArray())
                        row[name] = JsonNode.Parse(HideRows(row[name]?.ToJsonString() ?? "[]", false));
            }
            return rows.ToJsonString();
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException)
        {
            throw new InvalidDataException("미전송 렌탈 청구 형식을 확인할 수 없어 금액을 안전하게 숨길 수 없습니다. 원본을 보존했습니다.", e);
        }
    }
}
