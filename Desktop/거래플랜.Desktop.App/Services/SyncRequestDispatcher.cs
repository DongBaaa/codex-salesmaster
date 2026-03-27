namespace 거래플랜.Desktop.App.Services;

public enum SyncRequestMode
{
    Debounced,
    Flush
}

/// <summary>
/// In-process dispatcher for debounced and forced sync requests after local mutations.
/// </summary>
public sealed class SyncRequestDispatcher
{
    private const string DisableServerSyncEnvironmentKey = "GEORAEPLAN_DISABLE_SERVER_SYNC";
    private readonly object _gate = new();
    private TaskCompletionSource<bool>? _pendingSync;
    private SyncRequestMode _pendingMode = SyncRequestMode.Debounced;

    public event Action<SyncRequestMode>? SyncRequested;

    public void RequestDebouncedSync()
    {
        if (IsServerSyncDisabled())
        {
            CompleteSync(false);
            return;
        }

        lock (_gate)
        {
            _pendingSync ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_pendingMode != SyncRequestMode.Flush)
                _pendingMode = SyncRequestMode.Debounced;
        }

        SyncRequested?.Invoke(SyncRequestMode.Debounced);
    }

    public void RequestFlushSync()
    {
        if (IsServerSyncDisabled())
        {
            CompleteSync(false);
            return;
        }

        lock (_gate)
        {
            _pendingSync ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingMode = SyncRequestMode.Flush;
        }

        SyncRequested?.Invoke(SyncRequestMode.Flush);
    }

    public Task<bool> WaitForSyncCompletionAsync(CancellationToken ct = default)
    {
        Task<bool>? pendingTask;
        lock (_gate)
        {
            pendingTask = _pendingSync?.Task;
        }

        if (pendingTask is null)
            return Task.FromResult(false);

        return ct.CanBeCanceled
            ? pendingTask.WaitAsync(ct)
            : pendingTask;
    }

    public void CompleteSync(bool succeeded)
    {
        TaskCompletionSource<bool>? pending;
        lock (_gate)
        {
            pending = _pendingSync;
            _pendingSync = null;
            _pendingMode = SyncRequestMode.Debounced;
        }

        pending?.TrySetResult(succeeded);
    }

    private static bool IsServerSyncDisabled()
    {
        var raw = Environment.GetEnvironmentVariable(DisableServerSyncEnvironmentKey);
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        return string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase);
    }
}
