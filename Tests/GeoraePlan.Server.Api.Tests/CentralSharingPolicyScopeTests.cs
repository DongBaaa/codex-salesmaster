using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class CentralSharingPolicyScopeTests : IDisposable
{
    private readonly SqliteConnection _central = new($"Data Source=central-policy-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
    private readonly SqliteConnection _business = new($"Data Source=business-policy-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
    private readonly CurrentUser _user = new();

    public CentralSharingPolicyScopeTests()
    {
        _central.Open();
        _business.Open();
    }

    [Theory]
    [InlineData("Customers")]
    [InlineData("Items")]
    [InlineData("Invoices")]
    [InlineData("Payments")]
    [InlineData("Contracts")]
    [InlineData("Reports")]
    [InlineData("Rentals")]
    [InlineData("Deliveries")]
    public void RevokedCentralPolicy_DeniesStaleBusinessGrant(string area)
    {
        Seed(centralActive: false, businessActive: true);
        using var business = Open(_business);
        var scope = CreateScope(business);
        Assert.False(Read(scope, area));
        Assert.False(scope.CanWriteOfficeForCustomers("YEONSU", "USENET_GROUP"));
        Assert.True(scope.CanReadOfficeForCustomers("USENET", "USENET_GROUP"));
        Assert.True(business.DataSharingPolicies.Single().IsActive);
        Assert.False(business.ChangeTracker.HasChanges());
    }

    [Fact]
    public void NewCentralGrant_AppliesWithoutUpdatingBusinessCopy_AndPreservesReadOnly()
    {
        Seed(centralActive: true, businessActive: false);
        using (var central = Open(_central))
        {
            central.DataSharingPolicies.Single().AllowTargetWrite = false;
            central.SaveChanges();
        }
        using var business = Open(_business);
        var scope = CreateScope(business);
        Assert.True(scope.CanReadOfficeForCustomers("YEONSU", "USENET_GROUP"));
        Assert.False(scope.CanWriteOfficeForCustomers("YEONSU", "USENET_GROUP"));
        Assert.False(business.DataSharingPolicies.Single().IsActive);
    }

    [Fact]
    public void NextScopeReadsCommittedRevocation_InsteadOfReusingPriorRequestGrant()
    {
        Seed(centralActive: true, businessActive: true);
        using var business = Open(_business);
        Assert.True(CreateScope(business).CanWriteOfficeForCustomers("YEONSU", "USENET_GROUP"));
        using (var central = Open(_central))
        {
            var policy = central.DataSharingPolicies.Single();
            policy.IsDeleted = true;
            policy.IsActive = false;
            central.SaveChanges();
        }
        Assert.False(CreateScope(business).CanReadOfficeForCustomers("YEONSU", "USENET_GROUP"));
        Assert.False(CreateScope(business).CanWriteOfficeForCustomers("YEONSU", "USENET_GROUP"));
        Assert.True(business.DataSharingPolicies.Single().IsActive);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("source-office")]
    [InlineData("target-office")]
    public void CentralDefinitionDeactivation_DeniesStaleBusinessGrant(string definition)
    {
        Seed(centralActive: true, businessActive: true);
        using (var central = Open(_central))
        {
            if (definition == "tenant")
                central.TenantDefinitions.Single().IsActive = false;
            else
                central.TenantOfficeDefinitions.Single(x => x.OfficeCode == (definition == "source-office" ? "YEONSU" : "USENET")).IsActive = false;
            central.SaveChanges();
        }
        using var business = Open(_business);
        Assert.False(CreateScope(business).CanReadOfficeForInvoices("YEONSU", "USENET_GROUP"));
    }

    [Fact]
    public void CentralReadFailure_DoesNotAuthorizeFromBusinessCopy()
    {
        Seed(centralActive: false, businessActive: true);
        using var business = Open(_business);
        var resolver = new Resolver($"Data Source=missing-schema-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        var scope = new OfficeScopeService(_user, business, resolver);
        Assert.Throws<SqliteException>(() => scope.CanReadOfficeForCustomers("YEONSU", "USENET_GROUP"));
        Assert.True(business.DataSharingPolicies.Single().IsActive);
    }

    [Fact]
    public void DependencyInjectionUsesCentralResolver_WhenBusinessCopyStillGrants()
    {
        Seed(centralActive: false, businessActive: true);
        using var business = Open(_business);
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentUserContext>(_user);
        services.AddSingleton(business);
        services.AddSingleton<ITenantDatabaseConnectionResolver>(new Resolver(_central.ConnectionString));
        services.AddScoped<OfficeScopeService>();
        using var provider = services.BuildServiceProvider();
        using var requestScope = provider.CreateScope();
        Assert.False(requestScope.ServiceProvider.GetRequiredService<OfficeScopeService>().CanReadOfficeForCustomers("YEONSU", "USENET_GROUP"));
    }

    private OfficeScopeService CreateScope(AppDbContext business)
        => new(_user, business, new Resolver(_central.ConnectionString));

    private AppDbContext Open(SqliteConnection connection)
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options, _user, new RevisionClock());
        db.Database.EnsureCreated();
        return db;
    }

    private void Seed(bool centralActive, bool businessActive)
    {
        foreach (var (connection, active) in new[] { (_central, centralActive), (_business, businessActive) })
        {
            using var db = Open(connection);
            db.TenantDefinitions.Add(new TenantDefinition { TenantCode = "USENET_GROUP", DisplayName = "Group", IsActive = true });
            foreach (var office in new[] { "USENET", "YEONSU" })
                db.TenantOfficeDefinitions.Add(new TenantOfficeDefinition { TenantCode = "USENET_GROUP", OfficeCode = office, DisplayName = office, IsActive = true });
            db.DataSharingPolicies.Add(new DataSharingPolicy
            {
                SourceTenantCode = "USENET_GROUP", SourceOfficeCode = "YEONSU",
                TargetTenantCode = "USENET_GROUP", TargetOfficeCode = "USENET",
                IsActive = active, ShareCustomers = true, ShareItems = true, ShareInvoices = true,
                SharePayments = true, ShareContracts = true, ShareReports = true, ShareRentals = true,
                ShareDeliveries = true, AllowTargetWrite = true
            });
            db.SaveChanges();
        }
    }

    private static bool Read(OfficeScopeService scope, string area) => area switch
    {
        "Customers" => scope.CanReadOfficeForCustomers("YEONSU", "USENET_GROUP"),
        "Items" => scope.CanReadOfficeForItems("YEONSU", "USENET_GROUP"),
        "Invoices" => scope.CanReadOfficeForInvoices("YEONSU", "USENET_GROUP"),
        "Payments" => scope.CanReadOfficeForPayments("YEONSU", "USENET_GROUP"),
        "Contracts" => scope.CanReadOfficeForContracts("YEONSU", "USENET_GROUP"),
        "Reports" => scope.CanReadOfficeForReports("YEONSU", "USENET_GROUP"),
        "Rentals" => scope.CanReadOfficeForRentals("YEONSU", "USENET_GROUP"),
        "Deliveries" => scope.CanReadOfficeForDeliveries("YEONSU", "USENET_GROUP"),
        _ => throw new ArgumentOutOfRangeException(nameof(area))
    };

    private sealed class Resolver(string connectionString) : ITenantDatabaseConnectionResolver
    {
        public TenantDatabaseConnectionInfo ResolveCentral() => new() { UseSqlite = true, ConnectionString = connectionString, IsControlPlane = true };
        public TenantDatabaseConnectionInfo ResolveCurrent() => throw new InvalidOperationException("Business routing must not select the policy source.");
        public TenantDatabaseConnectionInfo ResolveBusinessTenant(string? tenantCode) => throw new InvalidOperationException();
        public IReadOnlyList<TenantDatabaseConnectionInfo> GetDedicatedBusinessConnections() => [];
    }

    private sealed class CurrentUser : ICurrentUserContext
    {
        public Guid? UserId { get; } = Guid.NewGuid();
        public string Username => "scope-user";
        public string TenantCode => "USENET_GROUP";
        public string OfficeCode => "USENET";
        public string ScopeType => "OfficeOnly";
        public bool IsAdmin => false;
        public bool IsGodMode => false;
        public bool HasPermission(string permission) => false;
    }

    public void Dispose()
    {
        _central.Dispose();
        _business.Dispose();
    }
}
