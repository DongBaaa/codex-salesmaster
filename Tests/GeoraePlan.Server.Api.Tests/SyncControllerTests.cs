using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class SyncControllerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _dbContext;
    private readonly SyncController _controller;

    public SyncControllerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var currentUser = new TestCurrentUserContext
        {
            Username = "admin",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin,
            IsAdmin = true
        };

        var revisionClock = new RevisionClock();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _dbContext = new AppDbContext(options, currentUser, revisionClock);
        _dbContext.Database.EnsureCreated();

        var officeScopeService = new OfficeScopeService(currentUser, _dbContext);
        _controller = new SyncController(
            _dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            officeScopeService,
            new StubCentralFileStorage(),
            revisionClock);
    }

    [Fact]
    public async Task Push_AssignsDistinctRentalIdentifiers_ForMultipleNewAssetsInSingleBatch()
    {
        var registeredAtUtc = new DateTime(2026, 3, 24, 0, 0, 0, DateTimeKind.Utc);

        var request = new SyncPushRequest
        {
            RentalAssets =
            [
                new RentalAssetDto
                {
                    OfficeCode = OfficeCodeCatalog.Itworld,
                    TenantCode = TenantScopeCatalog.Itworld,
                    ManagementCompanyCode = OfficeCodeCatalog.Itworld,
                    CurrentLocation = "렌탈",
                    CustomerName = "테스트 거래처 A",
                    ItemName = "MODEL-A",
                    CreatedAtUtc = registeredAtUtc,
                    UpdatedAtUtc = registeredAtUtc
                },
                new RentalAssetDto
                {
                    OfficeCode = OfficeCodeCatalog.Itworld,
                    TenantCode = TenantScopeCatalog.Itworld,
                    ManagementCompanyCode = OfficeCodeCatalog.Itworld,
                    CurrentLocation = "렌탈",
                    CustomerName = "테스트 거래처 B",
                    ItemName = "MODEL-B",
                    CreatedAtUtc = registeredAtUtc,
                    UpdatedAtUtc = registeredAtUtc
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.Equal(0, result.ConflictCount);

        var assets = await _dbContext.RentalAssets
            .IgnoreQueryFilters()
            .OrderBy(asset => asset.ManagementId)
            .ToListAsync();

        Assert.Equal(2, assets.Count);
        Assert.Equal(2, assets.Select(asset => asset.ManagementId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(2, assets.Select(asset => asset.ManagementNumber).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(["1", "2"], assets.Select(asset => asset.ManagementId).OrderBy(value => int.Parse(value)).ToArray());
        Assert.Equal(["2603-001", "2603-002"], assets.Select(asset => asset.ManagementNumber).OrderBy(value => value).ToArray());
    }

    [Fact]
    public async Task Push_AllowsScopedItemUpdate_ForSameOfficeNonAdmin()
    {
        var scopedUser = new TestCurrentUserContext
        {
            Username = "itworld_user",
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ScopeType = TenantScopeCatalog.ScopeTenantAll,
            IsAdmin = false
        };

        await using var scopedDb = CreateDbContext(scopedUser);
        var existing = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            NameOriginal = "SYNC-ASSET",
            NameMatchKey = "SYNCASSET",
            ItemKind = ItemKinds.Asset,
            TrackingType = ItemTrackingTypes.Asset,
            UpdatedAtUtc = new DateTime(2026, 3, 24, 0, 0, 0, DateTimeKind.Utc)
        };
        scopedDb.Items.Add(existing);
        await scopedDb.SaveChangesAsync();

        var controller = CreateController(scopedDb, scopedUser);
        var request = new SyncPushRequest
        {
            Items =
            [
                new ItemDto
                {
                    Id = existing.Id,
                    TenantCode = TenantScopeCatalog.Itworld,
                    OfficeCode = OfficeCodeCatalog.Itworld,
                    NameOriginal = existing.NameOriginal,
                    NameMatchKey = existing.NameMatchKey,
                    CategoryName = "A3컬러복합기",
                    ItemKind = ItemKinds.Asset,
                    TrackingType = ItemTrackingTypes.Asset,
                    Notes = "updated",
                    CreatedAtUtc = existing.CreatedAtUtc,
                    UpdatedAtUtc = existing.UpdatedAtUtc.AddMinutes(1)
                }
            ]
        };

        var response = await controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.Equal(0, result.ConflictCount);

        var updated = await scopedDb.Items.IgnoreQueryFilters().FirstAsync(item => item.Id == existing.Id);
        Assert.Equal("updated", updated.Notes);
    }

    [Fact]
    public async Task Push_AllowsScopedCustomerUpdate_ForSameOfficeNonAdmin()
    {
        var scopedUser = new TestCurrentUserContext
        {
            Username = "itworld_user",
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ScopeType = TenantScopeCatalog.ScopeTenantAll,
            IsAdmin = false
        };

        await using var scopedDb = CreateDbContext(scopedUser);
        var existing = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            NameOriginal = "SYNC-CUSTOMER",
            NameMatchKey = "SYNCCUSTOMER",
            TradeType = "매출",
            UpdatedAtUtc = new DateTime(2026, 3, 24, 0, 0, 0, DateTimeKind.Utc)
        };
        scopedDb.Customers.Add(existing);
        await scopedDb.SaveChangesAsync();

        var controller = CreateController(scopedDb, scopedUser);
        var request = new SyncPushRequest
        {
            Customers =
            [
                new CustomerDto
                {
                    Id = existing.Id,
                    TenantCode = TenantScopeCatalog.Itworld,
                    OfficeCode = OfficeCodeCatalog.Itworld,
                    NameOriginal = existing.NameOriginal,
                    NameMatchKey = existing.NameMatchKey,
                    TradeType = "매출",
                    Notes = "updated",
                    CreatedAtUtc = existing.CreatedAtUtc,
                    UpdatedAtUtc = existing.UpdatedAtUtc.AddMinutes(1)
                }
            ]
        };

        var response = await controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.Equal(0, result.ConflictCount);

        var updated = await scopedDb.Customers.IgnoreQueryFilters().FirstAsync(customer => customer.Id == existing.Id);
        Assert.Equal("updated", updated.Notes);
    }

    [Fact]
    public async Task Push_ResolvesRentalAssetCustomerReference_ByReadableCustomerName()
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            NameOriginal = "SYNC-RENTAL-CUSTOMER",
            NameMatchKey = "SYNCRENTALCUSTOMER",
            TradeType = "매출"
        };
        _dbContext.Customers.Add(customer);
        await _dbContext.SaveChangesAsync();

        var request = new SyncPushRequest
        {
            RentalAssets =
            [
                new RentalAssetDto
                {
                    Id = Guid.NewGuid(),
                    OfficeCode = OfficeCodeCatalog.Itworld,
                    TenantCode = TenantScopeCatalog.Itworld,
                    ManagementCompanyCode = OfficeCodeCatalog.Itworld,
                    CustomerId = Guid.NewGuid(),
                    CustomerName = customer.NameOriginal,
                    ItemName = "MODEL-C",
                    CurrentLocation = "창고",
                    CreatedAtUtc = new DateTime(2026, 3, 25, 0, 0, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2026, 3, 25, 0, 0, 0, DateTimeKind.Utc)
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.Equal(0, result.ConflictCount);

        var asset = await _dbContext.RentalAssets.IgnoreQueryFilters().FirstAsync();
        Assert.Equal(customer.Id, asset.CustomerId);
    }

    [Fact]
    public async Task Push_ClearsRentalAssetCustomerReference_WhenCustomerCannotBeResolved()
    {
        var request = new SyncPushRequest
        {
            RentalAssets =
            [
                new RentalAssetDto
                {
                    Id = Guid.NewGuid(),
                    OfficeCode = OfficeCodeCatalog.Itworld,
                    TenantCode = TenantScopeCatalog.Itworld,
                    ManagementCompanyCode = OfficeCodeCatalog.Itworld,
                    CustomerId = Guid.NewGuid(),
                    CustomerName = "UNKNOWN-RENTAL-CUSTOMER",
                    ItemName = "MODEL-D",
                    CurrentLocation = "창고",
                    CreatedAtUtc = new DateTime(2026, 3, 25, 0, 0, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2026, 3, 25, 0, 0, 0, DateTimeKind.Utc)
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.Equal(0, result.ConflictCount);

        var asset = await _dbContext.RentalAssets.IgnoreQueryFilters()
            .OrderByDescending(current => current.CreatedAtUtc)
            .FirstAsync();
        Assert.Null(asset.CustomerId);
        Assert.Equal("UNKNOWN-RENTAL-CUSTOMER", asset.CustomerName);
    }

    [Fact]
    public async Task Push_AllowsCrossTenantRentalAssetUpdate_ForUserWithRentalEditAll()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "itworld_user",
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ScopeType = TenantScopeCatalog.ScopeTenantAll,
            Permissions = [거래플랜.Server.Api.Security.PermissionNames.RentalEditAll]
        };

        await using var scopedDb = CreateDbContext(currentUser);
        var existing = new RentalAsset
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            AssetKey = "USENET|2603-001|CUSTOMER|MODEL",
            ManagementId = "1",
            ManagementNumber = "2603-001",
            CustomerName = "유즈넷 거래처",
            ItemName = "MODEL-X",
            CurrentLocation = "유즈넷",
            CreatedAtUtc = new DateTime(2026, 3, 27, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 3, 27, 0, 0, 0, DateTimeKind.Utc)
        };
        scopedDb.RentalAssets.Add(existing);
        await scopedDb.SaveChangesAsync();

        var controller = CreateController(scopedDb, currentUser);
        var request = new SyncPushRequest
        {
            RentalAssets =
            [
                new RentalAssetDto
                {
                    Id = existing.Id,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    AssetKey = existing.AssetKey,
                    ManagementId = existing.ManagementId,
                    ManagementNumber = existing.ManagementNumber,
                    CustomerName = existing.CustomerName,
                    ItemName = existing.ItemName,
                    CurrentLocation = "아이티월드에서 수정",
                    CreatedAtUtc = existing.CreatedAtUtc,
                    UpdatedAtUtc = existing.UpdatedAtUtc.AddMinutes(1)
                }
            ]
        };

        var response = await controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.Equal(0, result.ConflictCount);

        var updated = await scopedDb.RentalAssets.IgnoreQueryFilters().FirstAsync(asset => asset.Id == existing.Id);
        Assert.Equal("아이티월드에서 수정", updated.CurrentLocation);
        Assert.Equal(TenantScopeCatalog.UsenetGroup, updated.TenantCode);
        Assert.Equal(OfficeCodeCatalog.Usenet, updated.OfficeCode);
    }

    [Fact]
    public async Task Pull_IncludesCrossTenantDeliveryData_ForUserWithDeliveryViewAll()
    {
        var usenetCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "유즈넷 납품처",
            NameMatchKey = "유즈넷납품처",
            TradeType = "매출"
        };
        var itworldCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            NameOriginal = "아이티월드 납품처",
            NameMatchKey = "아이티월드납품처",
            TradeType = "매출"
        };
        _dbContext.Customers.AddRange(usenetCustomer, itworldCustomer);

        _dbContext.Invoices.AddRange(
            new Invoice
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                CustomerId = usenetCustomer.Id,
                VoucherType = VoucherType.Sales,
                InvoiceNumber = "US-DEL-1",
                InvoiceDate = new DateOnly(2026, 3, 27)
            },
            new Invoice
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                CustomerId = itworldCustomer.Id,
                VoucherType = VoucherType.Sales,
                InvoiceNumber = "IT-DEL-1",
                InvoiceDate = new DateOnly(2026, 3, 27)
            });
        await _dbContext.SaveChangesAsync();

        var currentUser = new TestCurrentUserContext
        {
            Username = "itworld_user",
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ScopeType = TenantScopeCatalog.ScopeTenantAll,
            Permissions = [거래플랜.Server.Api.Security.PermissionNames.DeliveryViewAll]
        };
        await using var scopedDb = CreateDbContext(currentUser);
        var controller = CreateController(scopedDb, currentUser);

        var response = await controller.Pull(0, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPullResponse>(ok.Value);

        Assert.Contains(result.Customers, customer => customer.Id == usenetCustomer.Id);
        Assert.Contains(result.Customers, customer => customer.Id == itworldCustomer.Id);
        Assert.Contains(result.Invoices, invoice => invoice.InvoiceNumber == "US-DEL-1");
        Assert.Contains(result.Invoices, invoice => invoice.InvoiceNumber == "IT-DEL-1");
    }

    [Fact]
    public async Task Push_ResolvesRentalAssetItemReference_ByReadableItemMetadata()
    {
        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            NameOriginal = "SYNC-RENTAL-ITEM",
            NameMatchKey = "SYNCRENTALITEM",
            MaterialNumber = "2603-123",
            SerialNumber = "SN-123",
            ItemKind = ItemKinds.Asset,
            TrackingType = ItemTrackingTypes.Asset
        };
        _dbContext.Items.Add(item);
        await _dbContext.SaveChangesAsync();

        var request = new SyncPushRequest
        {
            RentalAssets =
            [
                new RentalAssetDto
                {
                    Id = Guid.NewGuid(),
                    OfficeCode = OfficeCodeCatalog.Itworld,
                    TenantCode = TenantScopeCatalog.Itworld,
                    ManagementCompanyCode = OfficeCodeCatalog.Itworld,
                    ItemId = Guid.NewGuid(),
                    ItemName = item.NameOriginal,
                    ManagementNumber = item.MaterialNumber,
                    MachineNumber = item.SerialNumber,
                    CurrentLocation = "창고",
                    CreatedAtUtc = new DateTime(2026, 3, 25, 0, 0, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2026, 3, 25, 0, 0, 0, DateTimeKind.Utc)
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.Equal(0, result.ConflictCount);

        var asset = await _dbContext.RentalAssets.IgnoreQueryFilters()
            .OrderByDescending(current => current.CreatedAtUtc)
            .FirstAsync();
        Assert.Equal(item.Id, asset.ItemId);
    }

    [Fact]
    public async Task Push_ClearsRentalAssetItemReference_WhenItemCannotBeResolved()
    {
        var request = new SyncPushRequest
        {
            RentalAssets =
            [
                new RentalAssetDto
                {
                    Id = Guid.NewGuid(),
                    OfficeCode = OfficeCodeCatalog.Itworld,
                    TenantCode = TenantScopeCatalog.Itworld,
                    ManagementCompanyCode = OfficeCodeCatalog.Itworld,
                    ItemId = Guid.NewGuid(),
                    ItemName = "UNKNOWN-RENTAL-ITEM",
                    ManagementNumber = "2603-999",
                    MachineNumber = "SN-999",
                    CurrentLocation = "창고",
                    CreatedAtUtc = new DateTime(2026, 3, 25, 0, 0, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2026, 3, 25, 0, 0, 0, DateTimeKind.Utc)
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.Equal(0, result.ConflictCount);

        var asset = await _dbContext.RentalAssets.IgnoreQueryFilters()
            .OrderByDescending(current => current.CreatedAtUtc)
            .FirstAsync();
        Assert.Null(asset.ItemId);
        Assert.Equal("UNKNOWN-RENTAL-ITEM", asset.ItemName);
    }

    [Fact]
    public async Task Push_IgnoresDeletedCustomerContract_WhenServerContractIsAlreadyMissing()
    {
        var missingContractId = Guid.NewGuid();

        var request = new SyncPushRequest
        {
            CustomerContracts =
            [
                new CustomerContractDto
                {
                    Id = missingContractId,
                    CustomerId = Guid.NewGuid(),
                    IsDeleted = true,
                    UpdatedAtUtc = new DateTime(2026, 3, 25, 0, 0, 0, DateTimeKind.Utc)
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(0, result.ConflictCount);
        Assert.Empty(await _dbContext.CustomerContracts.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Push_NormalizesCustomerContractUploadedAtUtc_WhenKindIsUnspecified()
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            NameOriginal = "CONTRACT-CUSTOMER",
            NameMatchKey = "CONTRACTCUSTOMER",
            TradeType = "매출"
        };
        _dbContext.Customers.Add(customer);
        await _dbContext.SaveChangesAsync();

        var uploadedAt = new DateTime(2026, 3, 25, 17, 5, 0, DateTimeKind.Unspecified);
        var request = new SyncPushRequest
        {
            CustomerContracts =
            [
                new CustomerContractDto
                {
                    Id = Guid.NewGuid(),
                    CustomerId = customer.Id,
                    ContractType = "거래계약서",
                    FileName = "contract.pdf",
                    MimeType = "application/pdf",
                    FileSize = 4,
                    FileHash = "HASH",
                    FileContent = [1, 2, 3, 4],
                    UploadedAtUtc = uploadedAt,
                    CreatedAtUtc = new DateTime(2026, 3, 25, 17, 0, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2026, 3, 25, 17, 6, 0, DateTimeKind.Utc)
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(0, result.ConflictCount);

        var contract = await _dbContext.CustomerContracts.IgnoreQueryFilters().FirstAsync();
        Assert.Equal(uploadedAt, contract.UploadedAtUtc);
        Assert.Equal(DateTimeKind.Utc, contract.UploadedAtUtc.Kind);
    }

    [Fact]
    public async Task Push_AllowsTransactionReferencingInvoiceCreatedInSameBatch()
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "BATCH-TX-CUSTOMER",
            NameMatchKey = "BATCHTXCUSTOMER",
            TradeType = "매출"
        };
        _dbContext.Customers.Add(customer);
        await _dbContext.SaveChangesAsync();

        var invoiceId = Guid.NewGuid();
        var request = new SyncPushRequest
        {
            Invoices =
            [
                new InvoiceDto
                {
                    Id = invoiceId,
                    CustomerId = customer.Id,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    VoucherType = VoucherType.Purchase,
                    InvoiceDate = new DateOnly(2026, 3, 26),
                    CreatedAtUtc = new DateTime(2026, 3, 26, 0, 0, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2026, 3, 26, 0, 0, 0, DateTimeKind.Utc)
                }
            ],
            Transactions =
            [
                new TransactionDto
                {
                    Id = Guid.NewGuid(),
                    CustomerId = customer.Id,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    TransactionDate = new DateOnly(2026, 3, 26),
                    TransactionKind = "전표지급",
                    LinkedInvoiceId = invoiceId,
                    LinkedInvoiceNumber = "L202603-0099",
                    CashPayment = 1000m,
                    PaymentTotal = 1000m,
                    SettlementAmount = 1000m,
                    CreatedAtUtc = new DateTime(2026, 3, 26, 0, 0, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2026, 3, 26, 0, 0, 0, DateTimeKind.Utc)
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(0, result.ConflictCount);

        var storedTransaction = await _dbContext.Transactions.IgnoreQueryFilters().FirstAsync();
        Assert.Equal(invoiceId, storedTransaction.LinkedInvoiceId);
    }

    [Fact]
    public async Task Push_ClearsTransactionInvoiceReference_WhenLinkedInvoiceIsMissing()
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "MISSING-INVOICE-CUSTOMER",
            NameMatchKey = "MISSINGINVOICECUSTOMER",
            TradeType = "매출"
        };
        _dbContext.Customers.Add(customer);
        await _dbContext.SaveChangesAsync();

        var request = new SyncPushRequest
        {
            Transactions =
            [
                new TransactionDto
                {
                    Id = Guid.NewGuid(),
                    CustomerId = customer.Id,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    TransactionDate = new DateOnly(2026, 3, 26),
                    TransactionKind = "전표지급",
                    LinkedInvoiceId = Guid.NewGuid(),
                    LinkedInvoiceNumber = "L202603-0100",
                    BankPayment = 2000m,
                    PaymentTotal = 2000m,
                    SettlementAmount = 2000m,
                    CreatedAtUtc = new DateTime(2026, 3, 26, 0, 0, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2026, 3, 26, 0, 0, 0, DateTimeKind.Utc)
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(0, result.ConflictCount);

        var storedTransaction = await _dbContext.Transactions.IgnoreQueryFilters().FirstAsync();
        Assert.Null(storedTransaction.LinkedInvoiceId);
        Assert.Equal("일반지급", storedTransaction.TransactionKind);
        Assert.Equal("L202603-0100", storedTransaction.LinkedInvoiceNumber);
        Assert.Equal(0m, storedTransaction.SettlementAmount);
    }

    [Fact]
    public async Task Push_ResolvesTransactionCustomerReference_FromLinkedInvoiceCustomer()
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "DERIVED-TX-CUSTOMER",
            NameMatchKey = "DERIVEDTXCUSTOMER",
            TradeType = "매출"
        };
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            VoucherType = VoucherType.Purchase,
            InvoiceDate = new DateOnly(2026, 3, 26)
        };
        _dbContext.Customers.Add(customer);
        _dbContext.Invoices.Add(invoice);
        await _dbContext.SaveChangesAsync();

        var request = new SyncPushRequest
        {
            Transactions =
            [
                new TransactionDto
                {
                    Id = Guid.NewGuid(),
                    CustomerId = Guid.NewGuid(),
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    TransactionDate = new DateOnly(2026, 3, 26),
                    TransactionKind = "전표지급",
                    LinkedInvoiceId = invoice.Id,
                    LinkedInvoiceNumber = "L202603-0101",
                    CashPayment = 3000m,
                    PaymentTotal = 3000m,
                    SettlementAmount = 3000m,
                    CreatedAtUtc = new DateTime(2026, 3, 26, 0, 0, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2026, 3, 26, 0, 0, 0, DateTimeKind.Utc)
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(0, result.ConflictCount);

        var storedTransaction = await _dbContext.Transactions.IgnoreQueryFilters().FirstAsync();
        Assert.Equal(customer.Id, storedTransaction.CustomerId);
    }

    [Fact]
    public async Task Push_AllowsPaymentReferencingInvoiceCreatedInSameBatch()
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "BATCH-PAYMENT-CUSTOMER",
            NameMatchKey = "BATCHPAYMENTCUSTOMER",
            TradeType = "매출"
        };
        _dbContext.Customers.Add(customer);
        await _dbContext.SaveChangesAsync();

        var invoiceId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var request = new SyncPushRequest
        {
            Invoices =
            [
                new InvoiceDto
                {
                    Id = invoiceId,
                    CustomerId = customer.Id,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    VoucherType = VoucherType.Purchase,
                    InvoiceDate = new DateOnly(2026, 3, 26),
                    CreatedAtUtc = new DateTime(2026, 3, 26, 0, 0, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2026, 3, 26, 0, 0, 0, DateTimeKind.Utc)
                }
            ],
            Payments =
            [
                new PaymentDto
                {
                    Id = paymentId,
                    InvoiceId = invoiceId,
                    PaymentDate = new DateOnly(2026, 3, 26),
                    Amount = 5000m,
                    Note = "same-batch payment",
                    CreatedAtUtc = new DateTime(2026, 3, 26, 0, 0, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2026, 3, 26, 0, 0, 0, DateTimeKind.Utc)
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(0, result.ConflictCount);

        var storedPayment = await _dbContext.Payments.IgnoreQueryFilters().FirstAsync();
        Assert.Equal(invoiceId, storedPayment.InvoiceId);
        Assert.False(storedPayment.IsDeleted);
    }

    [Fact]
    public async Task Push_SkipsDeletedPayment_WhenInvoiceIsAlreadyMissing()
    {
        var request = new SyncPushRequest
        {
            Payments =
            [
                new PaymentDto
                {
                    Id = Guid.NewGuid(),
                    InvoiceId = Guid.NewGuid(),
                    PaymentDate = new DateOnly(2026, 3, 26),
                    Amount = 8000m,
                    Note = "stale delete",
                    IsDeleted = true,
                    CreatedAtUtc = new DateTime(2026, 3, 26, 0, 0, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2026, 3, 26, 0, 0, 0, DateTimeKind.Utc)
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(0, result.ConflictCount);
        Assert.Empty(await _dbContext.Payments.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Push_DeletesExistingPayment_WhenInvoiceReferenceIsMissing()
    {
        var paymentId = Guid.NewGuid();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "ORPHAN-PAYMENT-CUSTOMER",
            NameMatchKey = "ORPHANPAYMENTCUSTOMER",
            TradeType = "매출"
        };
        var existingInvoiceId = Guid.NewGuid();
        _dbContext.Customers.Add(customer);
        _dbContext.Invoices.Add(new Invoice
        {
            Id = existingInvoiceId,
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "T-20260326-001",
            LocalTempNumber = "TMP-20260326-001",
            InvoiceDate = new DateOnly(2026, 3, 25),
            VoucherType = VoucherType.Sales,
            TotalAmount = 8000m,
            SupplyAmount = 7273m,
            VatAmount = 727m
        });
        _dbContext.Payments.Add(new Payment
        {
            Id = paymentId,
            InvoiceId = existingInvoiceId,
            PaymentDate = new DateOnly(2026, 3, 25),
            Amount = 8000m,
            Note = "old payment",
            CreatedAtUtc = new DateTime(2026, 3, 25, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 3, 25, 0, 0, 0, DateTimeKind.Utc)
        });
        await _dbContext.SaveChangesAsync();

        var request = new SyncPushRequest
        {
            Payments =
            [
                new PaymentDto
                {
                    Id = paymentId,
                    InvoiceId = Guid.NewGuid(),
                    PaymentDate = new DateOnly(2026, 3, 26),
                    Amount = 8000m,
                    Note = "missing invoice payment",
                    CreatedAtUtc = DateTime.UtcNow.AddMinutes(1),
                    UpdatedAtUtc = DateTime.UtcNow.AddMinutes(1)
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(0, result.ConflictCount);

        var storedPayment = await _dbContext.Payments.IgnoreQueryFilters().FirstAsync();
        Assert.True(storedPayment.IsDeleted);
    }

    [Fact]
    public async Task Push_ResolvesInvoiceCustomerReference_FromExistingInvoiceCustomer_WhenIncomingCustomerIsMissing()
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "EXISTING-INVOICE-CUSTOMER",
            NameMatchKey = "EXISTINGINVOICECUSTOMER",
            TradeType = "매출"
        };
        var invoiceId = Guid.NewGuid();
        _dbContext.Customers.Add(customer);
        _dbContext.Invoices.Add(new Invoice
        {
            Id = invoiceId,
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "S-20260326-001",
            LocalTempNumber = "TMP-S-001",
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 3, 26),
            TotalAmount = 1000m,
            SupplyAmount = 909m,
            VatAmount = 91m,
            CreatedAtUtc = new DateTime(2026, 3, 26, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 3, 26, 0, 0, 0, DateTimeKind.Utc)
        });
        await _dbContext.SaveChangesAsync();
        var existingInvoice = await _dbContext.Invoices.IgnoreQueryFilters().FirstAsync(x => x.Id == invoiceId);
        var incomingCreatedAt = (existingInvoice.CreatedAtUtc == default
            ? existingInvoice.UpdatedAtUtc
            : existingInvoice.CreatedAtUtc).AddMinutes(1);
        var incomingUpdatedAt = existingInvoice.UpdatedAtUtc.AddMinutes(1);

        var request = new SyncPushRequest
        {
            Invoices =
            [
                new InvoiceDto
                {
                    Id = invoiceId,
                    CustomerId = Guid.NewGuid(),
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    VoucherType = VoucherType.Sales,
                    InvoiceDate = new DateOnly(2026, 3, 27),
                    Memo = "customer repaired from existing invoice",
                    CreatedAtUtc = incomingCreatedAt,
                    UpdatedAtUtc = incomingUpdatedAt
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(0, result.ConflictCount);

        var storedInvoice = await _dbContext.Invoices.IgnoreQueryFilters().FirstAsync(x => x.Id == invoiceId);
        Assert.Equal(customer.Id, storedInvoice.CustomerId);
        Assert.Equal("customer repaired from existing invoice", storedInvoice.Memo);
    }

    [Fact]
    public async Task Push_ReturnsConflict_WhenInvoiceServerVersionIsNewer()
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "STALE-INVOICE-CUSTOMER",
            NameMatchKey = "STALEINVOICECUSTOMER",
            TradeType = "매출"
        };
        var invoiceId = Guid.NewGuid();
        _dbContext.Customers.Add(customer);
        _dbContext.Invoices.Add(new Invoice
        {
            Id = invoiceId,
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "S-20260327-001",
            LocalTempNumber = "TMP-S-STALE-001",
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 3, 27),
            Memo = "server invoice",
            TotalAmount = 1200m,
            SupplyAmount = 1091m,
            VatAmount = 109m,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });
        await _dbContext.SaveChangesAsync();

        var staleRequest = new SyncPushRequest
        {
            Invoices =
            [
                new InvoiceDto
                {
                    Id = invoiceId,
                    CustomerId = customer.Id,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    VoucherType = VoucherType.Sales,
                    InvoiceDate = new DateOnly(2026, 3, 27),
                    Memo = "stale client invoice",
                    CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5),
                    UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-5)
                }
            ]
        };

        var response = await _controller.Push(staleRequest, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(1, result.ConflictCount);
        Assert.Single(result.Conflicts);

        var storedInvoice = await _dbContext.Invoices.IgnoreQueryFilters().FirstAsync(x => x.Id == invoiceId);
        Assert.Equal("server invoice", storedInvoice.Memo);
    }

    [Fact]
    public async Task Push_ResolvesInvoiceCustomerReference_FromCustomerName_WhenCustomerIdIsMissing()
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "NAME-RECOVERY-CUSTOMER",
            NameMatchKey = "NAMERECOVERYCUSTOMER",
            TradeType = "매출"
        };
        _dbContext.Customers.Add(customer);
        await _dbContext.SaveChangesAsync();

        var invoiceId = Guid.NewGuid();
        var request = new SyncPushRequest
        {
            Invoices =
            [
                new InvoiceDto
                {
                    Id = invoiceId,
                    CustomerId = Guid.NewGuid(),
                    CustomerName = customer.NameOriginal,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    VoucherType = VoucherType.Purchase,
                    InvoiceDate = new DateOnly(2026, 3, 26),
                    TotalAmount = 2200m,
                    SupplyAmount = 2000m,
                    VatAmount = 200m,
                    CreatedAtUtc = new DateTime(2026, 3, 26, 0, 0, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2026, 3, 26, 0, 0, 0, DateTimeKind.Utc)
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(0, result.ConflictCount);

        var storedInvoice = await _dbContext.Invoices.IgnoreQueryFilters().FirstAsync(x => x.Id == invoiceId);
        Assert.Equal(customer.Id, storedInvoice.CustomerId);
    }

    [Fact]
    public async Task Push_SkipsDeletedTransactionAttachment_WhenTransactionIsAlreadyMissing()
    {
        var request = new SyncPushRequest
        {
            TransactionAttachments =
            [
                new TransactionAttachmentDto
                {
                    Id = Guid.NewGuid(),
                    TransactionId = Guid.NewGuid(),
                    IsDeleted = true,
                    CreatedAtUtc = new DateTime(2026, 3, 26, 0, 0, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2026, 3, 26, 0, 0, 0, DateTimeKind.Utc)
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(0, result.ConflictCount);
        Assert.Empty(await _dbContext.TransactionAttachments.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Push_DeletesExistingTransactionAttachment_WhenIncomingTransactionReferenceIsMissing()
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "ATTACHMENT-CUSTOMER",
            NameMatchKey = "ATTACHMENTCUSTOMER",
            TradeType = "매출"
        };
        var transaction = new TransactionRecord
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 3, 25),
            TransactionKind = "일반수금",
            CashReceipt = 1000m,
            ReceiptTotal = 1000m,
            SettlementAmount = 1000m,
            CreatedAtUtc = new DateTime(2026, 3, 25, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 3, 25, 0, 0, 0, DateTimeKind.Utc)
        };
        var attachmentId = Guid.NewGuid();
        _dbContext.Customers.Add(customer);
        _dbContext.Transactions.Add(transaction);
        _dbContext.TransactionAttachments.Add(new TransactionAttachment
        {
            Id = attachmentId,
            TransactionId = transaction.Id,
            AttachmentType = "기타",
            FileName = "receipt.pdf",
            MimeType = "application/pdf",
            FileSize = 10,
            FileHash = "hash",
            Description = "existing attachment",
            UploadedByUsername = "admin",
            UploadedAtUtc = new DateTime(2026, 3, 25, 0, 0, 0, DateTimeKind.Utc),
            CreatedAtUtc = new DateTime(2026, 3, 25, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 3, 25, 0, 0, 0, DateTimeKind.Utc)
        });
        await _dbContext.SaveChangesAsync();
        var existingAttachment = await _dbContext.TransactionAttachments.IgnoreQueryFilters().FirstAsync(x => x.Id == attachmentId);
        var attachmentCreatedAt = (existingAttachment.CreatedAtUtc == default
            ? existingAttachment.UpdatedAtUtc
            : existingAttachment.CreatedAtUtc).AddMinutes(1);
        var attachmentUpdatedAt = existingAttachment.UpdatedAtUtc.AddMinutes(1);

        var request = new SyncPushRequest
        {
            TransactionAttachments =
            [
                new TransactionAttachmentDto
                {
                    Id = attachmentId,
                    TransactionId = Guid.NewGuid(),
                    FileName = "receipt.pdf",
                    MimeType = "application/pdf",
                    AttachmentType = "기타",
                    FileContent = [],
                    CreatedAtUtc = attachmentCreatedAt,
                    UpdatedAtUtc = attachmentUpdatedAt
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(0, result.ConflictCount);
        await using var verificationDb = CreateDbContext(new TestCurrentUserContext
        {
            Username = "admin",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin,
            IsAdmin = true
        });
        var storedAttachment = await verificationDb.TransactionAttachments
            .IgnoreQueryFilters()
            .FirstAsync(x => x.Id == attachmentId);
        Assert.Equal(transaction.Id, storedAttachment.TransactionId);
        Assert.True(storedAttachment.IsDeleted);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }

    private AppDbContext CreateDbContext(TestCurrentUserContext currentUser)
    {
        var revisionClock = new RevisionClock();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        var dbContext = new AppDbContext(options, currentUser, revisionClock);
        dbContext.Database.EnsureCreated();
        return dbContext;
    }

    private static SyncController CreateController(AppDbContext dbContext, TestCurrentUserContext currentUser)
    {
        var revisionClock = new RevisionClock();
        var officeScopeService = new OfficeScopeService(currentUser, dbContext);
        return new SyncController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            officeScopeService,
            new StubCentralFileStorage(),
            revisionClock);
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
        public IReadOnlyCollection<string> Permissions { get; init; } = [];

        public bool HasPermission(string permission)
            => IsAdmin || IsGodMode || Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);
    }

    private sealed class StubInvoiceNumberService : IInvoiceNumberService
    {
        public Task<string> GenerateAsync(Guid customerId, DateOnly invoiceDate, CancellationToken cancellationToken = default)
            => Task.FromResult($"{invoiceDate:yyyyMM}-0001");
    }

    private sealed class StubCentralFileStorage : ICentralFileStorage
    {
        public string RootPath => Path.GetTempPath();

        public Task<string> SaveBytesAsync(string area, string ownerId, Guid fileId, string fileName, byte[] content, CancellationToken cancellationToken = default)
            => Task.FromResult(Path.Combine(RootPath, fileName));

        public byte[] ReadBytes(string? storedPath, byte[]? fallback = null)
            => fallback ?? [];

        public void DeleteIfExists(string? storedPath)
        {
        }
    }
}
