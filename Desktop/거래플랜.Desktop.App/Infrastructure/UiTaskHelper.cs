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
                if (!string.IsNullOrWhiteSpace(userMessage))
                {
                    MessageBox.Show(
                        owner ?? Application.Current?.MainWindow,
                        $"{userMessage}{Environment.NewLine}{ex.Message}",
                        "오류",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }

                onError?.Invoke(ex);
            }, () => ActiveOperations.TryRemove(operationKey, out _));
        }
        catch (Exception ex)
        {
            ActiveOperations.TryRemove(operationKey, out _);
            AppLogger.Error(category, $"{operationName} 실패", ex);

            if (!string.IsNullOrWhiteSpace(userMessage))
            {
                MessageBox.Show(
                    owner ?? Application.Current?.MainWindow,
                    $"{userMessage}{Environment.NewLine}{ex.Message}",
                    "오류",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

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
}
