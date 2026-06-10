using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalIncludedBillingAssetsTests
{
    [Fact]
    public async Task GetIncludedBillingAssetsAsync_ExplicitIncludedAssetOutsideProfileSortWindowIsStillIncluded()
    {
        PrepareAppRoot("georaeplan-rental-included-assets-explicit-window");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.Parse("f2100000-1111-4444-8888-000000000001");
            for (var index = 0; index < 300; index++)
            {
                db.RentalAssets.Add(CreateRentalAsset(
                    $"A Profile Customer {index:D4}",
                    $"A-{index:D4}",
                    profileId));
            }

            var explicitAsset = CreateRentalAsset(
                "ZZZ Explicit Customer",
                "Z-9999",
                billingProfileId: null);
            db.RentalAssets.Add(explicitAsset);
            await db.SaveChangesAsync();

            var service = new RentalStateService(db);
            var rows = await service.GetIncludedBillingAssetsAsync(
                profileId,
                new[] { explicitAsset.Id },
                customerId: null,
                officeCode: OfficeCodeCatalog.Usenet,
                CreateAdminSession());

            Assert.Contains(rows, asset => asset.Id == explicitAsset.Id);
            Assert.Equal(301, rows.Count);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task GetIncludedBillingAssetsAsync_BatchesManyExplicitIncludedAssetIds()
    {
        PrepareAppRoot("georaeplan-rental-included-assets-explicit-batch");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.Parse("f2200000-1111-4444-8888-000000000001");
            for (var index = 0; index < 350; index++)
            {
                db.RentalAssets.Add(CreateRentalAsset(
                    $"A Profile Customer {index:D4}",
                    $"P-{index:D4}",
                    profileId));
            }

            var explicitAssetIds = new List<Guid>();
            for (var index = 0; index < 650; index++)
            {
                var explicitAsset = CreateRentalAsset(
                    $"Z Explicit Customer {index:D4}",
                    $"E-{index:D4}",
                    billingProfileId: null);
                explicitAssetIds.Add(explicitAsset.Id);
                db.RentalAssets.Add(explicitAsset);
            }

            await db.SaveChangesAsync();

            var service = new RentalStateService(db);
            var rows = await service.GetIncludedBillingAssetsAsync(
                profileId,
                explicitAssetIds,
                customerId: null,
                officeCode: OfficeCodeCatalog.Usenet,
                CreateAdminSession());

            Assert.Equal(950, rows.Count);
            Assert.Contains(rows, asset => asset.Id == explicitAssetIds.First());
            Assert.Contains(rows, asset => asset.Id == explicitAssetIds.Last());
            Assert.Equal(650, rows.Count(asset => explicitAssetIds.Contains(asset.Id)));
            Assert.Equal(300, rows.Count(asset => asset.BillingProfileId == profileId));
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

    private static LocalRentalAsset CreateRentalAsset(
        string customerName,
        string managementNumber,
        Guid? billingProfileId)
    {
        var assetId = Guid.NewGuid();
        return new LocalRentalAsset
        {
            Id = assetId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ManagementId = $"MID-{assetId:N}",
            ManagementNumber = managementNumber,
            AssetKey = $"ASSET-{assetId:N}",
            CustomerName = customerName,
            CurrentCustomerName = customerName,
            BillingProfileId = billingProfileId,
            ItemCategoryName = "Copier",
            ItemName = "Rental Copier",
            MachineNumber = $"SN-{assetId:N}",
            InstallSiteName = "HQ",
            InstallLocation = "HQ",
            AssetStatus = "임대진행중",
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
