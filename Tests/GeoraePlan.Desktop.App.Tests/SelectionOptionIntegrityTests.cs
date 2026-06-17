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


    [Fact]
    public async Task RecycleBin_IncludesDeletedCustomerCategoryForAdmin()
    {
        PrepareAppRoot("georaeplan-customer-category-recycle-list");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var categoryId = Guid.NewGuid();
            db.CustomerCategories.Add(new LocalCustomerCategory
            {
                Id = categoryId,
                Name = "휴지통분류",
                IsDeleted = true,
                IsDirty = false,
                UpdatedAtUtc = new DateTime(2026, 6, 17, 9, 0, 0, DateTimeKind.Utc)
            });
            await db.SaveChangesAsync();

            var local = CreateLocalStateService(db);
            var entries = await local.GetRecycleBinEntriesAsync(CreateAdminSession());

            var entry = Assert.Single(entries, current => current.EntityId == categoryId);
            Assert.Equal(RecycleBinEntityKind.CustomerCategory, entry.Kind);
            Assert.Equal("고객분류", entry.KindText);
            Assert.Equal("휴지통분류", entry.Title);
            Assert.False((await db.CustomerCategories.IgnoreQueryFilters().AsNoTracking().SingleAsync(row => row.Id == categoryId)).IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RestoreCustomerCategory_RejectsDuplicateActiveNameAndKeepsDeletedRowClean()
    {
        PrepareAppRoot("georaeplan-customer-category-restore-duplicate");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var activeId = Guid.NewGuid();
            var deletedId = Guid.NewGuid();
            db.CustomerCategories.AddRange(
                new LocalCustomerCategory
                {
                    Id = activeId,
                    Name = "공공기관",
                    IsDeleted = false,
                    IsDirty = false
                },
                new LocalCustomerCategory
                {
                    Id = deletedId,
                    Name = " 공공기관 ",
                    IsDeleted = true,
                    IsDirty = false
                });
            await db.SaveChangesAsync();

            var local = CreateLocalStateService(db);
            var result = await local.RestoreRecycleBinEntryAsync(
                RecycleBinEntityKind.CustomerCategory,
                deletedId,
                CreateAdminSession());

            Assert.False(result.Success);
            var rows = await db.CustomerCategories.IgnoreQueryFilters().AsNoTracking().ToDictionaryAsync(row => row.Id);
            Assert.False(rows[activeId].IsDeleted);
            Assert.False(rows[activeId].IsDirty);
            Assert.True(rows[deletedId].IsDeleted);
            Assert.False(rows[deletedId].IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RestoreCustomerCategory_RestoresDeletedRowAsDirty()
    {
        PrepareAppRoot("georaeplan-customer-category-restore-success");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var categoryId = Guid.NewGuid();
            db.CustomerCategories.Add(new LocalCustomerCategory
            {
                Id = categoryId,
                Name = "복원분류",
                IsDeleted = true,
                IsDirty = false
            });
            await db.SaveChangesAsync();

            var local = CreateLocalStateService(db);
            var result = await local.RestoreRecycleBinEntryAsync(
                RecycleBinEntityKind.CustomerCategory,
                categoryId,
                CreateAdminSession());

            Assert.True(result.Success, result.Message);
            var row = await db.CustomerCategories.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == categoryId);
            Assert.False(row.IsDeleted);
            Assert.True(row.IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task PermanentlyDeleteCustomerCategory_RejectsReferencedCategoryAndKeepsRow()
    {
        PrepareAppRoot("georaeplan-customer-category-purge-in-use");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var categoryId = Guid.NewGuid();
            db.CustomerCategories.Add(new LocalCustomerCategory
            {
                Id = categoryId,
                Name = "참조분류",
                IsDeleted = true,
                IsDirty = false
            });
            db.Customers.Add(new LocalCustomer
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "참조 거래처",
                NameMatchKey = "참조거래처",
                CategoryId = categoryId,
                TradeType = CustomerTradeTypes.Sales,
                IsDeleted = true,
                IsDirty = false
            });
            await db.SaveChangesAsync();

            var local = CreateLocalStateService(db);
            var result = await local.PermanentlyDeleteRecycleBinEntryAsync(
                RecycleBinEntityKind.CustomerCategory,
                categoryId,
                CreateAdminSession());

            Assert.False(result.Success);
            Assert.True(await db.CustomerCategories.IgnoreQueryFilters().AnyAsync(current => current.Id == categoryId));
            Assert.False((await db.CustomerCategories.IgnoreQueryFilters().AsNoTracking().SingleAsync(current => current.Id == categoryId)).IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RestorePriceGradeOption_RejectsDuplicateActiveNameAndKeepsDeletedRowClean()
    {
        PrepareAppRoot("georaeplan-price-grade-restore-duplicate");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var activeId = Guid.NewGuid();
            var deletedId = Guid.NewGuid();
            db.PriceGradeOptions.AddRange(
                new LocalPriceGradeOption
                {
                    Id = activeId,
                    Name = "VIP",
                    PriceSource = SelectionOptionDefaults.PriceSourceSales,
                    SortOrder = 10,
                    IsActive = true,
                    IsDeleted = false,
                    IsDirty = false
                },
                new LocalPriceGradeOption
                {
                    Id = deletedId,
                    Name = "VIP",
                    PriceSource = SelectionOptionDefaults.PriceSourceA,
                    SortOrder = 20,
                    IsActive = false,
                    IsDeleted = true,
                    IsDirty = false
                });
            await db.SaveChangesAsync();

            var local = CreateLocalStateService(db);
            var result = await local.RestoreRecycleBinEntryAsync(
                RecycleBinEntityKind.PriceGradeOption,
                deletedId,
                CreateAdminSession());

            Assert.False(result.Success);
            var rows = await db.PriceGradeOptions.IgnoreQueryFilters().AsNoTracking().ToDictionaryAsync(row => row.Id);
            Assert.False(rows[activeId].IsDeleted);
            Assert.True(rows[activeId].IsActive);
            Assert.False(rows[activeId].IsDirty);
            Assert.True(rows[deletedId].IsDeleted);
            Assert.False(rows[deletedId].IsActive);
            Assert.False(rows[deletedId].IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RestoreItemCategoryOption_RejectsLooseKeyDuplicateAndKeepsDeletedRowClean()
    {
        PrepareAppRoot("georaeplan-item-category-restore-duplicate");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var activeId = Guid.NewGuid();
            var deletedId = Guid.NewGuid();
            db.ItemCategoryOptions.AddRange(
                new LocalItemCategoryOption
                {
                    Id = activeId,
                    Name = "A3 Copier",
                    SortOrder = 10,
                    IsActive = true,
                    IsDeleted = false,
                    IsDirty = false
                },
                new LocalItemCategoryOption
                {
                    Id = deletedId,
                    Name = "A3Copier",
                    SortOrder = 20,
                    IsActive = false,
                    IsDeleted = true,
                    IsDirty = false
                });
            await db.SaveChangesAsync();

            var local = CreateLocalStateService(db);
            var result = await local.RestoreRecycleBinEntryAsync(
                RecycleBinEntityKind.ItemCategoryOption,
                deletedId,
                CreateAdminSession());

            Assert.False(result.Success);
            var rows = await db.ItemCategoryOptions.IgnoreQueryFilters().AsNoTracking().ToDictionaryAsync(row => row.Id);
            Assert.False(rows[activeId].IsDeleted);
            Assert.True(rows[activeId].IsActive);
            Assert.False(rows[activeId].IsDirty);
            Assert.True(rows[deletedId].IsDeleted);
            Assert.False(rows[deletedId].IsActive);
            Assert.False(rows[deletedId].IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }


    [Fact]
    public async Task RestoreTradeTypeOption_RejectsDuplicateCanonicalActiveNameAndKeepsDeletedRowClean()
    {
        PrepareAppRoot("georaeplan-trade-type-restore-duplicate");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var activeId = Guid.NewGuid();
            var deletedId = Guid.NewGuid();
            db.TradeTypeOptions.AddRange(
                new LocalTradeTypeOption
                {
                    Id = activeId,
                    Name = CustomerTradeTypes.Sales,
                    AllowsSales = true,
                    AllowsPurchase = false,
                    SortOrder = 0,
                    IsActive = true,
                    IsDeleted = false,
                    IsDirty = false
                },
                new LocalTradeTypeOption
                {
                    Id = deletedId,
                    Name = CustomerTradeTypes.Sales,
                    AllowsSales = true,
                    AllowsPurchase = false,
                    SortOrder = 30,
                    IsActive = false,
                    IsDeleted = true,
                    IsDirty = false
                });
            await db.SaveChangesAsync();

            var local = CreateLocalStateService(db);
            var result = await local.RestoreRecycleBinEntryAsync(
                RecycleBinEntityKind.TradeTypeOption,
                deletedId,
                CreateAdminSession());

            Assert.False(result.Success);
            var rows = await db.TradeTypeOptions.IgnoreQueryFilters().AsNoTracking().ToDictionaryAsync(row => row.Id);
            Assert.False(rows[activeId].IsDeleted);
            Assert.True(rows[activeId].IsActive);
            Assert.False(rows[activeId].IsDirty);
            Assert.True(rows[deletedId].IsDeleted);
            Assert.False(rows[deletedId].IsActive);
            Assert.False(rows[deletedId].IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private static SessionState CreateAdminSession()
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
        return session;
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
