using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed partial class SalesAmountHostedSyncTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, true, true)]
    [InlineData(false, true, true, true)]
    public async Task CurrentClient_RentalEditorServerCalculationRetryAndReopen(bool create, bool zero, bool linked = false, bool individual = false)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "trade-rental-hosted-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        HostedFixture? server = null;
        try
        {
            Assert.True(AppPaths.IsTestEnvironment);
            server = await HostedFixture.StartAsync(root);
            using var admin = new HttpClient { BaseAddress = server.BaseAddress };
            var administrator = await LoginAsync(admin, "admin", server.Password);
            admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", administrator.Token);
            new DesktopClientIdentityProvider().Apply(admin);
            await PostAsync(admin, "users", new CreateUserRequest {
                Username = "rental-editor", Password = server.Password, Role = DomainConstants.RoleUser,
                TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
                Permissions = [AppPermissionNames.RentalViewAll, AppPermissionNames.RentalProfileEdit] });
            var customerId = Guid.NewGuid(); var itemId = Guid.NewGuid(); var profileId = Guid.NewGuid();
            var linkedAssetId = Guid.NewGuid();
            await PostAsync(admin, "customers", new CustomerDto {
                Id = customerId, NameOriginal = "HOSTED-RENTAL-CUSTOMER", TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet });
            await PostAsync(admin, "items", new ItemDto {
                Id = itemId, NameOriginal = "HOSTED-RENTAL-ITEM", TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet, TrackingType = ItemTrackingTypes.NonStock,
                ItemKind = ItemKinds.Product, Unit = "EA", SalePrice = zero ? 0 : 1100 });
            if (!create)
            {
                var seed = new RentalBillingProfileDto {
                    Id = profileId, ProfileKey = "HOSTED-" + profileId.ToString("N"),
                    CustomerId = customerId, CustomerName = "HOSTED-RENTAL-CUSTOMER",
                    TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet, MonthlyAmount = zero ? 0 : 2200,
                    DepositAmount = 12000, ContractDate = new(2026, 9, 1), ContractStartDate = new(2026, 9, 1),
                    BillingDay = 25, BillingCycleMonths = 1, IsActive = true,
                    BillingTemplateJson = JsonSerializer.Serialize(new[] { new {
                        ItemId = Guid.NewGuid(), CatalogItemId = itemId, DisplayItemName = "HOSTED-RENTAL-ITEM",
                        BillingLineMode = individual ? "개별" : "묶음", Unit = "EA", Quantity = 2m,
                        UnitPrice = zero ? 0m : 1100m, Amount = zero ? 0m : 2200m,
                        IncludedAssetIds = linked ? new[] { linkedAssetId } : Array.Empty<Guid>(), Note = "기존 비고" } }) };
                using var response = await admin.PostAsJsonAsync("sync/push", new SyncPushRequest {
                    DeviceId = "isolated-seed", RentalBillingScheduleVersion = RentalBillingScheduleRules.ScheduleCapabilityVersion,
                    RentalBillingProfiles = [seed],
                    RentalAssets = linked ? [new RentalAssetDto {
                        Id = linkedAssetId, AssetKey = "LINK-" + linkedAssetId.ToString("N"),
                        CustomerId = customerId, BillingProfileId = profileId, ItemId = itemId,
                        TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
                        ResponsibleOfficeCode = OfficeCodeCatalog.Usenet, ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                        ManagementNumber = "HOSTED-LINK", ItemName = "HOSTED-RENTAL-ITEM", CustomerName = "HOSTED-RENTAL-CUSTOMER",
                        AssetStatus = "임대", PurchasePrice = 10000, SalePrice = 20000, MonthlyFee = zero ? 0 : 1100,
                        DepositText = "12000", ContractDate = new(2026, 9, 1), ContractStartDate = new(2026, 9, 1) }] : [] });
                response.EnsureSuccessStatusCode();
                var result = (await response.Content.ReadFromJsonAsync<SyncPushResult>())!;
                Assert.Empty(result.Conflicts); Assert.Equal(linked ? 2 : 1, result.AcceptedCount);
            }
            using var loginHttp = new HttpClient { BaseAddress = server.BaseAddress };
            var login = await LoginAsync(loginHttp, "rental-editor", server.Password);
            var session = new SessionState(); session.SetSession(login.Token, login.User, login.ExpiresAtUtc);
            var options = new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(new SqliteConnectionStringBuilder {
                DataSource = Path.Combine(root, "desktop.db"), Pooling = false }.ToString()).Options;
            using var capture = new RentalCaptureHandler();
            await using (var db = new LocalDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                var dispatcher = new SyncRequestDispatcher();
                var local = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
                var rental = new RentalStateService(db, local);
                using var http = new HttpClient(capture, disposeHandler: false) { BaseAddress = server.BaseAddress };
                var api = new ErpApiClient(http, session, local);
                using var sync = new SyncService(db, local, rental, api, session, dispatcher,
                    new SyncDiagnosticsService(session, () => new LocalDbContext(options)));
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                Assert.True(await sync.TrySyncAsync(deadline.Token), await LastSyncErrorAsync(db));
                var vm = new RentalBillingViewModel(rental, local, session);
                try
                {
                    if (create)
                    {
                        await vm.LoadAsync(); await vm.NewProfileCommand.ExecuteAsync(null);
                        vm.ApplySelectedCustomer(await db.Customers.AsNoTracking().SingleAsync(x => x.Id == customerId));
                        vm.EditContractDate = new(2026, 9, 1); vm.EditContractStartDate = new(2026, 9, 1);
                        var line = Assert.Single(vm.TemplateItems);
                        line.CatalogItemId = itemId; line.DisplayItemName = "HOSTED-RENTAL-ITEM";
                        line.BillingLineMode = "묶음"; line.Unit = "EA";
                        profileId = vm.EditId;
                    }
                    else await vm.LoadAndSelectProfileAsync(profileId);
                    vm.LinkAssetsLater = true;
                    Assert.True(vm.AreRentalAmountsReadOnly);
                    Assert.Null(vm.EditMonthlyAmount);
                    if (linked)
                    {
                        var asset = Assert.Single(vm.IncludedAssets);
                        Assert.Null(asset.PurchasePrice); Assert.Null(asset.SalePrice); Assert.Null(asset.MonthlyFee);
                        Assert.True(asset.SalesAmountsReadOnly);
                    }
                    Assert.Null(Assert.Single(vm.TemplateItems).UnitPrice);
                    var edited = Assert.Single(vm.TemplateItems); edited.Quantity = 3; edited.Note = "PC 첫 비고";
                    vm.EditNotes = "PC 첫 메모";
                    Assert.True(vm.SaveCommand.CanExecute(null));
                    await vm.SaveCommand.ExecuteAsync(null);
                    Assert.Contains("저장", vm.StatusMessage);
                }
                finally { await vm.CancelAndDrainPendingBackgroundWorkAsync(); }
                db.ChangeTracker.Clear();
                var draft = await db.RentalBillingProfiles.AsNoTracking().SingleAsync(x => x.Id == profileId);
                Assert.True(draft.IsDirty); Assert.True(draft.AmountsHidden); AssertRentalProfileHidden(LocalMappings.ToDto(draft));
                var synced = await sync.TrySyncAsync(deadline.Token);
                Assert.True(synced, await LastSyncErrorAsync(db) + "\nServer: " + (await ReadAdminRentalProfileAsync(admin, profileId)).BillingTemplateJson + "\nSubmitted: " + draft.BillingTemplateJson);
                var stored = await ReadAdminRentalProfileAsync(admin, profileId);
                Assert.Equal(zero ? 0m : 3300m, stored.MonthlyAmount);
                Assert.Equal("PC 첫 메모", stored.Notes);
                Assert.Equal(create ? 0m : 12000m, stored.DepositAmount);
                Assert.Equal(1, capture.LostAcknowledgements);
                Assert.Contains(capture.Profiles.GroupBy(x => x.MutationId), group => !string.IsNullOrEmpty(group.Key) && group.Count() > 1);
                foreach (var group in capture.Profiles.GroupBy(x => x.MutationId).Where(group => group.Count() > 1))
                    Assert.All(group.Skip(1), retry => Assert.Equal(JsonSerializer.Serialize(group.First()), JsonSerializer.Serialize(retry)));
                Assert.All(capture.Profiles, AssertRentalProfileHidden);
                if (linked)
                {
                    var pull = (await admin.GetFromJsonAsync<SyncPullResponse>($"sync/pull?sinceRev=0&rentalBillingScheduleVersion={RentalBillingScheduleRules.ScheduleCapabilityVersion}"))!;
                    var asset = Assert.Single(pull.RentalAssets, row => row.Id == linkedAssetId);
                    Assert.Equal(10000m, asset.PurchasePrice); Assert.Equal(20000m, asset.SalePrice);
                    Assert.Equal(zero ? 0m : 1100m, asset.MonthlyFee); Assert.Equal("12000", asset.DepositText);
                    Assert.Equal(profileId, asset.BillingProfileId);
                }
            }
            // Reopen the actual SQLite database and recreate the client/session/services.
            session = new SessionState(); session.SetSession(login.Token, login.User, login.ExpiresAtUtc);
            await using (var db = new LocalDbContext(options))
            {
                var dispatcher = new SyncRequestDispatcher();
                var local = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
                var rental = new RentalStateService(db, local);
                using var http = new HttpClient(capture, disposeHandler: false) { BaseAddress = server.BaseAddress };
                var api = new ErpApiClient(http, session, local);
                using var sync = new SyncService(db, local, rental, api, session, dispatcher,
                    new SyncDiagnosticsService(session, () => new LocalDbContext(options)));
                var persisted = await db.RentalBillingProfiles.AsNoTracking().SingleAsync(x => x.Id == profileId);
                Assert.False(persisted.IsDirty); Assert.True(persisted.AmountsHidden);
                AssertRentalProfileHidden(LocalMappings.ToDto(persisted));
                var vm = new RentalBillingViewModel(rental, local, session);
                try
                {
                    await vm.LoadAndSelectProfileAsync(profileId); vm.LinkAssetsLater = true;
                    Assert.Null(vm.EditMonthlyAmount);
                    var line = Assert.Single(vm.TemplateItems); Assert.Null(line.UnitPrice); Assert.Null(line.Amount);
                    Assert.Equal(3m, line.Quantity); Assert.Equal("PC 첫 비고", line.Note);
                    line.Quantity = 4; line.Note = "PC 재시작 비고"; vm.EditNotes = "PC 재시작 메모";
                    await vm.SaveCommand.ExecuteAsync(null); Assert.Contains("저장", vm.StatusMessage);
                }
                finally { await vm.CancelAndDrainPendingBackgroundWorkAsync(); }
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                Assert.True(await sync.TrySyncAsync(deadline.Token), await LastSyncErrorAsync(db));
                var stored = await ReadAdminRentalProfileAsync(admin, profileId);
                Assert.Equal(zero ? 0m : 4400m, stored.MonthlyAmount);
                Assert.Equal("PC 재시작 메모", stored.Notes);
                Assert.Equal("PC 재시작 비고", JsonNode.Parse(stored.BillingTemplateJson)![0]!["Note"]!.GetValue<string>());
                var revision = stored.Revision;
                Assert.True(await sync.TrySyncAsync(deadline.Token), await LastSyncErrorAsync(db));
                Assert.Equal(revision, (await ReadAdminRentalProfileAsync(admin, profileId)).Revision);
                Assert.All(capture.Profiles, AssertRentalProfileHidden);
                if (linked)
                {
                    var pull = (await admin.GetFromJsonAsync<SyncPullResponse>($"sync/pull?sinceRev=0&rentalBillingScheduleVersion={RentalBillingScheduleRules.ScheduleCapabilityVersion}"))!;
                    var asset = Assert.Single(pull.RentalAssets, row => row.Id == linkedAssetId);
                    Assert.Equal(10000m, asset.PurchasePrice); Assert.Equal(20000m, asset.SalePrice);
                    Assert.Equal(zero ? 0m : 1100m, asset.MonthlyFee); Assert.Equal("12000", asset.DepositText);
                    Assert.Equal(profileId, asset.BillingProfileId);
                }
                var outbox = await db.SyncOutboxEntries.Where(x => x.EntityName == nameof(LocalRentalBillingProfile)).ToListAsync();
                Assert.NotEmpty(outbox); Assert.All(outbox, x => Assert.NotNull(x.AcknowledgedAtUtc));
            }
        }
        finally
        {
            if (server is not null) await server.DisposeAsync();
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(root));
            Assert.StartsWith("trade-rental-hosted-", Path.GetFileName(root), StringComparison.Ordinal);
            for (var attempt = 0; Directory.Exists(root); attempt++)
            {
                try { Directory.Delete(root, recursive: true); break; }
                catch (IOException) when (attempt < 100) { await Task.Delay(100); }
                catch (UnauthorizedAccessException) when (attempt < 100) { await Task.Delay(100); }
            }
        }
    }

    private static async Task<RentalBillingProfileDto> ReadAdminRentalProfileAsync(HttpClient admin, Guid id)
        => Assert.Single((await admin.GetFromJsonAsync<SyncPullResponse>($"sync/pull?sinceRev=0&rentalBillingScheduleVersion={RentalBillingScheduleRules.ScheduleCapabilityVersion}"))!.RentalBillingProfiles, x => x.Id == id);

    private static void AssertRentalProfileHidden(RentalBillingProfileDto row)
    {
        Assert.Null(row.MonthlyAmount); Assert.Null(row.DepositAmount); Assert.Null(row.SettledAmount); Assert.Null(row.OutstandingAmount);
        foreach (var line in JsonNode.Parse(row.BillingTemplateJson)!.AsArray())
        { Assert.Null(line!["UnitPrice"]); Assert.Null(line["Amount"]); }
    }

    private sealed class RentalCaptureHandler : DelegatingHandler
    {
        public List<RentalBillingProfileDto> Profiles { get; } = [];
        public int LostAcknowledgements { get; private set; }
        public RentalCaptureHandler() : base(new HttpClientHandler()) { }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.True(request.RequestUri!.IsLoopback);
            Assert.Equal("4", Assert.Single(request.Headers.GetValues(ClientCompatibilityHeaders.Protocol)));
            var hasProfile = false;
            if (request.RequestUri.AbsolutePath == "/sync/push")
            {
                var body = JsonSerializer.Deserialize<SyncPushRequest>(await request.Content!.ReadAsStringAsync(ct), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                Profiles.AddRange(body.RentalBillingProfiles); hasProfile = body.RentalBillingProfiles.Count > 0;
            }
            var response = await base.SendAsync(request, ct);
            if (hasProfile && response.IsSuccessStatusCode && LostAcknowledgements == 0)
            {
                LostAcknowledgements++; response.Dispose();
                throw new HttpRequestException("Fixture lost rental acknowledgement after committed write.");
            }
            return response;
        }
    }
}
