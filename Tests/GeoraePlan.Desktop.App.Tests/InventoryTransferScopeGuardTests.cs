using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class InventoryTransferScopeGuardTests
{
    [Fact]
    public async Task SaveInventoryTransfer_DeniesTargetOfficeUserFromCreatingSourceStockMove()
    {
        using var appRoot = new LocalAppRootScope("georaeplan-transfer-save-source-scope");
        await using var db = CreateDbContext(appRoot.DbPath);
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var itemId = Guid.Parse("a1111111-1111-1111-1111-111111111111");
        db.Items.Add(CreateStockItem(itemId, "Target denied transfer item"));
        db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock
        {
            ItemId = itemId,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 10m,
            UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            Revision = 10
        });
        await db.SaveChangesAsync();

        var targetSession = CreateUserSession(
            TenantScopeCatalog.UsenetGroup,
            OfficeCodeCatalog.Yeonsu,
            TenantScopeCatalog.ScopeOfficeOnly,
            AppPermissionNames.DeliveryEdit);
        var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), targetSession);
        var transferId = Guid.Parse("a2222222-2222-2222-2222-222222222222");

        var result = await service.SaveInventoryTransferAsync(new LocalInventoryTransfer
        {
            Id = transferId,
            TransferDate = new DateOnly(2026, 6, 24),
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            Lines =
            [
                new LocalInventoryTransferLine
                {
                    Id = Guid.Parse("a3333333-3333-3333-3333-333333333333"),
                    TransferId = transferId,
                    ItemId = itemId,
                    ItemNameOriginal = "Target denied transfer item",
                    Unit = "EA",
                    Quantity = 2m
                }
            ]
        }, targetSession);

        Assert.False(result.Success);
        Assert.True(result.PermissionDenied);
        Assert.Contains("출발지", result.Message, StringComparison.Ordinal);
        db.ChangeTracker.Clear();
        Assert.False(await db.InventoryTransfers.IgnoreQueryFilters().AnyAsync(transfer => transfer.Id == transferId));
        Assert.Equal(10m, await db.ItemWarehouseStocks
            .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
        Assert.False(await db.ItemWarehouseStocks
            .AnyAsync(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.YeonsuMainWarehouse));
    }

    [Fact]
    public async Task DeleteInventoryTransfer_DeniesTargetOfficeUserFromDeletingPendingSourceMove()
    {
        using var appRoot = new LocalAppRootScope("georaeplan-transfer-delete-source-scope");
        await using var db = CreateDbContext(appRoot.DbPath);
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var itemId = Guid.Parse("b1111111-1111-1111-1111-111111111111");
        var transferId = Guid.Parse("b2222222-2222-2222-2222-222222222222");
        var lineId = Guid.Parse("b3333333-3333-3333-3333-333333333333");
        var now = new DateTime(2026, 6, 24, 1, 30, 0, DateTimeKind.Utc);
        db.Items.Add(CreateStockItem(itemId, "Target denied pending delete item"));
        db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock
        {
            ItemId = itemId,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 8m,
            UpdatedAtUtc = now,
            Revision = 20
        });
        db.InventoryTransfers.Add(new LocalInventoryTransfer
        {
            Id = transferId,
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            TransferNumber = "TR-PENDING-TARGET-DELETE-DENIED",
            TransferDate = new DateOnly(2026, 6, 24),
            TransferStatus = InventoryTransferStatusNormalizer.Pending,
            CreatedAtUtc = now.AddHours(-1),
            UpdatedAtUtc = now,
            Revision = 30,
            IsDirty = false,
            Lines =
            [
                new LocalInventoryTransferLine
                {
                    Id = lineId,
                    TransferId = transferId,
                    ItemId = itemId,
                    ItemNameOriginal = "Target denied pending delete item",
                    Unit = "EA",
                    Quantity = 2m
                }
            ]
        });
        await db.SaveChangesAsync();

        var targetSession = CreateUserSession(
            TenantScopeCatalog.UsenetGroup,
            OfficeCodeCatalog.Yeonsu,
            TenantScopeCatalog.ScopeOfficeOnly,
            AppPermissionNames.DeliveryEdit);
        var service = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), targetSession);

        var result = await service.DeleteInventoryTransferAsync(transferId, targetSession, expectedRevision: 30);

        Assert.False(result.Success);
        Assert.True(result.PermissionDenied);
        Assert.Contains("출발지", result.Message, StringComparison.Ordinal);
        db.ChangeTracker.Clear();
        var stored = await db.InventoryTransfers.IgnoreQueryFilters().SingleAsync(transfer => transfer.Id == transferId);
        Assert.False(stored.IsDeleted);
        Assert.False(stored.IsDirty);
        Assert.Equal(8m, await db.ItemWarehouseStocks
            .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync());
    }

    [Fact]
    public void InventoryTransferViewModel_CanDeleteTransfer_RequiresSourceOfficeForPendingStatus()
    {
        using var appRoot = new LocalAppRootScope("georaeplan-transfer-delete-ui-source-scope");
        using var db = CreateDbContext(appRoot.DbPath);
        db.Database.EnsureDeleted();
        db.Database.EnsureCreated();

        var targetSession = CreateUserSession(
            TenantScopeCatalog.UsenetGroup,
            OfficeCodeCatalog.Yeonsu,
            TenantScopeCatalog.ScopeOfficeOnly,
            AppPermissionNames.DeliveryEdit);
        var viewModel = new InventoryTransferViewModel(
            new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), targetSession),
            targetSession)
        {
            TransferId = Guid.Parse("c2222222-2222-2222-2222-222222222222"),
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            TransferStatus = InventoryTransferStatusNormalizer.Pending
        };

        Assert.False(viewModel.CanDeleteTransfer);
    }

    private static LocalItem CreateStockItem(Guid itemId, string name) => new()
    {
        Id = itemId,
        TenantCode = TenantScopeCatalog.UsenetGroup,
        OfficeCode = OfficeCodeCatalog.Shared,
        NameOriginal = name,
        NameMatchKey = name.Replace(" ", string.Empty).ToUpperInvariant(),
        Unit = "EA",
        ItemKind = ItemKinds.Product,
        TrackingType = ItemTrackingTypes.Stock,
        CurrentStock = 10m,
        IsDirty = false
    };

    private static SessionState CreateUserSession(
        string tenantCode,
        string officeCode,
        string scopeType,
        params string[] permissions)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            Username = $"{officeCode.ToLowerInvariant()}-delivery-user",
            Role = DomainConstants.RoleUser,
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ScopeType = scopeType,
            Permissions = permissions.ToList()
        });
        return session;
    }

    private static LocalDbContext CreateDbContext(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        return new LocalDbContext(options);
    }

    private sealed class LocalAppRootScope : IDisposable
    {
        private readonly string? _previousAppRoot;
        private readonly string _appRoot;
        public string DbPath { get; }

        public LocalAppRootScope(string prefix)
        {
            _previousAppRoot = Environment.GetEnvironmentVariable("GEORAEPLAN_APP_ROOT");
            _appRoot = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_appRoot);
            DbPath = Path.Combine(_appRoot, "거래플랜-test.db");
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", _appRoot);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", _previousAppRoot);
            SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(_appRoot))
                    Directory.Delete(_appRoot, recursive: true);
            }
            catch
            {
                // Test temp cleanup failures must not hide assertion failures.
            }
        }
    }
}
