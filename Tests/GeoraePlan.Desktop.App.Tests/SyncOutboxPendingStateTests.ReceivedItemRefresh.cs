using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed partial class SyncOutboxPendingStateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceivedItemAccess_ScopedRefreshClearsCompletedRequestButPreservesNewRequest(bool newerRequest)
    {
        PrepareAppRoot("received-item-scoped-refresh");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();
            var session = new SessionState();
            session.SetSession("synthetic", new UserSessionDto { UserId = Guid.NewGuid(), Username = "yeonsu",
                Role = DomainConstants.RoleUser, OfficeCode = OfficeCodeCatalog.Yeonsu,
                TenantCode = TenantScopeCatalog.UsenetGroup, ScopeType = TenantScopeCatalog.ScopeOfficeOnly }, DateTime.UtcNow.AddHours(1));
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            await local.MarkServerMirrorRefreshRequiredAsync();
            var customerId = Guid.NewGuid();
            var handler = new DelayedPullHandler(response: new SyncPullResponse { CurrentServerRevision = 100,
                Customers = [new CustomerDto { Id = customerId, NameOriginal = "destination", Revision = 100,
                    TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Yeonsu, ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu }] });
            using var sync = CreateSyncService(db, session, handler);
            var refresh = InvokeTryRefreshSharedMirrorCoreAsync(sync);
            await handler.PullReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (newerRequest) await local.MarkServerMirrorRefreshRequiredAsync();
            handler.ReleasePull();
            Assert.True(await refresh.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(await db.Customers.AnyAsync(x => x.Id == customerId));
            Assert.Equal(newerRequest, await local.IsServerMirrorRefreshRequiredAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }
}
