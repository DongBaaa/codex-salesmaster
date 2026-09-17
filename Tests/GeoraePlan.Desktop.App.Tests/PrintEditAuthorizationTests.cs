using System.Windows;
using System.Windows.Documents;
using System.Windows.Threading;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Printing;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class PrintEditAuthorizationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AllowedDraft_ManualAndCloseSaveStillPersist(bool automatic)
    {
        Sta(() => {
            InvoicePrintModel? stored=null;
            using var vm=Create(m=>{stored=m;return Task.CompletedTask;},()=>true);
            vm.Memo="EDITED";
            if(automatic)Assert.True(vm.TryAutoSaveOnCloseAsync().GetAwaiter().GetResult());
            else vm.SaveCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Assert.Equal("EDITED",stored!.Memo);
            Assert.True(vm.WasSaved);
            Assert.False(vm.HasPendingChanges);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RevokedDraft_ManualAndCloseSaveDoNotInvokePersistence(bool automatic)
    {
        Sta(() => {
            var allowed=true;var writes=0;
            using var vm=Create(_=>{writes++;return Task.CompletedTask;},()=>allowed);
            var retained=vm.PreviewDocument!;
            vm.Memo="PRIVATE-DRAFT";
            allowed=false;
            if(automatic)Assert.False(vm.TryAutoSaveOnCloseAsync().GetAwaiter().GetResult());
            else vm.SaveCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Assert.Equal(0,writes);Assert.False(vm.WasSaved);
            Assert.True(vm.IsAuthorizationInvalidated);
            Assert.NotSame(retained,vm.PreviewDocument);
            Assert.False(PrintDocumentAuthorization.Validate(retained,out _));
            allowed=true;
            Assert.False(vm.ValidateAuthorization());
            Assert.False(vm.SaveCommand.CanExecute(null));
        });
    }

    [Fact]
    public void BackgroundRevocation_InvalidatesWithoutAnotherUserAction()
    {
        Sta(() => {
            var allowed=true;var invalidations=0;
            using var vm=Create(_=>Task.CompletedTask,()=>allowed);
            vm.AuthorizationInvalidated+=()=>invalidations++;
            using var monitor=vm.MonitorAuthorization();
            allowed=false;
            var frame=new DispatcherFrame();
            var timeout=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(1300) };
            timeout.Tick+=(_,_)=>{timeout.Stop();frame.Continue=false;};
            timeout.Start();Dispatcher.PushFrame(frame);
            Assert.Equal(1,invalidations);Assert.True(vm.IsAuthorizationInvalidated);
            Assert.False(vm.PrintCommand.CanExecute(null));
        });
    }

    [Fact]
    public void PermissionReadFailure_FailsClosedWithoutSaving()
    {
        Sta(() => {
            var fail=false;var writes=0;
            using var vm=Create(_=>{writes++;return Task.CompletedTask;},()=>fail?throw new IOException("fixture"):true);
            vm.Memo="PRIVATE";fail=true;
            Assert.False(vm.TryAutoSaveOnCloseAsync().GetAwaiter().GetResult());
            Assert.Equal(0,writes);Assert.True(vm.IsAuthorizationInvalidated);
        });
    }

    [Fact]
    public void InitialDenial_DoesNotConstructAnEditablePreview()
    {
        Sta(()=>Assert.Throws<UnauthorizedAccessException>(()=>Create(_=>Task.CompletedTask,()=>false)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Print_UsesGuardedServiceOnlyWhileAuthorized(bool revoked)
    {
        Sta(() => {
            var allowed=true;var printer=new RecordingPrinter();
            using var vm=Create(_=>Task.CompletedTask,()=>allowed,printer);
            allowed=!revoked;vm.PrintCommand.Execute(null);
            Assert.Equal(revoked?0:1,printer.Calls);
            Assert.False(vm.IsPrinting);
        });
    }

    [Fact]
    public void RevocationInsidePrint_DoesNotCloseOwnerOrSaveDraft()
    {
        Sta(() => {
            var allowed=true;var writes=0;
            var printer=new RecordingPrinter();
            using var vm=Create(_=>{writes++;return Task.CompletedTask;},()=>allowed,printer);
            vm.Memo="DRAFT";
            printer.DuringPrint=()=>{
                Assert.True(vm.IsPrinting);Assert.False(vm.CloseCommand.CanExecute(null));
                allowed=false;
                Assert.False(vm.TryAutoSaveOnCloseAsync().GetAwaiter().GetResult());
            };
            vm.PrintCommand.Execute(null);
            Assert.Equal(0,writes);Assert.True(vm.IsAuthorizationInvalidated);
            Assert.False(vm.IsPrinting);Assert.True(vm.CloseCommand.CanExecute(null));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetadataWrite_RechecksAfterLookupWithoutMutatingExistingOrNewRows(bool existing)
    {
        await using var db=new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite("Data Source=:memory:").Options);
        await db.Database.OpenConnectionAsync();await db.Database.EnsureCreatedAsync();
        var local=new LocalStateService(db,new OfficeAccessService(),new SyncRequestDispatcher(),new SessionState());
        var id=Guid.NewGuid();
        if(existing)await local.SaveInvoicePrintPayloadAsync(id,"BASELINE");
        var checks=0;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>local.SaveInvoicePrintPayloadAsync(id,"REVOKED",canWrite:()=>++checks==1));
        Assert.Equal(2,checks);
        Assert.Equal(existing?"BASELINE":null,await local.GetInvoicePrintPayloadAsync(id));
        Assert.False(db.ChangeTracker.HasChanges());
    }

    private static PrintEditViewModel Create(Func<InvoicePrintModel,Task> save,Func<bool> allowed,IPrintService? printer=null)
        =>new(new InvoicePrintModel {InvoiceId=Guid.NewGuid(),BuyerName="PRIVATE",Memo="BASELINE"},save,(_,_)=>new FixedDocument(),allowed,printer);
    private static void Sta(Action action)
    {
        Exception? error=null;
        var thread=new Thread(()=>{try{action();}catch(Exception ex){error=ex;}});
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if(error is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
    private sealed class RecordingPrinter:IPrintService
    {
        public int Calls;public Action? DuringPrint;
        public bool TryPrint(IDocumentPaginatorSource document,string jobName,out string? errorMessage,int? currentPageNumber=null)
        {Calls++;Assert.True(PrintDocumentAuthorization.Validate(document,out _));DuringPrint?.Invoke();errorMessage=null;return false;}
        public InvoicePrintModel CreateDefaultModel(LocalInvoice i,LocalCustomer c,LocalCompanyProfile p,bool d,bool price)=>throw new NotSupportedException();
        public FixedDocument BuildFixedDocument(InvoicePrintModel model)=>throw new NotSupportedException();
    }
}
