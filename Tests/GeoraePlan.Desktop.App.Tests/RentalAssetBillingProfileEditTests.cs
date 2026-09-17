using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalAssetBillingProfileEditTests
{
    [Theory]
    [InlineData(false, "", "청구대상")]
    [InlineData(true, "", "청구대상")]
    [InlineData(false, "미확인", "미확인")]
    [InlineData(true, "미확인", "미확인")]
    [InlineData(false, "청구대상", "청구대상")]
    [InlineData(true, "청구대상", "청구대상")]
    [InlineData(false, "청구제외", "청구제외")]
    [InlineData(true, "청구제외", "청구제외")]
    public async Task AssetEditor_PreservesBillingProfile_ForExplicitAndCapturedSaves(
        bool capturedSave, string sourceEligibility, string expectedEligibility)
    {
        var root=Path.Combine(Path.GetTempPath(),"rental-profile-editor-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using var db=new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite($"Data Source={Path.Combine(root,"editor.db")}").Options);
        await db.Database.EnsureCreatedAsync();
        var assetId=Guid.NewGuid();
        var customer=new LocalCustomer { NameOriginal="Editor Customer",NameMatchKey="EDITORCUSTOMER",TradeType="매출",
            TenantCode=TenantScopeCatalog.UsenetGroup,OfficeCode=OfficeCodeCatalog.Usenet,ResponsibleOfficeCode=OfficeCodeCatalog.Usenet };
        var profile=new LocalRentalBillingProfile { ProfileKey="EDITOR-PROFILE",CustomerId=customer.Id,CustomerName=customer.NameOriginal,
            TenantCode=TenantScopeCatalog.UsenetGroup,OfficeCode=OfficeCodeCatalog.Usenet,ResponsibleOfficeCode=OfficeCodeCatalog.Usenet,
            ManagementCompanyCode=OfficeCodeCatalog.Usenet,MonthlyAmount=33000,
            BillingTemplateJson=JsonSerializer.Serialize(new[]{new RentalBillingTemplateItemModel
            { DisplayItemName="Rental",Quantity=1,UnitPrice=33000,Amount=33000,RepresentativeAssetId=assetId,IncludedAssetIds=[assetId] }}) };
        db.Customers.Add(customer);db.RentalBillingProfiles.Add(profile);
        db.ItemCategoryOptions.Add(new LocalItemCategoryOption {Name="Printer",IsActive=true});
        db.RentalAssets.Add(new LocalRentalAsset {Id=assetId,AssetKey="EDITOR-ASSET",ManagementId="EDITOR-ASSET",ManagementNumber="EDITOR-ASSET",
            TenantCode=TenantScopeCatalog.UsenetGroup,OfficeCode=OfficeCodeCatalog.Usenet,ResponsibleOfficeCode=OfficeCodeCatalog.Usenet,
            ManagementCompanyCode=OfficeCodeCatalog.Usenet,CustomerId=customer.Id,CustomerName=customer.NameOriginal,CurrentCustomerName=customer.NameOriginal,
            BillingProfileId=profile.Id,BillingEligibilityStatus=sourceEligibility,
            AssetStatus="임대진행중",CurrentLocation="설치",ItemCategoryName="Printer",MonthlyFee=33000,Notes="Before edit"});
        await db.SaveChangesAsync();db.ChangeTracker.Clear();
        var session=new SessionState();
        session.SetOfflineSession(new UserSessionDto {UserId=Guid.NewGuid(),Username="editor-test",Role=DomainConstants.RoleAdmin,
            TenantCode=TenantScopeCatalog.UsenetGroup,OfficeCode=OfficeCodeCatalog.Usenet,ScopeType=TenantScopeCatalog.ScopeAdmin});
        var local=new LocalStateService(db,new OfficeAccessService(),new SyncRequestDispatcher(),session);
        var rental=new RentalStateService(db,local);
        var vm=new RentalAssetViewModel(rental,local,new RentalDocumentService(),null!,session);
        try
        {
            await vm.LoadAndSelectAssetAsync(assetId);
            Assert.Equal(expectedEligibility,vm.EditBillingEligibilityStatus);
            typeof(RentalAssetViewModel).GetField("_suppressEditAutoSave",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(vm,true);
            vm.EditNotes="Updated through editor";
            if(capturedSave)
            {
                var capture=typeof(RentalAssetViewModel).GetMethod("CaptureEditSnapshot",BindingFlags.Instance|BindingFlags.NonPublic)!;
                var build=typeof(RentalAssetViewModel).GetMethod("BuildAsset",BindingFlags.Instance|BindingFlags.NonPublic)!;
                var captured=capture.Invoke(vm,null)!;
                // Simulate a newer editor state while an older save snapshot is queued.
                typeof(RentalAssetViewModel).GetMethod("ResetForNewAsset",BindingFlags.Instance|BindingFlags.NonPublic)!
                    .Invoke(vm,["New draft"]);
                var candidate=Assert.IsType<LocalRentalAsset>(build.Invoke(vm,[captured]));
                Assert.Equal(assetId,candidate.Id);Assert.Equal(profile.Id,candidate.BillingProfileId);
                var newCandidate=Assert.IsType<LocalRentalAsset>(build.Invoke(vm,[capture.Invoke(vm,null)!]));
                Assert.Null(newCandidate.BillingProfileId);
                var result=await rental.SaveAssetAsync(candidate,session);
                Assert.True(result.Success,result.Message);
            }
            else
            {
                await vm.SaveCommand.ExecuteAsync(null);
                Assert.Contains("저장했습니다",vm.StatusMessage);
            }
            db.ChangeTracker.Clear();
            var saved=await db.RentalAssets.AsNoTracking().SingleAsync(x=>x.Id==assetId);
            Assert.Equal(profile.Id,saved.BillingProfileId);Assert.Equal(customer.Id,saved.CustomerId);
            Assert.Equal("Updated through editor",saved.Notes);Assert.Equal(33000,saved.MonthlyFee);
            Assert.Equal(expectedEligibility,saved.BillingEligibilityStatus);
            var savedProfile=await db.RentalBillingProfiles.AsNoTracking().SingleAsync();
            Assert.Equal(customer.Id,savedProfile.CustomerId);Assert.Equal(33000,savedProfile.MonthlyAmount);
        }
        finally { vm.CancelPendingBackgroundWork(); }
    }
}
