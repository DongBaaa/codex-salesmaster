using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Server.Api.Utilities;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class InventoryTransferScopeGuardTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public InventoryTransferScopeGuardTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using var dbContext = CreateDbContext(CreateAdminUser());
        dbContext.Database.EnsureCreated();
    }

    [Fact]
    public async Task Push_RejectsNewInventoryTransfer_WhenOnlyTargetOfficeIsWritable()
    {
        var itemId = Guid.Parse("d1111111-1111-1111-1111-111111111111");
        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            seedDb.Items.Add(CreateStockItem(itemId, "Target-only create transfer item", currentStock: 10m));
            seedDb.ItemWarehouseStocks.Add(new ItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 10m,
                UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-5),
                Revision = 10
            });
            await seedDb.SaveChangesAsync();
        }

        var targetUser = CreateDeliveryUser("yeonsu-target-transfer-create", OfficeCodeCatalog.Yeonsu);
        await using var scopedDb = CreateDbContext(targetUser);
        var controller = CreateController(scopedDb, targetUser);
        var transferId = Guid.Parse("d2222222-2222-2222-2222-222222222222");

        var response = await controller.Push(new SyncPushRequest
        {
            DeviceId = "target-only-transfer-create",
            InventoryTransfers =
            [
                new InventoryTransferDto
                {
                    Id = transferId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    SourceOfficeCode = OfficeCodeCatalog.Usenet,
                    TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
                    TransferNumber = "TR-TARGET-CREATE-DENIED",
                    TransferDate = new DateOnly(2026, 6, 24),
                    FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                    ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                    TransferStatus = InventoryTransferStatusNormalizer.Pending,
                    CreatedByUsername = targetUser.Username,
                    RequestedByUsername = targetUser.Username,
                    RequestedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow,
                    MutationId = $"target-only-transfer-create:InventoryTransfer:{transferId:N}:1",
                    MutationCreatedAtUtc = DateTime.UtcNow,
                    Lines =
                    [
                        new InventoryTransferLineDto
                        {
                            Id = Guid.Parse("d3333333-3333-3333-3333-333333333333"),
                            TransferId = transferId,
                            ItemId = itemId,
                            ItemNameOriginal = "Target-only create transfer item",
                            Unit = "EA",
                            Quantity = 2m
                        }
                    ]
                }
            ]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            string.Equals(conflict.EntityName, nameof(InventoryTransfer), StringComparison.Ordinal) &&
            conflict.Reason.Contains("source office", StringComparison.OrdinalIgnoreCase));
        scopedDb.ChangeTracker.Clear();
        Assert.False(await scopedDb.InventoryTransfers.IgnoreQueryFilters().AnyAsync(transfer => transfer.Id == transferId));
        Assert.False(await scopedDb.InventoryLedgerEntries.AnyAsync(entry => entry.SourceDocumentId == transferId));
        Assert.Equal(10m, await scopedDb.ItemWarehouseStocks
            .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
    }

    [Fact]
    public async Task Push_AllowsTargetOfficeToConfirmExistingInventoryTransfer_WhenRequestedLinesAreUnchanged()
    {
        var itemId = Guid.Parse("e1111111-1111-1111-1111-111111111111");
        var transferId = Guid.Parse("e2222222-2222-2222-2222-222222222222");
        var lineId = Guid.Parse("e3333333-3333-3333-3333-333333333333");
        await SeedPendingTransferAsync(itemId, transferId, lineId, "Target receipt unchanged item", sourceStockQuantity: 8m);
        const string existingEvidencePath = "inventory-transfers/server-owned-evidence.pdf";
        await using (var preparationDb = CreateDbContext(CreateAdminUser()))
        {
            await preparationDb.InventoryTransfers
                .IgnoreQueryFilters()
                .Where(transfer => transfer.Id == transferId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(transfer => transfer.ReceiveEvidencePath, existingEvidencePath));
            preparationDb.InventoryTransferLines.Add(new InventoryTransferLine
            {
                Id = Guid.NewGuid(),
                TransferId = transferId,
                ItemNameOriginal = "historical deleted line",
                Unit = "EA",
                Quantity = 1m,
                IsDeleted = true
            });
            await preparationDb.SaveChangesAsync();
        }

        var targetUser = CreateDeliveryUser("yeonsu-target-transfer-receive", OfficeCodeCatalog.Yeonsu);
        await using var scopedDb = CreateDbContext(targetUser);
        var controller = CreateController(scopedDb, targetUser);
        var existing = await scopedDb.InventoryTransfers
            .IgnoreQueryFilters()
            .Include(transfer => transfer.Lines)
            .SingleAsync(transfer => transfer.Id == transferId);

        var response = await controller.Push(new SyncPushRequest
        {
            DeviceId = "target-transfer-receive",
            InventoryTransfers =
            [
                BuildReceiptDto(existing, targetUser.Username, requestedQuantity: 2m)
            ]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.Equal(0, result.ConflictCount);
        Assert.Equal(1, result.AcceptedCount);
        scopedDb.ChangeTracker.Clear();
        var stored = await scopedDb.InventoryTransfers.IgnoreQueryFilters()
            .Include(transfer => transfer.Lines)
            .SingleAsync(transfer => transfer.Id == transferId);
        Assert.Equal(InventoryTransferStatusNormalizer.Received, stored.TransferStatus);
        Assert.Equal("usenet-source", stored.CreatedByUsername);
        Assert.Equal(existingEvidencePath, stored.ReceiveEvidencePath);
        Assert.Equal(targetUser.Username, stored.ReceivedByUsername);
        Assert.Equal(targetUser.Username, stored.LastSavedByUsername);
        Assert.Equal(targetUser.Username, stored.LastStatusChangedByUsername);
        Assert.Equal(stored.ReceivedAtUtc, stored.LastSavedAtUtc);
        Assert.Equal(stored.ReceivedAtUtc, stored.LastStatusChangedAtUtc);
        Assert.Contains(stored.Lines, line => line.IsDeleted && line.ItemNameOriginal == "historical deleted line");
        Assert.Equal(8m, await scopedDb.ItemWarehouseStocks
            .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
        Assert.Equal(2m, await scopedDb.ItemWarehouseStocks
            .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.YeonsuMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
    }

    [Fact]
    public async Task Push_SourceEditThenStaleReceipt_DoesNotCommitDerivedDestinationStockOrReceipt()
    {
        var itemId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        var now = new DateTime(2026, 7, 31, 1, 0, 0, DateTimeKind.Utc);
        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            seedDb.Items.Add(
                CreateStockItem(
                    itemId,
                    "Stale receipt sibling stock item",
                    currentStock: 10m));
            seedDb.ItemWarehouseStocks.Add(new ItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 10m,
                UpdatedAtUtc = now.AddMinutes(-10)
            });
            await seedDb.SaveChangesAsync();
        }

        var sourceUser = CreateDeliveryUser(
            "usenet-stale-receipt-source",
            OfficeCodeCatalog.Usenet);
        await using (var createDb = CreateDbContext(sourceUser))
        {
            var createResult = Assert.IsType<SyncPushResult>(
                Assert.IsType<OkObjectResult>(
                    (await CreateController(createDb, sourceUser)
                        .Push(
                            new SyncPushRequest
                            {
                                DeviceId = "stale-receipt-source-create",
                                InventoryTransfers =
                                [
                                    new InventoryTransferDto
                                    {
                                        Id = transferId,
                                        TenantCode = TenantScopeCatalog.UsenetGroup,
                                        SourceOfficeCode = OfficeCodeCatalog.Usenet,
                                        TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
                                        TransferNumber = $"TR-STALE-{transferId:N}"[..24],
                                        TransferDate = new DateOnly(2026, 7, 31),
                                        FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                                        ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                                        Memo = "v1",
                                        TransferStatus = InventoryTransferStatusNormalizer.Pending,
                                        CreatedByUsername = sourceUser.Username,
                                        RequestedByUsername = sourceUser.Username,
                                        RequestedAtUtc = now,
                                        CreatedAtUtc = now,
                                        UpdatedAtUtc = now,
                                        LastSavedByUsername = sourceUser.Username,
                                        LastSavedAtUtc = now,
                                        MutationId = $"stale-receipt-create:InventoryTransfer:{transferId:N}",
                                        MutationCreatedAtUtc = now,
                                        Lines =
                                        [
                                            new InventoryTransferLineDto
                                            {
                                                Id = lineId,
                                                TransferId = transferId,
                                                ItemId = itemId,
                                                ItemNameOriginal = "Stale receipt sibling stock item",
                                                Unit = "EA",
                                                Quantity = 4m
                                            }
                                        ]
                                    }
                                ]
                            },
                            CancellationToken.None))
                        .Result)
                    .Value);
            Assert.Equal(1, createResult.AcceptedCount);
            Assert.Equal(0, createResult.ConflictCount);
        }

        InventoryTransfer staleTransfer;
        await using (var snapshotDb = CreateDbContext(CreateAdminUser()))
        {
            staleTransfer = await snapshotDb.InventoryTransfers
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Include(transfer => transfer.Lines)
                .SingleAsync(transfer => transfer.Id == transferId);
            Assert.Equal(
                6m,
                await snapshotDb.ItemWarehouseStocks
                    .Where(stock =>
                        stock.ItemId == itemId &&
                        stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
                    .Select(stock => stock.Quantity)
                    .SingleAsync());
            Assert.False(
                await snapshotDb.ItemWarehouseStocks.AnyAsync(stock =>
                    stock.ItemId == itemId &&
                    stock.WarehouseCode == OfficeCodeCatalog.YeonsuMainWarehouse));
        }

        var sourceEdit = staleTransfer.ToDto();
        sourceEdit.ExpectedRevision = staleTransfer.Revision;
        sourceEdit.UpdatedAtUtc = now.AddMinutes(1);
        sourceEdit.Memo = "v2";
        sourceEdit.LastSavedByUsername = sourceUser.Username;
        sourceEdit.LastSavedAtUtc = now.AddMinutes(1);
        sourceEdit.MutationId =
            $"stale-receipt-source-edit:InventoryTransfer:{transferId:N}";
        sourceEdit.MutationCreatedAtUtc = now.AddMinutes(1);
        await using (var sourceEditDb = CreateDbContext(sourceUser))
        {
            var sourceEditResult = Assert.IsType<SyncPushResult>(
                Assert.IsType<OkObjectResult>(
                    (await CreateController(sourceEditDb, sourceUser)
                        .Push(
                            new SyncPushRequest
                            {
                                DeviceId = "stale-receipt-source-edit",
                                InventoryTransfers = [sourceEdit]
                            },
                            CancellationToken.None))
                        .Result)
                    .Value);
            Assert.Equal(1, sourceEditResult.AcceptedCount);
            Assert.Equal(0, sourceEditResult.ConflictCount);
        }

        var targetUser = CreateInventoryDeliveryAdminUser(
            "yeonsu-stale-receipt-target",
            OfficeCodeCatalog.Yeonsu);
        var staleReceipt = BuildReceiptDto(
            staleTransfer,
            targetUser.Username,
            requestedQuantity: 4m);
        staleReceipt.MutationId =
            $"stale-receipt-target:InventoryTransfer:{transferId:N}";
        var destinationStock = new ItemWarehouseStockDto
        {
            ItemId = itemId,
            WarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            Quantity = 4m,
            UpdatedAtUtc = staleReceipt.ReceivedAtUtc!.Value,
            Revision = 0,
            ExpectedRevision = 0
        };
        await using var targetDb = CreateDbContext(targetUser);
        var targetResult = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(
                (await CreateController(targetDb, targetUser)
                    .Push(
                        new SyncPushRequest
                        {
                            DeviceId = "stale-receipt-target-device",
                            ItemWarehouseStocks = [destinationStock],
                            InventoryTransfers = [staleReceipt]
                        },
                        CancellationToken.None))
                    .Result)
                .Value);

        Assert.Contains(
            targetResult.Conflicts,
            conflict =>
                conflict.EntityName == nameof(InventoryTransfer) &&
                conflict.EntityId == transferId.ToString("D"));
        Assert.DoesNotContain(
            targetResult.AcceptedItemWarehouseStockKeys,
            key =>
                key.ItemId == itemId &&
                key.WarehouseCode == OfficeCodeCatalog.YeonsuMainWarehouse);

        targetDb.ChangeTracker.Clear();
        var storedTransfer = await targetDb.InventoryTransfers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(transfer => transfer.Id == transferId);
        Assert.Equal(InventoryTransferStatusNormalizer.Pending, storedTransfer.TransferStatus);
        Assert.Equal("v2", storedTransfer.Memo);
        Assert.False(
            await targetDb.ItemWarehouseStocks.AnyAsync(stock =>
                stock.ItemId == itemId &&
                stock.WarehouseCode == OfficeCodeCatalog.YeonsuMainWarehouse));
        Assert.Equal(
            6m,
            await targetDb.Items
                .IgnoreQueryFilters()
                .Where(item => item.Id == itemId)
                .Select(item => item.CurrentStock)
                .SingleAsync());

        var transferLedger = await targetDb.InventoryLedgerEntries
            .AsNoTracking()
            .Where(entry =>
                entry.SourceDocumentId == transferId &&
                entry.SourceLineId == lineId)
            .ToListAsync();
        var outboundLedger = Assert.Single(
            transferLedger,
            entry => entry.SourceType == "InventoryTransfer:Out");
        Assert.Equal(-4m, outboundLedger.QuantityDelta);
        Assert.DoesNotContain(
            transferLedger,
            entry => entry.SourceType == "InventoryTransfer:In");
        Assert.DoesNotContain(
            await targetDb.ProcessedSyncMutations.AsNoTracking().ToListAsync(),
            receipt =>
                receipt.EntityName == nameof(ItemWarehouseStock) &&
                receipt.EntityId ==
                $"{itemId:D}|{OfficeCodeCatalog.YeonsuMainWarehouse}");
    }

    [Fact]
    public async Task Push_ClientHandledTransferStock_DoesNotDoubleCountShortage()
    {
        var itemId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        var now = new DateTime(2026, 7, 31, 2, 0, 0, DateTimeKind.Utc);
        long sourceStockRevision;
        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            seedDb.Items.Add(
                CreateStockItem(
                    itemId,
                    "Client-handled transfer stock item",
                    currentStock: 10m));
            var sourceStock = new ItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 10m,
                UpdatedAtUtc = now.AddMinutes(-5)
            };
            seedDb.ItemWarehouseStocks.Add(sourceStock);
            await seedDb.SaveChangesAsync();
            sourceStockRevision = sourceStock.Revision;
        }

        var admin = CreateInventoryDeliveryAdminUser(
            "admin-client-handled-transfer",
            OfficeCodeCatalog.Usenet);
        await using var dbContext = CreateDbContext(admin);
        var response = await CreateController(dbContext, admin)
            .Push(
                new SyncPushRequest
                {
                    DeviceId = "client-handled-transfer-device",
                    ItemWarehouseStocks =
                    [
                        new ItemWarehouseStockDto
                        {
                            ItemId = itemId,
                            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                            Quantity = 4m,
                            UpdatedAtUtc = now,
                            Revision = sourceStockRevision,
                            ExpectedRevision = sourceStockRevision
                        }
                    ],
                    InventoryTransfers =
                    [
                        new InventoryTransferDto
                        {
                            Id = transferId,
                            TenantCode = TenantScopeCatalog.UsenetGroup,
                            SourceOfficeCode = OfficeCodeCatalog.Usenet,
                            TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
                            TransferNumber = $"TR-CLIENT-{transferId:N}"[..24],
                            TransferDate = new DateOnly(2026, 7, 31),
                            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                            TransferStatus = InventoryTransferStatusNormalizer.Pending,
                            CreatedByUsername = admin.Username,
                            RequestedByUsername = admin.Username,
                            RequestedAtUtc = now,
                            CreatedAtUtc = now,
                            UpdatedAtUtc = now,
                            LastSavedByUsername = admin.Username,
                            LastSavedAtUtc = now,
                            MutationId =
                                $"client-handled-transfer:InventoryTransfer:{transferId:N}",
                            MutationCreatedAtUtc = now,
                            Lines =
                            [
                                new InventoryTransferLineDto
                                {
                                    Id = lineId,
                                    TransferId = transferId,
                                    ItemId = itemId,
                                    ItemNameOriginal = "Client-handled transfer stock item",
                                    Unit = "EA",
                                    Quantity = 6m
                                }
                            ]
                        }
                    ]
                },
                CancellationToken.None);

        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(1, result.AcceptedCount);
        Assert.Equal(0, result.ConflictCount);
        Assert.Contains(
            result.AcceptedItemWarehouseStockKeys,
            key =>
                key.ItemId == itemId &&
                key.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse);

        dbContext.ChangeTracker.Clear();
        var transfer = await dbContext.InventoryTransfers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == transferId);
        Assert.Equal(InventoryTransferStatusNormalizer.Pending, transfer.TransferStatus);
        Assert.Equal(
            4m,
            await dbContext.ItemWarehouseStocks
                .Where(stock =>
                    stock.ItemId == itemId &&
                    stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => stock.Quantity)
                .SingleAsync());
        Assert.Equal(
            4m,
            await dbContext.Items
                .IgnoreQueryFilters()
                .Where(item => item.Id == itemId)
                .Select(item => item.CurrentStock)
                .SingleAsync());
        var outboundLedger = Assert.Single(
            await dbContext.InventoryLedgerEntries
                .AsNoTracking()
                .Where(entry =>
                    entry.SourceDocumentId == transferId &&
                    entry.SourceLineId == lineId &&
                    entry.SourceType == "InventoryTransfer:Out")
                .ToListAsync());
        Assert.Equal(-6m, outboundLedger.QuantityDelta);
        Assert.DoesNotContain(
            await dbContext.InventoryLedgerEntries
                .AsNoTracking()
                .Where(entry => entry.SourceDocumentId == transferId)
                .ToListAsync(),
            entry => entry.SourceType == "InventoryTransfer:In");
    }

    [Fact]
    public async Task Push_MixedClientHandledTransferStock_WhenUnhandledLineHasShortage_DoesNotCommitSiblingStock()
    {
        var handledItemId = Guid.NewGuid();
        var shortageItemId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        var handledLineId = Guid.NewGuid();
        var shortageLineId = Guid.NewGuid();
        var now = new DateTime(2026, 7, 31, 2, 15, 0, DateTimeKind.Utc);
        long handledStockRevision;
        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            seedDb.Items.AddRange(
                CreateStockItem(
                    handledItemId,
                    "Mixed transfer handled item",
                    currentStock: 10m),
                CreateStockItem(
                    shortageItemId,
                    "Mixed transfer shortage item",
                    currentStock: 2m));
            var handledSourceStock = new ItemWarehouseStock
            {
                ItemId = handledItemId,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 10m,
                UpdatedAtUtc = now.AddMinutes(-5)
            };
            seedDb.ItemWarehouseStocks.AddRange(
                handledSourceStock,
                new ItemWarehouseStock
                {
                    ItemId = shortageItemId,
                    WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                    Quantity = 2m,
                    UpdatedAtUtc = now.AddMinutes(-5)
                });
            await seedDb.SaveChangesAsync();
            handledStockRevision = handledSourceStock.Revision;
        }

        var admin = CreateInventoryDeliveryAdminUser(
            "admin-mixed-client-handled-transfer",
            OfficeCodeCatalog.Usenet);
        const string deviceId = "mixed-client-handled-transfer-device";
        await using var dbContext = CreateDbContext(admin);
        var response = await CreateController(dbContext, admin)
            .Push(
                new SyncPushRequest
                {
                    DeviceId = deviceId,
                    ItemWarehouseStocks =
                    [
                        new ItemWarehouseStockDto
                        {
                            ItemId = handledItemId,
                            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                            Quantity = 4m,
                            UpdatedAtUtc = now,
                            Revision = handledStockRevision,
                            ExpectedRevision = handledStockRevision
                        }
                    ],
                    InventoryTransfers =
                    [
                        new InventoryTransferDto
                        {
                            Id = transferId,
                            TenantCode = TenantScopeCatalog.UsenetGroup,
                            SourceOfficeCode = OfficeCodeCatalog.Usenet,
                            TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
                            TransferNumber = $"TR-MIXED-{transferId:N}"[..24],
                            TransferDate = new DateOnly(2026, 7, 31),
                            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                            TransferStatus = InventoryTransferStatusNormalizer.Pending,
                            CreatedByUsername = admin.Username,
                            RequestedByUsername = admin.Username,
                            RequestedAtUtc = now,
                            CreatedAtUtc = now,
                            UpdatedAtUtc = now,
                            LastSavedByUsername = admin.Username,
                            LastSavedAtUtc = now,
                            MutationId =
                                $"mixed-client-handled-transfer:InventoryTransfer:{transferId:N}",
                            MutationCreatedAtUtc = now,
                            Lines =
                            [
                                new InventoryTransferLineDto
                                {
                                    Id = handledLineId,
                                    TransferId = transferId,
                                    ItemId = handledItemId,
                                    ItemNameOriginal = "Mixed transfer handled item",
                                    Unit = "EA",
                                    Quantity = 6m
                                },
                                new InventoryTransferLineDto
                                {
                                    Id = shortageLineId,
                                    TransferId = transferId,
                                    ItemId = shortageItemId,
                                    ItemNameOriginal = "Mixed transfer shortage item",
                                    Unit = "EA",
                                    Quantity = 3m
                                }
                            ]
                        }
                    ]
                },
                CancellationToken.None);

        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(0, result.AcceptedCount);
        Assert.Contains(
            result.Conflicts,
            conflict =>
                conflict.EntityName == nameof(InventoryTransfer) &&
                conflict.EntityId == transferId.ToString("D"));
        Assert.DoesNotContain(
            result.AcceptedItemWarehouseStockKeys,
            key =>
                key.ItemId == handledItemId &&
                key.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse);

        dbContext.ChangeTracker.Clear();
        Assert.False(
            await dbContext.InventoryTransfers
                .IgnoreQueryFilters()
                .AnyAsync(transfer => transfer.Id == transferId));
        Assert.False(
            await dbContext.InventoryLedgerEntries
                .AnyAsync(entry => entry.SourceDocumentId == transferId));
        Assert.Equal(
            10m,
            await dbContext.ItemWarehouseStocks
                .Where(stock =>
                    stock.ItemId == handledItemId &&
                    stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => stock.Quantity)
                .SingleAsync());
        Assert.Equal(
            10m,
            await dbContext.Items
                .IgnoreQueryFilters()
                .Where(item => item.Id == handledItemId)
                .Select(item => item.CurrentStock)
                .SingleAsync());
        Assert.DoesNotContain(
            await dbContext.ProcessedSyncMutations
                .AsNoTracking()
                .ToListAsync(),
            receipt =>
                receipt.EntityName == nameof(ItemWarehouseStock) &&
                receipt.EntityId ==
                $"{handledItemId:D}|{OfficeCodeCatalog.UsenetMainWarehouse}");
    }

    [Fact]
    public async Task Push_ClientHandledKey_CannotMaskTransferShortage()
    {
        var itemId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        var now = new DateTime(2026, 7, 31, 2, 30, 0, DateTimeKind.Utc);
        long sourceStockRevision;
        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            seedDb.Items.Add(
                CreateStockItem(
                    itemId,
                    "Client-handled shortage mask item",
                    currentStock: 1m));
            var sourceStock = new ItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 1m,
                UpdatedAtUtc = now.AddMinutes(-5)
            };
            seedDb.ItemWarehouseStocks.Add(sourceStock);
            await seedDb.SaveChangesAsync();
            sourceStockRevision = sourceStock.Revision;
        }

        var admin = CreateInventoryDeliveryAdminUser(
            "admin-client-handled-shortage-mask",
            OfficeCodeCatalog.Usenet);
        await using var dbContext = CreateDbContext(admin);
        var response = await CreateController(dbContext, admin)
            .Push(
                new SyncPushRequest
                {
                    DeviceId = "client-handled-shortage-mask-device",
                    ItemWarehouseStocks =
                    [
                        new ItemWarehouseStockDto
                        {
                            ItemId = itemId,
                            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                            Quantity = 0m,
                            UpdatedAtUtc = now,
                            Revision = sourceStockRevision,
                            ExpectedRevision = sourceStockRevision
                        }
                    ],
                    InventoryTransfers =
                    [
                        new InventoryTransferDto
                        {
                            Id = transferId,
                            TenantCode = TenantScopeCatalog.UsenetGroup,
                            SourceOfficeCode = OfficeCodeCatalog.Usenet,
                            TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
                            TransferNumber = $"TR-MASK-{transferId:N}"[..24],
                            TransferDate = new DateOnly(2026, 7, 31),
                            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                            TransferStatus = InventoryTransferStatusNormalizer.Pending,
                            CreatedByUsername = admin.Username,
                            RequestedByUsername = admin.Username,
                            RequestedAtUtc = now,
                            CreatedAtUtc = now,
                            UpdatedAtUtc = now,
                            LastSavedByUsername = admin.Username,
                            LastSavedAtUtc = now,
                            MutationId =
                                $"client-handled-shortage-mask:InventoryTransfer:{transferId:N}",
                            MutationCreatedAtUtc = now,
                            Lines =
                            [
                                new InventoryTransferLineDto
                                {
                                    Id = lineId,
                                    TransferId = transferId,
                                    ItemId = itemId,
                                    ItemNameOriginal = "Client-handled shortage mask item",
                                    Unit = "EA",
                                    Quantity = 2m
                                }
                            ]
                        }
                    ]
                },
                CancellationToken.None);

        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(0, result.AcceptedCount);
        Assert.Contains(
            result.Conflicts,
            conflict =>
                conflict.EntityName == nameof(InventoryTransfer) &&
                conflict.EntityId == transferId.ToString("D"));
        Assert.DoesNotContain(
            result.AcceptedItemWarehouseStockKeys,
            key =>
                key.ItemId == itemId &&
                key.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse);

        dbContext.ChangeTracker.Clear();
        Assert.False(
            await dbContext.InventoryTransfers
                .IgnoreQueryFilters()
                .AnyAsync(transfer => transfer.Id == transferId));
        Assert.False(
            await dbContext.InventoryLedgerEntries
                .AnyAsync(entry => entry.SourceDocumentId == transferId));
        Assert.Equal(
            1m,
            await dbContext.ItemWarehouseStocks
                .Where(stock =>
                    stock.ItemId == itemId &&
                    stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => stock.Quantity)
                .SingleAsync());
        Assert.DoesNotContain(
            await dbContext.ProcessedSyncMutations
                .AsNoTracking()
                .ToListAsync(),
            receipt =>
                receipt.EntityName == nameof(ItemWarehouseStock) &&
                receipt.EntityId ==
                $"{itemId:D}|{OfficeCodeCatalog.UsenetMainWarehouse}");
    }

    [Fact]
    public async Task Push_TwoInventoryTransfersSharingNewMutationId_DoNotCommitRejectedTransferStock()
    {
        var firstItemId = Guid.NewGuid();
        var secondItemId = Guid.NewGuid();
        var firstTransferId = Guid.NewGuid();
        var secondTransferId = Guid.NewGuid();
        var now = new DateTime(2026, 7, 31, 2, 45, 0, DateTimeKind.Utc);
        var stockRevisions = new Dictionary<Guid, long>();
        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            seedDb.Items.AddRange(
                CreateStockItem(firstItemId, "Duplicate mutation first item", 10m),
                CreateStockItem(secondItemId, "Duplicate mutation second item", 10m));
            var stocks = new[]
            {
                new ItemWarehouseStock
                {
                    ItemId = firstItemId,
                    WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                    Quantity = 10m,
                    UpdatedAtUtc = now.AddMinutes(-5)
                },
                new ItemWarehouseStock
                {
                    ItemId = secondItemId,
                    WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                    Quantity = 10m,
                    UpdatedAtUtc = now.AddMinutes(-5)
                }
            };
            seedDb.ItemWarehouseStocks.AddRange(stocks);
            await seedDb.SaveChangesAsync();
            foreach (var stock in stocks)
                stockRevisions[stock.ItemId] = stock.Revision;
        }

        const string duplicateMutationId =
            "duplicate-transfer-mutation:InventoryTransfer:shared";
        InventoryTransferDto BuildTransfer(
            Guid transferId,
            Guid itemId,
            string itemName)
            => new()
            {
                Id = transferId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                SourceOfficeCode = OfficeCodeCatalog.Usenet,
                TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
                TransferNumber = $"TR-DUP-{transferId:N}"[..24],
                TransferDate = new DateOnly(2026, 7, 31),
                FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                TransferStatus = InventoryTransferStatusNormalizer.Pending,
                CreatedByUsername = "admin-duplicate-transfer-mutation",
                RequestedByUsername = "admin-duplicate-transfer-mutation",
                RequestedAtUtc = now,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                LastSavedByUsername = "admin-duplicate-transfer-mutation",
                LastSavedAtUtc = now,
                MutationId = duplicateMutationId,
                MutationCreatedAtUtc = now,
                Lines =
                [
                    new InventoryTransferLineDto
                    {
                        Id = Guid.NewGuid(),
                        TransferId = transferId,
                        ItemId = itemId,
                        ItemNameOriginal = itemName,
                        Unit = "EA",
                        Quantity = 2m
                    }
                ]
            };

        var admin = CreateInventoryDeliveryAdminUser(
            "admin-duplicate-transfer-mutation",
            OfficeCodeCatalog.Usenet);
        await using var dbContext = CreateDbContext(admin);
        var response = await CreateController(dbContext, admin)
            .Push(
                new SyncPushRequest
                {
                    DeviceId = "duplicate-transfer-mutation-device",
                    ItemWarehouseStocks =
                    [
                        new ItemWarehouseStockDto
                        {
                            ItemId = firstItemId,
                            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                            Quantity = 8m,
                            UpdatedAtUtc = now,
                            Revision = stockRevisions[firstItemId],
                            ExpectedRevision = stockRevisions[firstItemId]
                        },
                        new ItemWarehouseStockDto
                        {
                            ItemId = secondItemId,
                            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                            Quantity = 8m,
                            UpdatedAtUtc = now,
                            Revision = stockRevisions[secondItemId],
                            ExpectedRevision = stockRevisions[secondItemId]
                        }
                    ],
                    InventoryTransfers =
                    [
                        BuildTransfer(
                            firstTransferId,
                            firstItemId,
                            "Duplicate mutation first item"),
                        BuildTransfer(
                            secondTransferId,
                            secondItemId,
                            "Duplicate mutation second item")
                    ]
                },
                CancellationToken.None);

        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(2, result.ConflictCount);
        Assert.Empty(result.AcceptedItemWarehouseStockKeys);

        dbContext.ChangeTracker.Clear();
        Assert.False(
            await dbContext.InventoryTransfers
                .IgnoreQueryFilters()
                .AnyAsync(transfer =>
                    transfer.Id == firstTransferId ||
                    transfer.Id == secondTransferId));
        Assert.False(
            await dbContext.InventoryLedgerEntries
                .AnyAsync(entry =>
                    entry.SourceDocumentId == firstTransferId ||
                    entry.SourceDocumentId == secondTransferId));
        var storedSourceQuantities =
            await dbContext.ItemWarehouseStocks
                .Where(stock =>
                    (stock.ItemId == firstItemId ||
                     stock.ItemId == secondItemId) &&
                    stock.WarehouseCode ==
                    OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => stock.Quantity)
                .ToListAsync();
        Assert.Equal(
            20m,
            storedSourceQuantities.Sum());
        Assert.DoesNotContain(
            await dbContext.ProcessedSyncMutations
                .AsNoTracking()
                .ToListAsync(),
            receipt =>
                receipt.EntityName == nameof(ItemWarehouseStock) &&
                (receipt.EntityId ==
                     $"{firstItemId:D}|{OfficeCodeCatalog.UsenetMainWarehouse}" ||
                receipt.EntityId ==
                     $"{secondItemId:D}|{OfficeCodeCatalog.UsenetMainWarehouse}"));
    }

    [Fact]
    public async Task Push_InventoryTransferAndInvoiceSharingNewMutationId_FailsClosedForEntireCollisionGroup()
    {
        var itemId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        var now = new DateTime(2026, 7, 31, 2, 50, 0, DateTimeKind.Utc);
        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            seedDb.Items.Add(
                CreateStockItem(
                    itemId,
                    "Cross-type mutation collision item",
                    currentStock: 10m));
            seedDb.Customers.Add(new Customer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Cross-type mutation collision customer",
                NameMatchKey = "CROSSTYPEMUTATIONCOLLISIONCUSTOMER",
                TradeType = "Sales"
            });
            seedDb.ItemWarehouseStocks.Add(new ItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 10m,
                UpdatedAtUtc = now.AddMinutes(-5)
            });
            await seedDb.SaveChangesAsync();
        }

        const string sharedMutationId =
            "cross-type-mutation-collision:shared";
        var admin = CreateInventoryDeliveryAdminUser(
            "admin-cross-type-mutation-collision",
            OfficeCodeCatalog.Usenet);
        var invoice = BuildInventoryInvoiceDto(
            invoiceId,
            customerId,
            itemId,
            "Cross-type mutation collision item",
            VoucherType.Sales,
            quantity: 2m,
            username: admin.Username,
            now: now);
        invoice.MutationId = sharedMutationId;
        var transfer = BuildPendingTransferDto(
            transferId,
            itemId,
            "Cross-type mutation collision item",
            quantity: 2m,
            username: admin.Username,
            now: now,
            mutationPrefix: "cross-type-mutation-collision");
        transfer.MutationId = sharedMutationId;

        await using var dbContext = CreateDbContext(admin);
        var response = await CreateController(dbContext, admin)
            .Push(
                new SyncPushRequest
                {
                    DeviceId = "cross-type-mutation-collision-device",
                    Invoices = [invoice],
                    InventoryTransfers = [transfer]
                },
                CancellationToken.None);

        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(2, result.ConflictCount);
        Assert.All(
            result.Conflicts,
            conflict => Assert.Contains(
                "Mutation id is reused",
                conflict.Reason,
                StringComparison.OrdinalIgnoreCase));

        dbContext.ChangeTracker.Clear();
        Assert.False(
            await dbContext.Invoices
                .IgnoreQueryFilters()
                .AnyAsync(current => current.Id == invoiceId));
        Assert.False(
            await dbContext.InventoryTransfers
                .IgnoreQueryFilters()
                .AnyAsync(current => current.Id == transferId));
        Assert.Equal(
            10m,
            await dbContext.ItemWarehouseStocks
                .Where(stock =>
                    stock.ItemId == itemId &&
                    stock.WarehouseCode ==
                    OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => stock.Quantity)
                .SingleAsync());
        Assert.False(
            await dbContext.ProcessedSyncMutations
                .AsNoTracking()
                .AnyAsync(receipt =>
                    receipt.MutationId == sharedMutationId));
    }

    [Fact]
    public async Task Push_AmbiguousInvoices_DoNotCommitMatchingExplicitStockSnapshot()
    {
        var itemId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var now = new DateTime(2026, 7, 31, 2, 51, 0, DateTimeKind.Utc);
        long stockRevision;
        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            seedDb.Items.Add(
                CreateStockItem(
                    itemId,
                    "Ambiguous invoice explicit stock item",
                    currentStock: 10m));
            seedDb.Customers.Add(new Customer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Ambiguous invoice explicit stock customer",
                NameMatchKey = "AMBIGUOUSINVOICEEXPLICITSTOCKCUSTOMER",
                TradeType = "Sales"
            });
            var stock = new ItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 10m,
                UpdatedAtUtc = now.AddMinutes(-5)
            };
            seedDb.ItemWarehouseStocks.Add(stock);
            await seedDb.SaveChangesAsync();
            stockRevision = stock.Revision;
        }

        const string sharedMutationId =
            "ambiguous-invoice-explicit-stock:shared";
        var admin = CreateInventoryDeliveryAdminUser(
            "admin-ambiguous-invoice-explicit-stock",
            OfficeCodeCatalog.Usenet);
        var firstInvoice = BuildInventoryInvoiceDto(
            Guid.NewGuid(),
            customerId,
            itemId,
            "Ambiguous invoice explicit stock item",
            VoucherType.Sales,
            quantity: 2m,
            username: admin.Username,
            now: now);
        var secondInvoice = BuildInventoryInvoiceDto(
            Guid.NewGuid(),
            customerId,
            itemId,
            "Ambiguous invoice explicit stock item",
            VoucherType.Sales,
            quantity: 3m,
            username: admin.Username,
            now: now);
        firstInvoice.MutationId = sharedMutationId;
        secondInvoice.MutationId = sharedMutationId;

        await using var dbContext = CreateDbContext(admin);
        var response = await CreateController(dbContext, admin)
            .Push(
                new SyncPushRequest
                {
                    DeviceId =
                        "ambiguous-invoice-explicit-stock-device",
                    ItemWarehouseStocks =
                    [
                        new ItemWarehouseStockDto
                        {
                            ItemId = itemId,
                            WarehouseCode =
                                OfficeCodeCatalog.UsenetMainWarehouse,
                            Quantity = 8m,
                            UpdatedAtUtc = now,
                            Revision = stockRevision,
                            ExpectedRevision = stockRevision
                        }
                    ],
                    Invoices = [firstInvoice, secondInvoice]
                },
                CancellationToken.None);

        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(2, result.ConflictCount);
        Assert.Empty(result.AcceptedItemWarehouseStockKeys);
        Assert.All(
            result.Conflicts,
            conflict => Assert.Equal(
                nameof(Invoice),
                conflict.EntityName));

        dbContext.ChangeTracker.Clear();
        Assert.Equal(
            10m,
            await dbContext.ItemWarehouseStocks
                .Where(stock =>
                    stock.ItemId == itemId &&
                    stock.WarehouseCode ==
                    OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => stock.Quantity)
                .SingleAsync());
        Assert.False(
            await dbContext.Invoices
                .IgnoreQueryFilters()
                .AnyAsync(invoice =>
                    invoice.Id == firstInvoice.Id ||
                    invoice.Id == secondInvoice.Id));
        Assert.DoesNotContain(
            await dbContext.ProcessedSyncMutations
                .AsNoTracking()
                .ToListAsync(),
            receipt =>
                receipt.EntityName == nameof(ItemWarehouseStock) &&
                receipt.EntityId ==
                $"{itemId:D}|{OfficeCodeCatalog.UsenetMainWarehouse}");
    }

    [Fact]
    public async Task Push_AmbiguousInvoices_DoNotApplyCompleteStockSnapshotMarker()
    {
        var itemId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var now = new DateTime(2026, 7, 31, 2, 52, 0, DateTimeKind.Utc);
        long maxKnownRevision;
        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            seedDb.Items.Add(
                CreateStockItem(
                    itemId,
                    "Ambiguous invoice marker item",
                    currentStock: 16m));
            seedDb.Customers.Add(new Customer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Ambiguous invoice marker customer",
                NameMatchKey = "AMBIGUOUSINVOICEMARKERCUSTOMER",
                TradeType = "Sales"
            });
            var stocks = new[]
            {
                new ItemWarehouseStock
                {
                    ItemId = itemId,
                    WarehouseCode =
                        OfficeCodeCatalog.UsenetMainWarehouse,
                    Quantity = 10m,
                    UpdatedAtUtc = now.AddMinutes(-5)
                },
                new ItemWarehouseStock
                {
                    ItemId = itemId,
                    WarehouseCode =
                        OfficeCodeCatalog.YeonsuMainWarehouse,
                    Quantity = 6m,
                    UpdatedAtUtc = now.AddMinutes(-5)
                }
            };
            seedDb.ItemWarehouseStocks.AddRange(stocks);
            await seedDb.SaveChangesAsync();
            maxKnownRevision = stocks.Max(stock => stock.Revision);
        }

        const string sharedMutationId =
            "ambiguous-invoice-complete-marker:shared";
        var admin = CreateInventoryDeliveryAdminUser(
            "admin-ambiguous-invoice-marker",
            OfficeCodeCatalog.Usenet);
        var firstInvoice = BuildInventoryInvoiceDto(
            Guid.NewGuid(),
            customerId,
            itemId,
            "Ambiguous invoice marker item",
            VoucherType.Sales,
            quantity: 2m,
            username: admin.Username,
            now: now);
        var secondInvoice = BuildInventoryInvoiceDto(
            Guid.NewGuid(),
            customerId,
            itemId,
            "Ambiguous invoice marker item",
            VoucherType.Sales,
            quantity: 3m,
            username: admin.Username,
            now: now);
        firstInvoice.MutationId = sharedMutationId;
        secondInvoice.MutationId = sharedMutationId;

        await using var dbContext = CreateDbContext(admin);
        var response = await CreateController(dbContext, admin)
            .Push(
                new SyncPushRequest
                {
                    DeviceId =
                        "ambiguous-invoice-complete-marker-device",
                    ItemWarehouseStockSnapshotMarkers =
                    [
                        new ItemWarehouseStockSnapshotMarkerDto
                        {
                            ItemId = itemId,
                            MaxKnownRevision = maxKnownRevision
                        }
                    ],
                    Invoices = [firstInvoice, secondInvoice]
                },
                CancellationToken.None);

        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(2, result.ConflictCount);

        dbContext.ChangeTracker.Clear();
        var quantities = await dbContext.ItemWarehouseStocks
            .Where(stock => stock.ItemId == itemId)
            .ToDictionaryAsync(
                stock => stock.WarehouseCode,
                stock => stock.Quantity);
        Assert.Equal(
            10m,
            quantities[OfficeCodeCatalog.UsenetMainWarehouse]);
        Assert.Equal(
            6m,
            quantities[OfficeCodeCatalog.YeonsuMainWarehouse]);
    }

    [Fact]
    public async Task Push_AmbiguousInvoiceDelete_UsesPersistedInvoiceStockScope()
    {
        var itemId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var now = new DateTime(2026, 7, 31, 2, 53, 0, DateTimeKind.Utc);
        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            seedDb.Items.Add(
                CreateStockItem(
                    itemId,
                    "Persisted ambiguous invoice item",
                    currentStock: 10m));
            seedDb.Customers.Add(new Customer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Persisted ambiguous invoice customer",
                NameMatchKey = "PERSISTEDAMBIGUOUSINVOICECUSTOMER",
                TradeType = "Sales"
            });
            seedDb.ItemWarehouseStocks.Add(new ItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode =
                    OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 10m,
                UpdatedAtUtc = now.AddMinutes(-5)
            });
            await seedDb.SaveChangesAsync();
        }

        var admin = CreateInventoryDeliveryAdminUser(
            "admin-persisted-ambiguous-invoice",
            OfficeCodeCatalog.Usenet);
        var originalInvoice = BuildInventoryInvoiceDto(
            invoiceId,
            customerId,
            itemId,
            "Persisted ambiguous invoice item",
            VoucherType.Sales,
            quantity: 2m,
            username: admin.Username,
            now: now);
        await using (var createDb = CreateDbContext(admin))
        {
            var createResponse = await CreateController(createDb, admin)
                .Push(
                    new SyncPushRequest
                    {
                        DeviceId =
                            "persisted-ambiguous-invoice-create-device",
                        Invoices = [originalInvoice]
                    },
                    CancellationToken.None);
            var createResult = Assert.IsType<SyncPushResult>(
                Assert.IsType<OkObjectResult>(
                    createResponse.Result).Value);
            Assert.Equal(1, createResult.AcceptedCount);
            Assert.Equal(0, createResult.ConflictCount);
        }

        long invoiceRevision;
        long stockRevision;
        await using (var readDb = CreateDbContext(admin))
        {
            invoiceRevision = await readDb.Invoices
                .IgnoreQueryFilters()
                .Where(invoice => invoice.Id == invoiceId)
                .Select(invoice => invoice.Revision)
                .SingleAsync();
            stockRevision = await readDb.ItemWarehouseStocks
                .Where(stock =>
                    stock.ItemId == itemId &&
                    stock.WarehouseCode ==
                    OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => stock.Revision)
                .SingleAsync();
        }

        const string sharedMutationId =
            "persisted-ambiguous-invoice-delete:shared";
        var deleteInvoice = BuildInventoryInvoiceDto(
            invoiceId,
            customerId,
            itemId,
            "Persisted ambiguous invoice item",
            VoucherType.Sales,
            quantity: 2m,
            username: admin.Username,
            now: now.AddMinutes(1));
        deleteInvoice.IsDeleted = true;
        deleteInvoice.SourceWarehouseCode =
            OfficeCodeCatalog.YeonsuMainWarehouse;
        deleteInvoice.Lines = [];
        deleteInvoice.Revision = invoiceRevision;
        deleteInvoice.ExpectedRevision = invoiceRevision;
        deleteInvoice.MutationId = sharedMutationId;
        var collidingInvoice = BuildInventoryInvoiceDto(
            Guid.NewGuid(),
            customerId,
            itemId,
            "Persisted ambiguous invoice item",
            VoucherType.Sales,
            quantity: 1m,
            username: admin.Username,
            now: now.AddMinutes(1));
        collidingInvoice.SourceWarehouseCode =
            OfficeCodeCatalog.YeonsuMainWarehouse;
        collidingInvoice.Lines = [];
        collidingInvoice.MutationId = sharedMutationId;

        await using var dbContext = CreateDbContext(admin);
        var response = await CreateController(dbContext, admin)
            .Push(
                new SyncPushRequest
                {
                    DeviceId =
                        "persisted-ambiguous-invoice-delete-device",
                    ItemWarehouseStocks =
                    [
                        new ItemWarehouseStockDto
                        {
                            ItemId = itemId,
                            WarehouseCode =
                                OfficeCodeCatalog.UsenetMainWarehouse,
                            Quantity = 7m,
                            UpdatedAtUtc = now.AddMinutes(1),
                            Revision = stockRevision,
                            ExpectedRevision = stockRevision
                        }
                    ],
                    Invoices = [deleteInvoice, collidingInvoice]
                },
                CancellationToken.None);

        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(2, result.ConflictCount);
        Assert.Empty(result.AcceptedItemWarehouseStockKeys);

        dbContext.ChangeTracker.Clear();
        Assert.Equal(
            8m,
            await dbContext.ItemWarehouseStocks
                .Where(stock =>
                    stock.ItemId == itemId &&
                    stock.WarehouseCode ==
                    OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => stock.Quantity)
                .SingleAsync());
        Assert.False(
            await dbContext.Invoices
                .IgnoreQueryFilters()
                .Where(invoice => invoice.Id == invoiceId)
                .Select(invoice => invoice.IsDeleted)
                .SingleAsync());
    }

    [Fact]
    public async Task Push_LegacyWarehouseAliasMarker_ProjectsInvoiceAndTransferFromCanonicalTotal()
    {
        var itemId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        var now = new DateTime(2026, 7, 31, 2, 54, 0, DateTimeKind.Utc);
        long canonicalStockRevision;
        long maxKnownRevision;
        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            seedDb.Items.Add(
                CreateStockItem(
                    itemId,
                    "Legacy warehouse alias projection item",
                    currentStock: 15m));
            seedDb.Customers.Add(new Customer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Legacy warehouse alias projection customer",
                NameMatchKey = "LEGACYWAREHOUSEALIASPROJECTIONCUSTOMER",
                TradeType = "Sales"
            });
            var canonicalStock = new ItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode =
                    OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 10m,
                UpdatedAtUtc = now.AddMinutes(-5)
            };
            var legacyStock = new ItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode = "usenet",
                Quantity = 5m,
                UpdatedAtUtc = now.AddMinutes(-5)
            };
            seedDb.ItemWarehouseStocks.AddRange(
                canonicalStock,
                legacyStock);
            await seedDb.SaveChangesAsync();
            canonicalStockRevision = canonicalStock.Revision;
            maxKnownRevision = Math.Max(
                canonicalStock.Revision,
                legacyStock.Revision);
        }

        var admin = CreateInventoryDeliveryAdminUser(
            "admin-legacy-warehouse-alias-projection",
            OfficeCodeCatalog.Usenet);
        var invoice = BuildInventoryInvoiceDto(
            invoiceId,
            customerId,
            itemId,
            "Legacy warehouse alias projection item",
            VoucherType.Sales,
            quantity: 3m,
            username: admin.Username,
            now: now);
        var transfer = BuildPendingTransferDto(
            transferId,
            itemId,
            "Legacy warehouse alias projection item",
            quantity: 10m,
            username: admin.Username,
            now: now,
            mutationPrefix: "legacy-warehouse-alias-projection");

        await using var dbContext = CreateDbContext(admin);
        var response = await CreateController(dbContext, admin)
            .Push(
                new SyncPushRequest
                {
                    DeviceId =
                        "legacy-warehouse-alias-projection-device",
                    ItemWarehouseStocks =
                    [
                        new ItemWarehouseStockDto
                        {
                            ItemId = itemId,
                            WarehouseCode =
                                OfficeCodeCatalog.UsenetMainWarehouse,
                            Quantity = 2m,
                            UpdatedAtUtc = now,
                            Revision = canonicalStockRevision,
                            ExpectedRevision = canonicalStockRevision
                        }
                    ],
                    ItemWarehouseStockSnapshotMarkers =
                    [
                        new ItemWarehouseStockSnapshotMarkerDto
                        {
                            ItemId = itemId,
                            MaxKnownRevision = maxKnownRevision
                        }
                    ],
                    Invoices = [invoice],
                    InventoryTransfers = [transfer]
                },
                CancellationToken.None);

        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(2, result.AcceptedCount);
        Assert.Equal(0, result.ConflictCount);
        Assert.Contains(
            result.AcceptedItemWarehouseStockKeys,
            key =>
                key.ItemId == itemId &&
                key.WarehouseCode ==
                OfficeCodeCatalog.UsenetMainWarehouse);

        dbContext.ChangeTracker.Clear();
        var storedStocks = await dbContext.ItemWarehouseStocks
            .Where(stock => stock.ItemId == itemId)
            .ToDictionaryAsync(
                stock => stock.WarehouseCode,
                stock => stock.Quantity);
        Assert.Equal(
            2m,
            storedStocks[OfficeCodeCatalog.UsenetMainWarehouse]);
        Assert.Equal(0m, storedStocks["usenet"]);
        Assert.True(
            await dbContext.Invoices
                .IgnoreQueryFilters()
                .AnyAsync(current => current.Id == invoiceId));
        Assert.True(
            await dbContext.InventoryTransfers
                .IgnoreQueryFilters()
                .AnyAsync(current => current.Id == transferId));
    }

    [Fact]
    public async Task Push_LatestInvoiceDeleteMarkerAndTransfer_ProjectsPromotedVersionDelta()
    {
        var itemId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var firstInvoiceId = Guid.NewGuid();
        var versionGroupId = firstInvoiceId;
        var latestInvoiceId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        var now = new DateTime(2026, 7, 31, 2, 56, 0, DateTimeKind.Utc);
        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            seedDb.Items.Add(
                CreateStockItem(
                    itemId,
                    "Latest delete marker transfer item",
                    currentStock: 10m));
            seedDb.Customers.Add(new Customer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Latest delete marker transfer customer",
                NameMatchKey = "LATESTDELETEMARKERTRANSFERCUSTOMER",
                TradeType = "Sales"
            });
            seedDb.ItemWarehouseStocks.Add(new ItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode =
                    OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 10m,
                UpdatedAtUtc = now.AddMinutes(-5)
            });
            await seedDb.SaveChangesAsync();
        }

        var admin = CreateInventoryDeliveryAdminUser(
            "admin-latest-delete-marker-transfer",
            OfficeCodeCatalog.Usenet);
        var firstVersion = BuildInventoryInvoiceDto(
            firstInvoiceId,
            customerId,
            itemId,
            "Latest delete marker transfer item",
            VoucherType.Sales,
            quantity: 3m,
            username: admin.Username,
            now: now);
        firstVersion.VersionGroupId = versionGroupId;
        firstVersion.VersionNumber = 1;
        firstVersion.IsLatestVersion = false;
        var latestVersion = BuildInventoryInvoiceDto(
            latestInvoiceId,
            customerId,
            itemId,
            "Latest delete marker transfer item",
            VoucherType.Sales,
            quantity: 5m,
            username: admin.Username,
            now: now);
        latestVersion.VersionGroupId = versionGroupId;
        latestVersion.VersionNumber = 2;
        latestVersion.PreviousVersionId = firstInvoiceId;
        latestVersion.IsLatestVersion = true;

        await using (var createDb = CreateDbContext(admin))
        {
            var createResponse = await CreateController(createDb, admin)
                .Push(
                    new SyncPushRequest
                    {
                        DeviceId =
                            "latest-delete-marker-transfer-create-device",
                        Invoices = [firstVersion, latestVersion]
                    },
                    CancellationToken.None);
            var createResult = Assert.IsType<SyncPushResult>(
                Assert.IsType<OkObjectResult>(
                    createResponse.Result).Value);
            Assert.Equal(2, createResult.AcceptedCount);
            Assert.Equal(0, createResult.ConflictCount);
        }

        long latestInvoiceRevision;
        long stockRevision;
        await using (var readDb = CreateDbContext(admin))
        {
            latestInvoiceRevision = await readDb.Invoices
                .IgnoreQueryFilters()
                .Where(invoice => invoice.Id == latestInvoiceId)
                .Select(invoice => invoice.Revision)
                .SingleAsync();
            stockRevision = await readDb.ItemWarehouseStocks
                .Where(stock =>
                    stock.ItemId == itemId &&
                    stock.WarehouseCode ==
                    OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => stock.Revision)
                .SingleAsync();
            Assert.Equal(
                5m,
                await readDb.ItemWarehouseStocks
                    .Where(stock =>
                        stock.ItemId == itemId &&
                        stock.WarehouseCode ==
                        OfficeCodeCatalog.UsenetMainWarehouse)
                    .Select(stock => stock.Quantity)
                    .SingleAsync());
        }

        var deleteLatest = BuildInventoryInvoiceDto(
            latestInvoiceId,
            customerId,
            itemId,
            "Latest delete marker transfer item",
            VoucherType.Sales,
            quantity: 5m,
            username: admin.Username,
            now: now.AddMinutes(1));
        deleteLatest.VersionGroupId = versionGroupId;
        deleteLatest.VersionNumber = 2;
        deleteLatest.PreviousVersionId = firstInvoiceId;
        deleteLatest.IsLatestVersion = true;
        deleteLatest.IsDeleted = true;
        deleteLatest.Revision = latestInvoiceRevision;
        deleteLatest.ExpectedRevision = latestInvoiceRevision;
        deleteLatest.MutationId =
            $"latest-delete-marker-transfer:Invoice:{latestInvoiceId:N}:delete";
        var transfer = BuildPendingTransferDto(
            transferId,
            itemId,
            "Latest delete marker transfer item",
            quantity: 7m,
            username: admin.Username,
            now: now.AddMinutes(1),
            mutationPrefix:
                "latest-delete-marker-transfer-projection");

        await using var dbContext = CreateDbContext(admin);
        var response = await CreateController(dbContext, admin)
            .Push(
                new SyncPushRequest
                {
                    DeviceId =
                        "latest-delete-marker-transfer-device",
                    ItemWarehouseStockSnapshotMarkers =
                    [
                        new ItemWarehouseStockSnapshotMarkerDto
                        {
                            ItemId = itemId,
                            MaxKnownRevision = stockRevision
                        }
                    ],
                    Invoices = [deleteLatest],
                    InventoryTransfers = [transfer]
                },
                CancellationToken.None);

        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(2, result.AcceptedCount);
        Assert.Equal(0, result.ConflictCount);

        dbContext.ChangeTracker.Clear();
        var storedVersions = await dbContext.Invoices
            .IgnoreQueryFilters()
            .Where(invoice =>
                invoice.Id == firstInvoiceId ||
                invoice.Id == latestInvoiceId)
            .ToDictionaryAsync(invoice => invoice.Id);
        Assert.True(storedVersions[firstInvoiceId].IsLatestVersion);
        Assert.False(storedVersions[firstInvoiceId].IsDeleted);
        Assert.True(storedVersions[latestInvoiceId].IsDeleted);
        Assert.False(storedVersions[latestInvoiceId].IsLatestVersion);
        Assert.Equal(
            0m,
            await dbContext.ItemWarehouseStocks
                .Where(stock =>
                    stock.ItemId == itemId &&
                    stock.WarehouseCode ==
                    OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => stock.Quantity)
                .SingleAsync());
        Assert.True(
            await dbContext.InventoryTransfers
                .IgnoreQueryFilters()
                .AnyAsync(current => current.Id == transferId));
    }

    [Fact]
    public async Task Push_RedeleteLegacyDeletedLatestInvoice_RepairsScopedVersionStockAndLedgerOnce()
    {
        var itemId = Guid.NewGuid();
        var localCustomerId = Guid.NewGuid();
        var foreignCustomerId = Guid.NewGuid();
        var versionGroupId = Guid.NewGuid();
        var activeInvoiceId = Guid.NewGuid();
        var deletedLatestInvoiceId = Guid.NewGuid();
        var foreignInvoiceId = Guid.NewGuid();
        var activeLineId = Guid.NewGuid();
        var now = new DateTime(2026, 7, 31, 5, 45, 0, DateTimeKind.Utc);
        long deletedLatestRevision;
        long foreignInvoiceRevision;

        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            seedDb.Items.Add(
                CreateStockItem(
                    itemId,
                    "Legacy deleted latest repair item",
                    currentStock: 10m));
            seedDb.ItemWarehouseStocks.Add(new ItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode =
                    OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 10m,
                UpdatedAtUtc = now.AddMinutes(-10)
            });
            seedDb.Customers.AddRange(
                new Customer
                {
                    Id = localCustomerId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    NameOriginal = "Legacy deleted latest repair customer",
                    NameMatchKey = "LEGACYDELETEDLATESTREPAIRCUSTOMER",
                    TradeType = CustomerClassificationNormalizer.Sales
                },
                new Customer
                {
                    Id = foreignCustomerId,
                    TenantCode = TenantScopeCatalog.Itworld,
                    OfficeCode = OfficeCodeCatalog.Itworld,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                    NameOriginal = "Foreign same version group customer",
                    NameMatchKey = "FOREIGNSAMEVERSIONGROUPCUSTOMER",
                    TradeType = CustomerClassificationNormalizer.Sales
                });
            seedDb.Invoices.AddRange(
                new Invoice
                {
                    Id = activeInvoiceId,
                    CustomerId = localCustomerId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    SourceWarehouseCode =
                        OfficeCodeCatalog.UsenetMainWarehouse,
                    InvoiceNumber = "LEGACY-ACTIVE-0001",
                    VersionGroupId = versionGroupId,
                    VersionNumber = 1,
                    IsLatestVersion = false,
                    VoucherType = VoucherType.Sales,
                    InvoiceDate = new DateOnly(2026, 7, 31),
                    UpdatedAtUtc = now.AddMinutes(-3),
                    Lines =
                    [
                        new InvoiceLine
                        {
                            Id = activeLineId,
                            InvoiceId = activeInvoiceId,
                            ItemId = itemId,
                            ItemNameOriginal =
                                "Legacy deleted latest repair item",
                            Unit = "EA",
                            Quantity = 3m,
                            UnitPrice = 100m,
                            LineAmount = 300m,
                            OrderIndex = 1,
                            ItemTrackingType = ItemTrackingTypes.Stock
                        }
                    ]
                },
                new Invoice
                {
                    Id = deletedLatestInvoiceId,
                    CustomerId = localCustomerId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    SourceWarehouseCode =
                        OfficeCodeCatalog.UsenetMainWarehouse,
                    InvoiceNumber = "LEGACY-DELETED-0002",
                    VersionGroupId = versionGroupId,
                    VersionNumber = 2,
                    PreviousVersionId = activeInvoiceId,
                    IsLatestVersion = true,
                    IsDeleted = true,
                    VoucherType = VoucherType.Sales,
                    InvoiceDate = new DateOnly(2026, 7, 31),
                    UpdatedAtUtc = now.AddMinutes(-2)
                },
                new Invoice
                {
                    Id = foreignInvoiceId,
                    CustomerId = foreignCustomerId,
                    TenantCode = TenantScopeCatalog.Itworld,
                    OfficeCode = OfficeCodeCatalog.Itworld,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                    InvoiceNumber = "FOREIGN-SAME-GROUP-0099",
                    VersionGroupId = versionGroupId,
                    VersionNumber = 99,
                    IsLatestVersion = false,
                    VoucherType = VoucherType.Sales,
                    InvoiceDate = new DateOnly(2026, 7, 31),
                    UpdatedAtUtc = now.AddMinutes(-1)
                });
            await seedDb.SaveChangesAsync();
            deletedLatestRevision = await seedDb.Invoices
                .IgnoreQueryFilters()
                .Where(invoice => invoice.Id == deletedLatestInvoiceId)
                .Select(invoice => invoice.Revision)
                .SingleAsync();
            foreignInvoiceRevision = await seedDb.Invoices
                .IgnoreQueryFilters()
                .Where(invoice => invoice.Id == foreignInvoiceId)
                .Select(invoice => invoice.Revision)
                .SingleAsync();
        }

        var admin = CreateInventoryDeliveryAdminUser(
            "admin-legacy-deleted-latest-repair",
            OfficeCodeCatalog.Usenet);
        var deleteMutation = new InvoiceDto
        {
            Id = deletedLatestInvoiceId,
            CustomerId = foreignCustomerId,
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
            InvoiceNumber = "UNTRUSTED-REDELETE-PAYLOAD",
            VersionGroupId = foreignInvoiceId,
            VersionNumber = 999,
            IsLatestVersion = true,
            IsDeleted = true,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 7, 31),
            Revision = deletedLatestRevision,
            ExpectedRevision = deletedLatestRevision,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            MutationId =
                $"legacy-redelete-repair:Invoice:{deletedLatestInvoiceId:N}",
            MutationCreatedAtUtc = now
        };
        await using var dbContext = CreateDbContext(admin);
        var response = await CreateController(dbContext, admin)
            .Push(
                new SyncPushRequest
                {
                    DeviceId = "legacy-redelete-repair-device",
                    Invoices = [deleteMutation]
                },
                CancellationToken.None);

        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(1, result.AcceptedCount);
        Assert.Equal(0, result.ConflictCount);
        Assert.Equal(0, result.DuplicateMutationCount);

        dbContext.ChangeTracker.Clear();
        var localVersions = await dbContext.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(invoice =>
                invoice.Id == activeInvoiceId ||
                invoice.Id == deletedLatestInvoiceId)
            .ToDictionaryAsync(invoice => invoice.Id);
        Assert.True(localVersions[activeInvoiceId].IsLatestVersion);
        Assert.False(localVersions[activeInvoiceId].IsDeleted);
        Assert.True(localVersions[deletedLatestInvoiceId].IsDeleted);
        Assert.False(
            localVersions[deletedLatestInvoiceId].IsLatestVersion);
        Assert.Equal(
            versionGroupId,
            localVersions[deletedLatestInvoiceId].VersionGroupId);
        Assert.Equal(
            TenantScopeCatalog.UsenetGroup,
            localVersions[deletedLatestInvoiceId].TenantCode);
        Assert.Equal(
            OfficeCodeCatalog.Usenet,
            localVersions[deletedLatestInvoiceId].OfficeCode);
        Assert.Equal(
            foreignInvoiceRevision,
            await dbContext.Invoices
                .IgnoreQueryFilters()
                .Where(invoice => invoice.Id == foreignInvoiceId)
                .Select(invoice => invoice.Revision)
                .SingleAsync());
        Assert.False(
            await dbContext.Invoices
                .IgnoreQueryFilters()
                .Where(invoice => invoice.Id == foreignInvoiceId)
                .Select(invoice => invoice.IsLatestVersion)
                .SingleAsync());
        Assert.Equal(
            7m,
            await dbContext.ItemWarehouseStocks
                .Where(stock =>
                    stock.ItemId == itemId &&
                    stock.WarehouseCode ==
                    OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => stock.Quantity)
                .SingleAsync());
        Assert.Equal(
            7m,
            await dbContext.Items
                .IgnoreQueryFilters()
                .Where(item => item.Id == itemId)
                .Select(item => item.CurrentStock)
                .SingleAsync());
        var ledgerEntry = Assert.Single(
            await dbContext.InventoryLedgerEntries
                .Where(entry => entry.ItemId == itemId)
                .ToListAsync());
        Assert.Equal(activeInvoiceId, ledgerEntry.SourceDocumentId);
        Assert.Equal(activeLineId, ledgerEntry.SourceLineId);
        Assert.Equal(-3m, ledgerEntry.QuantityDelta);

        var repairedDeletedRevision =
            localVersions[deletedLatestInvoiceId].Revision;
        var repairedActiveRevision =
            localVersions[activeInvoiceId].Revision;
        var replayResult = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(
                (await CreateController(dbContext, admin)
                    .Push(
                        new SyncPushRequest
                        {
                            DeviceId =
                                "legacy-redelete-repair-device",
                            Invoices = [deleteMutation]
                        },
                        CancellationToken.None))
                .Result)
            .Value);
        Assert.Equal(1, replayResult.AcceptedCount);
        Assert.Equal(0, replayResult.ConflictCount);
        Assert.Equal(1, replayResult.DuplicateMutationCount);

        dbContext.ChangeTracker.Clear();
        Assert.Equal(
            repairedDeletedRevision,
            await dbContext.Invoices
                .IgnoreQueryFilters()
                .Where(invoice => invoice.Id == deletedLatestInvoiceId)
                .Select(invoice => invoice.Revision)
                .SingleAsync());
        Assert.Equal(
            repairedActiveRevision,
            await dbContext.Invoices
                .IgnoreQueryFilters()
                .Where(invoice => invoice.Id == activeInvoiceId)
                .Select(invoice => invoice.Revision)
                .SingleAsync());
        Assert.Equal(
            7m,
            await dbContext.ItemWarehouseStocks
                .Where(stock =>
                    stock.ItemId == itemId &&
                    stock.WarehouseCode ==
                    OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => stock.Quantity)
                .SingleAsync());
        Assert.Single(
            await dbContext.InventoryLedgerEntries
                .Where(entry => entry.ItemId == itemId)
                .ToListAsync());
    }

    [Fact]
    public async Task Push_NewActiveInvoice_CannotJoinForeignTenantVersionGroup()
    {
        var localCustomerId = Guid.NewGuid();
        var foreignCustomerId = Guid.NewGuid();
        var foreignInvoiceId = Guid.NewGuid();
        var localInvoiceId = Guid.NewGuid();
        var versionGroupId = Guid.NewGuid();
        var now = new DateTime(2026, 7, 31, 5, 50, 0, DateTimeKind.Utc);
        long foreignInvoiceRevision;

        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            seedDb.Customers.AddRange(
                new Customer
                {
                    Id = localCustomerId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    NameOriginal = "Local active group join customer",
                    NameMatchKey = "LOCALACTIVEGROUPJOINCUSTOMER",
                    TradeType = CustomerClassificationNormalizer.Sales
                },
                new Customer
                {
                    Id = foreignCustomerId,
                    TenantCode = TenantScopeCatalog.Itworld,
                    OfficeCode = OfficeCodeCatalog.Itworld,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                    NameOriginal = "Foreign active group owner",
                    NameMatchKey = "FOREIGNACTIVEGROUPOWNER",
                    TradeType = CustomerClassificationNormalizer.Sales
                });
            var foreignInvoice = new Invoice
            {
                Id = foreignInvoiceId,
                CustomerId = foreignCustomerId,
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                InvoiceNumber = "FOREIGN-ACTIVE-GROUP-0001",
                VersionGroupId = versionGroupId,
                VersionNumber = 1,
                IsLatestVersion = true,
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026, 7, 31),
                UpdatedAtUtc = now.AddMinutes(-1)
            };
            seedDb.Invoices.Add(foreignInvoice);
            await seedDb.SaveChangesAsync();
            foreignInvoiceRevision = foreignInvoice.Revision;
        }

        var admin = CreateInventoryDeliveryAdminUser(
            "admin-active-foreign-group-join",
            OfficeCodeCatalog.Usenet);
        var localInvoice = new InvoiceDto
        {
            Id = localInvoiceId,
            CustomerId = localCustomerId,
            CustomerName = "Local active group join customer",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "LOCAL-ACTIVE-GROUP-0099",
            VersionGroupId = versionGroupId,
            VersionNumber = 99,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 7, 31),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            MutationId =
                $"active-foreign-group-join:Invoice:{localInvoiceId:N}",
            MutationCreatedAtUtc = now
        };

        await using var dbContext = CreateDbContext(admin);
        var response = await CreateController(dbContext, admin)
            .Push(
                new SyncPushRequest
                {
                    DeviceId = "active-foreign-group-join-device",
                    Invoices = [localInvoice]
                },
                CancellationToken.None);

        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(
            result.Conflicts,
            conflict =>
                conflict.EntityName == nameof(Invoice) &&
                conflict.EntityId == localInvoiceId.ToString("D"));

        dbContext.ChangeTracker.Clear();
        Assert.False(
            await dbContext.Invoices
                .IgnoreQueryFilters()
                .AnyAsync(invoice => invoice.Id == localInvoiceId));
        Assert.True(
            await dbContext.Invoices
                .IgnoreQueryFilters()
                .Where(invoice => invoice.Id == foreignInvoiceId)
                .Select(invoice => invoice.IsLatestVersion)
                .SingleAsync());
        Assert.Equal(
            foreignInvoiceRevision,
            await dbContext.Invoices
                .IgnoreQueryFilters()
                .Where(invoice => invoice.Id == foreignInvoiceId)
                .Select(invoice => invoice.Revision)
                .SingleAsync());
    }

    [Fact]
    public async Task Push_NewVersion_JoinsMatchingScopedChainAndIgnoresForeignRawGroupCollision()
    {
        var localCustomerId = Guid.NewGuid();
        var foreignCustomerId = Guid.NewGuid();
        var localFirstInvoiceId = Guid.NewGuid();
        var localSecondInvoiceId = Guid.NewGuid();
        var foreignInvoiceId = Guid.NewGuid();
        var versionGroupId = localFirstInvoiceId;
        var now = new DateTime(2026, 7, 31, 5, 52, 0, DateTimeKind.Utc);
        long localFirstRevision;
        long foreignInvoiceRevision;

        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            seedDb.Customers.AddRange(
                new Customer
                {
                    Id = localCustomerId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    NameOriginal = "Local collision chain customer",
                    NameMatchKey = "LOCALCOLLISIONCHAINCUSTOMER",
                    TradeType = CustomerClassificationNormalizer.Sales
                },
                new Customer
                {
                    Id = foreignCustomerId,
                    TenantCode = TenantScopeCatalog.Itworld,
                    OfficeCode = OfficeCodeCatalog.Itworld,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                    NameOriginal = "Foreign raw collision customer",
                    NameMatchKey = "FOREIGNRAWCOLLISIONCUSTOMER",
                    TradeType = CustomerClassificationNormalizer.Sales
                });
            var localFirstInvoice = new Invoice
            {
                Id = localFirstInvoiceId,
                CustomerId = localCustomerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                InvoiceNumber = "LOCAL-COLLISION-0001",
                VersionGroupId = versionGroupId,
                VersionNumber = 1,
                IsLatestVersion = true,
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026, 7, 31),
                UpdatedAtUtc = now.AddMinutes(-2)
            };
            var foreignInvoice = new Invoice
            {
                Id = foreignInvoiceId,
                CustomerId = foreignCustomerId,
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                InvoiceNumber = "FOREIGN-COLLISION-0099",
                VersionGroupId = versionGroupId,
                VersionNumber = 99,
                IsLatestVersion = true,
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026, 7, 31),
                UpdatedAtUtc = now.AddMinutes(-1)
            };
            seedDb.Invoices.AddRange(
                localFirstInvoice,
                foreignInvoice);
            await seedDb.SaveChangesAsync();
            localFirstRevision =
                localFirstInvoice.Revision;
            foreignInvoiceRevision =
                foreignInvoice.Revision;
        }

        var admin = CreateInventoryDeliveryAdminUser(
            "admin-local-collision-chain",
            OfficeCodeCatalog.Usenet);
        var localSecondVersion = new InvoiceDto
        {
            Id = localSecondInvoiceId,
            CustomerId = localCustomerId,
            CustomerName = "Local collision chain customer",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "LOCAL-COLLISION-0002",
            VersionGroupId = versionGroupId,
            VersionNumber = 2,
            PreviousVersionId = localFirstInvoiceId,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 7, 31),
            Revision = localFirstRevision,
            ExpectedRevision = localFirstRevision,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            MutationId =
                $"local-collision-chain:Invoice:{localSecondInvoiceId:N}",
            MutationCreatedAtUtc = now
        };

        await using var dbContext = CreateDbContext(admin);
        var response = await CreateController(dbContext, admin)
            .Push(
                new SyncPushRequest
                {
                    DeviceId = "local-collision-chain-device",
                    Invoices = [localSecondVersion]
                },
                CancellationToken.None);

        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(1, result.AcceptedCount);
        Assert.Equal(0, result.ConflictCount);
        dbContext.ChangeTracker.Clear();
        Assert.False(
            await dbContext.Invoices
                .IgnoreQueryFilters()
                .Where(invoice =>
                    invoice.Id == localFirstInvoiceId)
                .Select(invoice => invoice.IsLatestVersion)
                .SingleAsync());
        Assert.True(
            await dbContext.Invoices
                .IgnoreQueryFilters()
                .Where(invoice =>
                    invoice.Id == localSecondInvoiceId)
                .Select(invoice => invoice.IsLatestVersion)
                .SingleAsync());
        Assert.True(
            await dbContext.Invoices
                .IgnoreQueryFilters()
                .Where(invoice => invoice.Id == foreignInvoiceId)
                .Select(invoice => invoice.IsLatestVersion)
                .SingleAsync());
        Assert.Equal(
            foreignInvoiceRevision,
            await dbContext.Invoices
                .IgnoreQueryFilters()
                .Where(invoice => invoice.Id == foreignInvoiceId)
                .Select(invoice => invoice.Revision)
                .SingleAsync());
    }

    [Fact]
    public async Task Push_NewInvoiceVersion_RejectsPreviousOutsideScopedVersionGroup()
    {
        var customerId = Guid.NewGuid();
        var firstInvoiceId = Guid.NewGuid();
        var unrelatedInvoiceId = Guid.NewGuid();
        var newInvoiceId = Guid.NewGuid();
        var versionGroupId = Guid.NewGuid();
        var unrelatedVersionGroupId = Guid.NewGuid();
        var now = new DateTime(2026, 7, 31, 5, 55, 0, DateTimeKind.Utc);
        long firstInvoiceRevision;
        long unrelatedInvoiceRevision;

        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            seedDb.Customers.Add(new Customer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Previous group validation customer",
                NameMatchKey = "PREVIOUSGROUPVALIDATIONCUSTOMER",
                TradeType = CustomerClassificationNormalizer.Sales
            });
            var firstInvoice = new Invoice
            {
                Id = firstInvoiceId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                InvoiceNumber = "PREVIOUS-GROUP-0001",
                VersionGroupId = versionGroupId,
                VersionNumber = 1,
                IsLatestVersion = true,
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026, 7, 31),
                UpdatedAtUtc = now.AddMinutes(-2)
            };
            var unrelatedInvoice = new Invoice
            {
                Id = unrelatedInvoiceId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                InvoiceNumber = "UNRELATED-GROUP-0001",
                VersionGroupId = unrelatedVersionGroupId,
                VersionNumber = 1,
                IsLatestVersion = true,
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026, 7, 31),
                UpdatedAtUtc = now.AddMinutes(-1)
            };
            seedDb.Invoices.AddRange(firstInvoice, unrelatedInvoice);
            await seedDb.SaveChangesAsync();
            firstInvoiceRevision = firstInvoice.Revision;
            unrelatedInvoiceRevision = unrelatedInvoice.Revision;
        }

        var admin = CreateInventoryDeliveryAdminUser(
            "admin-previous-group-validation",
            OfficeCodeCatalog.Usenet);
        var newVersion = new InvoiceDto
        {
            Id = newInvoiceId,
            CustomerId = customerId,
            CustomerName = "Previous group validation customer",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "PREVIOUS-GROUP-0002",
            VersionGroupId = versionGroupId,
            VersionNumber = 2,
            PreviousVersionId = unrelatedInvoiceId,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 7, 31),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            MutationId =
                $"invalid-previous-group:Invoice:{newInvoiceId:N}",
            MutationCreatedAtUtc = now
        };

        await using var dbContext = CreateDbContext(admin);
        var response = await CreateController(dbContext, admin)
            .Push(
                new SyncPushRequest
                {
                    DeviceId = "invalid-previous-group-device",
                    Invoices = [newVersion]
                },
                CancellationToken.None);

        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(1, result.ConflictCount);
        dbContext.ChangeTracker.Clear();
        Assert.False(
            await dbContext.Invoices
                .IgnoreQueryFilters()
                .AnyAsync(invoice => invoice.Id == newInvoiceId));
        Assert.Equal(
            firstInvoiceRevision,
            await dbContext.Invoices
                .IgnoreQueryFilters()
                .Where(invoice => invoice.Id == firstInvoiceId)
                .Select(invoice => invoice.Revision)
                .SingleAsync());
        Assert.Equal(
            unrelatedInvoiceRevision,
            await dbContext.Invoices
                .IgnoreQueryFilters()
                .Where(invoice => invoice.Id == unrelatedInvoiceId)
                .Select(invoice => invoice.Revision)
                .SingleAsync());
    }

    [Fact]
    public async Task Push_ExistingInvoice_CannotMutateVersionMetadataInPlace()
    {
        var customerId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var versionGroupId = Guid.NewGuid();
        var now = new DateTime(2026, 7, 31, 6, 0, 0, DateTimeKind.Utc);
        long invoiceRevision;

        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            seedDb.Customers.Add(new Customer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Existing metadata guard customer",
                NameMatchKey = "EXISTINGMETADATAGUARDCUSTOMER",
                TradeType = CustomerClassificationNormalizer.Sales
            });
            var invoice = new Invoice
            {
                Id = invoiceId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                InvoiceNumber = "EXISTING-METADATA-0001",
                VersionGroupId = versionGroupId,
                VersionNumber = 1,
                IsLatestVersion = true,
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026, 7, 31),
                UpdatedAtUtc = now.AddMinutes(-1)
            };
            seedDb.Invoices.Add(invoice);
            await seedDb.SaveChangesAsync();
            invoiceRevision = invoice.Revision;
        }

        var admin = CreateInventoryDeliveryAdminUser(
            "admin-existing-metadata-guard",
            OfficeCodeCatalog.Usenet);
        var mutation = new InvoiceDto
        {
            Id = invoiceId,
            CustomerId = customerId,
            CustomerName = "Existing metadata guard customer",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "EXISTING-METADATA-0001",
            VersionGroupId = versionGroupId,
            VersionNumber = 2,
            PreviousVersionId = Guid.NewGuid(),
            IsLatestVersion = false,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 7, 31),
            Revision = invoiceRevision,
            ExpectedRevision = invoiceRevision,
            CreatedAtUtc = now.AddMinutes(-1),
            UpdatedAtUtc = now,
            MutationId =
                $"existing-version-metadata:Invoice:{invoiceId:N}",
            MutationCreatedAtUtc = now
        };

        await using var dbContext = CreateDbContext(admin);
        var response = await CreateController(dbContext, admin)
            .Push(
                new SyncPushRequest
                {
                    DeviceId = "existing-version-metadata-device",
                    Invoices = [mutation]
                },
                CancellationToken.None);

        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(1, result.ConflictCount);
        dbContext.ChangeTracker.Clear();
        var storedInvoice = await dbContext.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(invoice => invoice.Id == invoiceId);
        Assert.Equal(invoiceRevision, storedInvoice.Revision);
        Assert.Equal(versionGroupId, storedInvoice.VersionGroupId);
        Assert.Equal(1, storedInvoice.VersionNumber);
        Assert.Null(storedInvoice.PreviousVersionId);
        Assert.True(storedInvoice.IsLatestVersion);
    }

    [Fact]
    public async Task Push_NewInitialInvoice_RejectsNonSelfVersionGroup()
    {
        var customerId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var now = new DateTime(2026, 7, 31, 6, 2, 0, DateTimeKind.Utc);
        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            seedDb.Customers.Add(new Customer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Non self group customer",
                NameMatchKey = "NONSELFGROUPCUSTOMER",
                TradeType = CustomerClassificationNormalizer.Sales
            });
            await seedDb.SaveChangesAsync();
        }

        var admin = CreateInventoryDeliveryAdminUser(
            "admin-non-self-version-group",
            OfficeCodeCatalog.Usenet);
        await using var dbContext = CreateDbContext(admin);
        var response = await CreateController(dbContext, admin)
            .Push(
                new SyncPushRequest
                {
                    DeviceId = "non-self-version-group-device",
                    Invoices =
                    [
                        new InvoiceDto
                        {
                            Id = invoiceId,
                            CustomerId = customerId,
                            CustomerName = "Non self group customer",
                            TenantCode = TenantScopeCatalog.UsenetGroup,
                            OfficeCode = OfficeCodeCatalog.Usenet,
                            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                            InvoiceNumber = "NON-SELF-GROUP-0001",
                            VersionGroupId = Guid.NewGuid(),
                            VersionNumber = 1,
                            IsLatestVersion = true,
                            VoucherType = VoucherType.Sales,
                            InvoiceDate = new DateOnly(2026, 7, 31),
                            CreatedAtUtc = now,
                            UpdatedAtUtc = now,
                            MutationId =
                                $"non-self-version-group:Invoice:{invoiceId:N}",
                            MutationCreatedAtUtc = now
                        }
                    ]
                },
                CancellationToken.None);

        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(1, result.ConflictCount);
        Assert.False(
            await dbContext.Invoices
                .IgnoreQueryFilters()
                .AnyAsync(invoice => invoice.Id == invoiceId));
    }

    [Fact]
    public async Task Push_NewInitialInvoiceWithEmptyGroup_ExactReplayKeepsOriginalPayloadHash()
    {
        var customerId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var now = new DateTime(2026, 7, 31, 6, 2, 30, DateTimeKind.Utc);
        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            seedDb.Customers.Add(new Customer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Empty group replay customer",
                NameMatchKey = "EMPTYGROUPREPLAYCUSTOMER",
                TradeType = CustomerClassificationNormalizer.Sales
            });
            await seedDb.SaveChangesAsync();
        }

        var mutation = new InvoiceDto
        {
            Id = invoiceId,
            CustomerId = customerId,
            CustomerName = "Empty group replay customer",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "EMPTY-GROUP-REPLAY-0001",
            VersionGroupId = Guid.Empty,
            VersionNumber = 1,
            IsLatestVersion = false,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 7, 31),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            MutationId =
                $"empty-group-replay:Invoice:{invoiceId:N}",
            MutationCreatedAtUtc = now
        };
        var admin = CreateInventoryDeliveryAdminUser(
            "admin-empty-group-replay",
            OfficeCodeCatalog.Usenet);
        await using var dbContext = CreateDbContext(admin);
        var firstResult = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(
                (await CreateController(dbContext, admin)
                    .Push(
                        new SyncPushRequest
                        {
                            DeviceId =
                                "empty-group-replay-device",
                            Invoices = [mutation]
                        },
                        CancellationToken.None))
                .Result)
            .Value);
        Assert.Equal(1, firstResult.AcceptedCount);
        Assert.Equal(0, firstResult.ConflictCount);
        Assert.Equal(0, firstResult.DuplicateMutationCount);
        Assert.Equal(Guid.Empty, mutation.VersionGroupId);

        dbContext.ChangeTracker.Clear();
        var stored = await dbContext.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(invoice => invoice.Id == invoiceId);
        Assert.Equal(invoiceId, stored.VersionGroupId);
        Assert.True(stored.IsLatestVersion);

        var replayResult = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(
                (await CreateController(dbContext, admin)
                    .Push(
                        new SyncPushRequest
                        {
                            DeviceId =
                                "empty-group-replay-device",
                            Invoices = [mutation]
                        },
                        CancellationToken.None))
                .Result)
            .Value);
        Assert.Equal(1, replayResult.AcceptedCount);
        Assert.Equal(0, replayResult.ConflictCount);
        Assert.Equal(1, replayResult.DuplicateMutationCount);
        Assert.Equal(Guid.Empty, mutation.VersionGroupId);

        dbContext.ChangeTracker.Clear();
        Assert.Equal(
            stored.Revision,
            await dbContext.Invoices
                .IgnoreQueryFilters()
                .Where(invoice => invoice.Id == invoiceId)
                .Select(invoice => invoice.Revision)
                .SingleAsync());
    }

    [Fact]
    public async Task Push_LatestFalseIsServerDerived_AndDesktopVersionBatchStillSucceeds()
    {
        var customerId = Guid.NewGuid();
        var firstInvoiceId = Guid.NewGuid();
        var secondInvoiceId = Guid.NewGuid();
        var now = new DateTime(2026, 7, 31, 6, 3, 0, DateTimeKind.Utc);
        long firstInvoiceRevision;
        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            seedDb.Customers.Add(new Customer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Desktop version batch customer",
                NameMatchKey = "DESKTOPVERSIONBATCHCUSTOMER",
                TradeType = CustomerClassificationNormalizer.Sales
            });
            var firstInvoice = new Invoice
            {
                Id = firstInvoiceId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                InvoiceNumber = "DESKTOP-VERSION-0001",
                VersionGroupId = firstInvoiceId,
                VersionNumber = 1,
                IsLatestVersion = true,
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026, 7, 31),
                CreatedAtUtc = now.AddMinutes(-2),
                UpdatedAtUtc = now.AddMinutes(-2)
            };
            seedDb.Invoices.Add(firstInvoice);
            await seedDb.SaveChangesAsync();
            firstInvoiceRevision = firstInvoice.Revision;
        }

        InvoiceDto CreateFirstVersionMutation(
            long revision,
            string mutationSuffix,
            DateTime updatedAtUtc)
            => new()
            {
                Id = firstInvoiceId,
                CustomerId = customerId,
                CustomerName = "Desktop version batch customer",
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                InvoiceNumber = "DESKTOP-VERSION-0001",
                VersionGroupId = firstInvoiceId,
                VersionNumber = 1,
                IsLatestVersion = false,
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026, 7, 31),
                Revision = revision,
                ExpectedRevision = revision,
                CreatedAtUtc = now.AddMinutes(-2),
                UpdatedAtUtc = updatedAtUtc,
                MutationId =
                    $"desktop-version-existing:{mutationSuffix}:Invoice:{firstInvoiceId:N}",
                MutationCreatedAtUtc = updatedAtUtc
            };
        InvoiceDto CreateSecondVersion(
            long baseRevision) => new()
        {
            Id = secondInvoiceId,
            CustomerId = customerId,
            CustomerName = "Desktop version batch customer",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "DESKTOP-VERSION-0002",
            VersionGroupId = firstInvoiceId,
            VersionNumber = 2,
            PreviousVersionId = firstInvoiceId,
            IsLatestVersion = true,
            Revision = baseRevision,
            ExpectedRevision = baseRevision,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 7, 31),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            MutationId =
                $"desktop-version-new:Invoice:{secondInvoiceId:N}",
            MutationCreatedAtUtc = now
        };

        var admin = CreateInventoryDeliveryAdminUser(
            "admin-desktop-version-batch",
            OfficeCodeCatalog.Usenet);
        await using var dbContext = CreateDbContext(admin);
        var loneResult = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(
                (await CreateController(dbContext, admin)
                    .Push(
                        new SyncPushRequest
                        {
                            DeviceId =
                                "desktop-version-lone-latest-device",
                            Invoices =
                            [
                                CreateFirstVersionMutation(
                                    firstInvoiceRevision,
                                    "lone",
                                    now.AddMinutes(-1))
                            ]
                        },
                        CancellationToken.None))
                .Result)
            .Value);
        Assert.Equal(1, loneResult.AcceptedCount);
        Assert.Equal(0, loneResult.ConflictCount);
        dbContext.ChangeTracker.Clear();
        Assert.True(
            await dbContext.Invoices
                .IgnoreQueryFilters()
                .Where(invoice => invoice.Id == firstInvoiceId)
                .Select(invoice => invoice.IsLatestVersion)
                .SingleAsync());
        var revisionAfterLoneMutation = await dbContext.Invoices
            .IgnoreQueryFilters()
            .Where(invoice => invoice.Id == firstInvoiceId)
            .Select(invoice => invoice.Revision)
            .SingleAsync();

        var versionBatchResult = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(
                (await CreateController(dbContext, admin)
                    .Push(
                        new SyncPushRequest
                        {
                            DeviceId =
                                "desktop-version-batch-device",
                            Invoices =
                            [
                                CreateSecondVersion(
                                    revisionAfterLoneMutation),
                                CreateFirstVersionMutation(
                                    revisionAfterLoneMutation,
                                    "batch",
                                    now)
                            ]
                        },
                        CancellationToken.None))
                .Result)
            .Value);
        Assert.Equal(2, versionBatchResult.AcceptedCount);
        Assert.Equal(0, versionBatchResult.ConflictCount);

        dbContext.ChangeTracker.Clear();
        var versions = await dbContext.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(invoice =>
                invoice.Id == firstInvoiceId ||
                invoice.Id == secondInvoiceId)
            .ToDictionaryAsync(invoice => invoice.Id);
        Assert.False(versions[firstInvoiceId].IsLatestVersion);
        Assert.True(versions[secondInvoiceId].IsLatestVersion);
        Assert.Equal(
            firstInvoiceId,
            versions[secondInvoiceId].VersionGroupId);
        Assert.Equal(
            firstInvoiceId,
            versions[secondInvoiceId].PreviousVersionId);
        Assert.Equal(2, versions[secondInvoiceId].VersionNumber);
    }

    [Fact]
    public async Task Push_SecondConcurrentVersionBranch_IsRejectedAfterFirstBranchWins()
    {
        var customerId = Guid.NewGuid();
        var firstInvoiceId = Guid.NewGuid();
        var winningVersionId = Guid.NewGuid();
        var losingVersionId = Guid.NewGuid();
        var now = new DateTime(2026, 7, 31, 6, 4, 0, DateTimeKind.Utc);
        long firstInvoiceRevision;
        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            seedDb.Customers.Add(new Customer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Concurrent version branch customer",
                NameMatchKey = "CONCURRENTVERSIONBRANCHCUSTOMER",
                TradeType = CustomerClassificationNormalizer.Sales
            });
            var firstInvoice = new Invoice
            {
                Id = firstInvoiceId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                InvoiceNumber = "CONCURRENT-BRANCH-0001",
                VersionGroupId = firstInvoiceId,
                VersionNumber = 1,
                IsLatestVersion = true,
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026, 7, 31),
                UpdatedAtUtc = now.AddMinutes(-1)
            };
            seedDb.Invoices.Add(firstInvoice);
            await seedDb.SaveChangesAsync();
            firstInvoiceRevision = firstInvoice.Revision;
        }

        InvoiceDto CreateBranch(
            Guid invoiceId,
            string mutationPrefix)
            => new()
            {
                Id = invoiceId,
                CustomerId = customerId,
                CustomerName = "Concurrent version branch customer",
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                InvoiceNumber =
                    $"CONCURRENT-BRANCH-{invoiceId:N}"[..26],
                VersionGroupId = firstInvoiceId,
                VersionNumber = 2,
                PreviousVersionId = firstInvoiceId,
                IsLatestVersion = true,
                Revision = firstInvoiceRevision,
                ExpectedRevision = firstInvoiceRevision,
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026, 7, 31),
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                MutationId =
                    $"{mutationPrefix}:Invoice:{invoiceId:N}",
                MutationCreatedAtUtc = now
            };

        var admin = CreateInventoryDeliveryAdminUser(
            "admin-concurrent-version-branch",
            OfficeCodeCatalog.Usenet);
        await using var dbContext = CreateDbContext(admin);
        var winningResult = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(
                (await CreateController(dbContext, admin)
                    .Push(
                        new SyncPushRequest
                        {
                            DeviceId =
                                "concurrent-version-winning-device",
                            Invoices =
                            [
                                CreateBranch(
                                    winningVersionId,
                                    "winning-version-branch")
                            ]
                        },
                        CancellationToken.None))
                .Result)
            .Value);
        Assert.Equal(1, winningResult.AcceptedCount);
        Assert.Equal(0, winningResult.ConflictCount);

        dbContext.ChangeTracker.Clear();
        var losingResult = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(
                (await CreateController(dbContext, admin)
                    .Push(
                        new SyncPushRequest
                        {
                            DeviceId =
                                "concurrent-version-losing-device",
                            Invoices =
                            [
                                CreateBranch(
                                    losingVersionId,
                                    "losing-version-branch")
                            ]
                        },
                        CancellationToken.None))
                .Result)
            .Value);
        Assert.Equal(0, losingResult.AcceptedCount);
        Assert.Equal(1, losingResult.ConflictCount);

        dbContext.ChangeTracker.Clear();
        Assert.True(
            await dbContext.Invoices
                .IgnoreQueryFilters()
                .Where(invoice => invoice.Id == winningVersionId)
                .Select(invoice => invoice.IsLatestVersion)
                .SingleAsync());
        Assert.False(
            await dbContext.Invoices
                .IgnoreQueryFilters()
                .AnyAsync(invoice => invoice.Id == losingVersionId));
        Assert.False(
            await dbContext.Invoices
                .IgnoreQueryFilters()
                .Where(invoice => invoice.Id == firstInvoiceId)
                .Select(invoice => invoice.IsLatestVersion)
                .SingleAsync());
    }

    [Fact]
    public async Task Push_NewVersion_RequiresFreshLatestBaseRevisionBeforeChangingStockAndLedger()
    {
        var itemId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var firstInvoiceId = Guid.NewGuid();
        var staleVersionId = Guid.NewGuid();
        var freshVersionId = Guid.NewGuid();
        var firstLineId = Guid.NewGuid();
        var now = new DateTime(2026, 7, 31, 6, 6, 0, DateTimeKind.Utc);
        long staleBaseRevision;
        long freshBaseRevision;

        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            seedDb.Items.Add(
                CreateStockItem(
                    itemId,
                    "Fresh version base stock item",
                    currentStock: 8m));
            seedDb.ItemWarehouseStocks.Add(new ItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode =
                    OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 8m,
                UpdatedAtUtc = now.AddMinutes(-5)
            });
            seedDb.Customers.Add(new Customer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Fresh version base customer",
                NameMatchKey = "FRESHVERSIONBASECUSTOMER",
                TradeType = CustomerClassificationNormalizer.Sales
            });
            var firstInvoice = new Invoice
            {
                Id = firstInvoiceId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                SourceWarehouseCode =
                    OfficeCodeCatalog.UsenetMainWarehouse,
                InvoiceNumber = "FRESH-BASE-0001",
                VersionGroupId = firstInvoiceId,
                VersionNumber = 1,
                IsLatestVersion = true,
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026, 7, 31),
                CreatedAtUtc = now.AddMinutes(-3),
                UpdatedAtUtc = now.AddMinutes(-3),
                Lines =
                [
                    new InvoiceLine
                    {
                        Id = firstLineId,
                        InvoiceId = firstInvoiceId,
                        ItemId = itemId,
                        ItemNameOriginal =
                            "Fresh version base stock item",
                        Unit = "EA",
                        Quantity = 2m,
                        UnitPrice = 100m,
                        LineAmount = 200m,
                        OrderIndex = 1,
                        ItemTrackingType = ItemTrackingTypes.Stock
                    }
                ]
            };
            seedDb.Invoices.Add(firstInvoice);
            await seedDb.SaveChangesAsync();
            staleBaseRevision = firstInvoice.Revision;
            firstInvoice.Memo = "server-side edit after client read";
            firstInvoice.UpdatedAtUtc = now.AddMinutes(-1);
            await seedDb.SaveChangesAsync();
            freshBaseRevision = firstInvoice.Revision;
            Assert.True(
                freshBaseRevision >
                staleBaseRevision);
            await new InventoryLedgerService(seedDb)
                .RebuildAsync();
        }

        InvoiceDto CreateVersion(
            Guid invoiceId,
            long baseRevision,
            string mutationPrefix)
            => new()
            {
                Id = invoiceId,
                CustomerId = customerId,
                CustomerName = "Fresh version base customer",
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                SourceWarehouseCode =
                    OfficeCodeCatalog.UsenetMainWarehouse,
                InvoiceNumber =
                    $"FRESH-BASE-{invoiceId:N}"[..24],
                VersionGroupId = firstInvoiceId,
                VersionNumber = 2,
                PreviousVersionId = firstInvoiceId,
                IsLatestVersion = true,
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026, 7, 31),
                SupplyAmount = 300m,
                TotalAmount = 300m,
                Revision = baseRevision,
                ExpectedRevision = baseRevision,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                MutationId =
                    $"{mutationPrefix}:Invoice:{invoiceId:N}",
                MutationCreatedAtUtc = now,
                Lines =
                [
                    new InvoiceLineDto
                    {
                        Id = Guid.NewGuid(),
                        InvoiceId = invoiceId,
                        ItemId = itemId,
                        ItemNameOriginal =
                            "Fresh version base stock item",
                        Unit = "EA",
                        Quantity = 3m,
                        UnitPrice = 100m,
                        LineAmount = 300m,
                        OrderIndex = 1,
                        ItemTrackingType =
                            ItemTrackingTypes.Stock
                    }
                ]
            };

        var admin = CreateInventoryDeliveryAdminUser(
            "admin-fresh-version-base",
            OfficeCodeCatalog.Usenet);
        await using var dbContext = CreateDbContext(admin);
        var staleResult = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(
                (await CreateController(dbContext, admin)
                    .Push(
                        new SyncPushRequest
                        {
                            DeviceId =
                                "stale-version-base-device",
                            Invoices =
                            [
                                CreateVersion(
                                    staleVersionId,
                                    staleBaseRevision,
                                    "stale-version-base")
                            ]
                        },
                        CancellationToken.None))
                .Result)
            .Value);
        Assert.Equal(0, staleResult.AcceptedCount);
        Assert.Equal(1, staleResult.ConflictCount);

        dbContext.ChangeTracker.Clear();
        Assert.False(
            await dbContext.Invoices
                .IgnoreQueryFilters()
                .AnyAsync(invoice => invoice.Id == staleVersionId));
        Assert.True(
            await dbContext.Invoices
                .IgnoreQueryFilters()
                .Where(invoice => invoice.Id == firstInvoiceId)
                .Select(invoice => invoice.IsLatestVersion)
                .SingleAsync());
        Assert.Equal(
            8m,
            await dbContext.ItemWarehouseStocks
                .Where(stock =>
                    stock.ItemId == itemId &&
                    stock.WarehouseCode ==
                    OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => stock.Quantity)
                .SingleAsync());
        Assert.Equal(
            8m,
            await dbContext.Items
                .IgnoreQueryFilters()
                .Where(item => item.Id == itemId)
                .Select(item => item.CurrentStock)
                .SingleAsync());
        var staleLedger = Assert.Single(
            await dbContext.InventoryLedgerEntries
                .Where(entry => entry.ItemId == itemId)
                .ToListAsync());
        Assert.Equal(
            firstInvoiceId,
            staleLedger.SourceDocumentId);
        Assert.Equal(-2m, staleLedger.QuantityDelta);

        var freshResult = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(
                (await CreateController(dbContext, admin)
                    .Push(
                        new SyncPushRequest
                        {
                            DeviceId =
                                "fresh-version-base-device",
                            Invoices =
                            [
                                CreateVersion(
                                    freshVersionId,
                                    freshBaseRevision,
                                    "fresh-version-base")
                            ]
                        },
                        CancellationToken.None))
                .Result)
            .Value);
        Assert.Equal(1, freshResult.AcceptedCount);
        Assert.Equal(0, freshResult.ConflictCount);

        dbContext.ChangeTracker.Clear();
        Assert.False(
            await dbContext.Invoices
                .IgnoreQueryFilters()
                .Where(invoice => invoice.Id == firstInvoiceId)
                .Select(invoice => invoice.IsLatestVersion)
                .SingleAsync());
        Assert.True(
            await dbContext.Invoices
                .IgnoreQueryFilters()
                .Where(invoice => invoice.Id == freshVersionId)
                .Select(invoice => invoice.IsLatestVersion)
                .SingleAsync());
        Assert.Equal(
            7m,
            await dbContext.ItemWarehouseStocks
                .Where(stock =>
                    stock.ItemId == itemId &&
                    stock.WarehouseCode ==
                    OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => stock.Quantity)
                .SingleAsync());
        Assert.Equal(
            7m,
            await dbContext.Items
                .IgnoreQueryFilters()
                .Where(item => item.Id == itemId)
                .Select(item => item.CurrentStock)
                .SingleAsync());
        var freshLedger = Assert.Single(
            await dbContext.InventoryLedgerEntries
                .Where(entry => entry.ItemId == itemId)
                .ToListAsync());
        Assert.Equal(
            freshVersionId,
            freshLedger.SourceDocumentId);
        Assert.Equal(-3m, freshLedger.QuantityDelta);
    }

    [Fact]
    public async Task Push_NewVersionBatchInReverseOrder_UsesLocalGroupAndExactReplayReceipts()
    {
        var customerId = Guid.NewGuid();
        var firstInvoiceId = Guid.NewGuid();
        var secondInvoiceId = Guid.NewGuid();
        var versionGroupId = firstInvoiceId;
        var now = new DateTime(2026, 7, 31, 6, 5, 0, DateTimeKind.Utc);
        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            seedDb.Customers.Add(new Customer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Reverse version batch customer",
                NameMatchKey = "REVERSEVERSIONBATCHCUSTOMER",
                TradeType = CustomerClassificationNormalizer.Sales
            });
            await seedDb.SaveChangesAsync();
        }

        InvoiceDto CreateFirstVersion() => new()
        {
            Id = firstInvoiceId,
            CustomerId = customerId,
            CustomerName = "Reverse version batch customer",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "REVERSE-BATCH-0001",
            VersionGroupId = versionGroupId,
            VersionNumber = 1,
            IsLatestVersion = false,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 7, 31),
            CreatedAtUtc = now.AddMinutes(-1),
            UpdatedAtUtc = now.AddMinutes(-1),
            MutationId =
                $"reverse-version-batch:Invoice:{firstInvoiceId:N}",
            MutationCreatedAtUtc = now.AddMinutes(-1)
        };
        InvoiceDto CreateSecondVersion() => new()
        {
            Id = secondInvoiceId,
            CustomerId = customerId,
            CustomerName = "Reverse version batch customer",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "REVERSE-BATCH-0002",
            VersionGroupId = versionGroupId,
            VersionNumber = 2,
            PreviousVersionId = firstInvoiceId,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 7, 31),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            MutationId =
                $"reverse-version-batch:Invoice:{secondInvoiceId:N}",
            MutationCreatedAtUtc = now
        };
        SyncPushRequest CreateRequest() => new()
        {
            DeviceId = "reverse-version-batch-device",
            Invoices =
            [
                CreateSecondVersion(),
                CreateFirstVersion()
            ]
        };

        var admin = CreateInventoryDeliveryAdminUser(
            "admin-reverse-version-batch",
            OfficeCodeCatalog.Usenet);
        await using var dbContext = CreateDbContext(admin);
        var firstResult = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(
                (await CreateController(dbContext, admin)
                    .Push(
                        CreateRequest(),
                        CancellationToken.None))
                .Result)
            .Value);
        Assert.Equal(2, firstResult.AcceptedCount);
        Assert.Equal(0, firstResult.ConflictCount);
        Assert.Equal(0, firstResult.DuplicateMutationCount);

        dbContext.ChangeTracker.Clear();
        var firstRevisions = await dbContext.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(invoice =>
                invoice.Id == firstInvoiceId ||
                invoice.Id == secondInvoiceId)
            .ToDictionaryAsync(
                invoice => invoice.Id,
                invoice => invoice.Revision);
        Assert.False(
            await dbContext.Invoices
                .IgnoreQueryFilters()
                .Where(invoice => invoice.Id == firstInvoiceId)
                .Select(invoice => invoice.IsLatestVersion)
                .SingleAsync());
        Assert.True(
            await dbContext.Invoices
                .IgnoreQueryFilters()
                .Where(invoice => invoice.Id == secondInvoiceId)
                .Select(invoice => invoice.IsLatestVersion)
                .SingleAsync());

        var replayResult = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(
                (await CreateController(dbContext, admin)
                    .Push(
                        CreateRequest(),
                        CancellationToken.None))
                .Result)
            .Value);
        Assert.Equal(2, replayResult.AcceptedCount);
        Assert.Equal(0, replayResult.ConflictCount);
        Assert.Equal(2, replayResult.DuplicateMutationCount);

        dbContext.ChangeTracker.Clear();
        var replayRevisions = await dbContext.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(invoice =>
                invoice.Id == firstInvoiceId ||
                invoice.Id == secondInvoiceId)
            .ToDictionaryAsync(
                invoice => invoice.Id,
                invoice => invoice.Revision);
        Assert.Equal(firstRevisions, replayRevisions);
        Assert.DoesNotContain(Guid.Empty, replayRevisions.Keys);
    }

    [Fact]
    public async Task Push_NewDeletedInvoiceTombstone_CannotJoinOrNormalizeForeignTenantVersionGroup()
    {
        var localCustomerId = Guid.NewGuid();
        var foreignCustomerId = Guid.NewGuid();
        var foreignVersionGroupId = Guid.NewGuid();
        var foreignActiveInvoiceId = Guid.NewGuid();
        var foreignDeletedInvoiceId = Guid.NewGuid();
        var tombstoneId = Guid.NewGuid();
        var now = new DateTime(2026, 7, 31, 4, 30, 0, DateTimeKind.Utc);
        long foreignActiveRevision;
        long foreignDeletedRevision;

        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            seedDb.Customers.AddRange(
                new Customer
                {
                    Id = localCustomerId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    NameOriginal = "Local tombstone customer",
                    NameMatchKey = "LOCALTOMBSTONECUSTOMER",
                    TradeType = CustomerClassificationNormalizer.Sales
                },
                new Customer
                {
                    Id = foreignCustomerId,
                    TenantCode = TenantScopeCatalog.Itworld,
                    OfficeCode = OfficeCodeCatalog.Itworld,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                    NameOriginal = "Foreign version group customer",
                    NameMatchKey = "FOREIGNVERSIONGROUPCUSTOMER",
                    TradeType = CustomerClassificationNormalizer.Sales
                });
            var foreignActiveInvoice = new Invoice
            {
                Id = foreignActiveInvoiceId,
                CustomerId = foreignCustomerId,
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                InvoiceNumber = "FOREIGN-ACTIVE-0001",
                VersionGroupId = foreignVersionGroupId,
                VersionNumber = 1,
                IsLatestVersion = false,
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026, 7, 31),
                UpdatedAtUtc = now.AddMinutes(-2)
            };
            var foreignDeletedInvoice = new Invoice
            {
                Id = foreignDeletedInvoiceId,
                CustomerId = foreignCustomerId,
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                InvoiceNumber = "FOREIGN-DELETED-0002",
                VersionGroupId = foreignVersionGroupId,
                VersionNumber = 2,
                PreviousVersionId = foreignActiveInvoiceId,
                IsLatestVersion = true,
                IsDeleted = true,
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026, 7, 31),
                UpdatedAtUtc = now.AddMinutes(-1)
            };
            seedDb.Invoices.AddRange(foreignActiveInvoice, foreignDeletedInvoice);
            await seedDb.SaveChangesAsync();
            foreignActiveRevision = foreignActiveInvoice.Revision;
            foreignDeletedRevision = foreignDeletedInvoice.Revision;
        }

        InvoiceDto CreateForeignGroupTombstone() => new()
        {
            Id = tombstoneId,
            CustomerId = localCustomerId,
            CustomerName = "Local tombstone customer",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "LOCAL-TOMBSTONE-0001",
            VersionGroupId = foreignVersionGroupId,
            VersionNumber = 99,
            IsLatestVersion = true,
            IsDeleted = true,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 7, 31),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            MutationId = $"foreign-group-tombstone:Invoice:{tombstoneId:N}",
            MutationCreatedAtUtc = now
        };
        var admin = CreateInventoryDeliveryAdminUser(
            "admin-foreign-group-tombstone",
            OfficeCodeCatalog.Usenet);
        await using var dbContext = CreateDbContext(admin);
        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(
                (await CreateController(dbContext, admin)
                    .Push(
                        new SyncPushRequest
                        {
                            DeviceId = "foreign-group-tombstone-device",
                            Invoices = [CreateForeignGroupTombstone()]
                        },
                        CancellationToken.None))
                .Result)
            .Value);

        Assert.Equal(1, result.AcceptedCount);
        Assert.Equal(0, result.ConflictCount);
        dbContext.ChangeTracker.Clear();
        var foreignVersions = await dbContext.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(invoice =>
                invoice.Id == foreignActiveInvoiceId ||
                invoice.Id == foreignDeletedInvoiceId)
            .ToDictionaryAsync(invoice => invoice.Id);
        Assert.False(foreignVersions[foreignActiveInvoiceId].IsLatestVersion);
        Assert.Equal(
            foreignActiveRevision,
            foreignVersions[foreignActiveInvoiceId].Revision);
        Assert.True(foreignVersions[foreignDeletedInvoiceId].IsLatestVersion);
        Assert.Equal(
            foreignDeletedRevision,
            foreignVersions[foreignDeletedInvoiceId].Revision);

        var storedTombstone = await dbContext.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(invoice => invoice.Id == tombstoneId);
        Assert.True(storedTombstone.IsDeleted);
        Assert.False(storedTombstone.IsLatestVersion);
        Assert.Equal(tombstoneId, storedTombstone.VersionGroupId);
        Assert.Equal(TenantScopeCatalog.UsenetGroup, storedTombstone.TenantCode);
        Assert.Equal(OfficeCodeCatalog.Usenet, storedTombstone.OfficeCode);

        var replayResult = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(
                (await CreateController(dbContext, admin)
                    .Push(
                        new SyncPushRequest
                        {
                            DeviceId = "foreign-group-tombstone-device",
                            Invoices = [CreateForeignGroupTombstone()]
                        },
                        CancellationToken.None))
                .Result)
            .Value);
        Assert.Equal(1, replayResult.AcceptedCount);
        Assert.Equal(0, replayResult.ConflictCount);
        Assert.Equal(1, replayResult.DuplicateMutationCount);

        dbContext.ChangeTracker.Clear();
        var replayedTombstone = await dbContext.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(invoice => invoice.Id == tombstoneId);
        Assert.Equal(storedTombstone.Revision, replayedTombstone.Revision);
        Assert.Equal(tombstoneId, replayedTombstone.VersionGroupId);
        Assert.False(replayedTombstone.IsLatestVersion);
        Assert.Equal(
            foreignActiveRevision,
            await dbContext.Invoices
                .IgnoreQueryFilters()
                .Where(invoice => invoice.Id == foreignActiveInvoiceId)
                .Select(invoice => invoice.Revision)
                .SingleAsync());
        Assert.Equal(
            foreignDeletedRevision,
            await dbContext.Invoices
                .IgnoreQueryFilters()
                .Where(invoice => invoice.Id == foreignDeletedInvoiceId)
                .Select(invoice => invoice.Revision)
                .SingleAsync());
    }

    [Fact]
    public async Task Push_ExactTransferReplaySharingMutationIdWithDifferentTransfer_FailsClosedForEntireCollisionGroup()
    {
        var firstItemId = Guid.NewGuid();
        var secondItemId = Guid.NewGuid();
        var firstTransferId = Guid.NewGuid();
        var secondTransferId = Guid.NewGuid();
        var now = new DateTime(2026, 7, 31, 2, 55, 0, DateTimeKind.Utc);
        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            seedDb.Items.AddRange(
                CreateStockItem(
                    firstItemId,
                    "Exact collision replay first item",
                    currentStock: 10m),
                CreateStockItem(
                    secondItemId,
                    "Exact collision replay second item",
                    currentStock: 10m));
            seedDb.ItemWarehouseStocks.AddRange(
                new ItemWarehouseStock
                {
                    ItemId = firstItemId,
                    WarehouseCode =
                        OfficeCodeCatalog.UsenetMainWarehouse,
                    Quantity = 10m,
                    UpdatedAtUtc = now.AddMinutes(-5)
                },
                new ItemWarehouseStock
                {
                    ItemId = secondItemId,
                    WarehouseCode =
                        OfficeCodeCatalog.UsenetMainWarehouse,
                    Quantity = 10m,
                    UpdatedAtUtc = now.AddMinutes(-5)
                });
            await seedDb.SaveChangesAsync();
        }

        const string sharedMutationId =
            "exact-transfer-collision-replay:shared";
        var admin = CreateInventoryDeliveryAdminUser(
            "admin-exact-transfer-collision-replay",
            OfficeCodeCatalog.Usenet);
        var exactReplay = BuildPendingTransferDto(
            firstTransferId,
            firstItemId,
            "Exact collision replay first item",
            quantity: 2m,
            username: admin.Username,
            now: now,
            mutationPrefix: "exact-transfer-collision-replay");
        exactReplay.MutationId = sharedMutationId;

        await using (var firstPushDb = CreateDbContext(admin))
        {
            var firstResponse =
                await CreateController(firstPushDb, admin)
                    .Push(
                        new SyncPushRequest
                        {
                            DeviceId =
                                "exact-transfer-collision-replay-device",
                            InventoryTransfers = [exactReplay]
                        },
                        CancellationToken.None);
            var firstResult = Assert.IsType<SyncPushResult>(
                Assert.IsType<OkObjectResult>(
                    firstResponse.Result).Value);
            Assert.Equal(1, firstResult.AcceptedCount);
            Assert.Equal(0, firstResult.ConflictCount);
        }

        var conflictingTransfer = BuildPendingTransferDto(
            secondTransferId,
            secondItemId,
            "Exact collision replay second item",
            quantity: 2m,
            username: admin.Username,
            now: now.AddMinutes(1),
            mutationPrefix: "exact-transfer-collision-replay");
        conflictingTransfer.MutationId = sharedMutationId;

        await using var replayDb = CreateDbContext(admin);
        var response = await CreateController(replayDb, admin)
            .Push(
                new SyncPushRequest
                {
                    DeviceId =
                        "exact-transfer-collision-replay-device",
                    InventoryTransfers =
                    [
                        exactReplay,
                        conflictingTransfer
                    ]
                },
                CancellationToken.None);

        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(0, result.DuplicateMutationCount);
        Assert.Equal(2, result.ConflictCount);
        Assert.All(
            result.Conflicts,
            conflict => Assert.Contains(
                "Mutation id is reused",
                conflict.Reason,
                StringComparison.OrdinalIgnoreCase));

        replayDb.ChangeTracker.Clear();
        Assert.True(
            await replayDb.InventoryTransfers
                .IgnoreQueryFilters()
                .AnyAsync(current => current.Id == firstTransferId));
        Assert.False(
            await replayDb.InventoryTransfers
                .IgnoreQueryFilters()
                .AnyAsync(current => current.Id == secondTransferId));
        Assert.Equal(
            8m,
            await replayDb.ItemWarehouseStocks
                .Where(stock =>
                    stock.ItemId == firstItemId &&
                    stock.WarehouseCode ==
                    OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => stock.Quantity)
                .SingleAsync());
        Assert.Equal(
            10m,
            await replayDb.ItemWarehouseStocks
                .Where(stock =>
                    stock.ItemId == secondItemId &&
                    stock.WarehouseCode ==
                    OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => stock.Quantity)
                .SingleAsync());
    }

    [Fact]
    public async Task Push_CompleteStockMarkerAndInvoice_DoNotApplyOmittedStockTwice()
    {
        var itemId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var now = new DateTime(2026, 7, 31, 2, 58, 0, DateTimeKind.Utc);
        long stockRevision;
        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            seedDb.Items.Add(
                CreateStockItem(
                    itemId,
                    "Complete marker invoice item",
                    currentStock: 10m));
            seedDb.Customers.Add(new Customer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Complete marker invoice customer",
                NameMatchKey = "COMPLETEMARKERINVOICECUSTOMER",
                TradeType = "Sales"
            });
            var stock = new ItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 10m,
                UpdatedAtUtc = now.AddMinutes(-5)
            };
            seedDb.ItemWarehouseStocks.Add(stock);
            await seedDb.SaveChangesAsync();
            stockRevision = stock.Revision;
        }

        var admin = CreateInventoryDeliveryAdminUser(
            "admin-complete-marker-invoice",
            OfficeCodeCatalog.Usenet);
        var invoice = BuildInventoryInvoiceDto(
            invoiceId,
            customerId,
            itemId,
            "Complete marker invoice item",
            VoucherType.Sales,
            quantity: 10m,
            username: admin.Username,
            now: now);

        await using var dbContext = CreateDbContext(admin);
        var response = await CreateController(dbContext, admin)
            .Push(
                new SyncPushRequest
                {
                    DeviceId = "complete-marker-invoice-device",
                    ItemWarehouseStockSnapshotMarkers =
                    [
                        new ItemWarehouseStockSnapshotMarkerDto
                        {
                            ItemId = itemId,
                            MaxKnownRevision = stockRevision
                        }
                    ],
                    Invoices = [invoice]
                },
                CancellationToken.None);

        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(1, result.AcceptedCount);
        Assert.Equal(0, result.ConflictCount);

        dbContext.ChangeTracker.Clear();
        Assert.Equal(
            0m,
            await dbContext.ItemWarehouseStocks
                .Where(stock =>
                    stock.ItemId == itemId &&
                    stock.WarehouseCode ==
                    OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => stock.Quantity)
                .SingleAsync());
        Assert.Equal(
            0m,
            await dbContext.Items
                .IgnoreQueryFilters()
                .Where(item => item.Id == itemId)
                .Select(item => item.CurrentStock)
                .SingleAsync());
    }

    [Fact]
    public async Task Push_ExactTransferReplayAfterPurge_RequiresOriginalRouteScope()
    {
        var itemId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        var now = new DateTime(2026, 7, 31, 3, 0, 0, DateTimeKind.Utc);
        var dto = new InventoryTransferDto
        {
            Id = transferId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Usenet,
            TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
            TransferNumber = $"TR-PURGED-{transferId:N}"[..24],
            TransferDate = new DateOnly(2026, 7, 31),
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            TransferStatus = InventoryTransferStatusNormalizer.Pending,
            CreatedByUsername = "usenet-purged-source",
            RequestedByUsername = "usenet-purged-source",
            RequestedAtUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            LastSavedByUsername = "usenet-purged-source",
            LastSavedAtUtc = now,
            MutationId =
                $"purged-transfer-replay:InventoryTransfer:{transferId:N}",
            MutationCreatedAtUtc = now,
            Lines =
            [
                new InventoryTransferLineDto
                {
                    Id = Guid.NewGuid(),
                    TransferId = transferId,
                    ItemId = itemId,
                    ItemNameOriginal = "Purged exact replay item",
                    Unit = "EA",
                    Quantity = 1m
                }
            ]
        };
        Guid historicalConflictId;
        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            seedDb.Items.Add(
                CreateStockItem(
                    itemId,
                    "Purged exact replay item",
                    currentStock: 10m));
            seedDb.RecycleBinPurgeRecords.Add(new RecycleBinPurgeRecord
            {
                Kind = "inventory-transfer",
                EntityId = transferId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Shared,
                SourceOfficeCode = OfficeCodeCatalog.Usenet,
                TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
                PurgedAtUtc = now.AddMinutes(1)
            });
            seedDb.ProcessedSyncMutations.Add(new ProcessedSyncMutation
            {
                MutationId = dto.MutationId!,
                DeviceId = "purged-transfer-original-device",
                EntityName = nameof(InventoryTransfer),
                EntityId = transferId.ToString("D"),
                ExpectedRevision = dto.ExpectedRevision,
                PayloadHash = SyncMutationPayloadHasher.Compute(dto),
                ProcessedAtUtc = now
            });
            var historicalConflict = new ConflictLog
            {
                Username = "usenet-purged-source",
                EntityName = nameof(InventoryTransfer),
                EntityId = transferId.ToString("D"),
                ClientJson = "{}",
                ServerJson = "{}",
                Reason = "historical transfer conflict",
                Status = "Open",
                CreatedAtUtc = now
            };
            seedDb.ConflictLogs.Add(historicalConflict);
            await seedDb.SaveChangesAsync();
            historicalConflictId = historicalConflict.Id;
        }

        var targetOnlyUser = CreateDeliveryUser(
            "yeonsu-purged-replay-target",
            OfficeCodeCatalog.Yeonsu);
        await using var dbContext = CreateDbContext(targetOnlyUser);
        var response = await CreateController(dbContext, targetOnlyUser)
            .Push(
                new SyncPushRequest
                {
                    DeviceId = "purged-transfer-replay-attacker",
                    InventoryTransfers = [dto]
                },
                CancellationToken.None);

        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(0, result.DuplicateMutationCount);
        Assert.Contains(
            result.Conflicts,
            conflict =>
                conflict.EntityName == nameof(InventoryTransfer) &&
                conflict.EntityId == transferId.ToString("D"));

        dbContext.ChangeTracker.Clear();
        Assert.False(
            await dbContext.InventoryTransfers
                .IgnoreQueryFilters()
                .AnyAsync(transfer => transfer.Id == transferId));
        Assert.False(
            await dbContext.InventoryLedgerEntries
                .AnyAsync(entry => entry.SourceDocumentId == transferId));
        Assert.Equal(
            "Open",
            await dbContext.ConflictLogs
                .Where(conflict => conflict.Id == historicalConflictId)
                .Select(conflict => conflict.Status)
                .SingleAsync());
    }

    [Fact]
    public async Task Push_SourceMutationForPurgedInventoryTransfer_AcknowledgesWithoutRecreatingState()
    {
        var itemId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        var now = new DateTime(2026, 8, 2, 4, 0, 0, DateTimeKind.Utc);
        long purgeRevision;
        RecycleBinPurgeRecordDto expectedPurgeRecord;
        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            seedDb.Items.Add(
                CreateStockItem(
                    itemId,
                    "Purged prior transfer item",
                    currentStock: 10m));
            seedDb.ItemWarehouseStocks.AddRange(
                new ItemWarehouseStock
                {
                    ItemId = itemId,
                    WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                    Quantity = 10m,
                    UpdatedAtUtc = now.AddMinutes(-10)
                },
                new ItemWarehouseStock
                {
                    ItemId = itemId,
                    WarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                    Quantity = 0m,
                    UpdatedAtUtc = now.AddMinutes(-10)
                });
            var purgeRecord = new RecycleBinPurgeRecord
            {
                Kind = "inventory-transfer",
                EntityId = transferId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Shared,
                SourceOfficeCode = OfficeCodeCatalog.Usenet,
                TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
                UpdatedAtUtc = now,
                PurgedAtUtc = now
            };
            seedDb.RecycleBinPurgeRecords.Add(purgeRecord);
            await seedDb.SaveChangesAsync();
            purgeRevision = purgeRecord.Revision;
            expectedPurgeRecord = purgeRecord.ToDto();
        }

        var sourceUser = CreateInventoryDeliveryAdminUser(
            "purged-transfer-source",
            OfficeCodeCatalog.Usenet);
        var dto = BuildPendingTransferDto(
            transferId,
            itemId,
            "Purged prior transfer item",
            quantity: 2m,
            username: sourceUser.Username,
            now: now.AddMinutes(-5),
            mutationPrefix: "purged-prior-transfer");
        dto.Revision = purgeRevision;
        dto.ExpectedRevision = purgeRevision;

        await using var dbContext = CreateDbContext(sourceUser);
        SyncPushRequest CreateRequest() => new()
        {
            DeviceId = "purged-prior-transfer-device",
            ItemWarehouseStocks =
            [
                new ItemWarehouseStockDto
                {
                    ItemId = itemId,
                    WarehouseCode =
                        OfficeCodeCatalog.UsenetMainWarehouse,
                    Quantity = 8m,
                    UpdatedAtUtc = now.AddMinutes(-5)
                }
            ],
            InventoryTransfers = [dto]
        };

        var firstResult = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(
                (await CreateController(dbContext, sourceUser)
                    .Push(CreateRequest(), CancellationToken.None))
                .Result)
            .Value);
        Assert.Equal(1, firstResult.AcceptedCount);
        Assert.Equal(0, firstResult.ConflictCount);
        Assert.Equal(0, firstResult.DuplicateMutationCount);
        Assert.Empty(firstResult.AcceptedItemWarehouseStockKeys);
        var purgeAcceptedRevision = Assert.Single(
            firstResult.AcceptedRevisions,
            revision =>
                revision.EntityName == nameof(InventoryTransfer) &&
                revision.EntityId == transferId &&
                revision.Revision == purgeRevision);
        Assert.True(purgeAcceptedRevision.IsDeleted);
        var firstPurgeReceipt = Assert.Single(firstResult.PurgeRecords);
        AssertInventoryTransferPurgeReceiptEqual(
            expectedPurgeRecord,
            firstPurgeReceipt);
        Assert.Contains(
            firstResult.Notices,
            notice =>
                notice.EntityId == transferId.ToString("D") &&
                notice.Code == "inventory-transfer-purged-mutation-noop");

        dbContext.ChangeTracker.Clear();
        Assert.False(
            await dbContext.InventoryTransfers.IgnoreQueryFilters()
                .AnyAsync(transfer => transfer.Id == transferId));
        Assert.False(
            await dbContext.InventoryTransferLines.IgnoreQueryFilters()
                .AnyAsync(line => line.TransferId == transferId));
        Assert.False(
            await dbContext.InventoryLedgerEntries
                .AnyAsync(entry => entry.SourceDocumentId == transferId));
        Assert.Equal(
            10m,
            await dbContext.ItemWarehouseStocks
                .Where(stock =>
                    stock.ItemId == itemId &&
                    stock.WarehouseCode ==
                    OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => stock.Quantity)
                .SingleAsync());
        Assert.Equal(
            0m,
            await dbContext.ItemWarehouseStocks
                .Where(stock =>
                    stock.ItemId == itemId &&
                    stock.WarehouseCode ==
                    OfficeCodeCatalog.YeonsuMainWarehouse)
                .Select(stock => stock.Quantity)
                .SingleAsync());

        var replayResult = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(
                (await CreateController(dbContext, sourceUser)
                    .Push(CreateRequest(), CancellationToken.None))
                .Result)
            .Value);
        Assert.Equal(1, replayResult.AcceptedCount);
        Assert.Equal(0, replayResult.ConflictCount);
        Assert.Equal(1, replayResult.DuplicateMutationCount);
        var replayPurgeReceipt = Assert.Single(replayResult.PurgeRecords);
        AssertInventoryTransferPurgeReceiptEqual(
            firstPurgeReceipt,
            replayPurgeReceipt);

        dbContext.ChangeTracker.Clear();
        Assert.False(
            await dbContext.InventoryTransfers.IgnoreQueryFilters()
                .AnyAsync(transfer => transfer.Id == transferId));
        Assert.False(
            await dbContext.InventoryTransferLines.IgnoreQueryFilters()
                .AnyAsync(line => line.TransferId == transferId));
        Assert.False(
            await dbContext.InventoryLedgerEntries
                .AnyAsync(entry => entry.SourceDocumentId == transferId));

        var newerDto = BuildPendingTransferDto(
            transferId,
            itemId,
            "Purged prior transfer item",
            quantity: 3m,
            username: sourceUser.Username,
            now: now.AddMinutes(5),
            mutationPrefix: "purged-newer-transfer");
        newerDto.Revision = purgeRevision + 1;
        newerDto.ExpectedRevision = purgeRevision + 1;
        var newerResult = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(
                (await CreateController(dbContext, sourceUser)
                    .Push(
                        new SyncPushRequest
                        {
                            DeviceId = "purged-newer-transfer-device",
                            InventoryTransfers = [newerDto]
                        },
                        CancellationToken.None))
                .Result)
            .Value);
        Assert.Equal(0, newerResult.AcceptedCount);
        Assert.Equal(1, newerResult.ConflictCount);
        Assert.Empty(newerResult.PurgeRecords);
        Assert.Contains(
            newerResult.Conflicts,
            conflict =>
                conflict.EntityName == nameof(InventoryTransfer) &&
                conflict.EntityId == transferId.ToString("D") &&
                conflict.Reason.Contains(
                    "purge record",
                    StringComparison.OrdinalIgnoreCase));

        dbContext.ChangeTracker.Clear();
        Assert.False(
            await dbContext.InventoryTransfers.IgnoreQueryFilters()
                .AnyAsync(transfer => transfer.Id == transferId));
        Assert.False(
            await dbContext.InventoryTransferLines.IgnoreQueryFilters()
                .AnyAsync(line => line.TransferId == transferId));
        Assert.False(
            await dbContext.InventoryLedgerEntries
                .AnyAsync(entry => entry.SourceDocumentId == transferId));
    }

    [Fact]
    public async Task Push_MissingDeletedInventoryTransferWithOversizedActiveLine_AcknowledgesWithoutPersistingTombstone()
    {
        var transferId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var now = new DateTime(2026, 8, 2, 4, 5, 0, DateTimeKind.Utc);
        var sourceUser = CreateInventoryDeliveryAdminUser(
            "missing-transfer-delete-source",
            OfficeCodeCatalog.Usenet);
        var dto = BuildPendingTransferDto(
            transferId,
            itemId,
            "Missing deleted transfer item",
            quantity: decimal.MaxValue,
            username: sourceUser.Username,
            now: now,
            mutationPrefix: "missing-transfer-delete");
        dto.IsDeleted = true;
        dto.Revision = 42;
        dto.ExpectedRevision = 42;

        await using var dbContext = CreateDbContext(sourceUser);
        SyncPushRequest CreateRequest() => new()
        {
            DeviceId = "missing-transfer-delete-device",
            InventoryTransfers = [dto]
        };

        var firstResult = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(
                (await CreateController(dbContext, sourceUser)
                    .Push(CreateRequest(), CancellationToken.None))
                .Result)
            .Value);
        Assert.Equal(1, firstResult.AcceptedCount);
        Assert.Equal(0, firstResult.ConflictCount);
        Assert.Equal(0, firstResult.DuplicateMutationCount);
        var missingDeleteAcceptedRevision = Assert.Single(
            firstResult.AcceptedRevisions,
            revision =>
                revision.EntityName == nameof(InventoryTransfer) &&
                revision.EntityId == transferId &&
                revision.Revision == 42);
        Assert.True(missingDeleteAcceptedRevision.IsDeleted);
        Assert.Empty(firstResult.PurgeRecords);

        dbContext.ChangeTracker.Clear();
        Assert.False(
            await dbContext.InventoryTransfers.IgnoreQueryFilters()
                .AnyAsync(transfer => transfer.Id == transferId));
        Assert.False(
            await dbContext.InventoryTransferLines.IgnoreQueryFilters()
                .AnyAsync(line => line.TransferId == transferId));
        Assert.False(
            await dbContext.InventoryLedgerEntries
                .AnyAsync(entry => entry.SourceDocumentId == transferId));

        var replayResult = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(
                (await CreateController(dbContext, sourceUser)
                    .Push(CreateRequest(), CancellationToken.None))
                .Result)
            .Value);
        Assert.Equal(1, replayResult.AcceptedCount);
        Assert.Equal(0, replayResult.ConflictCount);
        Assert.Equal(1, replayResult.DuplicateMutationCount);
        Assert.Empty(replayResult.PurgeRecords);

        dbContext.ChangeTracker.Clear();
        Assert.False(
            await dbContext.InventoryTransfers.IgnoreQueryFilters()
                .AnyAsync(transfer => transfer.Id == transferId));
        Assert.False(
            await dbContext.InventoryTransferLines.IgnoreQueryFilters()
                .AnyAsync(line => line.TransferId == transferId));
    }

    [Fact]
    public async Task Push_NewReceivedInventoryTransfer_PreservesFinalStatusAndCanonicalizesAuditAndStock()
    {
        var itemId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        var authenticatedUser = CreateInventoryDeliveryAdminUser(
            "authenticated-new-receiver",
            OfficeCodeCatalog.Yeonsu);
        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            seedDb.Items.Add(CreateStockItem(itemId, "New received transfer item", currentStock: 10m));
            seedDb.ItemWarehouseStocks.Add(new ItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 10m,
                UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-5),
                Revision = 10
            });
            await seedDb.SaveChangesAsync();
        }

        await using var scopedDb = CreateDbContext(authenticatedUser);
        var controller = CreateController(scopedDb, authenticatedUser);
        var spoofedUtc = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var beforePushUtc = DateTime.UtcNow;
        var response = await controller.Push(new SyncPushRequest
        {
            DeviceId = "new-received-transfer",
            InventoryTransfers =
            [
                new InventoryTransferDto
                {
                    Id = transferId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    SourceOfficeCode = OfficeCodeCatalog.Usenet,
                    TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
                    TransferNumber = "TR-NEW-RECEIVED",
                    TransferDate = new DateOnly(2026, 8, 2),
                    FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                    ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                    TransferStatus = InventoryTransferStatusNormalizer.Received,
                    CreatedByUsername = "spoofed-creator",
                    CreatedAtUtc = spoofedUtc,
                    RequestedByUsername = "spoofed-requester",
                    RequestedAtUtc = spoofedUtc,
                    LastSavedByUsername = "spoofed-saver",
                    LastSavedAtUtc = spoofedUtc,
                    LastStatusChangedByUsername = "spoofed-status-actor",
                    LastStatusChangedAtUtc = spoofedUtc,
                    ReceivedByUsername = "spoofed-receiver",
                    ReceivedAtUtc = spoofedUtc,
                    ReceiveMemo = "validated receive memo",
                    ReceiveEvidencePath = "client-owned-evidence.pdf",
                    RejectedByUsername = "spoofed-rejector",
                    RejectedAtUtc = spoofedUtc,
                    RejectReason = "spoofed opposite status",
                    UpdatedAtUtc = spoofedUtc,
                    MutationId = $"new-received-transfer:InventoryTransfer:{transferId:N}",
                    MutationCreatedAtUtc = spoofedUtc,
                    Lines =
                    [
                        new InventoryTransferLineDto
                        {
                            Id = lineId,
                            TransferId = transferId,
                            ItemId = itemId,
                            ItemNameOriginal = "New received transfer item",
                            Unit = "EA",
                            Quantity = 2m,
                            ReceivedQuantity = 1.5m,
                            QuantityDifference = 999m,
                            Remark = "source remark",
                            ReceiptRemark = "validated receipt remark"
                        }
                    ]
                }
            ]
        }, CancellationToken.None);
        var afterPushUtc = DateTime.UtcNow;

        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(1, result.AcceptedCount);
        Assert.Equal(0, result.ConflictCount);
        scopedDb.ChangeTracker.Clear();
        var stored = await scopedDb.InventoryTransfers
            .IgnoreQueryFilters()
            .Include(transfer => transfer.Lines)
            .SingleAsync(transfer => transfer.Id == transferId);
        Assert.Equal(InventoryTransferStatusNormalizer.Received, stored.TransferStatus);
        Assert.Equal(authenticatedUser.Username, stored.CreatedByUsername);
        Assert.Equal(authenticatedUser.Username, stored.RequestedByUsername);
        Assert.Equal(authenticatedUser.Username, stored.LastSavedByUsername);
        Assert.Equal(authenticatedUser.Username, stored.LastStatusChangedByUsername);
        Assert.Equal(authenticatedUser.Username, stored.ReceivedByUsername);
        Assert.Empty(stored.RejectedByUsername);
        Assert.Null(stored.RejectedAtUtc);
        Assert.Empty(stored.RejectReason);
        Assert.Empty(stored.ReceiveEvidencePath);
        Assert.InRange(stored.CreatedAtUtc, beforePushUtc, afterPushUtc);
        Assert.InRange(stored.ReceivedAtUtc!.Value, beforePushUtc, afterPushUtc);
        Assert.Equal(stored.ReceivedAtUtc, stored.RequestedAtUtc);
        Assert.Equal(stored.ReceivedAtUtc, stored.LastSavedAtUtc);
        Assert.Equal(stored.ReceivedAtUtc, stored.LastStatusChangedAtUtc);
        var storedLine = Assert.Single(stored.Lines, line => !line.IsDeleted);
        Assert.Equal(1.5m, storedLine.ReceivedQuantity);
        Assert.Equal(-0.5m, storedLine.QuantityDifference);
        Assert.Equal("validated receipt remark", storedLine.ReceiptRemark);
        Assert.Equal(8m, await scopedDb.ItemWarehouseStocks
            .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
        Assert.Equal(1.5m, await scopedDb.ItemWarehouseStocks
            .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.YeonsuMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
    }

    [Fact]
    public async Task Push_NewRejectedInventoryTransfer_PreservesFinalStatusAndCanonicalizesOppositeAndReceiptFields()
    {
        var itemId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        var authenticatedUser = CreateInventoryDeliveryAdminUser(
            "authenticated-new-rejector",
            OfficeCodeCatalog.Yeonsu);
        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            seedDb.Items.Add(CreateStockItem(itemId, "New rejected transfer item", currentStock: 10m));
            seedDb.ItemWarehouseStocks.Add(new ItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 10m,
                UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-5),
                Revision = 11
            });
            await seedDb.SaveChangesAsync();
        }

        await using var scopedDb = CreateDbContext(authenticatedUser);
        var controller = CreateController(scopedDb, authenticatedUser);
        var spoofedUtc = new DateTime(2020, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        var beforePushUtc = DateTime.UtcNow;
        var response = await controller.Push(new SyncPushRequest
        {
            DeviceId = "new-rejected-transfer",
            InventoryTransfers =
            [
                new InventoryTransferDto
                {
                    Id = transferId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    SourceOfficeCode = OfficeCodeCatalog.Usenet,
                    TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
                    TransferNumber = "TR-NEW-REJECTED",
                    TransferDate = new DateOnly(2026, 8, 2),
                    FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                    ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                    TransferStatus = InventoryTransferStatusNormalizer.Rejected,
                    CreatedByUsername = "spoofed-creator",
                    CreatedAtUtc = spoofedUtc,
                    RequestedByUsername = "spoofed-requester",
                    RequestedAtUtc = spoofedUtc,
                    LastSavedByUsername = "spoofed-saver",
                    LastSavedAtUtc = spoofedUtc,
                    LastStatusChangedByUsername = "spoofed-status-actor",
                    LastStatusChangedAtUtc = spoofedUtc,
                    RejectedByUsername = "spoofed-rejector",
                    RejectedAtUtc = spoofedUtc,
                    RejectReason = "validated reject reason",
                    ReceivedByUsername = "spoofed-receiver",
                    ReceivedAtUtc = spoofedUtc,
                    ReceiveMemo = "spoofed opposite memo",
                    ReceiveEvidencePath = "client-owned-evidence.pdf",
                    UpdatedAtUtc = spoofedUtc,
                    MutationId = $"new-rejected-transfer:InventoryTransfer:{transferId:N}",
                    MutationCreatedAtUtc = spoofedUtc,
                    Lines =
                    [
                        new InventoryTransferLineDto
                        {
                            Id = lineId,
                            TransferId = transferId,
                            ItemId = itemId,
                            ItemNameOriginal = "New rejected transfer item",
                            Unit = "EA",
                            Quantity = 2m,
                            ReceivedQuantity = 0.5m,
                            QuantityDifference = -1.5m,
                            ReceiptRemark = "spoofed receipt remark"
                        }
                    ]
                }
            ]
        }, CancellationToken.None);
        var afterPushUtc = DateTime.UtcNow;

        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(1, result.AcceptedCount);
        Assert.Equal(0, result.ConflictCount);
        scopedDb.ChangeTracker.Clear();
        var stored = await scopedDb.InventoryTransfers
            .IgnoreQueryFilters()
            .Include(transfer => transfer.Lines)
            .SingleAsync(transfer => transfer.Id == transferId);
        Assert.Equal(InventoryTransferStatusNormalizer.Rejected, stored.TransferStatus);
        Assert.Equal(authenticatedUser.Username, stored.RejectedByUsername);
        Assert.Empty(stored.ReceivedByUsername);
        Assert.Null(stored.ReceivedAtUtc);
        Assert.Empty(stored.ReceiveMemo);
        Assert.Empty(stored.ReceiveEvidencePath);
        Assert.InRange(stored.RejectedAtUtc!.Value, beforePushUtc, afterPushUtc);
        Assert.Equal(stored.RejectedAtUtc, stored.LastStatusChangedAtUtc);
        var storedLine = Assert.Single(stored.Lines, line => !line.IsDeleted);
        Assert.Equal(2m, storedLine.ReceivedQuantity);
        Assert.Equal(0m, storedLine.QuantityDifference);
        Assert.Empty(storedLine.ReceiptRemark);
        Assert.Equal(10m, await scopedDb.ItemWarehouseStocks
            .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
        Assert.False(await scopedDb.ItemWarehouseStocks.AnyAsync(stock =>
            stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.YeonsuMainWarehouse));
    }

    [Fact]
    public async Task Push_RejectsDuplicateNonEmptyInventoryTransferLineIdWithinPayload()
    {
        var itemId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        var duplicateLineId = Guid.NewGuid();
        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            seedDb.Items.Add(CreateStockItem(itemId, "Duplicate line id item", currentStock: 10m));
            seedDb.ItemWarehouseStocks.Add(new ItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 10m,
                UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-5),
                Revision = 12
            });
            await seedDb.SaveChangesAsync();
        }

        var adminUser = CreateInventoryDeliveryAdminUser(
            "duplicate-line-id-admin",
            OfficeCodeCatalog.Usenet);
        await using var scopedDb = CreateDbContext(adminUser);
        var controller = CreateController(scopedDb, adminUser);
        var dto = BuildPendingTransferDto(
            transferId,
            itemId,
            "Duplicate line id item",
            1m,
            adminUser.Username,
            DateTime.UtcNow,
            "duplicate-line-id");
        dto.Lines =
        [
            new InventoryTransferLineDto
            {
                Id = duplicateLineId,
                TransferId = transferId,
                ItemId = itemId,
                ItemNameOriginal = "Duplicate line id item",
                Unit = "EA",
                Quantity = 1m
            },
            new InventoryTransferLineDto
            {
                Id = duplicateLineId,
                TransferId = transferId,
                ItemId = itemId,
                ItemNameOriginal = "Duplicate line id item",
                Unit = "EA",
                Quantity = 1m
            }
        ];

        var response = await controller.Push(new SyncPushRequest
        {
            DeviceId = "duplicate-line-id",
            InventoryTransfers = [dto]
        }, CancellationToken.None);

        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(0, result.AcceptedCount);
        Assert.Contains(result.Conflicts, conflict =>
            conflict.EntityName == nameof(InventoryTransfer) &&
            conflict.EntityId == transferId.ToString("D") &&
            conflict.Reason.Contains("duplicate", StringComparison.OrdinalIgnoreCase));
        scopedDb.ChangeTracker.Clear();
        Assert.False(await scopedDb.InventoryTransfers.IgnoreQueryFilters()
            .AnyAsync(transfer => transfer.Id == transferId));
    }

    [Fact]
    public async Task Push_RejectsInventoryTransferLineIdOwnedByDifferentTransfer()
    {
        var itemId = Guid.NewGuid();
        var existingTransferId = Guid.NewGuid();
        var newTransferId = Guid.NewGuid();
        var existingLineId = Guid.NewGuid();
        await SeedPendingTransferAsync(
            itemId,
            existingTransferId,
            existingLineId,
            "Foreign-owned line id item",
            sourceStockQuantity: 10m);

        var adminUser = CreateInventoryDeliveryAdminUser(
            "foreign-line-id-admin",
            OfficeCodeCatalog.Usenet);
        await using var scopedDb = CreateDbContext(adminUser);
        var controller = CreateController(scopedDb, adminUser);
        var dto = BuildPendingTransferDto(
            newTransferId,
            itemId,
            "Foreign-owned line id item",
            2m,
            adminUser.Username,
            DateTime.UtcNow,
            "foreign-line-id");
        var line = Assert.Single(dto.Lines);
        line.Id = existingLineId;
        line.TransferId = newTransferId;

        var response = await controller.Push(new SyncPushRequest
        {
            DeviceId = "foreign-line-id",
            InventoryTransfers = [dto]
        }, CancellationToken.None);

        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(0, result.AcceptedCount);
        Assert.Contains(result.Conflicts, conflict =>
            conflict.EntityName == nameof(InventoryTransfer) &&
            conflict.EntityId == newTransferId.ToString("D") &&
            conflict.Reason.Contains("different inventory transfer", StringComparison.OrdinalIgnoreCase));
        scopedDb.ChangeTracker.Clear();
        Assert.False(await scopedDb.InventoryTransfers.IgnoreQueryFilters()
            .AnyAsync(transfer => transfer.Id == newTransferId));
        Assert.True(await scopedDb.InventoryTransferLines.IgnoreQueryFilters()
            .AnyAsync(existingLine =>
                existingLine.Id == existingLineId &&
                existingLine.TransferId == existingTransferId));
    }

    [Theory]
    [InlineData("non-inventory")]
    [InlineData("deleted")]
    public async Task Push_ExactStockReceiptReplay_AcknowledgesAfterMutableItemStateChanges(
        string itemState)
    {
        var itemId = Guid.NewGuid();
        var now = new DateTime(2026, 7, 31, 3, 15, 0, DateTimeKind.Utc);
        long sourceStockRevision;
        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            seedDb.Items.Add(
                CreateStockItem(
                    itemId,
                    "Exact stock receipt mutable item",
                    currentStock: 5m));
            var sourceStock = new ItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 5m,
                UpdatedAtUtc = now.AddMinutes(-5)
            };
            seedDb.ItemWarehouseStocks.Add(sourceStock);
            await seedDb.SaveChangesAsync();
            sourceStockRevision = sourceStock.Revision;
        }

        var stockDto = new ItemWarehouseStockDto
        {
            ItemId = itemId,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 4m,
            UpdatedAtUtc = now,
            Revision = sourceStockRevision,
            ExpectedRevision = sourceStockRevision
        };
        const string deviceId =
            "exact-stock-receipt-mutable-item-device";
        var admin = CreateInventoryDeliveryAdminUser(
            "admin-exact-stock-receipt-mutable-item",
            OfficeCodeCatalog.Usenet);
        await using (var firstPushDb = CreateDbContext(admin))
        {
            var firstResult = Assert.IsType<SyncPushResult>(
                Assert.IsType<OkObjectResult>(
                    (await CreateController(firstPushDb, admin)
                        .Push(
                            new SyncPushRequest
                            {
                                DeviceId = deviceId,
                                ItemWarehouseStocks = [stockDto]
                            },
                            CancellationToken.None))
                        .Result)
                    .Value);
            Assert.Equal(0, firstResult.ConflictCount);
            Assert.Contains(
                firstResult.AcceptedItemWarehouseStockKeys,
                key =>
                    key.ItemId == itemId &&
                    key.WarehouseCode ==
                    OfficeCodeCatalog.UsenetMainWarehouse);
        }

        await using (var mutationDb = CreateDbContext(CreateAdminUser()))
        {
            var item = await mutationDb.Items
                .IgnoreQueryFilters()
                .SingleAsync(current => current.Id == itemId);
            if (itemState == "deleted")
                item.IsDeleted = true;
            else
                item.TrackingType = ItemTrackingTypes.NonStock;
            item.UpdatedAtUtc = now.AddMinutes(1);
            await mutationDb.SaveChangesAsync();
        }

        await using var replayDb = CreateDbContext(admin);
        var replayResult = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(
                (await CreateController(replayDb, admin)
                    .Push(
                        new SyncPushRequest
                        {
                            DeviceId = deviceId,
                            ItemWarehouseStocks = [stockDto]
                        },
                        CancellationToken.None))
                    .Result)
                .Value);
        Assert.Equal(0, replayResult.ConflictCount);
        Assert.Contains(
            replayResult.AcceptedItemWarehouseStockKeys,
            key =>
                key.ItemId == itemId &&
                key.WarehouseCode ==
                OfficeCodeCatalog.UsenetMainWarehouse);

        replayDb.ChangeTracker.Clear();
        Assert.Equal(
            4m,
            await replayDb.ItemWarehouseStocks
                .IgnoreQueryFilters()
                .Where(stock =>
                    stock.ItemId == itemId &&
                    stock.WarehouseCode ==
                    OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => stock.Quantity)
                .SingleAsync());
        Assert.Single(
            await replayDb.ProcessedSyncMutations
                .AsNoTracking()
                .Where(receipt =>
                    receipt.EntityName == nameof(ItemWarehouseStock) &&
                    receipt.EntityId ==
                    $"{itemId:D}|{OfficeCodeCatalog.UsenetMainWarehouse}")
                .ToListAsync());
    }

    [Fact]
    public async Task Push_ConfirmedPurchaseThenTransfer_UsesProjectedServerStockForClientHandledKey()
    {
        var itemId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        var now = new DateTime(2026, 7, 31, 3, 30, 0, DateTimeKind.Utc);
        long sourceStockRevision;
        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            seedDb.Items.Add(
                CreateStockItem(
                    itemId,
                    "Purchase then transfer item",
                    currentStock: 0m));
            seedDb.Customers.Add(new Customer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Purchase then transfer customer",
                NameMatchKey = "PURCHASETHENTRANSFERCUSTOMER",
                TradeType = "Purchase"
            });
            var sourceStock = new ItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 0m,
                UpdatedAtUtc = now.AddMinutes(-5)
            };
            seedDb.ItemWarehouseStocks.Add(sourceStock);
            await seedDb.SaveChangesAsync();
            sourceStockRevision = sourceStock.Revision;
        }

        var admin = CreateInventoryDeliveryAdminUser(
            "admin-purchase-then-transfer",
            OfficeCodeCatalog.Usenet);
        var invoice = BuildInventoryInvoiceDto(
            invoiceId,
            customerId,
            itemId,
            "Purchase then transfer item",
            VoucherType.Purchase,
            quantity: 5m,
            username: admin.Username,
            now: now);
        invoice.PurchaseReceivingRequired = true;
        invoice.PurchaseReceivingStatus =
            InvoiceReceivingStatuses.Confirmed;
        invoice.PurchaseReceivedAtUtc = now;
        invoice.PurchaseReceivedByUsername = admin.Username;
        var transfer = BuildPendingTransferDto(
            transferId,
            itemId,
            "Purchase then transfer item",
            quantity: 5m,
            username: admin.Username,
            now: now,
            mutationPrefix: "purchase-then-transfer");

        await using var dbContext = CreateDbContext(admin);
        var response = await CreateController(dbContext, admin)
            .Push(
                new SyncPushRequest
                {
                    DeviceId = "purchase-then-transfer-device",
                    ItemWarehouseStocks =
                    [
                        new ItemWarehouseStockDto
                        {
                            ItemId = itemId,
                            WarehouseCode =
                                OfficeCodeCatalog.UsenetMainWarehouse,
                            Quantity = 0m,
                            UpdatedAtUtc = now,
                            Revision = sourceStockRevision,
                            ExpectedRevision = sourceStockRevision
                        }
                    ],
                    Invoices = [invoice],
                    InventoryTransfers = [transfer]
                },
                CancellationToken.None);

        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(2, result.AcceptedCount);
        Assert.Equal(0, result.ConflictCount);
        Assert.Contains(
            result.AcceptedItemWarehouseStockKeys,
            key =>
                key.ItemId == itemId &&
                key.WarehouseCode ==
                OfficeCodeCatalog.UsenetMainWarehouse);

        dbContext.ChangeTracker.Clear();
        Assert.True(
            await dbContext.Invoices
                .IgnoreQueryFilters()
                .AnyAsync(current => current.Id == invoiceId));
        Assert.True(
            await dbContext.InventoryTransfers
                .IgnoreQueryFilters()
                .AnyAsync(current => current.Id == transferId));
        Assert.Equal(
            0m,
            await dbContext.ItemWarehouseStocks
                .Where(stock =>
                    stock.ItemId == itemId &&
                    stock.WarehouseCode ==
                    OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => stock.Quantity)
                .SingleAsync());
    }

    [Fact]
    public async Task Push_SaleThenTransferShortage_RollsBackInvoiceAndClientHandledStock()
    {
        var itemId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        var now = new DateTime(2026, 7, 31, 3, 45, 0, DateTimeKind.Utc);
        long sourceStockRevision;
        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            seedDb.Items.Add(
                CreateStockItem(
                    itemId,
                    "Sale then transfer shortage item",
                    currentStock: 10m));
            seedDb.Customers.Add(new Customer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Sale then transfer customer",
                NameMatchKey = "SALETHENTRANSFERCUSTOMER",
                TradeType = "Sales"
            });
            var sourceStock = new ItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 10m,
                UpdatedAtUtc = now.AddMinutes(-5)
            };
            seedDb.ItemWarehouseStocks.Add(sourceStock);
            await seedDb.SaveChangesAsync();
            sourceStockRevision = sourceStock.Revision;
        }

        var admin = CreateInventoryDeliveryAdminUser(
            "admin-sale-then-transfer",
            OfficeCodeCatalog.Usenet);
        var invoice = BuildInventoryInvoiceDto(
            invoiceId,
            customerId,
            itemId,
            "Sale then transfer shortage item",
            VoucherType.Sales,
            quantity: 6m,
            username: admin.Username,
            now: now);
        var transfer = BuildPendingTransferDto(
            transferId,
            itemId,
            "Sale then transfer shortage item",
            quantity: 5m,
            username: admin.Username,
            now: now,
            mutationPrefix: "sale-then-transfer");

        await using var dbContext = CreateDbContext(admin);
        var response = await CreateController(dbContext, admin)
            .Push(
                new SyncPushRequest
                {
                    DeviceId = "sale-then-transfer-device",
                    ItemWarehouseStocks =
                    [
                        new ItemWarehouseStockDto
                        {
                            ItemId = itemId,
                            WarehouseCode =
                                OfficeCodeCatalog.UsenetMainWarehouse,
                            Quantity = 4m,
                            UpdatedAtUtc = now,
                            Revision = sourceStockRevision,
                            ExpectedRevision = sourceStockRevision
                        }
                    ],
                    Invoices = [invoice],
                    InventoryTransfers = [transfer]
                },
                CancellationToken.None);

        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(0, result.AcceptedCount);
        Assert.Contains(
            result.Conflicts,
            conflict =>
                conflict.EntityName == nameof(InventoryTransfer) &&
                conflict.EntityId == transferId.ToString("D"));
        Assert.Empty(result.AcceptedItemWarehouseStockKeys);
        Assert.Empty(result.AcceptedRevisions);
        Assert.Empty(result.AssignedInvoiceNumbers);
        Assert.Empty(result.AssignedTaxInvoiceNumbers);
        Assert.Contains(
            result.Notices,
            notice =>
                notice.Code ==
                "inventory-transfer-stock-atomicity-rollback");

        dbContext.ChangeTracker.Clear();
        Assert.False(
            await dbContext.Invoices
                .IgnoreQueryFilters()
                .AnyAsync(current => current.Id == invoiceId));
        Assert.False(
            await dbContext.InventoryTransfers
                .IgnoreQueryFilters()
                .AnyAsync(current => current.Id == transferId));
        Assert.Equal(
            10m,
            await dbContext.ItemWarehouseStocks
                .Where(stock =>
                    stock.ItemId == itemId &&
                    stock.WarehouseCode ==
                    OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => stock.Quantity)
                .SingleAsync());
        Assert.DoesNotContain(
            await dbContext.ProcessedSyncMutations
                .AsNoTracking()
                .ToListAsync(),
            receipt =>
                receipt.EntityName == nameof(ItemWarehouseStock) &&
                receipt.EntityId ==
                $"{itemId:D}|{OfficeCodeCatalog.UsenetMainWarehouse}");
    }

    [Theory]
    [InlineData("receive-evidence")]
    [InlineData("created-by")]
    [InlineData("status-audit")]
    [InlineData("opposite-status")]
    [InlineData("deleted-line")]
    public async Task Push_RejectsInitialFinalStatusTransition_WhenLockedFieldChanges(string mutationKind)
    {
        var itemId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        await SeedPendingTransferAsync(
            itemId,
            transferId,
            lineId,
            $"Locked transition {mutationKind}",
            sourceStockQuantity: 8m);

        var targetUser = CreateDeliveryUser(
            $"yeonsu-locked-transition-{mutationKind}",
            OfficeCodeCatalog.Yeonsu);
        await using var scopedDb = CreateDbContext(targetUser);
        var controller = CreateController(scopedDb, targetUser);
        var existing = await scopedDb.InventoryTransfers
            .IgnoreQueryFilters()
            .Include(transfer => transfer.Lines)
            .SingleAsync(transfer => transfer.Id == transferId);
        var dto = BuildReceiptDto(existing, targetUser.Username, requestedQuantity: 2m);
        switch (mutationKind)
        {
            case "receive-evidence":
                dto.ReceiveEvidencePath = "inventory-transfers/client-overwrite.pdf";
                break;
            case "created-by":
                dto.CreatedByUsername = "spoofed-creator";
                break;
            case "status-audit":
                dto.LastSavedByUsername = "spoofed-auditor";
                dto.LastStatusChangedAtUtc = dto.ReceivedAtUtc!.Value.AddMinutes(1);
                break;
            case "opposite-status":
                dto.RejectedByUsername = "spoofed-rejector";
                dto.RejectedAtUtc = dto.ReceivedAtUtc!.Value;
                dto.RejectReason = "injected opposite status";
                break;
            case "deleted-line":
                dto.Lines.Add(new InventoryTransferLineDto
                {
                    Id = Guid.NewGuid(),
                    TransferId = transferId,
                    ItemNameOriginal = "injected deleted line",
                    Unit = "EA",
                    Quantity = 1m,
                    IsDeleted = true
                });
                break;
            default:
                throw new InvalidOperationException($"Unknown mutation kind: {mutationKind}");
        }
        dto.MutationId = $"locked-initial-transition:{mutationKind}:{transferId:N}";

        var response = await controller.Push(new SyncPushRequest
        {
            DeviceId = $"locked-initial-transition-{mutationKind}",
            InventoryTransfers = [dto]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(1, result.ConflictCount);
        scopedDb.ChangeTracker.Clear();
        var stored = await scopedDb.InventoryTransfers.IgnoreQueryFilters()
            .Include(transfer => transfer.Lines)
            .SingleAsync(transfer => transfer.Id == transferId);
        Assert.Equal(InventoryTransferStatusNormalizer.Pending, stored.TransferStatus);
        Assert.Equal("usenet-source", stored.CreatedByUsername);
        Assert.Empty(stored.ReceiveEvidencePath);
        Assert.Empty(stored.RejectedByUsername);
        Assert.Null(stored.RejectedAtUtc);
        Assert.Empty(stored.RejectReason);
        Assert.DoesNotContain(stored.Lines, line => line.ItemNameOriginal == "injected deleted line");
    }

    [Theory]
    [InlineData("transfer-number")]
    [InlineData("created-at")]
    [InlineData("created-by")]
    [InlineData("requested-audit")]
    [InlineData("receipt-audit")]
    [InlineData("receive-evidence")]
    [InlineData("reject-audit")]
    [InlineData("status-audit")]
    [InlineData("last-saved-actor")]
    public async Task Push_RejectsPendingSourceEdit_WhenLockedFieldChanges(string mutationKind)
    {
        var itemId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        await SeedPendingTransferAsync(
            itemId,
            transferId,
            lineId,
            $"Locked pending source edit {mutationKind}",
            sourceStockQuantity: 8m);

        var sourceUser = CreateDeliveryUser(
            $"usenet-locked-pending-{mutationKind}",
            OfficeCodeCatalog.Usenet);
        await using var scopedDb = CreateDbContext(sourceUser);
        var controller = CreateController(scopedDb, sourceUser);
        var existing = await scopedDb.InventoryTransfers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(transfer => transfer.Lines)
            .SingleAsync(transfer => transfer.Id == transferId);
        var originalLine = Assert.Single(existing.Lines, line => !line.IsDeleted);
        var dto = existing.ToDto();
        dto.ExpectedRevision = existing.Revision;
        dto.UpdatedAtUtc = existing.UpdatedAtUtc.AddMinutes(1);
        dto.Memo = "allowed source edit must roll back with locked-field mutation";
        dto.LastSavedByUsername = sourceUser.Username;
        dto.LastSavedAtUtc = dto.UpdatedAtUtc;
        dto.MutationId = $"locked-pending-source:{mutationKind}:{transferId:N}";
        dto.MutationCreatedAtUtc = dto.UpdatedAtUtc;

        switch (mutationKind)
        {
            case "transfer-number":
                dto.TransferNumber = "TR-SPOOFED-NUMBER";
                break;
            case "created-at":
                dto.CreatedAtUtc = existing.CreatedAtUtc.AddDays(1);
                break;
            case "created-by":
                dto.CreatedByUsername = "spoofed-creator";
                break;
            case "requested-audit":
                dto.RequestedByUsername = "spoofed-requester";
                dto.RequestedAtUtc = existing.RequestedAtUtc!.Value.AddMinutes(1);
                break;
            case "receipt-audit":
                dto.ReceivedByUsername = "spoofed-receiver";
                dto.ReceivedAtUtc = dto.UpdatedAtUtc;
                dto.ReceiveMemo = "spoofed receipt memo";
                break;
            case "receive-evidence":
                dto.ReceiveEvidencePath = "inventory-transfers/spoofed-evidence.pdf";
                break;
            case "reject-audit":
                dto.RejectedByUsername = "spoofed-rejector";
                dto.RejectedAtUtc = dto.UpdatedAtUtc;
                dto.RejectReason = "spoofed reject reason";
                break;
            case "status-audit":
                dto.LastStatusChangedByUsername = "spoofed-status-auditor";
                dto.LastStatusChangedAtUtc = dto.UpdatedAtUtc;
                break;
            case "last-saved-actor":
                dto.LastSavedByUsername = "spoofed-last-saved-actor";
                break;
            default:
                throw new InvalidOperationException($"Unknown mutation kind: {mutationKind}");
        }

        var response = await controller.Push(new SyncPushRequest
        {
            DeviceId = $"locked-pending-source-{mutationKind}",
            InventoryTransfers = [dto]
        }, CancellationToken.None);

        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(1, result.ConflictCount);
        Assert.Empty(result.AcceptedRevisions);

        scopedDb.ChangeTracker.Clear();
        var stored = await scopedDb.InventoryTransfers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(transfer => transfer.Lines)
            .SingleAsync(transfer => transfer.Id == transferId);
        var storedLine = Assert.Single(stored.Lines, line => !line.IsDeleted);
        Assert.Equal(existing.TransferNumber, stored.TransferNumber);
        Assert.Equal(existing.Memo, stored.Memo);
        Assert.Equal(existing.CreatedAtUtc, stored.CreatedAtUtc);
        Assert.Equal(existing.CreatedByUsername, stored.CreatedByUsername);
        Assert.Equal(existing.RequestedByUsername, stored.RequestedByUsername);
        Assert.Equal(existing.RequestedAtUtc, stored.RequestedAtUtc);
        Assert.Equal(existing.TransferStatus, stored.TransferStatus);
        Assert.Equal(existing.ReceivedByUsername, stored.ReceivedByUsername);
        Assert.Equal(existing.ReceivedAtUtc, stored.ReceivedAtUtc);
        Assert.Equal(existing.ReceiveMemo, stored.ReceiveMemo);
        Assert.Equal(existing.ReceiveEvidencePath, stored.ReceiveEvidencePath);
        Assert.Equal(existing.RejectedByUsername, stored.RejectedByUsername);
        Assert.Equal(existing.RejectedAtUtc, stored.RejectedAtUtc);
        Assert.Equal(existing.RejectReason, stored.RejectReason);
        Assert.Equal(existing.LastSavedByUsername, stored.LastSavedByUsername);
        Assert.Equal(existing.LastSavedAtUtc, stored.LastSavedAtUtc);
        Assert.Equal(existing.LastStatusChangedByUsername, stored.LastStatusChangedByUsername);
        Assert.Equal(existing.LastStatusChangedAtUtc, stored.LastStatusChangedAtUtc);
        Assert.Equal(existing.Revision, stored.Revision);
        Assert.Equal(existing.UpdatedAtUtc, stored.UpdatedAtUtc);
        Assert.Equal(originalLine.Quantity, storedLine.Quantity);
        Assert.Equal(originalLine.ReceivedQuantity, storedLine.ReceivedQuantity);
        Assert.Equal(originalLine.QuantityDifference, storedLine.QuantityDifference);
        Assert.Equal(originalLine.ReceiptRemark, storedLine.ReceiptRemark);
        Assert.Equal(8m, await scopedDb.ItemWarehouseStocks
            .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
        Assert.DoesNotContain(
            await scopedDb.ProcessedSyncMutations.AsNoTracking().ToListAsync(),
            mutation => mutation.EntityName == nameof(InventoryTransfer) && mutation.EntityId == transferId.ToString("D"));
        Assert.Empty(await scopedDb.InventoryLedgerEntries
            .AsNoTracking()
            .Where(entry => entry.SourceDocumentId == transferId)
            .ToListAsync());
    }

    [Fact]
    public async Task Push_AllowsPendingSourceEdit_WhenOnlySourceOwnedFieldsChange()
    {
        var itemId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        await SeedPendingTransferAsync(
            itemId,
            transferId,
            lineId,
            "Allowed pending source edit",
            sourceStockQuantity: 8m);

        var sourceUser = CreateDeliveryUser(
            "usenet-allowed-pending-source-edit",
            OfficeCodeCatalog.Usenet);
        await using var scopedDb = CreateDbContext(sourceUser);
        var controller = CreateController(scopedDb, sourceUser);
        var existing = await scopedDb.InventoryTransfers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(transfer => transfer.Lines)
            .SingleAsync(transfer => transfer.Id == transferId);
        var dto = existing.ToDto();
        dto.ExpectedRevision = existing.Revision;
        dto.UpdatedAtUtc = existing.UpdatedAtUtc.AddMinutes(1);
        dto.TransferDate = existing.TransferDate.AddDays(1);
        dto.Memo = "allowed memo update";
        dto.LastSavedByUsername = sourceUser.Username;
        dto.LastSavedAtUtc = dto.UpdatedAtUtc;
        dto.MutationId = $"allowed-pending-source:{transferId:N}";
        dto.MutationCreatedAtUtc = dto.UpdatedAtUtc;
        var line = Assert.Single(dto.Lines);
        line.Quantity = 3m;
        line.Remark = "allowed requested-line update";
        line.ReceivedQuantity = 1m;
        line.QuantityDifference = -99m;
        line.ReceiptRemark = "stale target-owned receipt data";

        var response = await controller.Push(new SyncPushRequest
        {
            DeviceId = "allowed-pending-source-edit",
            InventoryTransfers = [dto]
        }, CancellationToken.None);

        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(1, result.AcceptedCount);
        Assert.Equal(0, result.ConflictCount);

        scopedDb.ChangeTracker.Clear();
        var stored = await scopedDb.InventoryTransfers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(transfer => transfer.Lines)
            .SingleAsync(transfer => transfer.Id == transferId);
        var storedLine = Assert.Single(stored.Lines, current => !current.IsDeleted);
        Assert.Equal(dto.TransferDate, stored.TransferDate);
        Assert.Equal(dto.Memo, stored.Memo);
        Assert.Equal(3m, storedLine.Quantity);
        Assert.Equal(line.Remark, storedLine.Remark);
        Assert.Equal(existing.TransferNumber, stored.TransferNumber);
        Assert.Equal(existing.CreatedAtUtc, stored.CreatedAtUtc);
        Assert.Equal(existing.CreatedByUsername, stored.CreatedByUsername);
        Assert.Equal(existing.RequestedByUsername, stored.RequestedByUsername);
        Assert.Equal(existing.RequestedAtUtc, stored.RequestedAtUtc);
        Assert.Equal(existing.TransferStatus, stored.TransferStatus);
        Assert.Equal(storedLine.Quantity, storedLine.ReceivedQuantity);
        Assert.Equal(0m, storedLine.QuantityDifference);
        Assert.Empty(storedLine.ReceiptRemark);
        Assert.Equal(sourceUser.Username, stored.LastSavedByUsername);
        Assert.Equal(dto.LastSavedAtUtc, stored.LastSavedAtUtc);
        Assert.True(stored.Revision > existing.Revision);
        Assert.Equal(7m, await scopedDb.ItemWarehouseStocks
            .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
    }

    [Fact]
    public async Task Push_NewPendingTransfer_CanonicalizesServerAndAuthenticatedFields()
    {
        var itemId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        var now = new DateTime(2026, 8, 2, 1, 0, 0, DateTimeKind.Utc);
        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            seedDb.Items.Add(CreateStockItem(itemId, "Canonical new transfer item", currentStock: 8m));
            seedDb.ItemWarehouseStocks.Add(new ItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 8m,
                UpdatedAtUtc = now
            });
            await seedDb.SaveChangesAsync();
        }

        var sourceUser = CreateDeliveryUser("usenet-canonical-new-transfer", OfficeCodeCatalog.Usenet);
        var dto = BuildPendingTransferDto(
            transferId,
            itemId,
            "Canonical new transfer item",
            quantity: 2m,
            username: sourceUser.Username,
            now: now,
            mutationPrefix: "canonical-new-transfer");
        dto.CreatedByUsername = "spoofed-creator";
        dto.RequestedByUsername = "spoofed-requester";
        dto.RequestedAtUtc = now.AddYears(10);
        dto.LastSavedByUsername = "spoofed-saver";
        dto.LastSavedAtUtc = now.AddYears(10);
        dto.LastStatusChangedByUsername = "spoofed-status-actor";
        dto.LastStatusChangedAtUtc = now.AddYears(10);
        dto.ReceiveEvidencePath = "inventory-transfers/spoofed-new-evidence.pdf";
        dto.ReceivedByUsername = "spoofed-receiver";
        dto.ReceivedAtUtc = now.AddYears(10);
        dto.ReceiveMemo = "spoofed receive memo";
        dto.RejectedByUsername = "spoofed-rejector";
        dto.RejectedAtUtc = now.AddYears(10);
        dto.RejectReason = "spoofed reject reason";
        var incomingLine = Assert.Single(dto.Lines);
        incomingLine.ReceivedQuantity = 999m;
        incomingLine.QuantityDifference = 997m;
        incomingLine.ReceiptRemark = "spoofed receipt remark";

        await using var scopedDb = CreateDbContext(sourceUser);
        var response = await CreateController(scopedDb, sourceUser).Push(
            new SyncPushRequest
            {
                DeviceId = "canonical-new-transfer-device",
                InventoryTransfers = [dto]
            },
            CancellationToken.None);

        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(1, result.AcceptedCount);
        Assert.Equal(0, result.ConflictCount);

        scopedDb.ChangeTracker.Clear();
        var stored = await scopedDb.InventoryTransfers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(transfer => transfer.Lines)
            .SingleAsync(transfer => transfer.Id == transferId);
        var storedLine = Assert.Single(stored.Lines, line => !line.IsDeleted);
        Assert.Equal(sourceUser.Username, stored.CreatedByUsername);
        Assert.Equal(sourceUser.Username, stored.RequestedByUsername);
        Assert.Equal(sourceUser.Username, stored.LastSavedByUsername);
        Assert.Equal(sourceUser.Username, stored.LastStatusChangedByUsername);
        Assert.Equal(InventoryTransferStatusNormalizer.Pending, stored.TransferStatus);
        Assert.Empty(stored.ReceiveEvidencePath);
        Assert.Empty(stored.ReceivedByUsername);
        Assert.Null(stored.ReceivedAtUtc);
        Assert.Empty(stored.ReceiveMemo);
        Assert.Empty(stored.RejectedByUsername);
        Assert.Null(stored.RejectedAtUtc);
        Assert.Empty(stored.RejectReason);
        Assert.Equal(storedLine.Quantity, storedLine.ReceivedQuantity);
        Assert.Equal(0m, storedLine.QuantityDifference);
        Assert.Empty(storedLine.ReceiptRemark);
    }

    [Fact]
    public async Task Push_RejectsNonDeletedInventoryTransferWithoutActiveLines()
    {
        var transferId = Guid.NewGuid();
        var sourceUser = CreateDeliveryUser(
            "usenet-empty-transfer-lines",
            OfficeCodeCatalog.Usenet);
        var dto = BuildPendingTransferDto(
            transferId,
            Guid.NewGuid(),
            "Removed transfer line",
            quantity: 1m,
            username: sourceUser.Username,
            now: new DateTime(2026, 8, 2, 1, 10, 0, DateTimeKind.Utc),
            mutationPrefix: "empty-transfer-lines");
        dto.Lines = [];

        await using var scopedDb = CreateDbContext(sourceUser);
        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(
                (await CreateController(scopedDb, sourceUser).Push(
                    new SyncPushRequest
                    {
                        DeviceId = "empty-transfer-lines-device",
                        InventoryTransfers = [dto]
                    },
                    CancellationToken.None)).Result).Value);

        Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            conflict.EntityName == nameof(InventoryTransfer) &&
            conflict.Reason.Contains("active", StringComparison.OrdinalIgnoreCase));
        Assert.False(await scopedDb.InventoryTransfers
            .IgnoreQueryFilters()
            .AnyAsync(transfer => transfer.Id == transferId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Push_RejectsNonDeletedInventoryTransferLineWithoutItemId(
        bool useEmptyItemId)
    {
        var transferId = Guid.NewGuid();
        var sourceUser = CreateDeliveryUser(
            $"usenet-itemless-transfer-{useEmptyItemId}",
            OfficeCodeCatalog.Usenet);
        var dto = BuildPendingTransferDto(
            transferId,
            Guid.NewGuid(),
            "Itemless transfer line",
            quantity: 1m,
            username: sourceUser.Username,
            now: new DateTime(2026, 8, 2, 1, 20, 0, DateTimeKind.Utc),
            mutationPrefix: $"itemless-transfer-{useEmptyItemId}");
        Assert.Single(dto.Lines).ItemId = useEmptyItemId ? Guid.Empty : null;

        await using var scopedDb = CreateDbContext(sourceUser);
        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(
                (await CreateController(scopedDb, sourceUser).Push(
                    new SyncPushRequest
                    {
                        DeviceId = $"itemless-transfer-{useEmptyItemId}-device",
                        InventoryTransfers = [dto]
                    },
                    CancellationToken.None)).Result).Value);

        Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            conflict.EntityName == nameof(InventoryTransfer) &&
            conflict.Reason.Contains("item", StringComparison.OrdinalIgnoreCase));
        Assert.False(await scopedDb.InventoryTransfers
            .IgnoreQueryFilters()
            .AnyAsync(transfer => transfer.Id == transferId));
    }

    [Fact]
    public async Task Push_GlobalAdminRejectsForeignTenantItemInInventoryTransfer()
    {
        var itemId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        var now = new DateTime(2026, 8, 2, 1, 30, 0, DateTimeKind.Utc);
        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            var foreignItem = CreateStockItem(
                itemId,
                "Foreign tenant globally readable item",
                currentStock: 10m);
            foreignItem.TenantCode = TenantScopeCatalog.Itworld;
            foreignItem.OfficeCode = OfficeCodeCatalog.Itworld;
            seedDb.Items.Add(foreignItem);
            seedDb.ItemWarehouseStocks.Add(new ItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 10m,
                UpdatedAtUtc = now
            });
            await seedDb.SaveChangesAsync();
        }

        var globalAdmin = CreateAdminUser();
        var dto = BuildPendingTransferDto(
            transferId,
            itemId,
            "Foreign tenant globally readable item",
            quantity: 2m,
            username: globalAdmin.Username,
            now: now,
            mutationPrefix: "foreign-tenant-transfer-item");

        await using var scopedDb = CreateDbContext(globalAdmin);
        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(
                (await CreateController(scopedDb, globalAdmin).Push(
                    new SyncPushRequest
                    {
                        DeviceId = "foreign-tenant-transfer-item-device",
                        InventoryTransfers = [dto]
                    },
                    CancellationToken.None)).Result).Value);

        Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            conflict.EntityName == nameof(InventoryTransfer) &&
            conflict.Reason.Contains("tenant", StringComparison.OrdinalIgnoreCase));
        Assert.False(await scopedDb.InventoryTransfers
            .IgnoreQueryFilters()
            .AnyAsync(transfer => transfer.Id == transferId));
        Assert.Equal(10m, await scopedDb.ItemWarehouseStocks
            .Where(stock =>
                stock.ItemId == itemId &&
                stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
    }

    [Fact]
    public async Task Push_AllowsSameTenantSharedItemInInventoryTransfer()
    {
        var itemId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        var now = new DateTime(2026, 8, 2, 1, 40, 0, DateTimeKind.Utc);
        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            seedDb.Items.Add(CreateStockItem(
                itemId,
                "Same tenant shared transfer item",
                currentStock: 10m));
            seedDb.ItemWarehouseStocks.Add(new ItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 10m,
                UpdatedAtUtc = now
            });
            await seedDb.SaveChangesAsync();
        }

        var sourceUser = CreateDeliveryUser(
            "usenet-same-tenant-shared-transfer-item",
            OfficeCodeCatalog.Usenet);
        var dto = BuildPendingTransferDto(
            transferId,
            itemId,
            "Same tenant shared transfer item",
            quantity: 2m,
            username: sourceUser.Username,
            now: now,
            mutationPrefix: "same-tenant-shared-transfer-item");

        await using var scopedDb = CreateDbContext(sourceUser);
        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(
                (await CreateController(scopedDb, sourceUser).Push(
                    new SyncPushRequest
                    {
                        DeviceId = "same-tenant-shared-transfer-item-device",
                        InventoryTransfers = [dto]
                    },
                    CancellationToken.None)).Result).Value);

        Assert.Equal(1, result.AcceptedCount);
        Assert.Equal(0, result.ConflictCount);
        Assert.False(
            Assert.Single(
                result.AcceptedRevisions,
                revision =>
                    revision.EntityName == nameof(InventoryTransfer) &&
                    revision.EntityId == transferId)
            .IsDeleted);
        Assert.True(await scopedDb.InventoryTransfers
            .IgnoreQueryFilters()
            .AnyAsync(transfer => transfer.Id == transferId));
    }

    [Fact]
    public async Task Push_PendingDelete_AppliesTombstoneWithoutPoisoningLockedFields()
    {
        var itemId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        await SeedPendingTransferAsync(
            itemId,
            transferId,
            lineId,
            "Canonical pending tombstone item",
            sourceStockQuantity: 8m);

        var sourceUser = CreateDeliveryUser("usenet-canonical-pending-delete", OfficeCodeCatalog.Usenet);
        await using var scopedDb = CreateDbContext(sourceUser);
        var existing = await scopedDb.InventoryTransfers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(transfer => transfer.Lines)
            .SingleAsync(transfer => transfer.Id == transferId);
        var existingLine = Assert.Single(existing.Lines, line => !line.IsDeleted);
        var dto = existing.ToDto();
        dto.ExpectedRevision = existing.Revision;
        dto.UpdatedAtUtc = existing.UpdatedAtUtc.AddMinutes(1);
        dto.IsDeleted = true;
        dto.CreatedByUsername = "spoofed-delete-creator";
        dto.RequestedByUsername = "spoofed-delete-requester";
        dto.ReceiveEvidencePath = "inventory-transfers/spoofed-delete-evidence.pdf";
        dto.ReceivedByUsername = "spoofed-delete-receiver";
        dto.ReceivedAtUtc = dto.UpdatedAtUtc;
        dto.RejectedByUsername = "spoofed-delete-rejector";
        dto.RejectedAtUtc = dto.UpdatedAtUtc;
        dto.LastStatusChangedByUsername = "spoofed-delete-status-actor";
        dto.LastStatusChangedAtUtc = dto.UpdatedAtUtc;
        dto.MutationId = $"canonical-pending-delete:InventoryTransfer:{transferId:N}";
        dto.MutationCreatedAtUtc = dto.UpdatedAtUtc;
        var incomingLine = Assert.Single(dto.Lines);
        incomingLine.Quantity = 999m;
        incomingLine.ReceivedQuantity = 888m;
        incomingLine.QuantityDifference = -111m;
        incomingLine.ReceiptRemark = "spoofed delete receipt";

        var response = await CreateController(scopedDb, sourceUser).Push(
            new SyncPushRequest
            {
                DeviceId = "canonical-pending-delete-device",
                InventoryTransfers = [dto]
            },
            CancellationToken.None);

        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(1, result.AcceptedCount);
        Assert.Equal(0, result.ConflictCount);
        Assert.True(
            Assert.Single(
                result.AcceptedRevisions,
                revision =>
                    revision.EntityName == nameof(InventoryTransfer) &&
                    revision.EntityId == transferId)
            .IsDeleted);

        scopedDb.ChangeTracker.Clear();
        var stored = await scopedDb.InventoryTransfers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(transfer => transfer.Lines)
            .SingleAsync(transfer => transfer.Id == transferId);
        var storedLine = Assert.Single(stored.Lines, line => !line.IsDeleted);
        Assert.True(stored.IsDeleted);
        Assert.Equal(existing.CreatedByUsername, stored.CreatedByUsername);
        Assert.Equal(existing.RequestedByUsername, stored.RequestedByUsername);
        Assert.Equal(existing.ReceiveEvidencePath, stored.ReceiveEvidencePath);
        Assert.Equal(existing.ReceivedByUsername, stored.ReceivedByUsername);
        Assert.Equal(existing.ReceivedAtUtc, stored.ReceivedAtUtc);
        Assert.Equal(existing.RejectedByUsername, stored.RejectedByUsername);
        Assert.Equal(existing.RejectedAtUtc, stored.RejectedAtUtc);
        Assert.Equal(existing.LastStatusChangedByUsername, stored.LastStatusChangedByUsername);
        Assert.Equal(existing.LastStatusChangedAtUtc, stored.LastStatusChangedAtUtc);
        Assert.Equal(existingLine.Quantity, storedLine.Quantity);
        Assert.Equal(existingLine.ReceivedQuantity, storedLine.ReceivedQuantity);
        Assert.Equal(existingLine.QuantityDifference, storedLine.QuantityDifference);
        Assert.Equal(existingLine.ReceiptRemark, storedLine.ReceiptRemark);
    }

    [Fact]
    public async Task Push_FinalStatusOnlyRetry_IsAcceptedWithoutRewritingPersistedSnapshot()
    {
        var itemId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        await SeedReceivedTransferAsync(itemId, transferId, lineId, "Final retry no-op item");

        var targetUser = CreateDeliveryUser("yeonsu-final-retry-noop", OfficeCodeCatalog.Yeonsu);
        await using var scopedDb = CreateDbContext(targetUser);
        var controller = CreateController(scopedDb, targetUser);
        var existing = await scopedDb.InventoryTransfers
            .IgnoreQueryFilters()
            .Include(transfer => transfer.Lines)
            .SingleAsync(transfer => transfer.Id == transferId);
        var originalRevision = existing.Revision;
        var originalUpdatedAtUtc = existing.UpdatedAtUtc;
        var dto = BuildFinalizedRetryDto(existing);

        var response = await controller.Push(new SyncPushRequest
        {
            DeviceId = "final-retry-noop",
            InventoryTransfers = [dto]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.Equal(1, result.AcceptedCount);
        Assert.Equal(0, result.ConflictCount);
        scopedDb.ChangeTracker.Clear();
        var stored = await scopedDb.InventoryTransfers.IgnoreQueryFilters()
            .SingleAsync(transfer => transfer.Id == transferId);
        Assert.Equal(originalRevision, stored.Revision);
        Assert.Equal(originalUpdatedAtUtc, stored.UpdatedAtUtc);
        Assert.Equal("usenet-source", stored.CreatedByUsername);
        Assert.Equal(existing.ReceivedByUsername, stored.ReceivedByUsername);
        Assert.Equal(existing.ReceivedAtUtc, stored.ReceivedAtUtc);
    }

    [Fact]
    public async Task Push_FinalStatusOnlyRetry_NormalizesNullableReceiptDefaults()
    {
        var itemId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        await SeedReceivedTransferAsync(itemId, transferId, lineId, "Final retry nullable defaults item");

        var targetUser = CreateDeliveryUser("yeonsu-final-retry-null-defaults", OfficeCodeCatalog.Yeonsu);
        await using var scopedDb = CreateDbContext(targetUser);
        await scopedDb.InventoryTransferLines
            .Where(line => line.Id == lineId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(line => line.QuantityDifference, 0m));
        var existing = await scopedDb.InventoryTransfers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(transfer => transfer.Lines)
            .SingleAsync(transfer => transfer.Id == transferId);
        var originalRevision = existing.Revision;
        var dto = BuildFinalizedRetryDto(existing);
        var incomingLine = Assert.Single(dto.Lines);
        incomingLine.ReceivedQuantity = null;
        incomingLine.QuantityDifference = null;

        var response = await CreateController(scopedDb, targetUser).Push(
            new SyncPushRequest
            {
                DeviceId = "final-retry-null-defaults-device",
                InventoryTransfers = [dto]
            },
            CancellationToken.None);

        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(1, result.AcceptedCount);
        Assert.Equal(0, result.ConflictCount);
        scopedDb.ChangeTracker.Clear();
        Assert.Equal(originalRevision, await scopedDb.InventoryTransfers
            .IgnoreQueryFilters()
            .Where(transfer => transfer.Id == transferId)
            .Select(transfer => transfer.Revision)
            .SingleAsync());
    }

    [Fact]
    public async Task Push_FinalizedDeleteWithSameStatus_AppliesAuthorizedTombstone()
    {
        var itemId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        await SeedReceivedTransferAsync(itemId, transferId, lineId, "Finalized delete same-status item");

        var admin = CreateInventoryDeliveryAdminUser(
            "admin-finalized-delete-same-status",
            OfficeCodeCatalog.Usenet);
        await using var scopedDb = CreateDbContext(admin);
        var existing = await scopedDb.InventoryTransfers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(transfer => transfer.Lines)
            .SingleAsync(transfer => transfer.Id == transferId);
        var dto = BuildFinalizedRetryDto(existing);
        dto.IsDeleted = true;
        dto.UpdatedAtUtc = existing.UpdatedAtUtc.AddMinutes(1);
        dto.MutationId = $"finalized-delete-same-status:InventoryTransfer:{transferId:N}";
        dto.MutationCreatedAtUtc = dto.UpdatedAtUtc;

        var response = await CreateController(scopedDb, admin).Push(
            new SyncPushRequest
            {
                DeviceId = "finalized-delete-same-status-device",
                InventoryTransfers = [dto]
            },
            CancellationToken.None);

        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(1, result.AcceptedCount);
        Assert.Equal(0, result.ConflictCount);
        scopedDb.ChangeTracker.Clear();
        Assert.True(await scopedDb.InventoryTransfers
            .IgnoreQueryFilters()
            .Where(transfer => transfer.Id == transferId)
            .Select(transfer => transfer.IsDeleted)
            .SingleAsync());
        Assert.Equal(10m, await scopedDb.ItemWarehouseStocks
            .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
        Assert.Equal(0m, await scopedDb.ItemWarehouseStocks
            .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.YeonsuMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
    }

    [Theory]
    [InlineData("receive-evidence")]
    [InlineData("created-by")]
    [InlineData("status-audit")]
    [InlineData("opposite-status")]
    [InlineData("deleted-line")]
    public async Task Push_RejectsFinalStatusOnlyRetry_WhenPersistedSnapshotFieldChanges(string mutationKind)
    {
        var itemId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        await SeedReceivedTransferAsync(itemId, transferId, lineId, $"Final retry locked {mutationKind}");

        var targetUser = CreateDeliveryUser(
            $"yeonsu-final-retry-{mutationKind}",
            OfficeCodeCatalog.Yeonsu);
        await using var scopedDb = CreateDbContext(targetUser);
        var controller = CreateController(scopedDb, targetUser);
        var existing = await scopedDb.InventoryTransfers
            .IgnoreQueryFilters()
            .Include(transfer => transfer.Lines)
            .SingleAsync(transfer => transfer.Id == transferId);
        var dto = BuildFinalizedRetryDto(existing);
        switch (mutationKind)
        {
            case "receive-evidence":
                dto.ReceiveEvidencePath = "inventory-transfers/final-client-overwrite.pdf";
                break;
            case "created-by":
                dto.CreatedByUsername = "spoofed-final-creator";
                break;
            case "status-audit":
                dto.LastSavedByUsername = "spoofed-final-auditor";
                dto.LastStatusChangedAtUtc = existing.UpdatedAtUtc.AddMinutes(1);
                break;
            case "opposite-status":
                dto.RejectedByUsername = "spoofed-final-rejector";
                dto.RejectedAtUtc = dto.ReceivedAtUtc;
                dto.RejectReason = "injected final opposite status";
                break;
            case "deleted-line":
                dto.Lines.Add(new InventoryTransferLineDto
                {
                    Id = Guid.NewGuid(),
                    TransferId = transferId,
                    ItemNameOriginal = "injected final deleted line",
                    Unit = "EA",
                    Quantity = 1m,
                    IsDeleted = true
                });
                break;
            default:
                throw new InvalidOperationException($"Unknown mutation kind: {mutationKind}");
        }
        dto.MutationId = $"locked-final-retry:{mutationKind}:{transferId:N}";

        var response = await controller.Push(new SyncPushRequest
        {
            DeviceId = $"locked-final-retry-{mutationKind}",
            InventoryTransfers = [dto]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(1, result.ConflictCount);
        scopedDb.ChangeTracker.Clear();
        var stored = await scopedDb.InventoryTransfers.IgnoreQueryFilters()
            .Include(transfer => transfer.Lines)
            .SingleAsync(transfer => transfer.Id == transferId);
        Assert.Equal(InventoryTransferStatusNormalizer.Received, stored.TransferStatus);
        Assert.Equal("usenet-source", stored.CreatedByUsername);
        Assert.Empty(stored.ReceiveEvidencePath);
        Assert.Empty(stored.RejectedByUsername);
        Assert.Null(stored.RejectedAtUtc);
        Assert.Empty(stored.RejectReason);
        Assert.DoesNotContain(stored.Lines, line => line.ItemNameOriginal == "injected final deleted line");
    }

    [Theory]
    [InlineData("-0.01")]
    [InlineData("2.01")]
    [InlineData("1.001")]
    public async Task Push_RejectsInvalidInitialReceiptQuantity(string receivedQuantityText)
    {
        var receivedQuantity = decimal.Parse(
            receivedQuantityText,
            System.Globalization.CultureInfo.InvariantCulture);
        var itemId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        await SeedPendingTransferAsync(itemId, transferId, lineId, "Invalid initial receipt quantity item", sourceStockQuantity: 8m);

        var targetUser = CreateDeliveryUser("yeonsu-invalid-initial-receipt", OfficeCodeCatalog.Yeonsu);
        await using var scopedDb = CreateDbContext(targetUser);
        var controller = CreateController(scopedDb, targetUser);
        var existing = await scopedDb.InventoryTransfers
            .IgnoreQueryFilters()
            .Include(transfer => transfer.Lines)
            .SingleAsync(transfer => transfer.Id == transferId);
        var dto = BuildReceiptDto(existing, targetUser.Username, requestedQuantity: 2m);
        var line = Assert.Single(dto.Lines);
        line.ReceivedQuantity = receivedQuantity;
        line.QuantityDifference = 999m;
        dto.MutationId = $"invalid-initial-receipt:InventoryTransfer:{transferId:N}:{receivedQuantityText}";

        var response = await controller.Push(new SyncPushRequest
        {
            DeviceId = "invalid-initial-receipt",
            InventoryTransfers = [dto]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            string.Equals(conflict.EntityName, nameof(InventoryTransfer), StringComparison.Ordinal) &&
            conflict.Reason.Contains("Received inventory transfer line quantity", StringComparison.Ordinal));

        scopedDb.ChangeTracker.Clear();
        Assert.Equal(
            InventoryTransferStatusNormalizer.Pending,
            await scopedDb.InventoryTransfers.IgnoreQueryFilters()
                .Where(transfer => transfer.Id == transferId)
                .Select(transfer => transfer.TransferStatus)
                .SingleAsync());
        Assert.False(await scopedDb.InventoryLedgerEntries.AnyAsync(entry =>
            entry.SourceDocumentId == transferId &&
            entry.SourceType == "InventoryTransfer:In"));
        Assert.False(await scopedDb.ItemWarehouseStocks.AnyAsync(stock =>
            stock.ItemId == itemId &&
            stock.WarehouseCode == OfficeCodeCatalog.YeonsuMainWarehouse));
    }

    [Fact]
    public async Task Push_InitialPartialReceipt_DerivesDifferenceAndMatchesPersistedLedger()
    {
        var itemId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        await SeedPendingTransferAsync(itemId, transferId, lineId, "Partial initial receipt item", sourceStockQuantity: 8m);

        var targetUser = CreateDeliveryUser("yeonsu-partial-initial-receipt", OfficeCodeCatalog.Yeonsu);
        await using var scopedDb = CreateDbContext(targetUser);
        var controller = CreateController(scopedDb, targetUser);
        var existing = await scopedDb.InventoryTransfers
            .IgnoreQueryFilters()
            .Include(transfer => transfer.Lines)
            .SingleAsync(transfer => transfer.Id == transferId);
        var dto = BuildReceiptDto(existing, targetUser.Username, requestedQuantity: 2m);
        var line = Assert.Single(dto.Lines);
        line.ReceivedQuantity = 1.25m;
        line.QuantityDifference = 999m;
        dto.MutationId = $"partial-initial-receipt:InventoryTransfer:{transferId:N}";

        var response = await controller.Push(new SyncPushRequest
        {
            DeviceId = "partial-initial-receipt",
            InventoryTransfers = [dto]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.Equal(0, result.ConflictCount);
        Assert.Equal(1, result.AcceptedCount);

        scopedDb.ChangeTracker.Clear();
        var storedLine = await scopedDb.InventoryTransferLines.IgnoreQueryFilters()
            .SingleAsync(current => current.Id == lineId);
        var inboundLedger = await scopedDb.InventoryLedgerEntries
            .SingleAsync(entry =>
                entry.SourceDocumentId == transferId &&
                entry.SourceLineId == lineId &&
                entry.SourceType == "InventoryTransfer:In");
        Assert.Equal(1.25m, storedLine.ReceivedQuantity);
        Assert.Equal(-0.75m, storedLine.QuantityDifference);
        Assert.Equal(storedLine.ReceivedQuantity, inboundLedger.QuantityDelta);
        Assert.Equal(1.25m, await scopedDb.ItemWarehouseStocks
            .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.YeonsuMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
    }

    [Fact]
    public async Task Push_InitialFullReceipt_DefaultsMissingQuantityAndDerivesDifference()
    {
        var itemId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        await SeedPendingTransferAsync(itemId, transferId, lineId, "Full initial receipt item", sourceStockQuantity: 8m);

        var targetUser = CreateDeliveryUser("yeonsu-full-initial-receipt", OfficeCodeCatalog.Yeonsu);
        await using var scopedDb = CreateDbContext(targetUser);
        var controller = CreateController(scopedDb, targetUser);
        var existing = await scopedDb.InventoryTransfers
            .IgnoreQueryFilters()
            .Include(transfer => transfer.Lines)
            .SingleAsync(transfer => transfer.Id == transferId);
        var dto = BuildReceiptDto(existing, targetUser.Username, requestedQuantity: 2m);
        var line = Assert.Single(dto.Lines);
        line.ReceivedQuantity = null;
        line.QuantityDifference = -999m;
        dto.MutationId = $"full-initial-receipt:InventoryTransfer:{transferId:N}";

        var response = await controller.Push(new SyncPushRequest
        {
            DeviceId = "full-initial-receipt",
            InventoryTransfers = [dto]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.Equal(0, result.ConflictCount);
        Assert.Equal(1, result.AcceptedCount);

        scopedDb.ChangeTracker.Clear();
        var storedLine = await scopedDb.InventoryTransferLines.IgnoreQueryFilters()
            .SingleAsync(current => current.Id == lineId);
        var inboundLedger = await scopedDb.InventoryLedgerEntries
            .SingleAsync(entry =>
                entry.SourceDocumentId == transferId &&
                entry.SourceLineId == lineId &&
                entry.SourceType == "InventoryTransfer:In");
        Assert.Equal(2m, storedLine.ReceivedQuantity);
        Assert.Equal(0m, storedLine.QuantityDifference);
        Assert.Equal(storedLine.ReceivedQuantity, inboundLedger.QuantityDelta);
        Assert.Equal(2m, await scopedDb.ItemWarehouseStocks
            .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.YeonsuMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
    }

    [Fact]
    public async Task Push_RejectsTargetOfficeReceipt_WhenRequestedLineQuantityChanges()
    {
        var itemId = Guid.Parse("f1111111-1111-1111-1111-111111111111");
        var transferId = Guid.Parse("f2222222-2222-2222-2222-222222222222");
        var lineId = Guid.Parse("f3333333-3333-3333-3333-333333333333");
        await SeedPendingTransferAsync(itemId, transferId, lineId, "Target receipt changed quantity item", sourceStockQuantity: 8m);

        var targetUser = CreateDeliveryUser("yeonsu-target-transfer-change", OfficeCodeCatalog.Yeonsu);
        await using var scopedDb = CreateDbContext(targetUser);
        var controller = CreateController(scopedDb, targetUser);
        var existing = await scopedDb.InventoryTransfers
            .IgnoreQueryFilters()
            .Include(transfer => transfer.Lines)
            .SingleAsync(transfer => transfer.Id == transferId);

        var response = await controller.Push(new SyncPushRequest
        {
            DeviceId = "target-transfer-receive-line-change",
            InventoryTransfers =
            [
                BuildReceiptDto(existing, targetUser.Username, requestedQuantity: 5m)
            ]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            string.Equals(conflict.EntityName, nameof(InventoryTransfer), StringComparison.Ordinal) &&
            conflict.Reason.Contains("target-only status updates", StringComparison.OrdinalIgnoreCase));
        scopedDb.ChangeTracker.Clear();
        var stored = await scopedDb.InventoryTransfers.IgnoreQueryFilters().SingleAsync(transfer => transfer.Id == transferId);
        Assert.Equal(InventoryTransferStatusNormalizer.Pending, stored.TransferStatus);
        Assert.Equal(8m, await scopedDb.ItemWarehouseStocks
            .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
        Assert.False(await scopedDb.ItemWarehouseStocks
            .AnyAsync(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.YeonsuMainWarehouse));
    }

    [Fact]
    public async Task Push_RejectsTargetOfficeDelete_WhenExistingTransferIsReceived()
    {
        var itemId = Guid.Parse("f4111111-1111-1111-1111-111111111111");
        var transferId = Guid.Parse("f4222222-2222-2222-2222-222222222222");
        var lineId = Guid.Parse("f4333333-3333-3333-3333-333333333333");
        await SeedReceivedTransferAsync(itemId, transferId, lineId, "Target final delete denied item");

        var targetUser = CreateDeliveryUser("yeonsu-target-transfer-final-delete", OfficeCodeCatalog.Yeonsu);
        await using var scopedDb = CreateDbContext(targetUser);
        var controller = CreateController(scopedDb, targetUser);
        var existing = await scopedDb.InventoryTransfers
            .IgnoreQueryFilters()
            .Include(transfer => transfer.Lines)
            .SingleAsync(transfer => transfer.Id == transferId);
        var dto = BuildReceiptDto(existing, targetUser.Username, requestedQuantity: 2m);
        dto.IsDeleted = true;
        dto.MutationId = $"target-transfer-final-delete:InventoryTransfer:{existing.Id:N}";

        var response = await controller.Push(new SyncPushRequest
        {
            DeviceId = "target-transfer-final-delete",
            InventoryTransfers = [dto]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            string.Equals(conflict.EntityName, nameof(InventoryTransfer), StringComparison.Ordinal) &&
            conflict.Reason.Contains("source office", StringComparison.OrdinalIgnoreCase));
        scopedDb.ChangeTracker.Clear();
        var stored = await scopedDb.InventoryTransfers.IgnoreQueryFilters().SingleAsync(transfer => transfer.Id == transferId);
        Assert.False(stored.IsDeleted);
        Assert.Equal(8m, await scopedDb.ItemWarehouseStocks
            .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
        Assert.Equal(2m, await scopedDb.ItemWarehouseStocks
            .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.YeonsuMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
    }

    [Fact]
    public async Task Push_RejectsTargetOfficeStatusFlip_WhenExistingTransferIsReceived()
    {
        var itemId = Guid.Parse("f7111111-1111-1111-1111-111111111111");
        var transferId = Guid.Parse("f7222222-2222-2222-2222-222222222222");
        var lineId = Guid.Parse("f7333333-3333-3333-3333-333333333333");
        await SeedReceivedTransferAsync(itemId, transferId, lineId, "Target final flip denied item");

        var targetUser = CreateDeliveryUser("yeonsu-target-transfer-final-flip", OfficeCodeCatalog.Yeonsu);
        await using var scopedDb = CreateDbContext(targetUser);
        var controller = CreateController(scopedDb, targetUser);
        var existing = await scopedDb.InventoryTransfers
            .IgnoreQueryFilters()
            .Include(transfer => transfer.Lines)
            .SingleAsync(transfer => transfer.Id == transferId);
        var dto = BuildReceiptDto(existing, targetUser.Username, requestedQuantity: 2m);
        dto.TransferStatus = InventoryTransferStatusNormalizer.Rejected;
        dto.RejectReason = "flip after receipt";
        dto.RejectedByUsername = targetUser.Username;
        dto.RejectedAtUtc = existing.UpdatedAtUtc.AddMinutes(2);
        dto.MutationId = $"target-transfer-final-flip:InventoryTransfer:{existing.Id:N}";

        var response = await controller.Push(new SyncPushRequest
        {
            DeviceId = "target-transfer-final-flip",
            InventoryTransfers = [dto]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            string.Equals(conflict.EntityName, nameof(InventoryTransfer), StringComparison.Ordinal));
        scopedDb.ChangeTracker.Clear();
        var stored = await scopedDb.InventoryTransfers.IgnoreQueryFilters().SingleAsync(transfer => transfer.Id == transferId);
        Assert.Equal(InventoryTransferStatusNormalizer.Received, stored.TransferStatus);
        Assert.Equal(8m, await scopedDb.ItemWarehouseStocks
            .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
        Assert.Equal(2m, await scopedDb.ItemWarehouseStocks
            .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.YeonsuMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
    }

    [Fact]
    public async Task Push_RejectsTargetOfficeReceivedQuantityChange_WhenTransferAlreadyReceived()
    {
        var itemId = Guid.Parse("f8111111-1111-1111-1111-111111111111");
        var transferId = Guid.Parse("f8222222-2222-2222-2222-222222222222");
        var lineId = Guid.Parse("f8333333-3333-3333-3333-333333333333");
        await SeedReceivedTransferAsync(itemId, transferId, lineId, "Target final quantity denied item");

        var targetUser = CreateDeliveryUser("yeonsu-target-transfer-final-quantity", OfficeCodeCatalog.Yeonsu);
        await using var scopedDb = CreateDbContext(targetUser);
        var controller = CreateController(scopedDb, targetUser);
        var existing = await scopedDb.InventoryTransfers
            .IgnoreQueryFilters()
            .Include(transfer => transfer.Lines)
            .SingleAsync(transfer => transfer.Id == transferId);

        var dto = BuildReceiptDto(existing, targetUser.Username, requestedQuantity: 2m);
        var line = Assert.Single(dto.Lines);
        line.ReceivedQuantity = 1m;
        line.QuantityDifference = -1m;
        line.ReceiptRemark = "changed after final";
        dto.MutationId = $"target-transfer-final-quantity:InventoryTransfer:{existing.Id:N}";

        var response = await controller.Push(new SyncPushRequest
        {
            DeviceId = "target-transfer-final-quantity",
            InventoryTransfers = [dto]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            string.Equals(conflict.EntityName, nameof(InventoryTransfer), StringComparison.Ordinal));
        scopedDb.ChangeTracker.Clear();
        var storedLine = await scopedDb.InventoryTransferLines.IgnoreQueryFilters().SingleAsync(line => line.Id == lineId);
        Assert.Equal(2m, storedLine.ReceivedQuantity);
        Assert.Equal(8m, await scopedDb.ItemWarehouseStocks
            .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
        Assert.Equal(2m, await scopedDb.ItemWarehouseStocks
            .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.YeonsuMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
    }

    [Fact]
    public async Task RecycleBinRestore_RejectsTargetOfficeUserFromRestoringSourceMove()
    {
        var itemId = Guid.Parse("f5111111-1111-1111-1111-111111111111");
        var transferId = Guid.Parse("f5222222-2222-2222-2222-222222222222");
        var lineId = Guid.Parse("f5333333-3333-3333-3333-333333333333");
        await SeedDeletedPendingTransferAsync(itemId, transferId, lineId, "Target restore denied item");

        var targetUser = CreateDeliveryUser("yeonsu-target-transfer-restore", OfficeCodeCatalog.Yeonsu);
        await using var scopedDb = CreateDbContext(targetUser);
        var controller = CreateRecycleBinController(scopedDb, targetUser);
        var storedBefore = await scopedDb.InventoryTransfers.IgnoreQueryFilters().SingleAsync(transfer => transfer.Id == transferId);

        var response = await controller.Restore(new RecycleBinMutationRequest
        {
            Items =
            [
                new RecycleBinMutationTargetDto
                {
                    EntityId = transferId,
                    Kind = "inventory-transfer",
                    ExpectedRevision = storedBefore.Revision
                }
            ]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.False(item.Success);
        Assert.Contains("source office", item.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, payload.SucceededCount);
        scopedDb.ChangeTracker.Clear();
        Assert.True((await scopedDb.InventoryTransfers.IgnoreQueryFilters().SingleAsync(transfer => transfer.Id == transferId)).IsDeleted);
        Assert.Equal(10m, await scopedDb.ItemWarehouseStocks
            .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
        Assert.False(await scopedDb.InventoryLedgerEntries.AnyAsync(entry => entry.SourceDocumentId == transferId));
    }

    [Fact]
    public async Task RecycleBinPurge_RejectsTargetOfficeUserFromPurgingSourceMove()
    {
        var itemId = Guid.Parse("f6111111-1111-1111-1111-111111111111");
        var transferId = Guid.Parse("f6222222-2222-2222-2222-222222222222");
        var lineId = Guid.Parse("f6333333-3333-3333-3333-333333333333");
        await SeedDeletedPendingTransferAsync(itemId, transferId, lineId, "Target purge denied item");

        var targetUser = CreateDeliveryUser("yeonsu-target-transfer-purge", OfficeCodeCatalog.Yeonsu);
        await using var scopedDb = CreateDbContext(targetUser);
        var controller = CreateRecycleBinController(scopedDb, targetUser);
        var storedBefore = await scopedDb.InventoryTransfers.IgnoreQueryFilters().SingleAsync(transfer => transfer.Id == transferId);

        var response = await controller.Purge(new RecycleBinMutationRequest
        {
            Items =
            [
                new RecycleBinMutationTargetDto
                {
                    EntityId = transferId,
                    Kind = "inventory-transfer",
                    ExpectedRevision = storedBefore.Revision
                }
            ]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.False(item.Success);
        Assert.Contains("source office", item.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, payload.SucceededCount);
        scopedDb.ChangeTracker.Clear();
        Assert.True(await scopedDb.InventoryTransfers.IgnoreQueryFilters().AnyAsync(transfer => transfer.Id == transferId));
        Assert.True(await scopedDb.InventoryTransferLines.IgnoreQueryFilters().AnyAsync(line => line.Id == lineId));
    }

    [Fact]
    public async Task RecycleBinPurge_RejectsTargetOfficeUserBeforeRevisionConflict()
    {
        var itemId = Guid.Parse("f9111111-1111-1111-1111-111111111111");
        var transferId = Guid.Parse("f9222222-2222-2222-2222-222222222222");
        var lineId = Guid.Parse("f9333333-3333-3333-3333-333333333333");
        await SeedDeletedPendingTransferAsync(itemId, transferId, lineId, "Target purge revision denied item");

        var targetUser = CreateDeliveryUser("yeonsu-target-transfer-purge-revision", OfficeCodeCatalog.Yeonsu);
        await using var scopedDb = CreateDbContext(targetUser);
        var controller = CreateRecycleBinController(scopedDb, targetUser);
        var storedBefore = await scopedDb.InventoryTransfers.IgnoreQueryFilters().SingleAsync(transfer => transfer.Id == transferId);

        var response = await controller.Purge(new RecycleBinMutationRequest
        {
            Items =
            [
                new RecycleBinMutationTargetDto
                {
                    EntityId = transferId,
                    Kind = "inventory-transfer",
                    ExpectedRevision = storedBefore.Revision + 1
                }
            ]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var item = Assert.Single(payload.Results);
        Assert.False(item.Success);
        Assert.Contains("source office", item.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, payload.SucceededCount);
        scopedDb.ChangeTracker.Clear();
        Assert.True(await scopedDb.InventoryTransfers.IgnoreQueryFilters().AnyAsync(transfer => transfer.Id == transferId));
        Assert.True(await scopedDb.InventoryTransferLines.IgnoreQueryFilters().AnyAsync(line => line.Id == lineId));
    }

    [Fact]
    public async Task RecycleBinPurge_PublishesInventoryTransferPurgeRecordToTargetOfficePull()
    {
        var itemId = Guid.Parse("fa111111-1111-1111-1111-111111111111");
        var transferId = Guid.Parse("fa222222-2222-2222-2222-222222222222");
        var lineId = Guid.Parse("fa333333-3333-3333-3333-333333333333");
        await SeedDeletedPendingTransferAsync(itemId, transferId, lineId, "Target purge pull item");

        var sourceUser = CreateDeliveryUser("usenet-source-transfer-purge", OfficeCodeCatalog.Usenet);
        await using (var sourceDb = CreateDbContext(sourceUser))
        {
            var purgeController = CreateRecycleBinController(sourceDb, sourceUser);
            var storedBefore = await sourceDb.InventoryTransfers.IgnoreQueryFilters().SingleAsync(transfer => transfer.Id == transferId);
            var purgeResponse = await purgeController.Purge(new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = transferId,
                        Kind = "inventory-transfer",
                        ExpectedRevision = storedBefore.Revision
                    }
                ]
            }, CancellationToken.None);

            var purgeOk = Assert.IsType<OkObjectResult>(purgeResponse.Result);
            var purgePayload = Assert.IsType<RecycleBinMutationResultDto>(purgeOk.Value);
            var purgeItem = Assert.Single(purgePayload.Results);
            Assert.True(purgeItem.Success, purgeItem.Message);
        }

        var targetUser = CreateDeliveryUser("yeonsu-target-transfer-purge-pull", OfficeCodeCatalog.Yeonsu);
        await using var targetDb = CreateDbContext(targetUser);
        var syncController = CreateController(targetDb, targetUser);

        var pullResponse = await syncController.Pull(0, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(pullResponse.Result);
        var payload = Assert.IsType<SyncPullResponse>(ok.Value);
        var purgeRecord = Assert.Single(payload.PurgeRecords, record =>
            string.Equals(record.Kind, "inventory-transfer", StringComparison.OrdinalIgnoreCase) &&
            record.EntityId == transferId);
        Assert.Equal(OfficeCodeCatalog.Usenet, purgeRecord.SourceOfficeCode);
        Assert.Equal(OfficeCodeCatalog.Yeonsu, purgeRecord.TargetOfficeCode);
        Assert.DoesNotContain(payload.InventoryTransfers, transfer => transfer.Id == transferId);
    }

    [Fact]
    public async Task Push_EmptyRequest_DoesNotRepairUntouchedForeignDuplicateLatestInvoiceScope_AsGlobalAdmin()
    {
        var itemId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var firstInvoiceId = Guid.NewGuid();
        var secondInvoiceId = Guid.NewGuid();
        var now = new DateTime(2026, 7, 31, 7, 0, 0, DateTimeKind.Utc);
        long firstRevision;
        long secondRevision;
        long itemRevision;
        long stockRevision;

        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            var item = new Item
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Shared,
                NameOriginal = "Foreign duplicate latest item",
                NameMatchKey = "FOREIGNDUPLICATELATESTITEM",
                Unit = "EA",
                ItemKind = ItemKinds.Product,
                TrackingType = ItemTrackingTypes.Stock,
                CurrentStock = 92m
            };
            var stock = new ItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode = OfficeCodeCatalog.ItworldMainWarehouse,
                Quantity = 92m,
                UpdatedAtUtc = now.AddMinutes(-5)
            };
            seedDb.Items.Add(item);
            seedDb.ItemWarehouseStocks.Add(stock);
            seedDb.Customers.Add(new Customer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                NameOriginal = "Foreign duplicate latest customer",
                NameMatchKey = "FOREIGNDUPLICATELATESTCUSTOMER",
                TradeType = CustomerClassificationNormalizer.Sales
            });
            seedDb.Invoices.AddRange(
                CreateVersionedInvoice(
                    firstInvoiceId,
                    firstInvoiceId,
                    1,
                    null,
                    customerId,
                    TenantScopeCatalog.Itworld,
                    OfficeCodeCatalog.Itworld,
                    OfficeCodeCatalog.Itworld,
                    OfficeCodeCatalog.ItworldMainWarehouse,
                    true,
                    itemId,
                    3m,
                    now),
                CreateVersionedInvoice(
                    secondInvoiceId,
                    firstInvoiceId,
                    2,
                    firstInvoiceId,
                    customerId,
                    TenantScopeCatalog.Itworld,
                    OfficeCodeCatalog.Itworld,
                    OfficeCodeCatalog.Itworld,
                    OfficeCodeCatalog.ItworldMainWarehouse,
                    true,
                    itemId,
                    5m,
                    now.AddMinutes(1)));
            await seedDb.SaveChangesAsync();
            firstRevision = await seedDb.Invoices
                .IgnoreQueryFilters()
                .Where(invoice => invoice.Id == firstInvoiceId)
                .Select(invoice => invoice.Revision)
                .SingleAsync();
            secondRevision = await seedDb.Invoices
                .IgnoreQueryFilters()
                .Where(invoice => invoice.Id == secondInvoiceId)
                .Select(invoice => invoice.Revision)
                .SingleAsync();
            itemRevision = item.Revision;
            stockRevision = stock.Revision;
        }

        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);
        var response = await CreateController(dbContext, currentUser)
            .Push(
                new SyncPushRequest
                {
                    DeviceId = "empty-push-foreign-duplicate"
                },
                CancellationToken.None);

        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(0, result.ConflictCount);

        dbContext.ChangeTracker.Clear();
        var versions = await dbContext.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(invoice =>
                invoice.Id == firstInvoiceId ||
                invoice.Id == secondInvoiceId)
            .ToDictionaryAsync(invoice => invoice.Id);
        Assert.True(versions[firstInvoiceId].IsLatestVersion);
        Assert.True(versions[secondInvoiceId].IsLatestVersion);
        Assert.Equal(firstRevision, versions[firstInvoiceId].Revision);
        Assert.Equal(secondRevision, versions[secondInvoiceId].Revision);
        var storedItem = await dbContext.Items
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(item => item.Id == itemId);
        Assert.Equal(92m, storedItem.CurrentStock);
        Assert.Equal(itemRevision, storedItem.Revision);
        var storedStock = await dbContext.ItemWarehouseStocks
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(stock =>
                stock.ItemId == itemId &&
                stock.WarehouseCode ==
                    OfficeCodeCatalog.ItworldMainWarehouse);
        Assert.Equal(92m, storedStock.Quantity);
        Assert.Equal(stockRevision, storedStock.Revision);
        Assert.False(await dbContext.InventoryLedgerEntries
            .AsNoTracking()
            .AnyAsync(entry =>
                entry.SourceDocumentId == firstInvoiceId ||
                entry.SourceDocumentId == secondInvoiceId));
    }

    [Fact]
    public async Task Push_DeleteLatestInvoice_RejectsBeforePromotingVersionFromForeignWarehouse()
    {
        var itemId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var firstInvoiceId = Guid.NewGuid();
        var latestInvoiceId = Guid.NewGuid();
        var now = new DateTime(2026, 7, 31, 7, 10, 0, DateTimeKind.Utc);
        long firstRevision;
        long latestRevision;
        long usenetStockRevision;
        long yeonsuStockRevision;

        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            seedDb.Items.Add(
                CreateStockItem(
                    itemId,
                    "Cross warehouse promotion item",
                    currentStock: 192m));
            seedDb.ItemWarehouseStocks.AddRange(
                new ItemWarehouseStock
                {
                    ItemId = itemId,
                    WarehouseCode =
                        OfficeCodeCatalog.UsenetMainWarehouse,
                    Quantity = 95m,
                    UpdatedAtUtc = now.AddMinutes(-5)
                },
                new ItemWarehouseStock
                {
                    ItemId = itemId,
                    WarehouseCode =
                        OfficeCodeCatalog.YeonsuMainWarehouse,
                    Quantity = 97m,
                    UpdatedAtUtc = now.AddMinutes(-5)
                });
            seedDb.Customers.Add(new Customer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Cross warehouse promotion customer",
                NameMatchKey = "CROSSWAREHOUSEPROMOTIONCUSTOMER",
                TradeType = CustomerClassificationNormalizer.Sales
            });
            seedDb.Invoices.AddRange(
                CreateVersionedInvoice(
                    firstInvoiceId,
                    firstInvoiceId,
                    1,
                    null,
                    customerId,
                    TenantScopeCatalog.UsenetGroup,
                    OfficeCodeCatalog.Usenet,
                    OfficeCodeCatalog.Usenet,
                    OfficeCodeCatalog.YeonsuMainWarehouse,
                    false,
                    itemId,
                    3m,
                    now),
                CreateVersionedInvoice(
                    latestInvoiceId,
                    firstInvoiceId,
                    2,
                    firstInvoiceId,
                    customerId,
                    TenantScopeCatalog.UsenetGroup,
                    OfficeCodeCatalog.Usenet,
                    OfficeCodeCatalog.Usenet,
                    OfficeCodeCatalog.UsenetMainWarehouse,
                    true,
                    itemId,
                    5m,
                    now.AddMinutes(1)));
            await seedDb.SaveChangesAsync();
            firstRevision = await seedDb.Invoices
                .IgnoreQueryFilters()
                .Where(invoice => invoice.Id == firstInvoiceId)
                .Select(invoice => invoice.Revision)
                .SingleAsync();
            latestRevision = await seedDb.Invoices
                .IgnoreQueryFilters()
                .Where(invoice => invoice.Id == latestInvoiceId)
                .Select(invoice => invoice.Revision)
                .SingleAsync();
            usenetStockRevision = await seedDb.ItemWarehouseStocks
                .Where(stock =>
                    stock.ItemId == itemId &&
                    stock.WarehouseCode ==
                        OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => stock.Revision)
                .SingleAsync();
            yeonsuStockRevision = await seedDb.ItemWarehouseStocks
                .Where(stock =>
                    stock.ItemId == itemId &&
                    stock.WarehouseCode ==
                        OfficeCodeCatalog.YeonsuMainWarehouse)
                .Select(stock => stock.Revision)
                .SingleAsync();
        }

        var currentUser = CreateInvoiceOfficeUser(
            "usenet-delete-foreign-warehouse-promotion",
            OfficeCodeCatalog.Usenet);
        var deleteMutation = BuildInventoryInvoiceDto(
            latestInvoiceId,
            customerId,
            itemId,
            "Cross warehouse promotion item",
            VoucherType.Sales,
            5m,
            currentUser.Username,
            now.AddMinutes(2));
        deleteMutation.VersionGroupId = firstInvoiceId;
        deleteMutation.VersionNumber = 2;
        deleteMutation.PreviousVersionId = firstInvoiceId;
        deleteMutation.IsLatestVersion = true;
        deleteMutation.IsDeleted = true;
        deleteMutation.Revision = latestRevision;
        deleteMutation.ExpectedRevision = latestRevision;
        deleteMutation.MutationId =
            $"foreign-warehouse-promotion:Invoice:{latestInvoiceId:N}:delete";

        await using var dbContext = CreateDbContext(currentUser);
        var response = await CreateController(dbContext, currentUser)
            .Push(
                new SyncPushRequest
                {
                    DeviceId =
                        "delete-foreign-warehouse-promotion-device",
                    Invoices = [deleteMutation]
                },
                CancellationToken.None);

        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(
            result.Conflicts,
            conflict =>
                conflict.EntityName == nameof(Invoice) &&
                conflict.Reason.Contains(
                    "version normalization",
                    StringComparison.OrdinalIgnoreCase));

        dbContext.ChangeTracker.Clear();
        var versions = await dbContext.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(invoice =>
                invoice.Id == firstInvoiceId ||
                invoice.Id == latestInvoiceId)
            .ToDictionaryAsync(invoice => invoice.Id);
        Assert.False(versions[firstInvoiceId].IsLatestVersion);
        Assert.False(versions[firstInvoiceId].IsDeleted);
        Assert.Equal(firstRevision, versions[firstInvoiceId].Revision);
        Assert.True(versions[latestInvoiceId].IsLatestVersion);
        Assert.False(versions[latestInvoiceId].IsDeleted);
        Assert.Equal(latestRevision, versions[latestInvoiceId].Revision);
        var stocks = await dbContext.ItemWarehouseStocks
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(stock => stock.ItemId == itemId)
            .ToDictionaryAsync(stock => stock.WarehouseCode);
        Assert.Equal(95m, stocks[OfficeCodeCatalog.UsenetMainWarehouse].Quantity);
        Assert.Equal(
            usenetStockRevision,
            stocks[OfficeCodeCatalog.UsenetMainWarehouse].Revision);
        Assert.Equal(97m, stocks[OfficeCodeCatalog.YeonsuMainWarehouse].Quantity);
        Assert.Equal(
            yeonsuStockRevision,
            stocks[OfficeCodeCatalog.YeonsuMainWarehouse].Revision);
        Assert.DoesNotContain(
            await dbContext.ProcessedSyncMutations
                .AsNoTracking()
                .ToListAsync(),
            receipt =>
                receipt.MutationId == deleteMutation.MutationId);
    }

    [Fact]
    public async Task Push_NewInvoiceVersion_RejectsBeforeDemotingVersionFromForeignWarehouse()
    {
        var itemId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var firstInvoiceId = Guid.NewGuid();
        var secondInvoiceId = Guid.NewGuid();
        var now = new DateTime(2026, 7, 31, 7, 20, 0, DateTimeKind.Utc);
        long firstRevision;
        long yeonsuStockRevision;

        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            seedDb.Items.Add(
                CreateStockItem(
                    itemId,
                    "Cross warehouse new version item",
                    currentStock: 97m));
            seedDb.ItemWarehouseStocks.AddRange(
                new ItemWarehouseStock
                {
                    ItemId = itemId,
                    WarehouseCode =
                        OfficeCodeCatalog.UsenetMainWarehouse,
                    Quantity = 100m,
                    UpdatedAtUtc = now.AddMinutes(-5)
                },
                new ItemWarehouseStock
                {
                    ItemId = itemId,
                    WarehouseCode =
                        OfficeCodeCatalog.YeonsuMainWarehouse,
                    Quantity = 97m,
                    UpdatedAtUtc = now.AddMinutes(-5)
                });
            seedDb.Customers.Add(new Customer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Cross warehouse new version customer",
                NameMatchKey = "CROSSWAREHOUSENEWVERSIONCUSTOMER",
                TradeType = CustomerClassificationNormalizer.Sales
            });
            seedDb.Invoices.Add(
                CreateVersionedInvoice(
                    firstInvoiceId,
                    firstInvoiceId,
                    1,
                    null,
                    customerId,
                    TenantScopeCatalog.UsenetGroup,
                    OfficeCodeCatalog.Usenet,
                    OfficeCodeCatalog.Usenet,
                    OfficeCodeCatalog.YeonsuMainWarehouse,
                    true,
                    itemId,
                    3m,
                    now));
            await seedDb.SaveChangesAsync();
            firstRevision = await seedDb.Invoices
                .IgnoreQueryFilters()
                .Where(invoice => invoice.Id == firstInvoiceId)
                .Select(invoice => invoice.Revision)
                .SingleAsync();
            yeonsuStockRevision = await seedDb.ItemWarehouseStocks
                .Where(stock =>
                    stock.ItemId == itemId &&
                    stock.WarehouseCode ==
                        OfficeCodeCatalog.YeonsuMainWarehouse)
                .Select(stock => stock.Revision)
                .SingleAsync();
        }

        var currentUser = CreateInvoiceOfficeUser(
            "usenet-new-version-foreign-warehouse",
            OfficeCodeCatalog.Usenet);
        var newVersion = BuildInventoryInvoiceDto(
            secondInvoiceId,
            customerId,
            itemId,
            "Cross warehouse new version item",
            VoucherType.Sales,
            5m,
            currentUser.Username,
            now.AddMinutes(1));
        newVersion.VersionGroupId = firstInvoiceId;
        newVersion.VersionNumber = 2;
        newVersion.PreviousVersionId = firstInvoiceId;
        newVersion.Revision = firstRevision;
        newVersion.ExpectedRevision = firstRevision;
        newVersion.MutationId =
            $"foreign-warehouse-new-version:Invoice:{secondInvoiceId:N}";

        await using var dbContext = CreateDbContext(currentUser);
        var response = await CreateController(dbContext, currentUser)
            .Push(
                new SyncPushRequest
                {
                    DeviceId =
                        "new-version-foreign-warehouse-device",
                    Invoices = [newVersion]
                },
                CancellationToken.None);

        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(
            result.Conflicts,
            conflict =>
                conflict.EntityName == nameof(Invoice) &&
                conflict.Reason.Contains(
                    "version normalization",
                    StringComparison.OrdinalIgnoreCase));

        dbContext.ChangeTracker.Clear();
        var firstVersion = await dbContext.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(invoice => invoice.Id == firstInvoiceId);
        Assert.True(firstVersion.IsLatestVersion);
        Assert.Equal(firstRevision, firstVersion.Revision);
        Assert.False(await dbContext.Invoices
            .IgnoreQueryFilters()
            .AnyAsync(invoice => invoice.Id == secondInvoiceId));
        var yeonsuStock = await dbContext.ItemWarehouseStocks
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(stock =>
                stock.ItemId == itemId &&
                stock.WarehouseCode ==
                    OfficeCodeCatalog.YeonsuMainWarehouse);
        Assert.Equal(97m, yeonsuStock.Quantity);
        Assert.Equal(yeonsuStockRevision, yeonsuStock.Revision);
        Assert.DoesNotContain(
            await dbContext.ProcessedSyncMutations
                .AsNoTracking()
                .ToListAsync(),
            receipt =>
                receipt.MutationId == newVersion.MutationId);
    }

    [Fact]
    public async Task Push_NewInvoiceVersion_RejectsBeforeDemotingVersionWithForeignRentalProfile()
    {
        var customerId = Guid.NewGuid();
        var firstInvoiceId = Guid.NewGuid();
        var secondInvoiceId = Guid.NewGuid();
        var foreignProfileId = Guid.NewGuid();
        var now = new DateTime(2026, 7, 31, 7, 30, 0, DateTimeKind.Utc);
        long firstRevision;
        long profileRevision;

        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            seedDb.Customers.Add(new Customer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Foreign rental version customer",
                NameMatchKey = "FOREIGNRENTALVERSIONCUSTOMER",
                TradeType = CustomerClassificationNormalizer.Sales
            });
            var profile = new RentalBillingProfile
            {
                Id = foreignProfileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Yeonsu,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                ProfileKey = "FOREIGN-VERSION-PROFILE",
                CustomerName = "Foreign rental version customer",
                ItemName = "Rental item",
                BillingDay = 25,
                MonthlyAmount = 100m
            };
            seedDb.RentalBillingProfiles.Add(profile);
            seedDb.Invoices.Add(
                CreateVersionedInvoice(
                    firstInvoiceId,
                    firstInvoiceId,
                    1,
                    null,
                    customerId,
                    TenantScopeCatalog.UsenetGroup,
                    OfficeCodeCatalog.Usenet,
                    OfficeCodeCatalog.Usenet,
                    OfficeCodeCatalog.UsenetMainWarehouse,
                    true,
                    null,
                    0m,
                    now,
                    linkedRentalBillingProfileId: foreignProfileId));
            await seedDb.SaveChangesAsync();
            firstRevision = await seedDb.Invoices
                .IgnoreQueryFilters()
                .Where(invoice => invoice.Id == firstInvoiceId)
                .Select(invoice => invoice.Revision)
                .SingleAsync();
            profileRevision = profile.Revision;
        }

        var currentUser = CreateInvoiceOfficeUser(
            "usenet-new-version-foreign-rental",
            OfficeCodeCatalog.Usenet);
        var newVersion = new InvoiceDto
        {
            Id = secondInvoiceId,
            CustomerId = customerId,
            CustomerName = "Foreign rental version customer",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "FOREIGN-RENTAL-VERSION-0002",
            VersionGroupId = firstInvoiceId,
            VersionNumber = 2,
            PreviousVersionId = firstInvoiceId,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            SourceWarehouseCode =
                OfficeCodeCatalog.UsenetMainWarehouse,
            InvoiceDate = new DateOnly(2026, 7, 31),
            Revision = firstRevision,
            ExpectedRevision = firstRevision,
            CreatedAtUtc = now.AddMinutes(1),
            UpdatedAtUtc = now.AddMinutes(1),
            MutationId =
                $"foreign-rental-new-version:Invoice:{secondInvoiceId:N}",
            MutationCreatedAtUtc = now.AddMinutes(1)
        };

        await using var dbContext = CreateDbContext(currentUser);
        var response = await CreateController(dbContext, currentUser)
            .Push(
                new SyncPushRequest
                {
                    DeviceId =
                        "new-version-foreign-rental-device",
                    Invoices = [newVersion]
                },
                CancellationToken.None);

        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(
            result.Conflicts,
            conflict =>
                conflict.EntityName == nameof(Invoice) &&
                conflict.Reason.Contains(
                    "version normalization",
                    StringComparison.OrdinalIgnoreCase));

        dbContext.ChangeTracker.Clear();
        var firstVersion = await dbContext.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(invoice => invoice.Id == firstInvoiceId);
        Assert.True(firstVersion.IsLatestVersion);
        Assert.Equal(firstRevision, firstVersion.Revision);
        Assert.False(await dbContext.Invoices
            .IgnoreQueryFilters()
            .AnyAsync(invoice => invoice.Id == secondInvoiceId));
        Assert.Equal(
            profileRevision,
            await dbContext.RentalBillingProfiles
                .IgnoreQueryFilters()
                .Where(profile => profile.Id == foreignProfileId)
                .Select(profile => profile.Revision)
                .SingleAsync());
        Assert.DoesNotContain(
            await dbContext.ProcessedSyncMutations
                .AsNoTracking()
                .ToListAsync(),
            receipt =>
                receipt.MutationId == newVersion.MutationId);
    }

    [Fact]
    public async Task Push_TouchedDuplicateLatestInvoice_RecalculatesRentalSettlementForDemotedVersion()
    {
        var customerId = Guid.NewGuid();
        var firstInvoiceId = Guid.NewGuid();
        var secondInvoiceId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var now = new DateTime(2026, 7, 31, 7, 40, 0, DateTimeKind.Utc);
        long secondRevision;

        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            seedDb.Customers.Add(new Customer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Duplicate latest rental customer",
                NameMatchKey = "DUPLICATELATESTRENTALCUSTOMER",
                TradeType = CustomerClassificationNormalizer.Sales
            });
            seedDb.RentalBillingProfiles.Add(new RentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ProfileKey = "DUPLICATE-LATEST-SETTLEMENT-PROFILE",
                CustomerName = "Duplicate latest rental customer",
                ItemName = "Rental item",
                BillingDay = 25,
                MonthlyAmount = 100m,
                SettledAmount = 40m,
                OutstandingAmount = 60m
            });
            seedDb.Invoices.AddRange(
                CreateVersionedInvoice(
                    firstInvoiceId,
                    firstInvoiceId,
                    1,
                    null,
                    customerId,
                    TenantScopeCatalog.UsenetGroup,
                    OfficeCodeCatalog.Usenet,
                    OfficeCodeCatalog.Usenet,
                    OfficeCodeCatalog.UsenetMainWarehouse,
                    true,
                    null,
                    0m,
                    now,
                    linkedRentalBillingProfileId: profileId,
                    totalAmount: 100m),
                CreateVersionedInvoice(
                    secondInvoiceId,
                    firstInvoiceId,
                    2,
                    firstInvoiceId,
                    customerId,
                    TenantScopeCatalog.UsenetGroup,
                    OfficeCodeCatalog.Usenet,
                    OfficeCodeCatalog.Usenet,
                    OfficeCodeCatalog.UsenetMainWarehouse,
                    true,
                    null,
                    0m,
                    now.AddMinutes(1),
                    totalAmount: 100m));
            seedDb.Payments.Add(new Payment
            {
                Id = Guid.NewGuid(),
                InvoiceId = firstInvoiceId,
                PaymentDate = new DateOnly(2026, 7, 31),
                Amount = 40m
            });
            await seedDb.SaveChangesAsync();
            secondRevision = await seedDb.Invoices
                .IgnoreQueryFilters()
                .Where(invoice => invoice.Id == secondInvoiceId)
                .Select(invoice => invoice.Revision)
                .SingleAsync();
        }

        var currentUser = new TestCurrentUserContext
        {
            Username =
                "usenet-touched-rental-recalculation",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions =
            [
                PermissionNames.InvoiceEdit,
                PermissionNames.ItemEdit,
                PermissionNames.PaymentEdit,
                PermissionNames.RentalProfileEdit
            ]
        };
        var touchMutation = new InvoiceDto
        {
            Id = secondInvoiceId,
            CustomerId = customerId,
            CustomerName = "Duplicate latest rental customer",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = $"VERSION-2-{secondInvoiceId:N}",
            VersionGroupId = firstInvoiceId,
            VersionNumber = 2,
            PreviousVersionId = firstInvoiceId,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            SourceWarehouseCode =
                OfficeCodeCatalog.UsenetMainWarehouse,
            InvoiceDate = DateOnly.FromDateTime(now.AddMinutes(1)),
            SupplyAmount = 100m,
            TotalAmount = 100m,
            Revision = secondRevision,
            ExpectedRevision = secondRevision,
            CreatedAtUtc = now.AddMinutes(1),
            UpdatedAtUtc = now.AddMinutes(2),
            MutationId =
                $"touched-rental-recalculation:Invoice:{secondInvoiceId:N}",
            MutationCreatedAtUtc = now.AddMinutes(2)
        };
        await using var dbContext = CreateDbContext(currentUser);
        var response = await CreateController(dbContext, currentUser)
            .Push(
                new SyncPushRequest
                {
                    DeviceId =
                        "touched-rental-recalculation-device",
                    Invoices = [touchMutation]
                },
                CancellationToken.None);

        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(1, result.AcceptedCount);
        Assert.Equal(0, result.ConflictCount);

        dbContext.ChangeTracker.Clear();
        var versions = await dbContext.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(invoice =>
                invoice.Id == firstInvoiceId ||
                invoice.Id == secondInvoiceId)
            .ToDictionaryAsync(invoice => invoice.Id);
        Assert.False(versions[firstInvoiceId].IsLatestVersion);
        Assert.True(versions[secondInvoiceId].IsLatestVersion);
        var profile = await dbContext.RentalBillingProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == profileId);
        Assert.Equal(0m, profile.SettledAmount);
        Assert.Equal(100m, profile.OutstandingAmount);
    }

    [Fact]
    public async Task Push_NonInvoiceMutation_DoesNotRepairUntouchedDuplicateLatestInvoiceOrRentalSettlement()
    {
        var customerId = Guid.NewGuid();
        var firstInvoiceId = Guid.NewGuid();
        var secondInvoiceId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var now = new DateTime(2026, 7, 31, 7, 50, 0, DateTimeKind.Utc);
        long firstRevision;
        long secondRevision;
        long profileRevision;

        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            seedDb.Customers.Add(new Customer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Untouched rental duplicate customer",
                NameMatchKey = "UNTOUCHEDRENTALDUPLICATECUSTOMER",
                TradeType = CustomerClassificationNormalizer.Sales
            });
            var profile = new RentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ProfileKey = "UNTOUCHED-DUPLICATE-PROFILE",
                CustomerName = "Untouched rental duplicate customer",
                ItemName = "Rental item",
                BillingDay = 25,
                MonthlyAmount = 100m,
                SettledAmount = 40m,
                OutstandingAmount = 60m
            };
            seedDb.RentalBillingProfiles.Add(profile);
            seedDb.Invoices.AddRange(
                CreateVersionedInvoice(
                    firstInvoiceId,
                    firstInvoiceId,
                    1,
                    null,
                    customerId,
                    TenantScopeCatalog.UsenetGroup,
                    OfficeCodeCatalog.Usenet,
                    OfficeCodeCatalog.Usenet,
                    OfficeCodeCatalog.UsenetMainWarehouse,
                    true,
                    null,
                    0m,
                    now,
                    linkedRentalBillingProfileId: profileId,
                    totalAmount: 100m),
                CreateVersionedInvoice(
                    secondInvoiceId,
                    firstInvoiceId,
                    2,
                    firstInvoiceId,
                    customerId,
                    TenantScopeCatalog.UsenetGroup,
                    OfficeCodeCatalog.Usenet,
                    OfficeCodeCatalog.Usenet,
                    OfficeCodeCatalog.UsenetMainWarehouse,
                    true,
                    null,
                    0m,
                    now.AddMinutes(1),
                    totalAmount: 100m));
            seedDb.Payments.Add(new Payment
            {
                Id = Guid.NewGuid(),
                InvoiceId = firstInvoiceId,
                PaymentDate = new DateOnly(2026, 7, 31),
                Amount = 40m
            });
            await seedDb.SaveChangesAsync();
            firstRevision = await seedDb.Invoices
                .IgnoreQueryFilters()
                .Where(invoice => invoice.Id == firstInvoiceId)
                .Select(invoice => invoice.Revision)
                .SingleAsync();
            secondRevision = await seedDb.Invoices
                .IgnoreQueryFilters()
                .Where(invoice => invoice.Id == secondInvoiceId)
                .Select(invoice => invoice.Revision)
                .SingleAsync();
            profileRevision = profile.Revision;
        }

        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);
        var response = await CreateController(dbContext, currentUser)
            .Push(
                new SyncPushRequest
                {
                    DeviceId =
                        "non-invoice-untouched-duplicate-device",
                    CustomerCategories =
                    [
                        new CustomerCategoryDto
                        {
                            Id = categoryId,
                            Name =
                                $"Unrelated category {categoryId:N}",
                            CreatedAtUtc = now.AddMinutes(2),
                            UpdatedAtUtc = now.AddMinutes(2),
                            MutationId =
                                $"non-invoice-untouched:CustomerCategory:{categoryId:N}",
                            MutationCreatedAtUtc =
                                now.AddMinutes(2)
                        }
                    ]
                },
                CancellationToken.None);

        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(1, result.AcceptedCount);
        Assert.Equal(0, result.ConflictCount);
        Assert.True(await dbContext.CustomerCategories
            .IgnoreQueryFilters()
            .AnyAsync(category => category.Id == categoryId));

        dbContext.ChangeTracker.Clear();
        var versions = await dbContext.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(invoice =>
                invoice.Id == firstInvoiceId ||
                invoice.Id == secondInvoiceId)
            .ToDictionaryAsync(invoice => invoice.Id);
        Assert.True(versions[firstInvoiceId].IsLatestVersion);
        Assert.True(versions[secondInvoiceId].IsLatestVersion);
        Assert.Equal(firstRevision, versions[firstInvoiceId].Revision);
        Assert.Equal(secondRevision, versions[secondInvoiceId].Revision);
        var storedProfile = await dbContext.RentalBillingProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(profile => profile.Id == profileId);
        Assert.Equal(40m, storedProfile.SettledAmount);
        Assert.Equal(60m, storedProfile.OutstandingAmount);
        Assert.Equal(profileRevision, storedProfile.Revision);
        Assert.False(await dbContext.InventoryLedgerEntries
            .AsNoTracking()
            .AnyAsync(entry =>
                entry.SourceDocumentId == firstInvoiceId ||
                entry.SourceDocumentId == secondInvoiceId));
    }

    [Fact]
    public async Task Push_RedeleteLegacyDeletedLatestInvoice_RejectsUnreadablePromotedVersionItem()
    {
        var itemId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var firstInvoiceId = Guid.NewGuid();
        var deletedLatestInvoiceId = Guid.NewGuid();
        var now = new DateTime(2026, 7, 31, 8, 0, 0, DateTimeKind.Utc);
        long firstRevision;
        long deletedLatestRevision;
        long itemRevision;
        long stockRevision;

        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            var item = new Item
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                NameOriginal = "Unreadable promoted version item",
                NameMatchKey = "UNREADABLEPROMOTEDVERSIONITEM",
                Unit = "EA",
                ItemKind = ItemKinds.Product,
                TrackingType = ItemTrackingTypes.Stock,
                CurrentStock = 10m
            };
            var stock = new ItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode =
                    OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 10m,
                UpdatedAtUtc = now.AddMinutes(-5)
            };
            seedDb.Items.Add(item);
            seedDb.ItemWarehouseStocks.Add(stock);
            seedDb.Customers.Add(new Customer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Unreadable promotion customer",
                NameMatchKey = "UNREADABLEPROMOTIONCUSTOMER",
                TradeType = CustomerClassificationNormalizer.Sales
            });
            seedDb.Invoices.AddRange(
                CreateVersionedInvoice(
                    firstInvoiceId,
                    firstInvoiceId,
                    1,
                    null,
                    customerId,
                    TenantScopeCatalog.UsenetGroup,
                    OfficeCodeCatalog.Usenet,
                    OfficeCodeCatalog.Usenet,
                    OfficeCodeCatalog.UsenetMainWarehouse,
                    false,
                    itemId,
                    3m,
                    now),
                new Invoice
                {
                    Id = deletedLatestInvoiceId,
                    CustomerId = customerId,
                    TenantCode =
                        TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode =
                        OfficeCodeCatalog.Usenet,
                    SourceWarehouseCode =
                        OfficeCodeCatalog.UsenetMainWarehouse,
                    InvoiceNumber =
                        "UNREADABLE-PROMOTION-DELETED-0002",
                    VersionGroupId = firstInvoiceId,
                    VersionNumber = 2,
                    PreviousVersionId = firstInvoiceId,
                    IsLatestVersion = true,
                    IsDeleted = true,
                    VoucherType = VoucherType.Sales,
                    InvoiceDate = new DateOnly(2026, 7, 31),
                    CreatedAtUtc = now.AddMinutes(1),
                    UpdatedAtUtc = now.AddMinutes(1)
                });
            await seedDb.SaveChangesAsync();
            firstRevision = await seedDb.Invoices
                .IgnoreQueryFilters()
                .Where(invoice => invoice.Id == firstInvoiceId)
                .Select(invoice => invoice.Revision)
                .SingleAsync();
            deletedLatestRevision = await seedDb.Invoices
                .IgnoreQueryFilters()
                .Where(invoice =>
                    invoice.Id == deletedLatestInvoiceId)
                .Select(invoice => invoice.Revision)
                .SingleAsync();
            itemRevision = item.Revision;
            stockRevision = stock.Revision;
        }

        var currentUser = CreateInvoiceOfficeUser(
            "usenet-redelete-unreadable-promotion",
            OfficeCodeCatalog.Usenet);
        var deleteMutation = new InvoiceDto
        {
            Id = deletedLatestInvoiceId,
            CustomerId = customerId,
            CustomerName = "Unreadable promotion customer",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            SourceWarehouseCode =
                OfficeCodeCatalog.UsenetMainWarehouse,
            InvoiceNumber =
                "UNREADABLE-PROMOTION-DELETED-0002",
            VersionGroupId = firstInvoiceId,
            VersionNumber = 2,
            PreviousVersionId = firstInvoiceId,
            IsLatestVersion = true,
            IsDeleted = true,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 7, 31),
            Revision = deletedLatestRevision,
            ExpectedRevision = deletedLatestRevision,
            CreatedAtUtc = now.AddMinutes(1),
            UpdatedAtUtc = now.AddMinutes(2),
            MutationId =
                $"redelete-unreadable-promotion:Invoice:{deletedLatestInvoiceId:N}",
            MutationCreatedAtUtc = now.AddMinutes(2)
        };

        await using var dbContext = CreateDbContext(currentUser);
        var response = await CreateController(dbContext, currentUser)
            .Push(
                new SyncPushRequest
                {
                    DeviceId =
                        "redelete-unreadable-promotion-device",
                    Invoices = [deleteMutation]
                },
                CancellationToken.None);

        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(
            result.Conflicts,
            conflict =>
                conflict.EntityName == nameof(Invoice) &&
                conflict.Reason.Contains(
                    "version normalization",
                    StringComparison.OrdinalIgnoreCase));

        dbContext.ChangeTracker.Clear();
        var versions = await dbContext.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(invoice =>
                invoice.Id == firstInvoiceId ||
                invoice.Id == deletedLatestInvoiceId)
            .ToDictionaryAsync(invoice => invoice.Id);
        Assert.False(versions[firstInvoiceId].IsLatestVersion);
        Assert.Equal(firstRevision, versions[firstInvoiceId].Revision);
        Assert.True(
            versions[deletedLatestInvoiceId].IsDeleted);
        Assert.True(
            versions[deletedLatestInvoiceId].IsLatestVersion);
        Assert.Equal(
            deletedLatestRevision,
            versions[deletedLatestInvoiceId].Revision);
        var storedItem = await dbContext.Items
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(item => item.Id == itemId);
        Assert.Equal(10m, storedItem.CurrentStock);
        Assert.Equal(itemRevision, storedItem.Revision);
        var storedStock = await dbContext.ItemWarehouseStocks
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(stock =>
                stock.ItemId == itemId &&
                stock.WarehouseCode ==
                    OfficeCodeCatalog.UsenetMainWarehouse);
        Assert.Equal(10m, storedStock.Quantity);
        Assert.Equal(stockRevision, storedStock.Revision);
        Assert.False(await dbContext.InventoryLedgerEntries
            .AnyAsync(entry =>
                entry.SourceDocumentId == firstInvoiceId ||
                entry.SourceDocumentId ==
                    deletedLatestInvoiceId));
        Assert.DoesNotContain(
            await dbContext.ProcessedSyncMutations
                .AsNoTracking()
                .ToListAsync(),
            receipt =>
                receipt.MutationId == deleteMutation.MutationId);
    }

    [Fact]
    public async Task Push_TouchedDuplicateLatestInvoice_RejectsPaymentSideEffectWithoutPaymentEdit()
    {
        var itemId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var firstInvoiceId = Guid.NewGuid();
        var secondInvoiceId = Guid.NewGuid();
        var now = new DateTime(2026, 7, 31, 8, 10, 0, DateTimeKind.Utc);
        long firstRevision;
        long secondRevision;
        long itemRevision;
        long stockRevision;

        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            var item = CreateStockItem(
                itemId,
                "Duplicate latest payment item",
                currentStock: 92m);
            var stock = new ItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode =
                    OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 92m,
                UpdatedAtUtc = now.AddMinutes(-5)
            };
            seedDb.Items.Add(item);
            seedDb.ItemWarehouseStocks.Add(stock);
            seedDb.Customers.Add(new Customer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Duplicate latest payment customer",
                NameMatchKey = "DUPLICATELATESTPAYMENTCUSTOMER",
                TradeType = CustomerClassificationNormalizer.Sales
            });
            seedDb.Invoices.AddRange(
                CreateVersionedInvoice(
                    firstInvoiceId,
                    firstInvoiceId,
                    1,
                    null,
                    customerId,
                    TenantScopeCatalog.UsenetGroup,
                    OfficeCodeCatalog.Usenet,
                    OfficeCodeCatalog.Usenet,
                    OfficeCodeCatalog.UsenetMainWarehouse,
                    true,
                    itemId,
                    3m,
                    now),
                CreateVersionedInvoice(
                    secondInvoiceId,
                    firstInvoiceId,
                    2,
                    firstInvoiceId,
                    customerId,
                    TenantScopeCatalog.UsenetGroup,
                    OfficeCodeCatalog.Usenet,
                    OfficeCodeCatalog.Usenet,
                    OfficeCodeCatalog.UsenetMainWarehouse,
                    true,
                    itemId,
                    5m,
                    now.AddMinutes(1)));
            seedDb.Payments.Add(new Payment
            {
                Id = Guid.NewGuid(),
                InvoiceId = firstInvoiceId,
                PaymentDate = new DateOnly(2026, 7, 31),
                Amount = 3m
            });
            await seedDb.SaveChangesAsync();
            firstRevision = await seedDb.Invoices
                .IgnoreQueryFilters()
                .Where(invoice => invoice.Id == firstInvoiceId)
                .Select(invoice => invoice.Revision)
                .SingleAsync();
            secondRevision = await seedDb.Invoices
                .IgnoreQueryFilters()
                .Where(invoice => invoice.Id == secondInvoiceId)
                .Select(invoice => invoice.Revision)
                .SingleAsync();
            itemRevision = item.Revision;
            stockRevision = stock.Revision;
        }

        var currentUser = CreateInvoiceOfficeUser(
            "usenet-duplicate-payment-no-payment-edit",
            OfficeCodeCatalog.Usenet);
        var touchMutation = BuildInventoryInvoiceDto(
            secondInvoiceId,
            customerId,
            itemId,
            "Duplicate latest payment item",
            VoucherType.Sales,
            5m,
            currentUser.Username,
            now.AddMinutes(2));
        touchMutation.VersionGroupId = firstInvoiceId;
        touchMutation.VersionNumber = 2;
        touchMutation.PreviousVersionId = firstInvoiceId;
        touchMutation.IsLatestVersion = true;
        touchMutation.Revision = secondRevision;
        touchMutation.ExpectedRevision = secondRevision;
        touchMutation.MutationId =
            $"duplicate-payment-touch:Invoice:{secondInvoiceId:N}";

        await using var dbContext = CreateDbContext(currentUser);
        var response = await CreateController(dbContext, currentUser)
            .Push(
                new SyncPushRequest
                {
                    DeviceId =
                        "duplicate-payment-touch-device",
                    Invoices = [touchMutation]
                },
                CancellationToken.None);

        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(
            result.Conflicts,
            conflict =>
                conflict.EntityName == nameof(Invoice) &&
                conflict.Reason.Contains(
                    "version normalization",
                    StringComparison.OrdinalIgnoreCase));

        dbContext.ChangeTracker.Clear();
        var versions = await dbContext.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(invoice =>
                invoice.Id == firstInvoiceId ||
                invoice.Id == secondInvoiceId)
            .ToDictionaryAsync(invoice => invoice.Id);
        Assert.True(versions[firstInvoiceId].IsLatestVersion);
        Assert.True(versions[secondInvoiceId].IsLatestVersion);
        Assert.Equal(firstRevision, versions[firstInvoiceId].Revision);
        Assert.Equal(secondRevision, versions[secondInvoiceId].Revision);
        var storedItem = await dbContext.Items
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(item => item.Id == itemId);
        Assert.Equal(92m, storedItem.CurrentStock);
        Assert.Equal(itemRevision, storedItem.Revision);
        var storedStock = await dbContext.ItemWarehouseStocks
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(stock =>
                stock.ItemId == itemId &&
                stock.WarehouseCode ==
                    OfficeCodeCatalog.UsenetMainWarehouse);
        Assert.Equal(92m, storedStock.Quantity);
        Assert.Equal(stockRevision, storedStock.Revision);
        Assert.False(await dbContext.InventoryLedgerEntries
            .AnyAsync(entry =>
                entry.SourceDocumentId == firstInvoiceId ||
                entry.SourceDocumentId == secondInvoiceId));
        Assert.DoesNotContain(
            await dbContext.ProcessedSyncMutations
                .AsNoTracking()
                .ToListAsync(),
            receipt =>
                receipt.MutationId == touchMutation.MutationId);
    }

    [Fact]
    public async Task Push_NewInvoiceVersion_RejectsPreviousLatestForeignLinkedTransaction()
    {
        var customerId = Guid.NewGuid();
        var firstInvoiceId = Guid.NewGuid();
        var secondInvoiceId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        var now = new DateTime(2026, 7, 31, 8, 20, 0, DateTimeKind.Utc);
        long firstRevision;
        long transactionRevision;

        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            seedDb.Customers.Add(new Customer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Foreign transaction version customer",
                NameMatchKey = "FOREIGNTRANSACTIONVERSIONCUSTOMER",
                TradeType = CustomerClassificationNormalizer.Sales
            });
            seedDb.Invoices.Add(
                CreateVersionedInvoice(
                    firstInvoiceId,
                    firstInvoiceId,
                    1,
                    null,
                    customerId,
                    TenantScopeCatalog.UsenetGroup,
                    OfficeCodeCatalog.Usenet,
                    OfficeCodeCatalog.Usenet,
                    OfficeCodeCatalog.UsenetMainWarehouse,
                    true,
                    null,
                    0m,
                    now,
                    totalAmount: 100m));
            var linkedTransaction = new TransactionRecord
            {
                Id = transactionId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Yeonsu,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                TransactionDate = new DateOnly(2026, 7, 31),
                TransactionKind = "Receipt",
                LinkedInvoiceId = firstInvoiceId,
                LinkedInvoiceNumber =
                    $"VERSION-1-{firstInvoiceId:N}",
                SettlementAmount = 10m,
                ReceiptTotal = 10m,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            seedDb.Transactions.Add(linkedTransaction);
            await seedDb.SaveChangesAsync();
            firstRevision = await seedDb.Invoices
                .IgnoreQueryFilters()
                .Where(invoice => invoice.Id == firstInvoiceId)
                .Select(invoice => invoice.Revision)
                .SingleAsync();
            transactionRevision = linkedTransaction.Revision;
        }

        var currentUser = new TestCurrentUserContext
        {
            Username =
                "usenet-new-version-foreign-transaction",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions =
            [
                PermissionNames.InvoiceEdit,
                PermissionNames.ItemEdit,
                PermissionNames.PaymentEdit,
                PermissionNames.RentalProfileEdit
            ]
        };
        var newVersion = new InvoiceDto
        {
            Id = secondInvoiceId,
            CustomerId = customerId,
            CustomerName =
                "Foreign transaction version customer",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber =
                "FOREIGN-TRANSACTION-VERSION-0002",
            VersionGroupId = firstInvoiceId,
            VersionNumber = 2,
            PreviousVersionId = firstInvoiceId,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            SourceWarehouseCode =
                OfficeCodeCatalog.UsenetMainWarehouse,
            InvoiceDate = new DateOnly(2026, 7, 31),
            SupplyAmount = 100m,
            TotalAmount = 100m,
            Revision = firstRevision,
            ExpectedRevision = firstRevision,
            CreatedAtUtc = now.AddMinutes(1),
            UpdatedAtUtc = now.AddMinutes(1),
            MutationId =
                $"foreign-transaction-new-version:Invoice:{secondInvoiceId:N}",
            MutationCreatedAtUtc = now.AddMinutes(1)
        };

        await using var dbContext = CreateDbContext(currentUser);
        var response = await CreateController(dbContext, currentUser)
            .Push(
                new SyncPushRequest
                {
                    DeviceId =
                        "foreign-transaction-new-version-device",
                    Invoices = [newVersion]
                },
                CancellationToken.None);

        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(
            result.Conflicts,
            conflict =>
                conflict.EntityName == nameof(Invoice) &&
                conflict.Reason.Contains(
                    "version normalization",
                    StringComparison.OrdinalIgnoreCase));

        dbContext.ChangeTracker.Clear();
        var firstVersion = await dbContext.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(invoice => invoice.Id == firstInvoiceId);
        Assert.True(firstVersion.IsLatestVersion);
        Assert.Equal(firstRevision, firstVersion.Revision);
        Assert.False(await dbContext.Invoices
            .IgnoreQueryFilters()
            .AnyAsync(invoice => invoice.Id == secondInvoiceId));
        var storedTransaction = await dbContext.Transactions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(transaction =>
                transaction.Id == transactionId);
        Assert.Equal(
            firstInvoiceId,
            storedTransaction.LinkedInvoiceId);
        Assert.Equal(
            transactionRevision,
            storedTransaction.Revision);
        Assert.False(await dbContext.InventoryLedgerEntries
            .AnyAsync(entry =>
                entry.SourceDocumentId == firstInvoiceId ||
                entry.SourceDocumentId == secondInvoiceId));
        Assert.DoesNotContain(
            await dbContext.ProcessedSyncMutations
                .AsNoTracking()
                .ToListAsync(),
            receipt =>
                receipt.MutationId == newVersion.MutationId);
    }

    [Fact]
    public async Task Push_DeleteInvoice_RejectsActivePaymentOutsidePaymentsDataArea()
    {
        var customerId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var now = new DateTime(2026, 7, 31, 8, 30, 0, DateTimeKind.Utc);
        long invoiceRevision;
        long paymentRevision;

        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            seedDb.DataSharingPolicies.Add(new DataSharingPolicy
            {
                Id = Guid.NewGuid(),
                SourceTenantCode =
                    TenantScopeCatalog.UsenetGroup,
                SourceOfficeCode = OfficeCodeCatalog.Yeonsu,
                TargetTenantCode =
                    TenantScopeCatalog.UsenetGroup,
                TargetOfficeCode = OfficeCodeCatalog.Usenet,
                ShareCustomers = true,
                ShareItems = true,
                ShareInvoices = true,
                SharePayments = false,
                ShareContracts = false,
                ShareReports = false,
                ShareRentals = false,
                ShareDeliveries = false,
                AllowTargetWrite = true,
                Note =
                    "invoice write is shared but payments are not"
            });
            seedDb.Customers.Add(new Customer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Yeonsu,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                NameOriginal = "Payments data area customer",
                NameMatchKey = "PAYMENTSDATAAREACUSTOMER",
                TradeType = CustomerClassificationNormalizer.Sales
            });
            seedDb.Invoices.Add(
                CreateVersionedInvoice(
                    invoiceId,
                    invoiceId,
                    1,
                    null,
                    customerId,
                    TenantScopeCatalog.UsenetGroup,
                    OfficeCodeCatalog.Yeonsu,
                    OfficeCodeCatalog.Yeonsu,
                    OfficeCodeCatalog.YeonsuMainWarehouse,
                    true,
                    null,
                    0m,
                    now,
                    totalAmount: 100m));
            var activePayment = new Payment
            {
                Id = paymentId,
                InvoiceId = invoiceId,
                PaymentDate = new DateOnly(2026, 7, 31),
                Amount = 100m,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            seedDb.Payments.Add(activePayment);
            await seedDb.SaveChangesAsync();
            invoiceRevision = await seedDb.Invoices
                .IgnoreQueryFilters()
                .Where(invoice => invoice.Id == invoiceId)
                .Select(invoice => invoice.Revision)
                .SingleAsync();
            paymentRevision = activePayment.Revision;
        }

        var currentUser = new TestCurrentUserContext
        {
            Username =
                "usenet-invoice-shared-payment-denied",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions =
            [
                PermissionNames.InvoiceEdit,
                PermissionNames.ItemEdit,
                PermissionNames.PaymentEdit,
                PermissionNames.RentalProfileEdit
            ]
        };
        var deleteMutation = new InvoiceDto
        {
            Id = invoiceId,
            CustomerId = customerId,
            CustomerName = "Payments data area customer",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            SourceWarehouseCode =
                OfficeCodeCatalog.YeonsuMainWarehouse,
            InvoiceNumber = $"VERSION-1-{invoiceId:N}",
            VersionGroupId = invoiceId,
            VersionNumber = 1,
            IsLatestVersion = true,
            IsDeleted = true,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 7, 31),
            SupplyAmount = 100m,
            TotalAmount = 100m,
            Revision = invoiceRevision,
            ExpectedRevision = invoiceRevision,
            CreatedAtUtc = now,
            UpdatedAtUtc = now.AddMinutes(1),
            MutationId =
                $"payment-area-delete:Invoice:{invoiceId:N}",
            MutationCreatedAtUtc = now.AddMinutes(1)
        };

        await using var dbContext = CreateDbContext(currentUser);
        var response = await CreateController(dbContext, currentUser)
            .Push(
                new SyncPushRequest
                {
                    DeviceId =
                        "payment-area-delete-device",
                    Invoices = [deleteMutation]
                },
                CancellationToken.None);

        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(
            result.Conflicts,
            conflict =>
                conflict.EntityName == nameof(Invoice) &&
                conflict.Reason.Contains(
                    "version normalization",
                    StringComparison.OrdinalIgnoreCase));

        dbContext.ChangeTracker.Clear();
        var storedInvoice = await dbContext.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(invoice => invoice.Id == invoiceId);
        Assert.False(storedInvoice.IsDeleted);
        Assert.True(storedInvoice.IsLatestVersion);
        Assert.Equal(invoiceRevision, storedInvoice.Revision);
        var storedPayment = await dbContext.Payments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(payment => payment.Id == paymentId);
        Assert.False(storedPayment.IsDeleted);
        Assert.Equal(paymentRevision, storedPayment.Revision);
        Assert.DoesNotContain(
            await dbContext.ProcessedSyncMutations
                .AsNoTracking()
                .ToListAsync(),
            receipt =>
                receipt.MutationId == deleteMutation.MutationId);
    }

    [Fact]
    public async Task Push_CanonicalSingleLatestPaidInvoice_MemoUpdateDoesNotRequirePaymentEdit()
    {
        var customerId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var now = new DateTime(2026, 7, 31, 8, 40, 0, DateTimeKind.Utc);
        long invoiceRevision;
        long paymentRevision;

        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            seedDb.Customers.Add(new Customer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Canonical paid invoice customer",
                NameMatchKey = "CANONICALPAIDINVOICECUSTOMER",
                TradeType = CustomerClassificationNormalizer.Sales
            });
            var invoice = CreateVersionedInvoice(
                invoiceId,
                invoiceId,
                1,
                null,
                customerId,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                OfficeCodeCatalog.Usenet,
                OfficeCodeCatalog.UsenetMainWarehouse,
                true,
                null,
                0m,
                now);
            invoice.Memo = "before memo update";
            var payment = new Payment
            {
                Id = paymentId,
                InvoiceId = invoiceId,
                PaymentDate = new DateOnly(2026, 7, 31),
                Amount = 10m,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            seedDb.Invoices.Add(invoice);
            seedDb.Payments.Add(payment);
            await seedDb.SaveChangesAsync();
            invoiceRevision = invoice.Revision;
            paymentRevision = payment.Revision;
        }

        var currentUser = CreateInvoiceOnlyOfficeUser(
            "usenet-canonical-paid-memo",
            OfficeCodeCatalog.Usenet);
        await using var dbContext = CreateDbContext(currentUser);
        var storedInvoice = await dbContext.Invoices
            .IgnoreQueryFilters()
            .Include(invoice => invoice.Customer)
            .Include(invoice => invoice.Lines)
            .Include(invoice => invoice.Payments)
            .AsNoTracking()
            .SingleAsync(invoice => invoice.Id == invoiceId);
        var mutation = storedInvoice.ToDto();
        mutation.Memo = "after memo update";
        mutation.ExpectedRevision = storedInvoice.Revision;
        mutation.UpdatedAtUtc = now.AddMinutes(1);
        mutation.MutationId =
            $"canonical-paid-memo:Invoice:{invoiceId:N}";
        mutation.MutationCreatedAtUtc = now.AddMinutes(1);

        var response = await CreateController(dbContext, currentUser)
            .Push(
                new SyncPushRequest
                {
                    DeviceId = "canonical-paid-memo-device",
                    Invoices = [mutation]
                },
                CancellationToken.None);

        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(1, result.AcceptedCount);
        Assert.Equal(0, result.ConflictCount);

        dbContext.ChangeTracker.Clear();
        var updatedInvoice = await dbContext.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(invoice => invoice.Id == invoiceId);
        Assert.Equal("after memo update", updatedInvoice.Memo);
        Assert.True(updatedInvoice.IsLatestVersion);
        Assert.True(updatedInvoice.Revision > invoiceRevision);
        var storedPayment = await dbContext.Payments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(payment => payment.Id == paymentId);
        Assert.False(storedPayment.IsDeleted);
        Assert.Equal(paymentRevision, storedPayment.Revision);
    }

    [Fact]
    public async Task Push_CanonicalMultiVersionPaidInvoice_MemoUpdateDoesNotRequirePaymentEdit()
    {
        var customerId = Guid.NewGuid();
        var firstInvoiceId = Guid.NewGuid();
        var latestInvoiceId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var now = new DateTime(2026, 7, 31, 8, 50, 0, DateTimeKind.Utc);
        long firstRevision;
        long latestRevision;
        long paymentRevision;

        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            seedDb.Customers.Add(new Customer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Canonical paid version customer",
                NameMatchKey = "CANONICALPAIDVERSIONCUSTOMER",
                TradeType = CustomerClassificationNormalizer.Sales
            });
            var firstVersion = CreateVersionedInvoice(
                firstInvoiceId,
                firstInvoiceId,
                1,
                null,
                customerId,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                OfficeCodeCatalog.Usenet,
                OfficeCodeCatalog.UsenetMainWarehouse,
                false,
                null,
                0m,
                now);
            var latestVersion = CreateVersionedInvoice(
                latestInvoiceId,
                firstInvoiceId,
                2,
                firstInvoiceId,
                customerId,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                OfficeCodeCatalog.Usenet,
                OfficeCodeCatalog.UsenetMainWarehouse,
                true,
                null,
                0m,
                now.AddMinutes(1));
            latestVersion.Memo = "before latest memo update";
            var payment = new Payment
            {
                Id = paymentId,
                InvoiceId = firstInvoiceId,
                PaymentDate = new DateOnly(2026, 7, 31),
                Amount = 10m,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            seedDb.Invoices.AddRange(firstVersion, latestVersion);
            seedDb.Payments.Add(payment);
            await seedDb.SaveChangesAsync();
            firstRevision = firstVersion.Revision;
            latestRevision = latestVersion.Revision;
            paymentRevision = payment.Revision;
        }

        var currentUser = CreateInvoiceOnlyOfficeUser(
            "usenet-canonical-version-memo",
            OfficeCodeCatalog.Usenet);
        await using var dbContext = CreateDbContext(currentUser);
        var storedLatest = await dbContext.Invoices
            .IgnoreQueryFilters()
            .Include(invoice => invoice.Customer)
            .Include(invoice => invoice.Lines)
            .Include(invoice => invoice.Payments)
            .AsNoTracking()
            .SingleAsync(invoice => invoice.Id == latestInvoiceId);
        var mutation = storedLatest.ToDto();
        mutation.Memo = "after latest memo update";
        mutation.ExpectedRevision = storedLatest.Revision;
        mutation.UpdatedAtUtc = now.AddMinutes(2);
        mutation.MutationId =
            $"canonical-version-memo:Invoice:{latestInvoiceId:N}";
        mutation.MutationCreatedAtUtc = now.AddMinutes(2);

        var response = await CreateController(dbContext, currentUser)
            .Push(
                new SyncPushRequest
                {
                    DeviceId = "canonical-version-memo-device",
                    Invoices = [mutation]
                },
                CancellationToken.None);

        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(1, result.AcceptedCount);
        Assert.Equal(0, result.ConflictCount);

        dbContext.ChangeTracker.Clear();
        var versions = await dbContext.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(invoice =>
                invoice.Id == firstInvoiceId ||
                invoice.Id == latestInvoiceId)
            .ToDictionaryAsync(invoice => invoice.Id);
        Assert.False(versions[firstInvoiceId].IsLatestVersion);
        Assert.Equal(firstRevision, versions[firstInvoiceId].Revision);
        Assert.True(versions[latestInvoiceId].IsLatestVersion);
        Assert.Equal(
            "after latest memo update",
            versions[latestInvoiceId].Memo);
        Assert.True(
            versions[latestInvoiceId].Revision >
            latestRevision);
        var storedPayment = await dbContext.Payments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(payment => payment.Id == paymentId);
        Assert.False(storedPayment.IsDeleted);
        Assert.Equal(paymentRevision, storedPayment.Revision);
    }

    [Fact]
    public async Task Push_DuplicateLatestSameVersion_UsesStableIdWinnerAcrossSecondNoOpTouch()
    {
        var itemId = Guid.Parse("fb111111-1111-1111-1111-111111111111");
        var customerId = Guid.Parse("fb222222-2222-2222-2222-222222222222");
        var profileId = Guid.Parse("fb333333-3333-3333-3333-333333333333");
        var firstInvoiceId =
            Guid.Parse("fb444444-4444-4444-4444-444444444441");
        var secondInvoiceId =
            Guid.Parse("fb444444-4444-4444-4444-444444444442");
        var versionGroupId =
            Guid.Parse("fb555555-5555-5555-5555-555555555555");
        var winnerId = new[] { firstInvoiceId, secondInvoiceId }
            .OrderByDescending(id => id)
            .First();
        var loserId = winnerId == firstInvoiceId
            ? secondInvoiceId
            : firstInvoiceId;
        var now = new DateTime(2026, 7, 31, 9, 0, 0, DateTimeKind.Utc);

        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            seedDb.Items.Add(
                CreateStockItem(
                    itemId,
                    "Stable duplicate latest item",
                    currentStock: 93m));
            seedDb.ItemWarehouseStocks.Add(new ItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode =
                    OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 93m,
                UpdatedAtUtc = now.AddMinutes(-5)
            });
            seedDb.Customers.Add(new Customer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Stable duplicate latest customer",
                NameMatchKey = "STABLEDUPLICATELATESTCUSTOMER",
                TradeType = CustomerClassificationNormalizer.Sales
            });
            seedDb.RentalBillingProfiles.Add(new RentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ProfileKey = "STABLE-DUPLICATE-LATEST-PROFILE",
                CustomerName = "Stable duplicate latest customer",
                ItemName = "Stable rental item",
                BillingDay = 25,
                MonthlyAmount = 100m,
                SettledAmount = 40m,
                OutstandingAmount = 60m
            });
            var winner = CreateVersionedInvoice(
                winnerId,
                versionGroupId,
                2,
                null,
                customerId,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                OfficeCodeCatalog.Usenet,
                OfficeCodeCatalog.UsenetMainWarehouse,
                true,
                itemId,
                5m,
                now,
                totalAmount: 5m);
            var loser = CreateVersionedInvoice(
                loserId,
                versionGroupId,
                2,
                null,
                customerId,
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                OfficeCodeCatalog.Usenet,
                OfficeCodeCatalog.UsenetMainWarehouse,
                true,
                itemId,
                2m,
                now,
                linkedRentalBillingProfileId: profileId,
                totalAmount: 100m);
            seedDb.Invoices.AddRange(winner, loser);
            seedDb.Payments.Add(new Payment
            {
                Id = Guid.NewGuid(),
                InvoiceId = loserId,
                PaymentDate = new DateOnly(2026, 7, 31),
                Amount = 40m,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            await seedDb.SaveChangesAsync();
        }

        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);
        var firstTouchSource = await dbContext.Invoices
            .IgnoreQueryFilters()
            .Include(invoice => invoice.Customer)
            .Include(invoice => invoice.Lines)
            .Include(invoice => invoice.Payments)
            .AsNoTracking()
            .SingleAsync(invoice => invoice.Id == winnerId);
        var firstTouch = firstTouchSource.ToDto();
        firstTouch.ExpectedRevision = firstTouchSource.Revision;
        firstTouch.MutationId =
            $"stable-duplicate-latest:Invoice:{winnerId:N}:first";
        firstTouch.MutationCreatedAtUtc = now.AddMinutes(1);

        var firstResponse = await CreateController(dbContext, currentUser)
            .Push(
                new SyncPushRequest
                {
                    DeviceId = "stable-duplicate-latest-device",
                    Invoices = [firstTouch]
                },
                CancellationToken.None);
        var firstResult = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(firstResponse.Result).Value);
        Assert.Equal(1, firstResult.AcceptedCount);
        Assert.Equal(0, firstResult.ConflictCount);

        dbContext.ChangeTracker.Clear();
        var afterFirstVersions = await dbContext.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(invoice =>
                invoice.Id == winnerId ||
                invoice.Id == loserId)
            .ToDictionaryAsync(invoice => invoice.Id);
        Assert.True(afterFirstVersions[winnerId].IsLatestVersion);
        Assert.False(afterFirstVersions[loserId].IsLatestVersion);
        var afterFirstInvoiceRevisions = afterFirstVersions
            .ToDictionary(pair => pair.Key, pair => pair.Value.Revision);
        var afterFirstItem = await dbContext.Items
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(item => item.Id == itemId);
        var afterFirstStock = await dbContext.ItemWarehouseStocks
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(stock =>
                stock.ItemId == itemId &&
                stock.WarehouseCode ==
                    OfficeCodeCatalog.UsenetMainWarehouse);
        var afterFirstProfile = await dbContext.RentalBillingProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(profile => profile.Id == profileId);
        Assert.Equal(95m, afterFirstItem.CurrentStock);
        Assert.Equal(95m, afterFirstStock.Quantity);
        Assert.Equal(0m, afterFirstProfile.SettledAmount);
        Assert.Equal(100m, afterFirstProfile.OutstandingAmount);
        var afterFirstLedger = Assert.Single(
            await dbContext.InventoryLedgerEntries
                .AsNoTracking()
                .Where(entry => entry.ItemId == itemId)
                .ToListAsync());
        Assert.Equal(winnerId, afterFirstLedger.SourceDocumentId);
        Assert.Equal(-5m, afterFirstLedger.QuantityDelta);

        var secondTouchSource = await dbContext.Invoices
            .IgnoreQueryFilters()
            .Include(invoice => invoice.Customer)
            .Include(invoice => invoice.Lines)
            .Include(invoice => invoice.Payments)
            .AsNoTracking()
            .SingleAsync(invoice => invoice.Id == winnerId);
        var secondTouch = secondTouchSource.ToDto();
        secondTouch.ExpectedRevision = secondTouchSource.Revision;
        secondTouch.MutationId =
            $"stable-duplicate-latest:Invoice:{winnerId:N}:second";
        secondTouch.MutationCreatedAtUtc = now.AddMinutes(2);

        var secondResponse = await CreateController(dbContext, currentUser)
            .Push(
                new SyncPushRequest
                {
                    DeviceId = "stable-duplicate-latest-device",
                    Invoices = [secondTouch]
                },
                CancellationToken.None);
        var secondResult = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(secondResponse.Result).Value);
        Assert.Equal(1, secondResult.AcceptedCount);
        Assert.Equal(0, secondResult.ConflictCount);

        dbContext.ChangeTracker.Clear();
        var afterSecondVersions = await dbContext.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(invoice =>
                invoice.Id == winnerId ||
                invoice.Id == loserId)
            .ToDictionaryAsync(invoice => invoice.Id);
        Assert.True(afterSecondVersions[winnerId].IsLatestVersion);
        Assert.False(afterSecondVersions[loserId].IsLatestVersion);
        Assert.Equal(
            afterFirstInvoiceRevisions[winnerId],
            afterSecondVersions[winnerId].Revision);
        Assert.Equal(
            afterFirstInvoiceRevisions[loserId],
            afterSecondVersions[loserId].Revision);
        var afterSecondItem = await dbContext.Items
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(item => item.Id == itemId);
        Assert.Equal(95m, afterSecondItem.CurrentStock);
        Assert.Equal(afterFirstItem.Revision, afterSecondItem.Revision);
        var afterSecondStock = await dbContext.ItemWarehouseStocks
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(stock =>
                stock.ItemId == itemId &&
                stock.WarehouseCode ==
                    OfficeCodeCatalog.UsenetMainWarehouse);
        Assert.Equal(95m, afterSecondStock.Quantity);
        Assert.Equal(afterFirstStock.Revision, afterSecondStock.Revision);
        var afterSecondProfile = await dbContext.RentalBillingProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(profile => profile.Id == profileId);
        Assert.Equal(0m, afterSecondProfile.SettledAmount);
        Assert.Equal(100m, afterSecondProfile.OutstandingAmount);
        Assert.Equal(
            afterFirstProfile.Revision,
            afterSecondProfile.Revision);
        var afterSecondLedger = Assert.Single(
            await dbContext.InventoryLedgerEntries
                .AsNoTracking()
                .Where(entry => entry.ItemId == itemId)
                .ToListAsync());
        Assert.Equal(winnerId, afterSecondLedger.SourceDocumentId);
        Assert.Equal(-5m, afterSecondLedger.QuantityDelta);
    }

    [Fact]
    public async Task Push_DeleteCanonicalItworldLatest_PromotesLegacyBlankPreviousUsingCustomerScope()
    {
        var customerId = Guid.NewGuid();
        var previousInvoiceId = Guid.NewGuid();
        var latestInvoiceId = Guid.NewGuid();
        var versionGroupId = Guid.NewGuid();
        var now = new DateTime(2026, 7, 31, 9, 10, 0, DateTimeKind.Utc);
        long latestRevision;

        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            seedDb.Customers.Add(new Customer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                NameOriginal = "Itworld legacy scope customer",
                NameMatchKey = "ITWORLDLEGACYSCOPECUSTOMER",
                TradeType = CustomerClassificationNormalizer.Sales
            });
            var previousVersion = CreateVersionedInvoice(
                previousInvoiceId,
                versionGroupId,
                1,
                null,
                customerId,
                string.Empty,
                string.Empty,
                string.Empty,
                OfficeCodeCatalog.ItworldMainWarehouse,
                false,
                null,
                0m,
                now);
            var latestVersion = CreateVersionedInvoice(
                latestInvoiceId,
                versionGroupId,
                2,
                previousInvoiceId,
                customerId,
                TenantScopeCatalog.Itworld,
                OfficeCodeCatalog.Itworld,
                OfficeCodeCatalog.Itworld,
                OfficeCodeCatalog.ItworldMainWarehouse,
                true,
                null,
                0m,
                now.AddMinutes(1));
            seedDb.Invoices.AddRange(
                previousVersion,
                latestVersion);
            await seedDb.SaveChangesAsync();
            latestRevision = latestVersion.Revision;
        }

        var currentUser = new TestCurrentUserContext
        {
            Username = "itworld-sync-legacy-promotion",
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = [PermissionNames.InvoiceEdit]
        };
        await using var dbContext = CreateDbContext(currentUser);
        var storedLatest = await dbContext.Invoices
            .IgnoreQueryFilters()
            .Include(invoice => invoice.Customer)
            .Include(invoice => invoice.Lines)
            .Include(invoice => invoice.Payments)
            .AsNoTracking()
            .SingleAsync(invoice =>
                invoice.Id == latestInvoiceId);
        var deleteMutation = storedLatest.ToDto();
        deleteMutation.IsDeleted = true;
        deleteMutation.ExpectedRevision = latestRevision;
        deleteMutation.UpdatedAtUtc = now.AddMinutes(2);
        deleteMutation.MutationId =
            $"itworld-legacy-promotion:Invoice:{latestInvoiceId:N}";
        deleteMutation.MutationCreatedAtUtc =
            now.AddMinutes(2);

        var response = await CreateController(
                dbContext,
                currentUser)
            .Push(
                new SyncPushRequest
                {
                    DeviceId =
                        "itworld-legacy-promotion-device",
                    Invoices = [deleteMutation]
                },
                CancellationToken.None);

        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(1, result.AcceptedCount);
        Assert.Equal(0, result.ConflictCount);

        dbContext.ChangeTracker.Clear();
        var versions = await dbContext.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(invoice =>
                invoice.Id == previousInvoiceId ||
                invoice.Id == latestInvoiceId)
            .ToDictionaryAsync(invoice => invoice.Id);
        Assert.False(
            versions[previousInvoiceId].IsDeleted);
        Assert.True(
            versions[previousInvoiceId].IsLatestVersion);
        Assert.Equal(
            string.Empty,
            versions[previousInvoiceId].TenantCode);
        Assert.Equal(
            string.Empty,
            versions[previousInvoiceId].OfficeCode);
        Assert.Equal(
            string.Empty,
            versions[previousInvoiceId]
                .ResponsibleOfficeCode);
        Assert.True(versions[latestInvoiceId].IsDeleted);
        Assert.False(
            versions[latestInvoiceId].IsLatestVersion);
    }

    [Fact]
    public async Task Push_UsenetDeleteLegacyBlankItworldInvoice_IsRejectedWithoutMutation()
    {
        var customerId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var now = new DateTime(2026, 7, 31, 9, 20, 0, DateTimeKind.Utc);
        long invoiceRevision;

        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            seedDb.Customers.Add(new Customer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                NameOriginal = "Protected legacy Itworld customer",
                NameMatchKey = "PROTECTEDLEGACYITWORLDCUSTOMER",
                TradeType = CustomerClassificationNormalizer.Sales
            });
            var invoice = CreateVersionedInvoice(
                invoiceId,
                invoiceId,
                1,
                null,
                customerId,
                string.Empty,
                string.Empty,
                string.Empty,
                OfficeCodeCatalog.ItworldMainWarehouse,
                true,
                null,
                0m,
                now);
            seedDb.Invoices.Add(invoice);
            await seedDb.SaveChangesAsync();
            invoiceRevision = invoice.Revision;
        }

        var currentUser = CreateInvoiceOnlyOfficeUser(
            "usenet-denied-legacy-itworld-delete",
            OfficeCodeCatalog.Usenet);
        await using var dbContext = CreateDbContext(currentUser);
        var storedInvoice = await dbContext.Invoices
            .IgnoreQueryFilters()
            .Include(invoice => invoice.Customer)
            .Include(invoice => invoice.Lines)
            .Include(invoice => invoice.Payments)
            .AsNoTracking()
            .SingleAsync(invoice => invoice.Id == invoiceId);
        var deleteMutation = storedInvoice.ToDto();
        deleteMutation.IsDeleted = true;
        deleteMutation.ExpectedRevision = invoiceRevision;
        deleteMutation.UpdatedAtUtc = now.AddMinutes(1);
        deleteMutation.MutationId =
            $"usenet-denied-itworld-delete:Invoice:{invoiceId:N}";
        deleteMutation.MutationCreatedAtUtc =
            now.AddMinutes(1);

        var response = await CreateController(
                dbContext,
                currentUser)
            .Push(
                new SyncPushRequest
                {
                    DeviceId =
                        "usenet-denied-itworld-delete-device",
                    Invoices = [deleteMutation]
                },
                CancellationToken.None);

        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(1, result.ConflictCount);

        dbContext.ChangeTracker.Clear();
        var unchanged = await dbContext.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(invoice => invoice.Id == invoiceId);
        Assert.False(unchanged.IsDeleted);
        Assert.True(unchanged.IsLatestVersion);
        Assert.Equal(invoiceRevision, unchanged.Revision);
        Assert.Equal(string.Empty, unchanged.TenantCode);
        Assert.Equal(string.Empty, unchanged.OfficeCode);
        Assert.Equal(
            string.Empty,
            unchanged.ResponsibleOfficeCode);
        Assert.DoesNotContain(
            await dbContext.ProcessedSyncMutations
                .AsNoTracking()
                .ToListAsync(),
            receipt =>
                receipt.MutationId ==
                deleteMutation.MutationId);
    }

    [Fact]
    public async Task Push_NewInvoiceVersion_IgnoresSoftDeletedForeignLinkedTransaction()
    {
        var customerId = Guid.NewGuid();
        var firstInvoiceId = Guid.NewGuid();
        var secondInvoiceId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var now = new DateTime(2026, 7, 31, 9, 30, 0, DateTimeKind.Utc);
        long firstRevision;
        long transactionRevision;
        long profileRevision;

        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            seedDb.Customers.Add(new Customer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Deleted foreign transaction customer",
                NameMatchKey = "DELETEDFOREIGNTRANSACTIONCUSTOMER",
                TradeType = CustomerClassificationNormalizer.Sales
            });
            seedDb.Invoices.Add(
                CreateVersionedInvoice(
                    firstInvoiceId,
                    firstInvoiceId,
                    1,
                    null,
                    customerId,
                    TenantScopeCatalog.UsenetGroup,
                    OfficeCodeCatalog.Usenet,
                    OfficeCodeCatalog.Usenet,
                    OfficeCodeCatalog.UsenetMainWarehouse,
                    true,
                    null,
                    0m,
                    now));
            var foreignProfile = new RentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                ProfileKey = "DELETED-FOREIGN-TRANSACTION-PROFILE",
                CustomerName =
                    "Deleted foreign transaction customer",
                ItemName = "Foreign rental item",
                BillingDay = 25,
                MonthlyAmount = 100m,
                SettledAmount = 0m,
                OutstandingAmount = 100m
            };
            var deletedTransaction = new TransactionRecord
            {
                Id = transactionId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                ResponsibleOfficeCode =
                    OfficeCodeCatalog.Itworld,
                TransactionDate =
                    new DateOnly(2026, 7, 31),
                TransactionKind = "Receipt",
                LinkedInvoiceId = firstInvoiceId,
                LinkedInvoiceNumber =
                    $"VERSION-1-{firstInvoiceId:N}",
                LinkedRentalBillingProfileId = profileId,
                SettlementAmount = 10m,
                ReceiptTotal = 10m,
                IsDeleted = true,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            seedDb.RentalBillingProfiles.Add(foreignProfile);
            seedDb.Transactions.Add(deletedTransaction);
            await seedDb.SaveChangesAsync();
            firstRevision = await seedDb.Invoices
                .IgnoreQueryFilters()
                .Where(invoice =>
                    invoice.Id == firstInvoiceId)
                .Select(invoice => invoice.Revision)
                .SingleAsync();
            transactionRevision =
                deletedTransaction.Revision;
            profileRevision = foreignProfile.Revision;
        }

        var currentUser = CreateInvoiceOnlyOfficeUser(
            "usenet-ignore-deleted-foreign-transaction",
            OfficeCodeCatalog.Usenet);
        var newVersion = new InvoiceDto
        {
            Id = secondInvoiceId,
            CustomerId = customerId,
            CustomerName =
                "Deleted foreign transaction customer",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode =
                OfficeCodeCatalog.Usenet,
            InvoiceNumber =
                "DELETED-FOREIGN-TRANSACTION-VERSION-0002",
            VersionGroupId = firstInvoiceId,
            VersionNumber = 2,
            PreviousVersionId = firstInvoiceId,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            SourceWarehouseCode =
                OfficeCodeCatalog.UsenetMainWarehouse,
            InvoiceDate = new DateOnly(2026, 7, 31),
            Revision = firstRevision,
            ExpectedRevision = firstRevision,
            CreatedAtUtc = now.AddMinutes(1),
            UpdatedAtUtc = now.AddMinutes(1),
            MutationId =
                $"ignore-deleted-foreign-transaction:Invoice:{secondInvoiceId:N}",
            MutationCreatedAtUtc = now.AddMinutes(1)
        };

        await using var dbContext = CreateDbContext(currentUser);
        var response = await CreateController(
                dbContext,
                currentUser)
            .Push(
                new SyncPushRequest
                {
                    DeviceId =
                        "ignore-deleted-foreign-transaction-device",
                    Invoices = [newVersion]
                },
                CancellationToken.None);

        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(1, result.AcceptedCount);
        Assert.Equal(0, result.ConflictCount);

        dbContext.ChangeTracker.Clear();
        var versions = await dbContext.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(invoice =>
                invoice.Id == firstInvoiceId ||
                invoice.Id == secondInvoiceId)
            .ToDictionaryAsync(invoice => invoice.Id);
        Assert.False(
            versions[firstInvoiceId].IsLatestVersion);
        Assert.True(
            versions[secondInvoiceId].IsLatestVersion);
        var unchangedTransaction = await dbContext.Transactions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(transaction =>
                transaction.Id == transactionId);
        Assert.True(unchangedTransaction.IsDeleted);
        Assert.Equal(
            firstInvoiceId,
            unchangedTransaction.LinkedInvoiceId);
        Assert.Equal(
            profileId,
            unchangedTransaction
                .LinkedRentalBillingProfileId);
        Assert.Equal(
            transactionRevision,
            unchangedTransaction.Revision);
        var unchangedProfile =
            await dbContext.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(profile =>
                    profile.Id == profileId);
        Assert.Equal(
            profileRevision,
            unchangedProfile.Revision);
        Assert.Equal(
            0m,
            unchangedProfile.SettledAmount);
        Assert.Equal(
            100m,
            unchangedProfile.OutstandingAmount);
    }

    [Fact]
    public async Task Push_NewInvoiceVersion_ExplicitTenantOfficeMismatchFailsClosed()
    {
        var customerId = Guid.NewGuid();
        var firstInvoiceId = Guid.NewGuid();
        var secondInvoiceId = Guid.NewGuid();
        var now = new DateTime(2026, 7, 31, 9, 40, 0, DateTimeKind.Utc);
        long firstRevision;

        await using (var seedDb = CreateDbContext(CreateAdminUser()))
        {
            seedDb.Customers.Add(new Customer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                NameOriginal = "Mismatched version scope customer",
                NameMatchKey = "MISMATCHEDVERSIONSCOPECUSTOMER",
                TradeType = CustomerClassificationNormalizer.Sales
            });
            var firstVersion = CreateVersionedInvoice(
                firstInvoiceId,
                firstInvoiceId,
                1,
                null,
                customerId,
                TenantScopeCatalog.Itworld,
                OfficeCodeCatalog.Itworld,
                OfficeCodeCatalog.Itworld,
                OfficeCodeCatalog.ItworldMainWarehouse,
                true,
                null,
                0m,
                now);
            seedDb.Invoices.Add(firstVersion);
            await seedDb.SaveChangesAsync();
            firstRevision = firstVersion.Revision;
        }

        var mismatchedVersion = new InvoiceDto
        {
            Id = secondInvoiceId,
            CustomerId = customerId,
            CustomerName =
                "Mismatched version scope customer",
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode =
                OfficeCodeCatalog.Usenet,
            SourceWarehouseCode =
                OfficeCodeCatalog.UsenetMainWarehouse,
            InvoiceNumber = "MISMATCHED-VERSION-SCOPE-0002",
            VersionGroupId = firstInvoiceId,
            VersionNumber = 2,
            PreviousVersionId = firstInvoiceId,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 7, 31),
            Revision = firstRevision,
            ExpectedRevision = firstRevision,
            CreatedAtUtc = now.AddMinutes(1),
            UpdatedAtUtc = now.AddMinutes(1),
            MutationId =
                $"mismatched-version-scope:Invoice:{secondInvoiceId:N}",
            MutationCreatedAtUtc = now.AddMinutes(1)
        };
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var response = await CreateController(
                dbContext,
                currentUser)
            .Push(
                new SyncPushRequest
                {
                    DeviceId =
                        "mismatched-version-scope-device",
                    Invoices = [mismatchedVersion]
                },
                CancellationToken.None);

        var result = Assert.IsType<SyncPushResult>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(
            result.Conflicts,
            conflict =>
                conflict.EntityName == nameof(Invoice) &&
                conflict.Reason.Contains(
                    "inconsistent",
                    StringComparison.OrdinalIgnoreCase));
        Assert.False(await dbContext.Invoices
            .IgnoreQueryFilters()
            .AnyAsync(invoice =>
                invoice.Id == secondInvoiceId));
        var unchangedFirst = await dbContext.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(invoice =>
                invoice.Id == firstInvoiceId);
        Assert.True(unchangedFirst.IsLatestVersion);
        Assert.Equal(
            firstRevision,
            unchangedFirst.Revision);
    }

    private static Invoice CreateVersionedInvoice(
        Guid invoiceId,
        Guid versionGroupId,
        int versionNumber,
        Guid? previousVersionId,
        Guid customerId,
        string tenantCode,
        string officeCode,
        string responsibleOfficeCode,
        string sourceWarehouseCode,
        bool isLatestVersion,
        Guid? itemId,
        decimal quantity,
        DateTime now,
        Guid? linkedRentalBillingProfileId = null,
        Guid? linkedRentalBillingRunId = null,
        decimal totalAmount = 0m)
    {
        var invoice = new Invoice
        {
            Id = invoiceId,
            CustomerId = customerId,
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ResponsibleOfficeCode = responsibleOfficeCode,
            SourceWarehouseCode = sourceWarehouseCode,
            InvoiceNumber = $"VERSION-{versionNumber}-{invoiceId:N}",
            VersionGroupId = versionGroupId,
            VersionNumber = versionNumber,
            PreviousVersionId = previousVersionId,
            IsLatestVersion = isLatestVersion,
            VoucherType = VoucherType.Sales,
            InvoiceDate = DateOnly.FromDateTime(now),
            SupplyAmount = totalAmount,
            TotalAmount = totalAmount,
            LinkedRentalBillingProfileId =
                linkedRentalBillingProfileId,
            LinkedRentalBillingRunId =
                linkedRentalBillingRunId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        if (itemId.HasValue && itemId.Value != Guid.Empty)
        {
            invoice.Lines.Add(new InvoiceLine
            {
                Id = Guid.NewGuid(),
                InvoiceId = invoiceId,
                ItemId = itemId,
                ItemNameOriginal = $"Versioned item {itemId:N}",
                Unit = "EA",
                Quantity = quantity,
                UnitPrice = 1m,
                LineAmount = quantity,
                OrderIndex = 1,
                ItemTrackingType = ItemTrackingTypes.Stock
            });
        }

        return invoice;
    }

    private static TestCurrentUserContext CreateInvoiceOfficeUser(
        string username,
        string officeCode)
        => new()
        {
            Username = username,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = officeCode,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions =
            [
                PermissionNames.InvoiceEdit,
                PermissionNames.ItemEdit,
                PermissionNames.RentalProfileEdit
            ]
        };

    private static TestCurrentUserContext CreateInvoiceOnlyOfficeUser(
        string username,
        string officeCode)
        => new()
        {
            Username = username,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = officeCode,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = [PermissionNames.InvoiceEdit]
        };

    private async Task SeedPendingTransferAsync(
        Guid itemId,
        Guid transferId,
        Guid lineId,
        string itemName,
        decimal sourceStockQuantity)
    {
        var now = new DateTime(2026, 6, 24, 2, 0, 0, DateTimeKind.Utc);
        await using var seedDb = CreateDbContext(CreateAdminUser());
        seedDb.Items.Add(CreateStockItem(itemId, itemName, currentStock: sourceStockQuantity));
        seedDb.ItemWarehouseStocks.Add(new ItemWarehouseStock
        {
            ItemId = itemId,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = sourceStockQuantity,
            UpdatedAtUtc = now,
            Revision = 20
        });
        seedDb.InventoryTransfers.Add(new InventoryTransfer
        {
            Id = transferId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Usenet,
            TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
            TransferNumber = $"TR-PENDING-{transferId:N}"[..24],
            TransferDate = new DateOnly(2026, 6, 24),
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            TransferStatus = InventoryTransferStatusNormalizer.Pending,
            CreatedByUsername = "usenet-source",
            RequestedByUsername = "usenet-source",
            RequestedAtUtc = now.AddMinutes(-20),
            CreatedAtUtc = now.AddMinutes(-20),
            UpdatedAtUtc = now,
            LastSavedAtUtc = now,
            Revision = 30,
            Lines =
            [
                new InventoryTransferLine
                {
                    Id = lineId,
                    TransferId = transferId,
                    ItemId = itemId,
                    ItemNameOriginal = itemName,
                    Unit = "EA",
                    Quantity = 2m,
                    ReceivedQuantity = 2m
                }
            ]
        });
        await seedDb.SaveChangesAsync();
    }

    private async Task SeedReceivedTransferAsync(
        Guid itemId,
        Guid transferId,
        Guid lineId,
        string itemName)
    {
        var now = new DateTime(2026, 6, 24, 2, 30, 0, DateTimeKind.Utc);
        await using var seedDb = CreateDbContext(CreateAdminUser());
        seedDb.Items.Add(CreateStockItem(itemId, itemName, currentStock: 10m));
        seedDb.ItemWarehouseStocks.AddRange(
            new ItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 8m,
                UpdatedAtUtc = now,
                Revision = 21
            },
            new ItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                Quantity = 2m,
                UpdatedAtUtc = now,
                Revision = 22
            });
        seedDb.InventoryTransfers.Add(new InventoryTransfer
        {
            Id = transferId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Usenet,
            TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
            TransferNumber = $"TR-RECEIVED-{transferId:N}"[..24],
            TransferDate = new DateOnly(2026, 6, 24),
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            TransferStatus = InventoryTransferStatusNormalizer.Received,
            CreatedByUsername = "usenet-source",
            RequestedByUsername = "usenet-source",
            RequestedAtUtc = now.AddMinutes(-30),
            ReceivedByUsername = "yeonsu-target",
            ReceivedAtUtc = now.AddMinutes(-10),
            CreatedAtUtc = now.AddMinutes(-30),
            UpdatedAtUtc = now,
            LastSavedAtUtc = now,
            Revision = 40,
            Lines =
            [
                new InventoryTransferLine
                {
                    Id = lineId,
                    TransferId = transferId,
                    ItemId = itemId,
                    ItemNameOriginal = itemName,
                    Unit = "EA",
                    Quantity = 2m,
                    ReceivedQuantity = 2m
                }
            ]
        });
        await seedDb.SaveChangesAsync();
    }

    private async Task SeedDeletedPendingTransferAsync(
        Guid itemId,
        Guid transferId,
        Guid lineId,
        string itemName)
    {
        var now = new DateTime(2026, 6, 24, 2, 40, 0, DateTimeKind.Utc);
        await using var seedDb = CreateDbContext(CreateAdminUser());
        seedDb.Items.Add(CreateStockItem(itemId, itemName, currentStock: 10m));
        seedDb.ItemWarehouseStocks.Add(new ItemWarehouseStock
        {
            ItemId = itemId,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 10m,
            UpdatedAtUtc = now,
            Revision = 23
        });
        seedDb.InventoryTransfers.Add(new InventoryTransfer
        {
            Id = transferId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Usenet,
            TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
            TransferNumber = $"TR-DELETED-{transferId:N}"[..24],
            TransferDate = new DateOnly(2026, 6, 24),
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            TransferStatus = InventoryTransferStatusNormalizer.Pending,
            CreatedByUsername = "usenet-source",
            RequestedByUsername = "usenet-source",
            RequestedAtUtc = now.AddMinutes(-30),
            CreatedAtUtc = now.AddMinutes(-30),
            UpdatedAtUtc = now,
            LastSavedAtUtc = now,
            Revision = 50,
            IsDeleted = true,
            Lines =
            [
                new InventoryTransferLine
                {
                    Id = lineId,
                    TransferId = transferId,
                    ItemId = itemId,
                    ItemNameOriginal = itemName,
                    Unit = "EA",
                    Quantity = 2m,
                    ReceivedQuantity = 2m
                }
            ]
        });
        await seedDb.SaveChangesAsync();
    }

    private static InventoryTransferDto BuildReceiptDto(
        InventoryTransfer existing,
        string username,
        decimal requestedQuantity)
    {
        var line = Assert.Single(existing.Lines, current => !current.IsDeleted);
        return new InventoryTransferDto
        {
            Id = existing.Id,
            CreatedAtUtc = existing.CreatedAtUtc,
            UpdatedAtUtc = existing.UpdatedAtUtc.AddMinutes(1),
            Revision = existing.Revision,
            ExpectedRevision = existing.Revision,
            TenantCode = existing.TenantCode,
            SourceOfficeCode = existing.SourceOfficeCode,
            TargetOfficeCode = existing.TargetOfficeCode,
            TransferNumber = existing.TransferNumber,
            TransferDate = existing.TransferDate,
            FromWarehouseCode = existing.FromWarehouseCode,
            ToWarehouseCode = existing.ToWarehouseCode,
            Memo = existing.Memo,
            CreatedByUsername = existing.CreatedByUsername,
            LastSavedByUsername = username,
            LastSavedAtUtc = existing.LastSavedAtUtc.AddMinutes(1),
            TransferStatus = InventoryTransferStatusNormalizer.Received,
            RequestedByUsername = existing.RequestedByUsername,
            RequestedAtUtc = existing.RequestedAtUtc,
            ReceivedByUsername = username,
            ReceivedAtUtc = existing.LastSavedAtUtc.AddMinutes(1),
            ReceiveMemo = "confirmed by target",
            ReceiveEvidencePath = existing.ReceiveEvidencePath,
            LastStatusChangedByUsername = username,
            LastStatusChangedAtUtc = existing.LastSavedAtUtc.AddMinutes(1),
            MutationId = $"target-transfer-receive:InventoryTransfer:{existing.Id:N}:{requestedQuantity}",
            MutationCreatedAtUtc = DateTime.UtcNow,
            Lines =
            [
                new InventoryTransferLineDto
                {
                    Id = line.Id,
                    TransferId = existing.Id,
                    ItemId = line.ItemId,
                    ItemNameOriginal = line.ItemNameOriginal,
                    SpecificationOriginal = line.SpecificationOriginal,
                    Unit = line.Unit,
                    Quantity = requestedQuantity,
                    ReceivedQuantity = requestedQuantity,
                    QuantityDifference = 0m,
                    Remark = line.Remark,
                    ReceiptRemark = "ok"
                }
            ]
        };
    }

    private static InventoryTransferDto BuildFinalizedRetryDto(InventoryTransfer existing)
        => new()
        {
            Id = existing.Id,
            CreatedAtUtc = existing.CreatedAtUtc,
            UpdatedAtUtc = existing.UpdatedAtUtc,
            Revision = existing.Revision,
            ExpectedRevision = existing.Revision,
            TenantCode = existing.TenantCode,
            SourceOfficeCode = existing.SourceOfficeCode,
            TargetOfficeCode = existing.TargetOfficeCode,
            TransferNumber = existing.TransferNumber,
            TransferDate = existing.TransferDate,
            FromWarehouseCode = existing.FromWarehouseCode,
            ToWarehouseCode = existing.ToWarehouseCode,
            Memo = existing.Memo,
            CreatedByUsername = existing.CreatedByUsername,
            LastSavedByUsername = existing.LastSavedByUsername,
            LastSavedAtUtc = existing.LastSavedAtUtc,
            TransferStatus = existing.TransferStatus,
            RequestedByUsername = existing.RequestedByUsername,
            RequestedAtUtc = existing.RequestedAtUtc,
            ReceivedByUsername = existing.ReceivedByUsername,
            ReceivedAtUtc = existing.ReceivedAtUtc,
            ReceiveMemo = existing.ReceiveMemo,
            ReceiveEvidencePath = existing.ReceiveEvidencePath,
            RejectedByUsername = existing.RejectedByUsername,
            RejectedAtUtc = existing.RejectedAtUtc,
            RejectReason = existing.RejectReason,
            LastStatusChangedByUsername = existing.LastStatusChangedByUsername,
            LastStatusChangedAtUtc = existing.LastStatusChangedAtUtc,
            MutationId = $"finalized-retry:InventoryTransfer:{existing.Id:N}:{Guid.NewGuid():N}",
            MutationCreatedAtUtc = DateTime.UtcNow,
            Lines = existing.Lines
                .Where(line => !line.IsDeleted)
                .Select(line => new InventoryTransferLineDto
                {
                    Id = line.Id,
                    TransferId = existing.Id,
                    ItemId = line.ItemId,
                    ItemNameOriginal = line.ItemNameOriginal,
                    SpecificationOriginal = line.SpecificationOriginal,
                    Unit = line.Unit,
                    Quantity = line.Quantity,
                    ReceivedQuantity = line.ReceivedQuantity,
                    QuantityDifference = line.QuantityDifference,
                    Remark = line.Remark,
                    ReceiptRemark = line.ReceiptRemark
                })
                .ToList()
        };

    private static InvoiceDto BuildInventoryInvoiceDto(
        Guid invoiceId,
        Guid customerId,
        Guid itemId,
        string itemName,
        VoucherType voucherType,
        decimal quantity,
        string username,
        DateTime now)
    {
        var unitPrice = 100m;
        var supplyAmount = quantity * unitPrice;
        return new InvoiceDto
        {
            Id = invoiceId,
            CustomerId = customerId,
            CustomerName =
                voucherType == VoucherType.Purchase
                    ? "Purchase then transfer customer"
                    : "Sale then transfer customer",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            VersionGroupId = invoiceId,
            VersionNumber = 1,
            IsLatestVersion = true,
            VoucherType = voucherType,
            SourceWarehouseCode =
                OfficeCodeCatalog.UsenetMainWarehouse,
            InvoiceDate = DateOnly.FromDateTime(now),
            SupplyAmount = supplyAmount,
            VatAmount = 0m,
            TotalAmount = supplyAmount,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            MutationId =
                $"{username}:Invoice:{invoiceId:N}",
            MutationCreatedAtUtc = now,
            Lines =
            [
                new InvoiceLineDto
                {
                    Id = Guid.NewGuid(),
                    InvoiceId = invoiceId,
                    ItemId = itemId,
                    ItemNameOriginal = itemName,
                    Unit = "EA",
                    Quantity = quantity,
                    UnitPrice = unitPrice,
                    LineAmount = supplyAmount,
                    OrderIndex = 1,
                    ItemTrackingType = ItemTrackingTypes.Stock
                }
            ]
        };
    }

    private static InventoryTransferDto BuildPendingTransferDto(
        Guid transferId,
        Guid itemId,
        string itemName,
        decimal quantity,
        string username,
        DateTime now,
        string mutationPrefix)
        => new()
        {
            Id = transferId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Usenet,
            TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
            TransferNumber = $"TR-{transferId:N}"[..24],
            TransferDate = DateOnly.FromDateTime(now),
            FromWarehouseCode =
                OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode =
                OfficeCodeCatalog.YeonsuMainWarehouse,
            TransferStatus =
                InventoryTransferStatusNormalizer.Pending,
            CreatedByUsername = username,
            RequestedByUsername = username,
            RequestedAtUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            LastSavedByUsername = username,
            LastSavedAtUtc = now,
            MutationId =
                $"{mutationPrefix}:InventoryTransfer:{transferId:N}",
            MutationCreatedAtUtc = now,
            Lines =
            [
                new InventoryTransferLineDto
                {
                    Id = Guid.NewGuid(),
                    TransferId = transferId,
                    ItemId = itemId,
                    ItemNameOriginal = itemName,
                    Unit = "EA",
                    Quantity = quantity
                }
            ]
        };

    private static Item CreateStockItem(Guid itemId, string name, decimal currentStock) => new()
    {
        Id = itemId,
        TenantCode = TenantScopeCatalog.UsenetGroup,
        OfficeCode = OfficeCodeCatalog.Shared,
        NameOriginal = name,
        NameMatchKey = name.Replace(" ", string.Empty).ToUpperInvariant(),
        Unit = "EA",
        ItemKind = ItemKinds.Product,
        TrackingType = ItemTrackingTypes.Stock,
        CurrentStock = currentStock
    };

    private static void AssertInventoryTransferPurgeReceiptEqual(
        RecycleBinPurgeRecordDto expected,
        RecycleBinPurgeRecordDto actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.EntityId, actual.EntityId);
        Assert.Equal(expected.Revision, actual.Revision);
        Assert.Equal(expected.TenantCode, actual.TenantCode);
        Assert.Equal(expected.OfficeCode, actual.OfficeCode);
        Assert.Equal(expected.SourceOfficeCode, actual.SourceOfficeCode);
        Assert.Equal(expected.TargetOfficeCode, actual.TargetOfficeCode);
        Assert.Equal(expected.PurgedAtUtc, actual.PurgedAtUtc);
        Assert.Equal(expected.CreatedAtUtc, actual.CreatedAtUtc);
        Assert.Equal(expected.UpdatedAtUtc, actual.UpdatedAtUtc);
        Assert.Equal(expected.IsDeleted, actual.IsDeleted);
        Assert.Equal(expected.ExpectedRevision, actual.ExpectedRevision);
        Assert.Equal(expected.MutationId, actual.MutationId);
        Assert.Equal(expected.MutationCreatedAtUtc, actual.MutationCreatedAtUtc);
    }

    private AppDbContext CreateDbContext(TestCurrentUserContext currentUser)
    {
        var revisionClock = new RevisionClock();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        return new AppDbContext(options, currentUser, revisionClock);
    }

    private static SyncController CreateController(AppDbContext dbContext, TestCurrentUserContext currentUser)
    {
        var revisionClock = new RevisionClock();
        return new SyncController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new StubCentralFileStorage(),
            revisionClock,
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, revisionClock),
            new RentalAssignmentHistoryService(dbContext),
            new RentalSettlementRecalculationService(dbContext));
    }

    private static RecycleBinController CreateRecycleBinController(AppDbContext dbContext, TestCurrentUserContext currentUser)
    {
        var revisionClock = new RevisionClock();
        return new RecycleBinController(
            dbContext,
            new OfficeScopeService(currentUser, dbContext),
            NoOpStoredFileReferenceReconciler.Instance,
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, revisionClock),
            new RentalSettlementRecalculationService(dbContext),
            NoOpStoredFileDeferredDeletionQueue.Instance);
    }

    private static TestCurrentUserContext CreateAdminUser() => new()
    {
        Username = "admin",
        TenantCode = TenantScopeCatalog.UsenetGroup,
        OfficeCode = OfficeCodeCatalog.Usenet,
        ScopeType = TenantScopeCatalog.ScopeAdmin,
        IsAdmin = true
    };

    private static TestCurrentUserContext CreateDeliveryUser(string username, string officeCode) => new()
    {
        Username = username,
        TenantCode = TenantScopeCatalog.UsenetGroup,
        OfficeCode = officeCode,
        ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
        Permissions = [PermissionNames.DeliveryEdit]
    };

    private static TestCurrentUserContext CreateInventoryDeliveryAdminUser(
        string username,
        string officeCode)
        => new()
        {
            Username = username,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = officeCode,
            ScopeType = TenantScopeCatalog.ScopeAdmin,
            IsAdmin = true,
            Permissions =
            [
                PermissionNames.DeliveryEdit,
                PermissionNames.ItemEdit
            ]
        };

    public void Dispose() => _connection.Dispose();

    private sealed class TestCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId { get; init; } = Guid.NewGuid();
        public string Username { get; init; } = string.Empty;
        public string TenantCode { get; init; } = TenantScopeCatalog.UsenetGroup;
        public string OfficeCode { get; init; } = OfficeCodeCatalog.Usenet;
        public string ScopeType { get; init; } = TenantScopeCatalog.ScopeOfficeOnly;
        public bool IsAdmin { get; init; }
        public bool IsGodMode { get; init; }
        public IReadOnlyCollection<string> Permissions { get; init; } = [];

        public bool HasPermission(string permission)
            => IsAdmin || IsGodMode || Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);
    }

    private sealed class StubInvoiceNumberService : IInvoiceNumberService
    {
        public Task<string> GenerateAsync(Guid customerId, DateOnly invoiceDate, CancellationToken cancellationToken = default)
            => Task.FromResult($"{invoiceDate:yyyyMM}-0001");
    }

    private sealed class StubCentralFileStorage : ICentralFileStorage
    {
        public string RootPath => Path.GetTempPath();

        public Task<string> SaveBytesAsync(
            string area,
            string ownerId,
            Guid fileId,
            string fileName,
            byte[] content,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Path.Combine(RootPath, fileName));

        public byte[] ReadBytes(string? storedPath, byte[]? fallback = null) => fallback ?? [];

        public void DeleteIfExists(string? storedPath)
        {
        }
    }
}
