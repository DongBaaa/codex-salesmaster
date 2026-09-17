using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class LoginOutboxRecoveryTests
{
    [Theory]
    [InlineData("USENET", "Prepared")]
    [InlineData("USENET", "Sent")]
    [InlineData("USENET", "Failed")]
    [InlineData("YEONSU", "Prepared")]
    [InlineData("YEONSU", "Sent")]
    [InlineData("YEONSU", "Failed")]
    [InlineData("ITWORLD", "Prepared")]
    [InlineData("ITWORLD", "Sent")]
    [InlineData("ITWORLD", "Failed")]
    public async Task Login_RecoversSameOwnerReceiptWithoutChangingRetryIdentity(string office, string status)
    {
        await using var fixture = await Fixture.CreateAsync(office);
        var row = fixture.Row;
        row.Status = status;
        await fixture.Db.SaveChangesAsync();
        var oldSession = row.SessionId;
        var expected = JsonSerializer.Deserialize<LocalSyncOutboxEntry>(JsonSerializer.Serialize(row))!;
        expected.SessionId = fixture.Session.SessionId;
        fixture.Db.Settings.Add(new LocalSetting { Key = "Editor.Unsaved", Value = "keep" });

        await fixture.Local.RegisterLoginScopeAsync(fixture.Session);
        await using var reopened = fixture.OpenDb();
        var actual = await reopened.SyncOutboxEntries.SingleAsync();
        Assert.NotEqual(oldSession, actual.SessionId);
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
        Assert.Null(await reopened.Settings.FindAsync("Editor.Unsaved"));
        Assert.Contains(fixture.Db.ChangeTracker.Entries<LocalSetting>(), x => x.State == EntityState.Added && x.Entity.Key == "Editor.Unsaved");

        // Registering again must not rewrite timestamps, operation IDs, or accepted evidence.
        await fixture.Local.RegisterLoginScopeAsync(fixture.Session);
        reopened.ChangeTracker.Clear();
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(await reopened.SyncOutboxEntries.SingleAsync()));
    }

    [Theory]
    [InlineData("other-user")]
    [InlineData("other-device")]
    [InlineData("missing-device-setting")]
    [InlineData("missing-session")]
    [InlineData("missing-user")]
    [InlineData("missing-mutation")]
    [InlineData("missing-entity")]
    [InlineData("missing-entity-name")]
    [InlineData("acknowledged")]
    [InlineData("unknown-status")]
    [InlineData("other-tenant")]
    [InlineData("wrong-database")]
    [InlineData("missing-database")]
    [InlineData("wrong-office")]
    [InlineData("wrong-responsible")]
    [InlineData("inaccessible-office")]
    [InlineData("offline-login")]
    [InlineData("expired-login")]
    public async Task Login_PreservesUnrelatedOrUnverifiableReceipts(string mismatch)
    {
        await using var fixture = await Fixture.CreateAsync("YEONSU");
        var row = fixture.Row;
        switch (mismatch)
        {
            case "other-user": row.UserId = Guid.NewGuid(); break;
            case "other-device": row.DeviceId = "another-device"; break;
            case "missing-device-setting": fixture.Db.Settings.Remove((await fixture.Db.Settings.FindAsync("Sync.DeviceId"))!); break;
            case "missing-session": row.SessionId = Guid.Empty; break;
            case "missing-user": row.UserId = Guid.Empty; break;
            case "missing-mutation": row.MutationId = ""; break;
            case "missing-entity": row.EntityId = Guid.Empty; break;
            case "missing-entity-name": row.EntityName = ""; break;
            case "acknowledged": row.Status = "Acknowledged"; break;
            case "unknown-status": row.Status = "unknown"; break;
            case "other-tenant": row.TenantCode = "ITWORLD"; row.BusinessDatabaseName = "ITWORLD"; row.OfficeCode = "ITWORLD"; row.ResponsibleOfficeCode = "ITWORLD"; break;
            case "wrong-database": row.BusinessDatabaseName = "ITWORLD"; break;
            case "missing-database": row.BusinessDatabaseName = ""; break;
            case "wrong-office": row.OfficeCode = "ITWORLD"; break;
            case "wrong-responsible": row.ResponsibleOfficeCode = "ITWORLD"; break;
            case "inaccessible-office": row.OfficeCode = "USENET"; row.ResponsibleOfficeCode = "USENET"; break;
            case "offline-login": fixture.Session.SetOfflineSession(fixture.Session.User!); break;
            case "expired-login": fixture.Session.SetSession("test-token", fixture.Session.User!, DateTime.UtcNow.AddMinutes(-1)); break;
        }
        await fixture.Db.SaveChangesAsync();
        var expected = JsonSerializer.Serialize(row);
        await fixture.Local.RegisterLoginScopeAsync(fixture.Session);
        await using var reopened = fixture.OpenDb();
        Assert.Equal(expected, JsonSerializer.Serialize(await reopened.SyncOutboxEntries.SingleAsync()));
    }

    [Fact]
    public async Task StoredOfficeLogin_RecoversReceiptsWithoutReplacingInteractiveLoginScope()
    {
        await using var fixture = await Fixture.CreateAsync("YEONSU");
        fixture.Db.Settings.Add(new LocalSetting { Key = "Login.LastScopeKey", Value = "other-user|ITWORLD|ITWORLD|OfficeOnly" });
        await fixture.Db.SaveChangesAsync();
        await fixture.Local.ResumeOutboxAfterOnlineLoginAsync(fixture.Session, CancellationToken.None);
        await using var reopened = fixture.OpenDb();
        Assert.Equal(fixture.Session.SessionId, (await reopened.SyncOutboxEntries.SingleAsync()).SessionId);
        Assert.Equal("other-user|ITWORLD|ITWORLD|OfficeOnly", (await reopened.Settings.FindAsync("Login.LastScopeKey"))!.Value);
    }

    [Fact]
    public async Task GlobalAdminLogin_RecoversOwnOtherBusinessDatabaseReceipt()
    {
        await using var fixture = await Fixture.CreateAsync("ITWORLD");
        var user = fixture.Session.User!;
        fixture.Session.SetSession("test-token", new UserSessionDto { UserId = user.UserId, Username = user.Username, Role = "Admin", TenantCode = "USENET_GROUP", OfficeCode = "USENET", ScopeType = "Admin" }, DateTime.UtcNow.AddDays(1));
        await fixture.Local.RegisterLoginScopeAsync(fixture.Session);
        await using var reopened = fixture.OpenDb();
        var receipt = await reopened.SyncOutboxEntries.SingleAsync();
        Assert.Equal(fixture.Session.SessionId, receipt.SessionId);
        Assert.Equal("ITWORLD", receipt.BusinessDatabaseName);
    }

    [Fact]
    public async Task Login_RecoveryFailureRollsBackScopeSettingsAndReceiptTogether()
    {
        await using var fixture = await Fixture.CreateAsync("USENET");
        await fixture.Db.Database.ExecuteSqlRawAsync("CREATE TRIGGER RejectLoginHandover BEFORE UPDATE OF SessionId ON SyncOutboxEntries BEGIN SELECT RAISE(ABORT, 'synthetic handover failure'); END;");
        var original = JsonSerializer.Serialize(fixture.Row);
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Local.RegisterLoginScopeAsync(fixture.Session));
        await using var reopened = fixture.OpenDb();
        Assert.Equal(original, JsonSerializer.Serialize(await reopened.SyncOutboxEntries.SingleAsync()));
        Assert.False(await reopened.Settings.AnyAsync(x => x.Key.StartsWith("Login.LastScope")));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"login-outbox-{Guid.NewGuid():N}.db");
        public LocalDbContext Db { get; private set; } = null!;
        public SessionState Session { get; } = new();
        public LocalStateService Local { get; private set; } = null!;
        public LocalSyncOutboxEntry Row { get; private set; } = null!;
        public LocalDbContext OpenDb() => new(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite($"Data Source={_path};Pooling=False").Options);

        public static async Task<Fixture> CreateAsync(string office)
        {
            var f = new Fixture();
            f.Db = f.OpenDb();
            await f.Db.Database.EnsureCreatedAsync();
            f.Session.SetSession("test-token", new UserSessionDto { UserId = Guid.NewGuid(), Username = "restart-user", Role = "User", TenantCode = office == "ITWORLD" ? "ITWORLD" : "USENET_GROUP", OfficeCode = office, ScopeType = "OfficeOnly" }, DateTime.UtcNow.AddDays(1));
            f.Local = new(f.Db, new OfficeAccessService(), new SyncRequestDispatcher(), f.Session);
            f.Row = new LocalSyncOutboxEntry
            {
                MutationId = "stable-retry-operation", DeviceId = "test-device", EntityName = "LocalPayment", EntityId = Guid.NewGuid(),
                UserId = f.Session.User!.UserId, SessionId = Guid.NewGuid(), TenantCode = f.Session.TenantCode,
                BusinessDatabaseName = TenantScopeCatalog.GetDatabaseName(f.Session.TenantCode),
                OfficeCode = office == "YEONSU" ? "USENET" : office, ResponsibleOfficeCode = office,
                Status = "Failed", ExpectedRevision = 4, AcceptedRevision = 5, ErrorMessage = "keep retry evidence",
                PreparedAtUtc = new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc),
                SentAtUtc = new DateTime(2026, 9, 7, 0, 0, 1, DateTimeKind.Utc),
                AcceptedUpdatedAtUtc = new DateTime(2026, 9, 7, 0, 0, 2, DateTimeKind.Utc)
            };
            f.Db.Settings.Add(new LocalSetting { Key = "Sync.DeviceId", Value = "test-device" });
            f.Db.SyncOutboxEntries.Add(f.Row);
            await f.Db.SaveChangesAsync();
            f.Db.ChangeTracker.Clear();
            f.Row = await f.Db.SyncOutboxEntries.SingleAsync();
            return f;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(_path + suffix);
        }
    }
}
