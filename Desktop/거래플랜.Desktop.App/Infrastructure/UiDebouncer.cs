using System;
using System.Threading;
using System.Threading.Tasks;

namespace 거래플랜.Desktop.App.Infrastructure;

public sealed class UiDebouncer : IDisposable
{
    private CancellationTokenSource? _cts;

    public void Debounce(TimeSpan delay, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var cts = ReplaceToken();
        _ = DebounceCoreAsync(cts, delay, () =>
        {
            action();
            return Task.CompletedTask;
        }, null);
    }

    public void DebounceAsync(TimeSpan delay, Func<Task> action, Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        var cts = ReplaceToken();
        _ = DebounceCoreAsync(cts, delay, action, onError);
    }

    private CancellationTokenSource ReplaceToken()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        return _cts;
    }

    private static async Task DebounceCoreAsync(CancellationTokenSource cts, TimeSpan delay, Func<Task> action, Action<Exception>? onError)
    {
        try
        {
            await Task.Delay(delay, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            await action();
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

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
