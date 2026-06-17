using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.Data;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.Infrastructure;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.Services;
using \uAC70\uB798\uD50C\uB79C.Shared.Contracts;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class MasterCrudScopeRegressionTests
{
    [Fact]
    public async Task CustomerMutations_RequireCustomerEditPermissionInsideOfficeScope()
    {
        PrepareIsolatedAppRoot("customer-permission");
        try
        {
            ResetLocalDatabaseFile();
            await using var db = new LocalDbContext();
            await LocalDbInitializer.InitializeAsync(db);
            var local = CreateLocalService(db, out var editorSession, out var viewerSession);

            var customerId = Guid.Parse("66a95bb3-b365-4491-b34e-fdf70b7f0001");
            var deniedCreate = await local.UpsertCustomerAsync(CreateCustomer(customerId), viewerSession);

            Assert.True(deniedCreate.PermissionDenied);
            Assert.False(await db.Customers.IgnoreQueryFilters().AnyAsync(customer => customer.Id == customerId));

            var created = await local.UpsertCustomerAsync(CreateCustomer(customerId), editorSession);
            Assert.True(created.Success);

            var deniedDelete = await local.DeleteCustomerAsync(customerId, viewerSession);
            Assert.True(deniedDelete.PermissionDenied);
            Assert.False((await db.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(customer => customer.Id == customerId)).IsDeleted);

            var deleted = await local.DeleteCustomerAsync(customerId, editorSession);
            Assert.True(deleted.Success);

            var deniedRestore = await local.RestoreCustomerAsync(customerId, viewerSession);
            Assert.True(deniedRestore.PermissionDenied);
            Assert.True((await db.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(customer => customer.Id == customerId)).IsDeleted);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            Environment.SetEnvironmentVariable("GEORAEPLAN_DISABLE_LEGACY_MERGE", null);
            Environment.SetEnvironmentVariable("GEORAEPLAN_DISABLE_SERVER_SYNC", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ItemDeleteRestore_KeepsCurrentStockAndWarehouseSnapshotConsistent()
    {
        PrepareIsolatedAppRoot("item-delete-restore-stock");
        try
        {
            ResetLocalDatabaseFile();
            await using var db = new LocalDbContext();
            await LocalDbInitializer.InitializeAsync(db);
            var local = CreateLocalService(db, out var editorSession, out var viewerSession);

            var itemId = Guid.Parse("66a95bb3-b365-4491-b34e-fdf70b7f0002");
            await local.UpsertItemAsync(CreateItem(itemId), editorSession, OfficeCodeCatalog.Usenet);
            await local.SetItemOfficeStockAsync(itemId, 5m, OfficeCodeCatalog.Usenet);

            Assert.Equal(5m, await ReadItemCurrentStockAsync(db, itemId));
            Assert.Equal(5m, await ReadWarehouseStockAsync(db, itemId));

            var deleted = await local.DeleteItemAsync(itemId, editorSession);
            Assert.True(deleted.Success);
            Assert.True((await db.Items.IgnoreQueryFilters().AsNoTracking().SingleAsync(item => item.Id == itemId)).IsDeleted);
            Assert.Equal(0m, await ReadItemCurrentStockAsync(db, itemId));
            Assert.Equal(0m, await ReadWarehouseStockAsync(db, itemId));

            var deniedRestore = await local.RestoreItemAsync(itemId, viewerSession);
            Assert.True(deniedRestore.PermissionDenied);

            var restored = await local.RestoreItemAsync(itemId, editorSession);
            Assert.True(restored.Success);
            Assert.False((await db.Items.IgnoreQueryFilters().AsNoTracking().SingleAsync(item => item.Id == itemId)).IsDeleted);
            Assert.Equal(5m, await ReadItemCurrentStockAsync(db, itemId));
            Assert.Equal(5m, await ReadWarehouseStockAsync(db, itemId));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            Environment.SetEnvironmentVariable("GEORAEPLAN_DISABLE_LEGACY_MERGE", null);
            Environment.SetEnvironmentVariable("GEORAEPLAN_DISABLE_SERVER_SYNC", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private static LocalStateService CreateLocalService(
        LocalDbContext db,
        out SessionState editorSession,
        out SessionState viewerSession)
    {
        editorSession = CreateSession("editor", AppPermissionNames.CustomerEdit, AppPermissionNames.ItemEdit);
        viewerSession = CreateSession("viewer");
        return new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), editorSession);
    }

    private static LocalCustomer CreateCustomer(Guid id) => new()
    {
        Id = id,
        TenantCode = TenantScopeCatalog.UsenetGroup,
        OfficeCode = OfficeCodeCatalog.Usenet,
        ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
        NameOriginal = "Regression Customer",
        NameMatchKey = "REGRESSIONCUSTOMER",
        TradeType = CustomerTradeTypes.Sales,
        BusinessNumber = "REG-CUST-001"
    };

    private static LocalItem CreateItem(Guid id) => new()
    {
        Id = id,
        TenantCode = TenantScopeCatalog.UsenetGroup,
        OfficeCode = OfficeCodeCatalog.Usenet,
        NameOriginal = "Regression Stock Item",
        NameMatchKey = "REGRESSIONSTOCKITEM",
        SpecificationOriginal = "BOX",
        SpecificationMatchKey = "BOX",
        ItemKind = ItemKinds.Product,
        TrackingType = ItemTrackingTypes.Stock,
        Unit = "EA",
        CurrentStock = 0m,
        IsSale = true
    };

    private static SessionState CreateSession(string username, params string[] permissions)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            UserId = Guid.NewGuid(),
            Username = username,
            Role = DomainConstants.RoleUser,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = permissions.ToList()
        });
        return session;
    }

    private static async Task<decimal> ReadItemCurrentStockAsync(LocalDbContext db, Guid itemId)
        => await db.Items.IgnoreQueryFilters().Where(item => item.Id == itemId).Select(item => item.CurrentStock).SingleAsync();

    private static async Task<decimal> ReadWarehouseStockAsync(LocalDbContext db, Guid itemId)
        => await db.ItemWarehouseStocks
            .Where(stock => stock.ItemId == itemId && stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleOrDefaultAsync();

    private static void ResetLocalDatabaseFile()
    {
        SqliteConnection.ClearAllPools();
        var dbFile = AppPaths.LocalDbFile;
        var directory = Path.GetDirectoryName(dbFile);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        if (File.Exists(dbFile))
            File.Delete(dbFile);
    }

    private static string PrepareIsolatedAppRoot(string scenario)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-master-crud-{scenario}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_DISABLE_LEGACY_MERGE", "1");
        Environment.SetEnvironmentVariable("GEORAEPLAN_DISABLE_SERVER_SYNC", "1");
        return tempRoot;
    }
}
