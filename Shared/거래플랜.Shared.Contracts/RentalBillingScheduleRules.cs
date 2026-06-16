using System;

namespace 거래플랜.Shared.Contracts;

public static class RentalBillingScheduleRules
{
    public const string BillingDayModeFixedDay = "고정일";
    public const string BillingDayModeEndOfMonth = "말일";

    public const string DocumentIssueModeSameAsDueDate = "결제일과 동일";
    public const string DocumentIssueModeDaysBeforeDueDate = "결제일 기준 며칠 전";
    public const string DocumentIssueModePreviousBusinessDay = "직전 영업일";
    public const string DocumentIssueModePreviousMonthEnd = "전월 말일";

    public static string NormalizeBillingDayMode(string? value)
        => string.Equals((value ?? string.Empty).Trim(), BillingDayModeEndOfMonth, StringComparison.Ordinal)
            ? BillingDayModeEndOfMonth
            : BillingDayModeFixedDay;

    public static string NormalizeDocumentIssueMode(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized switch
        {
            DocumentIssueModeSameAsDueDate => DocumentIssueModeSameAsDueDate,
            DocumentIssueModeDaysBeforeDueDate => DocumentIssueModeDaysBeforeDueDate,
            DocumentIssueModePreviousBusinessDay => DocumentIssueModePreviousBusinessDay,
            DocumentIssueModePreviousMonthEnd => DocumentIssueModePreviousMonthEnd,
            _ => DocumentIssueModeSameAsDueDate
        };
    }

    public static int NormalizeBillingDay(int day)
        => Math.Clamp(day <= 0 ? 25 : day, 1, 31);

    public static int NormalizeCycleMonths(int months)
        => Math.Max(1, months);

    public static int NormalizeDocumentLeadDays(int leadDays)
        => Math.Max(0, leadDays);

    public static int NormalizeBillingAnchorMonth(
        int cycleMonths,
        int? billingAnchorMonth,
        DateOnly? billingAnchorDate,
        DateOnly? billingStartDate,
        DateOnly? contractStartDate,
        DateOnly? contractDate,
        DateOnly? lastBilledDate,
        DateOnly referenceDate)
    {
        if (billingAnchorMonth is >= 1 and <= 12)
            return billingAnchorMonth.Value;

        cycleMonths = NormalizeCycleMonths(cycleMonths);
        var fallback = billingAnchorDate
                       ?? billingStartDate
                       ?? contractStartDate
                       ?? contractDate
                       ?? lastBilledDate;
        if (fallback.HasValue)
            return Math.Clamp(fallback.Value.Month, 1, 12);

        return cycleMonths switch
        {
            1 => Math.Clamp(referenceDate.Month, 1, 12),
            3 => 3,
            _ => 1
        };
    }

    public static DateOnly BuildBillingDate(int year, int month, int billingDay, string? billingDayMode)
    {
        var resolvedMode = NormalizeBillingDayMode(billingDayMode);
        var lastDay = DateTime.DaysInMonth(year, month);
        var day = string.Equals(resolvedMode, BillingDayModeEndOfMonth, StringComparison.Ordinal)
            ? lastDay
            : Math.Clamp(NormalizeBillingDay(billingDay), 1, lastDay);
        return new DateOnly(year, month, day);
    }

    public static bool IsBillingMonth(
        int cycleMonths,
        int anchorMonth,
        DateOnly referenceDate)
    {
        cycleMonths = NormalizeCycleMonths(cycleMonths);
        if (cycleMonths == 1)
            return true;

        var lag = GetBillingLagMonths(referenceDate.Month, anchorMonth, cycleMonths);
        return lag == 0;
    }

    public static DateOnly ResolveApplicableBillingDate(
        int billingDay,
        string? billingDayMode,
        int cycleMonths,
        int anchorMonth,
        DateOnly referenceDate,
        DateOnly? lastBilledDate)
        => ResolveApplicableBillingDate(
            billingDay,
            billingDayMode,
            cycleMonths,
            anchorMonth,
            referenceDate,
            lastBilledDate,
            firstBillingDate: null);

