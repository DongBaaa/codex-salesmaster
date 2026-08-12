using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Security;
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

    [Fact]
    public async Task OfficeOnlyAdmin_WriteSharing_DoesNotExpandUserAccountManagementScope()
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
            SourceTenantCode = currentUser.TenantCode,
            SourceOfficeCode = OfficeCodeCatalog.Yeonsu,
            TargetTenantCode = currentUser.TenantCode,
            TargetOfficeCode = currentUser.OfficeCode,
            ShareCustomers = true,
            AllowTargetWrite = true,
            IsActive = true
        });
        var ownOfficeUser = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "usenet-user",
            PasswordHash = "own-hash",
            Role = "User",
            TenantCode = currentUser.TenantCode,
            OfficeCode = currentUser.OfficeCode,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            IsActive = true
        };
        var sourceOfficeUser = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "yeonsu-user",
            PasswordHash = "source-hash",
            Role = "User",
            TenantCode = currentUser.TenantCode,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            IsActive = true,
            Permissions =
            [
                new UserPermission { Permission = "ExistingPermission" }
            ]
        };
        dbContext.Users.AddRange(ownOfficeUser, sourceOfficeUser);
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);

        var getResponse = await controller.GetAll(CancellationToken.None);
        var getOk = Assert.IsType<OkObjectResult>(getResponse.Result);
        var rows = Assert.IsType<List<UserAccountDto>>(getOk.Value);
        var row = Assert.Single(rows);
        Assert.Equal(ownOfficeUser.Id, row.Id);

        var create = await controller.Create(new CreateUserRequest
        {
            Username = "new-yeonsu-user",
            Password = "password",
            Role = "User",
            TenantCode = currentUser.TenantCode,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            IsActive = true
        }, CancellationToken.None);
        Assert.IsType<ForbidResult>(create.Result);

        var reassignmentRequest = BuildUpdateRequest(ownOfficeUser);
        reassignmentRequest.OfficeCode = OfficeCodeCatalog.Yeonsu;
        var reassignment = await controller.Update(
            ownOfficeUser.Id,
            reassignmentRequest,
            CancellationToken.None);
        Assert.IsType<ForbidResult>(reassignment.Result);

        var sourceOfficeUpdate = await controller.Update(
            sourceOfficeUser.Id,
            BuildUpdateRequest(sourceOfficeUser),
            CancellationToken.None);
        Assert.IsType<NotFoundResult>(sourceOfficeUpdate.Result);

        var permissionUpdate = await controller.UpdatePermissions(
            sourceOfficeUser.Id,
            new UpdateUserPermissionsRequest
            {
                ExpectedRevision = sourceOfficeUser.Revision,
                Permissions = ["ReplacementPermission"]
            },
            CancellationToken.None);
        Assert.IsType<NotFoundResult>(permissionUpdate.Result);

        var passwordUpdate = await controller.UpdatePassword(
            sourceOfficeUser.Id,
            new UpdateUserPasswordRequest
            {
                ExpectedRevision = sourceOfficeUser.Revision,
                Password = "new-password"
            },
            CancellationToken.None);
        Assert.IsType<NotFoundResult>(passwordUpdate);

        var delete = await controller.Delete(
            sourceOfficeUser.Id,
            sourceOfficeUser.Revision,
            CancellationToken.None);
        Assert.IsType<NotFoundResult>(delete);

        dbContext.ChangeTracker.Clear();
        var persistedOwnOffice = await dbContext.Users.IgnoreQueryFilters()
            .SingleAsync(user => user.Id == ownOfficeUser.Id);
        Assert.Equal(OfficeCodeCatalog.Usenet, persistedOwnOffice.OfficeCode);

        var persistedSourceOffice = await dbContext.Users.IgnoreQueryFilters()
            .SingleAsync(user => user.Id == sourceOfficeUser.Id);
        Assert.False(persistedSourceOffice.IsDeleted);
        Assert.Equal("source-hash", persistedSourceOffice.PasswordHash);
        Assert.Equal(
            ["ExistingPermission"],
            await dbContext.UserPermissions
                .Where(permission => permission.UserId == sourceOfficeUser.Id)
                .Select(permission => permission.Permission)
                .ToListAsync());
        Assert.False(await dbContext.Users.IgnoreQueryFilters()
            .AnyAsync(user => user.Username == "new-yeonsu-user"));
    }

    [Fact]
    public async Task OfficeOnlyAdmin_CannotAssignTenantAll_ToSelfOtherUserOrNewUser()
    {
        var actorId = Guid.NewGuid();
        var currentUser = new TestCurrentUserContext
        {
            UserId = actorId,
            Username = "office-admin",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            IsAdmin = true
        };
        await using var dbContext = CreateDbContext(currentUser);

        dbContext.DataSharingPolicies.Add(new DataSharingPolicy
        {
            SourceTenantCode = currentUser.TenantCode,
            SourceOfficeCode = OfficeCodeCatalog.Yeonsu,
            TargetTenantCode = currentUser.TenantCode,
            TargetOfficeCode = currentUser.OfficeCode,
            ShareCustomers = true,
            AllowTargetWrite = true,
            IsActive = true
        });
        var actor = new UserAccount
        {
            Id = actorId,
            Username = currentUser.Username,
            PasswordHash = "hash",
            Role = "Admin",
            TenantCode = currentUser.TenantCode,
            OfficeCode = currentUser.OfficeCode,
            ScopeType = currentUser.ScopeType,
            IsActive = true
        };
        var managedUser = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "managed-user",
            PasswordHash = "hash",
            Role = "User",
            TenantCode = currentUser.TenantCode,
            OfficeCode = currentUser.OfficeCode,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            IsActive = true
        };
        dbContext.Users.AddRange(actor, managedUser);
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);

        var selfRequest = BuildUpdateRequest(actor);
        selfRequest.ScopeType = TenantScopeCatalog.ScopeTenantAll;
        var selfUpdate = await controller.Update(actor.Id, selfRequest, CancellationToken.None);
        Assert.IsType<ForbidResult>(selfUpdate.Result);

        var otherRequest = BuildUpdateRequest(managedUser);
        otherRequest.ScopeType = TenantScopeCatalog.ScopeTenantAll;
        var otherUpdate = await controller.Update(managedUser.Id, otherRequest, CancellationToken.None);
        Assert.IsType<ForbidResult>(otherUpdate.Result);

        var create = await controller.Create(new CreateUserRequest
        {
            Username = "tenant-all-user",
            Password = "password",
            Role = "User",
            TenantCode = currentUser.TenantCode,
            OfficeCode = currentUser.OfficeCode,
            ScopeType = TenantScopeCatalog.ScopeTenantAll,
            IsActive = true
        }, CancellationToken.None);
        Assert.IsType<ForbidResult>(create.Result);

        dbContext.ChangeTracker.Clear();
        Assert.Equal(
            TenantScopeCatalog.ScopeOfficeOnly,
            await dbContext.Users.IgnoreQueryFilters()
                .Where(user => user.Id == actor.Id)
                .Select(user => user.ScopeType)
                .SingleAsync());
        Assert.Equal(
            TenantScopeCatalog.ScopeOfficeOnly,
            await dbContext.Users.IgnoreQueryFilters()
                .Where(user => user.Id == managedUser.Id)
                .Select(user => user.ScopeType)
                .SingleAsync());
        Assert.False(await dbContext.Users.IgnoreQueryFilters()
            .AnyAsync(user => user.Username == "tenant-all-user"));
    }

    [Fact]
    public async Task TenantAllAdmin_CanAssignTenantAll_WithinSameTenant()
    {
        var currentUser = CreateTenantAdmin();
        await using var dbContext = CreateDbContext(currentUser);

        var managedUser = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "managed-user",
            PasswordHash = "hash",
            Role = "User",
            TenantCode = currentUser.TenantCode,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            IsActive = true
        };
        dbContext.Users.Add(managedUser);
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, currentUser);
        var updateRequest = BuildUpdateRequest(managedUser);
        updateRequest.ScopeType = TenantScopeCatalog.ScopeTenantAll;

        var update = await controller.Update(managedUser.Id, updateRequest, CancellationToken.None);
        Assert.IsType<OkObjectResult>(update.Result);

        var create = await controller.Create(new CreateUserRequest
        {
            Username = "new-tenant-admin",
            Password = "password",
            Role = "Admin",
            TenantCode = currentUser.TenantCode,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeTenantAll,
            IsActive = true
        }, CancellationToken.None);
        Assert.IsType<CreatedAtActionResult>(create.Result);

        dbContext.ChangeTracker.Clear();
        Assert.Equal(
            2,
            await dbContext.Users.IgnoreQueryFilters()
                .CountAsync(user =>
                    user.TenantCode == currentUser.TenantCode &&
                    user.ScopeType == TenantScopeCatalog.ScopeTenantAll));
    }

    [Fact]
    public async Task UpdatePermissions_IncrementsUserRevisionAndRejectsStaleSecondWrite()
    {
        var currentUser = CreateTenantAdmin();
        await using var dbContext = CreateDbContext(currentUser);

        var user = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "permission-concurrency-user",
            PasswordHash = "hash",
            Role = "User",
            TenantCode = currentUser.TenantCode,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            IsActive = true,
            Permissions =
            [
                new UserPermission { Permission = PermissionNames.CompanyProfileEdit }
            ]
        };
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        var originalRevision = user.Revision;
        var controller = CreateController(dbContext, currentUser);
        var firstResponse = await controller.UpdatePermissions(
            user.Id,
            new UpdateUserPermissionsRequest
            {
                ExpectedRevision = originalRevision,
                Permissions = [PermissionNames.CustomerEdit]
            },
            CancellationToken.None);

        var firstOk = Assert.IsType<OkObjectResult>(firstResponse.Result);
        var firstPayload = Assert.IsType<UserAccountDto>(firstOk.Value);
        Assert.True(
            firstPayload.Revision > originalRevision,
            $"Permission changes must advance the user revision. before={originalRevision}, after={firstPayload.Revision}");

        dbContext.ChangeTracker.Clear();
        var staleResponse = await controller.UpdatePermissions(
            user.Id,
            new UpdateUserPermissionsRequest
            {
                ExpectedRevision = originalRevision,
                Permissions = [PermissionNames.ItemEdit]
            },
            CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(staleResponse.Result);
        Assert.Equal(
            [PermissionNames.CustomerEdit],
            await dbContext.UserPermissions
                .Where(permission => permission.UserId == user.Id)
                .Select(permission => permission.Permission)
                .ToListAsync());
    }

    [Fact]
    public async Task Update_NoOpRequest_AdvancesAggregateRevisionAndUpdatedAt()
    {
        var currentUser = CreateTenantAdmin();
        await using var dbContext = CreateDbContext(currentUser);
        var user = CreateManagedUser("no-op-user");
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        var originalRevision = user.Revision;
        var originalUpdatedAtUtc = user.UpdatedAtUtc;
        var controller = CreateController(dbContext, currentUser);

        var response = await controller.Update(
            user.Id,
            BuildUpdateRequest(user),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<UserAccountDto>(ok.Value);
        Assert.True(payload.Revision > originalRevision);
        Assert.True(payload.UpdatedAtUtc > originalUpdatedAtUtc);

        dbContext.ChangeTracker.Clear();
        var persisted = await dbContext.Users.SingleAsync(current => current.Id == user.Id);
        Assert.Equal(payload.Revision, persisted.Revision);
        Assert.Equal(payload.UpdatedAtUtc, persisted.UpdatedAtUtc);
    }

    [Fact]
    public async Task PasswordOnlyFlow_UsesRevisionReturnedByPrecedingNoOpUpdate()
    {
        var currentUser = CreateTenantAdmin();
        await using var dbContext = CreateDbContext(currentUser);
        var user = CreateManagedUser("password-only-user");
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        var originalRevision = user.Revision;
        var controller = CreateController(dbContext, currentUser);
        var updateResponse = await controller.Update(
            user.Id,
            BuildUpdateRequest(user),
            CancellationToken.None);
        var updateOk = Assert.IsType<OkObjectResult>(updateResponse.Result);
        var updatedUser = Assert.IsType<UserAccountDto>(updateOk.Value);

        Assert.True(updatedUser.Revision > originalRevision);
        var passwordResponse = await controller.UpdatePassword(
            user.Id,
            new UpdateUserPasswordRequest
            {
                ExpectedRevision = updatedUser.Revision,
                Password = "replacement-password"
            },
            CancellationToken.None);
        Assert.IsType<NoContentResult>(passwordResponse);

        dbContext.ChangeTracker.Clear();
        var persisted = await dbContext.Users.SingleAsync(current => current.Id == user.Id);
        Assert.True(persisted.Revision > updatedUser.Revision);
        Assert.True(BCrypt.Net.BCrypt.Verify("replacement-password", persisted.PasswordHash));

        var stalePasswordResponse = await controller.UpdatePassword(
            user.Id,
            new UpdateUserPasswordRequest
            {
                ExpectedRevision = updatedUser.Revision,
                Password = "must-not-apply"
            },
            CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(stalePasswordResponse);
    }

    [Fact]
    public async Task Update_PermissionOnlyRequest_AdvancesAggregateAndRejectsStaleWrite()
    {
        var currentUser = CreateTenantAdmin();
        await using var dbContext = CreateDbContext(currentUser);
        var user = CreateManagedUser("permission-only-user");
        user.Permissions =
        [
            new UserPermission { Permission = PermissionNames.CompanyProfileEdit }
        ];
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        var originalRevision = user.Revision;
        var controller = CreateController(dbContext, currentUser);
        var request = BuildUpdateRequest(user);
        request.Permissions = [PermissionNames.CustomerEdit];
        var response = await controller.Update(user.Id, request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<UserAccountDto>(ok.Value);
        Assert.True(payload.Revision > originalRevision);
        Assert.Equal([PermissionNames.CustomerEdit], payload.Permissions);

        dbContext.ChangeTracker.Clear();
        var staleRequest = BuildUpdateRequest(user);
        staleRequest.ExpectedRevision = originalRevision;
        staleRequest.Permissions = [PermissionNames.ItemEdit];
        var staleResponse = await controller.Update(user.Id, staleRequest, CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(staleResponse.Result);
        Assert.Equal(
            [PermissionNames.CustomerEdit],
            await dbContext.UserPermissions
                .Where(permission => permission.UserId == user.Id)
                .Select(permission => permission.Permission)
                .ToListAsync());
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

    private static UserAccount CreateManagedUser(string username)
        => new()
        {
            Id = Guid.NewGuid(),
            Username = username,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("initial-password"),
            Role = "User",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            IsActive = true
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
