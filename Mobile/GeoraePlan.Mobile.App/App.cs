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
    private readonly Services.MobileCompatibilityGateService _compatibilityGate;
    private readonly Services.MobileRealtimeSyncService _realtimeSyncService;
    private readonly SemaphoreSlim _rootInitializationLock = new(1, 1);
    private int _resumeSyncRunning;
    private int _backgroundStartRunning;
    private int _startupCompleted;
    private int _updatePromptRunning;
    private int _globalErrorDialogOpen;

    public App(
        Services.SessionStore sessionStore,
        Services.MobileSessionRecoveryService sessionRecoveryService,
        Services.SyncCoordinator syncCoordinator,
        Services.MobileAppUpdateService updateService,
        Services.MobileCompatibilityGateService compatibilityGate,
        Services.MobileRealtimeSyncService realtimeSyncService)
    {
        _sessionStore = sessionStore;
        _sessionRecoveryService = sessionRecoveryService;
        _syncCoordinator = syncCoordinator;
        _updateService = updateService;
        _compatibilityGate = compatibilityGate;
        _realtimeSyncService = realtimeSyncService;
        Services.MobileClientUpgradeRequiredSignal.Raised +=
            HandleClientUpgradeRequired;
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
        window.Activated += async (_, _) =>
            await RunResumeRevisionSyncAsync("window-activated");
        window.Resumed += async (_, _) =>
            await RunResumeRevisionSyncAsync("window-resumed");
        return window;
    }

    private async Task InitializeRootAsync()
    {
        await _rootInitializationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!await EnsureCompatibilityGatePassedAsync(
                    "app-startup").ConfigureAwait(false))
            {
                return;
            }

#if DEBUG
            if (!await _sessionStore.HasUsableSessionAsync().ConfigureAwait(false))
            {
                await Services.DebugSessionBootstrap
                    .TryApplyAsync(_sessionStore)
                    .ConfigureAwait(false);
            }
