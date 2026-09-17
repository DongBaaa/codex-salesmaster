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
    public async Task DashboardBillingTotals_AreNotLimitedByThirtyDisplayedAlerts()
    {
        PrepareAppRoot("georaeplan-dashboard-alert-totals");
        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var referenceDate = new DateOnly(2026, 9, 15);
            foreach (var (day, count) in new[] { (10, 35), (15, 5), (20, 4), (30, 2) })
                for (var i = 0; i < count; i++)
                    db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
                    {
                        Id = Guid.NewGuid(), TenantCode = TenantScopeCatalog.UsenetGroup,
                        OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                        ManagementCompanyCode = OfficeCodeCatalog.Usenet, ProfileKey = $"totals-{day}-{i}",
                        CustomerName = $"청구 집계 {day}-{i}", ItemName = "시험 품목", BillingType = "묶음",
                        BillingDay = day, BillingCycleMonths = 1, BillingAnchorMonth = 9,
                        MonthlyAmount = 1000m, BillingTemplateJson = "[]", IsActive = true
                    });
            await db.SaveChangesAsync();

            var summary = await new RentalStateService(db).GetDashboardSummaryAsync(CreateAdminSession(), referenceDate);

            Assert.Equal(35, summary.OverdueCount);
            Assert.Equal(5, summary.DueTodayCount);
            Assert.Equal(4, summary.UpcomingCount);
            Assert.Equal(30, summary.AlertItems.Count);
            Assert.All(summary.AlertItems, item => Assert.True(item.DaysRemaining < 0));
            Assert.Contains("지연 청구 35건", summary.AlertPopupMessage);
            Assert.Contains("오늘 청구 5건", summary.AlertPopupMessage);
            Assert.Contains("예정 청구 4건", summary.AlertPopupMessage);
            Assert.All(db.ChangeTracker.Entries(), entry => Assert.Equal(EntityState.Unchanged, entry.State));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task DashboardExpiryTotals_IncludeAllEligibleAssetsBeforeTwentyRowLimit()
    {
        PrepareAppRoot("georaeplan-dashboard-expiry-totals");
        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var referenceDate = new DateOnly(2026, 9, 15);
            for (var i = 0; i < 29; i++)
                db.RentalAssets.Add(new LocalRentalAsset
                {
                    Id = Guid.NewGuid(), TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet, AssetKey = $"expiry-{i}",
                    ManagementNumber = $"EXP-{i:D2}", CustomerName = "만료 집계", ItemName = "시험 품목",
                    AssetStatus = i == 28 ? "폐기" : "렌탈중",
                    RentalEndDate = referenceDate.AddDays(i < 25 ? -1 : i == 25 ? 0 : i == 26 ? 30 : 31)
                });
            await db.SaveChangesAsync();

            var summary = await new RentalStateService(db).GetDashboardSummaryAsync(CreateAdminSession(), referenceDate);

            Assert.Equal(27, summary.ExpiringContractCount);
            Assert.Equal(28, summary.ActiveAssetCount);
            Assert.Equal(20, summary.ExpiringAssets.Count);
            Assert.All(summary.ExpiringAssets, item => Assert.Equal(-1, item.DaysRemaining));
            Assert.Contains("만료 경과·30일 내 예정 27건", summary.AlertPopupMessage);
            Assert.All(db.ChangeTracker.Entries(), entry => Assert.Equal(EntityState.Unchanged, entry.State));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task GetDashboardSummaryAsync_KeepsCountsWithProjectedRentalRows()
    {
        PrepareAppRoot("georaeplan-rental-dashboard-summary");

        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
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
