using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class SyncMetadataFreshReadTests
{
    [Theory]
    [InlineData("LastSyncRevision", false)]
    [InlineData("LastSyncRevision", true)]
    [InlineData("Sync.LastSuccessAt", false)]
    [InlineData("Sync.LastSuccessAt", true)]
    [InlineData("Sync.PendingFullMirrorRefresh", false)]
    [InlineData("Sync.PendingFullMirrorRefresh", true)]
    public async Task Read_ReflectsOtherContextCommit_WithoutChangingPendingEdits(string key, bool delete)
    {
        var path = Path.Combine(Path.GetTempPath(), $"georaeplan-metadata-read-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite($"Data Source={path};Pooling=False").Options;
            await using var readerDb = new LocalDbContext(options);
            await readerDb.Database.EnsureCreatedAsync();
            readerDb.Settings.AddRange(new LocalSetting { Key = key, Value = "1" },
                new LocalSetting { Key = "Editor.TestPreference", Value = "saved" });
            await readerDb.SaveChangesAsync();
            // Keep the tracked metadata entity, as an earlier setting write does in the app.
            var tracked = await readerDb.Settings.FindAsync(key);
            var preference = await readerDb.Settings.FindAsync("Editor.TestPreference");
            preference!.Value = "pending";
            var pendingAdded = new LocalSetting { Key = "Editor.NewPreference", Value = "pending-new" };
            readerDb.Settings.Add(pendingAdded);
            var reader = CreateLocal(readerDb);
            Assert.Equal("1", await reader.GetSettingAsync(key));

            await using (var writerDb = new LocalDbContext(options))
            {
                var writer = CreateLocal(writerDb);
                if (delete)
                    await writer.DeleteSettingAsync(key);
                else
                    await writer.SetSettingAsync(key, "2");
            }

            Assert.Equal(delete ? null : "2", await reader.GetSettingAsync(key));
            Assert.Equal("1", tracked!.Value);
            Assert.Equal(EntityState.Unchanged, readerDb.Entry(tracked).State);
            Assert.Equal("pending", await reader.GetSettingAsync("Editor.TestPreference"));
            Assert.Equal(EntityState.Modified, readerDb.Entry(preference).State);
            Assert.Equal(EntityState.Added, readerDb.Entry(pendingAdded).State);
            await using var verificationDb = new LocalDbContext(options);
            Assert.Equal("saved", (await verificationDb.Settings.FindAsync("Editor.TestPreference"))!.Value);
            Assert.Null(await verificationDb.Settings.FindAsync("Editor.NewPreference"));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
        }
    }

    [Fact]
    public async Task MirrorRefreshRead_SeesIndependentClearAndNewRequest()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options;
        await using var readerDb = new LocalDbContext(options);
        await readerDb.Database.EnsureCreatedAsync();
        var reader = CreateLocal(readerDb);
        await reader.MarkServerMirrorRefreshRequiredAsync();
        Assert.True(await reader.IsServerMirrorRefreshRequiredAsync());
        await using var writerDb = new LocalDbContext(options);
        var writer = CreateLocal(writerDb);
        await writer.ClearServerMirrorRefreshRequiredAsync();
        Assert.False(await reader.IsServerMirrorRefreshRequiredAsync());
        await writer.MarkServerMirrorRefreshRequiredAsync();
        Assert.True(await reader.IsServerMirrorRefreshRequiredAsync());
    }

    [Theory]
    [InlineData("LastSyncRevision")]
    [InlineData("Sync.LastSuccessAt")]
    [InlineData("Sync.PendingFullMirrorRefresh")]
    public async Task Read_UsesPersistedValueAndHonorsCancellation(string key)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options;
        await using var db = new LocalDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var local = CreateLocal(db);
        Assert.Null(await local.GetSettingAsync(key));
        await local.SetSettingAsync(key, "saved");
        var tracked = await db.Settings.FindAsync(key);
        tracked!.Value = "not-committed";
        Assert.Equal("saved", await local.GetSettingAsync(key));
        Assert.Equal("not-committed", tracked.Value);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => local.GetSettingAsync(key, cancelled.Token));
    }

    private static LocalStateService CreateLocal(LocalDbContext db)
        => new(db, new OfficeAccessService(), new SyncRequestDispatcher(), new SessionState());
}
