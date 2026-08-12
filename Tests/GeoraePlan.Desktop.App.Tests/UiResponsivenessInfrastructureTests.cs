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
        var printWindow = ReadDesktopSource("Views", "TradePrintWindow.xaml.cs");
        var queueLoadBody = ExtractBlock(
            executor,
            "private static IReadOnlyList<PrintQueue> LoadInstalledPrintQueues(",
            "private static PrintQueue? TryGetDefaultPrintQueue(");
        var refreshHandlerBody = ExtractBlock(
            printWindow,
            "private void OnRefreshPrintersClick(object sender, RoutedEventArgs e)",
            "private async Task RefreshPrintersAsync()");
        var refreshBody = ExtractBlock(
            printWindow,
            "private async Task RefreshPrintersAsync()",
            "private void OnPageModeChecked(object sender, RoutedEventArgs e)");

        Assert.Single(Regex.Matches(queueLoadBody, "GetPrintQueues\\(", RegexOptions.CultureInvariant));
        Assert.DoesNotContain("InstalledPrinterQueueTypeGroups", executor, StringComparison.Ordinal);
        Assert.Contains("=> UiTaskHelper.Run(", refreshHandlerBody, StringComparison.Ordinal);
        Assert.Contains("this,", refreshHandlerBody, StringComparison.Ordinal);
        Assert.Contains("RefreshPrintersAsync,", refreshHandlerBody, StringComparison.Ordinal);
        Assert.DoesNotContain("async void", refreshHandlerBody, StringComparison.Ordinal);
        Assert.Contains("await Task.Run(_printerRefreshProvider)", refreshBody, StringComparison.Ordinal);
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
