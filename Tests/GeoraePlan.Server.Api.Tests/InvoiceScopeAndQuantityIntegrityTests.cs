using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class InvoiceScopeAndQuantityIntegrityTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public InvoiceScopeAndQuantityIntegrityTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using var dbContext = CreateDbContext(CreateAdminUser());
        dbContext.Database.EnsureCreated();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectInvoiceCreateOrUpdate_RejectsCrossOfficeSourceWarehouse_WithoutBusinessRowMutation(
        bool updateExisting)
    {
        var currentUser = CreateInvoiceUser("direct-warehouse-scope");
        await using var dbContext = CreateDbContext(currentUser);
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet);
        var item = CreateItem(ItemTrackingTypes.Stock, 9m);
        dbContext.Customers.Add(customer);
        dbContext.Items.Add(item);
        dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
        {
            ItemId = item.Id,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 9m
        });

        var invoiceId = Guid.NewGuid();
        Invoice? existing = null;
        if (updateExisting)
        {
            existing = CreateInvoice(invoiceId, customer, item, 1m, ItemTrackingTypes.Stock);
            dbContext.Invoices.Add(existing);
        }

        await dbContext.SaveChangesAsync();
        var expectedRevision = existing?.Revision ?? 0;
        var originalUpdatedAtUtc = existing?.UpdatedAtUtc;
        var controller = CreateInvoicesController(dbContext, currentUser);
        var dto = BuildInvoiceDto(
            invoiceId,
            customer,
            item,
            OfficeCodeCatalog.YeonsuMainWarehouse,
            quantity: updateExisting ? 2m : 1m,
            ItemTrackingTypes.Stock,
            expectedRevision);
        dto.Memo = "must-not-persist";

        var response = updateExisting
            ? await controller.Update(invoiceId, dto, CancellationToken.None)
            : await controller.Create(dto, CancellationToken.None);

        Assert.IsType<ForbidResult>(response.Result);
        dbContext.ChangeTracker.Clear();
        Assert.Equal(updateExisting ? 1 : 0, await dbContext.Invoices.IgnoreQueryFilters().CountAsync());
        Assert.Equal(updateExisting ? 1 : 0, await dbContext.InvoiceLines.IgnoreQueryFilters().CountAsync());
        Assert.Equal(9m, await dbContext.Items.IgnoreQueryFilters()
            .Where(row => row.Id == item.Id)
            .Select(row => row.CurrentStock)
            .SingleAsync());
        Assert.Equal(9m, await dbContext.ItemWarehouseStocks
            .Where(row => row.ItemId == item.Id && row.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(row => row.Quantity)
            .SingleAsync());
        Assert.Empty(await dbContext.InventoryLedgerEntries.ToListAsync());
        Assert.Empty(await dbContext.ProcessedSyncMutations.ToListAsync());

        if (updateExisting)
        {
            var stored = await dbContext.Invoices.IgnoreQueryFilters()
                .Include(row => row.Lines)
                .SingleAsync(row => row.Id == invoiceId);
            Assert.Equal(OfficeCodeCatalog.UsenetMainWarehouse, stored.SourceWarehouseCode);
            Assert.Equal(string.Empty, stored.Memo);
            Assert.Equal(expectedRevision, stored.Revision);
            Assert.Equal(originalUpdatedAtUtc, stored.UpdatedAtUtc);
            Assert.Equal(1m, Assert.Single(stored.Lines).Quantity);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DirectInvoiceCreateOrUpdate_RejectsInternallyInconsistentExplicitScope_WithoutMutation(
        bool updateExisting,
        bool mismatchResponsibleOffice)
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet);
        var item = CreateItem(ItemTrackingTypes.NonStock, 0m);
        dbContext.Customers.Add(customer);
        dbContext.Items.Add(item);

        var invoiceId = Guid.NewGuid();
        Invoice? existing = null;
        if (updateExisting)
        {
            existing = CreateInvoice(
                invoiceId,
                customer,
                item,
                1m,
                ItemTrackingTypes.NonStock);
            dbContext.Invoices.Add(existing);
        }

        await dbContext.SaveChangesAsync();
        var expectedRevision = existing?.Revision ?? 0;
        var originalUpdatedAtUtc = existing?.UpdatedAtUtc;
        var dto = BuildInvoiceDto(
            invoiceId,
            customer,
            item,
            OfficeCodeCatalog.UsenetMainWarehouse,
            quantity: 1m,
            ItemTrackingTypes.NonStock,
            expectedRevision);
        if (mismatchResponsibleOffice)
        {
            dto.TenantCode = TenantScopeCatalog.UsenetGroup;
            dto.OfficeCode = OfficeCodeCatalog.Usenet;
            dto.ResponsibleOfficeCode = OfficeCodeCatalog.Itworld;
        }
        else
        {
            // This mismatch would be silently normalized to USENET if the
            // request were checked only after ResolveTenantForCreate.
            dto.TenantCode = TenantScopeCatalog.Itworld;
            dto.OfficeCode = OfficeCodeCatalog.Usenet;
            dto.ResponsibleOfficeCode = OfficeCodeCatalog.Usenet;
        }
        dto.Memo = "must-not-persist";
        var controller = CreateInvoicesController(dbContext, currentUser);

        var response = updateExisting
            ? await controller.Update(invoiceId, dto, CancellationToken.None)
            : await controller.Create(dto, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(response.Result);
        dbContext.ChangeTracker.Clear();
        Assert.Equal(updateExisting ? 1 : 0, await dbContext.Invoices.IgnoreQueryFilters().CountAsync());
        Assert.Equal(updateExisting ? 1 : 0, await dbContext.InvoiceLines.IgnoreQueryFilters().CountAsync());
        Assert.Empty(await dbContext.InventoryLedgerEntries.ToListAsync());
        Assert.Empty(await dbContext.ProcessedSyncMutations.ToListAsync());

        if (updateExisting)
        {
            var stored = await dbContext.Invoices.IgnoreQueryFilters()
                .Include(row => row.Lines)
                .SingleAsync(row => row.Id == invoiceId);
            Assert.Equal(TenantScopeCatalog.UsenetGroup, stored.TenantCode);
            Assert.Equal(OfficeCodeCatalog.Usenet, stored.OfficeCode);
            Assert.Equal(OfficeCodeCatalog.Usenet, stored.ResponsibleOfficeCode);
            Assert.Equal(string.Empty, stored.Memo);
            Assert.Equal(expectedRevision, stored.Revision);
            Assert.Equal(originalUpdatedAtUtc, stored.UpdatedAtUtc);
            Assert.Equal(1m, Assert.Single(stored.Lines).Quantity);
        }
    }

    [Fact]
    public async Task DirectInvoiceUpdate_RejectsForeignExistingStockWarehouse_BeforeReversingPreviousDelta()
    {
        var currentUser = CreateInvoiceUser("direct-existing-warehouse-scope");
        await using var dbContext = CreateDbContext(currentUser);
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet);
        var item = CreateItem(ItemTrackingTypes.Stock, 14m);
        var invoiceId = Guid.NewGuid();
        var existing = CreateInvoice(invoiceId, customer, item, 1m, ItemTrackingTypes.Stock);
        existing.SourceWarehouseCode = OfficeCodeCatalog.ItworldMainWarehouse;
        dbContext.Customers.Add(customer);
        dbContext.Items.Add(item);
        dbContext.ItemWarehouseStocks.AddRange(
            new ItemWarehouseStock
            {
                ItemId = item.Id,
                WarehouseCode = OfficeCodeCatalog.ItworldMainWarehouse,
                Quantity = 9m
            },
            new ItemWarehouseStock
            {
                ItemId = item.Id,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 5m
            });
        dbContext.Invoices.Add(existing);
        await dbContext.SaveChangesAsync();

        var expectedRevision = existing.Revision;
        var originalUpdatedAtUtc = existing.UpdatedAtUtc;
        var dto = BuildInvoiceDto(
            invoiceId,
            customer,
            item,
            OfficeCodeCatalog.UsenetMainWarehouse,
            quantity: 2m,
            ItemTrackingTypes.Stock,
            expectedRevision);
        dto.Memo = "must-not-persist";
        var controller = CreateInvoicesController(dbContext, currentUser);

        var response = await controller.Update(invoiceId, dto, CancellationToken.None);

        Assert.IsType<ForbidResult>(response.Result);
        dbContext.ChangeTracker.Clear();
        var stored = await dbContext.Invoices.IgnoreQueryFilters()
            .Include(row => row.Lines)
            .SingleAsync(row => row.Id == invoiceId);
        Assert.Equal(OfficeCodeCatalog.ItworldMainWarehouse, stored.SourceWarehouseCode);
        Assert.Equal(string.Empty, stored.Memo);
        Assert.Equal(expectedRevision, stored.Revision);
        Assert.Equal(originalUpdatedAtUtc, stored.UpdatedAtUtc);
        Assert.Equal(1m, Assert.Single(stored.Lines).Quantity);
        Assert.Equal(14m, await dbContext.Items.IgnoreQueryFilters()
            .Where(row => row.Id == item.Id)
            .Select(row => row.CurrentStock)
            .SingleAsync());
        Assert.Equal(9m, await dbContext.ItemWarehouseStocks
            .Where(row => row.ItemId == item.Id &&
                          row.WarehouseCode == OfficeCodeCatalog.ItworldMainWarehouse)
            .Select(row => row.Quantity)
            .SingleAsync());
        Assert.Equal(5m, await dbContext.ItemWarehouseStocks
            .Where(row => row.ItemId == item.Id &&
                          row.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(row => row.Quantity)
            .SingleAsync());
        Assert.Empty(await dbContext.InventoryLedgerEntries.ToListAsync());
        Assert.Empty(await dbContext.ProcessedSyncMutations.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SyncInvoiceCreateOrUpdate_RejectsCrossOfficeSourceWarehouse_WithoutBusinessRowMutation(
        bool updateExisting)
    {
        var currentUser = CreateInvoiceUser("sync-warehouse-scope");
        await using var dbContext = CreateDbContext(currentUser);
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet);
        var item = CreateItem(ItemTrackingTypes.Stock, 9m);
        dbContext.Customers.Add(customer);
        dbContext.Items.Add(item);
        dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
        {
            ItemId = item.Id,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 9m
        });

        var invoiceId = Guid.NewGuid();
        Invoice? existing = null;
        if (updateExisting)
        {
            existing = CreateInvoice(invoiceId, customer, item, 1m, ItemTrackingTypes.Stock);
            dbContext.Invoices.Add(existing);
        }

        await dbContext.SaveChangesAsync();
        var expectedRevision = existing?.Revision ?? 0;
        var originalUpdatedAtUtc = existing?.UpdatedAtUtc;
        var controller = CreateSyncController(dbContext, currentUser);
        var dto = BuildInvoiceDto(
            invoiceId,
            customer,
            item,
            OfficeCodeCatalog.YeonsuMainWarehouse,
            quantity: updateExisting ? 2m : 1m,
            ItemTrackingTypes.Stock,
            expectedRevision);
        dto.Memo = "must-not-persist";
        dto.MutationId = $"warehouse-scope:Invoice:{invoiceId:N}:{updateExisting}";
        dto.MutationCreatedAtUtc = DateTime.UtcNow;

        var response = await controller.Push(new SyncPushRequest
        {
            DeviceId = "sync-warehouse-scope",
            Invoices = [dto]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            string.Equals(conflict.EntityName, nameof(Invoice), StringComparison.Ordinal) &&
            conflict.Reason.Contains("warehouse", StringComparison.OrdinalIgnoreCase));

        dbContext.ChangeTracker.Clear();
        Assert.Equal(updateExisting ? 1 : 0, await dbContext.Invoices.IgnoreQueryFilters().CountAsync());
        Assert.Equal(updateExisting ? 1 : 0, await dbContext.InvoiceLines.IgnoreQueryFilters().CountAsync());
        Assert.Equal(9m, await dbContext.Items.IgnoreQueryFilters()
            .Where(row => row.Id == item.Id)
            .Select(row => row.CurrentStock)
            .SingleAsync());
        Assert.Equal(9m, await dbContext.ItemWarehouseStocks
            .Where(row => row.ItemId == item.Id && row.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(row => row.Quantity)
            .SingleAsync());
        Assert.Empty(await dbContext.InventoryLedgerEntries.ToListAsync());
        Assert.Empty(await dbContext.ProcessedSyncMutations.ToListAsync());

        if (updateExisting)
        {
            var stored = await dbContext.Invoices.IgnoreQueryFilters()
                .Include(row => row.Lines)
                .SingleAsync(row => row.Id == invoiceId);
            Assert.Equal(OfficeCodeCatalog.UsenetMainWarehouse, stored.SourceWarehouseCode);
            Assert.Equal(string.Empty, stored.Memo);
            Assert.Equal(expectedRevision, stored.Revision);
            Assert.Equal(originalUpdatedAtUtc, stored.UpdatedAtUtc);
            Assert.Equal(1m, Assert.Single(stored.Lines).Quantity);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public async Task DirectInvoiceCreate_RejectsNonPositiveActiveLineQuantity(decimal quantity)
    {
        var currentUser = CreateInvoiceUser("direct-invoice-quantity");
        await using var dbContext = CreateDbContext(currentUser);
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet);
        var item = CreateItem(ItemTrackingTypes.Stock, 5m);
        dbContext.Customers.Add(customer);
        dbContext.Items.Add(item);
        await dbContext.SaveChangesAsync();

        var invoiceId = Guid.NewGuid();
        var response = await CreateInvoicesController(dbContext, currentUser).Create(
            BuildInvoiceDto(
                invoiceId,
                customer,
                item,
                OfficeCodeCatalog.UsenetMainWarehouse,
                quantity,
                ItemTrackingTypes.Stock),
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Contains("greater than zero", badRequest.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        dbContext.ChangeTracker.Clear();
        Assert.False(await dbContext.Invoices.IgnoreQueryFilters().AnyAsync(row => row.Id == invoiceId));
        Assert.False(await dbContext.InvoiceLines.IgnoreQueryFilters().AnyAsync(row => row.InvoiceId == invoiceId));
        Assert.Empty(await dbContext.ItemWarehouseStocks.ToListAsync());
        Assert.Empty(await dbContext.InventoryLedgerEntries.ToListAsync());
    }

    [Theory]
    [InlineData("0.001")]
    [InlineData("1.234")]
    [InlineData("10000000000000000.00")]
    public async Task DirectInvoiceCreate_RejectsQuantityOutsideDatabaseNumericContract_WithoutMutation(
        string rawQuantity)
    {
        var quantity = decimal.Parse(rawQuantity, System.Globalization.CultureInfo.InvariantCulture);
        var currentUser = CreateInvoiceUser("direct-invoice-numeric-contract");
        await using var dbContext = CreateDbContext(currentUser);
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet);
        var item = CreateItem(ItemTrackingTypes.Stock, 5m);
        dbContext.Customers.Add(customer);
        dbContext.Items.Add(item);
        await dbContext.SaveChangesAsync();

        var invoiceId = Guid.NewGuid();
        var dto = BuildInvoiceDto(
            invoiceId,
            customer,
            item,
            OfficeCodeCatalog.UsenetMainWarehouse,
            quantity,
            ItemTrackingTypes.Stock);
        dto.MutationId = $"numeric-contract:Invoice:{invoiceId:N}:{rawQuantity}";
        dto.MutationCreatedAtUtc = DateTime.UtcNow;

        var response = await CreateInvoicesController(dbContext, currentUser).Create(
            dto,
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Contains("numeric(18,2)", badRequest.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        dbContext.ChangeTracker.Clear();
        Assert.False(await dbContext.Invoices.IgnoreQueryFilters().AnyAsync(row => row.Id == invoiceId));
        Assert.False(await dbContext.InvoiceLines.IgnoreQueryFilters().AnyAsync(row => row.InvoiceId == invoiceId));
        Assert.Empty(await dbContext.ItemWarehouseStocks.ToListAsync());
        Assert.Empty(await dbContext.InventoryLedgerEntries.ToListAsync());
        Assert.False(await dbContext.ProcessedSyncMutations.AnyAsync(row => row.MutationId == dto.MutationId));
    }

    [Fact]
    public async Task DirectInvoiceUpdate_RejectsRouteBodyIdentityMismatch_BeforeReceiptLookupOrMutation()
    {
        var currentUser = CreateInvoiceUser("direct-invoice-route-body-id");
        await using var dbContext = CreateDbContext(currentUser);
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet);
        var item = CreateItem(ItemTrackingTypes.NonStock, 0m);
        var invoiceA = CreateInvoice(Guid.NewGuid(), customer, item, 1m, ItemTrackingTypes.NonStock);
        var invoiceBId = Guid.NewGuid();
        dbContext.Customers.Add(customer);
        dbContext.Items.Add(item);
        dbContext.Invoices.Add(invoiceA);
        await dbContext.SaveChangesAsync();

        var firstMismatchDto = BuildInvoiceDto(
            invoiceBId,
            customer,
            item,
            OfficeCodeCatalog.UsenetMainWarehouse,
            2m,
            ItemTrackingTypes.NonStock,
            invoiceA.Revision);
        firstMismatchDto.MutationId = $"route-body-first-mismatch:Invoice:{invoiceA.Id:N}";
        firstMismatchDto.MutationCreatedAtUtc = DateTime.UtcNow;
        firstMismatchDto.Memo = "must-not-reach-a";

        var controller = CreateInvoicesController(dbContext, currentUser);
        var firstMismatch = await controller.Update(
            invoiceA.Id,
            firstMismatchDto,
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(firstMismatch.Result);
        Assert.False(await dbContext.ProcessedSyncMutations
            .AnyAsync(row => row.MutationId == firstMismatchDto.MutationId));

        var successfulDto = BuildInvoiceDto(
            invoiceBId,
            customer,
            item,
            OfficeCodeCatalog.UsenetMainWarehouse,
            2m,
            ItemTrackingTypes.NonStock);
        successfulDto.MutationId = $"route-body-replay:Invoice:{invoiceBId:N}";
        successfulDto.MutationCreatedAtUtc = DateTime.UtcNow;
        successfulDto.Memo = "updated-b";

        var successful = await controller.Create(
            successfulDto,
            CancellationToken.None);
        Assert.IsType<OkObjectResult>(successful.Result);

        var replayAgainstDifferentRoute = await controller.Update(
            invoiceA.Id,
            successfulDto,
            CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(replayAgainstDifferentRoute.Result);

        dbContext.ChangeTracker.Clear();
        var storedA = await dbContext.Invoices.IgnoreQueryFilters()
            .Include(row => row.Lines)
            .SingleAsync(row => row.Id == invoiceA.Id);
        var storedB = await dbContext.Invoices.IgnoreQueryFilters()
            .Include(row => row.Lines)
            .SingleAsync(row => row.Id == invoiceBId);
        Assert.Equal(string.Empty, storedA.Memo);
        Assert.Equal(1m, Assert.Single(storedA.Lines).Quantity);
        Assert.Equal("updated-b", storedB.Memo);
        Assert.Equal(2m, Assert.Single(storedB.Lines).Quantity);
        Assert.Equal(1, await dbContext.ProcessedSyncMutations
            .CountAsync(row => row.MutationId == successfulDto.MutationId));
    }

    [Theory]
    [InlineData("-2")]
    [InlineData("0.001")]
    [InlineData("1.234")]
    [InlineData("10000000000000000.00")]
    public async Task SyncInvoiceCreate_RejectsQuantityOutsideDatabaseNumericContract(string rawQuantity)
    {
        var quantity = decimal.Parse(rawQuantity, System.Globalization.CultureInfo.InvariantCulture);
        var currentUser = CreateInvoiceUser("sync-invoice-quantity");
        await using var dbContext = CreateDbContext(currentUser);
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet);
        var item = CreateItem(ItemTrackingTypes.Stock, 5m);
        dbContext.Customers.Add(customer);
        dbContext.Items.Add(item);
        await dbContext.SaveChangesAsync();

        var invoiceId = Guid.NewGuid();
        var dto = BuildInvoiceDto(
            invoiceId,
            customer,
            item,
            OfficeCodeCatalog.UsenetMainWarehouse,
            quantity,
            ItemTrackingTypes.Stock);
        dto.MutationId = $"invalid-quantity:Invoice:{invoiceId:N}:{rawQuantity}";
        dto.MutationCreatedAtUtc = DateTime.UtcNow;

        var response = await CreateSyncController(dbContext, currentUser).Push(new SyncPushRequest
        {
            DeviceId = "sync-invoice-negative-quantity",
            Invoices = [dto]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            conflict.Reason.Contains("greater than zero", StringComparison.OrdinalIgnoreCase));
        dbContext.ChangeTracker.Clear();
        Assert.False(await dbContext.Invoices.IgnoreQueryFilters().AnyAsync(row => row.Id == invoiceId));
        Assert.Empty(await dbContext.ItemWarehouseStocks.ToListAsync());
        Assert.Empty(await dbContext.InventoryLedgerEntries.ToListAsync());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-2")]
    [InlineData("0.001")]
    [InlineData("1.234")]
    [InlineData("10000000000000000.00")]
    public async Task SyncInventoryTransferCreate_RejectsQuantityOutsideDatabaseNumericContract(
        string rawQuantity)
    {
        var quantity = decimal.Parse(rawQuantity, System.Globalization.CultureInfo.InvariantCulture);
        var currentUser = CreateDeliveryUser("sync-transfer-quantity");
        await using var dbContext = CreateDbContext(currentUser);
        var item = CreateItem(ItemTrackingTypes.Stock, 10m);
        dbContext.Items.Add(item);
        dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
        {
            ItemId = item.Id,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 10m
        });
        await dbContext.SaveChangesAsync();

        var transferId = Guid.NewGuid();
        var response = await CreateSyncController(dbContext, currentUser).Push(new SyncPushRequest
        {
            DeviceId = "sync-transfer-invalid-quantity",
            InventoryTransfers =
            [
                new InventoryTransferDto
                {
                    Id = transferId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    SourceOfficeCode = OfficeCodeCatalog.Usenet,
                    TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
                    TransferNumber = "TR-INVALID-QUANTITY",
                    TransferDate = new DateOnly(2026, 7, 26),
                    FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                    ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                    TransferStatus = InventoryTransferStatusNormalizer.Pending,
                    MutationId = $"invalid-quantity:InventoryTransfer:{transferId:N}:{rawQuantity}",
                    MutationCreatedAtUtc = DateTime.UtcNow,
                    Lines =
                    [
                        new InventoryTransferLineDto
                        {
                            Id = Guid.NewGuid(),
                            TransferId = transferId,
                            ItemId = item.Id,
                            ItemNameOriginal = item.NameOriginal,
                            Unit = "EA",
                            Quantity = quantity
                        }
                    ]
                }
            ]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            conflict.Reason.Contains("greater than zero", StringComparison.OrdinalIgnoreCase));
        dbContext.ChangeTracker.Clear();
        Assert.False(await dbContext.InventoryTransfers.IgnoreQueryFilters().AnyAsync(row => row.Id == transferId));
        Assert.Equal(10m, await dbContext.ItemWarehouseStocks
            .Where(row => row.ItemId == item.Id && row.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(row => row.Quantity)
            .SingleAsync());
        Assert.Empty(await dbContext.InventoryLedgerEntries.ToListAsync());
    }

    [Theory]
    [InlineData(ItemTrackingTypes.Stock, ItemTrackingTypes.NonStock, 3, -2, 1)]
    [InlineData(ItemTrackingTypes.NonStock, ItemTrackingTypes.Stock, 5, 0, 0)]
    public async Task DirectInvoiceCreate_UsesAuthoritativeItemTrackingType_ForSnapshotAndLedger(
        string actualTrackingType,
        string clientTrackingType,
        decimal expectedCurrentStock,
        decimal expectedLedgerDelta,
        int expectedWarehouseRowCount)
    {
        var currentUser = CreateInvoiceUser("direct-tracking-authority");
        await using var dbContext = CreateDbContext(currentUser);
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet);
        var item = CreateItem(actualTrackingType, 5m);
        dbContext.Customers.Add(customer);
        dbContext.Items.Add(item);
        if (ItemOperationalPolicy.SupportsInventory(actualTrackingType))
        {
            dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
            {
                ItemId = item.Id,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 5m
            });
        }
        await dbContext.SaveChangesAsync();

        var invoiceId = Guid.NewGuid();
        var response = await CreateInvoicesController(dbContext, currentUser).Create(
            BuildInvoiceDto(
                invoiceId,
                customer,
                item,
                OfficeCodeCatalog.UsenetMainWarehouse,
                2m,
                clientTrackingType),
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(response.Result);
        dbContext.ChangeTracker.Clear();
        var storedLine = await dbContext.InvoiceLines.IgnoreQueryFilters()
            .SingleAsync(row => row.InvoiceId == invoiceId);
        Assert.Equal(actualTrackingType, storedLine.ItemTrackingType);
        Assert.Equal(expectedCurrentStock, await dbContext.Items.IgnoreQueryFilters()
            .Where(row => row.Id == item.Id)
            .Select(row => row.CurrentStock)
            .SingleAsync());
        Assert.Equal(expectedWarehouseRowCount, await dbContext.ItemWarehouseStocks.CountAsync(row => row.ItemId == item.Id));

        var ledgerEntries = await dbContext.InventoryLedgerEntries
            .Where(row => row.SourceDocumentId == invoiceId)
            .ToListAsync();
        if (expectedLedgerDelta == 0m)
        {
            Assert.Empty(ledgerEntries);
        }
        else
        {
            Assert.Equal(expectedLedgerDelta, Assert.Single(ledgerEntries).QuantityDelta);
            Assert.Equal(expectedCurrentStock, await dbContext.ItemWarehouseStocks
                .Where(row => row.ItemId == item.Id && row.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(row => row.Quantity)
                .SingleAsync());
        }
    }

    [Theory]
    [InlineData(ItemTrackingTypes.Stock, ItemTrackingTypes.NonStock, 3, -2, 1)]
    [InlineData(ItemTrackingTypes.NonStock, ItemTrackingTypes.Stock, 5, 0, 0)]
    public async Task SyncInvoiceCreate_UsesAuthoritativeItemTrackingType_ForSnapshotAndLedger(
        string actualTrackingType,
        string clientTrackingType,
        decimal expectedCurrentStock,
        decimal expectedLedgerDelta,
        int expectedWarehouseRowCount)
    {
        var currentUser = CreateInvoiceUser("sync-tracking-authority");
        await using var dbContext = CreateDbContext(currentUser);
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet);
        var item = CreateItem(actualTrackingType, 5m);
        dbContext.Customers.Add(customer);
        dbContext.Items.Add(item);
        if (ItemOperationalPolicy.SupportsInventory(actualTrackingType))
        {
            dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
            {
                ItemId = item.Id,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 5m
            });
        }
        await dbContext.SaveChangesAsync();

        var invoiceId = Guid.NewGuid();
        var dto = BuildInvoiceDto(
            invoiceId,
            customer,
            item,
            OfficeCodeCatalog.UsenetMainWarehouse,
            2m,
            clientTrackingType);
        dto.MutationId = $"tracking-authority:Invoice:{invoiceId:N}";
        dto.MutationCreatedAtUtc = DateTime.UtcNow;
        var response = await CreateSyncController(dbContext, currentUser).Push(new SyncPushRequest
        {
            DeviceId = "sync-tracking-authority",
            Invoices = [dto]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.Equal(1, result.AcceptedCount);
        Assert.Equal(0, result.ConflictCount);
        dbContext.ChangeTracker.Clear();
        var storedLine = await dbContext.InvoiceLines.IgnoreQueryFilters()
            .SingleAsync(row => row.InvoiceId == invoiceId);
        Assert.Equal(actualTrackingType, storedLine.ItemTrackingType);
        Assert.Equal(expectedCurrentStock, await dbContext.Items.IgnoreQueryFilters()
            .Where(row => row.Id == item.Id)
            .Select(row => row.CurrentStock)
            .SingleAsync());
        Assert.Equal(expectedWarehouseRowCount, await dbContext.ItemWarehouseStocks.CountAsync(row => row.ItemId == item.Id));

        var ledgerEntries = await dbContext.InventoryLedgerEntries
            .Where(row => row.SourceDocumentId == invoiceId)
            .ToListAsync();
        if (expectedLedgerDelta == 0m)
            Assert.Empty(ledgerEntries);
        else
            Assert.Equal(expectedLedgerDelta, Assert.Single(ledgerEntries).QuantityDelta);
    }

    [Fact]
    public async Task SyncInvoiceBatchCreate_SameNewWarehouseStock_AggregatesBeforeSaveWithoutDuplicateTracking()
    {
        var currentUser = CreateInvoiceUser("sync-batch-stock-tracking");
        await using var dbContext = CreateDbContext(currentUser);
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet);
        var item = CreateItem(ItemTrackingTypes.Stock, 0m);
        dbContext.Customers.Add(customer);
        dbContext.Items.Add(item);
        await dbContext.SaveChangesAsync();

        var firstInvoiceId = Guid.NewGuid();
        var secondInvoiceId = Guid.NewGuid();
        var first = BuildInvoiceDto(
            firstInvoiceId,
            customer,
            item,
            OfficeCodeCatalog.UsenetMainWarehouse,
            2m,
            ItemTrackingTypes.Stock);
        var second = BuildInvoiceDto(
            secondInvoiceId,
            customer,
            item,
            OfficeCodeCatalog.UsenetMainWarehouse,
            3m,
            ItemTrackingTypes.Stock);
        foreach (var invoice in new[] { first, second })
        {
            invoice.VoucherType = VoucherType.Purchase;
            invoice.PurchaseReceivingStatus = InvoiceReceivingStatuses.Confirmed;
            invoice.MutationId = $"batch-stock:Invoice:{invoice.Id:N}";
            invoice.MutationCreatedAtUtc = DateTime.UtcNow;
        }

        var response = await CreateSyncController(dbContext, currentUser).Push(new SyncPushRequest
        {
            DeviceId = "sync-batch-stock-tracking",
            Invoices = [first, second]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.Equal(2, result.AcceptedCount);
        Assert.Equal(0, result.ConflictCount);

        dbContext.ChangeTracker.Clear();
        Assert.Equal(2, await dbContext.Invoices.IgnoreQueryFilters().CountAsync());
        var stock = Assert.Single(await dbContext.ItemWarehouseStocks
            .Where(row => row.ItemId == item.Id)
            .ToListAsync());
        Assert.Equal(OfficeCodeCatalog.UsenetMainWarehouse, stock.WarehouseCode);
        Assert.Equal(5m, stock.Quantity);
        Assert.Equal(5m, await dbContext.Items.IgnoreQueryFilters()
            .Where(row => row.Id == item.Id)
            .Select(row => row.CurrentStock)
            .SingleAsync());
        var ledgerQuantities = await dbContext.InventoryLedgerEntries
            .Where(row => row.ItemId == item.Id)
            .Select(row => row.QuantityDelta)
            .ToListAsync();
        Assert.Equal(5m, ledgerQuantities.Sum());
    }

    [Fact]
    public async Task PendingDeletedWarehouseStock_IsExcludedFromShortageAndRejectedByDeltaApply()
    {
        var currentUser = CreateInvoiceUser("pending-deleted-stock");
        await using var dbContext = CreateDbContext(currentUser);
        var item = CreateItem(ItemTrackingTypes.Stock, 5m);
        var stock = new ItemWarehouseStock
        {
            ItemId = item.Id,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 5m
        };
        dbContext.Items.Add(item);
        dbContext.ItemWarehouseStocks.Add(stock);
        await dbContext.SaveChangesAsync();
        dbContext.ItemWarehouseStocks.Remove(stock);

        var service = new InvoiceStockSnapshotService(dbContext, new RevisionClock());
        var key = new InvoiceStockSnapshotService.InvoiceStockKey(
            item.Id,
            OfficeCodeCatalog.UsenetMainWarehouse);
        var previous = new Dictionary<InvoiceStockSnapshotService.InvoiceStockKey, decimal>();
        var current = new Dictionary<InvoiceStockSnapshotService.InvoiceStockKey, decimal>
        {
            [key] = -1m
        };

        var shortage = Assert.Single(await service.FindStockShortagesAsync(
            previous,
            current,
            CancellationToken.None));
        Assert.Equal(0m, shortage.CurrentQuantity);
        Assert.Equal(1m, shortage.RequestedDecrease);
        Assert.Equal(1m, shortage.ShortageQuantity);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ApplyInvoiceStockDeltaDifferenceAsync(
                previous,
                current,
                CancellationToken.None));
        Assert.Contains("pending deletion", error.Message, StringComparison.Ordinal);
        Assert.Equal(EntityState.Deleted, dbContext.Entry(stock).State);
        Assert.Equal(5m, item.CurrentStock);
    }

    [Fact]
    public async Task PendingDeletedCaseVariantWarehouseStock_BlocksLogicalKeyDeltaApply()
    {
        var currentUser = CreateInvoiceUser("pending-deleted-stock-case-variant");
        await using var dbContext = CreateDbContext(currentUser);
        var canonicalWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse;
        var caseVariantWarehouseCode = canonicalWarehouseCode.ToLowerInvariant();
        if (string.Equals(
                canonicalWarehouseCode,
                caseVariantWarehouseCode,
                StringComparison.Ordinal))
        {
            caseVariantWarehouseCode = canonicalWarehouseCode.ToUpperInvariant();
        }
        Assert.NotEqual(canonicalWarehouseCode, caseVariantWarehouseCode);

        var item = CreateItem(ItemTrackingTypes.Stock, 10m);
        var canonicalStock = new ItemWarehouseStock
        {
            ItemId = item.Id,
            WarehouseCode = canonicalWarehouseCode,
            Quantity = 5m
        };
        var caseVariantStock = new ItemWarehouseStock
        {
            ItemId = item.Id,
            WarehouseCode = caseVariantWarehouseCode,
            Quantity = 5m
        };
        dbContext.Items.Add(item);
        dbContext.ItemWarehouseStocks.AddRange(canonicalStock, caseVariantStock);
        await dbContext.SaveChangesAsync();
        dbContext.ItemWarehouseStocks.Remove(caseVariantStock);

        var service = new InvoiceStockSnapshotService(dbContext, new RevisionClock());
        var key = new InvoiceStockSnapshotService.InvoiceStockKey(
            item.Id,
            canonicalWarehouseCode);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ApplyInvoiceStockDeltaDifferenceAsync(
                new Dictionary<InvoiceStockSnapshotService.InvoiceStockKey, decimal>(),
                new Dictionary<InvoiceStockSnapshotService.InvoiceStockKey, decimal>
                {
                    [key] = -1m
                },
                CancellationToken.None));

        Assert.Contains("pending deletion", error.Message, StringComparison.Ordinal);
        Assert.Equal(EntityState.Unchanged, dbContext.Entry(canonicalStock).State);
        Assert.Equal(EntityState.Deleted, dbContext.Entry(caseVariantStock).State);
        Assert.Equal(5m, canonicalStock.Quantity);
        Assert.Equal(10m, item.CurrentStock);
    }

    [Fact]
    public async Task LegacyNegativeSalesInvoice_Delete_ReversesAbsoluteStockAndLedgerEffect()
    {
        var currentUser = CreateInvoiceUser("legacy-negative-invoice-delete");
        await using var dbContext = CreateDbContext(currentUser);
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet);
        var item = CreateItem(ItemTrackingTypes.Stock, 8m);
        var invoice = CreateInvoice(
            Guid.NewGuid(),
            customer,
            item,
            -2m,
            ItemTrackingTypes.Stock);
        dbContext.Customers.Add(customer);
        dbContext.Items.Add(item);
        dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
        {
            ItemId = item.Id,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 8m
        });
        dbContext.Invoices.Add(invoice);
        await dbContext.SaveChangesAsync();
        var expectedRevision = invoice.Revision;

        await new InventoryLedgerService(dbContext).RebuildAsync(CancellationToken.None);
        dbContext.ChangeTracker.Clear();
        Assert.Equal(-2m, await dbContext.InventoryLedgerEntries
            .Where(row => row.SourceDocumentId == invoice.Id)
            .Select(row => row.QuantityDelta)
            .SingleAsync());

        var response = await CreateInvoicesController(dbContext, currentUser).Delete(
            invoice.Id,
            expectedRevision,
            CancellationToken.None);

        Assert.IsType<NoContentResult>(response);
        dbContext.ChangeTracker.Clear();
        Assert.Equal(10m, await dbContext.Items.IgnoreQueryFilters()
            .Where(row => row.Id == item.Id)
            .Select(row => row.CurrentStock)
            .SingleAsync());
        Assert.Equal(10m, await dbContext.ItemWarehouseStocks
            .Where(row =>
                row.ItemId == item.Id &&
                row.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(row => row.Quantity)
            .SingleAsync());
        Assert.Empty(await dbContext.InventoryLedgerEntries
            .Where(row => row.SourceDocumentId == invoice.Id)
            .ToListAsync());
        Assert.True(await dbContext.Invoices.IgnoreQueryFilters()
            .Where(row => row.Id == invoice.Id)
            .Select(row => row.IsDeleted)
            .SingleAsync());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("deleted")]
    [InlineData("out-of-scope")]
    public async Task SyncInvoiceDelete_WithUnusableCustomer_PreservesExistingScopeFields(string customerCase)
    {
        var currentUser = CreateInvoiceUser("sync-delete-scope");
        await using var dbContext = CreateDbContext(currentUser);
        var existingCustomer = CreateCustomer(OfficeCodeCatalog.Usenet);
        var incomingCustomer = CreateCustomer(OfficeCodeCatalog.Itworld);
        incomingCustomer.TenantCode = TenantScopeCatalog.Itworld;
        incomingCustomer.IsDeleted = string.Equals(customerCase, "deleted", StringComparison.Ordinal);
        var item = CreateItem(ItemTrackingTypes.NonStock, 0m);
        var invoiceId = Guid.NewGuid();
        var invoice = CreateInvoice(invoiceId, existingCustomer, item, 1m, ItemTrackingTypes.NonStock);
        dbContext.Customers.Add(existingCustomer);
        if (!string.Equals(customerCase, "missing", StringComparison.Ordinal))
            dbContext.Customers.Add(incomingCustomer);
        dbContext.Items.Add(item);
        dbContext.Invoices.Add(invoice);
        await dbContext.SaveChangesAsync();
        var expectedRevision = invoice.Revision;

        var dto = BuildInvoiceDto(
            invoiceId,
            incomingCustomer,
            item,
            OfficeCodeCatalog.ItworldMainWarehouse,
            1m,
            ItemTrackingTypes.NonStock,
            expectedRevision);
        dto.IsDeleted = true;
        dto.TenantCode = TenantScopeCatalog.Itworld;
        dto.OfficeCode = OfficeCodeCatalog.Itworld;
        dto.ResponsibleOfficeCode = OfficeCodeCatalog.Itworld;
        dto.MutationId = $"delete-scope:Invoice:{invoiceId:N}";
        dto.MutationCreatedAtUtc = DateTime.UtcNow;

        var response = await CreateSyncController(dbContext, currentUser).Push(new SyncPushRequest
        {
            DeviceId = "sync-delete-scope",
            Invoices = [dto]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.Equal(1, result.AcceptedCount);
        Assert.Equal(0, result.ConflictCount);
        dbContext.ChangeTracker.Clear();
        var stored = await dbContext.Invoices.IgnoreQueryFilters().SingleAsync(row => row.Id == invoiceId);
        Assert.True(stored.IsDeleted);
        Assert.Equal(existingCustomer.Id, stored.CustomerId);
        Assert.Equal(TenantScopeCatalog.UsenetGroup, stored.TenantCode);
        Assert.Equal(OfficeCodeCatalog.Usenet, stored.OfficeCode);
        Assert.Equal(OfficeCodeCatalog.Usenet, stored.ResponsibleOfficeCode);
        Assert.Equal(OfficeCodeCatalog.UsenetMainWarehouse, stored.SourceWarehouseCode);
    }

    [Fact]
    public async Task DirectInvoiceCreate_NormalizesClientVersionMetadata_ToNewSelfGroup()
    {
        var currentUser = CreateInvoiceUser("direct-version-create");
        await using var dbContext = CreateDbContext(currentUser);
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet);
        var foreignCustomer = CreateCustomer(OfficeCodeCatalog.Itworld);
        var item = CreateItem(ItemTrackingTypes.NonStock, 0m);
        var foreignGroupId = Guid.NewGuid();
        var foreignInvoiceId = Guid.NewGuid();
        var foreignInvoice = new Invoice
        {
            Id = foreignInvoiceId,
            CustomerId = foreignCustomer.Id,
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
            InvoiceNumber = $"INV-{foreignInvoiceId:N}"[..20],
            VersionGroupId = foreignGroupId,
            VersionNumber = 7,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            SourceWarehouseCode = OfficeCodeCatalog.ItworldMainWarehouse,
            InvoiceDate = new DateOnly(2026, 7, 30)
        };
        dbContext.Customers.AddRange(customer, foreignCustomer);
        dbContext.Items.Add(item);
        dbContext.Invoices.Add(foreignInvoice);
        await dbContext.SaveChangesAsync();
        var foreignRevision = foreignInvoice.Revision;

        var invoiceId = Guid.NewGuid();
        var dto = BuildInvoiceDto(
            invoiceId,
            customer,
            item,
            OfficeCodeCatalog.UsenetMainWarehouse,
            1m,
            ItemTrackingTypes.NonStock);
        dto.VersionGroupId = foreignGroupId;
        dto.VersionNumber = 99;
        dto.PreviousVersionId = foreignInvoiceId;
        dto.IsLatestVersion = false;
        dto.MutationId = $"direct-version-create:Invoice:{invoiceId:N}";
        dto.MutationCreatedAtUtc = DateTime.UtcNow;

        var response = await CreateInvoicesController(dbContext, currentUser).Create(
            dto,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var responseDto = Assert.IsType<InvoiceDto>(ok.Value);
        Assert.Equal(invoiceId, responseDto.VersionGroupId);
        Assert.Equal(1, responseDto.VersionNumber);
        Assert.Null(responseDto.PreviousVersionId);
        Assert.True(responseDto.IsLatestVersion);

        dbContext.ChangeTracker.Clear();
        var stored = await dbContext.Invoices.IgnoreQueryFilters()
            .SingleAsync(row => row.Id == invoiceId);
        Assert.Equal(invoiceId, stored.VersionGroupId);
        Assert.Equal(1, stored.VersionNumber);
        Assert.Null(stored.PreviousVersionId);
        Assert.True(stored.IsLatestVersion);

        var unchangedForeign = await dbContext.Invoices.IgnoreQueryFilters()
            .SingleAsync(row => row.Id == foreignInvoiceId);
        Assert.Equal(foreignGroupId, unchangedForeign.VersionGroupId);
        Assert.Equal(7, unchangedForeign.VersionNumber);
        Assert.True(unchangedForeign.IsLatestVersion);
        Assert.Equal(foreignRevision, unchangedForeign.Revision);
        Assert.Equal(1, await dbContext.ProcessedSyncMutations
            .CountAsync(row => row.MutationId == dto.MutationId));
    }

    [Fact]
    public async Task DirectInvoiceUpdate_RejectsClientVersionMetadataMutation_WithoutReceiptOrBusinessMutation()
    {
        var currentUser = CreateInvoiceUser("direct-version-update");
        await using var dbContext = CreateDbContext(currentUser);
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet);
        var item = CreateItem(ItemTrackingTypes.NonStock, 0m);
        var invoiceId = Guid.NewGuid();
        var invoice = CreateInvoice(
            invoiceId,
            customer,
            item,
            1m,
            ItemTrackingTypes.NonStock);
        dbContext.Customers.Add(customer);
        dbContext.Items.Add(item);
        dbContext.Invoices.Add(invoice);
        await dbContext.SaveChangesAsync();
        var expectedRevision = invoice.Revision;
        var originalUpdatedAtUtc = invoice.UpdatedAtUtc;

        var dto = BuildInvoiceDto(
            invoiceId,
            customer,
            item,
            OfficeCodeCatalog.UsenetMainWarehouse,
            1m,
            ItemTrackingTypes.NonStock,
            expectedRevision);
        dto.VersionGroupId = Guid.NewGuid();
        dto.VersionNumber = 42;
        dto.PreviousVersionId = Guid.NewGuid();
        dto.IsLatestVersion = false;
        dto.Memo = "must-not-persist";
        dto.MutationId = $"direct-version-update:Invoice:{invoiceId:N}";
        dto.MutationCreatedAtUtc = DateTime.UtcNow;

        var response = await CreateInvoicesController(dbContext, currentUser).Update(
            invoiceId,
            dto,
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Contains("version metadata", badRequest.Value?.ToString(), StringComparison.OrdinalIgnoreCase);

        dbContext.ChangeTracker.Clear();
        var unchanged = await dbContext.Invoices.IgnoreQueryFilters()
            .Include(row => row.Lines)
            .SingleAsync(row => row.Id == invoiceId);
        Assert.Equal(invoiceId, unchanged.VersionGroupId);
        Assert.Equal(1, unchanged.VersionNumber);
        Assert.Null(unchanged.PreviousVersionId);
        Assert.True(unchanged.IsLatestVersion);
        Assert.Equal(string.Empty, unchanged.Memo);
        Assert.Equal(expectedRevision, unchanged.Revision);
        Assert.Equal(originalUpdatedAtUtc, unchanged.UpdatedAtUtc);
        Assert.Equal(1m, Assert.Single(unchanged.Lines).Quantity);
        Assert.False(await dbContext.ProcessedSyncMutations
            .AnyAsync(row => row.MutationId == dto.MutationId));
    }

    [Fact]
    public async Task DirectInvoiceUpdate_PreservesServerLatestFlag_WhenEffectiveSelfGroupMetadataMatches()
    {
        var currentUser = CreateInvoiceUser("direct-version-latest-derived");
        await using var dbContext = CreateDbContext(currentUser);
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet);
        var item = CreateItem(ItemTrackingTypes.NonStock, 0m);
        var invoiceId = Guid.NewGuid();
        var invoice = CreateInvoice(
            invoiceId,
            customer,
            item,
            1m,
            ItemTrackingTypes.NonStock);
        dbContext.Customers.Add(customer);
        dbContext.Items.Add(item);
        dbContext.Invoices.Add(invoice);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        var storedBeforeUpdate = await dbContext.Invoices.IgnoreQueryFilters()
            .Include(row => row.Customer)
            .Include(row => row.Lines)
            .AsNoTracking()
            .SingleAsync(row => row.Id == invoiceId);

        var dto = storedBeforeUpdate.ToDto();
        dto.ExpectedRevision = storedBeforeUpdate.Revision;
        dto.VersionGroupId = Guid.Empty;
        dto.IsLatestVersion = false;
        dto.Memo = "metadata-safe-update";
        dto.MutationId = $"direct-version-latest-derived:Invoice:{invoiceId:N}";
        dto.MutationCreatedAtUtc = DateTime.UtcNow;

        var response = await CreateInvoicesController(dbContext, currentUser).Update(
            invoiceId,
            dto,
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var responseDto = Assert.IsType<InvoiceDto>(ok.Value);
        Assert.Equal(invoiceId, responseDto.VersionGroupId);
        Assert.Equal(1, responseDto.VersionNumber);
        Assert.Null(responseDto.PreviousVersionId);
        Assert.True(responseDto.IsLatestVersion);

        dbContext.ChangeTracker.Clear();
        var stored = await dbContext.Invoices.IgnoreQueryFilters()
            .SingleAsync(row => row.Id == invoiceId);
        Assert.Equal(invoiceId, stored.VersionGroupId);
        Assert.Equal(1, stored.VersionNumber);
        Assert.Null(stored.PreviousVersionId);
        Assert.True(stored.IsLatestVersion);
        Assert.Equal("metadata-safe-update", stored.Memo);
        Assert.Equal(1, await dbContext.ProcessedSyncMutations
            .CountAsync(row => row.MutationId == dto.MutationId));
    }

    [Fact]
    public async Task DirectInvoiceDelete_Latest_PromotesOnlySameNormalizedScopePreviousVersion()
    {
        var currentUser = CreateInvoiceUser("direct-version-delete");
        await using var dbContext = CreateDbContext(currentUser);
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet);
        var foreignCustomer = CreateCustomer(OfficeCodeCatalog.Itworld);
        var item = CreateItem(ItemTrackingTypes.Stock, 8m);
        var versionGroupId = Guid.NewGuid();
        var previousId = Guid.NewGuid();
        var latestId = Guid.NewGuid();

        var previous = CreateInvoice(
            previousId,
            customer,
            item,
            1m,
            ItemTrackingTypes.Stock);
        previous.VersionGroupId = versionGroupId;
        previous.VersionNumber = 1;
        previous.IsLatestVersion = false;
        previous.TenantCode = "USENET";
        previous.OfficeCode = "UZNET";
        previous.ResponsibleOfficeCode = "유즈넷";

        var latest = CreateInvoice(
            latestId,
            customer,
            item,
            2m,
            ItemTrackingTypes.Stock);
        latest.VersionGroupId = versionGroupId;
        latest.VersionNumber = 2;
        latest.PreviousVersionId = previousId;
        latest.IsLatestVersion = true;

        var sameCustomerForeignScopeId = Guid.NewGuid();
        var sameCustomerForeignScope = new Invoice
        {
            Id = sameCustomerForeignScopeId,
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
            InvoiceNumber = $"INV-{sameCustomerForeignScopeId:N}"[..20],
            VersionGroupId = versionGroupId,
            VersionNumber = 99,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            SourceWarehouseCode = OfficeCodeCatalog.ItworldMainWarehouse,
            InvoiceDate = new DateOnly(2026, 7, 30)
        };
        var foreignCustomerSameScopeId = Guid.NewGuid();
        var foreignCustomerSameScope = new Invoice
        {
            Id = foreignCustomerSameScopeId,
            CustomerId = foreignCustomer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = $"INV-{foreignCustomerSameScopeId:N}"[..20],
            VersionGroupId = versionGroupId,
            VersionNumber = 98,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            InvoiceDate = new DateOnly(2026, 7, 30)
        };

        dbContext.Customers.AddRange(customer, foreignCustomer);
        dbContext.Items.Add(item);
        dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
        {
            ItemId = item.Id,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 8m
        });
        dbContext.Invoices.AddRange(
            previous,
            latest,
            sameCustomerForeignScope,
            foreignCustomerSameScope);
        await dbContext.SaveChangesAsync();
        var expectedRevision = latest.Revision;
        var sameCustomerForeignScopeRevision = sameCustomerForeignScope.Revision;
        var foreignCustomerSameScopeRevision = foreignCustomerSameScope.Revision;

        await new InventoryLedgerService(dbContext).RebuildAsync(CancellationToken.None);
        dbContext.ChangeTracker.Clear();
        Assert.Equal(-2m, await dbContext.InventoryLedgerEntries
            .Where(row => row.SourceDocumentId == latestId)
            .Select(row => row.QuantityDelta)
            .SingleAsync());

        var response = await CreateInvoicesController(dbContext, currentUser).Delete(
            latestId,
            expectedRevision,
            CancellationToken.None);

        Assert.IsType<NoContentResult>(response);
        dbContext.ChangeTracker.Clear();
        var versions = await dbContext.Invoices.IgnoreQueryFilters()
            .Where(row => new[]
            {
                previousId,
                latestId,
                sameCustomerForeignScopeId,
                foreignCustomerSameScopeId
            }.Contains(row.Id))
            .ToDictionaryAsync(row => row.Id);

        Assert.False(versions[previousId].IsDeleted);
        Assert.True(versions[previousId].IsLatestVersion);
        Assert.True(versions[latestId].IsDeleted);
        Assert.False(versions[latestId].IsLatestVersion);

        Assert.True(versions[sameCustomerForeignScopeId].IsLatestVersion);
        Assert.Equal(99, versions[sameCustomerForeignScopeId].VersionNumber);
        Assert.Equal(sameCustomerForeignScopeRevision, versions[sameCustomerForeignScopeId].Revision);
        Assert.True(versions[foreignCustomerSameScopeId].IsLatestVersion);
        Assert.Equal(98, versions[foreignCustomerSameScopeId].VersionNumber);
        Assert.Equal(foreignCustomerSameScopeRevision, versions[foreignCustomerSameScopeId].Revision);

        Assert.Equal(9m, await dbContext.Items.IgnoreQueryFilters()
            .Where(row => row.Id == item.Id)
            .Select(row => row.CurrentStock)
            .SingleAsync());
        Assert.Equal(9m, await dbContext.ItemWarehouseStocks
            .Where(row =>
                row.ItemId == item.Id &&
                row.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(row => row.Quantity)
            .SingleAsync());
        Assert.Empty(await dbContext.InventoryLedgerEntries
            .Where(row => row.SourceDocumentId == latestId)
            .ToListAsync());
        Assert.Equal(-1m, await dbContext.InventoryLedgerEntries
            .Where(row => row.SourceDocumentId == previousId)
            .Select(row => row.QuantityDelta)
            .SingleAsync());
    }

    [Fact]
    public async Task DirectInvoiceDelete_DuplicateLatest_AppliesCombinedBeforeAfterStockDeltaAndLedgerParity()
    {
        var currentUser = CreateInvoiceUser("direct-duplicate-latest-delete");
        await using var dbContext = CreateDbContext(currentUser);
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet);
        var item = CreateItem(ItemTrackingTypes.Stock, 7m);
        var versionGroupId = Guid.NewGuid();
        var previousId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var previous = CreateInvoice(previousId, customer, item, 1m, ItemTrackingTypes.Stock);
        previous.VersionGroupId = versionGroupId;
        previous.VersionNumber = 1;
        previous.IsLatestVersion = true;
        var target = CreateInvoice(targetId, customer, item, 2m, ItemTrackingTypes.Stock);
        target.VersionGroupId = versionGroupId;
        target.VersionNumber = 2;
        target.PreviousVersionId = previousId;
        target.IsLatestVersion = true;

        dbContext.Customers.Add(customer);
        dbContext.Items.Add(item);
        dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
        {
            ItemId = item.Id,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 7m
        });
        dbContext.Invoices.AddRange(previous, target);
        await dbContext.SaveChangesAsync();
        await new InventoryLedgerService(dbContext).RebuildAsync(CancellationToken.None);
        dbContext.ChangeTracker.Clear();

        Assert.Equal(-3m, (await dbContext.InventoryLedgerEntries
            .Where(row => row.SourceDocumentId == previousId || row.SourceDocumentId == targetId)
            .Select(row => row.QuantityDelta)
            .ToListAsync()).Sum());
        var storedTarget = await dbContext.Invoices.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(row => row.Id == targetId);

        var response = await CreateInvoicesController(dbContext, currentUser).Delete(
            targetId,
            storedTarget.Revision,
            CancellationToken.None);

        Assert.IsType<NoContentResult>(response);
        dbContext.ChangeTracker.Clear();
        var storedVersions = await dbContext.Invoices.IgnoreQueryFilters()
            .Where(row => row.Id == previousId || row.Id == targetId)
            .ToDictionaryAsync(row => row.Id);
        Assert.True(storedVersions[previousId].IsLatestVersion);
        Assert.False(storedVersions[previousId].IsDeleted);
        Assert.False(storedVersions[targetId].IsLatestVersion);
        Assert.True(storedVersions[targetId].IsDeleted);
        Assert.Equal(9m, await dbContext.Items.IgnoreQueryFilters()
            .Where(row => row.Id == item.Id)
            .Select(row => row.CurrentStock)
            .SingleAsync());
        Assert.Equal(9m, await dbContext.ItemWarehouseStocks
            .Where(row =>
                row.ItemId == item.Id &&
                row.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(row => row.Quantity)
            .SingleAsync());
        var ledgerRows = await dbContext.InventoryLedgerEntries
            .Where(row => row.SourceDocumentId == previousId || row.SourceDocumentId == targetId)
            .ToListAsync();
        var ledgerRow = Assert.Single(ledgerRows);
        Assert.Equal(previousId, ledgerRow.SourceDocumentId);
        Assert.Equal(-1m, ledgerRow.QuantityDelta);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DirectInvoiceDelete_FailsClosed_WhenStockEffectWarehouseOutsideWritableScope(
        bool foreignWarehouseOnTarget)
    {
        var currentUser = CreateInvoiceUser("direct-version-warehouse-denial");
        await using var dbContext = CreateDbContext(currentUser);
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet);
        var item = CreateItem(ItemTrackingTypes.Stock, 17m);
        var versionGroupId = Guid.NewGuid();
        var previousId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var previous = CreateInvoice(previousId, customer, item, 1m, ItemTrackingTypes.Stock);
        previous.VersionGroupId = versionGroupId;
        previous.VersionNumber = 1;
        previous.IsLatestVersion = false;
        previous.SourceWarehouseCode = foreignWarehouseOnTarget
            ? OfficeCodeCatalog.UsenetMainWarehouse
            : OfficeCodeCatalog.ItworldMainWarehouse;
        var target = CreateInvoice(targetId, customer, item, 2m, ItemTrackingTypes.Stock);
        target.VersionGroupId = versionGroupId;
        target.VersionNumber = 2;
        target.PreviousVersionId = previousId;
        target.IsLatestVersion = true;
        target.SourceWarehouseCode = foreignWarehouseOnTarget
            ? OfficeCodeCatalog.ItworldMainWarehouse
            : OfficeCodeCatalog.UsenetMainWarehouse;

        dbContext.Customers.Add(customer);
        dbContext.Items.Add(item);
        dbContext.ItemWarehouseStocks.AddRange(
            new ItemWarehouseStock
            {
                ItemId = item.Id,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 10m
            },
            new ItemWarehouseStock
            {
                ItemId = item.Id,
                WarehouseCode = OfficeCodeCatalog.ItworldMainWarehouse,
                Quantity = 7m
            });
        dbContext.Invoices.AddRange(previous, target);
        await dbContext.SaveChangesAsync();
        await new InventoryLedgerService(dbContext).RebuildAsync(CancellationToken.None);
        dbContext.ChangeTracker.Clear();

        var beforeVersions = await dbContext.Invoices.IgnoreQueryFilters()
            .Where(row => row.Id == previousId || row.Id == targetId)
            .AsNoTracking()
            .ToDictionaryAsync(row => row.Id);
        var beforeLedger = Assert.Single(await dbContext.InventoryLedgerEntries
            .Where(row => row.SourceDocumentId == targetId)
            .AsNoTracking()
            .ToListAsync());

        var response = await CreateInvoicesController(dbContext, currentUser).Delete(
            targetId,
            beforeVersions[targetId].Revision,
            CancellationToken.None);

        Assert.IsType<ForbidResult>(response);
        dbContext.ChangeTracker.Clear();
        var unchangedVersions = await dbContext.Invoices.IgnoreQueryFilters()
            .Where(row => row.Id == previousId || row.Id == targetId)
            .Include(row => row.Lines)
            .ToDictionaryAsync(row => row.Id);
        Assert.False(unchangedVersions[previousId].IsLatestVersion);
        Assert.False(unchangedVersions[previousId].IsDeleted);
        Assert.Equal(beforeVersions[previousId].Revision, unchangedVersions[previousId].Revision);
        Assert.True(unchangedVersions[targetId].IsLatestVersion);
        Assert.False(unchangedVersions[targetId].IsDeleted);
        Assert.Equal(beforeVersions[targetId].Revision, unchangedVersions[targetId].Revision);
        Assert.All(unchangedVersions.Values.SelectMany(row => row.Lines), line => Assert.False(line.IsDeleted));
        Assert.Equal(17m, await dbContext.Items.IgnoreQueryFilters()
            .Where(row => row.Id == item.Id)
            .Select(row => row.CurrentStock)
            .SingleAsync());
        Assert.Equal(10m, await dbContext.ItemWarehouseStocks
            .Where(row =>
                row.ItemId == item.Id &&
                row.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(row => row.Quantity)
            .SingleAsync());
        Assert.Equal(7m, await dbContext.ItemWarehouseStocks
            .Where(row =>
                row.ItemId == item.Id &&
                row.WarehouseCode == OfficeCodeCatalog.ItworldMainWarehouse)
            .Select(row => row.Quantity)
            .SingleAsync());
        var unchangedLedger = Assert.Single(await dbContext.InventoryLedgerEntries
            .Where(row => row.SourceDocumentId == targetId)
            .AsNoTracking()
            .ToListAsync());
        Assert.Equal(beforeLedger.WarehouseCode, unchangedLedger.WarehouseCode);
        Assert.Equal(beforeLedger.QuantityDelta, unchangedLedger.QuantityDelta);
    }

    [Fact]
    public async Task DirectInvoiceDelete_RecalculatesRentalTargets_ForEveryChangedLatestFlag()
    {
        var currentUser = CreateInvoiceUser("direct-version-rental-targets");
        await using var dbContext = CreateDbContext(currentUser);
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet);
        var item = CreateItem(ItemTrackingTypes.NonStock, 0m);
        var versionGroupId = Guid.NewGuid();
        var profileSpecs = new[]
        {
            (ProfileId: Guid.NewGuid(), MonthlyAmount: 100m),
            (ProfileId: Guid.NewGuid(), MonthlyAmount: 200m),
            (ProfileId: Guid.NewGuid(), MonthlyAmount: 300m),
            (ProfileId: Guid.NewGuid(), MonthlyAmount: 400m)
        };
        var invoiceSpecs = new[]
        {
            (InvoiceId: Guid.NewGuid(), Version: 4, Latest: true, Profile: profileSpecs[0]),
            (InvoiceId: Guid.NewGuid(), Version: 3, Latest: false, Profile: profileSpecs[1]),
            (InvoiceId: Guid.NewGuid(), Version: 2, Latest: true, Profile: profileSpecs[2]),
            (InvoiceId: Guid.NewGuid(), Version: 1, Latest: true, Profile: profileSpecs[3])
        };
        var invoices = invoiceSpecs
            .Select(spec =>
            {
                var invoice = CreateInvoice(
                    spec.InvoiceId,
                    customer,
                    item,
                    1m,
                    ItemTrackingTypes.NonStock);
                invoice.VersionGroupId = versionGroupId;
                invoice.VersionNumber = spec.Version;
                invoice.IsLatestVersion = spec.Latest;
                invoice.LinkedRentalBillingProfileId = spec.Profile.ProfileId;
                return invoice;
            })
            .ToList();

        dbContext.Customers.Add(customer);
        dbContext.Items.Add(item);
        dbContext.RentalBillingProfiles.AddRange(profileSpecs.Select(spec =>
            CreateRentalBillingProfile(customer, spec.ProfileId, spec.MonthlyAmount)));
        dbContext.Invoices.AddRange(invoices);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        var target = await dbContext.Invoices.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(row => row.Id == invoiceSpecs[0].InvoiceId);

        var response = await CreateInvoicesController(dbContext, currentUser).Delete(
            target.Id,
            target.Revision,
            CancellationToken.None);

        Assert.IsType<NoContentResult>(response);
        dbContext.ChangeTracker.Clear();
        var profiles = await dbContext.RentalBillingProfiles.IgnoreQueryFilters()
            .Where(row => profileSpecs.Select(spec => spec.ProfileId).Contains(row.Id))
            .ToDictionaryAsync(row => row.Id);
        foreach (var spec in profileSpecs)
        {
            Assert.Equal(0m, profiles[spec.ProfileId].SettledAmount);
            Assert.Equal(spec.MonthlyAmount, profiles[spec.ProfileId].OutstandingAmount);
            Assert.Equal("미완료", profiles[spec.ProfileId].CompletionStatus);
        }

        var storedInvoices = await dbContext.Invoices.IgnoreQueryFilters()
            .Where(row => invoiceSpecs.Select(spec => spec.InvoiceId).Contains(row.Id))
            .ToDictionaryAsync(row => row.Id);
        Assert.True(storedInvoices[invoiceSpecs[0].InvoiceId].IsDeleted);
        Assert.False(storedInvoices[invoiceSpecs[0].InvoiceId].IsLatestVersion);
        Assert.True(storedInvoices[invoiceSpecs[1].InvoiceId].IsLatestVersion);
        Assert.False(storedInvoices[invoiceSpecs[2].InvoiceId].IsLatestVersion);
        Assert.False(storedInvoices[invoiceSpecs[3].InvoiceId].IsLatestVersion);
    }

    [Fact]
    public async Task DirectInvoiceDelete_NonLatest_LeavesOtherFlagsStockAndLedgerUntouched()
    {
        var currentUser = CreateInvoiceUser("direct-version-nonlatest-delete");
        await using var dbContext = CreateDbContext(currentUser);
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet);
        var item = CreateItem(ItemTrackingTypes.Stock, 8m);
        var versionGroupId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var latestId = Guid.NewGuid();
        var target = CreateInvoice(targetId, customer, item, 1m, ItemTrackingTypes.Stock);
        target.VersionGroupId = versionGroupId;
        target.VersionNumber = 1;
        target.IsLatestVersion = false;
        var latest = CreateInvoice(latestId, customer, item, 2m, ItemTrackingTypes.Stock);
        latest.VersionGroupId = versionGroupId;
        latest.VersionNumber = 2;
        latest.PreviousVersionId = targetId;
        latest.IsLatestVersion = true;

        dbContext.Customers.Add(customer);
        dbContext.Items.Add(item);
        dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
        {
            ItemId = item.Id,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 8m
        });
        dbContext.Invoices.AddRange(target, latest);
        await dbContext.SaveChangesAsync();
        await new InventoryLedgerService(dbContext).RebuildAsync(CancellationToken.None);
        dbContext.ChangeTracker.Clear();
        var storedBefore = await dbContext.Invoices.IgnoreQueryFilters()
            .Where(row => row.Id == targetId || row.Id == latestId)
            .AsNoTracking()
            .ToDictionaryAsync(row => row.Id);

        var response = await CreateInvoicesController(dbContext, currentUser).Delete(
            targetId,
            storedBefore[targetId].Revision,
            CancellationToken.None);

        Assert.IsType<NoContentResult>(response);
        dbContext.ChangeTracker.Clear();
        var storedAfter = await dbContext.Invoices.IgnoreQueryFilters()
            .Where(row => row.Id == targetId || row.Id == latestId)
            .ToDictionaryAsync(row => row.Id);
        Assert.True(storedAfter[targetId].IsDeleted);
        Assert.False(storedAfter[targetId].IsLatestVersion);
        Assert.False(storedAfter[latestId].IsDeleted);
        Assert.True(storedAfter[latestId].IsLatestVersion);
        Assert.Equal(storedBefore[latestId].Revision, storedAfter[latestId].Revision);
        Assert.Equal(8m, await dbContext.Items.IgnoreQueryFilters()
            .Where(row => row.Id == item.Id)
            .Select(row => row.CurrentStock)
            .SingleAsync());
        Assert.Equal(8m, await dbContext.ItemWarehouseStocks
            .Where(row =>
                row.ItemId == item.Id &&
                row.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(row => row.Quantity)
            .SingleAsync());
        var ledgerRow = Assert.Single(await dbContext.InventoryLedgerEntries
            .Where(row => row.SourceDocumentId == targetId || row.SourceDocumentId == latestId)
            .ToListAsync());
        Assert.Equal(latestId, ledgerRow.SourceDocumentId);
        Assert.Equal(-2m, ledgerRow.QuantityDelta);
    }

    [Fact]
    public async Task DirectInvoiceDelete_EffectiveEmptyGroup_IgnoresDeletedHighVersion()
    {
        var currentUser = CreateInvoiceUser("direct-version-empty-group-delete");
        await using var dbContext = CreateDbContext(currentUser);
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet);
        var item = CreateItem(ItemTrackingTypes.Stock, 8m);
        var targetId = Guid.NewGuid();
        var previousId = Guid.NewGuid();
        var deletedHighId = Guid.NewGuid();
        var previous = CreateInvoice(previousId, customer, item, 1m, ItemTrackingTypes.Stock);
        previous.VersionGroupId = targetId;
        previous.VersionNumber = 1;
        previous.IsLatestVersion = false;
        var target = CreateInvoice(targetId, customer, item, 2m, ItemTrackingTypes.Stock);
        target.VersionGroupId = Guid.Empty;
        target.VersionNumber = 2;
        target.PreviousVersionId = previousId;
        target.IsLatestVersion = true;
        var deletedHigh = CreateInvoice(deletedHighId, customer, item, 9m, ItemTrackingTypes.Stock);
        deletedHigh.VersionGroupId = targetId;
        deletedHigh.VersionNumber = 99;
        deletedHigh.IsDeleted = true;
        deletedHigh.IsLatestVersion = true;
        foreach (var line in deletedHigh.Lines)
            line.IsDeleted = true;

        dbContext.Customers.Add(customer);
        dbContext.Items.Add(item);
        dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
        {
            ItemId = item.Id,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 8m
        });
        dbContext.Invoices.AddRange(previous, target, deletedHigh);
        await dbContext.SaveChangesAsync();
        await new InventoryLedgerService(dbContext).RebuildAsync(CancellationToken.None);
        dbContext.ChangeTracker.Clear();
        var before = await dbContext.Invoices.IgnoreQueryFilters()
            .Where(row => row.Id == targetId || row.Id == deletedHighId)
            .AsNoTracking()
            .ToDictionaryAsync(row => row.Id);

        var response = await CreateInvoicesController(dbContext, currentUser).Delete(
            targetId,
            before[targetId].Revision,
            CancellationToken.None);

        Assert.IsType<NoContentResult>(response);
        dbContext.ChangeTracker.Clear();
        var stored = await dbContext.Invoices.IgnoreQueryFilters()
            .Where(row => row.Id == previousId || row.Id == targetId || row.Id == deletedHighId)
            .ToDictionaryAsync(row => row.Id);
        Assert.True(stored[previousId].IsLatestVersion);
        Assert.False(stored[previousId].IsDeleted);
        Assert.True(stored[targetId].IsDeleted);
        Assert.False(stored[targetId].IsLatestVersion);
        Assert.True(stored[deletedHighId].IsDeleted);
        Assert.True(stored[deletedHighId].IsLatestVersion);
        Assert.Equal(before[deletedHighId].Revision, stored[deletedHighId].Revision);
        Assert.Equal(9m, await dbContext.ItemWarehouseStocks
            .Where(row =>
                row.ItemId == item.Id &&
                row.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(row => row.Quantity)
            .SingleAsync());
        var ledgerRow = Assert.Single(await dbContext.InventoryLedgerEntries
            .Where(row =>
                row.SourceDocumentId == previousId ||
                row.SourceDocumentId == targetId ||
                row.SourceDocumentId == deletedHighId)
            .ToListAsync());
        Assert.Equal(previousId, ledgerRow.SourceDocumentId);
        Assert.Equal(-1m, ledgerRow.QuantityDelta);
    }

    [Fact]
    public async Task DirectInvoiceDelete_UsesCustomerScopeFallback_ForLegacyBlankPreviousVersion()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);
        var customer = CreateCustomer(OfficeCodeCatalog.Itworld);
        customer.TenantCode = TenantScopeCatalog.Itworld;
        var item = CreateItem(ItemTrackingTypes.NonStock, 0m);
        var versionGroupId = Guid.NewGuid();
        var previousId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var previous = CreateInvoice(
            previousId,
            customer,
            item,
            1m,
            ItemTrackingTypes.NonStock);
        previous.VersionGroupId = versionGroupId;
        previous.VersionNumber = 1;
        previous.IsLatestVersion = false;
        previous.TenantCode = string.Empty;
        previous.OfficeCode = string.Empty;
        previous.ResponsibleOfficeCode = string.Empty;
        previous.SourceWarehouseCode = OfficeCodeCatalog.ItworldMainWarehouse;
        var target = CreateInvoice(
            targetId,
            customer,
            item,
            1m,
            ItemTrackingTypes.NonStock);
        target.VersionGroupId = versionGroupId;
        target.VersionNumber = 2;
        target.PreviousVersionId = previousId;
        target.IsLatestVersion = true;
        target.TenantCode = TenantScopeCatalog.Itworld;
        target.OfficeCode = OfficeCodeCatalog.Itworld;
        target.ResponsibleOfficeCode = OfficeCodeCatalog.Itworld;
        target.SourceWarehouseCode = OfficeCodeCatalog.ItworldMainWarehouse;

        dbContext.Customers.Add(customer);
        dbContext.Items.Add(item);
        dbContext.Invoices.AddRange(previous, target);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        var storedTarget = await dbContext.Invoices.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(row => row.Id == targetId);

        var response = await CreateInvoicesController(dbContext, currentUser).Delete(
            targetId,
            storedTarget.Revision,
            CancellationToken.None);

        Assert.IsType<NoContentResult>(response);
        dbContext.ChangeTracker.Clear();
        var storedVersions = await dbContext.Invoices.IgnoreQueryFilters()
            .Where(row => row.Id == previousId || row.Id == targetId)
            .ToDictionaryAsync(row => row.Id);
        Assert.True(storedVersions[targetId].IsDeleted);
        Assert.False(storedVersions[targetId].IsLatestVersion);
        Assert.False(storedVersions[previousId].IsDeleted);
        Assert.True(storedVersions[previousId].IsLatestVersion);
    }

    [Fact]
    public async Task DirectInvoiceDelete_LegacyBlankScope_UsesCustomerScopeForWriteAuthorization()
    {
        var currentUser = CreateInvoiceUser("direct-version-customer-scope-authorization");
        await using var dbContext = CreateDbContext(currentUser);
        var customer = CreateCustomer(OfficeCodeCatalog.Itworld);
        customer.TenantCode = TenantScopeCatalog.Itworld;
        var item = CreateItem(ItemTrackingTypes.NonStock, 0m);
        var invoiceId = Guid.NewGuid();
        var invoice = CreateInvoice(
            invoiceId,
            customer,
            item,
            1m,
            ItemTrackingTypes.NonStock);
        invoice.TenantCode = string.Empty;
        invoice.OfficeCode = string.Empty;
        invoice.ResponsibleOfficeCode = string.Empty;
        invoice.SourceWarehouseCode = OfficeCodeCatalog.ItworldMainWarehouse;

        dbContext.Customers.Add(customer);
        dbContext.Items.Add(item);
        dbContext.Invoices.Add(invoice);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        var before = await dbContext.Invoices.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(row => row.Id == invoiceId);

        var response = await CreateInvoicesController(dbContext, currentUser).Delete(
            invoiceId,
            before.Revision,
            CancellationToken.None);

        Assert.IsType<ForbidResult>(response);
        dbContext.ChangeTracker.Clear();
        var stored = await dbContext.Invoices.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(row => row.Id == invoiceId);
        Assert.False(stored.IsDeleted);
        Assert.True(stored.IsLatestVersion);
        Assert.Equal(before.Revision, stored.Revision);
    }

    [Fact]
    public async Task DirectInvoiceDelete_LegacyBlankScopeWithPayment_AllowsLinkedCustomerOfficeWriter()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "direct-version-customer-payment-scope",
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = [PermissionNames.InvoiceEdit, PermissionNames.PaymentEdit]
        };
        await using var dbContext = CreateDbContext(currentUser);
        var customer = CreateCustomer(OfficeCodeCatalog.Itworld);
        var item = CreateItem(ItemTrackingTypes.NonStock, 0m);
        item.TenantCode = TenantScopeCatalog.Itworld;
        item.OfficeCode = OfficeCodeCatalog.Itworld;
        var invoiceId = Guid.NewGuid();
        var invoice = CreateInvoice(
            invoiceId,
            customer,
            item,
            1m,
            ItemTrackingTypes.NonStock);
        invoice.TenantCode = string.Empty;
        invoice.OfficeCode = string.Empty;
        invoice.ResponsibleOfficeCode = string.Empty;
        invoice.SourceWarehouseCode = OfficeCodeCatalog.ItworldMainWarehouse;
        var paymentId = Guid.NewGuid();

        dbContext.Customers.Add(customer);
        dbContext.Items.Add(item);
        dbContext.Invoices.Add(invoice);
        dbContext.Payments.Add(new Payment
        {
            Id = paymentId,
            InvoiceId = invoiceId,
            PaymentDate = new DateOnly(2026, 7, 31),
            Amount = 50m,
            Note = "legacy-customer-scope-payment"
        });
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        var before = await dbContext.Invoices.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(row => row.Id == invoiceId);

        var response = await CreateInvoicesController(dbContext, currentUser).Delete(
            invoiceId,
            before.Revision,
            CancellationToken.None);

        Assert.IsType<NoContentResult>(response);
        dbContext.ChangeTracker.Clear();
        var stored = await dbContext.Invoices.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(row => row.Id == invoiceId);
        Assert.True(stored.IsDeleted);
        Assert.False(stored.IsLatestVersion);
        Assert.True(await dbContext.Payments.IgnoreQueryFilters()
            .Where(row => row.Id == paymentId)
            .Select(row => row.IsDeleted)
            .SingleAsync());
    }

    [Fact]
    public async Task DirectInvoiceDelete_EqualVersionPromotion_UsesStableIdTieBreakInsteadOfMutableTimestamp()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet);
        var item = CreateItem(ItemTrackingTypes.NonStock, 0m);
        var versionGroupId = Guid.NewGuid();
        var lowerTieId = Guid.Parse("40000000-0000-0000-0000-000000000001");
        var higherTieId = Guid.Parse("50000000-0000-0000-0000-000000000001");
        var targetId = Guid.NewGuid();
        var lowerTie = CreateInvoice(
            lowerTieId,
            customer,
            item,
            1m,
            ItemTrackingTypes.NonStock);
        lowerTie.VersionGroupId = versionGroupId;
        lowerTie.VersionNumber = 2;
        lowerTie.IsLatestVersion = false;
        lowerTie.UpdatedAtUtc = new DateTime(2026, 7, 31, 3, 0, 0, DateTimeKind.Utc);
        var higherTie = CreateInvoice(
            higherTieId,
            customer,
            item,
            1m,
            ItemTrackingTypes.NonStock);
        higherTie.VersionGroupId = versionGroupId;
        higherTie.VersionNumber = 2;
        higherTie.IsLatestVersion = false;
        higherTie.UpdatedAtUtc = new DateTime(2026, 7, 31, 2, 0, 0, DateTimeKind.Utc);
        var target = CreateInvoice(
            targetId,
            customer,
            item,
            1m,
            ItemTrackingTypes.NonStock);
        target.VersionGroupId = versionGroupId;
        target.VersionNumber = 3;
        target.IsLatestVersion = true;

        dbContext.Customers.Add(customer);
        dbContext.Items.Add(item);
        dbContext.Invoices.AddRange(lowerTie, higherTie, target);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        var storedTarget = await dbContext.Invoices.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(row => row.Id == targetId);

        var response = await CreateInvoicesController(dbContext, currentUser).Delete(
            targetId,
            storedTarget.Revision,
            CancellationToken.None);

        Assert.IsType<NoContentResult>(response);
        dbContext.ChangeTracker.Clear();
        var latest = Assert.Single(
            await dbContext.Invoices.IgnoreQueryFilters()
                .Where(row =>
                    row.VersionGroupId == versionGroupId &&
                    !row.IsDeleted &&
                    row.IsLatestVersion)
                .ToListAsync());
        Assert.Equal(higherTieId, latest.Id);
    }

    [Fact]
    public async Task DirectInvoiceDelete_FailsClosed_WhenIndirectDemotedVersionHasPaymentAndPaymentEditMissing()
    {
        var currentUser = CreateInvoiceUser("direct-version-indirect-payment");
        await using var dbContext = CreateDbContext(currentUser);
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet);
        var item = CreateItem(ItemTrackingTypes.NonStock, 0m);
        var versionGroupId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var promotedId = Guid.NewGuid();
        var demotedId = Guid.NewGuid();
        var target = CreateInvoice(targetId, customer, item, 1m, ItemTrackingTypes.NonStock);
        target.VersionGroupId = versionGroupId;
        target.VersionNumber = 3;
        target.IsLatestVersion = true;
        var promoted = CreateInvoice(promotedId, customer, item, 1m, ItemTrackingTypes.NonStock);
        promoted.VersionGroupId = versionGroupId;
        promoted.VersionNumber = 2;
        promoted.IsLatestVersion = false;
        var demoted = CreateInvoice(demotedId, customer, item, 1m, ItemTrackingTypes.NonStock);
        demoted.VersionGroupId = versionGroupId;
        demoted.VersionNumber = 1;
        demoted.IsLatestVersion = true;
        var paymentId = Guid.NewGuid();

        dbContext.Customers.Add(customer);
        dbContext.Items.Add(item);
        dbContext.Invoices.AddRange(target, promoted, demoted);
        dbContext.Payments.Add(new Payment
        {
            Id = paymentId,
            InvoiceId = demotedId,
            PaymentDate = new DateOnly(2026, 7, 30),
            Amount = 50m,
            Note = "indirect latest demotion"
        });
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        var storedTarget = await dbContext.Invoices.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(row => row.Id == targetId);

        var response = await CreateInvoicesController(dbContext, currentUser).Delete(
            targetId,
            storedTarget.Revision,
            CancellationToken.None);

        Assert.IsType<ForbidResult>(response);
        dbContext.ChangeTracker.Clear();
        Assert.False(await dbContext.Invoices.IgnoreQueryFilters()
            .Where(row => row.Id == targetId)
            .Select(row => row.IsDeleted)
            .SingleAsync());
        Assert.True(await dbContext.Invoices.IgnoreQueryFilters()
            .Where(row => row.Id == targetId)
            .Select(row => row.IsLatestVersion)
            .SingleAsync());
        Assert.False(await dbContext.Invoices.IgnoreQueryFilters()
            .Where(row => row.Id == promotedId)
            .Select(row => row.IsLatestVersion)
            .SingleAsync());
        Assert.True(await dbContext.Invoices.IgnoreQueryFilters()
            .Where(row => row.Id == demotedId)
            .Select(row => row.IsLatestVersion)
            .SingleAsync());
        Assert.False(await dbContext.Payments.IgnoreQueryFilters()
            .Where(row => row.Id == paymentId)
            .Select(row => row.IsDeleted)
            .SingleAsync());
    }

    [Fact]
    public async Task DirectInvoiceDelete_FailsClosed_WhenLinkedPaymentIsOutsidePaymentWriteArea()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "direct-version-payment-area",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = [PermissionNames.InvoiceEdit, PermissionNames.PaymentEdit]
        };
        await using var dbContext = CreateDbContext(currentUser);
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet);
        var item = CreateItem(ItemTrackingTypes.NonStock, 0m);
        item.OfficeCode = OfficeCodeCatalog.Yeonsu;
        var invoiceId = Guid.NewGuid();
        var invoice = CreateInvoice(
            invoiceId,
            customer,
            item,
            1m,
            ItemTrackingTypes.NonStock);
        invoice.SourceWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse;
        var paymentId = Guid.NewGuid();

        dbContext.DataSharingPolicies.Add(new DataSharingPolicy
        {
            Id = Guid.NewGuid(),
            SourceTenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Usenet,
            TargetTenantCode = TenantScopeCatalog.UsenetGroup,
            TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
            ShareCustomers = true,
            ShareItems = true,
            ShareInvoices = true,
            SharePayments = false,
            AllowTargetWrite = true,
            IsActive = true
        });
        dbContext.Customers.Add(customer);
        dbContext.Items.Add(item);
        dbContext.Invoices.Add(invoice);
        dbContext.Payments.Add(new Payment
        {
            Id = paymentId,
            InvoiceId = invoiceId,
            PaymentDate = new DateOnly(2026, 7, 31),
            Amount = 50m,
            Note = "payment-area-preflight"
        });
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        var beforeInvoice = await dbContext.Invoices.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(row => row.Id == invoiceId);

        var response = await CreateInvoicesController(dbContext, currentUser).Delete(
            invoiceId,
            beforeInvoice.Revision,
            CancellationToken.None);

        Assert.IsType<ForbidResult>(response);
        dbContext.ChangeTracker.Clear();
        var storedInvoice = await dbContext.Invoices.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(row => row.Id == invoiceId);
        Assert.False(storedInvoice.IsDeleted);
        Assert.True(storedInvoice.IsLatestVersion);
        Assert.Equal(beforeInvoice.Revision, storedInvoice.Revision);
        Assert.False(await dbContext.Payments.IgnoreQueryFilters()
            .Where(row => row.Id == paymentId)
            .Select(row => row.IsDeleted)
            .SingleAsync());
        Assert.Empty(await dbContext.InventoryLedgerEntries.ToListAsync());
    }

    [Fact]
    public async Task DirectInvoiceDelete_FailsClosed_WhenIndirectDemotedVersionHasOutOfScopeTransaction()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "direct-version-indirect-transaction",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = [PermissionNames.InvoiceEdit, PermissionNames.PaymentEdit]
        };
        await using var dbContext = CreateDbContext(currentUser);
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet);
        var item = CreateItem(ItemTrackingTypes.NonStock, 0m);
        var versionGroupId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var promotedId = Guid.NewGuid();
        var demotedId = Guid.NewGuid();
        var target = CreateInvoice(targetId, customer, item, 1m, ItemTrackingTypes.NonStock);
        target.VersionGroupId = versionGroupId;
        target.VersionNumber = 3;
        target.IsLatestVersion = true;
        var promoted = CreateInvoice(promotedId, customer, item, 1m, ItemTrackingTypes.NonStock);
        promoted.VersionGroupId = versionGroupId;
        promoted.VersionNumber = 2;
        promoted.IsLatestVersion = false;
        var demoted = CreateInvoice(demotedId, customer, item, 1m, ItemTrackingTypes.NonStock);
        demoted.VersionGroupId = versionGroupId;
        demoted.VersionNumber = 1;
        demoted.IsLatestVersion = true;
        var transactionId = Guid.NewGuid();

        dbContext.Customers.Add(customer);
        dbContext.Items.Add(item);
        dbContext.Invoices.AddRange(target, promoted, demoted);
        dbContext.Transactions.Add(new TransactionRecord
        {
            Id = transactionId,
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
            TransactionDate = new DateOnly(2026, 7, 30),
            TransactionKind = "Payment",
            LinkedInvoiceId = demotedId,
            LinkedInvoiceNumber = demoted.InvoiceNumber,
            SettlementAmount = 50m
        });
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        var storedTarget = await dbContext.Invoices.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(row => row.Id == targetId);

        var response = await CreateInvoicesController(dbContext, currentUser).Delete(
            targetId,
            storedTarget.Revision,
            CancellationToken.None);

        Assert.IsType<ForbidResult>(response);
        dbContext.ChangeTracker.Clear();
        Assert.False(await dbContext.Invoices.IgnoreQueryFilters()
            .Where(row => row.Id == targetId)
            .Select(row => row.IsDeleted)
            .SingleAsync());
        Assert.True(await dbContext.Invoices.IgnoreQueryFilters()
            .Where(row => row.Id == targetId)
            .Select(row => row.IsLatestVersion)
            .SingleAsync());
        Assert.False(await dbContext.Invoices.IgnoreQueryFilters()
            .Where(row => row.Id == promotedId)
            .Select(row => row.IsLatestVersion)
            .SingleAsync());
        Assert.True(await dbContext.Invoices.IgnoreQueryFilters()
            .Where(row => row.Id == demotedId)
            .Select(row => row.IsLatestVersion)
            .SingleAsync());
        var unchangedTransaction = await dbContext.Transactions.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(row => row.Id == transactionId);
        Assert.Equal(demotedId, unchangedTransaction.LinkedInvoiceId);
        Assert.False(unchangedTransaction.IsDeleted);
    }

    [Fact]
    public async Task DirectInvoiceDelete_IgnoresDeletedForeignTransactionOnFlagOnlyVersion()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "direct-version-deleted-indirect-transaction",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = [PermissionNames.InvoiceEdit, PermissionNames.PaymentEdit]
        };
        await using var dbContext = CreateDbContext(currentUser);
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet);
        var item = CreateItem(ItemTrackingTypes.NonStock, 0m);
        var versionGroupId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var promotedId = Guid.NewGuid();
        var demotedId = Guid.NewGuid();
        var target = CreateInvoice(targetId, customer, item, 1m, ItemTrackingTypes.NonStock);
        target.VersionGroupId = versionGroupId;
        target.VersionNumber = 3;
        target.IsLatestVersion = true;
        var promoted = CreateInvoice(promotedId, customer, item, 1m, ItemTrackingTypes.NonStock);
        promoted.VersionGroupId = versionGroupId;
        promoted.VersionNumber = 2;
        promoted.IsLatestVersion = false;
        var demoted = CreateInvoice(demotedId, customer, item, 1m, ItemTrackingTypes.NonStock);
        demoted.VersionGroupId = versionGroupId;
        demoted.VersionNumber = 1;
        demoted.IsLatestVersion = true;
        var transactionId = Guid.NewGuid();

        dbContext.Customers.Add(customer);
        dbContext.Items.Add(item);
        dbContext.Invoices.AddRange(target, promoted, demoted);
        dbContext.Transactions.Add(new TransactionRecord
        {
            Id = transactionId,
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
            TransactionDate = new DateOnly(2026, 7, 30),
            TransactionKind = "Payment",
            LinkedInvoiceId = demotedId,
            LinkedInvoiceNumber = demoted.InvoiceNumber,
            SettlementAmount = 50m,
            IsDeleted = true
        });
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        var storedTarget = await dbContext.Invoices.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(row => row.Id == targetId);

        var response = await CreateInvoicesController(dbContext, currentUser).Delete(
            targetId,
            storedTarget.Revision,
            CancellationToken.None);

        Assert.IsType<NoContentResult>(response);
        dbContext.ChangeTracker.Clear();
        var storedVersions = await dbContext.Invoices.IgnoreQueryFilters()
            .Where(row => row.Id == targetId || row.Id == promotedId || row.Id == demotedId)
            .ToDictionaryAsync(row => row.Id);
        Assert.True(storedVersions[targetId].IsDeleted);
        Assert.False(storedVersions[targetId].IsLatestVersion);
        Assert.True(storedVersions[promotedId].IsLatestVersion);
        Assert.False(storedVersions[demotedId].IsLatestVersion);
        var unchangedTransaction = await dbContext.Transactions.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(row => row.Id == transactionId);
        Assert.Equal(demotedId, unchangedTransaction.LinkedInvoiceId);
        Assert.True(unchangedTransaction.IsDeleted);
    }

    [Fact]
    public async Task DirectInvoiceUpdate_DuplicateLatest_NormalizesExactChainWithStableWinnerAndParity()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet);
        var foreignCustomer = CreateCustomer(OfficeCodeCatalog.Itworld);
        var item = CreateItem(ItemTrackingTypes.Stock, 8m);
        var versionGroupId = Guid.NewGuid();
        var targetId = Guid.Parse("61000000-0000-0000-0000-000000000001");
        var lowerTieId = Guid.Parse("62000000-0000-0000-0000-000000000001");
        var higherTieId = Guid.Parse("63000000-0000-0000-0000-000000000001");
        var targetProfileId = Guid.NewGuid();
        var lowerTieProfileId = Guid.NewGuid();
        var higherTieProfileId = Guid.NewGuid();

        var target = CreateInvoice(targetId, customer, item, 1m, ItemTrackingTypes.Stock);
        target.VersionGroupId = versionGroupId;
        target.VersionNumber = 1;
        target.IsLatestVersion = true;
        target.LinkedRentalBillingProfileId = targetProfileId;

        var lowerTie = CreateInvoice(lowerTieId, customer, item, 1m, ItemTrackingTypes.Stock);
        lowerTie.VersionGroupId = versionGroupId;
        lowerTie.VersionNumber = 2;
        lowerTie.IsLatestVersion = true;
        lowerTie.LinkedRentalBillingProfileId = lowerTieProfileId;
        lowerTie.UpdatedAtUtc = new DateTime(2026, 7, 31, 8, 0, 0, DateTimeKind.Utc);

        var higherTie = CreateInvoice(higherTieId, customer, item, 3m, ItemTrackingTypes.Stock);
        higherTie.VersionGroupId = versionGroupId;
        higherTie.VersionNumber = 2;
        higherTie.IsLatestVersion = false;
        higherTie.LinkedRentalBillingProfileId = higherTieProfileId;
        higherTie.UpdatedAtUtc = new DateTime(2026, 7, 31, 7, 0, 0, DateTimeKind.Utc);

        var foreignCollisionId = Guid.NewGuid();
        var foreignCollision = new Invoice
        {
            Id = foreignCollisionId,
            CustomerId = foreignCustomer.Id,
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
            InvoiceNumber = $"INV-{foreignCollisionId:N}"[..20],
            VersionGroupId = versionGroupId,
            VersionNumber = 99,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            SourceWarehouseCode = OfficeCodeCatalog.ItworldMainWarehouse,
            InvoiceDate = new DateOnly(2026, 7, 31)
        };

        dbContext.Customers.AddRange(customer, foreignCustomer);
        dbContext.Items.Add(item);
        dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
        {
            ItemId = item.Id,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 8m
        });
        dbContext.RentalBillingProfiles.AddRange(
            CreateRentalBillingProfile(customer, targetProfileId, 100m),
            CreateRentalBillingProfile(customer, lowerTieProfileId, 200m),
            CreateRentalBillingProfile(customer, higherTieProfileId, 300m));
        dbContext.Invoices.AddRange(target, lowerTie, higherTie, foreignCollision);
        await dbContext.SaveChangesAsync();
        await new InventoryLedgerService(dbContext).RebuildAsync(CancellationToken.None);
        var foreignRevision = foreignCollision.Revision;
        dbContext.ChangeTracker.Clear();

        var storedTarget = await dbContext.Invoices.IgnoreQueryFilters()
            .Include(row => row.Customer)
            .Include(row => row.Lines)
            .AsNoTracking()
            .SingleAsync(row => row.Id == targetId);
        var dto = storedTarget.ToDto();
        dto.ExpectedRevision = storedTarget.Revision;
        dto.Memo = "normalize duplicate latest on direct update";
        dto.MutationId = $"direct-update-normalize:Invoice:{targetId:N}";
        dto.MutationCreatedAtUtc = DateTime.UtcNow;

        var response = await CreateInvoicesController(dbContext, currentUser).Update(
            targetId,
            dto,
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(response.Result);
        dbContext.ChangeTracker.Clear();
        var versions = await dbContext.Invoices.IgnoreQueryFilters()
            .Where(row => row.Id == targetId || row.Id == lowerTieId || row.Id == higherTieId || row.Id == foreignCollisionId)
            .ToDictionaryAsync(row => row.Id);
        Assert.False(versions[targetId].IsLatestVersion);
        Assert.False(versions[lowerTieId].IsLatestVersion);
        Assert.True(versions[higherTieId].IsLatestVersion);
        Assert.Equal("normalize duplicate latest on direct update", versions[targetId].Memo);
        Assert.True(versions[foreignCollisionId].IsLatestVersion);
        Assert.Equal(foreignRevision, versions[foreignCollisionId].Revision);

        Assert.Equal(7m, await dbContext.Items.IgnoreQueryFilters()
            .Where(row => row.Id == item.Id)
            .Select(row => row.CurrentStock)
            .SingleAsync());
        Assert.Equal(7m, await dbContext.ItemWarehouseStocks
            .Where(row => row.ItemId == item.Id && row.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(row => row.Quantity)
            .SingleAsync());
        var ledger = Assert.Single(await dbContext.InventoryLedgerEntries
            .Where(row => row.SourceDocumentId == targetId ||
                          row.SourceDocumentId == lowerTieId ||
                          row.SourceDocumentId == higherTieId)
            .ToListAsync());
        Assert.Equal(higherTieId, ledger.SourceDocumentId);
        Assert.Equal(-3m, ledger.QuantityDelta);

        var profiles = await dbContext.RentalBillingProfiles.IgnoreQueryFilters()
            .Where(row => row.Id == targetProfileId || row.Id == lowerTieProfileId || row.Id == higherTieProfileId)
            .ToDictionaryAsync(row => row.Id);
        Assert.Equal(100m, profiles[targetProfileId].OutstandingAmount);
        Assert.Equal(200m, profiles[lowerTieProfileId].OutstandingAmount);
        Assert.Equal(300m, profiles[higherTieProfileId].OutstandingAmount);
    }

    [Fact]
    public async Task DirectInvoiceUpdate_CanonicalLatestMemo_NoOpsPaidNonLatestParticipant()
    {
        var currentUser = CreateInvoiceUser("direct-update-paid-nonlatest-noop");
        await using var dbContext = CreateDbContext(currentUser);
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet);
        var item = CreateItem(ItemTrackingTypes.NonStock, 0m);
        var versionGroupId = Guid.NewGuid();
        var paidPreviousId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var paidPrevious = CreateInvoice(paidPreviousId, customer, item, 1m, ItemTrackingTypes.NonStock);
        paidPrevious.VersionGroupId = versionGroupId;
        paidPrevious.VersionNumber = 1;
        paidPrevious.IsLatestVersion = false;
        var target = CreateInvoice(targetId, customer, item, 1m, ItemTrackingTypes.NonStock);
        target.VersionGroupId = versionGroupId;
        target.VersionNumber = 2;
        target.IsLatestVersion = true;
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            InvoiceId = paidPreviousId,
            PaymentDate = new DateOnly(2026, 7, 31),
            Amount = 25m,
            Note = "must remain untouched"
        };

        dbContext.Customers.Add(customer);
        dbContext.Items.Add(item);
        dbContext.Invoices.AddRange(paidPrevious, target);
        dbContext.Payments.Add(payment);
        await dbContext.SaveChangesAsync();
        var paidPreviousRevision = paidPrevious.Revision;
        var paymentRevision = payment.Revision;
        dbContext.ChangeTracker.Clear();

        var storedTarget = await dbContext.Invoices.IgnoreQueryFilters()
            .Include(row => row.Customer)
            .Include(row => row.Lines)
            .AsNoTracking()
            .SingleAsync(row => row.Id == targetId);
        var dto = storedTarget.ToDto();
        dto.ExpectedRevision = storedTarget.Revision;
        dto.Memo = "metadata-only update";

        var response = await CreateInvoicesController(dbContext, currentUser).Update(
            targetId,
            dto,
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(response.Result);
        dbContext.ChangeTracker.Clear();
        var unchangedPrevious = await dbContext.Invoices.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(row => row.Id == paidPreviousId);
        var unchangedPayment = await dbContext.Payments.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(row => row.Id == payment.Id);
        Assert.False(unchangedPrevious.IsLatestVersion);
        Assert.Equal(paidPreviousRevision, unchangedPrevious.Revision);
        Assert.False(unchangedPayment.IsDeleted);
        Assert.Equal(paymentRevision, unchangedPayment.Revision);
        Assert.Equal("metadata-only update", await dbContext.Invoices.IgnoreQueryFilters()
            .Where(row => row.Id == targetId)
            .Select(row => row.Memo)
            .SingleAsync());
    }

    [Fact]
    public async Task DirectInvoiceUpdate_DuplicateLatest_FailsClosedWhenFlagChangingParticipantHasPayment()
    {
        var currentUser = CreateInvoiceUser("direct-update-paid-flag-change");
        await using var dbContext = CreateDbContext(currentUser);
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet);
        var item = CreateItem(ItemTrackingTypes.NonStock, 0m);
        var versionGroupId = Guid.NewGuid();
        var paidDuplicateId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var paidDuplicate = CreateInvoice(paidDuplicateId, customer, item, 1m, ItemTrackingTypes.NonStock);
        paidDuplicate.VersionGroupId = versionGroupId;
        paidDuplicate.VersionNumber = 1;
        paidDuplicate.IsLatestVersion = true;
        var target = CreateInvoice(targetId, customer, item, 1m, ItemTrackingTypes.NonStock);
        target.VersionGroupId = versionGroupId;
        target.VersionNumber = 2;
        target.IsLatestVersion = true;
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            InvoiceId = paidDuplicateId,
            PaymentDate = new DateOnly(2026, 7, 31),
            Amount = 25m,
            Note = "requires Payment.Edit before latest demotion"
        };

        dbContext.Customers.Add(customer);
        dbContext.Items.Add(item);
        dbContext.Invoices.AddRange(paidDuplicate, target);
        dbContext.Payments.Add(payment);
        await dbContext.SaveChangesAsync();
        var paidDuplicateRevision = paidDuplicate.Revision;
        var targetRevision = target.Revision;
        var paymentRevision = payment.Revision;
        dbContext.ChangeTracker.Clear();

        var storedTarget = await dbContext.Invoices.IgnoreQueryFilters()
            .Include(row => row.Customer)
            .Include(row => row.Lines)
            .AsNoTracking()
            .SingleAsync(row => row.Id == targetId);
        var dto = storedTarget.ToDto();
        dto.ExpectedRevision = storedTarget.Revision;
        dto.Memo = "must not persist";

        var response = await CreateInvoicesController(dbContext, currentUser).Update(
            targetId,
            dto,
            CancellationToken.None);

        Assert.IsType<ForbidResult>(response.Result);
        dbContext.ChangeTracker.Clear();
        var unchangedVersions = await dbContext.Invoices.IgnoreQueryFilters()
            .Where(row => row.Id == paidDuplicateId || row.Id == targetId)
            .AsNoTracking()
            .ToDictionaryAsync(row => row.Id);
        Assert.True(unchangedVersions[paidDuplicateId].IsLatestVersion);
        Assert.Equal(paidDuplicateRevision, unchangedVersions[paidDuplicateId].Revision);
        Assert.True(unchangedVersions[targetId].IsLatestVersion);
        Assert.Equal(targetRevision, unchangedVersions[targetId].Revision);
        Assert.Equal(string.Empty, unchangedVersions[targetId].Memo);
        var unchangedPayment = await dbContext.Payments.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(row => row.Id == payment.Id);
        Assert.False(unchangedPayment.IsDeleted);
        Assert.Equal(paymentRevision, unchangedPayment.Revision);
    }

    [Fact]
    public async Task DirectInvoiceDelete_NonLatest_NormalizesDuplicateLatestInExactChain()
    {
        var currentUser = CreateInvoiceUser("direct-delete-nonlatest-normalize");
        await using var dbContext = CreateDbContext(currentUser);
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet);
        var item = CreateItem(ItemTrackingTypes.NonStock, 0m);
        var versionGroupId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var lowerLatestId = Guid.NewGuid();
        var winnerId = Guid.NewGuid();
        var target = CreateInvoice(targetId, customer, item, 1m, ItemTrackingTypes.NonStock);
        target.VersionGroupId = versionGroupId;
        target.VersionNumber = 1;
        target.IsLatestVersion = false;
        var lowerLatest = CreateInvoice(lowerLatestId, customer, item, 1m, ItemTrackingTypes.NonStock);
        lowerLatest.VersionGroupId = versionGroupId;
        lowerLatest.VersionNumber = 2;
        lowerLatest.IsLatestVersion = true;
        var winner = CreateInvoice(winnerId, customer, item, 1m, ItemTrackingTypes.NonStock);
        winner.VersionGroupId = versionGroupId;
        winner.VersionNumber = 3;
        winner.IsLatestVersion = true;

        dbContext.Customers.Add(customer);
        dbContext.Items.Add(item);
        dbContext.Invoices.AddRange(target, lowerLatest, winner);
        await dbContext.SaveChangesAsync();
        var targetRevision = target.Revision;
        dbContext.ChangeTracker.Clear();

        var response = await CreateInvoicesController(dbContext, currentUser).Delete(
            targetId,
            targetRevision,
            CancellationToken.None);

        Assert.IsType<NoContentResult>(response);
        dbContext.ChangeTracker.Clear();
        var versions = await dbContext.Invoices.IgnoreQueryFilters()
            .Where(row => row.Id == targetId || row.Id == lowerLatestId || row.Id == winnerId)
            .ToDictionaryAsync(row => row.Id);
        Assert.True(versions[targetId].IsDeleted);
        Assert.False(versions[targetId].IsLatestVersion);
        Assert.False(versions[lowerLatestId].IsLatestVersion);
        Assert.True(versions[winnerId].IsLatestVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvoiceDelete_DirectOrSync_PreservesAlreadyDeletedFinancialChildrenAndActiveAttachments(
        bool throughSync)
    {
        var currentUser = CreateInvoiceUser(throughSync
            ? "sync-delete-deleted-finance"
            : "direct-delete-deleted-finance");
        await using var dbContext = CreateDbContext(currentUser);
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet);
        var item = CreateItem(ItemTrackingTypes.NonStock, 0m);
        var invoiceId = Guid.NewGuid();
        var invoice = CreateInvoice(invoiceId, customer, item, 1m, ItemTrackingTypes.NonStock);
        var paymentId = Guid.NewGuid();
        var paymentAttachmentId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        var transactionAttachmentId = Guid.NewGuid();
        var payment = new Payment
        {
            Id = paymentId,
            InvoiceId = invoiceId,
            PaymentDate = new DateOnly(2026, 7, 31),
            Amount = 44m,
            Note = "deleted payment restore evidence",
            IsDeleted = true
        };
        var paymentAttachment = new PaymentAttachment
        {
            Id = paymentAttachmentId,
            PaymentId = paymentId,
            AttachmentType = "Evidence",
            FileName = "deleted-payment.pdf",
            MimeType = "application/pdf",
            FileSize = 4,
            FileHash = "deleted-payment-hash",
            Description = "active attachment retained for restore",
            StoragePath = "payment-attachments/deleted-payment.pdf",
            FileContent = [1, 2, 3, 4]
        };
        var transaction = new TransactionRecord
        {
            Id = transactionId,
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 7, 31),
            TransactionKind = "invoice payment",
            LinkedInvoiceId = invoiceId,
            LinkedInvoiceNumber = invoice.InvoiceNumber,
            SettlementAmount = 44m,
            Note = "deleted transaction restore evidence",
            Memo = "must remain linked",
            IsDeleted = true
        };
        var transactionAttachment = new TransactionAttachment
        {
            Id = transactionAttachmentId,
            TransactionId = transactionId,
            AttachmentType = "Evidence",
            FileName = "deleted-transaction.pdf",
            MimeType = "application/pdf",
            FileSize = 4,
            FileHash = "deleted-transaction-hash",
            Description = "active transaction attachment retained for restore",
            StoragePath = "transaction-attachments/deleted-transaction.pdf",
            FileContent = [5, 6, 7, 8]
        };

        dbContext.Customers.Add(customer);
        dbContext.Items.Add(item);
        dbContext.Invoices.Add(invoice);
        dbContext.Payments.Add(payment);
        dbContext.PaymentAttachments.Add(paymentAttachment);
        dbContext.Transactions.Add(transaction);
        dbContext.TransactionAttachments.Add(transactionAttachment);
        await dbContext.SaveChangesAsync();
        var invoiceRevision = invoice.Revision;
        var paymentRevision = payment.Revision;
        var paymentAttachmentRevision = paymentAttachment.Revision;
        var transactionRevision = transaction.Revision;
        var transactionAttachmentRevision = transactionAttachment.Revision;
        dbContext.ChangeTracker.Clear();

        if (throughSync)
        {
            var storedInvoice = await dbContext.Invoices.IgnoreQueryFilters()
                .Include(row => row.Customer)
                .Include(row => row.Lines)
                .AsNoTracking()
                .SingleAsync(row => row.Id == invoiceId);
            var dto = storedInvoice.ToDto();
            dto.IsDeleted = true;
            dto.ExpectedRevision = invoiceRevision;
            dto.MutationId = $"sync-delete-deleted-finance:Invoice:{invoiceId:N}";
            dto.MutationCreatedAtUtc = DateTime.UtcNow;
            var response = await CreateSyncController(dbContext, currentUser).Push(new SyncPushRequest
            {
                DeviceId = "sync-delete-deleted-finance",
                Invoices = [dto]
            }, CancellationToken.None);
            var ok = Assert.IsType<OkObjectResult>(response.Result);
            var result = Assert.IsType<SyncPushResult>(ok.Value);
            Assert.Equal(1, result.AcceptedCount);
            Assert.Equal(0, result.ConflictCount);
        }
        else
        {
            var response = await CreateInvoicesController(dbContext, currentUser).Delete(
                invoiceId,
                invoiceRevision,
                CancellationToken.None);
            Assert.IsType<NoContentResult>(response);
        }

        dbContext.ChangeTracker.Clear();
        var unchangedPayment = await dbContext.Payments.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(row => row.Id == paymentId);
        Assert.True(unchangedPayment.IsDeleted);
        Assert.Equal(invoiceId, unchangedPayment.InvoiceId);
        Assert.Equal(44m, unchangedPayment.Amount);
        Assert.Equal("deleted payment restore evidence", unchangedPayment.Note);
        Assert.Equal(paymentRevision, unchangedPayment.Revision);

        var unchangedPaymentAttachment = await dbContext.PaymentAttachments.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(row => row.Id == paymentAttachmentId);
        Assert.False(unchangedPaymentAttachment.IsDeleted);
        Assert.Equal(paymentId, unchangedPaymentAttachment.PaymentId);
        Assert.Equal("deleted-payment-hash", unchangedPaymentAttachment.FileHash);
        Assert.Equal(paymentAttachmentRevision, unchangedPaymentAttachment.Revision);

        var unchangedTransaction = await dbContext.Transactions.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(row => row.Id == transactionId);
        Assert.True(unchangedTransaction.IsDeleted);
        Assert.Equal(invoiceId, unchangedTransaction.LinkedInvoiceId);
        Assert.Equal(invoice.InvoiceNumber, unchangedTransaction.LinkedInvoiceNumber);
        Assert.Equal(44m, unchangedTransaction.SettlementAmount);
        Assert.Equal("invoice payment", unchangedTransaction.TransactionKind);
        Assert.Equal("must remain linked", unchangedTransaction.Memo);
        Assert.Equal(transactionRevision, unchangedTransaction.Revision);

        var unchangedTransactionAttachment = await dbContext.TransactionAttachments.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(row => row.Id == transactionAttachmentId);
        Assert.False(unchangedTransactionAttachment.IsDeleted);
        Assert.Equal(transactionId, unchangedTransactionAttachment.TransactionId);
        Assert.Equal("deleted-transaction-hash", unchangedTransactionAttachment.FileHash);
        Assert.Equal(transactionAttachmentRevision, unchangedTransactionAttachment.Revision);
    }

    [Fact]
    public async Task RentalSettlementRecalculation_MixedProfileAndSpecificRunTargets_IsInputOrderIndependent()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet);
        var item = CreateItem(ItemTrackingTypes.NonStock, 0m);
        var profileId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var profile = CreateRentalBillingProfile(customer, profileId, 100m);
        var initialRunsJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                RunId = runId,
                RunKey = "20260701-20260731",
                ScheduledDate = new DateOnly(2026, 7, 1),
                PeriodStartDate = new DateOnly(2026, 7, 1),
                PeriodEndDate = new DateOnly(2026, 7, 31),
                CycleMonths = 1,
                PeriodLabel = "2026-07",
                Status = "stale",
                BilledAmount = 300m,
                SettledAmount = 999m,
                SettlementStatus = "stale",
                SettledDate = (DateOnly?)null
            }
        });
        profile.BillingRunsJson = initialRunsJson;
        var invoice = CreateInvoice(invoiceId, customer, item, 1m, ItemTrackingTypes.NonStock);
        invoice.TotalAmount = 300m;
        invoice.SupplyAmount = 300m;
        invoice.LinkedRentalBillingProfileId = profileId;
        invoice.LinkedRentalBillingRunId = runId;
        dbContext.Customers.Add(customer);
        dbContext.Items.Add(item);
        dbContext.RentalBillingProfiles.Add(profile);
        dbContext.Invoices.Add(invoice);
        dbContext.Transactions.Add(new TransactionRecord
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 7, 15),
            TransactionKind = "rental payment",
            LinkedInvoiceId = invoiceId,
            LinkedInvoiceNumber = invoice.InvoiceNumber,
            LinkedRentalBillingProfileId = profileId,
            LinkedRentalBillingRunId = runId,
            SettlementAmount = 50m
        });
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var service = new RentalSettlementRecalculationService(dbContext);
        await service.RecalculateRentalSettlementsAsync(
            [(profileId, null), (profileId, runId)],
            CancellationToken.None);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        var nullThenSpecific = await LoadRentalProfileSettlementSnapshotAsync(dbContext, profileId);

        await ResetRentalProfileSettlementAsync(dbContext, profileId, initialRunsJson);
        await service.RecalculateRentalSettlementsAsync(
            [(profileId, runId), (profileId, null)],
            CancellationToken.None);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        var specificThenNull = await LoadRentalProfileSettlementSnapshotAsync(dbContext, profileId);

        Assert.Equal(nullThenSpecific, specificThenNull);
        Assert.Equal(50m, specificThenNull.SettledAmount);
        Assert.Equal(50m, specificThenNull.OutstandingAmount);
    }

    [Fact]
    public async Task RentalSettlementRecalculation_MultipleSpecificRuns_UsesNewestCanonicalRunRegardlessOfInputOrder()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet);
        var item = CreateItem(ItemTrackingTypes.NonStock, 0m);
        var profileId = Guid.NewGuid();
        var olderRunId = Guid.NewGuid();
        var newerRunId = Guid.NewGuid();
        var olderInvoiceId = Guid.NewGuid();
        var newerInvoiceId = Guid.NewGuid();
        var profile = CreateRentalBillingProfile(customer, profileId, 100m);
        var initialRunsJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                RunId = olderRunId,
                RunKey = "20260601-20260630",
                ScheduledDate = new DateOnly(2026, 6, 1),
                PeriodStartDate = new DateOnly(2026, 6, 1),
                PeriodEndDate = new DateOnly(2026, 6, 30),
                CycleMonths = 1,
                PeriodLabel = "2026-06",
                Status = "stale",
                BilledAmount = 120m,
                SettledAmount = 999m,
                SettlementStatus = "stale",
                SettledDate = (DateOnly?)null
            },
            new
            {
                RunId = newerRunId,
                RunKey = "20260701-20260731",
                ScheduledDate = new DateOnly(2026, 7, 1),
                PeriodStartDate = new DateOnly(2026, 7, 1),
                PeriodEndDate = new DateOnly(2026, 7, 31),
                CycleMonths = 1,
                PeriodLabel = "2026-07",
                Status = "stale",
                BilledAmount = 300m,
                SettledAmount = 999m,
                SettlementStatus = "stale",
                SettledDate = (DateOnly?)null
            }
        });
        profile.BillingRunsJson = initialRunsJson;
        var olderInvoice = CreateInvoice(olderInvoiceId, customer, item, 1m, ItemTrackingTypes.NonStock);
        olderInvoice.TotalAmount = 120m;
        olderInvoice.SupplyAmount = 120m;
        olderInvoice.LinkedRentalBillingProfileId = profileId;
        olderInvoice.LinkedRentalBillingRunId = olderRunId;
        var newerInvoice = CreateInvoice(newerInvoiceId, customer, item, 1m, ItemTrackingTypes.NonStock);
        newerInvoice.TotalAmount = 300m;
        newerInvoice.SupplyAmount = 300m;
        newerInvoice.LinkedRentalBillingProfileId = profileId;
        newerInvoice.LinkedRentalBillingRunId = newerRunId;

        dbContext.Customers.Add(customer);
        dbContext.Items.Add(item);
        dbContext.RentalBillingProfiles.Add(profile);
        dbContext.Invoices.AddRange(olderInvoice, newerInvoice);
        dbContext.Transactions.AddRange(
            CreateRentalSettlementTransaction(customer, olderInvoice, profileId, olderRunId, 20m),
            CreateRentalSettlementTransaction(customer, newerInvoice, profileId, newerRunId, 50m));
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var service = new RentalSettlementRecalculationService(dbContext);
        await service.RecalculateRentalSettlementsAsync(
            [(profileId, olderRunId), (profileId, newerRunId)],
            CancellationToken.None);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        var olderThenNewer = await LoadRentalProfileSettlementSnapshotAsync(dbContext, profileId);

        await ResetRentalProfileSettlementAsync(dbContext, profileId, initialRunsJson);
        await service.RecalculateRentalSettlementsAsync(
            [(profileId, newerRunId), (profileId, olderRunId)],
            CancellationToken.None);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        var newerThenOlder = await LoadRentalProfileSettlementSnapshotAsync(dbContext, profileId);

        Assert.Equal(olderThenNewer, newerThenOlder);
        Assert.Equal(50m, newerThenOlder.SettledAmount);
        Assert.Equal(250m, newerThenOlder.OutstandingAmount);
    }

    [Fact]
    public async Task RentalSettlementRecalculation_RemovesEveryRequestedInactiveRunAndPreservesActiveRunRegardlessOfInputOrder()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet);
        var item = CreateItem(ItemTrackingTypes.NonStock, 0m);
        var profileId = Guid.NewGuid();
        var firstInactiveRunId = Guid.NewGuid();
        var secondInactiveRunId = Guid.NewGuid();
        var activeRunId = Guid.NewGuid();
        var activeInvoiceId = Guid.NewGuid();
        var profile = CreateRentalBillingProfile(customer, profileId, 100m);
        var initialRunsJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                RunId = secondInactiveRunId,
                RunKey = "20260601-20260630",
                ScheduledDate = new DateOnly(2026, 6, 1),
                PeriodStartDate = new DateOnly(2026, 6, 1),
                PeriodEndDate = new DateOnly(2026, 6, 30),
                CycleMonths = 1,
                PeriodLabel = "2026-06",
                Status = "stale",
                BilledAmount = 200m,
                SettledAmount = 999m,
                SettlementStatus = "stale",
                SettledDate = (DateOnly?)null
            },
            new
            {
                RunId = activeRunId,
                RunKey = "20260701-20260731",
                ScheduledDate = new DateOnly(2026, 7, 1),
                PeriodStartDate = new DateOnly(2026, 7, 1),
                PeriodEndDate = new DateOnly(2026, 7, 31),
                CycleMonths = 1,
                PeriodLabel = "2026-07",
                Status = "stale",
                BilledAmount = 300m,
                SettledAmount = 999m,
                SettlementStatus = "stale",
                SettledDate = (DateOnly?)null
            },
            new
            {
                RunId = firstInactiveRunId,
                RunKey = "20260501-20260531",
                ScheduledDate = new DateOnly(2026, 5, 1),
                PeriodStartDate = new DateOnly(2026, 5, 1),
                PeriodEndDate = new DateOnly(2026, 5, 31),
                CycleMonths = 1,
                PeriodLabel = "2026-05",
                Status = "stale",
                BilledAmount = 150m,
                SettledAmount = 999m,
                SettlementStatus = "stale",
                SettledDate = (DateOnly?)null
            }
        });
        profile.BillingRunsJson = initialRunsJson;
        var activeInvoice = CreateInvoice(
            activeInvoiceId,
            customer,
            item,
            1m,
            ItemTrackingTypes.NonStock);
        activeInvoice.TotalAmount = 300m;
        activeInvoice.SupplyAmount = 300m;
        activeInvoice.LinkedRentalBillingProfileId = profileId;
        activeInvoice.LinkedRentalBillingRunId = activeRunId;

        dbContext.Customers.Add(customer);
        dbContext.Items.Add(item);
        dbContext.RentalBillingProfiles.Add(profile);
        dbContext.Invoices.Add(activeInvoice);
        dbContext.Transactions.Add(
            CreateRentalSettlementTransaction(
                customer,
                activeInvoice,
                profileId,
                activeRunId,
                50m));
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var service = new RentalSettlementRecalculationService(dbContext);
        await service.RecalculateRentalSettlementsAsync(
            [
                (profileId, firstInactiveRunId),
                (profileId, secondInactiveRunId),
                (profileId, activeRunId)
            ],
            CancellationToken.None);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        var forwardOrder = await LoadRentalProfileSettlementSnapshotAsync(dbContext, profileId);

        await ResetRentalProfileSettlementAsync(dbContext, profileId, initialRunsJson);
        await service.RecalculateRentalSettlementsAsync(
            [
                (profileId, activeRunId),
                (profileId, secondInactiveRunId),
                (profileId, firstInactiveRunId)
            ],
            CancellationToken.None);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        var reverseOrder = await LoadRentalProfileSettlementSnapshotAsync(dbContext, profileId);

        Assert.Equal(forwardOrder, reverseOrder);
        Assert.Equal(50m, reverseOrder.SettledAmount);
        Assert.Equal(250m, reverseOrder.OutstandingAmount);
        Assert.Equal(
            [activeRunId],
            ReadRentalBillingRunIds(reverseOrder.BillingRunsJson));
    }

    private AppDbContext CreateDbContext(TestCurrentUserContext currentUser)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        return new AppDbContext(options, currentUser, new RevisionClock());
    }

    private static InvoicesController CreateInvoicesController(
        AppDbContext dbContext,
        TestCurrentUserContext currentUser)
    {
        var revisionClock = new RevisionClock();
        return new InvoicesController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, revisionClock),
            new RentalSettlementRecalculationService(dbContext));
    }

    private static SyncController CreateSyncController(
        AppDbContext dbContext,
        TestCurrentUserContext currentUser)
    {
        var revisionClock = new RevisionClock();
        return new SyncController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new StubCentralFileStorage(),
            revisionClock,
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, revisionClock),
            new RentalAssignmentHistoryService(dbContext),
            new RentalSettlementRecalculationService(dbContext));
    }

    private static Customer CreateCustomer(string officeCode) => new()
    {
        Id = Guid.NewGuid(),
        TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(null, officeCode),
        OfficeCode = officeCode,
        ResponsibleOfficeCode = officeCode,
        NameOriginal = $"Customer-{Guid.NewGuid():N}",
        NameMatchKey = $"CUSTOMER{Guid.NewGuid():N}",
        TradeType = "Sales"
    };

    private static Item CreateItem(string trackingType, decimal currentStock) => new()
    {
        Id = Guid.NewGuid(),
        TenantCode = TenantScopeCatalog.UsenetGroup,
        OfficeCode = OfficeCodeCatalog.Usenet,
        NameOriginal = $"Item-{Guid.NewGuid():N}",
        NameMatchKey = $"ITEM{Guid.NewGuid():N}",
        Unit = "EA",
        ItemKind = ItemKinds.Product,
        TrackingType = trackingType,
        CurrentStock = currentStock
    };

    private static Invoice CreateInvoice(
        Guid invoiceId,
        Customer customer,
        Item item,
        decimal quantity,
        string trackingType) => new()
    {
        Id = invoiceId,
        CustomerId = customer.Id,
        TenantCode = TenantScopeCatalog.UsenetGroup,
        OfficeCode = OfficeCodeCatalog.Usenet,
        ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
        InvoiceNumber = $"INV-{invoiceId:N}"[..20],
        VersionGroupId = invoiceId,
        VersionNumber = 1,
        IsLatestVersion = true,
        VoucherType = VoucherType.Sales,
        SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
        InvoiceDate = new DateOnly(2026, 7, 26),
        Lines =
        [
            new InvoiceLine
            {
                Id = Guid.NewGuid(),
                InvoiceId = invoiceId,
                ItemId = item.Id,
                ItemNameOriginal = item.NameOriginal,
                Unit = "EA",
                Quantity = quantity,
                UnitPrice = 100m,
                LineAmount = quantity * 100m,
                OrderIndex = 1,
                ItemTrackingType = trackingType
            }
        ]
    };

    private static RentalBillingProfile CreateRentalBillingProfile(
        Customer customer,
        Guid profileId,
        decimal monthlyAmount) => new()
    {
        Id = profileId,
        TenantCode = TenantScopeCatalog.UsenetGroup,
        OfficeCode = OfficeCodeCatalog.Usenet,
        ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
        ProfileKey = $"profile-{profileId:N}",
        CustomerId = customer.Id,
        CustomerName = customer.NameOriginal,
        ManagementCompanyCode = OfficeCodeCatalog.Usenet,
        MonthlyAmount = monthlyAmount,
        SettledAmount = 777m,
        OutstandingAmount = 888m,
        BillingStatus = "stale",
        SettlementStatus = "stale",
        CompletionStatus = "stale",
        IsActive = true
    };

    private static TransactionRecord CreateRentalSettlementTransaction(
        Customer customer,
        Invoice invoice,
        Guid profileId,
        Guid runId,
        decimal settlementAmount) => new()
    {
        Id = Guid.NewGuid(),
        CustomerId = customer.Id,
        TenantCode = TenantScopeCatalog.UsenetGroup,
        OfficeCode = OfficeCodeCatalog.Usenet,
        ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
        TransactionDate = invoice.InvoiceDate,
        TransactionKind = "rental payment",
        LinkedInvoiceId = invoice.Id,
        LinkedInvoiceNumber = invoice.InvoiceNumber,
        LinkedRentalBillingProfileId = profileId,
        LinkedRentalBillingRunId = runId,
        SettlementAmount = settlementAmount
    };

    private static async Task<RentalProfileSettlementSnapshot> LoadRentalProfileSettlementSnapshotAsync(
        AppDbContext dbContext,
        Guid profileId)
    {
        var profile = await dbContext.RentalBillingProfiles.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(row => row.Id == profileId);
        return new RentalProfileSettlementSnapshot(
            profile.SettledAmount,
            profile.OutstandingAmount,
            profile.BillingStatus,
            profile.SettlementStatus,
            profile.CompletionStatus,
            profile.LastBilledDate,
            profile.LastSettledDate,
            profile.BillingRunsJson);
    }

    private static async Task ResetRentalProfileSettlementAsync(
        AppDbContext dbContext,
        Guid profileId,
        string billingRunsJson)
    {
        dbContext.ChangeTracker.Clear();
        var profile = await dbContext.RentalBillingProfiles.IgnoreQueryFilters()
            .SingleAsync(row => row.Id == profileId);
        profile.SettledAmount = 777m;
        profile.OutstandingAmount = 888m;
        profile.BillingStatus = "stale";
        profile.SettlementStatus = "stale";
        profile.CompletionStatus = "stale";
        profile.LastBilledDate = null;
        profile.LastSettledDate = null;
        profile.BillingRunsJson = billingRunsJson;
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
    }

    private static List<Guid> ReadRentalBillingRunIds(string billingRunsJson)
    {
        using var document = JsonDocument.Parse(billingRunsJson);
        return document.RootElement
            .EnumerateArray()
            .Select(run => run.GetProperty("RunId").GetGuid())
            .ToList();
    }

    private static InvoiceDto BuildInvoiceDto(
        Guid invoiceId,
        Customer customer,
        Item item,
        string sourceWarehouseCode,
        decimal quantity,
        string trackingType,
        long expectedRevision = 0) => new()
    {
        Id = invoiceId,
        CustomerId = customer.Id,
        CustomerName = customer.NameOriginal,
        TenantCode = customer.TenantCode,
        OfficeCode = customer.OfficeCode,
        ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
        InvoiceNumber = $"INV-{invoiceId:N}"[..20],
        VersionGroupId = invoiceId,
        VersionNumber = 1,
        IsLatestVersion = true,
        VoucherType = VoucherType.Sales,
        SourceWarehouseCode = sourceWarehouseCode,
        InvoiceDate = new DateOnly(2026, 7, 26),
        VatMode = InvoiceVatModes.None,
        Revision = expectedRevision,
        ExpectedRevision = expectedRevision,
        UpdatedAtUtc = DateTime.UtcNow.AddMinutes(1),
        Lines =
        [
            new InvoiceLineDto
            {
                Id = Guid.NewGuid(),
                InvoiceId = invoiceId,
                ItemId = item.Id,
                ItemNameOriginal = item.NameOriginal,
                Unit = "EA",
                Quantity = quantity,
                UnitPrice = 100m,
                LineAmount = quantity * 100m,
                OrderIndex = 1,
                ItemTrackingType = trackingType
            }
        ]
    };

    private static TestCurrentUserContext CreateAdminUser() => new()
    {
        Username = "admin",
        TenantCode = TenantScopeCatalog.UsenetGroup,
        OfficeCode = OfficeCodeCatalog.Usenet,
        ScopeType = TenantScopeCatalog.ScopeAdmin,
        IsAdmin = true
    };

    private static TestCurrentUserContext CreateInvoiceUser(string username) => new()
    {
        Username = username,
        TenantCode = TenantScopeCatalog.UsenetGroup,
        OfficeCode = OfficeCodeCatalog.Usenet,
        ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
        Permissions = [PermissionNames.InvoiceEdit]
    };

    private static TestCurrentUserContext CreateDeliveryUser(string username) => new()
    {
        Username = username,
        TenantCode = TenantScopeCatalog.UsenetGroup,
        OfficeCode = OfficeCodeCatalog.Usenet,
        ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
        Permissions = [PermissionNames.DeliveryEdit]
    };

    public void Dispose() => _connection.Dispose();

    private sealed class TestCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId { get; init; } = Guid.NewGuid();
        public string Username { get; init; } = string.Empty;
        public string TenantCode { get; init; } = TenantScopeCatalog.UsenetGroup;
        public string OfficeCode { get; init; } = OfficeCodeCatalog.Usenet;
        public string ScopeType { get; init; } = TenantScopeCatalog.ScopeOfficeOnly;
        public bool IsAdmin { get; init; }
        public bool IsGodMode { get; init; }
        public IReadOnlyCollection<string> Permissions { get; init; } = [];

        public bool HasPermission(string permission)
            => IsAdmin || IsGodMode || Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);
    }

    private sealed class StubInvoiceNumberService : IInvoiceNumberService
    {
        public Task<string> GenerateAsync(
            Guid customerId,
            DateOnly invoiceDate,
            CancellationToken cancellationToken = default)
            => Task.FromResult($"{invoiceDate:yyyyMM}-0001");
    }

    private sealed record RentalProfileSettlementSnapshot(
        decimal SettledAmount,
        decimal OutstandingAmount,
        string BillingStatus,
        string SettlementStatus,
        string CompletionStatus,
        DateOnly? LastBilledDate,
        DateOnly? LastSettledDate,
        string BillingRunsJson);

    private sealed class StubCentralFileStorage : ICentralFileStorage
    {
        public string RootPath => Path.GetTempPath();

        public Task<string> SaveBytesAsync(
            string area,
            string ownerId,
            Guid fileId,
            string fileName,
            byte[] content,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Path.Combine(RootPath, fileName));

        public byte[] ReadBytes(string? storedPath, byte[]? fallback = null) => fallback ?? [];

        public void DeleteIfExists(string? storedPath)
        {
        }
    }
}
