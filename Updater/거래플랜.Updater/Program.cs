using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows;

namespace 거래플랜.Updater;

internal static class Program
{
    private const long MinimumUpdaterWorkBytes = 512L * 1024 * 1024;
    private const long InstallBufferBytes = 256L * 1024 * 1024;

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
        var workRoot = Path.Combine(Path.GetTempPath(), "GeoraePlan", "updates", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(workRoot);
        _sessionLogPath = Path.Combine(workRoot, "update.log");
        TryLog($"START version={options.Version} package={options.PackageUrl}");

        SetStage(window, "업데이트 준비 중", "임시 작업 폴더와 설치 공간을 확인하고 있습니다.");
        EnsureWorkDriveFreeSpace(workRoot, options.FileSize);

        var packagePath = Path.Combine(workRoot, string.IsNullOrWhiteSpace(options.FileName)
            ? $"desktop-{options.Version}.zip"
            : options.FileName);

        SetStage(window, "업데이트 다운로드 중", "새 버전 파일을 가져오고 있습니다.");
        await DownloadAsync(options.PackageUrl, packagePath, progress =>
        {
            var detail = progress.TotalBytes.HasValue
                ? $"다운로드 {FormatBytes(progress.DownloadedBytes)} / {FormatBytes(progress.TotalBytes.Value)}"
                : $"다운로드 {FormatBytes(progress.DownloadedBytes)}";
            SetStage(window, "업데이트 다운로드 중", detail);
        });

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

    private static async Task DownloadAsync(string packageUrl, string targetPath, Action<DownloadProgress>? reportProgress = null)
    {
        using var http = new HttpClient();
        using var response = await http.GetAsync(packageUrl, HttpCompletionOption.ResponseHeadersRead);
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

            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            await process.WaitForExitAsync(timeout.Token);
            TryLog($"PROCESS exited pid={processId}");
        }
        catch (ArgumentException)
        {
            TryLog($"PROCESS already exited pid={processId}");
        }
        catch (OperationCanceledException ex)
        {
            throw new InvalidOperationException("기존 거래플랜 종료를 10분 이상 기다렸지만 완료되지 않았습니다.", ex);
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

    private static string QuoteArgument(string value)
        => "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";

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
    public string Sha256 { get; init; } = string.Empty;
    public string InstallRoot { get; init; } = string.Empty;
    public string LaunchExe { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
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

        var packageUrl = Require(values, "--package-url");
        var installRoot = Require(values, "--install-root");
        var launchExe = Require(values, "--launch-exe");

        return new UpdateArguments
        {
            PackageUrl = packageUrl,
            Sha256 = Require(values, "--sha256"),
            InstallRoot = installRoot,
            LaunchExe = launchExe,
            Version = values.GetValueOrDefault("--version", string.Empty),
            FileName = values.GetValueOrDefault("--file-name", Path.GetFileName(new Uri(packageUrl).AbsolutePath)),
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
