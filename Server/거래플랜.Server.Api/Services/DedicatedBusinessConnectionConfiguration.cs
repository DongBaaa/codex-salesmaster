using 거래플랜.Shared.Contracts;

namespace 거래플랜.Server.Api.Services;

public static class DedicatedBusinessConnectionConfiguration
{
    public static IReadOnlyDictionary<string, string> Resolve(
        IConfiguration configuration,
        string defaultConnectionString)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var result = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        var connectionSection = configuration.GetSection("ConnectionStrings");
        var hasDefaultConnection =
            !string.IsNullOrWhiteSpace(defaultConnectionString);
        var defaultIdentity = hasDefaultConnection
            ? PhysicalDatabaseIdentity.FromConnectionInfo(
                CreateConnectionInfo(defaultConnectionString))
            : null;

        foreach (var tenantCode in TenantScopeCatalog.AllTenants)
        {
            var candidate =
                (connectionSection[tenantCode] ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            if (defaultIdentity is not null)
            {
                var candidateIdentity =
                    PhysicalDatabaseIdentity.FromConnectionInfo(
                        CreateConnectionInfo(candidate));
                if (string.Equals(
                        defaultIdentity,
                        candidateIdentity,
                        StringComparison.Ordinal))
                {
                    continue;
                }
            }

            result[tenantCode] = candidate;
        }

        return result;
    }

    private static TenantDatabaseConnectionInfo CreateConnectionInfo(
        string connectionString)
        => new()
        {
            UseSqlite = false,
            ConnectionString = connectionString
        };
}
