using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SalesMaster.Desktop.App.Configuration;
using SalesMaster.Desktop.App.Data;
using SalesMaster.Desktop.App.Services;
using SalesMaster.Desktop.App.ViewModels;
using SalesMaster.Desktop.App.Views;

namespace SalesMaster.Desktop.App;

public partial class App : Application
{
    private ServiceProvider? _services;
    private bool _shutdownInProgress;
    private readonly SemaphoreSlim _saveCycleLock = new(1, 1);
    private DispatcherTimer? _autoSaveTimer;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += (_, args) =>
        {
            AppLogger.Error("APP", "UI Thread Unhandled Exception", args.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                AppLogger.Error("APP", "AppDomain Unhandled Exception", ex);
            else
                AppLogger.Error("APP", "AppDomain Unhandled Exception (non-exception payload)");
        };

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
            services.AddTransient<LocalStateService>();
            services.AddTransient<RentalStateService>();
            services.AddTransient<RentalDocumentService>();
            services.AddTransient<SyncService>();
            services.AddTransient<StatementPrintService>();
            services.AddTransient<IPrintService, WpfInvoicePrintService>();
            services.AddTransient<BackupService>();
            services.AddTransient<RecentSelectionService>();
            services.AddTransient<LoginViewModel>();
            services.AddTransient<MainViewModel>();

            _services = services.BuildServiceProvider();

            await using (var scope = _services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();
                await LocalDbInitializer.InitializeAsync(db);
            }

            var loginVm = _services.GetRequiredService<LoginViewModel>();
            await loginVm.InitializeAsync();
            var loginWin = new LoginWindow(loginVm);
            var loggedIn = loginWin.ShowDialog();

            if (loggedIn != true)
            {
                Shutdown();
                return;
            }

            // Startup policy: try one immediate sync; on failure auto-backup only (no blocking popup).
            await using (var startupScope = _services.CreateAsyncScope())
            {
                var session = startupScope.ServiceProvider.GetRequiredService<SessionState>();
                var localState = startupScope.ServiceProvider.GetRequiredService<LocalStateService>();
                var dirtyBefore = await localState.CountDirtyAsync();

                if (!session.IsOfflineMode)
                {
                    var sync = startupScope.ServiceProvider.GetRequiredService<SyncService>();
                    var syncOk = await sync.TrySyncAsync();

                    if (!syncOk)
                    {
                        var dirtyAfter = await localState.CountDirtyAsync();
                        if (dirtyBefore > 0 || dirtyAfter > 0)
                        {
                            var backup = startupScope.ServiceProvider.GetRequiredService<BackupService>();
                            var backupOk = await backup.BackupNowAsync();
                            AppLogger.Warn(
                                "APP",
                                $"Startup sync failed with {dirtyAfter} dirty rows. Auto-backup {(backupOk ? "succeeded" : "failed")}.");
                        }
                    }
                }
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
                sp.GetRequiredService<ErpApiClient>());

            MainWindow = mainWin;

            StartAutoSaveTimer(sp, mainVm);

            // Shutdown policy: save/sync before final close.
            mainWin.Closing += async (_, args) =>
            {
                if (_shutdownInProgress)
                    return;

                args.Cancel = true;
                _shutdownInProgress = true;
                _autoSaveTimer?.Stop();

                mainVm.SyncStatus = "종료 시 저장중입니다. 데이터를 저장한 뒤 종료합니다.";
                var savingPopup = ShowShutdownSavingPopup(mainWin);
                mainWin.IsEnabled = false;
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);

                try
                {
                    await RunSaveCycleAsync(sp, mainVm, isShutdown: true);
                }
                catch (Exception ex)
                {
                    AppLogger.Error("APP", "Shutdown sync/backup failure", ex);
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
        _saveCycleLock.Dispose();
        _services?.Dispose();
        base.OnExit(e);
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

    private async Task RunSaveCycleAsync(IServiceProvider sp, MainViewModel mainVm, bool isShutdown)
    {
        if (isShutdown)
        {
            await _saveCycleLock.WaitAsync();
        }
        else
        {
            if (!await _saveCycleLock.WaitAsync(0))
                return;
        }

        try
        {
            if (!isShutdown)
                mainVm.SyncStatus = "자동 저장중입니다...";

            var session = sp.GetRequiredService<SessionState>();
            if (isShutdown && !session.IsOfflineMode)
            {
                var sync = sp.GetRequiredService<SyncService>();
                await sync.TrySyncAsync();
            }

            var backup = sp.GetRequiredService<BackupService>();
            var backupOk = await backup.BackupNowAsync();

            if (isShutdown)
                mainVm.SyncStatus = backupOk ? "저장 완료. 종료합니다." : "저장 완료(백업 실패). 종료합니다.";
            else
                mainVm.SyncStatus = backupOk
                    ? $"자동 저장 완료 {DateTime.Now:HH:mm:ss}"
                    : $"자동 저장 완료(백업 실패) {DateTime.Now:HH:mm:ss}";
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
            Text = "종료 시 저장중입니다.\n데이터를 저장하고 있습니다...",
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
