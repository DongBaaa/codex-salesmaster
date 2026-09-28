using System.Windows.Documents;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Printing;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Desktop.App.Views;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class PrintDocumentAuthorizationTests
{
    [Theory]
    [InlineData("customer-scope")]
    [InlineData("customer-deleted")]
    [InlineData("invoice-deleted")]
    [InlineData("invoice-customer-changed")]
    [InlineData("logout")]
    [InlineData("new-session")]
    [InlineData("office-change")]
    [InlineData("amount-permission")]
    [InlineData("invoice-amount-hidden")]
    [InlineData("line-amount-hidden")]
    [InlineData("payment-amount-hidden")]
    public async Task CapturedInvoiceAccess_SeesCommittedRevocationWithoutChangingDirtyRows(string change)
    {
        var folder = Path.Combine(Path.GetTempPath(), "trade-output-access-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var options = new DbContextOptionsBuilder<LocalDbContext>().UseSqlite("Data Source=" + Path.Combine(folder, "fixture.db")).Options;
        await using var db = new LocalDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var session = CreateSession();
        var customer = new LocalCustomer { Id=Guid.NewGuid(), NameOriginal="PRIVATE", NameMatchKey="PRIVATE", Revision=5, IsDirty=true,
            TenantCode=session.TenantCode, OfficeCode=session.OfficeCode, ResponsibleOfficeCode=session.OfficeCode, Notes="KEEP-DIRTY" };
        var invoice = new LocalInvoice { Id=Guid.NewGuid(), CustomerId=customer.Id, TenantCode=session.TenantCode, OfficeCode=session.OfficeCode,
            ResponsibleOfficeCode=session.OfficeCode, InvoiceNumber="OUTPUT-ACCESS", InvoiceDate=new DateOnly(2026,9,8), VersionGroupId=Guid.NewGuid(), IsLatestVersion=true, Revision=5 };
        db.Customers.Add(customer); db.Invoices.Add(invoice); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
        var access = local.CreateInvoicePrintAuthorization(invoice.Id, customer.Id, session);
        Assert.True(access());
        switch(change)
        {
            case "customer-scope":
                await using (var transaction=await db.Database.BeginTransactionAsync())
                {
                    await local.ApplyServerCustomerScopeSnapshotAsync(new CustomerScopeSnapshotDto {
                        Version=1, UserId=session.User!.UserId, TenantCode=session.AuthenticatedTenantCode,
                        OfficeCode=session.OfficeCode, ScopeType=session.ScopeType, VisibleCustomerIds=[] }, session, CancellationToken.None);
                    await transaction.CommitAsync();
                }
                break;
            case "customer-deleted": await db.Customers.ExecuteUpdateAsync(set=>set.SetProperty(row=>row.IsDeleted,true)); break;
            case "invoice-deleted": await db.Invoices.ExecuteUpdateAsync(set=>set.SetProperty(row=>row.IsDeleted,true)); break;
            case "invoice-customer-changed":
                var replacement = new LocalCustomer { Id=Guid.NewGuid(), NameOriginal="OTHER", NameMatchKey="OTHER", TenantCode=session.TenantCode, OfficeCode=session.OfficeCode, ResponsibleOfficeCode=session.OfficeCode };
                db.Customers.Add(replacement); await db.SaveChangesAsync();
                await db.Invoices.ExecuteUpdateAsync(set=>set.SetProperty(row=>row.CustomerId,replacement.Id));
                break;
            case "logout": session.Clear(); break;
            case "new-session": session.SetOfflineSession(CreateSession().User!); break;
            case "office-change": session.SetOfficeCode(OfficeCodeCatalog.Yeonsu); break;
            case "amount-permission": session.User!.Permissions.Remove(AppPermissionNames.AmountViewSales); break;
            case "invoice-amount-hidden": await db.Invoices.ExecuteUpdateAsync(set=>set.SetProperty(row=>row.AmountsHidden,true)); break;
            case "line-amount-hidden":
                db.InvoiceLines.Add(new LocalInvoiceLine { Id=Guid.NewGuid(), InvoiceId=invoice.Id, AmountsHidden=true });
                await db.SaveChangesAsync();
                break;
            case "payment-amount-hidden":
                db.Payments.Add(new LocalPayment { Id=Guid.NewGuid(), InvoiceId=invoice.Id, AmountsHidden=true });
                await db.SaveChangesAsync();
                break;
        }
        Assert.False(access());
        var retained=await db.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(row=>row.Id==customer.Id);
        Assert.True(retained.IsDirty); Assert.Equal("KEEP-DIRTY",retained.Notes);
    }

    [Theory]
    [InlineData(VoucherType.Sales, AppPermissionNames.AmountViewSales)]
    [InlineData(VoucherType.Collection, AppPermissionNames.AmountViewSales)]
    [InlineData(VoucherType.Purchase, AppPermissionNames.AmountViewPurchase)]
    [InlineData(VoucherType.Procurement, AppPermissionNames.AmountViewPurchase)]
    [InlineData(VoucherType.Expense, AppPermissionNames.AmountViewPurchase)]
    public async Task MonetaryPrintRequiresMatchingPermissionAndKnownAmountsWithoutMutatingInvoice(VoucherType type, string permission)
    {
        var folder = Path.Combine(Path.GetTempPath(), "trade-output-money-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var options = new DbContextOptionsBuilder<LocalDbContext>().UseSqlite("Data Source=" + Path.Combine(folder, "fixture.db")).Options;
        await using var db = new LocalDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var session = CreateSession();
        var customer = new LocalCustomer { Id=Guid.NewGuid(), NameOriginal="MONEY-PROBE", TenantCode=session.TenantCode,
            OfficeCode=session.OfficeCode, ResponsibleOfficeCode=session.OfficeCode };
        var invoice = new LocalInvoice { Id=Guid.NewGuid(), CustomerId=customer.Id, VoucherType=type, TenantCode=session.TenantCode,
            OfficeCode=session.OfficeCode, ResponsibleOfficeCode=session.OfficeCode, InvoiceDate=new DateOnly(2026,9,24),
            VersionGroupId=Guid.NewGuid(), IsLatestVersion=true, TotalAmount=98765, IsDirty=true, Memo="KEEP-EDIT" };
        db.Customers.Add(customer); db.Invoices.Add(invoice);
        db.InvoiceLines.Add(new LocalInvoiceLine { Id=Guid.NewGuid(), InvoiceId=invoice.Id, IsDeleted=true, AmountsHidden=true });
        db.Payments.Add(new LocalPayment { Id=Guid.NewGuid(), InvoiceId=invoice.Id, IsDeleted=true, AmountsHidden=true });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
        var access = local.CreateInvoicePrintAuthorization(invoice.Id, customer.Id, session);
        session.User!.Permissions.Remove(permission); // The other money permission must not authorize this document.
        Assert.False(access());
        session.User.Permissions.Add(permission);
        Assert.True(access()); // A deleted hidden row must not revoke an otherwise readable document.
        await db.Invoices.ExecuteUpdateAsync(set=>set.SetProperty(row=>row.AmountsHidden,true));
        Assert.False(access()); // Even a newly granted permission cannot make an unknown cached price valid.
        var retained=await db.Invoices.IgnoreQueryFilters().AsNoTracking().SingleAsync(row=>row.Id==invoice.Id);
        Assert.Equal(98765m,retained.TotalAmount);
        Assert.True(retained.IsDirty);
        Assert.Equal("KEEP-EDIT",retained.Memo);
    }

    [Fact]
    public void PreparationOwner_DoesNotFollowAReplacementSession()
    {
        var session=CreateSession();
        var owner=PrintDocumentAuthorization.CaptureOwner(session);
        Assert.True(owner());
        session.SetOfflineSession(CreateSession().User!);
        Assert.False(owner());
    }

    [Theory]
    [InlineData(TradePrintFileFormat.Pdf)]
    [InlineData(TradePrintFileFormat.Xps)]
    public void RevocationWhilePrintDialogIsOpen_PreventsFinalFileWrite(TradePrintFileFormat format)
    {
        RunOnSta(() => {
            var allowed=true;
            var document=new FixedDocument();
            document.Pages.Add(new PageContent { Child=new FixedPage { Width=793.7,Height=1122.5 } });
            PrintDocumentAuthorization.Attach(document,()=>allowed);
            var output=Path.Combine(Path.GetTempPath(),"must-not-exist-"+Guid.NewGuid().ToString("N")+"."+format.ToString().ToLowerInvariant());
            var dialogDriven=false;
            var choose=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(100) };
            choose.Tick+=(_,_)=>{
                var dialog=PresentationSource.CurrentSources.OfType<HwndSource>().Select(source=>source.RootVisual).OfType<TradePrintWindow>().FirstOrDefault();
                if(dialog is null) return;
                choose.Stop();
                allowed=false; // Models a completed permission change while the nested dialog is open.
                typeof(TradePrintWindow).GetProperty(nameof(TradePrintWindow.PrintOptions))!.SetValue(dialog,
                    new TradePrintDialogResult(null,1,true,null,false,SaveToFile:true,OutputFilePath:output,FileFormat:format));
                dialogDriven=true;
                dialog.DialogResult=true;
            };
            choose.Start();
            try
            {
                Assert.False(TradePrintExecutor.TryPrintDocument(document,"SYNTHETIC",out var error));
                Assert.True(dialogDriven,"The real nested print dialog must be reached before revocation.");
                Assert.Equal(PrintDocumentAuthorization.DeniedMessage,error);
                Assert.False(File.Exists(output));
            }
            finally { choose.Stop(); }
        });
    }

    [Fact]
    public void PreviewRevocation_BlanksDocumentAndStopsPrintService_WithoutReauthorizingOldDocument()
    {
        RunOnSta(() => {
            var allowed=true;
            var document=new FixedDocument();
            PrintDocumentAuthorization.Attach(document,()=>allowed);
            var service=new RecordingPrintService();
            var vm=new PrintPreviewViewModel(document,service,"SYNTHETIC");
            vm.PrintCommand.Execute(null);
            Assert.Equal(1,service.Calls);
            allowed=false;
            using var monitor=vm.MonitorAuthorization();
            Assert.NotSame(document,vm.Document);
            Assert.Empty(((FixedDocument)vm.Document).Pages);
            Assert.False(vm.PrintCommand.CanExecute(null));
            vm.PrintCommand.Execute(null);
            Assert.Equal(1,service.Calls);
            allowed=true;
            Assert.False(PrintDocumentAuthorization.Validate(document,out _));
            var reopened=new FixedDocument();
            PrintDocumentAuthorization.Attach(reopened,()=>allowed);
            Assert.True(PrintDocumentAuthorization.Validate(reopened,out _));
        });
    }

    [Fact]
    public void BackgroundMonitor_RemovesVisibleDocumentWithoutAnotherPrintClick()
    {
        RunOnSta(() => {
            var allowed=true;
            var document=new FixedDocument();
            PrintDocumentAuthorization.Attach(document,()=>allowed);
            var vm=new PrintPreviewViewModel(document,new RecordingPrintService(),"SYNTHETIC");
            using var monitor=vm.MonitorAuthorization();
            allowed=false;
            var frame=new DispatcherFrame();
            var timeout=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(1500) };
            timeout.Tick+=(_,_)=>{timeout.Stop();frame.Continue=false;};
            timeout.Start(); Dispatcher.PushFrame(frame);
            Assert.NotSame(document,vm.Document);
            Assert.False(vm.PrintCommand.CanExecute(null));
        });
    }

    [Fact]
    public void UnavailablePermissionRead_FailsClosedAndDoesNotOpenPrinterDialog()
    {
        RunOnSta(() => {
            var fail=false;
            var document=new FixedDocument();
            PrintDocumentAuthorization.Attach(document,()=>fail ? throw new IOException("fixture-unavailable") : true);
            fail=true;
            Assert.False(TradePrintExecutor.TryPrintDocument(document,"SYNTHETIC",out var error));
            Assert.Equal(PrintDocumentAuthorization.DeniedMessage,error);
        });
    }

    private static SessionState CreateSession()
    {
        var session=new SessionState();
        session.SetOfflineSession(new UserSessionDto { UserId=Guid.NewGuid(), Username="output-probe", Role=DomainConstants.RoleUser,
            TenantCode=TenantScopeCatalog.UsenetGroup, OfficeCode=OfficeCodeCatalog.Usenet, ScopeType=TenantScopeCatalog.ScopeOfficeOnly,
            Permissions=[AppPermissionNames.AmountViewSales, AppPermissionNames.AmountViewPurchase] });
        return session;
    }
    private static void RunOnSta(Action action)
    {
        Exception? error=null;
        var thread=new Thread(()=>{try{action();}catch(Exception ex){error=ex;}});
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if(error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
    private sealed class RecordingPrintService : IPrintService
    {
        public int Calls;
        public bool TryPrint(IDocumentPaginatorSource document,string jobName,out string? errorMessage,int? currentPageNumber=null)
        { Calls++; errorMessage=null; return false; }
        public InvoicePrintModel CreateDefaultModel(LocalInvoice invoice,LocalCustomer customer,LocalCompanyProfile company,bool printWithDate,bool printWithPrice)=>throw new NotSupportedException();
        public FixedDocument BuildFixedDocument(InvoicePrintModel model)=>throw new NotSupportedException();
    }
}
