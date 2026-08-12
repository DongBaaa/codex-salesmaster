namespace GeoraePlan.Desktop.App.Tests;

using System.Diagnostics;
using System.Text;
using Xunit;

public sealed class MultiPcDesktopE2ESourceGuardTests
{
    [Fact]
    public void AutomatedTestLaunchers_DoNotHoldConsoleWindowsOrShowFailureDialogs()
    {
        var projectRoot = FindProjectRoot();
        var preparation = File.ReadAllText(
            Path.Combine(
                projectRoot,
                "테스트 시행",
                "테스트-환경-준비.ps1"));
        var uiSmoke = File.ReadAllText(
            Path.Combine(
                projectRoot,
                "tools",
                "verification",
                "Invoke-GeoraePlanDesktopUiSmoke.ps1"));

        var launcherStart = preparation.IndexOf(
            "$runAllContent = @\"",
            StringComparison.Ordinal);
        var launcherEnd = preparation.IndexOf(
            "\"@",
            launcherStart,
            StringComparison.Ordinal);
        Assert.True(launcherStart >= 0 && launcherEnd > launcherStart);
        var launcher = preparation[launcherStart..launcherEnd];
        Assert.DoesNotContain(
            Environment.NewLine + "  pause",
            launcher,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "GEORAEPLAN_SUPPRESS_FAILURE_DIALOG",
            launcher,
            StringComparison.Ordinal);
        Assert.Contains("-NonInteractive", launcher, StringComparison.Ordinal);
        Assert.Contains("-WindowStyle Hidden", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "start \"\" powershell",
            launcher,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "set \"RUN_EXIT=%ERRORLEVEL%\"",
            launcher,
            StringComparison.Ordinal);
        Assert.Contains("exit /b %RUN_EXIT%", launcher, StringComparison.Ordinal);

        Assert.Contains(
            "$env:GEORAEPLAN_SUPPRESS_FAILURE_DIALOG",
            preparation,
            StringComparison.Ordinal);
        Assert.Contains(
            "$env:GEORAEPLAN_SUPPRESS_FAILURE_DIALOG = '1'",
            preparation,
            StringComparison.Ordinal);
        Assert.Contains(
            "$previousFailureDialogSuppression",
            preparation,
            StringComparison.Ordinal);

        var failureDialogStart = preparation.IndexOf(
            "-not [string]::IsNullOrWhiteSpace($runtimeFailureMessage)",
            StringComparison.Ordinal);
        var failureDialogEnd = preparation.IndexOf(
            "exit $runExitCode",
            failureDialogStart,
            StringComparison.Ordinal);
        Assert.True(
            failureDialogStart >= 0 && failureDialogEnd > failureDialogStart,
            "The generated Run-All failure-dialog block was not found.");
        var failureDialog =
            preparation[failureDialogStart..failureDialogEnd];
        Assert.Contains(
            "$env:GEORAEPLAN_SHOW_FAILURE_DIALOG",
            failureDialog,
            StringComparison.Ordinal);
        Assert.Contains(
            "$env:GEORAEPLAN_SUPPRESS_FAILURE_DIALOG",
            failureDialog,
            StringComparison.Ordinal);
        var optInIndex = failureDialog.IndexOf(
            "$env:GEORAEPLAN_SHOW_FAILURE_DIALOG",
            StringComparison.Ordinal);
        var optInValueIndex = failureDialog.IndexOf(
            "'1'",
            optInIndex,
            StringComparison.Ordinal);
        var suppressionIndex = failureDialog.IndexOf(
            "$env:GEORAEPLAN_SUPPRESS_FAILURE_DIALOG",
            StringComparison.Ordinal);
        Assert.True(
            optInValueIndex > optInIndex &&
            suppressionIndex > optInValueIndex,
            "The failure dialog must require explicit opt-in before the " +
            "legacy suppression override is evaluated.");
        Assert.Contains(
            "[System.Windows.MessageBox]::Show(",
            failureDialog,
            StringComparison.Ordinal);
        Assert.Contains(
            "[Console]::Error.WriteLine(",
            failureDialog,
            StringComparison.Ordinal);
        Assert.Contains(
            "Error log: $errorLogPath",
            failureDialog,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "GEORAEPLAN_SHOW_FAILURE_DIALOG",
            launcher,
            StringComparison.Ordinal);
        Assert.Contains(
            "$startInfo.UseShellExecute = $false",
            preparation,
            StringComparison.Ordinal);
        Assert.Contains(
            "$startInfo.CreateNoWindow = $true",
            preparation,
            StringComparison.Ordinal);
        Assert.Contains(
            "$startInfo.RedirectStandardOutput = $true",
            preparation,
            StringComparison.Ordinal);
        Assert.Contains(
            "$startInfo.RedirectStandardError = $true",
            preparation,
            StringComparison.Ordinal);
        Assert.Contains(
            "[Environment]::SystemDirectory",
            preparation,
            StringComparison.Ordinal);
        Assert.Contains(
            "WindowsPowerShell\\v1.0\\powershell.exe",
            preparation,
            StringComparison.Ordinal);
        Assert.Contains(
            "$ErrorActionPreference = ''Stop''",
            preparation,
            StringComparison.Ordinal);
        Assert.Contains(
            "$ProgressPreference = ''SilentlyContinue''",
            preparation,
            StringComparison.Ordinal);
        Assert.Contains(
            "3>&1 4>&1 5>&1 6>&1",
            preparation,
            StringComparison.Ordinal);
        Assert.Contains(
            "$setApiStandardErrorPresent",
            preparation,
            StringComparison.Ordinal);

        var preparationSetApiStart = preparation.IndexOf(
            "Copy-Item -LiteralPath $setApiSource",
            StringComparison.Ordinal);
        var preparationSetApiEnd = preparation.IndexOf(
            "$testAndroidPackageState",
            preparationSetApiStart,
            StringComparison.Ordinal);
        Assert.True(
            preparationSetApiStart >= 0 &&
            preparationSetApiEnd > preparationSetApiStart);
        var preparationSetApi =
            preparation[preparationSetApiStart..preparationSetApiEnd];
        Assert.Contains(
            "Invoke-HiddenSetApiBaseUrl",
            preparationSetApi,
            StringComparison.Ordinal);

        var uiSetApiStart = uiSmoke.IndexOf(
            "$setApiScript = Join-Path $testRoot 'Set-ApiBaseUrl.ps1'",
            StringComparison.Ordinal);
        var uiSetApiEnd = uiSmoke.IndexOf(
            "$serverProcess = Start-IsolatedTestServer",
            uiSetApiStart,
            StringComparison.Ordinal);
        Assert.True(uiSetApiStart >= 0 && uiSetApiEnd > uiSetApiStart);
        var uiSetApi = uiSmoke[uiSetApiStart..uiSetApiEnd];
        Assert.Contains(
            "Invoke-HiddenSetApiBaseUrl",
            uiSetApi,
            StringComparison.Ordinal);
        Assert.Contains(
            "$startInfo.CreateNoWindow = $true",
            uiSmoke,
            StringComparison.Ordinal);
        Assert.Contains(
            "$startInfo.UseShellExecute = $false",
            uiSmoke,
            StringComparison.Ordinal);
        Assert.Contains(
            "[Environment]::SystemDirectory",
            uiSmoke,
            StringComparison.Ordinal);
        Assert.Contains(
            "$ErrorActionPreference = ''Stop''",
            uiSmoke,
            StringComparison.Ordinal);
        Assert.Contains(
            "$ProgressPreference = ''SilentlyContinue''",
            uiSmoke,
            StringComparison.Ordinal);
        Assert.Contains(
            "3>&1 4>&1 5>&1 6>&1",
            uiSmoke,
            StringComparison.Ordinal);
        Assert.Contains(
            "$setApiStandardErrorPresent",
            uiSmoke,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeneratedRunAllLauncher_PropagatesPowerShellFailureWithoutPause()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var preparation = File.ReadAllText(
            Path.Combine(
                FindProjectRoot(),
                "테스트 시행",
                "테스트-환경-준비.ps1"));
        const string startMarker = "$runAllContent = @\"";
        var markerIndex = preparation.IndexOf(
            startMarker,
            StringComparison.Ordinal);
        var contentStart = preparation.IndexOf(
            '\n',
            markerIndex + startMarker.Length) + 1;
        var contentEnd = preparation.IndexOf(
            "\n\"@",
            contentStart,
            StringComparison.Ordinal);
        Assert.True(markerIndex >= 0 && contentStart > markerIndex);
        Assert.True(contentEnd > contentStart);
        var launcher = preparation[contentStart..contentEnd];

        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"run-all-exit-propagation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testRoot);
        try
        {
            var launcherPath = Path.Combine(testRoot, "Run-All.cmd");
            File.WriteAllText(
                launcherPath,
                launcher,
                Encoding.ASCII);
            File.WriteAllText(
                Path.Combine(testRoot, ".georaeplan-runtime-ready"),
                "runtime_ready=True",
                Encoding.ASCII);
            File.WriteAllText(
                Path.Combine(testRoot, "Run-All.ps1"),
                "exit 23",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var commandPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "cmd.exe");
            var startInfo = new ProcessStartInfo
            {
                FileName = commandPath,
                WorkingDirectory = testRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(launcherPath);

            using var process = Process.Start(startInfo);
            Assert.NotNull(process);
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync()
                .WaitAsync(TimeSpan.FromSeconds(20));
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            Assert.Equal(23, process.ExitCode);
            Assert.DoesNotContain(
                "Press any key",
                stdout + stderr,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void InAppHook_IsFailClosedAndExercisesActualServerStalePush()
    {
        var projectRoot = FindProjectRoot();
        var source = File.ReadAllText(
            Path.Combine(
                projectRoot,
                "Desktop",
                "거래플랜.Desktop.App",
                "MainWindow.MultiPcE2E.cs"));

        Assert.Contains("AppPaths.IsTestEnvironment", source, StringComparison.Ordinal);
        Assert.Contains("IsMultiPcEvidenceRoot", source, StringComparison.Ordinal);
        Assert.Contains("IsStrictLoopbackHttpUri", source, StringComparison.Ordinal);
        Assert.Contains("ValidateMultiPcServerAttestationAsync", source, StringComparison.Ordinal);
        Assert.Contains("RuntimeReadyMarkerSha256", source, StringComparison.Ordinal);
        Assert.Contains("ServerAssemblyPathSha256", source, StringComparison.Ordinal);
        Assert.Contains("processStartTimeUtc", source, StringComparison.Ordinal);
        Assert.Contains("AppPermissionNames.DataBackupRestore", source, StringComparison.Ordinal);
        Assert.Contains("_api.PushAsync", source, StringComparison.Ordinal);
        Assert.Contains("Expected revision mismatch.", source, StringComparison.Ordinal);
        Assert.Contains("_sync.EnsureDeviceIdAsync()", source, StringComparison.Ordinal);
        Assert.Contains("IsGeneratedSyncDeviceId", source, StringComparison.Ordinal);
        Assert.Contains("Guid.TryParseExact", source, StringComparison.Ordinal);
        Assert.Contains("TryCleanupFailedMultiPcFixtureAsync", source, StringComparison.Ordinal);
        Assert.Contains("payload.ProcessId == context.OtherProcessId", source, StringComparison.Ordinal);
        Assert.Contains("string.Equals(payload.Role, expectedRole", source, StringComparison.Ordinal);
        Assert.Contains("UserIdHash = ComputeRunScopedSha256(", source, StringComparison.Ordinal);
        Assert.Contains("otherSession.UserIdHash", source, StringComparison.Ordinal);
        Assert.Contains("InstallRootHash = ComputeRunScopedSha256(", source, StringComparison.Ordinal);
        Assert.Contains("BusinessDatabaseNameHash = ComputeRunScopedSha256(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("public Guid UserId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AppRoot = context is null", source, StringComparison.Ordinal);
        Assert.DoesNotContain("InstallRoot = AppContext.BaseDirectory", source, StringComparison.Ordinal);
        Assert.Contains("await WriteMultiPcReportAsync(context, result, steps);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteMultiPcReportAsync(reportPath", source, StringComparison.Ordinal);
        Assert.Contains("Multi-PC E2E report path left the validated run root.", source, StringComparison.Ordinal);
        Assert.Contains("[user-profile]", source, StringComparison.Ordinal);
        Assert.DoesNotContain("public string InstallRoot {", source, StringComparison.Ordinal);
        Assert.DoesNotContain("public string AppRoot {", source, StringComparison.Ordinal);
        var queueStart = source.IndexOf(
            "private bool QueueMultiPcDesktopE2EIfRequested",
            StringComparison.Ordinal);
        var testEnvironmentGate = source.IndexOf(
            "if (!AppPaths.IsTestEnvironment)",
            queueStart,
            StringComparison.Ordinal);
        var roleEnvironmentRead = source.IndexOf(
            "Environment.GetEnvironmentVariable(MultiPcRoleEnvironmentKey)",
            queueStart,
            StringComparison.Ordinal);
        Assert.True(queueStart >= 0);
        Assert.True(testEnvironmentGate > queueStart);
        Assert.True(roleEnvironmentRead > testEnvironmentGate);

        var cleanSyncStart = source.IndexOf(
            "private async Task SyncMultiPcAndRequireCleanAsync",
            StringComparison.Ordinal);
        var cleanSyncEnd = source.IndexOf(
            "private async Task<LocalCustomer>",
            cleanSyncStart,
            StringComparison.Ordinal);
        Assert.True(cleanSyncStart >= 0);
        Assert.True(cleanSyncEnd > cleanSyncStart);
        var cleanSyncBody = source[cleanSyncStart..cleanSyncEnd];
        var retryLoop = cleanSyncBody.IndexOf(
            "for (var attempt = 1; attempt <= 3; attempt++)",
            StringComparison.Ordinal);
        var syncAttempt = cleanSyncBody.IndexOf(
            "synced = await _sync.TrySyncAsync();",
            StringComparison.Ordinal);
        var dirtyCheck = cleanSyncBody.IndexOf(
            "dirtyCount = await _local.CountDirtyAsync(_session);",
            StringComparison.Ordinal);
        var outboxCheck = cleanSyncBody.IndexOf(
            "outbox = await _local.GetSyncOutboxSummaryAsync(_session);",
            dirtyCheck,
            StringComparison.Ordinal);
        var cleanReturn = cleanSyncBody.IndexOf(
            "return;",
            outboxCheck,
            StringComparison.Ordinal);
        Assert.True(retryLoop >= 0);
        Assert.True(syncAttempt > retryLoop);
        Assert.True(dirtyCheck > syncAttempt);
        Assert.True(outboxCheck > dirtyCheck);
        Assert.True(cleanReturn > outboxCheck);
        Assert.DoesNotContain("UserIdentityHash", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ComputeSha256(_session.User.Username",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void InAppHook_ExercisesActualItemStaleAutosaveAndExactFixtureCleanup()
    {
        var projectRoot = FindProjectRoot();
        var source = File.ReadAllText(
            Path.Combine(
                projectRoot,
                "Desktop",
                "거래플랜.Desktop.App",
                "MainWindow.MultiPcE2E.cs"));
        var runner = File.ReadAllText(
            Path.Combine(
                projectRoot,
                "테스트 시행",
                "Invoke-MultiPcDesktopE2E.ps1"));

        Assert.Contains("RunMultiPcItemRoleAAsync", source, StringComparison.Ordinal);
        Assert.Contains("RunMultiPcItemRoleBAsync", source, StringComparison.Ordinal);
        Assert.Contains("BuildMultiPcStaleItemDto", source, StringComparison.Ordinal);
        Assert.Contains("BuildMultiPcStaleItemDeleteDto", source, StringComparison.Ordinal);
        Assert.Contains("signal.ItemId == itemId", source, StringComparison.Ordinal);
        Assert.Contains("\"item-a-created.json\"", source, StringComparison.Ordinal);
        Assert.Contains("\"item-actual-server-stale-delete\"", source, StringComparison.Ordinal);
        Assert.Contains("\"item-stale-autosave-conflict\"", source, StringComparison.Ordinal);
        Assert.Contains("GetItemPurgeResidueCountsAsync(itemId)", source, StringComparison.Ordinal);
        Assert.Contains("item-fixture-purge-no-residue", source, StringComparison.Ordinal);
        Assert.Contains("Kind = \"item\"", source, StringComparison.Ordinal);
        Assert.Contains(
            "customer-and-item-stale-autosave-and-delete-propagation",
            source,
            StringComparison.Ordinal);

        Assert.Contains("\"item-a-created.json\"", runner, StringComparison.Ordinal);
        Assert.Contains("\"item-b-complete.json\"", runner, StringComparison.Ordinal);
        Assert.Contains("in-app-item-conflict", runner, StringComparison.Ordinal);
        Assert.Contains(
            "-InAppSelfTestTimeoutSec $roleInAppSelfTestTimeoutSeconds",
            runner,
            StringComparison.Ordinal);
        Assert.Contains("20/20 required nonce-bound signals present", runner, StringComparison.Ordinal);

        var uiSmoke = File.ReadAllText(
            Path.Combine(
                projectRoot,
                "tools",
                "verification",
                "Invoke-GeoraePlanDesktopUiSmoke.ps1"));
        Assert.Contains("[int]$InAppSelfTestTimeoutSec = 160", uiSmoke, StringComparison.Ordinal);
        Assert.Contains(
            "Wait-FileReady -Path $InAppSelfTestReportPath -TimeoutSeconds $InAppSelfTestTimeoutSec",
            uiSmoke,
            StringComparison.Ordinal);

        var inventoryViewModel = File.ReadAllText(
            Path.Combine(
                projectRoot,
                "Desktop",
                "거래플랜.Desktop.App",
                "ViewModels",
                "InventoryViewModel.cs"));
        Assert.Contains("_isInventoryRefreshInProgress", inventoryViewModel, StringComparison.Ordinal);
        Assert.Contains("oldValue.Id == newValue.Id", inventoryViewModel, StringComparison.Ordinal);
        Assert.Contains("_preservePendingEditOnSameItemSelection", inventoryViewModel, StringComparison.Ordinal);
        Assert.Contains("_preservePendingEditDuringListRefresh", inventoryViewModel, StringComparison.Ordinal);
        Assert.Contains("_preservedFilteredEditItemId", inventoryViewModel, StringComparison.Ordinal);
        Assert.Contains("selectedItemStillExists is not null", inventoryViewModel, StringComparison.Ordinal);
        Assert.DoesNotContain(
            ".Append('|').Append(snapshot.EditTotalStock)",
            inventoryViewModel,
            StringComparison.Ordinal);
        var itemScopeGuard = File.ReadAllText(
            Path.Combine(
                projectRoot,
                "Desktop",
                "거래플랜.Desktop.App",
                "Services",
                "LocalStateService.ItemScopeGuard.cs"));
        Assert.Contains("preserveExistingInventoryStock: true", itemScopeGuard, StringComparison.Ordinal);
    }

    [Fact]
    public void RunnerAndUiSmoke_FailClosedForSecretsUnicodeInputTimeoutAndExactCleanup()
    {
        var projectRoot = FindProjectRoot();
        var runner = File.ReadAllText(
            Path.Combine(
                projectRoot,
                "테스트 시행",
                "Invoke-MultiPcDesktopE2E.ps1"));
        var uiSmoke = File.ReadAllText(
            Path.Combine(
                projectRoot,
                "tools",
                "verification",
                "Invoke-GeoraePlanDesktopUiSmoke.ps1"));

        Assert.Contains("[int]$TimeoutSeconds = 900", runner, StringComparison.Ordinal);
        Assert.Contains("$minimumExternalTimeoutSeconds = [Math]::Max(", runner, StringComparison.Ordinal);
        Assert.Contains("$roleInAppSelfTestTimeoutSeconds = 480", runner, StringComparison.Ordinal);
        Assert.Contains("$shutdownAndReleaseBudgetSeconds = 120", runner, StringComparison.Ordinal);
        Assert.Contains(
            "$TimeoutSeconds -lt $minimumExternalTimeoutSeconds",
            runner,
            StringComparison.Ordinal);

        Assert.Contains("[string]$Username = ''", uiSmoke, StringComparison.Ordinal);
        Assert.Contains("[string]$Password = ''", uiSmoke, StringComparison.Ordinal);
        Assert.True(HasOnlyEmptyCredentialParameterDefaults(uiSmoke));
        Assert.Contains(
            "[Environment]::SetEnvironmentVariable(",
            uiSmoke,
            StringComparison.Ordinal);
        Assert.Contains("'GEORAEPLAN_TEST_PASSWORD'", uiSmoke, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "[System.Windows.Forms.SendKeys]::SendWait($Text)",
            uiSmoke,
            StringComparison.Ordinal);
        Assert.DoesNotContain("loginTexts=", uiSmoke, StringComparison.Ordinal);
        Assert.DoesNotContain("SendInput", uiSmoke, StringComparison.Ordinal);
        Assert.DoesNotContain("KEYEVENTF_UNICODE", uiSmoke, StringComparison.Ordinal);
        Assert.DoesNotContain("ReplaceFocusedTextWithUnicode", uiSmoke, StringComparison.Ordinal);
        Assert.DoesNotContain("struct INPUT", uiSmoke, StringComparison.Ordinal);
        Assert.DoesNotContain("struct KEYBDINPUT", uiSmoke, StringComparison.Ordinal);
        Assert.DoesNotContain("GetNativeInputSizeForVerification", uiSmoke, StringComparison.Ordinal);
        Assert.Contains("SeedUsers__EnableSeedUsers", uiSmoke, StringComparison.Ordinal);
        Assert.Contains("SeedUsers__AdminPassword", uiSmoke, StringComparison.Ordinal);
        Assert.Contains("SeedUsers__UpdateExistingAdminPassword", uiSmoke, StringComparison.Ordinal);
        Assert.Contains("SeedUsers__AdminOnlyBootstrap", uiSmoke, StringComparison.Ordinal);
        Assert.Contains("SeedUsers__UsenetUsername", uiSmoke, StringComparison.Ordinal);
        Assert.Contains("SeedUsers__UsenetPassword", uiSmoke, StringComparison.Ordinal);
        Assert.Contains(
            "SeedUsers__UpdateExistingUsenetPassword",
            uiSmoke,
            StringComparison.Ordinal);
        Assert.Contains("GetWindowThreadProcessId", uiSmoke, StringComparison.Ordinal);
        Assert.Contains("Assert-LoginInputTarget", uiSmoke, StringComparison.Ordinal);
        Assert.Contains("-ExpectedAutomationId 'UsernameBox'", uiSmoke, StringComparison.Ordinal);
        Assert.Contains("-ExpectedAutomationId 'PasswordBox'", uiSmoke, StringComparison.Ordinal);
        Assert.Contains("System.Threading.Mutex", uiSmoke, StringComparison.Ordinal);
        Assert.Contains("LoginInputMutexName", uiSmoke, StringComparison.Ordinal);
        Assert.Contains("$Username = $null", uiSmoke, StringComparison.Ordinal);
        Assert.Contains("$Password = $null", uiSmoke, StringComparison.Ordinal);

        var setElementTextStart = uiSmoke.IndexOf(
            "function Set-ElementText",
            StringComparison.Ordinal);
        var setElementTextEnd = uiSmoke.IndexOf(
            "function Close-Window",
            setElementTextStart,
            StringComparison.Ordinal);
        Assert.True(setElementTextStart >= 0);
        Assert.True(setElementTextEnd > setElementTextStart);
        var setElementText = uiSmoke[setElementTextStart..setElementTextEnd];
        var initialTargetAttestation = setElementText.IndexOf(
            "[void](Assert-LoginInputTarget",
            StringComparison.Ordinal);
        var valuePatternLookup = setElementText.IndexOf(
            "$valuePattern = $Element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)",
            initialTargetAttestation,
            StringComparison.Ordinal);
        var valuePatternInput = setElementText.IndexOf(
            "$valuePattern.SetValue($Text)",
            valuePatternLookup,
            StringComparison.Ordinal);
        var unsupportedPatternFailure = setElementText.IndexOf(
            "$null -eq $valuePattern -or $valuePattern.Current.IsReadOnly",
            valuePatternLookup,
            StringComparison.Ordinal);
        var firstFailClosedReturn = setElementText.IndexOf(
            "return $false",
            unsupportedPatternFailure,
            StringComparison.Ordinal);
        var catchStart = setElementText.IndexOf(
            "catch {",
            valuePatternInput,
            StringComparison.Ordinal);
        var catchFailClosedReturn = setElementText.IndexOf(
            "return $false",
            catchStart,
            StringComparison.Ordinal);
        var postInputTargetAttestation = setElementText.LastIndexOf(
            "[void](Assert-LoginInputTarget",
            StringComparison.Ordinal);
        Assert.True(initialTargetAttestation >= 0);
        Assert.True(valuePatternLookup > initialTargetAttestation);
        Assert.True(unsupportedPatternFailure > valuePatternLookup);
        Assert.True(firstFailClosedReturn > unsupportedPatternFailure);
        Assert.True(valuePatternInput > firstFailClosedReturn);
        Assert.True(catchStart > valuePatternInput);
        Assert.True(catchFailClosedReturn > catchStart);
        Assert.True(postInputTargetAttestation > valuePatternInput);
        Assert.DoesNotContain("SendInput", setElementText, StringComparison.Ordinal);
        Assert.DoesNotContain("SendKeys", setElementText, StringComparison.Ordinal);
        Assert.DoesNotContain("mouse_event", setElementText, StringComparison.Ordinal);
        Assert.DoesNotContain("ReplaceFocusedTextWithUnicode", setElementText, StringComparison.Ordinal);
        Assert.DoesNotContain("SetForegroundWindow", setElementText, StringComparison.Ordinal);
        Assert.DoesNotContain("GetForegroundWindow", setElementText, StringComparison.Ordinal);
        Assert.DoesNotContain("SetFocus", setElementText, StringComparison.Ordinal);
        Assert.DoesNotContain("-RequireForeground", setElementText, StringComparison.Ordinal);
        Assert.DoesNotContain("-RequireFocus", setElementText, StringComparison.Ordinal);
        Assert.Equal(
            1,
            setElementText.Split(
                "$valuePattern = $Element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)",
                StringSplitOptions.None).Length - 1);
        Assert.Equal(
            new[]
            {
                "[string]$Text,",
                "if ($null -eq $Element -or [string]::IsNullOrEmpty($Text)) {",
                "$valuePattern.SetValue($Text)",
            },
            setElementText
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')
                .Where(line => System.Text.RegularExpressions.Regex.IsMatch(
                    line,
                    @"(?i)\$Text\b"))
                .Select(line => line.Trim())
                .ToArray());
        Assert.DoesNotMatch(
            @"(?i)\$(?:global|script|local|private):Text\b",
            setElementText);

        var loginInputStart = uiSmoke.IndexOf(
            "if ($startupWindow.Kind -eq 'Login' -or (Test-IsLoginWindow -Window $startupWindow.Window))",
            StringComparison.Ordinal);
        var loginCredentialClear = uiSmoke.IndexOf(
            "$Password = $null",
            loginInputStart,
            StringComparison.Ordinal);
        Assert.True(loginInputStart >= 0);
        Assert.True(loginCredentialClear > loginInputStart);
        var loginInput = uiSmoke[
            loginInputStart..
            (loginCredentialClear + "$Password = $null".Length)];
        Assert.Equal(
            2,
            loginInput.Split("Set-ElementText", StringSplitOptions.None).Length - 1);
        Assert.Equal(
            1,
            loginInput.Split("-Text $Username", StringSplitOptions.None).Length - 1);
        Assert.Equal(
            1,
            loginInput.Split("-Text $Password", StringSplitOptions.None).Length - 1);
        Assert.Contains("-Element $usernameBox", loginInput, StringComparison.Ordinal);
        Assert.Contains("-Element $passwordBox", loginInput, StringComparison.Ordinal);
        Assert.Contains("-ExpectedAutomationId 'UsernameBox'", loginInput, StringComparison.Ordinal);
        Assert.Contains("-ExpectedAutomationId 'PasswordBox'", loginInput, StringComparison.Ordinal);
        Assert.DoesNotContain("SendInput", loginInput, StringComparison.Ordinal);
        Assert.DoesNotContain("SendKeys", loginInput, StringComparison.Ordinal);
        Assert.DoesNotContain("mouse_event", loginInput, StringComparison.Ordinal);
        Assert.DoesNotContain("ReplaceFocusedTextWithUnicode", loginInput, StringComparison.Ordinal);
        Assert.Equal(
            new[]
            {
                "[string]$Password = '',",
                "[string]::IsNullOrWhiteSpace($Password)",
                "-Text $Password `",
                "$Password = $null",
                "$Password = $null",
            },
            uiSmoke
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')
                .Where(line => System.Text.RegularExpressions.Regex.IsMatch(
                    line,
                    @"(?i)\$Password\b"))
                .Select(line => line.Trim())
                .ToArray());
        Assert.DoesNotMatch(
            @"(?i)\$(?:global|script|local|private):Password\b",
            uiSmoke);
        Assert.Equal(
            new[]
            {
                "[string]$Username = '',",
                "[string]::IsNullOrWhiteSpace($Username) -or",
                "-Text $Username `",
                "$Username = $null",
                "$Username = $null",
            },
            uiSmoke
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')
                .Where(line => System.Text.RegularExpressions.Regex.IsMatch(
                    line,
                    @"(?i)\$Username\b"))
                .Select(line => line.Trim())
                .ToArray());
        Assert.DoesNotMatch(
            @"(?i)\$(?:global|script|local|private):Username\b",
            uiSmoke);
        Assert.True(HasOnlyTargetBoundLoginCredentialInput(uiSmoke));

        var lineEnding = uiSmoke.Contains("\r\n", StringComparison.Ordinal)
            ? "\r\n"
            : "\n";
        var unsafePasswordAfterButton = uiSmoke.Replace(
            "$loginButton = Find-FirstByName -Root $loginWindow -Name '로그인'",
            "$loginButton = Find-FirstByName -Root $loginWindow -Name '로그인'" +
            lineEnding +
            "            Invoke-UnsafeInput -Value $Password",
            StringComparison.Ordinal);
        Assert.NotEqual(uiSmoke, unsafePasswordAfterButton);
        Assert.False(HasOnlyTargetBoundLoginCredentialInput(unsafePasswordAfterButton));

        var unsafeUsernameAfterButton = uiSmoke.Replace(
            "$loginButton = Find-FirstByName -Root $loginWindow -Name '로그인'",
            "$loginButton = Find-FirstByName -Root $loginWindow -Name '로그인'" +
            lineEnding +
            "            Invoke-UnsafeInput -Value $Username",
            StringComparison.Ordinal);
        Assert.NotEqual(uiSmoke, unsafeUsernameAfterButton);
        Assert.False(HasOnlyTargetBoundLoginCredentialInput(unsafeUsernameAfterButton));

        var wrongValuePatternReceiver = uiSmoke.Replace(
            "$valuePattern = $Element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)",
            "$valuePattern = $OtherElement.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)",
            StringComparison.Ordinal);
        Assert.NotEqual(uiSmoke, wrongValuePatternReceiver);
        Assert.False(HasOnlyTargetBoundLoginCredentialInput(wrongValuePatternReceiver));

        var attestationStart = uiSmoke.IndexOf(
            "function Assert-LoginInputTarget",
            StringComparison.Ordinal);
        var identityComparisonStart = uiSmoke.IndexOf(
            "function Test-SameAutomationElement",
            StringComparison.Ordinal);
        var attestationEnd = uiSmoke.IndexOf(
            "function Set-ElementText",
            attestationStart,
            StringComparison.Ordinal);
        Assert.True(identityComparisonStart >= 0);
        Assert.True(attestationStart > identityComparisonStart);
        Assert.True(attestationStart >= 0);
        Assert.True(attestationEnd > attestationStart);
        var noOpIdentityComparison =
            uiSmoke[..identityComparisonStart] +
            "function Test-SameAutomationElement {" +
            lineEnding +
            "    return $true" +
            lineEnding +
            "}" +
            lineEnding +
            lineEnding +
            uiSmoke[attestationStart..];
        Assert.False(HasOnlyTargetBoundLoginCredentialInput(noOpIdentityComparison));

        var noOpAttestation =
            uiSmoke[..attestationStart] +
            "function Assert-LoginInputTarget {" +
            lineEnding +
            "    return [IntPtr]::Zero" +
            lineEnding +
            "}" +
            lineEnding +
            lineEnding +
            uiSmoke[attestationEnd..];
        Assert.False(HasOnlyTargetBoundLoginCredentialInput(noOpAttestation));

        var unsafeRedefinition = uiSmoke +
            lineEnding +
            "Function set-elementtext { param($Element, $Text) " +
            "[System.Windows.Forms.SendKeys]::SendWait($Text) }" +
            lineEnding;
        Assert.False(HasOnlyTargetBoundLoginCredentialInput(unsafeRedefinition));

        var unsafeScopedRedefinition = uiSmoke +
            lineEnding +
            "Function script:Set-ElementText { param($Element, $Text) " +
            "[System.Windows.Forms.SendKeys]::SendWait($Text) }" +
            lineEnding;
        Assert.False(HasOnlyTargetBoundLoginCredentialInput(unsafeScopedRedefinition));

        var unsupportedBranch = string.Join(
            lineEnding,
            "if ($null -eq $valuePattern -or $valuePattern.Current.IsReadOnly) {",
            "            return $false",
            "        }");
        var unsafeUnsupportedBranch = uiSmoke.Replace(
            unsupportedBranch,
            string.Join(
                lineEnding,
                "if ($null -eq $valuePattern -or $valuePattern.Current.IsReadOnly) {",
                "            return $true",
                "            return $false",
                "        }"),
            StringComparison.Ordinal);
        Assert.NotEqual(uiSmoke, unsafeUnsupportedBranch);
        Assert.False(HasOnlyTargetBoundLoginCredentialInput(unsafeUnsupportedBranch));

        var unsafeTruthyUnsupportedBranch = uiSmoke.Replace(
            unsupportedBranch,
            string.Join(
                lineEnding,
                "if ($null -eq $valuePattern -or $valuePattern.Current.IsReadOnly) {",
                "            return (1)",
                "            return $false",
                "        }"),
            StringComparison.Ordinal);
        Assert.NotEqual(uiSmoke, unsafeTruthyUnsupportedBranch);
        Assert.False(HasOnlyTargetBoundLoginCredentialInput(unsafeTruthyUnsupportedBranch));

        var valuePatternCatch = string.Join(
            lineEnding,
            "$valuePattern.SetValue($Text)",
            "    }",
            "    catch {",
            "        return $false",
            "    }");
        var unsafeValuePatternCatch = uiSmoke.Replace(
            valuePatternCatch,
            string.Join(
                lineEnding,
                "$valuePattern.SetValue($Text)",
                "    }",
                "    catch {",
                "        return $true",
                "        return $false",
                "    }"),
            StringComparison.Ordinal);
        Assert.NotEqual(uiSmoke, unsafeValuePatternCatch);
        Assert.False(HasOnlyTargetBoundLoginCredentialInput(unsafeValuePatternCatch));

        var unsafeTruthyValuePatternCatch = uiSmoke.Replace(
            valuePatternCatch,
            string.Join(
                lineEnding,
                "$valuePattern.SetValue($Text)",
                "    }",
                "    catch {",
                "        return ($true)",
                "        return $false",
                "    }"),
            StringComparison.Ordinal);
        Assert.NotEqual(uiSmoke, unsafeTruthyValuePatternCatch);
        Assert.False(HasOnlyTargetBoundLoginCredentialInput(unsafeTruthyValuePatternCatch));

        Assert.Contains(
            "[Environment]::GetEnvironmentVariable(\"GEORAEPLAN_TEST_USERNAME\", \"Process\")",
            runner,
            StringComparison.Ordinal);
        Assert.Contains(
            "[Environment]::SetEnvironmentVariable('GEORAEPLAN_TEST_USERNAME', `$null, 'Process')",
            runner,
            StringComparison.Ordinal);
        Assert.Contains("Username = `$roleLoginUsername", runner, StringComparison.Ordinal);
        Assert.Contains("Password = `$roleLoginPassword", runner, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ConvertTo-SingleQuotedLiteral -Value $LoginUsername",
            runner,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ConvertTo-SingleQuotedLiteral -Value $LoginPassword",
            runner,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Detail $loginUsername", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("Detail $loginPassword", runner, StringComparison.Ordinal);
        Assert.Contains(
            "$startInfo.EnvironmentVariables[\"GEORAEPLAN_TEST_USERNAME\"] = $LoginUsername",
            runner,
            StringComparison.Ordinal);
        Assert.Contains(
            "$startInfo.EnvironmentVariables[\"GEORAEPLAN_TEST_PASSWORD\"] = $LoginPassword",
            runner,
            StringComparison.Ordinal);
        Assert.Contains("$startInfo.UseShellExecute = $false", runner, StringComparison.Ordinal);
        Assert.Contains("$startInfo.CreateNoWindow = $true", runner, StringComparison.Ordinal);
        Assert.Contains(
            "`$ProgressPreference = 'SilentlyContinue'",
            runner,
            StringComparison.Ordinal);
        Assert.Matches(
            @"(?m)^\s*& \$uiSmokeLiteral @invokeParameters 6>&1\s*$",
            runner);
        Assert.DoesNotMatch(
            @"(?m)^\s*& \$uiSmokeLiteral @invokeParameters\s*$",
            runner);
        Assert.Contains("$process.StandardOutput.ReadToEndAsync()", runner, StringComparison.Ordinal);
        Assert.Contains("Complete-RoleHostOutputCapture", runner, StringComparison.Ordinal);
        Assert.Contains("$RoleHost.Process.HasExited", runner, StringComparison.Ordinal);
        Assert.Contains("$RoleHost.StdoutTask.Wait($stdoutWaitMilliseconds)", runner, StringComparison.Ordinal);
        Assert.Contains("$RoleHost.StderrTask.Wait($stderrWaitMilliseconds)", runner, StringComparison.Ordinal);
        Assert.Contains("[int]$TimeoutMilliseconds = 5000", runner, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\"GEORAEPLAN_TEST_USERNAME\",\r\n            $LoginUsername",
            runner,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\"GEORAEPLAN_TEST_PASSWORD\",\r\n            $LoginPassword",
            runner,
            StringComparison.Ordinal);
        Assert.Contains(
            "Local\\GeoraePlan.MultiPc.LoginInput.$runId",
            runner,
            StringComparison.Ordinal);

        Assert.Contains(
            "$hasExternalLoginUsername -xor $hasExternalLoginPassword",
            runner,
            StringComparison.Ordinal);
        Assert.Contains("$loginUsername = \"admin\"", runner, StringComparison.Ordinal);
        Assert.Contains("New-Object byte[] 48", runner, StringComparison.Ordinal);
        Assert.Contains(
            "[System.Security.Cryptography.RandomNumberGenerator]::Create()",
            runner,
            StringComparison.Ordinal);
        Assert.Contains(
            "[Array]::Clear($passwordBytes, 0, $passwordBytes.Length)",
            runner,
            StringComparison.Ordinal);
        Assert.Contains(
            "-EnableEphemeralAdminBootstrap $useEphemeralAdminBootstrap",
            runner,
            StringComparison.Ordinal);
        Assert.Contains(
            "-EnableEphemeralAdminBootstrap $false",
            runner,
            StringComparison.Ordinal);
        Assert.Contains(
            "Remove-SeedUsersEnvironmentVariables -StartInfo $startInfo",
            runner,
            StringComparison.Ordinal);
        Assert.Contains(
            "[Diagnostics.Process]::Start($startInfo)",
            uiSmoke,
            StringComparison.Ordinal);
        Assert.Contains(
            "'GEORAEPLAN_MULTI_PC_E2E_ROLE'",
            uiSmoke,
            StringComparison.Ordinal);
        Assert.Contains(
            "'GEORAEPLAN_MULTI_PC_RUNTIME_ROOT'",
            uiSmoke,
            StringComparison.Ordinal);
        Assert.Contains(
            "'GEORAEPLAN_MULTI_PC_CERTIFICATION_ID'",
            uiSmoke,
            StringComparison.Ordinal);
        Assert.Contains("bootstrap-contract.json", runner, StringComparison.Ordinal);
        Assert.Contains(
            "Assert-EphemeralAdminBootstrapContract",
            uiSmoke,
            StringComparison.Ordinal);
        Assert.Contains(
            "Get-BootstrapDatabaseFileSetSha256",
            uiSmoke,
            StringComparison.Ordinal);
        Assert.Contains(
            "Remove-SeedUsersEnvironmentVariablesFromStartInfo",
            uiSmoke,
            StringComparison.Ordinal);
        Assert.Contains(
            "Remove-SeedUsersEnvironmentVariables -StartInfo $startInfo",
            runner,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "SeedUsers__UpdateExistingAdminPassword' = 'true'",
            runner,
            StringComparison.Ordinal);

        var snapshotBeforeRoleA = runner.IndexOf(
            "New-ServerDatabaseRollbackSnapshot `",
            StringComparison.Ordinal);
        var roleAStart = runner.IndexOf(
            "$roleHosts += Start-RoleHost `",
            snapshotBeforeRoleA,
            StringComparison.Ordinal);
        var ephemeralPasswordGeneration = runner.IndexOf(
            "New-Object byte[] 48",
            snapshotBeforeRoleA,
            StringComparison.Ordinal);
        var roleBStart = runner.IndexOf(
            "$roleHosts += Start-RoleHost `",
            roleAStart + 1,
            StringComparison.Ordinal);
        Assert.True(snapshotBeforeRoleA >= 0 && roleAStart > snapshotBeforeRoleA);
        Assert.True(
            ephemeralPasswordGeneration > snapshotBeforeRoleA &&
            ephemeralPasswordGeneration < roleAStart);
        Assert.True(roleBStart > roleAStart);
        var roleBLaunch = runner[roleBStart..runner.IndexOf(
            "$loginUsername = $null",
            roleBStart,
            StringComparison.Ordinal)];
        Assert.Contains(
            "-EnableEphemeralAdminBootstrap $false",
            roleBLaunch,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "-EnableEphemeralAdminBootstrap $useEphemeralAdminBootstrap",
            roleBLaunch,
            StringComparison.Ordinal);

        var seedOptions = File.ReadAllText(
            Path.Combine(
                projectRoot,
                "Server",
                "거래플랜.Server.Api",
                "Security",
                "SeedUsersOptions.cs"));
        var dbInitializer = File.ReadAllText(
            Path.Combine(
                projectRoot,
                "Server",
                "거래플랜.Server.Api",
                "Data",
                "DbInitializer.cs"));
        Assert.Contains(
            "public bool UpdateExistingAdminPassword { get; set; }",
            seedOptions,
            StringComparison.Ordinal);
        Assert.Contains(
            "public bool AdminOnlyBootstrap { get; set; }",
            seedOptions,
            StringComparison.Ordinal);
        Assert.Contains(
            "updatePasswordIfExists: seedUsersOptions.UpdateExistingAdminPassword",
            dbInitializer,
            StringComparison.Ordinal);
        Assert.Contains(
            "seedUsersOptions.AdminPassword = null;",
            dbInitializer,
            StringComparison.Ordinal);
        Assert.Contains(
            "EnvironmentVariableTarget.Process",
            dbInitializer,
            StringComparison.Ordinal);

        var parentCredentialRead = runner.IndexOf(
            "GetEnvironmentVariable(\"GEORAEPLAN_TEST_USERNAME\", \"Process\")",
            StringComparison.Ordinal);
        var parentCredentialClear = runner.IndexOf(
            "foreach ($credentialEnvironmentName in $credentialEnvironmentNames)",
            parentCredentialRead,
            StringComparison.Ordinal);
        var firstRoleStart = runner.IndexOf(
            "$roleHosts += Start-RoleHost",
            parentCredentialClear,
            StringComparison.Ordinal);
        Assert.True(parentCredentialRead >= 0);
        Assert.True(parentCredentialClear > parentCredentialRead);
        Assert.True(firstRoleStart > parentCredentialClear);

        var roleCredentialRead = runner.IndexOf(
            "`$roleLoginUsername = [Environment]::GetEnvironmentVariable",
            StringComparison.Ordinal);
        var roleCredentialClear = runner.IndexOf(
            "[Environment]::SetEnvironmentVariable('GEORAEPLAN_TEST_USERNAME', `$null, 'Process')",
            roleCredentialRead,
            StringComparison.Ordinal);
        var roleSmokeInvocation = runner.IndexOf(
            "& $uiSmokeLiteral @invokeParameters",
            roleCredentialClear,
            StringComparison.Ordinal);
        Assert.True(roleCredentialRead >= 0);
        Assert.True(roleCredentialClear > roleCredentialRead);
        Assert.True(roleSmokeInvocation > roleCredentialClear);

        Assert.Contains("$ownedServerStartTimeUtcTicks", runner, StringComparison.Ordinal);
        Assert.Contains("-AttestedServerProcessId $ownedServerProcessId", runner, StringComparison.Ordinal);
        Assert.Contains(
            "-AttestedServerStartTimeUtcTicks $ownedServerStartTimeUtcTicks",
            runner,
            StringComparison.Ordinal);
        Assert.Contains("$process.WaitForExit(10000)", runner, StringComparison.Ordinal);
        Assert.Contains("function Test-ExactProcessStillAlive", runner, StringComparison.Ordinal);
        Assert.Contains(
            "$candidate.StartTime.ToUniversalTime().Ticks -eq",
            runner,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (Test-ExactProcessStillAlive `",
            runner,
            StringComparison.Ordinal);
        Assert.Contains("Wait-LoopbackPortReleased", runner, StringComparison.Ordinal);
        Assert.Contains("Wait-DatabaseFileSetUnlocked", runner, StringComparison.Ordinal);
        Assert.Contains("runtime-release-before-rollback", runner, StringComparison.Ordinal);
        Assert.Contains("Stop-ExactProcessAndWait", uiSmoke, StringComparison.Ordinal);
        Assert.Contains("server-process-cleanup", uiSmoke, StringComparison.Ordinal);

        var finalCredentialClear = runner.LastIndexOf(
            "foreach ($credentialEnvironmentName in $credentialEnvironmentNames)",
            StringComparison.Ordinal);
        var finalizationStart = runner.LastIndexOf(
            "finally {",
            finalCredentialClear,
            StringComparison.Ordinal);
        Assert.True(finalCredentialClear >= 0);
        Assert.True(finalizationStart >= 0);
        var finalization = runner[finalizationStart..];
        var exactProcessStop = finalization.IndexOf(
            "Stop-ExactOwnedProcesses `",
            StringComparison.Ordinal);
        var portRelease = finalization.IndexOf(
            "Wait-LoopbackPortReleased",
            exactProcessStop,
            StringComparison.Ordinal);
        var databaseHandleRelease = finalization.IndexOf(
            "Wait-DatabaseFileSetUnlocked",
            portRelease,
            StringComparison.Ordinal);
        var releaseConfirmation = finalization.IndexOf(
            "$runtimeReleaseConfirmed = $true",
            databaseHandleRelease,
            StringComparison.Ordinal);
        var rollbackGate = finalization.IndexOf(
            "if (-not $runtimeReleaseConfirmed)",
            releaseConfirmation,
            StringComparison.Ordinal);
        var databaseRollback = finalization.IndexOf(
            "Restore-ServerDatabaseRollbackSnapshot `",
            rollbackGate,
            StringComparison.Ordinal);
        Assert.True(exactProcessStop >= 0);
        Assert.True(portRelease > exactProcessStop);
        Assert.True(databaseHandleRelease > portRelease);
        Assert.True(releaseConfirmation > databaseHandleRelease);
        Assert.True(rollbackGate > releaseConfirmation);
        Assert.True(databaseRollback > rollbackGate);

        Assert.Contains(
            "in-process ViewModel/API/DB integration (not UIA interaction evidence)",
            runner,
            StringComparison.Ordinal);
        Assert.Contains("login-main-window-uia", runner, StringComparison.Ordinal);
        Assert.Contains(
            "desktop-ui-smoke-*.json",
            runner,
            StringComparison.Ordinal);
        Assert.Contains(
            "$requiredUiSteps = @(\"login-window\", \"login-submit\", \"main-buttons\")",
            runner,
            StringComparison.Ordinal);
        Assert.Contains(
            "did not exercise the actual login-window credential input path",
            runner,
            StringComparison.Ordinal);
        Assert.Contains("DeferredInteractionGate", runner, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "-Name \"in-app-rental-billing-conflict\"",
            runner,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "-Name \"in-app-rental-asset-conflict\"",
            runner,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "-Name \"in-app-inventory-transfer-conflict\"",
            runner,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedSyncDeviceId_RequiresMachinePrefixAndNFormatGuid()
    {
        var validator = typeof(global::거래플랜.Desktop.App.MainWindow).GetMethod(
            "IsGeneratedSyncDeviceId",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(validator);
        var guid = Guid.NewGuid();
        Assert.True((bool)validator.Invoke(null, [$"PC-A:{guid:N}"])!);
        Assert.False((bool)validator.Invoke(null, [$"{guid:N}"])!);
        Assert.False((bool)validator.Invoke(null, [$"PC-A:{guid:D}"])!);
        Assert.False((bool)validator.Invoke(null, ["PC-A:not-a-guid"])!);
    }

    [Fact]
    public void Runner_RequiresPhysicalIsolationCertificationAndExplicitMode()
    {
        var projectRoot = FindProjectRoot();
        var runner = File.ReadAllText(
            Path.Combine(
                projectRoot,
                "테스트 시행",
                "Invoke-MultiPcDesktopE2E.ps1"));
        var contractRunner = File.ReadAllText(
            Path.Combine(
                projectRoot,
                "테스트 시행",
                "Invoke-MultiPcConflictCheck.ps1"));
        var preparer = File.ReadAllText(
            Path.Combine(
                projectRoot,
                "테스트 시행",
                "준비-다중PC-검증.ps1"));
        var uiSmoke = File.ReadAllText(
            Path.Combine(
                projectRoot,
                "tools",
                "verification",
                "Invoke-GeoraePlanDesktopUiSmoke.ps1"));
        var mainWindowXaml = File.ReadAllText(
            Path.Combine(
                projectRoot,
                "Desktop",
                "거래플랜.Desktop.App",
                "MainWindow.xaml"));

        Assert.Contains("Read-RuntimeCertificationMarker", runner, StringComparison.Ordinal);
        Assert.Contains("ExpectedInstanceSha256", runner, StringComparison.Ordinal);
        Assert.Contains("Assert-ServerProcessOwnedByRoleHost", runner, StringComparison.Ordinal);
        Assert.Contains("preflight-no-running-runtime", runner, StringComparison.Ordinal);
        Assert.Contains("App-PC-A", runner, StringComparison.Ordinal);
        Assert.Contains("App-PC-B", runner, StringComparison.Ordinal);
        Assert.Contains("DeviceIdHash", runner, StringComparison.Ordinal);
        Assert.Contains("InstallRootHash", runner, StringComparison.Ordinal);
        Assert.Contains("AppRootHash", runner, StringComparison.Ordinal);
        Assert.Contains("TempRootHash", runner, StringComparison.Ordinal);
        Assert.Contains("DownloadsRootHash", runner, StringComparison.Ordinal);
        Assert.Contains("process-and-listener-cleanup", runner, StringComparison.Ordinal);
        Assert.Contains("ownedStartTimeTicks", runner, StringComparison.Ordinal);
        Assert.Contains("server-db-rollback", runner, StringComparison.Ordinal);
        Assert.Contains("source-appsettings-rollback", runner, StringComparison.Ordinal);
        Assert.Contains("transient-backup-cleanup", runner, StringComparison.Ordinal);
        Assert.Contains(".gp-stage-*", runner, StringComparison.Ordinal);
        Assert.Contains(".gp-validate-*", runner, StringComparison.Ordinal);
        Assert.Contains(
            "EvidenceDirectory는 신규 또는 빈 디렉터리여야 합니다",
            runner,
            StringComparison.Ordinal);
        Assert.Contains("[ValidateSet(\"Contract\", \"DesktopE2E\", \"All\")]", contractRunner, StringComparison.Ordinal);
        Assert.Contains("[string]$Mode = \"Contract\"", contractRunner, StringComparison.Ordinal);
        Assert.Contains(
            """DELETE FROM \"Settings\" WHERE \"Key\" = 'Sync.DeviceId'""",
            preparer,
            StringComparison.Ordinal);
        Assert.Contains("GEORAEPLAN_TEMP_ROOT", preparer, StringComparison.Ordinal);
        Assert.Contains("GEORAEPLAN_DOWNLOADS_ROOT", preparer, StringComparison.Ordinal);
        Assert.Contains(
            "[System.Windows.Automation.AutomationElement]::FromHandle",
            uiSmoke,
            StringComparison.Ordinal);
        Assert.Contains("$process.WaitForExit(30000)", uiSmoke, StringComparison.Ordinal);
        Assert.Contains("function Test-IsLoginWindow", uiSmoke, StringComparison.Ordinal);
        Assert.Contains("function Test-IsMainWindow", uiSmoke, StringComparison.Ordinal);
        Assert.Equal(
            1,
            mainWindowXaml.Split(
                "x:Name=\"CustomerSettingsButton\"",
                StringSplitOptions.None).Length - 1);
        Assert.Equal(
            1,
            mainWindowXaml.Split(
                "x:Name=\"RentalManagementButton\"",
                StringSplitOptions.None).Length - 1);
        Assert.Contains(
            "Find-FirstByAutomationId -Root $Window -AutomationId 'CustomerSettingsButton'",
            uiSmoke,
            StringComparison.Ordinal);
        Assert.Contains(
            "Find-FirstByAutomationId -Root $Window -AutomationId 'RentalManagementButton'",
            uiSmoke,
            StringComparison.Ordinal);
        Assert.Contains(
            "return $null -ne $customerSettingsButton -and $null -ne $rentalManagementButton",
            uiSmoke,
            StringComparison.Ordinal);
        Assert.Contains(
            "-or (Test-IsLoginWindow -Window $startupWindow.Window)",
            uiSmoke,
            StringComparison.Ordinal);

        var waitLoginStart = uiSmoke.IndexOf(
            "function Wait-LoginOrMainWindow",
            StringComparison.Ordinal);
        var waitMainStart = uiSmoke.IndexOf(
            "function Wait-MainWindowOnly",
            waitLoginStart,
            StringComparison.Ordinal);
        var nextFunctionStart = uiSmoke.IndexOf(
            "function Find-RedirectedAppProcess",
            waitMainStart,
            StringComparison.Ordinal);
        Assert.True(waitLoginStart >= 0 && waitMainStart > waitLoginStart);
        Assert.True(nextFunctionStart > waitMainStart);

        var waitLoginBody = uiSmoke[waitLoginStart..waitMainStart];
        var waitMainBody = uiSmoke[waitMainStart..nextFunctionStart];
        Assert.Contains(
            "if (Test-IsMainWindow -Window $mainWindow)",
            waitLoginBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "if ($null -ne $loginWindow -and (Test-IsLoginWindow -Window $loginWindow))",
            waitLoginBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "return [pscustomobject]@{ Kind = 'Main'; Window = $mainWindow }",
            waitLoginBody,
            StringComparison.Ordinal);
        Assert.DoesNotContain("IndexOf('로그인'", waitLoginBody, StringComparison.Ordinal);
        Assert.Contains(
            "if ($null -ne $mainWindow -and (Test-IsMainWindow -Window $mainWindow))",
            waitMainBody,
            StringComparison.Ordinal);

        var loginLookup = waitLoginBody.IndexOf(
            "$loginWindow = Get-ProcessWindow -ProcessId $ProcessId -Name '로그인'",
            StringComparison.Ordinal);
        var mainLookup = waitLoginBody.IndexOf(
            "$mainWindow = Get-ProcessWindow -ProcessId $ProcessId -Name '거래플랜'",
            loginLookup,
            StringComparison.Ordinal);
        Assert.True(loginLookup >= 0 && mainLookup > loginLookup);
    }

    [Fact]
    public void PendingInventoryTransferAndRentalAssetFixtures_AreScopeBoundAndUseSemanticEvidence()
    {
        var projectRoot = FindProjectRoot();
        var inventoryTransfer = File.ReadAllText(
            Path.Combine(
                projectRoot,
                "Desktop",
                "거래플랜.Desktop.App",
                "MainWindow.MultiPcE2E.InventoryTransfer.cs"));
        var rentalAsset = File.ReadAllText(
            Path.Combine(
                projectRoot,
                "Desktop",
                "거래플랜.Desktop.App",
                "MainWindow.MultiPcE2E.RentalAsset.cs"));
        var runner = File.ReadAllText(
            Path.Combine(
                projectRoot,
                "테스트 시행",
                "Invoke-MultiPcDesktopE2E.ps1"));

        Assert.Contains("movement.Note.Contains(marker)", inventoryTransfer, StringComparison.Ordinal);
        Assert.Contains("movements[0].MovementType, \"TransferOutManual\"", inventoryTransfer, StringComparison.Ordinal);
        Assert.Contains("movement.IsActive", inventoryTransfer, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "movement.Note.Contains(\"(\" + transferNumber + \")\")",
            inventoryTransfer,
            StringComparison.Ordinal);
        Assert.DoesNotContain("layer.Id:D", inventoryTransfer, StringComparison.Ordinal);
        Assert.DoesNotContain("serial.Id:D", inventoryTransfer, StringComparison.Ordinal);
        Assert.Contains("GetMultiPcInventoryTransferUnitLayerEligibleItemIdsAsync(", inventoryTransfer, StringComparison.Ordinal);
        Assert.Contains("layer.RemainingQuantity >= 1m", inventoryTransfer, StringComparison.Ordinal);
        Assert.Contains("layer.RemainingQuantity > 0m &&", inventoryTransfer, StringComparison.Ordinal);
        Assert.Contains("layer.RemainingQuantity < 1m", inventoryTransfer, StringComparison.Ordinal);
        Assert.Contains("eligibleItemIds.ExceptWith(itemIdsWithSubUnitPositiveLayer);", inventoryTransfer, StringComparison.Ordinal);
        Assert.DoesNotContain(".MinAsync();", inventoryTransfer, StringComparison.Ordinal);
        Assert.Contains("!eligibleLayerItemIds.Contains(candidateId)", inventoryTransfer, StringComparison.Ordinal);
        Assert.Contains("GetItemsForInventoryTransferAsync(_session)", inventoryTransfer, StringComparison.Ordinal);
        Assert.Contains("OwnedInventoryTransferScope", inventoryTransfer, StringComparison.Ordinal);
        Assert.Contains("IsMultiPcInventoryTransferSignalForScope", inventoryTransfer, StringComparison.Ordinal);
        Assert.Matches(
            "\"transfer-a-created\\.json\",\\s*" +
            "transferId,\\s*" +
            "created\\.Revision,\\s*" +
            "created\\.Memo,",
            inventoryTransfer);
        Assert.DoesNotMatch(
            "\"transfer-a-created\\.json\",\\s*" +
            "transferId,\\s*" +
            "created\\.Revision,\\s*" +
            "marker,",
            inventoryTransfer);
        Assert.DoesNotContain("Expected revision mismatch.", inventoryTransfer, StringComparison.Ordinal);
        Assert.Contains("string.IsNullOrWhiteSpace(transfer.ReceivedByUsername)", inventoryTransfer, StringComparison.Ordinal);
        Assert.Contains("HasNeutralMultiPcPendingReceiptValues(line)", inventoryTransfer, StringComparison.Ordinal);
        Assert.Contains("HasExpectedMultiPcPendingTransferAggregate(", inventoryTransfer, StringComparison.Ordinal);
        Assert.Contains("line.ReceivedQuantity ?? line.Quantity", inventoryTransfer, StringComparison.Ordinal);
        Assert.Contains("line.QuantityDifference ?? (receivedQuantity - line.Quantity)", inventoryTransfer, StringComparison.Ordinal);
        Assert.Contains("receivedQuantity == line.Quantity", inventoryTransfer, StringComparison.Ordinal);
        Assert.Contains("quantityDifference == 0m", inventoryTransfer, StringComparison.Ordinal);
        Assert.DoesNotContain("line.ReceivedQuantity is null", inventoryTransfer, StringComparison.Ordinal);
        Assert.DoesNotContain("line.QuantityDifference is null", inventoryTransfer, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfirmInventoryTransferReceiptAsync", inventoryTransfer, StringComparison.Ordinal);
        Assert.DoesNotContain("RejectInventoryTransferAsync", inventoryTransfer, StringComparison.Ordinal);
        Assert.Contains("createdSignal.SourceQuantity.HasValue", inventoryTransfer, StringComparison.Ordinal);
        Assert.Contains("afterCreate.TransferMovementCount == 0", inventoryTransfer, StringComparison.Ordinal);
        Assert.Contains("afterWrite.HasExactSingleSourceTransferOut", inventoryTransfer, StringComparison.Ordinal);
        Assert.Contains("afterRetryPull.Equals(afterWrite)", inventoryTransfer, StringComparison.Ordinal);
        Assert.Contains("deleted.Revision > latest.Revision", inventoryTransfer, StringComparison.Ordinal);
        Assert.Contains("restored.SourceQuantity == afterCreate.SourceQuantity + 1m", inventoryTransfer, StringComparison.Ordinal);
        Assert.Contains("restored.LayerHash == afterCreate.LayerHash", inventoryTransfer, StringComparison.Ordinal);
        Assert.Contains("restored.MovementHash == afterCreate.MovementHash", inventoryTransfer, StringComparison.Ordinal);
        Assert.Contains(")).Equals(restored)", inventoryTransfer, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "deleted.Revision > retried.Revision && restored.Equals(before)",
            inventoryTransfer,
            StringComparison.Ordinal);

        Assert.Contains("LastBillingProfileId.HasValue", rentalAsset, StringComparison.Ordinal);
        Assert.Contains("vm.EditItemId = null;", rentalAsset, StringComparison.Ordinal);
        Assert.Contains("vm.EditItemName = string.Empty;", rentalAsset, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "vm.EditItemName = \"MULTIPC-PROFILE-FREE-ASSET\";",
            rentalAsset,
            StringComparison.Ordinal);
        Assert.Contains("RentalAssetAssignmentHistories", rentalAsset, StringComparison.Ordinal);
        Assert.Contains("IncludedAssetIds", rentalAsset, StringComparison.Ordinal);
        Assert.Contains("HasNoMultiPcRentalAssetDependenciesAsync", rentalAsset, StringComparison.Ordinal);
        Assert.Contains("HasNoMultiPcRentalAssetReferenceInBillingTemplate", rentalAsset, StringComparison.Ordinal);
        Assert.Contains("afterConflict.Revision == bWritten.Revision", rentalAsset, StringComparison.Ordinal);
        Assert.Contains("string.Equals(afterConflict.Notes, $\"{marker}|B-WINS\"", rentalAsset, StringComparison.Ordinal);
        Assert.Contains("SuppressExplicitSaveConflictDialog = true", rentalAsset, StringComparison.Ordinal);
        Assert.Contains("staleVm.EditExpectedRevision == staleVm.SelectedRow.Source.Revision", rentalAsset, StringComparison.Ordinal);
        Assert.Contains("LoadAndSelectAssetAsync(assetId).WaitAsync(TimeSpan.FromSeconds(30))", rentalAsset, StringComparison.Ordinal);
        Assert.Contains("SaveCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(30))", rentalAsset, StringComparison.Ordinal);
        Assert.Contains("WaitForEditAutoSaveQuiescenceAsync()", rentalAsset, StringComparison.Ordinal);
        Assert.Contains("!staleVm.IsEditAutoSaveOwnershipActive", rentalAsset, StringComparison.Ordinal);
        Assert.Contains("!staleVm.HasPendingChanges", rentalAsset, StringComparison.Ordinal);
        Assert.Contains("stabilizedRetry.Revision == retried.Revision", rentalAsset, StringComparison.Ordinal);
        Assert.Contains("stabilizedRetryOutbox.PendingCount == 0", rentalAsset, StringComparison.Ordinal);
        Assert.Contains("staleVm.SelectedRow is null", rentalAsset, StringComparison.Ordinal);
        Assert.Contains("staleVm.EditId != assetId", rentalAsset, StringComparison.Ordinal);
        Assert.Contains("\"asset-a-clean.json\", assetId, deleted.Revision", rentalAsset, StringComparison.Ordinal);
        Assert.Contains("IsMultiPcRentalAssetIdempotentDeleteNoOp", rentalAsset, StringComparison.Ordinal);
        Assert.Contains("\"rental-asset-server-idempotent-stale-delete-no-op\"", rentalAsset, StringComparison.Ordinal);
        Assert.Contains("\"rental-asset-idempotent-delete-propagation\"", rentalAsset, StringComparison.Ordinal);
        Assert.DoesNotContain("\"rental-asset-actual-server-stale-delete\"", rentalAsset, StringComparison.Ordinal);
        Assert.Contains("\"rental-asset-server-idempotent-stale-delete-no-op\"", runner, StringComparison.Ordinal);
        Assert.Contains("\"rental-asset-idempotent-delete-propagation\"", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("\"rental-asset-actual-server-stale-delete\"", runner, StringComparison.Ordinal);

        var pendingRentalAssetEdit = rentalAsset.IndexOf(
            "staleVm.EditNotes = pending;",
            StringComparison.Ordinal);
        var cancelRentalAssetAutoSave = rentalAsset.IndexOf(
            "staleVm.CancelPendingEditAutoSave();",
            pendingRentalAssetEdit,
            StringComparison.Ordinal);
        var publishRentalAssetStagedSignal = rentalAsset.IndexOf(
            "\"asset-a-staged.json\"",
            cancelRentalAssetAutoSave,
            StringComparison.Ordinal);
        Assert.True(pendingRentalAssetEdit >= 0);
        Assert.True(cancelRentalAssetAutoSave > pendingRentalAssetEdit);
        Assert.True(publishRentalAssetStagedSignal > cancelRentalAssetAutoSave);

        var retrySave = rentalAsset.IndexOf(
            "SaveCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(30))",
            StringComparison.Ordinal);
        var retryQuiescence = rentalAsset.IndexOf(
            "WaitForEditAutoSaveQuiescenceAsync()",
            retrySave,
            StringComparison.Ordinal);
        var publishRetriedSignal = rentalAsset.IndexOf(
            "\"asset-a-retried.json\"",
            retryQuiescence,
            StringComparison.Ordinal);
        Assert.True(retrySave >= 0);
        Assert.True(retryQuiescence > retrySave);
        Assert.True(publishRetriedSignal > retryQuiescence);

        var purgeSync = rentalAsset.IndexOf(
            "SyncMultiPcAndRequireCleanAsync(\"A-rental-asset-pull-purge\")",
            StringComparison.Ordinal);
        var purgeQuiescence = rentalAsset.IndexOf(
            "WaitForEditAutoSaveQuiescenceAsync()",
            purgeSync,
            StringComparison.Ordinal);
        var purgeEditorReset = rentalAsset.IndexOf(
            "staleVm.SelectedRow is null",
            purgeQuiescence,
            StringComparison.Ordinal);
        var publishCleanSignal = rentalAsset.IndexOf(
            "\"asset-a-clean.json\"",
            purgeEditorReset,
            StringComparison.Ordinal);
        Assert.True(purgeSync >= 0);
        Assert.True(purgeQuiescence > purgeSync);
        Assert.True(purgeEditorReset > purgeQuiescence);
        Assert.True(publishCleanSignal > purgeEditorReset);

        Assert.Contains("afterConflict.Revision == bWritten.Revision", inventoryTransfer, StringComparison.Ordinal);
        Assert.Contains("string.Equals(afterConflict.Memo, marker + \"|B-WINS\"", inventoryTransfer, StringComparison.Ordinal);
        Assert.Contains("staleVm.HasExternalTransferConflict", inventoryTransfer, StringComparison.Ordinal);
        Assert.Contains("!staleVm.IsExternalTransferUnavailable", inventoryTransfer, StringComparison.Ordinal);
        Assert.Contains(
            "staleVm.DiscardDraftAndReloadLatestTransferAsync()",
            inventoryTransfer,
            StringComparison.Ordinal);
        Assert.Contains(
            "staleVm.TransferId == Guid.Empty",
            inventoryTransfer,
            StringComparison.Ordinal);
        Assert.Contains(
            "var bDeleted = await WaitForMultiPcPayloadAsync<MultiPcSignal>(",
            inventoryTransfer,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsMultiPcInventoryTransferIdempotentDeleteNoOp(",
            inventoryTransfer,
            StringComparison.Ordinal);
        Assert.Contains(
            "push is { AcceptedCount: 1, ConflictCount: 0 }",
            inventoryTransfer,
            StringComparison.Ordinal);
        Assert.Contains(
            "push.AcceptedRevisions.Count == 1",
            inventoryTransfer,
            StringComparison.Ordinal);
        Assert.Contains(
            "accepted.EntityId == id",
            inventoryTransfer,
            StringComparison.Ordinal);
        Assert.Contains(
            "accepted.Revision == tombstoneRevision",
            inventoryTransfer,
            StringComparison.Ordinal);
        Assert.Contains(
            "deleted.Revision == bDeleted.Revision",
            inventoryTransfer,
            StringComparison.Ordinal);
        Assert.Contains(
            "string.Equals(deleted.Memo, retried.Memo",
            inventoryTransfer,
            StringComparison.Ordinal);
        Assert.Contains("deleted.IsDeleted &&", inventoryTransfer, StringComparison.Ordinal);
        Assert.Contains("!deleted.IsDirty &&", inventoryTransfer, StringComparison.Ordinal);
        Assert.Contains(
            "\"inventory-transfer-server-idempotent-stale-delete-no-op\"",
            inventoryTransfer,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"inventory-transfer-idempotent-delete-propagation\"",
            inventoryTransfer,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Runner-owned server did not reject stale inventory-transfer delete.",
            inventoryTransfer,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\"inventory-transfer-actual-server-stale-delete\"",
            inventoryTransfer,
            StringComparison.Ordinal);

        Assert.Contains("$activeFixtureSignals.Count -ne 49", runner, StringComparison.Ordinal);
        var fixtureSignalManifest =
            System.Text.RegularExpressions.Regex.Match(
                runner,
                @"\$activeFixtureSignals\s*=\s*@\((?<body>[\s\S]*?)\)\s*\$missingSignals");
        Assert.True(fixtureSignalManifest.Success);
        var fixtureSignalNames =
            System.Text.RegularExpressions.Regex.Matches(
                    fixtureSignalManifest.Groups["body"].Value,
                    "\"(?<name>[^\"]+\\.json)\"")
                .Select(match => match.Groups["name"].Value)
                .ToList();
        Assert.Equal(49, fixtureSignalNames.Count);
        Assert.Equal(
            49,
            fixtureSignalNames
                .Distinct(StringComparer.Ordinal)
                .Count());
        Assert.DoesNotMatch(
            @"(?m)^\s*,",
            fixtureSignalManifest.Groups["body"].Value);
        Assert.Contains("$signalPayload.Nonce -ne $nonce", runner, StringComparison.Ordinal);
        Assert.Contains("$signalPayload.ProcessId -ne $expectedProcessId", runner, StringComparison.Ordinal);
        Assert.Contains("$entityIdByScenario", runner, StringComparison.Ordinal);
        Assert.Contains("$capturedAtText -cmatch", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("TryParseExact(", runner, StringComparison.Ordinal);
        Assert.Contains("$transferScopeContract", runner, StringComparison.Ordinal);
        Assert.Contains("$transferMarker", runner, StringComparison.Ordinal);
        Assert.Contains("$transferConflictRevision -ne $transferWrittenRevision", runner, StringComparison.Ordinal);
        Assert.Contains("$assetDeletedRevision", runner, StringComparison.Ordinal);
        Assert.Contains("$rentalDeletedRevision", runner, StringComparison.Ordinal);
        Assert.Contains("\"A-RETRY-RENTAL-\" + $runId", runner, StringComparison.Ordinal);
        Assert.Contains("$transferDeletedRevision -le $transferRetriedRevision", runner, StringComparison.Ordinal);
        Assert.Contains(
            "\"rental-billing-server-idempotent-stale-delete-no-op\"",
            runner,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"rental-billing-idempotent-delete-propagation\"",
            runner,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\"rental-billing-actual-server-stale-delete\"",
            runner,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"inventory-transfer-server-idempotent-stale-delete-no-op\"",
            runner,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"inventory-transfer-idempotent-delete-propagation\"",
            runner,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\"inventory-transfer-actual-server-stale-delete\"",
            runner,
            StringComparison.Ordinal);

        var rentalBilling = File.ReadAllText(
            Path.Combine(
                projectRoot,
                "Desktop",
                "거래플랜.Desktop.App",
                "MainWindow.MultiPcE2E.RentalBilling.cs"));
        Assert.Contains("afterConflict.Revision == bWritten.Revision", rentalBilling, StringComparison.Ordinal);
        Assert.Contains("string.Equals(afterConflict.Notes, winningNotes", rentalBilling, StringComparison.Ordinal);
        Assert.Contains("string.Equals(retried.Notes, retryNotes", rentalBilling, StringComparison.Ordinal);
        Assert.Contains("deleted.Revision > latest.Revision", rentalBilling, StringComparison.Ordinal);
        Assert.Contains(
            "var bDeleted = await WaitForMultiPcPayloadAsync<MultiPcSignal>(context, \"rental-b-deleted.json\"",
            rentalBilling,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsMultiPcRentalBillingIdempotentDeleteNoOp(staleDeletePush, profileId, bDeleted.Revision)",
            rentalBilling,
            StringComparison.Ordinal);
        Assert.Contains("push is { AcceptedCount: 1, ConflictCount: 0 }", rentalBilling, StringComparison.Ordinal);
        Assert.Contains("push.AcceptedRevisions.Count == 1", rentalBilling, StringComparison.Ordinal);
        Assert.Contains("accepted.EntityId == id", rentalBilling, StringComparison.Ordinal);
        Assert.Contains("accepted.Revision == tombstoneRevision", rentalBilling, StringComparison.Ordinal);
        Assert.Contains("deleted.Revision == bDeleted.Revision", rentalBilling, StringComparison.Ordinal);
        Assert.Contains("string.Equals(deleted.Notes, retried.Notes", rentalBilling, StringComparison.Ordinal);
        Assert.Contains("deleted.IsDeleted &&", rentalBilling, StringComparison.Ordinal);
        Assert.Contains(
            "\"rental-billing-server-idempotent-stale-delete-no-op\"",
            rentalBilling,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"accepted=1; conflicts=0; accepted revision equals the existing PC-B tombstone revision\"",
            rentalBilling,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"rental-billing-idempotent-delete-propagation\"",
            rentalBilling,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"PC-B tombstone revision/content/deleted state preserved after pull; dirty=false\"",
            rentalBilling,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Runner-owned server did not reject the stale rental billing delete.",
            rentalBilling,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\"rental-billing-actual-server-stale-delete\"",
            rentalBilling,
            StringComparison.Ordinal);
        Assert.Equal(
            3,
            System.Text.RegularExpressions.Regex.Matches(
                rentalBilling,
                @"\b(?:vm|staleVm)\.LinkAssetsLater = true;").Count);
        Assert.Contains(
            "vm.SelectedTemplateItem!.DisplayItemName = MultiPcRentalBillingTemplateItemName;",
            rentalBilling,
            StringComparison.Ordinal);
        Assert.Contains("HasOnlyMultiPcRentalBillingTemplate", rentalBilling, StringComparison.Ordinal);
        Assert.Contains("HasNoMultiPcRentalBillingDependenciesAsync", rentalBilling, StringComparison.Ordinal);
        Assert.Contains("invoice.LinkedRentalBillingProfileId == profileId", rentalBilling, StringComparison.Ordinal);
        Assert.Contains("transaction.LinkedRentalBillingProfileId == profileId", rentalBilling, StringComparison.Ordinal);
        Assert.Contains("asset.LastBillingProfileId == profileId", rentalBilling, StringComparison.Ordinal);
        Assert.Contains("history.BillingProfileId == profileId", rentalBilling, StringComparison.Ordinal);
        Assert.Contains("log.BillingProfileId == profileId", rentalBilling, StringComparison.Ordinal);
        Assert.Contains(
            "conflict.Reason.StartsWith(\"Expected revision mismatch.\"",
            rentalBilling,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RentalBillingStaleDraftSettlement_WaitsForStableUiWinnerAndPersistedStaleDraft()
    {
        var projectRoot = FindProjectRoot();
        var source = File.ReadAllText(
            Path.Combine(
                projectRoot,
                "Desktop",
                "거래플랜.Desktop.App",
                "MainWindow.MultiPcE2E.RentalBilling.cs"));

        var syncIndex = source.IndexOf(
            "await SyncMultiPcAndRequireCleanAsync(\"A-rental-pull-winner\");",
            StringComparison.Ordinal);
        var settlementIndex = source.IndexOf(
            "var settlement = await WaitForMultiPcRentalBillingStaleDraftSettlementAsync(",
            StringComparison.Ordinal);
        var assertionIndex = source.IndexOf(
            "settlement.DraftRevision == stale.Revision",
            StringComparison.Ordinal);

        Assert.True(syncIndex >= 0 && settlementIndex > syncIndex && assertionIndex > settlementIndex);
        Assert.Contains("DispatcherPriority.ContextIdle", source, StringComparison.Ordinal);
        Assert.Contains("requiredStableUiObservations = 3", source, StringComparison.Ordinal);
        Assert.Contains("RunMultiPcRentalBillingBoundedOperationAsync(", source, StringComparison.Ordinal);
        Assert.Matches(
            @"DispatcherPriority\.ContextIdle,\s+timeoutToken\)",
            source);
        Assert.Contains("FirstOrDefaultAsync(current => current.Id == profileId, timeoutToken)", source, StringComparison.Ordinal);
        Assert.Contains("FirstOrDefaultAsync(timeoutToken)", source, StringComparison.Ordinal);
        Assert.Contains("var stagedDraftPersisted = await staleVm.FlushAutoSaveAsync();", source, StringComparison.Ordinal);
        Assert.Contains("stagedDraftPersisted &&", source, StringComparison.Ordinal);
        Assert.Contains("var currentEditorDraftPersisted = await vm.FlushAutoSaveAsync(timeoutToken);", source, StringComparison.Ordinal);
        Assert.Contains("tracker.MarkCurrentEditorDraftPersisted(currentEditorDraftPersisted);", source, StringComparison.Ordinal);
        Assert.Contains("Task.Delay(TimeSpan.FromMilliseconds(100), timeoutToken)", source, StringComparison.Ordinal);
        Assert.Contains("db.RentalBillingProfiles", source, StringComparison.Ordinal);
        Assert.Contains("db.Settings", source, StringComparison.Ordinal);
        Assert.Contains("observation.SelectedRevision == winningRevision", source, StringComparison.Ordinal);
        Assert.Contains("observation.DraftRevision == staleRevision", source, StringComparison.Ordinal);
        Assert.DoesNotContain("last?.EditorNotes", source, StringComparison.Ordinal);
        Assert.DoesNotContain("last?.LocalNotes", source, StringComparison.Ordinal);
        Assert.DoesNotContain("last?.DraftNotes", source, StringComparison.Ordinal);
        Assert.DoesNotContain("password=", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("username=", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DateTimeOffset.UtcNow.Add(timeout)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Thread.Sleep", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RentalBillingStaleDraftSettlement_RequiresEveryWinnerAndDraftInvariant()
    {
        var profileId = Guid.NewGuid();
        const long staleRevision = 41;
        const long winningRevision = 42;
        const string pendingNotes = "A-PENDING";
        const string winningNotes = "B-WINS";
        var settled = new global::거래플랜.Desktop.App.MainWindow.MultiPcRentalBillingSettlementObservation(
            IsBusy: false,
            SelectedProfileId: profileId,
            SelectedRevision: winningRevision,
            EditorProfileId: profileId,
            EditorNotes: pendingNotes,
            LocalProfileId: profileId,
            LocalRevision: winningRevision,
            LocalNotes: winningNotes,
            LocalIsDirty: false,
            LocalIsDeleted: false,
            DraftProfileId: profileId,
            DraftRevision: staleRevision,
            DraftNotes: pendingNotes);

        static bool IsSettled(
            global::거래플랜.Desktop.App.MainWindow.MultiPcRentalBillingSettlementObservation observation,
            Guid profileId,
            long staleRevision,
            long winningRevision,
            string pendingNotes,
            string winningNotes)
            => global::거래플랜.Desktop.App.MainWindow.IsMultiPcRentalBillingStaleDraftSettlement(
                observation,
                profileId,
                staleRevision,
                winningRevision,
                pendingNotes,
                winningNotes);

        Assert.True(IsSettled(settled, profileId, staleRevision, winningRevision, pendingNotes, winningNotes));

        var unsettled = new[]
        {
            settled with { IsBusy = true },
            settled with { SelectedProfileId = Guid.NewGuid() },
            settled with { SelectedRevision = staleRevision },
            settled with { EditorNotes = winningNotes },
            settled with { LocalRevision = staleRevision },
            settled with { LocalNotes = pendingNotes },
            settled with { LocalIsDirty = true },
            settled with { LocalIsDeleted = true },
            settled with { DraftProfileId = Guid.NewGuid() },
            settled with { DraftRevision = winningRevision },
            settled with { DraftNotes = winningNotes }
        };

        Assert.All(
            unsettled,
            observation => Assert.False(
                IsSettled(observation, profileId, staleRevision, winningRevision, pendingNotes, winningNotes)));
    }

    [Fact]
    public void RentalBillingStaleDraftSettlement_RequiresStablePostFlushRevalidation()
    {
        var profileId = Guid.NewGuid();
        const long staleRevision = 51;
        const long winningRevision = 52;
        const string pendingNotes = "A-PENDING";
        const string winningNotes = "B-WINS";
        var observation = new global::거래플랜.Desktop.App.MainWindow.MultiPcRentalBillingSettlementObservation(
            IsBusy: false,
            SelectedProfileId: profileId,
            SelectedRevision: winningRevision,
            EditorProfileId: profileId,
            EditorNotes: pendingNotes,
            LocalProfileId: profileId,
            LocalRevision: winningRevision,
            LocalNotes: winningNotes,
            LocalIsDirty: false,
            LocalIsDeleted: false,
            DraftProfileId: profileId,
            DraftRevision: staleRevision,
            DraftNotes: pendingNotes);
        var tracker = new global::거래플랜.Desktop.App.MainWindow.MultiPcRentalBillingSettlementTracker(3);

        bool IsSettled(global::거래플랜.Desktop.App.MainWindow.MultiPcRentalBillingSettlementObservation current)
            => global::거래플랜.Desktop.App.MainWindow.IsMultiPcRentalBillingStaleDraftSettlement(
                current,
                profileId,
                staleRevision,
                winningRevision,
                pendingNotes,
                winningNotes);

        Assert.Equal(
            global::거래플랜.Desktop.App.MainWindow.MultiPcRentalBillingSettlementAction.Pending,
            tracker.Observe(observation, IsSettled(observation)));
        Assert.Equal(
            global::거래플랜.Desktop.App.MainWindow.MultiPcRentalBillingSettlementAction.Pending,
            tracker.Observe(observation, IsSettled(observation)));
        Assert.Equal(
            global::거래플랜.Desktop.App.MainWindow.MultiPcRentalBillingSettlementAction.FlushCurrentEditorDraft,
            tracker.Observe(observation, IsSettled(observation)));
        Assert.False(tracker.CurrentEditorDraftPersisted);

        tracker.MarkCurrentEditorDraftPersisted(true);

        Assert.True(tracker.CurrentEditorDraftPersisted);
        Assert.Equal(0, tracker.StableObservationCount);
        Assert.Equal(
            global::거래플랜.Desktop.App.MainWindow.MultiPcRentalBillingSettlementAction.Pending,
            tracker.Observe(observation, IsSettled(observation)));
        Assert.Equal(
            global::거래플랜.Desktop.App.MainWindow.MultiPcRentalBillingSettlementAction.Pending,
            tracker.Observe(observation, IsSettled(observation)));
        Assert.Equal(
            global::거래플랜.Desktop.App.MainWindow.MultiPcRentalBillingSettlementAction.Complete,
            tracker.Observe(observation, IsSettled(observation)));

        var changed = observation with { DraftRevision = winningRevision };
        Assert.Equal(
            global::거래플랜.Desktop.App.MainWindow.MultiPcRentalBillingSettlementAction.Pending,
            tracker.Observe(changed, IsSettled(changed)));
        Assert.Equal(0, tracker.StableObservationCount);
    }

    [Fact]
    public void RentalBillingStaleDraftSettlement_SuppressedOrClearedFlushCannotEnterPostFlushPhase()
    {
        var profileId = Guid.NewGuid();
        var observation = new global::거래플랜.Desktop.App.MainWindow.MultiPcRentalBillingSettlementObservation(
            IsBusy: false,
            SelectedProfileId: profileId,
            SelectedRevision: 62,
            EditorProfileId: profileId,
            EditorNotes: "A-PENDING",
            LocalProfileId: profileId,
            LocalRevision: 62,
            LocalNotes: "B-WINS",
            LocalIsDirty: false,
            LocalIsDeleted: false,
            DraftProfileId: profileId,
            DraftRevision: 61,
            DraftNotes: "A-PENDING");
        var tracker = new global::거래플랜.Desktop.App.MainWindow.MultiPcRentalBillingSettlementTracker(1);

        Assert.Equal(
            global::거래플랜.Desktop.App.MainWindow.MultiPcRentalBillingSettlementAction.FlushCurrentEditorDraft,
            tracker.Observe(observation, matchesExpectedState: true));

        var exception = Assert.Throws<InvalidOperationException>(
            () => tracker.MarkCurrentEditorDraftPersisted(persisted: false));

        Assert.Equal(
            "PC-A rental billing current editor draft was not persisted after the winner refresh.",
            exception.Message);
        Assert.False(tracker.CurrentEditorDraftPersisted);
        Assert.Equal(
            global::거래플랜.Desktop.App.MainWindow.MultiPcRentalBillingSettlementAction.FlushCurrentEditorDraft,
            tracker.Observe(observation, matchesExpectedState: true));
    }

    [Fact]
    public async Task RentalBillingStaleDraftSettlement_TimeoutCancelsOperationAndUsesSafeDiagnostic()
    {
        CancellationToken observedToken = default;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var exception = await Assert.ThrowsAsync<TimeoutException>(
            () => global::거래플랜.Desktop.App.MainWindow.RunMultiPcRentalBillingBoundedOperationAsync(
                async token =>
                {
                    observedToken = token;
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return 1;
                },
                TimeSpan.FromMilliseconds(200),
                () => "rental settlement timed out; revision-only diagnostic"));

        stopwatch.Stop();
        Assert.True(observedToken.IsCancellationRequested);
        Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
        Assert.Equal("rental settlement timed out; revision-only diagnostic", exception.Message);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void RentalBillingTemplateMarkerGuard_AcceptsOnlyExactAssetlessTemplate()
    {
        var guard = typeof(global::거래플랜.Desktop.App.MainWindow).GetMethod(
            "HasOnlyMultiPcRentalBillingTemplate",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(guard);

        bool Invoke(string? json)
            => (bool)guard.Invoke(null, [json])!;

        var itemId = Guid.NewGuid();
        var valid =
            $$"""
              [{
                "ItemId":"{{itemId:D}}",
                "DisplayItemName":"MULTIPC-RENTAL-PROFILE-ONLY",
                "BillingLineMode":"\uBB36\uC74C",
                "IndividualGroupingMode":"\uBAA8\uB378\uC790\uB3D9",
                "Specification":"",
                "Unit":"",
                "MaterialNumber":"",
                "Quantity":1,
                "UnitPrice":0,
                "Amount":0,
                "Note":"",
                "IncludedAssetIds":[]
              }]
              """;

        Assert.True(Invoke(valid));
        Assert.False(Invoke(null));
        Assert.False(Invoke("[]"));
        Assert.False(Invoke("{"));
        Assert.False(Invoke(valid.Replace(
            "MULTIPC-RENTAL-PROFILE-ONLY",
            "OTHER-RENTAL-PROFILE",
            StringComparison.Ordinal)));
        Assert.False(Invoke(valid.Replace(
            "\"IncludedAssetIds\":[]",
            $"\"IncludedAssetIds\":[\"{Guid.NewGuid():D}\"]",
            StringComparison.Ordinal)));
        Assert.False(Invoke(valid.Replace(
            "\"IncludedAssetIds\":[]",
            $"\"RepresentativeAssetId\":\"{Guid.NewGuid():D}\",\"IncludedAssetIds\":[]",
            StringComparison.Ordinal)));
        Assert.False(Invoke(valid.Replace(
            "\"IncludedAssetIds\":[]",
            $"\"CatalogItemId\":\"{Guid.NewGuid():D}\",\"IncludedAssetIds\":[]",
            StringComparison.Ordinal)));
    }

    [Fact]
    public void RentalAssetTemplateDependencyGuard_RejectsMalformedAndRawAssetReferences()
    {
        var guard = typeof(global::거래플랜.Desktop.App.MainWindow).GetMethod(
            "HasNoMultiPcRentalAssetReferenceInBillingTemplate",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(guard);

        var assetId = Guid.NewGuid();
        bool Invoke(string? json)
            => (bool)guard.Invoke(null, [json, assetId])!;

        Assert.True(Invoke(null));
        Assert.True(Invoke("[]"));
        Assert.True(Invoke(
            $"[{{\"RepresentativeAssetId\":\"{Guid.NewGuid():D}\",\"IncludedAssetIds\":[]}}]"));
        Assert.False(Invoke("{"));
        Assert.False(Invoke("null"));
        Assert.False(Invoke(
            $"[{{\"RepresentativeAssetId\":\"{assetId:D}\",\"IncludedAssetIds\":[]}}]"));
        Assert.False(Invoke(
            $"[{{\"RepresentativeAssetId\":null,\"IncludedAssetIds\":[\"{assetId:D}\"]}}]"));
        Assert.False(Invoke(
            $"[{{\"IncludedAssetIds\":[\"{assetId:D}\"],\"includedassetids\":[]}}]"));
    }

    [Fact]
    public void EphemeralBootstrapContractAndSeedEnvironmentHelpers_FailClosedExecutable()
    {
        var projectRoot = FindProjectRoot();
        var uiSmokeSource = Path.Combine(
            projectRoot,
            "tools",
            "verification",
            "Invoke-GeoraePlanDesktopUiSmoke.ps1");
        var runner = Path.Combine(
            projectRoot,
            "테스트 시행",
            "Invoke-MultiPcDesktopE2E.ps1");
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "georaeplan-bootstrap-contract-" + Guid.NewGuid().ToString("N"));
        try
        {
            var scriptRoot = Path.Combine(tempRoot, "project", "tools", "verification");
            Directory.CreateDirectory(scriptRoot);
            var copiedUiSmoke = Path.Combine(scriptRoot, Path.GetFileName(uiSmokeSource));
            File.Copy(uiSmokeSource, copiedUiSmoke);

            var executionRoot = Path.Combine(tempRoot, "project", "테스트 시행", "실행환경");
            var serverDir = Path.Combine(executionRoot, "Server");
            var serverDataRoot = Path.Combine(executionRoot, "ServerData");
            var runId = Guid.NewGuid().ToString("N");
            var rollbackRoot = Path.Combine(executionRoot, "MultiPC", ".rollback", runId);
            Directory.CreateDirectory(serverDir);
            Directory.CreateDirectory(serverDataRoot);
            Directory.CreateDirectory(rollbackRoot);

            var serverDll = Path.Combine(serverDir, "거래플랜.Server.Api.dll");
            var serverDatabase = Path.Combine(serverDir, "거래플랜-local.db");
            var snapshot = Path.Combine(rollbackRoot, "server-before.db");
            File.WriteAllBytes(serverDll, Encoding.UTF8.GetBytes("certified-server-dll"));
            File.WriteAllBytes(serverDatabase, Encoding.UTF8.GetBytes("pre-bootstrap-database"));
            File.Copy(serverDatabase, snapshot);
            var serverDllSha256 = ComputeFileSha256(serverDll);
            var snapshotSha256 = ComputeDatabaseFileSetSha256(snapshot);
            var certificationId = Guid.NewGuid().ToString("N");
            var marker = Path.Combine(executionRoot, ".georaeplan-runtime-ready");
            File.WriteAllText(
                marker,
                string.Join(
                    "\n",
                    "runtime_ready=True",
                    $"runtime_root={executionRoot}",
                    $"runtime_physical_root={executionRoot}",
                    $"certification_id={certificationId}",
                    $"server_dll_sha256={serverDllSha256}") + "\n",
                new UTF8Encoding(false));
            var markerSha256 = ComputeFileSha256(marker);
            var contractPath = Path.Combine(rollbackRoot, "bootstrap-contract.json");

            Dictionary<string, object?> NewContract() => new(StringComparer.Ordinal)
            {
                ["SchemaVersion"] = "1",
                ["RunId"] = runId,
                ["Role"] = "A",
                ["ExecutionRoot"] = executionRoot,
                ["ServerDirectory"] = serverDir,
                ["ServerDataRoot"] = serverDataRoot,
                ["ServerDllPath"] = serverDll,
                ["ServerDllSha256"] = serverDllSha256,
                ["RuntimeMarkerPath"] = marker,
                ["RuntimeMarkerSha256"] = markerSha256,
                ["CertificationId"] = certificationId,
                ["SnapshotPath"] = snapshot,
                ["SnapshotSha256"] = snapshotSha256,
                ["CreatedAtUtc"] = DateTimeOffset.UtcNow.ToString("O")
            };

            string WriteContract(Dictionary<string, object?> contract)
            {
                File.WriteAllText(
                    contractPath,
                    System.Text.Json.JsonSerializer.Serialize(contract),
                    new UTF8Encoding(false));
                return ComputeFileSha256(contractPath);
            }

            int RunValidation(
                Dictionary<string, object?> contract,
                string? serverDirOverride = null,
                string? serverDataRootOverride = null)
            {
                var contractSha256 = WriteContract(contract);
                using var process = StartPowerShell(
                    copiedUiSmoke,
                    new[]
                    {
                        "-ValidateEphemeralBootstrapContractOnly",
                        "-ServerDir", serverDirOverride ?? serverDir,
                        "-ServerDataRoot", serverDataRootOverride ?? serverDataRoot,
                        "-BootstrapContractPath", contractPath,
                        "-BootstrapContractSha256", contractSha256
                    },
                    new Dictionary<string, string?>
                    {
                        ["GEORAEPLAN_MULTI_PC_E2E_ROLE"] = "A",
                        ["GEORAEPLAN_MULTI_PC_RUNTIME_ROOT"] = executionRoot,
                        ["GEORAEPLAN_MULTI_PC_CERTIFICATION_ID"] =
                            Convert.ToString(contract["CertificationId"]),
                        ["SeedUsers__Sentinel"] = "must-not-survive",
                        ["sEeDuSeRs__ItwPassword"] = "must-not-survive"
                    });
                process.WaitForExit();
                return process.ExitCode;
            }

            Assert.Equal(0, RunValidation(NewContract()));

            var arbitraryRoot = Path.Combine(tempRoot, "arbitrary", "Server");
            Directory.CreateDirectory(arbitraryRoot);
            Assert.NotEqual(0, RunValidation(NewContract(), serverDirOverride: arbitraryRoot));

            var forgedCertification = NewContract();
            forgedCertification["CertificationId"] = Guid.NewGuid().ToString("N");
            Assert.NotEqual(0, RunValidation(forgedCertification));

            var wrongDll = Path.Combine(serverDir, "forged.Server.Api.dll");
            File.WriteAllText(wrongDll, "forged");
            var wrongDllContract = NewContract();
            wrongDllContract["ServerDllPath"] = wrongDll;
            wrongDllContract["ServerDllSha256"] = ComputeFileSha256(wrongDll);
            Assert.NotEqual(0, RunValidation(wrongDllContract));

            File.Delete(snapshot);
            Assert.NotEqual(0, RunValidation(NewContract()));

            using var runnerValidation = StartPowerShell(
                runner,
                new[] { "-ValidateSeedEnvironmentIsolationOnly" },
                new Dictionary<string, string?>
                {
                    ["SeedUsers__Sentinel"] = "must-not-survive",
                    ["sEeDuSeRs__ItwPassword"] = "must-not-survive"
                });
            runnerValidation.WaitForExit();
            Assert.Equal(0, runnerValidation.ExitCode);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static Process StartPowerShell(
        string scriptPath,
        IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string?> environment)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        foreach (var pair in environment)
            startInfo.Environment[pair.Key] = pair.Value;
        return Process.Start(startInfo)!;
    }

    private static string ComputeFileSha256(string path)
        => Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

    private static string ComputeDatabaseFileSetSha256(string databasePath)
    {
        var entries = new List<string>();
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            var path = databasePath + suffix;
            if (!File.Exists(path))
                continue;
            entries.Add($"{suffix}\t{new FileInfo(path).Length}\t{ComputeFileSha256(path)}");
        }
        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(string.Join("\n", entries))));
    }

    private static bool HasOnlyTargetBoundLoginCredentialInput(string source)
    {
        const string attestationStartMarker = "function Test-SameAutomationElement";
        const string attestationEndMarker = "function Set-ElementText";
        const string functionStartMarker = "function Set-ElementText";
        const string functionEndMarker = "function Close-Window";
        const string valuePatternLookup =
            "$valuePattern = $Element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)";
        const string valuePatternUnsupported =
            "$null -eq $valuePattern -or $valuePattern.Current.IsReadOnly";
        const string valuePatternSet = "$valuePattern.SetValue($Text)";
        const string expectedFunctionBodySha256 =
            "9906AA4BF5AF850243EE379B982FDDB05C9DCA46231B92BDAF83A360BC96DA5D";
        const string expectedLoginInputSha256 =
            "AC71A5F95B3D766014A1A4EB0D28258CD2A1352BEA037BAA0B91AC8D02320047";
        const string expectedAttestationBodySha256 =
            "D60F85BD5F19100D85FBE4902BA1E81DBBBF65613F5078E386EE53A8093B7259";

        var attestationStart = source.IndexOf(attestationStartMarker, StringComparison.Ordinal);
        if (attestationStart < 0 ||
            System.Text.RegularExpressions.Regex.Matches(
                source,
                @"(?im)^[ \t]*function[ \t]+(?:(?:global|script|local|private):)?Test-SameAutomationElement\b").Count != 1 ||
            System.Text.RegularExpressions.Regex.Matches(
                source,
                @"(?im)^[ \t]*function[ \t]+(?:(?:global|script|local|private):)?Assert-LoginInputTarget\b").Count != 1)
        {
            return false;
        }

        var attestationEnd = source.IndexOf(
            attestationEndMarker,
            attestationStart,
            StringComparison.Ordinal);
        if (attestationEnd <= attestationStart)
        {
            return false;
        }

        var attestationBody = source[attestationStart..attestationEnd];
        if (!string.Equals(
                ComputeNormalizedSourceSha256(attestationBody),
                expectedAttestationBodySha256,
                StringComparison.Ordinal))
        {
            return false;
        }

        var functionStart = source.IndexOf(functionStartMarker, StringComparison.Ordinal);
        if (functionStart < 0 ||
            System.Text.RegularExpressions.Regex.Matches(
                source,
                @"(?im)^[ \t]*function[ \t]+(?:(?:global|script|local|private):)?Set-ElementText\b").Count != 1)
        {
            return false;
        }

        var functionEnd = source.IndexOf(
            functionEndMarker,
            functionStart,
            StringComparison.Ordinal);
        if (functionEnd <= functionStart)
        {
            return false;
        }

        var functionBody = source[functionStart..functionEnd];
        if (!string.Equals(
                ComputeNormalizedSourceSha256(functionBody),
                expectedFunctionBodySha256,
                StringComparison.Ordinal))
        {
            return false;
        }

        var initialTargetAttestation = functionBody.IndexOf(
            "[void](Assert-LoginInputTarget",
            StringComparison.Ordinal);
        var valuePatternTry = initialTargetAttestation < 0
            ? -1
            : functionBody.IndexOf(
                "try {",
                initialTargetAttestation,
                StringComparison.Ordinal);
        var lookup = valuePatternTry < 0
            ? -1
            : functionBody.IndexOf(
                valuePatternLookup,
                valuePatternTry,
                StringComparison.Ordinal);
        var unsupported = lookup < 0
            ? -1
            : functionBody.IndexOf(
                valuePatternUnsupported,
                lookup,
                StringComparison.Ordinal);
        var unsupportedFailClosed = unsupported < 0
            ? -1
            : functionBody.IndexOf(
                "return $false",
                unsupported,
                StringComparison.Ordinal);
        var setValue = unsupportedFailClosed < 0
            ? -1
            : functionBody.IndexOf(
                valuePatternSet,
                unsupportedFailClosed,
                StringComparison.Ordinal);
        var catchStart = setValue < 0
            ? -1
            : functionBody.IndexOf(
                "catch {",
                setValue,
                StringComparison.Ordinal);
        var catchFailClosed = catchStart < 0
            ? -1
            : functionBody.IndexOf(
                "return $false",
                catchStart,
                StringComparison.Ordinal);
        var postInputTargetAttestation = functionBody.LastIndexOf(
            "[void](Assert-LoginInputTarget",
            StringComparison.Ordinal);
        var successfulReturn = functionBody.LastIndexOf(
            "return $true",
            StringComparison.Ordinal);
        var tryCount = System.Text.RegularExpressions.Regex.Matches(
            functionBody,
            @"(?im)^[ \t]*try[ \t]*\{").Count;
        var catchCount = System.Text.RegularExpressions.Regex.Matches(
            functionBody,
            @"(?im)^[ \t]*catch[ \t]*\{").Count;
        var failClosedReturnCount = System.Text.RegularExpressions.Regex.Matches(
            functionBody,
            @"(?im)^[ \t]*return[ \t]+\$false[ \t]*$").Count;
        var successfulReturnCount = System.Text.RegularExpressions.Regex.Matches(
            functionBody,
            @"(?im)^[ \t]*return[ \t]+\$true[ \t]*$").Count;
        var allReturnTokenCount = System.Text.RegularExpressions.Regex.Matches(
            functionBody,
            @"(?i)\breturn\b").Count;
        if (initialTargetAttestation < 0 ||
            valuePatternTry <= initialTargetAttestation ||
            lookup <= valuePatternTry ||
            unsupported <= lookup ||
            unsupportedFailClosed <= unsupported ||
            setValue <= unsupportedFailClosed ||
            catchStart <= setValue ||
            catchFailClosed <= catchStart ||
            postInputTargetAttestation <= catchFailClosed ||
            successfulReturn <= postInputTargetAttestation ||
            tryCount != 1 ||
            catchCount != 1 ||
            failClosedReturnCount != 3 ||
            successfulReturnCount != 1 ||
            allReturnTokenCount != 4 ||
            functionBody.Split(valuePatternLookup, StringSplitOptions.None).Length - 1 != 1 ||
            functionBody.Split(valuePatternSet, StringSplitOptions.None).Length - 1 != 1)
        {
            return false;
        }

        var forbiddenGlobalInputTokens = new[]
        {
            "SendInput",
            "SendKeys",
            "mouse_event",
            "ReplaceFocusedTextWithUnicode",
            "SetForegroundWindow",
            "GetForegroundWindow",
            "SetFocus",
            "-RequireForeground",
            "-RequireFocus",
        };
        if (forbiddenGlobalInputTokens.Any(token =>
                functionBody.Contains(token, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var textUses = GetUnscopedVariableUseLines(functionBody, "Text");
        if (!textUses.SequenceEqual(
                new[]
                {
                    "[string]$Text,",
                    "if ($null -eq $Element -or [string]::IsNullOrEmpty($Text)) {",
                    valuePatternSet,
                },
                StringComparer.Ordinal) ||
            HasScopeQualifiedVariableUse(functionBody, "Text") ||
            HasBracedVariableUse(functionBody, "Text"))
        {
            return false;
        }

        var passwordUses = GetUnscopedVariableUseLines(source, "Password");
        if (!passwordUses.SequenceEqual(
                new[]
                {
                    "[string]$Password = '',",
                    "[string]::IsNullOrWhiteSpace($Password)",
                    "-Text $Password `",
                    "$Password = $null",
                    "$Password = $null",
                },
                StringComparer.Ordinal) ||
            HasScopeQualifiedVariableUse(source, "Password") ||
            HasBracedVariableUse(source, "Password"))
        {
            return false;
        }

        var usernameUses = GetUnscopedVariableUseLines(source, "Username");
        if (!usernameUses.SequenceEqual(
                new[]
                {
                    "[string]$Username = '',",
                    "[string]::IsNullOrWhiteSpace($Username) -or",
                    "-Text $Username `",
                    "$Username = $null",
                    "$Username = $null",
                },
                StringComparer.Ordinal) ||
            HasScopeQualifiedVariableUse(source, "Username") ||
            HasBracedVariableUse(source, "Username"))
        {
            return false;
        }

        var loginStart = source.IndexOf(
            "if ($startupWindow.Kind -eq 'Login' -or (Test-IsLoginWindow -Window $startupWindow.Window))",
            StringComparison.Ordinal);
        if (loginStart < 0)
        {
            return false;
        }

        var credentialClear = source.IndexOf(
            "$Password = $null",
            loginStart,
            StringComparison.Ordinal);
        if (credentialClear <= loginStart)
        {
            return false;
        }

        var loginInput = source[
            loginStart..
            (credentialClear + "$Password = $null".Length)];
        if (!string.Equals(
                ComputeNormalizedSourceSha256(loginInput),
                expectedLoginInputSha256,
                StringComparison.Ordinal))
        {
            return false;
        }

        return
            loginInput.Split("Set-ElementText", StringSplitOptions.None).Length - 1 == 2 &&
            loginInput.Split("-Text $Username", StringSplitOptions.None).Length - 1 == 1 &&
            loginInput.Split("-Text $Password", StringSplitOptions.None).Length - 1 == 1 &&
            loginInput.Contains("-Element $usernameBox", StringComparison.Ordinal) &&
            loginInput.Contains("-Element $passwordBox", StringComparison.Ordinal) &&
            loginInput.Contains("-ExpectedAutomationId 'UsernameBox'", StringComparison.Ordinal) &&
            loginInput.Contains("-ExpectedAutomationId 'PasswordBox'", StringComparison.Ordinal) &&
            forbiddenGlobalInputTokens.All(token =>
                !loginInput.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static string[] GetUnscopedVariableUseLines(
        string source,
        string variableName)
        => source
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Where(line => System.Text.RegularExpressions.Regex.IsMatch(
                line,
                $@"(?i)\${System.Text.RegularExpressions.Regex.Escape(variableName)}\b"))
            .Select(line => line.Trim())
            .ToArray();

    private static bool HasOnlyEmptyCredentialParameterDefaults(string source)
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(
            source,
            @"(?im)^[ \t]*\[string\]\$(?<name>Username|Password)[ \t]*=[ \t]*(?<value>[^\r\n,]*),[ \t]*$");
        if (matches.Count != 2)
            return false;

        return new[] { "Username", "Password" }.All(name =>
        {
            var matchingDeclarations = matches
                .Cast<System.Text.RegularExpressions.Match>()
                .Where(match => string.Equals(
                    match.Groups["name"].Value,
                    name,
                    StringComparison.Ordinal))
                .ToList();
            return matchingDeclarations.Count == 1 &&
                   string.Equals(
                       matchingDeclarations[0].Groups["value"].Value.Trim(),
                       "''",
                       StringComparison.Ordinal);
        });
    }

    private static bool HasScopeQualifiedVariableUse(
        string source,
        string variableName)
    {
        var escapedVariableName =
            System.Text.RegularExpressions.Regex.Escape(variableName);
        return
            System.Text.RegularExpressions.Regex.IsMatch(
                source,
                $@"(?i)\$(?:global|script|local|private):{escapedVariableName}\b") ||
            System.Text.RegularExpressions.Regex.IsMatch(
                source,
                $@"(?i)\$\{{(?:global|script|local|private):{escapedVariableName}\}}");
    }

    private static bool HasBracedVariableUse(
        string source,
        string variableName)
        => System.Text.RegularExpressions.Regex.IsMatch(
            source,
            $@"(?i)\$\{{{System.Text.RegularExpressions.Regex.Escape(variableName)}\}}");

    private static string ComputeNormalizedSourceSha256(string source)
        => Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(
                    source.Replace("\r\n", "\n", StringComparison.Ordinal))));

    private static string FindProjectRoot(
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "")
    {
        var startingPaths = new[]
        {
            sourceFilePath,
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory,
        };

        foreach (var startingPath in startingPaths.Where(
                     path => !string.IsNullOrWhiteSpace(path)))
        {
            var current = File.Exists(startingPath)
                ? new FileInfo(startingPath).Directory
                : new DirectoryInfo(startingPath);
            while (current is not null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, "Desktop")) &&
                    Directory.Exists(Path.Combine(current.FullName, "테스트 시행")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }
        }

        throw new DirectoryNotFoundException("Project root was not found.");
    }
}
