using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalBillingScheduleRulesTests
{
    [Theory]
    [InlineData("후불")]
    [InlineData("선불")]
    public void ResolveBillingPeriod_UsesScheduledMonthAsEndMonth_ForSixMonthCycle(string billingAdvanceMode)
    {
        var period = RentalBillingScheduleRules.ResolveBillingPeriod(
            cycleMonths: 6,
            billingAdvanceMode,
            scheduledDate: new DateOnly(2026, 12, 25));

        Assert.Equal(new DateOnly(2026, 7, 1), period.StartDate);
        Assert.Equal(new DateOnly(2026, 12, 31), period.EndDate);
    }

    [Fact]
    public void ResolveBillingPeriod_StartMonthCanCrossYearBoundary()
    {
        var period = RentalBillingScheduleRules.ResolveBillingPeriod(
            cycleMonths: 12,
            billingAdvanceMode: "후불",
            scheduledDate: new DateOnly(2027, 6, 25));

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

        Assert.Equal(new DateOnly(2026, 12, 25), scheduledDate);
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

        Assert.Equal(new DateOnly(2026, 12, 25), scheduledDate);
    }

    [Fact]
    public void ResolveApplicableBillingDate_UsesCycleEndMonthForQuarterlyStartMonth()
    {
        var scheduledDate = RentalBillingScheduleRules.ResolveApplicableBillingDate(
            billingDay: 25,
            billingDayMode: RentalBillingScheduleRules.BillingDayModeFixedDay,
            cycleMonths: 3,
            anchorMonth: 4,
            referenceDate: new DateOnly(2026, 6, 29),
            lastBilledDate: null);
        var period = RentalBillingScheduleRules.ResolveBillingPeriod(
            cycleMonths: 3,
            billingAdvanceMode: "후불",
            scheduledDate);

        Assert.Equal(new DateOnly(2026, 6, 25), scheduledDate);
        Assert.Equal(new DateOnly(2026, 4, 1), period.StartDate);
        Assert.Equal(new DateOnly(2026, 6, 30), period.EndDate);
    }

    [Fact]
    public void ResolveApplicableBillingDate_WhenReferenceIsNextCycleStart_KeepsConfiguredCycle()
    {
        var scheduledDate = RentalBillingScheduleRules.ResolveApplicableBillingDate(
            billingDay: 25,
            billingDayMode: RentalBillingScheduleRules.BillingDayModeFixedDay,
            cycleMonths: 4,
            anchorMonth: 3,
            referenceDate: new DateOnly(2026, 7, 2),
            lastBilledDate: null,
            firstBillingDate: new DateOnly(2026, 3, 25));
        var period = RentalBillingScheduleRules.ResolveBillingPeriod(
            cycleMonths: 4,
            billingAdvanceMode: "후불",
            scheduledDate);

        Assert.Equal(new DateOnly(2026, 10, 25), scheduledDate);
        Assert.Equal(new DateOnly(2026, 7, 1), period.StartDate);
        Assert.Equal(new DateOnly(2026, 10, 31), period.EndDate);
    }

    [Fact]
    public void ResolveApplicableBillingDate_WhenPreviousCycleAlreadyBilled_UsesNextCycleEnd()
    {
        var scheduledDate = RentalBillingScheduleRules.ResolveApplicableBillingDate(
            billingDay: 25,
            billingDayMode: RentalBillingScheduleRules.BillingDayModeFixedDay,
            cycleMonths: 4,
            anchorMonth: 3,
            referenceDate: new DateOnly(2026, 7, 2),
            lastBilledDate: new DateOnly(2026, 6, 25),
            firstBillingDate: new DateOnly(2026, 3, 25));
        var period = RentalBillingScheduleRules.ResolveBillingPeriod(
            cycleMonths: 4,
            billingAdvanceMode: "후불",
            scheduledDate);

        Assert.Equal(new DateOnly(2026, 10, 25), scheduledDate);
        Assert.Equal(new DateOnly(2026, 7, 1), period.StartDate);
        Assert.Equal(new DateOnly(2026, 10, 31), period.EndDate);
    }

    [Fact]
    public void ResolveApplicableBillingDate_DuringCurrentQuarter_KeepsConfiguredStartMonth()
    {
        var scheduledDate = RentalBillingScheduleRules.ResolveApplicableBillingDate(
            billingDay: 25,
            billingDayMode: RentalBillingScheduleRules.BillingDayModeFixedDay,
            cycleMonths: 3,
            anchorMonth: 6,
            referenceDate: new DateOnly(2026, 7, 13),
            lastBilledDate: null,
            firstBillingDate: new DateOnly(2026, 1, 25));
        var period = RentalBillingScheduleRules.ResolveBillingPeriod(
            cycleMonths: 3,
            billingAdvanceMode: "후불",
            scheduledDate);

        Assert.Equal(new DateOnly(2026, 8, 25), scheduledDate);
        Assert.Equal(new DateOnly(2026, 6, 1), period.StartDate);
        Assert.Equal(new DateOnly(2026, 8, 31), period.EndDate);
    }

    [Fact]
    public void ResolveConfiguredBillingDate_QuarterlyStartMonthSeven_ResolvesJulyToSeptember()
    {
        var scheduledDate = RentalBillingScheduleRules.ResolveConfiguredBillingDate(
            billingDay: 25,
            billingDayMode: RentalBillingScheduleRules.BillingDayModeFixedDay,
            cycleMonths: 3,
            anchorMonth: 7,
            referenceDate: new DateOnly(2026, 7, 13));
        var period = RentalBillingScheduleRules.ResolveBillingPeriod(
            cycleMonths: 3,
            billingAdvanceMode: "후불",
            scheduledDate);

        Assert.Equal(new DateOnly(2026, 9, 25), scheduledDate);
        Assert.Equal(new DateOnly(2026, 7, 1), period.StartDate);
        Assert.Equal(new DateOnly(2026, 9, 30), period.EndDate);
    }

    [Fact]
    public void ResolveApplicableBillingDate_TwentyFourMonthCycle_UsesAnchorYear()
    {
        var cycleAnchorDate = new DateOnly(2024, 3, 1);

        var currentCycleDate = RentalBillingScheduleRules.ResolveApplicableBillingDate(
            billingDay: 25,
            billingDayMode: RentalBillingScheduleRules.BillingDayModeFixedDay,
            cycleMonths: 24,
            anchorMonth: 3,
            referenceDate: new DateOnly(2026, 2, 10),
            lastBilledDate: null,
            firstBillingDate: null,
            cycleAnchorDate: cycleAnchorDate);
        var nextCycleDate = RentalBillingScheduleRules.ResolveApplicableBillingDate(
            billingDay: 25,
            billingDayMode: RentalBillingScheduleRules.BillingDayModeFixedDay,
            cycleMonths: 24,
            anchorMonth: 3,
            referenceDate: new DateOnly(2027, 2, 10),
            lastBilledDate: null,
            firstBillingDate: null,
            cycleAnchorDate: cycleAnchorDate);

        Assert.Equal(new DateOnly(2026, 2, 25), currentCycleDate);
        Assert.Equal(new DateOnly(2028, 2, 25), nextCycleDate);
        Assert.True(RentalBillingScheduleRules.IsBillingMonth(24, 3, new DateOnly(2026, 2, 1), cycleAnchorDate));
        Assert.False(RentalBillingScheduleRules.IsBillingMonth(24, 3, new DateOnly(2027, 2, 1), cycleAnchorDate));
    }

    [Fact]
    public void ResolveConfiguredBillingDate_FiveMonthCycle_DoesNotResetAtCalendarYear()
    {
        var scheduledDate = RentalBillingScheduleRules.ResolveConfiguredBillingDate(
            billingDay: 25,
            billingDayMode: RentalBillingScheduleRules.BillingDayModeFixedDay,
            cycleMonths: 5,
            anchorMonth: 7,
            referenceDate: new DateOnly(2026, 1, 13),
            firstBillingDate: null,
            cycleAnchorDate: new DateOnly(2025, 7, 1));
        var period = RentalBillingScheduleRules.ResolveBillingPeriod(
            cycleMonths: 5,
            billingAdvanceMode: "후불",
            scheduledDate);

        Assert.Equal(new DateOnly(2026, 4, 25), scheduledDate);
        Assert.Equal(new DateOnly(2025, 12, 1), period.StartDate);
        Assert.Equal(new DateOnly(2026, 4, 30), period.EndDate);
    }

    [Fact]
    public void ResolveCycleAnchorDate_UsesFirstConfiguredMonthOnOrAfterStoredStart()
    {
        var anchorDate = RentalBillingScheduleRules.ResolveCycleAnchorDate(
            anchorMonth: 3,
            referenceDate: new DateOnly(2026, 7, 13),
            billingAnchorDate: new DateOnly(2024, 9, 4),
            billingStartDate: null,
            contractStartDate: null,
            contractDate: null);

        Assert.Equal(new DateOnly(2025, 3, 1), anchorDate);
    }
}
