using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed partial class InventoryTransferScopeGuardTests
{
    [Theory]
    [InlineData(false, 2)]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    public async Task TransferPartialStock_DestinationReceiptAndRejectionPreserveOwnBaseline(bool reject, int received)
    {
        using var appRoot = new LocalAppRootScope("transfer-partial-destination");
        await using var db = CreateDbContext(appRoot.DbPath);
        await db.Database.EnsureCreatedAsync();
        var item = CreateStockItem(Guid.NewGuid(), "Sender original"); item.OfficeCode = OfficeCodeCatalog.Usenet;
        db.Items.Add(item);
        db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock { ItemId = item.Id,
            WarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse, Quantity = 5m, Revision = 77 });
        var transfer = new LocalInventoryTransfer { Id = Guid.NewGuid(), Revision = 100, IsDirty = false,
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse, ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            Lines = [new LocalInventoryTransferLine { ItemId = item.Id, ItemNameOriginal = item.NameOriginal, Quantity = 3m }] };
        db.InventoryTransfers.Add(transfer);
        await db.SaveChangesAsync();
        var session = CreateUserSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Yeonsu,
            TenantScopeCatalog.ScopeOfficeOnly, AppPermissionNames.DeliveryEdit);
        var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
        var result = reject
            ? await service.RejectInventoryTransferAsync(transfer.Id, "합성 시험 반려", session, expectedRevision: 100)
            : await service.ConfirmInventoryTransferReceiptAsync(transfer.Id,
                [new LocalInventoryTransferLine { Id = transfer.Lines.Single().Id, ReceivedQuantity = received }], "", session, expectedRevision: 100);
        Assert.True(result.Success, result.Message);
        var stock = await db.ItemWarehouseStocks.AsNoTracking().SingleAsync();
        Assert.Equal(OfficeCodeCatalog.YeonsuMainWarehouse, stock.WarehouseCode);
        Assert.Equal(5m + received, stock.Quantity);
        Assert.Equal(77, stock.Revision);
        Assert.False((await db.Items.AsNoTracking().SingleAsync()).IsDirty);
    }

    [Fact]
    public async Task TransferPartialStock_StockWriteFailureRollsBackDocumentAndAudit()
    {
        using var appRoot = new LocalAppRootScope("transfer-stock-rollback");
        await using var db = CreateDbContext(appRoot.DbPath);
        await db.Database.EnsureCreatedAsync();
        var item = CreateStockItem(Guid.NewGuid(), "Rollback item"); item.OfficeCode = OfficeCodeCatalog.Usenet;
        db.Items.Add(item);
        db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock { ItemId = item.Id,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse, Quantity = 10m, Revision = 77 });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("CREATE TEMP TRIGGER fail_transfer_stock BEFORE UPDATE ON ItemWarehouseStocks BEGIN SELECT RAISE(ABORT, 'test stock failure'); END;");
        var session = CreateUserSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet,
            TenantScopeCatalog.ScopeOfficeOnly, AppPermissionNames.DeliveryEdit);
        var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
        var transfer = new LocalInventoryTransfer { Id = Guid.NewGuid(),
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse, ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            Lines = [new LocalInventoryTransferLine { ItemId = item.Id, ItemNameOriginal = item.NameOriginal, Quantity = 3m }] };
        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => service.SaveInventoryTransferAsync(transfer, session));
        Assert.Contains("test stock failure", failure.ToString());
        Assert.Empty(await db.InventoryTransfers.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await db.InventoryTransferLines.ToListAsync());
        Assert.Empty(await db.AuditLogs.Where(x => x.EntityName == "LocalInventoryTransfer").ToListAsync());
        var stock = await db.ItemWarehouseStocks.AsNoTracking().SingleAsync();
        Assert.Equal(10m, stock.Quantity); Assert.Equal(77, stock.Revision);
    }

    [Fact]
    public async Task TransferPartialStock_PreservesServerBaselineAcrossSaveEditAndDelete()
    {
        using var appRoot = new LocalAppRootScope("transfer-partial-stock");
        await using var db = CreateDbContext(appRoot.DbPath);
        await db.Database.EnsureCreatedAsync();
        var item = CreateStockItem(Guid.NewGuid(), "Partial cache stock");
        item.OfficeCode = OfficeCodeCatalog.Usenet;
        item.Revision = 50;
        db.Items.Add(item);
        db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock { ItemId = item.Id,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse, Quantity = 10m, Revision = 77 });
        await db.SaveChangesAsync();
        var session = CreateUserSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet,
            TenantScopeCatalog.ScopeOfficeOnly, AppPermissionNames.DeliveryEdit, AppPermissionNames.ItemEdit);
        var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
        var transfer = new LocalInventoryTransfer { Id = Guid.NewGuid(), FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse, Lines = [new LocalInventoryTransferLine { ItemId = item.Id,
                ItemNameOriginal = item.NameOriginal, Unit = "EA", Quantity = 3m }] };
        var save = await service.SaveInventoryTransferAsync(transfer, session);
        Assert.True(save.Success, save.Message);
        var stock = await db.ItemWarehouseStocks.AsNoTracking().SingleAsync(x => x.ItemId == item.Id && x.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse);
        Assert.Equal(7m, stock.Quantity);
        Assert.Equal(77, stock.Revision);
        Assert.Empty(await service.GetDirtyItemsForSyncAsync(session));
        transfer = (await service.GetInventoryTransferAsync(transfer.Id))!;
        transfer.Lines.Single().Quantity = 4m;
        var edit = await service.SaveInventoryTransferAsync(transfer, session);
        Assert.True(edit.Success, edit.Message);
        Assert.Equal(6m, (await db.ItemWarehouseStocks.AsNoTracking().SingleAsync()).Quantity);
        var delete = await service.DeleteInventoryTransferAsync(transfer.Id, session);
        Assert.True(delete.Success, delete.Message);
        stock = await db.ItemWarehouseStocks.AsNoTracking().SingleAsync();
        Assert.Equal(10m, stock.Quantity);
        Assert.Equal(77, stock.Revision);
        Assert.Empty(await service.GetDirtyItemsForSyncAsync(session));
    }
}
