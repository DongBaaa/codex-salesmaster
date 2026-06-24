using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

public sealed class DesktopAppRuntimeSelfCheckResult
{
    public bool HasBlockingIssue { get; init; }
    public IReadOnlyList<string> BlockingMessages { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> WarningMessages { get; init; } = Array.Empty<string>();
    public int CleanedArtifactCount { get; init; }

    public string BuildUserMessage()
    {
        var lines = new List<string>();
        if (BlockingMessages.Count > 0)
            lines.AddRange(BlockingMessages);

        if (WarningMessages.Count > 0)
            lines.AddRange(WarningMessages);

        if (CleanedArtifactCount > 0)
            lines.Add($"이전 업데이트 잔여 파일 {CleanedArtifactCount:N0}건을 정리했습니다.");

        return string.Join(Environment.NewLine, lines.Where(line => !string.IsNullOrWhiteSpace(line)));
    }

    public string BuildLogMessage()
    {
        var parts = new List<string>();
        if (BlockingMessages.Count > 0)
            parts.Add("차단: " + string.Join(" / ", BlockingMessages));
        if (WarningMessages.Count > 0)
            parts.Add("경고: " + string.Join(" / ", WarningMessages));
        if (CleanedArtifactCount > 0)
            parts.Add($"잔여 파일 정리 {CleanedArtifactCount:N0}건");

        return string.Join(" | ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }
}

public readonly record struct DesktopUpdateDownloadProgress(long DownloadedBytes, long? TotalBytes);

public sealed class DesktopPreparedUpdatePackage
{
    public required string PackagePath { get; init; }
    public long FileSize { get; init; }
}

public sealed class DesktopAppUpdateService
{
    private const long MinimumUpdaterWorkBytes = 512L * 1024 * 1024;
    private const long InstallBufferBytes = 256L * 1024 * 1024;
    private const string CanonicalInstallFolderName = "tradeplan";
    private const string CanonicalExecutableName = "거래플랜.exe";
    private static readonly TimeSpan UpdateArtifactRetention = TimeSpan.FromDays(3);
    private static readonly TimeSpan InstallResidueRetention = TimeSpan.FromDays(14);
    private static readonly byte[] UpdaterRequestMetadataEntropy =
        Encoding.UTF8.GetBytes("GeoraePlan.UpdaterRequestMetadata.v1");
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PreparedPackageLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] StartupRequiredRelativePaths =
    [
        "appsettings.json"
    ];
    private static readonly string[] LiveOnlyRequiredRelativePaths =
    [
        Path.Combine("Updater", "거래플랜.Updater.exe")
    ];
    private static readonly string[] InstallResiduePatterns =
    [
        "*.old",
        "*.bak",
        "*.deleteme",
        "*.rollback"
    ];

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

    public static bool TryRelaunchCanonicalInstallIfNeeded(out string? message)
    {
        message = null;

        if (AppRuntimeInfo.IsTestRuntime)
            return false;

        var currentProcessPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentProcessPath) || !File.Exists(currentProcessPath))
            return false;

        if (IsUpdaterStagingPath(currentProcessPath))
            return false;

        var canonicalExePath = GetCanonicalLaunchExePath();
        if (!File.Exists(canonicalExePath))
            return false;

        if (PathsEqual(currentProcessPath, canonicalExePath))
            return false;

        Process.Start(new ProcessStartInfo
        {
            FileName = canonicalExePath,
            Arguments = BuildForwardedArguments(),
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(canonicalExePath) ?? AppContext.BaseDirectory
        });

        message = $"정식 설치 경로({canonicalExePath})로 다시 실행합니다.";
        return true;
    }

    public static string GetDefaultChannel()
        => AppRuntimeInfo.IsTestRuntime ? "test" : "stable";

