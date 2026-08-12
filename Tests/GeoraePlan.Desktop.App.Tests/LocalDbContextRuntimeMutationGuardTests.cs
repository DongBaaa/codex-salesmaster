using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class LocalDbContextRuntimeMutationGuardTests
{
    [Fact]
    public async Task ExplicitTransaction_HoldsRuntimeMutationGateUntilCommit_AndOwnerSaveIsReentrant()
    {
        await using var database = new RuntimeDatabase();
        var connectionString = database.ConnectionString;
        await using var ownerDb = CreateContext(connectionString);
        await ownerDb.Database.EnsureCreatedAsync();

        var item = CreateItem();
        await using var transaction =
            await ownerDb.BeginRuntimeMutationTransactionAsync();
        ownerDb.Items.Add(item);
        Assert.Equal(1, await ownerDb.SaveChangesAsync().WaitAsync(TimeSpan.FromSeconds(5)));

        var waiterStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var waiter = RunWithoutExecutionContext(async () =>
        {
            waiterStarted.TrySetResult();
            await using var lease = await LocalDbContext.AcquireRuntimeMutationGateAsync(
                CancellationToken.None);
        });

        await waiterStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(150);
        Assert.False(waiter.IsCompleted);

        await transaction.CommitAsync();
        await waiter.WaitAsync(TimeSpan.FromSeconds(5));

        await using var verificationDb = CreateContext(connectionString);
        Assert.True(await verificationDb.Items.AsNoTracking().AnyAsync(current => current.Id == item.Id));
    }

    [Fact]
    public async Task OwnerScopeDrain_RetainsRuntimeGateUntilChildTransactionCommits()
    {
        await using var database = new RuntimeDatabase();
        var connectionString = database.ConnectionString;
        await using var childDb = CreateContext(connectionString);
        await childDb.Database.EnsureCreatedAsync();

        var rootLease = await LocalDbContext.AcquireRuntimeMutationGateAsync(CancellationToken.None);
        var ownerScope = LocalDbContext.EnterRuntimeMutationGateOwnerScope(rootLease);
        IDbContextTransaction? childTransaction = null;
        try
        {
            childTransaction = await childDb.BeginRuntimeMutationTransactionAsync();
            var ownerDrain = ownerScope.DisposeAsync().AsTask();
            await Task.Delay(150);
            Assert.False(ownerDrain.IsCompleted);

            var waiterStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var waiter = RunWithoutExecutionContext(async () =>
            {
                waiterStarted.TrySetResult();
                await using var lease = await LocalDbContext.AcquireRuntimeMutationGateAsync(
                    CancellationToken.None);
            });

            await waiterStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(150);
            Assert.False(waiter.IsCompleted);

            var item = CreateItem();
            childDb.Items.Add(item);
            Assert.Equal(1, await childDb.SaveChangesAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            await childTransaction.CommitAsync();
            await ownerDrain.WaitAsync(TimeSpan.FromSeconds(5));
            await rootLease.DisposeAsync();
            await waiter.WaitAsync(TimeSpan.FromSeconds(5));

            await using var verificationDb = CreateContext(connectionString);
            Assert.True(await verificationDb.Items.AsNoTracking()
                .AnyAsync(current => current.Id == item.Id));
        }
        finally
        {
            await ownerScope.DisposeAsync();
            if (childTransaction is not null)
                await childTransaction.DisposeAsync();
            await rootLease.DisposeAsync();
        }
    }

    [Fact]
    public async Task ActiveOwnerScope_SerializesParallelChildMutationOperations()
    {
        await using var database = new RuntimeDatabase();
        var connectionString = database.ConnectionString;
        await using var firstDb = CreateContext(connectionString);
        await using var secondDb = CreateContext(connectionString);
        await firstDb.Database.EnsureCreatedAsync();

        var firstEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var rootLease =
            await LocalDbContext.AcquireRuntimeMutationGateAsync(CancellationToken.None);
        await using var ownerScope = LocalDbContext.EnterRuntimeMutationGateOwnerScope(rootLease);
        var first = Task.Run(() => firstDb.ExecuteRuntimeMutationOperationAsync(async () =>
        {
            firstEntered.TrySetResult();
            await releaseFirst.Task;
        }));

        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = Task.Run(() => secondDb.ExecuteRuntimeMutationOperationAsync(() =>
        {
            secondEntered.TrySetResult();
            return Task.CompletedTask;
        }));

        try
        {
            await Task.Delay(150);
            Assert.False(secondEntered.Task.IsCompleted);
        }
        finally
        {
            releaseFirst.TrySetResult();
        }

        await first.WaitAsync(TimeSpan.FromSeconds(5));
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(secondEntered.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ActiveOwnerScope_SerializesParallelChildOperationsOnSameContext()
    {
        await using var database = new RuntimeDatabase();
        await using var db = CreateContext(database.ConnectionString);
        await db.Database.EnsureCreatedAsync();
        var firstEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var rootLease =
            await LocalDbContext.AcquireRuntimeMutationGateAsync(CancellationToken.None);
        await using var ownerScope = LocalDbContext.EnterRuntimeMutationGateOwnerScope(rootLease);
        var first = Task.Run(() => db.ExecuteRuntimeMutationOperationAsync(async () =>
        {
            firstEntered.TrySetResult();
            await releaseFirst.Task;
        }));
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = Task.Run(() => db.ExecuteRuntimeMutationOperationAsync(() =>
        {
            secondEntered.TrySetResult();
            return Task.CompletedTask;
        }));

        try
        {
            await Task.Delay(150);
            Assert.False(secondEntered.Task.IsCompleted);
        }
        finally
        {
            releaseFirst.TrySetResult();
        }

        await first.WaitAsync(TimeSpan.FromSeconds(5));
        await second.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task NestedParallelOperationOnSameContext_IsRejectedInsteadOfBypassingOwnerGate()
    {
        await using var database = new RuntimeDatabase();
        await using var db = CreateContext(database.ConnectionString);
        await db.Database.EnsureCreatedAsync();

        await db.ExecuteRuntimeMutationOperationAsync(async () =>
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await Task.Run(() => db.ExecuteRuntimeMutationOperationAsync(
                    () => Task.CompletedTask)));
            Assert.Contains("Nested runtime mutation operations", exception.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task ExecutionScope_DrainsInheritedChildSaveBeforeReleasingRuntimeGate()
    {
        await using var database = new RuntimeDatabase();
        var connectionString = database.ConnectionString;
        await using (var seedDb = CreateContext(connectionString))
            await seedDb.Database.EnsureCreatedAsync();
        var blocker = new BlockingNonQueryInterceptor();
        await using var db = CreateContext(connectionString, blocker);
        Task<int>? childSave = null;

        var outerOperation = db.ExecuteRuntimeMutationOperationAsync(async () =>
        {
            db.Items.Add(CreateItem());
            childSave = Task.Run(() => db.SaveChangesAsync());
            await blocker.Entered.Task;
        });

        await blocker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var waiter = RunWithoutExecutionContext(async () =>
        {
            await using var lease = await LocalDbContext.AcquireRuntimeMutationGateAsync(
                CancellationToken.None);
        });
        try
        {
            await Task.Delay(150);
            Assert.False(outerOperation.IsCompleted);
            Assert.False(waiter.IsCompleted);
        }
        finally
        {
            blocker.Release.TrySetResult();
        }

        Assert.Equal(
            1,
            await Assert.IsAssignableFrom<Task<int>>(childSave)
                .WaitAsync(TimeSpan.FromSeconds(5)));
        await outerOperation.WaitAsync(TimeSpan.FromSeconds(5));
        await waiter.WaitAsync(TimeSpan.FromSeconds(5));
        await db.DisposeAsync();
    }

    [Fact]
    public async Task TransactionCommit_DrainsInheritedChildSaveBeforeCommittingAndReleasingGate()
    {
        await using var database = new RuntimeDatabase();
        var connectionString = database.ConnectionString;
        await using (var seedDb = CreateContext(connectionString))
            await seedDb.Database.EnsureCreatedAsync();
        var blocker = new BlockingNonQueryInterceptor();
        await using var db = CreateContext(connectionString, blocker);
        await using var transaction = await db.BeginRuntimeMutationTransactionAsync();
        db.Items.Add(CreateItem());
        var childSave = Task.Run(() => db.SaveChangesAsync());
        await blocker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var commit = transaction.CommitAsync();
        var waiter = RunWithoutExecutionContext(async () =>
        {
            await using var lease = await LocalDbContext.AcquireRuntimeMutationGateAsync(
                CancellationToken.None);
        });
        try
        {
            await Task.Delay(150);
            Assert.False(commit.IsCompleted);
            Assert.False(waiter.IsCompleted);
        }
        finally
        {
            blocker.Release.TrySetResult();
        }

        Assert.Equal(1, await childSave.WaitAsync(TimeSpan.FromSeconds(5)));
        await commit.WaitAsync(TimeSpan.FromSeconds(5));
        await waiter.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task OwnerCallbackBoundary_PermanentlySuppressesAuthorityInCapturedBackgroundTask()
    {
        await using var database = new RuntimeDatabase();
        await using var db = CreateContext(database.ConnectionString);
        await db.Database.EnsureCreatedAsync();
        var startBackground = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> backgroundSave;

        await using var rootLease =
            await LocalDbContext.AcquireRuntimeMutationGateAsync(CancellationToken.None);
        await using var ownerScope = LocalDbContext.EnterRuntimeMutationGateOwnerScope(rootLease);
        using (LocalDbContext.SuppressRuntimeMutationOwnerForCallback())
        {
            backgroundSave = Task.Run(async () =>
            {
                await startBackground.Task;
                db.Items.Add(CreateItem());
                return await db.SaveChangesAsync();
            });
        }

        startBackground.TrySetResult();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await backgroundSave.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("event or background callback", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CrossContextMutationInsideRuntimeTransaction_FailsFastAndRecoversAfterRollback()
    {
        await using var database = new RuntimeDatabase();
        var connectionString = database.ConnectionString;
        await using var ownerDb = CreateContext(connectionString);
        await using var otherDb = CreateContext(connectionString);
        await ownerDb.Database.EnsureCreatedAsync();

        await using var transaction = await ownerDb.BeginRuntimeMutationTransactionAsync();
        otherDb.Items.Add(CreateItem());
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await otherDb.SaveChangesAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("different LocalDbContext", exception.Message, StringComparison.Ordinal);

        await transaction.RollbackAsync();
        Assert.Equal(1, await otherDb.SaveChangesAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData("execute-update")]
    [InlineData("raw-sql")]
    public async Task CrossContextSetBasedMutationInsideRuntimeTransaction_FailsFast(
        string mutationKind)
    {
        await using var database = new RuntimeDatabase();
        var connectionString = database.ConnectionString;
        var item = CreateItem();
        await using var ownerDb = CreateContext(connectionString);
        await ownerDb.Database.EnsureCreatedAsync();
        ownerDb.Items.Add(item);
        await ownerDb.SaveChangesAsync();
        await using var otherDb = CreateContext(connectionString);

        await using var transaction = await ownerDb.BeginRuntimeMutationTransactionAsync();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            if (mutationKind == "execute-update")
            {
                await otherDb.Items
                    .Where(current => current.Id == item.Id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(
                        current => current.SimpleMemo,
                        "cross-context"));
                return;
            }

            await otherDb.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "Items"
                SET "SimpleMemo" = {"cross-context"}
                WHERE "Id" = {item.Id}
                """);
        });

        Assert.Contains("different LocalDbContext", exception.Message, StringComparison.Ordinal);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task AsNoTrackingEntityReadBeforeEpochAdvance_CannotBeAttachedAndSavedAfterAdvance()
    {
        await using var database = new RuntimeDatabase();
        var connectionString = database.ConnectionString;
        var item = CreateItem();
        await using (var seedDb = CreateContext(connectionString))
        {
            await seedDb.Database.EnsureCreatedAsync();
            seedDb.Items.Add(item);
            await seedDb.SaveChangesAsync();
        }

        await using var staleDb = CreateContext(connectionString);
        var staleItem = await staleDb.Items.AsNoTracking()
            .SingleAsync(current => current.Id == item.Id);
        await using (var runtimeLease =
                     await LocalDbContext.AcquireRuntimeMutationGateAsync(CancellationToken.None))
        await using (LocalDbContext.EnterRuntimeMutationGateOwnerScope(runtimeLease))
        {
            await using var freshDb = CreateContext(connectionString);
            var freshItem = await freshDb.Items.SingleAsync(current => current.Id == item.Id);
            freshItem.SimpleMemo = "server-fresh-detached";
            await freshDb.SaveChangesAsync();
            freshDb.AdvanceRuntimeMutationEpoch();
        }

        staleItem.SimpleMemo = "stale-detached";
        await using var writeDb = CreateContext(connectionString);
        writeDb.Update(staleItem);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            async () => await writeDb.SaveChangesAsync());

        await using var verificationDb = CreateContext(connectionString);
        Assert.Equal(
            "server-fresh-detached",
            await verificationDb.Items.AsNoTracking()
                .Where(current => current.Id == item.Id)
                .Select(current => current.SimpleMemo)
                .SingleAsync());
    }

    [Fact]
    public async Task StreamingMaterializationAfterEpochAdvance_RetainsReaderStartEpoch()
    {
        await using var database = new RuntimeDatabase();
        var connectionString = database.ConnectionString;
        await using (var seedDb = CreateContext(connectionString))
        {
            await seedDb.Database.EnsureCreatedAsync();
            seedDb.Items.AddRange(CreateItem(), CreateItem());
            await seedDb.SaveChangesAsync();
        }

        await using var readerDb = CreateContext(connectionString);
        await using var enumerator = readerDb.Items.AsNoTracking()
            .OrderBy(current => current.Id)
            .AsAsyncEnumerable()
            .GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());

        await using (var runtimeLease =
                     await LocalDbContext.AcquireRuntimeMutationGateAsync(CancellationToken.None))
        await using (LocalDbContext.EnterRuntimeMutationGateOwnerScope(runtimeLease))
        {
            await using var epochDb = CreateContext(connectionString);
            epochDb.AdvanceRuntimeMutationEpoch();
        }

        Assert.True(await enumerator.MoveNextAsync());
        var staleItem = enumerator.Current;
        staleItem.SimpleMemo = "stale-streamed";
        await using var writeDb = CreateContext(connectionString);
        writeDb.Update(staleItem);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            async () => await writeDb.SaveChangesAsync());
    }

    [Fact]
    public async Task NoOpSave_CannotAcceptNewEpochForAlreadyTrackedStaleEntity()
    {
        await using var database = new RuntimeDatabase();
        var connectionString = database.ConnectionString;
        var item = CreateItem();
        await using (var seedDb = CreateContext(connectionString))
        {
            await seedDb.Database.EnsureCreatedAsync();
            seedDb.Items.Add(item);
            await seedDb.SaveChangesAsync();
        }

        await using var staleDb = CreateContext(connectionString);
        var staleItem = await staleDb.Items.SingleAsync(current => current.Id == item.Id);
        await using (var runtimeLease =
                     await LocalDbContext.AcquireRuntimeMutationGateAsync(CancellationToken.None))
        await using (LocalDbContext.EnterRuntimeMutationGateOwnerScope(runtimeLease))
        {
            await using var freshDb = CreateContext(connectionString);
            var freshItem = await freshDb.Items.SingleAsync(current => current.Id == item.Id);
            freshItem.SimpleMemo = "server-fresh";
            await freshDb.SaveChangesAsync();
            freshDb.AdvanceRuntimeMutationEpoch();
        }

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            async () => await staleDb.SaveChangesAsync());

        staleItem.SimpleMemo = "stale-after-no-op";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            async () => await staleDb.SaveChangesAsync());

        await using var verificationDb = CreateContext(connectionString);
        Assert.Equal(
            "server-fresh",
            await verificationDb.Items.AsNoTracking()
                .Where(current => current.Id == item.Id)
                .Select(current => current.SimpleMemo)
                .SingleAsync());
    }

    [Fact]
    public async Task NestedExplicitTransaction_IsRejectedImmediatelyWithoutWaitingOnOwnedGate()
    {
        await using var database = new RuntimeDatabase();
        var connectionString = database.ConnectionString;
        await using var db = CreateContext(connectionString);
        await db.Database.EnsureCreatedAsync();
        await using var transaction = await db.BeginRuntimeMutationTransactionAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await db.BeginRuntimeMutationTransactionAsync()
                .WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Contains("Nested runtime mutation transactions", exception.Message, StringComparison.Ordinal);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task ProviderBeginInProgress_RejectsSameContextSaveUntilTransactionIsReady()
    {
        await using var database = new RuntimeDatabase();
        var connectionString = database.ConnectionString;
        await using (var seedDb = CreateContext(connectionString))
            await seedDb.Database.EnsureCreatedAsync();
        var blocker = new BlockingTransactionStartedInterceptor();
        await using var db = CreateContext(connectionString, blocker);
        var begin = db.BeginRuntimeMutationTransactionAsync();
        IDbContextTransaction? transaction = null;
        try
        {
            await blocker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            db.Items.Add(CreateItem());
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await db.SaveChangesAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains("starting or closing", exception.Message, StringComparison.Ordinal);
            db.ChangeTracker.Clear();
        }
        finally
        {
            blocker.Release.TrySetResult();
            transaction = await begin.WaitAsync(TimeSpan.FromSeconds(5));
        }

        await transaction.RollbackAsync();
        await transaction.DisposeAsync();

        db.Items.Add(CreateItem());
        Assert.Equal(1, await db.SaveChangesAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ProviderBeginFailureBeforeStart_PreservesPrimaryAndRestoresRootFlow()
    {
        await using var database = new RuntimeDatabase();
        var connectionString = database.ConnectionString;
        await using (var seedDb = CreateContext(connectionString))
            await seedDb.Database.EnsureCreatedAsync();
        var fault = new ThrowingFirstTransactionStartingInterceptor();
        await using var db = CreateContext(connectionString, fault);

        var primary = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await db.BeginRuntimeMutationTransactionAsync());
        Assert.Equal("runtime-before-provider-start-fault", primary.Message);
        Assert.Null(primary.Data["RuntimeMutationProviderCleanupFailure"]);

        await using (var transaction = await db.BeginRuntimeMutationTransactionAsync())
            await transaction.RollbackAsync();
        db.Items.Add(CreateItem());
        Assert.Equal(1, await db.SaveChangesAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ProviderBeginFailureAfterStart_FaultsDatabaseGuardWhenQuarantineFails()
    {
        await using var database = new RuntimeDatabase();
        var connectionString = database.ConnectionString;
        await using (var seedDb = CreateContext(connectionString))
            await seedDb.Database.EnsureCreatedAsync();
        var startedFault = new ThrowingFirstTransactionStartedInterceptor();
        var closeFault = new ThrowingFirstConnectionCloseInterceptor();
        await using var db = CreateContext(connectionString, startedFault, closeFault);

        var primary = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await db.BeginRuntimeMutationTransactionAsync());
        Assert.Equal("runtime-after-provider-start-fault", primary.Message);
        Assert.Contains(
            "runtime-connection-close-fault",
            Assert.IsAssignableFrom<Exception>(
                primary.Data["RuntimeMutationProviderCleanupFailure"]).ToString(),
            StringComparison.Ordinal);

        db.Items.Add(CreateItem());
        var guardFailure = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await db.SaveChangesAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("안전 정지", guardFailure.Message, StringComparison.Ordinal);

        await using var otherDatabase = new RuntimeDatabase();
        await using var otherDb = CreateContext(otherDatabase.ConnectionString);
        await otherDb.Database.EnsureCreatedAsync();
        otherDb.Items.Add(CreateItem());
        Assert.Equal(1, await otherDb.SaveChangesAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ContextFirstSyncDispose_ReleasesGateAndLaterWrapperDisposeIsIdempotent()
    {
        await using var database = new RuntimeDatabase();
        var connectionString = database.ConnectionString;
        await using (var seedDb = CreateContext(connectionString))
            await seedDb.Database.EnsureCreatedAsync();
        var tracker = new FaultingProviderTransactionInterceptor();
        var db = CreateContext(connectionString, tracker);
        var transaction = await db.BeginRuntimeMutationTransactionAsync();

        db.Dispose();
        Assert.Equal(1, tracker.SyncDisposeCallCount);
        transaction.Dispose();
        await transaction.DisposeAsync();
        Assert.Equal(1, tracker.SyncDisposeCallCount);
        Assert.Equal(0, tracker.AsyncDisposeCallCount);

        await using var nextDb = CreateContext(connectionString);
        nextDb.Items.Add(CreateItem());
        Assert.Equal(1, await nextDb.SaveChangesAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ContextFirstAsyncDispose_ReleasesGateAndLaterWrapperDisposeIsIdempotent()
    {
        await using var database = new RuntimeDatabase();
        var connectionString = database.ConnectionString;
        await using (var seedDb = CreateContext(connectionString))
            await seedDb.Database.EnsureCreatedAsync();
        var tracker = new FaultingProviderTransactionInterceptor();
        var db = CreateContext(connectionString, tracker);
        var transaction = await db.BeginRuntimeMutationTransactionAsync();

        await db.DisposeAsync();
        Assert.Equal(1, tracker.AsyncDisposeCallCount);
        await transaction.DisposeAsync();
        transaction.Dispose();
        Assert.Equal(1, tracker.AsyncDisposeCallCount);
        Assert.Equal(0, tracker.SyncDisposeCallCount);

        await using var nextDb = CreateContext(connectionString);
        nextDb.Items.Add(CreateItem());
        Assert.Equal(1, await nextDb.SaveChangesAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ConcurrentCommitAndRollback_AreSerializedAndProviderCompletesOnce()
    {
        await using var database = new RuntimeDatabase();
        var connectionString = database.ConnectionString;
        await using (var seedDb = CreateContext(connectionString))
            await seedDb.Database.EnsureCreatedAsync();
        var blocker = new BlockingTransactionCommitInterceptor();
        await using var db = CreateContext(connectionString, blocker);
        var transaction = await db.BeginRuntimeMutationTransactionAsync();
        var commit = transaction.CommitAsync();
        await blocker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopwatch = Stopwatch.StartNew();
        var busy = Assert.ThrowsAny<InvalidOperationException>(transaction.Rollback);
        stopwatch.Stop();
        Assert.Contains("asynchronously", busy.Message, StringComparison.Ordinal);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        var rollback = transaction.RollbackAsync();
        try
        {
            await Task.Delay(150);
            Assert.False(rollback.IsCompleted);
        }
        finally
        {
            blocker.Release.TrySetResult();
        }

        await commit.WaitAsync(TimeSpan.FromSeconds(5));
        var ended = await Assert.ThrowsAsync<InvalidOperationException>(
            () => rollback.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("already completed", ended.Message, StringComparison.Ordinal);
        await transaction.DisposeAsync();

        await using var nextDb = CreateContext(connectionString);
        nextDb.Items.Add(CreateItem());
        Assert.Equal(1, await nextDb.SaveChangesAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ConcurrentRollback_WaitsForFailedCommitCleanupThenBecomesNoop()
    {
        await using var database = new RuntimeDatabase();
        var connectionString = database.ConnectionString;
        await using (var seedDb = CreateContext(connectionString))
            await seedDb.Database.EnsureCreatedAsync();
        var blocker = new BlockingTransactionCommitInterceptor();
        var tracker = new FaultingProviderTransactionInterceptor(
            asyncCommitFailure: new InvalidOperationException("runtime-concurrent-commit-fault"));
        await using var db = CreateContext(connectionString, blocker, tracker);
        var transaction = await db.BeginRuntimeMutationTransactionAsync();

        var commit = transaction.CommitAsync();
        await blocker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var rollback = transaction.RollbackAsync();
        Assert.False(rollback.IsCompleted);
        blocker.Release.TrySetResult();

        var primary = await Assert.ThrowsAsync<InvalidOperationException>(
            () => commit.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("runtime-concurrent-commit-fault", primary.Message);
        await rollback.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, tracker.AsyncRollbackCallCount);
        Assert.Equal(1, tracker.AsyncDisposeCallCount);

        await using var nextDb = CreateContext(connectionString);
        nextDb.Items.Add(CreateItem());
        Assert.Equal(1, await nextDb.SaveChangesAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task SyncCommitWithUiBoundChildBorrow_FailsFastThenAsyncCommitDrains()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        var transaction = await db.BeginRuntimeMutationTransactionAsync();
        var nonPumping = new NonPumpingSynchronizationContext();
        var childEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseChild = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var previous = SynchronizationContext.Current;
        Task childBorrow;
        SynchronizationContext.SetSynchronizationContext(nonPumping);
        try
        {
            childBorrow = db.ExecuteRuntimeMutationOperationAsync(async () =>
            {
                childEntered.TrySetResult();
                await releaseChild.Task;
            });
            Assert.True(childEntered.Task.IsCompleted);

            var stopwatch = Stopwatch.StartNew();
            var drainRequired = Assert.ThrowsAny<InvalidOperationException>(transaction.Commit);
            stopwatch.Stop();
            Assert.Contains("CommitAsync", drainRequired.Message, StringComparison.Ordinal);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        releaseChild.TrySetResult();
        nonPumping.DrainUntilCompleted(childBorrow, TimeSpan.FromSeconds(5));
        await childBorrow.WaitAsync(TimeSpan.FromSeconds(5));
        await transaction.CommitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await transaction.DisposeAsync();
    }

    [Fact]
    public async Task ContextSyncDisposeWithUiBoundChildBorrow_PreservesContextForAsyncRecovery()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = CreateContext(connection);
        await db.Database.EnsureCreatedAsync();
        var transaction = await db.BeginRuntimeMutationTransactionAsync();
        var nonPumping = new NonPumpingSynchronizationContext();
        var childEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseChild = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var previous = SynchronizationContext.Current;
        Task childBorrow;
        SynchronizationContext.SetSynchronizationContext(nonPumping);
        try
        {
            childBorrow = db.ExecuteRuntimeMutationOperationAsync(async () =>
            {
                childEntered.TrySetResult();
                await releaseChild.Task;
            });
            Assert.True(childEntered.Task.IsCompleted);

            var stopwatch = Stopwatch.StartNew();
            var drainRequired = Assert.ThrowsAny<InvalidOperationException>(db.Dispose);
            stopwatch.Stop();
            Assert.Contains("DisposeAsync", drainRequired.Message, StringComparison.Ordinal);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        releaseChild.TrySetResult();
        nonPumping.DrainUntilCompleted(childBorrow, TimeSpan.FromSeconds(5));
        await childBorrow.WaitAsync(TimeSpan.FromSeconds(5));
        await transaction.RollbackAsync().WaitAsync(TimeSpan.FromSeconds(5));

        db.ChangeTracker.Clear();
        db.Items.Add(CreateItem());
        Assert.Equal(1, await db.SaveChangesAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        await db.DisposeAsync();
        await transaction.DisposeAsync();
        transaction.Dispose();

        await using var nextDb = CreateContext(connection);
        nextDb.Items.Add(CreateItem());
        Assert.Equal(1, await nextDb.SaveChangesAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task AlreadyCanceledCommitAdmission_PreservesTransactionForSaveAndRollbackRetry()
    {
        await using var database = new RuntimeDatabase();
        var connectionString = database.ConnectionString;
        await using var db = CreateContext(connectionString);
        await db.Database.EnsureCreatedAsync();
        var transaction = await db.BeginRuntimeMutationTransactionAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var commit = transaction.CommitAsync(cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => commit);

        db.Items.Add(CreateItem());
        Assert.Equal(1, await db.SaveChangesAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        await transaction.RollbackAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await transaction.DisposeAsync();
    }

    [Fact]
    public async Task CanceledLifecycleAdmissionWhileWaiting_PreservesTransactionForRetry()
    {
        await using var database = new RuntimeDatabase();
        var connectionString = database.ConnectionString;
        await using var db = CreateContext(connectionString);
        await db.Database.EnsureCreatedAsync();
        var transaction = await db.BeginRuntimeMutationTransactionAsync();
        var lifecycleGate = GetLifecycleGate(transaction);
        await lifecycleGate.WaitAsync();
        try
        {
            using var cancellation = new CancellationTokenSource();
            var commit = transaction.CommitAsync(cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => commit);

            db.Items.Add(CreateItem());
            Assert.Equal(1, await db.SaveChangesAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            lifecycleGate.Release();
        }

        await transaction.RollbackAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await transaction.DisposeAsync();
    }

    [Fact]
    public async Task CommittedWrapper_ContextDisposePhysicallyDisposesProviderOnce()
    {
        await using var database = new RuntimeDatabase();
        var connectionString = database.ConnectionString;
        await using (var seedDb = CreateContext(connectionString))
            await seedDb.Database.EnsureCreatedAsync();
        var tracker = new FaultingProviderTransactionInterceptor();
        var db = CreateContext(connectionString, tracker);
        var transaction = await db.BeginRuntimeMutationTransactionAsync();
        await transaction.CommitAsync();
        Assert.Equal(0, tracker.SyncDisposeCallCount + tracker.AsyncDisposeCallCount);

        await db.DisposeAsync();
        Assert.Equal(1, tracker.AsyncDisposeCallCount);
        await transaction.DisposeAsync();
        transaction.Dispose();
        Assert.Equal(1, tracker.AsyncDisposeCallCount);
        Assert.Equal(0, tracker.SyncDisposeCallCount);
    }

    [Fact]
    public async Task SequentialCommittedWrappers_ContextDisposeCleansEveryProviderOnce()
    {
        await using var database = new RuntimeDatabase();
        var connectionString = database.ConnectionString;
        await using (var seedDb = CreateContext(connectionString))
            await seedDb.Database.EnsureCreatedAsync();
        var tracker = new FaultingProviderTransactionInterceptor();
        var db = CreateContext(connectionString, tracker);
        var transactions = new List<IDbContextTransaction>();
        for (var index = 0; index < 3; index++)
        {
            var transaction = await db.BeginRuntimeMutationTransactionAsync();
            transactions.Add(transaction);
            await transaction.CommitAsync();
        }
        Assert.Equal(0, tracker.SyncDisposeCallCount + tracker.AsyncDisposeCallCount);

        db.Dispose();
        Assert.Equal(3, tracker.SyncDisposeCallCount);
        foreach (var transaction in transactions)
        {
            transaction.Dispose();
            await transaction.DisposeAsync();
        }
        Assert.Equal(3, tracker.SyncDisposeCallCount);
        Assert.Equal(0, tracker.AsyncDisposeCallCount);
    }

    [Theory]
    [InlineData("commit", false)]
    [InlineData("commit", true)]
    [InlineData("rollback", false)]
    [InlineData("rollback", true)]
    public async Task CapturedDeferredTransactionFlow_CannotMutateAfterTransactionEnds(
        string completion,
        bool useDifferentContext)
    {
        await using var database = new RuntimeDatabase();
        var connectionString = database.ConnectionString;
        await using var rootDb = CreateContext(connectionString);
        await rootDb.Database.EnsureCreatedAsync();
        LocalDbContext? differentDb = useDifferentContext
            ? CreateContext(connectionString)
            : null;
        var childDb = differentDb ?? rootDb;
        var childItem = CreateItem();
        var startChild = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int>? childSave = null;
        try
        {
            var transaction = await rootDb.BeginRuntimeMutationTransactionAsync();
            childSave = Task.Run(async () =>
            {
                await startChild.Task;
                childDb.Items.Add(childItem);
                return await childDb.SaveChangesAsync();
            });

            if (completion == "commit")
                await transaction.CommitAsync();
            else
                await transaction.RollbackAsync();
            await transaction.DisposeAsync();

            rootDb.Items.Add(CreateItem());
            Assert.Equal(1, await rootDb.SaveChangesAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            await using (var nextTransaction =
                         await rootDb.BeginRuntimeMutationTransactionAsync())
                await nextTransaction.RollbackAsync();

            startChild.TrySetResult();
            var closedFlow = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await childSave.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains("closed", closedFlow.Message, StringComparison.OrdinalIgnoreCase);

            await using var verificationDb = CreateContext(connectionString);
            Assert.False(await verificationDb.Items.AsNoTracking()
                .AnyAsync(item => item.Id == childItem.Id));
        }
        finally
        {
            startChild.TrySetResult();
            if (childSave is not null)
            {
                try
                {
                    await childSave.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch
                {
                }
            }
            if (differentDb is not null)
                await differentDb.DisposeAsync();
        }
    }

    [Fact]
    public async Task ExplicitTransaction_DisposeWithoutCommit_ReleasesRuntimeMutationGate()
    {
        await using var database = new RuntimeDatabase();
        var connectionString = database.ConnectionString;
        await using var ownerDb = CreateContext(connectionString);
        await ownerDb.Database.EnsureCreatedAsync();

        await using (var transaction = await ownerDb.BeginRuntimeMutationTransactionAsync())
        {
            ownerDb.Items.Add(CreateItem());
            await ownerDb.SaveChangesAsync();
        }

        await using var nextDb = CreateContext(connectionString);
        nextDb.Items.Add(CreateItem());
        Assert.Equal(
            1,
            await nextDb.SaveChangesAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task RollbackFailure_DisposesProviderTransactionBeforeReleasingRuntimeGate()
    {
        await using var database = new RuntimeDatabase();
        var connectionString = database.ConnectionString;
        await using (var seedDb = CreateContext(connectionString))
            await seedDb.Database.EnsureCreatedAsync();
        await using var ownerDb = CreateContext(
            connectionString,
            new FaultingProviderTransactionInterceptor(
                syncRollbackFailure: new InvalidOperationException("runtime-rollback-fault"),
                syncDisposeFailure: new InvalidOperationException("runtime-sync-dispose-fault")));
        var transaction = await ownerDb.BeginRuntimeMutationTransactionAsync();
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(transaction.Rollback);
            Assert.Equal("runtime-rollback-fault", exception.Message);
            Assert.Contains(
                "runtime-sync-dispose-fault",
                Assert.IsAssignableFrom<Exception>(
                    exception.Data["RuntimeMutationProviderCleanupFailure"]).ToString(),
                StringComparison.Ordinal);

            await using var nextDb = CreateContext(connectionString);
            nextDb.Items.Add(CreateItem());
            Assert.Equal(
                1,
                await nextDb.SaveChangesAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            await transaction.DisposeAsync();
        }
    }

    [Fact]
    public async Task AsyncRollbackFailure_PreservesPrimaryFailureWhenProviderDisposeAlsoFails()
    {
        await using var database = new RuntimeDatabase();
        var connectionString = database.ConnectionString;
        await using (var seedDb = CreateContext(connectionString))
            await seedDb.Database.EnsureCreatedAsync();
        await using var ownerDb = CreateContext(
            connectionString,
            new FaultingProviderTransactionInterceptor(
                asyncRollbackFailure: new InvalidOperationException("runtime-async-rollback-fault"),
                asyncDisposeFailure: new InvalidOperationException("runtime-async-dispose-fault")));
        var transaction = await ownerDb.BeginRuntimeMutationTransactionAsync();
        try
        {
            var rollback = transaction.RollbackAsync();
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => rollback);
            Assert.Equal("runtime-async-rollback-fault", exception.Message);
            Assert.Contains(
                "runtime-async-dispose-fault",
                Assert.IsAssignableFrom<Exception>(
                    exception.Data["RuntimeMutationProviderCleanupFailure"]).ToString(),
                StringComparison.Ordinal);

            await using var nextDb = CreateContext(connectionString);
            nextDb.Items.Add(CreateItem());
            Assert.Equal(
                1,
                await nextDb.SaveChangesAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            await DisposeFaultingTransactionForCleanupAsync(transaction);
        }
    }

    [Fact]
    public async Task SyncCommitFailure_DisposesProviderTransactionBeforeReleasingRuntimeGate()
    {
        await using var database = new RuntimeDatabase();
        var connectionString = database.ConnectionString;
        await using (var seedDb = CreateContext(connectionString))
            await seedDb.Database.EnsureCreatedAsync();
        var tracker = new FaultingProviderTransactionInterceptor(
            syncCommitFailure: new InvalidOperationException("runtime-sync-commit-fault"),
            syncDisposeFailure: new InvalidOperationException("runtime-sync-dispose-fault"));
        await using var ownerDb = CreateContext(connectionString, tracker);
        var transaction = await ownerDb.BeginRuntimeMutationTransactionAsync();
        Task? waiter = null;
        try
        {
            var waiterStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            waiter = RunWithoutExecutionContext(async () =>
            {
                waiterStarted.TrySetResult();
                await using var lease = await LocalDbContext.AcquireRuntimeMutationGateAsync(
                    CancellationToken.None);
            });
            await waiterStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(150);
            Assert.False(waiter.IsCompleted);

            var exception = Assert.Throws<InvalidOperationException>(transaction.Commit);
            Assert.Equal("runtime-sync-commit-fault", exception.Message);
            Assert.Contains(
                "runtime-sync-dispose-fault",
                Assert.IsAssignableFrom<Exception>(
                    exception.Data["RuntimeMutationProviderCleanupFailure"]).ToString(),
                StringComparison.Ordinal);
            Assert.Equal(1, tracker.SyncRollbackCallCount);
            Assert.Equal(1, tracker.SyncDisposeCallCount);
            transaction.Rollback();
            transaction.Rollback();
            Assert.Equal(1, tracker.SyncRollbackCallCount);
            Assert.Throws<InvalidOperationException>(transaction.Commit);
            Assert.Throws<InvalidOperationException>(() => transaction.CreateSavepoint("after-failed-commit"));
            await waiter.WaitAsync(TimeSpan.FromSeconds(5));

            await using var nextDb = CreateContext(connectionString);
            nextDb.Items.Add(CreateItem());
            Assert.Equal(
                1,
                await nextDb.SaveChangesAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            await transaction.DisposeAsync();
            if (waiter is not null)
                await waiter.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task AsyncCommitFailure_DisposesProviderTransactionBeforeReleasingRuntimeGate()
    {
        await using var database = new RuntimeDatabase();
        var connectionString = database.ConnectionString;
        await using (var seedDb = CreateContext(connectionString))
            await seedDb.Database.EnsureCreatedAsync();
        var tracker = new FaultingProviderTransactionInterceptor(
            asyncCommitFailure: new InvalidOperationException("runtime-async-commit-fault"),
            asyncRollbackFailure: new InvalidOperationException("runtime-async-rollback-fault"),
            asyncDisposeFailure: new InvalidOperationException("runtime-async-dispose-fault"));
        await using var ownerDb = CreateContext(connectionString, tracker);
        var transaction = await ownerDb.BeginRuntimeMutationTransactionAsync();
        Task? waiter = null;
        try
        {
            var waiterStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            waiter = RunWithoutExecutionContext(async () =>
            {
                waiterStarted.TrySetResult();
                await using var lease = await LocalDbContext.AcquireRuntimeMutationGateAsync(
                    CancellationToken.None);
            });
            await waiterStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(150);
            Assert.False(waiter.IsCompleted);

            var commit = transaction.CommitAsync();
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => commit);
            Assert.Equal("runtime-async-commit-fault", exception.Message);
            Assert.Contains(
                "runtime-async-rollback-fault",
                Assert.IsAssignableFrom<Exception>(
                    exception.Data["RuntimeMutationProviderCleanupFailure"]).ToString(),
                StringComparison.Ordinal);
            Assert.Contains(
                "runtime-async-dispose-fault",
                Assert.IsAssignableFrom<Exception>(
                    exception.Data["RuntimeMutationProviderCleanupFailure"]).ToString(),
                StringComparison.Ordinal);
            Assert.Equal(1, tracker.AsyncRollbackCallCount);
            Assert.Equal(1, tracker.AsyncDisposeCallCount);
            await transaction.RollbackAsync(CancellationToken.None);
            await transaction.RollbackAsync(CancellationToken.None);
            Assert.Equal(1, tracker.AsyncRollbackCallCount);
            await waiter.WaitAsync(TimeSpan.FromSeconds(5));

            await using var nextDb = CreateContext(connectionString);
            nextDb.Items.Add(CreateItem());
            Assert.Equal(
                1,
                await nextDb.SaveChangesAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            await DisposeFaultingTransactionForCleanupAsync(transaction);
            if (waiter is not null)
                await waiter.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task AsyncCommitCancellation_DisposesProviderTransactionBeforeReleasingRuntimeGate()
    {
        await using var database = new RuntimeDatabase();
        var connectionString = database.ConnectionString;
        await using (var seedDb = CreateContext(connectionString))
            await seedDb.Database.EnsureCreatedAsync();
        var tracker = new FaultingProviderTransactionInterceptor(
            asyncCommitFailure: new OperationCanceledException("runtime-async-commit-canceled"),
            asyncDisposeFailure: new InvalidOperationException("runtime-async-dispose-fault"));
        await using var ownerDb = CreateContext(connectionString, tracker);
        var transaction = await ownerDb.BeginRuntimeMutationTransactionAsync();
        Task? waiter = null;
        try
        {
            var waiterStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            waiter = RunWithoutExecutionContext(async () =>
            {
                waiterStarted.TrySetResult();
                await using var lease = await LocalDbContext.AcquireRuntimeMutationGateAsync(
                    CancellationToken.None);
            });
            await waiterStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(150);
            Assert.False(waiter.IsCompleted);

            var commit = transaction.CommitAsync();
            var exception = await Assert.ThrowsAsync<OperationCanceledException>(
                () => commit);
            Assert.Equal("runtime-async-commit-canceled", exception.Message);
            Assert.Contains(
                "runtime-async-dispose-fault",
                Assert.IsAssignableFrom<Exception>(
                    exception.Data["RuntimeMutationProviderCleanupFailure"]).ToString(),
                StringComparison.Ordinal);
            Assert.Equal(1, tracker.AsyncRollbackCallCount);
            Assert.Equal(1, tracker.AsyncDisposeCallCount);
            using var canceledRollback = new CancellationTokenSource();
            canceledRollback.Cancel();
            await transaction.RollbackAsync(canceledRollback.Token);
            Assert.Equal(1, tracker.AsyncRollbackCallCount);
            await waiter.WaitAsync(TimeSpan.FromSeconds(5));

            await using var nextDb = CreateContext(connectionString);
            nextDb.Items.Add(CreateItem());
            Assert.Equal(
                1,
                await nextDb.SaveChangesAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            await DisposeFaultingTransactionForCleanupAsync(transaction);
            if (waiter is not null)
                await waiter.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task SuccessfulCommit_SubsequentRollbackAndSavepointRemainRejected()
    {
        await using var database = new RuntimeDatabase();
        await using var db = CreateContext(database.ConnectionString);
        await db.Database.EnsureCreatedAsync();
        var transaction = await db.BeginRuntimeMutationTransactionAsync();

        db.Items.Add(CreateItem());
        Assert.Equal(1, await db.SaveChangesAsync());
        await transaction.CommitAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => transaction.RollbackAsync());
        Assert.Throws<InvalidOperationException>(transaction.Rollback);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => transaction.CreateSavepointAsync("after-successful-commit"));
        await transaction.DisposeAsync();
    }

    [Fact]
    public async Task SyncDisposeFailure_QuarantinesConnectionBeforeReleasingRuntimeGate()
    {
        await using var database = new RuntimeDatabase();
        var connectionString = database.ConnectionString;
        await using (var seedDb = CreateContext(connectionString))
            await seedDb.Database.EnsureCreatedAsync();
        await using var ownerDb = CreateContext(
            connectionString,
            new FaultingProviderTransactionInterceptor(
                syncDisposeFailure: new InvalidOperationException("runtime-sync-dispose-fault")));
        var transaction = await ownerDb.BeginRuntimeMutationTransactionAsync();

        var exception = Assert.Throws<InvalidOperationException>(transaction.Dispose);
        Assert.Equal("runtime-sync-dispose-fault", exception.Message);

        await using var nextDb = CreateContext(connectionString);
        nextDb.Items.Add(CreateItem());
        Assert.Equal(
            1,
            await nextDb.SaveChangesAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        await transaction.DisposeAsync();
    }

    [Fact]
    public async Task AsyncDisposeFailure_QuarantinesConnectionBeforeReleasingRuntimeGate()
    {
        await using var database = new RuntimeDatabase();
        var connectionString = database.ConnectionString;
        await using (var seedDb = CreateContext(connectionString))
            await seedDb.Database.EnsureCreatedAsync();
        await using var ownerDb = CreateContext(
            connectionString,
            new FaultingProviderTransactionInterceptor(
                asyncDisposeFailure: new InvalidOperationException("runtime-async-dispose-fault")));
        var transaction = await ownerDb.BeginRuntimeMutationTransactionAsync();

        var dispose = transaction.DisposeAsync().AsTask();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => dispose);
        Assert.Equal("runtime-async-dispose-fault", exception.Message);

        await using var nextDb = CreateContext(connectionString);
        nextDb.Items.Add(CreateItem());
        Assert.Equal(
            1,
            await nextDb.SaveChangesAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        await transaction.DisposeAsync();
    }

    [Fact]
    public async Task UnconfirmedProviderTermination_FaultsDatabaseGuardWithoutBlockingOtherDatabases()
    {
        await using var database = new RuntimeDatabase();
        var connectionString = database.ConnectionString;
        await using (var seedDb = CreateContext(connectionString))
            await seedDb.Database.EnsureCreatedAsync();
        var closeFault = new ThrowingFirstConnectionCloseInterceptor();
        await using var ownerDb = CreateContext(
            connectionString,
            new FaultingProviderTransactionInterceptor(
                asyncCommitFailure: new InvalidOperationException("runtime-async-commit-fault"),
                asyncRollbackFailure: new InvalidOperationException("runtime-async-rollback-fault"),
                asyncDisposeFailure: new InvalidOperationException("runtime-async-dispose-fault")),
            closeFault);
        var transaction = await ownerDb.BeginRuntimeMutationTransactionAsync();
        await using var sameDatabaseWaiter = CreateContext(connectionString);
        sameDatabaseWaiter.Items.Add(CreateItem());
        var waiterStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var waiter = RunWithoutExecutionContext(async () =>
        {
            waiterStarted.TrySetResult();
            await sameDatabaseWaiter.SaveChangesAsync();
        });

        try
        {
            await waiterStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(150);
            Assert.False(waiter.IsCompleted);

            var commit = transaction.CommitAsync();
            var primary = await Assert.ThrowsAsync<InvalidOperationException>(
                () => commit);
            Assert.Equal("runtime-async-commit-fault", primary.Message);
            Assert.Contains(
                "runtime-async-rollback-fault",
                Assert.IsAssignableFrom<Exception>(
                    primary.Data["RuntimeMutationProviderCleanupFailure"]).ToString(),
                StringComparison.Ordinal);
            Assert.Contains(
                "runtime-connection-close-fault",
                Assert.IsAssignableFrom<Exception>(
                    primary.Data["RuntimeMutationProviderCleanupFailure"]).ToString(),
                StringComparison.Ordinal);

            var guardFailure = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await waiter.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains("안전 정지", guardFailure.Message, StringComparison.Ordinal);

            await using var otherDatabase = new RuntimeDatabase();
            await using var otherDb = CreateContext(otherDatabase.ConnectionString);
            await otherDb.Database.EnsureCreatedAsync();
            otherDb.Items.Add(CreateItem());
            Assert.Equal(
                1,
                await otherDb.SaveChangesAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            await transaction.DisposeAsync();
            try
            {
                await waiter.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("-- runtime mutation guard comment\r\n")]
    [InlineData("/* runtime mutation guard comment */ ")]
    public async Task ReaderBasedRawMutation_IsRejectedWhileNormalSaveRemainsAvailable(
        string sqlPrefix)
    {
        await using var database = new RuntimeDatabase();
        var connectionString = database.ConnectionString;
        await using var db = CreateContext(connectionString);
        await db.Database.EnsureCreatedAsync();
        var item = CreateItem();
        db.Items.Add(item);
        Assert.Equal(1, await db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await db.Database.SqlQueryRaw<int>(
                    sqlPrefix +
                    "UPDATE \"Items\" " +
                    "SET \"NameOriginal\" = 'unsupported reader mutation' " +
                    "WHERE \"Id\" = {0} RETURNING 1 AS \"Value\";",
                    item.Id)
                .ToListAsync());
        Assert.Contains("Reader-based SQL mutations", exception.Message, StringComparison.Ordinal);

        var persisted = await db.Items.SingleAsync(candidate => candidate.Id == item.Id);
        Assert.NotEqual("unsupported reader mutation", persisted.NameOriginal);
        persisted.NameOriginal = "supported normal save";
        persisted.IsDirty = true;
        Assert.Equal(1, await db.SaveChangesAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ReaderBasedCteMutation_IsRejectedFailClosed()
    {
        await using var database = new RuntimeDatabase();
        var connectionString = database.ConnectionString;
        await using var db = CreateContext(connectionString);
        await db.Database.EnsureCreatedAsync();
        var item = CreateItem();
        db.Items.Add(item);
        Assert.Equal(1, await db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await db.Database.SqlQueryRaw<int>(
                    """
                    WITH "TargetItems" AS (
                        SELECT "Id" FROM "Items" WHERE "Id" = {0}
                    )
                    UPDATE "Items"
                    SET "NameOriginal" = 'unsupported cte reader mutation'
                    WHERE "Id" IN (SELECT "Id" FROM "TargetItems")
                    RETURNING 1 AS "Value";
                    """,
                    item.Id)
                .ToListAsync());
        Assert.Contains("Reader-based SQL mutations", exception.Message, StringComparison.Ordinal);

        var persisted = await db.Items.SingleAsync(candidate => candidate.Id == item.Id);
        Assert.NotEqual("unsupported cte reader mutation", persisted.NameOriginal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResultReaderMutations_AreRejectedBeforeSchemaHeaderOrRowsCanChange(
        bool async)
    {
        await using var database = new RuntimeDatabase();
        var connectionString = database.ConnectionString;
        await using var db = CreateContext(connectionString);
        await db.Database.EnsureCreatedAsync();
        var item = CreateItem();
        db.Items.Add(item);
        Assert.Equal(1, await db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        var mutationCommands = new[]
        {
            "CREATE TABLE \"UnsupportedReaderSchema\" (\"Id\" INTEGER);",
            "PRAGMA application_id = 73421;",
            $"UPDATE \"Items\" SET \"NameOriginal\" = 'reader mutation' " +
                $"WHERE \"Id\" = '{item.Id:D}' RETURNING 1;",
            $"WITH \"SELECT\" AS (SELECT \"Id\" FROM \"Items\") " +
                $"UPDATE \"Items\" SET \"NameOriginal\" = 'quoted cte mutation' " +
                $"WHERE \"Id\" = '{item.Id:D}' RETURNING 1;",
            $"SELECT 1; UPDATE \"Items\" SET \"NameOriginal\" = 'multi statement' " +
                $"WHERE \"Id\" = '{item.Id:D}' RETURNING 1;"
        };

        foreach (var sql in mutationCommands)
        {
            var exception = async
                ? await Assert.ThrowsAsync<InvalidOperationException>(
                    () => ExecuteRelationalReaderAsync(db, sql))
                : Assert.Throws<InvalidOperationException>(
                    () => ExecuteRelationalReader(db, sql));
            Assert.Contains("Reader-based SQL mutations", exception.Message, StringComparison.Ordinal);
        }

        Assert.Equal(
            0L,
            Convert.ToInt64(await ExecuteRelationalScalarAsync(
                db,
                "SELECT COUNT(*) FROM sqlite_master " +
                "WHERE type = 'table' AND name = 'UnsupportedReaderSchema';")));
        Assert.Equal(0L, Convert.ToInt64(await ExecuteRelationalScalarAsync(
            db,
            "PRAGMA application_id;")));
        Assert.Equal(
            item.NameOriginal,
            await db.Items.AsNoTracking()
                .Where(candidate => candidate.Id == item.Id)
                .Select(candidate => candidate.NameOriginal)
                .SingleAsync());
    }

    [Theory]
    [InlineData("CREATE TABLE \"UnsupportedScalarSchema\" (\"Id\" INTEGER);", false)]
    [InlineData("CREATE TABLE \"UnsupportedScalarSchema\" (\"Id\" INTEGER);", true)]
    [InlineData("PRAGMA application_id = 73422;", false)]
    [InlineData("PRAGMA application_id = 73422;", true)]
    [InlineData("PRAGMA journal_mode(WAL);", false)]
    [InlineData("PRAGMA journal_mode(WAL);", true)]
    [InlineData("PRAGMA wal_checkpoint(TRUNCATE);", false)]
    [InlineData("PRAGMA wal_checkpoint(TRUNCATE);", true)]
    [InlineData("ATTACH DATABASE ':memory:' AS \"UnsupportedScalarAttachment\";", false)]
    [InlineData("ATTACH DATABASE ':memory:' AS \"UnsupportedScalarAttachment\";", true)]
    [InlineData("DETACH DATABASE main;", false)]
    [InlineData("DETACH DATABASE main;", true)]
    [InlineData("VACUUM;", false)]
    [InlineData("VACUUM;", true)]
    [InlineData("REINDEX;", false)]
    [InlineData("REINDEX;", true)]
    [InlineData("ANALYZE;", false)]
    [InlineData("ANALYZE;", true)]
    public async Task ScalarMutations_AreRejectedBeforeSchemaOrHeaderCanChange(
        string sql,
        bool async)
    {
        await using var database = new RuntimeDatabase();
        await using var db = CreateContext(database.ConnectionString);
        await db.Database.EnsureCreatedAsync();

        var exception = async
            ? await Assert.ThrowsAsync<InvalidOperationException>(
                () => ExecuteRelationalScalarAsync(db, sql))
            : Assert.Throws<InvalidOperationException>(
                () => ExecuteRelationalScalar(db, sql));
        Assert.Contains("scalar SQL mutations", exception.Message, StringComparison.Ordinal);

        Assert.Equal(
            0L,
            Convert.ToInt64(await ExecuteRelationalScalarAsync(
                db,
                "SELECT COUNT(*) FROM sqlite_master " +
                "WHERE type = 'table' AND name = 'UnsupportedScalarSchema';")));
        Assert.Equal(0L, Convert.ToInt64(await ExecuteRelationalScalarAsync(
            db,
            "PRAGMA application_id;")));
        Assert.Equal(
            0L,
            Convert.ToInt64(await ExecuteRelationalScalarAsync(
                db,
                "SELECT COUNT(*) FROM pragma_database_list " +
                "WHERE name = 'UnsupportedScalarAttachment';")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadOnlyCteExplainAndPragma_AreAllowed_AndNormalSaveStillWorks(
        bool async)
    {
        await using var database = new RuntimeDatabase();
        await using var db = CreateContext(database.ConnectionString);
        await db.Database.EnsureCreatedAsync();

        var cteSql =
            "WITH \"Source\"(\"Value\") AS (SELECT 41) " +
            "SELECT \"Value\" + 1 FROM \"Source\";";
        var cteValue = async
            ? await ExecuteRelationalScalarAsync(db, cteSql)
            : ExecuteRelationalScalar(db, cteSql);
        Assert.Equal(42L, Convert.ToInt64(cteValue));

        var pragmaValue = async
            ? await ExecuteRelationalScalarAsync(db, "PRAGMA application_id;")
            : ExecuteRelationalScalar(db, "PRAGMA application_id;");
        Assert.Equal(0L, Convert.ToInt64(pragmaValue));

        var semicolonLiteral = async
            ? await ExecuteRelationalScalarAsync(
                db,
                "/* read-only prefix */ SELECT '; UPDATE Items' AS \"Value\";")
            : ExecuteRelationalScalar(
                db,
                "/* read-only prefix */ SELECT '; UPDATE Items' AS \"Value\";");
        Assert.Equal("; UPDATE Items", Convert.ToString(semicolonLiteral));

        if (async)
        {
            await ExecuteRelationalReaderAsync(db, "PRAGMA table_info(\"Items\");");
            await ExecuteRelationalReaderAsync(db, "EXPLAIN SELECT 1;");
        }
        else
        {
            ExecuteRelationalReader(db, "PRAGMA table_info(\"Items\");");
            ExecuteRelationalReader(db, "EXPLAIN SELECT 1;");
        }

        db.Items.Add(CreateItem());
        Assert.Equal(1, await db.SaveChangesAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task FailedDirectNonQuery_ReleasesRuntimeMutationGateForSubsequentSave()
    {
        await using var database = new RuntimeDatabase();
        var connectionString = database.ConnectionString;
        await using var db = CreateContext(connectionString);
        await db.Database.EnsureCreatedAsync();

        await Assert.ThrowsAsync<SqliteException>(
            async () => await db.Database.ExecuteSqlRawAsync(
                "UPDATE MissingRuntimeMutationTable SET MissingColumn = 1;"));

        db.Items.Add(CreateItem());
        Assert.Equal(
            1,
            await db.SaveChangesAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task OwnerScopeCapturedByDeferredTask_ExpiresAndCannotBypassRuntimeGate()
    {
        await using var database = new RuntimeDatabase();
        var connectionString = database.ConnectionString;
        await using (var seedDb = CreateContext(connectionString))
            await seedDb.Database.EnsureCreatedAsync();

        await using var deferredDb = CreateContext(connectionString);
        deferredDb.Items.Add(CreateItem());
        var startDeferredSave = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var deferredSaveStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> deferredSave;

        await using (var rootLease =
                     await LocalDbContext.AcquireRuntimeMutationGateAsync(CancellationToken.None))
        {
            await using (LocalDbContext.EnterRuntimeMutationGateOwnerScope(rootLease))
            {
                deferredSave = Task.Run(async () =>
                {
                    await startDeferredSave.Task;
                    deferredSaveStarted.TrySetResult();
                    return await deferredDb.SaveChangesAsync();
                });
            }

            startDeferredSave.TrySetResult();
            await deferredSaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(150);
            Assert.False(deferredSave.IsCompleted);
        }

        Assert.Equal(
            1,
            await deferredSave.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData("execute-update")]
    [InlineData("raw-sql")]
    public async Task DirectSetBasedMutation_WaitsForRuntimeGate_ThenRejectsStaleEpoch(
        string mutationKind)
    {
        await using var database = new RuntimeDatabase();
        var connectionString = database.ConnectionString;
        var item = CreateItem();
        await using (var seedDb = CreateContext(connectionString))
        {
            await seedDb.Database.EnsureCreatedAsync();
            seedDb.Items.Add(item);
            await seedDb.SaveChangesAsync();
        }

        await using var staleDb = CreateContext(connectionString);
        var startMutation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var mutationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var directMutation = Task.Run(async () =>
        {
            await startMutation.Task;
            mutationStarted.TrySetResult();
            if (mutationKind == "execute-update")
            {
                return await staleDb.Items
                    .Where(current => current.Id == item.Id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(
                        current => current.SimpleMemo,
                        "stale-direct"));
            }

            return await staleDb.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "Items"
                SET "SimpleMemo" = {"stale-direct"}
                WHERE "Id" = {item.Id}
                """);
        });

        await using (var runtimeLease =
                     await LocalDbContext.AcquireRuntimeMutationGateAsync(CancellationToken.None))
        await using (LocalDbContext.EnterRuntimeMutationGateOwnerScope(runtimeLease))
        {
            startMutation.TrySetResult();
            await mutationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(150);
            Assert.False(directMutation.IsCompleted);

            await using var freshDb = CreateContext(connectionString);
            var freshItem = await freshDb.Items.SingleAsync(current => current.Id == item.Id);
            freshItem.SimpleMemo = "server-fresh";
            await freshDb.SaveChangesAsync();
            freshDb.AdvanceRuntimeMutationEpoch();
        }

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            async () => await directMutation.WaitAsync(TimeSpan.FromSeconds(5)));

        await using var verificationDb = CreateContext(connectionString);
        Assert.Equal(
            "server-fresh",
            await verificationDb.Items.AsNoTracking()
                .Where(current => current.Id == item.Id)
                .Select(current => current.SimpleMemo)
                .SingleAsync());
    }

    private static LocalDbContext CreateContext(
        string connectionString,
        params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connectionString);
        if (interceptors.Length > 0)
            options.AddInterceptors(interceptors);
        return new LocalDbContext(options.Options);
    }

    private static LocalDbContext CreateContext(
        DbConnection connection,
        params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection);
        if (interceptors.Length > 0)
            options.AddInterceptors(interceptors);
        return new LocalDbContext(options.Options);
    }

    private static object? ExecuteRelationalScalar(LocalDbContext db, string sql)
    {
        var (command, parameterObject) = BuildRelationalCommand(db, sql);
        return command.RelationalCommand.ExecuteScalar(parameterObject);
    }

    private static Task<object?> ExecuteRelationalScalarAsync(LocalDbContext db, string sql)
    {
        var (command, parameterObject) = BuildRelationalCommand(db, sql);
        return command.RelationalCommand.ExecuteScalarAsync(parameterObject);
    }

    private static void ExecuteRelationalReader(LocalDbContext db, string sql)
    {
        var (command, parameterObject) = BuildRelationalCommand(db, sql);
        using var reader = command.RelationalCommand.ExecuteReader(parameterObject);
        while (reader.DbDataReader.Read())
        {
        }
    }

    private static async Task ExecuteRelationalReaderAsync(LocalDbContext db, string sql)
    {
        var (command, parameterObject) = BuildRelationalCommand(db, sql);
        await using var reader = await command.RelationalCommand.ExecuteReaderAsync(parameterObject);
        while (await reader.DbDataReader.ReadAsync())
        {
        }
    }

    private static (RawSqlCommand Command, RelationalCommandParameterObject ParameterObject)
        BuildRelationalCommand(LocalDbContext db, string sql)
    {
        var command = db.GetService<IRawSqlCommandBuilder>().Build(
            sql,
            Array.Empty<object>());
        var parameterObject = new RelationalCommandParameterObject(
            db.GetService<IRelationalConnection>(),
            command.ParameterValues,
            readerColumns: null,
            db,
            db.GetService<IRelationalCommandDiagnosticsLogger>(),
            CommandSource.Unknown);
        return (command, parameterObject);
    }

    private static Task RunWithoutExecutionContext(Func<Task> action)
    {
        using (ExecutionContext.SuppressFlow())
            return Task.Run(action);
    }

    private static SemaphoreSlim GetLifecycleGate(IDbContextTransaction transaction)
        => Assert.IsType<SemaphoreSlim>(
            transaction.GetType()
                .GetField("_lifecycleGate", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(transaction));

    private static async Task DisposeFaultingTransactionForCleanupAsync(
        IDbContextTransaction transaction)
    {
        try
        {
            await transaction.DisposeAsync();
        }
        catch (InvalidOperationException exception)
            when (exception.Message.Contains("dispose-fault", StringComparison.Ordinal))
        {
        }
    }

    private static LocalItem CreateItem()
        => new()
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "runtime mutation guard item",
            NameMatchKey = "runtime mutation guard item",
            SpecificationOriginal = "A4",
            SpecificationMatchKey = "A4",
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 0m,
            IsDirty = false
        };

    private sealed class RuntimeDatabase : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            $"georaeplan-runtime-mutation-{Guid.NewGuid():N}");

        public RuntimeDatabase()
        {
            Directory.CreateDirectory(_directory);
            ConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(_directory, "runtime.db"),
                Pooling = false
            }.ConnectionString;
        }

        public string ConnectionString { get; }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingTransactionStartedInterceptor : DbTransactionInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<DbTransaction> TransactionStartedAsync(
            DbConnection connection,
            TransactionEndEventData eventData,
            DbTransaction result,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return result;
        }
    }

    private sealed class NonPumpingSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _callbacks = new();

        public override void Post(SendOrPostCallback d, object? state)
            => _callbacks.Enqueue((d, state));

        public void DrainUntilCompleted(Task task, TimeSpan timeout)
        {
            var deadline = Stopwatch.StartNew();
            while (!task.IsCompleted)
            {
                if (deadline.Elapsed > timeout)
                    throw new TimeoutException("UI-bound continuation did not drain in time.");
                if (!_callbacks.TryDequeue(out var work))
                {
                    Thread.Sleep(5);
                    continue;
                }

                var previous = Current;
                SetSynchronizationContext(this);
                try
                {
                    work.Callback(work.State);
                }
                finally
                {
                    SetSynchronizationContext(previous);
                }
            }
        }
    }

    private sealed class BlockingTransactionCommitInterceptor : DbTransactionInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return result;
        }
    }

    private sealed class ThrowingFirstTransactionStartingInterceptor
        : DbTransactionInterceptor
    {
        private int _failurePending = 1;

        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection,
            TransactionStartingEventData eventData,
            InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken = default)
            => Interlocked.Exchange(ref _failurePending, 0) != 0
                ? ValueTask.FromException<InterceptionResult<DbTransaction>>(
                    new InvalidOperationException("runtime-before-provider-start-fault"))
                : ValueTask.FromResult(result);
    }

    private sealed class ThrowingFirstTransactionStartedInterceptor
        : DbTransactionInterceptor
    {
        private int _failurePending = 1;

        public override ValueTask<DbTransaction> TransactionStartedAsync(
            DbConnection connection,
            TransactionEndEventData eventData,
            DbTransaction result,
            CancellationToken cancellationToken = default)
            => Interlocked.Exchange(ref _failurePending, 0) != 0
                ? ValueTask.FromException<DbTransaction>(
                    new InvalidOperationException("runtime-after-provider-start-fault"))
                : ValueTask.FromResult(result);
    }

    private sealed class FaultingProviderTransactionInterceptor(
        Exception? syncCommitFailure = null,
        Exception? asyncCommitFailure = null,
        Exception? syncRollbackFailure = null,
        Exception? asyncRollbackFailure = null,
        Exception? syncDisposeFailure = null,
        Exception? asyncDisposeFailure = null)
        : DbTransactionInterceptor
    {
        private int _asyncDisposeCallCount;
        private int _asyncRollbackCallCount;
        private int _syncDisposeCallCount;
        private int _syncRollbackCallCount;

        public int AsyncDisposeCallCount => Volatile.Read(ref _asyncDisposeCallCount);
        public int AsyncRollbackCallCount => Volatile.Read(ref _asyncRollbackCallCount);
        public int SyncDisposeCallCount => Volatile.Read(ref _syncDisposeCallCount);
        public int SyncRollbackCallCount => Volatile.Read(ref _syncRollbackCallCount);

        public override DbTransaction TransactionStarted(
            DbConnection connection,
            TransactionEndEventData eventData,
            DbTransaction result)
            => Wrap(result);

        public override ValueTask<DbTransaction> TransactionStartedAsync(
            DbConnection connection,
            TransactionEndEventData eventData,
            DbTransaction result,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Wrap(result));

        public override InterceptionResult TransactionCommitting(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result)
            => syncCommitFailure is null
                ? result
                : throw syncCommitFailure;

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
            => asyncCommitFailure is null
                ? ValueTask.FromResult(result)
                : ValueTask.FromException<InterceptionResult>(asyncCommitFailure);

        public override InterceptionResult TransactionRollingBack(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result)
        {
            Interlocked.Increment(ref _syncRollbackCallCount);
            return syncRollbackFailure is null
                ? result
                : throw syncRollbackFailure;
        }

        public override ValueTask<InterceptionResult> TransactionRollingBackAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _asyncRollbackCallCount);
            return asyncRollbackFailure is null
                ? ValueTask.FromResult(result)
                : ValueTask.FromException<InterceptionResult>(asyncRollbackFailure);
        }

        private DbTransaction Wrap(DbTransaction transaction)
            => transaction is FaultingDisposeDbTransaction
                ? transaction
                : new FaultingDisposeDbTransaction(
                    transaction,
                    syncDisposeFailure,
                    asyncDisposeFailure,
                    () => Interlocked.Increment(ref _syncDisposeCallCount),
                    () => Interlocked.Increment(ref _asyncDisposeCallCount));
    }

    private sealed class FaultingDisposeDbTransaction(
        DbTransaction inner,
        Exception? syncDisposeFailure,
        Exception? asyncDisposeFailure,
        Action syncDisposeCalled,
        Action asyncDisposeCalled)
        : DbTransaction
    {
        private int _syncDisposeFailurePending = syncDisposeFailure is null ? 0 : 1;
        private int _asyncDisposeFailurePending = asyncDisposeFailure is null ? 0 : 1;

        public override IsolationLevel IsolationLevel => inner.IsolationLevel;
        protected override DbConnection? DbConnection => inner.Connection;
        public override bool SupportsSavepoints => inner.SupportsSavepoints;

        public override void Commit() => inner.Commit();

        public override Task CommitAsync(CancellationToken cancellationToken = default)
            => inner.CommitAsync(cancellationToken);

        public override void Rollback() => inner.Rollback();

        public override Task RollbackAsync(CancellationToken cancellationToken = default)
            => inner.RollbackAsync(cancellationToken);

        public override void Save(string savepointName) => inner.Save(savepointName);

        public override Task SaveAsync(
            string savepointName,
            CancellationToken cancellationToken = default)
            => inner.SaveAsync(savepointName, cancellationToken);

        public override void Rollback(string savepointName)
            => inner.Rollback(savepointName);

        public override Task RollbackAsync(
            string savepointName,
            CancellationToken cancellationToken = default)
            => inner.RollbackAsync(savepointName, cancellationToken);

        public override void Release(string savepointName) => inner.Release(savepointName);

        public override Task ReleaseAsync(
            string savepointName,
            CancellationToken cancellationToken = default)
            => inner.ReleaseAsync(savepointName, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (!disposing)
                return;
            syncDisposeCalled();
            if (Interlocked.Exchange(ref _syncDisposeFailurePending, 0) != 0)
                throw syncDisposeFailure!;
            inner.Dispose();
        }

        public override ValueTask DisposeAsync()
        {
            asyncDisposeCalled();
            if (Interlocked.Exchange(ref _asyncDisposeFailurePending, 0) != 0)
                return ValueTask.FromException(asyncDisposeFailure!);
            return inner.DisposeAsync();
        }
    }

    private sealed class ThrowingFirstConnectionCloseInterceptor : DbConnectionInterceptor
    {
        private int _failurePending = 1;

        public override InterceptionResult ConnectionClosing(
            DbConnection connection,
            ConnectionEventData eventData,
            InterceptionResult result)
            => Interlocked.Exchange(ref _failurePending, 0) != 0
                ? throw new InvalidOperationException("runtime-connection-close-fault")
                : result;

        public override ValueTask<InterceptionResult> ConnectionClosingAsync(
            DbConnection connection,
            ConnectionEventData eventData,
            InterceptionResult result)
            => Interlocked.Exchange(ref _failurePending, 0) != 0
                ? ValueTask.FromException<InterceptionResult>(
                    new InvalidOperationException("runtime-connection-close-fault"))
                : ValueTask.FromResult(result);
    }

    private sealed class BlockingNonQueryInterceptor : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return result;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return result;
        }
    }
}
