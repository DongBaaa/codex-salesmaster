using 거래플랜.Shared.Contracts;
using 거래플랜.Desktop.App.Services;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalBillingSchedulePlanTests
{
    [Theory]
    [InlineData(2026, 9, 1, 30)]
    [InlineData(2026, 9, 17, 30)]
    [InlineData(2026, 9, 30, 30)]
    [InlineData(2028, 2, 1, 29)]
    [InlineData(2028, 2, 29, 29)]
    [InlineData(2027, 2, 28, 28)]
    [InlineData(2026, 12, 31, 31)]
    public void NoFixedDay_UsesWholeMonthWithoutInventingDeadlines(int year, int month, int day, int lastDay)
    {
        foreach (var storedDay in new[] { 0, 1, 25, 31 })
        foreach (var documentMode in new[] { "결제일과 동일", "결제일 기준 며칠 전", "직전 영업일", "전월 말일" })
        {
            var plan = RentalBillingScheduleRules.ResolveConfiguredBillingPlan(
                storedDay, RentalBillingScheduleRules.BillingDayModeNoFixedDay, 1, month,
                new DateOnly(year, month, day), documentIssueMode: documentMode, documentLeadDays: 3);
            Assert.Equal(new DateOnly(year, month, 1), plan.PeriodStartDate);
            Assert.Equal(new DateOnly(year, month, lastDay), plan.PeriodEndDate);
            Assert.Null(plan.BillingDate);
            Assert.Null(plan.DocumentIssueDate);
            Assert.Null(plan.AlertDate);
            Assert.False(plan.HasFixedDeadline);
            Assert.False(plan.HasBillingEvidenceInPeriod);
        }
    }

    [Fact]
    public void NoFixedDay_ReopeningAfterInvoiceKeepsSameRunKeyForEntireMonth()
    {
        var expectedKey = "20260901-20260930";
        for (var day = 1; day <= 30; day++)
        {
            var plan = RentalBillingScheduleRules.ResolveApplicableBillingPlan(
                0, RentalBillingScheduleRules.BillingDayModeNoFixedDay, 1, 9,
                new DateOnly(2026, 9, day), new DateOnly(2026, 9, 1));
            Assert.Equal(expectedKey, plan.RunKey);
            Assert.True(plan.HasBillingEvidenceInPeriod);
            Assert.Null(plan.BillingDate);
        }
        var october = RentalBillingScheduleRules.ResolveApplicableBillingPlan(
            0, RentalBillingScheduleRules.BillingDayModeNoFixedDay, 1, 9,
            new DateOnly(2026, 10, 1), new DateOnly(2026, 9, 30));
        Assert.Equal("20261001-20261031", october.RunKey);
        Assert.False(october.HasBillingEvidenceInPeriod);
    }

    [Fact]
    public void NoFixedDay_UsesExistingRunIdentityAndHonorsDeletedPeriodOnLaterDay()
    {
        var profileId = Guid.NewGuid();
        var first = RentalBillingScheduleRules.ResolveConfiguredBillingPlan(
            0, RentalBillingScheduleRules.BillingDayModeNoFixedDay, 1, 9, new DateOnly(2026, 9, 1));
        var tombstone = new RentalBillingRunModel
        {
            RunKey = first.RunKey,
            RunId = SyncIdentityGenerator.CreateRentalBillingRunId(profileId, first.RunKey),
            IsTombstoned = true
        };
        for (var day = 2; day <= 30; day++)
        {
            var reopened = RentalBillingScheduleRules.ResolveApplicableBillingPlan(
                0, RentalBillingScheduleRules.BillingDayModeNoFixedDay, 1, 9, new DateOnly(2026, 9, day));
            var candidate = new RentalBillingRunModel
            {
                RunKey = reopened.RunKey,
                RunId = SyncIdentityGenerator.CreateRentalBillingRunId(profileId, reopened.RunKey)
            };
            Assert.Equal(tombstone.RunId, candidate.RunId);
            Assert.True(RentalBillingRunIdentityPolicy.IsSuppressedByTombstone([tombstone], candidate));
        }
        var nextMonth = RentalBillingScheduleRules.ResolveConfiguredBillingPlan(
            0, RentalBillingScheduleRules.BillingDayModeNoFixedDay, 1, 9, new DateOnly(2026, 10, 1));
        var nextCandidate = new RentalBillingRunModel
        {
            RunKey = nextMonth.RunKey,
            RunId = SyncIdentityGenerator.CreateRentalBillingRunId(profileId, nextMonth.RunKey)
        };
        Assert.False(RentalBillingRunIdentityPolicy.IsSuppressedByTombstone([tombstone], nextCandidate));
    }

    [Theory]
    [InlineData(1, "20260901-20260930")]
    [InlineData(20, "20260901-20260930")]
    public void NoFixedDay_StartConstraintDoesNotBecomeDueDate(int referenceDay, string key)
    {
        var plan = RentalBillingScheduleRules.ResolveConfiguredBillingPlan(
            31, RentalBillingScheduleRules.BillingDayModeNoFixedDay, 1, 9,
            new DateOnly(2026, 9, referenceDay), firstBillingDate: new DateOnly(2026, 9, 20));
        Assert.Equal(key, plan.RunKey);
        Assert.Equal(new DateOnly(2026, 9, 20), plan.EarliestBillingDate);
        Assert.Null(plan.BillingDate);
    }

    [Fact]
    public void NoFixedDay_FutureStartAndMultiMonthCycleKeepPeriodBoundary()
    {
        var plan = RentalBillingScheduleRules.ResolveApplicableBillingPlan(
            0, RentalBillingScheduleRules.BillingDayModeNoFixedDay, 3, 11,
            new DateOnly(2026, 7, 10), firstBillingDate: new DateOnly(2026, 12, 5),
            cycleAnchorDate: new DateOnly(2026, 11, 1));
        Assert.Equal("20261101-20270131", plan.RunKey);
        Assert.Null(plan.BillingDate);
        Assert.False(plan.HasBillingEvidenceInPeriod);
    }

    [Theory]
    [InlineData("고정일", 25)]
    [InlineData("말일", 31)]
    [InlineData("legacy-unknown", 0)]
    public void ExistingModes_PlanMatchesLegacyDatesAndPeriods(string mode, int day)
    {
        foreach (var month in Enumerable.Range(1, 12))
        foreach (var cycle in new[] { 1, 3, 6, 12, 18 })
        foreach (var lastBilled in new DateOnly?[] { null, new DateOnly(2028, month, 1) })
        {
            var reference = new DateOnly(2028, month, 17);
            var anchor = new DateOnly(2026, 1, 1);
            var legacy = RentalBillingScheduleRules.ResolveApplicableBillingDate(day, mode, cycle, 1,
                reference, lastBilled, null, anchor);
            var plan = RentalBillingScheduleRules.ResolveApplicableBillingPlan(day, mode, cycle, 1,
                reference, lastBilled, cycleAnchorDate: anchor,
                documentIssueMode: RentalBillingScheduleRules.DocumentIssueModeDaysBeforeDueDate, documentLeadDays: 3);
            Assert.Equal(legacy, plan.BillingDate);
            Assert.Equal(legacy.AddDays(-3), plan.DocumentIssueDate);
            Assert.Equal(plan.DocumentIssueDate, plan.AlertDate);
            Assert.True(plan.HasFixedDeadline);
            var period = RentalBillingScheduleRules.ResolveBillingPeriod(cycle, null, legacy);
            Assert.Equal(period.StartDate, plan.PeriodStartDate);
            Assert.Equal(period.EndDate, plan.PeriodEndDate);
        }
    }
}