    public static DesktopAppRuntimeSelfCheckResult RunStartupSelfCheck()
    {
        var installRoot = AppContext.BaseDirectory;
        var cleanedArtifactCount = TryCleanupStaleInstallArtifacts(installRoot);
        var blockingMessages = new List<string>();
        var warningMessages = new List<string>();

        foreach (var relativePath in StartupRequiredRelativePaths)
        {
            var fullPath = Path.Combine(installRoot, relativePath);
            if (!File.Exists(fullPath))
                blockingMessages.Add($"필수 설치 파일이 누락되었습니다: {relativePath}");
        }

        if (!AppRuntimeInfo.IsTestRuntime)
        {
            foreach (var relativePath in LiveOnlyRequiredRelativePaths)
            {
                var fullPath = Path.Combine(installRoot, relativePath);
                if (!File.Exists(fullPath))
                    blockingMessages.Add($"업데이트 도우미 파일이 누락되었습니다: {relativePath}");
            }
        }

        if (IsUpdaterStagingPath(installRoot))
            warningMessages.Add("업데이트 임시 실행 경로에서 시작되었습니다. 정식 설치 경로로 재실행이 권장됩니다.");

        return new DesktopAppRuntimeSelfCheckResult
        {
            HasBlockingIssue = blockingMessages.Count > 0,
            BlockingMessages = blockingMessages,
            WarningMessages = warningMessages,
            CleanedArtifactCount = cleanedArtifactCount
        };
    }

