using System.Threading;

namespace GeoraePlan.Mobile.App;

public sealed class App : Application
{
    private readonly Services.SessionStore _sessionStore;
    private readonly Services.SyncCoordinator _syncCoordinator;
    private IDispatcherTimer? _foregroundSyncTimer;
    private int _foregroundSyncRunning;

    public App(Services.SessionStore sessionStore, Services.SyncCoordinator syncCoordinator)
    {
        _sessionStore = sessionStore;
        _syncCoordinator = syncCoordinator;
        UserAppTheme = AppTheme.Light;
        MainPage = CreateRootPage();
#if DEBUG
        _ = TryBootstrapDebugSessionAsync();
#endif
        if (_sessionStore.HasCachedSession())
        {
            StartForegroundSyncTimer();
            _ = RunLaunchSyncAsync();
        }
    }

    private Page CreateRootPage()
        => _sessionStore.HasCachedSession()
            ? new AppShell()
            : new NavigationPage(new Pages.LoginPage());

    public static void ShowShell()
    {
        if (Current is not App app)
            return;

        app.MainPage = new AppShell();
        app.StartForegroundSyncTimer();
    }

    public static void ShowLogin()
    {
        if (Current is not App app)
            return;

        app.StopForegroundSyncTimer();
        app.MainPage = new NavigationPage(new Pages.LoginPage());
    }

    private async Task RunLaunchSyncAsync()
    {
        try
        {
            await _syncCoordinator.TryBackgroundSyncAsync("app-start", TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        }
        catch
        {
            // 앱 시작 UX를 막지 않습니다.
        }
    }

    private void StartForegroundSyncTimer()
    {
        var dispatcher = MainPage?.Dispatcher ?? Current?.Dispatcher;
        if (dispatcher is null)
            return;

        if (_foregroundSyncTimer is null)
        {
            _foregroundSyncTimer = dispatcher.CreateTimer();
            _foregroundSyncTimer.Interval = TimeSpan.FromSeconds(25);
            _foregroundSyncTimer.IsRepeating = true;
            _foregroundSyncTimer.Tick += async (_, _) => await RunForegroundSyncPulseAsync();
        }

        if (!_foregroundSyncTimer.IsRunning)
            _foregroundSyncTimer.Start();
    }

    private void StopForegroundSyncTimer()
        => _foregroundSyncTimer?.Stop();

    private async Task RunForegroundSyncPulseAsync()
    {
        if (!_sessionStore.HasCachedSession())
            return;

        if (Interlocked.Exchange(ref _foregroundSyncRunning, 1) == 1)
            return;

        try
        {
            await _syncCoordinator.TryBackgroundSyncAsync("app-foreground-timer", TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        }
        catch
        {
            // 백그라운드 타이머는 조용히 재시도합니다.
        }
        finally
        {
            Interlocked.Exchange(ref _foregroundSyncRunning, 0);
        }
    }

#if DEBUG
    private async Task TryBootstrapDebugSessionAsync()
    {
        if (_sessionStore.HasCachedSession())
            return;

        if (!await Services.DebugSessionBootstrap.TryApplyAsync(_sessionStore).ConfigureAwait(false))
            return;

        MainThread.BeginInvokeOnMainThread(ShowShell);
    }
#endif
}
