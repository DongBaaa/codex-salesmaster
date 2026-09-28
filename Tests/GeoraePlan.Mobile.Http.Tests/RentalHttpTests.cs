using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using GeoraePlan.Mobile.App.Models;
using GeoraePlan.Mobile.App.Services;
using Microsoft.Data.Sqlite;
using 거래플랜.Shared.Contracts;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace GeoraePlan.Mobile.Http.Tests;

public sealed partial class RentalHttpTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task Protocol4Candidate_ServerCalculatesQuantity_PreservesHiddenRestartAndLostAckRetry(bool zeroPrice, bool confirmedContractZero)
    {
        await RunAsync(async (server, admin, session, settings) =>
        {
            var item = new ItemDto { Id = Guid.NewGuid(), NameOriginal = "격리 렌탈 품목", TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet, TrackingType = ItemTrackingTypes.NonStock, ItemKind = ItemKinds.Product,
                Unit = "EA", SalePrice = zeroPrice ? 0 : 1100 };
            var customer = new CustomerDto { Id = Guid.NewGuid(), NameOriginal = "격리 렌탈 거래처", TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet };
            await PostAsync(admin, "items", item); await PostAsync(admin, "customers", customer);
            var api = Client(session, settings, ClientCompatibilityHeaders.NullableRentalProfileAmountsProtocolVersion);
            using var capture = new CaptureHandler();
            var field = typeof(GeoraePlanApiClient).GetField("_http", BindingFlags.NonPublic | BindingFlags.Instance)!;
            ((HttpClient)field.GetValue(api)!).Dispose(); using var http = new HttpClient(capture); field.SetValue(api, http);
            var store = new JsonSyncStateStore(session); var sync = Coordinator(session, store, api);
            var profile = new RentalBillingProfileDto { Id = Guid.NewGuid(), ProfileKey = "http-" + Guid.NewGuid().ToString("N"),
                CustomerId = customer.Id, CustomerName = customer.NameOriginal, TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                MonthlyAmount = null, DepositAmount = null, SettledAmount = null, OutstandingAmount = null,
                MutationId = Guid.NewGuid().ToString("N"), MutationCreatedAtUtc = DateTime.UtcNow, Notes = "신규 비고",
                BillingTemplateJson = JsonSerializer.Serialize(new[] { new { ItemId = Guid.NewGuid(), CatalogItemId = item.Id,
                    DisplayItemName = item.NameOriginal, Unit = "EA", Quantity = 3m, UnitPrice = (decimal?)null, Amount = (decimal?)null, Note = "품목 비고" } }) };
            if (confirmedContractZero)
            {
                // Catalog default zero means unconfigured. An admin-authored contract row is an explicit zero.
                profile.MonthlyAmount = profile.DepositAmount = profile.SettledAmount = profile.OutstandingAmount = 0;
                var confirmed = JsonNode.Parse(profile.BillingTemplateJson)!.AsArray();
                confirmed[0]!["UnitPrice"] = 0m; confirmed[0]!["Amount"] = 0m;
                profile.BillingTemplateJson = confirmed.ToJsonString();
                using var seedResponse = await admin.PostAsJsonAsync("sync/push", new SyncPushRequest {
                    DeviceId = "isolated-admin", RentalBillingScheduleVersion = RentalBillingScheduleRules.ScheduleCapabilityVersion,
                    RentalBillingProfiles = [profile] });
                seedResponse.EnsureSuccessStatusCode(); var seed = (await seedResponse.Content.ReadFromJsonAsync<SyncPushResult>())!;
                Assert.Empty(seed.Conflicts); Assert.Equal(1, seed.AcceptedCount);
                var initial = await sync.PullAsync(); Assert.True(string.IsNullOrEmpty(initial.LastError), initial.LastError);
                profile = Assert.Single(initial.SyncedRentalBillingProfiles, x => x.Id == profile.Id);
                AssertHidden(profile); profile.ExpectedRevision = profile.Revision;
                profile.MutationId = Guid.NewGuid().ToString("N"); profile.MutationCreatedAtUtc = DateTime.UtcNow;
                profile.UpdatedAtUtc = DateTime.UtcNow.AddSeconds(1);
            }
            await sync.QueueRentalBillingProfileDraftAsync(profile);
            var pushed = await sync.PushAsync();
            if (zeroPrice && !confirmedContractZero)
            {
                Assert.Contains("단가가 등록되어 있지 않습니다", pushed.LastError);
                var all = await admin.GetFromJsonAsync<SyncPullResponse>($"sync/pull?sinceRev=0&rentalBillingScheduleVersion={RentalBillingScheduleRules.ScheduleCapabilityVersion}");
                Assert.DoesNotContain(all!.RentalBillingProfiles, x => x.Id == profile.Id);
                Assert.Single(pushed.PendingPush.RentalBillingProfiles);
                return;
            }
            Assert.True(string.IsNullOrEmpty(pushed.LastError), pushed.LastError);
            Assert.Empty(pushed.PendingPush.RentalBillingProfiles);
            var saved = await AdminProfile(admin, profile.Id); Assert.Equal(zeroPrice ? 0 : 3300m, saved.MonthlyAmount);
            Assert.Equal("신규 비고", saved.Notes);
            var pulled = await sync.PullAsync(); Assert.True(string.IsNullOrEmpty(pulled.LastError), pulled.LastError);
            var hidden = Assert.Single(pulled.SyncedRentalBillingProfiles, x => x.Id == profile.Id); AssertHidden(hidden);
            var edit = JsonSerializer.Deserialize<RentalBillingProfileDto>(JsonSerializer.Serialize(hidden))!;
            edit.ExpectedRevision = edit.Revision; edit.MutationId = Guid.NewGuid().ToString("N"); edit.MutationCreatedAtUtc = DateTime.UtcNow;
            edit.UpdatedAtUtc = DateTime.UtcNow.AddSeconds(1); edit.Notes = "재시작할 비고";
            var rows = JsonNode.Parse(edit.BillingTemplateJson)!.AsArray(); rows[0]!["Quantity"] = 4m; rows[0]!["Note"] = "수정 품목 비고";
            edit.BillingTemplateJson = rows.ToJsonString(); await sync.QueueRentalBillingProfileDraftAsync(edit);
            capture.LoseNextPushAck = true;
            var lost = await sync.PushAsync(); Assert.NotEmpty(lost.LastError); Assert.Single(lost.PendingPush.RentalBillingProfiles);
            saved = await AdminProfile(admin, profile.Id); Assert.Equal(zeroPrice ? 0 : 4400m, saved.MonthlyAmount);
            var revision = saved.Revision; Assert.Equal("재시작할 비고", saved.Notes);
            var restoredSession = new SessionStore(); Assert.True(await restoredSession.HasUsableSessionAsync());
            var restoredApi = Client(restoredSession, settings, ClientCompatibilityHeaders.NullableRentalProfileAmountsProtocolVersion);
            using var retryCapture = new CaptureHandler();
            ((HttpClient)field.GetValue(restoredApi)!).Dispose(); using var retryHttp = new HttpClient(retryCapture); field.SetValue(restoredApi, retryHttp);
            var reopened = new JsonSyncStateStore(restoredSession); var retry = await Coordinator(restoredSession, reopened, restoredApi).PushAsync();
            Assert.True(string.IsNullOrEmpty(retry.LastError), retry.LastError); Assert.Empty(retry.PendingPush.RentalBillingProfiles);
            Assert.Equal(revision, (await AdminProfile(admin, profile.Id)).Revision);
            Assert.Equal(capture.PushPayloads[^1], Assert.Single(retryCapture.PushPayloads));
            await Coordinator(restoredSession, reopened, restoredApi).PullAsync();
            var reloaded = await new JsonSyncStateStore(restoredSession).LoadAsync();
            var final = Assert.Single(reloaded.SyncedRentalBillingProfiles, x => x.Id == profile.Id); AssertHidden(final);
            Assert.Equal("재시작할 비고", final.Notes);
            Assert.Equal(4m, JsonNode.Parse(final.BillingTemplateJson)![0]!["Quantity"]!.GetValue<decimal>());
            foreach (var payload in capture.PushPayloads)
            {
                var dto = JsonSerializer.Deserialize<SyncPushRequest>(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.RentalBillingProfiles.Single();
                AssertHidden(dto);
            }
        });
    }

    [Fact]
    public async Task CurrentUnmodifiedMobileIdentity_IsRejectedWithRequiredProtocol4()
    {
        await RunAsync(async (_, _, session, settings) =>
        {
            var identity = new MobileClientIdentityProvider();
            var api = new GeoraePlanApiClient(settings, session, new MobileSessionRecoveryService(settings, session, identity), identity);
            var error = await Assert.ThrowsAsync<MobileClientUpgradeRequiredException>(() => api.PullAsync(0));
            Assert.Equal(HttpStatusCode.UpgradeRequired, error.StatusCode);
            Assert.Equal(3, error.Response.Client.ProtocolVersion); Assert.Equal(4, error.Response.Required.MinimumProtocolVersion);
            Assert.Equal(3, ClientCompatibilityHeaders.CurrentProtocolVersion);
        });
    }

    private static void AssertHidden(RentalBillingProfileDto row)
    {
        Assert.Null(row.MonthlyAmount); Assert.Null(row.DepositAmount); Assert.Null(row.SettledAmount); Assert.Null(row.OutstandingAmount);
        var item = JsonNode.Parse(row.BillingTemplateJson)![0]!; Assert.Null(item["UnitPrice"]); Assert.Null(item["Amount"]);
    }
    private static SyncCoordinator Coordinator(SessionStore s, JsonSyncStateStore store, GeoraePlanApiClient api)
        => new(store, api, new PaymentAttachmentDraftStore(), new CustomerContractCacheStore(s), s);
    private static GeoraePlanApiClient Client(SessionStore s, SettingsService settings, int protocol)
    {
        var identity = new MobileClientIdentityProvider("kr.georaeplan.mobile", "android", "0.2.83", "194", protocol);
        return new(settings, s, new MobileSessionRecoveryService(settings, s, identity), identity);
    }
    private static async Task<RentalBillingProfileDto> AdminProfile(HttpClient admin, Guid id)
    {
        var pull = await admin.GetFromJsonAsync<SyncPullResponse>($"sync/pull?sinceRev=0&rentalBillingScheduleVersion={RentalBillingScheduleRules.ScheduleCapabilityVersion}");
        return Assert.Single(pull!.RentalBillingProfiles, x => x.Id == id);
    }
    private static async Task PostAsync<T>(HttpClient client, string path, T value)
    { using var response = await client.PostAsJsonAsync(path, value); Assert.True(response.IsSuccessStatusCode, $"{path}: HTTP {(int)response.StatusCode}"); }
    private static async Task RunAsync(Func<HostedFixture,HttpClient,SessionStore,SettingsService,Task> test, string[]? permissions = null)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "georaeplan-mobile-http-tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root); Preferences.Default.Reset(); SecureStorage.Default.Reset(); FileSystem.AppDataDirectory = Path.Combine(root, "mobile");
        HostedFixture? server = null;
        Exception? testFailure = null;
        try
        {
            server = await HostedFixture.StartAsync(root);
            using var admin = new HttpClient { BaseAddress = server.BaseAddress };
            var loginResponse = await admin.PostAsJsonAsync("auth/login", new LoginRequest { Username = "admin", Password = server.Password });
            loginResponse.EnsureSuccessStatusCode(); var login = (await loginResponse.Content.ReadFromJsonAsync<LoginResponse>())!; loginResponse.Dispose();
            admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
            admin.DefaultRequestHeaders.Add(ClientCompatibilityHeaders.Protocol, "4");
            await PostAsync(admin, "users", new CreateUserRequest { Username = "rental-reader", Password = server.Password,
                Role = "User", TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeOfficeOnly, Permissions = permissions?.ToList() ?? ["Rental.ProfileEdit", "Rental.AssetEdit"] });
            var settings = new SettingsService(); await settings.SaveBaseUrlAsync(server.BaseAddress.ToString());
            var session = new SessionStore(); var api = Client(session, settings, 4);
            var user = await api.LoginAsync(new LoginRequest { Username = "rental-reader", Password = server.Password });
            Assert.NotNull(user); await session.SaveAsync(user);
            await test(server, admin, session, settings);
        }
        catch (Exception error) { testFailure = error; }
        finally
        {
            if (server is not null) await server.DisposeAsync(); SqliteConnection.ClearAllPools();
            var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "georaeplan-mobile-http-tests")) + Path.DirectorySeparatorChar;
            Assert.StartsWith(allowed, root);
            // Windows can retain image sections briefly after the owned server exits.
            for (var attempt = 0; ; attempt++)
            {
                try { Directory.Delete(root, true); break; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    if (attempt == 20)
                        throw testFailure is null ? error : new AggregateException(testFailure, error);
                    await Task.Delay(100);
                }
            }
        }
        if (testFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(testFailure).Throw();
    }
    private sealed class CaptureHandler : DelegatingHandler
    {
        public bool LoseNextPushAck { get; set; }
        public List<string> PushPayloads { get; } = [];
        public List<SyncPushResult> PushResults { get; } = [];
        public CaptureHandler() : base(new HttpClientHandler()) { }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var push = request.RequestUri!.AbsolutePath == "/sync/push";
            if (push) PushPayloads.Add(await request.Content!.ReadAsStringAsync(ct));
            var response = await base.SendAsync(request, ct);
            if (push && response.IsSuccessStatusCode)
                PushResults.Add(JsonSerializer.Deserialize<SyncPushResult>(await response.Content.ReadAsStringAsync(ct),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))!);
            if (push && LoseNextPushAck && response.IsSuccessStatusCode)
            { LoseNextPushAck = false; response.Dispose(); throw new HttpRequestException("simulated lost acknowledgement after real server commit"); }
            return response;
        }
    }
}