    public async Task<DesktopAppUpdateCheckResult> CheckForUpdatesAsync(string? channel = null, CancellationToken ct = default)
    {
        var currentVersion = GetCurrentVersion();
        var resolvedChannel = string.IsNullOrWhiteSpace(channel) ? GetDefaultChannel() : channel.Trim();

        try
        {
            var manifest = await _api.GetUpdateManifestAsync(resolvedChannel, ct);
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
            var minimumSupportedVersion = ResolveMinimumSupportedVersion(package, latestVersion);
            var isUpdateAvailable = CompareVersions(latestVersion, currentVersion) > 0;
            var isBelowMinimumSupportedVersion =
                !string.IsNullOrWhiteSpace(minimumSupportedVersion) &&
                CompareVersions(currentVersion, minimumSupportedVersion) < 0;
            var requiresImmediateUpdate = isBelowMinimumSupportedVersion || (isUpdateAvailable && package.Mandatory);

            return new DesktopAppUpdateCheckResult
            {
                CurrentVersion = currentVersion,
                LatestVersion = latestVersion,
                MinimumSupportedVersion = minimumSupportedVersion,
                IsUpdateAvailable = isUpdateAvailable,
                IsBelowMinimumSupportedVersion = isBelowMinimumSupportedVersion,
                Package = package,
                Message = isBelowMinimumSupportedVersion
                    ? $"현재 버전({currentVersion})은 서버 최소 허용 버전({minimumSupportedVersion})보다 낮아 업데이트가 필요합니다."
                    : requiresImmediateUpdate
                        ? $"필수 PC 버전 {latestVersion}이 준비되어 있습니다."
                        : isUpdateAvailable
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

    public async Task<DesktopPreparedUpdatePackage> PrepareUpdatePackageAsync(
        AppUpdatePackageDto package,
        IProgress<DesktopUpdateDownloadProgress>? progress = null,
        CancellationToken ct = default)
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
        EnsureSufficientDiskSpace(package.FileSize, GetCanonicalInstallRoot());
        TryCleanupStaleUpdateArtifacts();

        var packageDirectory = GetPreparedPackageDirectory(package);
        Directory.CreateDirectory(packageDirectory);

        var safePackageFileName = ResolvePackageFileName(package);
        var targetPath = Path.GetFullPath(Path.Combine(packageDirectory, safePackageFileName));
        var safeDirectory = Path.GetFullPath(packageDirectory);
        if (!safeDirectory.EndsWith(Path.DirectorySeparatorChar))
            safeDirectory += Path.DirectorySeparatorChar;
        if (!targetPath.StartsWith(safeDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("업데이트 패키지 저장 경로가 안전하지 않습니다.");

        var packageLock = GetPreparedPackageLock(targetPath);
        await packageLock.WaitAsync(ct);
        try
        {
            if (File.Exists(targetPath) && await TryVerifySha256Async(targetPath, package.Sha256, ct))
            {
                VerifyExpectedPackageFileSize(targetPath, package.FileSize);
                var existingInfo = new FileInfo(targetPath);
                TryCleanupPackageTemporaryFiles(packageDirectory, safePackageFileName);
                progress?.Report(new DesktopUpdateDownloadProgress(existingInfo.Length, existingInfo.Length));
                return new DesktopPreparedUpdatePackage
                {
                    PackagePath = targetPath,
                    FileSize = existingInfo.Length
                };
            }

            var temporaryPath = CreateUniquePackageDownloadPath(targetPath);
            try
            {
                using var http = new HttpClient
                {
                    Timeout = TimeSpan.FromMinutes(10)
                };
                using var request = new HttpRequestMessage(HttpMethod.Get, packageUri);
                foreach (var header in _api.GetUpdateDownloadHeaders(packageUri))
                {
                    if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
                        request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength;
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var destination = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 81920,
                    useAsync: true);

                var buffer = new byte[81920];
                long downloadedBytes = 0;
                var lastReportUtc = DateTime.UtcNow;

                while (true)
                {
                    var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                    if (read <= 0)
                        break;

                    await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                    downloadedBytes += read;

                    var nowUtc = DateTime.UtcNow;
                    if ((nowUtc - lastReportUtc).TotalMilliseconds >= 250)
                    {
                        progress?.Report(new DesktopUpdateDownloadProgress(downloadedBytes, totalBytes));
                        lastReportUtc = nowUtc;
                    }
                }

                await destination.FlushAsync(ct);
                progress?.Report(new DesktopUpdateDownloadProgress(downloadedBytes, totalBytes));
                await VerifySha256Async(temporaryPath, package.Sha256, ct);
                VerifyExpectedPackageFileSize(temporaryPath, package.FileSize);

                File.Move(temporaryPath, targetPath, overwrite: true);
                TryCleanupPackageTemporaryFiles(packageDirectory, safePackageFileName);
                return new DesktopPreparedUpdatePackage
                {
                    PackagePath = targetPath,
                    FileSize = downloadedBytes
                };
            }
            finally
            {
                TryDeleteFile(temporaryPath);
            }
        }
        finally
        {
            packageLock.Release();
        }
    }

    public void StartUpdate(AppUpdatePackageDto package, string? preparedPackagePath = null)
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
        var preparedPackageFullPath = ValidatePreparedPackagePath(preparedPackagePath, package);

        var updaterPath = ResolveUpdaterPath();
        if (string.IsNullOrWhiteSpace(updaterPath))
            throw new FileNotFoundException("거래플랜.Updater.exe를 찾지 못했습니다. 현재 설치본에는 내부 업데이트 도우미가 없어 1회 수동 재설치가 필요할 수 있습니다.");

        var stagedUpdaterPath = StageUpdaterForExecution(updaterPath);

        var currentProcess = Process.GetCurrentProcess();
        if (string.IsNullOrWhiteSpace(Environment.ProcessPath ?? currentProcess.MainModule?.FileName))
            throw new InvalidOperationException("현재 실행 파일 경로를 확인하지 못했습니다.");

        var installRoot = GetCanonicalInstallRoot();
        var launchExePath = GetCanonicalLaunchExePath();
        EnsureSufficientDiskSpace(package.FileSize, installRoot);
        TryCleanupStaleUpdateArtifacts();
        var requestMetadataPath = string.IsNullOrWhiteSpace(preparedPackageFullPath)
            ? CreateUpdaterRequestMetadataFile(stagedUpdaterPath, packageUri)
            : null;

        var argumentParts = new List<string>
        {
            "--package-url",
            QuoteArgument(packageUri.ToString()),
            "--sha256",
            QuoteArgument(package.Sha256),
            "--install-root",
            QuoteArgument(installRoot),
            "--launch-exe",
            QuoteArgument(launchExePath),
            "--process-id",
            currentProcess.Id.ToString(),
            "--version",
            QuoteArgument(package.Version),
            "--file-size",
            package.FileSize.ToString(),
            "--file-name",
            QuoteArgument(ResolvePackageFileName(package)),
            "--notes",
            QuoteArgument(package.Notes ?? string.Empty)
        };

        if (!string.IsNullOrWhiteSpace(preparedPackageFullPath))
        {
            argumentParts.Add("--package-path");
            argumentParts.Add(QuoteArgument(preparedPackageFullPath));
        }

        if (!string.IsNullOrWhiteSpace(requestMetadataPath))
        {
            argumentParts.Add("--request-metadata-path");
            argumentParts.Add(QuoteArgument(requestMetadataPath));
        }

        var arguments = string.Join(" ", argumentParts);

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = stagedUpdaterPath,
                Arguments = arguments,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(stagedUpdaterPath) ?? AppContext.BaseDirectory
            });
        }
        catch
        {
            TryDeleteSensitiveFile(requestMetadataPath);
            throw;
        }
    }

