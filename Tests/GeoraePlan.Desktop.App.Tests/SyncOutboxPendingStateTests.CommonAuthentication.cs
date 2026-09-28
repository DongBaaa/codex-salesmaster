using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed partial class SyncOutboxPendingStateTests
{
    private static LocalDbContext CreateAuthenticationStorageBusinessFixture()
    {
        // AppPaths intentionally caches its root for the lifetime of the process.
        // Each test instead opens its explicitly selected fixture, including reads.
        var root = Environment.GetEnvironmentVariable("GEORAEPLAN_APP_ROOT")
            ?? throw new InvalidOperationException("An isolated fixture root is required.");
        var connection = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "business-fixture.db"), Pooling = false
        };
        return new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection.ConnectionString).Options);
    }

    [Fact]
    public async Task CommonAuthentication_LoginScopeResetStaysInBusinessDatabase()
    {
        PrepareAppRoot("common-auth-login-scope-reset");
        try
        {
            var root = Environment.GetEnvironmentVariable("GEORAEPLAN_APP_ROOT")!;
            var common = new CommonAuthenticationDatabase(root);
            var business = new BusinessCacheDatabase(root, "USENET");
            await common.CreateNewAsync(); await business.CreateNewAsync();
            await using var db = await business.OpenAsync();
            var session = CreateCustomerScopeSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session, common);
            await local.RegisterLoginScopeAsync(session);
            await local.SetSettingAsync("LastSyncRevision", "93");
            var user = session.User!;
            user.Username = "another-yeonsu-user"; user.UserId = Guid.NewGuid();
            session.SetSession("isolated-token", user, DateTime.UtcNow.AddHours(1));
            var pending = new LocalItem { Id = Guid.NewGuid(), NameOriginal = "keep editor pending", IsDirty = true };
            db.Items.Add(pending);
            Assert.True((await local.RegisterLoginScopeAsync(session)).ScopeChanged);
            Assert.Equal("another-yeonsu-user", await local.GetSettingAsync("Login.LastScopeUsername"));
            Assert.Equal("1", await local.GetSettingAsync("Sync.PendingFullMirrorRefresh"));
            Assert.Null(await local.GetSettingAsync("LastSyncRevision"));
            Assert.Equal(EntityState.Added, db.Entry(pending).State);
            await using var fresh = await business.OpenAsync();
            Assert.False(await fresh.Items.AnyAsync());
            await using var auth = await common.OpenAsync();
            Assert.Single(await auth.Settings.ToListAsync()); // Only the storage format marker.
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task CommonAuthentication_MissingCommonFileDoesNotFallBackToBusinessCredentials()
    {
        PrepareAppRoot("common-auth-missing-no-fallback");
        try
        {
            var root = Environment.GetEnvironmentVariable("GEORAEPLAN_APP_ROOT")!;
            var common = new CommonAuthenticationDatabase(root);
            await using var business = CreateAuthenticationStorageBusinessFixture();
            await business.Database.EnsureCreatedAsync();
            business.Settings.Add(new LocalSetting { Key = "Login.SavedUsername", Value = "stale-business-account" });
            await business.SaveChangesAsync();
            var local = new LocalStateService(business, new OfficeAccessService(), new SyncRequestDispatcher(), new SessionState(), common);
            await Assert.ThrowsAnyAsync<Exception>(() => local.GetSettingAsync("Login.SavedUsername"));
            await Assert.ThrowsAnyAsync<Exception>(() => local.SetSettingAsync("Login.SavedUsername", "must-not-write"));
            Assert.False(File.Exists(common.DatabasePath));
            Assert.Equal("stale-business-account", (await business.Settings.SingleAsync(row => row.Key == "Login.SavedUsername")).Value);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task CommonAuthentication_SwitchBusinessPreservesIdentityRevocationAndBusinessProgress()
    {
        PrepareAppRoot("common-auth-business-switch");
        try
        {
            var root = Environment.GetEnvironmentVariable("GEORAEPLAN_APP_ROOT")!;
            var common = new CommonAuthenticationDatabase(root);
            var usenet = new BusinessCacheDatabase(root, "USENET");
            var itworld = new BusinessCacheDatabase(root, "ITWORLD");
            await common.CreateNewAsync(); await usenet.CreateNewAsync(); await itworld.CreateNewAsync();
            await using var first = await usenet.OpenAsync();
            await using var second = await itworld.OpenAsync();
            var us = new LocalStateService(first, new OfficeAccessService(), new SyncRequestDispatcher(), new SessionState(), common);
            var it = new LocalStateService(second, new OfficeAccessService(), new SyncRequestDispatcher(), new SessionState(), common);
            await us.SetSettingAsync("LastSyncRevision", "101");
            await it.SetSettingAsync("LastSyncRevision", "202");
            await us.SetSettingAsync("Sync.PendingFullMirrorRefresh", "1");
            await us.SetSettingAsync("CompanyProfileAssignment.scope-user", "business-specific");
            var pending = new LocalItem { Id = Guid.NewGuid(), NameOriginal = "editor still open", IsDirty = true };
            first.Items.Add(pending);
            var yeonsuUser = CreateCustomerScopeSession().User!;
            var itworldUser = CreateCustomerScopeSession().User!;
            itworldUser.Username = "itworld-isolated";
            itworldUser.TenantCode = "ITWORLD"; itworldUser.OfficeCode = "ITWORLD";
            await us.SaveSessionCacheAsync(yeonsuUser, "isolated-yeonsu");
            await us.SaveOfficeSyncCredentialAsync(yeonsuUser, yeonsuUser.Username, "isolated-yeonsu");
            await it.SaveSessionCacheAsync(itworldUser, "isolated-itworld");
            await it.SaveOfficeSyncCredentialAsync(itworldUser, itworldUser.Username, "isolated-itworld");
            await us.SaveSettingsIndependentAsync(new Dictionary<string, string>
            {
                ["Login.SavedUsername"] = yeonsuUser.Username, ["Login.RememberUsername"] = "true"
            });
            Assert.Equal(yeonsuUser.Username, await it.GetSettingAsync("Login.SavedUsername"));
            await it.SetSettingAsync("Login.RememberUsername", "false");
            Assert.Equal("false", await us.GetSettingAsync("Login.RememberUsername"));
            var authenticated = await it.AuthenticateCachedSessionAsync(yeonsuUser.Username, "isolated-yeonsu");
            Assert.NotNull(authenticated);
            Assert.Equal(yeonsuUser.UserId, authenticated.User.UserId);
            Assert.Equal("YEONSU", authenticated.OfficeCode);
            Assert.Equal("USENET_GROUP", authenticated.User.TenantCode);
            Assert.Equal(yeonsuUser.Permissions, authenticated.User.Permissions);
            Assert.Equal(2, (await us.GetStoredSyncCredentialsAsync()).Count);
            Assert.Equal(0, await it.ClearInvalidOfficeSyncCredentialsAsync());
            await it.RevokeRejectedAuthenticationCacheAsync(yeonsuUser.Username, "YEONSU");
            Assert.Null(await us.AuthenticateCachedSessionAsync(yeonsuUser.Username, "isolated-yeonsu"));
            Assert.Null(await it.GetStoredSyncCredentialAsync("YEONSU"));
            Assert.NotNull(await us.AuthenticateCachedSessionAsync(itworldUser.Username, "isolated-itworld"));
            Assert.NotNull(await it.GetStoredSyncCredentialAsync("ITWORLD"));
            await us.ClearOfficeSyncCredentialAsync("ITWORLD");
            Assert.Empty(await it.GetStoredSyncCredentialsAsync());
            await using var reopened = await usenet.OpenAsync();
            Assert.False(await reopened.Items.AnyAsync());
            Assert.Equal(EntityState.Added, first.Entry(pending).State);
            Assert.Equal("101", await us.GetSettingAsync("LastSyncRevision"));
            Assert.Equal("202", await it.GetSettingAsync("LastSyncRevision"));
            Assert.Null(await it.GetSettingAsync("Sync.PendingFullMirrorRefresh"));
            Assert.Null(await it.GetSettingAsync("CompanyProfileAssignment.scope-user"));
            foreach (var db in new[] { reopened, second })
                Assert.False(await db.Settings.AnyAsync(row => row.Key.StartsWith("CachedSession") || row.Key.StartsWith("Login.") || row.Key.StartsWith("Sync.OfficeCredential.")));
            await using var authenticationDb = await common.OpenAsync();
            Assert.False(await authenticationDb.Items.AnyAsync());
            Assert.False(await authenticationDb.Settings.AnyAsync(row => row.Key == "LastSyncRevision" || row.Key == "Sync.PendingFullMirrorRefresh" || row.Key.StartsWith("CompanyProfileAssignment.")));
            await Assert.ThrowsAsync<InvalidOperationException>(() => us.SaveSettingsIndependentAsync(
                new Dictionary<string, string> { ["Login.SavedUsername"] = "must-not-commit", ["LastSyncRevision"] = "999" }));
            Assert.Equal(yeonsuUser.Username, await it.GetSettingAsync("Login.SavedUsername"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CredentialClearFailure_RollsBackWholeCredentialWithoutSavingEditor(bool separate)
    {
        PrepareAppRoot("common-auth-clear-rollback");
        try
        {
            var root = Environment.GetEnvironmentVariable("GEORAEPLAN_APP_ROOT")!;
            var common = new CommonAuthenticationDatabase(root);
            await common.CreateNewAsync();
            await using var business = CreateAuthenticationStorageBusinessFixture();
            await business.Database.EnsureCreatedAsync();
            var local = separate
                ? new LocalStateService(business, new OfficeAccessService(), new SyncRequestDispatcher(), new SessionState(), common)
                : new LocalStateService(business, new OfficeAccessService(), new SyncRequestDispatcher(), new SessionState());
            var user = CreateCustomerScopeSession().User!;
            await local.SaveOfficeSyncCredentialAsync(user, user.Username, "isolated-password");
            await using var store = separate ? await common.OpenAsync() : CreateAuthenticationStorageBusinessFixture();
            var before = await store.Settings.AsNoTracking().Where(row => row.Key.StartsWith("Sync.OfficeCredential.")).ToDictionaryAsync(row => row.Key, row => row.Value);
            await store.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_credential_clear BEFORE UPDATE ON Settings WHEN NEW.Key = 'Sync.OfficeCredential.YEONSU.SavedAtUtc' AND NEW.Value = '' BEGIN SELECT RAISE(ABORT, 'injected credential failure'); END");
            var pending = new LocalItem { Id = Guid.NewGuid(), NameOriginal = "unsaved", IsDirty = true };
            business.Items.Add(pending);
            await Assert.ThrowsAnyAsync<Exception>(() => local.ClearOfficeSyncCredentialAsync("YEONSU"));
            var after = await store.Settings.AsNoTracking().Where(row => row.Key.StartsWith("Sync.OfficeCredential.")).ToDictionaryAsync(row => row.Key, row => row.Value);
            Assert.Equal(before.OrderBy(row => row.Key), after.OrderBy(row => row.Key));
            Assert.Equal(EntityState.Added, business.Entry(pending).State);
            await using var fresh = CreateAuthenticationStorageBusinessFixture();
            Assert.False(await fresh.Items.AnyAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidCredentialCleanup_DoesNotSaveUnrelatedPendingBusinessEdit(bool separate)
    {
        PrepareAppRoot("invalid-credential-pending-business");
        try
        {
            await using var db = CreateAuthenticationStorageBusinessFixture();
            await db.Database.EnsureCreatedAsync();
            var common = new CommonAuthenticationDatabase(Environment.GetEnvironmentVariable("GEORAEPLAN_APP_ROOT")!);
            await common.CreateNewAsync();
            await using var credentials = separate ? await common.OpenAsync() : CreateAuthenticationStorageBusinessFixture();
            credentials.Settings.Add(new LocalSetting { Key = "Sync.OfficeCredential.USENET.Username", Value = "broken" });
            await credentials.SaveChangesAsync();
            var pending = new LocalItem { Id = Guid.NewGuid(), NameOriginal = "unsaved editor value", IsDirty = true };
            db.Items.Add(pending);
            var local = separate
                ? new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), new SessionState(), common)
                : new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), new SessionState());
            Assert.Equal(1, await local.ClearInvalidOfficeSyncCredentialsAsync());
            await using var fresh = CreateAuthenticationStorageBusinessFixture();
            Assert.False(await fresh.Items.AnyAsync(row => row.Id == pending.Id));
            Assert.Equal(EntityState.Added, db.Entry(pending).State);
            Assert.Equal("", await local.GetSettingAsync("Sync.OfficeCredential.USENET.Username"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }
}
