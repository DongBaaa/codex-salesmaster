using System.Security;
using 거래플랜.Updater;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class InstallRollbackSupervisorPresenceProbeTests
{
    [Theory]
    [InlineData("unauthorized")]
    [InlineData("io")]
    [InlineData("security")]
    public async Task Recovery_StatePresenceAccessError_IsNotTreatedAsAbsent(
        string errorKind)
    {
        var testRoot = CreateTestRoot("state-access-error");
        var artifactRoot = Path.Combine(testRoot, "artifacts");
        var installRoot = Path.Combine(testRoot, "install");
        var stateRoot = InstallRollbackSupervisor.GetStateRoot(
            artifactRoot,
            installRoot);

        try
        {
            Directory.CreateDirectory(stateRoot);
            InstallRollbackSupervisor.PresenceProbeForTests =
                path => PathsEqual(path, stateRoot)
                    ? throw CreateAccessException(errorKind)
                    : File.GetAttributes(path);

            var exception = await Assert.ThrowsAnyAsync<Exception>(
                () => InstallRollbackSupervisor.RecoverPendingOnceAsync(
                    artifactRoot,
                    installRoot,
                    log: null));

            Assert.IsType(
                CreateAccessException(errorKind).GetType(),
                exception);
            InstallRollbackSupervisor.PresenceProbeForTests = null;
            Assert.True(Directory.Exists(stateRoot));
        }
        finally
        {
            InstallRollbackSupervisor.PresenceProbeForTests = null;
            CleanupTestRoot(testRoot);
        }
    }

    [Theory]
    [InlineData("file")]
    [InlineData("directory")]
    public async Task Recovery_OnlyMissingExceptions_AreTreatedAsAbsent(
        string missingKind)
    {
        var testRoot = CreateTestRoot("missing-state");
        var artifactRoot = Path.Combine(testRoot, "artifacts");
        var installRoot = Path.Combine(testRoot, "install");
        var stateRoot = InstallRollbackSupervisor.GetStateRoot(
            artifactRoot,
            installRoot);
        var probeCount = 0;

        try
        {
            InstallRollbackSupervisor.PresenceProbeForTests =
                path =>
                {
                    if (!PathsEqual(path, stateRoot))
                        return File.GetAttributes(path);

                    Interlocked.Increment(ref probeCount);
                    throw missingKind == "file"
                        ? new FileNotFoundException("Injected missing file.")
                        : new DirectoryNotFoundException(
                            "Injected missing directory.");
                };

            await InstallRollbackSupervisor.RecoverPendingOnceAsync(
                artifactRoot,
                installRoot,
                log: null);

            Assert.Equal(1, Volatile.Read(ref probeCount));
        }
        finally
        {
            InstallRollbackSupervisor.PresenceProbeForTests = null;
            CleanupTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task Commit_StatePresenceIoError_PreventsCleanupSuccess()
    {
        var testRoot = CreateTestRoot("commit-cleanup-access-error");
        var artifactRoot = Path.Combine(testRoot, "artifacts");
        var installRoot = Path.Combine(testRoot, "install");

        try
        {
            var session = await InstallRollbackSupervisor.PrepareAsync(
                artifactRoot,
                installRoot);
            InstallRollbackSupervisor.MarkInstallerStarting(session);
            InstallRollbackSupervisor.PresenceProbeForTests =
                path => PathsEqual(path, session.Journal.StateRoot)
                    ? throw new IOException(
                        "Injected cleanup presence failure.")
                    : File.GetAttributes(path);

            Assert.Throws<IOException>(
                () => InstallRollbackSupervisor.Commit(session));

            Assert.Equal(
                InstallRollbackPhase.CleanupPending,
                session.Journal.Phase);
            InstallRollbackSupervisor.PresenceProbeForTests = null;
            Assert.True(File.Exists(session.JournalPath));
            Assert.True(Directory.Exists(session.Journal.StateRoot));
        }
        finally
        {
            InstallRollbackSupervisor.PresenceProbeForTests = null;
            CleanupTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task Prepare_InstallPresenceAccessError_DoesNotRecordNewInstall()
    {
        var testRoot = CreateTestRoot("prepare-install-access-error");
        var artifactRoot = Path.Combine(testRoot, "artifacts");
        var installRoot = Path.Combine(testRoot, "install");

        try
        {
            InstallRollbackSupervisor.PresenceProbeForTests =
                path => PathsEqual(path, installRoot)
                    ? throw new UnauthorizedAccessException(
                        "Injected install-root access denial.")
                    : File.GetAttributes(path);

            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => InstallRollbackSupervisor.PrepareAsync(
                    artifactRoot,
                    installRoot));

            InstallRollbackSupervisor.PresenceProbeForTests = null;
            var stateRoot = InstallRollbackSupervisor.GetStateRoot(
                artifactRoot,
                installRoot);
            Assert.True(Directory.Exists(stateRoot));
            Assert.False(
                File.Exists(Path.Combine(stateRoot, "journal.json")));
        }
        finally
        {
            InstallRollbackSupervisor.PresenceProbeForTests = null;
            CleanupTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task Recovery_JournalPresenceSecurityError_PreservesState()
    {
        var testRoot = CreateTestRoot("journal-access-error");
        var artifactRoot = Path.Combine(testRoot, "artifacts");
        var installRoot = Path.Combine(testRoot, "install");
        var stateRoot = InstallRollbackSupervisor.GetStateRoot(
            artifactRoot,
            installRoot);
        var journalPath = Path.Combine(stateRoot, "journal.json");

        try
        {
            Directory.CreateDirectory(stateRoot);
            InstallRollbackSupervisor.PresenceProbeForTests =
                path => PathsEqual(path, journalPath)
                    ? throw new SecurityException(
                        "Injected journal security failure.")
                    : File.GetAttributes(path);

            await Assert.ThrowsAsync<SecurityException>(
                () => InstallRollbackSupervisor.RecoverPendingOnceAsync(
                    artifactRoot,
                    installRoot,
                    log: null));

            InstallRollbackSupervisor.PresenceProbeForTests = null;
            Assert.True(Directory.Exists(stateRoot));
        }
        finally
        {
            InstallRollbackSupervisor.PresenceProbeForTests = null;
            CleanupTestRoot(testRoot);
        }
    }

    [Fact]
    public void VerifyManifest_SnapshotPresenceIoError_Propagates()
    {
        var testRoot = CreateTestRoot("snapshot-access-error");
        var snapshotRoot = Path.Combine(testRoot, "snapshot");

        try
        {
            Directory.CreateDirectory(snapshotRoot);
            InstallRollbackSupervisor.PresenceProbeForTests =
                path => PathsEqual(path, snapshotRoot)
                    ? throw new IOException(
                        "Injected snapshot presence failure.")
                    : File.GetAttributes(path);

            Assert.Throws<IOException>(
                () => InstallRollbackSupervisor.VerifyManifest(
                    snapshotRoot,
                    new InstallRollbackJournal()));
        }
        finally
        {
            InstallRollbackSupervisor.PresenceProbeForTests = null;
            CleanupTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task NewInstall_DeletionVerificationIoError_PreventsRecoverySuccess()
    {
        var testRoot = CreateTestRoot("new-install-delete-verification");
        var artifactRoot = Path.Combine(testRoot, "artifacts");
        var installRoot = Path.Combine(testRoot, "install");

        try
        {
            var session = await InstallRollbackSupervisor.PrepareAsync(
                artifactRoot,
                installRoot);
            Assert.False(session.Journal.HadExistingInstall);
            InstallRollbackSupervisor.MarkInstallerStarting(session);
            Directory.CreateDirectory(installRoot);
            File.WriteAllText(
                Path.Combine(installRoot, "incomplete.txt"),
                "incomplete");

            InstallRollbackSupervisor.PresenceProbeForTests =
                path =>
                {
                    if (PathsEqual(path, installRoot) &&
                        !Directory.Exists(installRoot))
                    {
                        throw new IOException(
                            "Injected final absence verification failure.");
                    }

                    return File.GetAttributes(path);
                };

            await Assert.ThrowsAsync<IOException>(
                () => InstallRollbackSupervisor.RecoverPendingOnceAsync(
                    artifactRoot,
                    installRoot,
                    log: null));

            InstallRollbackSupervisor.PresenceProbeForTests = null;
            Assert.False(Directory.Exists(installRoot));
            Assert.True(Directory.Exists(session.Journal.StateRoot));
            Assert.True(File.Exists(session.JournalPath));

            await InstallRollbackSupervisor.RecoverPendingOnceAsync(
                artifactRoot,
                installRoot,
                log: null);
            Assert.False(Directory.Exists(session.Journal.StateRoot));
        }
        finally
        {
            InstallRollbackSupervisor.PresenceProbeForTests = null;
            CleanupTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task RecoveryCandidates_RecoversPendingStateFromFallbackArtifactRoot()
    {
        var testRoot = CreateTestRoot("fallback-artifact-recovery");
        var primaryArtifactRoot = Path.Combine(
            testRoot,
            "primary-artifacts");
        var fallbackArtifactRoot = Path.Combine(
            testRoot,
            "fallback-artifacts");
        var installRoot = Path.Combine(testRoot, "install");
        var originalPath = Path.Combine(installRoot, "original.txt");
        var incompletePath = Path.Combine(installRoot, "incomplete.txt");

        try
        {
            Directory.CreateDirectory(installRoot);
            File.WriteAllText(originalPath, "original");
            var session = await InstallRollbackSupervisor.PrepareAsync(
                fallbackArtifactRoot,
                installRoot);
            InstallRollbackSupervisor.MarkInstallerStarting(session);
            File.WriteAllText(originalPath, "mutated");
            File.WriteAllText(incompletePath, "incomplete");

            await InstallRollbackSupervisor
                .RecoverPendingCandidatesOnceAsync(
                    [primaryArtifactRoot, fallbackArtifactRoot],
                    installRoot,
                    log: null);

            Assert.Equal("original", File.ReadAllText(originalPath));
            Assert.False(File.Exists(incompletePath));
            Assert.False(Directory.Exists(session.Journal.StateRoot));
        }
        finally
        {
            CleanupTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task RecoveryCandidates_MultiplePendingRootsFailClosedWithoutMutation()
    {
        var testRoot = CreateTestRoot("ambiguous-artifact-recovery");
        var firstArtifactRoot = Path.Combine(
            testRoot,
            "first-artifacts");
        var secondArtifactRoot = Path.Combine(
            testRoot,
            "second-artifacts");
        var installRoot = Path.Combine(testRoot, "install");
        var firstStateRoot = InstallRollbackSupervisor.GetStateRoot(
            firstArtifactRoot,
            installRoot);
        var secondStateRoot = InstallRollbackSupervisor.GetStateRoot(
            secondArtifactRoot,
            installRoot);

        try
        {
            Directory.CreateDirectory(firstStateRoot);
            Directory.CreateDirectory(secondStateRoot);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => InstallRollbackSupervisor
                    .RecoverPendingCandidatesOnceAsync(
                        [firstArtifactRoot, secondArtifactRoot],
                        installRoot,
                        log: null));

            Assert.True(Directory.Exists(firstStateRoot));
            Assert.True(Directory.Exists(secondStateRoot));
            Assert.False(Directory.Exists(installRoot));
        }
        finally
        {
            CleanupTestRoot(testRoot);
        }
    }

    private static Exception CreateAccessException(string errorKind)
        => errorKind switch
        {
            "unauthorized" => new UnauthorizedAccessException(
                "Injected access denial."),
            "io" => new IOException("Injected presence I/O failure."),
            "security" => new SecurityException(
                "Injected security failure."),
            _ => throw new ArgumentOutOfRangeException(nameof(errorKind))
        };

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);

    private static string CreateTestRoot(string scenario)
    {
        var root = Path.Combine(
            FindRepositoryRoot(),
            "temp",
            "updater-presence-probe-tests",
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
