using Microsoft.Maui.Dispatching;

namespace GeoraePlan.Mobile.App.Services;

public static class MobileErrorHandler
{
    public static async Task<bool> RunGuardedAsync(
        Func<Task> action,
        string context,
        string? userMessage = null,
        bool showAlert = false,
        Action<Exception>? onError = null)
    {
        try
        {
            await action();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            MobileAppLogger.Error("MOBILE", $"{context} 실패", ex);
            onError?.Invoke(ex);

            if (showAlert)
            {
                await ShowAlertAsync(
                    "오류",
                    userMessage ?? $"{context} 중 오류가 발생했습니다.{Environment.NewLine}{ex.Message}");
            }

            return false;
        }
    }

    public static void FireAndForget(
        Func<Task> action,
        string context,
        string? userMessage = null,
        bool showAlert = false,
        Action<Exception>? onError = null)
    {
        _ = RunGuardedAsync(action, context, userMessage, showAlert, onError);
    }

    public static Task ShowAlertAsync(string title, string message)
    {
        if (Application.Current?.MainPage is null)
            return Task.CompletedTask;

        return MainThread.InvokeOnMainThreadAsync(async () =>
        {
            if (Application.Current?.MainPage is null)
                return;

            await Application.Current.MainPage.DisplayAlert(title, message, "확인");
        });
    }
}
