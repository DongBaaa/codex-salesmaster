using 거래플랜.Shared.Contracts;

namespace 거래플랜.Server.Api.Services;

public sealed class TenantDatabaseConnectionInfo
{
    public bool UseSqlite { get; init; }
    public string ConnectionString { get; init; } = string.Empty;
    public string TenantCode { get; init; } = TenantScopeCatalog.UsenetGroup;
    public bool IsControlPlane { get; init; }
    public bool IsDedicatedBusinessDatabase { get; init; }
}
