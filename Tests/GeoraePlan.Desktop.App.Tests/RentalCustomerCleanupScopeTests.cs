using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalCustomerCleanupScopeTests
{
    [Fact]
    public async Task NormalizeRentalCustomerLinks_DoesNotMutateReadOnlyWideScopeRows()
    {
        PrepareAppRoot("georaeplan-rental-customer-cleanup-scope");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var usenetCustomer = CreateCustomer(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                "USENET 정식 거래처",
                "111-11-11111");
            var yeonsuCustomer = CreateCustomer(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Yeonsu,
                "YEONSU 정식 거래처",
                "222-22-22222");
            var usenetProfile = CreateProfile(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                "USENET 이전 거래처",
                usenetCustomer.BusinessNumber);
            var usenetReadOnlyCustomerNumberProfile = CreateProfile(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                "USENET 외부 번호 거래처",
                yeonsuCustomer.BusinessNumber);
            var yeonsuProfile = CreateProfile(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Yeonsu,
                "YEONSU 이전 거래처",
                yeonsuCustomer.BusinessNumber);
            var yeonsuAsset = CreateAsset(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Yeonsu,
                "YEONSU 잘못된 거래처",
                "YEONSU 정식 거래처");
            yeonsuAsset.CustomerId = yeonsuCustomer.Id;

            db.Customers.AddRange(usenetCustomer, yeonsuCustomer);
            db.RentalBillingProfiles.AddRange(usenetProfile, usenetReadOnlyCustomerNumberProfile, yeonsuProfile);
            db.RentalAssets.Add(yeonsuAsset);
            await db.SaveChangesAsync();

            var session = CreateOfficeSession(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                AppPermissionNames.RentalSettingsEdit,
                AppPermissionNames.RentalViewAll);

            var result = await new RentalStateService(db).NormalizeRentalCustomerLinksAsync(session);

            var storedUsenetProfile = await db.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(profile => profile.Id == usenetProfile.Id);
            var storedUsenetReadOnlyCustomerNumberProfile = await db.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(profile => profile.Id == usenetReadOnlyCustomerNumberProfile.Id);
            var storedYeonsuProfile = await db.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(profile => profile.Id == yeonsuProfile.Id);
            var storedYeonsuAsset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync(asset => asset.Id == yeonsuAsset.Id);

            Assert.Equal("USENET 정식 거래처", storedUsenetProfile.CustomerName);
            Assert.True(storedUsenetProfile.IsDirty);
            Assert.Equal("USENET 외부 번호 거래처", storedUsenetReadOnlyCustomerNumberProfile.CustomerName);
            Assert.Null(storedUsenetReadOnlyCustomerNumberProfile.CustomerId);
            Assert.Equal(OfficeCodeCatalog.Usenet, storedUsenetReadOnlyCustomerNumberProfile.ResponsibleOfficeCode);
            Assert.False(storedUsenetReadOnlyCustomerNumberProfile.IsDirty);
            Assert.Equal("YEONSU 이전 거래처", storedYeonsuProfile.CustomerName);
            Assert.False(storedYeonsuProfile.IsDirty);
            Assert.Equal("YEONSU 잘못된 거래처", storedYeonsuAsset.CustomerName);
            Assert.False(storedYeonsuAsset.IsDirty);
            Assert.Equal(1, result.UpdatedProfileCount);
            Assert.Equal(0, result.UpdatedAssetCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private static LocalCustomer CreateCustomer(
        string tenantCode,
        string officeCode,
        string name,
        string businessNumber)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ResponsibleOfficeCode = officeCode,
            NameOriginal = name,
            NameMatchKey = name,
            BusinessNumber = businessNumber,
            IsDeleted = false,
            IsDirty = false
        };

    private static LocalRentalBillingProfile CreateProfile(
        string tenantCode,
        string officeCode,
        string customerName,
        string businessNumber)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ResponsibleOfficeCode = officeCode,
            ManagementCompanyCode = officeCode,
            ProfileKey = $"CLEANUP-{tenantCode}-{officeCode}-{Guid.NewGuid():N}",
            CustomerName = customerName,
            BusinessNumber = businessNumber,
            InstallSiteName = customerName,
            ItemName = "렌탈 장비",
            MonthlyAmount = 10_000m,
            BillingType = "묶음",
            BillingAdvanceMode = "후불",
            BillingMethod = "현금",
            BillingDay = 25,
            BillingDayMode = RentalBillingScheduleRules.BillingDayModeFixedDay,
            BillingCycleMonths = 1,
            BillingAnchorMonth = 1,
            BillingStatus = "청구중",
            CompletionStatus = PaymentFlowConstants.CompletionPending,
            SettlementStatus = PaymentFlowConstants.SettlementStatusUnpaid,
            IsActive = true,
            IsDeleted = false,
            IsDirty = false
        };

    private static LocalRentalAsset CreateAsset(
        string tenantCode,
        string officeCode,
        string customerName,
        string currentCustomerName)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ResponsibleOfficeCode = officeCode,
            ManagementCompanyCode = officeCode,
            AssetKey = $"CLEANUP-ASSET-{tenantCode}-{officeCode}-{Guid.NewGuid():N}",
            ManagementId = $"MID-{Guid.NewGuid():N}",
            ManagementNumber = $"MN-{Guid.NewGuid():N}",
            CustomerName = customerName,
            CurrentCustomerName = currentCustomerName,
            InstallLocation = "설치처",
            InstallSiteName = "설치처",
            ItemName = "렌탈 장비",
            AssetStatus = "임대진행중",
            BillingEligibilityStatus = "미확인",
            IsDeleted = false,
            IsDirty = false
        };

    private static SessionState CreateOfficeSession(string tenantCode, string officeCode, params string[] permissions)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            UserId = Guid.NewGuid(),
            Username = $"{officeCode.ToLowerInvariant()}-settings-user",
            Role = DomainConstants.RoleUser,
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = permissions.ToList()
        });
        return session;
    }

    private static void PrepareAppRoot(string prefix)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);
    }
}
