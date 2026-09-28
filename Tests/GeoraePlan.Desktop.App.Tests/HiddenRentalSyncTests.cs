using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class HiddenRentalSyncTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pull_RentalPrivacyPreservesPendingEditsAndSurvivesReopen(bool dirty)
    {
        await using var f = await Fixture.Create(dirty);
        await f.Pull(hidden: true);
        await f.Reopen();
        await AssertRows(f.Db, dirty, hidden: true);

        // A subsequent disclosed response must not revive the old price of an
        // unacknowledged draft. Clean rows can accept the actual disclosed price.
        await f.Pull(hidden: false);
        await f.Reopen();
        await AssertRows(f.Db, dirty, hidden: dirty);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Pull_PendingAssetKeepsPurchaseAndSalesPrivacyIndependent(bool purchase, bool sales)
    {
        await using var f = await Fixture.Create(true);
        var dto = LocalMappings.ToDto(f.Asset);
        dto.PurchasePrice = purchase ? null : 900m;
        dto.DepositText = sales ? null : "server deposit";
        dto.Revision = 12;
        await f.Apply(new SyncPullResponse { RentalAssets = [dto] });
        await f.Reopen();
        var saved = await f.Db.RentalAssets.SingleAsync();
        Assert.Equal(purchase, saved.PurchaseAmountsHidden);
        Assert.Equal(sales, saved.SalesAmountsHidden);
        Assert.Equal(11, saved.Revision);
        Assert.Equal("pending asset", saved.Notes);
        Assert.Equal(123m, saved.PurchasePrice);
        Assert.Equal(456m, saved.MonthlyFee);
        var wire = LocalMappings.ToDto(saved);
        Assert.Equal(purchase ? null : (decimal?)123m, wire.PurchasePrice);
        Assert.Equal(sales ? null : (decimal?)456m, wire.MonthlyFee);
        Assert.Equal(sales ? null : "pending deposit", wire.DepositText);
    }

    [Fact]
    public async Task PendingPush_RentalScalarPayloadIsReplacedAfterHiddenPull()
    {
        await using var f = await Fixture.Create(true);
        await Assert.ThrowsAnyAsync<Exception>(() => f.Push());
        var original = f.Handler.Requests.Last();
        Assert.Equal(456m, Assert.Single(original.RentalAssets).MonthlyFee);
        Assert.Equal(456m, Assert.Single(original.RentalBillingProfiles).MonthlyAmount);
        await f.Pull(hidden: true);
        var count = f.Handler.Requests.Count;
        await Assert.ThrowsAnyAsync<Exception>(() => f.Push());
        Assert.True(f.Handler.Requests.Count > count);
        foreach (var request in f.Handler.Requests.Skip(count))
        {
            var asset = Assert.Single(request.RentalAssets);
            var profile = Assert.Single(request.RentalBillingProfiles);
            var history = Assert.Single(request.RentalAssetAssignmentHistories);
            var log = Assert.Single(request.RentalBillingLogs);
            Assert.Null(asset.PurchasePrice); Assert.Null(asset.SalePrice);
            Assert.Null(asset.MonthlyFee); Assert.Null(asset.DepositText);
            Assert.Null(asset.BlackOverageUnitPrice); Assert.Null(asset.ColorOverageUnitPrice);
            Assert.Null(profile.MonthlyAmount); Assert.Null(profile.DepositAmount);
            Assert.Null(profile.SettledAmount); Assert.Null(profile.OutstandingAmount);
            Assert.Null(history.MonthlyFee); Assert.Null(log.BilledAmount);
            using var template = JsonDocument.Parse(profile.BillingTemplateJson);
            Assert.Equal(JsonValueKind.Null, template.RootElement[0].GetProperty("UnitPrice").ValueKind);
            Assert.Equal(JsonValueKind.Null, template.RootElement[0].GetProperty("Amount").ValueKind);
            using var runs = JsonDocument.Parse(profile.BillingRunsJson);
            Assert.Equal(JsonValueKind.Null, runs.RootElement[0].GetProperty("BilledAmount").ValueKind);
            Assert.Equal(JsonValueKind.Null, runs.RootElement[0].GetProperty("SettledAmount").ValueKind);
            Assert.NotEqual(Assert.Single(original.RentalAssets).MutationId, asset.MutationId);
            Assert.NotEqual(Assert.Single(original.RentalBillingProfiles).MutationId, profile.MutationId);
            Assert.NotEqual(Assert.Single(original.RentalAssetAssignmentHistories).MutationId, history.MutationId);
            Assert.NotEqual(Assert.Single(original.RentalBillingLogs).MutationId, log.MutationId);
            Assert.Equal("pending asset", asset.Notes);
            Assert.Equal("pending profile", profile.Notes);
            Assert.Equal("pending history", history.ChangeReason);
            Assert.Equal("pending log", log.Note);
        }
        await f.Reopen();
        await AssertRows(f.Db, dirty: true, hidden: true);
    }

    private static async Task AssertRows(LocalDbContext db, bool dirty, bool hidden)
    {
        var asset = await db.RentalAssets.SingleAsync();
        var profile = await db.RentalBillingProfiles.SingleAsync();
        var history = await db.RentalAssetAssignmentHistories.SingleAsync();
        var log = await db.RentalBillingLogs.SingleAsync();
        Assert.Equal(hidden, asset.PurchaseAmountsHidden);
        Assert.Equal(hidden, asset.SalesAmountsHidden);
        Assert.Equal(hidden, profile.AmountsHidden);
        Assert.Equal(hidden, history.AmountsHidden);
        Assert.Equal(hidden, log.AmountsHidden);
        foreach (var row in new ILocalSyncEntity[] { asset, profile, history, log })
        {
            Assert.Equal(dirty, row.IsDirty);
            Assert.Equal(dirty ? 11 : 12, row.Revision);
        }
        Assert.Equal(dirty ? "pending asset" : "server asset", asset.Notes);
        Assert.Equal(dirty ? "pending profile" : "server profile", profile.Notes);
        Assert.Equal(dirty ? "pending history" : "server history", history.ChangeReason);
        Assert.Equal(dirty ? "pending log" : "server log", log.Note);
        Assert.Equal(TenantScopeCatalog.UsenetGroup, asset.TenantCode);
        Assert.Equal(OfficeCodeCatalog.Usenet, asset.ResponsibleOfficeCode);
        Assert.Equal(profile.Id, asset.BillingProfileId);
        Assert.Equal(asset.Id, history.AssetId);
        Assert.Equal(profile.Id, log.BillingProfileId);
        Assert.Equal(hidden ? null : (decimal?)999m, LocalMappings.ToDto(asset).MonthlyFee);
        Assert.Equal(hidden ? null : (decimal?)999m, LocalMappings.ToDto(profile).MonthlyAmount);
        Assert.Equal(hidden ? null : (decimal?)999m, LocalMappings.ToDto(history).MonthlyFee);
        Assert.Equal(hidden ? null : (decimal?)999m, LocalMappings.ToDto(log).BilledAmount);
        if (dirty)
        {
            Assert.Equal(456m, asset.MonthlyFee); Assert.Equal(123m, asset.PurchasePrice);
            Assert.Equal("pending deposit", asset.DepositText);
            Assert.Equal(456m, profile.MonthlyAmount); Assert.Equal(456m, history.MonthlyFee);
            Assert.Equal(456m, log.BilledAmount);
            Assert.Equal("[{\"Note\":\"pending template\",\"Quantity\":2,\"UnitPrice\":228,\"Amount\":456}]", profile.BillingTemplateJson);
            Assert.Equal("[{\"Note\":\"pending run\",\"Status\":\"예정\",\"BilledAmount\":456,\"SettledAmount\":100}]", profile.BillingRunsJson);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string? previousRoot = Environment.GetEnvironmentVariable("GEORAEPLAN_APP_ROOT");
        private DbContextOptions<LocalDbContext> options = null!;
        public LocalDbContext Db = null!;
        private SyncService sync = null!;
        private ErpApiClient api = null!;
        private SessionState session = null!;
        public LocalRentalAsset Asset = null!;
        public LocalRentalBillingProfile Profile = null!;
        public LocalRentalAssetAssignmentHistory History = null!;
        public LocalRentalBillingLog Log = null!;
        public CapturePushHandler Handler = new();

        public static async Task<Fixture> Create(bool dirty)
        {
            var f = new Fixture();
            var root = Path.Combine(Path.GetTempPath(), "georaeplan-hidden-rental-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", root);
            f.options = new DbContextOptionsBuilder<LocalDbContext>().UseSqlite("Data Source=" + Path.Combine(root, "isolated.db") + ";Pooling=False").Options;
            f.Db = new LocalDbContext(f.options);
            await f.Db.Database.EnsureCreatedAsync();
            f.Profile = new LocalRentalBillingProfile { Id = Guid.NewGuid(), ProfileKey = "privacy-profile", OfficeCode = OfficeCodeCatalog.Usenet,
                MonthlyAmount = 456, DepositAmount = 100, Notes = "pending profile", Revision = 11, IsDirty = dirty,
                BillingTemplateJson = "[{\"Note\":\"pending template\",\"Quantity\":2,\"UnitPrice\":228,\"Amount\":456}]", BillingRunsJson = "[{\"Note\":\"pending run\",\"Status\":\"예정\",\"BilledAmount\":456,\"SettledAmount\":100}]" };
            f.Asset = new LocalRentalAsset { Id = Guid.NewGuid(), AssetKey = "privacy-asset", ManagementNumber = "privacy-001", BillingProfileId = f.Profile.Id,
                OfficeCode = OfficeCodeCatalog.Usenet, PurchasePrice = 123, SalePrice = 789, MonthlyFee = 456, DepositText = "pending deposit",
                BlackOverageUnitPrice = 1, ColorOverageUnitPrice = 2, Notes = "pending asset", Revision = 11, IsDirty = dirty };
            f.History = new LocalRentalAssetAssignmentHistory { Id = Guid.NewGuid(), AssetId = f.Asset.Id, BillingProfileId = f.Profile.Id,
                MonthlyFee = 456, ChangeReason = "pending history", Revision = 11, IsDirty = dirty };
            f.Log = new LocalRentalBillingLog { Id = Guid.NewGuid(), BillingProfileId = f.Profile.Id, OfficeCode = OfficeCodeCatalog.Usenet,
                BillingYearMonth = "2026-09", BilledAmount = 456, Note = "pending log", Revision = 11, IsDirty = dirty };
            f.Db.AddRange(f.Profile, f.Asset, f.History, f.Log);
            await f.Db.SaveChangesAsync();
            f.session = new SessionState();
            f.session.SetOfflineSession(new UserSessionDto { UserId = Guid.NewGuid(), Username = "rental-sync", Role = DomainConstants.RoleAdmin,
                TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet, ScopeType = TenantScopeCatalog.ScopeAdmin });
            f.api = new ErpApiClient(new HttpClient(f.Handler) { BaseAddress = new Uri("http://localhost/") }, f.session);
            f.Connect();
            return f;
        }

        private void Connect()
        {
            var dispatcher = new SyncRequestDispatcher();
            var local = new LocalStateService(Db, new OfficeAccessService(), dispatcher, session);
            sync = new SyncService(Db, local, new RentalStateService(Db, local), api, session, dispatcher, new SyncDiagnosticsService(session));
        }

        public async Task Reopen()
        {
            sync.Dispose(); await Db.DisposeAsync();
            Db = new LocalDbContext(options); Connect();
        }

        public Task Pull(bool hidden)
        {
            var asset = LocalMappings.ToDto(Asset); var profile = LocalMappings.ToDto(Profile);
            var history = LocalMappings.ToDto(History); var log = LocalMappings.ToDto(Log);
            asset.PurchasePrice = hidden ? null : 900m;
            asset.SalePrice = hidden ? null : 789m;
            asset.MonthlyFee = hidden ? null : 999m; asset.DepositText = hidden ? null : "server deposit";
            profile.DepositAmount = hidden ? null : 100m;
            profile.SettledAmount = hidden ? null : 0m;
            profile.OutstandingAmount = hidden ? null : 999m;
            profile.MonthlyAmount = history.MonthlyFee = log.BilledAmount = hidden ? null : 999m;
            asset.Notes = "server asset"; profile.Notes = "server profile";
            history.ChangeReason = "server history"; log.Note = "server log";
            asset.Revision = profile.Revision = history.Revision = log.Revision = 12;
            return Apply(new SyncPullResponse { RentalAssets = [asset], RentalBillingProfiles = [profile], RentalAssetAssignmentHistories = [history], RentalBillingLogs = [log] });
        }

        public Task Apply(SyncPullResponse response) => (Task)typeof(SyncService).GetMethod("ApplyPullAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(sync, [response, 0L, CancellationToken.None, false])!;
        public Task Push() => (Task)typeof(SyncService).GetMethod("PushDirtyAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(sync, [api, session, true, CancellationToken.None])!;
        public async ValueTask DisposeAsync()
        {
            sync?.Dispose(); if (Db is not null) await Db.DisposeAsync();
            SqliteConnection.ClearAllPools(); Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", previousRoot);
        }
    }

    private sealed class CapturePushHandler : HttpMessageHandler
    {
        public List<SyncPushRequest> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/sync/push", request.RequestUri!.AbsolutePath);
            Requests.Add((await request.Content!.ReadFromJsonAsync<SyncPushRequest>(cancellationToken: cancellationToken))!);
            return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = JsonContent.Create(new { message = "isolated pending request" }) };
        }
    }
}
