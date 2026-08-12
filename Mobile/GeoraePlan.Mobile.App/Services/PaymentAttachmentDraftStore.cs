using System.Security.Cryptography;
using System.Text;
using GeoraePlan.Mobile.App.Models;

namespace GeoraePlan.Mobile.App.Services;

public sealed class PaymentAttachmentDraftStore
{
    private const string DraftDirectoryName = "payment-attachment-drafts";
    private const string OwnerDirectoryName = "owners";
    private readonly SessionStore? _sessionStore;

    public PaymentAttachmentDraftStore()
    {
    }

    public PaymentAttachmentDraftStore(SessionStore sessionStore)
    {
        _sessionStore = sessionStore;
    }

    private string DraftDirectory =>
        Path.Combine(FileSystem.AppDataDirectory, DraftDirectoryName);

    public async Task<PendingPaymentAttachmentRecord> ImportAsync(
        MobileSessionOwner owner,
        FileResult fileResult,
        string attachmentType,
        string description,
        CancellationToken ct = default)
    {
        await using var source = await fileResult.OpenReadAsync();
        return await SaveStreamAsync(
            owner,
            source,
            fileResult.FileName ?? "attachment.bin",
            ResolveMimeType(fileResult.FileName, null),
            attachmentType,
            description,
            ct);
    }

    public async Task<PendingPaymentAttachmentRecord> ImportAsync(
        MobileSessionOwner owner,
        string sourcePath,
        string fileName,
        string mimeType,
        string attachmentType,
        string description,
        CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(sourcePath);
        return await SaveStreamAsync(
            owner,
            stream,
            fileName,
            mimeType,
            attachmentType,
            description,
            ct);
    }

    public async Task RemoveAsync(
        MobileSessionOwner owner,
        PendingPaymentAttachmentRecord attachment,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        if (attachment is null ||
            string.IsNullOrWhiteSpace(attachment.StoredPath))
        {
            return;
        }

        var storedPath = await ResolveOwnedPathAsync(
            owner,
            attachment,
            ct);
        if (!string.IsNullOrWhiteSpace(storedPath))
        {
            await OwnerBoundFileMutation.DeleteIfExistsAsync(
                storedPath,
                token => AcquireOwnerCommitLeaseAsync(
                    owner,
                    token),
                () => ThrowIfOwnerChanged(owner),
                ct);
        }
    }

    public async Task<int> RemoveOrphanDraftsAsync(
        MobileSessionOwner owner,
        IEnumerable<PendingPaymentAttachmentRecord>? activeAttachments,
        TimeSpan minimumAge,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        await Task.Yield();
        ct.ThrowIfCancellationRequested();

        var ownerDirectory = GetOwnerDirectory(owner);
        var activePaths = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var attachment in
                 activeAttachments ??
                 Enumerable.Empty<PendingPaymentAttachmentRecord>())
        {
            ct.ThrowIfCancellationRequested();
            var activePath = await ResolveOwnedPathAsync(
                owner,
                attachment,
                ct);
            if (!string.IsNullOrWhiteSpace(activePath))
                activePaths.Add(activePath);
        }

        if (!Directory.Exists(ownerDirectory))
            return 0;

