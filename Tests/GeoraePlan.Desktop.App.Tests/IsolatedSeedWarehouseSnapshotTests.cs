using System.Text.Json;
using GeoraePlan.Tools.SyncDiag;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using Xunit;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class IsolatedSeedWarehouseSnapshotTests
{
    [Theory]
    [InlineData(false,7)]
    [InlineData(false,0)]
    [InlineData(false,-2)]
    [InlineData(true,7)]
    public async Task Bootstrap_RequiresFreshTarget_AndFinalQuantitiesMustBeRestored(bool populated,decimal quantity)
    {
        await using var connection=new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db=new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var item=new LocalItem { NameOriginal="seed",NameMatchKey="SEED",TenantCode="USENET_GROUP",OfficeCode="USENET",CurrentStock=quantity,Revision=91,IsDirty=false };
        db.Items.Add(item);
        var category = new LocalItemCategoryOption {Name="seed category",Revision=8,IsDirty=false};
        db.ItemCategoryOptions.Add(category);
        item.CategoryName=category.Name;
        var stock=new LocalItemWarehouseStock { ItemId=item.Id,WarehouseCode="USENET_MAIN",Quantity=quantity,Revision=0,UpdatedAtUtc=DateTime.UtcNow };
        db.ItemWarehouseStocks.Add(stock);
        db.InventoryTransfers.Add(new LocalInventoryTransfer { TransferNumber="BOOT",Lines=[new LocalInventoryTransferLine {ItemId=item.Id,Quantity=3}] });
        await db.SaveChangesAsync();
        using var handler=new BootstrapHandler(item.Id,category.Id,populated);
        using var http=new HttpClient(handler) {BaseAddress=new Uri("http://127.0.0.1:19080/")};
        var api=new ErpApiClient(http,new SessionState());
        if(populated)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(()=>IsolatedSeedWarehouseSnapshot.BootstrapAsync(db,api));
            Assert.Equal(0,handler.Pushes);
            Assert.Equal(0,stock.Revision);
            return;
        }
        var expected=await IsolatedSeedWarehouseSnapshot.BootstrapAsync(db,api);
        Assert.Equal(1,handler.Pushes);
        Assert.Equal(Math.Max(0,quantity)+3,handler.ServerQuantity);
        Assert.Equal(quantity,stock.Quantity);
        Assert.Equal(quantity,expected.Single().Quantity);
        Assert.Equal(51,stock.Revision);
        Assert.Equal(52,item.Revision);
        Assert.False(item.IsDirty);
        Assert.Equal(53,category.Revision);
        Assert.False(category.IsDirty);
        await IsolatedSeedWarehouseSnapshot.VerifyFinalAsync(db,expected);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>IsolatedSeedWarehouseSnapshot.VerifyServerFinalAsync(db,api,expected));
        handler.ServerQuantity=quantity;
        await IsolatedSeedWarehouseSnapshot.VerifyServerFinalAsync(db,api,expected);
        stock.Quantity=10; await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>IsolatedSeedWarehouseSnapshot.VerifyFinalAsync(db,expected));
    }

    private sealed class BootstrapHandler(Guid itemId,Guid categoryId,bool populated):HttpMessageHandler
    {
        public int Pushes {get;private set;}
        public decimal ServerQuantity {get;set;}
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            object response;
            if(request.Method==HttpMethod.Post)
            {
                var push=(await request.Content!.ReadFromJsonAsync<SyncPushRequest>(cancellationToken:ct))!;
                Assert.Empty(push.InventoryTransfers); Assert.Empty(push.Invoices);
                Assert.Equal(categoryId,push.ItemCategoryOptions.Single().Id);
                Assert.Equal(0,push.ItemWarehouseStocks.Single().Revision);
                ServerQuantity=push.ItemWarehouseStocks.Single().Quantity; Pushes++;
                response=new SyncPushResult {AcceptedItemWarehouseStockKeys=[new() {ItemId=itemId,WarehouseCode="USENET_MAIN"}]};
            }
            else
                response=Pushes==0 && !populated ? new SyncPullResponse() : new SyncPullResponse {
                    Items=[new() {Id=itemId,Revision=52}],
                    ItemCategoryOptions=[new() {Id=categoryId,Revision=53}],
                    ItemWarehouseStocks=[new() {ItemId=itemId,WarehouseCode="USENET_MAIN",Quantity=ServerQuantity,Revision=51}] };
            return new HttpResponseMessage(HttpStatusCode.OK) {Content=JsonContent.Create(response,response.GetType())};
        }
    }

    [Theory]
    [InlineData("prepare")]
    [InlineData("rollback")]
    [InlineData("invalid")]
    public async Task FreshSeed_PreservesQuantitiesOwnershipAndDocuments(string mode)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new LocalDbContext(
            new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var item = new LocalItem { Id=Guid.NewGuid(), NameOriginal="seed snapshot",
            NameMatchKey="SEEDSNAPSHOT", OfficeCode="USENET", TenantCode="USENET_GROUP",
            CurrentStock=10, Revision=91, IsDirty=false };
        db.Items.Add(item);
        var now = new DateTime(2026,9,15,0,0,0,DateTimeKind.Utc);
        db.ItemWarehouseStocks.AddRange(
            new LocalItemWarehouseStock { ItemId=item.Id, WarehouseCode="USENET_MAIN", Quantity=7, Revision=51, UpdatedAtUtc=now },
            new LocalItemWarehouseStock { ItemId=item.Id, WarehouseCode="YEONSU_MAIN", Quantity=3, Revision=mode=="invalid" ? -1 : 52, UpdatedAtUtc=now });
        var transfer = new LocalInventoryTransfer { TransferNumber="SEED-3", Revision=99,
            IsDirty=false, TransferStatus="수령완료", Lines=[new LocalInventoryTransferLine {
                ItemId=item.Id, Quantity=3, ReceivedQuantity=3 }] };
        db.InventoryTransfers.Add(transfer);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var beforeItem=JsonSerializer.Serialize(await db.Items.AsNoTracking().SingleAsync());
        var beforeTransfer=JsonSerializer.Serialize(await db.InventoryTransfers.AsNoTracking().SingleAsync());
        await using (var tx=await db.Database.BeginTransactionAsync())
        {
            if (mode=="invalid")
                await Assert.ThrowsAsync<InvalidOperationException>(() => IsolatedSeedWarehouseSnapshot.PrepareForFreshServerAsync(db));
            else
            {
                Assert.Equal(2, await IsolatedSeedWarehouseSnapshot.PrepareForFreshServerAsync(db));
                Assert.Equal(0, await IsolatedSeedWarehouseSnapshot.PrepareForFreshServerAsync(db));
            }
            if (mode=="prepare") await tx.CommitAsync();
            else await tx.RollbackAsync();
        }
        var stocks=await db.ItemWarehouseStocks.AsNoTracking().OrderBy(x=>x.WarehouseCode).ToListAsync();
        Assert.Equal(new decimal[] {7,3}, stocks.Select(x=>x.Quantity));
        Assert.All(stocks, x=>Assert.Equal(now,x.UpdatedAtUtc));
        Assert.Equal(mode=="prepare" ? new long[] {0,0} : new long[] {51,mode=="invalid" ? -1 : 52}, stocks.Select(x=>x.Revision));
        Assert.Equal(beforeItem,JsonSerializer.Serialize(await db.Items.AsNoTracking().SingleAsync()));
        Assert.Equal(beforeTransfer,JsonSerializer.Serialize(await db.InventoryTransfers.AsNoTracking().SingleAsync()));
        Assert.Equal(3, (await db.InventoryTransferLines.SingleAsync()).ReceivedQuantity);
        Assert.Empty(await db.SyncOutboxEntries.ToListAsync());
    }
}
