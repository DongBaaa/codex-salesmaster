using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using 거래플랜.Server.Api.Domain;
using Microsoft.Extensions.Options;
using Microsoft.Win32.SafeHandles;

namespace 거래플랜.Server.Api.Services;

public sealed class StoredFileOrphanRecheckOptions
{
    public const string SectionName = "FileStorage:OrphanRecheck";
    public static readonly TimeSpan DefaultInitialDelay = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(6);
    public static readonly TimeSpan DefaultMinimumCandidateAge = TimeSpan.FromHours(24);
    public static readonly TimeSpan DefaultMaximumCycleDuration = TimeSpan.FromMinutes(2);
    public const int DefaultBatchSize = 128;
    public const int DefaultMaximumBatchesPerCycle = 8;

    public TimeSpan InitialDelay { get; set; } = DefaultInitialDelay;
    public TimeSpan Interval { get; set; } = DefaultInterval;
    public TimeSpan MinimumCandidateAge { get; set; } = DefaultMinimumCandidateAge;
    public TimeSpan MaximumCycleDuration { get; set; } = DefaultMaximumCycleDuration;
    public int BatchSize { get; set; } = DefaultBatchSize;
    public int MaximumBatchesPerCycle { get; set; } = DefaultMaximumBatchesPerCycle;
    public bool EnableBroadSweep { get; set; }
}

public static class StoredFileOrphanCandidateEnumerator
{
    private const int MaximumBatchSize = 512;
    private static readonly HashSet<string> ManagedStorageAreas =
        new(StringComparer.OrdinalIgnoreCase)
    {
        "customer-contracts",
        "transaction-attachments",
        "payment-attachments"
    };
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    private static readonly EnumerationOptions SafeSingleDirectoryEnumerationOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        ReturnSpecialDirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    public static IEnumerable<IReadOnlyList<string>> EnumerateBatches(
        string storageRoot,
        int requestedBatchSize,
        DateTime? lastWriteCutoffUtc = null,
        CancellationToken cancellationToken = default,
        int skipCandidateCount = 0,
        int maximumCandidateCount = int.MaxValue)
    {
        if (string.IsNullOrWhiteSpace(storageRoot))
            throw new InvalidOperationException("The central file storage root is unavailable.");

        var rootPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(storageRoot));
        if (!Directory.Exists(rootPath))
            yield break;

        if ((File.GetAttributes(rootPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The central file storage root cannot be a reparse point.");

        var batchSize = Math.Clamp(requestedBatchSize, 1, MaximumBatchSize);
        var candidatesToSkip = Math.Max(0, skipCandidateCount);
        var candidatesToTake = Math.Max(0, maximumCandidateCount);
        if (candidatesToTake == 0)
            yield break;

        var rootPrefix = rootPath + Path.DirectorySeparatorChar;
        var batch = new List<string>(batchSize);
        var skippedCandidateCount = 0;
        var yieldedCandidateCount = 0;

        foreach (var enumeratedPath in EnumerateSafeFiles(
                     rootPath,
                     cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(enumeratedPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The entry changed or became inaccessible during enumeration.
                // A later cycle can safely retry it.
                continue;
            }

            var fileName = Path.GetFileName(enumeratedPath);
            if (!IsEligibleCandidate(fileName, attributes))
                continue;

            if (lastWriteCutoffUtc.HasValue)
            {
                DateTime lastWriteUtc;
                try
                {
                    lastWriteUtc = File.GetLastWriteTimeUtc(enumeratedPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if (lastWriteUtc >= lastWriteCutoffUtc.Value)
                    continue;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(enumeratedPath);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (!fullPath.StartsWith(rootPrefix, PathComparison))
                continue;

            if (!IsManagedStoredPath(rootPath, fullPath))
                continue;

            if (skippedCandidateCount < candidatesToSkip)
            {
                skippedCandidateCount++;
                continue;
            }

            if (yieldedCandidateCount >= candidatesToTake)
                yield break;

            batch.Add(fullPath);
            yieldedCandidateCount++;
            if (batch.Count < batchSize)
                continue;

            yield return batch.ToArray();
            batch.Clear();
        }

        if (batch.Count > 0)
            yield return batch.ToArray();
    }

    private static IEnumerable<string> EnumerateSafeFiles(
        string rootPath,
        CancellationToken cancellationToken)
    {
        foreach (var area in ManagedStorageAreas)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var areaPath = Path.Combine(rootPath, area);
            if (!IsSafeTraversalDirectory(areaPath))
                continue;

            foreach (var ownerPath in Directory.EnumerateDirectories(
                         areaPath,
                         "*",
                         SafeSingleDirectoryEnumerationOptions))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsSafeTraversalDirectory(areaPath) ||
                    !IsSafeTraversalDirectory(ownerPath) ||
                    !LooksLikeManagedOwnerName(
                        Path.GetFileName(ownerPath)))
                {
                    continue;
                }

                foreach (var filePath in Directory.EnumerateFiles(
                             ownerPath,
                             "*",
                             SafeSingleDirectoryEnumerationOptions))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Recheck both ancestors immediately after enumeration.
                    // CentralFileStorage performs the same check again before
                    // any physical delete.
                    if (!IsSafeTraversalDirectory(areaPath) ||
                        !IsSafeTraversalDirectory(ownerPath))
                    {
                        break;
                    }

                    yield return filePath;
                }
            }
        }
    }

    public static bool IsEligibleCandidate(
        string? fileName,
        FileAttributes attributes)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            (attributes & (FileAttributes.Directory |
                           FileAttributes.ReparsePoint |
                           FileAttributes.Hidden |
                           FileAttributes.System |
                           FileAttributes.Temporary)) != 0)
        {
            return false;
        }

        return LooksLikeManagedStoredFileName(fileName.Trim());
    }

