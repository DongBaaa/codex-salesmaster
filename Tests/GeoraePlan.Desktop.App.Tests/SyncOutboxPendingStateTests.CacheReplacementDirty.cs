using System.Net;
using System.Net.Http;
using System.Text;
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
    public async Task CacheReplacement_PreservesDirtyCustomerWithoutCurrentEditPermission(bool arrivesDuringPull)
    {
        PrepareAppRoot("cache-replacement-revoked-permission");
        try
        {
            await using var db=new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var session=new SessionState();
            session.SetSession("test-token",new UserSessionDto { UserId=Guid.NewGuid(),Username="revoked-edit-user",Role="User",TenantCode="USENET_GROUP",OfficeCode="YEONSU",ScopeType="OfficeOnly" },DateTime.UtcNow.AddHours(1));
            var id=Guid.NewGuid();
            db.Customers.Add(new LocalCustomer { Id=id,NameOriginal="Pending customer",NameMatchKey="PENDING",TradeType="매출",TenantCode="USENET_GROUP",OfficeCode="USENET",ResponsibleOfficeCode="YEONSU",Notes="preserve pending edit",Revision=5,IsDirty=!arrivesDuringPull });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var handler=new CacheReplacementDirtyHandler(async () =>
            {
                if (arrivesDuringPull)
                {
                    await using var concurrentDb=new LocalDbContext();
                    var customer=await concurrentDb.Customers.SingleAsync(x=>x.Id==id);
                    customer.IsDirty=true;
                    await concurrentDb.SaveChangesAsync();
                }
            });
            using var sync=CreateSyncService(db,session,handler);
            Assert.False(await sync.ReplaceCurrentBusinessScopeCacheFromServerAsync());
            await using var verify=new LocalDbContext();
            var preserved=await verify.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(x=>x.Id==id);
            Assert.True(preserved.IsDirty);
            Assert.False(preserved.IsDeleted);
            Assert.Equal("preserve pending edit",preserved.Notes);
            Assert.Equal(arrivesDuringPull?1:0,handler.Requests);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT",null);
            SqliteConnection.ClearAllPools();
        }
    }

    private sealed class CacheReplacementDirtyHandler(Func<Task> duringPull) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get,request.Method);
            Assert.Equal("/sync/pull",request.RequestUri!.AbsolutePath);
            Requests++;
            await duringPull();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content=new StringContent("{\"currentServerRevision\":10}",Encoding.UTF8,"application/json") };
        }
    }
}
