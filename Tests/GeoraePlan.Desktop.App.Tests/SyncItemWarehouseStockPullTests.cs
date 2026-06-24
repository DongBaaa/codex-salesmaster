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
}