    public static bool IsManagedStoredPath(
        string storageRoot,
        string candidatePath)
    {
        if (string.IsNullOrWhiteSpace(storageRoot) ||
            string.IsNullOrWhiteSpace(candidatePath))
        {
            return false;
        }

        try
        {
            var rootPath = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(storageRoot));
            var fullPath = Path.GetFullPath(candidatePath);
            var rootPrefix = rootPath + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(rootPrefix, PathComparison))
                return false;

            var segments = Path.GetRelativePath(rootPath, fullPath).Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
            return segments.Length == 3 &&
                   ManagedStorageAreas.Contains(segments[0]) &&
                   LooksLikeManagedOwnerName(segments[1]) &&
                   LooksLikeManagedStoredFileName(segments[2]);
        }
        catch (Exception ex) when (ex is ArgumentException or
                                   NotSupportedException or
                                   PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsSafeTraversalDirectory(string directoryPath)
    {
        try
        {
            var attributes = File.GetAttributes(directoryPath);
            return (attributes & FileAttributes.Directory) != 0 &&
                   (attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception ex) when (ex is IOException or
                                   UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool LooksLikeManagedStoredFileName(string fileName)
    {
        const int idLength = 32;
        if (fileName.Length <= idLength + 2 ||
            fileName[idLength] != '_' ||
            fileName[idLength + 1] != '_')
        {
            return false;
        }

        return fileName.AsSpan(0, idLength).IndexOfAnyExcept(
            "0123456789abcdefABCDEF") < 0;
    }

    private static bool LooksLikeManagedOwnerName(string ownerName)
    {
        const int entityIdLength = 32;
        if (ownerName.Length == entityIdLength)
        {
            return ownerName.AsSpan().IndexOfAnyExcept(
                "0123456789abcdefABCDEF") < 0;
        }

        const int databaseHashLength = 64;
        const int databaseOwnerLength =
            3 + databaseHashLength + 1 + entityIdLength;
        if (ownerName.Length != databaseOwnerLength ||
            !ownerName.StartsWith("db-", StringComparison.OrdinalIgnoreCase) ||
            ownerName[3 + databaseHashLength] != '_')
        {
            return false;
        }

        return ownerName.AsSpan(3, databaseHashLength).IndexOfAnyExcept(
                   "0123456789abcdefABCDEF") < 0 &&
               ownerName.AsSpan(
                       3 + databaseHashLength + 1,
                       entityIdLength)
                   .IndexOfAnyExcept(
                       "0123456789abcdefABCDEF") < 0;
    }
}

public interface IStoredFileDeferredDeletionQueue
{
    IStoredFileDeferredDeletionPreparation PrepareForDatabaseCommit(
        IEnumerable<string> candidatePaths);

    void Enqueue(IEnumerable<string> candidatePaths);

    IReadOnlyList<string> TakeBatch(int maximumCount);

    void AcknowledgeCompleted(IEnumerable<string> candidatePaths);
}

public interface IStoredFileDeferredDeletionPreparation : IDisposable
{
    void MarkDatabaseCommitCompleted();
}

internal static class StoredFileDirectoryDurability
{
    private const uint GenericWrite = 0x40000000;
    private const uint ShareReadWriteDelete = 0x00000007;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    internal static void Flush(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
            throw new ArgumentException("Directory path is required.", nameof(directoryPath));

        var fullPath = Path.GetFullPath(directoryPath);
        var attributes = File.GetAttributes(fullPath);
        if ((attributes & FileAttributes.Directory) == 0 ||
            (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException(
                "Stored-file coordination path is not a safe directory.");
        }

        if (OperatingSystem.IsWindows())
        {
            FlushWindowsDirectory(fullPath);
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            using var handle = File.OpenHandle(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                FileOptions.None);
            RandomAccess.FlushToDisk(handle);
            return;
        }

        throw new PlatformNotSupportedException(
            "Stored-file directory durability is supported only on Windows and Linux.");
    }

    private static void FlushWindowsDirectory(string directoryPath)
    {
        using var handle = CreateFileW(
            directoryPath,
            GenericWrite,
            ShareReadWriteDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new IOException(
                $"Stored-file coordination directory open failed. error={Marshal.GetLastWin32Error()}");
        }

        if (!FlushFileBuffers(handle))
        {
            throw new IOException(
                $"Stored-file coordination directory flush failed. error={Marshal.GetLastWin32Error()}");
        }
    }

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(
        SafeFileHandle fileHandle);
}

public sealed class StoredFileDeferredDeletionQueue
    : IStoredFileDeferredDeletionQueue
{
    private const string CoordinationDirectoryName =
        ".stored-file-deletion-queue";
    private const string MarkerExtension = ".pending";
    private const string PreparedMarkerExtension = ".prepared";
    private const int MaximumMarkerLength = 16 * 1024;
    private static readonly StringComparer StoredPathComparer =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
    private static readonly StringComparison StoredPathComparison =
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    private static readonly JsonSerializerOptions MarkerJsonOptions =
        new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    private readonly object _syncRoot = new();
    private readonly ConcurrentDictionary<string, byte> _candidatePaths =
        new(StoredPathComparer);
    private readonly string _storageRoot;
    private readonly string _coordinationDirectory;
    private readonly Action<string> _flushDirectory;
    private bool _coordinationDirectoryAvailable;

    public StoredFileDeferredDeletionQueue(
        ICentralFileStorage fileStorage)
        : this(fileStorage, StoredFileDirectoryDurability.Flush)
    {
    }

    internal StoredFileDeferredDeletionQueue(
        ICentralFileStorage fileStorage,
        Action<string> flushDirectory)
    {
        ArgumentNullException.ThrowIfNull(fileStorage);
        ArgumentNullException.ThrowIfNull(flushDirectory);
        _flushDirectory = flushDirectory;
        _storageRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(fileStorage.RootPath));
        _coordinationDirectory = Path.Combine(
            _storageRoot,
            CoordinationDirectoryName);

        try
        {
            if (!IsExistingNonReparseDirectory(_storageRoot))
                return;

            Directory.CreateDirectory(_coordinationDirectory);
            if (!IsExistingNonReparseDirectory(_coordinationDirectory))
                return;
            _flushDirectory(_storageRoot);

            TryMarkCoordinationDirectoryHidden();
            _coordinationDirectoryAvailable = true;
            ReloadMarkers();
        }
        catch (Exception ex) when (IsSafeFileSystemFailure(ex))
        {
            // Fail closed. A persistence failure must never turn a candidate
            // into an in-memory-only deletion request.
            _coordinationDirectoryAvailable = false;
        }
    }

    public IStoredFileDeferredDeletionPreparation PrepareForDatabaseCommit(
        IEnumerable<string> candidatePaths)
    {
        ArgumentNullException.ThrowIfNull(candidatePaths);

        lock (_syncRoot)
        {
            if (!EnsureCoordinationDirectoryIsSafe())
            {
                throw new InvalidOperationException(
                    "Stored-file deletion coordination is unavailable.");
            }

            var preparationId = Guid.NewGuid();
            var preparedCandidates = new List<PreparedDeletionCandidate>();
            var seenPaths = new HashSet<string>(StoredPathComparer);
            foreach (var candidatePath in candidatePaths)
            {
                if (!TryNormalizeManagedPath(
                        candidatePath,
                        out var fullPath,
                        out var relativePath) ||
                    !seenPaths.Add(fullPath))
                {
                    continue;
                }

                var pendingMarkerPath = GetMarkerPath(relativePath);
                if (MarkerEntryExists(pendingMarkerPath))
                {
                    if (!IsValidMarker(
                            pendingMarkerPath,
                            relativePath,
                            fullPath))
                    {
                        AbortPreparedMarkers(preparedCandidates);
                        throw new InvalidDataException(
                            "A stored-file deletion marker is invalid.");
                    }

                    _candidatePaths.TryAdd(fullPath, 0);
                    continue;
                }

                var preparedMarkerPath = GetPreparedMarkerPath(
                    relativePath,
                    preparationId);
                if (!EnsureDurableMarker(
                        preparedMarkerPath,
                        relativePath,
                        fullPath))
                {
                    AbortPreparedMarkers(preparedCandidates);
                    throw new IOException(
                        "A stored-file deletion preparation could not be persisted.");
                }

                preparedCandidates.Add(new PreparedDeletionCandidate(
                    fullPath,
                    relativePath,
                    preparedMarkerPath));
            }

            return new DatabaseCommitDeletionPreparation(
                this,
                preparedCandidates);
        }
    }

    public void Enqueue(IEnumerable<string> candidatePaths)
    {
        ArgumentNullException.ThrowIfNull(candidatePaths);

        lock (_syncRoot)
        {
            if (!EnsureCoordinationDirectoryIsSafe())
                return;

            foreach (var candidatePath in candidatePaths)
            {
                if (!TryNormalizeManagedPath(
                        candidatePath,
                        out var fullPath,
                        out var relativePath))
                {
                    continue;
                }

                var markerPath = GetMarkerPath(relativePath);
                if (!EnsureDurableMarker(
                        markerPath,
                        relativePath,
                        fullPath))
                {
                    continue;
                }

                _candidatePaths.TryAdd(fullPath, 0);
            }
        }
    }

    public IReadOnlyList<string> TakeBatch(int maximumCount)
    {
        lock (_syncRoot)
        {
            if (!EnsureCoordinationDirectoryIsSafe())
                return [];

            var boundedCount = Math.Clamp(maximumCount, 1, 512);
            var batch = new List<string>(boundedCount);
            foreach (var candidatePath in _candidatePaths.Keys)
            {
                if (!TryNormalizeManagedPath(
                        candidatePath,
                        out var fullPath,
                        out var relativePath) ||
                    !IsValidMarker(
                        GetMarkerPath(relativePath),
                        relativePath,
                        fullPath))
                {
                    _candidatePaths.TryRemove(candidatePath, out _);
                    continue;
                }

                // Taking work is deliberately not an acknowledgement. The
                // durable marker remains the source of truth until a completed
                // reconciliation explicitly acknowledges this exact path.
                batch.Add(fullPath);
                if (batch.Count >= boundedCount)
                    break;
            }

            return batch;
        }
    }

    public void AcknowledgeCompleted(IEnumerable<string> candidatePaths)
    {
        ArgumentNullException.ThrowIfNull(candidatePaths);

        lock (_syncRoot)
        {
            if (!EnsureCoordinationDirectoryIsSafe())
                return;

            foreach (var candidatePath in candidatePaths)
            {
                if (!TryNormalizeManagedPath(
                        candidatePath,
                        out var fullPath,
                        out var relativePath))
                {
                    continue;
                }

                var markerPath = GetMarkerPath(relativePath);
                if (MarkerEntryExists(markerPath) &&
                    !IsValidMarker(
                        markerPath,
                        relativePath,
                        fullPath))
                {
                    // Corrupt, substituted, or reparse markers are preserved
                    // for operator inspection and are never acknowledged.
                    continue;
                }

                try
                {
                    File.Delete(markerPath);
                    if (!File.Exists(markerPath))
                    {
                        _flushDirectory(_coordinationDirectory);
                        _candidatePaths.TryRemove(fullPath, out _);
                    }
                }
                catch (Exception ex) when (IsSafeFileSystemFailure(ex))
                {
                    // Keep both the marker and in-memory entry for retry.
                }
            }
        }
    }

    private void ReloadMarkers()
    {
        foreach (var markerPath in Directory.EnumerateFiles(
                     _coordinationDirectory,
                     $"*{MarkerExtension}",
                     SearchOption.TopDirectoryOnly))
        {
            if (!TryReadMarker(
                    markerPath,
                    out var relativePath,
                    out var fullPath))
            {
                continue;
            }

            if (!string.Equals(
                    markerPath,
                    GetMarkerPath(relativePath),
                    StoredPathComparison))
            {
                continue;
            }

            _candidatePaths.TryAdd(fullPath, 0);
        }

        foreach (var preparedMarkerPath in Directory.EnumerateFiles(
                     _coordinationDirectory,
                     $"*{PreparedMarkerExtension}",
                     SearchOption.TopDirectoryOnly))
        {
            TryRecoverPreparedMarker(preparedMarkerPath);
        }
    }

    private void TryRecoverPreparedMarker(string preparedMarkerPath)
    {
        if (!TryParsePreparedMarkerId(
                preparedMarkerPath,
                out var preparationId) ||
            !TryReadMarker(
                preparedMarkerPath,
                out var relativePath,
                out var fullPath) ||
            !string.Equals(
                preparedMarkerPath,
                GetPreparedMarkerPath(relativePath, preparationId),
                StoredPathComparison))
        {
            return;
        }

        TryPromotePreparedMarker(new PreparedDeletionCandidate(
            fullPath,
            relativePath,
            preparedMarkerPath));
    }

    private void CommitPreparedMarkers(
        IReadOnlyList<PreparedDeletionCandidate> preparedCandidates)
    {
        lock (_syncRoot)
        {
            if (!EnsureCoordinationDirectoryIsSafe())
                return;

            foreach (var preparedCandidate in preparedCandidates)
                TryPromotePreparedMarker(preparedCandidate);
        }
    }

    private void TryPromotePreparedMarker(
        PreparedDeletionCandidate preparedCandidate)
    {
        var pendingMarkerPath = GetMarkerPath(
            preparedCandidate.RelativePath);
        if (MarkerEntryExists(pendingMarkerPath))
        {
            if (!IsValidMarker(
                    pendingMarkerPath,
                    preparedCandidate.RelativePath,
                    preparedCandidate.FullPath))
            {
                return;
            }

            TryDeletePreparedMarker(preparedCandidate);
            _candidatePaths.TryAdd(preparedCandidate.FullPath, 0);
            return;
        }

        if (!IsValidMarkerAtPath(
                preparedCandidate.MarkerPath,
                preparedCandidate.RelativePath,
                preparedCandidate.FullPath))
        {
            return;
        }

        var movedPreparedMarker = false;
        try
        {
            File.Move(
                preparedCandidate.MarkerPath,
                pendingMarkerPath);
            movedPreparedMarker = true;
            _flushDirectory(_coordinationDirectory);
        }
        catch (IOException) when (
            !movedPreparedMarker &&
            File.Exists(pendingMarkerPath))
        {
            // Another preparation committed the same candidate first.
        }
        catch (Exception ex) when (IsSafeFileSystemFailure(ex))
        {
            // Preserve the prepared marker for recovery after restart.
            return;
        }

        if (!IsValidMarker(
                pendingMarkerPath,
                preparedCandidate.RelativePath,
                preparedCandidate.FullPath))
        {
            return;
        }

        TryDeletePreparedMarker(preparedCandidate);
        _candidatePaths.TryAdd(preparedCandidate.FullPath, 0);
    }

    private void AbortPreparedMarkers(
        IReadOnlyList<PreparedDeletionCandidate> preparedCandidates)
    {
        lock (_syncRoot)
        {
            if (!EnsureCoordinationDirectoryIsSafe())
                return;

            foreach (var preparedCandidate in preparedCandidates)
                TryDeletePreparedMarker(preparedCandidate);
        }
    }

    private void TryDeletePreparedMarker(
        PreparedDeletionCandidate preparedCandidate)
    {
        if (!IsValidMarkerAtPath(
                preparedCandidate.MarkerPath,
                preparedCandidate.RelativePath,
                preparedCandidate.FullPath))
        {
            return;
        }

        try
        {
            File.Delete(preparedCandidate.MarkerPath);
            if (!File.Exists(preparedCandidate.MarkerPath))
                _flushDirectory(_coordinationDirectory);
        }
        catch (Exception ex) when (IsSafeFileSystemFailure(ex))
        {
            // Preserve the marker for later inspection or restart recovery.
        }
    }

    private bool EnsureDurableMarker(
        string markerPath,
        string relativePath,
        string fullPath)
    {
        if (File.Exists(markerPath))
        {
            return IsValidMarkerAtPath(
                markerPath,
                relativePath,
                fullPath);
        }

        var markerBytes = JsonSerializer.SerializeToUtf8Bytes(
            new DeferredDeletionMarker(1, relativePath),
            MarkerJsonOptions);
        if (markerBytes.Length > MaximumMarkerLength)
            return false;

        var temporaryPath = Path.Combine(
            _coordinationDirectory,
            $".{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(markerBytes);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temporaryPath, markerPath);
            }
            catch (IOException) when (File.Exists(markerPath))
            {
                File.Delete(temporaryPath);
            }

            _flushDirectory(_coordinationDirectory);

            return IsValidMarkerAtPath(
                markerPath,
                relativePath,
                fullPath);
        }
        catch (Exception ex) when (IsSafeFileSystemFailure(ex))
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception cleanupEx) when (
                IsSafeFileSystemFailure(cleanupEx))
            {
                // A leftover temp file is ignored by marker loading and by
                // broad orphan enumeration.
            }

            return false;
        }
    }

    private bool IsValidMarker(
        string markerPath,
        string expectedRelativePath,
        string expectedFullPath)
        => IsValidMarkerAtPath(
               markerPath,
               expectedRelativePath,
               expectedFullPath) &&
           string.Equals(
               markerPath,
               GetMarkerPath(expectedRelativePath),
               StoredPathComparison);

    private bool IsValidMarkerAtPath(
        string markerPath,
        string expectedRelativePath,
        string expectedFullPath)
        => TryReadMarker(
               markerPath,
               out var relativePath,
               out var fullPath) &&
           string.Equals(
               expectedRelativePath,
               relativePath,
               StringComparison.Ordinal) &&
           StoredPathComparer.Equals(expectedFullPath, fullPath);

    private bool TryReadMarker(
        string markerPath,
        out string relativePath,
        out string fullPath)
    {
        relativePath = string.Empty;
        fullPath = string.Empty;

        try
        {
            if (!EnsureCoordinationDirectoryIsSafe())
                return false;

            var attributes = File.GetAttributes(markerPath);
            if ((attributes & (FileAttributes.Directory |
                               FileAttributes.ReparsePoint)) != 0)
            {
                return false;
            }

            var markerInfo = new FileInfo(markerPath);
            if (markerInfo.Length <= 0 ||
                markerInfo.Length > MaximumMarkerLength)
            {
                return false;
            }

            byte[] bytes;
            using (var stream = new FileStream(
                       markerPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       4096,
                       FileOptions.SequentialScan))
            {
                bytes = new byte[checked((int)stream.Length)];
                stream.ReadExactly(bytes);
            }

            var marker = JsonSerializer.Deserialize<DeferredDeletionMarker>(
                bytes,
                MarkerJsonOptions);
            if (marker is null ||
                marker.Version != 1 ||
                string.IsNullOrWhiteSpace(marker.RelativePath) ||
                Path.IsPathRooted(marker.RelativePath))
            {
                return false;
            }

            var candidatePath = Path.GetFullPath(
                Path.Combine(
                    _storageRoot,
                    marker.RelativePath.Replace(
                        '/',
                        Path.DirectorySeparatorChar)));
            if (!TryNormalizeManagedPath(
                    candidatePath,
                    out fullPath,
                    out relativePath) ||
                !string.Equals(
                    marker.RelativePath,
                    relativePath,
                    StringComparison.Ordinal))
            {
                relativePath = string.Empty;
                fullPath = string.Empty;
                return false;
            }

            return true;
        }
        catch (Exception ex) when (
            IsSafeFileSystemFailure(ex) ||
            ex is JsonException)
        {
            relativePath = string.Empty;
            fullPath = string.Empty;
            return false;
        }
    }

    private bool TryNormalizeManagedPath(
        string? candidatePath,
        out string fullPath,
        out string relativePath)
    {
        fullPath = string.Empty;
        relativePath = string.Empty;
        if (string.IsNullOrWhiteSpace(candidatePath))
            return false;

        try
        {
            fullPath = Path.GetFullPath(candidatePath.Trim());
            if (!StoredFileOrphanCandidateEnumerator.IsManagedStoredPath(
                    _storageRoot,
                    fullPath) ||
                !HasSafeExistingPathChain(fullPath))
            {
                fullPath = string.Empty;
                return false;
            }

            relativePath = Path.GetRelativePath(
                    _storageRoot,
                    fullPath)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
            return !relativePath.StartsWith(
                       "../",
                       StringComparison.Ordinal) &&
                   !string.Equals(
                       relativePath,
                       "..",
                       StringComparison.Ordinal);
        }
        catch (Exception ex) when (IsSafeFileSystemFailure(ex))
        {
            fullPath = string.Empty;
            relativePath = string.Empty;
            return false;
        }
    }

    private bool HasSafeExistingPathChain(string fullPath)
    {
        if (!IsExistingNonReparseDirectory(_storageRoot))
            return false;

        var relativePath = Path.GetRelativePath(_storageRoot, fullPath);
        var currentPath = _storageRoot;
        foreach (var segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, segment);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(currentPath);
            }
            catch (Exception ex) when (
                ex is FileNotFoundException or
                    DirectoryNotFoundException)
            {
                break;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
                return false;
        }

        return true;
    }

    private string GetMarkerPath(string relativePath)
    {
        var hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(relativePath)))
            .ToLowerInvariant();
        return Path.Combine(
            _coordinationDirectory,
            hash + MarkerExtension);
    }

    private string GetPreparedMarkerPath(
        string relativePath,
        Guid preparationId)
    {
        var hash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(relativePath)))
            .ToLowerInvariant();
        return Path.Combine(
            _coordinationDirectory,
            $"{hash}.{preparationId:N}{PreparedMarkerExtension}");
    }

    private static bool TryParsePreparedMarkerId(
        string preparedMarkerPath,
        out Guid preparationId)
    {
        preparationId = Guid.Empty;
        var fileName = Path.GetFileName(preparedMarkerPath);
        if (!fileName.EndsWith(
                PreparedMarkerExtension,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var markerName = fileName[..^PreparedMarkerExtension.Length];
        const int hashLength = 64;
        const int preparationIdLength = 32;
        if (markerName.Length !=
                hashLength + 1 + preparationIdLength ||
            markerName[hashLength] != '.' ||
            markerName.AsSpan(0, hashLength).IndexOfAnyExcept(
                "0123456789abcdefABCDEF") >= 0)
        {
            return false;
        }

        return Guid.TryParseExact(
            markerName.AsSpan(hashLength + 1),
            "N",
            out preparationId);
    }

    private bool MarkerEntryExists(string markerPath)
    {
        if (File.Exists(markerPath) ||
            Directory.Exists(markerPath))
        {
            return true;
        }

        try
        {
            var markerName = Path.GetFileName(markerPath);
            return Directory.EnumerateFileSystemEntries(
                    _coordinationDirectory,
                    markerName,
                    SearchOption.TopDirectoryOnly)
                .Any(path => string.Equals(
                    path,
                    markerPath,
                    StoredPathComparison));
        }
        catch (Exception ex) when (IsSafeFileSystemFailure(ex))
        {
            // An inconclusive lookup must preserve the marker.
            return true;
        }
    }

    private bool EnsureCoordinationDirectoryIsSafe()
        => _coordinationDirectoryAvailable &&
           IsExistingNonReparseDirectory(_storageRoot) &&
           IsExistingNonReparseDirectory(_coordinationDirectory);

    private void TryMarkCoordinationDirectoryHidden()
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            var attributes = File.GetAttributes(_coordinationDirectory);
            if ((attributes & FileAttributes.Hidden) == 0)
            {
                File.SetAttributes(
                    _coordinationDirectory,
                    attributes | FileAttributes.Hidden);
            }
        }
        catch (Exception ex) when (IsSafeFileSystemFailure(ex))
        {
            // The dot-prefixed dedicated directory still keeps coordination
            // files outside all managed storage areas.
        }
    }

    private static bool IsExistingNonReparseDirectory(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.Directory) != 0 &&
                   (attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception ex) when (IsSafeFileSystemFailure(ex))
        {
            return false;
        }
    }

    private static bool IsSafeFileSystemFailure(Exception exception)
        => exception is IOException or
            UnauthorizedAccessException or
            ArgumentException or
            NotSupportedException;

    private readonly record struct PreparedDeletionCandidate(
        string FullPath,
        string RelativePath,
        string MarkerPath);

    private sealed class DatabaseCommitDeletionPreparation(
        StoredFileDeferredDeletionQueue owner,
        IReadOnlyList<PreparedDeletionCandidate> preparedCandidates)
        : IStoredFileDeferredDeletionPreparation
    {
        private int _state;

        public void MarkDatabaseCommitCompleted()
        {
            if (Interlocked.CompareExchange(
                    ref _state,
                    1,
                    0) != 0)
            {
                return;
            }

            owner.CommitPreparedMarkers(preparedCandidates);
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(
                    ref _state,
                    2,
                    0) == 0)
            {
                owner.AbortPreparedMarkers(preparedCandidates);
            }
        }
    }

    private sealed record DeferredDeletionMarker(
        int Version,
        string RelativePath);
}

