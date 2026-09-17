using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalLegacyDraftRecoveryTests
{
    [Theory]
    [InlineData(RentalDraftKind.Billing)]
    [InlineData(RentalDraftKind.Onboarding)]
    public async Task ConfirmedRecovery_PreservesOriginalAndRestoresOnlyChosenBusinessOnce(RentalDraftKind kind)
    {
        await using var f=await Fixture.Create(kind);
        var preview=Assert.IsType<RentalLegacyDraftPreview>(await f.Rental.GetLegacyDraftPreviewAsync(kind,f.Session));
        Assert.False(preview.TargetHasDraft);Assert.Equal("LEGACY-CUSTOMER",preview.CustomerName);
        await f.Rental.RecoverLegacyDraftAsync(preview,f.Session);
        Assert.Equal("LEGACY-CUSTOMER",await f.ReadCurrent());
        Assert.Equal(Fixture.Payload,await f.ReadOriginal());
        Assert.Null(await f.Rental.GetLegacyDraftPreviewAsync(kind,f.Session));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Rental.RecoverLegacyDraftAsync(preview,f.Session));
        f.Session.SetBusinessDatabase("ITWORLD");
        Assert.Null(await f.ReadCurrent());Assert.Null(await f.Rental.GetLegacyDraftPreviewAsync(kind,f.Session));
        Assert.Equal(0,await f.Db.RentalBillingProfiles.CountAsync());Assert.Equal(0,await f.Db.Customers.CountAsync());
    }

    [Theory]
    [InlineData(RentalDraftKind.Billing,"scope")]
    [InlineData(RentalDraftKind.Onboarding,"scope")]
    [InlineData(RentalDraftKind.Billing,"source")]
    [InlineData(RentalDraftKind.Onboarding,"source")]
    [InlineData(RentalDraftKind.Billing,"target")]
    [InlineData(RentalDraftKind.Onboarding,"target")]
    [InlineData(RentalDraftKind.Billing,"pending")]
    [InlineData(RentalDraftKind.Onboarding,"pending")]
    public async Task ChangedContextOrDraft_BlocksRecoveryWithoutOverwriting(RentalDraftKind kind,string change)
    {
        await using var f=await Fixture.Create(kind);
        var preview=(await f.Rental.GetLegacyDraftPreviewAsync(kind,f.Session))!;
        if(change=="scope") {f.Session.SetBusinessDatabase("ITWORLD");f.Session.SetBusinessDatabase("USENET");}
        if(change=="source")await f.Db.Settings.Where(x=>x.Key==f.LegacyKey).ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Value,"{}"));
        if(change=="target")await f.SaveCurrent("CURRENT-PENDING");
        if(change=="pending")f.Db.Settings.Add(new LocalSetting {Key="UNRELATED-PENDING",Value="do not save"});
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Rental.RecoverLegacyDraftAsync(preview,f.Session));
        Assert.Equal(change=="target"?"CURRENT-PENDING":null,await f.ReadCurrent());
        Assert.Equal(change=="source"?"{}":Fixture.Payload,await f.ReadOriginal());
        Assert.False(await f.Db.Settings.AsNoTracking().AnyAsync(x=>x.Key.Contains(".RECOVERED.")));
        Assert.False(await f.Db.Settings.AsNoTracking().AnyAsync(x=>x.Key=="UNRELATED-PENDING"));
    }

    [Theory]
    [InlineData(RentalDraftKind.Billing)]
    [InlineData(RentalDraftKind.Onboarding)]
    public async Task FailedSave_RollsBackDraftAndMarkerPreservingLegacy(RentalDraftKind kind)
    {
        var fault=new FailSave();
        await using var f=await Fixture.Create(kind,fault);
        var preview=(await f.Rental.GetLegacyDraftPreviewAsync(kind,f.Session))!;
        fault.Fail=true;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Rental.RecoverLegacyDraftAsync(preview,f.Session));
        Assert.Null(await f.ReadCurrent());Assert.Equal(Fixture.Payload,await f.ReadOriginal());
        Assert.False(f.Db.ChangeTracker.HasChanges());
        Assert.False(await f.Db.Settings.AnyAsync(x=>x.Key.Contains(".RECOVERED.")));
    }

    [Theory]
    [InlineData(RentalDraftKind.Billing,"cancel")]
    [InlineData(RentalDraftKind.Onboarding,"cancel")]
    [InlineData(RentalDraftKind.Billing,"edit")]
    [InlineData(RentalDraftKind.Onboarding,"edit")]
    [InlineData(RentalDraftKind.Billing,"recover")]
    [InlineData(RentalDraftKind.Onboarding,"recover")]
    [InlineData(RentalDraftKind.Billing,"target")]
    [InlineData(RentalDraftKind.Onboarding,"target")]
    public async Task RecoveryCommand_RequiresConfirmationAndUnchangedEditor(RentalDraftKind kind,string action)
    {
        await using var f=await Fixture.Create(kind);
        var editor="before";var confirms=0;var restored=false;var status="";
        if(action=="target")await f.SaveCurrent("CURRENT-PENDING");
        var state=new RentalLegacyDraftRecoveryState(f.Rental,f.Session,kind,()=>Task.CompletedTask,()=>editor,
            ()=>{restored=true;return Task.CompletedTask;},()=>false,s=>status=s);
        state.ConfirmRecoveryAsync=_=>{confirms++;if(action=="edit")editor="new input";return Task.FromResult(action!="cancel");};
        await state.RefreshAsync();Assert.True(state.IsAvailable);
        await state.RecoverCommand.ExecuteAsync(null);
        Assert.Equal(action=="target"?0:1,confirms);
        Assert.Equal(action=="recover",restored);
        Assert.Equal(action=="recover"?"LEGACY-CUSTOMER":action=="target"?"CURRENT-PENDING":null,await f.ReadCurrent());
        Assert.Equal(Fixture.Payload,await f.ReadOriginal());Assert.NotEmpty(status);
    }

    [Fact]
    public async Task FullBillingLoad_OffersLegacyRecoveryWithoutAutomaticAttribution()
    {
        await using var f=await Fixture.Create(RentalDraftKind.Billing);
        var local=new LocalStateService(f.Db,new OfficeAccessService(),new SyncRequestDispatcher(),f.Session);
        var rental=new RentalStateService(f.Db,local);
        var vm=new RentalBillingViewModel(rental,local,f.Session);
        try
        {
            await vm.LoadAsync();
            Assert.True(vm.LegacyDraftRecovery.IsAvailable);
            Assert.NotEqual("LEGACY-CUSTOMER",vm.EditCustomerName);
            vm.LegacyDraftRecovery.ConfirmRecoveryAsync=_=>Task.FromResult(true);
            await vm.LegacyDraftRecovery.RecoverCommand.ExecuteAsync(null);
            Assert.True(vm.EditCustomerName=="LEGACY-CUSTOMER",vm.StatusMessage);
            Assert.False(vm.LegacyDraftRecovery.IsAvailable);
        }
        finally {await vm.CancelAndDrainPendingBackgroundWorkAsync();}
    }

    [Fact]
    public async Task FullOnboardingLoad_CancelAndEmptyFlushPreserveLegacy_ThenConfirmedRecoveryRestores()
    {
        await using var f = await Fixture.Create(RentalDraftKind.Onboarding);
        var local = new LocalStateService(f.Db, new OfficeAccessService(), new SyncRequestDispatcher(), f.Session);
        var rental = new RentalStateService(f.Db, local);
        var vm = new RentalCustomerOnboardingViewModel(rental, local, f.Session);
        await vm.LoadAsync();
        Assert.True(vm.LegacyDraftRecovery.IsAvailable);
        Assert.NotEqual("LEGACY-CUSTOMER", vm.CustomerName);
        vm.LegacyDraftRecovery.ConfirmRecoveryAsync = _ => Task.FromResult(false);
        await vm.LegacyDraftRecovery.RecoverCommand.ExecuteAsync(null);
        await vm.FlushAutoSaveAsync();
        Assert.Null(await f.ReadCurrent());
        Assert.Equal(Fixture.Payload, await f.ReadOriginal());
        Assert.False(await f.Db.Settings.AnyAsync(x => x.Key.Contains(".RECOVERED.")));
        vm.LegacyDraftRecovery.ConfirmRecoveryAsync = _ => Task.FromResult(true);
        await vm.LegacyDraftRecovery.RecoverCommand.ExecuteAsync(null);
        Assert.True(vm.CustomerName == "LEGACY-CUSTOMER", vm.StatusMessage);
        Assert.Equal("preserve me", vm.Notes);
        await vm.FlushAutoSaveAsync();
        Assert.Equal("LEGACY-CUSTOMER", await f.ReadCurrent());
        Assert.Equal(0, await f.Db.Customers.CountAsync());
        Assert.Equal(0, await f.Db.RentalBillingProfiles.CountAsync());
    }

    [Theory]
    [InlineData(RentalDraftKind.Billing)]
    [InlineData(RentalDraftKind.Onboarding)]
    public async Task LogoutAfterPreview_BlocksRecoveryAndKeepsOriginal(RentalDraftKind kind)
    {
        await using var f = await Fixture.Create(kind);
        var preview = (await f.Rental.GetLegacyDraftPreviewAsync(kind, f.Session))!;
        f.Session.Clear();
        Assert.Null(await f.Rental.GetLegacyDraftPreviewAsync(kind, f.Session));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Rental.RecoverLegacyDraftAsync(preview, f.Session));
        Assert.Equal(Fixture.Payload, await f.ReadOriginal());
        Assert.Equal(1, await f.Db.Settings.CountAsync());
    }

    [Theory]
    [InlineData(RentalDraftKind.Billing)]
    [InlineData(RentalDraftKind.Onboarding)]
    public async Task MalformedLegacy_PreservesPayloadAndReportsReadableFailure(RentalDraftKind kind)
    {
        await using var f = await Fixture.Create(kind);
        await f.Db.Settings.Where(x => x.Key == f.LegacyKey).ExecuteUpdateAsync(s => s.SetProperty(x => x.Value, "{broken"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Rental.GetLegacyDraftPreviewAsync(kind, f.Session));
        Assert.Contains("원문은 보관", ex.Message);
        Assert.Equal("{broken", await f.ReadOriginal());
        Assert.Equal(1, await f.Db.Settings.CountAsync());
    }

    private sealed class FailSave:SaveChangesInterceptor
    {
        public bool Fail;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,InterceptionResult<int> result,CancellationToken cancellationToken=default)
        {
            if(Fail)throw new InvalidOperationException("injected recovery save failure");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class Fixture:IAsyncDisposable
    {
        public const string Payload="{\"CustomerName\":\"LEGACY-CUSTOMER\",\"OfficeCode\":\"USENET\",\"LinkAssetsLater\":true,\"Notes\":\"preserve me\"}";
        public required SqliteConnection Connection;
        public required LocalDbContext Db;
        public required SessionState Session;
        public required RentalStateService Rental;
        public required RentalDraftKind Kind;
        public string LegacyKey=>(Kind==RentalDraftKind.Billing?"RENTAL.BILLINGEDITORDRAFT":"RENTAL.ONBOARDINGDRAFT")+".USENET.RECOVERY-TEST";
        public static async Task<Fixture> Create(RentalDraftKind kind,IInterceptor? interceptor=null)
        {
            var connection=new SqliteConnection("Data Source=:memory:");await connection.OpenAsync();
            var options=new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection);
            if(interceptor is not null)options.AddInterceptors(interceptor);
            var db=new LocalDbContext(options.Options);await db.Database.EnsureCreatedAsync();
            var session=new SessionState();session.SetOfflineSession(new UserSessionDto {UserId=Guid.NewGuid(),Username="recovery-test",Role=DomainConstants.RoleAdmin,
                TenantCode=TenantScopeCatalog.UsenetGroup,OfficeCode=OfficeCodeCatalog.Usenet,ScopeType=TenantScopeCatalog.ScopeAdmin});
            session.SetBusinessDatabase("USENET");
            var local=new LocalStateService(db,new OfficeAccessService(),new SyncRequestDispatcher(),session);
            var f=new Fixture {Connection=connection,Db=db,Session=session,Rental=new RentalStateService(db,local),Kind=kind};
            db.Settings.Add(new LocalSetting {Key=f.LegacyKey,Value=Payload});await db.SaveChangesAsync();db.ChangeTracker.Clear();return f;
        }
        public Task<string?> ReadOriginal()=>Db.Settings.AsNoTracking().Where(s=>s.Key==LegacyKey).Select(s=>s.Value).FirstOrDefaultAsync();
        public async Task<string?> ReadCurrent()=>Kind==RentalDraftKind.Billing?(await Rental.GetBillingEditorDraftAsync(Session))?.CustomerName:(await Rental.GetOnboardingDraftAsync(Session))?.CustomerName;
        public async Task SaveCurrent(string name)
        {
            if(Kind==RentalDraftKind.Billing)await Rental.SaveBillingEditorDraftAsync(new RentalBillingEditorDraftModel {CustomerName=name},Session);
            else await Rental.SaveOnboardingDraftAsync(new RentalCustomerOnboardingDraftModel {CustomerName=name},Session);
        }
        public async ValueTask DisposeAsync(){await Db.DisposeAsync();await Connection.DisposeAsync();}
    }
}

