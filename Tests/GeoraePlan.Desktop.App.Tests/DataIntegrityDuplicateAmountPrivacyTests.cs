using System.Text.Json;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed partial class DataIntegrityDuplicateAmountPrivacyTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task ScanAndReviewGuardAllPricesWithoutChangingMergeEvidence(bool sales, bool purchase, bool conflict)
    {
        await using var f = await Fixture.CreateAsync(conflict);
        var unrestricted = (await f.ScanAsync()).ItemDuplicateComparison!;
        f.Session.RefreshSession("restricted", User(f.UserId, sales, purchase));
        var issue = await f.ScanAsync();
        var scan = issue.ItemDuplicateComparison!;
        var review = (await f.Service.PrepareItemDuplicateReviewAsync(issue, f.Session)).Comparison;
        foreach (var comparison in new[] { scan, review })
        {
            Assert.Equal(unrestricted.SnapshotToken, comparison.SnapshotToken);
            Assert.Equal(unrestricted.BlockingConflictFields, comparison.BlockingConflictFields);
            Assert.Equal(!conflict, comparison.CanMerge);
            foreach (var candidate in comparison.Candidates)
            {
                var offset = conflict && candidate.ItemId == f.SecondId ? 1m : 0m;
                Assert.Equal<decimal?>(purchase ? 123456 + offset : null, candidate.PurchasePrice);
                Assert.Equal<decimal?>(sales ? 234567 + offset : null, candidate.SalePrice);
                Assert.Equal<decimal?>(sales ? 345678 + offset : null, candidate.RetailPrice);
                Assert.Equal<decimal?>(sales ? 456789 + offset : null, candidate.PriceGradeA);
                Assert.Equal<decimal?>(sales ? 567890 + offset : null, candidate.PriceGradeB);
                Assert.Equal<decimal?>(sales ? 678901 + offset : null, candidate.PriceGradeC);
                Assert.Contains("보관 창고", candidate.MasterDataSummary);
                Assert.Contains("보존 비고", candidate.AssetDataSummary);
                Assert.Equal(12, candidate.BoxQuantity);
                Assert.Equal(0, candidate.CurrentStock);
                Assert.Equal(3, candidate.SafetyStock);
            }
        }
        Assert.False(f.Db.ChangeTracker.HasChanges());
        Assert.Empty(await f.Db.SyncOutboxEntries.ToListAsync());
        f.Db.ChangeTracker.Clear();
        Assert.Equal(123456m, (await f.Db.Items.OrderBy(x => x.Id).FirstAsync()).PurchasePrice);
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("logout")]
    [InlineData("account")]
    public async Task CachedComparisonCannotRevealPriorAmountsAfterAccessChange(string action)
    {
        await using var f = await Fixture.CreateAsync(false);
        var issue = await f.ScanAsync();
        var review = await f.Service.PrepareItemDuplicateReviewAsync(issue, f.Session);
        Assert.Contains("123,456", review.Comparison.Candidates[0].MasterDataSummary);
        if (action == "revoke") f.Session.RefreshSession("revoke", User(f.UserId, false, false));
        if (action == "logout") f.Session.Clear();
        if (action == "account") f.Session.SetSession("other", User(Guid.NewGuid(), true, true));
        foreach (var comparison in new[] { issue.ItemDuplicateComparison!, review.Comparison })
        {
            foreach (var candidate in comparison.Candidates)
            {
                Assert.Null((decimal?)candidate.PurchasePrice);
                Assert.Null((decimal?)candidate.SalePrice);
                Assert.Contains("매입 비공개", candidate.MasterDataSummary);
                Assert.Contains("보존 비고", candidate.AssetDataSummary);
            }
            var json = JsonSerializer.Serialize(comparison);
            foreach (var amount in new[] { "123456", "234567", "345678", "456789", "567890", "678901", "123,456", "234,567" })
                Assert.DoesNotContain(amount, json);
        }
    }

    private static UserSessionDto User(Guid id, bool sales, bool purchase) => new()
    {
        UserId = id, Username = "duplicate-privacy", Role = DomainConstants.RoleUser,
        TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
        ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
        Permissions = new[] { AppPermissionNames.ItemEdit }
            .Concat(sales ? new[] { AppPermissionNames.AmountViewSales } : [])
            .Concat(purchase ? new[] { AppPermissionNames.AmountViewPurchase } : []).ToList()
    };

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AccountChangeDuringDatabaseReadCannotBindOldPricesToNewLogin(bool review, bool revokeOnly)
    {
        await using var f = await Fixture.CreateAsync(false);
        var issue = await f.ScanAsync();
        f.Interceptor.OnNextRead = () =>
        {
            if (revokeOnly) f.Session.RefreshSession("revoke", User(f.UserId, false, false));
            else f.Session.SetSession("other", User(Guid.NewGuid(), true, true));
        };
        var comparison = review ? (await f.Service.PrepareItemDuplicateReviewAsync(issue, f.Session)).Comparison
            : (await f.ScanAsync()).ItemDuplicateComparison!;
        Assert.Null(f.Interceptor.OnNextRead);
        Assert.All(comparison.Candidates, row => Assert.Null(row.PurchasePrice));
        Assert.All(comparison.Candidates, row => Assert.Null(row.SalePrice));
        Assert.DoesNotContain("123456", JsonSerializer.Serialize(comparison));
        Assert.False(f.Db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task AccessNotificationUpdatesVisibleFieldsOnceAndDetaches()
    {
        await using var f = await Fixture.CreateAsync(false);
        var comparison = (await f.ScanAsync()).ItemDuplicateComparison!;
        var changes = new List<string?>();
        comparison.Candidates[0].PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        comparison.AttachAmountAccess(); comparison.AttachAmountAccess(); changes.Clear();
        f.Session.RefreshSession("revoke", User(f.UserId, false, false));
        Assert.Equal(new[] { "PurchasePrice", "SalePrice", "RetailPrice", "PriceGradeA", "PriceGradeB", "PriceGradeC", "MasterDataSummary" }, changes);
        Assert.Contains("매입 비공개", comparison.Candidates[0].MasterDataSummary);
        comparison.DetachAmountAccess(); comparison.DetachAmountAccess(); changes.Clear();
        f.Session.Clear(); Assert.Empty(changes);
    }

    [Fact]
    public async Task RegrantRequiresFreshReadAndRejectedReviewCannotRebindCachedComparison()
    {
        await using var f = await Fixture.CreateAsync(false);
        var issue = await f.ScanAsync();
        f.Session.RefreshSession("revoke", User(f.UserId, false, false));
        f.Session.RefreshSession("regrant", User(f.UserId, true, true));
        Assert.All(issue.ItemDuplicateComparison!.Candidates, row => Assert.Null(row.PurchasePrice));
        var fresh = await f.ScanAsync();
        Assert.All(fresh.ItemDuplicateComparison!.Candidates, row => Assert.Equal<decimal?>(123456, row.PurchasePrice));
        var rejected = await f.Service.PrepareItemDuplicateReviewAsync(new DataIntegrityIssueDetail
        {
            Code = "invalid", ItemDuplicateComparison = issue.ItemDuplicateComparison
        }, f.Session);
        Assert.NotEmpty(rejected.PermissionBlockingReasons);
        Assert.Same(issue.ItemDuplicateComparison, rejected.Comparison);
        Assert.All(rejected.Comparison.Candidates, row => Assert.Null(row.PurchasePrice));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ZeroPriceRemainsKnownOnlyWithPermission(bool permitted)
    {
        await using var f = await Fixture.CreateAsync(false);
        foreach (var item in await f.Db.Items.ToListAsync()) item.PurchasePrice = item.SalePrice = 0;
        await f.Db.SaveChangesAsync();
        f.Session.RefreshSession("permissions", User(f.UserId, permitted, permitted));
        var comparison = (await f.ScanAsync()).ItemDuplicateComparison!;
        Assert.All(comparison.Candidates, row =>
        {
            Assert.Equal<decimal?>(permitted ? 0 : null, row.PurchasePrice);
            Assert.Equal<decimal?>(permitted ? 0 : null, row.SalePrice);
            Assert.Contains(permitted ? "매입 0" : "매입 비공개", row.MasterDataSummary);
        });
    }

    [Fact]
    public void UnboundOrLateBoundCandidateIsClosedAndCannotBeRebound()
    {
        var row = new DataIntegrityItemDuplicateCandidate { RawPurchasePrice = 123456, RawSalePrice = 234567 };
        Assert.Null(row.PurchasePrice); Assert.Null(row.SalePrice);
        var session = new SessionState(); session.SetSession("first", User(Guid.NewGuid(), true, true));
        var first = FinancialAmountVisibility.CaptureAccess(session);
        session.SetSession("second", User(Guid.NewGuid(), true, true));
        row.ProtectAmounts(session, first);
        row.ProtectAmounts(session, FinancialAmountVisibility.CaptureAccess(session));
        Assert.Null(row.PurchasePrice); Assert.Null(row.SalePrice);
        Assert.DoesNotContain("123456", JsonSerializer.Serialize(row));
    }

    private sealed class ReadInterceptor : DbCommandInterceptor
    {
        public Action? OnNextRead { get; set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            var action = OnNextRead; OnNextRead = null; action?.Invoke();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        public LocalDbContext Db { get; }
        public SessionState Session { get; } = new();
        public DataIntegrityIssueService Service { get; }
        public ReadInterceptor Interceptor { get; } = new();
        public Guid UserId { get; } = Guid.NewGuid();
        public Guid SecondId { get; } = Guid.Parse("22222222-2222-2222-2222-222222222222");
        private Fixture(SqliteConnection connection)
        {
            this.connection = connection;
            Db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).AddInterceptors(Interceptor).Options);
            Service = new DataIntegrityIssueService(Db);
            Session.SetSession("test", User(UserId, true, true));
        }
        public static async Task<Fixture> CreateAsync(bool conflict)
        {
            var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
            var f = new Fixture(connection); await f.Db.Database.EnsureCreatedAsync();
            f.Db.Items.AddRange(f.Item(Guid.Parse("11111111-1111-1111-1111-111111111111"), 0), f.Item(f.SecondId, conflict ? 1 : 0));
            await f.Db.SaveChangesAsync();
            return f;
        }
        private LocalItem Item(Guid id, decimal offset) => new()
        {
            Id = id, TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "검증 중복품목", NameMatchKey = "DUPLICATE", SpecificationOriginal = "규격",
            BoxQuantity = 12, SafetyStock = 3, StorageLocation = "창고", Notes = "보존 비고",
            PurchasePrice = 123456 + offset, SalePrice = 234567 + offset, RetailPrice = 345678 + offset,
            PriceGradeA = 456789 + offset, PriceGradeB = 567890 + offset, PriceGradeC = 678901 + offset,
            Revision = 9, IsDirty = false
        };
        public async Task<DataIntegrityIssueDetail> ScanAsync() => Assert.Single((await Service.ScanAsync(Session)).Issues,
            x => x.Code == DataIntegrityIssueCodes.ItemDuplicateCandidate);
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await connection.DisposeAsync(); }
    }
}
