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
        var deletedPolicyId = await dbContext.DataSharingPolicies
            .IgnoreQueryFilters()
            .Select(policy => policy.Id)
            .SingleAsync();
        await InstallFullRouteUniqueIndexAsync(dbContext);

        var controller = CreateController(dbContext);
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
        Assert.Equal(deletedPolicyId, created.Id);
        Assert.Equal(1, await dbContext.DataSharingPolicies.CountAsync(policy =>
            !policy.IsDeleted &&
            policy.SourceOfficeCode == OfficeCodeCatalog.Usenet &&
            policy.TargetOfficeCode == OfficeCodeCatalog.Yeonsu));
        Assert.Equal(1, await dbContext.DataSharingPolicies.IgnoreQueryFilters().CountAsync());
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
        await InstallFullRouteUniqueIndexAsync(dbContext);

        var expectedRevision = await dbContext.DataSharingPolicies
            .IgnoreQueryFilters()
            .Where(policy => policy.Id == activePolicyId)
            .Select(policy => policy.Revision)
            .SingleAsync();
        var controller = CreateController(dbContext);
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
        Assert.Equal(1, await dbContext.DataSharingPolicies.IgnoreQueryFilters().CountAsync(policy =>
            policy.SourceOfficeCode == OfficeCodeCatalog.Usenet &&
            policy.TargetOfficeCode == OfficeCodeCatalog.Yeonsu));
    }

    [Fact]
    public async Task TenantSettingsController_ForTenantAdmin_ReturnsForbid()
    {
        var currentUser = new TestCurrentUserContext
        {
            ScopeType = TenantScopeCatalog.ScopeTenantAll,
            IsAdmin = true
        };
        await using var dbContext = CreateDbContext(currentUser);
        var controller = CreateController(dbContext, currentUser);

        var getResponse = await controller.Get(CancellationToken.None);
        Assert.IsType<ForbidResult>(getResponse.Result);

        var createResponse = await controller.CreateSharingPolicy(new UpsertDataSharingPolicyRequest
        {
            SourceTenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Yeonsu,
            TargetTenantCode = TenantScopeCatalog.UsenetGroup,
            TargetOfficeCode = OfficeCodeCatalog.Usenet,
            ShareCustomers = true,
            IsActive = true
        }, CancellationToken.None);
        Assert.IsType<ForbidResult>(createResponse.Result);
    }

    [Fact]
    public async Task Get_IncludeInactive_ReturnsHiddenDefinitionsOnlyForSystemConfigurationAdmin()
    {
        await using var dbContext = CreateDbContext();
        await SeedTenantScopeDefinitionsAsync(dbContext);
        var tenant = await dbContext.TenantDefinitions.SingleAsync();
        var office = await dbContext.TenantOfficeDefinitions
            .SingleAsync(current => current.OfficeCode == OfficeCodeCatalog.Yeonsu);
        tenant.IsActive = false;
        tenant.IsDeleted = true;
        office.IsActive = false;
        office.IsDeleted = true;
        var policy = new DataSharingPolicy
        {
            SourceTenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Usenet,
            TargetTenantCode = TenantScopeCatalog.UsenetGroup,
            TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
            IsActive = false,
            IsDeleted = true
        };
        dbContext.DataSharingPolicies.Add(policy);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var controller = CreateController(dbContext);
        var activeOnlyResponse = await controller.Get(CancellationToken.None);
        var activeOnly = Assert.IsType<TenantConfigurationSnapshotDto>(
            Assert.IsType<OkObjectResult>(activeOnlyResponse.Result).Value);
        Assert.Empty(activeOnly.Tenants);
        Assert.DoesNotContain(activeOnly.Offices, current => current.Id == office.Id);
        Assert.Empty(activeOnly.SharingPolicies);

        var allResponse = await controller.Get(CancellationToken.None, includeInactive: true);
        var all = Assert.IsType<TenantConfigurationSnapshotDto>(
            Assert.IsType<OkObjectResult>(allResponse.Result).Value);
        Assert.Contains(all.Tenants, current => current.Id == tenant.Id && current.IsDeleted);
        Assert.Contains(all.Offices, current => current.Id == office.Id && current.IsDeleted);
        Assert.Contains(all.SharingPolicies, current => current.Id == policy.Id && current.IsDeleted);
    }

    [Fact]
    public async Task Get_IncludeInactive_DoesNotBroadenTenantAdminAccess()
    {
        var currentUser = new TestCurrentUserContext
        {
            ScopeType = TenantScopeCatalog.ScopeTenantAll,
            IsAdmin = true
        };
        await using var dbContext = CreateDbContext(currentUser);
        var controller = CreateController(dbContext, currentUser);

        var response = await controller.Get(CancellationToken.None, includeInactive: true);

        Assert.IsType<ForbidResult>(response.Result);
    }

    [Fact]
    public async Task DeleteSharingPolicy_ReturnsCanonicalInactiveRevisionForClientValidation()
    {
        await using var dbContext = CreateDbContext();
        await SeedTenantScopeDefinitionsAsync(dbContext);
        var policy = new DataSharingPolicy
        {
            SourceTenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Usenet,
            TargetTenantCode = TenantScopeCatalog.UsenetGroup,
            TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
            IsActive = true
        };
        dbContext.DataSharingPolicies.Add(policy);
        await dbContext.SaveChangesAsync();
        var originalRevision = policy.Revision;

        var response = await CreateController(dbContext).DeleteSharingPolicy(
            policy.Id,
            originalRevision,
            CancellationToken.None);

        var deleted = Assert.IsType<DataSharingPolicyDto>(
            Assert.IsType<OkObjectResult>(response).Value);
        Assert.Equal(policy.Id, deleted.Id);
        Assert.False(deleted.IsActive);
        Assert.True(deleted.IsDeleted);
        Assert.True(deleted.Revision > originalRevision);
    }

    [Fact]
    public async Task UpdateTenant_RejectsStorageModeChangeThatRequiresControlledDataMigration()
    {
        await using var dbContext = CreateDbContext();
        await SeedTenantScopeDefinitionsAsync(dbContext);
        var tenant = await dbContext.TenantDefinitions
            .SingleAsync(current => current.TenantCode == TenantScopeCatalog.UsenetGroup);
        var originalRevision = tenant.Revision;

        var controller = CreateController(dbContext);
        var response = await controller.UpdateTenant(
            tenant.TenantCode,
            new UpdateTenantDefinitionRequest
            {
                ExpectedRevision = originalRevision,
                DisplayName = tenant.DisplayName,
                StorageMode = TenantScopeCatalog.StorageDedicatedDatabase,
                Description = tenant.Description,
                IsActive = tenant.IsActive
            },
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Contains("데이터 이관", Assert.IsType<string>(badRequest.Value));
        dbContext.ChangeTracker.Clear();
        var unchanged = await dbContext.TenantDefinitions
            .SingleAsync(current => current.TenantCode == TenantScopeCatalog.UsenetGroup);
        Assert.Equal(TenantScopeCatalog.StorageSharedDatabase, unchanged.StorageMode);
        Assert.Equal(originalRevision, unchanged.Revision);
    }

    [Fact]
    public async Task AcceptedNoOpUpdatesAndDelete_AdvanceEntityAndCommittedRevision()
    {
        await using var dbContext = CreateDbContext();
        await SeedTenantScopeDefinitionsAsync(dbContext);
        var controller = CreateController(dbContext);

        var tenant = await dbContext.TenantDefinitions.SingleAsync();
        var tenantRevision = tenant.Revision;
        var tenantResult = await controller.UpdateTenant(
            tenant.TenantCode,
            new UpdateTenantDefinitionRequest
            {
                ExpectedRevision = tenantRevision,
                DisplayName = tenant.DisplayName,
                StorageMode = tenant.StorageMode,
                Description = tenant.Description,
                IsActive = tenant.IsActive
            },
            CancellationToken.None);
        var updatedTenant = Assert.IsType<TenantDefinitionDto>(
            Assert.IsType<OkObjectResult>(tenantResult.Result).Value);
        Assert.True(updatedTenant.Revision > tenantRevision);
        Assert.True(dbContext.GetCommittedRevision() >= updatedTenant.Revision);

        var office = await dbContext.TenantOfficeDefinitions
            .SingleAsync(current => current.OfficeCode == OfficeCodeCatalog.Usenet);
        var officeRevision = office.Revision;
        var officeResult = await controller.UpdateOffice(
            office.OfficeCode,
            new UpdateTenantOfficeDefinitionRequest
            {
                ExpectedRevision = officeRevision,
                DisplayName = office.DisplayName,
                IsHeadOffice = office.IsHeadOffice,
                IsActive = office.IsActive
            },
            CancellationToken.None);
        var updatedOffice = Assert.IsType<TenantOfficeDefinitionDto>(
            Assert.IsType<OkObjectResult>(officeResult.Result).Value);
        Assert.True(updatedOffice.Revision > officeRevision);
        Assert.True(dbContext.GetCommittedRevision() >= updatedOffice.Revision);

        var policy = new DataSharingPolicy
        {
            SourceTenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Usenet,
            TargetTenantCode = TenantScopeCatalog.UsenetGroup,
            TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
            ShareCustomers = true,
            ShareItems = true,
            IsActive = true,
            Note = "same"
        };
        dbContext.DataSharingPolicies.Add(policy);
        await dbContext.SaveChangesAsync();
        var policyRevision = policy.Revision;
        var updatePolicyResult = await controller.UpdateSharingPolicy(
            policy.Id,
            new UpsertDataSharingPolicyRequest
            {
                ExpectedRevision = policyRevision,
                SourceTenantCode = policy.SourceTenantCode,
                SourceOfficeCode = policy.SourceOfficeCode,
                TargetTenantCode = policy.TargetTenantCode,
                TargetOfficeCode = policy.TargetOfficeCode,
                ShareCustomers = policy.ShareCustomers,
                ShareItems = policy.ShareItems,
                ShareInvoices = policy.ShareInvoices,
                SharePayments = policy.SharePayments,
                ShareContracts = policy.ShareContracts,
                ShareReports = policy.ShareReports,
                ShareRentals = policy.ShareRentals,
                ShareDeliveries = policy.ShareDeliveries,
                AllowTargetWrite = policy.AllowTargetWrite,
                IsActive = policy.IsActive,
                Note = policy.Note
            },
            CancellationToken.None);
        var updatedPolicy = Assert.IsType<DataSharingPolicyDto>(
            Assert.IsType<OkObjectResult>(updatePolicyResult.Result).Value);
        Assert.True(updatedPolicy.Revision > policyRevision);
        Assert.True(dbContext.GetCommittedRevision() >= updatedPolicy.Revision);

        var firstDeleteResult = await controller.DeleteSharingPolicy(
            policy.Id,
            updatedPolicy.Revision,
            CancellationToken.None);
        var firstDelete = Assert.IsType<DataSharingPolicyDto>(
            Assert.IsType<OkObjectResult>(firstDeleteResult).Value);
        var noOpDeleteResult = await controller.DeleteSharingPolicy(
            policy.Id,
            firstDelete.Revision,
            CancellationToken.None);
        var noOpDelete = Assert.IsType<DataSharingPolicyDto>(
            Assert.IsType<OkObjectResult>(noOpDeleteResult).Value);
        Assert.True(noOpDelete.Revision > firstDelete.Revision);
        Assert.True(dbContext.GetCommittedRevision() >= noOpDelete.Revision);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private AppDbContext CreateDbContext(TestCurrentUserContext? currentUser = null)
    {
        currentUser ??= new TestCurrentUserContext();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        var dbContext = new AppDbContext(options, currentUser, new RevisionClock());
        dbContext.Database.EnsureCreated();
        return dbContext;
    }

    private static TenantSettingsController CreateController(
        AppDbContext dbContext,
        TestCurrentUserContext? currentUser = null)
    {
        currentUser ??= new TestCurrentUserContext();
        return new TenantSettingsController(
            dbContext,
            new OfficeScopeService(currentUser, dbContext));
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

    private static Task InstallFullRouteUniqueIndexAsync(AppDbContext dbContext)
        => dbContext.Database.ExecuteSqlRawAsync(
            """
            DROP INDEX IF EXISTS "IX_DataSharingPolicies_SourceTarget";
            CREATE UNIQUE INDEX "IX_DataSharingPolicies_SourceTarget"
                ON "DataSharingPolicies" (
                    "SourceTenantCode",
                    "SourceOfficeCode",
                    "TargetTenantCode",
                    "TargetOfficeCode");
            """);

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
