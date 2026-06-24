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

public sealed class UsersControllerScopeTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public UsersControllerScopeTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
    }

    [Fact]
    public async Task UsersController_ForTenantAdmin_FiltersUsersAndBlocksOutOfScopeWrites()
    {
        var currentUser = CreateTenantAdmin();
        await using var dbContext = CreateDbContext(currentUser);

        var visibleUser = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "visible-user",
            PasswordHash = "hash",
            Role = "User",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            IsActive = true
        };
        var hiddenTenantUser = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "hidden-itworld-user",
            PasswordHash = "hash",
            Role = "User",
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ScopeType = TenantScopeCatalog.ScopeTenantAll,
            IsActive = true
        };
        var hiddenGlobalAdmin = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "hidden-global-admin",
            PasswordHash = "hash",
            Role = "Admin",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin,
            IsActive = true
        };
        dbContext.Users.AddRange(visibleUser, hiddenTenantUser, hiddenGlobalAdmin);
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);

        var getResponse = await controller.GetAll(CancellationToken.None);
        var getOk = Assert.IsType<OkObjectResult>(getResponse.Result);
        var rows = Assert.IsType<List<UserAccountDto>>(getOk.Value);
        var row = Assert.Single(rows);
        Assert.Equal(visibleUser.Id, row.Id);

        var hiddenUpdate = await controller.Update(
            hiddenTenantUser.Id,
            BuildUpdateRequest(hiddenTenantUser),
            CancellationToken.None);
        Assert.IsType<NotFoundResult>(hiddenUpdate.Result);

        var globalAdminPassword = await controller.UpdatePassword(
            hiddenGlobalAdmin.Id,
            new UpdateUserPasswordRequest
            {
                ExpectedRevision = hiddenGlobalAdmin.Revision,
                Password = "new-password"
            },
            CancellationToken.None);
        Assert.IsType<NotFoundResult>(globalAdminPassword);

        var outOfScopeCreate = await controller.Create(new CreateUserRequest
        {
            Username = "new-itworld-user",
            Password = "password",
            Role = "User",
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ScopeType = TenantScopeCatalog.ScopeTenantAll,
            IsActive = true
        }, CancellationToken.None);
        Assert.IsType<ForbidResult>(outOfScopeCreate.Result);

        var globalScopeCreate = await controller.Create(new CreateUserRequest
        {
            Username = "new-global-admin",
            Password = "password",
            Role = "Admin",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin,
            IsActive = true
        }, CancellationToken.None);
        Assert.IsType<ForbidResult>(globalScopeCreate.Result);
    }

    [Fact]
    public async Task UsersController_ForOfficeAdmin_DoesNotExposeReadOnlySharedOfficeUsers()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "office-admin",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            IsAdmin = true
        };
        await using var dbContext = CreateDbContext(currentUser);

        dbContext.DataSharingPolicies.Add(new DataSharingPolicy
        {
            SourceTenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Yeonsu,
            TargetTenantCode = TenantScopeCatalog.UsenetGroup,
            TargetOfficeCode = OfficeCodeCatalog.Usenet,
            ShareCustomers = true,
            AllowTargetWrite = false,
            IsActive = true
        });
        var currentOfficeUser = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "usenet-user",
            PasswordHash = "hash",
            Role = "User",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            IsActive = true
        };
        var readOnlySharedOfficeUser = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "yeonsu-user",
            PasswordHash = "hash",
            Role = "User",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            IsActive = true
        };
        dbContext.Users.AddRange(currentOfficeUser, readOnlySharedOfficeUser);
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);

        var getResponse = await controller.GetAll(CancellationToken.None);
        var getOk = Assert.IsType<OkObjectResult>(getResponse.Result);
        var rows = Assert.IsType<List<UserAccountDto>>(getOk.Value);
        var row = Assert.Single(rows);
        Assert.Equal(currentOfficeUser.Id, row.Id);

        var readOnlyOfficeUpdate = await controller.Update(
            readOnlySharedOfficeUser.Id,
            BuildUpdateRequest(readOnlySharedOfficeUser),
            CancellationToken.None);
        Assert.IsType<NotFoundResult>(readOnlyOfficeUpdate.Result);
    }

    public void Dispose()
        => _connection.Dispose();

    private AppDbContext CreateDbContext(TestCurrentUserContext currentUser)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        var dbContext = new AppDbContext(options, currentUser, new RevisionClock());
        dbContext.Database.EnsureCreated();
        return dbContext;
    }

    private static UsersController CreateController(
        AppDbContext dbContext,
        TestCurrentUserContext currentUser)
        => new(
            dbContext,
            currentUser,
            new OfficeScopeService(currentUser, dbContext));

    private static UpdateUserRequest BuildUpdateRequest(UserAccount user)
        => new()
        {
            ExpectedRevision = user.Revision,
            Username = user.Username,
            Role = user.Role,
            TenantCode = user.TenantCode,
            OfficeCode = user.OfficeCode,
            ScopeType = user.ScopeType,
            IsActive = user.IsActive,
            Permissions = []
        };

    private static TestCurrentUserContext CreateTenantAdmin()
        => new()
        {
            Username = "tenant-admin",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeTenantAll,
            IsAdmin = true
        };

    private sealed class TestCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId { get; init; } = Guid.NewGuid();
        public string Username { get; init; } = string.Empty;
        public string TenantCode { get; init; } = TenantScopeCatalog.UsenetGroup;
        public string OfficeCode { get; init; } = OfficeCodeCatalog.Usenet;
        public string ScopeType { get; init; } = TenantScopeCatalog.ScopeOfficeOnly;
        public bool IsAdmin { get; init; }
        public bool IsGodMode { get; init; }

        public bool HasPermission(string permission)
            => IsAdmin || IsGodMode;
    }
}
