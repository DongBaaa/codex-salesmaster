using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed partial class InventoryTransferScopeGuardTests
{
    [Theory]
    [InlineData("sender-owned", false)]
    [InlineData("missing", true)]
    [InlineData("deleted", true)]
    [InlineData("cross-tenant", true)]
    [InlineData("outside-office", false)]
    public async Task IntegrityScope_ReceivedStockAndRentalReferencesDoNotRequireItemOwnership(string scenario, bool expected)
    {
        using var root = new LocalAppRootScope("received-reference-integrity");
        await using var db = CreateDbContext(root.DbPath);
        await db.Database.EnsureCreatedAsync();
        var item = CreateStockItem(Guid.NewGuid(), "Sender-owned reference");
        item.OfficeCode = OfficeCodeCatalog.Usenet;
        item.CurrentStock = 2m;
        item.IsDirty = true;
        item.IsDeleted = scenario == "deleted";
        if (scenario == "cross-tenant")
        {
            item.TenantCode = TenantScopeCatalog.Itworld;
            item.OfficeCode = OfficeCodeCatalog.Itworld;
        }
        if (scenario is not ("missing" or "outside-office")) db.Items.Add(item);
        var office = scenario == "outside-office" ? OfficeCodeCatalog.Usenet : OfficeCodeCatalog.Yeonsu;
        var warehouse = OfficeCodeCatalog.GetMainWarehouseCode(office);
        db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock { ItemId = item.Id, WarehouseCode = warehouse, Quantity = 2m });
        db.StockLayers.Add(new LocalStockLayer { ItemId = item.Id, WarehouseCode = warehouse, OriginalQuantity = 2m, RemainingQuantity = 2m });
        db.InventoryMovements.Add(new LocalInventoryMovement { ItemId = item.Id, WarehouseCode = warehouse, QuantityDelta = 2m });
        db.SerialLedgers.Add(new LocalSerialLedger { ItemId = item.Id, WarehouseCode = warehouse, SerialNumber = "REFERENCE-1" });
        db.RentalAssets.Add(new LocalRentalAsset { ItemId = item.Id, AssetKey = "REFERENCE-1",
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = office,
            ResponsibleOfficeCode = office, ManagementCompanyCode = office });
        await db.SaveChangesAsync();
        var before = db.ChangeTracker.Entries().ToDictionary(e => e.Entity,
            e => JsonSerializer.Serialize(e.CurrentValues.Properties.ToDictionary(p => p.Name, p => e.CurrentValues[p])));
        var session = CreateUserSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Yeonsu,
            TenantScopeCatalog.ScopeOfficeOnly, AppPermissionNames.DeliveryEdit);
        var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
        var report = await local.BuildIntegrityReportAsync(session);
        foreach (var code in new[] { "orphan_item_warehouse_stock_refs", "orphan_stock_layer_item_refs",
            "orphan_inventory_movement_item_refs", "orphan_serial_ledger_item_refs", "orphan_rental_asset_item_refs" })
        {
            Assert.Equal(expected, report.Issues.Any(x => x.Code == code));
            if (expected) Assert.Equal(1, Assert.Single(report.Issues, x => x.Code == code).Count);
        }
        foreach (var entry in db.ChangeTracker.Entries())
            Assert.Equal(before[entry.Entity], JsonSerializer.Serialize(entry.CurrentValues.Properties.ToDictionary(p => p.Name, p => entry.CurrentValues[p])));
        Assert.Empty(await db.SyncOutboxEntries.ToListAsync());
    }

    [Theory]
    [InlineData("other-tenant", false)]
    [InlineData("other-tenant-missing-item", false)]
    [InlineData("deleted-transfer", false)]
    [InlineData("deleted-line", false)]
    [InlineData("sender-owned-item", false)]
    [InlineData("missing-item", true)]
    [InlineData("deleted-item", true)]
    [InlineData("cross-tenant-item", true)]
    public async Task IntegrityScope_TransferReferencesUseActiveDocumentRoute(string scenario, bool expected)
    {
        using var root = new LocalAppRootScope("transfer-integrity-scope");
        await using var db = CreateDbContext(root.DbPath);
        await db.Database.EnsureCreatedAsync();
        var item = CreateStockItem(Guid.NewGuid(), "Transfer reference");
        item.CurrentStock = 0m;
        item.OfficeCode = OfficeCodeCatalog.Usenet;
        item.IsDeleted = scenario == "deleted-item";
        if (scenario == "cross-tenant-item")
        {
            item.TenantCode = TenantScopeCatalog.Itworld;
            item.OfficeCode = OfficeCodeCatalog.Itworld;
        }
        if (scenario is not ("missing-item" or "other-tenant-missing-item"))
            db.Items.Add(item);
        var transfer = new LocalInventoryTransfer
        {
            Revision = 100,
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            TransferStatus = InventoryTransferStatusNormalizer.Received,
            IsDeleted = scenario == "deleted-transfer",
            Lines = [new LocalInventoryTransferLine
            {
                ItemId = item.Id, ItemNameOriginal = item.NameOriginal, Quantity = 2m,
                ReceivedQuantity = 2m, IsDeleted = scenario == "deleted-line"
            }]
        };
        db.InventoryTransfers.Add(transfer);
        await db.SaveChangesAsync();
        var foreign = scenario.StartsWith("other-tenant", StringComparison.Ordinal);
        var session = CreateUserSession(foreign ? TenantScopeCatalog.Itworld : TenantScopeCatalog.UsenetGroup,
            foreign ? OfficeCodeCatalog.Itworld : OfficeCodeCatalog.Yeonsu,
            TenantScopeCatalog.ScopeOfficeOnly, AppPermissionNames.DeliveryEdit);
        string Snapshot() => JsonSerializer.Serialize(new
        {
            Header = db.Entry(transfer).CurrentValues.Properties.ToDictionary(p => p.Name, p => db.Entry(transfer).CurrentValues[p]),
            Lines = transfer.Lines.Select(line => db.Entry(line).CurrentValues.Properties.ToDictionary(p => p.Name, p => db.Entry(line).CurrentValues[p]))
        });
        var before = Snapshot();
        var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
        var report = await service.BuildIntegrityReportAsync(session);
        Assert.Equal(expected, report.Issues.Any(x => x.Code == "orphan_inventory_transfer_line_item_refs"));
        Assert.Equal(before, Snapshot());
        Assert.Empty(await db.SyncOutboxEntries.ToListAsync());
    }

    [Fact]
    public async Task IntegrityScope_PreservedForeignCacheDoesNotRequestRepairOrLosePendingValues()
    {
        using var root = new LocalAppRootScope("preserved-cache-integrity");
        await using var db = CreateDbContext(root.DbPath);
        await db.Database.EnsureCreatedAsync();
        var customer = new LocalCustomer { NameOriginal = "Foreign customer", NameMatchKey = "FOREIGN",
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet };
        var item = CreateStockItem(Guid.NewGuid(), "Foreign pending item");
        item.CurrentStock = 0m;
        item.IsDirty = true;
        db.Customers.Add(customer);
        db.Items.Add(item);
        db.Invoices.Add(new LocalInvoice { CustomerId = customer.Id, TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet, IsLatestVersion = true });
        db.Transactions.Add(new LocalTransaction { CustomerId = customer.Id, TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet });
        await db.SaveChangesAsync();
        var session = CreateUserSession(TenantScopeCatalog.Itworld, OfficeCodeCatalog.Itworld,
            TenantScopeCatalog.ScopeOfficeOnly, AppPermissionNames.InvoiceEdit);
        var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
        var report = await local.BuildIntegrityReportAsync(session);
        foreach (var code in new[] { "out_of_scope_customers", "out_of_scope_items", "out_of_scope_invoices", "out_of_scope_transactions" })
        {
            var issue = Assert.Single(report.Issues, x => x.Code == code);
            Assert.Equal("Info", issue.Severity);
            Assert.Equal(1, issue.Count);
            Assert.Contains("보존", issue.SuggestedAction);
        }
        Assert.False(report.RequiresFullMirrorRefresh);
        Assert.Equal(0, report.RoutineRepairCandidateIssueTypeCount);
        Assert.Equal(0, report.ManualReviewIssueTypeCount);
        Assert.True((await db.Items.AsNoTracking().SingleAsync()).IsDirty);
        Assert.Empty(await db.SyncOutboxEntries.ToListAsync());
        Assert.Empty(await local.GetInvoicesAsync(null, null, null, session));
    }

    [Fact]
    public void IntegrityScope_InformationalCacheDoesNotMaskRefreshMarkerOrRealErrors()
    {
        var info = new LocalIntegrityIssue("out_of_scope_items", "Info", 3, "Preserved cache");
        var error = new LocalIntegrityIssue("orphan_inventory_transfer_line_item_refs", "Error", 1, "Missing item");
        var infoOnly = new LocalIntegrityReport(DateTime.UtcNow, "ITWORLD", "ITWORLD", 0, false, [info]);
        Assert.False(infoOnly.RequiresFullMirrorRefresh);
        Assert.Equal(0, infoOnly.ManualReviewIssueTypeCount);
        var pending = new LocalIntegrityReport(DateTime.UtcNow, "ITWORLD", "ITWORLD", 0, true, [info]);
        Assert.True(pending.RequiresFullMirrorRefresh);
        Assert.Equal(1, pending.RoutineRepairCandidateIssueTypeCount);
        var broken = new LocalIntegrityReport(DateTime.UtcNow, "ITWORLD", "ITWORLD", 0, false, [info, error]);
        Assert.True(broken.RequiresFullMirrorRefresh);
        Assert.Equal(1, broken.ManualReviewIssueTypeCount);
    }
}
