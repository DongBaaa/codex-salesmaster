using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalServiceSearchGuardTests
{
    [Fact]
    public async Task GetAssetRowsAsync_SkipsSingleCharacterSearchAtServiceLayer()
    {
        PrepareAppRoot("georaeplan-rental-asset-service-search-guard");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            db.RentalAssets.Add(CreateRentalAsset("A-001", "A Customer"));
            await db.SaveChangesAsync();

            var service = new RentalStateService(db);
            var rows = await service.GetAssetRowsAsync(
                new RentalAssetFilter
                {
                    SearchText = "A",
                    MaxResults = RentalStateService.AssetListResultLimit
                },
                CreateAdminSession());

            Assert.Empty(rows);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task GetBillingRowsAsync_SkipsSingleCharacterSearchAtServiceLayer()
    {
        PrepareAppRoot("georaeplan-rental-billing-service-search-guard");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ProfileKey = "SERVICE-SEARCH-GUARD-PROFILE",
                CustomerName = "A Customer",
                ItemName = "Rental Copier",
                MonthlyAmount = 100_000m,
                IsActive = true,
                IsDeleted = false,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            var service = new RentalStateService(db);
            var rows = await service.GetBillingRowsAsync(
                new RentalBillingFilter
                {
                    SearchText = "A",
                    ExpandCustomerSummaryRows = true,
                    ReferenceDate = new DateOnly(2026, 6, 11)
                },
                CreateAdminSession());

            Assert.Empty(rows);
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

    private static LocalRentalAsset CreateRentalAsset(string managementNumber, string customerName)
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
