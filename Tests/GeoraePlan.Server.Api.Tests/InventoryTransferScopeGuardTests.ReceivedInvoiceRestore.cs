using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Security;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed partial class InventoryTransferScopeGuardTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceivedItemAccess_RestoreDeletedSale_RequiresValidReceiptWithoutChangingMaster(bool revokeReceipt)
    {
        var itemId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        await SeedPendingTransferAsync(itemId, transferId, Guid.NewGuid(), "received sale restore", 8m);
        await using (var seed = CreateDbContext(CreateAdminUser()))
        {
            (await seed.Items.SingleAsync(row => row.Id == itemId)).OfficeCode = OfficeCodeCatalog.Usenet;
            seed.Customers.Add(new Customer { Id = customerId, TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Yeonsu, ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                NameOriginal = "received sale customer", TradeType = "매출" });
            await seed.SaveChangesAsync();
        }
        var user = new TestCurrentUserContext { Username = "received-sale-restorer", OfficeCode = OfficeCodeCatalog.Yeonsu,
            TenantCode = TenantScopeCatalog.UsenetGroup, ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = [PermissionNames.DeliveryEdit, PermissionNames.InvoiceEdit, PermissionNames.DataBackupRestore] };
        await using var db = CreateDbContext(user);
        var sync = CreateController(db, user);
        var transfer = await db.InventoryTransfers.IgnoreQueryFilters().Include(row => row.Lines).SingleAsync(row => row.Id == transferId);
        var receipt = await sync.Push(new SyncPushRequest { DeviceId = "received-sale-restore",
            InventoryTransfers = [BuildReceiptDto(transfer, user.Username, 2m)] }, CancellationToken.None);
        Assert.Equal(1, Assert.IsType<SyncPushResult>(Assert.IsType<OkObjectResult>(receipt.Result).Value).AcceptedCount);
        var sale = BuildInventoryInvoiceDto(Guid.NewGuid(), customerId, itemId, "received sale restore", VoucherType.Sales, 1m, user.Username, DateTime.UtcNow);
        sale.OfficeCode = sale.ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu;
        sale.SourceWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse;
        var saved = await sync.Push(new SyncPushRequest { DeviceId = "received-sale-restore", Invoices = [sale] }, CancellationToken.None);
        Assert.Equal(1, Assert.IsType<SyncPushResult>(Assert.IsType<OkObjectResult>(saved.Result).Value).AcceptedCount);
        db.ChangeTracker.Clear();
        var pulled = Assert.IsType<SyncPullResponse>(Assert.IsType<OkObjectResult>((await sync.Pull(0, CancellationToken.None)).Result).Value);
        sale = Assert.Single(pulled.Invoices, row => row.Id == sale.Id);
        sale.ExpectedRevision = await db.Invoices.Where(row => row.Id == sale.Id).Select(row => row.Revision).SingleAsync();
        sale.IsDeleted = true;
        sale.UpdatedAtUtc = DateTime.UtcNow;
        sale.MutationId = Guid.NewGuid().ToString("N");
        sale.MutationCreatedAtUtc = DateTime.UtcNow;
        var deleted = await sync.Push(new SyncPushRequest { DeviceId = "received-sale-restore", Invoices = [sale] }, CancellationToken.None);
        var deletion = Assert.IsType<SyncPushResult>(Assert.IsType<OkObjectResult>(deleted.Result).Value);
        Assert.True(deletion.AcceptedCount == 1, System.Text.Json.JsonSerializer.Serialize(deletion));
        db.ChangeTracker.Clear();
        if (revokeReceipt)
        {
            (await db.InventoryTransfers.IgnoreQueryFilters().SingleAsync(row => row.Id == transferId)).IsDeleted = true;
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }
        var beforeItem = await db.Items.IgnoreQueryFilters().AsNoTracking().SingleAsync(row => row.Id == itemId);
        var beforeInvoice = await db.Invoices.IgnoreQueryFilters().AsNoTracking().SingleAsync(row => row.Id == sale.Id);
        var response = await CreateRecycleBinController(db, user).Restore(new RecycleBinMutationRequest {
            Items = [new RecycleBinMutationTargetDto { Kind = "invoice", EntityId = sale.Id, ExpectedRevision = beforeInvoice.Revision }]
        }, CancellationToken.None);
        var result = Assert.IsType<RecycleBinMutationResultDto>(Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(!revokeReceipt, Assert.Single(result.Results).Success);
        db.ChangeTracker.Clear();
        var restored = await db.Invoices.IgnoreQueryFilters().SingleAsync(row => row.Id == sale.Id);
        Assert.Equal(revokeReceipt, restored.IsDeleted);
        Assert.Equal(revokeReceipt ? 2m : 1m, await db.ItemWarehouseStocks.IgnoreQueryFilters()
            .Where(row => row.ItemId == itemId && row.WarehouseCode == OfficeCodeCatalog.YeonsuMainWarehouse)
            .Select(row => row.Quantity).SingleAsync());
        Assert.Equal(8m, await db.ItemWarehouseStocks.IgnoreQueryFilters()
            .Where(row => row.ItemId == itemId && row.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(row => row.Quantity).SingleAsync());
        var master = await db.Items.IgnoreQueryFilters().SingleAsync(row => row.Id == itemId);
        Assert.Equal(OfficeCodeCatalog.Usenet, master.OfficeCode);
        Assert.Equal(beforeItem.NameOriginal, master.NameOriginal);
        Assert.False(master.IsDeleted);
        if (revokeReceipt)
        {
            Assert.Equal(beforeInvoice.Revision, restored.Revision);
            Assert.Equal(beforeItem.Revision, master.Revision);
        }
    }
}
