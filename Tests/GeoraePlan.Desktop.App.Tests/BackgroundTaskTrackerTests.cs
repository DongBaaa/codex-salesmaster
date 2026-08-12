using 거래플랜.Desktop.App.Infrastructure;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class BackgroundTaskTrackerTests
{
    [Fact]
    public async Task DrainAsync_WaitsForEveryRegisteredTaskAndRejectsNewWorkUntilResume()
    {
        var tracker = new BackgroundTaskTracker();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.NotNull(tracker.TryStart(() => first.Task));
        tracker.Track(second.Task);

        var drain = tracker.DrainAsync();
        Assert.False(drain.IsCompleted);
        Assert.Null(tracker.TryStart(() => Task.CompletedTask));

        first.SetResult();
        Assert.False(drain.IsCompleted);
        second.SetResult();
        await drain;
        Assert.True(tracker.IsCompleted);

        tracker.Resume();
        Assert.NotNull(tracker.TryStart(() => Task.CompletedTask));
        await tracker.DrainAsync();
    }

    [Fact]
    public async Task DrainAsync_ObservesFaultedAndCanceledTasksWithoutBlockingCompletion()
    {
        var tracker = new BackgroundTaskTracker();
        tracker.Track(Task.FromException(new InvalidOperationException("expected")));
        tracker.Track(Task.FromCanceled(new CancellationToken(canceled: true)));

        await tracker.DrainAsync();

        Assert.True(tracker.IsCompleted);
    }

    [Fact]
    public async Task TryStart_AllowsSynchronousCrossThreadReentryAndDrainWaitsForBothOperations()
    {
        var tracker = new BackgroundTaskTracker();
        var outerCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var innerCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? innerTask = null;

        var outerTask = tracker.TryStart(() =>
        {
            var reentry = Task.Run(() =>
            {
                innerTask = tracker.TryStart(() => innerCompletion.Task);
            });
            Assert.True(reentry.Wait(TimeSpan.FromSeconds(2)), "Synchronous re-entry must not wait on the tracker gate.");
            return outerCompletion.Task;
        });

        Assert.NotNull(outerTask);
        Assert.Same(outerCompletion.Task, outerTask);
        Assert.Same(innerCompletion.Task, innerTask);

        var drain = tracker.DrainAsync();
        outerCompletion.SetResult();
        Assert.False(drain.IsCompleted);

        innerCompletion.SetResult();
        await drain;
        Assert.True(tracker.IsCompleted);
    }

    [Fact]
    public async Task Track_AfterBeginShutdown_IsStillIncludedInDrain()
    {
        var tracker = new BackgroundTaskTracker();
        var existingTask = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        tracker.BeginShutdown();
        tracker.Track(existingTask.Task);

        var drain = tracker.DrainAsync();
        Assert.False(drain.IsCompleted);

        existingTask.SetResult();
        await drain;
        Assert.True(tracker.IsCompleted);
    }

    [Fact]
    public async Task DrainAsync_AtomicallySealsLateTrackingUntilResume()
    {
        var tracker = new BackgroundTaskTracker();
        await tracker.DrainAsync();
        var lateTask = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var factoryInvoked = false;

        Assert.False(tracker.Track(lateTask.Task));
        Assert.Null(tracker.TryTrack(() =>
        {
            factoryInvoked = true;
            return lateTask.Task;
        }));
        Assert.False(factoryInvoked);
        Assert.True(tracker.IsCompleted);

        tracker.Resume();
        Assert.True(tracker.Track(lateTask.Task));
        var secondDrain = tracker.DrainAsync();
        Assert.False(secondDrain.IsCompleted);
        lateTask.SetResult();
        await secondDrain;
    }

    [Fact]
    public void PauseNewWork_RejectsStartAndTrackUntilResume()
    {
        var tracker = new BackgroundTaskTracker();
        var startInvoked = false;
        var trackInvoked = false;

        tracker.PauseNewWork();

        Assert.True(tracker.IsIdle);
        Assert.Null(tracker.TryStart(() =>
        {
            startInvoked = true;
            return Task.CompletedTask;
        }));
        Assert.Null(tracker.TryTrack(() =>
        {
            trackInvoked = true;
            return Task.CompletedTask;
        }));
        Assert.False(startInvoked);
        Assert.False(trackInvoked);

        tracker.Resume();

        Assert.NotNull(tracker.TryStart(() => Task.CompletedTask));
        Assert.NotNull(tracker.TryTrack(() => Task.CompletedTask));
    }
}
