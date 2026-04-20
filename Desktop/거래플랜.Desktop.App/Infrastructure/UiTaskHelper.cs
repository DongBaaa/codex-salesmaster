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
            var task = operation();
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

    public static void Forget(Task task, string category, string operation, Action<Exception>? onError = null, Action? onCompleted = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        if (task.IsCompleted)
        {
            ObserveCompletedTask(task, category, operation, onError, onCompleted);
            return;
        }

        _ = ObserveAsync(task, category, operation, onError, onCompleted);
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
            onError?.Invoke(exception);
        }
        finally
        {
            onCompleted?.Invoke();
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
            onError?.Invoke(ex);
        }
        finally
        {
            onCompleted?.Invoke();
        }
    }

    private static string BuildOperationKey(Window? owner, string category, string operationName)
        => $"{owner?.GetHashCode().ToString() ?? "app"}:{category}:{operationName}";

    private static void ShowUserError(Window? owner, Exception exception, string? userMessage)
    {
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
}
