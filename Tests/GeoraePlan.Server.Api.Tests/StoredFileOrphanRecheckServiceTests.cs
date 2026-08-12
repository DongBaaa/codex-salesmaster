using System.Reflection;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class StoredFileOrphanRecheckServiceTests : IDisposable
{
    private const string ManagedOwner =
        "0123456789abcdef0123456789abcdef";
    private readonly string _rootPath;

    public StoredFileOrphanRecheckServiceTests()
    {
        _rootPath = Path.Combine(
            Path.GetTempPath(),
            "georaeplan-orphan-recheck-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootPath);
    }

    [Fact]
    public void Options_DefaultToDelayedPeriodicBoundedWork()
    {
        var options = new StoredFileOrphanRecheckOptions();

        Assert.True(options.InitialDelay >= TimeSpan.FromMinutes(30));
        Assert.True(options.Interval >= TimeSpan.FromHours(1));
        Assert.True(options.MinimumCandidateAge >= TimeSpan.FromHours(24));
        Assert.True(options.MaximumCycleDuration >= TimeSpan.FromSeconds(30));
        Assert.InRange(options.BatchSize, 1, 512);
        Assert.InRange(options.MaximumBatchesPerCycle, 1, 64);
        Assert.False(options.EnableBroadSweep);
    }

    [Fact]
    public void DeletionLeaseProtocolVersion_IsStableForHostPreflight()
    {
        var leaseType = typeof(ICentralFileStorage).Assembly.GetType(
            $"{typeof(ICentralFileStorage).Namespace}.StoredFileDeletionLease",
            throwOnError: true);
        var protocolField = leaseType!.GetField(
            "ProtocolVersion",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(protocolField);
        Assert.Equal(
            "shared-flock-v1",
            protocolField!.GetRawConstantValue());
    }

    [Fact]
    public void EnumerateBatches_ExcludesCoordinationEntriesAndBoundsEveryBatch()
    {
        var storedDirectory = Path.Combine(
            _rootPath,
            "customer-contracts",
            ManagedOwner);
        Directory.CreateDirectory(storedDirectory);
        var expected = new[]
        {
            CreateManagedFile(storedDirectory, "a.bin"),
            CreateManagedFile(storedDirectory, "b.bin"),
            CreateManagedFile(storedDirectory, "c.bin"),
            CreateManagedFile(storedDirectory, "d.bin"),
            CreateManagedFile(storedDirectory, "e.bin"),
            CreateManagedFile(storedDirectory, "customer-upload.tmp"),
            CreateManagedFile(storedDirectory, "customer-upload.lock")
        };
        var migratedOwnerDirectory = Path.Combine(
            _rootPath,
            "customer-contracts",
            $"db-{new string('a', 64)}_{ManagedOwner}");
        Directory.CreateDirectory(migratedOwnerDirectory);
        var migratedOwnerFile = CreateManagedFile(
            migratedOwnerDirectory,
            "migrated.bin");
        var excluded = new[]
        {
            CreateFile(_rootPath, ".georaeplan-backup-delete.lock"),
            CreateFile(storedDirectory, ".active-write.tmp"),
            CreateFile(storedDirectory, ".coordination"),
            CreateFile(storedDirectory, "worker.lock"),
            CreateFile(storedDirectory, "worker.lck"),
            CreateFile(storedDirectory, "worker.lease"),
            CreateFile(storedDirectory, "worker.pid"),
            CreateFile(storedDirectory, "worker.coordination")
        };
        var hiddenCoordinationDirectory = Path.Combine(
            _rootPath,
            ".coordination");
        Directory.CreateDirectory(hiddenCoordinationDirectory);
        var hiddenDirectoryFile = CreateFile(
            hiddenCoordinationDirectory,
            "must-not-be-scanned.bin");
        var unexpectedAreaDirectory = Path.Combine(
            _rootPath,
            "unknown-area",
            ManagedOwner);
        Directory.CreateDirectory(unexpectedAreaDirectory);
        var unexpectedAreaFile = CreateManagedFile(
            unexpectedAreaDirectory,
            "must-not-be-scanned.bin");
        var extraDepthDirectory = Path.Combine(storedDirectory, "nested");
        Directory.CreateDirectory(extraDepthDirectory);
        var extraDepthFile = CreateManagedFile(
            extraDepthDirectory,
            "must-not-be-scanned.bin");
        var arbitraryOwnerDirectory = Path.Combine(
            _rootPath,
            "customer-contracts",
            "owner");
        Directory.CreateDirectory(arbitraryOwnerDirectory);
        var arbitraryOwnerFile = CreateManagedFile(
            arbitraryOwnerDirectory,
            "must-not-be-scanned.bin");

        var batches = StoredFileOrphanCandidateEnumerator
            .EnumerateBatches(_rootPath, requestedBatchSize: 2)
            .ToList();
        var candidates = batches.SelectMany(batch => batch).ToHashSet(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);

        Assert.Equal(4, batches.Count);
        Assert.All(batches, batch => Assert.InRange(batch.Count, 1, 2));
        Assert.Equal(expected.Length + 1, candidates.Count);
        Assert.All(expected, path => Assert.Contains(path, candidates));
        Assert.Contains(migratedOwnerFile, candidates);
        Assert.All(excluded, path => Assert.DoesNotContain(path, candidates));
        Assert.DoesNotContain(hiddenDirectoryFile, candidates);
        Assert.DoesNotContain(unexpectedAreaFile, candidates);
        Assert.DoesNotContain(extraDepthFile, candidates);
        Assert.DoesNotContain(arbitraryOwnerFile, candidates);
        Assert.False(StoredFileOrphanCandidateEnumerator.IsEligibleCandidate(
            "symbolic-file.bin",
            FileAttributes.ReparsePoint));
        Assert.False(StoredFileOrphanCandidateEnumerator.IsEligibleCandidate(
            "hidden-file.bin",
            FileAttributes.Hidden));
        Assert.False(StoredFileOrphanCandidateEnumerator.IsEligibleCandidate(
            ".hidden-write.tmp",
            FileAttributes.Normal));
        Assert.False(StoredFileOrphanCandidateEnumerator.IsEligibleCandidate(
            "customer-upload.tmp",
            FileAttributes.Normal));
        Assert.True(StoredFileOrphanCandidateEnumerator.IsEligibleCandidate(
            "0123456789abcdef0123456789abcdef__customer-upload.lock",
            FileAttributes.Normal));
        Assert.True(StoredFileOrphanCandidateEnumerator.IsManagedStoredPath(
            _rootPath,
            expected[0]));
        Assert.False(StoredFileOrphanCandidateEnumerator.IsManagedStoredPath(
            _rootPath,
            unexpectedAreaFile));
    }

    [Fact]
    public void EnumerateBatches_DoesNotTraverseOwnerDirectoryLink()
    {
        var targetDirectory = Path.Combine(
            _rootPath,
            "outside-approved-area",
            ManagedOwner);
        Directory.CreateDirectory(targetDirectory);
        _ = CreateManagedFile(targetDirectory, "outside.bin");
        var approvedArea = Path.Combine(
            _rootPath,
            "customer-contracts");
        Directory.CreateDirectory(approvedArea);
        var linkedOwner = Path.Combine(
            approvedArea,
            "abcdefabcdefabcdefabcdefabcdefab");
        try
        {
            Directory.CreateSymbolicLink(
                linkedOwner,
                targetDirectory);
        }
        catch (Exception ex) when (ex is IOException or
                                   UnauthorizedAccessException or
                                   PlatformNotSupportedException)
        {
            return;
        }

        var candidates = StoredFileOrphanCandidateEnumerator
            .EnumerateBatches(_rootPath, requestedBatchSize: 8)
            .SelectMany(batch => batch)
            .ToList();

        Assert.Empty(candidates);
    }

    [Fact]
    public void EnumerateBatches_ExcludesFilesInsideMinimumCandidateAge()
    {
        var storedDirectory = Path.Combine(
            _rootPath,
            "customer-contracts",
            ManagedOwner);
        Directory.CreateDirectory(storedDirectory);
        var oldCandidate = CreateManagedFile(storedDirectory, "old.bin");
        var freshCandidate = CreateManagedFile(storedDirectory, "fresh.bin");
        File.SetLastWriteTimeUtc(freshCandidate, DateTime.UtcNow);
        var cutoffUtc = DateTime.UtcNow.AddHours(-1);

        var candidates = StoredFileOrphanCandidateEnumerator
            .EnumerateBatches(
                _rootPath,
                requestedBatchSize: 8,
                lastWriteCutoffUtc: cutoffUtc)
            .SelectMany(batch => batch)
            .ToList();

        Assert.Contains(oldCandidate, candidates);
        Assert.DoesNotContain(freshCandidate, candidates);
    }

    [Fact]
    public async Task TryRecheckOnceAsync_DefersUntilDatabaseInitializationCompletes()
    {
        var storedDirectory = Path.Combine(
            _rootPath,
            "customer-contracts",
            ManagedOwner);
        Directory.CreateDirectory(storedDirectory);
        var candidate = CreateManagedFile(
            storedDirectory,
            "initialization.bin");
        var reconciler = new RecordingReconciler();
        var initializationState = new DatabaseInitializationState();
        initializationState.MarkStarted();
        await using var provider = CreateProvider(reconciler);
        var service = CreateService(
            provider,
            batchSize: 8,
            databaseInitializationState: initializationState);

        Assert.False(await service.TryRecheckOnceAsync());
        Assert.Empty(reconciler.Batches);

        initializationState.MarkCompleted();

        Assert.True(await service.TryRecheckOnceAsync());
        Assert.Single(reconciler.Batches);
        Assert.Contains(candidate, reconciler.Batches[0]);
    }

    [Fact]
    public async Task TryRecheckOnceAsync_DoesNotAllowConfiguredAgeBelowSafetyFloor()
    {
        var storedDirectory = Path.Combine(
            _rootPath,
            "customer-contracts",
            ManagedOwner);
        Directory.CreateDirectory(storedDirectory);
        var candidate = CreateManagedFile(
            storedDirectory,
            "still-too-young.bin");
        File.SetLastWriteTimeUtc(candidate, DateTime.UtcNow.AddHours(-6));
        var reconciler = new RecordingReconciler();
        await using var provider = CreateProvider(reconciler);
        var service = CreateService(
            provider,
            batchSize: 8,
            minimumCandidateAge: TimeSpan.FromMinutes(5));

        Assert.True(await service.TryRecheckOnceAsync());
        Assert.Empty(reconciler.Batches);

        File.SetLastWriteTimeUtc(candidate, DateTime.UtcNow.AddDays(-2));

        Assert.True(await service.TryRecheckOnceAsync());
        Assert.Single(reconciler.Batches);
        Assert.Contains(candidate, reconciler.Batches[0]);
    }

    [Fact]
    public async Task TryRecheckOnceAsync_UsesScopedBoundedBatchesAndRetriesFilesLeftByPriorCycle()
    {
        var storedDirectory = Path.Combine(
            _rootPath,
            "payment-attachments",
            ManagedOwner);
        Directory.CreateDirectory(storedDirectory);
        var expected = Enumerable.Range(1, 5)
            .Select(index => CreateManagedFile(
                storedDirectory,
                $"{index}.bin"))
            .ToHashSet(
                OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal);
        var reconciler = new RecordingReconciler();
        await using var provider = CreateProvider(reconciler);
        var service = CreateService(provider, batchSize: 2);

        Assert.True(await service.TryRecheckOnceAsync());
        var firstCycleBatches = reconciler.Batches.ToList();
        Assert.Equal(3, firstCycleBatches.Count);
        Assert.All(firstCycleBatches, batch => Assert.InRange(batch.Count, 1, 2));
        Assert.True(expected.SetEquals(
            firstCycleBatches.SelectMany(batch => batch)));

        // Simulates the existing reconciler deferring physical deletion while
        // the backup owns its exclusive deletion lease: every file still exists.
        Assert.All(expected, path => Assert.True(File.Exists(path)));

        Assert.True(await service.TryRecheckOnceAsync());
        var secondCycleBatches = reconciler.Batches.Skip(3).ToList();
        Assert.Equal(3, secondCycleBatches.Count);
        Assert.All(secondCycleBatches, batch => Assert.InRange(batch.Count, 1, 2));
        Assert.True(expected.SetEquals(
            secondCycleBatches.SelectMany(batch => batch)));
    }

    [Fact]
    public async Task TryRecheckOnceAsync_BoundsTotalBatchesAndContinuesFromPriorOffset()
    {
        var storedDirectory = Path.Combine(
            _rootPath,
            "payment-attachments",
            ManagedOwner);
        Directory.CreateDirectory(storedDirectory);
        var expected = Enumerable.Range(1, 7)
            .Select(index => CreateManagedFile(
                storedDirectory,
                $"bounded-{index}.bin"))
            .ToHashSet(
                OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal);
        var reconciler = new RecordingReconciler();
        await using var provider = CreateProvider(reconciler);
        var service = CreateService(
            provider,
            batchSize: 2,
            maximumBatchesPerCycle: 2);

        Assert.True(await service.TryRecheckOnceAsync());
        var firstCycleCandidates = reconciler.Batches
            .SelectMany(batch => batch)
            .ToHashSet(expected.Comparer);
        Assert.Equal(2, reconciler.Batches.Count);
        Assert.Equal(4, firstCycleCandidates.Count);

        Assert.True(await service.TryRecheckOnceAsync());
        var secondCycleBatches = reconciler.Batches.Skip(2).ToList();
        var secondCycleCandidates = secondCycleBatches
            .SelectMany(batch => batch)
            .ToHashSet(expected.Comparer);
        Assert.Equal(2, secondCycleBatches.Count);
        Assert.Equal(3, secondCycleCandidates.Count);
        Assert.Empty(firstCycleCandidates.Intersect(secondCycleCandidates));
        Assert.True(expected.SetEquals(
            firstCycleCandidates.Concat(secondCycleCandidates)));
    }

    [Fact]
    public async Task TryRecheckOnceAsync_RetriesQueuedCurrentUptimeFileWithoutBroadSweep()
    {
        var storedDirectory = Path.Combine(
            _rootPath,
            "payment-attachments",
            ManagedOwner);
        Directory.CreateDirectory(storedDirectory);
        var storage = new TestCentralFileStorage(_rootPath);
        var queue = new StoredFileDeferredDeletionQueue(storage);
        var reconciler = new RecordingReconciler();
        await using var provider = CreateProvider(reconciler);
        var service = CreateService(
            provider,
            batchSize: 8,
            deferredDeletionQueue: queue,
            fileStorage: storage,
            enableBroadSweep: false);
        var currentUptimeCandidate = CreateManagedFile(
            storedDirectory,
            "current-uptime.bin",
            ageFile: false);
        queue.Enqueue([currentUptimeCandidate]);

        Assert.True(await service.TryRecheckOnceAsync());

        Assert.Single(reconciler.Batches);
        Assert.Contains(
            currentUptimeCandidate,
            reconciler.Batches[0]);
    }

    [Fact]
    public async Task TryRecheckOnceAsync_DefaultSafetyModePreservesBroadCandidateAndProcessesDeferredQueue()
    {
        var storedDirectory = Path.Combine(
            _rootPath,
            "payment-attachments",
            ManagedOwner);
        Directory.CreateDirectory(storedDirectory);
        var broadCandidate = CreateManagedFile(
            storedDirectory,
            "broad-disabled.bin");
        var deferredCandidate = CreateManagedFile(
            storedDirectory,
            "deferred-enabled.bin",
            ageFile: false);
        var storage = new TestCentralFileStorage(_rootPath);
        var queue = new StoredFileDeferredDeletionQueue(storage);
        queue.Enqueue([deferredCandidate]);
        var reconciler = new RecordingReconciler();
        await using var provider = CreateProvider(reconciler);
        var service = CreateService(
            provider,
            batchSize: 8,
            deferredDeletionQueue: queue,
            fileStorage: storage,
            enableBroadSweep: false);

        Assert.True(await service.TryRecheckOnceAsync());

        Assert.Single(reconciler.Batches);
        Assert.Contains(deferredCandidate, reconciler.Batches[0]);
        Assert.DoesNotContain(broadCandidate, reconciler.Batches[0]);
        Assert.True(File.Exists(broadCandidate));
    }

    [Fact]
    public async Task BackupDeferredReconciler_QueuesManagedPathWhenLeaseIsUnavailable()
    {
        var storedDirectory = Path.Combine(
            _rootPath,
            "transaction-attachments",
            ManagedOwner);
        Directory.CreateDirectory(storedDirectory);
        var candidate = CreateManagedFile(
            storedDirectory,
            "deferred.bin",
            ageFile: false);
        var storage = new TestCentralFileStorage(_rootPath);
        var queue = new StoredFileDeferredDeletionQueue(storage);
        var logger =
            new RecordingLogger<BackupDeferredStoredFileReferenceReconciler>();
        var reconciler = new BackupDeferredStoredFileReferenceReconciler(
            null!,
            storage,
            new UnavailableDeletionLeaseProbe(),
            queue,
            logger);

        await reconciler.DeleteUnreferencedAsync([candidate]);

        Assert.Equal(candidate, Assert.Single(queue.TakeBatch(8)));
        Assert.Single(logger.Messages);
        Assert.DoesNotContain(
            _rootPath,
            logger.Messages[0],
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BackupDeferredReconciler_RequeuesLookupInconclusiveOutcome()
    {
        var storedDirectory = Path.Combine(
            _rootPath,
            "transaction-attachments",
            ManagedOwner);
        Directory.CreateDirectory(storedDirectory);
        var candidate = CreateManagedFile(
            storedDirectory,
            "lookup-inconclusive.bin",
            ageFile: false);
        var storage = new TestCentralFileStorage(_rootPath);
        var queue = new StoredFileDeferredDeletionQueue(storage);
        var innerLogger =
            new RecordingLogger<StoredFileReferenceReconciler>();
        await using var provider = new ServiceCollection()
            .BuildServiceProvider();
        var inner = new StoredFileReferenceReconciler(
            provider.GetRequiredService<IServiceScopeFactory>(),
            storage,
            new ThrowingConnectionResolver(),
            new RevisionClock(),
            innerLogger);
        var reconciler = new BackupDeferredStoredFileReferenceReconciler(
            inner,
            storage,
            new AvailableDeletionLeaseProbe(),
            queue,
            new RecordingLogger<
                BackupDeferredStoredFileReferenceReconciler>());

        await reconciler.DeleteUnreferencedAsync([candidate]);

        Assert.Equal(candidate, Assert.Single(queue.TakeBatch(8)));
        Assert.Single(innerLogger.Messages);
        Assert.DoesNotContain(
            _rootPath,
            innerLogger.Messages[0],
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BackupDeferredReconciler_RequeuesDeletionIncompleteOutcome()
    {
        var storedDirectory = Path.Combine(
            _rootPath,
            "payment-attachments",
            ManagedOwner);
        Directory.CreateDirectory(storedDirectory);
        var candidate = CreateManagedFile(
            storedDirectory,
            "delete-failed.bin",
            ageFile: false);
        var storage = new NonDeletingCentralFileStorage(_rootPath);
        var queue = new StoredFileDeferredDeletionQueue(storage);
        var currentUser = new TestCurrentUserContext();
        var revisionClock = new RevisionClock();
        var connectionResolver =
            new TestTenantDatabaseConnectionResolver(_rootPath);
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connectionResolver.ResolveCentral().ConnectionString)
            .Options;
        await using (var database = new AppDbContext(
                         dbOptions,
                         currentUser,
                         revisionClock))
        {
            await database.Database.EnsureCreatedAsync();
        }

        var services = new ServiceCollection();
        services.AddSingleton<ICurrentUserContext>(currentUser);
        await using var provider = services.BuildServiceProvider();
        var inner = new StoredFileReferenceReconciler(
            provider.GetRequiredService<IServiceScopeFactory>(),
            storage,
            connectionResolver,
            revisionClock);
        var reconciler = new BackupDeferredStoredFileReferenceReconciler(
            inner,
            storage,
            new AvailableDeletionLeaseProbe(),
            queue,
            new RecordingLogger<
                BackupDeferredStoredFileReferenceReconciler>());

        await reconciler.DeleteUnreferencedAsync([candidate]);

        Assert.True(File.Exists(candidate));
        Assert.Equal(candidate, Assert.Single(queue.TakeBatch(8)));
    }

    [Fact]
    public async Task StoredFileReferenceReconciler_PropagatesCancellationOutcome()
    {
        var storage = new TestCentralFileStorage(_rootPath);
        await using var provider = new ServiceCollection()
            .BuildServiceProvider();
        var inner = new StoredFileReferenceReconciler(
            provider.GetRequiredService<IServiceScopeFactory>(),
            storage,
            new TestTenantDatabaseConnectionResolver(_rootPath),
            new RevisionClock());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => inner.DeleteUnreferencedWithOutcomeAsync(
                ["candidate"],
                cancellation.Token));
    }

    [Fact]
    public async Task TryRecheckOnceAsync_FailsClosedWhenDatabaseTopologyIsIncomplete()
    {
        var storedDirectory = Path.Combine(
            _rootPath,
            "customer-contracts",
            ManagedOwner);
        Directory.CreateDirectory(storedDirectory);
        _ = CreateManagedFile(storedDirectory, "topology.bin");
        var reconciler = new RecordingReconciler();
        var logger = new RecordingLogger<StoredFileOrphanRecheckService>();
        await using var provider = CreateProvider(reconciler);
        var service = CreateService(
            provider,
            batchSize: 8,
            logger,
            connectionResolver: new ThrowingConnectionResolver());

        Assert.False(await service.TryRecheckOnceAsync());

        Assert.Empty(reconciler.Batches);
        Assert.Single(logger.Messages);
        Assert.Contains(
            nameof(InvalidOperationException),
            logger.Messages[0],
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryRecheckOnceAsync_ContainsFailureWithoutLoggingSensitivePath_AndCanRetry()
    {
        var storedDirectory = Path.Combine(
            _rootPath,
            "transaction-attachments",
            ManagedOwner);
        Directory.CreateDirectory(storedDirectory);
        var candidate = CreateManagedFile(storedDirectory, "receipt.bin");
        var reconciler = new ThrowOnceReconciler(
            new IOException($"sensitive-path={candidate}"));
        var logger = new RecordingLogger<StoredFileOrphanRecheckService>();
        await using var provider = CreateProvider(reconciler);
        var service = CreateService(provider, batchSize: 8, logger);

        Assert.False(await service.TryRecheckOnceAsync());
        Assert.Single(logger.Messages);
        Assert.DoesNotContain(_rootPath, logger.Messages[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains(nameof(IOException), logger.Messages[0], StringComparison.Ordinal);

        Assert.True(await service.TryRecheckOnceAsync());
        Assert.Equal(2, reconciler.CallCount);
        Assert.Contains(candidate, reconciler.SuccessfulCandidates);
    }

    [Fact]
    public async Task TryRecheckOnceAsync_PropagatesHostCancellationWithoutWarning()
    {
        var storedDirectory = Path.Combine(
            _rootPath,
            "customer-contracts",
            ManagedOwner);
        Directory.CreateDirectory(storedDirectory);
        _ = CreateManagedFile(storedDirectory, "cancelled.bin");
        var reconciler = new RecordingReconciler();
        var logger = new RecordingLogger<StoredFileOrphanRecheckService>();
        await using var provider = CreateProvider(reconciler);
        var service = CreateService(provider, batchSize: 8, logger);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.TryRecheckOnceAsync(cancellation.Token));

        Assert.Empty(logger.Messages);
        Assert.Empty(reconciler.Batches);
    }

    private StoredFileOrphanRecheckService CreateService(
        ServiceProvider provider,
        int batchSize,
        ILogger<StoredFileOrphanRecheckService>? logger = null,
        DatabaseInitializationState? databaseInitializationState = null,
        TimeSpan? minimumCandidateAge = null,
        int? maximumBatchesPerCycle = null,
        IStoredFileDeferredDeletionQueue? deferredDeletionQueue = null,
        ICentralFileStorage? fileStorage = null,
        ITenantDatabaseConnectionResolver? connectionResolver = null,
        bool enableBroadSweep = true)
    {
        databaseInitializationState ??= new DatabaseInitializationState();
        if (!databaseInitializationState.CreateSnapshot().Started)
        {
            databaseInitializationState.MarkStarted();
            databaseInitializationState.MarkCompleted();
        }

        fileStorage ??= new TestCentralFileStorage(_rootPath);
        deferredDeletionQueue ??=
            new StoredFileDeferredDeletionQueue(fileStorage);
        connectionResolver ??=
            new TestTenantDatabaseConnectionResolver(_rootPath);

        return new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            fileStorage,
            deferredDeletionQueue,
            connectionResolver,
            databaseInitializationState,
            Options.Create(new StoredFileOrphanRecheckOptions
            {
                BatchSize = batchSize,
                MinimumCandidateAge = minimumCandidateAge ??
                                      StoredFileOrphanRecheckOptions.DefaultMinimumCandidateAge,
                MaximumBatchesPerCycle = maximumBatchesPerCycle ??
                                         StoredFileOrphanRecheckOptions.DefaultMaximumBatchesPerCycle,
                EnableBroadSweep = enableBroadSweep
            }),
            logger ?? new RecordingLogger<StoredFileOrphanRecheckService>());
    }

    private static ServiceProvider CreateProvider(
        IStoredFileReferenceReconciler reconciler)
    {
        var services = new ServiceCollection();
        services.AddScoped<IStoredFileReferenceReconciler>(_ => reconciler);
        return services.BuildServiceProvider();
    }

    private static string CreateFile(
        string directory,
        string fileName,
        bool ageFile = true)
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, fileName);
        if (ageFile)
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-2));
        return Path.GetFullPath(path);
    }

    private static string CreateManagedFile(
        string directory,
        string fileName,
        bool ageFile = true)
        => CreateFile(
            directory,
            $"{Guid.NewGuid():N}__{fileName}",
            ageFile);

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

    private sealed class RecordingReconciler : IStoredFileReferenceReconciler
    {
        public List<IReadOnlyList<string>> Batches { get; } = [];

        public Task DeleteUnreferencedAsync(
            IEnumerable<string> candidatePaths,
            CancellationToken cancellationToken = default)
        {
            Batches.Add(candidatePaths.ToArray());
            return Task.CompletedTask;
        }

        public Task<PaymentAttachment?> FindPaymentAttachmentAsync(
            Guid attachmentId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<PaymentAttachment?>(null);
    }

    private sealed class ThrowOnceReconciler(Exception exception)
        : IStoredFileReferenceReconciler
    {
        public int CallCount { get; private set; }
        public List<string> SuccessfulCandidates { get; } = [];

        public Task DeleteUnreferencedAsync(
            IEnumerable<string> candidatePaths,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (CallCount == 1)
                throw exception;

            SuccessfulCandidates.AddRange(candidatePaths);
            return Task.CompletedTask;
        }

        public Task<PaymentAttachment?> FindPaymentAttachmentAsync(
            Guid attachmentId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<PaymentAttachment?>(null);
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

        public byte[] ReadBytes(string? storedPath, byte[]? fallback = null)
            => fallback ?? [];

        public void DeleteIfExists(string? storedPath)
        {
        }
    }

    private sealed class NonDeletingCentralFileStorage(string rootPath)
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

        public FileStorageInspectionResult Inspect(
            string? storedPath,
            bool computeHash = false)
            => new(
                HasStoredPath: !string.IsNullOrWhiteSpace(storedPath),
                IsSafePath: true,
                Exists: File.Exists(storedPath),
                Length: null,
                Hash: string.Empty,
                Error: string.Empty);

        public void DeleteIfExists(string? storedPath)
        {
            // Simulates a transient physical-delete failure.
        }
    }

    private sealed class TestCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId => null;
        public string Username => "orphan-recheck-test";
        public string TenantCode => "USENET_GROUP";
        public string OfficeCode => "USENET";
        public string ScopeType => "Admin";
        public bool IsAdmin => true;
        public bool IsGodMode => true;

        public bool HasPermission(string permission)
            => true;
    }

    private sealed class TestTenantDatabaseConnectionResolver(
        string rootPath) : ITenantDatabaseConnectionResolver
    {
        private readonly TenantDatabaseConnectionInfo _connection = new()
        {
            UseSqlite = true,
            ConnectionString =
                $"Data Source={Path.Combine(rootPath, "central.db")};Pooling=False"
        };

        public TenantDatabaseConnectionInfo ResolveCurrent()
            => _connection;

        public TenantDatabaseConnectionInfo ResolveCentral()
            => _connection;

        public TenantDatabaseConnectionInfo ResolveBusinessTenant(
            string? tenantCode)
            => _connection;

        public IReadOnlyList<TenantDatabaseConnectionInfo>
            GetDedicatedBusinessConnections()
            => [];
    }

    private sealed class ThrowingConnectionResolver
        : ITenantDatabaseConnectionResolver
    {
        public TenantDatabaseConnectionInfo ResolveCurrent()
            => throw new InvalidOperationException(
                "Required database topology is incomplete.");

        public TenantDatabaseConnectionInfo ResolveCentral()
            => throw new InvalidOperationException(
                "Required database topology is incomplete.");

        public TenantDatabaseConnectionInfo ResolveBusinessTenant(
            string? tenantCode)
            => throw new InvalidOperationException(
                "Required database topology is incomplete.");

        public IReadOnlyList<TenantDatabaseConnectionInfo>
            GetDedicatedBusinessConnections()
            => throw new InvalidOperationException(
                "Required database topology is incomplete.");
    }

    private sealed class UnavailableDeletionLeaseProbe
        : IStoredFileDeletionLeaseProbe
    {
        public IDisposable? TryAcquireShared(string storageRoot)
            => null;
    }

    private sealed class AvailableDeletionLeaseProbe
        : IStoredFileDeletionLeaseProbe
    {
        public IDisposable? TryAcquireShared(string storageRoot)
            => NoOpDisposable.Instance;
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public static NoOpDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
