using System.ComponentModel;

using System.Threading;

using System.Windows;

using System.Windows.Controls;

using System.Windows.Media;

using System.Windows.Threading;

using System.Diagnostics;

using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.EntityFrameworkCore;

using 거래플랜.Desktop.App.Configuration;

using 거래플랜.Desktop.App.Data;

using 거래플랜.Desktop.App.Infrastructure;

using 거래플랜.Desktop.App.Services;

using 거래플랜.Desktop.App.ViewModels;

using 거래플랜.Desktop.App.Views;



namespace 거래플랜.Desktop.App;



public partial class App : Application

{

    private sealed record SaveCycleResult(bool SyncAttempted, bool SyncSucceeded, int RemainingDirtyCount, bool BackupSucceeded);

    private static readonly TimeSpan ShutdownSyncTimeout = TimeSpan.FromSeconds(12);



    private ServiceProvider? _services;

    private bool _shutdownInProgress;

    private readonly SemaphoreSlim _saveCycleLock = new(1, 1);

    public static T? TryGetService<T>() where T : class
    {
        if (Current is App app && app._services is not null)
            return app._services.GetService<T>();

        return null;
    }

    private DispatcherTimer? _autoSaveTimer;

    private int _unexpectedErrorDialogOpen;

    private bool _restartToLoginRequested;

    private bool _updateShutdownRequested;



    internal void RequestRestartToLogin()

        => _restartToLoginRequested = true;



    public static void RequestShutdownForUpdate()

    {

        if (Current is not App app)

        {

            Current?.Shutdown(0);

            return;

        }



        if (app.Dispatcher.CheckAccess())

        {

            app.BeginShutdownForUpdate();

        }

        else

        {

            app.Dispatcher.BeginInvoke(

                new Action(app.BeginShutdownForUpdate),

                DispatcherPriority.Send);

        }

    }



    private void BeginShutdownForUpdate()

    {

        _updateShutdownRequested = true;

        _shutdownInProgress = true;

        _autoSaveTimer?.Stop();

        AppLogger.Info("UPDATE", "업데이트 적용을 위해 앱 종료를 시작합니다. 업데이트 준비 단계에서 dirty 동기화는 이미 완료되었습니다.");



        try

        {

            if (MainWindow is Window mainWindow)

            {

                if (mainWindow is MainWindow appWindow)

                    appWindow.BeginShutdownProtection();



                if (mainWindow.IsLoaded)

                {

                    mainWindow.Close();

                    return;

                }

            }

        }

        catch (Exception ex)

        {

            AppLogger.Warn("UPDATE", $"업데이트 종료 중 메인 창 닫기 실패: {ex.Message}");

        }



        Shutdown(0);

    }



    protected override async void OnStartup(StartupEventArgs e)

