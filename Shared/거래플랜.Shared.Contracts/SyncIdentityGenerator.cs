using System.Security.Cryptography;
using System.Text;

namespace 거래플랜.Shared.Contracts;

public static class SyncIdentityGenerator
{
    private const string RentalBillingProfileSeedPrefix = "RENTAL-BILLING-PROFILE|";

    public static Guid CreateRentalBillingProfileId(string? profileKey)
    {
        var normalizedKey = NormalizeKey(profileKey);
        if (string.IsNullOrWhiteSpace(normalizedKey))
            return Guid.Empty;

        var bytes = Encoding.UTF8.GetBytes(RentalBillingProfileSeedPrefix + normalizedKey);
        var hash = MD5.HashData(bytes);
        return new Guid(hash);
    }

    public static string NormalizeKey(string? value)
        => (value ?? string.Empty).Trim().ToUpperInvariant();
}
