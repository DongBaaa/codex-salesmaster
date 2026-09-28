using System.Text.Json;
using System.Data.Common;
using ClosedXML.Excel;
using 거래플랜.Desktop.App.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class DataIntegrityDiagnosticOutputPrivacyTests
{
    [Theory]
    [InlineData(VoucherType.Sales, false, false)]
    [InlineData(VoucherType.Sales, true, false)]
    [InlineData(VoucherType.Sales, false, true)]
    [InlineData(VoucherType.Sales, true, true)]
    [InlineData(VoucherType.Purchase, false, false)]
    [InlineData(VoucherType.Purchase, true, false)]
    [InlineData(VoucherType.Purchase, false, true)]
    [InlineData(VoucherType.Purchase, true, true)]
    public async Task InvoiceDiagnosticsUseDirectionPermissionAndPreserveNonFinancialDetails(VoucherType type, bool sales, bool purchase)
    {
        await using var f = await Fixture.CreateAsync(type, sales, purchase);
        var issue = await f.IssueAsync();
        var visible = type == VoucherType.Sales ? sales : purchase;
        Assert.Equal(visible, issue.CurrentValue.Contains("123,456"));
        Assert.Equal(visible, issue.ExpectedValue.Contains("234,567"));
        Assert.Contains("검증전표", issue.Message);
        Assert.Equal(f.InvoiceId, issue.EntityId);
        using var vm = new DataIntegrityIssueViewModel(f.Service, f.Session, initialScanResult: await f.Service.ScanAsync(f.Session));
        await vm.LoadAsync();
        using var output = new MemoryStream(); vm.SaveExcel(output); output.Position=0;
        using var workbook = new XLWorkbook(output);
        var detail = workbook.Worksheet("상세");
        var row = detail.RowsUsed().Single(x=>x.Cell(2).GetString()==DataIntegrityIssueCodes.InvoiceAmountMismatch);
        Assert.Equal(visible,row.Cell(10).GetString().Contains("123,456"));
        Assert.Equal(visible,row.Cell(11).GetString().Contains("234,567"));
        Assert.Contains("검증전표",row.Cell(14).GetString());
        Assert.False(f.Db.ChangeTracker.HasChanges());
        Assert.Empty(await f.Db.SyncOutboxEntries.ToListAsync());
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("logout")]
    [InlineData("account")]
    public async Task CachedDiagnosticCannotExposeMoneyAfterAccessChange(string change)
    {
        await using var f = await Fixture.CreateAsync(VoucherType.Sales, true, true);
        var issue = await f.IssueAsync();
        Assert.Contains("123,456", issue.CurrentValue);
        using var vm = new DataIntegrityIssueViewModel(f.Service, f.Session, initialScanResult: await f.Service.ScanAsync(f.Session));
        await vm.LoadAsync();
        if (change == "revoke") f.Session.RefreshSession("revoke", User(f.Session.User!.UserId, false, false));
        if (change == "logout") f.Session.Clear();
        if (change == "account") f.Session.SetSession("new", User(Guid.NewGuid(), true, true));
        Assert.DoesNotContain("123,456", JsonSerializer.Serialize(issue));
        Assert.DoesNotContain("234,567", JsonSerializer.Serialize(issue));
        Assert.Contains("검증전표", issue.Message);
        Assert.Empty(vm.Issues); Assert.Null(vm.SelectedIssue);
        using var output = new MemoryStream();
        Assert.Throws<InvalidOperationException>(()=>vm.SaveExcel(output)); Assert.Equal(0,output.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RentalReviewKeepsRunDatesAndIdentityWhileGuardingSalesAmounts(bool sales)
    {
        await using var f=await Fixture.CreateAsync(VoucherType.Purchase,sales,true);
        var profile=new LocalRentalBillingProfile { Id=Guid.NewGuid(), CustomerId=Guid.NewGuid(),
            TenantCode=TenantScopeCatalog.UsenetGroup, OfficeCode=OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode=OfficeCodeCatalog.Usenet, ManagementCompanyCode=OfficeCodeCatalog.Usenet,
            ProfileKey="REVIEW", CustomerName="보존 거래처", IsActive=true,
            BillingRunsJson=JsonSerializer.Serialize(new[] {new RentalBillingRunModel {
                RunId=Guid.Empty,RunKey="2026-09",ScheduledDate=new DateOnly(2026,9,24),
                PeriodStartDate=new DateOnly(2026,9,1),PeriodEndDate=new DateOnly(2026,9,30),BilledAmount=345678,SettledAmount=456789 }}) };
        f.Db.Add(profile);await f.Db.SaveChangesAsync();
        var issue=Assert.Single((await f.Service.ScanAsync(f.Session)).Issues,x=>x.Code==DataIntegrityIssueCodes.RentalBillingRunMissingRunId);
        Assert.Equal(sales,issue.CurrentValue.Contains("345,678"));
        Assert.Equal(sales,issue.ReviewInfoDisplay.Contains("456,789"));
        Assert.Contains("2026-09-24",issue.CurrentValue);
        Assert.Contains(profile.Id.ToString("D"),issue.ReviewInfoDisplay);
        Assert.Contains("RunId 없음",issue.ReviewInfoDisplay);
        f.Session.Clear();
        Assert.DoesNotContain("345,678",JsonSerializer.Serialize(issue));
        Assert.DoesNotContain("456,789",JsonSerializer.Serialize(issue));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrphanAmountsRespectHiddenFlagsEvenForAdministrator(bool hidden)
    {
        await using var f=await Fixture.CreateAsync(VoucherType.Sales,true,true);
        f.Session.SetSession("admin",new UserSessionDto { UserId=Guid.NewGuid(),Username="admin-fixture",Role=DomainConstants.RoleAdmin,
            TenantCode=TenantScopeCatalog.UsenetGroup,OfficeCode=OfficeCodeCatalog.Usenet,ScopeType=TenantScopeCatalog.ScopeAdmin });
        var missing=Guid.NewGuid();
        // Deliberately recreate legacy orphan rows in this in-memory database only.
        await f.Db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=OFF");
        f.Db.InvoiceLines.Add(new LocalInvoiceLine {Id=Guid.NewGuid(),InvoiceId=missing,ItemNameOriginal="보존 고아품목",AmountsHidden=hidden,LineAmount=345678,Quantity=19});
        f.Db.Payments.Add(new LocalPayment {Id=Guid.NewGuid(),InvoiceId=missing,AmountsHidden=hidden,Amount=456789});
        await f.Db.SaveChangesAsync();
        var result=await f.Service.ScanAsync(f.Session);
        var line=Assert.Single(result.Issues,x=>x.Code==DataIntegrityIssueCodes.InvoiceLineMissingInvoiceReference);
        var payment=Assert.Single(result.Issues,x=>x.Code==DataIntegrityIssueCodes.PaymentMissingInvoiceReference);
        Assert.Equal(!hidden,line.CurrentValue.Contains("345,678"));
        Assert.Equal(!hidden,payment.CurrentValue.Contains("456,789"));
        Assert.Contains(missing.ToString("D"),line.CurrentValue);
        Assert.Contains("보존 고아품목",line.Message);
    }

    [Fact]
    public async Task OldInitialResultCannotBeRepublishedAfterAccountSwitch()
    {
        await using var f=await Fixture.CreateAsync(VoucherType.Sales,true,true);
        var result=await f.Service.ScanAsync(f.Session);
        f.Session.SetSession("other",User(Guid.NewGuid(),true,true));
        using var vm=new DataIntegrityIssueViewModel(f.Service,f.Session,initialScanResult:result);
        await vm.LoadAsync();Assert.Empty(vm.Issues);
        using var output=new MemoryStream();Assert.Throws<InvalidOperationException>(()=>vm.SaveExcel(output));
        Assert.Equal(0,output.Length);
    }

    [Fact]
    public void NestedFormattingMasksOnlyMoneyAndNeverInventsZero()
    {
        var text=DiagnosticText.Text($"수량 {19:N0} / 날짜 {new DateOnly(2026,9,24):yyyy-MM-dd} / {DiagnosticText.Join(" | ",new[] {
            DiagnosticText.Text($"매출 {new DiagnosticMoney(123456,DiagnosticAmountScope.Sales):N0}"),
            DiagnosticText.Text($"매입 {new DiagnosticMoney(234567,DiagnosticAmountScope.Purchase):N0}") })}");
        Assert.Equal("수량 19 / 날짜 2026-09-24 / 매출 비공개 | 매입 234,567",text.Render(false,true));
        Assert.DoesNotContain("123456",text.ToString());
        Assert.DoesNotContain("123456",new DiagnosticMoney(123456,DiagnosticAmountScope.Sales).ToString());
    }

    [Theory]
    [InlineData(false,true,false,false)]
    [InlineData(false,false,true,false)]
    [InlineData(false,true,false,true)]
    [InlineData(false,true,true,true)]
    [InlineData(true,false,true,false)]
    [InlineData(true,true,false,false)]
    [InlineData(true,false,true,true)]
    [InlineData(true,true,true,true)]
    public async Task LinkedDiagnosticRespectsTransactionDirectionAndMixedChannels(bool purchaseVoucher,bool sales,bool purchase,bool mixed)
    {
        await using var f=await Fixture.CreateAsync(purchaseVoucher?VoucherType.Purchase:VoucherType.Sales,sales,purchase);
        var transaction=new LocalTransaction {Id=Guid.NewGuid(),CustomerId=f.CustomerId,
            TenantCode=TenantScopeCatalog.UsenetGroup,OfficeCode=OfficeCodeCatalog.Usenet,ResponsibleOfficeCode=OfficeCodeCatalog.Usenet,
            LinkedInvoiceId=f.InvoiceId,TransactionKind=purchaseVoucher?"전표지급":"전표수금",SettlementAmount=345678,
            ReceiptTotal=!purchaseVoucher?345678:mixed?7:0,PaymentTotal=purchaseVoucher?345678:mixed?7:0,IsDirty=false};
        f.Db.Add(transaction);f.Db.Payments.Add(new LocalPayment {Id=transaction.Id,InvoiceId=f.InvoiceId,Amount=456789});
        await f.Db.SaveChangesAsync();
        var issue=Assert.Single((await f.Service.ScanAsync(f.Session)).Issues,x=>x.Code==DataIntegrityIssueCodes.InvoiceLinkedTransactionPaymentMismatch);
        var invoiceAllowed=purchaseVoucher?purchase:sales;
        Assert.Equal(invoiceAllowed && (!mixed || sales && purchase),issue.CurrentValue.Contains("345,678"));
        Assert.Equal(invoiceAllowed,issue.CurrentValue.Contains("456,789"));
        Assert.Contains("검증전표",issue.CurrentValue);
        Assert.Contains(transaction.Id.ToString("D"),issue.ReviewInfoDisplay);
    }

    [Fact]
    public async Task SaveKeepsSessionStableUntilWorkbookWriteFinishes()
    {
        await using var f=await Fixture.CreateAsync(VoucherType.Sales,true,true);
        using var vm=new DataIntegrityIssueViewModel(f.Service,f.Session,initialScanResult:await f.Service.ScanAsync(f.Session));
        await vm.LoadAsync();Task? change=null;
        using var started=new ManualResetEventSlim();
        using var output=new CallbackStream(()=>
        {
            change=Task.Run(()=> { started.Set();f.Session.Clear(); });
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(f.Session.IsLoggedIn);Assert.False(change.IsCompleted);
        });
        vm.SaveExcel(output);
        Assert.NotNull(change);await change!.WaitAsync(TimeSpan.FromSeconds(5));Assert.False(f.Session.IsLoggedIn);
        output.Position=0;using var workbook=new XLWorkbook(output);
        Assert.Contains("123,456",workbook.Worksheet("상세").Cell(2,10).GetString());
    }

    private sealed class CallbackStream(Action onWrite) : MemoryStream
    {
        private bool called;
        public override void Write(byte[] buffer,int offset,int count) { if(!called) { called=true;onWrite(); } base.Write(buffer,offset,count); }
        public override void Write(ReadOnlySpan<byte> buffer) { if(!called) { called=true;onWrite(); } base.Write(buffer); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TemplateReferenceProtectsMoneyWhileRetainingAssetCounts(bool sales)
    {
        await using var f=await Fixture.CreateAsync(VoucherType.Purchase,sales,true);
        var assetId=Guid.NewGuid();var profileId=Guid.NewGuid();
        f.Db.RentalAssets.Add(new LocalRentalAsset {Id=assetId,CustomerId=f.CustomerId,
            TenantCode=TenantScopeCatalog.UsenetGroup,OfficeCode=OfficeCodeCatalog.Usenet,ResponsibleOfficeCode=OfficeCodeCatalog.Usenet,
            ManagementCompanyCode=OfficeCodeCatalog.Usenet,BillingProfileId=profileId,ManagementNumber="TEST-DIAG",ItemName="보존 장비",
            AssetStatus="임대중",BillingEligibilityStatus="청구대상",MonthlyFee=0});
        f.Db.RentalBillingProfiles.Add(new LocalRentalBillingProfile {Id=profileId,CustomerId=f.CustomerId,CustomerName="보존 거래처",
            TenantCode=TenantScopeCatalog.UsenetGroup,OfficeCode=OfficeCodeCatalog.Usenet,ResponsibleOfficeCode=OfficeCodeCatalog.Usenet,
            ManagementCompanyCode=OfficeCodeCatalog.Usenet,ProfileKey="TEMPLATE",IsActive=true,MonthlyAmount=345678,
            BillingTemplateJson=JsonSerializer.Serialize(new[] {new RentalBillingTemplateItemModel { ItemId=Guid.NewGuid(),DisplayItemName="보존 장비",
                Quantity=1,UnitPrice=345678,Amount=345678,IncludedAssetIds=[assetId] }}) });
        await f.Db.SaveChangesAsync();
        var result=await f.Service.ScanAsync(f.Session);
        var issue=Assert.Single(result.Issues,x=>x.Code==DataIntegrityIssueCodes.RentalBillableAssetWithoutMonthlyFee);
        Assert.Equal(sales,issue.ReviewInfoDisplay.Contains("345,678원"));
        Assert.Contains("포함 자산 1개",issue.ReviewInfoDisplay);
        Assert.Contains("보존 장비",issue.ReviewInfoDisplay);
        Assert.Equal(sales?"0원":"비공개",issue.CurrentValue);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccessChangedDuringScanCannotPublishOrExportLateResult(bool revoke)
    {
        await using var f=await Fixture.CreateAsync(VoucherType.Sales,true,true);
        f.Interceptor.OnNextRead=()=>
        {
            if(revoke)f.Session.RefreshSession("revoke",User(f.Session.User!.UserId,false,false));
            else f.Session.SetSession("other",User(Guid.NewGuid(),true,true));
        };
        var result=await f.Service.ScanAsync(f.Session);
        Assert.Null(f.Interceptor.OnNextRead);
        Assert.DoesNotContain("123,456",JsonSerializer.Serialize(result));
        using var vm=new DataIntegrityIssueViewModel(f.Service,f.Session,initialScanResult:result);
        await vm.LoadAsync();Assert.Empty(vm.Issues);
        using var output=new MemoryStream();Assert.Throws<InvalidOperationException>(()=>vm.SaveExcel(output));Assert.Equal(0,output.Length);
    }

    [Fact]
    public async Task DisposedViewModelDetachesAccessEvents()
    {
        await using var f=await Fixture.CreateAsync(VoucherType.Sales,true,true);
        var vm=new DataIntegrityIssueViewModel(f.Service,f.Session,initialScanResult:await f.Service.ScanAsync(f.Session));
        await vm.LoadAsync();var changed=0;vm.PropertyChanged+=(_,_)=>changed++;
        vm.Dispose();f.Session.Clear();Assert.Equal(0,changed);
        Assert.All(vm.Issues,row=>Assert.DoesNotContain("123,456",row.CurrentValue));
    }

    private sealed class ReadInterceptor : DbCommandInterceptor
    {
        public Action? OnNextRead {get;set;}
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData eventData,
            InterceptionResult<DbDataReader> result,CancellationToken cancellationToken=default)
        {
            var action=OnNextRead;OnNextRead=null;action?.Invoke();return ValueTask.FromResult(result);
        }
    }

    private static UserSessionDto User(Guid id, bool sales, bool purchase) => new()
    {
        UserId=id, Username="diagnostic-fixture", Role=DomainConstants.RoleUser,
        TenantCode=TenantScopeCatalog.UsenetGroup, OfficeCode=OfficeCodeCatalog.Usenet,
        ScopeType=TenantScopeCatalog.ScopeOfficeOnly,
        Permissions=(sales ? new[] { AppPermissionNames.AmountViewSales } : Array.Empty<string>())
            .Concat(purchase ? new[] { AppPermissionNames.AmountViewPurchase } : []).ToList()
    };
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        public LocalDbContext Db { get; }
        public SessionState Session { get; }=new();
        public Guid InvoiceId { get; }=Guid.NewGuid();
        public Guid CustomerId { get; }=Guid.NewGuid();
        public DataIntegrityIssueService Service { get; }
        public ReadInterceptor Interceptor {get;}=new();
        private Fixture(SqliteConnection connection)
        {
            this.connection=connection;
            Db=new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).AddInterceptors(Interceptor).Options);
            Service=new DataIntegrityIssueService(Db);
        }
        public static async Task<Fixture> CreateAsync(VoucherType type, bool sales, bool purchase)
        {
            var connection=new SqliteConnection("Data Source=:memory:");await connection.OpenAsync();
            var f=new Fixture(connection);await f.Db.Database.EnsureCreatedAsync();
            f.Session.SetSession("test",User(Guid.NewGuid(),sales,purchase));
            f.Db.Customers.Add(new LocalCustomer { Id=f.CustomerId, NameOriginal="보존 거래처",NameMatchKey="DIAGNOSTIC",
                TenantCode=TenantScopeCatalog.UsenetGroup,OfficeCode=OfficeCodeCatalog.Usenet,ResponsibleOfficeCode=OfficeCodeCatalog.Usenet });
            f.Db.Invoices.Add(new LocalInvoice { Id=f.InvoiceId, CustomerId=f.CustomerId, TenantCode=TenantScopeCatalog.UsenetGroup,
                OfficeCode=OfficeCodeCatalog.Usenet, ResponsibleOfficeCode=OfficeCodeCatalog.Usenet,
                VoucherType=type, InvoiceNumber="검증전표", IsLatestVersion=true, VatMode=InvoiceVatModes.None,
                TotalAmount=123456, SupplyAmount=123456, IsDirty=false });
            f.Db.InvoiceLines.Add(new LocalInvoiceLine { Id=Guid.NewGuid(), InvoiceId=f.InvoiceId,
                Quantity=2, UnitPrice=117283.5m, LineAmount=234567, ItemNameOriginal="보존 품목" });
            await f.Db.SaveChangesAsync();return f;
        }
        public async Task<DataIntegrityIssueDetail> IssueAsync() => Assert.Single((await Service.ScanAsync(Session)).Issues,
            x=>x.Code==DataIntegrityIssueCodes.InvoiceAmountMismatch);
        public async ValueTask DisposeAsync() { await Db.DisposeAsync();await connection.DisposeAsync(); }
    }
}
