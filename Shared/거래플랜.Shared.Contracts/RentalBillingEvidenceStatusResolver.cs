namespace 거래플랜.Shared.Contracts;

public static class RentalBillingEvidenceStatusResolver
{
    public const string Completed = "완료";
    public const string InProgress = "청구중";
    public const string PartiallySettled = "부분입금";
    public const string OnHold = "보류";
    public const string Cancelled = "취소";

    public static string Resolve(
        string? runStatus,
        bool hasFinancialEvidence,
        decimal settledAmount,
        decimal outstandingAmount,
        decimal billedAmount)
    {
        if (billedAmount > 0m && outstandingAmount <= 0m)
            return Completed;

        var normalizedRunStatus = Normalize(runStatus, string.Empty);
        if (IsManualStopStatus(normalizedRunStatus))
            return normalizedRunStatus;

        if (hasFinancialEvidence && settledAmount > 0m && outstandingAmount > 0m)
            return PartiallySettled;

        if (hasFinancialEvidence && billedAmount > 0m)
            return InProgress;

        if (string.Equals(normalizedRunStatus, Completed, StringComparison.OrdinalIgnoreCase) && outstandingAmount > 0m)
            return settledAmount > 0m ? PartiallySettled : InProgress;

        return Normalize(normalizedRunStatus, InProgress);
    }

    public static bool IsManualStopStatus(string? status)
        => string.Equals(Normalize(status, string.Empty), OnHold, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(Normalize(status, string.Empty), Cancelled, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
