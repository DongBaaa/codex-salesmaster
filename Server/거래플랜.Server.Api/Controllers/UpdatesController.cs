using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using 거래플랜.Server.Api.Middleware;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace 거래플랜.Server.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("updates")]
public sealed class UpdatesController : ControllerBase
{
    private const int MaximumManifestBytes = 4 * 1024 * 1024;
    private static readonly HashSet<string> ManifestPointerPropertyNames =
    [
        "owner",
        "schemaVersion",
        "channel",
        "generationId",
        "manifestRelativePath",
        "manifestSha256",
        "manifestFileSize",
        "deliveryManifestPath",
        "deliveryManifestSha256",
        "deliveryManifestFileSize"
    ];

    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private readonly UpdateOptions _options;

    public UpdatesController(IOptions<UpdateOptions> options)
    {
        _options = options.Value ?? new UpdateOptions();
    }

    [HttpGet("manifest")]
    [AllowDuringDatabaseInitialization]
    public async Task<ActionResult<AppUpdateManifestDto>> GetManifestAsync([FromQuery] string? channel = null, CancellationToken ct = default)
    {
        var normalizedChannel = NormalizeChannel(channel);
        var manifestDirectory = Path.Combine(GetStorageRoot(), "manifest");
        var pointerPath = Path.Combine(manifestDirectory, normalizedChannel + ".current.json");
        var usedLegacyManifest = false;
        byte[]? pointerBytes = null;
        try
        {
            pointerBytes = await ReadSharedFileSnapshotAsync(pointerPath, ct);
        }
        catch (FileNotFoundException)
        {
            // Explicit legacy fallback is allowed only when the pointer is absent.
        }
        catch (DirectoryNotFoundException)
        {
            // Explicit legacy fallback is allowed only when the pointer is absent.
        }
        catch (Exception exception) when (IsManifestSnapshotFailure(exception))
        {
            return ManifestUnavailable(normalizedChannel);
        }

        byte[] manifestBytes;
        if (pointerBytes is not null)
        {
            try
            {
                manifestBytes = await ReadPointerSelectedManifestAsync(
                    manifestDirectory,
                    normalizedChannel,
                    pointerBytes,
                    ct);
            }
            catch (Exception exception) when (IsManifestSnapshotFailure(exception))
            {
                return ManifestUnavailable(normalizedChannel);
            }
        }
        else
        {
            usedLegacyManifest = true;
            var legacyManifestPath = Path.Combine(
                manifestDirectory,
                normalizedChannel + ".json");
            try
            {
                manifestBytes = await ReadSharedFileSnapshotAsync(
                    legacyManifestPath,
                    ct);
            }
            catch (FileNotFoundException)
            {
                return NotFound(new
                {
                    message = $"update manifest not found: {normalizedChannel}"
                });
            }
            catch (DirectoryNotFoundException)
            {
                return NotFound(new
                {
                    message = $"update manifest not found: {normalizedChannel}"
                });
            }
            catch (Exception exception) when (IsManifestSnapshotFailure(exception))
            {
                return ManifestUnavailable(normalizedChannel);
            }
        }

        AppUpdateManifestDto? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<AppUpdateManifestDto>(
                manifestBytes,
                ManifestJsonOptions);
        }
        catch (JsonException)
        {
            return ManifestUnavailable(normalizedChannel);
        }
        if (manifest is null)
            return NotFound(new { message = $"update manifest is empty: {normalizedChannel}" });
        if (usedLegacyManifest &&
            (!string.IsNullOrEmpty(manifest.GenerationId) ||
             (!string.IsNullOrEmpty(manifest.Channel) &&
              !string.Equals(
                  manifest.Channel,
                  normalizedChannel,
                  StringComparison.Ordinal))))
        {
            return ManifestUnavailable(normalizedChannel);
        }

