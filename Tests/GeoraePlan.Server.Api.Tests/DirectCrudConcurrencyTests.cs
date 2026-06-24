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
    public async Task CustomersController_Delete_ReturnsConflict_WhenActiveBusinessReferencesRemain()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "REFERENCE-BLOCK-CUSTOMER",
            NameMatchKey = "REFERENCEBLOCKCUSTOMER",
            TradeType = "매출"
        };
        var profileId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        dbContext.Customers.Add(customer);
        dbContext.Invoices.Add(new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = customer.TenantCode,
            OfficeCode = customer.OfficeCode,
            ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
            InvoiceNumber = "CUSTOMER-DELETE-BLOCK-INVOICE",
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 19)
        });
        dbContext.Transactions.Add(new TransactionRecord
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = customer.TenantCode,
            OfficeCode = customer.OfficeCode,
            ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
            TransactionDate = new DateOnly(2026, 6, 19),
            TransactionKind = "수금"
        });
        dbContext.RentalBillingProfiles.Add(new RentalBillingProfile
        {
            Id = profileId,
            TenantCode = customer.TenantCode,
            OfficeCode = customer.OfficeCode,
            ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ProfileKey = $"profile-{profileId:N}",
            CustomerId = customer.Id,
            CustomerName = customer.NameOriginal
        });
        dbContext.RentalAssets.Add(new RentalAsset
        {
            Id = assetId,
            TenantCode = customer.TenantCode,
            OfficeCode = customer.OfficeCode,
            ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            AssetKey = $"asset-{assetId:N}",
            CustomerId = customer.Id,
            CustomerName = customer.NameOriginal,
            CurrentCustomerName = customer.NameOriginal,
            ManagementNumber = "A-001"
        });
        dbContext.RentalAssetAssignmentHistories.Add(new RentalAssetAssignmentHistory
        {
            Id = Guid.NewGuid(),
            AssetId = assetId,
            CustomerId = customer.Id,
            TenantCode = customer.TenantCode,
            OfficeCode = customer.OfficeCode,
            ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
            CustomerName = customer.NameOriginal,
            ManagementNumber = "A-001",
            IsCurrent = true
        });
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Customers.IgnoreQueryFilters().FirstAsync(x => x.Id == customer.Id);
        var controller = new CustomersController(dbContext, new OfficeScopeService(currentUser, dbContext), new StubCentralFileStorage());

        var response = await controller.Delete(stored.Id, stored.Revision, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(response);
        var payload = conflict.Value;
        Assert.NotNull(payload);
        var payloadType = payload!.GetType();
        Assert.Equal(CustomerDeletionReferenceGuard.ConflictCode, payloadType.GetProperty("error")?.GetValue(payload));
        var message = Assert.IsType<string>(payloadType.GetProperty("message")?.GetValue(payload));
        Assert.Contains("전표 1건", message, StringComparison.Ordinal);
        Assert.Contains("거래내역 1건", message, StringComparison.Ordinal);
        Assert.Contains("렌탈 청구 1건", message, StringComparison.Ordinal);
        Assert.Contains("렌탈 자산 1건", message, StringComparison.Ordinal);
        Assert.Contains("현재 설치이력 1건", message, StringComparison.Ordinal);
        Assert.False(await dbContext.Customers.IgnoreQueryFilters()
            .Where(current => current.Id == customer.Id)
            .Select(current => current.IsDeleted)
            .SingleAsync());
    }

    [Fact]
    public async Task CustomersController_Delete_CascadesContractsWithoutClearingPrimaryFlag()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "DELETE-CONTRACT-PRIMARY-CUSTOMER",
            NameMatchKey = "DELETECONTRACTPRIMARYCUSTOMER",
            TradeType = "매출"
        };
        var contract = new CustomerContract
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            ContractType = "대표 계약서",
            FileName = "primary-contract.pdf",
            MimeType = "application/pdf",
            FileHash = "PRIMARY-CONTRACT",
            FileSize = 1,
            IsPrimary = true,
            IsDeleted = false
        };
        dbContext.Customers.Add(customer);
        dbContext.CustomerContracts.Add(contract);
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Customers.IgnoreQueryFilters().FirstAsync(current => current.Id == customer.Id);
        var controller = new CustomersController(dbContext, new OfficeScopeService(currentUser, dbContext), new StubCentralFileStorage());

        var response = await controller.Delete(stored.Id, stored.Revision, CancellationToken.None);

        Assert.IsType<NoContentResult>(response);
        var deletedContract = await dbContext.CustomerContracts
            .IgnoreQueryFilters()
            .SingleAsync(current => current.Id == contract.Id);
        Assert.True(deletedContract.IsDeleted);
        Assert.True(deletedContract.IsPrimary);
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
    public async Task ItemsController_Delete_RejectsActiveInvoiceLineReference()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Active invoice item",
            NameMatchKey = "ACTIVEINVOICEITEM",
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 3m
        };
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Item delete invoice customer",
            NameMatchKey = "ITEMDELETEINVOICECUSTOMER",
            TradeType = CustomerClassificationNormalizer.Sales
        };
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "INV-ITEM-DELETE-BLOCK",
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 19),
            SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            TotalAmount = 100m,
            SupplyAmount = 91m,
            VatAmount = 9m,
            Lines =
            [
                new InvoiceLine
                {
                    Id = Guid.NewGuid(),
                    ItemId = item.Id,
                    ItemNameOriginal = item.NameOriginal,
                    ItemTrackingType = ItemTrackingTypes.Stock,
                    Unit = "EA",
                    Quantity = 1m,
                    UnitPrice = 100m,
                    LineAmount = 100m,
                    OrderIndex = 1
                }
            ]
        };
        dbContext.Items.Add(item);
        dbContext.Customers.Add(customer);
        dbContext.Invoices.Add(invoice);
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Items.IgnoreQueryFilters().SingleAsync(row => row.Id == item.Id);
        var controller = new ItemsController(dbContext, new OfficeScopeService(currentUser, dbContext));

        var response = await controller.Delete(stored.Id, stored.Revision, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(response);
        var payload = conflict.Value;
        Assert.NotNull(payload);
        var payloadType = payload!.GetType();
        var message = Assert.IsType<string>(payloadType.GetProperty("message")?.GetValue(payload));
        Assert.Contains("전표 라인", message);
        Assert.False(await dbContext.Items.IgnoreQueryFilters()
            .Where(row => row.Id == item.Id)
            .Select(row => row.IsDeleted)
            .SingleAsync());
    }

    [Fact]
    public async Task ItemsController_Delete_AllowsRentalBillingTemplateRowIdMatch()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Rental template referenced item",
            NameMatchKey = "RENTALTEMPLATEREFERENCEDITEM",
            TrackingType = ItemTrackingTypes.Stock
        };
        var profileId = Guid.NewGuid();
        dbContext.Items.Add(item);
        dbContext.RentalBillingProfiles.Add(new RentalBillingProfile
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ProfileKey = $"item-template-guard-{profileId:N}",
            CustomerName = "Item template guard customer",
            BillingTemplateJson = JsonSerializer.Serialize(new[]
            {
                new
                {
                    RowId = item.Id,
                    DisplayItemName = item.NameOriginal,
                    Quantity = 1m,
                    UnitPrice = 100_000m,
                    Amount = 100_000m
                }
            })
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
    }

    [Fact]
    public async Task ItemsController_Delete_RejectsActiveRentalBillingTemplateReference()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Rental template blocked item",
            NameMatchKey = "RENTALTEMPLATEBLOCKEDITEM",
            TrackingType = ItemTrackingTypes.Stock
        };
        var profileId = Guid.NewGuid();
        dbContext.Items.Add(item);
        dbContext.RentalBillingProfiles.Add(new RentalBillingProfile
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ProfileKey = $"item-template-block-{profileId:N}",
            CustomerName = "Item template block customer",
            BillingTemplateJson = JsonSerializer.Serialize(new[]
            {
                new
                {
                    ItemId = item.Id,
                    DisplayItemName = item.NameOriginal,
                    Quantity = 1m,
                    UnitPrice = 100_000m,
                    Amount = 100_000m
                }
            })
        });
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Items.IgnoreQueryFilters().SingleAsync(row => row.Id == item.Id);
        var controller = new ItemsController(dbContext, new OfficeScopeService(currentUser, dbContext));

        var response = await controller.Delete(stored.Id, stored.Revision, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(response);
        var payload = conflict.Value;
        Assert.NotNull(payload);
        var payloadType = payload!.GetType();
        var message = Assert.IsType<string>(payloadType.GetProperty("message")?.GetValue(payload));
        Assert.Contains("렌탈 청구프로필", message);
        Assert.False(await dbContext.Items.IgnoreQueryFilters()
            .Where(row => row.Id == item.Id)
            .Select(row => row.IsDeleted)
            .SingleAsync());
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
    public async Task InvoicesController_Update_RejectsProtectedInvoiceSameIdLineMutation_WhenPaymentExists()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "DIRECT-PAID-INVOICE-CUSTOMER",
            NameMatchKey = "DIRECTPAIDINVOICECUSTOMER",
            TradeType = "Sales"
        };
        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "DIRECT-PAID-INVOICE-ITEM",
            NameMatchKey = "DIRECTPAIDINVOICEITEM",
            TrackingType = ItemTrackingTypes.NonStock
        };
        var invoiceId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        dbContext.Customers.Add(customer);
        dbContext.Items.Add(item);
        dbContext.Invoices.Add(new Invoice
        {
            Id = invoiceId,
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "DIRECT-PAID-001",
            VersionGroupId = invoiceId,
            VersionNumber = 1,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 24),
            TotalAmount = 100m,
            SupplyAmount = 91m,
            VatAmount = 9m
        });
        dbContext.InvoiceLines.Add(new InvoiceLine
        {
            Id = lineId,
            InvoiceId = invoiceId,
            ItemId = item.Id,
            ItemNameOriginal = item.NameOriginal,
            ItemTrackingType = ItemTrackingTypes.NonStock,
            Unit = "EA",
            Quantity = 1m,
            UnitPrice = 100m,
            LineAmount = 100m
        });
        dbContext.Payments.Add(new Payment
        {
            Id = Guid.NewGuid(),
            InvoiceId = invoiceId,
            PaymentDate = new DateOnly(2026, 6, 25),
            Amount = 100m,
            Note = "paid before direct API edit"
        });
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(row => row.Customer)
            .Include(row => row.Lines)
            .SingleAsync(row => row.Id == invoiceId);
        var dto = stored.ToDto();
        dto.ExpectedRevision = stored.Revision;
        dto.Lines.Single().UnitPrice = 200m;
        dto.Lines.Single().LineAmount = 200m;
        dto.TotalAmount = 200m;
        dto.SupplyAmount = 182m;
        dto.VatAmount = 18m;

        var controller = new InvoicesController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));

        var response = await controller.Update(invoiceId, dto, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(response.Result);
        var payload = Assert.IsType<ExpectedRevisionConflictResponse>(conflict.Value);
        Assert.Equal(nameof(Invoice), payload.EntityName);
        Assert.Equal(ApiConflictReasonTranslator.ProtectedInvoiceSameIdStructuralMutation, payload.Reason);
        Assert.Equal(100m, await dbContext.Invoices.IgnoreQueryFilters()
            .Where(row => row.Id == invoiceId)
            .Select(row => row.TotalAmount)
            .SingleAsync());
        Assert.Equal(100m, await dbContext.InvoiceLines.IgnoreQueryFilters()
            .Where(row => row.Id == lineId)
            .Select(row => row.LineAmount)
            .SingleAsync());
        Assert.Equal(100m, await dbContext.Payments.IgnoreQueryFilters()
            .Where(row => row.InvoiceId == invoiceId && !row.IsDeleted)
            .Select(row => row.Amount)
            .SingleAsync());
    }

    [Fact]
    public async Task InvoicesController_Update_RejectsProtectedInvoiceSameIdLineMutation_WhenLinkedTransactionExists()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "DIRECT-TRANSACTION-INVOICE-CUSTOMER",
            NameMatchKey = "DIRECTTRANSACTIONINVOICECUSTOMER",
            TradeType = "Sales"
        };
        var invoiceId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        dbContext.Customers.Add(customer);
        dbContext.Invoices.Add(new Invoice
        {
            Id = invoiceId,
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "DIRECT-TRX-001",
            VersionGroupId = invoiceId,
            VersionNumber = 1,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 24),
            TotalAmount = 100m,
            SupplyAmount = 91m,
            VatAmount = 9m
        });
        dbContext.InvoiceLines.Add(new InvoiceLine
        {
            Id = lineId,
            InvoiceId = invoiceId,
            ItemNameOriginal = "transaction line",
            ItemTrackingType = ItemTrackingTypes.NonStock,
            Unit = "EA",
            Quantity = 1m,
            UnitPrice = 100m,
            LineAmount = 100m
        });
        dbContext.Transactions.Add(new TransactionRecord
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 6, 25),
            TransactionKind = "direct invoice receipt",
            LinkedInvoiceId = invoiceId,
            LinkedInvoiceNumber = "DIRECT-TRX-001",
            BankReceipt = 100m,
            ReceiptTotal = 100m,
            SettlementAmount = 100m
        });
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(row => row.Customer)
            .Include(row => row.Lines)
            .SingleAsync(row => row.Id == invoiceId);
        var dto = stored.ToDto();
        dto.ExpectedRevision = stored.Revision;
        dto.Lines.Single().UnitPrice = 200m;
        dto.Lines.Single().LineAmount = 200m;
        dto.TotalAmount = 200m;
        dto.SupplyAmount = 182m;
        dto.VatAmount = 18m;

        var controller = new InvoicesController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));

        var response = await controller.Update(invoiceId, dto, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(response.Result);
        var payload = Assert.IsType<ExpectedRevisionConflictResponse>(conflict.Value);
        Assert.Equal(nameof(Invoice), payload.EntityName);
        Assert.Equal(ApiConflictReasonTranslator.ProtectedInvoiceSameIdStructuralMutation, payload.Reason);
        Assert.Equal(100m, await dbContext.Invoices.IgnoreQueryFilters()
            .Where(row => row.Id == invoiceId)
            .Select(row => row.TotalAmount)
            .SingleAsync());
        Assert.Equal(100m, await dbContext.InvoiceLines.IgnoreQueryFilters()
            .Where(row => row.Id == lineId)
            .Select(row => row.LineAmount)
            .SingleAsync());
        Assert.Equal(100m, await dbContext.Transactions.IgnoreQueryFilters()
            .Where(row => row.LinkedInvoiceId == invoiceId && !row.IsDeleted)
            .Select(row => row.SettlementAmount)
            .SingleAsync());
    }

    [Fact]
    public async Task InvoicesController_Update_AllowsProtectedInvoiceMetadataOnlyChange_WhenPaymentExists()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "DIRECT-PAID-METADATA-CUSTOMER",
            NameMatchKey = "DIRECTPAIDMETADATACUSTOMER",
            TradeType = "Sales"
        };
        var invoiceId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        dbContext.Customers.Add(customer);
        dbContext.Invoices.Add(new Invoice
        {
            Id = invoiceId,
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "DIRECT-PAID-META-001",
            VersionGroupId = invoiceId,
            VersionNumber = 1,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 24),
            TotalAmount = 100m,
            SupplyAmount = 91m,
            VatAmount = 9m,
            Memo = "before"
        });
        dbContext.InvoiceLines.Add(new InvoiceLine
        {
            Id = lineId,
            InvoiceId = invoiceId,
            ItemNameOriginal = "metadata line",
            ItemTrackingType = ItemTrackingTypes.NonStock,
            Unit = "EA",
            Quantity = 1m,
            UnitPrice = 100m,
            LineAmount = 100m
        });
        dbContext.Payments.Add(new Payment
        {
            Id = Guid.NewGuid(),
            InvoiceId = invoiceId,
            PaymentDate = new DateOnly(2026, 6, 25),
            Amount = 100m,
            Note = "paid before metadata edit"
        });
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(row => row.Customer)
            .Include(row => row.Lines)
            .SingleAsync(row => row.Id == invoiceId);
        var dto = stored.ToDto();
        dto.ExpectedRevision = stored.Revision;
        dto.Memo = "memo-only direct API edit";

        var controller = new InvoicesController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));

        var saved = AssertOk(await controller.Update(invoiceId, dto, CancellationToken.None));

        Assert.Equal("memo-only direct API edit", saved.Memo);
        Assert.Equal(100m, await dbContext.Invoices.IgnoreQueryFilters()
            .Where(row => row.Id == invoiceId)
            .Select(row => row.TotalAmount)
            .SingleAsync());
        Assert.Equal(100m, await dbContext.InvoiceLines.IgnoreQueryFilters()
            .Where(row => row.Id == lineId)
            .Select(row => row.LineAmount)
            .SingleAsync());
        Assert.Equal(100m, await dbContext.Payments.IgnoreQueryFilters()
            .Where(row => row.InvoiceId == invoiceId && !row.IsDeleted)
            .Select(row => row.Amount)
            .SingleAsync());
    }

    [Fact]
    public async Task InvoicesController_Update_ForbidsTenantOfficeMismatchedExistingInvoice_ForOfficeScopedUser()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "usenet-invoice-editor",
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
            NameOriginal = "TENANT-MISMATCH-INVOICE-CUSTOMER",
            NameMatchKey = "TENANTMISMATCHINVOICECUSTOMER",
            TradeType = "Sales"
        };
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "TENANT-MISMATCH-INV",
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 23),
            Memo = "original mismatch memo"
        };
        dbContext.Customers.Add(customer);
        dbContext.Invoices.Add(invoice);
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Invoices
            .IgnoreQueryFilters()
            .Include(x => x.Customer)
            .Include(x => x.Lines)
            .FirstAsync(x => x.Id == invoice.Id);
        var dto = stored.ToDto();
        dto.Memo = "should not be saved";
        dto.TenantCode = TenantScopeCatalog.UsenetGroup;
        dto.ExpectedRevision = stored.Revision;

        var controller = new InvoicesController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));

        var response = await controller.Update(stored.Id, dto, CancellationToken.None);

        Assert.IsType<ForbidResult>(response.Result);
        dbContext.ChangeTracker.Clear();
        var unchanged = await dbContext.Invoices.IgnoreQueryFilters().SingleAsync(x => x.Id == invoice.Id);
        Assert.Equal(TenantScopeCatalog.Itworld, unchanged.TenantCode);
        Assert.Equal(OfficeCodeCatalog.Usenet, unchanged.OfficeCode);
        Assert.Equal("original mismatch memo", unchanged.Memo);
    }

    [Fact]
    public async Task InvoicesController_Create_RecordsMutationReceipt_ForMobileRetry()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customerId = Guid.NewGuid();
        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "MOBILE-MUTATION-CUSTOMER",
            NameMatchKey = "MOBILEMUTATIONCUSTOMER",
            TradeType = "Sales"
        });
        await dbContext.SaveChangesAsync();

        var invoiceId = Guid.NewGuid();
        var mutationId = $"mobile:invoice:{invoiceId:N}:{Guid.NewGuid():N}";
        var mutationCreatedAtUtc = new DateTime(2026, 6, 22, 1, 2, 3, DateTimeKind.Utc);
        var controller = new InvoicesController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));

        var response = await controller.Create(new InvoiceDto
        {
            Id = invoiceId,
            CustomerId = customerId,
            CustomerName = "MOBILE-MUTATION-CUSTOMER",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 22),
            MutationId = mutationId,
            MutationCreatedAtUtc = mutationCreatedAtUtc
        }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(response.Result);
        var receipt = await dbContext.ProcessedSyncMutations.SingleAsync(current => current.MutationId == mutationId);
        Assert.Equal(nameof(Invoice), receipt.EntityName);
        Assert.Equal(invoiceId.ToString("D"), receipt.EntityId);
        Assert.Equal(ProcessedSyncMutationRecorder.DirectApiDeviceId, receipt.DeviceId);
        Assert.Equal(mutationCreatedAtUtc, receipt.ProcessedAtUtc);
    }

    [Fact]
    public async Task PaymentsController_Create_RecordsMutationReceipt_ForMobileRetry()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customerId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "MOBILE-PAYMENT-MUTATION-CUSTOMER",
            NameMatchKey = "MOBILEPAYMENTMUTATIONCUSTOMER",
            TradeType = "Sales"
        });
        dbContext.Invoices.Add(new Invoice
        {
            Id = invoiceId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "MOBILE-PAYMENT-MUTATION-INVOICE",
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 22),
            TotalAmount = 100_000m
        });
        await dbContext.SaveChangesAsync();

        var storedInvoice = await dbContext.Invoices.IgnoreQueryFilters().SingleAsync(current => current.Id == invoiceId);
        var paymentId = Guid.NewGuid();
        var mutationId = $"mobile:payment:{paymentId:N}:{Guid.NewGuid():N}";
        var mutationCreatedAtUtc = new DateTime(2026, 6, 22, 2, 3, 4, DateTimeKind.Utc);
        var controller = CreatePaymentsController(dbContext, currentUser);

        var response = await controller.Create(new PaymentDto
        {
            Id = paymentId,
            InvoiceId = invoiceId,
            PaymentDate = new DateOnly(2026, 6, 22),
            Amount = 10_000m,
            Note = "mobile retry receipt",
            ExpectedRevision = storedInvoice.Revision,
            MutationId = mutationId,
            MutationCreatedAtUtc = mutationCreatedAtUtc
        }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(response.Result);
        var receipt = await dbContext.ProcessedSyncMutations.SingleAsync(current => current.MutationId == mutationId);
        Assert.Equal(nameof(Payment), receipt.EntityName);
        Assert.Equal(paymentId.ToString("D"), receipt.EntityId);
        Assert.Equal(storedInvoice.Revision, receipt.ExpectedRevision);
        Assert.Equal(ProcessedSyncMutationRecorder.DirectApiDeviceId, receipt.DeviceId);
        Assert.Equal(mutationCreatedAtUtc, receipt.ProcessedAtUtc);
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
        var paymentController = CreatePaymentsController(dbContext, currentUser);

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
    public async Task InvoicesController_Create_IgnoresDeletedLinesWhenCalculatingTotals()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Deleted Line Total Customer",
            NameMatchKey = "DELETEDLINETOTALCUSTOMER",
            TradeType = "매출"
        };
        dbContext.Customers.Add(customer);
        await dbContext.SaveChangesAsync();

        var invoiceId = Guid.NewGuid();
        var activeLineId = Guid.NewGuid();
        var deletedLineId = Guid.NewGuid();
        var dto = new InvoiceDto
        {
            Id = invoiceId,
            CustomerId = customer.Id,
            CustomerName = customer.NameOriginal,
            TenantCode = customer.TenantCode,
            OfficeCode = customer.OfficeCode,
            ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
            VoucherType = VoucherType.Sales,
            VatMode = InvoiceVatModes.None,
            InvoiceDate = new DateOnly(2026, 6, 19),
            Lines =
            [
                new InvoiceLineDto
                {
                    Id = deletedLineId,
                    InvoiceId = invoiceId,
                    ItemNameOriginal = "삭제 라인",
                    Unit = "EA",
                    Quantity = 1m,
                    UnitPrice = 90000m,
                    LineAmount = 90000m,
                    OrderIndex = 1,
                    IsDeleted = true
                },
                new InvoiceLineDto
                {
                    Id = activeLineId,
                    InvoiceId = invoiceId,
                    ItemNameOriginal = "활성 라인",
                    Unit = "EA",
                    Quantity = 1m,
                    UnitPrice = 10000m,
                    LineAmount = 10000m,
                    OrderIndex = 2
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

        Assert.Equal(10000m, created.SupplyAmount);
        Assert.Equal(0m, created.VatAmount);
        Assert.Equal(10000m, created.TotalAmount);
        var line = Assert.Single(created.Lines);
        Assert.Equal(activeLineId, line.Id);

        var storedInvoice = await dbContext.Invoices.IgnoreQueryFilters()
            .Include(invoice => invoice.Lines)
            .SingleAsync(invoice => invoice.Id == invoiceId);
        Assert.Equal(10000m, storedInvoice.SupplyAmount);
        Assert.Equal(0m, storedInvoice.VatAmount);
        Assert.Equal(10000m, storedInvoice.TotalAmount);
        Assert.DoesNotContain(storedInvoice.Lines, current => current.Id == deletedLineId);
        Assert.Equal(storedInvoice.TotalAmount, storedInvoice.Lines.Where(lineRow => !lineRow.IsDeleted).Sum(lineRow => lineRow.LineAmount) + storedInvoice.VatAmount);
    }

    [Fact]
    public async Task InvoicesController_Create_RenumbersActiveLinesByPayloadOrder()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Invoice Line Order Customer",
            NameMatchKey = "INVOICELINEORDERCUSTOMER",
            TradeType = "매출"
        };
        dbContext.Customers.Add(customer);
        await dbContext.SaveChangesAsync();

        var invoiceId = Guid.NewGuid();
        var firstPayloadLineId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var secondPayloadLineId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var dto = new InvoiceDto
        {
            Id = invoiceId,
            CustomerId = customer.Id,
            CustomerName = customer.NameOriginal,
            TenantCode = customer.TenantCode,
            OfficeCode = customer.OfficeCode,
            ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
            VoucherType = VoucherType.Sales,
            VatMode = InvoiceVatModes.None,
            InvoiceDate = new DateOnly(2026, 6, 20),
            Lines =
            [
                new InvoiceLineDto
                {
                    Id = firstPayloadLineId,
                    InvoiceId = invoiceId,
                    ItemNameOriginal = "payload first",
                    Unit = "EA",
                    Quantity = 1m,
                    UnitPrice = 1000m,
                    LineAmount = 1000m,
                    OrderIndex = 50
                },
                new InvoiceLineDto
                {
                    Id = secondPayloadLineId,
                    InvoiceId = invoiceId,
                    ItemNameOriginal = "payload second",
                    Unit = "EA",
                    Quantity = 1m,
                    UnitPrice = 2000m,
                    LineAmount = 2000m,
                    OrderIndex = 50
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

        Assert.Equal(new[] { "payload first", "payload second" }, created.Lines.Select(line => line.ItemNameOriginal).ToArray());
        Assert.Equal(new[] { 1, 2 }, created.Lines.Select(line => line.OrderIndex).ToArray());

        var storedLines = await dbContext.InvoiceLines
            .AsNoTracking()
            .Where(line => line.InvoiceId == invoiceId)
            .OrderBy(line => line.OrderIndex)
            .ToListAsync();
        Assert.Equal(new[] { "payload first", "payload second" }, storedLines.Select(line => line.ItemNameOriginal).ToArray());
        Assert.Equal(new[] { 1, 2 }, storedLines.Select(line => line.OrderIndex).ToArray());
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
    public async Task InvoicesController_Create_ForbidsReadSharedCustomerReference()
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

        var readSharedCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            NameOriginal = "READ-SHARED-INVOICE-CUSTOMER",
            NameMatchKey = "READSHAREDINVOICECUSTOMER",
            TradeType = "Sales"
        };
        dbContext.Customers.Add(readSharedCustomer);
        dbContext.DataSharingPolicies.Add(new DataSharingPolicy
        {
            SourceTenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Yeonsu,
            TargetTenantCode = TenantScopeCatalog.UsenetGroup,
            TargetOfficeCode = OfficeCodeCatalog.Usenet,
            ShareCustomers = true,
            AllowTargetWrite = false
        });
        await dbContext.SaveChangesAsync();

        var dto = new InvoiceDto
        {
            Id = Guid.NewGuid(),
            CustomerId = readSharedCustomer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            VoucherType = VoucherType.Sales,
            SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            InvoiceDate = new DateOnly(2026, 6, 19),
            TotalAmount = 100m,
            SupplyAmount = 91m,
            VatAmount = 9m
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
    public async Task InvoicesController_Delete_ForbidsOutOfScopeItemLine()
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
            NameOriginal = "Legacy invoice hidden item customer",
            NameMatchKey = "LEGACYINVOICEHIDDENITEMCUSTOMER",
            TradeType = "Sales"
        };
        var hiddenItem = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            NameOriginal = "Hidden delete invoice item",
            NameMatchKey = "HIDDENDELETEINVOICEITEM",
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 4m
        };
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            VoucherType = VoucherType.Sales,
            SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            InvoiceNumber = "LEGACY-HIDDEN-ITEM-INVOICE",
            InvoiceDate = new DateOnly(2026, 6, 19),
            TotalAmount = 100m,
            SupplyAmount = 91m,
            VatAmount = 9m,
            Lines =
            [
                new InvoiceLine
                {
                    Id = Guid.NewGuid(),
                    ItemId = hiddenItem.Id,
                    ItemNameOriginal = hiddenItem.NameOriginal,
                    ItemTrackingType = ItemTrackingTypes.Stock,
                    Unit = "EA",
                    Quantity = 1m,
                    UnitPrice = 100m,
                    LineAmount = 100m,
                    OrderIndex = 1
                }
            ]
        };
        dbContext.Customers.Add(customer);
        dbContext.Items.Add(hiddenItem);
        dbContext.Invoices.Add(invoice);
        dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
        {
            ItemId = hiddenItem.Id,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 4m
        });
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Invoices.IgnoreQueryFilters().SingleAsync(row => row.Id == invoice.Id);
        var controller = new InvoicesController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));

        var response = await controller.Delete(stored.Id, stored.Revision, CancellationToken.None);

        Assert.IsType<ForbidResult>(response);
        Assert.False(await dbContext.Invoices.IgnoreQueryFilters()
            .Where(row => row.Id == invoice.Id)
            .Select(row => row.IsDeleted)
            .SingleAsync());
        Assert.Equal(4m, await dbContext.Items.IgnoreQueryFilters()
            .Where(row => row.Id == hiddenItem.Id)
            .Select(row => row.CurrentStock)
            .SingleAsync());
        Assert.Equal(4m, await dbContext.ItemWarehouseStocks
            .Where(row => row.ItemId == hiddenItem.Id && row.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(row => row.Quantity)
            .SingleAsync());
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
    public async Task SyncPush_RejectsInvoiceWithReadSharedCustomerReference()
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

        var readSharedCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            NameOriginal = "READ-SHARED-SYNC-INVOICE-CUSTOMER",
            NameMatchKey = "READSHAREDSYNCINVOICECUSTOMER",
            TradeType = "Sales"
        };
        dbContext.Customers.Add(readSharedCustomer);
        dbContext.DataSharingPolicies.Add(new DataSharingPolicy
        {
            SourceTenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Yeonsu,
            TargetTenantCode = TenantScopeCatalog.UsenetGroup,
            TargetOfficeCode = OfficeCodeCatalog.Usenet,
            ShareCustomers = true,
            AllowTargetWrite = false
        });
        await dbContext.SaveChangesAsync();

        var invoiceId = Guid.NewGuid();
        var result = AssertSyncOk(await CreateSyncController(dbContext, currentUser)
            .Push(new SyncPushRequest
            {
                DeviceId = "invoice-read-shared-customer-device",
                Invoices =
                [
                    new InvoiceDto
                    {
                        Id = invoiceId,
                        CustomerId = readSharedCustomer.Id,
                        TenantCode = TenantScopeCatalog.UsenetGroup,
                        OfficeCode = OfficeCodeCatalog.Usenet,
                        ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                        VoucherType = VoucherType.Sales,
                        SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                        InvoiceDate = new DateOnly(2026, 6, 19),
                        TotalAmount = 100m,
                        SupplyAmount = 91m,
                        VatAmount = 9m
                    }
                ]
            }, CancellationToken.None));

        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            string.Equals(conflict.EntityName, nameof(Invoice), StringComparison.Ordinal) &&
            conflict.Reason.Contains("Referenced customer is outside the writable office scope", StringComparison.Ordinal));
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
    public async Task SyncPush_RejectsTransactionWithReadSharedCustomerReference()
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

        var readSharedCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            NameOriginal = "READ-SHARED-SYNC-TRANSACTION-CUSTOMER",
            NameMatchKey = "READSHAREDSYNCTRANSACTIONCUSTOMER",
            TradeType = "Sales"
        };
        dbContext.Customers.Add(readSharedCustomer);
        dbContext.DataSharingPolicies.Add(new DataSharingPolicy
        {
            SourceTenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Yeonsu,
            TargetTenantCode = TenantScopeCatalog.UsenetGroup,
            TargetOfficeCode = OfficeCodeCatalog.Usenet,
            ShareCustomers = true,
            AllowTargetWrite = false
        });
        await dbContext.SaveChangesAsync();

        var transactionId = Guid.NewGuid();
        var result = AssertSyncOk(await CreateSyncController(dbContext, currentUser)
            .Push(new SyncPushRequest
            {
                DeviceId = "transaction-read-shared-customer-device",
                Transactions =
                [
                    new TransactionDto
                    {
                        Id = transactionId,
                        CustomerId = readSharedCustomer.Id,
                        TenantCode = TenantScopeCatalog.UsenetGroup,
                        OfficeCode = OfficeCodeCatalog.Usenet,
                        ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                        TransactionDate = new DateOnly(2026, 6, 19),
                        TransactionKind = "GeneralReceipt",
                        CashReceipt = 100m,
                        ReceiptTotal = 100m,
                        SettlementAmount = 100m
                    }
                ]
            }, CancellationToken.None));

        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            string.Equals(conflict.EntityName, nameof(TransactionRecord), StringComparison.Ordinal) &&
            conflict.Reason.Contains("Referenced customer is outside the writable office scope", StringComparison.Ordinal));
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
    public async Task InvoicesController_Delete_ForbidsLinkedPayments_WhenPaymentEditMissing()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "invoice-only-delete",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = new[] { PermissionNames.InvoiceEdit }
        };
        await using var dbContext = CreateDbContext(currentUser);

        var customerId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "DIRECT-INVOICE-DELETE-PAYMENT-PERM-CUSTOMER",
            NameMatchKey = "DIRECTINVOICEDELETEPAYMENTPERMCUSTOMER",
            TradeType = "Sales"
        });
        dbContext.Invoices.Add(new Invoice
        {
            Id = invoiceId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "DIRECT-INVOICE-DELETE-PAYMENT-PERM",
            VersionGroupId = invoiceId,
            VersionNumber = 1,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 24),
            TotalAmount = 100_000m,
            SupplyAmount = 90_909m,
            VatAmount = 9_091m
        });
        dbContext.Payments.Add(new Payment
        {
            Id = paymentId,
            InvoiceId = invoiceId,
            PaymentDate = new DateOnly(2026, 6, 25),
            Amount = 40_000m,
            Note = "direct delete linked payment"
        });
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Invoices.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(row => row.Id == invoiceId);
        var controller = new InvoicesController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));

        var response = await controller.Delete(stored.Id, stored.Revision, CancellationToken.None);

        Assert.IsType<ForbidResult>(response);
        Assert.False(await dbContext.Invoices.IgnoreQueryFilters()
            .Where(row => row.Id == invoiceId)
            .Select(row => row.IsDeleted)
            .SingleAsync());
        Assert.False(await dbContext.Payments.IgnoreQueryFilters()
            .Where(row => row.Id == paymentId)
            .Select(row => row.IsDeleted)
            .SingleAsync());
    }

    [Fact]
    public async Task InvoicesController_Delete_ForbidsLinkedTransactionRentalProfileOutsideWritableScope()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "invoice-payment-editor-usenet",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = new[] { PermissionNames.InvoiceEdit, PermissionNames.PaymentEdit }
        };
        await using var dbContext = CreateDbContext(currentUser);

        var scenario = SeedPaymentWithOutOfScopeLinkedTransactionRentalProfileScenario(dbContext);
        await dbContext.SaveChangesAsync();
        var storedInvoice = await dbContext.Invoices.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(invoice => invoice.Id == scenario.InvoiceId);
        var controller = new InvoicesController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));

        var deleteResponse = await controller.Delete(scenario.InvoiceId, storedInvoice.Revision, CancellationToken.None);

        Assert.IsType<ForbidResult>(deleteResponse);
        Assert.False(await dbContext.Invoices.IgnoreQueryFilters()
            .Where(invoice => invoice.Id == scenario.InvoiceId)
            .Select(invoice => invoice.IsDeleted)
            .SingleAsync());
        Assert.False(await dbContext.Payments.IgnoreQueryFilters()
            .Where(payment => payment.Id == scenario.PaymentId)
            .Select(payment => payment.IsDeleted)
            .SingleAsync());
        var unchangedTransaction = await dbContext.Transactions.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(transaction => transaction.Id == scenario.PaymentId);
        Assert.False(unchangedTransaction.IsDeleted);
        Assert.Equal(scenario.InvoiceId, unchangedTransaction.LinkedInvoiceId);
        Assert.Equal(scenario.ProfileId, unchangedTransaction.LinkedRentalBillingProfileId);
        Assert.Equal(scenario.RunId, unchangedTransaction.LinkedRentalBillingRunId);
        Assert.Equal(40_000m, unchangedTransaction.SettlementAmount);
        await AssertOutOfScopeRentalSettlementUnchangedAsync(dbContext, scenario.ProfileId, settledAmount: 40_000m, outstandingAmount: 60_000m);
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
        var paymentAttachmentId = Guid.NewGuid();
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
        dbContext.PaymentAttachments.Add(new PaymentAttachment
        {
            Id = paymentAttachmentId,
            PaymentId = transactionId,
            AttachmentType = "입금증빙",
            FileName = "rental-payment-receipt.pdf",
            MimeType = "application/pdf",
            FileSize = 4,
            FileHash = "test-hash",
            UploadedAtUtc = new DateTime(2026, 5, 26, 0, 0, 0, DateTimeKind.Utc),
            FileContent = [0x25, 0x50, 0x44, 0x46]
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
        var deletedAttachment = await dbContext.PaymentAttachments.IgnoreQueryFilters().AsNoTracking().SingleAsync(attachment => attachment.Id == paymentAttachmentId);
        Assert.True(deletedAttachment.IsDeleted);
        var detachedTransaction = await dbContext.Transactions.IgnoreQueryFilters().AsNoTracking().SingleAsync(transaction => transaction.Id == transactionId);
        Assert.Null(detachedTransaction.LinkedInvoiceId);
        Assert.Equal(0m, detachedTransaction.SettlementAmount);
        Assert.Null(detachedTransaction.LinkedRentalBillingProfileId);
        Assert.Null(detachedTransaction.LinkedRentalBillingRunId);
        Assert.Equal("일반수금", detachedTransaction.TransactionKind);
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
    public async Task RentalSettlementRecalculation_PreservesCancelledRunWhenOutstandingRemains()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customerId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();

        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Server rental cancelled customer",
            NameMatchKey = "SERVERRENTALCANCELLEDCUSTOMER",
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
            CustomerName = "Server rental cancelled customer",
            BillingStatus = "취소",
            SettlementStatus = "확인대기",
            CompletionStatus = "미완료",
            MonthlyAmount = 100_000m,
            SettledAmount = 0m,
            OutstandingAmount = 100_000m,
            BillingRunsJson = JsonSerializer.Serialize(new[]
            {
                new ServerRentalBillingRunSnapshot
                {
                    RunId = runId,
                    RunKey = "2026-06",
                    ScheduledDate = new DateOnly(2026, 6, 25),
                    PeriodStartDate = new DateOnly(2026, 6, 1),
                    PeriodEndDate = new DateOnly(2026, 6, 30),
                    PeriodLabel = "2026-06",
                    Status = "취소",
                    BilledAmount = 100_000m,
                    SettledAmount = 0m,
                    SettlementStatus = "확인대기"
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
            InvoiceNumber = "RENTAL-CANCEL-001",
            VersionGroupId = invoiceId,
            VersionNumber = 1,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 25),
            TotalAmount = 100_000m,
            SupplyAmount = 90_909m,
            VatAmount = 9_091m,
            LinkedRentalBillingProfileId = profileId,
            LinkedRentalBillingRunId = runId
        });
        await dbContext.SaveChangesAsync();

        var service = new RentalSettlementRecalculationService(dbContext);
        await service.RecalculateRentalSettlementsAsync([(profileId, runId)], CancellationToken.None);
        await dbContext.SaveChangesAsync();

        var recalculatedProfile = await dbContext.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(profile => profile.Id == profileId);
        Assert.Equal(0m, recalculatedProfile.SettledAmount);
        Assert.Equal(100_000m, recalculatedProfile.OutstandingAmount);
        Assert.Equal("취소", recalculatedProfile.BillingStatus);
        var recalculatedRun = Assert.Single(JsonSerializer.Deserialize<List<ServerRentalBillingRunSnapshot>>(recalculatedProfile.BillingRunsJson) ?? []);
        Assert.Equal("취소", recalculatedRun.Status);
        Assert.Equal(0m, recalculatedRun.SettledAmount);
        Assert.Equal("확인대기", recalculatedRun.SettlementStatus);
    }

    [Fact]
    public async Task RentalSettlementRecalculation_RestoresMissingBillingRunJsonFromLinkedInvoice()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customerId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();

        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Server rental missing run customer",
            NameMatchKey = "SERVERRENTALMISSINGRUNCUSTOMER",
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
            CustomerName = "Server rental missing run customer",
            BillingStatus = "청구중",
            SettlementStatus = "확인대기",
            CompletionStatus = "미완료",
            MonthlyAmount = 100_000m,
            BillingCycleMonths = 1,
            SettledAmount = 0m,
            OutstandingAmount = 0m,
            BillingRunsJson = "[]"
        });
        dbContext.Invoices.Add(new Invoice
        {
            Id = invoiceId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "RENTAL-MISSING-RUN-001",
            VersionGroupId = invoiceId,
            VersionNumber = 1,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 8, 25),
            TotalAmount = 120_000m,
            SupplyAmount = 109_091m,
            VatAmount = 10_909m,
            LinkedRentalBillingProfileId = profileId,
            LinkedRentalBillingRunId = runId
        });
        dbContext.Transactions.Add(new TransactionRecord
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 8, 26),
            TransactionKind = "전표수금",
            LinkedInvoiceId = invoiceId,
            LinkedInvoiceNumber = "RENTAL-MISSING-RUN-001",
            LinkedRentalBillingProfileId = profileId,
            LinkedRentalBillingRunId = runId,
            BankReceipt = 40_000m,
            ReceiptTotal = 40_000m,
            SettlementAmount = 40_000m
        });
        await dbContext.SaveChangesAsync();

        var service = new RentalSettlementRecalculationService(dbContext);
        await service.RecalculateRentalSettlementsAsync([(profileId, runId)], CancellationToken.None);
        await dbContext.SaveChangesAsync();

        var recalculatedProfile = await dbContext.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(profile => profile.Id == profileId);
        Assert.Equal(40_000m, recalculatedProfile.SettledAmount);
        Assert.Equal(80_000m, recalculatedProfile.OutstandingAmount);
        Assert.Equal("미완료", recalculatedProfile.CompletionStatus);

        var restoredRun = Assert.Single(JsonSerializer.Deserialize<List<ServerRentalBillingRunSnapshot>>(recalculatedProfile.BillingRunsJson) ?? [], current => current.RunId == runId);
        Assert.Equal("20260801-20260831", restoredRun.RunKey);
        Assert.Equal(new DateOnly(2026, 8, 25), restoredRun.ScheduledDate);
        Assert.Equal(new DateOnly(2026, 8, 1), restoredRun.PeriodStartDate);
        Assert.Equal(new DateOnly(2026, 8, 31), restoredRun.PeriodEndDate);
        Assert.Equal("2026-08", restoredRun.PeriodLabel);
        Assert.Equal(120_000m, restoredRun.BilledAmount);
        Assert.Equal(40_000m, restoredRun.SettledAmount);
        Assert.Equal(new DateOnly(2026, 8, 26), restoredRun.SettledDate);
    }

    [Fact]
    public async Task RentalSettlementRecalculation_IgnoresZeroAmountEvidenceWhenRestoringMissingBillingRunJson()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customerId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var zeroPaymentId = Guid.NewGuid();

        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Server rental zero evidence customer",
            NameMatchKey = "SERVERRENTALZEROEVIDENCECUSTOMER",
            TradeType = "Sales"
        });
        dbContext.RentalBillingProfiles.Add(new RentalBillingProfile
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ProfileKey = $"profile-zero-evidence-{profileId:N}",
            CustomerId = customerId,
            CustomerName = "Server rental zero evidence customer",
            BillingStatus = "in-progress",
            SettlementStatus = "pending",
            CompletionStatus = "incomplete",
            MonthlyAmount = 100_000m,
            BillingCycleMonths = 1,
            SettledAmount = 0m,
            OutstandingAmount = 0m,
            BillingRunsJson = "[]"
        });
        dbContext.Invoices.Add(new Invoice
        {
            Id = invoiceId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "RENTAL-ZERO-EVIDENCE-001",
            VersionGroupId = invoiceId,
            VersionNumber = 1,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 9, 25),
            TotalAmount = 0m,
            SupplyAmount = 0m,
            VatAmount = 0m,
            LinkedRentalBillingProfileId = profileId,
            LinkedRentalBillingRunId = runId
        });
        dbContext.Payments.Add(new Payment
        {
            Id = zeroPaymentId,
            InvoiceId = invoiceId,
            PaymentDate = new DateOnly(2026, 9, 26),
            Amount = 0m,
            Note = "zero amount direct rental payment must not restore billing run"
        });
        dbContext.Transactions.Add(new TransactionRecord
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 9, 26),
            TransactionKind = "rental payment",
            LinkedInvoiceId = invoiceId,
            LinkedInvoiceNumber = "RENTAL-ZERO-EVIDENCE-001",
            LinkedRentalBillingProfileId = profileId,
            LinkedRentalBillingRunId = runId,
            BankReceipt = 0m,
            ReceiptTotal = 0m,
            SettlementAmount = 0m,
            Note = "zero amount rental transaction must not restore billing run"
        });
        await dbContext.SaveChangesAsync();

        var service = new RentalSettlementRecalculationService(dbContext);
        await service.RecalculateRentalSettlementsAsync([(profileId, runId)], CancellationToken.None);
        await dbContext.SaveChangesAsync();

        var recalculatedProfile = await dbContext.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(profile => profile.Id == profileId);
        Assert.Equal(0m, recalculatedProfile.SettledAmount);
        Assert.Equal(100_000m, recalculatedProfile.OutstandingAmount);
        Assert.Null(recalculatedProfile.LastSettledDate);
        Assert.Empty(JsonSerializer.Deserialize<List<ServerRentalBillingRunSnapshot>>(recalculatedProfile.BillingRunsJson) ?? []);
    }

    [Fact]
    public async Task InvoicesController_Update_RentalBillingInvoice_RecalculatesBilledAndOutstandingAmounts()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customerId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();

        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Server rental update customer",
            NameMatchKey = "SERVERRENTALUPDATECUSTOMER",
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
            CustomerName = "Server rental update customer",
            BillingStatus = "청구중",
            SettlementStatus = "부분입금",
            CompletionStatus = "미완료",
            MonthlyAmount = 100_000m,
            SettledAmount = 50_000m,
            OutstandingAmount = 50_000m,
            BillingRunsJson = JsonSerializer.Serialize(new[]
            {
                new ServerRentalBillingRunSnapshot
                {
                    RunId = runId,
                    RunKey = "2026-06",
                    ScheduledDate = new DateOnly(2026, 6, 25),
                    PeriodStartDate = new DateOnly(2026, 6, 1),
                    PeriodEndDate = new DateOnly(2026, 6, 30),
                    PeriodLabel = "2026-06",
                    Status = "청구중",
                    BilledAmount = 100_000m,
                    SettledAmount = 50_000m,
                    SettlementStatus = "부분입금",
                    SettledDate = new DateOnly(2026, 6, 26)
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
            InvoiceNumber = "RENTAL-UPD-001",
            VersionGroupId = invoiceId,
            VersionNumber = 1,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 25),
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
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 6, 26),
            TransactionKind = "렌탈수금",
            LinkedInvoiceId = invoiceId,
            LinkedInvoiceNumber = "RENTAL-UPD-001",
            LinkedRentalBillingProfileId = profileId,
            LinkedRentalBillingRunId = runId,
            BankReceipt = 50_000m,
            ReceiptTotal = 50_000m,
            SettlementAmount = 50_000m
        });
        await dbContext.SaveChangesAsync();

        var storedInvoice = await dbContext.Invoices
            .IgnoreQueryFilters()
            .Include(invoice => invoice.Customer)
            .Include(invoice => invoice.Lines)
            .SingleAsync(invoice => invoice.Id == invoiceId);
        var updateDto = storedInvoice.ToDto();
        updateDto.ExpectedRevision = storedInvoice.Revision;
        updateDto.Lines[0].UnitPrice = 120_000m;
        updateDto.Lines[0].LineAmount = 120_000m;

        var controller = new InvoicesController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));

        var updateResponse = await controller.Update(invoiceId, updateDto, CancellationToken.None);

        Assert.IsType<OkObjectResult>(updateResponse.Result);
        var updatedInvoice = await dbContext.Invoices.IgnoreQueryFilters().AsNoTracking().SingleAsync(invoice => invoice.Id == invoiceId);
        Assert.Equal(120_000m, updatedInvoice.TotalAmount);
        var updatedProfile = await dbContext.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(profile => profile.Id == profileId);
        Assert.Equal(50_000m, updatedProfile.SettledAmount);
        Assert.Equal(70_000m, updatedProfile.OutstandingAmount);
        Assert.Equal("부분입금", updatedProfile.SettlementStatus);
        Assert.Equal("미완료", updatedProfile.CompletionStatus);
        var updatedRun = Assert.Single(JsonSerializer.Deserialize<List<ServerRentalBillingRunSnapshot>>(updatedProfile.BillingRunsJson) ?? []);
        Assert.Equal(120_000m, updatedRun.BilledAmount);
        Assert.Equal(50_000m, updatedRun.SettledAmount);
        Assert.Equal("부분입금", updatedRun.SettlementStatus);
        Assert.Equal("청구중", updatedRun.Status);
    }

    [Fact]
    public async Task PaymentsController_Create_RentalBillingDirectPayment_RecalculatesSettlement()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var scenario = SeedRentalDirectPaymentScenario(dbContext);
        await dbContext.SaveChangesAsync();

        var controller = CreatePaymentsController(dbContext, currentUser);
        var createResponse = await controller.Create(new PaymentDto
        {
            Id = scenario.PaymentId,
            InvoiceId = scenario.InvoiceId,
            PaymentDate = new DateOnly(2026, 7, 26),
            Amount = 40_000m,
            Note = "direct rental payment"
        }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(createResponse.Result);
        await AssertRentalSettlementAsync(dbContext, scenario.ProfileId, scenario.RunId, expectedSettled: 40_000m, expectedOutstanding: 60_000m);
    }

    [Fact]
    public async Task PaymentsController_Create_ForbidsInvoiceLinkedToRentalProfileOutsideWritableScope()
    {
        var currentUser = CreateOfficePaymentEditor();
        await using var dbContext = CreateDbContext(currentUser);

        var scenario = SeedOutOfScopeRentalLinkedInvoiceScenario(dbContext);
        await dbContext.SaveChangesAsync();
        var paymentId = Guid.NewGuid();

        var controller = CreatePaymentsController(dbContext, currentUser);
        var createResponse = await controller.Create(new PaymentDto
        {
            Id = paymentId,
            InvoiceId = scenario.InvoiceId,
            PaymentDate = new DateOnly(2026, 8, 26),
            Amount = 40_000m,
            Note = "must be rejected because rental profile is outside writable scope"
        }, CancellationToken.None);

        Assert.IsType<ForbidResult>(createResponse.Result);
        Assert.False(await dbContext.Payments.IgnoreQueryFilters().AnyAsync(payment => payment.Id == paymentId));
        await AssertOutOfScopeRentalSettlementUnchangedAsync(dbContext, scenario.ProfileId, settledAmount: 0m, outstandingAmount: 100_000m);
    }

    [Fact]
    public async Task PaymentsController_Create_RejectsInvoiceWhoseCustomerIsDeleted()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var deletedCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "REST-PAYMENT-DELETED-CUSTOMER",
            NameMatchKey = "RESTPAYMENTDELETEDCUSTOMER",
            TradeType = "Sales",
            IsDeleted = true
        };
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = deletedCustomer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "PAY-DELETED-CUSTOMER-001",
            LocalTempNumber = "PAY-DELETED-CUSTOMER-TMP-001",
            InvoiceDate = new DateOnly(2026, 6, 19),
            VoucherType = VoucherType.Sales,
            TotalAmount = 50_000m,
            SupplyAmount = 45_455m,
            VatAmount = 4_545m
        };
        dbContext.Customers.Add(deletedCustomer);
        dbContext.Invoices.Add(invoice);
        await dbContext.SaveChangesAsync();

        var controller = CreatePaymentsController(dbContext, currentUser);
        var paymentId = Guid.NewGuid();
        var response = await controller.Create(new PaymentDto
        {
            Id = paymentId,
            InvoiceId = invoice.Id,
            PaymentDate = new DateOnly(2026, 6, 19),
            Amount = 10_000m,
            Note = "should be rejected"
        }, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Contains("invoice_customer_not_found", badRequest.Value?.ToString(), StringComparison.Ordinal);
        Assert.False(await dbContext.Payments.IgnoreQueryFilters().AnyAsync(payment => payment.Id == paymentId));
    }

    [Fact]
    public async Task PaymentsController_Update_RentalBillingDirectPayment_RecalculatesSettlement()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var scenario = SeedRentalDirectPaymentScenario(dbContext, existingPaymentAmount: 40_000m, storedSettledAmount: 40_000m);
        await dbContext.SaveChangesAsync();
        var storedPayment = await dbContext.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(payment => payment.Id == scenario.PaymentId);

        var controller = CreatePaymentsController(dbContext, currentUser);
        var updateResponse = await controller.Update(scenario.PaymentId, new PaymentDto
        {
            Id = scenario.PaymentId,
            InvoiceId = scenario.InvoiceId,
            PaymentDate = storedPayment.PaymentDate,
            Amount = 70_000m,
            Note = "direct rental payment updated",
            ExpectedRevision = storedPayment.Revision
        }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(updateResponse.Result);
        await AssertRentalSettlementAsync(dbContext, scenario.ProfileId, scenario.RunId, expectedSettled: 70_000m, expectedOutstanding: 30_000m);
    }

    [Fact]
    public async Task PaymentsController_Update_DerivedLinkedPayment_UpdatesSourceTransactionAndSettlement()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var scenario = SeedRentalDirectPaymentScenario(dbContext, existingPaymentAmount: 40_000m, storedSettledAmount: 40_000m);
        dbContext.Transactions.Add(new TransactionRecord
        {
            Id = scenario.PaymentId,
            CustomerId = scenario.CustomerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 7, 26),
            TransactionKind = "렌탈수금",
            LinkedInvoiceId = scenario.InvoiceId,
            LinkedInvoiceNumber = "RENTAL-DIRECT-PAY-001",
            LinkedRentalBillingProfileId = scenario.ProfileId,
            LinkedRentalBillingRunId = scenario.RunId,
            BankReceipt = 40_000m,
            ReceiptTotal = 40_000m,
            SettlementAmount = 40_000m
        });
        await dbContext.SaveChangesAsync();
        var storedPayment = await dbContext.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(payment => payment.Id == scenario.PaymentId);

        var controller = CreatePaymentsController(dbContext, currentUser);
        var updateResponse = await controller.Update(scenario.PaymentId, new PaymentDto
        {
            Id = scenario.PaymentId,
            InvoiceId = scenario.InvoiceId,
            PaymentDate = new DateOnly(2026, 7, 27),
            Amount = 70_000m,
            Note = "derived rental payment updated",
            ExpectedRevision = storedPayment.Revision
        }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(updateResponse.Result);
        dbContext.ChangeTracker.Clear();
        var linkedTransaction = await dbContext.Transactions.IgnoreQueryFilters().AsNoTracking().SingleAsync(transaction => transaction.Id == scenario.PaymentId);
        Assert.Equal(scenario.InvoiceId, linkedTransaction.LinkedInvoiceId);
        Assert.Equal("RENTAL-DIRECT-PAY-001", linkedTransaction.LinkedInvoiceNumber);
        Assert.Equal(scenario.CustomerId, linkedTransaction.CustomerId);
        Assert.Equal(scenario.ProfileId, linkedTransaction.LinkedRentalBillingProfileId);
        Assert.Equal(scenario.RunId, linkedTransaction.LinkedRentalBillingRunId);
        Assert.Equal(new DateOnly(2026, 7, 27), linkedTransaction.TransactionDate);
        Assert.Equal(70_000m, linkedTransaction.SettlementAmount);
        Assert.Equal(70_000m, linkedTransaction.ReceiptTotal);
        Assert.Equal(70_000m, linkedTransaction.BankReceipt);
        await AssertRentalSettlementAsync(
            dbContext,
            scenario.ProfileId,
            scenario.RunId,
            expectedSettled: 70_000m,
            expectedOutstanding: 30_000m,
            expectedSettledDate: new DateOnly(2026, 7, 27));
    }

    [Fact]
    public async Task PaymentsController_Update_ForbidsExistingRentalProfileOutsideWritableScope()
    {
        var currentUser = CreateOfficePaymentEditor();
        await using var dbContext = CreateDbContext(currentUser);

        var scenario = SeedOutOfScopeRentalLinkedInvoiceScenario(dbContext, existingPaymentAmount: 40_000m, storedSettledAmount: 40_000m);
        await dbContext.SaveChangesAsync();
        var storedPayment = await dbContext.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(payment => payment.Id == scenario.PaymentId);

        var controller = CreatePaymentsController(dbContext, currentUser);
        var updateResponse = await controller.Update(scenario.PaymentId, new PaymentDto
        {
            Id = scenario.PaymentId,
            InvoiceId = scenario.InvoiceId,
            PaymentDate = storedPayment.PaymentDate,
            Amount = 70_000m,
            Note = "must not update outside rental profile",
            ExpectedRevision = storedPayment.Revision
        }, CancellationToken.None);

        Assert.IsType<ForbidResult>(updateResponse.Result);
        var unchangedPayment = await dbContext.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(payment => payment.Id == scenario.PaymentId);
        Assert.Equal(40_000m, unchangedPayment.Amount);
        Assert.Equal("seeded out-of-scope rental payment", unchangedPayment.Note);
        await AssertOutOfScopeRentalSettlementUnchangedAsync(dbContext, scenario.ProfileId, settledAmount: 40_000m, outstandingAmount: 60_000m);
    }

    [Fact]
    public async Task PaymentsController_Update_ForbidsLinkedTransactionRentalProfileOutsideWritableScope()
    {
        var currentUser = CreateOfficePaymentEditor();
        await using var dbContext = CreateDbContext(currentUser);

        var scenario = SeedPaymentWithOutOfScopeLinkedTransactionRentalProfileScenario(dbContext);
        await dbContext.SaveChangesAsync();
        var storedPayment = await dbContext.Payments.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(payment => payment.Id == scenario.PaymentId);

        var controller = CreatePaymentsController(dbContext, currentUser);
        var updateResponse = await controller.Update(scenario.PaymentId, new PaymentDto
        {
            Id = scenario.PaymentId,
            InvoiceId = scenario.InvoiceId,
            PaymentDate = storedPayment.PaymentDate,
            Amount = 30_000m,
            Note = "must not recalculate hidden linked transaction rental profile",
            ExpectedRevision = storedPayment.Revision
        }, CancellationToken.None);

        Assert.IsType<ForbidResult>(updateResponse.Result);
        var unchangedPayment = await dbContext.Payments.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(payment => payment.Id == scenario.PaymentId);
        Assert.Equal(40_000m, unchangedPayment.Amount);
        Assert.Equal("seeded linked transaction payment", unchangedPayment.Note);
        var unchangedTransaction = await dbContext.Transactions.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(transaction => transaction.Id == scenario.PaymentId);
        Assert.False(unchangedTransaction.IsDeleted);
        Assert.Equal(scenario.ProfileId, unchangedTransaction.LinkedRentalBillingProfileId);
        Assert.Equal(scenario.RunId, unchangedTransaction.LinkedRentalBillingRunId);
        Assert.Equal(40_000m, unchangedTransaction.SettlementAmount);
        await AssertOutOfScopeRentalSettlementUnchangedAsync(dbContext, scenario.ProfileId, settledAmount: 40_000m, outstandingAmount: 60_000m);
    }

    [Fact]
    public async Task PaymentsController_Update_RejectsInvoiceWhoseCustomerIsDeleted()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var activeCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "REST-PAYMENT-ACTIVE-CUSTOMER",
            NameMatchKey = "RESTPAYMENTACTIVECUSTOMER",
            TradeType = "Sales"
        };
        var deletedCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "REST-PAYMENT-UPDATE-DELETED-CUSTOMER",
            NameMatchKey = "RESTPAYMENTUPDATEDELETEDCUSTOMER",
            TradeType = "Sales",
            IsDeleted = true
        };
        var activeInvoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = activeCustomer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "PAY-ACTIVE-CUSTOMER-001",
            LocalTempNumber = "PAY-ACTIVE-CUSTOMER-TMP-001",
            InvoiceDate = new DateOnly(2026, 6, 19),
            VoucherType = VoucherType.Sales,
            TotalAmount = 50_000m,
            SupplyAmount = 45_455m,
            VatAmount = 4_545m
        };
        var deletedCustomerInvoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = deletedCustomer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "PAY-UPDATE-DELETED-CUSTOMER-001",
            LocalTempNumber = "PAY-UPDATE-DELETED-CUSTOMER-TMP-001",
            InvoiceDate = new DateOnly(2026, 6, 19),
            VoucherType = VoucherType.Sales,
            TotalAmount = 50_000m,
            SupplyAmount = 45_455m,
            VatAmount = 4_545m
        };
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            InvoiceId = activeInvoice.Id,
            PaymentDate = new DateOnly(2026, 6, 19),
            Amount = 10_000m,
            Note = "stored payment"
        };
        dbContext.Customers.AddRange(activeCustomer, deletedCustomer);
        dbContext.Invoices.AddRange(activeInvoice, deletedCustomerInvoice);
        dbContext.Payments.Add(payment);
        await dbContext.SaveChangesAsync();

        var storedPayment = await dbContext.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(row => row.Id == payment.Id);
        var controller = CreatePaymentsController(dbContext, currentUser);
        var response = await controller.Update(payment.Id, new PaymentDto
        {
            Id = payment.Id,
            InvoiceId = deletedCustomerInvoice.Id,
            PaymentDate = payment.PaymentDate,
            Amount = 10_000m,
            Note = "should not relink",
            ExpectedRevision = storedPayment.Revision
        }, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Contains("invoice_customer_not_found", badRequest.Value?.ToString(), StringComparison.Ordinal);
        var unchanged = await dbContext.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(row => row.Id == payment.Id);
        Assert.Equal(activeInvoice.Id, unchanged.InvoiceId);
        Assert.Equal("stored payment", unchanged.Note);
    }

    [Fact]
    public async Task PaymentsController_Delete_RentalBillingDirectPayment_RecalculatesSettlement()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var scenario = SeedRentalDirectPaymentScenario(dbContext, existingPaymentAmount: 40_000m, storedSettledAmount: 40_000m);
        await dbContext.SaveChangesAsync();
        var storedPayment = await dbContext.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(payment => payment.Id == scenario.PaymentId);

        var controller = CreatePaymentsController(dbContext, currentUser);
        var deleteResponse = await controller.Delete(scenario.PaymentId, storedPayment.Revision, CancellationToken.None);

        Assert.IsType<NoContentResult>(deleteResponse);
        await AssertRentalSettlementAsync(dbContext, scenario.ProfileId, scenario.RunId, expectedSettled: 0m, expectedOutstanding: 100_000m);
    }

    [Fact]
    public async Task PaymentsController_Delete_DerivedLinkedPayment_DeletesSourceTransactionAndRevertsSettlement()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var scenario = SeedRentalDirectPaymentScenario(dbContext, existingPaymentAmount: 40_000m, storedSettledAmount: 40_000m);
        dbContext.Transactions.Add(new TransactionRecord
        {
            Id = scenario.PaymentId,
            CustomerId = scenario.CustomerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 7, 26),
            TransactionKind = "렌탈수금",
            LinkedInvoiceId = scenario.InvoiceId,
            LinkedInvoiceNumber = "RENTAL-DIRECT-PAY-001",
            LinkedRentalBillingProfileId = scenario.ProfileId,
            LinkedRentalBillingRunId = scenario.RunId,
            ReceiptTotal = 40_000m,
            BankReceipt = 40_000m,
            SettlementAmount = 40_000m,
            Note = "mobile linked transaction"
        });
        dbContext.TransactionAttachments.Add(new TransactionAttachment
        {
            Id = Guid.NewGuid(),
            TransactionId = scenario.PaymentId,
            FileName = "linked-transaction-evidence.pdf",
            StoragePath = "storage/linked-transaction-evidence.pdf"
        });
        await dbContext.SaveChangesAsync();
        var storedPayment = await dbContext.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(payment => payment.Id == scenario.PaymentId);

        var controller = CreatePaymentsController(dbContext, currentUser);
        var deleteResponse = await controller.Delete(scenario.PaymentId, storedPayment.Revision, CancellationToken.None);

        Assert.IsType<NoContentResult>(deleteResponse);
        var deletedPayment = await dbContext.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(payment => payment.Id == scenario.PaymentId);
        Assert.True(deletedPayment.IsDeleted);
        var deletedTransaction = await dbContext.Transactions.IgnoreQueryFilters().AsNoTracking().SingleAsync(transaction => transaction.Id == scenario.PaymentId);
        Assert.True(deletedTransaction.IsDeleted);
        var deletedAttachment = await dbContext.TransactionAttachments.IgnoreQueryFilters().AsNoTracking().SingleAsync(attachment => attachment.TransactionId == scenario.PaymentId);
        Assert.True(deletedAttachment.IsDeleted);
        await AssertRentalSettlementAsync(dbContext, scenario.ProfileId, scenario.RunId, expectedSettled: 0m, expectedOutstanding: 100_000m);
    }

    [Fact]
    public async Task PaymentsController_Delete_ForbidsExistingRentalProfileOutsideWritableScope()
    {
        var currentUser = CreateOfficePaymentEditor();
        await using var dbContext = CreateDbContext(currentUser);

        var scenario = SeedOutOfScopeRentalLinkedInvoiceScenario(dbContext, existingPaymentAmount: 40_000m, storedSettledAmount: 40_000m);
        await dbContext.SaveChangesAsync();
        var storedPayment = await dbContext.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(payment => payment.Id == scenario.PaymentId);

        var controller = CreatePaymentsController(dbContext, currentUser);
        var deleteResponse = await controller.Delete(scenario.PaymentId, storedPayment.Revision, CancellationToken.None);

        Assert.IsType<ForbidResult>(deleteResponse);
        var unchangedPayment = await dbContext.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(payment => payment.Id == scenario.PaymentId);
        Assert.False(unchangedPayment.IsDeleted);
        await AssertOutOfScopeRentalSettlementUnchangedAsync(dbContext, scenario.ProfileId, settledAmount: 40_000m, outstandingAmount: 60_000m);
    }

    [Fact]
    public async Task PaymentsController_Delete_ForbidsLinkedTransactionRentalProfileOutsideWritableScope()
    {
        var currentUser = CreateOfficePaymentEditor();
        await using var dbContext = CreateDbContext(currentUser);

        var scenario = SeedPaymentWithOutOfScopeLinkedTransactionRentalProfileScenario(dbContext);
        await dbContext.SaveChangesAsync();
        var storedPayment = await dbContext.Payments.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(payment => payment.Id == scenario.PaymentId);

        var controller = CreatePaymentsController(dbContext, currentUser);
        var deleteResponse = await controller.Delete(scenario.PaymentId, storedPayment.Revision, CancellationToken.None);

        Assert.IsType<ForbidResult>(deleteResponse);
        var unchangedPayment = await dbContext.Payments.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(payment => payment.Id == scenario.PaymentId);
        Assert.False(unchangedPayment.IsDeleted);
        var unchangedTransaction = await dbContext.Transactions.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(transaction => transaction.Id == scenario.PaymentId);
        Assert.False(unchangedTransaction.IsDeleted);
        Assert.Equal(scenario.ProfileId, unchangedTransaction.LinkedRentalBillingProfileId);
        Assert.Equal(scenario.RunId, unchangedTransaction.LinkedRentalBillingRunId);
        Assert.Equal(40_000m, unchangedTransaction.SettlementAmount);
        await AssertOutOfScopeRentalSettlementUnchangedAsync(dbContext, scenario.ProfileId, settledAmount: 40_000m, outstandingAmount: 60_000m);
    }

    [Fact]
    public async Task InvoicesController_Delete_ForbidsRentalProfileOutsideWritableScope()
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

        var scenario = SeedOutOfScopeRentalLinkedInvoiceScenario(dbContext, existingPaymentAmount: 40_000m, storedSettledAmount: 40_000m);
        await dbContext.SaveChangesAsync();
        var storedInvoice = await dbContext.Invoices.IgnoreQueryFilters().AsNoTracking().SingleAsync(invoice => invoice.Id == scenario.InvoiceId);
        var controller = new InvoicesController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, new RevisionClock()),
            new RentalSettlementRecalculationService(dbContext));

        var deleteResponse = await controller.Delete(scenario.InvoiceId, storedInvoice.Revision, CancellationToken.None);

        Assert.IsType<ForbidResult>(deleteResponse);
        Assert.False(await dbContext.Invoices.IgnoreQueryFilters()
            .Where(invoice => invoice.Id == scenario.InvoiceId)
            .Select(invoice => invoice.IsDeleted)
            .SingleAsync());
        Assert.False(await dbContext.Payments.IgnoreQueryFilters()
            .Where(payment => payment.Id == scenario.PaymentId)
            .Select(payment => payment.IsDeleted)
            .SingleAsync());
        await AssertOutOfScopeRentalSettlementUnchangedAsync(dbContext, scenario.ProfileId, settledAmount: 40_000m, outstandingAmount: 60_000m);
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
            PurchaseReceivingRequired = true,
            PurchaseReceivingStatus = InvoiceReceivingStatuses.Confirmed,
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
    public async Task InvoicesController_PurchasePending_DoesNotAdjustWarehouseStockOrLedger()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Purchase pending vendor",
            NameMatchKey = "PURCHASEPENDINGVENDOR",
            TradeType = "Purchase"
        };
        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Purchase pending item",
            NameMatchKey = "PURCHASEPENDINGITEM",
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
        var createResponse = await controller.Create(new InvoiceDto
        {
            Id = invoiceId,
            CustomerId = customer.Id,
            CustomerName = customer.NameOriginal,
            TenantCode = customer.TenantCode,
            OfficeCode = customer.OfficeCode,
            ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
            SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            VoucherType = VoucherType.Purchase,
            PurchaseReceivingRequired = true,
            PurchaseReceivingStatus = InvoiceReceivingStatuses.Pending,
            InvoiceDate = new DateOnly(2026, 5, 22),
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
        }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(createResponse.Result);
        Assert.Equal(5m, await dbContext.ItemWarehouseStocks
            .Where(stock => stock.ItemId == item.Id && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
        Assert.Equal(5m, (await dbContext.Items.IgnoreQueryFilters().SingleAsync(row => row.Id == item.Id)).CurrentStock);
        Assert.False(await dbContext.InventoryLedgerEntries.AnyAsync(entry => entry.SourceDocumentId == invoiceId));
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
        var controller = CreatePaymentsController(dbContext, currentUser);

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

        var controller = CreatePaymentsController(dbContext, currentUser);

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

        var controller = CreatePaymentsController(dbContext, currentUser);
        var clientAttachmentId = Guid.NewGuid();

        var first = AssertOk<PaymentAttachmentDto>(await controller.UploadAttachment(
            payment.Id,
            CreateFormFile("retry-receipt.pdf", "application/pdf", TestPdfBytes("first upload")),
            "내역첨부",
            "mobile retry",
            clientAttachmentId,
            CancellationToken.None));
        var second = AssertOk<PaymentAttachmentDto>(await controller.UploadAttachment(
            payment.Id,
            CreateFormFile("retry-receipt.pdf", "application/pdf", TestPdfBytes("second upload should not create duplicate")),
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
    public async Task PaymentsController_UploadAttachment_RejectsFileContentThatDoesNotMatchFileType()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Payment attachment signature customer",
            NameMatchKey = "PAYMENTATTACHMENTSIGNATURECUSTOMER",
            TradeType = "Sales"
        };
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "PAYMENT-ATTACHMENT-SIGNATURE",
            InvoiceDate = new DateOnly(2026, 6, 19)
        };
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            InvoiceId = invoice.Id,
            PaymentDate = new DateOnly(2026, 6, 19),
            Amount = 5000m,
            Note = "signature mismatch"
        };
        dbContext.Customers.Add(customer);
        dbContext.Invoices.Add(invoice);
        dbContext.Payments.Add(payment);
        await dbContext.SaveChangesAsync();

        var controller = CreatePaymentsController(dbContext, currentUser);

        var response = await controller.UploadAttachment(
            payment.Id,
            CreateFormFile("fake-receipt.pdf", "application/pdf", [0x4D, 0x5A, 0x90, 0x00]),
            "내역첨부",
            "fake pdf content must not be stored",
            Guid.NewGuid(),
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.Contains("file_content_mismatch", badRequest.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await dbContext.PaymentAttachments.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task PaymentsController_DoesNotExposePaymentAttachments_WhenParentInvoiceIsNotReadable()
    {
        var adminUser = CreateAdminUser();
        await using (var seedDb = CreateDbContext(adminUser))
        {
            seedDb.DataSharingPolicies.Add(new DataSharingPolicy
            {
                Id = Guid.NewGuid(),
                SourceTenantCode = TenantScopeCatalog.UsenetGroup,
                SourceOfficeCode = OfficeCodeCatalog.Usenet,
                TargetTenantCode = TenantScopeCatalog.UsenetGroup,
                TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
                ShareCustomers = false,
                ShareItems = false,
                ShareInvoices = false,
                SharePayments = true,
                ShareContracts = false,
                ShareReports = false,
                ShareRentals = false,
                ShareDeliveries = false,
                AllowTargetWrite = false,
                IsActive = true
            });

            var customer = new Customer
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Hidden invoice payment attachment customer",
                NameMatchKey = "HIDDENINVOICEPAYMENTATTACHMENTCUSTOMER",
                TradeType = "Sales"
            };
            var invoice = new Invoice
            {
                Id = Guid.NewGuid(),
                CustomerId = customer.Id,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                InvoiceNumber = "PAYMENT-ONLY-HIDDEN-INVOICE-DIRECT",
                InvoiceDate = new DateOnly(2026, 6, 24),
                VoucherType = VoucherType.Sales
            };
            var payment = new Payment
            {
                Id = Guid.NewGuid(),
                InvoiceId = invoice.Id,
                PaymentDate = new DateOnly(2026, 6, 24),
                Amount = 20_000m,
                Note = "payment only hidden direct API"
            };
            var attachment = new PaymentAttachment
            {
                Id = Guid.NewGuid(),
                PaymentId = payment.Id,
                AttachmentType = "PDF",
                FileName = "hidden-direct-payment.pdf",
                MimeType = "application/pdf",
                FileSize = TestPdfBytes("hidden direct payment attachment").LongLength,
                FileHash = "hash",
                FileContent = TestPdfBytes("hidden direct payment attachment"),
                UploadedAtUtc = new DateTime(2026, 6, 24, 0, 1, 0, DateTimeKind.Utc)
            };

            seedDb.Customers.Add(customer);
            seedDb.Invoices.Add(invoice);
            seedDb.Payments.Add(payment);
            seedDb.PaymentAttachments.Add(attachment);
            await seedDb.SaveChangesAsync();

            var scopedUser = new TestCurrentUserContext
            {
                Username = "yeonsu-payment-only-direct-reader",
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Yeonsu,
                ScopeType = TenantScopeCatalog.ScopeOfficeOnly
            };
            await using var scopedDb = CreateDbContext(scopedUser);
            var controller = CreatePaymentsController(scopedDb, scopedUser);

            var paymentsResponse = await controller.GetByInvoice(invoice.Id, CancellationToken.None);
            var paymentsOk = Assert.IsType<OkObjectResult>(paymentsResponse.Result);
            var payments = Assert.IsType<List<PaymentDto>>(paymentsOk.Value);
            Assert.Empty(payments);

            var attachmentsResponse = await controller.GetAttachments(payment.Id, CancellationToken.None);
            Assert.IsType<NotFoundResult>(attachmentsResponse.Result);

            var contentResponse = await controller.GetAttachmentContent(attachment.Id, CancellationToken.None);
            Assert.IsType<NotFoundResult>(contentResponse);
        }
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

        var controller = CreatePaymentsController(dbContext, currentUser);

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
    public async Task CustomersController_DoesNotExposeContractContent_WhenParentCustomerIsNotReadable()
    {
        var adminUser = CreateAdminUser();
        await using (var seedDb = CreateDbContext(adminUser))
        {
            seedDb.DataSharingPolicies.Add(new DataSharingPolicy
            {
                Id = Guid.NewGuid(),
                SourceTenantCode = TenantScopeCatalog.UsenetGroup,
                SourceOfficeCode = OfficeCodeCatalog.Usenet,
                TargetTenantCode = TenantScopeCatalog.UsenetGroup,
                TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
                ShareCustomers = false,
                ShareItems = false,
                ShareInvoices = false,
                SharePayments = false,
                ShareContracts = true,
                ShareReports = false,
                ShareRentals = false,
                ShareDeliveries = false,
                AllowTargetWrite = false,
                IsActive = true
            });

            var customer = new Customer
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Hidden customer contract",
                NameMatchKey = "HIDDENCUSTOMERCONTRACT",
                TradeType = "Sales"
            };
            var content = TestPdfBytes("hidden customer contract content");
            var contract = new CustomerContract
            {
                Id = Guid.NewGuid(),
                CustomerId = customer.Id,
                ContractType = "거래계약서",
                FileName = "hidden-customer-contract.pdf",
                MimeType = "application/pdf",
                FileSize = content.LongLength,
                FileHash = "hidden-contract-hash",
                FileContent = content,
                UploadedAtUtc = new DateTime(2026, 6, 24, 1, 30, 0, DateTimeKind.Utc)
            };

            seedDb.Customers.Add(customer);
            seedDb.CustomerContracts.Add(contract);
            await seedDb.SaveChangesAsync();

            var scopedUser = new TestCurrentUserContext
            {
                Username = "yeonsu-contract-only-direct-reader",
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Yeonsu,
                ScopeType = TenantScopeCatalog.ScopeOfficeOnly
            };
            await using var scopedDb = CreateDbContext(scopedUser);
            var controller = new CustomersController(
                scopedDb,
                new OfficeScopeService(scopedUser, scopedDb),
                new StubCentralFileStorage());

            var contractsResponse = await controller.GetContracts(customer.Id, CancellationToken.None);
            Assert.IsType<NotFoundResult>(contractsResponse.Result);

            var contentResponse = await controller.DownloadContractContent(contract.Id, CancellationToken.None);
            Assert.IsType<NotFoundResult>(contentResponse);
        }
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

        var controller = new CompanyProfileController(dbContext, new OfficeScopeService(currentUser, dbContext));
        var response = await controller.Upsert(dto, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(response.Result);
    }

    [Fact]
    public async Task CompanyProfileController_ForbidsOutOfScopeOfficeProfileReadAndWrite()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "company-profile-editor-usenet",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = [PermissionNames.CompanyProfileEdit]
        };
        await using var dbContext = CreateDbContext(currentUser);

        var usenetProfile = new CompanyProfile
        {
            Id = Guid.NewGuid(),
            OfficeCode = OfficeCodeCatalog.Usenet,
            ProfileName = "USENET 기본",
            TradeName = "유즈넷",
            BankAccountText = "USENET-ACCOUNT",
            IsDefaultForOffice = true,
            IsActive = true
        };
        var yeonsuProfile = new CompanyProfile
        {
            Id = Guid.NewGuid(),
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ProfileName = "YEONSU 기본",
            TradeName = "연수",
            BankAccountText = "YEONSU-SECRET-ACCOUNT",
            IsDefaultForOffice = true,
            IsActive = true
        };
        dbContext.CompanyProfiles.AddRange(usenetProfile, yeonsuProfile);
        await dbContext.SaveChangesAsync();

        var controller = new CompanyProfileController(dbContext, new OfficeScopeService(currentUser, dbContext));

        var readResponse = await controller.Get(OfficeCodeCatalog.Yeonsu, CancellationToken.None);
        Assert.IsType<ForbidResult>(readResponse.Result);

        var writeDto = yeonsuProfile.ToDto();
        writeDto.ExpectedRevision = yeonsuProfile.Revision;
        writeDto.TradeName = "연수 수정 시도";
        var writeResponse = await controller.Upsert(writeDto, CancellationToken.None);

        Assert.IsType<ForbidResult>(writeResponse.Result);
        var persistedYeonsu = await dbContext.CompanyProfiles.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(profile => profile.Id == yeonsuProfile.Id);
        Assert.Equal("연수", persistedYeonsu.TradeName);

        var ownReadResponse = await controller.Get(OfficeCodeCatalog.Usenet, CancellationToken.None);
        var ownReadOk = Assert.IsType<OkObjectResult>(ownReadResponse.Result);
        var ownReadDto = Assert.IsType<CompanyProfileDto>(ownReadOk.Value);
        Assert.Equal(usenetProfile.Id, ownReadDto.Id);
    }

    [Fact]
    public async Task CompanyProfileController_Get_DoesNotFallbackToOtherOfficeProfile()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);

        var usenetProfile = new CompanyProfile
        {
            Id = Guid.NewGuid(),
            OfficeCode = OfficeCodeCatalog.Usenet,
            ProfileName = "USENET default",
            TradeName = "USENET",
            BankAccountText = "USENET-ONLY-ACCOUNT",
            StampImage = [1, 2, 3],
            IsDefaultForOffice = true,
            IsActive = true
        };
        dbContext.CompanyProfiles.Add(usenetProfile);
        await dbContext.SaveChangesAsync();

        var controller = new CompanyProfileController(dbContext, new OfficeScopeService(currentUser, dbContext));

        var missingOfficeResponse = await controller.Get(OfficeCodeCatalog.Yeonsu, CancellationToken.None);

        Assert.IsType<NotFoundResult>(missingOfficeResponse.Result);
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
            new RentalAssignmentHistoryService(dbContext),
            new RentalSettlementRecalculationService(dbContext));

    private static PaymentsController CreatePaymentsController(AppDbContext dbContext, TestCurrentUserContext currentUser)
        => new(
            dbContext,
            new OfficeScopeService(currentUser, dbContext),
            new StubCentralFileStorage(),
            new RentalSettlementRecalculationService(dbContext));

    private static RentalDirectPaymentScenario SeedRentalDirectPaymentScenario(
        AppDbContext dbContext,
        decimal? existingPaymentAmount = null,
        decimal storedSettledAmount = 0m)
    {
        var customerId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var billedAmount = 100_000m;
        var storedOutstandingAmount = Math.Max(0m, billedAmount - storedSettledAmount);
        var storedSettlementStatus = storedSettledAmount <= 0m
            ? "확인대기"
            : storedSettledAmount < billedAmount
                ? "부분입금"
                : "입금확인";

        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Direct rental payment customer",
            NameMatchKey = "DIRECTRENTALPAYMENTCUSTOMER",
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
            CustomerName = "Direct rental payment customer",
            BillingStatus = storedOutstandingAmount <= 0m ? "완료" : "청구중",
            SettlementStatus = storedSettlementStatus,
            CompletionStatus = storedOutstandingAmount <= 0m ? "완료" : "미완료",
            MonthlyAmount = billedAmount,
            SettledAmount = storedSettledAmount,
            OutstandingAmount = storedOutstandingAmount,
            BillingRunsJson = JsonSerializer.Serialize(new[]
            {
                new ServerRentalBillingRunSnapshot
                {
                    RunId = runId,
                    RunKey = "2026-07",
                    ScheduledDate = new DateOnly(2026, 7, 25),
                    PeriodStartDate = new DateOnly(2026, 7, 1),
                    PeriodEndDate = new DateOnly(2026, 7, 31),
                    PeriodLabel = "2026-07",
                    Status = storedOutstandingAmount <= 0m ? "완료" : "청구중",
                    BilledAmount = billedAmount,
                    SettledAmount = storedSettledAmount,
                    SettlementStatus = storedSettlementStatus,
                    SettledDate = storedSettledAmount > 0m ? new DateOnly(2026, 7, 26) : null
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
            InvoiceNumber = "RENTAL-DIRECT-PAY-001",
            VersionGroupId = invoiceId,
            VersionNumber = 1,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 7, 25),
            TotalAmount = billedAmount,
            SupplyAmount = 90_909m,
            VatAmount = 9_091m,
            LinkedRentalBillingProfileId = profileId,
            LinkedRentalBillingRunId = runId
        });
        dbContext.InvoiceLines.Add(new InvoiceLine
        {
            Id = Guid.NewGuid(),
            InvoiceId = invoiceId,
            ItemNameOriginal = "Rental billing direct payment item",
            Unit = "EA",
            Quantity = 1m,
            UnitPrice = billedAmount,
            LineAmount = billedAmount
        });

        if (existingPaymentAmount.HasValue)
        {
            dbContext.Payments.Add(new Payment
            {
                Id = paymentId,
                InvoiceId = invoiceId,
                PaymentDate = new DateOnly(2026, 7, 26),
                Amount = existingPaymentAmount.Value,
                Note = "seeded direct rental payment"
            });
        }

        return new RentalDirectPaymentScenario(customerId, profileId, runId, invoiceId, paymentId);
    }

    private static OutOfScopeRentalLinkedInvoiceScenario SeedOutOfScopeRentalLinkedInvoiceScenario(
        AppDbContext dbContext,
        decimal? existingPaymentAmount = null,
        decimal storedSettledAmount = 0m)
    {
        var customerId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var billedAmount = 100_000m;
        var storedOutstandingAmount = Math.Max(0m, billedAmount - storedSettledAmount);

        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Direct out-of-scope rental customer",
            NameMatchKey = "DIRECTOUTOFSCOPERENTALCUSTOMER",
            TradeType = "Sales"
        });
        dbContext.RentalBillingProfiles.Add(new RentalBillingProfile
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            ManagementCompanyCode = OfficeCodeCatalog.Yeonsu,
            ProfileKey = $"profile-out-of-scope-{profileId:N}",
            CustomerId = customerId,
            CustomerName = "Direct out-of-scope rental customer",
            BillingStatus = storedOutstandingAmount <= 0m ? "completed" : "in-progress",
            SettlementStatus = storedSettledAmount <= 0m ? "pending" : storedOutstandingAmount > 0m ? "partial" : "settled",
            CompletionStatus = storedOutstandingAmount <= 0m ? "completed" : "incomplete",
            MonthlyAmount = billedAmount,
            SettledAmount = storedSettledAmount,
            OutstandingAmount = storedOutstandingAmount,
            BillingRunsJson = JsonSerializer.Serialize(new[]
            {
                new ServerRentalBillingRunSnapshot
                {
                    RunId = runId,
                    RunKey = "2026-08",
                    ScheduledDate = new DateOnly(2026, 8, 25),
                    PeriodStartDate = new DateOnly(2026, 8, 1),
                    PeriodEndDate = new DateOnly(2026, 8, 31),
                    PeriodLabel = "2026-08",
                    Status = storedOutstandingAmount <= 0m ? "completed" : "in-progress",
                    BilledAmount = billedAmount,
                    SettledAmount = storedSettledAmount,
                    SettlementStatus = storedSettledAmount <= 0m ? "pending" : storedOutstandingAmount > 0m ? "partial" : "settled",
                    SettledDate = storedSettledAmount > 0m ? new DateOnly(2026, 8, 26) : null
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
            InvoiceNumber = "RENTAL-OUT-OF-SCOPE-DIRECT-001",
            VersionGroupId = invoiceId,
            VersionNumber = 1,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 8, 25),
            TotalAmount = billedAmount,
            SupplyAmount = 90_909m,
            VatAmount = 9_091m,
            LinkedRentalBillingProfileId = profileId,
            LinkedRentalBillingRunId = runId
        });
        dbContext.InvoiceLines.Add(new InvoiceLine
        {
            Id = Guid.NewGuid(),
            InvoiceId = invoiceId,
            ItemNameOriginal = "Out-of-scope rental billing item",
            Unit = "EA",
            Quantity = 1m,
            UnitPrice = billedAmount,
            LineAmount = billedAmount
        });

        if (existingPaymentAmount.HasValue)
        {
            dbContext.Payments.Add(new Payment
            {
                Id = paymentId,
                InvoiceId = invoiceId,
                PaymentDate = new DateOnly(2026, 8, 26),
                Amount = existingPaymentAmount.Value,
                Note = "seeded out-of-scope rental payment"
            });
        }

        return new OutOfScopeRentalLinkedInvoiceScenario(customerId, profileId, runId, invoiceId, paymentId);
    }

    private static LinkedTransactionOutOfScopeRentalScenario SeedPaymentWithOutOfScopeLinkedTransactionRentalProfileScenario(
        AppDbContext dbContext)
    {
        var customerId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var billedAmount = 100_000m;
        var settledAmount = 40_000m;

        dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Payment linked transaction hidden rental customer",
            NameMatchKey = "PAYMENTLINKEDTRANSACTIONHIDDENRENTALCUSTOMER",
            TradeType = "Sales"
        });
        dbContext.RentalBillingProfiles.Add(new RentalBillingProfile
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            ManagementCompanyCode = OfficeCodeCatalog.Yeonsu,
            ProfileKey = $"linked-transaction-hidden-profile-{profileId:N}",
            CustomerId = customerId,
            CustomerName = "Payment linked transaction hidden rental customer",
            BillingStatus = "in-progress",
            SettlementStatus = "partial",
            CompletionStatus = "incomplete",
            MonthlyAmount = billedAmount,
            SettledAmount = settledAmount,
            OutstandingAmount = billedAmount - settledAmount,
            BillingRunsJson = JsonSerializer.Serialize(new[]
            {
                new ServerRentalBillingRunSnapshot
                {
                    RunId = runId,
                    RunKey = "2026-09",
                    ScheduledDate = new DateOnly(2026, 9, 25),
                    PeriodStartDate = new DateOnly(2026, 9, 1),
                    PeriodEndDate = new DateOnly(2026, 9, 30),
                    PeriodLabel = "2026-09",
                    Status = "in-progress",
                    BilledAmount = billedAmount,
                    SettledAmount = settledAmount,
                    SettlementStatus = "partial",
                    SettledDate = new DateOnly(2026, 9, 26)
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
            InvoiceNumber = "PAY-LINKED-TX-HIDDEN-RENTAL",
            VersionGroupId = invoiceId,
            VersionNumber = 1,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 9, 25),
            TotalAmount = billedAmount,
            SupplyAmount = 90_909m,
            VatAmount = 9_091m
        });
        dbContext.Payments.Add(new Payment
        {
            Id = paymentId,
            InvoiceId = invoiceId,
            PaymentDate = new DateOnly(2026, 9, 26),
            Amount = settledAmount,
            Note = "seeded linked transaction payment"
        });
        dbContext.Transactions.Add(new TransactionRecord
        {
            Id = paymentId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 9, 26),
            TransactionKind = "legacy linked rental receipt",
            LinkedInvoiceId = invoiceId,
            LinkedInvoiceNumber = "PAY-LINKED-TX-HIDDEN-RENTAL",
            LinkedRentalBillingProfileId = profileId,
            LinkedRentalBillingRunId = runId,
            BankReceipt = settledAmount,
            ReceiptTotal = settledAmount,
            SettlementAmount = settledAmount,
            Note = "legacy linked transaction with hidden rental profile"
        });

        return new LinkedTransactionOutOfScopeRentalScenario(customerId, profileId, runId, invoiceId, paymentId);
    }

    private static async Task AssertRentalSettlementAsync(
        AppDbContext dbContext,
        Guid profileId,
        Guid runId,
        decimal expectedSettled,
        decimal expectedOutstanding,
        DateOnly? expectedSettledDate = null)
    {
        var profile = await dbContext.RentalBillingProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == profileId);
        var expectedSettlementStatus = expectedSettled <= 0m
            ? "확인대기"
            : expectedOutstanding > 0m
                ? "부분입금"
                : "입금확인";

        Assert.Equal(expectedSettled, profile.SettledAmount);
        Assert.Equal(expectedOutstanding, profile.OutstandingAmount);
        Assert.Equal(expectedOutstanding <= 0m ? "완료" : "미완료", profile.CompletionStatus);
        Assert.Equal(expectedSettlementStatus, profile.SettlementStatus);

        var run = Assert.Single(JsonSerializer.Deserialize<List<ServerRentalBillingRunSnapshot>>(profile.BillingRunsJson) ?? [], current => current.RunId == runId);
        Assert.Equal(100_000m, run.BilledAmount);
        Assert.Equal(expectedSettled, run.SettledAmount);
        Assert.Equal(expectedSettlementStatus, run.SettlementStatus);
        Assert.Equal(expectedOutstanding <= 0m ? "완료" : "청구중", run.Status);
        if (expectedSettled <= 0m)
            Assert.Null(run.SettledDate);
        else
            Assert.Equal(expectedSettledDate ?? new DateOnly(2026, 7, 26), run.SettledDate);
    }

    private static async Task AssertOutOfScopeRentalSettlementUnchangedAsync(
        AppDbContext dbContext,
        Guid profileId,
        decimal settledAmount,
        decimal outstandingAmount)
    {
        var profile = await dbContext.RentalBillingProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == profileId);

        Assert.Equal(settledAmount, profile.SettledAmount);
        Assert.Equal(outstandingAmount, profile.OutstandingAmount);
        var run = Assert.Single(JsonSerializer.Deserialize<List<ServerRentalBillingRunSnapshot>>(profile.BillingRunsJson) ?? []);
        Assert.Equal(settledAmount, run.SettledAmount);
    }

    private sealed record RentalDirectPaymentScenario(
        Guid CustomerId,
        Guid ProfileId,
        Guid RunId,
        Guid InvoiceId,
        Guid PaymentId);

    private sealed record OutOfScopeRentalLinkedInvoiceScenario(
        Guid CustomerId,
        Guid ProfileId,
        Guid RunId,
        Guid InvoiceId,
        Guid PaymentId);

    private sealed record LinkedTransactionOutOfScopeRentalScenario(
        Guid CustomerId,
        Guid ProfileId,
        Guid RunId,
        Guid InvoiceId,
        Guid PaymentId);

    private static IFormFile CreateFormFile(string fileName, string contentType, string content)
        => CreateFormFile(fileName, contentType, System.Text.Encoding.UTF8.GetBytes(content));

    private static IFormFile CreateFormFile(string fileName, string contentType, byte[] bytes)
    {
        var stream = new MemoryStream(bytes);
        return new FormFile(stream, 0, bytes.Length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType
        };
    }

    private static byte[] TestPdfBytes(string marker)
        => System.Text.Encoding.UTF8.GetBytes($"%PDF-1.4\n% {marker}\n1 0 obj\n<<>>\nendobj\n%%EOF\n");

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

    private static TestCurrentUserContext CreateOfficePaymentEditor()
        => new()
        {
            Username = "payment-editor-usenet",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = new[] { PermissionNames.PaymentEdit }
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
