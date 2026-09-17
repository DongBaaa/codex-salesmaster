using System.Globalization;

namespace 거래플랜.Shared.Contracts;

/// <summary>A billing period and its optional deadlines, independently of the day an operator invoices it.</summary>
public sealed record RentalBillingSchedulePlan(
    DateOnly PeriodStartDate,
    DateOnly PeriodEndDate,
    DateOnly? BillingDate,
    DateOnly? DocumentIssueDate,
    bool HasBillingEvidenceInPeriod,
    DateOnly? EarliestBillingDate)
{
    public string RunKey => string.Create(CultureInfo.InvariantCulture, $"{PeriodStartDate:yyyyMMdd}-{PeriodEndDate:yyyyMMdd}");
    public bool HasFixedDeadline => BillingDate.HasValue;
    public DateOnly? AlertDate => BillingDate.HasValue
        ? RentalBillingScheduleRules.ResolveAlertDate(BillingDate.Value, DocumentIssueDate)
        : null;
}

public static partial class RentalBillingScheduleRules
{
    public const string BillingDayModeNoFixedDay = "지정일 없음";
    public const int NoFixedDayCapabilityVersion = 1;
    public const int CurrentMonthCapabilityVersion = 2;
    public const int ScheduleCapabilityVersion = CurrentMonthCapabilityVersion;
    public const string BillingAdvanceModeCurrentMonth = "당월";
    public const string ScheduleUpgradeRequiredMessage = "지정일 없음·당월 렌탈 청구를 안전하게 처리하려면 서버와 앱을 지원 버전으로 업데이트해야 합니다. 변경 자료는 보존되며 지원하지 않는 버전으로 전송하지 않습니다.";

    public static string NormalizeBillingAdvanceMode(string? value)
        => value?.Trim() switch
        {
            "선불" => "선불",
            BillingAdvanceModeCurrentMonth => BillingAdvanceModeCurrentMonth,
            _ => "후불"
        };

    public static int RequiredScheduleCapabilityVersion(string? billingDayMode, string? billingAdvanceMode)
        => NormalizeBillingAdvanceMode(billingAdvanceMode) == BillingAdvanceModeCurrentMonth
            ? CurrentMonthCapabilityVersion
            : IsNoFixedBillingDay(billingDayMode) ? NoFixedDayCapabilityVersion : 0;

    public static bool IsNoFixedBillingDay(string? value)
        => string.Equals(value?.Trim(), BillingDayModeNoFixedDay, StringComparison.Ordinal);

    /// <summary>
    /// Resolves the selected period without advancing an already invoiced manual
    /// period. Its stable key allows callers to find the existing run/tombstone
    /// instead of creating another run on a different day of the same month.
    /// No-fixed-day callers must use this plan, not the legacy date-only methods.
    /// </summary>
    public static RentalBillingSchedulePlan ResolveConfiguredBillingPlan(
        int billingDay,
        string? billingDayMode,
        int cycleMonths,
        int anchorMonth,
        DateOnly referenceDate,
        DateOnly? lastBilledDate = null,
        DateOnly? firstBillingDate = null,
        DateOnly? cycleAnchorDate = null,
        string? documentIssueMode = null,
        int documentLeadDays = 0)
    {
        cycleMonths = NormalizeCycleMonths(cycleMonths);
        if (!IsNoFixedBillingDay(billingDayMode))
        {
            var billingDate = ResolveConfiguredBillingDate(
                billingDay, billingDayMode, cycleMonths, anchorMonth,
                referenceDate, firstBillingDate, cycleAnchorDate);
            var period = ResolveBillingPeriod(cycleMonths, null, billingDate);
            return new RentalBillingSchedulePlan(
                period.StartDate, period.EndDate, billingDate,
                CalculateDocumentIssueDate(billingDate, documentIssueMode, documentLeadDays),
                HasEvidenceInPeriod(lastBilledDate, period.StartDate, period.EndDate),
                firstBillingDate);
        }

        var periodStart = ResolvePeriodStartMonth(cycleMonths, anchorMonth, referenceDate, cycleAnchorDate);
        var periodEnd = periodStart.AddMonths(cycleMonths).AddDays(-1);
        if (firstBillingDate.HasValue && periodEnd < firstBillingDate.Value)
        {
            periodStart = ResolvePeriodStartMonth(cycleMonths, anchorMonth, firstBillingDate.Value, cycleAnchorDate);
            periodEnd = periodStart.AddMonths(cycleMonths).AddDays(-1);
        }

        return new RentalBillingSchedulePlan(
            periodStart, periodEnd, BillingDate: null, DocumentIssueDate: null,
            HasEvidenceInPeriod(lastBilledDate, periodStart, periodEnd), firstBillingDate);
    }

    /// <summary>Preserves legacy next-period rules for fixed dates; manual periods stay selected.</summary>
    public static RentalBillingSchedulePlan ResolveApplicableBillingPlan(
        int billingDay,
        string? billingDayMode,
        int cycleMonths,
        int anchorMonth,
        DateOnly referenceDate,
        DateOnly? lastBilledDate = null,
        DateOnly? firstBillingDate = null,
        DateOnly? cycleAnchorDate = null,
        string? documentIssueMode = null,
        int documentLeadDays = 0)
    {
        if (IsNoFixedBillingDay(billingDayMode))
        {
            return ResolveConfiguredBillingPlan(
                billingDay, billingDayMode, cycleMonths, anchorMonth, referenceDate,
                lastBilledDate, firstBillingDate, cycleAnchorDate, documentIssueMode, documentLeadDays);
        }

        var billingDate = ResolveApplicableBillingDate(
            billingDay, billingDayMode, cycleMonths, anchorMonth,
            referenceDate, lastBilledDate, firstBillingDate, cycleAnchorDate);
        var period = ResolveBillingPeriod(cycleMonths, null, billingDate);
        return new RentalBillingSchedulePlan(
            period.StartDate, period.EndDate, billingDate,
            CalculateDocumentIssueDate(billingDate, documentIssueMode, documentLeadDays),
            HasEvidenceInPeriod(lastBilledDate, period.StartDate, period.EndDate),
            firstBillingDate);
    }

    private static bool HasEvidenceInPeriod(DateOnly? evidenceDate, DateOnly startDate, DateOnly endDate)
        => evidenceDate.HasValue && evidenceDate.Value >= startDate && evidenceDate.Value <= endDate;
}
