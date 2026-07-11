using System.Net;
using System.Runtime.ExceptionServices;
using System.Reflection;
using System.Text;
using System.Windows;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class SyncUserFacingStatusTests
{
    [Fact]
    public void SyncAttentionStatus_HidesInternalEntityIdsAndEnglishReasons()
    {
        const string entityId = "21cf7a32-a3aa-478a-84df-2eb4377e5934";
        var status = BuildStatus($"동기화 충돌 3건: Invoice {entityId} - Referenced customer was not found: 37f5e319-1859-43ba-a031-8c3ea5d2637a");

        Assert.StartsWith("동기화 확인 필요", status, StringComparison.Ordinal);
        Assert.Contains("거래처", status, StringComparison.Ordinal);
        Assert.Contains("동기화 진단", status, StringComparison.Ordinal);
        Assert.DoesNotContain(entityId, status, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Referenced customer", status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SyncAttentionStatus_ExplainsPermissionScopeWithoutRawHttpMessage()
    {
        var status = BuildStatus("동기화 업로드 실패: 403 Forbidden 현재 계정 권한으로 환경설정/분류 반영이 허용되지 않습니다.");

        Assert.Contains("계정 권한", status, StringComparison.Ordinal);
        Assert.Contains("담당지점", status, StringComparison.Ordinal);
        Assert.DoesNotContain("403", status, StringComparison.Ordinal);
        Assert.DoesNotContain("Forbidden", status, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, true)]
    [InlineData(HttpStatusCode.NotFound, true)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    public void EditSessionMonitor_SuppressesOnlyUnavailableSubjectStatuses(HttpStatusCode statusCode, bool expected)
    {
        var method = typeof(EntityEditSessionMonitor).GetMethod(
            "IsUnavailableEditSessionStatus",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var exception = new HttpRequestException("edit session", null, statusCode);
        Assert.Equal(expected, method!.Invoke(null, [exception]));
    }

    [Fact]
    public void EditSessionMonitor_ContinuesHeartbeatAttemptsForSuppressedUnavailableSubjects_AndClearsSuppressionAfterSuccess()
    {
        RunOnStaThread(() =>
        {
            var session = CreateLoggedInSession();
            var handler = new RecordingEditSessionHandler(
                () => CreateResponse(HttpStatusCode.NotFound),
                () => CreateResponse(HttpStatusCode.Forbidden),
                () => CreateResponse(HttpStatusCode.OK, "{\"otherEditors\":[]}"));
            using var httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri("https://example.test/")
            };

            var monitor = CreateMonitor(
                new Window { Title = "거래처 편집" },
                new ErpApiClient(httpClient, session),
                session,
                "거래처",
                () => new EditSessionSubject("Customer", "customer-001", "거래처 A"));

            InvokeSendHeartbeatAsync(monitor).GetAwaiter().GetResult();
            Assert.Equal(1, handler.HeartbeatRequestCount);
            Assert.Equal("Customer|customer-001", GetSuppressedSubjectKey(monitor));
            Assert.NotEqual(default, GetSuppressedUntilUtc(monitor));

            InvokeSendHeartbeatAsync(monitor).GetAwaiter().GetResult();
            Assert.Equal(2, handler.HeartbeatRequestCount);
            Assert.Equal("Customer|customer-001", GetSuppressedSubjectKey(monitor));

            InvokeSendHeartbeatAsync(monitor).GetAwaiter().GetResult();
            Assert.Equal(3, handler.HeartbeatRequestCount);
            Assert.Equal(string.Empty, GetSuppressedSubjectKey(monitor));
            Assert.Equal(default, GetSuppressedUntilUtc(monitor));
        });
    }

    [Fact]
    public void EditSessionMonitor_UsesNonBlockingDispatcherRestore()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Desktop",
            "거래플랜.Desktop.App",
            "Services",
            "EntityEditSessionMonitor.cs"));

        Assert.Contains("Dispatcher.BeginInvoke((Action)RestoreWindowTitle", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Dispatcher.Invoke(RestoreWindowTitle)", source, StringComparison.Ordinal);
        Assert.Contains("await ForgetRegisteredSessionAfterUnavailableSubjectAsync(ct);", source, StringComparison.Ordinal);
        Assert.Contains("if (!ShouldSuppressUnavailableSubjectNoise(subjectKey))", source, StringComparison.Ordinal);
        Assert.Contains("SuppressUnavailableSubjectNoise(subjectKey);", source, StringComparison.Ordinal);
        Assert.Contains("ClearUnavailableSubjectSuppression();", source, StringComparison.Ordinal);
        Assert.Contains("이전 편집 세션 정리 실패(자동 만료 대기)", source, StringComparison.Ordinal);

        var heartbeatCallIndex = source.IndexOf(
            "response = await _api.HeartbeatEditSessionAsync",
            StringComparison.Ordinal);
        var suppressionNoiseCheckIndex = source.IndexOf(
            "if (!ShouldSuppressUnavailableSubjectNoise(subjectKey))",
            StringComparison.Ordinal);

        Assert.True(heartbeatCallIndex >= 0, "heartbeat 호출 소스 가드를 찾지 못했습니다.");
        Assert.True(
            suppressionNoiseCheckIndex > heartbeatCallIndex,
            "403/404 suppression은 heartbeat 호출 이후에만 동작해야 합니다.");
    }

    private static string BuildStatus(string detail)
    {
        var method = typeof(SyncService).GetMethod(
            "BuildUserFacingSyncAttentionStatus",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsType<string>(method!.Invoke(null, [detail]));
    }

    private static SessionState CreateLoggedInSession()
    {
        var session = new SessionState();
        session.SetSession(
            "test-token",
            new UserSessionDto
            {
                Username = "tester",
                Role = "User",
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeOfficeOnly
            },
            DateTime.UtcNow.AddHours(1));
        return session;
    }

    private static EntityEditSessionMonitor CreateMonitor(
        Window owner,
        ErpApiClient api,
        SessionState session,
        string screenName,
        Func<EditSessionSubject?> subjectAccessor)
    {
        var constructor = typeof(EntityEditSessionMonitor).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(Window), typeof(ErpApiClient), typeof(SessionState), typeof(string), typeof(Func<EditSessionSubject?>)],
            modifiers: null);
        Assert.NotNull(constructor);

        return Assert.IsType<EntityEditSessionMonitor>(constructor!.Invoke([owner, api, session, screenName, subjectAccessor]));
    }

    private static Task InvokeSendHeartbeatAsync(EntityEditSessionMonitor monitor)
    {
        var method = typeof(EntityEditSessionMonitor).GetMethod(
            "SendHeartbeatAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        return Assert.IsAssignableFrom<Task>(method!.Invoke(monitor, [CancellationToken.None]));
    }

    private static string GetSuppressedSubjectKey(EntityEditSessionMonitor monitor)
        => Assert.IsType<string>(GetMonitorField(monitor, "_suppressedSubjectKey"));

    private static DateTime GetSuppressedUntilUtc(EntityEditSessionMonitor monitor)
        => Assert.IsType<DateTime>(GetMonitorField(monitor, "_suppressedUntilUtc"));

    private static object? GetMonitorField(EntityEditSessionMonitor monitor, string fieldName)
    {
        var field = typeof(EntityEditSessionMonitor).GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field!.GetValue(monitor);
    }

    private static HttpResponseMessage CreateResponse(HttpStatusCode statusCode, string json = "{}")
        => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static void RunOnStaThread(Action action)
    {
        Exception? captured = null;
        var completed = false;
        var thread = new Thread(() =>
        {
            try
            {
                action();
                completed = true;
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "STA WPF test timed out.");
        if (captured is not null)
            ExceptionDispatchInfo.Capture(captured).Throw();
        Assert.True(completed);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "Desktop")) &&
                Directory.Exists(Path.Combine(current.FullName, "Tests")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("거래플랜 저장소 루트를 찾지 못했습니다.");
    }

    private sealed class RecordingEditSessionHandler(params Func<HttpResponseMessage>[] responseFactories) : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responseFactories = new(responseFactories);

        public int HeartbeatRequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var absolutePath = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (absolutePath.EndsWith("/runtime/edit-sessions/heartbeat", StringComparison.OrdinalIgnoreCase))
                HeartbeatRequestCount++;

            if (_responseFactories.Count == 0)
                throw new InvalidOperationException($"테스트 응답이 부족합니다: {absolutePath}");

            return Task.FromResult(_responseFactories.Dequeue()());
        }
    }
}
