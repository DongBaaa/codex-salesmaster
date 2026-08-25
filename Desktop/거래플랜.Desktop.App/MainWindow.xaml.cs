using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Desktop.App.Views;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App;

public partial class MainWindow : Window
{
    private static readonly TimeSpan RealtimeRefreshMinInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PassiveIntegrityScanMinInterval = TimeSpan.FromMinutes(5);

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
    private readonly BackgroundTaskTracker _windowBackgroundWork = new();
    private readonly SemaphoreSlim _passiveSyncTransitionGate = new(1, 1);
    private readonly object _mainScopeSyncStopGate = new();
    private CancellationTokenSource _windowBackgroundWorkCts = new();
    private CancellationTokenSource? _realtimeRevisionCts;
    private Task? _realtimeRevisionTask;
    private Task _realtimeRevisionDrainTask = Task.CompletedTask;
    private Task _runtimeSyncDrainTask = Task.CompletedTask;
    private Task _windowCommandDrainTask = Task.CompletedTask;
    private Task _mainScopeSyncDrainTask = Task.CompletedTask;
    private bool _windowDrainWaitsWithoutDeadline;
    private bool _businessDatabaseTransitionInProgress;
    private Dictionary<Window, bool>? _shutdownWindowEnabledStates;
    private bool _mainScopeSyncStopRequested;
    private bool _isInitialized;
    private bool _runtimeServicesStarted;
    private DateTime _lastCentralRefreshUtc = DateTime.MinValue;
    private DateTime _lastPassiveIntegrityScanUtc = DateTime.MinValue;
    private long _lastPassiveServerRevisionHint;
    private int _passiveSyncFailureCount;
    private long _nextPassiveSyncRetryUtcTicks;
    private bool _centralRefreshInProgress;
    private bool _deactivateFlushInProgress;
    private bool _updatePromptInProgress;
    private bool _isClosingOrClosed;
    private bool _runtimeSafetyCheckInProgress;
    private bool _dataIntegrityPromptInProgress;
    private bool _isCompactResponsiveLayout;
    private bool _compactDashboardExpanded;
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
        MainWindowResponsiveLayoutPolicy.ApplyInitialWindowSize(this);
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
        Loaded += MainWindow_Loaded;
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

    private void MainWindow_Loaded(
        object sender,
        RoutedEventArgs e) =>
        ApplyResponsiveLayout(
            new Size(
                MainRootScrollViewer.ActualWidth,
                MainRootScrollViewer.ActualHeight));

    private void MainRootScrollViewer_SizeChanged(
        object sender,
        SizeChangedEventArgs e) =>
        ApplyResponsiveLayout(e.NewSize);

    internal void ApplyResponsiveLayoutForAudit(Size clientSize) =>
        ApplyResponsiveLayout(clientSize);

    private void ApplyResponsiveLayout(Size clientSize)
    {
        var useContentScrollFallback =
            MainWindowResponsiveLayoutPolicy
                .ShouldUseContentScrollFallback(clientSize);
        var rootScrollBarVisibility =
            useContentScrollFallback
                ? ScrollBarVisibility.Auto
                : ScrollBarVisibility.Disabled;
        MainRootScrollViewer.HorizontalScrollBarVisibility =
            rootScrollBarVisibility;
        MainRootScrollViewer.VerticalScrollBarVisibility =
            rootScrollBarVisibility;
        MainRootPanel.Width =
            useContentScrollFallback
                ? Math.Max(
                    clientSize.Width,
                    MainWindowResponsiveLayoutPolicy
                        .MinimumContentWidthDip)
                : double.NaN;
        MainRootPanel.Height =
            useContentScrollFallback
                ? Math.Max(
                    clientSize.Height,
                    MainWindowResponsiveLayoutPolicy
                        .MinimumContentHeightDip)
                : double.NaN;

        var useCompactLayout =
            MainWindowResponsiveLayoutPolicy.ShouldUseCompactLayout(
                clientSize);

        if (_isCompactResponsiveLayout != useCompactLayout)
        {
            _isCompactResponsiveLayout = useCompactLayout;
            _compactDashboardExpanded = false;
        }

        MainWindowTitlePanel.Visibility =
            useCompactLayout
                ? Visibility.Collapsed
                : Visibility.Visible;
        CompactDashboardToggleButton.Visibility =
            useCompactLayout
                ? Visibility.Visible
                : Visibility.Collapsed;
        CompactDesktopUpdateHost.Visibility =
            useCompactLayout
                ? Visibility.Visible
                : Visibility.Collapsed;
        DesktopUpdateBannerResponsiveHost.Visibility =
            useCompactLayout
                ? Visibility.Collapsed
                : Visibility.Visible;
        CurrentUserDisplayText.Visibility =
            useCompactLayout
                ? Visibility.Collapsed
                : Visibility.Visible;
        CurrentUserSeparator.Visibility =
            useCompactLayout
                ? Visibility.Collapsed
                : Visibility.Visible;

        var showDashboardPanels = !useCompactLayout;
        DashboardSummaryPanel.Visibility =
            showDashboardPanels
                ? Visibility.Visible
                : Visibility.Collapsed;
        DashboardContractAlertsPanel.Visibility =
            showDashboardPanels
                ? Visibility.Visible
                : Visibility.Collapsed;
        CompactDashboardToggleButton.Content =
            _compactDashboardExpanded
                ? "요약 닫기"
                : "요약 펼치기";
        CompactDashboardPopup.IsOpen =
            useCompactLayout &&
            _compactDashboardExpanded;

        MainHeaderPanel.Padding =
            useCompactLayout
                ? new Thickness(12, 4, 12, 4)
                : new Thickness(16, 10, 16, 10);
        MainContentPanel.Margin =
            useCompactLayout
                ? new Thickness(10, 4, 10, 4)
                : new Thickness(10);
        InvoiceToolbarPanel.Padding =
            useCompactLayout
                ? new Thickness(10, 6, 10, 6)
                : new Thickness(10);
        InvoiceToolbarPanel.Margin =
            useCompactLayout
                ? new Thickness(0, 0, 0, 4)
                : new Thickness(0, 0, 0, 8);
        CurrentUserDisplayText.MaxWidth =
            useCompactLayout
                ? 160d
                : double.PositiveInfinity;
    }