    {

        base.OnStartup(e);



        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        DispatcherUnhandledException += HandleDispatcherUnhandledException;

        AppDomain.CurrentDomain.UnhandledException += HandleAppDomainUnhandledException;

        TaskScheduler.UnobservedTaskException += HandleTaskSchedulerUnhandledException;

        DataGridAutoColumnWidthService.RegisterGlobal();
        WindowActivationStackService.RegisterGlobal();



        try

        {

            DesktopAppUpdateService.TryCleanupStaleUpdateArtifacts();



            var runtimeSelfCheck = DesktopAppUpdateService.RunStartupSelfCheck();

            var runtimeSelfCheckLog = runtimeSelfCheck.BuildLogMessage();

            if (!string.IsNullOrWhiteSpace(runtimeSelfCheckLog))

            {

                if (runtimeSelfCheck.HasBlockingIssue)

                    AppLogger.Error("UPDATE", "Startup runtime self-check failed: " + runtimeSelfCheckLog);

                else

                    AppLogger.Warn("UPDATE", "Startup runtime self-check warning: " + runtimeSelfCheckLog);

            }



            if (runtimeSelfCheck.HasBlockingIssue)

            {

                MessageBox.Show(

                    runtimeSelfCheck.BuildUserMessage() + Environment.NewLine + Environment.NewLine + "업데이트를 다시 적용하거나 설치 패키지로 재설치한 뒤 실행하세요.",

                    "거래플랜 오류",

                    MessageBoxButton.OK,

                    MessageBoxImage.Error);

                Shutdown(1);

                return;

            }



#if DEBUG
            AppLogger.Info("UPDATE", "Debug build skips canonical install relaunch for local verification.");
#else
            if (DesktopAppUpdateService.TryRelaunchCanonicalInstallIfNeeded(out var relaunchMessage))

            {

                if (!string.IsNullOrWhiteSpace(relaunchMessage))

                    AppLogger.Info("UPDATE", relaunchMessage);



                Shutdown();

                return;

            }
#endif



            var restoreNotice = BackupService.TryApplyPendingRestoreOnStartup();

            if (!string.IsNullOrWhiteSpace(restoreNotice))

            {

                AppLogger.Info("BACKUP", restoreNotice);

                var restoreMessageImage = restoreNotice.Contains("오류", StringComparison.OrdinalIgnoreCase) || restoreNotice.Contains("건너", StringComparison.OrdinalIgnoreCase)

                    ? MessageBoxImage.Warning

                    : MessageBoxImage.Information;

                MessageBox.Show(

                    restoreNotice,

                    "백업 복원",

                    MessageBoxButton.OK,

                    restoreMessageImage);

            }



            var config = new ConfigurationBuilder()

                .SetBasePath(AppContext.BaseDirectory)

                .AddJsonFile("appsettings.json", optional: false)

                .Build();



            var apiOptions = config.GetSection("Api").Get<ApiOptions>() ?? new ApiOptions();



            var services = new ServiceCollection();



            services.AddDbContext<LocalDbContext>();



            services.AddHttpClient<ErpApiClient>(client =>

            {

                client.BaseAddress = new Uri(apiOptions.BaseUrl.TrimEnd('/') + '/');

                client.Timeout = TimeSpan.FromSeconds(30);

            });



            services.AddSingleton<SessionState>();

            services.AddSingleton<OfficeAccessService>();

            services.AddSingleton<SyncRequestDispatcher>();

            services.AddScoped<SyncDiagnosticsService>();

            services.AddScoped<DataIntegrityIssueService>();

            services.AddScoped<LocalStateService>();

            services.AddScoped<RentalStateService>();

            services.AddScoped<RentalDocumentService>();

            services.AddScoped<SyncService>();

            services.AddScoped<BackupService>();

            services.AddScoped<StartupIntegrityService>();

            services.AddScoped<RecentSelectionService>();

            services.AddTransient<StatementPrintService>();

            services.AddTransient<IPrintService, WpfInvoicePrintService>();

            services.AddTransient<LoginViewModel>();

            services.AddTransient<MainViewModel>();



            _services = services.BuildServiceProvider();



            await OperationTiming.MeasureAsync(

                "APP",

                "로컬 DB 초기화 및 버전 정비",

                async () =>

                {

                    await using var scope = _services.CreateAsyncScope();

                    var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();

                    await LocalDbInitializer.InitializeAsync(db);

                    await RunVersionChangeMaintenanceAsync(scope.ServiceProvider);

                },

                warningThreshold: TimeSpan.FromSeconds(4));



            bool? loggedIn;

            using (var loginScope = _services.CreateScope())

            {

                var loginVm = loginScope.ServiceProvider.GetRequiredService<LoginViewModel>();

                await OperationTiming.MeasureAsync(

                    "AUTH",

                    "로그인 뷰모델 초기화",

                    () => loginVm.InitializeAsync(),

                    warningThreshold: TimeSpan.FromSeconds(2));

                var loginWin = new LoginWindow(loginVm);

                loggedIn = OperationTiming.Measure(

                    "AUTH",

                    "로그인 창 표시",

                    () => loginWin.ShowDialog(),

                    warningThreshold: TimeSpan.FromSeconds(10));

            }



            if (loggedIn != true)

            {

                Shutdown();

                return;

            }



            using (var startupSafetyScope = _services.CreateScope())

            {

                var canContinue = await OperationTiming.MeasureAsync(

                    "APP",

                    "로그인 후 안전 점검",

                    () => RunPostLoginSafetyChecksAsync(startupSafetyScope.ServiceProvider),

                    warningThreshold: TimeSpan.FromSeconds(4));

                if (!canContinue)

                {

                    Shutdown();

                    return;

                }

            }



            await OperationTiming.MeasureAsync(

                "APP",

                "지연 레거시 마이그레이션",

                () => TryRunDeferredLegacyMigrationAsync(),

                warningThreshold: TimeSpan.FromSeconds(4));



            var mainScope = _services.CreateScope();

            var sp = mainScope.ServiceProvider;

            var mainVm = sp.GetRequiredService<MainViewModel>();

            var mainWin = new MainWindow(

                mainVm,

                sp.GetRequiredService<LocalStateService>(),

                sp.GetRequiredService<RentalStateService>(),

                sp.GetRequiredService<RentalDocumentService>(),

                sp.GetRequiredService<StatementPrintService>(),

                sp.GetRequiredService<IPrintService>(),

                sp.GetRequiredService<SessionState>(),

                sp.GetRequiredService<ErpApiClient>(),

                sp.GetRequiredService<SyncService>(),

                sp.GetRequiredService<BackupService>(),

                sp.GetRequiredService<SyncDiagnosticsService>(),

                sp.GetRequiredService<DataIntegrityIssueService>(),

                sp.GetRequiredService<IServiceScopeFactory>());



            MainWindow = mainWin;



            StartAutoSaveTimer(sp, mainVm);

            var mainScopeDisposed = false;



            mainWin.Closing += (_, args) => HandleMainWindowClosing(mainWin, sp, mainVm, args);



            mainWin.Closed += (_, _) =>

            {

                try

                {

                    _autoSaveTimer?.Stop();

                    if (!mainScopeDisposed)

                    {

                        var session = sp.GetRequiredService<SessionState>();

                        _services?.GetRequiredService<OfficeAccessService>().ClearSessionAccess(session);

                        session.Clear();

                    }

                }

                catch (ObjectDisposedException ex)

                {

                    AppLogger.Warn("APP", $"메인 창 종료 정리 중 이미 dispose된 서비스 접근을 건너뜁니다: {ex.ObjectName}");

                }

                finally

                {

                    if (!mainScopeDisposed)

                    {

                        mainScope.Dispose();

                        mainScopeDisposed = true;

                    }



                    RestartToLoginIfRequested();

                    Shutdown();

                }

            };



            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            mainWin.Show();



            var session = sp.GetRequiredService<SessionState>();

            // 첫 화면은 즉시 조작 가능해야 하므로 로그인 후 동기화는 팝업/창 비활성화 없이 백그라운드에서 시작한다.

            var showStartupSyncPopupImmediately = false;

            Window? startupSyncPopup = null;



            var initSucceeded = await OperationTiming.MeasureAsync(

                "UI",

                "메인 윈도우 초기화",

                () => TryInitializeMainWindowAsync(mainWin, mainVm, deferStartupNotifications: showStartupSyncPopupImmediately),

                warningThreshold: TimeSpan.FromSeconds(4));

            if (initSucceeded && !_updateShutdownRequested && !_shutdownInProgress && !mainScopeDisposed && mainWin.IsLoaded)

            {

                var popupForPostLoginSync = startupSyncPopup;

                startupSyncPopup = null;

                UiTaskHelper.Forget(

                    RunPostLoginSyncThenStartupNotificationsAsync(

                        mainWin,

                        mainVm,

                        sp,

                        session,

                        popupForPostLoginSync,

                        showDeferredStartupNotifications: showStartupSyncPopupImmediately,

                        initialDashboardLoadTask: mainWin.InitialDashboardLoadTask),

                    "APP",

                    "로그인 후 자동 동기화",

                    ex => AppLogger.Error("APP", "Post-login sync scheduling failure", ex));

            }

            else

            {

                CloseStartupSyncPopup(mainWin, startupSyncPopup);

            }

        }

        catch (Exception ex)

        {

            if (_updateShutdownRequested || _shutdownInProgress)

            {

                AppLogger.Info("UPDATE", $"앱 종료 진행 중 시작 후속 처리를 건너뜁니다: {ex.Message}");

                Shutdown(0);

                return;

            }



            AppLogger.Error("APP", "Startup failure", ex);

            await TryRecordStartupDiagnosticAsync(ex);

            MessageBox.Show(

                $"시작 오류:\n{ex.Message}\n\n{ex.InnerException?.Message}",

                "거래플랜 오류",

                MessageBoxButton.OK,

                MessageBoxImage.Error);

            Shutdown(1);

        }

    }



