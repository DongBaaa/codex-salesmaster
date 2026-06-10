using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalBillingSearchQueryTests
{
    [Fact]
    public async Task GetBillingRowsAsync_SearchContainsFallbackFindsMiddleProfileMatch()
    {
        PrepareAppRoot("georaeplan-rental-billing-search-profile-contains");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profile = CreateBillingProfile(1, "Alpha Customer", "Rental Special Plan");
            db.RentalBillingProfiles.Add(profile);
            await db.SaveChangesAsync();

            var service = new RentalStateService(db);
            var rows = await service.GetBillingRowsAsync(
                new RentalBillingFilter
                {
                    SearchText = "Special",
                    ExpandCustomerSummaryRows = true,
                    ReferenceDate = new DateOnly(2026, 6, 11)
                },
                CreateAdminSession());

            var row = Assert.Single(rows);
            Assert.True(row.HasPersistedProfile);
            Assert.Equal(profile.Id, row.Source.Id);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task GetBillingRowsAsync_SearchContainsFallbackFindsMiddleUnlinkedAssetMatch()
    {
        PrepareAppRoot("georaeplan-rental-billing-search-unlinked-contains");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var asset = CreateUnlinkedAsset(1, "Alpha Customer", "Rental Special Copier");
            db.RentalAssets.Add(asset);
            await db.SaveChangesAsync();

            var service = new RentalStateService(db);
            var rows = await service.GetBillingRowsAsync(
                new RentalBillingFilter
                {
                    SearchText = "Special",
                    ExpandCustomerSummaryRows = true,
                    ReferenceDate = new DateOnly(2026, 6, 11)
                },
                CreateAdminSession());

            var row = Assert.Single(rows);
            Assert.False(row.HasPersistedProfile);
            Assert.True(row.RequiresBillingProfileCreation);
            Assert.Equal(asset.Id, row.SelectionId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task GetBillingRowsAsync_SearchCapsPersistedProfileResults()
    {
        PrepareAppRoot("georaeplan-rental-billing-search-profile-cap");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var totalProfiles = RentalStateService.BillingProfileSearchResultLimit + 5;
            for (var index = 0; index < totalProfiles; index++)
                db.RentalBillingProfiles.Add(CreateBillingProfile(index, $"Search Customer {index:D4}", $"Search Plan {index:D4}"));
            await db.SaveChangesAsync();

            var service = new RentalStateService(db);
            var rows = await service.GetBillingRowsAsync(
                new RentalBillingFilter
                {
                    SearchText = "Search",
                    ExpandCustomerSummaryRows = true,
                    ReferenceDate = new DateOnly(2026, 6, 11)
                },
                CreateAdminSession());

            Assert.Equal(RentalStateService.BillingProfileSearchResultLimit, rows.Count);
            Assert.All(rows, row => Assert.True(row.HasPersistedProfile));
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

    private static LocalRentalBillingProfile CreateBillingProfile(int index, string customerName, string itemName)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ProfileKey = $"SEARCH-PROFILE-{index:D4}",
            CustomerName = customerName,
            BusinessNumber = $"123-45-{index:D4}",
            ItemName = itemName,
            BillingType = "묶음",
            BillingStatus = PaymentFlowConstants.BillingStatusPlanned,
            BillingDay = 25,
            BillingDayMode = RentalBillingScheduleRules.BillingDayModeFixedDay,
            BillingCycleMonths = 1,
            BillingAnchorMonth = 1,
            MonthlyAmount = 100_000m + index,
            IsActive = true,
            IsDirty = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static LocalRentalAsset CreateUnlinkedAsset(int index, string customerName, string itemName)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ManagementId = $"SEARCH-ASSET-{index:D4}",
            ManagementNumber = $"SEARCH-ASSET-{index:D4}",
            AssetKey = $"SEARCH-ASSET-{Guid.NewGuid():N}",
            CustomerName = customerName,
            CurrentCustomerName = customerName,
            ItemName = itemName,
            ItemCategoryName = "Copier",
            MachineNumber = $"SEARCH-SN-{index:D4}",
            InstallLocation = "Main Office",
            InstallSiteName = "Main Office",
            MonthlyFee = 100_000m + index,
            AssetStatus = "임대",
            BillingEligibilityStatus = "청구대상",
            IsDirty = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

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
