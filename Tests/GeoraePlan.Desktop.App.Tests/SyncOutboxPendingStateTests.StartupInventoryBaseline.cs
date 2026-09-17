using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed partial class SyncOutboxPendingStateTests
{
    [Theory]
    [InlineData("online")]
    [InlineData("unavailable")]
    [InlineData("offline")]
    [InlineData("dirty")]
    public async Task StartupInventoryBaseline_RefreshesFromServerWithoutReplayingPartialHistory(string mode)
    {
        PrepareAppRoot("startup-inventory-baseline");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var item = new LocalItem { Id = Guid.NewGuid(), NameOriginal = "Startup stock", NameMatchKey = "STARTUPSTOCK",
                TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
                ItemKind = ItemKinds.Product, TrackingType = ItemTrackingTypes.Stock, CurrentStock = 9m, Revision = 50, IsDirty = mode == "dirty" };
            db.Items.Add(item);
            db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock { ItemId = item.Id,
                WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse, Quantity = 10m, Revision = 77 });
            await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            var user = new UserSessionDto { UserId = Guid.NewGuid(), Username = "startup-admin", Role = DomainConstants.RoleAdmin,
                TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet, ScopeType = TenantScopeCatalog.ScopeAdmin };
            var session = new SessionState();
            if (mode == "offline") session.SetOfflineSession(user);
            else session.SetSession("synthetic", user, DateTime.UtcNow.AddHours(1));
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var handler = new StartupInventoryPullHandler(mode == "unavailable", new SyncPullResponse { CurrentServerRevision = 100,
                Items = [new ItemDto { Id = item.Id, NameOriginal = item.NameOriginal, TenantCode = item.TenantCode, OfficeCode = item.OfficeCode,
                    ItemKind = item.ItemKind, TrackingType = item.TrackingType, CurrentStock = 10m, Revision = 100 }],
                ItemWarehouseStocks = [new ItemWarehouseStockDto { ItemId = item.Id, WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse, Quantity = 10m, Revision = 100 }] });
            using var sync = CreateSyncService(db, session, handler);
            var result = await new StartupIntegrityService(local, sync, new BackupService(), session).RunAsync().WaitAsync(TimeSpan.FromSeconds(40));
            var shouldAttempt = mode is "online" or "unavailable";
            Assert.Equal(shouldAttempt, result.RefreshAttempted);
            Assert.Equal(mode == "online", result.RefreshSucceeded);
            Assert.Equal(mode != "online", result.RequiresUserAttention);
            Assert.Equal(0, handler.PushCount);
            Assert.Equal(shouldAttempt, handler.PullCount > 0);
            var stock = await db.ItemWarehouseStocks.AsNoTracking().SingleAsync(x => x.ItemId == item.Id);
            Assert.Equal(10m, stock.Quantity);
            Assert.Equal(mode == "online" ? 100 : 77, stock.Revision);
            var master = await db.Items.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == item.Id);
            Assert.Equal(mode == "online" ? 10m : 9m, master.CurrentStock);
            Assert.Equal(mode == "dirty", master.IsDirty);
            if (shouldAttempt) Assert.True(System.IO.File.Exists(result.BackupPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private sealed class StartupInventoryPullHandler(bool unavailable, SyncPullResponse response) : HttpMessageHandler
    {
        public int PullCount { get; private set; }
        public int PushCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/sync/push")
            {
                PushCount++;
                throw new InvalidOperationException("Startup inspection must not upload reconstructed stock");
            }
            if (request.RequestUri.AbsolutePath == "/sync/pull")
            {
                PullCount++;
                return Task.FromResult(new HttpResponseMessage(unavailable ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
                    { Content = unavailable ? new StringContent("synthetic unavailable") : JsonContent.Create(response) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { status = "ok" }) });
        }
    }
}