    private static async Task RunVersionChangeMaintenanceAsync(IServiceProvider serviceProvider)

    {

        try

        {

            var api = serviceProvider.GetRequiredService<ErpApiClient>();

            var local = serviceProvider.GetRequiredService<LocalStateService>();

            var backup = serviceProvider.GetRequiredService<BackupService>();

            var updateService = new DesktopAppUpdateService(api);

            var result = await VersionChangeMaintenanceService.RunAsync(local, backup, updateService.GetCurrentVersion());

            if (result.Ran)

                AppLogger.Info("MAINT", result.Message);

        }

        catch (Exception ex)

        {

            AppLogger.Warn("MAINT", $"버전 변경 후 1회 정비 실패: {ex.Message}");

        }

    }



    private static async Task<bool> RunPostLoginSafetyChecksAsync(IServiceProvider serviceProvider)

    {

        if (!await EnsureMandatoryDesktopUpdateSatisfiedAsync(serviceProvider))

            return false;



        // 무결성/운영 점검은 사용자 작업을 막지 않도록 메인 화면 표시 후 백그라운드에서 실행한다.

        // 필수 업데이트처럼 계속 사용하면 안 되는 조건만 로그인 직후 차단한다.

        return true;

    }



    private static async Task<bool> EnsureMandatoryDesktopUpdateSatisfiedAsync(IServiceProvider serviceProvider)

