using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed partial class SyncOutboxPendingStateTests
{
    [Theory]
    [InlineData("add")]
    [InlineData("change")]
    [InlineData("remove")]
    [InlineData("unchanged")]
    [InlineData("rollback")]
    public async Task WarehouseOnlyPull_NotifiesCommittedChangesWithoutDirtyingMaster(string mode)
    {
        PrepareAppRoot("warehouse-pull-notification");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureCreatedAsync();
            var now = new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc);
            var id = Guid.NewGuid();
            db.Items.Add(new LocalItem
            {
                Id = id, NameOriginal = "Warehouse notification fixture", NameMatchKey = "WAREHOUSEFIXTURE",
                TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
                ItemKind = ItemKinds.Product, TrackingType = ItemTrackingTypes.Stock,
                CurrentStock = mode == "add" ? 0m : 10m, Revision = 1, IsDirty = false,
                CreatedAtUtc = now, UpdatedAtUtc = now
            });
            if (mode != "add")
                db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock
                {
                    ItemId = id, WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                    Quantity = 10m, Revision = 1, UpdatedAtUtc = now
                });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var expected = mode == "remove" ? 0m : mode == "unchanged" ? 10m : 12m;
            var notifier = new DesktopDataChangeNotifier();
            var observedStocks = new List<decimal>();
            notifier.InventoryStateChanged += (_, _) =>
            {
                // Read a separate connection: a notification must expose committed data.
                using var observer = new LocalDbContext();
                observedStocks.Add(observer.Items.AsNoTracking().Single(x => x.Id == id).CurrentStock);
            };
            using var sync = CreateSyncService(db, CreateAdminSession(), handler: null, notifier: notifier);
            sync.AfterPulledPurgeRecordsAsyncForTesting = _ =>
            {
                Assert.Empty(observedStocks);
                if (mode == "rollback") throw new InvalidOperationException("fixture rollback");
                return Task.CompletedTask;
            };
            var response = new SyncPullResponse
            {
                CurrentServerRevision = 2,
                ItemWarehouseStocks = mode == "remove" ? [] :
                [new ItemWarehouseStockDto
                {
                    ItemId = id, WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                    Quantity = expected, Revision = mode == "unchanged" ? 1 : 2,
                    UpdatedAtUtc = now
                }]
            };
            if (mode == "rollback")
                await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeApplyPullAsync(sync, response));
            else
                await InvokeApplyPullAsync(sync, response);

            if (mode is "unchanged" or "rollback") Assert.Empty(observedStocks);
            else Assert.Equal(new[] { expected }, observedStocks);
            await using var verification = new LocalDbContext();
            var item = await verification.Items.AsNoTracking().SingleAsync(x => x.Id == id);
            Assert.Equal(mode == "rollback" ? 10m : expected, item.CurrentStock);
            Assert.False(item.IsDirty);
            Assert.Equal(1, item.Revision);
            Assert.Equal(OfficeCodeCatalog.Usenet, item.OfficeCode);
            Assert.Empty(await verification.SyncOutboxEntries.ToListAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }
}
