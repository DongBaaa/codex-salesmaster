using System.Net;
using System.Net.Http;
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

            await viewModel.LoginCommand.ExecuteAsync(null);

            Assert.True(viewModel.ShowOfflineButton);
            Assert.Contains("서버에 연결할 수 없습니다", viewModel.ErrorMessage, StringComparison.Ordinal);
            Assert.False(session.IsLoggedIn);
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
}
