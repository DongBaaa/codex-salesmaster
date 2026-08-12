using System.IO;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class ShutdownCoordinatorRegressionSourceGuardTests
{
    [Fact]
    public void CustomerInlineAutoSave_UsesScopedPatchGlobalGateAndBlocksShutdownUntilSaved()
    {
        var source = ReadDesktopSource("ViewModels", "MainViewModel.cs");
        var patchSource = ReadDesktopSource(
            "ViewModels",
            "CustomerInlineEditPatchMerge.cs");
        var cancelBody = Extract(
            source,
            "public void CancelPendingBackgroundWorkForShutdown()",
            "private static void TryCancelShutdownToken(");
        var drainBody = Extract(
            source,
            "public async Task DrainPendingBackgroundWorkForShutdownAsync()",
            "public bool IsShutdownBackgroundWorkCompleted");
        var autoSaveBody = ExtractMethodBody(
            source,
            "private async Task AutoSaveCustomerAsync(");
        var commitBody = ExtractMethodBody(
            source,
            "private async Task<LocalCustomer> CommitCustomerInlineEditPatchAsync(");
        var scopeCaptureBody = ExtractMethodBody(
            source,
            "private CustomerInlineEditScopeIdentity CaptureCustomerInlineEditScopeIdentity()");

        Assert.Contains("internal readonly record struct CustomerInlineEditScopeIdentity(", patchSource, StringComparison.Ordinal);
        Assert.Contains("long SyncScopeEpoch,", patchSource, StringComparison.Ordinal);
        Assert.Contains("string BusinessOfficeCode,", patchSource, StringComparison.Ordinal);
        Assert.Contains("string BusinessDatabaseName);", patchSource, StringComparison.Ordinal);
        Assert.Contains("internal sealed record CustomerInlineEditPatch(", patchSource, StringComparison.Ordinal);
        Assert.Contains("CustomerInlineEditableFields Baseline,", patchSource, StringComparison.Ordinal);
        Assert.Contains("CustomerInlineEditableFields Desired,", patchSource, StringComparison.Ordinal);
        Assert.Contains("CustomerInlineFieldMask ChangedFields);", patchSource, StringComparison.Ordinal);
        Assert.Contains("CustomerInlineSaveStateTracker _customerInlineSaveState", source, StringComparison.Ordinal);
        Assert.Contains("private readonly SemaphoreSlim _customerInlineDataGate;", source, StringComparison.Ordinal);
        Assert.Contains("_customerInlineDataGate = local.OwnerScopeDataGate;", source, StringComparison.Ordinal);
        Assert.Equal(
            1,
            source.Split(
                "private readonly SemaphoreSlim _customerInlineDataGate;",
                StringSplitOptions.None).Length - 1);
        Assert.Contains("_pendingCustomerInlineEdits[customerId] = patch;", source, StringComparison.Ordinal);
        Assert.Contains("private async Task AutoSaveCustomerAsync(", source, StringComparison.Ordinal);
        Assert.Contains("CustomerInlineEditPatch patch,", source, StringComparison.Ordinal);
        Assert.Contains("await _customerInlineDataGate.WaitAsync(cancellationToken);", autoSaveBody, StringComparison.Ordinal);
        Assert.Contains("_customerInlineDataGate.Release();", autoSaveBody, StringComparison.Ordinal);
        Assert.Contains("patch.Scope != CaptureCustomerInlineEditScopeIdentityWithLeaseHeld()", commitBody, StringComparison.Ordinal);
        Assert.Contains("AcquireSyncScopeCommitLeaseAsync(cancellationToken)", commitBody, StringComparison.Ordinal);
        Assert.Contains("CustomerInlineEditPatchMerge.TryMerge(current, patch)", commitBody, StringComparison.Ordinal);
        Assert.Contains("CancellationToken.None", autoSaveBody, StringComparison.Ordinal);
        Assert.Contains("RebasePendingCustomerInlineEdit(", autoSaveBody, StringComparison.Ordinal);
        Assert.Contains("_session.AcquireSyncScopeSnapshotLease()", scopeCaptureBody, StringComparison.Ordinal);
        Assert.DoesNotContain("CustomerInlineEditSnapshot", source, StringComparison.Ordinal);
        Assert.DoesNotContain("snapshot.Customer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetCustomerAutoSaveExecutionGate", source, StringComparison.Ordinal);
        Assert.DoesNotContain("EndCustomerAutoSave", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_customerAutoSaveCtsByCustomer", cancelBody, StringComparison.Ordinal);
        Assert.Contains("_ownerScopeBackgroundWork.DrainAsync()", drainBody, StringComparison.Ordinal);
        Assert.Contains("SnapshotUnresolvedFailures()", drainBody, StringComparison.Ordinal);
        Assert.Contains("SnapshotPendingCustomerInlineEdits()", drainBody, StringComparison.Ordinal);
        Assert.Contains("거래처 정보 자동저장이 완료되지 않아 종료를 취소했습니다.", drainBody, StringComparison.Ordinal);
        Assert.Contains("pendingEdits.Select(patch => patch.Label)", drainBody, StringComparison.Ordinal);
        Assert.Contains("foreach (var patch in SnapshotPendingCustomerInlineEdits())", source, StringComparison.Ordinal);
        Assert.Contains("RetryUnresolvedCustomerInlineSave(value.Id);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedCustomerFilter", autoSaveBody, StringComparison.Ordinal);
    }

    [Fact]
    public void CustomerReloadAndBusinessDatabaseTransition_ShareTheGlobalInlineDataGate()
    {
        var source = ReadDesktopSource("ViewModels", "MainViewModel.cs");
        var loadBody = ExtractMethodBody(
            source,
            "private async Task LoadCustomersAsync(");
        var overlayBody = ExtractMethodBody(
            source,
            "private void ApplyPendingCustomerInlineEditOverlays(");
        var transitionBody = ExtractMethodBody(
            source,
            "public async Task RunBusinessDatabaseTransitionAsync(Func<Task> transitionAsync)");

        var waitIndex = loadBody.IndexOf(
            "await _customerInlineDataGate.WaitAsync(ct);",
            StringComparison.Ordinal);
        var overlayIndex = loadBody.IndexOf(
            "ApplyPendingCustomerInlineEditOverlays(customers);",
            StringComparison.Ordinal);
        var publishIndex = loadBody.IndexOf(
            "_allCustomers = customers;",
            StringComparison.Ordinal);
        var releaseIndex = loadBody.IndexOf(
            "_customerInlineDataGate.Release();",
            StringComparison.Ordinal);

        Assert.True(
            waitIndex >= 0 &&
            overlayIndex > waitIndex &&
            publishIndex > overlayIndex &&
            releaseIndex > publishIndex);
        Assert.Contains("SnapshotPendingCustomerInlineEdits()", overlayBody, StringComparison.Ordinal);
        Assert.Contains("patch.Scope == currentScope", overlayBody, StringComparison.Ordinal);
        Assert.Contains("patch.CustomerId", overlayBody, StringComparison.Ordinal);
        Assert.Contains("CustomerInlineEditPatchMerge.OverlayChangedFields(", overlayBody, StringComparison.Ordinal);
        Assert.Contains("patch.ChangedFields", overlayBody, StringComparison.Ordinal);

        Assert.Contains("_customerInlineBusinessTransitionInProgress = true;", transitionBody, StringComparison.Ordinal);
        Assert.Contains("await DrainCustomerAutoSaveTasksAsync();", transitionBody, StringComparison.Ordinal);
        Assert.Contains("ThrowIfCustomerInlineEditsIncomplete(\"업체 DB 전환\");", transitionBody, StringComparison.Ordinal);
        Assert.Contains("await _customerInlineDataGate.WaitAsync();", transitionBody, StringComparison.Ordinal);
        Assert.Contains("await transitionAsync();", transitionBody, StringComparison.Ordinal);
        Assert.Contains("finally", transitionBody, StringComparison.Ordinal);
        Assert.Contains("_customerInlineBusinessTransitionInProgress = false;", transitionBody, StringComparison.Ordinal);
        Assert.Contains("_customerInlineDataGate.Release();", transitionBody, StringComparison.Ordinal);
        Assert.Contains("bool dataGateAlreadyHeld = false", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AsyncLocal", source, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateShutdown_DoesNotBypassActiveMainScopeAfterCoordinatorFailure()
    {
        var source = ReadDesktopSource("App.xaml.cs");
        var updateBody = Extract(
            source,
            "private void BeginShutdownForUpdate()",
            "internal static IReadOnlyList<string> GetInstallRecoveryStartupRoots(");
        var recoveryIndex = updateBody.IndexOf(
            "TryRecoverActiveMainWindowAfterCanceledShutdown()",
            StringComparison.Ordinal);
        var directShutdownIndex = updateBody.LastIndexOf(
            "Shutdown(0);",
            StringComparison.Ordinal);

        Assert.True(recoveryIndex >= 0 && directShutdownIndex > recoveryIndex);
        Assert.Contains("return true;", Extract(
            source,
            "private bool TryRecoverActiveMainWindowAfterCanceledShutdown()",
            "private void QueueCloseAfterPostLoginDrain("), StringComparison.Ordinal);
    }

    [Fact]
    public void MandatoryWindowDrain_RetriesEveryPriorFailureWithoutDeadline()
    {
        var source = ReadDesktopSource("MainWindow.xaml.cs");
        var upgradeBody = Extract(
            source,
            "private async Task UpgradeWindowDrainToNoDeadlineAsync(",
            "public LocalStateService LocalStateService");

        Assert.Contains("catch (Exception ex)", upgradeBody, StringComparison.Ordinal);
        Assert.Contains(
            "waitForCompletionWithoutDeadline: true",
            upgradeBody,
            StringComparison.Ordinal);
        Assert.Contains("TryRunShutdownStep(", source, StringComparison.Ordinal);
        Assert.Contains("cancel window lifetime token", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RentalBillingNestedClosedRefreshes_AreTrackedAndSuppressedDuringShutdown()
    {
        var source = ReadDesktopSource("Views", "RentalBillingWindow.xaml.cs");
        var body = Extract(
            source,
            "private void AttachCustomerEditorClosedRefresh(",
            "private static T? FindAncestor<T>(");

        Assert.DoesNotContain("new Action(async", body, StringComparison.Ordinal);
        Assert.Contains("UiTaskHelper.Forget(", body, StringComparison.Ordinal);
        Assert.Contains("IsShutdownProtectionActive: true", body, StringComparison.Ordinal);
        Assert.Contains("await Dispatcher.Yield(", body, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalClose_AlwaysReleasesCoordinatorSemaphoreEvenWhenCloseThrows()
    {
        var source = ReadDesktopSource("App.xaml.cs");
        var body = Extract(
            source,
            "private async Task HandleMainWindowClosingAsync(",
            "private static Window ShowShutdownSavingPopup(");
        var closeIndex = body.LastIndexOf("mainWin.Close();", StringComparison.Ordinal);
        var finallyIndex = body.IndexOf("finally", closeIndex, StringComparison.Ordinal);
        var releaseIndex = body.IndexOf(
            "_mainWindowShutdownCoordinatorLock.Release();",
            closeIndex,
            StringComparison.Ordinal);

        Assert.True(closeIndex >= 0 && finallyIndex > closeIndex && releaseIndex > finallyIndex);
    }

    private static string ReadDesktopSource(params string[] relativeParts)
    {
        var root = FindRepositoryRoot();
        return File.ReadAllText(Path.Combine(
            new[] { root, "Desktop", "거래플랜.Desktop.App" }
                .Concat(relativeParts)
                .ToArray()));
    }

    private static string Extract(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Start marker not found: {startMarker}");
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"End marker not found: {endMarker}");
        return source[start..end];
    }

    private static string ExtractMethodBody(string source, string signature)
    {
        var signatureIndex = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(signatureIndex >= 0, $"Signature not found: {signature}");

        var openBraceIndex = source.IndexOf('{', signatureIndex + signature.Length);
        Assert.True(openBraceIndex >= 0, $"Opening brace not found: {signature}");

        var depth = 0;
        for (var index = openBraceIndex; index < source.Length; index++)
        {
            switch (source[index])
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                        return source[(openBraceIndex + 1)..index];
                    break;
            }
        }

        throw new Xunit.Sdk.XunitException(
            $"Closing brace not found: {signature}");
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "Desktop")) &&
                Directory.Exists(Path.Combine(current.FullName, "Tests")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("거래플랜 저장소 루트를 찾지 못했습니다.");
    }
}
