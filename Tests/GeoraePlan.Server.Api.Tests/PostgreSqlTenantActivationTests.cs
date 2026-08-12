using System.Security.Claims;
using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class PostgreSqlTenantActivationTests
{
    [PostgreSqlFact]
    public async Task TenantAndOfficeDeactivation_RejectsPostgreSqlSessionLoginAndRefresh_WhileStorageModeIsPreserved()
    {
        var configuredConnection = Environment.GetEnvironmentVariable(
            PostgreSqlSyncPushMutationIdempotencyTests.ConnectionVariableName);
        Assert.False(string.IsNullOrWhiteSpace(configuredConnection));

        var databaseName = $"gpv1_tenant_{Guid.NewGuid():N}";
        var maintenanceBuilder = new NpgsqlConnectionStringBuilder(configuredConnection)
        {
            Database = "postgres",
            IncludeErrorDetail = false
        };
        var testDatabaseBuilder = new NpgsqlConnectionStringBuilder(
            maintenanceBuilder.ConnectionString)
        {
            Database = databaseName,
            IncludeErrorDetail = false
        };
        var databaseCreated = false;

        try
        {
            await CreateDatabaseAsync(
                maintenanceBuilder.ConnectionString,
                databaseName);
            databaseCreated = true;

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(testDatabaseBuilder.ConnectionString)
                .Options;
            var admin = new TestCurrentUserContext();
            var userId = Guid.NewGuid();
            long userRevision;
            long tenantRevision;

            await using (var initializationDb = CreateDbContext(options, admin))
            {
                await initializationDb.Database.EnsureCreatedAsync();
                var tenant = new TenantDefinition
                {
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    DisplayName = "USENET",
                    StorageMode = TenantScopeCatalog.StorageSharedDatabase,
                    IsActive = true
                };
                var office = new TenantOfficeDefinition
                {
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    DisplayName = "USENET",
                    IsHeadOffice = true,
                    IsActive = true
                };
                var user = new UserAccount
                {
                    Id = userId,
                    Username = "postgres-tenant-activation-user",
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword("test-password"),
                    Role = "User",
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
                    IsActive = true
                };
                initializationDb.AddRange(tenant, office, user);
                await initializationDb.SaveChangesAsync();
                userRevision = user.Revision;
                tenantRevision = tenant.Revision;
            }

            var resolver = new TenantDatabaseConnectionResolver(
                new TenantDatabaseRoutingOptions
                {
                    UseSqlite = false,
                    DefaultConnectionString = testDatabaseBuilder.ConnectionString
                },
                new HttpContextAccessor());
            var validator = new ActiveUserSessionValidator(resolver);
            var currentToken = CreateSessionPrincipal(userId, userRevision);

            Assert.True(await validator.IsCurrentTokenAsync(
                userId,
                currentToken,
                CancellationToken.None));

            await using (var storageModeDb = CreateDbContext(options, admin))
            {
                var tenantController = new TenantSettingsController(
                    storageModeDb,
                    new OfficeScopeService(admin, storageModeDb));
                var response = await tenantController.UpdateTenant(
                    TenantScopeCatalog.UsenetGroup,
                    new UpdateTenantDefinitionRequest
                    {
                        ExpectedRevision = tenantRevision,
                        DisplayName = "USENET",
                        StorageMode = TenantScopeCatalog.StorageDedicatedDatabase,
                        Description = "must remain shared",
                        IsActive = true
                    },
                    CancellationToken.None);
                Assert.IsType<BadRequestObjectResult>(response.Result);

                storageModeDb.ChangeTracker.Clear();
                var unchangedTenant = await storageModeDb.TenantDefinitions
                    .IgnoreQueryFilters()
                    .SingleAsync(current =>
                        current.TenantCode == TenantScopeCatalog.UsenetGroup);
                Assert.Equal(
                    TenantScopeCatalog.StorageSharedDatabase,
                    unchangedTenant.StorageMode);
                Assert.Equal(tenantRevision, unchangedTenant.Revision);
            }

            await AssertAuthenticationAllowedAsync(
                options,
                admin,
                currentToken,
                userId);

            await using (var tenantDb = CreateDbContext(options, admin))
            {
                var tenant = await tenantDb.TenantDefinitions
                    .IgnoreQueryFilters()
                    .SingleAsync(current =>
                        current.TenantCode == TenantScopeCatalog.UsenetGroup);
                tenant.IsActive = false;
                tenant.IsDeleted = true;
                await tenantDb.SaveChangesAsync();
            }

            Assert.False(await validator.IsCurrentTokenAsync(
                userId,
                currentToken,
                CancellationToken.None));
            await AssertAuthenticationRejectedAsync(
                options,
                admin,
                currentToken,
                userId);

            await using (var scopeDb = CreateDbContext(options, admin))
            {
                var tenant = await scopeDb.TenantDefinitions
                    .IgnoreQueryFilters()
                    .SingleAsync(current =>
                        current.TenantCode == TenantScopeCatalog.UsenetGroup);
                tenant.IsActive = true;
                tenant.IsDeleted = false;

                var office = await scopeDb.TenantOfficeDefinitions
                    .IgnoreQueryFilters()
                    .SingleAsync(current =>
                        current.OfficeCode == OfficeCodeCatalog.Usenet);
                office.IsActive = false;
                office.IsDeleted = true;
                await scopeDb.SaveChangesAsync();
            }

            Assert.False(await validator.IsCurrentTokenAsync(
                userId,
                currentToken,
                CancellationToken.None));
            await AssertAuthenticationRejectedAsync(
                options,
                admin,
                currentToken,
                userId);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            if (databaseCreated)
            {
                await DropDatabaseAsync(
                    maintenanceBuilder.ConnectionString,
                    databaseName);
            }
        }
    }

    private static async Task AssertAuthenticationAllowedAsync(
        DbContextOptions<AppDbContext> options,
        TestCurrentUserContext currentUser,
        ClaimsPrincipal principal,
        Guid userId)
    {
        await using var dbContext = CreateDbContext(options, currentUser);
        var controller = CreateAuthController(dbContext, principal);

        var login = await controller.Login(
            new LoginRequest
            {
                Username = "postgres-tenant-activation-user",
                Password = "test-password"
            },
            CancellationToken.None);
        Assert.IsType<OkObjectResult>(login.Result);

        var refresh = await controller.Refresh(CancellationToken.None);
        Assert.Equal(
            userId.ToString(),
            controller.User.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.IsType<OkObjectResult>(refresh.Result);
    }

    private static async Task AssertAuthenticationRejectedAsync(
        DbContextOptions<AppDbContext> options,
        TestCurrentUserContext currentUser,
        ClaimsPrincipal principal,
        Guid userId)
    {
        await using var dbContext = CreateDbContext(options, currentUser);
        var controller = CreateAuthController(dbContext, principal);

        var login = await controller.Login(
            new LoginRequest
            {
                Username = "postgres-tenant-activation-user",
                Password = "test-password"
            },
            CancellationToken.None);
        Assert.IsType<UnauthorizedResult>(login.Result);

        var refresh = await controller.Refresh(CancellationToken.None);
        Assert.Equal(
            userId.ToString(),
            controller.User.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.IsType<UnauthorizedResult>(refresh.Result);
    }

    private static AuthController CreateAuthController(
        AppDbContext dbContext,
        ClaimsPrincipal principal)
        => new(dbContext, new StubJwtTokenFactory())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = principal
                }
            }
        };

    private static ClaimsPrincipal CreateSessionPrincipal(
        Guid userId,
        long userRevision)
        => new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, "postgres-tenant-activation-user"),
            new Claim(ClaimTypes.Role, "User"),
            new Claim("tenant", TenantScopeCatalog.UsenetGroup),
            new Claim("office", OfficeCodeCatalog.Usenet),
            new Claim("scope", TenantScopeCatalog.ScopeOfficeOnly),
            new Claim(
                JwtClaimTypes.UserRevision,
                userRevision.ToString(System.Globalization.CultureInfo.InvariantCulture))
        ],
        JwtBearerDefaults.AuthenticationScheme));

    private static AppDbContext CreateDbContext(
        DbContextOptions<AppDbContext> options,
        TestCurrentUserContext currentUser)
        => new(options, currentUser, new RevisionClock());

    private static async Task CreateDatabaseAsync(
        string maintenanceConnection,
        string databaseName)
    {
        await using var connection = new NpgsqlConnection(maintenanceConnection);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropDatabaseAsync(
        string maintenanceConnection,
        string databaseName)
    {
        await using var connection = new NpgsqlConnection(maintenanceConnection);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)";
        await command.ExecuteNonQueryAsync();
    }

    private sealed class StubJwtTokenFactory : IJwtTokenFactory
    {
        public LoginResponse Create(UserAccount user)
            => new()
            {
                AccessToken = "test-access-token",
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5),
                User = new UserSessionDto
                {
                    UserId = user.Id,
                    Username = user.Username,
                    Role = user.Role,
                    TenantCode = user.TenantCode,
                    OfficeCode = user.OfficeCode,
                    ScopeType = user.ScopeType,
                    Permissions = user.Permissions
                        .Select(current => current.Permission)
                        .ToList()
                }
            };
    }

    private sealed class TestCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId { get; init; } = Guid.NewGuid();
        public string Username { get; init; } = "postgres-admin";
        public string TenantCode { get; init; } = TenantScopeCatalog.UsenetGroup;
        public string OfficeCode { get; init; } = OfficeCodeCatalog.Usenet;
        public string ScopeType { get; init; } = TenantScopeCatalog.ScopeAdmin;
        public bool IsAdmin { get; init; } = true;
        public bool IsGodMode { get; init; } = true;
        public bool HasPermission(string permission) => true;
    }
}
