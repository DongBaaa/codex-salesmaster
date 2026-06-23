using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text.Json;
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
            revisionClock,
            new InventoryLedgerService(_dbContext),
            new InvoiceStockSnapshotService(_dbContext, revisionClock),
            new RentalAssignmentHistoryService(_dbContext),
            new RentalSettlementRecalculationService(_dbContext));
    }


    [Fact]
    public async Task SuccessfulSyncEndpoints_ReturnTypedResponseBodies()
    {
        var statusResponse = _controller.GetStatus(CancellationToken.None);
        var statusOk = Assert.IsType<OkObjectResult>(statusResponse.Result);
        Assert.IsType<SyncStatusDto>(statusOk.Value);

        var waitResponse = await _controller.WaitForChange(-1, 1, CancellationToken.None);
        var waitOk = Assert.IsType<OkObjectResult>(waitResponse.Result);
        Assert.IsType<SyncStatusDto>(waitOk.Value);

        var pullResponse = await _controller.Pull(0, CancellationToken.None);
        var pullOk = Assert.IsType<OkObjectResult>(pullResponse.Result);
        Assert.IsType<SyncPullResponse>(pullOk.Value);

        var pushResponse = await _controller.Push(new SyncPushRequest
        {
            DeviceId = "device-success-body-contract"
        }, CancellationToken.None);
        var pushOk = Assert.IsType<OkObjectResult>(pushResponse.Result);
        Assert.IsType<SyncPushResult>(pushOk.Value);
    }

    [Fact]
    public async Task Push_IgnoresNullPayloadEntries_WithoutFailingOrBlockingValidRows()
    {
        var customerId = Guid.NewGuid();
        _dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "SYNC-NULL-PAYLOAD-CUSTOMER",
            NameMatchKey = "SYNCNULLPAYLOADCUSTOMER",
            TradeType = CustomerClassificationNormalizer.Sales
        });
        await _dbContext.SaveChangesAsync();

        var invoiceId = Guid.NewGuid();
        var response = await _controller.Push(new SyncPushRequest
        {
            DeviceId = "device-null-payload-entries",
            CompanyProfiles = [null!],
            Units = [null!],
            CustomerCategories = [null!],
            PriceGradeOptions = [null!],
            TradeTypeOptions = [null!],
            ItemCategoryOptions = [null!],
            CustomerMasters = [null!],
            Customers = [null!],
            CustomerContracts = [null!],
            Items = [null!],
            ItemWarehouseStocks = [null!],
            Transactions = [null!],
            TransactionAttachments = [null!],
            InventoryTransfers = [null!],
            RentalManagementCompanies = [null!],
            RentalBillingProfiles = [null!],
            RentalAssets = [null!],
            RentalAssetAssignmentHistories = [null!],
            RentalBillingLogs = [null!],
            Invoices =
            [
                null!,
                new InvoiceDto
                {
                    Id = invoiceId,
                    CustomerId = customerId,
                    CustomerName = "SYNC-NULL-PAYLOAD-CUSTOMER",
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Shared,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    VoucherType = VoucherType.Sales,
                    InvoiceDate = new DateOnly(2026, 6, 22),
                    TotalAmount = 0m,
                    SupplyAmount = 0m,
                    VatAmount = 0m,
                    UpdatedAtUtc = DateTime.UtcNow,
                    Lines = [null!],
                    Payments = [null!]
                }
            ],
            Payments = [null!]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.Equal(1, result.AcceptedCount);
        Assert.Equal(0, result.ConflictCount);

        var storedInvoice = await _dbContext.Invoices
            .Include(invoice => invoice.Lines)
            .IgnoreQueryFilters()
            .SingleAsync(invoice => invoice.Id == invoiceId);
        Assert.Empty(storedInvoice.Lines);
    }

    [Fact]
    public async Task Pull_ReturnsGlobalSettingPurgeRecords_ForDifferentTenantUser()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "itworld-settings-reader",
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly
        };
        await using var dbContext = CreateDbContext(currentUser);
        var optionId = Guid.NewGuid();
        dbContext.RecycleBinPurgeRecords.Add(new RecycleBinPurgeRecord
        {
            Id = Guid.NewGuid(),
            Kind = "item-category-option",
            EntityId = optionId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            Revision = 10,
            PurgedAtUtc = new DateTime(2026, 6, 23, 0, 0, 0, DateTimeKind.Utc)
        });
        await dbContext.SaveChangesAsync();
        var controller = CreateController(dbContext, currentUser);

        var response = await controller.Pull(0, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<SyncPullResponse>(ok.Value);
        var purge = Assert.Single(payload.PurgeRecords);
        Assert.Equal("item-category-option", purge.Kind);
        Assert.Equal(optionId, purge.EntityId);
    }

    [Fact]
    public async Task Pull_CompanyProfiles_ReturnsOnlyReadableOfficeProfiles()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "yeonsu-company-reader",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly
        };
        await using var dbContext = CreateDbContext(currentUser);
        var yeonsuProfileId = Guid.NewGuid();
        var usenetProfileId = Guid.NewGuid();
        dbContext.CompanyProfiles.AddRange(
            new CompanyProfile
            {
                Id = yeonsuProfileId,
                OfficeCode = OfficeCodeCatalog.Yeonsu,
                ProfileName = "연수 회사설정",
                TradeName = "연수",
                Revision = 10
            },
            new CompanyProfile
            {
                Id = usenetProfileId,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ProfileName = "유즈넷 회사설정",
                TradeName = "유즈넷",
                Revision = 11
            });
        await dbContext.SaveChangesAsync();
        var controller = CreateController(dbContext, currentUser);

        var response = await controller.Pull(0, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var payload = Assert.IsType<SyncPullResponse>(ok.Value);
        var profile = Assert.Single(payload.CompanyProfiles);
        Assert.Equal(yeonsuProfileId, profile.Id);
        Assert.Equal(OfficeCodeCatalog.Yeonsu, profile.OfficeCode);
    }

    [Fact]
    public async Task Pull_RemovesInvoiceLinesReferencingItemsOutsideReadableItemScope()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "usenet-invoice-line-reader",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly
        };

        await using var scopedDb = CreateDbContext(currentUser);
        var controller = CreateController(scopedDb, currentUser);
        var inScopeItemId = Guid.NewGuid();
        var outOfScopeItemId = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        scopedDb.Items.AddRange(
            new Item
            {
                Id = inScopeItemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "SYNC-PULL-IN-SCOPE-INVOICE-ITEM",
                NameMatchKey = "SYNCPULLINSCOPEINVOICEITEM"
            },
            new Item
            {
                Id = outOfScopeItemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Yeonsu,
                NameOriginal = "SYNC-PULL-OUT-SCOPE-INVOICE-ITEM",
                NameMatchKey = "SYNCPULLOUTSCOPEINVOICEITEM"
            });
        scopedDb.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "SYNC-PULL-INVOICE-LINE-CUSTOMER",
            NameMatchKey = "SYNCPULLINVOICELINECUSTOMER",
            TradeType = CustomerClassificationNormalizer.Sales
        });
        scopedDb.Invoices.Add(new Invoice
        {
            Id = invoiceId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 22),
            InvoiceNumber = "SYNC-PULL-LINE-SCOPE",
            Revision = 10,
            Lines =
            [
                new InvoiceLine
                {
                    Id = Guid.NewGuid(),
                    ItemId = inScopeItemId,
                    ItemNameOriginal = "SYNC-PULL-IN-SCOPE-INVOICE-ITEM",
                    Unit = "EA",
                    Quantity = 1m,
                    UnitPrice = 1000m,
                    LineAmount = 1000m,
                    OrderIndex = 1
                },
                new InvoiceLine
                {
                    Id = Guid.NewGuid(),
                    ItemId = outOfScopeItemId,
                    ItemNameOriginal = "SYNC-PULL-OUT-SCOPE-INVOICE-ITEM",
                    Unit = "EA",
                    Quantity = 1m,
                    UnitPrice = 2000m,
                    LineAmount = 2000m,
                    OrderIndex = 2
                }
            ]
        });
        await scopedDb.SaveChangesAsync();

        var response = await controller.Pull(0, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPullResponse>(ok.Value);

        Assert.Contains(result.Items, item => item.Id == inScopeItemId);
        Assert.DoesNotContain(result.Items, item => item.Id == outOfScopeItemId);
        var invoice = Assert.Single(result.Invoices, current => current.Id == invoiceId);
        Assert.Contains(invoice.Lines, line => line.ItemId == inScopeItemId);
        Assert.DoesNotContain(invoice.Lines, line => line.ItemId == outOfScopeItemId);
    }

    [Fact]
    public async Task Pull_RemovesInventoryTransferLinesReferencingItemsOutsideReadableItemScope()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "usenet-transfer-line-reader",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly
        };

        await using var scopedDb = CreateDbContext(currentUser);
        var controller = CreateController(scopedDb, currentUser);
        var inScopeItemId = Guid.NewGuid();
        var outOfScopeItemId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        scopedDb.Items.AddRange(
            new Item
            {
                Id = inScopeItemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "SYNC-PULL-IN-SCOPE-TRANSFER-ITEM",
                NameMatchKey = "SYNCPULLINSCOPETRANSFERITEM"
            },
            new Item
            {
                Id = outOfScopeItemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Yeonsu,
                NameOriginal = "SYNC-PULL-OUT-SCOPE-TRANSFER-ITEM",
                NameMatchKey = "SYNCPULLOUTSCOPETRANSFERITEM"
            });
        scopedDb.InventoryTransfers.Add(new InventoryTransfer
        {
            Id = transferId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Usenet,
            TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
            TransferNumber = "SYNC-PULL-TRANSFER-LINE-SCOPE",
            TransferDate = new DateOnly(2026, 6, 22),
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            TransferStatus = InventoryTransferStatusNormalizer.Pending,
            Revision = 10,
            Lines =
            [
                new InventoryTransferLine
                {
                    Id = Guid.NewGuid(),
                    ItemId = inScopeItemId,
                    ItemNameOriginal = "SYNC-PULL-IN-SCOPE-TRANSFER-ITEM",
                    Unit = "EA",
                    Quantity = 1m
                },
                new InventoryTransferLine
                {
                    Id = Guid.NewGuid(),
                    ItemId = outOfScopeItemId,
                    ItemNameOriginal = "SYNC-PULL-OUT-SCOPE-TRANSFER-ITEM",
                    Unit = "EA",
                    Quantity = 1m
                }
            ]
        });
        await scopedDb.SaveChangesAsync();

        var response = await controller.Pull(0, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPullResponse>(ok.Value);

        Assert.Contains(result.Items, item => item.Id == inScopeItemId);
        Assert.DoesNotContain(result.Items, item => item.Id == outOfScopeItemId);
        var transfer = Assert.Single(result.InventoryTransfers, current => current.Id == transferId);
        Assert.Contains(transfer.Lines, line => line.ItemId == inScopeItemId);
        Assert.DoesNotContain(transfer.Lines, line => line.ItemId == outOfScopeItemId);
    }

    [Fact]
    public async Task Push_CompanyProfiles_RejectsOutOfScopeOfficeProfileMutation()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "yeonsu-company-writer",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly
        };
        await using var dbContext = CreateDbContext(currentUser);
        var profileId = Guid.NewGuid();
        dbContext.CompanyProfiles.Add(new CompanyProfile
        {
            Id = profileId,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ProfileName = "유즈넷 회사설정",
            TradeName = "유즈넷",
            Revision = 5
        });
        await dbContext.SaveChangesAsync();
        var controller = CreateController(dbContext, currentUser);

        var response = await controller.Push(new SyncPushRequest
        {
            DeviceId = "device-company-profile-out-of-scope",
            CompanyProfiles =
            [
                new CompanyProfileDto
                {
                    Id = profileId,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ProfileName = "유즈넷 회사설정 변경",
                    TradeName = "유즈넷 변경",
                    ExpectedRevision = 5,
                    CreatedAtUtc = DateTime.UtcNow.AddMinutes(-1),
                    UpdatedAtUtc = DateTime.UtcNow
                }
            ]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            conflict.EntityName == nameof(CompanyProfile) &&
            conflict.Reason.Contains("company profile office scope", StringComparison.OrdinalIgnoreCase));

        var stored = await dbContext.CompanyProfiles.IgnoreQueryFilters().SingleAsync(profile => profile.Id == profileId);
        Assert.Equal("유즈넷", stored.TradeName);
    }

    [Fact]
    public async Task Push_ReturnsForbidden_WhenDomainPermissionMissing()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "limited-user",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = []
        };

        await using var scopedDb = CreateDbContext(currentUser);
        var controller = CreateController(scopedDb, currentUser);
        var itemId = Guid.NewGuid();
        var request = new SyncPushRequest
        {
            DeviceId = "device-permission-denied",
            Items =
            [
                new ItemDto
                {
                    Id = itemId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    NameOriginal = "Permissionless item",
                    NameMatchKey = "Permissionlessitem",
                    Unit = "EA",
                    CreatedAtUtc = DateTime.UtcNow.AddMinutes(-1),
                    UpdatedAtUtc = DateTime.UtcNow
                }
            ]
        };

        var response = await controller.Push(request, CancellationToken.None);
        var forbidden = Assert.IsType<ObjectResult>(response.Result);

        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        Assert.False(await scopedDb.Items.IgnoreQueryFilters().AnyAsync(item => item.Id == itemId));
    }

    [Fact]
    public async Task Push_ReturnsForbidden_ForEveryUnauthorizedCollection()
    {
        foreach (var testCase in CreateUnauthorizedPushCases())
        {
            var currentUser = new TestCurrentUserContext
            {
                Username = $"limited-{testCase.Name}",
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
                Permissions = []
            };

            await using var scopedDb = CreateDbContext(currentUser);
            var controller = CreateController(scopedDb, currentUser);
            var request = testCase.CreateRequest();
            request.DeviceId = $"device-permission-denied-{testCase.Name}";

            var response = await controller.Push(request, CancellationToken.None);
            var forbidden = Assert.IsType<ObjectResult>(response.Result);
            var message = forbidden.Value?.GetType().GetProperty("message")?.GetValue(forbidden.Value) as string;

            Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
            Assert.False(string.IsNullOrWhiteSpace(message));
            Assert.Contains(testCase.ExpectedLabel, message);
            Assert.DoesNotContain(scopedDb.ChangeTracker.Entries(),
                entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
        }
    }

    private static IReadOnlyList<UnauthorizedPushCase> CreateUnauthorizedPushCases()
    {
        return
        [
            new UnauthorizedPushCase(
                nameof(SyncPushRequest.CompanyProfiles),
                () => new SyncPushRequest { CompanyProfiles = [CreateSyncDto<CompanyProfileDto>()] },
                "회사설정"),
            new UnauthorizedPushCase(
                nameof(SyncPushRequest.Units),
                () => new SyncPushRequest { Units = [CreateSyncDto<UnitDto>()] },
                "환경설정/분류"),
            new UnauthorizedPushCase(
                nameof(SyncPushRequest.CustomerCategories),
                () => new SyncPushRequest { CustomerCategories = [CreateSyncDto<CustomerCategoryDto>()] },
                "환경설정/분류"),
            new UnauthorizedPushCase(
                nameof(SyncPushRequest.PriceGradeOptions),
                () => new SyncPushRequest { PriceGradeOptions = [CreateSyncDto<PriceGradeOptionDto>()] },
                "환경설정/분류"),
            new UnauthorizedPushCase(
                nameof(SyncPushRequest.TradeTypeOptions),
                () => new SyncPushRequest { TradeTypeOptions = [CreateSyncDto<TradeTypeOptionDto>()] },
                "환경설정/분류"),
            new UnauthorizedPushCase(
                nameof(SyncPushRequest.ItemCategoryOptions),
                () => new SyncPushRequest { ItemCategoryOptions = [CreateSyncDto<ItemCategoryOptionDto>()] },
                "환경설정/분류"),
            new UnauthorizedPushCase(
                nameof(SyncPushRequest.CustomerMasters),
                () => new SyncPushRequest { CustomerMasters = [CreateSyncDto<CustomerMasterDto>()] },
                "거래처"),
            new UnauthorizedPushCase(
                nameof(SyncPushRequest.Customers),
                () => new SyncPushRequest { Customers = [CreateSyncDto<CustomerDto>()] },
                "거래처"),
            new UnauthorizedPushCase(
                nameof(SyncPushRequest.CustomerContracts),
                () => new SyncPushRequest { CustomerContracts = [CreateSyncDto<CustomerContractDto>()] },
                "거래처"),
            new UnauthorizedPushCase(
                nameof(SyncPushRequest.Items),
                () => new SyncPushRequest { Items = [CreateSyncDto<ItemDto>()] },
                "품목/재고"),
            new UnauthorizedPushCase(
                nameof(SyncPushRequest.ItemWarehouseStocks),
                () => new SyncPushRequest { ItemWarehouseStocks = [CreateWarehouseStockDto()] },
                "품목/재고"),
            new UnauthorizedPushCase(
                nameof(SyncPushRequest.Invoices),
                () => new SyncPushRequest { Invoices = [CreateSyncDto<InvoiceDto>()] },
                "전표"),
            new UnauthorizedPushCase(
                nameof(SyncPushRequest.Transactions),
                () => new SyncPushRequest { Transactions = [CreateSyncDto<TransactionDto>()] },
                "수금/지급"),
            new UnauthorizedPushCase(
                nameof(SyncPushRequest.TransactionAttachments),
                () => new SyncPushRequest { TransactionAttachments = [CreateSyncDto<TransactionAttachmentDto>()] },
                "수금/지급"),
            new UnauthorizedPushCase(
                nameof(SyncPushRequest.Payments),
                () => new SyncPushRequest { Payments = [CreateSyncDto<PaymentDto>()] },
                "수금/지급"),
            new UnauthorizedPushCase(
                nameof(SyncPushRequest.InventoryTransfers),
                () => new SyncPushRequest { InventoryTransfers = [CreateSyncDto<InventoryTransferDto>()] },
                "납품/재고이동"),
            new UnauthorizedPushCase(
                nameof(SyncPushRequest.RentalManagementCompanies),
                () => new SyncPushRequest { RentalManagementCompanies = [CreateSyncDto<RentalManagementCompanyDto>()] },
                "렌탈 관리업체"),
            new UnauthorizedPushCase(
                nameof(SyncPushRequest.RentalBillingProfiles),
                () => new SyncPushRequest { RentalBillingProfiles = [CreateSyncDto<RentalBillingProfileDto>()] },
                "렌탈 청구"),
            new UnauthorizedPushCase(
                nameof(SyncPushRequest.RentalBillingLogs),
                () => new SyncPushRequest { RentalBillingLogs = [CreateSyncDto<RentalBillingLogDto>()] },
                "렌탈 청구"),
            new UnauthorizedPushCase(
                nameof(SyncPushRequest.RentalAssets),
                () => new SyncPushRequest { RentalAssets = [CreateSyncDto<RentalAssetDto>()] },
                "렌탈 자산"),
            new UnauthorizedPushCase(
                nameof(SyncPushRequest.RentalAssetAssignmentHistories),
                () => new SyncPushRequest { RentalAssetAssignmentHistories = [CreateSyncDto<RentalAssetAssignmentHistoryDto>()] },
                "렌탈 자산")
        ];
    }

    private static T CreateSyncDto<T>() where T : SyncEntityDto, new()
    {
        var now = DateTime.UtcNow;
        return new T
        {
            Id = Guid.NewGuid(),
            CreatedAtUtc = now.AddMinutes(-1),
            UpdatedAtUtc = now
        };
    }

    private static ItemWarehouseStockDto CreateWarehouseStockDto()
    {
        return new ItemWarehouseStockDto
        {
            ItemId = Guid.NewGuid(),
            WarehouseCode = OfficeCodeCatalog.Usenet,
            Quantity = 1,
            UpdatedAtUtc = DateTime.UtcNow
        };
    }

    private sealed record UnauthorizedPushCase(
        string Name,
        Func<SyncPushRequest> CreateRequest,
        string ExpectedLabel);

    [Fact]
    public async Task Push_AllowsDomainChanges_WhenRequiredPermissionExists()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "item-editor",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = [PermissionNames.ItemEdit]
        };

        await using var scopedDb = CreateDbContext(currentUser);
        var controller = CreateController(scopedDb, currentUser);
        var itemId = Guid.NewGuid();
        var request = new SyncPushRequest
        {
            DeviceId = "device-permission-allowed",
            Items =
            [
                new ItemDto
                {
                    Id = itemId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    NameOriginal = "Permitted item",
                    NameMatchKey = "Permitteditem",
                    Unit = "EA",
                    CreatedAtUtc = DateTime.UtcNow.AddMinutes(-1),
                    UpdatedAtUtc = DateTime.UtcNow
                }
            ]
        };

        var response = await controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(1, result.AcceptedCount);
        Assert.True(await scopedDb.Items.IgnoreQueryFilters().AnyAsync(item => item.Id == itemId));
    }

    [Fact]
    public async Task Push_AllowsTargetOfficeToUpdateSharedSourceCustomer_WhenSharingPolicyAllowsWrite()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "yeonsu-customer-editor",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = [PermissionNames.CustomerEdit]
        };

        await using var scopedDb = CreateDbContext(currentUser);
        scopedDb.DataSharingPolicies.Add(new DataSharingPolicy
        {
            Id = Guid.NewGuid(),
            SourceTenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Usenet,
            TargetTenantCode = TenantScopeCatalog.UsenetGroup,
            TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
            ShareCustomers = true,
            AllowTargetWrite = true,
            IsActive = true
        });

        var customerId = Guid.NewGuid();
        scopedDb.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "공유 원본 거래처",
            NameMatchKey = "공유원본거래처",
            TradeType = "매출"
        });
        await scopedDb.SaveChangesAsync();

        var existing = await scopedDb.Customers.IgnoreQueryFilters().SingleAsync(customer => customer.Id == customerId);
        var controller = CreateController(scopedDb, currentUser);
        var response = await controller.Push(new SyncPushRequest
        {
            DeviceId = "shared-write-customer-device",
            Customers =
            [
                new CustomerDto
                {
                    Id = customerId,
                    TenantCode = existing.TenantCode,
                    OfficeCode = existing.OfficeCode,
                    ResponsibleOfficeCode = existing.ResponsibleOfficeCode,
                    NameOriginal = "공유 원본 거래처 수정",
                    NameMatchKey = "공유원본거래처수정",
                    TradeType = existing.TradeType,
                    ExpectedRevision = existing.Revision,
                    UpdatedAtUtc = DateTime.UtcNow.AddMinutes(1),
                    MutationId = $"shared-write-customer-device:Customer:{customerId:N}:{existing.Revision}",
                    MutationCreatedAtUtc = DateTime.UtcNow
                }
            ]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(1, result.AcceptedCount);
        Assert.Equal(0, result.ConflictCount);
        Assert.Equal("공유 원본 거래처 수정", await scopedDb.Customers.IgnoreQueryFilters()
            .Where(customer => customer.Id == customerId)
            .Select(customer => customer.NameOriginal)
            .SingleAsync());
    }

    [Fact]
    public async Task Push_AllowsNegativeItemWarehouseStockSnapshotForShortageSales()
    {
        var itemId = Guid.NewGuid();
        _dbContext.Items.Add(new Item
        {
            Id = itemId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Negative guard item",
            NameMatchKey = "NEGATIVEGUARDITEM",
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 3m
        });
        _dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
        {
            ItemId = itemId,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 3m,
            Revision = 1
        });
        await _dbContext.SaveChangesAsync();

        var response = await _controller.Push(new SyncPushRequest
        {
            DeviceId = "negative-stock-device",
            ItemWarehouseStocks =
            [
                new ItemWarehouseStockDto
                {
                    ItemId = itemId,
                    WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                    Quantity = -1m,
                    Revision = 1
                }
            ]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(0, result.ConflictCount);
        Assert.Empty(result.Conflicts);
        Assert.Equal(-1m, await _dbContext.ItemWarehouseStocks
            .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
        Assert.Equal(-1m, (await _dbContext.Items.IgnoreQueryFilters().SingleAsync(item => item.Id == itemId)).CurrentStock);
    }

    [Fact]
    public async Task Push_DeduplicatesMutationId_ForCustomerUpdates()
    {
        var customerId = Guid.NewGuid();
        _dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "기존 거래처",
            NameMatchKey = "기존거래처",
            TradeType = "매출"
        });
        await _dbContext.SaveChangesAsync();

        var customer = await _dbContext.Customers.IgnoreQueryFilters().FirstAsync(x => x.Id == customerId);
        var mutationId = $"device-1:Customer:{customerId:N}:{customer.Revision}";
        var request = new SyncPushRequest
        {
            DeviceId = "device-1",
            Customers =
            [
                new CustomerDto
                {
                    Id = customerId,
                    TenantCode = customer.TenantCode,
                    OfficeCode = customer.OfficeCode,
                    ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
                    NameOriginal = "수정 거래처",
                    NameMatchKey = "수정거래처",
                    TradeType = customer.TradeType,
                    ExpectedRevision = customer.Revision,
                    Revision = customer.Revision,
                    MutationId = mutationId,
                    MutationCreatedAtUtc = customer.UpdatedAtUtc.AddMinutes(1),
                    CreatedAtUtc = customer.CreatedAtUtc,
                    UpdatedAtUtc = customer.UpdatedAtUtc.AddMinutes(1)
                }
            ]
        };

        var firstResponse = await _controller.Push(request, CancellationToken.None);
        var firstOk = Assert.IsType<OkObjectResult>(firstResponse.Result);
        var firstResult = Assert.IsType<SyncPushResult>(firstOk.Value);
        Assert.Equal(1, firstResult.AcceptedCount);
        Assert.Equal(0, firstResult.DuplicateMutationCount);

        _dbContext.ChangeTracker.Clear();

        var secondResponse = await _controller.Push(request, CancellationToken.None);
        var secondOk = Assert.IsType<OkObjectResult>(secondResponse.Result);
        var secondResult = Assert.IsType<SyncPushResult>(secondOk.Value);
        Assert.Equal(1, secondResult.AcceptedCount);
        Assert.Equal(1, secondResult.DuplicateMutationCount);
        Assert.Equal(0, secondResult.ConflictCount);

        var storedCustomer = await _dbContext.Customers.IgnoreQueryFilters().FirstAsync(x => x.Id == customerId);
        Assert.Equal("수정 거래처", storedCustomer.NameOriginal);
        Assert.Equal(1, await _dbContext.ProcessedSyncMutations.CountAsync());
    }

    [Fact]
    public async Task Push_ReturnsConflict_WhenCustomerExpectedRevisionDoesNotMatch()
    {
        var customerId = Guid.NewGuid();
        _dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "서버 거래처",
            NameMatchKey = "서버거래처",
            TradeType = "매출"
        });
        await _dbContext.SaveChangesAsync();

        var customer = await _dbContext.Customers.IgnoreQueryFilters().FirstAsync(x => x.Id == customerId);
        var request = new SyncPushRequest
        {
            DeviceId = "device-2",
            Customers =
            [
                new CustomerDto
                {
                    Id = customerId,
                    TenantCode = customer.TenantCode,
                    OfficeCode = customer.OfficeCode,
                    ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
                    NameOriginal = "클라이언트 거래처",
                    NameMatchKey = "클라이언트거래처",
                    TradeType = customer.TradeType,
                    ExpectedRevision = customer.Revision + 1,
                    Revision = customer.Revision,
                    MutationId = $"device-2:Customer:{customerId:N}:mismatch",
                    MutationCreatedAtUtc = customer.UpdatedAtUtc.AddMinutes(1),
                    CreatedAtUtc = customer.CreatedAtUtc,
                    UpdatedAtUtc = customer.UpdatedAtUtc.AddMinutes(1)
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict => conflict.Reason.Contains("Expected revision mismatch", StringComparison.Ordinal));

        var storedCustomer = await _dbContext.Customers.IgnoreQueryFilters().FirstAsync(x => x.Id == customerId);
        Assert.Equal("서버 거래처", storedCustomer.NameOriginal);
        Assert.Equal(0, await _dbContext.ProcessedSyncMutations.CountAsync());
    }


    [Fact]
    public async Task Push_DeduplicatesRepeatedOpenConflictLogs_ForSameEntityReasonAndPayload()
    {
        var customerId = Guid.NewGuid();
        _dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "CONFLICT-DEDUP-SERVER-CUSTOMER",
            NameMatchKey = "CONFLICTDEDUPSERVERCUSTOMER",
            TradeType = "매출"
        });
        await _dbContext.SaveChangesAsync();

        var customer = await _dbContext.Customers.IgnoreQueryFilters().FirstAsync(x => x.Id == customerId);
        var request = new SyncPushRequest
        {
            DeviceId = "device-conflict-dedup",
            Customers =
            [
                new CustomerDto
                {
                    Id = customerId,
                    TenantCode = customer.TenantCode,
                    OfficeCode = customer.OfficeCode,
                    ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
                    NameOriginal = "CONFLICT-DEDUP-CLIENT-CUSTOMER",
                    NameMatchKey = "CONFLICTDEDUPCLIENTCUSTOMER",
                    TradeType = customer.TradeType,
                    ExpectedRevision = customer.Revision + 10,
                    Revision = customer.Revision,
                    CreatedAtUtc = customer.CreatedAtUtc,
                    UpdatedAtUtc = customer.UpdatedAtUtc.AddMinutes(1)
                }
            ]
        };

        var firstResponse = await _controller.Push(request, CancellationToken.None);
        var firstOk = Assert.IsType<OkObjectResult>(firstResponse.Result);
        var firstResult = Assert.IsType<SyncPushResult>(firstOk.Value);
        Assert.Equal(1, firstResult.ConflictCount);

        var secondResponse = await _controller.Push(request, CancellationToken.None);
        var secondOk = Assert.IsType<OkObjectResult>(secondResponse.Result);
        var secondResult = Assert.IsType<SyncPushResult>(secondOk.Value);
        Assert.Equal(1, secondResult.ConflictCount);

        var openConflicts = await _dbContext.ConflictLogs
            .Where(conflict =>
                conflict.Status == "Open" &&
                conflict.EntityName == nameof(Customer) &&
                conflict.EntityId == customerId.ToString("D") &&
                conflict.Reason.Contains("Expected revision mismatch"))
            .ToListAsync();
        var conflict = Assert.Single(openConflicts);
        Assert.Equal("CONFLICT-DEDUP-CLIENT-CUSTOMER", JsonDocument.Parse(conflict.ClientJson).RootElement.GetProperty("NameOriginal").GetString());
    }

    [Fact]
    public async Task Push_PreservesYeonsuResponsibleOffice_ForUsenetTenantAllCustomerUpdate()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "usenet_user",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeTenantAll,
            IsAdmin = false
        };

        await using var scopedDb = CreateDbContext(currentUser);
        var controller = CreateController(scopedDb, currentUser);

        var customerId = Guid.NewGuid();
        scopedDb.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "담당지점 변경 테스트",
            NameMatchKey = "담당지점변경테스트",
            TradeType = "매출"
        });
        await scopedDb.SaveChangesAsync();

        var customer = await scopedDb.Customers.IgnoreQueryFilters().FirstAsync(x => x.Id == customerId);
        var request = new SyncPushRequest
        {
            DeviceId = "device-yeonsu-reassign",
            Customers =
            [
                new CustomerDto
                {
                    Id = customerId,
                    TenantCode = customer.TenantCode,
                    OfficeCode = customer.OfficeCode,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                    NameOriginal = customer.NameOriginal,
                    NameMatchKey = customer.NameMatchKey,
                    TradeType = customer.TradeType,
                    ExpectedRevision = customer.Revision,
                    Revision = customer.Revision,
                    MutationId = $"device-yeonsu-reassign:Customer:{customerId:N}:1",
                    MutationCreatedAtUtc = customer.UpdatedAtUtc.AddMinutes(1),
                    CreatedAtUtc = customer.CreatedAtUtc,
                    UpdatedAtUtc = customer.UpdatedAtUtc.AddMinutes(1)
                }
            ]
        };

        var response = await controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(0, result.ConflictCount);
        Assert.Equal(1, result.AcceptedCount);

        scopedDb.ChangeTracker.Clear();
        var storedCustomer = await scopedDb.Customers.IgnoreQueryFilters().FirstAsync(x => x.Id == customerId);
        Assert.Equal(OfficeCodeCatalog.Yeonsu, storedCustomer.ResponsibleOfficeCode);
        Assert.Equal(OfficeCodeCatalog.Usenet, storedCustomer.OfficeCode);
        Assert.Equal(TenantScopeCatalog.UsenetGroup, storedCustomer.TenantCode);
    }

    [Fact]
    public async Task Push_ResolvesHistoricalConflict_WhenSameCustomerLaterSyncsSuccessfully()
    {
        var customerId = Guid.NewGuid();
        _dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "서버 거래처",
            NameMatchKey = "서버거래처",
            TradeType = "매출"
        });
        await _dbContext.SaveChangesAsync();

        var customer = await _dbContext.Customers.IgnoreQueryFilters().FirstAsync(x => x.Id == customerId);
        var staleRequest = new SyncPushRequest
        {
            DeviceId = "device-3",
            Customers =
            [
                new CustomerDto
                {
                    Id = customerId,
                    TenantCode = customer.TenantCode,
                    OfficeCode = customer.OfficeCode,
                    ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
                    NameOriginal = "클라이언트 stale",
                    NameMatchKey = "클라이언트STALE",
                    TradeType = customer.TradeType,
                    ExpectedRevision = customer.Revision + 1,
                    Revision = customer.Revision,
                    MutationId = $"device-3:Customer:{customerId:N}:stale",
                    MutationCreatedAtUtc = customer.UpdatedAtUtc.AddMinutes(1),
                    CreatedAtUtc = customer.CreatedAtUtc,
                    UpdatedAtUtc = customer.UpdatedAtUtc.AddMinutes(1)
                }
            ]
        };

        var staleResponse = await _controller.Push(staleRequest, CancellationToken.None);
        var staleOk = Assert.IsType<OkObjectResult>(staleResponse.Result);
        var staleResult = Assert.IsType<SyncPushResult>(staleOk.Value);
        Assert.Equal(1, staleResult.ConflictCount);

        _dbContext.ChangeTracker.Clear();
        var openConflict = await _dbContext.ConflictLogs.SingleAsync(log => log.EntityName == nameof(Customer) && log.EntityId == customerId.ToString());
        Assert.Equal("Open", openConflict.Status);

        var refreshedCustomer = await _dbContext.Customers.IgnoreQueryFilters().FirstAsync(x => x.Id == customerId);
        var successRequest = new SyncPushRequest
        {
            DeviceId = "device-3",
            Customers =
            [
                new CustomerDto
                {
                    Id = customerId,
                    TenantCode = refreshedCustomer.TenantCode,
                    OfficeCode = refreshedCustomer.OfficeCode,
                    ResponsibleOfficeCode = refreshedCustomer.ResponsibleOfficeCode,
                    NameOriginal = "정상 반영 거래처",
                    NameMatchKey = "정상반영거래처",
                    TradeType = refreshedCustomer.TradeType,
                    ExpectedRevision = refreshedCustomer.Revision,
                    Revision = refreshedCustomer.Revision,
                    MutationId = $"device-3:Customer:{customerId:N}:success",
                    MutationCreatedAtUtc = refreshedCustomer.UpdatedAtUtc.AddMinutes(2),
                    CreatedAtUtc = refreshedCustomer.CreatedAtUtc,
                    UpdatedAtUtc = refreshedCustomer.UpdatedAtUtc.AddMinutes(2)
                }
            ]
        };

        var successResponse = await _controller.Push(successRequest, CancellationToken.None);
        var successOk = Assert.IsType<OkObjectResult>(successResponse.Result);
        var successResult = Assert.IsType<SyncPushResult>(successOk.Value);
        Assert.Equal(0, successResult.ConflictCount);
        Assert.Equal(1, successResult.AcceptedCount);

        _dbContext.ChangeTracker.Clear();
        var resolvedConflict = await _dbContext.ConflictLogs.SingleAsync(log => log.EntityName == nameof(Customer) && log.EntityId == customerId.ToString());
        Assert.Equal("Resolved", resolvedConflict.Status);
        Assert.NotNull(resolvedConflict.ResolvedAtUtc);
        Assert.Contains("자동 해결", resolvedConflict.ResolutionNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Push_RejectsCrossTenantInventoryTransferRoute()
    {
        var itemId = Guid.NewGuid();
        _dbContext.Items.Add(new Item
        {
            Id = itemId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            NameOriginal = "테스트 품목",
            NameMatchKey = "테스트품목",
            Unit = "개",
            ItemKind = ItemKinds.Product,
            TrackingType = ItemTrackingTypes.Stock
        });
        await _dbContext.SaveChangesAsync();

        var transferId = Guid.NewGuid();
        var request = new SyncPushRequest
        {
            DeviceId = "device-transfer",
            InventoryTransfers =
            [
                new InventoryTransferDto
                {
                    Id = transferId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    SourceOfficeCode = OfficeCodeCatalog.Usenet,
                    TargetOfficeCode = OfficeCodeCatalog.Itworld,
                    TransferNumber = "TR-TEST-001",
                    TransferDate = new DateOnly(2026, 4, 13),
                    FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                    ToWarehouseCode = OfficeCodeCatalog.ItworldMainWarehouse,
                    TransferStatus = "수령대기",
                    MutationId = $"device-transfer:InventoryTransfer:{transferId:N}:1",
                    MutationCreatedAtUtc = DateTime.UtcNow,
                    Lines =
                    [
                        new InventoryTransferLineDto
                        {
                            Id = Guid.NewGuid(),
                            TransferId = transferId,
                            ItemId = itemId,
                            ItemNameOriginal = "테스트 품목",
                            SpecificationOriginal = string.Empty,
                            Unit = "개",
                            Quantity = 1m,
                            ReceivedQuantity = 1m
                        }
                    ]
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict => conflict.Reason.Contains("같은 업체 내부 지점 간 이동", StringComparison.Ordinal));
        Assert.False(await _dbContext.InventoryTransfers.IgnoreQueryFilters().AnyAsync(x => x.Id == transferId));
    }

    [Fact]
    public async Task Push_RejectsInventoryTransfer_WhenSourceStockWouldBecomeNegative()
    {
        var itemId = Guid.NewGuid();
        _dbContext.Items.Add(new Item
        {
            Id = itemId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            NameOriginal = "출고 부족 품목",
            NameMatchKey = "출고부족품목",
            Unit = "개",
            ItemKind = ItemKinds.Product,
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 1m
        });
        _dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
        {
            ItemId = itemId,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 1m,
            UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            Revision = 10
        });
        await _dbContext.SaveChangesAsync();

        var transferId = Guid.NewGuid();
        var request = new SyncPushRequest
        {
            DeviceId = "device-transfer-shortage",
            InventoryTransfers =
            [
                new InventoryTransferDto
                {
                    Id = transferId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    SourceOfficeCode = OfficeCodeCatalog.Usenet,
                    TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
                    TransferNumber = "TR-SHORT-001",
                    TransferDate = new DateOnly(2026, 5, 28),
                    FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                    ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                    TransferStatus = InventoryTransferStatusNormalizer.Pending,
                    MutationId = $"device-transfer-shortage:InventoryTransfer:{transferId:N}:1",
                    MutationCreatedAtUtc = DateTime.UtcNow,
                    Lines =
                    [
                        new InventoryTransferLineDto
                        {
                            Id = Guid.NewGuid(),
                            TransferId = transferId,
                            ItemId = itemId,
                            ItemNameOriginal = "출고 부족 품목",
                            SpecificationOriginal = string.Empty,
                            Unit = "개",
                            Quantity = 2m
                        }
                    ]
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(1, result.ConflictCount);
        Assert.False(await _dbContext.InventoryTransfers.IgnoreQueryFilters().AnyAsync(x => x.Id == transferId));
        var stock = await _dbContext.ItemWarehouseStocks.SingleAsync(x => x.ItemId == itemId && x.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse);
        Assert.Equal(1m, stock.Quantity);
    }

    [Fact]
    public async Task Push_AppliesInventoryTransferStockSnapshots_WhenWarehouseStocksAreNotInPayload()
    {
        var itemId = Guid.NewGuid();
        _dbContext.Items.Add(new Item
        {
            Id = itemId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            NameOriginal = "이동 스냅샷 품목",
            NameMatchKey = "이동스냅샷품목",
            Unit = "개",
            ItemKind = ItemKinds.Product,
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 10m
        });
        _dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
        {
            ItemId = itemId,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 10m,
            UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            Revision = 10
        });
        await _dbContext.SaveChangesAsync();

        var transferId = Guid.NewGuid();
        var request = new SyncPushRequest
        {
            DeviceId = "device-transfer-stock-snapshot",
            InventoryTransfers =
            [
                new InventoryTransferDto
                {
                    Id = transferId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    SourceOfficeCode = OfficeCodeCatalog.Usenet,
                    TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
                    TransferNumber = "TR-SNAPSHOT-001",
                    TransferDate = new DateOnly(2026, 6, 17),
                    FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                    ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                    TransferStatus = InventoryTransferStatusNormalizer.Pending,
                    MutationId = $"device-transfer-stock-snapshot:InventoryTransfer:{transferId:N}:1",
                    MutationCreatedAtUtc = DateTime.UtcNow,
                    Lines =
                    [
                        new InventoryTransferLineDto
                        {
                            Id = Guid.NewGuid(),
                            TransferId = transferId,
                            ItemId = itemId,
                            ItemNameOriginal = "이동 스냅샷 품목",
                            SpecificationOriginal = string.Empty,
                            Unit = "개",
                            Quantity = 2m
                        }
                    ]
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(0, result.ConflictCount);
        Assert.Equal(1, result.AcceptedCount);
        _dbContext.ChangeTracker.Clear();
        Assert.Equal(8m, await _dbContext.ItemWarehouseStocks
            .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
        Assert.Equal(8m, await _dbContext.Items.IgnoreQueryFilters()
            .Where(item => item.Id == itemId)
            .Select(item => item.CurrentStock)
            .SingleAsync());
        Assert.True(await _dbContext.InventoryLedgerEntries.AnyAsync(entry => entry.SourceDocumentId == transferId));
    }

    [Fact]
    public async Task Push_AppliesReceivedInventoryTransferTargetStock_WhenWarehouseStocksAreNotInPayload()
    {
        var itemId = Guid.NewGuid();
        _dbContext.Items.Add(new Item
        {
            Id = itemId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            NameOriginal = "수령 이동 스냅샷 품목",
            NameMatchKey = "수령이동스냅샷품목",
            Unit = "개",
            ItemKind = ItemKinds.Product,
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 10m
        });
        _dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
        {
            ItemId = itemId,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 10m,
            UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            Revision = 10
        });
        await _dbContext.SaveChangesAsync();

        var transferId = Guid.NewGuid();
        var request = new SyncPushRequest
        {
            DeviceId = "device-transfer-received-stock-snapshot",
            InventoryTransfers =
            [
                new InventoryTransferDto
                {
                    Id = transferId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    SourceOfficeCode = OfficeCodeCatalog.Usenet,
                    TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
                    TransferNumber = "TR-SNAPSHOT-RECEIVED-001",
                    TransferDate = new DateOnly(2026, 6, 17),
                    FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                    ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                    TransferStatus = InventoryTransferStatusNormalizer.Received,
                    ReceivedByUsername = "admin",
                    ReceivedAtUtc = DateTime.UtcNow,
                    MutationId = $"device-transfer-received-stock-snapshot:InventoryTransfer:{transferId:N}:1",
                    MutationCreatedAtUtc = DateTime.UtcNow,
                    Lines =
                    [
                        new InventoryTransferLineDto
                        {
                            Id = Guid.NewGuid(),
                            TransferId = transferId,
                            ItemId = itemId,
                            ItemNameOriginal = "수령 이동 스냅샷 품목",
                            SpecificationOriginal = string.Empty,
                            Unit = "개",
                            Quantity = 2m,
                            ReceivedQuantity = 2m
                        }
                    ]
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(0, result.ConflictCount);
        Assert.Equal(1, result.AcceptedCount);
        _dbContext.ChangeTracker.Clear();
        Assert.Equal(8m, await _dbContext.ItemWarehouseStocks
            .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
        Assert.Equal(2m, await _dbContext.ItemWarehouseStocks
            .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.YeonsuMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
        Assert.Equal(10m, await _dbContext.Items.IgnoreQueryFilters()
            .Where(item => item.Id == itemId)
            .Select(item => item.CurrentStock)
            .SingleAsync());
        Assert.True(await _dbContext.InventoryLedgerEntries.AnyAsync(entry => entry.SourceDocumentId == transferId));
    }

    [Fact]
    public async Task Push_DoesNotDoubleApplyInventoryTransferStockSnapshots_WhenWarehouseStocksAreInPayload()
    {
        var itemId = Guid.NewGuid();
        _dbContext.Items.Add(new Item
        {
            Id = itemId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            NameOriginal = "이동 스냅샷 중복 방지 품목",
            NameMatchKey = "이동스냅샷중복방지품목",
            Unit = "개",
            ItemKind = ItemKinds.Product,
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 10m
        });
        _dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
        {
            ItemId = itemId,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 10m,
            UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            Revision = 10
        });
        await _dbContext.SaveChangesAsync();

        var transferId = Guid.NewGuid();
        var request = new SyncPushRequest
        {
            DeviceId = "device-transfer-stock-snapshot-with-client-stock",
            ItemWarehouseStocks =
            [
                new ItemWarehouseStockDto
                {
                    ItemId = itemId,
                    WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                    Quantity = 8m,
                    Revision = 10,
                    ExpectedRevision = 10,
                    UpdatedAtUtc = DateTime.UtcNow
                }
            ],
            InventoryTransfers =
            [
                new InventoryTransferDto
                {
                    Id = transferId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    SourceOfficeCode = OfficeCodeCatalog.Usenet,
                    TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
                    TransferNumber = "TR-SNAPSHOT-CLIENT-STOCK-001",
                    TransferDate = new DateOnly(2026, 6, 17),
                    FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                    ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                    TransferStatus = InventoryTransferStatusNormalizer.Pending,
                    MutationId = $"device-transfer-stock-snapshot-with-client-stock:InventoryTransfer:{transferId:N}:1",
                    MutationCreatedAtUtc = DateTime.UtcNow,
                    Lines =
                    [
                        new InventoryTransferLineDto
                        {
                            Id = Guid.NewGuid(),
                            TransferId = transferId,
                            ItemId = itemId,
                            ItemNameOriginal = "이동 스냅샷 중복 방지 품목",
                            SpecificationOriginal = string.Empty,
                            Unit = "개",
                            Quantity = 2m
                        }
                    ]
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(0, result.ConflictCount);
        _dbContext.ChangeTracker.Clear();
        Assert.Equal(8m, await _dbContext.ItemWarehouseStocks
            .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
        Assert.Equal(8m, await _dbContext.Items.IgnoreQueryFilters()
            .Where(item => item.Id == itemId)
            .Select(item => item.CurrentStock)
            .SingleAsync());
    }

    [Fact]
    public async Task Push_RejectsInventoryTransferLineWithOutOfScopeItem()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "delivery-usenet",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = [PermissionNames.DeliveryEdit]
        };

        var outOfScopeItemId = Guid.NewGuid();
        _dbContext.Items.Add(new Item
        {
            Id = outOfScopeItemId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            NameOriginal = "Out of scope transfer item",
            NameMatchKey = "OUTOFSCOPETRANSFERITEM",
            Unit = "EA",
            ItemKind = ItemKinds.Product,
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 10m
        });
        _dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
        {
            ItemId = outOfScopeItemId,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 10m,
            UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            Revision = 10
        });
        await _dbContext.SaveChangesAsync();

        await using var scopedDb = CreateDbContext(currentUser);
        var controller = CreateController(scopedDb, currentUser);
        var transferId = Guid.NewGuid();
        var response = await controller.Push(new SyncPushRequest
        {
            DeviceId = "device-transfer-out-of-scope-item",
            InventoryTransfers =
            [
                new InventoryTransferDto
                {
                    Id = transferId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    SourceOfficeCode = OfficeCodeCatalog.Usenet,
                    TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
                    TransferNumber = "TR-SCOPE-ITEM-001",
                    TransferDate = new DateOnly(2026, 6, 17),
                    FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                    ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                    TransferStatus = InventoryTransferStatusNormalizer.Pending,
                    MutationId = $"device-transfer-out-of-scope-item:InventoryTransfer:{transferId:N}:1",
                    MutationCreatedAtUtc = DateTime.UtcNow,
                    Lines =
                    [
                        new InventoryTransferLineDto
                        {
                            Id = Guid.NewGuid(),
                            TransferId = transferId,
                            ItemId = outOfScopeItemId,
                            ItemNameOriginal = "Out of scope transfer item",
                            SpecificationOriginal = string.Empty,
                            Unit = "EA",
                            Quantity = 1m
                        }
                    ]
                }
            ]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            string.Equals(conflict.EntityName, nameof(InventoryTransfer), StringComparison.Ordinal) &&
            conflict.Reason.Contains("Referenced item is outside the readable office scope", StringComparison.Ordinal));
        scopedDb.ChangeTracker.Clear();
        Assert.False(await scopedDb.InventoryTransfers.IgnoreQueryFilters().AnyAsync(x => x.Id == transferId));
        Assert.False(await scopedDb.InventoryLedgerEntries.AnyAsync(entry => entry.SourceDocumentId == transferId));
        Assert.Equal(10m, await scopedDb.Items.IgnoreQueryFilters()
            .Where(item => item.Id == outOfScopeItemId)
            .Select(item => item.CurrentStock)
            .SingleAsync());
    }

    [Fact]
    public async Task Push_AcceptsRejectedInventoryTransfer_WithoutInventoryLedgerRows()
    {
        var itemId = Guid.NewGuid();
        _dbContext.Items.Add(new Item
        {
            Id = itemId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            NameOriginal = "반려 이동 품목",
            NameMatchKey = "반려이동품목",
            Unit = "개",
            ItemKind = ItemKinds.Product,
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 0m
        });
        await _dbContext.SaveChangesAsync();

        var transferId = Guid.NewGuid();
        var request = new SyncPushRequest
        {
            DeviceId = "device-transfer-rejected",
            InventoryTransfers =
            [
                new InventoryTransferDto
                {
                    Id = transferId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    SourceOfficeCode = OfficeCodeCatalog.Usenet,
                    TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
                    TransferNumber = "TR-REJECT-001",
                    TransferDate = new DateOnly(2026, 5, 28),
                    FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                    ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                    TransferStatus = InventoryTransferStatusNormalizer.Rejected,
                    RejectedByUsername = "admin",
                    RejectedAtUtc = DateTime.UtcNow,
                    MutationId = $"device-transfer-rejected:InventoryTransfer:{transferId:N}:1",
                    MutationCreatedAtUtc = DateTime.UtcNow,
                    Lines =
                    [
                        new InventoryTransferLineDto
                        {
                            Id = Guid.NewGuid(),
                            TransferId = transferId,
                            ItemId = itemId,
                            ItemNameOriginal = "반려 이동 품목",
                            SpecificationOriginal = string.Empty,
                            Unit = "개",
                            Quantity = 2m,
                            ReceivedQuantity = 2m
                        }
                    ]
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(0, result.ConflictCount);
        Assert.Equal(1, result.AcceptedCount);
        Assert.True(await _dbContext.InventoryTransfers.IgnoreQueryFilters().AnyAsync(x => x.Id == transferId));
        Assert.False(await _dbContext.InventoryLedgerEntries.AnyAsync(entry => entry.SourceDocumentId == transferId));
    }

    [Fact]
    public async Task Push_RejectsFinalInventoryTransferStatus_WhenTargetOfficeIsNotWritable()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "delivery-source-usenet",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = [PermissionNames.DeliveryEdit]
        };
        var itemId = Guid.NewGuid();
        _dbContext.Items.Add(new Item
        {
            Id = itemId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            NameOriginal = "Target guarded transfer item",
            NameMatchKey = "TARGETGUARDEDTRANSFERITEM",
            Unit = "EA",
            ItemKind = ItemKinds.Product,
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 10m
        });
        _dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
        {
            ItemId = itemId,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 10m,
            UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            Revision = 10
        });
        await _dbContext.SaveChangesAsync();

        await using var scopedDb = CreateDbContext(currentUser);
        var controller = CreateController(scopedDb, currentUser);
        var receivedTransferId = Guid.NewGuid();
        var rejectedTransferId = Guid.NewGuid();
        var response = await controller.Push(new SyncPushRequest
        {
            DeviceId = "device-transfer-final-status-target-scope",
            InventoryTransfers =
            [
                new InventoryTransferDto
                {
                    Id = receivedTransferId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    SourceOfficeCode = OfficeCodeCatalog.Usenet,
                    TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
                    TransferNumber = "TR-TARGET-SCOPE-RECEIVED",
                    TransferDate = new DateOnly(2026, 6, 17),
                    FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                    ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                    TransferStatus = InventoryTransferStatusNormalizer.Received,
                    ReceivedByUsername = currentUser.Username,
                    ReceivedAtUtc = DateTime.UtcNow,
                    MutationId = $"device-transfer-final-status-target-scope:InventoryTransfer:{receivedTransferId:N}:1",
                    MutationCreatedAtUtc = DateTime.UtcNow,
                    Lines =
                    [
                        new InventoryTransferLineDto
                        {
                            Id = Guid.NewGuid(),
                            TransferId = receivedTransferId,
                            ItemId = itemId,
                            ItemNameOriginal = "Target guarded transfer item",
                            SpecificationOriginal = string.Empty,
                            Unit = "EA",
                            Quantity = 2m,
                            ReceivedQuantity = 2m
                        }
                    ]
                },
                new InventoryTransferDto
                {
                    Id = rejectedTransferId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    SourceOfficeCode = OfficeCodeCatalog.Usenet,
                    TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
                    TransferNumber = "TR-TARGET-SCOPE-REJECTED",
                    TransferDate = new DateOnly(2026, 6, 17),
                    FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                    ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                    TransferStatus = InventoryTransferStatusNormalizer.Rejected,
                    RejectedByUsername = currentUser.Username,
                    RejectedAtUtc = DateTime.UtcNow,
                    RejectReason = "source office cannot reject for target",
                    MutationId = $"device-transfer-final-status-target-scope:InventoryTransfer:{rejectedTransferId:N}:1",
                    MutationCreatedAtUtc = DateTime.UtcNow,
                    Lines =
                    [
                        new InventoryTransferLineDto
                        {
                            Id = Guid.NewGuid(),
                            TransferId = rejectedTransferId,
                            ItemId = itemId,
                            ItemNameOriginal = "Target guarded transfer item",
                            SpecificationOriginal = string.Empty,
                            Unit = "EA",
                            Quantity = 1m,
                            ReceivedQuantity = 0m
                        }
                    ]
                }
            ]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(2, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            string.Equals(conflict.EntityName, nameof(InventoryTransfer), StringComparison.Ordinal) &&
            conflict.Reason.Contains("target office is outside the writable delivery scope", StringComparison.OrdinalIgnoreCase));
        scopedDb.ChangeTracker.Clear();
        Assert.False(await scopedDb.InventoryTransfers.IgnoreQueryFilters().AnyAsync(x => x.Id == receivedTransferId));
        Assert.False(await scopedDb.InventoryTransfers.IgnoreQueryFilters().AnyAsync(x => x.Id == rejectedTransferId));
        Assert.False(await scopedDb.InventoryLedgerEntries.AnyAsync(entry =>
            entry.SourceDocumentId == receivedTransferId || entry.SourceDocumentId == rejectedTransferId));
    }

    [Fact]
    public async Task Push_RollsBackEarlierChanges_WhenLaterContractStorageWriteFails()
    {
        var customerId = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        var seededCustomer = new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "롤백 거래처",
            NameMatchKey = "롤백거래처",
            TradeType = "매출",
            Notes = "before"
        };
        var seededContract = new CustomerContract
        {
            Id = contractId,
            CustomerId = customerId,
            ContractType = "거래계약서",
            FileName = "existing.pdf",
            MimeType = "application/pdf",
            FileSize = 10,
            FileHash = "seed",
            Description = "before"
        };
        _dbContext.Customers.Add(seededCustomer);
        _dbContext.CustomerContracts.Add(seededContract);
        await _dbContext.SaveChangesAsync();

        var currentUser = new TestCurrentUserContext
        {
            Username = "admin",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin,
            IsAdmin = true
        };
        var controller = CreateController(_dbContext, currentUser, new ThrowingCentralFileStorage());

        var currentCustomer = await _dbContext.Customers.IgnoreQueryFilters().FirstAsync(x => x.Id == customerId);
        var currentContract = await _dbContext.CustomerContracts.IgnoreQueryFilters().FirstAsync(x => x.Id == contractId);
        var request = new SyncPushRequest
        {
            DeviceId = "device-rollback",
            Customers =
            [
                new CustomerDto
                {
                    Id = currentCustomer.Id,
                    TenantCode = currentCustomer.TenantCode,
                    OfficeCode = currentCustomer.OfficeCode,
                    ResponsibleOfficeCode = currentCustomer.ResponsibleOfficeCode,
                    NameOriginal = currentCustomer.NameOriginal,
                    NameMatchKey = currentCustomer.NameMatchKey,
                    TradeType = currentCustomer.TradeType,
                    Notes = "after",
                    ExpectedRevision = currentCustomer.Revision,
                    Revision = currentCustomer.Revision,
                    MutationId = $"device-rollback:Customer:{currentCustomer.Id:N}:1",
                    MutationCreatedAtUtc = currentCustomer.UpdatedAtUtc.AddMinutes(1),
                    CreatedAtUtc = currentCustomer.CreatedAtUtc,
                    UpdatedAtUtc = currentCustomer.UpdatedAtUtc.AddMinutes(1)
                }
            ],
            CustomerContracts =
            [
                new CustomerContractDto
                {
                    Id = currentContract.Id,
                    CustomerId = currentCustomer.Id,
                    ContractType = currentContract.ContractType,
                    FileName = "updated.pdf",
                    MimeType = "application/pdf",
                    FileSize = 16,
                    FileHash = "changed",
                    Description = "after",
                    ExpectedRevision = currentContract.Revision,
                    Revision = currentContract.Revision,
                    MutationId = $"device-rollback:CustomerContract:{currentContract.Id:N}:1",
                    MutationCreatedAtUtc = currentContract.UpdatedAtUtc.AddMinutes(1),
                    CreatedAtUtc = currentContract.CreatedAtUtc,
                    UpdatedAtUtc = currentContract.UpdatedAtUtc.AddMinutes(1),
                    FileContent = [0x25, 0x50, 0x44, 0x46]
                }
            ]
        };

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await controller.Push(request, CancellationToken.None));

        _dbContext.ChangeTracker.Clear();
        var storedCustomer = await _dbContext.Customers.IgnoreQueryFilters().FirstAsync(x => x.Id == customerId);
        var storedContract = await _dbContext.CustomerContracts.IgnoreQueryFilters().FirstAsync(x => x.Id == contractId);
        Assert.Equal("before", storedCustomer.Notes);
        Assert.Equal("before", storedContract.Description);
        Assert.Equal(0, await _dbContext.ProcessedSyncMutations.CountAsync());
    }

    [Fact]
    public async Task ConflictLogsController_HidesResolvedByDefault_AndResolveMarksStatus()
    {
        var openConflict = new ConflictLog
        {
            EntityName = nameof(Customer),
            EntityId = Guid.NewGuid().ToString(),
            Reason = "open",
            Status = "Open",
            CreatedAtUtc = new DateTime(2026, 4, 13, 1, 0, 0, DateTimeKind.Utc)
        };
        var resolvedConflict = new ConflictLog
        {
            EntityName = nameof(Item),
            EntityId = Guid.NewGuid().ToString(),
            Reason = "resolved",
            Status = "Resolved",
            CreatedAtUtc = new DateTime(2026, 4, 13, 0, 0, 0, DateTimeKind.Utc),
            ResolvedAtUtc = new DateTime(2026, 4, 13, 0, 5, 0, DateTimeKind.Utc),
            ResolutionNote = "already fixed"
        };
        _dbContext.ConflictLogs.AddRange(openConflict, resolvedConflict);
        await _dbContext.SaveChangesAsync();

        var controller = new ConflictLogsController(_dbContext);

        var defaultResponse = await controller.GetAll(cancellationToken: CancellationToken.None);
        var defaultOk = Assert.IsType<OkObjectResult>(defaultResponse.Result);
        var defaultRows = Assert.IsType<List<ConflictLogDto>>(defaultOk.Value);
        Assert.Single(defaultRows);
        Assert.Equal(openConflict.Id, defaultRows[0].Id);

        var allResponse = await controller.GetAll(includeResolved: true, cancellationToken: CancellationToken.None);
        var allOk = Assert.IsType<OkObjectResult>(allResponse.Result);
        var allRows = Assert.IsType<List<ConflictLogDto>>(allOk.Value);
        Assert.Equal(2, allRows.Count);

        var resolveResponse = await controller.Resolve(openConflict.Id, "manual note", CancellationToken.None);
        var resolveOk = Assert.IsType<OkObjectResult>(resolveResponse.Result);
        var resolvedDto = Assert.IsType<ConflictLogDto>(resolveOk.Value);
        Assert.Equal("Resolved", resolvedDto.Status);
        Assert.Equal("manual note", resolvedDto.ResolutionNote);
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
        Assert.True(
            result.ConflictCount == 0,
            string.Join(" | ", result.Conflicts.Select(conflict => $"{conflict.EntityName}:{conflict.Reason}")));

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
    public async Task Push_RejectsItemDelete_WhenActiveInvoiceLineReferenceExists()
    {
        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "SYNC-DELETE-ACTIVE-INVOICE-ITEM",
            NameMatchKey = "SYNCDELETEACTIVEINVOICEITEM",
            TrackingType = ItemTrackingTypes.Stock
        };
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "SYNC-ITEM-DELETE-CUSTOMER",
            NameMatchKey = "SYNCITEMDELETECUSTOMER",
            TradeType = CustomerClassificationNormalizer.Sales
        };
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "SYNC-ITEM-DELETE-BLOCK",
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 22),
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
        _dbContext.Items.Add(item);
        _dbContext.Customers.Add(customer);
        _dbContext.Invoices.Add(invoice);
        await _dbContext.SaveChangesAsync();

        var stored = await _dbContext.Items.IgnoreQueryFilters().SingleAsync(row => row.Id == item.Id);
        var response = await _controller.Push(new SyncPushRequest
        {
            DeviceId = "device-item-delete-active-reference",
            Items =
            [
                new ItemDto
                {
                    Id = item.Id,
                    TenantCode = item.TenantCode,
                    OfficeCode = item.OfficeCode,
                    NameOriginal = item.NameOriginal,
                    NameMatchKey = item.NameMatchKey,
                    TrackingType = item.TrackingType,
                    IsDeleted = true,
                    ExpectedRevision = stored.Revision,
                    CreatedAtUtc = DateTime.UtcNow.AddMinutes(-1),
                    UpdatedAtUtc = DateTime.UtcNow
                }
            ]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            conflict.EntityName == nameof(Item) &&
            conflict.Reason.Contains("전표 라인", StringComparison.OrdinalIgnoreCase));
        Assert.False(await _dbContext.Items.IgnoreQueryFilters()
            .Where(row => row.Id == item.Id)
            .Select(row => row.IsDeleted)
            .SingleAsync());
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
    public async Task Push_ReusesExistingItem_WhenIncomingIdDiffersButMaterialNumberMatches()
    {
        var existing = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            NameOriginal = "IMC3010",
            NameMatchKey = "IMC3010",
            CategoryName = "A3컬러복합기",
            ItemKind = ItemKinds.Asset,
            TrackingType = ItemTrackingTypes.Asset,
            MaterialNumber = "2603-885",
            SerialNumber = "9155RC30012",
            Notes = "before",
            UpdatedAtUtc = new DateTime(2026, 4, 10, 0, 0, 0, DateTimeKind.Utc)
        };
        _dbContext.Items.Add(existing);
        await _dbContext.SaveChangesAsync();

        var request = new SyncPushRequest
        {
            Items =
            [
                new ItemDto
                {
                    Id = Guid.NewGuid(),
                    TenantCode = TenantScopeCatalog.Itworld,
                    OfficeCode = OfficeCodeCatalog.Itworld,
                    NameOriginal = existing.NameOriginal,
                    NameMatchKey = existing.NameMatchKey,
                    CategoryName = existing.CategoryName,
                    ItemKind = ItemKinds.Asset,
                    TrackingType = ItemTrackingTypes.Asset,
                    MaterialNumber = existing.MaterialNumber,
                    SerialNumber = existing.SerialNumber,
                    Notes = "after",
                    CreatedAtUtc = existing.CreatedAtUtc,
                    UpdatedAtUtc = existing.UpdatedAtUtc.AddMinutes(3)
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.Equal(0, result.ConflictCount);

        var items = await _dbContext.Items.IgnoreQueryFilters().ToListAsync();
        Assert.Single(items);
        Assert.Equal(existing.Id, items[0].Id);
        Assert.Equal("after", items[0].Notes);
    }

    [Fact]
    public async Task Push_EnsuresActiveItemCategoryOptionForAcceptedItem()
    {
        var itemId = Guid.NewGuid();
        var response = await _controller.Push(new SyncPushRequest
        {
            DeviceId = "device-item-category-option-ensure",
            Items =
            [
                new ItemDto
                {
                    Id = itemId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    NameOriginal = "Synced category item",
                    NameMatchKey = "SYNCEDCATEGORYITEM",
                    CategoryName = " A3 Copier ",
                    ItemKind = ItemKinds.Product,
                    TrackingType = ItemTrackingTypes.Stock,
                    Unit = "EA",
                    CreatedAtUtc = new DateTime(2026, 6, 17, 0, 0, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2026, 6, 17, 0, 1, 0, DateTimeKind.Utc)
                }
            ]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(0, result.ConflictCount);
        var item = await _dbContext.Items.IgnoreQueryFilters().SingleAsync(row => row.Id == itemId);
        Assert.Equal("A3 Copier", item.CategoryName);
        var option = await _dbContext.ItemCategoryOptions.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("A3 Copier", option.Name);
        Assert.True(option.IsActive);
        Assert.False(option.IsDeleted);
    }

    [Fact]
    public async Task Push_ReusesExistingItem_WhenDescriptorMatchesSingleServerItemWithoutIdentifiers()
    {
        var existing = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            NameOriginal = "SL-X4220RX",
            NameMatchKey = "SLX4220RX",
            CategoryName = "A3컬러복합기",
            ItemKind = ItemKinds.Asset,
            TrackingType = ItemTrackingTypes.Asset,
            MaterialNumber = string.Empty,
            SerialNumber = string.Empty,
            InstallLocation = "사무실",
            UpdatedAtUtc = new DateTime(2026, 4, 10, 0, 0, 0, DateTimeKind.Utc)
        };
        _dbContext.Items.Add(existing);
        await _dbContext.SaveChangesAsync();

        var request = new SyncPushRequest
        {
            Items =
            [
                new ItemDto
                {
                    Id = Guid.NewGuid(),
                    TenantCode = TenantScopeCatalog.Itworld,
                    OfficeCode = OfficeCodeCatalog.Itworld,
                    NameOriginal = existing.NameOriginal,
                    NameMatchKey = existing.NameMatchKey,
                    CategoryName = existing.CategoryName,
                    ItemKind = ItemKinds.Asset,
                    TrackingType = ItemTrackingTypes.Asset,
                    MaterialNumber = "2603-063",
                    SerialNumber = "28S3B1AG600076F",
                    InstallLocation = existing.InstallLocation,
                    Notes = "hydrated",
                    CreatedAtUtc = existing.CreatedAtUtc,
                    UpdatedAtUtc = existing.UpdatedAtUtc.AddMinutes(5)
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.Equal(0, result.ConflictCount);

        var items = await _dbContext.Items.IgnoreQueryFilters().ToListAsync();
        Assert.Single(items);
        Assert.Equal(existing.Id, items[0].Id);
        Assert.Equal("2603-063", items[0].MaterialNumber);
        Assert.Equal("28S3B1AG600076F", items[0].SerialNumber);
        Assert.Equal("hydrated", items[0].Notes);
    }

    [Fact]
    public async Task Push_DeduplicatesNewItemsWithinSingleBatch_WhenNaturalKeyMatches()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();

        var request = new SyncPushRequest
        {
            Items =
            [
                new ItemDto
                {
                    Id = firstId,
                    TenantCode = TenantScopeCatalog.Itworld,
                    OfficeCode = OfficeCodeCatalog.Itworld,
                    NameOriginal = "SL-C563W",
                    NameMatchKey = "SLC563W",
                    CategoryName = "A4컬러복합기",
                    ItemKind = ItemKinds.Asset,
                    TrackingType = ItemTrackingTypes.Asset,
                    MaterialNumber = "2603-532",
                    SerialNumber = "ZKKTB8KT5B023FZ",
                    Notes = "first",
                    UpdatedAtUtc = new DateTime(2026, 4, 10, 0, 0, 0, DateTimeKind.Utc)
                },
                new ItemDto
                {
                    Id = secondId,
                    TenantCode = TenantScopeCatalog.Itworld,
                    OfficeCode = OfficeCodeCatalog.Itworld,
                    NameOriginal = "SL-C563W",
                    NameMatchKey = "SLC563W",
                    CategoryName = "A4컬러복합기",
                    ItemKind = ItemKinds.Asset,
                    TrackingType = ItemTrackingTypes.Asset,
                    MaterialNumber = "2603-532",
                    SerialNumber = "ZKKTB8KT5B023FZ",
                    Notes = "second",
                    UpdatedAtUtc = new DateTime(2026, 4, 10, 0, 5, 0, DateTimeKind.Utc)
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.Equal(0, result.ConflictCount);

        var items = await _dbContext.Items.IgnoreQueryFilters().ToListAsync();
        Assert.Single(items);
        Assert.Equal(firstId, items[0].Id);
        Assert.Equal("second", items[0].Notes);
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
            ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
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
    public async Task Push_RejectsStaleCustomerUpdate_WhenServerRevisionIsNewerEvenIfIncomingTimestampIsLater()
    {
        var scopedUser = new TestCurrentUserContext
        {
            Username = "itworld_admin",
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ScopeType = TenantScopeCatalog.ScopeTenantAll,
            IsAdmin = true
        };

        await using var scopedDb = CreateDbContext(scopedUser);
        var existing = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
            NameOriginal = "STALE-CUSTOMER",
            NameMatchKey = "STALECUSTOMER",
            TradeType = "매출",
            Notes = "server-current"
        };
        scopedDb.Customers.Add(existing);
        await scopedDb.SaveChangesAsync();

        var serverRevision = existing.Revision;
        var serverUpdatedAtUtc = existing.UpdatedAtUtc;
        existing.Notes = "server-newer";
        await scopedDb.SaveChangesAsync();

        var controller = CreateController(scopedDb, scopedUser);
        var request = new SyncPushRequest
        {
            Customers =
            [
                new CustomerDto
                {
                    Id = existing.Id,
                    Revision = serverRevision,
                    TenantCode = TenantScopeCatalog.Itworld,
                    OfficeCode = OfficeCodeCatalog.Itworld,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                    NameOriginal = existing.NameOriginal,
                    NameMatchKey = existing.NameMatchKey,
                    TradeType = "매출",
                    Notes = "client-stale",
                    CreatedAtUtc = existing.CreatedAtUtc,
                    UpdatedAtUtc = serverUpdatedAtUtc.AddHours(1)
                }
            ]
        };

        var response = await controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(1, result.ConflictCount);
        Assert.Equal(0, result.AcceptedCount);

        var reloaded = await scopedDb.Customers.IgnoreQueryFilters().FirstAsync(customer => customer.Id == existing.Id);
        Assert.Equal("server-newer", reloaded.Notes);
    }

    [Fact]
    public async Task Push_RejectsRentalAssignmentHistoryRelinkToOutOfScopeAsset()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "rental-history-usenet",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = [PermissionNames.RentalAssetEdit]
        };

        var allowedAsset = new RentalAsset
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            AssetKey = "RENTAL-HISTORY-ALLOWED-ASSET",
            ManagementNumber = "RH-ALLOWED",
            ItemName = "Allowed copier",
            MachineNumber = "ALLOWED-001",
            MonthlyFee = 100m
        };
        var outOfScopeAsset = new RentalAsset
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            AssetKey = "RENTAL-HISTORY-HIDDEN-ASSET",
            ManagementNumber = "RH-HIDDEN",
            ItemName = "Hidden copier",
            MachineNumber = "HIDDEN-001",
            MonthlyFee = 200m
        };
        var history = new RentalAssetAssignmentHistory
        {
            Id = Guid.NewGuid(),
            AssetId = allowedAsset.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ItemName = allowedAsset.ItemName,
            MachineNumber = allowedAsset.MachineNumber,
            ManagementNumber = allowedAsset.ManagementNumber,
            MonthlyFee = allowedAsset.MonthlyFee,
            IsCurrent = true,
            LinkedAtUtc = DateTime.UtcNow.AddDays(-1)
        };
        _dbContext.RentalAssets.AddRange(allowedAsset, outOfScopeAsset);
        _dbContext.RentalAssetAssignmentHistories.Add(history);
        await _dbContext.SaveChangesAsync();

        await using var scopedDb = CreateDbContext(currentUser);
        var controller = CreateController(scopedDb, currentUser);
        var response = await controller.Push(new SyncPushRequest
        {
            DeviceId = "rental-history-out-of-scope-asset-device",
            RentalAssetAssignmentHistories =
            [
                new RentalAssetAssignmentHistoryDto
                {
                    Id = history.Id,
                    AssetId = outOfScopeAsset.Id,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    ItemName = "Client tries to relink",
                    MachineNumber = "CLIENT-RELINK",
                    ManagementNumber = "CLIENT-RELINK",
                    MonthlyFee = 300m,
                    IsCurrent = true,
                    LinkedAtUtc = DateTime.UtcNow,
                    Revision = history.Revision,
                    ExpectedRevision = history.Revision
                }
            ]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            string.Equals(conflict.EntityName, nameof(RentalAssetAssignmentHistory), StringComparison.Ordinal) &&
            conflict.Reason.Contains("Referenced rental asset is outside the writable office scope", StringComparison.Ordinal));
        scopedDb.ChangeTracker.Clear();
        var stored = await scopedDb.RentalAssetAssignmentHistories.IgnoreQueryFilters()
            .SingleAsync(row => row.Id == history.Id);
        Assert.Equal(allowedAsset.Id, stored.AssetId);
        Assert.Equal(allowedAsset.ManagementNumber, stored.ManagementNumber);
        Assert.Equal(allowedAsset.MonthlyFee, stored.MonthlyFee);
    }

    [Fact]
    public async Task Push_RejectsRentalAssignmentHistoryBillingProfile_WhenProfileIsReadSharedButNotWritable()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "rental-history-usenet",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = [PermissionNames.RentalAssetEdit]
        };

        var asset = new RentalAsset
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            AssetKey = "RENTAL-HISTORY-READ-SHARED-PROFILE-ASSET",
            ManagementNumber = "RH-READ-SHARED",
            ItemName = "Read shared profile copier",
            MachineNumber = "READ-SHARED-001",
            MonthlyFee = 100m
        };
        var sharedReadOnlyProfile = new RentalBillingProfile
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            ProfileKey = "READ-SHARED-HISTORY-PROFILE",
            CustomerName = "Read Shared History Customer",
            ItemName = asset.ItemName,
            InstallSiteName = "Read Shared History Site",
            BillingDay = 10,
            MonthlyAmount = 30000m
        };
        _dbContext.RentalAssets.Add(asset);
        _dbContext.RentalBillingProfiles.Add(sharedReadOnlyProfile);
        _dbContext.DataSharingPolicies.Add(new DataSharingPolicy
        {
            Id = Guid.NewGuid(),
            SourceTenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Yeonsu,
            TargetTenantCode = TenantScopeCatalog.UsenetGroup,
            TargetOfficeCode = OfficeCodeCatalog.Usenet,
            ShareRentals = true,
            AllowTargetWrite = false,
            Note = "read-only rental share for assignment history profile link scope test"
        });
        await _dbContext.SaveChangesAsync();

        await using var scopedDb = CreateDbContext(currentUser);
        var controller = CreateController(scopedDb, currentUser);
        var historyId = Guid.NewGuid();
        var response = await controller.Push(new SyncPushRequest
        {
            DeviceId = "rental-history-read-shared-profile-device",
            RentalAssetAssignmentHistories =
            [
                new RentalAssetAssignmentHistoryDto
                {
                    Id = historyId,
                    AssetId = asset.Id,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    BillingProfileId = sharedReadOnlyProfile.Id,
                    BillingProfileDisplay = sharedReadOnlyProfile.ProfileKey,
                    CustomerName = sharedReadOnlyProfile.CustomerName,
                    InstallLocation = sharedReadOnlyProfile.InstallSiteName,
                    ItemName = asset.ItemName,
                    MachineNumber = asset.MachineNumber,
                    ManagementNumber = asset.ManagementNumber,
                    MonthlyFee = asset.MonthlyFee,
                    IsCurrent = true,
                    LinkedAtUtc = DateTime.UtcNow
                }
            ]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            string.Equals(conflict.EntityName, nameof(RentalAssetAssignmentHistory), StringComparison.Ordinal) &&
            conflict.Reason.Contains("Referenced rental billing profile is outside the writable office scope", StringComparison.Ordinal));
        Assert.False(await scopedDb.RentalAssetAssignmentHistories.IgnoreQueryFilters().AnyAsync(row => row.Id == historyId));
    }

    [Fact]
    public async Task Push_RejectsCurrentRentalAssignmentHistory_WhenCustomerOrBillingProfileIsMissingOrDeleted()
    {
        var asset = new RentalAsset
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            AssetKey = "RENTAL-HISTORY-STALE-CURRENT-ASSET",
            ManagementNumber = "RH-STALE-CURRENT",
            ItemName = "Stale current copier",
            MachineNumber = "STALE-CURRENT-001",
            MonthlyFee = 100m
        };
        var deletedCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Deleted current history customer",
            NameMatchKey = "DELETEDCURRENTHISTORYCUSTOMER",
            TradeType = "매출",
            IsDeleted = true
        };
        _dbContext.RentalAssets.Add(asset);
        _dbContext.Customers.Add(deletedCustomer);
        await _dbContext.SaveChangesAsync();

        var deletedCustomerHistoryId = Guid.NewGuid();
        var missingProfileHistoryId = Guid.NewGuid();
        var missingProfileId = Guid.NewGuid();
        var response = await _controller.Push(new SyncPushRequest
        {
            DeviceId = "rental-history-stale-current-reference-device",
            RentalAssetAssignmentHistories =
            [
                new RentalAssetAssignmentHistoryDto
                {
                    Id = deletedCustomerHistoryId,
                    AssetId = asset.Id,
                    CustomerId = deletedCustomer.Id,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    CustomerName = deletedCustomer.NameOriginal,
                    ItemName = asset.ItemName,
                    MachineNumber = asset.MachineNumber,
                    ManagementNumber = asset.ManagementNumber,
                    MonthlyFee = asset.MonthlyFee,
                    IsCurrent = true,
                    LinkedAtUtc = DateTime.UtcNow
                },
                new RentalAssetAssignmentHistoryDto
                {
                    Id = missingProfileHistoryId,
                    AssetId = asset.Id,
                    BillingProfileId = missingProfileId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    BillingProfileDisplay = "missing profile",
                    ItemName = asset.ItemName,
                    MachineNumber = asset.MachineNumber,
                    ManagementNumber = asset.ManagementNumber,
                    MonthlyFee = asset.MonthlyFee,
                    IsCurrent = true,
                    LinkedAtUtc = DateTime.UtcNow
                }
            ]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(2, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            string.Equals(conflict.EntityName, nameof(RentalAssetAssignmentHistory), StringComparison.Ordinal) &&
            conflict.Reason.Contains("Referenced customer is missing or deleted", StringComparison.Ordinal));
        Assert.Contains(result.Conflicts, conflict =>
            string.Equals(conflict.EntityName, nameof(RentalAssetAssignmentHistory), StringComparison.Ordinal) &&
            conflict.Reason.Contains("Referenced rental billing profile is missing or deleted", StringComparison.Ordinal));
        Assert.False(await _dbContext.RentalAssetAssignmentHistories.IgnoreQueryFilters()
            .AnyAsync(row => row.Id == deletedCustomerHistoryId || row.Id == missingProfileHistoryId));
    }

    [Fact]
    public async Task Push_ClearsHistoricalRentalAssignmentHistory_DeletedCustomerAndProfileReferences()
    {
        var asset = new RentalAsset
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            AssetKey = "RENTAL-HISTORY-STALE-HISTORICAL-ASSET",
            ManagementNumber = "RH-STALE-HISTORICAL",
            ItemName = "Stale historical copier",
            MachineNumber = "STALE-HISTORICAL-001",
            MonthlyFee = 100m
        };
        var deletedCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Deleted historical history customer",
            NameMatchKey = "DELETEDHISTORICALHISTORYCUSTOMER",
            TradeType = "Sales",
            IsDeleted = true
        };
        var deletedProfile = new RentalBillingProfile
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ProfileKey = "DELETED-HISTORICAL-HISTORY-PROFILE",
            CustomerName = deletedCustomer.NameOriginal,
            InstallSiteName = "Historical site",
            IsDeleted = true
        };
        _dbContext.RentalAssets.Add(asset);
        _dbContext.Customers.Add(deletedCustomer);
        _dbContext.RentalBillingProfiles.Add(deletedProfile);
        await _dbContext.SaveChangesAsync();

        var historyId = Guid.NewGuid();
        var response = await _controller.Push(new SyncPushRequest
        {
            DeviceId = "rental-history-stale-historical-reference-device",
            RentalAssetAssignmentHistories =
            [
                new RentalAssetAssignmentHistoryDto
                {
                    Id = historyId,
                    AssetId = asset.Id,
                    BillingProfileId = deletedProfile.Id,
                    CustomerId = deletedCustomer.Id,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    BillingProfileDisplay = deletedProfile.ProfileKey,
                    CustomerName = deletedCustomer.NameOriginal,
                    InstallLocation = deletedProfile.InstallSiteName,
                    ItemName = asset.ItemName,
                    MachineNumber = asset.MachineNumber,
                    ManagementNumber = asset.ManagementNumber,
                    MonthlyFee = asset.MonthlyFee,
                    IsCurrent = false,
                    LinkedAtUtc = DateTime.UtcNow.AddDays(-7),
                    UnlinkedAtUtc = DateTime.UtcNow.AddDays(-1)
                }
            ]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(0, result.ConflictCount);
        Assert.Contains(result.AcceptedRevisions, revision =>
            revision.EntityId == historyId &&
            string.Equals(revision.EntityName, nameof(RentalAssetAssignmentHistory), StringComparison.Ordinal));
        Assert.Contains(result.Notices, notice =>
            notice.Code == "historical-rental-assignment-customer-reference-cleared");
        Assert.Contains(result.Notices, notice =>
            notice.Code == "historical-rental-assignment-profile-reference-cleared");

        var stored = await _dbContext.RentalAssetAssignmentHistories.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(row => row.Id == historyId);
        Assert.Null(stored.CustomerId);
        Assert.Null(stored.BillingProfileId);
        Assert.Equal(deletedCustomer.NameOriginal, stored.CustomerName);
        Assert.Equal(deletedProfile.InstallSiteName, stored.InstallLocation);
    }

    [Fact]
    public async Task Push_StoresAndPullsCurrentRentalAssignmentHistory_WithMergedActiveCustomerReference()
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Merged active history customer",
            NameMatchKey = "MERGEDACTIVEHISTORYCUSTOMER",
            TradeType = "매출"
        };
        var asset = new RentalAsset
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            AssetKey = "RENTAL-HISTORY-MERGED-CUSTOMER-ASSET",
            ManagementNumber = "RH-MERGED-CUSTOMER",
            ItemName = "Merged customer copier",
            MachineNumber = "MERGED-CUSTOMER-001",
            MonthlyFee = 100m
        };
        _dbContext.Customers.Add(customer);
        _dbContext.RentalAssets.Add(asset);
        await _dbContext.SaveChangesAsync();

        var historyId = Guid.NewGuid();
        var response = await _controller.Push(new SyncPushRequest
        {
            DeviceId = "rental-history-merged-customer-device",
            RentalAssetAssignmentHistories =
            [
                new RentalAssetAssignmentHistoryDto
                {
                    Id = historyId,
                    AssetId = asset.Id,
                    CustomerId = customer.Id,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    CustomerName = customer.NameOriginal,
                    ItemName = asset.ItemName,
                    MachineNumber = asset.MachineNumber,
                    ManagementNumber = asset.ManagementNumber,
                    MonthlyFee = asset.MonthlyFee,
                    IsCurrent = true,
                    LinkedAtUtc = DateTime.UtcNow
                }
            ]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(0, result.ConflictCount);
        Assert.Contains(result.AcceptedRevisions, revision =>
            revision.EntityId == historyId &&
            string.Equals(revision.EntityName, nameof(RentalAssetAssignmentHistory), StringComparison.Ordinal));

        var stored = await _dbContext.RentalAssetAssignmentHistories.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(row => row.Id == historyId);
        Assert.Equal(customer.Id, stored.CustomerId);
        Assert.Equal(customer.NameOriginal, stored.CustomerName);

        var pullResponse = await _controller.Pull(0, CancellationToken.None);
        var pullOk = Assert.IsType<OkObjectResult>(pullResponse.Result);
        var pull = Assert.IsType<SyncPullResponse>(pullOk.Value);
        var pulledHistory = Assert.Single(pull.RentalAssetAssignmentHistories, row => row.Id == historyId);
        Assert.Equal(customer.Id, pulledHistory.CustomerId);
        Assert.Equal(customer.NameOriginal, pulledHistory.CustomerName);
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
    public async Task Push_RejectsRentalAsset_WhenExplicitCustomerIdExistsOutsideReadableScope()
    {
        var outsideCustomerId = Guid.NewGuid();
        _dbContext.Customers.Add(new Customer
        {
            Id = outsideCustomerId,
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
            NameOriginal = "ITWORLD-HIDDEN-RENTAL-ASSET-CUSTOMER",
            NameMatchKey = "ITWORLDHIDDENRENTALASSETCUSTOMER",
            TradeType = "매출"
        });
        await _dbContext.SaveChangesAsync();

        var currentUser = new TestCurrentUserContext
        {
            Username = "usenet-rental-asset-customer-scope",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = [PermissionNames.RentalAssetEdit]
        };
        await using var scopedDb = CreateDbContext(currentUser);
        var controller = CreateController(scopedDb, currentUser);
        var assetId = Guid.NewGuid();

        var response = await controller.Push(new SyncPushRequest
        {
            DeviceId = "device-rental-asset-outside-customer",
            RentalAssets =
            [
                new RentalAssetDto
                {
                    Id = assetId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    ManagementId = "701",
                    ManagementNumber = "2606-701",
                    CustomerId = outsideCustomerId,
                    CustomerName = "ITWORLD-HIDDEN-RENTAL-ASSET-CUSTOMER",
                    CurrentCustomerName = "ITWORLD-HIDDEN-RENTAL-ASSET-CUSTOMER",
                    ItemName = "MODEL-OUTSIDE-CUSTOMER",
                    CurrentLocation = "설치",
                    CreatedAtUtc = new DateTime(2026, 6, 19, 0, 1, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2026, 6, 19, 0, 1, 0, DateTimeKind.Utc)
                }
            ]
        }, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            conflict.EntityName == nameof(RentalAsset) &&
            conflict.Reason.Contains("outside the readable office scope", StringComparison.OrdinalIgnoreCase));
        Assert.False(await scopedDb.RentalAssets.IgnoreQueryFilters().AnyAsync(asset => asset.Id == assetId));
    }

    [Fact]
    public async Task Push_RejectsCrossTenantRentalAssetUpdate_ForUserWithRentalEditAll()
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
        Assert.Equal(1, result.ConflictCount);

        var updated = await scopedDb.RentalAssets.IgnoreQueryFilters().FirstAsync(asset => asset.Id == existing.Id);
        Assert.Equal(existing.CurrentLocation, updated.CurrentLocation);
        Assert.Equal(TenantScopeCatalog.UsenetGroup, updated.TenantCode);
        Assert.Equal(OfficeCodeCatalog.Usenet, updated.OfficeCode);
    }

    [Fact]
    public async Task Push_ReusesExistingRentalAsset_WhenIncomingIdDiffersButManagementNumberMatches()
    {
        var existing = new RentalAsset
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ManagementCompanyCode = OfficeCodeCatalog.Itworld,
            AssetKey = "ITWORLD|2603-321|321|SYNC-ASSET|MODEL-Z",
            ManagementId = "321",
            ManagementNumber = "2603-321",
            CustomerName = "기존 거래처",
            ItemName = "MODEL-Z",
            CurrentLocation = "창고",
            CreatedAtUtc = new DateTime(2026, 3, 27, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 3, 27, 0, 0, 0, DateTimeKind.Utc)
        };
        _dbContext.RentalAssets.Add(existing);
        await _dbContext.SaveChangesAsync();

        var request = new SyncPushRequest
        {
            RentalAssets =
            [
                new RentalAssetDto
                {
                    Id = Guid.NewGuid(),
                    TenantCode = TenantScopeCatalog.Itworld,
                    OfficeCode = OfficeCodeCatalog.Itworld,
                    ManagementCompanyCode = OfficeCodeCatalog.Itworld,
                    ManagementId = existing.ManagementId,
                    ManagementNumber = existing.ManagementNumber,
                    CustomerName = existing.CustomerName,
                    ItemName = existing.ItemName,
                    CurrentLocation = "렌탈",
                    CreatedAtUtc = existing.CreatedAtUtc,
                    UpdatedAtUtc = existing.UpdatedAtUtc.AddMinutes(5)
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.Equal(0, result.ConflictCount);

        var assets = await _dbContext.RentalAssets.IgnoreQueryFilters().ToListAsync();
        Assert.Single(assets);
        Assert.Equal(existing.Id, assets[0].Id);
        Assert.Equal("렌탈", assets[0].CurrentLocation);
        Assert.Equal(existing.ManagementNumber, assets[0].ManagementNumber);
    }

    [Fact]
    public async Task Push_ReusesExistingRentalBillingProfile_WhenIncomingIdDiffersButProfileKeyMatches()
    {
        _dbContext.RentalManagementCompanies.Add(new RentalManagementCompany
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.Itworld,
            Code = OfficeCodeCatalog.Itworld,
            Name = "아이티월드"
        });

        var existing = new RentalBillingProfile
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ProfileKey = "ITWORLD|1234567890|기존거래처||MODEL-Z",
            CustomerName = "기존거래처",
            BusinessNumber = "123-45-67890",
            ItemName = "MODEL-Z",
            ManagementCompanyCode = OfficeCodeCatalog.Itworld,
            BillingDay = 25,
            MonthlyAmount = 55000m,
            CreatedAtUtc = new DateTime(2026, 3, 27, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 3, 27, 0, 0, 0, DateTimeKind.Utc)
        };
        _dbContext.RentalBillingProfiles.Add(existing);
        await _dbContext.SaveChangesAsync();

        var request = new SyncPushRequest
        {
            RentalBillingProfiles =
            [
                new RentalBillingProfileDto
                {
                    Id = Guid.NewGuid(),
                    TenantCode = TenantScopeCatalog.Itworld,
                    OfficeCode = OfficeCodeCatalog.Itworld,
                    ProfileKey = existing.ProfileKey,
                    CustomerName = existing.CustomerName,
                    BusinessNumber = existing.BusinessNumber,
                    ItemName = existing.ItemName,
                    ManagementCompanyCode = existing.ManagementCompanyCode,
                    BillingDay = 28,
                    MonthlyAmount = 77000m,
                    CreatedAtUtc = existing.CreatedAtUtc,
                    UpdatedAtUtc = existing.UpdatedAtUtc.AddMinutes(5)
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.True(result.ConflictCount == 0,
            string.Join(" | ", result.Conflicts.Select(conflict => $"{conflict.EntityName}:{conflict.Reason}")));

        var profiles = await _dbContext.RentalBillingProfiles.IgnoreQueryFilters().ToListAsync();
        Assert.Single(profiles);
        Assert.Equal(existing.Id, profiles[0].Id);
        Assert.Equal(28, profiles[0].BillingDay);
        Assert.Equal(77000m, profiles[0].MonthlyAmount);
    }

    [Fact]
    public async Task Push_AcceptsRentalBillingProfile_WhenReferencedManagementCompanyIsInSameBatch()
    {
        var companyId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var createdAtUtc = new DateTime(2026, 3, 28, 0, 0, 0, DateTimeKind.Utc);

        var request = new SyncPushRequest
        {
            RentalManagementCompanies =
            [
                new RentalManagementCompanyDto
                {
                    Id = companyId,
                    TenantCode = TenantScopeCatalog.Itworld,
                    Code = OfficeCodeCatalog.Itworld,
                    Name = "아이티월드",
                    CreatedAtUtc = createdAtUtc,
                    UpdatedAtUtc = createdAtUtc
                }
            ],
            RentalBillingProfiles =
            [
                new RentalBillingProfileDto
                {
                    Id = profileId,
                    TenantCode = TenantScopeCatalog.Itworld,
                    OfficeCode = OfficeCodeCatalog.Itworld,
                    ProfileKey = "ITWORLD|1112233334|동시등록거래처||IMC2010",
                    CustomerName = "동시등록거래처",
                    BusinessNumber = "111-22-33334",
                    ItemName = "IMC2010",
                    ManagementCompanyCode = OfficeCodeCatalog.Itworld,
                    BillingDay = 25,
                    MonthlyAmount = 55000m,
                    CreatedAtUtc = createdAtUtc,
                    UpdatedAtUtc = createdAtUtc
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.True(result.ConflictCount == 0,
            string.Join(" | ", result.Conflicts.Select(conflict => $"{conflict.EntityName}:{conflict.Reason}")));

        var storedCompany = await _dbContext.RentalManagementCompanies.IgnoreQueryFilters()
            .SingleAsync(company => company.Code == OfficeCodeCatalog.Itworld);
        var storedProfile = await _dbContext.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync();

        Assert.Equal(TenantScopeCatalog.Itworld, storedCompany.TenantCode);
        Assert.Equal(profileId, storedProfile.Id);
        Assert.Equal(OfficeCodeCatalog.Itworld, storedProfile.ManagementCompanyCode);
    }

    [Fact]
    public async Task Push_AcceptsRentalBillingProfile_WhenReferencedCustomerIsInSameBatch()
    {
        var customerId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var createdAtUtc = new DateTime(2026, 3, 28, 0, 0, 0, DateTimeKind.Utc);

        var request = new SyncPushRequest
        {
            Customers =
            [
                new CustomerDto
                {
                    Id = customerId,
                    TenantCode = TenantScopeCatalog.Itworld,
                    OfficeCode = OfficeCodeCatalog.Itworld,
                    NameOriginal = "동시등록렌탈거래처",
                    NameMatchKey = "동시등록렌탈거래처",
                    TradeType = "매출",
                    BusinessNumber = "555-66-77777",
                    CreatedAtUtc = createdAtUtc,
                    UpdatedAtUtc = createdAtUtc
                }
            ],
            RentalManagementCompanies =
            [
                new RentalManagementCompanyDto
                {
                    Id = Guid.NewGuid(),
                    TenantCode = TenantScopeCatalog.Itworld,
                    Code = OfficeCodeCatalog.Itworld,
                    Name = "아이티월드",
                    CreatedAtUtc = createdAtUtc,
                    UpdatedAtUtc = createdAtUtc
                }
            ],
            RentalBillingProfiles =
            [
                new RentalBillingProfileDto
                {
                    Id = profileId,
                    TenantCode = TenantScopeCatalog.Itworld,
                    OfficeCode = OfficeCodeCatalog.Itworld,
                    ProfileKey = "ITWORLD|5556677777|동시등록렌탈거래처||BP-1",
                    CustomerId = customerId,
                    CustomerName = "동시등록렌탈거래처",
                    BusinessNumber = "555-66-77777",
                    ItemName = "BP-1",
                    ManagementCompanyCode = OfficeCodeCatalog.Itworld,
                    BillingDay = 15,
                    MonthlyAmount = 66000m,
                    CreatedAtUtc = createdAtUtc,
                    UpdatedAtUtc = createdAtUtc
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.True(result.ConflictCount == 0,
            string.Join(" | ", result.Conflicts.Select(conflict => $"{conflict.EntityName}:{conflict.Reason}")));

        var storedProfile = await _dbContext.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(customerId, storedProfile.CustomerId);
    }

    [Fact]
    public async Task Push_ReusesReadableRentalBillingProfileCustomer_WhenIncomingCustomerIdIsStale()
    {
        _dbContext.Customers.Add(new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "한길노무법인",
            NameMatchKey = "한길노무법인",
            TradeType = "매출",
            BusinessNumber = "998-87-76655"
        });
        _dbContext.RentalManagementCompanies.Add(new RentalManagementCompany
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            Code = OfficeCodeCatalog.Usenet,
            Name = "유즈넷"
        });
        await _dbContext.SaveChangesAsync();

        var request = new SyncPushRequest
        {
            RentalBillingProfiles =
            [
                new RentalBillingProfileDto
                {
                    Id = Guid.NewGuid(),
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ProfileKey = "USENET|9988776655|한길노무법인||IMC2010",
                    CustomerId = Guid.NewGuid(),
                    CustomerName = "한길노무법인",
                    BusinessNumber = "998-87-76655",
                    ItemName = "IMC2010",
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    BillingDay = 25,
                    MonthlyAmount = 44000m,
                    CreatedAtUtc = new DateTime(2026, 3, 28, 0, 0, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2026, 3, 28, 0, 0, 0, DateTimeKind.Utc)
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.True(result.ConflictCount == 0,
            string.Join(" | ", result.Conflicts.Select(conflict => $"{conflict.EntityName}:{conflict.Reason}")));

        var storedProfile = await _dbContext.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync();
        Assert.NotNull(storedProfile.CustomerId);
        Assert.Equal("한길노무법인", storedProfile.CustomerName);
        Assert.Equal("998-87-76655", storedProfile.BusinessNumber);
    }

    [Fact]
    public async Task Push_DoesNotAttachDepartmentBillingProfile_ToParentCustomer()
    {
        var parentCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Waterworks",
            NameMatchKey = "WATERWORKS",
            TradeType = "매출",
            BusinessNumber = "131-83-01359"
        };
        _dbContext.Customers.Add(parentCustomer);
        _dbContext.RentalManagementCompanies.Add(new RentalManagementCompany
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            Code = OfficeCodeCatalog.Usenet,
            Name = "유즈넷"
        });
        await _dbContext.SaveChangesAsync();

        var request = new SyncPushRequest
        {
            RentalBillingProfiles =
            [
                new RentalBillingProfileDto
                {
                    Id = Guid.NewGuid(),
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    ProfileKey = "USENET|CUSTOMER:PARENT|NAME:WATERWORKSQUALITY|묶음|후불|25|1|전자세금계산서",
                    CustomerId = parentCustomer.Id,
                    CustomerName = "Waterworks[Quality]",
                    BusinessNumber = parentCustomer.BusinessNumber,
                    ItemName = "IMC2000",
                    BillingType = "묶음",
                    BillingAdvanceMode = "후불",
                    BillingDay = 25,
                    BillingCycleMonths = 1,
                    BillingMethod = "전자세금계산서",
                    MonthlyAmount = 55000m,
                    CreatedAtUtc = new DateTime(2026, 4, 21, 0, 0, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2026, 4, 21, 0, 0, 0, DateTimeKind.Utc)
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.True(result.ConflictCount == 0,
            string.Join(" | ", result.Conflicts.Select(conflict => $"{conflict.EntityName}:{conflict.Reason}")));

        var storedProfile = await _dbContext.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync();
        Assert.Null(storedProfile.CustomerId);
        Assert.Equal("Waterworks[Quality]", storedProfile.CustomerName);
    }

    [Fact]
    public async Task Push_RelinksDepartmentBillingProfile_ToExactDepartmentCustomer()
    {
        var parentCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Waterworks",
            NameMatchKey = "WATERWORKS",
            TradeType = "매출",
            BusinessNumber = "131-83-01359"
        };
        var departmentCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Waterworks[Quality]",
            NameMatchKey = "WATERWORKSQUALITY",
            TradeType = "매출",
            BusinessNumber = parentCustomer.BusinessNumber
        };
        _dbContext.Customers.AddRange(parentCustomer, departmentCustomer);
        _dbContext.RentalManagementCompanies.Add(new RentalManagementCompany
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            Code = OfficeCodeCatalog.Usenet,
            Name = "유즈넷"
        });
        await _dbContext.SaveChangesAsync();

        var request = new SyncPushRequest
        {
            RentalBillingProfiles =
            [
                new RentalBillingProfileDto
                {
                    Id = Guid.NewGuid(),
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    ProfileKey = "USENET|CUSTOMER:PARENT|NAME:WATERWORKSQUALITY|묶음|후불|25|1|전자세금계산서",
                    CustomerId = parentCustomer.Id,
                    CustomerName = departmentCustomer.NameOriginal,
                    BusinessNumber = parentCustomer.BusinessNumber,
                    ItemName = "IMC2000",
                    BillingType = "묶음",
                    BillingAdvanceMode = "후불",
                    BillingDay = 25,
                    BillingCycleMonths = 1,
                    BillingMethod = "전자세금계산서",
                    MonthlyAmount = 55000m,
                    CreatedAtUtc = new DateTime(2026, 4, 21, 0, 0, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2026, 4, 21, 0, 0, 0, DateTimeKind.Utc)
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.True(result.ConflictCount == 0,
            string.Join(" | ", result.Conflicts.Select(conflict => $"{conflict.EntityName}:{conflict.Reason}")));

        var storedProfile = await _dbContext.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(departmentCustomer.Id, storedProfile.CustomerId);
        Assert.Equal(departmentCustomer.NameOriginal, storedProfile.CustomerName);
    }

    [Fact]
    public async Task Push_AcceptsRentalAsset_WhenReferencedBillingProfileIsInSameBatch()
    {
        var companyId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var createdAtUtc = new DateTime(2026, 3, 28, 0, 0, 0, DateTimeKind.Utc);

        var request = new SyncPushRequest
        {
            RentalManagementCompanies =
            [
                new RentalManagementCompanyDto
                {
                    Id = companyId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    Code = OfficeCodeCatalog.Usenet,
                    Name = "유즈넷",
                    CreatedAtUtc = createdAtUtc,
                    UpdatedAtUtc = createdAtUtc
                }
            ],
            RentalBillingProfiles =
            [
                new RentalBillingProfileDto
                {
                    Id = profileId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ProfileKey = "USENET|1234509876|동시등록렌탈거래처||MXC3000",
                    CustomerName = "동시등록렌탈거래처",
                    BusinessNumber = "123-45-09876",
                    ItemName = "MXC3000",
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    BillingDay = 10,
                    MonthlyAmount = 88000m,
                    CreatedAtUtc = createdAtUtc,
                    UpdatedAtUtc = createdAtUtc
                }
            ],
            RentalAssets =
            [
                new RentalAssetDto
                {
                    Id = assetId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    ManagementId = "501",
                    ManagementNumber = "2603-501",
                    AssetKey = "USENET|2603-501|501|동시등록렌탈거래처|MXC3000",
                    CustomerName = "동시등록렌탈거래처",
                    ItemName = "MXC3000",
                    BillingProfileId = profileId,
                    CurrentLocation = "렌탈",
                    CreatedAtUtc = createdAtUtc,
                    UpdatedAtUtc = createdAtUtc
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.True(result.ConflictCount == 0,
            string.Join(" | ", result.Conflicts.Select(conflict => $"{conflict.EntityName}:{conflict.Reason}")));

        var storedAsset = await _dbContext.RentalAssets.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(assetId, storedAsset.Id);
        Assert.Equal(profileId, storedAsset.BillingProfileId);
        Assert.Equal("2603-501", storedAsset.ManagementNumber);
    }

    [Fact]
    public async Task Push_RejectsRentalAssetBillingProfile_WhenProfileIsReadSharedButNotWritable()
    {
        var sharedReadOnlyProfileId = Guid.NewGuid();
        _dbContext.DataSharingPolicies.Add(new DataSharingPolicy
        {
            Id = Guid.NewGuid(),
            SourceTenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Yeonsu,
            TargetTenantCode = TenantScopeCatalog.UsenetGroup,
            TargetOfficeCode = OfficeCodeCatalog.Usenet,
            ShareRentals = true,
            AllowTargetWrite = false,
            Note = "read-only rental share for rental asset profile link scope test"
        });
        _dbContext.Customers.Add(new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Read Shared Rental Asset Customer",
            NameMatchKey = "READSHAREDRENTALASSETCUSTOMER",
            TradeType = "매출"
        });
        _dbContext.RentalBillingProfiles.Add(new RentalBillingProfile
        {
            Id = sharedReadOnlyProfileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            ProfileKey = "READ-SHARED-ASSET-PROFILE",
            CustomerName = "Read Shared Rental Asset Customer",
            ItemName = "ASSET-PRINTER-READ-SHARED",
            InstallSiteName = "Read Shared Install Site",
            BillingDay = 10,
            MonthlyAmount = 30000m,
            CreatedAtUtc = new DateTime(2026, 6, 17, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 6, 17, 0, 0, 0, DateTimeKind.Utc)
        });
        await _dbContext.SaveChangesAsync();

        var currentUser = new TestCurrentUserContext
        {
            Username = "usenet-rental-asset-editor",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = [PermissionNames.RentalAssetEdit]
        };
        await using var scopedDb = CreateDbContext(currentUser);
        var controller = CreateController(scopedDb, currentUser);
        var assetId = Guid.NewGuid();

        var response = await controller.Push(new SyncPushRequest
        {
            DeviceId = "device-rental-asset-read-shared-profile",
            RentalAssets =
            [
                new RentalAssetDto
                {
                    Id = assetId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    ManagementId = "601",
                    ManagementNumber = "2606-601",
                    AssetKey = "USENET|2606-601|601|Read Shared Rental Asset Customer|ASSET-PRINTER-READ-SHARED",
                    CustomerName = "Read Shared Rental Asset Customer",
                    CurrentCustomerName = "Read Shared Rental Asset Customer",
                    ItemName = "ASSET-PRINTER-READ-SHARED",
                    InstallLocation = "Read Shared Install Site",
                    InstallSiteName = "Read Shared Install Site",
                    BillingProfileId = sharedReadOnlyProfileId,
                    CurrentLocation = "설치",
                    CreatedAtUtc = new DateTime(2026, 6, 17, 0, 1, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2026, 6, 17, 0, 1, 0, DateTimeKind.Utc)
                }
            ]
        }, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            conflict.EntityName == nameof(RentalAsset) &&
            conflict.Reason.Contains("outside the writable office scope", StringComparison.OrdinalIgnoreCase));
        Assert.False(await scopedDb.RentalAssets.IgnoreQueryFilters().AnyAsync(asset => asset.Id == assetId));
    }

    [Fact]
    public async Task Push_ReusesExistingRentalAssetBillingProfile_WhenIncomingBillingProfileIdIsStale()
    {
        _dbContext.RentalManagementCompanies.Add(new RentalManagementCompany
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            Code = OfficeCodeCatalog.Usenet,
            Name = "유즈넷"
        });

        var existingProfile = new RentalBillingProfile
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ProfileKey = "USENET|9988776655|한길노무법인||IMC2010",
            CustomerName = "한길노무법인",
            BusinessNumber = "998-87-76655",
            ItemName = "IMC2010",
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            BillingDay = 25,
            MonthlyAmount = 44000m,
            CreatedAtUtc = new DateTime(2026, 3, 27, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 3, 27, 0, 0, 0, DateTimeKind.Utc)
        };
        var existingAsset = new RentalAsset
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            AssetKey = "USENET|2603-233|233|한길노무법인|IMC2010",
            ManagementId = "233",
            ManagementNumber = "2603-233",
            CustomerName = "한길노무법인",
            ItemName = "IMC2010",
            BillingProfileId = existingProfile.Id,
            CurrentLocation = "렌탈",
            CreatedAtUtc = new DateTime(2026, 3, 27, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 3, 27, 0, 0, 0, DateTimeKind.Utc)
        };

        _dbContext.RentalBillingProfiles.Add(existingProfile);
        _dbContext.RentalAssets.Add(existingAsset);
        await _dbContext.SaveChangesAsync();

        var request = new SyncPushRequest
        {
            RentalAssets =
            [
                new RentalAssetDto
                {
                    Id = Guid.NewGuid(),
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    ManagementId = existingAsset.ManagementId,
                    ManagementNumber = existingAsset.ManagementNumber,
                    CustomerName = existingAsset.CustomerName,
                    ItemName = existingAsset.ItemName,
                    BillingProfileId = Guid.NewGuid(),
                    CurrentLocation = "창고",
                    CreatedAtUtc = existingAsset.CreatedAtUtc,
                    UpdatedAtUtc = existingAsset.UpdatedAtUtc.AddMinutes(3)
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.Equal(0, result.ConflictCount);

        var storedAsset = await _dbContext.RentalAssets.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(existingAsset.Id, storedAsset.Id);
        Assert.Equal(existingProfile.Id, storedAsset.BillingProfileId);
        Assert.Equal("창고", storedAsset.CurrentLocation);
    }


    [Fact]
    public async Task Push_ReassignsRentalAssetBillingProfile_ToSameScopeProfile_WhenIncomingReferenceUsesDifferentOffice()
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            NameOriginal = "YEONSU-CUSTOMER",
            NameMatchKey = "YEONSUCUSTOMER",
            TradeType = "매출"
        };

        var wrongScopeProfile = new RentalBillingProfile
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ProfileKey = "PROFILE-USENET",
            CustomerId = customer.Id,
            CustomerName = customer.NameOriginal,
            ItemName = "IMC2010",
            InstallSiteName = "Main Office",
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            BillingDay = 25,
            MonthlyAmount = 44000m
        };
        var correctScopeProfile = new RentalBillingProfile
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            ProfileKey = "PROFILE-YEONSU",
            CustomerId = customer.Id,
            CustomerName = customer.NameOriginal,
            ItemName = "IMC2010",
            InstallSiteName = "Main Office",
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            BillingDay = 25,
            MonthlyAmount = 44000m
        };
        var existingAsset = new RentalAsset
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            AssetKey = "USENET|2603-310|310|YEONSU-CUSTOMER|IMC2010",
            ManagementId = "310",
            ManagementNumber = "2603-310",
            CustomerId = customer.Id,
            CustomerName = customer.NameOriginal,
            CurrentCustomerName = customer.NameOriginal,
            ItemName = "IMC2010",
            InstallLocation = "Main Office",
            InstallSiteName = "Main Office",
            BillingProfileId = wrongScopeProfile.Id,
            CurrentLocation = "Room A",
            CreatedAtUtc = new DateTime(2026, 3, 28, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 3, 28, 0, 0, 0, DateTimeKind.Utc)
        };

        _dbContext.Customers.Add(customer);
        _dbContext.RentalBillingProfiles.AddRange(wrongScopeProfile, correctScopeProfile);
        _dbContext.RentalAssets.Add(existingAsset);
        await _dbContext.SaveChangesAsync();

        var request = new SyncPushRequest
        {
            RentalAssets =
            [
                new RentalAssetDto
                {
                    Id = existingAsset.Id,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    ManagementId = existingAsset.ManagementId,
                    ManagementNumber = existingAsset.ManagementNumber,
                    CustomerName = customer.NameOriginal,
                    CurrentCustomerName = customer.NameOriginal,
                    ItemName = existingAsset.ItemName,
                    InstallLocation = existingAsset.InstallLocation,
                    InstallSiteName = existingAsset.InstallSiteName,
                    BillingProfileId = wrongScopeProfile.Id,
                    CurrentLocation = "Room B",
                    CreatedAtUtc = existingAsset.CreatedAtUtc,
                    UpdatedAtUtc = existingAsset.UpdatedAtUtc.AddMinutes(5)
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.True(result.ConflictCount == 0,
            string.Join(" | ", result.Conflicts.Select(conflict => $"{conflict.EntityName}:{conflict.Reason}")));

        var storedAsset = await _dbContext.RentalAssets.IgnoreQueryFilters().SingleAsync(x => x.Id == existingAsset.Id);
        Assert.Equal(correctScopeProfile.Id, storedAsset.BillingProfileId);
        Assert.Equal(OfficeCodeCatalog.Yeonsu, storedAsset.ResponsibleOfficeCode);
        Assert.Equal("Room B", storedAsset.CurrentLocation);
    }

    [Fact]
    public async Task Push_AllowsRentalAssetReference_WhenBillingProfileCustomerIdIsMissingButNamesMatch()
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "상수도사업본부 중부수도사업소",
            NameMatchKey = "상수도사업본부중부수도사업소",
            TradeType = "매출"
        };
        var profile = new RentalBillingProfile
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ProfileKey = "USENET||상수도사업소중부수도사업소|묶음|후불|25|12||",
            CustomerId = null,
            CustomerName = "[상수도사업소]중부수도사업소",
            ItemName = "SL-M3820ND",
            InstallSiteName = "[상수도사업소]중부수도사업소",
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            BillingDay = 25,
            MonthlyAmount = 120000m
        };

        _dbContext.Customers.Add(customer);
        _dbContext.RentalBillingProfiles.Add(profile);
        await _dbContext.SaveChangesAsync();

        var request = new SyncPushRequest
        {
            RentalAssets =
            [
                new RentalAssetDto
                {
                    Id = Guid.NewGuid(),
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    ManagementId = "261",
                    ManagementNumber = "2603-261",
                    CustomerId = customer.Id,
                    CustomerName = customer.NameOriginal,
                    CurrentCustomerName = customer.NameOriginal,
                    ItemName = "SL-M3820ND",
                    InstallLocation = customer.NameOriginal,
                    InstallSiteName = customer.NameOriginal,
                    BillingProfileId = profile.Id,
                    CurrentLocation = "요금팀",
                    CreatedAtUtc = new DateTime(2026, 3, 28, 0, 0, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2026, 3, 28, 0, 5, 0, DateTimeKind.Utc)
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.True(result.ConflictCount == 0,
            string.Join(" | ", result.Conflicts.Select(conflict => $"{conflict.EntityName}:{conflict.Reason}")));

        var storedAsset = await _dbContext.RentalAssets.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(profile.Id, storedAsset.BillingProfileId);
        Assert.Equal(customer.Id, storedAsset.CustomerId);
    }

    [Fact]
    public async Task Push_ResolvesRentalAssetBillingProfileByName_WhenCustomerIdMatchesButProfileCustomerIdIsMissing()
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "[연수구]보건소-건강증진과",
            NameMatchKey = "연수구보건소건강증진과",
            TradeType = "매출"
        };
        var existingProfile = new RentalBillingProfile
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ProfileKey = "USENET||연수구보건소-건강증진과|묶음|후불|25|24||",
            CustomerId = null,
            CustomerName = customer.NameOriginal,
            ItemName = "IMC2010",
            InstallSiteName = customer.NameOriginal,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            BillingDay = 25,
            MonthlyAmount = 240000m
        };

        _dbContext.Customers.Add(customer);
        _dbContext.RentalBillingProfiles.Add(existingProfile);
        await _dbContext.SaveChangesAsync();

        var request = new SyncPushRequest
        {
            RentalAssets =
            [
                new RentalAssetDto
                {
                    Id = Guid.NewGuid(),
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    ManagementId = "230",
                    ManagementNumber = "2603-230",
                    CustomerId = customer.Id,
                    CustomerName = customer.NameOriginal,
                    CurrentCustomerName = customer.NameOriginal,
                    ItemName = "IMC2010",
                    InstallLocation = "영양플러스실",
                    InstallSiteName = customer.NameOriginal,
                    BillingProfileId = Guid.NewGuid(),
                    CurrentLocation = "영양플러스실",
                    CreatedAtUtc = new DateTime(2026, 3, 28, 0, 0, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2026, 3, 28, 0, 10, 0, DateTimeKind.Utc)
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.True(result.ConflictCount == 0,
            string.Join(" | ", result.Conflicts.Select(conflict => $"{conflict.EntityName}:{conflict.Reason}")));

        var storedAsset = await _dbContext.RentalAssets.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(existingProfile.Id, storedAsset.BillingProfileId);
        Assert.Equal(customer.Id, storedAsset.CustomerId);
    }

    [Fact]
    public async Task Pull_DoesNotIncludeCrossTenantRentalData_ForUserWithRentalEditAll()
    {
        var usenetCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "USENET-RENTAL-CUSTOMER",
            NameMatchKey = "USENETRENTALCUSTOMER",
            TradeType = "매출"
        };
        var itworldCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
            NameOriginal = "ITWORLD-RENTAL-CUSTOMER",
            NameMatchKey = "ITWORLDRENTALCUSTOMER",
            TradeType = "매출"
        };
        _dbContext.Customers.AddRange(usenetCustomer, itworldCustomer);

        _dbContext.RentalManagementCompanies.AddRange(
            new RentalManagementCompany
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                Code = OfficeCodeCatalog.Usenet,
                Name = "USENET"
            },
            new RentalManagementCompany
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.Itworld,
                Code = OfficeCodeCatalog.Itworld,
                Name = "ITWORLD"
            });

        _dbContext.RentalAssets.AddRange(
            new RentalAsset
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                AssetKey = "USENET|2603-901|901|USENET-RENTAL-CUSTOMER|MODEL-U",
                ManagementId = "901",
                ManagementNumber = "2603-901",
                CustomerId = usenetCustomer.Id,
                CustomerName = usenetCustomer.NameOriginal,
                ItemName = "MODEL-U",
                CurrentLocation = "USENET-ROOM",
                CreatedAtUtc = new DateTime(2026, 4, 7, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAtUtc = new DateTime(2026, 4, 7, 0, 0, 0, DateTimeKind.Utc)
            },
            new RentalAsset
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                ManagementCompanyCode = OfficeCodeCatalog.Itworld,
                AssetKey = "ITWORLD|2603-902|902|ITWORLD-RENTAL-CUSTOMER|MODEL-I",
                ManagementId = "902",
                ManagementNumber = "2603-902",
                CustomerId = itworldCustomer.Id,
                CustomerName = itworldCustomer.NameOriginal,
                ItemName = "MODEL-I",
                CurrentLocation = "ITWORLD-ROOM",
                CreatedAtUtc = new DateTime(2026, 4, 7, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAtUtc = new DateTime(2026, 4, 7, 0, 0, 0, DateTimeKind.Utc)
            });
        await _dbContext.SaveChangesAsync();

        var currentUser = new TestCurrentUserContext
        {
            Username = "itworld_user",
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ScopeType = TenantScopeCatalog.ScopeTenantAll,
            Permissions = [거래플랜.Server.Api.Security.PermissionNames.RentalEditAll]
        };
        await using var scopedDb = CreateDbContext(currentUser);
        var controller = CreateController(scopedDb, currentUser);

        var response = await controller.Pull(0, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPullResponse>(ok.Value);

        Assert.DoesNotContain(result.Customers, customer => customer.Id == usenetCustomer.Id);
        Assert.Contains(result.Customers, customer => customer.Id == itworldCustomer.Id);
        Assert.DoesNotContain(result.RentalAssets, asset => asset.ManagementNumber == "2603-901");
        Assert.Contains(result.RentalAssets, asset => asset.ManagementNumber == "2603-902");
    }

    [Fact]
    public async Task Pull_ReturnsCustomerContractMetadataWithoutFileContent()
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "SYNC-CONTRACT-CUSTOMER",
            NameMatchKey = "SYNCCONTRACTCUSTOMER",
            TradeType = "매출",
            Revision = 10
        };
        var contract = new CustomerContract
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            ContractType = "거래계약서",
            FileName = "contract.pdf",
            MimeType = "application/pdf",
            FileSize = 4,
            FileHash = "hash",
            FileContent = [0x25, 0x50, 0x44, 0x46],
            IsPrimary = true,
            UploadedByUsername = "admin",
            UploadedAtUtc = DateTime.UtcNow,
            Revision = 11
        };
        _dbContext.Customers.Add(customer);
        _dbContext.CustomerContracts.Add(contract);
        await _dbContext.SaveChangesAsync();

        var response = await _controller.Pull(0, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPullResponse>(ok.Value);
        var pulled = Assert.Single(result.CustomerContracts, current => current.Id == contract.Id);

        Assert.Equal("contract.pdf", pulled.FileName);
        Assert.Equal(4, pulled.FileSize);
        Assert.Empty(pulled.FileContent);
    }

    [Fact]
    public async Task Pull_OfficeOnlyUser_ReturnsOnlyResponsiblePaymentAndRentalRows()
    {
        var visibleCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            NameOriginal = "SYNC-VISIBLE-CUSTOMER",
            NameMatchKey = "SYNCVISIBLECUSTOMER",
            TradeType = "매출"
        };
        var hiddenCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "SYNC-HIDDEN-CUSTOMER",
            NameMatchKey = "SYNCHIDDENCUSTOMER",
            TradeType = "매출"
        };
        var visibleInvoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = visibleCustomer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            InvoiceNumber = "SYNC-PAY-VISIBLE",
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 5, 26)
        };
        var hiddenInvoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = hiddenCustomer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "SYNC-PAY-HIDDEN",
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 5, 26)
        };
        var visibleProfile = new RentalBillingProfile
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            ProfileKey = "SYNC-RENTAL-VISIBLE",
            CustomerId = visibleCustomer.Id,
            CustomerName = visibleCustomer.NameOriginal
        };
        var hiddenProfile = new RentalBillingProfile
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ProfileKey = "SYNC-RENTAL-HIDDEN",
            CustomerId = hiddenCustomer.Id,
            CustomerName = hiddenCustomer.NameOriginal
        };
        var visibleAsset = new RentalAsset
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            AssetKey = "SYNC-ASSET-VISIBLE",
            ManagementNumber = "SYNC-ASSET-VISIBLE",
            CustomerId = visibleCustomer.Id,
            BillingProfileId = visibleProfile.Id
        };
        var hiddenAsset = new RentalAsset
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            AssetKey = "SYNC-ASSET-HIDDEN",
            ManagementNumber = "SYNC-ASSET-HIDDEN",
            CustomerId = hiddenCustomer.Id,
            BillingProfileId = hiddenProfile.Id
        };

        _dbContext.Customers.AddRange(visibleCustomer, hiddenCustomer);
        _dbContext.Invoices.AddRange(visibleInvoice, hiddenInvoice);
        _dbContext.Payments.AddRange(
            new Payment
            {
                Id = Guid.NewGuid(),
                InvoiceId = visibleInvoice.Id,
                PaymentDate = new DateOnly(2026, 5, 27),
                Amount = 100m,
                Note = "SYNC-PAYMENT-VISIBLE"
            },
            new Payment
            {
                Id = Guid.NewGuid(),
                InvoiceId = hiddenInvoice.Id,
                PaymentDate = new DateOnly(2026, 5, 27),
                Amount = 100m,
                Note = "SYNC-PAYMENT-HIDDEN"
            });
        _dbContext.Transactions.AddRange(
            new TransactionRecord
            {
                Id = Guid.NewGuid(),
                CustomerId = visibleCustomer.Id,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                TransactionKind = "SYNC-TRANSACTION-VISIBLE",
                ReceiptTotal = 100m
            },
            new TransactionRecord
            {
                Id = Guid.NewGuid(),
                CustomerId = hiddenCustomer.Id,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionKind = "SYNC-TRANSACTION-HIDDEN",
                ReceiptTotal = 100m
            });
        _dbContext.RentalBillingProfiles.AddRange(visibleProfile, hiddenProfile);
        _dbContext.RentalAssets.AddRange(visibleAsset, hiddenAsset);
        _dbContext.RentalAssetAssignmentHistories.AddRange(
            new RentalAssetAssignmentHistory
            {
                Id = Guid.NewGuid(),
                AssetId = visibleAsset.Id,
                BillingProfileId = visibleProfile.Id,
                CustomerId = visibleCustomer.Id,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                ManagementNumber = "SYNC-ASSET-VISIBLE"
            },
            new RentalAssetAssignmentHistory
            {
                Id = Guid.NewGuid(),
                AssetId = hiddenAsset.Id,
                BillingProfileId = hiddenProfile.Id,
                CustomerId = hiddenCustomer.Id,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementNumber = "SYNC-ASSET-HIDDEN"
            });
        _dbContext.RentalBillingLogs.AddRange(
            new RentalBillingLog
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                BillingProfileId = visibleProfile.Id,
                BillingYearMonth = "2026-05",
                BilledAmount = 100m
            },
            new RentalBillingLog
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                BillingProfileId = hiddenProfile.Id,
                BillingYearMonth = "2026-06",
                BilledAmount = 100m
            });
        await _dbContext.SaveChangesAsync();

        var currentUser = new TestCurrentUserContext
        {
            Username = "yeonsu_sync_user",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly
        };
        await using var scopedDb = CreateDbContext(currentUser);
        var controller = CreateController(scopedDb, currentUser);

        var response = await controller.Pull(0, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPullResponse>(ok.Value);

        Assert.Contains(result.Customers, customer => customer.Id == visibleCustomer.Id);
        Assert.DoesNotContain(result.Customers, customer => customer.Id == hiddenCustomer.Id);
        Assert.Contains(result.Invoices, invoice => invoice.InvoiceNumber == "SYNC-PAY-VISIBLE");
        Assert.DoesNotContain(result.Invoices, invoice => invoice.InvoiceNumber == "SYNC-PAY-HIDDEN");
        Assert.Contains(result.Payments, payment => payment.Note == "SYNC-PAYMENT-VISIBLE");
        Assert.DoesNotContain(result.Payments, payment => payment.Note == "SYNC-PAYMENT-HIDDEN");
        Assert.Contains(result.Transactions, transaction => transaction.TransactionKind == "SYNC-TRANSACTION-VISIBLE");
        Assert.DoesNotContain(result.Transactions, transaction => transaction.TransactionKind == "SYNC-TRANSACTION-HIDDEN");
        Assert.Contains(result.RentalBillingProfiles, profile => profile.ProfileKey == "SYNC-RENTAL-VISIBLE");
        Assert.DoesNotContain(result.RentalBillingProfiles, profile => profile.ProfileKey == "SYNC-RENTAL-HIDDEN");
        Assert.Contains(result.RentalAssets, asset => asset.ManagementNumber == "SYNC-ASSET-VISIBLE");
        Assert.DoesNotContain(result.RentalAssets, asset => asset.ManagementNumber == "SYNC-ASSET-HIDDEN");
        Assert.Contains(result.RentalAssetAssignmentHistories, history => history.ManagementNumber == "SYNC-ASSET-VISIBLE");
        Assert.DoesNotContain(result.RentalAssetAssignmentHistories, history => history.ManagementNumber == "SYNC-ASSET-HIDDEN");
        Assert.Contains(result.RentalBillingLogs, log => log.BillingYearMonth == "2026-05");
        Assert.DoesNotContain(result.RentalBillingLogs, log => log.BillingYearMonth == "2026-06");
    }

    [Fact]
    public async Task Push_RejectsRentalBillingLog_WhenProfileIsReadSharedButNotWritable()
    {
        var sharedReadOnlyProfileId = Guid.NewGuid();
        _dbContext.DataSharingPolicies.Add(new DataSharingPolicy
        {
            Id = Guid.NewGuid(),
            SourceTenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Yeonsu,
            TargetTenantCode = TenantScopeCatalog.UsenetGroup,
            TargetOfficeCode = OfficeCodeCatalog.Usenet,
            ShareRentals = true,
            AllowTargetWrite = false,
            Note = "read-only rental share for billing log scope test"
        });
        _dbContext.RentalBillingProfiles.Add(new RentalBillingProfile
        {
            Id = sharedReadOnlyProfileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            ProfileKey = "READ-ONLY-RENTAL-PROFILE",
            CustomerName = "Read Only Rental Customer",
            BillingDay = 10,
            MonthlyAmount = 30000m,
            CreatedAtUtc = new DateTime(2026, 6, 17, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 6, 17, 0, 0, 0, DateTimeKind.Utc)
        });
        await _dbContext.SaveChangesAsync();

        var currentUser = new TestCurrentUserContext
        {
            Username = "usenet-rental-profile-editor",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = [PermissionNames.RentalProfileEdit]
        };
        await using var scopedDb = CreateDbContext(currentUser);
        var controller = CreateController(scopedDb, currentUser);
        var logId = Guid.NewGuid();

        var response = await controller.Push(new SyncPushRequest
        {
            DeviceId = "device-rental-billing-log-read-shared",
            RentalBillingLogs =
            [
                new RentalBillingLogDto
                {
                    Id = logId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    BillingProfileId = sharedReadOnlyProfileId,
                    BillingYearMonth = "2026-06",
                    ScheduledDate = new DateOnly(2026, 6, 10),
                    Status = "예정",
                    BilledAmount = 30000m,
                    Note = "attempted read-shared write",
                    CreatedAtUtc = new DateTime(2026, 6, 17, 0, 1, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2026, 6, 17, 0, 1, 0, DateTimeKind.Utc)
                }
            ]
        }, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            conflict.EntityName == nameof(RentalBillingLog) &&
            conflict.Reason.Contains("outside the writable office scope", StringComparison.OrdinalIgnoreCase));
        Assert.False(await scopedDb.RentalBillingLogs.IgnoreQueryFilters().AnyAsync(log => log.Id == logId));
    }

    [Fact]
    public async Task Push_RejectsNewRentalBillingProfile_WhenLinkedCustomerResolvesToReadSharedOffice()
    {
        var sharedReadOnlyCustomerId = Guid.NewGuid();
        _dbContext.DataSharingPolicies.Add(new DataSharingPolicy
        {
            Id = Guid.NewGuid(),
            SourceTenantCode = TenantScopeCatalog.UsenetGroup,
            SourceOfficeCode = OfficeCodeCatalog.Yeonsu,
            TargetTenantCode = TenantScopeCatalog.UsenetGroup,
            TargetOfficeCode = OfficeCodeCatalog.Usenet,
            ShareCustomers = true,
            ShareRentals = true,
            AllowTargetWrite = false,
            Note = "read-only rental/customer share for profile create scope test"
        });
        _dbContext.Customers.Add(new Customer
        {
            Id = sharedReadOnlyCustomerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            NameOriginal = "Read Shared Rental Customer",
            NameMatchKey = "READSHAREDRENTALCUSTOMER",
            TradeType = "매출",
            BusinessNumber = "777-88-99990"
        });
        _dbContext.RentalManagementCompanies.AddRange(
            new RentalManagementCompany
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                Code = OfficeCodeCatalog.Usenet,
                Name = "유즈넷 렌탈"
            },
            new RentalManagementCompany
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                Code = OfficeCodeCatalog.Yeonsu,
                Name = "연수 렌탈"
            });
        await _dbContext.SaveChangesAsync();

        var currentUser = new TestCurrentUserContext
        {
            Username = "usenet-rental-profile-create",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = [PermissionNames.RentalProfileEdit]
        };
        await using var scopedDb = CreateDbContext(currentUser);
        var controller = CreateController(scopedDb, currentUser);
        var profileId = Guid.NewGuid();

        var response = await controller.Push(new SyncPushRequest
        {
            DeviceId = "device-rental-profile-read-shared-customer",
            RentalBillingProfiles =
            [
                new RentalBillingProfileDto
                {
                    Id = profileId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    ProfileKey = "USENET|7778899990|Read Shared Rental Customer||RS-1000",
                    CustomerId = sharedReadOnlyCustomerId,
                    CustomerName = "Read Shared Rental Customer",
                    BusinessNumber = "777-88-99990",
                    ItemName = "RS-1000",
                    BillingDay = 10,
                    MonthlyAmount = 50000m,
                    CreatedAtUtc = new DateTime(2026, 6, 17, 0, 2, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2026, 6, 17, 0, 2, 0, DateTimeKind.Utc)
                }
            ]
        }, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            conflict.EntityName == nameof(RentalBillingProfile) &&
            conflict.Reason.Contains("outside the writable office scope", StringComparison.OrdinalIgnoreCase));
        Assert.False(await scopedDb.RentalBillingProfiles.IgnoreQueryFilters().AnyAsync(profile => profile.Id == profileId));
    }

    [Fact]
    public async Task Push_RejectsRentalBillingProfile_WhenExplicitCustomerIdExistsOutsideReadableScope()
    {
        var outsideCustomerId = Guid.NewGuid();
        _dbContext.Customers.Add(new Customer
        {
            Id = outsideCustomerId,
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
            NameOriginal = "ITWORLD-HIDDEN-RENTAL-PROFILE-CUSTOMER",
            NameMatchKey = "ITWORLDHIDDENRENTALPROFILECUSTOMER",
            TradeType = "매출",
            BusinessNumber = "123-45-67890"
        });
        _dbContext.RentalManagementCompanies.Add(new RentalManagementCompany
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            Code = OfficeCodeCatalog.Usenet,
            Name = "유즈넷 렌탈"
        });
        await _dbContext.SaveChangesAsync();

        var currentUser = new TestCurrentUserContext
        {
            Username = "usenet-rental-profile-customer-scope",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = [PermissionNames.RentalProfileEdit]
        };
        await using var scopedDb = CreateDbContext(currentUser);
        var controller = CreateController(scopedDb, currentUser);
        var profileId = Guid.NewGuid();

        var response = await controller.Push(new SyncPushRequest
        {
            DeviceId = "device-rental-profile-outside-customer",
            RentalBillingProfiles =
            [
                new RentalBillingProfileDto
                {
                    Id = profileId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    ProfileKey = "USENET|1234567890|ITWORLD-HIDDEN-RENTAL-PROFILE-CUSTOMER||OUT-1000",
                    CustomerId = outsideCustomerId,
                    CustomerName = "ITWORLD-HIDDEN-RENTAL-PROFILE-CUSTOMER",
                    BusinessNumber = "123-45-67890",
                    ItemName = "OUT-1000",
                    BillingDay = 10,
                    MonthlyAmount = 50000m,
                    CreatedAtUtc = new DateTime(2026, 6, 19, 0, 2, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2026, 6, 19, 0, 2, 0, DateTimeKind.Utc)
                }
            ]
        }, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            conflict.EntityName == nameof(RentalBillingProfile) &&
            conflict.Reason.Contains("outside the readable office scope", StringComparison.OrdinalIgnoreCase));
        Assert.False(await scopedDb.RentalBillingProfiles.IgnoreQueryFilters().AnyAsync(profile => profile.Id == profileId));
    }


    [Fact]
    public async Task Pull_AdminRentalUser_KeepsCustomerMirrorScoped_ButStillReceivesCrossTenantRentalAssets()
    {
        var usenetCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "USENET-ADMIN-RENTAL-CUSTOMER",
            NameMatchKey = "USENETADMINRENTALCUSTOMER",
            TradeType = "매출"
        };
        var itworldCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
            NameOriginal = "ITWORLD-ADMIN-RENTAL-CUSTOMER",
            NameMatchKey = "ITWORLDADMINRENTALCUSTOMER",
            TradeType = "매출"
        };
        _dbContext.Customers.AddRange(usenetCustomer, itworldCustomer);
        _dbContext.Items.AddRange(
            new Item
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "USENET-ADMIN-ITEM",
                NameMatchKey = "USENETADMINITEM"
            },
            new Item
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                NameOriginal = "ITWORLD-ADMIN-ITEM",
                NameMatchKey = "ITWORLDADMINITEM"
            });

        _dbContext.RentalAssets.AddRange(
            new RentalAsset
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                AssetKey = "USENET|2603-911|911|USENET-ADMIN-RENTAL-CUSTOMER|MODEL-U",
                ManagementId = "911",
                ManagementNumber = "2603-911",
                CustomerId = usenetCustomer.Id,
                CustomerName = usenetCustomer.NameOriginal,
                ItemName = "MODEL-U",
                CurrentLocation = "USENET-ROOM",
                CreatedAtUtc = new DateTime(2026, 4, 8, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAtUtc = new DateTime(2026, 4, 8, 0, 0, 0, DateTimeKind.Utc)
            },
            new RentalAsset
            {
                Id = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                ManagementCompanyCode = OfficeCodeCatalog.Itworld,
                AssetKey = "ITWORLD|2603-912|912|ITWORLD-ADMIN-RENTAL-CUSTOMER|MODEL-I",
                ManagementId = "912",
                ManagementNumber = "2603-912",
                CustomerId = itworldCustomer.Id,
                CustomerName = itworldCustomer.NameOriginal,
                ItemName = "MODEL-I",
                CurrentLocation = "ITWORLD-ROOM",
                CreatedAtUtc = new DateTime(2026, 4, 8, 0, 0, 0, DateTimeKind.Utc),
                UpdatedAtUtc = new DateTime(2026, 4, 8, 0, 0, 0, DateTimeKind.Utc)
            });
        await _dbContext.SaveChangesAsync();

        var currentUser = new TestCurrentUserContext
        {
            Username = "itworld_admin",
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            IsAdmin = true
        };
        await using var scopedDb = CreateDbContext(currentUser);
        var controller = CreateController(scopedDb, currentUser);

        var response = await controller.Pull(0, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPullResponse>(ok.Value);

        Assert.DoesNotContain(result.Customers, customer => customer.Id == usenetCustomer.Id);
        Assert.Contains(result.Customers, customer => customer.Id == itworldCustomer.Id);
        Assert.DoesNotContain(result.Items, item => item.NameOriginal == "USENET-ADMIN-ITEM");
        Assert.Contains(result.Items, item => item.NameOriginal == "ITWORLD-ADMIN-ITEM");
        Assert.Contains(result.RentalAssets, asset => asset.ManagementNumber == "2603-911");
        Assert.Contains(result.RentalAssets, asset => asset.ManagementNumber == "2603-912");
    }

    [Fact]
    public async Task Pull_IncludesCrossTenantDeliveryData_ForUserWithDeliveryViewAll()
    {
        var usenetCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "유즈넷 납품처",
            NameMatchKey = "유즈넷납품처",
            TradeType = "매출"
        };
        var itworldCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
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

        Assert.DoesNotContain(result.Customers, customer => customer.Id == usenetCustomer.Id);
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
    public async Task Push_DoesNotRelinkRentalAssetCustomerAcrossTenant()
    {
        var usenetCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "아이티월드",
            NameMatchKey = RentalCatalogValueNormalizer.NormalizeLooseKey("아이티월드"),
            TradeType = "매출"
        };
        _dbContext.Customers.Add(usenetCustomer);
        await _dbContext.SaveChangesAsync();

        var assetId = Guid.NewGuid();
        var request = new SyncPushRequest
        {
            RentalAssets =
            [
                new RentalAssetDto
                {
                    Id = assetId,
                    TenantCode = TenantScopeCatalog.Itworld,
                    OfficeCode = OfficeCodeCatalog.Itworld,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                    ManagementCompanyCode = OfficeCodeCatalog.Itworld,
                    CustomerId = usenetCustomer.Id,
                    CustomerName = usenetCustomer.NameOriginal,
                    CurrentCustomerName = usenetCustomer.NameOriginal,
                    ItemName = "MODEL-CROSS-TENANT",
                    ManagementNumber = "2603-998",
                    MachineNumber = "SN-998",
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

        var asset = await _dbContext.RentalAssets.IgnoreQueryFilters().FirstAsync(current => current.Id == assetId);
        Assert.Null(asset.CustomerId);
        Assert.Equal(TenantScopeCatalog.Itworld, asset.TenantCode);
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
    public async Task Push_CustomerContract_AllowsOwnerOfficeFallback_WhenCustomerResponsibleOfficeMissing()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "office-user",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            IsAdmin = false
        };
        await using var dbContext = CreateDbContext(currentUser);
        var controller = CreateController(dbContext, currentUser);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = string.Empty,
            NameOriginal = "LEGACY-CONTRACT-CUSTOMER",
            NameMatchKey = "LEGACYCONTRACTCUSTOMER",
            TradeType = CustomerClassificationNormalizer.Sales
        };
        dbContext.Customers.Add(customer);
        await dbContext.SaveChangesAsync();

        var contractId = Guid.NewGuid();
        var pdfBytes = TestPdfBytes();
        var response = await controller.Push(new SyncPushRequest
        {
            DeviceId = "device-legacy-contract-fallback",
            CustomerContracts =
            [
                new CustomerContractDto
                {
                    Id = contractId,
                    CustomerId = customer.Id,
                    ContractType = "Contract",
                    FileName = "legacy-contract.pdf",
                    MimeType = "application/pdf",
                    FileSize = pdfBytes.LongLength,
                    FileHash = ComputeTestSha256Hex(pdfBytes),
                    FileContent = pdfBytes,
                    UploadedAtUtc = new DateTime(2026, 6, 22, 0, 0, 0, DateTimeKind.Utc),
                    CreatedAtUtc = new DateTime(2026, 6, 22, 0, 0, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2026, 6, 22, 0, 1, 0, DateTimeKind.Utc)
                }
            ]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(0, result.ConflictCount);
        Assert.True(await dbContext.CustomerContracts.IgnoreQueryFilters()
            .AnyAsync(current => current.Id == contractId && current.CustomerId == customer.Id));
    }

    [Fact]
    public async Task Push_AllowsCustomerContractMetadataUpdate_WhenFileAlreadyStored()
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "CONTRACT-METADATA-CUSTOMER",
            NameMatchKey = "CONTRACTMETADATACUSTOMER",
            TradeType = "매출"
        };
        var contractId = Guid.NewGuid();
        var contract = new CustomerContract
        {
            Id = contractId,
            CustomerId = customer.Id,
            ContractType = "거래계약서",
            FileName = "stored-contract.pdf",
            MimeType = "application/pdf",
            FileSize = 4,
            FileHash = "OLD-HASH",
            Description = "수정 전",
            StoragePath = "customer-contracts/stored-contract.pdf",
            FileContent = [],
            UploadedByUsername = "admin",
            UploadedAtUtc = new DateTime(2026, 3, 25, 17, 0, 0, DateTimeKind.Utc),
            CreatedAtUtc = new DateTime(2026, 3, 25, 17, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 3, 25, 17, 0, 0, DateTimeKind.Utc)
        };
        _dbContext.Customers.Add(customer);
        _dbContext.CustomerContracts.Add(contract);
        await _dbContext.SaveChangesAsync();

        var request = new SyncPushRequest
        {
            CustomerContracts =
            [
                new CustomerContractDto
                {
                    Id = contractId,
                    CustomerId = customer.Id,
                    ContractType = "거래계약서",
                    FileName = "stored-contract.pdf",
                    MimeType = "application/pdf",
                    FileSize = 999,
                    FileHash = "CLIENT-SHOULD-NOT-REPLACE-STORED-HASH",
                    Description = "메타데이터만 수정",
                    SignedDate = new DateOnly(2026, 5, 26),
                    UploadedAtUtc = contract.UploadedAtUtc,
                    CreatedAtUtc = contract.CreatedAtUtc,
                    UpdatedAtUtc = new DateTime(2026, 5, 26, 0, 0, 0, DateTimeKind.Utc),
                    Revision = contract.Revision,
                    FileContent = []
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(0, result.ConflictCount);
        _dbContext.ChangeTracker.Clear();
        var stored = await _dbContext.CustomerContracts.IgnoreQueryFilters().FirstAsync(current => current.Id == contractId);
        Assert.Equal("메타데이터만 수정", stored.Description);
        Assert.Equal(new DateOnly(2026, 5, 26), stored.SignedDate);
        Assert.Equal("customer-contracts/stored-contract.pdf", stored.StoragePath);
        Assert.Empty(stored.FileContent);
        Assert.Equal(4, stored.FileSize);
        Assert.Equal("OLD-HASH", stored.FileHash);
    }

    [Fact]
    public async Task Push_NormalizesCustomerContractFileMetadata_FromUploadedContent()
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "CONTRACT-FILE-METADATA-CUSTOMER",
            NameMatchKey = "CONTRACTFILEMETADATACUSTOMER",
            TradeType = "Sales"
        };
        _dbContext.Customers.Add(customer);
        await _dbContext.SaveChangesAsync();

        var contractId = Guid.NewGuid();
        var pdfBytes = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x37 };
        var request = new SyncPushRequest
        {
            DeviceId = "device-contract-file-metadata",
            CustomerContracts =
            [
                new CustomerContractDto
                {
                    Id = contractId,
                    CustomerId = customer.Id,
                    ContractType = "거래계약서",
                    FileName = "contract.pdf",
                    MimeType = "application/pdf",
                    FileSize = 999,
                    FileHash = "WRONG-CLIENT-HASH",
                    Description = "server should normalize file metadata",
                    UploadedAtUtc = new DateTime(2026, 6, 18, 0, 0, 0, DateTimeKind.Utc),
                    CreatedAtUtc = new DateTime(2026, 6, 18, 0, 0, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2026, 6, 18, 0, 1, 0, DateTimeKind.Utc),
                    FileContent = pdfBytes
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(0, result.ConflictCount);
        _dbContext.ChangeTracker.Clear();
        var stored = await _dbContext.CustomerContracts.IgnoreQueryFilters().FirstAsync(current => current.Id == contractId);
        Assert.Equal(pdfBytes.LongLength, stored.FileSize);
        Assert.Equal(ComputeTestSha256Hex(pdfBytes), stored.FileHash);
        Assert.Empty(stored.FileContent);
        Assert.False(string.IsNullOrWhiteSpace(stored.StoragePath));
    }

    [Fact]
    public async Task Push_DowngradesNewCustomerContractMetadataOnlyPdf_ToDraftNotice()
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "CONTRACT-DRAFT-CUSTOMER",
            NameMatchKey = "CONTRACTDRAFTCUSTOMER",
            TradeType = "Sales"
        };
        _dbContext.Customers.Add(customer);
        await _dbContext.SaveChangesAsync();

        var contractId = Guid.NewGuid();
        var request = new SyncPushRequest
        {
            CustomerContracts =
            [
                new CustomerContractDto
                {
                    Id = contractId,
                    CustomerId = customer.Id,
                    ContractType = "Contract",
                    FileName = "contract.pdf",
                    MimeType = "application/pdf",
                    FileSize = 1024,
                    FileHash = "HASH-WITHOUT-CONTENT",
                    Description = "metadata only",
                    UploadedAtUtc = new DateTime(2026, 5, 28, 9, 0, 0, DateTimeKind.Utc),
                    CreatedAtUtc = new DateTime(2026, 5, 28, 9, 0, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2026, 5, 28, 9, 1, 0, DateTimeKind.Utc),
                    FileContent = []
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(0, result.ConflictCount);
        Assert.Contains(result.Notices, notice =>
            notice.EntityName == nameof(CustomerContract) &&
            notice.EntityId == contractId.ToString("D") &&
            notice.Code == "customer-contract-file-payload-missing");

        var stored = await _dbContext.CustomerContracts.IgnoreQueryFilters()
            .SingleAsync(current => current.Id == contractId);
        Assert.Equal("PDF not registered", stored.FileName);
        Assert.Equal("application/pdf", stored.MimeType);
        Assert.Equal(0, stored.FileSize);
        Assert.Equal(string.Empty, stored.FileHash);
        Assert.Empty(stored.FileContent);
        Assert.Equal("metadata only", stored.Description);
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
                    Lines =
                    [
                        new InvoiceLineDto
                        {
                            Id = Guid.NewGuid(),
                            InvoiceId = invoiceId,
                            ItemNameOriginal = "same-batch line",
                            Unit = "EA",
                            Quantity = 1m,
                            UnitPrice = 5000m,
                            LineAmount = 5000m
                        }
                    ],
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
    public async Task Push_IgnoresDeletedInvoiceLinesWhenCalculatingInvoiceTotals()
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "SYNC-DELETED-LINE-TOTAL-CUSTOMER",
            NameMatchKey = "SYNCDELETEDLINETOTALCUSTOMER",
            TradeType = "매출"
        };
        _dbContext.Customers.Add(customer);
        await _dbContext.SaveChangesAsync();

        var invoiceId = Guid.NewGuid();
        var activeLineId = Guid.NewGuid();
        var deletedLineId = Guid.NewGuid();
        var request = new SyncPushRequest
        {
            DeviceId = "device-invoice-deleted-line-total",
            Invoices =
            [
                new InvoiceDto
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
                    InvoiceNumber = "INV-SYNC-DELETED-LINE-TOTAL",
                    Lines =
                    [
                        new InvoiceLineDto
                        {
                            Id = deletedLineId,
                            InvoiceId = invoiceId,
                            ItemNameOriginal = "sync deleted line",
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
                            ItemNameOriginal = "sync active line",
                            Unit = "EA",
                            Quantity = 2m,
                            UnitPrice = 5000m,
                            LineAmount = 0m,
                            OrderIndex = 2
                        }
                    ],
                    CreatedAtUtc = DateTime.UtcNow.AddMinutes(-1),
                    UpdatedAtUtc = DateTime.UtcNow
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(0, result.ConflictCount);
        Assert.Equal(1, result.AcceptedCount);

        var storedInvoice = await _dbContext.Invoices.IgnoreQueryFilters()
            .Include(invoice => invoice.Lines)
            .SingleAsync(invoice => invoice.Id == invoiceId);
        Assert.Equal(10000m, storedInvoice.SupplyAmount);
        Assert.Equal(0m, storedInvoice.VatAmount);
        Assert.Equal(10000m, storedInvoice.TotalAmount);
        Assert.DoesNotContain(storedInvoice.Lines, line => line.Id == deletedLineId);
        var activeLine = Assert.Single(storedInvoice.Lines, line => !line.IsDeleted);
        Assert.Equal(activeLineId, activeLine.Id);
        Assert.Equal(10000m, activeLine.LineAmount);
    }

    [Fact]
    public async Task Push_RenumbersActiveInvoiceLinesByPayloadOrder()
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "SYNC-LINE-ORDER-CUSTOMER",
            NameMatchKey = "SYNCLINEORDERCUSTOMER",
            TradeType = "매출"
        };
        _dbContext.Customers.Add(customer);
        await _dbContext.SaveChangesAsync();

        var invoiceId = Guid.NewGuid();
        var firstPayloadLineId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var secondPayloadLineId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var request = new SyncPushRequest
        {
            DeviceId = "device-invoice-line-order",
            Invoices =
            [
                new InvoiceDto
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
                    InvoiceNumber = "INV-SYNC-LINE-ORDER",
                    Lines =
                    [
                        new InvoiceLineDto
                        {
                            Id = firstPayloadLineId,
                            InvoiceId = invoiceId,
                            ItemNameOriginal = "sync payload first",
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
                            ItemNameOriginal = "sync payload second",
                            Unit = "EA",
                            Quantity = 1m,
                            UnitPrice = 2000m,
                            LineAmount = 2000m,
                            OrderIndex = 50
                        }
                    ],
                    CreatedAtUtc = DateTime.UtcNow.AddMinutes(-1),
                    UpdatedAtUtc = DateTime.UtcNow
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(0, result.ConflictCount);
        Assert.Equal(1, result.AcceptedCount);

        var storedLines = await _dbContext.InvoiceLines
            .AsNoTracking()
            .Where(line => line.InvoiceId == invoiceId)
            .OrderBy(line => line.OrderIndex)
            .ToListAsync();
        Assert.Equal(new[] { "sync payload first", "sync payload second" }, storedLines.Select(line => line.ItemNameOriginal).ToArray());
        Assert.Equal(new[] { 1, 2 }, storedLines.Select(line => line.OrderIndex).ToArray());
    }

    [Fact]
    public async Task Push_RentalTransactionOnly_RecalculatesRentalSettlement()
    {
        var customerId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();

        _dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "SYNC-RENTAL-TX-CUSTOMER",
            NameMatchKey = "SYNCRENTALTXCUSTOMER",
            TradeType = "매출"
        });
        _dbContext.RentalBillingProfiles.Add(new RentalBillingProfile
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ProfileKey = $"profile-{profileId:N}",
            CustomerId = customerId,
            CustomerName = "SYNC-RENTAL-TX-CUSTOMER",
            BillingStatus = "청구중",
            SettlementStatus = "미입금",
            CompletionStatus = "미완료",
            MonthlyAmount = 100_000m,
            SettledAmount = 0m,
            OutstandingAmount = 100_000m,
            BillingRunsJson = JsonSerializer.Serialize(new[]
            {
                new SyncRentalBillingRunSnapshot
                {
                    RunId = runId,
                    RunKey = "2026-06",
                    ScheduledDate = new DateOnly(2026, 6, 25),
                    PeriodStartDate = new DateOnly(2026, 6, 1),
                    PeriodEndDate = new DateOnly(2026, 6, 30),
                    PeriodLabel = "2026-06",
                    Status = "청구중",
                    BilledAmount = 100_000m,
                    SettledAmount = 0m,
                    SettlementStatus = "미입금"
                }
            })
        });
        _dbContext.Invoices.Add(new Invoice
        {
            Id = invoiceId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "SYNC-RENTAL-TX-001",
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
        await _dbContext.SaveChangesAsync();

        var request = new SyncPushRequest
        {
            DeviceId = "device-rental-transaction-only",
            Transactions =
            [
                new TransactionDto
                {
                    Id = transactionId,
                    CustomerId = customerId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    TransactionDate = new DateOnly(2026, 6, 26),
                    TransactionKind = "렌탈수금",
                    LinkedInvoiceId = invoiceId,
                    LinkedInvoiceNumber = "SYNC-RENTAL-TX-001",
                    LinkedRentalBillingProfileId = profileId,
                    LinkedRentalBillingRunId = runId,
                    BankReceipt = 40_000m,
                    ReceiptTotal = 40_000m,
                    SettlementAmount = 40_000m,
                    CreatedAtUtc = new DateTime(2026, 6, 26, 1, 0, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2026, 6, 26, 1, 1, 0, DateTimeKind.Utc)
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(0, result.ConflictCount);
        var updatedProfile = await _dbContext.RentalBillingProfiles.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(profile => profile.Id == profileId);
        Assert.Equal(40_000m, updatedProfile.SettledAmount);
        Assert.Equal(60_000m, updatedProfile.OutstandingAmount);
        Assert.Equal("부분입금", updatedProfile.SettlementStatus);
        var updatedRun = Assert.Single(JsonSerializer.Deserialize<List<SyncRentalBillingRunSnapshot>>(updatedProfile.BillingRunsJson) ?? []);
        Assert.Equal(100_000m, updatedRun.BilledAmount);
        Assert.Equal(40_000m, updatedRun.SettledAmount);
        Assert.Equal("부분입금", updatedRun.SettlementStatus);
    }

    [Fact]
    public async Task Push_StaleRentalTransactionDelete_DoesNotRecalculateSettlement()
    {
        var customerId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();

        _dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "SYNC-STALE-RENTAL-TX-CUSTOMER",
            NameMatchKey = "SYNCSTALERENTALTXCUSTOMER",
            TradeType = "매출"
        });
        _dbContext.RentalBillingProfiles.Add(new RentalBillingProfile
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ProfileKey = $"stale-transaction-profile-{profileId:N}",
            CustomerId = customerId,
            CustomerName = "SYNC-STALE-RENTAL-TX-CUSTOMER",
            BillingStatus = "청구중",
            SettlementStatus = "미입금",
            CompletionStatus = "미완료",
            MonthlyAmount = 100_000m,
            SettledAmount = 0m,
            OutstandingAmount = 100_000m,
            BillingRunsJson = JsonSerializer.Serialize(new[]
            {
                new SyncRentalBillingRunSnapshot
                {
                    RunId = runId,
                    RunKey = "2026-06",
                    ScheduledDate = new DateOnly(2026, 6, 25),
                    PeriodStartDate = new DateOnly(2026, 6, 1),
                    PeriodEndDate = new DateOnly(2026, 6, 30),
                    PeriodLabel = "2026-06",
                    Status = "청구중",
                    BilledAmount = 100_000m,
                    SettledAmount = 0m,
                    SettlementStatus = "미입금"
                }
            })
        });
        _dbContext.Transactions.Add(new TransactionRecord
        {
            Id = transactionId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 6, 26),
            TransactionKind = "렌탈수금",
            LinkedRentalBillingProfileId = profileId,
            LinkedRentalBillingRunId = runId,
            BankReceipt = 40_000m,
            ReceiptTotal = 40_000m,
            SettlementAmount = 40_000m,
            CreatedAtUtc = new DateTime(2026, 6, 26, 1, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 6, 26, 1, 1, 0, DateTimeKind.Utc)
        });
        await _dbContext.SaveChangesAsync();
        var storedTransaction = await _dbContext.Transactions.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(transaction => transaction.Id == transactionId);

        var response = await _controller.Push(new SyncPushRequest
        {
            DeviceId = "device-stale-rental-transaction-delete",
            Transactions =
            [
                new TransactionDto
                {
                    Id = transactionId,
                    CustomerId = customerId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    TransactionDate = new DateOnly(2026, 6, 26),
                    TransactionKind = "렌탈수금",
                    LinkedRentalBillingProfileId = profileId,
                    LinkedRentalBillingRunId = runId,
                    BankReceipt = 40_000m,
                    ReceiptTotal = 40_000m,
                    SettlementAmount = 40_000m,
                    IsDeleted = true,
                    ExpectedRevision = storedTransaction.Revision + 1,
                    CreatedAtUtc = storedTransaction.CreatedAtUtc,
                    UpdatedAtUtc = DateTime.UtcNow.AddMinutes(1)
                }
            ]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.Equal(1, result.ConflictCount);

        Assert.False(await _dbContext.Transactions.IgnoreQueryFilters()
            .Where(transaction => transaction.Id == transactionId)
            .Select(transaction => transaction.IsDeleted)
            .SingleAsync());
        var profile = await _dbContext.RentalBillingProfiles.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == profileId);
        Assert.Equal(0m, profile.SettledAmount);
        Assert.Equal(100_000m, profile.OutstandingAmount);
        Assert.Equal("미입금", profile.SettlementStatus);
        var run = Assert.Single(JsonSerializer.Deserialize<List<SyncRentalBillingRunSnapshot>>(profile.BillingRunsJson) ?? []);
        Assert.Equal(0m, run.SettledAmount);
        Assert.Equal("미입금", run.SettlementStatus);
    }

    [Fact]
    public async Task Push_RentalTransactionWithZeroSettlement_ClearsRentalLinkAndDoesNotRecreateDirtyEvidence()
    {
        var customerId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();

        _dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "SYNC-RENTAL-ZERO-TX-CUSTOMER",
            NameMatchKey = "SYNCRENTALZEROTXCUSTOMER",
            TradeType = "매출"
        });
        _dbContext.RentalBillingProfiles.Add(new RentalBillingProfile
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ProfileKey = $"profile-{profileId:N}",
            CustomerId = customerId,
            CustomerName = "SYNC-RENTAL-ZERO-TX-CUSTOMER",
            BillingStatus = "청구중",
            SettlementStatus = "미입금",
            CompletionStatus = "미완료",
            MonthlyAmount = 100_000m,
            SettledAmount = 0m,
            OutstandingAmount = 100_000m,
            BillingRunsJson = JsonSerializer.Serialize(new[]
            {
                new SyncRentalBillingRunSnapshot
                {
                    RunId = runId,
                    RunKey = "2026-06",
                    ScheduledDate = new DateOnly(2026, 6, 25),
                    PeriodStartDate = new DateOnly(2026, 6, 1),
                    PeriodEndDate = new DateOnly(2026, 6, 30),
                    PeriodLabel = "2026-06",
                    Status = "청구중",
                    BilledAmount = 100_000m,
                    SettledAmount = 0m,
                    SettlementStatus = "미입금"
                }
            })
        });
        _dbContext.Invoices.Add(new Invoice
        {
            Id = invoiceId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "SYNC-RENTAL-ZERO-TX-001",
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
        await _dbContext.SaveChangesAsync();

        var response = await _controller.Push(new SyncPushRequest
        {
            DeviceId = "device-rental-zero-transaction",
            Transactions =
            [
                new TransactionDto
                {
                    Id = transactionId,
                    CustomerId = customerId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    TransactionDate = new DateOnly(2026, 6, 26),
                    TransactionKind = "렌탈수금",
                    LinkedInvoiceId = invoiceId,
                    LinkedInvoiceNumber = "SYNC-RENTAL-ZERO-TX-001",
                    LinkedRentalBillingProfileId = profileId,
                    LinkedRentalBillingRunId = runId,
                    BankReceipt = 0m,
                    ReceiptTotal = 0m,
                    SettlementAmount = 0m,
                    CreatedAtUtc = new DateTime(2026, 6, 26, 1, 0, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2026, 6, 26, 1, 1, 0, DateTimeKind.Utc)
                }
            ]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.Equal(0, result.ConflictCount);
        Assert.Contains(result.Notices, notice => notice.Code == "transaction-rental-zero-link-cleared" && notice.EntityId == transactionId.ToString("D"));

        var storedTransaction = await _dbContext.Transactions.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(transaction => transaction.Id == transactionId);
        Assert.Null(storedTransaction.LinkedRentalBillingProfileId);
        Assert.Null(storedTransaction.LinkedRentalBillingRunId);
        Assert.Equal("일반수금", storedTransaction.TransactionKind);
        Assert.Equal(0m, storedTransaction.SettlementAmount);

        var updatedProfile = await _dbContext.RentalBillingProfiles.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(profile => profile.Id == profileId);
        Assert.Equal(0m, updatedProfile.SettledAmount);
        Assert.Equal(100_000m, updatedProfile.OutstandingAmount);
        var updatedRun = Assert.Single(JsonSerializer.Deserialize<List<SyncRentalBillingRunSnapshot>>(updatedProfile.BillingRunsJson) ?? []);
        Assert.Equal(0m, updatedRun.SettledAmount);
        Assert.Null(updatedRun.SettledDate);
    }

    [Fact]
    public async Task Push_RentalPaymentOnly_RecalculatesRentalSettlement()
    {
        var customerId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();

        _dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "SYNC-RENTAL-PAYMENT-CUSTOMER",
            NameMatchKey = "SYNCRENTALPAYMENTCUSTOMER",
            TradeType = "매출"
        });
        _dbContext.RentalBillingProfiles.Add(new RentalBillingProfile
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ProfileKey = $"profile-{profileId:N}",
            CustomerId = customerId,
            CustomerName = "SYNC-RENTAL-PAYMENT-CUSTOMER",
            BillingStatus = "청구중",
            SettlementStatus = "미입금",
            CompletionStatus = "미완료",
            MonthlyAmount = 100_000m,
            SettledAmount = 0m,
            OutstandingAmount = 100_000m,
            BillingRunsJson = JsonSerializer.Serialize(new[]
            {
                new SyncRentalBillingRunSnapshot
                {
                    RunId = runId,
                    RunKey = "2026-06",
                    ScheduledDate = new DateOnly(2026, 6, 25),
                    PeriodStartDate = new DateOnly(2026, 6, 1),
                    PeriodEndDate = new DateOnly(2026, 6, 30),
                    PeriodLabel = "2026-06",
                    Status = "청구중",
                    BilledAmount = 100_000m,
                    SettledAmount = 0m,
                    SettlementStatus = "미입금"
                }
            })
        });
        _dbContext.Invoices.Add(new Invoice
        {
            Id = invoiceId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "SYNC-RENTAL-PAY-001",
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
        await _dbContext.SaveChangesAsync();

        var request = new SyncPushRequest
        {
            DeviceId = "device-rental-payment-only",
            Payments =
            [
                new PaymentDto
                {
                    Id = paymentId,
                    InvoiceId = invoiceId,
                    PaymentDate = new DateOnly(2026, 6, 27),
                    Amount = 35_000m,
                    Note = "direct rental payment sync",
                    CreatedAtUtc = new DateTime(2026, 6, 27, 1, 0, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2026, 6, 27, 1, 1, 0, DateTimeKind.Utc)
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(0, result.ConflictCount);
        var updatedProfile = await _dbContext.RentalBillingProfiles.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(profile => profile.Id == profileId);
        Assert.Equal(35_000m, updatedProfile.SettledAmount);
        Assert.Equal(65_000m, updatedProfile.OutstandingAmount);
        Assert.Equal("부분입금", updatedProfile.SettlementStatus);
        var updatedRun = Assert.Single(JsonSerializer.Deserialize<List<SyncRentalBillingRunSnapshot>>(updatedProfile.BillingRunsJson) ?? []);
        Assert.Equal(100_000m, updatedRun.BilledAmount);
        Assert.Equal(35_000m, updatedRun.SettledAmount);
        Assert.Equal("부분입금", updatedRun.SettlementStatus);
    }

    [Fact]
    public async Task Push_RejectsTransactionUpdate_WhenExistingRentalSettlementProfileIsOutsideWritableScope()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "usenet-payment-rental-scope",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = [PermissionNames.PaymentEdit]
        };

        await using var scopedDb = CreateDbContext(currentUser);
        var controller = CreateController(scopedDb, currentUser);
        var customerId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        scopedDb.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "SYNC-RENTAL-TX-OUT-SCOPE-CUSTOMER",
            NameMatchKey = "SYNCRENTALTXOUTSCOPECUSTOMER",
            TradeType = CustomerClassificationNormalizer.Sales
        });
        scopedDb.RentalBillingProfiles.Add(new RentalBillingProfile
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            ManagementCompanyCode = OfficeCodeCatalog.Yeonsu,
            ProfileKey = $"profile-out-scope-{profileId:N}",
            CustomerName = "SYNC-RENTAL-TX-OUT-SCOPE-CUSTOMER",
            BillingStatus = "청구중",
            SettlementStatus = "부분입금",
            CompletionStatus = "미완료",
            MonthlyAmount = 100_000m,
            SettledAmount = 40_000m,
            OutstandingAmount = 60_000m,
            BillingRunsJson = JsonSerializer.Serialize(new[]
            {
                new SyncRentalBillingRunSnapshot
                {
                    RunId = runId,
                    RunKey = "2026-06",
                    ScheduledDate = new DateOnly(2026, 6, 25),
                    PeriodStartDate = new DateOnly(2026, 6, 1),
                    PeriodEndDate = new DateOnly(2026, 6, 30),
                    PeriodLabel = "2026-06",
                    Status = "청구중",
                    BilledAmount = 100_000m,
                    SettledAmount = 40_000m,
                    SettlementStatus = "부분입금"
                }
            })
        });
        scopedDb.Transactions.Add(new TransactionRecord
        {
            Id = transactionId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 6, 26),
            TransactionKind = "렌탈수금",
            LinkedRentalBillingProfileId = profileId,
            LinkedRentalBillingRunId = runId,
            BankReceipt = 40_000m,
            ReceiptTotal = 40_000m,
            SettlementAmount = 40_000m
        });
        await scopedDb.SaveChangesAsync();
        var storedTransaction = await scopedDb.Transactions.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(transaction => transaction.Id == transactionId);

        var response = await controller.Push(new SyncPushRequest
        {
            DeviceId = "device-rental-transaction-out-scope-profile",
            Transactions =
            [
                new TransactionDto
                {
                    Id = transactionId,
                    CustomerId = customerId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    TransactionDate = storedTransaction.TransactionDate,
                    TransactionKind = "일반수금",
                    BankReceipt = 40_000m,
                    ReceiptTotal = 40_000m,
                    SettlementAmount = 0m,
                    Revision = storedTransaction.Revision,
                    ExpectedRevision = storedTransaction.Revision,
                    CreatedAtUtc = storedTransaction.CreatedAtUtc,
                    UpdatedAtUtc = storedTransaction.UpdatedAtUtc.AddMinutes(1)
                }
            ]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            string.Equals(conflict.EntityName, nameof(TransactionRecord), StringComparison.Ordinal) &&
            conflict.Reason.Contains("outside the writable office scope", StringComparison.OrdinalIgnoreCase));

        var profile = await scopedDb.RentalBillingProfiles.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == profileId);
        Assert.Equal(40_000m, profile.SettledAmount);
        Assert.Equal(60_000m, profile.OutstandingAmount);
        Assert.NotNull(await scopedDb.Transactions.IgnoreQueryFilters()
            .SingleAsync(transaction => transaction.Id == transactionId && transaction.LinkedRentalBillingProfileId == profileId));
    }

    [Fact]
    public async Task Push_RejectsPaymentCreate_WhenInvoiceRentalSettlementProfileIsOutsideWritableScope()
    {
        var currentUser = new TestCurrentUserContext
        {
            Username = "usenet-payment-invoice-rental-scope",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = [PermissionNames.PaymentEdit]
        };

        await using var scopedDb = CreateDbContext(currentUser);
        var controller = CreateController(scopedDb, currentUser);
        var customerId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        scopedDb.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "SYNC-RENTAL-PAY-OUT-SCOPE-CUSTOMER",
            NameMatchKey = "SYNCRENTALPAYOUTSCOPECUSTOMER",
            TradeType = CustomerClassificationNormalizer.Sales
        });
        scopedDb.RentalBillingProfiles.Add(new RentalBillingProfile
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            ManagementCompanyCode = OfficeCodeCatalog.Yeonsu,
            ProfileKey = $"profile-out-scope-{profileId:N}",
            CustomerName = "SYNC-RENTAL-PAY-OUT-SCOPE-CUSTOMER",
            BillingStatus = "청구중",
            SettlementStatus = "미입금",
            CompletionStatus = "미완료",
            MonthlyAmount = 100_000m,
            SettledAmount = 0m,
            OutstandingAmount = 100_000m
        });
        scopedDb.Invoices.Add(new Invoice
        {
            Id = invoiceId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "SYNC-RENTAL-PAY-OUT-SCOPE",
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
        await scopedDb.SaveChangesAsync();

        var response = await controller.Push(new SyncPushRequest
        {
            DeviceId = "device-rental-payment-out-scope-profile",
            Payments =
            [
                new PaymentDto
                {
                    Id = paymentId,
                    InvoiceId = invoiceId,
                    PaymentDate = new DateOnly(2026, 6, 27),
                    Amount = 30_000m,
                    Note = "out-of-scope rental profile payment",
                    CreatedAtUtc = new DateTime(2026, 6, 27, 1, 0, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2026, 6, 27, 1, 1, 0, DateTimeKind.Utc)
                }
            ]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            string.Equals(conflict.EntityName, nameof(Payment), StringComparison.Ordinal) &&
            conflict.Reason.Contains("outside the writable office scope", StringComparison.OrdinalIgnoreCase));

        Assert.False(await scopedDb.Payments.IgnoreQueryFilters().AnyAsync(payment => payment.Id == paymentId));
        var profile = await scopedDb.RentalBillingProfiles.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == profileId);
        Assert.Equal(0m, profile.SettledAmount);
        Assert.Equal(100_000m, profile.OutstandingAmount);
    }

    [Fact]
    public async Task Push_NormalizesUnspecifiedPurchaseReceivedAtUtc_ForInvoice()
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "PURCHASE-RECEIVED-CUSTOMER",
            NameMatchKey = "PURCHASERECEIVEDCUSTOMER",
            TradeType = "매입"
        };
        _dbContext.Customers.Add(customer);
        await _dbContext.SaveChangesAsync();

        var invoiceId = Guid.NewGuid();
        var unspecifiedReceivedAt = DateTime.SpecifyKind(new DateTime(2026, 5, 4, 8, 30, 0), DateTimeKind.Unspecified);
        var request = new SyncPushRequest
        {
            DeviceId = "device-unspecified-received-at",
            Invoices =
            [
                new InvoiceDto
                {
                    Id = invoiceId,
                    CustomerId = customer.Id,
                    CustomerName = customer.NameOriginal,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    VoucherType = VoucherType.Purchase,
                    InvoiceDate = new DateOnly(2026, 5, 4),
                    TotalAmount = 1100m,
                    SupplyAmount = 1000m,
                    VatAmount = 100m,
                    PurchaseReceivingRequired = true,
                    PurchaseReceivingStatus = InvoiceReceivingStatuses.Confirmed,
                    PurchaseReceivedAtUtc = unspecifiedReceivedAt,
                    PurchaseReceivedByUsername = "tester",
                    CreatedAtUtc = DateTime.SpecifyKind(new DateTime(2026, 5, 4, 8, 20, 0), DateTimeKind.Unspecified),
                    UpdatedAtUtc = DateTime.SpecifyKind(new DateTime(2026, 5, 4, 8, 31, 0), DateTimeKind.Unspecified),
                    MutationId = $"device-unspecified-received-at:Invoice:{invoiceId:N}:1",
                    MutationCreatedAtUtc = DateTime.SpecifyKind(new DateTime(2026, 5, 4, 8, 31, 1), DateTimeKind.Unspecified)
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(0, result.ConflictCount);
        Assert.Equal(1, result.AcceptedCount);

        var storedInvoice = await _dbContext.Invoices.IgnoreQueryFilters().FirstAsync(x => x.Id == invoiceId);
        Assert.NotNull(storedInvoice.PurchaseReceivedAtUtc);
        Assert.Equal(DateTimeKind.Utc, storedInvoice.PurchaseReceivedAtUtc.Value.Kind);
        Assert.Equal(DateTime.SpecifyKind(unspecifiedReceivedAt, DateTimeKind.Utc), storedInvoice.PurchaseReceivedAtUtc.Value);
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
                    Lines =
                    [
                        new InvoiceLineDto
                        {
                            Id = Guid.NewGuid(),
                            InvoiceId = invoiceId,
                            ItemNameOriginal = "same-batch line",
                            Unit = "EA",
                            Quantity = 1m,
                            UnitPrice = 5000m,
                            LineAmount = 5000m
                        }
                    ],
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

        Assert.True(
            result.ConflictCount == 0,
            string.Join(" | ", result.Conflicts.Select(conflict => $"{conflict.EntityName}:{conflict.Reason}")));

        var storedPayment = await _dbContext.Payments.IgnoreQueryFilters().FirstAsync();
        Assert.Equal(invoiceId, storedPayment.InvoiceId);
        Assert.False(storedPayment.IsDeleted);
    }

    [Fact]
    public async Task Push_RejectsNewInvoice_WhenLineItemIsMissing()
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "MISSING-LINE-ITEM-INVOICE-CUSTOMER",
            NameMatchKey = "MISSINGLINEITEMINVOICECUSTOMER",
            TradeType = "매출"
        };
        _dbContext.Customers.Add(customer);
        await _dbContext.SaveChangesAsync();

        var invoiceId = Guid.NewGuid();
        var missingItemId = Guid.NewGuid();
        var response = await _controller.Push(new SyncPushRequest
        {
            DeviceId = "device-invoice-missing-line-item",
            Invoices =
            [
                new InvoiceDto
                {
                    Id = invoiceId,
                    CustomerId = customer.Id,
                    CustomerName = customer.NameOriginal,
                    TenantCode = customer.TenantCode,
                    OfficeCode = customer.OfficeCode,
                    ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
                    VoucherType = VoucherType.Sales,
                    InvoiceDate = new DateOnly(2026, 6, 22),
                    Lines =
                    [
                        new InvoiceLineDto
                        {
                            Id = Guid.NewGuid(),
                            InvoiceId = invoiceId,
                            ItemId = missingItemId,
                            ItemNameOriginal = "missing item line",
                            Unit = "EA",
                            Quantity = 1m,
                            UnitPrice = 1000m,
                            LineAmount = 1000m
                        }
                    ],
                    CreatedAtUtc = DateTime.UtcNow.AddMinutes(-1),
                    UpdatedAtUtc = DateTime.UtcNow
                }
            ]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            conflict.EntityName == nameof(Invoice) &&
            conflict.Reason.Contains("Referenced invoice line item was not found", StringComparison.OrdinalIgnoreCase));
        Assert.False(await _dbContext.Invoices.IgnoreQueryFilters().AnyAsync(invoice => invoice.Id == invoiceId));
        Assert.False(await _dbContext.InvoiceLines.IgnoreQueryFilters().AnyAsync(line => line.InvoiceId == invoiceId));
    }

    [Fact]
    public async Task Push_RejectsNewPayment_WhenInvoiceCustomerIsDeleted()
    {
        var deletedCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "DELETED-PAYMENT-INVOICE-CUSTOMER",
            NameMatchKey = "DELETEDPAYMENTINVOICECUSTOMER",
            TradeType = "매출",
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
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 22),
            TotalAmount = 10000m,
            SupplyAmount = 9091m,
            VatAmount = 909m
        };
        _dbContext.Customers.Add(deletedCustomer);
        _dbContext.Invoices.Add(invoice);
        await _dbContext.SaveChangesAsync();

        var paymentId = Guid.NewGuid();
        var response = await _controller.Push(new SyncPushRequest
        {
            DeviceId = "device-payment-deleted-invoice-customer",
            Payments =
            [
                new PaymentDto
                {
                    Id = paymentId,
                    InvoiceId = invoice.Id,
                    PaymentDate = new DateOnly(2026, 6, 22),
                    Amount = 1000m,
                    Note = "deleted invoice customer payment",
                    CreatedAtUtc = DateTime.UtcNow.AddMinutes(-1),
                    UpdatedAtUtc = DateTime.UtcNow
                }
            ]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            conflict.EntityName == nameof(Payment) &&
            conflict.Reason.Contains("Referenced invoice customer was not found", StringComparison.OrdinalIgnoreCase));
        Assert.False(await _dbContext.Payments.IgnoreQueryFilters().AnyAsync(payment => payment.Id == paymentId));
    }

    [Fact]
    public async Task Push_RejectsNewLinkedTransaction_WhenInvoiceCustomerIsDeleted()
    {
        var deletedCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "DELETED-TX-INVOICE-CUSTOMER",
            NameMatchKey = "DELETEDTXINVOICECUSTOMER",
            TradeType = "매출",
            IsDeleted = true
        };
        var activeCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "ACTIVE-TX-PAYLOAD-CUSTOMER",
            NameMatchKey = "ACTIVETXPAYLOADCUSTOMER",
            TradeType = "매출"
        };
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = deletedCustomer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "TX-DELETED-CUSTOMER-001",
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 22),
            TotalAmount = 10000m,
            SupplyAmount = 9091m,
            VatAmount = 909m
        };
        _dbContext.Customers.AddRange(deletedCustomer, activeCustomer);
        _dbContext.Invoices.Add(invoice);
        await _dbContext.SaveChangesAsync();

        var transactionId = Guid.NewGuid();
        var response = await _controller.Push(new SyncPushRequest
        {
            DeviceId = "device-transaction-deleted-invoice-customer",
            Transactions =
            [
                new TransactionDto
                {
                    Id = transactionId,
                    CustomerId = activeCustomer.Id,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    TransactionDate = new DateOnly(2026, 6, 22),
                    TransactionKind = "전표수금",
                    LinkedInvoiceId = invoice.Id,
                    LinkedInvoiceNumber = invoice.InvoiceNumber,
                    BankReceipt = 1000m,
                    ReceiptTotal = 1000m,
                    SettlementAmount = 1000m,
                    CreatedAtUtc = DateTime.UtcNow.AddMinutes(-1),
                    UpdatedAtUtc = DateTime.UtcNow
                }
            ]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            conflict.EntityName == nameof(TransactionRecord) &&
            conflict.Reason.Contains("Referenced invoice customer was not found", StringComparison.OrdinalIgnoreCase));
        Assert.False(await _dbContext.Transactions.IgnoreQueryFilters().AnyAsync(transaction => transaction.Id == transactionId));
    }

    [Fact]
    public async Task Push_RejectsNewLinkedPaymentAndTransaction_WhenInvoiceRevisionIsStale()
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "STALE-INVOICE-LINKED-PAYMENT-CUSTOMER",
            NameMatchKey = "STALEINVOICELINKEDPAYMENTCUSTOMER",
            TradeType = "매출"
        };
        var invoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "STALE-PAY-001",
            LocalTempNumber = "STALE-PAY-TMP-001",
            InvoiceDate = new DateOnly(2026, 6, 21),
            VoucherType = VoucherType.Sales,
            TotalAmount = 100000m,
            SupplyAmount = 90909m,
            VatAmount = 9091m
        };
        _dbContext.Customers.Add(customer);
        _dbContext.Invoices.Add(invoice);
        await _dbContext.SaveChangesAsync();

        var staleExpectedRevision = invoice.Revision;
        invoice.Memo = "server side invoice revision changed before mobile payment arrived";
        await _dbContext.SaveChangesAsync();

        var paymentId = Guid.NewGuid();
        var request = new SyncPushRequest
        {
            DeviceId = "device-stale-invoice-linked-payment",
            Transactions =
            [
                new TransactionDto
                {
                    Id = paymentId,
                    CustomerId = customer.Id,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    TransactionDate = new DateOnly(2026, 6, 21),
                    TransactionKind = "전표수금",
                    LinkedInvoiceId = invoice.Id,
                    LinkedInvoiceNumber = invoice.InvoiceNumber,
                    BankReceipt = 10000m,
                    ReceiptTotal = 10000m,
                    SettlementAmount = 10000m,
                    ExpectedRevision = staleExpectedRevision,
                    CreatedAtUtc = new DateTime(2026, 6, 21, 1, 0, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2026, 6, 21, 1, 1, 0, DateTimeKind.Utc)
                }
            ],
            Payments =
            [
                new PaymentDto
                {
                    Id = paymentId,
                    InvoiceId = invoice.Id,
                    PaymentDate = new DateOnly(2026, 6, 21),
                    Amount = 10000m,
                    Note = "stale linked invoice payment",
                    ExpectedRevision = staleExpectedRevision,
                    CreatedAtUtc = new DateTime(2026, 6, 21, 1, 0, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2026, 6, 21, 1, 1, 0, DateTimeKind.Utc)
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(2, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            conflict.EntityName == nameof(TransactionRecord) &&
            conflict.Reason.Contains("Referenced invoice revision mismatch", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Conflicts, conflict =>
            conflict.EntityName == nameof(Payment) &&
            conflict.Reason.Contains("Referenced invoice revision mismatch", StringComparison.OrdinalIgnoreCase));
        Assert.False(await _dbContext.Transactions.IgnoreQueryFilters().AnyAsync(transaction => transaction.Id == paymentId));
        Assert.False(await _dbContext.Payments.IgnoreQueryFilters().AnyAsync(payment => payment.Id == paymentId));
    }

    [Fact]
    public async Task Push_RejectsPayment_WhenAmountExceedsOutstanding()
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "OVER-PAYMENT-CUSTOMER",
            NameMatchKey = "OVERPAYMENTCUSTOMER",
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
            InvoiceNumber = "OVER-PAY-001",
            LocalTempNumber = "OVER-PAY-TMP-001",
            InvoiceDate = new DateOnly(2026, 3, 26),
            VoucherType = VoucherType.Sales,
            TotalAmount = 10000m,
            SupplyAmount = 9091m,
            VatAmount = 909m
        });
        _dbContext.Payments.Add(new Payment
        {
            Id = Guid.NewGuid(),
            InvoiceId = invoiceId,
            PaymentDate = new DateOnly(2026, 3, 26),
            Amount = 7000m,
            Note = "already paid",
            CreatedAtUtc = new DateTime(2026, 3, 26, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 3, 26, 0, 0, 0, DateTimeKind.Utc)
        });
        await _dbContext.SaveChangesAsync();

        var request = new SyncPushRequest
        {
            Payments =
            [
                new PaymentDto
                {
                    Id = Guid.NewGuid(),
                    InvoiceId = invoiceId,
                    PaymentDate = new DateOnly(2026, 3, 27),
                    Amount = 4000m,
                    Note = "over payment",
                    CreatedAtUtc = new DateTime(2026, 3, 27, 0, 0, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2026, 3, 27, 0, 0, 0, DateTimeKind.Utc)
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(1, result.ConflictCount);
        Assert.Single(await _dbContext.Payments.IgnoreQueryFilters().ToListAsync());
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
    public async Task Push_RejectsNewPayment_WhenInvoiceIsMissing()
    {
        var missingInvoiceId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var request = new SyncPushRequest
        {
            DeviceId = "device-payment-missing-invoice",
            Payments =
            [
                new PaymentDto
                {
                    Id = paymentId,
                    InvoiceId = missingInvoiceId,
                    PaymentDate = new DateOnly(2026, 6, 19),
                    Amount = 8000m,
                    Note = "new payment missing invoice",
                    CreatedAtUtc = new DateTime(2026, 6, 19, 0, 3, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2026, 6, 19, 0, 3, 0, DateTimeKind.Utc)
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            conflict.EntityName == nameof(Payment) &&
            conflict.Reason.Contains("Referenced invoice was not found", StringComparison.OrdinalIgnoreCase));
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
    public async Task Push_DeletePayment_SoftDeletesPaymentAttachments()
    {
        var paymentId = Guid.NewGuid();
        var attachmentId = Guid.NewGuid();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "SYNC-PAYMENT-ATTACHMENT-DELETE-CUSTOMER",
            NameMatchKey = "SYNCPAYMENTATTACHMENTDELETECUSTOMER",
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
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "SYNC-PAYMENT-ATTACHMENT-DELETE",
            InvoiceDate = new DateOnly(2026, 6, 23),
            VoucherType = VoucherType.Sales,
            TotalAmount = 8000m,
            SupplyAmount = 7273m,
            VatAmount = 727m
        });
        _dbContext.Payments.Add(new Payment
        {
            Id = paymentId,
            InvoiceId = invoiceId,
            PaymentDate = new DateOnly(2026, 6, 23),
            Amount = 8000m,
            Note = "payment with attachment",
            CreatedAtUtc = new DateTime(2026, 6, 23, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 6, 23, 0, 0, 0, DateTimeKind.Utc)
        });
        _dbContext.PaymentAttachments.Add(new PaymentAttachment
        {
            Id = attachmentId,
            PaymentId = paymentId,
            FileName = "payment-delete-evidence.pdf",
            MimeType = "application/pdf",
            FileSize = 1234,
            StoragePath = "payment/payment-delete-evidence.pdf",
            FileContent = [1, 2, 3]
        });
        await _dbContext.SaveChangesAsync();
        var storedPayment = await _dbContext.Payments.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(payment => payment.Id == paymentId);

        var response = await _controller.Push(new SyncPushRequest
        {
            DeviceId = "device-payment-attachment-delete",
            Payments =
            [
                new PaymentDto
                {
                    Id = paymentId,
                    InvoiceId = invoiceId,
                    PaymentDate = new DateOnly(2026, 6, 23),
                    Amount = 8000m,
                    Note = "payment delete with attachment",
                    IsDeleted = true,
                    ExpectedRevision = storedPayment.Revision,
                    CreatedAtUtc = storedPayment.CreatedAtUtc,
                    UpdatedAtUtc = DateTime.UtcNow.AddMinutes(1)
                }
            ]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.Equal(0, result.ConflictCount);

        Assert.True(await _dbContext.Payments.IgnoreQueryFilters()
            .Where(payment => payment.Id == paymentId)
            .Select(payment => payment.IsDeleted)
            .SingleAsync());
        Assert.True(await _dbContext.PaymentAttachments.IgnoreQueryFilters()
            .Where(attachment => attachment.Id == attachmentId)
            .Select(attachment => attachment.IsDeleted)
            .SingleAsync());
    }

    [Fact]
    public async Task Push_StalePaymentDelete_DoesNotCascadeLinkedTransactionOrAttachments()
    {
        var paymentId = Guid.NewGuid();
        var transactionAttachmentId = Guid.NewGuid();
        var paymentAttachmentId = Guid.NewGuid();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "SYNC-STALE-PAYMENT-DELETE-CUSTOMER",
            NameMatchKey = "SYNCSTALEPAYMENTDELETECUSTOMER",
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
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "SYNC-STALE-PAYMENT-DELETE",
            InvoiceDate = new DateOnly(2026, 6, 24),
            VoucherType = VoucherType.Sales,
            TotalAmount = 9000m,
            SupplyAmount = 8182m,
            VatAmount = 818m
        });
        _dbContext.Payments.Add(new Payment
        {
            Id = paymentId,
            InvoiceId = invoiceId,
            PaymentDate = new DateOnly(2026, 6, 24),
            Amount = 9000m,
            Note = "server newer payment",
            CreatedAtUtc = new DateTime(2026, 6, 24, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 6, 24, 0, 0, 0, DateTimeKind.Utc)
        });
        _dbContext.PaymentAttachments.Add(new PaymentAttachment
        {
            Id = paymentAttachmentId,
            PaymentId = paymentId,
            FileName = "stale-payment-delete-evidence.pdf",
            MimeType = "application/pdf",
            FileSize = 100,
            StoragePath = "payment/stale-payment-delete-evidence.pdf",
            FileContent = [4, 5, 6]
        });
        _dbContext.Transactions.Add(new TransactionRecord
        {
            Id = paymentId,
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 6, 24),
            TransactionKind = "전표수금",
            LinkedInvoiceId = invoiceId,
            LinkedInvoiceNumber = "SYNC-STALE-PAYMENT-DELETE",
            ReceiptTotal = 9000m,
            SettlementAmount = 9000m
        });
        _dbContext.TransactionAttachments.Add(new TransactionAttachment
        {
            Id = transactionAttachmentId,
            TransactionId = paymentId,
            FileName = "stale-payment-delete-transaction.pdf",
            StoragePath = "transaction/stale-payment-delete-transaction.pdf"
        });
        await _dbContext.SaveChangesAsync();
        var storedPayment = await _dbContext.Payments.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(payment => payment.Id == paymentId);

        var response = await _controller.Push(new SyncPushRequest
        {
            DeviceId = "device-stale-payment-delete",
            Payments =
            [
                new PaymentDto
                {
                    Id = paymentId,
                    InvoiceId = invoiceId,
                    PaymentDate = new DateOnly(2026, 6, 24),
                    Amount = 9000m,
                    Note = "stale delete",
                    IsDeleted = true,
                    ExpectedRevision = storedPayment.Revision + 1,
                    CreatedAtUtc = storedPayment.CreatedAtUtc,
                    UpdatedAtUtc = DateTime.UtcNow.AddMinutes(1)
                }
            ]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.Equal(1, result.ConflictCount);

        Assert.False(await _dbContext.Payments.IgnoreQueryFilters()
            .Where(payment => payment.Id == paymentId)
            .Select(payment => payment.IsDeleted)
            .SingleAsync());
        Assert.False(await _dbContext.PaymentAttachments.IgnoreQueryFilters()
            .Where(attachment => attachment.Id == paymentAttachmentId)
            .Select(attachment => attachment.IsDeleted)
            .SingleAsync());
        Assert.False(await _dbContext.Transactions.IgnoreQueryFilters()
            .Where(transaction => transaction.Id == paymentId)
            .Select(transaction => transaction.IsDeleted)
            .SingleAsync());
        Assert.False(await _dbContext.TransactionAttachments.IgnoreQueryFilters()
            .Where(attachment => attachment.Id == transactionAttachmentId)
            .Select(attachment => attachment.IsDeleted)
            .SingleAsync());
    }

    [Fact]
    public async Task Push_RelinkPaymentBetweenRentalInvoices_RecalculatesPreviousAndTargetSettlement()
    {
        var customerId = Guid.NewGuid();
        var firstProfileId = Guid.NewGuid();
        var secondProfileId = Guid.NewGuid();
        var firstRunId = Guid.NewGuid();
        var secondRunId = Guid.NewGuid();
        var firstInvoiceId = Guid.NewGuid();
        var secondInvoiceId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();

        _dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "SYNC-PAYMENT-RELINK-SETTLEMENT-CUSTOMER",
            NameMatchKey = "SYNCPAYMENTRELINKSETTLEMENTCUSTOMER",
            TradeType = "매출"
        });
        _dbContext.RentalBillingProfiles.AddRange(
            new RentalBillingProfile
            {
                Id = firstProfileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ProfileKey = $"payment-relink-first-{firstProfileId:N}",
                CustomerId = customerId,
                CustomerName = "SYNC-PAYMENT-RELINK-SETTLEMENT-CUSTOMER",
                BillingStatus = "청구중",
                SettlementStatus = "부분입금",
                CompletionStatus = "미완료",
                MonthlyAmount = 100_000m,
                SettledAmount = 40_000m,
                OutstandingAmount = 60_000m,
                BillingRunsJson = JsonSerializer.Serialize(new[]
                {
                    new SyncRentalBillingRunSnapshot
                    {
                        RunId = firstRunId,
                        RunKey = "2026-06",
                        ScheduledDate = new DateOnly(2026, 6, 25),
                        PeriodStartDate = new DateOnly(2026, 6, 1),
                        PeriodEndDate = new DateOnly(2026, 6, 30),
                        PeriodLabel = "2026-06",
                        Status = "청구중",
                        BilledAmount = 100_000m,
                        SettledAmount = 40_000m,
                        SettlementStatus = "부분입금"
                    }
                })
            },
            new RentalBillingProfile
            {
                Id = secondProfileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ProfileKey = $"payment-relink-second-{secondProfileId:N}",
                CustomerId = customerId,
                CustomerName = "SYNC-PAYMENT-RELINK-SETTLEMENT-CUSTOMER",
                BillingStatus = "청구중",
                SettlementStatus = "미입금",
                CompletionStatus = "미완료",
                MonthlyAmount = 100_000m,
                SettledAmount = 0m,
                OutstandingAmount = 100_000m,
                BillingRunsJson = JsonSerializer.Serialize(new[]
                {
                    new SyncRentalBillingRunSnapshot
                    {
                        RunId = secondRunId,
                        RunKey = "2026-07",
                        ScheduledDate = new DateOnly(2026, 7, 25),
                        PeriodStartDate = new DateOnly(2026, 7, 1),
                        PeriodEndDate = new DateOnly(2026, 7, 31),
                        PeriodLabel = "2026-07",
                        Status = "청구중",
                        BilledAmount = 100_000m,
                        SettledAmount = 0m,
                        SettlementStatus = "미입금"
                    }
                })
            });
        _dbContext.Invoices.AddRange(
            new Invoice
            {
                Id = firstInvoiceId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                InvoiceNumber = "SYNC-PAYMENT-RELINK-FIRST",
                VersionGroupId = firstInvoiceId,
                VersionNumber = 1,
                IsLatestVersion = true,
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026, 6, 25),
                TotalAmount = 100_000m,
                SupplyAmount = 90_909m,
                VatAmount = 9_091m,
                LinkedRentalBillingProfileId = firstProfileId,
                LinkedRentalBillingRunId = firstRunId
            },
            new Invoice
            {
                Id = secondInvoiceId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                InvoiceNumber = "SYNC-PAYMENT-RELINK-SECOND",
                VersionGroupId = secondInvoiceId,
                VersionNumber = 1,
                IsLatestVersion = true,
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026, 7, 25),
                TotalAmount = 100_000m,
                SupplyAmount = 90_909m,
                VatAmount = 9_091m,
                LinkedRentalBillingProfileId = secondProfileId,
                LinkedRentalBillingRunId = secondRunId
            });
        _dbContext.Payments.Add(new Payment
        {
            Id = paymentId,
            InvoiceId = firstInvoiceId,
            PaymentDate = new DateOnly(2026, 6, 26),
            Amount = 40_000m,
            Note = "payment before relink",
            CreatedAtUtc = new DateTime(2026, 6, 26, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 6, 26, 0, 0, 0, DateTimeKind.Utc)
        });
        await _dbContext.SaveChangesAsync();
        var storedPayment = await _dbContext.Payments.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(payment => payment.Id == paymentId);

        var response = await _controller.Push(new SyncPushRequest
        {
            DeviceId = "device-payment-relink-settlement",
            Payments =
            [
                new PaymentDto
                {
                    Id = paymentId,
                    InvoiceId = secondInvoiceId,
                    PaymentDate = new DateOnly(2026, 7, 26),
                    Amount = 40_000m,
                    Note = "payment relinked to second invoice",
                    ExpectedRevision = storedPayment.Revision,
                    CreatedAtUtc = storedPayment.CreatedAtUtc,
                    UpdatedAtUtc = DateTime.UtcNow.AddMinutes(1)
                }
            ]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.Equal(0, result.ConflictCount);

        Assert.Equal(secondInvoiceId, await _dbContext.Payments.IgnoreQueryFilters()
            .Where(payment => payment.Id == paymentId)
            .Select(payment => payment.InvoiceId)
            .SingleAsync());

        var firstProfile = await _dbContext.RentalBillingProfiles.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(profile => profile.Id == firstProfileId);
        var secondProfile = await _dbContext.RentalBillingProfiles.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(profile => profile.Id == secondProfileId);
        Assert.Equal(0m, firstProfile.SettledAmount);
        Assert.Equal(100_000m, firstProfile.OutstandingAmount);
        Assert.Equal("확인대기", firstProfile.SettlementStatus);
        Assert.Equal(40_000m, secondProfile.SettledAmount);
        Assert.Equal(60_000m, secondProfile.OutstandingAmount);
        Assert.Equal("부분입금", secondProfile.SettlementStatus);
    }

    [Fact]
    public async Task Push_DeleteDerivedLinkedPayment_DeletesSourceTransactionAndRevertsRentalSettlement()
    {
        var customerId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        _dbContext.Customers.Add(new Customer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "SYNC-DERIVED-PAYMENT-DELETE-CUSTOMER",
            NameMatchKey = "SYNCDERIVEDPAYMENTDELETECUSTOMER",
            TradeType = "매출"
        });
        _dbContext.RentalBillingProfiles.Add(new RentalBillingProfile
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ProfileKey = $"sync-derived-payment-delete-{profileId:N}",
            CustomerId = customerId,
            CustomerName = "SYNC-DERIVED-PAYMENT-DELETE-CUSTOMER",
            BillingStatus = "완료",
            SettlementStatus = "입금확인",
            CompletionStatus = "완료",
            MonthlyAmount = 100_000m,
            SettledAmount = 40_000m,
            OutstandingAmount = 60_000m,
            BillingRunsJson = JsonSerializer.Serialize(new[]
            {
                new
                {
                    RunId = runId,
                    RunKey = "2026-09",
                    ScheduledDate = new DateOnly(2026, 9, 25),
                    PeriodStartDate = new DateOnly(2026, 9, 1),
                    PeriodEndDate = new DateOnly(2026, 9, 30),
                    PeriodLabel = "2026-09",
                    Status = "청구중",
                    BilledAmount = 100_000m,
                    SettledAmount = 40_000m,
                    SettlementStatus = "부분입금",
                    SettledDate = new DateOnly(2026, 9, 26)
                }
            })
        });
        _dbContext.Invoices.Add(new Invoice
        {
            Id = invoiceId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "SYNC-DERIVED-PAY-001",
            LocalTempNumber = "SYNC-DERIVED-PAY-TMP-001",
            VersionGroupId = invoiceId,
            VersionNumber = 1,
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 9, 25),
            TotalAmount = 100_000m,
            SupplyAmount = 90_909m,
            VatAmount = 9_091m,
            LinkedRentalBillingProfileId = profileId,
            LinkedRentalBillingRunId = runId
        });
        _dbContext.Payments.Add(new Payment
        {
            Id = paymentId,
            InvoiceId = invoiceId,
            PaymentDate = new DateOnly(2026, 9, 26),
            Amount = 40_000m,
            Note = "derived payment before sync delete",
            CreatedAtUtc = new DateTime(2026, 9, 26, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 9, 26, 0, 0, 0, DateTimeKind.Utc)
        });
        _dbContext.Transactions.Add(new TransactionRecord
        {
            Id = paymentId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 9, 26),
            TransactionKind = "렌탈수금",
            LinkedInvoiceId = invoiceId,
            LinkedInvoiceNumber = "SYNC-DERIVED-PAY-001",
            LinkedRentalBillingProfileId = profileId,
            LinkedRentalBillingRunId = runId,
            ReceiptTotal = 40_000m,
            BankReceipt = 40_000m,
            SettlementAmount = 40_000m,
            Note = "source transaction before sync delete"
        });
        _dbContext.TransactionAttachments.Add(new TransactionAttachment
        {
            Id = Guid.NewGuid(),
            TransactionId = paymentId,
            FileName = "sync-derived-payment-delete.pdf",
            StoragePath = "storage/sync-derived-payment-delete.pdf"
        });
        await _dbContext.SaveChangesAsync();
        var storedPayment = await _dbContext.Payments.IgnoreQueryFilters().AsNoTracking().SingleAsync(payment => payment.Id == paymentId);

        var response = await _controller.Push(new SyncPushRequest
        {
            DeviceId = "device-derived-payment-delete",
            Payments =
            [
                new PaymentDto
                {
                    Id = paymentId,
                    InvoiceId = invoiceId,
                    PaymentDate = new DateOnly(2026, 9, 26),
                    Amount = 40_000m,
                    Note = "derived payment sync delete",
                    IsDeleted = true,
                    ExpectedRevision = storedPayment.Revision,
                    CreatedAtUtc = storedPayment.CreatedAtUtc,
                    UpdatedAtUtc = DateTime.UtcNow.AddMinutes(1)
                }
            ]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.Equal(0, result.ConflictCount);

        Assert.True(await _dbContext.Payments.IgnoreQueryFilters()
            .Where(payment => payment.Id == paymentId)
            .Select(payment => payment.IsDeleted)
            .SingleAsync());
        Assert.True(await _dbContext.Transactions.IgnoreQueryFilters()
            .Where(transaction => transaction.Id == paymentId)
            .Select(transaction => transaction.IsDeleted)
            .SingleAsync());
        Assert.True(await _dbContext.TransactionAttachments.IgnoreQueryFilters()
            .Where(attachment => attachment.TransactionId == paymentId)
            .Select(attachment => attachment.IsDeleted)
            .SingleAsync());

        var profile = await _dbContext.RentalBillingProfiles.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.Id == profileId);
        Assert.Equal(0m, profile.SettledAmount);
        Assert.Equal(100_000m, profile.OutstandingAmount);
        Assert.Equal("미완료", profile.CompletionStatus);
    }

    [Fact]
    public async Task Push_RejectsExistingPayment_WhenExistingInvoiceIsOutOfScope()
    {
        var visibleCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "PAYMENT-VISIBLE-CUSTOMER",
            NameMatchKey = "PAYMENTVISIBLECUSTOMER",
            TradeType = "매출"
        };
        var hiddenCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            NameOriginal = "PAYMENT-HIDDEN-CUSTOMER",
            NameMatchKey = "PAYMENTHIDDENCUSTOMER",
            TradeType = "매출"
        };
        var visibleInvoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = visibleCustomer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "PAY-VISIBLE-001",
            LocalTempNumber = "PAY-VISIBLE-TMP-001",
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 17),
            TotalAmount = 1000m,
            SupplyAmount = 909m,
            VatAmount = 91m,
            CreatedAtUtc = new DateTime(2026, 6, 17, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 6, 17, 0, 0, 0, DateTimeKind.Utc)
        };
        var hiddenInvoice = new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = hiddenCustomer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            InvoiceNumber = "PAY-HIDDEN-001",
            LocalTempNumber = "PAY-HIDDEN-TMP-001",
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 17),
            TotalAmount = 1000m,
            SupplyAmount = 909m,
            VatAmount = 91m,
            CreatedAtUtc = new DateTime(2026, 6, 17, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 6, 17, 0, 0, 0, DateTimeKind.Utc)
        };
        var paymentId = Guid.NewGuid();
        _dbContext.Customers.AddRange(visibleCustomer, hiddenCustomer);
        _dbContext.Invoices.AddRange(visibleInvoice, hiddenInvoice);
        _dbContext.Payments.Add(new Payment
        {
            Id = paymentId,
            InvoiceId = hiddenInvoice.Id,
            PaymentDate = new DateOnly(2026, 6, 17),
            Amount = 300m,
            Note = "hidden payment",
            CreatedAtUtc = new DateTime(2026, 6, 17, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 6, 17, 0, 0, 0, DateTimeKind.Utc)
        });
        await _dbContext.SaveChangesAsync();
        var existingPaymentRevision = await _dbContext.Payments
            .IgnoreQueryFilters()
            .Where(x => x.Id == paymentId)
            .Select(x => x.Revision)
            .FirstAsync();

        var currentUser = new TestCurrentUserContext
        {
            Username = "usenet-payment-editor",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = [PermissionNames.PaymentEdit]
        };
        await using var scopedDb = CreateDbContext(currentUser);
        var controller = CreateController(scopedDb, currentUser);
        var response = await controller.Push(new SyncPushRequest
        {
            DeviceId = "device-payment-out-of-scope-existing",
            Payments =
            [
                new PaymentDto
                {
                    Id = paymentId,
                    InvoiceId = visibleInvoice.Id,
                    PaymentDate = new DateOnly(2026, 6, 17),
                    Amount = 300m,
                    Note = "attempted payment relink",
                    CreatedAtUtc = new DateTime(2026, 6, 17, 0, 0, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2026, 6, 17, 0, 2, 0, DateTimeKind.Utc),
                    Revision = existingPaymentRevision
                }
            ]
        }, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            conflict.EntityName == nameof(Payment) &&
            conflict.Reason.Contains("outside the writable office scope", StringComparison.OrdinalIgnoreCase));

        await using var verificationDb = CreateDbContext(new TestCurrentUserContext
        {
            Username = "admin",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin,
            IsAdmin = true
        });
        var storedPayment = await verificationDb.Payments
            .IgnoreQueryFilters()
            .FirstAsync(x => x.Id == paymentId);
        Assert.Equal(hiddenInvoice.Id, storedPayment.InvoiceId);
        Assert.Equal("hidden payment", storedPayment.Note);
        Assert.False(storedPayment.IsDeleted);
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
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal("admin", conflict.ServerUsername);
        Assert.True(conflict.ServerUserId.HasValue);

        var storedInvoice = await _dbContext.Invoices.IgnoreQueryFilters().FirstAsync(x => x.Id == invoiceId);
        Assert.Equal("server invoice", storedInvoice.Memo);
    }

    [Fact]
    public async Task Push_ItemWarehouseStockConflict_IncludesServerActorFromCompositeAuditKey()
    {
        var serverUserId = Guid.NewGuid();
        var serverUser = new TestCurrentUserContext
        {
            UserId = serverUserId,
            Username = "stock-server-user",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = [PermissionNames.ItemEdit]
        };

        var itemId = Guid.NewGuid();
        await using (var serverDb = CreateDbContext(serverUser))
        {
            serverDb.Items.Add(new Item
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "STOCK-ACTOR-ITEM",
                NameMatchKey = "STOCKACTORITEM",
                SpecificationOriginal = "MODEL-A",
                SpecificationMatchKey = "MODELA",
                Unit = "EA",
                TrackingType = ItemTrackingTypes.Stock
            });
            serverDb.ItemWarehouseStocks.Add(new ItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 5m,
                UpdatedAtUtc = DateTime.UtcNow,
                Revision = 7
            });
            await serverDb.SaveChangesAsync();
        }

        var pushUser = new TestCurrentUserContext
        {
            Username = "stock-push-user",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = [PermissionNames.ItemEdit]
        };
        await using var pushDb = CreateDbContext(pushUser);
        var controller = CreateController(pushDb, pushUser);

        var response = await controller.Push(new SyncPushRequest
        {
            DeviceId = "stock-actor-conflict-device",
            ItemWarehouseStocks =
            [
                new ItemWarehouseStockDto
                {
                    ItemId = itemId,
                    WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                    Quantity = 3m,
                    UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-5),
                    Revision = 6,
                    ExpectedRevision = 6
                }
            ]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(1, result.ConflictCount);
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal(nameof(ItemWarehouseStock), conflict.EntityName);
        Assert.Equal($"{itemId:D}|{OfficeCodeCatalog.UsenetMainWarehouse}", conflict.EntityId);
        Assert.Equal(serverUserId, conflict.ServerUserId);
        Assert.Equal("stock-server-user", conflict.ServerUsername);
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
    public async Task Push_DoesNotPersistActiveLines_WhenNewInvoiceIsDeleted()
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "SYNC-DELETED-INVOICE-CUSTOMER",
            NameMatchKey = "SYNCDELETEDINVOICECUSTOMER",
            TradeType = "매출"
        };
        _dbContext.Customers.Add(customer);
        await _dbContext.SaveChangesAsync();

        var invoiceId = Guid.NewGuid();
        var request = new SyncPushRequest
        {
            DeviceId = "device-deleted-invoice-line-create",
            Invoices =
            [
                new InvoiceDto
                {
                    Id = invoiceId,
                    CustomerId = customer.Id,
                    CustomerName = customer.NameOriginal,
                    TenantCode = customer.TenantCode,
                    OfficeCode = customer.OfficeCode,
                    ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
                    VoucherType = VoucherType.Sales,
                    InvoiceDate = new DateOnly(2026, 5, 28),
                    InvoiceNumber = "INV-SYNC-DELETED-NEW",
                    IsDeleted = true,
                    Lines =
                    [
                        new InvoiceLineDto
                        {
                            Id = Guid.NewGuid(),
                            InvoiceId = invoiceId,
                            ItemNameOriginal = "line that must not remain active",
                            Unit = "EA",
                            Quantity = 1m,
                            UnitPrice = 1000m,
                            LineAmount = 1000m
                        }
                    ],
                    CreatedAtUtc = DateTime.UtcNow.AddMinutes(-1),
                    UpdatedAtUtc = DateTime.UtcNow
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(0, result.ConflictCount);
        Assert.True(await _dbContext.Invoices.IgnoreQueryFilters()
            .Where(invoice => invoice.Id == invoiceId)
            .Select(invoice => invoice.IsDeleted)
            .SingleAsync());
        Assert.False(await _dbContext.InvoiceLines.IgnoreQueryFilters()
            .AnyAsync(line => line.InvoiceId == invoiceId && !line.IsDeleted));
    }

    [Fact]
    public async Task Push_RejectsCustomerDelete_WhenActiveBusinessReferencesRemain()
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "SYNC-CUSTOMER-DELETE-BLOCK",
            NameMatchKey = "SYNCCUSTOMERDELETEBLOCK",
            TradeType = "매출"
        };
        _dbContext.Customers.Add(customer);
        _dbContext.Invoices.Add(new Invoice
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = customer.TenantCode,
            OfficeCode = customer.OfficeCode,
            ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 19),
            InvoiceNumber = "SYNC-CUSTOMER-DELETE-BLOCK-INVOICE"
        });
        await _dbContext.SaveChangesAsync();

        var stored = await _dbContext.Customers.IgnoreQueryFilters().SingleAsync(current => current.Id == customer.Id);
        var request = new SyncPushRequest
        {
            DeviceId = "device-customer-delete-reference-block",
            Customers =
            [
                new CustomerDto
                {
                    Id = stored.Id,
                    TenantCode = stored.TenantCode,
                    OfficeCode = stored.OfficeCode,
                    ResponsibleOfficeCode = stored.ResponsibleOfficeCode,
                    NameOriginal = stored.NameOriginal,
                    NameMatchKey = stored.NameMatchKey,
                    TradeType = stored.TradeType,
                    IsDeleted = true,
                    CreatedAtUtc = stored.CreatedAtUtc,
                    UpdatedAtUtc = DateTime.UtcNow,
                    Revision = stored.Revision,
                    ExpectedRevision = stored.Revision
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(1, result.ConflictCount);
        var conflict = Assert.Single(result.Conflicts, current => current.EntityName == nameof(Customer));
        Assert.Contains("전표 1건", conflict.Reason, StringComparison.Ordinal);
        Assert.False(await _dbContext.Customers.IgnoreQueryFilters()
            .Where(current => current.Id == customer.Id)
            .Select(current => current.IsDeleted)
            .SingleAsync());
    }

    [Fact]
    public async Task Push_RemovesActiveLines_WhenExistingInvoiceIsDeleted()
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "SYNC-EXISTING-DELETED-INVOICE-CUSTOMER",
            NameMatchKey = "SYNCEXISTINGDELETEDINVOICECUSTOMER",
            TradeType = "매출"
        };
        var invoiceId = Guid.NewGuid();
        _dbContext.Customers.Add(customer);
        _dbContext.Invoices.Add(new Invoice
        {
            Id = invoiceId,
            CustomerId = customer.Id,
            TenantCode = customer.TenantCode,
            OfficeCode = customer.OfficeCode,
            ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 5, 28),
            InvoiceNumber = "INV-SYNC-DELETED-EXISTING"
        });
        _dbContext.InvoiceLines.Add(new InvoiceLine
        {
            Id = Guid.NewGuid(),
            InvoiceId = invoiceId,
            ItemNameOriginal = "existing active line",
            Unit = "EA",
            Quantity = 1m,
            UnitPrice = 1000m,
            LineAmount = 1000m
        });
        await _dbContext.SaveChangesAsync();

        var storedInvoice = await _dbContext.Invoices.IgnoreQueryFilters()
            .SingleAsync(invoice => invoice.Id == invoiceId);
        var request = new SyncPushRequest
        {
            DeviceId = "device-deleted-invoice-line-update",
            Invoices =
            [
                new InvoiceDto
                {
                    Id = invoiceId,
                    CustomerId = customer.Id,
                    CustomerName = customer.NameOriginal,
                    TenantCode = customer.TenantCode,
                    OfficeCode = customer.OfficeCode,
                    ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
                    VoucherType = VoucherType.Sales,
                    InvoiceDate = storedInvoice.InvoiceDate,
                    InvoiceNumber = storedInvoice.InvoiceNumber,
                    ExpectedRevision = storedInvoice.Revision,
                    IsDeleted = true,
                    Lines =
                    [
                        new InvoiceLineDto
                        {
                            Id = Guid.NewGuid(),
                            InvoiceId = invoiceId,
                            ItemNameOriginal = "incoming active line",
                            Unit = "EA",
                            Quantity = 1m,
                            UnitPrice = 2000m,
                            LineAmount = 2000m
                        }
                    ],
                    CreatedAtUtc = storedInvoice.CreatedAtUtc,
                    UpdatedAtUtc = storedInvoice.UpdatedAtUtc.AddMinutes(1)
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(0, result.ConflictCount);
        Assert.True(await _dbContext.Invoices.IgnoreQueryFilters()
            .Where(invoice => invoice.Id == invoiceId)
            .Select(invoice => invoice.IsDeleted)
            .SingleAsync());
        Assert.False(await _dbContext.InvoiceLines.IgnoreQueryFilters()
            .AnyAsync(line => line.InvoiceId == invoiceId && !line.IsDeleted));
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

    [Fact]
    public async Task Push_RejectsExistingTransactionAttachment_WhenExistingTransactionReferenceIsHardMissingForScopedUser()
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "ORPHAN-ATTACHMENT-CUSTOMER",
            NameMatchKey = "ORPHANATTACHMENTCUSTOMER",
            TradeType = "Sales"
        };
        var missingTransactionId = Guid.NewGuid();
        var visibleTransactionId = Guid.NewGuid();
        var attachmentId = Guid.NewGuid();
        var createdAtUtc = new DateTime(2026, 6, 23, 0, 0, 0, DateTimeKind.Utc);
        _dbContext.Customers.Add(customer);
        _dbContext.Transactions.AddRange(
            new TransactionRecord
            {
                Id = missingTransactionId,
                CustomerId = customer.Id,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionDate = new DateOnly(2026, 6, 23),
                TransactionKind = "Receipt",
                CashReceipt = 1000m,
                ReceiptTotal = 1000m,
                SettlementAmount = 1000m,
                CreatedAtUtc = createdAtUtc,
                UpdatedAtUtc = createdAtUtc
            },
            new TransactionRecord
            {
                Id = visibleTransactionId,
                CustomerId = customer.Id,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionDate = new DateOnly(2026, 6, 23),
                TransactionKind = "Receipt",
                CashReceipt = 2000m,
                ReceiptTotal = 2000m,
                SettlementAmount = 2000m,
                CreatedAtUtc = createdAtUtc,
                UpdatedAtUtc = createdAtUtc
            });
        _dbContext.TransactionAttachments.Add(new TransactionAttachment
        {
            Id = attachmentId,
            TransactionId = missingTransactionId,
            AttachmentType = "Evidence",
            FileName = "orphan-receipt.pdf",
            MimeType = "application/pdf",
            FileSize = 10,
            FileHash = "orphan-hash",
            Description = "orphan attachment must not be modified by scoped users",
            UploadedByUsername = "admin",
            UploadedAtUtc = createdAtUtc,
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = createdAtUtc
        });
        await _dbContext.SaveChangesAsync();

        _dbContext.ChangeTracker.Clear();
        await _dbContext.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        await _dbContext.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM "Transactions" WHERE "Id" = {missingTransactionId};""");
        await _dbContext.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");

        var currentUser = new TestCurrentUserContext
        {
            Username = "usenet-payment-editor",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = [PermissionNames.PaymentEdit]
        };
        await using var scopedDb = CreateDbContext(currentUser);
        var controller = CreateController(scopedDb, currentUser);
        var scopedExisting = await scopedDb.TransactionAttachments
            .IgnoreQueryFilters()
            .FirstAsync(current => current.Id == attachmentId);
        Assert.Equal(missingTransactionId, scopedExisting.TransactionId);
        Assert.False(await scopedDb.Transactions.IgnoreQueryFilters().AnyAsync(current => current.Id == missingTransactionId));

        var response = await controller.Push(new SyncPushRequest
        {
            DeviceId = "device-transaction-attachment-hard-missing-parent",
            TransactionAttachments =
            [
                new TransactionAttachmentDto
                {
                    Id = attachmentId,
                    TransactionId = visibleTransactionId,
                    IsDeleted = false,
                    FileName = "orphan-receipt.pdf",
                    MimeType = "application/pdf",
                    AttachmentType = "Evidence",
                    FileContent = TestPdfBytes(),
                    CreatedAtUtc = new DateTime(2026, 6, 23, 0, 1, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2026, 6, 23, 0, 2, 0, DateTimeKind.Utc)
                }
            ]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            conflict.EntityName == nameof(TransactionAttachment) &&
            conflict.Reason.Contains("cannot verify writable office scope", StringComparison.OrdinalIgnoreCase));

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
        Assert.Equal(missingTransactionId, storedAttachment.TransactionId);
        Assert.False(storedAttachment.IsDeleted);
        Assert.Equal("orphan-hash", storedAttachment.FileHash);
    }

    [Fact]
    public async Task Push_RejectsTransactionAttachment_WhenFileTypeUnsupported()
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "TRANSACTION-ATTACHMENT-UNSUPPORTED-CUSTOMER",
            NameMatchKey = "TRANSACTIONATTACHMENTUNSUPPORTEDCUSTOMER",
            TradeType = "Sales"
        };
        var transaction = new TransactionRecord
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 6, 19),
            TransactionKind = "일반수금",
            CashReceipt = 1000m,
            ReceiptTotal = 1000m,
            SettlementAmount = 1000m,
            CreatedAtUtc = new DateTime(2026, 6, 19, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 6, 19, 0, 0, 0, DateTimeKind.Utc)
        };
        _dbContext.Customers.Add(customer);
        _dbContext.Transactions.Add(transaction);
        await _dbContext.SaveChangesAsync();

        var attachmentId = Guid.NewGuid();
        var response = await _controller.Push(new SyncPushRequest
        {
            DeviceId = "device-transaction-attachment-unsupported-type",
            TransactionAttachments =
            [
                new TransactionAttachmentDto
                {
                    Id = attachmentId,
                    TransactionId = transaction.Id,
                    AttachmentType = "기타",
                    FileName = "payload.exe",
                    MimeType = "application/octet-stream",
                    Description = "unsupported executable must not be stored",
                    UploadedByUsername = "admin",
                    UploadedAtUtc = new DateTime(2026, 6, 19, 0, 1, 0, DateTimeKind.Utc),
                    CreatedAtUtc = new DateTime(2026, 6, 19, 0, 1, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2026, 6, 19, 0, 2, 0, DateTimeKind.Utc),
                    FileContent = [0x4D, 0x5A, 0x90, 0x00]
                }
            ]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            conflict.EntityName == nameof(TransactionAttachment) &&
            conflict.Reason.Contains("Only PDF or image attachments are allowed", StringComparison.OrdinalIgnoreCase));
        Assert.False(await _dbContext.TransactionAttachments.IgnoreQueryFilters()
            .AnyAsync(current => current.Id == attachmentId));
    }

    [Fact]
    public async Task Push_RejectsTransactionAttachment_WhenFileContentDoesNotMatchFileType()
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "TRANSACTION-ATTACHMENT-SIGNATURE-CUSTOMER",
            NameMatchKey = "TRANSACTIONATTACHMENTSIGNATURECUSTOMER",
            TradeType = "Sales"
        };
        var transaction = new TransactionRecord
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 6, 19),
            TransactionKind = "일반수금",
            CashReceipt = 1000m,
            ReceiptTotal = 1000m,
            SettlementAmount = 1000m,
            CreatedAtUtc = new DateTime(2026, 6, 19, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 6, 19, 0, 0, 0, DateTimeKind.Utc)
        };
        _dbContext.Customers.Add(customer);
        _dbContext.Transactions.Add(transaction);
        await _dbContext.SaveChangesAsync();

        var attachmentId = Guid.NewGuid();
        var response = await _controller.Push(new SyncPushRequest
        {
            DeviceId = "device-transaction-attachment-signature-mismatch",
            TransactionAttachments =
            [
                new TransactionAttachmentDto
                {
                    Id = attachmentId,
                    TransactionId = transaction.Id,
                    AttachmentType = "기타",
                    FileName = "receipt.pdf",
                    MimeType = "application/pdf",
                    Description = "fake pdf content must not be stored",
                    UploadedByUsername = "admin",
                    UploadedAtUtc = new DateTime(2026, 6, 19, 0, 1, 0, DateTimeKind.Utc),
                    CreatedAtUtc = new DateTime(2026, 6, 19, 0, 1, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2026, 6, 19, 0, 2, 0, DateTimeKind.Utc),
                    FileContent = [0x4D, 0x5A, 0x90, 0x00]
                }
            ]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            conflict.EntityName == nameof(TransactionAttachment) &&
            conflict.Reason.Contains("does not match the declared file type", StringComparison.OrdinalIgnoreCase));
        Assert.False(await _dbContext.TransactionAttachments.IgnoreQueryFilters()
            .AnyAsync(current => current.Id == attachmentId));
    }

    [Fact]
    public async Task Push_NormalizesTransactionAttachmentFileMetadata_FromUploadedContent()
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "TRANSACTION-ATTACHMENT-METADATA-CUSTOMER",
            NameMatchKey = "TRANSACTIONATTACHMENTMETADATACUSTOMER",
            TradeType = "Sales"
        };
        var transaction = new TransactionRecord
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 6, 18),
            TransactionKind = "일반수금",
            CashReceipt = 1000m,
            ReceiptTotal = 1000m,
            SettlementAmount = 1000m,
            CreatedAtUtc = new DateTime(2026, 6, 18, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 6, 18, 0, 0, 0, DateTimeKind.Utc)
        };
        _dbContext.Customers.Add(customer);
        _dbContext.Transactions.Add(transaction);
        await _dbContext.SaveChangesAsync();

        var attachmentId = Guid.NewGuid();
        var fileBytes = TestPdfBytes();
        var request = new SyncPushRequest
        {
            DeviceId = "device-transaction-attachment-file-metadata",
            TransactionAttachments =
            [
                new TransactionAttachmentDto
                {
                    Id = attachmentId,
                    TransactionId = transaction.Id,
                    AttachmentType = "기타",
                    FileName = "receipt.pdf",
                    MimeType = "application/pdf",
                    FileSize = 999,
                    FileHash = "WRONG-CLIENT-HASH",
                    Description = "server should normalize attachment metadata",
                    UploadedByUsername = "admin",
                    UploadedAtUtc = new DateTime(2026, 6, 18, 0, 1, 0, DateTimeKind.Utc),
                    CreatedAtUtc = new DateTime(2026, 6, 18, 0, 1, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2026, 6, 18, 0, 2, 0, DateTimeKind.Utc),
                    FileContent = fileBytes
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(0, result.ConflictCount);
        _dbContext.ChangeTracker.Clear();
        var stored = await _dbContext.TransactionAttachments.IgnoreQueryFilters().FirstAsync(current => current.Id == attachmentId);
        Assert.Equal(fileBytes.LongLength, stored.FileSize);
        Assert.Equal(ComputeTestSha256Hex(fileBytes), stored.FileHash);
        Assert.Empty(stored.FileContent);
        Assert.False(string.IsNullOrWhiteSpace(stored.StoragePath));
    }

    [Fact]
    public async Task Push_RejectsExistingTransactionAttachment_WhenExistingTransactionIsOutOfScope()
    {
        var visibleCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "ATTACHMENT-VISIBLE-CUSTOMER",
            NameMatchKey = "ATTACHMENTVISIBLECUSTOMER",
            TradeType = "매출"
        };
        var hiddenCustomer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            NameOriginal = "ATTACHMENT-HIDDEN-CUSTOMER",
            NameMatchKey = "ATTACHMENTHIDDENCUSTOMER",
            TradeType = "매출"
        };
        var visibleTransaction = new TransactionRecord
        {
            Id = Guid.NewGuid(),
            CustomerId = visibleCustomer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 6, 17),
            TransactionKind = "일반수금",
            CashReceipt = 500m,
            ReceiptTotal = 500m,
            SettlementAmount = 500m,
            CreatedAtUtc = new DateTime(2026, 6, 17, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 6, 17, 0, 0, 0, DateTimeKind.Utc)
        };
        var hiddenTransaction = new TransactionRecord
        {
            Id = Guid.NewGuid(),
            CustomerId = hiddenCustomer.Id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            TransactionDate = new DateOnly(2026, 6, 17),
            TransactionKind = "일반수금",
            CashReceipt = 700m,
            ReceiptTotal = 700m,
            SettlementAmount = 700m,
            CreatedAtUtc = new DateTime(2026, 6, 17, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 6, 17, 0, 0, 0, DateTimeKind.Utc)
        };
        var attachmentId = Guid.NewGuid();
        _dbContext.Customers.AddRange(visibleCustomer, hiddenCustomer);
        _dbContext.Transactions.AddRange(visibleTransaction, hiddenTransaction);
        _dbContext.TransactionAttachments.Add(new TransactionAttachment
        {
            Id = attachmentId,
            TransactionId = hiddenTransaction.Id,
            AttachmentType = "기타",
            FileName = "hidden-receipt.pdf",
            MimeType = "application/pdf",
            FileSize = 10,
            FileHash = "hidden-hash",
            Description = "hidden attachment",
            UploadedByUsername = "admin",
            UploadedAtUtc = new DateTime(2026, 6, 17, 0, 0, 0, DateTimeKind.Utc),
            CreatedAtUtc = new DateTime(2026, 6, 17, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 6, 17, 0, 0, 0, DateTimeKind.Utc)
        });
        await _dbContext.SaveChangesAsync();
        var existingAttachmentRevision = await _dbContext.TransactionAttachments
            .IgnoreQueryFilters()
            .Where(x => x.Id == attachmentId)
            .Select(x => x.Revision)
            .FirstAsync();

        var currentUser = new TestCurrentUserContext
        {
            Username = "usenet-payment-editor",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = [PermissionNames.PaymentEdit]
        };
        await using var scopedDb = CreateDbContext(currentUser);
        var controller = CreateController(scopedDb, currentUser);
        var response = await controller.Push(new SyncPushRequest
        {
            DeviceId = "device-transaction-attachment-out-of-scope-existing",
            TransactionAttachments =
            [
                new TransactionAttachmentDto
                {
                    Id = attachmentId,
                    TransactionId = visibleTransaction.Id,
                    AttachmentType = "기타",
                    FileName = "moved-receipt.pdf",
                    MimeType = "application/pdf",
                    FileSize = 3,
                    FileHash = "moved-hash",
                    Description = "attempted relink",
                    UploadedByUsername = "usenet-payment-editor",
                    UploadedAtUtc = new DateTime(2026, 6, 17, 0, 1, 0, DateTimeKind.Utc),
                    FileContent = [1, 2, 3],
                    CreatedAtUtc = new DateTime(2026, 6, 17, 0, 0, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2026, 6, 17, 0, 2, 0, DateTimeKind.Utc),
                    Revision = existingAttachmentRevision
                }
            ]
        }, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            conflict.EntityName == nameof(TransactionAttachment) &&
            conflict.Reason.Contains("outside the writable office scope", StringComparison.OrdinalIgnoreCase));

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
        Assert.Equal(hiddenTransaction.Id, storedAttachment.TransactionId);
        Assert.Equal("hidden-receipt.pdf", storedAttachment.FileName);
        Assert.False(storedAttachment.IsDeleted);
    }

    [Fact]
    public async Task Push_ReusesExistingRentalManagementCompany_WhenIncomingIdDiffersButTenantAndCodeMatch()
    {
        var existing = new RentalManagementCompany
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.Itworld,
            Code = OfficeCodeCatalog.Itworld,
            Name = "아이티월드",
            UpdatedAtUtc = new DateTime(2026, 4, 8, 0, 0, 0, DateTimeKind.Utc)
        };
        _dbContext.RentalManagementCompanies.Add(existing);
        await _dbContext.SaveChangesAsync();

        var request = new SyncPushRequest
        {
            RentalManagementCompanies =
            [
                new RentalManagementCompanyDto
                {
                    Id = Guid.NewGuid(),
                    TenantCode = TenantScopeCatalog.Itworld,
                    Code = OfficeCodeCatalog.Itworld,
                    Name = "아이티월드(수정)",
                    CreatedAtUtc = existing.CreatedAtUtc,
                    UpdatedAtUtc = existing.UpdatedAtUtc.AddMinutes(1)
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.Equal(0, result.ConflictCount);

        var companies = await _dbContext.RentalManagementCompanies.IgnoreQueryFilters().ToListAsync();
        Assert.Single(companies);
        Assert.Equal(existing.Id, companies[0].Id);
        Assert.Equal("아이티월드(수정)", companies[0].Name);
    }

    [Fact]
    public async Task Push_AllowsCombinedRentalPush_WhenReferencedManagementCompanyAlreadyExistsByNaturalKey()
    {
        var company = new RentalManagementCompany
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.Itworld,
            Code = OfficeCodeCatalog.Itworld,
            Name = "아이티월드",
            UpdatedAtUtc = new DateTime(2026, 4, 8, 0, 0, 0, DateTimeKind.Utc)
        };
        _dbContext.RentalManagementCompanies.Add(company);
        await _dbContext.SaveChangesAsync();

        var request = new SyncPushRequest
        {
            RentalManagementCompanies =
            [
                new RentalManagementCompanyDto
                {
                    Id = Guid.NewGuid(),
                    TenantCode = TenantScopeCatalog.Itworld,
                    Code = OfficeCodeCatalog.Itworld,
                    Name = "아이티월드",
                    CreatedAtUtc = company.CreatedAtUtc,
                    UpdatedAtUtc = company.UpdatedAtUtc.AddMinutes(1)
                }
            ],
            RentalAssets =
            [
                new RentalAssetDto
                {
                    Id = Guid.NewGuid(),
                    TenantCode = TenantScopeCatalog.Itworld,
                    OfficeCode = OfficeCodeCatalog.Itworld,
                    ManagementCompanyCode = OfficeCodeCatalog.Itworld,
                    ManagementId = "900",
                    ManagementNumber = "2604-900",
                    CustomerName = "테스트 거래처",
                    ItemName = "MODEL-900",
                    CurrentLocation = "창고",
                    CreatedAtUtc = new DateTime(2026, 4, 8, 0, 0, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2026, 4, 8, 0, 10, 0, DateTimeKind.Utc)
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);
        Assert.Equal(0, result.ConflictCount);

        Assert.Single(await _dbContext.RentalManagementCompanies.IgnoreQueryFilters().ToListAsync());
        Assert.Single(await _dbContext.RentalAssets.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Push_MergesItemByNaturalKey_AndReportsNotice()
    {
        var existing = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            NameOriginal = "A3 컬러복합기",
            NameMatchKey = "A3컬러복합기",
            SpecificationOriginal = "IMC2010",
            SpecificationMatchKey = "IMC2010",
            CategoryName = "A3컬러복합기",
            ItemKind = ItemKinds.Asset,
            TrackingType = ItemTrackingTypes.Asset,
            MaterialNumber = "2603-501",
            SerialNumber = "SN-501"
        };
        _dbContext.Items.Add(existing);
        await _dbContext.SaveChangesAsync();

        var request = new SyncPushRequest
        {
            DeviceId = "device-item-natural-key",
            Items =
            [
                new ItemDto
                {
                    Id = Guid.NewGuid(),
                    TenantCode = TenantScopeCatalog.Itworld,
                    OfficeCode = OfficeCodeCatalog.Itworld,
                    NameOriginal = existing.NameOriginal,
                    NameMatchKey = existing.NameMatchKey,
                    SpecificationOriginal = existing.SpecificationOriginal,
                    SpecificationMatchKey = existing.SpecificationMatchKey,
                    CategoryName = existing.CategoryName,
                    ItemKind = existing.ItemKind,
                    TrackingType = existing.TrackingType,
                    MaterialNumber = existing.MaterialNumber,
                    SerialNumber = "SN-501-UPDATED",
                    Notes = "병합 업데이트",
                    CreatedAtUtc = existing.CreatedAtUtc,
                    UpdatedAtUtc = existing.UpdatedAtUtc.AddMinutes(1)
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(1, result.AcceptedCount);
        Assert.Contains(result.Notices, notice => notice.Code == "item-natural-key-merged" && notice.EntityId == existing.Id.ToString("D"));

        var items = await _dbContext.Items.IgnoreQueryFilters().Where(item => item.OfficeCode == OfficeCodeCatalog.Itworld).ToListAsync();
        Assert.Single(items);
        Assert.Equal(existing.Id, items[0].Id);
        Assert.Equal("병합 업데이트", items[0].Notes);
        Assert.Equal("SN-501-UPDATED", items[0].SerialNumber);
    }

    [Fact]
    public async Task Push_ClearsMissingInvoiceLinkForTransaction_AndReportsNotice()
    {
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
        _dbContext.Customers.Add(customer);
        await _dbContext.SaveChangesAsync();

        var transactionId = Guid.NewGuid();
        var request = new SyncPushRequest
        {
            DeviceId = "device-transaction-repair",
            Transactions =
            [
                new TransactionDto
                {
                    Id = transactionId,
                    CustomerId = customer.Id,
                    TenantCode = customer.TenantCode,
                    OfficeCode = customer.OfficeCode,
                    ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
                    TransactionDate = new DateOnly(2026, 4, 13),
                    TransactionKind = "전표수금",
                    LinkedInvoiceId = Guid.NewGuid(),
                    LinkedInvoiceNumber = "INV-MISSING",
                    ReceiptTotal = 10000m,
                    SettlementAmount = 10000m,
                    CreatedAtUtc = DateTime.UtcNow.AddMinutes(-1),
                    UpdatedAtUtc = DateTime.UtcNow
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(1, result.AcceptedCount);
        Assert.Contains(result.Notices, notice => notice.Code == "transaction-invoice-link-cleared" && notice.EntityId == transactionId.ToString("D"));
        Assert.Contains(result.Notices, notice => notice.Code == "transaction-kind-normalized" && notice.EntityId == transactionId.ToString("D"));
        Assert.Contains(result.Notices, notice => notice.Code == "transaction-link-updated" && notice.EntityId == transactionId.ToString("D"));

        var stored = await _dbContext.Transactions.IgnoreQueryFilters().FirstAsync(x => x.Id == transactionId);
        Assert.Null(stored.LinkedInvoiceId);
        Assert.Equal("일반수금", stored.TransactionKind);
        Assert.Equal(0m, stored.SettlementAmount);
    }

    [Fact]
    public async Task Push_RelinksInvoiceCustomerByName_AndReportsNotice()
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "인천보건환경연구원[총무과]",
            NameMatchKey = "인천보건환경연구원총무과",
            TradeType = "매출"
        };
        _dbContext.Customers.Add(customer);
        await _dbContext.SaveChangesAsync();

        var invoiceId = Guid.NewGuid();
        var request = new SyncPushRequest
        {
            DeviceId = "device-invoice-relink",
            Invoices =
            [
                new InvoiceDto
                {
                    Id = invoiceId,
                    CustomerId = Guid.Empty,
                    CustomerName = customer.NameOriginal,
                    TenantCode = customer.TenantCode,
                    OfficeCode = customer.OfficeCode,
                    ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
                    VoucherType = VoucherType.Sales,
                    InvoiceDate = new DateOnly(2026, 4, 13),
                    InvoiceNumber = "INV-REL-001",
                    Lines =
                    [
                        new InvoiceLineDto
                        {
                            Id = Guid.NewGuid(),
                            InvoiceId = invoiceId,
                            ItemNameOriginal = "정기 청구",
                            Unit = "건",
                            Quantity = 1m,
                            UnitPrice = 50000m,
                            LineAmount = 50000m
                        }
                    ],
                    CreatedAtUtc = DateTime.UtcNow.AddMinutes(-1),
                    UpdatedAtUtc = DateTime.UtcNow
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(1, result.AcceptedCount);
        Assert.Contains(result.Notices, notice => notice.Code == "invoice-customer-relinked" && notice.EntityId == invoiceId.ToString("D"));

        var stored = await _dbContext.Invoices.IgnoreQueryFilters().FirstAsync(x => x.Id == invoiceId);
        Assert.Equal(customer.Id, stored.CustomerId);
    }

    [Fact]
    public async Task Push_SkipsOutOfScopeWarehouseStock_AndReportsNotice()
    {
        var scopedUser = new TestCurrentUserContext
        {
            Username = "usenet-manager",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeTenantAll
        };

        await using var scopedDb = CreateDbContext(scopedUser);
        var controller = CreateController(scopedDb, scopedUser);

        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "재고 품목",
            NameMatchKey = "재고품목",
            ItemKind = ItemKinds.Product,
            TrackingType = ItemTrackingTypes.Stock
        };
        scopedDb.Items.Add(item);
        await scopedDb.SaveChangesAsync();

        var request = new SyncPushRequest
        {
            DeviceId = "device-stock-scope",
            ItemWarehouseStocks =
            [
                new ItemWarehouseStockDto
                {
                    ItemId = item.Id,
                    WarehouseCode = OfficeCodeCatalog.ItworldMainWarehouse,
                    Quantity = 5m,
                    UpdatedAtUtc = DateTime.UtcNow
                }
            ]
        };

        var response = await controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Contains(result.Notices, notice => notice.Code == "item-warehouse-stock-skip-out-of-scope-warehouse");
        Assert.False(await scopedDb.ItemWarehouseStocks.AnyAsync(stock => stock.ItemId == item.Id));
    }

    [Fact]
    public async Task Push_RemovesWarehouseStockRows_WhenItemDeletedBySync()
    {
        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Sync deleted stock item",
            NameMatchKey = "SYNCDELETEDSTOCKITEM",
            SpecificationOriginal = "A",
            SpecificationMatchKey = "A",
            Unit = "EA",
            ItemKind = ItemKinds.Product,
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 8m,
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-10),
            UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-5)
        };
        _dbContext.Items.Add(item);
        _dbContext.ItemWarehouseStocks.AddRange(
            new ItemWarehouseStock
            {
                ItemId = item.Id,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 5m,
                UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-4),
                Revision = 10
            },
            new ItemWarehouseStock
            {
                ItemId = item.Id,
                WarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                Quantity = 3m,
                UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-3),
                Revision = 11
            });
        await _dbContext.SaveChangesAsync();

        var expectedRevision = await _dbContext.Items
            .IgnoreQueryFilters()
            .Where(current => current.Id == item.Id)
            .Select(current => current.Revision)
            .SingleAsync();

        var response = await _controller.Push(new SyncPushRequest
        {
            DeviceId = "device-sync-delete-item-stock-cleanup",
            Items =
            [
                new ItemDto
                {
                    Id = item.Id,
                    TenantCode = item.TenantCode,
                    OfficeCode = item.OfficeCode,
                    NameOriginal = item.NameOriginal,
                    NameMatchKey = item.NameMatchKey,
                    SpecificationOriginal = item.SpecificationOriginal,
                    SpecificationMatchKey = item.SpecificationMatchKey,
                    Unit = item.Unit,
                    ItemKind = item.ItemKind,
                    TrackingType = item.TrackingType,
                    IsDeleted = true,
                    ExpectedRevision = expectedRevision,
                    CreatedAtUtc = item.CreatedAtUtc,
                    UpdatedAtUtc = DateTime.UtcNow
                }
            ]
        }, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(0, result.ConflictCount);
        Assert.Equal(1, result.AcceptedCount);
        Assert.True(await _dbContext.Items
            .IgnoreQueryFilters()
            .Where(current => current.Id == item.Id)
            .Select(current => current.IsDeleted)
            .SingleAsync());
        Assert.False(await _dbContext.ItemWarehouseStocks
            .IgnoreQueryFilters()
            .AnyAsync(stock => stock.ItemId == item.Id));
    }

    [Fact]
    public async Task Pull_ExcludesWarehouseStockRows_ForDeletedItems()
    {
        var scopedUser = new TestCurrentUserContext
        {
            Username = "usenet-stock-viewer",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly
        };

        await using var scopedDb = CreateDbContext(scopedUser);
        var controller = CreateController(scopedDb, scopedUser);

        var activeItem = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Active stock item",
            NameMatchKey = "ACTIVESTOCKITEM",
            ItemKind = ItemKinds.Product,
            TrackingType = ItemTrackingTypes.Stock
        };
        var deletedItem = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Deleted stock item",
            NameMatchKey = "DELETEDSTOCKITEM",
            ItemKind = ItemKinds.Product,
            TrackingType = ItemTrackingTypes.Stock,
            IsDeleted = true
        };
        scopedDb.Items.AddRange(activeItem, deletedItem);
        scopedDb.ItemWarehouseStocks.AddRange(
            new ItemWarehouseStock
            {
                ItemId = activeItem.Id,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 3m,
                UpdatedAtUtc = DateTime.UtcNow,
                Revision = 10
            },
            new ItemWarehouseStock
            {
                ItemId = deletedItem.Id,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 0m,
                UpdatedAtUtc = DateTime.UtcNow,
                Revision = 11
            });
        await scopedDb.SaveChangesAsync();

        var response = await controller.Pull(0, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPullResponse>(ok.Value);

        Assert.Contains(result.ItemWarehouseStocks, stock => stock.ItemId == activeItem.Id);
        Assert.DoesNotContain(result.ItemWarehouseStocks, stock => stock.ItemId == deletedItem.Id);
    }

    [Fact]
    public async Task Pull_GlobalAdmin_ExcludesWarehouseStockRows_ForDeletedItems()
    {
        var activeItem = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Admin active stock item",
            NameMatchKey = "ADMINACTIVESTOCKITEM",
            ItemKind = ItemKinds.Product,
            TrackingType = ItemTrackingTypes.Stock
        };
        var deletedItem = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Admin deleted stock item",
            NameMatchKey = "ADMINDELETEDSTOCKITEM",
            ItemKind = ItemKinds.Product,
            TrackingType = ItemTrackingTypes.Stock,
            IsDeleted = true
        };
        _dbContext.Items.AddRange(activeItem, deletedItem);
        _dbContext.ItemWarehouseStocks.AddRange(
            new ItemWarehouseStock
            {
                ItemId = activeItem.Id,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 2m,
                UpdatedAtUtc = DateTime.UtcNow,
                Revision = 20
            },
            new ItemWarehouseStock
            {
                ItemId = deletedItem.Id,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 0m,
                UpdatedAtUtc = DateTime.UtcNow,
                Revision = 21
            });
        await _dbContext.SaveChangesAsync();

        var response = await _controller.Pull(0, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPullResponse>(ok.Value);

        Assert.Contains(result.ItemWarehouseStocks, stock => stock.ItemId == activeItem.Id);
        Assert.DoesNotContain(result.ItemWarehouseStocks, stock => stock.ItemId == deletedItem.Id);
    }

    [Fact]
    public async Task Push_RejectsStaleItemWarehouseStockExpectedRevision()
    {
        var itemId = Guid.NewGuid();
        _dbContext.Items.Add(new Item
        {
            Id = itemId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Stock item",
            NameMatchKey = "STOCKITEM",
            ItemKind = ItemKinds.Product,
            TrackingType = ItemTrackingTypes.Stock
        });
        _dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
        {
            ItemId = itemId,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 10m,
            UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            Revision = 200
        });
        await _dbContext.SaveChangesAsync();

        var request = new SyncPushRequest
        {
            DeviceId = "device-stock-stale",
            ItemWarehouseStocks =
            [
                new ItemWarehouseStockDto
                {
                    ItemId = itemId,
                    WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                    Quantity = 99m,
                    UpdatedAtUtc = DateTime.UtcNow,
                    Revision = 199,
                    ExpectedRevision = 199
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(1, result.ConflictCount);
        var stored = await _dbContext.ItemWarehouseStocks.FirstAsync(stock => stock.ItemId == itemId);
        Assert.Equal(10m, stored.Quantity);
        Assert.Equal(200, stored.Revision);
    }

    [Fact]
    public async Task Push_PreservesNewerServerItemWarehouseStockRows_WhenClientOmitsThem()
    {
        var itemId = Guid.NewGuid();
        _dbContext.Items.Add(new Item
        {
            Id = itemId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Multi warehouse item",
            NameMatchKey = "MULTIWAREHOUSEITEM",
            ItemKind = ItemKinds.Product,
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 15m,
            Revision = 100
        });
        _dbContext.ItemWarehouseStocks.AddRange(
            new ItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 10m,
                UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-5),
                Revision = 200
            },
            new ItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                Quantity = 5m,
                UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-1),
                Revision = 300
            });
        await _dbContext.SaveChangesAsync();

        var request = new SyncPushRequest
        {
            DeviceId = "device-stock-preserve",
            ItemWarehouseStocks =
            [
                new ItemWarehouseStockDto
                {
                    ItemId = itemId,
                    WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                    Quantity = 11m,
                    UpdatedAtUtc = DateTime.UtcNow,
                    Revision = 200,
                    ExpectedRevision = 200
                }
            ]
        };

        var response = await _controller.Push(request, CancellationToken.None);
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(0, result.ConflictCount);
        Assert.Contains(result.Notices, notice => notice.Code == "item-warehouse-stock-preserve-newer-server-row");
        Assert.True(await _dbContext.ItemWarehouseStocks.AnyAsync(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.YeonsuMainWarehouse));
        var updated = await _dbContext.ItemWarehouseStocks.FirstAsync(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse);
        Assert.Equal(11m, updated.Quantity);
        Assert.True(updated.Revision > 200);
        var refreshedItem = await _dbContext.Items.IgnoreQueryFilters().FirstAsync(item => item.Id == itemId);
        Assert.Equal(16m, refreshedItem.CurrentStock);
        Assert.True(refreshedItem.Revision > 100);
    }

    [Fact]
    public async Task Push_RejectsCustomerCategoryDuplicateNameAfterTrim()
    {
        var existingId = Guid.NewGuid();
        _dbContext.CustomerCategories.Add(new CustomerCategory
        {
            Id = existingId,
            Name = "관공서",
            UpdatedAtUtc = new DateTime(2026, 6, 17, 0, 0, 0, DateTimeKind.Utc)
        });
        await _dbContext.SaveChangesAsync();

        var incomingId = Guid.NewGuid();
        var response = await _controller.Push(new SyncPushRequest
        {
            DeviceId = "device-duplicate-customer-category",
            CustomerCategories =
            [
                new CustomerCategoryDto
                {
                    Id = incomingId,
                    Name = " 관공서 ",
                    CreatedAtUtc = new DateTime(2026, 6, 17, 0, 1, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2026, 6, 17, 0, 1, 0, DateTimeKind.Utc)
                }
            ]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            conflict.EntityName == nameof(CustomerCategory) &&
            conflict.Reason.Contains("already exists", StringComparison.OrdinalIgnoreCase));
        Assert.False(await _dbContext.CustomerCategories.IgnoreQueryFilters().AnyAsync(category => category.Id == incomingId));
        Assert.Equal(1, await _dbContext.CustomerCategories.IgnoreQueryFilters().CountAsync(category => !category.IsDeleted));
    }

    [Fact]
    public async Task Push_RejectsSelectionOptionDeleteWhenOnlyNameMatchesDifferentActiveId()
    {
        var existingId = Guid.NewGuid();
        _dbContext.PriceGradeOptions.Add(new PriceGradeOption
        {
            Id = existingId,
            Name = "VIP",
            PriceSource = "Sales",
            SortOrder = 10,
            IsActive = true,
            IsDeleted = false,
            UpdatedAtUtc = new DateTime(2026, 6, 23, 0, 0, 0, DateTimeKind.Utc)
        });
        await _dbContext.SaveChangesAsync();

        var incomingId = Guid.NewGuid();
        var response = await _controller.Push(new SyncPushRequest
        {
            DeviceId = "device-selection-option-wrong-delete-id",
            PriceGradeOptions =
            [
                new PriceGradeOptionDto
                {
                    Id = incomingId,
                    Name = " VIP ",
                    PriceSource = "Sales",
                    SortOrder = 10,
                    IsActive = false,
                    IsDeleted = true,
                    CreatedAtUtc = new DateTime(2026, 6, 23, 0, 1, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2026, 6, 23, 0, 1, 0, DateTimeKind.Utc)
                }
            ]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            conflict.EntityName == nameof(PriceGradeOption) &&
            conflict.Reason.Contains("does not exist", StringComparison.OrdinalIgnoreCase));
        Assert.False(await _dbContext.PriceGradeOptions.IgnoreQueryFilters().AnyAsync(option => option.Id == incomingId));

        var stored = await _dbContext.PriceGradeOptions.IgnoreQueryFilters().SingleAsync(option => option.Id == existingId);
        Assert.False(stored.IsDeleted);
        Assert.True(stored.IsActive);
    }

    [Fact]
    public async Task Push_RejectsSelectionOptionRecreateWhenDeletedSameNameHasDifferentId()
    {
        var deletedId = Guid.NewGuid();
        _dbContext.PriceGradeOptions.Add(new PriceGradeOption
        {
            Id = deletedId,
            Name = "VIP",
            PriceSource = "A",
            SortOrder = 10,
            IsActive = false,
            IsDeleted = true,
            UpdatedAtUtc = new DateTime(2026, 6, 23, 0, 0, 0, DateTimeKind.Utc)
        });
        await _dbContext.SaveChangesAsync();

        var incomingId = Guid.NewGuid();
        var response = await _controller.Push(new SyncPushRequest
        {
            DeviceId = "device-selection-option-recreate-deleted-name",
            PriceGradeOptions =
            [
                new PriceGradeOptionDto
                {
                    Id = incomingId,
                    Name = " VIP ",
                    PriceSource = "Sales",
                    SortOrder = 20,
                    IsActive = true,
                    IsDeleted = false,
                    CreatedAtUtc = new DateTime(2026, 6, 23, 0, 1, 0, DateTimeKind.Utc),
                    UpdatedAtUtc = new DateTime(2026, 6, 23, 0, 1, 0, DateTimeKind.Utc)
                }
            ]
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var result = Assert.IsType<SyncPushResult>(ok.Value);

        Assert.Equal(1, result.ConflictCount);
        Assert.Contains(result.Conflicts, conflict =>
            conflict.EntityName == nameof(PriceGradeOption) &&
            conflict.Reason.Contains("Restore", StringComparison.OrdinalIgnoreCase));
        Assert.False(await _dbContext.PriceGradeOptions.IgnoreQueryFilters().AnyAsync(option => option.Id == incomingId));

        var stored = await _dbContext.PriceGradeOptions.IgnoreQueryFilters().SingleAsync(option => option.Id == deletedId);
        Assert.True(stored.IsDeleted);
        Assert.False(stored.IsActive);
        Assert.Equal("A", stored.PriceSource);
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

    private static SyncController CreateController(
        AppDbContext dbContext,
        TestCurrentUserContext currentUser,
        ICentralFileStorage? fileStorage = null)
    {
        var revisionClock = new RevisionClock();
        var officeScopeService = new OfficeScopeService(currentUser, dbContext);
        return new SyncController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            officeScopeService,
            fileStorage ?? new StubCentralFileStorage(),
            revisionClock,
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, revisionClock),
            new RentalAssignmentHistoryService(dbContext),
            new RentalSettlementRecalculationService(dbContext));
    }

    private static string ComputeTestSha256Hex(byte[] content)
        => Convert.ToHexString(SHA256.HashData(content));

    private static byte[] TestPdfBytes()
        => "%PDF-1.4\n1 0 obj\n<<>>\nendobj\n%%EOF\n"u8.ToArray();

    private sealed class SyncRentalBillingRunSnapshot
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

    private sealed class TestCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId { get; init; } = Guid.NewGuid();
        public string Username { get; init; } = string.Empty;
        public string TenantCode { get; init; } = TenantScopeCatalog.UsenetGroup;
        public string OfficeCode { get; init; } = OfficeCodeCatalog.Usenet;
        public string ScopeType { get; init; } = TenantScopeCatalog.ScopeOfficeOnly;
        public bool IsAdmin { get; init; }
        private static readonly IReadOnlyCollection<string> DefaultPermissions =
        [
            PermissionNames.CompanyProfileEdit,
            PermissionNames.SettingsEdit,
            PermissionNames.CustomerEdit,
            PermissionNames.ItemEdit,
            PermissionNames.InvoiceEdit,
            PermissionNames.PaymentEdit,
            PermissionNames.DeliveryEdit,
            PermissionNames.RentalSettingsEdit,
            PermissionNames.RentalProfileEdit,
            PermissionNames.RentalAssetEdit
        ];

        public bool IsGodMode { get; init; }
        public IReadOnlyCollection<string> Permissions { get; init; } = DefaultPermissions;

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

    private sealed class ThrowingCentralFileStorage : ICentralFileStorage
    {
        public string RootPath => Path.GetTempPath();

        public Task<string> SaveBytesAsync(string area, string ownerId, Guid fileId, string fileName, byte[] content, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Simulated storage failure");

        public byte[] ReadBytes(string? storedPath, byte[]? fallback = null)
            => fallback ?? [];

        public void DeleteIfExists(string? storedPath)
        {
        }
    }
}
