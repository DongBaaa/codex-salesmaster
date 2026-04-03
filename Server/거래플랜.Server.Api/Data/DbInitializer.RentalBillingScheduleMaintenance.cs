using Microsoft.EntityFrameworkCore;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Server.Api.Data;

public static partial class DbInitializer
{
    private static async Task NormalizeRentalBillingScheduleRulesAsync(AppDbContext dbContext, CancellationToken cancellationToken)
    {
        var profiles = await dbContext.RentalBillingProfiles.IgnoreQueryFilters().ToListAsync(cancellationToken);
        if (profiles.Count == 0)
            return;

        var customers = await dbContext.Customers.IgnoreQueryFilters()
            .Where(customer => !customer.IsDeleted)
            .ToListAsync(cancellationToken);
        var customerNameById = customers
            .Where(customer => !string.IsNullOrWhiteSpace(customer.NameOriginal))
            .ToDictionary(customer => customer.Id, customer => customer.NameOriginal.Trim());

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
            var originalCustomerId = profile.CustomerId;
            var originalCustomerName = profile.CustomerName;

            if ((!profile.CustomerId.HasValue || profile.CustomerId.Value == Guid.Empty)
                && TryResolveBillingProfileCustomer(customers, profile, out var resolvedCustomerId))
            {
                profile.CustomerId = resolvedCustomerId;
            }

            if (profile.CustomerId.HasValue &&
                profile.CustomerId.Value != Guid.Empty &&
                customerNameById.TryGetValue(profile.CustomerId.Value, out var customerMasterName))
            {
                var normalizedMasterName = RentalCatalogValueNormalizer.NormalizeDisplayText(customerMasterName);
                profile.CustomerName = normalizedMasterName;
            }

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
                || originalLeadDays != profile.DocumentLeadDays
                || originalCustomerId != profile.CustomerId
                || !string.Equals(originalCustomerName, profile.CustomerName, StringComparison.Ordinal))
            {
                changed = true;
            }
        }

        if (changed)
            await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static bool TryResolveBillingProfileCustomer(
        IReadOnlyCollection<Customer> customers,
        RentalBillingProfile profile,
        out Guid customerId)
    {
        customerId = Guid.Empty;
        var businessNumber = NormalizeBusinessNumber(profile.BusinessNumber);
        if (!string.IsNullOrWhiteSpace(businessNumber))
        {
            var businessMatches = customers
                .Where(customer => NormalizeBusinessNumber(customer.BusinessNumber) == businessNumber)
                .ToList();
            if (businessMatches.Count == 1)
            {
                customerId = businessMatches[0].Id;
                return true;
            }
        }

        var candidateNames = new[]
            {
                profile.CustomerName
            }
            .Select(RentalCatalogValueNormalizer.NormalizeLooseKey)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (candidateNames.Count == 0)
            return false;

        var nameMatches = customers
            .Where(customer =>
            {
                if (!string.IsNullOrWhiteSpace(businessNumber) &&
                    NormalizeBusinessNumber(customer.BusinessNumber) != businessNumber)
                {
                    return false;
                }

                var normalizedCustomerName = RentalCatalogValueNormalizer.NormalizeLooseKey(customer.NameOriginal);
                return candidateNames.Contains(normalizedCustomerName, StringComparer.OrdinalIgnoreCase);
            })
            .ToList();
        if (nameMatches.Count != 1)
            return false;

        customerId = nameMatches[0].Id;
        return true;
    }

    private static string NormalizeBusinessNumber(string? businessNumber)
        => new((businessNumber ?? string.Empty).Where(char.IsDigit).ToArray());
}
