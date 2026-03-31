using Microsoft.EntityFrameworkCore;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Server.Api.Data;

public static partial class DbInitializer
{
    private static async Task NormalizeRentalBillingScheduleRulesAsync(AppDbContext dbContext, CancellationToken cancellationToken)
    {
        var profiles = await dbContext.RentalBillingProfiles.IgnoreQueryFilters().ToListAsync(cancellationToken);
        if (profiles.Count == 0)
            return;

        var referenceDate = DateOnly.FromDateTime(DateTime.Today);
        var changed = false;

        foreach (var profile in profiles)
        {
            var originalDayMode = profile.BillingDayMode;
            var originalDay = profile.BillingDay;
            var originalCycleMonths = profile.BillingCycleMonths;
            var originalAnchorMonth = profile.BillingAnchorMonth;
            var originalDocumentIssueMode = profile.DocumentIssueMode;
            var originalLeadDays = profile.DocumentLeadDays;

            var normalizedCycleMonths = RentalBillingScheduleRules.NormalizeCycleMonths(profile.BillingCycleMonths);
            var shouldTreatAsEndOfMonth = profile.BillingDay == 31 || string.Equals((profile.BillingDayMode ?? string.Empty).Trim(), RentalBillingScheduleRules.BillingDayModeEndOfMonth, StringComparison.Ordinal);
            profile.BillingDayMode = shouldTreatAsEndOfMonth
                ? RentalBillingScheduleRules.BillingDayModeEndOfMonth
                : RentalBillingScheduleRules.NormalizeBillingDayMode(profile.BillingDayMode);
            profile.BillingDay = RentalBillingScheduleRules.NormalizeBillingDay(profile.BillingDay);
            profile.BillingCycleMonths = normalizedCycleMonths;
            profile.BillingAnchorMonth = RentalBillingScheduleRules.NormalizeBillingAnchorMonth(
                normalizedCycleMonths,
                profile.BillingAnchorMonth,
                profile.BillingAnchorDate,
                profile.BillingStartDate,
                profile.ContractStartDate,
                profile.ContractDate,
                profile.LastBilledDate,
                referenceDate);
            profile.DocumentIssueMode = RentalBillingScheduleRules.NormalizeDocumentIssueMode(profile.DocumentIssueMode);
            profile.DocumentLeadDays = RentalBillingScheduleRules.NormalizeDocumentLeadDays(profile.DocumentLeadDays);

            if (!string.Equals(originalDayMode, profile.BillingDayMode, StringComparison.Ordinal)
                || originalDay != profile.BillingDay
                || originalCycleMonths != profile.BillingCycleMonths
                || originalAnchorMonth != profile.BillingAnchorMonth
                || !string.Equals(originalDocumentIssueMode, profile.DocumentIssueMode, StringComparison.Ordinal)
                || originalLeadDays != profile.DocumentLeadDays)
            {
                changed = true;
            }
        }

        if (changed)
            await dbContext.SaveChangesAsync(cancellationToken);
    }
}
