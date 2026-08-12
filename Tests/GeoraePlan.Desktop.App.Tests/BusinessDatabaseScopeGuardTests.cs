using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.CompilerServices;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class BusinessDatabaseScopeGuardTests
{
    [Fact]
    public void SessionState_SetBusinessDatabase_IgnoresTenantScopedAdmin()
    {
        var session = new SessionState();
        session.SetSession("tenant-admin-token", CreateUser(TenantScopeCatalog.ScopeTenantAll), DateTime.UtcNow.AddHours(1));

        session.SetBusinessDatabase(TenantScopeCatalog.Itworld, "ITWORLD");

        Assert.Equal(TenantScopeCatalog.UsenetGroup, session.TenantCode);
        Assert.Equal(TenantScopeCatalog.GetDatabaseName(TenantScopeCatalog.UsenetGroup), session.SelectedBusinessDatabaseName);
        Assert.False(session.HasSystemConfigurationScope);
    }

    [Fact]
    public void SessionState_SetBusinessDatabase_AllowsGlobalAdmin()
    {
        var session = new SessionState();
        session.SetSession("global-admin-token", CreateUser(TenantScopeCatalog.ScopeAdmin), DateTime.UtcNow.AddHours(1));

        session.SetBusinessDatabase(TenantScopeCatalog.Itworld, "ITWORLD");

        Assert.Equal(TenantScopeCatalog.Itworld, session.TenantCode);
        Assert.Equal(TenantScopeCatalog.GetDatabaseName(TenantScopeCatalog.Itworld), session.SelectedBusinessDatabaseName);
        Assert.True(session.HasSystemConfigurationScope);
    }

    [Fact]
    public void SessionState_BusinessDatabaseAba_AdvancesMonotonicScopeEpoch()
    {
        var session = new SessionState();
        session.SetSession(
            "global-admin-token",
            CreateUser(TenantScopeCatalog.ScopeAdmin),
            DateTime.UtcNow.AddHours(1));
        var initialEpoch = session.SyncScopeEpoch;

        session.SetBusinessDatabase(TenantScopeCatalog.Itworld, "ITWORLD");
        var changedEpoch = session.SyncScopeEpoch;
        session.SetBusinessDatabase(TenantScopeCatalog.UsenetGroup, "USENET");

        Assert.True(changedEpoch > initialEpoch);
        Assert.True(session.SyncScopeEpoch > changedEpoch);
        Assert.Equal(
            TenantScopeCatalog.GetDatabaseName(TenantScopeCatalog.UsenetGroup),
            session.SelectedBusinessDatabaseName);
    }

    [Fact]
    public void SessionState_RefreshPermissionOnlyChange_AdvancesScopeEpoch()
    {
        var userId = Guid.NewGuid();
        var initialUser = CreateUser(TenantScopeCatalog.ScopeAdmin);
        initialUser.UserId = userId;
        initialUser.Permissions = ["Customers.Read"];
        var refreshedUser = CreateUser(TenantScopeCatalog.ScopeAdmin);
        refreshedUser.UserId = userId;
        refreshedUser.Username = initialUser.Username;
        refreshedUser.Role = initialUser.Role;
        refreshedUser.TenantCode = initialUser.TenantCode;
        refreshedUser.OfficeCode = initialUser.OfficeCode;
        refreshedUser.Permissions =
        [
            "Customers.Read",
            "Customers.Write"
        ];
        var session = new SessionState();
        session.SetSession(
            "initial-token",
            initialUser,
            DateTime.UtcNow.AddHours(1));
        var initialEpoch = session.SyncScopeEpoch;

        session.RefreshSession(
            "refreshed-token",
            refreshedUser,
            DateTime.UtcNow.AddHours(1));

        Assert.True(session.SyncScopeEpoch > initialEpoch);
        Assert.True(session.HasPermission("Customers.Write"));
    }

    [Fact]
    public async Task SessionState_CommitLease_BlocksBusinessDatabaseMutationUntilReleased()
    {
        var session = new SessionState();
        session.SetSession(
            "global-admin-token",
            CreateUser(TenantScopeCatalog.ScopeAdmin),
            DateTime.UtcNow.AddHours(1));
        var mutationStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var commitLease =
            await session.AcquireSyncScopeCommitLeaseAsync();
        var mutationTask = Task.Run(() =>
        {
            mutationStarted.TrySetResult(true);
            session.SetBusinessDatabase(TenantScopeCatalog.Itworld, "ITWORLD");
        });
        try
        {
            await mutationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var firstCompletion = await Task.WhenAny(
                mutationTask,
                Task.Delay(TimeSpan.FromMilliseconds(150)));
            Assert.NotSame(mutationTask, firstCompletion);
        }
        finally
        {
            commitLease.Dispose();
        }

        await mutationTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(
            TenantScopeCatalog.GetDatabaseName(TenantScopeCatalog.Itworld),
            session.SelectedBusinessDatabaseName);
    }

    [Fact]
    public void SessionState_BusinessDatabaseChanged_IsRaisedAfterScopeLeaseAndEpochUpdate()
    {
        var session = new SessionState();
        session.SetSession(
            "global-admin-token",
            CreateUser(TenantScopeCatalog.ScopeAdmin),
            DateTime.UtcNow.AddHours(1));
        var initialEpoch = session.SyncScopeEpoch;
        var gateField = typeof(SessionState).GetField(
            "_syncScopeMutationGate",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var gate = Assert.IsType<SemaphoreSlim>(gateField?.GetValue(session));
        var observedGateCount = -1;
        var observedEpoch = initialEpoch;
        session.BusinessDatabaseChanged += (_, _) =>
        {
            observedGateCount = gate.CurrentCount;
            observedEpoch = session.SyncScopeEpoch;
        };

        session.SetBusinessDatabase(TenantScopeCatalog.Itworld, "ITWORLD");

        Assert.Equal(1, observedGateCount);
        Assert.True(observedEpoch > initialEpoch);
    }

    [Fact]
    public void SessionState_ThrowingBusinessDatabaseChangedSubscriber_DoesNotSuppressEpochAdvance()
    {
        var session = new SessionState();
        session.SetSession(
            "global-admin-token",
            CreateUser(TenantScopeCatalog.ScopeAdmin),
            DateTime.UtcNow.AddHours(1));
        var initialEpoch = session.SyncScopeEpoch;
        session.BusinessDatabaseChanged += (_, _) =>
            throw new InvalidOperationException("simulated UI callback failure");

        Assert.Throws<InvalidOperationException>(() =>
            session.SetBusinessDatabase(TenantScopeCatalog.Itworld, "ITWORLD"));

        Assert.True(session.SyncScopeEpoch > initialEpoch);
        Assert.Equal(
            TenantScopeCatalog.GetDatabaseName(TenantScopeCatalog.Itworld),
            session.SelectedBusinessDatabaseName);
    }

    [Fact]
    public void SessionState_ClearFromAlternateBusinessDatabase_RaisesChangedAfterLeaseAndEpochUpdate()
    {
        var session = new SessionState();
        session.SetSession(
            "global-admin-token",
            CreateUser(TenantScopeCatalog.ScopeAdmin),
            DateTime.UtcNow.AddHours(1));
        session.SetBusinessDatabase(TenantScopeCatalog.Itworld, "ITWORLD");
        var initialEpoch = session.SyncScopeEpoch;
        var gateField = typeof(SessionState).GetField(
            "_syncScopeMutationGate",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var gate = Assert.IsType<SemaphoreSlim>(gateField?.GetValue(session));
        var eventCount = 0;
        var observedGateCount = -1;
        var observedEpoch = initialEpoch;
        session.BusinessDatabaseChanged += (_, _) =>
        {
            eventCount++;
            observedGateCount = gate.CurrentCount;
            observedEpoch = session.SyncScopeEpoch;
        };

        session.Clear();

        Assert.Equal(1, eventCount);
        Assert.Equal(1, observedGateCount);
        Assert.True(observedEpoch > initialEpoch);
        Assert.Equal(
            TenantScopeCatalog.GetDatabaseName(TenantScopeCatalog.UsenetGroup),
            session.SelectedBusinessDatabaseName);
    }

    [Fact]
    public void SyncService_CommitLeaseCommit_ReleasesWithoutUiContextCapture()
    {
        var repositoryRoot = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "Services",
            "SyncService.cs"));

        Assert.Contains(
            "CommitAttachmentTransactionUnderOwnerLeaseAsync(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "await transaction.CommitAsync(ct).ConfigureAwait(false);",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "using (await _session.AcquireSyncScopeCommitLeaseAsync(ct))",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ErpApiClient_DoesNotSendTenantHeader_ForTenantScopedAdmin()
    {
        var session = new SessionState();
        session.SetSession("tenant-admin-token", CreateUser(TenantScopeCatalog.ScopeTenantAll), DateTime.UtcNow.AddHours(1));
        session.SetBusinessDatabase(TenantScopeCatalog.Itworld, "ITWORLD");
        var handler = new HeaderCaptureHandler();
        var api = new ErpApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost/")
        }, session);

        await api.HeartbeatEditSessionAsync(new EditSessionHeartbeatRequest
        {
            EditSessionId = Guid.NewGuid(),
            AppSessionId = Guid.NewGuid(),
            ScreenName = "test",
            EntityType = "Customer",
            EntityId = Guid.NewGuid().ToString("D")
        });

        Assert.Null(handler.LastTenantHeader);
    }

    [Fact]
    public async Task ErpApiClient_SendsTenantHeader_ForGlobalAdmin()
    {
        var session = new SessionState();
        session.SetSession("global-admin-token", CreateUser(TenantScopeCatalog.ScopeAdmin), DateTime.UtcNow.AddHours(1));
        session.SetBusinessDatabase(TenantScopeCatalog.Itworld, "ITWORLD");
        var handler = new HeaderCaptureHandler();
        var api = new ErpApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost/")
        }, session);

        await api.HeartbeatEditSessionAsync(new EditSessionHeartbeatRequest
        {
            EditSessionId = Guid.NewGuid(),
            AppSessionId = Guid.NewGuid(),
            ScreenName = "test",
            EntityType = "Customer",
            EntityId = Guid.NewGuid().ToString("D")
        });

        Assert.Equal(TenantScopeCatalog.GetDatabaseName(TenantScopeCatalog.Itworld), handler.LastTenantHeader);
    }

    private static UserSessionDto CreateUser(string scopeType)
        => new()
        {
            UserId = Guid.NewGuid(),
            Username = "admin",
            Role = "Admin",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = scopeType
        };

    private static string FindRepositoryRoot(
        [CallerFilePath] string sourceFilePath = "")
    {
        var directory = new DirectoryInfo(
            Path.GetDirectoryName(sourceFilePath) ?? AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) &&
                Directory.Exists(Path.Combine(directory.FullName, "Desktop")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class HeaderCaptureHandler : HttpMessageHandler
    {
        public string? LastTenantHeader { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastTenantHeader = request.Headers.TryGetValues("X-Tenant-Code", out var values)
                ? values.FirstOrDefault()
                : null;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new EditSessionHeartbeatResponse
                {
                    ServerUtc = DateTime.UtcNow,
                    OtherEditors = []
                })
            });
        }
    }
}
