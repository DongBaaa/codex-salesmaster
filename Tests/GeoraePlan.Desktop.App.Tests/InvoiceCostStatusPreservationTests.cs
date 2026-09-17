using System.Net.Http;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;
namespace GeoraePlan.Desktop.App.Tests;
public sealed class InvoiceCostStatusPreservationTests
{
    [Theory]
    [InlineData("USENET", false, false)]
    [InlineData("USENET", false, true)]
    [InlineData("USENET", true, false)]
    [InlineData("USENET", true, true)]
    [InlineData("YEONSU", false, false)]
    [InlineData("YEONSU", false, true)]
    [InlineData("YEONSU", true, false)]
    [InlineData("YEONSU", true, true)]
    [InlineData("ITWORLD", false, false)]
    [InlineData("ITWORLD", false, true)]
    [InlineData("ITWORLD", true, false)]
    [InlineData("ITWORLD", true, true)]
    public async Task ObserveCalculatedStateAcrossIdenticalPull(string office, bool shortage, bool dirty)
    {
        await using var f=await Fixture.CreateAsync(office);
        var purchase=await f.InvoiceAsync();
        purchase.PurchaseReceivingStatus=InvoiceReceivingStatuses.Confirmed;
        purchase.Lines.Single().Quantity=3;
        var salesId=Guid.NewGuid();
        var sales=new LocalInvoice {
            Id=salesId,VersionGroupId=salesId,CustomerId=purchase.CustomerId,
            TenantCode=purchase.TenantCode,OfficeCode=purchase.OfficeCode,ResponsibleOfficeCode=office,
            SourceWarehouseCode=office+"_MAIN",VoucherType=VoucherType.Sales,
            InvoiceDate=purchase.InvoiceDate.AddDays(1),Revision=12346,IsDirty=false,
            TotalAmount=shortage?55m:22m,SupplyAmount=shortage?50m:20m,VatAmount=shortage?5m:2m,
            CreatedAtUtc=Fixture.SavedAt,UpdatedAtUtc=Fixture.SavedAt,
            Lines=[new LocalInvoiceLine {Id=Guid.NewGuid(),InvoiceId=salesId,
                ItemId=purchase.Lines.Single().ItemId,ItemNameOriginal="cost diagnosis",
                Quantity=shortage?5:2,UnitPrice=10,LineAmount=shortage?50:20}]
        };
        f.Db.Invoices.Add(sales);await f.Db.SaveChangesAsync();
        var rebuild=typeof(LocalStateService).GetMethod("RebuildInventorySnapshotsAsync",BindingFlags.Instance|BindingFlags.NonPublic)!;
        await (Task)rebuild.Invoke(f.Local,[new InvoiceSaveContext {Username="admin",Role=DomainConstants.RoleAdmin,OfficeCode=office},CancellationToken.None])!;
        f.Db.ChangeTracker.Clear();
        var current=await f.Db.Invoices.Include(x=>x.Lines).Include(x=>x.Payments).SingleAsync(x=>x.Id==salesId);
        var expected=shortage?"Unsettled":"Settled";
        Assert.Equal(expected,current.CostStatus);
        Assert.Equal(shortage,await f.Db.CostAllocations.AnyAsync(x=>x.SalesInvoiceId==salesId && x.IsUnsettled));
        current.IsDirty=dirty;await f.Db.SaveChangesAsync();
        var dto=LocalMappings.ToDto(current);
        var caches=await CacheSnapshot(f.Db);
        var amounts=(current.TotalAmount,current.SupplyAmount,current.VatAmount);
        await f.PullAsync(dto);
        current=await f.Db.Invoices.IgnoreQueryFilters().SingleAsync(x=>x.Id==salesId);
        // An identical server invoice does not invalidate the completed local calculation.
        Assert.Equal(expected,current.CostStatus);
        Assert.Equal(dirty,current.IsDirty);
        Assert.Equal(amounts,(current.TotalAmount,current.SupplyAmount,current.VatAmount));
        Assert.Equal(caches,await CacheSnapshot(f.Db));
        Assert.Equal(shortage,await f.Db.CostAllocations.AnyAsync(x=>x.SalesInvoiceId==salesId && x.IsUnsettled));
    }
    [Theory]
    [InlineData("memo")]
    [InlineData("revision")]
    [InlineData("updated-time")]
    [InlineData("payment")]
    [InlineData("invoice-number")]
    [InlineData("line-remark")]
    [InlineData("zero-revision")]
    public async Task NonInventoryChangesRetainCalculation(string change)
    {
        await using var f=await Fixture.CreateAsync("USENET");
        var inv=await f.InvoiceAsync();inv.CostStatus="Settled";
        if(change=="zero-revision") inv.Revision=0;
        await f.Db.SaveChangesAsync();var dto=LocalMappings.ToDto(inv);
        switch(change)
        {
            case "memo":dto.Memo="new memo";break;
            case "revision":dto.Revision++;break;
            case "updated-time":dto.UpdatedAtUtc=dto.UpdatedAtUtc.AddHours(1);break;
            case "payment":dto.Payments.Add(new PaymentDto {Id=Guid.NewGuid(),InvoiceId=dto.Id,Amount=1,PaymentDate=dto.InvoiceDate});break;
            case "invoice-number":dto.InvoiceNumber="assigned-number";break;
            case "line-remark":dto.Lines[0].Remark="changed remark";break;
        }
        await f.PullAsync(dto);var stored=await f.InvoiceAsync();
        Assert.Equal("Settled",stored.CostStatus);Assert.False(stored.IsDirty);
        Assert.Equal(dto.Revision,stored.Revision);Assert.Equal(dto.Memo,stored.Memo);
    }
    [Theory]
    [InlineData("quantity")]
    [InlineData("unit-price")]
    [InlineData("line-amount")]
    [InlineData("item")]
    [InlineData("tracking")]
    [InlineData("line-order")]
    [InlineData("line-remove")]
    [InlineData("line-add")]
    [InlineData("warehouse")]
    [InlineData("office")]
    [InlineData("owner")]
    [InlineData("tenant")]
    [InlineData("date")]
    [InlineData("creation-time")]
    [InlineData("voucher")]
    [InlineData("receiving")]
    [InlineData("version")]
    [InlineData("latest")]
    [InlineData("deleted")]
    public async Task ChangedInventoryInputsInvalidateCalculation(string change)
    {
        await using var f=await Fixture.CreateAsync("USENET");
        var inv=await f.InvoiceAsync();inv.CostStatus="Settled";await f.Db.SaveChangesAsync();
        var dto=LocalMappings.ToDto(inv);dto.Revision++;
        switch(change)
        {
            case "quantity":dto.Lines[0].Quantity++;break;
            case "unit-price":dto.Lines[0].UnitPrice++;break;
            case "line-amount":dto.Lines[0].LineAmount++;break;
            case "item":dto.Lines[0].ItemId=null;break;
            case "tracking":dto.Lines[0].ItemTrackingType="비재고";break;
            case "line-order":dto.Lines[0].OrderIndex++;break;
            case "line-remove":dto.Lines.Clear();break;
            case "line-add":dto.Lines.Add(new InvoiceLineDto {Id=Guid.NewGuid(),InvoiceId=dto.Id,Quantity=1});break;
            case "warehouse":dto.SourceWarehouseCode="YEONSU_MAIN";break;
            case "office":dto.ResponsibleOfficeCode="YEONSU";break;
            case "owner":dto.OfficeCode="ITWORLD";break;
            case "tenant":
                dto.TenantCode="ORG_COST";dto.OfficeCode="ORG_COST";dto.ResponsibleOfficeCode="ORG_COST";
                Assert.NotEqual(inv.TenantCode,LocalMappings.ToLocal(dto).TenantCode);
                break;
            case "date":dto.InvoiceDate=dto.InvoiceDate.AddDays(1);break;
            case "creation-time":dto.CreatedAtUtc=dto.CreatedAtUtc.AddMinutes(1);break;
            case "voucher":dto.VoucherType=VoucherType.Sales;break;
            case "receiving":dto.PurchaseReceivingStatus=InvoiceReceivingStatuses.Confirmed;break;
            case "version":dto.VersionNumber++;break;
            case "latest":dto.IsLatestVersion=false;break;
            case "deleted":dto.IsDeleted=true;break;
        }
        await f.PullAsync(dto);var stored=await f.InvoiceAsync();
        Assert.Equal("Pending",stored.CostStatus);Assert.False(stored.IsDirty);
    }
    private static async Task<string> CacheSnapshot(LocalDbContext db)
    {
        var output=new List<string>();
        foreach(var table in new[]{"InventoryMovements","StockLayers","CostAllocations","ItemWarehouseStocks","SerialLedgers","InvoiceLineSerials","Items"})
        {
            using var cmd=db.Database.GetDbConnection().CreateCommand();cmd.CommandText="SELECT * FROM "+table;
            using var reader=await cmd.ExecuteReaderAsync();var rows=new List<string>();
            while(await reader.ReadAsync()) {var cells=new object[reader.FieldCount];reader.GetValues(cells);rows.Add(System.Text.Json.JsonSerializer.Serialize(cells));}
            rows.Sort(StringComparer.Ordinal);output.Add(table+":"+string.Join("\n",rows));
        }
        return string.Join("\n",output);
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public static readonly DateTime SavedAt = new(2026,9,12,0,0,0,DateTimeKind.Utc);
        private readonly SqliteConnection connection;
        private readonly SyncService sync;
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
