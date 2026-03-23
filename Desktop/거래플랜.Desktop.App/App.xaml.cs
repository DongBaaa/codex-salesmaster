using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using 거래플랜.Desktop.App.Configuration;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Desktop.App.Views;

namespace 거래플랜.Desktop.App;

public partial class App : Application
{
    private sealed record SaveCycleResult(bool SyncAttempted, bool SyncSucceeded, int RemainingDirtyCount, bool BackupSucceeded);

    private ServiceProvider? _services;
    private bool _shutdownInProgress;
    private readonly SemaphoreSlim _saveCycleLock = new(1, 1);
    private DispatcherTimer? _autoSaveTimer;
    private int _unexpectedErrorDialogOpen;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += HandleDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += HandleAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += HandleTaskSchedulerUnhandledException;

        try
        {
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
            services.AddScoped<LocalStateService>();
            services.AddScoped<RentalStateService>();
            services.AddScoped<RentalDocumentService>();
            services.AddScoped<SyncService>();
            services.AddScoped<BackupService>();
            services.AddScoped<RecentSelectionService>();
            services.AddTransient<StatementPrintService>();
            services.AddTransient<IPrintService, WpfInvoicePrintService>();
            services.AddTransient<LoginViewModel>();
            services.AddTransient<MainViewModel>();

            _services = services.BuildServiceProvider();

            await using (var scope = _services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();
                await LocalDbInitializer.InitializeAsync(db);

                try
                {
                    var localState = scope.ServiceProvider.GetRequiredService<LocalStateService>();
                    var legacyMigration = new LegacyDataMigrationService(localState);
                    var migrationResult = await legacyMigration.TryAutoMigrateLocalDataAsync();

                    if (migrationResult.Applied)
                        AppLogger.Info("LEGACY", $"자동 마이그레이션 완료. source={migrationResult.SourceType}, path={migrationResult.SourcePath}, message={migrationResult.Message}");
                    else if (!string.IsNullOrWhiteSpace(migrationResult.Message))
                        AppLogger.Info("LEGACY", $"자동 마이그레이션 건너뜀. source={migrationResult.SourceType}, path={migrationResult.SourcePath}, message={migrationResult.Message}");
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("LEGACY", $"자동 마이그레이션 실패. {ex.Message}");
                }
            }

            bool? loggedIn;
            using (var loginScope = _services.CreateScope())
            {
                var loginVm = loginScope.ServiceProvider.GetRequiredService<LoginViewModel>();
                await loginVm.InitializeAsync();
                var loginWin = new LoginWindow(loginVm);
                loggedIn = loginWin.ShowDialog();
            }

            if (loggedIn != true)
            {
                Shutdown();
                return;
            }

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
                sp.GetRequiredService<SyncService>());

            MainWindow = mainWin;

            StartAutoSaveTimer(sp, mainVm);

            mainWin.Closing += async (_, args) =>
            {
                if (_shutdownInProgress)
                    return;

                args.Cancel = true;
                _shutdownInProgress = true;
                _autoSaveTimer?.Stop();
                mainWin.BeginShutdownProtection();

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
                        shouldClose = false;
                        _shutdownInProgress = false;
                        mainWin.IsEnabled = true;
                        mainWin.EndShutdownProtection();
                        StartAutoSaveTimer(sp, mainVm);
                        mainVm.SyncStatus = "종료 전 서버 동기화가 완료되지 않았습니다. 동기화 후 다시 종료해 주세요.";
                        MessageBox.Show(
                            $"서버에 아직 반영되지 않은 변경 데이터가 {result.RemainingDirtyCount}건 남아 있습니다.{Environment.NewLine}" +
                            "네트워크를 확인한 뒤 자동/수동 동기화를 완료하고 다시 종료해 주세요.",
                            "거래플랜 동기화 필요",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Error("APP", "Shutdown sync/backup failure", ex);
                    shouldClose = false;
                    _shutdownInProgress = false;
                    mainWin.IsEnabled = true;
                    mainWin.EndShutdownProtection();
                    StartAutoSaveTimer(sp, mainVm);
                    mainVm.SyncStatus = "종료 전 서버 동기화 처리 중 오류가 발생했습니다.";
                    MessageBox.Show(
                        $"종료 전 동기화 처리 중 오류가 발생했습니다.{Environment.NewLine}{ex.Message}",
                        "거래플랜 오류",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
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
            };

            mainWin.Closed += (_, _) =>
            {
                _autoSaveTimer?.Stop();
                mainScope.Dispose();
                Shutdown();
            };

            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            mainWin.Show();
            await mainWin.InitAsync();
            await mainVm.RunPostLoginSyncAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Error("APP", "Startup failure", ex);
            MessageBox.Show(
                $"시작 오류:\n{ex.Message}\n\n{ex.InnerException?.Message}",
                "거래플랜 오류",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
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

        _autoSaveTimer.Tick += async (_, _) =>
        {
            try
            {
                await RunSaveCycleAsync(sp, mainVm, isShutdown: false);
            }
            catch (Exception ex)
            {
                AppLogger.Error("APP", "Periodic save cycle failure", ex);
            }
        };

        _autoSaveTimer.Start();
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
                syncSucceeded = await sync.FlushPendingChangesAsync();
            }

            var backup = sp.GetRequiredService<BackupService>();
            var backupOk = await backup.BackupNowAsync();
            if (!backupOk)
                AppLogger.Warn("APP", $"Background save completed but backup failed. isShutdown={isShutdown}");

            var localState = sp.GetRequiredService<LocalStateService>();
            var remainingDirtyCount = await localState.CountDirtyAsync();

            if (isShutdown)
            {
                mainVm.SyncStatus = remainingDirtyCount == 0
                    ? "저장이 완료되었습니다. 종료합니다."
                    : $"서버 반영 대기 데이터 {remainingDirtyCount}건이 남아 있습니다.";
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
    {
        var text = new TextBlock
        {
            Text = "종료 전 저장 중입니다.\n데이터를 서버와 동기화하고 있습니다...",
            FontFamily = new FontFamily("맑은 고딕"),
            FontSize = 15,
            Foreground = Brushes.Black,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 14)
        };

        var progress = new ProgressBar
        {
            Height = 14,
            IsIndeterminate = true,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1565C0")),
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D9E6F5"))
        };

        var root = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(20),
            Width = 340,
            Children = { text, progress }
        };

        var popup = new Window
        {
            Title = "거래플랜",
            Content = root,
            Owner = owner,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowStyle = WindowStyle.ToolWindow,
            SizeToContent = SizeToContent.WidthAndHeight,
            Background = Brushes.White,
            Topmost = true
        };

        popup.Show();
        return popup;
    }
}
