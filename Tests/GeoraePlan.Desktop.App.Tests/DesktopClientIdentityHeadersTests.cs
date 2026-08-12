using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class DesktopClientIdentityHeadersTests
{
    [Fact]
    public async Task LoginAsync_SendsOneCurrentDesktopIdentityValuePerHeader()
    {
        var handler = new CompatibilityIdentityCaptureHandler();
        using var http = CreateHttpClientWithStaleIdentityValues(handler);
        var api = new ErpApiClient(http, new SessionState());

        var response = await api.LoginAsync("desktop-user", "test-password");

        Assert.NotNull(response);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/auth/login", request.Path);
        AssertCurrentDesktopIdentity(request);
    }

    [Fact]
    public async Task AuthenticatedUnauthorizedRefreshAndRetry_KeepOneDesktopIdentityValuePerRequest()
    {
        var user = CreateUser();
        var session = new SessionState();
        session.SetSession("old-token", user, DateTime.UtcNow.AddDays(1));

        var handler = new CompatibilityIdentityCaptureHandler(
            failFirstSyncStatusWithUnauthorized: true);
        using var http = CreateHttpClientWithStaleIdentityValues(handler);
        var api = new ErpApiClient(http, session);

        var response = await api.GetSyncStatusAsync();

        Assert.NotNull(response);
        Assert.Collection(
            handler.Requests,
            request =>
            {
                Assert.Equal("/sync/status", request.Path);
                Assert.Equal("old-token", request.BearerToken);
                AssertCurrentDesktopIdentity(request);
            },
            request =>
            {
                Assert.Equal("/auth/refresh", request.Path);
                Assert.Equal("old-token", request.BearerToken);
                AssertCurrentDesktopIdentity(request);
            },
            request =>
            {
                Assert.Equal("/sync/status", request.Path);
                Assert.Equal("new-token", request.BearerToken);
                AssertCurrentDesktopIdentity(request);
            });
    }

    private static HttpClient CreateHttpClientWithStaleIdentityValues(
        HttpMessageHandler handler)
    {
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost/")
        };

        foreach (var headerName in CompatibilityHeaderNames)
        {
            Assert.True(
                http.DefaultRequestHeaders.TryAddWithoutValidation(
                    headerName,
                    new[] { "stale-one", "stale-two" }));
        }

        return http;
    }

    private static void AssertCurrentDesktopIdentity(RequestSnapshot request)
    {
        var assemblyVersion = typeof(ErpApiClient).Assembly.GetName().Version
            ?? throw new InvalidOperationException("Desktop assembly version is missing.");
        Assert.True(assemblyVersion.Build > 0);

        AssertSingleHeader(
            request,
            ClientCompatibilityHeaders.AppId,
            "kr.georaeplan.desktop");
        AssertSingleHeader(
            request,
            ClientCompatibilityHeaders.Platform,
            "windows");
        AssertSingleHeader(
            request,
            ClientCompatibilityHeaders.Version,
            assemblyVersion.ToString(3));
        AssertSingleHeader(
            request,
            ClientCompatibilityHeaders.Build,
            assemblyVersion.Build.ToString(CultureInfo.InvariantCulture));
        AssertSingleHeader(
            request,
            ClientCompatibilityHeaders.Protocol,
            ClientCompatibilityHeaders.CurrentProtocolVersion.ToString(
                CultureInfo.InvariantCulture));
    }

    private static void AssertSingleHeader(
        RequestSnapshot request,
        string headerName,
        string expected)
    {
        Assert.True(request.Headers.TryGetValue(headerName, out var values));
        Assert.Equal([expected], values);
    }

    private static UserSessionDto CreateUser() => new()
    {
        UserId = Guid.Parse("0a6c3182-d9f1-4d71-a869-2813bf3c0c61"),
        Username = "desktop-user",
        Role = "Admin",
        TenantCode = TenantScopeCatalog.UsenetGroup,
        OfficeCode = OfficeCodeCatalog.Usenet,
        ScopeType = TenantScopeCatalog.ScopeAdmin
    };

    private static readonly string[] CompatibilityHeaderNames =
    [
        ClientCompatibilityHeaders.AppId,
        ClientCompatibilityHeaders.Platform,
        ClientCompatibilityHeaders.Version,
        ClientCompatibilityHeaders.Build,
        ClientCompatibilityHeaders.Protocol
    ];

    private sealed class CompatibilityIdentityCaptureHandler(
        bool failFirstSyncStatusWithUnauthorized = false)
        : HttpMessageHandler
    {
        private int _syncStatusRequestCount;

        public List<RequestSnapshot> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RequestSnapshot(
                request.RequestUri?.AbsolutePath ?? string.Empty,
                request.Headers.Authorization?.Parameter,
                CompatibilityHeaderNames.ToDictionary(
                    static name => name,
                    name => request.Headers.TryGetValues(name, out var values)
                        ? values.ToArray()
                        : [])));

            return request.RequestUri?.AbsolutePath switch
            {
                "/auth/login" => Task.FromResult(Json(CreateLoginResponse("login-token"))),
                "/auth/refresh" => Task.FromResult(Json(CreateLoginResponse("new-token"))),
                "/sync/status" => Task.FromResult(CreateSyncStatusResponse()),
                _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))
            };
        }

        private HttpResponseMessage CreateSyncStatusResponse()
        {
            _syncStatusRequestCount++;
            if (failFirstSyncStatusWithUnauthorized &&
                _syncStatusRequestCount == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            return Json(new SyncStatusDto
            {
                CurrentServerRevision = _syncStatusRequestCount,
                ServerUtc = DateTime.UtcNow
            });
        }

        private static LoginResponse CreateLoginResponse(string token) => new()
        {
            Token = token,
            ExpiresAtUtc = DateTime.UtcNow.AddDays(30),
            User = CreateUser()
        };

        private static HttpResponseMessage Json<T>(T payload) =>
            new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(payload)
            };
    }

    private sealed record RequestSnapshot(
        string Path,
        string? BearerToken,
        IReadOnlyDictionary<string, string[]> Headers);
}
