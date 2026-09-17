using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed partial class InventoryTransferScopeGuardTests
{
    [Fact]
    public void ReceivedItemAccess_InventorySaveCommandTracksOriginalOwnerAndNewItem()
    {
        using var appRoot = new LocalAppRootScope("received-item-command");
        using var db = CreateDbContext(appRoot.DbPath);
        var session = CreateUserSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Yeonsu,
            TenantScopeCatalog.ScopeOfficeOnly, AppPermissionNames.ItemEdit);
        var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
        using var vm = new InventoryViewModel(local, session);
        var item = CreateStockItem(Guid.NewGuid(), "Sender owned"); item.OfficeCode = OfficeCodeCatalog.Usenet;
        var load = typeof(InventoryViewModel).GetMethod("LoadFormFromItem", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        load.Invoke(vm, [new InventoryItemRow(item, new Dictionary<string, decimal> { [OfficeCodeCatalog.Yeonsu] = 2m }, OfficeCodeCatalog.Yeonsu)]);
        Assert.False(vm.CanSaveItems);
        Assert.False(vm.SaveItemCommand.CanExecute(null));
        Assert.Equal(2m, vm.EditSelectedOfficeStock);
        vm.PrepareNewItemRegistration("Own new item");
        Assert.True(vm.CanSaveItems);
        Assert.True(vm.SaveItemCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("valid", false)]
    [InlineData("unsynced", true)]
    [InlineData("received", true)]
    [InlineData("foreign-route", true)]
    [InlineData("wrong-destination", true)]
    [InlineData("deleted-item", true)]
    [InlineData("no-snapshot", true)]
    public async Task ReceivedItemAccess_IntegrityDistinguishesPendingSnapshotFromBrokenReferences(string scenario, bool issueExpected)
    {
        using var appRoot = new LocalAppRootScope("received-item-integrity");
        await using var db = CreateDbContext(appRoot.DbPath);
        await db.Database.EnsureCreatedAsync();
        var itemId = Guid.NewGuid();
        if (scenario == "deleted-item")
        {
            var deleted = CreateStockItem(itemId, "Deleted"); deleted.IsDeleted = true;
            db.Items.Add(deleted);
        }
        db.InventoryTransfers.Add(new LocalInventoryTransfer
        {
            Revision = scenario == "unsynced" ? 0 : 100,
            FromWarehouseCode = scenario == "foreign-route" ? OfficeCodeCatalog.ItworldMainWarehouse : OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = scenario == "wrong-destination" ? OfficeCodeCatalog.UsenetMainWarehouse : OfficeCodeCatalog.YeonsuMainWarehouse,
            TransferStatus = scenario == "received" ? InventoryTransferStatusNormalizer.Received : InventoryTransferStatusNormalizer.Pending,
            Lines = [new LocalInventoryTransferLine { ItemId = itemId, ItemNameOriginal = scenario == "no-snapshot" ? "" : "Sender snapshot", Quantity = 2m }]
        });
        await db.SaveChangesAsync();
        var session = CreateUserSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Yeonsu, TenantScopeCatalog.ScopeOfficeOnly, AppPermissionNames.DeliveryEdit);
        var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
        var report = await service.BuildIntegrityReportAsync(session);
        Assert.Equal(issueExpected, report.Issues.Any(x => x.Code == "orphan_inventory_transfer_line_item_refs"));
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
    public async Task ReceivedItemAccess_AllowsDestinationSaleButNeverMasterEditing(string scenario, bool allowed)
    {
        using var appRoot = new LocalAppRootScope("received-item-access");
        await using var db = CreateDbContext(appRoot.DbPath);
        await db.Database.EnsureCreatedAsync();
        var item = CreateStockItem(Guid.NewGuid(), "Received original");
        item.OfficeCode = OfficeCodeCatalog.Usenet;
        item.TenantCode = scenario == "foreign-tenant" ? TenantScopeCatalog.Itworld : TenantScopeCatalog.UsenetGroup;
        item.CurrentStock = scenario == "sold-out" ? 0m : 2m;
        var unrelated = CreateStockItem(Guid.NewGuid(), "Unrelated original");
        unrelated.OfficeCode = OfficeCodeCatalog.Usenet;
        db.Items.AddRange(item, unrelated);
        var transfer = new LocalInventoryTransfer
        {
            Id = Guid.NewGuid(),
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = scenario == "wrong-warehouse" ? OfficeCodeCatalog.ItworldMainWarehouse : OfficeCodeCatalog.YeonsuMainWarehouse,
            TransferStatus = scenario == "pending" ? InventoryTransferStatusNormalizer.Pending : InventoryTransferStatusNormalizer.Received,
            ReceivedByUsername = scenario == "pending" ? "" : "yeonsu-receiver",
            IsDeleted = scenario == "deleted-transfer",
            Lines = [new LocalInventoryTransferLine { ItemId = item.Id, Quantity = 2m,
                ReceivedQuantity = scenario == "zero-received" ? 0m : 2m, IsDeleted = scenario == "deleted-line" }]
        };
        db.InventoryTransfers.Add(transfer);
        var customer = new LocalCustomer { Id = Guid.NewGuid(), NameOriginal = "Destination customer", NameMatchKey = "DESTINATION",
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Yeonsu, ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        var session = CreateUserSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Yeonsu,
            TenantScopeCatalog.ScopeOfficeOnly, AppPermissionNames.InvoiceEdit, AppPermissionNames.ItemEdit, AppPermissionNames.DeliveryEdit);
        var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
        var visible = await service.GetItemsAsync(session);
        Assert.Equal(allowed, visible.Any(x => x.Id == item.Id));
        Assert.DoesNotContain(visible, x => x.Id == unrelated.Id);
        Assert.False(service.CanWriteItemScope(item, session));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.UpsertItemAsync(item, session));
        var invoice = new LocalInvoice { Id = Guid.NewGuid(), CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup, ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            SourceWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse, VoucherType = VoucherType.Sales,
            Lines = [new LocalInvoiceLine { ItemId = item.Id, ItemNameOriginal = item.NameOriginal, ItemTrackingType = ItemTrackingTypes.Stock,
                Quantity = 1m, Unit = "EA", UnitPrice = 1000m, LineAmount = 1000m }] };
        var result = await service.SaveInvoiceAsync(invoice, new InvoiceSaveContext { Username = "yeonsu-delivery-user",
            Role = DomainConstants.RoleUser, OfficeCode = OfficeCodeCatalog.Yeonsu }, session);
        Assert.Equal(allowed, result.Success);
        if (!allowed) Assert.Contains("품목", result.Message);
        var original = await db.Items.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == item.Id);
        Assert.Equal("Received original", original.NameOriginal);
        Assert.Equal(OfficeCodeCatalog.Usenet, original.OfficeCode);
        Assert.False(original.IsDeleted);
        Assert.False(original.IsDirty);
    }
}
