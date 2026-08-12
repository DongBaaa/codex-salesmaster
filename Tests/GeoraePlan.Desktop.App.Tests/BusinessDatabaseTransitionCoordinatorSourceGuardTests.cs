using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class BusinessDatabaseTransitionCoordinatorSourceGuardTests
{
    [Fact]
    public void EnvironmentSettingsViewModel_RunsDirtyRecheckAndDatabaseTransitionInsideCoordinator()
    {
        var appRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Desktop", "거래플랜.Desktop.App"));
        var constructorSource = File.ReadAllText(Path.Combine(appRoot, "ViewModels", "EnvironmentSettingsViewModel.cs"));
        var transitionSource = File.ReadAllText(Path.Combine(appRoot, "ViewModels", "EnvironmentSettingsViewModel.BusinessDatabase.cs"));

        Assert.Contains("Func<Func<Task>, Task>? runBusinessDatabaseTransitionAsync = null", constructorSource, StringComparison.Ordinal);
        Assert.Contains("_runBusinessDatabaseTransitionAsync = runBusinessDatabaseTransitionAsync;", constructorSource, StringComparison.Ordinal);

        var transitionBody = ExtractMethodBody(transitionSource, "private async Task LoadSelectedBusinessDatabaseAsync()");
        var applyBody = ExtractMethodBody(transitionBody, "async Task ApplyTransitionAsync()");

        Assert.Contains("await _local.HasPendingSyncChangesAsync()", applyBody, StringComparison.Ordinal);
        Assert.Contains("_session.SetBusinessDatabase(target.DatabaseName, target.CompanyName);", applyBody, StringComparison.Ordinal);
        Assert.Contains("ReplaceCurrentBusinessScopeCacheFromServerAsync()", applyBody, StringComparison.Ordinal);
        Assert.Contains("if (!cacheReplaced)", applyBody, StringComparison.Ordinal);
        Assert.Contains("businessCacheReplacementCommitted = true;", applyBody, StringComparison.Ordinal);
        Assert.Contains("await _applyBusinessDatabaseChangeAsync.Invoke();", applyBody, StringComparison.Ordinal);
        Assert.Contains("await ReloadCompanyProfilesAsync();", applyBody, StringComparison.Ordinal);
        Assert.Contains("await LoadCurrentUserCompanyProfileAsync();", applyBody, StringComparison.Ordinal);
        Assert.True(
            applyBody.IndexOf("HasPendingSyncChangesAsync", StringComparison.Ordinal)
            < applyBody.IndexOf("SetBusinessDatabase", StringComparison.Ordinal));
        Assert.True(
            applyBody.IndexOf("SetBusinessDatabase", StringComparison.Ordinal)
            < applyBody.IndexOf("ReplaceCurrentBusinessScopeCacheFromServerAsync", StringComparison.Ordinal));
        Assert.True(
            applyBody.IndexOf("ReplaceCurrentBusinessScopeCacheFromServerAsync", StringComparison.Ordinal)
            < applyBody.IndexOf("businessCacheReplacementCommitted = true", StringComparison.Ordinal));
        Assert.DoesNotContain("ResetBusinessDataCacheAsync", applyBody, StringComparison.Ordinal);

        Assert.DoesNotContain("HasPendingSyncChangesAsync", transitionBody[..transitionBody.IndexOf("async Task ApplyTransitionAsync()", StringComparison.Ordinal)], StringComparison.Ordinal);
        Assert.Contains("await _runBusinessDatabaseTransitionAsync.Invoke(ApplyTransitionAsync);", transitionBody, StringComparison.Ordinal);
        Assert.Contains("await ApplyTransitionAsync();", transitionBody, StringComparison.Ordinal);
        Assert.Contains("catch (Exception ex)", transitionBody, StringComparison.Ordinal);
        Assert.Contains("finally", transitionBody, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_DisablesItselfForTheWholeBusinessDatabaseTransition()
    {
        var appRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Desktop", "거래플랜.Desktop.App"));
        var mainWindowSource = File.ReadAllText(Path.Combine(appRoot, "MainWindow.xaml.cs"));
        var openSettingsBody = ExtractMethodBody(
            mainWindowSource,
            "private async Task OpenEnvironmentSettingsWindowAsync(EnvironmentSettingsInitialTab initialTab = EnvironmentSettingsInitialTab.General)");
        var transitionBody = ExtractMethodBody(
            mainWindowSource,
            "private async Task RunBusinessDatabaseTransitionAsync(Func<Task> transitionAsync)");
        var passiveBody = ExtractMethodBody(
            mainWindowSource,
            "private async Task RunPassiveSyncRefreshAsync(");

        Assert.Contains(
            "runBusinessDatabaseTransitionAsync: RunBusinessDatabaseTransitionAsync",
            openSettingsBody,
            StringComparison.Ordinal);
        Assert.Contains("var wasEnabled = IsEnabled;", transitionBody, StringComparison.Ordinal);
        Assert.Contains("initiatingSettingsWindows.Length != 1", transitionBody, StringComparison.Ordinal);
        Assert.Contains("blockingWindows.Length > 0", transitionBody, StringComparison.Ordinal);
        Assert.Contains("var enabledStates = applicationWindows.ToDictionary", transitionBody, StringComparison.Ordinal);
        Assert.Contains("IsEnabled = false;", transitionBody, StringComparison.Ordinal);
        Assert.Contains("window.IsEnabled = false;", transitionBody, StringComparison.Ordinal);
        Assert.Contains("_windowBackgroundWork.PauseNewWork();", transitionBody, StringComparison.Ordinal);
        Assert.Contains("EnsureBusinessDatabaseTransitionQuiescence(initiatingSettingsWindow);", transitionBody, StringComparison.Ordinal);
        Assert.Contains("await _passiveSyncTransitionGate.WaitAsync();", transitionBody, StringComparison.Ordinal);
        Assert.Contains("await _vm.RunBusinessDatabaseTransitionAsync(transitionAsync);", transitionBody, StringComparison.Ordinal);
        Assert.Contains("finally", transitionBody, StringComparison.Ordinal);
        Assert.Contains("if (!_isClosingOrClosed)", transitionBody, StringComparison.Ordinal);
        Assert.Contains("_passiveSyncTransitionGate.Release();", transitionBody, StringComparison.Ordinal);
        Assert.Contains("IsEnabled = wasEnabled;", transitionBody, StringComparison.Ordinal);
        Assert.Contains("_windowBackgroundWork.Resume();", transitionBody, StringComparison.Ordinal);
        Assert.Contains("Volatile.Write(ref _businessDatabaseTransitionInProgress, true);", transitionBody, StringComparison.Ordinal);
        Assert.Contains("Volatile.Write(ref _businessDatabaseTransitionInProgress, false);", transitionBody, StringComparison.Ordinal);
        Assert.True(
            transitionBody.IndexOf("IsEnabled = false;", StringComparison.Ordinal)
            < transitionBody.IndexOf("await _vm.RunBusinessDatabaseTransitionAsync", StringComparison.Ordinal));
        Assert.True(
            transitionBody.IndexOf("blockingWindows.Length > 0", StringComparison.Ordinal)
            < transitionBody.IndexOf("await _passiveSyncTransitionGate.WaitAsync();", StringComparison.Ordinal));
        Assert.True(
            transitionBody.LastIndexOf("EnsureBusinessDatabaseTransitionQuiescence(initiatingSettingsWindow);", StringComparison.Ordinal)
            < transitionBody.IndexOf("await _vm.RunBusinessDatabaseTransitionAsync", StringComparison.Ordinal));
        Assert.Contains("LoadSelectedBusinessDatabaseCommand", mainWindowSource, StringComparison.Ordinal);
        Assert.Contains("_windowBackgroundWork.IsIdle", mainWindowSource, StringComparison.Ordinal);
        var runUiBody = ExtractMethodBody(
            mainWindowSource,
            "private void RunUiAsync(Func<Task> operation, string operationName, string? userMessage = null)");
        Assert.Contains("Volatile.Read(ref _businessDatabaseTransitionInProgress)", runUiBody, StringComparison.Ordinal);
        Assert.Contains("await _passiveSyncTransitionGate.WaitAsync(ct);", passiveBody, StringComparison.Ordinal);
        Assert.Contains("await _vm.ReloadAfterPassiveSyncAsync(ct);", passiveBody, StringComparison.Ordinal);
        Assert.Contains("_passiveSyncTransitionGate.Release();", passiveBody, StringComparison.Ordinal);

        var mainViewModelSource = File.ReadAllText(Path.Combine(appRoot, "ViewModels", "MainViewModel.cs"));
        var reloadBody = ExtractMethodBody(
            mainViewModelSource,
            "private async Task ReloadCustomerAndInvoiceDataAsync(CancellationToken ct = default)");
        Assert.Contains("await _customerInlineDataGate.WaitAsync(ct);", reloadBody, StringComparison.Ordinal);
        Assert.Contains("await LoadCustomersAsync(ct, dataGateAlreadyHeld: true);", reloadBody, StringComparison.Ordinal);
        Assert.Contains("await LoadInvoiceListCoreAsync", reloadBody, StringComparison.Ordinal);
        Assert.Contains("dataGateAlreadyHeld: true", reloadBody, StringComparison.Ordinal);
        Assert.Contains("_customerInlineDataGate.Release();", reloadBody, StringComparison.Ordinal);

        var invoiceLoadBody = ExtractMethodBody(
            mainViewModelSource,
            "private async Task LoadInvoiceListCoreAsync(");
        Assert.Contains("bool dataGateAlreadyHeld = false)", mainViewModelSource, StringComparison.Ordinal);
        Assert.Contains("if (!dataGateAlreadyHeld)", invoiceLoadBody, StringComparison.Ordinal);
        Assert.Contains("await _customerInlineDataGate.WaitAsync(ct);", invoiceLoadBody, StringComparison.Ordinal);
        Assert.Contains("_invoiceListLoadCts?.Cancel();", invoiceLoadBody, StringComparison.Ordinal);
        Assert.Contains("loadCts = CancellationTokenSource.CreateLinkedTokenSource", invoiceLoadBody, StringComparison.Ordinal);
        Assert.Contains("await _invoiceListLoadGate.WaitAsync(ct);", invoiceLoadBody, StringComparison.Ordinal);
        Assert.Contains("_customerInlineDataGate.Release();", invoiceLoadBody, StringComparison.Ordinal);
        Assert.True(
            invoiceLoadBody.IndexOf("await _customerInlineDataGate.WaitAsync(ct);", StringComparison.Ordinal)
            < invoiceLoadBody.IndexOf("await _invoiceListLoadGate.WaitAsync(ct);", StringComparison.Ordinal));
        Assert.True(
            invoiceLoadBody.IndexOf("await _customerInlineDataGate.WaitAsync(ct);", StringComparison.Ordinal)
            < invoiceLoadBody.IndexOf("_invoiceListLoadCts?.Cancel();", StringComparison.Ordinal));

        var transitionQuiesceBody = ExtractMethodBody(
            mainViewModelSource,
            "private async Task QuiesceInvoiceWorkForBusinessDatabaseTransitionAsync()");
        Assert.Contains("await _invoiceFilterDebouncer.CancelAndDrainAsync();", transitionQuiesceBody, StringComparison.Ordinal);
        Assert.Contains("TryCancelShutdownToken(filterCancellation", transitionQuiesceBody, StringComparison.Ordinal);
        Assert.Contains("TryCancelShutdownToken(_invoiceListLoadCts", transitionQuiesceBody, StringComparison.Ordinal);
        Assert.Contains("await _invoiceFilterApplyTask;", transitionQuiesceBody, StringComparison.Ordinal);

        var businessReloadSource = File.ReadAllText(Path.Combine(appRoot, "ViewModels", "MainViewModel.BusinessDatabase.cs"));
        var businessReloadBody = ExtractMethodBody(
            businessReloadSource,
            "public async Task ReloadForBusinessDatabaseChangeAsync()");
        Assert.Contains("await LoadCustomersAsync(dataGateAlreadyHeld: true);", businessReloadBody, StringComparison.Ordinal);
        Assert.Contains("await LoadInvoiceFilterSettingsAsync(dataGateAlreadyHeld: true);", businessReloadBody, StringComparison.Ordinal);
        Assert.Contains("await LoadInvoiceListCoreAsync(", businessReloadBody, StringComparison.Ordinal);
        Assert.Contains("dataGateAlreadyHeld: true", businessReloadBody, StringComparison.Ordinal);
        Assert.DoesNotContain("await LoadInvoiceListAsync", businessReloadBody, StringComparison.Ordinal);
        var clearBusinessUiBody = ExtractMethodBody(
            businessReloadSource,
            "private void ClearBusinessDatabaseScopedUiState()");
        Assert.Contains("_suppressFilterAutoSave = true;", clearBusinessUiBody, StringComparison.Ordinal);
        Assert.Contains("_suppressFilterAutoSave = wasSuppressingFilterAutoSave;", clearBusinessUiBody, StringComparison.Ordinal);
    }

    [Fact]
    public void BusinessCacheReplacement_ResetsAndAppliesInsideExistingPullTransaction()
    {
        var appRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Desktop", "거래플랜.Desktop.App"));
        var syncSource = File.ReadAllText(Path.Combine(appRoot, "Services", "SyncService.cs"));
        var localSource = File.ReadAllText(Path.Combine(appRoot, "Services", "LocalStateService.BusinessDatabase.cs"));
        var reloadSource = File.ReadAllText(Path.Combine(appRoot, "ViewModels", "MainViewModel.BusinessDatabase.cs"));
        var applyBody = ExtractMethodBody(
            syncSource,
            "private async Task<bool> TryApplyPullAtomicallyCoreAsync(");

        Assert.Contains("public async Task<bool> ReplaceCurrentBusinessScopeCacheFromServerAsync(", syncSource, StringComparison.Ordinal);
        Assert.Contains("replaceLocalBusinessCache: true", syncSource, StringComparison.Ordinal);
        Assert.Contains("await _db.BeginRuntimeMutationTransactionAsync(ct);", applyBody, StringComparison.Ordinal);
        Assert.Contains("await _local.ResetBusinessDataCacheWithAttachmentJournalAsync(", applyBody, StringComparison.Ordinal);
        Assert.Contains("itemInvoiceHistoryChanged = await ApplyPullInternalAsync(", applyBody, StringComparison.Ordinal);
        Assert.True(
            applyBody.IndexOf("BeginRuntimeMutationTransactionAsync", StringComparison.Ordinal)
            < applyBody.IndexOf("ResetBusinessDataCacheWithAttachmentJournalAsync", StringComparison.Ordinal));
        Assert.True(
            applyBody.IndexOf("ResetBusinessDataCacheWithAttachmentJournalAsync", StringComparison.Ordinal)
            < applyBody.IndexOf("ApplyPullInternalAsync", StringComparison.Ordinal));
        Assert.Contains("_db.Database.CurrentTransaction is null", localSource, StringComparison.Ordinal);
        Assert.DoesNotContain("TrySyncAsync", reloadSource, StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshCurrentBusinessScopeFromServerAsync", reloadSource, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvironmentSettingsWindow_DisablesCommandsAndRejectsCloseWhileBusy()
    {
        var appRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Desktop", "거래플랜.Desktop.App"));
        var viewModelSource = File.ReadAllText(Path.Combine(appRoot, "ViewModels", "EnvironmentSettingsViewModel.cs"));
        var xamlSource = File.ReadAllText(Path.Combine(appRoot, "Views", "EnvironmentSettingsWindow.xaml"));
        var codeBehindSource = File.ReadAllText(Path.Combine(appRoot, "Views", "EnvironmentSettingsWindow.xaml.cs"));

        Assert.Contains("public bool CanInteract => !IsBusy;", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding CanInteract}\"", xamlSource, StringComparison.Ordinal);
        Assert.Contains("Closing += EnvironmentSettingsWindow_Closing;", codeBehindSource, StringComparison.Ordinal);
        var closingBody = ExtractMethodBody(
            codeBehindSource,
            "private void EnvironmentSettingsWindow_Closing(");
        Assert.Contains("if (!_viewModel.IsBusy)", closingBody, StringComparison.Ordinal);
        Assert.Contains("e.Cancel = true;", closingBody, StringComparison.Ordinal);
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

        throw new Xunit.Sdk.XunitException($"Closing brace not found: {signature}");
    }
}
