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

public sealed class InvoicePullConcurrencyStampTests
{
    [Theory]
    [InlineData("USENET")]
    [InlineData("ITWORLD")]
    [InlineData("YEONSU")]
    public async Task AcknowledgedEchoRetainsEditorIdentityAndAllowsReceiving(string office)
    {
        await using var f = await Fixture.CreateAsync(office);
        var dto = LocalMappings.ToDto(await f.InvoiceAsync());
        await f.PullAsync(dto);
        await f.PullAsync(dto); // Repeated pull must also be idempotent for an open editor.
        var stored = await f.InvoiceAsync();
        Assert.Equal("editor-stamp", stored.ConcurrencyStamp);
        Assert.Equal("admin", stored.LastSavedByUsername);
        Assert.Equal("creator", stored.CreatedByUsername);
        Assert.Equal(Fixture.SavedAt, stored.LastSavedAtUtc);
        Assert.Equal(12345, stored.Revision);
        Assert.False(stored.IsDirty);
        var draft = LocalMappings.ToLocal(dto);
        draft.PurchaseReceivingStatus = InvoiceReceivingStatuses.Confirmed;
        draft.PurchaseReceivedAtUtc = DateTime.UtcNow;
        draft.PurchaseReceivedByUsername = "admin";
        var result = await f.Local.SaveInvoiceAsync(draft, new InvoiceSaveContext
        {
            Username = "admin", Role = DomainConstants.RoleAdmin, OfficeCode = office,
            ExpectedConcurrencyStamp = "editor-stamp"
        }, f.Session);
        Assert.True(result.Success, result.Message);
        var saved = await f.Db.Invoices.AsNoTracking().SingleAsync(x => x.Id == result.SavedInvoiceId);
        Assert.Equal(12345, saved.Revision);
        Assert.Equal(2, saved.VersionNumber);
        Assert.Equal(InvoiceReceivingStatuses.Confirmed, saved.PurchaseReceivingStatus);
    }

    [Theory]
    [InlineData("new-revision")]
    [InlineData("memo")]
    [InlineData("line-quantity")]
    [InlineData("warehouse")]
    [InlineData("responsible-office")]
    [InlineData("deleted")]
    [InlineData("zero-revision")]
    public async Task ChangedOrUnacknowledgedPullStillInvalidatesOpenEditor(string change)
    {
        await using var f = await Fixture.CreateAsync("USENET");
        var invoice = await f.InvoiceAsync();
        if (change == "zero-revision") { invoice.Revision = 0; await f.Db.SaveChangesAsync(); }
        var dto = LocalMappings.ToDto(invoice);
        switch(change)
        {
            case "new-revision": dto.Revision++; break;
            case "memo": dto.Memo = "another editor"; break;
            case "line-quantity": dto.Lines[0].Quantity = 2m; break;
            case "warehouse": dto.SourceWarehouseCode = "YEONSU_MAIN"; break;
            case "responsible-office": dto.ResponsibleOfficeCode = "YEONSU"; break;
            case "deleted": dto.IsDeleted = true; break;
        }
        await f.PullAsync(dto);
        var stored = await f.InvoiceAsync();
        Assert.NotEqual("editor-stamp", stored.ConcurrencyStamp);
        Assert.True(string.IsNullOrEmpty(stored.LastSavedByUsername));
        Assert.Equal(dto.Revision, stored.Revision);
        if (change == "memo") Assert.Equal("another editor", stored.Memo);
        if (change == "line-quantity") Assert.Equal(2m, stored.Lines.Single().Quantity);
    }

    [Fact]
    public async Task SeparatePaymentPullDoesNotInvalidateUnchangedInvoiceEditor()
    {
        await using var f = await Fixture.CreateAsync("USENET");
        var dto = LocalMappings.ToDto(await f.InvoiceAsync());
        var paymentId = Guid.NewGuid();
        dto.Payments.Add(new PaymentDto { Id = paymentId, InvoiceId = dto.Id, PaymentDate = new DateOnly(2026,9,12), Amount = 5m, Revision = 12346 });
        // This payment was already synchronized independently in an earlier cycle.
        f.Db.Payments.Add(LocalMappings.ToLocal(dto.Payments[0]));
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();
        await f.PullAsync(dto);
        Assert.Equal("editor-stamp", (await f.InvoiceAsync()).ConcurrencyStamp);
        Assert.Equal(5m, (await f.Db.Payments.SingleAsync(x => x.Id == paymentId)).Amount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewChildReceivedForExistingInvoiceIsInserted(bool isPayment)
    {
        await using var f = await Fixture.CreateAsync("USENET");
        var dto = LocalMappings.ToDto(await f.InvoiceAsync());
        var childId = Guid.NewGuid();
        if (isPayment)
            dto.Payments.Add(new PaymentDto { Id=childId,InvoiceId=dto.Id,PaymentDate=new DateOnly(2026,9,12),Amount=5m,Revision=12346 });
        else
        {
            dto.Revision++;
            dto.Lines.Add(new InvoiceLineDto { Id=childId,InvoiceId=dto.Id,ItemId=dto.Lines[0].ItemId,Quantity=2,UnitPrice=10,LineAmount=20,ItemNameOriginal="new line" });
        }
        await f.PullAsync(dto);
        if(isPayment)
        {
            Assert.Equal(5m,(await f.Db.Payments.SingleAsync(x=>x.Id==childId)).Amount);
            Assert.Equal("editor-stamp",(await f.InvoiceAsync()).ConcurrencyStamp);
        }
        else
        {
            Assert.Equal(2m,(await f.Db.InvoiceLines.SingleAsync(x=>x.Id==childId)).Quantity);
            Assert.NotEqual("editor-stamp",(await f.InvoiceAsync()).ConcurrencyStamp);
        }
    }

    [Fact]
    public async Task DirtyLocalInvoiceRetainsDraftAndEditorIdentity()
    {
        await using var f = await Fixture.CreateAsync("USENET");
        var original = await f.InvoiceAsync();
        var dto = LocalMappings.ToDto(original);
        original.Memo = "pending local draft"; original.IsDirty = true;
        await f.Db.SaveChangesAsync();
        dto.Revision++;
        await f.PullAsync(dto);
        var stored = await f.InvoiceAsync();
        Assert.Equal("pending local draft", stored.Memo);
        Assert.Equal("editor-stamp", stored.ConcurrencyStamp);
        Assert.True(stored.IsDirty);
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
