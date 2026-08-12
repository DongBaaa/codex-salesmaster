using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class DesktopCompatibilityPersistenceTests
{
    private static readonly DesktopClientRuntimeIdentity Runtime =
        new("1.1.689", 689, 1);

    [Fact]
    public void EvidenceClassification_RequiresExactClientAndCoherentPolicy()
    {
        var verified = DesktopCompatibilityPolicy.From426(
            UpgradeException(
                appId: "kr.georaeplan.desktop",
                platform: "windows",
                policyVersion: 7),
            Runtime,
            DateTime.UnixEpoch);
        var wrongPlatform = DesktopCompatibilityPolicy.From426(
            UpgradeException(
                appId: "kr.georaeplan.desktop",
                platform: "Windows",
                policyVersion: 7),
            Runtime,
            DateTime.UnixEpoch);
        var malformed = DesktopCompatibilityPolicy.From426(
            UpgradeException(
                appId: "kr.georaeplan.desktop",
                platform: "windows",
                policyVersion: 0),
            Runtime,
            DateTime.UnixEpoch);

        Assert.Equal(
            DesktopCompatibilityEvidenceKind.Verified426,
            verified.Kind);
        Assert.Equal(
            DesktopCompatibilityEvidenceKind.Opaque426,
            wrongPlatform.Kind);
        Assert.Equal(
            DesktopCompatibilityEvidenceKind.Opaque426,
            malformed.Kind);
        Assert.DoesNotContain(
            typeof(DesktopCompatibilityEvidence)
                .GetProperties(),
            property => property.Name.Contains(
                "Url",
                StringComparison.OrdinalIgnoreCase) ||
                        property.Name.Contains(
                            "Message",
                            StringComparison.OrdinalIgnoreCase) ||
                        property.Name.Contains(
                            "Raw",
                            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Merge_IsMonotonicAndOpaqueEvidenceDominates()
    {
        var baseline = Evidence(policy: 5, minimumBuild: 690);
        var older = Evidence(policy: 4, minimumBuild: 999);
        var equalStricter = Evidence(policy: 5, minimumBuild: 700);
        var newer = Evidence(policy: 6, minimumBuild: 691);
        var opaque = Evidence(
            policy: 0,
            minimumBuild: 0,
            DesktopCompatibilityEvidenceKind.Opaque426);

        Assert.Same(
            baseline,
            DesktopCompatibilityPolicy.Merge(
                baseline,
                older));
        Assert.Equal(
            700,
            DesktopCompatibilityPolicy.Merge(
                    baseline,
                    equalStricter)
                .MinimumBuild);
        Assert.Same(
            newer,
            DesktopCompatibilityPolicy.Merge(
                baseline,
                newer));
        Assert.Equal(
            DesktopCompatibilityEvidenceKind.Opaque426,
            DesktopCompatibilityPolicy.Merge(
                    baseline,
                    opaque)
                .Kind);
        Assert.Same(
            opaque,
            DesktopCompatibilityPolicy.Merge(
                opaque,
                newer));
    }

    [Fact]
    public async Task Store_DualSlotFallsBackAndClearRemovesMarkerLast()
    {
        var root = NewRoot();
        try
        {
            var store =
                new DesktopCompatibilityEvidenceStore(root);
            await store.PersistAsync(
                Evidence(policy: 5, minimumBuild: 690));
            await store.PersistAsync(
                Evidence(policy: 6, minimumBuild: 700));

            var active = (await File.ReadAllTextAsync(
                    Path.Combine(root, "active.slot")))
                .Trim();
            await File.WriteAllTextAsync(
                Path.Combine(
                    root,
                    active == "a"
                        ? "evidence-a.json"
                        : "evidence-b.json"),
                "{\"schemaVersion\":1");
            var recovered = await store.LoadAsync();
            Assert.True(
                recovered.State ==
                DesktopCompatibilityEvidenceState.Valid,
                recovered.DiagnosticCode + Environment.NewLine +
                string.Join(
                    Environment.NewLine,
                    Directory.EnumerateFiles(root)
                        .OrderBy(
                            static path => path,
                            StringComparer.Ordinal)
                        .Select(
                            static path =>
                                Path.GetFileName(path) + "=" +
                                File.ReadAllText(path))));
            Assert.Equal(5, recovered.Evidence!.PolicyVersion);

            await store.ClearAsync();
            var cleared = await store.LoadAsync();
            Assert.Equal(
                DesktopCompatibilityEvidenceState.None,
                cleared.State);
            Assert.False(
                File.Exists(
                    Path.Combine(root, "blocked.marker")));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Theory]
    [InlineData(nameof(DesktopCompatibilityStoreFaultPoint.AfterMarkerWrite))]
    [InlineData(nameof(DesktopCompatibilityStoreFaultPoint.BeforeSlotPublish))]
    [InlineData(nameof(DesktopCompatibilityStoreFaultPoint.AfterSlotPublish))]
    [InlineData(nameof(DesktopCompatibilityStoreFaultPoint.BeforePointerPublish))]
    [InlineData(nameof(DesktopCompatibilityStoreFaultPoint.AfterPointerPublish))]
    public async Task Store_InterruptedPersistNeverLooksAbsent(
        string faultPointName)
    {
        var faultPoint =
            Enum.Parse<DesktopCompatibilityStoreFaultPoint>(faultPointName);
        var root = NewRoot();
        try
        {
            var store = new DesktopCompatibilityEvidenceStore(
                root,
                point => point == faultPoint
                    ? new IOException("fault")
                    : null);
            await Assert.ThrowsAsync<IOException>(
                () => store.PersistAsync(
                    Evidence(policy: 5, minimumBuild: 690)));

            var loaded = await new
                    DesktopCompatibilityEvidenceStore(root)
                .LoadAsync();
            Assert.NotEqual(
                DesktopCompatibilityEvidenceState.None,
                loaded.State);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Handler_LatchesBeforeFailingPersistenceAndPreservesTyped426()
    {
        var root = NewRoot();
        try
        {
            var store = new DesktopCompatibilityEvidenceStore(
                root,
                point => point ==
                         DesktopCompatibilityStoreFaultPoint
                             .BeforeSlotPublish
                    ? new IOException("disk-failure")
                    : null);
            var latch = new DesktopCompatibilityLatch();
            var signal = new DesktopUpgradeRequiredSignal();
            var delivered = 0;
            signal.UpgradeRequired += _ =>
            {
                delivered++;
                throw new InvalidOperationException(
                    "ui-failure");
            };
            var observer = new DesktopUpgradeRequiredObserver(
                latch,
                store,
                new DesktopClientIdentityProvider(
                    new Version(1, 1, 689)),
                signal,
                () => DateTime.UnixEpoch);
            var handler = new DesktopUpgradeRequiredHandler(
                observer)
            {
                InnerHandler = new Json426Handler()
            };
            using var http = new HttpClient(handler)
            {
                BaseAddress = new Uri("https://trade.example/")
            };

            var exception = await Assert.ThrowsAsync<
                DesktopClientUpgradeRequiredException>(
                () => http.PostAsync(
                    "sync/push",
                    new StringContent("{}")));

            Assert.Equal(
                HttpStatusCode.UpgradeRequired,
                exception.StatusCode);
            Assert.True(latch.Snapshot.IsBlocked);
            Assert.Equal(1, delivered);
            Assert.Equal(
                DesktopCompatibilityEvidenceState.Unreadable,
                (await new
                        DesktopCompatibilityEvidenceStore(root)
                    .LoadAsync())
                .State);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ChangedSubscriberFailure_DoesNotBlockPersistenceOrOtherSubscribers()
    {
        var root = NewRoot();
        try
        {
            var store =
                new DesktopCompatibilityEvidenceStore(root);
            var latch = new DesktopCompatibilityLatch();
            var delivered = 0;
            var mutationDelivered = 0;
            latch.Changed += _ =>
                throw new InvalidOperationException(
                    "changed-subscriber-failure");
            latch.Changed += _ => delivered++;
            latch.MutationAvailabilityChanged += _ =>
                mutationDelivered++;
            var observer = new DesktopUpgradeRequiredObserver(
                latch,
                store,
                new DesktopClientIdentityProvider(
                    new Version(1, 1, 689)),
                new DesktopUpgradeRequiredSignal(),
                () => DateTime.UnixEpoch);

            await observer.ObserveAsync(
                UpgradeException(
                    appId: "kr.georaeplan.desktop",
                    platform: "windows",
                    policyVersion: 7));

            var loaded = await store.LoadAsync();
            Assert.True(latch.Snapshot.IsBlocked);
            Assert.Equal(1, delivered);
            Assert.Equal(1, mutationDelivered);
            Assert.Equal(
                DesktopCompatibilityEvidenceState.Valid,
                loaded.State);
            Assert.Equal(7, loaded.Evidence!.PolicyVersion);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task MutationSubscriberFailure_DoesNotBlockPersistenceOrOtherSubscribers()
    {
        var root = NewRoot();
        try
        {
            var store =
                new DesktopCompatibilityEvidenceStore(root);
            var latch = new DesktopCompatibilityLatch();
            var delivered = 0;
            latch.MutationAvailabilityChanged += _ =>
                throw new InvalidOperationException(
                    "mutation-subscriber-failure");
            latch.MutationAvailabilityChanged += _ =>
                delivered++;
            var observer = new DesktopUpgradeRequiredObserver(
                latch,
                store,
                new DesktopClientIdentityProvider(
                    new Version(1, 1, 689)),
                new DesktopUpgradeRequiredSignal(),
                () => DateTime.UnixEpoch);

            await observer.ObserveAsync(
                UpgradeException(
                    appId: "kr.georaeplan.desktop",
                    platform: "windows",
                    policyVersion: 7));

            var loaded = await store.LoadAsync();
            Assert.True(latch.Snapshot.IsBlocked);
            Assert.Equal(1, delivered);
            Assert.Equal(
                DesktopCompatibilityEvidenceState.Valid,
                loaded.State);
            Assert.Equal(7, loaded.Evidence!.PolicyVersion);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task MarkerOnlyPersistFailure_RetryCannotWeakenOpaqueMemoryEvidence()
    {
        var root = NewRoot();
        try
        {
            var store = new DesktopCompatibilityEvidenceStore(
                root,
                point => point ==
                         DesktopCompatibilityStoreFaultPoint
                             .BeforeSlotPublish
                    ? new IOException("disk-failure")
                    : null);
            var latch = new DesktopCompatibilityLatch();
            var identity = new DesktopClientIdentityProvider(
                new Version(1, 1, 689));
            var observer = new DesktopUpgradeRequiredObserver(
                latch,
                store,
                identity,
                new DesktopUpgradeRequiredSignal(),
                () => DateTime.UnixEpoch);

            await observer.ObserveAsync(
                UpgradeException(
                    appId: "untrusted-client",
                    platform: "unknown",
                    policyVersion: 7));

            var beforeRetry = latch.Snapshot;
            Assert.Equal(
                DesktopCompatibilityEvidenceState.Valid,
                beforeRetry.EvidenceState);
            Assert.Equal(
                DesktopCompatibilityEvidenceKind.Opaque426,
                beforeRetry.Evidence!.Kind);
            Assert.Equal(
                DesktopCompatibilityEvidenceState.Unreadable,
                (await new DesktopCompatibilityEvidenceStore(root)
                    .LoadAsync()).State);

            var compatible = ValidManifest();
            compatible.PolicyVersion = 8;
            compatible.Desktop!.PolicyVersion = 8;
            compatible.Desktop.MinimumSupportedVersion =
                Runtime.Version;
            compatible.Desktop.MinimumSupportedBuild =
                Runtime.Build;
            using var http = new HttpClient(
                new ManifestHandler(compatible))
            {
                BaseAddress = new Uri("https://trade.example/")
            };
            var api = new ErpApiClient(
                http,
                new SessionState(),
                clientIdentityProvider: identity);
            var gate = new DesktopCompatibilityGateService(
                new DesktopCompatibilityEvidenceStore(root),
                latch,
                identity,
                api);

            var decision = await gate.CheckAsync();

            Assert.True(decision.IsBlocked);
            Assert.Equal(
                DesktopCompatibilityEvidenceKind.Opaque426,
                decision.Evidence!.Kind);
            Assert.Equal(
                DesktopCompatibilityEvidenceState.Valid,
                latch.Snapshot.EvidenceState);
            Assert.Equal(
                beforeRetry.Revision,
                latch.Snapshot.Revision);
            var repaired =
                await new DesktopCompatibilityEvidenceStore(root)
                    .LoadAsync();
            Assert.Equal(
                DesktopCompatibilityEvidenceState.Valid,
                repaired.State);
            Assert.Equal(
                beforeRetry.Evidence,
                repaired.Evidence);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Runtime426_CancelsConcurrentServerRefreshBeforeLocalApply()
    {
        var root = NewRoot();
        await using var connection =
            new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options =
            new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite(connection)
                .Options;
        await using var db = new LocalDbContext(options);
        await db.Database.EnsureCreatedAsync();
        try
        {
            var session = new SessionState();
            session.SetSession(
                "compatibility-cancellation-test-token",
                new UserSessionDto
                {
                    Username = "compatibility-test",
                    Role = DomainConstants.RoleAdmin,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ScopeType = TenantScopeCatalog.ScopeAdmin
                });
            var dispatcher = new SyncRequestDispatcher();
            var local = new LocalStateService(
                db,
                new OfficeAccessService(),
                dispatcher,
                session);
            var rental = new RentalStateService(db, local);
            var diagnostics = new SyncDiagnosticsService(session);
            var handler = new BlockingPullHandler();
            using var http = new HttpClient(handler)
            {
                BaseAddress = new Uri("https://trade.example/")
            };
            var api = new ErpApiClient(http, session);
            var latch = new DesktopCompatibilityLatch();
            using var sync = new SyncService(
                db,
                local,
                rental,
                api,
                session,
                dispatcher,
                diagnostics,
                compatibilityRuntime: latch);
            var refresh =
                sync.RefreshSharedMirrorFromServerAsync();
            await handler.Started.Task.WaitAsync(
                TimeSpan.FromSeconds(5));

            var observer = new DesktopUpgradeRequiredObserver(
                latch,
                new DesktopCompatibilityEvidenceStore(root),
                new DesktopClientIdentityProvider(
                    new Version(1, 1, 689)),
                new DesktopUpgradeRequiredSignal(),
                () => DateTime.UnixEpoch);
            await observer.ObserveAsync(
                UpgradeException(
                    appId: "kr.georaeplan.desktop",
                    platform: "windows",
                    policyVersion: 7));

            Assert.False(
                await refresh.WaitAsync(
                    TimeSpan.FromSeconds(5)));
            await handler.Canceled.Task.WaitAsync(
                TimeSpan.FromSeconds(5));
            Assert.Null(
                await local.GetSettingAsync(
                    "Sync.LastError"));
            Assert.Equal(0, await db.Customers.CountAsync());
            Assert.Equal(0, await db.Items.CountAsync());
            Assert.Equal(0, await db.Invoices.CountAsync());
            Assert.Equal(0, await db.Payments.CountAsync());
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ErpApiClient_PullCallerCancellationIsNotWrapped()
    {
        var handler = new BlockingPullHandler();
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://trade.example/")
        };
        var api = new ErpApiClient(
            http,
            new SessionState());
        using var cts = new CancellationTokenSource();

        var pull = api.PullAsync(
            0,
            cts.Token);
        await handler.Started.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<
            OperationCanceledException>(
            () => pull);
        await handler.Canceled.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Gate_ManifestCallerCancellationIsPropagated()
    {
        var root = NewRoot();
        try
        {
            var handler = new BlockingPullHandler();
            using var http = new HttpClient(handler)
            {
                BaseAddress = new Uri("https://trade.example/")
            };
            var identity = new DesktopClientIdentityProvider(
                new Version(1, 1, 689));
            var gate = new DesktopCompatibilityGateService(
                new DesktopCompatibilityEvidenceStore(root),
                new DesktopCompatibilityLatch(),
                identity,
                new ErpApiClient(
                    http,
                    new SessionState(),
                    clientIdentityProvider: identity));
            using var cts = new CancellationTokenSource();

            var check = gate.CheckAsync(cts.Token);
            await handler.Started.Task.WaitAsync(
                TimeSpan.FromSeconds(5));
            cts.Cancel();

            await Assert.ThrowsAnyAsync<
                OperationCanceledException>(
                () => check);
            await handler.Canceled.Task.WaitAsync(
                TimeSpan.FromSeconds(5));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task Gate_DurableClearCallerCancellationIsPropagatedAndRemainsBlocked()
    {
        var root = NewRoot();
        try
        {
            var evidence = Evidence(
                policy: 5,
                minimumBuild: 690);
            await new DesktopCompatibilityEvidenceStore(root)
                .PersistAsync(evidence);
            var latch = new DesktopCompatibilityLatch();
            latch.Activate(evidence);
            using var cts = new CancellationTokenSource();
            var store = new DesktopCompatibilityEvidenceStore(
                root,
                point =>
                {
                    if (point !=
                        DesktopCompatibilityStoreFaultPoint
                            .BeforeClearSlots)
                    {
                        return null;
                    }

                    cts.Cancel();
                    return new OperationCanceledException(
                        cts.Token);
                });
            var identity = new DesktopClientIdentityProvider(
                new Version(1, 1, 700));
            using var http = new HttpClient(
                new ManifestHandler(ValidManifest()))
            {
                BaseAddress = new Uri("https://trade.example/")
            };
            var gate = new DesktopCompatibilityGateService(
                store,
                latch,
                identity,
                new ErpApiClient(
                    http,
                    new SessionState(),
                    clientIdentityProvider: identity));

            await Assert.ThrowsAnyAsync<
                OperationCanceledException>(
                () => gate.CheckAsync(cts.Token));

            Assert.True(cts.IsCancellationRequested);
            Assert.True(latch.Snapshot.IsBlocked);
            Assert.NotEqual(
                DesktopCompatibilityEvidenceState.None,
                (await new DesktopCompatibilityEvidenceStore(root)
                    .LoadAsync()).State);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Theory]
    [InlineData("stable", "desktop", true)]
    [InlineData("Stable", "desktop", false)]
    [InlineData("stable", "Desktop", false)]
    [InlineData(" stable", "desktop", false)]
    [InlineData("stable", " desktop", false)]
    public void StableVerifier_UsesLiteralChannelAndPlatform(
        string channel,
        string platform,
        bool expected)
    {
        var manifest = ValidManifest();
        manifest.Channel = channel;
        manifest.Desktop!.Platform = platform;

        var result = DesktopStablePolicyVerifier.Verify(
            manifest,
            new Uri("https://trade.example/"));

        Assert.Equal(expected, result.IsVerified);
    }

    [Fact]
    public void StartupWiring_GatesBeforeRestoreDatabaseAndLogin()
    {
        var source = File.ReadAllText(
            Path.Combine(
                FindRepositoryRoot(),
                "Desktop",
                "거래플랜.Desktop.App",
                "App.xaml.cs"));
        var gate = source.IndexOf(
            "EnsureDesktopCompatibilityBeforeLoginAsync()",
            StringComparison.Ordinal);
        var restore = source.IndexOf(
            "ApplyPendingRestoreAfterCompatibilityGate();",
            StringComparison.Ordinal);
        var database = source.IndexOf(
            "RunPreLoginInitializationAsync();",
            StringComparison.Ordinal);
        var login = source.IndexOf(
            "GetRequiredService<LoginViewModel>()",
            StringComparison.Ordinal);

        Assert.True(gate >= 0);
        Assert.True(restore > gate);
        Assert.True(database > restore);
        Assert.True(login > database);
        Assert.Contains(
            "HandleDesktopUpgradeRequiredSignal",
            source,
            StringComparison.Ordinal);
    }

    private static DesktopClientUpgradeRequiredException UpgradeException(
        string appId,
        string platform,
        int policyVersion)
        => new(
            "/sync/push",
            new ClientUpgradeRequiredResponse
            {
                Client = new ClientCompatibilityIdentityDto
                {
                    AppId = appId,
                    Platform = platform,
                    Version = Runtime.Version,
                    Build = Runtime.Build,
                    ProtocolVersion =
                        Runtime.ProtocolVersion
                },
                Required = new ClientCompatibilityPolicyDto
                {
                    PolicyVersion = policyVersion,
                    RequiresUserAction = true,
                    MinimumVersion = "1.1.690",
                    MinimumBuild = 690,
                    MinimumProtocolVersion = 1,
                    LatestVersion = "1.1.700",
                    LatestBuild = 700
                }
            });

    private static DesktopCompatibilityEvidence Evidence(
        int policy,
        int minimumBuild,
        DesktopCompatibilityEvidenceKind kind =
            DesktopCompatibilityEvidenceKind.Verified426)
        => new()
        {
            Kind = kind,
            PolicyVersion = policy,
            MinimumVersion =
                kind == DesktopCompatibilityEvidenceKind.Opaque426
                    ? string.Empty
                    : "1.1.690",
            MinimumBuild = minimumBuild,
            MinimumProtocolVersion =
                kind == DesktopCompatibilityEvidenceKind.Opaque426
                    ? 0
                    : 1,
            LatestVersion =
                kind == DesktopCompatibilityEvidenceKind.Opaque426
                    ? string.Empty
                    : "1.1.700",
            LatestBuild =
                kind == DesktopCompatibilityEvidenceKind.Opaque426
                    ? 0
                    : Math.Max(700, minimumBuild),
            ObservedVersion = Runtime.Version,
            ObservedBuild = Runtime.Build,
            ObservedProtocolVersion =
                Runtime.ProtocolVersion,
            ObservedAtUtc = DateTime.UnixEpoch
        };

    private static AppUpdateManifestDto ValidManifest()
    {
        const string fileName =
            "tradeplan-desktop-v1.1.700.zip";
        return new AppUpdateManifestDto
        {
            Channel = "stable",
            ProtocolVersion = 1,
            PolicyVersion = 7,
            RequiresUserAction = true,
            CompatibilityPolicy = "minimum",
            Desktop = new AppUpdatePackageDto
            {
                Platform = "desktop",
                Version = "1.1.700",
                Build = 700,
                ProtocolVersion = 1,
                MinimumSupportedVersion = "1.1.690",
                MinimumSupportedBuild = 690,
                MinimumSupportedProtocolVersion = 1,
                PolicyVersion = 7,
                RequiresUserAction = true,
                CompatibilityPolicy = "minimum",
                PackageUrl = $"/updates/{fileName}",
                FileName = fileName,
                Sha256 = new string('A', 64),
                FileSize = 1024
            }
        };
    }

    private sealed class Json426Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(
                new HttpResponseMessage(
                    HttpStatusCode.UpgradeRequired)
                {
                    RequestMessage = request,
                    Content = new StringContent(
                        """
                        {
                          "client": {
                            "appId": "kr.georaeplan.desktop",
                            "platform": "windows",
                            "version": "1.1.689",
                            "build": 689,
                            "protocolVersion": 1
                          },
                          "required": {
                            "policyVersion": 7,
                            "requiresUserAction": true,
                            "minimumVersion": "1.1.690",
                            "minimumBuild": 690,
                            "minimumProtocolVersion": 1,
                            "latestVersion": "1.1.700",
                            "latestBuild": 700
                          }
                        }
                        """,
                        Encoding.UTF8,
                        "application/json")
                });
    }

    private sealed class ManifestHandler(
        AppUpdateManifestDto manifest)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent(
                        System.Text.Json.JsonSerializer.Serialize(
                            manifest),
                        Encoding.UTF8,
                        "application/json")
                });
    }

    private sealed class BlockingPullHandler : HttpMessageHandler
    {
        public TaskCompletionSource<bool> Started { get; } =
            new(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Canceled { get; } =
            new(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            try
            {
                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken);
                throw new InvalidOperationException(
                    "The blocking request unexpectedly completed.");
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                Canceled.TrySetResult(true);
                throw;
            }
        }
    }

    private static string NewRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "georaeplan-desktop-compat-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private static string FindRepositoryRoot(
        [CallerFilePath] string sourceFilePath = "")
    {
        var directory = new DirectoryInfo(
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

        throw new DirectoryNotFoundException();
    }
}
