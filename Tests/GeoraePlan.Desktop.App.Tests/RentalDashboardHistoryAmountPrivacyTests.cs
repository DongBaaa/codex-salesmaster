using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalDashboardHistoryAmountPrivacyTests
{
    public static IEnumerable<object[]> AccessCases()
    {
        foreach (var role in new[] { "none", "purchase", "sales", "both", "admin", "god" })
        foreach (var hidden in new[] { false, true })
        foreach (var amount in new[] { 0, 23456 }) yield return [role, hidden, amount];
    }

    [Theory, MemberData(nameof(AccessCases))]
    public async Task DashboardAndHistoryRespectPermissionAndStoredPrivacyWithoutChangingCache(string role, bool hidden, int amount)
    {
        await using var db = await Database();
        var (asset, history, profile) = await Seed(db, hidden, amount);
        var session = Session(role);
        var service = new RentalStateService(db);
        var visible = !hidden && role is "sales" or "both" or "admin" or "god";
        var summary = await service.GetDashboardSummaryAsync(session, new(2026, 9, 25));
        var alert = Assert.Single(summary.AlertItems);
        Assert.Equal(visible ? (decimal?)amount : null, (decimal?)alert.MonthlyAmount);
        Assert.Equal(profile.Id, alert.BillingProfileId);
        Assert.Equal(1, summary.DueTodayCount);
        var row = Assert.Single(await service.GetAssetAssignmentHistoriesAsync(asset.Id, session));
        Assert.Equal(visible ? (decimal?)amount : null, (decimal?)row.MonthlyFee);
        Assert.Equal(history.Id, row.HistoryId);
        var edit = await service.CreateAssetAssignmentHistoryEditRequestAsync(asset.Id, session, history.Id);
        Assert.NotNull(edit);
        Assert.Equal(visible ? (decimal?)amount : null, (decimal?)edit.MonthlyFee);
        Assert.All(db.ChangeTracker.Entries(), e => Assert.Equal(EntityState.Unchanged, e.State));
        await db.Entry(history).ReloadAsync(); await db.Entry(profile).ReloadAsync();
        Assert.Equal(amount, history.MonthlyFee); Assert.Equal(hidden, history.AmountsHidden);
        Assert.Equal(amount, profile.MonthlyAmount); Assert.False(history.IsDirty); Assert.False(profile.IsDirty);
    }

    [Theory, MemberData(nameof(AccessCases))]
    public async Task ExistingHistoryNoteSavePreservesHiddenMoneyAndHistoricalLinksAndUnknownDates(string role, bool hidden, int amount)
    {
        await using var db = await Database();
        var (asset, history, _) = await Seed(db, hidden, amount);
        var oldCustomer = history.CustomerId; var oldProfile = history.BillingProfileId;
        var session = Session(role); var service = new RentalStateService(db);
        var edit = await service.CreateAssetAssignmentHistoryEditRequestAsync(asset.Id, session, history.Id);
        Assert.NotNull(edit);
        edit.MonthlyFee = 999m; edit.ChangeReason = "과거 이력 비고 수정";
        var result = await service.SaveAssetAssignmentHistoryAsync(edit, session);
        Assert.True(result.Success, result.Message);
        db.ChangeTracker.Clear(); history = await db.RentalAssetAssignmentHistories.SingleAsync();
        var visible = !hidden && role is "sales" or "both" or "admin" or "god";
        Assert.Equal(visible ? 999m : amount, history.MonthlyFee);
        Assert.Equal(!visible, history.AmountsHidden);
        Assert.Equal(oldCustomer, history.CustomerId); Assert.Equal(oldProfile, history.BillingProfileId);
        Assert.Null(history.ContractStartDate); Assert.Null(history.ContractEndDate);
        Assert.Equal("과거 이력 비고 수정", history.ChangeReason); Assert.True(history.IsDirty);
    }

    [Theory]
    [InlineData("none", false)]
    [InlineData("purchase", false)]
    [InlineData("sales", true)]
    [InlineData("admin", true)]
    public async Task NewHistoricalRecordWithoutDisclosedContractBasisCannotCreateZeroPricedHistory(string role, bool hidden)
    {
        await using var db = await Database();
        var (asset, _, _) = await Seed(db, hidden, 0);
        var session = Session(role); var service = new RentalStateService(db);
        var request = await service.CreateAssetAssignmentHistoryEditRequestAsync(asset.Id, session);
        Assert.NotNull(request); Assert.Null((decimal?)request.MonthlyFee);
        request.MonthlyFee = 999m;
        var result = await service.SaveAssetAssignmentHistoryAsync(request, session);
        Assert.False(result.Success); Assert.Equal(1, await db.RentalAssetAssignmentHistories.CountAsync());
        Assert.All(db.ChangeTracker.Entries(), e => Assert.Equal(EntityState.Unchanged, e.State));
    }

    [Fact]
    public async Task DashboardUnknownTemplateAmountDoesNotPublishKnownScalarOrFalseFeeMismatch()
    {
        await using var db = await Database();
        var (asset, _, profile) = await Seed(db, false, 12345);
        profile.BillingTemplateJson = "[{\"DisplayItemName\":\"장비\",\"Quantity\":1,\"UnitPrice\":null,\"Amount\":null}]";
        asset.SalesAmountsHidden = true; asset.MonthlyFee = 0m;
        await db.SaveChangesAsync();
        var summary = await new RentalStateService(db).GetDashboardSummaryAsync(Session("admin"), new(2026, 9, 25));
        Assert.Null((decimal?)Assert.Single(summary.AlertItems).MonthlyAmount);
        Assert.All(summary.UnresolvedLinkItems, x => Assert.DoesNotContain("월요금", x.ReviewNote));
    }

    [Fact]
    public async Task PermissionRevocationAndRestorationReevaluateCachedHistoryAndDashboard()
    {
        await using var db = await Database(); var (asset, _, _) = await Seed(db, false, 12345);
        var service = new RentalStateService(db);
        foreach (var role in new[] { "sales", "none", "purchase", "sales" })
        {
            var session = Session(role);
            var summary = await service.GetDashboardSummaryAsync(session, new(2026, 9, 25));
            var history = Assert.Single(await service.GetAssetAssignmentHistoriesAsync(asset.Id, session));
            Assert.Equal(role == "sales" ? 12345m : (decimal?)null, (decimal?)Assert.Single(summary.AlertItems).MonthlyAmount);
            Assert.Equal(role == "sales" ? 12345m : (decimal?)null, (decimal?)history.MonthlyFee);
        }
    }

    private static async Task<LocalDbContext> Database()
    {
        var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite("Data Source=:memory:").Options);
        await db.Database.OpenConnectionAsync(); await db.Database.EnsureCreatedAsync(); return db;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KnownHistoryAcceptsRealZeroButRejectsMissingMoney(bool missing)
    {
        await using var db = await Database(); var (asset, history, _) = await Seed(db, false, 12345);
        var service = new RentalStateService(db); var session = Session("sales");
        var request = (await service.CreateAssetAssignmentHistoryEditRequestAsync(asset.Id, session, history.Id))!;
        request.MonthlyFee = missing ? null : 0m;
        var result = await service.SaveAssetAssignmentHistoryAsync(request, session);
        Assert.Equal(!missing, result.Success);
        await db.Entry(history).ReloadAsync(); Assert.Equal(missing ? 12345m : 0m, history.MonthlyFee);
    }

    [Fact]
    public async Task OpenDashboardClearsCachedRowsWhenAccessChangesAndRequiresFreshLoadAfterRestoration()
    {
        await using var db = await Database(); await Seed(db, false, 12345);
        var session = Session("sales"); session.SetSession("initial", session.User!);
        using var vm = new RentalDashboardViewModel(new RentalStateService(db), session) { ReferenceDate = new(2026, 9, 25) };
        await vm.LoadAsync(); Assert.Equal(12345m, Assert.Single(vm.AlertItems).MonthlyAmount);
        session.RefreshSession("refresh", session.User!); Assert.Single(vm.AlertItems);
        session.SetOfflineSession(Session("none").User!); Assert.Empty(vm.AlertItems); Assert.Equal(0, vm.DueTodayCount);
        await vm.LoadAsync(); Assert.Null(Assert.Single(vm.AlertItems).MonthlyAmount);
        session.SetOfflineSession(Session("sales").User!); Assert.Empty(vm.AlertItems);
        await vm.LoadAsync(); Assert.Equal(12345m, Assert.Single(vm.AlertItems).MonthlyAmount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateDashboardQueryCannotRestoreDataAfterAccessChangeOrWindowClose(bool close)
    {
        var gate = new QueryGate();
        await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite("Data Source=:memory:").AddInterceptors(gate).Options);
        await db.Database.OpenConnectionAsync(); await db.Database.EnsureCreatedAsync(); await Seed(db, false, 12345);
        var session = Session("sales"); using var vm = new RentalDashboardViewModel(new RentalStateService(db), session) { ReferenceDate = new(2026, 9, 25) };
        gate.Arm(); var load = vm.LoadAsync();
        try { await gate.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(10)); if (close) vm.Dispose(); else session.SetOfflineSession(Session("none").User!); }
        finally { gate.Release(); }
        await load; Assert.Empty(vm.AlertItems); Assert.Empty(vm.ExpiringAssets); Assert.Empty(vm.UnresolvedLinkItems); Assert.False(vm.IsBusy);
    }

    private sealed class QueryGate : DbCommandInterceptor
    {
        private int _armed;
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Arm() => Volatile.Write(ref _armed, 1);
        public void Release() => _released.TrySetResult();
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("RentalBillingProfiles") && Interlocked.Exchange(ref _armed, 0) == 1)
            { Blocked.TrySetResult(); await _released.Task.WaitAsync(cancellationToken); }
            return result;
        }
    }

    private static async Task<(LocalRentalAsset, LocalRentalAssetAssignmentHistory, LocalRentalBillingProfile)> Seed(LocalDbContext db, bool hidden, int amount)
    {
        var profile = RentalReadAmountPrivacyTests.CreateProfile("known");
        profile.MonthlyAmount = amount; profile.AmountsHidden = hidden; profile.BillingTemplateJson = "[]";
        var asset = new LocalRentalAsset { Id = Guid.NewGuid(), AssetKey = Guid.NewGuid().ToString(), ManagementNumber = "HISTORY-PRIVACY",
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet, BillingProfileId = profile.Id, CustomerId = Guid.NewGuid(),
            AssetStatus = "임대진행중", MonthlyFee = 98765m, SalesAmountsHidden = hidden,
            ContractStartDate = new(2026, 9, 1), RentalEndDate = new(2027, 9, 1) };
        var history = new LocalRentalAssetAssignmentHistory { Id = Guid.NewGuid(), AssetId = asset.Id, CustomerId = Guid.NewGuid(),
            BillingProfileId = Guid.NewGuid(), TenantCode = asset.TenantCode, ResponsibleOfficeCode = asset.ResponsibleOfficeCode,
            MonthlyFee = amount, AmountsHidden = hidden, LinkedAtUtc = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UnlinkedAtUtc = new(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc), IsCurrent = false, IsDirty = false };
        db.AddRange(profile, asset, history); await db.SaveChangesAsync(); return (asset, history, profile);
    }

    private static SessionState Session(string access)
    {
        var permissions = new List<string> { AppPermissionNames.RentalViewAll, AppPermissionNames.RentalAssetEdit, AppPermissionNames.RentalProfileEdit };
        if (access is "sales" or "both") permissions.Add(AppPermissionNames.AmountViewSales);
        if (access is "purchase" or "both") permissions.Add(AppPermissionNames.AmountViewPurchase);
        var session = new SessionState();
        var user = new UserSessionDto { Username = "history-privacy", Role = access == "admin" ? DomainConstants.RoleAdmin : DomainConstants.RoleUser,
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = access is "admin" or "god" ? TenantScopeCatalog.ScopeAdmin : TenantScopeCatalog.ScopeOfficeOnly, Permissions = permissions };
        if (access == "god") session.SetSession("e30.eyJnb2QiOnRydWV9.test", user);
        else session.SetOfflineSession(user);
        return session;
    }
}
