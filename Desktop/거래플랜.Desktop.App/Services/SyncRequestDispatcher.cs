namespace 거래플랜.Desktop.App.Services;

/// <summary>
/// In-process dispatcher for requesting an accelerated sync after local data mutations.
/// </summary>
public sealed class SyncRequestDispatcher
{
    public event Action? ImmediateSyncRequested;

    public void RequestImmediateSync()
        => ImmediateSyncRequested?.Invoke();
}
