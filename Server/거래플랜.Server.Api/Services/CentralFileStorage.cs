using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace 거래플랜.Server.Api.Services;

public sealed class CentralFileStorage : ICentralFileStorage
{
    public string RootPath { get; }

    public CentralFileStorage(IOptions<CentralFileStorageOptions> options, IHostEnvironment hostEnvironment)
    {
        var configuredPath = options.Value.RootPath?.Trim();
        RootPath = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(hostEnvironment.ContentRootPath, "App_Data", "FileStore")
            : configuredPath;

        Directory.CreateDirectory(RootPath);
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
        Directory.CreateDirectory(directory);

        var targetPath = Path.Combine(directory, $"{fileId:N}__{safeFileName}");
        await File.WriteAllBytesAsync(targetPath, content ?? [], cancellationToken);
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

        foreach (var invalid in Path.GetInvalidFileNameChars())
            segment = segment.Replace(invalid, '_');

        segment = segment.Replace(Path.DirectorySeparatorChar, '_').Replace(Path.AltDirectorySeparatorChar, '_');
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
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return false;

            safePath = fullPath;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
