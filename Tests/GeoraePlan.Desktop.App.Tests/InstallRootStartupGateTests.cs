using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class InstallRootStartupGateTests
{
    [Fact]
    public void MultiRootGate_UsesDeterministicOrderAndReleasesPartialAcquisition()
    {
        var unique = Guid.NewGuid().ToString("N");
        var roots = new[]
        {
            Path.Combine(TestProcessIsolation.AppRoot, unique, "canonical"),
            Path.Combine(TestProcessIsolation.AppRoot, unique, "legacy")
        };
        var rootsByMutexName = roots
            .OrderBy(InstallRootUpdateGate.BuildMutexName, StringComparer.Ordinal)
            .ToArray();
        var expectedMutexNames = rootsByMutexName
            .Select(InstallRootUpdateGate.BuildMutexName)
            .ToArray();

        Assert.Equal(
            expectedMutexNames,
            InstallRootUpdateGate.GetOrderedMutexNames(
                new[] { roots[1], roots[0], roots[1] }));
        Assert.Equal(
            expectedMutexNames.Concat(
                roots
                    .Select(InstallRootUpdateGate.BuildOperationLeaseMutexName)
                    .OrderBy(name => name, StringComparer.Ordinal))
                .Concat(
                    roots
                        .Select(InstallRootUpdateGate.BuildWorkerLeaseMutexName)
                        .OrderBy(name => name, StringComparer.Ordinal)),
            InstallRootUpdateGate.GetOrderedGateMutexNames(
                new[] { roots[1], roots[0], roots[1] }));

        using var laterConflict = new Mutex(
            initiallyOwned: true,
            InstallRootUpdateGate.BuildMutexName(rootsByMutexName[1]),
            out var conflictCreated);
        Assert.True(conflictCreated);

        Assert.False(
            InstallRootUpdateGate.TryAcquireMany(
                new[] { roots[1], roots[0] },
                out var blocked));
        Assert.Null(blocked);

        Assert.True(
            InstallRootUpdateGate.TryAcquire(
                rootsByMutexName[0],
                out var releasedPartialGate));
        releasedPartialGate!.Dispose();

        laterConflict.ReleaseMutex();
        laterConflict.Dispose();
        Assert.True(
            InstallRootUpdateGate.TryAcquireMany(
                new[] { roots[1], roots[0] },
                out var completeGate));

        Assert.False(
            InstallRootUpdateGate.TryAcquire(
                roots[0],
                out var canonicalConflict));
        Assert.Null(canonicalConflict);
        Assert.False(
            InstallRootUpdateGate.TryAcquire(
                roots[1],
                out var legacyConflict));
        Assert.Null(legacyConflict);

        completeGate!.Dispose();

        Assert.True(
            InstallRootUpdateGate.TryAcquireMany(
                roots,
                out var reacquired));
        reacquired!.Dispose();
    }

    [Fact]
    public void MultiRootGate_WorkerLeaseAloneBlocksAndReleasesRootAndOperationPhases()
    {
        var unique = Guid.NewGuid().ToString("N");
        var roots = new[]
        {
            Path.Combine(TestProcessIsolation.AppRoot, unique, "canonical"),
            Path.Combine(TestProcessIsolation.AppRoot, unique, "legacy")
        };
        var heldWorkerLease = new Mutex(
            initiallyOwned: true,
            InstallRootUpdateGate.BuildWorkerLeaseMutexName(roots[1]),
            out var workerLeaseCreated);
        Assert.True(workerLeaseCreated);

        try
        {
            Assert.False(
                InstallRootUpdateGate.TryAcquireMany(
                    roots,
                    out var blocked));
            Assert.Null(blocked);

            Assert.True(
                InstallRootUpdateGate.TryAcquire(
                    roots[0],
                    out var releasedFirstRoot));
            releasedFirstRoot!.Dispose();

            var releasedSecondPrimary = new Mutex(
                initiallyOwned: true,
                InstallRootUpdateGate.BuildMutexName(roots[1]),
                out var secondPrimaryCreated);
            Assert.True(secondPrimaryCreated);

            var releasedSecondOperationLease = new Mutex(
                initiallyOwned: true,
                InstallRootUpdateGate.BuildOperationLeaseMutexName(roots[1]),
                out var secondOperationLeaseCreated);
            Assert.True(secondOperationLeaseCreated);

            releasedSecondOperationLease.ReleaseMutex();
            releasedSecondOperationLease.Dispose();
            releasedSecondPrimary.ReleaseMutex();
            releasedSecondPrimary.Dispose();
        }
        finally
        {
            heldWorkerLease.ReleaseMutex();
            heldWorkerLease.Dispose();
        }

        Assert.True(
            InstallRootUpdateGate.TryAcquireMany(
                roots,
                out var reacquired));
        reacquired!.Dispose();
    }

    [Fact]
    public void MultiRootGate_LeaseAloneBlocksAndReleasesAllPartialPrimaryMutexes()
    {
        var unique = Guid.NewGuid().ToString("N");
        var roots = new[]
        {
            Path.Combine(TestProcessIsolation.AppRoot, unique, "canonical"),
            Path.Combine(TestProcessIsolation.AppRoot, unique, "legacy")
        };
        var heldLease = new Mutex(
            initiallyOwned: true,
            InstallRootUpdateGate.BuildOperationLeaseMutexName(roots[1]),
            out var leaseCreated);
        Assert.True(leaseCreated);

        try
        {
            Assert.False(
                InstallRootUpdateGate.TryAcquireMany(
                    roots,
                    out var blocked));
            Assert.Null(blocked);

            Assert.True(
                InstallRootUpdateGate.TryAcquire(
                    roots[0],
                    out var releasedFirstRoot));
            releasedFirstRoot!.Dispose();

            var releasedSecondPrimary = new Mutex(
                initiallyOwned: true,
                InstallRootUpdateGate.BuildMutexName(roots[1]),
                out var secondPrimaryCreated);
            Assert.True(secondPrimaryCreated);
            releasedSecondPrimary.ReleaseMutex();
            releasedSecondPrimary.Dispose();
        }
        finally
        {
            heldLease.ReleaseMutex();
            heldLease.Dispose();
        }

        Assert.True(
            InstallRootUpdateGate.TryAcquireMany(
                roots,
                out var reacquired));
        reacquired!.Dispose();
    }

    [Fact]
    public void StartupRootResolution_BridgesCanonicalAndLegacyButBoundsCustomRoots()
    {
        var unique = Guid.NewGuid().ToString("N");
        var baseRoot = Path.Combine(TestProcessIsolation.AppRoot, unique);
        var canonicalRoot = Path.Combine(baseRoot, "Program Files", "tradeplan");
        var legacyRoot = Path.Combine(baseRoot, "Local AppData", "Programs", "거래플랜");
        var customRoot = Path.Combine(baseRoot, "portable");
        var expectedBridgeRoots = new[]
        {
            Path.GetFullPath(canonicalRoot),
            Path.GetFullPath(legacyRoot)
        };

        Assert.Equal(
            expectedBridgeRoots,
            거래플랜.Desktop.App.App.GetInstallRecoveryStartupRoots(
                canonicalRoot + Path.DirectorySeparatorChar,
                canonicalRoot,
                legacyRoot));
        Assert.Equal(
            expectedBridgeRoots,
            거래플랜.Desktop.App.App.GetInstallRecoveryStartupRoots(
                legacyRoot,
                canonicalRoot,
                legacyRoot));
        Assert.Equal(
            new[] { Path.GetFullPath(customRoot) },
            거래플랜.Desktop.App.App.GetInstallRecoveryStartupRoots(
                customRoot,
                canonicalRoot,
                legacyRoot));
    }

    [Fact]
    public void StartupRecoveryProbe_ChecksCanonicalFirstAndFailsClosedOnLegacyError()
    {
        var unique = Guid.NewGuid().ToString("N");
        var baseRoot = Path.Combine(TestProcessIsolation.AppRoot, unique);
        var canonicalRoot = Path.Combine(baseRoot, "Program Files", "tradeplan");
        var legacyRoot = Path.Combine(baseRoot, "Local AppData", "Programs", "거래플랜");
        var startupRoots =
            거래플랜.Desktop.App.App.GetInstallRecoveryStartupRoots(
                legacyRoot,
                canonicalRoot,
                legacyRoot);
        var visited = new List<string>();

        var blockMessage =
            거래플랜.Desktop.App.App.GetInstallRecoveryStartupBlockMessage(
                startupRoots,
                root =>
                {
                    visited.Add(root);
                    return string.Equals(
                        root,
                        canonicalRoot,
                        StringComparison.OrdinalIgnoreCase)
                        ? new InstallRecoveryStateProbeResult(
                            InstallRecoveryStateStatus.Absent,
                            Path.Combine(root, "state"))
                        : new InstallRecoveryStateProbeResult(
                            InstallRecoveryStateStatus.AccessError,
                            Path.Combine(root, "state"),
                            new UnauthorizedAccessException("deterministic denial"));
                });

        Assert.NotNull(blockMessage);
        Assert.Equal(startupRoots, visited);

        visited.Clear();
        var canonicalPendingMessage =
            거래플랜.Desktop.App.App.GetInstallRecoveryStartupBlockMessage(
                startupRoots,
                root =>
                {
                    visited.Add(root);
                    return new InstallRecoveryStateProbeResult(
                        InstallRecoveryStateStatus.Present,
                        Path.Combine(root, "state"));
                });

        Assert.NotNull(canonicalPendingMessage);
        Assert.Equal(new[] { startupRoots[0] }, visited);
    }

    [Fact]
    public void LegacyRollbackProbe_DetectsPendingAndFailsClosedOnAmbiguousOrInvalidState()
    {
        var sandboxRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            "legacy-rollback-probe",
            Guid.NewGuid().ToString("N"));
        var artifactRoot = Path.Combine(sandboxRoot, "artifact");
        var physicalInstallRoot = Path.Combine(sandboxRoot, "physical-install");
        var legacyInstallRoot = Path.Combine(sandboxRoot, "legacy-install");

        try
        {
            Directory.CreateDirectory(artifactRoot);
            var candidateStatePaths =
                LegacyInstallRollbackStateProbe.GetCandidateStatePathsCore(
                    artifactRoot,
                    physicalInstallRoot,
                    legacyInstallRoot);
            Assert.Equal(2, candidateStatePaths.Length);

            Directory.CreateDirectory(candidateStatePaths[0]);
            var physicalPending =
                LegacyInstallRollbackStateProbe.ProbeCore(
                    artifactRoot,
                    physicalInstallRoot,
                    legacyInstallRoot);
            Assert.Equal(
                InstallRecoveryStateStatus.Present,
                physicalPending.Status);
            Assert.Equal(candidateStatePaths[0], physicalPending.StatePath);

            Directory.CreateDirectory(candidateStatePaths[1]);
            var ambiguous =
                LegacyInstallRollbackStateProbe.ProbeCore(
                    artifactRoot,
                    physicalInstallRoot,
                    legacyInstallRoot);
            Assert.Equal(
                InstallRecoveryStateStatus.AccessError,
                ambiguous.Status);

            Directory.Delete(candidateStatePaths[0], recursive: true);
            Directory.Delete(candidateStatePaths[1], recursive: true);
            File.WriteAllText(candidateStatePaths[0], "not-a-directory");

            var invalidType =
                LegacyInstallRollbackStateProbe.ProbeCore(
                    artifactRoot,
                    physicalInstallRoot,
                    legacyInstallRoot);
            Assert.Equal(
                InstallRecoveryStateStatus.AccessError,
                invalidType.Status);
        }
        finally
        {
            if (Directory.Exists(sandboxRoot))
                Directory.Delete(sandboxRoot, recursive: true);
        }
    }

    [Fact]
    public void StartupRecoveryProbe_ChecksGeneratedAndLegacyStatesForEveryManagedRoot()
    {
        var unique = Guid.NewGuid().ToString("N");
        var baseRoot = Path.Combine(TestProcessIsolation.AppRoot, unique);
        var canonicalRoot = Path.Combine(
            baseRoot,
            "Program Files",
            "tradeplan");
        var legacyRoot = Path.Combine(
            baseRoot,
            "Local AppData",
            "Programs",
            "거래플랜");
        var artifactRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            unique,
            "GeoraePlan");
        var fallbackArtifactRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            unique,
            "fallback",
            "GeoraePlan");
        var artifactRoots = new[]
        {
            artifactRoot,
            fallbackArtifactRoot
        };
        var startupRoots =
            거래플랜.Desktop.App.App.GetInstallRecoveryStartupRoots(
                canonicalRoot,
                canonicalRoot,
                legacyRoot);
        var visited = new List<string>();

        var blockMessage =
            거래플랜.Desktop.App.App.GetInstallRecoveryStartupBlockMessage(
                startupRoots,
                artifactRoots,
                root =>
                {
                    visited.Add($"generated:{root}");
                    return new InstallRecoveryStateProbeResult(
                        InstallRecoveryStateStatus.Absent,
                        Path.Combine(root, "generated-state"));
                },
                (candidateArtifactRoot, root) =>
                {
                    visited.Add(
                        $"legacy:{candidateArtifactRoot}:{root}");
                    return new InstallRecoveryStateProbeResult(
                        string.Equals(
                            root,
                            legacyRoot,
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(
                            candidateArtifactRoot,
                            fallbackArtifactRoot,
                            StringComparison.OrdinalIgnoreCase)
                            ? InstallRecoveryStateStatus.Present
                            : InstallRecoveryStateStatus.Absent,
                        Path.Combine(root, "legacy-state"));
                });

        Assert.NotNull(blockMessage);
        Assert.Equal(
            new[]
            {
                $"generated:{startupRoots[0]}",
                $"legacy:{artifactRoot}:{startupRoots[0]}",
                $"legacy:{fallbackArtifactRoot}:{startupRoots[0]}",
                $"generated:{startupRoots[1]}",
                $"legacy:{artifactRoot}:{startupRoots[1]}",
                $"legacy:{fallbackArtifactRoot}:{startupRoots[1]}"
            },
            visited);

        var customRoot = Path.Combine(baseRoot, "portable");
        var customRoots =
            거래플랜.Desktop.App.App.GetInstallRecoveryStartupRoots(
                customRoot,
                canonicalRoot,
                legacyRoot);
        var customProbeCount = 0;
        var customBlockMessage =
            거래플랜.Desktop.App.App.GetInstallRecoveryStartupBlockMessage(
                customRoots,
                artifactRoots,
                root =>
                {
                    customProbeCount++;
                    return new InstallRecoveryStateProbeResult(
                        InstallRecoveryStateStatus.Absent,
                        root);
                },
                (_, root) =>
                {
                    customProbeCount++;
                    return new InstallRecoveryStateProbeResult(
                        InstallRecoveryStateStatus.Absent,
                        root);
                });

        Assert.Null(customBlockMessage);
        Assert.Equal(3, customProbeCount);
        Assert.Single(customRoots);
    }

    [Fact]
    public void LegacyRollbackProbe_FailsClosedWhenEnumerationIsDeniedOrStateIsReparsePoint()
    {
        var sandboxRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            "legacy-rollback-probe-contract",
            Guid.NewGuid().ToString("N"));
        var artifactRoot = Path.Combine(sandboxRoot, "artifact");
        var installRoot = Path.Combine(sandboxRoot, "install");

        try
        {
            Directory.CreateDirectory(artifactRoot);
            var statePath =
                LegacyInstallRollbackStateProbe
                    .GetCandidateStatePathsCore(
                        artifactRoot,
                        installRoot,
                        installRoot)
                    .Single();
            var stateParent = Path.GetDirectoryName(statePath)!;

            var denied =
                LegacyInstallRollbackStateProbe.ProbeCore(
                    artifactRoot,
                    installRoot,
                    installRoot,
                    (_, _) => throw new UnauthorizedAccessException(
                        "deterministic list denial"),
                    path =>
                        string.Equals(
                            path,
                            stateParent,
                            StringComparison.OrdinalIgnoreCase)
                            ? FileAttributes.Directory
                            : throw new FileNotFoundException());
            Assert.Equal(
                InstallRecoveryStateStatus.AccessError,
                denied.Status);

            var reparse =
                LegacyInstallRollbackStateProbe.ProbeCore(
                    artifactRoot,
                    installRoot,
                    installRoot,
                    (_, _) => [statePath],
                    path =>
                        string.Equals(
                            path,
                            stateParent,
                            StringComparison.OrdinalIgnoreCase)
                            ? FileAttributes.Directory
                            : FileAttributes.Directory |
                              FileAttributes.ReparsePoint);
            Assert.Equal(
                InstallRecoveryStateStatus.AccessError,
                reparse.Status);
        }
        finally
        {
            if (Directory.Exists(sandboxRoot))
                Directory.Delete(sandboxRoot, recursive: true);
        }
    }

    [Fact]
    public void LegacyRollbackArtifactResolver_IsReadOnlyAndDeduplicatesFallbacks()
    {
        var sandboxRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            "legacy-artifact-roots",
            Guid.NewGuid().ToString("N"));
        var firstTempRoot = Path.Combine(sandboxRoot, "first");
        var secondTempRoot = Path.Combine(sandboxRoot, "second");

        var artifactRoots =
            LegacyInstallRollbackStateProbe.GetArtifactRootsCore(
                [
                    firstTempRoot,
                    firstTempRoot + Path.DirectorySeparatorChar,
                    secondTempRoot
                ]);

        Assert.Equal(
            new[]
            {
                Path.Combine(firstTempRoot, "GeoraePlan"),
                Path.Combine(secondTempRoot, "GeoraePlan")
            },
            artifactRoots);
        Assert.False(Directory.Exists(sandboxRoot));
        Assert.False(Directory.Exists(firstTempRoot));
        Assert.False(Directory.Exists(secondTempRoot));
    }

    [Fact]
    public void LegacyRollbackArtifactResolver_OmitsUnavailableDefaultDDrive()
    {
        var sandboxRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            "legacy-artifact-no-d",
            Guid.NewGuid().ToString("N"));
        var systemTempRoot = Path.Combine(sandboxRoot, "system-temp");
        var configuredAppRoot = Path.Combine(sandboxRoot, "app-root");
        var localAppDataRoot = Path.Combine(sandboxRoot, "local-app-data");

        var artifactRoots =
            LegacyInstallRollbackStateProbe.GetDefaultArtifactRootsCore(
                configuredTempRoot: null,
                includeDefaultDDriveRoot: false,
                systemTempRoot,
                configuredAppRoot,
                localAppDataRoot);

        Assert.Equal(
            new[]
            {
                Path.Combine(systemTempRoot, "GeoraePlan"),
                Path.Combine(configuredAppRoot, "temp", "GeoraePlan"),
                Path.Combine(
                    localAppDataRoot,
                    "Temp",
                    "GeoraePlan"),
                Path.Combine(
                    localAppDataRoot,
                    "거래플랜",
                    "temp",
                    "GeoraePlan")
            },
            artifactRoots);
        Assert.DoesNotContain(
            artifactRoots,
            path => string.Equals(
                path,
                Path.Combine(
                    "D:\\",
                    "거래플랜",
                    "temp",
                    "GeoraePlan"),
                StringComparison.OrdinalIgnoreCase));
        Assert.False(Directory.Exists(sandboxRoot));
    }

    [Fact]
    public void LegacyRollbackRecoveryTargets_SkipProtectedRootButCoverManagedLegacyRoot()
    {
        var canonicalRoot =
            거래플랜.Updater.Program.GetCanonicalInstallRoot();
        var legacyRoot =
            거래플랜.Updater.Program.GetLegacyInstallRoot();
        var options = new 거래플랜.Updater.UpdateArguments
        {
            InstallRoot = canonicalRoot,
            LegacyInstallRoot = canonicalRoot
        };

        var targets =
            거래플랜.Updater.Program
                .GetLegacyRollbackRecoveryTargets(options);

        Assert.False(
            거래플랜.Desktop.App.App
                .CanRecoverLegacyInstallRollbackState(canonicalRoot));
        Assert.True(
            거래플랜.Desktop.App.App
                .CanRecoverLegacyInstallRollbackState(legacyRoot));
        Assert.DoesNotContain(
            targets,
            target => string.Equals(
                target.InstallRoot,
                canonicalRoot,
                StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            targets,
            target => string.Equals(
                target.InstallRoot,
                legacyRoot,
                StringComparison.OrdinalIgnoreCase));
        Assert.All(
            targets,
            target => Assert.False(
                거래플랜.Updater.Program.RequiresElevation(
                    target.InstallRoot)));
    }
}
