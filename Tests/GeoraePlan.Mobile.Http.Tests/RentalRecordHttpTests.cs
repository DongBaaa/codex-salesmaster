using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using GeoraePlan.Mobile.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Mobile.Http.Tests;

public sealed partial class RentalHttpTests
{
    [Theory]
    [InlineData("none", false)]
    [InlineData("purchase", false)]
    [InlineData("sales", false)]
    [InlineData("none", true)]
    public async Task Protocol4Candidate_RentalRecordsSaveNotes_WithDirectionalPrivacyAndDurableRetry(string direction, bool zero)
    {
        var permissions = new List<string> { "Rental.ProfileEdit", "Rental.AssetEdit" };
        if (direction == "purchase") permissions.Add("Amount.ViewPurchase");
        if (direction == "sales") permissions.Add("Amount.ViewSales");
        await RunAsync(async (_, admin, session, settings) =>
        {
            var original = await SeedRecords(admin, OfficeCodeCatalog.Usenet, zero);
            var api = Client(session, settings, 4);
            using var capture = new CaptureHandler(); using var http = InjectCapture(api, capture);
            var store = new JsonSyncStateStore(session); var sync = Coordinator(session, store, api);
            var pulled = await sync.PullAsync(); Assert.True(string.IsNullOrEmpty(pulled.LastError), pulled.LastError);
            var asset = Copy(Assert.Single(pulled.SyncedRentalAssets, x => x.Id == original.Asset.Id));
            var history = Copy(Assert.Single(pulled.SyncedRentalAssetAssignmentHistories, x => x.Id == original.History.Id));
            var log = Copy(Assert.Single(pulled.SyncedRentalBillingLogs, x => x.Id == original.Log.Id));
            AssertRecordPrivacy(asset, history, log, direction, original);
            Stamp(asset); Stamp(history); Stamp(log);
            asset.Notes = "직원 자산 비고"; history.ChangeReason = "직원 설치 비고"; log.Note = "직원 청구 비고";
            if (direction == "purchase") asset.PurchasePrice = 222;
            if (direction == "sales") { asset.MonthlyFee = 333; history.MonthlyFee = 444; log.BilledAmount = 555; }
            await sync.QueueRentalAssetDraftAsync(asset);
            await sync.QueueRentalAssetAssignmentHistoryDraftAsync(history);
            await sync.QueueRentalBillingLogDraftAsync(log);
            capture.LoseNextPushAck = true;
            var lost = await sync.PushAsync(); Assert.NotEmpty(lost.LastError);
            Assert.Single(lost.PendingPush.RentalAssets); Assert.Single(lost.PendingPush.RentalAssetAssignmentHistories); Assert.Single(lost.PendingPush.RentalBillingLogs);
            var after = await ReadRecords(admin, original);
            Assert.Equal(direction == "purchase" ? 222m : original.Asset.PurchasePrice, after.Asset.PurchasePrice);
            Assert.Equal(direction == "sales" ? 333m : original.Asset.MonthlyFee, after.Asset.MonthlyFee);
            Assert.Equal(original.Asset.SalePrice, after.Asset.SalePrice); Assert.Equal(original.Asset.DepositText, after.Asset.DepositText);
            Assert.Equal(original.Asset.BlackOverageUnitPrice, after.Asset.BlackOverageUnitPrice);
            Assert.Equal(original.Asset.ColorOverageUnitPrice, after.Asset.ColorOverageUnitPrice);
            Assert.Equal(direction == "sales" ? 444m : original.History.MonthlyFee, after.History.MonthlyFee);
            Assert.Equal(direction == "sales" ? 555m : original.Log.BilledAmount, after.Log.BilledAmount);
            Assert.Equal("직원 자산 비고", after.Asset.Notes); Assert.Equal("직원 설치 비고", after.History.ChangeReason); Assert.Equal("직원 청구 비고", after.Log.Note);
            Assert.Equal(original.Log.Status, after.Log.Status);
            var restartedSession = new SessionStore(); Assert.True(await restartedSession.HasUsableSessionAsync());
            var retryApi = Client(restartedSession, settings, 4); using var retryCapture = new CaptureHandler(); using var retryHttp = InjectCapture(retryApi, retryCapture);
            var restarted = new JsonSyncStateStore(restartedSession); var retrySync = Coordinator(restartedSession, restarted, retryApi);
            var retried = await retrySync.PushAsync(); Assert.True(string.IsNullOrEmpty(retried.LastError), retried.LastError);
            Assert.Empty(retried.PendingPush.RentalAssets); Assert.Empty(retried.PendingPush.RentalAssetAssignmentHistories); Assert.Empty(retried.PendingPush.RentalBillingLogs);
            Assert.Equal(Assert.Single(capture.PushPayloads), Assert.Single(retryCapture.PushPayloads));
            var afterRetry = await ReadRecords(admin, original);
            Assert.Equal(JsonSerializer.Serialize(after), JsonSerializer.Serialize(afterRetry));
            await retrySync.PullAsync(); var reloaded = await new JsonSyncStateStore(restartedSession).LoadAsync();
            AssertRecordPrivacy(Assert.Single(reloaded.SyncedRentalAssets, x => x.Id == asset.Id),
                Assert.Single(reloaded.SyncedRentalAssetAssignmentHistories, x => x.Id == history.Id),
                Assert.Single(reloaded.SyncedRentalBillingLogs, x => x.Id == log.Id), direction, after);
            var sent = JsonSerializer.Deserialize<SyncPushRequest>(capture.PushPayloads[0], new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            AssertRecordPrivacy(Assert.Single(sent.RentalAssets), Assert.Single(sent.RentalAssetAssignmentHistories), Assert.Single(sent.RentalBillingLogs), direction, after);
        }, permissions.ToArray());
    }

    [Theory]
    [InlineData("foreign-office")]
    [InlineData("foreign-office-unshared")]
    [InlineData("foreign-tenant")]
    [InlineData("no-edit")]
    [InlineData("stale")]
    public async Task Protocol4Candidate_RentalRecordsCannotBypassOfficeEditOrRevisionGuards(string denied)
    {
        await RunAsync(async (_, admin, session, settings) =>
        {
            var otherOffice = denied.StartsWith("foreign-office", StringComparison.Ordinal);
            if (otherOffice)
            {
                var configuration = (await admin.GetFromJsonAsync<TenantConfigurationSnapshotDto>("tenant-settings"))!;
                var sharing = Assert.Single(configuration.SharingPolicies, x => x.SourceOfficeCode == OfficeCodeCatalog.Yeonsu && x.TargetOfficeCode == OfficeCodeCatalog.Usenet);
                Assert.True(sharing.ShareRentals); Assert.False(sharing.AllowTargetWrite);
                if (denied == "foreign-office-unshared")
                {
                    var update = JsonSerializer.Deserialize<UpsertDataSharingPolicyRequest>(JsonSerializer.Serialize(sharing))!;
                    update.ExpectedRevision = sharing.Revision; update.ShareRentals = false;
                    using var updated = await admin.PutAsJsonAsync($"tenant-settings/sharing-policies/{sharing.Id}", update);
                    updated.EnsureSuccessStatusCode();
                }
            }
            var original = await SeedRecords(admin, denied == "foreign-tenant" ? OfficeCodeCatalog.Itworld : otherOffice ? OfficeCodeCatalog.Yeonsu : OfficeCodeCatalog.Usenet, false);
            var api = Client(session, settings, 4); var store = new JsonSyncStateStore(session);
            var visible = await Coordinator(session, store, api).PullAsync();
            Assert.True(string.IsNullOrEmpty(visible.LastError), visible.LastError);
            if (denied is "foreign-office-unshared" or "foreign-tenant")
            {
                Assert.DoesNotContain(visible.SyncedRentalAssets, x => x.Id == original.Asset.Id);
                Assert.DoesNotContain(visible.SyncedRentalAssetAssignmentHistories, x => x.Id == original.History.Id);
                Assert.DoesNotContain(visible.SyncedRentalBillingLogs, x => x.Id == original.Log.Id);
            }
            if (denied == "foreign-office")
            {
                AssertRecordPrivacy(Assert.Single(visible.SyncedRentalAssets, x => x.Id == original.Asset.Id),
                    Assert.Single(visible.SyncedRentalAssetAssignmentHistories, x => x.Id == original.History.Id),
                    Assert.Single(visible.SyncedRentalBillingLogs, x => x.Id == original.Log.Id), "none", original);
            }
            var a = Copy(original.Asset); var h = Copy(original.History); var l = Copy(original.Log);
            Stamp(a); Stamp(h); Stamp(l); a.Notes = h.ChangeReason = l.Note = "거부되어야 하는 수정";
            if (denied == "stale") { a.ExpectedRevision++; h.ExpectedRevision++; l.ExpectedRevision++; }
            a.PurchasePrice = a.SalePrice = a.MonthlyFee = a.BlackOverageUnitPrice = a.ColorOverageUnitPrice = null; a.DepositText = null;
            h.MonthlyFee = l.BilledAmount = null;
            // Deliberately call the real API directly: a guessed foreign ID must be rejected by the server too.
            var request = new SyncPushRequest { DeviceId = "isolated-denied", RentalAssets = [a], RentalAssetAssignmentHistories = [h], RentalBillingLogs = [l] };
            if (denied == "no-edit")
            {
                var error = await Assert.ThrowsAsync<HttpRequestException>(() => api.PushAsync(request));
                Assert.Equal(System.Net.HttpStatusCode.Forbidden, error.StatusCode);
            }
            else
            {
                var result = await api.PushAsync(request); Assert.NotNull(result); Assert.Equal(0, result.AcceptedCount); Assert.Equal(3, result.ConflictCount);
                foreach (var conflict in result.Conflicts)
                foreach (var snapshot in new[] { conflict.ClientJson, conflict.ServerJson }.Where(x => !string.IsNullOrWhiteSpace(x)))
                {
                    using var json = JsonDocument.Parse(snapshot);
                    var financialNames = new[] { "PurchasePrice", "SalePrice", "MonthlyFee", "BlackOverageUnitPrice", "ColorOverageUnitPrice", "DepositText", "BilledAmount" };
                    foreach (var property in json.RootElement.EnumerateObject())
                        if (financialNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                            Assert.Equal(JsonValueKind.Null, property.Value.ValueKind);
                }
            }
            Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(await ReadRecords(admin, original)));
        }, denied == "no-edit" ? [] : null);
    }

    private static HttpClient InjectCapture(GeoraePlanApiClient api, CaptureHandler handler)
    {
        var field = typeof(GeoraePlanApiClient).GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ((HttpClient)field.GetValue(api)!).Dispose(); var http = new HttpClient(handler); field.SetValue(api, http); return http;
    }
    private static T Copy<T>(T row) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(row))!;
    private static void Stamp(SyncEntityDto row)
    { row.ExpectedRevision = row.Revision; row.MutationId = Guid.NewGuid().ToString("N"); row.MutationCreatedAtUtc = DateTime.UtcNow; row.UpdatedAtUtc = DateTime.UtcNow.AddSeconds(1); }
    private sealed record Records(RentalAssetDto Asset, RentalAssetAssignmentHistoryDto History, RentalBillingLogDto Log);
    private static void AssertRecordPrivacy(RentalAssetDto a, RentalAssetAssignmentHistoryDto h, RentalBillingLogDto l, string direction, Records expected)
    {
        Assert.Equal(direction == "purchase" ? expected.Asset.PurchasePrice : null, a.PurchasePrice);
        Assert.Equal(direction == "sales" ? expected.Asset.MonthlyFee : null, a.MonthlyFee);
        Assert.Equal(direction == "sales" ? expected.Asset.SalePrice : null, a.SalePrice);
        Assert.Equal(direction == "sales" ? expected.Asset.DepositText : null, a.DepositText);
        Assert.Equal(direction == "sales" ? expected.Asset.BlackOverageUnitPrice : null, a.BlackOverageUnitPrice);
        Assert.Equal(direction == "sales" ? expected.Asset.ColorOverageUnitPrice : null, a.ColorOverageUnitPrice);
        Assert.Equal(direction == "sales" ? expected.History.MonthlyFee : null, h.MonthlyFee);
        Assert.Equal(direction == "sales" ? expected.Log.BilledAmount : null, l.BilledAmount);
    }
    private static async Task<Records> SeedRecords(HttpClient admin, string office, bool zero)
    {
        var tenant = office == OfficeCodeCatalog.Itworld ? TenantScopeCatalog.Itworld : TenantScopeCatalog.UsenetGroup;
        var p = new RentalBillingProfileDto { Id = Guid.NewGuid(), ProfileKey = "records-" + Guid.NewGuid().ToString("N"),
            TenantCode = tenant, OfficeCode = office, ResponsibleOfficeCode = office, MonthlyAmount = zero ? 0 : 100000,
            CustomerName = "격리 렌탈 이력 거래처" };
        var a = new RentalAssetDto { Id = Guid.NewGuid(), ManagementNumber = "HTTP-" + Guid.NewGuid().ToString("N"),
            TenantCode = p.TenantCode, OfficeCode = office, ResponsibleOfficeCode = office, BillingProfileId = p.Id,
            PurchasePrice = zero ? 0 : 123456, SalePrice = zero ? 0 : 765432, MonthlyFee = zero ? 0 : 654321,
            DepositText = zero ? "0" : "123000", BlackOverageUnitPrice = zero ? 0 : 12.5m, ColorOverageUnitPrice = zero ? 0 : 45.6m,
            Notes = "기존 자산 비고" };
        var h = new RentalAssetAssignmentHistoryDto { Id = Guid.NewGuid(), AssetId = a.Id, BillingProfileId = p.Id,
            TenantCode = p.TenantCode, OfficeCode = office, ResponsibleOfficeCode = office, MonthlyFee = zero ? 0 : 234567,
            ChangeReason = "기존 설치 비고", IsCurrent = true, LinkedAtUtc = DateTime.UtcNow };
        var l = new RentalBillingLogDto { Id = Guid.NewGuid(), BillingProfileId = p.Id, BillingYearMonth = "2026-09", ScheduledDate = new(2026, 9, 25),
            TenantCode = p.TenantCode, OfficeCode = office, ResponsibleOfficeCode = office, BilledAmount = zero ? 0 : 345678,
            Status = "예정", Note = "기존 청구 비고" };
        foreach (var row in new SyncEntityDto[] { p, a, h, l }) Stamp(row);
        using var response = await admin.PostAsJsonAsync("sync/push", new SyncPushRequest { DeviceId = "isolated-record-seed",
            RentalBillingScheduleVersion = RentalBillingScheduleRules.ScheduleCapabilityVersion,
            RentalBillingProfiles = [p], RentalAssets = [a], RentalAssetAssignmentHistories = [h], RentalBillingLogs = [l] });
        response.EnsureSuccessStatusCode(); var result = (await response.Content.ReadFromJsonAsync<SyncPushResult>())!;
        Assert.True(result.ConflictCount == 0, string.Join(" | ", result.Conflicts.Select(x => x.Reason))); Assert.Equal(4, result.AcceptedCount);
        var persisted = await ReadRecords(admin, new(a, h, l));
        Assert.Equal(office, persisted.Asset.ResponsibleOfficeCode);
        Assert.Equal(office, persisted.History.ResponsibleOfficeCode);
        Assert.Equal(office, persisted.Log.ResponsibleOfficeCode);
        Assert.Equal(tenant, persisted.Asset.TenantCode); Assert.Equal(tenant, persisted.History.TenantCode); Assert.Equal(tenant, persisted.Log.TenantCode);
        return persisted;
    }
    private static async Task<Records> ReadRecords(HttpClient admin, Records ids)
    {
        var pull = (await admin.GetFromJsonAsync<SyncPullResponse>($"sync/pull?sinceRev=0&rentalBillingScheduleVersion={RentalBillingScheduleRules.ScheduleCapabilityVersion}"))!;
        return new(Assert.Single(pull.RentalAssets, x => x.Id == ids.Asset.Id),
            Assert.Single(pull.RentalAssetAssignmentHistories, x => x.Id == ids.History.Id),
            Assert.Single(pull.RentalBillingLogs, x => x.Id == ids.Log.Id));
    }
}
