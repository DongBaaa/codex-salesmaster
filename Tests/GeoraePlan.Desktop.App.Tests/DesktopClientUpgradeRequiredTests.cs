using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class DesktopClientUpgradeRequiredTests
{
    [Fact]
    public async Task Mutation426_ThrowsTypedSanitizedExceptionWithoutRetryWrapper()
    {
        const string body =
            """
            {
              "error": "attacker-controlled",
              "message": "do-not-display",
              "upgrade": " georaeplan-client\r\nbad ",
              "client": {
                "appId": "kr.georaeplan.desktop<script>",
                "platform": "windows",
                "version": "1.1.689",
                "build": 689,
                "protocolVersion": 1
              },
              "required": {
                "policyVersion": 7,
                "requiresUserAction": false,
                "minimumVersion": "1.1.690",
                "minimumBuild": 690,
                "minimumProtocolVersion": 2,
                "latestVersion": "1.1.691",
                "latestBuild": 691,
                "updateUrl": "javascript:alert(1)"
              }
            }
            """;
        var handler = new UpgradeRequiredHandler(body);
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost/")
        };
        var api = new ErpApiClient(http, new SessionState());

        var exception =
            await Assert.ThrowsAsync<DesktopClientUpgradeRequiredException>(
                () => api.CreateUserAsync(new CreateUserRequest
                {
                    Username = "new-user",
                    Password = "not-a-real-secret"
                }));

        Assert.Equal(HttpStatusCode.UpgradeRequired, exception.StatusCode);
        Assert.Equal("/users", exception.RequestPath);
        Assert.Equal("client_upgrade_required", exception.Response.Error);
        Assert.Equal("georaeplan-client", exception.Response.Upgrade);
        Assert.Equal(string.Empty, exception.Response.Client.AppId);
        Assert.True(exception.Response.Required.RequiresUserAction);
        Assert.Equal(7, exception.Response.Required.PolicyVersion);
        Assert.Equal("1.1.690", exception.Response.Required.MinimumVersion);
        Assert.Equal(690, exception.Response.Required.MinimumBuild);
        Assert.Equal(2, exception.Response.Required.MinimumProtocolVersion);
        Assert.Equal(string.Empty, exception.Response.Required.UpdateUrl);
        Assert.DoesNotContain(
            "do-not-display",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Login426_PreservesTypedRequirementWithoutRetryWrapper()
    {
        var handler = new UpgradeRequiredHandler("{}");
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost/")
        };
        var api = new ErpApiClient(http, new SessionState());

        var exception =
            await Assert.ThrowsAsync<DesktopClientUpgradeRequiredException>(
                () => api.LoginAsync(
                    "desktop-user",
                    "not-a-real-secret"));

        Assert.Equal("/auth/login", exception.RequestPath);
        Assert.True(exception.Response.Required.RequiresUserAction);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Refresh426_PreservesTypedRequirementWithoutRetryWrapper()
    {
        var handler = new UpgradeRequiredHandler("{}");
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost/")
        };
        var session = new SessionState();
        session.SetSession(
            "old-token",
            new UserSessionDto
            {
                UserId = Guid.Parse(
                    "9f003bb5-a84d-4579-a457-f65c8a8eaf8d"),
                Username = "desktop-user",
                Role = "Admin",
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeAdmin
            },
            DateTime.UtcNow.AddDays(1));
        var api = new ErpApiClient(http, session);

        var exception =
            await Assert.ThrowsAsync<DesktopClientUpgradeRequiredException>(
                () => api.RefreshSessionAsync());

        Assert.Equal("/auth/refresh", exception.RequestPath);
        Assert.True(exception.Response.Required.RequiresUserAction);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Oversized426Body_FailsClosedToTypedOpaqueRequirement()
    {
        var oversized = new string('x', (64 * 1024) + 1);
        using var content =
            new StringContent(oversized, Encoding.UTF8, "application/json");

        var parsed =
            await DesktopUpgradeRequiredResponseParser
                .ParseOrFallbackAsync(content);

        Assert.Equal("client_upgrade_required", parsed.Error);
        Assert.True(parsed.Required.RequiresUserAction);
        Assert.Equal(0, parsed.Required.PolicyVersion);
        Assert.Equal(string.Empty, parsed.Required.MinimumVersion);
        Assert.Null(parsed.Required.MinimumBuild);
        Assert.Null(parsed.Required.MinimumProtocolVersion);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{ malformed")]
    public async Task Invalid426Body_StillProducesTypedOpaqueRequirement(
        string body)
    {
        using var content =
            new StringContent(body, Encoding.UTF8, "application/json");

        var parsed =
            await DesktopUpgradeRequiredResponseParser
                .ParseOrFallbackAsync(content);

        Assert.Equal("client_upgrade_required", parsed.Error);
        Assert.True(parsed.Required.RequiresUserAction);
        Assert.Equal(0, parsed.Required.PolicyVersion);
        Assert.Equal(string.Empty, parsed.Required.UpdateUrl);
    }

    [Fact]
    public async Task CancellationAfter426Headers_StillProducesTypedOpaqueRequirement()
    {
        using var content = new CancellationAwareContent();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception =
            await DesktopUpgradeRequiredResponseParser
                .CreateExceptionAsync(
                    "/sync/push",
                    content,
                    cancellation.Token);

        Assert.Equal(HttpStatusCode.UpgradeRequired, exception.StatusCode);
        Assert.True(exception.Response.Required.RequiresUserAction);
        Assert.Equal(0, exception.Response.Required.PolicyVersion);
    }

    [Fact]
    public async Task HandlerBoundsChunked426BeforeHttpClientResponseBuffering()
    {
        var upgradeHandler = new DesktopUpgradeRequiredHandler
        {
            InnerHandler = new ChunkedUpgradeRequiredHandler()
        };
        using var http = new HttpClient(upgradeHandler)
        {
            BaseAddress = new Uri("http://localhost/")
        };

        var exception =
            await Assert.ThrowsAsync<DesktopClientUpgradeRequiredException>(
                () => http.GetAsync("sync/push"));

        Assert.Equal(HttpStatusCode.UpgradeRequired, exception.StatusCode);
        Assert.True(exception.Response.Required.RequiresUserAction);
        Assert.Equal(0, exception.Response.Required.PolicyVersion);
    }

    [Fact]
    public async Task OfficeSessionClient_UsesBoundedUpgradeHandler()
    {
        var chunkedHandler = new ChunkedUpgradeRequiredHandler();
        var clientFactory = new StubHttpClientFactory(() =>
            new HttpClient(
                new DesktopUpgradeRequiredHandler
                {
                    InnerHandler = chunkedHandler
                },
                disposeHandler: true));
        var options =
            new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite("Data Source=:memory:")
                .Options;
        await using var db = new LocalDbContext(options);
        var session = new SessionState();
        var dispatcher = new SyncRequestDispatcher();
        var local =
            new LocalStateService(
                db,
                new OfficeAccessService(),
                dispatcher,
                session);
        var rental = new RentalStateService(db, local);
        var diagnostics = new SyncDiagnosticsService(session);
        using var rootHttp = new HttpClient(
            new UpgradeRequiredHandler("{}"))
        {
            BaseAddress = new Uri("http://localhost/")
        };
        var rootApi = new ErpApiClient(rootHttp, session);
        using var sync = new SyncService(
            db,
            local,
            rental,
            rootApi,
            session,
            dispatcher,
            diagnostics,
            httpClientFactory: clientFactory);
        using var officeHttp = sync.CreateOfficeSessionHttpClient();
        var officeApi =
            new ErpApiClient(
                officeHttp,
                new SessionState());

        var exception =
            await Assert.ThrowsAsync<DesktopClientUpgradeRequiredException>(
                () => officeApi.CreateUserAsync(
                    new CreateUserRequest
                    {
                        Username = "office-user",
                        Password = "not-a-real-secret"
                    }));

        Assert.Equal("/users", exception.RequestPath);
        Assert.True(exception.Response.Required.RequiresUserAction);
        Assert.Equal(
            SyncService.OfficeSessionHttpClientName,
            Assert.Single(clientFactory.RequestedNames));
    }

    [Fact]
    public async Task AppRegistration_OfficeSessionClientUsesUpgradeHandler()
    {
        var repositoryRoot = FindRepositoryRoot();
        var source =
            await File.ReadAllTextAsync(
                Path.Combine(
                    repositoryRoot,
                    "Desktop",
                    "거래플랜.Desktop.App",
                    "App.xaml.cs"));
        var nameIndex = source.IndexOf(
            "SyncService.OfficeSessionHttpClientName",
            StringComparison.Ordinal);
        Assert.True(nameIndex >= 0);
        var registrationStart = source.LastIndexOf(
            "services.AddHttpClient(",
            nameIndex,
            StringComparison.Ordinal);
        var registrationEnd = source.IndexOf(
            "services.AddSingleton<SessionState>()",
            nameIndex,
            StringComparison.Ordinal);
        Assert.True(registrationStart >= 0);
        Assert.True(registrationEnd > registrationStart);

        var registration =
            source[registrationStart..registrationEnd];

        Assert.Contains(
            ".AddHttpMessageHandler<DesktopUpgradeRequiredHandler>()",
            registration,
            StringComparison.Ordinal);
    }

    private sealed class UpgradeRequiredHandler(string body)
        : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.UpgradeRequired)
                {
                    RequestMessage = request,
                    Content = new StringContent(
                        body,
                        Encoding.UTF8,
                        "application/json")
                });
        }
    }

    private sealed class ChunkedUpgradeRequiredHandler
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.UpgradeRequired)
                {
                    RequestMessage = request,
                    Content = new StreamContent(
                        new NonSeekableReadStream(
                            new byte[(64 * 1024) + 1]))
                });
    }

    private sealed class StubHttpClientFactory(
        Func<HttpClient> createClient)
        : IHttpClientFactory
    {
        public List<string> RequestedNames { get; } = [];

        public HttpClient CreateClient(string name)
        {
            RequestedNames.Add(name);
            return createClient();
        }
    }

    private static string FindRepositoryRoot(
        [CallerFilePath] string sourceFilePath = "")
    {
        var directory =
            new DirectoryInfo(
                Path.GetDirectoryName(sourceFilePath) ??
                AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(
                    Path.Combine(
                        directory.FullName,
                        "거래플랜.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "거래플랜 저장소 루트를 찾지 못했습니다.");
    }

    private sealed class CancellationAwareContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context)
            => Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
            => Task.FromResult<Stream>(
                new CancellationAwareStream());
    }

    private sealed class CancellationAwareStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length =>
            throw new NotSupportedException();
        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override int Read(
            byte[] buffer,
            int offset,
            int count)
            => throw new OperationCanceledException();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(
                new OperationCanceledException(cancellationToken));

        public override void Flush()
        {
        }

        public override long Seek(
            long offset,
            SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(
            byte[] buffer,
            int offset,
            int count)
            => throw new NotSupportedException();
    }

    private sealed class NonSeekableReadStream(byte[] bytes)
        : Stream
    {
        private readonly MemoryStream _inner = new(bytes, writable: false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length =>
            throw new NotSupportedException();
        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(
            byte[] buffer,
            int offset,
            int count)
            => _inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
            => _inner.ReadAsync(buffer, cancellationToken);

        public override void Flush()
        {
        }

        public override long Seek(
            long offset,
            SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(
            byte[] buffer,
            int offset,
            int count)
            => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
