using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;

namespace 거래플랜.Desktop.App.Services;

internal enum AttachmentCommitResolution
{
    Committed,
    RolledBack,
    Unknown
}

internal sealed class AttachmentFileJournalContentionException(
    string message,
    Exception innerException)
    : InvalidOperationException(message, innerException);

/// <summary>
/// Keeps attachment file mutations reversible until the related database
/// transaction has committed.
/// </summary>
internal sealed class AttachmentFileJournal : IDisposable
{
    internal static Action<string>? BeforePathMutationForTesting { get; set; }
    internal static Action<string>? BeforeDirectoryCreateForTesting { get; set; }
    internal static Action<string>? BeforeManifestMoveForTesting { get; set; }
    internal static Func<string, CancellationToken, Task>?
        AfterRecoveryRootEnsuredBeforeLeaseAsyncForTesting { get; set; }

    private const string JournalDirectoryPrefix = "attachment-files-";
    private const string RecoveryManifestFileName = "recovery.json";
    private const string RecoveryManifestTemporaryFileName = "recovery.json.tmp";
    private const string ActiveLeaseFileName = "active.lock";
    private const string RootMutationLeaseFileName = "mutation-root.lock";
    private const string CommitEvidenceSettingPrefix =
        "__internal.attachment-file-commit.";
    private const int RootMutationLeaseRetryMilliseconds = 25;
    private readonly string _journalDirectory;
    private readonly string _journalRoot;
    private readonly string _allowedMutationRoot;
    private readonly string _commitEvidenceSettingKey =
        $"{CommitEvidenceSettingPrefix}{Guid.NewGuid():N}";
    // One OS-backed lease per journal root serializes file snapshots through
    // the post-commit reference scan. The file remains across process exits,
    // while the exclusive handle is released automatically on a hard stop.
    private readonly SemaphoreSlim _rootMutationLeaseGate = new(1, 1);
    private FileStream? _activeLease;
    private IDisposable? _rootMutationLease;
    private readonly List<WriteOperation> _writes = [];
    private readonly List<DeleteOperation> _deletes = [];
    private bool _promoted;
    private bool _completed;
    private bool _rolledBack;
    private bool _preservedForRecovery;
    private bool _commitEvidenceStaged;
    private bool _disposed;

