using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace 거래플랜.Updater;

internal static class Program
{
    private const long MinimumUpdaterWorkBytes = 512L * 1024 * 1024;
    private const long InstallBufferBytes = 256L * 1024 * 1024;
    private static readonly TimeSpan UpdateArtifactRetention = TimeSpan.FromDays(3);
    private static readonly TimeSpan ProcessExitGracePeriod = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ProcessCloseWindowGracePeriod = TimeSpan.FromSeconds(15);

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

            window.ShowFailure("업데이트를 완료하지 못했습니다.", message);
            MessageBox.Show(
                $"업데이트를 완료하지 못했습니다.{Environment.NewLine}{Environment.NewLine}{message}",
                "거래플랜 업데이터",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            app.Shutdown(1);
        }
    }

    private static async Task ExecuteAsync(UpdateArguments options, UpdateProgressWindow window)
    {
        TryCleanupStaleUpdateArtifacts();

        var workRoot = Path.Combine(Path.GetTempPath(), "GeoraePlan", "updates", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
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
        var requestMetadata = UpdateRequestMetadata.LoadAndDelete(options.RequestMetadataPath);

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

        SetStage(window, "프로그램 종료 대기 중", "현재 실행 중인 거래플랜을 종료하고 있습니다.");
        await WaitForProcessExitAsync(options.ProcessId);

        var extractRoot = Path.Combine(workRoot, "package");
        SetStage(window, "설치 파일 준비 중", "업데이트 패키지를 압축 해제하고 있습니다.");
        ZipFile.ExtractToDirectory(packagePath, extractRoot, overwriteFiles: true);
        EnsureInstallDriveFreeSpace(extractRoot, options.InstallRoot);

        var installScriptPath = Path.Combine(extractRoot, "Install-GeoraePlan.ps1");
        if (!File.Exists(installScriptPath))
            throw new FileNotFoundException("설치 스크립트를 찾지 못했습니다.", installScriptPath);

        SetStage(window, "업데이트 적용 중", "새 버전 파일을 설치 위치에 복사하고 있습니다.");
        await RunInstallScriptAsync(options, extractRoot, installScriptPath, _sessionLogPath);
        ValidateInstalledApplication(options);

        if (!string.IsNullOrWhiteSpace(options.LaunchExe) && File.Exists(options.LaunchExe))
        {
            SetStage(window, "업데이트 완료", "최신 버전으로 다시 실행하고 있습니다.");
            Process.Start(new ProcessStartInfo
            {
                FileName = options.LaunchExe,
                WorkingDirectory = Path.GetDirectoryName(options.LaunchExe) ?? options.InstallRoot,
                UseShellExecute = true
            });
        }

        SchedulePostExitCleanup(workRoot, GetCurrentUpdaterStagingRoot());
        TryLog("SUCCESS");
    }

    private static async Task RunInstallScriptAsync(UpdateArguments options, string extractRoot, string installScriptPath, string? logPath)
    {
        var requiresElevation = RequiresElevation(options.InstallRoot);
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
            "-LogPath",
            QuoteArgument(logPath ?? string.Empty)
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

        if (!requiresElevation)
        {
            var stdoutTask = RelayStreamToLogAsync(installProcess.StandardOutput, "INSTALL-OUT");
            var stderrTask = RelayStreamToLogAsync(installProcess.StandardError, "INSTALL-ERR");
            await installProcess.WaitForExitAsync();
            await Task.WhenAll(stdoutTask, stderrTask);
        }
        else
        {
            await installProcess.WaitForExitAsync();
        }

        if (installProcess.ExitCode != 0)
            throw new InvalidOperationException($"업데이트 설치가 실패했습니다. exitCode={installProcess.ExitCode}");
    }

    private static void ValidateInstalledApplication(UpdateArguments options)
    {
        if (string.IsNullOrWhiteSpace(options.InstallRoot))
            throw new InvalidOperationException("설치 경로가 비어 있어 업데이트 결과를 검증할 수 없습니다.");

        if (!Directory.Exists(options.InstallRoot))
            throw new DirectoryNotFoundException($"설치 경로를 찾지 못했습니다: {options.InstallRoot}");

        if (string.IsNullOrWhiteSpace(options.LaunchExe) || !File.Exists(options.LaunchExe))
            throw new FileNotFoundException("업데이트 후 실행 파일을 찾지 못했습니다.", options.LaunchExe);

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
        if (string.IsNullOrWhiteSpace(sha256))
            return;

        await using var stream = File.OpenRead(filePath);
        using var algorithm = SHA256.Create();
        var hash = await algorithm.ComputeHashAsync(stream);
        var actual = Convert.ToHexString(hash);
        if (!string.Equals(actual, sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("다운로드한 업데이트 패키지의 SHA256 검증에 실패했습니다.");

        TryLog($"SHA256 verified {actual}");
    }

    private static async Task WaitForProcessExitAsync(int processId)
    {
        if (processId <= 0)
            return;

        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
                return;

            if (await WaitForProcessExitWithinAsync(process, ProcessExitGracePeriod))
            {
                TryLog($"PROCESS exited pid={processId}");
                return;
            }

            TryLog($"PROCESS close requested after grace timeout pid={processId}");
            try
            {
                if (!process.CloseMainWindow())
                    TryLog($"PROCESS close main window unavailable pid={processId}");
            }
            catch (InvalidOperationException)
            {
                TryLog($"PROCESS exited before close request pid={processId}");
                return;
            }

            if (await WaitForProcessExitWithinAsync(process, ProcessCloseWindowGracePeriod))
            {
                TryLog($"PROCESS exited after close request pid={processId}");
                return;
            }

            TryLog($"PROCESS kill requested pid={processId}");
            process.Kill();
            await process.WaitForExitAsync();
            TryLog($"PROCESS killed pid={processId}");
        }
        catch (ArgumentException)
        {
            TryLog($"PROCESS already exited pid={processId}");
        }
        catch (InvalidOperationException)
        {
            TryLog($"PROCESS already exited pid={processId}");
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

    private static bool RequiresElevation(string installRoot)
    {
        var fullPath = Path.GetFullPath(installRoot);
        var protectedRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows)
        }
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Select(path => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar);

        return protectedRoots.Any(root => fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolvePowerShellPath()
    {
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var candidate = Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(candidate) ? candidate : "powershell.exe";
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
        var georaePlanTempRoot = Path.Combine(Path.GetTempPath(), "GeoraePlan");
        TryCleanupChildDirectories(Path.Combine(georaePlanTempRoot, "prepared-updates"));
        TryCleanupChildDirectories(Path.Combine(georaePlanTempRoot, "updates"));
        TryCleanupChildDirectories(Path.Combine(georaePlanTempRoot, "updater-run"));
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

        var expectedRoot = Path.Combine(Path.GetTempPath(), "GeoraePlan").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var actualRoot = parentDirectory.Parent?.FullName?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(actualRoot, expectedRoot, StringComparison.OrdinalIgnoreCase))
            return null;

        return currentDirectory;
    }

    private static void SchedulePostExitCleanup(params string?[] directoryPaths)
    {
        var targets = directoryPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (targets.Length == 0)
            return;

        try
        {
            var arguments = "/c ping 127.0.0.1 -n 6 > nul";
            foreach (var target in targets)
                arguments += $" & rmdir /s /q {QuoteArgument(target)}";

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch (Exception ex)
        {
            TryLog($"CLEANUP-SCHEDULE failed {ex.Message}");
        }
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

internal sealed class UpdateArguments
{
    public string PackageUrl { get; init; } = string.Empty;
    public string PackagePath { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public string InstallRoot { get; init; } = string.Empty;
    public string LaunchExe { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string RequestMetadataPath { get; init; } = string.Empty;
    public long FileSize { get; init; }
    public int ProcessId { get; init; }

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

        var installRoot = Require(values, "--install-root");
        var launchExe = Require(values, "--launch-exe");
        var fileName = values.GetValueOrDefault("--file-name", string.Empty);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = !string.IsNullOrWhiteSpace(packagePath)
                ? Path.GetFileName(packagePath)
                : Path.GetFileName(new Uri(packageUrl).AbsolutePath);
        }

        return new UpdateArguments
        {
            PackageUrl = packageUrl,
            PackagePath = packagePath,
            Sha256 = Require(values, "--sha256"),
            InstallRoot = installRoot,
            LaunchExe = launchExe,
            Version = values.GetValueOrDefault("--version", string.Empty),
            FileName = fileName,
            RequestMetadataPath = values.GetValueOrDefault("--request-metadata-path", string.Empty),
            FileSize = long.TryParse(values.GetValueOrDefault("--file-size", "0"), out var fileSize) ? fileSize : 0,
            ProcessId = int.TryParse(values.GetValueOrDefault("--process-id", "0"), out var pid) ? pid : 0
        };
    }

    private static string Require(Dictionary<string, string> values, string key)
    {
        if (!values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"필수 인자가 없습니다: {key}");

        return value.Trim();
    }
}

internal sealed class UpdateRequestMetadata
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);

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
        {
            if (string.IsNullOrWhiteSpace(header.Key) || string.IsNullOrWhiteSpace(header.Value))
                continue;

            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
                request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }
}
