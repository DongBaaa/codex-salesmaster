using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class SyncAttemptCompletionTests
{
    internal static async Task<string> DescribeAsync(SyncService sync)
    {
        var local = (LocalStateService)typeof(SyncService).GetField("_local", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(sync)!;
        var pending = await local.GetPendingSyncSummaryAsync();
        return "completion=" + await local.GetSettingAsync("Sync.LastError") + "; pending=" +
            System.Text.Json.JsonSerializer.Serialize(pending);
    }

    public static IEnumerable<object[]> PendingCases()
    {
        foreach (var office in new[] { "USENET", "YEONSU", "ITWORLD" })
        foreach (var admin in new[] { false, true })
        foreach (var pending in new[] { "dirty", "price", "history", "Prepared", "Failed" })
            yield return new object[] { office, admin, pending };
    }

    [Theory]
    [MemberData(nameof(PendingCases))]
    public async Task PendingWork_PreservesOpenDiagnosticsAndLastSuccessfulAttempt(string office, bool admin, string pending)
    {
        await using var fixture = await Fixture.CreateAsync(office, admin);
        var db = fixture.Db;
        await fixture.Diagnostics.RecordIssueAsync("push", "synthetic timeout");
        var diagnosticId = await db.SyncDiagnosticEvents.Select(x => x.Id).SingleAsync();
        db.Settings.Add(new LocalSetting { Key = "Sync.LastSuccessAt", Value = "previous-success" });
        var customerId = Guid.NewGuid();
        if (pending == "dirty")
            db.Customers.Add(new LocalCustomer { Id = customerId, TenantCode = fixture.Session.TenantCode,
                OfficeCode = office, ResponsibleOfficeCode = office, NameOriginal = "preserved edit", IsDirty = true });
        else if (pending == "price")
        {
            db.Items.Add(new LocalItem { Id = customerId, OfficeCode = office, TenantCode = fixture.Session.TenantCode, NameOriginal = "price owner", IsDirty = false });
            db.ItemPriceGrades.Add(new LocalItemPriceGrade { ItemId = customerId, PriceGradeOptionId = Guid.NewGuid(), UnitPrice = 123m, IsDirty = true });
        }
        else if (pending == "history")
            db.RentalAssetAssignmentHistories.Add(new LocalRentalAssetAssignmentHistory { AssetId = customerId,
                TenantCode = fixture.Session.TenantCode, ResponsibleOfficeCode = office, CustomerName = "preserved history", IsDirty = true });
        else
            db.SyncOutboxEntries.Add(new LocalSyncOutboxEntry { EntityId = customerId, EntityName = "Customer",
                TenantCode = fixture.Session.TenantCode, OfficeCode = office, Status = pending });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        using var lease = await fixture.Session.AcquireSyncScopeCommitLeaseAsync(default);
        var result = await fixture.Diagnostics.RecordSyncAttemptCompletionAsync(db, DateTime.UtcNow);

        Assert.False(result.Succeeded);
        Assert.Contains("동기화 확인 필요", result.Message);
        Assert.Equal(pending is "dirty" or "price" or "history" ? 1 : 0, result.DirtyCount);
        Assert.Equal(pending == "Prepared" ? 1 : 0, result.PendingCount);
        Assert.Equal(pending == "Failed" ? 1 : 0, result.FailedCount);
        db.ChangeTracker.Clear();
        var diagnostic = await db.SyncDiagnosticEvents.SingleAsync(x => x.Id == diagnosticId);
        Assert.Equal("Open", diagnostic.Status);
        Assert.False(diagnostic.RecoverySucceeded);
        Assert.Null(diagnostic.ResolvedAtUtc);
        Assert.Equal("previous-success", await db.Settings.Where(x => x.Key == "Sync.LastSuccessAt").Select(x => x.Value).SingleAsync());
        Assert.Equal(result.Message, await db.Settings.Where(x => x.Key == "Sync.LastError").Select(x => x.Value).SingleAsync());
        if (pending == "dirty")
        {
            var customer = await db.Customers.SingleAsync(x => x.Id == customerId);
            Assert.True(customer.IsDirty);
            Assert.Equal("preserved edit", customer.NameOriginal);
        }
        else if (pending == "price")
        {
            var grade = await db.ItemPriceGrades.SingleAsync();
            Assert.True(grade.IsDirty);
            Assert.Equal(123m, grade.UnitPrice);
        }
        else if (pending == "history")
        {
            var history = await db.RentalAssetAssignmentHistories.SingleAsync();
            Assert.True(history.IsDirty);
            Assert.Equal("preserved history", history.CustomerName);
        }
        else
            Assert.Equal(pending, await db.SyncOutboxEntries.Select(x => x.Status).SingleAsync());
    }

    [Theory]
    [InlineData("USENET")]
    [InlineData("YEONSU")]
    [InlineData("ITWORLD")]
    public async Task CleanExchange_ResolvesOnlyEarlierTransportInCurrentScope(string office)
    {
        await using var fixture = await Fixture.CreateAsync(office, false);
        var db = fixture.Db;
        await fixture.Diagnostics.RecordIssueAsync("push", "old timeout");
        var old = await db.SyncDiagnosticEvents.AsNoTracking().SingleAsync();
        var start = DateTime.UtcNow;
        var conflictId = Guid.NewGuid();
        await fixture.Diagnostics.RecordIssueAsync("push", $"Customer {conflictId} - Expected revision mismatch");
        await fixture.Diagnostics.RecordIssueAsync("selected-repair", "repair timeout");
        await fixture.Diagnostics.RecordIssueAsync("pull", "new timeout");
        await db.SyncDiagnosticEvents.Where(x => x.Subcategory == "revision_conflict" || x.SyncPhase == "selected-repair")
            .ExecuteUpdateAsync(changes => changes.SetProperty(x => x.OccurredAtUtc, start.AddMinutes(-1))
                .SetProperty(x => x.LastOccurredAtUtc, start.AddMinutes(-1)));
        var otherOffice = office == "ITWORLD" ? "USENET" : "ITWORLD";
        db.SyncDiagnosticEvents.Add(new LocalSyncDiagnosticEvent {
            Id = Guid.NewGuid(), OfficeCode = otherOffice,
            TenantCode = otherOffice == "ITWORLD" ? TenantScopeCatalog.Itworld : TenantScopeCatalog.UsenetGroup,
            SyncPhase = "push", Subcategory = "network_timeout", IsRecoverable = true, Status = "Open",
            OccurredAtUtc = start.AddMinutes(-2), LastOccurredAtUtc = start.AddMinutes(-2) });
        // Work in another office must neither prevent this scope's successful
        // exchange nor be modified or have its diagnostic resolved.
        db.Customers.Add(new LocalCustomer { TenantCode = otherOffice == "ITWORLD" ? TenantScopeCatalog.Itworld : TenantScopeCatalog.UsenetGroup,
            OfficeCode = otherOffice, ResponsibleOfficeCode = otherOffice, NameOriginal = "other office dirty", IsDirty = true });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        using var lease = await fixture.Session.AcquireSyncScopeCommitLeaseAsync(default);
        var result = await fixture.Diagnostics.RecordSyncAttemptCompletionAsync(db, start);

        Assert.True(result.Succeeded);
        db.ChangeTracker.Clear();
        var rows = await db.SyncDiagnosticEvents.ToListAsync();
        Assert.Equal("Resolved", rows.Single(x => x.Id == old.Id).Status);
        Assert.All(rows.Where(x => x.Id != old.Id), x => { Assert.Equal("Open", x.Status); Assert.False(x.RecoverySucceeded); });
        Assert.True(await db.Customers.Where(x => x.OfficeCode == otherOffice).Select(x => x.IsDirty).SingleAsync());
        Assert.True(DateTime.TryParse(await db.Settings.Where(x => x.Key == "Sync.LastSuccessAt").Select(x => x.Value).SingleAsync(), out _));
        Assert.Equal("", await db.Settings.Where(x => x.Key == "Sync.LastError").Select(x => x.Value).SingleAsync());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required LocalDbContext Db { get; init; }
        public required SessionState Session { get; init; }
        public required SyncDiagnosticsService Diagnostics { get; init; }
        public static async Task<Fixture> CreateAsync(string office, bool admin)
        {
            var path = Path.Combine(Path.GetTempPath(), "sync-attempt-completion-" + Guid.NewGuid().ToString("N") + ".db");
            var options = new DbContextOptionsBuilder<LocalDbContext>().UseSqlite("Data Source=" + path + ";Pooling=False").Options;
            var db = new LocalDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var session = new SessionState();
            session.SetSession("synthetic", new UserSessionDto { UserId = Guid.NewGuid(), Username = "fixture",
                OfficeCode = office, TenantCode = office == "ITWORLD" ? TenantScopeCatalog.Itworld : TenantScopeCatalog.UsenetGroup,
                Role = admin ? DomainConstants.RoleAdmin : DomainConstants.RoleUser,
                ScopeType = admin ? TenantScopeCatalog.ScopeAdmin : TenantScopeCatalog.ScopeOfficeOnly }, DateTime.UtcNow.AddHours(1));
            return new Fixture { Db = db, Session = session, Diagnostics = new SyncDiagnosticsService(session, () => new LocalDbContext(options)) };
        }
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
