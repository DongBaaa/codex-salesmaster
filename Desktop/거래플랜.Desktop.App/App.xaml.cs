using System.ComponentModel;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

    private ServiceProvider? _services;
    private bool _shutdownInProgress;
    private readonly SemaphoreSlim _saveCycleLock = new(1, 1);
    private DispatcherTimer? _autoSaveTimer;
    private int _unexpectedErrorDialogOpen;
    private bool _restartToLoginRequested;

    internal void RequestRestartToLogin()
        => _restartToLoginRequested = true;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += HandleDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += HandleAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += HandleTaskSchedulerUnhandledException;

        try
        {
            DesktopAppUpdateService.TryCleanupStaleUpdateArtifacts();

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

            mainWin.Closing += (_, args) => HandleMainWindowClosing(mainWin, sp, mainVm, args);

            mainWin.Closed += (_, _) =>
            {
                _autoSaveTimer?.Stop();
                var session = sp.GetRequiredService<SessionState>();
                _services?.GetRequiredService<OfficeAccessService>().ClearSessionAccess(session);
                session.Clear();
                mainScope.Dispose();
                RestartToLoginIfRequested();
                Shutdown();
            };

            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            mainWin.Show();

            var initSucceeded = await TryInitializeMainWindowAsync(mainWin, mainVm);
            if (initSucceeded)
            {
                UiTaskHelper.Forget(
                    RunPostLoginSyncWithPopupAsync(mainWin, mainVm, sp.GetRequiredService<SessionState>()),
                    "APP",
                    "로그인 후 자동 동기화",
                    ex => AppLogger.Error("APP", "Post-login sync scheduling failure", ex));
            }
        }
        catch (Exception ex)
        {
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

    private async Task<bool> TryInitializeMainWindowAsync(MainWindow mainWin, MainViewModel mainVm)
    {
        try
        {
            await mainWin.InitAsync();
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("APP", "Main window initialization failed", ex);
            await TryRecordStartupDiagnosticAsync(ex);
            mainVm.SyncStatus = "초기 로딩 일부에 실패했지만 앱은 계속 사용할 수 있습니다. 필요한 경우 동기화 진단을 확인하세요.";
            MessageBox.Show(
                $"초기 로딩 중 일부 오류가 발생했습니다.{Environment.NewLine}{ex.Message}{Environment.NewLine}{Environment.NewLine}앱은 제한 모드로 계속 실행됩니다.",
                "거래플랜 경고",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }
    }

    private async Task RunPostLoginSyncWithPopupAsync(MainWindow mainWin, MainViewModel mainVm, SessionState session)
    {
        if (session.IsOfflineMode)
        {
            await mainVm.RunPostLoginSyncAsync();
            return;
        }

        Window? popup = null;
        try
        {
            await Dispatcher.InvokeAsync(() =>
            {
                mainWin.IsEnabled = false;
                popup = ShowActivityPopup(
                    mainWin,
                    "거래플랜 동기화",
                    "로그인 후 데이터를 불러오고 있습니다.\n첫 동기화는 데이터 양에 따라 잠시 걸릴 수 있습니다.");
            }, DispatcherPriority.Send);

            await Dispatcher.InvokeAsync(() =>
            {
                popup?.UpdateLayout();
            }, DispatcherPriority.Render);

            await Task.Delay(150);

            await mainVm.RunPostLoginSyncAsync();
        }
        finally
        {
            await Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    popup?.Close();
                }
                catch
                {
                    // ignored
                }

                mainWin.IsEnabled = true;
                mainWin.Activate();
            }, DispatcherPriority.Background);
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
                syncSucceeded = await sync.FlushPendingChangesAsync();
            }

            var backup = sp.GetRequiredService<BackupService>();
            var backupOk = await backup.BackupNowAsync();
            if (!backupOk)
                AppLogger.Warn("APP", $"Background save completed but backup failed. isShutdown={isShutdown}");

            var localState = sp.GetRequiredService<LocalStateService>();
            var remainingDirtyCount = await localState.CountDirtyAsync(sp.GetRequiredService<SessionState>());

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
        => ShowActivityPopup(owner, "거래플랜", "종료 전 저장 중입니다.\n데이터를 서버와 동기화하고 있습니다...");

    private static Window ShowActivityPopup(Window owner, string title, string message)
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
            Topmost = true
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
