using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class LocalIntegrityInventoryResidueTests
{
    [Fact]
    public async Task BuildIntegrityReportAsync_FindsDeletedItemStockResidue_AndStartupRepairClearsIt()
    {
        PrepareAppRoot("georaeplan-local-integrity-deleted-item-stock-residue");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateYeonsuAdminSession();
            var deletedItemId = Guid.NewGuid();
            db.Items.Add(new LocalItem
            {
                Id = deletedItemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Yeonsu,
                NameOriginal = "Deleted local residue item",
                NameMatchKey = "DELETEDLOCALRESIDUEITEM",
                TrackingType = ItemTrackingTypes.Stock,
                CurrentStock = 9m,
                IsDeleted = true,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock
            {
                ItemId = deletedItemId,
                WarehouseCode = OfficeCodeCatalog.GetMainWarehouseCode(OfficeCodeCatalog.Yeonsu),
                Quantity = 9m,
                UpdatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var report = await service.BuildIntegrityReportAsync(session);
            var residueIssue = Assert.Single(report.Issues, issue => issue.Code == "inventory_deleted_item_stock_residue");
            Assert.Equal(1, residueIssue.Count);
            Assert.Contains("Deleted local residue item", residueIssue.EffectiveDetailRows.Single(), StringComparison.Ordinal);

            var repairResult = await service.RepairInventoryIntegrityForStartupAsync(session);
            Assert.True(repairResult.RepairedAny);

            var repairedReport = await service.BuildIntegrityReportAsync(session);
            Assert.DoesNotContain(repairedReport.Issues, issue => issue.Code == "inventory_deleted_item_stock_residue");
            Assert.Equal(0m, await db.Items.IgnoreQueryFilters()
                .Where(item => item.Id == deletedItemId)
                .Select(item => item.CurrentStock)
                .SingleAsync());
            Assert.False(await db.ItemWarehouseStocks.AnyAsync(stock => stock.ItemId == deletedItemId));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RepairInventoryIntegrityForStartupAsync_DoesNotMutateMixedOfficeInventoryScope()
    {
        PrepareAppRoot("georaeplan-local-integrity-mixed-office-repair-guard");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateYeonsuAdminSession();
            var yeonsuDeletedItemId = Guid.NewGuid();
            var usenetDeletedItemId = Guid.NewGuid();
            db.Items.AddRange(
                new LocalItem
                {
                    Id = yeonsuDeletedItemId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Yeonsu,
                    NameOriginal = "Yeonsu deleted residue item",
                    NameMatchKey = "YEONSUDELETEDRESIDUEITEM",
                    TrackingType = ItemTrackingTypes.Stock,
                    CurrentStock = 9m,
                    IsDeleted = true,
                    CreatedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow
                },
                new LocalItem
                {
                    Id = usenetDeletedItemId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    NameOriginal = "Usenet deleted residue item",
                    NameMatchKey = "USENETDELETEDRESIDUEITEM",
                    TrackingType = ItemTrackingTypes.Stock,
                    CurrentStock = 7m,
                    IsDeleted = true,
                    CreatedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow
                });
            db.ItemWarehouseStocks.AddRange(
                new LocalItemWarehouseStock
                {
                    ItemId = yeonsuDeletedItemId,
                    WarehouseCode = OfficeCodeCatalog.GetMainWarehouseCode(OfficeCodeCatalog.Yeonsu),
                    Quantity = 9m,
                    UpdatedAtUtc = DateTime.UtcNow
                },
                new LocalItemWarehouseStock
                {
                    ItemId = usenetDeletedItemId,
                    WarehouseCode = OfficeCodeCatalog.GetMainWarehouseCode(OfficeCodeCatalog.Usenet),
                    Quantity = 7m,
                    UpdatedAtUtc = DateTime.UtcNow
                });
            await db.SaveChangesAsync();

            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var report = await service.BuildIntegrityReportAsync(session);
            Assert.Contains(report.Issues, issue =>
                issue.Code == "inventory_deleted_item_stock_residue" &&
                issue.Count == 1 &&
                issue.EffectiveDetailRows.Any(row => row.Contains("Yeonsu deleted residue item", StringComparison.Ordinal)));
            Assert.DoesNotContain(report.Issues, issue =>
                issue.Code == "inventory_deleted_item_stock_residue" &&
                issue.EffectiveDetailRows.Any(row => row.Contains("Usenet deleted residue item", StringComparison.Ordinal)));
            Assert.DoesNotContain(report.Issues, issue => issue.Code == "out_of_scope_items");

            var repairResult = await service.RepairInventoryIntegrityForStartupAsync(session);

            Assert.False(repairResult.RepairedAny);
            Assert.Equal(9m, await db.Items.IgnoreQueryFilters()
                .Where(item => item.Id == yeonsuDeletedItemId)
                .Select(item => item.CurrentStock)
                .SingleAsync());
            Assert.Equal(7m, await db.Items.IgnoreQueryFilters()
                .Where(item => item.Id == usenetDeletedItemId)
                .Select(item => item.CurrentStock)
                .SingleAsync());
            Assert.True(await db.ItemWarehouseStocks.AnyAsync(stock =>
                stock.ItemId == yeonsuDeletedItemId &&
                stock.WarehouseCode == OfficeCodeCatalog.GetMainWarehouseCode(OfficeCodeCatalog.Yeonsu)));
            Assert.True(await db.ItemWarehouseStocks.AnyAsync(stock =>
                stock.ItemId == usenetDeletedItemId &&
                stock.WarehouseCode == OfficeCodeCatalog.GetMainWarehouseCode(OfficeCodeCatalog.Usenet)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RepairInventoryIntegrityForStartupAsync_NonInventoryItemCleansStaleRowsAndSafetyStock()
    {
        PrepareAppRoot("georaeplan-local-integrity-noninventory-stock-residue");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateYeonsuAdminSession();
            var itemId = Guid.NewGuid();
            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Yeonsu,
                NameOriginal = "Legacy asset stock residue item",
                NameMatchKey = "LEGACYASSETSTOCKRESIDUEITEM",
                ItemKind = ItemKinds.Asset,
                TrackingType = ItemTrackingTypes.Asset,
                IsRental = true,
                IsSale = false,
                CurrentStock = 6m,
                SafetyStock = 2m,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode = OfficeCodeCatalog.GetMainWarehouseCode(OfficeCodeCatalog.Yeonsu),
                Quantity = 6m,
                UpdatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var repairResult = await service.RepairInventoryIntegrityForStartupAsync(session);

            Assert.True(repairResult.RepairedAny);
            Assert.False(await db.ItemWarehouseStocks.AnyAsync(stock => stock.ItemId == itemId));
            var item = await db.Items.AsNoTracking().SingleAsync(current => current.Id == itemId);
            Assert.Equal(ItemTrackingTypes.Asset, item.TrackingType);
            Assert.Equal(ItemKinds.Asset, item.ItemKind);
            Assert.True(item.IsRental);
            Assert.False(item.IsSale);
            Assert.Equal(0m, item.CurrentStock);
            Assert.Equal(0m, item.SafetyStock);
            Assert.True(item.IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SetItemOfficeStockAsync_NonInventoryItemCleansStaleRowsAndRejectsQuantity()
    {
        PrepareAppRoot("georaeplan-local-noninventory-stock-set-guard");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateYeonsuAdminSession();
            var itemId = Guid.NewGuid();
            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Yeonsu,
                NameOriginal = "수동 재고 차단 렌탈 장비",
                NameMatchKey = "수동재고차단렌탈장비",
                ItemKind = ItemKinds.Asset,
                TrackingType = ItemTrackingTypes.Asset,
                IsRental = true,
                IsSale = false,
                CurrentStock = 6m,
                SafetyStock = 2m,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode = OfficeCodeCatalog.GetMainWarehouseCode(OfficeCodeCatalog.Yeonsu),
                Quantity = 6m,
                UpdatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.SetItemOfficeStockAsync(itemId, 4m, OfficeCodeCatalog.Yeonsu));

            Assert.Contains("재고 추적 대상이 아닌 품목", ex.Message, StringComparison.Ordinal);
            Assert.False(await db.ItemWarehouseStocks.AnyAsync(stock => stock.ItemId == itemId));
            var item = await db.Items.AsNoTracking().SingleAsync(current => current.Id == itemId);
            Assert.Equal(0m, item.CurrentStock);
            Assert.Equal(0m, item.SafetyStock);
            Assert.True(item.IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private static void PrepareAppRoot(string prefix)
    {
        var root = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", root);
    }

    private static SessionState CreateYeonsuAdminSession()
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            Username = "yeonsu-admin",
            Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ScopeType = TenantScopeCatalog.ScopeAdmin
        });
        return session;
    }
}
