using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed partial class InventoryTransferScopeGuardTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ItemDeleteRestore_RetainsHiddenWarehouseBaselineThroughStartup_AndPurgesIt(bool directDelete)
    {
        var user = CreateAdminUser();
        await using var db = CreateDbContext(user);
        var item = CreateStockItem(Guid.NewGuid(), "no-history stock restore", 5m);
        item.OfficeCode = OfficeCodeCatalog.Usenet;
        db.Items.Add(item);
        db.ItemWarehouseStocks.Add(new ItemWarehouseStock { ItemId = item.Id,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse, Quantity = 5m });
        await db.SaveChangesAsync();
        var before = await db.ItemWarehouseStocks.AsNoTracking().Where(row => row.ItemId == item.Id)
            .Select(row => new { row.Quantity, row.Revision, row.UpdatedAtUtc }).SingleAsync();
        async Task DeleteAsync()
        {
            db.ChangeTracker.Clear();
            var current = await db.Items.IgnoreQueryFilters().SingleAsync(row => row.Id == item.Id);
            if (directDelete)
                Assert.IsType<NoContentResult>(await new ItemsController(db, new OfficeScopeService(user, db))
                    .Delete(current.Id, current.Revision, CancellationToken.None));
            else
            {
                var dto = current.ToDto();
                dto.IsDeleted = true;
                dto.ExpectedRevision = current.Revision;
                dto.MutationId = Guid.NewGuid().ToString("N");
                dto.MutationCreatedAtUtc = dto.UpdatedAtUtc = DateTime.UtcNow;
                var response = await CreateController(db, user).Push(new SyncPushRequest {
                    DeviceId = "item-delete-restore", Items = [dto]
                }, CancellationToken.None);
                Assert.Equal(1, Assert.IsType<SyncPushResult>(Assert.IsType<OkObjectResult>(response.Result).Value).AcceptedCount);
            }
            db.ChangeTracker.Clear();
        }
        await DeleteAsync();
        Assert.False(await db.ItemWarehouseStocks.AnyAsync(row => row.ItemId == item.Id));
        var deleted = await db.Items.IgnoreQueryFilters().SingleAsync(row => row.Id == item.Id);
        Assert.True(deleted.IsDeleted);
        var retained = await db.ItemWarehouseStocks.IgnoreQueryFilters().AsNoTracking().Where(row => row.ItemId == item.Id)
            .Select(row => new { row.Quantity, row.Revision, row.UpdatedAtUtc }).SingleOrDefaultAsync();
        Assert.Equal(before, retained);
        Assert.Equal(0m, deleted.CurrentStock);
        var maintenance = typeof(DbInitializer).GetMethod("PurgeDeletedItemWarehouseStocksAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        await (Task)maintenance.Invoke(null, [db, CancellationToken.None])!;
        await db.SaveChangesAsync();
        var restore = await CreateRecycleBinController(db, user).Restore(new RecycleBinMutationRequest {
            Items = [new RecycleBinMutationTargetDto { Kind = "item", EntityId = item.Id, ExpectedRevision = deleted.Revision }]
        }, CancellationToken.None);
        Assert.True(Assert.Single(Assert.IsType<RecycleBinMutationResultDto>(Assert.IsType<OkObjectResult>(restore.Result).Value).Results).Success);
        db.ChangeTracker.Clear();
        Assert.Equal(5m, await db.Items.Where(row => row.Id == item.Id).Select(row => row.CurrentStock).SingleAsync());
        Assert.Equal(before, await db.ItemWarehouseStocks.AsNoTracking().Where(row => row.ItemId == item.Id)
            .Select(row => new { row.Quantity, row.Revision, row.UpdatedAtUtc }).SingleAsync());
        await DeleteAsync();
        var revision = await db.Items.IgnoreQueryFilters().Where(row => row.Id == item.Id).Select(row => row.Revision).SingleAsync();
        var purge = await CreateRecycleBinController(db, user).Purge(new RecycleBinMutationRequest {
            Items = [new RecycleBinMutationTargetDto { Kind = "item", EntityId = item.Id, ExpectedRevision = revision }]
        }, CancellationToken.None);
        Assert.True(Assert.Single(Assert.IsType<RecycleBinMutationResultDto>(Assert.IsType<OkObjectResult>(purge.Result).Value).Results).Success);
        Assert.False(await db.ItemWarehouseStocks.IgnoreQueryFilters().AnyAsync(row => row.ItemId == item.Id));
    }
}
