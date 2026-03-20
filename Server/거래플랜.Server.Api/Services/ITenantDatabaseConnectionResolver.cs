namespace 거래플랜.Server.Api.Services;

public interface ITenantDatabaseConnectionResolver
{
    TenantDatabaseConnectionInfo ResolveCurrent();
    TenantDatabaseConnectionInfo ResolveCentral();
    TenantDatabaseConnectionInfo ResolveBusinessTenant(string? tenantCode);
    IReadOnlyList<TenantDatabaseConnectionInfo> GetDedicatedBusinessConnections();
}
