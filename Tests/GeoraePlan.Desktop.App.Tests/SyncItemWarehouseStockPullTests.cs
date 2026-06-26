using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class SyncItemWarehouseStockPullTests
{
    [Fact]
    public async Task SyncPullInventoryTransferReceipt_RequeriesTransferAndRecalculatesItemCurrentStock()
    {
        PrepareAppRoot("georaeplan-sync-transfer-receipt-pull");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var now = DateTime.UtcNow;
            var itemId = Guid.Parse("81700000-0000-0000-0000-000000000001");
            var transferId = Guid.Parse("81700000-0000-0000-0000-000000000002");
            var lineId = Guid.Parse("81700000-0000-0000-0000-000000000003");

            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "재고이동 pull 수령 품목",
                NameMatchKey = "재고이동pull수령품목",
                TrackingType = ItemTrackingTypes.Stock,
                Unit = "EA",
                CurrentStock = 3m,
                CreatedAtUtc = now.AddHours(-2),
                UpdatedAtUtc = now.AddHours(-2),
                Revision = 10,
                IsDirty = false
            });
            db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode = DomainConstants.WarehouseUsenetMain,
                Quantity = 3m,
                UpdatedAtUtc = now.AddHours(-1),
                Revision = 10
            });
            db.InventoryTransfers.Add(new LocalInventoryTransfer
            {
                Id = transferId,
                TransferNumber = "TR-PULL-RECEIPT",
                TransferDate = new DateOnly(2026, 6, 27),
                FromWarehouseCode = DomainConstants.WarehouseUsenetMain,
                ToWarehouseCode = DomainConstants.WarehouseYeonsuMain,
                TransferStatus = InventoryTransferStatusNormalizer.Pending,
                CreatedByUsername = "usenet",
                RequestedByUsername = "usenet",
                RequestedAtUtc = now.AddHours(-1),
                CreatedAtUtc = now.AddHours(-1),
                UpdatedAtUtc = now.AddHours(-1),
                Revision = 10,
                IsDirty = false,
                Lines =
                {
                    new LocalInventoryTransferLine
                    {
                        Id = lineId,
                        TransferId = transferId,
                        ItemId = itemId,
                        ItemNameOriginal = "재고이동 pull 수령 품목",
                        Unit = "EA",
                        Quantity = 2m,
                        ReceivedQuantity = 2m
                    }
                }
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            using var sync = CreateSyncService(db, session);
            await InvokeApplyPullAsync(
                sync,
                new SyncPullResponse
                {
                    CurrentServerRevision = 50,
                    ItemWarehouseStocks =
                    {
                        new ItemWarehouseStockDto
                        {
                            ItemId = itemId,
                            WarehouseCode = DomainConstants.WarehouseUsenetMain,
                            Quantity = 3m,
                            UpdatedAtUtc = now.AddMinutes(10),
                            Revision = 49
                        },
                        new ItemWarehouseStockDto
                        {
                            ItemId = itemId,
                            WarehouseCode = DomainConstants.WarehouseYeonsuMain,
                            Quantity = 2m,
                            UpdatedAtUtc = now.AddMinutes(10),
                            Revision = 50
                        }
                    },
                    InventoryTransfers =
                    {
                        new InventoryTransferDto
                        {
                            Id = transferId,
                            TenantCode = TenantScopeCatalog.UsenetGroup,
                            SourceOfficeCode = OfficeCodeCatalog.Usenet,
                            TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
                            TransferNumber = "TR-PULL-RECEIPT",
                            TransferDate = new DateOnly(2026, 6, 27),
                            FromWarehouseCode = DomainConstants.WarehouseUsenetMain,
                            ToWarehouseCode = DomainConstants.WarehouseYeonsuMain,
                            TransferStatus = InventoryTransferStatusNormalizer.Received,
                            CreatedByUsername = "usenet",
                            RequestedByUsername = "usenet",
                            RequestedAtUtc = now.AddHours(-1),
                            ReceivedByUsername = "yeonsu",
                            ReceivedAtUtc = now.AddMinutes(9),
                            ReceiveMemo = "서버 수령확정",
                            LastStatusChangedByUsername = "yeonsu",
                            LastStatusChangedAtUtc = now.AddMinutes(9),
                            CreatedAtUtc = now.AddHours(-1),
                            UpdatedAtUtc = now.AddMinutes(10),
                            Revision = 50,
                            Lines =
                            {
                                new InventoryTransferLineDto
                                {
                                    Id = lineId,
                                    TransferId = transferId,
                                    ItemId = itemId,
                                    ItemNameOriginal = "재고이동 pull 수령 품목",
                                    Unit = "EA",
                                    Quantity = 2m,
                                    ReceivedQuantity = 2m,
                                    QuantityDifference = 0m,
                                    ReceiptRemark = "검수 완료"
                                }
                            }
                        }
                    }
                });

            db.ChangeTracker.Clear();

            var item = await db.Items.AsNoTracking().SingleAsync(current => current.Id == itemId);
            Assert.Equal(5m, item.CurrentStock);
            Assert.False(item.IsDirty);

            var stocks = await db.ItemWarehouseStocks
                .AsNoTracking()
                .Where(stock => stock.ItemId == itemId)
                .OrderBy(stock => stock.WarehouseCode)
                .ToListAsync();
            Assert.Collection(
                stocks,
                stock =>
                {
                    Assert.Equal(DomainConstants.WarehouseUsenetMain, stock.WarehouseCode);
                    Assert.Equal(3m, stock.Quantity);
                    Assert.Equal(49, stock.Revision);
                },
                stock =>
                {
                    Assert.Equal(DomainConstants.WarehouseYeonsuMain, stock.WarehouseCode);
                    Assert.Equal(2m, stock.Quantity);
                    Assert.Equal(50, stock.Revision);
                });

            var transfer = await db.InventoryTransfers
                .AsNoTracking()
                .Include(current => current.Lines)
                .SingleAsync(current => current.Id == transferId);
            var line = Assert.Single(transfer.Lines);
            Assert.Equal(InventoryTransferStatusNormalizer.Received, transfer.TransferStatus);
            Assert.Equal("서버 수령확정", transfer.ReceiveMemo);
            Assert.Equal("yeonsu", transfer.ReceivedByUsername);
            Assert.False(transfer.IsDirty);
            Assert.Equal(50, transfer.Revision);
            Assert.Equal(2m, line.ReceivedQuantity);
            Assert.Equal(0m, line.QuantityDifference);
            Assert.Equal("검수 완료", line.ReceiptRemark);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncPullInventoryTransferRejection_RestoresSourceStockAndRecalculatesCurrentStock()
    {
        PrepareAppRoot("georaeplan-sync-transfer-reject-pull");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var now = DateTime.UtcNow;
            var itemId = Guid.Parse("81800000-0000-0000-0000-000000000001");
            var transferId = Guid.Parse("81800000-0000-0000-0000-000000000002");
            var lineId = Guid.Parse("81800000-0000-0000-0000-000000000003");

            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "재고이동 pull 반려 품목",
                NameMatchKey = "재고이동pull반려품목",
                TrackingType = ItemTrackingTypes.Stock,
                Unit = "EA",
                CurrentStock = 3m,
                CreatedAtUtc = now.AddHours(-2),
                UpdatedAtUtc = now.AddHours(-2),
                Revision = 10,
                IsDirty = false
            });
            db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode = DomainConstants.WarehouseUsenetMain,
                Quantity = 3m,
                UpdatedAtUtc = now.AddHours(-1),
                Revision = 10
            });
            db.InventoryTransfers.Add(new LocalInventoryTransfer
            {
                Id = transferId,
                TransferNumber = "TR-PULL-REJECT",
                TransferDate = new DateOnly(2026, 6, 27),
                FromWarehouseCode = DomainConstants.WarehouseUsenetMain,
                ToWarehouseCode = DomainConstants.WarehouseYeonsuMain,
                TransferStatus = InventoryTransferStatusNormalizer.Pending,
                CreatedByUsername = "usenet",
                RequestedByUsername = "usenet",
                RequestedAtUtc = now.AddHours(-1),
                CreatedAtUtc = now.AddHours(-1),
                UpdatedAtUtc = now.AddHours(-1),
                Revision = 10,
                IsDirty = false,
                Lines =
                {
                    new LocalInventoryTransferLine
                    {
                        Id = lineId,
                        TransferId = transferId,
                        ItemId = itemId,
                        ItemNameOriginal = "재고이동 pull 반려 품목",
                        Unit = "EA",
                        Quantity = 2m
                    }
                }
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            using var sync = CreateSyncService(db, session);
            await InvokeApplyPullAsync(
                sync,
                new SyncPullResponse
                {
                    CurrentServerRevision = 60,
                    ItemWarehouseStocks =
                    {
                        new ItemWarehouseStockDto
                        {
                            ItemId = itemId,
                            WarehouseCode = DomainConstants.WarehouseUsenetMain,
                            Quantity = 5m,
                            UpdatedAtUtc = now.AddMinutes(10),
                            Revision = 60
                        }
                    },
                    InventoryTransfers =
                    {
                        new InventoryTransferDto
                        {
                            Id = transferId,
                            TenantCode = TenantScopeCatalog.UsenetGroup,
                            SourceOfficeCode = OfficeCodeCatalog.Usenet,
                            TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
                            TransferNumber = "TR-PULL-REJECT",
                            TransferDate = new DateOnly(2026, 6, 27),
                            FromWarehouseCode = DomainConstants.WarehouseUsenetMain,
                            ToWarehouseCode = DomainConstants.WarehouseYeonsuMain,
                            TransferStatus = InventoryTransferStatusNormalizer.Rejected,
                            CreatedByUsername = "usenet",
                            RequestedByUsername = "usenet",
                            RequestedAtUtc = now.AddHours(-1),
                            RejectedByUsername = "yeonsu",
                            RejectedAtUtc = now.AddMinutes(9),
                            RejectReason = "서버 검수 반려",
                            LastStatusChangedByUsername = "yeonsu",
                            LastStatusChangedAtUtc = now.AddMinutes(9),
                            CreatedAtUtc = now.AddHours(-1),
                            UpdatedAtUtc = now.AddMinutes(10),
                            Revision = 60,
                            Lines =
                            {
                                new InventoryTransferLineDto
                                {
                                    Id = lineId,
                                    TransferId = transferId,
                                    ItemId = itemId,
                                    ItemNameOriginal = "재고이동 pull 반려 품목",
                                    Unit = "EA",
                                    Quantity = 2m
                                }
                            }
                        }
                    }
                });

            db.ChangeTracker.Clear();

            var item = await db.Items.AsNoTracking().SingleAsync(current => current.Id == itemId);
            Assert.Equal(5m, item.CurrentStock);
            Assert.False(item.IsDirty);

            var stock = await db.ItemWarehouseStocks
                .AsNoTracking()
                .SingleAsync(current => current.ItemId == itemId && current.WarehouseCode == DomainConstants.WarehouseUsenetMain);
            Assert.Equal(5m, stock.Quantity);
            Assert.Equal(60, stock.Revision);
            Assert.False(await db.ItemWarehouseStocks
                .AnyAsync(current => current.ItemId == itemId && current.WarehouseCode == DomainConstants.WarehouseYeonsuMain));

            var transfer = await db.InventoryTransfers
                .AsNoTracking()
                .Include(current => current.Lines)
                .SingleAsync(current => current.Id == transferId);
            var line = Assert.Single(transfer.Lines);
            Assert.Equal(InventoryTransferStatusNormalizer.Rejected, transfer.TransferStatus);
            Assert.Equal("서버 검수 반려", transfer.RejectReason);
            Assert.Equal("yeonsu", transfer.RejectedByUsername);
            Assert.False(transfer.IsDirty);
            Assert.Equal(60, transfer.Revision);
            Assert.Equal(2m, line.Quantity);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncPullItemWarehouseStocks_RemovesServerMissingRowsAndPreservesDirtyItemRows()
    {
        PrepareAppRoot("georaeplan-sync-stock-pull-replace");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var itemId = Guid.NewGuid();
            var dirtyItemId = Guid.NewGuid();
            var now = DateTime.UtcNow;

            db.Items.AddRange(
                new LocalItem
                {
                    Id = itemId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    NameOriginal = "재고 pull 정리 품목",
                    NameMatchKey = "재고 pull 정리 품목",
                    CurrentStock = 7m,
                    CreatedAtUtc = now.AddHours(-2),
                    UpdatedAtUtc = now.AddHours(-2),
                    Revision = 10,
                    IsDirty = false
                },
                new LocalItem
                {
                    Id = dirtyItemId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    NameOriginal = "재고 pull dirty 보존 품목",
                    NameMatchKey = "재고 pull dirty 보존 품목",
                    CurrentStock = 3m,
                    CreatedAtUtc = now.AddHours(-2),
                    UpdatedAtUtc = now,
                    Revision = 11,
                    IsDirty = true
                });
            db.ItemWarehouseStocks.AddRange(
                new LocalItemWarehouseStock
                {
                    ItemId = itemId,
                    WarehouseCode = DomainConstants.WarehouseUsenetMain,
                    Quantity = 5m,
                    UpdatedAtUtc = now.AddHours(-1),
                    Revision = 10
                },
                new LocalItemWarehouseStock
                {
                    ItemId = itemId,
                    WarehouseCode = DomainConstants.WarehouseYeonsuMain,
                    Quantity = 2m,
                    UpdatedAtUtc = now.AddHours(-1),
                    Revision = 9
                },
                new LocalItemWarehouseStock
                {
                    ItemId = dirtyItemId,
                    WarehouseCode = DomainConstants.WarehouseUsenetMain,
                    Quantity = 3m,
                    UpdatedAtUtc = now,
                    Revision = 11
                });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            using var sync = CreateSyncService(db, session);
            await InvokeApplyPullAsync(
                sync,
                new SyncPullResponse
                {
                    CurrentServerRevision = 30,
                    ItemWarehouseStocks =
                    {
                        new ItemWarehouseStockDto
                        {
                            ItemId = itemId,
                            WarehouseCode = DomainConstants.WarehouseUsenetMain,
                            Quantity = 4m,
                            UpdatedAtUtc = now.AddMinutes(1),
                            Revision = 30
                        }
                    }
                });

            db.ChangeTracker.Clear();

            var cleanItemStocks = await db.ItemWarehouseStocks
                .AsNoTracking()
                .Where(stock => stock.ItemId == itemId)
                .OrderBy(stock => stock.WarehouseCode)
                .ToListAsync();
            var stock = Assert.Single(cleanItemStocks);
            Assert.Equal(DomainConstants.WarehouseUsenetMain, stock.WarehouseCode);
            Assert.Equal(4m, stock.Quantity);
            Assert.Equal(30, stock.Revision);

            var dirtyItemStock = await db.ItemWarehouseStocks
                .AsNoTracking()
                .SingleAsync(stock => stock.ItemId == dirtyItemId);
            Assert.Equal(3m, dirtyItemStock.Quantity);
            Assert.Equal(11, dirtyItemStock.Revision);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncPullItemWarehouseStocks_DiscardsNonInventoryRowsAndZerosSnapshots()
    {
        PrepareAppRoot("georaeplan-sync-stock-pull-noninventory-prune");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var assetItemId = Guid.Parse("81900000-0000-0000-0000-000000000001");
            var stockItemId = Guid.Parse("81900000-0000-0000-0000-000000000002");
            var now = DateTime.UtcNow;

            db.Items.AddRange(
                new LocalItem
                {
                    Id = assetItemId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    NameOriginal = "렌탈 장비 pull 오염 품목",
                    NameMatchKey = "렌탈장비pull오염품목",
                    ItemKind = ItemKinds.Asset,
                    TrackingType = ItemTrackingTypes.Asset,
                    IsRental = true,
                    IsSale = false,
                    CurrentStock = 8m,
                    SafetyStock = 2m,
                    CreatedAtUtc = now.AddHours(-2),
                    UpdatedAtUtc = now.AddHours(-2),
                    Revision = 10,
                    IsDirty = false
                },
                new LocalItem
                {
                    Id = stockItemId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    NameOriginal = "일반 재고 pull 품목",
                    NameMatchKey = "일반재고pull품목",
                    ItemKind = ItemKinds.Product,
                    TrackingType = ItemTrackingTypes.Stock,
                    IsRental = false,
                    IsSale = true,
                    CurrentStock = 3m,
                    SafetyStock = 1m,
                    CreatedAtUtc = now.AddHours(-2),
                    UpdatedAtUtc = now.AddHours(-2),
                    Revision = 10,
                    IsDirty = false
                });
            db.ItemWarehouseStocks.AddRange(
                new LocalItemWarehouseStock
                {
                    ItemId = assetItemId,
                    WarehouseCode = DomainConstants.WarehouseUsenetMain,
                    Quantity = 8m,
                    UpdatedAtUtc = now.AddHours(-1),
                    Revision = 10
                },
                new LocalItemWarehouseStock
                {
                    ItemId = stockItemId,
                    WarehouseCode = DomainConstants.WarehouseUsenetMain,
                    Quantity = 3m,
                    UpdatedAtUtc = now.AddHours(-1),
                    Revision = 10
                });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            using var sync = CreateSyncService(db, session);
            await InvokeApplyPullAsync(
                sync,
                new SyncPullResponse
                {
                    CurrentServerRevision = 70,
                    ItemWarehouseStocks =
                    {
                        new ItemWarehouseStockDto
                        {
                            ItemId = assetItemId,
                            WarehouseCode = DomainConstants.WarehouseUsenetMain,
                            Quantity = 9m,
                            UpdatedAtUtc = now.AddMinutes(1),
                            Revision = 70
                        },
                        new ItemWarehouseStockDto
                        {
                            ItemId = stockItemId,
                            WarehouseCode = DomainConstants.WarehouseUsenetMain,
                            Quantity = 5m,
                            UpdatedAtUtc = now.AddMinutes(1),
                            Revision = 70
                        }
                    }
                });

            db.ChangeTracker.Clear();

            Assert.False(await db.ItemWarehouseStocks.AnyAsync(stock => stock.ItemId == assetItemId));
            var assetItem = await db.Items.AsNoTracking().SingleAsync(item => item.Id == assetItemId);
            Assert.Equal(0m, assetItem.CurrentStock);
            Assert.Equal(0m, assetItem.SafetyStock);
            Assert.False(assetItem.IsDirty);

            var stockItem = await db.Items.AsNoTracking().SingleAsync(item => item.Id == stockItemId);
            Assert.Equal(5m, stockItem.CurrentStock);
            Assert.Equal(1m, stockItem.SafetyStock);
            var stock = await db.ItemWarehouseStocks
                .AsNoTracking()
                .SingleAsync(current => current.ItemId == stockItemId);
            Assert.Equal(5m, stock.Quantity);
            Assert.Equal(70, stock.Revision);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncPushItemWarehouseStocks_PrunesAndExcludesNonInventoryRows()
    {
        PrepareAppRoot("georaeplan-sync-stock-push-noninventory-prune");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var assetItemId = Guid.Parse("81910000-0000-0000-0000-000000000001");
            var stockItemId = Guid.Parse("81910000-0000-0000-0000-000000000002");
            var now = DateTime.UtcNow;

            db.Items.AddRange(
                new LocalItem
                {
                    Id = assetItemId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    NameOriginal = "push 제외 렌탈 장비",
                    NameMatchKey = "push제외렌탈장비",
                    ItemKind = ItemKinds.Asset,
                    TrackingType = ItemTrackingTypes.Asset,
                    IsRental = true,
                    IsSale = false,
                    CurrentStock = 7m,
                    SafetyStock = 3m,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                },
                new LocalItem
                {
                    Id = stockItemId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    NameOriginal = "push 허용 일반 재고",
                    NameMatchKey = "push허용일반재고",
                    ItemKind = ItemKinds.Product,
                    TrackingType = ItemTrackingTypes.Stock,
                    IsRental = false,
                    IsSale = true,
                    CurrentStock = 4m,
                    SafetyStock = 1m,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
            db.ItemWarehouseStocks.AddRange(
                new LocalItemWarehouseStock
                {
                    ItemId = assetItemId,
                    WarehouseCode = DomainConstants.WarehouseUsenetMain,
                    Quantity = 7m,
                    UpdatedAtUtc = now
                },
                new LocalItemWarehouseStock
                {
                    ItemId = stockItemId,
                    WarehouseCode = DomainConstants.WarehouseUsenetMain,
                    Quantity = 4m,
                    UpdatedAtUtc = now
                });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            using var sync = CreateSyncService(db, session);
            var removedCount = await InvokePruneNonInventoryItemWarehouseStocksAsync(sync);
            var pushStocks = await InvokeLoadInventoryTrackedItemWarehouseStocksForPushAsync(sync);

            Assert.Equal(1, removedCount);
            Assert.DoesNotContain(pushStocks, stock => stock.ItemId == assetItemId);
            var pushStock = Assert.Single(pushStocks);
            Assert.Equal(stockItemId, pushStock.ItemId);
            Assert.Equal(4m, pushStock.Quantity);
            Assert.False(await db.ItemWarehouseStocks.AnyAsync(stock => stock.ItemId == assetItemId));

            var assetItem = await db.Items.AsNoTracking().SingleAsync(item => item.Id == assetItemId);
            Assert.Equal(0m, assetItem.CurrentStock);
            Assert.Equal(0m, assetItem.SafetyStock);
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
        session.SetSession(
            "test-token",
            new UserSessionDto
            {
                UserId = Guid.NewGuid(),
                Username = "sync-stock-admin",
                Role = "Admin",
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeAdmin
            },
            DateTime.UtcNow.AddDays(1));
        return session;
    }

    private static void PrepareAppRoot(string prefix)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);
    }

    private static SyncService CreateSyncService(LocalDbContext db, SessionState session)
    {
        var dispatcher = new SyncRequestDispatcher();
        var local = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
        var rental = new RentalStateService(db, local);
        var diagnostics = new SyncDiagnosticsService(session);
        var api = new ErpApiClient(new HttpClient { BaseAddress = new Uri("http://localhost/") }, session);
        return new SyncService(db, local, rental, api, session, dispatcher, diagnostics);
    }

    private static Task InvokeApplyPullAsync(SyncService sync, SyncPullResponse pull)
    {
        var method = typeof(SyncService).GetMethod("ApplyPullAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(SyncService), "ApplyPullAsync");
        return (Task)method.Invoke(
            sync,
            [
                pull,
                0L,
                CancellationToken.None,
                false
            ])!;
    }

    private static async Task<int> InvokePruneNonInventoryItemWarehouseStocksAsync(SyncService sync)
    {
        var method = typeof(SyncService).GetMethod("PruneNonInventoryItemWarehouseStocksAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(SyncService), "PruneNonInventoryItemWarehouseStocksAsync");
        return await (Task<int>)method.Invoke(sync, [CancellationToken.None])!;
    }

    private static async Task<List<LocalItemWarehouseStock>> InvokeLoadInventoryTrackedItemWarehouseStocksForPushAsync(SyncService sync)
    {
        var method = typeof(SyncService).GetMethod("LoadInventoryTrackedItemWarehouseStocksForPushAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(SyncService), "LoadInventoryTrackedItemWarehouseStocksForPushAsync");
        return await (Task<List<LocalItemWarehouseStock>>)method.Invoke(sync, [CancellationToken.None])!;
    }
}
