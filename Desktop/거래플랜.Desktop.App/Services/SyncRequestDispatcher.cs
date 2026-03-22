namespace 거래플랜.Desktop.App.Services;

/// <summary>
/// In-process dispatcher for requesting an accelerated sync after local data mutations.
/// </summary>
public sealed class SyncRequestDispatcher
{
    private readonly object _gate = new();
    private TaskCompletionSource<bool>? _pendingImmediateSync;

    public event Action? ImmediateSyncRequested;

    public void RequestImmediateSync()
    {
        lock (_gate)
        {
            _pendingImmediateSync ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        ImmediateSyncRequested?.Invoke();
    }

    public Task<bool> WaitForImmediateSyncCompletionAsync(CancellationToken ct = default)
    {
        Task<bool>? pendingTask;
        lock (_gate)
        {
            pendingTask = _pendingImmediateSync?.Task;
        }

        if (pendingTask is null)
            return Task.FromResult(false);

        return ct.CanBeCanceled
            ? pendingTask.WaitAsync(ct)
            : pendingTask;
    }

    public void CompleteImmediateSync(bool succeeded)
    {
        TaskCompletionSource<bool>? pending;
        lock (_gate)
        {
            pending = _pendingImmediateSync;
            _pendingImmediateSync = null;
        }

        pending?.TrySetResult(succeeded);
    }
}
