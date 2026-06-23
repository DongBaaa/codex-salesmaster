using System.Security.Cryptography;
using GeoraePlan.Mobile.App.Models;

namespace GeoraePlan.Mobile.App.Services;

public sealed class PaymentAttachmentDraftStore
{
    private string DraftDirectory => Path.Combine(FileSystem.AppDataDirectory, "payment-attachment-drafts");

    public async Task<PendingPaymentAttachmentRecord> ImportAsync(
        FileResult fileResult,
        string attachmentType,
        string description,
        CancellationToken ct = default)
    {
        await using var source = await fileResult.OpenReadAsync();
        return await SaveStreamAsync(
            source,
            fileResult.FileName ?? "attachment.bin",
            ResolveMimeType(fileResult.FileName, null),
            attachmentType,
            description,
            ct);
    }

    public async Task<PendingPaymentAttachmentRecord> ImportAsync(
        string sourcePath,
        string fileName,
        string mimeType,
        string attachmentType,
        string description,
        CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(sourcePath);
        return await SaveStreamAsync(stream, fileName, mimeType, attachmentType, description, ct);
    }

    public async Task RemoveAsync(PendingPaymentAttachmentRecord attachment, CancellationToken ct = default)
    {
        await Task.Yield();
        if (attachment is null || string.IsNullOrWhiteSpace(attachment.StoredPath))
            return;

        var draftRoot = EnsureTrailingDirectorySeparator(Path.GetFullPath(DraftDirectory));
        var storedPath = NormalizeDraftPathOrNull(attachment.StoredPath, draftRoot);
        if (!string.IsNullOrWhiteSpace(storedPath) && File.Exists(storedPath))
            File.Delete(storedPath);
    }

    public async Task<int> RemoveOrphanDraftsAsync(
        IEnumerable<PendingPaymentAttachmentRecord>? activeAttachments,
        TimeSpan minimumAge,
        CancellationToken ct = default)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        if (!Directory.Exists(DraftDirectory))
            return 0;

        var draftRoot = EnsureTrailingDirectorySeparator(Path.GetFullPath(DraftDirectory));
        var activePaths = (activeAttachments ?? Enumerable.Empty<PendingPaymentAttachmentRecord>())
            .Select(attachment => NormalizeDraftPathOrNull(attachment.StoredPath, draftRoot))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cutoffUtc = DateTime.UtcNow - (minimumAge < TimeSpan.Zero ? TimeSpan.Zero : minimumAge);
        var removed = 0;

        foreach (var path in Directory.EnumerateFiles(DraftDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();

            var fullPath = Path.GetFullPath(path);
            if (!IsDraftFileName(Path.GetFileName(fullPath)) ||
                activePaths.Contains(fullPath) ||
                File.GetLastWriteTimeUtc(fullPath) > cutoffUtc)
            {
                continue;
            }

            try
            {
                File.Delete(fullPath);
                removed++;
            }
            catch (Exception ex)
            {
                MobileAppLogger.Warn("SYNC", $"고아 수금첨부 임시 파일 정리 실패: {Path.GetFileName(fullPath)} / {ex.Message}");
            }
        }

        return removed;
    }

    public Task<Stream> OpenReadAsync(PendingPaymentAttachmentRecord attachment, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Stream stream = File.OpenRead(attachment.StoredPath);
        return Task.FromResult(stream);
    }

    private async Task<PendingPaymentAttachmentRecord> SaveStreamAsync(
        Stream source,
        string fileName,
        string mimeType,
        string attachmentType,
        string description,
        CancellationToken ct)
    {
        Directory.CreateDirectory(DraftDirectory);

        var localId = Guid.NewGuid();
        var safeFileName = string.IsNullOrWhiteSpace(fileName)
            ? $"attachment-{localId:N}.bin"
            : Path.GetFileName(fileName);
        var storedPath = Path.Combine(DraftDirectory, $"{localId:N}_{safeFileName}");

        await using var target = File.Create(storedPath);
        await source.CopyToAsync(target, ct);
        await target.FlushAsync(ct);
        target.Close();

        var bytes = await File.ReadAllBytesAsync(storedPath, ct);
        var mime = string.IsNullOrWhiteSpace(mimeType)
            ? ResolveMimeType(safeFileName, null)
            : mimeType;

        return new PendingPaymentAttachmentRecord
        {
            LocalId = localId,
            AttachmentType = string.IsNullOrWhiteSpace(attachmentType) ? "내역첨부" : attachmentType.Trim(),
            Description = description?.Trim() ?? string.Empty,
            FileName = safeFileName,
            StoredPath = storedPath,
            MimeType = mime,
            FileSize = bytes.LongLength,
            FileHash = Convert.ToHexString(SHA256.HashData(bytes)),
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    private static string ResolveMimeType(string? fileName, string? fallback)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        return extension switch
        {
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".gif" => "image/gif",
            _ => string.IsNullOrWhiteSpace(fallback) ? "application/octet-stream" : fallback
        };
    }

    private static string? NormalizeDraftPathOrNull(string? path, string draftRoot)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            var fullPath = Path.GetFullPath(path);
            return fullPath.StartsWith(draftRoot, StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsDraftFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length < 34 || fileName[32] != '_')
            return false;

        for (var index = 0; index < 32; index++)
        {
            var ch = fileName[index];
            if (!char.IsDigit(ch) &&
                ch is not (>= 'a' and <= 'f') &&
                ch is not (>= 'A' and <= 'F'))
            {
                return false;
            }
        }

        return true;
    }

    private static string EnsureTrailingDirectorySeparator(string path)
        => path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
}
