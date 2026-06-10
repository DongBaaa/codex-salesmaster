using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalAssetSearchLimitTests
{
    [Fact]
    public async Task GetAssetRowsAsync_CapsSearchResultsButKeepsUnfilteredListLimit()
    {
        PrepareAppRoot("georaeplan-rental-asset-search-limit");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var totalAssets = RentalStateService.AssetSearchResultLimit + 25;
            for (var index = 0; index < totalAssets; index++)
                db.RentalAssets.Add(CreateRentalAsset(index));
            await db.SaveChangesAsync();

            var service = new RentalStateService(db);
            var session = CreateAdminSession();

            var unfilteredRows = await service.GetAssetRowsAsync(
                new RentalAssetFilter { MaxResults = RentalStateService.AssetListResultLimit },
                session);
            Assert.Equal(totalAssets, unfilteredRows.Count);

            var searchRows = await service.GetAssetRowsAsync(
                new RentalAssetFilter
                {
                    SearchText = "검색고객",
                    MaxResults = RentalStateService.AssetListResultLimit
                },
                session);
            Assert.Equal(RentalStateService.AssetSearchResultLimit, searchRows.Count);
            Assert.All(searchRows, row => Assert.Contains("검색고객", row.CurrentCustomerName));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    private static void PrepareAppRoot(string prefix)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);
    }

    private static LocalRentalAsset CreateRentalAsset(int index)
    {
        var assetId = Guid.NewGuid();
        return new LocalRentalAsset
        {
            Id = assetId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ManagementId = $"M-{index:D4}",
            ManagementNumber = $"MN-{index:D4}",
            AssetKey = $"AK-{assetId:N}",
            CustomerName = $"검색고객 {index:D4}",
            CurrentCustomerName = $"검색고객 {index:D4}",
            ItemCategoryName = "복합기",
            ItemName = $"렌탈 복합기 {index:D4}",
            MachineNumber = $"SN-{index:D4}",
            InstallSiteName = "본점",
            InstallLocation = "본점",
            AssetStatus = "렌탈중",
            BillingEligibilityStatus = "청구대상",
            MonthlyFee = 100_000m,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
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