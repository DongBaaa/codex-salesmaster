using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed partial class InventoryTransferScopeGuardTests
{
    [Theory]
    [InlineData("restore-item")]
    [InlineData("purge-item")]
    [InlineData("restore-transfer")]
    [InlineData("server-purge-item")]
    [InlineData("server-purge-transfer")]
    [InlineData("server-purge-invoice")]
    public async Task RecyclePartialStock_ChangesOnlyTargetAndPreservesUnrelatedBaseline(string action)
    {
        using var appRoot = new LocalAppRootScope("recycle-partial-stock");
        await using var db = CreateDbContext(appRoot.DbPath);
        await db.Database.EnsureCreatedAsync();
        var item = CreateStockItem(Guid.NewGuid(), "Target item"); item.OfficeCode = OfficeCodeCatalog.Usenet;
        item.IsDeleted = action.EndsWith("item", StringComparison.Ordinal);
        var unrelated = CreateStockItem(Guid.NewGuid(), "Unrelated stock"); unrelated.OfficeCode = OfficeCodeCatalog.Usenet;
        unrelated.CurrentStock = 17m;
        db.Items.AddRange(item, unrelated);
        db.ItemWarehouseStocks.AddRange(new LocalItemWarehouseStock { ItemId = item.Id, WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse, Quantity = 10m, Revision = 77 },
            new LocalItemWarehouseStock { ItemId = unrelated.Id, WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse, Quantity = 17m, Revision = 88 });
        var targetId = item.Id;
        if (action.EndsWith("transfer", StringComparison.Ordinal))
        {
            var transfer = new LocalInventoryTransfer { Id = Guid.NewGuid(), IsDeleted = true, IsDirty = false,
                FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse, ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                Lines = [new LocalInventoryTransferLine { ItemId = item.Id, ItemNameOriginal = item.NameOriginal, Quantity = 3m }] };
            db.InventoryTransfers.Add(transfer); targetId = transfer.Id;
        }
        if (action == "server-purge-invoice")
        {
            var customer = new LocalCustomer { Id = Guid.NewGuid(), NameOriginal = "Synthetic customer", NameMatchKey = "SYNTHETIC",
                TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet };
            db.Customers.Add(customer);
            var invoice = new LocalInvoice { CustomerId = customer.Id, IsDeleted = true, IsDirty = false, IsLatestVersion = false,
                TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse, VoucherType = VoucherType.Sales, IsConfirmed = true,
                Lines = [new LocalInvoiceLine { ItemId = item.Id, ItemNameOriginal = item.NameOriginal, Quantity = 2m, UnitPrice = 1000m, LineAmount = 2000m }] };
            db.Invoices.Add(invoice); targetId = invoice.Id;
        }
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var session = CreateUserSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet, TenantScopeCatalog.ScopeOfficeOnly,
            AppPermissionNames.ItemEdit, AppPermissionNames.DeliveryEdit, AppPermissionNames.InvoiceEdit);
        var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
        var result = action switch
        {
            "restore-item" => await service.RestoreItemAsync(targetId, session),
            "purge-item" => await service.PermanentlyDeleteItemAsync(targetId, session),
            "restore-transfer" => await service.RestoreRecycleBinEntryAsync(RecycleBinEntityKind.InventoryTransfer, targetId, session),
            "server-purge-item" => await service.ApplyServerPurgeRecycleBinEntryAsync(RecycleBinEntityKind.Item, targetId),
            "server-purge-transfer" => await service.ApplyServerPurgeRecycleBinEntryAsync(RecycleBinEntityKind.InventoryTransfer, targetId),
            _ => await service.ApplyServerPurgeRecycleBinEntryAsync(RecycleBinEntityKind.Invoice, targetId)
        };
        Assert.True(result.Success, result.Message);
        var untouched = await db.ItemWarehouseStocks.AsNoTracking().SingleAsync(x => x.ItemId == unrelated.Id);
        Assert.Equal(17m, untouched.Quantity); Assert.Equal(88, untouched.Revision);
        var untouchedMaster = await db.Items.AsNoTracking().SingleAsync(x => x.Id == unrelated.Id);
        Assert.Equal(17m, untouchedMaster.CurrentStock); Assert.False(untouchedMaster.IsDirty);
        if (action is "purge-item" or "server-purge-item")
            Assert.False(await db.ItemWarehouseStocks.AnyAsync(x => x.ItemId == item.Id));
        else
        {
            var stock = await db.ItemWarehouseStocks.AsNoTracking().SingleAsync(x => x.ItemId == item.Id);
            Assert.Equal(action == "restore-transfer" ? 7m : 10m, stock.Quantity);
            Assert.Equal(77, stock.Revision);
        }
    }
}
