using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class MultiPcInventoryTransferOpenListUiaGateTests
{
    [Fact]
    public void MultiPcRuntime_DrainsTheShutdownBoundaryBeforeStartingFixtureWork()
    {
        var root = FindProjectRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "MainWindow.MultiPcE2E.cs"));
        var methodStart = source.IndexOf(
            "private async Task RunMultiPcDesktopE2EAsync",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "private async Task<MultiPcE2EContext> ValidateMultiPcE2EContextAsync",
            methodStart,
            StringComparison.Ordinal);
        Assert.True(methodStart >= 0 && methodEnd > methodStart);
        var method = source[methodStart..methodEnd];

        var beginShutdown = method.IndexOf("BeginShutdownProtection();", StringComparison.Ordinal);
        var drainShutdown = method.IndexOf(
            "await Task.WhenAll(",
            StringComparison.Ordinal);
        var fixturePreflight = method.IndexOf(
            "await RunMultiPcSessionPreflightAsync(context, steps);",
            StringComparison.Ordinal);

        Assert.True(beginShutdown >= 0);
        Assert.True(beginShutdown < drainShutdown);
        Assert.True(drainShutdown < fixturePreflight);
        var stoppedBoundary = method[drainShutdown..fixturePreflight];
        Assert.Contains("_vm.DrainPendingBackgroundWorkForShutdownAsync()", stoppedBoundary, StringComparison.Ordinal);
        Assert.Contains("_realtimeRevisionDrainTask", stoppedBoundary, StringComparison.Ordinal);
        Assert.Contains("_runtimeSyncDrainTask", stoppedBoundary, StringComparison.Ordinal);
        Assert.Contains("_windowCommandDrainTask", stoppedBoundary, StringComparison.Ordinal);
        Assert.DoesNotContain("_windowBackgroundWork.DrainAsync", stoppedBoundary, StringComparison.Ordinal);
    }

    [Fact]
    public void Runner_ParsesAndRequiresSameVisibleWindowRowDeltaAndTransferIdentity()
    {
        var root = FindProjectRoot();
        var runnerPath = Path.Combine(root, "테스트 시행", "Invoke-MultiPcDesktopE2E.ps1");
        var runner = File.ReadAllText(runnerPath);

        var observationStart = runner.IndexOf(
            "function Get-InventoryTransferListUiaObservation",
            StringComparison.Ordinal);
        var gateStart = runner.IndexOf(
            "function Invoke-InventoryTransferListUiaGate",
            observationStart,
            StringComparison.Ordinal);
        var gateEnd = runner.IndexOf(
            "function Get-ProcessByIdSafe",
            gateStart,
            StringComparison.Ordinal);
        Assert.True(observationStart >= 0 && gateStart > observationStart && gateEnd > gateStart);
        var observation = runner[observationStart..gateStart];
        var gate = runner[gateStart..gateEnd];

        Assert.Contains("Add-Type -AssemblyName UIAutomationClient", gate, StringComparison.Ordinal);
        Assert.Contains("[System.Windows.Automation.GridPattern]::Pattern", observation, StringComparison.Ordinal);
        Assert.Contains("$process.StartTime.ToUniversalTime().Ticks -ne $ExpectedProcessStartTimeUtcTicks", observation, StringComparison.Ordinal);
        Assert.Contains("[System.Windows.Automation.TreeScope]::Descendants", observation, StringComparison.Ordinal);
        Assert.Contains("[System.Windows.Automation.AndCondition]::new(", observation, StringComparison.Ordinal);
        Assert.Contains("InventoryTransferWindow", observation, StringComparison.Ordinal);
        Assert.Contains("TransferListGrid", observation, StringComparison.Ordinal);
        Assert.Contains("[System.Windows.Automation.ControlType]::DataItem", observation, StringComparison.Ordinal);
        Assert.Contains("-not $_.Current.IsOffscreen", observation, StringComparison.Ordinal);
        Assert.Contains("if ($matchingRows.Count -ne 1)", observation, StringComparison.Ordinal);
        Assert.Contains("function Wait-InventoryTransferListUiaObservation", observation, StringComparison.Ordinal);
        Assert.Contains("while ($wait.Elapsed -lt [TimeSpan]::FromSeconds($TimeoutSeconds))", observation, StringComparison.Ordinal);
        Assert.Contains("Start-Sleep -Milliseconds 100", observation, StringComparison.Ordinal);
        Assert.Contains("$before = Wait-InventoryTransferListUiaObservation", gate, StringComparison.Ordinal);
        Assert.Contains("-TimeoutSeconds 15", gate, StringComparison.Ordinal);
        Assert.Contains("-ExpectedTransferAutomationId $expectedTransferAutomationId", gate, StringComparison.Ordinal);
        Assert.Contains("[int]$candidate.RowCount -eq ([int]$before.RowCount + 1)", gate, StringComparison.Ordinal);
        Assert.Contains("[long]$candidate.WindowNativeHandle -eq [long]$before.WindowNativeHandle", gate, StringComparison.Ordinal);
        Assert.Contains("[string]$candidate.WindowRuntimeId -eq [string]$before.WindowRuntimeId", gate, StringComparison.Ordinal);
        Assert.Contains("[string]$candidate.ListRuntimeId -eq [string]$before.ListRuntimeId", gate, StringComparison.Ordinal);
        Assert.Contains("[string]$payload.Nonce -eq $Nonce", gate, StringComparison.Ordinal);
        Assert.Contains("[int]$payload.ProcessId -eq $appBProcessId", gate, StringComparison.Ordinal);
        Assert.Contains("[DateTimeOffset]$payload.CapturedAtUtc -gt [DateTimeOffset]$beforeGate.CapturedAtUtc", gate, StringComparison.Ordinal);
        Assert.Contains("[TimeSpan]::FromSeconds(60)", gate, StringComparison.Ordinal);
        Assert.Contains("out-of-process UIAutomationClient observations", gate, StringComparison.Ordinal);
        Assert.Contains("in-process ViewModel coordination only; not UIA evidence", gate, StringComparison.Ordinal);
        Assert.Contains("RunnerProcessId = $PID", gate, StringComparison.Ordinal);
    }

    [Fact]
    public void RoleA_WaitsForRunnerBeforeGateBeforeCreatingOrSavingTransfer()
    {
        var source = ReadInventoryTransferMultiPcSource();
        var methodStart = source.IndexOf(
            "private async Task RunMultiPcInventoryTransferRoleAAsync",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "private async Task RunMultiPcInventoryTransferRoleBAsync",
            methodStart,
            StringComparison.Ordinal);
        Assert.True(methodStart >= 0 && methodEnd > methodStart);
        var method = source[methodStart..methodEnd];

        var beforeGateWait = method.IndexOf(
            "await WaitForMultiPcInventoryTransferUiaGateAsync(",
            StringComparison.Ordinal);
        var beforeGateIdentity = method.IndexOf(
            "\"transfer-b-list-uia-ready.json\"",
            beforeGateWait,
            StringComparison.Ordinal);
        var viewModelCreation = method.IndexOf(
            "var vm = new InventoryTransferViewModel",
            StringComparison.Ordinal);
        var save = method.IndexOf(
            "await vm.SaveTransferCommand.ExecuteAsync(null);",
            StringComparison.Ordinal);

        Assert.True(beforeGateWait >= 0);
        Assert.True(beforeGateWait < beforeGateIdentity);
        Assert.True(beforeGateIdentity < viewModelCreation);
        Assert.True(viewModelCreation < save);
    }

    [Fact]
    public void RoleB_OpensListAndStartsRealRevisionMonitorBeforeCreateWithoutExplicitRefresh()
    {
        var source = ReadInventoryTransferMultiPcSource();
        var methodStart = source.IndexOf(
            "private async Task RunMultiPcInventoryTransferRoleBAsync",
            StringComparison.Ordinal);
        var methodEnd = source.IndexOf(
            "private async Task WriteMultiPcInventoryTransferListSignalAsync",
            methodStart,
            StringComparison.Ordinal);
        Assert.True(methodStart >= 0 && methodEnd > methodStart);
        var method = source[methodStart..methodEnd];

        var initialLoad = method.IndexOf("await vm.LoadAsync();", StringComparison.Ordinal);
        var windowOpen = method.IndexOf("ShowMultiPcInventoryTransferWindow(vm)", StringComparison.Ordinal);
        var monitorStart = method.IndexOf("StartMultiPcRealtimeRevisionObservation();", StringComparison.Ordinal);
        var readySignal = method.IndexOf("\"transfer-b-list-ready.json\"", StringComparison.Ordinal);
        var createdSignal = method.IndexOf("\"transfer-a-created.json\"", StringComparison.Ordinal);
        var vmUpdatedSignal = method.IndexOf("\"transfer-b-list-vm-updated.json\"", StringComparison.Ordinal);
        var afterUiaGate = method.IndexOf("\"transfer-b-list-uia-updated.json\"", StringComparison.Ordinal);
        var explicitOpen = method.IndexOf("await vm.OpenTransferAsync(transferId);", StringComparison.Ordinal);

        Assert.True(initialLoad < windowOpen);
        Assert.True(windowOpen < monitorStart);
        Assert.True(monitorStart < readySignal);
        Assert.True(readySignal < createdSignal);
        Assert.True(createdSignal < vmUpdatedSignal);
        Assert.True(vmUpdatedSignal < afterUiaGate);
        Assert.True(afterUiaGate < explicitOpen);

        var passiveObservationStart = method.IndexOf(
            "var beforeUiaGate = await WaitForMultiPcInventoryTransferUiaGateAsync(",
            StringComparison.Ordinal);
        var passiveObservationEnd = method.IndexOf(
            "StopMultiPcRealtimeRevisionObservation();",
            passiveObservationStart,
            StringComparison.Ordinal);
        Assert.True(passiveObservationStart >= 0 && passiveObservationEnd > passiveObservationStart);
        var passiveObservation = method[passiveObservationStart..passiveObservationEnd];
        Assert.DoesNotContain("SyncMultiPcAndRequireCleanAsync", passiveObservation, StringComparison.Ordinal);
        Assert.DoesNotContain("vm.LoadAsync", passiveObservation, StringComparison.Ordinal);
        Assert.DoesNotContain("vm.OpenTransferAsync", passiveObservation, StringComparison.Ordinal);
        Assert.DoesNotContain("HandleInventoryStateChangedAsync", passiveObservation, StringComparison.Ordinal);
        Assert.DoesNotContain("InventoryStateChanged?.Invoke", passiveObservation, StringComparison.Ordinal);
        Assert.DoesNotContain("window.Activate", passiveObservation, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowMultiPcInventoryTransferWindow", passiveObservation, StringComparison.Ordinal);
        Assert.Contains("_lastCentralRefreshUtc > createdSignal.CapturedAtUtc.UtcDateTime", passiveObservation, StringComparison.Ordinal);
        Assert.Contains("transfer.Revision == createdSignal.Revision", passiveObservation, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(60)", passiveObservation, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(70)", passiveObservation, StringComparison.Ordinal);
        Assert.Contains("StartRealtimeRevisionMonitor();", source, StringComparison.Ordinal);
        Assert.Contains("StopRealtimeRevisionMonitor();", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RealtimeObservation_ReopensAndResealsTheCanceledWindowLifetimeBoundary()
    {
        var source = ReadInventoryTransferMultiPcSource();
        var startMethodStart = source.IndexOf(
            "private void StartMultiPcRealtimeRevisionObservation()",
            StringComparison.Ordinal);
        var stopMethodStart = source.IndexOf(
            "private void StopMultiPcRealtimeRevisionObservation()",
            startMethodStart,
            StringComparison.Ordinal);
        var stopMethodEnd = source.IndexOf(
            "private static async Task<MultiPcUiaGate> WaitForMultiPcInventoryTransferUiaGateAsync",
            stopMethodStart,
            StringComparison.Ordinal);
        Assert.True(startMethodStart >= 0 && stopMethodStart > startMethodStart && stopMethodEnd > stopMethodStart);

        var startMethod = source[startMethodStart..stopMethodStart];
        var stopMethod = source[stopMethodStart..stopMethodEnd];
        var resumeViewModel = startMethod.IndexOf("_vm.ResumePendingBackgroundWorkAfterShutdownCanceled();", StringComparison.Ordinal);
        var resumeWindowTracker = startMethod.IndexOf("_windowBackgroundWork.Resume();", StringComparison.Ordinal);
        var replaceWindowToken = startMethod.IndexOf("_windowBackgroundWorkCts = new CancellationTokenSource();", StringComparison.Ordinal);
        var restoreWindows = startMethod.IndexOf("RestoreApplicationWindowsAfterCanceledShutdown();", StringComparison.Ordinal);
        var clearClosing = startMethod.IndexOf("_isClosingOrClosed = false;", StringComparison.Ordinal);
        var startMonitor = startMethod.IndexOf("StartRealtimeRevisionMonitor();", StringComparison.Ordinal);

        Assert.True(resumeViewModel >= 0);
        Assert.True(resumeViewModel < resumeWindowTracker);
        Assert.True(resumeWindowTracker < replaceWindowToken);
        Assert.True(replaceWindowToken < restoreWindows);
        Assert.True(restoreWindows < clearClosing);
        Assert.True(clearClosing < startMonitor);
        Assert.DoesNotContain("_windowBackgroundWork.IsCompleted", startMethod, StringComparison.Ordinal);
        Assert.Contains("_runtimeSyncDrainTask.IsCompletedSuccessfully", startMethod, StringComparison.Ordinal);
        Assert.Contains("_windowCommandDrainTask.IsCompletedSuccessfully", startMethod, StringComparison.Ordinal);
        Assert.Contains("_vm.IsShutdownBackgroundWorkCompleted", startMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("StartRuntimeSyncService", startMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("_centralRevisionPollTimer?.Start()", startMethod, StringComparison.Ordinal);

        var stopMonitor = stopMethod.IndexOf("StopRealtimeRevisionMonitor();", StringComparison.Ordinal);
        var sealWindowTracker = stopMethod.IndexOf("_windowBackgroundWork.BeginShutdown();", StringComparison.Ordinal);
        var cancelWindowToken = stopMethod.IndexOf("_windowBackgroundWorkCts.Cancel();", StringComparison.Ordinal);
        var cancelViewModel = stopMethod.IndexOf("_vm.CancelPendingBackgroundWorkForShutdown();", StringComparison.Ordinal);
        var restoreClosing = stopMethod.IndexOf("_isClosingOrClosed = true;", StringComparison.Ordinal);

        Assert.True(stopMonitor >= 0);
        Assert.True(stopMonitor < sealWindowTracker);
        Assert.True(sealWindowTracker < cancelWindowToken);
        Assert.True(cancelWindowToken < cancelViewModel);
        Assert.True(cancelViewModel < restoreClosing);
    }

    [Fact]
    public void InventoryTransferList_ExposesStableWindowGridAndRowAutomationIdentities()
    {
        var root = FindProjectRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "Views",
            "InventoryTransferWindow.xaml"));

        Assert.Contains("AutomationProperties.AutomationId=\"InventoryTransferWindow\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"TransferListGrid\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Property=\"AutomationProperties.AutomationId\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Value=\"{Binding Id}\"", xaml, StringComparison.Ordinal);
    }

    private static string ReadInventoryTransferMultiPcSource()
    {
        var root = FindProjectRoot();
        return File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "MainWindow.MultiPcE2E.InventoryTransfer.cs"));
    }

    private static string FindProjectRoot(
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "")
    {
        var current = new FileInfo(sourceFilePath).Directory;
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "Desktop")) &&
                Directory.Exists(Path.Combine(current.FullName, "테스트 시행")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Project root was not found.");
    }
}
