using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed partial class InventoryTransferScopeGuardTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceivedItemAccess_ReceiptThenSaleSyncsStock_ButMasterMutationIsDenied(bool deleteMaster)
    {
        var itemId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        await SeedPendingTransferAsync(itemId, transferId, Guid.NewGuid(), "source-owned sale", 8m);
        await using (var seed = CreateDbContext(CreateAdminUser()))
        {
            var item = await seed.Items.SingleAsync(item => item.Id == itemId);
            item.OfficeCode = OfficeCodeCatalog.Usenet;
            seed.Customers.Add(new Customer { Id = customerId, TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Yeonsu, ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                NameOriginal = "destination customer", TradeType = "매출" });
            await seed.SaveChangesAsync();
        }
        var user = new TestCurrentUserContext { Username = "destination-sale", OfficeCode = OfficeCodeCatalog.Yeonsu,
            TenantCode = TenantScopeCatalog.UsenetGroup, ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = [PermissionNames.DeliveryEdit, PermissionNames.InvoiceEdit, PermissionNames.ItemEdit] };
        await using var db = CreateDbContext(user);
        var controller = CreateController(db, user);
        var before = Assert.IsType<SyncPullResponse>(Assert.IsType<OkObjectResult>((await controller.Pull(0, CancellationToken.None)).Result).Value);
        Assert.DoesNotContain(before.Items, item => item.Id == itemId);
        var transfer = await db.InventoryTransfers.IgnoreQueryFilters().Include(row => row.Lines).SingleAsync(row => row.Id == transferId);
        var receipt = await controller.Push(new SyncPushRequest { DeviceId = "receipt-and-sale",
            InventoryTransfers = [BuildReceiptDto(transfer, user.Username, 2m)] }, CancellationToken.None);
        Assert.Equal(0, Assert.IsType<SyncPushResult>(Assert.IsType<OkObjectResult>(receipt.Result).Value).ConflictCount);
        db.ChangeTracker.Clear();
        var incremental = Assert.IsType<SyncPullResponse>(Assert.IsType<OkObjectResult>((await controller.Pull(before.CurrentServerRevision, CancellationToken.None)).Result).Value);
        Assert.Contains(incremental.Items, item => item.Id == itemId && item.OfficeCode == OfficeCodeCatalog.Usenet);
        Assert.Contains(incremental.ItemWarehouseStocks, stock => stock.ItemId == itemId && stock.Quantity == 2m && stock.WarehouseCode == OfficeCodeCatalog.YeonsuMainWarehouse);

        var invoice = BuildInventoryInvoiceDto(Guid.NewGuid(), customerId, itemId, "source-owned sale", VoucherType.Sales, 1m, user.Username, DateTime.UtcNow);
        invoice.OfficeCode = invoice.ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu;
        invoice.SourceWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse;
        var sale = await controller.Push(new SyncPushRequest { DeviceId = "receipt-and-sale", Invoices = [invoice] }, CancellationToken.None);
        var saleResult = Assert.IsType<SyncPushResult>(Assert.IsType<OkObjectResult>(sale.Result).Value);
        Assert.Equal(0, saleResult.ConflictCount);
        Assert.Equal(1, saleResult.AcceptedCount);
        db.ChangeTracker.Clear();
        var after = Assert.IsType<SyncPullResponse>(Assert.IsType<OkObjectResult>((await controller.Pull(0, CancellationToken.None)).Result).Value);
        Assert.Equal(itemId, Assert.Single(Assert.Single(after.Invoices, row => row.Id == invoice.Id).Lines).ItemId);
        Assert.Contains(after.ItemWarehouseStocks, stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.YeonsuMainWarehouse && stock.Quantity == 1m);
        Assert.DoesNotContain(after.ItemWarehouseStocks, stock => stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse);

        // Startup maintenance must not turn a sender-owned item into a shared master
        // merely because the destination has received and sold its stock.
        await using (var maintenance = CreateDbContext(CreateAdminUser()))
        {
            var backfill = typeof(거래플랜.Server.Api.Data.DbInitializer).GetMethod(
                "BackfillItemScopeFieldsAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            await (Task)backfill.Invoke(null, [maintenance, CancellationToken.None])!;
        }
        db.ChangeTracker.Clear();
        Assert.Equal(OfficeCodeCatalog.Usenet, await db.Items.IgnoreQueryFilters()
            .Where(item => item.Id == itemId).Select(item => item.OfficeCode).SingleAsync());

        var storedItem = await db.Items.IgnoreQueryFilters().SingleAsync(item => item.Id == itemId);
        var forbidden = storedItem.ToDto();
        forbidden.ExpectedRevision = storedItem.Revision;
        forbidden.IsDeleted = deleteMaster;
        forbidden.NameOriginal = "unauthorized replacement";
        forbidden.MutationId = "forbidden-master-" + Guid.NewGuid().ToString("N");
        forbidden.MutationCreatedAtUtc = DateTime.UtcNow;
        var mutation = await controller.Push(new SyncPushRequest { DeviceId = "receipt-and-sale", Items = [forbidden] }, CancellationToken.None);
        var mutationResult = Assert.IsType<SyncPushResult>(Assert.IsType<OkObjectResult>(mutation.Result).Value);
        Assert.Equal(0, mutationResult.AcceptedCount);
        Assert.Equal(1, mutationResult.ConflictCount);
        db.ChangeTracker.Clear();
        storedItem = await db.Items.IgnoreQueryFilters().SingleAsync(item => item.Id == itemId);
        Assert.False(storedItem.IsDeleted);
        Assert.Equal("source-owned sale", storedItem.NameOriginal);
        Assert.Equal(OfficeCodeCatalog.Usenet, storedItem.OfficeCode);
    }

    [Theory]
    [InlineData("received", true)]
    [InlineData("sold-out", true)]
    [InlineData("pending", false)]
    [InlineData("zero-received", false)]
    [InlineData("deleted-transfer", false)]
    [InlineData("deleted-line", false)]
    [InlineData("foreign-tenant", false)]
    [InlineData("wrong-warehouse", false)]
    public async Task ReceivedItemAccess_GrantsOnlyDestinationRead_AndPreservesSourceOwnership(string scenario, bool expected)
    {
        var itemId = Guid.NewGuid();
        var unrelatedItemId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        await SeedPendingTransferAsync(itemId, transferId, Guid.NewGuid(), "received item access", 8m);
        await using (var seed = CreateDbContext(CreateAdminUser()))
        {
            var item = await seed.Items.IgnoreQueryFilters().SingleAsync(item => item.Id == itemId);
            item.OfficeCode = OfficeCodeCatalog.Usenet;
            if (scenario == "foreign-tenant") item.TenantCode = TenantScopeCatalog.Itworld;
            var unrelated = CreateStockItem(unrelatedItemId, "unrelated source-owned item", 5m);
            unrelated.OfficeCode = OfficeCodeCatalog.Usenet;
            seed.Items.Add(unrelated);
            var transfer = await seed.InventoryTransfers.IgnoreQueryFilters().Include(row => row.Lines)
                .SingleAsync(row => row.Id == transferId);
            transfer.TransferStatus = scenario == "pending" ? InventoryTransferStatusNormalizer.Pending : InventoryTransferStatusNormalizer.Received;
            transfer.ReceivedAtUtc = scenario == "pending" ? null : DateTime.UtcNow;
            transfer.ReceivedByUsername = scenario == "pending" ? "" : "yeonsu-receiver";
            transfer.IsDeleted = scenario == "deleted-transfer";
            if (scenario == "wrong-warehouse") transfer.FromWarehouseCode = OfficeCodeCatalog.ItworldMainWarehouse;
            var line = Assert.Single(transfer.Lines);
            line.IsDeleted = scenario == "deleted-line";
            line.ReceivedQuantity = scenario == "zero-received" ? 0m : 2m;
            seed.ItemWarehouseStocks.Add(new 거래플랜.Server.Api.Domain.ItemWarehouseStock
            {
                ItemId = itemId, WarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                Quantity = scenario == "sold-out" ? 0m : 2m, UpdatedAtUtc = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }
        var targetUser = CreateDeliveryUser("yeonsu-reader", OfficeCodeCatalog.Yeonsu);
        await using var db = CreateDbContext(targetUser);
        var scope = new OfficeScopeService(targetUser, db);
        var visible = await scope.ApplyItemScope(db.Items.AsNoTracking()).Select(item => item.Id).ToListAsync();
        Assert.Equal(expected, visible.Contains(itemId));
        Assert.DoesNotContain(unrelatedItemId, visible);
        var stocks = await scope.ApplyItemWarehouseStockScope(db.ItemWarehouseStocks.AsNoTracking()).ToListAsync();
        Assert.Equal(expected, stocks.Any(stock => stock.ItemId == itemId));
        Assert.DoesNotContain(stocks, stock => stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse);
        Assert.False(scope.CanWriteOfficeForItems(OfficeCodeCatalog.Usenet, TenantScopeCatalog.UsenetGroup));
        Assert.Equal(OfficeCodeCatalog.Usenet, await db.Items.IgnoreQueryFilters().Where(item => item.Id == itemId)
            .Select(item => item.OfficeCode).SingleAsync());
    }
}
