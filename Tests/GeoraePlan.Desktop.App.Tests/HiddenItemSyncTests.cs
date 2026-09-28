using System.Net.Http;
using System.Net;
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

public sealed class HiddenItemSyncTests
{
    [Theory]
    [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task PendingPush_AfterHiddenPull_ReplacesOldMoneyPayloadWithoutLosingMemo(bool purchase, bool sales)
    {
        await using var f = await Fixture.Create(true);
        await Assert.ThrowsAnyAsync<Exception>(() => f.Push());
        var before = f.Handler.Requests.Last();
        Assert.Equal(701m, before.Items.Single().PurchasePrice);
        Assert.Equal(801m, before.ItemPriceGrades.Single().UnitPrice);
        var remote = LocalMappings.ToDto(f.Item);
        if (purchase) remote.PurchasePrice = null;
        if (sales) remote.SalePrice = remote.RetailPrice = remote.PriceGradeA = remote.PriceGradeB = remote.PriceGradeC = null;
        var grade = LocalMappings.ToDto(f.Grade); if (sales) grade.UnitPrice = null;
        await f.Pull(remote, grade);
        var previousCount = f.Handler.Requests.Count;
        await Assert.ThrowsAnyAsync<Exception>(() => f.Push());
        Assert.True(f.Handler.Requests.Count > previousCount);
        foreach (var request in f.Handler.Requests.Skip(previousCount))
        {
            var item = request.Items.Single(); var price = request.ItemPriceGrades.Single();
            Assert.Equal(purchase ? null : (decimal?)701m, item.PurchasePrice);
            Assert.Equal(sales ? null : (decimal?)1101m, item.SalePrice);
            Assert.Equal(sales ? null : (decimal?)801m, price.UnitPrice);
            Assert.Equal("pending memo", item.SimpleMemo);
            Assert.NotEqual(before.Items.Single().MutationId, item.MutationId);
            if (sales) Assert.NotEqual(before.ItemPriceGrades.Single().MutationId, price.MutationId);
        }
        f.Db.ChangeTracker.Clear();
        var saved = await f.Db.Items.SingleAsync(); Assert.True(saved.IsDirty); Assert.Equal("pending memo", saved.SimpleMemo);
    }

    [Theory]
    [InlineData(true, true, false)] [InlineData(true, false, true)] [InlineData(true, true, true)]
    [InlineData(false, true, false)] [InlineData(false, false, true)] [InlineData(false, true, true)]
    public async Task Pull_ClosesDisclosureWithoutDiscardingPendingItemAndGrade(bool dirty, bool purchase, bool sales)
    {
        await using var f = await Fixture.Create(dirty);
        var remote = LocalMappings.ToDto(f.Item);
        remote.SimpleMemo = "server memo"; remote.CurrentStock = 99; remote.Revision = 12;
        if (purchase) remote.PurchasePrice = null;
        if (sales) remote.SalePrice = remote.RetailPrice = remote.PriceGradeA = remote.PriceGradeB = remote.PriceGradeC = null;
        var grade = LocalMappings.ToDto(f.Grade); grade.Revision = 12; grade.PriceGradeName = "server grade";
        if (sales) grade.UnitPrice = null;
        await f.Pull(remote, grade);
        f.Db.ChangeTracker.Clear();
        var saved = await f.Db.Items.SingleAsync(); var savedGrade = await f.Db.ItemPriceGrades.SingleAsync();
        Assert.Equal(purchase, saved.PurchaseAmountsHidden); Assert.Equal(sales, saved.SalesAmountsHidden);
        Assert.Equal(sales, savedGrade.AmountsHidden);
        Assert.Equal(dirty, saved.IsDirty); Assert.Equal(dirty, savedGrade.IsDirty);
        Assert.Equal(dirty ? "pending memo" : "server memo", saved.SimpleMemo);
        Assert.Equal(dirty ? "pending grade" : "server grade", savedGrade.PriceGradeName);
        Assert.Equal(dirty ? 11 : 12, saved.Revision); Assert.Equal(dirty ? 11 : 12, savedGrade.Revision);
        Assert.Equal(4m, saved.CurrentStock);
        Assert.Equal(4m, (await f.Db.ItemWarehouseStocks.SingleAsync()).Quantity);
        Assert.Equal(purchase ? null : (decimal?)701m, LocalMappings.ToDto(saved).PurchasePrice);
        Assert.Equal(sales ? null : (decimal?)1101m, LocalMappings.ToDto(saved).SalePrice);
        Assert.Equal(sales ? null : (decimal?)801m, LocalMappings.ToDto(savedGrade).UnitPrice);
        Assert.Empty(await f.Db.SyncOutboxEntries.ToListAsync());

        if (dirty)
        {
            // Pending monetary edits must not be silently replaced with a response's
            // prices or become visible again merely because a later response discloses them.
            Assert.Equal(701m, saved.PurchasePrice); Assert.Equal(1101m, saved.SalePrice); Assert.Equal(801m, savedGrade.UnitPrice);
            remote.PurchasePrice = remote.SalePrice = remote.RetailPrice = remote.PriceGradeA = remote.PriceGradeB = remote.PriceGradeC = 9999;
            grade.UnitPrice = 9999;
            await f.Pull(remote, grade);
            f.Db.ChangeTracker.Clear(); saved = await f.Db.Items.SingleAsync(); savedGrade = await f.Db.ItemPriceGrades.SingleAsync();
            Assert.Equal(purchase, saved.PurchaseAmountsHidden); Assert.Equal(sales, saved.SalesAmountsHidden);
            Assert.Equal(sales, savedGrade.AmountsHidden); Assert.Equal("pending memo", saved.SimpleMemo);
            Assert.True(saved.IsDirty); Assert.Equal(11, saved.Revision);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CanonicalConflict_WithHiddenPrices_DoesNotDiscardDifferentPendingMemo(bool differentMemo)
    {
        await using var f = await Fixture.Create(true);
        f.Item.PurchaseAmountsHidden = f.Item.SalesAmountsHidden = true;
        await f.Db.SaveChangesAsync();
        var remote = LocalMappings.ToDto(f.Item); remote.Revision = 12;
        if (differentMemo) remote.SimpleMemo = "server memo";
        var conflict = new ConflictLogDto
        {
            EntityName = "Item", EntityId = f.Item.Id.ToString(),
            Reason = "Expected revision mismatch. client=11, server=12",
            ServerJson = JsonSerializer.Serialize(remote)
        };
        var applied = await (Task<bool>)typeof(SyncService).GetMethod("TryApplyServerItemConflictSnapshotAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(f.Sync, [conflict, CancellationToken.None])!;
        Assert.Equal(!differentMemo, applied);
        f.Db.ChangeTracker.Clear(); var saved = await f.Db.Items.SingleAsync();
        Assert.Equal("pending memo", saved.SimpleMemo);
        Assert.Equal(differentMemo, saved.IsDirty);
        Assert.True(saved.PurchaseAmountsHidden); Assert.True(saved.SalesAmountsHidden);
        Assert.Null(LocalMappings.ToDto(saved).SalePrice);
    }

    [Fact]
    public void CanonicalComparison_DistinguishesKnownZeroFromUnknownAndIgnoresHiddenStorageValues()
    {
        var item = new LocalItem { Id = Guid.NewGuid(), SalesAmountsHidden = true, SalePrice = 98765 };
        var remote = LocalMappings.ToDto(item);
        var compare = typeof(SyncService).GetMethod("AreEquivalentItemSnapshots", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.True((bool)compare.Invoke(null, [item, remote])!);
        item.SalePrice = 0;
        Assert.True((bool)compare.Invoke(null, [item, remote])!);
        item.SalesAmountsHidden = false;
        Assert.False((bool)compare.Invoke(null, [item, remote])!);
        item.SalesAmountsHidden = true; item.SimpleMemo = "pending memo";
        Assert.False((bool)compare.Invoke(null, [item, remote])!);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string? _previousRoot = Environment.GetEnvironmentVariable("GEORAEPLAN_APP_ROOT");
        public LocalDbContext Db = null!;
        public SyncService Sync = null!;
        public LocalItem Item = null!;
        public LocalItemPriceGrade Grade = null!;
        public CapturePushHandler Handler = new();
        public ErpApiClient Api = null!;
        public SessionState Session = null!;
        public static async Task<Fixture> Create(bool dirty)
        {
            var f = new Fixture();
            var root = Path.Combine(Path.GetTempPath(), "georaeplan-hidden-item-sync-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root); Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", root);
            f.Db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite("Data Source=" + Path.Combine(root, "isolated.db") + ";Pooling=False").Options);
            await f.Db.Database.EnsureCreatedAsync();
            f.Item = new LocalItem { Id = Guid.NewGuid(), NameOriginal = "hidden item", NameMatchKey = "HIDDENITEM", TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet, SimpleMemo = "pending memo", CurrentStock = 4, PurchasePrice = 701, SalePrice = 1101, RetailPrice = 1201, PriceGradeA = 1001, PriceGradeB = 901, PriceGradeC = 801, Revision = 11, IsDirty = dirty };
            var option = new LocalPriceGradeOption { Id = Guid.NewGuid(), Name = "grade", IsActive = true };
            f.Grade = new LocalItemPriceGrade { Id = Guid.NewGuid(), ItemId = f.Item.Id, PriceGradeOptionId = option.Id, PriceGradeName = "pending grade", UnitPrice = 801, Revision = 11, IsDirty = dirty };
            f.Db.Items.Add(f.Item); f.Db.PriceGradeOptions.Add(option); f.Db.ItemPriceGrades.Add(f.Grade);
            f.Db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock { ItemId = f.Item.Id, WarehouseCode = DomainConstants.WarehouseUsenetMain, Quantity = 4 });
            await f.Db.SaveChangesAsync();
            var session = new SessionState(); session.SetOfflineSession(new UserSessionDto { UserId = Guid.NewGuid(), Username = "hidden-sync", Role = DomainConstants.RoleAdmin, TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet, ScopeType = TenantScopeCatalog.ScopeAdmin });
            var dispatcher = new SyncRequestDispatcher(); var local = new LocalStateService(f.Db, new OfficeAccessService(), dispatcher, session);
            f.Session = session;
            f.Api = new ErpApiClient(new HttpClient(f.Handler) { BaseAddress = new Uri("http://localhost/") }, session);
            f.Sync = new SyncService(f.Db, local, new RentalStateService(f.Db, local), f.Api, session, dispatcher, new SyncDiagnosticsService(session));
            return f;
        }
        public Task Push() => (Task)typeof(SyncService).GetMethod("PushDirtyAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Sync, [Api, Session, true, CancellationToken.None])!;
        public Task Pull(ItemDto item, ItemPriceGradeDto grade)
            => (Task)typeof(SyncService).GetMethod("ApplyPullAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Sync, [new SyncPullResponse
            {
                CurrentServerRevision = 12, Items = [item], ItemPriceGrades = [grade],
                ItemWarehouseStocks = [new ItemWarehouseStockDto { ItemId = item.Id, WarehouseCode = DomainConstants.WarehouseUsenetMain, Quantity = 4, Revision = 12 }]
            }, 0L, CancellationToken.None, false])!;
        public async ValueTask DisposeAsync()
        {
            Sync?.Dispose(); if (Db is not null) await Db.DisposeAsync();
            SqliteConnection.ClearAllPools(); Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", _previousRoot);
        }
    }

    private sealed class CapturePushHandler : HttpMessageHandler
    {
        public List<SyncPushRequest> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/sync/push", request.RequestUri!.AbsolutePath);
            Requests.Add((await request.Content!.ReadFromJsonAsync<SyncPushRequest>(cancellationToken: cancellationToken))!);
            // Leave the real prepared mutation/outbox pending, then exercise its next retry.
            return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = JsonContent.Create(new { message = "isolated pending request" }) };
        }
    }
}