    public AttachmentFileJournal(
        string journalRoot,
        string allowedMutationRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(allowedMutationRoot);

        var fullJournalRoot = NormalizeDirectoryPath(journalRoot);
        var fullAllowedMutationRoot =
            NormalizeDirectoryPath(allowedMutationRoot);
        if (!string.Equals(
                Path.GetPathRoot(fullJournalRoot),
                Path.GetPathRoot(fullAllowedMutationRoot),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The attachment journal and destination must use the same filesystem volume.");
        }

        _journalRoot = fullJournalRoot;
        _journalDirectory = Path.Combine(
            _journalRoot,
            $"{JournalDirectoryPrefix}{Guid.NewGuid():N}");
        _allowedMutationRoot = fullAllowedMutationRoot;
        var journalVolumeRoot = Path.GetPathRoot(fullJournalRoot);
        if (string.IsNullOrWhiteSpace(journalVolumeRoot))
        {
            throw new InvalidOperationException(
                "The attachment journal volume root could not be resolved.");
        }

        EnsureExistingPathChainHasNoReparsePoint(
            journalVolumeRoot,
            fullJournalRoot);
        ThrowIfReparsePoint(fullJournalRoot);
        EnsureDirectoryExistsSafely(_journalDirectory);
        var activeLeasePath = ResolveSafeJournalMutationPath(Path.Combine(
            _journalDirectory,
            ActiveLeaseFileName));
        using (var journalDirectoryLease = AcquirePathMutationLease(
                   _journalRoot,
                   activeLeasePath))
        {
            journalDirectoryLease.Validate();
            activeLeasePath = ResolveSafeJournalMutationPath(activeLeasePath);
            _activeLease = new FileStream(
                activeLeasePath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough);
            journalDirectoryLease.Validate();
        }
    }

    public async Task StageWriteAsync(
        string destinationPath,
        ReadOnlyMemory<byte> content,
        CancellationToken ct = default)
    {
        ThrowIfMutationClosed();
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var fullDestinationPath = ResolveSafeMutationPath(destinationPath);
        await AcquireRootMutationLeaseAsync(ct);
        fullDestinationPath = ResolveSafeMutationPath(destinationPath);
        if (_writes.Any(current =>
                string.Equals(
                    current.DestinationPath,
                    fullDestinationPath,
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"같은 첨부파일 경로를 두 번 스테이징할 수 없습니다: {fullDestinationPath}");
        }

        if (File.Exists(fullDestinationPath))
        {
            if (FileMatchesContent(fullDestinationPath, content.Span))
            {
                _deletes.RemoveAll(current =>
                    string.Equals(
                        current.OriginalPath,
                        fullDestinationPath,
                        StringComparison.OrdinalIgnoreCase));
                return;
            }

            throw new InvalidOperationException(
                "기존 첨부파일과 다른 내용을 같은 경로에 덮어쓸 수 없습니다.");
        }

        var stagedPath = ResolveSafeJournalMutationPath(Path.Combine(
            _journalDirectory,
            $"{Guid.NewGuid():N}.stage"));
        try
        {
            await using var stream = new FileStream(
                stagedPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(content, ct);
            await stream.FlushAsync(ct);
        }
        catch
        {
            TryDeleteJournalFileBestEffort(stagedPath);
            throw;
        }

        stagedPath = ResolveSafeJournalMutationPath(stagedPath);
        var identity = TryReadFileIdentity(stagedPath);
        if (!identity.IsComplete)
        {
            TryDeleteJournalFileBestEffort(stagedPath);
            throw new IOException(
                "스테이징한 첨부파일의 Windows 파일 ID를 확인할 수 없습니다.");
        }

        _writes.Add(new WriteOperation(
            fullDestinationPath,
            stagedPath,
            content.Length,
            Convert.ToHexString(SHA256.HashData(content.Span)),
            identity.VolumeSerialNumber,
            identity.FileId));
        _deletes.RemoveAll(current =>
            string.Equals(
                current.OriginalPath,
                fullDestinationPath,
                StringComparison.OrdinalIgnoreCase));
    }

    public async Task StageCopyAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken ct = default)
    {
        ThrowIfMutationClosed();
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var fullSourcePath = ResolveSafeMutationPath(sourcePath);
        var fullDestinationPath = ResolveSafeMutationPath(destinationPath);
        if (string.Equals(
                fullSourcePath,
                fullDestinationPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The attachment copy source and destination must differ.");
        }

        await AcquireRootMutationLeaseAsync(ct);
        fullSourcePath = ResolveSafeMutationPath(sourcePath);
        fullDestinationPath = ResolveSafeMutationPath(destinationPath);
        if (_writes.Any(current =>
                string.Equals(
                    current.DestinationPath,
                    fullDestinationPath,
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"The same attachment destination cannot be staged twice: {fullDestinationPath}");
        }

        using var sourceMutationLease = AcquirePathMutationLease(
            _allowedMutationRoot,
            fullSourcePath);
        sourceMutationLease.Validate();
        using var sourceHandle = OpenStableFileHandle(
            fullSourcePath,
            requireDeleteAccess: false);
        if (sourceHandle.IsInvalid ||
            !TryGetFileSnapshot(sourceHandle, out var sourceIdentity) ||
            !sourceIdentity.IsComplete)
        {
            throw new IOException(
                "The attachment copy source could not be opened as a stable regular file.");
        }

        sourceMutationLease.Validate();
        var currentSourceIdentity = TryReadFileIdentity(fullSourcePath);
        if (!FileSnapshotsMatch(currentSourceIdentity, sourceIdentity))
        {
            throw new IOException(
                "The attachment copy source changed before staging began.");
        }

        if (File.Exists(fullDestinationPath))
        {
            var destinationIdentity = TryReadFileIdentity(fullDestinationPath);
            if (FileSnapshotContentMatches(
                    destinationIdentity,
                    sourceIdentity))
            {
                _deletes.RemoveAll(current =>
                    string.Equals(
                        current.OriginalPath,
                        fullDestinationPath,
                        StringComparison.OrdinalIgnoreCase));
                return;
            }

            throw new InvalidOperationException(
                "An attachment with different content already exists at the copy destination.");
        }

        var stagedPath = ResolveSafeJournalMutationPath(Path.Combine(
            _journalDirectory,
            $"{Guid.NewGuid():N}.stage"));
        try
        {
            await using var stagedStream = new FileStream(
                stagedPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            var buffer = new byte[81920];
            long offset = 0;
            while (offset < sourceIdentity.Length)
            {
                ct.ThrowIfCancellationRequested();
                var count = RandomAccess.Read(sourceHandle, buffer, offset);
                if (count == 0)
                {
                    throw new IOException(
                        "The attachment copy source ended before its verified length.");
                }

                await stagedStream.WriteAsync(
                    buffer.AsMemory(0, count),
                    ct);
                offset += count;
            }

            if (RandomAccess.GetLength(sourceHandle) != sourceIdentity.Length)
            {
                throw new IOException(
                    "The attachment copy source length changed while staging.");
            }

            await stagedStream.FlushAsync(ct);
        }
        catch
        {
            TryDeleteJournalFileBestEffort(stagedPath);
            throw;
        }

        sourceMutationLease.Validate();
        currentSourceIdentity = TryReadFileIdentity(fullSourcePath);
        var stagedIdentity = TryReadFileIdentity(stagedPath);
        if (!FileSnapshotsMatch(currentSourceIdentity, sourceIdentity) ||
            !FileSnapshotContentMatches(stagedIdentity, sourceIdentity))
        {
            TryDeleteJournalFileBestEffort(stagedPath);
            throw new IOException(
                "The attachment copy source or staged content changed during staging.");
        }

        _writes.Add(new WriteOperation(
            fullDestinationPath,
            stagedPath,
            stagedIdentity.Length,
            stagedIdentity.Sha256,
            stagedIdentity.VolumeSerialNumber,
            stagedIdentity.FileId));
        _deletes.RemoveAll(current =>
            string.Equals(
                current.OriginalPath,
                fullDestinationPath,
                StringComparison.OrdinalIgnoreCase));
    }

    public void StageDelete(string path)
    {
        ThrowIfMutationClosed();
        if (string.IsNullOrWhiteSpace(path))
            return;

        var fullPath = ResolveSafeMutationPath(path);
        AcquireRootMutationLease();
        fullPath = ResolveSafeMutationPath(path);
        var pendingWrite = _writes.FirstOrDefault(current =>
            string.Equals(
                current.DestinationPath,
                fullPath,
                StringComparison.OrdinalIgnoreCase));
        if (pendingWrite is not null)
        {
            // A write followed by a delete in the same database transaction means
            // the final committed state has no file. Cancel the staged write
            // before the durable manifest is created.
            _writes.Remove(pendingWrite);
            TryDeleteJournalFileBestEffort(pendingWrite.StagedPath);
            _deletes.RemoveAll(current =>
                string.Equals(
                    current.OriginalPath,
                    fullPath,
                    StringComparison.OrdinalIgnoreCase));
            return;
        }

        if (_deletes.Any(current =>
                string.Equals(current.OriginalPath, fullPath, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var identity = TryReadFileIdentity(fullPath);
        _deletes.Add(new DeleteOperation(
            fullPath,
            identity.Length,
            identity.Sha256,
            identity.VolumeSerialNumber,
            identity.FileId));
    }

    internal async Task StageCommitEvidenceAsync(
        LocalDbContext db,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ThrowIfMutationClosed();
        if (_commitEvidenceStaged)
            return;
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "Attachment commit evidence must be staged in the same database transaction.");
        }

        // Zero-file transactions still need the same root lease. Otherwise a
        // concurrent startup recovery could remove a just-committed marker
        // before a post-commit exception is independently resolved.
        AcquireRootMutationLease();
        db.Settings.Add(new LocalSetting
        {
            Key = _commitEvidenceSettingKey,
            Value = DateTime.UtcNow.ToString("O")
        });
        await db.SaveChangesAsync(ct);
        _commitEvidenceStaged = true;
    }

    /// <summary>
    /// Promotes only staged writes. Deletes remain deferred until Complete so a
    /// process stop before the database commit cannot remove the prior files.
    /// Call this after SaveChanges and before committing the DB transaction.
    /// </summary>
    public void Promote()
    {
        ThrowIfFinalized();
        if (_promoted)
            return;

        try
        {
            PersistRecoveryManifest();

            foreach (var write in _writes)
            {
                var safeDestinationPath = ResolveSafeMutationPath(
                    write.DestinationPath);
                var destinationDirectory = Path.GetDirectoryName(
                    safeDestinationPath);
                if (!string.IsNullOrWhiteSpace(destinationDirectory))
                    EnsureDirectoryExistsSafely(destinationDirectory);

                using var destinationMutationLease =
                    AcquirePathMutationLease(
                        _allowedMutationRoot,
                        write.DestinationPath);
                using var stagedMutationLease =
                    AcquirePathMutationLease(
                        _journalDirectory,
                        write.StagedPath);
                BeforePathMutationForTesting?.Invoke(
                    write.DestinationPath);

                // Directory creation and a hostile parent swap are separate
                // mutation boundaries. Recheck the entire existing path chain
                // immediately before the move.
                safeDestinationPath = ResolveSafeMutationPath(
                    write.DestinationPath);
                var safeStagedPath = ResolveSafeJournalMutationPath(
                    write.StagedPath);
                destinationMutationLease.Validate();
                stagedMutationLease.Validate();
                File.Move(safeStagedPath, safeDestinationPath);
                // From this point rollback must inspect the destination even
                // if a subsequent parent or file-identity verification fails.
                write.WasPromoted = true;
                destinationMutationLease.Validate();
                stagedMutationLease.Validate();
                var promotedIdentity = TryReadFileIdentity(
                    safeDestinationPath);
                if (!promotedIdentity.IsComplete ||
                    promotedIdentity.Length != write.Length ||
                    !string.Equals(
                        promotedIdentity.Sha256,
                        write.Sha256,
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(
                        promotedIdentity.VolumeSerialNumber,
                        write.VolumeSerialNumber,
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(
                        promotedIdentity.FileId,
                        write.FileId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException(
                        "The promoted attachment identity changed during the atomic move.");
                }
            }

            _promoted = true;
        }
        catch
        {
            Rollback();
            throw;
        }
    }

    /// <summary>
    /// Marks the file work durable after the database commit, then performs
    /// best-effort deletion of files no longer referenced by committed rows.
    /// </summary>
    public void Complete()
        => CompleteCore(committedReferencedPaths: null);

    /// <summary>
    /// Completes a committed file transaction only after re-reading both
    /// attachment reference tables. A surviving legacy/shared reference wins
    /// over a staged delete.
    /// </summary>
    internal async Task CompleteAfterDatabaseCommitAsync(
        LocalDbContext sourceDb,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sourceDb);
        if (_completed)
            return;
        if (!_promoted)
            throw new InvalidOperationException("파일 작업을 승격하기 전에 완료할 수 없습니다.");
        if (sourceDb.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException(
                "첨부파일 저널 완료 전에 소유한 DB 트랜잭션을 종료해야 합니다.");
        }

        try
        {
            var referencedPaths = await ReadReferencedPathsAsync(sourceDb, ct);
            CompleteCore(referencedPaths);
            await RemoveCommitEvidenceBestEffortAsync(
                sourceDb,
                CancellationToken.None);
        }
        catch
        {
            // The database commit is already final. If the committed reference
            // set cannot be established, keep every file and the durable
            // journal for the next startup recovery pass.
            PreserveForRecovery();
        }
    }

    private void CompleteCore(
        IReadOnlySet<string>? committedReferencedPaths)
    {
        if (_completed)
            return;
        if (!_promoted)
            throw new InvalidOperationException("파일 작업을 승격하기 전에 완료할 수 없습니다.");

        var canFinalize = true;
        if (committedReferencedPaths is not null)
        {
            foreach (var write in _writes.Where(current => current.WasPromoted))
            {
                if (committedReferencedPaths.Contains(write.DestinationPath))
                    continue;

                if (!TryResolveSafeMutationPathForFinalization(
                        write.DestinationPath,
                        out var safeDestinationPath))
                {
                    canFinalize = false;
                    continue;
                }

                if (!File.Exists(safeDestinationPath))
                    continue;

                var deleteResult = TryDeleteMatchingFile(
                    _allowedMutationRoot,
                    safeDestinationPath,
                    write.Length,
                    write.Sha256,
                    write.VolumeSerialNumber,
                    write.FileId);
                if (deleteResult is not IdentityDeleteResult.Deleted and
                    not IdentityDeleteResult.Missing)
                {
                    canFinalize = false;
                }
            }
        }

        foreach (var delete in _deletes)
        {
            if (committedReferencedPaths?.Contains(delete.OriginalPath) == true)
                continue;

            if (!TryResolveSafeMutationPathForFinalization(
                    delete.OriginalPath,
                    out var safeOriginalPath))
            {
                canFinalize = false;
                continue;
            }

            if (!File.Exists(safeOriginalPath))
                continue;

            var deleteResult = TryDeleteMatchingFile(
                _allowedMutationRoot,
                safeOriginalPath,
                delete.Length,
                delete.Sha256,
                delete.VolumeSerialNumber,
                delete.FileId);
            if (deleteResult is not IdentityDeleteResult.Deleted and
                not IdentityDeleteResult.Missing)
            {
                canFinalize = false;
            }
        }

        _completed = true;
        ReleaseActiveLeaseBestEffort();
        if (canFinalize)
        {
            CleanupJournalBestEffort();
        }
        else
        {
            // The database commit is final, but a path now contains content
            // different from the file staged for deletion. Preserve both the
            // replacement and the durable journal for operator review.
            _preservedForRecovery = true;
        }

        ReleaseRootMutationLeaseBestEffort();
    }

    private static async Task<HashSet<string>> ReadReferencedPathsAsync(
        LocalDbContext sourceDb,
        CancellationToken ct)
    {
        var storedPaths = await sourceDb.TransactionAttachments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(attachment => attachment.StoredPath != string.Empty)
            .Select(attachment => attachment.StoredPath)
            .ToListAsync(ct);
        storedPaths.AddRange(await sourceDb.InventoryTransfers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(transfer => transfer.ReceiveEvidencePath != string.Empty)
            .Select(transfer => transfer.ReceiveEvidencePath)
            .ToListAsync(ct));
        storedPaths.AddRange(await sourceDb.InventoryTransferTombstoneConflicts
            .AsNoTracking()
            .Where(conflict =>
                conflict.ArchivedReceiveEvidencePath != string.Empty)
            .Select(conflict => conflict.ArchivedReceiveEvidencePath)
            .ToListAsync(ct));

        var referencedPaths = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var path in storedPaths)
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;

            try
            {
                referencedPaths.Add(Path.GetFullPath(path));
            }
            catch
            {
                // An unrelated malformed legacy path cannot match a validated
                // journal destination and must not block safe cleanup.
            }
        }

        return referencedPaths;
    }

    private async Task RemoveCommitEvidenceBestEffortAsync(
        LocalDbContext db,
        CancellationToken ct)
    {
        if (!_commitEvidenceStaged)
            return;

        try
        {
            await db.Settings
                .Where(setting =>
                    setting.Key == _commitEvidenceSettingKey)
                .ExecuteDeleteAsync(ct);
            foreach (var trackedSetting in db.Settings.Local
                         .Where(setting => string.Equals(
                             setting.Key,
                             _commitEvidenceSettingKey,
                             StringComparison.Ordinal))
                         .ToList())
            {
                db.Entry(trackedSetting).State =
                    EntityState.Detached;
            }

            _commitEvidenceStaged = false;
        }
        catch
        {
            // A reserved marker residue is safe and is reclaimed by startup
            // recovery after it has acquired the same root mutation lease.
        }
    }

    private static async Task RemoveCommitEvidenceResidueBestEffortAsync(
        LocalDbContext db,
        CancellationToken ct)
    {
        try
        {
            await db.Settings
                .Where(setting =>
                    setting.Key.StartsWith(CommitEvidenceSettingPrefix))
                .ExecuteDeleteAsync(ct);
            foreach (var trackedSetting in db.Settings.Local
                         .Where(setting => setting.Key.StartsWith(
                             CommitEvidenceSettingPrefix,
                             StringComparison.Ordinal))
                         .ToList())
            {
                db.Entry(trackedSetting).State =
                    EntityState.Detached;
            }
        }
        catch
        {
            // Reserved evidence never affects business state. A later startup
            // pass can retry cleanup without changing commit interpretation.
        }
    }

    public void Rollback()
    {
        if (_completed || _rolledBack)
            return;

        _rolledBack = true;
        foreach (var write in _writes.AsEnumerable().Reverse())
        {
            try
            {
                if (write.WasPromoted)
                {
                    if (TryResolveSafeMutationPathForFinalization(
                            write.DestinationPath,
                            out var safeDestinationPath))
                    {
                        _ = TryDeleteMatchingFile(
                            _allowedMutationRoot,
                            safeDestinationPath,
                            write.Length,
                            write.Sha256,
                            write.VolumeSerialNumber,
                            write.FileId);
                    }
                }

                TryDeleteJournalFileBestEffort(write.StagedPath);
            }
            catch
            {
                // Keep any remaining journal file for operator recovery.
            }
        }

        ReleaseActiveLeaseBestEffort();
        CleanupEmptyJournalDirectoryBestEffort();
        ReleaseRootMutationLeaseBestEffort();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (!_completed && !_preservedForRecovery)
            Rollback();
    }

    internal async Task<AttachmentCommitResolution> ResolveCommitAmbiguityAsync(
        LocalDbContext sourceDb,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sourceDb);
        if (_completed)
            return AttachmentCommitResolution.Committed;
        if (_rolledBack)
            return AttachmentCommitResolution.RolledBack;
        if (_preservedForRecovery)
            return AttachmentCommitResolution.Unknown;
        if (!_promoted)
        {
            Rollback();
            return AttachmentCommitResolution.RolledBack;
        }

        try
        {
            var connectionString = sourceDb.Database.GetConnectionString();
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                PreserveForRecovery();
                return AttachmentCommitResolution.Unknown;
            }

            var options = new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite(connectionString)
                .Options;
            await using var verificationDb = new LocalDbContext(options);
            var commitEvidenceExists = await verificationDb.Settings
                .IgnoreQueryFilters()
                .AsNoTracking()
                .AnyAsync(
                    setting => setting.Key == _commitEvidenceSettingKey,
                    ct);
            if (commitEvidenceExists)
            {
                await CompleteAfterDatabaseCommitAsync(
                    verificationDb,
                    CancellationToken.None);
                return AttachmentCommitResolution.Committed;
            }

            Rollback();
            return AttachmentCommitResolution.RolledBack;
        }
        catch
        {
            // If an independent read cannot establish the commit outcome, keep
            // all files and let the durable journal be resolved on re-entry.
        }

        PreserveForRecovery();
        return AttachmentCommitResolution.Unknown;
    }

    internal void PreserveForRecovery()
    {
        if (_completed || _rolledBack || _preservedForRecovery)
            return;

        _preservedForRecovery = true;
        ReleaseActiveLeaseBestEffort();
        ReleaseRootMutationLeaseBestEffort();
    }

    /// <summary>
    /// Recovers journals left by a process stop between file promotion and the
    /// database commit. A promoted file is removed only when no database row
    /// references it and its length/hash still match the durable journal entry.
    /// </summary>
    internal static async Task RecoverIncompleteJournalsAsync(
        LocalDbContext db,
        string journalRoot,
        string allowedMutationRoot,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(journalRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(allowedMutationRoot);

        ResolveAndValidateRecoveryRoots(
            journalRoot,
            allowedMutationRoot,
            out var fullJournalRoot,
            out var fullAllowedRoot,
            out var journalVolumeRoot,
            out var allowedVolumeRoot);
        EnsureDirectoryExistsSafely(fullJournalRoot);
        if (AfterRecoveryRootEnsuredBeforeLeaseAsyncForTesting is not null)
        {
            await AfterRecoveryRootEnsuredBeforeLeaseAsyncForTesting(
                fullJournalRoot,
                ct);
        }

        using var rootMutationLease =
            TryAcquireRootMutationLeaseForRecovery(fullJournalRoot);
        if (rootMutationLease is null)
            return;

        ValidateRecoveryRootChains(
            fullJournalRoot,
            fullAllowedRoot,
            journalVolumeRoot,
            allowedVolumeRoot);

        var referencedPaths = await db.TransactionAttachments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(attachment => attachment.StoredPath != string.Empty)
            .Select(attachment => attachment.StoredPath)
            .ToListAsync(ct);
        referencedPaths.AddRange(await db.InventoryTransfers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(transfer => transfer.ReceiveEvidencePath != string.Empty)
            .Select(transfer => transfer.ReceiveEvidencePath)
            .ToListAsync(ct));
        referencedPaths.AddRange(await db.InventoryTransferTombstoneConflicts
            .AsNoTracking()
            .Where(conflict =>
                conflict.ArchivedReceiveEvidencePath != string.Empty)
            .Select(conflict => conflict.ArchivedReceiveEvidencePath)
            .ToListAsync(ct));

        RecoverIncompleteJournalsCore(
            fullJournalRoot,
            fullAllowedRoot,
            referencedPaths);
        await RemoveCommitEvidenceResidueBestEffortAsync(
            db,
            CancellationToken.None);
    }

    internal static void RecoverIncompleteJournals(
        string journalRoot,
        string allowedMutationRoot,
        IEnumerable<string?> databaseReferencedPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(allowedMutationRoot);
        ArgumentNullException.ThrowIfNull(databaseReferencedPaths);

        ResolveAndValidateRecoveryRoots(
            journalRoot,
            allowedMutationRoot,
            out var fullJournalRoot,
            out var fullAllowedRoot,
            out var journalVolumeRoot,
            out var allowedVolumeRoot);
        EnsureDirectoryExistsSafely(fullJournalRoot);

        using var rootMutationLease =
            TryAcquireRootMutationLeaseForRecovery(fullJournalRoot);
        if (rootMutationLease is null)
            return;

        ValidateRecoveryRootChains(
            fullJournalRoot,
            fullAllowedRoot,
            journalVolumeRoot,
            allowedVolumeRoot);

        RecoverIncompleteJournalsCore(
            fullJournalRoot,
            fullAllowedRoot,
            databaseReferencedPaths);
    }

    private static void RecoverIncompleteJournalsCore(
        string fullJournalRoot,
        string fullAllowedRoot,
        IEnumerable<string?> databaseReferencedPaths)
    {
        var referencedPaths = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var path in databaseReferencedPaths)
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;

            try
            {
                referencedPaths.Add(Path.GetFullPath(path));
            }
            catch
            {
                // A malformed legacy metadata path must not block recovery.
                // It cannot match a validated destination and is never deleted.
            }
        }

        foreach (var journalDirectory in Directory.EnumerateDirectories(
                     fullJournalRoot,
                     $"{JournalDirectoryPrefix}*",
                     SearchOption.TopDirectoryOnly))
        {
            if ((File.GetAttributes(journalDirectory) & FileAttributes.ReparsePoint) != 0)
                continue;

            var leasePath = Path.Combine(journalDirectory, ActiveLeaseFileName);
            FileStream recoveryLease;
            try
            {
                recoveryLease = new FileStream(
                    leasePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.WriteThrough);
            }
            catch (IOException)
            {
                // Another operation still owns this journal.
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            using (recoveryLease)
            {
                RecoverJournalDirectory(
                    journalDirectory,
                    fullJournalRoot,
                    fullAllowedRoot,
                    referencedPaths);
            }

            TryDeleteFileBestEffort(leasePath);
            try
            {
                if (!Directory.EnumerateFileSystemEntries(journalDirectory).Any())
                    Directory.Delete(journalDirectory, recursive: false);
            }
            catch
            {
                // A later recovery pass can retry private journal cleanup.
            }
        }
    }

    private string ResolveSafeMutationPath(string path)
        => ResolveSafeMutationPath(
            _allowedMutationRoot,
            path,
            _journalRoot);

    private string ResolveSafeJournalMutationPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!IsPathAtOrBelow(_journalDirectory, fullPath))
        {
            throw new InvalidOperationException(
                "첨부파일 저널 자체 범위 밖의 경로는 변경할 수 없습니다.");
        }

        var volumeRoot = Path.GetPathRoot(_journalDirectory);
        if (string.IsNullOrWhiteSpace(volumeRoot))
        {
            throw new InvalidOperationException(
                "첨부파일 저널 볼륨 루트를 확인할 수 없습니다.");
        }

        EnsureExistingPathChainHasNoReparsePoint(
            volumeRoot,
            fullPath);
        ThrowIfReparsePoint(fullPath);
        return fullPath;
    }

    private async Task AcquireRootMutationLeaseAsync(
        CancellationToken ct)
    {
        await _rootMutationLeaseGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_rootMutationLease is not null)
                return;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var safeLeasePath = ResolveRootMutationLeasePath(
                    _journalRoot);
                try
                {
                    _rootMutationLease = OpenRootMutationLease(
                        _journalRoot,
                        safeLeasePath);
                    return;
                }
                catch (IOException ex) when (IsRootMutationLeaseContention(ex))
                {
                    await Task.Delay(
                            RootMutationLeaseRetryMilliseconds,
                            ct)
                        .ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _rootMutationLeaseGate.Release();
        }
    }

    private static void ResolveAndValidateRecoveryRoots(
        string journalRoot,
        string allowedMutationRoot,
        out string fullJournalRoot,
        out string fullAllowedRoot,
        out string journalVolumeRoot,
        out string allowedVolumeRoot)
    {
        fullJournalRoot = NormalizeDirectoryPath(journalRoot);
        fullAllowedRoot = NormalizeDirectoryPath(allowedMutationRoot);
        journalVolumeRoot = Path.GetPathRoot(fullJournalRoot) ?? string.Empty;
        allowedVolumeRoot = Path.GetPathRoot(fullAllowedRoot) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(journalVolumeRoot) ||
            string.IsNullOrWhiteSpace(allowedVolumeRoot) ||
            !string.Equals(
                journalVolumeRoot,
                allowedVolumeRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The attachment journal and destination must use the same filesystem volume.");
        }

        ValidateRecoveryRootChains(
            fullJournalRoot,
            fullAllowedRoot,
            journalVolumeRoot,
            allowedVolumeRoot);
    }

    private static void ValidateRecoveryRootChains(
        string fullJournalRoot,
        string fullAllowedRoot,
        string journalVolumeRoot,
        string allowedVolumeRoot)
    {
        EnsureExistingPathChainHasNoReparsePoint(
            journalVolumeRoot,
            fullJournalRoot);
        ThrowIfReparsePoint(fullJournalRoot);
        EnsureExistingPathChainHasNoReparsePoint(
            allowedVolumeRoot,
            fullAllowedRoot);
        ThrowIfReparsePoint(fullAllowedRoot);
    }

    private void AcquireRootMutationLease()
    {
        if (!_rootMutationLeaseGate.Wait(0))
        {
            throw new InvalidOperationException(
                "Another attachment file mutation is already being staged by this journal.");
        }

        try
        {
            if (_rootMutationLease is not null)
                return;

            var safeLeasePath = ResolveRootMutationLeasePath(
                _journalRoot);
            try
            {
                _rootMutationLease = OpenRootMutationLease(
                    _journalRoot,
                    safeLeasePath);
            }
            catch (IOException ex) when (IsRootMutationLeaseContention(ex))
            {
                // StageDelete is synchronous and may run on the UI thread.
                // Blocking here could deadlock the async owner whose commit
                // continuation needs that thread, so fail closed and retry the
                // outer operation after the active transaction finishes.
                throw new AttachmentFileJournalContentionException(
                    "Another attachment file transaction is active. Retry the operation.",
                    ex);
            }
        }
        finally
        {
            _rootMutationLeaseGate.Release();
        }
    }

    private static IDisposable? TryAcquireRootMutationLeaseForRecovery(
        string journalRoot)
    {
        var safeLeasePath = ResolveRootMutationLeasePath(journalRoot);
        try
        {
            return OpenRootMutationLease(
                journalRoot,
                safeLeasePath);
        }
        catch (IOException ex) when (IsRootMutationLeaseContention(ex))
        {
            // A live transaction owns the root. Its DB commit and reference
            // snapshot must finish before recovery can inspect any journal.
            return null;
        }
    }

    private static IDisposable OpenRootMutationLease(
        string journalRoot,
        string safeLeasePath)
    {
        var pathLease = AcquirePathMutationLease(
            journalRoot,
            safeLeasePath);
        FileStream? fileLease = null;
        try
        {
            pathLease.Validate();
            fileLease = new FileStream(
                safeLeasePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough);
            pathLease.Validate();
            return new CompositeMutationLease(
                fileLease!,
                pathLease);
        }
        catch
        {
            fileLease?.Dispose();
            pathLease.Dispose();
            throw;
        }
    }

    private static string ResolveRootMutationLeasePath(
        string journalRoot)
    {
        var fullJournalRoot = NormalizeDirectoryPath(journalRoot);
        var volumeRoot = Path.GetPathRoot(fullJournalRoot);
        if (string.IsNullOrWhiteSpace(volumeRoot))
        {
            throw new InvalidOperationException(
                "The attachment journal volume root could not be resolved.");
        }

        var fullLeasePath = Path.Combine(
            fullJournalRoot,
            RootMutationLeaseFileName);
        EnsureExistingPathChainHasNoReparsePoint(
            volumeRoot,
            fullLeasePath);
        ThrowIfReparsePoint(fullLeasePath);
        return fullLeasePath;
    }

    private static bool IsRootMutationLeaseContention(
        IOException exception)
    {
        var nativeError = exception.HResult & 0xFFFF;
        return nativeError is ErrorSharingViolation or ErrorLockViolation ||
               (!OperatingSystem.IsWindows() && nativeError == ErrorResourceUnavailable);
    }

    private static void EnsureDirectoryExistsSafely(string directoryPath)
    {
        var fullDirectoryPath = NormalizeDirectoryPath(directoryPath);
        var volumeRoot = Path.GetPathRoot(fullDirectoryPath);
        if (string.IsNullOrWhiteSpace(volumeRoot))
        {
            throw new DirectoryNotFoundException(
                "The attachment directory volume root could not be resolved.");
        }

        var normalizedVolumeRoot = NormalizeDirectoryPath(volumeRoot);
        if (!Directory.Exists(normalizedVolumeRoot))
        {
            throw new DirectoryNotFoundException(
                "The attachment directory volume root does not exist.");
        }

        var currentPath = normalizedVolumeRoot;
        var relativePath = Path.GetRelativePath(
            normalizedVolumeRoot,
            fullDirectoryPath);
        foreach (var segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(segment, ".", StringComparison.Ordinal))
                continue;

            var nextPath = Path.Combine(currentPath, segment);
            if (Directory.Exists(nextPath))
            {
                ThrowIfReparsePoint(nextPath);
                currentPath = nextPath;
                continue;
            }

            using (var ancestorLease = AcquirePathMutationLease(
                       normalizedVolumeRoot,
                       Path.Combine(
                           currentPath,
                           $".attachment-directory-create-{Guid.NewGuid():N}")))
            {
                ancestorLease.Validate();
                BeforeDirectoryCreateForTesting?.Invoke(nextPath);
                ancestorLease.Validate();
                if (!Directory.Exists(nextPath))
                    Directory.CreateDirectory(nextPath);
                ancestorLease.Validate();
                ThrowIfReparsePoint(nextPath);
            }

            // Open the newly observed directory without Share.Delete before
            // proceeding to any descendant. A concurrently inserted junction
            // is rejected before it can be traversed by a recursive create.
            using (var createdDirectoryLease = AcquirePathMutationLease(
                       normalizedVolumeRoot,
                       Path.Combine(
                           nextPath,
                           $".attachment-directory-verify-{Guid.NewGuid():N}")))
            {
                createdDirectoryLease.Validate();
            }

            currentPath = nextPath;
        }
    }

