using System.Threading;
using GeoraePlan.Mobile.App.Pages;
using GeoraePlan.Mobile.App.Theme;

namespace GeoraePlan.Mobile.App;

public sealed class App : Application
{
    private const string LastPromptedMobileVersionKey = "updates.last_prompted_mobile_version";
    private readonly Services.SessionStore _sessionStore;
    private readonly Services.MobileSessionRecoveryService _sessionRecoveryService;
    private readonly Services.SyncCoordinator _syncCoordinator;
    private readonly Services.MobileAppUpdateService _updateService;
    private IDispatcherTimer? _foregroundSyncTimer;
    private int _foregroundSyncRunning;
    private int _updatePromptRunning;

    public App(
        Services.SessionStore sessionStore,
        Services.MobileSessionRecoveryService sessionRecoveryService,
        Services.SyncCoordinator syncCoordinator,
        Services.MobileAppUpdateService updateService)
    {
        _sessionStore = sessionStore;
        _sessionRecoveryService = sessionRecoveryService;
        _syncCoordinator = syncCoordinator;
        _updateService = updateService;
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
        _ = app.RunUpdatePromptAsync();
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

    private async Task RunUpdatePromptAsync()
    {
        if (Interlocked.Exchange(ref _updatePromptRunning, 1) == 1)
            return;

        try
        {
            var result = await _updateService.CheckForUpdatesAsync().ConfigureAwait(false);
            if (!result.IsUpdateAvailable || result.Package is null)
                return;

            var lastPromptedVersion = Preferences.Default.Get(LastPromptedMobileVersionKey, string.Empty);
            if (string.Equals(lastPromptedVersion, result.LatestVersion, StringComparison.OrdinalIgnoreCase))
                return;

            Preferences.Default.Set(LastPromptedMobileVersionKey, result.LatestVersion);

            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                if (Current?.MainPage is null)
                    return;

                var installNow = await Current.MainPage.DisplayAlert(
                    "업데이트 알림",
                    $"새 안드로이드 버전 {result.LatestVersion}이 준비되어 있습니다.{Environment.NewLine}{Environment.NewLine}지금 설치하시겠습니까?",
                    "설치",
                    "나중에");

                if (!installNow)
                    return;

                try
                {
                    await _updateService.DownloadAndLaunchInstallerAsync(result.Package);
                    await Current.MainPage.DisplayAlert(
                        "업데이트",
                        "APK 다운로드가 완료되었습니다. 안드로이드 설치 화면을 확인하세요.",
                        "확인");
                }
                catch (Exception ex)
                {
                    await Current.MainPage.DisplayAlert(
                        "업데이트 실패",
                        ex.Message,
                        "확인");
                }
            });
        }
        catch
        {
            // 자동 업데이트 알림 실패는 조용히 무시합니다.
        }
        finally
        {
            Interlocked.Exchange(ref _updatePromptRunning, 0);
        }
    }
}