#endif

            var hasSession = await _sessionStore
                .HasUsableSessionAsync()
                .ConfigureAwait(false);
            if (!hasSession)
            {
                var recovery = await _sessionRecoveryService
                    .TryRestoreSessionAsync("app-startup")
                    .ConfigureAwait(false);
                hasSession =
                    recovery.Success &&
                    await _sessionStore
                        .HasUsableSessionAsync()
                        .ConfigureAwait(false);
            }

            if (_compatibilityGate.IsBlocking)
                return;

            Interlocked.Exchange(ref _startupCompleted, 1);
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (hasSession)
                    ShowShell();
                else
                    ShowLogin();
            });
        }
        catch (Services.MobileClientUpgradeRequiredException ex)
        {
            await HandleClientUpgradeRequiredAsync(ex).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ReportUnexpectedException(
                "모바일 시작 화면 초기화",
                ex,
                showAlert: true);
        }
        finally
        {
            _rootInitializationLock.Release();
        }
    }

    public static void ShowShell()
    {
        if (Current is not App app)
            return;

        _ = app.ShowShellAfterCompatibilityGateAsync();
    }

    private async Task ShowShellAfterCompatibilityGateAsync()
    {
        if (!await EnsureCompatibilityGatePassedAsync(
                "before-shell").ConfigureAwait(false))
        {
            return;
        }

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            if (_compatibilityGate.IsBlocking)
                return;

            MainPage = new AppShell();
            StartBackgroundServicesAfterFirstFrame();
        });
    }

    public static void ShowLogin()
    {
        if (Current is not App app)
            return;

        if (app._compatibilityGate.IsBlocking)
        {
            _ = app.ShowLatestCompatibilityGateAsync();
            return;
        }

        app._realtimeSyncService.Stop();
        app.MainPage = new NavigationPage(new LoginPage());
    }

    private async Task RunLaunchSyncAsync()
    {
        try
        {
            await _syncCoordinator.RefreshIfServerChangedAsync("app-start", TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Services.MobileClientUpgradeRequiredException ex)
        {
            await HandleClientUpgradeRequiredAsync(ex).ConfigureAwait(false);
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
            _ = StartBackgroundServicesAsync();
        });
    }

    private async Task StartBackgroundServicesAsync()
    {
        if (Interlocked.Exchange(ref _backgroundStartRunning, 1) == 1)
            return;

        try
        {
            if (!await EnsureCompatibilityGatePassedAsync(
                    "after-first-frame").ConfigureAwait(false))
            {
                return;
            }

            if (!await _sessionStore.HasUsableSessionAsync().ConfigureAwait(false))
                return;

            _realtimeSyncService.Start();
            await RunLaunchSyncAsync().ConfigureAwait(false);
            if (!_compatibilityGate.IsBlocking)
                await RunUpdatePromptAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            MobileAppLogger.Warn(
                "APP",
                $"모바일 시작 후 백그라운드 서비스 시작 실패: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _backgroundStartRunning, 0);
        }
    }

    private async Task RunResumeRevisionSyncAsync(string reason)
    {
        if (Interlocked.Exchange(ref _resumeSyncRunning, 1) == 1)
            return;

        try
        {
            // No realtime or revision request may race ahead of the resume gate.
            _realtimeSyncService.Stop();
            if (!await EnsureCompatibilityGatePassedAsync(reason).ConfigureAwait(false))
                return;

            if (Volatile.Read(ref _startupCompleted) == 0)
                return;

            if (!await _sessionStore.HasUsableSessionAsync().ConfigureAwait(false))
            {
                var recovery = await _sessionRecoveryService
                    .TryRestoreSessionAsync(reason)
                    .ConfigureAwait(false);
                if (!recovery.Success &&
                    !await _sessionStore
                        .HasUsableSessionAsync()
                        .ConfigureAwait(false))
                {
                    MainThread.BeginInvokeOnMainThread(ShowLogin);
                    return;
                }
            }

            await _syncCoordinator.RefreshIfServerChangedAsync(reason, TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            if (!_compatibilityGate.IsBlocking)
                _realtimeSyncService.Start();
        }
        catch (Services.MobileClientUpgradeRequiredException ex)
        {
            await HandleClientUpgradeRequiredAsync(ex).ConfigureAwait(false);
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
            var outcome = await _compatibilityGate.CheckAsync().ConfigureAwait(false);
            if (outcome.IsBlocked)
            {
                await ShowCompatibilityGateAsync(outcome).ConfigureAwait(false);
                return;
            }

            var result = outcome.Update;
            if (!result.IsUpdateAvailable || result.Package is null)
                return;

            // A required update never uses a dismissible alert. It transitions
            // to UpdateRequiredPage through the compatibility gate above.
            if (result.RequiresImmediateUpdate)
                return;

            var latestReleaseIdentity =
                $"{result.LatestVersion}|{result.LatestBuild ?? 0}";
            var lastPromptedVersion = Preferences.Default.Get(
                LastPromptedMobileVersionKey,
                string.Empty);
            if (string.Equals(
                    lastPromptedVersion,
                    latestReleaseIdentity,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Preferences.Default.Set(
                LastPromptedMobileVersionKey,
                latestReleaseIdentity);

            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                if (Current?.MainPage is null)
                    return;

                var installNow = await Current.MainPage.DisplayAlert(
                    "업데이트 알림",
                    $"안드로이드 버전 {result.LatestVersion}{(result.LatestBuild is > 0 ? $" (빌드 {result.LatestBuild.Value})" : string.Empty)}이 준비되었습니다.{Environment.NewLine}{Environment.NewLine}지금 설치하시겠습니까?",
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
        catch (Services.MobileClientUpgradeRequiredException ex)
        {
            await HandleClientUpgradeRequiredAsync(ex).ConfigureAwait(false);
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

    private async Task<bool> EnsureCompatibilityGatePassedAsync(string reason)
    {
        var outcome = await _compatibilityGate.CheckAsync().ConfigureAwait(false);
        if (!outcome.IsBlocked)
            return true;

        MobileAppLogger.Warn(
            "UPDATE",
            $"앱 호환성 게이트 진입: {reason} / {outcome.Source}");
        await ShowCompatibilityGateAsync(outcome).ConfigureAwait(false);
        return false;
    }

    private async Task ShowLatestCompatibilityGateAsync()
    {
        var outcome = _compatibilityGate.LatestOutcome;
        if (outcome is null || !outcome.IsBlocked)
            outcome = await _compatibilityGate.CheckAsync().ConfigureAwait(false);

        if (outcome.IsBlocked)
            await ShowCompatibilityGateAsync(outcome).ConfigureAwait(false);
    }

    private async Task ShowCompatibilityGateAsync(
        Services.MobileCompatibilityGateOutcome outcome)
    {
        _realtimeSyncService.Stop();
        Interlocked.Exchange(ref _startupCompleted, 0);

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            if (Current?.MainPage is UpdateRequiredPage existing)
            {
                existing.UpdateOutcome(outcome);
                return;
            }

            MainPage = new UpdateRequiredPage(
                outcome,
                HandleRequiredUpdateInstallAsync,
                HandleRequiredUpdateRetryAsync);
        });
    }

    private async Task HandleRequiredUpdateInstallAsync(UpdateRequiredPage page)
    {
        var outcome = await _compatibilityGate
            .CheckAsync(forceRefresh: true)
            .ConfigureAwait(false);
        if (!outcome.IsBlocked)
        {
            await ResumeAfterCompatibilityGateAsync().ConfigureAwait(false);
            return;
        }

        await MainThread.InvokeOnMainThreadAsync(() =>
            page.UpdateOutcome(outcome));

        var package = outcome.Update.Package;
        if (package is null ||
            string.IsNullOrWhiteSpace(package.PackageUrl) ||
            string.IsNullOrWhiteSpace(package.Sha256))
        {
            await MainThread.InvokeOnMainThreadAsync(() =>
                page.SetStatus(
                    "검증된 APK 정보를 아직 받지 못했습니다. 네트워크 연결을 확인한 뒤 다시 확인을 눌러 주세요."));
            return;
        }

        try
        {
            await _updateService
                .DownloadAndLaunchInstallerAsync(package)
                .ConfigureAwait(false);
            await MainThread.InvokeOnMainThreadAsync(() =>
                page.SetStatus(
                    "APK 무결성 확인을 마쳤습니다. 안드로이드 설치 화면에서 업데이트를 완료한 뒤 앱으로 돌아와 다시 확인해 주세요."));
        }
        catch (Exception ex)
        {
            await MainThread.InvokeOnMainThreadAsync(() =>
                page.SetStatus($"업데이트 설치 준비 실패: {ex.Message}"));
        }
    }

    private async Task HandleRequiredUpdateRetryAsync(UpdateRequiredPage page)
    {
        var outcome = await _compatibilityGate
            .CheckAsync(forceRefresh: true)
            .ConfigureAwait(false);
        if (outcome.IsBlocked)
        {
            await MainThread.InvokeOnMainThreadAsync(() =>
                page.UpdateOutcome(outcome));
            return;
        }

        await ResumeAfterCompatibilityGateAsync().ConfigureAwait(false);
    }

    private async Task ResumeAfterCompatibilityGateAsync()
    {
        _realtimeSyncService.Stop();
        Interlocked.Exchange(ref _startupCompleted, 0);
        await MainThread.InvokeOnMainThreadAsync(() =>
            MainPage = CreateStartupPage());
        await InitializeRootAsync().ConfigureAwait(false);
    }

    private void HandleClientUpgradeRequired(
        Services.MobileClientUpgradeRequiredException exception)
    {
        // Stop synchronously so a swallowed 426 cannot leave realtime running.
        _realtimeSyncService.Stop();
        _ = HandleClientUpgradeRequiredAsync(exception);
    }

    private async Task HandleClientUpgradeRequiredAsync(
        Services.MobileClientUpgradeRequiredException exception)
    {
        try
        {
            var outcome = await _compatibilityGate
                .ActivateAsync(exception)
                .ConfigureAwait(false);
            await ShowCompatibilityGateAsync(outcome).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            MobileAppLogger.Error(
                "UPDATE",
                "서버 강제 업데이트 응답을 게이트로 전환하지 못했습니다.",
                ex);
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
        if (ex is Services.MobileClientUpgradeRequiredException upgradeRequired)
        {
            _ = HandleClientUpgradeRequiredAsync(upgradeRequired);
            return;
        }

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