        var cutoffUtc =
            DateTime.UtcNow -
            (minimumAge < TimeSpan.Zero
                ? TimeSpan.Zero
                : minimumAge);
        var removed = 0;
        foreach (var path in Directory.EnumerateFiles(
                     ownerDirectory,
                     "*",
                     SearchOption.TopDirectoryOnly))
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
                if (await OwnerBoundFileMutation.DeleteIfExistsAsync(
                        fullPath,
                        token => AcquireOwnerCommitLeaseAsync(
                            owner,
                            token),
                        () => ThrowIfOwnerChanged(owner),
                        ct))
                {
                    removed++;
                }
            }
            catch (StaleMobileSessionOwnerException)
            {
                throw;
            }
            catch (Exception ex)
            {
                MobileAppLogger.Warn(
                    "SYNC",
                    $"고아 수금첨부 임시 파일 정리 실패: {Path.GetFileName(fullPath)} / {ex.Message}");
            }
        }

        return removed;
    }

    public async Task<bool> PrepareOwnedDraftsAsync(
        MobileSessionOwner owner,
        IEnumerable<PendingPaymentAttachmentRecord>? attachments,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var changed = false;
        foreach (var attachment in
                 attachments ??
                 Enumerable.Empty<PendingPaymentAttachmentRecord>())
        {
            ct.ThrowIfCancellationRequested();
            var previousPath = attachment.StoredPath;
            await ResolveOwnedPathAsync(
                owner,
                attachment,
                ct);
            changed |= !string.Equals(
                previousPath,
                attachment.StoredPath,
                StringComparison.OrdinalIgnoreCase);
        }

        return changed;
    }

    public async Task<Stream> OpenReadAsync(
        MobileSessionOwner owner,
        PendingPaymentAttachmentRecord attachment,
        CancellationToken ct = default)
    {
        var storedPath = await ResolveOwnedPathAsync(
            owner,
            attachment,
            ct);
        if (string.IsNullOrWhiteSpace(storedPath))
        {
            throw new FileNotFoundException(
                "현재 로그인 소유자의 수금첨부 파일을 찾을 수 없습니다.");
        }

        var stream = new FileStream(
            storedPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        try
        {
            ThrowIfOwnerChanged(owner);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public async Task<string?> ResolveOwnedPathAsync(
        MobileSessionOwner owner,
        PendingPaymentAttachmentRecord attachment,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(attachment);
        ct.ThrowIfCancellationRequested();
        ThrowIfOwnerChanged(owner);

        var ownerRoot = EnsureTrailingDirectorySeparator(
            Path.GetFullPath(GetOwnerDirectory(owner)));
        var ownedPath = NormalizeDraftPathOrNull(
            attachment.StoredPath,
            ownerRoot);
        if (!string.IsNullOrWhiteSpace(ownedPath))
        {
            ThrowIfOwnerChanged(owner);
            return ownedPath;
        }

        var legacyPath = NormalizeLegacyDraftPathOrNull(
            attachment);
        if (string.IsNullOrWhiteSpace(legacyPath))
            return null;

        var destinationPath = Path.Combine(
            ownerRoot,
            Path.GetFileName(legacyPath));
        if (!File.Exists(legacyPath))
        {
            using (await AcquireOwnerCommitLeaseAsync(owner, ct))
            {
                ThrowIfOwnerChanged(owner);
                if (!await FilesMatchAttachmentAsync(
                        destinationPath,
                        attachment,
                        ct))
                {
                    return null;
                }

                ThrowIfOwnerChanged(owner);
                attachment.StoredPath = destinationPath;
                return destinationPath;
            }
        }

        using (await AcquireOwnerCommitLeaseAsync(owner, ct))
        {
            ThrowIfOwnerChanged(owner);
            Directory.CreateDirectory(ownerRoot);
            await MigrateLegacyDraftAsync(
                legacyPath,
                destinationPath,
                attachment,
                ct);
            ThrowIfOwnerChanged(owner);
            attachment.StoredPath = destinationPath;
            return destinationPath;
        }
    }

    private async Task<PendingPaymentAttachmentRecord> SaveStreamAsync(
        MobileSessionOwner owner,
        Stream source,
        string fileName,
        string mimeType,
        string attachmentType,
        string description,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var ownerDirectory = GetOwnerDirectory(owner);
        Directory.CreateDirectory(ownerDirectory);

        var localId = Guid.NewGuid();
        var safeFileName = string.IsNullOrWhiteSpace(fileName)
            ? $"attachment-{localId:N}.bin"
            : Path.GetFileName(fileName);
        var storedPath = Path.Combine(
            ownerDirectory,
            $"{localId:N}_{safeFileName}");
        var temporaryPath = Path.Combine(
            ownerDirectory,
            $".{localId:N}.{Guid.NewGuid():N}.tmp");
        long fileSize;
        string fileHash;
        try
        {
            await using (var target = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             FileOptions.Asynchronous |
                             FileOptions.SequentialScan))
            {
                await source.CopyToAsync(target, ct);
                await target.FlushAsync(ct);
            }

            await using (var verify = new FileStream(
                             temporaryPath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read))
            {
                fileSize = verify.Length;
                fileHash = Convert.ToHexString(
                    await SHA256.HashDataAsync(verify, ct));
            }

            await OwnerBoundFileMutation.PublishAsync(
                temporaryPath,
                storedPath,
                overwrite: false,
                token => AcquireOwnerCommitLeaseAsync(
                    owner,
                    token),
                () => ThrowIfOwnerChanged(owner),
                ct);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }

        var mime = string.IsNullOrWhiteSpace(mimeType)
            ? ResolveMimeType(safeFileName, null)
            : mimeType;

        return new PendingPaymentAttachmentRecord
        {
            LocalId = localId,
            AttachmentType = string.IsNullOrWhiteSpace(attachmentType)
                ? "내역첨부"
                : attachmentType.Trim(),
            Description = description?.Trim() ?? string.Empty,
            FileName = safeFileName,
            StoredPath = storedPath,
            MimeType = mime,
            FileSize = fileSize,
            FileHash = fileHash,
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    private async Task MigrateLegacyDraftAsync(
        string legacyPath,
        string destinationPath,
        PendingPaymentAttachmentRecord attachment,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(destinationPath))
        {
            try
            {
                File.Move(
                    legacyPath,
                    destinationPath,
                    overwrite: false);
                return;
            }
            catch (IOException)
                when (File.Exists(destinationPath))
            {
                // Another operation for the same owner may have
                // completed this deterministic migration first.
            }
        }

        if (!await FilesMatchAttachmentAsync(
                destinationPath,
                attachment,
                ct))
        {
            throw new IOException(
                "기존 수금첨부 파일과 소유자 저장 영역의 파일이 일치하지 않습니다.");
        }

        if (await FilesMatchAttachmentAsync(
                legacyPath,
                attachment,
                ct))
        {
            File.Delete(legacyPath);
        }
    }

    private static async Task<bool> FilesMatchAttachmentAsync(
        string path,
        PendingPaymentAttachmentRecord attachment,
        CancellationToken ct)
    {
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists)
            return false;
        if (attachment.FileSize > 0 &&
            fileInfo.Length != attachment.FileSize)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(attachment.FileHash))
            return true;

        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return string.Equals(
            Convert.ToHexString(hash),
            attachment.FileHash,
            StringComparison.OrdinalIgnoreCase);
    }

    private string GetOwnerDirectory(
        MobileSessionOwner owner)
    {
        var ownerKey = owner.BuildStateKey();
        if (string.Equals(
                ownerKey,
                "legacy",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "인증된 모바일 세션 소유자만 수금첨부 임시 파일을 사용할 수 있습니다.");
        }

        var ownerHash = Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(ownerKey)))
            .ToLowerInvariant();
        return Path.Combine(
            DraftDirectory,
            OwnerDirectoryName,
            ownerHash);
    }

    private string? NormalizeLegacyDraftPathOrNull(
        PendingPaymentAttachmentRecord attachment)
    {
        if (string.IsNullOrWhiteSpace(attachment.StoredPath))
            return null;

        try
        {
            var legacyRoot = EnsureTrailingDirectorySeparator(
                Path.GetFullPath(DraftDirectory));
            var fullPath = Path.GetFullPath(
                attachment.StoredPath);
            var parent = Path.GetDirectoryName(fullPath);
            if (!string.Equals(
                    EnsureTrailingDirectorySeparator(
                        parent ?? string.Empty),
                    legacyRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var fileName = Path.GetFileName(fullPath);
            return IsDraftFileName(fileName) &&
                   fileName.StartsWith(
                       attachment.LocalId.ToString("N"),
                       StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveMimeType(
        string? fileName,
        string? fallback)
    {
        var extension = Path.GetExtension(
                fileName ?? string.Empty)
            .ToLowerInvariant();
        return extension switch
        {
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".gif" => "image/gif",
            _ => string.IsNullOrWhiteSpace(fallback)
                ? "application/octet-stream"
                : fallback
        };
    }

    private static string? NormalizeDraftPathOrNull(
        string? path,
        string draftRoot)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            var fullPath = Path.GetFullPath(path);
            return fullPath.StartsWith(
                draftRoot,
                StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsDraftFileName(
        string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            fileName.Length < 34 ||
            fileName[32] != '_')
        {
            return false;
        }

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

    private async Task<IDisposable> AcquireOwnerCommitLeaseAsync(
        MobileSessionOwner owner,
        CancellationToken ct)
    {
        if (_sessionStore is null)
            return NoopDisposable.Instance;

        return await _sessionStore.AcquireOwnerCommitLeaseAsync(
            owner,
            ct);
    }

    private void ThrowIfOwnerChanged(
        MobileSessionOwner owner)
    {
        _sessionStore?.ThrowIfOwnerChanged(owner);
    }

    private static string EnsureTrailingDirectorySeparator(
        string path)
        => path.EndsWith(Path.DirectorySeparatorChar) ||
           path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
