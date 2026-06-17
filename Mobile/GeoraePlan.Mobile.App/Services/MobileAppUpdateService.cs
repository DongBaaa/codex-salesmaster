using System.Security.Cryptography;
using 거래플랜.Shared.Contracts;
#if ANDROID
using Android.App;
using Android.Content;
using Android.OS;
using Android.Provider;
#endif

namespace GeoraePlan.Mobile.App.Services;

public sealed class MobileAppUpdateService
{
    private readonly GeoraePlanApiClient _api;
    private readonly HttpClient _http = new();

    public MobileAppUpdateService(GeoraePlanApiClient api)
    {
        _api = api;
    }

    public string GetCurrentVersion()
        => NormalizeVersionText(AppInfo.Current.VersionString);

    public async Task<MobileAppUpdateCheckResult> CheckForUpdatesAsync(string channel = "stable", CancellationToken ct = default)
    {
        var currentVersion = GetCurrentVersion();

        try
        {
            var manifest = await _api.GetUpdateManifestAsync(channel, ct);
            var package = manifest?.Android;
            if (package is null || string.IsNullOrWhiteSpace(package.Version))
            {
                return new MobileAppUpdateCheckResult
                {
                    CurrentVersion = currentVersion,
                    LatestVersion = currentVersion,
                    Message = "배포된 안드로이드 업데이트 정보를 찾지 못했습니다."
                };
            }

            var latestVersion = NormalizeVersionText(package.Version);
            var isUpdateAvailable = CompareVersions(latestVersion, currentVersion) > 0;

            return new MobileAppUpdateCheckResult
            {
                CurrentVersion = currentVersion,
                LatestVersion = latestVersion,
                IsUpdateAvailable = isUpdateAvailable,
                Package = package,
                Message = isUpdateAvailable
                    ? $"새 안드로이드 버전 {latestVersion}이 준비되어 있습니다."
                    : $"현재 버전({currentVersion})이 최신입니다."
            };
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return new MobileAppUpdateCheckResult
            {
                CurrentVersion = currentVersion,
                LatestVersion = currentVersion,
                Message = "업데이트 매니페스트가 아직 배포되지 않았습니다."
            };
        }
    }

    public async Task<string> DownloadAndLaunchInstallerAsync(AppUpdatePackageDto package, CancellationToken ct = default)
    {
        if (DeviceInfo.Platform != DevicePlatform.Android)
            throw new InvalidOperationException("안드로이드 기기에서만 APK 설치를 진행할 수 있습니다.");
        if (string.IsNullOrWhiteSpace(package.PackageUrl))
            throw new InvalidOperationException("APK 다운로드 주소가 비어 있습니다.");

        var packageUrl = _api.ResolveAbsoluteUrl(package.PackageUrl);
        if (string.IsNullOrWhiteSpace(packageUrl))
            throw new InvalidOperationException("APK 다운로드 주소를 절대 경로로 해석하지 못했습니다.");
        if (string.IsNullOrWhiteSpace(package.Sha256))
            throw new InvalidOperationException("APK SHA256 정보가 비어 있습니다.");

        var packageUri = ValidatePackageUri(packageUrl, _api.GetBaseUri());

        var downloadRoot = Path.Combine(FileSystem.CacheDirectory, "updates");
        Directory.CreateDirectory(downloadRoot);

        var fileName = string.IsNullOrWhiteSpace(package.FileName)
            ? $"georaeplan-{NormalizeVersionText(package.Version)}.apk"
            : Path.GetFileName(package.FileName) ?? string.Empty;
        var targetPath = await DownloadPackageAsync(packageUri.ToString(), downloadRoot, fileName, package.Sha256, ct);

        if (!await HasMatchingFileAsync(targetPath, package.Sha256, ct))
            throw new InvalidOperationException("다운로드한 APK의 무결성 검증에 실패했습니다.");

        var opened = await OpenInstallerAsync(targetPath);
        if (!opened)
            throw new InvalidOperationException("안드로이드 설치 화면을 열지 못했습니다. 알 수 없는 앱 설치 권한을 확인하세요.");

        return targetPath;
    }

