using System.Net;
using System.Net.Http.Json;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class ErpApiClientSessionRefreshTests
{
    [Fact]
    public async Task GetSyncStatusAsync_RefreshesSessionBeforeExpiringToken()
    {
        var user = CreateAdminUser();
        var session = new SessionState();
        session.SetSession("old-token", user, DateTime.UtcNow.AddMinutes(10));

        var handler = new RefreshingSyncStatusHandler(user);
        var api = new ErpApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost/")
        }, session);

        var status = await api.GetSyncStatusAsync();

        Assert.NotNull(status);
        Assert.Collection(
            handler.Requests,
            request =>
            {
                Assert.Equal("/auth/refresh", request.Path);
                Assert.Equal("old-token", request.BearerToken);
            },
            request =>
            {
                Assert.Equal("/sync/status", request.Path);
                Assert.Equal("new-token", request.BearerToken);
            });
        Assert.Equal("new-token", session.Token);
        Assert.True(session.TokenExpiresAtUtc > DateTime.UtcNow.AddDays(29));
    }

    [Fact]
    public async Task GetSyncStatusAsync_RetriesWithRefreshedSessionAfterUnauthorized()
    {
        var user = CreateAdminUser();
        var session = new SessionState();
        session.SetSession("old-token", user, DateTime.UtcNow.AddDays(1));

        var handler = new RefreshingSyncStatusHandler(user, failFirstStatusWithUnauthorized: true);
        var api = new ErpApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost/")
        }, session);

        var status = await api.GetSyncStatusAsync();

        Assert.NotNull(status);
        Assert.Collection(
            handler.Requests,
            request =>
            {
                Assert.Equal("/sync/status", request.Path);
                Assert.Equal("old-token", request.BearerToken);
            },
            request =>
            {
                Assert.Equal("/auth/refresh", request.Path);
                Assert.Equal("old-token", request.BearerToken);
            },
            request =>
            {
                Assert.Equal("/sync/status", request.Path);
                Assert.Equal("new-token", request.BearerToken);
            });
        Assert.Equal("new-token", session.Token);
    }

    private static UserSessionDto CreateAdminUser() => new()
    {
        UserId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        Username = "session-refresh-user",
        Role = "Admin",
        TenantCode = TenantScopeCatalog.UsenetGroup,
        OfficeCode = OfficeCodeCatalog.Usenet,
        ScopeType = TenantScopeCatalog.ScopeAdmin
    };

    private sealed class RefreshingSyncStatusHandler : HttpMessageHandler
    {
        private readonly UserSessionDto _user;
        private readonly bool _failFirstStatusWithUnauthorized;
        private int _syncStatusRequestCount;

        public RefreshingSyncStatusHandler(UserSessionDto user, bool failFirstStatusWithUnauthorized = false)
        {
            _user = user;
            _failFirstStatusWithUnauthorized = failFirstStatusWithUnauthorized;
        }

        public List<RequestSnapshot> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            Requests.Add(new RequestSnapshot(path, request.Headers.Authorization?.Parameter));

            if (path == "/auth/refresh")
            {
                return Task.FromResult(Json(new LoginResponse
                {
                    Token = "new-token",
                    ExpiresAtUtc = DateTime.UtcNow.AddDays(30),
                    User = _user
                }));
            }

            if (path == "/sync/status")
            {
                _syncStatusRequestCount++;
                if (_failFirstStatusWithUnauthorized && _syncStatusRequestCount == 1)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));

                return Task.FromResult(Json(new SyncStatusDto
                {
                    CurrentServerRevision = _syncStatusRequestCount,
                    ServerUtc = DateTime.UtcNow
                }));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json<T>(T payload) => new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(payload)
        };
    }

    public sealed record RequestSnapshot(string Path, string? BearerToken);
}
