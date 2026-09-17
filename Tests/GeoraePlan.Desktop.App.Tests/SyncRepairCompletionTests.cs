using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class SyncRepairCompletionTests
{
    [Theory]
    [InlineData(false, true, false, 0, 0, 0)]
    [InlineData(true, true, false, 1, 0, 0)]
    [InlineData(true, true, false, 0, 1, 0)]
    [InlineData(true, true, false, 0, 0, 1)]
    [InlineData(true, false, false, 0, 0, 0)]
    [InlineData(true, true, true, 0, 0, 0)]
    public void UnverifiedOrIncompleteRepair_IsNeverReportedRecovered(
        bool steps, bool verified, bool manual, int dirty, int unacknowledged, int targets)
    {
        var result = SyncRepairCompletion.Evaluate(steps, verified, manual, dirty, unacknowledged, targets);
        Assert.False(result.Succeeded);
        Assert.DoesNotContain("복구와 현재 범위의 전송 완료를 확인", result.Message);
    }

    [Fact]
    public void ConfirmedRepair_ReportsCompletion()
    {
        var result = SyncRepairCompletion.Evaluate(true, true, false, 0, 0, 0);
        Assert.True(result.Succeeded);
        Assert.Contains("확인했습니다", result.Message);
    }

    [Theory]
    [InlineData("USENET")]
    [InlineData("YEONSU")]
    [InlineData("ITWORLD")]
    public async Task RevisionConflict_RemainsOpenAndManual_AndCannotProduceRecoveredFailure(string office)
    {
        var root = Path.Combine(Path.GetTempPath(), "sync-repair-outcome-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite("Data Source=" + Path.Combine(root, "fixture.db")).Options;
        try
        {
            var session = new SessionState();
            session.SetSession("test", new UserSessionDto
            {
                UserId=Guid.NewGuid(), Username="test-admin", Role=DomainConstants.RoleAdmin,
                TenantCode=office=="ITWORLD" ? TenantScopeCatalog.Itworld : TenantScopeCatalog.UsenetGroup,
                OfficeCode=office, ScopeType=TenantScopeCatalog.ScopeAdmin
            },DateTime.UtcNow.AddHours(1));
            await using var db = new LocalDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var diagnostics = new SyncDiagnosticsService(session, () => new LocalDbContext(options));
            var id = Guid.NewGuid();
            await diagnostics.RecordIssueAsync("push", $"RentalBillingProfile {id:D} - Expected revision mismatch. client=1, server=2");
            var filter = new SyncDiagnosticFilter("", "전체", "전체", "전체", false);
            var conflict = Assert.Single(await diagnostics.GetEventsAsync(filter));
            Assert.Equal("동시성 충돌", conflict.Category);
            Assert.Equal("revision_conflict", conflict.Subcategory);
            Assert.Equal(id.ToString(), conflict.EntityId);
            Assert.False(conflict.IsRecoverable);
            Assert.Equal("Open", conflict.Status);
            Assert.Empty(await diagnostics.GetEventsAsync(filter with { OnlyRecoverable=true }));

            await db.SyncDiagnosticEvents.Where(x=>x.Id==conflict.Id).ExecuteUpdateAsync(
                changes=>changes.SetProperty(x=>x.Category,"저장/동기화 확인")
                    .SetProperty(x=>x.Subcategory,"general_sync_failure")
                    .SetProperty(x=>x.IsRecoverable,true));
            await diagnostics.RecordIssueAsync("push", $"RentalBillingProfile {id:D} - Expected revision mismatch. client=1, server=2");
            var reclassified = Assert.Single(await diagnostics.GetEventsAsync(filter));
            Assert.Equal(conflict.Id,reclassified.Id);
            Assert.Equal(2,reclassified.OccurrenceCount);
            Assert.Equal("revision_conflict",reclassified.Subcategory);
            Assert.False(reclassified.IsRecoverable);

            var outcome = SyncRepairCompletion.Evaluate(false,true,true,1,1,1);
            await diagnostics.RecordIssueAsync("selected-repair", outcome.Message,
                recoveryAttempted:true, recoverySucceeded:outcome.Succeeded);
            var rows = await diagnostics.GetEventsAsync(filter);
            var failure = Assert.Single(rows,x=>x.SyncPhase=="selected-repair");
            Assert.Equal("Open",failure.Status);
            Assert.False(failure.RecoverySucceeded);
            Assert.Null(failure.ResolvedAtUtc);
            Assert.Equal("Open",rows.Single(x=>x.Id==conflict.Id).Status);
            Assert.Equal(2,await diagnostics.CountUnconfirmedRecoveryTargetsAsync([conflict.Id,failure.Id,conflict.Id]));
            await db.SyncDiagnosticEvents.Where(x=>x.Id==conflict.Id).ExecuteUpdateAsync(changes=>changes.SetProperty(x=>x.Status,"Resolved"));
            Assert.Equal(0,await diagnostics.CountUnconfirmedRecoveryTargetsAsync([conflict.Id]));
            Assert.Equal(1,await diagnostics.CountUnconfirmedRecoveryTargetsAsync([Guid.NewGuid()]));
            // Confirmation must not depend on the 300-row diagnostic screen limit.
            var saved = await db.SyncDiagnosticEvents.AsNoTracking().SingleAsync(x=>x.Id==conflict.Id);
            for(var index=0;index<305;index++)
                db.SyncDiagnosticEvents.Add(new LocalSyncDiagnosticEvent
                {
                    Id=Guid.NewGuid(),UserName=saved.UserName,OfficeCode=saved.OfficeCode,TenantCode=saved.TenantCode,
                    Status="Open",OccurredAtUtc=DateTime.UtcNow,LastOccurredAtUtc=DateTime.UtcNow,
                    RawMessage="newer unrelated event",NormalizedMessage="newer-"+index
                });
            await db.SaveChangesAsync();
            Assert.DoesNotContain(await diagnostics.GetEventsAsync(filter),x=>x.Id==conflict.Id);
            Assert.Equal(0,await diagnostics.CountUnconfirmedRecoveryTargetsAsync([conflict.Id]));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }
}
