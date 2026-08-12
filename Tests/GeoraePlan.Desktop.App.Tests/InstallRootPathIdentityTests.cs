using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DesktopInstallRootUpdateGate =
    \uac70\ub798\ud50c\ub79c.Desktop.App.Infrastructure.InstallRootUpdateGate;
using InstallRootPathIdentity =
    \uac70\ub798\ud50c\ub79c.Shared.Contracts.InstallRootPathIdentity;
using InstallRecoveryStateProbe =
    \uac70\ub798\ud50c\ub79c.Shared.Contracts.InstallRecoveryStateProbe;
using LegacyInstallRollbackStateProbe =
    \uac70\ub798\ud50c\ub79c.Shared.Contracts.LegacyInstallRollbackStateProbe;
using InstallRollbackSupervisor =
    \uac70\ub798\ud50c\ub79c.Updater.InstallRollbackSupervisor;
using UpdaterInstallRootUpdateLock =
    \uac70\ub798\ud50c\ub79c.Updater.InstallRootUpdateLock;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class InstallRootPathIdentityTests
{
    [Fact]
    public void NonexistentLeaf_UsesFinalExistingAncestorWithoutCreatingIt()
    {
        var testRoot = CreateTestRoot("missing-leaf");
        try
        {
            var existingRoot = Path.Combine(
                testRoot,
                "existing physical root");
            Directory.CreateDirectory(existingRoot);
            var requestedRoot = Path.Combine(
                existingRoot,
                "missing",
                "nested",
                "install");

            var resolvedExistingRoot =
                InstallRootPathIdentity.Resolve(existingRoot);
            var resolvedRequestedRoot =
                InstallRootPathIdentity.Resolve(requestedRoot);

            Assert.Equal(
                Path.Combine(
                    resolvedExistingRoot,
                    "missing",
                    "nested",
                    "install"),
                resolvedRequestedRoot,
                ignoreCase: true);
            Assert.False(Directory.Exists(requestedRoot));
            Assert.Equal(
                DesktopInstallRootUpdateGate.BuildMutexName(
                    requestedRoot),
                UpdaterInstallRootUpdateLock.CreateMutexName(
                    requestedRoot));
            Assert.Equal(
                DesktopInstallRootUpdateGate
                    .BuildOperationLeaseMutexName(requestedRoot),
                UpdaterInstallRootUpdateLock
                    .CreateOperationLeaseMutexName(requestedRoot));
            Assert.Equal(
                DesktopInstallRootUpdateGate
                    .BuildWorkerLeaseMutexName(requestedRoot),
                UpdaterInstallRootUpdateLock
                    .CreateWorkerLeaseMutexName(requestedRoot));
        }
        finally
        {
            CleanupTestRoot(testRoot);
        }
    }

    [Fact]
    public void JunctionAncestor_IsRejectedBeforeMissingLeafMutation()
    {
        var testRoot = CreateTestRoot("junction-ancestor");
        var physicalRoot = Path.Combine(testRoot, "physical-target");
        var junctionRoot = Path.Combine(testRoot, "junction-alias");
        try
        {
            Directory.CreateDirectory(physicalRoot);
            Assert.True(
                TryCreateJunction(
                    junctionRoot,
                    physicalRoot,
                    out var junctionDiagnostic),
                "A real junction was required but could not be created. " +
                junctionDiagnostic);

            var requestedRoot = Path.Combine(
                junctionRoot,
                "missing",
                "install");
            Assert.ThrowsAny<IOException>(
                () => InstallRootPathIdentity.Resolve(requestedRoot));
            Assert.ThrowsAny<IOException>(
                () => DesktopInstallRootUpdateGate.BuildMutexName(
                    requestedRoot));
            Assert.ThrowsAny<IOException>(
                () => UpdaterInstallRootUpdateLock.CreateMutexName(
                    requestedRoot));
            Assert.ThrowsAny<IOException>(
                () => InstallRecoveryStateProbe.GetStatePath(
                    requestedRoot));
            Assert.ThrowsAny<IOException>(
                () => InstallRollbackSupervisor.GetStateRoot(
                    Path.Combine(testRoot, "artifacts"),
                    requestedRoot));
            Assert.False(
                Directory.Exists(
                    Path.Combine(physicalRoot, "missing")));
        }
        finally
        {
            if (Directory.Exists(junctionRoot))
                Directory.Delete(junctionRoot);
            CleanupTestRoot(testRoot);
        }
    }

    [Fact]
    public async Task ShortAndLongAliases_WhenAvailableShareAllGateAndStateKeys()
    {
        var testRoot = CreateTestRoot("short-path-alias");
        try
        {
            var longRoot = Path.Combine(
                testRoot,
                "long install directory identity");
            var artifactRoot = Path.Combine(
                testRoot,
                "artifact directory identity");
            Directory.CreateDirectory(longRoot);
            Directory.CreateDirectory(artifactRoot);

            var shortRoot = TryGetShortPath(longRoot);
            if (string.IsNullOrWhiteSpace(shortRoot) ||
                string.Equals(
                    shortRoot,
                    longRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                if (await TrySetShortNameAsync(longRoot, "GPROOT"))
                    shortRoot = TryGetShortPath(longRoot);
            }

            if (string.IsNullOrWhiteSpace(shortRoot) ||
                string.Equals(
                    shortRoot,
                    longRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                Assert.Fail(
                    "A real Windows short-path alias was required but could not be created.");
            }

            shortRoot = Path.GetFullPath(shortRoot);
            Assert.Equal(
                InstallRootPathIdentity.Resolve(longRoot),
                InstallRootPathIdentity.Resolve(shortRoot),
                ignoreCase: true);
            Assert.Equal(
                DesktopInstallRootUpdateGate.BuildMutexName(longRoot),
                DesktopInstallRootUpdateGate.BuildMutexName(shortRoot));
            Assert.Equal(
                UpdaterInstallRootUpdateLock
                    .CreateOperationLeaseMutexName(longRoot),
                UpdaterInstallRootUpdateLock
                    .CreateOperationLeaseMutexName(shortRoot));
            Assert.Equal(
                UpdaterInstallRootUpdateLock
                    .CreateWorkerLeaseMutexName(longRoot),
                UpdaterInstallRootUpdateLock
                    .CreateWorkerLeaseMutexName(shortRoot));
            Assert.Equal(
                InstallRecoveryStateProbe.GetStatePath(longRoot),
                InstallRecoveryStateProbe.GetStatePath(shortRoot),
                ignoreCase: true);
            Assert.Equal(
                InstallRollbackSupervisor.GetStateRoot(
                    artifactRoot,
                    longRoot),
                InstallRollbackSupervisor.GetStateRoot(
                    artifactRoot,
                    shortRoot),
                ignoreCase: true);

            var legacyCandidatePaths =
                LegacyInstallRollbackStateProbe
                    .GetCandidateStatePathsCore(
                        artifactRoot,
                        longRoot,
                        shortRoot);
            Assert.Equal(2, legacyCandidatePaths.Length);
            var rawLegacyStatePath = legacyCandidatePaths.Single(
                path => !string.Equals(
                    path,
                    InstallRollbackSupervisor.GetStateRoot(
                        artifactRoot,
                        longRoot),
                    StringComparison.OrdinalIgnoreCase));
            Directory.CreateDirectory(rawLegacyStatePath);
            var rawLegacyProbe =
                LegacyInstallRollbackStateProbe.Probe(
                    artifactRoot,
                    longRoot,
                    shortRoot);
            Assert.Equal(
                \uac70\ub798\ud50c\ub79c.Shared.Contracts.InstallRecoveryStateStatus.Present,
                rawLegacyProbe.Status);
            Assert.Equal(
                rawLegacyStatePath,
                rawLegacyProbe.StatePath,
                ignoreCase: true);

            await InstallRollbackSupervisor
                .RecoverPendingCandidatesOnceAsync(
                    [artifactRoot],
                    longRoot,
                    shortRoot,
                    log: null);
            Assert.False(Directory.Exists(rawLegacyStatePath));

            var legacyInstallRootCandidates =
                \uac70\ub798\ud50c\ub79c.Desktop.App.App
                    .GetLegacyRollbackInstallRootCandidates(
                        [longRoot],
                        [shortRoot]);
            var physicalLongRoot =
                InstallRootPathIdentity.Resolve(longRoot);
            Assert.Contains(
                Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(shortRoot)),
                legacyInstallRootCandidates[physicalLongRoot],
                StringComparer.OrdinalIgnoreCase);

            var launchExe = Path.Combine(longRoot, "거래플랜.exe");
            File.WriteAllText(launchExe, "test executable placeholder");
            var parsedArguments =
                \uac70\ub798\ud50c\ub79c.Updater.UpdateArguments.Parse(
                    [
                        "--package-path",
                        Path.Combine(testRoot, "package.zip"),
                        "--sha256",
                        new string('A', 64),
                        "--install-root",
                        shortRoot,
                        "--launch-exe",
                        launchExe,
                        "--process-id",
                        Environment.ProcessId.ToString(),
                        "--process-exe",
                        launchExe,
                        "--process-start-time-utc-ticks",
                        DateTime.UtcNow.Ticks.ToString(),
                        "--handoff-pipe",
                        \uac70\ub798\ud50c\ub79c.Updater.UpdateArguments
                            .HandoffPipeNamePrefix +
                        Guid.NewGuid().ToString("N"),
                        "--version",
                        "1.0.0",
                        "--file-size",
                        "1"
                    ]);
            Assert.Equal(
                physicalLongRoot,
                parsedArguments.InstallRoot,
                ignoreCase: true);
            Assert.Equal(
                Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(shortRoot)),
                parsedArguments.LegacyInstallRoot,
                ignoreCase: true);

            Assert.True(
                DesktopInstallRootUpdateGate.TryAcquire(
                    longRoot,
                    out var appGate));
            using (appGate)
            {
                Assert.Throws<InvalidOperationException>(
                    () => UpdaterInstallRootUpdateLock
                        .AcquireForDesktopHandoff(
                            shortRoot,
                            TimeSpan.Zero));
            }
        }
        finally
        {
            CleanupTestRoot(testRoot);
        }
    }

    private static string CreateTestRoot(string scenario)
    {
        var root = Path.Combine(
            TestProcessIsolation.TempRoot,
            "path-identity",
            $"{scenario}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static bool TryCreateJunction(
        string junctionPath,
        string targetPath,
        out string diagnostic)
    {
        using var process = Process.Start(
            new ProcessStartInfo
            {
                FileName = "cmd.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList =
                {
                    "/d",
                    "/c",
                    "mklink",
                    "/J",
                    junctionPath,
                    targetPath
                }
            }) ?? throw new InvalidOperationException(
                "Junction helper did not start.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        diagnostic =
            $"ExitCode={process.ExitCode}; " +
            $"stdout={stdout.Trim()}; stderr={stderr.Trim()}";
        return process.ExitCode == 0 && Directory.Exists(junctionPath);
    }

    private static string? TryGetShortPath(string path)
    {
        var buffer = new StringBuilder(512);
        var result = GetShortPathName(
            path,
            buffer,
            checked((uint)buffer.Capacity));
        if (result == 0)
        {
            var error = Marshal.GetLastWin32Error();
            if (error is 0 or 1 or 50)
                return null;
            throw new Win32Exception(
                error,
                $"Short path lookup failed: {path}");
        }

        if (result >= buffer.Capacity)
        {
            buffer = new StringBuilder(checked((int)result + 1));
            result = GetShortPathName(
                path,
                buffer,
                checked((uint)buffer.Capacity));
            if (result == 0 || result >= buffer.Capacity)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"Short path lookup failed: {path}");
            }
        }

        return buffer.ToString();
    }

    private static async Task<bool> TrySetShortNameAsync(
        string path,
        string shortName)
    {
        var fsutilPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "fsutil.exe");
        if (!File.Exists(fsutilPath))
            return false;

        using var process = Process.Start(
            new ProcessStartInfo
            {
                FileName = fsutilPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList =
                {
                    "file",
                    "setShortName",
                    path,
                    shortName
                }
            }) ?? throw new InvalidOperationException(
                "Short-name helper did not start.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync()
                .WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            return false;
        }

        await Task.WhenAll(stdoutTask, stderrTask);
        return process.ExitCode == 0;
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

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetShortPathNameW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern uint GetShortPathName(
        string longPath,
        StringBuilder shortPath,
        uint bufferLength);
}
