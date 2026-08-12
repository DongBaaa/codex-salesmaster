using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class InventoryTransferScopeGuardTests
{
    [Fact]
    public void InventoryWindowPermissionState_UsesExplicitResetAndDeliveryPermissions()
    {
        using var appRoot = new LocalAppRootScope("georaeplan-inventory-window-permission-state");
        using var db = CreateDbContext(appRoot.DbPath);
        var restrictedSession = CreateUserSession(
            TenantScopeCatalog.UsenetGroup,
            OfficeCodeCatalog.Yeonsu,
            TenantScopeCatalog.ScopeOfficeOnly,
            AppPermissionNames.InventoryReset,
            AppPermissionNames.DeliveryEdit);
        var service = new LocalStateService(
            db,
            new OfficeAccessService(),
            new SyncRequestDispatcher(),
            restrictedSession);
        using var viewModel = new InventoryViewModel(service, restrictedSession);

        Assert.False(viewModel.IsAdmin);
        Assert.True(viewModel.CanResetInventory);
        Assert.True(viewModel.CanManageInventoryTransfers);
        Assert.Contains("상단 재고이동 버튼", viewModel.TransferGuideMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResetItemInventoryValue_OfficeOnlyUserResetsOnlyWritableWarehouse()
    {
        using var appRoot = new LocalAppRootScope("georaeplan-inventory-reset-office-scope");
        await using var db = CreateDbContext(appRoot.DbPath);
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var itemId = Guid.Parse("91111111-1111-1111-1111-111111111111");
        var unrelatedItemId = Guid.Parse("94444444-4444-4444-4444-444444444444");
        var item = CreateStockItem(itemId, "Office scoped reset item");
        item.OfficeCode = OfficeCodeCatalog.Yeonsu;
        item.CurrentStock = 10m;
        var unrelatedItem = CreateStockItem(unrelatedItemId, "Foreign office untouched item");
        unrelatedItem.OfficeCode = OfficeCodeCatalog.Usenet;
        unrelatedItem.CurrentStock = 8m;
        db.Items.AddRange(item, unrelatedItem);

        var expectedResetDate = DateOnly.FromDateTime(DateTime.Today).AddDays(5);
        var outOfScopeFutureDate = expectedResetDate.AddDays(25);
        db.ItemWarehouseStocks.AddRange(
            new LocalItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                Quantity = 4m,
                UpdatedAtUtc = DateTime.UtcNow,
                Revision = 1
            },
            new LocalItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 6m,
                UpdatedAtUtc = DateTime.UtcNow,
                Revision = 2
            },
            new LocalItemWarehouseStock
            {
                ItemId = unrelatedItemId,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 8m,
                UpdatedAtUtc = DateTime.UtcNow,
                Revision = 5
            });
        db.InventoryMovements.AddRange(
            new LocalInventoryMovement
            {
                Id = Guid.Parse("92222222-2222-2222-2222-222222222222"),
                ItemId = itemId,
                WarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                MovementType = "StockAdjustmentManual",
                QuantityDelta = 4m,
                UnitCost = 100m,
                Amount = 400m,
                OccurredDate = expectedResetDate,
                IsSettledCost = true,
                IsActive = true,
                CreatedByUsername = "seed",
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-2)
            },
            new LocalInventoryMovement
            {
                Id = Guid.Parse("93333333-3333-3333-3333-333333333333"),
                ItemId = itemId,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                MovementType = "StockAdjustmentManual",
                QuantityDelta = 6m,
                UnitCost = 100m,
                Amount = 600m,
                OccurredDate = outOfScopeFutureDate,
                IsSettledCost = true,
                IsActive = true,
                CreatedByUsername = "seed",
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-1)
            });
        var foreignInvoiceId = Guid.Parse("95555555-5555-5555-5555-555555555555");
        db.Invoices.Add(new LocalInvoice
        {
            Id = foreignInvoiceId,
            CustomerId = Guid.Parse("96666666-6666-6666-6666-666666666666"),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = string.Empty,
            SourceWarehouseCode = string.Empty,
            InvoiceDate = outOfScopeFutureDate.AddDays(1),
            VersionGroupId = foreignInvoiceId,
            IsLatestVersion = true,
            IsConfirmed = true,
            IsDirty = false,
            Lines =
            [
                new LocalInvoiceLine
                {
                    Id = Guid.Parse("97777777-7777-7777-7777-777777777777"),
                    InvoiceId = foreignInvoiceId,
                    ItemId = itemId,
                    ItemNameOriginal = item.NameOriginal,
                    Unit = "EA",
                    Quantity = 1m
                }
            ]
        });
        var foreignTransferId = Guid.Parse("98888888-8888-8888-8888-888888888888");
        db.InventoryTransfers.Add(new LocalInventoryTransfer
        {
            Id = foreignTransferId,
            TransferNumber = "TR-FOREIGN-RESET-DATE",
            TransferDate = outOfScopeFutureDate.AddDays(2),
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.ItworldMainWarehouse,
            IsDirty = false,
            Lines =
            [
                new LocalInventoryTransferLine
                {
                    Id = Guid.Parse("99999999-9999-9999-9999-999999999999"),
                    TransferId = foreignTransferId,
                    ItemId = itemId,
                    ItemNameOriginal = item.NameOriginal,
                    Unit = "EA",
                    Quantity = 1m
                }
            ]
        });
        await db.SaveChangesAsync();

        var yeonsuSession = CreateUserSession(
            TenantScopeCatalog.UsenetGroup,
            OfficeCodeCatalog.Yeonsu,
            TenantScopeCatalog.ScopeOfficeOnly,
            AppPermissionNames.InventoryReset);
        var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), yeonsuSession);

        var result = await service.ResetItemInventoryValueAsync(itemId, yeonsuSession);

        Assert.True(result.Success, result.Message);
        db.ChangeTracker.Clear();
        Assert.Equal(0m, await db.ItemWarehouseStocks
            .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.YeonsuMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
        Assert.Equal(6m, await db.ItemWarehouseStocks
            .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
        Assert.Equal(2L, await db.ItemWarehouseStocks
            .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Revision)
            .SingleAsync());
        Assert.Equal(6m, (await db.Items.IgnoreQueryFilters().SingleAsync(current => current.Id == itemId)).CurrentStock);
        Assert.Equal(8m, await db.ItemWarehouseStocks
            .Where(stock => stock.ItemId == unrelatedItemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
        Assert.Equal(5L, await db.ItemWarehouseStocks
            .Where(stock => stock.ItemId == unrelatedItemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Revision)
            .SingleAsync());
        var untouchedItem = await db.Items.IgnoreQueryFilters().SingleAsync(current => current.Id == unrelatedItemId);
        Assert.Equal(8m, untouchedItem.CurrentStock);
        Assert.False(untouchedItem.IsDirty);

        var resetMarkers = await db.InventoryMovements
            .Where(movement => movement.ItemId == itemId && movement.MovementType == "StockResetToZero")
            .ToListAsync();
        var resetMarker = Assert.Single(resetMarkers);
        Assert.Equal(OfficeCodeCatalog.YeonsuMainWarehouse, resetMarker.WarehouseCode);
        Assert.Equal(expectedResetDate, resetMarker.OccurredDate);
        Assert.NotEqual(outOfScopeFutureDate, resetMarker.OccurredDate);
    }

    [Fact]
    public async Task SaveInventoryTransfer_DeniesTargetOfficeUserFromCreatingSourceStockMove()
    {
        using var appRoot = new LocalAppRootScope("georaeplan-transfer-save-source-scope");
        await using var db = CreateDbContext(appRoot.DbPath);
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var itemId = Guid.Parse("a1111111-1111-1111-1111-111111111111");
        db.Items.Add(CreateStockItem(itemId, "Target denied transfer item"));
        db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock
        {
            ItemId = itemId,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 10m,
            UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            Revision = 10
        });
        await db.SaveChangesAsync();

        var targetSession = CreateUserSession(
            TenantScopeCatalog.UsenetGroup,
            OfficeCodeCatalog.Yeonsu,
            TenantScopeCatalog.ScopeOfficeOnly,
            AppPermissionNames.DeliveryEdit);
        var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), targetSession);
        var transferId = Guid.Parse("a2222222-2222-2222-2222-222222222222");

        var result = await service.SaveInventoryTransferAsync(new LocalInventoryTransfer
        {
            Id = transferId,
            TransferDate = new DateOnly(2026, 6, 24),
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            Lines =
            [
                new LocalInventoryTransferLine
                {
                    Id = Guid.Parse("a3333333-3333-3333-3333-333333333333"),
                    TransferId = transferId,
                    ItemId = itemId,
                    ItemNameOriginal = "Target denied transfer item",
                    Unit = "EA",
                    Quantity = 2m
                }
            ]
        }, targetSession);

        Assert.False(result.Success);
        Assert.True(result.PermissionDenied);
        Assert.Contains("출발지", result.Message, StringComparison.Ordinal);
        db.ChangeTracker.Clear();
        Assert.False(await db.InventoryTransfers.IgnoreQueryFilters().AnyAsync(transfer => transfer.Id == transferId));
        Assert.Equal(10m, await db.ItemWarehouseStocks
            .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
        Assert.False(await db.ItemWarehouseStocks
            .AnyAsync(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.YeonsuMainWarehouse));
    }

    [Fact]
    public async Task SaveInventoryTransfer_DeniesTargetOfficeUserFromReversingExistingPendingRoute()
    {
        using var appRoot = new LocalAppRootScope("georaeplan-transfer-save-existing-source-scope");
        await using var db = CreateDbContext(appRoot.DbPath);
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var itemId = Guid.Parse("a4111111-1111-1111-1111-111111111111");
        var transferId = Guid.Parse("a4222222-2222-2222-2222-222222222222");
        var lineId = Guid.Parse("a4333333-3333-3333-3333-333333333333");
        var now = new DateTime(2026, 6, 24, 1, 0, 0, DateTimeKind.Utc);
        db.Items.Add(CreateStockItem(itemId, "Existing route source guard item"));
        db.ItemWarehouseStocks.AddRange(
            new LocalItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 8m,
                UpdatedAtUtc = now,
                Revision = 20
            },
            new LocalItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                Quantity = 10m,
                UpdatedAtUtc = now,
                Revision = 21
            });
        db.InventoryTransfers.Add(new LocalInventoryTransfer
        {
            Id = transferId,
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            TransferNumber = "TR-PENDING-ROUTE-SOURCE-GUARD",
            TransferDate = new DateOnly(2026, 6, 24),
            TransferStatus = InventoryTransferStatusNormalizer.Pending,
            Memo = "original source route",
            CreatedAtUtc = now.AddHours(-1),
            UpdatedAtUtc = now,
            Revision = 30,
            IsDirty = false,
            Lines =
            [
                new LocalInventoryTransferLine
                {
                    Id = lineId,
                    TransferId = transferId,
                    ItemId = itemId,
                    ItemNameOriginal = "Existing route source guard item",
                    Unit = "EA",
                    Quantity = 2m
                }
            ]
        });
        await db.SaveChangesAsync();

        var targetSession = CreateUserSession(
            TenantScopeCatalog.UsenetGroup,
            OfficeCodeCatalog.Yeonsu,
            TenantScopeCatalog.ScopeOfficeOnly,
            AppPermissionNames.DeliveryEdit);
        var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), targetSession);

        var result = await service.SaveInventoryTransferAsync(new LocalInventoryTransfer
        {
            Id = transferId,
            FromWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            TransferNumber = "TR-PENDING-ROUTE-SOURCE-GUARD",
            TransferDate = new DateOnly(2026, 6, 24),
            TransferStatus = InventoryTransferStatusNormalizer.Pending,
            Memo = "target office route reversal must be denied",
            CreatedAtUtc = now.AddHours(-1),
            UpdatedAtUtc = now,
            Revision = 30,
            IsDirty = false,
            Lines =
            [
                new LocalInventoryTransferLine
                {
                    Id = lineId,
                    TransferId = transferId,
                    ItemId = itemId,
                    ItemNameOriginal = "Existing route source guard item",
                    Unit = "EA",
                    Quantity = 2m
                }
            ]
        }, targetSession);

        Assert.False(result.Success);
        Assert.True(result.PermissionDenied);
        Assert.Contains("기존 출발지", result.Message, StringComparison.Ordinal);
        db.ChangeTracker.Clear();
        var stored = await db.InventoryTransfers
            .IgnoreQueryFilters()
            .Include(transfer => transfer.Lines)
            .SingleAsync(transfer => transfer.Id == transferId);
        Assert.Equal(OfficeCodeCatalog.UsenetMainWarehouse, stored.FromWarehouseCode);
        Assert.Equal(OfficeCodeCatalog.YeonsuMainWarehouse, stored.ToWarehouseCode);
        Assert.Equal("original source route", stored.Memo);
        Assert.Equal(30, stored.Revision);
        Assert.False(stored.IsDirty);
        Assert.Equal(2m, Assert.Single(stored.Lines).Quantity);
        Assert.Equal(8m, await db.ItemWarehouseStocks
            .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
        Assert.Equal(10m, await db.ItemWarehouseStocks
            .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.YeonsuMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
        Assert.False(await db.AuditLogs.AnyAsync(log => log.EntityId == transferId.ToString("D")));
    }

    [Fact]
    public async Task SaveInventoryTransfer_ExistingEditPreservesImmutableAndStatusOwnedFields()
    {
        using var appRoot = new LocalAppRootScope("georaeplan-transfer-save-existing-field-ownership");
        await using var db = CreateDbContext(appRoot.DbPath);
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var itemId = Guid.Parse("a5111111-1111-1111-1111-111111111111");
        var transferId = Guid.Parse("a5222222-2222-2222-2222-222222222222");
        var lineId = Guid.Parse("a5333333-3333-3333-3333-333333333333");
        var now = new DateTime(2026, 6, 24, 1, 20, 0, DateTimeKind.Utc);
        db.Items.Add(CreateStockItem(itemId, "Existing field ownership item"));
        db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock
        {
            ItemId = itemId,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 8m,
            UpdatedAtUtc = now,
            Revision = 20
        });
        db.InventoryTransfers.Add(new LocalInventoryTransfer
        {
            Id = transferId,
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            TransferNumber = "TR-PENDING-FIELD-OWNER",
            TransferDate = new DateOnly(2026, 6, 24),
            TransferStatus = InventoryTransferStatusNormalizer.Pending,
            Memo = "original memo",
            CreatedByUsername = string.Empty,
            RequestedByUsername = string.Empty,
            RequestedAtUtc = null,
            ReceivedByUsername = string.Empty,
            ReceivedAtUtc = null,
            ReceiveMemo = "existing receive memo",
            ReceiveEvidencePath = "inventory-transfers/existing-evidence.pdf",
            RejectedByUsername = string.Empty,
            RejectedAtUtc = null,
            RejectReason = "existing reject reason",
            LastStatusChangedByUsername = string.Empty,
            LastStatusChangedAtUtc = null,
            CreatedAtUtc = now.AddHours(-1),
            UpdatedAtUtc = now,
            Revision = 30,
            IsDirty = false,
            Lines =
            [
                new LocalInventoryTransferLine
                {
                    Id = lineId,
                    TransferId = transferId,
                    ItemId = itemId,
                    ItemNameOriginal = "Existing field ownership item",
                    Unit = "EA",
                    Quantity = 2m,
                    ReceivedQuantity = 1m,
                    QuantityDifference = -1m,
                    Remark = "original request remark",
                    ReceiptRemark = "existing receipt remark"
                }
            ]
        });
        await db.SaveChangesAsync();

        var sourceSession = CreateUserSession(
            TenantScopeCatalog.UsenetGroup,
            OfficeCodeCatalog.Usenet,
            TenantScopeCatalog.ScopeOfficeOnly,
            AppPermissionNames.DeliveryEdit);
        var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), sourceSession);
        var result = await service.SaveInventoryTransferAsync(new LocalInventoryTransfer
        {
            Id = transferId,
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            TransferNumber = "TR-SPOOFED-NUMBER",
            TransferDate = new DateOnly(2026, 6, 25),
            TransferStatus = InventoryTransferStatusNormalizer.Received,
            Memo = "allowed source memo update",
            CreatedByUsername = "spoofed-creator",
            RequestedByUsername = "spoofed-requester",
            RequestedAtUtc = now.AddDays(1),
            ReceivedByUsername = "spoofed-receiver",
            ReceivedAtUtc = now.AddDays(1),
            ReceiveMemo = "spoofed receive memo",
            ReceiveEvidencePath = "inventory-transfers/spoofed-evidence.pdf",
            RejectedByUsername = "spoofed-rejector",
            RejectedAtUtc = now.AddDays(1),
            RejectReason = "spoofed reject reason",
            LastStatusChangedByUsername = "spoofed-status-auditor",
            LastStatusChangedAtUtc = now.AddDays(1),
            CreatedAtUtc = now.AddDays(1),
            UpdatedAtUtc = now,
            Revision = 30,
            IsDirty = false,
            Lines =
            [
                new LocalInventoryTransferLine
                {
                    Id = lineId,
                    TransferId = transferId,
                    ItemId = itemId,
                    ItemNameOriginal = "Existing field ownership item",
                    Unit = "EA",
                    Quantity = 3m,
                    ReceivedQuantity = 3m,
                    QuantityDifference = 0m,
                    Remark = "allowed request remark update",
                    ReceiptRemark = "spoofed receipt remark"
                }
            ]
        }, sourceSession);

        Assert.True(result.Success, result.Message);
        db.ChangeTracker.Clear();
        var stored = await db.InventoryTransfers
            .IgnoreQueryFilters()
            .Include(transfer => transfer.Lines)
            .SingleAsync(transfer => transfer.Id == transferId);
        var storedLine = Assert.Single(stored.Lines);
        Assert.Equal("TR-PENDING-FIELD-OWNER", stored.TransferNumber);
        Assert.Equal(new DateOnly(2026, 6, 25), stored.TransferDate);
        Assert.Equal("allowed source memo update", stored.Memo);
        Assert.Equal(now.AddHours(-1), stored.CreatedAtUtc);
        Assert.Empty(stored.CreatedByUsername);
        Assert.Empty(stored.RequestedByUsername);
        Assert.Null(stored.RequestedAtUtc);
        Assert.Equal(InventoryTransferStatusNormalizer.Pending, stored.TransferStatus);
        Assert.Empty(stored.ReceivedByUsername);
        Assert.Null(stored.ReceivedAtUtc);
        Assert.Equal("existing receive memo", stored.ReceiveMemo);
        Assert.Equal("inventory-transfers/existing-evidence.pdf", stored.ReceiveEvidencePath);
        Assert.Empty(stored.RejectedByUsername);
        Assert.Null(stored.RejectedAtUtc);
        Assert.Equal("existing reject reason", stored.RejectReason);
        Assert.Empty(stored.LastStatusChangedByUsername);
        Assert.Null(stored.LastStatusChangedAtUtc);
        Assert.Equal(sourceSession.User!.Username, stored.LastSavedByUsername);
        Assert.True(stored.LastSavedAtUtc > now);
        Assert.Equal(3m, storedLine.Quantity);
        Assert.Equal("allowed request remark update", storedLine.Remark);
        Assert.Equal(3m, storedLine.ReceivedQuantity);
        Assert.Equal(0m, storedLine.QuantityDifference);
        Assert.Empty(storedLine.ReceiptRemark);
    }

    [Theory]
    [InlineData("1.001")]
    [InlineData("10000000000000000")]
    public async Task SaveInventoryTransfer_RejectsRequestedQuantityOutsideNumericContractWithoutMutation(
        string invalidQuantityText)
    {
        using var appRoot = new LocalAppRootScope("georaeplan-transfer-save-invalid-quantity");
        await using var db = CreateDbContext(appRoot.DbPath);
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var itemId = Guid.Parse("a6111111-1111-1111-1111-111111111111");
        var transferId = Guid.Parse("a6222222-2222-2222-2222-222222222222");
        var lineId = Guid.Parse("a6333333-3333-3333-3333-333333333333");
        var now = new DateTime(2026, 6, 24, 1, 25, 0, DateTimeKind.Utc);
        db.Items.Add(CreateStockItem(itemId, "Invalid requested quantity item"));
        db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock
        {
            ItemId = itemId,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 8m,
            UpdatedAtUtc = now,
            Revision = 20
        });
        db.InventoryTransfers.Add(new LocalInventoryTransfer
        {
            Id = transferId,
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            TransferNumber = "TR-INVALID-REQUESTED-QUANTITY",
            TransferDate = new DateOnly(2026, 6, 24),
            TransferStatus = InventoryTransferStatusNormalizer.Pending,
            CreatedAtUtc = now.AddHours(-1),
            UpdatedAtUtc = now,
            Revision = 30,
            IsDirty = false,
            Lines =
            [
                new LocalInventoryTransferLine
                {
                    Id = lineId,
                    TransferId = transferId,
                    ItemId = itemId,
                    ItemNameOriginal = "Invalid requested quantity item",
                    Unit = "EA",
                    Quantity = 2m,
                    ReceivedQuantity = 2m,
                    QuantityDifference = 0m
                }
            ]
        });
        await db.SaveChangesAsync();

        var sourceSession = CreateUserSession(
            TenantScopeCatalog.UsenetGroup,
            OfficeCodeCatalog.Usenet,
            TenantScopeCatalog.ScopeOfficeOnly,
            AppPermissionNames.DeliveryEdit);
        var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), sourceSession);
        var invalidQuantity = decimal.Parse(invalidQuantityText, System.Globalization.CultureInfo.InvariantCulture);

        var result = await service.SaveInventoryTransferAsync(new LocalInventoryTransfer
        {
            Id = transferId,
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            TransferDate = new DateOnly(2026, 6, 24),
            TransferStatus = InventoryTransferStatusNormalizer.Pending,
            CreatedAtUtc = now.AddHours(-1),
            UpdatedAtUtc = now,
            Revision = 30,
            IsDirty = false,
            Lines =
            [
                new LocalInventoryTransferLine
                {
                    Id = lineId,
                    TransferId = transferId,
                    ItemId = itemId,
                    ItemNameOriginal = "Invalid requested quantity item",
                    Unit = "EA",
                    Quantity = invalidQuantity
                }
            ]
        }, sourceSession);

        Assert.False(result.Success);
        Assert.True(result.PermissionDenied);
        Assert.Contains("numeric(18,2)", result.Message, StringComparison.Ordinal);
        db.ChangeTracker.Clear();
        var stored = await db.InventoryTransfers
            .IgnoreQueryFilters()
            .Include(transfer => transfer.Lines)
            .SingleAsync(transfer => transfer.Id == transferId);
        Assert.Equal(30, stored.Revision);
        Assert.False(stored.IsDirty);
        Assert.Equal(2m, Assert.Single(stored.Lines).Quantity);
        Assert.Equal(8m, await db.ItemWarehouseStocks
            .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
        Assert.False((await db.Items.IgnoreQueryFilters().SingleAsync(item => item.Id == itemId)).IsDirty);
        Assert.False(await db.AuditLogs.AnyAsync(log => log.EntityId == transferId.ToString("D")));
    }

    [Theory]
    [InlineData("2", "2.01")]
    [InlineData("2", "1.001")]
    [InlineData("2", "-0.01")]
    [InlineData("2", "10000000000000000")]
    [InlineData("1.001", "1")]
    public async Task ConfirmInventoryTransferReceipt_RejectsRequestedOrReceivedQuantityOutsideNumericContractWithoutMutation(
        string requestedQuantityText,
        string invalidReceivedQuantityText)
    {
        using var appRoot = new LocalAppRootScope("georaeplan-transfer-confirm-invalid-quantity");
        await using var db = CreateDbContext(appRoot.DbPath);
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var itemId = Guid.Parse("a7111111-1111-1111-1111-111111111111");
        var transferId = Guid.Parse("a7222222-2222-2222-2222-222222222222");
        var lineId = Guid.Parse("a7333333-3333-3333-3333-333333333333");
        var now = new DateTime(2026, 6, 24, 1, 27, 0, DateTimeKind.Utc);
        db.Items.Add(CreateStockItem(itemId, "Invalid received quantity item"));
        db.ItemWarehouseStocks.AddRange(
            new LocalItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 8m,
                UpdatedAtUtc = now,
                Revision = 20
            },
            new LocalItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                Quantity = 0m,
                UpdatedAtUtc = now,
                Revision = 20
            });
        var requestedQuantity = decimal.Parse(requestedQuantityText, System.Globalization.CultureInfo.InvariantCulture);
        db.InventoryTransfers.Add(new LocalInventoryTransfer
        {
            Id = transferId,
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            TransferNumber = "TR-INVALID-RECEIVED-QUANTITY",
            TransferDate = new DateOnly(2026, 6, 24),
            TransferStatus = InventoryTransferStatusNormalizer.Pending,
            ReceiveMemo = "original receive memo",
            CreatedAtUtc = now.AddHours(-1),
            UpdatedAtUtc = now,
            Revision = 40,
            IsDirty = false,
            Lines =
            [
                new LocalInventoryTransferLine
                {
                    Id = lineId,
                    TransferId = transferId,
                    ItemId = itemId,
                    ItemNameOriginal = "Invalid received quantity item",
                    Unit = "EA",
                    Quantity = requestedQuantity,
                    ReceivedQuantity = requestedQuantity,
                    QuantityDifference = 0m,
                    ReceiptRemark = "original receipt remark"
                }
            ]
        });
        await db.SaveChangesAsync();

        var targetSession = CreateUserSession(
            TenantScopeCatalog.UsenetGroup,
            OfficeCodeCatalog.Yeonsu,
            TenantScopeCatalog.ScopeOfficeOnly,
            AppPermissionNames.DeliveryEdit);
        var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), targetSession);
        var invalidReceivedQuantity = decimal.Parse(invalidReceivedQuantityText, System.Globalization.CultureInfo.InvariantCulture);

        var result = await service.ConfirmInventoryTransferReceiptAsync(
            transferId,
            [new LocalInventoryTransferLine { Id = lineId, ReceivedQuantity = invalidReceivedQuantity }],
            "spoofed receive memo",
            targetSession,
            expectedRevision: 40);

        Assert.False(result.Success);
        Assert.True(result.PermissionDenied);
        Assert.Contains("numeric(18,2)", result.Message, StringComparison.Ordinal);
        db.ChangeTracker.Clear();
        var stored = await db.InventoryTransfers
            .IgnoreQueryFilters()
            .Include(transfer => transfer.Lines)
            .SingleAsync(transfer => transfer.Id == transferId);
        var storedLine = Assert.Single(stored.Lines);
        Assert.Equal(InventoryTransferStatusNormalizer.Pending, stored.TransferStatus);
        Assert.Equal("original receive memo", stored.ReceiveMemo);
        Assert.Empty(stored.ReceivedByUsername);
        Assert.Null(stored.ReceivedAtUtc);
        Assert.Equal(40, stored.Revision);
        Assert.False(stored.IsDirty);
        Assert.Equal(requestedQuantity, storedLine.Quantity);
        Assert.Equal(requestedQuantity, storedLine.ReceivedQuantity);
        Assert.Equal(0m, storedLine.QuantityDifference);
        Assert.Equal("original receipt remark", storedLine.ReceiptRemark);
        Assert.Equal(8m, await db.ItemWarehouseStocks
            .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
        Assert.Equal(0m, await db.ItemWarehouseStocks
            .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.YeonsuMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
        Assert.False((await db.Items.IgnoreQueryFilters().SingleAsync(item => item.Id == itemId)).IsDirty);
        Assert.False(await db.AuditLogs.AnyAsync(log => log.EntityId == transferId.ToString("D")));
    }

    [Fact]
    public async Task SaveInventoryTransfer_RejectsDuplicateActiveLineIdsWithoutMutation()
    {
        using var appRoot = new LocalAppRootScope("georaeplan-transfer-save-duplicate-line-id");
        await using var db = CreateDbContext(appRoot.DbPath);
        var fixture = await SeedInventoryTransferLineIdentityFixtureAsync(db);
        var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), fixture.SourceSession);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveInventoryTransferAsync(
                BuildInventoryTransferSaveCandidate(fixture, fixture.PrimaryLineId, fixture.PrimaryLineId),
                fixture.SourceSession));

        Assert.Contains("품목 행 ID가 중복", exception.Message, StringComparison.Ordinal);
        await AssertInventoryTransferLineIdentityFixtureUnchangedAsync(db, fixture);
    }

    [Fact]
    public async Task SaveInventoryTransfer_RejectsLineIdOwnedBySoftDeletedForeignTransferWithoutMutation()
    {
        using var appRoot = new LocalAppRootScope("georaeplan-transfer-save-foreign-line-id");
        await using var db = CreateDbContext(appRoot.DbPath);
        var fixture = await SeedInventoryTransferLineIdentityFixtureAsync(db);
        var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), fixture.SourceSession);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveInventoryTransferAsync(
                BuildInventoryTransferSaveCandidate(fixture, fixture.ForeignLineId),
                fixture.SourceSession));

        Assert.Contains("다른 재고이동 문서에 속해", exception.Message, StringComparison.Ordinal);
        await AssertInventoryTransferLineIdentityFixtureUnchangedAsync(db, fixture);
    }

    [Fact]
    public async Task SaveInventoryTransfer_AfterPurgeConflict_DeniesReusingOldTransferId()
    {
        using var appRoot = new LocalAppRootScope(
            "georaeplan-transfer-purge-conflict-old-id");
        await using var db = CreateDbContext(appRoot.DbPath);
        var fixture = await SeedInventoryTransferLineIdentityFixtureAsync(db);
        var primary = await db.InventoryTransfers
            .IgnoreQueryFilters()
            .Include(transfer => transfer.Lines)
            .SingleAsync(transfer => transfer.Id == fixture.PrimaryTransferId);
        db.InventoryTransferLines.RemoveRange(primary.Lines);
        db.InventoryTransfers.Remove(primary);
        var now = DateTime.UtcNow;
        db.InventoryTransferTombstoneConflicts.Add(
            new LocalInventoryTransferTombstoneConflict
            {
                TransferId = fixture.PrimaryTransferId,
                BusinessDatabaseName = "USENET",
                TenantCode = TenantScopeCatalog.UsenetGroup,
                SourceOfficeCode = OfficeCodeCatalog.Usenet,
                TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
                LocalSnapshotJson = "{}",
                ServerTombstoneJson = "{}",
                OutboxMutationIdsJson = "[]",
                LocalRevision = fixture.PrimaryRevision,
                ServerRevision = fixture.PrimaryRevision + 1,
                ServerUpdatedAtUtc = now,
                Status = InventoryTransferTombstoneConflictPolicy.ResolvedStatus,
                Resolution = InventoryTransferTombstoneConflictPolicy.DiscardedResolution,
                DetectedAtUtc = now,
                UpdatedAtUtc = now,
                ResolvedAtUtc = now
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var service = new LocalStateService(
            db,
            new OfficeAccessService(),
            new SyncRequestDispatcher(),
            fixture.SourceSession);
        var candidate = BuildInventoryTransferSaveCandidate(
            fixture,
            Guid.NewGuid());
        candidate.Revision = 0;

        var result = await service.SaveInventoryTransferAsync(
            candidate,
            fixture.SourceSession);

        Assert.False(result.Success);
        Assert.Contains("ID", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(await db.InventoryTransfers
            .IgnoreQueryFilters()
            .AnyAsync(transfer => transfer.Id == fixture.PrimaryTransferId));
    }

    [Fact]
    public async Task ConfirmInventoryTransferReceipt_RejectsDuplicateLineIdsWithoutMutation()
    {
        using var appRoot = new LocalAppRootScope("georaeplan-transfer-receive-duplicate-line-id");
        await using var db = CreateDbContext(appRoot.DbPath);
        var fixture = await SeedInventoryTransferLineIdentityFixtureAsync(db);
        var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), fixture.TargetSession);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ConfirmInventoryTransferReceiptAsync(
                fixture.PrimaryTransferId,
                [
                    new LocalInventoryTransferLine { Id = fixture.PrimaryLineId, ReceivedQuantity = 1m },
                    new LocalInventoryTransferLine { Id = fixture.PrimaryLineId, ReceivedQuantity = 2m }
                ],
                "must not be saved",
                fixture.TargetSession,
                expectedRevision: fixture.PrimaryRevision));

        Assert.Contains("품목 행 ID가 중복", exception.Message, StringComparison.Ordinal);
        await AssertInventoryTransferLineIdentityFixtureUnchangedAsync(db, fixture);
    }

    [Fact]
    public async Task ConfirmInventoryTransferReceipt_RejectsForeignLineIdWithoutMutation()
    {
        using var appRoot = new LocalAppRootScope("georaeplan-transfer-receive-foreign-line-id");
        await using var db = CreateDbContext(appRoot.DbPath);
        var fixture = await SeedInventoryTransferLineIdentityFixtureAsync(db);
        var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), fixture.TargetSession);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ConfirmInventoryTransferReceiptAsync(
                fixture.PrimaryTransferId,
                [new LocalInventoryTransferLine { Id = fixture.ForeignLineId, ReceivedQuantity = 1m }],
                "must not be saved",
                fixture.TargetSession,
                expectedRevision: fixture.PrimaryRevision));

        Assert.Contains("다른 재고이동 문서에 속해", exception.Message, StringComparison.Ordinal);
        await AssertInventoryTransferLineIdentityFixtureUnchangedAsync(db, fixture);
    }

    [Fact]
    public async Task ConfirmInventoryTransferReceipt_RejectsEmptyAndUnknownLineIdsWithoutMutation()
    {
        using var appRoot = new LocalAppRootScope("georaeplan-transfer-receive-invalid-line-id");
        await using var db = CreateDbContext(appRoot.DbPath);
        var fixture = await SeedInventoryTransferLineIdentityFixtureAsync(db);
        var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), fixture.TargetSession);

        var emptyException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ConfirmInventoryTransferReceiptAsync(
                fixture.PrimaryTransferId,
                [new LocalInventoryTransferLine { Id = Guid.Empty, ReceivedQuantity = 1m }],
                "must not be saved",
                fixture.TargetSession,
                expectedRevision: fixture.PrimaryRevision));
        Assert.Contains("ID가 비어", emptyException.Message, StringComparison.Ordinal);
        await AssertInventoryTransferLineIdentityFixtureUnchangedAsync(db, fixture);

        var unknownLineId = Guid.Parse("d1000000-0000-0000-0000-000000000099");
        var unknownException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ConfirmInventoryTransferReceiptAsync(
                fixture.PrimaryTransferId,
                [new LocalInventoryTransferLine { Id = unknownLineId, ReceivedQuantity = 1m }],
                "must not be saved",
                fixture.TargetSession,
                expectedRevision: fixture.PrimaryRevision));
        Assert.Contains("찾을 수 없는", unknownException.Message, StringComparison.Ordinal);
        await AssertInventoryTransferLineIdentityFixtureUnchangedAsync(db, fixture);
    }

    [Fact]
    public async Task DeleteInventoryTransfer_DeniesTargetOfficeUserFromDeletingPendingSourceMove()
    {
        using var appRoot = new LocalAppRootScope("georaeplan-transfer-delete-source-scope");
        await using var db = CreateDbContext(appRoot.DbPath);
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var itemId = Guid.Parse("b1111111-1111-1111-1111-111111111111");
        var transferId = Guid.Parse("b2222222-2222-2222-2222-222222222222");
        var lineId = Guid.Parse("b3333333-3333-3333-3333-333333333333");
        var now = new DateTime(2026, 6, 24, 1, 30, 0, DateTimeKind.Utc);
        db.Items.Add(CreateStockItem(itemId, "Target denied pending delete item"));
        db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock
        {
            ItemId = itemId,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 8m,
            UpdatedAtUtc = now,
            Revision = 20
        });
        db.InventoryTransfers.Add(new LocalInventoryTransfer
        {
            Id = transferId,
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            TransferNumber = "TR-PENDING-TARGET-DELETE-DENIED",
            TransferDate = new DateOnly(2026, 6, 24),
            TransferStatus = InventoryTransferStatusNormalizer.Pending,
            CreatedAtUtc = now.AddHours(-1),
            UpdatedAtUtc = now,
            Revision = 30,
            IsDirty = false,
            Lines =
            [
                new LocalInventoryTransferLine
                {
                    Id = lineId,
                    TransferId = transferId,
                    ItemId = itemId,
                    ItemNameOriginal = "Target denied pending delete item",
                    Unit = "EA",
                    Quantity = 2m
                }
            ]
        });
        await db.SaveChangesAsync();

        var targetSession = CreateUserSession(
            TenantScopeCatalog.UsenetGroup,
            OfficeCodeCatalog.Yeonsu,
            TenantScopeCatalog.ScopeOfficeOnly,
            AppPermissionNames.DeliveryEdit);
        var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), targetSession);

        var result = await service.DeleteInventoryTransferAsync(transferId, targetSession, expectedRevision: 30);

        Assert.False(result.Success);
        Assert.True(result.PermissionDenied);
        Assert.Contains("출발지", result.Message, StringComparison.Ordinal);
        db.ChangeTracker.Clear();
        var stored = await db.InventoryTransfers.IgnoreQueryFilters().SingleAsync(transfer => transfer.Id == transferId);
        Assert.False(stored.IsDeleted);
        Assert.False(stored.IsDirty);
        Assert.Equal(8m, await db.ItemWarehouseStocks
            .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
    }

    [Fact]
    public async Task DeleteInventoryTransfer_DeniesSingleTargetOfficeUserFromDeletingReceivedMove()
    {
        using var appRoot = new LocalAppRootScope("georaeplan-transfer-delete-final-scope");
        await using var db = CreateDbContext(appRoot.DbPath);
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var itemId = Guid.Parse("b4111111-1111-1111-1111-111111111111");
        var transferId = Guid.Parse("b4222222-2222-2222-2222-222222222222");
        var lineId = Guid.Parse("b4333333-3333-3333-3333-333333333333");
        var now = new DateTime(2026, 6, 24, 2, 10, 0, DateTimeKind.Utc);
        db.Items.Add(CreateStockItem(itemId, "Target denied received delete item"));
        db.ItemWarehouseStocks.AddRange(
            new LocalItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 8m,
                UpdatedAtUtc = now,
                Revision = 20
            },
            new LocalItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                Quantity = 2m,
                UpdatedAtUtc = now,
                Revision = 21
            });
        db.InventoryTransfers.Add(new LocalInventoryTransfer
        {
            Id = transferId,
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            TransferNumber = "TR-RECEIVED-TARGET-DELETE-DENIED",
            TransferDate = new DateOnly(2026, 6, 24),
            TransferStatus = InventoryTransferStatusNormalizer.Received,
            ReceivedByUsername = "yeonsu-target",
            ReceivedAtUtc = now.AddMinutes(-5),
            CreatedAtUtc = now.AddHours(-1),
            UpdatedAtUtc = now,
            Revision = 40,
            IsDirty = false,
            Lines =
            [
                new LocalInventoryTransferLine
                {
                    Id = lineId,
                    TransferId = transferId,
                    ItemId = itemId,
                    ItemNameOriginal = "Target denied received delete item",
                    Unit = "EA",
                    Quantity = 2m,
                    ReceivedQuantity = 2m
                }
            ]
        });
        await db.SaveChangesAsync();

        var targetSession = CreateUserSession(
            TenantScopeCatalog.UsenetGroup,
            OfficeCodeCatalog.Yeonsu,
            TenantScopeCatalog.ScopeOfficeOnly,
            AppPermissionNames.DeliveryEdit);
        var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), targetSession);

        var result = await service.DeleteInventoryTransferAsync(transferId, targetSession, expectedRevision: 40);

        Assert.False(result.Success);
        Assert.True(result.PermissionDenied);
        db.ChangeTracker.Clear();
        var stored = await db.InventoryTransfers.IgnoreQueryFilters().SingleAsync(transfer => transfer.Id == transferId);
        Assert.False(stored.IsDeleted);
        Assert.False(stored.IsDirty);
        Assert.Equal(8m, await db.ItemWarehouseStocks
            .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
        Assert.Equal(2m, await db.ItemWarehouseStocks
            .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.YeonsuMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
    }

    [Fact]
    public async Task RestoreRecycleBinInventoryTransfer_DeniesTargetOfficeUserFromRestoringSourceMove()
    {
        using var appRoot = new LocalAppRootScope("georaeplan-transfer-restore-recycle-source-scope");
        await using var db = CreateDbContext(appRoot.DbPath);
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var itemId = Guid.Parse("b5111111-1111-1111-1111-111111111111");
        var transferId = Guid.Parse("b5222222-2222-2222-2222-222222222222");
        var lineId = Guid.Parse("b5333333-3333-3333-3333-333333333333");
        var now = new DateTime(2026, 6, 24, 2, 20, 0, DateTimeKind.Utc);
        db.Items.Add(CreateStockItem(itemId, "Target denied restore transfer item"));
        db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock
        {
            ItemId = itemId,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 10m,
            UpdatedAtUtc = now,
            Revision = 20
        });
        db.InventoryTransfers.Add(new LocalInventoryTransfer
        {
            Id = transferId,
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            TransferNumber = "TR-RESTORE-TARGET-DENIED",
            TransferDate = new DateOnly(2026, 6, 24),
            TransferStatus = InventoryTransferStatusNormalizer.Pending,
            CreatedAtUtc = now.AddHours(-1),
            UpdatedAtUtc = now,
            Revision = 50,
            IsDeleted = true,
            IsDirty = false,
            Lines =
            [
                new LocalInventoryTransferLine
                {
                    Id = lineId,
                    TransferId = transferId,
                    ItemId = itemId,
                    ItemNameOriginal = "Target denied restore transfer item",
                    Unit = "EA",
                    Quantity = 2m
                }
            ]
        });
        await db.SaveChangesAsync();

        var targetSession = CreateUserSession(
            TenantScopeCatalog.UsenetGroup,
            OfficeCodeCatalog.Yeonsu,
            TenantScopeCatalog.ScopeOfficeOnly,
            AppPermissionNames.DeliveryEdit);
        var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), targetSession);

        var result = await service.RestoreRecycleBinEntryAsync(
            RecycleBinEntityKind.InventoryTransfer,
            transferId,
            targetSession);

        Assert.False(result.Success);
        Assert.True(result.PermissionDenied);
        db.ChangeTracker.Clear();
        Assert.True((await db.InventoryTransfers.IgnoreQueryFilters().SingleAsync(transfer => transfer.Id == transferId)).IsDeleted);
        Assert.Equal(10m, await db.ItemWarehouseStocks
            .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
    }

    [Fact]
    public async Task PermanentlyDeleteRecycleBinInventoryTransfer_DeniesTargetOfficeUserFromPurgingSourceMove()
    {
        using var appRoot = new LocalAppRootScope("georaeplan-transfer-purge-recycle-source-scope");
        await using var db = CreateDbContext(appRoot.DbPath);
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var itemId = Guid.Parse("b6111111-1111-1111-1111-111111111111");
        var transferId = Guid.Parse("b6222222-2222-2222-2222-222222222222");
        var lineId = Guid.Parse("b6333333-3333-3333-3333-333333333333");
        db.Items.Add(CreateStockItem(itemId, "Target denied purge transfer item"));
        db.InventoryTransfers.Add(new LocalInventoryTransfer
        {
            Id = transferId,
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            TransferNumber = "TR-PURGE-TARGET-DENIED",
            TransferDate = new DateOnly(2026, 6, 24),
            TransferStatus = InventoryTransferStatusNormalizer.Pending,
            CreatedAtUtc = DateTime.UtcNow.AddHours(-1),
            UpdatedAtUtc = DateTime.UtcNow,
            Revision = 60,
            IsDeleted = true,
            IsDirty = false,
            Lines =
            [
                new LocalInventoryTransferLine
                {
                    Id = lineId,
                    TransferId = transferId,
                    ItemId = itemId,
                    ItemNameOriginal = "Target denied purge transfer item",
                    Unit = "EA",
                    Quantity = 2m
                }
            ]
        });
        await db.SaveChangesAsync();

        var targetSession = CreateUserSession(
            TenantScopeCatalog.UsenetGroup,
            OfficeCodeCatalog.Yeonsu,
            TenantScopeCatalog.ScopeOfficeOnly,
            AppPermissionNames.DeliveryEdit);
        var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), targetSession);

        var result = await service.PermanentlyDeleteRecycleBinEntryAsync(
            RecycleBinEntityKind.InventoryTransfer,
            transferId,
            targetSession);

        Assert.False(result.Success);
        Assert.True(result.PermissionDenied);
        Assert.True(await db.InventoryTransfers.IgnoreQueryFilters().AnyAsync(transfer => transfer.Id == transferId));
        Assert.True(await db.InventoryTransferLines.IgnoreQueryFilters().AnyAsync(line => line.Id == lineId));
    }

    [Fact]
    public async Task RejectInventoryTransfer_DeniesAlreadyRejectedTransferFromChangingFinalReason()
    {
        using var appRoot = new LocalAppRootScope("georaeplan-transfer-reject-final-locked");
        await using var db = CreateDbContext(appRoot.DbPath);
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var itemId = Guid.Parse("b7111111-1111-1111-1111-111111111111");
        var transferId = Guid.Parse("b7222222-2222-2222-2222-222222222222");
        var lineId = Guid.Parse("b7333333-3333-3333-3333-333333333333");
        var now = new DateTime(2026, 6, 24, 3, 55, 0, DateTimeKind.Utc);
        db.Items.Add(CreateStockItem(itemId, "Rejected transfer reason locked item"));
        db.InventoryTransfers.Add(new LocalInventoryTransfer
        {
            Id = transferId,
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            TransferNumber = "TR-REJECT-FINAL-LOCKED",
            TransferDate = new DateOnly(2026, 6, 24),
            TransferStatus = InventoryTransferStatusNormalizer.Rejected,
            RejectReason = "initial reject",
            RejectedByUsername = "yeonsu-target",
            RejectedAtUtc = now.AddMinutes(-10),
            CreatedAtUtc = now.AddHours(-1),
            UpdatedAtUtc = now,
            Revision = 70,
            IsDirty = false,
            Lines =
            [
                new LocalInventoryTransferLine
                {
                    Id = lineId,
                    TransferId = transferId,
                    ItemId = itemId,
                    ItemNameOriginal = "Rejected transfer reason locked item",
                    Unit = "EA",
                    Quantity = 2m
                }
            ]
        });
        await db.SaveChangesAsync();

        var targetSession = CreateUserSession(
            TenantScopeCatalog.UsenetGroup,
            OfficeCodeCatalog.Yeonsu,
            TenantScopeCatalog.ScopeOfficeOnly,
            AppPermissionNames.DeliveryEdit);
        var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), targetSession);

        var result = await service.RejectInventoryTransferAsync(
            transferId,
            "changed reject reason",
            targetSession,
            expectedRevision: 70);

        Assert.False(result.Success);
        db.ChangeTracker.Clear();
        var stored = await db.InventoryTransfers.IgnoreQueryFilters().SingleAsync(transfer => transfer.Id == transferId);
        Assert.Equal(InventoryTransferStatusNormalizer.Rejected, stored.TransferStatus);
        Assert.Equal("initial reject", stored.RejectReason);
        Assert.False(stored.IsDirty);
    }

    [Fact]
    public async Task SaveInventoryTransfer_DeniesLegacyBlankStatusWithReceivedAudit()
    {
        using var appRoot = new LocalAppRootScope("georaeplan-transfer-save-legacy-received-final");
        await using var db = CreateDbContext(appRoot.DbPath);
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var itemId = Guid.Parse("b8111111-1111-1111-1111-111111111111");
        var transferId = Guid.Parse("b8222222-2222-2222-2222-222222222222");
        var lineId = Guid.Parse("b8333333-3333-3333-3333-333333333333");
        var now = new DateTime(2026, 6, 24, 4, 20, 0, DateTimeKind.Utc);
        var receivedAtUtc = now.AddMinutes(-10);
        db.Items.Add(CreateStockItem(itemId, "Legacy received transfer item"));
        db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock
        {
            ItemId = itemId,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 8m,
            UpdatedAtUtc = now,
            Revision = 80
        });
        db.InventoryTransfers.Add(new LocalInventoryTransfer
        {
            Id = transferId,
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            TransferNumber = "TR-LEGACY-RECEIVED-FINAL",
            TransferDate = new DateOnly(2026, 6, 24),
            TransferStatus = " ",
            Memo = "immutable legacy memo",
            ReceivedAtUtc = receivedAtUtc,
            CreatedAtUtc = now.AddHours(-1),
            UpdatedAtUtc = now,
            Revision = 80,
            IsDirty = false,
            Lines =
            [
                new LocalInventoryTransferLine
                {
                    Id = lineId,
                    TransferId = transferId,
                    ItemId = itemId,
                    ItemNameOriginal = "Legacy received transfer item",
                    Unit = "EA",
                    Quantity = 2m
                }
            ]
        });
        await db.SaveChangesAsync();

        var sourceSession = CreateUserSession(
            TenantScopeCatalog.UsenetGroup,
            OfficeCodeCatalog.Usenet,
            TenantScopeCatalog.ScopeOfficeOnly,
            AppPermissionNames.DeliveryEdit);
        var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), sourceSession);

        var result = await service.SaveInventoryTransferAsync(new LocalInventoryTransfer
        {
            Id = transferId,
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            TransferNumber = "TR-LEGACY-RECEIVED-FINAL",
            TransferDate = new DateOnly(2026, 6, 25),
            Memo = "must not change",
            CreatedAtUtc = now.AddHours(-1),
            UpdatedAtUtc = now,
            Revision = 80,
            IsDirty = false,
            Lines =
            [
                new LocalInventoryTransferLine
                {
                    Id = lineId,
                    TransferId = transferId,
                    ItemId = itemId,
                    ItemNameOriginal = "Legacy received transfer item",
                    Unit = "EA",
                    Quantity = 3m
                }
            ]
        }, sourceSession);

        Assert.False(result.Success);
        Assert.True(result.PermissionDenied);
        db.ChangeTracker.Clear();
        var stored = await db.InventoryTransfers
            .IgnoreQueryFilters()
            .Include(transfer => transfer.Lines)
            .SingleAsync(transfer => transfer.Id == transferId);
        Assert.Equal(" ", stored.TransferStatus);
        Assert.Equal("immutable legacy memo", stored.Memo);
        Assert.Equal(receivedAtUtc, stored.ReceivedAtUtc);
        Assert.Equal(80, stored.Revision);
        Assert.False(stored.IsDirty);
        Assert.Equal(2m, Assert.Single(stored.Lines).Quantity);
        Assert.Equal(8m, await db.ItemWarehouseStocks
            .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
        Assert.False(await db.AuditLogs.AnyAsync(log => log.EntityId == transferId.ToString("D")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InventoryTransferFinalActions_DenyLegacyBlankStatusWithOppositeFinalAudit(bool legacyReceived)
    {
        using var appRoot = new LocalAppRootScope("georaeplan-transfer-action-legacy-final");
        await using var db = CreateDbContext(appRoot.DbPath);
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var itemId = Guid.Parse("b9111111-1111-1111-1111-111111111111");
        var transferId = Guid.Parse("b9222222-2222-2222-2222-222222222222");
        var lineId = Guid.Parse("b9333333-3333-3333-3333-333333333333");
        var now = new DateTime(2026, 6, 24, 4, 45, 0, DateTimeKind.Utc);
        var finalAtUtc = now.AddMinutes(-10);
        db.Items.Add(CreateStockItem(itemId, "Legacy final action transfer item"));
        db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock
        {
            ItemId = itemId,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 8m,
            UpdatedAtUtc = now,
            Revision = 90
        });
        db.InventoryTransfers.Add(new LocalInventoryTransfer
        {
            Id = transferId,
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            TransferNumber = "TR-LEGACY-FINAL-ACTION",
            TransferDate = new DateOnly(2026, 6, 24),
            TransferStatus = " ",
            ReceivedAtUtc = legacyReceived ? finalAtUtc : null,
            RejectedAtUtc = legacyReceived ? null : finalAtUtc,
            RejectReason = legacyReceived ? string.Empty : "original legacy reject",
            CreatedAtUtc = now.AddHours(-1),
            UpdatedAtUtc = now,
            Revision = 90,
            IsDirty = false,
            Lines =
            [
                new LocalInventoryTransferLine
                {
                    Id = lineId,
                    TransferId = transferId,
                    ItemId = itemId,
                    ItemNameOriginal = "Legacy final action transfer item",
                    Unit = "EA",
                    Quantity = 2m
                }
            ]
        });
        await db.SaveChangesAsync();

        var targetSession = CreateUserSession(
            TenantScopeCatalog.UsenetGroup,
            OfficeCodeCatalog.Yeonsu,
            TenantScopeCatalog.ScopeOfficeOnly,
            AppPermissionNames.DeliveryEdit);
        var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), targetSession);

        OfficeMutationResult result;
        if (legacyReceived)
        {
            result = await service.RejectInventoryTransferAsync(
                transferId,
                "must not replace received final state",
                targetSession,
                expectedRevision: 90);
        }
        else
        {
            result = await service.ConfirmInventoryTransferReceiptAsync(
                transferId,
                [new LocalInventoryTransferLine { Id = lineId, ReceivedQuantity = 2m }],
                "must not replace rejected final state",
                targetSession,
                expectedRevision: 90);
        }

        Assert.False(result.Success);
        Assert.True(result.PermissionDenied);
        db.ChangeTracker.Clear();
        var stored = await db.InventoryTransfers.IgnoreQueryFilters().SingleAsync(transfer => transfer.Id == transferId);
        Assert.Equal(" ", stored.TransferStatus);
        Assert.Equal(legacyReceived ? finalAtUtc : null, stored.ReceivedAtUtc);
        Assert.Equal(legacyReceived ? null : finalAtUtc, stored.RejectedAtUtc);
        Assert.Equal(legacyReceived ? string.Empty : "original legacy reject", stored.RejectReason);
        Assert.Equal(90, stored.Revision);
        Assert.False(stored.IsDirty);
        Assert.False(await db.AuditLogs.AnyAsync(log => log.EntityId == transferId.ToString("D")));
    }

    [Fact]
    public async Task InventoryTransferViewModel_LoadsLegacyFinalAuditAsReadOnlyFinalStatus()
    {
        using var appRoot = new LocalAppRootScope("georaeplan-transfer-ui-legacy-final-status");
        await using var db = CreateDbContext(appRoot.DbPath);
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();
        var transfer = new LocalInventoryTransfer
        {
            Id = Guid.Parse("ba222222-2222-2222-2222-222222222222"),
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            TransferNumber = "TR-LEGACY-UI-RECEIVED",
            TransferDate = new DateOnly(2026, 6, 24),
            TransferStatus = " ",
            ReceivedAtUtc = new DateTime(2026, 6, 24, 5, 0, 0, DateTimeKind.Utc),
            CreatedAtUtc = new DateTime(2026, 6, 24, 4, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 6, 24, 5, 0, 0, DateTimeKind.Utc),
            Revision = 100,
            IsDirty = false
        };
        db.InventoryTransfers.Add(transfer);
        await db.SaveChangesAsync();

        var sourceSession = CreateUserSession(
            TenantScopeCatalog.UsenetGroup,
            OfficeCodeCatalog.Usenet,
            TenantScopeCatalog.ScopeOfficeOnly,
            AppPermissionNames.DeliveryEdit);
        using var viewModel = new InventoryTransferViewModel(
            new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), sourceSession),
            sourceSession);

        await viewModel.LoadAsync(transfer);

        Assert.Equal(InventoryTransferStatusNormalizer.Received, viewModel.TransferStatus);
        Assert.True(viewModel.IsFinalTransferStatus);
        Assert.False(viewModel.CanEditSourceDraft);
        Assert.False(viewModel.CanEditReceiptDraft);
        Assert.False(viewModel.CanSaveTransfer);
    }

    [Fact]
    public void InventoryTransferViewModel_CanDeleteTransfer_RequiresSourceOfficeForPendingStatus()
    {
        using var appRoot = new LocalAppRootScope("georaeplan-transfer-delete-ui-source-scope");
        using var db = CreateDbContext(appRoot.DbPath);
        db.Database.EnsureDeleted();
        db.Database.EnsureCreated();

        var targetSession = CreateUserSession(
            TenantScopeCatalog.UsenetGroup,
            OfficeCodeCatalog.Yeonsu,
            TenantScopeCatalog.ScopeOfficeOnly,
            AppPermissionNames.DeliveryEdit);
        var viewModel = new InventoryTransferViewModel(
            new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), targetSession),
            targetSession)
        {
            TransferId = Guid.Parse("c2222222-2222-2222-2222-222222222222"),
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            TransferStatus = InventoryTransferStatusNormalizer.Pending
        };

        Assert.False(viewModel.CanDeleteTransfer);
    }

    [Theory]
    [InlineData(OfficeCodeCatalog.Usenet, true, InventoryTransferStatusNormalizer.Pending, true, false)]
    [InlineData(OfficeCodeCatalog.Yeonsu, true, InventoryTransferStatusNormalizer.Pending, false, true)]
    [InlineData(OfficeCodeCatalog.Yeonsu, false, InventoryTransferStatusNormalizer.Pending, false, false)]
    [InlineData(OfficeCodeCatalog.Usenet, true, InventoryTransferStatusNormalizer.Received, false, false)]
    [InlineData(OfficeCodeCatalog.Yeonsu, true, InventoryTransferStatusNormalizer.Rejected, false, false)]
    public void InventoryTransferViewModel_EditorOwnership_SeparatesSourceReceiptAndFinalStates(
        string officeCode,
        bool hasDeliveryPermission,
        string transferStatus,
        bool expectedSourceEdit,
        bool expectedReceiptEdit)
    {
        using var appRoot = new LocalAppRootScope("georaeplan-transfer-ui-editor-ownership");
        using var db = CreateDbContext(appRoot.DbPath);
        db.Database.EnsureDeleted();
        db.Database.EnsureCreated();

        var permissions = hasDeliveryPermission
            ? new[] { AppPermissionNames.DeliveryEdit }
            : Array.Empty<string>();
        var session = CreateUserSession(
            TenantScopeCatalog.UsenetGroup,
            officeCode,
            TenantScopeCatalog.ScopeOfficeOnly,
            permissions);
        using var viewModel = new InventoryTransferViewModel(
            new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session),
            session)
        {
            TransferId = Guid.Parse("c3111111-1111-1111-1111-111111111111"),
            TransferRevision = 1,
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            TransferStatus = transferStatus
        };

        Assert.Equal(expectedSourceEdit, viewModel.CanEditSourceDraft);
        Assert.Equal(expectedReceiptEdit, viewModel.CanEditReceiptDraft);
        Assert.Equal(expectedSourceEdit, viewModel.CanSaveTransfer);
        Assert.Equal(expectedSourceEdit || expectedReceiptEdit, viewModel.CanEditTransferDraft);
    }

    [Fact]
    public void InventoryTransferViewModel_RouteAndStatusChanges_NotifyDerivedOwnershipProperties()
    {
        using var appRoot = new LocalAppRootScope("georaeplan-transfer-ui-editor-notifications");
        using var db = CreateDbContext(appRoot.DbPath);
        db.Database.EnsureDeleted();
        db.Database.EnsureCreated();
        var session = CreateUserSession(
            TenantScopeCatalog.UsenetGroup,
            OfficeCodeCatalog.Usenet,
            TenantScopeCatalog.ScopeOfficeOnly,
            AppPermissionNames.DeliveryEdit);
        using var viewModel = new InventoryTransferViewModel(
            new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session),
            session)
        {
            TransferId = Guid.Parse("c3222222-2222-2222-2222-222222222222"),
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            TransferStatus = InventoryTransferStatusNormalizer.Pending
        };
        var changedProperties = new HashSet<string>(StringComparer.Ordinal);
        viewModel.PropertyChanged += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.PropertyName))
                changedProperties.Add(args.PropertyName);
        };

        viewModel.FromWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse;

        Assert.Contains(nameof(viewModel.CanEditSourceDraft), changedProperties);
        Assert.Contains(nameof(viewModel.CanSaveTransfer), changedProperties);
        Assert.Contains(nameof(viewModel.CanUpdateSourceLine), changedProperties);

        changedProperties.Clear();
        viewModel.ToWarehouseCode = OfficeCodeCatalog.ItworldMainWarehouse;

        Assert.Contains(nameof(viewModel.CanEditReceiptDraft), changedProperties);
        Assert.Contains(nameof(viewModel.CanUpdateReceiptLine), changedProperties);
        Assert.Contains(nameof(viewModel.CanConfirmReceipt), changedProperties);

        changedProperties.Clear();
        viewModel.TransferStatus = InventoryTransferStatusNormalizer.Received;

        Assert.Contains(nameof(viewModel.CanEditSourceDraft), changedProperties);
        Assert.Contains(nameof(viewModel.CanEditReceiptDraft), changedProperties);
        Assert.Contains(nameof(viewModel.CanSaveTransfer), changedProperties);
        Assert.Contains(nameof(viewModel.CanAddLine), changedProperties);
        Assert.Contains(nameof(viewModel.CanUpdateSourceLine), changedProperties);
        Assert.Contains(nameof(viewModel.CanUpdateReceiptLine), changedProperties);
        Assert.Contains(nameof(viewModel.CanDeleteLine), changedProperties);
    }

    [Fact]
    public void InventoryTransferViewModel_LineCommands_EnforceSourceAndReceiptFieldOwnership()
    {
        using var appRoot = new LocalAppRootScope("georaeplan-transfer-ui-line-ownership");
        using var sourceDb = CreateDbContext(appRoot.DbPath);
        sourceDb.Database.EnsureDeleted();
        sourceDb.Database.EnsureCreated();
        var sourceSession = CreateUserSession(
            TenantScopeCatalog.UsenetGroup,
            OfficeCodeCatalog.Usenet,
            TenantScopeCatalog.ScopeOfficeOnly,
            AppPermissionNames.DeliveryEdit);
        using var sourceViewModel = new InventoryTransferViewModel(
            new LocalStateService(sourceDb, new OfficeAccessService(), new SyncRequestDispatcher(), sourceSession),
            sourceSession)
        {
            TransferId = Guid.Parse("c3333333-3333-3333-3333-333333333333"),
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            TransferStatus = InventoryTransferStatusNormalizer.Pending
        };
        var originalItem = CreateStockItem(Guid.Parse("c3444444-4444-4444-4444-444444444444"), "Original item");
        var replacementItem = CreateStockItem(Guid.Parse("c3555555-5555-5555-5555-555555555555"), "Replacement item");
        var sourceLine = new InventoryTransferLineEditModel
        {
            Id = Guid.Parse("c3666666-6666-6666-6666-666666666666"),
            ItemId = originalItem.Id,
            ItemName = originalItem.NameOriginal,
            Specification = "old spec",
            Unit = "EA",
            Quantity = 2m,
            ReceivedQuantity = 1m,
            Remark = "old source remark",
            ReceiptRemark = "target-owned receipt remark"
        };
        sourceViewModel.Lines.Add(sourceLine);
        sourceViewModel.SelectedLine = sourceLine;
        sourceViewModel.SelectedInputItem = replacementItem;
        sourceViewModel.InputItemName = replacementItem.NameOriginal;
        sourceViewModel.InputSpec = "new spec";
        sourceViewModel.InputUnit = "BOX";
        sourceViewModel.InputQty = 4m;
        sourceViewModel.InputRemark = "new source remark";
        sourceViewModel.InputReceivedQty = 99m;
        sourceViewModel.InputReceiptRemark = "source spoof";

        sourceViewModel.UpdateSourceLineCommand.Execute(null);

        Assert.Equal(replacementItem.Id, sourceLine.ItemId);
        Assert.Equal("new spec", sourceLine.Specification);
        Assert.Equal(4m, sourceLine.Quantity);
        Assert.Equal("new source remark", sourceLine.Remark);
        Assert.Equal(4m, sourceLine.ReceivedQuantity);
        Assert.Empty(sourceLine.ReceiptRemark);

        sourceViewModel.UpdateReceiptLineCommand.Execute(null);

        Assert.Equal(4m, sourceLine.ReceivedQuantity);
        Assert.Empty(sourceLine.ReceiptRemark);

        sourceViewModel.SelectedLine = null;
        sourceViewModel.SelectedInputItem = replacementItem;
        sourceViewModel.InputItemName = replacementItem.NameOriginal;
        sourceViewModel.InputSpec = "added spec";
        sourceViewModel.InputUnit = "EA";
        sourceViewModel.InputQty = 3m;
        sourceViewModel.InputRemark = "added source remark";
        sourceViewModel.InputReceivedQty = 77m;
        sourceViewModel.InputReceiptRemark = "added spoof";

        sourceViewModel.AddLineCommand.Execute(null);

        var addedLine = Assert.Single(sourceViewModel.Lines, line => line.Id != sourceLine.Id);
        Assert.Equal(3m, addedLine.ReceivedQuantity);
        Assert.Empty(addedLine.ReceiptRemark);

        using var targetDb = CreateDbContext(appRoot.DbPath);
        var targetSession = CreateUserSession(
            TenantScopeCatalog.UsenetGroup,
            OfficeCodeCatalog.Yeonsu,
            TenantScopeCatalog.ScopeOfficeOnly,
            AppPermissionNames.DeliveryEdit);
        using var targetViewModel = new InventoryTransferViewModel(
            new LocalStateService(targetDb, new OfficeAccessService(), new SyncRequestDispatcher(), targetSession),
            targetSession)
        {
            TransferId = Guid.Parse("c3777777-7777-7777-7777-777777777777"),
            TransferRevision = 1,
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            TransferStatus = InventoryTransferStatusNormalizer.Pending
        };
        var targetLine = new InventoryTransferLineEditModel
        {
            Id = Guid.Parse("c3888888-8888-8888-8888-888888888888"),
            ItemId = originalItem.Id,
            ItemName = originalItem.NameOriginal,
            Specification = "locked spec",
            Unit = "EA",
            Quantity = 5m,
            ReceivedQuantity = 5m,
            Remark = "locked source remark",
            ReceiptRemark = string.Empty
        };
        targetViewModel.Lines.Add(targetLine);
        targetViewModel.SelectedLine = targetLine;
        targetViewModel.SelectedInputItem = replacementItem;
        targetViewModel.InputItemName = "target spoof";
        targetViewModel.InputSpec = "target spoof spec";
        targetViewModel.InputUnit = "BOX";
        targetViewModel.InputQty = 999m;
        targetViewModel.InputRemark = "target spoof remark";
        targetViewModel.InputReceivedQty = 0m;
        targetViewModel.InputReceiptRemark = "short shipment";

        targetViewModel.UpdateSourceLineCommand.Execute(null);

        Assert.Equal(originalItem.Id, targetLine.ItemId);
        Assert.Equal(originalItem.NameOriginal, targetLine.ItemName);
        Assert.Equal("locked spec", targetLine.Specification);
        Assert.Equal(5m, targetLine.Quantity);
        Assert.Equal("locked source remark", targetLine.Remark);

        targetViewModel.UpdateReceiptLineCommand.Execute(null);

        Assert.Equal(originalItem.Id, targetLine.ItemId);
        Assert.Equal(originalItem.NameOriginal, targetLine.ItemName);
        Assert.Equal("locked spec", targetLine.Specification);
        Assert.Equal(5m, targetLine.Quantity);
        Assert.Equal("locked source remark", targetLine.Remark);
        Assert.Equal(0m, targetLine.ReceivedQuantity);
        Assert.Equal("short shipment", targetLine.ReceiptRemark);

        var targetLineCount = targetViewModel.Lines.Count;
        targetViewModel.AddLineCommand.Execute(null);
        targetViewModel.DeleteLineCommand.Execute(null);
        Assert.Equal(targetLineCount, targetViewModel.Lines.Count);
        Assert.Same(targetLine, targetViewModel.SelectedLine);
    }

    [Fact]
    public void InventoryTransferViewModel_QuantityCommands_RejectInvalidValuesWithoutMutation()
    {
        using var appRoot = new LocalAppRootScope("georaeplan-transfer-ui-quantity-contract");
        using var sourceDb = CreateDbContext(appRoot.DbPath);
        sourceDb.Database.EnsureDeleted();
        sourceDb.Database.EnsureCreated();
        var sourceSession = CreateUserSession(
            TenantScopeCatalog.UsenetGroup,
            OfficeCodeCatalog.Usenet,
            TenantScopeCatalog.ScopeOfficeOnly,
            AppPermissionNames.DeliveryEdit);
        using var sourceViewModel = new InventoryTransferViewModel(
            new LocalStateService(sourceDb, new OfficeAccessService(), new SyncRequestDispatcher(), sourceSession),
            sourceSession)
        {
            TransferId = Guid.Parse("c3cccccc-cccc-cccc-cccc-cccccccccccc"),
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            TransferStatus = InventoryTransferStatusNormalizer.Pending
        };
        var item = CreateStockItem(Guid.Parse("c3dddddd-dddd-dddd-dddd-dddddddddddd"), "Quantity item");
        var sourceLine = new InventoryTransferLineEditModel
        {
            Id = Guid.Parse("c3eeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
            ItemId = item.Id,
            ItemName = item.NameOriginal,
            Unit = "EA",
            Quantity = 2m,
            ReceivedQuantity = 2m,
            Remark = "original source remark"
        };
        sourceViewModel.Lines.Add(sourceLine);
        sourceViewModel.SelectedLine = sourceLine;
        sourceViewModel.SelectedInputItem = item;
        sourceViewModel.InputItemName = item.NameOriginal;
        sourceViewModel.InputUnit = "EA";
        sourceViewModel.InputQty = 1.001m;
        sourceViewModel.InputRemark = "should not apply";

        Assert.False(sourceViewModel.CanUpdateSourceLine);
        sourceViewModel.UpdateSourceLineCommand.Execute(null);

        Assert.Equal(2m, sourceLine.Quantity);
        Assert.Equal("original source remark", sourceLine.Remark);
        Assert.Contains("소수 둘째 자리", sourceViewModel.StatusMessage, StringComparison.Ordinal);

        sourceViewModel.SelectedLine = null;
        var sourceLineCount = sourceViewModel.Lines.Count;
        Assert.False(sourceViewModel.CanAddLine);
        sourceViewModel.AddLineCommand.Execute(null);
        Assert.Equal(sourceLineCount, sourceViewModel.Lines.Count);

        using var targetDb = CreateDbContext(appRoot.DbPath);
        var targetSession = CreateUserSession(
            TenantScopeCatalog.UsenetGroup,
            OfficeCodeCatalog.Yeonsu,
            TenantScopeCatalog.ScopeOfficeOnly,
            AppPermissionNames.DeliveryEdit);
        using var targetViewModel = new InventoryTransferViewModel(
            new LocalStateService(targetDb, new OfficeAccessService(), new SyncRequestDispatcher(), targetSession),
            targetSession)
        {
            TransferId = Guid.Parse("c3ffffff-ffff-ffff-ffff-ffffffffffff"),
            TransferRevision = 1,
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            TransferStatus = InventoryTransferStatusNormalizer.Pending
        };
        var targetLine = new InventoryTransferLineEditModel
        {
            Id = Guid.Parse("c4000000-0000-0000-0000-000000000001"),
            ItemId = item.Id,
            ItemName = item.NameOriginal,
            Unit = "EA",
            Quantity = 2m,
            ReceivedQuantity = 1m,
            ReceiptRemark = "original receipt remark"
        };
        targetViewModel.Lines.Add(targetLine);
        targetViewModel.SelectedLine = targetLine;

        foreach (var invalidReceivedQuantity in new[]
                 {
                     2.01m,
                     1.001m,
                     -0.01m,
                     QuantityNumericContract.MaxQuantity18Scale2 + 0.01m
                 })
        {
            targetViewModel.InputReceivedQty = invalidReceivedQuantity;
            targetViewModel.InputReceiptRemark = "should not apply";

            Assert.False(targetViewModel.CanUpdateReceiptLine);
            targetViewModel.UpdateReceiptLineCommand.Execute(null);

            Assert.Equal(1m, targetLine.ReceivedQuantity);
            Assert.Equal("original receipt remark", targetLine.ReceiptRemark);
            Assert.Contains("요청수량 이하", targetViewModel.StatusMessage, StringComparison.Ordinal);
        }

        targetViewModel.InputReceivedQty = 0m;
        targetViewModel.InputReceiptRemark = "zero receipt is valid";
        Assert.True(targetViewModel.CanUpdateReceiptLine);
        targetViewModel.UpdateReceiptLineCommand.Execute(null);
        Assert.Equal(0m, targetLine.ReceivedQuantity);
        Assert.Equal("zero receipt is valid", targetLine.ReceiptRemark);
    }

    [Fact]
    public async Task InventoryTransferViewModel_ConfirmReceiptCommand_RejectsInvalidLineBeforeLocalMutation()
    {
        using var appRoot = new LocalAppRootScope("georaeplan-transfer-ui-confirm-quantity-contract");
        await using var db = CreateDbContext(appRoot.DbPath);
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();
        var targetSession = CreateUserSession(
            TenantScopeCatalog.UsenetGroup,
            OfficeCodeCatalog.Yeonsu,
            TenantScopeCatalog.ScopeOfficeOnly,
            AppPermissionNames.DeliveryEdit);
        using var viewModel = new InventoryTransferViewModel(
            new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), targetSession),
            targetSession)
        {
            TransferId = Guid.Parse("c4000000-0000-0000-0000-000000000002"),
            TransferRevision = 1,
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            TransferStatus = InventoryTransferStatusNormalizer.Pending
        };
        viewModel.Lines.Add(new InventoryTransferLineEditModel
        {
            Id = Guid.Parse("c4000000-0000-0000-0000-000000000003"),
            ItemId = Guid.Parse("c4000000-0000-0000-0000-000000000004"),
            ItemName = "Invalid receipt item",
            Unit = "EA",
            Quantity = 2m,
            ReceivedQuantity = 1.001m
        });

        Assert.True(viewModel.CanConfirmReceipt);
        await viewModel.ConfirmReceiptCommand.ExecuteAsync(null);

        Assert.Equal(InventoryTransferStatusNormalizer.Pending, viewModel.TransferStatus);
        Assert.Contains("소수 둘째 자리", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.False(await db.InventoryTransfers.IgnoreQueryFilters().AnyAsync());
        Assert.False(await db.AuditLogs.AnyAsync());
    }

    [Fact]
    public async Task InventoryTransferViewModel_TargetReceiptDraft_DoesNotUseSourceAutoSavePath()
    {
        using var appRoot = new LocalAppRootScope("georaeplan-transfer-ui-target-autosave");
        await using var db = CreateDbContext(appRoot.DbPath);
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();
        var targetSession = CreateUserSession(
            TenantScopeCatalog.UsenetGroup,
            OfficeCodeCatalog.Yeonsu,
            TenantScopeCatalog.ScopeOfficeOnly,
            AppPermissionNames.DeliveryEdit);
        using var viewModel = new InventoryTransferViewModel(
            new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), targetSession),
            targetSession)
        {
            TransferId = Guid.Parse("c3999999-9999-9999-9999-999999999999"),
            TransferRevision = 1,
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            TransferStatus = InventoryTransferStatusNormalizer.Pending,
            ReceiveMemo = "target receipt draft"
        };
        viewModel.Lines.Add(new InventoryTransferLineEditModel
        {
            Id = Guid.Parse("c3aaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            ItemId = Guid.Parse("c3bbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            ItemName = "Receipt draft item",
            Unit = "EA",
            Quantity = 2m,
            ReceivedQuantity = 1m,
            ReceiptRemark = "one missing"
        });

        Assert.False(viewModel.CanEditSourceDraft);
        Assert.True(viewModel.CanEditReceiptDraft);
        Assert.True(viewModel.HasPendingChanges);

        var saved = await viewModel.TryAutoSaveOnCloseAsync();

        Assert.False(saved);
        Assert.Contains("출발지 문서로 자동저장하지 않았습니다", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.False(await db.InventoryTransfers.IgnoreQueryFilters().AnyAsync());
    }

    private static async Task<InventoryTransferLineIdentityFixture> SeedInventoryTransferLineIdentityFixtureAsync(
        LocalDbContext db)
    {
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var itemId = Guid.Parse("d1000000-0000-0000-0000-000000000001");
        var primaryTransferId = Guid.Parse("d1000000-0000-0000-0000-000000000002");
        var primaryLineId = Guid.Parse("d1000000-0000-0000-0000-000000000003");
        var foreignTransferId = Guid.Parse("d1000000-0000-0000-0000-000000000004");
        var foreignLineId = Guid.Parse("d1000000-0000-0000-0000-000000000005");
        const long primaryRevision = 41;
        var now = new DateTime(2026, 8, 2, 4, 0, 0, DateTimeKind.Utc);

        db.Items.Add(CreateStockItem(itemId, "Line identity guard item"));
        db.ItemWarehouseStocks.AddRange(
            new LocalItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 10m,
                UpdatedAtUtc = now,
                Revision = 10
            },
            new LocalItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                Quantity = 0m,
                UpdatedAtUtc = now,
                Revision = 10
            });
        db.InventoryTransfers.AddRange(
            new LocalInventoryTransfer
            {
                Id = primaryTransferId,
                TransferNumber = "TR-LINE-ID-PRIMARY",
                TransferDate = new DateOnly(2026, 8, 2),
                FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                Memo = "original transfer memo",
                TransferStatus = InventoryTransferStatusNormalizer.Pending,
                CreatedAtUtc = now.AddHours(-1),
                UpdatedAtUtc = now,
                Revision = primaryRevision,
                IsDirty = false,
                Lines =
                [
                    new LocalInventoryTransferLine
                    {
                        Id = primaryLineId,
                        TransferId = primaryTransferId,
                        ItemId = itemId,
                        ItemNameOriginal = "Line identity guard item",
                        Unit = "EA",
                        Quantity = 2m,
                        ReceivedQuantity = 2m,
                        QuantityDifference = 0m,
                        Remark = "original line remark",
                        ReceiptRemark = "original receipt remark"
                    }
                ]
            },
            new LocalInventoryTransfer
            {
                Id = foreignTransferId,
                TransferNumber = "TR-LINE-ID-FOREIGN-DELETED",
                TransferDate = new DateOnly(2026, 8, 1),
                FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                ToWarehouseCode = OfficeCodeCatalog.ItworldMainWarehouse,
                TransferStatus = InventoryTransferStatusNormalizer.Pending,
                CreatedAtUtc = now.AddHours(-2),
                UpdatedAtUtc = now.AddHours(-1),
                Revision = 40,
                IsDirty = false,
                IsDeleted = true,
                Lines =
                [
                    new LocalInventoryTransferLine
                    {
                        Id = foreignLineId,
                        TransferId = foreignTransferId,
                        ItemId = itemId,
                        ItemNameOriginal = "Line identity guard item",
                        Unit = "EA",
                        Quantity = 1m,
                        ReceivedQuantity = 1m,
                        QuantityDifference = 0m,
                        IsDeleted = true
                    }
                ]
            });
        await db.SaveChangesAsync();

        return new InventoryTransferLineIdentityFixture(
            itemId,
            primaryTransferId,
            primaryLineId,
            foreignTransferId,
            foreignLineId,
            primaryRevision,
            CreateUserSession(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet,
                TenantScopeCatalog.ScopeOfficeOnly,
                AppPermissionNames.DeliveryEdit),
            CreateUserSession(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Yeonsu,
                TenantScopeCatalog.ScopeOfficeOnly,
                AppPermissionNames.DeliveryEdit));
    }

    private static LocalInventoryTransfer BuildInventoryTransferSaveCandidate(
        InventoryTransferLineIdentityFixture fixture,
        params Guid[] lineIds)
    {
        return new LocalInventoryTransfer
        {
            Id = fixture.PrimaryTransferId,
            TransferNumber = "TR-LINE-ID-PRIMARY",
            TransferDate = new DateOnly(2026, 8, 2),
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            Memo = "must not be saved",
            TransferStatus = InventoryTransferStatusNormalizer.Pending,
            CreatedAtUtc = new DateTime(2026, 8, 2, 3, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 8, 2, 4, 0, 0, DateTimeKind.Utc),
            Revision = fixture.PrimaryRevision,
            IsDirty = false,
            Lines = lineIds.Select(lineId => new LocalInventoryTransferLine
            {
                Id = lineId,
                TransferId = fixture.PrimaryTransferId,
                ItemId = fixture.ItemId,
                ItemNameOriginal = "Line identity guard item",
                Unit = "EA",
                Quantity = 1m
            }).ToList()
        };
    }

    private static async Task AssertInventoryTransferLineIdentityFixtureUnchangedAsync(
        LocalDbContext db,
        InventoryTransferLineIdentityFixture fixture)
    {
        db.ChangeTracker.Clear();
        var stored = await db.InventoryTransfers
            .IgnoreQueryFilters()
            .Include(transfer => transfer.Lines)
            .SingleAsync(transfer => transfer.Id == fixture.PrimaryTransferId);
        var storedLine = Assert.Single(stored.Lines);
        Assert.Equal("original transfer memo", stored.Memo);
        Assert.Equal(InventoryTransferStatusNormalizer.Pending, stored.TransferStatus);
        Assert.Equal(fixture.PrimaryRevision, stored.Revision);
        Assert.False(stored.IsDirty);
        Assert.Empty(stored.ReceivedByUsername);
        Assert.Null(stored.ReceivedAtUtc);
        Assert.Equal(fixture.PrimaryLineId, storedLine.Id);
        Assert.Equal(2m, storedLine.Quantity);
        Assert.Equal(2m, storedLine.ReceivedQuantity);
        Assert.Equal(0m, storedLine.QuantityDifference);
        Assert.Equal("original line remark", storedLine.Remark);
        Assert.Equal("original receipt remark", storedLine.ReceiptRemark);
        Assert.Equal(10m, await db.ItemWarehouseStocks
            .Where(stock => stock.ItemId == fixture.ItemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
        Assert.Equal(0m, await db.ItemWarehouseStocks
            .Where(stock => stock.ItemId == fixture.ItemId && stock.WarehouseCode == OfficeCodeCatalog.YeonsuMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
        Assert.False((await db.Items.IgnoreQueryFilters().SingleAsync(item => item.Id == fixture.ItemId)).IsDirty);
        var foreignLine = await db.InventoryTransferLines
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(line => line.Id == fixture.ForeignLineId);
        Assert.Equal(fixture.ForeignTransferId, foreignLine.TransferId);
        Assert.True(foreignLine.IsDeleted);
        Assert.False(await db.AuditLogs.AnyAsync(log => log.EntityId == fixture.PrimaryTransferId.ToString("D")));
    }

    private sealed record InventoryTransferLineIdentityFixture(
        Guid ItemId,
        Guid PrimaryTransferId,
        Guid PrimaryLineId,
        Guid ForeignTransferId,
        Guid ForeignLineId,
        long PrimaryRevision,
        SessionState SourceSession,
        SessionState TargetSession);

    private static LocalItem CreateStockItem(Guid itemId, string name) => new()
    {
        Id = itemId,
        TenantCode = TenantScopeCatalog.UsenetGroup,
        OfficeCode = OfficeCodeCatalog.Shared,
        NameOriginal = name,
        NameMatchKey = name.Replace(" ", string.Empty).ToUpperInvariant(),
        Unit = "EA",
        ItemKind = ItemKinds.Product,
        TrackingType = ItemTrackingTypes.Stock,
        CurrentStock = 10m,
        IsDirty = false
    };

    private static SessionState CreateUserSession(
        string tenantCode,
        string officeCode,
        string scopeType,
        params string[] permissions)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            Username = $"{officeCode.ToLowerInvariant()}-delivery-user",
            Role = DomainConstants.RoleUser,
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ScopeType = scopeType,
            Permissions = permissions.ToList()
        });
        return session;
    }

    private static LocalDbContext CreateDbContext(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        return new LocalDbContext(options);
    }

    private sealed class LocalAppRootScope : IDisposable
    {
        private readonly string? _previousAppRoot;
        private readonly string _appRoot;
        public string DbPath { get; }

        public LocalAppRootScope(string prefix)
        {
            _previousAppRoot = Environment.GetEnvironmentVariable("GEORAEPLAN_APP_ROOT");
            _appRoot = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_appRoot);
            DbPath = Path.Combine(_appRoot, "거래플랜-test.db");
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", _appRoot);
        }

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
                // Test temp cleanup failures must not hide assertion failures.
            }
        }
    }
}
