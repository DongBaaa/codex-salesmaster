using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class DashboardBalancePrivacyTests
{
    private static UserSessionDto User(Guid id, bool sales = true, bool purchase = true, bool edit = true) => new()
    {
        UserId=id,Username="balance-fixture",Role=DomainConstants.RoleUser,TenantCode=TenantScopeCatalog.UsenetGroup,
        OfficeCode=OfficeCodeCatalog.Usenet,ScopeType=TenantScopeCatalog.ScopeOfficeOnly,
        Permissions=new[]{AppPermissionNames.InvoiceEdit,sales?AppPermissionNames.AmountViewSales:"",purchase?AppPermissionNames.AmountViewPurchase:"",edit?AppPermissionNames.PaymentEdit:""}.Where(p=>p.Length>0).ToList()
    };

    [Theory]
    [InlineData(VoucherType.Sales, false, true)]
    [InlineData(VoucherType.Purchase, true, false)]
    public void BuilderMasksCachedMoneyUsingCurrentDirection(VoucherType type,bool sales,bool purchase)
    {
        var session=new SessionState();session.SetSession("fixture",User(Guid.NewGuid(),sales,purchase));
        var source=new LocalInvoiceListSummary{Id=Guid.NewGuid(),CustomerId=Guid.NewGuid(),VoucherType=type,TotalAmount=12345m,SettledAmount=200m};
        var row=Assert.Single(DashboardBalanceDetailBuilder.BuildRows([source],new Dictionary<Guid,string>(),type,session));
        Assert.True(row.AmountsHidden);Assert.Null(row.TotalAmount);Assert.Null(row.SettledAmount);Assert.Null(row.BalanceAmount);Assert.Null(row.CustomerBalance);
        Assert.Equal("비공개",row.BalanceAmountDisplay);Assert.False(row.CanSelectForProcessing);
        Assert.Equal(12345m,source.TotalAmount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10000)]
    public void HiddenInvoiceCannotBecomeZeroOrPartialCustomerBalance(int rawAmount)
    {
        var customer=Guid.NewGuid();
        var rows=DashboardBalanceDetailBuilder.BuildRows([
            new(){Id=Guid.NewGuid(),CustomerId=customer,VoucherType=VoucherType.Sales,TotalAmount=100m},
            new(){Id=Guid.NewGuid(),CustomerId=customer,VoucherType=VoucherType.Sales,TotalAmount=rawAmount,AmountsHidden=true},
            new(){Id=Guid.NewGuid(),CustomerId=customer,VoucherType=VoucherType.Sales,TotalAmount=0m}],new Dictionary<Guid,string>(),VoucherType.Sales);
        Assert.Equal(2,rows.Count);Assert.All(rows,row=>Assert.Null(row.CustomerBalance));
        Assert.Single(rows,row=>row.AmountsHidden);Assert.Single(rows,row=>row.BalanceAmount==100m);
    }

    [Theory]
    [InlineData(VoucherType.Sales)]
    [InlineData(VoucherType.Purchase)]
    public async Task RevocationClearsRowsSelectionInputsAndStatusButTokenRefreshDoesNot(VoucherType type)
    {
        await using var f=await Fixture.CreateAsync(type);
        await f.Vm.RefreshAsync();Assert.Equal("400",f.Vm.ProcessAmountText);Assert.Equal("400원",f.Vm.TotalAmountText);
        f.Session.RefreshSession("token",User(f.Session.User!.UserId));Assert.Single(f.Vm.Rows);
        f.Session.RefreshSession("revoke",User(f.Session.User!.UserId,false,false));
        Assert.Empty(f.Vm.Rows);Assert.Null(f.Vm.SelectedRow);Assert.Equal("",f.Vm.ProcessAmountText);Assert.Equal("",f.Vm.ProcessNote);
        Assert.Equal("비공개",f.Vm.TotalAmountText);Assert.DoesNotContain("400",f.Vm.StatusMessage);Assert.False(f.Vm.CanProcessPayments);
        f.Session.RefreshSession("restore",User(f.Session.User!.UserId));Assert.Empty(f.Vm.Rows);Assert.Equal("비공개",f.Vm.TotalAmountText);
        await f.Vm.RefreshAsync();Assert.Equal("400원",f.Vm.TotalAmountText);
    }

    [Theory]
    [InlineData(VoucherType.Sales)]
    [InlineData(VoucherType.Purchase)]
    public async Task RestrictedRefreshRetainsMetadataAndUnknownAmount(VoucherType type)
    {
        await using var f=await Fixture.CreateAsync(type);
        f.Session.RefreshSession("restricted",User(f.Session.User!.UserId,false,false));
        await f.Vm.RefreshAsync();var row=Assert.Single(f.Vm.Rows);
        Assert.Equal(f.Invoice.Id,row.InvoiceId);Assert.True(row.AmountsHidden);
        Assert.Equal("비공개",f.Vm.TotalAmountText);Assert.Equal("",f.Vm.ProcessAmountText);
        Assert.False(f.Vm.CanProcessPayments);Assert.True(f.Session.HasPermission(AppPermissionNames.InvoiceEdit));
        Assert.Equal(500m,f.Invoice.TotalAmount);Assert.False(f.Invoice.IsDirty);
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("regrant")]
    [InlineData("dispose")]
    public async Task DelayedRefreshCannotRestorePrivateRows(string action)
    {
        await using var f=await Fixture.CreateAsync();f.Gate.Arm();var pending=f.Vm.RefreshAsync();
        try
        {
            await f.Gate.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if(action=="dispose")f.Vm.Dispose();
            else {f.Session.RefreshSession("revoke",User(f.Session.User!.UserId,false,false));if(action=="regrant")f.Session.RefreshSession("restore",User(f.Session.User!.UserId));}
        }
        finally{f.Gate.Release();}
        await pending;Assert.Empty(f.Vm.Rows);Assert.Null(f.Vm.SelectedRow);Assert.Equal("비공개",f.Vm.TotalAmountText);
    }

    [Theory]
    [InlineData("hidden",false)]
    [InlineData("hidden",true)]
    [InlineData("revoked",false)]
    [InlineData("revoked",true)]
    [InlineData("no-edit",false)]
    [InlineData("no-edit",true)]
    public async Task ProcessingDoesNotWriteHiddenOrUnauthorizedBalances(string reason,bool batch)
    {
        await using var f=await Fixture.CreateAsync();
        if(reason=="hidden"){f.Invoice.AmountsHidden=true;await f.Db.SaveChangesAsync();}
        await f.Vm.RefreshAsync();var row=Assert.Single(f.Vm.Rows);row.IsBatchSelected=true;
        if(reason=="revoked") f.Session.RefreshSession("revoke",User(f.Session.User!.UserId,false,false));
        if(reason=="no-edit") f.Session.RefreshSession("no-edit",User(f.Session.User!.UserId,edit:false));
        // A stale external selection must not bypass the handler's current access check.
        f.Vm.SelectedRow=row;f.Vm.ProcessAmountText="50";
        if(batch){f.Vm.Rows.Clear();f.Vm.Rows.Add(row);await f.Vm.ProcessCheckedBalancesCommand.ExecuteAsync(null);}
        else await f.Vm.ProcessSelectedBalanceCommand.ExecuteAsync(null);
        Assert.Empty(await f.Db.Transactions.ToListAsync());Assert.Equal(500m,f.Invoice.TotalAmount);Assert.False(f.Invoice.IsDirty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisclosedBalanceStillSupportsExplicitSettlement(bool batch)
    {
        await using var f=await Fixture.CreateAsync();await f.Vm.RefreshAsync();var row=Assert.Single(f.Vm.Rows);row.IsBatchSelected=true;
        f.Vm.ProcessAmountText="50";
        if(batch)await f.Vm.ProcessCheckedBalancesCommand.ExecuteAsync(null);else await f.Vm.ProcessSelectedBalanceCommand.ExecuteAsync(null);
        var saved=Assert.Single(await f.Db.Transactions.ToListAsync());Assert.Equal(50m,saved.ReceiptTotal);Assert.Equal(f.Invoice.Id,saved.LinkedInvoiceId);
    }

    [Fact]
    public async Task OfficeBoundaryRemainsApplied()
    {
        await using var f=await Fixture.CreateAsync();
        f.Db.Invoices.Add(new LocalInvoice { Id=Guid.NewGuid(),CustomerId=f.Customer.Id,TenantCode=TenantScopeCatalog.Itworld,
            OfficeCode=OfficeCodeCatalog.Itworld,ResponsibleOfficeCode=OfficeCodeCatalog.Itworld,VoucherType=VoucherType.Sales,TotalAmount=999m,IsLatestVersion=true });
        await f.Db.SaveChangesAsync();await f.Vm.RefreshAsync();Assert.Equal(f.Invoice.Id,Assert.Single(f.Vm.Rows).InvoiceId);
    }

    [Theory]
    [InlineData("line")]
    [InlineData("payment")]
    [InlineData("transaction")]
    [InlineData("deleted-transaction")]
    public async Task HiddenSettlementEvidenceMakesBalanceUnknown(string evidence)
    {
        await using var f=await Fixture.CreateAsync();
        if(evidence=="line")f.Invoice.Lines.Single().AmountsHidden=true;
        if(evidence=="payment")f.Invoice.Payments.Single().AmountsHidden=true;
        if(evidence.EndsWith("transaction"))f.Db.Transactions.Add(new LocalTransaction{Id=Guid.NewGuid(),CustomerId=f.Customer.Id,LinkedInvoiceId=f.Invoice.Id,
            TenantCode=f.Customer.TenantCode,OfficeCode=f.Customer.OfficeCode,ResponsibleOfficeCode=f.Customer.OfficeCode,
            TransactionKind="전표수금",AmountsHidden=true,IsDeleted=evidence=="deleted-transaction"});
        await f.Db.SaveChangesAsync();await f.Vm.RefreshAsync();
        var row=Assert.Single(f.Vm.Rows);
        Assert.Equal(evidence!="deleted-transaction",row.AmountsHidden);
        Assert.Equal(evidence=="deleted-transaction"?"400원":"비공개",f.Vm.TotalAmountText);
    }

    private sealed class Fixture:IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public LocalDbContext Db {get;}
        public SessionState Session {get;}=new();
        public LocalCustomer Customer {get;}
        public LocalInvoice Invoice {get;}
        public DashboardBalanceDetailViewModel Vm {get;}
        public QueryGate Gate {get;}
        private Fixture(SqliteConnection connection,LocalDbContext db,QueryGate gate,LocalCustomer c,LocalInvoice i)
        {
            _connection=connection;Db=db;Gate=gate;Customer=c;Invoice=i;Session.SetSession("fixture",User(Guid.NewGuid()));
            var local=new LocalStateService(db,new OfficeAccessService(),new SyncRequestDispatcher(),Session);
            Vm=new DashboardBalanceDetailViewModel(local,Session,i.VoucherType,"Fixture","Fixture","잔액","#000000");
        }
        public static async Task<Fixture> CreateAsync(VoucherType type=VoucherType.Sales)
        {
            var connection=new SqliteConnection("Data Source=:memory:");await connection.OpenAsync();var gate=new QueryGate();
            var db=new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).AddInterceptors(gate).Options);await db.Database.EnsureCreatedAsync();
            var c=new LocalCustomer {Id=Guid.NewGuid(),TenantCode=TenantScopeCatalog.UsenetGroup,OfficeCode=OfficeCodeCatalog.Usenet,ResponsibleOfficeCode=OfficeCodeCatalog.Usenet,NameOriginal="Fixture",NameMatchKey="FIXTURE",IsDirty=false};
            var i=new LocalInvoice {Id=Guid.NewGuid(),CustomerId=c.Id,TenantCode=c.TenantCode,OfficeCode=c.OfficeCode,ResponsibleOfficeCode=c.OfficeCode,VoucherType=type,TotalAmount=500m,IsLatestVersion=true,IsConfirmed=true,IsDirty=false};i.VersionGroupId=i.Id;
            i.Lines.Add(new LocalInvoiceLine{Id=Guid.NewGuid(),InvoiceId=i.Id,Quantity=1m,UnitPrice=500m,LineAmount=500m});i.Payments.Add(new LocalPayment{Id=Guid.NewGuid(),InvoiceId=i.Id,Amount=100m,IsDirty=false});
            db.Customers.Add(c);db.Invoices.Add(i);await db.SaveChangesAsync();return new Fixture(connection,db,gate,c,i);
        }
        public async ValueTask DisposeAsync(){Gate.Release();Vm.Dispose();await Db.DisposeAsync();await _connection.DisposeAsync();}
    }
    private sealed class QueryGate:DbCommandInterceptor
    {
        private int _armed;
        public TaskCompletionSource Blocked {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Arm()=>Volatile.Write(ref _armed,1);
        public void Release()=>_released.TrySetResult();
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData eventData,InterceptionResult<DbDataReader> result,CancellationToken cancellationToken=default)
        {if(command.CommandText.Contains("Invoices")&&Interlocked.Exchange(ref _armed,0)==1){Blocked.TrySetResult();await _released.Task.WaitAsync(cancellationToken);}return result;}
    }
}
