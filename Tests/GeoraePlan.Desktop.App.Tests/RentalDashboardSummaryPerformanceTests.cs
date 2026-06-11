using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalDashboardSummaryPerformanceTests
{
    [Fact]
    public async Task GetDashboardSummaryAsync_KeepsCountsWithProjectedRentalRows()
    {
        PrepareAppRoot("georaeplan-rental-dashboard-summary");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var referenceDate = new DateOnly(2026, 6, 11);
            var linkedProfileId = Guid.NewGuid();
            db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
            {
                Id = linkedProfileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ProfileKey = "dashboard-profile-linked",
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                CustomerName = "요약 고객",
                BusinessNumber = "111-11-11111",
                ItemName = "정수기",
                BillingType = "묶음",
                InstallSiteName = "본점",
                BillingDay = referenceDate.Day,
                BillingCycleMonths = 1,
                BillingAnchorMonth = referenceDate.Month,
                MonthlyAmount = 100_000m,
                BillingTemplateJson = "[]",
                IsActive = true
            });
            db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ProfileKey = "dashboard-profile-assetless",
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                CustomerId = Guid.NewGuid(),
                CustomerName = "자산 없는 고객",
                ItemName = "복합기",
                BillingType = "묶음",
                InstallSiteName = "지점",
                BillingDay = 25,
                BillingCycleMonths = 1,
                BillingAnchorMonth = referenceDate.Month,
                MonthlyAmount = 50_000m,
                BillingTemplateJson = "[]",
                IsActive = true
            });

            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                AssetKey = "dashboard-asset-linked",
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                CustomerId = Guid.NewGuid(),
                BillingProfileId = linkedProfileId,
                CustomerName = "요약 고객",
                CurrentCustomerName = "요약 고객",
                ItemName = "정수기",
                ManagementNumber = "A-001",
                InstallLocation = "본점",
                InstallSiteName = "본점",
                MonthlyFee = 100_000m,
                RentalEndDate = referenceDate.AddDays(9),
                BillingEligibilityStatus = "청구",
                AssetStatus = "렌탈중"
            });
            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                AssetKey = "dashboard-asset-unlinked",
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                CustomerName = "미연결 고객",
                CurrentCustomerName = string.Empty,
                ItemName = "공기청정기",
                ManagementNumber = "A-002",
                InstallLocation = "창고",
                InstallSiteName = "창고",
                MonthlyFee = 0m,
                AssetStatus = "렌탈중"
            });
            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                AssetKey = "dashboard-asset-disposed",
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                CustomerName = "폐기 고객",
                ItemName = "폐기품",
                ManagementNumber = "A-003",
                AssetStatus = "폐기"
            });
            db.Customers.Add(new LocalCustomer
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "요약 고객",
                NameMatchKey = RentalCatalogValueNormalizer.NormalizeLooseKey("요약 고객"),
                BusinessNumber = "111-11-11111"
            });
            db.Customers.Add(new LocalCustomer
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "미연결 고객",
                NameMatchKey = RentalCatalogValueNormalizer.NormalizeLooseKey("미연결 고객")
            });
            db.Customers.Add(new LocalCustomer
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "화면 대상 아님",
                NameMatchKey = RentalCatalogValueNormalizer.NormalizeLooseKey("화면 대상 아님")
            });
            await db.SaveChangesAsync();

            var summary = await new RentalStateService(db).GetDashboardSummaryAsync(
                CreateAdminSession(),
                referenceDate);

            Assert.Equal(1, summary.DueTodayCount);
            Assert.Equal(0, summary.UpcomingCount);
            Assert.Equal(0, summary.OverdueCount);
            Assert.Equal(2, summary.ActiveAssetCount);
            Assert.Equal(1, summary.ExpiringContractCount);
            Assert.Equal(1, summary.BillingCustomerUnlinkedCount);
            Assert.Equal(1, summary.AssetCustomerUnlinkedCount);
            Assert.Equal(1, summary.AssetBillingUnlinkedCount);
            Assert.Equal(1, summary.AssetlessBillingProfileCount);
            Assert.Equal(4, summary.UnassignedCount);
            Assert.Contains(summary.AlertItems, item => item.CustomerName == "요약 고객");
            Assert.Contains(summary.ExpiringAssets, item => item.ManagementNumber == "A-001");
            Assert.Contains(summary.UnresolvedLinkItems, item => item.QueueType == "프로필 고객 미연결" && item.CandidateCount == 1);
            Assert.Contains(summary.UnresolvedLinkItems, item => item.QueueType == "자산 고객 미연결" && item.CandidateCount == 1);
            Assert.Contains(summary.UnresolvedLinkItems, item => item.QueueType == "자산 청구 미연결");
            Assert.Contains(summary.UnresolvedLinkItems, item => item.QueueType == "자산 없는 청구프로필");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private static void PrepareAppRoot(string prefix)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);
    }

    private static SessionState CreateAdminSession()
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            Username = "admin",
            Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin
        });
        return session;
    }
}
