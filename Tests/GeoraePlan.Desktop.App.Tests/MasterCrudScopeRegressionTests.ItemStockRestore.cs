using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed partial class MasterCrudScopeRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ItemRestore_PreservesDeletedBaselineWithoutLocalHistory(bool scopedDelete)
    {
        var previousLegacyMerge = Environment.GetEnvironmentVariable("GEORAEPLAN_DISABLE_LEGACY_MERGE");
        var previousServerSync = Environment.GetEnvironmentVariable("GEORAEPLAN_DISABLE_SERVER_SYNC");
        PrepareIsolatedAppRoot("item-baseline-restore");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync(); await db.Database.EnsureCreatedAsync();
            var local = CreateLocalService(db, out var session, out _);
            var item = CreateItem(Guid.NewGuid()); item.CurrentStock = 10m; item.IsDirty = false;
            var unrelated = CreateItem(Guid.NewGuid()); unrelated.CurrentStock = 17m; unrelated.IsDirty = false;
            db.Items.AddRange(item, unrelated);
            db.ItemWarehouseStocks.AddRange(
                new LocalItemWarehouseStock { ItemId = item.Id, WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse, Quantity = 10m, Revision = 77 },
                new LocalItemWarehouseStock { ItemId = unrelated.Id, WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse, Quantity = 17m, Revision = 88 });
            await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            if (scopedDelete) Assert.True((await local.DeleteItemAsync(item.Id, session)).Success);
            else { await local.DeleteItemAsync(item.Id); await local.DeleteItemAsync(item.Id); }
            db.ChangeTracker.Clear();
            Assert.Empty(await db.ItemWarehouseStocks.Where(x => x.ItemId == item.Id).ToListAsync());
            Assert.True((await local.RestoreItemAsync(item.Id, session)).Success);
            Assert.True((await local.RestoreItemAsync(item.Id, session)).Success);
            db.ChangeTracker.Clear();
            var restored = await db.ItemWarehouseStocks.SingleAsync(x => x.ItemId == item.Id);
            Assert.Equal(10m, restored.Quantity); Assert.Equal(77, restored.Revision);
            Assert.Equal(10m, (await db.Items.SingleAsync(x => x.Id == item.Id)).CurrentStock);
            var untouched = await db.ItemWarehouseStocks.SingleAsync(x => x.ItemId == unrelated.Id);
            Assert.Equal(17m, untouched.Quantity); Assert.Equal(88, untouched.Revision);
            Assert.False((await db.Items.SingleAsync(x => x.Id == unrelated.Id)).IsDirty);
            Assert.False(await db.Settings.AnyAsync(x => x.Key == $"Inventory.DeletedItemBaseline.{item.Id:D}"));
        }
        finally
        {
            SqliteConnection.ClearAllPools(); Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            Environment.SetEnvironmentVariable("GEORAEPLAN_DISABLE_LEGACY_MERGE", previousLegacyMerge);
            Environment.SetEnvironmentVariable("GEORAEPLAN_DISABLE_SERVER_SYNC", previousServerSync);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ItemDeletionAndRestore_FailureRollsBackBaselineAndItem(bool failRestore)
    {
        var previousLegacyMerge = Environment.GetEnvironmentVariable("GEORAEPLAN_DISABLE_LEGACY_MERGE");
        var previousServerSync = Environment.GetEnvironmentVariable("GEORAEPLAN_DISABLE_SERVER_SYNC");
        PrepareIsolatedAppRoot("item-baseline-failure");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync(); await db.Database.EnsureCreatedAsync();
            var local = CreateLocalService(db, out var session, out _);
            var item = CreateItem(Guid.NewGuid()); item.CurrentStock = 10m;
            db.Items.Add(item);
            db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock { ItemId = item.Id, WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse, Quantity = 10m, Revision = 77 });
            await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            if (failRestore) Assert.True((await local.DeleteItemAsync(item.Id, session)).Success);
            var triggerSql = failRestore
                ? "CREATE TRIGGER fail_stock BEFORE INSERT ON ItemWarehouseStocks BEGIN SELECT RAISE(ABORT, 'injected_stock_failure'); END;"
                : "CREATE TRIGGER fail_stock BEFORE DELETE ON ItemWarehouseStocks BEGIN SELECT RAISE(ABORT, 'injected_stock_failure'); END;";
            await db.Database.ExecuteSqlRawAsync(triggerSql);
            var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                if (failRestore) await local.RestoreItemAsync(item.Id, session);
                else await local.DeleteItemAsync(item.Id, session);
            });
            Assert.Contains("injected_stock_failure", error.ToString());
            db.ChangeTracker.Clear();
            var preserved = await db.Items.IgnoreQueryFilters().SingleAsync(x => x.Id == item.Id);
            Assert.Equal(failRestore, preserved.IsDeleted);
            Assert.Equal(failRestore ? 0m : 10m, preserved.CurrentStock);
            Assert.Equal(failRestore, await db.Settings.AnyAsync(x => x.Key == $"Inventory.DeletedItemBaseline.{item.Id:D}"));
            Assert.Equal(failRestore ? 0 : 1, await db.ItemWarehouseStocks.CountAsync(x => x.ItemId == item.Id));
        }
        finally
        {
            SqliteConnection.ClearAllPools(); Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            Environment.SetEnvironmentVariable("GEORAEPLAN_DISABLE_LEGACY_MERGE", previousLegacyMerge);
            Environment.SetEnvironmentVariable("GEORAEPLAN_DISABLE_SERVER_SYNC", previousServerSync);
        }
    }
}
