namespace 거래플랜.Shared.Contracts;

public static class InvoiceReceivingStatuses
{
    public const string NotApplicable = "";
    public const string Pending = "입고확인 필요";
    public const string Confirmed = "입고완료";
    public const string NotRequired = "확인불필요";

    public static string Normalize(string? status, bool isPurchase, bool required = true)
    {
        if (!isPurchase)
            return NotApplicable;

        var normalized = (status ?? string.Empty).Trim();
        if (string.Equals(normalized, Confirmed, StringComparison.OrdinalIgnoreCase))
            return Confirmed;
        if (!required)
            return NotRequired;
        return Pending;
    }

    public static bool IsConfirmed(string? status)
        => string.Equals((status ?? string.Empty).Trim(), Confirmed, StringComparison.OrdinalIgnoreCase);
}
