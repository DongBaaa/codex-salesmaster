using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace 거래플랜.Desktop.App.Infrastructure;

public sealed class UiDebouncer : IDisposable, IAsyncDisposable
{
    private readonly object _stateLock = new();
    private readonly HashSet<Task> _activeTasks = new();
    private readonly SemaphoreSlim _actionGate = new(1, 1);
    private CancellationTokenSource? _cts;
    private long _generation;
    private bool _disposed;

    public void Debounce(TimeSpan delay, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Schedule(delay, () =>
        {
            action();
            return Task.CompletedTask;
        }, null);
    }

    public void DebounceAsync(TimeSpan delay, Func<Task> action, Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        Schedule(delay, action, onError);
    }

    private void Schedule(TimeSpan delay, Func<Task> action, Action<Exception>? onError)
    {
        var next = new CancellationTokenSource();
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource? previous;
        Task task;

        lock (_stateLock)
        {
            if (_disposed)
            {
                next.Dispose();
                return;
            }

            previous = _cts;
            _cts = next;
            _generation++;
            task = DebounceCoreAsync(startGate.Task, next.Token, delay, action, onError);
            _activeTasks.Add(task);
        }

        _ = task.ContinueWith(
            completedTask =>
            {
                lock (_stateLock)
                    _activeTasks.Remove(completedTask);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            CancelAndDispose(previous);
        }
        finally
        {
            startGate.TrySetResult();
        }
    }

    private async Task DebounceCoreAsync(
        Task startTask,
        CancellationToken token,
        TimeSpan delay,
        Func<Task> action,
        Action<Exception>? onError)
    {
        try
        {
            await startTask;
            await Task.Delay(delay, token);
            token.ThrowIfCancellationRequested();
            await _actionGate.WaitAsync(token);
            try
            {
                token.ThrowIfCancellationRequested();
                await action();
            }
            finally
            {
                _actionGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when a newer UI action replaces the pending debounce request.
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex);
        }
    }

    public void Cancel()
    {
        CancellationTokenSource? current;
        lock (_stateLock)
        {
            current = _cts;
            _cts = null;
            _generation++;
        }

        CancelAndDispose(current);
    }

    public async Task CancelAndDrainAsync()
    {
        while (true)
        {
            CancellationTokenSource? current;
            Task[] activeTasks;
            long generation;

            lock (_stateLock)
            {
                _activeTasks.RemoveWhere(task => task.IsCompleted);
                current = _cts;
                _cts = null;
                generation = ++_generation;
                activeTasks = _activeTasks.ToArray();
            }

            CancelAndDispose(current);
            if (activeTasks.Length > 0)
                await Task.WhenAll(activeTasks);

            lock (_stateLock)
            {
                _activeTasks.RemoveWhere(task => task.IsCompleted);
                if (generation == _generation &&
                    _cts is null &&
                    _activeTasks.Count == 0)
                {
                    return;
                }
            }
        }
    }

    public async Task WaitForIdleAsync()
    {
        while (true)
        {
            Task[] activeTasks;
            long generation;

            lock (_stateLock)
            {
                _activeTasks.RemoveWhere(task => task.IsCompleted);
                generation = _generation;
                activeTasks = _activeTasks.ToArray();
                if (activeTasks.Length == 0)
                    return;
            }

            await Task.WhenAll(activeTasks);

            lock (_stateLock)
            {
                _activeTasks.RemoveWhere(task => task.IsCompleted);
                if (generation == _generation && _activeTasks.Count == 0)
                    return;
            }
        }
    }

    public bool IsIdle
    {
        get
        {
            lock (_stateLock)
            {
                _activeTasks.RemoveWhere(task => task.IsCompleted);
                return _activeTasks.Count == 0 && _cts is null;
            }
        }
    }

    private static void CancelAndDispose(CancellationTokenSource? cts)
    {
        if (cts is null)
            return;

        cts.Cancel();
        cts.Dispose();
    }

    public void Dispose()
    {
        CancellationTokenSource? current;
        lock (_stateLock)
        {
            if (_disposed)
                return;

            _disposed = true;
            current = _cts;
            _cts = null;
            _generation++;
        }

        CancelAndDispose(current);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_stateLock)
            _disposed = true;

        await CancelAndDrainAsync();
    }
}
