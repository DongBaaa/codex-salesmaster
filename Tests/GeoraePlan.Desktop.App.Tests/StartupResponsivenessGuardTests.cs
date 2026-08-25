using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using CommunityToolkit.Mvvm.Input;
using System.Text.RegularExpressions;
using Xunit;
using 거래플랜.Desktop.App;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class StartupResponsivenessGuardTests
{
    [Fact]
    public void PeriodicIntegrityReportOpen_DoesNotReuseAReplacedInstallDirectory()
    {
        var desktopRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "Desktop",
            "거래플랜.Desktop.App"));
        var source = File.ReadAllText(Path.Combine(desktopRoot, "MainWindow.xaml.cs"));
        var body = ExtractSourceSection(
            source,
            "private void OpenPeriodicIntegrityReport(string path)",
            "private async Task RunPassiveSyncRefreshAsync(");

        Assert.Contains("var fullPath = Path.GetFullPath(path);", body, StringComparison.Ordinal);
        Assert.Contains("WorkingDirectory = Directory.Exists(folder) ? folder : AppPaths.DiagnosticsDir", body, StringComparison.Ordinal);
        Assert.Contains("WorkingDirectory = folder", body, StringComparison.Ordinal);
        Assert.DoesNotContain("WorkingDirectory = AppContext.BaseDirectory", body, StringComparison.Ordinal);
        Assert.Contains("catch (Exception folderOpenException)", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SyncStatusCompositionCoordinator_SerializesCallbacksAndRecoversAfterFault()
    {
        var coordinator = new SyncStatusCompositionCoordinator();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = false;
        var active = 0;
        var maxActive = 0;

        var first = coordinator.RunAsync(async () =>
        {
            var current = Interlocked.Increment(ref active);
            maxActive = Math.Max(maxActive, current);
            firstEntered.SetResult();
            await releaseFirst.Task;
            Interlocked.Decrement(ref active);
        });
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = coordinator.RunAsync(() =>
        {
            var current = Interlocked.Increment(ref active);
            maxActive = Math.Max(maxActive, current);
            secondEntered = true;
            Interlocked.Decrement(ref active);
            return Task.CompletedTask;
        });
        await Task.Delay(50);
        Assert.False(secondEntered);

        releaseFirst.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, maxActive);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.RunAsync(() => throw new InvalidOperationException("synthetic")));
        await coordinator.RunAsync(() => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void SyncStatusCompositionCoordinator_LatestGenerationAndIdentityWinRegardlessOfCompletionOrder()
    {
        var coordinator = new SyncStatusCompositionCoordinator();
        var identity = new SyncStatusCompositionIdentity(
            Guid.NewGuid(),
            7,
            "tenant-a",
            "office-a",
            "database-a");

        var olderGeneration = coordinator.BeginRequest();
        var newerGeneration = coordinator.BeginRequest();

        Assert.False(coordinator.IsCurrent(olderGeneration, identity, identity));
        Assert.True(coordinator.IsCurrent(newerGeneration, identity, identity));

        var switchedBusinessDatabase = identity with
        {
            SyncScopeEpoch = identity.SyncScopeEpoch + 1,
            BusinessDatabaseName = "database-b"
        };
        Assert.False(coordinator.IsCurrent(newerGeneration, identity, switchedBusinessDatabase));

        coordinator.Invalidate();
        Assert.False(coordinator.IsCurrent(newerGeneration, identity, identity));
    }

    [Fact]
    public void SyncStatusCompositionCoordinator_SameTextAuthoritativeApply_InvalidatesRequestStartedBeforeIdentityCapture()
    {
        var coordinator = new SyncStatusCompositionCoordinator();
        var displayedStatus = "same status";
        var generation = coordinator.BeginRequest();

        coordinator.ApplyAuthoritative(() => displayedStatus = "same status");
        var identityCapturedAfterAuthoritativeApply = new SyncStatusCompositionIdentity(
            Guid.NewGuid(),
            11,
            "tenant-a",
            "office-a",
            "database-a");

        Assert.Equal("same status", displayedStatus);
        Assert.False(coordinator.IsCurrent(
            generation,
            identityCapturedAfterAuthoritativeApply,
            identityCapturedAfterAuthoritativeApply));
    }

    [Fact]
    public void RuntimeSyncStatusCallbacks_MarshalToUiDispatcherBeforeUiTaskTracking()
    {
        var repositoryRoot = FindRepositoryRoot();
        var mainWindowSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "MainWindow.xaml.cs"));
        var mainViewModelSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "ViewModels",
            "MainViewModel.cs"));
        var runtimeHandler = ExtractSourceSection(
            mainWindowSource,
            "private void HandleRuntimeSyncStatusChanged(string status)",
            "private async Task<T> RunIsolatedSyncAsync<T>(");
        var viewModelHandler = ExtractSourceSection(
            mainViewModelSource,
            "private void HandleSyncStatusChanged(string status)",
            "public void ApplyExternalSyncStatus(string status)");
        var runtimeDispatchBranch = ExtractSourceSection(
            runtimeHandler,
            "if (!Dispatcher.CheckAccess())",
            "if (_isClosingOrClosed)");
        var viewModelDispatchBranch = ExtractSourceSection(
            viewModelHandler,
            "if (dispatcher is not null && !dispatcher.CheckAccess())",
            "var generation = _syncStatusCompositionCoordinator.BeginRequest();");

        var runtimeDispatchIndex = runtimeHandler.IndexOf("Dispatcher.BeginInvoke(", StringComparison.Ordinal);
        var runtimeForwardIndex = runtimeHandler.IndexOf("_vm.ApplyExternalSyncStatus(status);", StringComparison.Ordinal);
        var runtimeBranchDispatchIndex = runtimeDispatchBranch.IndexOf("Dispatcher.BeginInvoke(", StringComparison.Ordinal);
        var runtimeBranchReturnIndex = runtimeDispatchBranch.IndexOf("return;", runtimeBranchDispatchIndex, StringComparison.Ordinal);
        Assert.Contains("if (!Dispatcher.CheckAccess())", runtimeHandler, StringComparison.Ordinal);
        Assert.True(
            runtimeDispatchIndex >= 0 && runtimeForwardIndex > runtimeDispatchIndex,
            "Runtime sync status must reach the view model through the owner Dispatcher.");
        Assert.True(
            runtimeBranchDispatchIndex >= 0 && runtimeBranchReturnIndex > runtimeBranchDispatchIndex,
            "Runtime sync status must return immediately after queueing the Dispatcher callback.");

        var viewModelDispatchIndex = viewModelHandler.IndexOf("dispatcher.BeginInvoke(", StringComparison.Ordinal);
        var viewModelTrackingIndex = viewModelHandler.IndexOf("_ownerScopeBackgroundWork.TryStart(", StringComparison.Ordinal);
        var viewModelBranchDispatchIndex = viewModelDispatchBranch.IndexOf("dispatcher.BeginInvoke(", StringComparison.Ordinal);
        var viewModelBranchReturnIndex = viewModelDispatchBranch.IndexOf("return;", viewModelBranchDispatchIndex, StringComparison.Ordinal);
        Assert.Contains("if (dispatcher is not null && !dispatcher.CheckAccess())", viewModelHandler, StringComparison.Ordinal);
        Assert.True(
            viewModelDispatchIndex >= 0 && viewModelTrackingIndex > viewModelDispatchIndex,
            "View-model sync status callbacks must enter the UI Dispatcher before UiTaskHelper tracking.");
        Assert.True(
            viewModelBranchDispatchIndex >= 0 && viewModelBranchReturnIndex > viewModelBranchDispatchIndex,
            "View-model sync status callbacks must return before starting UI-owned background work.");
    }

    [Fact]
    public void MainViewModel_SyncStatusComposition_UsesScopedLocalStateAndSerializedCallbacks()
    {
        var repositoryRoot = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "ViewModels",
            "MainViewModel.cs"));
        var businessDatabaseSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "ViewModels",
            "MainViewModel.BusinessDatabase.cs"));
        var isolatedLocalBody = ExtractSourceSection(
            source,
            "private async Task<T> RunIsolatedLocalStateAsync<T>(",
            "private async Task ApplySyncStatusAsync(");
        var applyStatusBody = ExtractSourceSection(
            source,
            "private async Task ApplySyncStatusAsync(",
            "private async Task<string> ComposeSyncStatusAsync(string status)");
        var composeStatusBody = ExtractSourceSection(
            source,
            "private async Task<string> ComposeSyncStatusAsync(string status)",
            "private static bool IsSyncAttentionStatus(string status)");
        var syncStatusChangedBody = ExtractSourceSection(
            source,
            "private void HandleSyncStatusChanged(string status)",
            "public void ApplyExternalSyncStatus(string status)");

        Assert.Contains("if (_serviceScopeFactory is null)", isolatedLocalBody, StringComparison.Ordinal);
        Assert.DoesNotContain("operation(_local)", isolatedLocalBody, StringComparison.Ordinal);
        Assert.Contains("throw new InvalidOperationException", isolatedLocalBody, StringComparison.Ordinal);
        Assert.Contains("using var scope = _serviceScopeFactory.CreateScope();", isolatedLocalBody, StringComparison.Ordinal);
        Assert.Contains("GetRequiredService<LocalStateService>()", isolatedLocalBody, StringComparison.Ordinal);
        Assert.Contains("await operation(local).ConfigureAwait(false)", isolatedLocalBody, StringComparison.Ordinal);

        Assert.Contains("_syncStatusCompositionCoordinator.RunAsync", applyStatusBody, StringComparison.Ordinal);
        Assert.Contains("IsSyncStatusCompositionCurrent(generation, identity)", applyStatusBody, StringComparison.Ordinal);
        Assert.Contains("ApplyComposedSyncStatusIfCurrent", applyStatusBody, StringComparison.Ordinal);
        Assert.Contains("partial void OnSyncStatusChanging(string value)", applyStatusBody, StringComparison.Ordinal);
        Assert.Contains("InvalidatePendingSyncStatusComposition();", applyStatusBody, StringComparison.Ordinal);
        Assert.Contains("SyncStatus = status", applyStatusBody, StringComparison.Ordinal);
        Assert.Contains("if (_serviceScopeFactory is null)", composeStatusBody, StringComparison.Ordinal);
        Assert.Contains("return status;", composeStatusBody, StringComparison.Ordinal);
        Assert.Contains("RunIsolatedLocalStateAsync(async local =>", composeStatusBody, StringComparison.Ordinal);
        Assert.Contains("local.CountDirtyAsync(_session)", composeStatusBody, StringComparison.Ordinal);
        Assert.Contains("local.GetPendingSyncWaitingMessageAsync(_session", composeStatusBody, StringComparison.Ordinal);
        Assert.DoesNotContain("_local.CountDirtyAsync(_session)", composeStatusBody, StringComparison.Ordinal);
        Assert.DoesNotContain("_local.GetPendingSyncWaitingMessageAsync(_session", composeStatusBody, StringComparison.Ordinal);
        Assert.Contains("InvalidatePendingSyncStatusComposition();", businessDatabaseSource, StringComparison.Ordinal);
        Assert.True(
            syncStatusChangedBody.IndexOf("BeginRequest()", StringComparison.Ordinal) <
            syncStatusChangedBody.IndexOf("CaptureSyncStatusCompositionIdentity()", StringComparison.Ordinal));
        Assert.Contains("ApplyAuthoritative", applyStatusBody, StringComparison.Ordinal);
        Assert.True(Regex.Matches(source, @"\bSyncStatus\s*=").Count == 2);
        Assert.Contains("SyncStatus = status;", applyStatusBody, StringComparison.Ordinal);
        Assert.Contains("ApplyAuthoritative(() => SyncStatus = status)", applyStatusBody, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void DesktopRenderModePolicy_OverridesRenderingOnlyForIsolatedTestRuntime(
        bool isTestRuntime,
        bool expected)
    {
        var forceSoftwareRenderingCalls = 0;

        var applied = DesktopRenderModePolicy.Apply(
            isTestRuntime,
            () => forceSoftwareRenderingCalls++);

        Assert.Equal(expected, applied);
        Assert.Equal(expected ? 1 : 0, forceSoftwareRenderingCalls);
    }

    [Fact]
    public void App_AppliesTestRenderingPolicyBeforeWpfStartupAndAnyWindow()
    {
        var repositoryRoot = FindRepositoryRoot();
        var appRoot = Path.Combine(repositoryRoot, "Desktop", "거래플랜.Desktop.App");
        var appSource = File.ReadAllText(Path.Combine(appRoot, "App.xaml.cs"));
        var appXaml = File.ReadAllText(Path.Combine(appRoot, "App.xaml"));
        var policySource = File.ReadAllText(Path.Combine(
            appRoot,
            "Infrastructure",
            "DesktopRenderModePolicy.cs"));
        var startupBody = ExtractSourceSection(
            appSource,
            "protected override async void OnStartup(StartupEventArgs e)",
            "var startupInstallRoots =");
        var autoLoginTakeIndex = startupBody.IndexOf(
            "IsolatedTestAutoLogin.TakeFromCurrentProcess();",
            StringComparison.Ordinal);
        var renderPolicyIndex = startupBody.IndexOf(
            "DesktopRenderModePolicy.ApplyForCurrentRuntime();",
            StringComparison.Ordinal);
        var baseStartupIndex = startupBody.IndexOf(
            "base.OnStartup(e);",
            StringComparison.Ordinal);
        Assert.True(
            autoLoginTakeIndex >= 0 &&
            renderPolicyIndex > autoLoginTakeIndex &&
            baseStartupIndex > renderPolicyIndex,
            "자동 로그인 비밀값을 먼저 소비한 뒤 렌더링 정책을 WPF 시작보다 먼저 적용해야 합니다.");
        Assert.Contains(
            "RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly",
            policySource,
            StringComparison.Ordinal);
        Assert.Contains(
            "AppRuntimeInfo.IsTestRuntime",
            policySource,
            StringComparison.Ordinal);
        Assert.DoesNotContain("StartupUri=", appXaml, StringComparison.Ordinal);
        Assert.True(
            startupBody.IndexOf(
                "DesktopRenderModePolicy.ApplyForCurrentRuntime();",
                StringComparison.Ordinal) <
            startupBody.IndexOf("base.OnStartup(e);", StringComparison.Ordinal),
            "격리 테스트 렌더링 정책은 WPF 시작 처리보다 먼저 적용되어야 합니다.");
    }

    [Fact]
    public void MainWindow_DesktopUpdateCheck_SkipsIsolatedTestRuntimeBeforeNetworkRequest()
    {
        var repositoryRoot = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "MainWindow.xaml.cs"));
        var updateCheckStart = source.IndexOf(
            "private async Task CheckAndPromptForDesktopUpdateAsync(bool showPrompt = true)",
            StringComparison.Ordinal);

        Assert.True(updateCheckStart >= 0, "MainWindow 데스크톱 업데이트 확인 메서드를 찾지 못했습니다.");

        var updateCheckBody = source[updateCheckStart..];
        var testRuntimeGuardIndex = updateCheckBody.IndexOf(
            "AppRuntimeInfo.IsTestRuntime",
            StringComparison.Ordinal);
        var networkRequestIndex = updateCheckBody.IndexOf(
            "_updateService.CheckForUpdatesAsync()",
            StringComparison.Ordinal);

        Assert.True(testRuntimeGuardIndex >= 0, "격리 테스트 런타임 가드가 필요합니다.");
        Assert.True(
            networkRequestIndex > testRuntimeGuardIndex,
            "격리 테스트 런타임은 업데이트 네트워크 요청 전에 반환해야 합니다.");
    }

    [Fact]
    public void App_ShowsResponsiveLoadingWindowBeforeLocalDatabaseMaintenance()
    {
        var repositoryRoot = FindRepositoryRoot();
        var appRoot = Path.Combine(repositoryRoot, "Desktop", "거래플랜.Desktop.App");
        var appSource = File.ReadAllText(Path.Combine(appRoot, "App.xaml.cs"));
        var loadingWindowXaml = File.ReadAllText(Path.Combine(appRoot, "Views", "StartupLoadingWindow.xaml"));

        Assert.Contains("await RunPreLoginInitializationAsync();", appSource, StringComparison.Ordinal);
        Assert.Contains("var loadingWindow = new StartupLoadingWindow();", appSource, StringComparison.Ordinal);
        Assert.Contains("loadingWindow.Show();", appSource, StringComparison.Ordinal);
        Assert.Contains("DispatcherPriority.ApplicationIdle", appSource, StringComparison.Ordinal);
        Assert.Contains("() => Task.Run(async () =>", appSource, StringComparison.Ordinal);
        Assert.True(
            appSource.IndexOf("loadingWindow.Show();", StringComparison.Ordinal) <
            appSource.IndexOf("() => Task.Run(async () =>", StringComparison.Ordinal),
            "시작 상태창은 로컬 DB 백그라운드 정비보다 먼저 표시되어야 합니다.");

        Assert.Contains("Title=\"거래플랜 - 시작 중\"", loadingWindowXaml, StringComparison.Ordinal);
        Assert.Contains("IsIndeterminate=\"True\"", loadingWindowXaml, StringComparison.Ordinal);
        Assert.Contains("ShowInTaskbar=\"True\"", loadingWindowXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void App_LoginPerformanceTiming_StopsAtFirstRenderInsteadOfUserDialogWait()
    {
        var repositoryRoot = FindRepositoryRoot();
        var appSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "App.xaml.cs"));
        var timingHelper = ExtractSourceSection(
            appSource,
            "private static bool? ShowLoginDialogWithFirstRenderTiming(",
            "private async Task RunPreLoginInitializationAsync()");

        Assert.Contains(
            "ShowLoginDialogWithFirstRenderTiming(",
            appSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "loginWin);",
            appSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "loginWindow.ContentRendered += contentRenderedHandler;",
            timingHelper,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"로그인 창 최초 표시\"",
            timingHelper,
            StringComparison.Ordinal);
        Assert.Contains(
            "return DialogWindowCloseHelper.ShowDialog(loginWindow);",
            timingHelper,
            StringComparison.Ordinal);
        Assert.Contains(
            "loginWindow.ContentRendered -= contentRenderedHandler;",
            timingHelper,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "OperationTiming.Measure(",
            timingHelper,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\"로그인 창 표시\"",
            appSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void App_LoginDialogNonSuccess_LogsExplicitExitReasonBeforeShutdown()
    {
        var repositoryRoot = FindRepositoryRoot();
        var appSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "App.xaml.cs"));
        var nonSuccessBranch = ExtractSourceSection(
            appSource,
            "if (loggedIn != true)",
            "var mainScope = _services.CreateScope();");

        Assert.Contains("loggedIn is false", nonSuccessBranch, StringComparison.Ordinal);
        Assert.Contains("\"dialog-result-false\"", nonSuccessBranch, StringComparison.Ordinal);
        Assert.Contains("\"dialog-result-null\"", nonSuccessBranch, StringComparison.Ordinal);
        Assert.Contains(
            "Login dialog closed before authentication. reason={loginExitReason}",
            nonSuccessBranch,
            StringComparison.Ordinal);
        Assert.True(
            nonSuccessBranch.IndexOf("AppLogger.Info(", StringComparison.Ordinal) <
            nonSuccessBranch.IndexOf("Shutdown();", StringComparison.Ordinal),
            "로그인 비성공 종료 사유는 앱 종료 호출 전에 기록되어야 합니다.");
    }

    [Fact]
    public void PostLoginSync_ReloadsAgainOnlyWhenBusinessScopeRefreshActuallyRuns()
    {
        var repositoryRoot = FindRepositoryRoot();
        var mainViewModelSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "ViewModels",
            "MainViewModel.cs"));
        var methodBody = ExtractSourceSection(
            mainViewModelSource,
            "public async Task RunPostLoginSyncAsync(",
            "private async Task<bool> ShouldSkipImmediatePostLoginSyncAsync(");

        Assert.Contains("var currentBusinessScopeRefreshAttempted = false;", methodBody, StringComparison.Ordinal);
        Assert.Contains("currentBusinessScopeRefreshAttempted = true;", methodBody, StringComparison.Ordinal);
        var normalizedMethodBody = methodBody.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains(
            "if (currentBusinessScopeRefreshAttempted)\n                    await ReloadAfterPassiveSyncAsync(ct);",
            normalizedMethodBody,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PostLoginSync_PropagatesMainScopeLifetimeCancellationBeforeStartingNewWork()
    {
        var repositoryRoot = FindRepositoryRoot();
        var appSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "App.xaml.cs"));
        var mainViewModelSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "ViewModels",
            "MainViewModel.cs"));
        var mainWindowSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "MainWindow.xaml.cs"));
        var customerFinancialSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "ViewModels",
            "MainViewModel.CustomerFinancials.cs"));
        var appWorkflowBody = ExtractSourceSection(
            appSource,
            "private async Task RunPostLoginSyncThenStartupNotificationsAsync(",
            "private async Task<Task?> StartPostLoginSyncWithPopupAsync(");
        var popupBody = ExtractSourceSection(
            appSource,
            "private async Task<Task?> StartPostLoginSyncWithPopupAsync(",
            "private static async Task CompletePostLoginSyncAndIntegrityAsync(");
        var syncBody = ExtractSourceSection(
            mainViewModelSource,
            "public async Task RunPostLoginSyncAsync(",
            "private async Task<bool> ShouldSkipImmediatePostLoginSyncAsync(");
        var isolatedSyncBody = ExtractSourceSection(
            mainViewModelSource,
            "private async Task<T> RunIsolatedSyncAsync<T>(",
            "private async Task<T> RunIsolatedLocalStateAsync<T>(");
        var invoiceLoadBody = ExtractSourceSection(
            mainViewModelSource,
            "private async Task LoadInvoiceListCoreAsync(",
            "private bool IsCurrentInvoiceListLoad(");
        var initialLoadBody = ExtractSourceSection(
            mainWindowSource,
            "private async Task RunInitialDashboardLoadAsync(",
            "private void StartRuntimeServicesAfterInitialDashboardLoad(");
        var viewModelLoadBody = ExtractSourceSection(
            mainViewModelSource,
            "public async Task LoadAsync(",
            "public void SetInvoiceDefaultDateRange(");
        var financialPreviewRequestBody = ExtractSourceSection(
            customerFinancialSource,
            "private void RequestRefreshCustomerFinancialPreview(",
            "private async Task RunQueuedCustomerFinancialPreviewAsync(");
        var closeAfterDrainBody = ExtractSourceSection(
            appSource,
            "private async Task CloseAfterPostLoginDrainAsync(",
            "private async Task DrainPostLoginWorkAsync(");
        var normalCloseBody = ExtractSourceSection(
            appSource,
            "private async Task HandleMainWindowClosingAsync(",
            "private static Window ShowShutdownSavingPopup(");

        var dashboardAwaitIndex = appWorkflowBody.IndexOf("await initialDashboardLoadTask;", StringComparison.Ordinal);
        var cancellationCheckIndex = appWorkflowBody.IndexOf(
            "mainScopeLifetimeToken.ThrowIfCancellationRequested();",
            dashboardAwaitIndex,
            StringComparison.Ordinal);
        var syncStartIndex = appWorkflowBody.IndexOf(
            "syncTask = await StartPostLoginSyncWithPopupAsync(",
            StringComparison.Ordinal);

        Assert.True(dashboardAwaitIndex >= 0, "초기 대시보드 로드 대기를 찾지 못했습니다.");
        Assert.True(
            cancellationCheckIndex > dashboardAwaitIndex && cancellationCheckIndex < syncStartIndex,
            "종료 취소 토큰은 초기 로드 대기 직후이면서 새 동기화를 시작하기 전에 확인해야 합니다.");
        Assert.Contains("mainScopeLifetimeToken);", appWorkflowBody, StringComparison.Ordinal);
        Assert.Contains(
            "mainScopeLifetimeToken: mainScopeLifetimeToken",
            appSource,
            StringComparison.Ordinal);
        Assert.Contains("ShouldShowPostLoginSyncPopupAsync(mainScopeLifetimeToken)", popupBody, StringComparison.Ordinal);
        Assert.Contains("RunPostLoginSyncAsync(mainScopeLifetimeToken)", popupBody, StringComparison.Ordinal);
        Assert.Contains(
            "catch (OperationCanceledException) when (mainScopeLifetimeToken.IsCancellationRequested)",
            popupBody,
            StringComparison.Ordinal);

        Assert.Contains("CancellationToken ct = default", syncBody, StringComparison.Ordinal);
        Assert.Contains("ShouldSkipImmediatePostLoginSyncAsync(ct)", syncBody, StringComparison.Ordinal);
        Assert.Contains("TrySyncAsync(token)", syncBody, StringComparison.Ordinal);
        Assert.Contains("RefreshCurrentBusinessScopeFromServerAsync(token)", syncBody, StringComparison.Ordinal);
        Assert.Contains("RefreshSharedMirrorFromServerAsync(token)", syncBody, StringComparison.Ordinal);
        Assert.Contains("ReloadAfterPassiveSyncAsync(ct)", syncBody, StringComparison.Ordinal);
        Assert.Contains("BackupNowAsync(ct)", syncBody, StringComparison.Ordinal);
        Assert.Contains("ct: ct", syncBody, StringComparison.Ordinal);
        Assert.DoesNotContain("CancellationToken.None", syncBody, StringComparison.Ordinal);
        Assert.Contains(
            "catch (OperationCanceledException) when (ct.IsCancellationRequested)",
            syncBody,
            StringComparison.Ordinal);

        Assert.Contains("Func<SyncService, CancellationToken, Task<T>> operation", isolatedSyncBody, StringComparison.Ordinal);
        Assert.Contains("operation(sync, ct)", isolatedSyncBody, StringComparison.Ordinal);
        Assert.Contains("}, ct);", isolatedSyncBody, StringComparison.Ordinal);
        Assert.Contains("CreateLinkedTokenSource(cancellationToken)", invoiceLoadBody, StringComparison.Ordinal);
        Assert.Contains("!cancellationToken.IsCancellationRequested", invoiceLoadBody, StringComparison.Ordinal);
        Assert.Contains(
            "RefreshSelectedCustomerFinancialPreviewAsync(ct, dataGateAlreadyHeld: true)",
            invoiceLoadBody,
            StringComparison.Ordinal);
        Assert.Contains("EnsureCompanyProfilesHealthyAsync(mainScopeLifetimeToken)", initialLoadBody, StringComparison.Ordinal);
        Assert.Contains("ResolveServerTodayAsync(mainScopeLifetimeToken)", initialLoadBody, StringComparison.Ordinal);
        Assert.Contains("_vm.LoadAsync(mainScopeLifetimeToken)", initialLoadBody, StringComparison.Ordinal);
        Assert.Contains("if (!mainScopeLifetimeToken.IsCancellationRequested)", initialLoadBody, StringComparison.Ordinal);
        Assert.Contains("LoadCustomersAsync(ct)", viewModelLoadBody, StringComparison.Ordinal);
        Assert.Contains("LoadInvoiceListAsync(ct)", viewModelLoadBody, StringComparison.Ordinal);
        Assert.Contains("_shutdownBackgroundWorkCancellationRequested", financialPreviewRequestBody, StringComparison.Ordinal);
        Assert.Contains("RunQueuedCustomerFinancialPreviewAsync(", financialPreviewRequestBody, StringComparison.Ordinal);
        Assert.Contains("previousCts?.Cancel();", financialPreviewRequestBody, StringComparison.Ordinal);
        Assert.Contains("await mainWin.DrainPendingBackgroundWorkForShutdownAsync();", closeAfterDrainBody, StringComparison.Ordinal);
        Assert.Contains("await mainWin.DrainPendingBackgroundWorkForShutdownAsync();", normalCloseBody, StringComparison.Ordinal);
        Assert.Contains("mainWin.IsShutdownBackgroundWorkCompleted", appSource, StringComparison.Ordinal);
        Assert.Contains("_vm.ResumePendingBackgroundWorkAfterShutdownCanceled();", mainWindowSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ShutdownDrain_TracksOwnerScopeAndWindowWorkBeforeDisposingRuntimeScope()
    {
        var repositoryRoot = FindRepositoryRoot();
        var desktopRoot = Path.Combine(
            repositoryRoot,
            "Desktop",
            "거래플랜.Desktop.App");
        var mainViewModelSource = File.ReadAllText(Path.Combine(
            desktopRoot,
            "ViewModels",
            "MainViewModel.cs"));
        var customerContractsSource = File.ReadAllText(Path.Combine(
            desktopRoot,
            "ViewModels",
            "MainViewModel.CustomerContracts.cs"));
        var updateSource = File.ReadAllText(Path.Combine(
            desktopRoot,
            "ViewModels",
            "MainViewModel.Update.cs"));
        var mainWindowSource = File.ReadAllText(Path.Combine(desktopRoot, "MainWindow.xaml.cs"));
        var appSource = File.ReadAllText(Path.Combine(desktopRoot, "App.xaml.cs"));
        var trackerSource = File.ReadAllText(Path.Combine(
            desktopRoot,
            "Infrastructure",
            "BackgroundTaskTracker.cs"));
        var windowShowHelperSource = File.ReadAllText(Path.Combine(
            desktopRoot,
            "Infrastructure",
            "WindowShowHelper.cs"));
        var uiTaskHelperSource = File.ReadAllText(Path.Combine(
            desktopRoot,
            "Infrastructure",
            "UiTaskHelper.cs"));
        var rentalAssetWindowSource = File.ReadAllText(Path.Combine(
            desktopRoot,
            "Views",
            "RentalAssetWindow.xaml.cs"));
        var periodLedgerWindowSource = File.ReadAllText(Path.Combine(
            desktopRoot,
            "Views",
            "PeriodLedgerWindow.xaml.cs"));
        var tradePrintWindowSource = File.ReadAllText(Path.Combine(
            desktopRoot,
            "Views",
            "TradePrintWindow.xaml.cs"));
        var dialogWindowCloseHelperSource = File.ReadAllText(Path.Combine(
            desktopRoot,
            "Infrastructure",
            "DialogWindowCloseHelper.cs"));
        var runtimeSafetyMonitorSource = File.ReadAllText(Path.Combine(
            desktopRoot,
            "Services",
            "RuntimeSafetyMonitorService.cs"));
        var syncServiceSource = File.ReadAllText(Path.Combine(
            desktopRoot,
            "Services",
            "SyncService.cs"));

        var viewModelCancelBody = ExtractSourceSection(
            mainViewModelSource,
            "public void CancelPendingBackgroundWorkForShutdown()",
            "public async Task DrainPendingBackgroundWorkForShutdownAsync()");
        var viewModelDrainBody = ExtractSourceSection(
            mainViewModelSource,
            "public async Task DrainPendingBackgroundWorkForShutdownAsync()",
            "public bool IsShutdownBackgroundWorkCompleted");
        var windowDrainBody = ExtractSourceSection(
            mainWindowSource,
            "public async Task DrainPendingBackgroundWorkForShutdownAsync()",
            "public bool IsShutdownBackgroundWorkCompleted");
        var runtimeScopeDrainBody = ExtractSourceSection(
            mainWindowSource,
            "private static async Task StopAndDisposeRuntimeSyncScopeAsync(",
            "private void HandleRuntimeSyncStatusChanged(");
        var mainViewModelIsolatedSyncBody = ExtractSourceSection(
            mainViewModelSource,
            "private async Task<T> RunIsolatedSyncAsync<T>(",
            "private async Task<T> RunIsolatedLocalStateAsync<T>(");
        var mainWindowIsolatedSyncBody = ExtractSourceSection(
            mainWindowSource,
            "private async Task<T> RunIsolatedSyncAsync<T>(",
            "private void StartRealtimeRevisionMonitor()");
        var isolatedChildSyncBody = ExtractSourceSection(
            syncServiceSource,
            "private async Task<T> ExecuteUsingIsolatedRuntimeScopeAsync<T>(",
            "private async Task<T> AwaitWithTrackedChangesPreservedAsync<T>(");
        var runtimeSafetyScopeStart = runtimeSafetyMonitorSource.IndexOf(
            "private async Task<T> WithScopedRuntimeServicesAsync<T>(",
            StringComparison.Ordinal);
        Assert.True(runtimeSafetyScopeStart >= 0);
        var runtimeSafetyScopeBody = runtimeSafetyMonitorSource[runtimeSafetyScopeStart..];
        var commandDrainBody = ExtractSourceSection(
            mainWindowSource,
            "private async Task DrainActiveWindowCommandsAsync(",
            "private async Task DrainWindowCommandsAndSecondaryWindowsAsync(");
        var windowCloseCoordinatorBody = ExtractSourceSection(
            mainWindowSource,
            "private async Task DrainWindowCommandsAndSecondaryWindowsAsync(",
            "private async Task CloseActiveModalWindowsForShutdownAsync(");
        var compatibilitySignalBody = ExtractSourceSection(
            appSource,
            "private void HandleDesktopUpgradeRequiredSignal(",
            "private async Task ShowRuntimeCompatibilityRecoveryAsync(");
        var compatibilityRecoveryBody = ExtractSourceSection(
            appSource,
            "private async Task ShowRuntimeCompatibilityRecoveryAsync(",
            "private async Task PrepareActiveMainScopeForCompatibilityExitAsync(");
        var compatibilityPreparationBody = ExtractSourceSection(
            appSource,
            "private async Task PrepareActiveMainScopeForCompatibilityExitAsync(",
            "private async Task RunPreLoginInitializationAsync(");
        var updateShutdownBody = ExtractSourceSection(
            appSource,
            "private void BeginShutdownForUpdate()",
            "internal static IReadOnlyList<string> GetInstallRecoveryStartupRoots(");
        var mainWindowClosingBody = ExtractSourceSection(
            appSource,
            "private void HandleMainWindowClosing(",
            "private void CancelMainScopeBackgroundWork()");
        var activeScopeCoordinatorBody = ExtractSourceSection(
            appSource,
            "private bool TryQueueActiveMainWindowShutdown(",
            "private async Task CloseAfterPostLoginDrainAsync(");
        var periodicTickBody = ExtractSourceSection(
            appSource,
            "private void HandleAutoSaveTimerTick(",
            "private async Task RunPeriodicSaveCycleAsync(");
        var periodicDrainBody = ExtractSourceSection(
            appSource,
            "private async Task DrainPeriodicSaveCycleAsync(",
            "private static async Task StopMainScopeSyncServiceForShutdownAsync(");
        var mainSyncStopBody = ExtractSourceSection(
            appSource,
            "private static async Task StopMainScopeSyncServiceForShutdownAsync(",
            "private async Task HandleMainWindowClosingAsync(");
        var showUserErrorStart = uiTaskHelperSource.IndexOf(
            "private static void ShowUserError(",
            StringComparison.Ordinal);
        Assert.True(showUserErrorStart >= 0);
        var showUserErrorBody = uiTaskHelperSource[showUserErrorStart..];
        var updateCloseBody = ExtractSourceSection(
            appSource,
            "private async Task CloseAfterPostLoginDrainAsync(",
            "private async Task DrainPostLoginWorkAsync(");
        var normalCloseBody = ExtractSourceSection(
            appSource,
            "private async Task HandleMainWindowClosingAsync(",
            "private static Window ShowShutdownSavingPopup(");
        var mainWindowSyncStopBody = ExtractSourceSection(
            mainWindowSource,
            "public Task StopAndDrainMainScopeSyncServiceAsync()",
            "public void EndShutdownProtection()");

        Assert.Contains("private bool _accepting = true;", trackerSource, StringComparison.Ordinal);
        Assert.Contains("private bool _trackingSealed;", trackerSource, StringComparison.Ordinal);
        Assert.Contains("_accepting = false;", trackerSource, StringComparison.Ordinal);
        Assert.Contains("_trackingSealed = true;", trackerSource, StringComparison.Ordinal);
        Assert.Contains("if (_newWorkPaused ||", trackerSource, StringComparison.Ordinal);
        Assert.Contains("_trackingSealed ||", trackerSource, StringComparison.Ordinal);
        Assert.Contains("public Task? TryTrack(Func<Task> operation)", trackerSource, StringComparison.Ordinal);
        Assert.Contains("await Task.WhenAll(activeTasks);", trackerSource, StringComparison.Ordinal);
        Assert.Contains("_ownerScopeBackgroundWork.BeginShutdown();", viewModelCancelBody, StringComparison.Ordinal);
        Assert.DoesNotContain("_customerAutoSaveCtsByCustomer", viewModelCancelBody, StringComparison.Ordinal);
        Assert.Contains(
            "private readonly SemaphoreSlim _customerInlineDataGate;",
            mainViewModelSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "_customerInlineDataGate = local.OwnerScopeDataGate;",
            mainViewModelSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "CustomerInlineEditScopeIdentity CaptureCustomerInlineEditScopeIdentity()",
            mainViewModelSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "CustomerInlineEditPatchMerge.TryMerge(current, patch)",
            mainViewModelSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "ApplyPendingCustomerInlineEditOverlays(customers);",
            mainViewModelSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "CustomerInlineEditPatchMerge.OverlayChangedFields(",
            mainViewModelSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "CustomerInlineEditPatchMerge.RebaseAfterSupersededSave(",
            mainViewModelSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "public async Task RunBusinessDatabaseTransitionAsync(Func<Task> transitionAsync)",
            mainViewModelSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain("CustomerInlineEditSnapshot", mainViewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("snapshot.Customer", mainViewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("GetCustomerAutoSaveExecutionGate", mainViewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("EndCustomerAutoSave", mainViewModelSource, StringComparison.Ordinal);
        Assert.Contains("SnapshotUnresolvedFailures()", viewModelDrainBody, StringComparison.Ordinal);
        Assert.Contains("SnapshotPendingCustomerInlineEdits()", viewModelDrainBody, StringComparison.Ordinal);
        Assert.Contains("TryCancelShutdownToken(_invoicePreviewCts", viewModelCancelBody, StringComparison.Ordinal);
        Assert.Contains("TryCancelShutdownToken(_invoiceListLoadCts", viewModelCancelBody, StringComparison.Ordinal);
        Assert.Contains("TryCancelShutdownToken(\n            _backgroundDesktopUpdateCts", viewModelCancelBody, StringComparison.Ordinal);
        Assert.Contains("_customerFilterDebouncer.CancelAndDrainAsync()", viewModelDrainBody, StringComparison.Ordinal);
        Assert.Contains("_invoiceFilterDebouncer.CancelAndDrainAsync()", viewModelDrainBody, StringComparison.Ordinal);
        Assert.Contains("_ownerScopeBackgroundWork.DrainAsync()", viewModelDrainBody, StringComparison.Ordinal);
        Assert.Contains("_ownerScopeBackgroundWork.TryStart(", mainViewModelSource, StringComparison.Ordinal);
        Assert.Contains("_ownerScopeBackgroundWork.TryStart(", customerContractsSource, StringComparison.Ordinal);
        Assert.Contains("_ownerScopeBackgroundWork.TryStart(", updateSource, StringComparison.Ordinal);

        Assert.Contains("_runtimeSyncDrainTask", windowDrainBody, StringComparison.Ordinal);
        Assert.Contains("_windowCommandDrainTask", windowDrainBody, StringComparison.Ordinal);
        Assert.Contains("_windowBackgroundWork.DrainAsync()", windowDrainBody, StringComparison.Ordinal);
        Assert.Contains("DisableApplicationWindowsForShutdown,", mainWindowSource, StringComparison.Ordinal);
        Assert.Contains("DrainActiveWindowCommandsAsync(", mainWindowSource, StringComparison.Ordinal);
        Assert.Contains("CloseSecondaryWindowsForShutdownAsync(", mainWindowSource, StringComparison.Ordinal);
        var modalCloseIndex = windowCloseCoordinatorBody.IndexOf(
            "await CloseActiveModalWindowsForShutdownAsync(",
            StringComparison.Ordinal);
        var commandDrainIndex = windowCloseCoordinatorBody.IndexOf(
            "await DrainActiveWindowCommandsAsync(",
            StringComparison.Ordinal);
        var secondaryCloseIndex = windowCloseCoordinatorBody.IndexOf(
            "await CloseSecondaryWindowsForShutdownAsync(",
            StringComparison.Ordinal);
        Assert.True(
            modalCloseIndex >= 0 &&
            commandDrainIndex > modalCloseIndex &&
            secondaryCloseIndex > commandDrainIndex);
        Assert.Contains(
            "completion.WaitAsync(TimeSpan.FromMinutes(2))",
            commandDrainBody,
            StringComparison.Ordinal);
        Assert.Contains("if (waitForCompletionWithoutDeadline)", commandDrainBody, StringComparison.Ordinal);
        Assert.Contains("await completion;", commandDrainBody, StringComparison.Ordinal);
        Assert.Contains("catch (TimeoutException ex)", commandDrainBody, StringComparison.Ordinal);
        Assert.Contains("closed.Task.WaitAsync(TimeSpan.FromMinutes(2))", mainWindowSource, StringComparison.Ordinal);
        Assert.Contains("OrderByDescending(GetWindowOwnershipDepth)", mainWindowSource, StringComparison.Ordinal);
        Assert.Contains("typeof(IAsyncRelayCommand)", mainWindowSource, StringComparison.Ordinal);
        var windowCommandCompletionIndex = windowDrainBody.IndexOf(
            "_windowCommandDrainTask",
            StringComparison.Ordinal);
        var sealedWindowTrackerDrainIndex = windowDrainBody.IndexOf(
            "await _windowBackgroundWork.DrainAsync();",
            StringComparison.Ordinal);
        Assert.True(
            windowCommandCompletionIndex >= 0 &&
            sealedWindowTrackerDrainIndex > windowCommandCompletionIndex,
            "The window tracker must stay registration-open until modal/secondary Closed callbacks are scheduled.");
        Assert.Contains("mainWindowLifetime.RunTrackedWindowOperationAsync(operation)", uiTaskHelperSource, StringComparison.Ordinal);
        Assert.Contains("mainWindowLifetime.TryTrackWindowObservation(", uiTaskHelperSource, StringComparison.Ordinal);
        Assert.Contains("() => ObserveAsync(task, category, operation, onError, onCompleted)", uiTaskHelperSource, StringComparison.Ordinal);
        Assert.Contains("ObserveAfterTrackingSealedAsync(", uiTaskHelperSource, StringComparison.Ordinal);
        Assert.Contains("Func<Task> taskFactory", uiTaskHelperSource, StringComparison.Ordinal);
        Assert.Contains("() => StartAndObserveAsync(", uiTaskHelperSource, StringComparison.Ordinal);
        Assert.Contains("if (trackedTask is null)", uiTaskHelperSource, StringComparison.Ordinal);
        Assert.Contains("Do not invoke the", uiTaskHelperSource, StringComparison.Ordinal);
        Assert.DoesNotContain("TrackExistingWindowTask", uiTaskHelperSource, StringComparison.Ordinal);
        Assert.Contains("UiTaskHelper.Forget(", compatibilitySignalBody, StringComparison.Ordinal);
        Assert.Contains("ShowRuntimeCompatibilityRecoveryAsync(", compatibilitySignalBody, StringComparison.Ordinal);
        Assert.Contains("trackForWindowLifetime: false", compatibilitySignalBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Shutdown(1)", compatibilitySignalBody, StringComparison.Ordinal);
        Assert.Contains("var safeToExit = false;", compatibilityRecoveryBody, StringComparison.Ordinal);
        var compatibilityTryIndex = compatibilityRecoveryBody.IndexOf("try", StringComparison.Ordinal);
        var compatibilityCancellationIndex = compatibilityRecoveryBody.IndexOf(
            "CancelMainScopeBackgroundWork();",
            StringComparison.Ordinal);
        Assert.True(
            compatibilityTryIndex >= 0 &&
            compatibilityCancellationIndex > compatibilityTryIndex,
            "Runtime compatibility setup and cancellation must remain inside the protected try block.");
        Assert.Contains("DialogWindowCloseHelper.ShowDialog(", compatibilityRecoveryBody, StringComparison.Ordinal);
        Assert.Contains("allowDuringShutdown: true", compatibilityRecoveryBody, StringComparison.Ordinal);
        Assert.Contains(
            "public static bool? ShowDialog(Window window, bool allowDuringShutdown = false)",
            dialogWindowCloseHelperSource,
            StringComparison.Ordinal);
        Assert.Contains("if (!allowDuringShutdown &&", dialogWindowCloseHelperSource, StringComparison.Ordinal);
        Assert.Contains("mainWindow.IsShutdownProtectionActive", dialogWindowCloseHelperSource, StringComparison.Ordinal);
        Assert.Contains("return false;", dialogWindowCloseHelperSource, StringComparison.Ordinal);
        Assert.Contains("ActiveDialogs.Add(window);", dialogWindowCloseHelperSource, StringComparison.Ordinal);
        Assert.Contains("ActiveDialogs.Remove(window);", dialogWindowCloseHelperSource, StringComparison.Ordinal);
        Assert.Contains("SnapshotActiveDialogs()", dialogWindowCloseHelperSource, StringComparison.Ordinal);
        Assert.Contains("DialogWindowCloseHelper.SnapshotActiveDialogs()", mainWindowSource, StringComparison.Ordinal);
        var shutdownErrorGateIndex = showUserErrorBody.IndexOf(
            "IsShutdownProtectionActive()",
            StringComparison.Ordinal);
        var shutdownErrorReturnIndex = showUserErrorBody.IndexOf(
            "return;",
            shutdownErrorGateIndex,
            StringComparison.Ordinal);
        var errorMessageBoxIndex = showUserErrorBody.IndexOf("MessageBox.Show(", StringComparison.Ordinal);
        Assert.True(
            shutdownErrorGateIndex >= 0 &&
            shutdownErrorReturnIndex > shutdownErrorGateIndex &&
            errorMessageBoxIndex > shutdownErrorReturnIndex,
            "Shutdown-protected observation callbacks must not open a new error modal.");
        Assert.Contains(
            "if (_shutdownInProgress || _updateShutdownRequested || _restartToLoginRequested)",
            periodicTickBody,
            StringComparison.Ordinal);
        Assert.Contains("RunPeriodicSaveCycleAsync(sp, mainVm)", periodicTickBody, StringComparison.Ordinal);
        Assert.Contains("_saveCycleLock.WaitAsync(TimeSpan.FromMinutes(2))", periodicDrainBody, StringComparison.Ordinal);
        Assert.Contains("if (waitForCompletionWithoutDeadline)", periodicDrainBody, StringComparison.Ordinal);
        Assert.Contains("await _saveCycleLock.WaitAsync();", periodicDrainBody, StringComparison.Ordinal);
        Assert.Contains("throw new TimeoutException(", periodicDrainBody, StringComparison.Ordinal);
        Assert.Contains("private void Window_Closed", rentalAssetWindowSource, StringComparison.Ordinal);
        Assert.Contains("UiTaskHelper.Forget(", rentalAssetWindowSource, StringComparison.Ordinal);
        Assert.DoesNotContain("async void LedgerRowsDataGrid_MouseDoubleClick", periodLedgerWindowSource, StringComparison.Ordinal);
        Assert.DoesNotContain("async void DetailItemsDataGrid_MouseDoubleClick", periodLedgerWindowSource, StringComparison.Ordinal);
        Assert.Contains("UiTaskHelper.Run(", periodLedgerWindowSource, StringComparison.Ordinal);
        Assert.DoesNotContain("async void OnRefreshPrintersClick", tradePrintWindowSource, StringComparison.Ordinal);
        Assert.Contains(
            "mainWindowLifetime.RunTrackedWindowOperationAsync(operation)",
            windowShowHelperSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "() => RunWithinMainWindowLifetimeAsync(loadAsync)",
            windowShowHelperSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "() => RunWithinMainWindowLifetimeAsync(closedAsync)",
            windowShowHelperSource,
            StringComparison.Ordinal);
        Assert.Contains("TryQueueActiveMainWindowShutdown()", updateShutdownBody, StringComparison.Ordinal);
        Assert.Contains("fatalStartup: true", appSource, StringComparison.Ordinal);
        Assert.Contains("waitForCompletionWithoutDeadline: true", appSource, StringComparison.Ordinal);
        Assert.Contains("_fatalStartupShutdownRequested", mainWindowClosingBody, StringComparison.Ordinal);
        Assert.Contains("if (safeToExit)", compatibilityRecoveryBody, StringComparison.Ordinal);
        Assert.Contains("waitForCompletionWithoutDeadline: true", compatibilityRecoveryBody, StringComparison.Ordinal);
        Assert.Contains("_mainWindowShutdownCoordinatorLock.WaitAsync()", compatibilityPreparationBody, StringComparison.Ordinal);
        Assert.Contains("waitForCompletionWithoutDeadline: true", compatibilityPreparationBody, StringComparison.Ordinal);
        Assert.Contains("_activeMainScopeServiceProvider is null", activeScopeCoordinatorBody, StringComparison.Ordinal);
        Assert.Contains("_activeMainViewModel is null", activeScopeCoordinatorBody, StringComparison.Ordinal);
        Assert.Contains("mainWin.BeginShutdownProtection(", activeScopeCoordinatorBody, StringComparison.Ordinal);
        Assert.Contains("QueueCloseAfterPostLoginDrain(", activeScopeCoordinatorBody, StringComparison.Ordinal);
        var coordinatedCloseGateIndex = mainWindowClosingBody.IndexOf(
            "if (_coordinatedMainWindowCloseReady)",
            StringComparison.Ordinal);
        var coordinatedCloseQueueIndex = mainWindowClosingBody.IndexOf(
            "QueueCloseAfterPostLoginDrain(mainWin, sp, mainVm);",
            StringComparison.Ordinal);
        Assert.True(
            coordinatedCloseGateIndex >= 0 && coordinatedCloseQueueIndex > coordinatedCloseGateIndex);
        Assert.Contains("_coordinatedMainWindowCloseReady = true;", updateCloseBody, StringComparison.Ordinal);
        Assert.Contains("_coordinatedMainWindowCloseReady = false;", updateCloseBody, StringComparison.Ordinal);
        Assert.Contains("_mainWindowShutdownCoordinatorLock.WaitAsync()", updateCloseBody, StringComparison.Ordinal);
        Assert.Contains("_mainWindowShutdownCoordinatorLock.WaitAsync()", normalCloseBody, StringComparison.Ordinal);
        Assert.Contains("mainSyncStopAttempted = true;", updateCloseBody, StringComparison.Ordinal);
        Assert.Contains("mainSyncStopAttempted = true;", normalCloseBody, StringComparison.Ordinal);
        Assert.Contains("ResumePostLoginWorkAfterCanceledShutdown(", updateCloseBody, StringComparison.Ordinal);
        Assert.Contains("ResumePostLoginWorkAfterCanceledShutdown(", normalCloseBody, StringComparison.Ordinal);
        Assert.Contains("_coordinatedMainWindowCloseReady = true;", normalCloseBody, StringComparison.Ordinal);
        Assert.Contains("if (!_coordinatedMainWindowCloseReady)", mainWindowClosingBody, StringComparison.Ordinal);
        Assert.Contains("args.Cancel = true;", mainWindowClosingBody, StringComparison.Ordinal);
        Assert.Contains("StopAndDrainMainScopeSyncServiceCoreAsync()", mainWindowSyncStopBody, StringComparison.Ordinal);
        Assert.Contains("await Task.Yield();", mainWindowSyncStopBody, StringComparison.Ordinal);
        var publishedDrainIndex = mainWindowSyncStopBody.IndexOf(
            "_mainScopeSyncDrainTask = StopAndDrainMainScopeSyncServiceCoreAsync();",
            StringComparison.Ordinal);
        var publishedStopFlagIndex = mainWindowSyncStopBody.IndexOf(
            "_mainScopeSyncStopRequested = true;",
            StringComparison.Ordinal);
        Assert.True(
            publishedDrainIndex >= 0 && publishedStopFlagIndex > publishedDrainIndex,
            "The real sync drain Task must be published before the completed-state flag.");
        AssertScopedSyncDrain(
            mainViewModelIsolatedSyncBody,
            "using var scope = _serviceScopeFactory.CreateScope();",
            "await sync.StopAndDrainAsync().ConfigureAwait(false);");
        AssertScopedSyncDrain(
            mainWindowIsolatedSyncBody,
            "using var scope = _serviceScopeFactory.CreateScope();",
            "await sync.StopAndDrainAsync().ConfigureAwait(false);");
        AssertScopedSyncDrain(
            runtimeSafetyScopeBody,
            "await using var scope = _scopeFactory.CreateAsyncScope();",
            "await sync.StopAndDrainAsync().ConfigureAwait(false);");
        AssertScopedSyncDrain(
            isolatedChildSyncBody,
            "await using var scope = _scopeFactory.CreateAsyncScope();",
            "await child.StopAndDrainAsync().ConfigureAwait(false);");
        var stopIndex = runtimeScopeDrainBody.IndexOf("await sync.StopAndDrainAsync();", StringComparison.Ordinal);
        var disposeIndex = runtimeScopeDrainBody.IndexOf("scope?.Dispose();", StringComparison.Ordinal);
        Assert.True(stopIndex >= 0 && disposeIndex > stopIndex);
        Assert.DoesNotContain(".Wait(", runtimeScopeDrainBody, StringComparison.Ordinal);
        Assert.DoesNotContain(".Result", runtimeScopeDrainBody, StringComparison.Ordinal);

        var postLoginDrainIndex = updateCloseBody.IndexOf("await DrainPostLoginWorkAsync();", StringComparison.Ordinal);
        var windowDrainIndex = updateCloseBody.IndexOf("await mainWin.DrainPendingBackgroundWorkForShutdownAsync();", StringComparison.Ordinal);
        var saveDrainIndex = updateCloseBody.IndexOf("await DrainPeriodicSaveCycleAsync(", StringComparison.Ordinal);
        var finalSaveIndex = updateCloseBody.IndexOf(
            "await RunSaveCycleAsync(sp, mainVm, isShutdown: true)",
            StringComparison.Ordinal);
        var mainSyncDrainIndex = updateCloseBody.IndexOf(
            "await StopMainScopeSyncServiceForShutdownAsync(mainWin);",
            StringComparison.Ordinal);
        var closeIndex = updateCloseBody.IndexOf("mainWin.Close();", StringComparison.Ordinal);
        Assert.True(
            postLoginDrainIndex >= 0 &&
            windowDrainIndex > postLoginDrainIndex &&
            saveDrainIndex > windowDrainIndex &&
            finalSaveIndex > saveDrainIndex &&
            mainSyncDrainIndex > finalSaveIndex &&
            closeIndex > mainSyncDrainIndex);
        Assert.Contains("result.RemainingDirtyCount > 0", updateCloseBody, StringComparison.Ordinal);
        Assert.Contains("mainWin.EndShutdownProtection();", updateCloseBody, StringComparison.Ordinal);
        Assert.Contains("StartAutoSaveTimer(sp, mainVm);", updateCloseBody, StringComparison.Ordinal);
        Assert.Contains("mainWin.IsMainScopeSyncDrainCompleted", appSource, StringComparison.Ordinal);
        Assert.Contains("await mainWin.StopAndDrainMainScopeSyncServiceAsync();", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("catch", mainSyncStopBody, StringComparison.Ordinal);
        Assert.Contains("while (true)", syncServiceSource, StringComparison.Ordinal);
        Assert.Contains("faulted generation is", syncServiceSource, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShutdownCommandSnapshot_FindsActiveAsyncRelayCommandExecution()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new AsyncRelayCommand(() => release.Task);
        var execution = command.ExecuteAsync(null);
        var holder = new AsyncCommandHolder(command);

        var activeTasks = MainWindow.GetActiveAsyncCommandTasks(holder).ToArray();

        var capturedTask = Assert.Single(activeTasks);
        Assert.Same(command.ExecutionTask, capturedTask);
        Assert.Empty(MainWindow.GetActiveAsyncCommandTasks(holder, nameof(AsyncCommandHolder.ActiveCommand)));
        release.SetResult();
        await execution;
        Assert.Empty(MainWindow.GetActiveAsyncCommandTasks(holder));
    }

    [Fact]
    public async Task PostLoginSync_PreCanceledLifetimeStopsBeforeAnyServiceWork()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new LocalDbContext(options);
        var session = new SessionState();
        var dispatcher = new SyncRequestDispatcher();
        var local = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
        var rental = new RentalStateService(db, local);
        var diagnostics = new SyncDiagnosticsService(session);
        var api = new ErpApiClient(
            new HttpClient { BaseAddress = new Uri("http://localhost/") },
            session);
        using var sync = new SyncService(db, local, rental, api, session, dispatcher, diagnostics);
        var viewModel = new MainViewModel(
            local,
            sync,
            new BackupService(),
            rental,
            diagnostics,
            api,
            session);
        using var lifetimeCts = new CancellationTokenSource();
        lifetimeCts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => viewModel.RunPostLoginSyncAsync(lifetimeCts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => viewModel.LoadAsync(lifetimeCts.Token));
    }

    [Fact]
    public void PostLoginIntegrity_SerializesMainScopeCacheOnDispatcherAndDrainsBeforeDispose()
    {
        var repositoryRoot = FindRepositoryRoot();
        var appSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "App.xaml.cs"));
        var completionBody = ExtractSourceSection(
            appSource,
            "private static async Task CompletePostLoginSyncAndIntegrityAsync(",
            "private static async Task PrewarmAdministrativeBusinessCachesOnDispatcherAsync(");
        var prewarmBody = ExtractSourceSection(
            appSource,
            "private static async Task PrewarmAdministrativeBusinessCachesOnDispatcherAsync(",
            "private static Task<string?> RunPostLoginIntegrityChecksInBackgroundAsync(");
        var backgroundBody = ExtractSourceSection(
            appSource,
            "private static Task<string?> RunPostLoginIntegrityChecksInBackgroundAsync(",
            "private static void CloseStartupSyncPopup(");

        Assert.DoesNotContain("ReloadAfterPassiveSyncAsync", completionBody, StringComparison.Ordinal);
        Assert.DoesNotContain("RunDataIntegrityScanAndPromptAsync", completionBody, StringComparison.Ordinal);
        Assert.Contains("PrewarmAdministrativeBusinessCachesOnDispatcherAsync(", completionBody, StringComparison.Ordinal);
        Assert.Contains("RunPostLoginIntegrityChecksInBackgroundAsync(", completionBody, StringComparison.Ordinal);
        Assert.True(
            completionBody.IndexOf("PrewarmAdministrativeBusinessCachesOnDispatcherAsync(", StringComparison.Ordinal) <
            completionBody.IndexOf("RunPostLoginIntegrityChecksInBackgroundAsync(", StringComparison.Ordinal));
        Assert.Contains("mainWin.Dispatcher.CheckAccess()", prewarmBody, StringComparison.Ordinal);
        Assert.Contains("mainWin.Dispatcher.InvokeAsync(", prewarmBody, StringComparison.Ordinal);
        Assert.Contains(
            "serviceProvider.GetRequiredService<SyncService>();",
            prewarmBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "mainScopeSyncService.EnsureAdministrativeBusinessCachesAsync(",
            prewarmBody,
            StringComparison.Ordinal);
        Assert.Contains("Task.Run(async () =>", backgroundBody, StringComparison.Ordinal);
        Assert.Contains("scopeFactory.CreateAsyncScope()", backgroundBody, StringComparison.Ordinal);
        Assert.Contains("scopedProvider.GetRequiredService<DataIntegrityIssueService>()", backgroundBody, StringComparison.Ordinal);
        Assert.DoesNotContain("SyncService", backgroundBody, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsureAdministrativeBusinessCachesAsync", backgroundBody, StringComparison.Ordinal);
        Assert.Contains("notices={result.PassiveStartupNoticeIssueCount:N0}", backgroundBody, StringComparison.Ordinal);
        Assert.Contains("mainWin.Dispatcher.InvokeAsync", completionBody, StringComparison.Ordinal);
        Assert.Contains("_postLoginCompletionTask =", appSource, StringComparison.Ordinal);
        Assert.Contains("CancelMainScopeBackgroundWork();", appSource, StringComparison.Ordinal);
        Assert.Contains("await DrainPostLoginWorkAsync();", appSource, StringComparison.Ordinal);
        Assert.Contains(
            "if (!mainScopeDisposed && mainScopeBackgroundWorkCompleted)",
            appSource,
            StringComparison.Ordinal);
        Assert.Contains("mainWin.IsShutdownBackgroundWorkCompleted", appSource, StringComparison.Ordinal);
        Assert.Contains("mainWin.IsMainScopeSyncDrainCompleted", appSource, StringComparison.Ordinal);
    }

    private sealed class AsyncCommandHolder(IAsyncRelayCommand activeCommand)
    {
        public IAsyncRelayCommand ActiveCommand { get; } = activeCommand;
    }

    private static void AssertScopedSyncDrain(
        string sourceSection,
        string scopeCreationMarker,
        string drainMarker)
    {
        var scopeCreationIndex = sourceSection.IndexOf(scopeCreationMarker, StringComparison.Ordinal);
        var finallyIndex = sourceSection.IndexOf("finally", scopeCreationIndex, StringComparison.Ordinal);
        var drainIndex = sourceSection.IndexOf(drainMarker, StringComparison.Ordinal);

        Assert.True(
            scopeCreationIndex >= 0 && finallyIndex > scopeCreationIndex && drainIndex > finallyIndex,
            $"Isolated SyncService scope must await StopAndDrainAsync before disposal: {scopeCreationMarker}");
    }

    private static string ExtractSourceSection(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"시작 마커를 찾지 못했습니다: {startMarker}");

        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"종료 마커를 찾지 못했습니다: {endMarker}");
        return source[start..end];
    }

    private static string FindRepositoryRoot()
    {
        foreach (var seedPath in new[]
                 {
                     Environment.GetEnvironmentVariable("GEORAEPLAN_REPOSITORY_ROOT"),
                     AppContext.BaseDirectory,
                     Environment.CurrentDirectory
                 }.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var current = new DirectoryInfo(seedPath!);
            while (current is not null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, "Desktop")) &&
                    Directory.Exists(Path.Combine(current.FullName, "Tests")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }
        }

        throw new DirectoryNotFoundException("거래플랜 저장소 루트를 찾지 못했습니다.");
    }
}
