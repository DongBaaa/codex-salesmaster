using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalBillingAssetCandidatesTests
{
    [Fact]
    public async Task GetBillingAssetCandidatesAsync_ProjectionKeepsBillingOptionFields()
    {
        PrepareAppRoot("georaeplan-rental-billing-candidate-projection");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.Parse("c7100000-1111-4444-8888-000000000001");
            var asset = CreateRentalAsset(customerId);
            db.RentalAssets.Add(asset);
            await db.SaveChangesAsync();

            var service = new RentalStateService(db);
            var candidates = await service.GetBillingAssetCandidatesAsync(
                billingProfileId: null,
                customerId,
                customerName: "Projection Customer",
                officeCode: OfficeCodeCatalog.Usenet,
                includeOfficePoolAssets: false,
                CreateAdminSession());

            var candidate = Assert.Single(candidates);
            Assert.Equal(asset.Id, candidate.Id);
            Assert.Equal(customerId, candidate.CustomerId);
            Assert.Equal("MN-PROJECTION-001", candidate.ManagementNumber);
            Assert.Equal("Projection Customer", candidate.CurrentCustomerName);
            Assert.Equal("Projection Copier", candidate.ItemName);
            Assert.Equal("A3 Copier", candidate.ItemCategoryName);
            Assert.Equal("Projection Maker", candidate.Manufacturer);
            Assert.Equal("SN-PROJECTION-001", candidate.MachineNumber);
            Assert.Equal("HQ 3F", candidate.InstallLocation);
            Assert.Equal("임대진행중", candidate.AssetStatus);
            Assert.Equal("청구대상", candidate.BillingEligibilityStatus);
            Assert.Equal("projection note", candidate.Notes);
            Assert.Equal(123_000m, candidate.MonthlyFee);
            Assert.Equal(new DateOnly(2026, 1, 10), candidate.ContractStartDate);
            Assert.Equal(new DateOnly(2026, 1, 15), candidate.InstallDate);
            Assert.Equal(new DateOnly(2025, 12, 20), candidate.PurchaseDate);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task GetBillingAssetCandidatesAsync_TreatsDeletedProfileLinkAsReconnectable()
    {
        PrepareAppRoot("georaeplan-rental-billing-candidate-deleted-profile");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.Parse("c7200000-1111-4444-8888-000000000001");
            var deletedProfileId = Guid.Parse("c7200000-1111-4444-8888-0000000000d1");
            db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
            {
                Id = deletedProfileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                CustomerId = customerId,
                CustomerName = "Projection Customer",
                BillingType = "묶음",
                BillingAdvanceMode = "후불",
                BillingDay = 25,
                BillingCycleMonths = 1,
                IsDeleted = true,
                IsActive = false,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            var asset = CreateRentalAsset(customerId);
            asset.BillingProfileId = deletedProfileId;
            db.RentalAssets.Add(asset);
            await db.SaveChangesAsync();

            var candidates = await new RentalStateService(db).GetBillingAssetCandidatesAsync(
                billingProfileId: null,
                customerId,
                customerName: "Projection Customer",
                officeCode: OfficeCodeCatalog.Usenet,
                includeOfficePoolAssets: false,
                CreateAdminSession());

            var candidate = Assert.Single(candidates);
            Assert.Equal(asset.Id, candidate.Id);
            Assert.Equal(deletedProfileId, candidate.BillingProfileId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task GetBillingAssetCandidatesAsync_ExcludesZeroFeeAssetsFromBillingProfileCreation()
    {
        PrepareAppRoot("georaeplan-rental-billing-candidate-zero-fee");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.Parse("c7300000-1111-4444-8888-000000000001");
            var asset = CreateRentalAsset(customerId);
            asset.MonthlyFee = 0m;
            db.RentalAssets.Add(asset);
            await db.SaveChangesAsync();

            var candidates = await new RentalStateService(db).GetBillingAssetCandidatesAsync(
                billingProfileId: null,
                customerId,
                customerName: "Projection Customer",
                officeCode: OfficeCodeCatalog.Usenet,
                includeOfficePoolAssets: false,
                CreateAdminSession());

            Assert.Empty(candidates);
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

    private static LocalRentalAsset CreateRentalAsset(Guid customerId)
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
            ManagementNumber = "MN-PROJECTION-001",
            AssetKey = $"ASSET-{assetId:N}",
            CustomerId = customerId,
            CustomerName = "Projection Customer",
            CurrentCustomerName = "Projection Customer",
            ItemCategoryName = "A3 Copier",
            ItemName = "Projection Copier",
            Manufacturer = "Projection Maker",
            MachineNumber = "SN-PROJECTION-001",
            InstallSiteName = "HQ",
            InstallLocation = "HQ 3F",
            AssetStatus = "임대진행중",
            BillingEligibilityStatus = "청구대상",
            Notes = "projection note",
            MonthlyFee = 123_000m,
            ContractStartDate = new DateOnly(2026, 1, 10),
            InstallDate = new DateOnly(2026, 1, 15),
            PurchaseDate = new DateOnly(2025, 12, 20),
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
