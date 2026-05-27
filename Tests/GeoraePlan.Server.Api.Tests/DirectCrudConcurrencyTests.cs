using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Server.Api.Services;
using 거래플랜.Server.Api.Utilities;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class DirectCrudConcurrencyTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public DirectCrudConcurrencyTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
    }

    [Fact]
    public async Task CustomersController_Update_ReturnsConflict_WhenExpectedRevisionDoesNotMatch()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "기존 거래처",
            NameMatchKey = "기존거래처",
            TradeType = "매출"
        };
        dbContext.Customers.Add(customer);
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Customers.IgnoreQueryFilters().FirstAsync(x => x.Id == customer.Id);
        var dto = stored.ToDto();
        dto.NameOriginal = "수정 거래처";
        dto.NameMatchKey = "수정거래처";
        dto.ExpectedRevision = stored.Revision + 1;

        var controller = new CustomersController(dbContext, new OfficeScopeService(currentUser, dbContext), new StubCentralFileStorage());
        var response = await controller.Update(stored.Id, dto, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(response.Result);
        var payload = Assert.IsType<ExpectedRevisionConflictResponse>(conflict.Value);
        Assert.Equal(nameof(Customer), payload.EntityName);
        Assert.Equal(stored.Id, payload.EntityId);
        Assert.Equal(stored.Revision, payload.CurrentRevision);
    }

    [Fact]
    public async Task CustomersController_Delete_ReturnsConflict_WhenExpectedRevisionDoesNotMatch()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "삭제 대상 거래처",
            NameMatchKey = "삭제대상거래처",
            TradeType = "매출"
        };
        dbContext.Customers.Add(customer);
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Customers.IgnoreQueryFilters().FirstAsync(x => x.Id == customer.Id);
        var controller = new CustomersController(dbContext, new OfficeScopeService(currentUser, dbContext), new StubCentralFileStorage());

        var response = await controller.Delete(stored.Id, stored.Revision + 1, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(response);
        var payload = Assert.IsType<ExpectedRevisionConflictResponse>(conflict.Value);
        Assert.Equal(nameof(Customer), payload.EntityName);
        Assert.Equal(stored.Revision, payload.CurrentRevision);
    }

    [Fact]
    public async Task ItemsController_Update_ReturnsConflict_WhenRevisionFallbackDoesNotMatch()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            NameOriginal = "ITEM-A",
            NameMatchKey = "ITEMA"
        };
        dbContext.Items.Add(item);
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Items.IgnoreQueryFilters().FirstAsync(x => x.Id == item.Id);
        var dto = stored.ToDto();
        dto.NameOriginal = "ITEM-B";
        dto.NameMatchKey = "ITEMB";
        dto.Revision = stored.Revision + 1;

        var controller = new ItemsController(dbContext, new OfficeScopeService(currentUser, dbContext));
        var response = await controller.Update(stored.Id, dto, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(response.Result);
        var payload = Assert.IsType<ExpectedRevisionConflictResponse>(conflict.Value);
        Assert.Equal(nameof(Item), payload.EntityName);
    }

    [Fact]
    public async Task ItemsController_Delete_RemovesWarehouseStockRows()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Delete stock item",
            NameMatchKey = "DELETESTOCKITEM",
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 4m
        };
        dbContext.Items.Add(item);
        dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
        {
            ItemId = item.Id,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 4m,
            Revision = 1
        });
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Items.IgnoreQueryFilters().SingleAsync(row => row.Id == item.Id);
        var controller = new ItemsController(dbContext, new OfficeScopeService(currentUser, dbContext));

        var response = await controller.Delete(stored.Id, stored.Revision, CancellationToken.None);

        Assert.IsType<NoContentResult>(response);
        Assert.True(await dbContext.Items.IgnoreQueryFilters()
            .Where(row => row.Id == item.Id)
            .Select(row => row.IsDeleted)
            .SingleAsync());
        Assert.False(await dbContext.ItemWarehouseStocks.AnyAsync(stock => stock.ItemId == item.Id));
    }

    [Fact]
    public async Task DbInitializer_RepairItemCurrentStockSnapshots_RecalculatesFromWarehouseTotals()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Repair stock item",
            NameMatchKey = "REPAIRSTOCKITEM",
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 0m
        };
        dbContext.Items.Add(item);
        dbContext.ItemWarehouseStocks.AddRange(
            new ItemWarehouseStock
            {
                ItemId = item.Id,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 1m,
                Revision = 1
            },
            new ItemWarehouseStock
            {
                ItemId = item.Id,
                WarehouseCode = "USENET_SUB",
                Quantity = 2m,
                Revision = 2
            });
        await dbContext.SaveChangesAsync();

        var method = typeof(DbInitializer).GetMethod(
            "RepairItemCurrentStockSnapshotsAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var repaired = await Assert.IsType<Task<int>>(method.Invoke(null, new object[] { dbContext, CancellationToken.None }));
        await dbContext.SaveChangesAsync();

        Assert.Equal(1, repaired);
        Assert.Equal(3m, await dbContext.Items.IgnoreQueryFilters()
            .Where(row => row.Id == item.Id)
            .Select(row => row.CurrentStock)
            .SingleAsync());
    }

    [Fact]
    public async Task InvoicesController_Update_ReturnsConflict_WhenExpectedRevisionDoesNotMatch()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "청구 거래처",
            NameMatchKey = "청구거래처",
            TradeType = "매출"
        };
        dbContext.Customers.Add(customer);

        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "INV-001",
            InvoiceDate = new DateOnly(2026, 4, 11)
        };
        dbContext.Invoices.Add(invoice);
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Invoices
            .IgnoreQueryFilters()
            .Include(x => x.Customer)
            .Include(x => x.Lines)
            .Include(x => x.Payments)
            .FirstAsync(x => x.Id == invoice.Id);
        var dto = stored.ToDto();
        dto.ExpectedRevision = stored.Revision + 1;

        var controller = new InvoicesController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()));

        var response = await controller.Update(stored.Id, dto, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(response.Result);
        var payload = Assert.IsType<ExpectedRevisionConflictResponse>(conflict.Value);
        Assert.Equal(nameof(Invoice), payload.EntityName);
    }

    [Fact]
    public async Task DirectCrud_AllowsConsecutiveUpdates_WhenClientUsesReturnedRevision()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Consecutive customer",
            NameMatchKey = "CONSECUTIVECUSTOMER",
            TradeType = "Sales"
        };
        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Consecutive item",
            NameMatchKey = "CONSECUTIVEITEM",
            TrackingType = ItemTrackingTypes.NonStock
        };
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "INV-CONSECUTIVE-001",
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 5, 26),
            TotalAmount = 1000m,
            SupplyAmount = 909m,
            VatAmount = 91m
        };
        var invoiceLine = new InvoiceLine
        {
            Id = Guid.NewGuid(),
            InvoiceId = invoice.Id,
            ItemId = item.Id,
            ItemNameOriginal = item.NameOriginal,
            ItemTrackingType = ItemTrackingTypes.NonStock,
            Unit = "EA",
            Quantity = 1m,
            UnitPrice = 1000m,
            LineAmount = 1000m
        };
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            InvoiceId = invoice.Id,
            PaymentDate = new DateOnly(2026, 5, 26),
            Amount = 100m,
            Note = "initial"
        };
        dbContext.Customers.Add(customer);
        dbContext.Items.Add(item);
        dbContext.Invoices.Add(invoice);
        dbContext.InvoiceLines.Add(invoiceLine);
        dbContext.Payments.Add(payment);
        await dbContext.SaveChangesAsync();

        var customerController = new CustomersController(dbContext, new OfficeScopeService(currentUser, dbContext), new StubCentralFileStorage());
        var itemController = new ItemsController(dbContext, new OfficeScopeService(currentUser, dbContext));
        var invoiceController = new InvoicesController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()));
        var paymentController = new PaymentsController(dbContext, new OfficeScopeService(currentUser, dbContext), new StubCentralFileStorage());

        var customerDto = (await dbContext.Customers.IgnoreQueryFilters().SingleAsync(row => row.Id == customer.Id)).ToDto();
        customerDto.ExpectedRevision = customerDto.Revision;
        customerDto.Notes = "first save";
        var savedCustomer = AssertOk(await customerController.Update(customer.Id, customerDto, CancellationToken.None));
        savedCustomer.ExpectedRevision = savedCustomer.Revision;
        savedCustomer.Notes = "second save";
        var savedCustomerAgain = AssertOk(await customerController.Update(customer.Id, savedCustomer, CancellationToken.None));
        Assert.Equal("second save", savedCustomerAgain.Notes);

        var itemDto = (await dbContext.Items.IgnoreQueryFilters().SingleAsync(row => row.Id == item.Id)).ToDto();
        itemDto.ExpectedRevision = itemDto.Revision;
        itemDto.SimpleMemo = "first save";
        var savedItem = AssertOk(await itemController.Update(item.Id, itemDto, CancellationToken.None));
        savedItem.ExpectedRevision = savedItem.Revision;
        savedItem.SimpleMemo = "second save";
        var savedItemAgain = AssertOk(await itemController.Update(item.Id, savedItem, CancellationToken.None));
        Assert.Equal("second save", savedItemAgain.SimpleMemo);

        var invoiceDto = (await dbContext.Invoices
            .IgnoreQueryFilters()
            .Include(row => row.Customer)
            .Include(row => row.Lines)
            .Include(row => row.Payments)
            .SingleAsync(row => row.Id == invoice.Id)).ToDto();
        invoiceDto.ExpectedRevision = invoiceDto.Revision;
        invoiceDto.Memo = "first save";
        var savedInvoice = AssertOk(await invoiceController.Update(invoice.Id, invoiceDto, CancellationToken.None));
        savedInvoice.ExpectedRevision = savedInvoice.Revision;
        savedInvoice.Memo = "second save";
        var savedInvoiceAgain = AssertOk(await invoiceController.Update(invoice.Id, savedInvoice, CancellationToken.None));
        Assert.Equal("second save", savedInvoiceAgain.Memo);

        var paymentDto = (await dbContext.Payments
            .IgnoreQueryFilters()
            .Include(row => row.Invoice)
            .ThenInclude(invoiceRow => invoiceRow!.Customer)
            .Include(row => row.Attachments)
            .SingleAsync(row => row.Id == payment.Id)).ToDto();
        paymentDto.ExpectedRevision = paymentDto.Revision;
        paymentDto.Note = "first save";
        var savedPayment = AssertOk(await paymentController.Update(payment.Id, paymentDto, CancellationToken.None));
        savedPayment.ExpectedRevision = savedPayment.Revision;
        savedPayment.Note = "second save";
        var savedPaymentAgain = AssertOk(await paymentController.Update(payment.Id, savedPayment, CancellationToken.None));
        Assert.Equal("second save", savedPaymentAgain.Note);
    }

    [Fact]
    public async Task InvoicesController_Create_PreservesExtendedInvoiceLineFields()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "렌탈 거래처",
            NameMatchKey = "렌탈거래처",
            TradeType = "매출"
        };
        dbContext.Customers.Add(customer);
        await dbContext.SaveChangesAsync();

        var invoiceId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        var dto = new InvoiceDto
        {
            Id = invoiceId,
            CustomerId = customer.Id,
            CustomerName = customer.NameOriginal,
            TenantCode = customer.TenantCode,
            OfficeCode = customer.OfficeCode,
            ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 4, 13),
            Lines =
            [
                new InvoiceLineDto
                {
                    Id = lineId,
                    InvoiceId = invoiceId,
                    ItemNameOriginal = "A3 컬러복합기",
                    Unit = "대",
                    Quantity = 1m,
                    UnitPrice = 150000m,
                    LineAmount = 150000m,
                    SerialNumber = "SN-2603-001",
                    MaterialNumber = "2603-001",
                    InstallLocation = "2층 사무실",
                    RentalStartDate = new DateOnly(2026, 4, 1),
                    RentalEndDate = new DateOnly(2029, 3, 31),
                    ItemTrackingType = ItemTrackingTypes.Asset
                }
            ]
        };

        var controller = new InvoicesController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()));

        var response = await controller.Create(dto, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var created = Assert.IsType<InvoiceDto>(ok.Value);
        var line = Assert.Single(created.Lines);

        Assert.Equal("SN-2603-001", line.SerialNumber);
        Assert.Equal("2603-001", line.MaterialNumber);
        Assert.Equal("2층 사무실", line.InstallLocation);
        Assert.Equal(new DateOnly(2026, 4, 1), line.RentalStartDate);
        Assert.Equal(new DateOnly(2029, 3, 31), line.RentalEndDate);

        var storedLine = await dbContext.InvoiceLines.IgnoreQueryFilters().SingleAsync(x => x.Id == lineId);
        Assert.Equal("SN-2603-001", storedLine.SerialNumber);
        Assert.Equal("2603-001", storedLine.MaterialNumber);
        Assert.Equal("2층 사무실", storedLine.InstallLocation);
        Assert.Equal(new DateOnly(2026, 4, 1), storedLine.RentalStartDate);
        Assert.Equal(new DateOnly(2029, 3, 31), storedLine.RentalEndDate);
    }

    [Fact]
    public async Task InvoicesController_SalesCreateUpdateDelete_AdjustsWarehouseStockSnapshots()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Stock customer",
            NameMatchKey = "STOCKCUSTOMER",
            TradeType = "매출"
        };
        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Stock item",
            NameMatchKey = "STOCKITEM",
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 5m
        };
        dbContext.Customers.Add(customer);
        dbContext.Items.Add(item);
        dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
        {
            ItemId = item.Id,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 5m,
            Revision = 1
        });
        await dbContext.SaveChangesAsync();

        var controller = new InvoicesController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()));
        var invoiceId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        var createDto = new InvoiceDto
        {
            Id = invoiceId,
            CustomerId = customer.Id,
            CustomerName = customer.NameOriginal,
            TenantCode = customer.TenantCode,
            OfficeCode = customer.OfficeCode,
            ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
            SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 5, 21),
            Lines =
            [
                new InvoiceLineDto
                {
                    Id = lineId,
                    InvoiceId = invoiceId,
                    ItemId = item.Id,
                    ItemNameOriginal = item.NameOriginal,
                    ItemTrackingType = ItemTrackingTypes.Stock,
                    Unit = "EA",
                    Quantity = 2m,
                    UnitPrice = 1000m,
                    LineAmount = 2000m
                }
            ]
        };

        var createResponse = await controller.Create(createDto, CancellationToken.None);
        Assert.IsType<OkObjectResult>(createResponse.Result);
        Assert.Equal(3m, await dbContext.ItemWarehouseStocks
            .Where(stock => stock.ItemId == item.Id && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
        Assert.Equal(3m, (await dbContext.Items.IgnoreQueryFilters().SingleAsync(row => row.Id == item.Id)).CurrentStock);

        var storedInvoice = await dbContext.Invoices
            .IgnoreQueryFilters()
            .Include(invoice => invoice.Customer)
            .Include(invoice => invoice.Lines)
            .SingleAsync(invoice => invoice.Id == invoiceId);
        var updateDto = storedInvoice.ToDto();
        updateDto.ExpectedRevision = storedInvoice.Revision;
        updateDto.Lines[0].Quantity = 1m;
        updateDto.Lines[0].LineAmount = 1000m;

        var updateResponse = await controller.Update(invoiceId, updateDto, CancellationToken.None);
        Assert.IsType<OkObjectResult>(updateResponse.Result);
        Assert.Equal(4m, await dbContext.ItemWarehouseStocks
            .Where(stock => stock.ItemId == item.Id && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
        Assert.Equal(4m, (await dbContext.Items.IgnoreQueryFilters().SingleAsync(row => row.Id == item.Id)).CurrentStock);

        var latestInvoice = await dbContext.Invoices.IgnoreQueryFilters().SingleAsync(invoice => invoice.Id == invoiceId);
        var deleteResponse = await controller.Delete(invoiceId, latestInvoice.Revision, CancellationToken.None);

        Assert.IsType<NoContentResult>(deleteResponse);
        Assert.Equal(5m, await dbContext.ItemWarehouseStocks
            .Where(stock => stock.ItemId == item.Id && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
        Assert.Equal(5m, (await dbContext.Items.IgnoreQueryFilters().SingleAsync(row => row.Id == item.Id)).CurrentStock);
        Assert.True(await dbContext.InvoiceLines.IgnoreQueryFilters()
            .AnyAsync(line => line.InvoiceId == invoiceId));
        Assert.True(await dbContext.InvoiceLines.IgnoreQueryFilters()
            .Where(line => line.InvoiceId == invoiceId)
            .AllAsync(line => line.IsDeleted));
    }

    [Fact]
    public async Task InvoicesController_SalesCreate_RejectsWhenWarehouseStockWouldBecomeNegative()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Shortage customer",
            NameMatchKey = "SHORTAGECUSTOMER",
            TradeType = "Sales"
        };
        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Shortage stock item",
            NameMatchKey = "SHORTAGESTOCKITEM",
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 1m
        };
        dbContext.Customers.Add(customer);
        dbContext.Items.Add(item);
        dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
        {
            ItemId = item.Id,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 1m,
            Revision = 1
        });
        await dbContext.SaveChangesAsync();

        var controller = new InvoicesController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()));
        var invoiceId = Guid.NewGuid();

        var response = await controller.Create(new InvoiceDto
        {
            Id = invoiceId,
            CustomerId = customer.Id,
            CustomerName = customer.NameOriginal,
            TenantCode = customer.TenantCode,
            OfficeCode = customer.OfficeCode,
            ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
            SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 5, 28),
            Lines =
            [
                new InvoiceLineDto
                {
                    Id = Guid.NewGuid(),
                    InvoiceId = invoiceId,
                    ItemId = item.Id,
                    ItemNameOriginal = item.NameOriginal,
                    ItemTrackingType = ItemTrackingTypes.Stock,
                    Unit = "EA",
                    Quantity = 2m,
                    UnitPrice = 1000m,
                    LineAmount = 2000m
                }
            ]
        }, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(response.Result);
        var message = badRequest.Value?.GetType().GetProperty("message")?.GetValue(badRequest.Value)?.ToString();
        Assert.Contains("재고", message);
        Assert.False(await dbContext.Invoices.IgnoreQueryFilters().AnyAsync(invoice => invoice.Id == invoiceId));
        Assert.Equal(1m, await dbContext.ItemWarehouseStocks
            .Where(stock => stock.ItemId == item.Id && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
        Assert.Equal(1m, (await dbContext.Items.IgnoreQueryFilters().SingleAsync(row => row.Id == item.Id)).CurrentStock);
    }

    [Fact]
    public async Task InvoicesController_PurchaseCreateUpdateDelete_AdjustsWarehouseStockSnapshots()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Purchase stock vendor",
            NameMatchKey = "PURCHASESTOCKVENDOR",
            TradeType = "Purchase"
        };
        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Purchase stock item",
            NameMatchKey = "PURCHASESTOCKITEM",
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 5m
        };
        dbContext.Customers.Add(customer);
        dbContext.Items.Add(item);
        dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
        {
            ItemId = item.Id,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 5m,
            Revision = 1
        });
        await dbContext.SaveChangesAsync();

        var controller = new InvoicesController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()));
        var invoiceId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        var createDto = new InvoiceDto
        {
            Id = invoiceId,
            CustomerId = customer.Id,
            CustomerName = customer.NameOriginal,
            TenantCode = customer.TenantCode,
            OfficeCode = customer.OfficeCode,
            ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
            SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            VoucherType = VoucherType.Purchase,
            InvoiceDate = new DateOnly(2026, 5, 21),
            Lines =
            [
                new InvoiceLineDto
                {
                    Id = lineId,
                    InvoiceId = invoiceId,
                    ItemId = item.Id,
                    ItemNameOriginal = item.NameOriginal,
                    ItemTrackingType = ItemTrackingTypes.Stock,
                    Unit = "EA",
                    Quantity = 2m,
                    UnitPrice = 1000m,
                    LineAmount = 2000m
                }
            ]
        };

        var createResponse = await controller.Create(createDto, CancellationToken.None);
        Assert.IsType<OkObjectResult>(createResponse.Result);
        Assert.Equal(7m, await dbContext.ItemWarehouseStocks
            .Where(stock => stock.ItemId == item.Id && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
        Assert.Equal(7m, (await dbContext.Items.IgnoreQueryFilters().SingleAsync(row => row.Id == item.Id)).CurrentStock);

        var storedInvoice = await dbContext.Invoices
            .IgnoreQueryFilters()
            .Include(invoice => invoice.Customer)
            .Include(invoice => invoice.Lines)
            .SingleAsync(invoice => invoice.Id == invoiceId);
        var updateDto = storedInvoice.ToDto();
        updateDto.ExpectedRevision = storedInvoice.Revision;
        updateDto.Lines[0].Quantity = 1m;
        updateDto.Lines[0].LineAmount = 1000m;

        var updateResponse = await controller.Update(invoiceId, updateDto, CancellationToken.None);
        Assert.IsType<OkObjectResult>(updateResponse.Result);
        Assert.Equal(6m, await dbContext.ItemWarehouseStocks
            .Where(stock => stock.ItemId == item.Id && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
        Assert.Equal(6m, (await dbContext.Items.IgnoreQueryFilters().SingleAsync(row => row.Id == item.Id)).CurrentStock);

        var latestInvoice = await dbContext.Invoices.IgnoreQueryFilters().SingleAsync(invoice => invoice.Id == invoiceId);
        var deleteResponse = await controller.Delete(invoiceId, latestInvoice.Revision, CancellationToken.None);

        Assert.IsType<NoContentResult>(deleteResponse);
        Assert.Equal(5m, await dbContext.ItemWarehouseStocks
            .Where(stock => stock.ItemId == item.Id && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
        Assert.Equal(5m, (await dbContext.Items.IgnoreQueryFilters().SingleAsync(row => row.Id == item.Id)).CurrentStock);
        Assert.True(await dbContext.InvoiceLines.IgnoreQueryFilters()
            .AnyAsync(line => line.InvoiceId == invoiceId));
        Assert.True(await dbContext.InvoiceLines.IgnoreQueryFilters()
            .Where(line => line.InvoiceId == invoiceId)
            .AllAsync(line => line.IsDeleted));
    }

    [Fact]
    public async Task PaymentsController_Delete_ReturnsConflict_WhenExpectedRevisionDoesNotMatch()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "수금 거래처",
            NameMatchKey = "수금거래처",
            TradeType = "매출"
        };
        dbContext.Customers.Add(customer);

        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "INV-PAY-001",
            InvoiceDate = new DateOnly(2026, 4, 11)
        };
        dbContext.Invoices.Add(invoice);

        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            InvoiceId = invoice.Id,
            PaymentDate = new DateOnly(2026, 4, 11),
            Amount = 10000m,
            Note = "테스트 수금"
        };
        dbContext.Payments.Add(payment);
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Payments
            .IgnoreQueryFilters()
            .Include(x => x.Invoice)
            .ThenInclude(invoice => invoice!.Customer)
            .FirstAsync(x => x.Id == payment.Id);
        var controller = new PaymentsController(dbContext, new OfficeScopeService(currentUser, dbContext), new StubCentralFileStorage());

        var response = await controller.Delete(stored.Id, stored.Revision + 1, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(response);
        var payload = Assert.IsType<ExpectedRevisionConflictResponse>(conflict.Value);
        Assert.Equal(nameof(Payment), payload.EntityName);
    }

    [Fact]
    public async Task CompanyProfileController_Upsert_ReturnsConflict_WhenExpectedRevisionDoesNotMatch()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var profile = new CompanyProfile
        {
            Id = Guid.NewGuid(),
            OfficeCode = OfficeCodeCatalog.Usenet,
            ProfileName = "기본",
            TradeName = "유즈넷",
            IsActive = true
        };
        dbContext.CompanyProfiles.Add(profile);
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.CompanyProfiles.IgnoreQueryFilters().FirstAsync(x => x.Id == profile.Id);
        var dto = stored.ToDto();
        dto.ExpectedRevision = stored.Revision + 1;

        var controller = new CompanyProfileController(dbContext);
        var response = await controller.Upsert(dto, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(response.Result);
    }

    [Fact]
    public async Task UsersController_Update_ReturnsConflict_WhenExpectedRevisionDoesNotMatch()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var user = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "user1",
            PasswordHash = "hash",
            Role = "User",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            IsActive = true
        };
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Users.IgnoreQueryFilters().Include(x => x.Permissions).FirstAsync(x => x.Id == user.Id);
        var controller = new UsersController(dbContext, currentUser);
        var response = await controller.Update(
            stored.Id,
            new UpdateUserRequest
            {
                ExpectedRevision = stored.Revision + 1,
                Username = stored.Username,
                Role = stored.Role,
                TenantCode = stored.TenantCode,
                OfficeCode = stored.OfficeCode,
                ScopeType = stored.ScopeType,
                IsActive = stored.IsActive,
                Permissions = []
            },
            CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(response.Result);
    }

    [Fact]
    public async Task TenantSettingsController_UpdateTenant_ReturnsConflict_WhenExpectedRevisionDoesNotMatch()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var tenant = new TenantDefinition
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            DisplayName = "유즈넷",
            StorageMode = TenantScopeCatalog.StorageSharedDatabase,
            IsActive = true
        };
        dbContext.TenantDefinitions.Add(tenant);
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.TenantDefinitions.IgnoreQueryFilters().FirstAsync(x => x.TenantCode == tenant.TenantCode);
        var controller = new TenantSettingsController(dbContext);
        var response = await controller.UpdateTenant(
            stored.TenantCode,
            new UpdateTenantDefinitionRequest
            {
                ExpectedRevision = stored.Revision + 1,
                DisplayName = stored.DisplayName,
                StorageMode = stored.StorageMode,
                Description = stored.Description,
                IsActive = stored.IsActive
            },
            CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(response.Result);
    }

    [Fact]
    public async Task UnitsAndCustomerCategories_UseOptimisticConcurrencyGuard()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var unit = new Unit
        {
            Id = Guid.NewGuid(),
            Name = "대"
        };
        var category = new CustomerCategory
        {
            Id = Guid.NewGuid(),
            Name = "기업"
        };
        dbContext.Units.Add(unit);
        dbContext.CustomerCategories.Add(category);
        await dbContext.SaveChangesAsync();

        var storedUnit = await dbContext.Units.IgnoreQueryFilters().FirstAsync(x => x.Id == unit.Id);
        var storedCategory = await dbContext.CustomerCategories.IgnoreQueryFilters().FirstAsync(x => x.Id == category.Id);

        var unitController = new UnitsController(dbContext);
        var categoryController = new CustomerCategoriesController(dbContext);

        var unitDto = storedUnit.ToDto();
        unitDto.ExpectedRevision = storedUnit.Revision + 1;
        var categoryDto = storedCategory.ToDto();
        categoryDto.ExpectedRevision = storedCategory.Revision + 1;

        var unitResponse = await unitController.Update(storedUnit.Id, unitDto, CancellationToken.None);
        var categoryResponse = await categoryController.Update(storedCategory.Id, categoryDto, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(unitResponse.Result);
        Assert.IsType<ConflictObjectResult>(categoryResponse.Result);
    }

    private static TDto AssertOk<TDto>(ActionResult<TDto> response)
    {
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        return Assert.IsType<TDto>(ok.Value);
    }

    private AppDbContext CreateDbContext(TestCurrentUserContext currentUser)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        var dbContext = new AppDbContext(options, currentUser, new RevisionClock());
        dbContext.Database.EnsureCreated();
        return dbContext;
    }

    private static TestCurrentUserContext CreateAdminUser()
        => new()
        {
            Username = "admin",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin,
            IsAdmin = true
        };

    public void Dispose()
    {
        _connection.Dispose();
    }

    private sealed class TestCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId { get; init; } = Guid.NewGuid();
        public string Username { get; init; } = string.Empty;
        public string TenantCode { get; init; } = TenantScopeCatalog.UsenetGroup;
        public string OfficeCode { get; init; } = OfficeCodeCatalog.Usenet;
        public string ScopeType { get; init; } = TenantScopeCatalog.ScopeOfficeOnly;
        public bool IsAdmin { get; init; }
        public bool IsGodMode { get; init; }
        public IReadOnlyCollection<string> Permissions { get; init; } = Array.Empty<string>();

        public bool HasPermission(string permission)
            => IsAdmin || IsGodMode || Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);
    }

    private sealed class StubInvoiceNumberService : IInvoiceNumberService
    {
        public Task<string> GenerateAsync(Guid customerId, DateOnly invoiceDate, CancellationToken cancellationToken = default)
            => Task.FromResult($"INV-{invoiceDate:yyyyMMdd}-0001");
    }

    private sealed class StubCentralFileStorage : ICentralFileStorage
    {
        public string RootPath => Path.GetTempPath();

        public Task<string> SaveBytesAsync(string category, string tenantKey, Guid fileId, string? fileName, byte[] content, CancellationToken cancellationToken = default)
            => Task.FromResult(Path.Combine(RootPath, category, tenantKey, fileId.ToString("N"), fileName ?? "file.bin"));

        public byte[] ReadBytes(string? storedPath, byte[]? fallbackContent)
            => fallbackContent ?? Array.Empty<byte>();

        public void DeleteIfExists(string? storedPath)
        {
        }
    }
}
