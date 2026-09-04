using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.IO.Pipes;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32.SafeHandles;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Updater;

internal static class Program
{
    private const long MinimumUpdaterWorkBytes = 512L * 1024 * 1024;
    private const long InstallBufferBytes = 256L * 1024 * 1024;
    internal const long MaximumUpdatePackageBytes = 512L * 1024 * 1024;
    internal const int MaximumArchiveEntryCount = 10_000;
    internal const long MaximumArchiveEntryBytes = 512L * 1024 * 1024;
    internal const long MaximumArchiveTotalUncompressedBytes = 2L * 1024 * 1024 * 1024;
    internal const int MaximumArchivePathLength = 1_024;
    internal const int MaximumArchivePathSegmentLength = 255;
    private const string TempRootOverrideEnvironmentKey = "GEORAEPLAN_TEMP_ROOT";
    private static readonly TimeSpan UpdateArtifactRetention = TimeSpan.FromDays(3);
    private static readonly TimeSpan ProcessExitGracePeriod = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ProcessCloseWindowGracePeriod = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DesktopGateHandoffTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DesktopIdentityHandoffTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan InstallProcessTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan InstallProcessTerminationTimeout = TimeSpan.FromSeconds(10);

    private static string? _sessionLogPath;

    [STAThread]
    public static int Main(string[] args)
    {
        var app = new Application
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose
        };

        var window = new UpdateProgressWindow();
        app.MainWindow = window;

        app.Startup += async (_, _) => await RunUpdateAsync(app, window, args);

