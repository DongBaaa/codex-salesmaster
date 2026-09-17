using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalBillingEditorNavigationTests
{
    [Theory]
    [InlineData("selection","USENET")]
    [InlineData("decline","USENET")]
    [InlineData("accept","USENET")]
    [InlineData("changed-input","USENET")]
    [InlineData("changed-owner","USENET")]
    [InlineData("new-decline","USENET")]
    [InlineData("new-accept","USENET")]
    [InlineData("restored-selection","USENET")]
    [InlineData("restored-accept","USENET")]
    [InlineData("selection","YEONSU")]
    [InlineData("decline","YEONSU")]
    [InlineData("accept","YEONSU")]
    [InlineData("changed-input","YEONSU")]
    [InlineData("changed-owner","YEONSU")]
    [InlineData("new-decline","YEONSU")]
    [InlineData("new-accept","YEONSU")]
    [InlineData("restored-selection","YEONSU")]
    [InlineData("restored-accept","YEONSU")]
    [InlineData("selection","ITWORLD")]
    [InlineData("decline","ITWORLD")]
    [InlineData("accept","ITWORLD")]
    [InlineData("changed-input","ITWORLD")]
    [InlineData("changed-owner","ITWORLD")]
    [InlineData("new-decline","ITWORLD")]
    [InlineData("new-accept","ITWORLD")]
    [InlineData("restored-selection","ITWORLD")]
    [InlineData("restored-accept","ITWORLD")]
    public async Task Navigation_PreservesDraftUnlessExplicitlyConfirmed(string action,string office)
    {
        await using var connection=new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db=new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var session=new SessionState();
        session.SetOfflineSession(new UserSessionDto {UserId=Guid.NewGuid(),Username="save-state-test",Role=DomainConstants.RoleAdmin,
            TenantCode=TenantScopeCatalog.GetTenantCodeForOffice(office),OfficeCode=office,ScopeType=TenantScopeCatalog.ScopeAdmin});
        var database=office=="ITWORLD"?"georaeplan_itworld":"georaeplan_usenet";
        session.SetBusinessDatabase(database);
        var local=new LocalStateService(db,new OfficeAccessService(),new SyncRequestDispatcher(),session);
        var rental=new RentalStateService(db,local);
        var customerId=Guid.NewGuid();var profileId=Guid.NewGuid();var itemId=Guid.NewGuid();var assetId=Guid.NewGuid();
        db.Customers.Add(new LocalCustomer {Id=customerId,NameOriginal="저장 상태 검증",NameMatchKey="SAVE-STATE",TenantCode=session.TenantCode,OfficeCode=session.OfficeCode,ResponsibleOfficeCode=session.OfficeCode});
        db.Items.Add(new LocalItem {Id=itemId,NameOriginal="검증 장비",NameMatchKey="SAVE-ITEM",TenantCode=session.TenantCode,OfficeCode=session.OfficeCode,IsRental=true,Unit="대"});
        var template=new RentalBillingTemplateItemModel {ItemId=itemId,DisplayItemName="검증 장비",BillingLineMode="개별",IndividualGroupingMode=RentalBillingTemplateItemModel.IndividualGroupingByModel,Quantity=1,UnitPrice=240000,Amount=240000,IncludedAssetIds=[assetId]};
        db.RentalBillingProfiles.Add(new LocalRentalBillingProfile {Id=profileId,ProfileKey="SAVE-STATE",CustomerId=customerId,CustomerName="저장 상태 검증",ItemName="검증 장비",
            TenantCode=session.TenantCode,OfficeCode=session.OfficeCode,ResponsibleOfficeCode=session.OfficeCode,ManagementCompanyCode=session.OfficeCode,
            BillingType="개별",BillingDay=25,BillingCycleMonths=1,BillingAnchorMonth=9,ContractDate=new DateOnly(2026,9,1),ContractStartDate=new DateOnly(2026,9,1),MonthlyAmount=240000,
            BillingTemplateJson=rental.SerializeBillingTemplateItems([template]),IsActive=true});
        db.RentalAssets.Add(new LocalRentalAsset {Id=assetId,AssetKey="SAVE-ASSET",ManagementNumber="SAVE-001",ItemId=itemId,CustomerId=customerId,BillingProfileId=profileId,
            TenantCode=session.TenantCode,OfficeCode=session.OfficeCode,ResponsibleOfficeCode=session.OfficeCode,ManagementCompanyCode=session.OfficeCode,
            ItemName="검증 장비",CustomerName="저장 상태 검증",CurrentCustomerName="저장 상태 검증",MonthlyFee=240000,AssetStatus="임대진행중",BillingEligibilityStatus="청구대상"});
        await db.SaveChangesAsync();db.ChangeTracker.Clear();
        var vm=new RentalBillingViewModel(rental,local,session);
        try
        {
            await vm.LoadAndSelectProfileAsync(profileId);
            vm.EditNotes="LOCAL-DRAFT";
            await vm.FlushAutoSaveAsync();
            await db.RentalBillingProfiles.Where(x=>x.Id==profileId)
                .ExecuteUpdateAsync(s=>s.SetProperty(x=>x.Revision,11).SetProperty(x=>x.Notes,"PEER-WINNER"));
            if(action.StartsWith("restored-"))
            {
                await vm.CancelAndDrainPendingBackgroundWorkAsync();
                vm=new RentalBillingViewModel(rental,local,session);
                await vm.LoadAsync();
            }
            else await vm.ReloadCommand.ExecuteAsync(null);
            var target=vm.Rows.Single(x=>x.Source.Id==profileId);
            // Distinct row reference represents a newly clicked row, not the
            // already selected item on a normal refresh.
            if(action=="selection")
            {
                target=(await rental.GetBillingRowAsync(profileId,session,vm.ReferenceDate))!;
                vm.Rows.Add(target);
            }
            var confirmed=0;
            vm.ConfirmEditorDiscard=_=>
            {
                confirmed++;
                if(action=="changed-input")vm.EditNotes="NEWER-INPUT";
                if(action=="changed-owner")session.SetBusinessDatabase(database=="georaeplan_itworld"?"georaeplan_usenet":"georaeplan_itworld");
                return action is not "decline" and not "new-decline";
            };
            if(action.EndsWith("selection"))
                Assert.False(vm.TrySelectEditorRow(target));
            else if(action.StartsWith("new-"))await vm.NewProfileCommand.ExecuteAsync(null);
            else await vm.CancelEditingCommand.ExecuteAsync(null);
            if(action=="changed-owner")session.SetBusinessDatabase(database);

            var accepted=action.EndsWith("accept");
            if(accepted)
            {
                Assert.Equal(1,confirmed);
                Assert.Null(vm.SelectedRow);
                Assert.True(string.IsNullOrEmpty(vm.EditNotes));
                Assert.Null(await rental.GetBillingEditorDraftAsync(session));
                if(!action.StartsWith("new-"))
                {
                    var latest=vm.Rows.Single(x=>x.Source.Id==profileId);
                    Assert.Equal(11,latest.Source.Revision);
                    Assert.True(vm.TrySelectEditorRow(latest));
                    await DrainSelection(vm);
                    Assert.Equal("PEER-WINNER",vm.EditNotes);
                    vm.EditNotes="RESOLVED";
                    await vm.SaveCommand.ExecuteAsync(null);
                    Assert.Equal("RESOLVED",(await db.RentalBillingProfiles.AsNoTracking().SingleAsync()).Notes);
                    Assert.Null(await rental.GetBillingEditorDraftAsync(session));
                }
            }
            else
            {
                Assert.Equal(action=="changed-input"?"NEWER-INPUT":"LOCAL-DRAFT",vm.EditNotes);
                await vm.FlushAutoSaveForCloseAsync();
                var draft=await rental.GetBillingEditorDraftAsync(session);
                Assert.NotNull(draft);
                Assert.Equal(vm.EditNotes,draft.Notes);
                Assert.Equal(0,draft.Revision);
                Assert.Equal("PEER-WINNER",(await db.RentalBillingProfiles.AsNoTracking().SingleAsync()).Notes);
            }
            Assert.Equal(0,await db.Invoices.CountAsync());
            Assert.Equal(240000m,(await db.RentalAssets.AsNoTracking().SingleAsync()).MonthlyFee);
        }
        finally {await vm.CancelAndDrainPendingBackgroundWorkAsync();}
    }

    private static async Task DrainSelection(RentalBillingViewModel vm)
    {
        var coordinator=typeof(RentalBillingViewModel).GetField("_selectionPipelineCoordinator",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(vm)!;
        if(coordinator.GetType().GetField("_currentTask",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(coordinator) is Task task)
            await task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
