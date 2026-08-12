using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalAssetProfileFreeSaveTests
{
    [Fact]
    public async Task SaveAssetAsync_BlankItemName_DoesNotCreateOrLinkCatalogItem()
    {
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"georaeplan-profile-free-rental-asset-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var assetId = Guid.NewGuid();
            var marker = $"PROFILE-FREE-{assetId:N}";
            var asset = new LocalRentalAsset
            {
                Id = assetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ManagementId = marker,
                ManagementNumber = marker,
                MachineNumber = marker,
                CurrentLocation = "MULTIPC-ISOLATED",
                ItemCategoryName = "Printer",
                ItemName = string.Empty,
                AssetStatus = "창고",
                BillingEligibilityStatus = "청구제외",
                BillingExclusionReason = marker,
                Notes = $"{marker}|INITIAL"
            };

            var session = new SessionState();
            session.SetOfflineSession(new UserSessionDto
            {
                UserId = Guid.NewGuid(),
                Username = $"profile-free-rental-asset-{Guid.NewGuid():N}",
                Role = DomainConstants.RoleAdmin,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeAdmin
            });

            var result = await new RentalStateService(db).SaveAssetAsync(
                asset,
                session,
                allowCategoryRecovery: true);

            Assert.True(result.Success, result.Message);
            var stored = await db.RentalAssets
                .IgnoreQueryFilters()
                .SingleAsync(current => current.Id == assetId);
            Assert.Null(stored.ItemId);
            Assert.True(string.IsNullOrWhiteSpace(stored.ItemName));
            Assert.Empty(await db.Items.IgnoreQueryFilters().ToListAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }
}
