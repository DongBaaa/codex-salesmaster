using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Server.Api.Utilities;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using System.Text.Json;
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
    public async Task ItemsController_Create_EnsuresActiveItemCategoryOption()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var controller = new ItemsController(dbContext, new OfficeScopeService(currentUser, dbContext));
        var response = await controller.Create(new ItemDto
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Server direct item category",
            NameMatchKey = "SERVERDIRECTITEMCATEGORY",
            CategoryName = " A3 Copier ",
            ItemKind = ItemKinds.Product,
            TrackingType = ItemTrackingTypes.Stock,
            Unit = "EA"
        }, CancellationToken.None);

        var item = AssertOk<ItemDto>(response);

        Assert.Equal("A3 Copier", item.CategoryName);
        var option = await dbContext.ItemCategoryOptions.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("A3 Copier", option.Name);
        Assert.True(option.IsActive);
        Assert.False(option.IsDeleted);
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
    public async Task DbInitializer_PreservesNegativeWarehouseStockAndRecalculatesSnapshots()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Negative stock item",
            NameMatchKey = "NEGATIVESTOCKITEM",
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = -1m
        };
        dbContext.Items.Add(item);
        dbContext.ItemWarehouseStocks.AddRange(
            new ItemWarehouseStock
            {
                ItemId = item.Id,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = -1m,
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

        var repairNegativeMethod = typeof(DbInitializer).GetMethod(
            "RepairNegativeItemWarehouseStocksAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        var repairSnapshotMethod = typeof(DbInitializer).GetMethod(
            "RepairItemCurrentStockSnapshotsAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(repairNegativeMethod);
        Assert.NotNull(repairSnapshotMethod);

        var repairedNegativeRows = await Assert.IsType<Task<int>>(repairNegativeMethod!.Invoke(null, new object[] { dbContext, CancellationToken.None }));
        await dbContext.SaveChangesAsync();
        var repairedSnapshots = await Assert.IsType<Task<int>>(repairSnapshotMethod!.Invoke(null, new object[] { dbContext, CancellationToken.None }));
        await dbContext.SaveChangesAsync();

        Assert.Equal(0, repairedNegativeRows);
        Assert.Equal(1, repairedSnapshots);
        Assert.Equal(-1m, await dbContext.ItemWarehouseStocks
            .Where(row => row.ItemId == item.Id && row.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(row => row.Quantity)
            .SingleAsync());
        Assert.Equal(1m, await dbContext.Items.IgnoreQueryFilters()
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
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));

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
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));
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
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));

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
    public async Task InvoicesController_Create_ForbidsOutOfScopeItemLine()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "invoice-editor-usenet",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = new[] { PermissionNames.InvoiceEdit }
        };
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Allowed invoice customer",
            NameMatchKey = "ALLOWEDINVOICECUSTOMER",
            TradeType = "Sales"
        };
        var outOfScopeItem = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            NameOriginal = "Out of scope invoice item",
            NameMatchKey = "OUTOFSCOPEINVOICEITEM",
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 5m
        };
        dbContext.Customers.Add(customer);
        dbContext.Items.Add(outOfScopeItem);
        await dbContext.SaveChangesAsync();

        var dto = new InvoiceDto
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            VoucherType = VoucherType.Sales,
            SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            InvoiceDate = new DateOnly(2026, 6, 17),
            TotalAmount = 100m,
            SupplyAmount = 91m,
            VatAmount = 9m,
            Lines =
            [
                new InvoiceLineDto
                {
                    Id = Guid.NewGuid(),
                    ItemId = outOfScopeItem.Id,
                    ItemNameOriginal = outOfScopeItem.NameOriginal,
                    ItemTrackingType = ItemTrackingTypes.Stock,
                    Unit = "EA",
                    Quantity = 1m,
                    UnitPrice = 100m,
                    LineAmount = 100m
                }
            ]
        };

        var controller = new InvoicesController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));

        var response = await controller.Create(dto, CancellationToken.None);

        Assert.IsType<ForbidResult>(response.Result);
        Assert.False(await dbContext.Invoices.IgnoreQueryFilters().AnyAsync(row => row.Id == dto.Id));
        Assert.Equal(5m, await dbContext.Items.IgnoreQueryFilters()
            .Where(row => row.Id == outOfScopeItem.Id)
            .Select(row => row.CurrentStock)
            .SingleAsync());
        Assert.False(await dbContext.ItemWarehouseStocks
            .AnyAsync(row => row.ItemId == outOfScopeItem.Id));
    }

    [Fact]
    public async Task InvoicesController_Create_ForbidsReadSharedRentalBillingProfileLink()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "invoice-editor-usenet",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = new[] { PermissionNames.InvoiceEdit }
        };
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Allowed invoice rental customer",
            NameMatchKey = "ALLOWEDINVOICERENTALCUSTOMER",
            TradeType = "Sales"
        };
        var readSharedProfile = new RentalBillingProfile
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            ProfileKey = "READ-SHARED-INVOICE-PROFILE",
            CustomerName = "Read shared invoice profile",
            BillingDay = 25,
            MonthlyAmount = 100m
        };
        dbContext.Customers.Add(customer);
        dbContext.RentalBillingProfiles.Add(readSharedProfile);
        dbContext.DataSharingPolicies.Add(new DataSharingPolicy
        {
            SourceTenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Yeonsu,
            TargetTenantCode = TenantScopeCatalog.UsenetGroup,
            TargetOfficeCode = OfficeCodeCatalog.Usenet,
            ShareRentals = true,
            AllowTargetWrite = false
        });
        await dbContext.SaveChangesAsync();

        var dto = new InvoiceDto
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            VoucherType = VoucherType.Sales,
            SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            InvoiceDate = new DateOnly(2026, 6, 17),
            TotalAmount = 100m,
            SupplyAmount = 91m,
            VatAmount = 9m,
            LinkedRentalBillingProfileId = readSharedProfile.Id,
            LinkedRentalBillingRunId = Guid.NewGuid()
        };

        var controller = new InvoicesController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));

        var response = await controller.Create(dto, CancellationToken.None);

        Assert.IsType<ForbidResult>(response.Result);
        Assert.False(await dbContext.Invoices.IgnoreQueryFilters().AnyAsync(row => row.Id == dto.Id));
    }

    [Fact]
    public async Task SyncPush_RejectsInvoiceLineWithOutOfScopeItem()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "invoice-sync-usenet",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = new[] { PermissionNames.InvoiceEdit }
        };
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Allowed sync invoice customer",
            NameMatchKey = "ALLOWEDSYNCINVOICECUSTOMER",
            TradeType = "Sales"
        };
        var outOfScopeItem = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            NameOriginal = "Out of scope sync invoice item",
            NameMatchKey = "OUTOFSCOPESYNCINVOICEITEM",
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 5m
        };
        dbContext.Customers.Add(customer);
        dbContext.Items.Add(outOfScopeItem);
        await dbContext.SaveChangesAsync();

        var invoiceId = Guid.NewGuid();
        var result = AssertSyncOk(await CreateSyncController(dbContext, currentUser)
            .Push(new SyncPushRequest
            {
                DeviceId = "invoice-out-of-scope-item-device",
                Invoices =
                [
                    new InvoiceDto
                    {
                        Id = invoiceId,
                        CustomerId = customer.Id,
                        TenantCode = TenantScopeCatalog.UsenetGroup,
                        OfficeCode = OfficeCodeCatalog.Usenet,
                        ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                        VoucherType = VoucherType.Sales,
                        SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                        InvoiceDate = new DateOnly(2026, 6, 17),
                        TotalAmount = 100m,
                        SupplyAmount = 91m,
                        VatAmount = 9m,
                        Lines =
                        [
                            new InvoiceLineDto
                            {
                                Id = Guid.NewGuid(),
                                InvoiceId = invoiceId,
                                ItemId = outOfScopeItem.Id,
                                ItemNameOriginal = outOfScopeItem.NameOriginal,
                                ItemTrackingType = ItemTrackingTypes.Stock,
                                Unit = "EA",
                                Quantity = 1m,
                                UnitPrice = 100m,
                                LineAmount = 100m
                            }
                        ]
                    }
                ]
            }, CancellationToken.None));

        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            string.Equals(conflict.EntityName, nameof(Invoice), StringComparison.Ordinal) &&
            conflict.Reason.Contains("Referenced item is outside the readable office scope", StringComparison.Ordinal));
        Assert.False(await dbContext.Invoices.IgnoreQueryFilters().AnyAsync(row => row.Id == invoiceId));
        Assert.Equal(5m, await dbContext.Items.IgnoreQueryFilters()
            .Where(row => row.Id == outOfScopeItem.Id)
            .Select(row => row.CurrentStock)
            .SingleAsync());
        Assert.False(await dbContext.ItemWarehouseStocks
            .AnyAsync(row => row.ItemId == outOfScopeItem.Id));
    }

    [Fact]
    public async Task SyncPush_RejectsInvoiceLineWithOutOfScopeItem_WhenCustomerRelinkedByName()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "invoice-sync-usenet",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = new[] { PermissionNames.InvoiceEdit }
        };
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Relinked sync invoice customer",
            NameMatchKey = MatchKeyNormalizer.Normalize("Relinked sync invoice customer"),
            TradeType = "Sales"
        };
        var outOfScopeItem = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            NameOriginal = "Out of scope relinked invoice item",
            NameMatchKey = "OUTOFSCOPERELINKEDINVOICEITEM",
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 5m
        };
        dbContext.Customers.Add(customer);
        dbContext.Items.Add(outOfScopeItem);
        await dbContext.SaveChangesAsync();

        var invoiceId = Guid.NewGuid();
        var result = AssertSyncOk(await CreateSyncController(dbContext, currentUser)
            .Push(new SyncPushRequest
            {
                DeviceId = "invoice-relinked-out-of-scope-item-device",
                Invoices =
                [
                    new InvoiceDto
                    {
                        Id = invoiceId,
                        CustomerId = Guid.NewGuid(),
                        CustomerName = customer.NameOriginal,
                        TenantCode = TenantScopeCatalog.UsenetGroup,
                        OfficeCode = OfficeCodeCatalog.Usenet,
                        ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                        VoucherType = VoucherType.Sales,
                        SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                        InvoiceDate = new DateOnly(2026, 6, 17),
                        TotalAmount = 100m,
                        SupplyAmount = 91m,
                        VatAmount = 9m,
                        Lines =
                        [
                            new InvoiceLineDto
                            {
                                Id = Guid.NewGuid(),
                                InvoiceId = invoiceId,
                                ItemId = outOfScopeItem.Id,
                                ItemNameOriginal = outOfScopeItem.NameOriginal,
                                ItemTrackingType = ItemTrackingTypes.Stock,
                                Unit = "EA",
                                Quantity = 1m,
                                UnitPrice = 100m,
                                LineAmount = 100m
                            }
                        ]
                    }
                ]
            }, CancellationToken.None));

        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            string.Equals(conflict.EntityName, nameof(Invoice), StringComparison.Ordinal) &&
            conflict.Reason.Contains("Referenced item is outside the readable office scope", StringComparison.Ordinal));
        Assert.False(await dbContext.Invoices.IgnoreQueryFilters().AnyAsync(row => row.Id == invoiceId));
        Assert.Equal(5m, await dbContext.Items.IgnoreQueryFilters()
            .Where(row => row.Id == outOfScopeItem.Id)
            .Select(row => row.CurrentStock)
            .SingleAsync());
        Assert.False(await dbContext.ItemWarehouseStocks
            .AnyAsync(row => row.ItemId == outOfScopeItem.Id));
    }

    [Fact]
    public async Task SyncPush_RejectsInvoiceWithReadSharedRentalBillingProfileLink()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "invoice-sync-usenet",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = new[] { PermissionNames.InvoiceEdit }
        };
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Allowed sync invoice rental customer",
            NameMatchKey = "ALLOWEDSYNCINVOICERENTALCUSTOMER",
            TradeType = "Sales"
        };
        var readSharedProfile = new RentalBillingProfile
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            ProfileKey = "READ-SHARED-SYNC-INVOICE-PROFILE",
            CustomerName = "Read shared sync invoice profile",
            BillingDay = 25,
            MonthlyAmount = 100m
        };
        dbContext.Customers.Add(customer);
        dbContext.RentalBillingProfiles.Add(readSharedProfile);
        dbContext.DataSharingPolicies.Add(new DataSharingPolicy
        {
            SourceTenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Yeonsu,
            TargetTenantCode = TenantScopeCatalog.UsenetGroup,
            TargetOfficeCode = OfficeCodeCatalog.Usenet,
            ShareRentals = true,
            AllowTargetWrite = false
        });
        await dbContext.SaveChangesAsync();

        var invoiceId = Guid.NewGuid();
        var result = AssertSyncOk(await CreateSyncController(dbContext, currentUser)
            .Push(new SyncPushRequest
            {
                DeviceId = "invoice-read-shared-rental-link-device",
                Invoices =
                [
                    new InvoiceDto
                    {
                        Id = invoiceId,
                        CustomerId = customer.Id,
                        TenantCode = TenantScopeCatalog.UsenetGroup,
                        OfficeCode = OfficeCodeCatalog.Usenet,
                        ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                        VoucherType = VoucherType.Sales,
                        SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                        InvoiceDate = new DateOnly(2026, 6, 17),
                        TotalAmount = 100m,
                        SupplyAmount = 91m,
                        VatAmount = 9m,
                        LinkedRentalBillingProfileId = readSharedProfile.Id,
                        LinkedRentalBillingRunId = Guid.NewGuid()
                    }
                ]
            }, CancellationToken.None));

        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            string.Equals(conflict.EntityName, nameof(Invoice), StringComparison.Ordinal) &&
            conflict.Reason.Contains("Referenced rental billing profile is outside the writable office scope", StringComparison.Ordinal));
        Assert.False(await dbContext.Invoices.IgnoreQueryFilters().AnyAsync(row => row.Id == invoiceId));
    }

    [Fact]
    public async Task SyncPush_RejectsTransactionWithReadSharedRentalBillingProfileLink()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "payment-sync-usenet",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = new[] { PermissionNames.PaymentEdit }
        };
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Allowed sync rental payment customer",
            NameMatchKey = "ALLOWEDSYNCRENTALPAYMENTCUSTOMER",
            TradeType = "Sales"
        };
        var readSharedProfile = new RentalBillingProfile
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            ProfileKey = "READ-SHARED-SYNC-TRANSACTION-PROFILE",
            CustomerName = "Read shared sync transaction profile",
            BillingDay = 25,
            MonthlyAmount = 100m
        };
        dbContext.Customers.Add(customer);
        dbContext.RentalBillingProfiles.Add(readSharedProfile);
        dbContext.DataSharingPolicies.Add(new DataSharingPolicy
        {
            SourceTenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Yeonsu,
            TargetTenantCode = TenantScopeCatalog.UsenetGroup,
            TargetOfficeCode = OfficeCodeCatalog.Usenet,
            ShareRentals = true,
            AllowTargetWrite = false
        });
        await dbContext.SaveChangesAsync();

        var transactionId = Guid.NewGuid();
        var result = AssertSyncOk(await CreateSyncController(dbContext, currentUser)
            .Push(new SyncPushRequest
            {
                DeviceId = "transaction-read-shared-rental-link-device",
                Transactions =
                [
                    new TransactionDto
                    {
                        Id = transactionId,
                        CustomerId = customer.Id,
                        TenantCode = TenantScopeCatalog.UsenetGroup,
                        OfficeCode = OfficeCodeCatalog.Usenet,
                        ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                        TransactionDate = new DateOnly(2026, 6, 17),
                        TransactionKind = "렌탈수금",
                        LinkedRentalBillingProfileId = readSharedProfile.Id,
                        LinkedRentalBillingRunId = Guid.NewGuid(),
                        CashReceipt = 100m,
                        ReceiptTotal = 100m,
                        SettlementAmount = 100m
                    }
                ]
            }, CancellationToken.None));

        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            string.Equals(conflict.EntityName, nameof(TransactionRecord), StringComparison.Ordinal) &&
            conflict.Reason.Contains("Referenced rental billing profile is outside the writable office scope", StringComparison.Ordinal));
        Assert.False(await dbContext.Transactions.IgnoreQueryFilters().AnyAsync(row => row.Id == transactionId));
    }

    [Fact]
    public async Task SyncPush_RejectsTransactionWithReadSharedInvoiceLink()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "payment-sync-usenet",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = new[] { PermissionNames.PaymentEdit }
        };
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Allowed sync invoice payment customer",
            NameMatchKey = "ALLOWEDSYNCINVOICEPAYMENTCUSTOMER",
            TradeType = "Sales"
        };
        var readSharedInvoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 17),
            TotalAmount = 100m,
            SupplyAmount = 91m,
            VatAmount = 9m
        };
        dbContext.Customers.Add(customer);
        dbContext.Invoices.Add(readSharedInvoice);
        dbContext.DataSharingPolicies.Add(new DataSharingPolicy
        {
            SourceTenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Yeonsu,
            TargetTenantCode = TenantScopeCatalog.UsenetGroup,
            TargetOfficeCode = OfficeCodeCatalog.Usenet,
            ShareInvoices = true,
            AllowTargetWrite = false
        });
        await dbContext.SaveChangesAsync();

        var transactionId = Guid.NewGuid();
        var result = AssertSyncOk(await CreateSyncController(dbContext, currentUser)
            .Push(new SyncPushRequest
            {
                DeviceId = "transaction-read-shared-invoice-link-device",
                Transactions =
                [
                    new TransactionDto
                    {
                        Id = transactionId,
                        CustomerId = customer.Id,
                        TenantCode = TenantScopeCatalog.UsenetGroup,
                        OfficeCode = OfficeCodeCatalog.Usenet,
                        ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                        TransactionDate = new DateOnly(2026, 6, 17),
                        TransactionKind = "전표수금",
                        LinkedInvoiceId = readSharedInvoice.Id,
                        LinkedInvoiceNumber = "READ-SHARED-INV-001",
                        CashReceipt = 100m,
                        ReceiptTotal = 100m,
                        SettlementAmount = 100m
                    }
                ]
            }, CancellationToken.None));

        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            string.Equals(conflict.EntityName, nameof(TransactionRecord), StringComparison.Ordinal) &&
            conflict.Reason.Contains("Referenced invoice is outside the writable payment office scope", StringComparison.Ordinal));
        Assert.False(await dbContext.Transactions.IgnoreQueryFilters().AnyAsync(row => row.Id == transactionId));
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
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));
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
    public async Task InvoicesController_Delete_RentalBillingInvoice_RevertsSettlementAndDeletesLinkedPayments()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customerId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        var invoiceNumber = "RENTAL-DEL-001";

        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Server rental delete customer",
            NameMatchKey = "SERVERRENTALDELETECUSTOMER",
            TradeType = "매출"
        });
        dbContext.RentalBillingProfiles.Add(new RentalBillingProfile
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ProfileKey = $"profile-{profileId:N}",
            CustomerId = customerId,
            CustomerName = "Server rental delete customer",
            BillingStatus = "완료",
            SettlementStatus = "입금확인",
            CompletionStatus = "완료",
            MonthlyAmount = 100_000m,
            SettledAmount = 100_000m,
            OutstandingAmount = 0m,
            BillingRunsJson = JsonSerializer.Serialize(new[]
            {
                new ServerRentalBillingRunSnapshot
                {
                    RunId = runId,
                    RunKey = "2026-05",
                    ScheduledDate = new DateOnly(2026, 5, 25),
                    PeriodStartDate = new DateOnly(2026, 5, 1),
                    PeriodEndDate = new DateOnly(2026, 5, 31),
                    PeriodLabel = "2026-05",
                    Status = "완료",
                    BilledAmount = 100_000m,
                    SettledAmount = 100_000m,
                    SettlementStatus = "입금확인",
                    SettledDate = new DateOnly(2026, 5, 26)
                }
            })
        });
        dbContext.Invoices.Add(new Invoice
        {
            Id = invoiceId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = invoiceNumber,
            VersionGroupId = invoiceId,
            VersionNumber = 1,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 5, 25),
            TotalAmount = 100_000m,
            SupplyAmount = 90_909m,
            VatAmount = 9_091m,
            LinkedRentalBillingProfileId = profileId,
            LinkedRentalBillingRunId = runId
        });
        dbContext.InvoiceLines.Add(new InvoiceLine
        {
            Id = Guid.NewGuid(),
            InvoiceId = invoiceId,
            ItemNameOriginal = "Rental billing item",
            Unit = "EA",
            Quantity = 1m,
            UnitPrice = 100_000m,
            LineAmount = 100_000m
        });
        dbContext.Transactions.Add(new TransactionRecord
        {
            Id = transactionId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 5, 26),
            TransactionKind = "렌탈수금",
            LinkedInvoiceId = invoiceId,
            LinkedInvoiceNumber = invoiceNumber,
            LinkedRentalBillingProfileId = profileId,
            LinkedRentalBillingRunId = runId,
            BankReceipt = 100_000m,
            ReceiptTotal = 100_000m,
            SettlementAmount = 100_000m
        });
        dbContext.Payments.Add(new Payment
        {
            Id = transactionId,
            InvoiceId = invoiceId,
            PaymentDate = new DateOnly(2026, 5, 26),
            Amount = 100_000m,
            Note = "linked rental payment"
        });
        await dbContext.SaveChangesAsync();

        var storedInvoice = await dbContext.Invoices.IgnoreQueryFilters().SingleAsync(invoice => invoice.Id == invoiceId);
        var controller = new InvoicesController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));

        var deleteResponse = await controller.Delete(invoiceId, storedInvoice.Revision, CancellationToken.None);

        Assert.IsType<NoContentResult>(deleteResponse);
        var deletedInvoice = await dbContext.Invoices.IgnoreQueryFilters().AsNoTracking().SingleAsync(invoice => invoice.Id == invoiceId);
        Assert.True(deletedInvoice.IsDeleted);
        var deletedPayment = await dbContext.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(payment => payment.Id == transactionId);
        Assert.True(deletedPayment.IsDeleted);
        var detachedTransaction = await dbContext.Transactions.IgnoreQueryFilters().AsNoTracking().SingleAsync(transaction => transaction.Id == transactionId);
        Assert.Null(detachedTransaction.LinkedInvoiceId);
        Assert.Equal(0m, detachedTransaction.SettlementAmount);
        Assert.False(detachedTransaction.IsDeleted);
        var revertedProfile = await dbContext.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(profile => profile.Id == profileId);
        Assert.Equal(0m, revertedProfile.SettledAmount);
        Assert.Equal(100_000m, revertedProfile.OutstandingAmount);
        Assert.Equal("미완료", revertedProfile.CompletionStatus);
        var revertedRun = Assert.Single(JsonSerializer.Deserialize<List<ServerRentalBillingRunSnapshot>>(revertedProfile.BillingRunsJson) ?? []);
        Assert.Equal(0m, revertedRun.SettledAmount);
        Assert.NotEqual("완료", revertedRun.Status);
        Assert.NotEqual("입금확인", revertedRun.SettlementStatus);
        Assert.Null(revertedRun.SettledDate);
    }

    [Fact]
    public async Task InvoicesController_SalesCreate_AllowsNegativeWarehouseStockWhenInventoryIsShort()
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
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));
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

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var savedInvoice = Assert.IsType<InvoiceDto>(ok.Value);
        Assert.Equal(invoiceId, savedInvoice.Id);
        Assert.True(await dbContext.Invoices.IgnoreQueryFilters().AnyAsync(invoice => invoice.Id == invoiceId));
        Assert.Equal(-1m, await dbContext.ItemWarehouseStocks
            .Where(stock => stock.ItemId == item.Id && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
        Assert.Equal(-1m, (await dbContext.Items.IgnoreQueryFilters().SingleAsync(row => row.Id == item.Id)).CurrentStock);
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
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));
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
    public async Task PaymentsController_Update_ForbidsRelinkingPaymentToOutOfScopeInvoice()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "payment-editor-usenet",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = new[] { PermissionNames.PaymentEdit }
        };
        await using var dbContext = CreateDbContext(currentUser);

        var allowedCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Allowed payment customer",
            NameMatchKey = "ALLOWEDPAYMENTCUSTOMER",
            TradeType = "Sales"
        };
        var outOfScopeCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            NameOriginal = "Out of scope payment customer",
            NameMatchKey = "OUTOFSCOPEPAYMENTCUSTOMER",
            TradeType = "Sales"
        };
        var allowedInvoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = allowedCustomer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "INV-PAY-SCOPE-ALLOWED",
            InvoiceDate = new DateOnly(2026, 6, 17),
            TotalAmount = 1000m
        };
        var outOfScopeInvoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = outOfScopeCustomer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            InvoiceNumber = "INV-PAY-SCOPE-BLOCKED",
            InvoiceDate = new DateOnly(2026, 6, 17),
            TotalAmount = 1000m
        };
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            InvoiceId = allowedInvoice.Id,
            PaymentDate = new DateOnly(2026, 6, 17),
            Amount = 100m,
            Note = "original scope"
        };
        dbContext.Customers.AddRange(allowedCustomer, outOfScopeCustomer);
        dbContext.Invoices.AddRange(allowedInvoice, outOfScopeInvoice);
        dbContext.Payments.Add(payment);
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Payments
            .IgnoreQueryFilters()
            .Include(row => row.Invoice)
            .ThenInclude(invoice => invoice!.Customer)
            .Include(row => row.Attachments)
            .SingleAsync(row => row.Id == payment.Id);
        var dto = stored.ToDto();
        dto.ExpectedRevision = stored.Revision;
        dto.InvoiceId = outOfScopeInvoice.Id;
        dto.Note = "attempted out-of-scope relink";

        var controller = new PaymentsController(dbContext, new OfficeScopeService(currentUser, dbContext), new StubCentralFileStorage());

        var response = await controller.Update(payment.Id, dto, CancellationToken.None);

        Assert.IsType<ForbidResult>(response.Result);
        var persisted = await dbContext.Payments
            .IgnoreQueryFilters()
            .SingleAsync(row => row.Id == payment.Id);
        Assert.Equal(allowedInvoice.Id, persisted.InvoiceId);
        Assert.Equal("original scope", persisted.Note);
    }

    [Fact]
    public async Task PaymentsController_UploadAttachment_IsIdempotentForClientAttachmentId()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Mobile attachment retry customer",
            NameMatchKey = "MOBILEATTACHMENTRETRYCUSTOMER",
            TradeType = "Sales"
        };
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "MOBILE-ATTACHMENT-IDEMPOTENT",
            InvoiceDate = new DateOnly(2026, 6, 18)
        };
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            InvoiceId = invoice.Id,
            PaymentDate = new DateOnly(2026, 6, 18),
            Amount = 5000m,
            Note = "mobile upload retry"
        };
        dbContext.Customers.Add(customer);
        dbContext.Invoices.Add(invoice);
        dbContext.Payments.Add(payment);
        await dbContext.SaveChangesAsync();

        var controller = new PaymentsController(dbContext, new OfficeScopeService(currentUser, dbContext), new StubCentralFileStorage());
        var clientAttachmentId = Guid.NewGuid();

        var first = AssertOk<PaymentAttachmentDto>(await controller.UploadAttachment(
            payment.Id,
            CreateFormFile("retry-receipt.pdf", "application/pdf", "first upload"),
            "내역첨부",
            "mobile retry",
            clientAttachmentId,
            CancellationToken.None));
        var second = AssertOk<PaymentAttachmentDto>(await controller.UploadAttachment(
            payment.Id,
            CreateFormFile("retry-receipt.pdf", "application/pdf", "second upload should not create duplicate"),
            "내역첨부",
            "mobile retry",
            clientAttachmentId,
            CancellationToken.None));

        Assert.Equal(clientAttachmentId, first.Id);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.FileHash, second.FileHash);
        Assert.Equal(1, await dbContext.PaymentAttachments.IgnoreQueryFilters().CountAsync(current => current.PaymentId == payment.Id));
    }

    [Fact]
    public async Task PaymentsController_GetAttachmentContent_ReturnsNotFound_WhenStoredContentIsMissing()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Missing payment attachment customer",
            NameMatchKey = "MISSINGPAYMENTATTACHMENTCUSTOMER",
            TradeType = "Sales"
        };
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "MISSING-PAYMENT-ATTACHMENT",
            InvoiceDate = new DateOnly(2026, 6, 18)
        };
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            InvoiceId = invoice.Id,
            PaymentDate = new DateOnly(2026, 6, 18),
            Amount = 5000m,
            Note = "missing attachment content"
        };
        var attachment = new PaymentAttachment
        {
            Id = Guid.NewGuid(),
            PaymentId = payment.Id,
            AttachmentType = "내역첨부",
            FileName = "missing-receipt.pdf",
            MimeType = "application/pdf",
            FileSize = 12,
            FileHash = "missing",
            StoragePath = Path.Combine(Path.GetTempPath(), "georaeplan-missing", Guid.NewGuid().ToString("N"), "missing-receipt.pdf"),
            FileContent = []
        };
        dbContext.Customers.Add(customer);
        dbContext.Invoices.Add(invoice);
        dbContext.Payments.Add(payment);
        dbContext.PaymentAttachments.Add(attachment);
        await dbContext.SaveChangesAsync();

        var controller = new PaymentsController(dbContext, new OfficeScopeService(currentUser, dbContext), new StubCentralFileStorage());

        var result = await controller.GetAttachmentContent(attachment.Id, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task CustomersController_DownloadContractContent_ReturnsNotFound_WhenStoredContentIsMissing()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Missing contract customer",
            NameMatchKey = "MISSINGCONTRACTCUSTOMER",
            TradeType = "Sales"
        };
        var contract = new CustomerContract
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            ContractType = "거래계약서",
            FileName = "missing-contract.pdf",
            MimeType = "application/pdf",
            FileSize = 20,
            FileHash = "missing",
            StoragePath = Path.Combine(Path.GetTempPath(), "georaeplan-missing", Guid.NewGuid().ToString("N"), "missing-contract.pdf"),
            FileContent = []
        };
        dbContext.Customers.Add(customer);
        dbContext.CustomerContracts.Add(contract);
        await dbContext.SaveChangesAsync();

        var controller = new CustomersController(dbContext, new OfficeScopeService(currentUser, dbContext), new StubCentralFileStorage());

        var result = await controller.DownloadContractContent(contract.Id, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
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
    public async Task SyncPush_ReturnsAcceptedRevision_ForConsecutiveCompanyProfileSaves()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var profile = new CompanyProfile
        {
            Id = Guid.NewGuid(),
            OfficeCode = OfficeCodeCatalog.Usenet,
            ProfileName = "USENET 기본",
            TradeName = "유즈넷",
            IsDefaultForOffice = true,
            IsActive = true
        };
        dbContext.CompanyProfiles.Add(profile);
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.CompanyProfiles.IgnoreQueryFilters().SingleAsync(row => row.Id == profile.Id);
        var baselineRevision = stored.Revision;
        var firstDto = stored.ToDto();
        firstDto.ExpectedRevision = stored.Revision;
        firstDto.TradeName = "유즈넷 1차 저장";

        var controller = CreateSyncController(dbContext, currentUser);
        var firstResult = AssertSyncOk(await controller.Push(new SyncPushRequest
        {
            DeviceId = "test-device",
            CompanyProfiles = [firstDto]
        }, CancellationToken.None));

        var accepted = Assert.Single(firstResult.AcceptedRevisions, revision => revision.EntityId == profile.Id);
        Assert.Equal(nameof(CompanyProfile), accepted.EntityName);
        Assert.True(accepted.Revision > baselineRevision);

        var secondDto = firstDto;
        secondDto.Revision = accepted.Revision;
        secondDto.ExpectedRevision = accepted.Revision;
        secondDto.UpdatedAtUtc = accepted.UpdatedAtUtc;
        secondDto.TradeName = "유즈넷 2차 저장";

        var secondResult = AssertSyncOk(await controller.Push(new SyncPushRequest
        {
            DeviceId = "test-device",
            CompanyProfiles = [secondDto]
        }, CancellationToken.None));

        Assert.Equal(0, secondResult.ConflictCount);
        Assert.Contains(secondResult.AcceptedRevisions, revision => revision.EntityId == profile.Id && revision.Revision > accepted.Revision);
        Assert.Equal("유즈넷 2차 저장", await dbContext.CompanyProfiles.IgnoreQueryFilters()
            .Where(row => row.Id == profile.Id)
            .Select(row => row.TradeName)
            .SingleAsync());
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

    [Fact]
    public async Task CustomerCategoriesController_Create_ReturnsConflict_WhenActiveNameAlreadyExistsAfterTrim()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var existingId = Guid.NewGuid();
        dbContext.CustomerCategories.Add(new CustomerCategory
        {
            Id = existingId,
            Name = "관공서"
        });
        await dbContext.SaveChangesAsync();

        var incomingId = Guid.NewGuid();
        var controller = new CustomerCategoriesController(dbContext);
        var response = await controller.Create(new CustomerCategoryDto
        {
            Id = incomingId,
            Name = " 관공서 "
        }, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(response.Result);
        Assert.False(await dbContext.CustomerCategories.IgnoreQueryFilters().AnyAsync(category => category.Id == incomingId));
        Assert.Equal(1, await dbContext.CustomerCategories.IgnoreQueryFilters().CountAsync(category => !category.IsDeleted));
    }

    [Fact]
    public async Task CustomerCategoriesController_Update_ReturnsConflict_WhenActiveNameAlreadyExistsAfterTrim()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var existingId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        dbContext.CustomerCategories.AddRange(
            new CustomerCategory
            {
                Id = existingId,
                Name = "관공서"
            },
            new CustomerCategory
            {
                Id = targetId,
                Name = "학교"
            });
        await dbContext.SaveChangesAsync();

        var storedTarget = await dbContext.CustomerCategories.FirstAsync(category => category.Id == targetId);
        var controller = new CustomerCategoriesController(dbContext);
        var response = await controller.Update(targetId, new CustomerCategoryDto
        {
            Id = targetId,
            Name = " 관공서 ",
            ExpectedRevision = storedTarget.Revision
        }, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(response.Result);
        Assert.Equal("학교", await dbContext.CustomerCategories
            .IgnoreQueryFilters()
            .Where(category => category.Id == targetId)
            .Select(category => category.Name)
            .SingleAsync());
        Assert.Equal(2, await dbContext.CustomerCategories.IgnoreQueryFilters().CountAsync(category => !category.IsDeleted));
    }

    private static TDto AssertOk<TDto>(ActionResult<TDto> response)
    {
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        return Assert.IsType<TDto>(ok.Value);
    }

    private static SyncPushResult AssertSyncOk(ActionResult<SyncPushResult> response)
    {
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        return Assert.IsType<SyncPushResult>(ok.Value);
    }

    private static SyncController CreateSyncController(AppDbContext dbContext, TestCurrentUserContext currentUser)
        => new(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new StubCentralFileStorage(),
            new RevisionClock(),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalAssignmentHistoryService(dbContext));

    private static IFormFile CreateFormFile(string fileName, string contentType, string content)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var stream = new MemoryStream(bytes);
        return new FormFile(stream, 0, bytes.Length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType
        };
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

    private sealed class ServerRentalBillingRunSnapshot
    {
        public Guid RunId { get; set; }
        public string RunKey { get; set; } = string.Empty;
        public DateOnly ScheduledDate { get; set; }
        public DateOnly PeriodStartDate { get; set; }
        public DateOnly PeriodEndDate { get; set; }
        public string PeriodLabel { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public decimal BilledAmount { get; set; }
        public decimal SettledAmount { get; set; }
        public string SettlementStatus { get; set; } = string.Empty;
        public DateOnly? SettledDate { get; set; }
    }

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
