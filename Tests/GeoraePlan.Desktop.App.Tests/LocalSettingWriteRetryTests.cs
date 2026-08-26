using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class LocalSettingWriteRetryTests
{
    [Fact]
    public async Task SetSettingAsync_RetriesTransientSqliteWriterLock()
    {
        var root = Path.Combine(
            FindRepositoryRoot(),
            "temp",
            "local-setting-write-retry-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "settings.db");
        var connectionString = $"Data Source={databasePath};Default Timeout=1;Pooling=False";

        try
        {
            var options = new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite(connectionString)
                .Options;
            await using (var initializer = new LocalDbContext(options))
                await initializer.Database.EnsureCreatedAsync();

            await using var lockConnection = new SqliteConnection(connectionString);
            await lockConnection.OpenAsync();
            await using var lockTransaction =
                (SqliteTransaction)await lockConnection.BeginTransactionAsync();
            await using (var lockCommand = lockConnection.CreateCommand())
            {
                lockCommand.Transaction = lockTransaction;
                lockCommand.CommandText =
                    "INSERT INTO Settings (Key, Value) VALUES ('WriterLock', 'held');";
                await lockCommand.ExecuteNonQueryAsync();
            }

            await using var db = new LocalDbContext(options);
            var local = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                new SessionState());

            var writeTask = Task.Run(
                () => local.SetSettingAsync("PeriodicIntegrity.LastRun", "saved"));
            await Task.Delay(1100);
            Assert.False(writeTask.IsCompleted);

            await lockTransaction.CommitAsync();
            await writeTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(
                "saved",
                await local.GetSettingAsync("PeriodicIntegrity.LastRun"));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Desktop")) &&
                Directory.Exists(Path.Combine(directory.FullName, "Tests")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
