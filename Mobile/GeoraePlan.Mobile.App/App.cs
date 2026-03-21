using System.Threading;
using GeoraePlan.Mobile.App.Pages;
using GeoraePlan.Mobile.App.Theme;

namespace GeoraePlan.Mobile.App;

public sealed class App : Application
{
    private readonly Services.SessionStore _sessionStore;
    private readonly Services.MobileSessionRecoveryService _sessionRecoveryService;
    private readonly Services.SyncCoordinator _syncCoordinator;
    private IDispatcherTimer? _foregroundSyncTimer;
    private int _foregroundSyncRunning;

    public App(
        Services.SessionStore sessionStore,
        Services.MobileSessionRecoveryService sessionRecoveryService,
        Services.SyncCoordinator syncCoordinator)
    {
        _sessionStore = sessionStore;
        _sessionRecoveryService = sessionRecoveryService;
        _syncCoordinator = syncCoordinator;
        UserAppTheme = AppTheme.Light;
        MainPage = CreateStartupPage();
        _ = InitializeRootAsync();
    }

    private static Page CreateStartupPage()
    {
        return new ContentPage
        {
            BackgroundColor = GeoraePlanTheme.PageBackground,
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(24),
                Spacing = 12,
                VerticalOptions = LayoutOptions.Center,
                HorizontalOptions = LayoutOptions.Center,
                Children =
                {
                    new ActivityIndicator
                    {
                        IsRunning = true,
                        Color = GeoraePlanTheme.Accent,
                        WidthRequest = 28,
                        HeightRequest = 28
                    },
                    new Label
                    {
                        Text = "거래플랜을 준비하고 있습니다.",
                        TextColor = GeoraePlanTheme.TextSecondary,
                        FontSize = 13,
                        HorizontalTextAlignment = TextAlignment.Center
                    }
                }
            }
        };
    }

    private async Task InitializeRootAsync()
    {
#if DEBUG
        if (!await _sessionStore.HasUsableSessionAsync().ConfigureAwait(false))
            await Services.DebugSessionBootstrap.TryApplyAsync(_sessionStore).ConfigureAwait(false);
#endif

        var hasSession = await _sessionStore.HasUsableSessionAsync().ConfigureAwait(false);
        if (!hasSession)
        {
            var recovery = await _sessionRecoveryService.TryRestoreSessionAsync("app-startup").ConfigureAwait(false);
            hasSession = recovery.Success && await _sessionStore.HasUsableSessionAsync().ConfigureAwait(false);
        }

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (hasSession)
                ShowShell();
            else
                ShowLogin();
        });
    }

    public static void ShowShell()
    {
        if (Current is not App app)
            return;

        app.MainPage = new AppShell();
        app.StartForegroundSyncTimer();
        _ = app.RunLaunchSyncAsync();
    }

    public static void ShowLogin()
    {
        if (Current is not App app)
            return;

        app.StopForegroundSyncTimer();
        app.MainPage = new NavigationPage(new LoginPage());
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
        if (!await _sessionStore.HasUsableSessionAsync().ConfigureAwait(false))
        {
            var recovery = await _sessionRecoveryService.TryRestoreSessionAsync("foreground-sync").ConfigureAwait(false);
            if (!recovery.Success && !await _sessionStore.HasUsableSessionAsync().ConfigureAwait(false))
            {
                MainThread.BeginInvokeOnMainThread(ShowLogin);
                return;
            }
        }

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
}
