using System.Reflection;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class StartupTrackedStubRegressionTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _dbContext;

    public StartupTrackedStubRegressionTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var currentUser = new TestCurrentUserContext
        {
            Username = "admin",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin,
            IsAdmin = true
        };
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _dbContext = new AppDbContext(options, currentUser, new RevisionClock());
        _dbContext.Database.EnsureCreated();
    }

    [Fact]
    public async Task CustomerScopeBackfill_FollowedByRentalLinkageRepair_PreservesValidProfileCustomerLink()
    {
        var customerId = Guid.Parse("a1111111-1111-4111-8111-111111111111");
        var profileId = Guid.Parse("b2222222-2222-4222-8222-222222222222");
        const string customerName = "Startup linkage customer";

        _dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = string.Empty,
            OfficeCode = string.Empty,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = customerName,
            NameMatchKey = "STARTUPLINKAGECUSTOMER",
            IsDeleted = false
        });
        _dbContext.RentalBillingProfiles.Add(new RentalBillingProfile
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            ManagementCompanyCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ProfileKey = "STARTUP-TRACKED-STUB",
            CustomerId = customerId,
            CustomerName = customerName,
            BillingTemplateJson = "[]",
            IsActive = true,
            IsDeleted = false
        });
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        await InvokePrivateTaskAsync(
            "BackfillCustomerScopeFieldsAsync",
            _dbContext,
            CancellationToken.None);
        await InvokePrivateTaskAsync(
            "RepairRentalCustomerLinkageAsync",
            _dbContext,
            CancellationToken.None);

        var profile = await _dbContext.RentalBillingProfiles
            .IgnoreQueryFilters()
            .SingleAsync(entity => entity.Id == profileId);

        Assert.Equal(customerId, profile.CustomerId);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }

    private static async Task InvokePrivateTaskAsync(string methodName, params object?[] arguments)
    {
        var method = typeof(DbInitializer).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(method!.Invoke(null, arguments));
        await task;
    }

    private sealed class TestCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId { get; init; }
        public string Username { get; init; } = string.Empty;
        public string TenantCode { get; init; } = TenantScopeCatalog.UsenetGroup;
        public string OfficeCode { get; init; } = OfficeCodeCatalog.Usenet;
        public string ScopeType { get; init; } = TenantScopeCatalog.ScopeAdmin;
        public bool IsAdmin { get; init; }
        public bool IsGodMode { get; init; }
        public bool HasPermission(string permission) => IsAdmin;
    }
}
