using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class VersionChangeDraftPreservationTests : IDisposable
{
    private readonly string? _previousAppRoot = Environment.GetEnvironmentVariable("GEORAEPLAN_APP_ROOT");

    public void Dispose() => Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", _previousAppRoot);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Maintenance_PreservesDraftPayloadsAndRecoveryMarkers(bool sameVersion, bool oldRepairEpoch)
    {
        PrepareTestPaths();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var expected = new Dictionary<string, string>();
        foreach (var prefix in new[] { "RENTAL.BILLINGEDITORDRAFT", "RENTAL.ONBOARDINGDRAFT" })
        {
            expected[$"{prefix}.USENET.ADMIN"] = "{\"CustomerName\":\"LEGACY-PENDING\"}";
            expected[$"{prefix}.USENET.USENET.ADMIN"] = "{\"CustomerName\":\"USENET-PENDING\"}";
            expected[$"{prefix}.ITWORLD.USENET.ADMIN"] = "{\"CustomerName\":\"ITWORLD-PENDING\"}";
            expected[$"{prefix}.USENET.ADMIN.RECOVERED.SENTINEL"] = $"{prefix}.USENET.USENET.ADMIN";
        }
        expected["RENTAL.BILLINGEDITORDRAFT.USENET.OTHER-USER"] = "malformed raw draft retained for recovery";
        expected["Probe.Unrelated"] = "keep";
        foreach (var pair in expected) db.Settings.Add(new LocalSetting { Key = pair.Key, Value = pair.Value });
        db.Settings.Add(new LocalSetting { Key = "System.LastPostUpdateMaintenanceVersion", Value = sameVersion ? "1.1.701" : "1.1.700" });
        db.Settings.Add(new LocalSetting { Key = "System.LastCacheMirrorRepairEpoch", Value = oldRepairEpoch ? "2026-05-27-lightweight-full-sync-mirror" : "2026-09-15-received-item-access" });
        await db.SaveChangesAsync();
        var session = CreateSession();
        var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

        var result = await VersionChangeMaintenanceService.RunAsync(local, new BackupService(), "1.1.701");

        Assert.Equal(!sameVersion || oldRepairEpoch, result.Ran);
        Assert.Equal(0, result.ClearedSettingCount);
        var actual = await db.Settings.AsNoTracking().Where(s => expected.Keys.Contains(s.Key)).ToDictionaryAsync(s => s.Key, s => s.Value);
        Assert.Equal(expected.OrderBy(p => p.Key), actual.OrderBy(p => p.Key));
        Assert.False((await VersionChangeMaintenanceService.RunAsync(local, new BackupService(), "1.1.701")).Ran);
        Assert.False(await db.RentalAssets.AnyAsync());
        Assert.False(await db.RentalBillingProfiles.AnyAsync());
        Assert.False(await db.Invoices.AnyAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Maintenance_KeepsLegacyRecoveryAvailableAndDoesNotOfferRecoveredDraftAgain(bool onboarding)
    {
        PrepareTestPaths();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var kind = onboarding ? RentalDraftKind.Onboarding : RentalDraftKind.Billing;
        var prefix = onboarding ? "RENTAL.ONBOARDINGDRAFT" : "RENTAL.BILLINGEDITORDRAFT";
        var key = $"{prefix}.USENET.ADMIN";
        var raw = onboarding
            ? JsonSerializer.Serialize(new RentalCustomerOnboardingDraftModel { CustomerName = "RECOVER-AFTER-UPDATE" })
            : JsonSerializer.Serialize(new RentalBillingEditorDraftModel { CustomerName = "RECOVER-AFTER-UPDATE" });
        db.Settings.Add(new LocalSetting { Key = key, Value = raw });
        await db.SaveChangesAsync();
        var session = CreateSession();
        var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
        var rental = new RentalStateService(db, local);
        await VersionChangeMaintenanceService.RunAsync(local, new BackupService(), "1.1.701");
        var preview = await rental.GetLegacyDraftPreviewAsync(kind, session);
        Assert.NotNull(preview);
        Assert.False(preview.TargetHasDraft);
        await rental.RecoverLegacyDraftAsync(preview, session);
        await VersionChangeMaintenanceService.RunAsync(local, new BackupService(), "1.1.702");

        Assert.Equal(raw, await db.Settings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).SingleAsync());
        Assert.Equal(raw, await db.Settings.AsNoTracking().Where(s => s.Key == preview.TargetKey).Select(s => s.Value).SingleAsync());
        Assert.Null(await rental.GetLegacyDraftPreviewAsync(kind, session));
        session.SetBusinessDatabase("ITWORLD");
        if (onboarding) Assert.Null(await rental.GetOnboardingDraftAsync(session));
        else Assert.Null(await rental.GetBillingEditorDraftAsync(session));
    }

    private static SessionState CreateSession()
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto { UserId = Guid.NewGuid(), Username = "admin", Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet, ScopeType = TenantScopeCatalog.ScopeAdmin });
        session.SetBusinessDatabase("USENET");
        return session;
    }

    private static void PrepareTestPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "trade-version-draft-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", root);
    }
}

