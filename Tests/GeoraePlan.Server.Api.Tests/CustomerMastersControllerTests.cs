using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class CustomerMastersControllerTests
{
    [Fact]
    public async Task Create_ReturnsForbid_WhenUserCannotEditCustomers()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var currentUser = new TestCurrentUserContext
        {
            Username = "readonly-customer-master",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = []
        };
        var revisionClock = new RevisionClock();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var dbContext = new AppDbContext(options, currentUser, revisionClock);
        await dbContext.Database.EnsureCreatedAsync();

        var controller = new CustomerMastersController(
            dbContext,
            new OfficeScopeService(currentUser, dbContext));
        var masterId = Guid.Parse("a5555555-5555-5555-5555-555555555555");

        var response = await controller.Create(new CustomerMasterDto
        {
            Id = masterId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "권한 없는 기준 거래처",
            NameMatchKey = "UNAUTHORIZEDCUSTOMERMASTER"
        }, CancellationToken.None);

        Assert.IsType<ForbidResult>(response.Result);
        Assert.False(await dbContext.CustomerMasters.IgnoreQueryFilters().AnyAsync(master => master.Id == masterId));
    }

    [Fact]
    public async Task Create_AllowsCustomerEditorAndNormalizesScope()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var currentUser = new TestCurrentUserContext
        {
            Username = "customer-master-editor",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = [PermissionNames.CustomerEdit]
        };
        var revisionClock = new RevisionClock();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var dbContext = new AppDbContext(options, currentUser, revisionClock);
        await dbContext.Database.EnsureCreatedAsync();

        var controller = new CustomerMastersController(
            dbContext,
            new OfficeScopeService(currentUser, dbContext));
        var masterId = Guid.Parse("a5666666-6666-6666-6666-666666666666");

        var response = await controller.Create(new CustomerMasterDto
        {
            Id = masterId,
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            NameOriginal = "권한 있는 기준 거래처",
            NameMatchKey = "AUTHORIZEDCUSTOMERMASTER"
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var dto = Assert.IsType<CustomerMasterDto>(ok.Value);
        Assert.Equal(masterId, dto.Id);
        Assert.Equal(OfficeCodeCatalog.Yeonsu, dto.OfficeCode);
        Assert.Equal(TenantScopeCatalog.UsenetGroup, dto.TenantCode);
    }

    private sealed class TestCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId { get; init; } = Guid.NewGuid();
        public string Username { get; init; } = string.Empty;
        public string TenantCode { get; init; } = TenantScopeCatalog.UsenetGroup;
        public string OfficeCode { get; init; } = OfficeCodeCatalog.Usenet;
        public string ScopeType { get; init; } = TenantScopeCatalog.ScopeOfficeOnly;
        public bool IsAdmin { get; init; }
        public bool IsGodMode { get; init; }
        public IReadOnlyCollection<string> Permissions { get; init; } = [];

        public bool HasPermission(string permission)
            => IsAdmin || IsGodMode || Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);
    }
}
