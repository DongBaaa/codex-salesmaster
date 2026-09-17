using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed partial class InventoryTransferScopeGuardTests
{
    [Theory]
    [InlineData("create")]
    [InlineData("revise")]
    [InlineData("delete")]
    public async Task InvoicePartialStock_StockFailureRollsBackInvoiceVersionsAndAudit(string operation)
    {
        using var appRoot = new LocalAppRootScope("invoice-stock-rollback");
        await using var db = CreateDbContext(appRoot.DbPath);
        await db.Database.EnsureCreatedAsync();
        var item = CreateStockItem(Guid.NewGuid(), "Owned stock"); item.OfficeCode = OfficeCodeCatalog.Usenet;
        var customer = new LocalCustomer { Id = Guid.NewGuid(), NameOriginal = "Own customer", NameMatchKey = "OWN",
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet };
        db.Items.Add(item); db.Customers.Add(customer);
        db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock { ItemId = item.Id,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse, Quantity = operation == "create" ? 10m : 9m, Revision = 77 });
        var invoice = new LocalInvoice { Id = Guid.NewGuid(), CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse, VoucherType = VoucherType.Sales, IsConfirmed = true, IsLatestVersion = true,
            Lines = [new LocalInvoiceLine { ItemId = item.Id, ItemNameOriginal = item.NameOriginal, ItemTrackingType = ItemTrackingTypes.Stock,
                Quantity = 1m, UnitPrice = 1000m, LineAmount = 1000m }] };
        if (operation != "create") db.Invoices.Add(invoice);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var before = await Snapshot();
        await db.Database.ExecuteSqlRawAsync("CREATE TEMP TRIGGER fail_invoice_stock BEFORE UPDATE ON ItemWarehouseStocks BEGIN SELECT RAISE(ABORT, 'test invoice stock failure'); END;");
        var session = CreateUserSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet,
            TenantScopeCatalog.ScopeOfficeOnly, AppPermissionNames.InvoiceEdit);
        var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
        invoice.Lines.Single().Quantity = 2m; invoice.Lines.Single().LineAmount = 2000m;
        var exception = await Assert.ThrowsAsync<DbUpdateException>(async () =>
        {
            if (operation == "delete") await service.DeleteInvoiceAsync(invoice.Id, session);
            else await service.SaveInvoiceAsync(invoice, new InvoiceSaveContext { Username = "owner", Role = DomainConstants.RoleUser,
                OfficeCode = OfficeCodeCatalog.Usenet }, session);
        });
        Assert.Contains("test invoice stock failure", exception.ToString());
        Assert.Equal(before, await Snapshot());

        async Task<string> Snapshot() => System.Text.Json.JsonSerializer.Serialize(new
        {
            Invoices = await db.Invoices.IgnoreQueryFilters().AsNoTracking().OrderBy(x => x.Id)
                .Select(x => new { x.Id, x.IsLatestVersion, x.IsDeleted, x.IsDirty, x.VersionGroupId }).ToListAsync(),
            Lines = await db.InvoiceLines.AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.InvoiceId, x.Quantity }).ToListAsync(),
            Stock = await db.ItemWarehouseStocks.AsNoTracking().Select(x => new { x.Quantity, x.Revision }).ToListAsync(),
            AuditCount = await db.AuditLogs.CountAsync()
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task InvoicePartialStock_ReceivedStockSurvivesSaleRevisionAndDeletion(bool systemDelete, bool beforeReset)
    {
        using var appRoot = new LocalAppRootScope("invoice-partial-stock");
        await using var db = CreateDbContext(appRoot.DbPath);
        await db.Database.EnsureCreatedAsync();
        var item = CreateStockItem(Guid.NewGuid(), "Received original");
        item.OfficeCode = OfficeCodeCatalog.Usenet;
        db.Items.Add(item);
        db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock { ItemId = item.Id,
            WarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse, Quantity = 10m, Revision = 77 });
        db.InventoryTransfers.Add(new LocalInventoryTransfer { FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse, TransferStatus = InventoryTransferStatusNormalizer.Received,
            ReceivedByUsername = "receiver", Revision = 50,
            Lines = [new LocalInventoryTransferLine { ItemId = item.Id, ItemNameOriginal = item.NameOriginal, Quantity = 2m, ReceivedQuantity = 2m }] });
        var customer = new LocalCustomer { Id = Guid.NewGuid(), NameOriginal = "Destination customer", NameMatchKey = "DESTINATION",
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Yeonsu, ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu };
        db.Customers.Add(customer);
        if (beforeReset)
            db.InventoryMovements.Add(new LocalInventoryMovement { ItemId = item.Id,
                WarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse, MovementType = "StockResetToZero",
                OccurredDate = DateOnly.FromDateTime(DateTime.Today), IsActive = true });
        await db.SaveChangesAsync();
        var session = CreateUserSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Yeonsu,
            TenantScopeCatalog.ScopeOfficeOnly, AppPermissionNames.InvoiceEdit);
        var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
        var invoice = new LocalInvoice { CustomerId = customer.Id, OfficeCode = OfficeCodeCatalog.Yeonsu,
            InvoiceDate = DateOnly.FromDateTime(beforeReset ? DateTime.Today.AddDays(-1) : DateTime.Today),
            TenantCode = TenantScopeCatalog.UsenetGroup, ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            SourceWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse, VoucherType = VoucherType.Sales,
            Lines = [new LocalInvoiceLine { ItemId = item.Id, ItemNameOriginal = item.NameOriginal, ItemTrackingType = ItemTrackingTypes.Stock,
                Quantity = 2m, Unit = "EA", UnitPrice = 1000m, LineAmount = 2000m }] };
        var context = new InvoiceSaveContext { Username = "receiver", Role = DomainConstants.RoleUser, OfficeCode = OfficeCodeCatalog.Yeonsu };
        var saved = await service.SaveInvoiceAsync(invoice, context, session);
        Assert.True(saved.Success, saved.Message);
        Assert.Equal(beforeReset ? 10m : 8m, await db.ItemWarehouseStocks.Where(x => x.WarehouseCode == OfficeCodeCatalog.YeonsuMainWarehouse).Select(x => x.Quantity).SingleAsync());
        invoice = (await service.GetInvoiceAsync(saved.SavedInvoiceId))!;
        invoice.Lines.Single().Quantity = 3m;
        invoice.Lines.Single().LineAmount = 3000m;
        saved = await service.SaveInvoiceAsync(invoice, context, session);
        Assert.True(saved.Success, saved.Message);
        Assert.Equal(beforeReset ? 10m : 7m, await db.ItemWarehouseStocks.Where(x => x.WarehouseCode == OfficeCodeCatalog.YeonsuMainWarehouse).Select(x => x.Quantity).SingleAsync());
        if (systemDelete) await service.DeleteInvoiceAsync(saved.SavedInvoiceId);
        else
        {
            var deleted = await service.DeleteInvoiceAsync(saved.SavedInvoiceId, session);
            Assert.True(deleted.Success, deleted.Message);
        }
        var stock = await db.ItemWarehouseStocks.AsNoTracking().SingleAsync();
        Assert.Equal(OfficeCodeCatalog.YeonsuMainWarehouse, stock.WarehouseCode);
        Assert.Equal(10m, stock.Quantity);
        Assert.Equal(77, stock.Revision);
        var restored = await service.RestoreInvoiceAsync(saved.SavedInvoiceId, session);
        Assert.True(restored.Success, restored.Message);
        Assert.Equal(beforeReset ? 10m : 7m, await db.ItemWarehouseStocks.Where(x => x.WarehouseCode == OfficeCodeCatalog.YeonsuMainWarehouse).Select(x => x.Quantity).SingleAsync());
        var repeatedRestore = await service.RestoreInvoiceAsync(saved.SavedInvoiceId, session);
        Assert.True(repeatedRestore.Success, repeatedRestore.Message);
        Assert.Equal(beforeReset ? 10m : 7m, await db.ItemWarehouseStocks.Where(x => x.WarehouseCode == OfficeCodeCatalog.YeonsuMainWarehouse).Select(x => x.Quantity).SingleAsync());
        var deletedAgain = await service.DeleteInvoiceAsync(saved.SavedInvoiceId, session);
        Assert.True(deletedAgain.Success, deletedAgain.Message);
        var purged = await service.PermanentlyDeleteInvoiceAsync(saved.SavedInvoiceId, session);
        Assert.True(purged.Success, purged.Message);
        stock = await db.ItemWarehouseStocks.AsNoTracking().SingleAsync();
        Assert.Equal(10m, stock.Quantity);
        Assert.Equal(77, stock.Revision);
        Assert.Empty(await db.Invoices.IgnoreQueryFilters().ToListAsync());
        var original = await db.Items.AsNoTracking().SingleAsync();
        Assert.Equal(OfficeCodeCatalog.Usenet, original.OfficeCode);
        Assert.False(original.IsDirty);
    }
}
