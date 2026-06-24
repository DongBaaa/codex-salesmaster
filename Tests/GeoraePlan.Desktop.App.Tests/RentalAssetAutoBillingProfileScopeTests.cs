using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalAssetAutoBillingProfileScopeTests
{
    [Fact]
    public async Task SaveAsset_AutoLinksBillingProfileWhenTenantAndOfficeMatch()
    {
        PrepareAppRoot("georaeplan-rental-asset-auto-profile-positive");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customer = new LocalCustomer
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "USENET 자동연결 거래처",
                NameMatchKey = "USENET 자동연결 거래처",
                BusinessNumber = "333-33-33333",
                IsDeleted = false
            };
            var profile = CreateBillingProfile(
                customer.Id,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                OfficeCodeCatalog.Usenet);

            db.Customers.Add(customer);
            db.RentalBillingProfiles.Add(profile);
            await db.SaveChangesAsync();

            var asset = CreateAsset(customer);
            var session = CreateOfficeSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);

            var result = await new RentalStateService(db).SaveAssetAsync(
                asset,
                session,
                allowCategoryRecovery: true);

            Assert.True(result.Success, result.Message);
            var storedAsset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync(current => current.Id == asset.Id);
            Assert.Equal(profile.Id, storedAsset.BillingProfileId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SaveAsset_DoesNotAutoLinkBillingProfileWhoseTenantConflictsWithAsset()
    {
        PrepareAppRoot("georaeplan-rental-asset-auto-profile-scope");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customer = new LocalCustomer
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "USENET 자동연결 거래처",
                NameMatchKey = "USENET 자동연결 거래처",
                BusinessNumber = "333-33-33333",
                IsDeleted = false
            };
            var conflictingProfile = CreateBillingProfile(
                customer.Id,
                TenantScopeCatalog.Itworld,
                OfficeCodeCatalog.Itworld,
                OfficeCodeCatalog.Usenet);

            db.Customers.Add(customer);
            db.RentalBillingProfiles.Add(conflictingProfile);
            await db.SaveChangesAsync();

            var asset = CreateAsset(customer);
            var session = CreateOfficeSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);

            var result = await new RentalStateService(db).SaveAssetAsync(
                asset,
                session,
                allowCategoryRecovery: true);

            Assert.True(result.Success, result.Message);
            var storedAsset = await db.RentalAssets.IgnoreQueryFilters().SingleAsync(current => current.Id == asset.Id);
            Assert.Null(storedAsset.BillingProfileId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private static LocalRentalBillingProfile CreateBillingProfile(
        Guid customerId,
        string tenantCode,
        string ownerOfficeCode,
        string responsibleOfficeCode)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantCode = tenantCode,
            OfficeCode = ownerOfficeCode,
            ResponsibleOfficeCode = responsibleOfficeCode,
            ManagementCompanyCode = ownerOfficeCode,
            CustomerId = customerId,
            ProfileKey = $"AUTO-LINK-PROFILE-{Guid.NewGuid():N}",
            CustomerName = "USENET 자동연결 거래처",
            BusinessNumber = "333-33-33333",
            InstallSiteName = "본관 1층",
            ItemName = "자동연결 테스트 장비",
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

    private static LocalRentalAsset CreateAsset(LocalCustomer customer)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            CustomerId = customer.Id,
            CustomerName = customer.NameOriginal,
            CurrentCustomerName = customer.NameOriginal,
            InstallLocation = "본관 1층",
            InstallSiteName = "본관 1층",
            ItemCategoryName = "복합기",
            ItemName = "자동연결 테스트 장비",
            ManagementNumber = $"AUTO-LINK-{Guid.NewGuid():N}",
            ManagementId = $"AUTO-LINK-ID-{Guid.NewGuid():N}",
            MachineNumber = $"AUTO-LINK-SN-{Guid.NewGuid():N}",
            AssetStatus = "임대진행중",
            BillingEligibilityStatus = "대상"
        };

    private static SessionState CreateOfficeSession(string tenantCode, string officeCode)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            UserId = Guid.NewGuid(),
            Username = $"{officeCode.ToLowerInvariant()}-asset-user",
            Role = DomainConstants.RoleUser,
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = [AppPermissionNames.RentalAssetEdit]
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