    private static string? ValidatePreparedPackagePath(string? preparedPackagePath, AppUpdatePackageDto package)
    {
        if (string.IsNullOrWhiteSpace(preparedPackagePath))
            return null;

        var fullPath = Path.GetFullPath(preparedPackagePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("미리 다운로드한 업데이트 패키지를 찾지 못했습니다.", fullPath);

        if (!string.Equals(Path.GetExtension(fullPath), ".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("미리 다운로드한 업데이트 패키지 형식이 올바르지 않습니다.");

        VerifySha256Async(fullPath, package.Sha256, CancellationToken.None).GetAwaiter().GetResult();
        return fullPath;
    }

    private static string ResolvePackageFileName(AppUpdatePackageDto package)
    {
        var fileName = Path.GetFileName(package.FileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = $"desktop-{NormalizeVersionText(package.Version)}.zip";

        return fileName;
    }

    private static SemaphoreSlim GetPreparedPackageLock(string targetPath)
        => PreparedPackageLocks.GetOrAdd(
            Path.GetFullPath(targetPath),
            static _ => new SemaphoreSlim(1, 1));

    private static string CreateUniquePackageDownloadPath(string targetPath)
    {
        var directoryPath = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrWhiteSpace(directoryPath))
            directoryPath = AppPaths.TempDir;

        return Path.Combine(
            directoryPath,
            $"{Path.GetFileName(targetPath)}.{Environment.ProcessId}.{Guid.NewGuid():N}.download");
    }

    private static void TryCleanupPackageTemporaryFiles(string packageDirectory, string packageFileName)
    {
        if (!Directory.Exists(packageDirectory))
            return;

        foreach (var filePath in Directory.EnumerateFiles(packageDirectory, $"{packageFileName}*.download", SearchOption.TopDirectoryOnly))
            TryDeleteFile(filePath);
    }

    private static void TryDeleteFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        try
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
        catch
        {
            // 다른 업데이트 준비 작업이 파일을 잡고 있으면 다음 정리 단계에서 다시 삭제
        }
    }

    private static string GetPreparedPackageDirectory(AppUpdatePackageDto package)
    {
        var version = SanitizePathSegment(NormalizeVersionText(package.Version));
        var sha = (package.Sha256 ?? string.Empty).Trim();
        var shaPrefix = sha.Length > 12 ? sha[..12] : sha;
        if (string.IsNullOrWhiteSpace(shaPrefix))
            shaPrefix = "unknown";

        return Path.Combine(
            GetUpdateArtifactRoot(),
            "prepared-updates",
            $"{version}-{SanitizePathSegment(shaPrefix)}");
    }

    private static string SanitizePathSegment(string value)
    {
        var sanitized = new string((value ?? string.Empty)
            .Select(static ch => char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_')
            .ToArray());

        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
    }

    private static async Task<bool> TryVerifySha256Async(string filePath, string sha256, CancellationToken ct)
    {
        try
        {
            await VerifySha256Async(filePath, sha256, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static async Task VerifySha256Async(string filePath, string sha256, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sha256))
            return;

        await using var stream = File.OpenRead(filePath);
        using var algorithm = SHA256.Create();
        var hash = await algorithm.ComputeHashAsync(stream, ct);
        var actual = Convert.ToHexString(hash);
        if (!string.Equals(actual, sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("업데이트 패키지 SHA256 검증에 실패했습니다.");
    }

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

    public static string GetCanonicalInstallRoot()
    {
        var programFilesRoot = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (string.IsNullOrWhiteSpace(programFilesRoot))
            programFilesRoot = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        if (string.IsNullOrWhiteSpace(programFilesRoot))
            throw new InvalidOperationException("Program Files 경로를 확인하지 못했습니다.");

        return Path.Combine(programFilesRoot, CanonicalInstallFolderName);
    }

    public static string GetCanonicalLaunchExePath()
        => Path.Combine(GetCanonicalInstallRoot(), CanonicalExecutableName);

    private static string StageUpdaterForExecution(string updaterPath)
    {
        var updaterDirectory = Path.GetDirectoryName(updaterPath);
        if (string.IsNullOrWhiteSpace(updaterDirectory) || !Directory.Exists(updaterDirectory))
            return updaterPath;

        var stagingRoot = Path.Combine(GetUpdateArtifactRoot(), "updater-run", DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
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

    private string? CreateUpdaterRequestMetadataFile(string stagedUpdaterPath, Uri packageUri)
    {
        var headers = _api.GetUpdateDownloadHeaders(packageUri);
        if (headers.Count == 0)
            return null;

        var updaterDirectory = Path.GetDirectoryName(stagedUpdaterPath);
        if (string.IsNullOrWhiteSpace(updaterDirectory) || !Directory.Exists(updaterDirectory))
            return null;

        var metadataPath = Path.Combine(updaterDirectory, "request-metadata.json");
        var payload = JsonSerializer.Serialize(new UpdaterRequestMetadata
        {
            ProtectedHeaders = headers.ToDictionary(
                pair => pair.Key,
                pair => ProtectUpdaterMetadataValue(pair.Value),
                StringComparer.OrdinalIgnoreCase)
        });

        WriteSensitiveUpdaterMetadataFile(metadataPath, payload);

        try
        {
            File.SetAttributes(metadataPath, FileAttributes.Hidden | FileAttributes.Temporary);
        }
        catch
        {
            // 속성 지정 실패가 업데이트 자체를 막지 않도록 무시
        }

        return metadataPath;
    }

    private static void WriteSensitiveUpdaterMetadataFile(string metadataPath, string payload)
    {
        try
        {
            var metadataDirectory = Path.GetDirectoryName(metadataPath);
            if (!string.IsNullOrWhiteSpace(metadataDirectory))
                Directory.CreateDirectory(metadataDirectory);

            var bytes = Encoding.UTF8.GetBytes(payload);
            using var stream = new FileStream(
                metadataPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough);
            stream.Write(bytes, 0, bytes.Length);
        }
        catch
        {
            TryDeleteSensitiveFile(metadataPath);
            throw;
        }
    }

    private static string ProtectUpdaterMetadataValue(string value)
    {
        var plainBytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        try
        {
            var protectedBytes = ProtectedData.Protect(
                plainBytes,
                UpdaterRequestMetadataEntropy,
                DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    private static void TryDeleteSensitiveFile(string? metadataPath)
    {
        if (string.IsNullOrWhiteSpace(metadataPath))
            return;

        try
        {
            if (File.Exists(metadataPath))
                File.Delete(metadataPath);
        }
        catch
        {
            // stale updater-run cleanup에서 다시 삭제를 시도한다.
        }
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

        if (!string.Equals(packageUri.Authority, baseUri.Authority, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("업데이트 패키지 호스트가 현재 서버와 일치하지 않습니다.");

        if (!IsExpectedDesktopPackageUri(packageUri))
            throw new InvalidOperationException("업데이트 패키지 경로가 PC 업데이트 다운로드 경로와 일치하지 않습니다.");

        return packageUri;
    }

    private static bool IsExpectedDesktopPackageUri(Uri packageUri)
    {
        if (!string.IsNullOrWhiteSpace(packageUri.Query) || !string.IsNullOrWhiteSpace(packageUri.Fragment))
            return false;

        const string expectedPathPrefix = "/updates/download/desktop/";
        var path = packageUri.AbsolutePath;
        if (!path.StartsWith(expectedPathPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var encodedFileName = path[expectedPathPrefix.Length..];
        if (string.IsNullOrWhiteSpace(encodedFileName) ||
            encodedFileName.Contains("/", StringComparison.Ordinal) ||
            encodedFileName.Contains("\\", StringComparison.Ordinal))
        {
            return false;
        }

        var fileName = Uri.UnescapeDataString(encodedFileName);
        return !string.IsNullOrWhiteSpace(fileName) &&
               !fileName.Contains("/", StringComparison.Ordinal) &&
               !fileName.Contains("\\", StringComparison.Ordinal) &&
               string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal) &&
               string.Equals(Path.GetExtension(fileName), ".zip", StringComparison.OrdinalIgnoreCase);
    }

    private static int CompareVersions(string left, string right)
    {
        if (!Version.TryParse(NormalizeVersionText(left), out var leftVersion))
            leftVersion = new Version(0, 0, 0);
        if (!Version.TryParse(NormalizeVersionText(right), out var rightVersion))
            rightVersion = new Version(0, 0, 0);
        return leftVersion.CompareTo(rightVersion);
    }

    private static string ResolveMinimumSupportedVersion(AppUpdatePackageDto package, string latestVersion)
    {
        if (!string.IsNullOrWhiteSpace(package.MinimumSupportedVersion))
            return NormalizeVersionText(package.MinimumSupportedVersion);

        return package.Mandatory ? latestVersion : string.Empty;
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

    private static string BuildForwardedArguments()
    {
        var args = Environment.GetCommandLineArgs().Skip(1);
        return string.Join(" ", args.Select(QuoteArgument));
    }

    private static bool IsUpdaterStagingPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        return path.IndexOf($"{Path.DirectorySeparatorChar}GeoraePlan{Path.DirectorySeparatorChar}updater-run{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) >= 0
               || path.IndexOf($"{Path.AltDirectorySeparatorChar}GeoraePlan{Path.AltDirectorySeparatorChar}updater-run{Path.AltDirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            var leftFull = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var rightFull = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(leftFull, rightFull, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string QuoteArgument(string value)
        => "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";

    private static void EnsureSufficientDiskSpace(long packageBytes, string installRoot)
    {
        if (packageBytes <= 0)
            return;

        var tempRoot = AppPaths.TempDir;
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
        var georaePlanTempRoot = GetUpdateArtifactRoot();
        TryCleanupChildDirectories(Path.Combine(georaePlanTempRoot, "prepared-updates"));
        TryCleanupChildDirectories(Path.Combine(georaePlanTempRoot, "updates"));
        TryCleanupChildDirectories(Path.Combine(georaePlanTempRoot, "updater-run"));
    }

    private static string GetUpdateArtifactRoot()
    {
        var root = Path.Combine(AppPaths.TempDir, "GeoraePlan");
        Directory.CreateDirectory(root);
        return root;
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

    private sealed class UpdaterRequestMetadata
    {
        public Dictionary<string, string> ProtectedHeaders { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static int TryCleanupStaleInstallArtifacts(string installRoot)
    {
        if (string.IsNullOrWhiteSpace(installRoot) || !Directory.Exists(installRoot))
            return 0;

        var cleanedCount = 0;
        foreach (var rootPath in new[]
                 {
                     installRoot,
                     Path.Combine(installRoot, "Updater")
                 }.Where(Directory.Exists))
        {
            cleanedCount += TryCleanupInstallFiles(rootPath);
        }

        return cleanedCount;
    }

    private static int TryCleanupInstallFiles(string rootPath)
    {
        var cutoffUtc = DateTime.UtcNow - InstallResidueRetention;
        var deletedCount = 0;

        foreach (var pattern in InstallResiduePatterns)
        {
            foreach (var file in Directory.EnumerateFiles(rootPath, pattern, SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.LastWriteTimeUtc > cutoffUtc)
                        continue;

                    info.Delete();
                    deletedCount++;
                }
                catch
                {
                    // 설치 잔여 파일 정리 실패는 앱 기동을 막지 않음
                }
            }
        }

        return deletedCount;
    }
}
