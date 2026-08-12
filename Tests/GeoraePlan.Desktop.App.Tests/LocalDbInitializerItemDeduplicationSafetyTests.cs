using System.Data;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class LocalDbInitializerItemDeduplicationSafetyTests
{
    [Fact]
    public async Task NormalizeCaseVariantItemIdsAsync_PreopenedEfConnection_RemainsUsableBySameContext()
    {
        PrepareAppRoot("georaeplan-initializer-item-case-connection");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var canonical = CreateExactDuplicateItem("a1111111-1111-1111-1111-111111111111");
            var caseVariant = CreateExactDuplicateItem("b2222222-2222-2222-2222-222222222222");
            canonical.Revision = 10;
            caseVariant.Revision = 1;
            db.Items.AddRange(canonical, caseVariant);
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync(
                """
                UPDATE "Items"
                SET "Id" = 'A1111111-1111-1111-1111-111111111111'
                WHERE "Id" = 'A1111111-1111-1111-1111-111111111111' COLLATE NOCASE;

                UPDATE "Items"
                SET "Id" = 'a1111111-1111-1111-1111-111111111111'
                WHERE "Id" = 'B2222222-2222-2222-2222-222222222222' COLLATE NOCASE;
                """);
            db.ChangeTracker.Clear();

            await db.Database.OpenConnectionAsync();
            Assert.Equal(ConnectionState.Open, db.Database.GetDbConnection().State);

            await InvokeNormalizeCaseVariantItemIdsAsync(db);

            Assert.Equal(ConnectionState.Open, db.Database.GetDbConnection().State);
            Assert.Single(await db.Items.IgnoreQueryFilters().AsNoTracking()
                .Where(item => item.Id == canonical.Id)
                .ToListAsync());

            const string settingKey = "Test.LocalDbInitializer.CaseConnectionOwnership";
            var setting = new LocalSetting { Key = settingKey, Value = "written" };
            db.Settings.Add(setting);
            await db.SaveChangesAsync();
            setting.Value = "updated";
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            Assert.Equal("updated", await db.Settings.AsNoTracking()
                .Where(current => current.Key == settingKey)
                .Select(current => current.Value)
                .SingleAsync());
            Assert.Equal(ConnectionState.Open, db.Database.GetDbConnection().State);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task InitializeAsync_RetainsDuplicateItemsWithPendingItemOutbox()
    {
        PrepareAppRoot("georaeplan-initializer-item-dedup-startup-outbox");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var canonical = CreateExactDuplicateItem("80111111-1111-1111-1111-111111111111");
            var duplicate = CreateExactDuplicateItem("80222222-2222-2222-2222-222222222222");
            db.Items.AddRange(canonical, duplicate);
            db.SyncOutboxEntries.Add(new LocalSyncOutboxEntry
            {
                MutationId = "initializer-item-dedup-startup-pending",
                EntityName = nameof(ItemDto),
                EntityId = duplicate.Id,
                Status = "Prepared"
            });
            await db.SaveChangesAsync();

            await LocalDbInitializer.InitializeAsync(db);
            db.ChangeTracker.Clear();

            Assert.Equal(2, await db.Items.IgnoreQueryFilters()
                .AsNoTracking()
                .CountAsync(item => item.Id == canonical.Id || item.Id == duplicate.Id));
            Assert.Equal(duplicate.Id, (await db.SyncOutboxEntries.AsNoTracking()
                .SingleAsync(entry => entry.MutationId == "initializer-item-dedup-startup-pending")).EntityId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData("dirty")]
    [InlineData("catalog-pending")]
    [InlineData("current-stock")]
    [InlineData("warehouse-stock")]
    [InlineData("price-grade")]
    [InlineData("outbox")]
    [InlineData("asset-semantic-conflict")]
    [InlineData("blank-vs-filled")]
    [InlineData("inventory-movement")]
    [InlineData("stock-layer")]
    [InlineData("serial-ledger")]
    public async Task MergeDuplicateItemsAsync_RetainsGroupWhenAutomaticMergeSafetyBlockExists(string blocker)
    {
        PrepareAppRoot($"georaeplan-initializer-item-dedup-{blocker}");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var canonical = CreateExactDuplicateItem("81111111-1111-1111-1111-111111111111");
            var duplicate = CreateExactDuplicateItem("82222222-2222-2222-2222-222222222222");
            db.Items.AddRange(canonical, duplicate);

            switch (blocker)
            {
                case "dirty":
                    duplicate.IsDirty = true;
                    break;
                case "catalog-pending":
                    duplicate.CatalogExtensionSyncPending = true;
                    break;
                case "current-stock":
                    canonical.CurrentStock = 2m;
                    duplicate.CurrentStock = 2m;
                    break;
                case "warehouse-stock":
                    db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock
                    {
                        ItemId = duplicate.Id,
                        WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                        Quantity = 3m
                    });
                    break;
                case "price-grade":
                    db.ItemPriceGrades.Add(new LocalItemPriceGrade
                    {
                        ItemId = duplicate.Id,
                        PriceGradeOptionId = Guid.Parse("83333333-3333-3333-3333-333333333333"),
                        PriceGradeName = "VIP",
                        UnitPrice = 1234m,
                        IsActive = true,
                        IsDirty = false
                    });
                    break;
                case "outbox":
                    db.SyncOutboxEntries.Add(new LocalSyncOutboxEntry
                    {
                        MutationId = "initializer-item-dedup-pending",
                        EntityName = nameof(LocalItem).ToLowerInvariant(),
                        EntityId = duplicate.Id,
                        Status = "Prepared"
                    });
                    break;
                case "asset-semantic-conflict":
                    canonical.SimpleMemo = RentalStateService.AutoCreatedRentalItemMemo;
                    duplicate.SimpleMemo = RentalStateService.AutoCreatedRentalItemMemo;
                    canonical.Notes = "설치 자산 A";
                    duplicate.Notes = "설치 자산 B";
                    break;
                case "blank-vs-filled":
                    duplicate.CategoryName = "정보가 있는 분류";
                    break;
                case "inventory-movement":
                case "stock-layer":
                case "serial-ledger":
                    AddDerivedItemReference(db, blocker, duplicate.Id);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown blocker: {blocker}");
            }

            await db.SaveChangesAsync();
            await InvokeMergeDuplicateItemsAsync(db);
            await db.SaveChangesAsync();

            var storedItems = await db.Items.IgnoreQueryFilters()
                .AsNoTracking()
                .Where(item => item.Id == canonical.Id || item.Id == duplicate.Id)
                .ToListAsync();
            Assert.Equal(2, storedItems.Count);
            Assert.All(storedItems, item => Assert.False(item.IsDeleted));

            if (blocker == "warehouse-stock")
                Assert.Equal(3m, (await db.ItemWarehouseStocks.AsNoTracking().SingleAsync()).Quantity);
            if (blocker == "price-grade")
                Assert.Equal(duplicate.Id, (await db.ItemPriceGrades.IgnoreQueryFilters().AsNoTracking().SingleAsync()).ItemId);
            if (blocker == "outbox")
                Assert.Equal(duplicate.Id, (await db.SyncOutboxEntries.AsNoTracking().SingleAsync()).EntityId);
            if (blocker is "inventory-movement" or "stock-layer" or "serial-ledger")
                Assert.Equal(duplicate.Id, await GetDerivedItemReferenceIdAsync(db, blocker));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData("inventory-movement")]
    [InlineData("stock-layer")]
    [InlineData("serial-ledger")]
    public async Task MergeDuplicateItemsAsync_RetainsGroupWhenCanonicalHasLocalDerivedReference(string blocker)
    {
        PrepareAppRoot($"georaeplan-initializer-item-dedup-canonical-{blocker}");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var canonical = CreateExactDuplicateItem("83111111-1111-1111-1111-111111111111");
            var duplicate = CreateExactDuplicateItem("83222222-2222-2222-2222-222222222222");
            db.Items.AddRange(canonical, duplicate);
            AddDerivedItemReference(db, blocker, canonical.Id);
            await db.SaveChangesAsync();

            await InvokeMergeDuplicateItemsAsync(db);
            await db.SaveChangesAsync();

            Assert.Equal(2, await db.Items.IgnoreQueryFilters().AsNoTracking()
                .CountAsync(item => item.Id == canonical.Id || item.Id == duplicate.Id));
            Assert.Equal(canonical.Id, await GetDerivedItemReferenceIdAsync(db, blocker));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task InitializeAsync_RetainsSafeExactDuplicateGroupWithoutAutomaticHardDelete()
    {
        PrepareAppRoot("georaeplan-initializer-item-dedup-safe");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var canonical = CreateExactDuplicateItem("84111111-1111-1111-1111-111111111111");
            var duplicate = CreateExactDuplicateItem("85222222-2222-2222-2222-222222222222");
            canonical.UpdatedAtUtc = new DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc);
            duplicate.UpdatedAtUtc = canonical.UpdatedAtUtc.AddMinutes(-1);
            db.Items.AddRange(canonical, duplicate);
            await db.SaveChangesAsync();

            await LocalDbInitializer.InitializeAsync(db);
            db.ChangeTracker.Clear();

            var storedItems = await db.Items.IgnoreQueryFilters().AsNoTracking()
                .Where(item => item.Id == canonical.Id || item.Id == duplicate.Id)
                .OrderBy(item => item.Id)
                .ToListAsync();
            Assert.Equal(2, storedItems.Count);
            Assert.All(storedItems, item =>
            {
                Assert.False(item.IsDeleted);
                Assert.False(item.IsDirty);
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData("invoice-line", false)]
    [InlineData("invoice-line", true)]
    [InlineData("inventory-transfer-line", false)]
    [InlineData("inventory-transfer-line", true)]
    public async Task MergeDuplicateItemsAsync_RetainsCanonicalAndDuplicateSyncedReferencesWithoutStartupRemap(
        string referenceKind,
        bool referenceDuplicate)
    {
        PrepareAppRoot($"georaeplan-initializer-item-dedup-{referenceKind}-{referenceDuplicate}");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var canonical = CreateExactDuplicateItem("86111111-1111-1111-1111-111111111111");
            var duplicate = CreateExactDuplicateItem("86222222-2222-2222-2222-222222222222");
            canonical.UpdatedAtUtc = new DateTime(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc);
            duplicate.UpdatedAtUtc = canonical.UpdatedAtUtc.AddMinutes(-1);
            var invoice = new LocalInvoice
            {
                Id = Guid.Parse("86333333-3333-3333-3333-333333333333"),
                CustomerId = Guid.Parse("86444444-4444-4444-4444-444444444444"),
                InvoiceNumber = "DEDUP-ROOT-1",
                IsDirty = false
            };
            var transfer = new LocalInventoryTransfer
            {
                Id = Guid.Parse("86555555-5555-5555-5555-555555555555"),
                TransferNumber = "DEDUP-TRANSFER-1",
                IsDirty = false
            };

            db.Items.AddRange(canonical, duplicate);
            var referencedItemId = referenceDuplicate ? duplicate.Id : canonical.Id;
            if (referenceKind == "invoice-line")
            {
                db.Invoices.Add(invoice);
                db.InvoiceLines.Add(new LocalInvoiceLine
                {
                    Id = Guid.NewGuid(),
                    InvoiceId = invoice.Id,
                    ItemId = referencedItemId
                });
                if (referenceDuplicate)
                {
                    db.InvoiceLines.AddRange(
                        new LocalInvoiceLine { Id = Guid.NewGuid(), InvoiceId = invoice.Id, ItemId = canonical.Id },
                        new LocalInvoiceLine { Id = Guid.NewGuid(), InvoiceId = invoice.Id, ItemId = canonical.Id });
                }
            }
            else if (referenceKind == "inventory-transfer-line")
            {
                db.InventoryTransfers.Add(transfer);
                db.InventoryTransferLines.Add(new LocalInventoryTransferLine
                {
                    Id = Guid.NewGuid(),
                    TransferId = transfer.Id,
                    ItemId = referencedItemId
                });
                if (referenceDuplicate)
                {
                    db.InventoryTransferLines.AddRange(
                        new LocalInventoryTransferLine { Id = Guid.NewGuid(), TransferId = transfer.Id, ItemId = canonical.Id },
                        new LocalInventoryTransferLine { Id = Guid.NewGuid(), TransferId = transfer.Id, ItemId = canonical.Id });
                }
            }
            else
            {
                throw new InvalidOperationException($"Unknown reference kind: {referenceKind}");
            }
            await db.SaveChangesAsync();

            await InvokeMergeDuplicateItemsAsync(db);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var storedItems = await db.Items.IgnoreQueryFilters().AsNoTracking()
                .Where(item => item.Id == canonical.Id || item.Id == duplicate.Id)
                .ToListAsync();
            Assert.Equal(2, storedItems.Count);
            Assert.All(storedItems, item => Assert.False(item.IsDeleted));

            var storedReferenceIds = referenceKind == "invoice-line"
                ? await db.InvoiceLines.IgnoreQueryFilters().AsNoTracking().Select(line => line.ItemId).ToListAsync()
                : await db.InventoryTransferLines.IgnoreQueryFilters().AsNoTracking().Select(line => line.ItemId).ToListAsync();
            if (referenceDuplicate)
                Assert.Contains(duplicate.Id, storedReferenceIds);
            else
                Assert.All(storedReferenceIds, itemId => Assert.Equal(canonical.Id, itemId));
            if (referenceKind == "invoice-line")
                Assert.False((await db.Invoices.IgnoreQueryFilters().AsNoTracking().SingleAsync()).IsDirty);
            else
                Assert.False((await db.InventoryTransfers.IgnoreQueryFilters().AsNoTracking().SingleAsync()).IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private static void AddDerivedItemReference(LocalDbContext db, string blocker, Guid itemId)
    {
        switch (blocker)
        {
            case "inventory-movement":
                db.InventoryMovements.Add(new LocalInventoryMovement
                {
                    ItemId = itemId,
                    WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                    QuantityDelta = 0m
                });
                break;
            case "stock-layer":
                db.StockLayers.Add(new LocalStockLayer
                {
                    ItemId = itemId,
                    WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                    OriginalQuantity = 0m,
                    RemainingQuantity = 0m
                });
                break;
            case "serial-ledger":
                db.SerialLedgers.Add(new LocalSerialLedger
                {
                    ItemId = itemId,
                    WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                    SerialNumber = $"DEDUP-{Guid.NewGuid():N}"
                });
                break;
            default:
                throw new InvalidOperationException($"Unknown derived blocker: {blocker}");
        }
    }

    private static async Task<Guid?> GetDerivedItemReferenceIdAsync(LocalDbContext db, string blocker)
        => blocker switch
        {
            "inventory-movement" => (await db.InventoryMovements.AsNoTracking().SingleAsync()).ItemId,
            "stock-layer" => (await db.StockLayers.AsNoTracking().SingleAsync()).ItemId,
            "serial-ledger" => (await db.SerialLedgers.AsNoTracking().SingleAsync()).ItemId,
            _ => throw new InvalidOperationException($"Unknown derived blocker: {blocker}")
        };

    private static LocalItem CreateExactDuplicateItem(string id)
        => new()
        {
            Id = Guid.Parse(id),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "초기화 안전 중복 품목",
            NameMatchKey = "초기화 안전 중복 품목",
            SpecificationOriginal = "A4",
            SpecificationMatchKey = "A4",
            ItemKind = ItemKinds.Product,
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 0m,
            CatalogExtensionSyncPending = false,
            IsDirty = false
        };

    private static async Task InvokeMergeDuplicateItemsAsync(LocalDbContext db)
    {
        var method = typeof(LocalDbInitializer).GetMethod(
            "MergeDuplicateItemsAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var task = method!.Invoke(null, [db]) as Task;
        Assert.NotNull(task);
        await task!;
    }

    private static async Task InvokeNormalizeCaseVariantItemIdsAsync(LocalDbContext db)
    {
        var method = typeof(LocalDbInitializer).GetMethod(
            "NormalizeCaseVariantItemIdsAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var task = method!.Invoke(null, [db]) as Task;
        Assert.NotNull(task);
        await task!;
    }

    private static void PrepareAppRoot(string prefix)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);
    }
}