    private void CompactDashboardToggleButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!_isCompactResponsiveLayout)
            return;

        _compactDashboardExpanded = !_compactDashboardExpanded;
        var currentSize = new Size(
            Math.Max(MainRootScrollViewer.ActualWidth, MinWidth),
            Math.Max(MainRootScrollViewer.ActualHeight, MinHeight));
        ApplyResponsiveLayout(currentSize);
    }

    private void CompactDashboardPopup_Closed(
        object? sender,
        EventArgs e)
    {
        _compactDashboardExpanded = false;
        CompactDashboardToggleButton.Content = "요약 펼치기";
        if (IsActive &&
            _isCompactResponsiveLayout &&
            CompactDashboardToggleButton.IsVisible)
        {
            _ = CompactDashboardToggleButton.Focus();
        }
    }

    private void CompactDashboardPopup_Opened(
        object? sender,
        EventArgs e) =>
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(
                () =>
                {
                    _ = CompactDashboardPopupCloseButton.Focus();
                    _ = Keyboard.Focus(
                        CompactDashboardPopupCloseButton);
                }));

    private void CompactDashboardPopupPanel_PreviewKeyDown(
        object sender,
        KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        e.Handled = true;
        CompactDashboardPopup.IsOpen = false;
    }

    private void CompactDashboardPopupCloseButton_Click(
        object sender,
        RoutedEventArgs e) =>
        CompactDashboardPopup.IsOpen = false;

    private void CompactDesktopUpdateMenuButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not Button button ||
            button.ContextMenu is null)
        {
            return;
        }

        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
    }

    public void BeginShutdownProtection(
        bool waitForCompletionWithoutDeadline = false)
    {
        if (_isClosingOrClosed)
        {
            if (waitForCompletionWithoutDeadline &&
                !_windowDrainWaitsWithoutDeadline)
            {
                _windowDrainWaitsWithoutDeadline = true;
                _windowCommandDrainTask = UpgradeWindowDrainToNoDeadlineAsync(
                    _windowCommandDrainTask);
            }

            return;
        }

        _isClosingOrClosed = true;
        _windowDrainWaitsWithoutDeadline = waitForCompletionWithoutDeadline;
        TryRunShutdownStep(
            DisableApplicationWindowsForShutdown,
            "disable application windows");
        TryRunShutdownStep(
            _windowBackgroundWork.BeginShutdown,
            "seal new window work");
        TryRunShutdownStep(
            _windowBackgroundWorkCts.Cancel,
            "cancel window lifetime token");
        TryRunShutdownStep(
            () => _centralRevisionPollTimer?.Stop(),
            "stop central revision timer");
        TryRunShutdownStep(
            () => _runtimeSafetyTimer?.Stop(),
            "stop runtime safety timer");
        TryRunShutdownStep(
            StopRealtimeRevisionMonitor,
            "stop realtime revision monitor");
        TryRunShutdownStep(
            StopRuntimeSyncService,
            "stop runtime sync service");
        TryRunShutdownStep(
            _vm.CancelPendingBackgroundWorkForShutdown,
            "cancel main view-model background work");
        _windowCommandDrainTask = DrainWindowCommandsAndSecondaryWindowsAsync(
            waitForCompletionWithoutDeadline);
    }

    private static void TryRunShutdownStep(Action step, string operation)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            AppLogger.Error(
                "APP",
                $"Synchronous shutdown step failed and the remaining drains will continue: {operation}",
                ex);
        }
    }

    private async Task UpgradeWindowDrainToNoDeadlineAsync(
        Task previousDrain)
    {
        try
        {
            await previousDrain;
            return;
        }
        catch (Exception ex)
        {
            AppLogger.Warn(
                "UI",
                $"안전 필수 종료가 요청되어 이전 창 종료 단계의 실패를 기록하고 제한시간 없이 다시 드레인합니다. {ex.Message}");
        }

        await DrainWindowCommandsAndSecondaryWindowsAsync(
            waitForCompletionWithoutDeadline: true);
    }

    public LocalStateService LocalStateService => _local;
    public RentalStateService RentalStateService => _rental;
    public RentalDocumentService RentalDocumentService => _rentalDocuments;
    public IPrintService InvoicePrintService => _invoicePrintService;
    public SessionState SessionState => _session;
    public ErpApiClient ApiClient => _api;
    internal bool IsShutdownProtectionActive => _isClosingOrClosed;
    public Task? InitialDashboardLoadTask { get; private set; }
    public async Task DrainPendingBackgroundWorkForShutdownAsync()
    {
        await Task.WhenAll(
            _vm.DrainPendingBackgroundWorkForShutdownAsync(),
            _realtimeRevisionDrainTask,
            _runtimeSyncDrainTask,
            _windowCommandDrainTask);
        await _windowBackgroundWork.DrainAsync();
    }
    public bool IsShutdownBackgroundWorkCompleted
        => _vm.IsShutdownBackgroundWorkCompleted &&
           _realtimeRevisionDrainTask.IsCompletedSuccessfully &&
           _runtimeSyncDrainTask.IsCompletedSuccessfully &&
           _windowCommandDrainTask.IsCompletedSuccessfully &&
           _windowBackgroundWork.IsCompleted;

    public bool IsMainScopeSyncDrainCompleted
        => _mainScopeSyncStopRequested && _mainScopeSyncDrainTask.IsCompletedSuccessfully;

    public Task StopAndDrainMainScopeSyncServiceAsync()
    {
        lock (_mainScopeSyncStopGate)
        {
            if (_mainScopeSyncStopRequested)
                return _mainScopeSyncDrainTask;

            // Publish a real Task before marking the stop as requested. The yield
            // also captures any synchronous exception raised while StopAndDrainAsync
            // invokes CancellationToken callbacks.
            _mainScopeSyncDrainTask = StopAndDrainMainScopeSyncServiceCoreAsync();
            _mainScopeSyncStopRequested = true;
            return _mainScopeSyncDrainTask;
        }
    }

    private async Task StopAndDrainMainScopeSyncServiceCoreAsync()
    {
        await Task.Yield();
        await _sync.StopAndDrainAsync();
    }

    public void EndShutdownProtection()
    {
        _isClosingOrClosed = false;
        _vm.ResumePendingBackgroundWorkAfterShutdownCanceled();
        _windowBackgroundWork.Resume();
        _windowBackgroundWorkCts.Dispose();
        _windowBackgroundWorkCts = new CancellationTokenSource();
        _realtimeRevisionDrainTask = Task.CompletedTask;
        _runtimeSyncDrainTask = Task.CompletedTask;
        _windowCommandDrainTask = Task.CompletedTask;
        _windowDrainWaitsWithoutDeadline = false;
        RestoreApplicationWindowsAfterCanceledShutdown();
        if (_isInitialized && !_session.IsOfflineMode)
        {
            StartRuntimeSyncService();
            StartRealtimeRevisionMonitor();
            _centralRevisionPollTimer?.Start();
            _runtimeSafetyTimer?.Start();
        }
    }

    private void RunUiAsync(Func<Task> operation, string operationName, string? userMessage = null)
    {
        if (Volatile.Read(ref _businessDatabaseTransitionInProgress))
            return;

        UiTaskHelper.Run(
            this,
            () => RunTrackedWindowOperationAsync(operation),
            "UI",
            operationName,
            userMessage ?? $"{operationName} 중 오류가 발생했습니다.");
    }

    internal async Task RunTrackedWindowOperationAsync(Func<Task> operation)
    {
        var task = _windowBackgroundWork.TryStart(operation);
        if (task is not null)
            await task;
    }

    internal Task? TryTrackWindowObservation(Func<Task> observationFactory)
        => _windowBackgroundWork.TryTrack(observationFactory);

    private void DisableApplicationWindowsForShutdown()
    {
        var windows = Application.Current?.Windows.OfType<Window>().ToArray() ?? [];
        _shutdownWindowEnabledStates = windows.ToDictionary(
            window => window,
            window => window.IsEnabled);
        foreach (var window in windows)
            window.IsEnabled = false;
    }

    private void RestoreApplicationWindowsAfterCanceledShutdown()
    {
        var states = _shutdownWindowEnabledStates;
        _shutdownWindowEnabledStates = null;
        if (states is null)
            return;

        foreach (var (window, wasEnabled) in states)
        {
            if (window.IsLoaded)
                window.IsEnabled = wasEnabled;
        }
    }

    private async Task DrainActiveWindowCommandsAsync(
        bool waitForCompletionWithoutDeadline)
    {
        while (true)
        {
            var activeTasks = SnapshotActiveWindowCommandTasks();
            if (activeTasks.Length == 0)
                return;

            try
            {
                var completion = Task.WhenAll(activeTasks);
                if (waitForCompletionWithoutDeadline)
                    await completion;
                else
                    await completion.WaitAsync(TimeSpan.FromMinutes(2));
            }
            catch (TimeoutException ex)
            {
                throw new TimeoutException(
                    "An active window command did not finish before the shutdown deadline.",
                    ex);
            }
            catch (OperationCanceledException)
            {
                // A canceled UI command has still completed and no longer owns scoped services.
            }
            catch (Exception ex)
            {
                AppLogger.Warn(
                    "UI",
                    $"종료 중 실행 중이던 화면 명령의 완료를 확인했습니다. {ex.Message}");
            }
        }
    }

    private async Task DrainWindowCommandsAndSecondaryWindowsAsync(
        bool waitForCompletionWithoutDeadline)
    {
        await CloseActiveModalWindowsForShutdownAsync(
            waitForCompletionWithoutDeadline);
        if (DialogWindowCloseHelper.ActiveNativeDialogCount > 0)
        {
            if (!waitForCompletionWithoutDeadline)
            {
                throw new InvalidOperationException(
                    "파일 선택/저장 창을 먼저 닫은 뒤 종료를 다시 시도해 주세요.");
            }

            await DialogWindowCloseHelper.WaitForNoActiveNativeDialogsAsync();
        }

        await DrainActiveWindowCommandsAsync(
            waitForCompletionWithoutDeadline);
        await CloseSecondaryWindowsForShutdownAsync(
            waitForCompletionWithoutDeadline);
    }

    private async Task CloseActiveModalWindowsForShutdownAsync(
        bool waitForCompletionWithoutDeadline)
    {
        var activeDialogs = DialogWindowCloseHelper.SnapshotActiveDialogs()
            .Where(window => !ReferenceEquals(window, this))
            .OrderByDescending(GetWindowOwnershipDepth)
            .ToArray();

        foreach (var dialog in activeDialogs)
            await CloseWindowForShutdownAsync(
                dialog,
                waitForCompletionWithoutDeadline);
    }

    private async Task CloseSecondaryWindowsForShutdownAsync(
        bool waitForCompletionWithoutDeadline)
    {
        var secondaryWindows = Application.Current?.Windows
            .OfType<Window>()
            .Where(window => !ReferenceEquals(window, this))
            .OrderByDescending(GetWindowOwnershipDepth)
            .ToArray() ?? [];

        foreach (var window in secondaryWindows)
        {
            if (!window.IsLoaded)
                continue;

            await CloseWindowForShutdownAsync(
                window,
                waitForCompletionWithoutDeadline);
        }
    }

    private static async Task CloseWindowForShutdownAsync(
        Window window,
        bool waitForCompletionWithoutDeadline)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler closedHandler = (_, _) => closed.TrySetResult();
        window.Closed += closedHandler;
        try
        {
            window.Close();
            if (!window.IsLoaded)
                closed.TrySetResult();

            if (waitForCompletionWithoutDeadline)
                await closed.Task;
            else
                await closed.Task.WaitAsync(TimeSpan.FromMinutes(2));
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(
                $"Window did not finish its close/save workflow: {window.GetType().Name}",
                ex);
        }
        finally
        {
            window.Closed -= closedHandler;
        }
    }

    internal static int GetWindowOwnershipDepth(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var visited = new HashSet<Window>(ReferenceEqualityComparer.Instance);
        var depth = 0;
        var owner = window.Owner;
        while (owner is not null && visited.Add(owner))
        {
            depth++;
            owner = owner.Owner;
        }

        return depth;
    }

    internal Task[] SnapshotActiveWindowCommandTasks()
    {
        var dataContexts = new HashSet<object>(ReferenceEqualityComparer.Instance)
        {
            _vm
        };
        var windows = Application.Current?.Windows.OfType<Window>().ToArray() ?? [];
        foreach (var window in windows)
        {
            if (window.DataContext is not null)
                dataContexts.Add(window.DataContext);
        }

        return dataContexts
            .SelectMany(dataContext => GetActiveAsyncCommandTasks(dataContext))
            .Distinct()
            .ToArray();
    }

    internal static IEnumerable<Task> GetActiveAsyncCommandTasks(
        object dataContext,
        string? excludedCommandPropertyName = null)
    {
        ArgumentNullException.ThrowIfNull(dataContext);
        foreach (var property in dataContext.GetType().GetProperties(
                     BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (property.GetIndexParameters().Length != 0 ||
                string.Equals(
                    property.Name,
                    excludedCommandPropertyName,
                    StringComparison.Ordinal) ||
                !typeof(IAsyncRelayCommand).IsAssignableFrom(property.PropertyType))
            {
                continue;
            }

            IAsyncRelayCommand? command;
            try
            {
                command = property.GetValue(dataContext) as IAsyncRelayCommand;
            }
            catch (TargetInvocationException ex)
            {
                AppLogger.Warn(
                    "UI",
                    $"종료 중 비동기 명령 상태를 읽지 못했습니다. property={property.Name}, error={ex.InnerException?.Message ?? ex.Message}");
                continue;
            }

            var executionTask = command?.ExecutionTask;
            if (executionTask is not null && !executionTask.IsCompleted)
                yield return executionTask;
        }
    }

    private Task ForgetWindowBackgroundTask(
        Func<Task> operationFactory,
        string category,
        string operation,
        Action<Exception>? onError = null,
        Action? onCompleted = null)
    {
        var task = _windowBackgroundWork.TryStart(operationFactory);
        if (task is null)
            return Task.CompletedTask;

        UiTaskHelper.Forget(task, category, operation, onError, onCompleted);
        return task;
    }

    private void ShowModelessWithDeferredLoad(
        Window window,
        Func<Task> loadAsync,
        string windowTitle,
        string failureMessage,
        Func<Task>? closedAsync = null,
        bool blockWindowDuringLoad = true)
        => WindowShowHelper.ShowModelessWithDeferredLoad(
            window,
            loadAsync,
            windowTitle,
            failureMessage,
            this,
            closedAsync,
            blockWindowDuringLoad);

    public Task InitAsync(
        bool deferStartupNotifications = false,
        CancellationToken mainScopeLifetimeToken = default)
    {
        mainScopeLifetimeToken.ThrowIfCancellationRequested();
        if (_isClosingOrClosed)
            return Task.CompletedTask;

        _vm.SyncStatus = "메인 화면을 표시했습니다. 대시보드와 거래내역은 백그라운드에서 불러오는 중입니다.";
        InitialDashboardLoadTask = ForgetWindowBackgroundTask(
            () => RunInitialDashboardLoadAsync(
                deferStartupNotifications,
                mainScopeLifetimeToken),
            "UI",
            "메인 대시보드 백그라운드 로드",
            ex =>
            {
                if (mainScopeLifetimeToken.IsCancellationRequested)
                    return;
                _vm.SyncStatus = "초기 대시보드 로드 중 오류가 발생했습니다. 메뉴는 사용할 수 있으며 필요한 화면에서 다시 조회할 수 있습니다.";
                AppLogger.Error("UI", "Initial dashboard background load failed", ex);
            });
        return Task.CompletedTask;
    }

    private async Task RunInitialDashboardLoadAsync(
        bool deferStartupNotifications,
        CancellationToken mainScopeLifetimeToken)
    {
        ServerClockCheckResult? serverClockCheck = null;
        try
        {
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            mainScopeLifetimeToken.ThrowIfCancellationRequested();
            if (_isClosingOrClosed)
                return;

            await OperationTiming.MeasureAsync(
                "APP",
                "회사 프로필 상태 점검",
                () => _local.EnsureCompanyProfilesHealthyAsync(mainScopeLifetimeToken),
                warningThreshold: TimeSpan.FromSeconds(2));

            serverClockCheck = await OperationTiming.MeasureAsync(
                "APP",
                "서버 기준 날짜 확인",
                () => _runtimeSafety.ResolveServerTodayAsync(mainScopeLifetimeToken),
                warningThreshold: TimeSpan.FromSeconds(2));
            mainScopeLifetimeToken.ThrowIfCancellationRequested();
            _vm.SetInvoiceDefaultDateRange(serverClockCheck.ServerToday);

            await OperationTiming.MeasureAsync(
                "UI",
                "메인 대시보드 로드",
                () => _vm.LoadAsync(mainScopeLifetimeToken),
                warningThreshold: TimeSpan.FromSeconds(3));
            mainScopeLifetimeToken.ThrowIfCancellationRequested();

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
            if (!mainScopeLifetimeToken.IsCancellationRequested)
            {
                StartRuntimeServicesAfterInitialDashboardLoad();
                QueueDeferredStartupSafetyChecks();
                _vm.QueueBackgroundDesktopUpdateCheck();
            }
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
        var scope = _runtimeSyncScope;
        _runtimeSyncService = null;
        _runtimeSyncScope = null;
        if (sync is not null)
            sync.SyncStatusChanged -= HandleRuntimeSyncStatusChanged;

        _runtimeSyncDrainTask = StopAndDisposeRuntimeSyncScopeAsync(sync, scope);
    }

    private static async Task StopAndDisposeRuntimeSyncScopeAsync(
        SyncService? sync,
        IServiceScope? scope)
    {
        if (sync is not null)
            await sync.StopAndDrainAsync();

        scope?.Dispose();
    }

    private void HandleRuntimeSyncStatusChanged(string status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return;

        if (!Dispatcher.CheckAccess())
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                return;

            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.DataBind,
                new Action(() => HandleRuntimeSyncStatusChanged(status)));
            return;
        }

        if (_isClosingOrClosed)
            return;

        // 자동/실시간 동기화는 짧은 주기로 실행될 수 있으므로 진행 문구로 상태바를
        // 계속 덮어쓰지 않는다. 완료·대기·오류 결과는 그대로 표시한다.
        if (string.Equals(status, "동기화 중...", StringComparison.Ordinal))
            return;

        _vm.ApplyExternalSyncStatus(status);
    }

    private async Task<T> RunIsolatedSyncAsync<T>(
        Func<SyncService, CancellationToken, Task<T>> operation,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await Task.Run(async () =>
        {
            ct.ThrowIfCancellationRequested();
            using var scope = _serviceScopeFactory.CreateScope();
            var sync = scope.ServiceProvider.GetRequiredService<SyncService>();
            sync.SyncStatusChanged += HandleRuntimeSyncStatusChanged;
            try
            {
                return await operation(sync, ct).ConfigureAwait(false);
            }
            finally
            {
                sync.SyncStatusChanged -= HandleRuntimeSyncStatusChanged;
                await sync.StopAndDrainAsync().ConfigureAwait(false);
            }
        }, ct).ConfigureAwait(true);
    }

    private void StartRealtimeRevisionMonitor()
    {
        if (_realtimeRevisionCts is not null || _session.IsOfflineMode || !_session.IsLoggedIn || _isClosingOrClosed)
            return;

        var cts = new CancellationTokenSource();
        _realtimeRevisionCts = cts;
        _realtimeRevisionTask = StartRealtimeRevisionMonitorTask(
            RunRealtimeRevisionMonitorAsync,
            cts);
    }

    private void StopRealtimeRevisionMonitor()
    {
        var cts = _realtimeRevisionCts;
        var task = _realtimeRevisionTask;
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

        if (task is null)
        {
            cts.Dispose();
            return;
        }

        _realtimeRevisionDrainTask = ObserveAndDisposeRealtimeRevisionMonitorAsync(task, cts);
        UiTaskHelper.Forget(
            _realtimeRevisionDrainTask,
            "SYNC",
            "실시간 revision monitor 종료");
    }

    internal static Task StartRealtimeRevisionMonitorTask(
        Func<CancellationToken, Task> monitor,
        CancellationTokenSource cts)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(cts);

        var token = cts.Token;
        return Task.Run(() => monitor(token), token);
    }

    internal static async Task ObserveAndDisposeRealtimeRevisionMonitorAsync(
        Task task,
        CancellationTokenSource cts)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(cts);

        try
        {
            await task.ConfigureAwait(false);
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

                var passiveRetryDelay = GetRemainingPassiveSyncRetryDelay();
                if (passiveRetryDelay > TimeSpan.Zero)
                {
                    await Task.Delay(passiveRetryDelay, ct);
                    continue;
                }

                var baselineRevision = _lastPassiveServerRevisionHint;
                if (baselineRevision <= 0)
                {
                    baselineRevision = await ResolveLocalLastSyncRevisionAsync(ct);
                    _lastPassiveServerRevisionHint = baselineRevision;
                }
                var status = await _api.WaitForSyncChangeAsync(
                    baselineRevision,
                    TimeSpan.FromSeconds(25),
                    _session.SelectedBusinessDatabaseName,
                    ct);
                if (status is null || status.CurrentServerRevision <= baselineRevision)
                    continue;

                await Dispatcher.InvokeAsync(
                    () => ForgetWindowBackgroundTask(
                        () => RunPassiveSyncRefreshAsync(
                            "실시간 변경 감지",
                            RealtimeRefreshMinInterval,
                            requireServerRevisionChange: false,
                            observedServerRevision: status.CurrentServerRevision,
                            ct: _windowBackgroundWorkCts.Token),
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

    private Task<long> ResolveLocalLastSyncRevisionAsync(CancellationToken ct)
        => ResolveLocalLastSyncRevisionAsync(_serviceScopeFactory, ct);

    internal static async Task<long> ResolveLocalLastSyncRevisionAsync(
        IServiceScopeFactory serviceScopeFactory,
        CancellationToken ct)
    {
        var raw = await RunIsolatedLocalStateOperationAsync(
            serviceScopeFactory,
            local => local.GetSettingAsync("LastSyncRevision", ct));
        return long.TryParse(raw, out var value) ? value : 0L;
    }

    internal static async Task<T> RunIsolatedLocalStateOperationAsync<T>(
        IServiceScopeFactory serviceScopeFactory,
        Func<LocalStateService, Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(serviceScopeFactory);
        ArgumentNullException.ThrowIfNull(operation);

        using var scope = serviceScopeFactory.CreateScope();
        var local = scope.ServiceProvider.GetRequiredService<LocalStateService>();
        return await operation(local).ConfigureAwait(false);
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
        ForgetWindowBackgroundTask(
            () => RunDeferredStartupSafetyChecksAsync(),
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
            () => RunPeriodicRuntimeSafetyCheckAsync(
                force: false,
                showPrompt: false,
                ct: _windowBackgroundWorkCts.Token),
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

        await RunPassiveSyncRefreshAsync(
            "창 활성화",
            TimeSpan.FromMinutes(1),
            requireServerRevisionChange: true,
            ct: _windowBackgroundWorkCts.Token);
    }

    private void MainWindow_Deactivated(object? sender, EventArgs e)
    {
        CompactDashboardPopup.IsOpen = false;
        RunUiAsync(
            () => MainWindow_DeactivatedAsync(),
            "메인 창 비활성화 처리",
            "창 비활성화 처리 중 오류가 발생했습니다.");
    }

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

        await RunPassiveSyncRefreshAsync(
            "중앙 revision polling",
            TimeSpan.FromMinutes(2),
            requireServerRevisionChange: true,
            ct: _windowBackgroundWorkCts.Token);
    }

    private void RuntimeSafetyTimer_Tick(object? sender, EventArgs e)
        => RunUiAsync(
            () => RunPeriodicRuntimeSafetyCheckAsync(
                force: false,
                ct: _windowBackgroundWorkCts.Token),
            "주기 운영 안전 점검",
            "주기 운영 안전 점검 중 오류가 발생했습니다.");

    private async Task RunPeriodicRuntimeSafetyCheckAsync(
        bool force,
        bool showPrompt = true,
        CancellationToken ct = default)
    {
        if (_isClosingOrClosed || !_isInitialized || _session.IsOfflineMode || _runtimeSafetyCheckInProgress)
            return;

        _runtimeSafetyCheckInProgress = true;
        try
        {
            var result = await _runtimeSafety.RunPeriodicIntegrityAsync(force, ct);
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

        var fullPath = Path.GetFullPath(path);
        var folder = Path.GetDirectoryName(fullPath);
        try
        {
            if (!File.Exists(fullPath))
            {
                MessageBox.Show(
                    this,
                    $"무결성 상세 리포트 파일을 찾을 수 없습니다.{Environment.NewLine}{fullPath}",
                    "주기 무결성 점검",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = fullPath,
                WorkingDirectory = Directory.Exists(folder) ? folder : AppPaths.DiagnosticsDir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppLogger.Warn("RUNTIME", $"주기 무결성 상세 리포트 열기 실패: {ex.Message}");
            if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = folder,
                        WorkingDirectory = folder,
                        UseShellExecute = true
                    });
                }
                catch (Exception folderOpenException)
                {
                    AppLogger.Warn("RUNTIME", $"주기 무결성 리포트 폴더 열기 실패: {folderOpenException.Message}");
                }
            }

            MessageBox.Show(
                this,
                $"리포트를 직접 열지 못했습니다. 폴더에서 파일을 확인하세요.{Environment.NewLine}{fullPath}{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                "주기 무결성 점검",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async Task RunPassiveSyncRefreshAsync(
        string reason,
        TimeSpan minInterval,
        bool requireServerRevisionChange,
        long? observedServerRevision = null,
        CancellationToken ct = default)
    {
        await _passiveSyncTransitionGate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (_isClosingOrClosed || !_isInitialized || _session.IsOfflineMode || _centralRefreshInProgress || _vm.ForceSyncCommand.IsRunning)
                return;
            if (GetRemainingPassiveSyncRetryDelay() > TimeSpan.Zero)
                return;

            var startAtUtc = DateTime.UtcNow;
            _centralRefreshInProgress = true;
            try
            {
                var pendingServerRevision = await GetPendingPassiveServerRevisionAsync(
                    minInterval,
                    requireServerRevisionChange,
                    observedServerRevision,
                    ct);
                if (!pendingServerRevision.HasValue)
                    return;

                var syncOutcome = await RunIsolatedSyncAsync(async (sync, token) =>
                {
                    var succeeded = await sync.TrySyncAsync(token);
                    return new PassiveSyncOutcome(succeeded, sync.LastPullChangeCount);
                }, ct);
                if (!syncOutcome.Succeeded)
                {
                    RecordPassiveSyncFailure(reason);
                    return;
                }

                ResetPassiveSyncFailureBackoff();

                _lastCentralRefreshUtc = DateTime.UtcNow;
                if (pendingServerRevision.Value > 0)
                {
                    var lastSyncRevisionRaw = await _local.GetSettingAsync("LastSyncRevision", ct);
                    _ = long.TryParse(lastSyncRevisionRaw, out var lastSyncRevision);
                    _lastPassiveServerRevisionHint = Math.Max(_lastPassiveServerRevisionHint, Math.Max(pendingServerRevision.Value, lastSyncRevision));
                }

                if (syncOutcome.PulledChangeCount > 0)
                {
                    await _vm.ReloadAfterPassiveSyncAsync(ct);
                    AppLogger.Info("SYNC", $"{reason} 후 서버 변경 {syncOutcome.PulledChangeCount:N0}건을 화면에 반영했습니다.");
                }
                else
                {
                    AppLogger.Info("SYNC", $"{reason} 후 현재 업체 DB 변경이 없어 화면 전체 재조회를 생략했습니다.");
                }

                if (DateTime.UtcNow - _lastPassiveIntegrityScanUtc >= PassiveIntegrityScanMinInterval)
                {
                    _lastPassiveIntegrityScanUtc = DateTime.UtcNow;
                    await RunDataIntegrityScanAndPromptAsync(
                        $"{reason} 후 동기화",
                        showPrompt: false,
                        ct: ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                RecordPassiveSyncFailure(reason);
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
        finally
        {
            _passiveSyncTransitionGate.Release();
        }
    }

    private readonly record struct PassiveSyncOutcome(bool Succeeded, int PulledChangeCount);

    private TimeSpan GetRemainingPassiveSyncRetryDelay()
    {
        var retryAtTicks = Interlocked.Read(ref _nextPassiveSyncRetryUtcTicks);
        if (retryAtTicks <= 0)
            return TimeSpan.Zero;

        var remaining = new DateTime(retryAtTicks, DateTimeKind.Utc) - DateTime.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private void RecordPassiveSyncFailure(string reason)
    {
        var failureCount = Math.Min(Interlocked.Increment(ref _passiveSyncFailureCount), 8);
        var retryDelay = ComputePassiveSyncRetryDelay(failureCount);
        Interlocked.Exchange(ref _nextPassiveSyncRetryUtcTicks, DateTime.UtcNow.Add(retryDelay).Ticks);
        AppLogger.Warn(
            "SYNC",
            $"{reason} 동기화 실패가 반복되어 {retryDelay.TotalSeconds:N0}초 뒤 다시 확인합니다. 업무 입력과 수동 동기화는 계속 사용할 수 있습니다.");
    }

    private void ResetPassiveSyncFailureBackoff()
    {
        Interlocked.Exchange(ref _passiveSyncFailureCount, 0);
        Interlocked.Exchange(ref _nextPassiveSyncRetryUtcTicks, 0L);
    }

    private static TimeSpan ComputePassiveSyncRetryDelay(int consecutiveFailureCount)
        => consecutiveFailureCount switch
        {
            <= 1 => TimeSpan.FromSeconds(30),
            2 => TimeSpan.FromMinutes(1),
            3 => TimeSpan.FromMinutes(2),
            _ => TimeSpan.FromMinutes(5)
        };

    public async Task RunDataIntegrityScanAndPromptAsync(
        string reason,
        bool forceShow = false,
        bool showPrompt = true,
        CancellationToken ct = default)
    {
        if (_isClosingOrClosed || _dataIntegrityPromptInProgress)
            return;

        _dataIntegrityPromptInProgress = true;
        try
        {
            var result = await OperationTiming.MeasureAsync(
                "INTEGRITY",
                $"{reason} 운영 점검",
                () => _dataIntegrity.ScanAsync(_session, ct),
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
                _vm.SyncStatus = result.BuildPassiveStartupStatusMessage();
                AppLogger.Warn(
                    "INTEGRITY",
                    $"{reason} 운영 점검 알림을 상태바로 전환했습니다. " +
                    $"notices={result.PassiveStartupNoticeIssueCount:N0}, total={result.TotalIssueCount:N0}, " +
                    $"errors={result.ErrorIssueCount:N0}, warnings={result.WarningIssueCount:N0}, info={result.InformationalIssueCount:N0}");
                return;
            }

            await Dispatcher.InvokeAsync(() => ShowDataIntegrityAlert(result), DispatcherPriority.Background);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
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
            ForgetWindowBackgroundTask(
                () => HandleDataIntegrityAlertActionAsync(args.Action, args.Summary, win, result),
                "INTEGRITY",
                "운영 점검 바로수정",
                ex => MessageBox.Show(
                    win,
                    $"운영 점검 바로가기를 열지 못했습니다.{Environment.NewLine}{ex.Message}",
                    "운영 점검",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning));
        };

        if (DialogWindowCloseHelper.ShowDialog(win) != true)
            return;

        ForgetWindowBackgroundTask(
            () => HandleDataIntegrityAlertActionAsync(win.RequestedAction, win.RequestedSummary, this, result),
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
        Window? ownerOverride = null,
        DataIntegrityScanResult? existingScanResult = null)
    {
        if (action == DataIntegrityAlertAction.None)
            return;

        if (action == DataIntegrityAlertAction.Details)
        {
            await OpenDataIntegrityIssueWindowAsync(summary?.Code, ownerOverride, existingScanResult);
            return;
        }

        if (action != DataIntegrityAlertAction.Fix)
            return;

        if (summary is null)
        {
            await OpenDataIntegrityIssueWindowAsync(null, ownerOverride, existingScanResult);
            return;
        }

        var scan = existingScanResult ?? await _dataIntegrity.ScanAsync(_session);
        var issues = scan.Issues
            .Where(issue => string.Equals(issue.Code, summary.Code, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (issues.Count == 1)
        {
            await OpenDataIntegrityFixTargetAsync(issues[0], ownerOverride);
            return;
        }

        await OpenDataIntegrityIssueWindowAsync(summary.Code, ownerOverride, scan);
    }

    private Task OpenDataIntegrityIssueWindowAsync(
        string? initialCode,
        Window? ownerOverride = null,
        DataIntegrityScanResult? initialScanResult = null)
    {
        var vm = new DataIntegrityIssueViewModel(_dataIntegrity, _session, initialCode, initialScanResult);
        var win = new DataIntegrityIssueWindow(vm)
        {
            Owner = ownerOverride ?? this
        };
        win.FixRequested += (_, args) =>
        {
            ForgetWindowBackgroundTask(
                () => OpenDataIntegrityFixTargetAsync(args.Issue, win),
                "INTEGRITY",
                "운영 점검 상세 바로수정",
                ex => MessageBox.Show(
                    win,
                    $"운영 점검 바로가기를 열지 못했습니다.{Environment.NewLine}{ex.Message}",
                    "운영 점검",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning));
        };
        win.MergeRequested += (_, args) =>
        {
            ForgetWindowBackgroundTask(
                () => MergeDataIntegrityDuplicateAsync(args.Issue, vm, win),
                "INTEGRITY",
                "운영점검 중복 병합",
                ex => MessageBox.Show(
                    win,
                    $"운영점검 중복 병합을 실행하지 못했습니다.{Environment.NewLine}{ex.Message}",
                    "운영 점검",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning));
        };
        ShowModelessWithDeferredLoad(
            win,
            () => vm.LoadAsync(),
            "운영 점검 상세",
            "운영 점검 데이터를 불러오지 못했습니다.");
        return Task.CompletedTask;
    }

    private async Task MergeDataIntegrityDuplicateAsync(
        DataIntegrityIssueDetail issue,
        DataIntegrityIssueViewModel viewModel,
        Window owner)
    {
        OfficeMutationResult result;
        if (string.Equals(issue.Code, DataIntegrityIssueCodes.ItemDuplicateCandidate, StringComparison.OrdinalIgnoreCase))
        {
            if (issue.ItemDuplicateComparison is null)
            {
                MessageBox.Show(
                    owner,
                    "품목 후보 비교 정보가 없습니다. 운영점검을 새로고침한 뒤 다시 시도하세요.",
                    "운영 점검",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            viewModel.StatusMessage = "현재 후보·동기화 상태·연결 자료 권한을 읽기 전용으로 점검하는 중입니다.";
            var review = await _dataIntegrity.PrepareItemDuplicateReviewAsync(issue, _session);
            var comparisonWindow = new ItemDuplicateComparisonWindow(review) { Owner = owner };
            var comparisonResult = DialogWindowCloseHelper.ShowDialog(comparisonWindow);
            if (comparisonWindow.RequestedItemId.HasValue)
            {
                await OpenInventoryWindowAsync(comparisonWindow.RequestedItemId.Value, owner);
                return;
            }

            if (comparisonResult != true || !comparisonWindow.SelectedCanonicalItemId.HasValue)
                return;

            var canonicalItemId = comparisonWindow.SelectedCanonicalItemId.Value;
            var selectedCandidate = review.Comparison.Candidates
                .FirstOrDefault(candidate => candidate.ItemId == canonicalItemId);
            if (selectedCandidate is null)
            {
                MessageBox.Show(
                    owner,
                    "선택한 대표 품목을 비교 후보에서 찾지 못했습니다. 운영점검을 새로고침하세요.",
                    "운영 점검",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var itemResponse = MessageBox.Show(
                owner,
                $"선택한 품목을 대표로 병합합니다.{Environment.NewLine}{Environment.NewLine}" +
                $"대표: {selectedCandidate.NameAndSpecification} ({selectedCandidate.ItemId:N}){Environment.NewLine}" +
                $"영향: {review.Comparison.SummaryText}{Environment.NewLine}{Environment.NewLine}" +
                "병합 직전에 후보·revision·동기화·참조 상태를 다시 확인합니다. 검증이 통과하면 참조를 대표 품목으로 옮기고 나머지 후보를 삭제 처리합니다. 계속할까요?",
                "운영 점검 품목 중복 병합",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (itemResponse != MessageBoxResult.Yes)
                return;

            viewModel.StatusMessage = "선택한 대표 품목과 최신 비교 스냅샷을 확인하는 중입니다.";
            result = await _dataIntegrity.MergeDuplicateItemIssueAsync(
                issue,
                canonicalItemId,
                review.Comparison.SnapshotToken,
                _session);
        }
        else
        {
            if (!issue.CanMergeDuplicates)
            {
                MessageBox.Show(
                    owner,
                    "선택 항목은 자동 병합 대상이 아닙니다. 판단 정보를 확인한 뒤 원본 화면에서 수동 정리하세요.",
                    "운영 점검",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var response = MessageBox.Show(
                owner,
                $"선택한 중복 후보를 대표 항목 1건으로 병합합니다.{Environment.NewLine}{Environment.NewLine}" +
                $"{issue.ReviewInfoDisplay}{Environment.NewLine}{Environment.NewLine}" +
                "병합 후 참조 전표/렌탈/재고 내역은 대표 항목으로 이동하고 나머지 후보는 삭제 처리됩니다. 계속할까요?",
                "운영 점검 중복 병합",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (response != MessageBoxResult.Yes)
                return;

            viewModel.StatusMessage = "중복 후보를 병합하는 중입니다.";
            result = await _dataIntegrity.MergeDuplicateIssueAsync(issue, _session);
        }

        if (!result.Success)
        {
            viewModel.StatusMessage = result.Message;
            MessageBox.Show(
                owner,
                result.Message,
                "운영 점검 중복 병합",
                MessageBoxButton.OK,
                result.PermissionDenied ? MessageBoxImage.Warning : MessageBoxImage.Information);
            return;
        }

        var serverWriteResult = await _local.WaitForServerWriteWithTimeoutAsync(TimeSpan.FromSeconds(3));
        var message = LocalStateService.ComposeServerWriteStatusMessage(result.Message, serverWriteResult);
        viewModel.StatusMessage = message;
        await viewModel.RefreshAsync();
        if (!string.IsNullOrWhiteSpace(message))
            viewModel.StatusMessage = message;

        MessageBox.Show(
            owner,
            message,
            "운영 점검 중복 병합",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
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
        long? observedServerRevision = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_sync.HasActiveOrQueuedSync)
            return null;

        var nowUtc = DateTime.UtcNow;
        if (nowUtc - _lastCentralRefreshUtc < minInterval)
            return null;

        if (_sync.HasRecentSuccessfulSync(minInterval))
            return null;

        if (await _local.HasPendingSyncChangesAsync(_session, ct))
        {
            // 실시간 감시가 이미 관측한 revision을 0으로 버리면 후속 처리에서
            // 기준 revision을 갱신하지 못해 같은 변경을 2초마다 다시 감지한다.
            // 관측값이 없는 화면 전환/강제 확인만 기존처럼 0으로 즉시 동기화한다.
            return observedServerRevision.GetValueOrDefault();
        }

        if (!requireServerRevisionChange && !observedServerRevision.HasValue)
            return 0L;

        var currentServerRevision = observedServerRevision;
        if (!currentServerRevision.HasValue)
        {
            var status = await _api.GetSyncStatusAsync(ct);
            if (status is null)
                return null;

            currentServerRevision = status.CurrentServerRevision;
        }

        var lastSyncRevisionRaw = await _local.GetSettingAsync("LastSyncRevision", ct);
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
    {
        var source = e.OriginalSource as DependencyObject;
        var row = FindAncestor<DataGridRow>(source);
        if (row?.DataContext is InvoiceListRow { IsTransactionRow: true } transactionRow)
        {
            RunUiAsync(
                () => OpenPaymentPopupForTransactionAsync(transactionRow.TransactionId ?? transactionRow.Id, null),
                "수금/지급 내역 수정");
            return;
        }

        RunUiAsync(OpenSelectedInvoiceEditorAsync, "전표 상세 열기");
    }

    private async Task OpenSelectedInvoiceEditorAsync()
    {
        if (_vm.SelectedInvoiceRow is null)
        {
            MessageBox.Show("수정할 전표를 선택하세요.", "알림", MessageBoxButton.OK);
            return;
        }
        if (_vm.SelectedInvoiceRow.IsTransactionRow)
        {
            await OpenPaymentPopupForTransactionAsync(_vm.SelectedInvoiceRow.TransactionId ?? _vm.SelectedInvoiceRow.Id, null);
            return;
        }

        var inv = await _vm.GetLatestSelectedInvoiceAsync();
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

    private void CustomerInvoiceLookupButton_Click(object sender, RoutedEventArgs e)
        => RunUiAsync(OpenCustomerInvoiceLookupWindowAsync, "거래내역 조회 새창 열기");

    private void CustomerInvoiceLookupContextMenu_Click(object sender, RoutedEventArgs e)
        => RunUiAsync(OpenCustomerInvoiceLookupWindowAsync, "거래처 거래내역 조회 새창 열기");

    private async Task OpenCustomerInvoiceLookupWindowAsync()
    {
        await FlushPendingChangesBeforeNavigationAsync("거래내역 조회");

        var lookupViewModel = new CustomerInvoiceLookupViewModel(_local, _session);
        CustomerInvoiceLookupWindow? lookupWindow = null;
        Task RefreshLookupRowsAsync()
            => lookupViewModel.RefreshRowsAsync();
        Task RefreshLookupCustomersAndRowsAsync()
            => lookupViewModel.RefreshCustomersAndRowsAsync();
        Task OpenLookupInvoiceRowAsync(InvoiceListRow row)
            => row.IsTransactionRow
                ? OpenPaymentPopupForTransactionAsync(row.TransactionId ?? row.Id, lookupWindow, RefreshLookupRowsAsync)
                : OpenInvoiceWindowAsync(row.Id, lookupWindow, RefreshLookupRowsAsync);
        Task OpenLookupCustomerAsync(Guid customerId)
            => OpenCustomerEditorAsync(customerId, lookupWindow, RefreshLookupCustomersAndRowsAsync);
        Task OpenLookupInvoiceEntryAsync(VoucherType voucherType, Data.LocalCustomer? customer)
            => OpenNewInvoiceWindowAsync(
                voucherType,
                preselectSelectedCustomer: customer is not null,
                preselectCustomerOverride: customer,
                ownerOverride: lookupWindow,
                afterClosedAsync: RefreshLookupRowsAsync);
        Task OpenLookupPaymentEntryAsync(InvoiceListRow? row, Data.LocalCustomer? customer)
        {
            if (row is null)
                return OpenPaymentPopupAsync(
                    targetInvoiceId: null,
                    ownerOverride: lookupWindow,
                    preselectCustomerOverride: customer,
                    afterPaymentChangedAsync: RefreshLookupRowsAsync);

            return row.IsTransactionRow
                ? OpenPaymentPopupForTransactionAsync(row.TransactionId ?? row.Id, lookupWindow, RefreshLookupRowsAsync)
                : OpenPaymentPopupAsync(
                    row.Id,
                    lookupWindow,
                    preselectCustomerOverride: customer,
                    afterPaymentChangedAsync: RefreshLookupRowsAsync);
        }
        Task PrintLookupInvoiceRowAsync(InvoiceListRow? row)
            => PrintInvoiceRowFromLookupAsync(row, lookupWindow);

        lookupWindow = new CustomerInvoiceLookupWindow(
            lookupViewModel,
            OpenLookupInvoiceRowAsync,
            OpenLookupCustomerAsync,
            OpenLookupInvoiceEntryAsync,
            OpenLookupPaymentEntryAsync,
            PrintLookupInvoiceRowAsync)
        {
            Owner = this
        };

        var selectedCustomerId = _vm.SelectedCustomerFilter?.Id;
        var selectedCustomerSearch = _vm.CustomerFilterText;
        ShowModelessWithDeferredLoad(
            lookupWindow,
            () => lookupViewModel.LoadAsync(selectedCustomerId, selectedCustomerSearch),
            "거래내역 조회",
            "거래내역 조회창 데이터를 불러오지 못했습니다.",
            () => _vm.LoadInvoiceListCommand.ExecuteAsync(null));
    }

    // 거래처 우클릭 -> 거래처 삭제
    private async Task PrintInvoiceRowFromLookupAsync(InvoiceListRow? row, Window? ownerOverride)
    {
        if (row is null)
        {
            MessageBox.Show(
                ownerOverride ?? this,
                "출력할 전표를 선택하세요.",
                "알림",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (row.IsTransactionRow)
        {
            MessageBox.Show(
                ownerOverride ?? this,
                "수금/지급 입력 내역은 전표 인쇄 대상이 아닙니다. 연결된 전표 행을 선택하세요.",
                "전표 인쇄",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var previousStatementInvoice = _vm.StatementInvoice;
        var previousSelectedInvoiceRow = _vm.SelectedInvoiceRow;
        try
        {
            _vm.StatementInvoice = row;
            await _vm.PrintStatementCommand.ExecuteAsync(null);
        }
        finally
        {
            _vm.StatementInvoice = previousStatementInvoice;
            _vm.SelectedInvoiceRow = previousSelectedInvoiceRow;
        }
    }

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
        await FlushPendingChangesBeforeNavigationAsync("화면 전환");
        var vm = new PeriodLedgerViewModel(
            _local,
            new PeriodLedgerAggregationService(_local),
            new PeriodLedgerExcelExportService(),
            _session);
        var win = new PeriodLedgerWindow(vm) { Owner = this };
        ShowModelessWithDeferredLoad(
            win,
            () => vm.InitializeAsync(),
            "기간별 집계",
            "기간별 집계 데이터를 불러오지 못했습니다.");
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

    private Task OpenNewInvoiceWindowAsync(
        VoucherType voucherType,
        bool preselectSelectedCustomer,
        Data.LocalCustomer? preselectCustomerOverride = null,
        Window? ownerOverride = null,
        Func<Task>? afterClosedAsync = null)
    {
        var vm = new SalesViewModel(_local, _print, _invoicePrintService, _session, voucherType);
        var win = new SalesWindow(vm) { Owner = ownerOverride ?? this };
        win.Closed += SalesWindow_Closed;
        if (afterClosedAsync is not null)
        {
            win.Closed += (_, _) => RunUiAsync(
                afterClosedAsync,
                "거래내역 조회 새창 재조회",
                "거래내역 조회 새창을 다시 불러오는 중 오류가 발생했습니다.");
        }

        ShowModelessWithDeferredLoad(
            win,
            async () =>
            {
                await vm.LoadAsync();
                vm.NewInvoice();

                var preselectCustomer = preselectCustomerOverride ?? _vm.SelectedCustomerFilter;
                if (preselectSelectedCustomer &&
                    preselectCustomer is not null &&
                    vm.CanSelectCustomer(preselectCustomer))
                {
                    vm.SetCustomer(preselectCustomer);
                    vm.MarkCurrentStateAsPristine();
                }
            },
            $"{voucherType} 전표 작성",
            "전표 작성 데이터를 불러오지 못했습니다.");
        return Task.CompletedTask;
    }

    private async Task OpenInvoiceWindowAsync(
        Guid invoiceId,
        Window? ownerOverride = null,
        Func<Task>? afterClosedAsync = null)
    {
        var invoice = await _local.GetLatestInvoiceVersionAsync(invoiceId, _session);
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

        await OpenInvoiceWindowAsync(invoice, ownerOverride, afterClosedAsync);
    }

    public Task OpenInvoiceFromChildWindowAsync(Guid invoiceId, Window? ownerOverride = null)
        => OpenInvoiceWindowAsync(invoiceId, ownerOverride);

    public async Task<bool> DeleteInvoiceFromChildWindowAsync(Guid invoiceId, long? expectedRevision, Window? ownerOverride = null)
    {
        var owner = ownerOverride ?? this;
        var invoice = await _local.GetLatestInvoiceVersionAsync(invoiceId, _session);
        if (invoice is null)
        {
            MessageBox.Show(
                owner,
                "삭제할 전표를 찾을 수 없습니다. 목록을 새로고침한 뒤 다시 시도하세요.",
                "전표 삭제",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return false;
        }

        var displayNumber = !string.IsNullOrWhiteSpace(invoice.InvoiceNumber)
            ? invoice.InvoiceNumber.Trim()
            : !string.IsNullOrWhiteSpace(invoice.LocalTempNumber)
                ? invoice.LocalTempNumber.Trim()
                : "(번호 없음)";
        var confirm = MessageBox.Show(
            owner,
            $"전표 '{displayNumber}'을 삭제하시겠습니까?{Environment.NewLine}삭제된 전표는 환경설정 > 휴지통에서 복원할 수 있습니다.",
            "전표 삭제 확인",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK)
            return false;

        var result = await _local.DeleteInvoiceAsync(invoice.Id, _session, expectedRevision ?? invoice.Revision);
        if (!result.Success)
        {
            await _vm.LoadInvoiceListCommand.ExecuteAsync(null);
            MessageBox.Show(
                owner,
                result.Message,
                result.ConcurrencyConflict ? "동시 수정 충돌" : result.PermissionDenied ? "권한 없음" : "삭제 실패",
                MessageBoxButton.OK,
                result.ConcurrencyConflict || result.PermissionDenied
                    ? MessageBoxImage.Warning
                    : MessageBoxImage.Error);
            return false;
        }

        var serverWriteResult = await _local.WaitForServerWriteWithTimeoutAsync(TimeSpan.FromSeconds(3));
        await _vm.LoadInvoiceListCommand.ExecuteAsync(null);
        MessageBox.Show(
            owner,
            LocalStateService.ComposeServerWriteStatusMessage("전표를 삭제했습니다.", serverWriteResult),
            "전표 삭제",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        return true;
    }

    private async Task OpenInvoiceWindowAsync(
        Data.LocalInvoice invoice,
        Window? ownerOverride = null,
        Func<Task>? afterClosedAsync = null)
    {
        await FlushPendingChangesBeforeNavigationAsync("화면 전환");
        var entryType = invoice.VoucherType switch
        {
            VoucherType.Purchase => VoucherType.Purchase,
            VoucherType.Procurement => VoucherType.Procurement,
            _ => VoucherType.Sales
        };

        var vm = new SalesViewModel(_local, _print, _invoicePrintService, _session, entryType);
        var win = new SalesWindow(vm) { Owner = ownerOverride ?? this };
        win.Closed += SalesWindow_Closed;
        if (afterClosedAsync is not null)
        {
            win.Closed += (_, _) => RunUiAsync(
                afterClosedAsync,
                "거래내역 조회 새창 재조회",
                "전표 편집 후 거래내역 조회 새창을 다시 불러오는 중 오류가 발생했습니다.");
        }
        ShowModelessWithDeferredLoad(
            win,
            async () =>
            {
                var latestInvoice = await _local.GetLatestInvoiceVersionAsync(invoice.Id, _session) ?? invoice;
                await vm.LoadAsync();
                await vm.LoadInvoiceAsync(latestInvoice);
            },
            $"{entryType} 전표 편집",
            "전표 상세 데이터를 불러오지 못했습니다.");
    }

    private async Task OpenCustomerEditorAsync(
        Guid customerId,
        Window? ownerOverride = null,
        Func<Task>? afterClosedAsync = null)
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

        await OpenCustomerEditorAsync(customer, ownerOverride, afterClosedAsync);
    }

    private async Task OpenCustomerEditorAsync(
        Data.LocalCustomer? customer = null,
        Window? ownerOverride = null,
        Func<Task>? afterClosedAsync = null)
    {
        await FlushPendingChangesBeforeNavigationAsync("화면 전환");
        var vm = new CustomerEditViewModel(_local, _session, _api);
        var win = new CustomerEditWindow(vm) { Owner = ownerOverride ?? this };
        win.Closed += (_, _) => RunUiAsync(
            async () =>
            {
                await _vm.RefreshCustomersCommand.ExecuteAsync(null);
                if (afterClosedAsync is not null)
                    await afterClosedAsync();
            },
            "거래처 등록/수정 후 거래처 목록 새로고침",
            "거래처 등록/수정 후 목록을 다시 불러오는 중 오류가 발생했습니다.");
        ShowModelessWithDeferredLoad(
            win,
            () => vm.LoadAsync(customer),
            "거래처 등록/수정",
            "거래처 데이터를 불러오지 못했습니다.");
    }

    private Task OpenInventoryWindowAsync()
        => OpenInventoryWindowAsync(null, null);

    private async Task OpenInventoryWindowAsync(Guid? targetItemId, Window? ownerOverride)
    {
        await FlushPendingChangesBeforeNavigationAsync("화면 전환");
        var vm = new InventoryViewModel(_local, _session);
        var win = new InventoryWindow(vm) { Owner = ownerOverride ?? this };
        ShowModelessWithDeferredLoad(
            win,
            () => targetItemId.HasValue ? vm.LoadAndSelectItemAsync(targetItemId.Value) : vm.LoadAsync(),
            "품목/재고 관리",
            "품목/재고 데이터를 불러오지 못했습니다.");
    }

    private Task OpenPaymentPopupAsync()
        => OpenPaymentPopupAsync(null, null);

    private async Task OpenPaymentPopupForTransactionAsync(
        Guid transactionId,
        Window? ownerOverride,
        Func<Task>? afterPaymentChangedAsync = null)
    {
        await FlushPendingChangesBeforeNavigationAsync("화면 전환");
        var vm = new PaymentViewModel(_local, _session);
        var win = new PaymentWindow(vm) { Owner = ownerOverride ?? this };
        Guid? refreshCustomerId = null;
        void RefreshAfterPaymentChange()
            => RunUiAsync(
                async () =>
                {
                    await _vm.RefreshAfterFinancialTransactionChangedAsync(refreshCustomerId);
                    if (afterPaymentChangedAsync is not null)
                        await afterPaymentChangedAsync();
                },
                "수금/지급 후 거래내역 재조회",
                "수금/지급 후 거래내역을 다시 불러오는 중 오류가 발생했습니다.");

        EventHandler paymentTransactionsChanged = (_, _) => RefreshAfterPaymentChange();
        vm.TransactionsChanged += paymentTransactionsChanged;
        win.Closed += (_, _) =>
        {
            vm.TransactionsChanged -= paymentTransactionsChanged;
            RefreshAfterPaymentChange();
        };
        ShowModelessWithDeferredLoad(
            win,
            async () =>
            {
                var transaction = await _local.GetTransactionAsync(transactionId, _session)
                                  ?? throw new InvalidOperationException("수정할 수금/지급 내역을 찾을 수 없습니다.");
                refreshCustomerId = transaction.CustomerId;
                var preselect = await _local.GetCustomerAsync(transaction.CustomerId, _session);
                await vm.LoadAsync(preselect);
                await vm.LoadTransactionForEditingAsync(transaction.Id);
            },
            "수금/지급",
            "수금/지급 내역을 불러오지 못했습니다.");
    }

    private async Task OpenPaymentPopupAsync(
        Guid? targetInvoiceId,
        Window? ownerOverride,
        Data.LocalCustomer? preselectCustomerOverride = null,
        Func<Task>? afterPaymentChangedAsync = null)
    {
        await FlushPendingChangesBeforeNavigationAsync("화면 전환");
        var vm = new PaymentViewModel(_local, _session);
        var win = new PaymentWindow(vm) { Owner = ownerOverride ?? this };
        Guid? refreshCustomerId = null;
        void RefreshAfterPaymentChange()
            => RunUiAsync(
                async () =>
                {
                    await _vm.RefreshAfterFinancialTransactionChangedAsync(refreshCustomerId);
                    if (afterPaymentChangedAsync is not null)
                        await afterPaymentChangedAsync();
                },
                "수금/지급 후 거래내역 재조회",
                "수금/지급 후 거래내역을 다시 불러오는 중 오류가 발생했습니다.");

        EventHandler paymentTransactionsChanged = (_, _) => RefreshAfterPaymentChange();
        vm.TransactionsChanged += paymentTransactionsChanged;
        win.Closed += (_, _) =>
        {
            vm.TransactionsChanged -= paymentTransactionsChanged;
            RefreshAfterPaymentChange();
        };
        ShowModelessWithDeferredLoad(
            win,
            async () =>
            {
                Data.LocalInvoice? targetInvoice = null;
                Data.LocalCustomer? preselect = preselectCustomerOverride ?? _vm.SelectedCustomerFilter;
                if (targetInvoiceId.HasValue)
                {
                    targetInvoice = await _local.GetLatestInvoiceVersionAsync(targetInvoiceId.Value, _session)
                                    ?? throw new InvalidOperationException("전표를 찾을 수 없어 수금/지급 창을 열 수 없습니다.");
                    preselect = await _local.GetCustomerAsync(targetInvoice.CustomerId, _session);
                }
                else if (preselect is null && _vm.SelectedInvoiceRow is not null)
                {
                    if (_vm.SelectedInvoiceRow.IsTransactionRow)
                    {
                        preselect = await _local.GetCustomerAsync(_vm.SelectedInvoiceRow.CustomerId, _session);
                    }
                    else
                    {
                        var invoice = await _vm.GetLatestSelectedInvoiceAsync();
                        if (invoice is not null)
                            preselect = await _local.GetCustomerAsync(invoice.CustomerId, _session);
                    }
                }

                refreshCustomerId = preselect?.Id ?? targetInvoice?.CustomerId;
                await vm.LoadAsync(preselect);
                if (targetInvoice is not null)
                    await vm.ConfigureForInvoiceAsync(targetInvoice);
            },
            "수금/지급",
            "수금/지급 데이터를 불러오지 못했습니다.");
    }

    private async Task OpenYeonsuDeliveryWindowAsync()
    {
        await FlushPendingChangesBeforeNavigationAsync("화면 전환");
        var vm = new YeonsuDeliveryViewModel(_local, _session);
        var win = new YeonsuDeliveryWindow(vm, _local, _print, _invoicePrintService, _session)
        {
            Owner = this
        };
        ShowModelessWithDeferredLoad(
            win,
            () => vm.InitializeAsync(),
            "매입/매출 장부",
            "매입/매출 장부 데이터를 불러오지 못했습니다.");
    }

    private async Task OpenSyncDiagnosticsWindowAsync(Window? ownerOverride = null)
    {
        await FlushPendingChangesBeforeNavigationAsync("화면 전환");
        var diagnosticsViewModel = new SyncDiagnosticsViewModel(_diagnostics, _sync, _api, _local, _rental, _session);
        var window = new SyncDiagnosticsWindow(diagnosticsViewModel)
        {
            Owner = ownerOverride ?? this
        };
        ShowModelessWithDeferredLoad(
            window,
            () => diagnosticsViewModel.LoadAsync(),
            "동기화 진단",
            "동기화 진단 데이터를 불러오지 못했습니다.");
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
                applyBusinessDatabaseChangeAsync: async () => await _vm.ReloadForBusinessDatabaseChangeAsync(),
                runBusinessDatabaseTransitionAsync: RunBusinessDatabaseTransitionAsync);
            var win = new EnvironmentSettingsWindow(vm, initialTab)
            {
                Owner = this
            };
            win.Closed += (_, _) =>
            {
                if (vm.BusinessDatabaseChanged)
                    return;

                RunUiAsync(
                    () => _vm.LoadInvoiceListCommand.ExecuteAsync(null),
                    "환경설정 닫기 후 전표 목록 새로고침",
                    "환경설정 닫기 후 전표 목록을 다시 불러오는 중 오류가 발생했습니다.");
            };
            ShowModelessWithDeferredLoad(
                win,
                () => vm.InitializeAsync(),
                "환경설정",
                "환경설정 데이터를 불러오지 못했습니다.",
                blockWindowDuringLoad: false);
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

    private async Task RunBusinessDatabaseTransitionAsync(Func<Task> transitionAsync)
    {
        ArgumentNullException.ThrowIfNull(transitionAsync);

        var wasEnabled = IsEnabled;
        var applicationWindows = Application.Current?.Windows
            .OfType<Window>()
            .Where(window => window.IsLoaded)
            .ToArray() ?? [this];
        var initiatingSettingsWindows = applicationWindows
            .OfType<EnvironmentSettingsWindow>()
            .Where(window => window.DataContext is EnvironmentSettingsViewModel { IsBusy: true })
            .ToArray();
        if (initiatingSettingsWindows.Length != 1)
        {
            throw new InvalidOperationException(
                "업체 DB 전환을 시작한 환경설정 창을 하나로 확인할 수 없습니다. 다른 환경설정 창을 닫고 다시 시도해 주세요.");
        }

        var initiatingSettingsWindow = initiatingSettingsWindows[0];
        var blockingWindows = applicationWindows
            .Where(window =>
                !ReferenceEquals(window, this) &&
                !ReferenceEquals(window, initiatingSettingsWindow))
            .ToArray();
        if (blockingWindows.Length > 0)
        {
            var blockingWindowLabels = blockingWindows
                .Select(window => string.IsNullOrWhiteSpace(window.Title)
                    ? window.GetType().Name
                    : window.Title)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(label => label, StringComparer.Ordinal)
                .ToArray();
            throw new InvalidOperationException(
                $"업체 DB 전환 전에 열려 있는 업무 창을 닫아 주세요: {string.Join(", ", blockingWindowLabels)}");
        }

        var enabledStates = applicationWindows.ToDictionary(
            window => window,
            window => window.IsEnabled);
        IsEnabled = false;
        foreach (var window in applicationWindows)
            window.IsEnabled = false;

        var transitionGateEntered = false;
        var windowWorkPaused = false;
        try
        {
            Volatile.Write(ref _businessDatabaseTransitionInProgress, true);
            _windowBackgroundWork.PauseNewWork();
            windowWorkPaused = true;
            EnsureBusinessDatabaseTransitionQuiescence(initiatingSettingsWindow);
            await _passiveSyncTransitionGate.WaitAsync();
            transitionGateEntered = true;
            EnsureBusinessDatabaseTransitionQuiescence(initiatingSettingsWindow);
            await _vm.RunBusinessDatabaseTransitionAsync(transitionAsync);
        }
        finally
        {
            if (transitionGateEntered)
                _passiveSyncTransitionGate.Release();

            if (!_isClosingOrClosed)
            {
                foreach (var (window, wasWindowEnabled) in enabledStates)
                {
                    if (window.IsLoaded)
                        window.IsEnabled = wasWindowEnabled;
                }

                IsEnabled = wasEnabled;
                if (windowWorkPaused)
                    _windowBackgroundWork.Resume();
            }

            Volatile.Write(ref _businessDatabaseTransitionInProgress, false);
        }
    }

    private void EnsureBusinessDatabaseTransitionQuiescence(
        EnvironmentSettingsWindow initiatingSettingsWindow)
    {
        if (!_windowBackgroundWork.IsIdle)
        {
            throw new InvalidOperationException(
                "진행 중인 화면 저장·조회 작업이 있습니다. 작업이 끝난 뒤 업체 DB 전환을 다시 시도해 주세요.");
        }

        var activeCommandTasks = GetActiveAsyncCommandTasks(_vm)
            .Concat(GetActiveAsyncCommandTasks(
                initiatingSettingsWindow.DataContext,
                "LoadSelectedBusinessDatabaseCommand"))
            .Distinct()
            .ToArray();
        if (activeCommandTasks.Length > 0)
        {
            throw new InvalidOperationException(
                "진행 중인 저장 명령이 있습니다. 저장이 끝난 뒤 업체 DB 전환을 다시 시도해 주세요.");
        }
    }

    private async Task OpenCustomerManagementWindowAsync()
    {
        await FlushPendingChangesBeforeNavigationAsync("화면 전환");
        var vm = new CustomerManagementViewModel(_local, _session);
        var win = new CustomerManagementWindow(vm, _local, _session, _api)
        {
            Owner = this
        };
        win.Closed += (_, _) => RunUiAsync(
            () => _vm.RefreshCustomersCommand.ExecuteAsync(null),
            "거래처 관리 닫기 후 거래처 목록 새로고침",
            "거래처 관리 닫기 후 거래처 목록을 다시 불러오는 중 오류가 발생했습니다.");
        ShowModelessWithDeferredLoad(
            win,
            () => vm.InitializeAsync(),
            "거래처 관리",
            "거래처 관리 데이터를 불러오지 못했습니다.");
    }

    private async Task OpenRentalCustomerOnboardingAsync()
    {
        await FlushPendingChangesBeforeNavigationAsync("화면 전환");
        var onboardingViewModel = new RentalCustomerOnboardingViewModel(_rental, _local, _session);
        var onboardingWindow = new RentalCustomerOnboardingWindow(onboardingViewModel)
        {
            Owner = this
        };

        onboardingWindow.Closed += (_, _) =>
        {
            if (!onboardingViewModel.IsCompleted)
                return;

            RunUiAsync(
                async () =>
                {
                    await _vm.RefreshCustomersCommand.ExecuteAsync(null);
                    await _vm.LoadInvoiceListCommand.ExecuteAsync(null);
                },
                "신규 렌탈 거래처 등록 후 메인 새로고침",
                "신규 렌탈 거래처 등록 후 메인 화면을 다시 불러오는 중 오류가 발생했습니다.");
        };
        ShowModelessWithDeferredLoad(
            onboardingWindow,
            () => onboardingViewModel.LoadAsync(),
            "신규 렌탈 거래처 등록",
            "렌탈 거래처 데이터를 불러오지 못했습니다.");
    }

    private async Task OpenRentalDashboardWindowAsync()
    {
        await FlushPendingChangesBeforeNavigationAsync("화면 전환");
        var vm = new RentalDashboardViewModel(_rental, _session);
        var win = new RentalDashboardWindow(vm)
        {
            Owner = this
        };
        ShowModelessWithDeferredLoad(
            win,
            () => vm.LoadAsync(),
            "렌탈 대시보드",
            "렌탈 대시보드 데이터를 불러오지 못했습니다.",
            () => _vm.LoadInvoiceListCommand.ExecuteAsync(null));
    }

    private async Task OpenRentalBillingWindowAsync(Guid? targetProfileId = null, Window? ownerOverride = null)
    {
        await FlushPendingChangesBeforeNavigationAsync("화면 전환");
        var vm = new RentalBillingViewModel(_rental, _local, _session, _api);
        var win = new RentalBillingWindow(
            vm,
            (invoiceId, owner) => OpenInvoiceWindowAsync(invoiceId, owner),
            (assetId, owner) => OpenRentalAssetWindowAsync(
                assetId,
                owner,
                () => vm.RefreshAfterExternalAssetEditAsync(assetId)),
            () => _vm.LoadInvoiceListCommand.ExecuteAsync(null))
        {
            Owner = ownerOverride ?? this
        };
        ShowModelessWithDeferredLoad(
            win,
            () => targetProfileId.HasValue ? vm.LoadAndSelectProfileAsync(targetProfileId.Value) : vm.LoadAsync(),
            "렌탈 청구관리",
            "렌탈 청구관리 데이터를 불러오지 못했습니다.",
            () => _vm.LoadInvoiceListCommand.ExecuteAsync(null));
    }

    private async Task OpenRentalAssetWindowAsync(
        Guid? targetAssetId = null,
        Window? ownerOverride = null,
        Func<Task>? closedAsync = null)
    {
        await FlushPendingChangesBeforeNavigationAsync("화면 전환");
        var vm = new RentalAssetViewModel(_rental, _local, _rentalDocuments, _invoicePrintService, _session);
        var win = new RentalAssetWindow(vm)
        {
            Owner = ownerOverride ?? this
        };
        ShowModelessWithDeferredLoad(
            win,
            () => targetAssetId.HasValue ? vm.LoadAndSelectAssetAsync(targetAssetId.Value) : vm.LoadAsync(),
            "렌탈 자산 / 설치현황",
            "렌탈 자산 데이터를 불러오지 못했습니다.",
            async () =>
            {
                await _vm.LoadInvoiceListCommand.ExecuteAsync(null);
                if (closedAsync is not null)
                    await closedAsync();
            });
    }

    private async Task OpenRentalSettingsWindowAsync()
    {
        await FlushPendingChangesBeforeNavigationAsync("화면 전환");
        var vm = new RentalSettingsViewModel(_rental, _local, _session);
        var win = new RentalSettingsWindow(vm)
        {
            Owner = this
        };
        ShowModelessWithDeferredLoad(
            win,
            () => vm.LoadAsync(),
            "렌탈 설정",
            "렌탈 설정 데이터를 불러오지 못했습니다.",
            () => _vm.LoadInvoiceListCommand.ExecuteAsync(null));
    }

    private void CentralRevisionPollTimer_Tick(object? sender, EventArgs e)
        => ForgetWindowBackgroundTask(
            () => PollCentralRevisionAsync(),
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
                _ = ForgetWindowBackgroundTask(
                    () => RunIsolatedSyncAsync(
                        (sync, token) => sync.TrySyncAsync(token),
                        _windowBackgroundWorkCts.Token),
                    "SYNC",
                    $"{reason} 백그라운드 동기화",
                    ex => AppLogger.Warn("SYNC", $"{reason} background sync failed: {ex.Message}"));
                return;
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            _vm.SyncStatus = $"{reason} 전 중앙 서버에 변경사항 저장 중...";
            var flushed = await RunIsolatedSyncAsync(
                (sync, _) => sync.FlushPendingChangesAsync(cts.Token),
                cts.Token);
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
        if (_isClosingOrClosed ||
            _updatePromptInProgress ||
            _session.IsOfflineMode ||
            AppRuntimeInfo.IsTestRuntime)
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
            await _updateService.StartUpdateAsync(result.Package);
            _vm.SyncStatus = $"업데이트 {result.LatestVersion} 설치를 시작했습니다.";
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
