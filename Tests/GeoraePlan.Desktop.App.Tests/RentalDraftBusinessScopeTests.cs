using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalDraftBusinessScopeTests
{
    [Theory]
    [InlineData(false,"read")]
    [InlineData(true,"read")]
    [InlineData(false,"clear")]
    [InlineData(true,"clear")]
    [InlineData(false,"overwrite")]
    [InlineData(true,"overwrite")]
    [InlineData(false,"load")]
    [InlineData(false,"legacy")]
    [InlineData(true,"legacy")]
    public async Task DifferentBusinessDatabase_DoesNotReadOrChangeOtherDraft(bool onboarding,string operation)
    {
        await using var connection=new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db=new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var session=new SessionState();
        session.SetOfflineSession(new UserSessionDto {UserId=Guid.NewGuid(),Username="scope-test",Role=DomainConstants.RoleAdmin,
            TenantCode=TenantScopeCatalog.UsenetGroup,OfficeCode=OfficeCodeCatalog.Usenet,ScopeType=TenantScopeCatalog.ScopeAdmin});
        var local=new LocalStateService(db,new OfficeAccessService(),new SyncRequestDispatcher(),session);
        var rental=new RentalStateService(db,local);
        var legacyKey=onboarding?"RENTAL.ONBOARDINGDRAFT.USENET.SCOPE-TEST":"RENTAL.BILLINGEDITORDRAFT.USENET.SCOPE-TEST";
        const string legacyPayload="{\"CustomerName\":\"LEGACY-UNASSIGNED\"}";
        if(operation=="legacy")
        {
            db.Settings.Add(new LocalSetting {Key=legacyKey,Value=legacyPayload});
            await db.SaveChangesAsync();
        }
        async Task Save(string marker)
        {
            if(onboarding)await rental.SaveOnboardingDraftAsync(new RentalCustomerOnboardingDraftModel {CustomerName=marker},session);
            else await rental.SaveBillingEditorDraftAsync(new RentalBillingEditorDraftModel {CustomerName=marker},session);
        }
        async Task<string?> Read()=>onboarding?(await rental.GetOnboardingDraftAsync(session))?.CustomerName:(await rental.GetBillingEditorDraftAsync(session))?.CustomerName;
        async Task Clear()
        {
            if(onboarding)await rental.ClearOnboardingDraftAsync(session);
            else await rental.ClearBillingEditorDraftAsync(session);
        }
        session.SetBusinessDatabase("USENET");
        await Save("USENET-PENDING");
        Assert.Equal("USENET-PENDING",await Read());
        session.SetBusinessDatabase("ITWORLD");
        Assert.Equal("ITWORLD",session.SelectedBusinessDatabaseName,ignoreCase:true);
        // The real business transition replaces the business cache while keeping local drafts.
        await local.ResetBusinessDataCacheAsync(session);
        if(operation=="legacy")
        {
            await Save("ITWORLD-PENDING");await Clear();
            session.SetBusinessDatabase("USENET");await Clear();
            Assert.Equal(legacyPayload,(await db.Settings.AsNoTracking().SingleAsync(s=>s.Key==legacyKey)).Value);
        }
        else if(operation=="load")
        {
            var vm=new RentalBillingViewModel(rental,local,session);
            try {await vm.LoadAsync();Assert.NotEqual("USENET-PENDING",vm.EditCustomerName);}
            finally {await vm.CancelAndDrainPendingBackgroundWorkAsync();}
        }
        else if(operation=="read")Assert.Null(await Read());
        else
        {
            if(operation=="clear")await Clear();else await Save("ITWORLD-PENDING");
            session.SetBusinessDatabase("USENET");
            Assert.Equal("USENET-PENDING",await Read());
        }
    }
}