    public static DateOnly ResolveApplicableBillingDate(
        int billingDay,
        string? billingDayMode,
        int cycleMonths,
        int anchorMonth,
        DateOnly referenceDate,
        DateOnly? lastBilledDate,
        DateOnly? firstBillingDate)
    {
        cycleMonths = NormalizeCycleMonths(cycleMonths);
        anchorMonth = Math.Clamp(anchorMonth, 1, 12);
        var lag = cycleMonths == 1 ? 0 : GetBillingLagMonths(referenceDate.Month, anchorMonth, cycleMonths);
        var applicableMonth = new DateOnly(referenceDate.Year, referenceDate.Month, 1).AddMonths(-lag);
        var candidate = BuildBillingDate(applicableMonth.Year, applicableMonth.Month, billingDay, billingDayMode);

        if (firstBillingDate.HasValue)
        {
            while (candidate < firstBillingDate.Value)
            {
                applicableMonth = applicableMonth.AddMonths(cycleMonths);
                candidate = BuildBillingDate(applicableMonth.Year, applicableMonth.Month, billingDay, billingDayMode);
            }
        }

        if (lastBilledDate.HasValue)
        {
            while (candidate <= lastBilledDate.Value)
            {
                applicableMonth = applicableMonth.AddMonths(cycleMonths);
                candidate = BuildBillingDate(applicableMonth.Year, applicableMonth.Month, billingDay, billingDayMode);
            }
        }

        return candidate;
    }

    public static (DateOnly StartDate, DateOnly EndDate) ResolveBillingPeriod(
        int cycleMonths,
        string? billingAdvanceMode,
        DateOnly scheduledDate)
    {
        cycleMonths = NormalizeCycleMonths(cycleMonths);
        var monthStart = new DateOnly(scheduledDate.Year, scheduledDate.Month, 1);
        var endMonth = monthStart.AddMonths(cycleMonths - 1);
        var end = new DateOnly(endMonth.Year, endMonth.Month, DateTime.DaysInMonth(endMonth.Year, endMonth.Month));

        return (monthStart, end);
    }

    public static DateOnly? CalculateDocumentIssueDate(
        DateOnly? dueDate,
        string? documentIssueMode,
        int documentLeadDays)
    {
        if (!dueDate.HasValue)
            return null;

        var normalizedMode = NormalizeDocumentIssueMode(documentIssueMode);
        documentLeadDays = NormalizeDocumentLeadDays(documentLeadDays);
        return normalizedMode switch
        {
            DocumentIssueModeDaysBeforeDueDate => dueDate.Value.AddDays(-documentLeadDays),
            DocumentIssueModePreviousBusinessDay => GetPreviousBusinessDay(dueDate.Value),
            DocumentIssueModePreviousMonthEnd => new DateOnly(dueDate.Value.AddMonths(-1).Year, dueDate.Value.AddMonths(-1).Month, DateTime.DaysInMonth(dueDate.Value.AddMonths(-1).Year, dueDate.Value.AddMonths(-1).Month)),
            _ => dueDate.Value
        };
    }

    public static DateOnly ResolveAlertDate(DateOnly billingDate, DateOnly? documentIssueDate)
        => documentIssueDate.HasValue && documentIssueDate.Value < billingDate
            ? documentIssueDate.Value
            : billingDate;

    public static string ResolveAlertReason(DateOnly billingDate, DateOnly? documentIssueDate)
        => documentIssueDate.HasValue && documentIssueDate.Value < billingDate
            ? "서류발송"
            : "청구";

    private static int GetBillingLagMonths(int referenceMonth, int anchorMonth, int cycleMonths)
    {
        var raw = (referenceMonth - anchorMonth) % cycleMonths;
        if (raw < 0)
            raw += cycleMonths;
        return raw;
    }

    private static DateOnly GetPreviousBusinessDay(DateOnly dueDate)
    {
        var candidate = dueDate.AddDays(-1);
        while (candidate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            candidate = candidate.AddDays(-1);
        return candidate;
    }
}
