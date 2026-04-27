using System.Security.Cryptography;
using System.Text;

namespace 거래플랜.Shared.Contracts;

public static class SyncIdentityGenerator
{
    private const string RentalBillingProfileSeedPrefix = "RENTAL-BILLING-PROFILE|";
    private const string RentalBillingRunSeedPrefix = "RENTAL-BILLING-RUN|";
    private const string RentalAssetAssignmentHistorySeedPrefix = "RENTAL-ASSET-ASSIGNMENT-HISTORY|";

    public static Guid CreateRentalBillingProfileId(string? profileKey)
    {
        var normalizedKey = NormalizeKey(profileKey);
        if (string.IsNullOrWhiteSpace(normalizedKey))
            return Guid.Empty;

        var bytes = Encoding.UTF8.GetBytes(RentalBillingProfileSeedPrefix + normalizedKey);
        var hash = MD5.HashData(bytes);
        return new Guid(hash);
    }

    public static Guid CreateRentalBillingRunId(Guid profileId, string? runKey)
    {
        if (profileId == Guid.Empty)
            return Guid.Empty;

        var normalizedRunKey = NormalizeKey(runKey);
        if (string.IsNullOrWhiteSpace(normalizedRunKey))
            return Guid.Empty;

        var bytes = Encoding.UTF8.GetBytes($"{RentalBillingRunSeedPrefix}{profileId:D}|{normalizedRunKey}");
        var hash = MD5.HashData(bytes);
        return new Guid(hash);
    }

    public static Guid CreateRentalAssetAssignmentHistoryId(
        Guid assetId,
        DateTime linkedAtUtc,
        Guid? billingProfileId,
        Guid? customerId,
        string? customerName,
        string? installLocation)
    {
        if (assetId == Guid.Empty)
            return Guid.Empty;

        var normalizedLinkedAtUtc = NormalizeUtc(linkedAtUtc);
        var seed = string.Join(
            "|",
            RentalAssetAssignmentHistorySeedPrefix,
            assetId.ToString("D"),
            normalizedLinkedAtUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            billingProfileId?.ToString("D") ?? string.Empty,
            customerId?.ToString("D") ?? string.Empty,
            NormalizeKey(customerName),
            NormalizeKey(installLocation));
        var bytes = Encoding.UTF8.GetBytes(seed);
        var hash = MD5.HashData(bytes);
        return new Guid(hash);
    }

    public static string NormalizeKey(string? value)
        => (value ?? string.Empty).Trim().ToUpperInvariant();

    private static DateTime NormalizeUtc(DateTime value)
    {
        if (value == default)
            return DateTime.UnixEpoch;
        if (value.Kind == DateTimeKind.Utc)
            return value;
        if (value.Kind == DateTimeKind.Local)
            return value.ToUniversalTime();
        return DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }
}
