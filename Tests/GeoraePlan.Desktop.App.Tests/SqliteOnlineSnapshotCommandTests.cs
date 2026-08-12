using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class SqliteOnlineSnapshotCommandTests
{
    private const string SnapshotTestPhaseEnvironmentKey =
        "GEORAEPLAN_SNAPSHOT_TEST_PHASE";
    private const string SnapshotTestOptInEnvironmentKey =
        "GEORAEPLAN_SNAPSHOT_TEST_FAULT_INJECTION";
    private const string SnapshotTestRootEnvironmentKey =
        "GEORAEPLAN_SNAPSHOT_TEST_ROOT";

    [Fact]
    public void SnapshotCommand_HoldsNoDeleteIdentityLeasesThroughVerification()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "SyncDiag",
            "Program.cs"));

        Assert.Contains(
            "SnapshotSourceFileLease.CreateOwned(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "temporaryLease.AssertStable();",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "SnapshotSidecarLeaseSet.Acquire(paths.SourceDatabasePath)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "SnapshotSourceFileLease.PathEntryExists",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "var committedFingerprint =",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "VerifySerializedSqliteSnapshot(serializedDatabase)",
            source,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-wal")]
    [InlineData("-shm")]
    public async Task SnapshotCommand_HardLinkedSourceSidecarIsRejected(
        string suffix)
    {
        var repositoryRoot = FindRepositoryRoot();
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"sqlite-sidecar-hardlink-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var targetRoot = Path.Combine(testRoot, "target");
        var sourceDatabase = Path.Combine(sourceRoot, "data", "거래플랜.db");
        var targetDatabase = Path.Combine(targetRoot, "data", "거래플랜.db");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceDatabase)!);
        Directory.CreateDirectory(Path.GetDirectoryName(targetDatabase)!);

        try
        {
            await using (var connection = new SqliteConnection(
                $"Data Source={sourceDatabase};Pooling=False"))
            {
                await connection.OpenAsync();
                await ExecuteNonQueryAsync(
                    connection,
                    "CREATE TABLE T (Id INTEGER PRIMARY KEY);");
            }
            SqliteConnection.ClearAllPools();

            var sidecar = sourceDatabase + suffix;
            var victim = Path.Combine(testRoot, "sidecar-victim.bin");
            await File.WriteAllBytesAsync(
                victim,
                System.Text.Encoding.UTF8.GetBytes("preserve-sidecar-victim"));
            Assert.True(
                NativeMethods.CreateHardLinkW(
                    sidecar,
                    victim,
                    IntPtr.Zero));
            var victimHash = ComputeSha256(victim);

            var result = await RunProcessAsync(
                ResolveDotnetPath(),
                repositoryRoot,
                TimeSpan.FromSeconds(30),
                [
                    ResolveSyncDiagToolPath(repositoryRoot),
                    "snapshot-sqlite",
                    sourceDatabase,
                    targetDatabase
                ],
                new Dictionary<string, string?>
                {
                    ["GEORAEPLAN_TEST_MODE"] = "1",
                    ["GEORAEPLAN_SOURCE_SNAPSHOT_ROOT"] = sourceRoot,
                    ["GEORAEPLAN_TARGET_SNAPSHOT_ROOT"] = targetRoot
                });

            Assert.Equal(1, result.ExitCode);
            Assert.Empty(result.Stdout);
            Assert.Matches(
                "^snapshot_sqlite_failed reason_code=[a-z0-9_]+\\r?\\n?$",
                result.Stderr);
            Assert.Equal(victimHash, ComputeSha256(victim));
            Assert.False(File.Exists(targetDatabase));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SnapshotCommand_PostSourceBackupTempInPlaceRewriteIsBlocked()
    {
#if !DEBUG
        await Task.CompletedTask;
        return;
#else
        var repositoryRoot = FindRepositoryRoot();
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"sqlite-post-backup-race-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var targetRoot = Path.Combine(testRoot, "target");
        var sourceDatabase = Path.Combine(
            sourceRoot,
            "data",
            "\uac70\ub798\ud50c\ub79c.db");
        var targetDatabase = Path.Combine(
            targetRoot,
            "data",
            "\uac70\ub798\ud50c\ub79c.db");
        var targetData = Path.GetDirectoryName(targetDatabase)!;
        var signal = GetSnapshotHookMarkerPath(
            targetData,
            "POST_SOURCE_BACKUP",
            "signal");
        var resume = GetSnapshotHookMarkerPath(
            targetData,
            "POST_SOURCE_BACKUP",
            "continue");
        var attacker = Path.Combine(testRoot, "valid-attacker.db");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceDatabase)!);
        Directory.CreateDirectory(targetData);
        Process? process = null;

        try
        {
            await CreateRaceDatabaseAsync(sourceDatabase, "snapshot-row");
            await CreateRaceDatabaseAsync(attacker, "attacker-row");
            SqliteConnection.ClearAllPools();
            var attackerHash = ComputeSha256(attacker);

            var startInfo = CreateSnapshotStartInfo(
                repositoryRoot,
                sourceDatabase,
                targetDatabase,
                sourceRoot,
                targetRoot);
            ConfigureSnapshotTestHook(
                startInfo,
                targetData,
                "POST_SOURCE_BACKUP");
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "The post-backup race child did not start.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            var signalTask =
                WaitForPathAsync(signal, TimeSpan.FromSeconds(30));
            if (await Task.WhenAny(
                    signalTask,
                    process.WaitForExitAsync()) != signalTask)
            {
                throw new InvalidOperationException(
                    "The post-backup race child exited before signaling: " +
                    await stderrTask);
            }
            await signalTask;
            var temporaryDatabase = Assert.Single(
                Directory.GetFiles(
                    targetData,
                    "*.snapshot-*.tmp",
                    SearchOption.TopDirectoryOnly));

            var rewriteFailure = Record.Exception(() =>
                RewriteFileInPlace(attacker, temporaryDatabase));
            Assert.True(
                rewriteFailure is IOException or
                    UnauthorizedAccessException,
                $"Unexpected in-place rewrite result: {rewriteFailure}");
            Assert.Equal(attackerHash, ComputeSha256(attacker));

            await File.WriteAllBytesAsync(resume, [1]);
            await process.WaitForExitAsync().WaitAsync(
                TimeSpan.FromSeconds(30));
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            Assert.True(
                process.ExitCode == 0,
                stdout + Environment.NewLine + stderr);
            Assert.Empty(stderr);
            Assert.Equal(
                "snapshot-row",
                await ReadRaceDatabaseValueAsync(targetDatabase));
        }
        finally
        {
            try
            {
                if (Directory.Exists(targetData) &&
                    !File.Exists(resume))
                {
                    await File.WriteAllBytesAsync(resume, [1]);
                }
                if (process is { HasExited: false })
                {
                    await process.WaitForExitAsync().WaitAsync(
                        TimeSpan.FromSeconds(5));
                }
            }
            catch (TimeoutException)
            {
                process?.Kill(entireProcessTree: true);
            }
            finally
            {
                process?.Dispose();
            }
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
#endif
    }

    [Fact]
    public async Task SnapshotCommand_UnexpectedTempSidecarIsPreservedOnFailure()
    {
#if !DEBUG
        await Task.CompletedTask;
        return;
#else
        var repositoryRoot = FindRepositoryRoot();
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"sqlite-temp-sidecar-race-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var targetRoot = Path.Combine(testRoot, "target");
        var sourceDatabase = Path.Combine(
            sourceRoot,
            "data",
            "\uac70\ub798\ud50c\ub79c.db");
        var targetDatabase = Path.Combine(
            targetRoot,
            "data",
            "\uac70\ub798\ud50c\ub79c.db");
        var targetData = Path.GetDirectoryName(targetDatabase)!;
        var signal = GetSnapshotHookMarkerPath(
            targetData,
            "POST_SOURCE_BACKUP",
            "signal");
        var resume = GetSnapshotHookMarkerPath(
            targetData,
            "POST_SOURCE_BACKUP",
            "continue");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceDatabase)!);
        Directory.CreateDirectory(targetData);
        Process? process = null;
        string? unexpectedSidecar = null;

        try
        {
            await CreateRaceDatabaseAsync(sourceDatabase, "snapshot-row");
            SqliteConnection.ClearAllPools();

            var startInfo = CreateSnapshotStartInfo(
                repositoryRoot,
                sourceDatabase,
                targetDatabase,
                sourceRoot,
                targetRoot);
            ConfigureSnapshotTestHook(
                startInfo,
                targetData,
                "POST_SOURCE_BACKUP");
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "The temporary-sidecar race child did not start.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            var signalTask =
                WaitForPathAsync(signal, TimeSpan.FromSeconds(30));
            if (await Task.WhenAny(
                    signalTask,
                    process.WaitForExitAsync()) != signalTask)
            {
                throw new InvalidOperationException(
                    "The temporary-sidecar race child exited before signaling: " +
                    await stderrTask);
            }
            await signalTask;
            var temporaryDatabase = Assert.Single(
                Directory.GetFiles(
                    targetData,
                    "*.snapshot-*.tmp",
                    SearchOption.TopDirectoryOnly));
            unexpectedSidecar = temporaryDatabase + "-journal";
            var victimBytes =
                System.Text.Encoding.UTF8.GetBytes(
                    "unexpected-sidecar-must-remain");
            await File.WriteAllBytesAsync(
                unexpectedSidecar,
                victimBytes);
            var victimHash = ComputeSha256(unexpectedSidecar);

            await File.WriteAllBytesAsync(resume, [1]);
            await process.WaitForExitAsync().WaitAsync(
                TimeSpan.FromSeconds(30));
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            Assert.Equal(1, process.ExitCode);
            Assert.Empty(stdout);
            Assert.Matches(
                "^snapshot_sqlite_failed reason_code=[a-z0-9_]+\\r?\\n?$",
                stderr);
            Assert.False(File.Exists(targetDatabase));
            Assert.True(File.Exists(unexpectedSidecar));
            Assert.Equal(victimHash, ComputeSha256(unexpectedSidecar));
        }
        finally
        {
            try
            {
                if (Directory.Exists(targetData) &&
                    !File.Exists(resume))
                {
                    await File.WriteAllBytesAsync(resume, [1]);
                }
                if (process is { HasExited: false })
                {
                    await process.WaitForExitAsync().WaitAsync(
                        TimeSpan.FromSeconds(5));
                }
            }
            catch (TimeoutException)
            {
                process?.Kill(entireProcessTree: true);
            }
            finally
            {
                process?.Dispose();
            }
            SqliteConnection.ClearAllPools();
            if (unexpectedSidecar is not null &&
                File.Exists(unexpectedSidecar))
            {
                File.Delete(unexpectedSidecar);
            }
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
#endif
    }

    [Fact]
    public async Task SnapshotCommand_CommittedValidationFailureDeletesOnlyOwnedTarget()
    {
#if !DEBUG
        await Task.CompletedTask;
        return;
#else
        var repositoryRoot = FindRepositoryRoot();
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"sqlite-committed-cleanup-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var targetRoot = Path.Combine(testRoot, "target");
        var sourceDatabase = Path.Combine(
            sourceRoot,
            "data",
            "\uac70\ub798\ud50c\ub79c.db");
        var targetDatabase = Path.Combine(
            targetRoot,
            "data",
            "\uac70\ub798\ud50c\ub79c.db");
        var targetData = Path.GetDirectoryName(targetDatabase)!;
        var signal = GetSnapshotHookMarkerPath(
            targetData,
            "POST_COMMITTED_FINGERPRINT",
            "signal");
        var resume = GetSnapshotHookMarkerPath(
            targetData,
            "POST_COMMITTED_FINGERPRINT",
            "continue");
        var unexpectedSidecar = targetDatabase + "-journal";
        Directory.CreateDirectory(Path.GetDirectoryName(sourceDatabase)!);
        Directory.CreateDirectory(targetData);
        Process? process = null;

        try
        {
            await CreateRaceDatabaseAsync(sourceDatabase, "snapshot-row");
            SqliteConnection.ClearAllPools();

            var startInfo = CreateSnapshotStartInfo(
                repositoryRoot,
                sourceDatabase,
                targetDatabase,
                sourceRoot,
                targetRoot);
            ConfigureSnapshotTestHook(
                startInfo,
                targetData,
                "POST_COMMITTED_FINGERPRINT");
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "The committed-cleanup child did not start.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            var signalTask =
                WaitForPathAsync(signal, TimeSpan.FromSeconds(30));
            if (await Task.WhenAny(
                    signalTask,
                    process.WaitForExitAsync()) != signalTask)
            {
                throw new InvalidOperationException(
                    "The committed-cleanup child exited before signaling: " +
                    await stderrTask);
            }
            await signalTask;
            Assert.True(File.Exists(targetDatabase));
            var sidecarBytes =
                System.Text.Encoding.UTF8.GetBytes(
                    "committed-sidecar-must-remain");
            await File.WriteAllBytesAsync(
                unexpectedSidecar,
                sidecarBytes);
            var sidecarHash = ComputeSha256(unexpectedSidecar);

            await File.WriteAllBytesAsync(resume, [1]);
            await process.WaitForExitAsync().WaitAsync(
                TimeSpan.FromSeconds(30));
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            Assert.Equal(1, process.ExitCode);
            Assert.Empty(stdout);
            Assert.Matches(
                "^snapshot_sqlite_failed reason_code=[a-z0-9_]+\\r?\\n?$",
                stderr);
            Assert.False(File.Exists(targetDatabase));
            Assert.True(File.Exists(unexpectedSidecar));
            Assert.Equal(sidecarHash, ComputeSha256(unexpectedSidecar));
        }
        finally
        {
            try
            {
                if (Directory.Exists(targetData) &&
                    !File.Exists(resume))
                {
                    await File.WriteAllBytesAsync(resume, [1]);
                }
                if (process is { HasExited: false })
                {
                    await process.WaitForExitAsync().WaitAsync(
                        TimeSpan.FromSeconds(5));
                }
            }
            catch (TimeoutException)
            {
                process?.Kill(entireProcessTree: true);
            }
            finally
            {
                process?.Dispose();
            }
            SqliteConnection.ClearAllPools();
            if (File.Exists(unexpectedSidecar))
                File.Delete(unexpectedSidecar);
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
#endif
    }

    [Fact]
    public async Task SnapshotCommand_PostRenameValidationFailureLeavesNoOwnedDatabase()
    {
#if !DEBUG
        await Task.CompletedTask;
        return;
#else
        var repositoryRoot = FindRepositoryRoot();
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"sqlite-post-rename-failure-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var targetRoot = Path.Combine(testRoot, "target");
        var sourceDatabase = Path.Combine(
            sourceRoot,
            "data",
            "\uac70\ub798\ud50c\ub79c.db");
        var targetDatabase = Path.Combine(
            targetRoot,
            "data",
            "\uac70\ub798\ud50c\ub79c.db");
        var targetData = Path.GetDirectoryName(targetDatabase)!;
        var signal = GetSnapshotHookMarkerPath(
            targetData,
            "POST_RENAME_STATE_TRANSITION",
            "signal");
        var resume = GetSnapshotHookMarkerPath(
            targetData,
            "POST_RENAME_STATE_TRANSITION",
            "continue");
        var victim = Path.Combine(testRoot, "rename-failure-victim.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceDatabase)!);
        Directory.CreateDirectory(targetData);
        Process? process = null;

        try
        {
            await CreateRaceDatabaseAsync(sourceDatabase, "snapshot-row");
            await File.WriteAllBytesAsync(
                victim,
                System.Text.Encoding.UTF8.GetBytes(
                    "rename-failure-victim-must-remain"));
            var victimHash = ComputeSha256(victim);
            SqliteConnection.ClearAllPools();

            var startInfo = CreateSnapshotStartInfo(
                repositoryRoot,
                sourceDatabase,
                targetDatabase,
                sourceRoot,
                targetRoot);
            ConfigureSnapshotTestHook(
                startInfo,
                targetData,
                "POST_RENAME_STATE_TRANSITION");
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "The post-rename failure child did not start.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            var signalTask =
                WaitForPathAsync(signal, TimeSpan.FromSeconds(30));
            if (await Task.WhenAny(
                    signalTask,
                    process.WaitForExitAsync()) != signalTask)
            {
                throw new InvalidOperationException(
                    "The post-rename failure child exited before signaling: " +
                    await stderrTask);
            }
            await signalTask;
            Assert.True(File.Exists(targetDatabase));

            await File.WriteAllBytesAsync(resume, [1]);
            await process.WaitForExitAsync().WaitAsync(
                TimeSpan.FromSeconds(30));
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            Assert.Equal(1, process.ExitCode);
            Assert.Empty(stdout);
            Assert.Matches(
                "^snapshot_sqlite_failed reason_code=[a-z0-9_]+\\r?\\n?$",
                stderr);
            Assert.False(File.Exists(targetDatabase));
            Assert.Empty(
                Directory.GetFiles(
                    targetData,
                    "*.snapshot-*.tmp",
                    SearchOption.TopDirectoryOnly));
            Assert.Equal(victimHash, ComputeSha256(victim));
        }
        finally
        {
            try
            {
                if (Directory.Exists(targetData) &&
                    !File.Exists(resume))
                {
                    await File.WriteAllBytesAsync(resume, [1]);
                }
                if (process is { HasExited: false })
                {
                    await process.WaitForExitAsync().WaitAsync(
                        TimeSpan.FromSeconds(5));
                }
            }
            catch (TimeoutException)
            {
                process?.Kill(entireProcessTree: true);
            }
            finally
            {
                process?.Dispose();
            }
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
#endif
    }

    [Fact]
    public async Task SnapshotCommand_PersistentMovedValidationFailureLeavesNoOwnedDatabase()
    {
#if !DEBUG
        await Task.CompletedTask;
        return;
#else
        var repositoryRoot = FindRepositoryRoot();
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"sqlite-persistent-moved-failure-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var targetRoot = Path.Combine(testRoot, "target");
        var sourceDatabase = Path.Combine(
            sourceRoot,
            "data",
            "\uac70\ub798\ud50c\ub79c.db");
        var targetDatabase = Path.Combine(
            targetRoot,
            "data",
            "\uac70\ub798\ud50c\ub79c.db");
        var targetData = Path.GetDirectoryName(targetDatabase)!;
        var signal = GetSnapshotHookMarkerPath(
            targetData,
            "POST_RENAME_PERSISTENT_VALIDATION_FAILURE",
            "signal");
        var resume = GetSnapshotHookMarkerPath(
            targetData,
            "POST_RENAME_PERSISTENT_VALIDATION_FAILURE",
            "continue");
        var victim = Path.Combine(
            testRoot,
            "persistent-validation-victim.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceDatabase)!);
        Directory.CreateDirectory(targetData);
        Process? process = null;

        try
        {
            await CreateRaceDatabaseAsync(sourceDatabase, "snapshot-row");
            await File.WriteAllBytesAsync(
                victim,
                System.Text.Encoding.UTF8.GetBytes(
                    "persistent-validation-victim-must-remain"));
            var victimHash = ComputeSha256(victim);
            SqliteConnection.ClearAllPools();

            var startInfo = CreateSnapshotStartInfo(
                repositoryRoot,
                sourceDatabase,
                targetDatabase,
                sourceRoot,
                targetRoot);
            ConfigureSnapshotTestHook(
                startInfo,
                targetData,
                "POST_RENAME_PERSISTENT_VALIDATION_FAILURE");
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "The persistent-validation child did not start.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            var signalTask =
                WaitForPathAsync(signal, TimeSpan.FromSeconds(30));
            if (await Task.WhenAny(
                    signalTask,
                    process.WaitForExitAsync()) != signalTask)
            {
                throw new InvalidOperationException(
                    "The persistent-validation child exited before signaling: " +
                    await stderrTask);
            }
            await signalTask;
            Assert.True(File.Exists(targetDatabase));

            await File.WriteAllBytesAsync(resume, [1]);
            await process.WaitForExitAsync().WaitAsync(
                TimeSpan.FromSeconds(30));
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            Assert.Equal(1, process.ExitCode);
            Assert.Empty(stdout);
            Assert.Matches(
                "^snapshot_sqlite_failed reason_code=[a-z0-9_]+\\r?\\n?$",
                stderr);
            Assert.False(File.Exists(targetDatabase));
            Assert.Empty(
                Directory.GetFiles(
                    targetData,
                    "*.snapshot-*.tmp",
                    SearchOption.TopDirectoryOnly));
            Assert.Equal(victimHash, ComputeSha256(victim));
        }
        finally
        {
            try
            {
                if (Directory.Exists(targetData) &&
                    !File.Exists(resume))
                {
                    await File.WriteAllBytesAsync(resume, [1]);
                }
                if (process is { HasExited: false })
                {
                    await process.WaitForExitAsync().WaitAsync(
                        TimeSpan.FromSeconds(5));
                }
            }
            catch (TimeoutException)
            {
                process?.Kill(entireProcessTree: true);
            }
            finally
            {
                process?.Dispose();
            }
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
#endif
    }

    [Fact]
    public void SnapshotCommand_KeepsOwnedTempIdentityThroughCommitAndCleanup()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "SyncDiag",
            "Program.cs"));
        var methodStart = source.IndexOf(
            "static SqliteSnapshotResult CreateStandaloneSqliteSnapshot(",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "static SqliteSnapshotPaths ValidateSqliteSnapshotPaths(",
            methodStart,
            StringComparison.Ordinal);
        Assert.True(methodStart >= 0 && methodEnd > methodStart);
        var snapshotMethod = source[methodStart..methodEnd];

        Assert.Contains(
            "SnapshotSourceFileLease.CreateOwned(",
            snapshotMethod,
            StringComparison.Ordinal);
        Assert.Contains(
            "temporaryLease.DeleteOwnedFile();",
            snapshotMethod,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ReacquireWithoutWriteSharing(",
            snapshotMethod,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "DeleteOwnedSnapshotArtifacts(",
            snapshotMethod,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "DeleteOwnedSnapshotSidecars(",
            snapshotMethod,
            StringComparison.Ordinal);

        var moveStart = source.IndexOf(
            "public void MoveTo(",
            StringComparison.Ordinal);
        var moveEnd = source.IndexOf(
            "private static void RenameByHandle(",
            moveStart,
            StringComparison.Ordinal);
        Assert.True(moveStart >= 0 && moveEnd > moveStart);
        var moveMethod = source[moveStart..moveEnd];
        var renameIndex = moveMethod.IndexOf(
            "RenameByHandle(",
            StringComparison.Ordinal);
        var pathTransitionIndex = moveMethod.IndexOf(
            "_path = fullDestinationPath;",
            renameIndex,
            StringComparison.Ordinal);
        var movedTransitionIndex = moveMethod.IndexOf(
            "_moved = true;",
            pathTransitionIndex,
            StringComparison.Ordinal);
        var injectedFailureIndex = moveMethod.IndexOf(
            "afterRenameStateTransition?.Invoke();",
            movedTransitionIndex,
            StringComparison.Ordinal);
        var validationIndex = moveMethod.IndexOf(
            "ReadInformation(_stream.SafeFileHandle)",
            injectedFailureIndex,
            StringComparison.Ordinal);
        Assert.True(
            renameIndex >= 0 &&
            pathTransitionIndex > renameIndex &&
            movedTransitionIndex > pathTransitionIndex &&
            injectedFailureIndex > movedTransitionIndex &&
            validationIndex > injectedFailureIndex,
            "MoveTo must transition to the destination identity immediately after rename and before validation.");

        var verifyStart = source.IndexOf(
            "static string VerifySerializedSqliteSnapshot(",
            StringComparison.Ordinal);
        var verifyEnd = source.IndexOf(
            "static void AssertSerializedSnapshotFingerprint(",
            verifyStart,
            StringComparison.Ordinal);
        Assert.True(verifyStart >= 0 && verifyEnd > verifyStart);
        var verifyMethod = source[verifyStart..verifyEnd];
        Assert.Contains(
            "SQLitePCL.raw.sqlite3_malloc64(",
            verifyMethod,
            StringComparison.Ordinal);
        Assert.Contains(
            "connection.Close();",
            verifyMethod,
            StringComparison.Ordinal);
        Assert.Contains(
            "(connectionClosed || !bufferMayBeInUse)",
            verifyMethod,
            StringComparison.Ordinal);
        Assert.Contains(
            "ZeroUnmanagedBuffer(",
            verifyMethod,
            StringComparison.Ordinal);
        Assert.Contains(
            "SQLitePCL.raw.sqlite3_free(nativeBuffer)",
            verifyMethod,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "sqliteDeserializeFreeOnClose",
            verifyMethod,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "GCHandle.Alloc(",
            verifyMethod,
            StringComparison.Ordinal);
        Assert.True(
            verifyMethod.IndexOf(
                "connection.Close();",
                StringComparison.Ordinal) <
            verifyMethod.LastIndexOf(
                "ZeroUnmanagedBuffer(",
                StringComparison.Ordinal),
            "The deserialize backing buffer must only be zeroed after a successful native close.");

        var serializeStart = source.IndexOf(
            "static byte[] SerializeAttestedSqliteSnapshot(",
            StringComparison.Ordinal);
        var serializeEnd = source.IndexOf(
            "static void ZeroUnmanagedBuffer(",
            serializeStart,
            StringComparison.Ordinal);
        Assert.True(
            serializeStart >= 0 &&
            serializeEnd > serializeStart);
        var serializeMethod =
            source[serializeStart..serializeEnd];
        Assert.Contains(
            "validatedSerializedSize",
            serializeMethod,
            StringComparison.Ordinal);
        var safeActualSizeIndex = serializeMethod.IndexOf(
            "serializedSize <= maximumSerializedSnapshotBytes",
            StringComparison.Ordinal);
        var actualSizeCaptureIndex = serializeMethod.IndexOf(
            "validatedSerializedSize =",
            safeActualSizeIndex,
            StringComparison.Ordinal);
        var expectedSizeMismatchIndex = serializeMethod.IndexOf(
            "serializedSize != expectedSerializedBytes",
            actualSizeCaptureIndex,
            StringComparison.Ordinal);
        Assert.True(
            safeActualSizeIndex >= 0 &&
            actualSizeCaptureIndex > safeActualSizeIndex &&
            expectedSizeMismatchIndex > actualSizeCaptureIndex,
            "A bounded actual serialize size must be captured for zeroing before equality validation.");
        Assert.True(
            serializeMethod.LastIndexOf(
                "ZeroUnmanagedBuffer(",
                StringComparison.Ordinal) <
            serializeMethod.LastIndexOf(
                "SQLitePCL.raw.sqlite3_free(serializedPointer)",
                StringComparison.Ordinal),
            "The validated sqlite3_serialize buffer must be zeroed before it is freed.");

        var createOwnedStart = source.IndexOf(
            "public static SnapshotSourceFileLease CreateOwned(",
            StringComparison.Ordinal);
        var createOwnedEnd = source.IndexOf(
            "public void ReplaceContent(",
            createOwnedStart,
            StringComparison.Ordinal);
        Assert.True(
            createOwnedStart >= 0 &&
            createOwnedEnd > createOwnedStart);
        var createOwnedMethod =
            source[createOwnedStart..createOwnedEnd];
        var deletePendingIndex = createOwnedMethod.IndexOf(
            "SetDeleteDisposition(handle, deleteFile: true);",
            StringComparison.Ordinal);
        var fileStreamIndex = createOwnedMethod.IndexOf(
            "new FileStream(handle, FileAccess.ReadWrite)",
            StringComparison.Ordinal);
        var leaseIndex = createOwnedMethod.IndexOf(
            "new SnapshotSourceFileLease(",
            fileStreamIndex,
            StringComparison.Ordinal);
        var clearDeleteIndex = createOwnedMethod.IndexOf(
            "SetDeleteDisposition(handle, deleteFile: false);",
            leaseIndex,
            StringComparison.Ordinal);
        Assert.True(
            deletePendingIndex >= 0 &&
            fileStreamIndex > deletePendingIndex &&
            leaseIndex > fileStreamIndex &&
            clearDeleteIndex > leaseIndex,
            "The created file must remain delete-pending until FileStream and the assignment-only lease are constructed.");

        var deleteOwnedStart = source.IndexOf(
            "public void DeleteOwnedFile()",
            StringComparison.Ordinal);
        var deleteOwnedEnd = source.IndexOf(
            "public void MoveTo(",
            deleteOwnedStart,
            StringComparison.Ordinal);
        Assert.True(
            deleteOwnedStart >= 0 &&
            deleteOwnedEnd > deleteOwnedStart);
        var deleteOwnedMethod =
            source[deleteOwnedStart..deleteOwnedEnd];
        Assert.Contains(
            "SetDeleteDisposition(",
            deleteOwnedMethod,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AssertStable();",
            deleteOwnedMethod,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SnapshotCommand_PostQuickCheckTempInPlaceRewriteIsBlocked()
    {
#if !DEBUG
        await Task.CompletedTask;
        return;
#else
        var repositoryRoot = FindRepositoryRoot();
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"sqlite-post-check-race-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var targetRoot = Path.Combine(testRoot, "target");
        var sourceDatabase = Path.Combine(sourceRoot, "data", "거래플랜.db");
        var targetDatabase = Path.Combine(targetRoot, "data", "거래플랜.db");
        var targetData = Path.GetDirectoryName(targetDatabase)!;
        var signal = GetSnapshotHookMarkerPath(
            targetData,
            "POST_QUICK_CHECK",
            "signal");
        var resume = GetSnapshotHookMarkerPath(
            targetData,
            "POST_QUICK_CHECK",
            "continue");
        var attacker = Path.Combine(testRoot, "valid-attacker.db");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceDatabase)!);
        Directory.CreateDirectory(targetData);
        Process? process = null;

        try
        {
            await using (var connection = new SqliteConnection(
                $"Data Source={sourceDatabase};Pooling=False"))
            {
                await connection.OpenAsync();
                await ExecuteNonQueryAsync(
                    connection,
                    "CREATE TABLE T (Id INTEGER PRIMARY KEY, V TEXT NOT NULL);" +
                    "INSERT INTO T(V) VALUES ('snapshot-row');");
            }
            await CreateRaceDatabaseAsync(attacker, "attacker-row");
            SqliteConnection.ClearAllPools();
            var attackerHash = ComputeSha256(attacker);

            var startInfo = CreateSnapshotStartInfo(
                repositoryRoot,
                sourceDatabase,
                targetDatabase,
                sourceRoot,
                targetRoot);
            ConfigureSnapshotTestHook(
                startInfo,
                targetData,
                "POST_QUICK_CHECK");
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "The post-check race child did not start.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            var signalTask =
                WaitForPathAsync(signal, TimeSpan.FromSeconds(30));
            if (await Task.WhenAny(
                    signalTask,
                    process.WaitForExitAsync()) != signalTask)
            {
                throw new InvalidOperationException(
                    "The post-check race child exited before signaling: " +
                    await stderrTask);
            }
            await signalTask;
            var temporaryDatabase = Assert.Single(
                Directory.GetFiles(
                    targetData,
                    "*.snapshot-*.tmp",
                    SearchOption.TopDirectoryOnly));

            var rewriteFailure = Record.Exception(() =>
                RewriteFileInPlace(attacker, temporaryDatabase));
            Assert.True(
                rewriteFailure is IOException or
                    UnauthorizedAccessException,
                $"Unexpected in-place rewrite result: {rewriteFailure}");
            Assert.Equal(attackerHash, ComputeSha256(attacker));
            Assert.True(File.Exists(temporaryDatabase));

            await File.WriteAllBytesAsync(resume, [1]);
            await process.WaitForExitAsync().WaitAsync(
                TimeSpan.FromSeconds(30));
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            Assert.True(
                process.ExitCode == 0,
                stdout + Environment.NewLine + stderr);
            Assert.Empty(stderr);
            Assert.Contains(
                "quick_check=ok",
                stdout,
                StringComparison.Ordinal);
            Assert.True(File.Exists(targetDatabase));
            Assert.Equal(attackerHash, ComputeSha256(attacker));
            Assert.Equal(
                "snapshot-row",
                await ReadRaceDatabaseValueAsync(targetDatabase));
        }
        finally
        {
            try
            {
                if (Directory.Exists(targetData) &&
                    !File.Exists(resume))
                {
                    await File.WriteAllBytesAsync(resume, [1]);
                }
                if (process is { HasExited: false })
                {
                    await process.WaitForExitAsync().WaitAsync(
                        TimeSpan.FromSeconds(5));
                }
            }
            catch (TimeoutException)
            {
                process?.Kill(entireProcessTree: true);
            }
            finally
            {
                process?.Dispose();
            }
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
#endif
    }

    [Fact]
    public async Task SnapshotCommand_PostQuickCheckTempReplacementIsBlocked()
    {
#if !DEBUG
        await Task.CompletedTask;
        return;
#else
        var repositoryRoot = FindRepositoryRoot();
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"sqlite-post-check-replacement-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var targetRoot = Path.Combine(testRoot, "target");
        var sourceDatabase = Path.Combine(sourceRoot, "data", "거래플랜.db");
        var targetDatabase = Path.Combine(targetRoot, "data", "거래플랜.db");
        var targetData = Path.GetDirectoryName(targetDatabase)!;
        var signal = GetSnapshotHookMarkerPath(
            targetData,
            "POST_QUICK_CHECK",
            "signal");
        var resume = GetSnapshotHookMarkerPath(
            targetData,
            "POST_QUICK_CHECK",
            "continue");
        var victim = Path.Combine(testRoot, "replacement-victim.db");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceDatabase)!);
        Directory.CreateDirectory(targetData);
        Process? process = null;

        try
        {
            await CreateRaceDatabaseAsync(sourceDatabase, "snapshot-row");
            await File.WriteAllBytesAsync(
                victim,
                System.Text.Encoding.UTF8.GetBytes(
                    "replacement-victim-must-remain"));
            SqliteConnection.ClearAllPools();
            var victimHash = ComputeSha256(victim);

            var startInfo = CreateSnapshotStartInfo(
                repositoryRoot,
                sourceDatabase,
                targetDatabase,
                sourceRoot,
                targetRoot);
            ConfigureSnapshotTestHook(
                startInfo,
                targetData,
                "POST_QUICK_CHECK");
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "The post-check replacement child did not start.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            var signalTask =
                WaitForPathAsync(signal, TimeSpan.FromSeconds(30));
            if (await Task.WhenAny(
                    signalTask,
                    process.WaitForExitAsync()) != signalTask)
            {
                throw new InvalidOperationException(
                    "The post-check replacement child exited before signaling: " +
                    await stderrTask);
            }
            await signalTask;
            var temporaryDatabase = Assert.Single(
                Directory.GetFiles(
                    targetData,
                    "*.snapshot-*.tmp",
                    SearchOption.TopDirectoryOnly));

            var replacementFailure = Record.Exception(() =>
                File.Move(victim, temporaryDatabase, overwrite: true));
            Assert.True(
                replacementFailure is IOException or
                    UnauthorizedAccessException,
                $"Unexpected replacement result: {replacementFailure}");
            Assert.Equal(victimHash, ComputeSha256(victim));
            Assert.True(File.Exists(temporaryDatabase));

            await File.WriteAllBytesAsync(resume, [1]);
            await process.WaitForExitAsync().WaitAsync(
                TimeSpan.FromSeconds(30));
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            Assert.True(
                process.ExitCode == 0,
                stdout + Environment.NewLine + stderr);
            Assert.Empty(stderr);
            Assert.Contains(
                "quick_check=ok",
                stdout,
                StringComparison.Ordinal);
            Assert.True(File.Exists(targetDatabase));
            Assert.Equal(victimHash, ComputeSha256(victim));
            Assert.Equal(
                "snapshot-row",
                await ReadRaceDatabaseValueAsync(targetDatabase));
        }
        finally
        {
            try
            {
                if (Directory.Exists(targetData) &&
                    !File.Exists(resume))
                {
                    await File.WriteAllBytesAsync(resume, [1]);
                }
                if (process is { HasExited: false })
                {
                    await process.WaitForExitAsync().WaitAsync(
                        TimeSpan.FromSeconds(5));
                }
            }
            catch (TimeoutException)
            {
                process?.Kill(entireProcessTree: true);
            }
            finally
            {
                process?.Dispose();
            }
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
#endif
    }

    [Fact]
    public async Task SnapshotCommand_PostCommittedFingerprintInPlaceRewriteIsBlocked()
    {
#if !DEBUG
        await Task.CompletedTask;
        return;
#else
        var repositoryRoot = FindRepositoryRoot();
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"sqlite-committed-race-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var targetRoot = Path.Combine(testRoot, "target");
        var sourceDatabase = Path.Combine(sourceRoot, "data", "거래플랜.db");
        var targetDatabase = Path.Combine(targetRoot, "data", "거래플랜.db");
        var targetData = Path.GetDirectoryName(targetDatabase)!;
        var signal = GetSnapshotHookMarkerPath(
            targetData,
            "POST_COMMITTED_FINGERPRINT",
            "signal");
        var resume = GetSnapshotHookMarkerPath(
            targetData,
            "POST_COMMITTED_FINGERPRINT",
            "continue");
        var attacker = Path.Combine(testRoot, "valid-attacker.db");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceDatabase)!);
        Directory.CreateDirectory(targetData);
        Process? process = null;

        try
        {
            await CreateRaceDatabaseAsync(sourceDatabase, "snapshot-row");
            await CreateRaceDatabaseAsync(attacker, "attacker-row");
            SqliteConnection.ClearAllPools();
            var attackerHash = ComputeSha256(attacker);

            var startInfo = CreateSnapshotStartInfo(
                repositoryRoot,
                sourceDatabase,
                targetDatabase,
                sourceRoot,
                targetRoot);
            ConfigureSnapshotTestHook(
                startInfo,
                targetData,
                "POST_COMMITTED_FINGERPRINT");
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "The committed-fingerprint race child did not start.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            var signalTask =
                WaitForPathAsync(signal, TimeSpan.FromSeconds(30));
            if (await Task.WhenAny(
                    signalTask,
                    process.WaitForExitAsync()) != signalTask)
            {
                throw new InvalidOperationException(
                    "The committed-fingerprint race child exited before signaling: " +
                    await stderrTask);
            }
            await signalTask;
            Assert.True(File.Exists(targetDatabase));

            var rewriteFailure = Record.Exception(() =>
                RewriteFileInPlace(attacker, targetDatabase));
            Assert.True(
                rewriteFailure is IOException or
                    UnauthorizedAccessException,
                $"Unexpected committed in-place rewrite result: {rewriteFailure}");
            Assert.Equal(attackerHash, ComputeSha256(attacker));

            await File.WriteAllBytesAsync(resume, [1]);
            await process.WaitForExitAsync().WaitAsync(
                TimeSpan.FromSeconds(30));
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            Assert.Equal(0, process.ExitCode);
            Assert.Empty(stderr);
            Assert.Contains(
                $"target_sha256={ComputeSha256(targetDatabase)}",
                stdout,
                StringComparison.Ordinal);
            Assert.Equal(
                "snapshot-row",
                await ReadRaceDatabaseValueAsync(targetDatabase));
        }
        finally
        {
            try
            {
                if (Directory.Exists(targetData) &&
                    !File.Exists(resume))
                {
                    await File.WriteAllBytesAsync(resume, [1]);
                }
                if (process is { HasExited: false })
                {
                    await process.WaitForExitAsync().WaitAsync(
                        TimeSpan.FromSeconds(5));
                }
            }
            catch (TimeoutException)
            {
                process?.Kill(entireProcessTree: true);
            }
            finally
            {
                process?.Dispose();
            }
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
#endif
    }

    [Fact]
    public async Task SnapshotCommand_TestPhaseHookRequiresExplicitDebugAuthorization()
    {
#if !DEBUG
        await Task.CompletedTask;
        return;
#else
        var repositoryRoot = FindRepositoryRoot();
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"sqlite-hook-auth-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var targetRoot = Path.Combine(testRoot, "target");
        var sourceDatabase = Path.Combine(sourceRoot, "data", "거래플랜.db");
        var targetDatabase = Path.Combine(targetRoot, "data", "거래플랜.db");
        var targetData = Path.GetDirectoryName(targetDatabase)!;
        var signal = GetSnapshotHookMarkerPath(
            targetData,
            "POST_QUICK_CHECK",
            "signal");
        Directory.CreateDirectory(targetData);

        try
        {
            await CreateRaceDatabaseAsync(sourceDatabase, "snapshot-row");
            SqliteConnection.ClearAllPools();
            var startInfo = CreateSnapshotStartInfo(
                repositoryRoot,
                sourceDatabase,
                targetDatabase,
                sourceRoot,
                targetRoot);
            startInfo.Environment[SnapshotTestPhaseEnvironmentKey] =
                "POST_QUICK_CHECK";
            startInfo.Environment[SnapshotTestRootEnvironmentKey] =
                targetData;

            var result = await RunProcessAsync(
                startInfo,
                TimeSpan.FromSeconds(30));

            Assert.Equal(1, result.ExitCode);
            Assert.Empty(result.Stdout);
            Assert.Matches(
                "^snapshot_sqlite_failed reason_code=[a-z0-9_]+\\r?\\n?$",
                result.Stderr);
            Assert.False(File.Exists(signal));
            Assert.False(File.Exists(targetDatabase));
            Assert.Empty(
                Directory.GetFiles(
                    targetData,
                    "*.snapshot-*.tmp*",
                    SearchOption.TopDirectoryOnly));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
#endif
    }

    [Fact]
    public async Task SnapshotCommand_TestPhaseHookIsRejectedBeforeArtifactsInRelease()
    {
#if DEBUG
        await Task.CompletedTask;
        return;
#else
        var repositoryRoot = FindRepositoryRoot();
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"sqlite-release-hook-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var targetRoot = Path.Combine(testRoot, "target");
        var sourceDatabase = Path.Combine(sourceRoot, "data", "거래플랜.db");
        var targetDatabase = Path.Combine(targetRoot, "data", "거래플랜.db");
        var targetData = Path.GetDirectoryName(targetDatabase)!;
        var signal = GetSnapshotHookMarkerPath(
            targetData,
            "POST_QUICK_CHECK",
            "signal");
        Directory.CreateDirectory(targetData);

        try
        {
            await CreateRaceDatabaseAsync(sourceDatabase, "snapshot-row");
            SqliteConnection.ClearAllPools();
            var startInfo = CreateSnapshotStartInfo(
                repositoryRoot,
                sourceDatabase,
                targetDatabase,
                sourceRoot,
                targetRoot);
            ConfigureSnapshotTestHook(
                startInfo,
                targetData,
                "POST_QUICK_CHECK");

            var result = await RunProcessAsync(
                startInfo,
                TimeSpan.FromSeconds(30));

            Assert.Equal(1, result.ExitCode);
            Assert.Empty(result.Stdout);
            Assert.Matches(
                "^snapshot_sqlite_failed reason_code=[a-z0-9_]+\\r?\\n?$",
                result.Stderr);
            Assert.False(File.Exists(signal));
            Assert.False(File.Exists(targetDatabase));
            Assert.Empty(
                Directory.GetFiles(
                    targetData,
                    "*.snapshot-*.tmp*",
                    SearchOption.TopDirectoryOnly));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
#endif
    }

    [Fact]
    public async Task SnapshotCommand_HookTargetDirectoryCannotBeReplacedByJunction()
    {
#if !DEBUG
        await Task.CompletedTask;
        return;
#else
        var repositoryRoot = FindRepositoryRoot();
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"sqlite-hook-root-race-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var targetRoot = Path.Combine(testRoot, "target");
        var sourceDatabase = Path.Combine(sourceRoot, "data", "거래플랜.db");
        var targetDatabase = Path.Combine(targetRoot, "data", "거래플랜.db");
        var targetData = Path.GetDirectoryName(targetDatabase)!;
        var displacedData = Path.Combine(targetRoot, "displaced-data");
        var externalRoot = Path.Combine(testRoot, "external-victim");
        var signal = GetSnapshotHookMarkerPath(
            targetData,
            "POST_QUICK_CHECK",
            "signal");
        var resume = GetSnapshotHookMarkerPath(
            targetData,
            "POST_QUICK_CHECK",
            "continue");
        Directory.CreateDirectory(targetData);
        Directory.CreateDirectory(externalRoot);
        Process? process = null;

        try
        {
            await CreateRaceDatabaseAsync(sourceDatabase, "snapshot-row");
            SqliteConnection.ClearAllPools();
            var startInfo = CreateSnapshotStartInfo(
                repositoryRoot,
                sourceDatabase,
                targetDatabase,
                sourceRoot,
                targetRoot);
            ConfigureSnapshotTestHook(
                startInfo,
                targetData,
                "POST_QUICK_CHECK");
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "The hook-root race child did not start.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            var signalTask =
                WaitForPathAsync(signal, TimeSpan.FromSeconds(30));
            if (await Task.WhenAny(
                    signalTask,
                    process.WaitForExitAsync()) != signalTask)
            {
                throw new InvalidOperationException(
                    "The hook-root race child exited before signaling: " +
                    await stderrTask);
            }
            await signalTask;

            var replacementFailure = Record.Exception(
                () => Directory.Move(targetData, displacedData));
            Assert.True(
                replacementFailure is IOException or
                    UnauthorizedAccessException,
                $"Unexpected hook-root replacement result: {replacementFailure}");
            Assert.False(Directory.Exists(displacedData));
            Assert.Empty(Directory.EnumerateFileSystemEntries(externalRoot));

            await File.WriteAllBytesAsync(resume, [1]);
            await process.WaitForExitAsync().WaitAsync(
                TimeSpan.FromSeconds(30));
            Assert.Equal(0, process.ExitCode);
            Assert.Empty(await stderrTask);
            Assert.Contains(
                "quick_check=ok",
                await stdoutTask,
                StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFileSystemEntries(externalRoot));
        }
        finally
        {
            try
            {
                if (Directory.Exists(targetData) &&
                    !File.Exists(resume))
                {
                    await File.WriteAllBytesAsync(resume, [1]);
                }
                if (process is { HasExited: false })
                {
                    await process.WaitForExitAsync().WaitAsync(
                        TimeSpan.FromSeconds(5));
                }
            }
            catch (TimeoutException)
            {
                process?.Kill(entireProcessTree: true);
            }
            finally
            {
                process?.Dispose();
            }
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(displacedData) &&
                !Directory.Exists(targetData))
            {
                Directory.Move(displacedData, targetData);
            }
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
#endif
    }

    [Theory]
    [InlineData(true, "-wal")]
    [InlineData(true, "-shm")]
    [InlineData(true, "-journal")]
    [InlineData(false, "-wal")]
    [InlineData(false, "-shm")]
    [InlineData(false, "-journal")]
    public async Task SnapshotCommand_DanglingSidecarReparseEntryIsRejected(
        bool targetSidecar,
        string suffix)
    {
        var repositoryRoot = FindRepositoryRoot();
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"sqlite-dangling-sidecar-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var targetRoot = Path.Combine(testRoot, "target");
        var sourceDatabase = Path.Combine(sourceRoot, "data", "거래플랜.db");
        var targetDatabase = Path.Combine(targetRoot, "data", "거래플랜.db");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceDatabase)!);
        Directory.CreateDirectory(Path.GetDirectoryName(targetDatabase)!);
        var reparsePath =
            (targetSidecar ? targetDatabase : sourceDatabase) + suffix;
        var missingTarget = Path.Combine(
            testRoot,
            $"missing-{Guid.NewGuid():N}");
        var victim = Path.Combine(testRoot, "preserved-victim.bin");

        try
        {
            await using (var connection = new SqliteConnection(
                $"Data Source={sourceDatabase};Pooling=False"))
            {
                await connection.OpenAsync();
                await ExecuteNonQueryAsync(
                    connection,
                    "CREATE TABLE T (Id INTEGER PRIMARY KEY);");
            }
            SqliteConnection.ClearAllPools();
            await File.WriteAllBytesAsync(
                victim,
                System.Text.Encoding.UTF8.GetBytes("preserve-victim"));
            var victimHash = ComputeSha256(victim);
            CreateDirectoryJunctionOrThrow(reparsePath, missingTarget);

            var result = await RunProcessAsync(
                ResolveDotnetPath(),
                repositoryRoot,
                TimeSpan.FromSeconds(30),
                [
                    ResolveSyncDiagToolPath(repositoryRoot),
                    "snapshot-sqlite",
                    sourceDatabase,
                    targetDatabase
                ],
                new Dictionary<string, string?>
                {
                    ["GEORAEPLAN_TEST_MODE"] = "1",
                    ["GEORAEPLAN_SOURCE_SNAPSHOT_ROOT"] = sourceRoot,
                    ["GEORAEPLAN_TARGET_SNAPSHOT_ROOT"] = targetRoot
                });

            Assert.Equal(1, result.ExitCode);
            Assert.Empty(result.Stdout);
            Assert.Matches(
                "^snapshot_sqlite_failed reason_code=[a-z0-9_]+\\r?\\n?$",
                result.Stderr);
            Assert.Equal(victimHash, ComputeSha256(victim));
            Assert.False(File.Exists(targetDatabase));
        }
        finally
        {
            try
            {
                Directory.Delete(reparsePath);
            }
            catch (DirectoryNotFoundException)
            {
            }
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("-wal")]
    [InlineData("-shm")]
    public async Task SnapshotCommand_SourceSidecarReplacementIsBlockedAfterBackup(
        string suffix)
    {
#if !DEBUG
        _ = suffix;
        await Task.CompletedTask;
        return;
#else
        var repositoryRoot = FindRepositoryRoot();
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"sqlite-sidecar-swap-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var targetRoot = Path.Combine(testRoot, "target");
        var sourceDatabase =
            Path.Combine(sourceRoot, "data", "거래플랜.db");
        var targetDatabase =
            Path.Combine(targetRoot, "data", "거래플랜.db");
        var targetData = Path.GetDirectoryName(targetDatabase)!;
        var signal = GetSnapshotHookMarkerPath(
            targetData,
            "POST_SOURCE_BACKUP",
            "signal");
        var resume = GetSnapshotHookMarkerPath(
            targetData,
            "POST_SOURCE_BACKUP",
            "continue");
        var victim = Path.Combine(testRoot, "sidecar-swap-victim.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceDatabase)!);
        Directory.CreateDirectory(targetData);
        Process? process = null;

        try
        {
            await using var sourceConnection = new SqliteConnection(
                $"Data Source={sourceDatabase};Pooling=False");
            await sourceConnection.OpenAsync();
            await ExecuteNonQueryAsync(
                sourceConnection,
                """
                PRAGMA journal_mode=WAL;
                PRAGMA wal_autocheckpoint=0;
                CREATE TABLE T (Id INTEGER PRIMARY KEY, V TEXT NOT NULL);
                INSERT INTO T(V) VALUES ('base');
                PRAGMA wal_checkpoint(TRUNCATE);
                INSERT INTO T(V) VALUES ('wal-row');
                """);
            var sidecar = sourceDatabase + suffix;
            Assert.True(File.Exists(sidecar));
            await File.WriteAllBytesAsync(
                victim,
                System.Text.Encoding.UTF8.GetBytes(
                    "sidecar-swap-victim-must-remain"));
            var victimHash = ComputeSha256(victim);

            var startInfo = CreateSnapshotStartInfo(
                repositoryRoot,
                sourceDatabase,
                targetDatabase,
                sourceRoot,
                targetRoot);
            ConfigureSnapshotTestHook(
                startInfo,
                targetData,
                "POST_SOURCE_BACKUP");
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "The source-sidecar race child did not start.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            var signalTask =
                WaitForPathAsync(signal, TimeSpan.FromSeconds(30));
            if (await Task.WhenAny(
                    signalTask,
                    process.WaitForExitAsync()) != signalTask)
            {
                throw new InvalidOperationException(
                    "The source-sidecar race child exited before signaling: " +
                    await stderrTask);
            }
            await signalTask;

            var replacementFailure = Record.Exception(() =>
                File.Move(victim, sidecar, overwrite: true));
            Assert.True(
                replacementFailure is IOException or
                    UnauthorizedAccessException,
                $"Unexpected sidecar replacement result: {replacementFailure}");
            Assert.Equal(victimHash, ComputeSha256(victim));
            Assert.True(File.Exists(sidecar));

            await File.WriteAllBytesAsync(resume, [1]);
            await process.WaitForExitAsync().WaitAsync(
                TimeSpan.FromSeconds(30));
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            Assert.Equal(0, process.ExitCode);
            Assert.Empty(stderr);
            Assert.Contains(
                "quick_check=ok",
                stdout,
                StringComparison.Ordinal);
            Assert.True(File.Exists(targetDatabase));
            Assert.Equal(victimHash, ComputeSha256(victim));
        }
        finally
        {
            try
            {
                if (Directory.Exists(targetData) &&
                    !File.Exists(resume))
                {
                    await File.WriteAllBytesAsync(resume, [1]);
                }
                if (process is { HasExited: false })
                {
                    await process.WaitForExitAsync().WaitAsync(
                        TimeSpan.FromSeconds(5));
                }
            }
            catch (TimeoutException)
            {
                process?.Kill(entireProcessTree: true);
            }
            finally
            {
                process?.Dispose();
            }
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
#endif
    }

    [Fact]
    public void SnapshotCommand_IsWiredBeforeMutableDesktopDatabaseInitialization()
    {
        var programPath = Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "SyncDiag",
            "Program.cs");
        var source = File.ReadAllText(programPath);
        var snapshotBranch = source.IndexOf(
            "string.Equals(command, \"snapshot-sqlite\"",
            StringComparison.Ordinal);
        var mutableContext = source.IndexOf(
            "await using var db = new LocalDbContext();",
            StringComparison.Ordinal);
        var snapshotMethodStart = source.IndexOf(
            "static SqliteSnapshotResult CreateStandaloneSqliteSnapshot(",
            StringComparison.Ordinal);
        var snapshotMethodEnd = source.IndexOf(
            "static SqliteSnapshotPaths ValidateSqliteSnapshotPaths(",
            snapshotMethodStart,
            StringComparison.Ordinal);

        Assert.True(snapshotBranch >= 0);
        Assert.True(
            mutableContext > snapshotBranch,
            "The snapshot command must return before LocalDbContext can mutate application data.");
        Assert.True(
            snapshotMethodStart >= 0 &&
            snapshotMethodEnd > snapshotMethodStart,
            "The standalone SQLite snapshot method boundary was not found.");
        var snapshotMethod = source[
            snapshotMethodStart..snapshotMethodEnd];
        Assert.Contains(
            "\"GEORAEPLAN_SOURCE_SNAPSHOT_ROOT\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"GEORAEPLAN_TARGET_SNAPSHOT_ROOT\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Mode = SqliteOpenMode.ReadOnly",
            snapshotMethod,
            StringComparison.Ordinal);
        Assert.Contains(
            "Mode = SqliteOpenMode.Memory",
            snapshotMethod,
            StringComparison.Ordinal);
        Assert.Contains(
            "SnapshotSourceFileLease.CreateOwned(",
            snapshotMethod,
            StringComparison.Ordinal);
        Assert.Contains(
            "SnapshotSourceFileLease.Acquire(",
            snapshotMethod,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"after source open\"",
            snapshotMethod,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"after source backup\"",
            snapshotMethod,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"after source close\"",
            snapshotMethod,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"before target commit\"",
            snapshotMethod,
            StringComparison.Ordinal);
        Assert.Contains(
            "sourceConnection.BackupDatabase(destinationConnection)",
            snapshotMethod,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"PRAGMA quick_check;\"",
            snapshotMethod,
            StringComparison.Ordinal);
        Assert.Contains(
            "SerializeAttestedSqliteSnapshot(",
            snapshotMethod,
            StringComparison.Ordinal);
        Assert.Contains(
            "temporaryLease.ReplaceContent(serializedDatabase)",
            snapshotMethod,
            StringComparison.Ordinal);
        Assert.Contains(
            "temporaryLease.MoveTo(",
            snapshotMethod,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "File.Move(",
            snapshotMethod,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "File.Replace(",
            snapshotMethod,
            StringComparison.Ordinal);
        Assert.Contains(
            "NumberOfLinks",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "FileFlagOpenReparsePoint",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "snapshot_sqlite_failed reason_code=",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommandLineHelp_DistinguishesMissingCommandFromExplicitHelp()
    {
        var repositoryRoot = FindRepositoryRoot();
        var dotnetPath = ResolveDotnetPath();
        var toolPath = ResolveSyncDiagToolPath(
            repositoryRoot);
        var missingCommand = await RunProcessAsync(
            dotnetPath,
            repositoryRoot,
            TimeSpan.FromMinutes(1),
            [toolPath]);
        Assert.Equal(2, missingCommand.ExitCode);
        Assert.Empty(missingCommand.Stdout);
        Assert.StartsWith(
            "usage: SyncDiag ",
            missingCommand.Stderr,
            StringComparison.Ordinal);

        foreach (var helpArgument in new[] { "--help", "-h", "help" })
        {
            var explicitHelp = await RunProcessAsync(
                dotnetPath,
                repositoryRoot,
                TimeSpan.FromMinutes(1),
                [toolPath, helpArgument]);
            Assert.Equal(0, explicitHelp.ExitCode);
            Assert.StartsWith(
                "usage: SyncDiag ",
                explicitHelp.Stdout,
                StringComparison.Ordinal);
            Assert.Empty(explicitHelp.Stderr);
        }
    }

    [Fact]
    public async Task SnapshotCommand_CopiesOpenWalSourceAndAtomicallyCreatesTarget()
    {
        var repositoryRoot = FindRepositoryRoot();
        var dotnetPath = ResolveDotnetPath();
        var toolPath = ResolveSyncDiagToolPath(
            repositoryRoot);

        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"sqlite-online-snapshot-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var targetRoot = Path.Combine(testRoot, "target");
        var sourceData = Path.Combine(sourceRoot, "data");
        var targetData = Path.Combine(targetRoot, "data");
        var sourceDatabase = Path.Combine(sourceData, "거래플랜.db");
        var targetDatabase = Path.Combine(targetData, "거래플랜.db");
        Directory.CreateDirectory(sourceData);
        Directory.CreateDirectory(targetData);

        try
        {
            await using var sourceConnection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = sourceDatabase,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Pooling = false
                }.ToString());
            await sourceConnection.OpenAsync();
            await ExecuteNonQueryAsync(
                sourceConnection,
                """
                PRAGMA journal_mode=WAL;
                PRAGMA wal_autocheckpoint=0;
                CREATE TABLE SnapshotRows (
                    Id INTEGER PRIMARY KEY,
                    Value TEXT NOT NULL
                );
                INSERT INTO SnapshotRows (Value) VALUES ('main-row');
                """);
            await ExecuteNonQueryAsync(
                sourceConnection,
                "PRAGMA wal_checkpoint(TRUNCATE);");
            await ExecuteNonQueryAsync(
                sourceConnection,
                "INSERT INTO SnapshotRows (Value) VALUES ('open-wal-row');");
            Assert.True(File.Exists(sourceDatabase + "-wal"));
            Assert.True(new FileInfo(sourceDatabase + "-wal").Length > 0);
            Assert.True(File.Exists(sourceDatabase + "-shm"));
            Assert.True(new FileInfo(sourceDatabase + "-shm").Length > 0);

            var sourceMainHashBefore = ComputeSha256(sourceDatabase);
            var sourceWalHashBefore = ComputeSha256(sourceDatabase + "-wal");

            var environment = new Dictionary<string, string?>
            {
                ["GEORAEPLAN_TEST_MODE"] = "1",
                ["GEORAEPLAN_SOURCE_SNAPSHOT_ROOT"] = sourceRoot,
                ["GEORAEPLAN_TARGET_SNAPSHOT_ROOT"] = targetRoot
            };
            var result = await RunProcessAsync(
                dotnetPath,
                repositoryRoot,
                TimeSpan.FromMinutes(1),
                [toolPath, "snapshot-sqlite", sourceDatabase, targetDatabase],
                environment);

            Assert.True(
                result.ExitCode == 0,
                result.Stdout + Environment.NewLine + result.Stderr);
            var output = result.Stdout
                .Split(
                    ['\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(5, output.Length);
            Assert.Equal("snapshot_succeeded=True", output[0]);
            Assert.StartsWith("target_length=", output[1], StringComparison.Ordinal);
            Assert.Matches("^target_sha256=[0-9A-F]{64}$", output[2]);
            Assert.Equal("quick_check=ok", output[3]);
            Assert.Equal("sidecar_count=0", output[4]);
            Assert.Equal(
                new FileInfo(targetDatabase).Length,
                long.Parse(
                    output[1]["target_length=".Length..],
                    System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(
                ComputeSha256(targetDatabase),
                output[2]["target_sha256=".Length..]);
            Assert.DoesNotContain(sourceDatabase, result.Stdout, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(targetDatabase, result.Stdout, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("open-wal-row", result.Stdout, StringComparison.Ordinal);

            Assert.Equal(sourceMainHashBefore, ComputeSha256(sourceDatabase));
            Assert.Equal(sourceWalHashBefore, ComputeSha256(sourceDatabase + "-wal"));
            // SQLite WAL readers may update transient read-marks in -shm while
            // preserving the authoritative main database and WAL bytes.
            Assert.True(File.Exists(sourceDatabase + "-shm"));
            Assert.True(new FileInfo(sourceDatabase + "-shm").Length > 0);
            Assert.Equal(0, CountSidecars(targetDatabase));
            Assert.Empty(
                Directory.GetFiles(
                    targetData,
                    "*.snapshot-*.tmp*",
                    SearchOption.TopDirectoryOnly));

            await using var verificationConnection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = targetDatabase,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false
                }.ToString());
            await verificationConnection.OpenAsync();
            Assert.Equal(
                2L,
                await ExecuteScalarInt64Async(
                    verificationConnection,
                    "SELECT COUNT(*) FROM SnapshotRows;"));
            Assert.Equal(
                "delete",
                (await ExecuteScalarTextAsync(
                    verificationConnection,
                    "PRAGMA journal_mode;")).ToLowerInvariant());
            Assert.Equal(
                "ok",
                await ExecuteScalarTextAsync(
                    verificationConnection,
                    "PRAGMA quick_check;"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SnapshotCommand_ExistingTargetIsRejectedAndPreserved()
    {
        var repositoryRoot = FindRepositoryRoot();
        var dotnetPath = ResolveDotnetPath();
        var toolPath = ResolveSyncDiagToolPath(repositoryRoot);
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"sqlite-existing-target-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var targetRoot = Path.Combine(testRoot, "target");
        var sourceDatabase = Path.Combine(
            sourceRoot,
            "data",
            "거래플랜.db");
        var targetDatabase = Path.Combine(
            targetRoot,
            "data",
            "거래플랜.db");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceDatabase)!);
        Directory.CreateDirectory(Path.GetDirectoryName(targetDatabase)!);

        try
        {
            await CreateExistingTargetAsync(sourceDatabase);
            await CreateExistingTargetAsync(targetDatabase);
            var targetHardLink = Path.Combine(
                Path.GetDirectoryName(targetDatabase)!,
                "preserved-target-hardlink.db");
            CreateHardLinkOrThrow(
                targetHardLink,
                targetDatabase);
            var sourceHashBefore = ComputeSha256(sourceDatabase);
            var targetHashBefore = ComputeSha256(targetDatabase);

            var result = await RunProcessAsync(
                dotnetPath,
                repositoryRoot,
                TimeSpan.FromMinutes(1),
                [toolPath, "snapshot-sqlite", sourceDatabase, targetDatabase],
                new Dictionary<string, string?>
                {
                    ["GEORAEPLAN_TEST_MODE"] = "1",
                    ["GEORAEPLAN_SOURCE_SNAPSHOT_ROOT"] = sourceRoot,
                    ["GEORAEPLAN_TARGET_SNAPSHOT_ROOT"] = targetRoot
                });

            Assert.Equal(1, result.ExitCode);
            Assert.Empty(result.Stdout);
            Assert.Matches(
                "^snapshot_sqlite_failed reason_code=[a-z0-9_]+\\r?\\n?$",
                result.Stderr);
            Assert.DoesNotContain(
                sourceDatabase,
                result.Stderr,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                targetDatabase,
                result.Stderr,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(sourceHashBefore, ComputeSha256(sourceDatabase));
            Assert.Equal(targetHashBefore, ComputeSha256(targetDatabase));
            Assert.Equal(targetHashBefore, ComputeSha256(targetHardLink));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SnapshotCommand_HardLinkedSourceIsRejectedWithoutTargetMutation()
    {
        var repositoryRoot = FindRepositoryRoot();
        var dotnetPath = ResolveDotnetPath();
        var toolPath = ResolveSyncDiagToolPath(repositoryRoot);
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"sqlite-hardlink-source-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var targetRoot = Path.Combine(testRoot, "target");
        var sourceDatabase = Path.Combine(
            sourceRoot,
            "data",
            "거래플랜.db");
        var targetDatabase = Path.Combine(
            targetRoot,
            "data",
            "거래플랜.db");
        Directory.CreateDirectory(Path.GetDirectoryName(targetDatabase)!);

        try
        {
            await CreateExistingTargetAsync(sourceDatabase);
            var sourceHardLink = Path.Combine(
                Path.GetDirectoryName(sourceDatabase)!,
                "source-hardlink.db");
            CreateHardLinkOrThrow(
                sourceHardLink,
                sourceDatabase);
            var sourceHashBefore = ComputeSha256(sourceDatabase);

            var result = await RunProcessAsync(
                dotnetPath,
                repositoryRoot,
                TimeSpan.FromMinutes(1),
                [toolPath, "snapshot-sqlite", sourceDatabase, targetDatabase],
                new Dictionary<string, string?>
                {
                    ["GEORAEPLAN_TEST_MODE"] = "1",
                    ["GEORAEPLAN_SOURCE_SNAPSHOT_ROOT"] = sourceRoot,
                    ["GEORAEPLAN_TARGET_SNAPSHOT_ROOT"] = targetRoot
                });

            Assert.Equal(1, result.ExitCode);
            Assert.Empty(result.Stdout);
            Assert.Matches(
                "^snapshot_sqlite_failed reason_code=[a-z0-9_]+\\r?\\n?$",
                result.Stderr);
            Assert.Equal(sourceHashBefore, ComputeSha256(sourceDatabase));
            Assert.Equal(sourceHashBefore, ComputeSha256(sourceHardLink));
            Assert.False(File.Exists(targetDatabase));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SnapshotCommand_ReparseSourceIsRejectedWithoutTargetMutation()
    {
        var repositoryRoot = FindRepositoryRoot();
        var dotnetPath = ResolveDotnetPath();
        var toolPath = ResolveSyncDiagToolPath(repositoryRoot);
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"sqlite-reparse-source-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var targetRoot = Path.Combine(testRoot, "target");
        var sourceData = Path.Combine(sourceRoot, "data");
        var targetData = Path.Combine(targetRoot, "data");
        var sourceDatabase = Path.Combine(
            sourceData,
            "거래플랜.db");
        var realDatabase = Path.Combine(
            testRoot,
            "real-data",
            "거래플랜.db");
        var targetDatabase = Path.Combine(
            targetData,
            "거래플랜.db");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(realDatabase)!);
        Directory.CreateDirectory(targetData);

        try
        {
            await CreateExistingTargetAsync(realDatabase);
            CreateDirectoryJunctionOrThrow(
                sourceData,
                Path.GetDirectoryName(realDatabase)!);
            var realHashBefore = ComputeSha256(realDatabase);

            var result = await RunProcessAsync(
                dotnetPath,
                repositoryRoot,
                TimeSpan.FromMinutes(1),
                [toolPath, "snapshot-sqlite", sourceDatabase, targetDatabase],
                new Dictionary<string, string?>
                {
                    ["GEORAEPLAN_TEST_MODE"] = "1",
                    ["GEORAEPLAN_SOURCE_SNAPSHOT_ROOT"] = sourceRoot,
                    ["GEORAEPLAN_TARGET_SNAPSHOT_ROOT"] = targetRoot
                });

            Assert.Equal(1, result.ExitCode);
            Assert.Empty(result.Stdout);
            Assert.Matches(
                "^snapshot_sqlite_failed reason_code=[a-z0-9_]+\\r?\\n?$",
                result.Stderr);
            Assert.Equal(realHashBefore, ComputeSha256(realDatabase));
            Assert.False(File.Exists(targetDatabase));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (
                Directory.Exists(sourceData) &&
                (File.GetAttributes(sourceData) &
                 FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(sourceData);
            }
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SnapshotCommand_PrecommitTargetRaceFailsAndPreservesRacingFile()
    {
        var repositoryRoot = FindRepositoryRoot();
        var dotnetPath = ResolveDotnetPath();
        var toolPath = ResolveSyncDiagToolPath(repositoryRoot);
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"sqlite-target-race-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var targetRoot = Path.Combine(testRoot, "target");
        var sourceDatabase = Path.Combine(
            sourceRoot,
            "data",
            "거래플랜.db");
        var targetDatabase = Path.Combine(
            targetRoot,
            "data",
            "거래플랜.db");
        var targetData = Path.GetDirectoryName(targetDatabase)!;
        Directory.CreateDirectory(
            Path.GetDirectoryName(sourceDatabase)!);
        Directory.CreateDirectory(targetData);

        try
        {
            await using (var sourceConnection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = sourceDatabase,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Pooling = false
                }.ToString()))
            {
                await sourceConnection.OpenAsync();
                await ExecuteNonQueryAsync(
                    sourceConnection,
                    """
                    CREATE TABLE SnapshotPayload (
                        Id INTEGER PRIMARY KEY,
                        Payload BLOB NOT NULL
                    );
                    INSERT INTO SnapshotPayload (Payload)
                    VALUES (zeroblob(67108864));
                    """);
            }
            SqliteConnection.ClearAllPools();

            var startInfo = new ProcessStartInfo
            {
                FileName = dotnetPath,
                WorkingDirectory = repositoryRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add(toolPath);
            startInfo.ArgumentList.Add("snapshot-sqlite");
            startInfo.ArgumentList.Add(sourceDatabase);
            startInfo.ArgumentList.Add(targetDatabase);
            startInfo.Environment["GEORAEPLAN_TEST_MODE"] = "1";
            startInfo.Environment["GEORAEPLAN_SOURCE_SNAPSHOT_ROOT"] =
                sourceRoot;
            startInfo.Environment["GEORAEPLAN_TARGET_SNAPSHOT_ROOT"] =
                targetRoot;

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "The snapshot race child did not start.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (
                !process.HasExited &&
                Directory.GetFiles(
                    targetData,
                    "*.snapshot-*.tmp",
                    SearchOption.TopDirectoryOnly).Length == 0 &&
                DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }
            Assert.False(
                process.HasExited,
                "The snapshot completed before the precommit race was injected.");

            var racingBytes =
                System.Text.Encoding.UTF8.GetBytes(
                    "racing-target-must-be-preserved");
            await File.WriteAllBytesAsync(
                targetDatabase,
                racingBytes);
            await process.WaitForExitAsync().WaitAsync(
                TimeSpan.FromMinutes(1));
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            Assert.Equal(1, process.ExitCode);
            Assert.Empty(stdout);
            Assert.Matches(
                "^snapshot_sqlite_failed reason_code=[a-z0-9_]+\\r?\\n?$",
                stderr);
            Assert.Equal(
                racingBytes,
                await File.ReadAllBytesAsync(targetDatabase));
            Assert.Empty(
                Directory.GetFiles(
                    targetData,
                    "*.snapshot-*.tmp*",
                    SearchOption.TopDirectoryOnly));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SnapshotCommand_SidecarFreeWalHeaderSourceRemainsByteAndMetadataImmutable()
    {
        var repositoryRoot = FindRepositoryRoot();
        var dotnetPath = ResolveDotnetPath();
        var toolPath = ResolveSyncDiagToolPath(repositoryRoot);
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"sqlite-sidecar-free-wal-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var targetRoot = Path.Combine(testRoot, "target");
        var sourceData = Path.Combine(sourceRoot, "data");
        var targetData = Path.Combine(targetRoot, "data");
        var sourceDatabase = Path.Combine(sourceData, "거래플랜.db");
        var targetDatabase = Path.Combine(targetData, "거래플랜.db");
        Directory.CreateDirectory(sourceData);
        Directory.CreateDirectory(targetData);

        try
        {
            await using (var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = sourceDatabase,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Pooling = false
                }.ToString()))
            {
                await connection.OpenAsync();
                await ExecuteNonQueryAsync(
                    connection,
                    """
                    PRAGMA journal_mode=WAL;
                    PRAGMA wal_autocheckpoint=0;
                    CREATE TABLE SnapshotRows (
                        Id INTEGER PRIMARY KEY,
                        Value TEXT NOT NULL
                    );
                    INSERT INTO SnapshotRows (Value)
                    VALUES ('alpha'), ('beta'), ('gamma');
                    PRAGMA wal_checkpoint(TRUNCATE);
                    """);
            }
            SqliteConnection.ClearAllPools();
            foreach (var sidecar in EnumerateSidecars(sourceDatabase))
            {
                if (File.Exists(sidecar))
                    File.Delete(sidecar);
            }
            Assert.Equal(0, CountSidecars(sourceDatabase));

            var stableMtime = DateTime.UtcNow.AddMinutes(-10);
            File.SetLastWriteTimeUtc(sourceDatabase, stableMtime);
            var sourceLengthBefore = new FileInfo(sourceDatabase).Length;
            var sourceHashBefore = ComputeSha256(sourceDatabase);
            var sourceMtimeBefore =
                File.GetLastWriteTimeUtc(sourceDatabase);
            var logicalHashBefore =
                await ComputeSnapshotRowsLogicalHashAsync(
                    sourceDatabase,
                    immutable: true);

            var result = await RunProcessAsync(
                dotnetPath,
                repositoryRoot,
                TimeSpan.FromMinutes(1),
                [toolPath, "snapshot-sqlite", sourceDatabase, targetDatabase],
                new Dictionary<string, string?>
                {
                    ["GEORAEPLAN_TEST_MODE"] = "1",
                    ["GEORAEPLAN_SOURCE_SNAPSHOT_ROOT"] = sourceRoot,
                    ["GEORAEPLAN_TARGET_SNAPSHOT_ROOT"] = targetRoot
                });

            Assert.True(
                result.ExitCode == 0,
                result.Stdout + Environment.NewLine + result.Stderr);
            Assert.Equal(0, CountSidecars(sourceDatabase));
            Assert.Equal(
                sourceLengthBefore,
                new FileInfo(sourceDatabase).Length);
            Assert.Equal(sourceHashBefore, ComputeSha256(sourceDatabase));
            Assert.Equal(
                sourceMtimeBefore,
                File.GetLastWriteTimeUtc(sourceDatabase));
            Assert.Equal(
                logicalHashBefore,
                await ComputeSnapshotRowsLogicalHashAsync(
                    targetDatabase,
                    immutable: false));
            Assert.Equal(
                "ok",
                await ReadQuickCheckAsync(
                    sourceDatabase,
                    immutable: true));
            Assert.Equal(
                "ok",
                await ReadQuickCheckAsync(
                    targetDatabase,
                    immutable: false));
            Assert.Equal(0, CountSidecars(targetDatabase));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SnapshotCommand_InvalidSourceCleansOwnedTemporaryFiles()
    {
        var repositoryRoot = FindRepositoryRoot();
        var dotnetPath = ResolveDotnetPath();
        var toolPath = ResolveSyncDiagToolPath(
            repositoryRoot);
        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"sqlite-online-snapshot-failure-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var targetRoot = Path.Combine(testRoot, "target");
        var sourceData = Path.Combine(sourceRoot, "data");
        var targetData = Path.Combine(targetRoot, "data");
        var sourceDatabase = Path.Combine(sourceData, "거래플랜.db");
        var targetDatabase = Path.Combine(targetData, "거래플랜.db");
        Directory.CreateDirectory(sourceData);
        Directory.CreateDirectory(targetData);

        try
        {
            var invalidSource = new byte[] { 7, 11, 13, 17, 19 };
            await File.WriteAllBytesAsync(sourceDatabase, invalidSource);

            var result = await RunProcessAsync(
                dotnetPath,
                repositoryRoot,
                TimeSpan.FromMinutes(1),
                [toolPath, "snapshot-sqlite", sourceDatabase, targetDatabase],
                new Dictionary<string, string?>
                {
                    ["GEORAEPLAN_TEST_MODE"] = "1",
                    ["GEORAEPLAN_SOURCE_SNAPSHOT_ROOT"] = sourceRoot,
                    ["GEORAEPLAN_TARGET_SNAPSHOT_ROOT"] = targetRoot
                });

            Assert.NotEqual(0, result.ExitCode);
            Assert.Empty(result.Stdout);
            Assert.Equal(invalidSource, await File.ReadAllBytesAsync(sourceDatabase));
            Assert.False(File.Exists(targetDatabase));
            Assert.Equal(0, CountSidecars(targetDatabase));
            Assert.Empty(
                Directory.GetFiles(
                    targetData,
                    "*.snapshot-*.tmp*",
                    SearchOption.TopDirectoryOnly));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    private static async Task CreateExistingTargetAsync(string databasePath)
    {
        Directory.CreateDirectory(
            Path.GetDirectoryName(databasePath)
            ?? throw new InvalidOperationException(
                "The SQLite fixture path has no parent."));
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString());
        await connection.OpenAsync();
        await ExecuteNonQueryAsync(
            connection,
            """
            CREATE TABLE PreviousTarget (
                Id INTEGER PRIMARY KEY,
                Value TEXT NOT NULL
            );
            INSERT INTO PreviousTarget (Value) VALUES ('must-be-replaced');
            """);
    }

    private static async Task CreateRaceDatabaseAsync(
        string databasePath,
        string value)
    {
        Directory.CreateDirectory(
            Path.GetDirectoryName(databasePath)
            ?? throw new InvalidOperationException(
                "The SQLite race fixture path has no parent."));
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE T (Id INTEGER PRIMARY KEY, V TEXT NOT NULL);" +
            "INSERT INTO T(V) VALUES ($value);";
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ReadRaceDatabaseValueAsync(
        string databasePath)
    {
        await using var connection = CreateReadConnection(
            databasePath,
            immutable: true);
        await connection.OpenAsync();
        return await ExecuteScalarTextAsync(
            connection,
            "SELECT V FROM T WHERE Id = 1;");
    }

    private static void RewriteFileInPlace(
        string sourcePath,
        string destinationPath)
    {
        using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        using var destination = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.ReadWrite);
        source.CopyTo(destination);
        destination.Flush(flushToDisk: true);
    }

    private static void ConfigureSnapshotTestHook(
        ProcessStartInfo startInfo,
        string targetDataDirectory,
        string phase)
    {
        startInfo.Environment["GEORAEPLAN_TEST_SEED_MODE"] = "1";
        startInfo.Environment[SnapshotTestOptInEnvironmentKey] = "1";
        startInfo.Environment[SnapshotTestRootEnvironmentKey] =
            Path.GetFullPath(targetDataDirectory);
        startInfo.Environment[SnapshotTestPhaseEnvironmentKey] = phase;
    }

    private static string GetSnapshotHookMarkerPath(
        string targetDataDirectory,
        string phase,
        string markerKind)
        => Path.Combine(
            targetDataDirectory,
            $".georaeplan-snapshot-test-" +
            $"{phase.ToLowerInvariant().Replace('_', '-')}.{markerKind}");

    private static void CreateHardLinkOrThrow(
        string hardLinkPath,
        string existingPath)
    {
        if (!NativeMethods.CreateHardLinkW(
                hardLinkPath,
                existingPath,
                IntPtr.Zero))
        {
            throw new InvalidOperationException(
                $"Could not create a hard-link fixture. Win32Error={Marshal.GetLastWin32Error()}");
        }
    }

    private static void CreateDirectoryJunctionOrThrow(
        string junctionPath,
        string targetPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(junctionPath);
        startInfo.ArgumentList.Add(targetPath);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "The directory-junction fixture did not start.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "Could not create the directory-junction fixture." +
                Environment.NewLine + stdout +
                Environment.NewLine + stderr);
        }
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ExecuteScalarInt64Async(
        SqliteConnection connection,
        string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string> ExecuteScalarTextAsync(
        SqliteConnection connection,
        string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync())
               ?? string.Empty;
    }

    private static int CountSidecars(string databasePath)
        => EnumerateSidecars(databasePath).Count(File.Exists);

    private static IEnumerable<string> EnumerateSidecars(
        string databasePath)
    {
        yield return databasePath + "-wal";
        yield return databasePath + "-shm";
        yield return databasePath + "-journal";
    }

    private static async Task<string> ComputeSnapshotRowsLogicalHashAsync(
        string databasePath,
        bool immutable)
    {
        var rows = new List<string>();
        await using var connection = CreateReadConnection(
            databasePath,
            immutable);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Id, Value FROM SnapshotRows ORDER BY Id;";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(
                $"{reader.GetInt64(0)}|{reader.GetString(1)}");
        }
        return Convert.ToHexString(
            SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(
                    string.Join("\n", rows))));
    }

    private static async Task<string> ReadQuickCheckAsync(
        string databasePath,
        bool immutable)
    {
        await using var connection = CreateReadConnection(
            databasePath,
            immutable);
        await connection.OpenAsync();
        return await ExecuteScalarTextAsync(
            connection,
            "PRAGMA quick_check;");
    }

    private static SqliteConnection CreateReadConnection(
        string databasePath,
        bool immutable)
        => new(
            new SqliteConnectionStringBuilder
            {
                DataSource = immutable
                    ? new Uri(databasePath).AbsoluteUri + "?immutable=1"
                    : databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string ResolveDotnetPath()
    {
        var isolatedSdk = @"D:\.dotnet-sdk\dotnet.exe";
        if (File.Exists(isolatedSdk))
            return isolatedSdk;

        var configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return !string.IsNullOrWhiteSpace(configured) &&
               File.Exists(configured)
             ? configured
             : "dotnet";
    }

    private static string ResolveSyncDiagToolPath(
        string repositoryRoot)
    {
        var testAssemblyDirectory = new DirectoryInfo(
            Path.GetDirectoryName(
                typeof(SqliteOnlineSnapshotCommandTests)
                    .Assembly
                    .Location)
            ?? throw new InvalidOperationException(
                "The desktop test assembly directory was not found."));
        var configuration = testAssemblyDirectory.Parent?.Name
            ?? throw new InvalidOperationException(
                "The desktop test build configuration was not found.");
        var toolPath = Path.Combine(
            repositoryRoot,
            "tools",
            "SyncDiag",
            "bin",
            configuration,
            testAssemblyDirectory.Name,
            "SyncDiag.dll");
        if (!File.Exists(toolPath))
        {
            throw new FileNotFoundException(
                $"The test project did not build the {configuration} SyncDiag dependency.",
                toolPath);
        }

        return toolPath;
    }

    private static ProcessStartInfo CreateSnapshotStartInfo(
        string repositoryRoot,
        string sourceDatabase,
        string targetDatabase,
        string sourceRoot,
        string targetRoot)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveDotnetPath(),
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(
            ResolveSyncDiagToolPath(repositoryRoot));
        startInfo.ArgumentList.Add("snapshot-sqlite");
        startInfo.ArgumentList.Add(sourceDatabase);
        startInfo.ArgumentList.Add(targetDatabase);
        startInfo.Environment["GEORAEPLAN_TEST_MODE"] = "1";
        startInfo.Environment["GEORAEPLAN_SOURCE_SNAPSHOT_ROOT"] =
            sourceRoot;
        startInfo.Environment["GEORAEPLAN_TARGET_SNAPSHOT_ROOT"] =
            targetRoot;
        return startInfo;
    }

    private static async Task WaitForPathAsync(
        string path,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!File.Exists(path))
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException(
                    $"Timed out waiting for fixture path: {path}");
            await Task.Delay(10);
        }
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        string workingDirectory,
        TimeSpan timeout,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        if (environment is not null)
        {
            foreach (var entry in environment)
                startInfo.Environment[entry.Key] = entry.Value;
        }

        return await RunProcessAsync(startInfo, timeout);
    }

    private static async Task<ProcessResult> RunProcessAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout)
    {
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "The child process did not start.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var completionTask = Task.WhenAll(
            process.WaitForExitAsync(),
            stdoutTask,
            stderrTask);
        using var timeoutSource = new CancellationTokenSource();
        var timeoutTask = Task.Delay(
            timeout,
            timeoutSource.Token);
        if (await Task.WhenAny(
                completionTask,
                timeoutTask) != completionTask)
        {
            var cleanupFailures = new List<Exception>();
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync()
                        .WaitAsync(TimeSpan.FromSeconds(5));
                }
                else if (!stdoutTask.IsCompleted ||
                         !stderrTask.IsCompleted)
                {
                    cleanupFailures.Add(
                        new InvalidOperationException(
                            "The child exited, but a descendant retained a redirected output handle."));
                }
            }
            catch (Exception cleanupEx)
                when (cleanupEx is InvalidOperationException or
                      System.ComponentModel.Win32Exception or
                      TimeoutException)
            {
                cleanupFailures.Add(cleanupEx);
            }

            var message =
                $"The child process exceeded the {timeout} timeout.";
            throw cleanupFailures.Count == 0
                ? new TimeoutException(message)
                : new TimeoutException(
                    message,
                    new AggregateException(cleanupFailures));
        }

        await timeoutSource.CancelAsync();
        await completionTask;
        return new ProcessResult(
            process.ExitCode,
            await stdoutTask,
            await stderrTask);
    }

    private static string FindRepositoryRoot(
        [CallerFilePath] string sourceFilePath = "")
    {
        var directory = new DirectoryInfo(
            Path.GetDirectoryName(sourceFilePath)
            ?? AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(
                    Path.Combine(directory.FullName, ".git")) &&
                Directory.Exists(
                    Path.Combine(directory.FullName, "tools", "SyncDiag")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the repository root.");
    }

    private sealed record ProcessResult(
        int ExitCode,
        string Stdout,
        string Stderr);

    private static class NativeMethods
    {
        [DllImport(
            "kernel32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreateHardLinkW(
            string fileName,
            string existingFileName,
            IntPtr securityAttributes);
    }
}
