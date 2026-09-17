using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class ReceivedItemStartupOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupBackfill_PreservesExplicitOwnerWithDestinationStock(bool destinationOnly)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var item = new LocalItem { Id = Guid.NewGuid(), OfficeCode = OfficeCodeCatalog.Usenet,
            TenantCode = TenantScopeCatalog.UsenetGroup, NameOriginal = "Sender original", NameMatchKey = "SENDERORIGINAL",
            Revision = 77, IsDirty = false };
        db.Items.Add(item);
        db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock { ItemId = item.Id,
            WarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse, Quantity = 2m });
        if (!destinationOnly)
            db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock { ItemId = item.Id,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse, Quantity = 7m });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var backfill = typeof(LocalDbInitializer).GetMethod("BackfillItemScopeFieldsAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        await (Task)backfill.Invoke(null, [db])!;
        db.ChangeTracker.Clear();
        var saved = await db.Items.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(OfficeCodeCatalog.Usenet, saved.OfficeCode);
        Assert.Equal(TenantScopeCatalog.UsenetGroup, saved.TenantCode);
        Assert.Equal(77, saved.Revision);
        Assert.False(saved.IsDirty);
        Assert.Equal(2m, await db.ItemWarehouseStocks.Where(x => x.WarehouseCode == OfficeCodeCatalog.YeonsuMainWarehouse).Select(x => x.Quantity).SingleAsync());
    }
}
