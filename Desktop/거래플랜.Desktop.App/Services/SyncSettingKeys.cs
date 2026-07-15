using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

internal static class SyncSettingKeys
{
    public const string AdministrativeBusinessCacheRevisionPrefix = "Sync.AdminBusinessCacheRevision.";

    public static string BuildAdministrativeBusinessCacheRevisionKey(string? businessDatabaseName)
        => AdministrativeBusinessCacheRevisionPrefix + TenantScopeCatalog.GetDatabaseName(businessDatabaseName);
}