    {

        var session = serviceProvider.GetRequiredService<SessionState>();

        if (!session.IsLoggedIn || session.IsOfflineMode)

            return true;



        var diagnostics = serviceProvider.GetService<SyncDiagnosticsService>();



        try

        {

            var api = serviceProvider.GetRequiredService<ErpApiClient>();

            var updateService = new DesktopAppUpdateService(api);

            var update = await updateService.CheckForUpdatesAsync(ct: CancellationToken.None);

            if (!update.RequiresImmediateUpdate)

                return true;



            var requiredVersion = string.IsNullOrWhiteSpace(update.MinimumSupportedVersion)

                ? update.LatestVersion

                : update.MinimumSupportedVersion;

            var message = update.IsBelowMinimumSupportedVersion

                ? $"현재 버전 {update.CurrentVersion}은 서버 최소 허용 버전 {requiredVersion}보다 낮아 더 이상 사용할 수 없습니다.{Environment.NewLine}{Environment.NewLine}업데이트를 완료한 뒤 다시 실행하세요."

                : $"현재 버전 {update.CurrentVersion}에서는 필수 PC 업데이트가 필요합니다.{Environment.NewLine}필수 버전: {update.LatestVersion}{Environment.NewLine}{Environment.NewLine}업데이트를 완료한 뒤 다시 실행하세요.";



            AppLogger.Warn("UPDATE", message);

            if (diagnostics is not null)

            {

                await diagnostics.RecordIssueAsync(

                    phase: "startup-version-check",

                    rawMessage: message,

                    severity: "Warning");

            }



            MessageBox.Show(

                message,

                "필수 업데이트",

                MessageBoxButton.OK,

                MessageBoxImage.Warning);

            return false;

        }

        catch (Exception ex)

        {

            AppLogger.Warn("UPDATE", $"시작 시 필수 업데이트 확인 실패: {ex.Message}");

            if (diagnostics is not null)

            {

                await diagnostics.RecordIssueAsync(

                    phase: "startup-version-check",

                    rawMessage: ex.InnerException?.Message ?? ex.Message,

                    exception: ex,

                    severity: "Warning");

            }



            return true;

        }

    }



    private static async Task RunStartupIntegrityCheckAsync(

        IServiceProvider serviceProvider,

        bool showUserAlert = true,

        Action<string>? updateStatus = null)

    {

        var diagnostics = serviceProvider.GetService<SyncDiagnosticsService>();



        try

        {

            var startupIntegrity = serviceProvider.GetRequiredService<StartupIntegrityService>();

            var result = await startupIntegrity.RunAsync(CancellationToken.None);

            if (string.IsNullOrWhiteSpace(result.Message))

                return;



            AppLogger.Info("MAINT", result.Message);

            if (diagnostics is not null)

            {

                await diagnostics.RecordIssueAsync(

                    phase: "startup-integrity",

                    rawMessage: result.Message,

                    severity: result.RequiresUserAttention ? "Warning" : "Info",

                    recoveryAttempted: result.RefreshAttempted,

                    recoverySucceeded: result.RefreshSucceeded);

            }



            if (!result.RequiresUserAttention)

            {

                if (!string.IsNullOrWhiteSpace(result.Message))

                    updateStatus?.Invoke(result.Message);

                return;

            }



            if (!showUserAlert)

            {

                updateStatus?.Invoke("시작 운영 점검에서 확인이 필요한 항목이 있습니다. 업무는 바로 진행할 수 있으며, 환경설정 > 동기화 진단에서 상세 내용을 확인하세요.");

                return;

            }



            MessageBox.Show(

                result.Message,

                "시작 무결성 점검",

                MessageBoxButton.OK,

                MessageBoxImage.Warning);

        }

        catch (Exception ex)

        {

            AppLogger.Warn("MAINT", $"시작 시 무결성 점검 실패: {ex.Message}");

            if (diagnostics is not null)

            {

                await diagnostics.RecordIssueAsync(

                    phase: "startup-integrity",

                    rawMessage: ex.InnerException?.Message ?? ex.Message,

                    exception: ex,

                    severity: "Warning");

            }

        }

    }



