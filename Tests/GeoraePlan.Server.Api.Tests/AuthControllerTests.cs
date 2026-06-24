using System.Security.Claims;
using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class AuthControllerTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public AuthControllerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
    }

    [Fact]
    public async Task Refresh_ReturnsNewLoginResponse_ForActiveAuthenticatedUser()
    {
        var user = new UserAccount
        {
            Username = "long-running-user",
            PasswordHash = "unused-refresh-hash",
            Role = "Admin",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin,
            IsActive = true,
            Permissions =
            [
                new UserPermission { Permission = PermissionNames.InvoiceEdit }
            ]
        };

        await using var dbContext = CreateDbContext();
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, user.Id);
        var response = await controller.Refresh(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<LoginResponse>(ok.Value);
        Assert.False(string.IsNullOrWhiteSpace(payload.Token));
        Assert.Equal(user.Username, payload.User.Username);
        Assert.Contains(PermissionNames.InvoiceEdit, payload.User.Permissions);
        Assert.True(payload.ExpiresAtUtc > DateTime.UtcNow.AddDays(29));
    }

    [Fact]
    public async Task Refresh_ReturnsUnauthorized_WhenAuthenticatedUserIsInactive()
    {
        var user = new UserAccount
        {
            Username = "inactive-user",
            PasswordHash = "unused-refresh-hash",
            Role = "User",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            IsActive = false
        };

        await using var dbContext = CreateDbContext();
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, user.Id);
        var response = await controller.Refresh(CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(response.Result);
    }

    [Fact]
    public async Task Refresh_ReturnsUnauthorized_WhenAuthenticatedUserIsDeleted()
    {
        var user = new UserAccount
        {
            Username = "deleted-refresh-user",
            PasswordHash = "unused-refresh-hash",
            Role = "User",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            IsActive = true,
            IsDeleted = true
        };

        await using var dbContext = CreateDbContext();
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, user.Id);
        var response = await controller.Refresh(CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(response.Result);
    }

    [Fact]
    public async Task Login_ReturnsUnauthorized_WhenUserIsInactiveEvenWithValidPassword()
    {
        var user = new UserAccount
        {
            Username = "inactive-login-user",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("correct-password"),
            Role = "User",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            IsActive = false
        };

        await using var dbContext = CreateDbContext();
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, user.Id);
        var response = await controller.Login(
            new LoginRequest
            {
                Username = user.Username,
                Password = "correct-password"
            },
            CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(response.Result);
    }

    [Fact]
    public async Task Login_ReturnsUnauthorized_WhenUserIsDeletedEvenWithValidPassword()
    {
        var user = new UserAccount
        {
            Username = "deleted-login-user",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("correct-password"),
            Role = "User",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            IsActive = true,
            IsDeleted = true
        };

        await using var dbContext = CreateDbContext();
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        var controller = CreateController(dbContext, user.Id);
        var response = await controller.Login(
            new LoginRequest
            {
                Username = user.Username,
                Password = "correct-password"
            },
            CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(response.Result);
    }

    [Theory]
    [InlineData(null, "password")]
    [InlineData("", "password")]
    [InlineData("   ", "password")]
    [InlineData("active-user", null)]
    [InlineData("active-user", "")]
    [InlineData("active-user", "   ")]
    public async Task Login_ReturnsUnauthorized_ForBlankOrNullCredentials(string? username, string? password)
    {
        await using var dbContext = CreateDbContext();
        var controller = CreateController(dbContext, Guid.NewGuid());

        var response = await controller.Login(
            new LoginRequest
            {
                Username = username!,
                Password = password!
            },
            CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(response.Result);
    }

    private AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        var dbContext = new AppDbContext(options, new TestCurrentUserContext(), new RevisionClock());
        dbContext.Database.EnsureCreated();
        return dbContext;
    }

    private static AuthController CreateController(AppDbContext dbContext, Guid userId)
    {
        var tokenFactory = new JwtTokenFactory(Options.Create(new JwtOptions
        {
            Issuer = "거래플랜",
            Audience = "georaeplan-client",
            SigningKey = "거래플랜_TestSigningKey_For_Refresh_Token_AtLeast32Chars",
            ExpirationMinutes = 43200
        }));

        return new AuthController(dbContext, tokenFactory)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                        new Claim(ClaimTypes.Name, "refresh-test-user")
                    ], "TestAuth"))
                }
            }
        };
    }

    public void Dispose() => _connection.Dispose();

    private sealed class TestCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId { get; init; } = Guid.NewGuid();
        public string Username { get; init; } = "refresh-test-user";
        public string TenantCode { get; init; } = TenantScopeCatalog.UsenetGroup;
        public string OfficeCode { get; init; } = OfficeCodeCatalog.Usenet;
        public string ScopeType { get; init; } = TenantScopeCatalog.ScopeAdmin;
        public bool IsAdmin { get; init; } = true;
        public bool IsGodMode { get; init; }
        public bool HasPermission(string permission) => true;
    }
}
