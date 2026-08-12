using System.Collections.Concurrent;
using 거래플랜.Desktop.App.Infrastructure;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class UiAsyncRefreshCoalescerTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task RequestDuringActiveRefresh_CoalescesBurstAndReplaysExactlyOnce()
    {
        var firstEntered = CreateSignal();
        var releaseFirst = CreateSignal();
        var secondEntered = CreateSignal();
        var releaseSecond = CreateSignal();
        var observedTasks = new ConcurrentQueue<Task>();
        var refreshCount = 0;
        var activeRefreshCount = 0;
        var maximumConcurrentRefreshCount = 0;

        async Task RefreshAsync()
        {
            var call = Interlocked.Increment(ref refreshCount);
            var active = Interlocked.Increment(ref activeRefreshCount);
            UpdateMaximum(ref maximumConcurrentRefreshCount, active);

            try
            {
                if (call == 1)
                {
                    firstEntered.TrySetResult();
                    await releaseFirst.Task;
                    return;
                }

                if (call == 2)
                {
                    secondEntered.TrySetResult();
                    await releaseSecond.Task;
                    return;
                }

                throw new InvalidOperationException(
                    $"Expected two coalesced refreshes, but refresh {call} started.");
            }
            finally
            {
                Interlocked.Decrement(ref activeRefreshCount);
            }
        }

        using var coalescer = new UiAsyncRefreshCoalescer(
            RefreshAsync,
            task => observedTasks.Enqueue(task));

        coalescer.Request();
        await firstEntered.Task.WaitAsync(TestTimeout);

        coalescer.Request();
        coalescer.Request();
        coalescer.Request();

        Assert.Equal(1, Volatile.Read(ref refreshCount));
        Assert.True(coalescer.IsRunning);
        Assert.True(coalescer.HasPendingRefresh);

        releaseFirst.TrySetResult();
        await secondEntered.Task.WaitAsync(TestTimeout);

        Assert.Equal(2, Volatile.Read(ref refreshCount));
        Assert.Equal(1, Volatile.Read(ref maximumConcurrentRefreshCount));

        releaseSecond.TrySetResult();
        var drainTask = Assert.Single(observedTasks);
        await drainTask.WaitAsync(TestTimeout);

        Assert.Equal(2, Volatile.Read(ref refreshCount));
        Assert.Equal(1, Volatile.Read(ref maximumConcurrentRefreshCount));
        Assert.False(coalescer.IsRunning);
        Assert.False(coalescer.HasPendingRefresh);
    }

    [Fact]
    public async Task DisposeDuringActiveRefresh_DropsPendingReplayAndRejectsLaterRequests()
    {
        var firstEntered = CreateSignal();
        var releaseFirst = CreateSignal();
        var observedTasks = new ConcurrentQueue<Task>();
        var refreshCount = 0;

        async Task RefreshAsync()
        {
            var call = Interlocked.Increment(ref refreshCount);
            if (call != 1)
            {
                throw new InvalidOperationException(
                    $"A disposed coalescer started unexpected refresh {call}.");
            }

            firstEntered.TrySetResult();
            await releaseFirst.Task;
        }

        var coalescer = new UiAsyncRefreshCoalescer(
            RefreshAsync,
            task => observedTasks.Enqueue(task));

        coalescer.Request();
        await firstEntered.Task.WaitAsync(TestTimeout);

        coalescer.Request();
        Assert.True(coalescer.HasPendingRefresh);

        coalescer.Dispose();
        coalescer.Request();

        Assert.True(coalescer.IsDisposed);
        Assert.False(coalescer.HasPendingRefresh);

        releaseFirst.TrySetResult();
        var drainTask = Assert.Single(observedTasks);
        await drainTask.WaitAsync(TestTimeout);

        Assert.Equal(1, Volatile.Read(ref refreshCount));
        Assert.False(coalescer.IsRunning);
        Assert.False(coalescer.HasPendingRefresh);

        coalescer.Dispose();
        coalescer.Request();

        Assert.Equal(1, Volatile.Read(ref refreshCount));
        Assert.Same(drainTask, Assert.Single(observedTasks));
    }

    [Fact]
    public async Task RequestAtOwnerReleaseBoundary_StartsReplayWithoutOverlapOrLoss()
    {
        using var ownerReleaseReached = new ManualResetEventSlim();
        using var allowOwnerRelease = new ManualResetEventSlim();
        var secondEntered = CreateSignal();
        var releaseSecond = CreateSignal();
        var observedTasks = new ConcurrentQueue<Task>();
        var refreshCount = 0;
        var activeRefreshCount = 0;
        var maximumConcurrentRefreshCount = 0;

        async Task RefreshAsync()
        {
            var call = Interlocked.Increment(ref refreshCount);
            var active = Interlocked.Increment(ref activeRefreshCount);
            UpdateMaximum(ref maximumConcurrentRefreshCount, active);

            try
            {
                if (call == 1)
                    return;

                if (call == 2)
                {
                    secondEntered.TrySetResult();
                    await releaseSecond.Task;
                    return;
                }

                throw new InvalidOperationException(
                    $"Expected the owner-boundary request to replay once, but refresh {call} started.");
            }
            finally
            {
                Interlocked.Decrement(ref activeRefreshCount);
            }
        }

        using var coalescer = new UiAsyncRefreshCoalescer(
            RefreshAsync,
            task => observedTasks.Enqueue(task),
            () =>
            {
                ownerReleaseReached.Set();
                if (!allowOwnerRelease.Wait(TestTimeout))
                    throw new TimeoutException("Timed out waiting to release the refresh owner.");
            });

        var initialRequest = Task.Run(coalescer.Request);
        Assert.True(ownerReleaseReached.Wait(TestTimeout));

        coalescer.Request();
        Assert.True(coalescer.HasPendingRefresh);
        Assert.Equal(1, Volatile.Read(ref refreshCount));

        allowOwnerRelease.Set();
        await initialRequest.WaitAsync(TestTimeout);
        await secondEntered.Task.WaitAsync(TestTimeout);

        Assert.Equal(2, Volatile.Read(ref refreshCount));
        Assert.Equal(1, Volatile.Read(ref maximumConcurrentRefreshCount));

        releaseSecond.TrySetResult();
        Assert.True(SpinWait.SpinUntil(() => observedTasks.Count == 2, TestTimeout));
        await Task.WhenAll(observedTasks.ToArray()).WaitAsync(TestTimeout);

        Assert.False(coalescer.IsRunning);
        Assert.False(coalescer.HasPendingRefresh);
    }

    [Fact]
    public async Task FaultedRefresh_ReleasesOwnerAndReplaysPendingRequest()
    {
        var firstEntered = CreateSignal();
        var releaseFirst = CreateSignal();
        var secondEntered = CreateSignal();
        var observedTasks = new ConcurrentQueue<Task>();
        var refreshCount = 0;
        var activeRefreshCount = 0;
        var maximumConcurrentRefreshCount = 0;

        async Task RefreshAsync()
        {
            var call = Interlocked.Increment(ref refreshCount);
            var active = Interlocked.Increment(ref activeRefreshCount);
            UpdateMaximum(ref maximumConcurrentRefreshCount, active);

            try
            {
                if (call == 1)
                {
                    firstEntered.TrySetResult();
                    await releaseFirst.Task;
                    throw new InvalidOperationException("expected refresh failure");
                }

                if (call == 2)
                {
                    secondEntered.TrySetResult();
                    return;
                }

                throw new InvalidOperationException(
                    $"Expected one replay after the fault, but refresh {call} started.");
            }
            finally
            {
                Interlocked.Decrement(ref activeRefreshCount);
            }
        }

        using var coalescer = new UiAsyncRefreshCoalescer(
            RefreshAsync,
            task => observedTasks.Enqueue(task));

        coalescer.Request();
        await firstEntered.Task.WaitAsync(TestTimeout);
        coalescer.Request();
        releaseFirst.TrySetResult();
        await secondEntered.Task.WaitAsync(TestTimeout);

        Assert.True(SpinWait.SpinUntil(() => observedTasks.Count == 2, TestTimeout));
        var observed = observedTasks.ToArray();
        foreach (var task in observed)
        {
            try
            {
                await task.WaitAsync(TestTimeout);
            }
            catch (InvalidOperationException ex) when (
                string.Equals(ex.Message, "expected refresh failure", StringComparison.Ordinal))
            {
            }
        }

        Assert.Equal(2, Volatile.Read(ref refreshCount));
        Assert.Equal(1, Volatile.Read(ref maximumConcurrentRefreshCount));
        Assert.Equal(1, observed.Count(task => task.IsFaulted));
        Assert.Equal(1, observed.Count(task => task.Status == TaskStatus.RanToCompletion));
        Assert.False(coalescer.IsRunning);
        Assert.False(coalescer.HasPendingRefresh);
    }

    private static TaskCompletionSource CreateSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref maximum);
            if (candidate <= current)
                return;

            if (Interlocked.CompareExchange(ref maximum, candidate, current) == current)
                return;
        }
    }
}
