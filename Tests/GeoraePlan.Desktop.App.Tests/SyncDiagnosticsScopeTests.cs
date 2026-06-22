using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class SyncDiagnosticsScopeTests
{
    [Fact]
    public async Task SyncDiagnosticsOperations_AreLimitedToCurrentSessionScope()
    {
        PrepareAppRoot("georaeplan-sync-diagnostics-scope");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var usenetOpenId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa1");
            var itworldOpenId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbb1");
            var usenetResolvedId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa2");
            var itworldResolvedId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbb2");
            var baseUtc = new DateTime(2026, 6, 23, 1, 20, 0, DateTimeKind.Utc);

            db.Settings.Add(new LocalSetting
            {
                Key = "Sync.LastError",
                Value = "ITWORLD 전역 설정 오류가 다른 지점 화면에 노출되면 안 됨"
            });
            db.SyncDiagnosticEvents.AddRange(
                CreateDiagnosticEvent(
                    usenetOpenId,
                    "Open",
                    baseUtc.AddMinutes(4),
                    TenantScopeCatalog.UsenetGroup,
                    OfficeCodeCatalog.Usenet,
                    "usenet-user",
                    "USENET 동기화 오류",
                    isRecoverable: true),
                CreateDiagnosticEvent(
                    itworldOpenId,
                    "Open",
                    baseUtc.AddMinutes(7),
                    TenantScopeCatalog.Itworld,
                    OfficeCodeCatalog.Itworld,
                    "itworld-user",
                    "ITWORLD 동기화 오류",
                    isRecoverable: true),
                CreateDiagnosticEvent(
                    usenetResolvedId,
                    "Resolved",
                    baseUtc.AddMinutes(1),
                    TenantScopeCatalog.UsenetGroup,
                    OfficeCodeCatalog.Usenet,
                    "usenet-user",
                    "USENET 해결 이력"),
                CreateDiagnosticEvent(
                    itworldResolvedId,
                    "Resolved",
                    baseUtc.AddMinutes(2),
                    TenantScopeCatalog.Itworld,
                    OfficeCodeCatalog.Itworld,
                    "itworld-user",
                    "ITWORLD 해결 이력"));
            await db.SaveChangesAsync();

            var diagnostics = new SyncDiagnosticsService(CreateOfficeSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet));

            var scopedEvents = await diagnostics.GetEventsAsync(new SyncDiagnosticFilter(string.Empty, "전체", "전체", "전체", false));

            Assert.Equal([usenetOpenId, usenetResolvedId], scopedEvents.Select(item => item.Id).OrderBy(id => id));
            Assert.All(scopedEvents, item => Assert.Equal(OfficeCodeCatalog.Usenet, item.OfficeCode));

            var scopedSummary = await diagnostics.GetSummaryAsync();
            Assert.Equal(1, scopedSummary.OpenIssueCount);
            Assert.Equal(1, scopedSummary.RecoverableIssueCount);
            Assert.Equal(2, scopedSummary.TotalIssueCount);
            Assert.Equal(baseUtc.AddMinutes(4), scopedSummary.LastFailureAtUtc);
            Assert.Equal("USENET 동기화 오류", scopedSummary.LastError);

            await diagnostics.ResolveOpenIssuesAsync();
            db.ChangeTracker.Clear();

            Assert.Equal("Resolved", await ReadDiagnosticStatusAsync(db, usenetOpenId));
            Assert.Equal("Open", await ReadDiagnosticStatusAsync(db, itworldOpenId));

            await diagnostics.ClearResolvedEventsAsync();
            db.ChangeTracker.Clear();

            Assert.False(await db.SyncDiagnosticEvents.AsNoTracking().AnyAsync(current => current.Id == usenetOpenId));
            Assert.False(await db.SyncDiagnosticEvents.AsNoTracking().AnyAsync(current => current.Id == usenetResolvedId));
            Assert.True(await db.SyncDiagnosticEvents.AsNoTracking().AnyAsync(current => current.Id == itworldOpenId));
            Assert.True(await db.SyncDiagnosticEvents.AsNoTracking().AnyAsync(current => current.Id == itworldResolvedId));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RecordIssueAsync_DoesNotCoalesceAcrossDiagnosticScope()
    {
        PrepareAppRoot("georaeplan-sync-diagnostics-coalesce-scope");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var existingId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
            db.SyncDiagnosticEvents.Add(CreateDiagnosticEvent(
                existingId,
                "Open",
                DateTime.UtcNow.AddMinutes(-1),
                TenantScopeCatalog.Itworld,
                OfficeCodeCatalog.Itworld,
                "itworld-user",
                "shared timeout while posting",
                normalizedMessage: "shared timeout while posting",
                occurrenceCount: 3,
                category: "통신 오류",
                subcategory: "network_timeout",
                isRecoverable: true));
            await db.SaveChangesAsync();

            var diagnostics = new SyncDiagnosticsService(CreateOfficeSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet));

            await diagnostics.RecordIssueAsync("manual-sync", "shared timeout while posting");
            db.ChangeTracker.Clear();

            var rows = await db.SyncDiagnosticEvents.AsNoTracking()
                .OrderBy(current => current.OfficeCode)
                .ToListAsync();

            Assert.Equal(2, rows.Count);
            var itworldEvent = Assert.Single(rows, current => current.Id == existingId);
            Assert.Equal(3, itworldEvent.OccurrenceCount);
            Assert.Equal(OfficeCodeCatalog.Itworld, itworldEvent.OfficeCode);

            var usenetEvent = Assert.Single(rows, current => current.Id != existingId);
            Assert.Equal(1, usenetEvent.OccurrenceCount);
            Assert.Equal(OfficeCodeCatalog.Usenet, usenetEvent.OfficeCode);
            Assert.Equal(TenantScopeCatalog.UsenetGroup, usenetEvent.TenantCode);
            Assert.Equal("usenet-user", usenetEvent.UserName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private static LocalSyncDiagnosticEvent CreateDiagnosticEvent(
        Guid id,
        string status,
        DateTime lastOccurredAtUtc,
        string tenantCode,
        string officeCode,
        string userName,
        string rawMessage,
        string normalizedMessage = "",
        int occurrenceCount = 1,
        string category = "저장/동기화 확인",
        string subcategory = "general_sync_failure",
        bool isRecoverable = false)
        => new()
        {
            Id = id,
            OccurredAtUtc = lastOccurredAtUtc.AddMinutes(-1),
            LastOccurredAtUtc = lastOccurredAtUtc,
            OccurrenceCount = occurrenceCount,
            Severity = "Error",
            Category = category,
            Subcategory = subcategory,
            UserName = userName,
            OfficeCode = officeCode,
            TenantCode = tenantCode,
            MachineName = "test-machine",
            AppVersion = "1.1.487",
            SyncPhase = "manual-sync",
            RawMessage = rawMessage,
            NormalizedMessage = string.IsNullOrWhiteSpace(normalizedMessage) ? rawMessage : normalizedMessage,
            IsRecoverable = isRecoverable,
            RecoveryAction = "동기화를 다시 시도하세요.",
            RecoveryAttempted = !string.Equals(status, "Open", StringComparison.OrdinalIgnoreCase),
            RecoverySucceeded = !string.Equals(status, "Open", StringComparison.OrdinalIgnoreCase),
            ResolvedAtUtc = string.Equals(status, "Open", StringComparison.OrdinalIgnoreCase) ? null : lastOccurredAtUtc,
            Status = status
        };

    private static Task<string> ReadDiagnosticStatusAsync(LocalDbContext db, Guid eventId)
        => db.SyncDiagnosticEvents
            .AsNoTracking()
            .Where(current => current.Id == eventId)
            .Select(current => current.Status)
            .SingleAsync();

    private static SessionState CreateOfficeSession(string tenantCode, string officeCode)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            UserId = Guid.NewGuid(),
            Username = $"{officeCode.ToLowerInvariant()}-user",
            Role = DomainConstants.RoleUser,
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly
        });
        return session;
    }

    private static void PrepareAppRoot(string prefix)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);
    }
}
