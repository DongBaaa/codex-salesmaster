using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class EmptyTenantStartupTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public async Task SuccessfulFullPull_EmptyTenantCompletesInitialLoad(long revision)
    {
        await using var fixture = await Fixture.CreateAsync(revision);
        Assert.True(await fixture.ViewModel.IsInitialServerDataLoadRequiredAsync());

        await fixture.ViewModel.RunPostLoginSyncAsync();

        Assert.True(fixture.Handler.FullPulls > 0);
        Assert.False(await fixture.Local.HasVisiblePrimaryWorkCacheAsync(fixture.Session));
        Assert.False(await fixture.ViewModel.IsInitialServerDataLoadRequiredAsync());
        Assert.Contains("초기 데이터 동기화 완료", fixture.ViewModel.SyncStatus);
        Assert.Equal(0, await fixture.Local.CountDirtyAsync());
        var pullCount = fixture.Handler.FullPulls;
        await fixture.ViewModel.RunPostLoginSyncAsync();
        Assert.Equal(pullCount, fixture.Handler.FullPulls);
    }

    [Theory]
    [InlineData("login")]
    [InlineData("office")]
    [InlineData("permission")]
    [InlineData("database")]
    public async Task ChangedOwnerCannotReuseEmptyResult(string change)
    {
        await using var f = await Fixture.CreateAsync(100);
        if (change == "database")
        {
            var administrator = CopyUser(f.Session.User!);
            administrator.Role = DomainConstants.RoleAdmin;
            administrator.ScopeType = TenantScopeCatalog.ScopeAdmin;
            f.Session.SetSession("test-global-admin", administrator, DateTime.UtcNow.AddHours(1));
        }
        await f.ViewModel.RunPostLoginSyncAsync();
        Assert.False(await f.ViewModel.IsInitialServerDataLoadRequiredAsync());
        var user = CopyUser(f.Session.User!);
        switch (change)
        {
            case "login": f.Session.SetSession("new-test-login", user); break;
            case "office":
                user.OfficeCode = OfficeCodeCatalog.Yeonsu;
                user.TenantCode = TenantScopeCatalog.UsenetGroup;
                f.Session.RefreshSession("new-test-scope", user);
                break;
            case "permission":
                user.ScopeType = TenantScopeCatalog.ScopeTenantAll;
                f.Session.RefreshSession("new-test-permission", user);
                break;
            case "database":
                f.Session.SetBusinessDatabase("georaeplan_usenet");
                break;
        }
        Assert.True(await f.ViewModel.IsInitialServerDataLoadRequiredAsync());
    }

    [Fact]
    public async Task TokenRefreshWithSameOwnerKeepsVerifiedEmptyResult()
    {
        await using var f = await Fixture.CreateAsync(0);
        await f.ViewModel.RunPostLoginSyncAsync();
        f.Session.RefreshSession("renewed-test-token", CopyUser(f.Session.User!), DateTime.UtcNow.AddHours(1));
        Assert.False(await f.ViewModel.IsInitialServerDataLoadRequiredAsync());
    }

    [Fact]
    public async Task NewRefreshRequestInvalidatesReceiptEvenAfterFlagCleared()
    {
        await using var f = await Fixture.CreateAsync(100);
        await f.ViewModel.RunPostLoginSyncAsync();
        await f.Local.MarkServerMirrorRefreshRequiredAsync();
        Assert.True(await f.ViewModel.IsInitialServerDataLoadRequiredAsync());
        await f.Local.ClearServerMirrorRefreshRequiredAsync();
        Assert.True(await f.ViewModel.IsInitialServerDataLoadRequiredAsync());
    }

    [Fact]
    public async Task GlobalRevisionAndForeignCacheDoNotProveCurrentScopeLoaded()
    {
        await using var f = await Fixture.CreateAsync(100);
        var foreign = new LocalCustomer
        {
            Id = Guid.NewGuid(), TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Preserved foreign fixture", NameMatchKey = "FOREIGN", TradeType = "매출",
            Revision = 90, IsDirty = false
        };
        f.Db.Customers.Add(foreign);
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();
        await f.Local.SetSettingAsync("LastSyncRevision", "100");
        Assert.True(await f.ViewModel.IsInitialServerDataLoadRequiredAsync());
        await f.ViewModel.RunPostLoginSyncAsync();
        Assert.False(await f.ViewModel.IsInitialServerDataLoadRequiredAsync());
        var preserved = await f.Db.Customers.AsNoTracking().SingleAsync();
        Assert.Equal(foreign.Id, preserved.Id);
        Assert.Equal(foreign.NameOriginal, preserved.NameOriginal);
        Assert.Equal(90, preserved.Revision);
        Assert.False(preserved.IsDirty);
    }

    [Fact]
    public async Task FailedFullPullCannotCompleteEmptyScopeAfterIncrementalSync()
    {
        await using var f = await Fixture.CreateAsync(100);
        await f.Local.SetSettingAsync("LastSyncRevision", "90");
        f.Handler.FailFullPull = true;
        await f.ViewModel.RunPostLoginSyncAsync();
        Assert.True(f.Handler.FullPulls > 0);
        Assert.True(await f.ViewModel.IsInitialServerDataLoadRequiredAsync());
        Assert.DoesNotContain("동기화 완료", f.ViewModel.SyncStatus);
    }

    [Fact]
    public async Task DisabledServerSyncCannotCertifyAnEmptyScope()
    {
        await using var f = await Fixture.CreateAsync(100);
        Environment.SetEnvironmentVariable("GEORAEPLAN_DISABLE_SERVER_SYNC", "1");
        await f.ViewModel.RunPostLoginSyncAsync();
        Assert.Equal(0, f.Handler.FullPulls);
        Assert.True(await f.ViewModel.IsInitialServerDataLoadRequiredAsync());
    }

    [Fact]
    public async Task CanceledFullPullCannotCertifyAnEmptyScope()
    {
        await using var f = await Fixture.CreateAsync(100);
        using var cts = new CancellationTokenSource();
        f.Handler.BeforeFullPull = () => cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.ViewModel.RunPostLoginSyncAsync(cts.Token));
        Assert.True(await f.ViewModel.IsInitialServerDataLoadRequiredAsync());
    }

    [Fact]
    public async Task ScopeChangeDuringFullPullDoesNotCertifyReplacementOwner()
    {
        await using var f = await Fixture.CreateAsync(100);
        f.Handler.BeforeFullPull = () =>
        {
            f.Handler.BeforeFullPull = null;
            var user = CopyUser(f.Session.User!);
            user.ScopeType = TenantScopeCatalog.ScopeTenantAll;
            f.Session.RefreshSession("changed-scope-token", user);
        };
        await f.ViewModel.RunPostLoginSyncAsync();
        Assert.True(await f.ViewModel.IsInitialServerDataLoadRequiredAsync());
    }

    [Fact]
    public async Task LeavingAndReturningToSameScopeRequiresFreshConfirmation()
    {
        await using var f = await Fixture.CreateAsync(100);
        await f.ViewModel.RunPostLoginSyncAsync();
        var original = CopyUser(f.Session.User!);
        var changed = CopyUser(original);
        changed.ScopeType = TenantScopeCatalog.ScopeTenantAll;
        f.Session.RefreshSession("test-other-scope", changed);
        f.Session.RefreshSession("test-original-scope", original);
        Assert.True(await f.ViewModel.IsInitialServerDataLoadRequiredAsync());
    }

    [Fact]
    public async Task CorruptedCacheStillRequiresRepairAfterSuccessfulEmptyPull()
    {
        await using var f = await Fixture.CreateAsync(100);
        await f.ViewModel.RunPostLoginSyncAsync();
        f.Db.Customers.Add(new LocalCustomer
        {
            Id = Guid.NewGuid(), TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld, ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
            NameOriginal = "", NameMatchKey = "CORRUPT", TradeType = "매출", IsDirty = false
        });
        await f.Db.SaveChangesAsync();
        Assert.True(await f.ViewModel.IsInitialServerDataLoadRequiredAsync());
        Assert.True(await f.Local.IsServerMirrorRefreshRequiredAsync());
    }

    private static UserSessionDto CopyUser(UserSessionDto user) => new()
    {
        UserId = user.UserId, Username = user.Username, Role = user.Role,
        TenantCode = user.TenantCode, OfficeCode = user.OfficeCode, ScopeType = user.ScopeType
    };

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string? _previousRoot = Environment.GetEnvironmentVariable("GEORAEPLAN_APP_ROOT");
        private readonly string? _previousDisabled = Environment.GetEnvironmentVariable("GEORAEPLAN_DISABLE_SERVER_SYNC");
        internal LocalDbContext Db = null!;
        internal SessionState Session = null!;
        internal LocalStateService Local = null!;
        internal MainViewModel ViewModel = null!;
        internal EmptyServerHandler Handler = null!;
        private SyncService _sync = null!;
        private HttpClient _http = null!;

        internal static async Task<Fixture> CreateAsync(long revision)
        {
            var f = new Fixture();
            var root = Path.Combine(Path.GetTempPath(), "georaeplan-empty-startup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", root);
            Environment.SetEnvironmentVariable("GEORAEPLAN_DISABLE_SERVER_SYNC", null);
            // AppPaths caches its first root for the process; each case needs its own database.
            var options = new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "fixture.db") }.ToString())
                .Options;
            f.Db = new LocalDbContext(options);
            await f.Db.Database.EnsureCreatedAsync();
            f.Session = new SessionState();
            f.Session.SetSession("isolated-test-token", new UserSessionDto
            {
                UserId = Guid.NewGuid(), Username = "empty-office", Role = DomainConstants.RoleUser,
                TenantCode = TenantScopeCatalog.Itworld, OfficeCode = OfficeCodeCatalog.Itworld,
                ScopeType = TenantScopeCatalog.ScopeOfficeOnly
            }, DateTime.UtcNow.AddHours(1));
            var dispatcher = new SyncRequestDispatcher();
            f.Local = new LocalStateService(f.Db, new OfficeAccessService(), dispatcher, f.Session);
            var rental = new RentalStateService(f.Db, f.Local);
            var diagnostics = new SyncDiagnosticsService(f.Session, () => new LocalDbContext(options));
            f.Handler = new EmptyServerHandler(revision);
            f._http = new HttpClient(f.Handler) { BaseAddress = new Uri("http://localhost/") };
            var api = new ErpApiClient(f._http, f.Session);
            f._sync = new SyncService(f.Db, f.Local, rental, api, f.Session, dispatcher, diagnostics);
            f.ViewModel = new MainViewModel(f.Local, f._sync, new BackupService(), rental, diagnostics, api, f.Session);
            return f;
        }

        public async ValueTask DisposeAsync()
        {
            ViewModel.CancelPendingBackgroundWorkForShutdown();
            await ViewModel.DrainPendingBackgroundWorkForShutdownAsync();
            await _sync.StopAndDrainAsync();
            _sync.Dispose();
            _http.Dispose();
            await Db.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", _previousRoot);
            Environment.SetEnvironmentVariable("GEORAEPLAN_DISABLE_SERVER_SYNC", _previousDisabled);
        }
    }

    private sealed class EmptyServerHandler(long revision) : HttpMessageHandler
    {
        internal int FullPulls;
        internal bool FailFullPull;
        internal Action? BeforeFullPull;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/sync/pull")
            {
                if (request.RequestUri.Query.Contains("sinceRev=0&", StringComparison.Ordinal))
                {
                    FullPulls++;
                    BeforeFullPull?.Invoke();
                    cancellationToken.ThrowIfCancellationRequested();
                    if (FailFullPull)
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new SyncPullResponse { CurrentServerRevision = revision })
                });
            }
            if (path == "/sync/push")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new SyncPushResult { CurrentServerRevision = revision })
                });
            if (path == "/sync/status")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new SyncStatusDto { CurrentServerRevision = revision })
                });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
