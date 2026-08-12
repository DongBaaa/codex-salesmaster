using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class InventoryTransferLocalItemReferenceTests
{
    public static TheoryData<string> InvalidItemReferenceCases => new()
    {
        "empty",
        "missing",
        "deleted",
        "foreign-tenant",
        "outside-readable-office"
    };

    [Theory]
    [MemberData(nameof(InvalidItemReferenceCases))]
    public async Task SaveInventoryTransferAsync_RejectsInvalidItemReferenceBeforeMutation(string scenario)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDbContext(connection);
        await db.Database.EnsureCreatedAsync();

        var itemId = scenario == "empty"
            ? Guid.Empty
            : Guid.Parse("d1111111-1111-1111-1111-111111111111");
        if (scenario is not ("empty" or "missing"))
        {
            var item = CreateItem(itemId, "Reference guard item");
            if (scenario == "deleted")
                item.IsDeleted = true;
            if (scenario == "foreign-tenant")
                item.TenantCode = TenantScopeCatalog.Itworld;
            if (scenario == "outside-readable-office")
                item.OfficeCode = OfficeCodeCatalog.Yeonsu;

            db.Items.Add(item);
            db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock
            {
                ItemId = itemId,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 10m,
                UpdatedAtUtc = DateTime.UtcNow,
                Revision = 1
            });
            await db.SaveChangesAsync();
        }

        var session = CreateUsenetSourceSession();
        var service = new LocalStateService(
            db,
            new OfficeAccessService(),
            new SyncRequestDispatcher(),
            session);
        var transferId = Guid.Parse("d1222222-2222-2222-2222-222222222222");

        var result = await service.SaveInventoryTransferAsync(
            CreateTransfer(transferId, itemId),
            session);

        Assert.False(result.Success);
        db.ChangeTracker.Clear();
        Assert.False(await db.InventoryTransfers.IgnoreQueryFilters().AnyAsync());
        Assert.False(await db.InventoryTransferLines.IgnoreQueryFilters().AnyAsync());
        Assert.False(await db.AuditLogs.AnyAsync(log => log.EntityId == transferId.ToString("D")));
    }

    [Fact]
    public async Task SaveInventoryTransferAsync_AllowsReadableSharedItemInSourceTenant()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDbContext(connection);
        await db.Database.EnsureCreatedAsync();

        var itemId = Guid.Parse("d2111111-1111-1111-1111-111111111111");
        db.Items.Add(CreateItem(itemId, "Shared transfer item"));
        db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock
        {
            ItemId = itemId,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 10m,
            UpdatedAtUtc = DateTime.UtcNow,
            Revision = 1
        });
        await db.SaveChangesAsync();

        var session = CreateUsenetSourceSession();
        var service = new LocalStateService(
            db,
            new OfficeAccessService(),
            new SyncRequestDispatcher(),
            session);
        var transferId = Guid.Parse("d2222222-2222-2222-2222-222222222222");

        var result = await service.SaveInventoryTransferAsync(
            CreateTransfer(transferId, itemId),
            session);

        Assert.True(result.Success, result.Message);
        db.ChangeTracker.Clear();
        var stored = await db.InventoryTransfers
            .IgnoreQueryFilters()
            .Include(transfer => transfer.Lines)
            .SingleAsync(transfer => transfer.Id == transferId);
        var line = Assert.Single(stored.Lines);
        Assert.Equal(itemId, line.ItemId);
        Assert.Equal(1m, line.Quantity);
    }

    private static LocalInventoryTransfer CreateTransfer(Guid transferId, Guid itemId) => new()
    {
        Id = transferId,
        TransferDate = new DateOnly(2026, 8, 2),
        FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
        ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
        Lines =
        [
            new LocalInventoryTransferLine
            {
                Id = Guid.Parse("d1333333-3333-3333-3333-333333333333"),
                TransferId = transferId,
                ItemId = itemId,
                ItemNameOriginal = "Programmatic item line",
                Unit = "EA",
                Quantity = 1m
            }
        ]
    };

    private static LocalItem CreateItem(Guid itemId, string name) => new()
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

    private static SessionState CreateUsenetSourceSession()
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            Username = "usenet-transfer-user",
            Role = DomainConstants.RoleUser,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = [AppPermissionNames.DeliveryEdit]
        });
        return session;
    }

    private static LocalDbContext CreateDbContext(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection)
            .Options;
        return new LocalDbContext(options);
    }
}
