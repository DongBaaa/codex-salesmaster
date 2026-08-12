using 거래플랜.Desktop.App.Infrastructure;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class UiDebouncerTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Cancel_PreventsPendingAction_AndAllowsLaterRequest()
    {
        using var debouncer = new UiDebouncer();
        var canceledInvocation = CreateSignal();

        debouncer.DebounceAsync(
            TimeSpan.FromMilliseconds(250),
            () =>
            {
                canceledInvocation.TrySetResult();
                return Task.CompletedTask;
            });

        debouncer.Cancel();
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        Assert.False(canceledInvocation.Task.IsCompleted);

        var laterInvocation = CreateSignal();
        debouncer.DebounceAsync(
            TimeSpan.FromMilliseconds(10),
            () =>
            {
                laterInvocation.TrySetResult();
                return Task.CompletedTask;
            });

        await laterInvocation.Task.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task CancelAndDrainAsync_WaitsForAlreadyRunningAction()
    {
        using var debouncer = new UiDebouncer();
        var actionEntered = CreateSignal();
        var releaseAction = CreateSignal();

        debouncer.DebounceAsync(
            TimeSpan.Zero,
            async () =>
            {
                actionEntered.TrySetResult();
                await releaseAction.Task;
            });

        await actionEntered.Task.WaitAsync(TestTimeout);
        var drainTask = debouncer.CancelAndDrainAsync();

        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50));
            Assert.False(drainTask.IsCompleted);
        }
        finally
        {
            releaseAction.TrySetResult();
        }

        await drainTask.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task CancelAndDrainAsync_WaitsForSupersededRunningAction_AndCancelsQueuedReplacement()
    {
        using var debouncer = new UiDebouncer();
        var firstActionEntered = CreateSignal();
        var releaseFirstAction = CreateSignal();
        var replacementInvoked = CreateSignal();

        debouncer.DebounceAsync(
            TimeSpan.Zero,
            async () =>
            {
                firstActionEntered.TrySetResult();
                await releaseFirstAction.Task;
            });
        await firstActionEntered.Task.WaitAsync(TestTimeout);

        debouncer.DebounceAsync(
            TimeSpan.Zero,
            () =>
            {
                replacementInvoked.TrySetResult();
                return Task.CompletedTask;
            });
        var drainTask = debouncer.CancelAndDrainAsync();

        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50));
            Assert.False(drainTask.IsCompleted);
            Assert.False(replacementInvoked.Task.IsCompleted);
        }
        finally
        {
            releaseFirstAction.TrySetResult();
        }

        await drainTask.WaitAsync(TestTimeout);
        Assert.False(replacementInvoked.Task.IsCompleted);
    }

    [Fact]
    public async Task WaitForIdleAsync_AllowsPendingActionToRun_AndWaitsForCompletion()
    {
        using var debouncer = new UiDebouncer();
        var actionEntered = CreateSignal();
        var releaseAction = CreateSignal();

        debouncer.DebounceAsync(
            TimeSpan.FromMilliseconds(50),
            async () =>
            {
                actionEntered.TrySetResult();
                await releaseAction.Task;
            });

        var waitTask = debouncer.WaitForIdleAsync();
        await actionEntered.Task.WaitAsync(TestTimeout);
        Assert.False(waitTask.IsCompleted);

        releaseAction.TrySetResult();
        await waitTask.WaitAsync(TestTimeout);
        Assert.True(actionEntered.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task DisposeAsync_WaitsForAlreadyRunningAction()
    {
        var debouncer = new UiDebouncer();
        var actionEntered = CreateSignal();
        var releaseAction = CreateSignal();
        debouncer.DebounceAsync(
            TimeSpan.Zero,
            async () =>
            {
                actionEntered.TrySetResult();
                await releaseAction.Task;
            });
        await actionEntered.Task.WaitAsync(TestTimeout);

        var disposeTask = debouncer.DisposeAsync().AsTask();
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50));
            Assert.False(disposeTask.IsCompleted);
        }
        finally
        {
            releaseAction.TrySetResult();
        }

        await disposeTask.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task Dispose_IsTerminalAndRejectsLaterRequests()
    {
        var debouncer = new UiDebouncer();
        debouncer.Dispose();
        var invoked = CreateSignal();

        debouncer.DebounceAsync(
            TimeSpan.Zero,
            () =>
            {
                invoked.TrySetResult();
                return Task.CompletedTask;
            });

        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.False(invoked.Task.IsCompleted);
        Assert.True(debouncer.IsIdle);
    }

    [Fact]
    public async Task DisposeAsync_IsTerminalAndRejectsLaterRequests()
    {
        var debouncer = new UiDebouncer();
        await debouncer.DisposeAsync();
        var invoked = CreateSignal();

        debouncer.Debounce(
            TimeSpan.Zero,
            () => invoked.TrySetResult());

        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.False(invoked.Task.IsCompleted);
        Assert.True(debouncer.IsIdle);
    }

    private static TaskCompletionSource CreateSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
