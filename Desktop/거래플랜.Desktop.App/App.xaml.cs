using System.ComponentModel;

using System.IO;

using System.Threading;

using System.Windows;

using System.Windows.Controls;

using System.Windows.Media;

using System.Windows.Threading;

using System.Diagnostics;

using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.EntityFrameworkCore;
using 거래플랜.Shared.Contracts;

using 거래플랜.Desktop.App.Configuration;

using 거래플랜.Desktop.App.Data;

using 거래플랜.Desktop.App.Infrastructure;

using 거래플랜.Desktop.App.Services;

using 거래플랜.Desktop.App.ViewModels;

using 거래플랜.Desktop.App.Views;



namespace 거래플랜.Desktop.App;



public partial class App : Application

{

    private static int _globalWindowLayoutRegistration;

    public App()
    {
        if (Interlocked.Exchange(ref _globalWindowLayoutRegistration, 1) != 0)
            return;

        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnAnyWindowLoaded),
            handledEventsToo: true);
    }

    private static void OnAnyWindowLoaded(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Window window ||
            !ReferenceEquals(eventArgs.OriginalSource, window))
        {
            return;
        }

        if (ResponsiveWindowBehavior.GetIsGlobalLayoutExcluded(window))
            ResponsiveWindowBehavior.SetIsEnabled(window, false);
        else
            ResponsiveWindowBehavior.SetIsEnabled(window, true);
        FullTextLayoutBehavior.SetIsEnabled(window, true);
    }

    private sealed record SaveCycleResult(bool SyncAttempted, bool SyncSucceeded, int RemainingDirtyCount, bool BackupSucceeded);

    private static readonly TimeSpan ShutdownSyncTimeout = TimeSpan.FromSeconds(12);



    private ServiceProvider? _services;

    private SingleInstanceGuard? _singleInstanceGuard;

    private InstallRootUpdateGate? _installRootUpdateGate;

    private bool _shutdownInProgress;

    private readonly SemaphoreSlim _saveCycleLock = new(1, 1);
    private readonly SemaphoreSlim _mainWindowShutdownCoordinatorLock = new(1, 1);

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
    private int _compatibilityWindowOpen;
    private CancellationTokenSource? _mainScopeLifetimeCts;
    private Task _postLoginCompletionTask = Task.CompletedTask;
    private bool _postLoginDrainCompleted = true;
    private bool _postLoginWorkNeedsResumeAfterCanceledShutdown;
    private bool _postLoginDrainCloseQueued;
    private IServiceProvider? _activeMainScopeServiceProvider;
    private MainViewModel? _activeMainViewModel;
    private bool _coordinatedMainWindowCloseReady;
    private bool _fatalStartupShutdownRequested;
    private bool _runtimeCompatibilityShutdownRequested;
    private int _requestedShutdownExitCode;



    internal void RequestRestartToLogin()

    {

        _restartToLoginRequested = true;

        _coordinatedMainWindowCloseReady = false;
        _requestedShutdownExitCode = 0;

        CancelMainScopeBackgroundWork();

    }



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

        _coordinatedMainWindowCloseReady = false;
        _requestedShutdownExitCode = 0;

        _shutdownInProgress = true;

        _autoSaveTimer?.Stop();

        CancelMainScopeBackgroundWork();

        AppLogger.Info("UPDATE", "업데이트 적용을 위해 앱 종료를 시작합니다. 업데이트 준비 단계에서 dirty 동기화는 이미 완료되었습니다.");



        try

        {

            if (TryQueueActiveMainWindowShutdown())
                return;

            if (MainWindow is Window mainWindow && mainWindow.IsLoaded)
            {
                mainWindow.Close();
                return;
            }

        }

        catch (Exception ex)

        {

            AppLogger.Warn("UPDATE", $"업데이트 종료 중 메인 창 닫기 실패: {ex.Message}");

            if (TryRecoverActiveMainWindowAfterCanceledShutdown())
                return;

        }



        Shutdown(0);

    }

    internal static IReadOnlyList<string> GetInstallRecoveryStartupRoots(
        string appBaseDirectory,
        string? canonicalInstallRootOverride = null,
        string? legacyInstallRootOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appBaseDirectory);

        var appRoot = NormalizeInstallRoot(appBaseDirectory);
        var canonicalRoot = NormalizeInstallRoot(
            canonicalInstallRootOverride ??
            DesktopAppUpdateService.GetCanonicalInstallRoot());
        var legacyRoot = NormalizeInstallRoot(
            legacyInstallRootOverride ??
            GetLegacyInstallRoot());

        if (!string.Equals(appRoot, canonicalRoot, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(appRoot, legacyRoot, StringComparison.OrdinalIgnoreCase))
        {
            return [appRoot];
        }

        return new[] { canonicalRoot, legacyRoot }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static string GetLegacyInstallRoot()
    {
        var localAppData =
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException(
                "Local AppData 경로를 확인하지 못했습니다.");
        }

        return Path.Combine(localAppData, "Programs", "거래플랜");
    }

    private static string NormalizeInstallRoot(string installRoot)
        => InstallRootPathIdentity.Resolve(installRoot);

    internal static string? GetInstallRecoveryStartupBlockMessage(
        string installRoot,
        InstallRecoveryStateProbeResult? probeOverride = null)
    {
        if (probeOverride is null)
        {
            try
            {
                var canonicalInstallRoot =
                    DesktopAppUpdateService.GetCanonicalInstallRoot();
                var legacyInstallRoot = GetLegacyInstallRoot();
                var startupRoots = GetInstallRecoveryStartupRoots(
                    installRoot,
                    canonicalInstallRoot,
                    legacyInstallRoot);
                var legacyInstallRootCandidates =
                    GetLegacyRollbackInstallRootCandidates(
                        startupRoots,
                        [
                            installRoot,
                            canonicalInstallRoot,
                            legacyInstallRoot
                        ]);
                legacyInstallRootCandidates =
                    legacyInstallRootCandidates
                        .Where(
                            static pair =>
                                CanRecoverLegacyInstallRollbackState(
                                    pair.Key))
                        .ToDictionary(
                            static pair => pair.Key,
                            static pair => pair.Value,
                            StringComparer.OrdinalIgnoreCase);
                return GetInstallRecoveryStartupBlockMessage(
                    startupRoots,
                    GetLegacyRollbackArtifactRoots(),
                    legacyInstallRootCandidates,
                    InstallRecoveryStateProbe.Probe,
                    LegacyInstallRollbackStateProbe.Probe);
            }
            catch (Exception ex)
            {
                return GetInstallRecoveryStartupBlockMessage(
                    new InstallRecoveryStateProbeResult(
                        InstallRecoveryStateStatus.AccessError,
                        string.Empty,
                        ex));
            }
        }

        return GetInstallRecoveryStartupBlockMessage(probeOverride);
    }

    internal static string? GetInstallRecoveryStartupBlockMessage(
        IReadOnlyList<string> installRoots,
        Func<string, InstallRecoveryStateProbeResult> probe)
    {
        ArgumentNullException.ThrowIfNull(installRoots);
        ArgumentNullException.ThrowIfNull(probe);

        if (installRoots.Count == 0)
            throw new ArgumentException("At least one install root is required.", nameof(installRoots));

        foreach (var installRoot in installRoots)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);

            InstallRecoveryStateProbeResult result;
            try
            {
                result = probe(installRoot);
            }
            catch (Exception ex)
            {
                result = new InstallRecoveryStateProbeResult(
                    InstallRecoveryStateStatus.AccessError,
                    InstallRecoveryStateProbe.GetStatePath(installRoot),
                    ex);
            }

            var blockMessage = GetInstallRecoveryStartupBlockMessage(result);
            if (!string.IsNullOrWhiteSpace(blockMessage))
                return blockMessage;
        }

        return null;
    }

    internal static string? GetInstallRecoveryStartupBlockMessage(
        IReadOnlyList<string> installRoots,
        string legacyRollbackArtifactRoot,
        Func<string, InstallRecoveryStateProbeResult> generatedStateProbe,
        Func<string, string, InstallRecoveryStateProbeResult> legacyStateProbe)
        => GetInstallRecoveryStartupBlockMessage(
            installRoots,
            [legacyRollbackArtifactRoot],
            generatedStateProbe,
            legacyStateProbe);

    internal static string? GetInstallRecoveryStartupBlockMessage(
        IReadOnlyList<string> installRoots,
        IReadOnlyList<string> legacyRollbackArtifactRoots,
        Func<string, InstallRecoveryStateProbeResult> generatedStateProbe,
        Func<string, string, InstallRecoveryStateProbeResult> legacyStateProbe)
        => GetInstallRecoveryStartupBlockMessage(
            installRoots,
            legacyRollbackArtifactRoots,
            installRoots.ToDictionary(
                static installRoot => installRoot,
                static installRoot =>
                    (IReadOnlyList<string>)[installRoot],
                StringComparer.OrdinalIgnoreCase),
            generatedStateProbe,
            legacyStateProbe);

    internal static string? GetInstallRecoveryStartupBlockMessage(
        IReadOnlyList<string> installRoots,
        IReadOnlyList<string> legacyRollbackArtifactRoots,
        IReadOnlyDictionary<string, IReadOnlyList<string>>
            legacyInstallRootCandidates,
        Func<string, InstallRecoveryStateProbeResult> generatedStateProbe,
        Func<string, string, InstallRecoveryStateProbeResult> legacyStateProbe)
    {
        ArgumentNullException.ThrowIfNull(installRoots);
        ArgumentNullException.ThrowIfNull(legacyRollbackArtifactRoots);
        ArgumentNullException.ThrowIfNull(legacyInstallRootCandidates);
        ArgumentNullException.ThrowIfNull(generatedStateProbe);
        ArgumentNullException.ThrowIfNull(legacyStateProbe);

        if (installRoots.Count == 0)
            throw new ArgumentException("At least one install root is required.", nameof(installRoots));
        if (legacyRollbackArtifactRoots.Count == 0)
        {
            throw new ArgumentException(
                "At least one legacy rollback artifact root is required.",
                nameof(legacyRollbackArtifactRoots));
        }

        foreach (var installRoot in installRoots)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);

            var generatedBlockMessage =
                GetInstallRecoveryStartupBlockMessage(
                    InvokeInstallRecoveryProbe(
                        installRoot,
                        generatedStateProbe));
            if (!string.IsNullOrWhiteSpace(generatedBlockMessage))
                return generatedBlockMessage;

            foreach (var legacyRollbackArtifactRoot in
                     legacyRollbackArtifactRoots)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(
                    legacyRollbackArtifactRoot);

                if (!legacyInstallRootCandidates.TryGetValue(
                        installRoot,
                        out var candidateInstallRoots) ||
                    candidateInstallRoots.Count == 0)
                {
                    continue;
                }

                foreach (var candidateInstallRoot in
                         candidateInstallRoots)
                {
                    ArgumentException.ThrowIfNullOrWhiteSpace(
                        candidateInstallRoot);

                    InstallRecoveryStateProbeResult legacyResult;
                    try
                    {
                        legacyResult = legacyStateProbe(
                            legacyRollbackArtifactRoot,
                            candidateInstallRoot);
                    }
                    catch (Exception ex)
                    {
                        legacyResult = new InstallRecoveryStateProbeResult(
                            InstallRecoveryStateStatus.AccessError,
                            string.Empty,
                            ex);
                    }

                    var legacyBlockMessage =
                        GetInstallRecoveryStartupBlockMessage(legacyResult);
                    if (!string.IsNullOrWhiteSpace(legacyBlockMessage))
                        return legacyBlockMessage;
                }
            }
        }

        return null;
    }

    private static InstallRecoveryStateProbeResult InvokeInstallRecoveryProbe(
        string installRoot,
        Func<string, InstallRecoveryStateProbeResult> probe)
    {
        try
        {
            return probe(installRoot);
        }
        catch (Exception ex)
        {
            string statePath;
            try
            {
                statePath = InstallRecoveryStateProbe.GetStatePath(installRoot);
            }
            catch
            {
                statePath = string.Empty;
            }

            return new InstallRecoveryStateProbeResult(
                InstallRecoveryStateStatus.AccessError,
                statePath,
                ex);
        }
    }

    internal static IReadOnlyList<string> GetLegacyRollbackArtifactRoots()
        => LegacyInstallRollbackStateProbe.GetDefaultArtifactRoots();

    internal static IReadOnlyDictionary<string, IReadOnlyList<string>>
        GetLegacyRollbackInstallRootCandidates(
            IReadOnlyList<string> physicalInstallRoots,
            IEnumerable<string> rawInstallRoots)
    {
        ArgumentNullException.ThrowIfNull(physicalInstallRoots);
        ArgumentNullException.ThrowIfNull(rawInstallRoots);
        if (physicalInstallRoots.Count == 0)
        {
            throw new ArgumentException(
                "At least one physical install root is required.",
                nameof(physicalInstallRoots));
        }

        var candidatesByPhysicalRoot =
            physicalInstallRoots.ToDictionary(
                static root => NormalizeInstallRoot(root),
                static root =>
                    new HashSet<string>(
                        [NormalizeLegacyInstallRoot(root)],
                        StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
        foreach (var rawInstallRoot in rawInstallRoots)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rawInstallRoot);
            var legacyInstallRoot =
                NormalizeLegacyInstallRoot(rawInstallRoot);
            var physicalInstallRoot =
                NormalizeInstallRoot(legacyInstallRoot);
            if (candidatesByPhysicalRoot.TryGetValue(
                    physicalInstallRoot,
                    out var candidates))
            {
                candidates.Add(legacyInstallRoot);
            }
        }

        return candidatesByPhysicalRoot.ToDictionary(
            static pair => pair.Key,
            static pair =>
                (IReadOnlyList<string>)pair.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeLegacyInstallRoot(string installRoot)
        => Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(installRoot));

    internal static bool CanRecoverLegacyInstallRollbackState(
        string installRoot)
    {
        var physicalInstallRoot = NormalizeInstallRoot(installRoot);
        var protectedRoots = new[]
            {
                Environment.GetFolderPath(
                    Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(
                    Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(
                    Environment.SpecialFolder.Windows)
            }
            .Where(static root => !string.IsNullOrWhiteSpace(root))
            .Select(NormalizeInstallRoot);
        return !protectedRoots.Any(
            protectedRoot =>
                string.Equals(
                    physicalInstallRoot,
                    protectedRoot,
                    StringComparison.OrdinalIgnoreCase) ||
                physicalInstallRoot.StartsWith(
                    protectedRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetInstallRecoveryStartupBlockMessage(
        InstallRecoveryStateProbeResult probe)
    {
        return probe.Status switch
        {
            InstallRecoveryStateStatus.Absent => null,
            InstallRecoveryStateStatus.Present =>
                "중단된 거래플랜 업데이트 복구 상태가 남아 있어 앱 시작을 차단했습니다." +
                Environment.NewLine +
                "원본 데이터에는 손대지 않았습니다." +
                Environment.NewLine +
                "검증된 업데이트 패키지로 업데이트를 다시 실행해 주세요.",
            _ =>
                "중단된 거래플랜 업데이트 복구 상태를 안전하게 확인할 수 없어 앱 시작을 차단했습니다." +
                Environment.NewLine +
                "원본 데이터에는 손대지 않았습니다." +
                Environment.NewLine +
                "업데이트를 다시 실행해 복구를 완료해 주세요."
        };
    }



    protected override async void OnStartup(StartupEventArgs e)

    {
        var testAutoLogin =
            IsolatedTestAutoLogin.TakeFromCurrentProcess();
        if (testAutoLogin.Requested &&
            testAutoLogin.Request is null)
        {
            ShutdownMode =
                ShutdownMode.OnExplicitShutdown;
            AppLogger.Error(
                "AUTH",
                "Rejected isolated test auto-login request. " +
                $"reason={testAutoLogin.FailureReason}");
            Shutdown(1);
            return;
        }

        var testSoftwareRenderingEnabled =
            DesktopRenderModePolicy.ApplyForCurrentRuntime();

        base.OnStartup(e);

        if (testSoftwareRenderingEnabled)
        {
            AppLogger.Info(
                "APP",
                "Isolated test runtime software rendering enabled.");
        }



        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var startupInstallRoots =
            GetInstallRecoveryStartupRoots(AppContext.BaseDirectory);
        if (!InstallRootUpdateGate.TryAcquire(AppContext.BaseDirectory, out var installRootUpdateGate, startupInstallRoots))
        {
            MessageBox.Show(
                "거래플랜 업데이트가 진행 중입니다. 업데이트가 끝난 뒤 다시 실행해 주세요.",
                "거래플랜",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        _installRootUpdateGate = installRootUpdateGate;

        var installRecoveryBlockMessage =
            GetInstallRecoveryStartupBlockMessage(AppContext.BaseDirectory);
        if (!string.IsNullOrWhiteSpace(installRecoveryBlockMessage))
        {
            _installRootUpdateGate?.Dispose();
            _installRootUpdateGate = null;
            MessageBox.Show(
                installRecoveryBlockMessage,
                "거래플랜 업데이트 복구 필요",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }

        var singleInstanceAcquired =
            SingleInstanceGuard.TryAcquireForCurrentAppRoot(
                out var singleInstanceGuard,
                out var appRootIdentity);
        AppLogger.Info(
            "APP",
            $"Single-instance acquisition {(singleInstanceAcquired ? "succeeded" : "rejected")}. appRootIdentity={appRootIdentity}");
        if (!singleInstanceAcquired)
        {
            _installRootUpdateGate?.Dispose();
            _installRootUpdateGate = null;
            MessageBox.Show(
                "거래플랜이 이미 실행 중입니다.",
                "거래플랜",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        _singleInstanceGuard = singleInstanceGuard;

        DispatcherUnhandledException += HandleDispatcherUnhandledException;

        AppDomain.CurrentDomain.UnhandledException += HandleAppDomainUnhandledException;

        TaskScheduler.UnobservedTaskException += HandleTaskSchedulerUnhandledException;

        DataGridAutoColumnWidthService.RegisterGlobal();
        DataGridCheckBoxSingleClickService.RegisterGlobal();
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
            if (DesktopAppUpdateService.TryRelaunchCanonicalInstallIfNeeded(
                    out var relaunchMessage,
                    ReleaseRuntimeGatesForHandoff))

            {

                if (!string.IsNullOrWhiteSpace(relaunchMessage))

                    AppLogger.Info("UPDATE", relaunchMessage);



                Shutdown();

                return;

            }
#endif



            var config = new ConfigurationBuilder()

                .SetBasePath(AppContext.BaseDirectory)

                .AddJsonFile("appsettings.json", optional: false)

                .Build();



            var apiOptions = config.GetSection("Api").Get<ApiOptions>() ?? new ApiOptions();



            var services = new ServiceCollection();



            services.AddDbContext<LocalDbContext>();



            services.AddSingleton<DesktopClientIdentityProvider>();
            services.AddSingleton<DesktopCompatibilityEvidenceStore>();
            services.AddSingleton<DesktopCompatibilityLatch>();
            services.AddSingleton<IDesktopCompatibilityRuntime>(
                serviceProvider =>
                    serviceProvider.GetRequiredService<
                        DesktopCompatibilityLatch>());
            services.AddSingleton<DesktopUpgradeRequiredSignal>();
            services.AddSingleton<DesktopUpgradeRequiredObserver>();
            services.AddSingleton<IDesktopUpgradeRequiredObserver>(
                serviceProvider =>
                    serviceProvider.GetRequiredService<
                        DesktopUpgradeRequiredObserver>());
            services.AddTransient<DesktopUpgradeRequiredHandler>();
            services.AddTransient<DesktopCompatibilityGateService>();
            services.AddTransient<DesktopAppUpdateService>();

            services.AddHttpClient<ErpApiClient>(client =>

            {

                client.BaseAddress = new Uri(apiOptions.BaseUrl.TrimEnd('/') + '/');

                client.Timeout = TimeSpan.FromSeconds(30);

            })
                .AddHttpMessageHandler<DesktopUpgradeRequiredHandler>();

            services.AddHttpClient(
                    SyncService.OfficeSessionHttpClientName,
                    client =>
                    {
                        client.BaseAddress =
                            new Uri(apiOptions.BaseUrl.TrimEnd('/') + '/');
                        client.Timeout = TimeSpan.FromSeconds(100);
                    })
                .AddHttpMessageHandler<DesktopUpgradeRequiredHandler>();



            services.AddSingleton<SessionState>();

            services.AddSingleton<OfficeAccessService>();

            services.AddSingleton<SyncRequestDispatcher>();

            services.AddSingleton<DesktopDataChangeNotifier>();

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

            _services
                .GetRequiredService<DesktopUpgradeRequiredSignal>()
                .UpgradeRequired +=
                    HandleDesktopUpgradeRequiredSignal;

            if (!await EnsureDesktopCompatibilityBeforeLoginAsync())
            {
                Shutdown();
                return;
            }

            ApplyPendingRestoreAfterCompatibilityGate();


            await RunPreLoginInitializationAsync();



            bool? loggedIn;

            using (var loginScope = _services.CreateScope())

            {

                var loginVm = loginScope.ServiceProvider.GetRequiredService<LoginViewModel>();

                await OperationTiming.MeasureAsync(

                    "AUTH",

                    "로그인 뷰모델 초기화",

                    () => loginVm.InitializeAsync(),

                    warningThreshold: TimeSpan.FromSeconds(2));

                if (testAutoLogin.Requested)
                {
                    // Explicit test auto-login is an unattended contract:
                    // fail closed instead of waiting on a manual login dialog.
                    AppLogger.Info(
                        "AUTH",
                        "격리 테스트 프로세스 자동 로그인을 시작합니다.");
                    loggedIn =
                        await loginVm
                            .TryIsolatedTestAutoLoginAsync(
                                testAutoLogin.Request!);
                    if (loggedIn != true)
                    {
                        throw new InvalidOperationException(
                            "격리 테스트 자동 로그인에 실패했습니다. " +
                            "서버 준비 상태와 테스트 계정 구성을 확인하세요.");
                    }

                    AppLogger.Info(
                        "AUTH",
                        "격리 테스트 프로세스 자동 로그인이 완료되었습니다.");
                }
                else
                {
                    var loginWin = new LoginWindow(loginVm);
                    loggedIn =
                        ShowLoginDialogWithFirstRenderTiming(
                            loginWin);
                }

            }



            if (loggedIn != true)

            {
                var loginExitReason = loggedIn is false
                    ? "dialog-result-false"
                    : "dialog-result-null";
                AppLogger.Info(
                    "AUTH",
                    $"Login dialog closed before authentication. reason={loginExitReason}");

                Shutdown();

                return;

            }



            await OperationTiming.MeasureAsync(

                "APP",

                "지연 레거시 마이그레이션",

                () => TryRunDeferredLegacyMigrationAsync(),

                warningThreshold: TimeSpan.FromSeconds(4));



            var mainScope = _services.CreateScope();

            _mainScopeLifetimeCts?.Dispose();
            _mainScopeLifetimeCts = new CancellationTokenSource();
            _postLoginCompletionTask = Task.CompletedTask;
            _postLoginDrainCompleted = false;
            _postLoginWorkNeedsResumeAfterCanceledShutdown = false;
            _postLoginDrainCloseQueued = false;

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

            _activeMainScopeServiceProvider = sp;
            _activeMainViewModel = mainVm;
            _coordinatedMainWindowCloseReady = false;



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

                    _activeMainScopeServiceProvider = null;
                    _activeMainViewModel = null;
                    _coordinatedMainWindowCloseReady = false;

                    _mainScopeLifetimeCts?.Cancel();

                    var mainScopeBackgroundWorkCompleted =
                        _postLoginCompletionTask.IsCompleted &&
                        mainWin.IsShutdownBackgroundWorkCompleted &&
                        mainWin.IsMainScopeSyncDrainCompleted;

                    if (!mainScopeBackgroundWorkCompleted)

                    {

                        AppLogger.Warn(

                            "APP",

                            "메인 창이 닫혔지만 메인 스코프 백그라운드 작업이 아직 종료되지 않아 메인 스코프 해제를 프로세스 종료 시점으로 미룹니다.");

                    }

                    if (!mainScopeDisposed && mainScopeBackgroundWorkCompleted)

                    {

                        mainScope.Dispose();

                        mainScopeDisposed = true;

                    }

                    if (mainScopeBackgroundWorkCompleted)

                    {

                        _mainScopeLifetimeCts?.Dispose();

                        _mainScopeLifetimeCts = null;

                    }



                    if (mainScopeBackgroundWorkCompleted)
                    {
                        RestartToLoginIfRequested();
                    }
                    else if (_restartToLoginRequested)
                    {
                        _restartToLoginRequested = false;
                        AppLogger.Warn(
                            "AUTH",
                            "Main-scope sync drain was not confirmed, so login process restart was skipped.");
                    }

                    Shutdown(_requestedShutdownExitCode);

                }

            };



            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            mainWin.Show();



            var session = sp.GetRequiredService<SessionState>();

            // 첫 화면은 즉시 조작 가능해야 하므로 로그인 후 동기화는 팝업/창 비활성화 없이 백그라운드에서 시작한다.

            var showStartupSyncPopupImmediately = false;

            Window? startupSyncPopup = null;



            var mainScopeLifetimeToken =

                (_mainScopeLifetimeCts ??

                 throw new InvalidOperationException("메인 작업 수명 토큰이 준비되지 않았습니다."))

                .Token;



            var initSucceeded = await OperationTiming.MeasureAsync(

                "UI",

                "메인 윈도우 초기화",

                () => TryInitializeMainWindowAsync(

                    mainWin,

                    mainVm,

                    deferStartupNotifications: showStartupSyncPopupImmediately,

                    mainScopeLifetimeToken: mainScopeLifetimeToken),

                warningThreshold: TimeSpan.FromSeconds(4));

            if (initSucceeded && !_updateShutdownRequested && !_shutdownInProgress && !mainScopeDisposed && mainWin.IsLoaded)

            {

                var popupForPostLoginSync = startupSyncPopup;

                startupSyncPopup = null;

                _postLoginCompletionTask =
                    RunPostLoginSyncThenStartupNotificationsAsync(

                        mainWin,

                        mainVm,

                        sp,

                        session,

                        popupForPostLoginSync,

                        showDeferredStartupNotifications: showStartupSyncPopupImmediately,

                        initialDashboardLoadTask: mainWin.InitialDashboardLoadTask,

                        mainScopeLifetimeToken: mainScopeLifetimeToken);

                UiTaskHelper.Forget(

                    _postLoginCompletionTask,

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

                if (_updateShutdownRequested &&
                    TryQueueActiveMainWindowShutdown())
                    return;

                if (_shutdownInProgress &&
                    MainWindow is global::거래플랜.Desktop.App.MainWindow &&
                    _activeMainScopeServiceProvider is not null &&
                    _activeMainViewModel is not null)
                {
                    // A normal-close or runtime-compatibility coordinator already owns
                    // the active main scope. Explicit Shutdown would bypass its drain.
                    return;
                }

                Shutdown(0);

                return;

            }



            AppLogger.Error("APP", "Startup failure", ex);

            await TryRecordStartupDiagnosticAsync(ex);

            if (TryQueueActiveMainWindowShutdown(
                    fatalStartup: true,
                    waitForCompletionWithoutDeadline: true))
            {
                return;
            }

            if (!testAutoLogin.Requested)
            {
                MessageBox.Show(

                    $"시작 오류:\n{ex.Message}\n\n{ex.InnerException?.Message}",

                    "거래플랜 오류",

                    MessageBoxButton.OK,

                    MessageBoxImage.Error);
            }

            Shutdown(1);

        }

    }



    private static bool? ShowLoginDialogWithFirstRenderTiming(LoginWindow loginWindow)
    {
        ArgumentNullException.ThrowIfNull(loginWindow);

        var firstRenderStopwatch = Stopwatch.StartNew();
        EventHandler? contentRenderedHandler = null;
        contentRenderedHandler = (_, _) =>
        {
            loginWindow.ContentRendered -= contentRenderedHandler;
            firstRenderStopwatch.Stop();
            OperationTiming.LogIfSlow(
                "AUTH",
                "로그인 창 최초 표시",
                firstRenderStopwatch.Elapsed,
                warningThreshold: TimeSpan.FromSeconds(10));
        };
        loginWindow.ContentRendered += contentRenderedHandler;

        try
        {
            return DialogWindowCloseHelper.ShowDialog(loginWindow);
        }
        finally
        {
            loginWindow.ContentRendered -= contentRenderedHandler;
            if (firstRenderStopwatch.IsRunning)
                firstRenderStopwatch.Stop();
        }
    }



    private async Task<bool> EnsureDesktopCompatibilityBeforeLoginAsync()
    {
        if (_services is null)
        {
            throw new InvalidOperationException(
                "서비스 초기화가 완료되지 않았습니다.");
        }

        using var scope = _services.CreateScope();
        var decision = await scope.ServiceProvider
            .GetRequiredService<DesktopCompatibilityGateService>()
            .CheckAsync(CancellationToken.None);
        if (decision.CanStart)
            return true;

        AppLogger.Warn(
            "UPDATE",
            $"Desktop compatibility startup gate blocked login. diagnostic={decision.DiagnosticCode}");
        var recoveryWindow = new DesktopUpdateRequiredWindow(
            scope.ServiceProvider
                .GetRequiredService<DesktopCompatibilityGateService>(),
            scope.ServiceProvider
                .GetRequiredService<DesktopAppUpdateService>(),
            decision);
        return DialogWindowCloseHelper.ShowDialog(recoveryWindow) == true;
    }

    private static void ApplyPendingRestoreAfterCompatibilityGate()
    {
        var restoreNotice =
            BackupService.TryApplyPendingRestoreOnStartup();
        if (string.IsNullOrWhiteSpace(restoreNotice))
            return;

        AppLogger.Info("BACKUP", restoreNotice);
        var image =
            restoreNotice.Contains(
                "오류",
                StringComparison.OrdinalIgnoreCase) ||
            restoreNotice.Contains(
                "건너",
                StringComparison.OrdinalIgnoreCase)
                ? MessageBoxImage.Warning
                : MessageBoxImage.Information;
        MessageBox.Show(
            restoreNotice,
            "백업 복원",
            MessageBoxButton.OK,
            image);
    }

    private void HandleDesktopUpgradeRequiredSignal(
        DesktopCompatibilityLatchSnapshot snapshot)
    {
        if (!snapshot.IsBlocked ||
            Interlocked.Exchange(
                ref _compatibilityWindowOpen,
                1) != 0)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            new Action(
                () => UiTaskHelper.Forget(
                    () => ShowRuntimeCompatibilityRecoveryAsync(
                        snapshot),
                    "UPDATE",
                    "런타임 호환성 차단 복구 창",
                    ex => AppLogger.Error(
                        "UPDATE",
                        "Runtime compatibility recovery coordinator failed without a proven safe exit.",
                        ex),
                    trackForWindowLifetime: false)),
            DispatcherPriority.Send);
    }

    private async Task ShowRuntimeCompatibilityRecoveryAsync(
        DesktopCompatibilityLatchSnapshot snapshot)
    {
        var safeToExit = false;

        try
        {
            _shutdownInProgress = true;
            _runtimeCompatibilityShutdownRequested = true;
            _requestedShutdownExitCode = 1;
            _autoSaveTimer?.Stop();
            CancelMainScopeBackgroundWork();

            if (MainWindow is MainWindow appWindow)
            {
                appWindow.BeginShutdownProtection(
                    waitForCompletionWithoutDeadline: true);
                appWindow.IsEnabled = false;
                await PrepareActiveMainScopeForCompatibilityExitAsync(appWindow);
                safeToExit = true;
            }
            else
            {
                safeToExit = true;
            }

            foreach (Window window in Windows)
            {
                if (window is not DesktopUpdateRequiredWindow)
                    window.IsEnabled = false;
            }

            if (_services is null)
                return;

            using var scope = _services.CreateScope();
            var decision = new DesktopCompatibilityGateDecision(
                true,
                snapshot.DiagnosticCode,
                snapshot.Evidence,
                null);
            var recoveryWindow =
                new DesktopUpdateRequiredWindow(
                    scope.ServiceProvider.GetRequiredService<
                        DesktopCompatibilityGateService>(),
                    scope.ServiceProvider.GetRequiredService<
                        DesktopAppUpdateService>(),
                    decision);
            DialogWindowCloseHelper.ShowDialog(
                recoveryWindow,
                allowDuringShutdown: true);
        }
        catch (Exception ex)
        {
            AppLogger.Error(
                "UPDATE",
                "Runtime compatibility recovery could not prove that active work was drained. The blocked process will remain open instead of forcing an unsafe exit.",
                ex);
            if (!safeToExit)
            {
                MessageBox.Show(
                    "필수 PC 업데이트가 감지되었지만 실행 중인 작업의 안전 종료를 확인하지 못했습니다.\n\n서버 변경은 계속 차단됩니다. 열려 있는 파일 선택창이나 작업 창을 닫은 뒤 거래플랜을 다시 종료해 주세요.",
                    "거래플랜 안전 종료 대기",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        finally
        {
            // A safely drained runtime 426 ends the current process. If drain
            // cannot be proven, mutation remains blocked and forced exit is avoided.
            if (safeToExit)
            {
                _coordinatedMainWindowCloseReady = true;
                Shutdown(1);
            }
        }
    }

    private async Task PrepareActiveMainScopeForCompatibilityExitAsync(
        MainWindow appWindow)
    {
        await _mainWindowShutdownCoordinatorLock.WaitAsync();
        try
        {
            if (_activeMainScopeServiceProvider is null ||
                _activeMainViewModel is null)
            {
                return;
            }

            await DrainPostLoginWorkAsync();
            await appWindow.DrainPendingBackgroundWorkForShutdownAsync();
            await DrainPeriodicSaveCycleAsync(
                waitForCompletionWithoutDeadline: true);

            try
            {
                var result = await RunSaveCycleAsync(
                    _activeMainScopeServiceProvider,
                    _activeMainViewModel,
                    isShutdown: true);
                if (result.RemainingDirtyCount > 0)
                {
                    AppLogger.Warn(
                        "UPDATE",
                        $"Runtime compatibility exit preserved {result.RemainingDirtyCount:N0} pending item(s) locally because server mutation is blocked until the desktop is upgraded.");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error(
                    "UPDATE",
                    "Runtime compatibility exit final save/backup failed; the process must still exit because server mutation is blocked.",
                    ex);
            }

            await StopMainScopeSyncServiceForShutdownAsync(appWindow);
        }
        finally
        {
            _mainWindowShutdownCoordinatorLock.Release();
        }
    }



    private async Task RunPreLoginInitializationAsync()
    {
        if (_services is null)
            throw new InvalidOperationException("서비스 초기화가 완료되지 않았습니다.");

        var loadingWindow = new StartupLoadingWindow();
        loadingWindow.Show();

        // 창을 먼저 그린 뒤 SQLite 정비를 전용 스레드에서 실행한다.
        // Microsoft.Data.Sqlite의 일부 async 호출은 내부적으로 동기 실행되므로
        // UI 스레드에서 직접 기다리면 Windows가 앱을 '응답 없음'으로 표시할 수 있다.
        await loadingWindow.Dispatcher.InvokeAsync(
            static () => { },
            DispatcherPriority.ApplicationIdle);

        try
        {
            await OperationTiming.MeasureAsync(
                "APP",
                "로컬 DB 초기화 및 버전 정비",
                () => Task.Run(async () =>
                {
                     await using var scope = _services.CreateAsyncScope();
                     var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();
                     await LocalDbInitializer.InitializeAsync(db);
                     await AttachmentFileJournal.RecoverIncompleteJournalsAsync(
                         db,
                         AppPaths.AttachmentFileJournalDir,
                         AppPaths.AttachmentsDir);
                     await RunVersionChangeMaintenanceAsync(scope.ServiceProvider);
                 }),
                warningThreshold: TimeSpan.FromSeconds(4));
        }
        finally
        {
            loadingWindow.Complete();
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



            return false;

        }

    }



    private static async Task RunStartupIntegrityCheckAsync(

        IServiceProvider serviceProvider,

        bool showUserAlert = true,

        Action<string>? updateStatus = null,

        CancellationToken ct = default)

    {

        var diagnostics = serviceProvider.GetService<SyncDiagnosticsService>();



        try

        {

            var startupIntegrity = serviceProvider.GetRequiredService<StartupIntegrityService>();

            var result = await startupIntegrity.RunAsync(ct);

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

                    recoverySucceeded: result.RefreshSucceeded,

                    ct: ct);

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

        catch (OperationCanceledException)

            when (ct.IsCancellationRequested)

        {

            throw;

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

                    severity: "Warning",

                    ct: ct);

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

        if (_services is not null)
        {
            var upgradeRequiredSignal =
                _services.GetService<DesktopUpgradeRequiredSignal>();
            if (upgradeRequiredSignal is not null)
            {
                upgradeRequiredSignal.UpgradeRequired -=
                    HandleDesktopUpgradeRequiredSignal;
            }
        }

        _services?.Dispose();

        _installRootUpdateGate?.Dispose();
        _installRootUpdateGate = null;

        _singleInstanceGuard?.Dispose();
        _singleInstanceGuard = null;

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



        _autoSaveTimer.Tick += (_, _) => HandleAutoSaveTimerTick(sp, mainVm);



        _autoSaveTimer.Start();

    }

    private void HandleAutoSaveTimerTick(IServiceProvider sp, MainViewModel mainVm)
    {
        if (_shutdownInProgress || _updateShutdownRequested || _restartToLoginRequested)
            return;

        UiTaskHelper.Forget(
            () => RunPeriodicSaveCycleAsync(sp, mainVm),
            "APP",
            "주기 저장",
            ex => AppLogger.Error("APP", "Periodic save cycle failure", ex));
    }



    private async Task RunPeriodicSaveCycleAsync(IServiceProvider sp, MainViewModel mainVm)

        => await RunSaveCycleAsync(sp, mainVm, isShutdown: false);



    private void HandleMainWindowClosing(MainWindow mainWin, IServiceProvider sp, MainViewModel mainVm, CancelEventArgs args)

    {

        if (_updateShutdownRequested ||
            _restartToLoginRequested ||
            _fatalStartupShutdownRequested ||
            _runtimeCompatibilityShutdownRequested)

        {
            if (_coordinatedMainWindowCloseReady)
                return;

            args.Cancel = true;

            _autoSaveTimer?.Stop();

            mainWin.BeginShutdownProtection(
                waitForCompletionWithoutDeadline:
                    _fatalStartupShutdownRequested ||
                    _runtimeCompatibilityShutdownRequested);

            _shutdownInProgress = true;

            CancelMainScopeBackgroundWork();

            QueueCloseAfterPostLoginDrain(mainWin, sp, mainVm);

            return;

        }



        if (_shutdownInProgress)
        {
            if (!_coordinatedMainWindowCloseReady)
                args.Cancel = true;

            return;
        }



        args.Cancel = true;

        _shutdownInProgress = true;

        _autoSaveTimer?.Stop();

        MarkPostLoginWorkInterruptedIfIncomplete();
        mainWin.BeginShutdownProtection();

        CancelMainScopeBackgroundWork();



        UiTaskHelper.Forget(
            () => HandleMainWindowClosingAsync(mainWin, sp, mainVm),

            "APP",

            "앱 종료 처리",

            ex => AppLogger.Error("APP", "Shutdown sync/backup failure", ex),
            trackForWindowLifetime: false);

    }



    private void CancelMainScopeBackgroundWork()

    {

        MarkPostLoginWorkInterruptedIfIncomplete();

        try

        {

            _mainScopeLifetimeCts?.Cancel();

        }

        catch (ObjectDisposedException)

        {

            // 이미 완료된 메인 스코프 수명 토큰은 다시 취소할 필요가 없습니다.

        }

        catch (Exception ex)

        {

            // CancellationTokenSource.Cancel invokes user callbacks synchronously.
            // Run every callback, record the failure, and let the shutdown drains
            // prove safety instead of allowing a callback exception to bypass them.
            AppLogger.Error(
                "APP",
                "Main-scope background cancellation callback failure",
                ex);

        }

    }

    private void MarkPostLoginWorkInterruptedIfIncomplete()
    {
        if (!_postLoginCompletionTask.IsCompleted)
            _postLoginWorkNeedsResumeAfterCanceledShutdown = true;
    }

    private void ResumePostLoginWorkAfterCanceledShutdown(
        MainWindow mainWin,
        IServiceProvider sp,
        MainViewModel mainVm)
    {
        if (!_postLoginWorkNeedsResumeAfterCanceledShutdown ||
            !mainWin.IsLoaded)
        {
            return;
        }

        _mainScopeLifetimeCts?.Dispose();
        _mainScopeLifetimeCts = new CancellationTokenSource();
        var mainScopeLifetimeToken = _mainScopeLifetimeCts.Token;

        _postLoginWorkNeedsResumeAfterCanceledShutdown = false;
        _postLoginDrainCompleted = false;
        _postLoginCompletionTask =
            RunPostLoginSyncThenStartupNotificationsAsync(
                mainWin,
                mainVm,
                sp,
                sp.GetRequiredService<SessionState>(),
                startupSyncPopup: null,
                showDeferredStartupNotifications: false,
                initialDashboardLoadTask: null,
                mainScopeLifetimeToken: mainScopeLifetimeToken);

        UiTaskHelper.Forget(
            _postLoginCompletionTask,
            "APP",
            "종료 취소 후 로그인 후속 작업 재개",
            ex => AppLogger.Error(
                "APP",
                "Post-login work restart after canceled shutdown failed",
                ex));
    }

    private bool TryRecoverActiveMainWindowAfterCanceledShutdown()
    {
        if (MainWindow is not MainWindow mainWin ||
            _activeMainScopeServiceProvider is null ||
            _activeMainViewModel is null)
        {
            return false;
        }

        var sp = _activeMainScopeServiceProvider;
        var mainVm = _activeMainViewModel;
        _coordinatedMainWindowCloseReady = false;
        _shutdownInProgress = false;
        _updateShutdownRequested = false;
        _restartToLoginRequested = false;
        _requestedShutdownExitCode = 0;

        try
        {
            mainWin.EndShutdownProtection();
            mainWin.IsEnabled = true;
            ResumePostLoginWorkAfterCanceledShutdown(mainWin, sp, mainVm);
            StartAutoSaveTimer(sp, mainVm);
        }
        catch (Exception recoveryException)
        {
            // The active main scope still exists. Remain fail-closed rather than
            // bypassing its final save and background drains with Shutdown().
            AppLogger.Error(
                "UPDATE",
                "Canceled update shutdown could not restore the active main window safely.",
                recoveryException);
        }

        return true;
    }



    private void QueueCloseAfterPostLoginDrain(
        MainWindow mainWin,
        IServiceProvider sp,
        MainViewModel mainVm)

    {

        if (_postLoginDrainCloseQueued)

            return;

        _postLoginDrainCloseQueued = true;

        UiTaskHelper.Forget(
            () => CloseAfterPostLoginDrainAsync(mainWin, sp, mainVm),

            "APP",

            "로그인 후 백그라운드 작업 종료 대기",

            ex => AppLogger.Error(

                "APP",

                "Post-login background drain failure",

                ex),
            trackForWindowLifetime: false);

    }

    private bool TryQueueActiveMainWindowShutdown(
        bool fatalStartup = false,
        bool waitForCompletionWithoutDeadline = false)
    {
        if (MainWindow is not MainWindow mainWin ||
            _activeMainScopeServiceProvider is null ||
            _activeMainViewModel is null)
        {
            return false;
        }

        if (fatalStartup)
        {
            _fatalStartupShutdownRequested = true;
            _requestedShutdownExitCode = 1;
        }

        _shutdownInProgress = true;
        _autoSaveTimer?.Stop();
        CancelMainScopeBackgroundWork();
        mainWin.BeginShutdownProtection(
            waitForCompletionWithoutDeadline);
        QueueCloseAfterPostLoginDrain(
            mainWin,
            _activeMainScopeServiceProvider,
            _activeMainViewModel);
        return true;
    }



    private async Task CloseAfterPostLoginDrainAsync(
        MainWindow mainWin,
        IServiceProvider sp,
        MainViewModel mainVm)

    {
        await _mainWindowShutdownCoordinatorLock.WaitAsync();
        var mainSyncStopAttempted = false;
        try
        {
            if (!ReferenceEquals(_activeMainScopeServiceProvider, sp) ||
                !ReferenceEquals(_activeMainViewModel, mainVm))
            {
                return;
            }

            await DrainPostLoginWorkAsync();
            await mainWin.DrainPendingBackgroundWorkForShutdownAsync();
            await DrainPeriodicSaveCycleAsync(
                waitForCompletionWithoutDeadline:
                    _fatalStartupShutdownRequested ||
                    _runtimeCompatibilityShutdownRequested);

            var result = await RunSaveCycleAsync(sp, mainVm, isShutdown: true);
            if (result.RemainingDirtyCount > 0 &&
                !_fatalStartupShutdownRequested &&
                !_runtimeCompatibilityShutdownRequested)
            {
                throw new InvalidOperationException(
                    $"Final shutdown synchronization left {result.RemainingDirtyCount:N0} pending item(s). Update/login restart was canceled.");
            }

            if (result.RemainingDirtyCount > 0)
            {
                AppLogger.Warn(
                    "APP",
                    $"Mandatory safe shutdown preserved {result.RemainingDirtyCount:N0} pending item(s) locally for the next run.");
            }

            mainSyncStopAttempted = true;
            await StopMainScopeSyncServiceForShutdownAsync(mainWin);

            _coordinatedMainWindowCloseReady = true;

            if (mainWin.IsLoaded)
                mainWin.Close();
            else
                Shutdown(_requestedShutdownExitCode);
        }
        catch
        {
            if (_fatalStartupShutdownRequested ||
                _runtimeCompatibilityShutdownRequested ||
                mainSyncStopAttempted)
            {
                throw;
            }

            _coordinatedMainWindowCloseReady = false;
            _shutdownInProgress = false;
            _updateShutdownRequested = false;
            _restartToLoginRequested = false;
            _requestedShutdownExitCode = 0;
            mainWin.EndShutdownProtection();
            mainWin.IsEnabled = true;
            ResumePostLoginWorkAfterCanceledShutdown(mainWin, sp, mainVm);
            StartAutoSaveTimer(sp, mainVm);
            if (DialogWindowCloseHelper.ActiveNativeDialogCount > 0)
            {
                mainVm.SyncStatus =
                    "파일 선택/저장 창을 닫은 뒤 업데이트 또는 로그아웃을 다시 시도해 주세요.";
            }
            throw;
        }
        finally
        {
            _postLoginDrainCloseQueued = false;
            _mainWindowShutdownCoordinatorLock.Release();
        }

    }



    private async Task DrainPostLoginWorkAsync()

    {

        if (_postLoginDrainCompleted)

            return;

        try

        {

            await _postLoginCompletionTask;

        }

        catch (OperationCanceledException)

            when (_mainScopeLifetimeCts?.IsCancellationRequested == true)

        {

            AppLogger.Info(

                "APP",

                "메인 창 종료에 맞춰 로그인 후 백그라운드 작업을 취소했습니다.");

        }

        catch (Exception ex)

        {

            AppLogger.Warn(

                "APP",

                $"로그인 후 백그라운드 작업 종료 확인 중 오류를 기록하고 종료 절차를 계속합니다: {ex.Message}");

        }

        finally

        {

            _postLoginDrainCompleted = true;

        }

    }

    private async Task DrainPeriodicSaveCycleAsync(
        bool waitForCompletionWithoutDeadline = false)
    {
        if (waitForCompletionWithoutDeadline)
        {
            await _saveCycleLock.WaitAsync();
            _saveCycleLock.Release();
            return;
        }

        if (!await _saveCycleLock.WaitAsync(TimeSpan.FromMinutes(2)))
        {
            throw new TimeoutException(
                "The active save cycle did not finish before the shutdown deadline.");
        }

        _saveCycleLock.Release();
    }

    private static async Task StopMainScopeSyncServiceForShutdownAsync(MainWindow mainWin)
    {
        await mainWin.StopAndDrainMainScopeSyncServiceAsync();
    }



    private async Task HandleMainWindowClosingAsync(MainWindow mainWin, IServiceProvider sp, MainViewModel mainVm)

    {
        await _mainWindowShutdownCoordinatorLock.WaitAsync();
        Window? savingPopup = null;
        var shouldClose = false;
        var mainSyncStopAttempted = false;
        try
        {
            if (!ReferenceEquals(_activeMainScopeServiceProvider, sp) ||
                !ReferenceEquals(_activeMainViewModel, mainVm))
            {
                return;
            }

            await DrainPostLoginWorkAsync();
            await mainWin.DrainPendingBackgroundWorkForShutdownAsync();

            mainVm.SyncStatus = "종료 전 서버와 동기화하고 데이터를 저장합니다.";
            savingPopup = ShowShutdownSavingPopup(mainWin);
            mainWin.IsEnabled = false;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);

            var result = await RunSaveCycleAsync(sp, mainVm, isShutdown: true);

            if (result.RemainingDirtyCount > 0 &&
                (_updateShutdownRequested || _restartToLoginRequested) &&
                !_fatalStartupShutdownRequested &&
                !_runtimeCompatibilityShutdownRequested)
            {
                throw new InvalidOperationException(
                    $"Final shutdown synchronization left {result.RemainingDirtyCount:N0} pending item(s). Update/login restart was canceled.");
            }

            mainSyncStopAttempted = true;
            await StopMainScopeSyncServiceForShutdownAsync(mainWin);

            if (!result.SyncSucceeded && result.RemainingDirtyCount > 0)
            {
                AppLogger.Warn("APP", $"Shutdown continues with {result.RemainingDirtyCount:N0} pending sync item(s). They remain saved locally and will sync on the next run.");
                mainVm.SyncStatus = $"로컬 저장 완료. 미동기화 {result.RemainingDirtyCount:N0}건은 다음 실행 시 다시 동기화됩니다.";
            }

            _coordinatedMainWindowCloseReady = true;
            shouldClose = true;
        }
        catch (Exception ex)
        {
            shouldClose = false;

            if (_fatalStartupShutdownRequested ||
                _runtimeCompatibilityShutdownRequested ||
                mainSyncStopAttempted)
            {
                throw;
            }

            _coordinatedMainWindowCloseReady = false;
            _shutdownInProgress = false;
            _updateShutdownRequested = false;
            _restartToLoginRequested = false;
            _requestedShutdownExitCode = 0;
            mainWin.IsEnabled = true;
            mainWin.EndShutdownProtection();
            ResumePostLoginWorkAfterCanceledShutdown(mainWin, sp, mainVm);
            StartAutoSaveTimer(sp, mainVm);
            mainVm.SyncStatus = "종료 전 서버 동기화 확인이 필요합니다.";

            if (DialogWindowCloseHelper.ActiveNativeDialogCount == 0)
            {
                MessageBox.Show(

                    $"종료 전 동기화 확인이 필요합니다.{Environment.NewLine}{ex.Message}",

                    "거래플랜 오류",

                    MessageBoxButton.OK,

                    MessageBoxImage.Error);
            }
            else
            {
                mainVm.SyncStatus =
                    "파일 선택/저장 창을 닫은 뒤 종료를 다시 시도해 주세요.";
            }

            throw;
        }
        finally
        {
            try
            {
                savingPopup?.Close();
            }
            catch
            {
                // ignored
            }
            try
            {
                if (shouldClose)
                    mainWin.Close();
            }
            finally
            {
                _mainWindowShutdownCoordinatorLock.Release();
            }
        }
    }



    private async Task<bool> TryInitializeMainWindowAsync(

        MainWindow mainWin,

        MainViewModel mainVm,

        bool deferStartupNotifications = false,

        CancellationToken mainScopeLifetimeToken = default)

    {

        try

        {

            await mainWin.InitAsync(

                deferStartupNotifications,

                mainScopeLifetimeToken);

            mainWin.QueueDesktopUiSmokeSelfTestIfRequested();

            return true;

        }

        catch (OperationCanceledException) when (mainScopeLifetimeToken.IsCancellationRequested)

        {

            throw;

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

        Task? initialDashboardLoadTask = null,

        CancellationToken mainScopeLifetimeToken = default)

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

                catch (OperationCanceledException) when (mainScopeLifetimeToken.IsCancellationRequested)

                {

                    throw;

                }

                catch (Exception ex)

                {

                    AppLogger.Warn("APP", $"초기 대시보드 로드 완료 대기 중 오류: {ex.Message}");

                }

            }



            mainScopeLifetimeToken.ThrowIfCancellationRequested();



            if (!mainWin.IsLoaded)

                return;



            syncTask = await StartPostLoginSyncWithPopupAsync(

                mainWin,

                mainVm,

                session,

                startupSyncPopup,

                mainScopeLifetimeToken);

        }

        finally

        {

            if (mainScopeLifetimeToken.IsCancellationRequested)

            {

                CloseStartupSyncPopup(mainWin, startupSyncPopup);

            }

            else if (showDeferredStartupNotifications && mainWin.IsLoaded)

            {

                await Dispatcher.InvokeAsync(

                    mainWin.ShowDeferredStartupNotifications,

                    DispatcherPriority.Background);

            }

        }



        if (syncTask is not null)

            await CompletePostLoginSyncAndIntegrityAsync(

                mainWin,

                mainVm,

                serviceProvider,

                syncTask,

                integrityPromptReason,

                mainScopeLifetimeToken);

    }



    private async Task<Task?> StartPostLoginSyncWithPopupAsync(

        MainWindow mainWin,

        MainViewModel mainVm,

        SessionState session,

        Window? existingPopup = null,

        CancellationToken mainScopeLifetimeToken = default)

    {

        CloseStartupSyncPopup(mainWin, existingPopup);

        mainScopeLifetimeToken.ThrowIfCancellationRequested();



        if (session.IsOfflineMode)

        {

            mainVm.SyncStatus = "오프라인 모드입니다. 로컬 점검은 백그라운드에서 진행합니다.";

            return mainVm.RunPostLoginSyncAsync(mainScopeLifetimeToken);

        }



        try

        {

            _ = await mainVm.ShouldShowPostLoginSyncPopupAsync(mainScopeLifetimeToken);

        }

        catch (OperationCanceledException) when (mainScopeLifetimeToken.IsCancellationRequested)

        {

            throw;

        }

        catch (Exception ex)

        {

            AppLogger.Warn("APP", $"시작 동기화 필요 여부 판단 실패: {ex.Message}");

        }



        mainVm.SyncStatus = "로그인 완료. 시작 동기화는 백그라운드에서 진행하므로 바로 작업할 수 있습니다.";

        mainScopeLifetimeToken.ThrowIfCancellationRequested();

        return mainVm.RunPostLoginSyncAsync(mainScopeLifetimeToken);

    }



    private static async Task CompletePostLoginSyncAndIntegrityAsync(

        MainWindow mainWin,

        MainViewModel mainVm,

        IServiceProvider serviceProvider,

        Task syncTask,

        string integrityPromptReason,

        CancellationToken mainScopeLifetimeToken)

    {

        await syncTask;

        mainScopeLifetimeToken.ThrowIfCancellationRequested();



        if (!mainWin.IsLoaded)

            return;



        await PrewarmAdministrativeBusinessCachesOnDispatcherAsync(

            mainWin,

            serviceProvider,

            mainScopeLifetimeToken);

        mainScopeLifetimeToken.ThrowIfCancellationRequested();

        if (!mainWin.IsLoaded)

            return;

        var integrityStatus = await RunPostLoginIntegrityChecksInBackgroundAsync(

            serviceProvider,

            integrityPromptReason,

            mainScopeLifetimeToken);

        if (string.IsNullOrWhiteSpace(integrityStatus) ||

            !mainWin.IsLoaded ||

            mainScopeLifetimeToken.IsCancellationRequested)

            return;

        await mainWin.Dispatcher.InvokeAsync(

            () =>

            {

                if (mainWin.IsLoaded &&

                    !mainScopeLifetimeToken.IsCancellationRequested)

                    mainVm.SyncStatus = integrityStatus;

            },

            DispatcherPriority.Background);

    }



    private static async Task PrewarmAdministrativeBusinessCachesOnDispatcherAsync(

        MainWindow mainWin,

        IServiceProvider serviceProvider,

        CancellationToken mainScopeLifetimeToken)

    {

        if (!mainWin.Dispatcher.CheckAccess())

        {

            await mainWin.Dispatcher.InvokeAsync(

                    () => PrewarmAdministrativeBusinessCachesOnDispatcherAsync(

                        mainWin,

                        serviceProvider,

                        mainScopeLifetimeToken),

                    DispatcherPriority.Background)

                .Task

                .Unwrap();

            return;

        }



        mainScopeLifetimeToken.ThrowIfCancellationRequested();

        if (!mainWin.IsLoaded)

            return;

        var mainScopeSyncService =

            serviceProvider.GetRequiredService<SyncService>();

        try

        {

            _ = await OperationTiming.MeasureAsync(

                "SYNC",

                "로그인 후 관리자 렌탈 캐시 사전 예열",

                () => mainScopeSyncService.EnsureAdministrativeBusinessCachesAsync(

                    mainScopeLifetimeToken),

                warningThreshold: TimeSpan.FromSeconds(3));

        }

        catch (OperationCanceledException)

            when (mainScopeLifetimeToken.IsCancellationRequested)

        {

            throw;

        }

        catch (Exception ex)

        {

            AppLogger.Warn(

                "SYNC",

                $"로그인 후 관리자 렌탈 캐시 사전 예열 실패: {ex.Message}");

        }

    }



    private static Task<string?> RunPostLoginIntegrityChecksInBackgroundAsync(
        IServiceProvider serviceProvider,
        string integrityPromptReason,
        CancellationToken mainScopeLifetimeToken)
    {
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        return Task.Run(async () =>
        {
            mainScopeLifetimeToken.ThrowIfCancellationRequested();
            await using var integrityScope = scopeFactory.CreateAsyncScope();
            var scopedProvider = integrityScope.ServiceProvider;
            string? statusMessage = null;

            await RunStartupIntegrityCheckAsync(
                scopedProvider,
                showUserAlert: false,
                updateStatus: message => statusMessage = message,
                ct: mainScopeLifetimeToken);

            try
            {
                mainScopeLifetimeToken.ThrowIfCancellationRequested();
                var session = scopedProvider.GetRequiredService<SessionState>();
                var dataIntegrity = scopedProvider.GetRequiredService<DataIntegrityIssueService>();
                var result = await OperationTiming.MeasureAsync(
                    "INTEGRITY",
                    $"{integrityPromptReason} 운영 점검",
                    () => dataIntegrity.ScanAsync(
                        session,
                        mainScopeLifetimeToken),
                    warningThreshold: TimeSpan.FromSeconds(3));

                if (result.HasIssues && result.HasPassiveStartupNoticeIssues)
                {
                    statusMessage = result.BuildPassiveStartupStatusMessage();
                    AppLogger.Warn(
                        "INTEGRITY",
                        $"{integrityPromptReason} 운영 점검 알림을 상태바로 전환했습니다. " +
                        $"notices={result.PassiveStartupNoticeIssueCount:N0}, total={result.TotalIssueCount:N0}, " +
                        $"errors={result.ErrorIssueCount:N0}, warnings={result.WarningIssueCount:N0}, info={result.InformationalIssueCount:N0}");
                }
            }
            catch (OperationCanceledException)
                when (mainScopeLifetimeToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("INTEGRITY", $"{integrityPromptReason} 운영 점검 실패: {ex.Message}");
            }

            return statusMessage;
        }, mainScopeLifetimeToken);
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

            TextWrapping = TextWrapping.Wrap,

            TextTrimming = TextTrimming.None,

            Margin = new Thickness(0, 0, 0, 6)

        };



        var text = new TextBlock

        {

            Text = message,

            FontFamily = new FontFamily("맑은 고딕"),

            FontSize = 14,

            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2C3E50")),

            TextAlignment = TextAlignment.Center,

            TextWrapping = TextWrapping.Wrap,

            TextTrimming = TextTrimming.None,

            Margin = new Thickness(0, 0, 0, 10)

        };



        var progress = new ProgressBar

        {

            Height = 12,

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

            TextWrapping = TextWrapping.Wrap,

            TextTrimming = TextTrimming.None,

            Margin = new Thickness(0, 8, 0, 0)

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

            Padding = new Thickness(20, 14, 20, 12),

            Width = 420,

            Child = content

        };



        var popupScrollViewer = new ScrollViewer

        {

            Content = root,

            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,

            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,

            MaxWidth = Math.Max(1d, SystemParameters.WorkArea.Width - 16d),

            MaxHeight = Math.Max(1d, SystemParameters.WorkArea.Height - 16d)

        };



        var popup = new Window

        {
            Style = new Style(typeof(Window)),

            Title = title,

            Content = popupScrollViewer,

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

        PreserveContentSizedActivityPopup(popup);
        popup.Loaded += (_, _) => PreserveContentSizedActivityPopup(popup);

        FullTextLayoutBehavior.SetIsEnabled(popup, true);



        popup.Show();

        return popup;

    }

    private static void PreserveContentSizedActivityPopup(Window popup)
    {
        ResponsiveWindowBehavior.SetIsGlobalLayoutExcluded(popup, true);
        ResponsiveWindowBehavior.SetIsEnabled(popup, false);
        popup.MinWidth = 0;
        popup.MinHeight = 0;
        popup.MaxWidth = Math.Max(1d, SystemParameters.WorkArea.Width - 16d);
        popup.MaxHeight = Math.Max(1d, SystemParameters.WorkArea.Height - 16d);
        popup.ClearValue(FrameworkElement.WidthProperty);
        popup.ClearValue(FrameworkElement.HeightProperty);
        popup.SizeToContent = SizeToContent.WidthAndHeight;
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

            ReleaseRuntimeGatesForHandoff();



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

    private void ReleaseRuntimeGatesForHandoff()
    {
        _installRootUpdateGate?.Dispose();
        _installRootUpdateGate = null;

        _singleInstanceGuard?.Dispose();
        _singleInstanceGuard = null;
    }

}
