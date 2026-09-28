using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;

namespace 거래플랜.Desktop.App.Services;

public sealed partial class LocalStateService
{
    private static readonly string[] CommonAuthenticationExactKeys =
    [
        "Login.RememberUsername", "Login.RememberPassword",
        "Login.SavedUsername", "Login.SavedPasswordProtected"
    ];

    private static readonly string[] CommonAuthenticationPrefixes =
    [
        "CachedSession.", "CachedSession_", SyncOfficeCredentialPrefix
    ];

    private static bool IsCommonAuthenticationSetting(string key)
        => CommonAuthenticationExactKeys.Contains(key, StringComparer.Ordinal)
            || CommonAuthenticationPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal));

    private static bool IsReservedStorageSetting(string key)
        => key is BusinessCacheDatabase.OwnerSettingKey or BusinessCacheDatabase.FormatSettingKey
            or CommonAuthenticationDatabase.FormatSettingKey;

    private static void EnsureOrdinarySettings(IEnumerable<string> keys)
    {
        if (keys.Any(IsReservedStorageSetting))
            throw new InvalidOperationException("저장소 소유 정보와 형식은 일반 설정 작업으로 변경할 수 없습니다.");
    }

    private static bool IsCommonAuthenticationDeletionPrefix(string prefix)
    {
        if (CommonAuthenticationPrefixes.Any(root => prefix.StartsWith(root, StringComparison.Ordinal)))
            return true;
        if (CommonAuthenticationPrefixes.Any(root => root.StartsWith(prefix, StringComparison.Ordinal)) ||
            CommonAuthenticationExactKeys.Any(key => key.StartsWith(prefix, StringComparison.Ordinal)))
            throw new InvalidOperationException("공통 인증과 업무 설정을 함께 삭제할 수 없습니다. 정확한 설정 키 또는 저장소별 접두어를 지정하세요.");
        return false;
    }

    private async Task<int> DeleteSettingsIndependentAsync(string key, bool byPrefix, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var common = _commonAuthenticationDatabase is not null &&
            (byPrefix ? IsCommonAuthenticationDeletionPrefix(key) : IsCommonAuthenticationSetting(key));
        await using var db = common
            ? await CreateIndependentAuthenticationDbAsync(ct)
            : CreateSettingsDeletionBusinessDb();
        await using var transaction = await db.BeginRuntimeMutationTransactionAsync(ct);
        var candidates = byPrefix
            ? await db.Settings.Where(row => row.Key.StartsWith(key)).ToListAsync(ct)
            : await db.Settings.Where(row => row.Key == key).ToListAsync(ct);
        // SQLite LIKE is case insensitive by default. Match .NET key semantics
        // after the provider's escaped prefix lookup; '%' and '_' stay literal.
        var rows = candidates.Where(row => byPrefix
            ? row.Key.StartsWith(key, StringComparison.Ordinal)
            : string.Equals(row.Key, key, StringComparison.Ordinal)).ToList();
        EnsureOrdinarySettings(rows.Select(row => row.Key));
        if (rows.Count > 0)
        {
            db.Settings.RemoveRange(rows);
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
        if (!common)
            DetachBusinessSettings(rows.Select(row => row.Key));
        return rows.Count;
    }

    private LocalDbContext CreateSettingsDeletionBusinessDb()
    {
        if (!RequiresOwningSqliteConnection(GetSyncMetadataConnectionStringBuilder()))
            return CreateIndependentBusinessSettingsDb();
        // Private in-memory databases cannot be reopened by connection string.
        // Borrow only the connection, never the UI change tracker or transaction.
        ThrowIfOwningConnectionTransactionActive();
        return new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(_db.Database.GetDbConnection()).Options);
    }
}
