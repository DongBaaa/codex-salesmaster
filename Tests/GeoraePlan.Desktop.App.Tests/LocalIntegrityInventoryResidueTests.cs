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
