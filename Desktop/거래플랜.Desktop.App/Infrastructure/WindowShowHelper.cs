using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using 거래플랜.Desktop.App.Services;

namespace 거래플랜.Desktop.App.Infrastructure;

internal static class WindowShowHelper
{
    public static void ShowModeless(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        window.Show();
        ActivateWhenReady(window);
    }

    public static void ShowModelessWithDeferredLoad(
        Window window,
        Func<Task> loadAsync,
        string windowTitle,
        string failureMessage,
        Window? messageOwner = null,
        Func<Task>? closedAsync = null,
        bool blockWindowDuringLoad = true)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(loadAsync);

        var loadStarted = false;
        var wasEnabled = window.IsEnabled;
        var previousCursor = window.Cursor;
        var mainWindowLifetime = Application.Current?.MainWindow as global::거래플랜.Desktop.App.MainWindow;

        Task RunWithinMainWindowLifetimeAsync(Func<Task> operation)
            => mainWindowLifetime is null
                ? operation()
                : mainWindowLifetime.RunTrackedWindowOperationAsync(operation);

        void ApplyDeferredLoadState()
        {
            if (blockWindowDuringLoad)
                window.IsEnabled = false;
            window.Cursor = Cursors.Wait;
        }

        void RestoreDeferredLoadState()
        {
            if (blockWindowDuringLoad)
                window.IsEnabled = wasEnabled;
            window.Cursor = previousCursor;
        }

        async Task StartLoadAsync()
        {
            if (loadStarted)
                return;

            loadStarted = true;
            try
            {
                await window.Dispatcher.InvokeAsync(
                    static () => { },
                    DispatcherPriority.ApplicationIdle);

                if (!window.IsLoaded || !window.IsVisible)
                    return;

                await OperationTiming.MeasureAsync(
                    "UI",
                    $"{windowTitle} 초기화",
                    () => RunWithinMainWindowLifetimeAsync(loadAsync),
                    detail: window.GetType().Name,
                    infoThreshold: TimeSpan.FromMilliseconds(600),
                    warningThreshold: TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                AppLogger.Error("UI", $"{windowTitle} 초기화 실패", ex);
                MessageBox.Show(
                    messageOwner ?? window.Owner ?? window,
                    $"{failureMessage}{Environment.NewLine}{ex.Message}",
                    windowTitle,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                if (window.IsLoaded)
                    window.Close();
            }
            finally
            {
                RestoreDeferredLoadState();
            }
        }

        if (closedAsync is not null)
        {
            window.Closed += (_, _) =>
            {
                UiTaskHelper.Run(
                    messageOwner ?? window.Owner ?? Application.Current?.MainWindow,
                    () => RunWithinMainWindowLifetimeAsync(closedAsync),
                    "UI",
                    $"{windowTitle} 닫힘 후 처리",
                    $"{windowTitle} 닫힘 후 처리 중 오류가 발생했습니다.");
            };
        }

        ShowModeless(window);
        ApplyDeferredLoadState();
        _ = StartLoadAsync();
    }

    public static void ActivateWhenReady(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        _ = window.Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() =>
            {
                if (!window.IsLoaded || !window.IsVisible)
                    return;

                if (window.WindowState == WindowState.Minimized)
                    window.WindowState = WindowState.Normal;

                window.Activate();
                window.Focus();
            }));
    }
}
