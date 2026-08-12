using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
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
    public void TenantDatabaseConnectionResolver_RequiredDedicatedTenant_RejectsMissingConnection()
    {
        var resolver = CreateRequiredDedicatedResolver(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        var error = Assert.Throws<InvalidOperationException>(
            () => resolver.ResolveBusinessTenant(TenantScopeCatalog.Itworld));

        Assert.Contains("requires a dedicated", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TenantDatabaseConnectionResolver_RequiredDedicatedTenant_RejectsCentralEquivalentConnection()
    {
        var resolver = CreateRequiredDedicatedResolver(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [TenantScopeCatalog.Itworld] =
                    "Database=central;Host=DB-SERVER;Port=5432;Pooling=false"
            });

        Assert.Throws<InvalidOperationException>(
            () => resolver.ResolveBusinessTenant(TenantScopeCatalog.Itworld));
        Assert.Throws<InvalidOperationException>(
            () => resolver.GetDedicatedBusinessConnections());
    }

    [Fact]
    public void TenantDatabaseConnectionResolver_OptionalDedicatedTenant_AllowsCentralFallback()
    {
        var resolver = new TenantDatabaseConnectionResolver(
            new TenantDatabaseRoutingOptions
            {
                UseSqlite = false,
                DefaultConnectionString = "Host=db-server;Port=5432;Database=central"
            },
            new HttpContextAccessor());

        var resolved = resolver.ResolveBusinessTenant(TenantScopeCatalog.Itworld);

        Assert.False(resolved.IsDedicatedBusinessDatabase);
        Assert.Equal("Host=db-server;Port=5432;Database=central", resolved.ConnectionString);
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
    public async Task IsCurrentTokenAsync_RejectsStaleRoleScopeAndPermissionClaims()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"georaeplan-current-token-{Guid.NewGuid():N}.db");

        try
        {
            var userId = Guid.NewGuid();
            long userRevision;
            await using (var dbContext = CreateDbContext(tempDb))
            {
                await dbContext.Database.EnsureCreatedAsync();
                var user = new UserAccount
                {
                    Id = userId,
                    Username = "scope-token-user",
                    PasswordHash = "unused",
                    Role = "User",
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Yeonsu,
                    ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
                    IsActive = true,
                    Permissions =
                    {
                        new UserPermission { Permission = PermissionNames.CustomerEdit },
                        new UserPermission { Permission = PermissionNames.InvoiceEdit }
                    }
                };
                dbContext.Users.Add(user);
                await dbContext.SaveChangesAsync();
                userRevision = user.Revision;
            }

            var validator = new ActiveUserSessionValidator(CreateResolver(tempDb));
            var currentToken = CreateSessionPrincipal(
                userId,
                userRevision,
                role: "User",
                tenantCode: TenantScopeCatalog.UsenetGroup,
                officeCode: OfficeCodeCatalog.Yeonsu,
                scopeType: TenantScopeCatalog.ScopeOfficeOnly,
                PermissionNames.CustomerEdit,
                PermissionNames.InvoiceEdit);

            Assert.True(await validator.IsCurrentTokenAsync(userId, currentToken, CancellationToken.None));

            var staleRoleToken = CreateSessionPrincipal(
                userId,
                userRevision,
                role: "Admin",
                tenantCode: TenantScopeCatalog.UsenetGroup,
                officeCode: OfficeCodeCatalog.Yeonsu,
                scopeType: TenantScopeCatalog.ScopeOfficeOnly,
                PermissionNames.CustomerEdit,
                PermissionNames.InvoiceEdit);
            Assert.False(await validator.IsCurrentTokenAsync(userId, staleRoleToken, CancellationToken.None));

            var staleOfficeToken = CreateSessionPrincipal(
                userId,
                userRevision,
                role: "User",
                tenantCode: TenantScopeCatalog.UsenetGroup,
                officeCode: OfficeCodeCatalog.Usenet,
                scopeType: TenantScopeCatalog.ScopeOfficeOnly,
                PermissionNames.CustomerEdit,
                PermissionNames.InvoiceEdit);
            Assert.False(await validator.IsCurrentTokenAsync(userId, staleOfficeToken, CancellationToken.None));

            var staleScopeToken = CreateSessionPrincipal(
                userId,
                userRevision,
                role: "User",
                tenantCode: TenantScopeCatalog.UsenetGroup,
                officeCode: OfficeCodeCatalog.Yeonsu,
                scopeType: TenantScopeCatalog.ScopeTenantAll,
                PermissionNames.CustomerEdit,
                PermissionNames.InvoiceEdit);
            Assert.False(await validator.IsCurrentTokenAsync(userId, staleScopeToken, CancellationToken.None));

            var stalePermissionToken = CreateSessionPrincipal(
                userId,
                userRevision,
                role: "User",
                tenantCode: TenantScopeCatalog.UsenetGroup,
                officeCode: OfficeCodeCatalog.Yeonsu,
                scopeType: TenantScopeCatalog.ScopeOfficeOnly,
                PermissionNames.CustomerEdit,
                PermissionNames.InvoiceEdit,
                PermissionNames.DataBackupRestore);
            Assert.False(await validator.IsCurrentTokenAsync(userId, stalePermissionToken, CancellationToken.None));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(tempDb))
                File.Delete(tempDb);
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task IsCurrentTokenAsync_RejectsInactiveTenantOrOffice(
        bool deactivateTenant,
        bool deactivateOffice)
    {
        var tempDb = Path.Combine(
            Path.GetTempPath(),
            $"georaeplan-inactive-scope-token-{Guid.NewGuid():N}.db");

        try
        {
            var userId = Guid.NewGuid();
            long userRevision;
            await using (var dbContext = CreateDbContext(tempDb))
            {
                await dbContext.Database.EnsureCreatedAsync();
                var user = new UserAccount
                {
                    Id = userId,
                    Username = "inactive-scope-token-user",
                    PasswordHash = "unused",
                    Role = "User",
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
                    IsActive = true
                };
                dbContext.Users.Add(user);
                dbContext.TenantDefinitions.Add(new TenantDefinition
                {
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    DisplayName = "USENET",
                    StorageMode = TenantScopeCatalog.StorageSharedDatabase,
                    IsActive = !deactivateTenant,
                    IsDeleted = deactivateTenant
                });
                dbContext.TenantOfficeDefinitions.Add(new TenantOfficeDefinition
                {
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    DisplayName = "USENET",
                    IsActive = !deactivateOffice,
                    IsDeleted = deactivateOffice
                });
                await dbContext.SaveChangesAsync();
                userRevision = user.Revision;
            }

            var validator = new ActiveUserSessionValidator(CreateResolver(tempDb));
            var token = CreateSessionPrincipal(
                userId,
                userRevision,
                role: "User",
                tenantCode: TenantScopeCatalog.UsenetGroup,
                officeCode: OfficeCodeCatalog.Usenet,
                scopeType: TenantScopeCatalog.ScopeOfficeOnly);

            Assert.False(await validator.IsActiveUserAsync(userId, CancellationToken.None));
            Assert.False(await validator.IsCurrentTokenAsync(userId, token, CancellationToken.None));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(tempDb))
                File.Delete(tempDb);
        }
    }

    [Fact]
    public async Task PasswordReset_InvalidatesOldLoginToken_AndNewLoginTokenPasses()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"georaeplan-password-revision-token-{Guid.NewGuid():N}.db");

        try
        {
            var currentUser = new TestCurrentUserContext();
            await using var dbContext = CreateDbContext(tempDb, currentUser);
            await dbContext.Database.EnsureCreatedAsync();
            var user = new UserAccount
            {
                Id = Guid.NewGuid(),
                Username = "password-revision-user",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("old-password"),
                Role = "User",
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
                IsActive = true,
                Permissions =
                {
                    new UserPermission { Permission = PermissionNames.CustomerEdit }
                }
            };
            dbContext.Users.Add(user);
            await dbContext.SaveChangesAsync();
            var initialRevision = user.Revision;

            var tokenFactory = CreateJwtTokenFactory();
            var authController = new AuthController(dbContext, tokenFactory);
            var oldLoginResponse = await authController.Login(
                new LoginRequest
                {
                    Username = user.Username,
                    Password = "old-password"
                },
                CancellationToken.None);
            var oldLoginOk = Assert.IsType<OkObjectResult>(oldLoginResponse.Result);
            var oldLogin = Assert.IsType<LoginResponse>(oldLoginOk.Value);
            var oldPrincipal = ValidateLoginToken(oldLogin.AccessToken);
            Assert.Equal(
                initialRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                oldPrincipal.FindFirstValue(JwtClaimTypes.UserRevision));

            var validator = new ActiveUserSessionValidator(CreateResolver(tempDb));
            Assert.True(await validator.IsCurrentTokenAsync(user.Id, oldPrincipal, CancellationToken.None));

            var usersController = new UsersController(
                dbContext,
                currentUser,
                new OfficeScopeService(currentUser, dbContext));
            var passwordReset = await usersController.UpdatePassword(
                user.Id,
                new UpdateUserPasswordRequest
                {
                    ExpectedRevision = initialRevision,
                    Password = "new-password"
                },
                CancellationToken.None);
            Assert.IsType<NoContentResult>(passwordReset);

            dbContext.ChangeTracker.Clear();
            var updatedUser = await dbContext.Users
                .Include(current => current.Permissions)
                .SingleAsync(current => current.Id == user.Id);
            Assert.True(updatedUser.Revision > initialRevision);
            Assert.False(await validator.IsCurrentTokenAsync(user.Id, oldPrincipal, CancellationToken.None));

            var events = new ActiveUserJwtBearerEvents(validator);
            var staleContext = CreateTokenValidatedContext(oldPrincipal);
            await events.TokenValidated(staleContext);
            Assert.NotNull(staleContext.Result?.Failure);

            var newLoginResponse = await authController.Login(
                new LoginRequest
                {
                    Username = user.Username,
                    Password = "new-password"
                },
                CancellationToken.None);
            var newLoginOk = Assert.IsType<OkObjectResult>(newLoginResponse.Result);
            var newLogin = Assert.IsType<LoginResponse>(newLoginOk.Value);
            var newPrincipal = ValidateLoginToken(newLogin.AccessToken);
            Assert.Equal(
                updatedUser.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                newPrincipal.FindFirstValue(JwtClaimTypes.UserRevision));
            Assert.True(await validator.IsCurrentTokenAsync(user.Id, newPrincipal, CancellationToken.None));

            var freshContext = CreateTokenValidatedContext(newPrincipal);
            await events.TokenValidated(freshContext);
            Assert.Null(freshContext.Result?.Failure);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(tempDb))
                File.Delete(tempDb);
        }
    }

    [Fact]
    public async Task TokenValidated_Fails_WhenUserRevisionClaimIsMissingOrMalformed()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"georaeplan-invalid-revision-token-{Guid.NewGuid():N}.db");

        try
        {
            var userId = Guid.NewGuid();
            long userRevision;
            await using (var dbContext = CreateDbContext(tempDb))
            {
                await dbContext.Database.EnsureCreatedAsync();
                var user = new UserAccount
                {
                    Id = userId,
                    Username = "invalid-revision-token-user",
                    PasswordHash = "unused",
                    Role = "User",
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
                    IsActive = true
                };
                dbContext.Users.Add(user);
                await dbContext.SaveChangesAsync();
                userRevision = user.Revision;
            }

            var currentPrincipal = CreateSessionPrincipal(
                userId,
                userRevision,
                role: "User",
                tenantCode: TenantScopeCatalog.UsenetGroup,
                officeCode: OfficeCodeCatalog.Usenet,
                scopeType: TenantScopeCatalog.ScopeOfficeOnly);
            var validator = new ActiveUserSessionValidator(CreateResolver(tempDb));
            var events = new ActiveUserJwtBearerEvents(validator);

            var missingRevisionPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
                currentPrincipal.Claims.Where(claim =>
                    !string.Equals(claim.Type, JwtClaimTypes.UserRevision, StringComparison.Ordinal)),
                JwtBearerDefaults.AuthenticationScheme));
            var missingContext = CreateTokenValidatedContext(missingRevisionPrincipal);
            await events.TokenValidated(missingContext);
            Assert.NotNull(missingContext.Result?.Failure);

            var malformedRevisionClaims = currentPrincipal.Claims
                .Where(claim => !string.Equals(claim.Type, JwtClaimTypes.UserRevision, StringComparison.Ordinal))
                .Append(new Claim(JwtClaimTypes.UserRevision, "not-a-revision"));
            var malformedRevisionPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
                malformedRevisionClaims,
                JwtBearerDefaults.AuthenticationScheme));
            var malformedContext = CreateTokenValidatedContext(malformedRevisionPrincipal);
            await events.TokenValidated(malformedContext);
            Assert.NotNull(malformedContext.Result?.Failure);
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

        var missingUserIdContext = CreateTokenValidatedContext((Guid?)null);
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
        var claims = new List<Claim>();
        if (userId.HasValue)
            claims.Add(new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()));
        return CreateTokenValidatedContext(new ClaimsPrincipal(
            new ClaimsIdentity(claims, JwtBearerDefaults.AuthenticationScheme)));
    }

    private static TokenValidatedContext CreateTokenValidatedContext(ClaimsPrincipal principal)
    {
        var httpContext = new DefaultHttpContext();
        var scheme = new AuthenticationScheme(
            JwtBearerDefaults.AuthenticationScheme,
            JwtBearerDefaults.AuthenticationScheme,
            typeof(JwtBearerHandler));
        var context = new TokenValidatedContext(httpContext, scheme, new JwtBearerOptions());
        context.Principal = principal;
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

    private static TenantDatabaseConnectionResolver CreateRequiredDedicatedResolver(
        IReadOnlyDictionary<string, string> dedicatedConnections)
        => new(
            new TenantDatabaseRoutingOptions
            {
                UseSqlite = false,
                DefaultConnectionString =
                    "Host=db-server;Port=5432;Database=central;Pooling=true",
                DedicatedBusinessConnections = dedicatedConnections,
                RequiredDedicatedTenantCodes = [TenantScopeCatalog.Itworld]
            },
            new HttpContextAccessor());

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

    private static ClaimsPrincipal CreateSessionPrincipal(
        Guid userId,
        long userRevision,
        string role,
        string tenantCode,
        string officeCode,
        string scopeType,
        params string[] permissions)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, "scope-token-user"),
            new(ClaimTypes.Role, role),
            new("tenant", tenantCode),
            new("office", officeCode),
            new("scope", scopeType),
            new(JwtClaimTypes.UserRevision, userRevision.ToString(System.Globalization.CultureInfo.InvariantCulture))
        };
        claims.AddRange(permissions.Select(permission => new Claim("perm", permission)));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, JwtBearerDefaults.AuthenticationScheme));
    }

    private static AppDbContext CreateDbContext(string sqliteDbPath)
        => CreateDbContext(sqliteDbPath, new TestCurrentUserContext());

    private static AppDbContext CreateDbContext(
        string sqliteDbPath,
        TestCurrentUserContext currentUser)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={sqliteDbPath}")
            .Options;
        return new AppDbContext(options, currentUser, new RevisionClock());
    }

    private static JwtTokenFactory CreateJwtTokenFactory()
        => new(Options.Create(new JwtOptions
        {
            Issuer = "georaeplan-test",
            Audience = "georaeplan-test-client",
            SigningKey = "GeoraePlan_Test_Signing_Key_At_Least_32_Characters",
            ExpirationMinutes = 60
        }));

    private static ClaimsPrincipal ValidateLoginToken(string accessToken)
    {
        var options = new JwtOptions
        {
            Issuer = "georaeplan-test",
            Audience = "georaeplan-test-client",
            SigningKey = "GeoraePlan_Test_Signing_Key_At_Least_32_Characters",
            ExpirationMinutes = 60
        };
        return new JwtSecurityTokenHandler().ValidateToken(
            accessToken,
            new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey)),
                ValidateIssuer = true,
                ValidIssuer = options.Issuer,
                ValidateAudience = true,
                ValidAudience = options.Audience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero
            },
            out _);
    }

    private sealed class StubActiveUserSessionValidator(bool isActive) : IActiveUserSessionValidator
    {
        public Guid? LastUserId { get; private set; }

        public Task<bool> IsActiveUserAsync(Guid userId, CancellationToken cancellationToken)
        {
            LastUserId = userId;
            return Task.FromResult(isActive);
        }

        public Task<bool> IsCurrentTokenAsync(
            Guid userId,
            ClaimsPrincipal principal,
            CancellationToken cancellationToken)
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
