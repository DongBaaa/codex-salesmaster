using System.IO;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed partial class SyncOutboxPendingStateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoreAuthentication_OldBackupCannotReinstateRevokedOfflineLogin(bool legacy)
    {
        PrepareAppRoot("restore-revoked-offline-login");
        try
        {
            var root = Environment.GetEnvironmentVariable("GEORAEPLAN_APP_ROOT")!;
            var path = Path.Combine(root, "business-fixture.db");
            var attachments = Path.Combine(root, "attachments");
            var backups = Path.Combine(root, "backups");
            Directory.CreateDirectory(attachments); Directory.CreateDirectory(backups);
            var package = Path.Combine(backups, legacy ? "거래플랜_20260928_010101_001.db" : "old.gpbackup");
            var user = CreateCustomerScopeSession().User!;
            user.Username = "restore-user-" + Guid.NewGuid().ToString("N");
            var itemId = Guid.NewGuid();
            await using (var db = CreateAuthenticationStorageBusinessFixture())
            {
                await db.Database.EnsureCreatedAsync();
                db.Items.Add(new LocalItem { Id = itemId, NameOriginal = "preserve restored item", IsDirty = true });
                db.Settings.Add(new LocalSetting { Key = "CachedSessionX_Preference", Value = "not authentication" });
                await db.SaveChangesAsync();
                var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), new SessionState());
                await local.SaveSessionCacheAsync(user, "isolated-restore-password");
                Assert.NotNull(await local.AuthenticateCachedSessionAsync(user.Username, "isolated-restore-password"));
                if (legacy)
                {
                    await using var source = new SqliteConnection(db.Database.GetConnectionString());
                    await using var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = package, Pooling = false }.ConnectionString);
                    await source.OpenAsync(); await destination.OpenAsync();
                    source.BackupDatabase(destination);
                }
                else
                    await BackupService.CreateConsistentBackupPackageAsync(path, attachments, package, CancellationToken.None);
                await local.RevokeRejectedAuthenticationCacheAsync(user.Username, user.OfficeCode);
                Assert.Null(await local.AuthenticateCachedSessionAsync(user.Username, "isolated-restore-password"));
            }
            Assert.True(BackupService.IsVerifiedBackupArtifact(package));
            var backupHash = SHA256.HashData(await File.ReadAllBytesAsync(package));
            BackupService.RestoreBackupArtifact(package, path, attachments, backups);
            Assert.Equal(backupHash, SHA256.HashData(await File.ReadAllBytesAsync(package)));
            await using var restored = CreateAuthenticationStorageBusinessFixture();
            var next = new LocalStateService(restored, new OfficeAccessService(), new SyncRequestDispatcher(), new SessionState());
            Assert.Null(await next.AuthenticateCachedSessionAsync(user.Username, "isolated-restore-password"));
            Assert.False(await restored.Settings.AnyAsync(row => row.Key.StartsWith("CachedSession.") || row.Key.StartsWith("CachedSession_")));
            Assert.Equal("preserve restored item", (await restored.Items.SingleAsync(row => row.Id == itemId)).NameOriginal);
            Assert.True((await restored.Items.SingleAsync(row => row.Id == itemId)).IsDirty);
            Assert.Equal("not authentication", (await restored.Settings.SingleAsync(row => row.Key == "CachedSessionX_Preference")).Value);
            // A verified online login may create a new cache after restoration.
            await next.SaveSessionCacheAsync(user, "new-isolated-password");
            Assert.NotNull(await next.AuthenticateCachedSessionAsync(user.Username, "new-isolated-password"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData("before-switch")]
    [InlineData("ignored-delete")]
    [InlineData("after-switch")]
    public async Task RestoreAuthentication_FailurePreservesCurrentLoginAndBusinessData(string failure)
    {
        PrepareAppRoot("restore-authentication-failure");
        try
        {
            var root = Environment.GetEnvironmentVariable("GEORAEPLAN_APP_ROOT")!;
            var path = Path.Combine(root, "business-fixture.db");
            var attachments = Path.Combine(root, "attachments");
            var backups = Path.Combine(root, "backups");
            Directory.CreateDirectory(attachments); Directory.CreateDirectory(backups);
            var package = Path.Combine(backups, "rollback-test.gpbackup");
            var user = CreateCustomerScopeSession().User!;
            user.Username = "restore-failure-user-" + Guid.NewGuid().ToString("N");
            await using (var db = CreateAuthenticationStorageBusinessFixture())
            {
                await db.Database.EnsureCreatedAsync();
                db.Settings.Add(new LocalSetting { Key = "Business.RestoreProbe", Value = "old backup" });
                await db.SaveChangesAsync();
                var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), new SessionState());
                await local.SaveSessionCacheAsync(user, "old-password");
                if (failure != "after-switch")
                    await db.Database.ExecuteSqlRawAsync(failure == "ignored-delete"
                        ? "CREATE TRIGGER fail_auth_delete BEFORE DELETE ON Settings WHEN OLD.Key GLOB 'CachedSession.*' BEGIN SELECT RAISE(IGNORE); END"
                        : "CREATE TRIGGER fail_auth_delete BEFORE DELETE ON Settings WHEN OLD.Key GLOB 'CachedSession.*' BEGIN SELECT RAISE(ABORT, 'injected authentication cleanup failure'); END");
                await BackupService.CreateConsistentBackupPackageAsync(path, attachments, package, CancellationToken.None);
                if (failure != "after-switch") await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_auth_delete");
                await local.SetSettingAsync("Business.RestoreProbe", "current protected data");
                await local.SaveSessionCacheAsync(user, "current-password");
            }
            var switchReached = false;
            Assert.ThrowsAny<Exception>(() => BackupService.RestoreBackupArtifact(package, path, attachments, backups,
                afterSwitchBeforeValidation: () => { switchReached = true; throw new InvalidOperationException("injected after switch"); }));
            Assert.Equal(failure == "after-switch", switchReached);
            await using var current = CreateAuthenticationStorageBusinessFixture();
            var service = new LocalStateService(current, new OfficeAccessService(), new SyncRequestDispatcher(), new SessionState());
            Assert.Equal("current protected data", await service.GetSettingAsync("Business.RestoreProbe"));
            Assert.NotNull(await service.AuthenticateCachedSessionAsync(user.Username, "current-password"));
            Assert.Null(await service.AuthenticateCachedSessionAsync(user.Username, "old-password"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }
}