    private static string NormalizeDirectoryPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (!string.IsNullOrWhiteSpace(root) &&
            string.Equals(
                fullPath,
                root,
                StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }

        return fullPath.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
    }

    private static PathMutationLease AcquirePathMutationLease(
        string allowedMutationRoot,
        string targetPath)
    {
        var fullRoot = NormalizeDirectoryPath(allowedMutationRoot);
        var fullTargetPath = Path.GetFullPath(targetPath);
        if (!IsPathAtOrBelow(fullRoot, fullTargetPath))
        {
            throw new InvalidOperationException(
                "The attachment mutation target is outside its allowed root.");
        }

        var parentPath = Path.GetDirectoryName(fullTargetPath);
        if (string.IsNullOrWhiteSpace(parentPath) ||
            !Directory.Exists(fullRoot))
        {
            throw new DirectoryNotFoundException(
                "The attachment mutation parent chain is incomplete.");
        }

        var directoryPaths = new List<string> { fullRoot };
        var relativeParent = Path.GetRelativePath(fullRoot, parentPath);
        var currentPath = fullRoot;
        foreach (var segment in relativeParent.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(segment, ".", StringComparison.Ordinal))
                continue;

            currentPath = Path.Combine(currentPath, segment);
            if (!Directory.Exists(currentPath))
            {
                throw new DirectoryNotFoundException(
                    $"The attachment mutation parent directory is missing: {currentPath}");
            }

            directoryPaths.Add(currentPath);
        }

