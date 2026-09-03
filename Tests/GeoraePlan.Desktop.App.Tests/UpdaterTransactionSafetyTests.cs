using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.IO.Pipes;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using DesktopApplication =
    \uac70\ub798\ud50c\ub79c.Desktop.App.App;
using \uac70\ub798\ud50c\ub79c.Desktop.App.Infrastructure;
using \uac70\ub798\ud50c\ub79c.Desktop.App.Services;
using \uac70\ub798\ud50c\ub79c.Shared.Contracts;
using 거래플랜.Updater;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class UpdaterTransactionSafetyTests
{
    [Fact]
    public void Program_AcquiresTheInstallRootLockBeforeCreatingOrCleaningUpdateArtifacts()
    {
        var programPath = Path.Combine(
            FindRepositoryRoot(),
            "Updater",
            "거래플랜.Updater",
            "Program.cs");
        var source = File.ReadAllText(programPath);

        var executeMethod = source.IndexOf(
            "private static async Task ExecuteAsync",
            StringComparison.Ordinal);
        var acquireLock = source.IndexOf(
            "InstallRootUpdateLock.AcquireForDesktopHandoff(",
            executeMethod,
            StringComparison.Ordinal);
        var validateIdentity = source.IndexOf(
            "EnsureExpectedProcessIdentity(options);",
            executeMethod,
            StringComparison.Ordinal);
        var signalHandoff = source.IndexOf(
            "await SignalDesktopHandoffAsync(options);",
            executeMethod,
            StringComparison.Ordinal);
        var firstArtifactMutation = source.IndexOf(
            "TryCleanupStaleUpdateArtifacts();",
            executeMethod,
            StringComparison.Ordinal);
        var installedVersionGate = source.IndexOf(
            "GetInstalledVersionState(options",
            acquireLock,
            StringComparison.Ordinal);

        Assert.True(executeMethod >= 0, "Updater ExecuteAsync method was not found.");
        Assert.True(validateIdentity > executeMethod);
        Assert.True(signalHandoff > validateIdentity);
        Assert.True(acquireLock > signalHandoff);
        Assert.True(firstArtifactMutation > acquireLock);
        Assert.True(installedVersionGate > firstArtifactMutation);
        Assert.True(acquireLock > executeMethod, "Install-root lock acquisition was not found in ExecuteAsync.");
        Assert.True(
            firstArtifactMutation > acquireLock,
            "The updater must acquire the install-root lock before cleaning or creating update artifacts.");
    }

    [Fact]
    public void Program_ReleasesTheInstallRootGateBeforeLaunchingTheUpdatedDesktop()
    {
        var programPath = Path.Combine(
            FindRepositoryRoot(),
            "Updater",
            "거래플랜.Updater",
            "Program.cs");
        var source = File.ReadAllText(programPath);
        var executeMethod = source.IndexOf(
            "private static async Task ExecuteAsync",
            StringComparison.Ordinal);
        var releaseGate = source.IndexOf(
            "installRootUpdateLock.Dispose();",
            executeMethod,
            StringComparison.Ordinal);
        var launchDesktop = source.IndexOf(
            "Process.Start(new ProcessStartInfo",
            releaseGate,
            StringComparison.Ordinal);

        Assert.True(releaseGate > executeMethod);
        Assert.True(
            launchDesktop > releaseGate,
            "The updated desktop must be launched only after the updater releases the shared gate.");
    }

    [Fact]
    public void DesktopLauncher_PassesBoundIdentityWithArgumentList()
    {
        var servicePath = Path.Combine(
            FindRepositoryRoot(),
            "Desktop",
            "거래플랜.Desktop.App",
            "Services",
            "DesktopAppUpdateService.cs");
        var source = File.ReadAllText(servicePath);

        Assert.Contains("\"--process-exe\"", source, StringComparison.Ordinal);
        Assert.Contains("\"--process-start-time-utc-ticks\"", source, StringComparison.Ordinal);
        Assert.Contains("\"--handoff-pipe\"", source, StringComparison.Ordinal);
        Assert.Contains(
            "if (!PathsEqual(currentProcessPath, launchExePath))",
            source,
            StringComparison.Ordinal);
        Assert.Contains("startInfo.ArgumentList.Add(argument);", source, StringComparison.Ordinal);
        Assert.Contains(
            "await WaitForUpdaterIdentityVerificationAsync(",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Arguments = string.Join(\" \", argumentParts)",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopLauncher_RevalidatesVersionPolicyImmediatelyBeforeStartingUpdater()
    {
        var servicePath = Path.Combine(
            FindRepositoryRoot(),
            "Desktop",
            "거래플랜.Desktop.App",
            "Services",
            "DesktopAppUpdateService.cs");
        var source = File.ReadAllText(servicePath);
        var startMethod = source.IndexOf(
            "private async Task StartUpdateCoreAsync",
            StringComparison.Ordinal);
        var validatePolicy = source.IndexOf(
            "if (!IsPackageVersionPolicySatisfied(",
            startMethod,
            StringComparison.Ordinal);
        var startUpdater = source.IndexOf(
            "Process.Start(startInfo)",
            startMethod,
            StringComparison.Ordinal);

        Assert.True(startMethod >= 0);
        Assert.True(validatePolicy > startMethod);
        Assert.True(startUpdater > validatePolicy);
    }

    [Fact]
    public void MutexName_NormalizesInstallRootAndDoesNotExposeThePath()
    {
        var installRoot = NewInstallRoot();
        var equivalentRoot = installRoot.ToLowerInvariant() + Path.DirectorySeparatorChar;

        var firstName = InstallRootUpdateLock.CreateMutexName(installRoot);
        var secondName = InstallRootUpdateLock.CreateMutexName(equivalentRoot);

        Assert.Equal(firstName, secondName);
        Assert.StartsWith(@"Global\GeoraePlan.Updater.InstallRoot.", firstName, StringComparison.Ordinal);
        Assert.Matches(@"^Global\\GeoraePlan\.Updater\.InstallRoot\.[0-9A-F]{64}$", firstName);
        Assert.DoesNotContain(Path.GetFileName(installRoot), firstName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(installRoot, firstName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Acquire_RejectsASecondUpdaterForTheSameInstallRoot()
    {
        var installRoot = NewInstallRoot();
        using var first = InstallRootUpdateLock.Acquire(installRoot);

        var error = Assert.Throws<InvalidOperationException>(
            () => InstallRootUpdateLock.Acquire(installRoot + Path.DirectorySeparatorChar));

        Assert.Contains("이미 진행 중", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopAndUpdater_UseTheSameInstallRootGate()
    {
        var installRoot = NewInstallRoot();

        Assert.Equal(
            InstallRootUpdateGate.BuildMutexName(installRoot),
            InstallRootUpdateLock.CreateMutexName(
                installRoot.ToLowerInvariant() + Path.DirectorySeparatorChar));

        using var updaterLock = InstallRootUpdateLock.Acquire(installRoot);
        Assert.False(InstallRootUpdateGate.TryAcquire(installRoot, out var desktopGate));
        Assert.Null(desktopGate);
    }

    [Fact]
    public void UpdaterHandoffWait_IsBoundedAndSucceedsAfterDesktopReleasesTheGate()
    {
        var installRoot = NewInstallRoot();
        Assert.True(InstallRootUpdateGate.TryAcquire(installRoot, out var desktopGate));

        try
        {
            Assert.Throws<InvalidOperationException>(
                () => InstallRootUpdateLock.AcquireForDesktopHandoff(
                    installRoot,
                    TimeSpan.FromMilliseconds(50)));
        }
        finally
        {
            desktopGate!.Dispose();
        }

        using var updaterLock = InstallRootUpdateLock.AcquireForDesktopHandoff(
            installRoot,
            TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Acquire_AllowsDifferentInstallRootsAtTheSameTime()
    {
        using var first = InstallRootUpdateLock.Acquire(NewInstallRoot());
        using var second = InstallRootUpdateLock.Acquire(NewInstallRoot());
    }

    [Fact]
    public void Dispose_AfterAnExceptionalExit_AllowsTheInstallRootToBeAcquiredAgain()
    {
        var installRoot = NewInstallRoot();

        Assert.Throws<ExpectedUpdaterFailureException>((Action)(() =>
        {
            using var updateLock = InstallRootUpdateLock.Acquire(installRoot);
            throw new ExpectedUpdaterFailureException();
        }));

        using var reacquired = InstallRootUpdateLock.Acquire(installRoot);
    }

    [Fact]
    public void WorkRoot_IsCollisionResistantForConcurrentUpdaterProcesses()
    {
        var first = Program.CreateUpdateWorkRoot();
        var second = Program.CreateUpdateWorkRoot();

        Assert.NotEqual(first, second);
        Assert.Contains(
            Environment.ProcessId.ToString(),
            Path.GetFileName(first),
            StringComparison.Ordinal);
        Assert.StartsWith(
            Path.GetFullPath(TestProcessIsolation.TempRoot),
            Path.GetFullPath(first),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProcessIdentity_AllowsOnlyTheExpectedExecutableAndStartTime()
    {
        using var process = Process.GetCurrentProcess();
        var processPath = Path.GetFullPath(
            process.MainModule?.FileName
            ?? throw new InvalidOperationException("Test process path unavailable."));
        var expected = new UpdateArguments
        {
            ProcessId = process.Id,
            ExpectedProcessExePath = processPath,
            ProcessStartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks
        };

        Program.EnsureExpectedProcessIdentity(expected);

        var wrongStartTime = new UpdateArguments
        {
            ProcessId = process.Id,
            ExpectedProcessExePath = processPath,
            ProcessStartTimeUtcTicks = expected.ProcessStartTimeUtcTicks + 1
        };
        Assert.Throws<ProcessIdentityValidationException>(
            () => Program.EnsureExpectedProcessIdentity(wrongStartTime));

        var wrongPath = new UpdateArguments
        {
            ProcessId = process.Id,
            ExpectedProcessExePath = Path.Combine(
                Path.GetDirectoryName(processPath)!,
                "unrelated.exe"),
            ProcessStartTimeUtcTicks = expected.ProcessStartTimeUtcTicks
        };
        Assert.Throws<ProcessIdentityValidationException>(
            () => Program.EnsureExpectedProcessIdentity(wrongPath));

        var missingProcess = new UpdateArguments
        {
            ProcessId = int.MaxValue,
            ExpectedProcessExePath = processPath,
            ProcessStartTimeUtcTicks = expected.ProcessStartTimeUtcTicks
        };
        Assert.Throws<ProcessIdentityValidationException>(
            () => Program.EnsureExpectedProcessIdentity(missingProcess));
    }

    [Fact]
    public void UpdateArguments_RequireBoundProcessIdentityAndPreservePathsWithSpaces()
    {
        var installRoot = Path.Combine(NewInstallRoot(), "root with spaces");
        var launchExe = Path.Combine(installRoot, "거래플랜.exe");
        var args = new[]
        {
            "--package-path", Path.Combine(installRoot, "package with spaces.zip"),
            "--sha256", new string('A', 64),
            "--install-root", installRoot,
            "--launch-exe", launchExe,
            "--process-id", "1234",
            "--process-exe", launchExe,
            "--process-start-time-utc-ticks", "638888888888888888",
            "--handoff-pipe", UpdateArguments.HandoffPipeNamePrefix + Guid.NewGuid().ToString("N"),
            "--version", "2.0.0"
        };

        var parsed = UpdateArguments.Parse(args);

        Assert.Equal(Path.GetFullPath(installRoot), parsed.InstallRoot);
        Assert.Equal(Path.GetFullPath(launchExe), parsed.ExpectedProcessExePath);
        Assert.Equal(638888888888888888L, parsed.ProcessStartTimeUtcTicks);

        foreach (var requiredKey in new[]
                 {
                     "--process-id",
                     "--process-exe",
                     "--process-start-time-utc-ticks",
                     "--handoff-pipe",
                     "--version"
                 })
        {
            var keyIndex = Array.IndexOf(args, requiredKey);
            var missingIdentity = args
                .Where((_, index) => index != keyIndex && index != keyIndex + 1)
                .ToArray();
            Assert.Throws<InvalidOperationException>(
                () => UpdateArguments.Parse(missingIdentity));
        }

        var zeroProcessId = (string[])args.Clone();
        zeroProcessId[Array.IndexOf(zeroProcessId, "--process-id") + 1] = "0";
        Assert.Throws<InvalidOperationException>(
            () => UpdateArguments.Parse(zeroProcessId));
    }

    [Theory]
    [InlineData("3.0.0")]
    [InlineData("invalid")]
    public async Task CheckForUpdatesAsync_InconsistentManifestPreservesBlockingMinimumState(
        string packageVersion)
    {
        var package = CreateUpdatePackage(packageVersion, "4.0.0");
        var handler = new UpdateManifestHandler(package);
        var api = new ErpApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") },
            new SessionState());
        var service = new DesktopAppUpdateService(
            api,
            currentVersionProvider: () => "2.0.0",
            startUpdateCoreOverride: null);

        var result = await service.CheckForUpdatesAsync("stable");

        Assert.Equal("2.0.0", result.CurrentVersion);
        Assert.Equal("4.0.0", result.MinimumSupportedVersion);
        Assert.True(result.IsBelowMinimumSupportedVersion);
        Assert.True(result.RequiresImmediateUpdate);
        Assert.False(result.IsUpdateAvailable);
        Assert.Null(result.Package);
        Assert.Contains("차단", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("invalid", "3.0.0", "", false)]
    [InlineData("2.0.0", "3.0.0", "invalid", false)]
    [InlineData("2.0.0", "invalid", "", true)]
    public async Task CheckForUpdatesAsync_UnparseableBlockingPolicyFailsClosed(
        string currentVersion,
        string packageVersion,
        string minimumSupportedVersion,
        bool mandatory)
    {
        var package = CreateUpdatePackage(
            packageVersion,
            minimumSupportedVersion,
            mandatory);
        var handler = new UpdateManifestHandler(package);
        var api = new ErpApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") },
            new SessionState());
        var service = new DesktopAppUpdateService(
            api,
            currentVersionProvider: () => currentVersion,
            startUpdateCoreOverride: null,
            verifiedHandoffShutdownScheduler: () => { });

        var result = await service.CheckForUpdatesAsync("stable");

        Assert.True(result.HasBlockingPolicyIssue);
        Assert.True(result.RequiresImmediateUpdate);
        Assert.False(result.IsUpdateAvailable);
        Assert.Null(result.Package);
        Assert.Contains("차단", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_ValidStaleManifestIsNonBlocking()
    {
        var package = CreateUpdatePackage(
            "2.0.0",
            "1.0.0",
            mandatory: true);
        var handler = new UpdateManifestHandler(package);
        var api = new ErpApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") },
            new SessionState());
        var service = new DesktopAppUpdateService(
            api,
            currentVersionProvider: () => "3.0.0",
            startUpdateCoreOverride: null,
            verifiedHandoffShutdownScheduler: () => { });

        var result = await service.CheckForUpdatesAsync("stable");

        Assert.False(result.HasBlockingPolicyIssue);
        Assert.False(result.IsBelowMinimumSupportedVersion);
        Assert.False(result.RequiresImmediateUpdate);
        Assert.False(result.IsUpdateAvailable);
        Assert.Null(result.Package);
        Assert.Contains("오래된 업데이트 매니페스트", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1.1.705", "1.1.705")]
    [InlineData("1.1.705.0", "1.1.705")]
    [InlineData("1.1.705", "1.1.705.0")]
    [InlineData("v1.1.705+desktop", "1.1.705")]
    public async Task CheckForUpdatesAsync_EquivalentVersionFormsReportCurrentVersion(
        string currentVersion,
        string packageVersion)
    {
        var package = CreateUpdatePackage(
            packageVersion,
            "1.1.704",
            mandatory: true);
        var handler = new UpdateManifestHandler(package);
        var api = new ErpApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") },
            new SessionState());
        var service = new DesktopAppUpdateService(
            api,
            currentVersionProvider: () => currentVersion,
            startUpdateCoreOverride: null,
            verifiedHandoffShutdownScheduler: () => { });

        var result = await service.CheckForUpdatesAsync("stable");

        Assert.False(result.HasBlockingPolicyIssue);
        Assert.False(result.IsBelowMinimumSupportedVersion);
        Assert.False(result.RequiresImmediateUpdate);
        Assert.False(result.IsUpdateAvailable);
        Assert.Null(result.Package);
        Assert.Contains("배포된 최신 버전입니다", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("오래된 업데이트 매니페스트", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("test", "desktop")]
    [InlineData("", "desktop")]
    [InlineData("Stable", "desktop")]
    [InlineData(" stable ", "desktop")]
    [InlineData("stable", "android")]
    [InlineData("stable", "")]
    [InlineData("stable", "DESKTOP")]
    [InlineData("stable", " desktop ")]
    public async Task CheckForUpdatesAsync_RequiresRequestedChannelAndDesktopPlatform(
        string manifestChannel,
        string packagePlatform)
    {
        var package = CreateUpdatePackage("3.0.0", "2.0.0");
        package.Platform = packagePlatform;
        var handler =
            new UpdateManifestHandler(
                package,
                manifestChannel);
        var api = new ErpApiClient(
            new HttpClient(handler)
            {
                BaseAddress = new Uri("http://localhost/")
            },
            new SessionState());
        var service = new DesktopAppUpdateService(
            api,
            currentVersionProvider: () => "2.0.0",
            startUpdateCoreOverride: null);

        var result =
            await service.CheckForUpdatesAsync("stable");

        Assert.True(result.HasBlockingPolicyIssue);
        Assert.True(result.RequiresImmediateUpdate);
        Assert.False(result.IsUpdateAvailable);
        Assert.Null(result.Package);
    }

    [Fact]
    public async Task StartUpdateAsync_AllowsOnlyOneProcessWideVerifiedLaunch()
    {
        DesktopAppUpdateService.ResetUpdateLaunchLatchForTests();
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var launchCount = 0;
        var api = new ErpApiClient(
            new HttpClient { BaseAddress = new Uri("http://localhost/") },
            new SessionState());
        var service = new DesktopAppUpdateService(
            api,
            currentVersionProvider: () => "1.0.0",
            startUpdateCoreOverride: async (_, _) =>
            {
                Interlocked.Increment(ref launchCount);
                entered.TrySetResult();
                await release.Task;
            },
            verifiedHandoffShutdownScheduler: () => { });
        var package = CreateUpdatePackage("2.0.0", string.Empty);

        try
        {
            var first = service.StartUpdateAsync(package);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.StartUpdateAsync(package));

            release.TrySetResult();
            await first;
            Assert.Equal(1, launchCount);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.StartUpdateAsync(package));
        }
        finally
        {
            release.TrySetResult();
            DesktopAppUpdateService.ResetUpdateLaunchLatchForTests();
        }
    }

    [Fact]
    public async Task StartUpdateAsync_FailedHandoffResetsLatchButStalePackageCannotRace()
    {
        DesktopAppUpdateService.ResetUpdateLaunchLatchForTests();
        var currentVersion = "1.0.0";
        var launchCount = 0;
        var api = new ErpApiClient(
            new HttpClient { BaseAddress = new Uri("http://localhost/") },
            new SessionState());
        var service = new DesktopAppUpdateService(
            api,
            currentVersionProvider: () => currentVersion,
            startUpdateCoreOverride: (_, _) =>
            {
                Interlocked.Increment(ref launchCount);
                return Task.FromException(
                    new InvalidOperationException("simulated handoff failure"));
            },
            verifiedHandoffShutdownScheduler: () => { });
        var stalePackage = CreateUpdatePackage("2.0.0", string.Empty);

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.StartUpdateAsync(stalePackage));
            Assert.Equal(1, launchCount);

            currentVersion = "2.0.0";
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.StartUpdateAsync(stalePackage));
            Assert.Equal(1, launchCount);
        }
        finally
        {
            DesktopAppUpdateService.ResetUpdateLaunchLatchForTests();
        }
    }

    [Fact]
    public async Task StartUpdateAsync_VerifiedHandoffSchedulesShutdownBeforeCallerContinuationFails()
    {
        DesktopAppUpdateService.ResetUpdateLaunchLatchForTests();
        var shutdownScheduleCount = 0;
        var api = new ErpApiClient(
            new HttpClient { BaseAddress = new Uri("http://localhost/") },
            new SessionState());
        var service = new DesktopAppUpdateService(
            api,
            currentVersionProvider: () => "1.0.0",
            startUpdateCoreOverride: (_, _) => Task.CompletedTask,
            verifiedHandoffShutdownScheduler: () =>
                Interlocked.Increment(ref shutdownScheduleCount));
        var package = CreateUpdatePackage("2.0.0", string.Empty);

        try
        {
            await Assert.ThrowsAsync<CallerStatusSetterException>(
                async () =>
                {
                    await service.StartUpdateAsync(package);
                    throw new CallerStatusSetterException();
                });
            Assert.Equal(1, shutdownScheduleCount);
        }
        finally
        {
            DesktopAppUpdateService.ResetUpdateLaunchLatchForTests();
        }
    }

    [Fact]
    public void InstalledVersionGate_RejectsDuplicateOrDowngradePackage()
    {
        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Test executable path unavailable.");
        var options = new UpdateArguments
        {
            LaunchExe = executablePath,
            Version = "0.0.1"
        };

        Assert.Equal(
            InstalledVersionState.AtLeastRequested,
            Program.GetInstalledVersionState(
                options,
                out var installedVersion));
        Assert.False(string.IsNullOrWhiteSpace(installedVersion));
    }

    [Fact]
    public async Task InstalledVersionGate_AllowsOnlyAnAbsentExecutableAmongUnknownStates()
    {
        var testRoot = NewInstallRoot();
        var launchExe = Path.Combine(testRoot, "거래플랜.exe");
        var options = new UpdateArguments
        {
            LaunchExe = launchExe,
            Version = "2.0.0"
        };

        try
        {
            Assert.Equal(
                InstalledVersionState.Absent,
                Program.GetInstalledVersionState(
                    options,
                    out var absentVersion));
            Assert.Equal(string.Empty, absentVersion);

            Directory.CreateDirectory(testRoot);
            await File.WriteAllTextAsync(
                launchExe,
                "not a versioned executable");

            Assert.Equal(
                InstalledVersionState.Unparseable,
                Program.GetInstalledVersionState(
                    options,
                    out var unparseableVersion));
            Assert.True(string.IsNullOrWhiteSpace(unparseableVersion));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task InstallProcessTimeout_KillsHangingFakeInstallerAndAllowsLockRelease()
    {
        var testRoot = NewInstallRoot();
        var fakeInstaller = Path.Combine(testRoot, "fake-hanging-installer.cmd");
        Directory.CreateDirectory(testRoot);
        await File.WriteAllTextAsync(
            fakeInstaller,
            """
            @echo off
            :hang
            ping 127.0.0.1 -n 2 >nul
            goto hang
            """);
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add(fakeInstaller);

        try
        {
            using (InstallRootUpdateLock.Acquire(testRoot))
            using (var process = Process.Start(startInfo)
                   ?? throw new InvalidOperationException("Fake installer did not start."))
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => Program.WaitForInstallProcessExitAsync(
                        process,
                        TimeSpan.FromMilliseconds(250)));
                Assert.Contains("초과", error.Message, StringComparison.Ordinal);
                Assert.True(process.HasExited);
            }

            using var reacquired = InstallRootUpdateLock.Acquire(testRoot);
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task GeneratedInstaller_HardKilledSupervisorKillsWorkerAndRecoveryRestores()
    {
        var testRoot = NewInstallRoot();
        var generatedInstaller =
            await BuildGeneratedInstallerPackageAsync(
                FindRepositoryRoot(),
                testRoot);
        await RewriteGeneratedManagedRootsAsync(
            generatedInstaller,
            Path.Combine(testRoot, "isolated-managed-roots"));
        var installRoot = Path.Combine(
            testRoot,
            "installed",
            "tradeplan");
        var originalMarker = Path.Combine(
            installRoot,
            "original-data.marker");
        var workerPidPath =
            GetGeneratedHangWorkerPidPath(generatedInstaller);
        var mutationPath = Path.Combine(
            installRoot,
            ".georaeplan-installer-timeout-mutation");
        Process? supervisor = null;
        Process? orphanWorker = null;

        try
        {
            Directory.CreateDirectory(installRoot);
            await File.WriteAllTextAsync(
                originalMarker,
                "original payload must survive");

            Assert.True(
                InstallRootUpdateGate.TryAcquire(
                    installRoot,
                    out var runningAppGate));
            try
            {
                var appBlockedResult = await RunPowerShellAsync(
                    generatedInstaller.InstallScriptPath,
                    new Dictionary<string, string?>(),
                    TimeSpan.FromSeconds(15),
                    "-InstallRoot",
                    installRoot,
                    "-RecoveryOnly",
                    "-NoLaunch",
                    "-NoShortcuts",
                    "-SuppressUi");
                Assert.NotEqual(0, appBlockedResult.ExitCode);
                Assert.Contains(
                    "같은 설치 위치",
                    appBlockedResult.StdOut + appBlockedResult.StdErr,
                    StringComparison.Ordinal);
            }
            finally
            {
                runningAppGate!.Dispose();
            }

            supervisor = StartPowerShellScript(
                generatedInstaller.InstallScriptPath,
                new Dictionary<string, string?>
                {
                    ["GEORAEPLAN_INSTALLER_TEST_HANG_AFTER_ROLLBACK_SNAPSHOT"] =
                        "1"
                },
                "-InstallRoot",
                installRoot,
                "-NoLaunch",
                "-NoShortcuts",
                "-SuppressUi",
                "-WorkerTimeoutSeconds",
                "60");
            await WaitForFileAsync(
                workerPidPath,
                TimeSpan.FromSeconds(20));
            await WaitForFileAsync(
                mutationPath,
                TimeSpan.FromSeconds(20));

            var secondInstallerResult = await RunPowerShellAsync(
                generatedInstaller.InstallScriptPath,
                new Dictionary<string, string?>(),
                TimeSpan.FromSeconds(15),
                "-InstallRoot",
                installRoot,
                "-RecoveryOnly",
                "-NoLaunch",
                "-NoShortcuts",
                "-SuppressUi");
            Assert.NotEqual(0, secondInstallerResult.ExitCode);
            Assert.Contains(
                "같은 설치 위치",
                secondInstallerResult.StdOut + secondInstallerResult.StdErr,
                StringComparison.Ordinal);
            Assert.False(
                InstallRootUpdateGate.TryAcquire(
                    installRoot,
                    out var blockedWhileSupervisorRuns));
            Assert.Null(blockedWhileSupervisorRuns);

            Assert.True(
                int.TryParse(
                    (await File.ReadAllTextAsync(workerPidPath)).Trim(),
                    out var workerPid));
            orphanWorker = Process.GetProcessById(workerPid);
            supervisor.Kill(entireProcessTree: false);
            await supervisor.WaitForExitAsync()
                .WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(250);
            await orphanWorker.WaitForExitAsync()
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(orphanWorker.HasExited);
            orphanWorker.Dispose();
            orphanWorker = null;

            Assert.True(
                InstallRootUpdateGate.TryAcquire(
                    installRoot,
                    out var reacquiredAfterSupervisorExit));
            reacquiredAfterSupervisorExit!.Dispose();
            Assert.NotNull(
                DesktopApplication.GetInstallRecoveryStartupBlockMessage(
                    installRoot));

            var recoveryResult = await RunPowerShellAsync(
                generatedInstaller.InstallScriptPath,
                new Dictionary<string, string?>(),
                TimeSpan.FromSeconds(30),
                "-InstallRoot",
                installRoot,
                "-RecoveryOnly",
                "-NoLaunch",
                "-NoShortcuts",
                "-SuppressUi");
            Assert.True(
                recoveryResult.ExitCode == 0,
                recoveryResult.StdOut + Environment.NewLine +
                recoveryResult.StdErr);
            Assert.Equal(
                "original payload must survive",
                await File.ReadAllTextAsync(originalMarker));
            Assert.False(File.Exists(mutationPath));
            Assert.True(
                InstallRootUpdateGate.TryAcquire(
                    installRoot,
                    out var reacquiredAfterRecovery));
            reacquiredAfterRecovery!.Dispose();
        }
        finally
        {
            TryTerminateProcess(orphanWorker);
            TryTerminateProcess(supervisor);
            MakeTreeDeletable(testRoot);
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task GeneratedInstaller_HardKilledSupervisorKillsExitedWorkerDescendantBeforeRecovery()
    {
        var testRoot = NewInstallRoot();
        var generatedInstaller =
            await BuildGeneratedInstallerPackageAsync(
                FindRepositoryRoot(),
                testRoot);
        await RewriteGeneratedManagedRootsAsync(
            generatedInstaller,
            Path.Combine(testRoot, "isolated-managed-roots"));
        var installRoot = Path.Combine(
            testRoot,
            "installed",
            "tradeplan");
        var originalMarker = Path.Combine(
            installRoot,
            "original-data.marker");
        var descendantPidPath = Path.Combine(
            Path.GetDirectoryName(
                generatedInstaller.InstallScriptPath)
                ?? throw new InvalidOperationException(
                    "Generated package root was not resolved."),
            ".georaeplan-installer-delayed-descendant.pid");
        var descendantMutationPath = Path.Combine(
            installRoot,
            ".georaeplan-installer-delayed-descendant-mutation");
        Process? supervisor = null;
        Process? descendant = null;

        try
        {
            Directory.CreateDirectory(installRoot);
            await File.WriteAllTextAsync(
                originalMarker,
                "original payload must survive");

            supervisor = StartPowerShellScript(
                generatedInstaller.InstallScriptPath,
                new Dictionary<string, string?>
                {
                    ["GEORAEPLAN_INSTALLER_TEST_SPAWN_DELAYED_DESCENDANT"] =
                        "1",
                    ["GEORAEPLAN_INSTALLER_TEST_DELAYED_DESCENDANT_DELAY_MS"] =
                        "15000",
                    ["GEORAEPLAN_INSTALLER_TEST_EXIT_AFTER_DESCENDANT_SPAWN"] =
                        "1"
                },
                "-InstallRoot",
                installRoot,
                "-NoLaunch",
                "-NoShortcuts",
                "-SuppressUi",
                "-WorkerTimeoutSeconds",
                "60");
            await WaitForFileAsync(
                descendantPidPath,
                TimeSpan.FromSeconds(20));

            var installParent = Path.GetDirectoryName(installRoot)
                ?? throw new InvalidOperationException(
                    "Install parent was not resolved.");
            var stateRoot = Assert.Single(
                Directory.EnumerateDirectories(
                    installParent,
                    ".tradeplan-update-supervisor-state-*",
                    SearchOption.TopDirectoryOnly));
            var journalPath = Path.Combine(stateRoot, "journal.json");
            await WaitForFileAsync(
                journalPath,
                TimeSpan.FromSeconds(10));
            using var journal = JsonDocument.Parse(
                await File.ReadAllTextAsync(journalPath));
            Assert.Equal(
                2,
                journal.RootElement
                    .GetProperty("FormatVersion")
                    .GetInt32());
            Assert.Equal(
                "WorkerRunning",
                journal.RootElement
                    .GetProperty("Phase")
                    .GetString());
            var workerPid = journal.RootElement
                .GetProperty("WorkerProcessId")
                .GetInt32();
            await WaitForProcessExitAsync(
                workerPid,
                TimeSpan.FromSeconds(10));

            Assert.True(
                int.TryParse(
                    (await File.ReadAllTextAsync(descendantPidPath)).Trim(),
                    out var descendantPid));
            descendant = Process.GetProcessById(descendantPid);
            descendant.Refresh();
            Assert.False(descendant.HasExited);
            Assert.False(supervisor.HasExited);

            supervisor.Kill(entireProcessTree: false);
            await supervisor.WaitForExitAsync()
                .WaitAsync(TimeSpan.FromSeconds(10));
            await descendant.WaitForExitAsync()
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(descendant.HasExited);
            descendant.Dispose();
            descendant = null;
            await Task.Delay(500);
            Assert.False(File.Exists(descendantMutationPath));

            var recoveryResult = await RunPowerShellAsync(
                generatedInstaller.InstallScriptPath,
                new Dictionary<string, string?>(),
                TimeSpan.FromSeconds(30),
                "-InstallRoot",
                installRoot,
                "-RecoveryOnly",
                "-NoLaunch",
                "-NoShortcuts",
                "-SuppressUi");
            Assert.True(
                recoveryResult.ExitCode == 0,
                recoveryResult.StdOut + Environment.NewLine +
                recoveryResult.StdErr);
            Assert.Equal(
                "original payload must survive",
                await File.ReadAllTextAsync(originalMarker));
            Assert.False(Directory.Exists(stateRoot));
            Assert.False(File.Exists(descendantMutationPath));
        }
        finally
        {
            TryTerminateProcess(descendant);
            TryTerminateProcess(supervisor);
            MakeTreeDeletable(testRoot);
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("WorkerJobName")]
    [InlineData("WorkerProcessId")]
    public async Task GeneratedInstaller_TamperedV2WorkerJournalRemainsFailClosed(
        string tamperedField)
    {
        var testRoot = NewInstallRoot();
        var generatedInstaller =
            await BuildGeneratedInstallerPackageAsync(
                FindRepositoryRoot(),
                testRoot);
        await RewriteGeneratedManagedRootsAsync(
            generatedInstaller,
            Path.Combine(testRoot, "isolated-managed-roots"));
        var installRoot = Path.Combine(
            testRoot,
            "installed",
            "tradeplan");
        var originalMarker = Path.Combine(
            installRoot,
            "original-data.marker");
        var workerPidPath =
            GetGeneratedHangWorkerPidPath(generatedInstaller);
        var mutationPath = Path.Combine(
            installRoot,
            ".georaeplan-installer-timeout-mutation");
        Process? supervisor = null;
        Process? recovery = null;

        try
        {
            Directory.CreateDirectory(installRoot);
            await File.WriteAllTextAsync(
                originalMarker,
                "original payload must survive");
            supervisor = StartPowerShellScript(
                generatedInstaller.InstallScriptPath,
                new Dictionary<string, string?>
                {
                    ["GEORAEPLAN_INSTALLER_TEST_HANG_AFTER_ROLLBACK_SNAPSHOT"] =
                        "1"
                },
                "-InstallRoot",
                installRoot,
                "-NoLaunch",
                "-NoShortcuts",
                "-SuppressUi",
                "-WorkerTimeoutSeconds",
                "60");
            await WaitForFileAsync(
                workerPidPath,
                TimeSpan.FromSeconds(20));
            await WaitForFileAsync(
                mutationPath,
                TimeSpan.FromSeconds(20));

            var installParent = Path.GetDirectoryName(installRoot)
                ?? throw new InvalidOperationException(
                    "Install parent was not resolved.");
            var stateRoot = Assert.Single(
                Directory.EnumerateDirectories(
                    installParent,
                    ".tradeplan-update-supervisor-state-*",
                    SearchOption.TopDirectoryOnly));
            var journalPath = Path.Combine(stateRoot, "journal.json");
            await WaitForFileAsync(
                journalPath,
                TimeSpan.FromSeconds(10));
            var workerPid = int.Parse(
                (await File.ReadAllTextAsync(workerPidPath)).Trim());

            supervisor.Kill(entireProcessTree: false);
            await supervisor.WaitForExitAsync()
                .WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForProcessExitAsync(
                workerPid,
                TimeSpan.FromSeconds(10));

            var journalNode = JsonNode.Parse(
                    await File.ReadAllTextAsync(journalPath))
                ?.AsObject()
                ?? throw new InvalidOperationException(
                    "Supervisor journal JSON was not resolved.");
            Assert.Equal(
                2,
                journalNode["FormatVersion"]?.GetValue<int>());
            Assert.Equal(
                "WorkerRunning",
                journalNode["Phase"]?.GetValue<string>());
            if (tamperedField == "WorkerJobName")
            {
                journalNode["WorkerJobName"] =
                    "Local\\GeoraePlan.InstallerWorker.invalid";
            }
            else
            {
                journalNode["WorkerProcessId"] = 0;
            }
            await File.WriteAllTextAsync(
                journalPath,
                journalNode.ToJsonString(
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }));

            recovery = StartPowerShellScript(
                generatedInstaller.InstallScriptPath,
                new Dictionary<string, string?>(),
                "-InstallRoot",
                installRoot,
                "-RecoveryOnly",
                "-NoLaunch",
                "-NoShortcuts",
                "-SuppressUi");
            await Task.Delay(1500);
            recovery.Refresh();
            Assert.False(recovery.HasExited);
            Assert.True(Directory.Exists(stateRoot));
            Assert.True(File.Exists(mutationPath));
            Assert.False(
                InstallRootUpdateGate.TryAcquire(
                    installRoot,
                    out var blockedByFailClosedRecovery));
            Assert.Null(blockedByFailClosedRecovery);
        }
        finally
        {
            TryTerminateProcess(recovery);
            TryTerminateProcess(supervisor);
            if (recovery is not null)
            {
                try
                {
                    await recovery.WaitForExitAsync()
                        .WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch
                {
                    // Test cleanup only.
                }
            }
            MakeTreeDeletable(testRoot);
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task GeneratedInstaller_RejectsUnauthenticatedWorkerAndCarriesBridgeAndNoLaunchContracts()
    {
        var testRoot = NewInstallRoot();
        var generatedInstaller =
            await BuildGeneratedInstallerPackageAsync(
                FindRepositoryRoot(),
                testRoot);
        await RewriteGeneratedManagedRootsAsync(
            generatedInstaller,
            Path.Combine(testRoot, "isolated-managed-roots"));
        var installRoot = Path.Combine(
            testRoot,
            "installed",
            "portable");
        var markerPath = Path.Combine(
            installRoot,
            "original-data.marker");

        try
        {
            Directory.CreateDirectory(installRoot);
            await File.WriteAllTextAsync(markerPath, "unchanged");
            var rejectedWorker = await RunPowerShellAsync(
                generatedInstaller.InstallScriptPath,
                new Dictionary<string, string?>(),
                TimeSpan.FromSeconds(15),
                "-InstallRoot",
                installRoot,
                "-WorkerMode",
                "-BootstrapperOwnsInstallRootGate",
                "-NoShortcuts",
                "-SuppressUi");
            Assert.NotEqual(0, rejectedWorker.ExitCode);
            Assert.Contains(
                "supervisor-bound start pipe identity",
                rejectedWorker.StdOut + rejectedWorker.StdErr,
                StringComparison.Ordinal);
            Assert.Equal(
                "unchanged",
                await File.ReadAllTextAsync(markerPath));

            var generatedScript =
                await File.ReadAllTextAsync(
                    generatedInstaller.InstallScriptPath);
            var elevationStart = generatedScript.IndexOf(
                "function Ensure-ElevatedIfNeeded {",
                StringComparison.Ordinal);
            var elevationEnd = generatedScript.IndexOf(
                "function Ensure-SufficientInstallSpace {",
                elevationStart,
                StringComparison.Ordinal);
            var elevation =
                generatedScript[elevationStart..elevationEnd];
            Assert.Contains(
                "'-NoLaunch'",
                elevation,
                StringComparison.Ordinal);
            Assert.Contains(
                "$argumentParts += '-LegacyBridgeCopy'",
                elevation,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "if ($NoLaunch)",
                elevation,
                StringComparison.Ordinal);

            var supervisorStart = generatedScript.IndexOf(
                "function Invoke-WorkerUnderRollbackSupervisor {",
                StringComparison.Ordinal);
            var topLevelStart = generatedScript.IndexOf(
                "$ErrorActionPreference = 'Stop'",
                supervisorStart,
                StringComparison.Ordinal);
            var supervisor =
                generatedScript[supervisorStart..topLevelStart];
            Assert.Contains(
                "$workerArgumentParts += '-LegacyBridgeCopy'",
                supervisor,
                StringComparison.Ordinal);
            Assert.Contains(
                "$script:heldInstallWorkerLeases = @()",
                supervisor,
                StringComparison.Ordinal);
            Assert.Contains(
                "GetNamedPipeClientProcessId",
                generatedScript,
                StringComparison.Ordinal);
            Assert.Contains(
                "GetNamedPipeServerProcessId",
                generatedScript,
                StringComparison.Ordinal);
            Assert.Contains(
                "JobObjectLimitKillOnJobClose",
                generatedScript,
                StringComparison.Ordinal);
            AssertInOrder(
                supervisor,
                "if (-not $worker.Start())",
                "$workerJob.AssignProcess($worker)",
                "$journal.WorkerProcessId = $worker.Id",
                "$journal.Phase = 'WorkerRunning'",
                "Write-SupervisorJournal -Journal $journal -JournalPath $journalPath",
                "Wait-AndAuthorizeWorkerStart -Pipe $workerStartPipe -Worker $worker -Token $workerStartToken",
                "$workerJob.WaitForEmpty(30000)",
                "$journal.Phase = 'CommittedCleanupPending'");
            AssertInOrder(
                supervisor,
                "$workerJob.TerminateAndWait(252, 120000)",
                "[void](Invoke-PendingSupervisorRecovery)");

            var topLevel = generatedScript[topLevelStart..];
            var normalizedTopLevel = topLevel.Replace(
                "\r\n",
                "\n",
                StringComparison.Ordinal);
            Assert.Contains(
                "if ($WorkerMode) {\n    $NoLaunch = $true",
                normalizedTopLevel,
                StringComparison.Ordinal);
            var releaseGate = topLevel.IndexOf(
                "Exit-InstallRootGates -Gates $heldInstallRootGates",
                StringComparison.Ordinal);
            var launch = topLevel.IndexOf(
                "Start-Process -FilePath $launchExecutable",
                StringComparison.Ordinal);
            Assert.True(releaseGate >= 0 && launch > releaseGate);
            Assert.Contains(
                "WindowsBuiltInRole]::Administrator",
                topLevel,
                StringComparison.Ordinal);
            Assert.Contains(
                "Get-SupervisorRecoveryInstallRoots",
                topLevel,
                StringComparison.Ordinal);
            AssertInOrder(
                topLevel,
                "$heldInstallWorkerLeases = @(",
                "Confirm-WorkerStartAuthorization",
                "$installRollbackSnapshots = @()",
                "Write-InstallLog (\"설치 시작.");
        }
        finally
        {
            MakeTreeDeletable(testRoot);
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task GeneratedInstaller_ForgedWorkerStartServerPidRejectsBeforeMutation()
    {
        var testRoot = NewInstallRoot();
        var generatedInstaller =
            await BuildGeneratedInstallerPackageAsync(
                FindRepositoryRoot(),
                testRoot);
        await RewriteGeneratedManagedRootsAsync(
            generatedInstaller,
            Path.Combine(testRoot, "isolated-managed-roots"));
        var installRoot = Path.Combine(
            testRoot,
            "installed",
            "portable");
        var markerPath = Path.Combine(
            installRoot,
            "original-data.marker");
        var preGoMutationPath = Path.Combine(
            installRoot,
            ".georaeplan-installer-delayed-descendant-mutation");
        var descendantPidPath = Path.Combine(
            Path.GetDirectoryName(
                generatedInstaller.InstallScriptPath)
                ?? throw new InvalidOperationException(
                    "Generated package root was not resolved."),
            ".georaeplan-installer-delayed-descendant.pid");

        try
        {
            Directory.CreateDirectory(installRoot);
            await File.WriteAllTextAsync(markerPath, "unchanged");
            var rejectedResult = await RunPowerShellAsync(
                generatedInstaller.InstallScriptPath,
                new Dictionary<string, string?>
                {
                    ["GEORAEPLAN_INSTALLER_TEST_WORKER_START_SERVER_PID"] =
                        int.MaxValue.ToString(),
                    ["GEORAEPLAN_INSTALLER_TEST_SPAWN_DELAYED_DESCENDANT"] =
                        "1",
                    ["GEORAEPLAN_INSTALLER_TEST_DELAYED_DESCENDANT_DELAY_MS"] =
                        "0"
                },
                TimeSpan.FromSeconds(30),
                "-InstallRoot",
                installRoot,
                "-NoLaunch",
                "-NoShortcuts",
                "-SuppressUi");

            Assert.True(
                rejectedResult.ExitCode != 0,
                rejectedResult.StdOut + Environment.NewLine +
                rejectedResult.StdErr);
            Assert.Equal(
                "unchanged",
                await File.ReadAllTextAsync(markerPath));
            Assert.False(File.Exists(preGoMutationPath));
            Assert.False(File.Exists(descendantPidPath));
            Assert.Null(
                DesktopApplication.GetInstallRecoveryStartupBlockMessage(
                    installRoot));
            Assert.True(
                InstallRootUpdateGate.TryAcquire(
                    installRoot,
                    out var reacquiredAfterRejectedWorker));
            reacquiredAfterRejectedWorker!.Dispose();
        }
        finally
        {
            MakeTreeDeletable(testRoot);
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task GeneratedInstaller_ProductionPackageRejectsTestHooksAndOmitsCapabilityMarker()
    {
        var testRoot = NewInstallRoot();
        var generatedInstaller =
            await BuildGeneratedInstallerPackageAsync(
                FindRepositoryRoot(),
                testRoot,
                enableTestHooks: false);
        await RewriteGeneratedManagedRootsAsync(
            generatedInstaller,
            Path.Combine(testRoot, "isolated-managed-roots"));
        var packageRoot = Path.GetDirectoryName(
                generatedInstaller.InstallScriptPath)
            ?? throw new InvalidOperationException(
                "Generated package root was not resolved.");
        var capabilityMarkerPath = Path.Combine(
            packageRoot,
            ".georaeplan-installer-test-capability");
        var installRoot = Path.Combine(
            testRoot,
            "installed",
            "portable");
        var originalMarker = Path.Combine(
            installRoot,
            "original-data.marker");
        var mutationPath = Path.Combine(
            installRoot,
            ".georaeplan-installer-timeout-mutation");

        try
        {
            Directory.CreateDirectory(installRoot);
            await File.WriteAllTextAsync(
                originalMarker,
                "unchanged");
            Assert.False(File.Exists(capabilityMarkerPath));
            var productionScript = (
                    await File.ReadAllTextAsync(
                        generatedInstaller.InstallScriptPath))
                .Replace(
                    "\r\n",
                    "\n",
                    StringComparison.Ordinal);
            Assert.Contains(
                "$script:InstallerTestHooksEnabled =\n    $false",
                productionScript,
                StringComparison.Ordinal);

            var result = await RunPowerShellAsync(
                generatedInstaller.InstallScriptPath,
                new Dictionary<string, string?>
                {
                    ["GEORAEPLAN_INSTALLER_TEST_HANG_AFTER_ROLLBACK_SNAPSHOT"] =
                        "1",
                    ["GEORAEPLAN_INSTALLER_TEST_CAPABILITY"] =
                        new string('A', 64)
                },
                TimeSpan.FromSeconds(20),
                "-InstallRoot",
                installRoot,
                "-NoLaunch",
                "-NoShortcuts",
                "-SuppressUi");

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(
                "production installer package",
                result.StdOut + result.StdErr,
                StringComparison.Ordinal);
            Assert.Equal(
                "unchanged",
                await File.ReadAllTextAsync(originalMarker));
            Assert.False(File.Exists(mutationPath));
            Assert.False(
                File.Exists(
                    GetGeneratedHangWorkerPidPath(
                        generatedInstaller)));
            Assert.Equal(
                InstallRecoveryStateStatus.Absent,
                InstallRecoveryStateProbe.Probe(
                    installRoot).Status);
            Assert.True(
                InstallRootUpdateGate.TryAcquire(
                    installRoot,
                    out var reacquiredAfterRejectedHook));
            reacquiredAfterRejectedHook!.Dispose();
        }
        finally
        {
            MakeTreeDeletable(testRoot);
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task GeneratedInstaller_ShortcutRepairJournalSurvivesCrashAndCleansOnlyExactLegacyLinks()
    {
        var testRoot = NewInstallRoot();
        var generatedInstaller =
            await BuildGeneratedInstallerPackageAsync(
                FindRepositoryRoot(),
                testRoot);
        var isolatedManagedRoot = Path.Combine(
            testRoot,
            "isolated-managed-roots");
        await RewriteGeneratedManagedRootsAsync(
            generatedInstaller,
            isolatedManagedRoot);
        var installRoot = Path.Combine(
            isolatedManagedRoot,
            "canonical");
        var legacyRoot = Path.Combine(
            isolatedManagedRoot,
            "legacy");
        var packageRoot = Path.GetDirectoryName(
                generatedInstaller.InstallScriptPath)
            ?? throw new InvalidOperationException(
                "Generated package root was not resolved.");
        var packageExecutable = Path.Combine(
            packageRoot,
            "App",
            generatedInstaller.AppDisplayName + ".exe");
        var shortcutTestRoot = Path.Combine(
            packageRoot,
            ".georaeplan-installer-test-shortcuts");
        var commonDesktopShortcut = Path.Combine(
            shortcutTestRoot,
            "common-desktop",
            generatedInstaller.AppDisplayName + ".lnk");
        var commonStartMenuRoot = Path.Combine(
            shortcutTestRoot,
            "common-programs",
            generatedInstaller.AppDisplayName);
        var commonApplicationShortcut = Path.Combine(
            commonStartMenuRoot,
            generatedInstaller.AppDisplayName + ".lnk");
        var commonRemoveShortcut = Path.Combine(
            commonStartMenuRoot,
            generatedInstaller.AppDisplayName + " 제거.lnk");
        var userDesktopShortcut = Path.Combine(
            shortcutTestRoot,
            "user-desktop",
            generatedInstaller.AppDisplayName + ".lnk");
        var userStartMenuRoot = Path.Combine(
            shortcutTestRoot,
            "user-programs",
            generatedInstaller.AppDisplayName);
        var userApplicationShortcut = Path.Combine(
            userStartMenuRoot,
            generatedInstaller.AppDisplayName + ".lnk");
        var userRemoveShortcut = Path.Combine(
            userStartMenuRoot,
            generatedInstaller.AppDisplayName + " 제거.lnk");

        try
        {
            File.Copy(
                typeof(Program).Assembly.Location,
                packageExecutable,
                overwrite: true);
            var productVersion =
                FileVersionInfo.GetVersionInfo(
                    packageExecutable).ProductVersion
                ?? throw new InvalidOperationException(
                    "Versioned test executable has no product version.");
            var generatedScript = await File.ReadAllTextAsync(
                generatedInstaller.InstallScriptPath);
            generatedScript = generatedScript.Replace(
                $"$ExpectedVersion = '{generatedInstaller.ExpectedVersion}'",
                $"$ExpectedVersion = '{productVersion}'",
                StringComparison.Ordinal);
            await File.WriteAllTextAsync(
                generatedInstaller.InstallScriptPath,
                generatedScript,
                new System.Text.UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: true));

            Directory.CreateDirectory(legacyRoot);
            var legacyExecutable = Path.Combine(
                legacyRoot,
                generatedInstaller.AppDisplayName + ".exe");
            var legacyUninstallScript = Path.Combine(
                legacyRoot,
                "Uninstall-GeoraePlan.ps1");
            await File.WriteAllTextAsync(
                legacyExecutable,
                "legacy executable");
            await File.WriteAllTextAsync(
                legacyUninstallScript,
                "# legacy uninstall");
            await File.WriteAllTextAsync(
                Path.Combine(legacyRoot, "legacy-only.marker"),
                "legacy installation");

            var unrelatedRoot = Path.Combine(
                testRoot,
                "unrelated-shortcut-target");
            Directory.CreateDirectory(unrelatedRoot);
            var unrelatedExecutable = Path.Combine(
                unrelatedRoot,
                generatedInstaller.AppDisplayName + ".exe");
            await File.WriteAllTextAsync(
                unrelatedExecutable,
                "unrelated application");
            await CreateShellShortcutAsync(
                testRoot,
                userDesktopShortcut,
                unrelatedExecutable,
                unrelatedRoot);
            await CreateShellShortcutAsync(
                testRoot,
                userApplicationShortcut,
                legacyExecutable,
                legacyRoot);
            await CreateShellShortcutAsync(
                testRoot,
                userRemoveShortcut,
                "powershell.exe",
                legacyRoot,
                $"-ExecutionPolicy Bypass -File \"{legacyUninstallScript}\"");

            var isolatedShortcutEnvironment =
                new Dictionary<string, string?>
                {
                    ["GEORAEPLAN_INSTALLER_TEST_USE_ISOLATED_SHORTCUT_ROOTS"] =
                        "1",
                    ["GEORAEPLAN_INSTALLER_TEST_FORCE_PROTECTED_SHORTCUT_SCOPE"] =
                        "1"
                };
            var crashEnvironment =
                new Dictionary<string, string?>(
                    isolatedShortcutEnvironment)
                {
                    ["GEORAEPLAN_INSTALLER_TEST_CRASH_AFTER_SHORTCUT_REPAIR_PENDING"] =
                        "1"
                };
            var crashResult = await RunPowerShellAsync(
                generatedInstaller.InstallScriptPath,
                crashEnvironment,
                TimeSpan.FromSeconds(45),
                "-InstallRoot",
                installRoot,
                "-NoLaunch",
                "-SuppressUi");
            Assert.NotEqual(0, crashResult.ExitCode);
            var pendingAfterCrash =
                InstallRecoveryStateProbe.Probe(installRoot);
            Assert.Equal(
                InstallRecoveryStateStatus.Present,
                pendingAfterCrash.Status);
            var journalPath = Path.Combine(
                pendingAfterCrash.StatePath,
                "journal.json");
            Assert.Contains(
                "\"ShortcutRepairPending\"",
                await File.ReadAllTextAsync(journalPath),
                StringComparison.Ordinal);
            Assert.False(Directory.Exists(legacyRoot));
            Assert.False(File.Exists(commonDesktopShortcut));

            var journalJson = JsonNode.Parse(
                    await File.ReadAllTextAsync(journalPath))
                ?? throw new InvalidOperationException(
                    "Shortcut repair journal JSON was not parsed.");
            var originalOriginSid =
                journalJson["OriginUserSid"]?.GetValue<string>()
                ?? throw new InvalidOperationException(
                    "Origin SID was not recorded.");
            journalJson["OriginUserSid"] = "S-1-0-0";
            await File.WriteAllTextAsync(
                journalPath,
                journalJson.ToJsonString());
            var mismatchedRecovery = await RunPowerShellAsync(
                generatedInstaller.InstallScriptPath,
                isolatedShortcutEnvironment,
                TimeSpan.FromSeconds(30),
                "-InstallRoot",
                installRoot,
                "-RecoveryOnly",
                "-NoLaunch",
                "-SuppressUi");
            Assert.NotEqual(0, mismatchedRecovery.ExitCode);
            Assert.Contains(
                "Windows 사용자",
                mismatchedRecovery.StdOut +
                mismatchedRecovery.StdErr,
                StringComparison.Ordinal);
            Assert.Equal(
                InstallRecoveryStateStatus.Present,
                InstallRecoveryStateProbe.Probe(installRoot).Status);
            Assert.False(File.Exists(commonDesktopShortcut));
            journalJson["OriginUserSid"] = originalOriginSid;
            await File.WriteAllTextAsync(
                journalPath,
                journalJson.ToJsonString());

            var noShortcutsRecovery = await RunPowerShellAsync(
                generatedInstaller.InstallScriptPath,
                isolatedShortcutEnvironment,
                TimeSpan.FromSeconds(30),
                "-InstallRoot",
                installRoot,
                "-RecoveryOnly",
                "-NoLaunch",
                "-NoShortcuts",
                "-SuppressUi");
            Assert.NotEqual(0, noShortcutsRecovery.ExitCode);
            Assert.Contains(
                "NoShortcuts",
                noShortcutsRecovery.StdOut +
                noShortcutsRecovery.StdErr,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                InstallRecoveryStateStatus.Present,
                InstallRecoveryStateProbe.Probe(installRoot).Status);

            var failAfterCommonEnvironment =
                new Dictionary<string, string?>(
                    isolatedShortcutEnvironment)
                {
                    ["GEORAEPLAN_INSTALLER_TEST_FAIL_SHORTCUTS_AFTER_COMMON"] =
                        "1"
                };
            var partialRepair = await RunPowerShellAsync(
                generatedInstaller.InstallScriptPath,
                failAfterCommonEnvironment,
                TimeSpan.FromSeconds(30),
                "-InstallRoot",
                installRoot,
                "-RecoveryOnly",
                "-NoLaunch",
                "-SuppressUi");
            Assert.NotEqual(0, partialRepair.ExitCode);
            Assert.True(File.Exists(commonDesktopShortcut));
            Assert.True(File.Exists(commonApplicationShortcut));
            Assert.True(File.Exists(commonRemoveShortcut));
            Assert.True(File.Exists(userDesktopShortcut));
            Assert.True(File.Exists(userApplicationShortcut));
            Assert.True(File.Exists(userRemoveShortcut));
            Assert.Equal(
                InstallRecoveryStateStatus.Present,
                InstallRecoveryStateProbe.Probe(installRoot).Status);

            var completedRepair = await RunPowerShellAsync(
                generatedInstaller.InstallScriptPath,
                isolatedShortcutEnvironment,
                TimeSpan.FromSeconds(30),
                "-InstallRoot",
                installRoot,
                "-RecoveryOnly",
                "-NoLaunch",
                "-SuppressUi");
            Assert.True(
                completedRepair.ExitCode == 0,
                completedRepair.StdOut + Environment.NewLine +
                completedRepair.StdErr);
            Assert.Equal(
                InstallRecoveryStateStatus.Absent,
                InstallRecoveryStateProbe.Probe(installRoot).Status);
            Assert.True(File.Exists(commonDesktopShortcut));
            Assert.True(File.Exists(commonApplicationShortcut));
            Assert.True(File.Exists(commonRemoveShortcut));
            Assert.True(
                File.Exists(userDesktopShortcut),
                "Unrelated same-name shortcut must be preserved.");
            Assert.False(File.Exists(userApplicationShortcut));
            Assert.False(File.Exists(userRemoveShortcut));
            Assert.False(Directory.Exists(userStartMenuRoot));

            Directory.CreateDirectory(legacyRoot);
            await File.WriteAllTextAsync(
                legacyExecutable,
                "legacy bridge executable before update");
            await File.WriteAllTextAsync(
                legacyUninstallScript,
                "# stale bridge uninstall");
            await File.WriteAllTextAsync(
                Path.Combine(legacyRoot, "bridge-old-only.marker"),
                "must be mirrored away");
            await CreateShellShortcutAsync(
                testRoot,
                userDesktopShortcut,
                legacyExecutable,
                legacyRoot);
            await CreateShellShortcutAsync(
                testRoot,
                userApplicationShortcut,
                legacyExecutable,
                legacyRoot);
            await CreateShellShortcutAsync(
                testRoot,
                userRemoveShortcut,
                "powershell.exe",
                legacyRoot,
                $"-ExecutionPolicy Bypass -File \"{legacyUninstallScript}\" -InstallRoot \"{legacyRoot}\"");

            var bridgeInstall = await RunPowerShellAsync(
                generatedInstaller.InstallScriptPath,
                isolatedShortcutEnvironment,
                TimeSpan.FromSeconds(45),
                "-InstallRoot",
                legacyRoot,
                "-LegacyBridgeCopy",
                "-NoLaunch",
                "-SuppressUi");
            Assert.True(
                bridgeInstall.ExitCode == 0,
                bridgeInstall.StdOut + Environment.NewLine +
                bridgeInstall.StdErr);
            Assert.Equal(
                InstallRecoveryStateStatus.Absent,
                InstallRecoveryStateProbe.Probe(installRoot).Status);
            Assert.True(File.Exists(legacyExecutable));
            Assert.False(File.Exists(legacyUninstallScript));
            Assert.False(File.Exists(Path.Combine(
                legacyRoot,
                "bridge-old-only.marker")));
            Assert.True(File.Exists(userDesktopShortcut));
            Assert.True(File.Exists(userApplicationShortcut));
            Assert.False(File.Exists(userRemoveShortcut));
            Assert.True(File.Exists(commonDesktopShortcut));
            Assert.True(File.Exists(commonApplicationShortcut));
            Assert.True(File.Exists(commonRemoveShortcut));
        }
        finally
        {
            MakeTreeDeletable(testRoot);
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task GeneratedInstaller_RejectsReparseShortcutRootBeforePendingJournalOrShortcutMutation()
    {
        var testRoot = NewInstallRoot();
        var generatedInstaller =
            await BuildGeneratedInstallerPackageAsync(
                FindRepositoryRoot(),
                testRoot);
        var isolatedManagedRoot = Path.Combine(
            testRoot,
            "isolated-managed-roots");
        await RewriteGeneratedManagedRootsAsync(
            generatedInstaller,
            isolatedManagedRoot);
        var installRoot = Path.Combine(
            isolatedManagedRoot,
            "canonical");
        var legacyRoot = Path.Combine(
            isolatedManagedRoot,
            "legacy");
        var packageRoot = Path.GetDirectoryName(
                generatedInstaller.InstallScriptPath)
            ?? throw new InvalidOperationException(
                "Generated package root was not resolved.");
        var packageExecutable = Path.Combine(
            packageRoot,
            "App",
            generatedInstaller.AppDisplayName + ".exe");
        var shortcutTestRoot = Path.Combine(
            packageRoot,
            ".georaeplan-installer-test-shortcuts");
        var userProgramsJunction = Path.Combine(
            shortcutTestRoot,
            "user-programs");
        var junctionTarget = Path.Combine(
            testRoot,
            "shortcut-junction-target");
        var commonDesktopShortcut = Path.Combine(
            shortcutTestRoot,
            "common-desktop",
            generatedInstaller.AppDisplayName + ".lnk");
        var commonStartMenuRoot = Path.Combine(
            shortcutTestRoot,
            "common-programs",
            generatedInstaller.AppDisplayName);
        var commonApplicationShortcut = Path.Combine(
            commonStartMenuRoot,
            generatedInstaller.AppDisplayName + ".lnk");
        var commonRemoveShortcut = Path.Combine(
            commonStartMenuRoot,
            generatedInstaller.AppDisplayName + " ?쒓굅.lnk");
        var canonicalMarker = Path.Combine(
            installRoot,
            "canonical-before.marker");
        var legacyMarker = Path.Combine(
            legacyRoot,
            "legacy-before.marker");

        try
        {
            File.Copy(
                typeof(Program).Assembly.Location,
                packageExecutable,
                overwrite: true);
            var productVersion =
                FileVersionInfo.GetVersionInfo(
                    packageExecutable).ProductVersion
                ?? throw new InvalidOperationException(
                    "Versioned test executable has no product version.");
            var generatedScript = await File.ReadAllTextAsync(
                generatedInstaller.InstallScriptPath);
            generatedScript = generatedScript.Replace(
                $"$ExpectedVersion = '{generatedInstaller.ExpectedVersion}'",
                $"$ExpectedVersion = '{productVersion}'",
                StringComparison.Ordinal);
            await File.WriteAllTextAsync(
                generatedInstaller.InstallScriptPath,
                generatedScript,
                new System.Text.UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: true));

            Directory.CreateDirectory(installRoot);
            Directory.CreateDirectory(legacyRoot);
            await File.WriteAllTextAsync(
                canonicalMarker,
                "canonical before install");
            await File.WriteAllTextAsync(
                legacyMarker,
                "legacy before install");
            Directory.CreateDirectory(shortcutTestRoot);
            Directory.CreateDirectory(junctionTarget);
            await File.WriteAllTextAsync(
                Path.Combine(junctionTarget, "must-remain.marker"),
                "junction target unchanged");
            if (!TryCreateDirectoryJunction(
                    userProgramsJunction,
                    junctionTarget))
            {
                return;
            }

            var rejected = await RunPowerShellAsync(
                generatedInstaller.InstallScriptPath,
                new Dictionary<string, string?>
                {
                    ["GEORAEPLAN_INSTALLER_TEST_USE_ISOLATED_SHORTCUT_ROOTS"] =
                        "1",
                    ["GEORAEPLAN_INSTALLER_TEST_FORCE_PROTECTED_SHORTCUT_SCOPE"] =
                        "1"
                },
                TimeSpan.FromSeconds(45),
                "-InstallRoot",
                installRoot,
                "-NoLaunch",
                "-SuppressUi");

            Assert.NotEqual(0, rejected.ExitCode);
            Assert.Contains(
                "reparse point",
                rejected.StdOut + rejected.StdErr,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                InstallRecoveryStateStatus.Absent,
                InstallRecoveryStateProbe.Probe(installRoot).Status);
            Assert.Equal(
                "canonical before install",
                await File.ReadAllTextAsync(canonicalMarker));
            Assert.Equal(
                "legacy before install",
                await File.ReadAllTextAsync(legacyMarker));
            Assert.False(File.Exists(commonDesktopShortcut));
            Assert.False(File.Exists(commonApplicationShortcut));
            Assert.False(File.Exists(commonRemoveShortcut));
            Assert.Equal(
                "junction target unchanged",
                await File.ReadAllTextAsync(
                    Path.Combine(
                        junctionTarget,
                        "must-remain.marker")));
            Assert.Empty(
                Directory.EnumerateFiles(
                    junctionTarget,
                    "*.lnk",
                    SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(userProgramsJunction))
                Directory.Delete(userProgramsJunction, recursive: false);
            MakeTreeDeletable(testRoot);
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task GeneratedInstaller_OriginSidGuardUsesVerifiedProcessHandleAndPureComparison()
    {
        var testRoot = NewInstallRoot();
        try
        {
            var generatedInstaller =
                await BuildGeneratedInstallerPackageAsync(
                    FindRepositoryRoot(),
                    testRoot);
            var generatedScript = await File.ReadAllTextAsync(
                generatedInstaller.InstallScriptPath);

            Assert.DoesNotContain(
                "[string]$OriginUserSid",
                generatedScript,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "GEORAEPLAN_INSTALLER_TEST_FORCE_ORIGIN_SID",
                generatedScript,
                StringComparison.Ordinal);
            Assert.Contains(
                "[GeoraePlanInstaller.ProcessTokenIdentity]::GetUserSid(",
                generatedScript,
                StringComparison.Ordinal);

            var identityFunction = generatedScript.IndexOf(
                "function Get-VerifiedOriginProcessUserSid {",
                StringComparison.Ordinal);
            var processPathCheck = generatedScript.IndexOf(
                "$actualProcessPath",
                identityFunction,
                StringComparison.Ordinal);
            var processStartCheck = generatedScript.IndexOf(
                "$actualStartTimeUtcTicks",
                processPathCheck,
                StringComparison.Ordinal);
            var handleTokenRead = generatedScript.IndexOf(
                "[GeoraePlanInstaller.ProcessTokenIdentity]::GetUserSid(",
                processStartCheck,
                StringComparison.Ordinal);
            var comparisonFunction = generatedScript.IndexOf(
                "function Test-SameInstallerUserSid {",
                handleTokenRead,
                StringComparison.Ordinal);
            var mismatchGuard = generatedScript.IndexOf(
                "if (-not (Test-SameInstallerUserSid",
                comparisonFunction,
                StringComparison.Ordinal);
            var legacyRootConstruction = generatedScript.IndexOf(
                "$LegacyUserRoot = Join-Path $env:LOCALAPPDATA",
                mismatchGuard,
                StringComparison.Ordinal);
            var journalBindingFunction = generatedScript.IndexOf(
                "function Assert-SupervisorJournalBinding {",
                legacyRootConstruction,
                StringComparison.Ordinal);
            var journalSidComparison = generatedScript.IndexOf(
                "$journalOriginUserSid",
                journalBindingFunction,
                StringComparison.Ordinal);
            var taggedMismatch = generatedScript.IndexOf(
                "New-OriginUserMismatchException",
                journalSidComparison,
                StringComparison.Ordinal);
            var recoveryFunction = generatedScript.IndexOf(
                "function Invoke-PendingSupervisorRecoveryForRoot {",
                taggedMismatch,
                StringComparison.Ordinal);
            var boundJournalRead = generatedScript.IndexOf(
                "$journal = Read-SupervisorJournal",
                recoveryFunction,
                StringComparison.Ordinal);
            var workerBarrier = generatedScript.IndexOf(
                "Complete-WorkerJobBarrierForJournal -Journal $journal",
                boundJournalRead,
                StringComparison.Ordinal);
            var journalFactory = generatedScript.IndexOf(
                "function New-SupervisorJournal {",
                workerBarrier,
                StringComparison.Ordinal);
            var journalOriginBinding = generatedScript.IndexOf(
                "OriginUserSid = $script:OriginUserSid",
                journalFactory,
                StringComparison.Ordinal);
            var supervisorFunction = generatedScript.IndexOf(
                "function Invoke-WorkerUnderRollbackSupervisor {",
                journalOriginBinding,
                StringComparison.Ordinal);
            var immediateNonzeroHandler = generatedScript.IndexOf(
                "if (Test-OriginUserMismatchException",
                supervisorFunction,
                StringComparison.Ordinal);
            var mismatchReturn = generatedScript.IndexOf(
                "return 3",
                immediateNonzeroHandler,
                StringComparison.Ordinal);
            Assert.True(identityFunction >= 0);
            Assert.True(processPathCheck > identityFunction);
            Assert.True(processStartCheck > processPathCheck);
            Assert.True(handleTokenRead > processStartCheck);
            Assert.True(comparisonFunction > handleTokenRead);
            Assert.True(mismatchGuard > comparisonFunction);
            Assert.True(legacyRootConstruction > mismatchGuard);
            Assert.True(journalBindingFunction > legacyRootConstruction);
            Assert.True(journalSidComparison > journalBindingFunction);
            Assert.True(taggedMismatch > journalSidComparison);
            Assert.True(recoveryFunction > taggedMismatch);
            Assert.True(boundJournalRead > recoveryFunction);
            Assert.True(workerBarrier > boundJournalRead);
            Assert.True(journalFactory > workerBarrier);
            Assert.True(journalOriginBinding > journalFactory);
            Assert.True(supervisorFunction > journalOriginBinding);
            Assert.True(immediateNonzeroHandler > supervisorFunction);
            Assert.True(mismatchReturn > immediateNonzeroHandler);

            var helperEnd = mismatchGuard;
            var helperScriptPath = Path.Combine(
                testRoot,
                "test-same-installer-user-sid.ps1");
            await File.WriteAllTextAsync(
                helperScriptPath,
                generatedScript[
                    comparisonFunction..helperEnd] +
                """

                if (-not (Test-SameInstallerUserSid `
                    -OriginSid 'S-1-5-21-100' `
                    -CurrentSid 'S-1-5-21-100')) {
                    exit 10
                }
                if (Test-SameInstallerUserSid `
                    -OriginSid 'S-1-5-21-100' `
                    -CurrentSid 'S-1-5-21-101') {
                    exit 11
                }
                exit 0
                """,
                new System.Text.UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: true));
            var helperResult = await RunPowerShellAsync(
                helperScriptPath,
                new Dictionary<string, string?>(),
                TimeSpan.FromSeconds(15));
            Assert.True(
                helperResult.ExitCode == 0,
                helperResult.StdOut + Environment.NewLine +
                helperResult.StdErr);
        }
        finally
        {
            MakeTreeDeletable(testRoot);
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void Updater_ProbesAndRecoversGeneratedStateBeforeVersionSkipAndAfterInstall()
    {
        var updaterSource = File.ReadAllText(
            Path.Combine(
                FindRepositoryRoot(),
                "Updater",
                "거래플랜.Updater",
                "Program.cs"));
        var executeStart = updaterSource.IndexOf(
            "private static async Task ExecuteAsync(",
            StringComparison.Ordinal);
        var executeInstallStart = updaterSource.IndexOf(
            "internal static async Task ExecuteInstallWithRollbackAsync(",
            executeStart,
            StringComparison.Ordinal);
        Assert.True(executeStart >= 0);
        Assert.True(executeInstallStart > executeStart);
        var execute = updaterSource[
            executeStart..executeInstallStart];
        AssertInOrder(
            execute,
            "RequireGeneratedInstallRecoveryProbe(options.InstallRoot)",
            "if (generatedRecoveryProbe.Status == InstallRecoveryStateStatus.Absent)",
            "InstalledVersionState.AtLeastRequested",
            "RECOVERED-SKIP",
            "ExecuteInstallWithRollbackAsync(");

        var recoveryMethodStart = updaterSource.IndexOf(
            "internal static async Task<InstalledVersionState>",
            executeInstallStart,
            StringComparison.Ordinal);
        Assert.True(recoveryMethodStart > executeInstallStart);
        var executeInstall = updaterSource[
            executeInstallStart..recoveryMethodStart];
        AssertInOrder(
            executeInstall,
            "await RunInstallScriptAsync(",
            "RequireGeneratedInstallRecoveryProbe(options.InstallRoot)",
            "InstallRecoveryStateStatus.Present",
            "RecoverGeneratedInstallStateBeforeVersionDecisionAsync(",
            "EnsureGeneratedInstallRecoveryAbsent(options.InstallRoot)",
            "ValidateInstalledApplication(options)");
    }

    [Fact]
    public async Task GeneratedInstaller_TestEnabledPackageRejectsHooksForProtectedRootBeforeCapabilityUse()
    {
        var testRoot = NewInstallRoot();
        var generatedInstaller =
            await BuildGeneratedInstallerPackageAsync(
                FindRepositoryRoot(),
                testRoot,
                enableTestHooks: true);
        var packageRoot = Path.GetDirectoryName(
                generatedInstaller.InstallScriptPath)
            ?? throw new InvalidOperationException(
                "Generated package root was not resolved.");
        var capabilityMarkerPath = Path.Combine(
            packageRoot,
            ".georaeplan-installer-test-capability");
        var probeScriptPath = Path.Combine(
            testRoot,
            "protected-test-hook-probe.ps1");

        try
        {
            Assert.True(File.Exists(capabilityMarkerPath));
            Assert.Matches(
                "^[A-F0-9]{64}$",
                await File.ReadAllTextAsync(
                    capabilityMarkerPath));
            var generatedScript = await File.ReadAllTextAsync(
                generatedInstaller.InstallScriptPath);
            var normalizedGeneratedScript = generatedScript.Replace(
                "\r\n",
                "\n",
                StringComparison.Ordinal);
            Assert.Contains(
                "$script:InstallerTestHooksEnabled =\n    $true",
                normalizedGeneratedScript,
                StringComparison.Ordinal);
            var gateStart = generatedScript.IndexOf(
                "function Get-ActiveInstallerTestHooks {",
                StringComparison.Ordinal);
            var gateEnd = generatedScript.IndexOf(
                "function Get-InstallRootGateMutexName {",
                gateStart,
                StringComparison.Ordinal);
            Assert.True(gateStart >= 0);
            Assert.True(gateEnd > gateStart);
            var gateFunctions = generatedScript[
                gateStart..gateEnd];
            AssertInOrder(
                gateFunctions,
                "if (Test-ProtectedInstallRoot -Path $InstallRoot)",
                "if (-not $script:InstallerTestHooksEnabled)",
                "$capabilityMarkerPath = Join-Path",
                "$env:GEORAEPLAN_INSTALLER_TEST_CAPABILITY");

            var newLine = Environment.NewLine;
            var probeScript =
                "$ErrorActionPreference = 'Stop'" + newLine +
                "$script:InstallerTestHooksEnabled = $true" + newLine +
                "$InstallRoot = 'D:\\protected-root-probe'" + newLine +
                "function Test-ProtectedInstallRoot { param([string]$Path) return $true }" + newLine +
                gateFunctions + newLine +
                "$env:GEORAEPLAN_INSTALLER_TEST_HANG_AFTER_ROLLBACK_SNAPSHOT = '1'" + newLine +
                "try {" + newLine +
                "    Assert-InstallerTestHooksAllowed" + newLine +
                "    throw 'protected test hook was accepted'" + newLine +
                "}" + newLine +
                "catch {" + newLine +
                "    if ($_.Exception.Message -notlike '보호된 설치 경로에서는*') { throw }" + newLine +
                "    Write-Output 'PROTECTED_TEST_HOOK_REJECTED'" + newLine +
                "}";
            await File.WriteAllTextAsync(
                probeScriptPath,
                probeScript,
                new System.Text.UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: true));

            var result = await RunPowerShellAsync(
                probeScriptPath,
                new Dictionary<string, string?>(),
                TimeSpan.FromSeconds(20));
            Assert.True(
                result.ExitCode == 0,
                result.StdOut + Environment.NewLine +
                result.StdErr);
            Assert.Contains(
                "PROTECTED_TEST_HOOK_REJECTED",
                result.StdOut,
                StringComparison.Ordinal);
        }
        finally
        {
            MakeTreeDeletable(testRoot);
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task GeneratedInstaller_CanonicalRecoverySelectsLegacyPendingStateBeforeMutation()
    {
        var testRoot = NewInstallRoot();
        var generatedInstaller =
            await BuildGeneratedInstallerPackageAsync(
                FindRepositoryRoot(),
                testRoot);
        var isolatedLocalAppData = Path.Combine(
            testRoot,
            "isolated-localappdata");
        var legacyRoot = Path.Combine(
            isolatedLocalAppData,
            "Programs",
            generatedInstaller.AppDisplayName);
        var canonicalRoot = Path.Combine(
            testRoot,
            "managed-roots",
            "tradeplan");
        var legacyMarker = Path.Combine(
            legacyRoot,
            "legacy-original.marker");
        var canonicalMarker = Path.Combine(
            canonicalRoot,
            "canonical-original.marker");
        var workerPidPath =
            GetGeneratedHangWorkerPidPath(generatedInstaller);
        var legacyMutation = Path.Combine(
            legacyRoot,
            ".georaeplan-installer-timeout-mutation");
        Process? supervisor = null;

        try
        {
            Directory.CreateDirectory(legacyRoot);
            Directory.CreateDirectory(canonicalRoot);
            await File.WriteAllTextAsync(
                legacyMarker,
                "legacy original remains");
            await File.WriteAllTextAsync(
                canonicalMarker,
                "canonical must not be touched by legacy recovery");

            var scriptText =
                await File.ReadAllTextAsync(
                    generatedInstaller.InstallScriptPath);
            const string canonicalAssignment =
                "$CanonicalInstallRoot = Join-Path $programFilesRoot 'tradeplan'";
            var legacyCanonicalAssignment =
                $"$CanonicalInstallRoot = '{legacyRoot.Replace("'", "''")}'";
            var legacyUserRootAssignment =
                $"$LegacyUserRoot = Join-Path $env:LOCALAPPDATA 'Programs\\{generatedInstaller.AppDisplayName}'";
            var isolatedLegacyUserRootAssignment =
                $"$LegacyUserRoot = '{legacyRoot.Replace("'", "''")}'";
            Assert.Contains(
                canonicalAssignment,
                scriptText,
                StringComparison.Ordinal);
            Assert.Contains(
                legacyUserRootAssignment,
                scriptText,
                StringComparison.Ordinal);
            scriptText = scriptText.Replace(
                canonicalAssignment,
                legacyCanonicalAssignment,
                StringComparison.Ordinal);
            scriptText = scriptText.Replace(
                legacyUserRootAssignment,
                isolatedLegacyUserRootAssignment,
                StringComparison.Ordinal);
            await File.WriteAllTextAsync(
                generatedInstaller.InstallScriptPath,
                scriptText,
                new System.Text.UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: true));

            supervisor = StartPowerShellScript(
                generatedInstaller.InstallScriptPath,
                new Dictionary<string, string?>
                {
                    ["GEORAEPLAN_INSTALLER_TEST_HANG_AFTER_ROLLBACK_SNAPSHOT"] =
                        "1"
                },
                "-InstallRoot",
                legacyRoot,
                "-NoLaunch",
                "-NoShortcuts",
                "-SuppressUi",
                "-WorkerTimeoutSeconds",
                "60");
            await WaitForFileAsync(
                workerPidPath,
                TimeSpan.FromSeconds(20));
            await WaitForFileAsync(
                legacyMutation,
                TimeSpan.FromSeconds(20));
            Assert.True(
                int.TryParse(
                    (await File.ReadAllTextAsync(workerPidPath)).Trim(),
                    out var workerPid));

            supervisor.Kill(entireProcessTree: true);
            await supervisor.WaitForExitAsync()
                .WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForProcessExitAsync(
                workerPid,
                TimeSpan.FromSeconds(10));

            scriptText =
                await File.ReadAllTextAsync(
                    generatedInstaller.InstallScriptPath);
            var managedCanonicalAssignment =
                $"$CanonicalInstallRoot = '{canonicalRoot.Replace("'", "''")}'";
            Assert.Contains(
                legacyCanonicalAssignment,
                scriptText,
                StringComparison.Ordinal);
            scriptText = scriptText.Replace(
                legacyCanonicalAssignment,
                managedCanonicalAssignment,
                StringComparison.Ordinal);
            await File.WriteAllTextAsync(
                generatedInstaller.InstallScriptPath,
                scriptText,
                new System.Text.UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: true));

            var recoveryResult = await RunPowerShellAsync(
                generatedInstaller.InstallScriptPath,
                new Dictionary<string, string?>(),
                TimeSpan.FromSeconds(30),
                "-InstallRoot",
                canonicalRoot,
                "-RecoveryOnly",
                "-NoLaunch",
                "-NoShortcuts",
                "-SuppressUi");
            Assert.True(
                recoveryResult.ExitCode == 0,
                recoveryResult.StdOut + Environment.NewLine +
                recoveryResult.StdErr);
            Assert.Equal(
                "legacy original remains",
                await File.ReadAllTextAsync(legacyMarker));
            Assert.Equal(
                "canonical must not be touched by legacy recovery",
                await File.ReadAllTextAsync(canonicalMarker));
            Assert.False(File.Exists(legacyMutation));
            Assert.Equal(
                InstallRecoveryStateStatus.Absent,
                InstallRecoveryStateProbe.Probe(legacyRoot).Status);
            Assert.Equal(
                InstallRecoveryStateStatus.Absent,
                InstallRecoveryStateProbe.Probe(canonicalRoot).Status);
        }
        finally
        {
            TryTerminateProcess(supervisor);
            MakeTreeDeletable(testRoot);
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task GeneratedInstaller_CustomInstallPreservesRealLegacyRootAndUsesOnlyCustomGate()
    {
        var testRoot = NewInstallRoot();
        var generatedInstaller =
            await BuildGeneratedInstallerPackageAsync(
                FindRepositoryRoot(),
                testRoot);
        var installRoot = Path.Combine(
            testRoot,
            "portable",
            "tradeplan");
        var localAppData = Path.Combine(
            testRoot,
            "isolated-localappdata");
        var legacyRoot = Path.Combine(
            localAppData,
            "Programs",
            generatedInstaller.AppDisplayName);
        var legacyMarker = Path.Combine(
            legacyRoot,
            "original-v1-data.marker");

        try
        {
            Directory.CreateDirectory(legacyRoot);
            await File.WriteAllTextAsync(
                legacyMarker,
                "real legacy payload remains");

            var packageRoot =
                Path.GetDirectoryName(
                    generatedInstaller.InstallScriptPath)
                ?? throw new InvalidOperationException(
                    "Generated package root was not resolved.");
            var packageExecutable = Path.Combine(
                packageRoot,
                "App",
                generatedInstaller.AppDisplayName + ".exe");
            File.Copy(
                typeof(Program).Assembly.Location,
                packageExecutable,
                overwrite: true);
            var productVersion =
                FileVersionInfo.GetVersionInfo(
                    packageExecutable).ProductVersion
                ?? throw new InvalidOperationException(
                    "Versioned test executable has no product version.");
            var scriptText =
                await File.ReadAllTextAsync(
                    generatedInstaller.InstallScriptPath);
            var legacyUserRootAssignment =
                $"$LegacyUserRoot = Join-Path $env:LOCALAPPDATA 'Programs\\{generatedInstaller.AppDisplayName}'";
            var isolatedLegacyUserRootAssignment =
                $"$LegacyUserRoot = '{legacyRoot.Replace("'", "''")}'";
            Assert.Contains(
                legacyUserRootAssignment,
                scriptText,
                StringComparison.Ordinal);
            scriptText = scriptText.Replace(
                legacyUserRootAssignment,
                isolatedLegacyUserRootAssignment,
                StringComparison.Ordinal);
            scriptText = scriptText.Replace(
                $"$ExpectedVersion = '{generatedInstaller.ExpectedVersion}'",
                $"$ExpectedVersion = '{productVersion}'",
                StringComparison.Ordinal);
            await File.WriteAllTextAsync(
                generatedInstaller.InstallScriptPath,
                scriptText,
                new System.Text.UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: true));

            Assert.True(
                InstallRootUpdateGate.TryAcquire(
                    legacyRoot,
                    out var legacyAppGate));
            try
            {
                var installResult = await RunPowerShellAsync(
                    generatedInstaller.InstallScriptPath,
                    new Dictionary<string, string?>(),
                    TimeSpan.FromSeconds(30),
                    "-InstallRoot",
                    installRoot,
                    "-NoLaunch",
                    "-NoShortcuts",
                    "-SuppressUi");
                Assert.True(
                    installResult.ExitCode == 0,
                    installResult.StdOut + Environment.NewLine +
                    installResult.StdErr);
            }
            finally
            {
                legacyAppGate!.Dispose();
            }

            Assert.Equal(
                "real legacy payload remains",
                await File.ReadAllTextAsync(legacyMarker));
            Assert.True(
                File.Exists(
                    Path.Combine(
                        installRoot,
                        generatedInstaller.AppDisplayName + ".exe")));
            Assert.Empty(
                Directory.EnumerateDirectories(
                    Path.GetDirectoryName(installRoot)!,
                    ".tradeplan-update-supervisor-state-*",
                    SearchOption.TopDirectoryOnly));
        }
        finally
        {
            MakeTreeDeletable(testRoot);
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task GeneratedInstaller_RejectsJunctionAncestorBeforeMutation()
    {
        var testRoot = NewInstallRoot();
        var generatedInstaller =
            await BuildGeneratedInstallerPackageAsync(
                FindRepositoryRoot(),
                testRoot);
        var physicalRoot = Path.Combine(
            testRoot,
            "physical-target");
        var junctionRoot = Path.Combine(
            testRoot,
            "junction-alias");

        try
        {
            Directory.CreateDirectory(physicalRoot);
            if (!TryCreateDirectoryJunction(
                    junctionRoot,
                    physicalRoot))
            {
                return;
            }

            var isolatedCanonicalRoot = Path.Combine(
                testRoot,
                "managed-roots",
                "canonical");
            var isolatedLegacyRoot = Path.Combine(
                testRoot,
                "managed-roots",
                "legacy");
            var scriptText =
                await File.ReadAllTextAsync(
                    generatedInstaller.InstallScriptPath);
            const string canonicalAssignment =
                "$CanonicalInstallRoot = Join-Path $programFilesRoot 'tradeplan'";
            var legacyAssignment =
                $"$LegacyUserRoot = Join-Path $env:LOCALAPPDATA 'Programs\\{generatedInstaller.AppDisplayName}'";
            Assert.Contains(
                canonicalAssignment,
                scriptText,
                StringComparison.Ordinal);
            Assert.Contains(
                legacyAssignment,
                scriptText,
                StringComparison.Ordinal);
            scriptText = scriptText
                .Replace(
                    canonicalAssignment,
                    $"$CanonicalInstallRoot = '{isolatedCanonicalRoot.Replace("'", "''")}'",
                    StringComparison.Ordinal)
                .Replace(
                    legacyAssignment,
                    $"$LegacyUserRoot = '{isolatedLegacyRoot.Replace("'", "''")}'",
                    StringComparison.Ordinal);
            await File.WriteAllTextAsync(
                generatedInstaller.InstallScriptPath,
                scriptText,
                new System.Text.UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: true));

            var requestedRoot = Path.Combine(
                junctionRoot,
                "missing-install");
            var result = await RunPowerShellAsync(
                generatedInstaller.InstallScriptPath,
                new Dictionary<string, string?>(),
                TimeSpan.FromSeconds(20),
                "-InstallRoot",
                requestedRoot,
                "-NoLaunch",
                "-NoShortcuts",
                "-SuppressUi");

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(
                "reparse point",
                result.StdOut + result.StdErr,
                StringComparison.OrdinalIgnoreCase);
            Assert.False(
                Directory.Exists(
                    Path.Combine(
                        physicalRoot,
                        "missing-install")));
        }
        finally
        {
            if (Directory.Exists(junctionRoot))
                Directory.Delete(junctionRoot);
            MakeTreeDeletable(testRoot);
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task GeneratedInstallerSupervisor_RestoresExactSnapshotAfterDeterministicWorkerTimeout()
    {
        var repositoryRoot = FindRepositoryRoot();
        var builderPath = Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Build-GeoraePlanDesktopInstaller.ps1");
        var testRoot = NewInstallRoot();
        var fakeProjectRoot = Path.Combine(testRoot, "project");
        var sourceFolder = Path.Combine(testRoot, "source");
        var outputRoot = Path.Combine(testRoot, "output");
        var installRoot = Path.Combine(testRoot, "installed", "tradeplan");
        var installParent = Path.GetDirectoryName(installRoot)
            ?? throw new InvalidOperationException(
                "Install parent was not resolved.");
        var descendantMutationPath = Path.Combine(
            installRoot,
            ".georaeplan-installer-delayed-descendant-mutation");
        var preexistingRollback = Path.Combine(
            installParent,
            ".tradeplan-install-rollback-preexisting");

        try
        {
            Directory.CreateDirectory(
                Path.Combine(fakeProjectRoot, "deploy"));
            await File.WriteAllTextAsync(
                Path.Combine(
                    fakeProjectRoot,
                    "deploy",
                    "Set-ApiBaseUrl.ps1"),
                "# test deployment marker");
            var desktopProjectDirectory = Path.Combine(
                fakeProjectRoot,
                "Desktop",
                "거래플랜.Desktop.App");
            Directory.CreateDirectory(desktopProjectDirectory);
            var versionedDesktopFixturePath =
                typeof(Program).Assembly.Location;
            var versionedDesktopFixtureProductVersion =
                FileVersionInfo.GetVersionInfo(
                    versionedDesktopFixturePath).ProductVersion
                ?? throw new InvalidOperationException(
                    "Versioned desktop fixture has no ProductVersion.");
            var desktopFixtureVersion =
                versionedDesktopFixtureProductVersion
                    .Split('+')[0]
                    .Split('-')[0]
                    .Trim()
                    .TrimStart('v', 'V');
            Assert.True(
                Version.TryParse(desktopFixtureVersion, out _),
                $"Invalid desktop fixture ProductVersion: {versionedDesktopFixtureProductVersion}");
            await File.WriteAllTextAsync(
                Path.Combine(
                    desktopProjectDirectory,
                    "거래플랜.Desktop.App.csproj"),
                $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <Version>{desktopFixtureVersion}</Version>
                  </PropertyGroup>
                </Project>
                """);

            Directory.CreateDirectory(
                Path.Combine(sourceFolder, "Updater"));
            await File.WriteAllTextAsync(
                Path.Combine(sourceFolder, "appsettings.json"),
                "{\"Api\":{\"BaseUrl\":\"https://example.invalid/new\"}}");
            File.Copy(
                versionedDesktopFixturePath,
                Path.Combine(sourceFolder, "거래플랜.exe"),
                overwrite: true);
            await File.WriteAllTextAsync(
                Path.Combine(
                    sourceFolder,
                    "Updater",
                    "거래플랜.Updater.exe"),
                "new updater");

            var fakeDotnetPath = Path.Combine(
                testRoot,
                "fake-dotnet.cmd");
            await File.WriteAllTextAsync(
                fakeDotnetPath,
                """
                @echo off
                if "%~1"=="--version" (
                  echo 8.0.100
                  exit /b 0
                )
                exit /b 0
                """);

            var buildResult = await RunPowerShellAsync(
                builderPath,
                new Dictionary<string, string?>
                {
                    ["DOTNET_EXE"] = fakeDotnetPath
                },
                TimeSpan.FromMinutes(2),
                "-ProjectRoot",
                fakeProjectRoot,
                "-SourceFolder",
                sourceFolder,
                "-OutputRoot",
                outputRoot,
                "-EnableTestHooks",
                "-SkipNativeInstallers");
            Assert.Equal(
                0,
                buildResult.ExitCode);

            var installScriptPath = Path.Combine(
                outputRoot,
                "관리자용",
                "거래플랜-PC-설치패키지",
                "Install-GeoraePlan.ps1");
            var descendantPidPath = Path.Combine(
                Path.GetDirectoryName(installScriptPath)
                    ?? throw new InvalidOperationException(
                        "Generated package root was not resolved."),
                ".georaeplan-installer-delayed-descendant.pid");
            var workerPidPath = Path.Combine(
                Path.GetDirectoryName(installScriptPath)
                    ?? throw new InvalidOperationException(
                        "Generated package root was not resolved."),
                ".georaeplan-installer-hang-worker.pid");
            Assert.True(File.Exists(installScriptPath));
            var generatedScript = await File.ReadAllTextAsync(
                installScriptPath);
            Assert.Contains(
                "GEORAEPLAN_INSTALL_SUPERVISOR_CONTRACT_V1",
                generatedScript,
                StringComparison.Ordinal);
            await RewriteGeneratedManagedRootsAsync(
                new GeneratedInstallerPackage(
                    installScriptPath,
                    "거래플랜"),
                Path.Combine(testRoot, "isolated-managed-roots"));

            Directory.CreateDirectory(
                Path.Combine(installRoot, "nested"));
            await File.WriteAllTextAsync(
                Path.Combine(installRoot, "거래플랜.exe"),
                "old executable");
            await File.WriteAllTextAsync(
                Path.Combine(installRoot, "appsettings.json"),
                "{\"Api\":{\"BaseUrl\":\"https://example.invalid/old\"}}");
            var protectedFilePath = Path.Combine(
                installRoot,
                "nested",
                "hidden-readonly.bin");
            await File.WriteAllBytesAsync(
                protectedFilePath,
                [1, 3, 3, 7, 9]);
            var fixedTimestamp =
                new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(
                protectedFilePath,
                fixedTimestamp);
            File.SetAttributes(
                protectedFilePath,
                FileAttributes.Hidden |
                FileAttributes.ReadOnly |
                FileAttributes.Archive);
            Directory.SetLastWriteTimeUtc(
                Path.Combine(installRoot, "nested"),
                fixedTimestamp);
            Directory.SetLastWriteTimeUtc(
                installRoot,
                fixedTimestamp);
            Directory.CreateDirectory(preexistingRollback);
            await File.WriteAllTextAsync(
                Path.Combine(preexistingRollback, "keep.txt"),
                "preexisting snapshot must remain");
            var beforeManifest = CaptureInstallTreeManifest(
                installRoot);

            var packageRoot = Path.GetDirectoryName(installScriptPath)
                ?? throw new InvalidOperationException(
                    "Generated package root was not resolved.");
            var installerTestCapability =
                await File.ReadAllTextAsync(
                    Path.Combine(
                        packageRoot,
                        ".georaeplan-installer-test-capability"));
            var installError =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => Program.ExecuteInstallWithRollbackAsync(
                        new UpdateArguments
                        {
                            InstallRoot = installRoot,
                            LaunchExe = Path.Combine(
                                installRoot,
                                "거래플랜.exe"),
                            Version = desktopFixtureVersion
                        },
                        packageRoot,
                        installScriptPath,
                        Path.Combine(testRoot, "install.log"),
                        Path.Combine(testRoot, "artifacts"),
                        TimeSpan.FromSeconds(8),
                        new Dictionary<string, string?>
                        {
                            ["GEORAEPLAN_INSTALLER_TEST_HANG_AFTER_ROLLBACK_SNAPSHOT"] = "1",
                            ["GEORAEPLAN_INSTALLER_TEST_SPAWN_DELAYED_DESCENDANT"] =
                                "1",
                            ["GEORAEPLAN_INSTALLER_TEST_DELAYED_DESCENDANT_DELAY_MS"] =
                                "30000",
                            ["GEORAEPLAN_INSTALLER_TEST_CAPABILITY"] =
                                installerTestCapability
                        }));

            Assert.Contains(
                "업데이트 설치가 실패했습니다. exitCode=1",
                installError.Message,
                StringComparison.Ordinal);
            Assert.True(
                File.Exists(workerPidPath),
                File.Exists(Path.Combine(testRoot, "install.log"))
                    ? await File.ReadAllTextAsync(
                        Path.Combine(testRoot, "install.log"))
                    : installError.ToString());
            Assert.True(
                int.TryParse(
                    (await File.ReadAllTextAsync(workerPidPath)).Trim(),
                    out var workerPid));
            Assert.Throws<ArgumentException>(
                () => Process.GetProcessById(workerPid));
            Assert.True(File.Exists(descendantPidPath));
            Assert.True(
                int.TryParse(
                    (await File.ReadAllTextAsync(descendantPidPath)).Trim(),
                    out var descendantPid));
            Assert.Throws<ArgumentException>(
                () => Process.GetProcessById(descendantPid));
            Assert.False(File.Exists(descendantMutationPath));
            Assert.False(
                File.Exists(
                    Path.Combine(
                        installRoot,
                        ".georaeplan-installer-timeout-mutation")));
            Assert.Equal(
                beforeManifest,
                CaptureInstallTreeManifest(installRoot));
            Assert.Equal(
                [Path.GetFullPath(preexistingRollback)],
                Directory.EnumerateDirectories(
                        installParent,
                        ".tradeplan-install-rollback-*",
                        SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFullPath)
                    .OrderBy(
                        static path => path,
                        StringComparer.OrdinalIgnoreCase)
                    .ToArray());
            Assert.Empty(
                Directory.EnumerateDirectories(
                    installParent,
                    ".tradeplan-update-supervisor-state-*",
                    SearchOption.TopDirectoryOnly));

            using var reacquired =
                InstallRootUpdateLock.Acquire(installRoot);
        }
        finally
        {
            MakeTreeDeletable(testRoot);
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task GeneratedInstallerSupervisor_SameVersionWorkerRunningRecoversBeforeUpdaterSkip()
    {
        var repositoryRoot = FindRepositoryRoot();
        var testRoot = NewInstallRoot();
        var generatedInstaller =
            await BuildGeneratedInstallerPackageAsync(
                repositoryRoot,
                testRoot);
        var installRoot = Path.Combine(
            testRoot,
            "installed",
            "tradeplan");
        var installParent = Path.GetDirectoryName(installRoot)
            ?? throw new InvalidOperationException(
                "Install parent was not resolved.");
        var workerPidPath =
            GetGeneratedHangWorkerPidPath(generatedInstaller);
        var firstRunLogPath = Path.Combine(
            testRoot,
            "crash-first-run.log");
        var recoveryLogPath = Path.Combine(
            testRoot,
            "crash-recovery.log");
        var mutationPath = Path.Combine(
            installRoot,
            ".georaeplan-installer-timeout-mutation");
        var preexistingRollback = Path.Combine(
            installParent,
            ".tradeplan-install-rollback-preexisting");
        Process? supervisor = null;

        try
        {
            Directory.CreateDirectory(
                Path.Combine(installRoot, "nested"));
            var launchExePath = Path.Combine(
                installRoot,
                generatedInstaller.AppDisplayName + ".exe");
            File.Copy(
                typeof(Program).Assembly.Location,
                launchExePath);
            var installedProductVersion =
                FileVersionInfo.GetVersionInfo(
                    launchExePath).ProductVersion;
            Assert.False(
                string.IsNullOrWhiteSpace(installedProductVersion));
            await File.WriteAllTextAsync(
                Path.Combine(installRoot, "appsettings.json"),
                "{\"Api\":{\"BaseUrl\":\"https://example.invalid/old\"}}");
            var protectedFilePath = Path.Combine(
                installRoot,
                "nested",
                "hidden-readonly.bin");
            await File.WriteAllBytesAsync(
                protectedFilePath,
                [2, 7, 1, 8, 2, 8]);
            var fixedTimestamp =
                new DateTime(2025, 2, 3, 4, 5, 6, DateTimeKind.Utc);
            File.SetCreationTimeUtc(
                protectedFilePath,
                fixedTimestamp);
            File.SetLastWriteTimeUtc(
                protectedFilePath,
                fixedTimestamp);
            File.SetAttributes(
                protectedFilePath,
                FileAttributes.Hidden |
                FileAttributes.ReadOnly |
                FileAttributes.Archive);
            Directory.SetLastWriteTimeUtc(
                Path.Combine(installRoot, "nested"),
                fixedTimestamp);
            Directory.SetCreationTimeUtc(
                Path.Combine(installRoot, "nested"),
                fixedTimestamp);
            File.SetAttributes(
                Path.Combine(installRoot, "nested"),
                File.GetAttributes(
                    Path.Combine(installRoot, "nested")) |
                FileAttributes.ReadOnly);
            Directory.SetLastWriteTimeUtc(
                installRoot,
                fixedTimestamp);
            Directory.SetCreationTimeUtc(
                installRoot,
                fixedTimestamp);
            File.SetAttributes(
                installRoot,
                File.GetAttributes(installRoot) |
                FileAttributes.ReadOnly);
            Directory.CreateDirectory(preexistingRollback);
            await File.WriteAllTextAsync(
                Path.Combine(preexistingRollback, "keep.txt"),
                "preexisting snapshot must remain");
            var beforeManifest =
                CaptureInstallTreeManifest(installRoot);

            supervisor = StartPowerShellScript(
                generatedInstaller.InstallScriptPath,
                new Dictionary<string, string?>
                {
                    ["GEORAEPLAN_INSTALLER_TEST_HANG_AFTER_ROLLBACK_SNAPSHOT"] = "1"
                },
                "-InstallRoot",
                installRoot,
                "-NoLaunch",
                "-NoShortcuts",
                "-SuppressUi",
                "-WorkerTimeoutSeconds",
                "60",
                "-LogPath",
                firstRunLogPath);
            await WaitForFileAsync(
                workerPidPath,
                TimeSpan.FromSeconds(20));
            await WaitForFileAsync(
                mutationPath,
                TimeSpan.FromSeconds(20));

            var stateRoot = Assert.Single(
                Directory.EnumerateDirectories(
                    installParent,
                    ".tradeplan-update-supervisor-state-*",
                    SearchOption.TopDirectoryOnly));
            var journalPath = Path.Combine(
                stateRoot,
                "journal.json");
            Assert.Contains(
                "\"WorkerRunning\"",
                await File.ReadAllTextAsync(journalPath),
                StringComparison.Ordinal);

            supervisor.Kill(entireProcessTree: true);
            await supervisor.WaitForExitAsync()
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(File.Exists(mutationPath));
            Assert.True(File.Exists(journalPath));

            Assert.True(
                int.TryParse(
                    (await File.ReadAllTextAsync(workerPidPath)).Trim(),
                    out var workerPid));
            await WaitForProcessExitAsync(
                workerPid,
                TimeSpan.FromSeconds(10));

            var recoveredVersionState =
                await Program
                    .RecoverGeneratedInstallStateBeforeVersionDecisionAsync(
                        new UpdateArguments
                        {
                            InstallRoot = installRoot,
                            LaunchExe = launchExePath,
                            Version = installedProductVersion!
                        },
                        Path.GetDirectoryName(
                            generatedInstaller.InstallScriptPath)
                        ?? throw new InvalidOperationException(
                            "Generated package root was not resolved."),
                        generatedInstaller.InstallScriptPath,
                        recoveryLogPath,
                        TimeSpan.FromSeconds(30));
            Assert.Equal(
                InstalledVersionState.AtLeastRequested,
                recoveredVersionState);

            Assert.False(File.Exists(mutationPath));
            Assert.Equal(
                beforeManifest,
                CaptureInstallTreeManifest(installRoot));
            Assert.Equal(
                [Path.GetFullPath(preexistingRollback)],
                Directory.EnumerateDirectories(
                        installParent,
                        ".tradeplan-install-rollback-*",
                        SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFullPath)
                    .OrderBy(
                        static path => path,
                        StringComparer.OrdinalIgnoreCase)
                    .ToArray());
            Assert.Empty(
                Directory.EnumerateDirectories(
                    installParent,
                    ".tradeplan-update-supervisor-state-*",
                    SearchOption.TopDirectoryOnly));
            Assert.Throws<ArgumentException>(
                () => Process.GetProcessById(workerPid));

            using var reacquired =
                InstallRootUpdateLock.Acquire(installRoot);
        }
        finally
        {
            TryTerminateProcess(supervisor);
            MakeTreeDeletable(testRoot);
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task GeneratedInstallerSupervisor_MkdirAclCrashRecoversOnlyEmptyPreparationStateAndUnblocksDesktop()
    {
        var testRoot = NewInstallRoot();
        var generatedInstaller =
            await BuildGeneratedInstallerPackageAsync(
                FindRepositoryRoot(),
                testRoot);
        var installRoot = Path.Combine(
            testRoot,
            "installed",
            "tradeplan");
        var originalMarkerPath = Path.Combine(
            installRoot,
            "original-data.marker");
        Process? rejectedRecovery = null;

        try
        {
            Directory.CreateDirectory(installRoot);
            await File.WriteAllTextAsync(
                originalMarkerPath,
                "original data remains unchanged");

            var crashResult = await RunPowerShellAsync(
                generatedInstaller.InstallScriptPath,
                new Dictionary<string, string?>
                {
                    ["GEORAEPLAN_INSTALLER_TEST_CRASH_AFTER_STATE_ROOT_CREATE"] =
                        "1"
                },
                TimeSpan.FromSeconds(30),
                "-InstallRoot",
                installRoot,
                "-NoLaunch",
                "-NoShortcuts",
                "-SuppressUi");
            Assert.NotEqual(0, crashResult.ExitCode);

            var pendingState =
                InstallRecoveryStateProbe.Probe(installRoot);
            Assert.Equal(
                InstallRecoveryStateStatus.Present,
                pendingState.Status);
            Assert.Empty(
                Directory.EnumerateFileSystemEntries(
                    pendingState.StatePath));
            Assert.NotNull(
                DesktopApplication
                    .GetInstallRecoveryStartupBlockMessage(
                        installRoot));

            var recoveryResult = await RunPowerShellAsync(
                generatedInstaller.InstallScriptPath,
                new Dictionary<string, string?>(),
                TimeSpan.FromSeconds(30),
                "-InstallRoot",
                installRoot,
                "-RecoveryOnly",
                "-NoLaunch",
                "-NoShortcuts",
                "-SuppressUi");
            Assert.True(
                recoveryResult.ExitCode == 0,
                recoveryResult.StdOut + Environment.NewLine +
                recoveryResult.StdErr);
            Assert.Equal(
                InstallRecoveryStateStatus.Absent,
                InstallRecoveryStateProbe.Probe(installRoot).Status);
            Assert.Null(
                DesktopApplication
                    .GetInstallRecoveryStartupBlockMessage(
                        installRoot));
            Assert.Equal(
                "original data remains unchanged",
                await File.ReadAllTextAsync(originalMarkerPath));
            Assert.False(
                File.Exists(
                    Path.Combine(
                        installRoot,
                        generatedInstaller.AppDisplayName + ".exe")));
            Assert.False(
                File.Exists(
                    Path.Combine(installRoot, "appsettings.json")));

            var invalidModeResult = await RunPowerShellAsync(
                generatedInstaller.InstallScriptPath,
                new Dictionary<string, string?>(),
                TimeSpan.FromSeconds(30),
                "-InstallRoot",
                installRoot,
                "-WorkerMode",
                "-RecoveryOnly",
                "-NoLaunch",
                "-NoShortcuts",
                "-SuppressUi");
            Assert.NotEqual(0, invalidModeResult.ExitCode);
            Assert.Contains(
                "RecoveryOnly",
                invalidModeResult.StdOut + invalidModeResult.StdErr,
                StringComparison.Ordinal);

            var firstJournalCrashResult =
                await RunPowerShellAsync(
                    generatedInstaller.InstallScriptPath,
                    new Dictionary<string, string?>
                    {
                        ["GEORAEPLAN_INSTALLER_TEST_CRASH_AFTER_FIRST_JOURNAL_TEMP_FLUSH"] =
                            "1"
                    },
                    TimeSpan.FromSeconds(30),
                    "-InstallRoot",
                    installRoot,
                    "-NoLaunch",
                    "-NoShortcuts",
                    "-SuppressUi");
            Assert.NotEqual(
                0,
                firstJournalCrashResult.ExitCode);
            var firstJournalPending =
                InstallRecoveryStateProbe.Probe(installRoot);
            Assert.Equal(
                InstallRecoveryStateStatus.Present,
                firstJournalPending.Status);
            var emptySnapshotsScaffold = Assert.Single(
                Directory.EnumerateDirectories(
                    firstJournalPending.StatePath));
            Assert.Equal(
                "snapshots",
                Path.GetFileName(emptySnapshotsScaffold));
            Assert.Empty(
                Directory.EnumerateFileSystemEntries(
                    emptySnapshotsScaffold));
            Assert.Empty(
                Directory.EnumerateFiles(
                    firstJournalPending.StatePath));
            var firstJournalStateParent =
                Path.GetDirectoryName(
                    firstJournalPending.StatePath)
                ?? throw new InvalidOperationException(
                    "Supervisor state parent was not resolved.");
            var journalTemporaryPattern =
                "." +
                Path.GetFileName(
                    firstJournalPending.StatePath) +
                ".journal.*.tmp";
            Assert.Single(
                Directory.EnumerateFiles(
                    firstJournalStateParent,
                    journalTemporaryPattern,
                    SearchOption.TopDirectoryOnly));
            Assert.NotNull(
                DesktopApplication
                    .GetInstallRecoveryStartupBlockMessage(
                        installRoot));

            var firstJournalRecoveryResult =
                await RunPowerShellAsync(
                    generatedInstaller.InstallScriptPath,
                    new Dictionary<string, string?>(),
                    TimeSpan.FromSeconds(30),
                    "-InstallRoot",
                    installRoot,
                    "-RecoveryOnly",
                    "-NoLaunch",
                    "-NoShortcuts",
                    "-SuppressUi");
            Assert.True(
                firstJournalRecoveryResult.ExitCode == 0,
                firstJournalRecoveryResult.StdOut +
                Environment.NewLine +
                firstJournalRecoveryResult.StdErr);
            Assert.Equal(
                InstallRecoveryStateStatus.Absent,
                InstallRecoveryStateProbe.Probe(installRoot)
                    .Status);
            Assert.Empty(
                Directory.EnumerateFiles(
                    firstJournalStateParent,
                    journalTemporaryPattern,
                    SearchOption.TopDirectoryOnly));
            Assert.Null(
                DesktopApplication
                    .GetInstallRecoveryStartupBlockMessage(
                        installRoot));
            Assert.False(
                File.Exists(
                    Path.Combine(
                        installRoot,
                        generatedInstaller.AppDisplayName +
                        ".exe")));

            var rejectedStatePath =
                InstallRecoveryStateProbe.GetStatePath(installRoot);
            Directory.CreateDirectory(rejectedStatePath);
            var unexpectedPayloadPath = Path.Combine(
                rejectedStatePath,
                "unexpected-payload.bin");
            await File.WriteAllBytesAsync(
                unexpectedPayloadPath,
                [6, 2, 6, 4]);
            rejectedRecovery = StartPowerShellScript(
                generatedInstaller.InstallScriptPath,
                "-InstallRoot",
                installRoot,
                "-RecoveryOnly",
                "-NoLaunch",
                "-NoShortcuts",
                "-SuppressUi");
            await Task.Delay(750);

            Assert.False(rejectedRecovery.HasExited);
            Assert.True(File.Exists(unexpectedPayloadPath));
            Assert.Equal(
                InstallRecoveryStateStatus.Present,
                InstallRecoveryStateProbe.Probe(installRoot).Status);
            Assert.NotNull(
                DesktopApplication
                    .GetInstallRecoveryStartupBlockMessage(
                        installRoot));
        }
        finally
        {
            TryTerminateProcess(rejectedRecovery);
            MakeTreeDeletable(testRoot);
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task GeneratedInstallerSupervisor_LegacyV1JournalWithoutCreationTicksRecovers()
    {
        var testRoot = NewInstallRoot();
        var generatedInstaller =
            await BuildGeneratedInstallerPackageAsync(
                FindRepositoryRoot(),
                testRoot);
        var installRoot = Path.Combine(
            testRoot,
            "installed",
            "tradeplan");
        var installParent = Path.GetDirectoryName(installRoot)
            ?? throw new InvalidOperationException(
                "Install parent was not resolved.");
        var originalFilePath = Path.Combine(
            installRoot,
            "legacy-v1-original.txt");
        var workerPidPath =
            GetGeneratedHangWorkerPidPath(generatedInstaller);
        var mutationPath = Path.Combine(
            installRoot,
            ".georaeplan-installer-timeout-mutation");
        Process? supervisor = null;

        try
        {
            Directory.CreateDirectory(installRoot);
            await File.WriteAllTextAsync(
                originalFilePath,
                "legacy v1 original payload");
            var originalLastWriteUtc =
                new DateTime(
                    2024,
                    6,
                    7,
                    8,
                    9,
                    10,
                    DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(
                originalFilePath,
                originalLastWriteUtc);

            supervisor = StartPowerShellScript(
                generatedInstaller.InstallScriptPath,
                new Dictionary<string, string?>
                {
                    ["GEORAEPLAN_INSTALLER_TEST_HANG_AFTER_ROLLBACK_SNAPSHOT"] =
                        "1"
                },
                "-InstallRoot",
                installRoot,
                "-NoLaunch",
                "-NoShortcuts",
                "-SuppressUi",
                "-WorkerTimeoutSeconds",
                "60");
            await WaitForFileAsync(
                workerPidPath,
                TimeSpan.FromSeconds(20));
            await WaitForFileAsync(
                mutationPath,
                TimeSpan.FromSeconds(20));

            var stateRoot = Assert.Single(
                Directory.EnumerateDirectories(
                    installParent,
                    ".tradeplan-update-supervisor-state-*",
                    SearchOption.TopDirectoryOnly));
            var journalPath = Path.Combine(
                stateRoot,
                "journal.json");
            Assert.True(File.Exists(journalPath));

            supervisor.Kill(entireProcessTree: true);
            await supervisor.WaitForExitAsync()
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(
                int.TryParse(
                    (await File.ReadAllTextAsync(workerPidPath))
                    .Trim(),
                    out var workerPid));
            await WaitForProcessExitAsync(
                workerPid,
                TimeSpan.FromSeconds(10));

            var legacyJournal =
                System.Text.RegularExpressions.Regex.Replace(
                    await File.ReadAllTextAsync(journalPath),
                    @"(?m)^[ \t]*""(?:Root)?CreationTimeUtcTicks""\s*:\s*\d+\s*,\r?\n",
                    string.Empty);
            var legacyJournalNode = JsonNode.Parse(legacyJournal)
                ?.AsObject()
                ?? throw new InvalidOperationException(
                    "Legacy supervisor journal JSON was not resolved.");
            legacyJournalNode["FormatVersion"] = 1;
            Assert.True(legacyJournalNode.Remove("WorkerJobName"));
            Assert.True(legacyJournalNode.Remove("WorkerProcessId"));
            Assert.True(legacyJournalNode.Remove("WorkerProcessPath"));
            Assert.True(
                legacyJournalNode.Remove(
                    "WorkerProcessStartTimeUtcTicks"));
            legacyJournal = legacyJournalNode.ToJsonString(
                new JsonSerializerOptions
                {
                    WriteIndented = true
                });
            Assert.DoesNotContain(
                "CreationTimeUtcTicks",
                legacyJournal,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "WorkerJobName",
                legacyJournal,
                StringComparison.Ordinal);
            await File.WriteAllTextAsync(
                journalPath,
                legacyJournal);

            var recoveryResult = await RunPowerShellAsync(
                generatedInstaller.InstallScriptPath,
                new Dictionary<string, string?>(),
                TimeSpan.FromSeconds(30),
                "-InstallRoot",
                installRoot,
                "-RecoveryOnly",
                "-NoLaunch",
                "-NoShortcuts",
                "-SuppressUi");
            Assert.True(
                recoveryResult.ExitCode == 0,
                recoveryResult.StdOut + Environment.NewLine +
                recoveryResult.StdErr);
            Assert.Equal(
                "legacy v1 original payload",
                await File.ReadAllTextAsync(originalFilePath));
            Assert.Equal(
                originalLastWriteUtc,
                File.GetLastWriteTimeUtc(originalFilePath));
            Assert.False(File.Exists(mutationPath));
            Assert.False(Directory.Exists(stateRoot));
        }
        finally
        {
            TryTerminateProcess(supervisor);
            MakeTreeDeletable(testRoot);
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void InstallRecoveryStateProbe_UsesExactSiblingAndFailsClosedOnAccessError()
    {
        var testRoot = NewInstallRoot();
        var installRoot = Path.Combine(
            testRoot,
            "installed",
            "tradeplan");
        string? restrictedStatePath = null;
        FileSystemAccessRule? deniedReadAttributes = null;
        try
        {
            Directory.CreateDirectory(installRoot);
            var statePath =
                InstallRecoveryStateProbe.GetStatePath(installRoot);
            Assert.Equal(
                Path.GetDirectoryName(installRoot),
                Path.GetDirectoryName(statePath));
            Assert.StartsWith(
                InstallRecoveryStateProbe.StateDirectoryPrefix,
                Path.GetFileName(statePath),
                StringComparison.Ordinal);
            Assert.Equal(
                InstallRecoveryStateStatus.Absent,
                InstallRecoveryStateProbe.Probe(installRoot).Status);

            Directory.CreateDirectory(statePath);
            var present =
                InstallRecoveryStateProbe.Probe(installRoot);
            Assert.Equal(
                InstallRecoveryStateStatus.Present,
                present.Status);
            Assert.Equal(
                Path.GetFullPath(statePath),
                Path.GetFullPath(present.StatePath));

            restrictedStatePath = statePath;
            var currentUserSid =
                WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException(
                    "Current Windows user SID was not resolved.");
            deniedReadAttributes = new FileSystemAccessRule(
                currentUserSid,
                FileSystemRights.ReadAttributes |
                FileSystemRights.ReadExtendedAttributes |
                FileSystemRights.ReadData,
                AccessControlType.Deny);
            var stateDirectoryInfo =
                new DirectoryInfo(statePath);
            var stateSecurity =
                stateDirectoryInfo.GetAccessControl();
            stateSecurity.AddAccessRule(
                deniedReadAttributes);
            stateDirectoryInfo.SetAccessControl(
                stateSecurity);
            Assert.Throws<UnauthorizedAccessException>(
                () => Directory
                    .EnumerateFileSystemEntries(statePath)
                    .ToArray());
            Assert.Equal(
                InstallRecoveryStateStatus.Present,
                InstallRecoveryStateProbe.Probe(installRoot)
                    .Status);

            var accessError =
                InstallRecoveryStateProbe.ProbeCore(
                    installRoot,
                    (_, _) => throw new UnauthorizedAccessException(
                        "deterministic probe denial"));
            Assert.Equal(
                InstallRecoveryStateStatus.AccessError,
                accessError.Status);
            Assert.IsType<UnauthorizedAccessException>(
                accessError.Error);
            Assert.Throws<InvalidOperationException>(
                () => Program.RequireGeneratedInstallRecoveryProbe(
                    installRoot + "\0"));

            var programFilesRoot =
                Environment.GetFolderPath(
                    Environment.SpecialFolder.ProgramFilesX86);
            if (string.IsNullOrWhiteSpace(programFilesRoot))
            {
                programFilesRoot = Environment.GetFolderPath(
                    Environment.SpecialFolder.ProgramFiles);
            }
            Assert.False(
                string.IsNullOrWhiteSpace(programFilesRoot));
            var absentProtectedInstallRoot = Path.Combine(
                programFilesRoot,
                "tradeplan-probe-" + Guid.NewGuid().ToString("N"));
            Assert.Equal(
                InstallRecoveryStateStatus.Absent,
                InstallRecoveryStateProbe.Probe(
                    absentProtectedInstallRoot).Status);
        }
        finally
        {
            if (restrictedStatePath is not null &&
                deniedReadAttributes is not null &&
                Directory.Exists(restrictedStatePath))
            {
                var stateDirectoryInfo =
                    new DirectoryInfo(restrictedStatePath);
                var stateSecurity =
                    stateDirectoryInfo.GetAccessControl();
                stateSecurity.RemoveAccessRuleSpecific(
                    deniedReadAttributes);
                stateDirectoryInfo.SetAccessControl(
                    stateSecurity);
            }
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void DesktopStartup_BlocksPendingAndProbeAccessErrorBeforeUiInitialization()
    {
        var testRoot = NewInstallRoot();
        var installRoot = Path.Combine(
            testRoot,
            "installed",
            "tradeplan");
        try
        {
            Directory.CreateDirectory(installRoot);
            var statePath =
                InstallRecoveryStateProbe.GetStatePath(installRoot);
            Directory.CreateDirectory(statePath);

            var pendingMessage =
                거래플랜.Desktop.App.App
                    .GetInstallRecoveryStartupBlockMessage(
                        installRoot);
            Assert.NotNull(pendingMessage);
            Assert.Contains(
                "앱 시작을 차단",
                pendingMessage,
                StringComparison.Ordinal);
            Assert.Contains(
                "원본 데이터에는 손대지 않았습니다",
                pendingMessage,
                StringComparison.Ordinal);
            Assert.Contains(
                "업데이트를 다시 실행",
                pendingMessage,
                StringComparison.Ordinal);

            var accessErrorMessage =
                거래플랜.Desktop.App.App
                    .GetInstallRecoveryStartupBlockMessage(
                        installRoot,
                        new InstallRecoveryStateProbeResult(
                            InstallRecoveryStateStatus.AccessError,
                            statePath,
                            new UnauthorizedAccessException(
                                "deterministic probe denial")));
            Assert.NotNull(accessErrorMessage);
            Assert.Contains(
                "안전하게 확인할 수 없어",
                accessErrorMessage,
                StringComparison.Ordinal);

            var appSource = File.ReadAllText(
                Path.Combine(
                    FindRepositoryRoot(),
                    "Desktop",
                    "거래플랜.Desktop.App",
                    "App.xaml.cs"));
            AssertInOrder(
                appSource,
                "InstallRootUpdateGate.TryAcquire(",
                "GetInstallRecoveryStartupBlockMessage(AppContext.BaseDirectory)",
                "SingleInstanceGuard.TryAcquireForCurrentAppRoot",
                "DataGridAutoColumnWidthService.RegisterGlobal");
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ElevatedSupervisorTimeout_RetainsTheGateUntilTheSupervisorExits()
    {
        var testRoot = NewInstallRoot();
        var fakeSupervisor = Path.Combine(testRoot, "fake-elevated-supervisor.cmd");
        Directory.CreateDirectory(testRoot);
        await File.WriteAllTextAsync(
            fakeSupervisor,
            """
            @echo off
            :hang
            ping 127.0.0.1 -n 2 >nul
            goto hang
            """);
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add(fakeSupervisor);

        try
        {
            using (InstallRootUpdateLock.Acquire(testRoot))
            using (var process = Process.Start(startInfo)
                   ?? throw new InvalidOperationException(
                       "Fake elevated supervisor did not start."))
            {
                var waitTask = Program.WaitForInstallProcessExitAsync(
                    process,
                    TimeSpan.FromMilliseconds(150),
                    allowTimeoutTermination: false);
                await Task.Delay(400);

                Assert.False(waitTask.IsCompleted);
                Assert.Throws<InvalidOperationException>(
                    () => InstallRootUpdateLock.Acquire(testRoot));

                process.Kill(entireProcessTree: true);
                await waitTask.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(process.HasExited);
            }

            using var reacquired = InstallRootUpdateLock.Acquire(testRoot);
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task AllInstalls_RequireTheGeneratedSupervisorAsSingleTransactionWriter()
    {
        var programFilesRoot =
            Environment.GetFolderPath(
                Environment.SpecialFolder.ProgramFilesX86);
        if (string.IsNullOrWhiteSpace(programFilesRoot))
        {
            programFilesRoot = Environment.GetFolderPath(
                Environment.SpecialFolder.ProgramFiles);
        }

        Assert.False(string.IsNullOrWhiteSpace(programFilesRoot));
        Assert.True(
            Program.RequiresElevation(
                Path.Combine(programFilesRoot, "tradeplan")));
        Assert.False(
            Program.RequiresElevation(
                Path.Combine(
                    @"D:\DevCaches",
                    "georaeplan-unprotected-install")));

        var testRoot = NewInstallRoot();
        var missingContractScript = Path.Combine(
            testRoot,
            "missing-contract.ps1");
        var compatibleContractScript = Path.Combine(
            testRoot,
            "compatible-contract.ps1");
        try
        {
            Directory.CreateDirectory(testRoot);
            await File.WriteAllTextAsync(
                missingContractScript,
                "# ordinary installer");
            await File.WriteAllTextAsync(
                compatibleContractScript,
                """
                # GEORAEPLAN_INSTALL_SUPERVISOR_CONTRACT_V1
                # GEORAEPLAN_INSTALL_RECOVERY_ONLY_CONTRACT_V1
                """);

            Assert.Throws<InvalidOperationException>(
                () => Program.EnsureInstallSupervisorContract(
                    missingContractScript));
            Program.EnsureInstallSupervisorContract(
                compatibleContractScript);
            Assert.Throws<InvalidOperationException>(
                () => Program.EnsureInstallRecoveryOnlyContract(
                    missingContractScript));
            Program.EnsureInstallRecoveryOnlyContract(
                compatibleContractScript);

            var source = File.ReadAllText(
                Path.Combine(
                    FindRepositoryRoot(),
                    "Updater",
                    "거래플랜.Updater",
                    "Program.cs"));
            var executeInstall = source.IndexOf(
                "internal static async Task ExecuteInstallWithRollbackAsync(",
                StringComparison.Ordinal);
            var executeInstallEnd = source.IndexOf(
                "internal static async Task<InstalledVersionState>",
                executeInstall,
                StringComparison.Ordinal);
            var executeInstallSource =
                source[executeInstall..executeInstallEnd];
            var contractCheck = source.IndexOf(
                "EnsureInstallSupervisorContract(installScriptPath);",
                executeInstall,
                StringComparison.Ordinal);
            var recoveryContractCheck = source.IndexOf(
                "EnsureInstallRecoveryOnlyContract(installScriptPath);",
                contractCheck,
                StringComparison.Ordinal);
            var generatedInstall = source.IndexOf(
                "await RunInstallScriptAsync(",
                recoveryContractCheck,
                StringComparison.Ordinal);
            var retainGate = source.IndexOf(
                "allowTimeoutTermination: false",
                generatedInstall,
                StringComparison.Ordinal);
            var installedValidation = source.IndexOf(
                "ValidateInstalledApplication(options);",
                retainGate,
                StringComparison.Ordinal);

            Assert.True(executeInstall >= 0);
            Assert.True(executeInstallEnd > executeInstall);
            Assert.True(contractCheck > executeInstall);
            Assert.True(recoveryContractCheck > contractCheck);
            Assert.True(generatedInstall > recoveryContractCheck);
            Assert.True(retainGate > generatedInstall);
            Assert.True(installedValidation > retainGate);
            Assert.Contains(
                "_ = artifactRoot;",
                executeInstallSource,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "RequiresElevation(",
                executeInstallSource,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "InstallRollbackSupervisor.",
                executeInstallSource,
                StringComparison.Ordinal);

            var installerBuilderSource = File.ReadAllText(
                Path.Combine(
                    FindRepositoryRoot(),
                    "tools",
                    "release",
                    "Build-GeoraePlanDesktopInstaller.ps1"));
            Assert.Contains(
                "'.tradeplan-update-supervisor-state-'",
                installerBuilderSource,
                StringComparison.Ordinal);
            Assert.Contains(
                "'S-1-5-18'",
                installerBuilderSource,
                StringComparison.Ordinal);
            Assert.Contains(
                "'S-1-5-32-544'",
                installerBuilderSource,
                StringComparison.Ordinal);
            Assert.Contains(
                "'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464'",
                installerBuilderSource,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                ".StartsWith(\n            'S-1-5-80-'",
                installerBuilderSource,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "[System.Security.AccessControl.FileSystemRights]::FullControl -bor",
                installerBuilderSource,
                StringComparison.Ordinal);
            Assert.Contains(
                "GEORAEPLAN_INSTALLER_TEST_RECOVERY_ONLY",
                installerBuilderSource,
                StringComparison.Ordinal);
            Assert.Contains(
                "[switch]`$RecoveryOnly",
                installerBuilderSource,
                StringComparison.Ordinal);
            Assert.Contains(
                "GEORAEPLAN_INSTALL_RECOVERY_ONLY_CONTRACT_V1",
                installerBuilderSource,
                StringComparison.Ordinal);
            Assert.Contains(
                "`$acl.SetOwner(`$administratorSid)",
                installerBuilderSource,
                StringComparison.Ordinal);
            Assert.Contains(
                "`$acl.SetAccessRuleProtection(`$true, `$false)",
                installerBuilderSource,
                StringComparison.Ordinal);
            Assert.Contains(
                "Set-ProtectedSupervisorTreeSecurity",
                installerBuilderSource,
                StringComparison.Ordinal);
            var atomicDirectoryFactory =
                installerBuilderSource.IndexOf(
                    "function New-SupervisorStateDirectory {",
                    StringComparison.Ordinal);
            var atomicProtectedCreate =
                installerBuilderSource.IndexOf(
                    "[void][System.IO.Directory]::CreateDirectory(`$Path, `$acl)",
                    atomicDirectoryFactory,
                    StringComparison.Ordinal);
            Assert.True(atomicDirectoryFactory >= 0);
            Assert.True(atomicProtectedCreate > atomicDirectoryFactory);
            var newJournal = installerBuilderSource.IndexOf(
                "function New-SupervisorJournal {",
                StringComparison.Ordinal);
            var validateParentAcl = installerBuilderSource.IndexOf(
                "Assert-ProtectedSupervisorParentSecurity -ParentPath `$installParent",
                newJournal,
                StringComparison.Ordinal);
            var createState = installerBuilderSource.IndexOf(
                "New-SupervisorStateDirectory -Path `$stateRoot",
                newJournal,
                StringComparison.Ordinal);
            var crashAfterStateCreate = installerBuilderSource.IndexOf(
                "GEORAEPLAN_INSTALLER_TEST_CRASH_AFTER_STATE_ROOT_CREATE",
                createState,
                StringComparison.Ordinal);
            var secureState = installerBuilderSource.IndexOf(
                "Set-ProtectedSupervisorObjectSecurity -Path `$stateRoot",
                crashAfterStateCreate,
                StringComparison.Ordinal);
            var validateStateAcl = installerBuilderSource.IndexOf(
                "Assert-ProtectedSupervisorStateAcl -StateRoot `$stateRoot",
                secureState,
                StringComparison.Ordinal);
            var persistPreparingJournal = installerBuilderSource.IndexOf(
                "Write-SupervisorJournal -Journal `$journal",
                validateStateAcl,
                StringComparison.Ordinal);
            Assert.True(newJournal >= 0);
            Assert.True(validateParentAcl > newJournal);
            Assert.True(createState > validateParentAcl);
            Assert.True(crashAfterStateCreate > createState);
            Assert.True(secureState > crashAfterStateCreate);
            Assert.True(validateStateAcl > secureState);
            Assert.True(persistPreparingJournal > validateStateAcl);

            var stateAclFunction = installerBuilderSource[
                installerBuilderSource.IndexOf(
                    "function Assert-ProtectedSupervisorStateAcl {",
                    StringComparison.Ordinal)..
                installerBuilderSource.IndexOf(
                    "function Get-PreexistingInstallerRollbackDirectories {",
                    StringComparison.Ordinal)];
            Assert.DoesNotContain(
                "'S-1-3-0'",
                stateAclFunction,
                StringComparison.Ordinal);

            var pendingRecovery = installerBuilderSource.IndexOf(
                "function Invoke-PendingSupervisorRecoveryForRoot {",
                StringComparison.Ordinal);
            var bindRecoveryStatePath = installerBuilderSource.IndexOf(
                "Assert-SupervisorChildPath -Candidate `$stateRoot",
                pendingRecovery,
                StringComparison.Ordinal);
            var validateRecoveryParent = installerBuilderSource.IndexOf(
                "Assert-ProtectedSupervisorParentSecurity -ParentPath `$installParent",
                bindRecoveryStatePath,
                StringComparison.Ordinal);
            var probeRecoveryState = installerBuilderSource.IndexOf(
                "Test-SupervisorPathExistsFailClosed -Path `$stateRoot",
                validateRecoveryParent,
                StringComparison.Ordinal);
            var rejectRecoveryReparse = installerBuilderSource.IndexOf(
                "Assert-NoReparsePoints -Path `$stateRoot",
                probeRecoveryState,
                StringComparison.Ordinal);
            var probeJournal = installerBuilderSource.IndexOf(
                "Test-SupervisorPathExistsFailClosed -Path `$journalPath",
                rejectRecoveryReparse,
                StringComparison.Ordinal);
            var hardenJournalLessState = installerBuilderSource.IndexOf(
                "Set-ProtectedSupervisorTreeSecurity -StateRoot `$stateRoot",
                probeJournal,
                StringComparison.Ordinal);
            var validateJournalLessAcl = installerBuilderSource.IndexOf(
                "Assert-ProtectedSupervisorStateAcl -StateRoot `$stateRoot",
                hardenJournalLessState,
                StringComparison.Ordinal);
            var validateJournalLessLayout = installerBuilderSource.IndexOf(
                "Assert-AbandonedSupervisorPreparationState -StateRoot `$stateRoot",
                validateJournalLessAcl,
                StringComparison.Ordinal);
            var finishJournalLessRecovery = installerBuilderSource.IndexOf(
                "return `$true",
                validateJournalLessLayout,
                StringComparison.Ordinal);
            var validateJournalAcl = installerBuilderSource.IndexOf(
                "Assert-ProtectedSupervisorStateAcl -StateRoot `$stateRoot",
                finishJournalLessRecovery,
                StringComparison.Ordinal);
            var readBoundJournal = installerBuilderSource.IndexOf(
                "Read-SupervisorJournal -JournalPath `$journalPath",
                validateJournalAcl,
                StringComparison.Ordinal);
            Assert.True(pendingRecovery >= 0);
            Assert.True(bindRecoveryStatePath > pendingRecovery);
            Assert.True(validateRecoveryParent > bindRecoveryStatePath);
            Assert.True(probeRecoveryState > validateRecoveryParent);
            Assert.True(rejectRecoveryReparse > probeRecoveryState);
            Assert.True(probeJournal > rejectRecoveryReparse);
            Assert.True(hardenJournalLessState > probeJournal);
            Assert.True(validateJournalLessAcl > hardenJournalLessState);
            Assert.True(validateJournalLessLayout > validateJournalLessAcl);
            Assert.True(
                finishJournalLessRecovery > validateJournalLessLayout);
            Assert.True(validateJournalAcl > finishJournalLessRecovery);
            Assert.True(readBoundJournal > validateJournalAcl);
            Assert.DoesNotContain(
                "Set-ProtectedSupervisorTreeSecurity",
                installerBuilderSource[
                    finishJournalLessRecovery..readBoundJournal],
                StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void GeneratedInstaller_DirectLaunchOccursOnlyAfterCommitAndDurableStateCleanup()
    {
        var installerBuilderSource = File.ReadAllText(
            Path.Combine(
                FindRepositoryRoot(),
                "tools",
                "release",
                "Build-GeoraePlanDesktopInstaller.ps1"));
        var supervisorStart = installerBuilderSource.IndexOf(
            "function Invoke-WorkerUnderRollbackSupervisor {",
            StringComparison.Ordinal);
        var supervisorEnd = installerBuilderSource.IndexOf(
            "`$ErrorActionPreference = 'Stop'",
            supervisorStart,
            StringComparison.Ordinal);
        Assert.True(supervisorStart >= 0);
        Assert.True(supervisorEnd > supervisorStart);
        var supervisor = installerBuilderSource[
            supervisorStart..supervisorEnd];

        var workerArguments = supervisor.IndexOf(
            "`$workerArgumentParts = @(",
            StringComparison.Ordinal);
        var forcedWorkerNoLaunch = supervisor.IndexOf(
            "'-NoLaunch'",
            workerArguments,
            StringComparison.Ordinal);
        var workerStart = supervisor.IndexOf(
            "`$worker.Start()",
            forcedWorkerNoLaunch,
            StringComparison.Ordinal);
        var commitPending = supervisor.IndexOf(
            "`$journal.Phase = 'CommittedCleanupPending'",
            workerStart,
            StringComparison.Ordinal);
        var durableStateRemoval = supervisor.IndexOf(
            "Remove-CompletedSupervisorState -Journal `$journal",
            commitPending,
            StringComparison.Ordinal);
        var cleanupConfirmed = supervisor.IndexOf(
            "`$cleanupComplete = `$true",
            durableStateRemoval,
            StringComparison.Ordinal);
        var topLevel = installerBuilderSource[
            supervisorEnd..];
        var supervisorInvocation = topLevel.IndexOf(
            "Invoke-WorkerUnderRollbackSupervisor",
            StringComparison.Ordinal);
        var releaseRootGate = topLevel.IndexOf(
            "Exit-InstallRootGates -Gates `$heldInstallRootGates",
            supervisorInvocation,
            StringComparison.Ordinal);
        var outerLaunchGuard = topLevel.IndexOf(
            "if (`$supervisorExitCode -eq 0 -and -not `$NoLaunch) {",
            releaseRootGate,
            StringComparison.Ordinal);
        var outerLaunch = topLevel.IndexOf(
            "Start-Process -FilePath `$launchExecutable",
            outerLaunchGuard,
            StringComparison.Ordinal);

        Assert.True(workerArguments >= 0);
        Assert.True(forcedWorkerNoLaunch > workerArguments);
        Assert.True(workerStart > forcedWorkerNoLaunch);
        Assert.True(commitPending > workerStart);
        Assert.True(durableStateRemoval > commitPending);
        Assert.True(cleanupConfirmed > durableStateRemoval);
        Assert.True(supervisorInvocation >= 0);
        Assert.True(releaseRootGate > supervisorInvocation);
        Assert.True(outerLaunchGuard > releaseRootGate);
        Assert.True(outerLaunch > outerLaunchGuard);
        Assert.DoesNotContain(
            "Start-Process -FilePath `$exePath",
            supervisor,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedInstaller_RecoveryOnlySurvivesElevationAndReturnsBeforeFullInstall()
    {
        var installerBuilderSource = File.ReadAllText(
            Path.Combine(
                FindRepositoryRoot(),
                "tools",
                "release",
                "Build-GeoraePlanDesktopInstaller.ps1"));
        var elevationStart = installerBuilderSource.IndexOf(
            "function Ensure-ElevatedIfNeeded {",
            StringComparison.Ordinal);
        var elevationEnd = installerBuilderSource.IndexOf(
            "function Ensure-SufficientInstallSpace {",
            elevationStart,
            StringComparison.Ordinal);
        Assert.True(elevationStart >= 0);
        Assert.True(elevationEnd > elevationStart);
        var elevation = installerBuilderSource[
            elevationStart..elevationEnd];
        Assert.Contains(
            "if (`$RecoveryOnly) {",
            elevation,
            StringComparison.Ordinal);
        Assert.Contains(
            "`$argumentParts += '-RecoveryOnly'",
            elevation,
            StringComparison.Ordinal);

        var topLevelStart = installerBuilderSource.IndexOf(
            "`$ErrorActionPreference = 'Stop'",
            StringComparison.Ordinal);
        var rejectCombinedMode = installerBuilderSource.IndexOf(
            "if (`$WorkerMode -and `$RecoveryOnly) {",
            topLevelStart,
            StringComparison.Ordinal);
        var supervisorDispatch = installerBuilderSource.IndexOf(
            "if (-not `$WorkerMode) {",
            rejectCombinedMode,
            StringComparison.Ordinal);
        Assert.True(topLevelStart >= 0);
        Assert.True(rejectCombinedMode > topLevelStart);
        Assert.True(supervisorDispatch > rejectCombinedMode);

        var supervisorStart = installerBuilderSource.IndexOf(
            "function Invoke-WorkerUnderRollbackSupervisor {",
            StringComparison.Ordinal);
        var pendingRecovery = installerBuilderSource.IndexOf(
            "[void](Invoke-PendingSupervisorRecovery)",
            supervisorStart,
            StringComparison.Ordinal);
        var recoveryOnlyGate = installerBuilderSource.IndexOf(
            "if (`$RecoveryOnly -or",
            pendingRecovery,
            StringComparison.Ordinal);
        var recoveryOnlyReturn = installerBuilderSource.IndexOf(
            "return 0",
            recoveryOnlyGate,
            StringComparison.Ordinal);
        var fullInstallPackageResolution = installerBuilderSource.IndexOf(
            "`$packageRoot = Split-Path -Parent `$InstallerScriptPath",
            recoveryOnlyReturn,
            StringComparison.Ordinal);
        var newInstallJournal = installerBuilderSource.IndexOf(
            "`$journal = New-SupervisorJournal",
            fullInstallPackageResolution,
            StringComparison.Ordinal);
        Assert.True(supervisorStart >= 0);
        Assert.True(pendingRecovery > supervisorStart);
        Assert.True(recoveryOnlyGate > pendingRecovery);
        Assert.True(recoveryOnlyReturn > recoveryOnlyGate);
        Assert.True(fullInstallPackageResolution > recoveryOnlyReturn);
        Assert.True(newInstallJournal > fullInstallPackageResolution);
    }

    [Fact]
    public async Task GeneratedInstaller_ProtectedAclProbe_AcceptsProgramFilesAndRejectsWeakStateOnWindowsPowerShell()
    {
        var programFilesRoot =
            Environment.GetFolderPath(
                Environment.SpecialFolder.ProgramFilesX86);
        if (string.IsNullOrWhiteSpace(programFilesRoot))
        {
            programFilesRoot = Environment.GetFolderPath(
                Environment.SpecialFolder.ProgramFiles);
        }

        Assert.False(string.IsNullOrWhiteSpace(programFilesRoot));
        var testRoot = NewInstallRoot();
        try
        {
            Directory.CreateDirectory(testRoot);
            var generated =
                await BuildGeneratedInstallerPackageAsync(
                    FindRepositoryRoot(),
                    testRoot);
            var weakStateRoot = Path.Combine(
                testRoot,
                "weak-inherited-state");
            Directory.CreateDirectory(weakStateRoot);
            await File.WriteAllTextAsync(
                Path.Combine(weakStateRoot, "journal.json"),
                "{}");
            var probeScriptPath = Path.Combine(
                testRoot,
                "protected-acl-probe.ps1");
            await File.WriteAllTextAsync(
                probeScriptPath,
                """
                param(
                    [Parameter(Mandatory = $true)][string]$GeneratedScriptPath,
                    [Parameter(Mandatory = $true)][string]$InstallRootPath,
                    [Parameter(Mandatory = $true)][string]$WeakStateRootPath
                )

                $parseTokens = $null
                $parseErrors = $null
                $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                    $GeneratedScriptPath,
                    [ref]$parseTokens,
                    [ref]$parseErrors)
                if ($parseErrors.Count -ne 0) {
                    throw (($parseErrors | ForEach-Object Message) -join '; ')
                }

                $allFunctions = @($ast.FindAll({
                    param($node)
                    $node -is [System.Management.Automation.Language.FunctionDefinitionAst]
                }, $true))
                $requiredFunctionNames = @(
                    'Test-ProtectedInstallRoot',
                    'Get-NormalizedSupervisorPath',
                    'Test-SameSupervisorPath',
                    'Get-ProtectedSupervisorAllowedSids',
                    'Get-ProtectedSupervisorWriteMask',
                    'Resolve-ProtectedSupervisorSid',
                    'Assert-ProtectedSupervisorParentSecurity',
                    'Assert-ProtectedSupervisorStateAcl'
                )
                $functionTexts = @()
                foreach ($name in $requiredFunctionNames) {
                    $matches = @($allFunctions | Where-Object Name -eq $name)
                    if ($matches.Count -ne 1) {
                        throw "required function resolution failed: $name, Count=$($matches.Count)"
                    }
                    $functionTexts += $matches[0].Extent.Text
                }
                . ([scriptblock]::Create(
                    ($functionTexts -join [Environment]::NewLine)))

                # This extracted ACL-only harness does not execute the
                # generated script's NativePathIdentity Add-Type preamble.
                function Resolve-InstallerPathIdentity {
                    param(
                        [Parameter(Mandatory = $true)][string]$Path
                    )

                    return [System.IO.Path]::GetFullPath($Path)
                }

                $script:InstallRoot = $InstallRootPath
                $script:ActiveSupervisorInstallRoot = $InstallRootPath
                Assert-ProtectedSupervisorParentSecurity `
                    -ParentPath (Split-Path -Parent $InstallRootPath)

                $weakStateRejected = $false
                try {
                    Assert-ProtectedSupervisorStateAcl `
                        -StateRoot $WeakStateRootPath
                }
                catch {
                    $weakStateRejected = $true
                }
                if (-not $weakStateRejected) {
                    throw 'weak inherited supervisor state ACL was accepted'
                }

                Write-Output 'ACL_PROBE_PASS'
                """);

            var probeResult = await RunPowerShellAsync(
                probeScriptPath,
                new Dictionary<string, string?>(),
                TimeSpan.FromMinutes(2),
                "-GeneratedScriptPath",
                generated.InstallScriptPath,
                "-InstallRootPath",
                Path.Combine(programFilesRoot, "tradeplan"),
                "-WeakStateRootPath",
                weakStateRoot);

            Assert.True(
                probeResult.ExitCode == 0,
                probeResult.StdOut + Environment.NewLine +
                probeResult.StdErr);
            Assert.Contains(
                "ACL_PROBE_PASS",
                probeResult.StdOut,
                StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task StaleUpdateSkip_WaitsDeletesMetadataAndSchedulesCleanupBeforeGateRelease()
    {
        var programPath = Path.Combine(
            FindRepositoryRoot(),
            "Updater",
            "거래플랜.Updater",
            "Program.cs");
        var source = File.ReadAllText(programPath);
        var executeMethod = source.IndexOf(
            "private static async Task ExecuteAsync",
            StringComparison.Ordinal);
        var waitForDesktop = source.IndexOf(
            "await WaitForProcessExitAsync(options);",
            executeMethod,
            StringComparison.Ordinal);
        var deleteMetadata = source.IndexOf(
            "UpdateRequestMetadata.LoadAndDelete(",
            waitForDesktop,
            StringComparison.Ordinal);
        var cleanupArtifacts = source.IndexOf(
            "TryCleanupStaleUpdateArtifacts();",
            deleteMetadata,
            StringComparison.Ordinal);
        var generatedRecoveryProbe = source.IndexOf(
            "RequireGeneratedInstallRecoveryProbe(options.InstallRoot);",
            cleanupArtifacts,
            StringComparison.Ordinal);
        var versionGate = source.IndexOf(
            "GetInstalledVersionState(options",
            generatedRecoveryProbe,
            StringComparison.Ordinal);
        var ensureNoPendingBeforeSkip = source.IndexOf(
            "EnsureGeneratedInstallRecoveryAbsent(options.InstallRoot);",
            versionGate,
            StringComparison.Ordinal);
        var scheduleStagingCleanup = source.IndexOf(
            "SchedulePostExitCleanup(GetCurrentUpdaterStagingRoot());",
            ensureNoPendingBeforeSkip,
            StringComparison.Ordinal);
        var releaseGate = source.IndexOf(
            "installRootUpdateLock.Dispose();",
            scheduleStagingCleanup,
            StringComparison.Ordinal);
        var relaunch = source.IndexOf(
            "LaunchExistingDesktop(options);",
            releaseGate,
            StringComparison.Ordinal);

        Assert.True(executeMethod >= 0);
        Assert.True(waitForDesktop > executeMethod);
        Assert.True(deleteMetadata > waitForDesktop);
        Assert.True(cleanupArtifacts > deleteMetadata);
        Assert.True(generatedRecoveryProbe > cleanupArtifacts);
        Assert.True(versionGate > generatedRecoveryProbe);
        Assert.True(ensureNoPendingBeforeSkip > versionGate);
        Assert.True(scheduleStagingCleanup > ensureNoPendingBeforeSkip);
        Assert.True(releaseGate > scheduleStagingCleanup);
        Assert.True(relaunch > releaseGate);

        var verifyPackage = source.IndexOf(
            "await ExtractVerifiedPackageAsync(",
            versionGate,
            StringComparison.Ordinal);
        var resolveInstallScript = source.IndexOf(
            "var installScriptPath = Path.Combine(",
            verifyPackage,
            StringComparison.Ordinal);
        var recoverBeforeVersionDecision = source.IndexOf(
            "RecoverGeneratedInstallStateBeforeVersionDecisionAsync(",
            resolveInstallScript,
            StringComparison.Ordinal);
        var fullInstall = source.IndexOf(
            "await ExecuteInstallWithRollbackAsync(",
            recoverBeforeVersionDecision,
            StringComparison.Ordinal);
        Assert.True(verifyPackage > versionGate);
        Assert.True(resolveInstallScript > verifyPackage);
        Assert.True(recoverBeforeVersionDecision > resolveInstallScript);
        Assert.True(fullInstall > recoverBeforeVersionDecision);

        var cleanupRoot = Path.Combine(
            NewInstallRoot(),
            "staged updater with spaces");
        try
        {
            Directory.CreateDirectory(cleanupRoot);
            await File.WriteAllTextAsync(
                Path.Combine(cleanupRoot, "staged-updater.bin"),
                "cleanup");

            using var cleanupProcess =
                Program.SchedulePostExitCleanup(cleanupRoot)
                ?? throw new InvalidOperationException(
                    "Cleanup helper process was not started.");
            await cleanupProcess.WaitForExitAsync()
                .WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal(0, cleanupProcess.ExitCode);
            Assert.False(Directory.Exists(cleanupRoot));
        }
        finally
        {
            if (Directory.Exists(cleanupRoot))
                Directory.Delete(cleanupRoot, recursive: true);
        }
    }

    [Fact]
    public async Task HandoffPipe_BindsBothSidesToTheExpectedDesktopAndUpdaterProcesses()
    {
        Assert.Equal(
            DesktopAppUpdateService.UpdaterHandoffPipeNamePrefix,
            UpdateArguments.HandoffPipeNamePrefix);
        Assert.Equal(
            DesktopAppUpdateService.UpdaterIdentityVerifiedMarker,
            UpdaterHandoffProtocol.IdentityVerifiedMarker);

        var pipeName =
            DesktopAppUpdateService.UpdaterHandoffPipeNamePrefix +
            Guid.NewGuid().ToString("N");
        await using var handoffPipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.In,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var process = Process.GetCurrentProcess();
        var options = new UpdateArguments
        {
            ProcessId = process.Id,
            HandoffPipeName = pipeName
        };

        var desktopWait = DesktopAppUpdateService.WaitForUpdaterIdentityVerificationAsync(
            handoffPipe,
            process,
            TimeSpan.FromSeconds(5));
        await Program.SignalDesktopHandoffAsync(options);
        await desktopWait;
    }

    [Fact]
    public async Task HandoffPipe_RejectsAPipeNotOwnedByTheClaimedDesktopProcess()
    {
        var pipeName =
            DesktopAppUpdateService.UpdaterHandoffPipeNamePrefix +
            Guid.NewGuid().ToString("N");
        await using var handoffPipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.In,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var process = Process.GetCurrentProcess();
        var claimedDesktopProcessId =
            process.Id == int.MaxValue ? process.Id - 1 : process.Id + 1;
        var options = new UpdateArguments
        {
            ProcessId = claimedDesktopProcessId,
            HandoffPipeName = pipeName
        };

        var acceptConnection = handoffPipe.WaitForConnectionAsync();
        await Assert.ThrowsAsync<ProcessIdentityValidationException>(
            () => Program.SignalDesktopHandoffAsync(options));
        await acceptConnection.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task HandoffPipe_AcceptsTheExactLaunchedClientAcrossProcesses()
    {
        var testRoot = NewInstallRoot();
        var clientScript = Path.Combine(testRoot, "pipe-client.ps1");
        Directory.CreateDirectory(testRoot);
        await File.WriteAllTextAsync(
            clientScript,
            """
            param([string]$PipeName)
            $pipe = [System.IO.Pipes.NamedPipeClientStream]::new(
                '.',
                $PipeName,
                [System.IO.Pipes.PipeDirection]::Out,
                [System.IO.Pipes.PipeOptions]::Asynchronous)
            try {
                $pipe.Connect(5000)
                $pipe.WriteByte(165)
                $pipe.Flush()
            }
            finally {
                $pipe.Dispose()
            }
            """);
        var pipeName =
            DesktopAppUpdateService.UpdaterHandoffPipeNamePrefix +
            Guid.NewGuid().ToString("N");

        try
        {
            await using var handoffPipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.In,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var client = StartPowerShellScript(clientScript, pipeName);

            await DesktopAppUpdateService.WaitForUpdaterIdentityVerificationAsync(
                handoffPipe,
                client,
                TimeSpan.FromSeconds(5));
            await client.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, client.ExitCode);
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task HandoffPipe_VerifiesTheDesktopServerPidAcrossProcesses()
    {
        var testRoot = NewInstallRoot();
        var serverScript = Path.Combine(testRoot, "pipe-server.ps1");
        var readyPath = Path.Combine(testRoot, "server-ready.txt");
        Directory.CreateDirectory(testRoot);
        await File.WriteAllTextAsync(
            serverScript,
            """
            param([string]$PipeName, [string]$ReadyPath)
            $pipe = [System.IO.Pipes.NamedPipeServerStream]::new(
                $PipeName,
                [System.IO.Pipes.PipeDirection]::In,
                1,
                [System.IO.Pipes.PipeTransmissionMode]::Byte,
                [System.IO.Pipes.PipeOptions]::Asynchronous)
            try {
                [System.IO.File]::WriteAllText(
                    $ReadyPath,
                    [System.Diagnostics.Process]::GetCurrentProcess().Id.ToString())
                $pipe.WaitForConnection()
                if ($pipe.ReadByte() -ne 165) {
                    exit 7
                }
            }
            finally {
                $pipe.Dispose()
            }
            """);
        var pipeName =
            DesktopAppUpdateService.UpdaterHandoffPipeNamePrefix +
            Guid.NewGuid().ToString("N");

        Process? server = null;
        try
        {
            server = StartPowerShellScript(serverScript, pipeName, readyPath);
            await WaitForFileAsync(readyPath, TimeSpan.FromSeconds(5));
            var options = new UpdateArguments
            {
                ProcessId = server.Id,
                HandoffPipeName = pipeName
            };

            await Program.SignalDesktopHandoffAsync(options);
            await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, server.ExitCode);
        }
        finally
        {
            TryTerminateProcess(server);
            server?.Dispose();
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task HandoffPipe_RejectsWrongClientPreemptionAcrossProcesses()
    {
        var testRoot = NewInstallRoot();
        var clientScript = Path.Combine(testRoot, "wrong-client.ps1");
        var idleScript = Path.Combine(testRoot, "expected-updater.ps1");
        Directory.CreateDirectory(testRoot);
        await File.WriteAllTextAsync(
            clientScript,
            """
            param([string]$PipeName)
            $pipe = [System.IO.Pipes.NamedPipeClientStream]::new(
                '.',
                $PipeName,
                [System.IO.Pipes.PipeDirection]::Out)
            try {
                $pipe.Connect(5000)
                $pipe.WriteByte(165)
                $pipe.Flush()
            }
            finally {
                $pipe.Dispose()
            }
            """);
        await File.WriteAllTextAsync(
            idleScript,
            "Start-Sleep -Seconds 10");
        var pipeName =
            DesktopAppUpdateService.UpdaterHandoffPipeNamePrefix +
            Guid.NewGuid().ToString("N");

        Process? expectedUpdater = null;
        Process? wrongClient = null;
        try
        {
            await using var handoffPipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.In,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            expectedUpdater = StartPowerShellScript(idleScript);
            wrongClient = StartPowerShellScript(clientScript, pipeName);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => DesktopAppUpdateService.WaitForUpdaterIdentityVerificationAsync(
                    handoffPipe,
                    expectedUpdater,
                    TimeSpan.FromSeconds(5)));
            Assert.Contains("신원이 일치하지 않습니다", error.Message, StringComparison.Ordinal);
            TryTerminateProcess(wrongClient);
            await wrongClient.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            TryTerminateProcess(wrongClient);
            TryTerminateProcess(expectedUpdater);
            wrongClient?.Dispose();
            expectedUpdater?.Dispose();
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void DesktopUpdateCallers_ShutdownOnlyAfterTheVerifiedHandoffCompletes()
    {
        var repositoryRoot = FindRepositoryRoot();
        var serviceSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "Services",
            "DesktopAppUpdateService.cs"));
        var mainWindow = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "MainWindow.xaml.cs"));
        var mainUpdate = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "ViewModels",
            "MainViewModel.Update.cs"));
        var settingsUpdate = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "ViewModels",
            "EnvironmentSettingsViewModel.Update.cs"));

        AssertInOrder(
            serviceSource,
            "verifiedHandoffCompleted = true;",
            "_verifiedHandoffShutdownScheduler();");
        Assert.Contains(
            "await BackgroundDesktopUpdateService.StartUpdateAsync(",
            mainUpdate,
            StringComparison.Ordinal);
        Assert.Contains(
            "await _updateService.StartUpdateAsync(",
            settingsUpdate,
            StringComparison.Ordinal);
        Assert.Contains(
            "await _updateService.StartUpdateAsync(result.Package);",
            mainWindow,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "App.RequestShutdownForUpdate",
            mainUpdate,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "App.RequestShutdownForUpdate",
            settingsUpdate,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "App.RequestShutdownForUpdate",
            mainWindow,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopUpdateReadiness_ReportsPendingAndFailedOutboxCountsSeparately()
    {
        var repositoryRoot = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "Services",
            "UpdateReadinessService.cs"));

        Assert.Contains(
            "sync outbox 대기 {outboxSummary.PendingCount:N0}건과 실패 {outboxSummary.FailedCount:N0}건",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "(실패 {outboxSummary.FailedCount:N0}건 포함)",
            source,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2.0.0", "1.0.0", "3.0.0")]
    [InlineData("2.0.0", "2.0.0", "2.0.0")]
    [InlineData("2.0.0", "3.0.0", "4.0.0")]
    [InlineData("2.0.0", "invalid", "2.0.0")]
    public void DesktopUpdateVersionPolicy_RejectsDowngradesAndInconsistentMinimums(
        string currentVersion,
        string packageVersion,
        string minimumSupportedVersion)
    {
        Assert.False(DesktopAppUpdateService.IsPackageVersionPolicySatisfied(
            currentVersion,
            packageVersion,
            minimumSupportedVersion,
            out var failure));
        Assert.False(string.IsNullOrWhiteSpace(failure));
    }

    [Fact]
    public void DesktopUpdateVersionPolicy_AcceptsOnlyANewerConsistentPackage()
    {
        Assert.True(DesktopAppUpdateService.IsPackageVersionPolicySatisfied(
            "2.0.0",
            "3.0.0",
            "2.5.0",
            out var failure));
        Assert.Equal(string.Empty, failure);
    }

    [Theory]
    [InlineData("1.1.705", "1.1.706")]
    [InlineData("1.1.705.0", "1.1.706")]
    [InlineData("1.1.705", "1.1.706.0")]
    public void DesktopUpdateVersionPolicy_TreatsMissingRevisionAsZero(
        string currentVersion,
        string packageVersion)
    {
        Assert.True(DesktopAppUpdateService.IsPackageVersionPolicySatisfied(
            currentVersion,
            packageVersion,
            "1.1.705",
            out var failure));
        Assert.Equal(string.Empty, failure);
    }

    [Fact]
    public async Task ExtractVerifiedPackageAsync_RechecksHashAndExtractsFromTheVerifiedStream()
    {
        var testRoot = NewInstallRoot();
        var packagePath = Path.Combine(testRoot, "package.zip");
        var extractRoot = Path.Combine(testRoot, "extract");

        try
        {
            Directory.CreateDirectory(testRoot);
            await using (var stream = new FileStream(packagePath, FileMode.CreateNew))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("payload.txt");
                await using var writer = entry.Open();
                await writer.WriteAsync("trusted payload"u8.ToArray());
            }

            var packageBytes = await File.ReadAllBytesAsync(packagePath);
            var sha256 = Convert.ToHexString(SHA256.HashData(packageBytes));
            await Program.ExtractVerifiedPackageAsync(
                packagePath,
                extractRoot,
                sha256,
                packageBytes.LongLength);

            Assert.Equal(
                "trusted payload",
                await File.ReadAllTextAsync(Path.Combine(extractRoot, "payload.txt")));

            await File.AppendAllTextAsync(packagePath, "tampered");
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => Program.ExtractVerifiedPackageAsync(
                    packagePath,
                    Path.Combine(testRoot, "tampered-extract"),
                    sha256,
                    expectedFileSize: 0));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CopyArchiveEntryBoundedAsync_RejectsAStreamLongerThanItsDeclaration()
    {
        await using var source = new MemoryStream(new byte[9]);
        await using var destination = new MemoryStream();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Program.CopyArchiveEntryBoundedAsync(
                source,
                destination,
                "payload.bin",
                declaredLength: 8,
                totalBytesBeforeEntry: 0,
                maximumEntryBytes: 8,
                maximumTotalBytes: 32));

        Assert.Equal(0, destination.Length);
    }

    [Fact]
    public async Task CopyArchiveEntryBoundedAsync_EnforcesEntryAndTotalLimits()
    {
        await using var entrySource = new MemoryStream(new byte[5]);
        await using var entryDestination = new MemoryStream();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Program.CopyArchiveEntryBoundedAsync(
                entrySource,
                entryDestination,
                "entry-limit.bin",
                declaredLength: 5,
                totalBytesBeforeEntry: 0,
                maximumEntryBytes: 4,
                maximumTotalBytes: 20));

        await using var totalSource = new MemoryStream(new byte[4]);
        await using var totalDestination = new MemoryStream();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Program.CopyArchiveEntryBoundedAsync(
                totalSource,
                totalDestination,
                "total-limit.bin",
                declaredLength: 4,
                totalBytesBeforeEntry: 7,
                maximumEntryBytes: 10,
                maximumTotalBytes: 10));

        Assert.Equal(0, entryDestination.Length);
        Assert.Equal(0, totalDestination.Length);
    }

    [Fact]
    public async Task CopyArchiveEntryBoundedAsync_PropagatesDestinationWriteFailure()
    {
        await using var source = new MemoryStream("payload"u8.ToArray());
        await using var destination = new WriteFailingStream();

        var error = await Assert.ThrowsAsync<IOException>(() =>
            Program.CopyArchiveEntryBoundedAsync(
                source,
                destination,
                "write-failure.bin",
                declaredLength: source.Length,
                totalBytesBeforeEntry: 0,
                maximumEntryBytes: 32,
                maximumTotalBytes: 32));

        Assert.Contains("synthetic write failure", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractVerifiedPackageAsync_RejectsUnixSymlinkBeforeCreatingExtractionRoot()
    {
        var testRoot = NewInstallRoot();
        var packagePath = Path.Combine(testRoot, "package.zip");
        var extractRoot = Path.Combine(testRoot, "extract");

        try
        {
            Directory.CreateDirectory(testRoot);
            await using (var stream = new FileStream(packagePath, FileMode.CreateNew))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("App/link");
                entry.ExternalAttributes = unchecked((int)0xA0000000);
                await using var writer = entry.Open();
                await writer.WriteAsync("target.txt"u8.ToArray());
            }

            var packageBytes = await File.ReadAllBytesAsync(packagePath);
            var sha256 = Convert.ToHexString(SHA256.HashData(packageBytes));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Program.ExtractVerifiedPackageAsync(
                    packagePath,
                    extractRoot,
                    sha256,
                    packageBytes.LongLength));

            Assert.False(Directory.Exists(extractRoot));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExtractVerifiedPackageAsync_PreservesExtractionRootWhenEntryOpenFails()
    {
        var testRoot = NewInstallRoot();
        var packagePath = Path.Combine(testRoot, "package.zip");
        var extractRoot = Path.Combine(testRoot, "extract");

        try
        {
            Directory.CreateDirectory(testRoot);
            await using (var stream = new FileStream(packagePath, FileMode.CreateNew))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("payload.txt");
                await using var writer = entry.Open();
                await writer.WriteAsync("payload"u8.ToArray());
            }

            var packageBytes = await File.ReadAllBytesAsync(packagePath);
            SetUnsupportedCompressionMethod(packageBytes);
            await File.WriteAllBytesAsync(packagePath, packageBytes);
            var sha256 = Convert.ToHexString(SHA256.HashData(packageBytes));

            await Assert.ThrowsAnyAsync<Exception>(() =>
                Program.ExtractVerifiedPackageAsync(
                    packagePath,
                    extractRoot,
                    sha256,
                    packageBytes.LongLength));

            Assert.True(Directory.Exists(extractRoot));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void FailedExtractionPreservation_DoesNotTraverseAReplacedRootJunction()
    {
        var testRoot = NewInstallRoot();
        var outsideRoot = NewInstallRoot();
        var extractionRoot = Path.Combine(testRoot, "extract");
        var sentinelPath = Path.Combine(outsideRoot, "sentinel.txt");

        try
        {
            Directory.CreateDirectory(testRoot);
            Directory.CreateDirectory(outsideRoot);
            File.WriteAllText(sentinelPath, "preserve");
            Assert.True(TryCreateDirectoryJunction(extractionRoot, outsideRoot));

            var preserveMethod = typeof(Program).GetMethod(
                "PreserveFailedExtractionRoot",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static);
            var legacyDeleteMethod = typeof(Program).GetMethod(
                "DeleteFailedExtractionRoot",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static);
            Assert.NotNull(preserveMethod);
            Assert.Null(legacyDeleteMethod);
            preserveMethod!.Invoke(null, new object[] { extractionRoot });

            Assert.True(File.Exists(sentinelPath));
            Assert.True(Directory.Exists(extractionRoot));
        }
        finally
        {
            if (Directory.Exists(extractionRoot))
                Directory.Delete(extractionRoot, recursive: false);
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
            if (Directory.Exists(outsideRoot))
                Directory.Delete(outsideRoot, recursive: true);
        }
    }

    [Fact]
    public void FailedExtractionPreservation_DoesNotTraverseAReplacedChildJunction()
    {
        var testRoot = NewInstallRoot();
        var outsideRoot = NewInstallRoot();
        var extractionRoot = Path.Combine(testRoot, "extract");
        var childJunction = Path.Combine(extractionRoot, "child");
        var sentinelPath = Path.Combine(outsideRoot, "sentinel.txt");

        try
        {
            Directory.CreateDirectory(extractionRoot);
            Directory.CreateDirectory(outsideRoot);
            File.WriteAllText(sentinelPath, "preserve");
            Assert.True(TryCreateDirectoryJunction(childJunction, outsideRoot));

            var preserveMethod = typeof(Program).GetMethod(
                "PreserveFailedExtractionRoot",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static);
            Assert.NotNull(preserveMethod);
            preserveMethod!.Invoke(null, new object[] { extractionRoot });

            Assert.True(File.Exists(sentinelPath));
            Assert.True(Directory.Exists(childJunction));
        }
        finally
        {
            if (Directory.Exists(childJunction))
                Directory.Delete(childJunction, recursive: false);
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
            if (Directory.Exists(outsideRoot))
                Directory.Delete(outsideRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExtractVerifiedPackageAsync_RejectsEncryptedFlagBeforeSpaceCheckOrExtractionRoot()
    {
        var testRoot = NewInstallRoot();
        var packagePath = Path.Combine(testRoot, "package.zip");
        var extractRoot = Path.Combine(testRoot, "extract");
        var spaceCheckCalls = 0;

        try
        {
            Directory.CreateDirectory(testRoot);
            await using (var stream = new FileStream(packagePath, FileMode.CreateNew))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("payload.txt");
                await using var writer = entry.Open();
                await writer.WriteAsync("payload"u8.ToArray());
            }

            var packageBytes = await File.ReadAllBytesAsync(packagePath);
            SetArchiveFlags(packageBytes, flags => (ushort)(flags | 1));
            await File.WriteAllBytesAsync(packagePath, packageBytes);
            var sha256 = Convert.ToHexString(SHA256.HashData(packageBytes));

            await Assert.ThrowsAnyAsync<Exception>(() =>
                Program.ExtractVerifiedPackageAsync(
                    packagePath,
                    extractRoot,
                    sha256,
                    packageBytes.LongLength,
                    _ =>
                    {
                        spaceCheckCalls++;
                        return long.MaxValue;
                    }));

            Assert.Equal(0, spaceCheckCalls);
            Assert.False(Directory.Exists(extractRoot));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("multi-disk")]
    [InlineData("zip64")]
    [InlineData("entry-count")]
    [InlineData("directory-overflow")]
    [InlineData("directory-end-mismatch")]
    [InlineData("central-parse-end")]
    [InlineData("entry-disk-start")]
    [InlineData("trailing-data")]
    public async Task ExtractVerifiedPackageAsync_RejectsInvalidRawZipDirectoryBeforeExtraction(
        string mutation)
    {
        var testRoot = NewInstallRoot();
        var packagePath = Path.Combine(testRoot, "package.zip");
        var extractRoot = Path.Combine(testRoot, "extract");
        var spaceCheckCalls = 0;

        try
        {
            Directory.CreateDirectory(testRoot);
            var packageBytes = await CreatePackageBytesAsync(
                "payload.txt",
                "payload"u8.ToArray(),
                forceDataDescriptor: false);
            packageBytes = MutateRawZipDirectory(packageBytes, mutation);
            await File.WriteAllBytesAsync(packagePath, packageBytes);
            var sha256 = Convert.ToHexString(SHA256.HashData(packageBytes));

            await Assert.ThrowsAnyAsync<Exception>(() =>
                Program.ExtractVerifiedPackageAsync(
                    packagePath,
                    extractRoot,
                    sha256,
                    packageBytes.LongLength,
                    _ =>
                    {
                        spaceCheckCalls++;
                        return long.MaxValue;
                    }));

            Assert.Equal(0, spaceCheckCalls);
            Assert.False(Directory.Exists(extractRoot));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExtractVerifiedPackageAsync_AcceptsDataDescriptorUtf8AndSignaturesInZipComment()
    {
        var testRoot = NewInstallRoot();
        var packagePath = Path.Combine(testRoot, "package.zip");
        var extractRoot = Path.Combine(testRoot, "extract");
        const string entryName = "자료/업데이트-✓.txt";

        try
        {
            Directory.CreateDirectory(testRoot);
            var packageBytes = await CreatePackageBytesAsync(
                entryName,
                "trusted payload"u8.ToArray(),
                forceDataDescriptor: true);
            var localHeaderOffset = FindSignatureOffset(
                packageBytes,
                0x04034B50,
                useLast: false);
            var localFlags = BinaryPrimitives.ReadUInt16LittleEndian(
                packageBytes.AsSpan(localHeaderOffset + 6, sizeof(ushort)));
            Assert.NotEqual(0, localFlags & (1 << 3));
            Assert.NotEqual(0, localFlags & (1 << 11));

            packageBytes = SetZipComment(
                packageBytes,
                new byte[]
                {
                    (byte)'c', 0x50, 0x4B, 0x05, 0x06,
                    (byte)'x', 0x50, 0x4B, 0x01, 0x02, (byte)'!'
                });
            await File.WriteAllBytesAsync(packagePath, packageBytes);
            var sha256 = Convert.ToHexString(SHA256.HashData(packageBytes));

            await Program.ExtractVerifiedPackageAsync(
                packagePath,
                extractRoot,
                sha256,
                packageBytes.LongLength,
                _ => long.MaxValue);

            Assert.Equal(
                "trusted payload",
                await File.ReadAllTextAsync(Path.Combine(
                    extractRoot,
                    "자료",
                    "업데이트-✓.txt")));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData(0x0001)]
    [InlineData(0x9901)]
    public void ValidateZipExtraFields_RejectsZip64AndAesMetadata(int headerId)
    {
        var extraField = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(
            extraField,
            checked((ushort)headerId));

        Assert.Throws<InvalidOperationException>(() =>
            Program.ValidateZipExtraFields(
                extraField,
                "payload.bin"u8));
    }

    [Fact]
    public void ValidatePackageArchiveEntries_RejectsUnsafeWindowsPathsAndAliases()
    {
        var canonical = new Program.PackageArchiveEntryMetadata(
            "App/same.txt",
            1);
        var unsafePaths = new[]
        {
            "App/./same.txt",
            "App/sub/../same.txt",
            "App//same.txt",
            "app/SAME.txt",
            @"App\same.txt",
            "/rooted.txt",
            @"C:\rooted.txt",
            @"\\server\share\rooted.txt",
            "../outside.txt",
            "App/file.txt:stream",
            "App/file.",
            "App/file ",
            "App/CON",
            "App/com1.txt",
            "App/COM\u00B9.txt",
            "App/com\u00B2.log",
            "App/CoM\u00B3",
            "App/LPT\u00B9.txt",
            "App/lpt\u00B2.log",
            "App/LpT\u00B3",
            "App/control\u0001.txt",
            "App/" + new string(
                'a',
                Program.MaximumArchivePathSegmentLength + 1),
            string.Join(
                '/',
                Enumerable.Repeat(
                    "segment",
                    Program.MaximumArchivePathLength / 4))
        };

        foreach (var unsafePath in unsafePaths)
        {
            Assert.Throws<InvalidOperationException>(() =>
                Program.ValidatePackageArchiveEntries(
                    new[]
                    {
                        canonical,
                        new Program.PackageArchiveEntryMetadata(
                            unsafePath,
                            1)
                    },
                    packageSize: 1));
        }
    }

    [Fact]
    public void ValidatePackageArchiveEntries_ReturnsUncompressedTotalForSpacePreflight()
    {
        const long compressedPackageBytes = 1_024;
        const long compressedSizeBasedMinimum = 512L * 1024 * 1024;
        const long firstEntryBytes = 400L * 1024 * 1024;
        const long secondEntryBytes = 300L * 1024 * 1024;
        const long availableFreeSpace = 600L * 1024 * 1024;

        var totalUncompressedBytes = Program.ValidatePackageArchiveEntries(
            new[]
            {
                new Program.PackageArchiveEntryMetadata(
                    "App/first.bin",
                    firstEntryBytes),
                new Program.PackageArchiveEntryMetadata(
                    "App/second.bin",
                    secondEntryBytes)
            },
            compressedPackageBytes);

        Assert.Equal(
            firstEntryBytes + secondEntryBytes,
            totalUncompressedBytes);
        Assert.True(
            availableFreeSpace >= Math.Max(
                compressedSizeBasedMinimum,
                compressedPackageBytes * 4),
            "The test seam must represent space that the compressed-size check would accept.");
        Assert.Throws<InvalidOperationException>(() =>
            Program.EnsureWorkDriveFreeSpaceForExtraction(
                NewInstallRoot(),
                totalUncompressedBytes,
                _ => availableFreeSpace));
    }

    [Fact]
    public void ValidatePackageArchiveEntries_RejectsFileDirectoryAncestorConflicts()
    {
        var conflictingArchives = new[]
        {
            new[]
            {
                new Program.PackageArchiveEntryMetadata("App", 1),
                new Program.PackageArchiveEntryMetadata("App/child.txt", 1)
            },
            new[]
            {
                new Program.PackageArchiveEntryMetadata("App/sub/child.txt", 1),
                new Program.PackageArchiveEntryMetadata("app/sub", 1)
            },
            new[]
            {
                new Program.PackageArchiveEntryMetadata("App/", 0),
                new Program.PackageArchiveEntryMetadata("app", 1)
            }
        };

        foreach (var entries in conflictingArchives)
        {
            Assert.Throws<InvalidOperationException>(() =>
                Program.ValidatePackageArchiveEntries(entries, packageSize: 1));
        }
    }

    [Fact]
    public void ValidatePackageArchiveEntries_RejectsBoundViolationsWithoutLargePayloads()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Program.ValidatePackageArchiveEntries(
                Array.Empty<Program.PackageArchiveEntryMetadata>(),
                Program.MaximumUpdatePackageBytes + 1));

        var tooManyEntries = Enumerable.Range(
                0,
                Program.MaximumArchiveEntryCount + 1)
            .Select(index => new Program.PackageArchiveEntryMetadata(
                $"App/{index}.txt",
                0));
        Assert.Throws<InvalidOperationException>(() =>
            Program.ValidatePackageArchiveEntries(
                tooManyEntries,
                packageSize: 1));

        Assert.Throws<InvalidOperationException>(() =>
            Program.ValidatePackageArchiveEntries(
                new[]
                {
                    new Program.PackageArchiveEntryMetadata(
                        "large.bin",
                        Program.MaximumArchiveEntryBytes + 1)
                },
                packageSize: 1));

        var excessiveTotal = Enumerable.Range(0, 5)
            .Select(index => new Program.PackageArchiveEntryMetadata(
                $"App/{index}.bin",
                index < 4 ? Program.MaximumArchiveEntryBytes : 1));
        Assert.Throws<InvalidOperationException>(() =>
            Program.ValidatePackageArchiveEntries(
                excessiveTotal,
                packageSize: 1));
    }

    [Fact]
    public void ValidatePackageArchiveEntries_RejectsReparseMetadata()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Program.ValidatePackageArchiveEntries(
                new[]
                {
                    new Program.PackageArchiveEntryMetadata(
                        "App/link",
                        0,
                        (int)FileAttributes.ReparsePoint)
                },
                packageSize: 1));
    }

    [Theory]
    [InlineData("App/COM\u00B9.txt")]
    [InlineData("App/COM\u00B2.txt")]
    [InlineData("App/COM\u00B3.txt")]
    [InlineData("App/LPT\u00B9.txt")]
    [InlineData("App/LPT\u00B2.txt")]
    [InlineData("App/LPT\u00B3.txt")]
    public async Task ExtractVerifiedPackageAsync_RejectsSuperscriptDeviceAliasBeforeCreatingExtractionRoot(
        string entryName)
    {
        var testRoot = NewInstallRoot();
        var packagePath = Path.Combine(testRoot, "package.zip");
        var extractRoot = Path.Combine(testRoot, "extract");

        try
        {
            Directory.CreateDirectory(testRoot);
            await using (var stream = new FileStream(packagePath, FileMode.CreateNew))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry(entryName);
                await using var writer = entry.Open();
                await writer.WriteAsync("payload"u8.ToArray());
            }

            var packageBytes = await File.ReadAllBytesAsync(packagePath);
            var sha256 = Convert.ToHexString(SHA256.HashData(packageBytes));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Program.ExtractVerifiedPackageAsync(
                    packagePath,
                    extractRoot,
                    sha256,
                    packageBytes.LongLength));

            Assert.False(Directory.Exists(extractRoot));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExtractVerifiedPackageAsync_RejectsCaseAliasBeforeCreatingExtractionRoot()
    {
        var testRoot = NewInstallRoot();
        var packagePath = Path.Combine(testRoot, "package.zip");
        var extractRoot = Path.Combine(testRoot, "extract");

        try
        {
            Directory.CreateDirectory(testRoot);
            await using (var stream = new FileStream(packagePath, FileMode.CreateNew))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (var entryName in new[]
                         {
                             "App/same.txt",
                             "app/SAME.txt"
                         })
                {
                    var entry = archive.CreateEntry(entryName);
                    await using var writer = entry.Open();
                    await writer.WriteAsync("payload"u8.ToArray());
                }
            }

            var packageBytes = await File.ReadAllBytesAsync(packagePath);
            var sha256 = Convert.ToHexString(SHA256.HashData(packageBytes));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Program.ExtractVerifiedPackageAsync(
                    packagePath,
                    extractRoot,
                    sha256,
                    packageBytes.LongLength));

            Assert.False(Directory.Exists(extractRoot));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExtractVerifiedPackageAsync_ChecksUncompressedSpaceBeforeCreatingExtractionRoot()
    {
        var testRoot = NewInstallRoot();
        var packagePath = Path.Combine(testRoot, "package.zip");
        var extractRoot = Path.Combine(testRoot, "extract");
        var spaceCheckCalls = 0;

        try
        {
            Directory.CreateDirectory(testRoot);
            await using (var stream = new FileStream(packagePath, FileMode.CreateNew))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry(
                    "payload.txt",
                    CompressionLevel.SmallestSize);
                await using var writer = entry.Open();
                await writer.WriteAsync("compressible payload"u8.ToArray());
            }

            var packageBytes = await File.ReadAllBytesAsync(packagePath);
            var sha256 = Convert.ToHexString(SHA256.HashData(packageBytes));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Program.ExtractVerifiedPackageAsync(
                    packagePath,
                    extractRoot,
                    sha256,
                    packageBytes.LongLength,
                    _ =>
                    {
                        Assert.False(Directory.Exists(extractRoot));
                        spaceCheckCalls++;
                        return 0;
                    }));

            Assert.Equal(1, spaceCheckCalls);
            Assert.False(Directory.Exists(extractRoot));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExtractVerifiedPackageAsync_RejectsPreexistingExtractionRootWithoutOverwrite()
    {
        var testRoot = NewInstallRoot();
        var packagePath = Path.Combine(testRoot, "package.zip");
        var extractRoot = Path.Combine(testRoot, "extract");
        var existingPath = Path.Combine(extractRoot, "existing.txt");

        try
        {
            Directory.CreateDirectory(extractRoot);
            await File.WriteAllTextAsync(existingPath, "keep me");
            await using (var stream = new FileStream(packagePath, FileMode.CreateNew))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("payload.txt");
                await using var writer = entry.Open();
                await writer.WriteAsync("trusted payload"u8.ToArray());
            }

            var packageBytes = await File.ReadAllBytesAsync(packagePath);
            var sha256 = Convert.ToHexString(SHA256.HashData(packageBytes));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Program.ExtractVerifiedPackageAsync(
                    packagePath,
                    extractRoot,
                    sha256,
                    packageBytes.LongLength));

            Assert.Equal("keep me", await File.ReadAllTextAsync(existingPath));
            Assert.False(File.Exists(Path.Combine(extractRoot, "payload.txt")));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void Program_HoldsTheVerifiedPackageHandleThroughZipExtraction()
    {
        var programPath = Path.Combine(
            FindRepositoryRoot(),
            "Updater",
            "거래플랜.Updater",
            "Program.cs");
        var source = File.ReadAllText(programPath);
        var methodStart = source.IndexOf(
            "internal static async Task ExtractVerifiedPackageAsync(",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "private static void VerifyExpectedPackageFileSize(",
            methodStart,
            StringComparison.Ordinal);
        Assert.True(methodStart >= 0 && methodEnd > methodStart);
        var method = source[methodStart..methodEnd];

        Assert.Contains("FileShare.Read", method, StringComparison.Ordinal);
        Assert.Contains("await VerifySha256Async(packageStream, sha256);", method, StringComparison.Ordinal);
        Assert.Contains("new ZipArchive(packageStream, ZipArchiveMode.Read", method, StringComparison.Ordinal);
        AssertInOrder(
            method,
            "ValidateRawPackageArchiveStructure(packageStream)",
            "var archiveEntries = archive.Entries;");
        AssertInOrder(
            method,
            "ValidatePackageArchiveEntries(",
            "EnsureWorkDriveFreeSpaceForExtraction(",
            "PrepareEmptyExtractionRoot(extractRoot)",
            "ExtractPackageArchiveEntriesAsync(");
        Assert.Contains("PreserveFailedExtractionRoot(safeExtractRoot);", method, StringComparison.Ordinal);
        Assert.DoesNotContain("DeleteFailedExtractionRoot", method, StringComparison.Ordinal);
        Assert.DoesNotContain("archive.ExtractToDirectory", method, StringComparison.Ordinal);
        Assert.DoesNotContain("ZipFile.ExtractToDirectory", method, StringComparison.Ordinal);
    }

    private static string NewInstallRoot()
        => Path.Combine(
            @"D:\DevCaches\georaeplan-v1-tests\updater-transaction-safety",
            Guid.NewGuid().ToString("N"));

    private static async Task<byte[]> CreatePackageBytesAsync(
        string entryName,
        byte[] payload,
        bool forceDataDescriptor)
    {
        using var output = new MemoryStream();
        using var nonSeekableOutput = forceDataDescriptor
            ? new NonSeekableWriteStream(output)
            : null;
        var archiveOutput = (Stream?)nonSeekableOutput ?? output;
        using (var archive = new ZipArchive(
                   archiveOutput,
                   ZipArchiveMode.Create,
                   leaveOpen: true))
        {
            var entry = archive.CreateEntry(entryName);
            await using var writer = entry.Open();
            await writer.WriteAsync(payload);
        }

        return output.ToArray();
    }

    private static byte[] MutateRawZipDirectory(
        byte[] archiveBytes,
        string mutation)
    {
        var endOffset = FindEndOfCentralDirectoryOffset(archiveBytes);
        switch (mutation)
        {
            case "multi-disk":
                BinaryPrimitives.WriteUInt16LittleEndian(
                    archiveBytes.AsSpan(endOffset + 4, sizeof(ushort)),
                    1);
                return archiveBytes;
            case "zip64":
                BinaryPrimitives.WriteUInt16LittleEndian(
                    archiveBytes.AsSpan(endOffset + 8, sizeof(ushort)),
                    ushort.MaxValue);
                BinaryPrimitives.WriteUInt16LittleEndian(
                    archiveBytes.AsSpan(endOffset + 10, sizeof(ushort)),
                    ushort.MaxValue);
                return archiveBytes;
            case "entry-count":
                BinaryPrimitives.WriteUInt16LittleEndian(
                    archiveBytes.AsSpan(endOffset + 8, sizeof(ushort)),
                    Program.MaximumArchiveEntryCount + 1);
                BinaryPrimitives.WriteUInt16LittleEndian(
                    archiveBytes.AsSpan(endOffset + 10, sizeof(ushort)),
                    Program.MaximumArchiveEntryCount + 1);
                return archiveBytes;
            case "directory-overflow":
                BinaryPrimitives.WriteUInt32LittleEndian(
                    archiveBytes.AsSpan(endOffset + 12, sizeof(uint)),
                    128);
                BinaryPrimitives.WriteUInt32LittleEndian(
                    archiveBytes.AsSpan(endOffset + 16, sizeof(uint)),
                    uint.MaxValue - 1);
                return archiveBytes;
            case "directory-end-mismatch":
                var directorySize = BinaryPrimitives.ReadUInt32LittleEndian(
                    archiveBytes.AsSpan(endOffset + 12, sizeof(uint)));
                BinaryPrimitives.WriteUInt32LittleEndian(
                    archiveBytes.AsSpan(endOffset + 12, sizeof(uint)),
                    checked(directorySize + 1));
                return archiveBytes;
            case "central-parse-end":
                var expanded = new byte[archiveBytes.Length + 4];
                archiveBytes.AsSpan(0, endOffset).CopyTo(expanded);
                new byte[] { 0x01, 0x02, 0x03, 0x04 }
                    .CopyTo(expanded, endOffset);
                archiveBytes.AsSpan(endOffset).CopyTo(expanded.AsSpan(endOffset + 4));
                var expandedEndOffset = endOffset + 4;
                var originalDirectorySize = BinaryPrimitives.ReadUInt32LittleEndian(
                    expanded.AsSpan(expandedEndOffset + 12, sizeof(uint)));
                BinaryPrimitives.WriteUInt32LittleEndian(
                    expanded.AsSpan(expandedEndOffset + 12, sizeof(uint)),
                    checked(originalDirectorySize + 4));
                return expanded;
            case "entry-disk-start":
                var centralOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
                    archiveBytes.AsSpan(endOffset + 16, sizeof(uint))));
                BinaryPrimitives.WriteUInt16LittleEndian(
                    archiveBytes.AsSpan(centralOffset + 34, sizeof(ushort)),
                    1);
                return archiveBytes;
            case "trailing-data":
                var withTrailingData = new byte[archiveBytes.Length + 1];
                archiveBytes.CopyTo(withTrailingData, 0);
                withTrailingData[^1] = 0x7F;
                return withTrailingData;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
        }
    }

    private static byte[] SetZipComment(byte[] archiveBytes, byte[] comment)
    {
        Assert.True(comment.Length <= ushort.MaxValue);
        var endOffset = FindEndOfCentralDirectoryOffset(archiveBytes);
        Assert.Equal(archiveBytes.Length, endOffset + 22);
        var withComment = new byte[archiveBytes.Length + comment.Length];
        archiveBytes.CopyTo(withComment, 0);
        comment.CopyTo(withComment, archiveBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(
            withComment.AsSpan(endOffset + 20, sizeof(ushort)),
            checked((ushort)comment.Length));
        return withComment;
    }

    private static int FindEndOfCentralDirectoryOffset(byte[] archiveBytes)
    {
        for (var offset = archiveBytes.Length - 22; offset >= 0; offset--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(
                    archiveBytes.AsSpan(offset, sizeof(uint))) != 0x06054B50)
            {
                continue;
            }

            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(
                archiveBytes.AsSpan(offset + 20, sizeof(ushort)));
            if (offset + 22 + commentLength == archiveBytes.Length)
                return offset;
        }

        throw new InvalidOperationException("Test ZIP EOCD was not found.");
    }

    private static int FindSignatureOffset(
        byte[] archiveBytes,
        uint signature,
        bool useLast)
    {
        Span<byte> signatureBytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(signatureBytes, signature);
        var offset = useLast
            ? archiveBytes.AsSpan().LastIndexOf(signatureBytes)
            : archiveBytes.AsSpan().IndexOf(signatureBytes);
        Assert.True(offset >= 0);
        return offset;
    }

    private static void SetCentralDirectoryUncompressedLength(
        byte[] archiveBytes,
        uint uncompressedLength)
    {
        ReadOnlySpan<byte> centralDirectorySignature =
            stackalloc byte[] { 0x50, 0x4B, 0x01, 0x02 };
        var headerOffset = archiveBytes.AsSpan().LastIndexOf(
            centralDirectorySignature);
        Assert.True(headerOffset >= 0);
        BinaryPrimitives.WriteUInt32LittleEndian(
            archiveBytes.AsSpan(headerOffset + 24, sizeof(uint)),
            uncompressedLength);
    }

    private static void SetUnsupportedCompressionMethod(byte[] archiveBytes)
    {
        const ushort unsupportedCompressionMethod = 98;
        ReadOnlySpan<byte> localHeaderSignature =
            stackalloc byte[] { 0x50, 0x4B, 0x03, 0x04 };
        ReadOnlySpan<byte> centralDirectorySignature =
            stackalloc byte[] { 0x50, 0x4B, 0x01, 0x02 };
        var localHeaderOffset = archiveBytes.AsSpan().IndexOf(localHeaderSignature);
        var centralHeaderOffset = archiveBytes.AsSpan().LastIndexOf(
            centralDirectorySignature);
        Assert.True(localHeaderOffset >= 0);
        Assert.True(centralHeaderOffset >= 0);
        BinaryPrimitives.WriteUInt16LittleEndian(
            archiveBytes.AsSpan(localHeaderOffset + 8, sizeof(ushort)),
            unsupportedCompressionMethod);
        BinaryPrimitives.WriteUInt16LittleEndian(
            archiveBytes.AsSpan(centralHeaderOffset + 10, sizeof(ushort)),
            unsupportedCompressionMethod);
    }

    private static void SetArchiveFlags(
        byte[] archiveBytes,
        Func<ushort, ushort> transform)
    {
        ReadOnlySpan<byte> localHeaderSignature =
            stackalloc byte[] { 0x50, 0x4B, 0x03, 0x04 };
        ReadOnlySpan<byte> centralDirectorySignature =
            stackalloc byte[] { 0x50, 0x4B, 0x01, 0x02 };
        var localHeaderOffset = archiveBytes.AsSpan().IndexOf(localHeaderSignature);
        var centralHeaderOffset = archiveBytes.AsSpan().LastIndexOf(
            centralDirectorySignature);
        Assert.True(localHeaderOffset >= 0);
        Assert.True(centralHeaderOffset >= 0);
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(
            archiveBytes.AsSpan(localHeaderOffset + 6, sizeof(ushort)));
        var transformed = transform(flags);
        BinaryPrimitives.WriteUInt16LittleEndian(
            archiveBytes.AsSpan(localHeaderOffset + 6, sizeof(ushort)),
            transformed);
        BinaryPrimitives.WriteUInt16LittleEndian(
            archiveBytes.AsSpan(centralHeaderOffset + 8, sizeof(ushort)),
            transformed);
    }

    private static bool TryCreateDirectoryJunction(
        string junctionPath,
        string targetPath)
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
        process.WaitForExit();
        return process.ExitCode == 0 &&
               Directory.Exists(junctionPath);
    }

    private static async Task<GeneratedInstallerPackage>
        BuildGeneratedInstallerPackageAsync(
            string repositoryRoot,
            string testRoot,
            bool enableTestHooks = true)
    {
        var builderPath = Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Build-GeoraePlanDesktopInstaller.ps1");
        var fakeProjectRoot = Path.Combine(
            testRoot,
            "project");
        var sourceFolder = Path.Combine(
            testRoot,
            "source");
        var outputRoot = Path.Combine(
            testRoot,
            "output");
        Directory.CreateDirectory(
            Path.Combine(fakeProjectRoot, "deploy"));
        await File.WriteAllTextAsync(
            Path.Combine(
                fakeProjectRoot,
                "deploy",
                "Set-ApiBaseUrl.ps1"),
            "# test deployment marker");

        var repositoryDesktopRoot = Path.Combine(
            repositoryRoot,
            "Desktop");
        var desktopProjectPath = Directory.EnumerateFiles(
                repositoryDesktopRoot,
                "*.Desktop.App.csproj",
                SearchOption.AllDirectories)
            .Single();
        var desktopProjectRelativePath = Path.GetRelativePath(
            repositoryDesktopRoot,
            desktopProjectPath);
        var fakeDesktopProjectPath = Path.Combine(
            fakeProjectRoot,
            "Desktop",
            desktopProjectRelativePath);
        Directory.CreateDirectory(
            Path.GetDirectoryName(fakeDesktopProjectPath)
            ?? throw new InvalidOperationException(
                "Fake desktop project directory was not resolved."));
        var desktopProjectName =
            Path.GetFileNameWithoutExtension(desktopProjectPath);
        const string desktopSuffix = ".Desktop.App";
        Assert.EndsWith(
            desktopSuffix,
            desktopProjectName,
            StringComparison.Ordinal);
        var appDisplayName = desktopProjectName[
            ..^desktopSuffix.Length];
        var versionedDesktopFixturePath =
            typeof(Program).Assembly.Location;
        var versionedDesktopFixtureProductVersion =
            FileVersionInfo.GetVersionInfo(
                versionedDesktopFixturePath).ProductVersion
            ?? throw new InvalidOperationException(
                "Versioned desktop fixture has no ProductVersion.");
        var desktopFixtureVersion =
            versionedDesktopFixtureProductVersion
                .Split('+')[0]
                .Split('-')[0]
                .Trim()
                .TrimStart('v', 'V');
        Assert.True(
            Version.TryParse(desktopFixtureVersion, out _),
            $"Invalid desktop fixture ProductVersion: {versionedDesktopFixtureProductVersion}");
        await File.WriteAllTextAsync(
            fakeDesktopProjectPath,
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <Version>{desktopFixtureVersion}</Version>
              </PropertyGroup>
            </Project>
            """);
        Directory.CreateDirectory(
            Path.Combine(sourceFolder, "Updater"));
        await File.WriteAllTextAsync(
            Path.Combine(sourceFolder, "appsettings.json"),
            "{\"Api\":{\"BaseUrl\":\"https://example.invalid/new\"}}");
        File.Copy(
            versionedDesktopFixturePath,
            Path.Combine(
                sourceFolder,
                appDisplayName + ".exe"),
            overwrite: true);
        await File.WriteAllTextAsync(
            Path.Combine(
                sourceFolder,
                "Updater",
                appDisplayName + ".Updater.exe"),
            "new updater");

        var fakeDotnetPath = Path.Combine(
            testRoot,
            "fake-dotnet.cmd");
        await File.WriteAllTextAsync(
            fakeDotnetPath,
            """
            @echo off
            if "%~1"=="--version" (
              echo 8.0.100
              exit /b 0
            )
            exit /b 0
            """);

        var builderArguments = new List<string>
        {
            "-ProjectRoot",
            fakeProjectRoot,
            "-SourceFolder",
            sourceFolder,
            "-OutputRoot",
            outputRoot
        };
        if (enableTestHooks)
            builderArguments.Add("-EnableTestHooks");
        builderArguments.Add("-SkipNativeInstallers");
        var buildResult = await RunPowerShellAsync(
            builderPath,
            new Dictionary<string, string?>
            {
                ["DOTNET_EXE"] = fakeDotnetPath
            },
            TimeSpan.FromMinutes(2),
            builderArguments.ToArray());
        Assert.True(
            buildResult.ExitCode == 0,
            buildResult.StdOut + Environment.NewLine +
            buildResult.StdErr);

        var installScriptPath = Assert.Single(
            Directory.EnumerateFiles(
                outputRoot,
                "Install-GeoraePlan.ps1",
                SearchOption.AllDirectories));
        var generatedScript = await File.ReadAllTextAsync(
            installScriptPath);
        Assert.Contains(
            "GEORAEPLAN_INSTALL_SUPERVISOR_CONTRACT_V1",
            generatedScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "GEORAEPLAN_INSTALL_RECOVERY_ONLY_CONTRACT_V1",
            generatedScript,
            StringComparison.Ordinal);
        Assert.Contains(
            "[switch]$RecoveryOnly",
            generatedScript,
            StringComparison.Ordinal);
        return new GeneratedInstallerPackage(
            installScriptPath,
            appDisplayName,
            desktopFixtureVersion);
    }

    private static AppUpdatePackageDto CreateUpdatePackage(
        string version,
        string minimumSupportedVersion,
        bool mandatory = false)
        => new()
        {
            Platform = "desktop",
            Version = version,
            MinimumSupportedVersion = minimumSupportedVersion,
            Mandatory = mandatory,
            PackageUrl = "https://localhost/desktop.zip",
            FileName = "desktop.zip",
            Sha256 = new string('A', 64),
            FileSize = 1024
        };

    private static async Task RewriteGeneratedManagedRootsAsync(
        GeneratedInstallerPackage generatedInstaller,
        string isolatedRoot)
    {
        var scriptText = await File.ReadAllTextAsync(
            generatedInstaller.InstallScriptPath);
        const string canonicalAssignment =
            "$CanonicalInstallRoot = Join-Path $programFilesRoot 'tradeplan'";
        var legacyAssignment =
            $"$LegacyUserRoot = Join-Path $env:LOCALAPPDATA 'Programs\\{generatedInstaller.AppDisplayName}'";
        Assert.Contains(
            canonicalAssignment,
            scriptText,
            StringComparison.Ordinal);
        Assert.Contains(
            legacyAssignment,
            scriptText,
            StringComparison.Ordinal);
        scriptText = scriptText
            .Replace(
                canonicalAssignment,
                $"$CanonicalInstallRoot = '{Path.Combine(isolatedRoot, "canonical").Replace("'", "''")}'",
                StringComparison.Ordinal)
            .Replace(
                legacyAssignment,
                $"$LegacyUserRoot = '{Path.Combine(isolatedRoot, "legacy").Replace("'", "''")}'",
                StringComparison.Ordinal);
        await File.WriteAllTextAsync(
            generatedInstaller.InstallScriptPath,
            scriptText,
            new System.Text.UTF8Encoding(
                encoderShouldEmitUTF8Identifier: true));
    }

    private static async Task CreateShellShortcutAsync(
        string testRoot,
        string shortcutPath,
        string targetPath,
        string workingDirectory,
        string arguments = "")
    {
        var helperPath = Path.Combine(
            testRoot,
            "create-test-shortcut.ps1");
        if (!File.Exists(helperPath))
        {
            await File.WriteAllTextAsync(
                helperPath,
                """
                param(
                    [string]$ShortcutPath,
                    [string]$TargetPath,
                    [string]$WorkingDirectory,
                    [string]$Arguments = ''
                )
                $ErrorActionPreference = 'Stop'
                New-Item -ItemType Directory -Force -Path (
                    Split-Path -Parent $ShortcutPath
                ) | Out-Null
                $shell = New-Object -ComObject WScript.Shell
                $shortcut = $shell.CreateShortcut($ShortcutPath)
                $shortcut.TargetPath = $TargetPath
                $shortcut.WorkingDirectory = $WorkingDirectory
                $shortcut.Arguments = $Arguments
                $shortcut.Save()
                """,
                new System.Text.UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: true));
        }

        var result = await RunPowerShellAsync(
            helperPath,
            new Dictionary<string, string?>(),
            TimeSpan.FromSeconds(15),
            "-ShortcutPath",
            shortcutPath,
            "-TargetPath",
            targetPath,
            "-WorkingDirectory",
            workingDirectory,
            "-Arguments",
            arguments);
        Assert.True(
            result.ExitCode == 0,
            result.StdOut + Environment.NewLine +
            result.StdErr);
    }

    private static string GetGeneratedHangWorkerPidPath(
        GeneratedInstallerPackage generatedInstaller)
        => Path.Combine(
            Path.GetDirectoryName(
                generatedInstaller.InstallScriptPath)
                ?? throw new InvalidOperationException(
                    "Generated package root was not resolved."),
            ".georaeplan-installer-hang-worker.pid");

    private static string[] CaptureInstallTreeManifest(
        string rootPath)
    {
        var fullRoot = Path.GetFullPath(rootPath);
        var entries = new List<string>
        {
            $"ROOT|{(int)File.GetAttributes(fullRoot)}|{Directory.GetCreationTimeUtc(fullRoot).Ticks}|{Directory.GetLastWriteTimeUtc(fullRoot).Ticks}"
        };
        foreach (var directoryPath in Directory.EnumerateDirectories(
                     fullRoot,
                     "*",
                     SearchOption.AllDirectories)
                     .OrderBy(
                         static path => path,
                         StringComparer.OrdinalIgnoreCase))
        {
            entries.Add(
                $"D|{Path.GetRelativePath(fullRoot, directoryPath)}|{(int)File.GetAttributes(directoryPath)}|{Directory.GetCreationTimeUtc(directoryPath).Ticks}|{Directory.GetLastWriteTimeUtc(directoryPath).Ticks}");
        }

        foreach (var filePath in Directory.EnumerateFiles(
                     fullRoot,
                     "*",
                     SearchOption.AllDirectories)
                     .OrderBy(
                         static path => path,
                         StringComparer.OrdinalIgnoreCase))
        {
            var fileInfo = new FileInfo(filePath);
            entries.Add(
                $"F|{Path.GetRelativePath(fullRoot, filePath)}|{fileInfo.Length}|{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(filePath)))}|{(int)fileInfo.Attributes}|{fileInfo.CreationTimeUtc.Ticks}|{fileInfo.LastWriteTimeUtc.Ticks}");
        }

        return entries.ToArray();
    }

    private static void MakeTreeDeletable(string rootPath)
    {
        if (!Directory.Exists(rootPath))
            return;

        foreach (var entryPath in Directory.EnumerateFileSystemEntries(
                     rootPath,
                     "*",
                     SearchOption.AllDirectories))
        {
            try
            {
                var attributes = File.GetAttributes(entryPath);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(
                        entryPath,
                        attributes & ~FileAttributes.ReadOnly);
                }
            }
            catch
            {
                // Test cleanup only.
            }
        }
    }

    private static async Task<PowerShellProcessResult> RunPowerShellAsync(
        string scriptPath,
        IReadOnlyDictionary<string, string?> environmentOverrides,
        TimeSpan timeout,
        params string[] arguments)
    {
        var powerShellPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = File.Exists(powerShellPath)
                ? powerShellPath
                : "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory =
                Path.GetDirectoryName(scriptPath) ??
                TestProcessIsolation.TempRoot
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        ApplyPowerShellEnvironmentOverrides(
            startInfo,
            scriptPath,
            environmentOverrides);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                $"PowerShell process did not start: {scriptPath}");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync()
                .WaitAsync(timeout);
        }
        catch (TimeoutException ex)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException(
                "PowerShell test process timed out." +
                Environment.NewLine +
                "STDOUT:" + Environment.NewLine +
                await stdoutTask +
                Environment.NewLine +
                "STDERR:" + Environment.NewLine +
                await stderrTask,
                ex);
        }

        return new PowerShellProcessResult(
            process.ExitCode,
            await stdoutTask,
            await stderrTask);
    }

    private static Process StartPowerShellScript(
        string scriptPath,
        params string[] arguments)
        => StartPowerShellScript(
            scriptPath,
            new Dictionary<string, string?>(),
            arguments);

    private static Process StartPowerShellScript(
        string scriptPath,
        IReadOnlyDictionary<string, string?> environmentOverrides,
        params string[] arguments)
    {
        var powerShellPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = File.Exists(powerShellPath) ? powerShellPath : "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? TestProcessIsolation.TempRoot
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        ApplyPowerShellEnvironmentOverrides(
            startInfo,
            scriptPath,
            environmentOverrides);

        return Process.Start(startInfo)
               ?? throw new InvalidOperationException(
                   $"PowerShell test helper did not start: {scriptPath}");
    }

    private static void ApplyPowerShellEnvironmentOverrides(
        ProcessStartInfo startInfo,
        string scriptPath,
        IReadOnlyDictionary<string, string?> environmentOverrides)
    {
        foreach (var pair in environmentOverrides)
            startInfo.Environment[pair.Key] = pair.Value;

        const string capabilityVariableName =
            "GEORAEPLAN_INSTALLER_TEST_CAPABILITY";
        var usesInstallerTestHook = environmentOverrides.Any(
            static pair =>
                pair.Key.StartsWith(
                    "GEORAEPLAN_INSTALLER_TEST_",
                    StringComparison.OrdinalIgnoreCase) &&
                !pair.Key.Equals(
                    capabilityVariableName,
                    StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(pair.Value));
        var hasExplicitCapability = environmentOverrides.Keys.Any(
            static key => key.Equals(
                capabilityVariableName,
                StringComparison.OrdinalIgnoreCase));
        if (!usesInstallerTestHook || hasExplicitCapability)
            return;

        var markerPath = Path.Combine(
            Path.GetDirectoryName(scriptPath)
                ?? throw new InvalidOperationException(
                    "PowerShell script directory was not resolved."),
            ".georaeplan-installer-test-capability");
        if (File.Exists(markerPath))
        {
            startInfo.Environment[capabilityVariableName] =
                File.ReadAllText(markerPath);
        }
    }

    private static async Task WaitForFileAsync(string filePath, TimeSpan timeout)
    {
        var startedAtUtc = DateTime.UtcNow;
        while (!File.Exists(filePath))
        {
            if (DateTime.UtcNow - startedAtUtc >= timeout)
                throw new TimeoutException($"Timed out waiting for helper file: {filePath}");

            await Task.Delay(25);
        }
    }

    private static async Task WaitForProcessExitAsync(
        int processId,
        TimeSpan timeout)
    {
        var startedAtUtc = DateTime.UtcNow;
        while (true)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                    return;
            }
            catch (ArgumentException)
            {
                return;
            }

            if (DateTime.UtcNow - startedAtUtc >= timeout)
            {
                throw new TimeoutException(
                    $"Timed out waiting for process exit: {processId}");
            }

            await Task.Delay(25);
        }
    }

    private static void TryTerminateProcess(Process? process)
    {
        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Test cleanup only.
        }
    }

    private static string FindRepositoryRoot(
        [CallerFilePath] string sourceFilePath = "")
    {
        var directory = new DirectoryInfo(
            Path.GetDirectoryName(sourceFilePath) ?? AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(
                    directory.FullName,
                    "Updater",
                    "거래플랜.Updater",
                    "Program.cs")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("거래플랜 저장소 루트를 찾지 못했습니다.");
    }

    private static void AssertInOrder(string source, params string[] expected)
    {
        var previous = -1;
        foreach (var value in expected)
        {
            var current = source.IndexOf(value, previous + 1, StringComparison.Ordinal);
            Assert.True(current >= 0, $"Expected source fragment was not found: {value}");
            Assert.True(current > previous, $"Source fragment was out of order: {value}");
            previous = current;
        }
    }

    private sealed class NonSeekableWriteStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) =>
            throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) =>
            inner.Write(buffer);
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            inner.WriteAsync(buffer, cancellationToken);
    }

    private sealed class WriteFailingStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) =>
            throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new IOException("synthetic write failure");
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("synthetic write failure"));
    }

    private sealed class UpdateManifestHandler(
        AppUpdatePackageDto package,
        string channel = "stable")
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new AppUpdateManifestDto
                {
                    Channel = channel,
                    Desktop = package
                })
            });
    }

    private sealed record PowerShellProcessResult(
        int ExitCode,
        string StdOut,
        string StdErr);
    private sealed record GeneratedInstallerPackage(
        string InstallScriptPath,
        string AppDisplayName,
        string ExpectedVersion = "9.9.999");
    private sealed class ExpectedUpdaterFailureException : Exception;
    private sealed class CallerStatusSetterException : Exception;
}
