using System.Runtime.CompilerServices;
using DesktopInstallRootUpdateGate =
    \uac70\ub798\ud50c\ub79c.Desktop.App.Infrastructure.InstallRootUpdateGate;
using UpdaterInstallRootUpdateLock =
    \uac70\ub798\ud50c\ub79c.Updater.InstallRootUpdateLock;
using UpdaterProgram =
    \uac70\ub798\ud50c\ub79c.Updater.Program;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class UpdaterTripleLeaseContractTests
{
    [Fact]
    public void TripleLease_RuntimeAcquiresRootOperationWorkerInContractOrderAndReleasesAll()
    {
        var roots = NewInstallRoots();
        var expectedNames = roots
            .Select(UpdaterInstallRootUpdateLock.CreateMutexName)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .Concat(
                roots
                    .Select(UpdaterInstallRootUpdateLock.CreateOperationLeaseMutexName)
                    .OrderBy(static name => name, StringComparer.Ordinal))
            .Concat(
                roots
                    .Select(UpdaterInstallRootUpdateLock.CreateWorkerLeaseMutexName)
                    .OrderBy(static name => name, StringComparer.Ordinal))
            .ToArray();

        Assert.Equal(
            expectedNames,
            DesktopInstallRootUpdateGate.GetOrderedGateMutexNames(
                new[] { roots[1], roots[0], roots[1] }));

        using var rootLease =
            UpdaterInstallRootUpdateLock.AcquireForDesktopHandoff(
                roots,
                TimeSpan.Zero);
        AssertLeaseConflict(
            () => UpdaterInstallRootUpdateLock.AcquireForDesktopHandoff(
                roots,
                TimeSpan.Zero));

        using var operationLease =
            UpdaterInstallRootUpdateLock.AcquireOperationLeasesForDesktopHandoff(
                roots,
                TimeSpan.Zero);
        AssertLeaseConflict(
            () => UpdaterInstallRootUpdateLock.AcquireOperationLeasesForDesktopHandoff(
                roots,
                TimeSpan.Zero));

        using var workerLease =
            UpdaterInstallRootUpdateLock.AcquireWorkerLeasesForDesktopHandoff(
                roots,
                TimeSpan.Zero);
        AssertLeaseConflict(
            () => UpdaterInstallRootUpdateLock.AcquireWorkerLeasesForDesktopHandoff(
                roots,
                TimeSpan.Zero));

        AssertAppGateBlocked(roots);

        workerLease.Dispose();
        operationLease.Dispose();
        rootLease.Dispose();

        Assert.True(
            DesktopInstallRootUpdateGate.TryAcquireMany(
                roots,
                out var appGate));
        appGate!.Dispose();
    }

    [Fact]
    public void WorkerLeaseOrphan_AloneBlocksAppAndFailedAppAttemptReleasesPartialLeases()
    {
        var root = NewInstallRoots()[0];
        using var workerLease =
            UpdaterInstallRootUpdateLock.AcquireWorkerLeasesForDesktopHandoff(
                [root],
                TimeSpan.Zero);

        AssertAppGateBlocked([root]);

        using (var primaryProbe = new Mutex(
                   initiallyOwned: true,
                   DesktopInstallRootUpdateGate.BuildMutexName(root),
                   out var primaryCreated))
        {
            Assert.True(primaryCreated);
            primaryProbe.ReleaseMutex();
        }

        using (var operationProbe = new Mutex(
                   initiallyOwned: true,
                   DesktopInstallRootUpdateGate.BuildOperationLeaseMutexName(root),
                   out var operationCreated))
        {
            Assert.True(operationCreated);
            operationProbe.ReleaseMutex();
        }

        workerLease.Dispose();

        Assert.True(
            DesktopInstallRootUpdateGate.TryAcquire(
                root,
                out var appGate));
        appGate!.Dispose();
    }

    [Fact]
    public void InstallerMutationGateRoots_CustomIsBoundedAndCanonicalLegacyAreBridged()
    {
        var customRoot = NewInstallRoots()[0];
        var canonicalRoot = UpdaterProgram.GetCanonicalInstallRoot();
        var legacyRoot = UpdaterProgram.GetLegacyInstallRoot();
        var expectedBridgeRoots = new[]
        {
            Path.GetFullPath(canonicalRoot),
            Path.GetFullPath(legacyRoot)
        }
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(UpdaterInstallRootUpdateLock.CreateMutexName, StringComparer.Ordinal)
        .ToArray();

        Assert.Equal(
            [Path.GetFullPath(customRoot)],
            UpdaterProgram.GetInstallerMutationGateRoots(
                customRoot)
                .ToArray());
        Assert.Equal(
            expectedBridgeRoots,
            UpdaterProgram.GetInstallerMutationGateRoots(canonicalRoot)
                .ToArray());
        Assert.Equal(
            expectedBridgeRoots,
            UpdaterProgram.GetInstallerMutationGateRoots(
                legacyRoot + Path.DirectorySeparatorChar)
                .ToArray());
    }

    [Fact]
    public void EffectiveInstallAndRecoveryProbeRoots_BridgeLegacyToCanonicalButBoundCustom()
    {
        var customRoot = NewInstallRoots()[0];
        var canonicalRoot = UpdaterProgram.GetCanonicalInstallRoot();
        var legacyRoot = UpdaterProgram.GetLegacyInstallRoot();
        var expectedProbeRoots = new[]
        {
            Path.GetFullPath(canonicalRoot),
            Path.GetFullPath(legacyRoot)
        }
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

        Assert.Equal(
            Path.GetFullPath(canonicalRoot),
            UpdaterProgram.GetEffectiveInstallerInstallRoot(
                legacyRoot + Path.DirectorySeparatorChar));
        Assert.Equal(
            Path.GetFullPath(customRoot),
            UpdaterProgram.GetEffectiveInstallerInstallRoot(
                customRoot));

        Assert.Equal(
            expectedProbeRoots,
            UpdaterProgram.GetGeneratedRecoveryProbeRoots(canonicalRoot)
                .ToArray());
        Assert.Equal(
            expectedProbeRoots,
            UpdaterProgram.GetGeneratedRecoveryProbeRoots(legacyRoot)
                .ToArray());
        Assert.Equal(
            [Path.GetFullPath(customRoot)],
            UpdaterProgram.GetGeneratedRecoveryProbeRoots(
                customRoot)
                .ToArray());

        Assert.False(UpdaterProgram.RequiresElevation(legacyRoot));
        Assert.True(
            UpdaterProgram.RequiresElevation(
                UpdaterProgram.GetEffectiveInstallerInstallRoot(legacyRoot)));
        Assert.False(
            UpdaterProgram.RequiresElevation(
                UpdaterProgram.GetEffectiveInstallerInstallRoot(customRoot)));
    }

    [Fact]
    public void ProgramSource_HandoffsLeasesToGeneratedSupervisorForRecoveryAndInstall()
    {
        var source = ReadUpdaterProgramSource();
        var execute = GetMethodSlice(
            source,
            "private static async Task ExecuteAsync",
            "internal static async Task ExecuteInstallWithRollbackAsync");
        var generatedHandoff = execute.IndexOf(
            "// The updater retains every primary root gate.",
            StringComparison.Ordinal);
        Assert.True(generatedHandoff >= 0);
        var rootGateAcquire = execute.IndexOf(
            "using var installRootUpdateLock =",
            StringComparison.Ordinal);

        var workerRelease = execute.IndexOf(
            "installWorkerLease.Dispose();",
            generatedHandoff,
            StringComparison.Ordinal);
        var operationRelease = execute.IndexOf(
            "installOperationLease.Dispose();",
            workerRelease + 1,
            StringComparison.Ordinal);
        var recoveryChild = execute.IndexOf(
            "await RecoverGeneratedInstallStateBeforeVersionDecisionAsync(",
            operationRelease + 1,
            StringComparison.Ordinal);
        var installChild = execute.IndexOf(
            "await ExecuteInstallWithRollbackAsync(",
            operationRelease + 1,
            StringComparison.Ordinal);
        var firstRootGateRelease = execute.IndexOf(
            "installRootUpdateLock.Dispose();",
            generatedHandoff,
            StringComparison.Ordinal);
        var normalInstallRootGateRelease = execute.IndexOf(
            "installRootUpdateLock.Dispose();",
            installChild,
            StringComparison.Ordinal);

        Assert.True(rootGateAcquire >= 0 && rootGateAcquire < generatedHandoff);
        Assert.True(workerRelease > generatedHandoff);
        Assert.True(operationRelease > workerRelease);
        Assert.True(recoveryChild > operationRelease);
        Assert.True(installChild > operationRelease);
        Assert.True(firstRootGateRelease > recoveryChild);
        Assert.True(normalInstallRootGateRelease > installChild);

        var executeInstall = GetMethodSlice(
            source,
            "internal static async Task ExecuteInstallWithRollbackAsync",
            "internal static async Task<InstalledVersionState>");
        var installChildOwnsRootGate = execute.IndexOf(
            "updaterOwnsInstallRootGate: true",
            installChild,
            StringComparison.Ordinal);
        var recoveryChildOwnsRootGate = execute.IndexOf(
            "updaterOwnsInstallRootGate: true",
            recoveryChild,
            StringComparison.Ordinal);
        var runGeneratedSupervisor = executeInstall.IndexOf(
            "await RunInstallScriptAsync(",
            StringComparison.Ordinal);
        var recoverGeneratedSupervisor = executeInstall.IndexOf(
            "RecoverGeneratedInstallStateBeforeVersionDecisionAsync(",
            runGeneratedSupervisor + 1,
            StringComparison.Ordinal);
        var verifyRecoveryAbsent = executeInstall.IndexOf(
            "EnsureGeneratedInstallRecoveryAbsent(options.InstallRoot);",
            recoverGeneratedSupervisor + 1,
            StringComparison.Ordinal);

        Assert.True(
            recoveryChildOwnsRootGate > recoveryChild &&
            recoveryChildOwnsRootGate < installChild);
        Assert.True(installChildOwnsRootGate > installChild);
        Assert.True(runGeneratedSupervisor >= 0);
        Assert.True(recoverGeneratedSupervisor > runGeneratedSupervisor);
        Assert.True(verifyRecoveryAbsent > recoverGeneratedSupervisor);
        Assert.DoesNotContain(
            "InstallRollbackSupervisor.RecoverUntilVerifiedAsync(",
            executeInstall,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AcquireOperationLeasesForDesktopHandoff(",
            executeInstall,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AcquireWorkerLeasesForDesktopHandoff(",
            executeInstall,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProgramSource_UsesEffectiveCanonicalRootForBothLegacyElevationPaths()
    {
        var source = ReadUpdaterProgramSource();
        var executeInstall = GetMethodSlice(
            source,
            "internal static async Task ExecuteInstallWithRollbackAsync",
            "internal static async Task<InstalledVersionState>");
        var recoverInstall = GetMethodSlice(
            source,
            "internal static async Task<InstalledVersionState>",
            "private static async Task RunInstallScriptAsync");
        var runInstall = GetMethodSlice(
            source,
            "private static async Task RunInstallScriptAsync",
            "internal static async Task WaitForInstallProcessExitAsync");
        const string expected =
            "RequiresElevation(GetEffectiveInstallerInstallRoot(options.InstallRoot))";

        Assert.Contains(
            "await RunInstallScriptAsync(",
            executeInstall,
            StringComparison.Ordinal);
        Assert.Contains(
            "await RunInstallScriptAsync(",
            recoverInstall,
            StringComparison.Ordinal);
        Assert.Contains(expected, runInstall, StringComparison.Ordinal);
        Assert.DoesNotContain(
            expected,
            executeInstall,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            expected,
            recoverInstall,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "RequiresElevation(options.InstallRoot)",
            executeInstall,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "RequiresElevation(options.InstallRoot)",
            recoverInstall,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "RequiresElevation(options.InstallRoot)",
            runInstall,
            StringComparison.Ordinal);
    }

    private static string[] NewInstallRoots()
    {
        var unique = Guid.NewGuid().ToString("N");
        var root = Path.Combine(
            TestProcessIsolation.AppRoot,
            "updater-triple-lease",
            unique);
        return
        [
            Path.Combine(root, "canonical"),
            Path.Combine(root, "legacy")
        ];
    }

    private static void AssertLeaseConflict(
        Func<UpdaterInstallRootUpdateLock> acquire)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            using var unexpected = acquire();
        });
        Assert.NotEmpty(error.Message);
    }

    private static void AssertAppGateBlocked(IEnumerable<string> roots)
    {
        Assert.False(
            DesktopInstallRootUpdateGate.TryAcquireMany(
                roots,
                out var blocked));
        Assert.Null(blocked);
    }

    private static string ReadUpdaterProgramSource()
        => File.ReadAllText(
            Path.Combine(
                FindRepositoryRoot(),
                "Updater",
                "\uac70\ub798\ud50c\ub79c.Updater",
                "Program.cs"));

    private static string GetMethodSlice(
        string source,
        string startMarker,
        string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Method start was not found: {startMarker}");
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"Method end was not found: {endMarker}");
        return source[start..end];
    }

    private static string FindRepositoryRoot(
        [CallerFilePath] string sourceFilePath = "")
    {
        var directory = new DirectoryInfo(
            Path.GetDirectoryName(sourceFilePath)
            ?? AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(
                    directory.FullName,
                    "Updater",
                    "\uac70\ub798\ud50c\ub79c.Updater",
                    "Program.cs")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "\uac70\ub798\ud50c\ub79c repository root was not found.");
    }
}
