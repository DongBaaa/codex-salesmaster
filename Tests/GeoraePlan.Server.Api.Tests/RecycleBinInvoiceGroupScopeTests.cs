using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class RecycleBinInvoiceGroupScopeTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public RecycleBinInvoiceGroupScopeTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        using var dbContext = CreateDbContext(CreateOfficeUser());
        dbContext.Database.EnsureCreated();
    }

    [Fact]
    public async Task RestoreInvoiceGroup_GlobalAdminRawGroupCollision_RestoresOnlySelectedCompositeChain()
    {
        var user = CreateAdminUser();
        var groupId = Guid.NewGuid();
        var selectedCustomerId = Guid.NewGuid();
        var foreignCustomerId = Guid.NewGuid();
        var firstInvoiceId = Guid.NewGuid();
        var secondInvoiceId = Guid.NewGuid();
        var firstLineId = Guid.NewGuid();
        var secondLineId = Guid.NewGuid();

        await using (var seedDb = CreateDbContext(user))
        {
            seedDb.Customers.AddRange(
                CreateCustomer(selectedCustomerId, OfficeCodeCatalog.Usenet, isDeleted: true),
                CreateCustomer(
                    foreignCustomerId,
                    OfficeCodeCatalog.Itworld,
                    isDeleted: true,
                    tenantCode: TenantScopeCatalog.Itworld,
                    officeCode: OfficeCodeCatalog.Itworld));
            seedDb.Invoices.AddRange(
                CreateInvoice(firstInvoiceId, selectedCustomerId, groupId, 1, isLatest: false),
                CreateInvoice(
                    secondInvoiceId,
                    foreignCustomerId,
                    groupId,
                    2,
                    isLatest: true,
                    tenantCode: TenantScopeCatalog.Itworld,
                    officeCode: OfficeCodeCatalog.Itworld,
                    responsibleOfficeCode: OfficeCodeCatalog.Itworld,
                    sourceWarehouseCode: OfficeCodeCatalog.ItworldMainWarehouse));
            seedDb.InvoiceLines.AddRange(
                CreateLine(firstLineId, firstInvoiceId, 3m, "first-version-line"),
                CreateLine(secondLineId, secondInvoiceId, 7m, "second-version-line"));
            await seedDb.SaveChangesAsync();
        }

        await using var scopedDb = CreateDbContext(user);
        var beforeForeignInvoice = (await ReadInvoiceStateAsync(scopedDb, groupId))
            .Single(invoice => invoice.Id == secondInvoiceId);
        var beforeForeignCustomer = Assert.Single(
            await ReadCustomerStateAsync(scopedDb, foreignCustomerId));
        var beforeForeignLine = Assert.Single(
            await ReadLineStateAsync(scopedDb, secondLineId));
        var expectedRevision = await scopedDb.Invoices
            .IgnoreQueryFilters()
            .Where(invoice => invoice.Id == firstInvoiceId)
            .Select(invoice => invoice.Revision)
            .SingleAsync();
        var controller = CreateController(scopedDb, user);

        var response = await controller.Restore(
            CreateInvoiceMutationRequest(firstInvoiceId, expectedRevision),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var result = Assert.Single(payload.Results);
        Assert.True(result.Success, result.Message);
        Assert.Equal(1, payload.SucceededCount);

        scopedDb.ChangeTracker.Clear();
        var invoices = await ReadInvoiceStateAsync(scopedDb, groupId);
        Assert.False(invoices.Single(invoice => invoice.Id == firstInvoiceId).IsDeleted);
        Assert.Equal(beforeForeignInvoice, invoices.Single(invoice => invoice.Id == secondInvoiceId));
        Assert.False(Assert.Single(
            await ReadCustomerStateAsync(scopedDb, selectedCustomerId)).IsDeleted);
        Assert.Equal(
            beforeForeignCustomer,
            Assert.Single(await ReadCustomerStateAsync(scopedDb, foreignCustomerId)));
        Assert.False(Assert.Single(
            await ReadLineStateAsync(scopedDb, firstLineId)).IsDeleted);
        Assert.Equal(
            beforeForeignLine,
            Assert.Single(await ReadLineStateAsync(scopedDb, secondLineId)));
    }

    [Fact]
    public async Task RestoreInvoiceGroup_WhenAllVersionsAreDeleted_PreservesGroupLatestFlagsAndLines()
    {
        var user = CreateOfficeUser();
        var groupId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var firstInvoiceId = Guid.NewGuid();
        var secondInvoiceId = Guid.NewGuid();
        var firstLineId = Guid.NewGuid();
        var secondLineId = Guid.NewGuid();

        await using (var seedDb = CreateDbContext(user))
        {
            seedDb.Customers.Add(
                CreateCustomer(customerId, OfficeCodeCatalog.Usenet, isDeleted: true));
            seedDb.Invoices.AddRange(
                CreateInvoice(firstInvoiceId, customerId, groupId, 1, isLatest: false),
                CreateInvoice(secondInvoiceId, customerId, groupId, 2, isLatest: true));
            seedDb.InvoiceLines.AddRange(
                CreateLine(firstLineId, firstInvoiceId, 3m, "first-version-line"),
                CreateLine(secondLineId, secondInvoiceId, 7m, "second-version-line"));
            await seedDb.SaveChangesAsync();
        }

        await using var scopedDb = CreateDbContext(user);
        var expectedRevision = await scopedDb.Invoices
            .IgnoreQueryFilters()
            .Where(invoice => invoice.Id == firstInvoiceId)
            .Select(invoice => invoice.Revision)
            .SingleAsync();
        var controller = CreateController(scopedDb, user);

        var response = await controller.Restore(
            CreateInvoiceMutationRequest(firstInvoiceId, expectedRevision),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var result = Assert.Single(payload.Results);
        Assert.True(result.Success, result.Message);
        Assert.Equal(1, payload.SucceededCount);

        scopedDb.ChangeTracker.Clear();
        var invoices = await ReadInvoiceStateAsync(scopedDb, groupId);
        Assert.Equal(2, invoices.Length);
        Assert.All(invoices, invoice =>
        {
            Assert.False(invoice.IsDeleted);
            Assert.Equal(groupId, invoice.VersionGroupId);
        });
        Assert.False(invoices.Single(invoice => invoice.Id == firstInvoiceId).IsLatestVersion);
        Assert.True(invoices.Single(invoice => invoice.Id == secondInvoiceId).IsLatestVersion);
        Assert.Equal(1, invoices.Single(invoice => invoice.Id == firstInvoiceId).VersionNumber);
        Assert.Equal(2, invoices.Single(invoice => invoice.Id == secondInvoiceId).VersionNumber);

        Assert.False(Assert.Single(
            await ReadCustomerStateAsync(scopedDb, customerId)).IsDeleted);

        var lines = await ReadLineStateAsync(scopedDb, firstLineId, secondLineId);
        Assert.Equal(
            (firstLineId, firstInvoiceId, false, 3m, "first-version-line"),
            lines.Single(line => line.Id == firstLineId));
        Assert.Equal(
            (secondLineId, secondInvoiceId, false, 7m, "second-version-line"),
            lines.Single(line => line.Id == secondLineId));
    }

    [Fact]
    public async Task PurgeInvoiceGroup_GlobalAdminRawGroupCollision_PurgesOnlySelectedCompositeChain()
    {
        var user = CreateAdminUser();
        var groupId = Guid.NewGuid();
        var selectedCustomerId = Guid.NewGuid();
        var foreignCustomerId = Guid.NewGuid();
        var selectedInvoiceId = Guid.NewGuid();
        var foreignInvoiceId = Guid.NewGuid();
        var selectedLineId = Guid.NewGuid();
        var foreignLineId = Guid.NewGuid();

        await using (var seedDb = CreateDbContext(user))
        {
            seedDb.Customers.AddRange(
                CreateCustomer(selectedCustomerId, OfficeCodeCatalog.Usenet, isDeleted: false),
                CreateCustomer(
                    foreignCustomerId,
                    OfficeCodeCatalog.Itworld,
                    isDeleted: false,
                    tenantCode: TenantScopeCatalog.Itworld,
                    officeCode: OfficeCodeCatalog.Itworld));
            seedDb.Invoices.AddRange(
                CreateInvoice(selectedInvoiceId, selectedCustomerId, groupId, 1, isLatest: true),
                CreateInvoice(
                    foreignInvoiceId,
                    foreignCustomerId,
                    groupId,
                    1,
                    isLatest: true,
                    tenantCode: TenantScopeCatalog.Itworld,
                    officeCode: OfficeCodeCatalog.Itworld,
                    responsibleOfficeCode: OfficeCodeCatalog.Itworld,
                    sourceWarehouseCode: OfficeCodeCatalog.ItworldMainWarehouse));
            seedDb.InvoiceLines.AddRange(
                CreateLine(selectedLineId, selectedInvoiceId, 3m, "selected-purge-line"),
                CreateLine(foreignLineId, foreignInvoiceId, 7m, "foreign-purge-line"));
            await seedDb.SaveChangesAsync();
        }

        await using var scopedDb = CreateDbContext(user);
        var beforeForeignInvoice = (await ReadInvoiceStateAsync(scopedDb, groupId))
            .Single(invoice => invoice.Id == foreignInvoiceId);
        var beforeForeignCustomer = Assert.Single(
            await ReadCustomerStateAsync(scopedDb, foreignCustomerId));
        var beforeForeignLine = Assert.Single(
            await ReadLineStateAsync(scopedDb, foreignLineId));
        var expectedRevision = await scopedDb.Invoices
            .IgnoreQueryFilters()
            .Where(invoice => invoice.Id == selectedInvoiceId)
            .Select(invoice => invoice.Revision)
            .SingleAsync();
        var controller = CreateController(scopedDb, user);

        var response = await controller.Purge(
            CreateInvoiceMutationRequest(selectedInvoiceId, expectedRevision),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var result = Assert.Single(payload.Results);
        Assert.True(result.Success, result.Message);
        Assert.Equal(1, payload.SucceededCount);

        scopedDb.ChangeTracker.Clear();
        Assert.False(await scopedDb.Invoices
            .IgnoreQueryFilters()
            .AnyAsync(invoice => invoice.Id == selectedInvoiceId));
        Assert.False(await scopedDb.InvoiceLines
            .IgnoreQueryFilters()
            .AnyAsync(line => line.Id == selectedLineId));
        Assert.Equal(
            beforeForeignInvoice,
            (await ReadInvoiceStateAsync(scopedDb, groupId))
            .Single(invoice => invoice.Id == foreignInvoiceId));
        Assert.Equal(
            beforeForeignCustomer,
            Assert.Single(await ReadCustomerStateAsync(scopedDb, foreignCustomerId)));
        Assert.Equal(
            beforeForeignLine,
            Assert.Single(await ReadLineStateAsync(scopedDb, foreignLineId)));
        Assert.DoesNotContain(
            await scopedDb.RecycleBinPurgeRecords.AsNoTracking().ToListAsync(),
            record => record.EntityId == foreignInvoiceId);
    }

    [Fact]
    public async Task PurgeInvoiceGroup_WhenSelectedCompositeChainContainsActiveVersion_FailsWithoutChanges()
    {
        var user = CreateAdminUser();
        var groupId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var activeInvoiceId = Guid.NewGuid();
        var deletedInvoiceId = Guid.NewGuid();
        var activeLineId = Guid.NewGuid();
        var deletedLineId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var ledgerId = Guid.NewGuid();

        await using (var seedDb = CreateDbContext(user))
        {
            seedDb.Customers.Add(
                CreateCustomer(customerId, OfficeCodeCatalog.Usenet, isDeleted: false));
            seedDb.Items.Add(new Item
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "active-version-purge-item",
                NameMatchKey = "ACTIVEVERSIONPURGEITEM",
                TrackingType = ItemTrackingTypes.Stock,
                CurrentStock = 17m
            });
            seedDb.Invoices.AddRange(
                CreateInvoice(
                    activeInvoiceId,
                    customerId,
                    groupId,
                    1,
                    isLatest: true,
                    isDeleted: false),
                CreateInvoice(
                    deletedInvoiceId,
                    customerId,
                    groupId,
                    2,
                    isLatest: false));
            seedDb.InvoiceLines.AddRange(
                CreateLine(
                    activeLineId,
                    activeInvoiceId,
                    3m,
                    "active-version-line",
                    itemId,
                    isDeleted: false),
                CreateLine(
                    deletedLineId,
                    deletedInvoiceId,
                    5m,
                    "deleted-version-line",
                    itemId));
            seedDb.ItemWarehouseStocks.Add(new ItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 17m,
                Revision = 7
            });
            seedDb.InventoryLedgerEntries.Add(new InventoryLedgerEntry
            {
                Id = ledgerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ItemId = itemId,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                SourceType = "Invoice",
                SourceDocumentId = activeInvoiceId,
                SourceLineId = activeLineId,
                QuantityDelta = -3m,
                OccurredDate = new DateOnly(2026, 7, 30)
            });
            await seedDb.SaveChangesAsync();
        }

        await using var scopedDb = CreateDbContext(user);
        var beforeInvoices = await ReadInvoiceStateAsync(scopedDb, groupId);
        var beforeLines = await ReadLineStateAsync(scopedDb, activeLineId, deletedLineId);
        var beforeItem = await scopedDb.Items
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item => item.Id == itemId)
            .Select(item => new { item.CurrentStock, item.Revision })
            .SingleAsync();
        var beforeWarehouseRows = await scopedDb.ItemWarehouseStocks
            .AsNoTracking()
            .Where(stock => stock.ItemId == itemId)
            .Select(stock => new { stock.WarehouseCode, stock.Quantity, stock.Revision })
            .ToArrayAsync();
        var beforeLedgerRows = await scopedDb.InventoryLedgerEntries
            .AsNoTracking()
            .Where(entry => entry.ItemId == itemId)
            .Select(entry => new { entry.Id, entry.SourceDocumentId, entry.SourceLineId, entry.QuantityDelta })
            .ToArrayAsync();
        var expectedRevision = beforeInvoices
            .Single(invoice => invoice.Id == deletedInvoiceId)
            .Revision;
        var controller = CreateController(scopedDb, user);

        var response = await controller.Purge(
            CreateInvoiceMutationRequest(deletedInvoiceId, expectedRevision),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var result = Assert.Single(payload.Results);
        Assert.False(result.Success);
        Assert.Equal(0, payload.SucceededCount);

        scopedDb.ChangeTracker.Clear();
        Assert.Equal(beforeInvoices, await ReadInvoiceStateAsync(scopedDb, groupId));
        Assert.Equal(beforeLines, await ReadLineStateAsync(scopedDb, activeLineId, deletedLineId));
        Assert.Equal(
            beforeItem,
            await scopedDb.Items
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(item => item.Id == itemId)
                .Select(item => new { item.CurrentStock, item.Revision })
                .SingleAsync());
        Assert.Equal(
            beforeWarehouseRows,
            await scopedDb.ItemWarehouseStocks
                .AsNoTracking()
                .Where(stock => stock.ItemId == itemId)
                .Select(stock => new { stock.WarehouseCode, stock.Quantity, stock.Revision })
                .ToArrayAsync());
        Assert.Equal(
            beforeLedgerRows,
            await scopedDb.InventoryLedgerEntries
                .AsNoTracking()
                .Where(entry => entry.ItemId == itemId)
                .Select(entry => new { entry.Id, entry.SourceDocumentId, entry.SourceLineId, entry.QuantityDelta })
                .ToArrayAsync());
        Assert.Empty(await scopedDb.RecycleBinPurgeRecords
            .AsNoTracking()
            .Where(record => record.EntityId == activeInvoiceId || record.EntityId == deletedInvoiceId)
            .ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvoiceGroupMutation_WhenLinkedDeletedPaymentIsOutsidePaymentWriteScope_FailsWithoutChanges(
        bool purge)
    {
        var admin = CreateAdminUser();
        var user = CreateOfficeUser(
            TenantScopeCatalog.UsenetGroup,
            OfficeCodeCatalog.Yeonsu);
        var customerId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();

        await using (var seedDb = CreateDbContext(admin))
        {
            seedDb.DataSharingPolicies.Add(new DataSharingPolicy
            {
                Id = Guid.NewGuid(),
                SourceTenantCode = TenantScopeCatalog.UsenetGroup,
                SourceOfficeCode = OfficeCodeCatalog.Usenet,
                TargetTenantCode = TenantScopeCatalog.UsenetGroup,
                TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
                ShareCustomers = true,
                ShareInvoices = true,
                SharePayments = false,
                AllowTargetWrite = true,
                IsActive = true
            });
            seedDb.Customers.Add(
                CreateCustomer(customerId, OfficeCodeCatalog.Usenet, isDeleted: false));
            seedDb.Invoices.Add(
                CreateInvoice(
                    invoiceId,
                    customerId,
                    Guid.NewGuid(),
                    1,
                    isLatest: true));
            seedDb.Payments.Add(new Payment
            {
                Id = paymentId,
                InvoiceId = invoiceId,
                PaymentDate = new DateOnly(2026, 7, 31),
                Amount = 12_000m,
                Note = "payment-area-preflight",
                IsDeleted = true
            });
            await seedDb.SaveChangesAsync();
        }

        await using var scopedDb = CreateDbContext(user);
        var groupId = await scopedDb.Invoices
            .IgnoreQueryFilters()
            .Where(invoice => invoice.Id == invoiceId)
            .Select(invoice => invoice.VersionGroupId)
            .SingleAsync();
        var beforeInvoice = Assert.Single(await ReadInvoiceStateAsync(scopedDb, groupId));
        var beforeCustomer = Assert.Single(await ReadCustomerStateAsync(scopedDb, customerId));
        var beforePayment = await scopedDb.Payments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(payment => payment.Id == paymentId)
            .Select(payment => new { payment.Id, payment.InvoiceId, payment.IsDeleted, payment.Revision })
            .SingleAsync();
        var controller = CreateController(scopedDb, user);

        var response = purge
            ? await controller.Purge(
                CreateInvoiceMutationRequest(invoiceId, beforeInvoice.Revision),
                CancellationToken.None)
            : await controller.Restore(
                CreateInvoiceMutationRequest(invoiceId, beforeInvoice.Revision),
                CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var result = Assert.Single(payload.Results);
        Assert.False(result.Success);
        Assert.Equal(0, payload.SucceededCount);

        scopedDb.ChangeTracker.Clear();
        Assert.Equal(beforeInvoice, Assert.Single(await ReadInvoiceStateAsync(scopedDb, groupId)));
        Assert.Equal(beforeCustomer, Assert.Single(await ReadCustomerStateAsync(scopedDb, customerId)));
        Assert.Equal(
            beforePayment,
            await scopedDb.Payments
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(payment => payment.Id == paymentId)
                .Select(payment => new { payment.Id, payment.InvoiceId, payment.IsDeleted, payment.Revision })
                .SingleAsync());
        Assert.Empty(await scopedDb.RecycleBinPurgeRecords
            .AsNoTracking()
            .Where(record => record.EntityId == invoiceId || record.EntityId == paymentId)
            .ToListAsync());
    }

    [Fact]
    public async Task RestoreInvoiceGroup_WhenSourceWarehouseIsOutsideWritableScope_FailsBeforeAnyChanges()
    {
        var user = CreateOfficeUser();
        var customerId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        var itemId = Guid.NewGuid();

        await using (var seedDb = CreateDbContext(user))
        {
            seedDb.Customers.Add(
                CreateCustomer(customerId, OfficeCodeCatalog.Usenet, isDeleted: true));
            seedDb.Items.Add(new Item
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "warehouse-preflight-item",
                NameMatchKey = "WAREHOUSEPREFLIGHTITEM",
                TrackingType = ItemTrackingTypes.Stock,
                CurrentStock = 23m
            });
            seedDb.Invoices.Add(
                CreateInvoice(
                    invoiceId,
                    customerId,
                    Guid.NewGuid(),
                    1,
                    isLatest: true,
                    sourceWarehouseCode: OfficeCodeCatalog.ItworldMainWarehouse));
            seedDb.InvoiceLines.Add(
                CreateLine(
                    lineId,
                    invoiceId,
                    4m,
                    "warehouse-preflight-line",
                    itemId));
            await seedDb.SaveChangesAsync();
        }

        await using var scopedDb = CreateDbContext(user);
        var invoiceGroupId = await scopedDb.Invoices
            .IgnoreQueryFilters()
            .Where(invoice => invoice.Id == invoiceId)
            .Select(invoice => invoice.VersionGroupId)
            .SingleAsync();
        var beforeInvoices = await ReadInvoiceStateAsync(scopedDb, invoiceGroupId);
        var beforeCustomer = Assert.Single(await ReadCustomerStateAsync(scopedDb, customerId));
        var beforeLine = Assert.Single(await ReadLineStateAsync(scopedDb, lineId));
        var beforeItem = await scopedDb.Items
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item => item.Id == itemId)
            .Select(item => new { item.CurrentStock, item.Revision })
            .SingleAsync();
        var beforeWarehouseRows = await scopedDb.ItemWarehouseStocks
            .AsNoTracking()
            .Where(stock => stock.ItemId == itemId)
            .Select(stock => new { stock.WarehouseCode, stock.Quantity, stock.Revision })
            .ToArrayAsync();
        var beforeLedgerRows = await scopedDb.InventoryLedgerEntries
            .AsNoTracking()
            .Where(entry => entry.ItemId == itemId)
            .Select(entry => new { entry.Id, entry.WarehouseCode, entry.QuantityDelta })
            .ToArrayAsync();
        var expectedRevision = beforeInvoices.Single().Revision;
        var controller = CreateController(scopedDb, user);

        var response = await controller.Restore(
            CreateInvoiceMutationRequest(invoiceId, expectedRevision),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var result = Assert.Single(payload.Results);
        Assert.False(result.Success);
        Assert.Equal(0, payload.SucceededCount);

        scopedDb.ChangeTracker.Clear();
        Assert.Equal(beforeInvoices, await ReadInvoiceStateAsync(scopedDb, invoiceGroupId));
        Assert.Equal(beforeCustomer, Assert.Single(await ReadCustomerStateAsync(scopedDb, customerId)));
        Assert.Equal(beforeLine, Assert.Single(await ReadLineStateAsync(scopedDb, lineId)));
        var afterItem = await scopedDb.Items
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item => item.Id == itemId)
            .Select(item => new { item.CurrentStock, item.Revision })
            .SingleAsync();
        Assert.Equal(beforeItem, afterItem);
        Assert.Equal(
            beforeWarehouseRows,
            await scopedDb.ItemWarehouseStocks
                .AsNoTracking()
                .Where(stock => stock.ItemId == itemId)
                .Select(stock => new { stock.WarehouseCode, stock.Quantity, stock.Revision })
                .ToArrayAsync());
        Assert.Equal(
            beforeLedgerRows,
            await scopedDb.InventoryLedgerEntries
                .AsNoTracking()
                .Where(entry => entry.ItemId == itemId)
                .Select(entry => new { entry.Id, entry.WarehouseCode, entry.QuantityDelta })
                .ToArrayAsync());
    }

    [Fact]
    public async Task RestoreInvoiceGroup_WhenVersionNumbersTie_SelectsExactlyOneDeterministicLatest()
    {
        var user = CreateAdminUser();
        var groupId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var lowerVersionId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var lowerTieId = Guid.Parse("20000000-0000-0000-0000-000000000001");
        var higherTieId = Guid.Parse("30000000-0000-0000-0000-000000000001");
        var latestTimestamp = new DateTime(2026, 7, 31, 2, 0, 0, DateTimeKind.Utc);

        await using (var seedDb = CreateDbContext(user))
        {
            seedDb.Customers.Add(
                CreateCustomer(customerId, OfficeCodeCatalog.Usenet, isDeleted: true));
            seedDb.Invoices.AddRange(
                CreateInvoice(
                    lowerVersionId,
                    customerId,
                    groupId,
                    1,
                    isLatest: true,
                    updatedAtUtc: latestTimestamp.AddDays(1)),
                CreateInvoice(
                    lowerTieId,
                    customerId,
                    groupId,
                    2,
                    isLatest: true,
                    updatedAtUtc: latestTimestamp.AddHours(1)),
                CreateInvoice(
                    higherTieId,
                    customerId,
                    groupId,
                    2,
                    isLatest: true,
                    updatedAtUtc: latestTimestamp));
            await seedDb.SaveChangesAsync();
        }

        await using var scopedDb = CreateDbContext(user);
        var expectedRevision = await scopedDb.Invoices
            .IgnoreQueryFilters()
            .Where(invoice => invoice.Id == lowerVersionId)
            .Select(invoice => invoice.Revision)
            .SingleAsync();
        var controller = CreateController(scopedDb, user);

        var response = await controller.Restore(
            CreateInvoiceMutationRequest(lowerVersionId, expectedRevision),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        Assert.True(Assert.Single(payload.Results).Success);

        scopedDb.ChangeTracker.Clear();
        var invoices = await ReadInvoiceStateAsync(scopedDb, groupId);
        Assert.All(invoices, invoice => Assert.False(invoice.IsDeleted));
        var latest = Assert.Single(invoices, invoice => invoice.IsLatestVersion);
        Assert.Equal(higherTieId, latest.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvoiceGroupMutation_ExplicitTenantMismatchRawCollision_DoesNotTouchOtherCompositeChain(
        bool purge)
    {
        var user = CreateAdminUser();
        var groupId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var mismatchedInvoiceId = Guid.NewGuid();
        var canonicalInvoiceId = Guid.NewGuid();
        var mismatchedLineId = Guid.NewGuid();
        var canonicalLineId = Guid.NewGuid();
        var mismatchedItemId = Guid.NewGuid();
        var canonicalItemId = Guid.NewGuid();
        var mismatchedProfileId = Guid.NewGuid();
        var canonicalProfileId = Guid.NewGuid();

        await using (var seedDb = CreateDbContext(user))
        {
            var mismatchedInvoice = CreateInvoice(
                mismatchedInvoiceId,
                customerId,
                groupId,
                1,
                isLatest: true,
                tenantCode: TenantScopeCatalog.Itworld,
                officeCode: OfficeCodeCatalog.Usenet,
                responsibleOfficeCode: OfficeCodeCatalog.Itworld);
            mismatchedInvoice.LinkedRentalBillingProfileId = mismatchedProfileId;
            mismatchedInvoice.LinkedRentalBillingRunId = Guid.NewGuid();

            var canonicalInvoice = CreateInvoice(
                canonicalInvoiceId,
                customerId,
                groupId,
                2,
                isLatest: true,
                tenantCode: TenantScopeCatalog.UsenetGroup,
                officeCode: OfficeCodeCatalog.Usenet,
                responsibleOfficeCode: OfficeCodeCatalog.Itworld);
            canonicalInvoice.LinkedRentalBillingProfileId = canonicalProfileId;
            canonicalInvoice.LinkedRentalBillingRunId = Guid.NewGuid();

            seedDb.Customers.Add(
                CreateCustomer(
                    customerId,
                    OfficeCodeCatalog.Itworld,
                    isDeleted: false,
                    tenantCode: TenantScopeCatalog.UsenetGroup,
                    officeCode: OfficeCodeCatalog.Usenet));
            seedDb.Items.AddRange(
                new Item
                {
                    Id = mismatchedItemId,
                    TenantCode = TenantScopeCatalog.Itworld,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    NameOriginal = "mismatched-chain-stock",
                    NameMatchKey = "MISMATCHEDCHAINSTOCK",
                    TrackingType = ItemTrackingTypes.Stock,
                    CurrentStock = 20m
                },
                new Item
                {
                    Id = canonicalItemId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    NameOriginal = "canonical-chain-stock",
                    NameMatchKey = "CANONICALCHAINSTOCK",
                    TrackingType = ItemTrackingTypes.Stock,
                    CurrentStock = 20m
                });
            seedDb.Invoices.AddRange(mismatchedInvoice, canonicalInvoice);
            seedDb.InvoiceLines.AddRange(
                CreateLine(
                    mismatchedLineId,
                    mismatchedInvoiceId,
                    2m,
                    "mismatched-chain-line",
                    mismatchedItemId),
                CreateLine(
                    canonicalLineId,
                    canonicalInvoiceId,
                    7m,
                    "canonical-chain-line",
                    canonicalItemId));
            seedDb.ItemWarehouseStocks.AddRange(
                new ItemWarehouseStock
                {
                    ItemId = mismatchedItemId,
                    WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                    Quantity = 20m
                },
                new ItemWarehouseStock
                {
                    ItemId = canonicalItemId,
                    WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                    Quantity = 20m
                });
            seedDb.RentalBillingProfiles.AddRange(
                CreateRentalProfile(
                    mismatchedProfileId,
                    customerId,
                    TenantScopeCatalog.Itworld,
                    "MISMATCHED-CHAIN-RENTAL"),
                CreateRentalProfile(
                    canonicalProfileId,
                    customerId,
                    TenantScopeCatalog.UsenetGroup,
                    "CANONICAL-CHAIN-RENTAL"));
            await seedDb.SaveChangesAsync();
        }

        await using var scopedDb = CreateDbContext(user);
        var beforeCanonicalInvoice = (await ReadInvoiceStateAsync(scopedDb, groupId))
            .Single(invoice => invoice.Id == canonicalInvoiceId);
        var beforeCanonicalLine = Assert.Single(
            await ReadLineStateAsync(scopedDb, canonicalLineId));
        var beforeCanonicalStock = await scopedDb.ItemWarehouseStocks
            .AsNoTracking()
            .Where(stock =>
                stock.ItemId == canonicalItemId &&
                stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => new { stock.Quantity, stock.Revision })
            .SingleAsync();
        var beforeCanonicalItem = await scopedDb.Items
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item => item.Id == canonicalItemId)
            .Select(item => new { item.CurrentStock, item.Revision })
            .SingleAsync();
        var beforeCanonicalProfile = await scopedDb.RentalBillingProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(profile => profile.Id == canonicalProfileId)
            .Select(profile => new
            {
                profile.BillingStatus,
                profile.SettlementStatus,
                profile.CompletionStatus,
                profile.SettledAmount,
                profile.OutstandingAmount,
                profile.BillingRunsJson,
                profile.Revision
            })
            .SingleAsync();
        var expectedRevision = await scopedDb.Invoices
            .IgnoreQueryFilters()
            .Where(invoice => invoice.Id == mismatchedInvoiceId)
            .Select(invoice => invoice.Revision)
            .SingleAsync();
        var request = CreateInvoiceMutationRequest(mismatchedInvoiceId, expectedRevision);
        var controller = CreateController(scopedDb, user);

        var response = purge
            ? await controller.Purge(request, CancellationToken.None)
            : await controller.Restore(request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var result = Assert.Single(payload.Results);
        Assert.True(result.Success, result.Message);
        Assert.Equal(1, payload.SucceededCount);

        scopedDb.ChangeTracker.Clear();
        if (purge)
        {
            Assert.False(await scopedDb.Invoices
                .IgnoreQueryFilters()
                .AnyAsync(invoice => invoice.Id == mismatchedInvoiceId));
            Assert.False(await scopedDb.InvoiceLines
                .IgnoreQueryFilters()
                .AnyAsync(line => line.Id == mismatchedLineId));
        }
        else
        {
            var restored = await scopedDb.Invoices
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(invoice => invoice.Id == mismatchedInvoiceId);
            Assert.False(restored.IsDeleted);
            Assert.True(restored.IsLatestVersion);
            Assert.False(Assert.Single(
                await ReadLineStateAsync(scopedDb, mismatchedLineId)).IsDeleted);
            Assert.Equal(
                18m,
                await scopedDb.ItemWarehouseStocks
                    .AsNoTracking()
                    .Where(stock =>
                        stock.ItemId == mismatchedItemId &&
                        stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
                    .Select(stock => stock.Quantity)
                    .SingleAsync());
        }

        Assert.Equal(
            beforeCanonicalInvoice,
            (await ReadInvoiceStateAsync(scopedDb, groupId))
            .Single(invoice => invoice.Id == canonicalInvoiceId));
        Assert.Equal(
            beforeCanonicalLine,
            Assert.Single(await ReadLineStateAsync(scopedDb, canonicalLineId)));
        Assert.Equal(
            beforeCanonicalStock,
            await scopedDb.ItemWarehouseStocks
                .AsNoTracking()
                .Where(stock =>
                    stock.ItemId == canonicalItemId &&
                    stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => new { stock.Quantity, stock.Revision })
                .SingleAsync());
        Assert.Equal(
            beforeCanonicalItem,
            await scopedDb.Items
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(item => item.Id == canonicalItemId)
                .Select(item => new { item.CurrentStock, item.Revision })
                .SingleAsync());
        Assert.Equal(
            beforeCanonicalProfile,
            await scopedDb.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(profile => profile.Id == canonicalProfileId)
                .Select(profile => new
                {
                    profile.BillingStatus,
                    profile.SettlementStatus,
                    profile.CompletionStatus,
                    profile.SettledAmount,
                    profile.OutstandingAmount,
                    profile.BillingRunsJson,
                    profile.Revision
                })
                .SingleAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task InvoiceGroupMutation_SameTenantOwnerOrResponsibleOnlyRawCollision_DoesNotTouchOtherCompositeChain(
        bool purge,
        bool ownerCollision)
    {
        var user = CreateAdminUser();
        var groupId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var selectedInvoiceId = Guid.NewGuid();
        var foreignInvoiceId = Guid.NewGuid();
        var selectedLineId = Guid.NewGuid();
        var foreignLineId = Guid.NewGuid();
        var selectedItemId = Guid.NewGuid();
        var foreignItemId = Guid.NewGuid();
        var selectedProfileId = Guid.NewGuid();
        var foreignProfileId = Guid.NewGuid();
        var selectedOfficeCode = OfficeCodeCatalog.Usenet;
        var selectedResponsibleOfficeCode = OfficeCodeCatalog.Usenet;
        var foreignOfficeCode = ownerCollision
            ? OfficeCodeCatalog.Itworld
            : selectedOfficeCode;
        var foreignResponsibleOfficeCode = ownerCollision
            ? selectedResponsibleOfficeCode
            : OfficeCodeCatalog.Itworld;

        await using (var seedDb = CreateDbContext(user))
        {
            var selectedInvoice = CreateInvoice(
                selectedInvoiceId,
                customerId,
                groupId,
                1,
                isLatest: true,
                tenantCode: TenantScopeCatalog.UsenetGroup,
                officeCode: selectedOfficeCode,
                responsibleOfficeCode: selectedResponsibleOfficeCode,
                sourceWarehouseCode: OfficeCodeCatalog.UsenetMainWarehouse);
            selectedInvoice.LinkedRentalBillingProfileId = selectedProfileId;
            selectedInvoice.LinkedRentalBillingRunId = Guid.NewGuid();

            var foreignInvoice = CreateInvoice(
                foreignInvoiceId,
                customerId,
                groupId,
                2,
                isLatest: true,
                tenantCode: TenantScopeCatalog.UsenetGroup,
                officeCode: foreignOfficeCode,
                responsibleOfficeCode: foreignResponsibleOfficeCode,
                sourceWarehouseCode: ownerCollision
                    ? OfficeCodeCatalog.ItworldMainWarehouse
                    : OfficeCodeCatalog.UsenetMainWarehouse);
            foreignInvoice.LinkedRentalBillingProfileId = foreignProfileId;
            foreignInvoice.LinkedRentalBillingRunId = Guid.NewGuid();

            seedDb.Customers.Add(
                CreateCustomer(
                    customerId,
                    selectedResponsibleOfficeCode,
                    isDeleted: false,
                    tenantCode: TenantScopeCatalog.UsenetGroup,
                    officeCode: selectedOfficeCode));
            seedDb.Items.AddRange(
                new Item
                {
                    Id = selectedItemId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = selectedOfficeCode,
                    NameOriginal = "selected-owner-responsible-chain-stock",
                    NameMatchKey = "SELECTEDOWNERRESPONSIBLECHAINSTOCK",
                    TrackingType = ItemTrackingTypes.Stock,
                    CurrentStock = 20m
                },
                new Item
                {
                    Id = foreignItemId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = foreignOfficeCode,
                    NameOriginal = "foreign-owner-responsible-chain-stock",
                    NameMatchKey = "FOREIGNOWNERRESPONSIBLECHAINSTOCK",
                    TrackingType = ItemTrackingTypes.Stock,
                    CurrentStock = 30m
                });
            seedDb.Invoices.AddRange(selectedInvoice, foreignInvoice);
            seedDb.InvoiceLines.AddRange(
                CreateLine(
                    selectedLineId,
                    selectedInvoiceId,
                    2m,
                    "selected-owner-responsible-line",
                    selectedItemId),
                CreateLine(
                    foreignLineId,
                    foreignInvoiceId,
                    7m,
                    "foreign-owner-responsible-line",
                    foreignItemId));
            seedDb.ItemWarehouseStocks.AddRange(
                new ItemWarehouseStock
                {
                    ItemId = selectedItemId,
                    WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                    Quantity = 20m
                },
                new ItemWarehouseStock
                {
                    ItemId = foreignItemId,
                    WarehouseCode = ownerCollision
                        ? OfficeCodeCatalog.ItworldMainWarehouse
                        : OfficeCodeCatalog.UsenetMainWarehouse,
                    Quantity = 30m
                });
            seedDb.RentalBillingProfiles.AddRange(
                CreateRentalProfile(
                    selectedProfileId,
                    customerId,
                    TenantScopeCatalog.UsenetGroup,
                    $"SELECTED-{ownerCollision}-RENTAL",
                    selectedOfficeCode,
                    selectedResponsibleOfficeCode),
                CreateRentalProfile(
                    foreignProfileId,
                    customerId,
                    TenantScopeCatalog.UsenetGroup,
                    $"FOREIGN-{ownerCollision}-RENTAL",
                    foreignOfficeCode,
                    foreignResponsibleOfficeCode));
            await seedDb.SaveChangesAsync();
        }

        await using var scopedDb = CreateDbContext(user);
        var foreignWarehouseCode = ownerCollision
            ? OfficeCodeCatalog.ItworldMainWarehouse
            : OfficeCodeCatalog.UsenetMainWarehouse;
        var beforeForeignInvoice = await scopedDb.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(invoice => invoice.Id == foreignInvoiceId)
            .Select(invoice => new
            {
                invoice.VersionGroupId,
                invoice.VersionNumber,
                invoice.IsLatestVersion,
                invoice.IsDeleted,
                invoice.TenantCode,
                invoice.OfficeCode,
                invoice.ResponsibleOfficeCode,
                invoice.Revision,
                invoice.UpdatedAtUtc
            })
            .SingleAsync();
        var beforeForeignLine = Assert.Single(
            await ReadLineStateAsync(scopedDb, foreignLineId));
        var beforeForeignStock = await scopedDb.ItemWarehouseStocks
            .AsNoTracking()
            .Where(stock =>
                stock.ItemId == foreignItemId &&
                stock.WarehouseCode == foreignWarehouseCode)
            .Select(stock => new { stock.Quantity, stock.Revision })
            .SingleAsync();
        var beforeForeignItem = await scopedDb.Items
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item => item.Id == foreignItemId)
            .Select(item => new { item.CurrentStock, item.Revision })
            .SingleAsync();
        var beforeForeignProfile = await scopedDb.RentalBillingProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(profile => profile.Id == foreignProfileId)
            .Select(profile => new
            {
                profile.TenantCode,
                profile.OfficeCode,
                profile.ResponsibleOfficeCode,
                profile.BillingStatus,
                profile.SettlementStatus,
                profile.CompletionStatus,
                profile.SettledAmount,
                profile.OutstandingAmount,
                profile.BillingRunsJson,
                profile.Revision,
                profile.UpdatedAtUtc
            })
            .SingleAsync();
        var expectedRevision = await scopedDb.Invoices
            .IgnoreQueryFilters()
            .Where(invoice => invoice.Id == selectedInvoiceId)
            .Select(invoice => invoice.Revision)
            .SingleAsync();
        var request = CreateInvoiceMutationRequest(selectedInvoiceId, expectedRevision);
        var controller = CreateController(scopedDb, user);

        var response = purge
            ? await controller.Purge(request, CancellationToken.None)
            : await controller.Restore(request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var result = Assert.Single(payload.Results);
        Assert.True(result.Success, result.Message);
        Assert.Equal(1, payload.SucceededCount);

        scopedDb.ChangeTracker.Clear();
        if (purge)
        {
            Assert.False(await scopedDb.Invoices
                .IgnoreQueryFilters()
                .AnyAsync(invoice => invoice.Id == selectedInvoiceId));
            Assert.False(await scopedDb.InvoiceLines
                .IgnoreQueryFilters()
                .AnyAsync(line => line.Id == selectedLineId));
        }
        else
        {
            var selectedAfterRestore = await scopedDb.Invoices
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(invoice => invoice.Id == selectedInvoiceId);
            Assert.False(selectedAfterRestore.IsDeleted);
            Assert.True(selectedAfterRestore.IsLatestVersion);
            Assert.False(Assert.Single(
                await ReadLineStateAsync(scopedDb, selectedLineId)).IsDeleted);
            Assert.Equal(
                18m,
                await scopedDb.ItemWarehouseStocks
                    .AsNoTracking()
                    .Where(stock =>
                        stock.ItemId == selectedItemId &&
                        stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
                    .Select(stock => stock.Quantity)
                    .SingleAsync());
        }

        Assert.Equal(
            beforeForeignInvoice,
            await scopedDb.Invoices
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(invoice => invoice.Id == foreignInvoiceId)
                .Select(invoice => new
                {
                    invoice.VersionGroupId,
                    invoice.VersionNumber,
                    invoice.IsLatestVersion,
                    invoice.IsDeleted,
                    invoice.TenantCode,
                    invoice.OfficeCode,
                    invoice.ResponsibleOfficeCode,
                    invoice.Revision,
                    invoice.UpdatedAtUtc
                })
                .SingleAsync());
        Assert.Equal(
            beforeForeignLine,
            Assert.Single(await ReadLineStateAsync(scopedDb, foreignLineId)));
        Assert.Equal(
            beforeForeignStock,
            await scopedDb.ItemWarehouseStocks
                .AsNoTracking()
                .Where(stock =>
                    stock.ItemId == foreignItemId &&
                    stock.WarehouseCode == foreignWarehouseCode)
                .Select(stock => new { stock.Quantity, stock.Revision })
                .SingleAsync());
        Assert.Equal(
            beforeForeignItem,
            await scopedDb.Items
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(item => item.Id == foreignItemId)
                .Select(item => new { item.CurrentStock, item.Revision })
                .SingleAsync());
        Assert.Equal(
            beforeForeignProfile,
            await scopedDb.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(profile => profile.Id == foreignProfileId)
                .Select(profile => new
                {
                    profile.TenantCode,
                    profile.OfficeCode,
                    profile.ResponsibleOfficeCode,
                    profile.BillingStatus,
                    profile.SettlementStatus,
                    profile.CompletionStatus,
                    profile.SettledAmount,
                    profile.OutstandingAmount,
                    profile.BillingRunsJson,
                    profile.Revision,
                    profile.UpdatedAtUtc
                })
                .SingleAsync());
        Assert.DoesNotContain(
            await scopedDb.RecycleBinPurgeRecords
                .AsNoTracking()
                .Where(record => record.EntityId == foreignInvoiceId)
                .ToListAsync(),
            record => record.EntityId == foreignInvoiceId);
    }

    [Fact]
    public async Task RestoreInvoiceGroup_LegacyInvalidScope_UsesLinkedCustomerOperationalScope()
    {
        var user = CreateOfficeUser(
            TenantScopeCatalog.Itworld,
            OfficeCodeCatalog.Itworld);
        var groupId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var legacyInvoiceId = Guid.NewGuid();
        var canonicalInvoiceId = Guid.NewGuid();

        await using (var seedDb = CreateDbContext(user))
        {
            seedDb.Customers.Add(
                CreateCustomer(
                    customerId,
                    OfficeCodeCatalog.Itworld,
                    isDeleted: true,
                    tenantCode: TenantScopeCatalog.Itworld,
                    officeCode: OfficeCodeCatalog.Itworld));
            seedDb.Invoices.AddRange(
                CreateInvoice(
                    legacyInvoiceId,
                    customerId,
                    groupId,
                    1,
                    isLatest: false,
                    tenantCode: string.Empty,
                    officeCode: "LEGACY_INVALID_OFFICE",
                    responsibleOfficeCode: string.Empty,
                    sourceWarehouseCode: OfficeCodeCatalog.ItworldMainWarehouse),
                CreateInvoice(
                    canonicalInvoiceId,
                    customerId,
                    groupId,
                    2,
                    isLatest: true,
                    tenantCode: TenantScopeCatalog.Itworld,
                    officeCode: OfficeCodeCatalog.Itworld,
                    responsibleOfficeCode: OfficeCodeCatalog.Itworld,
                    sourceWarehouseCode: OfficeCodeCatalog.ItworldMainWarehouse));
            await seedDb.SaveChangesAsync();
        }

        await using var scopedDb = CreateDbContext(user);
        var expectedRevision = await scopedDb.Invoices
            .IgnoreQueryFilters()
            .Where(invoice => invoice.Id == legacyInvoiceId)
            .Select(invoice => invoice.Revision)
            .SingleAsync();
        var controller = CreateController(scopedDb, user);

        var response = await controller.Restore(
            CreateInvoiceMutationRequest(legacyInvoiceId, expectedRevision),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<RecycleBinMutationResultDto>(ok.Value);
        var result = Assert.Single(payload.Results);
        Assert.True(result.Success, result.Message);

        scopedDb.ChangeTracker.Clear();
        var invoices = await ReadInvoiceStateAsync(scopedDb, groupId);
        Assert.Equal(2, invoices.Length);
        Assert.All(invoices, invoice => Assert.False(invoice.IsDeleted));
        Assert.Equal(canonicalInvoiceId, Assert.Single(
            invoices,
            invoice => invoice.IsLatestVersion).Id);
        Assert.False(Assert.Single(
            await ReadCustomerStateAsync(scopedDb, customerId)).IsDeleted);
    }

    public void Dispose() => _connection.Dispose();

    private AppDbContext CreateDbContext(TestCurrentUserContext currentUser)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        return new AppDbContext(options, currentUser, new RevisionClock());
    }

    private static RecycleBinController CreateController(
        AppDbContext dbContext,
        TestCurrentUserContext currentUser)
    {
        var revisionClock = new RevisionClock();
        return new RecycleBinController(
            dbContext,
            new OfficeScopeService(currentUser, dbContext),
            NoOpStoredFileReferenceReconciler.Instance,
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, revisionClock),
            new RentalSettlementRecalculationService(dbContext),
            NoOpStoredFileDeferredDeletionQueue.Instance);
    }

    private static RecycleBinMutationRequest CreateInvoiceMutationRequest(
        Guid invoiceId,
        long expectedRevision)
        => new()
        {
            Items =
            [
                new RecycleBinMutationTargetDto
                {
                    EntityId = invoiceId,
                    Kind = "invoice",
                    ExpectedRevision = expectedRevision
                }
            ]
        };

    private static Customer CreateCustomer(
        Guid id,
        string responsibleOfficeCode,
        bool isDeleted,
        string? tenantCode = null,
        string? officeCode = null)
        => new()
        {
            Id = id,
            TenantCode = tenantCode ?? TenantScopeCatalog.UsenetGroup,
            OfficeCode = officeCode ?? OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = responsibleOfficeCode,
            NameOriginal = $"invoice-group-customer-{id:N}",
            NameMatchKey = $"invoicegroupcustomer{id:N}",
            IsDeleted = isDeleted
        };

    private static Invoice CreateInvoice(
        Guid id,
        Guid customerId,
        Guid groupId,
        int versionNumber,
        bool isLatest,
        string? tenantCode = null,
        string? officeCode = null,
        string? responsibleOfficeCode = null,
        string? sourceWarehouseCode = null,
        DateTime? updatedAtUtc = null,
        bool isDeleted = true)
        => new()
        {
            Id = id,
            CustomerId = customerId,
            TenantCode = tenantCode ?? TenantScopeCatalog.UsenetGroup,
            OfficeCode = officeCode ?? OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = responsibleOfficeCode ?? OfficeCodeCatalog.Usenet,
            SourceWarehouseCode = sourceWarehouseCode ?? OfficeCodeCatalog.UsenetMainWarehouse,
            InvoiceNumber = $"GROUP-{versionNumber:000}",
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 7, versionNumber),
            VersionGroupId = groupId,
            VersionNumber = versionNumber,
            IsLatestVersion = isLatest,
            IsDeleted = isDeleted,
            UpdatedAtUtc = updatedAtUtc ?? DateTime.UtcNow
        };

    private static RentalBillingProfile CreateRentalProfile(
        Guid id,
        Guid customerId,
        string tenantCode,
        string profileKey,
        string? officeCode = null,
        string? responsibleOfficeCode = null)
        => new()
        {
            Id = id,
            TenantCode = tenantCode,
            OfficeCode = officeCode ?? OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = responsibleOfficeCode ?? OfficeCodeCatalog.Itworld,
            ManagementCompanyCode = officeCode ?? OfficeCodeCatalog.Usenet,
            ProfileKey = profileKey,
            CustomerId = customerId,
            CustomerName = profileKey,
            MonthlyAmount = 500m,
            BillingMethod = "CASH",
            BillingStatus = "COMPLETED",
            SettlementStatus = "SETTLED",
            CompletionStatus = "COMPLETED",
            SettledAmount = 200m,
            OutstandingAmount = 0m,
            BillingRunsJson = "[]",
            IsActive = true
        };

    private static InvoiceLine CreateLine(
        Guid id,
        Guid invoiceId,
        decimal quantity,
        string remark,
        Guid? itemId = null,
        bool isDeleted = true)
        => new()
        {
            Id = id,
            InvoiceId = invoiceId,
            ItemId = itemId,
            ItemNameOriginal = remark,
            ItemTrackingType = ItemTrackingTypes.Stock,
            Unit = "EA",
            Quantity = quantity,
            UnitPrice = 100m,
            LineAmount = quantity * 100m,
            Remark = remark,
            IsDeleted = isDeleted
        };

    private static Task<(Guid Id, Guid VersionGroupId, int VersionNumber, bool IsLatestVersion, bool IsDeleted, long Revision)[]>
        ReadInvoiceStateAsync(
            AppDbContext dbContext,
            Guid groupId)
        => dbContext.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(invoice => invoice.VersionGroupId == groupId)
            .OrderBy(invoice => invoice.Id)
            .Select(invoice => new ValueTuple<Guid, Guid, int, bool, bool, long>(
                invoice.Id,
                invoice.VersionGroupId,
                invoice.VersionNumber,
                invoice.IsLatestVersion,
                invoice.IsDeleted,
                invoice.Revision))
            .ToArrayAsync();

    private static Task<(Guid Id, bool IsDeleted, long Revision)[]> ReadCustomerStateAsync(
        AppDbContext dbContext,
        params Guid[] customerIds)
        => dbContext.Customers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(customer => customerIds.Contains(customer.Id))
            .OrderBy(customer => customer.Id)
            .Select(customer => new ValueTuple<Guid, bool, long>(
                customer.Id,
                customer.IsDeleted,
                customer.Revision))
            .ToArrayAsync();

    private static Task<(Guid Id, Guid InvoiceId, bool IsDeleted, decimal Quantity, string Remark)[]> ReadLineStateAsync(
        AppDbContext dbContext,
        params Guid[] lineIds)
        => dbContext.InvoiceLines
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(line => lineIds.Contains(line.Id))
            .OrderBy(line => line.Id)
            .Select(line => new ValueTuple<Guid, Guid, bool, decimal, string>(
                line.Id,
                line.InvoiceId,
                line.IsDeleted,
                line.Quantity,
                line.Remark))
            .ToArrayAsync();

    private static TestCurrentUserContext CreateOfficeUser(
        string? tenantCode = null,
        string? officeCode = null)
        => new()
        {
            Username = "invoice-group-office-user",
            TenantCode = tenantCode ?? TenantScopeCatalog.UsenetGroup,
            OfficeCode = officeCode ?? OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = [PermissionNames.DataBackupRestore, PermissionNames.PaymentEdit]
        };

    private static TestCurrentUserContext CreateAdminUser()
        => new()
        {
            Username = "invoice-group-global-admin",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin,
            IsAdmin = true,
            Permissions = [PermissionNames.DataBackupRestore]
        };

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
