using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class TenantSettingsControllerTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public TenantSettingsControllerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
    }

    [Fact]
    public async Task CreateSharingPolicy_AllowsRecreate_WhenMatchingPolicyWasDeleted()
    {
        await using var dbContext = CreateDbContext();
        await SeedTenantScopeDefinitionsAsync(dbContext);
        dbContext.DataSharingPolicies.Add(new DataSharingPolicy
        {
            Id = Guid.NewGuid(),
            SourceTenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Usenet,
            TargetTenantCode = TenantScopeCatalog.UsenetGroup,
            TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
            ShareCustomers = true,
            IsActive = false,
            IsDeleted = true
        });
        await dbContext.SaveChangesAsync();

        var controller = new TenantSettingsController(dbContext);
        var response = await controller.CreateSharingPolicy(new UpsertDataSharingPolicyRequest
        {
            SourceTenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Usenet,
            TargetTenantCode = TenantScopeCatalog.UsenetGroup,
            TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
            ShareCustomers = true,
            IsActive = true
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var created = Assert.IsType<DataSharingPolicyDto>(ok.Value);

        Assert.False(created.IsDeleted);
        Assert.True(created.IsActive);
        Assert.Equal(1, await dbContext.DataSharingPolicies.CountAsync(policy =>
            !policy.IsDeleted &&
            policy.SourceOfficeCode == OfficeCodeCatalog.Usenet &&
            policy.TargetOfficeCode == OfficeCodeCatalog.Yeonsu));
    }

    [Fact]
    public async Task UpdateSharingPolicy_AllowsMoveToSourceTarget_WhenMatchingPolicyWasDeleted()
    {
        await using var dbContext = CreateDbContext();
        await SeedTenantScopeDefinitionsAsync(dbContext);
        var activePolicyId = Guid.NewGuid();
        dbContext.DataSharingPolicies.AddRange(
            new DataSharingPolicy
            {
                Id = activePolicyId,
                SourceTenantCode = TenantScopeCatalog.UsenetGroup,
                SourceOfficeCode = OfficeCodeCatalog.Yeonsu,
                TargetTenantCode = TenantScopeCatalog.UsenetGroup,
                TargetOfficeCode = OfficeCodeCatalog.Usenet,
                ShareCustomers = true,
                IsActive = true,
                IsDeleted = false
            },
            new DataSharingPolicy
            {
                Id = Guid.NewGuid(),
                SourceTenantCode = TenantScopeCatalog.UsenetGroup,
                SourceOfficeCode = OfficeCodeCatalog.Usenet,
                TargetTenantCode = TenantScopeCatalog.UsenetGroup,
                TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
                ShareCustomers = true,
                IsActive = false,
                IsDeleted = true
            });
        await dbContext.SaveChangesAsync();

        var expectedRevision = await dbContext.DataSharingPolicies
            .IgnoreQueryFilters()
            .Where(policy => policy.Id == activePolicyId)
            .Select(policy => policy.Revision)
            .SingleAsync();
        var controller = new TenantSettingsController(dbContext);
        var response = await controller.UpdateSharingPolicy(activePolicyId, new UpsertDataSharingPolicyRequest
        {
            ExpectedRevision = expectedRevision,
            SourceTenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Usenet,
            TargetTenantCode = TenantScopeCatalog.UsenetGroup,
            TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
            ShareCustomers = true,
            IsActive = true
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var updated = Assert.IsType<DataSharingPolicyDto>(ok.Value);

        Assert.Equal(activePolicyId, updated.Id);
        Assert.False(updated.IsDeleted);
        Assert.True(updated.IsActive);
        Assert.Equal(1, await dbContext.DataSharingPolicies.CountAsync(policy =>
            !policy.IsDeleted &&
            policy.SourceOfficeCode == OfficeCodeCatalog.Usenet &&
            policy.TargetOfficeCode == OfficeCodeCatalog.Yeonsu));
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private AppDbContext CreateDbContext()
    {
        var currentUser = new TestCurrentUserContext();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        var dbContext = new AppDbContext(options, currentUser, new RevisionClock());
        dbContext.Database.EnsureCreated();
        return dbContext;
    }

    private static async Task SeedTenantScopeDefinitionsAsync(AppDbContext dbContext)
    {
        dbContext.TenantDefinitions.Add(new TenantDefinition
        {
            TenantCode = TenantScopeCatalog.UsenetGroup,
            DisplayName = "USENET",
            StorageMode = TenantScopeCatalog.StorageSharedDatabase,
            IsActive = true
        });
        dbContext.TenantOfficeDefinitions.AddRange(
            new TenantOfficeDefinition
            {
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                DisplayName = "USENET",
                IsActive = true
            },
            new TenantOfficeDefinition
            {
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Yeonsu,
                DisplayName = "YEONSU",
                IsActive = true
            });
        await dbContext.SaveChangesAsync();
    }

    private sealed class TestCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId { get; init; } = Guid.NewGuid();
        public string Username { get; init; } = "admin";
        public string TenantCode { get; init; } = TenantScopeCatalog.UsenetGroup;
        public string OfficeCode { get; init; } = OfficeCodeCatalog.Usenet;
        public string ScopeType { get; init; } = TenantScopeCatalog.ScopeAdmin;
        public bool IsAdmin { get; init; } = true;
        public bool IsGodMode { get; init; }

        public bool HasPermission(string permission)
            => true;
    }
}
