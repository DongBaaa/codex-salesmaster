using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed partial class InventoryTransferScopeGuardTests
{
    [Theory]
    [InlineData("USENET", 7)]
    [InlineData("YEONSU", 3)]
    [InlineData("YEONSU", -2)]
    [InlineData("ITWORLD", 0)]
    public async Task SalesWarehouseStock_UsesSelectedWarehouseWithoutChangingMaster(string office, int quantity)
    {
        using var root = new LocalAppRootScope("sales-warehouse-stock");
        await using var db = CreateDbContext(root.DbPath);
        await db.Database.EnsureCreatedAsync();
        var tenant = office == "ITWORLD" ? TenantScopeCatalog.Itworld : TenantScopeCatalog.UsenetGroup;
        var item = CreateStockItem(Guid.NewGuid(), "Selected warehouse item");
        item.TenantCode = tenant;
        item.CurrentStock = 100m;
        db.Items.Add(item);
        foreach (var code in new[] { "USENET", "YEONSU", "ITWORLD" })
        {
            var location = new LocalOffice { Code = code, Name = code };
            db.Offices.Add(location);
            db.Warehouses.Add(new LocalWarehouse { OfficeId = location.Id, OfficeCode = code, Code = OfficeCodeCatalog.GetMainWarehouseCode(code), Name = code });
        }
        db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock { ItemId = item.Id, WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse, Quantity = office == "USENET" ? quantity : 7m });
        db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock { ItemId = item.Id, WarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse, Quantity = office == "YEONSU" ? quantity : 3m });
        await db.SaveChangesAsync();
        var session = CreateUserSession(tenant, office, TenantScopeCatalog.ScopeOfficeOnly, AppPermissionNames.InvoiceEdit);
        var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
        using var vm = new SalesViewModel(local, null!, null!, session, VoucherType.Sales);
        await vm.LoadAsync();
        Assert.Equal(OfficeCodeCatalog.GetMainWarehouseCode(office), vm.SelectedWarehouseCode);
        Assert.Equal(quantity, Assert.Single(vm.ItemSearchResults).CurrentStock);
        Assert.Equal(quantity, Assert.Single(vm.FindItemsForQuickInput("Selected")).CurrentStock);
        Assert.Equal(100m, (await db.Items.AsNoTracking().SingleAsync()).CurrentStock);
        Assert.False(db.ChangeTracker.HasChanges());
        if (office == "YEONSU")
        {
            var stock = await db.ItemWarehouseStocks.SingleAsync(s => s.WarehouseCode == OfficeCodeCatalog.YeonsuMainWarehouse);
            stock.Quantity = 2m;
            await db.SaveChangesAsync();
            await vm.ReloadItemsAsync();
            Assert.Equal(2m, Assert.Single(vm.ItemSearchResults).CurrentStock);
            Assert.Equal(100m, (await db.Items.AsNoTracking().SingleAsync()).CurrentStock);
        }
    }

    [Fact]
    public async Task SalesWarehouseStock_SwitchingOfficeAndInventoryRefreshKeepDraftAndMaster()
    {
        using var root = new LocalAppRootScope("sales-warehouse-switch");
        await using var db = CreateDbContext(root.DbPath);
        await db.Database.EnsureCreatedAsync();
        var item = CreateStockItem(Guid.NewGuid(), "Shared stock");
        item.CurrentStock = 10m;
        db.Items.Add(item);
        foreach (var (office, quantity) in new[] { ("USENET", 7m), ("YEONSU", 3m) })
        {
            var location = new LocalOffice { Code = office, Name = office };
            db.Offices.Add(location);
            db.Warehouses.Add(new LocalWarehouse { OfficeId = location.Id, OfficeCode = office, Code = OfficeCodeCatalog.GetMainWarehouseCode(office), Name = office });
            db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock { ItemId = item.Id, WarehouseCode = OfficeCodeCatalog.GetMainWarehouseCode(office), Quantity = quantity });
        }
        await db.SaveChangesAsync();
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto { Username = "stock-admin", Role = "Admin", TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = "USENET", ScopeType = TenantScopeCatalog.ScopeTenantAll });
        var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
        using var vm = new SalesViewModel(local, null!, null!, session, VoucherType.Sales);
        await vm.LoadAsync();
        Assert.Equal(7m, Assert.Single(vm.ItemSearchResults).CurrentStock);
        vm.ApplyInputItem(Assert.Single(vm.ItemSearchResults));
        vm.InputQty = 2m;
        vm.SelectedResponsibleOfficeCode = "YEONSU";
        Assert.Equal(3m, Assert.Single(vm.ItemSearchResults).CurrentStock);
        Assert.Equal(item.Id, vm.SelectedInputItem!.Id);
        Assert.Equal(2m, vm.InputQty);
        var stock = await db.ItemWarehouseStocks.SingleAsync(s => s.WarehouseCode == OfficeCodeCatalog.YeonsuMainWarehouse);
        stock.Quantity = 1m;
        await db.SaveChangesAsync();
        var refresh = typeof(SalesViewModel).GetMethod("RefreshItemsAfterInventoryChangedAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await (Task)refresh.Invoke(vm, [0])!;
        Assert.Equal(1m, Assert.Single(vm.ItemSearchResults).CurrentStock);
        Assert.Equal(2m, vm.InputQty);
        vm.SelectedResponsibleOfficeCode = "USENET";
        Assert.Equal(7m, Assert.Single(vm.ItemSearchResults).CurrentStock);
        Assert.Equal(10m, (await db.Items.AsNoTracking().SingleAsync()).CurrentStock);
        Assert.False(db.ChangeTracker.HasChanges());
    }
}
