using System;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed partial class SyncOutboxPendingStateTests
{
    [Fact]
    public async Task OfflineAuthentication_OnlineResponseIdentitySurvivesFreshContextAndWatermark()
    {
        PrepareAppRoot("offline-explicit-identity");
        try
        {
            var user = CreateCustomerScopeSession().User!;
            await using (var db = new LocalDbContext())
            {
                await db.Database.EnsureDeletedAsync(); await db.Database.EnsureCreatedAsync();
                var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), new SessionState());
                await local.SaveSessionCacheAsync(user, "isolated-test-password");
            }
            await using var reopened = new LocalDbContext();
            var next = new LocalStateService(reopened, new OfficeAccessService(), new SyncRequestDispatcher(), new SessionState());
            for (var i = 0; i < 2; i++)
            {
                var cached = await next.AuthenticateCachedSessionAsync(user.Username, "isolated-test-password");
                Assert.NotNull(cached); Assert.Equal(user.UserId, cached.User.UserId);
            }
            await next.RefreshCachedSessionAfterOnlineValidationAsync(user.Username, user);
            Assert.Equal(user.UserId, (await next.GetCachedSessionAsync(user.Username))!.UserId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData("UserId")]
    [InlineData("SchemaVersion")]
    public async Task OfflineAuthentication_IdentityTamperAndPreviousFormatAreRejected(string field)
    {
        PrepareAppRoot("offline-identity-tamper");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync(); await db.Database.EnsureCreatedAsync();
            var user = CreateCustomerScopeSession().User!;
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), new SessionState());
            await local.SaveSessionCacheAsync(user, "isolated-test-password");
            var value = field == "UserId" ? Guid.NewGuid().ToString("D") : "4";
            await local.SetSettingAsync("CachedSession.scope-user." + field, value);
            await local.SetSettingAsync("CachedSession_" + field, value);
            Assert.Null(await local.AuthenticateCachedSessionAsync(user.Username, "isolated-test-password"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task OfflineAuthentication_RecreatedUserDoesNotInheritPreviousPasswordProof()
    {
        PrepareAppRoot("offline-recreated-identity");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync(); await db.Database.EnsureCreatedAsync();
            var user = CreateCustomerScopeSession().User!;
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), new SessionState());
            await local.SaveSessionCacheAsync(user, "isolated-test-password");
            user.UserId = Guid.NewGuid();
            await local.RefreshCachedSessionAfterOnlineValidationAsync(user.Username, user);
            Assert.Null(await local.AuthenticateCachedSessionAsync(user.Username, "isolated-test-password"));
            await local.SaveSessionCacheAsync(user, "new-isolated-password");
            Assert.Equal(user.UserId, (await local.AuthenticateCachedSessionAsync(user.Username, "new-isolated-password"))!.User.UserId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task OfflineAuthentication_UnknownIdentityCannotBypassCustomerScope()
    {
        PrepareAppRoot("offline-unknown-identity");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync(); await db.Database.EnsureCreatedAsync();
            var session = CreateCustomerScopeSession();
            var user = session.User!;
            user.UserId = Guid.Empty;
            session.SetOfflineSession(user);
            var customerId = Guid.NewGuid();
            db.Customers.Add(CustomerScopeFixture(customerId, 5, false));
            await db.SaveChangesAsync();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            Assert.Empty(await local.GetCustomersAsync(session));
            Assert.Null(await local.GetCustomerForOperationalSelectionAsync(customerId, session));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task OfflineAuthentication_RetainsServerCustomerExclusionForSameUser()
    {
        PrepareAppRoot("offline-customer-identity");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var session = CreateCustomerScopeSession();
            var serverUserId = session.User!.UserId;
            var customerId = Guid.NewGuid();
            db.Customers.Add(CustomerScopeFixture(customerId, 5, true));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            await local.SaveSessionCacheAsync(session.User.Username, session.User.Role, session.User.Permissions,
                session.User.TenantCode, session.User.ScopeType, session.OfficeCode, "isolated-test-password");
            using (var sync = CreateSyncService(db, session))
                await InvokeApplyPullAndUpdateRevisionAsync(sync, new SyncPullResponse
                {
                    CurrentServerRevision = 6,
                    CustomerScopeSnapshot = CustomerScopeSnapshotFor(session)
                }, 5);
            Assert.DoesNotContain(await local.GetCustomersAsync(session), row => row.Id == customerId);
            var cached = await local.AuthenticateCachedSessionAsync(session.User.Username, "isolated-test-password");
            Assert.NotNull(cached);
            session.SetOfflineSession(cached.User);
            Assert.DoesNotContain(await local.GetCustomersAsync(session), row => row.Id == customerId);
            Assert.Equal(serverUserId, session.User!.UserId);
            Assert.True((await db.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync()).IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }
}
