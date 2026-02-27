using System.Windows;
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

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ── 로그인 창이 닫혀도 앱이 종료되지 않도록 ──────────────────────────
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

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
            services.AddTransient<LocalStateService>();
            services.AddTransient<SyncService>();
            services.AddTransient<StatementPrintService>();
            services.AddTransient<IPrintService, WpfInvoicePrintService>();
            services.AddTransient<BackupService>();
            services.AddTransient<RecentSelectionService>();
            services.AddTransient<LoginViewModel>();
            services.AddTransient<MainViewModel>();

            _services = services.BuildServiceProvider();

            // DB 초기화
            await using (var scope = _services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();
                await LocalDbInitializer.InitializeAsync(db);
            }

            // 로그인
            var loginVm = _services.GetRequiredService<LoginViewModel>();
            await loginVm.InitializeAsync();
            var loginWin = new LoginWindow(loginVm);
            var loggedIn = loginWin.ShowDialog();

            if (loggedIn != true)
            {
                Shutdown();
                return;
            }

            // 미동기화 백업 안내
            await using (var scope = _services.CreateAsyncScope())
            {
                var localState = scope.ServiceProvider.GetRequiredService<LocalStateService>();
                var dirtyCount = await localState.CountDirtyAsync();
                if (dirtyCount > 0)
                {
                    var res = MessageBox.Show(
                        $"미동기화 데이터 {dirtyCount}건이 있습니다. 백업 후 시작하시겠습니까?",
                        "미동기화 데이터 감지", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (res == MessageBoxResult.Yes)
                    {
                        var backup = scope.ServiceProvider.GetRequiredService<BackupService>();
                        var ok = await backup.BackupNowAsync();
                        MessageBox.Show(ok ? "백업 완료." : "백업 실패.", "백업", MessageBoxButton.OK);
                    }
                }
            }

            // 메인 창: 단일 스코프로 수명 관리
            var mainScope = _services.CreateScope();
            var sp = mainScope.ServiceProvider;
            var mainVm   = sp.GetRequiredService<MainViewModel>();
            var mainWin  = new MainWindow(
                mainVm,
                sp.GetRequiredService<LocalStateService>(),
                sp.GetRequiredService<StatementPrintService>(),
                sp.GetRequiredService<IPrintService>(),
                sp.GetRequiredService<SessionState>());

            MainWindow = mainWin;
            // 메인 창이 닫히면 앱 종료
            mainWin.Closed += (_, _) =>
            {
                mainScope.Dispose();
                Shutdown();
            };
            ShutdownMode = ShutdownMode.OnExplicitShutdown; // 명시적 종료 유지
            mainWin.Show();
            await mainWin.InitAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"시작 오류:\n{ex.Message}\n\n{ex.InnerException?.Message}",
                "코덱스 레거시 판매관리 오류", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }
}
