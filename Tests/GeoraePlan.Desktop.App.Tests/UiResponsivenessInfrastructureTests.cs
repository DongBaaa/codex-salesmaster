using System.Text.RegularExpressions;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class UiResponsivenessInfrastructureTests
{
    [Fact]
    public void WindowShowHelper_ShowsAndRendersWindowBeforeStartingDeferredLoad()
    {
        var source = ReadDesktopSource("Infrastructure", "WindowShowHelper.cs")
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        var showIndex = source.IndexOf("ShowModeless(window);", StringComparison.Ordinal);
        var loadingStateIndex = source.IndexOf("ApplyDeferredLoadState();", StringComparison.Ordinal);
        var startLoadIndex = source.IndexOf("_ = StartLoadAsync();", StringComparison.Ordinal);
        var startLoadBody = ExtractBlock(
            source,
            "async Task StartLoadAsync()",
            "if (closedAsync is not null)");
        var idleYieldIndex = startLoadBody.IndexOf("await window.Dispatcher.InvokeAsync(", StringComparison.Ordinal);
        var measuredLoadIndex = startLoadBody.IndexOf("await OperationTiming.MeasureAsync(", StringComparison.Ordinal);

        Assert.True(showIndex >= 0, "The deferred window must be shown before loading starts.");
        Assert.True(loadingStateIndex > showIndex, "The loading state must be applied immediately after the window is shown.");
        Assert.True(startLoadIndex > loadingStateIndex, "The deferred load started before its re-entry guard was applied.");
        Assert.True(idleYieldIndex >= 0, "The deferred load must yield until the window can render.");
        Assert.True(measuredLoadIndex > idleYieldIndex, "The data load started before the ApplicationIdle render yield.");
        Assert.Contains("DispatcherPriority.ApplicationIdle", startLoadBody, StringComparison.Ordinal);
        Assert.Contains("if (!window.IsLoaded || !window.IsVisible)", startLoadBody, StringComparison.Ordinal);
        Assert.Contains("window.IsEnabled = false;", source, StringComparison.Ordinal);
        Assert.Contains("window.Cursor = Cursors.Wait;", source, StringComparison.Ordinal);
        Assert.Contains("finally", startLoadBody, StringComparison.Ordinal);
        Assert.Contains("window.IsEnabled = wasEnabled;", source, StringComparison.Ordinal);
        Assert.Contains("window.Cursor = previousCursor;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Mouse.OverrideCursor", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvironmentSettings_StaysClosableWhileItsBusyBoundContentIsLocked()
    {
        var helper = ReadDesktopSource("Infrastructure", "WindowShowHelper.cs");
        var mainWindow = ReadDesktopSource("MainWindow.xaml.cs");
        var environmentViewModel = ReadDesktopSource("ViewModels", "EnvironmentSettingsViewModel.cs");
        var environmentWindow = ReadDesktopSource("Views", "EnvironmentSettingsWindow.xaml");
        var environmentWindowCode = ReadDesktopSource("Views", "EnvironmentSettingsWindow.xaml.cs");
        var openBody = ExtractBlock(
            mainWindow,
            "private async Task OpenEnvironmentSettingsWindowAsync(",
            "private async Task RunBusinessDatabaseTransitionAsync(");

        Assert.Contains("bool blockWindowDuringLoad = true", helper, StringComparison.Ordinal);
        Assert.Contains("if (blockWindowDuringLoad)", helper, StringComparison.Ordinal);
        Assert.Contains("window.IsEnabled = false;", helper, StringComparison.Ordinal);
        Assert.Contains("window.IsEnabled = wasEnabled;", helper, StringComparison.Ordinal);
        Assert.Contains("window.Cursor = Cursors.Wait;", helper, StringComparison.Ordinal);
        Assert.Contains("window.Cursor = previousCursor;", helper, StringComparison.Ordinal);

        Assert.Contains("public bool CanInteract => !IsBusy;", environmentViewModel, StringComparison.Ordinal);
        Assert.Contains("public bool IsCloseBlocked => IsBusy && !IsInitialLoadInProgress;", environmentViewModel, StringComparison.Ordinal);
        Assert.Contains("IsInitialLoadInProgress = true;", environmentViewModel, StringComparison.Ordinal);
        Assert.Contains("IsInitialLoadInProgress = false;", environmentViewModel, StringComparison.Ordinal);
        Assert.Contains("if (!_viewModel.IsCloseBlocked)", environmentWindowCode, StringComparison.Ordinal);
        Assert.DoesNotContain("if (!_viewModel.IsBusy)", environmentWindowCode, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding CanInteract}\"", environmentWindow, StringComparison.Ordinal);
        Assert.Contains("blockWindowDuringLoad: false", openBody, StringComparison.Ordinal);
        Assert.Contains("() => vm.InitializeAsync()", openBody, StringComparison.Ordinal);

        var rentalOpenBody = ExtractBlock(
            mainWindow,
            "private async Task OpenRentalBillingWindowAsync(",
            "private async Task OpenRentalAssetWindowAsync(");
        Assert.DoesNotContain("blockWindowDuringLoad: false", rentalOpenBody, StringComparison.Ordinal);
    }

    [Fact]
    public void DataGridAutoFit_KeepsVirtualizationAndBoundsExpensiveTextMeasurement()
    {
        var source = ReadDesktopSource("Infrastructure", "DataGridAutoColumnWidthService.cs")
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var desiredWidthBody = ExtractBlock(
            source,
            "private static double ResolveDesiredColumnWidth(",
            "private static double EstimateTextWidthUnits(");

        Assert.Contains("private const int MaxAutoFitSampleCount = 100;", source, StringComparison.Ordinal);
        Assert.Contains("grid.EnableColumnVirtualization = true;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("grid.EnableColumnVirtualization = false;", source, StringComparison.Ordinal);
        Assert.Contains("if (sampledItemCount++ >= MaxAutoFitSampleCount)", desiredWidthBody, StringComparison.Ordinal);
        Assert.Contains("MeasureText(grid, widestCandidate, grid.FontWeight)", desiredWidthBody, StringComparison.Ordinal);
        Assert.Equal(
            2,
            Regex.Matches(desiredWidthBody, "MeasureText\\(grid,", RegexOptions.CultureInvariant).Count);
    }

    [Fact]
    public void DataGridAutoFit_CoalescesRepeatedSchedulingRequests()
    {
        var source = ReadDesktopSource("Infrastructure", "DataGridAutoColumnWidthService.cs")
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var scheduleBody = ExtractBlock(
            source,
            "private static void ScheduleAutoFit(DataGrid grid)",
            "private static void ApplyAutoFit(DataGrid grid)");

        var pendingGuardIndex = scheduleBody.IndexOf("if ((bool)grid.GetValue(PendingAutoFitProperty))", StringComparison.Ordinal);
        var markPendingIndex = scheduleBody.IndexOf("grid.SetValue(PendingAutoFitProperty, true);", StringComparison.Ordinal);
        var dispatchIndex = scheduleBody.IndexOf("grid.Dispatcher.BeginInvoke(", StringComparison.Ordinal);
        var clearPendingIndex = scheduleBody.IndexOf("grid.SetValue(PendingAutoFitProperty, false);", StringComparison.Ordinal);

        Assert.True(pendingGuardIndex >= 0);
        Assert.True(markPendingIndex > pendingGuardIndex);
        Assert.True(dispatchIndex > markPendingIndex);
        Assert.True(clearPendingIndex > dispatchIndex);
    }

    [Fact]
    public void DataGridAutoFit_AppliesHeaderMinimumsBeforeDeferredContentMeasurement()
    {
        var source = ReadDesktopSource("Infrastructure", "DataGridAutoColumnWidthService.cs")
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var loadedBody = ExtractBlock(
            source,
            "private static void OnDataGridLoaded(object sender, RoutedEventArgs e)",
            "private static void OnDataGridUnloaded(object sender, RoutedEventArgs e)");

        var trackIndex = loadedBody.IndexOf("TrackItemsSource(grid);", StringComparison.Ordinal);
        var immediateApplyIndex = loadedBody.IndexOf("ApplyAutoFit(grid);", StringComparison.Ordinal);
        var deferredApplyIndex = loadedBody.IndexOf("ScheduleAutoFit(grid);", StringComparison.Ordinal);

        Assert.True(trackIndex >= 0);
        Assert.True(
            immediateApplyIndex > trackIndex,
            "표가 처음 보일 때 머리글 최소폭을 즉시 적용해야 합니다.");
        Assert.True(
            deferredApplyIndex > immediateApplyIndex,
            "초기 최소폭 적용 뒤 데이터 기반 재측정을 예약해야 합니다.");
    }

    [Fact]
    public void CustomerContractGrid_DeclaresNonCompressibleColumnMinimums()
    {
        var source = ReadDesktopSource("Views", "CustomerEditWindow.xaml")
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var contractGrid = ExtractBlock(
            source,
            "<DataGrid Grid.Row=\"0\"",
            "<StackPanel Grid.Row=\"1\"");

        Assert.Contains("ScrollViewer.HorizontalScrollBarVisibility=\"Auto\"", contractGrid, StringComparison.Ordinal);
        Assert.Contains("Header=\"대표\" Binding=\"{Binding IsPrimary}\" Width=\"60\" MinWidth=\"60\"", contractGrid, StringComparison.Ordinal);
        Assert.Contains("Header=\"상태\" Width=\"78\" MinWidth=\"78\"", contractGrid, StringComparison.Ordinal);
        Assert.Contains("Header=\"구분\" Binding=\"{Binding ContractType}\" Width=\"110\" MinWidth=\"78\"", contractGrid, StringComparison.Ordinal);
        Assert.Contains("Header=\"체결일\" Binding=\"{Binding SignedDate}\" Width=\"112\" MinWidth=\"112\"", contractGrid, StringComparison.Ordinal);
        Assert.Contains("Header=\"만료일\" Binding=\"{Binding ExpireDate}\" Width=\"112\" MinWidth=\"112\"", contractGrid, StringComparison.Ordinal);
        Assert.Contains("Header=\"파일명\" Binding=\"{Binding FileName}\" Width=\"190\" MinWidth=\"190\"", contractGrid, StringComparison.Ordinal);
        Assert.Contains("Header=\"용량\" Binding=\"{Binding FileSize, StringFormat={}{0:N0} B}\" Width=\"100\" MinWidth=\"100\"", contractGrid, StringComparison.Ordinal);
        Assert.Contains("Header=\"등록자\" Binding=\"{Binding UploadedByUsername}\" Width=\"90\" MinWidth=\"90\"", contractGrid, StringComparison.Ordinal);
        Assert.Contains("Header=\"등록시각\" Binding=\"{Binding UploadedAtUtc, StringFormat={}{0:yyyy-MM-dd HH:mm}}\" Width=\"145\" MinWidth=\"145\"", contractGrid, StringComparison.Ordinal);
        Assert.Contains("Header=\"메모\" Binding=\"{Binding Description}\" Width=\"140\" MinWidth=\"140\"", contractGrid, StringComparison.Ordinal);
    }

    [Fact]
    public void RealtimeSync_IsThrottledAndSkipsUnchangedUiReloads()
    {
        var mainWindow = ReadDesktopSource("MainWindow.xaml.cs")
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var syncService = ReadDesktopSource("Services", "SyncService.cs");

        Assert.Contains("RealtimeRefreshMinInterval = TimeSpan.FromSeconds(30)", mainWindow, StringComparison.Ordinal);
        Assert.Contains("PassiveIntegrityScanMinInterval = TimeSpan.FromMinutes(5)", mainWindow, StringComparison.Ordinal);
        Assert.Contains("sync.LastPullChangeCount", mainWindow, StringComparison.Ordinal);
        Assert.Contains("if (syncOutcome.PulledChangeCount > 0)", mainWindow, StringComparison.Ordinal);
        Assert.Contains("LastPullChangeCount = CountPullChanges(pull);", syncService, StringComparison.Ordinal);
    }

    [Fact]
    public void IsolatedSync_RunsItsScopeAndWorkOffTheUiThread()
    {
        var mainWindow = ReadDesktopSource("MainWindow.xaml.cs");
        var mainViewModel = ReadDesktopSource("ViewModels", "MainViewModel.cs");

        var windowSyncBody = ExtractBlock(
            mainWindow,
            "private async Task<T> RunIsolatedSyncAsync<T>",
            "private void StartRealtimeRevisionMonitor()");
        var viewModelSyncBody = ExtractBlock(
            mainViewModel,
            "private async Task<T> RunIsolatedSyncAsync<T>",
            "private async Task ApplySyncStatusAsync");

        Assert.Contains("return await Task.Run(async () =>", windowSyncBody, StringComparison.Ordinal);
        Assert.Contains("_serviceScopeFactory.CreateScope()", windowSyncBody, StringComparison.Ordinal);
        Assert.Contains("return await Task.Run(async () =>", viewModelSyncBody, StringComparison.Ordinal);
        Assert.Contains("_serviceScopeFactory.CreateScope()", viewModelSyncBody, StringComparison.Ordinal);
    }

    [Fact]
    public void RealtimeRevisionMonitor_UsesIsolatedLocalStateScope()
    {
        var mainWindow = ReadDesktopSource("MainWindow.xaml.cs");
        var instanceLookupBody = ExtractBlock(
            mainWindow,
            "private Task<long> ResolveLocalLastSyncRevisionAsync(CancellationToken ct)",
            "internal static async Task<long> ResolveLocalLastSyncRevisionAsync(");
        var revisionLookupBody = ExtractBlock(
            mainWindow,
            "internal static async Task<long> ResolveLocalLastSyncRevisionAsync(",
            "internal static async Task<T> RunIsolatedLocalStateOperationAsync<T>");
        var isolatedOperationBody = ExtractBlock(
            mainWindow,
            "internal static async Task<T> RunIsolatedLocalStateOperationAsync<T>",
            "public void ShowDeferredStartupNotifications()");

        Assert.Contains("ResolveLocalLastSyncRevisionAsync(_serviceScopeFactory, ct)", instanceLookupBody, StringComparison.Ordinal);
        Assert.Contains("RunIsolatedLocalStateOperationAsync(", revisionLookupBody, StringComparison.Ordinal);
        Assert.Contains("local => local.GetSettingAsync(\"LastSyncRevision\", ct)", revisionLookupBody, StringComparison.Ordinal);
        Assert.DoesNotContain("_local.GetSettingAsync(\"LastSyncRevision\")", revisionLookupBody, StringComparison.Ordinal);
        Assert.Contains("using var scope = serviceScopeFactory.CreateScope();", isolatedOperationBody, StringComparison.Ordinal);
        Assert.Contains("scope.ServiceProvider.GetRequiredService<LocalStateService>()", isolatedOperationBody, StringComparison.Ordinal);
        Assert.Contains("await operation(local).ConfigureAwait(false)", isolatedOperationBody, StringComparison.Ordinal);
    }

    [Fact]
    public void RealtimeRevisionMonitor_ProductionWiringOwnsAndObservesItsCts()
    {
        var mainWindow = ReadDesktopSource("MainWindow.xaml.cs");
        var startBody = ExtractBlock(
            mainWindow,
            "private void StartRealtimeRevisionMonitor()",
            "private void StopRealtimeRevisionMonitor()");
        var stopBody = ExtractBlock(
            mainWindow,
            "private void StopRealtimeRevisionMonitor()",
            "internal static Task StartRealtimeRevisionMonitorTask(");

        Assert.Contains("var cts = new CancellationTokenSource();", startBody, StringComparison.Ordinal);
        Assert.Contains("_realtimeRevisionCts = cts;", startBody, StringComparison.Ordinal);
        Assert.Contains("StartRealtimeRevisionMonitorTask(", startBody, StringComparison.Ordinal);
        Assert.Contains("RunRealtimeRevisionMonitorAsync,", startBody, StringComparison.Ordinal);
        Assert.DoesNotContain("_realtimeRevisionCts.Token", startBody, StringComparison.Ordinal);

        Assert.Contains("var cts = _realtimeRevisionCts;", stopBody, StringComparison.Ordinal);
        Assert.Contains("var task = _realtimeRevisionTask;", stopBody, StringComparison.Ordinal);
        Assert.Contains("_realtimeRevisionCts = null;", stopBody, StringComparison.Ordinal);
        Assert.Contains("_realtimeRevisionTask = null;", stopBody, StringComparison.Ordinal);
        Assert.Contains("cts.Cancel();", stopBody, StringComparison.Ordinal);
        Assert.Contains("_realtimeRevisionDrainTask = ObserveAndDisposeRealtimeRevisionMonitorAsync(task, cts);", stopBody, StringComparison.Ordinal);
        Assert.Contains("UiTaskHelper.Forget(", stopBody, StringComparison.Ordinal);
        Assert.DoesNotContain("finally", stopBody, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopUpdate_StartAndHashValidationAreAsync()
    {
        var updateService = ReadDesktopSource("Services", "DesktopAppUpdateService.cs");
        var updateViewModel = ReadDesktopSource("ViewModels", "MainViewModel.Update.cs");

        Assert.Contains("public Task StartUpdateAsync(", updateService, StringComparison.Ordinal);
        Assert.Contains("Task.Run(() => StartUpdateCoreAsync", updateService, StringComparison.Ordinal);
        Assert.Contains("await VerifySha256Async", updateService, StringComparison.Ordinal);
        Assert.DoesNotContain("VerifySha256Async(fullPath, package.Sha256, CancellationToken.None).GetAwaiter().GetResult()", updateService, StringComparison.Ordinal);
        Assert.Contains("await BackgroundDesktopUpdateService.StartUpdateAsync", updateViewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void RentalLargeListQueries_RunOffUiAndApplyAfterAwait()
    {
        var billingViewModel = ReadDesktopSource("ViewModels", "RentalBillingViewModel.cs");
        var assetViewModel = ReadDesktopSource("ViewModels", "RentalAssetViewModel.cs");
        var settingsViewModel = ReadDesktopSource("ViewModels", "RentalSettingsViewModel.cs");

        Assert.Contains("Task.Run(\n                    () => _rental.GetBillingRowsAsync", billingViewModel.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("Task.Run(\n                    () => _rental.GetAssetRowsAsync", assetViewModel.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("Task.Run(() => _rental.ImportBillingWorkbookAsync", settingsViewModel, StringComparison.Ordinal);
        Assert.Contains("Task.Run(() => _rental.ImportAssetWorkbookAsync", settingsViewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void PrinterRefresh_DoesNotBlockUiOrRepeatQueueEnumeration()
    {
        var executor = ReadDesktopSource("Services", "TradePrintExecutor.cs");
        var catalog = ReadDesktopSource("Printing", "TradePrinterCatalog.cs");
        var printWindow = ReadDesktopSource("Views", "TradePrintWindow.xaml.cs");
        var refreshHandlerBody = ExtractBlock(
            printWindow,
            "private void OnRefreshPrintersClick(object sender, RoutedEventArgs e)",
            "private async Task RefreshPrintersAsync()");
        var refreshBody = ExtractBlock(
            printWindow,
            "private async Task RefreshPrintersAsync()",
            "private void OnPageModeChecked(object sender, RoutedEventArgs e)");

        Assert.Contains("TradePrinterCatalog.LoadSnapshot()", executor, StringComparison.Ordinal);
        Assert.Contains("PrinterInfoLevel = 2", catalog, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Printing", catalog, StringComparison.Ordinal);
        Assert.Contains("=> UiTaskHelper.Run(", refreshHandlerBody, StringComparison.Ordinal);
        Assert.Contains("this,", refreshHandlerBody, StringComparison.Ordinal);
        Assert.Contains("RefreshPrintersAsync,", refreshHandlerBody, StringComparison.Ordinal);
        Assert.DoesNotContain("async void", refreshHandlerBody, StringComparison.Ordinal);
        Assert.Contains("await Task.Run(_printerCatalogProvider)", refreshBody, StringComparison.Ordinal);
        Assert.DoesNotContain("PrintQueue", printWindow, StringComparison.Ordinal);
    }

    private static string ReadDesktopSource(params string[] relativeSegments)
    {
        var pathSegments = new[]
        {
            FindRepositoryRoot(),
            "Desktop",
            "거래플랜.Desktop.App"
        }.Concat(relativeSegments).ToArray();

        return File.ReadAllText(Path.Combine(pathSegments));
    }

    private static string ExtractBlock(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Start marker not found: {startMarker}");

        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"End marker not found after start: {endMarker}");
        return source[start..end];
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "거래플랜.sln")))
                return current.FullName;

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("거래플랜 repository root was not found.");
    }
}
