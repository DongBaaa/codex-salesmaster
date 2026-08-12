namespace 거래플랜.Desktop.App.Infrastructure;

/// <summary>
/// Coalesces refresh requests while one refresh is active and replays once
/// when at least one newer request arrived during that refresh.
/// </summary>
internal sealed class UiAsyncRefreshCoalescer : IDisposable
{
    private readonly Func<Task> _refreshAsync;
    private readonly Action<Task> _observeTask;
    private readonly Action? _beforeOwnerRelease;
    private int _pending;
    private int _running;
    private int _disposed;

    public UiAsyncRefreshCoalescer(
        Func<Task> refreshAsync,
        Action<Task> observeTask,
        Action? beforeOwnerRelease = null)
    {
        _refreshAsync = refreshAsync ?? throw new ArgumentNullException(nameof(refreshAsync));
        _observeTask = observeTask ?? throw new ArgumentNullException(nameof(observeTask));
        _beforeOwnerRelease = beforeOwnerRelease;
    }

    internal bool IsRunning => Volatile.Read(ref _running) != 0;

    internal bool HasPendingRefresh => Volatile.Read(ref _pending) != 0;

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public void Request()
    {
        if (IsDisposed)
            return;

        Volatile.Write(ref _pending, 1);
        if (IsDisposed)
        {
            Interlocked.Exchange(ref _pending, 0);
            return;
        }

        TryStart();
    }

    private void TryStart()
    {
        if (IsDisposed ||
            Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return;
        }

        var task = DrainAsync();
        _observeTask(task);
    }

    private async Task DrainAsync()
    {
        try
        {
            while (!IsDisposed)
            {
                Interlocked.Exchange(ref _pending, 0);
                await _refreshAsync();

                if (IsDisposed || !HasPendingRefresh)
                    return;
            }
        }
        finally
        {
            try
            {
                _beforeOwnerRelease?.Invoke();
            }
            finally
            {
                Volatile.Write(ref _running, 0);

                // A request can arrive after the last pending check but before the
                // running owner is released. Recheck after release so either that
                // request or this drain always acquires the next owner.
                if (!IsDisposed && HasPendingRefresh)
                    TryStart();
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Interlocked.Exchange(ref _pending, 0);
    }
}