        return app.Run(window);
    }

    private static async Task RunUpdateAsync(Application app, UpdateProgressWindow window, string[] args)
    {
        try
        {
            var options = UpdateArguments.Parse(args);
            await ExecuteAsync(options, window);
            app.Shutdown(0);
        }
        catch (Exception ex)
        {
            TryLog($"FATAL {ex}");
            var message = string.IsNullOrWhiteSpace(_sessionLogPath)
                ? ex.Message
                : $"{ex.Message}{Environment.NewLine}{Environment.NewLine}로그 파일: {_sessionLogPath}";

            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            window.Closed += (_, _) => app.Shutdown(1);
            window.ShowFailure("업데이트를 완료하지 못했습니다.", message, _sessionLogPath);
            window.Activate();
        }
    }

    private static async Task ExecuteAsync(UpdateArguments options, UpdateProgressWindow window)
    {
        EnsureExpectedProcessIdentity(options);
        await SignalDesktopHandoffAsync(options);
        var installMutationGateRoots =
            GetInstallerMutationGateRoots(options.InstallRoot);
        using var installRootUpdateLock = InstallRootUpdateLock.AcquireForDesktopHandoff(
            installMutationGateRoots,
            DesktopGateHandoffTimeout);
        using var installOperationLease =
            InstallRootUpdateLock.AcquireOperationLeasesForDesktopHandoff(
                installMutationGateRoots,
                DesktopGateHandoffTimeout);
        using var installWorkerLease =
            InstallRootUpdateLock.AcquireWorkerLeasesForDesktopHandoff(
                installMutationGateRoots,
                DesktopGateHandoffTimeout);

        await WaitForProcessExitAsync(options);
        var requestMetadata = UpdateRequestMetadata.LoadAndDelete(options.RequestMetadataPath);
        TryCleanupStaleUpdateArtifacts();
        var legacyRollbackArtifactRoots =
            LegacyInstallRollbackStateProbe.GetDefaultArtifactRoots();
        foreach (var legacyRecoveryTarget in
                 GetLegacyRollbackRecoveryTargets(options))
        {
            await InstallRollbackSupervisor.RecoverPendingUntilResolvedAsync(
                legacyRollbackArtifactRoots,
                legacyRecoveryTarget.InstallRoot,
                legacyRecoveryTarget.LegacyInstallRoot,
                TryLog);
        }

        var generatedRecoveryProbe =
            RequireGeneratedInstallRecoveryProbe(options.InstallRoot);
        if (generatedRecoveryProbe.Status == InstallRecoveryStateStatus.Absent)
        {
            var installedVersionState =
                GetInstalledVersionState(options, out var installedVersion);
            if (installedVersionState == InstalledVersionState.Unparseable)
            {
                throw new InvalidOperationException(
                    $"기존 거래플랜 실행 파일 버전을 확인할 수 없어 업데이트를 중단합니다: {options.LaunchExe}");
            }

            if (installedVersionState == InstalledVersionState.AtLeastRequested)
            {
                EnsureGeneratedInstallRecoveryAbsent(options.InstallRoot);
                TryLog(
                    $"SKIP requested={NormalizeVersionText(options.Version)} installed={NormalizeVersionText(installedVersion)}");
                var currentUpdaterStagingRoot = GetCurrentUpdaterStagingRoot();
                TryCleanupSupersededUpdateArtifacts(
                    GetUpdateArtifactRoot(),
                    currentUpdaterStagingRoot);
                SchedulePostExitCleanup(currentUpdaterStagingRoot);
                installWorkerLease.Dispose();
                installOperationLease.Dispose();
                installRootUpdateLock.Dispose();
                LaunchExistingDesktop(options);
                return;
            }
        }

        var workRoot = CreateUpdateWorkRoot();
        Directory.CreateDirectory(workRoot);
        _sessionLogPath = Path.Combine(workRoot, "update.log");
        TryLog($"START version={options.Version} package={options.PackageUrl} preparedPackage={options.PackagePath}");

        SetStage(window, "업데이트 준비 중", "임시 작업 폴더와 설치 공간을 확인하고 있습니다.");
        EnsureWorkDriveFreeSpace(workRoot, options.FileSize);

        var safePackageFileName = string.IsNullOrWhiteSpace(options.FileName)
            ? $"desktop-{options.Version}.zip"
            : Path.GetFileName(options.FileName);
        if (string.IsNullOrWhiteSpace(safePackageFileName))
            safePackageFileName = $"desktop-{options.Version}.zip";
        var safeWorkRoot = Path.GetFullPath(workRoot);
        if (!safeWorkRoot.EndsWith(Path.DirectorySeparatorChar))
            safeWorkRoot += Path.DirectorySeparatorChar;
        var packagePath = Path.GetFullPath(Path.Combine(workRoot, safePackageFileName));
        if (!packagePath.StartsWith(safeWorkRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("업데이트 패키지 저장 경로가 안전하지 않습니다.");

        if (!string.IsNullOrWhiteSpace(options.PackagePath))
        {
            SetStage(window, "업데이트 파일 확인 중", "미리 받아둔 새 버전 파일을 확인하고 있습니다.");
            CopyPreparedPackage(options.PackagePath, packagePath);
        }
        else
        {
            SetStage(window, "업데이트 다운로드 중", "새 버전 파일을 가져오고 있습니다.");
            await DownloadAsync(options.PackageUrl, packagePath, requestMetadata, progress =>
            {
                var detail = progress.TotalBytes.HasValue
                    ? $"다운로드 {FormatBytes(progress.DownloadedBytes)} / {FormatBytes(progress.TotalBytes.Value)}"
                    : $"다운로드 {FormatBytes(progress.DownloadedBytes)}";
                SetStage(window, "업데이트 다운로드 중", detail);
            });
        }

        SetStage(window, "무결성 확인 중", "다운로드한 파일의 SHA256을 검증하고 있습니다.");
        await VerifySha256Async(packagePath, options.Sha256);
        VerifyExpectedPackageFileSize(packagePath, options.FileSize);

        var extractRoot = Path.Combine(workRoot, "package");
        SetStage(window, "설치 파일 준비 중", "업데이트 패키지를 압축 해제하고 있습니다.");
        await ExtractVerifiedPackageAsync(
            packagePath,
            extractRoot,
            options.Sha256,
            options.FileSize);
        EnsureInstallDriveFreeSpace(
            extractRoot,
            GetEffectiveInstallerInstallRoot(options.InstallRoot));

        var installScriptPath = Path.Combine(extractRoot, "Install-GeoraePlan.ps1");
        if (!File.Exists(installScriptPath))
            throw new FileNotFoundException("설치 스크립트를 찾지 못했습니다.", installScriptPath);

        // The updater retains every primary root gate. The generated supervisor
        // takes the operation lease, and its worker takes the worker lease.
        installWorkerLease.Dispose();
        installOperationLease.Dispose();

        if (generatedRecoveryProbe.Status == InstallRecoveryStateStatus.Present)
        {
            SetStage(
                window,
                "중단된 업데이트 복구 중",
                "검증된 설치 스크립트로 기존 설치 상태를 먼저 복구하고 있습니다.");
            var recoveredVersionState =
                await RecoverGeneratedInstallStateBeforeVersionDecisionAsync(
                    options,
                    extractRoot,
                     installScriptPath,
                     _sessionLogPath,
                     InstallProcessTimeout,
                     updaterOwnsInstallRootGate: true);
            if (recoveredVersionState == InstalledVersionState.Unparseable)
            {
                throw new InvalidOperationException(
                    $"복구된 거래플랜 실행 파일 버전을 확인할 수 없어 업데이트를 중단합니다: {options.LaunchExe}");
            }

            if (recoveredVersionState == InstalledVersionState.AtLeastRequested)
            {
                _ = GetInstalledVersionState(
                    options,
                    out var recoveredInstalledVersion);
                EnsureGeneratedInstallRecoveryAbsent(options.InstallRoot);
                TryLog(
                    $"RECOVERED-SKIP requested={NormalizeVersionText(options.Version)} installed={NormalizeVersionText(recoveredInstalledVersion)}");
                var currentUpdaterStagingRoot = GetCurrentUpdaterStagingRoot();
                TryCleanupSupersededUpdateArtifacts(
                    GetUpdateArtifactRoot(),
                    workRoot,
                    currentUpdaterStagingRoot);
                SchedulePostExitCleanup(
                    workRoot,
                    currentUpdaterStagingRoot);
                installWorkerLease.Dispose();
                installOperationLease.Dispose();
                installRootUpdateLock.Dispose();
                LaunchExistingDesktop(options);
                return;
            }
        }

        SetStage(window, "업데이트 적용 중", "새 버전 파일을 설치 위치에 복사하고 있습니다.");
        await ExecuteInstallWithRollbackAsync(
            options,
            extractRoot,
            installScriptPath,
            _sessionLogPath,
            GetUpdateArtifactRoot(),
            InstallProcessTimeout,
            environmentOverrides: null,
            updaterOwnsInstallRootGate: true);

        EnsureGeneratedInstallRecoveryAbsent(options.InstallRoot);
        var completedUpdaterStagingRoot = GetCurrentUpdaterStagingRoot();
        TryCleanupSupersededUpdateArtifacts(
            GetUpdateArtifactRoot(),
            workRoot,
            completedUpdaterStagingRoot);
        installWorkerLease.Dispose();
        installOperationLease.Dispose();
        installRootUpdateLock.Dispose();

        if (!string.IsNullOrWhiteSpace(options.LaunchExe) && File.Exists(options.LaunchExe))
        {
            SetStage(window, "업데이트 완료", "최신 버전으로 다시 실행하고 있습니다.");
            LaunchExistingDesktop(options);
        }

        SchedulePostExitCleanup(workRoot, completedUpdaterStagingRoot);
        TryLog("SUCCESS");
    }

    internal static async Task ExecuteInstallWithRollbackAsync(
        UpdateArguments options,
        string extractRoot,
        string installScriptPath,
        string? logPath,
        string artifactRoot,
        TimeSpan installTimeout,
        IReadOnlyDictionary<string, string?>? environmentOverrides = null,
        bool updaterOwnsInstallRootGate = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (installTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(installTimeout));

        _ = artifactRoot;
        EnsureInstallSupervisorContract(installScriptPath);
        EnsureInstallRecoveryOnlyContract(installScriptPath);
        await RunInstallScriptAsync(
                options,
                extractRoot,
                installScriptPath,
                logPath,
                installTimeout,
                environmentOverrides,
                allowTimeoutTermination: false,
                updaterOwnsInstallRootGate:
                    updaterOwnsInstallRootGate)
            .ConfigureAwait(false);
        var postInstallRecovery =
            RequireGeneratedInstallRecoveryProbe(options.InstallRoot);
        if (postInstallRecovery.Status ==
            InstallRecoveryStateStatus.Present)
        {
            _ = await
                RecoverGeneratedInstallStateBeforeVersionDecisionAsync(
                    options,
                    extractRoot,
                    installScriptPath,
                    logPath,
                    installTimeout,
                    updaterOwnsInstallRootGate,
                    environmentOverrides)
                .ConfigureAwait(false);
        }
        EnsureGeneratedInstallRecoveryAbsent(options.InstallRoot);
        ValidateInstalledApplication(options);
    }

    internal static async Task<InstalledVersionState>
        RecoverGeneratedInstallStateBeforeVersionDecisionAsync(
            UpdateArguments options,
            string extractRoot,
            string installScriptPath,
            string? logPath,
            TimeSpan recoveryTimeout,
            bool updaterOwnsInstallRootGate = false,
            IReadOnlyDictionary<string, string?>? environmentOverrides = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (recoveryTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(recoveryTimeout));

        var beforeRecovery =
            RequireGeneratedInstallRecoveryProbe(options.InstallRoot);
        if (beforeRecovery.Status == InstallRecoveryStateStatus.Absent)
        {
            return GetInstalledVersionState(
                options,
                out _);
        }

        EnsureInstallSupervisorContract(installScriptPath);
        EnsureInstallRecoveryOnlyContract(installScriptPath);
        TryLog(
            $"RECOVERY pending={beforeRecovery.StatePath}");
        await RunInstallScriptAsync(
                options,
                extractRoot,
                installScriptPath,
                logPath,
                recoveryTimeout,
                 environmentOverrides,
                 allowTimeoutTermination: false,
                 recoveryOnly: true,
                 updaterOwnsInstallRootGate: updaterOwnsInstallRootGate)
            .ConfigureAwait(false);
        EnsureGeneratedInstallRecoveryAbsent(options.InstallRoot);
        TryLog("RECOVERY completed; pending state absent");
        return GetInstalledVersionState(
            options,
            out _);
    }

    private static async Task RunInstallScriptAsync(
        UpdateArguments options,
        string extractRoot,
        string installScriptPath,
        string? logPath,
        TimeSpan installTimeout,
        IReadOnlyDictionary<string, string?>? environmentOverrides,
        bool allowTimeoutTermination,
        bool recoveryOnly = false,
        bool updaterOwnsInstallRootGate = false)
    {
        var requiresElevation =
            RequiresElevation(GetEffectiveInstallerInstallRoot(options.InstallRoot));
        var legacyBridgeCopy = IsLegacyInstallRoot(options.InstallRoot);
        using var currentProcess = updaterOwnsInstallRootGate
            ? Process.GetCurrentProcess()
            : null;
        var gateOwnerProcessPath = currentProcess?.MainModule?.FileName;
        var gateOwnerStartTimeUtcTicks = currentProcess?.StartTime.ToUniversalTime().Ticks ?? 0;
        if (updaterOwnsInstallRootGate &&
            string.IsNullOrWhiteSpace(gateOwnerProcessPath))
        {
            throw new InvalidOperationException(
                "설치 잠금 소유 updater 실행 파일 경로를 확인하지 못했습니다.");
        }

        var arguments = string.Join(" ", new[]
        {
            "-NoProfile",
            "-NonInteractive",
            "-ExecutionPolicy", "Bypass",
            requiresElevation ? string.Empty : "-WindowStyle Hidden",
            "-File",
            QuoteArgument(installScriptPath),
            "-InstallRoot",
            QuoteArgument(options.InstallRoot),
            "-NoLaunch",
            "-SuppressUi",
            "-WorkerTimeoutSeconds",
            Math.Max(1, (int)Math.Ceiling(installTimeout.TotalSeconds)).ToString(),
            "-LogPath",
            QuoteArgument(logPath ?? string.Empty),
            recoveryOnly ? "-RecoveryOnly" : string.Empty,
            legacyBridgeCopy ? "-LegacyBridgeCopy" : string.Empty,
            updaterOwnsInstallRootGate ? "-UpdaterOwnsInstallRootGate" : string.Empty,
            updaterOwnsInstallRootGate ? "-InstallRootGateOwnerProcessId" : string.Empty,
            updaterOwnsInstallRootGate ? Environment.ProcessId.ToString() : string.Empty,
            updaterOwnsInstallRootGate ? "-InstallRootGateOwnerProcessPath" : string.Empty,
            updaterOwnsInstallRootGate ? QuoteArgument(gateOwnerProcessPath!) : string.Empty,
            updaterOwnsInstallRootGate ? "-InstallRootGateOwnerProcessStartTimeUtcTicks" : string.Empty,
            updaterOwnsInstallRootGate ? gateOwnerStartTimeUtcTicks.ToString() : string.Empty
        }.Where(static part => !string.IsNullOrWhiteSpace(part)));

        var installStartInfo = new ProcessStartInfo
        {
            FileName = ResolvePowerShellPath(),
            Arguments = arguments,
            WorkingDirectory = extractRoot,
            UseShellExecute = requiresElevation
        };

        if (requiresElevation)
        {
            installStartInfo.Verb = "runas";
        }
        else
        {
            installStartInfo.CreateNoWindow = true;
            installStartInfo.RedirectStandardOutput = true;
            installStartInfo.RedirectStandardError = true;
        }

        if (environmentOverrides is not null)
        {
            if (requiresElevation)
            {
                throw new InvalidOperationException(
                    "관리자 권한 설치에는 테스트 환경 변수 override를 사용할 수 없습니다.");
            }

            foreach (var pair in environmentOverrides)
                installStartInfo.Environment[pair.Key] = pair.Value;
        }

        Process? installProcess;
        try
        {
            installProcess = Process.Start(installStartInfo);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("업데이트 설치에 필요한 관리자 권한 승인이 취소되었습니다.", ex);
        }

        if (installProcess is null)
            throw new InvalidOperationException("업데이트 설치 프로세스를 시작하지 못했습니다.");

        using (installProcess)
        {
            if (!requiresElevation)
            {
                var stdoutTask = RelayStreamToLogAsync(installProcess.StandardOutput, "INSTALL-OUT");
                var stderrTask = RelayStreamToLogAsync(installProcess.StandardError, "INSTALL-ERR");
                try
                {
                    await WaitForInstallProcessExitAsync(
                        installProcess,
                        installTimeout + TimeSpan.FromMinutes(2),
                        allowTimeoutTermination);
                }
                finally
                {
                    if (installProcess.HasExited)
                        await Task.WhenAll(stdoutTask, stderrTask);
                }
            }
            else
            {
                await WaitForInstallProcessExitAsync(
                    installProcess,
                    installTimeout + TimeSpan.FromMinutes(2),
                    allowTimeoutTermination);
            }

            if (installProcess.ExitCode != 0)
                throw new InvalidOperationException($"업데이트 설치가 실패했습니다. exitCode={installProcess.ExitCode}");
        }
    }

    internal static async Task WaitForInstallProcessExitAsync(
        Process installProcess,
        TimeSpan timeout,
        bool allowTimeoutTermination = true)
    {
        ArgumentNullException.ThrowIfNull(installProcess);
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        using var timeoutCts = new CancellationTokenSource(timeout);
        try
        {
            await installProcess.WaitForExitAsync(timeoutCts.Token);
            return;
        }
        catch (OperationCanceledException)
        {
            if (installProcess.HasExited)
                return;

            TryLog(
                $"INSTALL timeout pid={installProcess.Id} timeoutSeconds={timeout.TotalSeconds:N0}");
        }

        if (!allowTimeoutTermination)
        {
            TryLog(
                $"INSTALL elevated supervisor still active; install gate retained. pid={installProcess.Id}");
            await installProcess.WaitForExitAsync();
            TryLog(
                $"INSTALL elevated supervisor exit verified. pid={installProcess.Id} exitCode={installProcess.ExitCode}");
            return;
        }

        Exception? lastTerminationFailure = null;
        while (!installProcess.HasExited)
        {
            try
            {
                installProcess.Kill(entireProcessTree: true);
                lastTerminationFailure = null;
            }
            catch (Exception ex) when (
                ex is Win32Exception or
                InvalidOperationException or
                NotSupportedException)
            {
                lastTerminationFailure = ex;
                TryLog(
                    $"INSTALL terminate failed; install gate retained. pid={installProcess.Id} error={ex.Message}");
            }

            if (installProcess.HasExited)
                break;

            using var terminationCts =
                new CancellationTokenSource(InstallProcessTerminationTimeout);
            try
            {
                await installProcess.WaitForExitAsync(
                    terminationCts.Token);
            }
            catch (OperationCanceledException)
            {
                TryLog(
                    $"INSTALL worker still alive; install gate retained. pid={installProcess.Id}");
            }
        }

        await installProcess.WaitForExitAsync();
        TryLog(
            $"INSTALL worker exit verified after timeout. pid={installProcess.Id} exitCode={installProcess.ExitCode}");
        throw new InvalidOperationException(
            $"업데이트 설치 시간이 {timeout.TotalMinutes:0.##}분을 초과하여 설치 프로세스 트리 종료를 확인했습니다.",
            lastTerminationFailure);
    }

    private static void ValidateInstalledApplication(UpdateArguments options)
    {
        if (string.IsNullOrWhiteSpace(options.InstallRoot))
            throw new InvalidOperationException("설치 경로가 비어 있어 업데이트 결과를 검증할 수 없습니다.");

        if (!Directory.Exists(options.InstallRoot))
            throw new DirectoryNotFoundException($"설치 경로를 찾지 못했습니다: {options.InstallRoot}");

        if (string.IsNullOrWhiteSpace(options.LaunchExe) || !File.Exists(options.LaunchExe))
            throw new FileNotFoundException("업데이트 후 실행 파일을 찾지 못했습니다.", options.LaunchExe);

        // Keep this contract aligned with the generated Install-GeoraePlan.ps1.
        // The installer validates these before discarding rollback snapshots.
        foreach (var requiredPath in new[]
                 {
                     Path.Combine(options.InstallRoot, "appsettings.json"),
                     Path.Combine(options.InstallRoot, "Updater", "거래플랜.Updater.exe")
                 })
        {
            if (!File.Exists(requiredPath))
                throw new FileNotFoundException($"업데이트 후 필수 파일이 누락되었습니다: {requiredPath}", requiredPath);
        }

        var installedVersion = FileVersionInfo.GetVersionInfo(options.LaunchExe).ProductVersion ?? string.Empty;
        if (CompareVersions(installedVersion, options.Version) < 0)
        {
            throw new InvalidOperationException(
                $"업데이트 후 실행 파일 버전이 기대 버전보다 낮습니다. 기대: {NormalizeVersionText(options.Version)}, 실제: {NormalizeVersionText(installedVersion)}");
        }

        TryLog($"VALIDATE installRoot={options.InstallRoot} version={NormalizeVersionText(installedVersion)}");
    }

    internal static InstalledVersionState GetInstalledVersionState(
        UpdateArguments options,
        out string installedVersion)
    {
        ArgumentNullException.ThrowIfNull(options);

        installedVersion = string.Empty;
        if (string.IsNullOrWhiteSpace(options.LaunchExe) ||
            !File.Exists(options.LaunchExe))
        {
            return InstalledVersionState.Absent;
        }

        if (!TryParseVersionStrict(
                options.Version,
                out var requestedVersion))
        {
            throw new InvalidOperationException(
                $"요청 업데이트 버전 형식이 올바르지 않습니다: {options.Version}");
        }

        installedVersion =
            FileVersionInfo.GetVersionInfo(options.LaunchExe).ProductVersion ?? string.Empty;
        if (!TryParseVersionStrict(
                installedVersion,
                out var parsedInstalledVersion))
        {
            return InstalledVersionState.Unparseable;
        }

        return parsedInstalledVersion >= requestedVersion
            ? InstalledVersionState.AtLeastRequested
            : InstalledVersionState.Older;
    }

    internal static InstallRecoveryStateProbeResult
        RequireGeneratedInstallRecoveryProbe(string installRoot)
    {
        IReadOnlyList<string> probeRoots;
        try
        {
            probeRoots = GetGeneratedRecoveryProbeRoots(installRoot);
        }
        catch (Exception ex) when (
            ex is ArgumentException or
            IOException or
            NotSupportedException or
            UnauthorizedAccessException or
            System.Security.SecurityException or
            Win32Exception)
        {
            throw new InvalidOperationException(
                "중단된 업데이트 복구 상태 확인 경로를 안전하게 해석할 수 없습니다.",
                ex);
        }

        var probes = probeRoots
            .Select(InstallRecoveryStateProbe.Probe)
            .ToArray();
        var accessError = probes.FirstOrDefault(
            static probe =>
                probe.Status == InstallRecoveryStateStatus.AccessError);
        if (accessError is not null)
        {
            throw new InvalidOperationException(
                $"중단된 업데이트 복구 상태에 접근할 수 없어 안전하게 업데이트를 중단합니다: {accessError.StatePath}",
                accessError.Error);
        }

        var pending = probes
            .Where(
                static probe =>
                    probe.Status == InstallRecoveryStateStatus.Present)
            .ToArray();
        if (pending.Length > 1)
        {
            throw new InvalidOperationException(
                "canonical과 legacy 설치 경로에 복구 상태가 동시에 남아 있어 자동 복구 대상을 안전하게 결정할 수 없습니다.");
        }

        return pending.FirstOrDefault()
            ?? probes.FirstOrDefault()
            ?? throw new InvalidOperationException(
                "중단된 업데이트 복구 상태 확인 경로가 비어 있습니다.");
    }

    internal static void EnsureGeneratedInstallRecoveryAbsent(
        string installRoot)
    {
        var probe = RequireGeneratedInstallRecoveryProbe(installRoot);
        if (probe.Status == InstallRecoveryStateStatus.Present)
        {
            throw new InvalidOperationException(
                $"중단된 업데이트 복구 상태가 남아 있어 앱 실행을 차단합니다: {probe.StatePath}");
        }
    }

    private static void LaunchExistingDesktop(UpdateArguments options)
    {
        EnsureGeneratedInstallRecoveryAbsent(options.InstallRoot);
        if (string.IsNullOrWhiteSpace(options.LaunchExe) || !File.Exists(options.LaunchExe))
            throw new FileNotFoundException(
                "다시 실행할 거래플랜 파일을 찾지 못했습니다.",
                options.LaunchExe);

        Process.Start(new ProcessStartInfo
        {
            FileName = options.LaunchExe,
            WorkingDirectory = Path.GetDirectoryName(options.LaunchExe) ?? options.InstallRoot,
            UseShellExecute = true
        });
    }

    private static async Task RelayStreamToLogAsync(StreamReader reader, string prefix)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync();
            if (line is null)
                break;

            TryLog($"{prefix} {line}");
        }
    }

    private static void CopyPreparedPackage(string preparedPackagePath, string targetPath)
    {
        var sourcePath = Path.GetFullPath(preparedPackagePath);
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("미리 다운로드한 업데이트 패키지를 찾지 못했습니다.", sourcePath);

        if (!string.Equals(Path.GetExtension(sourcePath), ".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("미리 다운로드한 업데이트 패키지 형식이 올바르지 않습니다.");

        var sourceInfo = new FileInfo(sourcePath);
        if (sourceInfo.Length <= 0)
            throw new InvalidOperationException("미리 다운로드한 업데이트 패키지가 비어 있습니다.");

        File.Copy(sourcePath, targetPath, overwrite: true);
        TryLog($"DOWNLOAD reused prepared package bytes={sourceInfo.Length} path={sourcePath}");
    }

    private static async Task DownloadAsync(
        string packageUrl,
        string targetPath,
        UpdateRequestMetadata requestMetadata,
        Action<DownloadProgress>? reportProgress = null)
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, packageUrl);
        requestMetadata.ApplyTo(request);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync();
        await using var destination = File.Create(targetPath);

        var buffer = new byte[81920];
        long downloadedBytes = 0;
        var lastReportUtc = DateTime.UtcNow;

        while (true)
        {
            var read = await source.ReadAsync(buffer);
            if (read <= 0)
                break;

            await destination.WriteAsync(buffer.AsMemory(0, read));
            downloadedBytes += read;

            var nowUtc = DateTime.UtcNow;
            if (reportProgress is not null && (nowUtc - lastReportUtc).TotalMilliseconds >= 250)
            {
                reportProgress(new DownloadProgress(downloadedBytes, totalBytes));
                lastReportUtc = nowUtc;
            }
        }

        await destination.FlushAsync();
        reportProgress?.Invoke(new DownloadProgress(downloadedBytes, totalBytes));
        TryLog($"DOWNLOAD completed bytes={downloadedBytes}");
    }

    private static async Task VerifySha256Async(string filePath, string sha256)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);
        await VerifySha256Async(stream, sha256);
    }

    private static async Task VerifySha256Async(Stream stream, string sha256)
    {
        if (string.IsNullOrWhiteSpace(sha256))
            throw new InvalidOperationException("업데이트 패키지의 SHA256 정보가 비어 있습니다.");

        stream.Position = 0;
        using var algorithm = SHA256.Create();
        var hash = await algorithm.ComputeHashAsync(stream);
        var actual = Convert.ToHexString(hash);
        if (!string.Equals(actual, sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("다운로드한 업데이트 패키지의 SHA256 검증에 실패했습니다.");

        TryLog($"SHA256 verified {actual}");
    }

    internal static async Task ExtractVerifiedPackageAsync(
        string packagePath,
        string extractRoot,
        string sha256,
        long expectedFileSize,
        Func<string, long>? availableFreeSpaceProvider = null)
    {
        await using var packageStream = new FileStream(
            packagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);

        if (expectedFileSize > 0 && packageStream.Length != expectedFileSize)
        {
            throw new InvalidOperationException(
                $"업데이트 패키지 크기가 manifest와 일치하지 않습니다. 기록 {expectedFileSize:N0}바이트, 실제 {packageStream.Length:N0}바이트입니다.");
        }

        if (packageStream.Length > MaximumUpdatePackageBytes)
        {
            throw new InvalidOperationException(
                $"업데이트 패키지가 허용 크기({MaximumUpdatePackageBytes:N0}바이트)를 초과합니다.");
        }

        await VerifySha256Async(packageStream, sha256);
        packageStream.Position = 0;
        var rawArchiveEntries = ValidateRawPackageArchiveStructure(packageStream);
        packageStream.Position = 0;
        using var archive = new ZipArchive(packageStream, ZipArchiveMode.Read, leaveOpen: true);
        var archiveEntries = archive.Entries;
        ValidateRawPackageArchiveEntriesMatch(rawArchiveEntries, archiveEntries);
        var totalUncompressedBytes = ValidatePackageArchiveEntries(
            archiveEntries.Select(static entry =>
                new PackageArchiveEntryMetadata(
                    entry.FullName,
                    entry.Length,
                    entry.ExternalAttributes)),
            packageStream.Length);

        EnsureWorkDriveFreeSpaceForExtraction(
            extractRoot,
            totalUncompressedBytes,
            availableFreeSpaceProvider);
        string? safeExtractRoot = null;
        try
        {
            safeExtractRoot = PrepareEmptyExtractionRoot(extractRoot);
            await ExtractPackageArchiveEntriesAsync(
                archive,
                safeExtractRoot,
                totalUncompressedBytes);
        }
        catch (Exception) when (safeExtractRoot is not null)
        {
            PreserveFailedExtractionRoot(safeExtractRoot);
            throw;
        }
    }

    private static IReadOnlyList<RawPackageArchiveEntry>
        ValidateRawPackageArchiveStructure(Stream packageStream)
    {
        const uint endOfCentralDirectorySignature = 0x06054B50;
        const uint centralDirectoryHeaderSignature = 0x02014B50;
        const uint localFileHeaderSignature = 0x04034B50;
        const int endOfCentralDirectoryLength = 22;
        const int maximumZipCommentLength = ushort.MaxValue;
        var originalPosition = packageStream.Position;

        try
        {
            if (!packageStream.CanSeek || packageStream.Length < endOfCentralDirectoryLength)
                throw new InvalidOperationException("The update package ZIP structure is invalid.");

            var tailLength = checked((int)Math.Min(
                packageStream.Length,
                endOfCentralDirectoryLength + maximumZipCommentLength));
            var tail = new byte[tailLength];
            var tailStartOffset = packageStream.Length - tailLength;
            packageStream.Position = tailStartOffset;
            packageStream.ReadExactly(tail);

            var endOffset = -1;
            for (var index = tail.Length - endOfCentralDirectoryLength;
                 index >= 0;
                 index--)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(index, 4)) !=
                    endOfCentralDirectorySignature)
                {
                    continue;
                }

                var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(
                    tail.AsSpan(index + 20, 2));
                if (index + endOfCentralDirectoryLength + commentLength == tail.Length)
                {
                    endOffset = index;
                    break;
                }
            }

            if (endOffset < 0)
                throw new InvalidOperationException("The update package ZIP end record is invalid.");

            var endRecord = tail.AsSpan(endOffset, endOfCentralDirectoryLength);
            var endRecordAbsoluteOffset = checked(tailStartOffset + endOffset);
            var diskNumber = BinaryPrimitives.ReadUInt16LittleEndian(endRecord[4..]);
            var centralDirectoryDisk = BinaryPrimitives.ReadUInt16LittleEndian(endRecord[6..]);
            var entriesOnDisk = BinaryPrimitives.ReadUInt16LittleEndian(endRecord[8..]);
            var totalEntries = BinaryPrimitives.ReadUInt16LittleEndian(endRecord[10..]);
            var centralDirectorySize = BinaryPrimitives.ReadUInt32LittleEndian(endRecord[12..]);
            var centralDirectoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(endRecord[16..]);
            if (diskNumber != 0 || centralDirectoryDisk != 0 ||
                entriesOnDisk != totalEntries ||
                totalEntries > MaximumArchiveEntryCount ||
                totalEntries == ushort.MaxValue ||
                centralDirectorySize == uint.MaxValue ||
                centralDirectoryOffset == uint.MaxValue)
            {
                throw new InvalidOperationException(
                    "The update package uses an unsupported split or ZIP64 directory layout.");
            }

            if (centralDirectoryOffset > endRecordAbsoluteOffset ||
                centralDirectorySize > endRecordAbsoluteOffset - centralDirectoryOffset)
            {
                throw new InvalidOperationException(
                    "The update package central directory offset and size overflow its ZIP end record.");
            }

            var centralDirectoryEnd = checked(
                (long)centralDirectoryOffset + centralDirectorySize);
            if (centralDirectoryEnd != endRecordAbsoluteOffset ||
                centralDirectoryEnd > packageStream.Length)
            {
                throw new InvalidOperationException(
                    "The update package central directory bounds do not match its ZIP end record.");
            }

            packageStream.Position = centralDirectoryOffset;
            var centralHeader = new byte[46];
            var localHeader = new byte[30];
            var rawEntries = new List<RawPackageArchiveEntry>(totalEntries);
            for (var index = 0; index < totalEntries; index++)
            {
                if (packageStream.Position > centralDirectoryEnd - centralHeader.Length)
                {
                    throw new InvalidOperationException(
                        "The update package central directory entry exceeds its declared bounds.");
                }
                packageStream.ReadExactly(centralHeader);
                if (BinaryPrimitives.ReadUInt32LittleEndian(centralHeader) !=
                    centralDirectoryHeaderSignature)
                {
                    throw new InvalidOperationException(
                        "The update package central directory is invalid.");
                }

                var flags = BinaryPrimitives.ReadUInt16LittleEndian(centralHeader.AsSpan(8, 2));
                var compressionMethod = BinaryPrimitives.ReadUInt16LittleEndian(centralHeader.AsSpan(10, 2));
                var crc32 = BinaryPrimitives.ReadUInt32LittleEndian(centralHeader.AsSpan(16, 4));
                var compressedLength = BinaryPrimitives.ReadUInt32LittleEndian(centralHeader.AsSpan(20, 4));
                var uncompressedLength = BinaryPrimitives.ReadUInt32LittleEndian(centralHeader.AsSpan(24, 4));
                var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(centralHeader.AsSpan(28, 2));
                var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(centralHeader.AsSpan(30, 2));
                var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(centralHeader.AsSpan(32, 2));
                var diskStart = BinaryPrimitives.ReadUInt16LittleEndian(centralHeader.AsSpan(34, 2));
                var localHeaderOffset = BinaryPrimitives.ReadUInt32LittleEndian(centralHeader.AsSpan(42, 4));
                const ushort encryptedFlags = (1 << 0) | (1 << 6) | (1 << 13);
                if (compressedLength == uint.MaxValue || uncompressedLength == uint.MaxValue ||
                    localHeaderOffset == uint.MaxValue || diskStart != 0 ||
                    (flags & encryptedFlags) != 0 || compressionMethod == 99)
                {
                    throw new InvalidOperationException(
                        "The update package entry is encrypted or uses unsupported split or ZIP64 metadata.");
                }

                var variableLength = checked((long)nameLength + extraLength + commentLength);
                if (packageStream.Position > centralDirectoryEnd - variableLength)
                {
                    throw new InvalidOperationException(
                        "The update package central directory variable data exceeds its declared bounds.");
                }
                var centralName = new byte[nameLength];
                packageStream.ReadExactly(centralName);
                var centralExtra = new byte[extraLength];
                packageStream.ReadExactly(centralExtra);
                ValidateZipExtraFields(centralExtra, centralName);
                packageStream.Position = checked(packageStream.Position + commentLength);
                var nextCentralHeaderOffset = packageStream.Position;

                if ((long)localHeaderOffset + localHeader.Length > centralDirectoryOffset)
                {
                    throw new InvalidOperationException(
                        "The update package local entry header overlaps its central directory.");
                }
                packageStream.Position = localHeaderOffset;
                packageStream.ReadExactly(localHeader);
                if (BinaryPrimitives.ReadUInt32LittleEndian(localHeader) !=
                    localFileHeaderSignature)
                {
                    throw new InvalidOperationException(
                        "The update package local entry header is invalid.");
                }

                var localFlags = BinaryPrimitives.ReadUInt16LittleEndian(localHeader.AsSpan(6, 2));
                var localCompressionMethod = BinaryPrimitives.ReadUInt16LittleEndian(localHeader.AsSpan(8, 2));
                var localCrc32 = BinaryPrimitives.ReadUInt32LittleEndian(localHeader.AsSpan(14, 4));
                var localCompressedLength = BinaryPrimitives.ReadUInt32LittleEndian(localHeader.AsSpan(18, 4));
                var localUncompressedLength = BinaryPrimitives.ReadUInt32LittleEndian(localHeader.AsSpan(22, 4));
                var localNameLength = BinaryPrimitives.ReadUInt16LittleEndian(localHeader.AsSpan(26, 2));
                var localExtraLength = BinaryPrimitives.ReadUInt16LittleEndian(localHeader.AsSpan(28, 2));
                var localVariableEnd = checked(
                    (long)localHeaderOffset + localHeader.Length + localNameLength + localExtraLength);
                if (localVariableEnd > centralDirectoryOffset)
                {
                    throw new InvalidOperationException(
                        "The update package local entry variable data overlaps its central directory.");
                }
                var localName = new byte[localNameLength];
                packageStream.ReadExactly(localName);
                var localExtra = new byte[localExtraLength];
                packageStream.ReadExactly(localExtra);
                ValidateZipExtraFields(localExtra, localName);

                const ushort dataDescriptorFlag = 1 << 3;
                var localUsesDataDescriptor = (localFlags & dataDescriptorFlag) != 0;
                if (localFlags != flags || localCompressionMethod != compressionMethod ||
                    !localName.AsSpan().SequenceEqual(centralName) ||
                    (!localUsesDataDescriptor &&
                     (localCrc32 != crc32 ||
                      localCompressedLength != compressedLength ||
                      localUncompressedLength != uncompressedLength)) ||
                    (localUsesDataDescriptor &&
                     ((localCrc32 != 0 && localCrc32 != crc32) ||
                      (localCompressedLength != 0 && localCompressedLength != compressedLength) ||
                      (localUncompressedLength != 0 && localUncompressedLength != uncompressedLength))))
                {
                    throw new InvalidOperationException(
                        "The update package local and central entry declarations do not match.");
                }

                var dataOffset = checked(
                    (long)localHeaderOffset + localHeader.Length + localNameLength + localExtraLength);
                if (dataOffset < 0 ||
                    dataOffset > centralDirectoryOffset - compressedLength)
                {
                    throw new InvalidOperationException(
                        "The update package entry data range is invalid.");
                }

                rawEntries.Add(new RawPackageArchiveEntry(
                    compressedLength,
                    uncompressedLength));
                packageStream.Position = nextCentralHeaderOffset;
            }

            if (packageStream.Position != centralDirectoryEnd)
            {
                throw new InvalidOperationException(
                    "The update package central directory parser did not end at its declared boundary.");
            }

            return rawEntries;
        }
        finally
        {
            packageStream.Position = originalPosition;
        }
    }

    internal static void ValidateZipExtraFields(
        ReadOnlySpan<byte> extraFields,
        ReadOnlySpan<byte> entryName)
    {
        const ushort zip64ExtraFieldId = 0x0001;
        const ushort winZipAesExtraFieldId = 0x9901;
        var remaining = extraFields;
        while (!remaining.IsEmpty)
        {
            if (remaining.Length < 4)
            {
                throw new InvalidOperationException(
                    "The update package entry contains malformed ZIP extra metadata.");
            }

            var headerId = BinaryPrimitives.ReadUInt16LittleEndian(remaining);
            var dataLength = BinaryPrimitives.ReadUInt16LittleEndian(remaining[2..]);
            remaining = remaining[4..];
            if (dataLength > remaining.Length)
            {
                throw new InvalidOperationException(
                    "The update package entry contains malformed ZIP extra metadata.");
            }
            if (headerId is zip64ExtraFieldId or winZipAesExtraFieldId)
            {
                var displayName = Encoding.UTF8.GetString(entryName);
                throw new InvalidOperationException(
                    $"The update package entry uses unsupported ZIP64 or encrypted metadata: {displayName}");
            }

            remaining = remaining[dataLength..];
        }
    }

    private static void ValidateRawPackageArchiveEntriesMatch(
        IReadOnlyList<RawPackageArchiveEntry> rawEntries,
        IReadOnlyList<ZipArchiveEntry> archiveEntries)
    {
        if (rawEntries.Count != archiveEntries.Count)
        {
            throw new InvalidOperationException(
                "The update package ZIP entry count changed while it was being validated.");
        }

        for (var index = 0; index < rawEntries.Count; index++)
        {
            if (rawEntries[index].CompressedLength != archiveEntries[index].CompressedLength ||
                rawEntries[index].UncompressedLength != archiveEntries[index].Length)
            {
                throw new InvalidOperationException(
                    $"The update package raw and decoded entry sizes do not match: {archiveEntries[index].FullName}");
            }
        }
    }

    private static async Task ExtractPackageArchiveEntriesAsync(
        ZipArchive archive,
        string safeExtractRoot,
        long declaredTotalUncompressedBytes)
    {
        var rootPrefix = safeExtractRoot.EndsWith(Path.DirectorySeparatorChar)
            ? safeExtractRoot
            : safeExtractRoot + Path.DirectorySeparatorChar;
        long actualTotalUncompressedBytes = 0;

        foreach (var entry in archive.Entries)
        {
            var normalizedPath = ValidateAndNormalizeArchiveEntryPath(
                entry.FullName,
                out var isDirectory);
            var destinationPath = Path.GetFullPath(Path.Combine(
                safeExtractRoot,
                normalizedPath.Replace('/', Path.DirectorySeparatorChar)));
            if (!destinationPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The update package entry escapes the extraction root: {entry.FullName}");
            }

            if (isDirectory)
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            var parentPath = Path.GetDirectoryName(destinationPath)
                ?? throw new InvalidOperationException(
                    $"The update package entry has no safe parent directory: {entry.FullName}");
            Directory.CreateDirectory(parentPath);

            await using var entryStream = entry.Open();
            await using var destinationStream = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true);
            var actualEntryBytes = await CopyArchiveEntryBoundedAsync(
                entryStream,
                destinationStream,
                entry.FullName,
                entry.Length,
                actualTotalUncompressedBytes,
                MaximumArchiveEntryBytes,
                MaximumArchiveTotalUncompressedBytes);
            actualTotalUncompressedBytes = checked(
                actualTotalUncompressedBytes + actualEntryBytes);
        }

        if (actualTotalUncompressedBytes != declaredTotalUncompressedBytes)
        {
            throw new InvalidOperationException(
                "The update package extracted data length does not match its declared total.");
        }
    }

    internal static async Task<long> CopyArchiveEntryBoundedAsync(
        Stream source,
        Stream destination,
        string entryName,
        long declaredLength,
        long totalBytesBeforeEntry,
        long maximumEntryBytes = MaximumArchiveEntryBytes,
        long maximumTotalBytes = MaximumArchiveTotalUncompressedBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (!source.CanRead)
            throw new ArgumentException("The archive entry stream is not readable.", nameof(source));
        if (!destination.CanWrite)
            throw new ArgumentException("The archive destination stream is not writable.", nameof(destination));
        if (declaredLength < 0)
            throw new ArgumentOutOfRangeException(nameof(declaredLength));
        if (totalBytesBeforeEntry < 0)
            throw new ArgumentOutOfRangeException(nameof(totalBytesBeforeEntry));
        if (maximumEntryBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumEntryBytes));
        if (maximumTotalBytes < 0 || totalBytesBeforeEntry > maximumTotalBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumTotalBytes));

        if (declaredLength > maximumEntryBytes)
        {
            throw new InvalidOperationException(
                $"The update package entry exceeds the permitted size: {entryName}");
        }
        if (declaredLength > maximumTotalBytes - totalBytesBeforeEntry)
        {
            throw new InvalidOperationException(
                $"The update package entry exceeds the permitted total extracted size: {entryName}");
        }

        var buffer = new byte[81920];
        long actualEntryBytes = 0;
        while (true)
        {
            var bytesRead = await source.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0)
                break;

            if (bytesRead > declaredLength - actualEntryBytes ||
                bytesRead > maximumEntryBytes - actualEntryBytes)
            {
                throw new InvalidOperationException(
                    $"The update package entry produced more data than its declared or permitted size: {entryName}");
            }
            if (bytesRead > maximumTotalBytes - totalBytesBeforeEntry - actualEntryBytes)
            {
                throw new InvalidOperationException(
                    $"The update package produced more extracted data than the permitted total size: {entryName}");
            }

            await destination.WriteAsync(
                buffer.AsMemory(0, bytesRead),
                cancellationToken);
            actualEntryBytes += bytesRead;
        }

        if (actualEntryBytes != declaredLength)
        {
            throw new InvalidOperationException(
                $"The update package entry data length does not match its declaration: {entryName}");
        }

        return actualEntryBytes;
    }

    private static void PreserveFailedExtractionRoot(string safeExtractRoot)
    {
        // Deleting by path cannot prove no-follow semantics if an attacker swaps
        // the root or a descendant for a reparse point after validation. Preserve
        // the failed root for the retention cleanup and diagnostics instead.
        TryLog($"EXTRACT-FAILURE-PRESERVED root={safeExtractRoot}");
    }

    internal static long ValidatePackageArchiveEntries(
        IEnumerable<PackageArchiveEntryMetadata> entries,
        long packageSize)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (packageSize < 0 || packageSize > MaximumUpdatePackageBytes)
        {
            throw new InvalidOperationException(
                $"업데이트 패키지가 허용 크기({MaximumUpdatePackageBytes:N0}바이트)를 벗어납니다.");
        }

        var entryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directoryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var requiredDirectoryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalUncompressedBytes = 0;
        var entryCount = 0;

        foreach (var entry in entries)
        {
            entryCount++;
            if (entryCount > MaximumArchiveEntryCount)
            {
                throw new InvalidOperationException(
                    $"업데이트 패키지 항목 수가 허용 개수({MaximumArchiveEntryCount:N0}개)를 초과합니다.");
            }

            if (entry.Length < 0 || entry.Length > MaximumArchiveEntryBytes)
            {
                throw new InvalidOperationException(
                    $"업데이트 패키지 항목 크기가 허용 크기({MaximumArchiveEntryBytes:N0}바이트)를 벗어납니다: {entry.FullName}");
            }

            if (entry.Length > MaximumArchiveTotalUncompressedBytes - totalUncompressedBytes)
            {
                throw new InvalidOperationException(
                    $"업데이트 패키지의 압축 해제 크기가 허용 크기({MaximumArchiveTotalUncompressedBytes:N0}바이트)를 초과합니다.");
            }
            totalUncompressedBytes += entry.Length;

            if (IsArchiveReparsePoint(entry.ExternalAttributes))
            {
                throw new InvalidOperationException(
                    $"업데이트 패키지에 링크 또는 reparse 항목이 포함되어 있습니다: {entry.FullName}");
            }

            var normalizedPath = ValidateAndNormalizeArchiveEntryPath(entry.FullName, out var isDirectory);
            if (!entryPaths.Add(normalizedPath))
            {
                throw new InvalidOperationException(
                    $"업데이트 패키지에 Windows에서 같은 경로로 해석되는 항목이 있습니다: {entry.FullName}");
            }

            var segments = normalizedPath.Split('/');
            var ancestorPath = string.Empty;
            for (var index = 0; index < segments.Length - 1; index++)
            {
                ancestorPath = index == 0
                    ? segments[index]
                    : ancestorPath + "/" + segments[index];
                if (filePaths.Contains(ancestorPath))
                {
                    throw new InvalidOperationException(
                        $"업데이트 패키지에 파일과 디렉터리 경로 충돌이 있습니다: {entry.FullName}");
                }
                requiredDirectoryPaths.Add(ancestorPath);
            }

            if (isDirectory)
            {
                if (filePaths.Contains(normalizedPath))
                {
                    throw new InvalidOperationException(
                        $"업데이트 패키지에 파일과 디렉터리 경로 충돌이 있습니다: {entry.FullName}");
                }
                directoryPaths.Add(normalizedPath);
            }
            else
            {
                if (directoryPaths.Contains(normalizedPath) ||
                    requiredDirectoryPaths.Contains(normalizedPath))
                {
                    throw new InvalidOperationException(
                        $"업데이트 패키지에 파일과 디렉터리 경로 충돌이 있습니다: {entry.FullName}");
                }
                filePaths.Add(normalizedPath);
            }
        }

        return totalUncompressedBytes;
    }

    private static string ValidateAndNormalizeArchiveEntryPath(
        string entryPath,
        out bool isDirectory)
    {
        if (string.IsNullOrEmpty(entryPath))
            throw new InvalidOperationException("업데이트 패키지에 빈 경로 항목이 있습니다.");

        var normalizedPath = entryPath.Replace('\\', '/');
        if (normalizedPath.Length > MaximumArchivePathLength)
        {
            throw new InvalidOperationException(
                $"업데이트 패키지 경로가 너무 깁니다: {entryPath}");
        }

        if (normalizedPath.StartsWith('/') ||
            Path.IsPathRooted(normalizedPath) ||
            (normalizedPath.Length >= 2 &&
             char.IsAsciiLetter(normalizedPath[0]) &&
             normalizedPath[1] == ':'))
        {
            throw new InvalidOperationException(
                $"업데이트 패키지에 rooted 경로가 있습니다: {entryPath}");
        }

        isDirectory = normalizedPath.EndsWith('/');
        var segments = normalizedPath.Split('/');
        if (isDirectory)
            segments = segments[..^1];
        if (segments.Length == 0 || segments.Any(static segment => segment.Length == 0))
        {
            throw new InvalidOperationException(
                $"업데이트 패키지에 빈 경로 구간이 있습니다: {entryPath}");
        }

        foreach (var segment in segments)
        {
            if (segment is "." or "..")
            {
                throw new InvalidOperationException(
                    $"업데이트 패키지에 경로 이동 구간이 있습니다: {entryPath}");
            }
            if (segment.Length > MaximumArchivePathSegmentLength ||
                segment.EndsWith(' ') || segment.EndsWith('.') ||
                segment.Contains(':') ||
                segment.Any(static character => char.IsControl(character)) ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                IsReservedWindowsPathSegment(segment))
            {
                throw new InvalidOperationException(
                    $"업데이트 패키지에 Windows에서 안전하지 않은 경로 구간이 있습니다: {entryPath}");
            }
        }

        return string.Join('/', segments);
    }

    private static bool IsReservedWindowsPathSegment(string segment)
    {
        var baseName = segment.Split('.')[0];
        if (baseName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return baseName.Length == 4 &&
               (baseName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                 baseName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
               IsWindowsDeviceNumber(baseName[3]);
    }

    private static bool IsWindowsDeviceNumber(char character)
        => character is >= '1' and <= '9' or '\u00B9' or '\u00B2' or '\u00B3';

    private static bool IsArchiveReparsePoint(int externalAttributes)
    {
        const int unixFileTypeMask = 0xF000;
        const int unixSymbolicLink = 0xA000;
        var unixMode = (externalAttributes >> 16) & 0xFFFF;
        return (externalAttributes & (int)FileAttributes.ReparsePoint) != 0 ||
               (unixMode & unixFileTypeMask) == unixSymbolicLink;
    }

    private static string PrepareEmptyExtractionRoot(string extractRoot)
    {
        var fullExtractRoot = Path.GetFullPath(extractRoot);
        if (File.Exists(fullExtractRoot) || Directory.Exists(fullExtractRoot))
        {
            throw new InvalidOperationException(
                $"업데이트 압축 해제 경로가 이미 존재합니다: {fullExtractRoot}");
        }

        for (var parent = Directory.GetParent(fullExtractRoot);
             parent is not null;
             parent = parent.Parent)
        {
            if (!parent.Exists)
                continue;
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    $"업데이트 압축 해제 경로에 reparse 디렉터리가 포함되어 있습니다: {parent.FullName}");
            }
        }

        Directory.CreateDirectory(fullExtractRoot);
        if ((File.GetAttributes(fullExtractRoot) & FileAttributes.ReparsePoint) != 0 ||
            Directory.EnumerateFileSystemEntries(fullExtractRoot).Any())
        {
            throw new InvalidOperationException(
                $"업데이트 압축 해제 경로를 안전하게 준비하지 못했습니다: {fullExtractRoot}");
        }
        return fullExtractRoot;
    }

    internal readonly record struct PackageArchiveEntryMetadata(
        string FullName,
        long Length,
        int ExternalAttributes = 0);

    private readonly record struct RawPackageArchiveEntry(
        long CompressedLength,
        long UncompressedLength);

    private static void VerifyExpectedPackageFileSize(string filePath, long expectedFileSize)
    {
        if (expectedFileSize <= 0)
            return;

        var actualFileSize = new FileInfo(filePath).Length;
        if (actualFileSize != expectedFileSize)
        {
            throw new InvalidOperationException(
                $"업데이트 패키지 크기가 manifest와 일치하지 않습니다. 기록 {expectedFileSize:N0}바이트, 실제 {actualFileSize:N0}바이트입니다.");
        }

        TryLog($"FILESIZE verified {actualFileSize}");
    }

    internal static void EnsureExpectedProcessIdentity(UpdateArguments options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.ProcessId <= 0)
            throw new ProcessIdentityValidationException(
                "업데이트를 요청한 거래플랜 프로세스 ID가 없습니다.");

        Process process;
        try
        {
            process = Process.GetProcessById(options.ProcessId);
        }
        catch (ArgumentException ex)
        {
            throw new ProcessIdentityValidationException(
                "업데이트를 요청한 거래플랜 프로세스가 이미 종료되었습니다.",
                ex);
        }

        using (process)
        {
            bool hasExited;
            try
            {
                hasExited = process.HasExited;
            }
            catch (InvalidOperationException ex)
            {
                throw new ProcessIdentityValidationException(
                    "업데이트를 요청한 거래플랜 프로세스 상태를 확인하지 못했습니다.",
                    ex);
            }

            if (hasExited)
            {
                throw new ProcessIdentityValidationException(
                    "업데이트를 요청한 거래플랜 프로세스가 이미 종료되었습니다.");
            }

            EnsureProcessIdentityMatches(process, options);
        }
    }

    internal static async Task SignalDesktopHandoffAsync(UpdateArguments options)
    {
        ArgumentNullException.ThrowIfNull(options);

        using var timeoutCts = new CancellationTokenSource(DesktopIdentityHandoffTimeout);
        await using var handoffPipe = new NamedPipeClientStream(
            ".",
            options.HandoffPipeName,
            PipeDirection.Out,
            PipeOptions.Asynchronous,
            System.Security.Principal.TokenImpersonationLevel.Identification);
        try
        {
            await handoffPipe.ConnectAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException ex)
        {
            throw new InvalidOperationException(
                "거래플랜 데스크톱 handoff 연결 시간이 초과되었습니다.",
                ex);
        }

        if (!GetNamedPipeServerProcessId(
                handoffPipe.SafePipeHandle,
                out var serverProcessId) ||
            serverProcessId != (uint)options.ProcessId)
        {
            throw new ProcessIdentityValidationException(
                "업데이트 handoff를 만든 프로세스가 검증된 거래플랜 프로세스와 일치하지 않습니다.");
        }

        await handoffPipe.WriteAsync(
            new[] { UpdaterHandoffProtocol.IdentityVerifiedMarker },
            timeoutCts.Token);
        await handoffPipe.FlushAsync(timeoutCts.Token);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(
        SafePipeHandle pipe,
        out uint serverProcessId);

    private static async Task WaitForProcessExitAsync(UpdateArguments options)
    {
        if (options.ProcessId <= 0)
            throw new ProcessIdentityValidationException(
                "종료할 거래플랜 프로세스 ID가 없습니다.");

        try
        {
            using var process = Process.GetProcessById(options.ProcessId);
            if (process.HasExited)
                return;

            EnsureProcessIdentityMatches(process, options);

            if (await WaitForProcessExitWithinAsync(process, ProcessExitGracePeriod))
            {
                TryLog($"PROCESS exited pid={options.ProcessId}");
                return;
            }

            TryLog($"PROCESS close requested after grace timeout pid={options.ProcessId}");
            try
            {
                if (!process.CloseMainWindow())
                    TryLog($"PROCESS close main window unavailable pid={options.ProcessId}");
            }
            catch (InvalidOperationException)
            {
                TryLog($"PROCESS exited before close request pid={options.ProcessId}");
                return;
            }

            if (await WaitForProcessExitWithinAsync(process, ProcessCloseWindowGracePeriod))
            {
                TryLog($"PROCESS exited after close request pid={options.ProcessId}");
                return;
            }

            EnsureProcessIdentityMatches(process, options);
            TryLog($"PROCESS kill requested pid={options.ProcessId}");
            process.Kill();
            await process.WaitForExitAsync();
            TryLog($"PROCESS killed pid={options.ProcessId}");
        }
        catch (ArgumentException)
        {
            TryLog($"PROCESS already exited pid={options.ProcessId}");
        }
        catch (ProcessIdentityValidationException)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            TryLog($"PROCESS already exited pid={options.ProcessId}");
        }
    }

    private static void EnsureProcessIdentityMatches(Process process, UpdateArguments options)
    {
        string actualPath;
        long actualStartTimeUtcTicks;
        try
        {
            actualPath = Path.GetFullPath(process.MainModule?.FileName
                ?? throw new InvalidOperationException("실행 파일 경로를 확인할 수 없습니다."));
            actualStartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw new ProcessIdentityValidationException(
                "종료 대상 프로세스의 신원을 안전하게 확인하지 못했습니다. 업데이트를 중단합니다.",
                ex);
        }

        if (!string.Equals(
                actualPath,
                options.ExpectedProcessExePath,
                StringComparison.OrdinalIgnoreCase) ||
            actualStartTimeUtcTicks != options.ProcessStartTimeUtcTicks)
        {
            throw new ProcessIdentityValidationException(
                "종료 대상 프로세스가 업데이트를 요청한 거래플랜 프로세스와 일치하지 않습니다. 업데이트를 중단합니다.");
        }
    }

    private static async Task<bool> WaitForProcessExitWithinAsync(Process process, TimeSpan timeout)
    {
        if (process.HasExited)
            return true;

        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static void EnsureWorkDriveFreeSpace(string workRoot, long packageBytes)
    {
        if (packageBytes <= 0)
            return;

        var drive = GetDriveInfo(workRoot);
        var requiredBytes = Math.Max(MinimumUpdaterWorkBytes, checked(packageBytes * 4));
        if (drive.AvailableFreeSpace >= requiredBytes)
            return;

        throw new InvalidOperationException(
            $"{drive.Name} 드라이브 여유 공간이 부족합니다. 업데이트 준비에 최소 {FormatBytes(requiredBytes)} 정도가 필요합니다. 현재 여유 공간: {FormatBytes(drive.AvailableFreeSpace)}");
    }

    internal static void EnsureWorkDriveFreeSpaceForExtraction(
        string extractRoot,
        long totalUncompressedBytes,
        Func<string, long>? availableFreeSpaceProvider = null)
    {
        if (totalUncompressedBytes < 0 ||
            totalUncompressedBytes > MaximumArchiveTotalUncompressedBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(totalUncompressedBytes));
        }

        var requiredBytes = Math.Max(
            MinimumUpdaterWorkBytes,
            checked(totalUncompressedBytes + InstallBufferBytes));
        var drive = GetDriveInfo(extractRoot);
        var availableFreeSpace = availableFreeSpaceProvider is null
            ? drive.AvailableFreeSpace
            : availableFreeSpaceProvider(extractRoot);
        if (availableFreeSpace < 0)
        {
            throw new InvalidOperationException(
                "작업 드라이브 여유 공간을 확인하지 못했습니다.");
        }
        if (availableFreeSpace >= requiredBytes)
            return;

        throw new InvalidOperationException(
            $"{drive.Name} 드라이브 여유 공간이 부족합니다. 압축 해제에 최소 {FormatBytes(requiredBytes)} 정도가 필요합니다. 현재 여유 공간: {FormatBytes(availableFreeSpace)}");
    }

    private static void EnsureInstallDriveFreeSpace(string extractRoot, string installRoot)
    {
        var installDrive = GetDriveInfo(installRoot);
        var extractDrive = GetDriveInfo(extractRoot);
        if (!string.Equals(installDrive.Name, extractDrive.Name, StringComparison.OrdinalIgnoreCase))
            return;

        var extractedSize = GetDirectorySize(extractRoot);
        var requiredBytes = Math.Max(InstallBufferBytes, checked(extractedSize + 128L * 1024 * 1024));
        if (installDrive.AvailableFreeSpace >= requiredBytes)
            return;

        throw new InvalidOperationException(
            $"{installDrive.Name} 드라이브 여유 공간이 부족합니다. 설치에 최소 {FormatBytes(requiredBytes)} 정도가 필요합니다. 현재 여유 공간: {FormatBytes(installDrive.AvailableFreeSpace)}");
    }

    private static DriveInfo GetDriveInfo(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException($"드라이브 경로를 확인하지 못했습니다: {path}");

        return new DriveInfo(root);
    }

    private static long GetDirectorySize(string path)
    {
        if (!Directory.Exists(path))
            return 0;

        return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Select(file => new FileInfo(file).Length)
            .Aggregate(0L, (total, length) => checked(total + length));
    }

    internal static bool RequiresElevation(string installRoot)
    {
        var fullPath = InstallRootPathIdentity.Resolve(installRoot);
        var protectedRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows)
        }
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Select(InstallRootPathIdentity.Resolve);

        return protectedRoots.Any(root =>
            string.Equals(
                fullPath,
                root,
                StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(
                root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase));
    }

    internal static IReadOnlyList<string> GetInstallerMutationGateRoots(
        string installRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
        EnsureInstallerInstallRootScope(installRoot);

        var fullInstallRoot =
            InstallRootPathIdentity.Resolve(installRoot);
        var roots = new List<string> { fullInstallRoot };
        var canonicalRoot = GetCanonicalInstallRoot();
        if (IsLegacyInstallRoot(installRoot) ||
            string.Equals(
                Path.TrimEndingDirectorySeparator(fullInstallRoot),
                canonicalRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            roots.Add(canonicalRoot);
            roots.Add(GetLegacyInstallRoot());
        }

        return roots
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(InstallRootUpdateLock.CreateMutexName, StringComparer.Ordinal)
            .ToArray();
    }

    internal static IReadOnlyList<string> GetGeneratedRecoveryProbeRoots(
        string installRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);

        var fullInstallRoot =
            InstallRootPathIdentity.Resolve(installRoot);
        var canonicalRoot = GetCanonicalInstallRoot();
        var legacyRoot = GetLegacyInstallRoot();
        if (!string.Equals(
                fullInstallRoot,
                canonicalRoot,
                StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(
                fullInstallRoot,
                legacyRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            return [fullInstallRoot];
        }

        return new[]
        {
            canonicalRoot,
            legacyRoot
        }
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    }

    internal static IReadOnlyList<LegacyRollbackRecoveryTarget>
        GetLegacyRollbackRecoveryTargets(UpdateArguments options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.InstallRoot);

        return GetGeneratedRecoveryProbeRoots(options.InstallRoot)
            .Where(
                static installRoot =>
                    !RequiresElevation(installRoot))
            .Select(
                installRoot =>
                    new LegacyRollbackRecoveryTarget(
                        installRoot,
                        string.Equals(
                            installRoot,
                            options.InstallRoot,
                            StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(
                            options.LegacyInstallRoot)
                            ? options.LegacyInstallRoot
                            : installRoot))
            .ToArray();
    }

    internal static string GetEffectiveInstallerInstallRoot(string installRoot)
    {
        EnsureInstallerInstallRootScope(installRoot);
        return IsLegacyInstallRoot(installRoot)
            ? GetCanonicalInstallRoot()
            : InstallRootPathIdentity.Resolve(installRoot);
    }

    internal static void EnsureInstallerInstallRootScope(string installRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);

        var fullInstallRoot =
            InstallRootPathIdentity.Resolve(installRoot);
        var canonicalRoot = GetCanonicalInstallRoot();
        var legacyRoot = GetLegacyInstallRoot();
        if (string.Equals(
                fullInstallRoot,
                canonicalRoot,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                fullInstallRoot,
                legacyRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (InstallRootPathIdentity.PathsOverlap(
                fullInstallRoot,
                canonicalRoot) ||
            InstallRootPathIdentity.PathsOverlap(
                fullInstallRoot,
                legacyRoot))
        {
            throw new InvalidOperationException(
                "custom 설치 경로는 canonical/legacy 설치 경로와 조상 또는 자손 관계일 수 없습니다.");
        }
    }

    internal static bool IsLegacyInstallRoot(string installRoot)
        => string.Equals(
            InstallRootPathIdentity.Resolve(installRoot),
            GetLegacyInstallRoot(),
            StringComparison.OrdinalIgnoreCase);

    internal static string GetCanonicalInstallRoot()
    {
        var programFilesRoot =
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (string.IsNullOrWhiteSpace(programFilesRoot))
        {
            programFilesRoot =
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        }

        if (string.IsNullOrWhiteSpace(programFilesRoot))
        {
            throw new InvalidOperationException(
                "Program Files 경로를 확인하지 못했습니다.");
        }

        return InstallRootPathIdentity.Resolve(
            Path.Combine(programFilesRoot, "tradeplan"));
    }

    internal static string GetLegacyInstallRoot()
    {
        var localApplicationData =
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException(
                "LocalApplicationData 경로를 확인하지 못했습니다.");
        }

        return InstallRootPathIdentity.Resolve(
            Path.Combine(localApplicationData, "Programs", "거래플랜"));
    }

    private static string ResolvePowerShellPath()
    {
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var candidate = Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(candidate) ? candidate : "powershell.exe";
    }

    internal static void EnsureInstallSupervisorContract(
        string installScriptPath)
    {
        const string contractMarker =
            "GEORAEPLAN_INSTALL_SUPERVISOR_CONTRACT_V1";
        var script = File.ReadAllText(
            installScriptPath,
            Encoding.UTF8);
        if (!script.Contains(
                contractMarker,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "보호된 설치 경로에는 timeout rollback supervisor 계약이 포함된 설치 패키지가 필요합니다.");
        }
    }

    internal static void EnsureInstallRecoveryOnlyContract(
        string installScriptPath)
    {
        const string contractMarker =
            "GEORAEPLAN_INSTALL_RECOVERY_ONLY_CONTRACT_V1";
        var script = File.ReadAllText(
            installScriptPath,
            Encoding.UTF8);
        if (!script.Contains(
                contractMarker,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "중단된 업데이트를 복구하려면 RecoveryOnly 계약이 포함된 검증된 설치 패키지가 필요합니다.");
        }
    }

    private static void SetStage(UpdateProgressWindow window, string title, string detail)
    {
        TryLog($"STAGE {title} :: {detail}");
        window.Dispatcher.Invoke(() => window.SetStatus(title, detail));
    }

    private static void TryLog(string message)
    {
        if (string.IsNullOrWhiteSpace(_sessionLogPath))
            return;

        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}";
            File.AppendAllText(_sessionLogPath, line, Encoding.UTF8);
        }
        catch
        {
            // ignore logging failures
        }
    }

    private static void TryCleanupStaleUpdateArtifacts()
    {
        var georaePlanTempRoot = GetUpdateArtifactRoot();
        TryCleanupChildDirectories(Path.Combine(georaePlanTempRoot, "prepared-updates"));
        TryCleanupChildDirectories(Path.Combine(georaePlanTempRoot, "updates"));
        TryCleanupChildDirectories(Path.Combine(georaePlanTempRoot, "updater-run"));
    }

    private static string GetUpdateArtifactRoot()
    {
        var root = Path.Combine(ResolveWorkTempRoot(), "GeoraePlan");
        Directory.CreateDirectory(root);
        return root;
    }

    internal static string CreateUpdateWorkRoot()
        => Path.Combine(
            GetUpdateArtifactRoot(),
            "updates",
            $"{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{Environment.ProcessId}_{Guid.NewGuid():N}");

    private static string ResolveWorkTempRoot()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable(TempRootOverrideEnvironmentKey),
            Path.Combine("D:\\", "거래플랜", "temp"),
            Path.GetTempPath()
        };

        foreach (var candidate in candidates)
        {
            if (TryPrepareWritableDirectory(candidate, out var resolvedPath))
            {
                Environment.SetEnvironmentVariable(TempRootOverrideEnvironmentKey, resolvedPath);
                Environment.SetEnvironmentVariable("TEMP", resolvedPath);
                Environment.SetEnvironmentVariable("TMP", resolvedPath);
                return resolvedPath;
            }
        }

        return Path.GetTempPath();
    }

    private static bool TryPrepareWritableDirectory(string? path, out string resolvedPath)
    {
        resolvedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            resolvedPath = Path.GetFullPath(path);
            Directory.CreateDirectory(resolvedPath);

            var probePath = Path.Combine(resolvedPath, $".write-test-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probePath, string.Empty);
            File.Delete(probePath);
            return true;
        }
        catch
        {
            resolvedPath = string.Empty;
            return false;
        }
    }

    private static void TryCleanupChildDirectories(string rootPath)
    {
        if (!Directory.Exists(rootPath))
            return;

        var cutoffUtc = DateTime.UtcNow - UpdateArtifactRetention;
        foreach (var directory in Directory.EnumerateDirectories(rootPath))
        {
            try
            {
                var lastWriteUtc = Directory.GetLastWriteTimeUtc(directory);
                if (lastWriteUtc > cutoffUtc)
                    continue;

                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // 다음 실행에서 다시 정리 시도
            }
        }
    }

    /// <summary>
    /// Removes update-only cache directories after a verified installation or
    /// a verified already-current version decision. Active updater directories
    /// are explicitly protected and deleted by the post-exit cleanup instead.
    /// </summary>
    internal static int TryCleanupSupersededUpdateArtifacts(
        string artifactRoot,
        params string?[] protectedDirectoryPaths)
    {
        if (string.IsNullOrWhiteSpace(artifactRoot) ||
            !Directory.Exists(artifactRoot))
        {
            return 0;
        }

        var safeArtifactRoot = Path.GetFullPath(artifactRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var protectedPaths = protectedDirectoryPaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => Path.GetFullPath(path!))
            .ToArray();
        var removedCount = 0;

        foreach (var categoryName in new[]
                 {
                     "prepared-updates",
                     "updates",
                     "updater-run"
                 })
        {
            var categoryRoot = Path.GetFullPath(
                Path.Combine(safeArtifactRoot, categoryName));
            if (!IsSamePathOrDescendant(categoryRoot, safeArtifactRoot) ||
                !Directory.Exists(categoryRoot))
            {
                continue;
            }

            string[] categoryDirectories;
            try
            {
                categoryDirectories = Directory.GetDirectories(categoryRoot);
            }
            catch (Exception ex)
            {
                TryLog(
                    $"CACHE-CLEANUP deferred={categoryRoot} reason={ex.GetType().Name}:{ex.Message}");
                continue;
            }

            foreach (var directory in categoryDirectories)
            {
                var candidate = Path.GetFullPath(directory);
                if (protectedPaths.Any(
                        protectedPath =>
                            IsSamePathOrDescendant(protectedPath, candidate)))
                {
                    continue;
                }

                try
                {
                    var attributes = File.GetAttributes(candidate);
                    Directory.Delete(
                        candidate,
                        recursive: (attributes & FileAttributes.ReparsePoint) == 0);
                    removedCount++;
                    TryLog($"CACHE-CLEANUP removed={candidate}");
                }
                catch (Exception ex)
                {
                    TryLog(
                        $"CACHE-CLEANUP deferred={candidate} reason={ex.GetType().Name}:{ex.Message}");
                }
            }
        }

        return removedCount;
    }

    private static bool IsSamePathOrDescendant(
        string candidatePath,
        string rootPath)
    {
        var candidate = Path.GetFullPath(candidatePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetFullPath(rootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(
                   root + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetCurrentUpdaterStagingRoot()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
            return null;

        var currentDirectory = Path.GetDirectoryName(processPath);
        if (string.IsNullOrWhiteSpace(currentDirectory))
            return null;

        var parentDirectory = Directory.GetParent(currentDirectory);
        if (parentDirectory is null || !string.Equals(parentDirectory.Name, "updater-run", StringComparison.OrdinalIgnoreCase))
            return null;

        var expectedRoot = GetUpdateArtifactRoot().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var actualRoot = parentDirectory.Parent?.FullName?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(actualRoot, expectedRoot, StringComparison.OrdinalIgnoreCase))
            return null;

        return currentDirectory;
    }

    internal static Process? SchedulePostExitCleanup(
        params string?[] directoryPaths)
    {
        var targets = directoryPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (targets.Length == 0)
            return null;

        var targetLiterals = string.Join(
            ",",
            targets.Select(
                target =>
                    "'" + target.Replace("'", "''") + "'"));
        var cleanupScript =
            "$ErrorActionPreference='SilentlyContinue';" +
            "Start-Sleep -Seconds 1;" +
            $"$targets=@({targetLiterals});" +
            "for($attempt=0;$attempt -lt 120;$attempt++){" +
            "foreach($target in $targets){Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction SilentlyContinue};" +
            "if(@($targets | Where-Object { Test-Path -LiteralPath $_ }).Count -eq 0){exit 0};" +
            "Start-Sleep -Seconds 1" +
            "};exit 1";
        var encodedCleanupScript = Convert.ToBase64String(
            Encoding.Unicode.GetBytes(cleanupScript));

        var cleanupStartInfo = new ProcessStartInfo
        {
            FileName = ResolvePowerShellPath(),
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        cleanupStartInfo.ArgumentList.Add("-NoProfile");
        cleanupStartInfo.ArgumentList.Add("-NonInteractive");
        cleanupStartInfo.ArgumentList.Add("-WindowStyle");
        cleanupStartInfo.ArgumentList.Add("Hidden");
        cleanupStartInfo.ArgumentList.Add("-EncodedCommand");
        cleanupStartInfo.ArgumentList.Add(encodedCleanupScript);
        var cleanupProcess = Process.Start(cleanupStartInfo)
            ?? throw new InvalidOperationException(
            "업데이트 임시 폴더 정리 프로세스를 시작하지 못했습니다.");
        TryLog(
            $"CLEANUP-SCHEDULE pid={cleanupProcess.Id} targets={string.Join("|", targets)}");
        return cleanupProcess;
    }

    private static string QuoteArgument(string value)
        => "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";

    private static int CompareVersions(string left, string right)
    {
        if (!Version.TryParse(NormalizeVersionText(left), out var leftVersion))
            leftVersion = new Version(0, 0, 0);
        if (!Version.TryParse(NormalizeVersionText(right), out var rightVersion))
            rightVersion = new Version(0, 0, 0);

        return leftVersion.CompareTo(rightVersion);
    }

    private static string NormalizeVersionText(string raw)
    {
        var normalized = (raw ?? string.Empty).Trim();
        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[1..];

        var plusIndex = normalized.IndexOf('+');
        if (plusIndex >= 0)
            normalized = normalized[..plusIndex];

        return string.IsNullOrWhiteSpace(normalized) ? "0.0.0" : normalized;
    }

    private static bool TryParseVersionStrict(
        string raw,
        out Version version)
    {
        version = null!;
        var normalized = (raw ?? string.Empty).Trim();
        if (normalized.StartsWith(
                "v",
                StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[1..];
        }

        var plusIndex = normalized.IndexOf('+');
        if (plusIndex >= 0)
            normalized = normalized[..plusIndex];

        if (string.IsNullOrWhiteSpace(normalized) ||
            !Version.TryParse(normalized, out var parsedVersion) ||
            parsedVersion is null)
        {
            return false;
        }

        version = parsedVersion;
        return true;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024d;
            unitIndex++;
        }

        return $"{value:0.##} {units[unitIndex]}";
    }

    private readonly record struct DownloadProgress(long DownloadedBytes, long? TotalBytes);
}

internal enum InstalledVersionState
{
    Absent,
    Older,
    AtLeastRequested,
    Unparseable
}

internal sealed record LegacyRollbackRecoveryTarget(
    string InstallRoot,
    string LegacyInstallRoot);

internal sealed class UpdateArguments
{
    internal const string HandoffPipeNamePrefix = "GeoraePlan.Updater.Handoff.";

    public string PackageUrl { get; init; } = string.Empty;
    public string PackagePath { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public string InstallRoot { get; init; } = string.Empty;
    public string LegacyInstallRoot { get; init; } = string.Empty;
    public string LaunchExe { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string RequestMetadataPath { get; init; } = string.Empty;
    public long FileSize { get; init; }
    public int ProcessId { get; init; }
    public string ExpectedProcessExePath { get; init; } = string.Empty;
    public long ProcessStartTimeUtcTicks { get; init; }
    public string HandoffPipeName { get; init; } = string.Empty;

    public static UpdateArguments Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (!key.StartsWith("--", StringComparison.Ordinal))
                continue;

            var value = i + 1 < args.Length ? args[i + 1] : string.Empty;
            values[key] = value;
            i++;
        }

        var packageUrl = values.GetValueOrDefault("--package-url", string.Empty).Trim();
        var packagePath = values.GetValueOrDefault("--package-path", string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(packageUrl) && string.IsNullOrWhiteSpace(packagePath))
            throw new InvalidOperationException("필수 인자가 없습니다: --package-url 또는 --package-path");

        var legacyInstallRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(
                Require(values, "--install-root")));
        var installRoot = InstallRootPathIdentity.Resolve(
            legacyInstallRoot);
        Program.EnsureInstallerInstallRootScope(installRoot);
        var launchExe = InstallRootPathIdentity.Resolve(
            Require(values, "--launch-exe"));
        var normalizedInstallRoot =
            installRoot + Path.DirectorySeparatorChar;
        if (!launchExe.StartsWith(normalizedInstallRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "재실행 파일이 지정된 설치 경로 밖에 있습니다.");
        }
        var fileName = values.GetValueOrDefault("--file-name", string.Empty);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = !string.IsNullOrWhiteSpace(packagePath)
                ? Path.GetFileName(packagePath)
                : Path.GetFileName(new Uri(packageUrl).AbsolutePath);
        }

        if (!int.TryParse(Require(values, "--process-id"), out var processId) || processId <= 0)
            throw new InvalidOperationException("--process-id는 0보다 큰 정수여야 합니다.");

        var expectedProcessExePath = Require(values, "--process-exe");
        if (!Path.IsPathFullyQualified(expectedProcessExePath))
        {
            throw new InvalidOperationException(
                "--process-exe에는 절대 경로가 필요합니다.");
        }

        expectedProcessExePath = Path.GetFullPath(expectedProcessExePath);
        if (!long.TryParse(
                Require(values, "--process-start-time-utc-ticks"),
                out var processStartTimeUtcTicks) ||
            processStartTimeUtcTicks <= 0)
        {
            throw new InvalidOperationException(
                "--process-start-time-utc-ticks는 0보다 큰 정수여야 합니다.");
        }

        if (!expectedProcessExePath.StartsWith(
                normalizedInstallRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "종료 대상 실행 파일이 지정된 설치 경로 밖에 있습니다.");
        }

        if (!string.Equals(
                expectedProcessExePath,
                launchExe,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "종료 대상 실행 파일과 업데이트 후 재실행 파일이 일치하지 않습니다.");
        }

        var handoffPipeName = Require(values, "--handoff-pipe");
        var handoffSuffix = handoffPipeName.StartsWith(
            HandoffPipeNamePrefix,
            StringComparison.Ordinal)
            ? handoffPipeName[HandoffPipeNamePrefix.Length..]
            : string.Empty;
        if (!Guid.TryParseExact(handoffSuffix, "N", out _))
        {
            throw new InvalidOperationException(
                "--handoff-pipe 형식이 올바르지 않습니다.");
        }

        var requestedVersion = Require(values, "--version");
        var normalizedRequestedVersion = requestedVersion.Trim();
        if (normalizedRequestedVersion.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            normalizedRequestedVersion = normalizedRequestedVersion[1..];
        var buildMetadataIndex = normalizedRequestedVersion.IndexOf('+');
        if (buildMetadataIndex >= 0)
            normalizedRequestedVersion = normalizedRequestedVersion[..buildMetadataIndex];
        if (!System.Version.TryParse(normalizedRequestedVersion, out _))
            throw new InvalidOperationException("--version 형식이 올바르지 않습니다.");

        return new UpdateArguments
        {
            PackageUrl = packageUrl,
            PackagePath = packagePath,
            Sha256 = Require(values, "--sha256"),
            InstallRoot = installRoot,
            LegacyInstallRoot = legacyInstallRoot,
            LaunchExe = launchExe,
            Version = requestedVersion,
            FileName = fileName,
            RequestMetadataPath = values.GetValueOrDefault("--request-metadata-path", string.Empty),
            FileSize = long.TryParse(values.GetValueOrDefault("--file-size", "0"), out var fileSize) ? fileSize : 0,
            ProcessId = processId,
            ExpectedProcessExePath = expectedProcessExePath,
            ProcessStartTimeUtcTicks = processStartTimeUtcTicks,
            HandoffPipeName = handoffPipeName
        };
    }

    private static string Require(Dictionary<string, string> values, string key)
    {
        if (!values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"필수 인자가 없습니다: {key}");

        return value.Trim();
    }
}

internal static class UpdaterHandoffProtocol
{
    internal const byte IdentityVerifiedMarker = 0xA5;
}

internal sealed class ProcessIdentityValidationException : InvalidOperationException
{
    public ProcessIdentityValidationException(string message)
        : base(message)
    {
    }

    public ProcessIdentityValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed class UpdateRequestMetadata
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly byte[] MetadataEntropy =
        Encoding.UTF8.GetBytes("GeoraePlan.UpdaterRequestMetadata.v1");

    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ProtectedHeaders { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public static UpdateRequestMetadata LoadAndDelete(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return new UpdateRequestMetadata();

        try
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("업데이트 인증 메타데이터 파일을 찾지 못했습니다.", filePath);

            var json = File.ReadAllText(filePath, Encoding.UTF8);
            return JsonSerializer.Deserialize<UpdateRequestMetadata>(json, JsonOptions) ?? new UpdateRequestMetadata();
        }
        finally
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
                    File.Delete(filePath);
            }
            catch
            {
                // 다음 정리 단계에서 다시 삭제 시도
            }
        }
    }

    public void ApplyTo(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);

        foreach (var header in Headers)
            ApplyHeader(request, header.Key, header.Value);

        foreach (var header in ProtectedHeaders)
            ApplyHeader(request, header.Key, UnprotectMetadataValue(header.Value));
    }

    private static void ApplyHeader(HttpRequestMessage request, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            return;

        if (!request.Headers.TryAddWithoutValidation(key, value))
            request.Content?.Headers.TryAddWithoutValidation(key, value);
    }

    private static string UnprotectMetadataValue(string protectedValue)
    {
        var protectedBytes = Convert.FromBase64String(protectedValue);
        var plainBytes = ProtectedData.Unprotect(
            protectedBytes,
            MetadataEntropy,
            DataProtectionScope.CurrentUser);
        try
        {
            return Encoding.UTF8.GetString(plainBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
        }
    }
}