public interface IStoredFileDeletionLeaseProbe
{
    IDisposable? TryAcquireShared(string storageRoot);
}

public sealed class StoredFileDeletionLeaseProbe
    : IStoredFileDeletionLeaseProbe
{
    public IDisposable? TryAcquireShared(string storageRoot)
        => StoredFileDeletionLease.TryAcquireShared(storageRoot);
}

public sealed class BackupDeferredStoredFileReferenceReconciler(
    StoredFileReferenceReconciler inner,
    ICentralFileStorage fileStorage,
    IStoredFileDeletionLeaseProbe deletionLeaseProbe,
    IStoredFileDeferredDeletionQueue deferredDeletionQueue,
    ILogger<BackupDeferredStoredFileReferenceReconciler> logger)
    : IStoredFileReferenceReconciler
{
    private static readonly StringComparer StoredPathComparer =
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    public async Task DeleteUnreferencedAsync(
        IEnumerable<string> candidatePaths,
        CancellationToken cancellationToken = default)
        => _ = await DeleteUnreferencedWithOutcomeAsync(
            candidatePaths,
            cancellationToken);

    public async Task<StoredFileReconcileOutcome>
        DeleteUnreferencedWithOutcomeAsync(
            IEnumerable<string> candidatePaths,
            CancellationToken cancellationToken = default)
    {
        var paths = candidatePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim())
            .Distinct(StoredPathComparer)
            .ToArray();
        if (paths.Length == 0)
            return StoredFileReconcileOutcome.Completed;

        IDisposable? deletionLease;
        try
        {
            deletionLease = deletionLeaseProbe.TryAcquireShared(
                fileStorage.RootPath);
        }
        catch (Exception ex)
        {
            deferredDeletionQueue.Enqueue(paths);
            logger.LogWarning(
                "Stored-file cleanup was queued after deletion-lease acquisition failed. errorType={ErrorType}",
                ex.GetType().Name);
            return StoredFileReconcileOutcome.LeaseDeferred;
        }

        if (deletionLease is null)
        {
            deferredDeletionQueue.Enqueue(paths);
            logger.LogInformation(
                "Stored-file cleanup was queued because a backup owns the exclusive deletion lease.");
            return StoredFileReconcileOutcome.LeaseDeferred;
        }

        using (deletionLease)
        {
            try
            {
                var outcome =
                    await inner.DeleteUnreferencedWithOutcomeAsync(
                    paths,
                    cancellationToken);
                if (outcome == StoredFileReconcileOutcome.Completed)
                {
                    deferredDeletionQueue.AcknowledgeCompleted(paths);
                }
                else
                {
                    deferredDeletionQueue.Enqueue(paths);
                }

                return outcome;
            }
            catch
            {
                deferredDeletionQueue.Enqueue(paths);
                throw;
            }
        }
    }

    public Task<PaymentAttachment?> FindPaymentAttachmentAsync(
        Guid attachmentId,
        CancellationToken cancellationToken = default)
        => inner.FindPaymentAttachmentAsync(
            attachmentId,
            cancellationToken);
}

