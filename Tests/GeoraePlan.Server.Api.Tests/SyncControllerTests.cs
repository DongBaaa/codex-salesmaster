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

        public bool HasPermission(string permission) => IsAdmin || IsGodMode;
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
