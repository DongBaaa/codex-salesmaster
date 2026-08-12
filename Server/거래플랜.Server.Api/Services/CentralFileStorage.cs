using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;

namespace 거래플랜.Server.Api.Services;

public sealed class CentralFileStorage : ICentralFileStorage
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    public string RootPath { get; }

    public CentralFileStorage(IOptions<CentralFileStorageOptions> options, IHostEnvironment hostEnvironment)
    {
        var configuredPath = options.Value.RootPath?.Trim();
        RootPath = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(hostEnvironment.ContentRootPath, "App_Data", "FileStore")
            : configuredPath;

        Directory.CreateDirectory(RootPath);
        EnsureExistingPathChainHasNoReparsePoint(
            Path.GetFullPath(RootPath),
            Path.GetFullPath(RootPath));
    }

    public async Task<string> SaveBytesAsync(
        string area,
        string ownerId,
        Guid fileId,
        string fileName,
        byte[] content,
        CancellationToken cancellationToken = default)
    {
        var safeArea = SanitizeSegment(area, "misc");
        var safeOwnerId = SanitizeSegment(ownerId, "unassigned");
        var safeFileName = SanitizeFileName(fileName, fileId);
        var directory = Path.Combine(RootPath, safeArea, safeOwnerId);
        EnsureExistingPathChainHasNoReparsePoint(RootPath, directory);
        Directory.CreateDirectory(directory);
        EnsureExistingPathChainHasNoReparsePoint(RootPath, directory);

        var targetPath = Path.Combine(directory, $"{fileId:N}__{safeFileName}");
        var resolvedContent = content ?? [];
        if (TryUseExistingIdenticalFile(targetPath, resolvedContent))
            return targetPath;

        var stagingPath = Path.Combine(directory, $".{fileId:N}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             stagingPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(resolvedContent, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            EnsureExistingPathChainHasNoReparsePoint(RootPath, directory);
        }
        catch
        {
            TryDeleteStagingFile(stagingPath);
            throw;
        }

        try
        {
            File.Move(stagingPath, targetPath, overwrite: false);
        }
        catch (IOException)
        {
            TryDeleteStagingFile(stagingPath);
            if (TryUseExistingIdenticalFile(targetPath, resolvedContent))
                return targetPath;
            throw;
        }

        EnsureExistingPathChainHasNoReparsePoint(RootPath, targetPath);
        return targetPath;
    }

    public byte[] ReadBytes(string? storedPath, byte[]? fallback = null)
    {
        if (TryResolveSafeStoredPath(storedPath, out var safePath) && File.Exists(safePath))
        {
            try
            {
                return File.ReadAllBytes(safePath);
            }
            catch
            {
                // fallback below
            }
        }

        return fallback ?? [];
    }

    public FileStorageInspectionResult Inspect(string? storedPath, bool computeHash = false)
    {
        if (string.IsNullOrWhiteSpace(storedPath))
        {
            return new FileStorageInspectionResult(
                HasStoredPath: false,
                IsSafePath: false,
                Exists: false,
                Length: null,
                Hash: string.Empty,
                Error: string.Empty);
        }

        if (!TryResolveSafeStoredPath(storedPath, out var safePath))
        {
            return new FileStorageInspectionResult(
                HasStoredPath: true,
                IsSafePath: false,
                Exists: false,
                Length: null,
                Hash: string.Empty,
                Error: "unsafe_storage_path");
        }

        if (!File.Exists(safePath))
        {
            return new FileStorageInspectionResult(
                HasStoredPath: true,
                IsSafePath: true,
                Exists: false,
                Length: null,
                Hash: string.Empty,
                Error: "stored_file_not_found");
        }

        try
        {
            var fileInfo = new FileInfo(safePath);
            var hash = string.Empty;
            if (computeHash)
            {
                using var stream = File.OpenRead(safePath);
                hash = Convert.ToHexString(SHA256.HashData(stream));
            }

            return new FileStorageInspectionResult(
                HasStoredPath: true,
                IsSafePath: true,
                Exists: true,
                Length: fileInfo.Length,
                Hash: hash,
                Error: string.Empty);
        }
        catch (Exception ex)
        {
            return new FileStorageInspectionResult(
                HasStoredPath: true,
                IsSafePath: true,
                Exists: false,
                Length: null,
                Hash: string.Empty,
                Error: ex.GetType().Name);
        }
    }

    public void DeleteIfExists(string? storedPath)
    {
        if (!TryResolveSafeStoredPath(storedPath, out var safePath) || !File.Exists(safePath))
            return;

        try
        {
            File.Delete(safePath);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private static string SanitizeSegment(string? value, string fallback)
    {
        var segment = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(segment))
            return fallback;
        if (segment is "." or "..")
            throw new ArgumentException("Storage path segments cannot be '.' or '..'.", nameof(value));

        foreach (var invalid in Path.GetInvalidFileNameChars())
            segment = segment.Replace(invalid, '_');

        segment = segment.Replace(Path.DirectorySeparatorChar, '_').Replace(Path.AltDirectorySeparatorChar, '_');
        if (segment is "." or "..")
            throw new ArgumentException("Storage path segments cannot be '.' or '..'.", nameof(value));
        return string.IsNullOrWhiteSpace(segment) ? fallback : segment;
    }

    private static string SanitizeFileName(string? fileName, Guid fileId)
    {
        var safeName = Path.GetFileName(fileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(safeName))
            safeName = $"{fileId:N}.bin";

        foreach (var invalid in Path.GetInvalidFileNameChars())
            safeName = safeName.Replace(invalid, '_');

        return safeName;
    }

    private bool TryResolveSafeStoredPath(string? storedPath, out string safePath)
    {
        safePath = string.Empty;
        if (string.IsNullOrWhiteSpace(storedPath))
            return false;

        try
        {
            var root = Path.GetFullPath(RootPath);
            if (!root.EndsWith(Path.DirectorySeparatorChar))
                root += Path.DirectorySeparatorChar;

            var fullPath = Path.GetFullPath(storedPath);
            if (!fullPath.StartsWith(root, PathComparison))
                return false;

            EnsureExistingPathChainHasNoReparsePoint(
                Path.TrimEndingDirectorySeparator(root),
                fullPath);
            safePath = fullPath;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureExistingPathChainHasNoReparsePoint(
        string rootPath,
        string targetPath)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        var normalizedTarget = Path.GetFullPath(targetPath);
        var rootPrefix = normalizedRoot + Path.DirectorySeparatorChar;
        if (!string.Equals(normalizedTarget, normalizedRoot, PathComparison) &&
            !normalizedTarget.StartsWith(rootPrefix, PathComparison))
        {
            throw new InvalidDataException("Storage path is outside the configured root.");
        }

        ThrowIfReparsePoint(normalizedRoot);
        if (string.Equals(normalizedTarget, normalizedRoot, PathComparison))
            return;

        var relativePath = Path.GetRelativePath(normalizedRoot, normalizedTarget);
        var currentPath = normalizedRoot;
        foreach (var segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, segment);
            if (!File.Exists(currentPath) && !Directory.Exists(currentPath))
                break;
            ThrowIfReparsePoint(currentPath);
        }
    }

    private static void ThrowIfReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Storage paths cannot contain symbolic links or reparse points.");
    }

    private static void TryDeleteStagingFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // The original write failure remains authoritative.
        }
    }

    private bool TryUseExistingIdenticalFile(string targetPath, byte[] content)
    {
        if (!File.Exists(targetPath))
            return false;

        EnsureExistingPathChainHasNoReparsePoint(RootPath, targetPath);
        var fileInfo = new FileInfo(targetPath);
        if (fileInfo.Length != content.LongLength)
            throw new IOException("A different file already exists at the requested storage path.");

        using var stream = new FileStream(
            targetPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.SequentialScan);
        var existingHash = SHA256.HashData(stream);
        var requestedHash = SHA256.HashData(content);
        if (!CryptographicOperations.FixedTimeEquals(existingHash, requestedHash))
            throw new IOException("A different file already exists at the requested storage path.");

        return true;
    }
}