    private async Task TryRecordStartupDiagnosticAsync(Exception ex)

    {

        if (_services is null)

            return;



        try

        {

            await using var scope = _services.CreateAsyncScope();

            var diagnostics = scope.ServiceProvider.GetService<SyncDiagnosticsService>();

            if (diagnostics is null)

                return;



            await diagnostics.RecordIssueAsync(

                phase: "startup-fatal",

                rawMessage: ex.InnerException?.Message ?? ex.Message,

                exception: ex,

                severity: "Error");

        }

        catch

        {

            // startup 진단 저장 실패가 앱 종료를 막지 않도록 무시

        }

    }



    protected override void OnExit(ExitEventArgs e)

    {

        _autoSaveTimer?.Stop();

        DispatcherUnhandledException -= HandleDispatcherUnhandledException;

        AppDomain.CurrentDomain.UnhandledException -= HandleAppDomainUnhandledException;

        TaskScheduler.UnobservedTaskException -= HandleTaskSchedulerUnhandledException;

        _saveCycleLock.Dispose();

        _services?.Dispose();

        base.OnExit(e);

    }



    private void HandleDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)

    {

        ReportUnexpectedException("UI Thread Unhandled Exception", args.Exception, showAlert: !_shutdownInProgress);

        args.Handled = true;

    }



    private void HandleAppDomainUnhandledException(object? sender, UnhandledExceptionEventArgs args)

    {

        if (args.ExceptionObject is Exception ex)

            ReportUnexpectedException("AppDomain Unhandled Exception", ex, showAlert: !args.IsTerminating && !_shutdownInProgress);

        else

            AppLogger.Error("APP", "AppDomain Unhandled Exception (non-exception payload)");

    }



    private void HandleTaskSchedulerUnhandledException(object? sender, UnobservedTaskExceptionEventArgs args)

    {

        ReportUnexpectedException("TaskScheduler Unobserved Exception", args.Exception, showAlert: !_shutdownInProgress);

        args.SetObserved();

    }



    private void ReportUnexpectedException(string context, Exception ex, bool showAlert)

    {

        if (IsBenignShutdownException(ex))

        {

            AppLogger.Warn("APP", $"{context} during shutdown ignored: {ex.Message}");

            return;

        }



        AppLogger.Error("APP", context, ex);



        if (!showAlert)

            return;



        if (Interlocked.Exchange(ref _unexpectedErrorDialogOpen, 1) == 1)

            return;



        try

        {

            MessageBox.Show(

                $"예기치 않은 오류가 발생했습니다.{Environment.NewLine}{ex.Message}",

                "거래플랜 오류",

                MessageBoxButton.OK,

                MessageBoxImage.Warning);

        }

        finally

        {

            Interlocked.Exchange(ref _unexpectedErrorDialogOpen, 0);

        }

    }



    private static bool IsBenignShutdownException(Exception ex)

    {

        if (ex is ObjectDisposedException)

            return true;



        var message = ex.ToString();

        return message.Contains("disposed context", StringComparison.OrdinalIgnoreCase)

               || message.Contains("Object name: 'LocalDbContext'", StringComparison.OrdinalIgnoreCase)

               || message.Contains("The application is shutting down", StringComparison.OrdinalIgnoreCase);

    }

    private void StartAutoSaveTimer(IServiceProvider sp, MainViewModel mainVm)

    {

        _autoSaveTimer?.Stop();

        _autoSaveTimer = new DispatcherTimer(DispatcherPriority.Background)

        {

            Interval = TimeSpan.FromMinutes(15)

        };



        _autoSaveTimer.Tick += (_, _) => UiTaskHelper.Forget(

            RunPeriodicSaveCycleAsync(sp, mainVm),

            "APP",

            "주기 저장",

            ex => AppLogger.Error("APP", "Periodic save cycle failure", ex));



        _autoSaveTimer.Start();

    }



    private async Task RunPeriodicSaveCycleAsync(IServiceProvider sp, MainViewModel mainVm)

        => await RunSaveCycleAsync(sp, mainVm, isShutdown: false);



    private void HandleMainWindowClosing(MainWindow mainWin, IServiceProvider sp, MainViewModel mainVm, CancelEventArgs args)

    {

        if (_updateShutdownRequested)

        {

            _autoSaveTimer?.Stop();

            mainWin.BeginShutdownProtection();

            _shutdownInProgress = true;

            return;

        }



        if (_restartToLoginRequested)

        {

            _autoSaveTimer?.Stop();

            mainWin.BeginShutdownProtection();

            _shutdownInProgress = true;

            return;

        }



        if (_shutdownInProgress)

            return;



        args.Cancel = true;

        _shutdownInProgress = true;

        _autoSaveTimer?.Stop();

        mainWin.BeginShutdownProtection();



        UiTaskHelper.Forget(

            HandleMainWindowClosingAsync(mainWin, sp, mainVm),

            "APP",

            "앱 종료 처리",

            ex => AppLogger.Error("APP", "Shutdown sync/backup failure", ex));

    }



    private async Task HandleMainWindowClosingAsync(MainWindow mainWin, IServiceProvider sp, MainViewModel mainVm)

    {

        mainVm.SyncStatus = "종료 전 서버와 동기화하고 데이터를 저장합니다.";

        var savingPopup = ShowShutdownSavingPopup(mainWin);

        mainWin.IsEnabled = false;

        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);



        var shouldClose = true;

        try

        {

            var result = await RunSaveCycleAsync(sp, mainVm, isShutdown: true);

            if (!result.SyncSucceeded && result.RemainingDirtyCount > 0)
            {
                AppLogger.Warn("APP", $"Shutdown continues with {result.RemainingDirtyCount:N0} pending sync item(s). They remain saved locally and will sync on the next run.");
                mainVm.SyncStatus = $"로컬 저장 완료. 미동기화 {result.RemainingDirtyCount:N0}건은 다음 실행 시 다시 동기화됩니다.";
            }

        }

        catch (Exception ex)

        {

            shouldClose = false;

            _shutdownInProgress = false;

            mainWin.IsEnabled = true;

            mainWin.EndShutdownProtection();

            StartAutoSaveTimer(sp, mainVm);

            mainVm.SyncStatus = "종료 전 서버 동기화 확인이 필요합니다.";

            MessageBox.Show(

                $"종료 전 동기화 확인이 필요합니다.{Environment.NewLine}{ex.Message}",

                "거래플랜 오류",

                MessageBoxButton.OK,

                MessageBoxImage.Error);

            throw;

        }

        finally

        {

            try

            {

                savingPopup.Close();

            }

            catch

            {

                // ignored

            }



            if (shouldClose)

                mainWin.Close();

        }

    }



    private async Task<bool> TryInitializeMainWindowAsync(MainWindow mainWin, MainViewModel mainVm, bool deferStartupNotifications = false)

    {

        try

        {

            await mainWin.InitAsync(deferStartupNotifications);

            mainWin.QueueDesktopUiSmokeSelfTestIfRequested();

            return true;

        }

        catch (Exception ex)

        {

            AppLogger.Error("APP", "Main window initialization failed", ex);

            await TryRecordStartupDiagnosticAsync(ex);

            mainVm.SyncStatus = "초기 로딩 일부에 실패했지만 앱은 계속 사용할 수 있습니다. 필요한 경우 동기화 진단을 확인하세요.";

            MessageBox.Show(

                $"초기 로딩 중 일부 오류가 발생했습니다.{Environment.NewLine}{ex.Message}{Environment.NewLine}{Environment.NewLine}앱은 계속 실행되며 필요한 데이터는 다시 불러올 수 있습니다.",

                "거래플랜 경고",

                MessageBoxButton.OK,

                MessageBoxImage.Warning);

            return false;

        }

    }



    private Task TryRunDeferredLegacyMigrationAsync()

    {

        AppLogger.Info("LEGACY", "레거시 자동 마이그레이션은 비활성화되어 환경설정의 백업/이전 데이터 관리에서만 수동 실행합니다.");

        return Task.CompletedTask;

    }



    private async Task RunPostLoginSyncThenStartupNotificationsAsync(

        MainWindow mainWin,

        MainViewModel mainVm,

        IServiceProvider serviceProvider,

        SessionState session,

        Window? startupSyncPopup,

        bool showDeferredStartupNotifications,

        Task? initialDashboardLoadTask = null)

    {

        Task? syncTask = null;

        var integrityPromptReason = session.IsOfflineMode ? "오프라인 로컬 점검" : "로그인 후 동기화";

        try

        {

            if (initialDashboardLoadTask is not null)

            {

                try

                {

                    await initialDashboardLoadTask;

                }

                catch (Exception ex)

                {

                    AppLogger.Warn("APP", $"초기 대시보드 로드 완료 대기 중 오류: {ex.Message}");

                }

            }



            if (!mainWin.IsLoaded)

                return;



            syncTask = await StartPostLoginSyncWithPopupAsync(mainWin, mainVm, session, startupSyncPopup);

        }

        finally

        {

            if (showDeferredStartupNotifications && mainWin.IsLoaded)

            {

                await Dispatcher.InvokeAsync(

                    mainWin.ShowDeferredStartupNotifications,

                    DispatcherPriority.Background);

            }

        }



        if (syncTask is not null)

            QueuePostLoginSyncContinuation(mainWin, mainVm, serviceProvider, syncTask, integrityPromptReason);

    }



    private async Task<Task?> StartPostLoginSyncWithPopupAsync(MainWindow mainWin, MainViewModel mainVm, SessionState session, Window? existingPopup = null)

    {

        CloseStartupSyncPopup(mainWin, existingPopup);



        if (session.IsOfflineMode)

        {

            mainVm.SyncStatus = "오프라인 모드입니다. 로컬 점검은 백그라운드에서 진행합니다.";

            return mainVm.RunPostLoginSyncAsync();

        }



        try

        {

            _ = await mainVm.ShouldShowPostLoginSyncPopupAsync();

        }

        catch (Exception ex)

        {

            AppLogger.Warn("APP", $"시작 동기화 필요 여부 판단 실패: {ex.Message}");

        }



        mainVm.SyncStatus = "로그인 완료. 시작 동기화는 백그라운드에서 진행하므로 바로 작업할 수 있습니다.";

        return mainVm.RunPostLoginSyncAsync();

    }



    private static void QueuePostLoginSyncContinuation(

        MainWindow mainWin,

        MainViewModel mainVm,

        IServiceProvider serviceProvider,

        Task syncTask,

        string integrityPromptReason)

    {

        UiTaskHelper.Forget(

            CompletePostLoginSyncAndIntegrityAsync(mainWin, mainVm, serviceProvider, syncTask, integrityPromptReason),

            "APP",

            "로그인 후 자동 동기화 후속 점검",

            ex => AppLogger.Error("APP", "Post-login sync continuation failure", ex));

    }



    private static async Task CompletePostLoginSyncAndIntegrityAsync(

        MainWindow mainWin,

        MainViewModel mainVm,

        IServiceProvider serviceProvider,

        Task syncTask,

        string integrityPromptReason)

    {

        await syncTask;



        if (mainWin.IsLoaded)

        {

            try

            {

                await mainVm.ReloadAfterPassiveSyncAsync();

            }

            catch (Exception ex)

            {

                AppLogger.Warn("APP", $"로그인 후 동기화 완료 뒤 메인 목록 재조회 실패: {ex.Message}");

            }



            await RunStartupIntegrityCheckAsync(

                serviceProvider,

                showUserAlert: false,

                updateStatus: message => mainVm.SyncStatus = message);



            await mainWin.RunDataIntegrityScanAndPromptAsync(integrityPromptReason, showPrompt: false);

        }

    }



    private static void CloseStartupSyncPopup(MainWindow mainWin, Window? popup)

    {

        try

        {

            popup?.Close();

        }

        catch

        {

            // ignored

        }



        if (mainWin.IsLoaded)

        {

            mainWin.IsEnabled = true;

        }

    }



    private async Task<SaveCycleResult> RunSaveCycleAsync(IServiceProvider sp, MainViewModel mainVm, bool isShutdown)

    {

        if (isShutdown)

        {

            await _saveCycleLock.WaitAsync();

        }

        else

        {

            if (!await _saveCycleLock.WaitAsync(0))

                return new SaveCycleResult(false, false, 0, false);

        }



        try

        {

            if (!isShutdown)

                mainVm.SyncStatus = "자동 저장 중...";



            var session = sp.GetRequiredService<SessionState>();

            var syncAttempted = false;

            var syncSucceeded = true;

            if (isShutdown && !session.IsOfflineMode)

            {

                var sync = sp.GetRequiredService<SyncService>();

                syncAttempted = true;

                using var shutdownSyncCts = new CancellationTokenSource(ShutdownSyncTimeout);

                try
                {
                    syncSucceeded = await sync.FlushPendingChangesAsync(shutdownSyncCts.Token);
                }
                catch (OperationCanceledException)
                {
                    syncSucceeded = false;
                    AppLogger.Warn("APP", $"Shutdown sync timed out after {ShutdownSyncTimeout.TotalSeconds:N0}s. Pending changes will remain in the local sync queue.");
                }
                catch (Exception ex)
                {
                    syncSucceeded = false;
                    AppLogger.Error("APP", "Shutdown sync failed. Pending changes will remain in the local sync queue.", ex);
                }

            }



            var backup = sp.GetRequiredService<BackupService>();

            var backupOk = await backup.BackupNowAsync();

            if (!backupOk)

                AppLogger.Warn("APP", $"Background save completed but backup failed. isShutdown={isShutdown}");



            var localState = sp.GetRequiredService<LocalStateService>();

            var remainingDirtyCount = await localState.CountDirtyAsync(session);



            if (isShutdown)

            {

                var pendingMessage = remainingDirtyCount > 0

                    ? await localState.GetPendingSyncWaitingMessageAsync(session, ct: CancellationToken.None)

                    : null;

                mainVm.SyncStatus = remainingDirtyCount == 0

                    ? "저장이 완료되었습니다. 종료합니다."

                    : pendingMessage ?? $"서버 반영 대기 데이터 {remainingDirtyCount}건이 남아 있습니다.";

            }

            else

            {

                mainVm.SyncStatus = $"자동 저장 완료 {DateTime.Now:HH:mm:ss}";

            }



            return new SaveCycleResult(syncAttempted, syncSucceeded, remainingDirtyCount, backupOk);

        }

        finally

        {

            _saveCycleLock.Release();

        }

    }



    private static Window ShowShutdownSavingPopup(Window owner)

        => ShowActivityPopup(owner, "거래플랜", "종료 전 저장 중입니다.\n데이터를 서버와 동기화하고 있습니다...");



    private static Window ShowActivityPopup(Window owner, string title, string message, bool topmost = true, bool showActivated = true)

    {

        var heading = new TextBlock

        {

            Text = "동기화 중",

            FontFamily = new FontFamily("맑은 고딕"),

            FontSize = 20,

            FontWeight = FontWeights.Bold,

            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0F2C5C")),

            TextAlignment = TextAlignment.Center,

            Margin = new Thickness(0, 0, 0, 10)

        };



        var text = new TextBlock

        {

            Text = message,

            FontFamily = new FontFamily("맑은 고딕"),

            FontSize = 14,

            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2C3E50")),

            TextAlignment = TextAlignment.Center,

            TextWrapping = TextWrapping.Wrap,

            Margin = new Thickness(0, 0, 0, 14)

        };



        var progress = new ProgressBar

        {

            Height = 16,

            IsIndeterminate = true,

            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1565C0")),

            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D9E6F5"))

        };



        var hint = new TextBlock

        {

            Text = "앱이 멈춘 것이 아니며, 완료 후 자동으로 닫힙니다.",

            FontFamily = new FontFamily("맑은 고딕"),

            FontSize = 12,

            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#5C6F82")),

            TextAlignment = TextAlignment.Center,

            Margin = new Thickness(0, 12, 0, 0)

        };



        var content = new StackPanel

        {

            Orientation = Orientation.Vertical

        };

        content.Children.Add(heading);

        content.Children.Add(text);

        content.Children.Add(progress);

        content.Children.Add(hint);



        var root = new Border

        {

            Background = Brushes.White,

            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#C8D6E5")),

            BorderThickness = new Thickness(1),

            CornerRadius = new CornerRadius(12),

            Padding = new Thickness(24, 22, 24, 20),

            Width = 420,

            Child = content

        };



        var popup = new Window

        {

            Title = title,

            Content = root,

            Owner = owner,

            ShowInTaskbar = false,

            ResizeMode = ResizeMode.NoResize,

            WindowStartupLocation = WindowStartupLocation.CenterOwner,

            WindowStyle = WindowStyle.None,

            AllowsTransparency = true,

            SizeToContent = SizeToContent.WidthAndHeight,

            Background = Brushes.Transparent,

            Topmost = topmost,

            ShowActivated = showActivated

        };



        popup.Show();

        return popup;

    }



    private void RestartToLoginIfRequested()

    {

        if (!_restartToLoginRequested)

            return;



        _restartToLoginRequested = false;



        try

        {

            var executablePath = Environment.ProcessPath;

            if (string.IsNullOrWhiteSpace(executablePath))

                executablePath = Process.GetCurrentProcess().MainModule?.FileName;



            if (string.IsNullOrWhiteSpace(executablePath))

                return;



            Process.Start(new ProcessStartInfo

            {

                FileName = executablePath,

                WorkingDirectory = AppContext.BaseDirectory,

                UseShellExecute = true

            });

        }

        catch (Exception ex)

        {

            AppLogger.Warn("AUTH", $"로그아웃 후 로그인 화면 재시작 실패: {ex.Message}");

        }

    }

}
