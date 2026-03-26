using 거래플랜.Desktop.App.Services;

namespace 거래플랜.Desktop.App.Infrastructure;

internal static class UiTaskHelper
{
    public static void Forget(Task task, string category, string operation, Action<Exception>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        if (task.IsCompleted)
        {
            ObserveCompletedTask(task, category, operation, onError);
            return;
        }

        _ = ObserveAsync(task, category, operation, onError);
    }

    private static void ObserveCompletedTask(Task task, string category, string operation, Action<Exception>? onError)
    {
        if (task.IsCanceled)
            return;

        if (task.Exception is null)
            return;

        var exception = task.Exception.InnerException ?? task.Exception;
        AppLogger.Error(category, $"{operation} 실패", exception);
        onError?.Invoke(exception);
    }

    private static async Task ObserveAsync(Task task, string category, string operation, Action<Exception>? onError)
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
    }
}