public sealed class StoredFileOrphanRecheckService : BackgroundService
{
    private static readonly TimeSpan MinimumInitialDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaximumInitialDelay = TimeSpan.FromHours(24);
    private static readonly TimeSpan MaximumInterval = TimeSpan.FromDays(7);
    private static readonly TimeSpan MinimumCandidateAgeFloor =
        StoredFileOrphanRecheckOptions.DefaultMinimumCandidateAge;
    private static readonly TimeSpan MaximumCandidateAge = TimeSpan.FromDays(30);
    private static readonly TimeSpan MinimumCycleDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumCycleDuration = TimeSpan.FromMinutes(30);
    private const int MaximumBatchesPerCycleLimit = 64;

    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ICentralFileStorage _fileStorage;
    private readonly IStoredFileDeferredDeletionQueue _deferredDeletionQueue;
    private readonly ITenantDatabaseConnectionResolver _connectionResolver;
    private readonly DatabaseInitializationState _databaseInitializationState;
    private readonly ILogger<StoredFileOrphanRecheckService> _logger;
    private readonly DateTime _serviceStartedUtc;
    private readonly TimeSpan _initialDelay;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _minimumCandidateAge;
    private readonly TimeSpan _maximumCycleDuration;
    private readonly int _batchSize;
    private readonly int _maximumBatchesPerCycle;
    private readonly bool _enableBroadSweep;
    private int _nextBroadSweepSkipCount;

