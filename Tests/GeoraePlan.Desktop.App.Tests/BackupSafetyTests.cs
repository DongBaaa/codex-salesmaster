using Microsoft.Data.Sqlite;
using 거래플랜.Desktop.App.Services;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class BackupSafetyTests
{
    [Fact]
    public async Task CreateConsistentBackup_ValidatesStagingBeforePublishingFinalFile()
    {
        using var scope = new TemporaryDirectory();
        var sourcePath = Path.Combine(scope.Path, "source.db");
        var backupDirectory = Path.Combine(scope.Path, "backup");
        var destinationPath = Path.Combine(backupDirectory, "legacy-v1.db");
        await CreateValidDatabaseAsync(sourcePath, "source");

        await BackupService.CreateConsistentSqliteBackupAsync(
            sourcePath,
            destinationPath,
            CancellationToken.None);

        Assert.True(File.Exists(destinationPath));
        Assert.True(BackupService.IsVerifiedSqliteDatabase(destinationPath));
        Assert.Equal("source", await ReadStateValueAsync(destinationPath));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(backupDirectory),
            path => Path.GetFileName(path).Contains("backup-staging", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CreateConsistentBackup_CorruptSourceDoesNotPublishPartialBackup()
    {
        using var scope = new TemporaryDirectory();
        var sourcePath = Path.Combine(scope.Path, "corrupt-source.db");
        var backupDirectory = Path.Combine(scope.Path, "backup");
        var destinationPath = Path.Combine(backupDirectory, "must-not-exist.db");
        Directory.CreateDirectory(backupDirectory);
        await File.WriteAllTextAsync(sourcePath, "not a sqlite database");

        await Assert.ThrowsAnyAsync<Exception>(() =>
            BackupService.CreateConsistentSqliteBackupAsync(
                sourcePath,
                destinationPath,
                CancellationToken.None));

        Assert.False(File.Exists(destinationPath));
        Assert.Empty(Directory.EnumerateFiles(backupDirectory));
    }

    [Fact]
    public async Task CreateConsistentBackup_DoesNotOverwriteAnExistingPublishedBackup()
    {
        using var scope = new TemporaryDirectory();
        var sourcePath = Path.Combine(scope.Path, "source.db");
        var destinationPath = Path.Combine(scope.Path, "existing.db");
        await CreateValidDatabaseAsync(sourcePath, "new");
        await CreateValidDatabaseAsync(destinationPath, "existing");

        await Assert.ThrowsAsync<IOException>(() =>
            BackupService.CreateConsistentSqliteBackupAsync(
                sourcePath,
                destinationPath,
                CancellationToken.None));

        Assert.Equal("existing", await ReadStateValueAsync(destinationPath));
        Assert.True(BackupService.IsVerifiedSqliteDatabase(destinationPath));
    }

    [Fact]
    public async Task VerifiedBackupListing_KeepsLegacyDbAndExcludesPartialAndCorruptFiles()
    {
        using var scope = new TemporaryDirectory();
        var validLegacyPath = Path.Combine(scope.Path, "salesmaster-legacy.db");
        var partialPath = Path.Combine(scope.Path, "salesmaster-copy.partial.db");
        var corruptPath = Path.Combine(scope.Path, "salesmaster-corrupt.db");
        await CreateValidDatabaseAsync(validLegacyPath, "legacy");
        File.Copy(validLegacyPath, partialPath);
        await File.WriteAllTextAsync(corruptPath, "corrupt");

        var backups = BackupService.GetVerifiedPublishedBackupFiles(scope.Path);

        var backup = Assert.Single(backups);
        Assert.Equal(validLegacyPath, backup.FullName);
    }

    [Fact]
    public async Task Validation_RejectsForeignKeyViolations()
    {
        using var scope = new TemporaryDirectory();
        var databasePath = Path.Combine(scope.Path, "foreign-key-violation.db");
        await CreateDatabaseWithForeignKeyViolationAsync(databasePath);

        Assert.False(BackupService.IsVerifiedSqliteDatabase(databasePath));
        Assert.Throws<InvalidDataException>(
            () => BackupService.ValidateSqliteDatabaseOrThrow(databasePath));
    }

    [Fact]
    public async Task PendingRestore_ValidBackupReplacesDatabaseAndRemovesMarkerOnlyAfterSuccess()
    {
        using var scope = new TemporaryDirectory();
        var backupDirectory = Path.Combine(scope.Path, "backup");
        var dataDirectory = Path.Combine(scope.Path, "data");
        var tempDirectory = Path.Combine(scope.Path, "temp");
        Directory.CreateDirectory(backupDirectory);
        Directory.CreateDirectory(dataDirectory);
        Directory.CreateDirectory(tempDirectory);

        var currentPath = Path.Combine(dataDirectory, "거래플랜.db");
        var backupPath = Path.Combine(backupDirectory, "salesmaster-legacy-v1.db");
        var markerPath = Path.Combine(tempDirectory, "pending-db-restore.txt");
        await CreateValidDatabaseAsync(currentPath, "current");
        await CreateValidDatabaseAsync(backupPath, "restored");
        await File.WriteAllTextAsync(markerPath, backupPath);

        var result = BackupService.TryApplyPendingRestoreOnStartup(
            markerPath,
            backupDirectory,
            currentPath);

        Assert.NotNull(result);
        Assert.Contains("백업 복원이 적용되었습니다", result);
        Assert.False(File.Exists(markerPath));
        Assert.Equal("restored", await ReadStateValueAsync(currentPath));
        Assert.True(BackupService.IsVerifiedSqliteDatabase(currentPath));

        var beforeRestorePath = Assert.Single(
            Directory.EnumerateFiles(
                backupDirectory,
                "거래플랜_before_restore_*.gpbackup",
                SearchOption.TopDirectoryOnly));
        Assert.True(BackupService.IsVerifiedBackupArtifact(beforeRestorePath));
    }

    [Fact]
    public async Task PendingRestore_InvalidBackupKeepsMarkerAndCurrentDatabaseUntouched()
    {
        using var scope = new TemporaryDirectory();
        var backupDirectory = Path.Combine(scope.Path, "backup");
        var dataDirectory = Path.Combine(scope.Path, "data");
        var tempDirectory = Path.Combine(scope.Path, "temp");
        Directory.CreateDirectory(backupDirectory);
        Directory.CreateDirectory(dataDirectory);
        Directory.CreateDirectory(tempDirectory);

        var currentPath = Path.Combine(dataDirectory, "거래플랜.db");
        var corruptBackupPath = Path.Combine(backupDirectory, "corrupt.db");
        var markerPath = Path.Combine(tempDirectory, "pending-db-restore.txt");
        await CreateValidDatabaseAsync(currentPath, "current");
        await File.WriteAllTextAsync(corruptBackupPath, "corrupt");
        await File.WriteAllTextAsync(markerPath, corruptBackupPath);
        var originalBytes = await File.ReadAllBytesAsync(currentPath);

        var result = BackupService.TryApplyPendingRestoreOnStartup(
            markerPath,
            backupDirectory,
            currentPath);

        Assert.NotNull(result);
        Assert.Contains("현재 데이터와 복원 예약은 변경하지 않았습니다", result);
        Assert.True(File.Exists(markerPath));
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(currentPath));
        Assert.Equal("current", await ReadStateValueAsync(currentPath));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(backupDirectory),
            path => Path.GetFileName(path).StartsWith(
                "거래플랜_before_restore_",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Retention_PreservesPendingSourceAndNewestVerifiedBackup()
    {
        using var scope = new TemporaryDirectory();
        var backupDirectory = Path.Combine(scope.Path, "backup");
        var markerPath = Path.Combine(scope.Path, "pending-db-restore.txt");
        Directory.CreateDirectory(backupDirectory);

        var pendingPath = Path.Combine(backupDirectory, "거래플랜_20260101_010101_001.db");
        var newestValidPath = Path.Combine(backupDirectory, "거래플랜_20260102_010101_001.db");
        var newerCorruptPath = Path.Combine(backupDirectory, "거래플랜_20260103_010101_001.db");
        await CreateValidDatabaseAsync(pendingPath, "pending");
        await CreateValidDatabaseAsync(newestValidPath, "newest-valid");
        await File.WriteAllTextAsync(newerCorruptPath, "corrupt");

        File.SetLastWriteTime(pendingPath, DateTime.Now.AddDays(-42));
        File.SetLastWriteTime(newestValidPath, DateTime.Now.AddDays(-41));
        File.SetLastWriteTime(newerCorruptPath, DateTime.Now.AddDays(-40));
        await File.WriteAllTextAsync(markerPath, pendingPath);

        BackupService.TrimManagedBackups(backupDirectory, markerPath);

        Assert.True(File.Exists(pendingPath));
        Assert.True(File.Exists(newestValidPath));
        Assert.False(File.Exists(newerCorruptPath));
    }

    [Fact]
    public async Task Retention_IndeterminateLockedBackupIsNeverDeletedAsCorrupt()
    {
        using var scope = new TemporaryDirectory();
        var backupDirectory = Path.Combine(scope.Path, "backup");
        var markerPath = Path.Combine(scope.Path, "pending-db-restore.txt");
        var lockedBackupPath = Path.Combine(
            backupDirectory,
            "거래플랜_20260101_010101_001.db");
        var corruptBackupPath = Path.Combine(
            backupDirectory,
            "거래플랜_20260102_010101_001.db");
        await CreateValidDatabaseAsync(lockedBackupPath, "locked");
        await File.WriteAllTextAsync(corruptBackupPath, "corrupt");
        File.SetLastWriteTime(lockedBackupPath, DateTime.Now.AddDays(-45));
        File.SetLastWriteTime(corruptBackupPath, DateTime.Now.AddDays(-44));

        using (new FileStream(
                   lockedBackupPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.None))
        {
            BackupService.TrimManagedBackups(backupDirectory, markerPath);

            Assert.True(File.Exists(lockedBackupPath));
            Assert.False(File.Exists(corruptBackupPath));
        }
    }

    private static async Task CreateValidDatabaseAsync(string path, string stateValue)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA foreign_keys=ON;
            PRAGMA application_id=1196444750;
            CREATE TABLE Parent (Id INTEGER PRIMARY KEY);
            CREATE TABLE Child (
                Id INTEGER PRIMARY KEY,
                ParentId INTEGER NOT NULL REFERENCES Parent(Id)
            );
            CREATE TABLE State (Value TEXT NOT NULL);
            CREATE TABLE Settings ("Key" TEXT PRIMARY KEY, "Value" TEXT NOT NULL);
            CREATE TABLE Customers (Id TEXT PRIMARY KEY);
            CREATE TABLE Items (Id TEXT PRIMARY KEY);
            CREATE TABLE Invoices (Id TEXT PRIMARY KEY);
            CREATE TABLE Payments (Id TEXT PRIMARY KEY);
            CREATE TABLE Transactions (Id TEXT PRIMARY KEY);
            CREATE TABLE TransactionAttachments (
                Id TEXT PRIMARY KEY,
                TransactionId TEXT NOT NULL,
                StoredPath TEXT NOT NULL,
                FileSize INTEGER NOT NULL DEFAULT 0,
                FileHash TEXT NOT NULL DEFAULT '',
                IsDeleted INTEGER NOT NULL DEFAULT 0
            );
            INSERT INTO Parent (Id) VALUES (1);
            INSERT INTO Child (Id, ParentId) VALUES (1, 1);
            INSERT INTO State (Value) VALUES ($stateValue);
            """;
        command.Parameters.AddWithValue("$stateValue", stateValue);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CreateDatabaseWithForeignKeyViolationAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA foreign_keys=OFF;
            CREATE TABLE Parent (Id INTEGER PRIMARY KEY);
            CREATE TABLE Child (
                Id INTEGER PRIMARY KEY,
                ParentId INTEGER NOT NULL REFERENCES Parent(Id)
            );
            INSERT INTO Child (Id, ParentId) VALUES (1, 999);
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ReadStateValueAsync(string path)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={path};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM State LIMIT 1;";
        return (await command.ExecuteScalarAsync())?.ToString();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"georaeplan-backup-safety-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
