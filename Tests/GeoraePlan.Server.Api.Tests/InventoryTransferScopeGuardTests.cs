using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
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
        var line = Assert.Single(existing.Lines);
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
            new StubCentralFileStorage(),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, revisionClock),
            new RentalSettlementRecalculationService(dbContext));
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