    public StoredFileOrphanRecheckService(
        IServiceScopeFactory serviceScopeFactory,
        ICentralFileStorage fileStorage,
        IStoredFileDeferredDeletionQueue deferredDeletionQueue,
        ITenantDatabaseConnectionResolver connectionResolver,
        DatabaseInitializationState databaseInitializationState,
        IOptions<StoredFileOrphanRecheckOptions> options,
        ILogger<StoredFileOrphanRecheckService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _fileStorage = fileStorage;
        _deferredDeletionQueue = deferredDeletionQueue;
        _connectionResolver = connectionResolver;
        _databaseInitializationState = databaseInitializationState;
        _logger = logger;
        _serviceStartedUtc = DateTime.UtcNow;

        var configured = options.Value;
        _initialDelay = configured.InitialDelay < MinimumInitialDelay ||
                        configured.InitialDelay > MaximumInitialDelay
            ? StoredFileOrphanRecheckOptions.DefaultInitialDelay
            : configured.InitialDelay;
        _interval = configured.Interval < MinimumInterval ||
                    configured.Interval > MaximumInterval
            ? StoredFileOrphanRecheckOptions.DefaultInterval
            : configured.Interval;
        _minimumCandidateAge =
            configured.MinimumCandidateAge < MinimumCandidateAgeFloor ||
            configured.MinimumCandidateAge > MaximumCandidateAge
                ? StoredFileOrphanRecheckOptions.DefaultMinimumCandidateAge
                : configured.MinimumCandidateAge;
        _maximumCycleDuration =
            configured.MaximumCycleDuration < MinimumCycleDuration ||
            configured.MaximumCycleDuration > MaximumCycleDuration
                ? StoredFileOrphanRecheckOptions.DefaultMaximumCycleDuration
                : configured.MaximumCycleDuration;
        _batchSize = Math.Clamp(
            configured.BatchSize,
            1,
            512);
        _maximumBatchesPerCycle = Math.Clamp(
            configured.MaximumBatchesPerCycle,
            1,
            MaximumBatchesPerCycleLimit);
        _enableBroadSweep = configured.EnableBroadSweep;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(_initialDelay, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                await TryRecheckOnceAsync(stoppingToken);
                await Task.Delay(_interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
    }

    public async Task<bool> TryRecheckOnceAsync(
        CancellationToken cancellationToken = default)
    {
        using var cycleCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cycleCancellation.CancelAfter(_maximumCycleDuration);
        var cycleToken = cycleCancellation.Token;

        try
        {
            cycleToken.ThrowIfCancellationRequested();
            var initialization = _databaseInitializationState.CreateSnapshot();
            if (!initialization.Completed || initialization.Failed)
            {
                _logger.LogDebug(
                    "Stored-file orphan recheck deferred until database initialization completes.");
                return false;
            }

            EnsureCompleteDatabaseTopology();

            var candidateCount = 0;
            var batchCount = 0;
            while (batchCount < _maximumBatchesPerCycle)
            {
                cycleToken.ThrowIfCancellationRequested();
                var deferredBatch = _deferredDeletionQueue.TakeBatch(
                    _batchSize);
                if (deferredBatch.Count == 0)
                    break;

                try
                {
                    var deletionResult = await DeleteBatchAsync(
                        deferredBatch,
                        cycleToken);
                    if (!deletionResult.Completed)
                    {
                        return false;
                    }

                    if (deletionResult.RequiresQueueAcknowledgement)
                    {
                        _deferredDeletionQueue.AcknowledgeCompleted(
                            deferredBatch);
                    }
                }
                catch
                {
                    _deferredDeletionQueue.Enqueue(deferredBatch);
                    throw;
                }

                candidateCount += deferredBatch.Count;
                batchCount++;
            }

            if (!_enableBroadSweep)
            {
                _logger.LogDebug(
                    "Stored-file deferred deletion recheck completed. candidates={CandidateCount}, batches={BatchCount}",
                    candidateCount,
                    batchCount);
                return true;
            }

            var ageCutoffUtc = DateTime.UtcNow - _minimumCandidateAge;
            // Never sweep files published during this API process lifetime.
            // They become eligible only after a later clean restart and
            // successful database initialization.
            var lastWriteCutoffUtc = ageCutoffUtc < _serviceStartedUtc
                ? ageCutoffUtc
                : _serviceStartedUtc;
            var remainingBatchCount =
                _maximumBatchesPerCycle - batchCount;
            var maximumBroadCandidateCount =
                remainingBatchCount * _batchSize;
            var broadCandidateCount = 0;
            foreach (var batch in StoredFileOrphanCandidateEnumerator
                         .EnumerateBatches(
                             _fileStorage.RootPath,
                             _batchSize,
                             lastWriteCutoffUtc,
                             cycleToken,
                             _nextBroadSweepSkipCount,
                             maximumBroadCandidateCount))
            {
                cycleToken.ThrowIfCancellationRequested();
                var deletionResult = await DeleteBatchAsync(
                    batch,
                    cycleToken);
                if (!deletionResult.Completed)
                {
                    return false;
                }

                candidateCount += batch.Count;
                broadCandidateCount += batch.Count;
                batchCount++;
            }

            _nextBroadSweepSkipCount =
                maximumBroadCandidateCount > 0 &&
                broadCandidateCount >= maximumBroadCandidateCount
                    ? AddBounded(
                        _nextBroadSweepSkipCount,
                        broadCandidateCount)
                    : 0;

            _logger.LogDebug(
                "Stored-file orphan recheck completed. candidates={CandidateCount}, batches={BatchCount}",
                candidateCount,
                batchCount);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (cycleToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Stored-file orphan recheck reached its cycle time limit and will continue later.");
            return false;
        }
        catch (Exception ex)
        {
            // Do not attach the exception object: file-system exceptions can
            // contain sensitive absolute storage paths.
            _logger.LogWarning(
                "Stored-file orphan recheck was deferred after a safe failure. errorType={ErrorType}",
                ex.GetType().Name);
            return false;
        }
    }

    private async Task<BatchDeletionResult> DeleteBatchAsync(
        IReadOnlyList<string> batch,
        CancellationToken cancellationToken)
    {
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var reconciler = scope.ServiceProvider
            .GetRequiredService<IStoredFileReferenceReconciler>();
        if (reconciler is BackupDeferredStoredFileReferenceReconciler
            backupDeferredReconciler)
        {
            var outcome =
                await backupDeferredReconciler
                    .DeleteUnreferencedWithOutcomeAsync(
                        batch,
                        cancellationToken);
            return new BatchDeletionResult(
                Completed:
                    outcome == StoredFileReconcileOutcome.Completed,
                RequiresQueueAcknowledgement: false);
        }

        await reconciler.DeleteUnreferencedAsync(
            batch,
            cancellationToken);
        return new BatchDeletionResult(
            Completed: true,
            RequiresQueueAcknowledgement: true);
    }

    private void EnsureCompleteDatabaseTopology()
    {
        var connections = new List<TenantDatabaseConnectionInfo>
        {
            _connectionResolver.ResolveCentral()
        };
        connections.AddRange(
            _connectionResolver.GetDedicatedBusinessConnections());

        var physicalDatabaseIdentities = new HashSet<string>(
            StringComparer.Ordinal);
        foreach (var connection in connections)
        {
            physicalDatabaseIdentities.Add(
                PhysicalDatabaseIdentity.FromConnectionInfo(connection));
        }

        if (physicalDatabaseIdentities.Count == 0)
            throw new InvalidOperationException(
                "No physical database is available for stored-file reconciliation.");
    }

    private static int AddBounded(int current, int increment)
        => current > int.MaxValue - increment
            ? 0
            : current + increment;

    private readonly record struct BatchDeletionResult(
        bool Completed,
        bool RequiresQueueAcknowledgement);
}
