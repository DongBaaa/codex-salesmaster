using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
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
    [InlineData("new", false, false)]
    [InlineData("new", true, false)]
    [InlineData("relink", false, false)]
    [InlineData("reference", false, false)]
    [InlineData("new", false, true)]
    [InlineData("new", true, true)]
    [InlineData("relink", false, true)]
    [InlineData("reference", false, true)]
    public async Task RentalAssetLinkSave_CurrentClientFullAndRestrictedAmounts(string mode, bool zero, bool purchaseVisible)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "trade-asset-link-http-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        HostedFixture? server = null;
        try
        {
            Assert.True(AppPaths.IsTestEnvironment);
            server = await HostedFixture.StartAsync(root);
            using var admin = new HttpClient { BaseAddress = server.BaseAddress };
            var adminLogin = await LoginAsync(admin, "admin", server.Password);
            admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminLogin.Token);
            new DesktopClientIdentityProvider().Apply(admin);
            var permissions = new List<string> { AppPermissionNames.RentalViewAll, AppPermissionNames.RentalProfileEdit,
                AppPermissionNames.RentalAssetEdit, AppPermissionNames.AmountViewSales };
            if (purchaseVisible) permissions.Add(AppPermissionNames.AmountViewPurchase);
            await PostAsync(admin, "users", new CreateUserRequest {
                Username = "link-editor", Password = server.Password, Role = DomainConstants.RoleUser,
                TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
                Permissions = permissions });
            var customerId = Guid.NewGuid(); var assetId = Guid.NewGuid(); var previousId = Guid.NewGuid();
            await PostAsync(admin, "customers", new CustomerDto {
                Id = customerId, NameOriginal = "LINK-TARGET", TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet });
            var reference = mode == "reference";
            var assetTenant = reference ? TenantScopeCatalog.Itworld : TenantScopeCatalog.UsenetGroup;
            var assetOffice = reference ? OfficeCodeCatalog.Itworld : OfficeCodeCatalog.Usenet;
            var seedAsset = new RentalAssetDto {
                Id = assetId, AssetKey = "LINK-" + assetId.ToString("N"), TenantCode = assetTenant,
                OfficeCode = assetOffice, ResponsibleOfficeCode = assetOffice, ManagementCompanyCode = assetOffice,
                ManagementNumber = "LINK-HTTP", ItemName = "LINK-MACHINE", AssetStatus = "임대진행중",
                BillingEligibilityStatus = "청구대상", PurchasePrice = 123456, SalePrice = 234567,
                MonthlyFee = zero ? 0 : 300, DepositText = "50000", ContractMonths = 24,
                ContractDate = new(2026, 9, 1), ContractStartDate = new(2026, 9, 1),
                FreeSupplyItems = "정품 토너", PaidSupplyItems = "특수 용지", Notes = "원본 메모",
                BillingProfileId = mode == "relink" ? previousId : null };
            var seedRequest = new SyncPushRequest { DeviceId = "link-seed",
                RentalBillingScheduleVersion = RentalBillingScheduleRules.ScheduleCapabilityVersion, RentalAssets = [seedAsset] };
            seedRequest.CustomerContracts.Add(new CustomerContractDto {
                Id = Guid.NewGuid(), CustomerId = customerId, ContractType = "렌탈계약서",
                SignedDate = new(2026, 9, 1), IsPrimary = true });
            if (mode == "relink")
                seedRequest.RentalBillingProfiles.Add(new RentalBillingProfileDto {
                    Id = previousId, ProfileKey = "OLD-" + previousId.ToString("N"), CustomerId = customerId,
                    CustomerName = "LINK-TARGET", TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet, MonthlyAmount = 300, BillingDay = 25,
                    ContractDate = new(2026, 9, 1), ContractStartDate = new(2026, 9, 1),
                    BillingTemplateJson = JsonSerializer.Serialize(new[] { new {
                        ItemId = Guid.NewGuid(), DisplayItemName = "LINK-MACHINE", BillingLineMode = "묶음", Quantity = 1m,
                        UnitPrice = 300m, Amount = 300m, IncludedAssetIds = new[] { assetId } } }) });
            using (var seeded = await admin.PostAsJsonAsync("sync/push", seedRequest))
            {
                seeded.EnsureSuccessStatusCode();
                var result = (await seeded.Content.ReadFromJsonAsync<SyncPushResult>())!;
                Assert.Empty(result.Conflicts); Assert.Equal(mode == "relink" ? 3 : 2, result.AcceptedCount);
            }
            var before = Assert.Single((await PullAsync()).RentalAssets, x => x.Id == assetId);
            using var loginHttp = new HttpClient { BaseAddress = server.BaseAddress };
            var login = await LoginAsync(loginHttp, "link-editor", server.Password);
            var session = new SessionState(); session.SetSession(login.Token, login.User, login.ExpiresAtUtc);
            var options = new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(new SqliteConnectionStringBuilder {
                DataSource = Path.Combine(root, "desktop.db"), Pooling = false }.ToString()).Options;
            using var capture = new AssetLinkCaptureHandler();
            Guid savedId;
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
                Assert.True(await sync.TrySyncAsync(deadline.Token), await LastSyncErrorAsync(db) + capture.FailureSummary);
                var customer = await db.Customers.AsNoTracking().SingleAsync(x => x.Id == customerId);
                var dialog = new RentalAssetLinkDialogViewModel(rental, session, null, customerId,
                    "LINK-TARGET", OfficeCodeCatalog.Usenet, "본점") { IncludeOtherOfficeAssets = reference };
                await dialog.LoadAsync();
                if (reference)
                {
                    // The real tenant-filtered pull does not expose another tenant's
                    // asset. Do not fabricate a shared reference in the client cache.
                    Assert.DoesNotContain(dialog.Assets, x => x.AssetId == assetId);
                    Assert.False(await db.RentalAssets.AnyAsync(x => x.Id == assetId));
                    Assert.Empty(capture.Assets);
                    Assert.Empty(await db.SyncOutboxEntries.ToListAsync());
                    Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(Assert.Single((await PullAsync()).RentalAssets, x => x.Id == assetId)));
                    return;
                }
                var option = Assert.Single(dialog.Assets, x => x.AssetId == assetId);
                Assert.Equal(reference, option.IsReferenceOnly);
                Assert.Equal(purchaseVisible ? 123456m : (decimal?)null, option.PurchasePrice);
                Assert.Equal(zero ? 0m : 300m, option.MonthlyFee);
                option.IsSelected = true;
                if (!reference) option.Notes = "연결 후 메모";
                var denied = !purchaseVisible && !reference;
                var vm = new RentalBillingViewModel(rental, local, session, api);
                try
                {
                    await vm.LoadAsync(); await vm.NewProfileCommand.ExecuteAsync(null);
                    vm.ApplySelectedCustomer(customer);
                    vm.EditContractDate = new(2026, 9, 1); vm.EditContractStartDate = new(2026, 9, 1);
                    vm.EditBillingType = "개별"; savedId = vm.EditId;
                    var saved = await vm.ApplyAssetLinkSelectionsAndSaveAsync(dialog.GetSelectedAssets());
                    Assert.Equal(!denied, saved);
                    if (denied) Assert.Contains("금액", vm.StatusMessage);
                }
                finally { await vm.CancelAndDrainPendingBackgroundWorkAsync(); }
                if (denied)
                {
                    Assert.False(await db.RentalBillingProfiles.AnyAsync(x => x.Id == savedId));
                    Assert.False(await db.RentalBillingProfiles.AnyAsync(x => x.IsDirty));
                    Assert.False((await db.RentalAssets.AsNoTracking().SingleAsync(x => x.Id == assetId)).IsDirty);
                    Assert.Empty(await db.SyncOutboxEntries.ToListAsync());
                    Assert.True(await sync.TrySyncAsync(deadline.Token), await LastSyncErrorAsync(db));
                    Assert.Empty(capture.Assets);
                    Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(Assert.Single((await PullAsync()).RentalAssets, x => x.Id == assetId)));
                    return;
                }
                Assert.True(await sync.TrySyncAsync(deadline.Token), await LastSyncErrorAsync(db) + capture.FailureSummary);
                var after = Assert.Single((await PullAsync()).RentalAssets, x => x.Id == assetId);
                AssertAsset(after);
                var profile = Assert.Single((await PullAsync()).RentalBillingProfiles, x => x.Id == savedId);
                Assert.Contains(assetId.ToString(), profile.BillingTemplateJson, StringComparison.OrdinalIgnoreCase);
                var histories = (await PullAsync()).RentalAssetAssignmentHistories.Where(x => x.AssetId == assetId && !x.IsDeleted).ToList();
                Assert.Equal(savedId, Assert.Single(histories, x => x.IsCurrent).BillingProfileId);
                if (mode == "relink")
                {
                    var previous = Assert.Single((await PullAsync()).RentalBillingProfiles, x => x.Id == previousId);
                    Assert.DoesNotContain(assetId.ToString(), previous.BillingTemplateJson, StringComparison.OrdinalIgnoreCase);
                    Assert.Contains(histories, x => x.BillingProfileId == previousId && !x.IsCurrent && x.UnlinkedAtUtc.HasValue);
                }
                if (reference) Assert.DoesNotContain(capture.Assets, x => x.Id == assetId);
                else
                {
                    Assert.NotEmpty(capture.Assets);
                    Assert.All(capture.Assets, x => Assert.Equal(before.PurchasePrice, x.PurchasePrice));
                    Assert.Equal(1, capture.LostAcknowledgements);
                    Assert.Contains(capture.Assets.GroupBy(x => x.MutationId), group =>
                        !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1);
                }
                var revision = after.Revision;
                Assert.True(await sync.TrySyncAsync(deadline.Token), await LastSyncErrorAsync(db));
                Assert.Equal(revision, Assert.Single((await PullAsync()).RentalAssets, x => x.Id == assetId).Revision);
                Assert.Equal(histories.Count, (await PullAsync()).RentalAssetAssignmentHistories.Count(x => x.AssetId == assetId && !x.IsDeleted));
            }
            await using (var reopened = new LocalDbContext(options))
            {
                var asset = await reopened.RentalAssets.AsNoTracking().SingleAsync(x => x.Id == assetId);
                Assert.Equal(!purchaseVisible, asset.PurchaseAmountsHidden); Assert.False(asset.IsDirty);
                Assert.Equal(reference ? before.BillingProfileId : savedId, asset.BillingProfileId);
                Assert.NotEmpty(await reopened.SyncOutboxEntries.ToListAsync());
                Assert.All(await reopened.SyncOutboxEntries.ToListAsync(), row => Assert.NotNull(row.AcknowledgedAtUtc));
            }

            async Task<SyncPullResponse> PullAsync() => (await admin.GetFromJsonAsync<SyncPullResponse>(
                $"sync/pull?sinceRev=0&rentalBillingScheduleVersion={RentalBillingScheduleRules.ScheduleCapabilityVersion}"))!;
            void AssertAsset(RentalAssetDto after)
            {
                Assert.Equal(before.TenantCode, after.TenantCode); Assert.Equal(before.OfficeCode, after.OfficeCode);
                Assert.Equal(before.ManagementCompanyCode, after.ManagementCompanyCode);
                Assert.Equal(before.PurchasePrice, after.PurchasePrice); Assert.Equal(before.SalePrice, after.SalePrice);
                Assert.Equal(before.MonthlyFee, after.MonthlyFee); Assert.Equal(before.DepositText, after.DepositText);
                Assert.Equal(before.ContractMonths, after.ContractMonths);
                Assert.Equal(before.FreeSupplyItems, after.FreeSupplyItems); Assert.Equal(before.PaidSupplyItems, after.PaidSupplyItems);
                Assert.Equal(reference ? before.BillingProfileId : savedId, after.BillingProfileId);
                Assert.Equal(reference ? before.Notes : "연결 후 메모", after.Notes);
                if (reference) Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after));
            }
        }
        finally
        {
            if (server is not null) await server.DisposeAsync();
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(root));
            Assert.StartsWith("trade-asset-link-http-", Path.GetFileName(root), StringComparison.Ordinal);
            for (var attempt = 0; Directory.Exists(root); attempt++)
            {
                try { Directory.Delete(root, recursive: true); break; }
                catch (IOException) when (attempt < 100) { await Task.Delay(100); }
                catch (UnauthorizedAccessException) when (attempt < 100) { await Task.Delay(100); }
            }
        }
    }

    private sealed class AssetLinkCaptureHandler : DelegatingHandler
    {
        public List<RentalAssetDto> Assets { get; } = [];
        public string FailureSummary { get; private set; } = "";
        public int LostAcknowledgements { get; private set; }
        public AssetLinkCaptureHandler() : base(new HttpClientHandler()) { }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.True(request.RequestUri!.IsLoopback);
            Assert.Equal("4", Assert.Single(request.Headers.GetValues(ClientCompatibilityHeaders.Protocol)));
            var hasAssets = false;
            if (request.RequestUri.AbsolutePath == "/sync/push")
            {
                var body = JsonSerializer.Deserialize<SyncPushRequest>(await request.Content!.ReadAsStringAsync(ct), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                Assets.AddRange(body.RentalAssets);
                hasAssets = body.RentalAssets.Count > 0;
            }
            var response = await base.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                FailureSummary += $" {request.RequestUri.AbsolutePath}: HTTP {(int)response.StatusCode}";
            else if (request.RequestUri.AbsolutePath == "/sync/push")
            {
                var result = JsonSerializer.Deserialize<SyncPushResult>(
                    await response.Content.ReadAsStringAsync(ct), new JsonSerializerOptions(JsonSerializerDefaults.Web));
                FailureSummary += string.Join("; ", result!.Conflicts.Select(x => $"{x.EntityName}: {x.Reason}"));
                if (hasAssets && result.Conflicts.Count == 0 && LostAcknowledgements == 0)
                {
                    LostAcknowledgements++;
                    response.Dispose();
                    throw new HttpRequestException("Fixture lost acknowledgement after asset-link commit.");
                }
            }
            return response;
        }
    }
}
