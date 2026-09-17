using Microsoft.EntityFrameworkCore;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed partial class RecycleBinRestoreCascadeRevisionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    public async Task RestoreItem_ReconcilesMasterFromRetainedWarehousesWithoutHistory(decimal deletedMasterQuantity)
    {
        await using var db = CreateDbContext();
        var item = new Item { Id = Guid.NewGuid(), TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet, NameOriginal = "Deleted stock item", NameMatchKey = "DELETEDSTOCKITEM",
            ItemKind = ItemKinds.Product, TrackingType = ItemTrackingTypes.Stock, CurrentStock = deletedMasterQuantity, IsDeleted = true };
        var unrelated = new Item { Id = Guid.NewGuid(), TenantCode = item.TenantCode, OfficeCode = item.OfficeCode,
            NameOriginal = "Other stock", NameMatchKey = "OTHERSTOCK", TrackingType = ItemTrackingTypes.Stock, CurrentStock = 17m };
        db.Items.AddRange(item, unrelated);
        db.ItemWarehouseStocks.AddRange(
            new ItemWarehouseStock { ItemId = item.Id, WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse, Quantity = 5m },
            new ItemWarehouseStock { ItemId = item.Id, WarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse, Quantity = 3m },
            new ItemWarehouseStock { ItemId = unrelated.Id, WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse, Quantity = 17m });
        await db.SaveChangesAsync();
        var revision = item.Revision;
        var beforeStocks = await db.ItemWarehouseStocks.IgnoreQueryFilters().AsNoTracking().OrderBy(x => x.ItemId).ThenBy(x => x.WarehouseCode)
            .Select(x => new { x.ItemId, x.WarehouseCode, x.Quantity, x.Revision, x.UpdatedAtUtc }).ToListAsync();
        var unrelatedRevision = unrelated.Revision;

        var result = await RestoreAsync(db, "item", item.Id, revision);
        Assert.True(result.Success, result.Message);
        db.ChangeTracker.Clear();
        var restored = await db.Items.IgnoreQueryFilters().SingleAsync(x => x.Id == item.Id);
        Assert.False(restored.IsDeleted);
        Assert.Equal(8m, restored.CurrentStock);
        Assert.Equal(item.OfficeCode, restored.OfficeCode);
        Assert.True(restored.Revision > revision);
        var afterStocks = await db.ItemWarehouseStocks.IgnoreQueryFilters().AsNoTracking().OrderBy(x => x.ItemId).ThenBy(x => x.WarehouseCode)
            .Select(x => new { x.ItemId, x.WarehouseCode, x.Quantity, x.Revision, x.UpdatedAtUtc }).ToListAsync();
        Assert.Equal(beforeStocks, afterStocks);
        var preserved = await db.Items.SingleAsync(x => x.Id == unrelated.Id);
        Assert.Equal(17m, preserved.CurrentStock);
        Assert.Equal(unrelatedRevision, preserved.Revision);
    }
}
