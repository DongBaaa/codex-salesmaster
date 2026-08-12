using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App;
using 거래플랜.Desktop.App.Data;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class MultiPcInventoryTransferFixtureSelectionTests
{
    [Fact]
    public void PendingReceiptValues_AcceptCurrentAndLegacyNeutralDrafts()
    {
        var lines = new[]
        {
            new LocalInventoryTransferLine
            {
                Quantity = 1m,
                ReceivedQuantity = 1m,
                QuantityDifference = 0m,
                ReceiptRemark = string.Empty
            },
            new LocalInventoryTransferLine
            {
                Quantity = 1m,
                ReceivedQuantity = null,
                QuantityDifference = null,
                ReceiptRemark = string.Empty
            }
        };

        Assert.All(
            lines,
            line => Assert.True(
                MainWindow.HasNeutralMultiPcPendingReceiptValues(line)));
    }

    [Fact]
    public void PendingReceiptValues_RejectMissingOrNonNeutralReceiptDraft()
    {
        var invalidLines = new[]
        {
            new LocalInventoryTransferLine
            {
                Quantity = 1m,
                ReceivedQuantity = 0.5m,
                QuantityDifference = -0.5m,
                ReceiptRemark = string.Empty
            },
            new LocalInventoryTransferLine
            {
                Quantity = 1m,
                ReceivedQuantity = 1m,
                QuantityDifference = 0.5m,
                ReceiptRemark = string.Empty
            },
            new LocalInventoryTransferLine
            {
                Quantity = 1m,
                ReceivedQuantity = 1m,
                QuantityDifference = 0m,
                ReceiptRemark = "received"
            }
        };

        Assert.All(
            invalidLines,
            line => Assert.False(
                MainWindow.HasNeutralMultiPcPendingReceiptValues(line)));
    }

    [Fact]
    public void PendingTransferAggregate_RequiresExactSourceOnlyQuantityDelta()
    {
        Assert.True(
            MainWindow.HasExpectedMultiPcPendingTransferAggregate(
                sourceQuantityBefore: 5m,
                destinationQuantityBefore: 2m,
                sourceQuantityAfter: 4m,
                destinationQuantityAfter: 2m,
                transferQuantity: 1m));

        Assert.False(
            MainWindow.HasExpectedMultiPcPendingTransferAggregate(
                sourceQuantityBefore: 5m,
                destinationQuantityBefore: 2m,
                sourceQuantityAfter: 3m,
                destinationQuantityAfter: 2m,
                transferQuantity: 1m));
        Assert.False(
            MainWindow.HasExpectedMultiPcPendingTransferAggregate(
                sourceQuantityBefore: 5m,
                destinationQuantityBefore: 2m,
                sourceQuantityAfter: 4m,
                destinationQuantityAfter: 3m,
                transferQuantity: 1m));
        Assert.False(
            MainWindow.HasExpectedMultiPcPendingTransferAggregate(
                sourceQuantityBefore: 5m,
                destinationQuantityBefore: 2m,
                sourceQuantityAfter: 5m,
                destinationQuantityAfter: 2m,
                transferQuantity: 0m));
    }

    [Fact]
    public async Task UnitLayerEligibility_UsesWholePositiveLayersWithinRequestedWarehouseAndItem()
    {
        using var appRoot = new LocalAppRootScope(
            "georaeplan-multipc-inventory-transfer-fixture-selection");
        await using var db = CreateDbContext(appRoot.DbPath);
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        const string requestedWarehouse = "WH-REQUESTED";
        const string otherWarehouse = "WH-OTHER";
        var noLayerItemId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var exactUnitItemId = Guid.Parse("10000000-0000-0000-0000-000000000002");
        var multipleWholeLayersItemId = Guid.Parse("10000000-0000-0000-0000-000000000003");
        var subUnitLayerItemId = Guid.Parse("10000000-0000-0000-0000-000000000004");
        var nonPositiveLayersItemId = Guid.Parse("10000000-0000-0000-0000-000000000005");
        var crossWarehouseItemId = Guid.Parse("10000000-0000-0000-0000-000000000006");
        var otherWarehouseOnlyItemId = Guid.Parse("10000000-0000-0000-0000-000000000007");
        var unrelatedSubUnitItemId = Guid.Parse("10000000-0000-0000-0000-000000000008");

        db.StockLayers.AddRange(
            Layer(exactUnitItemId, requestedWarehouse, 1m),
            Layer(multipleWholeLayersItemId, requestedWarehouse, 1m),
            Layer(multipleWholeLayersItemId, requestedWarehouse, 1.25m),
            Layer(subUnitLayerItemId, requestedWarehouse, 0.999999999999m),
            Layer(subUnitLayerItemId, requestedWarehouse, 2m),
            Layer(nonPositiveLayersItemId, requestedWarehouse, 1m),
            Layer(nonPositiveLayersItemId, requestedWarehouse, 0m),
            Layer(nonPositiveLayersItemId, requestedWarehouse, -1m),
            Layer(crossWarehouseItemId, requestedWarehouse, 1m),
            Layer(crossWarehouseItemId, otherWarehouse, 0.5m),
            Layer(otherWarehouseOnlyItemId, otherWarehouse, 2m),
            Layer(unrelatedSubUnitItemId, requestedWarehouse, 0.5m),
            Layer(itemId: null, requestedWarehouse, 10m));
        await db.SaveChangesAsync();

        var eligibleItemIds =
            await MainWindow.GetMultiPcInventoryTransferUnitLayerEligibleItemIdsAsync(
                db,
                $"  {requestedWarehouse}  ",
                CancellationToken.None);

        Assert.True(
            eligibleItemIds.SetEquals(
            [
                exactUnitItemId,
                multipleWholeLayersItemId,
                nonPositiveLayersItemId,
                crossWarehouseItemId
            ]),
            "Eligible fixture item IDs did not match the whole-positive-layer contract.");
        Assert.DoesNotContain(noLayerItemId, eligibleItemIds);
        Assert.DoesNotContain(subUnitLayerItemId, eligibleItemIds);
        Assert.DoesNotContain(otherWarehouseOnlyItemId, eligibleItemIds);
        Assert.DoesNotContain(unrelatedSubUnitItemId, eligibleItemIds);
    }

    private static LocalStockLayer Layer(
        Guid? itemId,
        string warehouseCode,
        decimal remainingQuantity)
        => new()
        {
            ItemId = itemId,
            WarehouseCode = warehouseCode,
            OriginalQuantity = remainingQuantity,
            RemainingQuantity = remainingQuantity,
            UnitCost = 1m,
            ReceiptDate = new DateOnly(2026, 8, 1),
            CreatedAtUtc = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
        };

    private static LocalDbContext CreateDbContext(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite($"Data Source={dbPath};Pooling=False")
            .Options;
        return new LocalDbContext(options);
    }

    private sealed class LocalAppRootScope : IDisposable
    {
        private readonly string? _previousAppRoot;
        private readonly string _appRoot;

        public LocalAppRootScope(string prefix)
        {
            _previousAppRoot = Environment.GetEnvironmentVariable("GEORAEPLAN_APP_ROOT");
            _appRoot = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_appRoot);
            DbPath = Path.Combine(_appRoot, "georaeplan-test.db");
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", _appRoot);
        }

        public string DbPath { get; }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", _previousAppRoot);
            SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(_appRoot))
                    Directory.Delete(_appRoot, recursive: true);
            }
            catch
            {
                // Temp cleanup failures must not hide the test assertion result.
            }
        }
    }
}
