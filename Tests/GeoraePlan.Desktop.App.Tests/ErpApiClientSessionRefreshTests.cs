using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class ErpApiClientSessionRefreshTests
{
    [Fact]
    public async Task RefreshSessionAsync_CallerCancellationIsNotWrappedAsTransportFailure()
    {
        var session = new SessionState();
        session.SetSession("old-token", CreateAdminUser(), DateTime.UtcNow.AddDays(1));

        var handler = new CallerCanceledRefreshHandler(unauthorizedBeforeRefresh: false);
        var api = new ErpApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost/")
        }, session);
        using var cancellation = new CancellationTokenSource();

        var refreshTask = api.RefreshSessionAsync(cancellation.Token);
        await handler.RefreshStarted.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refreshTask);
        Assert.True(session.IsLoggedIn);
        Assert.Equal("old-token", session.Token);
        Assert.Equal(1, handler.RefreshRequestCount);
    }

    [Fact]
    public async Task GetSyncStatusAsync_CallerCancellationDuringUnauthorizedRefreshIsRethrown()
    {
        var session = new SessionState();
        session.SetSession("old-token", CreateAdminUser(), DateTime.UtcNow.AddDays(1));

        var handler = new CallerCanceledRefreshHandler(unauthorizedBeforeRefresh: true);
        var api = new ErpApiClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost/")
        }, session);
        using var cancellation = new CancellationTokenSource();

        var statusTask = api.GetSyncStatusAsync(cancellation.Token);
        await handler.RefreshStarted.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => statusTask);
        Assert.True(session.IsLoggedIn);
        Assert.Equal("old-token", session.Token);
        Assert.Equal(1, handler.SyncStatusRequestCount);
        Assert.Equal(1, handler.RefreshRequestCount);
    }

    [Fact]
    public async Task GetSyncStatusAsync_PreflightRefresh426_RethrowsObservedInstanceWithoutContinuing()
    {
        var session = new SessionState();
        session.SetSession("old-token", CreateAdminUser(), DateTime.UtcNow.AddMinutes(10));
        var handler = new RefreshUpgradeRequiredHandler(unauthorizedBeforeRefresh: false);
        var observer = new RecordingUpgradeRequiredObserver();
        var api = new ErpApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") },
            session,
            upgradeObserver: observer);

        var exception =
            await Assert.ThrowsAsync<DesktopClientUpgradeRequiredException>(
                () => api.GetSyncStatusAsync());

        Assert.True(observer.IsLatched);
        Assert.Same(observer.ObservedException, exception);
        Assert.Equal("/auth/refresh", exception.RequestPath);
        Assert.Collection(
            handler.Requests,
            request => Assert.Equal("/auth/refresh", request.Path));
        Assert.Equal(0, handler.SyncStatusRequestCount);
        Assert.Equal(1, handler.RefreshRequestCount);
    }

    [Fact]
    public async Task GetSyncStatusAsync_UnauthorizedRefresh426_RethrowsObservedInstanceWithoutRetry()
    {
        var session = new SessionState();
        session.SetSession("old-token", CreateAdminUser(), DateTime.UtcNow.AddDays(1));
        var handler = new RefreshUpgradeRequiredHandler(unauthorizedBeforeRefresh: true);
        var observer = new RecordingUpgradeRequiredObserver();
        var api = new ErpApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") },
            session,
            upgradeObserver: observer);

        var exception =
            await Assert.ThrowsAsync<DesktopClientUpgradeRequiredException>(
                () => api.GetSyncStatusAsync());

        Assert.True(observer.IsLatched);
        Assert.Same(observer.ObservedException, exception);
        Assert.Equal("/auth/refresh", exception.RequestPath);
        Assert.Collection(
            handler.Requests,
            request => Assert.Equal("/sync/status", request.Path),
            request => Assert.Equal("/auth/refresh", request.Path));
        Assert.Equal(1, handler.SyncStatusRequestCount);
        Assert.Equal(1, handler.RefreshRequestCount);
    }

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
    public async Task GetSyncStatusAsync_SuccessfulRefreshPersistsReducedOfflineAuthorizationOnly()
    {
        const string password = "cached-password";
        var originalUser = CreateAdminUser();
        originalUser.Permissions =
        [
            "Customers.Read",
            "Customers.Write",
            "System.Configure"
        ];
        var reducedUser = new UserSessionDto
        {
            UserId = originalUser.UserId,
            Username = originalUser.Username,
            Role = "User",
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = ["Customers.Read"]
        };
        await using var fixture = await PersistentAuthFixture.CreateAsync();
        await fixture.SaveAuthenticationAsync(originalUser, password);
        fixture.TrackUnsavedSetting("Unrelated.Pending", "must-not-be-saved");

        var session = new SessionState();
        session.SetSession("old-token", originalUser, DateTime.UtcNow.AddMinutes(10));
        var api = new ErpApiClient(
            new HttpClient(new RefreshingSyncStatusHandler(reducedUser))
            {
                BaseAddress = new Uri("http://localhost/")
            },
            session,
            fixture.Local);

        Assert.NotNull(await api.GetSyncStatusAsync());

        var cached = await fixture.Local.GetCachedSessionAsync(originalUser.Username);
        Assert.NotNull(cached);
        Assert.Equal("User", cached.Role);
        Assert.Equal(TenantScopeCatalog.Itworld, cached.TenantCode);
        Assert.Equal(TenantScopeCatalog.ScopeOfficeOnly, cached.ScopeType);
        Assert.Equal(OfficeCodeCatalog.Itworld, cached.OfficeCode);
        Assert.Equal(["Customers.Read"], cached.Permissions);
        Assert.DoesNotContain("Customers.Write", cached.Permissions);
        Assert.DoesNotContain("System.Configure", cached.Permissions);
        Assert.True(
            await fixture.Local.VerifyCachedSessionPasswordAsync(
                originalUser.Username,
                password));
        Assert.False(await fixture.IsSettingPersistedAsync("Unrelated.Pending"));
        Assert.True(fixture.IsSettingTrackedAsAdded("Unrelated.Pending"));
    }

    [Fact]
    public async Task GetSyncStatusAsync_CacheWriteFailureKeepsOnlineSessionAndBlocksOfflineFallback()
    {
        var user = CreateAdminUser(username: "refresh-cache-write-failure");
        await using var fixture = await PersistentAuthFixture.CreateAsync();
        await fixture.SaveAuthenticationAsync(user, "cached-password");

        await using (var lockConnection = new SqliteConnection(fixture.ConnectionString))
        {
            await lockConnection.OpenAsync();
            await using var lockCommand = lockConnection.CreateCommand();
            lockCommand.CommandText = "BEGIN EXCLUSIVE;";
            await lockCommand.ExecuteNonQueryAsync();

            var session = new SessionState();
            session.SetSession("old-token", user, DateTime.UtcNow.AddMinutes(10));
            var api = new ErpApiClient(
                new HttpClient(new RefreshingSyncStatusHandler(user))
                {
                    BaseAddress = new Uri("http://localhost/")
                },
                session,
                fixture.Local);

            Assert.NotNull(await api.GetSyncStatusAsync());
            Assert.True(session.IsLoggedIn);
            Assert.Equal("new-token", session.Token);
            Assert.True(LocalStateService.HasAuthenticationRevocationTombstone(user.Username));
            Assert.Null(await fixture.Local.GetCachedSessionAsync(user.Username));

            await using var rollbackCommand = lockConnection.CreateCommand();
            rollbackCommand.CommandText = "ROLLBACK;";
            await rollbackCommand.ExecuteNonQueryAsync();
        }

        await fixture.SaveAuthenticationAsync(user, "new-password");
        Assert.False(LocalStateService.HasAuthenticationRevocationTombstone(user.Username));
    }

    [Fact]
    public async Task RevokeRejectedAuthenticationCacheAsync_TombstoneFailureInvalidatesDatabaseBeforeRemoval()
    {
        var user = CreateAdminUser(username: "marker-fallback-user");
        await using var fixture = await PersistentAuthFixture.CreateAsync();
        await fixture.SaveAuthenticationAsync(user, "cached-password");
        fixture.Local.AuthenticationTombstoneWriteFailureFactory =
            _ => new IOException("simulated marker write failure");

        await fixture.Local.RevokeRejectedAuthenticationCacheAsync(
            user.Username,
            user.OfficeCode);

        Assert.False(LocalStateService.HasAuthenticationRevocationTombstone(user.Username));
        Assert.Null(await fixture.Local.GetCachedSessionAsync(user.Username));
        Assert.False(
            await fixture.Local.VerifyCachedSessionPasswordAsync(
                user.Username,
                "cached-password"));
    }

    [Fact]
    public async Task GetSyncStatusAsync_MarkerAndSchemaBarrierFailureClearsOnlineSession()
    {
        try
        {
            var user = CreateAdminUser(username: "unsafe-barrier-user");
            await using var fixture = await PersistentAuthFixture.CreateAsync();
            await fixture.SaveAuthenticationAsync(user, "cached-password");
            fixture.Local.AuthenticationTombstoneWriteFailureFactory =
                _ => new IOException("simulated marker write failure");
            fixture.Local.AuthenticationSchemaInvalidationFailureFactory =
                _ => new SqliteException("simulated schema invalidation failure", 5);
            fixture.Local.SecondaryAuthenticationTombstoneWriteFailureFactory =
                _ => new IOException("simulated secondary marker write failure");
            fixture.Local.EmergencyAuthenticationTombstoneWriteFailureFactory =
                _ => new IOException("simulated emergency marker write failure");

            var session = new SessionState();
            session.SetSession("old-token", user, DateTime.UtcNow.AddMinutes(10));
            var handler = new RefreshingSyncStatusHandler(user);
            var api = new ErpApiClient(
                new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") },
                session,
                fixture.Local);

            var exception =
                await Assert.ThrowsAsync<LocalStateService.AuthenticationCachePersistenceException>(
                    () => api.GetSyncStatusAsync());

            Assert.False(exception.OfflineFallbackBlocked);
            Assert.True(LocalStateService.IsAuthenticationFatalFailureLatched);
            Assert.False(session.IsLoggedIn);
            Assert.Null(session.Token);
            var request = Assert.Single(handler.Requests);
            Assert.Equal("/auth/refresh", request.Path);
            Assert.Equal("old-token", request.BearerToken);
            Assert.Equal(
                OfflineSessionCachePolicy.CurrentSchemaVersion.ToString(),
                await fixture.ReadPersistedSettingAsync(
                    "CachedSession.unsafe-barrier-user.SchemaVersion"));
        }
        finally
        {
            LocalStateService.ResetAuthenticationFatalFailureForTests();
        }
    }

    [Fact]
    public async Task RevokeRejectedAuthenticationCacheAsync_EmergencyMarkerSurvivesNewContext()
    {
        var user = CreateAdminUser(username: "secondary-marker-user");
        await using var fixture = await PersistentAuthFixture.CreateAsync();
        await fixture.SaveAuthenticationAsync(user, "cached-password");
        fixture.Local.AuthenticationTombstoneWriteFailureFactory =
            _ => new IOException("simulated primary marker write failure");
        fixture.Local.SecondaryAuthenticationTombstoneWriteFailureFactory =
            _ => new IOException("simulated secondary marker write failure");

        await using (var lockConnection = new SqliteConnection(fixture.ConnectionString))
        {
            await lockConnection.OpenAsync();
            await using var lockCommand = lockConnection.CreateCommand();
            lockCommand.CommandText = "BEGIN EXCLUSIVE;";
            await lockCommand.ExecuteNonQueryAsync();

            var exception =
                await Assert.ThrowsAsync<LocalStateService.AuthenticationCachePersistenceException>(
                    () => fixture.Local.RevokeRejectedAuthenticationCacheAsync(
                        user.Username,
                        user.OfficeCode));
            Assert.True(exception.OfflineFallbackBlocked);
            Assert.True(File.Exists(
                LocalStateService.GetEmergencyAuthenticationRevocationTombstonePath(
                    user.Username)));

            await using var rollbackCommand = lockConnection.CreateCommand();
            rollbackCommand.CommandText = "ROLLBACK;";
            await rollbackCommand.ExecuteNonQueryAsync();
        }

        await using var newDb = fixture.CreateIndependentDbContext();
        var newLocal = new LocalStateService(
            newDb,
            new OfficeAccessService(),
            new SyncRequestDispatcher(),
            new SessionState());
        Assert.Null(await newLocal.GetCachedSessionAsync(user.Username));
        Assert.False(
            await newLocal.VerifyCachedSessionPasswordAsync(
                user.Username,
                "cached-password"));

        await newLocal.SaveSessionCacheAsync(
            user.Username,
            user.Role,
            user.Permissions,
            user.TenantCode,
            user.ScopeType,
            user.OfficeCode,
            "new-password");
        Assert.False(LocalStateService.HasAuthenticationRevocationTombstone(user.Username));
    }

    [Fact]
    public async Task GetSyncStatusAsync_ChangedRefreshSubjectRejectsSessionAndRevokesBothCaches()
    {
        var previousUser = CreateAdminUser(username: "previous-admin");
        previousUser.Permissions = ["System.Configure", "Customers.Write"];
        var responseUser = CreateAdminUser(
            username: "response-admin",
            tenantCode: TenantScopeCatalog.Itworld,
            officeCode: OfficeCodeCatalog.Itworld);
        responseUser.Permissions = ["System.Configure", "Users.Manage"];
        await using var fixture = await PersistentAuthFixture.CreateAsync();
        await fixture.SaveAuthenticationAsync(previousUser, "previous-password");
        await fixture.SaveAuthenticationAsync(responseUser, "response-password");

        var session = new SessionState();
        session.SetSession("old-token", previousUser, DateTime.UtcNow.AddMinutes(10));
        var api = new ErpApiClient(
            new HttpClient(new SubjectChangingRefreshHandler(responseUser))
            {
                BaseAddress = new Uri("http://localhost/")
            },
            session,
            fixture.Local);

        await Assert.ThrowsAsync<HttpRequestException>(() => api.GetSyncStatusAsync());

        Assert.False(session.IsLoggedIn);
        Assert.Null(await fixture.Local.GetCachedSessionAsync(previousUser.Username));
        Assert.Null(await fixture.Local.GetCachedSessionAsync(responseUser.Username));
        Assert.False(
            await fixture.Local.VerifyCachedSessionPasswordAsync(
                previousUser.Username,
                "previous-password"));
        Assert.False(
            await fixture.Local.VerifyCachedSessionPasswordAsync(
                responseUser.Username,
                "response-password"));
    }

    [Fact]
    public async Task ConcurrentWatermarkReads_CannotRestoreAuthorizationReducedByRefresh()
    {
        var originalUser = CreateAdminUser(username: "concurrent-reduction-user");
        originalUser.Permissions = ["System.Configure", "Customers.Write"];
        var reducedUser = new UserSessionDto
        {
            UserId = originalUser.UserId,
            Username = originalUser.Username,
            Role = "User",
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = ["Customers.Read"]
        };
        await using var fixture = await PersistentAuthFixture.CreateAsync();
        await fixture.SaveAuthenticationAsync(originalUser, "cached-password");

        var session = new SessionState();
        session.SetSession("old-token", originalUser, DateTime.UtcNow.AddMinutes(10));
        var api = new ErpApiClient(
            new HttpClient(new RefreshingSyncStatusHandler(reducedUser))
            {
                BaseAddress = new Uri("http://localhost/")
            },
            session,
            fixture.Local);

        var watermarkReached = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWatermark = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Local.AuthenticationWatermarkBeforeCommitHook = async () =>
        {
            watermarkReached.TrySetResult();
            await releaseWatermark.Task;
        };

        var watermarkRead =
            fixture.Local.GetCachedSessionAsync(originalUser.Username);
        await watermarkReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var refreshTask = api.GetSyncStatusAsync();
        Assert.False(refreshTask.IsCompleted);
        releaseWatermark.TrySetResult();
        Assert.NotNull(await watermarkRead);
        Assert.NotNull(await refreshTask);
        fixture.Local.AuthenticationWatermarkBeforeCommitHook = null;

        var cached = await fixture.Local.GetCachedSessionAsync(originalUser.Username);
        Assert.NotNull(cached);
        Assert.Equal("User", cached.Role);
        Assert.Equal(["Customers.Read"], cached.Permissions);
        Assert.DoesNotContain("System.Configure", cached.Permissions);
        Assert.DoesNotContain("Customers.Write", cached.Permissions);
    }

    [Fact]
    public async Task SaveSessionCacheAsync_DoesNotSaveUnrelatedAddedOrModifiedSettings()
    {
        var user = CreateAdminUser(username: "independent-save-user");
        await using var fixture = await PersistentAuthFixture.CreateAsync();
        await fixture.TrackModifiedSettingAsync(
            "Unrelated.Modified",
            "persisted-value",
            "pending-value");
        fixture.TrackUnsavedSetting("Unrelated.Added", "must-not-be-saved");

        await fixture.Local.SaveSessionCacheAsync(
            user.Username,
            user.Role,
            user.Permissions,
            user.TenantCode,
            user.ScopeType,
            user.OfficeCode,
            "cached-password");

        Assert.False(await fixture.IsSettingPersistedAsync("Unrelated.Added"));
        Assert.True(fixture.IsSettingTrackedAsAdded("Unrelated.Added"));
        Assert.Equal(
            "persisted-value",
            await fixture.ReadPersistedSettingAsync("Unrelated.Modified"));
        Assert.True(fixture.IsSettingTrackedAsModified("Unrelated.Modified"));
        Assert.NotNull(await fixture.Local.GetCachedSessionAsync(user.Username));
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
        Assert.Contains("담당지점", exception.Message);
        Assert.Contains("사업 범위", exception.Message);
        Assert.Contains("다시 로그인", exception.Message);
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
    public async Task GetSyncStatusAsync_RejectedRefreshRevokesPersistedOfflineAuthentication()
    {
        var user = CreateAdminUser();
        await using var fixture = await PersistentAuthFixture.CreateAsync();
        await fixture.Local.SaveSessionCacheAsync(
            user.Username,
            user.Role,
            user.Permissions,
            user.TenantCode,
            user.ScopeType,
            user.OfficeCode,
            "cached-password");
        await fixture.Local.SaveOfficeSyncCredentialAsync(
            user,
            user.Username,
            "cached-password");

        var session = new SessionState();
        session.SetSession("blocked-token", user, DateTime.UtcNow.AddDays(1));
        var handler = new RejectedRefreshHandler();
        var api = new ErpApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") },
            session,
            fixture.Local);

        await Assert.ThrowsAsync<HttpRequestException>(() => api.GetSyncStatusAsync());

        Assert.False(session.IsLoggedIn);
        Assert.Null(await fixture.Local.GetCachedSessionAsync(user.Username));
        Assert.False(
            await fixture.Local.VerifyCachedSessionPasswordAsync(
                user.Username,
                "cached-password"));
        Assert.Null(
            await fixture.Local.GetStoredSyncCredentialAsync(user.OfficeCode));
    }

    [Fact]
    public async Task RevokeRejectedAuthenticationCacheAsync_IgnoresCanceledCallerToken()
    {
        var user = CreateAdminUser();
        await using var fixture = await PersistentAuthFixture.CreateAsync();
        await fixture.SaveAuthenticationAsync(user, "cached-password");
        using var requestCancellation = new CancellationTokenSource();
        requestCancellation.Cancel();

        await fixture.Local.RevokeRejectedAuthenticationCacheAsync(
            user.Username,
            user.OfficeCode,
            requestCancellation.Token);

        Assert.True(requestCancellation.IsCancellationRequested);
        Assert.Null(await fixture.Local.GetCachedSessionAsync(user.Username));
        Assert.Null(await fixture.Local.GetStoredSyncCredentialAsync(user.OfficeCode));
        Assert.False(LocalStateService.HasAuthenticationRevocationTombstone(user.Username));
    }

    [Fact]
    public async Task GetSyncStatusAsync_RejectedRefreshDoesNotSaveUnrelatedTrackedChanges()
    {
        var user = CreateAdminUser();
        await using var fixture = await PersistentAuthFixture.CreateAsync();
        await fixture.SaveAuthenticationAsync(user, "cached-password");
        fixture.TrackUnsavedSetting("Unrelated.Pending", "must-not-be-saved");

        var session = new SessionState();
        session.SetSession("blocked-token", user, DateTime.UtcNow.AddDays(1));
        var api = new ErpApiClient(
            new HttpClient(new RejectedRefreshHandler()) { BaseAddress = new Uri("http://localhost/") },
            session,
            fixture.Local);

        await Assert.ThrowsAsync<HttpRequestException>(() => api.GetSyncStatusAsync());

        Assert.False(await fixture.IsSettingPersistedAsync("Unrelated.Pending"));
        Assert.True(fixture.IsSettingTrackedAsAdded("Unrelated.Pending"));
        Assert.Null(await fixture.Local.GetCachedSessionAsync(user.Username));
    }

    [Fact]
    public async Task GetSyncStatusAsync_RejectedRefreshRevokesOnlyMatchingUserCredential()
    {
        var rejectedUser = CreateAdminUser();
        var otherUser = CreateAdminUser(
            username: "other-session-user",
            tenantCode: TenantScopeCatalog.Itworld,
            officeCode: OfficeCodeCatalog.Itworld);
        var rejectedUserOtherOffice = CreateAdminUser(
            username: rejectedUser.Username,
            tenantCode: TenantScopeCatalog.UsenetGroup,
            officeCode: OfficeCodeCatalog.Yeonsu);
        await using var fixture = await PersistentAuthFixture.CreateAsync();
        await fixture.SaveAuthenticationAsync(rejectedUser, "rejected-password");
        await fixture.Local.SaveOfficeSyncCredentialAsync(
            rejectedUserOtherOffice,
            rejectedUserOtherOffice.Username,
            "rejected-other-office-password");
        await fixture.Local.SaveSessionCacheAsync(
            otherUser.Username,
            otherUser.Role,
            otherUser.Permissions,
            otherUser.TenantCode,
            otherUser.ScopeType,
            otherUser.OfficeCode,
            "other-password");
        await fixture.Local.SaveOfficeSyncCredentialAsync(
            otherUser,
            otherUser.Username,
            "other-password");

        var session = new SessionState();
        session.SetSession("blocked-token", rejectedUser, DateTime.UtcNow.AddDays(1));
        var api = new ErpApiClient(
            new HttpClient(new RejectedRefreshHandler()) { BaseAddress = new Uri("http://localhost/") },
            session,
            fixture.Local);

        await Assert.ThrowsAsync<HttpRequestException>(() => api.GetSyncStatusAsync());

        Assert.Null(await fixture.Local.GetCachedSessionAsync(rejectedUser.Username));
        Assert.Null(await fixture.Local.GetStoredSyncCredentialAsync(rejectedUser.OfficeCode));
        Assert.Null(await fixture.Local.GetStoredSyncCredentialAsync(rejectedUserOtherOffice.OfficeCode));
        Assert.NotNull(await fixture.Local.GetCachedSessionAsync(otherUser.Username));
        Assert.True(
            await fixture.Local.VerifyCachedSessionPasswordAsync(
                otherUser.Username,
                "other-password"));
        Assert.Equal(
            otherUser.Username,
            (await fixture.Local.GetStoredSyncCredentialAsync(otherUser.OfficeCode))?.Username);
    }

    [Fact]
    public async Task GetSyncStatusAsync_RevocationDatabaseFailureLeavesDurableOfflineBlock()
    {
        var user = CreateAdminUser(username: "locked-revocation-user");
        await using var fixture = await PersistentAuthFixture.CreateAsync();
        await fixture.SaveAuthenticationAsync(user, "cached-password");

        await using (var lockConnection = new SqliteConnection(fixture.ConnectionString))
        {
            await lockConnection.OpenAsync();
            await using var lockCommand = lockConnection.CreateCommand();
            lockCommand.CommandText = "BEGIN EXCLUSIVE;";
            await lockCommand.ExecuteNonQueryAsync();

            var session = new SessionState();
            session.SetSession("blocked-token", user, DateTime.UtcNow.AddDays(1));
            var api = new ErpApiClient(
                new HttpClient(new RejectedRefreshHandler()) { BaseAddress = new Uri("http://localhost/") },
                session,
                fixture.Local);

            await Assert.ThrowsAsync<HttpRequestException>(() => api.GetSyncStatusAsync());

            Assert.False(session.IsLoggedIn);
            Assert.True(LocalStateService.HasAuthenticationRevocationTombstone(user.Username));
            Assert.Null(await fixture.Local.GetCachedSessionAsync(user.Username));

            await using var rollbackCommand = lockConnection.CreateCommand();
            rollbackCommand.CommandText = "ROLLBACK;";
            await rollbackCommand.ExecuteNonQueryAsync();
        }

        // A later confirmed online login replaces the old cache and clears the fail-closed marker.
        await fixture.SaveAuthenticationAsync(user, "new-password");
        Assert.False(LocalStateService.HasAuthenticationRevocationTombstone(user.Username));
        Assert.True(
            await fixture.Local.VerifyCachedSessionPasswordAsync(
                user.Username,
                "new-password"));
    }

    [Fact]
    public async Task GetSyncStatusAsync_TransportFailureDoesNotRevokeOfflineAuthentication()
    {
        var user = CreateAdminUser(username: "transport-failure-user");
        await using var fixture = await PersistentAuthFixture.CreateAsync();
        await fixture.SaveAuthenticationAsync(user, "cached-password");

        var session = new SessionState();
        session.SetSession("temporarily-unreachable-token", user, DateTime.UtcNow.AddMinutes(10));
        var api = new ErpApiClient(
            new HttpClient(new TransportFailureHandler()) { BaseAddress = new Uri("http://localhost/") },
            session,
            fixture.Local);

        await Assert.ThrowsAsync<HttpRequestException>(() => api.GetSyncStatusAsync());

        Assert.True(session.IsLoggedIn);
        Assert.False(LocalStateService.HasAuthenticationRevocationTombstone(user.Username));
        Assert.NotNull(await fixture.Local.GetCachedSessionAsync(user.Username));
        Assert.True(
            await fixture.Local.VerifyCachedSessionPasswordAsync(
                user.Username,
                "cached-password"));
        Assert.NotNull(await fixture.Local.GetStoredSyncCredentialAsync(user.OfficeCode));
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

    private static UserSessionDto CreateAdminUser(
        string username = "session-refresh-user",
        string tenantCode = TenantScopeCatalog.UsenetGroup,
        string officeCode = OfficeCodeCatalog.Usenet) => new()
    {
        UserId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        Username = username,
        Role = "Admin",
        TenantCode = tenantCode,
        OfficeCode = officeCode,
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

    private sealed class CallerCanceledRefreshHandler(bool unauthorizedBeforeRefresh)
        : HttpMessageHandler
    {
        private readonly TaskCompletionSource _refreshStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task RefreshStarted => _refreshStarted.Task;

        public int SyncStatusRequestCount { get; private set; }

        public int RefreshRequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath;
            if (path == "/sync/status")
            {
                SyncStatusRequestCount++;
                return unauthorizedBeforeRefresh
                    ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new SyncStatusDto
                        {
                            CurrentServerRevision = 1,
                            ServerUtc = DateTime.UtcNow
                        })
                    };
            }

            if (path == "/auth/refresh")
            {
                RefreshRequestCount++;
                _refreshStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
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

    private sealed class RefreshUpgradeRequiredHandler(bool unauthorizedBeforeRefresh)
        : HttpMessageHandler
    {
        public List<RequestSnapshot> Requests { get; } = new();

        public int SyncStatusRequestCount { get; private set; }

        public int RefreshRequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            Requests.Add(new RequestSnapshot(path, request.Headers.Authorization?.Parameter));

            if (path == "/sync/status")
            {
                SyncStatusRequestCount++;
                return Task.FromResult(
                    unauthorizedBeforeRefresh
                        ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                        : new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = JsonContent.Create(new SyncStatusDto
                            {
                                CurrentServerRevision = 1,
                                ServerUtc = DateTime.UtcNow
                            })
                        });
            }

            if (path == "/auth/refresh")
            {
                RefreshRequestCount++;
                return Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.UpgradeRequired)
                    {
                        RequestMessage = request,
                        Content = JsonContent.Create(new
                        {
                            error = "client_upgrade_required",
                            upgrade = "georaeplan-client",
                            required = new
                            {
                                requiresUserAction = true,
                                policyVersion = 7,
                                minimumVersion = "1.1.700"
                            }
                        })
                    });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class RecordingUpgradeRequiredObserver
        : IDesktopUpgradeRequiredObserver
    {
        public DesktopClientUpgradeRequiredException? ObservedException { get; private set; }

        public bool IsLatched => ObservedException is not null;

        public Task ObserveAsync(
            DesktopClientUpgradeRequiredException exception,
            CancellationToken ct = default)
        {
            ObservedException = exception;
            return Task.CompletedTask;
        }
    }

    private sealed class SubjectChangingRefreshHandler(UserSessionDto responseUser)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/auth/refresh")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new LoginResponse
                    {
                        Token = "changed-subject-token",
                        ExpiresAtUtc = DateTime.UtcNow.AddDays(30),
                        User = responseUser
                    })
                });
            }

            if (request.RequestUri?.AbsolutePath == "/sync/status")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class TransportFailureHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => throw new HttpRequestException("simulated transport failure");
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

    private sealed class PersistentAuthFixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly LocalDbContext _db;

        private PersistentAuthFixture(
            string root,
            LocalDbContext db,
            LocalStateService local)
        {
            _root = root;
            _db = db;
            Local = local;
        }

        public LocalStateService Local { get; }

        public string ConnectionString
            => _db.Database.GetConnectionString()
               ?? throw new InvalidOperationException("Test database connection string is missing.");

        public async Task SaveAuthenticationAsync(UserSessionDto user, string password)
        {
            await Local.SaveSessionCacheAsync(
                user.Username,
                user.Role,
                user.Permissions,
                user.TenantCode,
                user.ScopeType,
                user.OfficeCode,
                password);
            await Local.SaveOfficeSyncCredentialAsync(user, user.Username, password);
        }

        public void TrackUnsavedSetting(string key, string value)
            => _db.Settings.Add(new LocalSetting { Key = key, Value = value });

        public bool IsSettingTrackedAsAdded(string key)
            => _db.ChangeTracker.Entries<LocalSetting>()
                .Any(entry => entry.Entity.Key == key && entry.State == EntityState.Added);

        public bool IsSettingTrackedAsModified(string key)
            => _db.ChangeTracker.Entries<LocalSetting>()
                .Any(entry => entry.Entity.Key == key && entry.State == EntityState.Modified);

        public async Task TrackModifiedSettingAsync(
            string key,
            string persistedValue,
            string pendingValue)
        {
            await Local.SetSettingAsync(key, persistedValue);
            var setting = await _db.Settings.FindAsync(key)
                ?? throw new InvalidOperationException($"Missing setting: {key}");
            setting.Value = pendingValue;
        }

        public async Task<bool> IsSettingPersistedAsync(string key)
        {
            await using var verificationDb = new LocalDbContext(
                new DbContextOptionsBuilder<LocalDbContext>()
                    .UseSqlite(ConnectionString)
                    .Options);
            return await verificationDb.Settings.AsNoTracking().AnyAsync(setting => setting.Key == key);
        }

        public async Task<string?> ReadPersistedSettingAsync(string key)
        {
            await using var verificationDb = new LocalDbContext(
                new DbContextOptionsBuilder<LocalDbContext>()
                    .UseSqlite(ConnectionString)
                    .Options);
            return await verificationDb.Settings
                .AsNoTracking()
                .Where(setting => setting.Key == key)
                .Select(setting => setting.Value)
                .SingleOrDefaultAsync();
        }

        public LocalDbContext CreateIndependentDbContext()
            => new(
                new DbContextOptionsBuilder<LocalDbContext>()
                    .UseSqlite(ConnectionString)
                    .Options);

        public static async Task<PersistentAuthFixture> CreateAsync()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "georaeplan-rejected-auth-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var options = new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite($"Data Source={Path.Combine(root, "auth-cache.db")}")
                .Options;
            var db = new LocalDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var local = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                new SessionState());
            return new PersistentAuthFixture(root, db, local);
        }

        public async ValueTask DisposeAsync()
        {
            await _db.DisposeAsync();
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup; SQLite can briefly retain a file handle.
            }
        }
    }

    public sealed record RequestSnapshot(string Path, string? BearerToken);
}
