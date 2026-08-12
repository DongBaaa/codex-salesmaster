using System.Diagnostics;
using 거래플랜.Updater;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class InstallRollbackSupervisorDurabilityTests
{
    [Fact]
    public async Task FirstJournalTempCrash_RecoversEmptyStateButRejectsArbitraryResidue()
    {
        var testRoot = CreateTestRoot("first-journal-temp-crash");
        var artifactRoot = Path.Combine(testRoot, "artifacts");
        var installRoot = Path.Combine(testRoot, "install");
        string? orphanedTemporaryPath = null;

        try
        {
            InstallRollbackSupervisor
                    .JournalAfterFlushCrashFactoryForTests =
                temporaryPath =>
                {
                    orphanedTemporaryPath = temporaryPath;
                    return new IOException(
                        "Injected crash after first journal temp flush.");
                };

            await Assert.ThrowsAsync<IOException>(
                () => InstallRollbackSupervisor.PrepareAsync(
                    artifactRoot,
                    installRoot));

            var stateRoot =
                InstallRollbackSupervisor.GetStateRoot(
                    artifactRoot,
                    installRoot);
            Assert.True(Directory.Exists(stateRoot));
            Assert.Empty(
                Directory.EnumerateFileSystemEntries(stateRoot));
            Assert.NotNull(orphanedTemporaryPath);
            Assert.True(File.Exists(orphanedTemporaryPath));
            Assert.Equal(
                Path.GetDirectoryName(stateRoot),
                Path.GetDirectoryName(orphanedTemporaryPath));

            InstallRollbackSupervisor
                    .JournalAfterFlushCrashFactoryForTests =
                null;
            await InstallRollbackSupervisor
                .RecoverPendingUntilResolvedAsync(
                    artifactRoot,
                    installRoot,
                    retryDelay: TimeSpan.FromMilliseconds(10));

            Assert.False(Directory.Exists(stateRoot));
            Assert.False(File.Exists(orphanedTemporaryPath));

            Directory.CreateDirectory(stateRoot);
            var arbitraryResiduePath = Path.Combine(
                stateRoot,
                "arbitrary-residue.bin");
            File.WriteAllBytes(
                arbitraryResiduePath,
                [1, 6, 1, 8]);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => InstallRollbackSupervisor
                    .RecoverPendingOnceAsync(
                        artifactRoot,
                        installRoot,
                        log: null));
            Assert.True(File.Exists(arbitraryResiduePath));
            Assert.True(Directory.Exists(stateRoot));
        }
        finally
        {
            InstallRollbackSupervisor
                    .JournalAfterFlushCrashFactoryForTests =
                null;
            InstallRollbackSupervisor
                    .CompletedStateCleanupFailureFactoryForTests =
                null;
            CleanupTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task CommitCleanupFailure_PersistsCleanupPendingAndStartupRecoveryRetainsGate()
    {
        var testRoot = CreateTestRoot("commit-cleanup-pending");
        var artifactRoot = Path.Combine(testRoot, "artifacts");
        var installRoot = Path.Combine(testRoot, "install");
        Task? recoveryTask = null;
        var allowCleanup = 0;

        try
        {
            var session = await InstallRollbackSupervisor.PrepareAsync(
                artifactRoot,
                installRoot);
            InstallRollbackSupervisor.MarkInstallerStarting(session);
            Directory.CreateDirectory(installRoot);
            File.WriteAllText(
                Path.Combine(installRoot, "installed.txt"),
                "new install");

            InstallRollbackSupervisor.CompletedStateCleanupFailureFactoryForTests =
                _ => new IOException("Injected commit cleanup failure.");

            Assert.Throws<IOException>(() =>
                InstallRollbackSupervisor.Commit(session));
            Assert.Equal(
                InstallRollbackPhase.CleanupPending,
                session.Journal.Phase);
            Assert.True(File.Exists(session.JournalPath));
            Assert.True(Directory.Exists(session.Journal.StateRoot));
            Assert.True(Directory.Exists(installRoot));

            var cleanupAttempts = 0;
            InstallRollbackSupervisor.CompletedStateCleanupFailureFactoryForTests =
                _ =>
                {
                    Interlocked.Increment(ref cleanupAttempts);
                    return Volatile.Read(ref allowCleanup) == 0
                        ? new IOException("Injected startup cleanup retry.")
                        : null;
                };

            recoveryTask =
                InstallRollbackSupervisor.RecoverPendingUntilResolvedAsync(
                    artifactRoot,
                    installRoot,
                    retryDelay: TimeSpan.FromMilliseconds(10));
            await WaitForConditionAsync(
                () => Volatile.Read(ref cleanupAttempts) >= 2,
                TimeSpan.FromSeconds(5));

            Assert.False(recoveryTask.IsCompleted);
            Assert.True(File.Exists(session.JournalPath));
            Assert.True(Directory.Exists(session.Journal.StateRoot));

            Volatile.Write(ref allowCleanup, 1);
            await recoveryTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(Directory.Exists(session.Journal.StateRoot));
            Assert.True(Directory.Exists(installRoot));
        }
        finally
        {
            Volatile.Write(ref allowCleanup, 1);
            if (recoveryTask is not null)
            {
                try
                {
                    await recoveryTask.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch
                {
                    // The assertion failure remains the primary test result.
                }
            }

            InstallRollbackSupervisor.CompletedStateCleanupFailureFactoryForTests =
                null;
            CleanupTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task RestoreCleanupFailure_DoesNotReturnBeforeCleanupCompletes()
    {
        var testRoot = CreateTestRoot("restore-cleanup-pending");
        var artifactRoot = Path.Combine(testRoot, "artifacts");
        var installRoot = Path.Combine(testRoot, "install");
        Task? recoveryTask = null;
        var allowCleanup = 0;

        try
        {
            var session = await InstallRollbackSupervisor.PrepareAsync(
                artifactRoot,
                installRoot);
            InstallRollbackSupervisor.MarkInstallerStarting(session);
            Directory.CreateDirectory(installRoot);
            File.WriteAllText(
                Path.Combine(installRoot, "incomplete.txt"),
                "incomplete install");

            var cleanupAttempts = 0;
            InstallRollbackSupervisor.CompletedStateCleanupFailureFactoryForTests =
                _ =>
                {
                    Interlocked.Increment(ref cleanupAttempts);
                    return Volatile.Read(ref allowCleanup) == 0
                        ? new IOException("Injected restore cleanup retry.")
                        : null;
                };

            recoveryTask = InstallRollbackSupervisor.RecoverUntilVerifiedAsync(
                session,
                retryDelay: TimeSpan.FromMilliseconds(10));
            await WaitForConditionAsync(
                () => Volatile.Read(ref cleanupAttempts) >= 2,
                TimeSpan.FromSeconds(5));

            Assert.False(recoveryTask.IsCompleted);
            Assert.Equal(
                InstallRollbackPhase.CleanupPending,
                session.Journal.Phase);
            Assert.True(File.Exists(session.JournalPath));
            Assert.False(Directory.Exists(installRoot));

            Volatile.Write(ref allowCleanup, 1);
            await recoveryTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(Directory.Exists(session.Journal.StateRoot));
        }
        finally
        {
            Volatile.Write(ref allowCleanup, 1);
            if (recoveryTask is not null)
            {
                try
                {
                    await recoveryTask.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch
                {
                    // The assertion failure remains the primary test result.
                }
            }

            InstallRollbackSupervisor.CompletedStateCleanupFailureFactoryForTests =
                null;
            CleanupTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task Restore_ReinstatesExactFileAndDirectoryCreationTimes()
    {
        var testRoot = CreateTestRoot("creation-time-restore");
        var artifactRoot = Path.Combine(testRoot, "artifacts");
        var installRoot = Path.Combine(testRoot, "install");
        var childDirectory = Path.Combine(installRoot, "data");
        var filePath = Path.Combine(childDirectory, "original.txt");

        try
        {
            Directory.CreateDirectory(childDirectory);
            File.WriteAllText(filePath, "original payload");

            Directory.SetCreationTimeUtc(
                installRoot,
                new DateTime(2022, 1, 2, 3, 4, 6, DateTimeKind.Utc));
            Directory.SetCreationTimeUtc(
                childDirectory,
                new DateTime(2022, 2, 3, 4, 5, 8, DateTimeKind.Utc));
            File.SetCreationTimeUtc(
                filePath,
                new DateTime(2022, 3, 4, 5, 6, 10, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(
                filePath,
                new DateTime(2023, 3, 4, 5, 6, 12, DateTimeKind.Utc));
            Directory.SetLastWriteTimeUtc(
                childDirectory,
                new DateTime(2023, 2, 3, 4, 5, 14, DateTimeKind.Utc));
            Directory.SetLastWriteTimeUtc(
                installRoot,
                new DateTime(2023, 1, 2, 3, 4, 16, DateTimeKind.Utc));

            var expectedRootCreationUtc =
                Directory.GetCreationTimeUtc(installRoot);
            var expectedDirectoryCreationUtc =
                Directory.GetCreationTimeUtc(childDirectory);
            var expectedFileCreationUtc =
                File.GetCreationTimeUtc(filePath);

            var session = await InstallRollbackSupervisor.PrepareAsync(
                artifactRoot,
                installRoot);
            Assert.Equal(
                expectedRootCreationUtc.Ticks,
                session.Journal.RootCreationTimeUtcTicks);
            Assert.Equal(
                expectedDirectoryCreationUtc.Ticks,
                Assert.Single(session.Journal.Directories)
                    .CreationTimeUtcTicks);
            Assert.Equal(
                expectedFileCreationUtc.Ticks,
                Assert.Single(session.Journal.Files)
                    .CreationTimeUtcTicks);

            InstallRollbackSupervisor.MarkInstallerStarting(session);
            File.WriteAllText(filePath, "mutated payload");
            Directory.SetCreationTimeUtc(
                installRoot,
                new DateTime(2025, 1, 2, 3, 4, 6, DateTimeKind.Utc));
            Directory.SetCreationTimeUtc(
                childDirectory,
                new DateTime(2025, 2, 3, 4, 5, 8, DateTimeKind.Utc));
            File.SetCreationTimeUtc(
                filePath,
                new DateTime(2025, 3, 4, 5, 6, 10, DateTimeKind.Utc));

            await InstallRollbackSupervisor.RecoverUntilVerifiedAsync(
                session,
                retryDelay: TimeSpan.FromMilliseconds(10));

            Assert.Equal("original payload", File.ReadAllText(filePath));
            Assert.Equal(
                expectedRootCreationUtc,
                Directory.GetCreationTimeUtc(installRoot));
            Assert.Equal(
                expectedDirectoryCreationUtc,
                Directory.GetCreationTimeUtc(childDirectory));
            Assert.Equal(
                expectedFileCreationUtc,
                File.GetCreationTimeUtc(filePath));
            Assert.False(Directory.Exists(session.Journal.StateRoot));
        }
        finally
        {
            InstallRollbackSupervisor.CompletedStateCleanupFailureFactoryForTests =
                null;
            CleanupTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task Restore_ReinstatesReadOnlyFileWithExactMetadata()
    {
        var testRoot = CreateTestRoot("readonly-file-restore");
        var artifactRoot = Path.Combine(testRoot, "artifacts");
        var installRoot = Path.Combine(testRoot, "install");
        var filePath = Path.Combine(installRoot, "readonly.txt");

        try
        {
            Directory.CreateDirectory(installRoot);
            File.WriteAllText(filePath, "original readonly payload");
            File.SetCreationTimeUtc(
                filePath,
                new DateTime(2022, 4, 5, 6, 7, 8, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(
                filePath,
                new DateTime(2023, 5, 6, 7, 8, 10, DateTimeKind.Utc));
            File.SetAttributes(
                filePath,
                File.GetAttributes(filePath) |
                FileAttributes.ReadOnly);

            var expectedAttributes = File.GetAttributes(filePath);
            var expectedCreationUtc = File.GetCreationTimeUtc(filePath);
            var expectedLastWriteUtc = File.GetLastWriteTimeUtc(filePath);

            var session = await InstallRollbackSupervisor.PrepareAsync(
                artifactRoot,
                installRoot);
            InstallRollbackSupervisor.MarkInstallerStarting(session);

            File.SetAttributes(
                filePath,
                expectedAttributes & ~FileAttributes.ReadOnly);
            File.WriteAllText(filePath, "mutated payload");

            await InstallRollbackSupervisor.RecoverUntilVerifiedAsync(
                session,
                retryDelay: TimeSpan.FromMilliseconds(10));

            Assert.Equal(
                "original readonly payload",
                File.ReadAllText(filePath));
            Assert.Equal(expectedAttributes, File.GetAttributes(filePath));
            Assert.Equal(expectedCreationUtc, File.GetCreationTimeUtc(filePath));
            Assert.Equal(expectedLastWriteUtc, File.GetLastWriteTimeUtc(filePath));
            Assert.False(Directory.Exists(session.Journal.StateRoot));
        }
        finally
        {
            InstallRollbackSupervisor
                    .CompletedStateCleanupFailureFactoryForTests =
                null;
            CleanupTestRoot(testRoot);
        }
    }

    private static async Task WaitForConditionAsync(
        Func<bool> predicate,
        TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (stopwatch.Elapsed >= timeout)
                throw new TimeoutException("The expected retry condition was not observed.");

            await Task.Delay(10);
        }
    }

    private static string CreateTestRoot(string scenario)
    {
        var root = Path.Combine(
            FindRepositoryRoot(),
            "temp",
            "updater-rollback-supervisor-tests",
            $"{scenario}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void CleanupTestRoot(string testRoot)
    {
        if (!Directory.Exists(testRoot))
            return;

        foreach (var path in Directory.EnumerateFileSystemEntries(
                     testRoot,
                     "*",
                     SearchOption.AllDirectories))
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        Directory.Delete(testRoot, recursive: true);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "Updater")) &&
                Directory.Exists(Path.Combine(current.FullName, "Tests")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "거래플랜 저장소 루트를 찾지 못했습니다.");
    }
}
