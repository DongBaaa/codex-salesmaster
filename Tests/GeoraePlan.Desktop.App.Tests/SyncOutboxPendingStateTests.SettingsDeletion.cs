using System.IO;
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
    public async Task SettingsDeletion_PrefixFailureRollsBackAllRowsAndKeepsEditor(bool commonTarget)
    {
        PrepareAppRoot("settings-deletion-rollback");
        try
        {
            var common = new CommonAuthenticationDatabase(Environment.GetEnvironmentVariable("GEORAEPLAN_APP_ROOT")!);
            await common.CreateNewAsync();
            await using var business = CreateAuthenticationStorageBusinessFixture();
            await business.Database.EnsureCreatedAsync();
            await using var target = commonTarget ? await common.OpenAsync() : CreateAuthenticationStorageBusinessFixture();
            var prefix = commonTarget ? "Sync.OfficeCredential.TEST." : "Editor.";
            target.Settings.AddRange(new LocalSetting { Key = prefix + "A", Value = "first" },
                new LocalSetting { Key = prefix + "Z", Value = "last" });
            await target.SaveChangesAsync();
            await target.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_last_delete BEFORE DELETE ON Settings WHEN OLD.Value = 'last' BEGIN SELECT RAISE(ABORT, 'injected last delete failure'); END");
            var pending = new LocalItem { Id = Guid.NewGuid(), NameOriginal = "unsaved", IsDirty = true };
            business.Items.Add(pending);
            var local = new LocalStateService(business, new OfficeAccessService(), new SyncRequestDispatcher(), new SessionState(), common);
            await Assert.ThrowsAnyAsync<Exception>(() => local.DeleteSettingsByPrefixAsync(prefix));
            Assert.Equal(2, await target.Settings.AsNoTracking().CountAsync(row => row.Key.StartsWith(prefix)));
            Assert.Equal(EntityState.Added, business.Entry(pending).State);
            await using var fresh = CreateAuthenticationStorageBusinessFixture();
            Assert.False(await fresh.Items.AnyAsync());
            await target.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_last_delete");
            Assert.Equal(2, await local.DeleteSettingsByPrefixAsync(prefix));
            Assert.Equal(0, await local.DeleteSettingsByPrefixAsync(prefix));
            Assert.False(await fresh.Items.AnyAsync());
            Assert.Equal(EntityState.Added, business.Entry(pending).State);
            await using var auth = await common.OpenAsync();
            Assert.Equal("1", (await auth.Settings.SingleAsync(row => row.Key == CommonAuthenticationDatabase.FormatSettingKey)).Value);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData("Login.")]
    [InlineData("Sync.")]
    [InlineData("CachedSession")]
    [InlineData("Login.SavedUsername")]
    public async Task SettingsDeletion_MixedStoragePrefixIsRejectedBeforeAnyMutation(string prefix)
    {
        PrepareAppRoot("settings-deletion-mixed-storage");
        try
        {
            var common = new CommonAuthenticationDatabase(Environment.GetEnvironmentVariable("GEORAEPLAN_APP_ROOT")!);
            await common.CreateNewAsync();
            await using var business = CreateAuthenticationStorageBusinessFixture();
            await business.Database.EnsureCreatedAsync();
            var businessKey = prefix + "BusinessOnly";
            business.Settings.Add(new LocalSetting { Key = businessKey, Value = "preserve business" });
            await business.SaveChangesAsync();
            var local = new LocalStateService(business, new OfficeAccessService(), new SyncRequestDispatcher(), new SessionState(), common);
            await local.SetSettingAsync("Login.SavedUsername", "preserve common");
            var before = await business.Settings.AsNoTracking().ToDictionaryAsync(row => row.Key, row => row.Value);
            await Assert.ThrowsAsync<InvalidOperationException>(() => local.DeleteSettingsByPrefixAsync(prefix));
            Assert.Equal("preserve common", await local.GetSettingAsync("Login.SavedUsername"));
            Assert.Equal(before.OrderBy(row => row.Key),
                (await business.Settings.AsNoTracking().ToDictionaryAsync(row => row.Key, row => row.Value)).OrderBy(row => row.Key));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData("BusinessCache.Owner.v1")]
    [InlineData("BusinessCache.Format")]
    [InlineData("CommonAuthentication.Format")]
    public async Task SettingsDeletion_StorageIdentityCannotBeChangedByGeneralSettings(string marker)
    {
        PrepareAppRoot("settings-deletion-storage-marker");
        try
        {
            await using var db = CreateAuthenticationStorageBusinessFixture();
            await db.Database.EnsureCreatedAsync();
            db.Settings.AddRange(new LocalSetting { Key = marker, Value = "preserved" },
                new LocalSetting { Key = marker + ".Other", Value = "also-preserved" });
            await db.SaveChangesAsync();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), new SessionState());
            await Assert.ThrowsAsync<InvalidOperationException>(() => local.SetSettingAsync(marker, "changed"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => local.DeleteSettingAsync(marker));
            await Assert.ThrowsAsync<InvalidOperationException>(() => local.DeleteSettingsByPrefixAsync(marker));
            await Assert.ThrowsAsync<InvalidOperationException>(() => local.SaveSettingsIndependentAsync(
                new Dictionary<string, string> { ["Login.SavedUsername"] = "must-not-commit", [marker] = "changed" }));
            await using var fresh = CreateAuthenticationStorageBusinessFixture();
            Assert.Equal("preserved", (await fresh.Settings.SingleAsync(row => row.Key == marker)).Value);
            Assert.Equal("also-preserved", (await fresh.Settings.SingleAsync(row => row.Key == marker + ".Other")).Value);
            Assert.Null(await fresh.Settings.FindAsync("Login.SavedUsername"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SettingsDeletion_PrivateMemoryConnectionKeepsTrackerAndHonorsCaseAndCancellation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options;
        await using var db = new LocalDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.Settings.Add(new LocalSetting { Key = "Editor.Test", Value = "saved" });
        await db.SaveChangesAsync();
        var pending = new LocalItem { Id = Guid.NewGuid(), NameOriginal = "not saved", IsDirty = true };
        db.Items.Add(pending);
        var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), new SessionState());
        Assert.Equal(0, await local.DeleteSettingsByPrefixAsync("editor."));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => local.DeleteSettingAsync("Editor.Test", new CancellationToken(true)));
        Assert.Equal("saved", await local.GetSettingAsync("Editor.Test"));
        await using (var transaction = await db.Database.BeginTransactionAsync())
            await Assert.ThrowsAsync<InvalidOperationException>(() => local.DeleteSettingAsync("Editor.Test"));
        Assert.True(await local.DeleteSettingAsync("Editor.Test"));
        Assert.Null(await local.GetSettingAsync("Editor.Test"));
        Assert.Equal(EntityState.Added, db.Entry(pending).State);
        Assert.False(await db.Items.AsNoTracking().AnyAsync());
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
    }

    [Theory]
    [InlineData("_")]
    [InlineData("%")]
    public async Task SettingsDeletion_PrefixIsLiteralAndDoesNotSaveUnrelatedBusinessEdit(string wildcard)
    {
        PrepareAppRoot("settings-deletion-literal");
        try
        {
            await using var db = CreateAuthenticationStorageBusinessFixture();
            await db.Database.EnsureCreatedAsync();
            db.Settings.AddRange(
                new LocalSetting { Key = "Editor" + wildcard + ".Target", Value = "delete" },
                new LocalSetting { Key = "EditorX.Target", Value = "preserve" });
            await db.SaveChangesAsync();
            var pending = new LocalItem { Id = Guid.NewGuid(), NameOriginal = "unsaved item", IsDirty = true };
            db.Items.Add(pending);
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), new SessionState());
            Assert.Equal(1, await local.DeleteSettingsByPrefixAsync("Editor" + wildcard + "."));
            await using var fresh = CreateAuthenticationStorageBusinessFixture();
            Assert.Equal("preserve", (await fresh.Settings.SingleAsync(row => row.Key == "EditorX.Target")).Value);
            Assert.False(await fresh.Items.AnyAsync());
            Assert.Equal(EntityState.Added, db.Entry(pending).State);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SettingsDeletion_CommonKeyDeletesOnlyCommonCopy()
    {
        PrepareAppRoot("settings-deletion-common-copy");
        try
        {
            var common = new CommonAuthenticationDatabase(Environment.GetEnvironmentVariable("GEORAEPLAN_APP_ROOT")!);
            await common.CreateNewAsync();
            await using var business = CreateAuthenticationStorageBusinessFixture();
            await business.Database.EnsureCreatedAsync();
            business.Settings.Add(new LocalSetting { Key = "Login.SavedUsername", Value = "old archive copy" });
            await business.SaveChangesAsync();
            var local = new LocalStateService(business, new OfficeAccessService(), new SyncRequestDispatcher(), new SessionState(), common);
            await local.SetSettingAsync("Login.SavedUsername", "current login");
            Assert.True(await local.DeleteSettingAsync("Login.SavedUsername"));
            Assert.Null(await local.GetSettingAsync("Login.SavedUsername"));
            await using var fresh = CreateAuthenticationStorageBusinessFixture();
            Assert.Equal("old archive copy", (await fresh.Settings.SingleAsync(row => row.Key == "Login.SavedUsername")).Value);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }
}
