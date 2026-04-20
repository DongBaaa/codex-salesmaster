using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

public sealed partial class LocalStateService
{
    private const string SyncOfficeCredentialPrefix = "Sync.OfficeCredential.";
    private const string SyncOfficeCredentialUsernameSuffix = ".Username";
    private const string SyncOfficeCredentialTenantSuffix = ".TenantCode";
    private const string SyncOfficeCredentialPasswordSuffix = ".PasswordProtected";
    private const string SyncOfficeCredentialSavedAtSuffix = ".SavedAtUtc";

    public async Task SaveOfficeSyncCredentialAsync(
        UserSessionDto user,
        string username,
        string password,
        CancellationToken ct = default)
    {
        var normalizedUsername = (username ?? string.Empty).Trim();
        var normalizedPassword = password ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedUsername) || string.IsNullOrEmpty(normalizedPassword))
            return;

        var normalizedOfficeCode = NormalizeOfficeCode(user.OfficeCode, ResolveOfficeCodeFromTenant(user.TenantCode));
        var normalizedTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(user.TenantCode, normalizedOfficeCode);
        if (string.IsNullOrWhiteSpace(normalizedOfficeCode))
            return;

        await SetSettingAsync(GetSyncCredentialSettingKey(normalizedOfficeCode, SyncOfficeCredentialUsernameSuffix), normalizedUsername, ct);
        await SetSettingAsync(GetSyncCredentialSettingKey(normalizedOfficeCode, SyncOfficeCredentialTenantSuffix), normalizedTenantCode, ct);
        await SetSettingAsync(GetSyncCredentialSettingKey(normalizedOfficeCode, SyncOfficeCredentialPasswordSuffix), ProtectSyncCredential(normalizedPassword), ct);
        await SetSettingAsync(GetSyncCredentialSettingKey(normalizedOfficeCode, SyncOfficeCredentialSavedAtSuffix), DateTime.UtcNow.ToString("O"), ct);
    }

    public async Task<StoredSyncCredential?> GetStoredSyncCredentialAsync(
        string? officeCode,
        CancellationToken ct = default)
    {
        var normalizedOfficeCode = NormalizeOfficeCode(officeCode, string.Empty);
        if (string.IsNullOrWhiteSpace(normalizedOfficeCode))
            return null;

        return (await GetStoredSyncCredentialsAsync(ct))
            .FirstOrDefault(credential => string.Equals(credential.OfficeCode, normalizedOfficeCode, StringComparison.OrdinalIgnoreCase));
    }

    public async Task ClearOfficeSyncCredentialAsync(string? officeCode, CancellationToken ct = default)
    {
        var normalizedOfficeCode = NormalizeOfficeCode(officeCode, string.Empty);
        if (string.IsNullOrWhiteSpace(normalizedOfficeCode))
            return;

        await SetSettingAsync(GetSyncCredentialSettingKey(normalizedOfficeCode, SyncOfficeCredentialUsernameSuffix), string.Empty, ct);
        await SetSettingAsync(GetSyncCredentialSettingKey(normalizedOfficeCode, SyncOfficeCredentialTenantSuffix), string.Empty, ct);
        await SetSettingAsync(GetSyncCredentialSettingKey(normalizedOfficeCode, SyncOfficeCredentialPasswordSuffix), string.Empty, ct);
        await SetSettingAsync(GetSyncCredentialSettingKey(normalizedOfficeCode, SyncOfficeCredentialSavedAtSuffix), string.Empty, ct);
    }

    public async Task<int> ClearInvalidOfficeSyncCredentialsAsync(CancellationToken ct = default)
    {
        var settings = await _db.Settings
            .Where(setting => setting.Key.StartsWith(SyncOfficeCredentialPrefix))
            .ToListAsync(ct);
        if (settings.Count == 0)
            return 0;

        var grouped = new Dictionary<string, Dictionary<string, LocalSetting>>(StringComparer.OrdinalIgnoreCase);
        foreach (var setting in settings)
        {
            if (!TryParseSyncCredentialSetting(setting.Key, out var officeCode, out var suffix))
                continue;

            if (!grouped.TryGetValue(officeCode, out var bucket))
            {
                bucket = new Dictionary<string, LocalSetting>(StringComparer.OrdinalIgnoreCase);
                grouped[officeCode] = bucket;
            }

            bucket[suffix] = setting;
        }

        var mutated = false;
        var clearedOfficeCount = 0;
        foreach (var (officeCode, bucket) in grouped)
        {
            var normalizedOfficeCode = NormalizeOfficeCode(officeCode, string.Empty);
            var isValidOfficeCode = !string.IsNullOrWhiteSpace(normalizedOfficeCode);
            var username = bucket.TryGetValue(SyncOfficeCredentialUsernameSuffix, out var usernameSetting)
                ? usernameSetting.Value?.Trim()
                : string.Empty;
            var password = bucket.TryGetValue(SyncOfficeCredentialPasswordSuffix, out var passwordSetting)
                ? UnprotectSyncCredential(passwordSetting.Value)
                : string.Empty;

            if (isValidOfficeCode && !string.IsNullOrWhiteSpace(username) && !string.IsNullOrEmpty(password))
                continue;

            var bucketMutated = false;
            foreach (var setting in bucket.Values)
            {
                setting.Value = string.Empty;
                mutated = true;
                bucketMutated = true;
            }

            if (bucketMutated)
                clearedOfficeCount++;
        }

        if (mutated)
            await _db.SaveChangesAsync(ct);

        return clearedOfficeCount;
    }

    public async Task<IReadOnlyList<StoredSyncCredential>> GetStoredSyncCredentialsAsync(CancellationToken ct = default)
    {
        var settings = await _db.Settings.AsNoTracking()
            .Where(setting => setting.Key.StartsWith(SyncOfficeCredentialPrefix))
            .ToListAsync(ct);

        if (settings.Count == 0)
            return [];

        var grouped = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var setting in settings)
        {
            if (!TryParseSyncCredentialSetting(setting.Key, out var officeCode, out var suffix))
                continue;

            if (!grouped.TryGetValue(officeCode, out var bucket))
            {
                bucket = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                grouped[officeCode] = bucket;
            }

            bucket[suffix] = setting.Value ?? string.Empty;
        }

        var credentials = new List<StoredSyncCredential>();
        foreach (var (officeCode, bucket) in grouped)
        {
            if (!bucket.TryGetValue(SyncOfficeCredentialUsernameSuffix, out var username) ||
                !bucket.TryGetValue(SyncOfficeCredentialPasswordSuffix, out var protectedPassword))
                continue;

            var password = UnprotectSyncCredential(protectedPassword);
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
                continue;

            bucket.TryGetValue(SyncOfficeCredentialTenantSuffix, out var tenantCode);
            bucket.TryGetValue(SyncOfficeCredentialSavedAtSuffix, out var savedAtRaw);

            var normalizedOfficeCode = NormalizeOfficeCode(officeCode, string.Empty);
            if (string.IsNullOrWhiteSpace(normalizedOfficeCode))
                continue;

            var normalizedTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(tenantCode, normalizedOfficeCode);
            _ = DateTime.TryParse(savedAtRaw, out var savedAtUtc);
            credentials.Add(new StoredSyncCredential(
                normalizedOfficeCode,
                normalizedTenantCode,
                username.Trim(),
                password,
                savedAtUtc.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(savedAtUtc, DateTimeKind.Utc)
                    : savedAtUtc.ToUniversalTime()));
        }

        return credentials
            .OrderByDescending(credential => credential.SavedAtUtc)
            .ThenBy(credential => credential.OfficeCode, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<DirtyOfficeSummary>> GetDirtyOfficeSummariesAsync(CancellationToken ct = default)
    {
        var summary = new Dictionary<(string OfficeCode, string TenantCode), int>();

        void AddCount(string? officeCode, string? tenantCode, int increment = 1)
        {
            var normalizedOfficeCode = NormalizeOfficeCode(officeCode, string.Empty);
            if (string.IsNullOrWhiteSpace(normalizedOfficeCode))
                return;

            var normalizedTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(tenantCode, normalizedOfficeCode);
            var key = (normalizedOfficeCode, normalizedTenantCode);
            summary[key] = summary.TryGetValue(key, out var current) ? current + increment : increment;
        }

        foreach (var item in await _db.Items.IgnoreQueryFilters()
                     .Where(entity => entity.IsDirty)
                     .Select(entity => new { entity.OfficeCode, entity.TenantCode })
                     .ToListAsync(ct))
        {
            AddCount(item.OfficeCode, item.TenantCode);
        }

        foreach (var master in await _db.CustomerMasters.IgnoreQueryFilters()
                     .Where(entity => entity.IsDirty)
                     .Select(entity => new { OfficeCode = entity.OfficeCode, entity.TenantCode })
                     .ToListAsync(ct))
        {
            AddCount(master.OfficeCode, master.TenantCode);
        }

        foreach (var customer in await _db.Customers.IgnoreQueryFilters()
                     .Where(entity => entity.IsDirty)
                     .Select(entity => new { OfficeCode = entity.ResponsibleOfficeCode, entity.TenantCode })
                     .ToListAsync(ct))
        {
            AddCount(customer.OfficeCode, customer.TenantCode);
        }

        foreach (var contract in await _db.CustomerContracts.IgnoreQueryFilters()
                     .Where(entity => entity.IsDirty)
                     .Join(
                         _db.Customers.IgnoreQueryFilters(),
                         contract => contract.CustomerId,
                         customer => customer.Id,
                         (contract, customer) => new { OfficeCode = customer.ResponsibleOfficeCode, customer.TenantCode })
                     .ToListAsync(ct))
        {
            AddCount(contract.OfficeCode, contract.TenantCode);
        }

        foreach (var invoice in await _db.Invoices.IgnoreQueryFilters()
                     .Where(entity => entity.IsDirty)
                     .Select(entity => new { OfficeCode = entity.ResponsibleOfficeCode, entity.TenantCode })
                     .ToListAsync(ct))
        {
            AddCount(invoice.OfficeCode, invoice.TenantCode);
        }

        foreach (var payment in await _db.Payments.IgnoreQueryFilters()
                     .Where(entity => entity.IsDirty)
                     .Join(
                          _db.Invoices.IgnoreQueryFilters(),
                          payment => payment.InvoiceId,
                          invoice => invoice.Id,
                          (payment, invoice) => new { OfficeCode = invoice.ResponsibleOfficeCode, invoice.TenantCode })
                     .ToListAsync(ct))
        {
            AddCount(payment.OfficeCode, payment.TenantCode);
        }

        foreach (var transaction in await _db.Transactions.IgnoreQueryFilters()
                     .Where(entity => entity.IsDirty)
                     .Select(entity => new { OfficeCode = entity.ResponsibleOfficeCode, entity.TenantCode })
                     .ToListAsync(ct))
        {
            AddCount(transaction.OfficeCode, transaction.TenantCode);
        }

        foreach (var attachment in await _db.TransactionAttachments.IgnoreQueryFilters()
                     .Where(entity => entity.IsDirty)
                     .Join(
                          _db.Transactions.IgnoreQueryFilters(),
                          attachment => attachment.TransactionId,
                          transaction => transaction.Id,
                          (attachment, transaction) => new { OfficeCode = transaction.ResponsibleOfficeCode, transaction.TenantCode })
                     .ToListAsync(ct))
        {
            AddCount(attachment.OfficeCode, attachment.TenantCode);
        }

        foreach (var transfer in await _db.InventoryTransfers.IgnoreQueryFilters()
                     .Where(entity => entity.IsDirty)
                     .Select(entity => new { entity.FromWarehouseCode, entity.ToWarehouseCode })
                     .ToListAsync(ct))
        {
            AddCount(ResolveOfficeCodeFromWarehouseCode(transfer.FromWarehouseCode), null);
            AddCount(ResolveOfficeCodeFromWarehouseCode(transfer.ToWarehouseCode), null);
        }

        foreach (var profile in await _db.RentalBillingProfiles.IgnoreQueryFilters()
                     .Where(entity => entity.IsDirty)
                     .Select(entity => new { OfficeCode = entity.ResponsibleOfficeCode, entity.TenantCode })
                     .ToListAsync(ct))
        {
            AddCount(profile.OfficeCode, profile.TenantCode);
        }

        foreach (var asset in await _db.RentalAssets.IgnoreQueryFilters()
                     .Where(entity => entity.IsDirty)
                     .Select(entity => new { OfficeCode = entity.ResponsibleOfficeCode, entity.TenantCode })
                     .ToListAsync(ct))
        {
            AddCount(asset.OfficeCode, asset.TenantCode);
        }

        foreach (var log in await _db.RentalBillingLogs.IgnoreQueryFilters()
                     .Where(entity => entity.IsDirty)
                     .Select(entity => new { OfficeCode = entity.ResponsibleOfficeCode, entity.TenantCode })
                     .ToListAsync(ct))
        {
            AddCount(log.OfficeCode, log.TenantCode);
        }

        foreach (var profile in await _db.CompanyProfiles.IgnoreQueryFilters()
                     .Where(entity => entity.IsDirty)
                     .Select(entity => entity.OfficeCode)
                     .ToListAsync(ct))
        {
            AddCount(profile, null);
        }

        return summary
            .Select(entry => new DirtyOfficeSummary(entry.Key.OfficeCode, entry.Key.TenantCode, entry.Value))
            .OrderByDescending(entry => entry.Count)
            .ThenBy(entry => entry.OfficeCode, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string GetSyncCredentialSettingKey(string officeCode, string suffix)
        => $"{SyncOfficeCredentialPrefix}{officeCode}{suffix}";

    private static bool TryParseSyncCredentialSetting(
        string key,
        out string officeCode,
        out string suffix)
    {
        officeCode = string.Empty;
        suffix = string.Empty;
        if (!key.StartsWith(SyncOfficeCredentialPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var remainder = key[SyncOfficeCredentialPrefix.Length..];
        var separatorIndex = remainder.IndexOf('.');
        if (separatorIndex <= 0)
            return false;

        officeCode = remainder[..separatorIndex].Trim();
        suffix = remainder[separatorIndex..].Trim();
        return !string.IsNullOrWhiteSpace(officeCode) && !string.IsNullOrWhiteSpace(suffix);
    }

    private static string ProtectSyncCredential(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return string.Empty;

        try
        {
            var bytes = Encoding.UTF8.GetBytes(plainText);
            var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string UnprotectSyncCredential(string? protectedText)
    {
        if (string.IsNullOrWhiteSpace(protectedText))
            return string.Empty;

        try
        {
            var protectedBytes = Convert.FromBase64String(protectedText);
            var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ResolveOfficeCodeFromTenant(string? tenantCode)
        => TenantScopeCatalog.GetOfficeCodesForTenant(tenantCode).FirstOrDefault()
           ?? DomainConstants.OfficeUsenet;
}
