using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalBillingDraftFieldsTests
{
    [Fact]
    public async Task LegacyDraftWithoutPooledAllowance_RestoresDefault()
    {
        var vm=Create();
        try
        {
            vm.EditPoolMeterAllowance=true;
            var draft=JsonSerializer.Deserialize<RentalBillingEditorDraftModel>("{\"CustomerName\":\"legacy draft\",\"LinkAssetsLater\":true}")!;
            Invoke(vm,"ApplyBillingEditorDraft",draft,true);
            Assert.False(vm.EditPoolMeterAllowance);
        }
        finally {await vm.CancelAndDrainPendingBackgroundWorkAsync();}
    }

    [Theory]
    [InlineData(true,"개별")]
    [InlineData(false,"개별")]
    [InlineData(true,"묶음")]
    [InlineData(false,"묶음")]
    public async Task DebouncedDraft_PersistsInSqliteAndRestoresIntoFreshEditor(bool desired,string billingType)
    {
        await using var connection=new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db=new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var session=new SessionState();
        session.SetOfflineSession(new UserSessionDto {UserId=Guid.NewGuid(),Username="draft-db-test",Role=DomainConstants.RoleAdmin,
            TenantCode=TenantScopeCatalog.UsenetGroup,OfficeCode=OfficeCodeCatalog.Usenet,ScopeType=TenantScopeCatalog.ScopeAdmin});
        session.SetBusinessDatabase("georaeplan_usenet");
        var local=new LocalStateService(db,new OfficeAccessService(),new SyncRequestDispatcher(),session);
        var rental=new RentalStateService(db,local);
        var customerId=Guid.NewGuid();var itemId=Guid.NewGuid();var assetId=Guid.NewGuid();
        db.Customers.Add(new LocalCustomer {Id=customerId,NameOriginal="임시본 고객",NameMatchKey="DRAFT-CUSTOMER",TenantCode=session.TenantCode,OfficeCode=session.OfficeCode,ResponsibleOfficeCode=session.OfficeCode});
        db.Items.Add(new LocalItem {Id=itemId,NameOriginal="임시본 모델",NameMatchKey="DRAFT-MODEL",TenantCode=session.TenantCode,OfficeCode=session.OfficeCode,IsRental=true,Unit="대"});
        db.RentalAssets.Add(new LocalRentalAsset {Id=assetId,AssetKey="DRAFT-ASSET",ManagementNumber="DRAFT-001",ItemId=itemId,CustomerId=customerId,
            TenantCode=session.TenantCode,OfficeCode=session.OfficeCode,ResponsibleOfficeCode=session.OfficeCode,ManagementCompanyCode=session.OfficeCode,
            ItemName="임시본 모델",CustomerName="임시본 고객",CurrentCustomerName="임시본 고객",MonthlyFee=240000,AssetStatus="임대진행중",BillingEligibilityStatus="청구대상"});
        await db.SaveChangesAsync();db.ChangeTracker.Clear();
        var source=new RentalBillingViewModel(rental,local,session);
        RentalBillingViewModel? destination=null;
        try
        {
            source.EditCustomerId=customerId;source.EditCustomerName="임시본 고객";source.EditBillingType=billingType;
            source.EditPoolMeterAllowance=!desired;
            var item=new RentalBillingTemplateEditorItem {ItemId=Guid.NewGuid(),CatalogItemId=itemId,DisplayItemName="사용자 표시 품목",BillingLineMode=billingType,
                IndividualGroupingMode=RentalBillingTemplateItemModel.IndividualGroupingCustom,RepresentativeAssetId=assetId,Quantity=1,UnitPrice=240000,Amount=240000,Note="복원 메모"};
            item.IncludedAssetIds.Add(assetId);
            source.TemplateItems.Clear();source.TemplateItems.Add(item);source.SelectedTemplateItem=item;
            Assert.True(await source.FlushAutoSaveAsync());
            // The only subsequent input is the pooled allowance flag. Wait for actual debounced persistence.
            source.EditPoolMeterAllowance=desired;
            RentalBillingEditorDraftModel? stored=null;
            var deadline=DateTime.UtcNow.AddSeconds(8);
            do
            {
                await Task.Delay(100);
                await local.OwnerScopeDataGate.WaitAsync();
                try {stored=await rental.GetBillingEditorDraftAsync(session);}
                finally {local.OwnerScopeDataGate.Release();}
            }while(stored?.PoolMeterAllowance!=desired && DateTime.UtcNow<deadline);
            Assert.NotNull(stored);Assert.Equal(desired,stored.PoolMeterAllowance);
            await source.CancelAndDrainPendingBackgroundWorkAsync();
            destination=new RentalBillingViewModel(rental,local,session);
            Assert.True(await destination.RestoreAutoSaveDraftAsync());
            Assert.Equal(desired,destination.EditPoolMeterAllowance);
            var restored=Assert.Single(destination.TemplateItems);
            Assert.Equal(item.ItemId,restored.ItemId);
            Assert.Equal(itemId,restored.CatalogItemId);
            Assert.Equal(RentalBillingTemplateItemModel.IndividualGroupingCustom,restored.IndividualGroupingMode);
            Assert.Equal(assetId,Assert.Single(restored.IncludedAssetIds));
            Assert.Equal(billingType=="묶음"?(Guid?)assetId:null,restored.RepresentativeAssetId);
            Assert.Equal("복원 메모",restored.Note);
            await local.OwnerScopeDataGate.WaitAsync();
            try
            {
                Assert.Equal(0,await db.RentalBillingProfiles.CountAsync());
                Assert.Equal(0,await db.Invoices.CountAsync());
                Assert.Equal(240000,(await db.RentalAssets.AsNoTracking().SingleAsync()).MonthlyFee);
            }
            finally {local.OwnerScopeDataGate.Release();}
        }
        finally
        {
            await source.CancelAndDrainPendingBackgroundWorkAsync();
            if(destination is not null)await destination.CancelAndDrainPendingBackgroundWorkAsync();
        }
    }

    [Theory]
    [InlineData(true,false)]
    [InlineData(false,true)]
    public async Task JsonDraftRoundTrip_PreservesPooledAllowanceInsteadOfDestinationState(bool desired,bool stale)
    {
        var source=Create();var destination=Create();
        try
        {
            source.EditPoolMeterAllowance=desired;destination.EditPoolMeterAllowance=stale;
            var draft=RoundTrip(source);
            Invoke(destination,"ApplyBillingEditorDraft",draft,true);
            Assert.Equal(desired,destination.EditPoolMeterAllowance);
        }
        finally {await source.CancelAndDrainPendingBackgroundWorkAsync();await destination.CancelAndDrainPendingBackgroundWorkAsync();}
    }

    [Fact]
    public async Task JsonDraftRoundTrip_PreservesAllDisplayTemplateFieldsAndAssetLinks()
    {
        var source=Create();var destination=Create();
        try
        {
            source.EditBillingType="개별";
            var asset=Guid.NewGuid();
            var item=new RentalBillingTemplateEditorItem {ItemId=Guid.NewGuid(),CatalogItemId=Guid.NewGuid(),DisplayItemName="복원 검증",BillingLineMode="개별",
                IndividualGroupingMode=RentalBillingTemplateItemModel.IndividualGroupingCustom,Specification="규격",Unit="대",MaterialNumber="DRAFT-001",
                RepresentativeAssetId=asset,Quantity=1,UnitPrice=240000,Amount=240000,Note="표시 메모"};
            item.IncludedAssetIds.Add(asset);
            source.TemplateItems.Clear();source.TemplateItems.Add(item);source.SelectedTemplateItem=item;
            var draft=RoundTrip(source);
            Invoke(destination,"ApplyBillingEditorDraft",draft,true);
            var restored=Invoke<RentalBillingEditorDraftModel>(destination,"BuildBillingEditorDraft");
            Assert.Equal(JsonSerializer.Serialize(draft.TemplateItems),JsonSerializer.Serialize(restored.TemplateItems));
            Assert.Equal(draft.SelectedTemplateItemId,restored.SelectedTemplateItemId);
        }
        finally {await source.CancelAndDrainPendingBackgroundWorkAsync();await destination.CancelAndDrainPendingBackgroundWorkAsync();}
    }

    [Fact]
    public async Task PooledAllowanceChange_SchedulesAutoSaveOnItsOwn()
    {
        var vm=Create();
        try
        {
            // First settle setup work; the new change must independently schedule its own draft.
            var field=typeof(RentalBillingViewModel).GetField("_autoSaveCts",BindingFlags.NonPublic|BindingFlags.Instance)!;
            if(field.GetValue(vm) is CancellationTokenSource previous) {previous.Cancel();previous.Dispose();}
            field.SetValue(vm,null);
            vm.EditPoolMeterAllowance=true;
            Assert.NotNull(typeof(RentalBillingViewModel).GetField("_autoSaveCts",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(vm));
        }
        finally {await vm.CancelAndDrainPendingBackgroundWorkAsync();}
    }

    private static RentalBillingViewModel Create()
    {
        var session=new SessionState();session.SetOfflineSession(new UserSessionDto {Username="draft-check",Role=DomainConstants.RoleAdmin,TenantCode=TenantScopeCatalog.UsenetGroup,OfficeCode=OfficeCodeCatalog.Usenet,ScopeType=TenantScopeCatalog.ScopeAdmin});
        return new RentalBillingViewModel(null!,null!,session){EditCustomerName="임시본 검증",LinkAssetsLater=true};
    }
    private static RentalBillingEditorDraftModel RoundTrip(RentalBillingViewModel vm)
        =>JsonSerializer.Deserialize<RentalBillingEditorDraftModel>(JsonSerializer.Serialize(Invoke<RentalBillingEditorDraftModel>(vm,"BuildBillingEditorDraft")))!;
    private static object? Invoke(RentalBillingViewModel vm,string method,params object?[] args)
        =>typeof(RentalBillingViewModel).GetMethod(method,BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(vm,args);
    private static T Invoke<T>(RentalBillingViewModel vm,string method,params object?[] args)=>(T)Invoke(vm,method,args)!;
}
