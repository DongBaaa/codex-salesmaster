using System.Diagnostics;
using 거래플랜.Desktop.App.Services;

namespace 거래플랜.Desktop.App.Infrastructure;

internal static class OperationTiming
{
    private static readonly TimeSpan InfoThreshold = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan WarningThreshold = TimeSpan.FromMilliseconds(2500);

    public static void LogIfSlow(
        string category,
        string operation,
        TimeSpan elapsed,
        string? detail = null,
        TimeSpan? infoThreshold = null,
        TimeSpan? warningThreshold = null)
    {
        var resolvedInfoThreshold = infoThreshold ?? InfoThreshold;
        var resolvedWarningThreshold = warningThreshold ?? WarningThreshold;
        if (elapsed < resolvedInfoThreshold)
            return;

        var suffix = string.IsNullOrWhiteSpace(detail)
            ? string.Empty
            : $" / {detail}";
        var message = $"{category}:{operation} {elapsed.TotalMilliseconds:N0}ms{suffix}";
        if (elapsed >= resolvedWarningThreshold)
        {
            AppLogger.Warn("PERF", message);
            return;
        }

        AppLogger.Info("PERF", message);
    }

    public static async Task MeasureAsync(
        string category,
        string operation,
        Func<Task> action,
        string? detail = null,
        TimeSpan? infoThreshold = null,
        TimeSpan? warningThreshold = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await action();
        }
        finally
        {
            stopwatch.Stop();
            LogIfSlow(category, operation, stopwatch.Elapsed, detail, infoThreshold, warningThreshold);
        }
    }

    public static async Task<T> MeasureAsync<T>(
        string category,
        string operation,
        Func<Task<T>> action,
        string? detail = null,
        TimeSpan? infoThreshold = null,
        TimeSpan? warningThreshold = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return await action();
        }
        finally
        {
            stopwatch.Stop();
            LogIfSlow(category, operation, stopwatch.Elapsed, detail, infoThreshold, warningThreshold);
        }
    }

    public static T Measure<T>(
        string category,
        string operation,
        Func<T> action,
        string? detail = null,
        TimeSpan? infoThreshold = null,
        TimeSpan? warningThreshold = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return action();
        }
        finally
        {
            stopwatch.Stop();
            LogIfSlow(category, operation, stopwatch.Elapsed, detail, infoThreshold, warningThreshold);
        }
    }
}