        EnsureExistingPathChainHasNoReparsePoint(
            fullRoot,
            fullTargetPath);
        if (!OperatingSystem.IsWindows())
        {
            return new PathMutationLease(
                fullRoot,
                fullTargetPath,
                []);
        }

        var handles = new List<DirectoryMutationHandle>(
            directoryPaths.Count);
        try
        {
            foreach (var directoryPath in directoryPaths)
            {
                ThrowIfReparsePoint(directoryPath);
                var handle = CreateFile(
                    directoryPath,
                    FileReadAttributes,
                    FileShare.Read | FileShare.Write,
                    IntPtr.Zero,
                    OpenExisting,
                    FileFlagOpenReparsePoint | FileFlagBackupSemantics,
                    IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    var error = Marshal.GetLastPInvokeError();
                    handle.Dispose();
                    throw new IOException(
                        $"Could not lock the attachment parent directory. win32={error}");
                }

                if (!TryReadDirectoryHandleIdentity(
                        handle,
                        directoryPath,
                        out var identity))
                {
                    handle.Dispose();
                    throw new IOException(
                        "The attachment parent directory identity could not be verified.");
                }

                handles.Add(new DirectoryMutationHandle(
                    handle,
                    identity));
            }

            var lease = new PathMutationLease(
                fullRoot,
                fullTargetPath,
                handles);
            lease.Validate();
            return lease;
        }
        catch
        {
            foreach (var handle in handles)
                handle.Handle.Dispose();
            throw;
        }
    }

    private static bool TryReadDirectoryHandleIdentity(
        SafeFileHandle handle,
        string expectedPath,
        out DirectoryHandleIdentity identity)
    {
        identity = default;
        if (!GetFileInformationByHandle(handle, out var information) ||
            (information.FileAttributes & FileAttributes.ReparsePoint) != 0 ||
            (information.FileAttributes & FileAttributes.Directory) == 0)
        {
            return false;
        }

        var finalPath = TryGetFinalPathByHandle(handle);
        if (string.IsNullOrWhiteSpace(finalPath) ||
            !string.Equals(
                Path.GetFullPath(expectedPath)
                    .TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar),
                finalPath.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        identity = new DirectoryHandleIdentity(
            finalPath,
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) |
            information.FileIndexLow);
        return true;
    }

    private static string? TryGetFinalPathByHandle(
        SafeFileHandle handle)
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
                return null;
            if (length < (uint)buffer.Capacity)
                return NormalizeWindowsHandlePath(buffer.ToString());

            capacity = checked((int)length + 1);
        }

        return null;
    }

    private static string NormalizeWindowsHandlePath(string path)
    {
        const string uncPrefix = @"\\?\UNC\";
        const string localPrefix = @"\\?\";
        if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
            return $@"\\{path[uncPrefix.Length..]}";
        if (path.StartsWith(localPrefix, StringComparison.OrdinalIgnoreCase))
            return path[localPrefix.Length..];
        return path;
    }

    private static string ResolveSafeMutationPath(
        string allowedMutationRoot,
        string path,
        string? disallowedMutationRoot = null)
    {
        var fullPath = Path.GetFullPath(path);
        var relativePath = Path.GetRelativePath(allowedMutationRoot, fullPath);
        if (string.Equals(relativePath, ".", StringComparison.Ordinal) ||
            Path.IsPathRooted(relativePath) ||
            string.Equals(relativePath, "..", StringComparison.Ordinal) ||
            relativePath.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal) ||
            relativePath.StartsWith(
                $"..{Path.AltDirectorySeparatorChar}",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "첨부파일 저장 루트 밖의 경로는 변경할 수 없습니다.");
        }

        if (!string.IsNullOrWhiteSpace(disallowedMutationRoot) &&
            IsPathAtOrBelow(disallowedMutationRoot, fullPath))
        {
            throw new InvalidOperationException(
                "첨부파일 저널 자체 또는 하위 경로는 변경할 수 없습니다.");
        }

        EnsureExistingPathChainHasNoReparsePoint(allowedMutationRoot, fullPath);
        return fullPath;
    }

    private static bool IsPathAtOrBelow(string root, string candidate)
    {
        var fullRoot = NormalizeDirectoryPath(root);
        var fullCandidate = Path.GetFullPath(candidate);
        var relativePath = Path.GetRelativePath(fullRoot, fullCandidate);
        return string.Equals(relativePath, ".", StringComparison.Ordinal) ||
               (!Path.IsPathRooted(relativePath) &&
                !string.Equals(relativePath, "..", StringComparison.Ordinal) &&
                !relativePath.StartsWith(
                    $"..{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal) &&
                !relativePath.StartsWith(
                    $"..{Path.AltDirectorySeparatorChar}",
                    StringComparison.Ordinal));
    }

    private static void EnsureExistingPathChainHasNoReparsePoint(
        string allowedMutationRoot,
        string fullPath)
    {
        ThrowIfReparsePoint(allowedMutationRoot);

        var parentPath = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(parentPath))
            throw new InvalidOperationException("첨부파일 대상 폴더를 확인할 수 없습니다.");

        var relativeParent = Path.GetRelativePath(allowedMutationRoot, parentPath);
        var currentPath = allowedMutationRoot;
        foreach (var segment in relativeParent.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, segment);
            if (!Directory.Exists(currentPath))
                break;

            ThrowIfReparsePoint(currentPath);
        }

        if (File.Exists(fullPath))
            ThrowIfReparsePoint(fullPath);
    }

    private static void ThrowIfReparsePoint(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return;

        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "재분석 지점을 통과하는 첨부파일 경로는 변경할 수 없습니다.");
        }
    }

    private void CleanupJournalBestEffort()
    {
        try
        {
            foreach (var write in _writes)
                TryDeleteJournalFileBestEffort(write.StagedPath);

            TryDeleteJournalFileBestEffort(Path.Combine(
                _journalDirectory,
                RecoveryManifestFileName));
            TryDeleteJournalFileBestEffort(Path.Combine(
                _journalDirectory,
                RecoveryManifestTemporaryFileName));
            CleanupEmptyJournalDirectoryBestEffort();
        }
        catch
        {
            // The DB is already committed. A leftover private journal copy is
            // safer than surfacing a false save failure or deleting live data.
        }
    }

    private void ReleaseActiveLeaseBestEffort()
    {
        try
        {
            _activeLease?.Dispose();
        }
        catch
        {
            // Recovery in a later process can reclaim an unlocked lease file.
        }
        finally
        {
            _activeLease = null;
        }

        TryDeleteJournalFileBestEffort(Path.Combine(
            _journalDirectory,
            ActiveLeaseFileName));
    }

    private void ReleaseRootMutationLeaseBestEffort()
    {
        try
        {
            _rootMutationLease?.Dispose();
        }
        catch
        {
            // The OS releases the lease when this process exits.
        }
        finally
        {
            _rootMutationLease = null;
        }
    }

    private void CleanupEmptyJournalDirectoryBestEffort()
    {
        try
        {
            var safeJournalDirectory = ResolveSafeJournalMutationPath(
                _journalDirectory);
            if (Directory.Exists(safeJournalDirectory) &&
                !Directory.EnumerateFileSystemEntries(
                    safeJournalDirectory).Any())
            {
                Directory.Delete(
                    safeJournalDirectory,
                    recursive: false);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private static void TryDeleteFileBestEffort(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Keep the partial staging file for operator recovery.
        }
    }

    private void TryDeleteJournalFileBestEffort(string path)
    {
        try
        {
            var safePath = ResolveSafeJournalMutationPath(path);
            using var mutationLease = AcquirePathMutationLease(
                _journalDirectory,
                safePath);
            mutationLease.Validate();
            if (File.Exists(safePath))
                File.Delete(safePath);
            mutationLease.Validate();
        }
        catch
        {
            // Keep ambiguous private journal content for operator recovery.
        }
    }

    private static bool FileMatchesContent(
        string path,
        ReadOnlySpan<byte> content)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            if (fileInfo.Length != content.Length)
                return false;

            using var stream = File.OpenRead(path);
            var existingHash = SHA256.HashData(stream);
            var incomingHash = SHA256.HashData(content);
            return CryptographicOperations.FixedTimeEquals(
                existingHash,
                incomingHash);
        }
        catch
        {
            return false;
        }
    }

    private void ThrowIfFinalized()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_preservedForRecovery)
            throw new InvalidOperationException("복구 대기 중인 첨부파일 저널입니다.");
        if (_completed)
            throw new InvalidOperationException("이미 완료된 첨부파일 저널입니다.");
        if (_rolledBack)
            throw new InvalidOperationException("이미 롤백된 첨부파일 저널입니다.");
    }

    private void ThrowIfMutationClosed()
    {
        ThrowIfFinalized();
        if (_promoted)
        {
            throw new InvalidOperationException(
                "승격된 첨부파일 저널에는 작업을 추가할 수 없습니다.");
        }
    }

    private bool TryResolveSafeMutationPathForFinalization(
        string path,
        out string safePath)
    {
        try
        {
            safePath = ResolveSafeMutationPath(path);
            return true;
        }
        catch
        {
            // A database commit may already be durable. Never follow a path that
            // became ambiguous; preserve the file and journal for recovery.
            safePath = string.Empty;
            return false;
        }
    }

    private void PersistRecoveryManifest()
    {
        if (_writes.Count == 0 && _deletes.Count == 0)
            return;

        var manifestPath = ResolveSafeJournalMutationPath(Path.Combine(
            _journalDirectory,
            RecoveryManifestFileName));
        var temporaryPath = ResolveSafeJournalMutationPath(Path.Combine(
            _journalDirectory,
            RecoveryManifestTemporaryFileName));
        var manifest = new RecoveryManifest
        {
            Writes = _writes
                .Select(write => new RecoveryWrite
                {
                    DestinationPath = write.DestinationPath,
                    Length = write.Length,
                    Sha256 = write.Sha256,
                    VolumeSerialNumber = write.VolumeSerialNumber,
                    FileId = write.FileId
                })
                .ToList(),
            Deletes = _deletes
                .Select(delete => new RecoveryDelete
                {
                    OriginalPath = delete.OriginalPath,
                    Length = delete.Length,
                    Sha256 = delete.Sha256,
                    VolumeSerialNumber = delete.VolumeSerialNumber,
                    FileId = delete.FileId
                })
                .ToList()
        };
        var payload = JsonSerializer.SerializeToUtf8Bytes(manifest);

        using var manifestMutationLease = AcquirePathMutationLease(
            _journalDirectory,
            manifestPath);
        manifestMutationLease.Validate();
        TryDeleteJournalFileBestEffort(temporaryPath);
        temporaryPath = ResolveSafeJournalMutationPath(temporaryPath);
        manifestMutationLease.Validate();
        FileSnapshot temporaryIdentity;
        using (var stream = new FileStream(
                   temporaryPath,
                   FileMode.CreateNew,
                   FileAccess.ReadWrite,
                   FileShare.Read | FileShare.Write | FileShare.Delete,
                   bufferSize: 4096,
                   FileOptions.WriteThrough))
        {
            stream.Write(payload);
            stream.Flush(flushToDisk: true);
            if (!TryGetFileSnapshot(
                    stream.SafeFileHandle,
                    out temporaryIdentity))
            {
                throw new IOException(
                    "The attachment recovery manifest identity could not be established.");
            }

            BeforeManifestMoveForTesting?.Invoke(temporaryPath);
            manifestMutationLease.Validate();

            temporaryPath = ResolveSafeJournalMutationPath(temporaryPath);
            manifestPath = ResolveSafeJournalMutationPath(manifestPath);
            File.Move(temporaryPath, manifestPath);

            if (!TryGetFileSnapshot(
                    stream.SafeFileHandle,
                    out var movedHandleIdentity) ||
                movedHandleIdentity.Length != temporaryIdentity.Length ||
                !string.Equals(
                    movedHandleIdentity.Sha256,
                    temporaryIdentity.Sha256,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    movedHandleIdentity.VolumeSerialNumber,
                    temporaryIdentity.VolumeSerialNumber,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    movedHandleIdentity.FileId,
                    temporaryIdentity.FileId,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(
                    "The attachment recovery manifest changed during its atomic move.");
            }
        }

        var movedIdentity = TryReadFileIdentity(manifestPath);
        if (!movedIdentity.IsComplete ||
            movedIdentity.Length != temporaryIdentity.Length ||
            !string.Equals(
                movedIdentity.Sha256,
                temporaryIdentity.Sha256,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                movedIdentity.VolumeSerialNumber,
                temporaryIdentity.VolumeSerialNumber,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                movedIdentity.FileId,
                temporaryIdentity.FileId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(
                "The attachment recovery manifest path no longer identifies the moved file.");
        }

        manifestMutationLease.Validate();
    }

    private static void RecoverJournalDirectory(
        string journalDirectory,
        string journalRoot,
        string allowedMutationRoot,
        IReadOnlySet<string> referencedPaths)
    {
        var manifestPath = Path.Combine(
            journalDirectory,
            RecoveryManifestFileName);
        using var journalMutationLease = AcquirePathMutationLease(
            journalRoot,
            manifestPath);
        BeforePathMutationForTesting?.Invoke(journalDirectory);
        journalMutationLease.Validate();
        var canFinalize = true;

        if (File.Exists(manifestPath))
        {
            if ((File.GetAttributes(manifestPath) & FileAttributes.ReparsePoint) != 0)
                return;

            RecoveryManifest? manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<RecoveryManifest>(
                    File.ReadAllBytes(manifestPath));
            }
            catch
            {
                return;
            }

            // Version 1 did not record a Windows file ID. Length and SHA alone
            // cannot distinguish a replaced file with identical bytes, so old
            // manifests are intentionally retained for manual review.
            if (manifest?.Version != 2 ||
                manifest.Writes is null ||
                manifest.Deletes is null)
                return;

            foreach (var write in manifest.Writes)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(write.DestinationPath) ||
                        string.IsNullOrWhiteSpace(write.Sha256))
                    {
                        canFinalize = false;
                        continue;
                    }

                    var destinationPath = ResolveSafeMutationPath(
                        allowedMutationRoot,
                        write.DestinationPath,
                        Path.GetDirectoryName(journalDirectory));
                    if (referencedPaths.Contains(destinationPath) ||
                        !File.Exists(destinationPath))
                    {
                        continue;
                    }

                    var deleteResult = TryDeleteMatchingFile(
                        allowedMutationRoot,
                        destinationPath,
                        write.Length,
                        write.Sha256,
                        write.VolumeSerialNumber,
                        write.FileId);
                    if (deleteResult is not IdentityDeleteResult.Deleted and
                        not IdentityDeleteResult.Missing)
                    {
                        canFinalize = false;
                    }
                }
                catch
                {
                    canFinalize = false;
                }
            }

            foreach (var delete in manifest.Deletes)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(delete.OriginalPath))
                    {
                        canFinalize = false;
                        continue;
                    }

                    var originalPath = ResolveSafeMutationPath(
                        allowedMutationRoot,
                        delete.OriginalPath,
                        Path.GetDirectoryName(journalDirectory));
                    if (referencedPaths.Contains(originalPath) ||
                        !File.Exists(originalPath))
                    {
                        continue;
                    }

                    var deleteResult = TryDeleteMatchingFile(
                        allowedMutationRoot,
                        originalPath,
                        delete.Length,
                        delete.Sha256,
                        delete.VolumeSerialNumber,
                        delete.FileId);
                    if (deleteResult is not IdentityDeleteResult.Deleted and
                        not IdentityDeleteResult.Missing)
                    {
                        canFinalize = false;
                    }
                }
                catch
                {
                    canFinalize = false;
                }
            }
        }

        foreach (var stagedPath in Directory.EnumerateFiles(
                     journalDirectory,
                     "*.stage",
                     SearchOption.TopDirectoryOnly))
        {
            try
            {
                journalMutationLease.Validate();
                if ((File.GetAttributes(stagedPath) & FileAttributes.ReparsePoint) == 0)
                    File.Delete(stagedPath);
                else
                    canFinalize = false;
            }
            catch
            {
                canFinalize = false;
            }
        }

        if (!canFinalize)
            return;

        journalMutationLease.Validate();
        TryDeleteFileBestEffort(manifestPath);
        TryDeleteFileBestEffort(Path.Combine(
            journalDirectory,
            RecoveryManifestTemporaryFileName));
        try
        {
            if (!Directory.EnumerateFileSystemEntries(journalDirectory).Any())
                Directory.Delete(journalDirectory, recursive: false);
        }
        catch
        {
            // A later recovery pass can retry private journal cleanup.
        }
    }

    private static IdentityDeleteResult TryDeleteMatchingFile(
        string allowedMutationRoot,
        string path,
        long expectedLength,
        string expectedSha256,
        string expectedVolumeSerialNumber,
        string expectedFileId)
    {
        if (expectedLength < 0 ||
            expectedSha256.Length != 64 ||
            !expectedSha256.All(Uri.IsHexDigit) ||
            expectedVolumeSerialNumber.Length != 8 ||
            !expectedVolumeSerialNumber.All(Uri.IsHexDigit) ||
            expectedFileId.Length != 16 ||
            !expectedFileId.All(Uri.IsHexDigit))
        {
            return IdentityDeleteResult.IdentityMismatch;
        }

        SafeFileHandle? handle = null;
        try
        {
            using var pathMutationLease = AcquirePathMutationLease(
                allowedMutationRoot,
                path);
            path = ResolveSafeMutationPath(
                allowedMutationRoot,
                path);
            BeforePathMutationForTesting?.Invoke(path);
            pathMutationLease.Validate();
            handle = OpenStableFileHandle(path, requireDeleteAccess: true);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                return error is ErrorFileNotFound or ErrorPathNotFound
                    ? IdentityDeleteResult.Missing
                    : IdentityDeleteResult.Failed;
            }

            if (!TryGetFileSnapshot(handle, out var snapshot) ||
                snapshot.Length != expectedLength ||
                !string.Equals(
                    snapshot.Sha256,
                    expectedSha256,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    snapshot.VolumeSerialNumber,
                    expectedVolumeSerialNumber,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    snapshot.FileId,
                    expectedFileId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return IdentityDeleteResult.IdentityMismatch;
            }

            pathMutationLease.Validate();
            var disposition = new FileDispositionInfo
            {
                DeleteFile = true
            };
            if (!SetFileInformationByHandle(
                    handle,
                    FileInformationClass.FileDispositionInfo,
                    ref disposition,
                    (uint)Marshal.SizeOf<FileDispositionInfo>()))
            {
                return IdentityDeleteResult.Failed;
            }

            return IdentityDeleteResult.Deleted;
        }
        catch
        {
            return IdentityDeleteResult.Failed;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    private static FileSnapshot TryReadFileIdentity(string path)
    {
        SafeFileHandle? handle = null;
        try
        {
            handle = OpenStableFileHandle(path, requireDeleteAccess: false);
            return !handle.IsInvalid &&
                   TryGetFileSnapshot(handle, out var snapshot)
                ? snapshot
                : FileSnapshot.Empty;
        }
        catch
        {
            return FileSnapshot.Empty;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    private static bool FileSnapshotsMatch(
        FileSnapshot left,
        FileSnapshot right)
        => left.IsComplete &&
           right.IsComplete &&
           left.Length == right.Length &&
           string.Equals(
               left.Sha256,
               right.Sha256,
               StringComparison.OrdinalIgnoreCase) &&
           string.Equals(
               left.VolumeSerialNumber,
               right.VolumeSerialNumber,
               StringComparison.OrdinalIgnoreCase) &&
           string.Equals(
               left.FileId,
               right.FileId,
               StringComparison.OrdinalIgnoreCase);

    private static bool FileSnapshotContentMatches(
        FileSnapshot left,
        FileSnapshot right)
        => left.IsComplete &&
           right.IsComplete &&
           left.Length == right.Length &&
           string.Equals(
               left.Sha256,
               right.Sha256,
               StringComparison.OrdinalIgnoreCase);

    private static SafeFileHandle OpenStableFileHandle(
        string path,
        bool requireDeleteAccess)
    {
        var desiredAccess = GenericRead | FileReadAttributes;
        if (requireDeleteAccess)
            desiredAccess |= DeleteAccess;

        return CreateFile(
            path,
            desiredAccess,
            FileShare.Read | FileShare.Delete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint,
            IntPtr.Zero);
    }

    private static bool TryGetFileSnapshot(
        SafeFileHandle handle,
        out FileSnapshot snapshot)
    {
        snapshot = FileSnapshot.Empty;
        if (!GetFileInformationByHandle(handle, out var fileInformation) ||
            (fileInformation.FileAttributes & FileAttributes.ReparsePoint) != 0)
        {
            return false;
        }

        try
        {
            var length = RandomAccess.GetLength(handle);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long offset = 0;
            while (offset < length)
            {
                var count = RandomAccess.Read(handle, buffer, offset);
                if (count == 0)
                    return false;

                hasher.AppendData(buffer, 0, count);
                offset += count;
            }

            if (RandomAccess.GetLength(handle) != length)
                return false;

            var fileId =
                ((ulong)fileInformation.FileIndexHigh << 32) |
                fileInformation.FileIndexLow;
            snapshot = new FileSnapshot(
                length,
                Convert.ToHexString(hasher.GetHashAndReset()),
                fileInformation.VolumeSerialNumber.ToString("X8"),
                fileId.ToString("X16"));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private sealed class PathMutationLease(
        string allowedMutationRoot,
        string targetPath,
        IReadOnlyList<DirectoryMutationHandle> handles)
        : IDisposable
    {
        private bool _disposed;

        public void Validate()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureExistingPathChainHasNoReparsePoint(
                allowedMutationRoot,
                targetPath);

            foreach (var handle in handles)
            {
                if (!TryReadDirectoryHandleIdentity(
                        handle.Handle,
                        handle.Identity.FinalPath,
                        out var current) ||
                    current.VolumeSerialNumber !=
                    handle.Identity.VolumeSerialNumber ||
                    current.FileId != handle.Identity.FileId)
                {
                    throw new IOException(
                        "An attachment parent directory changed during the file mutation.");
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            foreach (var handle in handles.Reverse())
                handle.Handle.Dispose();
        }
    }

    private sealed record DirectoryMutationHandle(
        SafeFileHandle Handle,
        DirectoryHandleIdentity Identity);

    private sealed class CompositeMutationLease(
        IDisposable fileLease,
        IDisposable pathLease)
        : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            try
            {
                fileLease.Dispose();
            }
            finally
            {
                pathLease.Dispose();
            }
        }
    }

    private readonly record struct DirectoryHandleIdentity(
        string FinalPath,
        uint VolumeSerialNumber,
        ulong FileId);

    private sealed class WriteOperation(
        string destinationPath,
        string stagedPath,
        long length,
        string sha256,
        string volumeSerialNumber,
        string fileId)
    {
        public string DestinationPath { get; } = destinationPath;

        public string StagedPath { get; } = stagedPath;

        public long Length { get; } = length;

        public string Sha256 { get; } = sha256;

        public string VolumeSerialNumber { get; } = volumeSerialNumber;

        public string FileId { get; } = fileId;

        public bool WasPromoted { get; set; }
    }

    private sealed class DeleteOperation(
        string originalPath,
        long length,
        string sha256,
        string volumeSerialNumber,
        string fileId)
    {
        public string OriginalPath { get; } = originalPath;

        public long Length { get; } = length;

        public string Sha256 { get; } = sha256;

        public string VolumeSerialNumber { get; } = volumeSerialNumber;

        public string FileId { get; } = fileId;

    }

    private sealed class RecoveryManifest
    {
        public int Version { get; set; } = 2;

        public List<RecoveryWrite> Writes { get; set; } = [];

        public List<RecoveryDelete> Deletes { get; set; } = [];
    }

    private sealed class RecoveryWrite
    {
        public string DestinationPath { get; set; } = string.Empty;

        public long Length { get; set; }

        public string Sha256 { get; set; } = string.Empty;

        public string VolumeSerialNumber { get; set; } = string.Empty;

        public string FileId { get; set; } = string.Empty;
    }

    private sealed class RecoveryDelete
    {
        public string OriginalPath { get; set; } = string.Empty;

        public long Length { get; set; }

        public string Sha256 { get; set; } = string.Empty;

        public string VolumeSerialNumber { get; set; } = string.Empty;

        public string FileId { get; set; } = string.Empty;
    }

    private readonly record struct FileSnapshot(
        long Length,
        string Sha256,
        string VolumeSerialNumber,
        string FileId)
    {
        public static FileSnapshot Empty { get; } =
            new(-1, string.Empty, string.Empty, string.Empty);

        public bool IsComplete =>
            Length >= 0 &&
            Sha256.Length == 64 &&
            VolumeSerialNumber.Length == 8 &&
            FileId.Length == 16;
    }

    private enum IdentityDeleteResult
    {
        Deleted,
        Missing,
        IdentityMismatch,
        Failed
    }

    private const uint GenericRead = 0x80000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorResourceUnavailable = 11;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public FileAttributes FileAttributes;
        public NativeFileTime CreationTime;
        public NativeFileTime LastAccessTime;
        public NativeFileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfo
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool DeleteFile;
    }

    private enum FileInformationClass
    {
        FileDispositionInfo = 4
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFinalPathNameByHandleW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        FileInformationClass fileInformationClass,
        ref FileDispositionInfo fileInformation,
        uint bufferSize);
}
