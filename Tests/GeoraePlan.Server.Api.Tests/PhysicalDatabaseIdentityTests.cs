using System.Reflection;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class PhysicalDatabaseIdentityTests
{
    [Fact]
    public void PostgreSqlIdentity_NormalizesHostButPreservesDatabaseAndFallbackUsernameCase()
    {
        var lowerDatabase = CreatePostgreSqlConnectionInfo(
            "Host=DB-SERVER;Port=5432;Database=tenant;Username=app;Password=test");
        var equivalentHost = CreatePostgreSqlConnectionInfo(
            "Host=db-server;Port=5432;Database=tenant;Username=other;Password=other");
        var caseDistinctDatabase = CreatePostgreSqlConnectionInfo(
            "Host=db-server;Port=5432;Database=Tenant;Username=app;Password=test");
        var lowerFallbackUsername = CreatePostgreSqlConnectionInfo(
            "Host=db-server;Port=5432;Username=tenant;Password=test");
        var caseDistinctFallbackUsername = CreatePostgreSqlConnectionInfo(
            "Host=DB-SERVER;Port=5432;Username=Tenant;Password=test");

        Assert.Equal(
            PhysicalDatabaseIdentity.FromConnectionInfo(lowerDatabase),
            PhysicalDatabaseIdentity.FromConnectionInfo(equivalentHost));
        Assert.NotEqual(
            PhysicalDatabaseIdentity.FromConnectionInfo(lowerDatabase),
            PhysicalDatabaseIdentity.FromConnectionInfo(caseDistinctDatabase));
        Assert.NotEqual(
            PhysicalDatabaseIdentity.FromConnectionInfo(lowerFallbackUsername),
            PhysicalDatabaseIdentity.FromConnectionInfo(caseDistinctFallbackUsername));
    }

    [Fact]
    public void SqliteIdentity_UsesHostFileSystemPathCaseSemantics()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "GeoraePlan-Identity",
            "CaseSensitive.db");
        var differentlyCasedPath = path.ToUpperInvariant();
        var first = new TenantDatabaseConnectionInfo
        {
            UseSqlite = true,
            ConnectionString = $"Data Source={path}"
        };
        var differentlyCased = new TenantDatabaseConnectionInfo
        {
            UseSqlite = true,
            ConnectionString = $"Data Source={differentlyCasedPath}"
        };

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(
                PhysicalDatabaseIdentity.FromConnectionInfo(first),
                PhysicalDatabaseIdentity.FromConnectionInfo(differentlyCased));
        }
        else
        {
            Assert.NotEqual(
                PhysicalDatabaseIdentity.FromConnectionInfo(first),
                PhysicalDatabaseIdentity.FromConnectionInfo(differentlyCased));
        }
    }

    [Fact]
    public void TenantResolver_AcceptsCaseDistinctRequiredDatabaseButRejectsExactPhysicalDatabase()
    {
        const string central =
            "Host=db-server;Port=5432;Database=tenant;Username=app;Password=test";
        const string caseDistinct =
            "Host=DB-SERVER;Port=5432;Database=Tenant;Username=app;Password=test";
        var resolver = CreateRequiredResolver(central, caseDistinct);

        var resolved = resolver.ResolveBusinessTenant(TenantScopeCatalog.Itworld);

        Assert.True(resolved.IsDedicatedBusinessDatabase);
        Assert.Equal(caseDistinct, resolved.ConnectionString);
        Assert.Single(resolver.GetDedicatedBusinessConnections());

        var sameDatabaseResolver = CreateRequiredResolver(
            central,
            "Host=DB-SERVER;Port=5432;Database=tenant;Username=other;Password=other");
        Assert.Throws<InvalidOperationException>(
            () => sameDatabaseResolver.ResolveBusinessTenant(TenantScopeCatalog.Itworld));
        Assert.Throws<InvalidOperationException>(
            () => sameDatabaseResolver.GetDedicatedBusinessConnections());
    }

    [Fact]
    public void DedicatedConfiguration_KeepsCaseDistinctDatabaseButOmitsExactPhysicalDatabase()
    {
        const string central =
            "Host=db-server;Port=5432;Database=tenant;Username=app;Password=test";
        var caseDistinctConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [$"ConnectionStrings:{TenantScopeCatalog.Itworld}"] =
                        "Host=DB-SERVER;Port=5432;Database=Tenant;Username=app;Password=test"
                })
            .Build();

        var caseDistinct =
            DedicatedBusinessConnectionConfiguration.Resolve(
                caseDistinctConfiguration,
                central);

        Assert.True(caseDistinct.ContainsKey(TenantScopeCatalog.Itworld));

        var exactConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [$"ConnectionStrings:{TenantScopeCatalog.Itworld}"] =
                        "Host=DB-SERVER;Port=5432;Database=tenant;Username=other;Password=other"
                })
            .Build();

        var exact = DedicatedBusinessConnectionConfiguration.Resolve(
            exactConfiguration,
            central);

        Assert.False(exact.ContainsKey(TenantScopeCatalog.Itworld));
    }

    [Fact]
    public void StoredFileReconciler_DeduplicatesExactDatabaseButKeepsCaseDistinctDatabase()
    {
        var central = CreatePostgreSqlConnectionInfo(
            "Host=db-server;Port=5432;Database=tenant;Username=app;Password=test");
        var caseDistinct = CreatePostgreSqlConnectionInfo(
            "Host=DB-SERVER;Port=5432;Database=Tenant;Username=app;Password=test");
        var exactDatabase = CreatePostgreSqlConnectionInfo(
            "Host=DB-SERVER;Port=5432;Database=tenant;Username=other;Password=other");

        Assert.Equal(
            2,
            GetDistinctConnections(
                new TestConnectionResolver(central, [caseDistinct])).Count);
        Assert.Single(
            GetDistinctConnections(
                new TestConnectionResolver(central, [exactDatabase])));
    }

    [Fact]
    public void StorageNamespace_DiffersForCaseDistinctPostgreSqlDatabase()
    {
        var currentUser = new TestCurrentUserContext();
        var revisionClock = new RevisionClock();
        using var lowerContext = CreateContext(
            "Host=db-server;Port=5432;Database=tenant;Username=app;Password=test",
            currentUser,
            revisionClock);
        using var upperContext = CreateContext(
            "Host=DB-SERVER;Port=5432;Database=Tenant;Username=app;Password=test",
            currentUser,
            revisionClock);

        Assert.NotEqual(
            PhysicalDatabaseIdentity.GetStorageNamespace(lowerContext),
            PhysicalDatabaseIdentity.GetStorageNamespace(upperContext));
    }

    private static TenantDatabaseConnectionResolver CreateRequiredResolver(
        string central,
        string dedicated)
        => new(
            new TenantDatabaseRoutingOptions
            {
                UseSqlite = false,
                DefaultConnectionString = central,
                DedicatedBusinessConnections =
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [TenantScopeCatalog.Itworld] = dedicated
                    },
                RequiredDedicatedTenantCodes = [TenantScopeCatalog.Itworld]
            },
            new HttpContextAccessor());

    private static IReadOnlyList<TenantDatabaseConnectionInfo> GetDistinctConnections(
        ITenantDatabaseConnectionResolver connectionResolver)
    {
        var reconciler = new StoredFileReferenceReconciler(
            null!,
            null!,
            connectionResolver,
            new RevisionClock());
        var method = typeof(StoredFileReferenceReconciler).GetMethod(
            "GetDistinctPhysicalConnections",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsAssignableFrom<IReadOnlyList<TenantDatabaseConnectionInfo>>(
            method!.Invoke(reconciler, null));
    }

    private static AppDbContext CreateContext(
        string connectionString,
        ICurrentUserContext currentUser,
        RevisionClock revisionClock)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new AppDbContext(options, currentUser, revisionClock);
    }

    private static TenantDatabaseConnectionInfo CreatePostgreSqlConnectionInfo(
        string connectionString)
        => new()
        {
            UseSqlite = false,
            ConnectionString = connectionString,
            TenantCode = TenantScopeCatalog.UsenetGroup
        };

    private sealed class TestConnectionResolver(
        TenantDatabaseConnectionInfo central,
        IReadOnlyList<TenantDatabaseConnectionInfo> dedicated)
        : ITenantDatabaseConnectionResolver
    {
        public TenantDatabaseConnectionInfo ResolveCurrent() => central;
        public TenantDatabaseConnectionInfo ResolveCentral() => central;
        public TenantDatabaseConnectionInfo ResolveBusinessTenant(string? tenantCode)
            => dedicated.FirstOrDefault() ?? central;
        public IReadOnlyList<TenantDatabaseConnectionInfo>
            GetDedicatedBusinessConnections()
            => dedicated;
    }

    private sealed class TestCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId { get; } = Guid.NewGuid();
        public string Username => "physical-database-identity-test";
        public string TenantCode => TenantScopeCatalog.UsenetGroup;
        public string OfficeCode => OfficeCodeCatalog.Usenet;
        public string ScopeType => TenantScopeCatalog.ScopeAdmin;
        public bool IsAdmin => true;
        public bool IsGodMode => false;
        public IReadOnlyCollection<string> Permissions => [];
        public bool HasPermission(string permission) => true;
    }
}
