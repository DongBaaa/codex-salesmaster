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

    [Fact]
    public async Task GetSyncStatusAsync_ClearsSessionWhenUnauthorizedRefreshIsRejected()
    {
        var session = new SessionState();
        session.SetSession("blocked-token", CreateAdminUser(), DateTime.UtcNow.AddDays(1));

        var handler = new RejectedRefreshHandler();
        var api = new ErpApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost/")
        }, session);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => api.GetSyncStatusAsync());

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.False(session.IsLoggedIn);
        Assert.Null(session.Token);
        Assert.Collection(
            handler.Requests,
            request =>
            {
                Assert.Equal("/sync/status", request.Path);
                Assert.Equal("blocked-token", request.BearerToken);
            },
            request =>
            {
                Assert.Equal("/auth/refresh", request.Path);
                Assert.Equal("blocked-token", request.BearerToken);
            });
    }

    [Fact]
    public async Task PushAsync_ForbiddenMessagePayload_ThrowsReadablePermissionMessageWithoutRetry()
    {
        const string permissionMessage = "현재 계정 권한으로 서버 동기화 반영이 허용되지 않는 변경이 포함되어 있습니다: 전표";
        var session = new SessionState();
        session.SetSession("forbidden-token", CreateAdminUser(), DateTime.UtcNow.AddDays(1));

        var handler = new ForbiddenPushHandler(permissionMessage);
        var api = new ErpApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost/")
        }, session);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => api.PushAsync(new SyncPushRequest
        {
            Invoices =
            [
                new InvoiceDto
                {
                    Id = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                    CustomerId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
                    CustomerName = "권한 테스트 거래처",
                    InvoiceDate = DateOnly.FromDateTime(DateTime.Today),
                    VoucherType = VoucherType.Sales
                }
            ]
        }));

        var request = Assert.Single(handler.Requests);
        Assert.Equal("/sync/push", request.Path);
        Assert.Contains(permissionMessage, exception.Message);
        Assert.DoesNotContain("{\"message\"", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\\u", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
    }

    [Fact]
    public async Task GetSyncStatusAsync_ValidationProblemPayload_ThrowsReadableValidationDetails()
    {
        var session = new SessionState();
        session.SetSession("validation-token", CreateAdminUser(), DateTime.UtcNow.AddDays(1));

        var handler = new ErrorResponseHandler(
            HttpStatusCode.BadRequest,
            new
            {
                title = "One or more validation errors occurred.",
                status = 400,
                detail = "입력값을 확인하세요.",
                errors = new Dictionary<string, string[]>
                {
                    ["InvoiceDate"] = ["날짜가 올바르지 않습니다."],
                    ["CustomerId"] = ["거래처가 필요합니다."]
                }
            });
        var api = new ErpApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost/")
        }, session);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => api.GetSyncStatusAsync());

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Contains("입력값을 확인하세요.", exception.Message);
        Assert.Contains("InvoiceDate", exception.Message);
        Assert.Contains("날짜가 올바르지 않습니다.", exception.Message);
        Assert.DoesNotContain("{\"title\"", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\\u", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetSyncStatusAsync_EmptyForbiddenPayload_ThrowsReadablePermissionFallback()
    {
        var session = new SessionState();
        session.SetSession("forbidden-token", CreateAdminUser(), DateTime.UtcNow.AddDays(1));

        var handler = new ErrorResponseHandler(HttpStatusCode.Forbidden, payload: null);
        var api = new ErpApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost/")
        }, session);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => api.GetSyncStatusAsync());

        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.Contains("권한", exception.Message);
        Assert.Contains("관리자", exception.Message);
    }



    [Fact]
    public async Task GetSyncStatusAsync_ExpectedRevisionConflictPayload_ThrowsBusinessGuidanceWithoutEnglishReason()
    {
        var session = new SessionState();
        session.SetSession("conflict-token", CreateAdminUser(), DateTime.UtcNow.AddDays(1));

        var handler = new ErrorResponseHandler(
            HttpStatusCode.Conflict,
            new
            {
                entityName = "Invoice",
                entityId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                expectedRevision = 10,
                currentRevision = 12,
                reason = "A paid, rental-linked, or versioned invoice cannot be structurally changed with the same invoice id. Save it as a new invoice version."
            });
        var api = new ErpApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost/")
        }, session);

        var exception = await Assert.ThrowsAnyAsync<HttpRequestException>(() => api.GetSyncStatusAsync());

        Assert.Equal(HttpStatusCode.Conflict, exception.StatusCode);
        Assert.Contains(ApiConflictReasonTranslator.ProtectedInvoiceSameIdStructuralMutation, exception.Message);
        Assert.DoesNotContain("same invoice id", exception.Message, StringComparison.OrdinalIgnoreCase);
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

    private sealed class RejectedRefreshHandler : HttpMessageHandler
    {
        public List<RequestSnapshot> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            Requests.Add(new RequestSnapshot(path, request.Headers.Authorization?.Parameter));

            if (path is "/sync/status" or "/auth/refresh")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class ErrorResponseHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly object? _payload;

        public ErrorResponseHandler(HttpStatusCode statusCode, object? payload)
        {
            _statusCode = statusCode;
            _payload = payload;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_statusCode);
            if (_payload is not null)
                response.Content = JsonContent.Create(_payload);

            return Task.FromResult(response);
        }
    }

    private sealed class ForbiddenPushHandler : HttpMessageHandler
    {
        private readonly string _message;

        public ForbiddenPushHandler(string message)
        {
            _message = message;
        }

        public List<RequestSnapshot> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new RequestSnapshot(request.RequestUri?.AbsolutePath ?? string.Empty, request.Headers.Authorization?.Parameter));

            if (request.RequestUri?.AbsolutePath == "/sync/push")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = JsonContent.Create(new { message = _message })
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    public sealed record RequestSnapshot(string Path, string? BearerToken);
}
