using System.Reflection;
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

public sealed class RentalBillingSaveStateTests
{
    [Theory]
    [InlineData(false,false)]
    [InlineData(true,false)]
    [InlineData(false,true)]
    [InlineData(true,true)]
    public async Task SaveCommand_AcknowledgesPersistedEditsAndPreservesLaterInput(bool editDuringSave,bool editDuringLoad)
    {
        await using var connection=new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var afterSave=new AfterProfileSave();
        var duringLoad=new DuringAssetRead();
        await using var db=new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection).AddInterceptors(afterSave,duringLoad).Options);
        await db.Database.EnsureCreatedAsync();
        var session=new SessionState();
        session.SetOfflineSession(new UserSessionDto {UserId=Guid.NewGuid(),Username="save-state-test",Role=DomainConstants.RoleAdmin,
            TenantCode=TenantScopeCatalog.UsenetGroup,OfficeCode=OfficeCodeCatalog.Usenet,ScopeType=TenantScopeCatalog.ScopeAdmin});
        session.SetBusinessDatabase("georaeplan_usenet");
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
            if(editDuringLoad)duringLoad.OnRead=()=>
            {
                if(vm.SelectedRow?.Source.Id!=profileId)return false;
                vm.EditNotes="input while loading";return true;
            };
            await vm.LoadAndSelectProfileAsync(profileId);
            Assert.Equal(editDuringLoad,HasEdits(vm));
            if(editDuringLoad)Assert.Equal("input while loading",vm.EditNotes);
            vm.EditNotes="persisted edit";
            Assert.True(HasEdits(vm));
            if(editDuringSave)afterSave.OnSaved=()=>vm.EditNotes="newer input";
            Assert.True(vm.SaveCommand.CanExecute(null));
            await vm.SaveCommand.ExecuteAsync(null);
            // Wait for the normal detail load without cancelling the operation under test.
            var coordinator=typeof(RentalBillingViewModel).GetField("_selectionPipelineCoordinator",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(vm)!;
            if(coordinator.GetType().GetField("_currentTask",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(coordinator) is Task selectionTask)
                await selectionTask.WaitAsync(TimeSpan.FromSeconds(15));
            LocalRentalBillingProfile saved;
            int invoices;
            await local.OwnerScopeDataGate.WaitAsync();
            try
            {
                saved=await db.RentalBillingProfiles.AsNoTracking().SingleAsync(x=>x.Id==profileId);
                invoices=await db.Invoices.CountAsync();
            }
            finally {local.OwnerScopeDataGate.Release();}
            Assert.Equal("persisted edit",saved.Notes);
            Assert.Equal(profileId,vm.SelectedRow?.Source.Id);
            Assert.Equal(editDuringSave?"newer input":"persisted edit",vm.EditNotes);
            Assert.Equal(editDuringSave,HasEdits(vm));
            if(editDuringSave)
            {
                Assert.True(afterSave.Fired);
                vm.EditNotes="persisted edit";
                Assert.False(HasEdits(vm),DescribeDifference(vm));
            }
            else Assert.DoesNotContain("저장하지 않은",vm.StatusMessage);
            Assert.Equal(0,invoices);
            Assert.Equal(session.TenantCode,saved.TenantCode);
            Assert.Equal(session.OfficeCode,saved.OfficeCode);
        }
        finally { await vm.CancelAndDrainPendingBackgroundWorkAsync(); }
    }

    private static bool HasEdits(RentalBillingViewModel vm)=>(bool)typeof(RentalBillingViewModel)
        .GetMethod("HasUnsavedSelectedRowChanges",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(vm,null)!;

    private static string DescribeDifference(RentalBillingViewModel vm)
    {
        var baseline=((string)typeof(RentalBillingViewModel).GetField("_selectedRowBaselineSignature",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(vm)!).Split("||");
        var current=((string)typeof(RentalBillingViewModel).GetMethod("BuildCurrentEditorSignature",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(vm,null)!).Split("||");
        return string.Join("\n",baseline.Zip(current).Select((p,i)=>(p,i)).Where(x=>x.p.First!=x.p.Second).Select(x=>$"{x.i}: baseline={x.p.First}; current={x.p.Second}"));
    }

    private sealed class AfterProfileSave:SaveChangesInterceptor
    {
        public Action? OnSaved {get;set;}
        public bool Fired {get;private set;}
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData,int result,CancellationToken cancellationToken=default)
        {
            if(OnSaved is not null && eventData.Context!.ChangeTracker.Entries<LocalRentalBillingProfile>().Any(e=>e.Entity.Notes=="persisted edit"))
            {var callback=OnSaved;OnSaved=null;Fired=true;callback();}
            return ValueTask.FromResult(result);
        }
    }

    private sealed class DuringAssetRead:DbCommandInterceptor
    {
        public Func<bool>? OnRead {get;set;}
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData eventData,InterceptionResult<DbDataReader> result,CancellationToken cancellationToken=default)
        {
            if(command.CommandText.Contains("FROM \"RentalAssets\"") && OnRead?.Invoke()==true)OnRead=null;
            return ValueTask.FromResult(result);
        }
    }
}
