using System.Net;
using System.Net.Http;
using System.IO;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class InvoiceStockRollbackRetryTests
{
    [Theory]
    [InlineData("equivalent")]
    [InlineData("server-edit")]
    [InlineData("local-edit")]
    [InlineData("accepted-count")]
    [InlineData("accepted-revision")]
    [InlineData("purge-record")]
    [InlineData("wrong-conflict-id")]
    [InlineData("retry-write-failure")]
    public async Task WholePushRollback_PreservesPendingData_AndRetriesOnlyEquivalentRevision(string mode)
    {
        var previousRoot = Environment.GetEnvironmentVariable("GEORAEPLAN_APP_ROOT");
        var root = Path.Combine(Path.GetTempPath(), "invoice-rollback-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", root);
        try
        {
            var session = new SessionState();
            session.SetSession("test-token", new UserSessionDto
            {
                UserId = Guid.NewGuid(), Username = "rollback-admin", Role = "Admin",
                TenantCode = "USENET_GROUP", OfficeCode = "USENET", ScopeType = TenantScopeCatalog.ScopeAdmin
            }, DateTime.UtcNow.AddDays(1));
            var invoiceId = Guid.NewGuid();
            var itemId = Guid.NewGuid();
            var unitId = Guid.NewGuid();
            var connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "test.db") }.ToString();
            var options = new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connectionString).Options;
            var handler = new RollbackHandler(mode, invoiceId, options);
            var services = new ServiceCollection();
            services.AddDbContext<LocalDbContext>(builder => builder.UseSqlite(connectionString));
            services.AddSingleton(session);
            services.AddSingleton<OfficeAccessService>();
            services.AddSingleton<SyncRequestDispatcher>();
            services.AddSingleton<DesktopDataChangeNotifier>();
            services.AddScoped<SyncDiagnosticsService>();
            services.AddScoped<LocalStateService>();
            services.AddScoped<RentalStateService>();
            services.AddScoped(_ => new HttpClient(handler, false) { BaseAddress = new Uri("http://localhost") });
            services.AddScoped<ErpApiClient>();
            services.AddScoped<SyncService>();
            await using var provider = services.BuildServiceProvider();
            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();
                Assert.Equal(Path.Combine(root, "test.db"), db.Database.GetDbConnection().DataSource);
                await db.Database.EnsureCreatedAsync();
                var customerId = Guid.NewGuid();
                var now = DateTime.UtcNow.AddMinutes(-5);
                db.Customers.Add(new LocalCustomer { Id = customerId, NameOriginal = "rollback customer", TenantCode = "USENET_GROUP", OfficeCode = "USENET", ResponsibleOfficeCode = "USENET", Revision = 1, IsDirty = false });
                db.Items.Add(new LocalItem { Id = itemId, NameOriginal = "rollback item", NameMatchKey = "rollback item", TenantCode = "USENET_GROUP", OfficeCode = "SHARED", ItemKind = ItemKinds.Product, TrackingType = ItemTrackingTypes.Stock, CurrentStock = 10, Revision = 11, IsDirty = true, CreatedAtUtc = now, UpdatedAtUtc = now });
                db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock { ItemId = itemId, WarehouseCode = "USENET_MAIN", Quantity = 10, Revision = 31, UpdatedAtUtc = now });
                db.Units.Add(new LocalUnit { Id = unitId, Name = "rollback unrelated", Revision = 2, IsDirty = true, CreatedAtUtc = now, UpdatedAtUtc = now });
                db.Invoices.Add(new LocalInvoice
                {
                    Id = invoiceId, CustomerId = customerId, TenantCode = "USENET_GROUP", OfficeCode = "USENET", ResponsibleOfficeCode = "USENET", SourceWarehouseCode = "USENET_MAIN",
                    VersionGroupId = invoiceId, VersionNumber = 1, IsLatestVersion = true, VoucherType = VoucherType.Purchase, InvoiceDate = new DateOnly(2026, 9, 12),
                    Memo = "original invoice", Revision = 7, IsDirty = true, CreatedAtUtc = now, UpdatedAtUtc = now,
                    Lines = [new LocalInvoiceLine { Id = Guid.NewGuid(), InvoiceId = invoiceId, ItemId = itemId, ItemNameOriginal = "rollback item", Quantity = 1, UnitPrice = 10, LineAmount = 10 }]
                });
                db.Settings.Add(new LocalSetting { Key = "LastSyncRevision", Value = "1" });
                await db.SaveChangesAsync();
            }
            await using var runtime = provider.CreateAsyncScope();
            var sync = runtime.ServiceProvider.GetRequiredService<SyncService>();
            Assert.False(await sync.TrySyncAsync().WaitAsync(TimeSpan.FromSeconds(20)));
            Assert.Single(handler.Pushes);
            Assert.Equal(0, handler.PullCount);
            await using (var db = new LocalDbContext(options))
            {
                var invoice = await db.Invoices.IgnoreQueryFilters().SingleAsync(x => x.Id == invoiceId);
                Assert.True(invoice.IsDirty);
                Assert.Equal(mode == "equivalent" ? 8 : 7, invoice.Revision);
                Assert.Equal(mode == "local-edit" ? "new local edit" : "original invoice", invoice.Memo);
                Assert.True((await db.Items.SingleAsync(x => x.Id == itemId)).IsDirty);
                Assert.True((await db.Units.SingleAsync(x => x.Id == unitId)).IsDirty);
                Assert.Equal(10, (await db.ItemWarehouseStocks.SingleAsync()).Quantity);
                Assert.Equal(31, (await db.ItemWarehouseStocks.SingleAsync()).Revision);
                Assert.DoesNotContain(await db.SyncOutboxEntries.ToListAsync(), x => x.Status == "Acknowledged");
            }
            if (mode != "equivalent") return;
            var retrySucceeded = await sync.TrySyncAsync().WaitAsync(TimeSpan.FromSeconds(20));
            await using var diagnosticDb = new LocalDbContext(options);
            var retryError = await diagnosticDb.Settings.Where(x => x.Key == "Sync.LastError").Select(x => x.Value).FirstOrDefaultAsync();
            Assert.True(retrySucceeded, retryError);
            Assert.Equal(2, handler.Pushes.Count);
            Assert.True(handler.PullCount > 0);
            var retry = handler.Pushes[1];
            Assert.Equal(8, Assert.Single(retry.Invoices).ExpectedRevision);
            Assert.NotEqual(Assert.Single(handler.Pushes[0].Invoices).MutationId, Assert.Single(retry.Invoices).MutationId);
            Assert.Contains(retry.Units, x => x.Id == unitId);
            Assert.Contains(retry.ItemWarehouseStocks, x => x.ItemId == itemId && x.Quantity == 10);
            await using var finalDb = new LocalDbContext(options);
            Assert.False((await finalDb.Invoices.SingleAsync(x => x.Id == invoiceId)).IsDirty);
            Assert.False((await finalDb.Items.SingleAsync(x => x.Id == itemId)).IsDirty);
            Assert.False((await finalDb.Units.SingleAsync(x => x.Id == unitId)).IsDirty);
            Assert.Equal(10, (await finalDb.ItemWarehouseStocks.SingleAsync()).Quantity);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", previousRoot);
            SqliteConnection.ClearAllPools();
        }
    }

    private sealed class RollbackHandler(string mode, Guid invoiceId, DbContextOptions<LocalDbContext> options) : HttpMessageHandler
    {
        public List<SyncPushRequest> Pushes { get; } = [];
        public int PullCount { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/sync/pull")
            {
                PullCount++;
                Assert.Equal(2, Pushes.Count);
                var stocks = JsonSerializer.Deserialize<List<ItemWarehouseStockDto>>(
                    JsonSerializer.Serialize(Pushes[1].ItemWarehouseStocks))!;
                foreach (var stock in stocks) stock.Revision = 100;
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new SyncPullResponse { CurrentServerRevision = 100, ItemWarehouseStocks = stocks }) };
            }
            if (request.RequestUri.AbsolutePath != "/sync/push") return new(HttpStatusCode.NotFound);
            var push = (await request.Content!.ReadFromJsonAsync<SyncPushRequest>(cancellationToken: ct))!;
            Pushes.Add(push);
            var sentInvoice = Assert.Single(push.Invoices);
            Assert.NotEmpty(push.ItemWarehouseStocks);
            if (Pushes.Count == 1)
            {
                var server = JsonSerializer.Deserialize<InvoiceDto>(JsonSerializer.Serialize(sentInvoice))!;
                server.Revision = 8;
                if (mode == "server-edit") server.Memo = "another PC edit";
                if (mode == "local-edit")
                {
                    await using var db = new LocalDbContext(options);
                    var local = await db.Invoices.SingleAsync(x => x.Id == invoiceId, ct);
                    local.Memo = "new local edit"; local.UpdatedAtUtc = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                }
                if (mode == "retry-write-failure")
                {
                    await using var db = new LocalDbContext(options);
                    await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_revision_retry BEFORE UPDATE OF Revision ON Invoices WHEN NEW.Revision <> OLD.Revision BEGIN SELECT RAISE(ABORT, 'injected retry storage failure'); END", ct);
                }
                var result = new SyncPushResult
                {
                    ConflictCount = 1, CurrentServerRevision = 90,
                    Conflicts = [new ConflictLogDto { EntityName = "Invoice", EntityId = (mode == "wrong-conflict-id" ? Guid.NewGuid() : invoiceId).ToString(), Reason = "Expected revision mismatch.", ClientJson = JsonSerializer.Serialize(sentInvoice), ServerJson = JsonSerializer.Serialize(server) }],
                    Notices = [new SyncNoticeDto { EntityName = "Invoice", Code = "invoice-stock-atomicity-rollback", Message = "Whole push rolled back." }]
                };
                if (mode == "accepted-count") result.AcceptedCount = 1;
                if (mode == "accepted-revision") result.AcceptedRevisions.Add(new SyncAcceptedRevisionDto { EntityName = "Invoice", EntityId = invoiceId, Revision = 99 });
                if (mode == "purge-record") result.PurgeRecords.Add(new RecycleBinPurgeRecordDto());
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(result) };
            }
            var accepted = new SyncPushResult { CurrentServerRevision = 100 };
            accepted.AcceptedRevisions.AddRange(push.Invoices.Select(x => new SyncAcceptedRevisionDto { EntityName = "Invoice", EntityId = x.Id, Revision = 100, UpdatedAtUtc = x.UpdatedAtUtc }));
            accepted.AcceptedRevisions.AddRange(push.Items.Select(x => new SyncAcceptedRevisionDto { EntityName = "Item", EntityId = x.Id, Revision = 100, UpdatedAtUtc = x.UpdatedAtUtc }));
            accepted.AcceptedRevisions.AddRange(push.Units.Select(x => new SyncAcceptedRevisionDto { EntityName = "Unit", EntityId = x.Id, Revision = 100, UpdatedAtUtc = x.UpdatedAtUtc }));
            accepted.AcceptedCount = accepted.AcceptedRevisions.Count;
            accepted.AcceptedItemWarehouseStockKeys.AddRange(push.ItemWarehouseStocks.Select(x => new SyncAcceptedItemWarehouseStockKeyDto { ItemId = x.ItemId, WarehouseCode = x.WarehouseCode }));
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(accepted) };
        }
    }
}
