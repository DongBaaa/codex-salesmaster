using System.Collections.Concurrent;
using System.Windows;
using 거래플랜.Desktop.App.Services;

namespace 거래플랜.Desktop.App.Infrastructure;

internal static class UiTaskHelper
{
    private static readonly ConcurrentDictionary<string, byte> ActiveOperations = new(StringComparer.Ordinal);

    public static void Run(
        Window? owner,
        Func<Task> operation,
        string category,
        string operationName,
        string? userMessage = null,
        Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);

        var operationKey = BuildOperationKey(owner, category, operationName);
        if (!ActiveOperations.TryAdd(operationKey, 0))
            return;

        try
        {
            var mainWindowLifetime = Application.Current?.MainWindow as global::거래플랜.Desktop.App.MainWindow;
            var task = mainWindowLifetime is null
                ? operation()
                : mainWindowLifetime.RunTrackedWindowOperationAsync(operation);
            Forget(task, category, operationName, ex =>
            {
                ShowUserError(owner, ex, userMessage);

                onError?.Invoke(ex);
            }, () => ActiveOperations.TryRemove(operationKey, out _));
        }
        catch (Exception ex)
        {
            ActiveOperations.TryRemove(operationKey, out _);
            AppLogger.Error(category, $"{operationName} 실패", ex);

            ShowUserError(owner, ex, userMessage);

            onError?.Invoke(ex);
        }
    }

    public static void Forget(
        Task task,
        string category,
        string operation,
        Action<Exception>? onError = null,
        Action? onCompleted = null,
        bool trackForWindowLifetime = true)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        if (task.IsCompleted)
        {
            ObserveCompletedTask(task, category, operation, onError, onCompleted);
            return;
        }

        Task observationTask;
        if (trackForWindowLifetime &&
            Application.Current?.MainWindow is global::거래플랜.Desktop.App.MainWindow mainWindowLifetime)
        {
            observationTask = mainWindowLifetime.TryTrackWindowObservation(
                () => ObserveAsync(task, category, operation, onError, onCompleted))
                ?? ObserveAfterTrackingSealedAsync(
                    task,
                    category,
                    operation,
                    onCompleted);
        }
        else
        {
            observationTask = ObserveAsync(
                task,
                category,
                operation,
                onError,
                onCompleted);
        }

        _ = observationTask;
    }

    public static void Forget(
        Func<Task> taskFactory,
        string category,
        string operation,
        Action<Exception>? onError = null,
        Action? onCompleted = null,
        bool trackForWindowLifetime = true)
    {
        ArgumentNullException.ThrowIfNull(taskFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        if (trackForWindowLifetime &&
            Application.Current?.MainWindow is global::거래플랜.Desktop.App.MainWindow mainWindowLifetime)
        {
            var trackedTask = mainWindowLifetime.TryTrackWindowObservation(
                () => StartAndObserveAsync(
                    taskFactory,
                    category,
                    operation,
                    onError,
                    onCompleted));
            if (trackedTask is null)
            {
                // The lifetime tracker is already sealed. Do not invoke the
                // factory: no new scope-bound work may start past that boundary.
                InvokeCompletedCallback(onCompleted, category, operation);
            }

            return;
        }

        _ = StartAndObserveAsync(
            taskFactory,
            category,
            operation,
            onError,
            onCompleted);
    }

    private static async Task StartAndObserveAsync(
        Func<Task> taskFactory,
        string category,
        string operation,
        Action<Exception>? onError,
        Action? onCompleted)
    {
        try
        {
            await taskFactory();
        }
        catch (OperationCanceledException)
        {
            // 화면 상태 전환에 의한 취소는 예외로 취급하지 않습니다.
        }
        catch (Exception ex)
        {
            AppLogger.Error(category, $"{operation} 실패", ex);
            if (!IsShutdownProtectionActive())
                InvokeErrorCallback(onError, ex, category, operation);
        }
        finally
        {
            InvokeCompletedCallback(onCompleted, category, operation);
        }
    }

    private static void ObserveCompletedTask(Task task, string category, string operation, Action<Exception>? onError, Action? onCompleted)
    {
        try
        {
            if (task.IsCanceled)
                return;

            if (task.Exception is null)
                return;

            var exception = task.Exception.InnerException ?? task.Exception;
            AppLogger.Error(category, $"{operation} 실패", exception);
            if (!IsShutdownProtectionActive())
                InvokeErrorCallback(onError, exception, category, operation);
        }
        finally
        {
            InvokeCompletedCallback(onCompleted, category, operation);
        }
    }

    private static async Task ObserveAsync(Task task, string category, string operation, Action<Exception>? onError, Action? onCompleted)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // 화면 상태 전환에 의한 취소는 예외로 취급하지 않습니다.
        }
        catch (Exception ex)
        {
            AppLogger.Error(category, $"{operation} 실패", ex);
            if (!IsShutdownProtectionActive())
                InvokeErrorCallback(onError, ex, category, operation);
        }
        finally
        {
            InvokeCompletedCallback(onCompleted, category, operation);
        }
    }

    private static async Task ObserveAfterTrackingSealedAsync(
        Task task,
        string category,
        string operation,
        Action? onCompleted)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // Shutdown already sealed the lifetime tracker.
        }
        catch (Exception ex)
        {
            AppLogger.Error(
                category,
                $"{operation} failed after the shutdown tracker was sealed",
                ex);
        }
        finally
        {
            InvokeCompletedCallback(onCompleted, category, operation);
        }
    }

    private static void InvokeErrorCallback(
        Action<Exception>? callback,
        Exception exception,
        string category,
        string operation)
    {
        if (callback is null)
            return;

        try
        {
            callback(exception);
        }
        catch (Exception callbackException)
        {
            AppLogger.Error(category, $"{operation} 오류 callback 실패", callbackException);
        }
    }

    private static void InvokeCompletedCallback(
        Action? callback,
        string category,
        string operation)
    {
        if (callback is null)
            return;

        try
        {
            callback();
        }
        catch (Exception callbackException)
        {
            AppLogger.Error(category, $"{operation} 완료 callback 실패", callbackException);
        }
    }

    private static string BuildOperationKey(Window? owner, string category, string operationName)
        => $"{owner?.GetHashCode().ToString() ?? "app"}:{category}:{operationName}";

    private static void ShowUserError(Window? owner, Exception exception, string? userMessage)
    {
        if (IsShutdownProtectionActive())
            return;

        var isConflict = exception is ExpectedRevisionConflictException;
        var title = isConflict ? "동시 수정 충돌" : "오류";
        var icon = isConflict ? MessageBoxImage.Warning : MessageBoxImage.Error;
        var message = isConflict
            ? exception.Message
            : string.IsNullOrWhiteSpace(userMessage)
                ? exception.Message
                : $"{userMessage}{Environment.NewLine}{exception.Message}";

        if (string.IsNullOrWhiteSpace(message))
            return;

        MessageBox.Show(
            owner ?? Application.Current?.MainWindow,
            message,
            title,
            MessageBoxButton.OK,
            icon);
    }

    private static bool IsShutdownProtectionActive()
        => Application.Current?.MainWindow is global::거래플랜.Desktop.App.MainWindow mainWindow &&
           mainWindow.IsShutdownProtectionActive;
}