    private async Task<string> DownloadPackageAsync(string packageUrl, string downloadRoot, string fileName, string expectedSha256, CancellationToken ct)
    {
        var safeFileName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeFileName))
            safeFileName = "georaeplan-update.apk";

        var targetPath = Path.GetFullPath(Path.Combine(downloadRoot, safeFileName));
        var safeRoot = Path.GetFullPath(downloadRoot);
        if (!safeRoot.EndsWith(Path.DirectorySeparatorChar))
            safeRoot += Path.DirectorySeparatorChar;
        if (!targetPath.StartsWith(safeRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("업데이트 파일 저장 경로가 안전하지 않습니다.");

        if (await HasMatchingFileAsync(targetPath, expectedSha256, ct))
            return targetPath;

        var temporaryPath = Path.Combine(
            safeRoot,
            $"{safeFileName}.{System.Environment.ProcessId}.{Guid.NewGuid():N}.download");

        try
        {
            using var response = await _http.GetAsync(packageUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using (var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true))
            {
                await source.CopyToAsync(destination, ct);
                await destination.FlushAsync(ct);
            }

            if (!await HasMatchingFileAsync(temporaryPath, expectedSha256, ct))
                throw new InvalidOperationException("다운로드한 APK의 무결성 검증에 실패했습니다.");

            System.IO.File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }

        return targetPath;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (System.IO.File.Exists(path))
                System.IO.File.Delete(path);
        }
        catch
        {
            // 다음 업데이트 시 다시 정리합니다.
        }
    }

    private static async Task<bool> HasMatchingFileAsync(string path, string expectedSha256, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
            return false;
        if (string.IsNullOrWhiteSpace(expectedSha256))
            return new FileInfo(path).Length > 0;

        var actual = await ComputeSha256Async(path, ct);
        return string.Equals(actual, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static Task<bool> OpenInstallerAsync(string targetPath)
    {
#if ANDROID
        var context = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity ?? Android.App.Application.Context;
        if (OperatingSystem.IsAndroidVersionAtLeast(26) &&
            !context.PackageManager!.CanRequestPackageInstalls())
        {
            var settingsIntent = new Intent(
                Settings.ActionManageUnknownAppSources,
                Android.Net.Uri.Parse($"package:{AppInfo.PackageName}"));
            settingsIntent.AddFlags(ActivityFlags.NewTask);
            context.StartActivity(settingsIntent);
            return Task.FromResult(false);
        }

        var apkFile = new Java.IO.File(targetPath);
        var authority = $"{AppInfo.PackageName}.fileprovider";
        var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(context, authority, apkFile);

        var intent = new Intent(Intent.ActionView);
        intent.SetDataAndType(uri, "application/vnd.android.package-archive");
        intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.GrantReadUriPermission);

        context.StartActivity(intent);
        return Task.FromResult(true);
#else
        return Launcher.Default.OpenAsync(new OpenFileRequest(
            "거래플랜 업데이트",
            new ReadOnlyFile(targetPath, "application/vnd.android.package-archive")));
#endif
    }


    private static Uri ValidatePackageUri(string packageUrl, Uri baseUri)
    {
        if (!Uri.TryCreate(packageUrl, UriKind.Absolute, out var packageUri))
            throw new InvalidOperationException("APK 다운로드 주소 형식이 올바르지 않습니다.");

        var isLocal = packageUri.IsLoopback ||
                      string.Equals(packageUri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(packageUri.Host, "10.0.2.2", StringComparison.OrdinalIgnoreCase);

        if (!isLocal && !string.Equals(packageUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("운영 APK 다운로드 주소는 HTTPS만 허용됩니다.");

        if (!string.Equals(packageUri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("APK 다운로드 호스트가 현재 서버와 일치하지 않습니다.");

        return packageUri;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = System.IO.File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash);
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

        return string.IsNullOrWhiteSpace(normalized) ? "0.0.0" : normalized;
    }
}

public sealed class MobileAppUpdateCheckResult
{
    public string CurrentVersion { get; set; } = string.Empty;
    public string LatestVersion { get; set; } = string.Empty;
    public bool IsUpdateAvailable { get; set; }
    public string Message { get; set; } = string.Empty;
    public AppUpdatePackageDto? Package { get; set; }
}
