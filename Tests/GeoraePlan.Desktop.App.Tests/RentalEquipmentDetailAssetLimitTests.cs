using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.Data;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.Services;
using \uAC70\uB798\uD50C\uB79C.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalEquipmentDetailAssetLimitTests
{
    [Fact]
    public async Task GetAssetsForEquipmentDetailAsync_BoundsRelatedAssetsAndKeepsAnchor()
    {
        PrepareAppRoot("georaeplan-rental-equipment-detail-limit");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.Parse("44444444-4444-4444-4444-444444444444");
            var anchorAsset = CreateAsset(RentalStateService.EquipmentDetailAssetLimit + 50, customerId, "ZZZ-ANCHOR");
            for (var index = 0; index < RentalStateService.EquipmentDetailAssetLimit + 50; index++)
                db.RentalAssets.Add(CreateAsset(index, customerId, $"ASSET-{index:D4}"));
            db.RentalAssets.Add(anchorAsset);
            await db.SaveChangesAsync();

            var rows = await new RentalStateService(db).GetAssetsForEquipmentDetailAsync(
                anchorAsset,
                CreateAdminSession());

            Assert.Equal(RentalStateService.EquipmentDetailAssetLimit, rows.Count);
            Assert.Equal(anchorAsset.Id, rows[0].Id);
            Assert.Contains(rows, asset => asset.Id == anchorAsset.Id);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private static LocalRentalAsset CreateAsset(int index, Guid customerId, string managementNumber)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ManagementId = $"DETAIL-{index:D4}",
            ManagementNumber = managementNumber,
            AssetKey = $"DETAIL-ASSET-{Guid.NewGuid():N}",
            CustomerId = customerId,
            CustomerName = "Detail Customer",
            CurrentCustomerName = "Detail Customer",
            ItemCategoryName = "Copier",
            ItemName = $"Printer {index:D4}",
            MachineNumber = $"DETAIL-SN-{index:D4}",
            InstallSiteName = "Main Office",
            InstallLocation = "Main Office",
            AssetStatus = "Rental",
            BillingEligibilityStatus = "Billable",
            MonthlyFee = 100_000m,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

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
