using System.Windows.Documents;
using System.Windows.Threading;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Printing;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalDocumentAuthorizationTests
{
    [Fact]
    public void ContractRefresh_PrintsCurrentDraftThroughSharedService_AndKeepsCurrentPage()
        => RunSta(() => {
            var service = new RecordingPrintService();
            var vm = CreateEditor(() => true, service);
            var first = vm.PreviewDocument;
            vm.CurrentPageNumberProvider = () => 2;
            vm.PrintCommand.Execute(null);
            Assert.Same(first, service.Document);
            Assert.Equal(2, service.CurrentPage);
            vm.MonthlyFee = 220000;
            vm.RefreshPreviewCommand.Execute(null);
            Assert.NotSame(first, vm.PreviewDocument);
            Assert.Equal(220000, vm.BuildModel().MonthlyFee);
            vm.PrintCommand.Execute(null);
            Assert.Same(vm.PreviewDocument, service.Document);
            Assert.Equal(2, service.Calls);
        });

    [Theory]
    [InlineData("logout")]
    [InlineData("office")]
    [InlineData("permission")]
    [InlineData("replacement")]
    public void OwnerChange_RevokesOldContractAndRefreshCannotReactivateIt(string change)
        => RunSta(() => {
            var session = CreateSession();
            var service = new RecordingPrintService();
            var vm = CreateEditor(PrintDocumentAuthorization.CaptureOwner(session), service);
            var old = vm.PreviewDocument!;
            var originalId = session.SessionId;
            var userId = session.User!.UserId;
            switch (change)
            {
                case "logout": session.Clear(); break;
                case "office": session.RefreshSession("fixture", User(userId, OfficeCodeCatalog.Yeonsu), DateTime.UtcNow.AddHours(1)); break;
                case "permission": session.RefreshSession("fixture", User(userId, permissions: []), DateTime.UtcNow.AddHours(1)); break;
                default: session.SetSession("fixture", User(Guid.NewGuid()), DateTime.UtcNow.AddHours(1)); break;
            }
            if (change is "office" or "permission") Assert.Equal(originalId, session.SessionId);
            vm.PrintCommand.Execute(null);
            Assert.Equal(0, service.Calls);
            Assert.Null(vm.PreviewDocument);
            Assert.False(vm.PrintCommand.CanExecute(null));
            Assert.False(PrintDocumentAuthorization.Validate(old, out _));
            session.SetSession("fixture", User(userId), DateTime.UtcNow.AddHours(1));
            vm.RefreshPreviewCommand.Execute(null);
            Assert.Null(vm.PreviewDocument);
            var fresh = CreateEditor(PrintDocumentAuthorization.CaptureOwner(session), service);
            Assert.NotNull(fresh.PreviewDocument);
            Assert.True(fresh.PrintCommand.CanExecute(null));
        });

    [Fact]
    public void TokenRefreshWithSameScope_DoesNotCloseValidContract()
        => RunSta(() => {
            var session = CreateSession();
            var vm = CreateEditor(PrintDocumentAuthorization.CaptureOwner(session), new RecordingPrintService());
            var epoch = session.SyncScopeEpoch;
            session.RefreshSession("renewed-fixture", User(session.User!.UserId), DateTime.UtcNow.AddHours(2));
            Assert.Equal(epoch, session.SyncScopeEpoch);
            Assert.True(PrintDocumentAuthorization.Validate(vm.PreviewDocument!, out _));
        });

    [Fact]
    public void ContractMonitor_ClosesEditorAndClearsPreviewWithoutPrintClick()
        => RunSta(() => {
            var allowed = true;
            var vm = CreateEditor(() => allowed, new RecordingPrintService());
            var closeRequests = 0;
            vm.RequestClose += () => closeRequests++;
            using var monitor = vm.MonitorAuthorization();
            allowed = false;
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame);
            Assert.Equal(1, closeRequests);
            Assert.Null(vm.PreviewDocument);
            Assert.False(vm.PrintCommand.CanExecute(null));
        });

    [Fact]
    public void RefreshedContract_CarriesAuthorizationIntoFinalPrintBoundary()
        => RunSta(() => {
            var allowed = true;
            var service = new RecordingPrintService { BeforeReturn = document => {
                allowed = false;
                Assert.False(TradePrintExecutor.TryPrintDocument(document, "RENTAL-AUTH-FIXTURE", out var error));
                Assert.Equal(PrintDocumentAuthorization.DeniedMessage, error);
            }};
            var vm = CreateEditor(() => allowed, service);
            vm.RefreshPreviewCommand.Execute(null);
            vm.PrintCommand.Execute(null);
            Assert.Equal(1, service.Calls);
            Assert.Null(vm.PreviewDocument);
        });

    [Theory]
    [InlineData("equipment")]
    [InlineData("return")]
    [InlineData("contract")]
    public void RentalCommands_RejectLoggedOutDraftBeforeDatabaseRead(string command)
        => RunSta(() => {
            // No schema is created. Reaching a DB query would fail this test.
            using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite("Data Source=:memory:").Options);
            var session = new SessionState();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var vm = new RentalAssetViewModel(new RentalStateService(db, local), local, new RentalDocumentService(), new RecordingPrintService(), session);
            try
            {
                var operation = command switch {
                    "equipment" => vm.OpenEquipmentDetailCommand,
                    "return" => vm.OpenReturnReportCommand,
                    _ => vm.OpenContractWriterCommand
                };
                operation.ExecuteAsync(null).GetAwaiter().GetResult();
                Assert.Equal(PrintDocumentAuthorization.DeniedMessage, vm.StatusMessage);
            }
            finally { vm.CancelPendingBackgroundWork(); }
        });

    private static RentalContractEditorViewModel CreateEditor(Func<bool> allowed, IPrintService service)
        => new(new RentalContractDocumentModel { TenantName = "SYNTHETIC", CompanyName = "TEST", MonthlyFee = 110000 },
            new RentalDocumentService(), canReadDocument: allowed, printService: service);
    private static UserSessionDto User(Guid id, string office = OfficeCodeCatalog.Usenet, List<string>? permissions = null)
        => new() { UserId = id, Username = "rental-document-fixture", Role = DomainConstants.RoleUser,
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = office, ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = permissions ?? [AppPermissionNames.RentalAssetEdit] };
    private static SessionState CreateSession()
    {
        var session = new SessionState();
        session.SetSession("fixture", User(Guid.NewGuid()), DateTime.UtcNow.AddHours(1));
        return session;
    }
    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Rental document test timed out.");
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
    private sealed class RecordingPrintService : IPrintService
    {
        public int Calls;
        public IDocumentPaginatorSource? Document;
        public int? CurrentPage;
        public Action<IDocumentPaginatorSource>? BeforeReturn;
        public bool TryPrint(IDocumentPaginatorSource document, string jobName, out string? errorMessage, int? currentPageNumber = null)
        { Calls++; Document = document; CurrentPage = currentPageNumber; BeforeReturn?.Invoke(document); errorMessage = null; return false; }
        public InvoicePrintModel CreateDefaultModel(LocalInvoice invoice, LocalCustomer customer, LocalCompanyProfile company, bool printWithDate, bool printWithPrice) => throw new NotSupportedException();
        public FixedDocument BuildFixedDocument(InvoicePrintModel model) => throw new NotSupportedException();
    }
}
