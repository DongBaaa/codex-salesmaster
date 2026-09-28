using System.Text.Json;
using GeoraePlan.Mobile.App.Models;
using GeoraePlan.Mobile.App.ViewModels;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Services;

/// <summary>Read-only projections. Never write redacted display copies back to the cache/outbox.</summary>
public readonly record struct MobileRentalAmountAccess(bool Purchase, bool Sales)
{
    public static MobileRentalAmountAccess Capture(SessionSnapshot session)
    {
        var access = MobileItemAmountAccess.Capture(session.IsAuthenticated, session.Role,
            session.Permissions, canEditItems: false);
        return new(access.Purchase, access.Sales);
    }

    public RentalBillingProfileDto Display(RentalBillingProfileDto source)
    {
        if (Sales && !source.AmountsHidden) return source;
        var copy = Copy(source);
        copy.MonthlyAmount = copy.DepositAmount = copy.SettledAmount = copy.OutstandingAmount = null;
        copy.BillingTemplateJson = "[]"; // Templates are not used by this read-only screen.
        var runs = MobileRentalRunSnapshot.Parse(source.BillingRunsJson);
        foreach (var run in runs) run.BilledAmount = run.SettledAmount = null;
        copy.BillingRunsJson = JsonSerializer.Serialize(runs);
        return copy;
    }

    public RentalAssetDto Display(RentalAssetDto source)
    {
        var copy = Copy(source);
        if (!Purchase) copy.PurchasePrice = null;
        if (!Sales || source.SalesAmountsHidden)
        {
            copy.SalePrice = copy.MonthlyFee = null;
            copy.DepositText = null;
            copy.BlackOverageUnitPrice = copy.ColorOverageUnitPrice = null;
            copy.MeterEvidenceJson = "[]";
        }
        return copy;
    }

    public RentalBillingLogDto Display(RentalBillingLogDto source)
    {
        if (Sales) return source;
        var copy = Copy(source); copy.BilledAmount = null; return copy;
    }

    public RentalAssetAssignmentHistoryDto Display(RentalAssetAssignmentHistoryDto source)
    {
        if (Sales) return source;
        var copy = Copy(source); copy.MonthlyFee = null; return copy;
    }

    public static string Money(decimal? amount, bool visible = true)
        => visible && amount.HasValue ? $"{amount:N0}원" : "비공개";

    public static string ProfileNote(RentalBillingProfileDto profile)
    {
        var run = MobileRentalRunSnapshot.Parse(profile.BillingRunsJson)
            .OrderByDescending(x => x.ScheduledDate).ThenByDescending(x => x.PeriodEndDate).FirstOrDefault();
        if (run is not null)
        {
            decimal? outstanding = !profile.AmountsHidden && !run.AmountsHidden
                ? Math.Max(0m, run.BilledAmount!.Value - run.SettledAmount!.Value) : null;
            var dateLabel = RentalBillingScheduleRules.IsNoFixedBillingDay(profile.BillingDayMode) ? "청구 기준일" : "예정";
            return $"최근 회차 {Text(run.PeriodLabel, "기간 미정")} / {dateLabel} {run.ScheduledDate:yyyy-MM-dd} / {Text(run.Status, "예정")} / 미수 {Money(outstanding)}";
        }
        return string.IsNullOrWhiteSpace(profile.Notes)
            ? $"정산 {Text(profile.SettlementStatus, "미정")} / 미수 {Money(profile.OutstandingAmount, !profile.AmountsHidden)}"
            : profile.Notes.Trim();
    }

    private static T Copy<T>(T source) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(source))!;
    private static string Text(string? text, string fallback) => string.IsNullOrWhiteSpace(text) ? fallback : text.Trim();
}
