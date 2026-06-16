using System.Windows;
using System.Windows.Threading;

namespace 거래플랜.Desktop.App.Infrastructure;

/// <summary>
/// Tracks top-level windows in the order they were opened and re-activates the
/// previous visible window when a child/work window is closed.
/// </summary>
public static class WindowActivationStackService
{
    private static readonly List<Window> OpenedWindows = new();
    private static bool _registered;

    public static void RegisterGlobal()
    {
        if (_registered)
            return;

        _registered = true;
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnWindowLoaded),
            handledEventsToo: true);
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window window)
            return;

        RegisterWindow(window);
    }

    private static void RegisterWindow(Window window)
    {
        if (OpenedWindows.Contains(window))
            return;

        OpenedWindows.Add(window);
        window.Closed += OnWindowClosed;
    }

    private static void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not Window closedWindow)
            return;

        closedWindow.Closed -= OnWindowClosed;
        OpenedWindows.Remove(closedWindow);

        var app = Application.Current;
        if (app is null || app.Dispatcher.HasShutdownStarted || app.Dispatcher.HasShutdownFinished)
            return;

        app.Dispatcher.BeginInvoke(
            new Action(ActivatePreviousWindow),
            DispatcherPriority.ApplicationIdle);
    }

    private static void ActivatePreviousWindow()
    {
        var app = Application.Current;
        if (app is null || app.Dispatcher.HasShutdownStarted || app.Dispatcher.HasShutdownFinished)
            return;

        for (var index = OpenedWindows.Count - 1; index >= 0; index--)
        {
            var candidate = OpenedWindows[index];
            if (!CanActivate(candidate))
                continue;

            try
            {
                if (candidate.WindowState == WindowState.Minimized)
                    candidate.WindowState = WindowState.Normal;

                candidate.Activate();
                candidate.Focus();
            }
            catch
            {
                // Window activation is best-effort only. Closing should never fail
                // because a previous window cannot be activated.
            }

            return;
        }
    }

    private static bool CanActivate(Window window)
        => window.IsLoaded && window.IsVisible;
}
