using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Desktop.App.Views;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly LocalStateService _local;
    private readonly RentalStateService _rental;
    private readonly RentalDocumentService _rentalDocuments;
    private readonly StatementPrintService _print;
    private readonly IPrintService _invoicePrintService;
    private readonly SessionState _session;
    private readonly ErpApiClient _api;
    private readonly SyncService _sync;
    private readonly BackupService _backup;
    private readonly SyncDiagnosticsService _diagnostics;
    private readonly DataIntegrityIssueService _dataIntegrity;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly DesktopAppUpdateService _updateService;
    private readonly RuntimeSafetyMonitorService _runtimeSafety;
    private readonly DispatcherTimer _centralRevisionPollTimer;
    private readonly DispatcherTimer _runtimeSafetyTimer;
    private CancellationTokenSource? _realtimeRevisionCts;
    private Task? _realtimeRevisionTask;
    private bool _isInitialized;
    private bool _runtimeServicesStarted;
    private DateTime _lastCentralRefreshUtc = DateTime.MinValue;
    private long _lastPassiveServerRevisionHint;
    private bool _centralRefreshInProgress;
    private bool _deactivateFlushInProgress;
    private bool _updatePromptInProgress;
    private bool _isClosingOrClosed;
    private bool _runtimeSafetyCheckInProgress;
    private bool _dataIntegrityPromptInProgress;
    private string _lastDataIntegrityIssueSignature = string.Empty;
    private string? _deferredStartupDashboardMessage;
    private string? _deferredStartupClockWarningMessage;
    private IServiceScope? _runtimeSyncScope;
    private SyncService? _runtimeSyncService;

    public MainWindow(MainViewModel vm, LocalStateService local,
                      RentalStateService rental,
                      RentalDocumentService rentalDocuments,
                      StatementPrintService print,
                      IPrintService invoicePrintService,
                      SessionState session,
                      ErpApiClient api,
                      SyncService sync,
                      BackupService backup,
                      SyncDiagnosticsService diagnostics,
                      DataIntegrityIssueService dataIntegrity,
                      IServiceScopeFactory serviceScopeFactory)
    {
        InitializeComponent();
        Title = AppRuntimeInfo.WithTestLabel(Title);
        _vm = vm;
        _local = local;
        _rental = rental;
        _rentalDocuments = rentalDocuments;
        _print = print;
        _invoicePrintService = invoicePrintService;
        _session = session;
        _api = api;
        _sync = sync;
        _backup = backup;
        _diagnostics = diagnostics;
        _dataIntegrity = dataIntegrity;
        _serviceScopeFactory = serviceScopeFactory;
        _updateService = new DesktopAppUpdateService(api);
        _runtimeSafety = new RuntimeSafetyMonitorService(local, sync, backup, session, api, diagnostics, serviceScopeFactory);
        DataContext = vm;
        Activated += MainWindow_Activated;
        Deactivated += MainWindow_Deactivated;
        Closed += (_, _) => BeginShutdownProtection();
        _centralRevisionPollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(60)
        };
        _centralRevisionPollTimer.Tick += CentralRevisionPollTimer_Tick;
        _runtimeSafetyTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMinutes(30)
        };
        _runtimeSafetyTimer.Tick += RuntimeSafetyTimer_Tick;
    }

    public void BeginShutdownProtection()
    {
        if (_isClosingOrClosed)
            return;

        _isClosingOrClosed = true;
        StopRealtimeRevisionMonitor();
        StopRuntimeSyncService();
        _centralRevisionPollTimer?.Stop();
        _runtimeSafetyTimer?.Stop();
    }

    public LocalStateService LocalStateService => _local;
    public RentalStateService RentalStateService => _rental;
    public RentalDocumentService RentalDocumentService => _rentalDocuments;
    public IPrintService InvoicePrintService => _invoicePrintService;
    public SessionState SessionState => _session;
    public ErpApiClient ApiClient => _api;
    public Task? InitialDashboardLoadTask { get; private set; }

    public void EndShutdownProtection()
    {
        _isClosingOrClosed = false;
        if (_isInitialized && !_session.IsOfflineMode)
        {
            StartRuntimeSyncService();
            StartRealtimeRevisionMonitor();
            _centralRevisionPollTimer?.Start();
            _runtimeSafetyTimer?.Start();
        }
    }

    private void RunUiAsync(Func<Task> operation, string operationName, string? userMessage = null)
        => UiTaskHelper.Run(
            this,
            operation,
            "UI",
            operationName,
            userMessage ?? $"{operationName} 중 오류가 발생했습니다.");

    private void ShowDialogWithDeferredLoad(Window window, Func<Task> loadAsync, string windowTitle, string failureMessage)
    {
        var loadStarted = false;
        window.ContentRendered += async (_, _) =>
        {
            if (loadStarted)
                return;

            loadStarted = true;
            try
            {
                await OperationTiming.MeasureAsync(
                    "UI",
                    $"{windowTitle} 초기화",
                    loadAsync,
                    detail: window.GetType().Name,
                    infoThreshold: TimeSpan.FromMilliseconds(600),
                    warningThreshold: TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                AppLogger.Error("UI", $"{windowTitle} 초기화 실패", ex);
                MessageBox.Show(
                    this,
                    $"{failureMessage}{Environment.NewLine}{ex.Message}",
                    windowTitle,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                if (window.IsLoaded)
                    window.Close();
            }
        };

        window.ShowDialog();
    }

    public Task InitAsync(bool deferStartupNotifications = false)
    {
        if (_isClosingOrClosed)
            return Task.CompletedTask;

        _vm.SyncStatus = "메인 화면을 표시했습니다. 대시보드와 거래내역은 백그라운드에서 불러오는 중입니다.";
        InitialDashboardLoadTask = RunInitialDashboardLoadAsync(deferStartupNotifications);
        UiTaskHelper.Forget(
            InitialDashboardLoadTask,
            "UI",
            "메인 대시보드 백그라운드 로드",
            ex =>
            {
                _vm.SyncStatus = "초기 대시보드 로드 중 오류가 발생했습니다. 메뉴는 사용할 수 있으며 필요한 화면에서 다시 조회할 수 있습니다.";
                AppLogger.Error("UI", "Initial dashboard background load failed", ex);
            });
        return Task.CompletedTask;
    }

    private async Task RunInitialDashboardLoadAsync(bool deferStartupNotifications)
    {
        ServerClockCheckResult? serverClockCheck = null;
        try
        {
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            if (_isClosingOrClosed)
                return;

            await OperationTiming.MeasureAsync(
                "APP",
                "회사 프로필 상태 점검",
                () => _local.EnsureCompanyProfilesHealthyAsync(),
                warningThreshold: TimeSpan.FromSeconds(2));

            serverClockCheck = await OperationTiming.MeasureAsync(
                "APP",
                "서버 기준 날짜 확인",
                () => _runtimeSafety.ResolveServerTodayAsync(),
                warningThreshold: TimeSpan.FromSeconds(2));
            _vm.SetInvoiceDefaultDateRange(serverClockCheck.ServerToday);

            await OperationTiming.MeasureAsync(
                "UI",
                "메인 대시보드 로드",
                () => _vm.LoadAsync(),
                warningThreshold: TimeSpan.FromSeconds(3));

            var popupSections = new List<string>();
            if (!string.IsNullOrWhiteSpace(_vm.ContractAlertPopupMessage))
                popupSections.Add(_vm.ContractAlertPopupMessage);
            if (!string.IsNullOrWhiteSpace(_vm.RentalAlertPopupMessage))
                popupSections.Add(_vm.RentalAlertPopupMessage);

            var dashboardMessage = popupSections.Count > 0
                ? string.Join(Environment.NewLine + Environment.NewLine, popupSections)
                  + Environment.NewLine
                  + Environment.NewLine
                  + "확인을 누르면 메인화면으로 이동해 계속 작업할 수 있습니다."
                : null;

            var clockWarningMessage = serverClockCheck.WarningRequired && !string.IsNullOrWhiteSpace(serverClockCheck.WarningMessage)
                ? serverClockCheck.WarningMessage
                : null;

            if (deferStartupNotifications)
            {
                _deferredStartupDashboardMessage = dashboardMessage;
                _deferredStartupClockWarningMessage = clockWarningMessage;
            }
            else
            {
                ShowStartupNotifications(dashboardMessage, clockWarningMessage);
            }
        }
        finally
        {
            _isInitialized = true;
            StartRuntimeServicesAfterInitialDashboardLoad();
            QueueDeferredStartupSafetyChecks();
        }
    }

    private void StartRuntimeServicesAfterInitialDashboardLoad()
    {
        if (_runtimeServicesStarted || _session.IsOfflineMode || _isClosingOrClosed)
            return;

        _runtimeServicesStarted = true;
        StartRuntimeSyncService();
        StartRealtimeRevisionMonitor();
        _centralRevisionPollTimer.Start();
        _runtimeSafetyTimer.Start();
    }

    private void StartRuntimeSyncService()
    {
        if (_runtimeSyncScope is not null || _session.IsOfflineMode || _isClosingOrClosed)
            return;

        _runtimeSyncScope = _serviceScopeFactory.CreateScope();
        _runtimeSyncService = _runtimeSyncScope.ServiceProvider.GetRequiredService<SyncService>();
        _runtimeSyncService.SyncStatusChanged += HandleRuntimeSyncStatusChanged;
        _runtimeSyncService.Start(TimeSpan.FromMinutes(5));
    }

    private void StopRuntimeSyncService()
    {
        var sync = _runtimeSyncService;
        _runtimeSyncService = null;
        if (sync is not null)
            sync.SyncStatusChanged -= HandleRuntimeSyncStatusChanged;

        _runtimeSyncScope?.Dispose();
        _runtimeSyncScope = null;
    }

    private void HandleRuntimeSyncStatusChanged(string status)
    {
        if (_isClosingOrClosed || string.IsNullOrWhiteSpace(status))
            return;

        _vm.ApplyExternalSyncStatus(status);
    }

    private async Task<bool> RunIsolatedSyncAsync(Func<SyncService, Task<bool>> operation)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var sync = scope.ServiceProvider.GetRequiredService<SyncService>();
        sync.SyncStatusChanged += HandleRuntimeSyncStatusChanged;
        try
        {
            return await operation(sync);
        }
        finally
        {
            sync.SyncStatusChanged -= HandleRuntimeSyncStatusChanged;
        }
    }

    private void StartRealtimeRevisionMonitor()
    {
        if (_realtimeRevisionCts is not null || _session.IsOfflineMode || !_session.IsLoggedIn || _isClosingOrClosed)
            return;

        _realtimeRevisionCts = new CancellationTokenSource();
        _realtimeRevisionTask = Task.Run(
            () => RunRealtimeRevisionMonitorAsync(_realtimeRevisionCts.Token),
            _realtimeRevisionCts.Token);
    }

    private void StopRealtimeRevisionMonitor()
    {
        var cts = _realtimeRevisionCts;
        _realtimeRevisionCts = null;
        _realtimeRevisionTask = null;
        if (cts is null)
            return;

        try
        {
            cts.Cancel();
        }
        catch
        {
            // ignore shutdown race
        }
        finally
        {
            cts.Dispose();
        }
    }

    private async Task RunRealtimeRevisionMonitorAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_isClosingOrClosed || !_isInitialized || _session.IsOfflineMode || !_session.IsLoggedIn)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), ct);
                    continue;
                }

                if (_sync.HasActiveOrQueuedSync || _centralRefreshInProgress)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    continue;
                }

                var baselineRevision = _lastPassiveServerRevisionHint;
                var status = await _api.WaitForSyncChangeAsync(
                    baselineRevision,
                    TimeSpan.FromSeconds(25),
                    _session.SelectedBusinessDatabaseName,
                    ct);
                if (status is null || status.CurrentServerRevision <= baselineRevision)
                    continue;

                await Dispatcher.InvokeAsync(
                    () => UiTaskHelper.Forget(
                        RunPassiveSyncRefreshAsync(
                            "실시간 변경 감지",
                            TimeSpan.Zero,
                            requireServerRevisionChange: false,
                            observedServerRevision: status.CurrentServerRevision),
                        "SYNC",
                        "실시간 변경 감지 후 재동기화",
                        ex => AppLogger.Warn("SYNC", $"실시간 변경 감지 후 재동기화 재시도: {ex.Message}")),
                    DispatcherPriority.Background,
                    ct);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (IsRealtimeRevisionWaitTransient(ex))
                    AppLogger.Info("SYNC", $"실시간 변경 감지 대기 재시도: {ex.Message}");
                else
                    AppLogger.Warn("SYNC", $"실시간 변경 감지 대기 확인 필요: {ex.Message}");

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private static bool IsRealtimeRevisionWaitTransient(Exception ex)
    {
        var detail = ex.ToString();
        return ex is TaskCanceledException
               || ex is TimeoutException
               || detail.Contains("실시간 변경 대기(sync/wait)", StringComparison.OrdinalIgnoreCase)
               || detail.Contains("The operation was canceled", StringComparison.OrdinalIgnoreCase)
               || detail.Contains("A task was canceled", StringComparison.OrdinalIgnoreCase)
               || detail.Contains("timed out", StringComparison.OrdinalIgnoreCase)
               || detail.Contains("timeout", StringComparison.OrdinalIgnoreCase)
               || detail.Contains("Gateway Time-out", StringComparison.OrdinalIgnoreCase)
               || detail.Contains("504", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<long> ResolveLocalLastSyncRevisionAsync()
    {
        var raw = await _local.GetSettingAsync("LastSyncRevision");
        return long.TryParse(raw, out var value) ? value : 0L;
    }

    public void ShowDeferredStartupNotifications()
    {
        var dashboardMessage = _deferredStartupDashboardMessage;
        var clockWarningMessage = _deferredStartupClockWarningMessage;
        _deferredStartupDashboardMessage = null;
        _deferredStartupClockWarningMessage = null;

        ShowStartupNotifications(dashboardMessage, clockWarningMessage);
    }

    private void ShowStartupNotifications(string? dashboardMessage, string? clockWarningMessage)
    {
        if (_isClosingOrClosed || !IsLoaded)
            return;

        if (!string.IsNullOrWhiteSpace(dashboardMessage))
        {
            _vm.SyncStatus = "대시보드 확인 항목이 있습니다. 업무는 바로 진행할 수 있으며, 상단 대시보드 카드에서 계약/렌탈 알림을 확인하세요.";
            AppLogger.Info("DASHBOARD", "초기 대시보드 알림을 상태바로 전환했습니다." + Environment.NewLine + dashboardMessage);
        }

        if (!string.IsNullOrWhiteSpace(clockWarningMessage))
        {
            _vm.SyncStatus = "PC 시간 확인이 필요합니다. 업무는 바로 진행할 수 있으며, 로그에서 상세 내용을 확인하세요.";
            AppLogger.Warn("RUNTIME", "초기 PC 시간 경고를 상태바로 전환했습니다. " + clockWarningMessage);
        }
    }

    private void QueueDeferredStartupSafetyChecks()
    {
        UiTaskHelper.Forget(
            RunDeferredStartupSafetyChecksAsync(),
            "APP",
            "메인 화면 후속 안전 점검",
            ex => AppLogger.Warn("APP", $"메인 화면 후속 안전 점검 실패: {ex.Message}"));
    }

    private async Task RunDeferredStartupSafetyChecksAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(2));
        await WaitForInitialSyncIdleAsync(TimeSpan.FromSeconds(20));

        if (_isClosingOrClosed)
            return;

        await OperationTiming.MeasureAsync(
            "UPDATE",
            "데스크톱 업데이트 확인",
            () => CheckAndPromptForDesktopUpdateAsync(showPrompt: false),
            warningThreshold: TimeSpan.FromSeconds(2));

        if (_isClosingOrClosed)
            return;

        if (await ShouldDeferStartupRuntimeSafetyCheckAsync())
            return;

        await OperationTiming.MeasureAsync(
            "APP",
            "주기 안전 점검 초기 실행",
            () => RunPeriodicRuntimeSafetyCheckAsync(force: false, showPrompt: false),
            warningThreshold: TimeSpan.FromSeconds(2));
    }

    private async Task WaitForInitialSyncIdleAsync(TimeSpan maxWait)
    {
        var startedAtUtc = DateTime.UtcNow;
        while (!_isClosingOrClosed
               && !_session.IsOfflineMode
               && _sync.HasActiveOrQueuedSync
               && DateTime.UtcNow - startedAtUtc < maxWait)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }
    }

    private async Task<bool> ShouldDeferStartupRuntimeSafetyCheckAsync()
    {
        if (_sync.HasActiveOrQueuedSync)
        {
            _vm.SyncStatus = "초기 데이터 동기화가 진행 중입니다. 거래처/거래내역을 서버에서 받는 동안 잠시만 기다려 주세요.";
            AppLogger.Info("RUNTIME", "초기 동기화가 진행 중이라 시작 안전 점검을 뒤로 미룹니다.");
            return true;
        }

        try
        {
            if (await _vm.IsInitialServerDataLoadRequiredAsync())
            {
                _vm.SyncStatus = "초기 데이터 동기화가 필요합니다. 거래처/거래내역 수신 후 안전 점검을 진행합니다.";
                AppLogger.Info("RUNTIME", "초기 서버 데이터 수신이 필요한 상태라 시작 안전 점검을 뒤로 미룹니다.");
                return true;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("RUNTIME", $"초기 동기화 상태 확인 실패: {ex.Message}");
        }

        return false;
    }

    private async Task<DateOnly> ResolveServerTodayAsync()
    {
        var result = await _runtimeSafety.ResolveServerTodayAsync();
        return result.ServerToday;
    }

    private void MainWindow_Activated(object? sender, EventArgs e)
        => RunUiAsync(
            () => MainWindow_ActivatedAsync(),
            "메인 창 활성화 처리",
            "창 활성화 처리 중 오류가 발생했습니다.");

    private async Task MainWindow_ActivatedAsync()
    {
        if (_isClosingOrClosed || !_isInitialized || _session.IsOfflineMode)
            return;

        await RunPassiveSyncRefreshAsync("창 활성화", TimeSpan.FromMinutes(1), requireServerRevisionChange: true);
    }

    private void MainWindow_Deactivated(object? sender, EventArgs e)
        => RunUiAsync(
            () => MainWindow_DeactivatedAsync(),
            "메인 창 비활성화 처리",
            "창 비활성화 처리 중 오류가 발생했습니다.");

    private async Task MainWindow_DeactivatedAsync()
    {
        if (_isClosingOrClosed || !_isInitialized || _session.IsOfflineMode || _deactivateFlushInProgress)
            return;

        _deactivateFlushInProgress = true;
        try
        {
            await FlushPendingChangesBeforeNavigationAsync("창 비활성화", blockUntilServerFlush: false);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("SYNC", $"Window deactivation flush failed: {ex.Message}");
        }
        finally
        {
            _deactivateFlushInProgress = false;
        }
    }

    private async Task PollCentralRevisionAsync()
    {
        if (_isClosingOrClosed || !_isInitialized || _session.IsOfflineMode)
            return;

        await RunPassiveSyncRefreshAsync("중앙 revision polling", TimeSpan.FromMinutes(2), requireServerRevisionChange: true);
    }

    private void RuntimeSafetyTimer_Tick(object? sender, EventArgs e)
        => RunUiAsync(
            () => RunPeriodicRuntimeSafetyCheckAsync(force: false),
            "주기 운영 안전 점검",
            "주기 운영 안전 점검 중 오류가 발생했습니다.");

    private async Task RunPeriodicRuntimeSafetyCheckAsync(bool force, bool showPrompt = true)
    {
        if (_isClosingOrClosed || !_isInitialized || _session.IsOfflineMode || _runtimeSafetyCheckInProgress)
            return;

        _runtimeSafetyCheckInProgress = true;
        try
        {
            var result = await _runtimeSafety.RunPeriodicIntegrityAsync(force);
            if (!result.Executed)
                return;

            if (!string.IsNullOrWhiteSpace(result.StatusMessage))
                _vm.SyncStatus = result.StatusMessage;

            if (result.WarningRequired && !string.IsNullOrWhiteSpace(result.WarningMessage))
            {
                if (!showPrompt)
                {
                    _vm.SyncStatus = "운영 안전 점검에서 확인이 필요한 항목이 있습니다. 업무는 바로 진행할 수 있으며, 동기화 진단에서 상세 내용을 확인하세요.";
                    AppLogger.Warn("RUNTIME", $"초기 운영 안전 점검 알림을 상태바로 전환했습니다: {result.WarningMessage}");
                    return;
                }

                if (string.IsNullOrWhiteSpace(result.DetailReportPath))
                {
                    MessageBox.Show(
                        this,
                        result.WarningMessage,
                        "주기 무결성 점검",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                else
                {
                    var actionQuestion = result.HasDirectAction
                        ? $"{Environment.NewLine}{Environment.NewLine}거래플랜에서 문제 위치를 바로 열까요?"
                        : $"{Environment.NewLine}{Environment.NewLine}상세 내역과 수정 방법을 지금 열까요?";
                    var response = MessageBox.Show(
                        this,
                        result.WarningMessage + actionQuestion,
                        "주기 무결성 점검",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);
                    if (response == MessageBoxResult.Yes)
                        await OpenPeriodicIntegrityTargetAsync(result);
                }
            }
        }
        finally
        {
            _runtimeSafetyCheckInProgress = false;
        }
    }


    private async Task OpenPeriodicIntegrityTargetAsync(PeriodicIntegrityMonitorResult result)
    {
        if (result.HasDirectAction)
        {
            switch (result.DirectActionKind)
            {
                case DataIntegrityDirectActionKind.OpenInventoryItem when result.TargetEntityId.HasValue:
                    await OpenInventoryWindowAsync(result.TargetEntityId.Value, this);
                    return;
                case DataIntegrityDirectActionKind.OpenRentalBillingProfile when result.TargetEntityId.HasValue:
                    await OpenRentalBillingWindowAsync(result.TargetEntityId.Value, this);
                    return;
                case DataIntegrityDirectActionKind.OpenRentalAsset when result.TargetEntityId.HasValue:
                    await OpenRentalAssetWindowAsync(result.TargetEntityId.Value, this);
                    return;
                case DataIntegrityDirectActionKind.OpenSyncDiagnostics:
                    await OpenSyncDiagnosticsWindowAsync(this);
                    return;
                case DataIntegrityDirectActionKind.OpenEnvironmentSettings:
                    await OpenEnvironmentSettingsWindowAsync(EnvironmentSettingsInitialTab.General);
                    return;
            }
        }

        OpenPeriodicIntegrityReport(result.DetailReportPath);
    }
    private void OpenPeriodicIntegrityReport(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            if (!File.Exists(path))
            {
                MessageBox.Show(
                    this,
                    $"무결성 상세 리포트 파일을 찾을 수 없습니다.{Environment.NewLine}{path}",
                    "주기 무결성 점검",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppLogger.Warn("RUNTIME", $"주기 무결성 상세 리포트 열기 실패: {ex.Message}");
            var folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = folder,
                    UseShellExecute = true
                });
            }

            MessageBox.Show(
                this,
                $"리포트를 직접 열지 못했습니다. 폴더에서 파일을 확인하세요.{Environment.NewLine}{path}{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                "주기 무결성 점검",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async Task RunPassiveSyncRefreshAsync(
        string reason,
        TimeSpan minInterval,
        bool requireServerRevisionChange,
        long? observedServerRevision = null)
    {
        if (_isClosingOrClosed || !_isInitialized || _session.IsOfflineMode || _centralRefreshInProgress || _vm.ForceSyncCommand.IsRunning)
            return;

        var startAtUtc = DateTime.UtcNow;
        _centralRefreshInProgress = true;
        try
        {
            var pendingServerRevision = await GetPendingPassiveServerRevisionAsync(
                minInterval,
                requireServerRevisionChange,
                observedServerRevision);
            if (!pendingServerRevision.HasValue)
                return;

            var syncOk = await RunIsolatedSyncAsync(sync => sync.TrySyncAsync());
            if (!syncOk)
                return;

            _lastCentralRefreshUtc = DateTime.UtcNow;
            if (pendingServerRevision.Value > 0)
            {
                var lastSyncRevisionRaw = await _local.GetSettingAsync("LastSyncRevision");
                _ = long.TryParse(lastSyncRevisionRaw, out var lastSyncRevision);
                _lastPassiveServerRevisionHint = Math.Max(_lastPassiveServerRevisionHint, Math.Max(pendingServerRevision.Value, lastSyncRevision));
            }

            await _vm.ReloadAfterPassiveSyncAsync();
            AppLogger.Info("SYNC", $"{reason} 후 경량 재동기화 완료");
            await RunDataIntegrityScanAndPromptAsync($"{reason} 후 동기화");
        }
        catch (Exception ex)
        {
            AppLogger.Warn("SYNC", $"{reason} refresh failed: {ex.Message}");
        }
        finally
        {
            OperationTiming.LogIfSlow(
                "SYNC",
                $"{reason} 경량 재동기화",
                DateTime.UtcNow - startAtUtc,
                detail: requireServerRevisionChange ? "revision-check" : "forced-check");
            _centralRefreshInProgress = false;
        }
    }

    public async Task RunDataIntegrityScanAndPromptAsync(string reason, bool forceShow = false, bool showPrompt = true)
    {
        if (_isClosingOrClosed || _dataIntegrityPromptInProgress)
            return;

        _dataIntegrityPromptInProgress = true;
        try
        {
            var result = await OperationTiming.MeasureAsync(
                "INTEGRITY",
                $"{reason} 운영 점검",
                () => _dataIntegrity.ScanAsync(_session),
                warningThreshold: TimeSpan.FromSeconds(3));

            if (!result.HasIssues)
                return;

            if (!forceShow && !result.HasPassiveStartupNoticeIssues)
                return;

            var issueSignature = forceShow
                ? result.IssueSignature
                : result.PassiveStartupNoticeSignature;
            if (!forceShow && string.Equals(_lastDataIntegrityIssueSignature, issueSignature, StringComparison.Ordinal))
                return;

            _lastDataIntegrityIssueSignature = issueSignature;
            if (!showPrompt)
            {
                _vm.SyncStatus = "운영 점검 알림: 확인할 항목이 있습니다. 목록 조회와 업무는 계속 가능하며, 상세 내용은 동기화 진단에서 확인하세요.";
                AppLogger.Warn("INTEGRITY", $"{reason} 운영 점검 알림을 상태바로 전환했습니다. issues={result.Issues.Count:N0}");
                return;
            }

            await Dispatcher.InvokeAsync(() => ShowDataIntegrityAlert(result), DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("INTEGRITY", $"{reason} 운영 점검 실패: {ex.Message}");
        }
        finally
        {
            _dataIntegrityPromptInProgress = false;
        }
    }

    private void ShowDataIntegrityAlert(DataIntegrityScanResult result)
    {
        if (_isClosingOrClosed || !IsLoaded)
            return;

        var vm = new DataIntegrityAlertViewModel(result);
        var win = new DataIntegrityAlertWindow
        {
            Owner = this,
            DataContext = vm
        };
        win.NonClosingActionRequested += (_, args) =>
        {
            UiTaskHelper.Forget(
                HandleDataIntegrityAlertActionAsync(args.Action, args.Summary, win),
                "INTEGRITY",
                "운영 점검 바로수정",
                ex => MessageBox.Show(
                    win,
                    $"운영 점검 바로가기를 열지 못했습니다.{Environment.NewLine}{ex.Message}",
                    "운영 점검",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning));
        };

        if (win.ShowDialog() != true)
            return;

        UiTaskHelper.Forget(
            HandleDataIntegrityAlertActionAsync(win.RequestedAction, win.RequestedSummary, this),
            "INTEGRITY",
            "운영 점검 바로가기",
            ex => MessageBox.Show(
                this,
                $"운영 점검 바로가기를 열지 못했습니다.{Environment.NewLine}{ex.Message}",
                "운영 점검",
                MessageBoxButton.OK,
                MessageBoxImage.Warning));
    }

    private async Task HandleDataIntegrityAlertActionAsync(
        DataIntegrityAlertAction action,
        DataIntegrityIssueSummary? summary,
        Window? ownerOverride = null)
    {
        if (action == DataIntegrityAlertAction.None)
            return;

        if (action == DataIntegrityAlertAction.Details)
        {
            await OpenDataIntegrityIssueWindowAsync(summary?.Code, ownerOverride);
            return;
        }

        if (action != DataIntegrityAlertAction.Fix)
            return;

        if (summary is null)
        {
            await OpenDataIntegrityIssueWindowAsync(null, ownerOverride);
            return;
        }

        var scan = await _dataIntegrity.ScanAsync(_session);
        var issues = scan.Issues
            .Where(issue => string.Equals(issue.Code, summary.Code, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (issues.Count == 1)
        {
            await OpenDataIntegrityFixTargetAsync(issues[0], ownerOverride);
            return;
        }

        await OpenDataIntegrityIssueWindowAsync(summary.Code, ownerOverride);
    }

    private async Task OpenDataIntegrityIssueWindowAsync(string? initialCode, Window? ownerOverride = null)
    {
        var vm = new DataIntegrityIssueViewModel(_dataIntegrity, _session, initialCode);
        await OperationTiming.MeasureAsync(
            "UI",
            "운영 점검 상세 창 초기화",
            () => vm.LoadAsync(),
            warningThreshold: TimeSpan.FromSeconds(2));

        var win = new DataIntegrityIssueWindow(vm)
        {
            Owner = ownerOverride ?? this
        };
        if (win.ShowDialog() == true && win.RequestedIssue is not null)
            await OpenDataIntegrityFixTargetAsync(win.RequestedIssue, ownerOverride);
    }

    private async Task OpenDataIntegrityFixTargetAsync(DataIntegrityIssueDetail issue, Window? ownerOverride = null)
    {
        switch (issue.DirectActionKind)
        {
            case DataIntegrityDirectActionKind.OpenRentalBillingProfile when issue.ProfileId.HasValue:
                await OpenRentalBillingWindowAsync(issue.ProfileId.Value, ownerOverride);
                break;
            case DataIntegrityDirectActionKind.OpenRentalAsset when issue.AssetId.HasValue:
                await OpenRentalAssetWindowAsync(issue.AssetId.Value, ownerOverride);
                break;
            case DataIntegrityDirectActionKind.OpenRentalBillingProfile when issue.AssetId.HasValue:
                await OpenRentalAssetWindowAsync(issue.AssetId.Value, ownerOverride);
                break;
            case DataIntegrityDirectActionKind.OpenInventoryItem when issue.EntityId.HasValue:
                await OpenInventoryWindowAsync(issue.EntityId.Value, ownerOverride);
                break;
            case DataIntegrityDirectActionKind.OpenCustomer when issue.EntityId.HasValue:
                await OpenCustomerEditorAsync(issue.EntityId.Value, ownerOverride);
                break;
            case DataIntegrityDirectActionKind.OpenInvoice when issue.EntityId.HasValue:
                await OpenInvoiceWindowAsync(issue.EntityId.Value, ownerOverride);
                break;
            case DataIntegrityDirectActionKind.OpenPaymentForInvoice when issue.EntityId.HasValue:
                await OpenPaymentPopupAsync(issue.EntityId.Value, ownerOverride);
                break;
            case DataIntegrityDirectActionKind.OpenSyncDiagnostics:
                await OpenSyncDiagnosticsWindowAsync(ownerOverride);
                break;
            case DataIntegrityDirectActionKind.OpenEnvironmentSettings:
                await OpenEnvironmentSettingsWindowAsync(EnvironmentSettingsInitialTab.General);
                break;
            default:
                MessageBox.Show(
                    ownerOverride ?? this,
                    "이 항목은 원본 화면 바로가기를 지원하지 않습니다. 상세 내용을 기준으로 수동 확인하세요.",
                    "운영 점검",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                break;
        }
    }

    private async Task<long?> GetPendingPassiveServerRevisionAsync(
        TimeSpan minInterval,
        bool requireServerRevisionChange,
        long? observedServerRevision = null)
    {
        if (_sync.HasActiveOrQueuedSync)
            return null;

        var nowUtc = DateTime.UtcNow;
        if (nowUtc - _lastCentralRefreshUtc < minInterval)
            return null;

        if (_sync.HasRecentSuccessfulSync(minInterval))
            return null;

        if (await _local.HasPendingSyncChangesAsync())
            return 0L;

        if (!requireServerRevisionChange && !observedServerRevision.HasValue)
            return 0L;

        var currentServerRevision = observedServerRevision;
        if (!currentServerRevision.HasValue)
        {
            var status = await _api.GetSyncStatusAsync();
            if (status is null)
                return null;

            currentServerRevision = status.CurrentServerRevision;
        }

        var lastSyncRevisionRaw = await _local.GetSettingAsync("LastSyncRevision");
        _ = long.TryParse(lastSyncRevisionRaw, out var lastSyncRevision);
        var baselineRevision = Math.Max(lastSyncRevision, _lastPassiveServerRevisionHint);
        return currentServerRevision.Value > baselineRevision
            ? currentServerRevision.Value
            : null;
    }

    // F9: 거래명세서 인쇄, F6: 신규 판매작성
    // Ctrl+Shift+C: 거래처등록, Ctrl+Shift+I: 재고관리, Ctrl+Shift+P: 수금지불
    private void Window_KeyDown(object sender, KeyEventArgs e)
        => RunUiAsync(
            () => Window_KeyDownAsync(e),
            "메인 단축키 처리",
            "단축키 처리 중 오류가 발생했습니다.");

    private async Task Window_KeyDownAsync(KeyEventArgs e)
    {
        if (e.Key == Key.F9)
        {
            if (_vm.PrintStatementCommand.CanExecute(null))
                _vm.PrintStatementCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F6)
        {
            await OpenSalesWindowAsync(preselectSelectedCustomer: true);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            if (e.Key == Key.C)
            {
                await OpenCustomerEditorAsync();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.I)
            {
                await OpenInventoryWindowAsync();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.P)
            {
                await OpenPaymentPopupAsync();
                e.Handled = true;
            }
        }
    }

    // 판매작성 (리스트 툴바 버튼)
    private void SalesToolbarButton_Click(object sender, RoutedEventArgs e)
        => RunUiAsync(() => OpenSalesWindowAsync(preselectSelectedCustomer: true), "판매 전표 창 열기");

    private void PurchaseToolbarButton_Click(object sender, RoutedEventArgs e)
        => RunUiAsync(() => OpenPurchaseWindowAsync(preselectSelectedCustomer: true), "매입 전표 창 열기");

    private void ProcurementToolbarButton_Click(object sender, RoutedEventArgs e)
        => RunUiAsync(() => OpenProcurementWindowAsync(preselectSelectedCustomer: true), "견적/발주 창 열기");

    // 전표 목록 더블클릭 수정
    private void InvoiceRowsDataGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid)
            return;

        var source = e.OriginalSource as DependencyObject;
        var row = FindAncestor<DataGridRow>(source);
        if (row?.DataContext is not InvoiceListRow invoiceRow)
            return;

        if (!grid.SelectedItems.Contains(invoiceRow))
        {
            grid.SelectedItems.Clear();
            row.IsSelected = true;
            grid.SelectedItem = invoiceRow;
            _vm.SelectedInvoiceRow = invoiceRow;
        }

        row.Focus();
    }

    private void InvoiceRowsDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        => RunUiAsync(OpenSelectedInvoiceEditorAsync, "전표 상세 열기");

    private async Task OpenSelectedInvoiceEditorAsync()
    {
        if (_vm.SelectedInvoiceRow is null)
        {
            MessageBox.Show("수정할 전표를 선택하세요.", "알림", MessageBoxButton.OK);
            return;
        }

        var inv = await _local.GetInvoiceAsync(_vm.SelectedInvoiceRow.Id, _session);
        if (inv is null) return;

        await OpenInvoiceWindowAsync(inv);
    }

    // 거래처 우클릭 -> 거래처 수정
    private void CustomerEditContextMenu_Click(object sender, RoutedEventArgs e)
        => RunUiAsync(CustomerEditContextMenu_ClickAsync, "거래처 수정 창 열기");

    private async Task CustomerEditContextMenu_ClickAsync()
    {
        var customer = _vm.SelectedCustomerFilter;
        if (customer is null) return;
        await OpenCustomerEditorAsync(customer);
    }

    // 거래처 우클릭 -> 거래처 삭제
    private void CustomerDeleteContextMenu_Click(object sender, RoutedEventArgs e)
        => RunUiAsync(DeleteSelectedCustomerAsync, "거래처 삭제");

    // 거래처 더블클릭 -> 거래처 수정창 열기
    private void CustomerListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        => RunUiAsync(CustomerListBox_MouseDoubleClickAsync, "거래처 상세 열기");

    private async Task CustomerListBox_MouseDoubleClickAsync()
    {
        var customer = _vm.SelectedCustomerFilter;
        if (customer is null)
            return;

        await OpenCustomerEditorAsync(customer);
    }

    private void CustomerListBox_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox listBox)
            return;

        var source = e.OriginalSource as DependencyObject;
        var item = FindAncestor<ListBoxItem>(source);
        if (item?.DataContext is Data.LocalCustomer customer)
            listBox.SelectedItem = customer;
    }

    private async Task DeleteSelectedCustomerAsync()
    {
        var customer = _vm.SelectedCustomerFilter;
        if (customer is null)
        {
            MessageBox.Show("삭제할 거래처를 선택하세요.", "알림", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var activeInvoices = await _local.GetInvoicesAsync(customerId: customer.Id);
        if (activeInvoices.Count > 0)
        {
            MessageBox.Show(
                $"해당 거래처 전표가 {activeInvoices.Count:N0}건 남아 있어 삭제할 수 없습니다.\n먼저 전표를 모두 삭제한 뒤 거래처를 삭제하세요.",
                "거래처 삭제",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var confirm = MessageBox.Show(
            $"거래처 '{customer.NameOriginal}'를 삭제하시겠습니까?{Environment.NewLine}삭제된 항목은 환경설정 > 휴지통에서 복원할 수 있습니다.",
            "거래처 삭제 확인",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK)
            return;

        var deleteCustomerResult = await _local.DeleteCustomerAsync(customer.Id, _session, customer.Revision);
        if (!deleteCustomerResult.Success)
        {
            MessageBox.Show(deleteCustomerResult.Message, "거래처 삭제", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await _vm.RefreshCustomersCommand.ExecuteAsync(null);
    }

    // 재고관리 버튼
    private void InventoryButton_Click(object sender, RoutedEventArgs e)
        => RunUiAsync(OpenInventoryWindowAsync, "재고관리 창 열기");

    // 거래처등록 버튼
    private void CustomerEditButton_Click(object sender, RoutedEventArgs e)
        => RunUiAsync(() => OpenCustomerEditorAsync(), "거래처 등록 창 열기");

    // 거래처삭제 버튼
    private void CustomerDeleteButton_Click(object sender, RoutedEventArgs e)
        => RunUiAsync(DeleteSelectedCustomerAsync, "거래처 삭제");

    private void CustomerManagementButton_Click(object sender, RoutedEventArgs e)
        => RunUiAsync(OpenCustomerManagementWindowAsync, "거래처관리 창 열기");

    private void CustomerSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is null)
            return;

        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
    }

    private void CustomerRegisterMenuItem_Click(object sender, RoutedEventArgs e)
        => CustomerEditButton_Click(sender, e);

    private void CustomerDeleteMenuItem_Click(object sender, RoutedEventArgs e)
        => CustomerDeleteButton_Click(sender, e);

    private void CustomerManagementMenuItem_Click(object sender, RoutedEventArgs e)
        => CustomerManagementButton_Click(sender, e);

    private void NewRentalCustomerButton_Click(object sender, RoutedEventArgs e)
        => RunUiAsync(OpenRentalCustomerOnboardingAsync, "신규 렌탈 거래처 등록");

    private void DeleteSelectedInvoicesContextMenu_Click(object sender, RoutedEventArgs e)
        => RunUiAsync(
            () => DeleteSelectedInvoicesContextMenu_ClickAsync(sender),
            "선택 전표 삭제",
            "전표를 삭제하는 중 오류가 발생했습니다.");

    private async Task DeleteSelectedInvoicesContextMenu_ClickAsync(object sender)
    {
        var rows = GetSelectedInvoiceRows(sender).ToList();
        await _vm.DeleteInvoiceRowsAsync(rows);
    }

    private static IEnumerable<InvoiceListRow> GetSelectedInvoiceRows(object sender)
    {
        if (sender is not MenuItem menuItem)
            return Enumerable.Empty<InvoiceListRow>();

        if (menuItem.Parent is not ContextMenu contextMenu)
            return Enumerable.Empty<InvoiceListRow>();

        if (contextMenu.PlacementTarget is not DataGrid grid)
            return Enumerable.Empty<InvoiceListRow>();

        return grid.SelectedItems.OfType<InvoiceListRow>();
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T found)
                return found;
            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    // 판매작성 버튼(헤더)
    private void SalesButton_Click(object sender, RoutedEventArgs e)
        => RunUiAsync(() => OpenSalesWindowAsync(preselectSelectedCustomer: false), "판매 전표 창 열기");

    // 수금지불 버튼(헤더)
    private void PaymentButton_Click(object sender, RoutedEventArgs e)
        => RunUiAsync(OpenPaymentPopupAsync, "수금/지급 창 열기");

    // 기간별 집계 버튼(헤더)
    private void PeriodLedgerButton_Click(object sender, RoutedEventArgs e)
        => RunUiAsync(OpenPeriodLedgerWindowAsync, "기간별 집계 창 열기");

    private async Task OpenPeriodLedgerWindowAsync()
    {
        var vm = new PeriodLedgerViewModel(
            _local,
            new PeriodLedgerAggregationService(_local),
            new PeriodLedgerExcelExportService(),
            _session);

        await OperationTiming.MeasureAsync(
            "UI",
            "기간별 집계 창 초기화",
            () => vm.InitializeAsync(),
            warningThreshold: TimeSpan.FromSeconds(2));
        var win = new PeriodLedgerWindow(vm) { Owner = this };
        win.ShowDialog();
    }

    private void YeonsuDeliveryButton_Click(object sender, RoutedEventArgs e)
        => RunUiAsync(OpenYeonsuDeliveryWindowAsync, "매입/매출 장부 창 열기");

    private void EnvironmentSettingsButton_Click(object sender, RoutedEventArgs e)
        => RunUiAsync(() => OpenEnvironmentSettingsWindowAsync(), "환경설정 창 열기");

    private void RecycleBinButton_Click(object sender, RoutedEventArgs e)
        => RunUiAsync(() => OpenEnvironmentSettingsWindowAsync(EnvironmentSettingsInitialTab.RecycleBin), "휴지통 창 열기");

    private void LogoutButton_Click(object sender, RoutedEventArgs e)
        => RunUiAsync(LogoutAsync, "로그아웃", "로그아웃 처리 중 오류가 발생했습니다.");

    private async Task LogoutAsync()
    {
        var answer = MessageBox.Show(
            "현재 로그인 상태를 해제하고 로그인 화면으로 이동하시겠습니까?",
            "로그아웃",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
            return;

        try
        {
            await FlushPendingChangesBeforeNavigationAsync("로그아웃", blockUntilServerFlush: true);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("AUTH", $"로그아웃 전 변경사항 저장 시도 실패: {ex.Message}");
        }

        if (Application.Current is App app)
            app.RequestRestartToLogin();

        Close();
    }

    private void RentalManagementButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is null)
            return;

        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
    }

    private void RentalDashboardMenuItem_Click(object sender, RoutedEventArgs e)
        => RunUiAsync(OpenRentalDashboardWindowAsync, "렌탈 대시보드 창 열기");

    private void RentalBillingMenuItem_Click(object sender, RoutedEventArgs e)
        => RunUiAsync(() => OpenRentalBillingWindowAsync(), "렌탈 청구관리 창 열기");

    private void RentalAssetMenuItem_Click(object sender, RoutedEventArgs e)
        => RunUiAsync(() => OpenRentalAssetWindowAsync(), "렌탈 자산 창 열기");

    private void RentalSettingsMenuItem_Click(object sender, RoutedEventArgs e)
        => RunUiAsync(OpenRentalSettingsWindowAsync, "렌탈 설정 창 열기");

    // 전표 목록 탭의 수금 입력 버튼
    private void PaymentEntryButton_Click(object sender, RoutedEventArgs e)
        => RunUiAsync(OpenPaymentPopupAsync, "전표 목록 수금/지급 창 열기");

    private async Task OpenSalesWindowAsync(bool preselectSelectedCustomer)
    {
        await FlushPendingChangesBeforeNavigationAsync("화면 전환");
        await OpenNewInvoiceWindowAsync(거래플랜.Shared.Contracts.VoucherType.Sales, preselectSelectedCustomer);
    }

    private async Task OpenPurchaseWindowAsync(bool preselectSelectedCustomer)
    {
        await FlushPendingChangesBeforeNavigationAsync("화면 전환");
        await OpenNewInvoiceWindowAsync(거래플랜.Shared.Contracts.VoucherType.Purchase, preselectSelectedCustomer);
    }

    private async Task OpenProcurementWindowAsync(bool preselectSelectedCustomer)
    {
        await FlushPendingChangesBeforeNavigationAsync("화면 전환");
        await OpenNewInvoiceWindowAsync(거래플랜.Shared.Contracts.VoucherType.Procurement, preselectSelectedCustomer);
    }

    private async Task OpenNewInvoiceWindowAsync(
        거래플랜.Shared.Contracts.VoucherType voucherType,
        bool preselectSelectedCustomer)
    {
        var vm = new SalesViewModel(_local, _print, _invoicePrintService, _session, voucherType);
        await OperationTiming.MeasureAsync(
            "UI",
            $"{voucherType} 전표 창 초기화",
            () => vm.LoadAsync(),
            warningThreshold: TimeSpan.FromSeconds(2));
        vm.NewInvoice();

        if (preselectSelectedCustomer &&
            _vm.SelectedCustomerFilter is not null &&
            vm.CanSelectCustomer(_vm.SelectedCustomerFilter))
        {
            vm.SetCustomer(_vm.SelectedCustomerFilter);
            vm.MarkCurrentStateAsPristine();
        }

        var win = new SalesWindow(vm) { Owner = this };
        win.Closed += SalesWindow_Closed;
        win.Show();
    }

    private async Task OpenInvoiceWindowAsync(Guid invoiceId, Window? ownerOverride = null)
    {
        var invoice = await _local.GetInvoiceAsync(invoiceId, _session);
        if (invoice is null)
        {
            MessageBox.Show(
                ownerOverride ?? this,
                "전표를 찾을 수 없어 전표 작성창을 열 수 없습니다.",
                "운영 점검",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        await OpenInvoiceWindowAsync(invoice, ownerOverride);
    }

    private async Task OpenInvoiceWindowAsync(Data.LocalInvoice invoice, Window? ownerOverride = null)
    {
        await FlushPendingChangesBeforeNavigationAsync("화면 전환");
        var entryType = invoice.VoucherType switch
        {
            VoucherType.Purchase => VoucherType.Purchase,
            VoucherType.Procurement => VoucherType.Procurement,
            _ => VoucherType.Sales
        };

        var vm = new SalesViewModel(_local, _print, _invoicePrintService, _session, entryType);
        await OperationTiming.MeasureAsync(
            "UI",
            $"{entryType} 전표 편집 창 초기화",
            () => vm.LoadAsync(),
            warningThreshold: TimeSpan.FromSeconds(2));
        await OperationTiming.MeasureAsync(
            "UI",
            $"{entryType} 전표 상세 로드",
            () => vm.LoadInvoiceAsync(invoice),
            warningThreshold: TimeSpan.FromSeconds(2));

        var win = new SalesWindow(vm) { Owner = ownerOverride ?? this };
        win.Closed += SalesWindow_Closed;
        win.Show();
    }

    private async Task OpenCustomerEditorAsync(Guid customerId, Window? ownerOverride = null)
    {
        var customer = await _local.GetCustomerAsync(customerId, _session);
        if (customer is null)
        {
            MessageBox.Show(
                ownerOverride ?? this,
                "거래처를 찾을 수 없어 거래처 수정창을 열 수 없습니다.",
                "운영 점검",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        await OpenCustomerEditorAsync(customer, ownerOverride);
    }

    private async Task OpenCustomerEditorAsync(Data.LocalCustomer? customer = null, Window? ownerOverride = null)
    {
        await FlushPendingChangesBeforeNavigationAsync("화면 전환");
        var vm = new CustomerEditViewModel(_local, _session);
        await OperationTiming.MeasureAsync(
            "UI",
            "거래처 등록/수정 창 초기화",
            () => vm.LoadAsync(customer),
            warningThreshold: TimeSpan.FromSeconds(2));

        var win = new CustomerEditWindow(vm) { Owner = ownerOverride ?? this };
        if (win.ShowDialog() == true)
            await _vm.RefreshCustomersCommand.ExecuteAsync(null);
    }

    private Task OpenInventoryWindowAsync()
        => OpenInventoryWindowAsync(null, null);

    private async Task OpenInventoryWindowAsync(Guid? targetItemId, Window? ownerOverride)
    {
        await FlushPendingChangesBeforeNavigationAsync("화면 전환");
        var vm = new InventoryViewModel(_local, _session);
        await OperationTiming.MeasureAsync(
            "UI",
            "품목/재고 관리 창 초기화",
            () => targetItemId.HasValue ? vm.LoadAndSelectItemAsync(targetItemId.Value) : vm.LoadAsync(),
            warningThreshold: TimeSpan.FromSeconds(2));
        var win = new InventoryWindow(vm) { Owner = ownerOverride ?? this };
        win.Show();
    }

    private Task OpenPaymentPopupAsync()
        => OpenPaymentPopupAsync(null, null);

    private async Task OpenPaymentPopupAsync(Guid? targetInvoiceId, Window? ownerOverride)
    {
        await FlushPendingChangesBeforeNavigationAsync("화면 전환");
        var vm = new PaymentViewModel(_local, _session);

        Data.LocalInvoice? targetInvoice = null;
        Data.LocalCustomer? preselect = _vm.SelectedCustomerFilter;
        if (targetInvoiceId.HasValue)
        {
            targetInvoice = await _local.GetInvoiceAsync(targetInvoiceId.Value, _session);
            if (targetInvoice is null)
            {
                MessageBox.Show(
                    ownerOverride ?? this,
                    "전표를 찾을 수 없어 수금/지급 창을 열 수 없습니다.",
                    "운영 점검",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            preselect = await _local.GetCustomerAsync(targetInvoice.CustomerId, _session);
        }
        else if (preselect is null && _vm.SelectedInvoiceRow is not null)
        {
            var invoice = await _local.GetInvoiceAsync(_vm.SelectedInvoiceRow.Id, _session);
            if (invoice is not null)
                preselect = await _local.GetCustomerAsync(invoice.CustomerId, _session);
        }

        var refreshCustomerId = preselect?.Id ?? targetInvoice?.CustomerId;
        await OperationTiming.MeasureAsync(
            "UI",
            "수금/지급 창 초기화",
            () => vm.LoadAsync(preselect),
            warningThreshold: TimeSpan.FromSeconds(2));
        if (targetInvoice is not null)
            await vm.ConfigureForInvoiceAsync(targetInvoice);

        var win = new PaymentWindow(vm) { Owner = ownerOverride ?? this };
        void RefreshMainAfterPaymentChange()
            => RunUiAsync(
                () => _vm.RefreshAfterFinancialTransactionChangedAsync(refreshCustomerId),
                "수금/지급 후 메인 화면 재조회",
                "수금/지급 후 메인 화면을 다시 불러오는 중 오류가 발생했습니다.");

        EventHandler paymentTransactionsChanged = (_, _) => RefreshMainAfterPaymentChange();
        vm.TransactionsChanged += paymentTransactionsChanged;
        win.Closed += (_, _) =>
        {
            vm.TransactionsChanged -= paymentTransactionsChanged;
            RefreshMainAfterPaymentChange();
        };
        win.Show();
    }

    private async Task OpenYeonsuDeliveryWindowAsync()
    {
        await FlushPendingChangesBeforeNavigationAsync("화면 전환");
        var vm = new YeonsuDeliveryViewModel(_local, _session);
        await OperationTiming.MeasureAsync(
            "UI",
            "매입/매출 장부 창 초기화",
            () => vm.InitializeAsync(),
            warningThreshold: TimeSpan.FromSeconds(2));
        var win = new YeonsuDeliveryWindow(vm, _local, _print, _invoicePrintService, _session)
        {
            Owner = this
        };
        win.Show();
    }

    private async Task OpenSyncDiagnosticsWindowAsync(Window? ownerOverride = null)
    {
        await FlushPendingChangesBeforeNavigationAsync("화면 전환");
        var diagnosticsViewModel = new SyncDiagnosticsViewModel(_diagnostics, _sync, _api, _local, _rental, _session);
        await OperationTiming.MeasureAsync(
            "UI",
            "동기화 진단 창 초기화",
            () => diagnosticsViewModel.LoadAsync(),
            warningThreshold: TimeSpan.FromSeconds(2));

        var window = new SyncDiagnosticsWindow(diagnosticsViewModel)
        {
            Owner = ownerOverride ?? this
        };
        window.ShowDialog();
    }

    private async Task OpenEnvironmentSettingsWindowAsync(EnvironmentSettingsInitialTab initialTab = EnvironmentSettingsInitialTab.General)
    {
        try
        {
            await FlushPendingChangesBeforeNavigationAsync("화면 전환");
            var vm = new EnvironmentSettingsViewModel(
                _local,
                _session,
                _api,
                _sync,
                _backup,
                _diagnostics,
                _dataIntegrity,
                _rental,
                _print,
                _rentalDocuments,
                _invoicePrintService,
                async () => await _vm.ReloadForBusinessDatabaseChangeAsync());
            await OperationTiming.MeasureAsync(
                "UI",
                "환경설정 창 초기화",
                () => vm.InitializeAsync(),
                warningThreshold: TimeSpan.FromSeconds(2));
            var win = new EnvironmentSettingsWindow(vm, initialTab)
            {
                Owner = this
            };
            win.ShowDialog();

            if (!vm.BusinessDatabaseChanged)
            {
                try
                {
                    await _vm.LoadInvoiceListCommand.ExecuteAsync(null);
                }
                catch (Exception refreshEx)
                {
                    AppLogger.Error("SETTINGS", "환경설정 닫기 후 전표 목록 새로고침 실패", refreshEx);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("SETTINGS", "환경설정 창 열기 실패", ex);
            MessageBox.Show(
                $"환경설정을 여는 중 오류가 발생했습니다.{Environment.NewLine}{ex.Message}",
                "환경설정",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async Task OpenCustomerManagementWindowAsync()
    {
        await FlushPendingChangesBeforeNavigationAsync("화면 전환");
        var vm = new CustomerManagementViewModel(_local, _session);
        await OperationTiming.MeasureAsync(
            "UI",
            "거래처 관리 창 초기화",
            () => vm.InitializeAsync(),
            warningThreshold: TimeSpan.FromSeconds(2));
        var win = new CustomerManagementWindow(vm, _local, _session)
        {
            Owner = this
        };
        win.ShowDialog();
        await _vm.RefreshCustomersCommand.ExecuteAsync(null);
    }

    private async Task OpenRentalCustomerOnboardingAsync()
    {
        await FlushPendingChangesBeforeNavigationAsync("화면 전환");
        var onboardingViewModel = new RentalCustomerOnboardingViewModel(_rental, _local, _session);
        await OperationTiming.MeasureAsync(
            "UI",
            "신규 렌탈 거래처 등록 창 초기화",
            () => onboardingViewModel.LoadAsync(),
            warningThreshold: TimeSpan.FromSeconds(2));

        var onboardingWindow = new RentalCustomerOnboardingWindow(onboardingViewModel)
        {
            Owner = this
        };

        onboardingWindow.ShowDialog();
        if (!onboardingViewModel.IsCompleted)
            return;

        await _vm.RefreshCustomersCommand.ExecuteAsync(null);
        await _vm.LoadInvoiceListCommand.ExecuteAsync(null);
    }

    private async Task OpenRentalDashboardWindowAsync()
    {
        await FlushPendingChangesBeforeNavigationAsync("화면 전환");
        var vm = new RentalDashboardViewModel(_rental, _session);
        var win = new RentalDashboardWindow(vm)
        {
            Owner = this
        };
        ShowDialogWithDeferredLoad(win, () => vm.LoadAsync(), "렌탈 대시보드", "렌탈 대시보드 데이터를 불러오지 못했습니다.");
        await _vm.LoadInvoiceListCommand.ExecuteAsync(null);
    }

    private async Task OpenRentalBillingWindowAsync(Guid? targetProfileId = null, Window? ownerOverride = null)
    {
        await FlushPendingChangesBeforeNavigationAsync("화면 전환");
        var vm = new RentalBillingViewModel(_rental, _local, _session);
        var win = new RentalBillingWindow(vm)
        {
            Owner = ownerOverride ?? this
        };
        ShowDialogWithDeferredLoad(
            win,
            () => targetProfileId.HasValue ? vm.LoadAndSelectProfileAsync(targetProfileId.Value) : vm.LoadAsync(),
            "렌탈 청구관리",
            "렌탈 청구관리 데이터를 불러오지 못했습니다.");

        if (vm.InvoiceToOpenAfterClose.HasValue)
        {
            var invoice = await _local.GetInvoiceAsync(vm.InvoiceToOpenAfterClose.Value);
            if (invoice is not null)
                await OpenInvoiceWindowAsync(invoice);
        }

        await _vm.LoadInvoiceListCommand.ExecuteAsync(null);
    }

    private async Task OpenRentalAssetWindowAsync(Guid? targetAssetId = null, Window? ownerOverride = null)
    {
        await FlushPendingChangesBeforeNavigationAsync("화면 전환");
        var vm = new RentalAssetViewModel(_rental, _local, _rentalDocuments, _invoicePrintService, _session);
        var win = new RentalAssetWindow(vm)
        {
            Owner = ownerOverride ?? this
        };
        ShowDialogWithDeferredLoad(
            win,
            () => targetAssetId.HasValue ? vm.LoadAndSelectAssetAsync(targetAssetId.Value) : vm.LoadAsync(),
            "렌탈 자산 / 설치현황",
            "렌탈 자산 데이터를 불러오지 못했습니다.");
        await _vm.LoadInvoiceListCommand.ExecuteAsync(null);
    }

    private async Task OpenRentalSettingsWindowAsync()
    {
        await FlushPendingChangesBeforeNavigationAsync("화면 전환");
        var vm = new RentalSettingsViewModel(_rental, _local, _session);
        var win = new RentalSettingsWindow(vm)
        {
            Owner = this
        };
        ShowDialogWithDeferredLoad(win, () => vm.LoadAsync(), "렌탈 설정", "렌탈 설정 데이터를 불러오지 못했습니다.");
        await _vm.LoadInvoiceListCommand.ExecuteAsync(null);
    }

    private void CentralRevisionPollTimer_Tick(object? sender, EventArgs e)
        => UiTaskHelper.Forget(
            PollCentralRevisionAsync(),
            "UI",
            "중앙 revision polling",
            ex => AppLogger.Warn("SYNC", $"중앙 revision polling 실패: {ex.Message}"));

    private void SalesWindow_Closed(object? sender, EventArgs e)
        => RunUiAsync(
            () => _vm.LoadInvoiceListCommand.ExecuteAsync(null),
            "전표 창 종료 후 목록 재조회",
            "전표 목록을 다시 불러오는 중 오류가 발생했습니다.");

    private async Task FlushPendingChangesBeforeNavigationAsync(string reason, bool blockUntilServerFlush = false)
    {
        if (_isClosingOrClosed || _session.IsOfflineMode)
            return;

        var startAtUtc = DateTime.UtcNow;
        if (!blockUntilServerFlush && _sync.HasActiveOrQueuedSync)
            return;

        var dirtyCount = await _local.CountDirtyAsync(_session);
        if (dirtyCount == 0)
            return;

        try
        {
            if (!blockUntilServerFlush)
            {
                _vm.SyncStatus = $"{reason} 전 변경사항을 백그라운드로 동기화합니다...";
                UiTaskHelper.Forget(
                    RunIsolatedSyncAsync(sync => sync.TrySyncAsync()),
                    "SYNC",
                    $"{reason} 백그라운드 동기화",
                    ex => AppLogger.Warn("SYNC", $"{reason} background sync failed: {ex.Message}"));
                return;
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            _vm.SyncStatus = $"{reason} 전 중앙 서버에 변경사항 저장 중...";
            var flushed = await RunIsolatedSyncAsync(sync => sync.FlushPendingChangesAsync(cts.Token));
            var remainingDirtyCount = await _local.CountDirtyAsync(_session);
            if (!flushed || remainingDirtyCount > 0)
            {
                _vm.SyncStatus = await _local.GetPendingSyncWaitingMessageAsync(
                                     _session,
                                     $"{reason} 전 변경사항을 서버에 모두 반영하지 못했습니다.",
                                     cts.Token)
                                 ?? $"{reason} 전 서버 반영 대기 데이터 {remainingDirtyCount:N0}건이 남아 있습니다.";
                AppLogger.Warn("SYNC", $"{reason} flush incomplete: flushed={flushed}, remainingDirty={remainingDirtyCount}");
            }
            else
            {
                _vm.SyncStatus = $"{reason} 전 변경사항을 서버에 모두 반영했습니다.";
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("SYNC", $"{reason} flush failed: {ex.Message}");
        }
        finally
        {
            OperationTiming.LogIfSlow(
                "SYNC",
                $"{reason} 전 dirty flush",
                DateTime.UtcNow - startAtUtc,
                detail: $"dirty={dirtyCount:N0}, block={blockUntilServerFlush}");
        }
    }

    private async Task<bool> EnsureReadyForDesktopUpdateAsync(string targetVersion)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        _vm.SyncStatus = $"업데이트 {targetVersion} 전 dirty 데이터를 모두 동기화하는 중...";
        var readiness = await UpdateReadinessService.EnsureReadyForUpdateAsync(_local, _sync, _session, cts.Token);
        if (readiness.CanProceed)
        {
            if (readiness.SyncAttempted)
                _vm.SyncStatus = readiness.Message;

            return true;
        }

        _vm.SyncStatus = readiness.Message;
        MessageBox.Show(
            readiness.Message + Environment.NewLine + Environment.NewLine + "모든 dirty 데이터가 중앙 서버에 반영된 뒤에만 업데이트를 시작할 수 있습니다.",
            "업데이트 보류",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        return false;
    }

    private async Task CheckAndPromptForDesktopUpdateAsync(bool showPrompt = true)
    {
        if (_isClosingOrClosed || _updatePromptInProgress || _session.IsOfflineMode)
            return;

        _updatePromptInProgress = true;
        try
        {
            var result = await _updateService.CheckForUpdatesAsync();
            if (!result.IsUpdateAvailable || result.Package is null)
                return;

            var lastPromptedVersion = await _local.GetSettingAsync("Update.LastPromptedDesktopVersion");
            if (string.Equals(lastPromptedVersion, result.LatestVersion, StringComparison.OrdinalIgnoreCase))
                return;

            if (!showPrompt)
            {
                _vm.SyncStatus = $"새 PC 버전 {result.LatestVersion}이 준비되어 있습니다. 업무는 바로 진행할 수 있습니다.";
                AppLogger.Info("UPDATE", $"초기 업데이트 알림을 상태바로 전환했습니다. version={result.LatestVersion}");
                return;
            }

            var answer = MessageBox.Show(
                $"새 PC 버전 {result.LatestVersion}이 준비되어 있습니다.{Environment.NewLine}{Environment.NewLine}" +
                "지금 업데이트를 시작하시겠습니까?",
                "업데이트 알림",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (answer != MessageBoxResult.Yes)
            {
                await _local.SetSettingAsync("Update.LastPromptedDesktopVersion", result.LatestVersion, CancellationToken.None);
                return;
            }

            if (!await EnsureReadyForDesktopUpdateAsync(result.LatestVersion))
                return;

            await _local.SetSettingAsync("Update.LastPromptedDesktopVersion", result.LatestVersion, CancellationToken.None);
            _updateService.StartUpdate(result.Package);
            _vm.SyncStatus = $"업데이트 {result.LatestVersion} 설치를 시작했습니다.";
            Application.Current?.Dispatcher.BeginInvoke(
                new Action(App.RequestShutdownForUpdate),
                DispatcherPriority.Send);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("UPDATE", $"Desktop update prompt failed: {ex.Message}");
        }
        finally
        {
            _updatePromptInProgress = false;
        }
    }
}
