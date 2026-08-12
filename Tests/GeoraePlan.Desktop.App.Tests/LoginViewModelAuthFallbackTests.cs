using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class LoginViewModelAuthFallbackTests
{
    private static readonly SemaphoreSlim AppRootLock = new(1, 1);

    [Fact]
    public async Task LoginAsync_ServerHttpError_DoesNotOfferOfflineLoginFromCachedPassword()
    {
        await AppRootLock.WaitAsync();
        var tempRoot = PrepareAppRoot("georaeplan-login-http-error");

        try
        {
            await using var db = CreateDbContext(tempRoot);
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = new SessionState();
            var local = CreateLocalStateService(db, session);
            await local.SaveSessionCacheAsync(
                "cached-user",
                "user",
                Array.Empty<string>(),
                TenantScopeCatalog.UsenetGroup,
                TenantScopeCatalog.ScopeOfficeOnly,
                OfficeCodeCatalog.Usenet,
                "cached-password");

            var api = new ErpApiClient(
                new HttpClient(new StaticLoginResponseHandler(HttpStatusCode.InternalServerError))
                {
                    BaseAddress = new Uri("http://localhost/")
                },
                session);
            var viewModel = new LoginViewModel(api, session, local)
            {
                Username = "cached-user",
                Password = "cached-password"
            };
            var loginSucceeded = false;
            viewModel.LoginSucceeded += () => loginSucceeded = true;

            await viewModel.LoginCommand.ExecuteAsync(null);

            Assert.False(viewModel.ShowOfflineButton);
            Assert.False(loginSucceeded);
            Assert.False(session.IsLoggedIn);
            Assert.Contains("오류:", viewModel.ErrorMessage, StringComparison.Ordinal);
        }
        finally
        {
            await CleanupAppRootAsync(tempRoot);
            AppRootLock.Release();
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task LoginAsync_BlockedOrUnauthorizedResponse_DoesNotOfferOfflineLoginFromCachedPassword(HttpStatusCode statusCode)
    {
        await AppRootLock.WaitAsync();
        var tempRoot = PrepareAppRoot($"georaeplan-login-{statusCode:D}");

        try
        {
            await using var db = CreateDbContext(tempRoot);
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = new SessionState();
            var local = CreateLocalStateService(db, session);
            await local.SaveSessionCacheAsync(
                "cached-user",
                "user",
                Array.Empty<string>(),
                TenantScopeCatalog.UsenetGroup,
                TenantScopeCatalog.ScopeOfficeOnly,
                OfficeCodeCatalog.Usenet,
                "cached-password");

            var api = new ErpApiClient(
                new HttpClient(new StaticLoginResponseHandler(statusCode, "BLOCKED"))
                {
                    BaseAddress = new Uri("http://localhost/")
                },
                session);
            var viewModel = new LoginViewModel(api, session, local)
            {
                Username = "cached-user",
                Password = "cached-password"
            };
            var loginSucceeded = false;
            viewModel.LoginSucceeded += () => loginSucceeded = true;

            await viewModel.LoginCommand.ExecuteAsync(null);

            Assert.False(viewModel.ShowOfflineButton);
            Assert.False(loginSucceeded);
            Assert.False(session.IsLoggedIn);
            Assert.Null(await local.GetCachedSessionAsync("cached-user"));
            Assert.False(await local.VerifyCachedSessionPasswordAsync("cached-user", "cached-password"));
        }
        finally
        {
            await CleanupAppRootAsync(tempRoot);
            AppRootLock.Release();
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task LoginAsync_RejectedWrongPassword_PreservesExistingOfflineCache(HttpStatusCode statusCode)
    {
        await AppRootLock.WaitAsync();
        var tempRoot = PrepareAppRoot($"georaeplan-login-wrong-password-{statusCode:D}");

        try
        {
            await using var db = CreateDbContext(tempRoot);
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = new SessionState();
            var local = CreateLocalStateService(db, session);
            await local.SaveSessionCacheAsync(
                "cached-user",
                "user",
                Array.Empty<string>(),
                TenantScopeCatalog.UsenetGroup,
                TenantScopeCatalog.ScopeOfficeOnly,
                OfficeCodeCatalog.Usenet,
                "cached-password");

            var api = new ErpApiClient(
                new HttpClient(new StaticLoginResponseHandler(statusCode, "BLOCKED"))
                {
                    BaseAddress = new Uri("http://localhost/")
                },
                session);
            var viewModel = new LoginViewModel(api, session, local)
            {
                Username = "cached-user",
                Password = "wrong-password"
            };

            await viewModel.LoginCommand.ExecuteAsync(null);

            Assert.False(viewModel.ShowOfflineButton);
            Assert.False(session.IsLoggedIn);
            Assert.NotNull(await local.GetCachedSessionAsync("cached-user"));
            Assert.True(await local.VerifyCachedSessionPasswordAsync("cached-user", "cached-password"));
        }
        finally
        {
            await CleanupAppRootAsync(tempRoot);
            AppRootLock.Release();
        }
    }

    [Fact]
    public async Task LoginAsync_TransportSocketFailure_OffersOfflineLoginWhenCachePasswordMatches()
    {
        await AppRootLock.WaitAsync();
        var tempRoot = PrepareAppRoot("georaeplan-login-network-error");

        try
        {
            await using var db = CreateDbContext(tempRoot);
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = new SessionState();
            var local = CreateLocalStateService(db, session);
            await local.SaveSessionCacheAsync(
                "cached-user",
                "user",
                Array.Empty<string>(),
                TenantScopeCatalog.UsenetGroup,
                TenantScopeCatalog.ScopeOfficeOnly,
                OfficeCodeCatalog.Usenet,
                "cached-password");

            var api = new ErpApiClient(
                new HttpClient(new SocketFailureLoginHandler())
                {
                    BaseAddress = new Uri("http://localhost/")
                },
                session);
            var viewModel = new LoginViewModel(api, session, local)
            {
                Username = "cached-user",
                Password = "cached-password"
            };
            var loginSucceeded = false;
            viewModel.LoginSucceeded += () => loginSucceeded = true;

            await viewModel.LoginCommand.ExecuteAsync(null);

            Assert.True(viewModel.ShowOfflineButton);
            Assert.Contains("서버에 연결할 수 없습니다", viewModel.ErrorMessage, StringComparison.Ordinal);
            Assert.False(session.IsLoggedIn);
            Assert.NotNull(await local.GetCachedSessionAsync("cached-user"));
            Assert.True(await local.VerifyCachedSessionPasswordAsync("cached-user", "cached-password"));

            await viewModel.OfflineLoginCommand.ExecuteAsync(null);

            Assert.True(loginSucceeded);
            Assert.True(session.IsLoggedIn);
            Assert.True(session.IsOfflineMode);
            Assert.Equal(OfficeCodeCatalog.Usenet, session.OfficeCode);
        }
        finally
        {
            await CleanupAppRootAsync(tempRoot);
            AppRootLock.Release();
        }
    }

    [Fact]
    public async Task LoginAsync_SuccessfulFullFlowDoesNotSaveUnrelatedTrackedSettings()
    {
        await AppRootLock.WaitAsync();
        var tempRoot = PrepareAppRoot("georaeplan-login-success-independent-settings");

        try
        {
            await using var db = CreateDbContext(tempRoot);
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = new SessionState();
            var local = CreateLocalStateService(db, session);
            await local.SetSettingAsync("Unrelated.Modified", "persisted-value");
            var modified = await db.Settings.FindAsync("Unrelated.Modified")
                ?? throw new InvalidOperationException("Missing modified test setting.");
            modified.Value = "pending-value";
            db.Settings.Add(new LocalSetting
            {
                Key = "Unrelated.Added",
                Value = "must-not-be-saved"
            });

            var user = new UserSessionDto
            {
                UserId = Guid.NewGuid(),
                Username = "successful-user",
                Role = "User",
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
                Permissions = ["Customers.Read"]
            };
            var api = new ErpApiClient(
                new HttpClient(new SuccessfulLoginHandler(user))
                {
                    BaseAddress = new Uri("http://localhost/")
                },
                session,
                local);
            var viewModel = new LoginViewModel(api, session, local)
            {
                Username = user.Username,
                Password = "successful-password",
                RememberUsername = true,
                RememberPassword = true
            };
            var loginSucceeded = false;
            viewModel.LoginSucceeded += () => loginSucceeded = true;

            await viewModel.LoginCommand.ExecuteAsync(null);

            Assert.True(loginSucceeded);
            Assert.True(session.IsLoggedIn);
            Assert.False(session.IsOfflineMode);
            Assert.Equal(user.Username, session.User?.Username);
            Assert.NotNull(await local.GetStoredSyncCredentialAsync(user.OfficeCode));

            await using var verificationDb = CreateDbContext(tempRoot);
            Assert.False(await verificationDb.Settings
                .AsNoTracking()
                .AnyAsync(setting => setting.Key == "Unrelated.Added"));
            Assert.Equal(
                "persisted-value",
                await verificationDb.Settings
                    .AsNoTracking()
                    .Where(setting => setting.Key == "Unrelated.Modified")
                    .Select(setting => setting.Value)
                    .SingleAsync());
            Assert.Equal(
                "1",
                await verificationDb.Settings
                    .AsNoTracking()
                    .Where(setting => setting.Key == "Login.RememberUsername")
                    .Select(setting => setting.Value)
                    .SingleAsync());
            Assert.Equal(
                user.Username,
                await verificationDb.Settings
                    .AsNoTracking()
                    .Where(setting => setting.Key == "Login.SavedUsername")
                    .Select(setting => setting.Value)
                    .SingleAsync());
            Assert.Contains(
                db.ChangeTracker.Entries<LocalSetting>(),
                entry => entry.Entity.Key == "Unrelated.Added"
                         && entry.State == EntityState.Added);
            Assert.Contains(
                db.ChangeTracker.Entries<LocalSetting>(),
                entry => entry.Entity.Key == "Unrelated.Modified"
                         && entry.State == EntityState.Modified);
        }
        finally
        {
            await CleanupAppRootAsync(tempRoot);
            AppRootLock.Release();
        }
    }

    private static LocalStateService CreateLocalStateService(LocalDbContext db, SessionState session)
        => new(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

    private static LocalDbContext CreateDbContext(string tempRoot)
    {
        Directory.CreateDirectory(tempRoot);
        var dbPath = Path.Combine(tempRoot, "login-fallback-test.db");
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        return new LocalDbContext(options);
    }

    private static string PrepareAppRoot(string prefix)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        return tempRoot;
    }

    private static async Task CleanupAppRootAsync(string tempRoot)
    {
        SqliteConnection.ClearAllPools();

        if (!Directory.Exists(tempRoot))
            return;

        try
        {
            await Task.Run(() => Directory.Delete(tempRoot, recursive: true));
        }
        catch
        {
            // best-effort cleanup; SQLite can keep handles open briefly on some runners
        }
    }

    private sealed class StaticLoginResponseHandler(HttpStatusCode statusCode, string responseBody = "test login failure") : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody)
            });
    }

    private sealed class SocketFailureLoginHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(
                new InvalidOperationException(
                    "transport failed",
                    new SocketException((int)SocketError.NetworkUnreachable)));
    }

    private sealed class SuccessfulLoginHandler(UserSessionDto user) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath != "/auth/login")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new LoginResponse
                {
                    Token = "successful-token",
                    ExpiresAtUtc = DateTime.UtcNow.AddDays(30),
                    User = user
                })
            });
        }
    }
}
