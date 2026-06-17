using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class SelectionOptionIntegrityTests
{
    [Fact]
    public async Task SaveItemCategoryOption_RejectsLooseKeyDuplicateAndKeepsDatabaseClean()
    {
        PrepareAppRoot("georaeplan-item-category-loose-duplicate");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var existingId = Guid.NewGuid();
            db.ItemCategoryOptions.Add(new LocalItemCategoryOption
            {
                Id = existingId,
                Name = "A3 Copier",
                SortOrder = 10,
                IsActive = true,
                IsDeleted = false,
                IsDirty = false
            });
            await db.SaveChangesAsync();

            var local = CreateLocalStateService(db);

            var result = await local.SaveItemCategoryOptionAsync(new LocalItemCategoryOption
            {
                Id = Guid.NewGuid(),
                Name = "A3Copier",
                SortOrder = 20
            });

            Assert.False(result.Success);
            Assert.Contains("이미", result.Message);
            var options = await db.ItemCategoryOptions.IgnoreQueryFilters().AsNoTracking().ToListAsync();
            var existing = Assert.Single(options);
            Assert.Equal(existingId, existing.Id);
            Assert.Equal("A3 Copier", existing.Name);
            Assert.False(existing.IsDirty);
            Assert.False(existing.IsDeleted);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SaveItemCategoryOption_RenamePropagatesToItemsAndRentalAssetsAsDirty()
    {
        PrepareAppRoot("georaeplan-item-category-rename-propagation");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var optionId = Guid.NewGuid();
            var itemId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            db.ItemCategoryOptions.Add(new LocalItemCategoryOption
            {
                Id = optionId,
                Name = "Printer",
                SortOrder = 10,
                Revision = 5,
                IsActive = true,
                IsDeleted = false,
                IsDirty = false
            });
            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Linked Item",
                NameMatchKey = "LINKEDITEM",
                CategoryName = "Printer",
                SpecificationOriginal = "A3",
                SpecificationMatchKey = "A3",
                TrackingType = ItemTrackingTypes.Stock,
                IsDeleted = false,
                IsDirty = false
            });
            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = assetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ItemCategoryName = "Printer",
                ItemName = "Linked Asset",
                ManagementNumber = "RA-001",
                AssetStatus = RentalAssetStatusNormalizer.Active,
                IsDeleted = false,
                IsDirty = false
            });
            await db.SaveChangesAsync();

            var local = CreateLocalStateService(db);

            var result = await local.SaveItemCategoryOptionAsync(
                new LocalItemCategoryOption
                {
                    Id = optionId,
                    Name = "Office Printer",
                    SortOrder = 15,
                    Revision = 5
                },
                previousName: "Printer",
                expectedRevision: 5);

            Assert.True(result.Success, result.Message);
            var option = await db.ItemCategoryOptions.IgnoreQueryFilters().AsNoTracking().SingleAsync(row => row.Id == optionId);
            var item = await db.Items.IgnoreQueryFilters().AsNoTracking().SingleAsync(row => row.Id == itemId);
            var asset = await db.RentalAssets.IgnoreQueryFilters().AsNoTracking().SingleAsync(row => row.Id == assetId);

            Assert.Equal("Office Printer", option.Name);
            Assert.Equal(15, option.SortOrder);
            Assert.True(option.IsDirty);
            Assert.Equal("Office Printer", item.CategoryName);
            Assert.True(item.IsDirty);
            Assert.Equal("Office Printer", asset.ItemCategoryName);
            Assert.True(asset.IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task DeleteItemCategoryOption_DetectsLooseKeyUsageBeforeDeleting()
    {
        PrepareAppRoot("georaeplan-item-category-delete-loose-in-use");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var optionId = Guid.NewGuid();
            var itemId = Guid.NewGuid();
            db.ItemCategoryOptions.Add(new LocalItemCategoryOption
            {
                Id = optionId,
                Name = "A3 Copier",
                SortOrder = 10,
                Revision = 7,
                IsActive = true,
                IsDeleted = false,
                IsDirty = false
            });
            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "In-use Item",
                NameMatchKey = "INUSEITEM",
                CategoryName = "A3Copier",
                SpecificationOriginal = "A3",
                SpecificationMatchKey = "A3",
                TrackingType = ItemTrackingTypes.Stock,
                IsDeleted = false,
                IsDirty = false
            });
            await db.SaveChangesAsync();

            var local = CreateLocalStateService(db);

            var result = await local.DeleteItemCategoryOptionAsync(optionId, expectedRevision: 7);

            Assert.False(result.Success);
            Assert.Contains("사용 중", result.Message);
            var option = await db.ItemCategoryOptions.IgnoreQueryFilters().AsNoTracking().SingleAsync(row => row.Id == optionId);
            Assert.False(option.IsDeleted);
            Assert.True(option.IsActive);
            Assert.False(option.IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private static LocalStateService CreateLocalStateService(LocalDbContext db)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            UserId = Guid.NewGuid(),
            Username = "selection-admin",
            Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin
        });
        return new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
    }

    private static void PrepareAppRoot(string prefix)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);
    }
}
