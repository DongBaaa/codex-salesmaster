using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

public sealed class DesktopAppUpdateService
{
    private const long MinimumUpdaterWorkBytes = 512L * 1024 * 1024;
    private const long InstallBufferBytes = 256L * 1024 * 1024;
    private static readonly TimeSpan UpdateArtifactRetention = TimeSpan.FromDays(3);

    private readonly ErpApiClient _api;

    public DesktopAppUpdateService(ErpApiClient api)
    {
        _api = api;
    }

    public string GetCurrentVersion()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
        {
            var fileVersion = FileVersionInfo.GetVersionInfo(processPath).ProductVersion;
            if (!string.IsNullOrWhiteSpace(fileVersion))
                return NormalizeVersionText(fileVersion);
        }

        var informational = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
            return NormalizeVersionText(informational);

        return NormalizeVersionText(Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "1.0.0");
    }

    public async Task<DesktopAppUpdateCheckResult> CheckForUpdatesAsync(string channel = "stable", CancellationToken ct = default)
    {
        var currentVersion = GetCurrentVersion();

        try
        {
            var manifest = await _api.GetUpdateManifestAsync(channel, ct);
            var package = manifest?.Desktop;
            if (package is null || string.IsNullOrWhiteSpace(package.Version))
            {
                return new DesktopAppUpdateCheckResult
                {
                    CurrentVersion = currentVersion,
                    LatestVersion = currentVersion,
                    Message = "배포된 PC 업데이트 정보를 찾지 못했습니다."
                };
            }

            var latestVersion = NormalizeVersionText(package.Version);
            var isUpdateAvailable = CompareVersions(latestVersion, currentVersion) > 0;

            return new DesktopAppUpdateCheckResult
            {
                CurrentVersion = currentVersion,
                LatestVersion = latestVersion,
                IsUpdateAvailable = isUpdateAvailable,
                Package = package,
                Message = isUpdateAvailable
                    ? $"새 PC 버전 {latestVersion}이 준비되어 있습니다."
                    : $"현재 버전({currentVersion})이 최신입니다."
            };
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return new DesktopAppUpdateCheckResult
            {
                CurrentVersion = currentVersion,
                LatestVersion = currentVersion,
                Message = "업데이트 매니페스트가 아직 배포되지 않았습니다."
            };
        }
    }

    public void StartUpdate(AppUpdatePackageDto package)
    {
        if (package is null)
            throw new ArgumentNullException(nameof(package));
        if (string.IsNullOrWhiteSpace(package.PackageUrl))
            throw new InvalidOperationException("업데이트 패키지 주소가 비어 있습니다.");
        if (string.IsNullOrWhiteSpace(package.Sha256))
            throw new InvalidOperationException("업데이트 SHA256 정보가 비어 있습니다.");

        var packageUrl = _api.ResolveAbsoluteUrl(package.PackageUrl);
        if (string.IsNullOrWhiteSpace(packageUrl))
            throw new InvalidOperationException("업데이트 패키지 주소를 절대 경로로 해석하지 못했습니다.");

        var packageUri = ValidatePackageUri(packageUrl, _api.GetBaseUri());

        var updaterPath = ResolveUpdaterPath();
        if (string.IsNullOrWhiteSpace(updaterPath))
            throw new FileNotFoundException("거래플랜.Updater.exe를 찾지 못했습니다. 현재 설치본에는 내부 업데이트 도우미가 없어 1회 수동 재설치가 필요할 수 있습니다.");

        var stagedUpdaterPath = StageUpdaterForExecution(updaterPath);

        var currentProcess = Process.GetCurrentProcess();
        var currentExePath = Environment.ProcessPath ?? currentProcess.MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(currentExePath))
            throw new InvalidOperationException("현재 실행 파일 경로를 확인하지 못했습니다.");

        var installRoot = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        EnsureSufficientDiskSpace(package.FileSize, installRoot);
        TryCleanupStaleUpdateArtifacts();

        var arguments = string.Join(" ", new[]
        {
            "--package-url",
            QuoteArgument(packageUri.ToString()),
            "--sha256",
            QuoteArgument(package.Sha256),
            "--install-root",
            QuoteArgument(installRoot),
            "--launch-exe",
            QuoteArgument(currentExePath),
            "--process-id",
            currentProcess.Id.ToString(),
            "--version",
            QuoteArgument(package.Version),
            "--file-size",
            package.FileSize.ToString(),
            "--file-name",
            QuoteArgument(package.FileName ?? string.Empty),
            "--notes",
            QuoteArgument(package.Notes ?? string.Empty)
        });

        Process.Start(new ProcessStartInfo
        {
            FileName = stagedUpdaterPath,
            Arguments = arguments,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(stagedUpdaterPath) ?? AppContext.BaseDirectory
        });
    }

    private static string? ResolveUpdaterPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "거래플랜.Updater.exe"),
            Path.Combine(AppContext.BaseDirectory, "Updater", "거래플랜.Updater.exe"),
            Path.Combine(AppContext.BaseDirectory, "거래플랜.Updater", "거래플랜.Updater.exe")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string StageUpdaterForExecution(string updaterPath)
    {
        var updaterDirectory = Path.GetDirectoryName(updaterPath);
        if (string.IsNullOrWhiteSpace(updaterDirectory) || !Directory.Exists(updaterDirectory))
            return updaterPath;

        var stagingRoot = Path.Combine(Path.GetTempPath(), "GeoraePlan", "updater-run", DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
        Directory.CreateDirectory(stagingRoot);

        foreach (var file in Directory.EnumerateFiles(updaterDirectory))
        {
            var destinationPath = Path.Combine(stagingRoot, Path.GetFileName(file));
            File.Copy(file, destinationPath, overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(updaterDirectory))
        {
            CopyDirectory(directory, Path.Combine(stagingRoot, Path.GetFileName(directory)));
        }

        return Path.Combine(stagingRoot, Path.GetFileName(updaterPath));
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var file in Directory.EnumerateFiles(sourceDirectory))
        {
            var destinationPath = Path.Combine(destinationDirectory, Path.GetFileName(file));
            File.Copy(file, destinationPath, overwrite: true);
        }

        foreach (var childDirectory in Directory.EnumerateDirectories(sourceDirectory))
        {
            CopyDirectory(childDirectory, Path.Combine(destinationDirectory, Path.GetFileName(childDirectory)));
        }
    }

    private static Uri ValidatePackageUri(string packageUrl, Uri baseUri)
    {
        if (!Uri.TryCreate(packageUrl, UriKind.Absolute, out var packageUri))
            throw new InvalidOperationException("업데이트 패키지 주소 형식이 올바르지 않습니다.");

        var isLocal = packageUri.IsLoopback || string.Equals(packageUri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
        if (!isLocal && !string.Equals(packageUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("운영 업데이트 패키지는 HTTPS 주소만 허용됩니다.");

        if (!string.Equals(packageUri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("업데이트 패키지 호스트가 현재 서버와 일치하지 않습니다.");

        return packageUri;
    }

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

        return string.IsNullOrWhiteSpace(normalized) ? "1.0.0" : normalized;
    }

    private static string QuoteArgument(string value)
        => "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";

    private static void EnsureSufficientDiskSpace(long packageBytes, string installRoot)
    {
        if (packageBytes <= 0)
            return;

        var tempRoot = Path.GetTempPath();
        var tempDrive = GetDriveInfo(tempRoot);
        var installDrive = GetDriveInfo(installRoot);

        var requiredTempBytes = Math.Max(MinimumUpdaterWorkBytes, checked(packageBytes * 4));
        if (tempDrive.AvailableFreeSpace < requiredTempBytes)
        {
            throw new InvalidOperationException(
                $"{tempDrive.Name} 드라이브 여유 공간이 부족합니다. " +
                $"업데이트 작업용으로 최소 {FormatBytes(requiredTempBytes)} 정도가 필요합니다. " +
                $"현재 여유 공간: {FormatBytes(tempDrive.AvailableFreeSpace)}");
        }

        var requiredInstallBytes = Math.Max(InstallBufferBytes, checked(packageBytes * 2));
        if (!string.Equals(tempDrive.Name, installDrive.Name, StringComparison.OrdinalIgnoreCase) &&
            installDrive.AvailableFreeSpace < requiredInstallBytes)
        {
            throw new InvalidOperationException(
                $"{installDrive.Name} 드라이브 여유 공간이 부족합니다. " +
                $"설치용으로 최소 {FormatBytes(requiredInstallBytes)} 정도가 필요합니다. " +
                $"현재 여유 공간: {FormatBytes(installDrive.AvailableFreeSpace)}");
        }
    }

    private static DriveInfo GetDriveInfo(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException($"드라이브 경로를 확인하지 못했습니다: {path}");

        return new DriveInfo(root);
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

    public static void TryCleanupStaleUpdateArtifacts()
    {
        var georaePlanTempRoot = Path.Combine(Path.GetTempPath(), "GeoraePlan");
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
                // 업데이트 캐시 정리 실패가 실제 업데이트 동작을 막지 않도록 무시
            }
        }
    }
}
