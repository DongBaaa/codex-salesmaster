using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Utilities;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed partial class InventoryTransferScopeGuardTests
{
    [Theory]
    [InlineData("USENET", "USENET_GROUP", false, true)]
    [InlineData("ITWORLD", "ITWORLD", false, false)]
    [InlineData("USENET", "ITWORLD", false, false)]
    [InlineData("USENET", "USENET_GROUP", true, false)]
    public async Task Pull_TargetReadsOnlyValidSourceOwnedTransferLines_WithoutItemMasterAccess(
        string itemOffice, string itemTenant, bool deleted, bool expectedVisible)
    {
        var itemId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        await SeedPendingTransferAsync(itemId, transferId, lineId, "source-owned receipt", 8m);
        await using (var seed = CreateDbContext(CreateAdminUser()))
        {
            await seed.Items.IgnoreQueryFilters().Where(item => item.Id == itemId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.OfficeCode, itemOffice)
                    .SetProperty(item => item.TenantCode, itemTenant)
                    .SetProperty(item => item.IsDeleted, deleted));
        }

        var targetUser = CreateDeliveryUser("target-source-owned-reader", OfficeCodeCatalog.Yeonsu);
        await using var db = CreateDbContext(targetUser);
        var response = await CreateController(db, targetUser).Pull(0, CancellationToken.None);
        var result = Assert.IsType<SyncPullResponse>(Assert.IsType<OkObjectResult>(response.Result).Value);
        var transfer = Assert.Single(result.InventoryTransfers, row => row.Id == transferId);
        Assert.Equal(expectedVisible, transfer.Lines.Any(line => line.Id == lineId && line.ItemId == itemId));
        Assert.DoesNotContain(result.Items, item => item.Id == itemId);
    }

    [Theory]
    [InlineData(false, "USENET_GROUP")]
    [InlineData(true, "USENET_GROUP")]
    [InlineData(false, "ITWORLD")]
    public async Task Push_TargetReceiptForSourceOwnedItem_RequiresUnchangedStoredLine(bool replaceItem, string itemTenant)
    {
        var expectedAccepted = !replaceItem && itemTenant == TenantScopeCatalog.UsenetGroup;
        var itemId = Guid.NewGuid();
        var replacementItemId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        await SeedPendingTransferAsync(itemId, transferId, lineId, "source-owned receipt", 8m);
        await using (var seed = CreateDbContext(CreateAdminUser()))
        {
            await seed.Items.Where(item => item.Id == itemId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.OfficeCode, OfficeCodeCatalog.Usenet)
                    .SetProperty(item => item.TenantCode, itemTenant));
            var replacement = CreateStockItem(replacementItemId, "other source item", 0m);
            replacement.OfficeCode = OfficeCodeCatalog.Usenet;
            seed.Items.Add(replacement);
            await seed.SaveChangesAsync();
        }
        var targetUser = CreateDeliveryUser("target-source-owned-receiver", OfficeCodeCatalog.Yeonsu);
        await using var db = CreateDbContext(targetUser);
        var existing = await db.InventoryTransfers.IgnoreQueryFilters().Include(row => row.Lines)
            .SingleAsync(row => row.Id == transferId);
        var receipt = BuildReceiptDto(existing, targetUser.Username, 2m);
        if (replaceItem)
            receipt.Lines.Single().ItemId = replacementItemId;
        var response = await CreateController(db, targetUser).Push(new SyncPushRequest
        {
            DeviceId = "source-owned-receipt",
            InventoryTransfers = [receipt]
        }, CancellationToken.None);
        var result = Assert.IsType<SyncPushResult>(Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(expectedAccepted ? 0 : 1, result.ConflictCount);
        Assert.Equal(expectedAccepted ? 1 : 0, result.AcceptedCount);
        db.ChangeTracker.Clear();
        var stored = await db.InventoryTransfers.IgnoreQueryFilters().Include(row => row.Lines)
            .SingleAsync(row => row.Id == transferId);
        Assert.Equal(expectedAccepted ? InventoryTransferStatusNormalizer.Received : InventoryTransferStatusNormalizer.Pending,
            stored.TransferStatus);
        Assert.Equal(itemId, Assert.Single(stored.Lines).ItemId);
        Assert.Equal(8m, await db.ItemWarehouseStocks.Where(stock => stock.ItemId == itemId &&
            stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse).Select(stock => stock.Quantity).SingleAsync());
        var destinationQuantities = await db.ItemWarehouseStocks.Where(stock => stock.ItemId == itemId &&
            stock.WarehouseCode == OfficeCodeCatalog.YeonsuMainWarehouse).Select(stock => stock.Quantity).ToListAsync();
        Assert.Equal(expectedAccepted ? 2m : 0m, destinationQuantities.Sum());
    }
}
