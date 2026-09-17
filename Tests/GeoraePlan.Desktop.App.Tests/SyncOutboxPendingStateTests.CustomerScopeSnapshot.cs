using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
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
    public async Task CustomerScopeSnapshot_ExcludesRevokedCustomerWithoutDeletingEditsOrContracts(bool dirty)
    {
        PrepareAppRoot("customer-scope-revocation");
        try
        {
            await using var db=new LocalDbContext();
            await db.Database.EnsureDeletedAsync();await db.Database.EnsureCreatedAsync();
            var session=CreateCustomerScopeSession();
            var id=Guid.NewGuid(); var newId=Guid.NewGuid(); var contractId=Guid.NewGuid();
            db.Customers.AddRange(CustomerScopeFixture(id,5,dirty),CustomerScopeFixture(newId,0,true));
            db.CustomerContracts.Add(new LocalCustomerContract { Id=contractId,CustomerId=id,FileName="preserved.pdf",FileContent=[1,2,3],Revision=5,IsDirty=false });
            await db.SaveChangesAsync();db.ChangeTracker.Clear();
            using var sync=CreateSyncService(db,session);
            var local=new LocalStateService(db,new OfficeAccessService(),new SyncRequestDispatcher(),session);
            Assert.Contains(await local.GetCustomersAsync(session),x=>x.Id==id);
            await InvokeApplyPullAndUpdateRevisionAsync(sync,new SyncPullResponse {CurrentServerRevision=6,CustomerScopeSnapshot=CustomerScopeSnapshotFor(session)},5);
            Assert.DoesNotContain(await local.GetCustomersAsync(session),x=>x.Id==id);
            Assert.Contains(await local.GetCustomersForOperationalSelectionAsync(session),x=>x.Id==newId);
            Assert.Null(await local.GetCustomerForOperationalSelectionAsync(id,session));
            Assert.Empty(await local.GetCustomerContractsAsync(id,session));
            var preserved=await db.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(x=>x.Id==id);
            Assert.Equal(dirty,preserved.IsDirty);Assert.False(preserved.IsDeleted);
            Assert.Equal("preserve my edit",preserved.Notes);Assert.Equal(5,preserved.Revision);
            Assert.Equal(new byte[]{1,2,3},(await db.CustomerContracts.IgnoreQueryFilters().AsNoTracking().SingleAsync(x=>x.Id==contractId)).FileContent);
            await using(var reopened=new LocalDbContext())
            {
                var next=new LocalStateService(reopened,new OfficeAccessService(),new SyncRequestDispatcher(),session);
                Assert.DoesNotContain(await next.GetCustomersAsync(session),x=>x.Id==id);
                var other=CreateCustomerScopeSession();
                Assert.Contains(await next.GetCustomersAsync(other),x=>x.Id==id);
            }
            // A missing old-server field must not erase an already-known exclusion.
            await InvokeApplyPullAndUpdateRevisionAsync(sync,new SyncPullResponse {CurrentServerRevision=6},6);
            Assert.DoesNotContain(await local.GetCustomersAsync(session),x=>x.Id==id);
            // Permission metadata may change even when the business cursor does not.
            await InvokeApplyPullAndUpdateRevisionAsync(sync,new SyncPullResponse {CurrentServerRevision=6,CustomerScopeSnapshot=CustomerScopeSnapshotFor(session,id)},6);
            Assert.Contains(await local.GetCustomersAsync(session),x=>x.Id==id);
            preserved=await db.Customers.AsNoTracking().SingleAsync(x=>x.Id==id);
            Assert.Equal(dirty,preserved.IsDirty);Assert.Equal("preserve my edit",preserved.Notes);
        }
        finally {Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT",null);SqliteConnection.ClearAllPools();}
    }

    [Theory]
    [InlineData("user")][InlineData("tenant")][InlineData("office")]
    [InlineData("scope")][InlineData("version")][InlineData("duplicate")][InlineData("null")]
    [InlineData("missing")][InlineData("empty-id")]
    public async Task CustomerScopeSnapshot_InvalidAuthorityRollsBackDataAndCursor(string malformed)
    {
        PrepareAppRoot("customer-scope-invalid-authority");
        try
        {
            await using var db=new LocalDbContext();await db.Database.EnsureDeletedAsync();await db.Database.EnsureCreatedAsync();
            var session=CreateCustomerScopeSession();var id=Guid.NewGuid();
            db.Customers.Add(CustomerScopeFixture(id,5,false));await db.SaveChangesAsync();db.ChangeTracker.Clear();
            var scope=CustomerScopeSnapshotFor(session);
            switch(malformed) {
                case "user":scope.UserId=Guid.NewGuid();break;
                case "tenant":scope.TenantCode="ITWORLD";break;
                case "office":scope.OfficeCode="ITWORLD";break;
                case "scope":scope.ScopeType="Admin";break;
                case "version":scope.Version=2;break;
                case "duplicate":scope.VisibleCustomerIds=[id,id];break;
                case "null":scope.VisibleCustomerIds=null!;break;
                case "missing":scope=JsonSerializer.Deserialize<CustomerScopeSnapshotDto>(JsonSerializer.Serialize(new {scope.Version,scope.UserId,scope.TenantCode,scope.OfficeCode,scope.ScopeType}))!;break;
                case "empty-id":scope.VisibleCustomerIds=[Guid.Empty];break;
            }
            using var sync=CreateSyncService(db,session);
            await Assert.ThrowsAsync<InvalidDataException>(()=>InvokeApplyPullAndUpdateRevisionAsync(sync,new SyncPullResponse {CurrentServerRevision=6,CustomerScopeSnapshot=scope},5));
            Assert.False(await db.Settings.AsNoTracking().AnyAsync(x=>x.Key.StartsWith("Sync.CustomerScopeExclusion.")));
            Assert.Null(await db.Settings.AsNoTracking().SingleOrDefaultAsync(x=>x.Key=="LastSyncRevision"));
            Assert.Equal(5,(await db.Customers.AsNoTracking().SingleAsync(x=>x.Id==id)).Revision);
        }
        finally {Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT",null);SqliteConnection.ClearAllPools();}
    }

    [Fact]
    public async Task CustomerScopeSnapshot_AdminSelectionUsesAuthenticatedTenantAndKeepsDatabaseExclusionsSeparate()
    {
        PrepareAppRoot("customer-scope-business-database");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var session = CreateAdminSession();
            session.SetBusinessDatabase(TenantScopeCatalog.Itworld);
            Assert.Equal("USENET_GROUP", session.AuthenticatedTenantCode);
            Assert.Equal("ITWORLD", session.TenantCode);
            var id = Guid.NewGuid();
            var customer = CustomerScopeFixture(id, 5, false);
            customer.TenantCode = customer.OfficeCode = customer.ResponsibleOfficeCode = "ITWORLD";
            db.Customers.Add(customer);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            using var sync = CreateSyncService(db, session);
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            await InvokeApplyPullAndUpdateRevisionAsync(sync, new SyncPullResponse
            {
                CurrentServerRevision = 6,
                CustomerScopeSnapshot = CustomerScopeSnapshotFor(session)
            }, 5);
            Assert.DoesNotContain(await local.GetCustomersAsync(session), x => x.Id == id);
            session.SetBusinessDatabase(TenantScopeCatalog.UsenetGroup);
            Assert.Contains(await local.GetCustomersAsync(session), x => x.Id == id);
            session.SetBusinessDatabase(TenantScopeCatalog.Itworld);
            Assert.DoesNotContain(await local.GetCustomersAsync(session), x => x.Id == id);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CustomerScopeSnapshot_HttpPullDeserializesAndRejectsChangedOwner(bool changeOwner)
    {
        PrepareAppRoot("customer-scope-public-pull");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var session = CreateCustomerScopeSession();
            var id = Guid.NewGuid();
            db.Customers.Add(CustomerScopeFixture(id, 5, false));
            db.Settings.Add(new LocalSetting { Key = "LastSyncRevision", Value = "5" });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var response = new SyncPullResponse
            {
                CurrentServerRevision = 5,
                CustomerScopeSnapshot = CustomerScopeSnapshotFor(session)
            };
            var handler = new CustomerScopePullHandler(response, () =>
            {
                if (changeOwner)
                {
                    var next = CreateCustomerScopeSession();
                    session.SetSession("next-token", next.User!, DateTime.UtcNow.AddHours(1));
                }
            });
            using var sync = CreateSyncService(db, session, handler);
            Assert.Equal(!changeOwner, await InvokePullNewCoreAsync(sync, false));
            Assert.Equal(1, handler.Requests);
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            Assert.Equal(changeOwner, (await local.GetCustomersAsync(session)).Any(x => x.Id == id));
            Assert.Equal(!changeOwner, await db.Settings.AsNoTracking()
                .AnyAsync(x => x.Key.StartsWith("Sync.CustomerScopeExclusion.")));
            Assert.Equal("5", (await db.Settings.AsNoTracking().SingleAsync(x => x.Key == "LastSyncRevision")).Value);
            if (!changeOwner)
            {
                Assert.True(sync.LastPullChangeCount > 0);
                Assert.True(await InvokePullNewCoreAsync(sync, false));
                Assert.Equal(0, sync.LastPullChangeCount);
                response.CustomerScopeSnapshot.VisibleCustomerIds.Add(id);
                Assert.True(await InvokePullNewCoreAsync(sync, false));
                Assert.True(sync.LastPullChangeCount > 0);
                Assert.Contains(await local.GetCustomersAsync(session), x => x.Id == id);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private sealed class CustomerScopePullHandler(SyncPullResponse response, Action duringPull) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/sync/pull", request.RequestUri!.AbsolutePath);
            Requests++;
            duringPull();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(response)
            });
        }
    }

    private static SessionState CreateCustomerScopeSession()
    {
        var session=new SessionState();session.SetSession("test-token",new UserSessionDto { UserId=Guid.NewGuid(),Username="scope-user",Role="User",TenantCode="USENET_GROUP",OfficeCode="YEONSU",ScopeType="OfficeOnly",Permissions=[AppPermissionNames.CustomerEdit] },DateTime.UtcNow.AddHours(1));return session;
    }
    private static CustomerScopeSnapshotDto CustomerScopeSnapshotFor(SessionState session,params Guid[] ids)
        => new() {UserId=session.User!.UserId,TenantCode=session.AuthenticatedTenantCode,OfficeCode=session.OfficeCode,ScopeType=session.ScopeType,VisibleCustomerIds=ids.ToList()};
    private static LocalCustomer CustomerScopeFixture(Guid id,long revision,bool dirty)
        => new() {Id=id,NameOriginal="Scope fixture",NameMatchKey=id.ToString(),TradeType="매출",TenantCode="USENET_GROUP",OfficeCode="USENET",ResponsibleOfficeCode="YEONSU",Notes="preserve my edit",Revision=revision,IsDirty=dirty};
}
