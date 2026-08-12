using 거래플랜.Server.Api.Services;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class StoredFileDeferredDeletionQueueDurabilityTests
    : IDisposable
{
    private const string ManagedOwner =
        "0123456789abcdef0123456789abcdef";
    private readonly string _rootPath;
    private readonly TestCentralFileStorage _storage;

    public StoredFileDeferredDeletionQueueDurabilityTests()
    {
        _rootPath = Path.Combine(
            Path.GetTempPath(),
            "georaeplan-deferred-deletion-queue-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootPath);
        _storage = new TestCentralFileStorage(_rootPath);
    }

    [Fact]
    public void DirectoryMetadataFlush_SucceedsForExistingSafeDirectory()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            return;

        _ = new StoredFileDeferredDeletionQueue(_storage);

        StoredFileDirectoryDurability.Flush(
            GetCoordinationDirectory());
    }

    [Fact]
    public void PrepareCommitAndAcknowledge_FlushEachDirectoryMetadataMutation()
    {
        var candidate = CreateManagedFile(
            "customer-contracts",
            "directory-flush-sequence.bin");
        var flushedDirectories = new List<string>();
        var queue = new StoredFileDeferredDeletionQueue(
            _storage,
            path => flushedDirectories.Add(Path.GetFullPath(path)));
        Assert.Equal(
            new[] { Path.GetFullPath(_rootPath) },
            flushedDirectories);
        flushedDirectories.Clear();

        using var preparation = queue.PrepareForDatabaseCommit([candidate]);
        Assert.Equal(
            new[] { Path.GetFullPath(GetCoordinationDirectory()) },
            flushedDirectories);

        flushedDirectories.Clear();
        preparation.MarkDatabaseCommitCompleted();
        Assert.Equal(
            new[] { Path.GetFullPath(GetCoordinationDirectory()) },
            flushedDirectories);

        flushedDirectories.Clear();
        queue.AcknowledgeCompleted([candidate]);
        Assert.Equal(
            new[] { Path.GetFullPath(GetCoordinationDirectory()) },
            flushedDirectories);
    }

    [Fact]
    public void PrepareForDatabaseCommit_WhenDirectoryFlushFails_FailsClosedAndRemainsRecoverable()
    {
        var candidate = CreateManagedFile(
            "payment-attachments",
            "directory-flush-failure.bin");
        var coordinationDirectory = Path.GetFullPath(
            GetCoordinationDirectory());
        var queue = new StoredFileDeferredDeletionQueue(
            _storage,
            path =>
            {
                if (string.Equals(
                        Path.GetFullPath(path),
                        coordinationDirectory,
                        OperatingSystem.IsWindows()
                            ? StringComparison.OrdinalIgnoreCase
                            : StringComparison.Ordinal))
                {
                    throw new IOException(
                        "deterministic directory flush failure");
                }
            });

        var exception = Assert.Throws<IOException>(() =>
            queue.PrepareForDatabaseCommit([candidate]));

        Assert.Contains(
            "could not be persisted",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Empty(queue.TakeBatch(8));
        Assert.Single(GetPreparedMarkerFiles());

        var restartedQueue = new StoredFileDeferredDeletionQueue(_storage);
        Assert.Equal(
            candidate,
            Assert.Single(restartedQueue.TakeBatch(8)));
    }

    [Fact]
    public void PrepareForDatabaseCommit_DoesNotExposeCandidateUntilCommit()
    {
        var candidate = CreateManagedFile(
            "customer-contracts",
            "precommit-hidden.bin");
        var queue = new StoredFileDeferredDeletionQueue(_storage);

        using var preparation = queue.PrepareForDatabaseCommit([candidate]);

        Assert.Empty(queue.TakeBatch(8));
        Assert.Single(GetPreparedMarkerFiles());
        Assert.Empty(GetMarkerFiles());

        preparation.MarkDatabaseCommitCompleted();

        Assert.Empty(GetPreparedMarkerFiles());
        Assert.Single(GetMarkerFiles());
        Assert.Equal(candidate, Assert.Single(queue.TakeBatch(8)));
    }

    [Fact]
    public void PreparedMarker_IsRecoveredAfterRestartWhenCommitOutcomeIsUnknown()
    {
        var candidate = CreateManagedFile(
            "payment-attachments",
            "precommit-restart.bin");
        var queue = new StoredFileDeferredDeletionQueue(_storage);
        using var interruptedPreparation =
            queue.PrepareForDatabaseCommit([candidate]);

        Assert.Empty(queue.TakeBatch(8));
        Assert.Single(GetPreparedMarkerFiles());

        var restartedQueue = new StoredFileDeferredDeletionQueue(_storage);

        Assert.Empty(GetPreparedMarkerFiles());
        Assert.Single(GetMarkerFiles());
        Assert.Equal(
            candidate,
            Assert.Single(restartedQueue.TakeBatch(8)));
    }

    [Fact]
    public void DisposedUncommittedPreparation_IsRemovedAndNotRecovered()
    {
        var candidate = CreateManagedFile(
            "transaction-attachments",
            "precommit-abort.bin");
        var queue = new StoredFileDeferredDeletionQueue(_storage);

        using (queue.PrepareForDatabaseCommit([candidate]))
        {
            Assert.Single(GetPreparedMarkerFiles());
            Assert.Empty(queue.TakeBatch(8));
        }

        Assert.Empty(GetPreparedMarkerFiles());
        Assert.Empty(GetMarkerFiles());
        Assert.Empty(
            new StoredFileDeferredDeletionQueue(_storage)
                .TakeBatch(8));
    }

    [Fact]
    public void ConcurrentPreparations_CommitAndAbort_PreserveCommittedCandidate()
    {
        var candidate = CreateManagedFile(
            "payment-attachments",
            "concurrent-preparations.bin");
        var queue = new StoredFileDeferredDeletionQueue(_storage);
        var firstPreparation =
            queue.PrepareForDatabaseCommit([candidate]);
        using var secondPreparation =
            queue.PrepareForDatabaseCommit([candidate]);

        Assert.Equal(2, GetPreparedMarkerFiles().Length);
        Assert.Empty(queue.TakeBatch(8));

        secondPreparation.MarkDatabaseCommitCompleted();
        firstPreparation.Dispose();

        Assert.Empty(GetPreparedMarkerFiles());
        Assert.Single(GetMarkerFiles());
        Assert.Equal(candidate, Assert.Single(queue.TakeBatch(8)));
    }

    [Fact]
    public void CorruptPreparedMarker_IsPreservedAndNeverRecoveredOrAborted()
    {
        var candidate = CreateManagedFile(
            "customer-contracts",
            "corrupt-prepared-marker.bin");
        var queue = new StoredFileDeferredDeletionQueue(_storage);
        var preparation = queue.PrepareForDatabaseCommit([candidate]);
        var marker = Assert.Single(GetPreparedMarkerFiles());
        File.WriteAllText(
            marker,
            "{\"version\":1,\"relativePath\":\"../outside.bin\"}");

        var restartedQueue = new StoredFileDeferredDeletionQueue(_storage);
        preparation.Dispose();

        Assert.Empty(restartedQueue.TakeBatch(8));
        Assert.True(File.Exists(marker));
        Assert.Empty(GetMarkerFiles());
    }

    [Fact]
    public void PrepareForDatabaseCommit_WhenCoordinationDirectoryIsLost_FailsClosed()
    {
        var candidate = CreateManagedFile(
            "transaction-attachments",
            "coordination-lost.bin");
        var queue = new StoredFileDeferredDeletionQueue(_storage);
        var coordinationDirectory = GetCoordinationDirectory();
        Directory.Delete(coordinationDirectory, recursive: true);
        File.WriteAllText(coordinationDirectory, "not a directory");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            queue.PrepareForDatabaseCommit([candidate]));

        Assert.Contains(
            "coordination is unavailable",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Empty(queue.TakeBatch(8));
    }

    [Fact]
    public void TakeBatch_PreservesMarker_AndNewInstanceRecoversUntilAcknowledged()
    {
        var candidate = CreateManagedFile(
            "transaction-attachments",
            "crash-recovery.bin");
        var queue = new StoredFileDeferredDeletionQueue(_storage);

        queue.Enqueue([candidate]);

        var marker = Assert.Single(GetMarkerFiles());
        Assert.DoesNotContain(
            Path.GetFullPath(_rootPath),
            File.ReadAllText(marker),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(candidate, Assert.Single(queue.TakeBatch(8)));
        Assert.Equal(candidate, Assert.Single(queue.TakeBatch(8)));
        Assert.True(File.Exists(marker));

        var restartedQueue =
            new StoredFileDeferredDeletionQueue(_storage);
        Assert.Equal(
            candidate,
            Assert.Single(restartedQueue.TakeBatch(8)));

        restartedQueue.AcknowledgeCompleted([candidate]);

        Assert.False(File.Exists(marker));
        Assert.Empty(restartedQueue.TakeBatch(8));
        Assert.Empty(
            new StoredFileDeferredDeletionQueue(_storage)
                .TakeBatch(8));
    }

    [Fact]
    public void Enqueue_IsIdempotent_AndRejectsNonManagedPaths()
    {
        var candidate = CreateManagedFile(
            "customer-contracts",
            "idempotent.bin");
        var outsidePath = Path.Combine(
            Path.GetDirectoryName(_rootPath)!,
            $"{Guid.NewGuid():N}__outside.bin");
        File.WriteAllText(outsidePath, "outside");
        try
        {
            var queue = new StoredFileDeferredDeletionQueue(_storage);

            queue.Enqueue(
            [
                candidate,
                candidate,
                outsidePath,
                Path.Combine(_rootPath, "unexpected", "candidate.bin")
            ]);
            queue.Enqueue([candidate]);

            Assert.Equal(candidate, Assert.Single(queue.TakeBatch(8)));
            Assert.Single(GetMarkerFiles());
            Assert.Empty(
                Directory.EnumerateFiles(
                    GetCoordinationDirectory(),
                    ".*.tmp",
                    SearchOption.TopDirectoryOnly));
        }
        finally
        {
            File.Delete(outsidePath);
        }
    }

    [Fact]
    public void CorruptOrOutOfRootMarker_IsPreservedAndNeverLoadedOrAcknowledged()
    {
        var candidate = CreateManagedFile(
            "payment-attachments",
            "corrupt-marker.bin");
        var queue = new StoredFileDeferredDeletionQueue(_storage);
        queue.Enqueue([candidate]);
        var marker = Assert.Single(GetMarkerFiles());
        var outsidePath = Path.GetFullPath(
            Path.Combine(
                _rootPath,
                "..",
                $"{Guid.NewGuid():N}__outside.bin"));
        File.WriteAllText(
            marker,
            $$"""{"version":1,"relativePath":"../{{Path.GetFileName(outsidePath)}}" }""");

        var restartedQueue =
            new StoredFileDeferredDeletionQueue(_storage);

        Assert.Empty(restartedQueue.TakeBatch(8));
        Assert.True(File.Exists(marker));
        restartedQueue.AcknowledgeCompleted([candidate]);
        Assert.True(File.Exists(marker));
        Assert.False(File.Exists(outsidePath));
    }

    [Fact]
    public void CoordinationMarkerAndTempFiles_AreNeverBroadSweepCandidates()
    {
        var candidate = CreateManagedFile(
            "transaction-attachments",
            "coordination-exclusion.bin");
        var queue = new StoredFileDeferredDeletionQueue(_storage);
        queue.Enqueue([candidate]);
        File.Delete(candidate);
        var coordinationDirectory = GetCoordinationDirectory();
        File.WriteAllText(
            Path.Combine(coordinationDirectory, ".interrupted.tmp"),
            "interrupted");

        var candidates = StoredFileOrphanCandidateEnumerator
            .EnumerateBatches(
                _rootPath,
                requestedBatchSize: 8)
            .SelectMany(batch => batch)
            .ToArray();

        Assert.Empty(candidates);
        Assert.Single(GetMarkerFiles());
    }

    [Fact]
    public void ReparseMarker_IsPreservedAndNeverLoaded()
    {
        var candidate = CreateManagedFile(
            "customer-contracts",
            "reparse-marker.bin");
        var queue = new StoredFileDeferredDeletionQueue(_storage);
        queue.Enqueue([candidate]);
        var marker = Assert.Single(GetMarkerFiles());
        var markerPayload = File.ReadAllText(marker);
        var externalPayloadPath = Path.Combine(
            _rootPath,
            "external-marker-payload.json");
        File.WriteAllText(externalPayloadPath, markerPayload);
        File.Delete(marker);

        try
        {
            File.CreateSymbolicLink(marker, externalPayloadPath);
        }
        catch (Exception ex) when (ex is IOException or
                                   UnauthorizedAccessException or
                                   PlatformNotSupportedException)
        {
            // Some Windows environments do not grant symbolic-link creation.
            // The corrupt-marker test still verifies the fail-closed loader.
            return;
        }

        var restartedQueue =
            new StoredFileDeferredDeletionQueue(_storage);

        Assert.Empty(restartedQueue.TakeBatch(8));
        Assert.True(File.Exists(marker));
        restartedQueue.AcknowledgeCompleted([candidate]);
        Assert.True(File.Exists(marker));
        Assert.True(File.Exists(externalPayloadPath));
    }

    private string CreateManagedFile(string area, string fileName)
    {
        var directory = Path.Combine(
            _rootPath,
            area,
            ManagedOwner);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(
            directory,
            $"{Guid.NewGuid():N}__{fileName}");
        File.WriteAllText(path, fileName);
        return Path.GetFullPath(path);
    }

    private string GetCoordinationDirectory()
        => Path.Combine(
            _rootPath,
            ".stored-file-deletion-queue");

    private string[] GetMarkerFiles()
        => Directory.GetFiles(
            GetCoordinationDirectory(),
            "*.pending",
            SearchOption.TopDirectoryOnly);

    private string[] GetPreparedMarkerFiles()
        => Directory.GetFiles(
            GetCoordinationDirectory(),
            "*.prepared",
            SearchOption.TopDirectoryOnly);

    public void Dispose()
    {
        var tempRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(Path.GetTempPath()));
        var resolvedRoot = Path.GetFullPath(_rootPath);
        if (resolvedRoot.StartsWith(
                tempRoot + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal) &&
            Directory.Exists(resolvedRoot))
        {
            Directory.Delete(resolvedRoot, recursive: true);
        }
    }

    private sealed class TestCentralFileStorage(string rootPath)
        : ICentralFileStorage
    {
        public string RootPath { get; } = rootPath;

        public Task<string> SaveBytesAsync(
            string area,
            string ownerId,
            Guid fileId,
            string fileName,
            byte[] content,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public byte[] ReadBytes(
            string? storedPath,
            byte[]? fallback = null)
            => fallback ?? [];

        public void DeleteIfExists(string? storedPath)
        {
        }
    }
}
