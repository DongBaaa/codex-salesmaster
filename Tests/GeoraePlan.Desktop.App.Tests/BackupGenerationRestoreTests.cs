using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using 거래플랜.Desktop.App.Services;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class BackupGenerationRestoreTests
{
    [Fact]
    public async Task BackupNow_DispatchesDatabaseSnapshotAndCompressionAwayFromCallingUiThread()
    {
        var testSourcePath = GetTestSourcePath();
        var repositoryRoot = Path.GetFullPath(
            Path.Combine(Path.GetDirectoryName(testSourcePath)!, "..", ".."));
        var serviceSource = await File.ReadAllTextAsync(
            Path.Combine(
                repositoryRoot,
                "Desktop",
                "거래플랜.Desktop.App",
                "Services",
                "BackupService.cs"));
        var backupMethodStart = serviceSource.IndexOf(
            "public async Task<string?> BackupNowWithPathAsync(",
            StringComparison.Ordinal);
        var backupMethodEnd = serviceSource.IndexOf(
            "public IReadOnlyList<BackupSnapshotInfo> GetBackupSnapshots()",
            backupMethodStart,
            StringComparison.Ordinal);
        Assert.True(backupMethodStart >= 0 && backupMethodEnd > backupMethodStart);
        var backupMethodSource = serviceSource[backupMethodStart..backupMethodEnd];
        Assert.Contains(
            "RunBackupWorkOffUiThreadAsync",
            backupMethodSource,
            StringComparison.Ordinal);

        var helper = typeof(BackupService)
            .GetMethods(System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Static)
            .SingleOrDefault(method =>
                string.Equals(
                    method.Name,
                    "RunBackupWorkOffUiThreadAsync",
                    StringComparison.Ordinal) &&
                method.IsGenericMethodDefinition);
        Assert.NotNull(helper);

        var completion = new TaskCompletionSource<(int Caller, int Worker, int Value)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var uiThread = new Thread(() =>
        {
            try
            {
                var callerThreadId = Environment.CurrentManagedThreadId;
                Func<Task<int>> work = async () =>
                {
                    var workerThreadId = Environment.CurrentManagedThreadId;
                    await Task.Yield();
                    return workerThreadId;
                };
                var closedHelper = helper!.MakeGenericMethod(typeof(int));
                var task = Assert.IsAssignableFrom<Task<int>>(
                    closedHelper.Invoke(
                        null,
                        [work, CancellationToken.None]));
                var workerThreadId = task.GetAwaiter().GetResult();
                completion.TrySetResult((callerThreadId, workerThreadId, 1));
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "backup-ui-responsiveness-contract"
        };
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.Start();

        var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, result.Value);
        Assert.NotEqual(result.Caller, result.Worker);
        Assert.True(uiThread.Join(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task PackageBackup_CrossRootRestoreRewritesStoredPathAndRestoresOneGeneration()
    {
        using var scope = new TemporaryDirectory();
        var backupDirectory = Path.Combine(scope.Path, "backup");
        var sourceDatabase = Path.Combine(scope.Path, "source", "거래플랜.db");
        var sourceAttachments = Path.Combine(scope.Path, "source", "attachments");
        var currentDatabase = Path.Combine(scope.Path, "current", "거래플랜.db");
        var currentAttachments = Path.Combine(scope.Path, "current", "attachments");
        var packagePath = Path.Combine(backupDirectory, "거래플랜_20260723_010101_001.gpbackup");

        await CreateTradePlanDatabaseAsync(sourceDatabase, "restored");
        await CreateTradePlanDatabaseAsync(currentDatabase, "current");
        await WriteAttachmentRecordAsync(
            sourceDatabase,
            sourceAttachments,
            "tx-a/source.txt",
            "source-generation");
        await WriteAttachmentRecordAsync(
            currentDatabase,
            currentAttachments,
            "tx-old/old.txt",
            "old-generation");

        await BackupService.CreateConsistentBackupPackageAsync(
            sourceDatabase,
            sourceAttachments,
            packagePath,
            CancellationToken.None);

        Assert.True(BackupService.IsVerifiedBackupArtifact(packagePath));
        BackupService.RestoreBackupArtifact(
            packagePath,
            currentDatabase,
            currentAttachments,
            backupDirectory);

        Assert.Equal("restored", await ReadStateValueAsync(currentDatabase));
        Assert.Equal(
            "source-generation",
            await File.ReadAllTextAsync(Path.Combine(currentAttachments, "tx-a", "source.txt")));
        Assert.Equal(
            Path.Combine(currentAttachments, "tx-a", "source.txt"),
            await ReadStoredPathAsync(currentDatabase));
        Assert.False(Path.Exists(Path.Combine(currentAttachments, "tx-old", "old.txt")));

        var beforeRestorePackage = Assert.Single(
            Directory.EnumerateFiles(
                backupDirectory,
                "거래플랜_before_restore_*.gpbackup",
                SearchOption.TopDirectoryOnly));
        Assert.True(BackupService.IsVerifiedBackupArtifact(beforeRestorePackage));
    }

    [Fact]
    public async Task PackageBackup_ConflictOwnedInventoryTransferEvidence_RestoresBytesAndRewritesPath()
    {
        using var scope = new TemporaryDirectory();
        var backupDirectory = Path.Combine(scope.Path, "backup");
        var sourceDatabase = Path.Combine(scope.Path, "source", "거래플랜.db");
        var sourceAttachments = Path.Combine(scope.Path, "source", "attachments");
        var currentDatabase = Path.Combine(scope.Path, "current", "거래플랜.db");
        var currentAttachments = Path.Combine(scope.Path, "current", "attachments");
        var packagePath = Path.Combine(
            backupDirectory,
            "거래플랜_20260802_050000_001.gpbackup");
        var transferId = Guid.NewGuid();
        var relativePath = Path.Combine(
            ".inventory-transfer-conflicts",
            transferId.ToString("N"),
            "receive-evidence.bin");
        var sourceEvidencePath = Path.Combine(
            sourceAttachments,
            relativePath);
        var expected = RandomNumberGenerator.GetBytes(4096);

        await CreateTradePlanDatabaseAsync(sourceDatabase, "restored");
        await CreateTradePlanDatabaseAsync(currentDatabase, "current");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceEvidencePath)!);
        await File.WriteAllBytesAsync(sourceEvidencePath, expected);
        await using (var connection = new SqliteConnection(
                         $"Data Source={sourceDatabase};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE "InventoryTransferTombstoneConflicts" (
                    "TransferId" TEXT NOT NULL,
                    "ArchivedReceiveEvidencePath" TEXT NOT NULL DEFAULT ''
                );
                INSERT INTO "InventoryTransferTombstoneConflicts" (
                    "TransferId",
                    "ArchivedReceiveEvidencePath")
                VALUES ($transferId, $storedPath);
                """;
            command.Parameters.AddWithValue(
                "$transferId",
                transferId.ToString("D"));
            command.Parameters.AddWithValue(
                "$storedPath",
                sourceEvidencePath);
            await command.ExecuteNonQueryAsync();
        }

        await BackupService.CreateConsistentBackupPackageAsync(
            sourceDatabase,
            sourceAttachments,
            packagePath,
            CancellationToken.None);
        BackupService.RestoreBackupArtifact(
            packagePath,
            currentDatabase,
            currentAttachments,
            backupDirectory);

        var restoredEvidencePath = Path.Combine(
            currentAttachments,
            relativePath);
        Assert.Equal(expected, await File.ReadAllBytesAsync(restoredEvidencePath));
        await using var restoredConnection = new SqliteConnection(
            $"Data Source={currentDatabase};Pooling=False");
        await restoredConnection.OpenAsync();
        await using var read = restoredConnection.CreateCommand();
        read.CommandText =
            "SELECT \"ArchivedReceiveEvidencePath\" " +
            "FROM \"InventoryTransferTombstoneConflicts\" " +
            "WHERE \"TransferId\" = $transferId;";
        read.Parameters.AddWithValue(
            "$transferId",
            transferId.ToString("D"));
        Assert.Equal(
            restoredEvidencePath,
            (string?)await read.ExecuteScalarAsync());
    }

    [Fact]
    public async Task PackageRestore_DeletedAttachmentOutsideSourceRoot_IsSanitizedAndIgnored()
    {
        using var scope = new TemporaryDirectory();
        var backupDirectory = Path.Combine(scope.Path, "backup");
        var sourceDatabase = Path.Combine(scope.Path, "source", "거래플랜.db");
        var sourceAttachments = Path.Combine(scope.Path, "source", "attachments");
        var currentDatabase = Path.Combine(scope.Path, "current", "거래플랜.db");
        var currentAttachments = Path.Combine(scope.Path, "current", "attachments");
        var packagePath = Path.Combine(backupDirectory, "거래플랜_20260723_011111_001.gpbackup");
        var deletedAttachmentId = Guid.NewGuid().ToString();
        await CreateTradePlanDatabaseAsync(sourceDatabase, "restored");
        await CreateTradePlanDatabaseAsync(currentDatabase, "current");
        await using (var connection = new SqliteConnection(
                         $"Data Source={sourceDatabase};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO TransactionAttachments (
                    Id,
                    TransactionId,
                    StoredPath,
                    FileSize,
                    FileHash,
                    IsDeleted
                )
                VALUES (
                    $id,
                    $transactionId,
                    $storedPath,
                    123,
                    $fileHash,
                    1
                );
                """;
            command.Parameters.AddWithValue("$id", deletedAttachmentId);
            command.Parameters.AddWithValue("$transactionId", Guid.NewGuid().ToString());
            command.Parameters.AddWithValue(
                "$storedPath",
                Path.Combine(scope.Path, "legacy-outside", "deleted.txt"));
            command.Parameters.AddWithValue("$fileHash", new string('a', 64));
            await command.ExecuteNonQueryAsync();
        }
        await BackupService.CreateConsistentBackupPackageAsync(
            sourceDatabase,
            sourceAttachments,
            packagePath,
            CancellationToken.None);
        Assert.True(BackupService.IsVerifiedBackupArtifact(packagePath));

        BackupService.RestoreBackupArtifact(
            packagePath,
            currentDatabase,
            currentAttachments,
            backupDirectory);

        Assert.Equal("restored", await ReadStateValueAsync(currentDatabase));
        var deletedMetadata = await ReadAttachmentMetadataAsync(
            currentDatabase,
            deletedAttachmentId);
        Assert.Equal(string.Empty, deletedMetadata.StoredPath);
        Assert.Equal(0, deletedMetadata.FileSize);
        Assert.Equal(string.Empty, deletedMetadata.FileHash);
        Assert.Empty(Directory.EnumerateFileSystemEntries(currentAttachments));
    }

    [Fact]
    public async Task PackageBackup_AttachmentTamperInvalidatesWholeGeneration()
    {
        using var scope = new TemporaryDirectory();
        var databasePath = Path.Combine(scope.Path, "source", "거래플랜.db");
        var attachmentsPath = Path.Combine(scope.Path, "source", "attachments");
        var packagePath = Path.Combine(scope.Path, "거래플랜_20260723_020202_002.gpbackup");
        await CreateTradePlanDatabaseAsync(databasePath, "source");
        await WriteAttachmentRecordAsync(
            databasePath,
            attachmentsPath,
            "tx-a/proof.txt",
            "original");
        await BackupService.CreateConsistentBackupPackageAsync(
            databasePath,
            attachmentsPath,
            packagePath,
            CancellationToken.None);

        using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Update))
        {
            var entry = archive.GetEntry("attachments/tx-a/proof.txt");
            Assert.NotNull(entry);
            entry.Delete();
            var replacement = archive.CreateEntry("attachments/tx-a/proof.txt");
            await using var writer = new StreamWriter(replacement.Open());
            await writer.WriteAsync("tampered");
        }

        Assert.False(BackupService.IsVerifiedBackupArtifact(packagePath));
    }

    [Fact]
    public async Task PackageBackup_ConcurrentAttachmentGenerationMismatchRetriesAndNeverPublishes()
    {
        using var scope = new TemporaryDirectory();
        var databasePath = Path.Combine(scope.Path, "source", "거래플랜.db");
        var attachmentsPath = Path.Combine(scope.Path, "source", "attachments");
        var relativePath = "tx-a/proof.txt";
        var attachmentPath = Path.Combine(
            attachmentsPath,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        var packagePath = Path.Combine(scope.Path, "거래플랜_20260723_021212_002.gpbackup");
        await CreateTradePlanDatabaseAsync(databasePath, "source");
        await WriteAttachmentRecordAsync(
            databasePath,
            attachmentsPath,
            relativePath,
            "snapshot-generation");
        var snapshotAttempts = 0;

        await Assert.ThrowsAnyAsync<IOException>(() =>
            BackupService.CreateConsistentBackupPackageAsync(
                databasePath,
                attachmentsPath,
                packagePath,
                CancellationToken.None,
                async attempt =>
                {
                    snapshotAttempts = attempt;
                    await File.WriteAllTextAsync(
                        attachmentPath,
                        $"concurrent-change-{attempt}");
                }));

        Assert.Equal(3, snapshotAttempts);
        Assert.False(File.Exists(packagePath));
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(scope.Path, "*", SearchOption.AllDirectories),
            path => Path.GetFileName(path).StartsWith(".gp-", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PackageBackup_UnsignedManifestCannotReplaceMissingTradePlanApplicationId()
    {
        using var scope = new TemporaryDirectory();
        var packageRoot = Path.Combine(scope.Path, "crafted");
        var databasePath = Path.Combine(packageRoot, "database.db");
        var attachmentsPath = Path.Combine(packageRoot, "attachments");
        var packagePath = Path.Combine(scope.Path, "거래플랜_20260723_022222_002.gpbackup");
        Directory.CreateDirectory(attachmentsPath);
        await CreateTradePlanDatabaseAsync(databasePath, "crafted");
        await using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA application_id=0;";
            await command.ExecuteNonQueryAsync();
        }

        var manifest = new
        {
            SchemaVersion = 2,
            GenerationId = Guid.NewGuid().ToString("N"),
            CreatedAtUtc = DateTime.UtcNow,
            DatabasePath = "database.db",
            DatabaseSize = new FileInfo(databasePath).Length,
            DatabaseSha256 = ComputeSha256(databasePath),
            SourceAttachmentRoot = attachmentsPath,
            Attachments = Array.Empty<object>()
        };
        await File.WriteAllTextAsync(
            Path.Combine(packageRoot, "manifest.json"),
            JsonSerializer.Serialize(manifest));
        ZipFile.CreateFromDirectory(packageRoot, packagePath);

        Assert.False(BackupService.IsVerifiedBackupArtifact(packagePath));
    }

    [Fact]
    public async Task PackageBackup_HighExpansionZipBombFailsPreflightBeforeExtraction()
    {
        using var scope = new TemporaryDirectory();
        var packagePath = Path.Combine(scope.Path, "거래플랜_20260723_zipbomb.gpbackup");
        await using (var packageStream = new FileStream(
                         packagePath,
                         FileMode.CreateNew,
                         FileAccess.ReadWrite,
                         FileShare.None))
        using (var archive = new ZipArchive(
                   packageStream,
                   ZipArchiveMode.Create,
                   leaveOpen: false))
        {
            var manifestEntry = archive.CreateEntry(
                "manifest.json",
                CompressionLevel.Optimal);
            await using (var manifestWriter = new StreamWriter(manifestEntry.Open()))
                await manifestWriter.WriteAsync("{}");

            var databaseEntry = archive.CreateEntry(
                "database.db",
                CompressionLevel.SmallestSize);
            await using var databaseStream = databaseEntry.Open();
            var zeroBlock = new byte[64 * 1024];
            for (var i = 0; i < 128; i++)
                await databaseStream.WriteAsync(zeroBlock);
        }

        Assert.False(BackupService.IsBackupPackageArchiveWithinBounds(packagePath));
        Assert.False(BackupService.IsVerifiedBackupArtifact(packagePath));
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(scope.Path, "*", SearchOption.TopDirectoryOnly),
            path => Path.GetFileName(path).StartsWith(".gp-validate-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LegacyDatabase_RejectsArbitraryHealthySqliteFile()
    {
        using var scope = new TemporaryDirectory();
        var arbitraryDatabase = Path.Combine(scope.Path, "salesmaster-arbitrary.db");
        await using (var connection = new SqliteConnection($"Data Source={arbitraryDatabase};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE Notes (Id INTEGER PRIMARY KEY, Value TEXT);";
            await command.ExecuteNonQueryAsync();
        }

        Assert.True(BackupService.IsVerifiedSqliteDatabase(arbitraryDatabase));
        Assert.False(BackupService.IsVerifiedBackupArtifact(arbitraryDatabase));
        Assert.Empty(BackupService.GetVerifiedPublishedBackupFiles(scope.Path));
    }

    [Fact]
    public async Task LegacyDatabase_AllowsOnlyManagedSchemaCompatibleDatabaseWithoutAttachments()
    {
        using var scope = new TemporaryDirectory();
        var legacyDatabase = Path.Combine(scope.Path, "salesmaster-20250101.db");
        await CreateTradePlanDatabaseAsync(legacyDatabase, "legacy");
        await using (var connection = new SqliteConnection($"Data Source={legacyDatabase};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA application_id=0;";
            await command.ExecuteNonQueryAsync();
        }

        Assert.True(BackupService.IsVerifiedBackupArtifact(legacyDatabase));

        var wrongApplicationIdCopy = Path.Combine(scope.Path, "salesmaster-wrong-id.db");
        File.Copy(legacyDatabase, wrongApplicationIdCopy);
        await using (var connection = new SqliteConnection(
                         $"Data Source={wrongApplicationIdCopy};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA application_id=12345;";
            await command.ExecuteNonQueryAsync();
        }
        Assert.False(BackupService.IsVerifiedBackupArtifact(wrongApplicationIdCopy));

        var unmanagedCopy = Path.Combine(scope.Path, "renamed-arbitrary.db");
        File.Copy(legacyDatabase, unmanagedCopy);
        Assert.False(BackupService.IsVerifiedBackupArtifact(unmanagedCopy));
    }

    [Fact]
    public async Task LegacyDatabase_WithoutTransactionAttachmentsTable_RestoresSuccessfully()
    {
        using var scope = new TemporaryDirectory();
        var backupDirectory = Path.Combine(scope.Path, "backup");
        var legacyDatabase = Path.Combine(backupDirectory, "salesmaster-20240101.db");
        var currentDatabase = Path.Combine(scope.Path, "current", "거래플랜.db");
        var currentAttachments = Path.Combine(scope.Path, "current", "attachments");
        await CreateTradePlanDatabaseAsync(legacyDatabase, "legacy-without-attachments-table");
        await CreateTradePlanDatabaseAsync(currentDatabase, "current");
        await using (var connection = new SqliteConnection(
                         $"Data Source={legacyDatabase};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                DROP TABLE TransactionAttachments;
                PRAGMA application_id=0;
                """;
            await command.ExecuteNonQueryAsync();
        }

        Assert.True(BackupService.IsVerifiedBackupArtifact(legacyDatabase));

        BackupService.RestoreBackupArtifact(
            legacyDatabase,
            currentDatabase,
            currentAttachments,
            backupDirectory);

        Assert.Equal(
            "legacy-without-attachments-table",
            await ReadStateValueAsync(currentDatabase));
        Assert.False(await TableExistsAsync(
            currentDatabase,
            "TransactionAttachments"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(currentAttachments));
    }

    [Fact]
    public async Task CompletedProcessingMarker_NeverReappliesBackupEvenWhenCleanupFails()
    {
        using var scope = new TemporaryDirectory();
        var backupDirectory = Path.Combine(scope.Path, "backup");
        var packageDatabase = Path.Combine(scope.Path, "package-source", "거래플랜.db");
        var packageAttachments = Path.Combine(scope.Path, "package-source", "attachments");
        var packagePath = Path.Combine(backupDirectory, "거래플랜_20260723_030303_003.gpbackup");
        var currentDatabase = Path.Combine(scope.Path, "current", "거래플랜.db");
        var currentAttachments = Path.Combine(scope.Path, "current", "attachments");
        var markerPath = Path.Combine(scope.Path, "temp", "pending-db-restore.txt");
        var processingMarkerPath = markerPath + ".processing";
        await CreateTradePlanDatabaseAsync(packageDatabase, "must-not-reapply");
        await CreateTradePlanDatabaseAsync(currentDatabase, "current");
        await BackupService.CreateConsistentBackupPackageAsync(
            packageDatabase,
            packageAttachments,
            packagePath,
            CancellationToken.None);
        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
        await File.WriteAllTextAsync(
            processingMarkerPath,
            $$"""{"backupPath":"{{packagePath.Replace("\\", "\\\\")}}","state":"Completed"}""");

        using (new FileStream(
                   processingMarkerPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read))
        {
            var result = BackupService.ApplyPendingRestoreOnStartup(
                markerPath,
                backupDirectory,
                currentDatabase,
                currentAttachments);

            Assert.False(result.StartupBlocked);
            Assert.Contains("다시 적용하지 않았습니다", result.Message);
            Assert.Equal("current", await ReadStateValueAsync(currentDatabase));
        }
    }

    [Fact]
    public async Task CompletedProcessingMarker_ArtifactCleanupFailureKeepsMarkerAndRetriesNextStart()
    {
        using var scope = new TemporaryDirectory();
        var backupDirectory = Path.Combine(scope.Path, "backup");
        var currentDatabase = Path.Combine(scope.Path, "current", "거래플랜.db");
        var currentAttachments = Path.Combine(scope.Path, "current", "attachments");
        var markerPath = Path.Combine(scope.Path, "temp", "pending-db-restore.txt");
        var processingMarkerPath = markerPath + ".processing";
        var operationId = Guid.NewGuid().ToString("N");
        var paths = BuildRecoveryPaths(currentDatabase, currentAttachments, operationId);
        await CreateTradePlanDatabaseAsync(currentDatabase, "current");
        Directory.CreateDirectory(currentAttachments);
        Directory.CreateDirectory(Path.GetDirectoryName(processingMarkerPath)!);
        await File.WriteAllTextAsync(
            processingMarkerPath,
            JsonSerializer.Serialize(new
            {
                backupPath = Path.Combine(backupDirectory, "already-applied.gpbackup"),
                state = "Completed",
                operationId,
                phase = "Completed",
                hadCurrentDatabase = true,
                hadCurrentAttachments = true
            }));
        await File.WriteAllTextAsync(paths.DatabaseRollbackPath, "rollback-artifact");

        using (new FileStream(
                   paths.DatabaseRollbackPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read))
        {
            var firstResult = BackupService.ApplyPendingRestoreOnStartup(
                markerPath,
                backupDirectory,
                currentDatabase,
                currentAttachments);

            Assert.False(firstResult.StartupBlocked);
            Assert.Contains("다음 시작 때 다시 정리", firstResult.Message);
            Assert.True(File.Exists(processingMarkerPath));
            Assert.True(File.Exists(paths.DatabaseRollbackPath));
            Assert.Equal("current", await ReadStateValueAsync(currentDatabase));
        }

        var retryResult = BackupService.ApplyPendingRestoreOnStartup(
            markerPath,
            backupDirectory,
            currentDatabase,
            currentAttachments);

        Assert.False(retryResult.StartupBlocked);
        Assert.False(File.Exists(processingMarkerPath));
        Assert.False(File.Exists(paths.DatabaseRollbackPath));
        Assert.Equal("current", await ReadStateValueAsync(currentDatabase));
    }

    [Fact]
    public async Task ApplyingProcessingMarker_BlocksStartupWithoutReapplyingBackup()
    {
        using var scope = new TemporaryDirectory();
        var backupDirectory = Path.Combine(scope.Path, "backup");
        var packagePath = Path.Combine(backupDirectory, "거래플랜_20260723_040404_004.gpbackup");
        var packageDatabase = Path.Combine(scope.Path, "package-source", "거래플랜.db");
        var currentDatabase = Path.Combine(scope.Path, "current", "거래플랜.db");
        var currentAttachments = Path.Combine(scope.Path, "current", "attachments");
        var markerPath = Path.Combine(scope.Path, "temp", "pending-db-restore.txt");
        await CreateTradePlanDatabaseAsync(packageDatabase, "must-not-reapply");
        await CreateTradePlanDatabaseAsync(currentDatabase, "current");
        await BackupService.CreateConsistentBackupPackageAsync(
            packageDatabase,
            Path.Combine(scope.Path, "package-source", "attachments"),
            packagePath,
            CancellationToken.None);
        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
        await File.WriteAllTextAsync(
            markerPath + ".processing",
            $$"""{"backupPath":"{{packagePath.Replace("\\", "\\\\")}}","state":"Applying"}""");

        var result = BackupService.ApplyPendingRestoreOnStartup(
            markerPath,
            backupDirectory,
            currentDatabase,
            currentAttachments);

        Assert.True(result.StartupBlocked);
        Assert.Equal("current", await ReadStateValueAsync(currentDatabase));
    }

    [Fact]
    public async Task ApplyingPendingMarker_BeforeMoveCrash_AutomaticallyCancelsPreparedRestore()
    {
        using var scope = new TemporaryDirectory();
        var backupDirectory = Path.Combine(scope.Path, "backup");
        var currentDatabase = Path.Combine(scope.Path, "current", "거래플랜.db");
        var currentAttachments = Path.Combine(scope.Path, "current", "attachments");
        var markerPath = Path.Combine(scope.Path, "temp", "pending-db-restore.txt");
        var operationId = Guid.NewGuid().ToString("N");
        await CreateTradePlanDatabaseAsync(currentDatabase, "current");
        Directory.CreateDirectory(currentAttachments);
        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
        await WriteApplyingMarkerAsync(
            markerPath,
            Path.Combine(backupDirectory, "source.gpbackup"),
            operationId,
            "Prepared",
            hadCurrentDatabase: true,
            hadCurrentAttachments: true);

        var result = BackupService.ApplyPendingRestoreOnStartup(
            markerPath,
            backupDirectory,
            currentDatabase,
            currentAttachments);

        Assert.False(result.StartupBlocked);
        Assert.Contains("자동 롤백", result.Message);
        Assert.Equal("current", await ReadStateValueAsync(currentDatabase));
        Assert.False(File.Exists(markerPath));
        Assert.False(File.Exists(markerPath + ".processing"));
    }

    [Fact]
    public async Task ApplyingJournal_AfterDatabaseSwitch_AutomaticallyRollsBackOriginalGeneration()
    {
        using var scope = new TemporaryDirectory();
        var backupDirectory = Path.Combine(scope.Path, "backup");
        var currentDatabase = Path.Combine(scope.Path, "current", "거래플랜.db");
        var currentAttachments = Path.Combine(scope.Path, "current", "attachments");
        var replacementDatabase = Path.Combine(scope.Path, "replacement", "거래플랜.db");
        var markerPath = Path.Combine(scope.Path, "temp", "pending-db-restore.txt");
        var processingMarkerPath = markerPath + ".processing";
        var operationId = Guid.NewGuid().ToString("N");
        var paths = BuildRecoveryPaths(currentDatabase, currentAttachments, operationId);

        await CreateTradePlanDatabaseAsync(currentDatabase, "original");
        await CreateTradePlanDatabaseAsync(replacementDatabase, "replacement");
        await WriteAttachmentRecordAsync(
            currentDatabase,
            currentAttachments,
            "old/original.txt",
            "original-attachment");
        Directory.CreateDirectory(Path.GetDirectoryName(paths.DatabaseRollbackPath)!);
        File.Copy(currentDatabase, paths.DatabaseRollbackPath);
        File.Copy(replacementDatabase, currentDatabase, overwrite: true);
        Directory.CreateDirectory(Path.GetDirectoryName(processingMarkerPath)!);
        await WriteApplyingMarkerAsync(
            processingMarkerPath,
            Path.Combine(backupDirectory, "source.gpbackup"),
            operationId,
            "DatabaseSwitched",
            hadCurrentDatabase: true,
            hadCurrentAttachments: true);

        var result = BackupService.ApplyPendingRestoreOnStartup(
            markerPath,
            backupDirectory,
            currentDatabase,
            currentAttachments);

        Assert.False(result.StartupBlocked);
        Assert.Contains("자동 롤백", result.Message);
        Assert.Equal("original", await ReadStateValueAsync(currentDatabase));
        Assert.Equal(
            "original-attachment",
            await File.ReadAllTextAsync(
                Path.Combine(currentAttachments, "old", "original.txt")));
        Assert.False(File.Exists(processingMarkerPath));
        Assert.False(File.Exists(paths.DatabaseRollbackPath));
        Assert.False(File.Exists(paths.DatabaseFailedPath));
    }

    [Fact]
    public async Task ApplyingJournal_AfterAttachmentSwitch_AutomaticallyRollsBackDbAndAttachments()
    {
        using var scope = new TemporaryDirectory();
        var backupDirectory = Path.Combine(scope.Path, "backup");
        var currentDatabase = Path.Combine(scope.Path, "current", "거래플랜.db");
        var currentAttachments = Path.Combine(scope.Path, "current", "attachments");
        var replacementDatabase = Path.Combine(scope.Path, "replacement", "거래플랜.db");
        var markerPath = Path.Combine(scope.Path, "temp", "pending-db-restore.txt");
        var processingMarkerPath = markerPath + ".processing";
        var operationId = Guid.NewGuid().ToString("N");
        var paths = BuildRecoveryPaths(currentDatabase, currentAttachments, operationId);

        await CreateTradePlanDatabaseAsync(currentDatabase, "original");
        await CreateTradePlanDatabaseAsync(replacementDatabase, "replacement");
        await WriteAttachmentRecordAsync(
            currentDatabase,
            currentAttachments,
            "old/original.txt",
            "original-attachment");
        File.Copy(currentDatabase, paths.DatabaseRollbackPath);
        Directory.Move(currentAttachments, paths.AttachmentsRollbackDirectory);
        File.Copy(replacementDatabase, currentDatabase, overwrite: true);
        Directory.CreateDirectory(currentAttachments);
        await File.WriteAllTextAsync(
            Path.Combine(currentAttachments, "replacement.txt"),
            "replacement-attachment");
        Directory.CreateDirectory(Path.GetDirectoryName(processingMarkerPath)!);
        await WriteApplyingMarkerAsync(
            processingMarkerPath,
            Path.Combine(backupDirectory, "source.gpbackup"),
            operationId,
            "AttachmentsSwitched",
            hadCurrentDatabase: true,
            hadCurrentAttachments: true);

        var result = BackupService.ApplyPendingRestoreOnStartup(
            markerPath,
            backupDirectory,
            currentDatabase,
            currentAttachments);

        Assert.False(result.StartupBlocked);
        Assert.Equal("original", await ReadStateValueAsync(currentDatabase));
        Assert.Equal(
            "original-attachment",
            await File.ReadAllTextAsync(
                Path.Combine(currentAttachments, "old", "original.txt")));
        Assert.False(File.Exists(
            Path.Combine(currentAttachments, "replacement.txt")));
        Assert.False(Directory.Exists(paths.AttachmentsRollbackDirectory));
        Assert.False(Directory.Exists(paths.AttachmentsFailedDirectory));
    }

    [Fact]
    public async Task ApplyingJournal_BeforeAnySwitch_CancelsInterruptedRestoreAndKeepsOriginal()
    {
        using var scope = new TemporaryDirectory();
        var backupDirectory = Path.Combine(scope.Path, "backup");
        var currentDatabase = Path.Combine(scope.Path, "current", "거래플랜.db");
        var currentAttachments = Path.Combine(scope.Path, "current", "attachments");
        var markerPath = Path.Combine(scope.Path, "temp", "pending-db-restore.txt");
        var processingMarkerPath = markerPath + ".processing";
        var operationId = Guid.NewGuid().ToString("N");
        await CreateTradePlanDatabaseAsync(currentDatabase, "original");
        Directory.CreateDirectory(currentAttachments);
        Directory.CreateDirectory(Path.GetDirectoryName(processingMarkerPath)!);
        await WriteApplyingMarkerAsync(
            processingMarkerPath,
            Path.Combine(backupDirectory, "source.gpbackup"),
            operationId,
            "Prepared",
            hadCurrentDatabase: true,
            hadCurrentAttachments: true);

        var result = BackupService.ApplyPendingRestoreOnStartup(
            markerPath,
            backupDirectory,
            currentDatabase,
            currentAttachments);

        Assert.False(result.StartupBlocked);
        Assert.Equal("original", await ReadStateValueAsync(currentDatabase));
        Assert.True(Directory.Exists(currentAttachments));
        Assert.False(File.Exists(processingMarkerPath));
    }

    [Fact]
    public async Task ApplyingJournal_MissingRequiredRollbackArtifact_FailsClosedWithPreservedPaths()
    {
        using var scope = new TemporaryDirectory();
        var backupDirectory = Path.Combine(scope.Path, "backup");
        var currentDatabase = Path.Combine(scope.Path, "current", "거래플랜.db");
        var currentAttachments = Path.Combine(scope.Path, "current", "attachments");
        var markerPath = Path.Combine(scope.Path, "temp", "pending-db-restore.txt");
        var processingMarkerPath = markerPath + ".processing";
        var operationId = Guid.NewGuid().ToString("N");
        var paths = BuildRecoveryPaths(currentDatabase, currentAttachments, operationId);
        await CreateTradePlanDatabaseAsync(currentDatabase, "replacement");
        Directory.CreateDirectory(currentAttachments);
        Directory.CreateDirectory(Path.GetDirectoryName(processingMarkerPath)!);
        await WriteApplyingMarkerAsync(
            processingMarkerPath,
            Path.Combine(backupDirectory, "source.gpbackup"),
            operationId,
            "DatabaseSwitched",
            hadCurrentDatabase: true,
            hadCurrentAttachments: true);

        var result = BackupService.ApplyPendingRestoreOnStartup(
            markerPath,
            backupDirectory,
            currentDatabase,
            currentAttachments);

        Assert.True(result.StartupBlocked);
        Assert.Contains(processingMarkerPath, result.Message);
        Assert.Contains(paths.DatabaseRollbackPath, result.Message);
        Assert.True(File.Exists(processingMarkerPath));
        Assert.Equal("replacement", await ReadStateValueAsync(currentDatabase));
    }

    [Fact]
    public async Task Retention_NewerCorruptBackupDoesNotEvictOlderValidBackupFromSameDay()
    {
        using var scope = new TemporaryDirectory();
        var backupDirectory = Path.Combine(scope.Path, "backup");
        var markerPath = Path.Combine(scope.Path, "temp", "pending-db-restore.txt");
        var targetDate = DateTime.Now.Date.AddDays(-2);
        var validSameDay = Path.Combine(
            backupDirectory,
            "거래플랜_20260720_090000_000.db");
        var corruptSameDay = Path.Combine(
            backupDirectory,
            "거래플랜_20260720_230000_000.db");
        var globallyNewestValid = Path.Combine(
            backupDirectory,
            "거래플랜_20260721_090000_000.db");
        await CreateTradePlanDatabaseAsync(validSameDay, "valid-same-day");
        await File.WriteAllTextAsync(corruptSameDay, "corrupt");
        await CreateTradePlanDatabaseAsync(globallyNewestValid, "newest-valid");
        File.SetLastWriteTime(validSameDay, targetDate.AddHours(9));
        File.SetLastWriteTime(corruptSameDay, targetDate.AddHours(23));
        File.SetLastWriteTime(
            globallyNewestValid,
            targetDate.AddDays(1).AddHours(9));

        BackupService.TrimManagedBackups(backupDirectory, markerPath);

        Assert.True(File.Exists(validSameDay));
        Assert.False(File.Exists(corruptSameDay));
        Assert.True(File.Exists(globallyNewestValid));
    }

    [Fact]
    public async Task Restore_DamagedCurrentAttachmentGeneration_IsQuarantinedAndDoesNotBlockVerifiedRestore()
    {
        using var scope = new TemporaryDirectory();
        var backupDirectory = Path.Combine(scope.Path, "backup");
        var sourceDatabase = Path.Combine(scope.Path, "source", "거래플랜.db");
        var sourceAttachments = Path.Combine(scope.Path, "source", "attachments");
        var currentDatabase = Path.Combine(scope.Path, "current", "거래플랜.db");
        var currentAttachments = Path.Combine(scope.Path, "current", "attachments");
        var packagePath = Path.Combine(backupDirectory, "거래플랜_20260723_045454_004.gpbackup");
        await CreateTradePlanDatabaseAsync(sourceDatabase, "replacement");
        await CreateTradePlanDatabaseAsync(currentDatabase, "damaged-current");
        await WriteAttachmentRecordAsync(
            sourceDatabase,
            sourceAttachments,
            "new/new.txt",
            "replacement");
        await WriteAttachmentRecordAsync(
            currentDatabase,
            currentAttachments,
            "old/missing.txt",
            "will-be-missing");
        File.Delete(Path.Combine(currentAttachments, "old", "missing.txt"));
        var orphanPath = Path.Combine(currentAttachments, "orphan", "preserve-me.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(orphanPath)!);
        await File.WriteAllTextAsync(orphanPath, "raw-orphan");
        await BackupService.CreateConsistentBackupPackageAsync(
            sourceDatabase,
            sourceAttachments,
            packagePath,
            CancellationToken.None);

        BackupService.RestoreBackupArtifact(
            packagePath,
            currentDatabase,
            currentAttachments,
            backupDirectory);

        Assert.Equal("replacement", await ReadStateValueAsync(currentDatabase));
        Assert.Equal(
            "replacement",
            await File.ReadAllTextAsync(Path.Combine(currentAttachments, "new", "new.txt")));
        var rawRecoveryDirectory = Assert.Single(
            Directory.EnumerateDirectories(
                backupDirectory,
                "복원격리-*",
                SearchOption.TopDirectoryOnly));
        Assert.Equal(
            "damaged-current",
            await ReadStateValueAsync(Path.Combine(rawRecoveryDirectory, "database.db")));
        Assert.Equal(
            "raw-orphan",
            await File.ReadAllTextAsync(
                Path.Combine(
                    rawRecoveryDirectory,
                    "attachments",
                    "orphan",
                    "preserve-me.txt")));
        Assert.True(File.Exists(Path.Combine(rawRecoveryDirectory, "recovery.json")));
    }

    [Fact]
    public async Task Restore_HealthyCurrentGenerationWithOrphan_QuarantinesOnlyOrphanAndSucceeds()
    {
        using var scope = new TemporaryDirectory();
        var backupDirectory = Path.Combine(scope.Path, "backup");
        var sourceDatabase = Path.Combine(scope.Path, "source", "거래플랜.db");
        var sourceAttachments = Path.Combine(scope.Path, "source", "attachments");
        var currentDatabase = Path.Combine(scope.Path, "current", "거래플랜.db");
        var currentAttachments = Path.Combine(scope.Path, "current", "attachments");
        var packagePath = Path.Combine(
            backupDirectory,
            "거래플랜_20260723_045757_004.gpbackup");
        await CreateTradePlanDatabaseAsync(sourceDatabase, "replacement");
        await CreateTradePlanDatabaseAsync(currentDatabase, "healthy-current");
        await WriteAttachmentRecordAsync(
            sourceDatabase,
            sourceAttachments,
            "new/new.txt",
            "replacement");
        await WriteAttachmentRecordAsync(
            currentDatabase,
            currentAttachments,
            "old/referenced.txt",
            "healthy-reference");
        var orphanPath = Path.Combine(
            currentAttachments,
            "orphan",
            "preserve-me.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(orphanPath)!);
        await File.WriteAllTextAsync(orphanPath, "unreferenced-only-copy");
        await BackupService.CreateConsistentBackupPackageAsync(
            sourceDatabase,
            sourceAttachments,
            packagePath,
            CancellationToken.None);

        BackupService.RestoreBackupArtifact(
            packagePath,
            currentDatabase,
            currentAttachments,
            backupDirectory);

        Assert.Equal("replacement", await ReadStateValueAsync(currentDatabase));
        Assert.Equal(
            "replacement",
            await File.ReadAllTextAsync(
                Path.Combine(currentAttachments, "new", "new.txt")));
        var recoveryDirectory = Assert.Single(
            Directory.EnumerateDirectories(
                backupDirectory,
                "복원격리-*",
                SearchOption.TopDirectoryOnly));
        Assert.Equal(
            "unreferenced-only-copy",
            await File.ReadAllTextAsync(
                Path.Combine(
                    recoveryDirectory,
                    "attachments",
                    "orphan",
                    "preserve-me.txt")));
        Assert.False(
            File.Exists(
                Path.Combine(
                    recoveryDirectory,
                    "attachments",
                    "old",
                    "referenced.txt")));

        using var recoveryMetadata = JsonDocument.Parse(
            await File.ReadAllTextAsync(
                Path.Combine(recoveryDirectory, "recovery.json")));
        Assert.Equal(
            "UnreferencedCurrentAttachments",
            recoveryMetadata.RootElement.GetProperty("reason").GetString());
        Assert.Equal(
            1,
            recoveryMetadata.RootElement
                .GetProperty("preservedAttachmentCount")
                .GetInt32());
    }

    [Fact]
    public async Task Restore_MissingCurrentDatabase_PreservesExistingAttachmentsInRecoveryQuarantine()
    {
        using var scope = new TemporaryDirectory();
        var backupDirectory = Path.Combine(scope.Path, "backup");
        var sourceDatabase = Path.Combine(scope.Path, "source", "거래플랜.db");
        var sourceAttachments = Path.Combine(scope.Path, "source", "attachments");
        var currentDatabase = Path.Combine(scope.Path, "current", "거래플랜.db");
        var currentAttachments = Path.Combine(scope.Path, "current", "attachments");
        var packagePath = Path.Combine(backupDirectory, "거래플랜_20260723_045959_004.gpbackup");

        await CreateTradePlanDatabaseAsync(sourceDatabase, "replacement");
        await WriteAttachmentRecordAsync(
            sourceDatabase,
            sourceAttachments,
            "new/new.txt",
            "replacement");
        var onlyCopyPath = Path.Combine(
            currentAttachments,
            "orphan",
            "only-copy.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(onlyCopyPath)!);
        await File.WriteAllTextAsync(onlyCopyPath, "only-current-copy");
        Assert.False(File.Exists(currentDatabase));

        await BackupService.CreateConsistentBackupPackageAsync(
            sourceDatabase,
            sourceAttachments,
            packagePath,
            CancellationToken.None);

        BackupService.RestoreBackupArtifact(
            packagePath,
            currentDatabase,
            currentAttachments,
            backupDirectory);

        Assert.Equal("replacement", await ReadStateValueAsync(currentDatabase));
        Assert.Equal(
            "replacement",
            await File.ReadAllTextAsync(
                Path.Combine(currentAttachments, "new", "new.txt")));
        Assert.False(File.Exists(onlyCopyPath));

        var rawRecoveryDirectory = Assert.Single(
            Directory.EnumerateDirectories(
                backupDirectory,
                "복원격리-*",
                SearchOption.TopDirectoryOnly));
        Assert.Equal(
            "only-current-copy",
            await File.ReadAllTextAsync(
                Path.Combine(
                    rawRecoveryDirectory,
                    "attachments",
                    "orphan",
                    "only-copy.pdf")));

        using var recoveryMetadata = JsonDocument.Parse(
            await File.ReadAllTextAsync(
                Path.Combine(rawRecoveryDirectory, "recovery.json")));
        Assert.Equal(
            "CurrentDatabaseMissing",
            recoveryMetadata.RootElement.GetProperty("reason").GetString());
        Assert.Equal(
            JsonValueKind.Null,
            recoveryMetadata.RootElement.GetProperty("sourceDatabasePath").ValueKind);
    }

    [Fact]
    public void RestoreMarkerWrite_UsesWriteThroughAndFlushToDiskBeforeAtomicMove()
    {
        var testSourcePath = GetTestSourcePath();
        var repositoryRoot = Path.GetFullPath(
            Path.Combine(Path.GetDirectoryName(testSourcePath)!, "..", ".."));
        var serviceSource = File.ReadAllText(
            Path.Combine(
                repositoryRoot,
                "Desktop",
                "거래플랜.Desktop.App",
                "Services",
                "BackupService.cs"));

        Assert.Contains("FileOptions.WriteThrough", serviceSource);
        Assert.Contains("stream.Flush(flushToDisk: true);", serviceSource);
        Assert.Contains(
            "File.Move(stagingPath, markerPath, overwrite: true);",
            serviceSource);
    }

    [Fact]
    public async Task RestoreFailureAfterSwitch_RollsBackDatabaseAndAttachmentGeneration()
    {
        using var scope = new TemporaryDirectory();
        var backupDirectory = Path.Combine(scope.Path, "backup");
        var sourceDatabase = Path.Combine(scope.Path, "source", "거래플랜.db");
        var sourceAttachments = Path.Combine(scope.Path, "source", "attachments");
        var currentDatabase = Path.Combine(scope.Path, "current", "거래플랜.db");
        var currentAttachments = Path.Combine(scope.Path, "current", "attachments");
        var packagePath = Path.Combine(backupDirectory, "거래플랜_20260723_050505_005.gpbackup");
        await CreateTradePlanDatabaseAsync(sourceDatabase, "replacement");
        await CreateTradePlanDatabaseAsync(currentDatabase, "current");
        await WriteAttachmentRecordAsync(
            sourceDatabase,
            sourceAttachments,
            "new/new.txt",
            "replacement");
        await WriteAttachmentRecordAsync(
            currentDatabase,
            currentAttachments,
            "old/old.txt",
            "current");
        await BackupService.CreateConsistentBackupPackageAsync(
            sourceDatabase,
            sourceAttachments,
            packagePath,
            CancellationToken.None);

        Assert.Throws<InjectedRestoreFailureException>(() =>
            BackupService.RestoreBackupArtifact(
                packagePath,
                currentDatabase,
                currentAttachments,
                backupDirectory,
                () => throw new InjectedRestoreFailureException()));

        Assert.Equal("current", await ReadStateValueAsync(currentDatabase));
        Assert.Equal(
            "current",
            await File.ReadAllTextAsync(Path.Combine(currentAttachments, "old", "old.txt")));
        Assert.False(Path.Exists(Path.Combine(currentAttachments, "new", "new.txt")));
    }

    private static async Task WriteApplyingMarkerAsync(
        string processingMarkerPath,
        string backupPath,
        string operationId,
        string phase,
        bool hadCurrentDatabase,
        bool hadCurrentAttachments)
    {
        await File.WriteAllTextAsync(
            processingMarkerPath,
            JsonSerializer.Serialize(new
            {
                backupPath,
                state = "Applying",
                operationId,
                phase,
                hadCurrentDatabase,
                hadCurrentAttachments
            }));
    }

    private static RecoveryPaths BuildRecoveryPaths(
        string currentDatabase,
        string currentAttachments,
        string operationId)
    {
        var prefix = $".gp-restore-{operationId}";
        return new RecoveryPaths(
            Path.Combine(
                Path.GetDirectoryName(currentDatabase)!,
                prefix + "-database-rollback"),
            Path.Combine(
                Path.GetDirectoryName(currentDatabase)!,
                prefix + "-database-failed"),
            Path.Combine(
                Path.GetDirectoryName(currentAttachments)!,
                prefix + "-attachments-rollback"),
            Path.Combine(
                Path.GetDirectoryName(currentAttachments)!,
                prefix + "-attachments-failed"));
    }

    private static async Task CreateTradePlanDatabaseAsync(string path, string stateValue)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA foreign_keys=ON;
            PRAGMA application_id=1196444750;
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
            INSERT INTO State (Value) VALUES ($stateValue);
            """;
        command.Parameters.AddWithValue("$stateValue", stateValue);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task WriteAttachmentRecordAsync(
        string databasePath,
        string root,
        string relativePath,
        string content)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
        var bytes = await File.ReadAllBytesAsync(path);

        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO TransactionAttachments (
                Id,
                TransactionId,
                StoredPath,
                FileSize,
                FileHash,
                IsDeleted
            )
            VALUES (
                $id,
                $transactionId,
                $storedPath,
                $fileSize,
                $fileHash,
                0
            );
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("$transactionId", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("$storedPath", Path.GetFullPath(path));
        command.Parameters.AddWithValue("$fileSize", bytes.LongLength);
        command.Parameters.AddWithValue(
            "$fileHash",
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
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

    private static async Task<bool> TableExistsAsync(
        string databasePath,
        string tableName)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$tableName;";
        command.Parameters.AddWithValue("$tableName", tableName);
        return Convert.ToInt32(await command.ExecuteScalarAsync() ?? 0) == 1;
    }

    private static string GetTestSourcePath(
        [CallerFilePath] string sourceFilePath = "")
        => sourceFilePath;

    private static async Task<string?> ReadStoredPathAsync(string databasePath)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT StoredPath FROM TransactionAttachments WHERE IsDeleted = 0 LIMIT 1;";
        return (await command.ExecuteScalarAsync())?.ToString();
    }

    private static async Task<(string StoredPath, long FileSize, string FileHash)>
        ReadAttachmentMetadataAsync(
            string databasePath,
            string attachmentId)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT StoredPath, FileSize, FileHash
            FROM TransactionAttachments
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", attachmentId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetString(2));
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private sealed class InjectedRestoreFailureException : Exception;

    private sealed record RecoveryPaths(
        string DatabaseRollbackPath,
        string DatabaseFailedPath,
        string AttachmentsRollbackDirectory,
        string AttachmentsFailedDirectory);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                TestProcessIsolation.AppRoot,
                "backup-generation-tests",
                Guid.NewGuid().ToString("N"));
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
