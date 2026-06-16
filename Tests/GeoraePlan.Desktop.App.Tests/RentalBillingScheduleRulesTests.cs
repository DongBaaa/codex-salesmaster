using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalBillingScheduleRulesTests
{
    [Theory]
    [InlineData("후불")]
    [InlineData("선불")]
    public void ResolveBillingPeriod_UsesScheduledMonthAsStartMonth_ForSixMonthCycle(string billingAdvanceMode)
    {
        var period = RentalBillingScheduleRules.ResolveBillingPeriod(
            cycleMonths: 6,
            billingAdvanceMode,
            scheduledDate: new DateOnly(2026, 7, 25));

        Assert.Equal(new DateOnly(2026, 7, 1), period.StartDate);
        Assert.Equal(new DateOnly(2026, 12, 31), period.EndDate);
    }

    [Fact]
    public void ResolveBillingPeriod_StartMonthCanCrossYearBoundary()
    {
        var period = RentalBillingScheduleRules.ResolveBillingPeriod(
            cycleMonths: 12,
            billingAdvanceMode: "후불",
            scheduledDate: new DateOnly(2026, 7, 25));

        Assert.Equal(new DateOnly(2026, 7, 1), period.StartDate);
        Assert.Equal(new DateOnly(2027, 6, 30), period.EndDate);
    }

    [Fact]
    public void ResolveApplicableBillingDate_AndBillingPeriod_MatchSelectedStartMonthSettings()
    {
        var scheduledDate = RentalBillingScheduleRules.ResolveApplicableBillingDate(
            billingDay: 25,
            billingDayMode: RentalBillingScheduleRules.BillingDayModeFixedDay,
            cycleMonths: 6,
            anchorMonth: 7,
            referenceDate: new DateOnly(2026, 7, 20),
            lastBilledDate: null);
        var period = RentalBillingScheduleRules.ResolveBillingPeriod(
            cycleMonths: 6,
            billingAdvanceMode: "후불",
            scheduledDate);

        Assert.Equal(new DateOnly(2026, 7, 25), scheduledDate);
        Assert.Equal(new DateOnly(2026, 7, 1), period.StartDate);
        Assert.Equal(new DateOnly(2026, 12, 31), period.EndDate);
    }

    [Fact]
    public void ResolveApplicableBillingDate_DoesNotBackfillBeforeFirstBillingDate()
    {
        var scheduledDate = RentalBillingScheduleRules.ResolveApplicableBillingDate(
            billingDay: 25,
            billingDayMode: RentalBillingScheduleRules.BillingDayModeFixedDay,
            cycleMonths: 6,
            anchorMonth: 7,
            referenceDate: new DateOnly(2026, 6, 16),
            lastBilledDate: null,
            firstBillingDate: new DateOnly(2026, 7, 25));

        Assert.Equal(new DateOnly(2026, 7, 25), scheduledDate);
    }
}
