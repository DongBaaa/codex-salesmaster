namespace 거래플랜.Desktop.App.Infrastructure;

internal sealed class BackgroundTaskTracker
{
    private readonly object _gate = new();
    private readonly HashSet<Task> _activeTasks = [];
    private bool _accepting = true;
    private bool _trackingSealed;
    private bool _newWorkPaused;

    public Task? TryStart(Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return TryReserveAndStart(operation, allowDuringShutdown: false);
    }

    public Task? TryTrack(Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return TryReserveAndStart(operation, allowDuringShutdown: true);
    }

    public bool Track(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return TryTrack(() => task) is not null;
    }

    public void BeginShutdown()
    {
        lock (_gate)
            _accepting = false;
    }

    public void PauseNewWork()
    {
        lock (_gate)
            _newWorkPaused = true;
    }

    public void Resume()
    {
        lock (_gate)
        {
            _activeTasks.RemoveWhere(task => task.IsCompleted);
            _trackingSealed = false;
            _accepting = true;
            _newWorkPaused = false;
        }
    }

    public bool IsIdle
    {
        get
        {
            lock (_gate)
            {
                _activeTasks.RemoveWhere(task => task.IsCompleted);
                return _activeTasks.Count == 0;
            }
        }
    }

    public bool IsCompleted
    {
        get
        {
            lock (_gate)
            {
                _activeTasks.RemoveWhere(task => task.IsCompleted);
                return _activeTasks.Count == 0 && (_accepting || _trackingSealed);
            }
        }
    }

    public async Task DrainAsync()
    {
        BeginShutdown();

        while (true)
        {
            Task[] activeTasks;
            lock (_gate)
            {
                _activeTasks.RemoveWhere(task => task.IsCompleted);
                activeTasks = _activeTasks.ToArray();

                if (activeTasks.Length == 0)
                {
                    _trackingSealed = true;
                    return;
                }
            }

            try
            {
                await Task.WhenAll(activeTasks);
            }
            catch
            {
                // The launching UiTaskHelper owns error reporting. Drain only observes completion.
            }
        }
    }

    private Task? TryReserveAndStart(
        Func<Task> operation,
        bool allowDuringShutdown)
    {
        var reservation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_newWorkPaused ||
                _trackingSealed ||
                (!allowDuringShutdown && !_accepting))
                return null;

            _activeTasks.Add(reservation.Task);
        }

        ObserveCompletion(reservation.Task);

        Task task;
        try
        {
            task = operation() ?? throw new InvalidOperationException(
                "A tracked operation returned a null task.");
        }
        catch
        {
            reservation.TrySetResult();
            throw;
        }

        _ = task.ContinueWith(
            _ => reservation.TrySetResult(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return task;
    }

    private void ObserveCompletion(Task task)
    {
        _ = task.ContinueWith(
            completedTask =>
            {
                lock (_gate)
                    _activeTasks.Remove(completedTask);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
