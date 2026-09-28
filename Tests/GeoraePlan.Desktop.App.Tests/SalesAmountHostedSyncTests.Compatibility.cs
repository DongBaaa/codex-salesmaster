using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed partial class SalesAmountHostedSyncTests
{
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public async Task LegacyProtocol3_Real426PreservesDraftUntilCurrentClient4ResumesSync(
        bool salesVisible, bool purchaseVisible, bool useTransportObserver)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "trade-current-426-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        HostedFixture? server = null;
        var passed = false;
        try
        {
            Assert.True(AppPaths.IsTestEnvironment);
            Assert.Equal(3, ClientCompatibilityHeaders.CurrentProtocolVersion);
            Assert.Equal(4, DesktopClientIdentityProvider.CurrentProtocolVersion);
            server = await HostedFixture.StartAsync(root);
            using var admin = new HttpClient { BaseAddress = server.BaseAddress };
            var adminLogin = await LoginAsync(admin, "admin", server.Password);
            admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminLogin.Token);
            new DesktopClientIdentityProvider().Apply(admin);
            var permissions = new List<string> { AppPermissionNames.InvoiceEdit };
            if (salesVisible) permissions.Add(AppPermissionNames.AmountViewSales);
            if (purchaseVisible) permissions.Add(AppPermissionNames.AmountViewPurchase);
            await PostAsync(admin, "users", new CreateUserRequest {
                Username = "current-editor", Password = server.Password, Role = DomainConstants.RoleUser,
                TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeOfficeOnly, Permissions = permissions });
            var customer = new CustomerDto { Id = Guid.NewGuid(), NameOriginal = "CURRENT-426-CUSTOMER",
                TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet };
            await PostAsync(admin, "customers", customer);
            var item = new ItemDto { Id = Guid.NewGuid(), NameOriginal = "CURRENT-426-ITEM",
                TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
                TrackingType = ItemTrackingTypes.NonStock, ItemKind = ItemKinds.Product, Unit = "EA", SalePrice = 1100m };
            await PostAsync(admin, "items", item);
            using var loginHttp = new HttpClient { BaseAddress = server.BaseAddress };
            var login = await LoginAsync(loginHttp, "current-editor", server.Password);
            var session = new SessionState(); session.SetSession(login.Token, login.User, login.ExpiresAtUtc);
            var options = new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(new SqliteConnectionStringBuilder {
                DataSource = Path.Combine(root, "desktop.db"), Pooling = false }.ToString()).Options;
            var identity = new DesktopClientIdentityProvider(typeof(DesktopClientIdentityProvider).Assembly.GetName().Version, protocolVersion: 3);
            var storeRoot = Path.Combine(root, "compatibility");
            var store = new DesktopCompatibilityEvidenceStore(storeRoot);
            var latch = new DesktopCompatibilityLatch();
            var signal = new DesktopUpgradeRequiredSignal();
            var delivered = 0; signal.UpgradeRequired += _ => delivered++;
            var observer = new DesktopUpgradeRequiredObserver(latch, store, identity, signal);
            Guid invoiceId;
            string before;
            List<Guid> pendingIds;
            await using (var db = new LocalDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                // A previously downloaded customer and an offline draft: the live
                // save path must survive a compatibility rejection on reconnect.
                db.Customers.Add(LocalMappings.ToLocal(customer));
                db.Items.Add(LocalMappings.ToLocal(item)); await db.SaveChangesAsync();
                var dispatcher = new SyncRequestDispatcher();
                var local = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
                var draft = LocalMappings.ToLocal(new InvoiceDto {
                    Id = Guid.NewGuid(), CustomerId = customer.Id, TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    TotalAmount = salesVisible ? 3300m : null, SupplyAmount = salesVisible ? 3000m : null,
                    VatAmount = salesVisible ? 300m : null, Memo = "업데이트 전 미전송 메모",
                    Lines = [new InvoiceLineDto { Id = Guid.NewGuid(), ItemId = item.Id, ItemNameOriginal = item.NameOriginal,
                        ItemTrackingType = ItemTrackingTypes.NonStock, Unit = item.Unit, Quantity = 3, Remark = "미전송 비고",
                        UnitPrice = salesVisible ? 1100m : null, LineAmount = salesVisible ? 3300m : null }] });
                var saved = await local.SaveInvoiceAsync(draft, new InvoiceSaveContext {
                    Username = login.User.Username, Role = login.User.Role, OfficeCode = OfficeCodeCatalog.Usenet }, session);
                Assert.True(saved.Success, saved.Message); invoiceId = saved.SavedInvoiceId;
                db.ChangeTracker.Clear();
                var pending = await db.Invoices.Include(x => x.Lines).SingleAsync(x => x.Id == invoiceId);
                Assert.True(pending.IsDirty); before = JsonSerializer.Serialize(LocalMappings.ToDto(pending));
                pendingIds = await db.SyncOutboxEntries.Where(x => x.EntityId == invoiceId).Select(x => x.Id).ToListAsync();
                using var capture = new CurrentIdentityCaptureHandler();
                using var pipeline = useTransportObserver
                    ? new DesktopUpgradeRequiredHandler(observer) { InnerHandler = capture }
                    : (HttpMessageHandler)capture;
                using var http = new HttpClient(pipeline) { BaseAddress = server.BaseAddress };
                var api = new ErpApiClient(http, session, local, identity, observer);
                using var sync = new SyncService(db, local, new RentalStateService(db), api, session, dispatcher,
                    new SyncDiagnosticsService(session, () => new LocalDbContext(options)),
                    compatibilityRuntime: latch, upgradeObserver: observer);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                Assert.False(await sync.TrySyncAsync(deadline.Token));
                Assert.Equal(1, capture.Rejections); Assert.Equal(1, delivered);
                Assert.False(latch.CanMutate);
                var evidence = await store.LoadAsync();
                Assert.Equal(DesktopCompatibilityEvidenceState.Valid, evidence.State);
                Assert.Equal(DesktopCompatibilityEvidenceKind.Verified426, evidence.Evidence!.Kind);
                Assert.Equal(3, evidence.Evidence.ObservedProtocolVersion);
                Assert.Equal(4, evidence.Evidence.MinimumProtocolVersion);
                var versionOnlyUpdate = identity.GetRuntimeIdentity() with { Version = "9.0.0", Build = 9000 };
                Assert.True(DesktopCompatibilityPolicy.RuntimeStrictlyAdvancedWithoutRegression(evidence.Evidence, versionOnlyUpdate));
                Assert.False(DesktopCompatibilityPolicy.RuntimeSatisfies(evidence.Evidence, versionOnlyUpdate));
                Assert.True(DesktopCompatibilityPolicy.RuntimeSatisfies(evidence.Evidence,
                    identity.GetRuntimeIdentity() with { ProtocolVersion = 4 }));
                var requests = capture.SyncRequests;
                Assert.False(await sync.TrySyncAsync(deadline.Token));
                Assert.Equal(requests, capture.SyncRequests);
                db.ChangeTracker.Clear();
                pending = await db.Invoices.Include(x => x.Lines).SingleAsync(x => x.Id == invoiceId);
                Assert.True(pending.IsDirty);
                Assert.Equal(before, JsonSerializer.Serialize(LocalMappings.ToDto(pending)));
                var outstanding = await db.SyncOutboxEntries.Where(x => x.EntityId == invoiceId).ToListAsync();
                Assert.NotEmpty(outstanding);
                Assert.All(outstanding, x => Assert.Null(x.AcknowledgedAtUtc));
            }
            await using (var reopened = new LocalDbContext(options))
            {
                var resumedSession = new SessionState(); resumedSession.SetSession(login.Token, login.User, login.ExpiresAtUtc);
                var resumedLatch = new DesktopCompatibilityLatch();
                using var http = new HttpClient { BaseAddress = server.BaseAddress };
                var api = new ErpApiClient(http, resumedSession, clientIdentityProvider: identity);
                var gate = new DesktopCompatibilityGateService(new DesktopCompatibilityEvidenceStore(storeRoot), resumedLatch, identity, api);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                Assert.True((await gate.CheckAsync(deadline.Token)).IsBlocked);
                Assert.False(resumedLatch.CanMutate);
                // A newer executable that still advertises protocol 3 must not
                // clear this persisted protocol-4 requirement on startup.
                var versionOnlyGate = new DesktopCompatibilityGateService(
                    new DesktopCompatibilityEvidenceStore(storeRoot), new DesktopCompatibilityLatch(),
                    new DesktopClientIdentityProvider(new Version(9, 0, 9000), protocolVersion: 3), api);
                Assert.True((await versionOnlyGate.CheckAsync(deadline.Token)).IsBlocked);
                var pending = await reopened.Invoices.Include(x => x.Lines).SingleAsync(x => x.Id == invoiceId);
                Assert.True(pending.IsDirty);
                Assert.Equal(before, JsonSerializer.Serialize(LocalMappings.ToDto(pending)));
                var rows = await reopened.SyncOutboxEntries.Where(x => x.EntityId == invoiceId).ToListAsync();
                Assert.NotEmpty(rows);
                Assert.All(pendingIds, id => Assert.Contains(rows, row => row.Id == id));
                Assert.All(rows, row => Assert.Null(row.AcknowledgedAtUtc));
                using (var notCreated = await admin.GetAsync($"invoices/{invoiceId}"))
                    Assert.Equal(HttpStatusCode.NotFound, notCreated.StatusCode);

                // Recreate the actual default client and gate, without rewriting
                // any request headers. It now satisfies the durable protocol bound.
                var currentIdentity = new DesktopClientIdentityProvider();
                var currentLatch = new DesktopCompatibilityLatch();
                var currentStore = new DesktopCompatibilityEvidenceStore(storeRoot);
                var currentObserver = new DesktopUpgradeRequiredObserver(currentLatch, currentStore, currentIdentity, new DesktopUpgradeRequiredSignal());
                var dispatcher = new SyncRequestDispatcher();
                var local = new LocalStateService(reopened, new OfficeAccessService(), dispatcher, resumedSession);
                using var currentHttp = new HttpClient(new DesktopUpgradeRequiredHandler(currentObserver) { InnerHandler = new HttpClientHandler() }) { BaseAddress = server.BaseAddress };
                var currentApi = new ErpApiClient(currentHttp, resumedSession, local, currentIdentity, currentObserver);
                var currentGate = new DesktopCompatibilityGateService(currentStore, currentLatch, currentIdentity, currentApi);
                Assert.False((await currentGate.CheckAsync(deadline.Token)).IsBlocked);
                Assert.True(currentLatch.CanMutate);
                Assert.Equal(DesktopCompatibilityEvidenceState.None, (await currentStore.LoadAsync()).State);
                using var resumedSync = new SyncService(reopened, local, new RentalStateService(reopened), currentApi, resumedSession, dispatcher,
                    new SyncDiagnosticsService(resumedSession, () => new LocalDbContext(options)),
                    compatibilityRuntime: currentLatch, upgradeObserver: currentObserver);
                using var syncDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                Assert.True(await resumedSync.TrySyncAsync(syncDeadline.Token), await LastSyncErrorAsync(reopened));
                reopened.ChangeTracker.Clear();
                var acknowledged = await reopened.Invoices.Include(x => x.Lines).SingleAsync(x => x.Id == invoiceId);
                Assert.False(acknowledged.IsDirty);
                Assert.Equal(!salesVisible, acknowledged.AmountsHidden);
                Assert.Equal("업데이트 전 미전송 메모", acknowledged.Memo);
                Assert.Equal("미전송 비고", Assert.Single(acknowledged.Lines).Remark);
                var receipt = await reopened.SyncOutboxEntries.Where(x => x.EntityId == invoiceId).ToListAsync();
                Assert.NotEmpty(receipt); Assert.All(receipt, row => Assert.NotNull(row.AcknowledgedAtUtc));
                var serverInvoice = (await admin.GetFromJsonAsync<InvoiceDto>($"invoices/{invoiceId}"))!;
                Assert.Equal(3300m, serverInvoice.TotalAmount);
                Assert.Equal(1100m, Assert.Single(serverInvoice.Lines).UnitPrice);
            }
            await using var serverDb = new SqliteConnection(new SqliteConnectionStringBuilder {
                DataSource = Path.Combine(root, "server", "거래플랜-local.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            await serverDb.OpenAsync();
            await using var count = serverDb.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM Invoices WHERE lower(Id)=lower($id)";
            count.Parameters.AddWithValue("$id", invoiceId.ToString());
            Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
            passed = true;
        }
        finally
        {
            if (server is not null) await server.DisposeAsync();
            // Preserve failed fixture evidence; only our successfully verified,
            // randomly named root can be removed.
            if (passed)
            {
                Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), root, StringComparison.OrdinalIgnoreCase);
                Assert.StartsWith("trade-current-426-", Path.GetFileName(root), StringComparison.Ordinal);
                for (var attempt = 0; Directory.Exists(root); attempt++)
                {
                    try { Directory.Delete(root, recursive: true); break; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        if (attempt >= 5) throw;
                        await Task.Delay(100 * (attempt + 1));
                    }
                }
            }
        }
    }

    private sealed class CurrentIdentityCaptureHandler : DelegatingHandler
    {
        public int SyncRequests { get; private set; }
        public int Rejections { get; private set; }
        public CurrentIdentityCaptureHandler() : base(new HttpClientHandler()) { }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.True(request.RequestUri!.IsLoopback);
            Assert.Equal("3", Assert.Single(request.Headers.GetValues(ClientCompatibilityHeaders.Protocol)));
            if (request.RequestUri.AbsolutePath.StartsWith("/sync/", StringComparison.Ordinal)) SyncRequests++;
            var response = await base.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.UpgradeRequired) Rejections++;
            return response;
        }
    }
}
