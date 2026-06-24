using System.Security.Claims;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class ActiveUserJwtBearerEventsTests
{
    [Fact]
    public void TenantDatabaseConnectionResolver_IgnoresTenantHeader_ForTenantScopedAdmin()
    {
        var resolver = CreateHttpResolver(
            user: CreatePrincipal(
                isAdmin: true,
                scopeType: TenantScopeCatalog.ScopeTenantAll,
                tenantCode: TenantScopeCatalog.UsenetGroup,
                officeCode: OfficeCodeCatalog.Usenet),
            requestedTenantCode: TenantScopeCatalog.Itworld);

        var resolved = resolver.ResolveCurrent();

        Assert.Equal(TenantScopeCatalog.UsenetGroup, resolved.TenantCode);
        Assert.False(resolved.IsDedicatedBusinessDatabase);
        Assert.Equal("Host=default", resolved.ConnectionString);
    }

    [Fact]
    public void TenantDatabaseConnectionResolver_HonorsTenantHeader_ForGlobalAdmin()
    {
        var resolver = CreateHttpResolver(
            user: CreatePrincipal(
                isAdmin: true,
                scopeType: TenantScopeCatalog.ScopeAdmin,
                tenantCode: TenantScopeCatalog.UsenetGroup,
                officeCode: OfficeCodeCatalog.Usenet),
            requestedTenantCode: TenantScopeCatalog.Itworld);

        var resolved = resolver.ResolveCurrent();

        Assert.Equal(TenantScopeCatalog.Itworld, resolved.TenantCode);
        Assert.True(resolved.IsDedicatedBusinessDatabase);
        Assert.Equal("Host=itworld", resolved.ConnectionString);
    }

    [Fact]
    public async Task IsActiveUserAsync_UsesCentralUserActiveAndDeletedState()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"georaeplan-active-user-{Guid.NewGuid():N}.db");

        try
        {
            var activeUserId = Guid.NewGuid();
            var inactiveUserId = Guid.NewGuid();
            var deletedUserId = Guid.NewGuid();

            await using (var dbContext = CreateDbContext(tempDb))
            {
                await dbContext.Database.EnsureCreatedAsync();
                dbContext.Users.AddRange(
                    new UserAccount
                    {
                        Id = activeUserId,
                        Username = "active-token-user",
                        PasswordHash = "unused",
                        Role = "User",
                        TenantCode = TenantScopeCatalog.UsenetGroup,
                        OfficeCode = OfficeCodeCatalog.Usenet,
                        ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
                        IsActive = true
                    },
                    new UserAccount
                    {
                        Id = inactiveUserId,
                        Username = "inactive-token-user",
                        PasswordHash = "unused",
                        Role = "User",
                        TenantCode = TenantScopeCatalog.UsenetGroup,
                        OfficeCode = OfficeCodeCatalog.Usenet,
                        ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
                        IsActive = false
                    },
                    new UserAccount
                    {
                        Id = deletedUserId,
                        Username = "deleted-token-user",
                        PasswordHash = "unused",
                        Role = "User",
                        TenantCode = TenantScopeCatalog.UsenetGroup,
                        OfficeCode = OfficeCodeCatalog.Usenet,
                        ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
                        IsActive = true,
                        IsDeleted = true
                    });
                await dbContext.SaveChangesAsync();
            }

            var validator = new ActiveUserSessionValidator(CreateResolver(tempDb));

            Assert.True(await validator.IsActiveUserAsync(activeUserId, CancellationToken.None));
            Assert.False(await validator.IsActiveUserAsync(inactiveUserId, CancellationToken.None));
            Assert.False(await validator.IsActiveUserAsync(deletedUserId, CancellationToken.None));
            Assert.False(await validator.IsActiveUserAsync(Guid.NewGuid(), CancellationToken.None));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(tempDb))
                File.Delete(tempDb);
        }
    }

    [Fact]
    public async Task TokenValidated_Fails_WhenUserIdClaimIsMissingOrInactive()
    {
        var inactiveValidator = new StubActiveUserSessionValidator(false);
        var events = new ActiveUserJwtBearerEvents(inactiveValidator);

        var missingUserIdContext = CreateTokenValidatedContext(null);
        await events.TokenValidated(missingUserIdContext);

        Assert.NotNull(missingUserIdContext.Result?.Failure);

        var userId = Guid.NewGuid();
        var inactiveContext = CreateTokenValidatedContext(userId);
        await events.TokenValidated(inactiveContext);

        Assert.Equal(userId, inactiveValidator.LastUserId);
        Assert.NotNull(inactiveContext.Result?.Failure);
    }

    [Fact]
    public async Task TokenValidated_AllowsActiveUser()
    {
        var activeValidator = new StubActiveUserSessionValidator(true);
        var events = new ActiveUserJwtBearerEvents(activeValidator);
        var userId = Guid.NewGuid();
        var context = CreateTokenValidatedContext(userId);

        await events.TokenValidated(context);

        Assert.Equal(userId, activeValidator.LastUserId);
        Assert.Null(context.Result?.Failure);
    }

    private static TokenValidatedContext CreateTokenValidatedContext(Guid? userId)
    {
        var httpContext = new DefaultHttpContext();
        var scheme = new AuthenticationScheme(
            JwtBearerDefaults.AuthenticationScheme,
            JwtBearerDefaults.AuthenticationScheme,
            typeof(JwtBearerHandler));
        var context = new TokenValidatedContext(httpContext, scheme, new JwtBearerOptions());
        var claims = new List<Claim>();
        if (userId.HasValue)
            claims.Add(new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()));
        context.Principal = new ClaimsPrincipal(new ClaimsIdentity(claims, JwtBearerDefaults.AuthenticationScheme));
        return context;
    }

    private static TenantDatabaseConnectionResolver CreateResolver(string sqliteDbPath)
        => new(
            new TenantDatabaseRoutingOptions
            {
                UseSqlite = true,
                SqliteDbPath = sqliteDbPath
            },
            new HttpContextAccessor());

    private static TenantDatabaseConnectionResolver CreateHttpResolver(
        ClaimsPrincipal user,
        string requestedTenantCode)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/sync/pull";
        httpContext.Request.Headers["X-Tenant-Code"] = requestedTenantCode;
        httpContext.User = user;

        return new TenantDatabaseConnectionResolver(
            new TenantDatabaseRoutingOptions
            {
                UseSqlite = false,
                DefaultConnectionString = "Host=default",
                DedicatedBusinessConnections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [TenantScopeCatalog.Itworld] = "Host=itworld"
                }
            },
            new HttpContextAccessor
            {
                HttpContext = httpContext
            });
    }

    private static ClaimsPrincipal CreatePrincipal(
        bool isAdmin,
        string scopeType,
        string tenantCode,
        string officeCode)
    {
        var claims = new List<Claim>
        {
            new("tenant", tenantCode),
            new("office", officeCode),
            new("scope", scopeType)
        };
        if (isAdmin)
            claims.Add(new Claim(ClaimTypes.Role, "Admin"));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static AppDbContext CreateDbContext(string sqliteDbPath)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={sqliteDbPath}")
            .Options;
        return new AppDbContext(options, new TestCurrentUserContext(), new RevisionClock());
    }

    private sealed class StubActiveUserSessionValidator(bool isActive) : IActiveUserSessionValidator
    {
        public Guid? LastUserId { get; private set; }

        public Task<bool> IsActiveUserAsync(Guid userId, CancellationToken cancellationToken)
        {
            LastUserId = userId;
            return Task.FromResult(isActive);
        }
    }

    private sealed class TestCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId { get; init; } = Guid.NewGuid();
        public string Username { get; init; } = "active-user-test";
        public string TenantCode { get; init; } = TenantScopeCatalog.UsenetGroup;
        public string OfficeCode { get; init; } = OfficeCodeCatalog.Usenet;
        public string ScopeType { get; init; } = TenantScopeCatalog.ScopeAdmin;
        public bool IsAdmin { get; init; } = true;
        public bool IsGodMode { get; init; }
        public bool HasPermission(string permission) => true;
    }
}
