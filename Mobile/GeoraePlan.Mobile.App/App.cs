using System.Threading;
#if ANDROID
using Android.Runtime;
#endif
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
    private readonly Services.MobileRealtimeSyncService _realtimeSyncService;
    private int _resumeSyncRunning;
    private int _updatePromptRunning;
    private int _globalErrorDialogOpen;

    public App(
        Services.SessionStore sessionStore,
        Services.MobileSessionRecoveryService sessionRecoveryService,
        Services.SyncCoordinator syncCoordinator,
        Services.MobileAppUpdateService updateService,
        Services.MobileRealtimeSyncService realtimeSyncService)
    {
        _sessionStore = sessionStore;
        _sessionRecoveryService = sessionRecoveryService;
        _syncCoordinator = syncCoordinator;
        _updateService = updateService;
        _realtimeSyncService = realtimeSyncService;
        RegisterGlobalExceptionHandlers();
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

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = base.CreateWindow(activationState);
        window.Activated += async (_, _) => await RunResumeRevisionSyncAsync("window-activated");
        window.Resumed += async (_, _) => await RunResumeRevisionSyncAsync("window-resumed");
        return window;
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
        app.StartBackgroundServicesAfterFirstFrame();
    }

    public static void ShowLogin()
    {
        if (Current is not App app)
            return;

        app._realtimeSyncService.Stop();
        app.MainPage = new NavigationPage(new LoginPage());
    }

    private async Task RunLaunchSyncAsync()
    {
        try
        {
            await _syncCoordinator.RefreshIfServerChangedAsync("app-start", TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            MobileAppLogger.Warn("SYNC", $"앱 시작 동기화 실패: {ex.Message}");
        }
    }

    private void StartBackgroundServicesAfterFirstFrame()
    {
        var dispatcher = MainPage?.Dispatcher ?? Dispatcher;
        dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(750), () =>
        {
            try
            {
                _realtimeSyncService.Start();
                _ = RunLaunchSyncAsync();
                _ = RunUpdatePromptAsync();
            }
            catch (Exception ex)
            {
                MobileAppLogger.Warn("APP", $"모바일 시작 후 백그라운드 서비스 시작 실패: {ex.Message}");
            }
        });
    }

    private async Task RunResumeRevisionSyncAsync(string reason)
    {
        if (!await _sessionStore.HasUsableSessionAsync().ConfigureAwait(false))
        {
            var recovery = await _sessionRecoveryService.TryRestoreSessionAsync(reason).ConfigureAwait(false);
            if (!recovery.Success && !await _sessionStore.HasUsableSessionAsync().ConfigureAwait(false))
            {
                MainThread.BeginInvokeOnMainThread(ShowLogin);
                return;
            }
        }

        if (Interlocked.Exchange(ref _resumeSyncRunning, 1) == 1)
            return;

        try
        {
            await _syncCoordinator.RefreshIfServerChangedAsync(reason, TimeSpan.FromSeconds(8)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            MobileAppLogger.Warn("SYNC", $"앱 복귀 동기화 실패: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _resumeSyncRunning, 0);
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
                    $"안드로이드 버전 {result.LatestVersion}이 준비되었습니다.{Environment.NewLine}{Environment.NewLine}지금 설치하시겠습니까?",
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
        catch (Exception ex)
        {
            MobileAppLogger.Warn("UPDATE", $"자동 업데이트 알림 실패: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _updatePromptRunning, 0);
        }
    }

    private void RegisterGlobalExceptionHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += HandleUnhandledException;
        TaskScheduler.UnobservedTaskException += HandleUnobservedTaskException;
#if ANDROID
        AndroidEnvironment.UnhandledExceptionRaiser += HandleAndroidUnhandledException;
#endif
    }

    private void HandleUnhandledException(object? sender, UnhandledExceptionEventArgs args)
    {
        if (args.ExceptionObject is Exception ex)
            ReportUnexpectedException("AppDomain Unhandled Exception", ex, showAlert: !args.IsTerminating);
        else
            MobileAppLogger.Error("APP", "AppDomain Unhandled Exception (non-exception payload)");
    }

    private void HandleUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        ReportUnexpectedException("TaskScheduler Unobserved Exception", args.Exception, showAlert: true);
        args.SetObserved();
    }

#if ANDROID
    private void HandleAndroidUnhandledException(object? sender, RaiseThrowableEventArgs args)
    {
        ReportUnexpectedException("Android Unhandled Exception", args.Exception, showAlert: true);
        args.Handled = true;
    }
#endif

    private void ReportUnexpectedException(string context, Exception ex, bool showAlert)
    {
        MobileAppLogger.Error("APP", context, ex);
        if (!showAlert)
            return;

        if (Interlocked.Exchange(ref _globalErrorDialogOpen, 1) == 1)
            return;

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                if (Current?.MainPage is not null)
                {
                    await Current.MainPage.DisplayAlert(
                        "오류",
                        $"예기치 않은 오류가 발생했습니다.{Environment.NewLine}{ex.Message}",
                        "확인");
                }
            }
            finally
            {
                Interlocked.Exchange(ref _globalErrorDialogOpen, 0);
            }
        });
    }
}
