using System.Security.Claims;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Http;

namespace 거래플랜.Server.Api.Services;

public sealed class TenantDatabaseRoutingOptions
{
    public bool UseSqlite { get; init; }
    public string SqliteDbPath { get; init; } = string.Empty;
    public string DefaultConnectionString { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> DedicatedBusinessConnections { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyCollection<string> RequiredDedicatedTenantCodes { get; init; } = [];
}

public sealed class TenantDatabaseConnectionResolver : ITenantDatabaseConnectionResolver
{
    private static readonly string[] ControlPlanePrefixes =
    [
        "/auth",
        "/users",
        "/tenant-settings"
    ];

    private readonly TenantDatabaseRoutingOptions _routingOptions;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public TenantDatabaseConnectionResolver(
        TenantDatabaseRoutingOptions routingOptions,
        IHttpContextAccessor httpContextAccessor)
    {
        _routingOptions = routingOptions;
        _httpContextAccessor = httpContextAccessor;
    }

    public TenantDatabaseConnectionInfo ResolveCurrent()
    {
        if (_routingOptions.UseSqlite)
            return ResolveSqlite();

        var httpContext = _httpContextAccessor.HttpContext;
        var path = httpContext?.Request.Path.Value;
        if (IsControlPlanePath(path))
            return ResolveCentral();

        var tenantCode = ResolveRequestedTenantCode(httpContext);
        return ResolveBusinessTenant(tenantCode);
    }

    public TenantDatabaseConnectionInfo ResolveCentral()
    {
        if (_routingOptions.UseSqlite)
            return ResolveSqlite();

        return new TenantDatabaseConnectionInfo
        {
            UseSqlite = false,
            ConnectionString = _routingOptions.DefaultConnectionString,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            IsControlPlane = true,
            IsDedicatedBusinessDatabase = false
        };
    }

    public TenantDatabaseConnectionInfo ResolveBusinessTenant(string? tenantCode)
    {
        if (_routingOptions.UseSqlite)
            return ResolveSqlite();

        var normalizedTenantCode = TenantScopeCatalog.NormalizeTenantCodeOrDefault(tenantCode);
        if (_routingOptions.DedicatedBusinessConnections.TryGetValue(normalizedTenantCode, out var dedicatedConnectionString) &&
            !string.IsNullOrWhiteSpace(dedicatedConnectionString) &&
            !UsesCentralPhysicalDatabase(dedicatedConnectionString))
        {
            return new TenantDatabaseConnectionInfo
            {
                UseSqlite = false,
                ConnectionString = dedicatedConnectionString,
                TenantCode = normalizedTenantCode,
                IsControlPlane = false,
                IsDedicatedBusinessDatabase = true
            };
        }

        if (_routingOptions.RequiredDedicatedTenantCodes.Any(
                requiredTenantCode => string.Equals(
                    TenantScopeCatalog.NormalizeTenantCodeOrDefault(requiredTenantCode),
                    normalizedTenantCode,
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Tenant '{normalizedTenantCode}' requires a dedicated business database connection.");
        }

        return new TenantDatabaseConnectionInfo
        {
            UseSqlite = false,
            ConnectionString = _routingOptions.DefaultConnectionString,
            TenantCode = normalizedTenantCode,
            IsControlPlane = false,
            IsDedicatedBusinessDatabase = false
        };
    }

    public IReadOnlyList<TenantDatabaseConnectionInfo> GetDedicatedBusinessConnections()
    {
        if (_routingOptions.UseSqlite)
            return [];

        var connections = new List<TenantDatabaseConnectionInfo>();
        foreach (var pair in _routingOptions.DedicatedBusinessConnections)
        {
            if (string.IsNullOrWhiteSpace(pair.Value))
            {
                throw new InvalidOperationException(
                    $"Dedicated business database connection is empty for tenant '{pair.Key}'.");
            }

            if (UsesCentralPhysicalDatabase(pair.Value))
            {
                continue;
            }

            connections.Add(new TenantDatabaseConnectionInfo
            {
                UseSqlite = false,
                ConnectionString = pair.Value,
                TenantCode = TenantScopeCatalog.NormalizeTenantCodeOrDefault(pair.Key),
                IsControlPlane = false,
                IsDedicatedBusinessDatabase = true
            });
        }

        foreach (var requiredTenantCode in _routingOptions.RequiredDedicatedTenantCodes)
        {
            var normalizedRequiredTenantCode =
                TenantScopeCatalog.NormalizeTenantCodeOrDefault(requiredTenantCode);
            if (!connections.Any(connection => string.Equals(
                    connection.TenantCode,
                    normalizedRequiredTenantCode,
                    StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"Tenant '{normalizedRequiredTenantCode}' requires a dedicated business database connection.");
            }
        }

        return connections;
    }

    private bool UsesCentralPhysicalDatabase(string candidateConnectionString)
    {
        if (string.IsNullOrWhiteSpace(_routingOptions.DefaultConnectionString))
            throw new InvalidOperationException("Central database connection is empty.");

        var centralIdentity = PhysicalDatabaseIdentity.FromConnectionInfo(new TenantDatabaseConnectionInfo
        {
            UseSqlite = false,
            ConnectionString = _routingOptions.DefaultConnectionString
        });
        var candidateIdentity = PhysicalDatabaseIdentity.FromConnectionInfo(new TenantDatabaseConnectionInfo
        {
            UseSqlite = false,
            ConnectionString = candidateConnectionString
        });
        return string.Equals(centralIdentity, candidateIdentity, StringComparison.Ordinal);
    }

    private TenantDatabaseConnectionInfo ResolveSqlite()
        => new()
        {
            UseSqlite = true,
            ConnectionString = $"Data Source={_routingOptions.SqliteDbPath}",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            IsControlPlane = false,
            IsDedicatedBusinessDatabase = false
        };

    private static bool IsControlPlanePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return true;

        return ControlPlanePrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveRequestedTenantCode(HttpContext? httpContext)
    {
        if (httpContext is null)
            return TenantScopeCatalog.UsenetGroup;

        if (CanUseRequestedTenantHeader(httpContext.User))
        {
            var requestedTenant = httpContext.Request.Query["tenantCode"].FirstOrDefault()
                                  ?? httpContext.Request.Headers["X-Tenant-Code"].FirstOrDefault();
            if (TenantScopeCatalog.TryNormalizeTenantCode(requestedTenant, out var adminTenantCode))
                return adminTenantCode;
        }

        var tenantClaim = httpContext.User.FindFirstValue("tenant");
        var officeClaim = httpContext.User.FindFirstValue("office");
        return TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(tenantClaim, officeClaim);
    }

    private static bool CanUseRequestedTenantHeader(ClaimsPrincipal user)
    {
        if (user.Claims.Any(claim => claim.Type == "god" && string.Equals(claim.Value, "true", StringComparison.OrdinalIgnoreCase)))
            return true;

        var scopeClaim = user.FindFirstValue("scope");
        return user.IsInRole("Admin") &&
               string.Equals(
                   TenantScopeCatalog.NormalizeScopeTypeOrDefault(scopeClaim),
                   TenantScopeCatalog.ScopeAdmin,
                   StringComparison.OrdinalIgnoreCase);
    }
}
