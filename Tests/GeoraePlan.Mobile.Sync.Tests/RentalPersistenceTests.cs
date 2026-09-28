using System.Text.Json;
using GeoraePlan.Mobile.App.Models;
using GeoraePlan.Mobile.App.Services;
using Microsoft.Data.Sqlite;
using 거래플랜.Shared.Contracts;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace GeoraePlan.Mobile.Sync.Tests;

public sealed class RentalPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "georaeplan-mobile-sync-tests", Guid.NewGuid().ToString("N"));
    public RentalPersistenceTests()
    {
        Preferences.Default.Reset(); SecureStorage.Default.Reset(); MobileAppLogger.Messages.Clear();
        Directory.CreateDirectory(_root); FileSystem.AppDataDirectory = _root;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task HiddenPullAndPendingPayload_SurviveRealStorageReopenAndJsonFallback(bool pending, bool fallback)
    {
        var session = await Login(); var store = new JsonSyncStateStore(session);
        var pull = HiddenPull(); var state = await store.LoadAsync();
        if (pending)
        {
            state.PendingPush.RentalBillingProfiles = RoundTrip(pull.RentalBillingProfiles);
            state.PendingPush.RentalAssets = RoundTrip(pull.RentalAssets);
            state.PendingPush.RentalAssetAssignmentHistories = RoundTrip(pull.RentalAssetAssignmentHistories);
            state.PendingPush.RentalBillingLogs = RoundTrip(pull.RentalBillingLogs);
            await store.SaveAsync(state);
        }
        var api = new GeoraePlanApiClient(RoundTrip(pull));
        var coordinator = Coordinator(session, store, api);
        state = await coordinator.PullAsync();
        Assert.Equal("", state.LastError); Assert.Equal(12, state.LastRevision);
        var db = Path.Combine(_root, "sync-states", "mobile-sync-state.db");
        Assert.True(File.Exists(db));
        using (var connection = new SqliteConnection($"Data Source={db};Mode=ReadOnly"))
        {
            connection.Open(); using var cmd = connection.CreateCommand(); cmd.CommandText = "pragma quick_check";
            Assert.Equal("ok", cmd.ExecuteScalar());
        }
        if (fallback)
        {
            SqliteConnection.ClearAllPools();
            File.Move(db, db + ".preserved"); Directory.CreateDirectory(db);
        }
        var reopened = new JsonSyncStateStore(session);
        state = await reopened.LoadAsync();
        AssertHidden(state, pending);
        if (fallback) Assert.Contains(MobileAppLogger.Messages, x => x.Contains("JSON 백업 저장소로 전환"));
        if (pending)
        {
            var capture = new GeoraePlanApiClient();
            await Coordinator(session, reopened, capture).PushAsync();
            var sent = Assert.Single(capture.SubmittedPushes);
            Assert.Null(Assert.Single(sent.RentalAssets).MonthlyFee);
            Assert.Null(Assert.Single(sent.RentalBillingProfiles).MonthlyAmount);
            Assert.Null(Assert.Single(sent.RentalAssetAssignmentHistories).MonthlyFee);
            Assert.Null(Assert.Single(sent.RentalBillingLogs).BilledAmount);
        }
    }

    [Fact]
    public async Task HiddenPull_DoesNotResendOldKnownPendingRentalAmounts()
    {
        var session = await Login(); var store = new JsonSyncStateStore(session); var pull = HiddenPull();
        var state = await store.LoadAsync();
        var pending = RoundTrip(pull.RentalBillingProfiles[0]);
        pending.MonthlyAmount = 123456; pending.DepositAmount = 54321;
        pending.SettledAmount = 100; pending.OutstandingAmount = 123356;
        pending.BillingTemplateJson = "[{\"Quantity\":2,\"UnitPrice\":61728,\"Amount\":123456,\"Remark\":\"보존\"}]";
        pending.BillingRunsJson = "[{\"BilledAmount\":123456,\"SettledAmount\":100,\"Status\":\"예정\"}]";
        pending.MutationId = "previous-payload"; pending.Notes = "미전송 비고";
        state.PendingPush.RentalBillingProfiles = [pending]; await store.SaveAsync(state);
        await Coordinator(session, store, new(pull)).PullAsync();
        var capture = new GeoraePlanApiClient { BeforePushReturnAsync = () => throw new HttpRequestException("response lost") };
        await Coordinator(session, new(session), capture).PushAsync();
        var sent = Assert.Single(Assert.Single(capture.SubmittedPushes).RentalBillingProfiles);
        Assert.Null(sent.MonthlyAmount); Assert.Null(sent.DepositAmount); Assert.Null(sent.OutstandingAmount);
        Assert.Equal("미전송 비고", sent.Notes);
        Assert.NotEqual("previous-payload", sent.MutationId);
        using var json = JsonDocument.Parse(sent.BillingTemplateJson);
        Assert.Equal(2, json.RootElement[0].GetProperty("Quantity").GetInt32());
        Assert.Equal(JsonValueKind.Null, json.RootElement[0].GetProperty("UnitPrice").ValueKind);
        var reopened = await new JsonSyncStateStore(session).LoadAsync();
        Assert.Equal(sent.MutationId, Assert.Single(reopened.PendingPush.RentalBillingProfiles).MutationId);
        var retry = new GeoraePlanApiClient { BeforePushReturnAsync = () => throw new HttpRequestException("response lost again") };
        await Coordinator(session, new(session), retry).PushAsync();
        Assert.Equal(JsonSerializer.Serialize(sent), JsonSerializer.Serialize(Assert.Single(Assert.Single(retry.SubmittedPushes).RentalBillingProfiles)));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task PushWithoutPull_UsesCurrentDirectionalPermissions(bool sales, bool purchase)
    {
        var session = await Login(); var response = Response("rental-reader");
        if (sales) response.User.Permissions.Add("Amount.ViewSales");
        if (purchase) response.User.Permissions.Add("Amount.ViewPurchase");
        await session.SaveAsync(response);
        var store = new JsonSyncStateStore(session); var state = await store.LoadAsync();
        var asset = HiddenPull().RentalAssets[0];
        asset.PurchasePrice = 101; asset.SalePrice = 202; asset.MonthlyFee = 303;
        asset.BlackOverageUnitPrice = 4; asset.ColorOverageUnitPrice = 5; asset.DepositText = "606";
        state.PendingPush.RentalAssets = [asset]; await store.SaveAsync(state);
        var api = new GeoraePlanApiClient(); await Coordinator(session, store, api).PushAsync();
        var sent = Assert.Single(Assert.Single(api.SubmittedPushes).RentalAssets);
        Assert.Equal(purchase ? 101m : null, sent.PurchasePrice);
        Assert.Equal(sales ? 303m : null, sent.MonthlyFee);
        Assert.Equal(sales ? 202m : null, sent.SalePrice);
        Assert.Equal(sales ? "606" : null, sent.DepositText);
        Assert.Equal(sales ? 4m : null, sent.BlackOverageUnitPrice);
        Assert.Equal(sales ? 5m : null, sent.ColorOverageUnitPrice);
    }

    [Fact]
    public async Task HiddenSyncedRow_OverridesGrantedPermission_AndPreservesNestedNonMoney()
    {
        var session = await Login(); var response = Response("rental-reader");
        response.User.Permissions.Add("Amount.ViewSales"); await session.SaveAsync(response);
        var store = new JsonSyncStateStore(session); var state = await store.LoadAsync(); var pull = HiddenPull();
        var pending = RoundTrip(pull.RentalBillingProfiles[0]); pending.MonthlyAmount = 99;
        pending.DepositAmount = pending.SettledAmount = pending.OutstandingAmount = 0;
        pending.BillingRunsJson = "[{\"billedAmount\":99,\"settledAmount\":0,\"Status\":\"예정\",\"items\":[{\"unitPrice\":33,\"amount\":99,\"Quantity\":3,\"Remark\":\"보존\",\"Code\":\"A\"}]}]";
        state.PendingPush.RentalBillingProfiles = [pending]; await store.SaveAsync(state);
        await Coordinator(session, store, new(pull)).PullAsync();
        var api = new GeoraePlanApiClient(); await Coordinator(session, new(session), api).PushAsync();
        var sent = Assert.Single(Assert.Single(api.SubmittedPushes).RentalBillingProfiles);
        Assert.Null(sent.MonthlyAmount);
        using var json = JsonDocument.Parse(sent.BillingRunsJson);
        var run = json.RootElement[0]; Assert.Equal("예정", run.GetProperty("Status").GetString());
        Assert.Equal(JsonValueKind.Null, run.GetProperty("BilledAmount").ValueKind);
        var item = run.GetProperty("items")[0]; Assert.Equal(3, item.GetProperty("Quantity").GetInt32());
        Assert.Equal("보존", item.GetProperty("Remark").GetString()); Assert.Equal("A", item.GetProperty("Code").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("UnitPrice").ValueKind);
        Assert.False(item.TryGetProperty("unitPrice", out _));
    }

    [Fact]
    public async Task MalformedPendingJson_PreservesOriginalAndCursor_AndDoesNotSend()
    {
        var session = await Login(); var store = new JsonSyncStateStore(session); var state = await store.LoadAsync();
        var pending = HiddenPull().RentalBillingProfiles[0]; pending.MonthlyAmount = 321;
        pending.BillingRunsJson = "invalid-original"; pending.MutationId = "unchanged";
        state.PendingPush.RentalBillingProfiles = [pending]; await store.SaveAsync(state);
        var pulled = await Coordinator(session, store, new(HiddenPull())).PullAsync();
        Assert.NotEmpty(pulled.LastError); Assert.Equal(0, pulled.LastRevision);
        var api = new GeoraePlanApiClient(); var pushed = await Coordinator(session, new(session), api).PushAsync();
        Assert.NotEmpty(pushed.LastError); Assert.Empty(api.SubmittedPushes);
        var persisted = await new JsonSyncStateStore(session).LoadAsync();
        Assert.Equal(0, persisted.LastRevision); Assert.Empty(persisted.SyncedRentalBillingProfiles);
        Assert.Equal(JsonSerializer.Serialize(pending), JsonSerializer.Serialize(Assert.Single(persisted.PendingPush.RentalBillingProfiles)));
    }

    [Fact]
    public async Task DifferentOwnerAndStaleCompletion_CannotReadOrReplacePriorOwnerState()
    {
        var session = await Login(); var originalOwner = session.CaptureOwner();
        var store = new JsonSyncStateStore(session); var pull = HiddenPull();
        await Coordinator(session, store, new(pull)).PullAsync();
        await session.SaveAsync(Response("another-reader"));
        Assert.Empty((await new JsonSyncStateStore(session).LoadAsync()).SyncedRentalAssets);
        await Assert.ThrowsAsync<StaleMobileSessionOwnerException>(() => store.SaveAsync(originalOwner, new MobileSyncState()));
        await session.SaveAsync(Response("rental-reader"));
        AssertHidden(await new JsonSyncStateStore(session).LoadAsync(), false);
    }

    [Fact]
    public async Task OwnerChangeDuringPull_DiscardsResponseWithoutOverwritingEitherOwner()
    {
        var session = await Login(); var store = new JsonSyncStateStore(session);
        await Coordinator(session, store, new(HiddenPull())).PullAsync();
        var late = HiddenPull(); late.CurrentServerRevision = 99;
        var api = new GeoraePlanApiClient(late) { BeforePullReturnAsync = () => session.SaveAsync(Response("another-reader")) };
        await Assert.ThrowsAsync<StaleMobileSessionOwnerException>(() => Coordinator(session, store, api).PullAsync());
        Assert.Empty((await new JsonSyncStateStore(session).LoadAsync()).SyncedRentalAssets);
        await session.SaveAsync(Response("rental-reader"));
        AssertHidden(await new JsonSyncStateStore(session).LoadAsync(), false);
    }

    [Fact]
    public async Task RevokedPermissionWithoutPull_RedactsPreviouslyKnownPendingMoney()
    {
        var session = await Login(); var response = Response("rental-reader");
        response.User.Permissions.Add("Amount.ViewSales"); await session.SaveAsync(response);
        var store = new JsonSyncStateStore(session); var state = await store.LoadAsync();
        var profile = HiddenPull().RentalBillingProfiles[0]; profile.MonthlyAmount = 123;
        profile.Notes = "수정할 비고"; state.PendingPush.RentalBillingProfiles = [profile]; await store.SaveAsync(state);
        await session.SaveAsync(Response("rental-reader"));
        var api = new GeoraePlanApiClient(); await Coordinator(session, new(session), api).PushAsync();
        var sent = Assert.Single(Assert.Single(api.SubmittedPushes).RentalBillingProfiles);
        Assert.Null(sent.MonthlyAmount); Assert.Equal("수정할 비고", sent.Notes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingHistoryAndLog_RedactKnownMoney_WithoutChangingRevision(bool grantedWithHiddenPull)
    {
        var session = await Login();
        if (grantedWithHiddenPull)
        {
            var response = Response("rental-reader"); response.User.Permissions.Add("Amount.ViewSales");
            await session.SaveAsync(response);
        }
        var store = new JsonSyncStateStore(session); var state = await store.LoadAsync(); var pull = HiddenPull();
        var history = RoundTrip(pull.RentalAssetAssignmentHistories[0]); history.MonthlyFee = 456;
        var log = RoundTrip(pull.RentalBillingLogs[0]); log.BilledAmount = 789;
        history.MutationId = "history-original"; log.MutationId = "log-original";
        state.PendingPush.RentalAssetAssignmentHistories = [history]; state.PendingPush.RentalBillingLogs = [log];
        await store.SaveAsync(state);
        if (grantedWithHiddenPull) await Coordinator(session, store, new(pull)).PullAsync();
        var api = new GeoraePlanApiClient(); await Coordinator(session, new(session), api).PushAsync();
        var sent = Assert.Single(api.SubmittedPushes);
        var sentHistory = Assert.Single(sent.RentalAssetAssignmentHistories);
        var sentLog = Assert.Single(sent.RentalBillingLogs);
        Assert.Null(sentHistory.MonthlyFee); Assert.Null(sentLog.BilledAmount);
        Assert.Equal(history.Revision, sentHistory.Revision); Assert.Equal(log.Revision, sentLog.Revision);
        Assert.NotEqual(history.MutationId, sentHistory.MutationId); Assert.NotEqual(log.MutationId, sentLog.MutationId);
    }

    private static void AssertHidden(MobileSyncState state, bool pending)
    {
        Assert.Equal(12, state.LastRevision);
        var profile = Assert.Single(state.SyncedRentalBillingProfiles);
        Assert.Null(profile.MonthlyAmount); Assert.Null(profile.DepositAmount);
        Assert.Null(profile.SettledAmount); Assert.Null(profile.OutstandingAmount);
        var asset = Assert.Single(state.SyncedRentalAssets);
        Assert.Null(asset.MonthlyFee); Assert.Null(asset.PurchasePrice); Assert.Null(asset.DepositText);
        Assert.Null(Assert.Single(state.SyncedRentalAssetAssignmentHistories).MonthlyFee);
        Assert.Null(Assert.Single(state.SyncedRentalBillingLogs).BilledAmount);
        Assert.Equal("보존할 비고", profile.Notes);
        using var json = JsonDocument.Parse(profile.BillingRunsJson);
        Assert.Equal(JsonValueKind.Null, json.RootElement[0].GetProperty("BilledAmount").ValueKind);
        if (pending) Assert.Null(Assert.Single(state.PendingPush.RentalBillingProfiles).MonthlyAmount);
    }

    private static SyncCoordinator Coordinator(SessionStore session, JsonSyncStateStore store, GeoraePlanApiClient api)
        => new(store, api, new PaymentAttachmentDraftStore(), new CustomerContractCacheStore(session), session);
    private static async Task<SessionStore> Login()
    { var session = new SessionStore(); await session.SaveAsync(Response("rental-reader")); return session; }
    private static LoginResponse Response(string username) => new()
    {
        Token = "synthetic-only", ExpiresAtUtc = DateTime.UtcNow.AddHours(1), User = new()
        { Username = username, Role = "User", TenantCode = "USENET_GROUP", OfficeCode = "USENET", ScopeType = "OfficeOnly",
            Permissions = ["Rental.ProfileEdit", "Rental.AssetEdit"] }
    };
    private static T RoundTrip<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    private static SyncPullResponse HiddenPull()
    {
        var profile = new RentalBillingProfileDto { Id = Guid.NewGuid(), MonthlyAmount = null, DepositAmount = null,
            SettledAmount = null, OutstandingAmount = null, Notes = "보존할 비고", Revision = 12,
            BillingRunsJson = "[{\"BilledAmount\":null,\"SettledAmount\":null,\"Status\":\"예정\"}]" };
        var asset = new RentalAssetDto { Id = Guid.NewGuid(), BillingProfileId = profile.Id,
            PurchasePrice = null, SalePrice = null, MonthlyFee = null, DepositText = null, Revision = 12 };
        return new() { CurrentServerRevision = 12, RentalBillingProfiles = [profile], RentalAssets = [asset],
            RentalAssetAssignmentHistories = [new() { Id = Guid.NewGuid(), AssetId = asset.Id, MonthlyFee = null, Revision = 12 }],
            RentalBillingLogs = [new() { Id = Guid.NewGuid(), BillingProfileId = profile.Id, BilledAmount = null, Revision = 12 }] };
    }
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "georaeplan-mobile-sync-tests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(_root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
        Directory.Delete(_root, true);
    }
}
