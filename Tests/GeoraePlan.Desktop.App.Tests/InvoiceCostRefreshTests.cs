using System.Net.Http;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;
namespace GeoraePlan.Desktop.App.Tests;
public sealed class InvoiceCostRefreshTests
{
    [Theory]
    [InlineData("USENET","identical")]
    [InlineData("USENET","sales-quantity")]
    [InlineData("USENET","purchase-cost")]
    [InlineData("YEONSU","identical")]
    [InlineData("YEONSU","sales-quantity")]
    [InlineData("YEONSU","purchase-cost")]
    [InlineData("ITWORLD","identical")]
    [InlineData("ITWORLD","sales-quantity")]
    [InlineData("ITWORLD","purchase-cost")]
    public async Task LedgerUsesCurrentPulledCostInputs(string office,string change)
    {
        await using var f=await Fixture.CreateAsync(office);
        var purchase=await f.InvoiceAsync();
        purchase.IsConfirmed=true;
        purchase.InvoiceDate=DateOnly.FromDateTime(DateTime.Today).AddDays(-2);
        purchase.PurchaseReceivingStatus=InvoiceReceivingStatuses.Confirmed;
        purchase.Lines.Single().Quantity=10;
        purchase.Lines.Single().LineAmount=100;
        purchase.TotalAmount=110;purchase.SupplyAmount=100;purchase.VatAmount=10;
        var itemId=purchase.Lines.Single().ItemId!.Value;
        var salesId=Guid.NewGuid();
        var sales=new LocalInvoice {Id=salesId,VersionGroupId=salesId,VersionNumber=1,
            IsLatestVersion=true,IsConfirmed=true,CustomerId=purchase.CustomerId,
            TenantCode=purchase.TenantCode,OfficeCode=purchase.OfficeCode,ResponsibleOfficeCode=office,
            SourceWarehouseCode=office+"_MAIN",VoucherType=VoucherType.Sales,
            InvoiceDate=purchase.InvoiceDate.AddDays(1),Revision=12346,IsDirty=false,
            TotalAmount=66,SupplyAmount=60,VatAmount=6,
            CreatedAtUtc=Fixture.SavedAt,UpdatedAtUtc=Fixture.SavedAt,
            Lines=[new LocalInvoiceLine {Id=Guid.NewGuid(),InvoiceId=salesId,ItemId=itemId,
                ItemNameOriginal="cost diagnosis",Quantity=2,UnitPrice=30,LineAmount=60}]};
        var item=await f.Db.Items.SingleAsync();item.PurchasePrice=10;
        f.Db.Invoices.Add(sales);await f.Db.SaveChangesAsync();
        await Rebuild(f,office);
        var before=await Ledger(f);Assert.Equal(20,before.PurchaseAmount);Assert.Equal(40,before.ProfitAmount);
        purchase=await f.InvoiceAsync();
        sales=await f.Db.Invoices.Include(x=>x.Lines).Include(x=>x.Payments).SingleAsync(x=>x.Id==salesId);
        var purchaseDto=LocalMappings.ToDto(purchase);var salesDto=LocalMappings.ToDto(sales);
        if(change=="sales-quantity")
        {salesDto.Lines[0].Quantity=4;salesDto.Lines[0].LineAmount=120;salesDto.TotalAmount=132;salesDto.SupplyAmount=120;salesDto.VatAmount=12;salesDto.Revision++;}
        if(change=="purchase-cost")
        {purchaseDto.Lines[0].UnitPrice=20;purchaseDto.Lines[0].LineAmount=200;purchaseDto.TotalAmount=220;purchaseDto.SupplyAmount=200;purchaseDto.VatAmount=20;purchaseDto.Revision++;}
        var expectedStock=change=="sales-quantity"?6m:8m;
        var itemDto=LocalMappings.ToDto(await f.Db.Items.SingleAsync());itemDto.CurrentStock=expectedStock;itemDto.Revision=13000;
        var pull=new SyncPullResponse {CurrentServerRevision=14000,
            Invoices=[purchaseDto,salesDto],Items=[itemDto],
            ItemWarehouseStocks=[new ItemWarehouseStockDto {ItemId=itemId,WarehouseCode=office+"_MAIN",Quantity=expectedStock,Revision=13000,UpdatedAtUtc=Fixture.SavedAt.AddHours(1)}]};
        var apply=typeof(SyncService).GetMethod("ApplyPullAsync",BindingFlags.NonPublic|BindingFlags.Instance)!;
        await (Task)apply.Invoke(f.sync,[pull,12346L,CancellationToken.None,true])!;
        f.Db.ChangeTracker.Clear();
        Assert.Equal("14000",await f.Local.GetSettingAsync("LastSyncRevision"));
        Assert.Equal(expectedStock,(await f.Db.ItemWarehouseStocks.SingleAsync()).Quantity);
        Assert.False(await f.Db.Invoices.AnyAsync(x=>x.IsDirty));
        var current=await Ledger(f);
        var snapshotConnection=(SqliteConnection)f.Db.Database.GetDbConnection();
        var firstPull=await InventoryCostRefreshProtectionTests.Snapshot(snapshotConnection);
        await (Task)apply.Invoke(f.sync,[pull,14000L,CancellationToken.None,true])!;
        var repeatedPull=await InventoryCostRefreshProtectionTests.Snapshot(snapshotConnection);
        foreach(var table in firstPull.Keys)Assert.Equal(firstPull[table],repeatedPull[table]);
        var status=(await f.Db.Invoices.SingleAsync(x=>x.Id==salesId)).CostStatus;
        var currentQty=(await f.Db.InvoiceLines.SingleAsync(x=>x.InvoiceId==salesId)).Quantity;
        var currentPrice=(await f.Db.InvoiceLines.SingleAsync(x=>x.InvoiceId==purchase.Id)).UnitPrice;
        await Rebuild(f,office);
        var recalculated=await Ledger(f);
        var expectedCost=change=="identical"?20m:40m;
        var expectedSales=change=="sales-quantity"?120m:60m;
        Assert.Equal(expectedCost,recalculated.PurchaseAmount);
        Assert.Equal(expectedSales-expectedCost,recalculated.ProfitAmount);
        var result=new {office,change,currentQty,currentPrice,status,expectedStock,
            observedCost=current.PurchaseAmount,observedProfit=current.ProfitAmount,
            expectedCost,expectedProfit=expectedSales-expectedCost,
            recalculatedCost=recalculated.PurchaseAmount,dirty=await f.Db.Invoices.CountAsync(x=>x.IsDirty)};
        var root=System.Environment.GetEnvironmentVariable("AUDIT_CASE");
        if (!string.IsNullOrWhiteSpace(root)) await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(root,office+"-"+change+".json"),System.Text.Json.JsonSerializer.Serialize(result));
        Assert.Equal(expectedCost,current.PurchaseAmount);
        Assert.Equal(expectedSales-expectedCost,current.ProfitAmount);
    }
    private static async Task Rebuild(Fixture f,string office)
    {
        var rebuild=typeof(LocalStateService).GetMethod("RebuildInventorySnapshotsAsync",BindingFlags.Instance|BindingFlags.NonPublic)!;
        await (Task)rebuild.Invoke(f.Local,[new InvoiceSaveContext {Username="admin",Role=DomainConstants.RoleAdmin,OfficeCode=office},CancellationToken.None])!;
        f.Db.ChangeTracker.Clear();
    }
    private static async Task<YeonsuDeliveryRow> Ledger(Fixture f)
    {
        await using var vm=new YeonsuDeliveryViewModel(f.Local,f.Session);
        await vm.InitializeAsync();vm.SelectedViewTarget=YeonsuDeliveryViewModel.ViewTargetSales;
        return Assert.Single(vm.Deliveries);
    }
    [Fact]
    public async Task CostCalculationFailureRollsBackInvoiceAndSyncCursor()
    {
        await using var f=await Fixture.CreateAsync("USENET");
        var inv=await f.InvoiceAsync();inv.PurchaseReceivingStatus=InvoiceReceivingStatuses.Confirmed;
        await f.Db.SaveChangesAsync();await Rebuild(f,"USENET");
        inv=await f.InvoiceAsync();var dto=LocalMappings.ToDto(inv);dto.Lines[0].Quantity=4;dto.Revision++;
        var connection=(SqliteConnection)f.Db.Database.GetDbConnection();
        await f.Db.Database.ExecuteSqlRawAsync("CREATE TEMP TRIGGER fail_cost_cache BEFORE INSERT ON StockLayers BEGIN SELECT RAISE(ABORT, 'test cost failure'); END;");
        var before=await InventoryCostRefreshProtectionTests.Snapshot(connection);
        var apply=typeof(SyncService).GetMethod("ApplyPullAsync",BindingFlags.NonPublic|BindingFlags.Instance)!;
        var failure=await Assert.ThrowsAnyAsync<Exception>(async ()=>await (Task)apply.Invoke(f.sync,[new SyncPullResponse {CurrentServerRevision=14000,Invoices=[dto]},12345L,CancellationToken.None,true])!);
        Assert.Contains("test cost failure",failure.ToString());f.Db.ChangeTracker.Clear();
        var after=await InventoryCostRefreshProtectionTests.Snapshot(connection);
        foreach(var table in before.Keys)Assert.Equal(before[table],after[table]);
    }
    [Fact]
    public async Task ForcedRefreshRestoresClearedDerivedCacheWithoutStockWrites()
    {
        await using var f=await Fixture.CreateAsync("USENET");
        var inv=await f.InvoiceAsync();inv.PurchaseReceivingStatus=InvoiceReceivingStatuses.Confirmed;await f.Db.SaveChangesAsync();
        var refresh=typeof(LocalStateService).GetMethod("RefreshInventoryCostAfterPullAsync",BindingFlags.NonPublic|BindingFlags.Instance)!;
        await using(var tx=await f.Db.Database.BeginTransactionAsync())
        {await (Task)refresh.Invoke(f.Local,[false,CancellationToken.None])!;await tx.CommitAsync();}
        var connection=(SqliteConnection)f.Db.Database.GetDbConnection();
        var before=await InventoryCostRefreshProtectionTests.Snapshot(connection);
        Assert.Single(await f.Db.StockLayers.ToListAsync());
        await using(var tx=await f.Db.Database.BeginTransactionAsync())
        {
            await f.Db.StockLayers.ExecuteDeleteAsync();f.Db.ChangeTracker.Clear();
            await (Task)refresh.Invoke(f.Local,[true,CancellationToken.None])!;await tx.CommitAsync();
        }
        Assert.Single(await f.Db.StockLayers.ToListAsync());
        var after=await InventoryCostRefreshProtectionTests.Snapshot(connection);
        Assert.Equal(before["Items"],after["Items"]);Assert.Equal(before["ItemWarehouseStocks"],after["ItemWarehouseStocks"]);
        Assert.Equal(before["Invoices"],after["Invoices"]);Assert.Equal(before["InvoiceLines"],after["InvoiceLines"]);
    }
    [Theory]
    [InlineData("transfer-receipt")]
    [InlineData("receipt-order")]
    public async Task DependentWarehouseAndChronologyChangesRefreshSalesCost(string change)
    {
        await using var f=await Fixture.CreateAsync("USENET");
        var purchase=await f.InvoiceAsync();purchase.IsConfirmed=true;
        purchase.InvoiceDate=DateOnly.FromDateTime(DateTime.Today).AddDays(-1);
        purchase.PurchaseReceivingStatus=InvoiceReceivingStatuses.Confirmed;
        purchase.Lines.Single().Quantity=10;purchase.Lines.Single().LineAmount=100;
        purchase.SourceWarehouseCode=change=="transfer-receipt"?"YEONSU_MAIN":"USENET_MAIN";
        purchase.LastSavedAtUtc=Fixture.SavedAt;
        var itemId=purchase.Lines.Single().ItemId!.Value;var id=Guid.NewGuid();
        var sales=new LocalInvoice {Id=id,VersionGroupId=id,VersionNumber=1,IsConfirmed=true,IsLatestVersion=true,
            CustomerId=purchase.CustomerId,TenantCode=purchase.TenantCode,OfficeCode="USENET",ResponsibleOfficeCode="USENET",
            SourceWarehouseCode="USENET_MAIN",VoucherType=VoucherType.Sales,InvoiceDate=purchase.InvoiceDate,
            LastSavedAtUtc=Fixture.SavedAt.AddHours(1),CreatedAtUtc=Fixture.SavedAt,IsDirty=false,
            Lines=[new LocalInvoiceLine {Id=Guid.NewGuid(),InvoiceId=id,ItemId=itemId,Quantity=2,UnitPrice=30,LineAmount=60}]};
        f.Db.Invoices.Add(sales);
        var transferId=Guid.NewGuid();var transfer=new LocalInventoryTransfer {Id=transferId,
            FromWarehouseCode="YEONSU_MAIN",ToWarehouseCode="USENET_MAIN",TransferDate=purchase.InvoiceDate,
            CreatedAtUtc=Fixture.SavedAt,LastSavedAtUtc=Fixture.SavedAt.AddMinutes(30),TransferStatus="수령대기",IsDirty=false,
            Lines=[new LocalInventoryTransferLine {Id=Guid.NewGuid(),TransferId=transferId,ItemId=itemId,Quantity=2,ReceivedQuantity=2}]};
        if(change=="transfer-receipt")f.Db.InventoryTransfers.Add(transfer);
        await f.Db.SaveChangesAsync();await Rebuild(f,"USENET");
        Assert.Equal(change=="transfer-receipt"?0:20,(await Ledger(f)).PurchaseAmount);
        purchase=await f.InvoiceAsync();var purchaseDto=LocalMappings.ToDto(purchase);
        var salesDto=LocalMappings.ToDto(await f.Db.Invoices.Include(x=>x.Lines).Include(x=>x.Payments).SingleAsync(x=>x.Id==id));
        var pull=new SyncPullResponse {CurrentServerRevision=15000,Invoices=[purchaseDto,salesDto]};
        if(change=="receipt-order")purchaseDto.InvoiceDate=purchaseDto.InvoiceDate.AddDays(1);
        else
        {var dto=LocalMappings.ToDto(await f.Db.InventoryTransfers.Include(x=>x.Lines).SingleAsync());dto.TransferStatus="수령확정";pull.InventoryTransfers.Add(dto);}
        var apply=typeof(SyncService).GetMethod("ApplyPullAsync",BindingFlags.NonPublic|BindingFlags.Instance)!;
        await (Task)apply.Invoke(f.sync,[pull,12345L,CancellationToken.None,true])!;
        Assert.Equal(change=="transfer-receipt"?20:0,(await Ledger(f)).PurchaseAmount);
        Assert.False(await f.Db.Invoices.AnyAsync(x=>x.IsDirty));
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public static readonly DateTime SavedAt = new(2026,9,12,0,0,0,DateTimeKind.Utc);
        private readonly SqliteConnection connection;
        public readonly SyncService sync;
        private readonly HttpClient http;
        private readonly Guid invoiceId;
        public LocalDbContext Db { get; }
        public SessionState Session { get; }
        public LocalStateService Local { get; }
        private Fixture(SqliteConnection connection, LocalDbContext db, string office, Guid invoiceId)
        {
            this.connection = connection; Db = db; this.invoiceId = invoiceId;
            Session = new SessionState();
            Session.SetOfflineSession(new UserSessionDto { Username="admin", Role=DomainConstants.RoleAdmin, TenantCode=office=="ITWORLD"?"ITWORLD":"USENET_GROUP", OfficeCode=office, ScopeType=TenantScopeCatalog.ScopeAdmin });
            var dispatcher = new SyncRequestDispatcher();
            Local = new LocalStateService(db,new OfficeAccessService(),dispatcher,Session);
            http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1/") };
            sync = new SyncService(db,Local,new RentalStateService(db),new ErpApiClient(http,Session),Session,dispatcher,new SyncDiagnosticsService(Session));
        }
        public static async Task<Fixture> CreateAsync(string office)
        {
            var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
            var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var id=Guid.NewGuid();var customer=Guid.NewGuid();var item=Guid.NewGuid();
            var tenant=office=="ITWORLD"?"ITWORLD":"USENET_GROUP";var owner=office=="YEONSU"?"USENET":office;
            db.Customers.Add(new LocalCustomer { Id=customer,NameOriginal="pull test",TenantCode=tenant,OfficeCode=owner,ResponsibleOfficeCode=office,IsDirty=false });
            db.Items.Add(new LocalItem { Id=item,NameOriginal="pull stock",TenantCode=tenant,OfficeCode=owner,CurrentStock=0,IsDirty=false });
            db.Invoices.Add(new LocalInvoice { Id=id,CustomerId=customer,TenantCode=tenant,OfficeCode=owner,ResponsibleOfficeCode=office,SourceWarehouseCode=office+"_MAIN",VersionGroupId=id,VersionNumber=1,IsLatestVersion=true,VoucherType=VoucherType.Purchase,InvoiceDate=new DateOnly(2026,9,12),PurchaseReceivingRequired=true,PurchaseReceivingStatus=InvoiceReceivingStatuses.Pending,Revision=12345,CreatedAtUtc=SavedAt,UpdatedAtUtc=SavedAt,CreatedByUsername="creator",LastSavedByUsername="admin",LastSavedAtUtc=SavedAt,ConcurrencyStamp="editor-stamp",IsDirty=false,Lines=[new LocalInvoiceLine { Id=Guid.NewGuid(),InvoiceId=id,ItemId=item,Quantity=1,UnitPrice=10,LineAmount=10,ItemNameOriginal="pull stock" }] });
            await db.SaveChangesAsync();return new Fixture(connection,db,office,id);
        }
        public Task<LocalInvoice> InvoiceAsync() => Db.Invoices.IgnoreQueryFilters().Include(x=>x.Lines).Include(x=>x.Payments).SingleAsync(x=>x.Id==invoiceId);
        public async Task PullAsync(InvoiceDto dto)
        {
            var method=typeof(SyncService).GetMethod("UpsertPulledInvoicesAsync",BindingFlags.NonPublic|BindingFlags.Instance)!;
            await (Task)method.Invoke(sync,[new List<InvoiceDto>{dto},CancellationToken.None])!;
            Db.ChangeTracker.Clear();
        }
        public async ValueTask DisposeAsync() { sync.Dispose();http.Dispose();await Db.DisposeAsync();await connection.DisposeAsync(); }
    }
}