        manifest.Channel = string.IsNullOrWhiteSpace(manifest.Channel) ? normalizedChannel : manifest.Channel.Trim();
        NormalizePackage(manifest.Desktop, "desktop");
        NormalizePackage(manifest.Android, "android");
        return Ok(manifest);
    }

    [HttpGet("download/{platform}/{fileName}")]
    [AllowDuringDatabaseInitialization]
    public IActionResult DownloadPackage(string platform, string fileName)
    {
        if (!TryResolveDownloadPackagePath(platform, fileName, out var fullPath, out var safeFileName))
            return NotFound();

        FileStream stream;
        try
        {
            stream = OpenVerifiedDownloadSnapshotStream(fullPath);
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
        catch (DirectoryNotFoundException)
        {
            return NotFound();
        }
        catch (Exception exception) when (IsDownloadSnapshotFailure(exception))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        ApplyDownloadHeaders(safeFileName);
        return File(
            stream,
            ResolveContentType(safeFileName),
            enableRangeProcessing: true);
    }

    [HttpHead("download/{platform}/{fileName}")]
    [AllowDuringDatabaseInitialization]
    public IActionResult HeadPackage(string platform, string fileName)
    {
        if (!TryResolveDownloadPackagePath(platform, fileName, out var fullPath, out var safeFileName))
            return NotFound();

        try
        {
            using var stream = OpenVerifiedDownloadSnapshotStream(fullPath);
            ApplyDownloadHeaders(safeFileName);
            Response.ContentType = ResolveContentType(safeFileName);
            Response.ContentLength = stream.Length;
            return new EmptyResult();
        }
        catch (FileNotFoundException)
        {
            return NotFound();
        }
        catch (DirectoryNotFoundException)
        {
            return NotFound();
        }
        catch (Exception exception) when (IsDownloadSnapshotFailure(exception))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    private bool TryResolveDownloadPackagePath(
        string platform,
        string fileName,
        out string fullPath,
        out string safeFileName)
    {
        fullPath = string.Empty;
        safeFileName = string.Empty;

        var normalizedPlatform = NormalizePlatform(platform);
        if (normalizedPlatform is null)
            return false;

        safeFileName = Path.GetFileName(fileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(safeFileName) || !string.Equals(safeFileName, fileName, StringComparison.Ordinal))
            return false;

        fullPath = Path.Combine(GetStorageRoot(), "downloads", normalizedPlatform, safeFileName);
        return true;
    }

    private void ApplyDownloadHeaders(string safeFileName)
    {
        Response.Headers.AcceptRanges = "bytes";
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Update-FileName"] = Uri.EscapeDataString(safeFileName);
    }

    private void NormalizePackage(AppUpdatePackageDto? package, string platform)
    {
        if (package is null)
            return;

        package.Platform = string.IsNullOrWhiteSpace(package.Platform) ? platform : package.Platform.Trim();
        if (package.Mandatory && string.IsNullOrWhiteSpace(package.MinimumSupportedVersion))
            package.MinimumSupportedVersion = package.Version;

        foreach (var installer in package.Installers ?? [])
            NormalizeInstaller(installer, platform);

        var packageUrl = package.PackageUrl?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(package.FileName) && !string.IsNullOrWhiteSpace(packageUrl))
            package.FileName = Path.GetFileName(packageUrl);

        if (!string.IsNullOrWhiteSpace(packageUrl) &&
            packageUrl.StartsWith("/", StringComparison.Ordinal) &&
            IsAllowedDownloadPackagePath(packageUrl, platform))
        {
            package.PackageUrl = $"{Request.Scheme}://{Request.Host}{packageUrl}";
            return;
        }

        if (!string.IsNullOrWhiteSpace(packageUrl) &&
            Uri.TryCreate(packageUrl, UriKind.Absolute, out var absolutePackageUri) &&
            IsAllowedAbsolutePackageUri(absolutePackageUri, platform))
        {
            package.PackageUrl = packageUrl;
            return;
        }

        if (string.IsNullOrWhiteSpace(package.FileName))
            return;

        var encodedFileName = Uri.EscapeDataString(package.FileName);
        package.PackageUrl = $"{Request.Scheme}://{Request.Host}/updates/download/{platform}/{encodedFileName}";
    }

    private void NormalizeInstaller(AppUpdateInstallerDto installer, string platform)
    {
        installer.Audience = installer.Audience?.Trim() ?? string.Empty;
        installer.Format = installer.Format?.Trim().ToLowerInvariant() ?? string.Empty;
        installer.Version = installer.Version?.Trim() ?? string.Empty;
        installer.FileName = installer.FileName?.Trim() ?? string.Empty;
        installer.Sha256 = installer.Sha256?.Trim() ?? string.Empty;

        var packageUrl = installer.PackageUrl?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(installer.FileName) && !string.IsNullOrWhiteSpace(packageUrl))
            installer.FileName = Path.GetFileName(packageUrl);

        if (!string.IsNullOrWhiteSpace(packageUrl) &&
            packageUrl.StartsWith("/", StringComparison.Ordinal) &&
            IsAllowedDownloadPackagePath(packageUrl, platform))
        {
            installer.PackageUrl = $"{Request.Scheme}://{Request.Host}{packageUrl}";
            return;
        }

        if (!string.IsNullOrWhiteSpace(packageUrl) &&
            Uri.TryCreate(packageUrl, UriKind.Absolute, out var absolutePackageUri) &&
            IsAllowedAbsolutePackageUri(absolutePackageUri, platform))
        {
            installer.PackageUrl = packageUrl;
            return;
        }

        if (string.IsNullOrWhiteSpace(installer.FileName))
            return;

        var encodedFileName = Uri.EscapeDataString(installer.FileName);
        installer.PackageUrl = $"{Request.Scheme}://{Request.Host}/updates/download/{platform}/{encodedFileName}";
    }

    private bool IsAllowedAbsolutePackageUri(Uri packageUri, string platform)
    {
        if (!string.Equals(packageUri.Scheme, Request.Scheme, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.Equals(packageUri.Authority, Request.Host.Value, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(packageUri.Query) || !string.IsNullOrWhiteSpace(packageUri.Fragment))
            return false;

        return IsAllowedDownloadPackagePath(packageUri.AbsolutePath, platform);
    }

    private static bool IsAllowedDownloadPackagePath(string path, string platform)
    {
        var expectedPathPrefix = $"/updates/download/{platform}/";
        if (!path.StartsWith(expectedPathPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        if (path.Contains("?", StringComparison.Ordinal) || path.Contains("#", StringComparison.Ordinal))
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
               string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal);
    }

    private string GetStorageRoot()
    {
        var configured = string.IsNullOrWhiteSpace(_options.StorageRoot) ? "updates" : _options.StorageRoot.Trim();
        return Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(AppContext.BaseDirectory, configured);
    }

    private static async Task<byte[]> ReadPointerSelectedManifestAsync(
        string manifestDirectory,
        string channel,
        byte[] pointerBytes,
        CancellationToken ct)
    {
        var pointer = ParseManifestPointer(pointerBytes);
        if (!string.Equals(
                pointer["owner"],
                "georaeplan-release-manifest-pointer",
                StringComparison.Ordinal) ||
            !string.Equals(pointer["schemaVersion"], "1", StringComparison.Ordinal) ||
            !string.Equals(pointer["channel"], channel, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Manifest pointer ownership is invalid.");
        }

        var generationId = pointer["generationId"];
        if (generationId.Length != 32 ||
            generationId.Any(character =>
                character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f')))
        {
            throw new InvalidDataException("Manifest pointer generation id is invalid.");
        }

        var expectedRelativePath =
            $"generations/{channel}/{generationId}.json";
        if (!string.Equals(
                pointer["manifestRelativePath"],
                expectedRelativePath,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Manifest pointer path is invalid.");
        }

        if (!TryParseManifestLength(pointer["manifestFileSize"], out var expectedLength) ||
            !TryParseManifestLength(
                pointer["deliveryManifestFileSize"],
                out var expectedDeliveryLength) ||
            !IsSha256(pointer["manifestSha256"]) ||
            !IsSha256(pointer["deliveryManifestSha256"]) ||
            !string.Equals(
                pointer["manifestSha256"],
                pointer["deliveryManifestSha256"],
                StringComparison.OrdinalIgnoreCase) ||
            expectedLength != expectedDeliveryLength)
        {
            throw new InvalidDataException("Manifest pointer evidence is invalid.");
        }

        ValidateDeliveryGenerationPath(
            pointer["deliveryManifestPath"],
            channel,
            generationId);
        var generationDirectory = Path.GetFullPath(
            Path.Combine(manifestDirectory, "generations", channel));
        var generationPath = Path.GetFullPath(
            Path.Combine(generationDirectory, generationId + ".json"));
        if (!string.Equals(
                Path.GetDirectoryName(generationPath),
                generationDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Manifest generation escaped its root.");
        }

        var manifestBytes = await ReadSharedFileSnapshotAsync(generationPath, ct);
        var actualHash = Convert.ToHexString(SHA256.HashData(manifestBytes));
        if (manifestBytes.LongLength != expectedLength ||
            !string.Equals(
                actualHash,
                pointer["manifestSha256"],
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Manifest generation evidence is invalid.");
        }

        var selectedManifest = JsonSerializer.Deserialize<AppUpdateManifestDto>(
            manifestBytes,
            ManifestJsonOptions);
        if (selectedManifest is null ||
            !string.Equals(
                selectedManifest.GenerationId,
                generationId,
                StringComparison.Ordinal) ||
            !string.Equals(
                selectedManifest.Channel,
                channel,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Manifest generation binding is invalid.");
        }

        return manifestBytes;
    }

    private static Dictionary<string, string> ParseManifestPointer(
        byte[] pointerBytes)
    {
        using var document = JsonDocument.Parse(pointerBytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Manifest pointer is not an object.");

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!ManifestPointerPropertyNames.Contains(property.Name) ||
                !values.TryAdd(
                    property.Name,
                    property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString() ?? string.Empty
                        : throw new InvalidDataException(
                            "Manifest pointer values must be strings.")))
            {
                throw new InvalidDataException(
                    "Manifest pointer schema is not exact.");
            }
        }

        if (values.Count != ManifestPointerPropertyNames.Count ||
            ManifestPointerPropertyNames.Any(name => !values.ContainsKey(name)))
        {
            throw new InvalidDataException("Manifest pointer schema is incomplete.");
        }

        return values;
    }

    private static async Task<byte[]> ReadSharedFileSnapshotAsync(
        string path,
        CancellationToken ct)
    {
        await using var stream = OpenSharedReadSnapshotStream(path);
        if (stream.Length > MaximumManifestBytes)
            throw new InvalidDataException("Manifest snapshot is too large.");

        using var buffer = new MemoryStream((int)stream.Length);
        await stream.CopyToAsync(buffer, ct);
        if (buffer.Length > MaximumManifestBytes)
            throw new InvalidDataException("Manifest snapshot is too large.");
        return buffer.ToArray();
    }

    private static FileStream OpenSharedReadSnapshotStream(string path)
    {
        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private FileStream OpenVerifiedDownloadSnapshotStream(string path)
    {
        var storageRoot = Path.GetFullPath(GetStorageRoot());
        var expectedPath = Path.GetFullPath(path);
        AssertDownloadPathHasNoLinks(storageRoot, expectedPath);
        var stream = OpenSharedReadSnapshotStream(expectedPath);
        try
        {
            var openedPath = GetOpenedFilePath(stream.SafeFileHandle);
            if (!PathsEqual(openedPath, expectedPath))
            {
                throw new IOException(
                    "Opened update package escaped its configured path.");
            }
            var openedAttributes = System.IO.File.GetAttributes(openedPath);
            if ((openedAttributes & FileAttributes.Directory) != 0 ||
                (openedAttributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(
                    "Opened update package is not a regular file.");
            }

            // Re-check after the handle is open. The opened-handle path above
            // catches a link inserted before open; this pass catches a link
            // inserted after open. Later path swaps cannot retarget the handle.
            AssertDownloadPathHasNoLinks(storageRoot, expectedPath);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static void AssertDownloadPathHasNoLinks(
        string storageRoot,
        string path)
    {
        var resolvedRoot = Path.GetFullPath(storageRoot);
        var resolvedPath = Path.GetFullPath(path);
        var relativePath = Path.GetRelativePath(resolvedRoot, resolvedPath);
        if (Path.IsPathRooted(relativePath) ||
            relativePath.Equals("..", StringComparison.Ordinal) ||
            relativePath.StartsWith(
                ".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal) ||
            relativePath.StartsWith(
                ".." + Path.AltDirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException(
                "Update package path escaped its configured root.");
        }

        AssertPathEntryHasNoLink(resolvedRoot);
        var currentPath = resolvedRoot;
        foreach (var segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, segment);
            AssertPathEntryHasNoLink(currentPath);
        }
    }

    private static void AssertPathEntryHasNoLink(string path)
    {
        var attributes = System.IO.File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException(
                "Update package path contains a reparse point.");
        }

        FileSystemInfo entry = (attributes & FileAttributes.Directory) != 0
            ? new DirectoryInfo(path)
            : new FileInfo(path);
        entry.Refresh();
        if (entry.LinkTarget is not null)
        {
            throw new IOException(
                "Update package path contains a symbolic link.");
        }
    }

    private static string GetOpenedFilePath(SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows())
            return GetWindowsOpenedFilePath(handle);
        if (OperatingSystem.IsLinux())
        {
            var descriptorPath =
                $"/proc/self/fd/{handle.DangerousGetHandle().ToInt64()}";
            var target = System.IO.File.ResolveLinkTarget(
                descriptorPath,
                returnFinalTarget: true);
            if (target is null)
            {
                throw new IOException(
                    "Could not resolve the opened update package handle.");
            }
            return Path.GetFullPath(target.FullName);
        }

        throw new PlatformNotSupportedException(
            "Opened update package identity is unsupported on this platform.");
    }

    private static string GetWindowsOpenedFilePath(SafeFileHandle handle)
    {
        var capacity = 512;
        while (capacity <= 32768)
        {
            var buffer = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandle(
                handle,
                buffer,
                (uint)buffer.Capacity,
                0);
            if (length == 0)
            {
                throw new IOException(
                    "Could not resolve the opened update package handle.",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }
            if (length < (uint)buffer.Capacity)
                return NormalizeWindowsHandlePath(buffer.ToString());
            capacity = checked((int)length + 1);
        }

        throw new IOException(
            "Opened update package path exceeded the supported length.");
    }

    private static string NormalizeWindowsHandlePath(string path)
    {
        const string uncPrefix = @"\\?\UNC\";
        const string devicePrefix = @"\\?\";
        if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
            path = @"\\" + path[uncPrefix.Length..];
        else if (path.StartsWith(
                     devicePrefix,
                     StringComparison.OrdinalIgnoreCase))
            path = path[devicePrefix.Length..];
        return Path.GetFullPath(path);
    }

    private static bool PathsEqual(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            comparison);
    }

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);

    private static bool IsManifestSnapshotFailure(Exception exception)
    {
        return exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or JsonException
            or ArgumentException
            or NotSupportedException;
    }

    private static bool IsDownloadSnapshotFailure(Exception exception)
    {
        return exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException;
    }

    private ObjectResult ManifestUnavailable(string channel)
    {
        return StatusCode(
            StatusCodes.Status503ServiceUnavailable,
            new { message = $"update manifest unavailable: {channel}" });
    }

    private static bool TryParseManifestLength(string value, out long length)
    {
        return long.TryParse(
                   value,
                   System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out length) &&
               length >= 0 &&
               length <= MaximumManifestBytes;
    }

    private static bool IsSha256(string value)
    {
        return value.Length == 64 &&
               value.All(character =>
                   character is (>= '0' and <= '9') or
                       (>= 'A' and <= 'F') or
                       (>= 'a' and <= 'f'));
    }

    private static void ValidateDeliveryGenerationPath(
        string path,
        string channel,
        string generationId)
    {
        var portablePath = path.Replace('\\', '/');
        var expectedSuffix =
            $"/.georaeplan-release-generations/{channel}/{generationId}.json";
        if (string.IsNullOrWhiteSpace(portablePath) ||
            portablePath.Contains("/../", StringComparison.Ordinal) ||
            portablePath.Contains("/./", StringComparison.Ordinal) ||
            !portablePath.EndsWith(expectedSuffix, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Delivery generation path binding is invalid.");
        }
    }

    private static string NormalizeChannel(string? channel)
    {
        var normalized = (channel ?? "stable").Trim().ToLowerInvariant();
        return normalized switch
        {
            "" or "stable" => "stable",
            "test" => "test",
            "beta" => "beta",
            _ => "stable"
        };
    }

    private static string? NormalizePlatform(string? platform)
    {
        var normalized = (platform ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "desktop" => "desktop",
            "android" => "android",
            _ => null
        };
    }

    private static string ResolveContentType(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return extension.ToLowerInvariant() switch
        {
            ".apk" => "application/vnd.android.package-archive",
            ".json" => "application/json",
            ".zip" => "application/zip",
            ".exe" => "application/vnd.microsoft.portable-executable",
            ".msi" => "application/x-msi",
            ".txt" => "text/plain; charset=utf-8",
            _ => "application/octet-stream"
        };
    }
}
