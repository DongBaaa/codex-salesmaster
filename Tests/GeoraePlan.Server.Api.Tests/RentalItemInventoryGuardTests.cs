using System.Reflection;
using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class RentalItemInventoryGuardTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly RevisionClock _revisionClock = new();

    public RentalItemInventoryGuardTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
    }

    [Fact]
    public async Task ItemsController_Create_NormalizesRentalItemAsAssetAndClearsInventoryQuantities()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);
        dbContext.Database.EnsureCreated();

        var controller = new ItemsController(dbContext, new OfficeScopeService(currentUser, dbContext));
        var response = await controller.Create(new ItemDto
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Rental asset item",
            NameMatchKey = "RENTALASSETITEM",
            ItemKind = ItemKinds.Product,
            TrackingType = ItemTrackingTypes.Stock,
            CategoryName = "복합기",
            Unit = "대",
            CurrentStock = 7m,
            SafetyStock = 2m,
            IsRental = true,
            IsSale = true
        }, CancellationToken.None);

        var item = AssertOk<ItemDto>(response);

        Assert.Equal(ItemKinds.Asset, item.ItemKind);
        Assert.Equal(ItemTrackingTypes.Asset, item.TrackingType);
        Assert.True(item.IsRental);
        Assert.False(item.IsSale);
        Assert.Equal(0m, item.CurrentStock);
        Assert.Equal(0m, item.SafetyStock);

        var stored = await dbContext.Items.IgnoreQueryFilters().SingleAsync(row => row.Id == item.Id);
        Assert.Equal(ItemKinds.Asset, stored.ItemKind);
        Assert.Equal(ItemTrackingTypes.Asset, stored.TrackingType);
        Assert.True(stored.IsRental);
        Assert.False(stored.IsSale);
        Assert.Equal(0m, stored.CurrentStock);
        Assert.Equal(0m, stored.SafetyStock);
    }

    [Fact]
    public async Task ItemsController_UpdateToRentalAsset_RemovesExistingWarehouseStocks()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);
        dbContext.Database.EnsureCreated();

        var item = new Item
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Convertible stock item",
            NameMatchKey = "CONVERTIBLESTOCKITEM",
            ItemKind = ItemKinds.Product,
            TrackingType = ItemTrackingTypes.Stock,
            Unit = "EA",
            CurrentStock = 5m,
            SafetyStock = 1m,
            IsSale = true
        };
        dbContext.Items.Add(item);
        dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
        {
            ItemId = item.Id,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 5m,
            Revision = 1
        });
        await dbContext.SaveChangesAsync();

        var stored = await dbContext.Items.IgnoreQueryFilters().SingleAsync(row => row.Id == item.Id);
        var dto = stored.ToDto();
        dto.ItemKind = ItemKinds.Product;
        dto.TrackingType = ItemTrackingTypes.Stock;
        dto.IsRental = true;
        dto.IsSale = true;
        dto.CurrentStock = 12m;
        dto.SafetyStock = 3m;
        dto.ExpectedRevision = stored.Revision;

        var controller = new ItemsController(dbContext, new OfficeScopeService(currentUser, dbContext));
        var response = await controller.Update(item.Id, dto, CancellationToken.None);

        var updated = AssertOk<ItemDto>(response);
        Assert.Equal(ItemTrackingTypes.Asset, updated.TrackingType);
        Assert.Equal(ItemKinds.Asset, updated.ItemKind);
        Assert.Equal(0m, updated.CurrentStock);
        Assert.Equal(0m, updated.SafetyStock);
        Assert.Empty(await dbContext.ItemWarehouseStocks.Where(stock => stock.ItemId == item.Id).ToListAsync());
    }

    [Fact]
    public async Task SyncPush_IgnoresWarehouseStockForRentalAssetAndRemovesStaleRows()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);
        dbContext.Database.EnsureCreated();

        var itemId = Guid.NewGuid();
        dbContext.Items.Add(new Item
        {
            Id = itemId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Synced rental asset item",
            NameMatchKey = "SYNCEDRENTALASSETITEM",
            ItemKind = ItemKinds.Asset,
            TrackingType = ItemTrackingTypes.Asset,
            IsRental = true,
            CurrentStock = 4m,
            SafetyStock = 1m
        });
        dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
        {
            ItemId = itemId,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 4m,
            Revision = 1
        });
        await dbContext.SaveChangesAsync();

        var controller = CreateSyncController(dbContext, currentUser);
        var response = await controller.Push(new SyncPushRequest
        {
            DeviceId = "rental-item-inventory-guard",
            ItemWarehouseStocks =
            [
                new ItemWarehouseStockDto
                {
                    ItemId = itemId,
                    WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                    Quantity = 9m,
                    Revision = 1,
                    ExpectedRevision = 1
                }
            ]
        }, CancellationToken.None);

        var result = AssertOk<SyncPushResult>(response);
        Assert.Contains(result.Notices, notice => string.Equals(
            notice.Code,
            "item-warehouse-stock-skip-non-inventory-item",
            StringComparison.Ordinal));
        Assert.Empty(await dbContext.ItemWarehouseStocks.Where(stock => stock.ItemId == itemId).ToListAsync());
        var stored = await dbContext.Items.IgnoreQueryFilters().AsNoTracking().SingleAsync(row => row.Id == itemId);
        Assert.Equal(0m, stored.CurrentStock);
        Assert.Equal(0m, stored.SafetyStock);
    }

    [Fact]
    public async Task DbInitializer_PurgesWarehouseStockAndZeroesCurrentStockForRentalAssetItems()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);
        dbContext.Database.EnsureCreated();

        var itemId = Guid.NewGuid();
        dbContext.Items.Add(new Item
        {
            Id = itemId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Startup rental asset item",
            NameMatchKey = "STARTUPRENTALASSETITEM",
            ItemKind = ItemKinds.Asset,
            TrackingType = ItemTrackingTypes.Asset,
            IsRental = true,
            CurrentStock = 6m,
            SafetyStock = 2m
        });
        dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
        {
            ItemId = itemId,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 6m,
            Revision = 1
        });
        await dbContext.SaveChangesAsync();

        await InvokePrivateStaticTask(
            typeof(DbInitializer),
            "PurgeDeletedItemWarehouseStocksAsync",
            dbContext,
            CancellationToken.None);
        var repaired = await InvokePrivateStaticTask<int>(
            typeof(DbInitializer),
            "RepairItemCurrentStockSnapshotsAsync",
            dbContext,
            CancellationToken.None);
        await dbContext.SaveChangesAsync();

        Assert.Equal(1, repaired);
        Assert.Empty(await dbContext.ItemWarehouseStocks.Where(stock => stock.ItemId == itemId).ToListAsync());
        Assert.Equal(0m, await dbContext.Items.IgnoreQueryFilters()
            .Where(row => row.Id == itemId)
            .Select(row => row.CurrentStock)
            .SingleAsync());
    }

    private AppDbContext CreateDbContext(TestCurrentUserContext currentUser)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        return new AppDbContext(options, currentUser, _revisionClock);
    }

    private SyncController CreateSyncController(AppDbContext dbContext, TestCurrentUserContext currentUser)
        => new(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new StubCentralFileStorage(),
            _revisionClock,
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, _revisionClock),
            new RentalAssignmentHistoryService(dbContext),
            new RentalSettlementRecalculationService(dbContext));

    private static T AssertOk<T>(ActionResult<T> response)
    {
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        return Assert.IsType<T>(ok.Value);
    }

    private static async Task InvokePrivateStaticTask(Type type, string methodName, params object?[] args)
    {
        var method = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        await Assert.IsAssignableFrom<Task>(method!.Invoke(null, args));
    }

    private static async Task<T> InvokePrivateStaticTask<T>(Type type, string methodName, params object?[] args)
    {
        var method = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return await Assert.IsType<Task<T>>(method!.Invoke(null, args));
    }

    private static TestCurrentUserContext CreateAdminUser() => new()
    {
        Username = "admin",
        TenantCode = TenantScopeCatalog.UsenetGroup,
        OfficeCode = OfficeCodeCatalog.Usenet,
        ScopeType = TenantScopeCatalog.ScopeAdmin,
        IsAdmin = true
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
        public Task<string> GenerateAsync(Guid customerId, DateOnly invoiceDate, CancellationToken cancellationToken = default)
            => Task.FromResult($"{invoiceDate:yyyyMM}-0001");
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
