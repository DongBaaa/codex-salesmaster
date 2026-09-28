using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class HiddenTransactionSyncTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Pull_TransactionHidesAmountsAndPreservesPendingMemo(bool dirty)
    {
        await using var f=await Fixture.Create(dirty);
        var incoming=LocalMappings.ToDto(f.Row);incoming.Revision=12;incoming.Memo="server memo";incoming.ReceiptTotal=null;
        await f.Pull(incoming);f.Db.ChangeTracker.Clear();
        var saved=await f.Db.Transactions.SingleAsync();
        Assert.True(saved.AmountsHidden);Assert.Equal(dirty,saved.IsDirty);
        Assert.Equal(dirty?11:12,saved.Revision);Assert.Equal(dirty?"pending memo":"server memo",saved.Memo);
        foreach(var p in typeof(TransactionDto).GetProperties().Where(p=>p.PropertyType==typeof(decimal?)))
            Assert.Null(p.GetValue(LocalMappings.ToDto(saved)));
        Assert.Empty(await f.Db.Payments.ToListAsync());
        if(dirty)
        {
            Assert.Equal(1234m,saved.ReceiptTotal);Assert.Equal(1234m,saved.CashReceipt);
            incoming.ReceiptTotal=9999;incoming.CashReceipt=9999;
            await f.Pull(incoming);f.Db.ChangeTracker.Clear();saved=await f.Db.Transactions.SingleAsync();
            Assert.True(saved.AmountsHidden);Assert.True(saved.IsDirty);Assert.Equal(11,saved.Revision);
            Assert.Equal("pending memo",saved.Memo);Assert.Equal(1234m,saved.ReceiptTotal);
        }
    }

    [Fact]
    public async Task PendingPush_TransactionReplacesOldMoneyPayloadAfterHiddenPull()
    {
        await using var f=await Fixture.Create(true);
        await Assert.ThrowsAnyAsync<Exception>(()=>f.Push());
        var before=Assert.Single(f.Handler.Requests.Last().Transactions);Assert.Equal(1234m,before.ReceiptTotal);
        var incoming=LocalMappings.ToDto(f.Row);incoming.ReceiptTotal=null;
        await f.Pull(incoming);var count=f.Handler.Requests.Count;
        await Assert.ThrowsAnyAsync<Exception>(()=>f.Push());
        Assert.True(f.Handler.Requests.Count>count);
        foreach(var request in f.Handler.Requests.Skip(count))
        {
            var dto=Assert.Single(request.Transactions);
            foreach(var p in typeof(TransactionDto).GetProperties().Where(p=>p.PropertyType==typeof(decimal?)))Assert.Null(p.GetValue(dto));
            Assert.Equal("pending memo",dto.Memo);Assert.NotEqual(before.MutationId,dto.MutationId);
        }
        f.Db.ChangeTracker.Clear();var saved=await f.Db.Transactions.SingleAsync();
        Assert.True(saved.IsDirty);Assert.Equal(1234m,saved.ReceiptTotal);Assert.Equal("pending memo",saved.Memo);
    }

    private sealed class Fixture:IAsyncDisposable
    {
        private readonly string? previousRoot=Environment.GetEnvironmentVariable("GEORAEPLAN_APP_ROOT");
        public LocalDbContext Db=null!; public SyncService Sync=null!; public LocalTransaction Row=null!;
        public CapturePushHandler Handler=new();public ErpApiClient Api=null!;public SessionState Session=null!;
        public static async Task<Fixture> Create(bool dirty)
        {
            var f=new Fixture();var root=Path.Combine(Path.GetTempPath(),"georaeplan-hidden-transaction-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT",root);
            f.Db=new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite("Data Source="+Path.Combine(root,"isolated.db")+";Pooling=False").Options);
            await f.Db.Database.EnsureCreatedAsync();
            var customer=new LocalCustomer {Id=Guid.NewGuid(),NameOriginal="transaction customer",NameMatchKey="TRANSACTIONCUSTOMER",TenantCode=TenantScopeCatalog.UsenetGroup,OfficeCode=OfficeCodeCatalog.Usenet};
            f.Row=new LocalTransaction {Id=Guid.NewGuid(),CustomerId=customer.Id,TenantCode=TenantScopeCatalog.UsenetGroup,OfficeCode=OfficeCodeCatalog.Usenet,ResponsibleOfficeCode=OfficeCodeCatalog.Usenet,
                TransactionKind="일반수금",Memo="pending memo",CashReceipt=1234,ReceiptTotal=1234,Revision=11,IsDirty=dirty};
            f.Db.AddRange(customer,f.Row);await f.Db.SaveChangesAsync();
            f.Session=new SessionState();f.Session.SetOfflineSession(new UserSessionDto {UserId=Guid.NewGuid(),Username="transaction-sync",Role=DomainConstants.RoleAdmin,TenantCode=TenantScopeCatalog.UsenetGroup,OfficeCode=OfficeCodeCatalog.Usenet,ScopeType=TenantScopeCatalog.ScopeAdmin});
            var dispatcher=new SyncRequestDispatcher();var local=new LocalStateService(f.Db,new OfficeAccessService(),dispatcher,f.Session);
            f.Api=new ErpApiClient(new HttpClient(f.Handler){BaseAddress=new Uri("http://localhost/")},f.Session);
            f.Sync=new SyncService(f.Db,local,new RentalStateService(f.Db,local),f.Api,f.Session,dispatcher,new SyncDiagnosticsService(f.Session));return f;
        }
        public Task Push()=>(Task)typeof(SyncService).GetMethod("PushDirtyAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(Sync,[Api,Session,true,CancellationToken.None])!;
        public Task Pull(TransactionDto dto)=>(Task)typeof(SyncService).GetMethod("ApplyPullAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(Sync,[new SyncPullResponse {CurrentServerRevision=12,Transactions=[dto]},0L,CancellationToken.None,false])!;
        public async ValueTask DisposeAsync(){Sync?.Dispose();if(Db is not null)await Db.DisposeAsync();SqliteConnection.ClearAllPools();Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT",previousRoot);}
    }
    private sealed class CapturePushHandler:HttpMessageHandler
    {
        public List<SyncPushRequest> Requests {get;}=[];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        {
            Assert.Equal("/sync/push",request.RequestUri!.AbsolutePath);
            Requests.Add((await request.Content!.ReadFromJsonAsync<SyncPushRequest>(cancellationToken:cancellationToken))!);
            return new HttpResponseMessage(HttpStatusCode.Forbidden){Content=JsonContent.Create(new {message="isolated pending request"})};
        }
    }
}
