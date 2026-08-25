using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Desktop.App.Views;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App;

public partial class MainWindow
{
    private const string MultiPcRoleEnvironmentKey = "GEORAEPLAN_MULTI_PC_E2E_ROLE";
    private const string MultiPcRunRootEnvironmentKey = "GEORAEPLAN_MULTI_PC_E2E_RUN_ROOT";
    private const string MultiPcNonceEnvironmentKey = "GEORAEPLAN_MULTI_PC_E2E_NONCE";
    private const string MultiPcContractFileName = "run-contract.json";
    private static readonly JsonSerializerOptions MultiPcJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private bool QueueMultiPcDesktopE2EIfRequested(string reportPath)
    {
        if (!AppPaths.IsTestEnvironment)
            return false;

        var role = Environment.GetEnvironmentVariable(MultiPcRoleEnvironmentKey);
        if (string.IsNullOrWhiteSpace(role))
            return false;

        UiTaskHelper.Forget(
            () => RunMultiPcDesktopE2EAsync(reportPath),
            "MULTIPC-E2E",
            "다중 PC Desktop E2E",
            ex => AppLogger.Error("MULTIPC-E2E", "다중 PC Desktop E2E 실패", ex));
        return true;
    }

    private async Task RunMultiPcDesktopE2EAsync(string reportPath)
    {
        var steps = new List<MultiPcE2EStep>();
        MultiPcE2EContext? context = null;
        var result = "FAIL";

        try
        {
            context = await ValidateMultiPcE2EContextAsync(reportPath);
            AddPassedStep(
                steps,
                "runtime-contract",
                $"runId={context.Contract.RunId}; role={context.Role}; api={context.ApiBaseUri}");

            if (InitialDashboardLoadTask is not null)
                await InitialDashboardLoadTask.WaitAsync(TimeSpan.FromSeconds(120));

            BeginShutdownProtection();
            await Task.WhenAll(
                _vm.DrainPendingBackgroundWorkForShutdownAsync(),
                _realtimeRevisionDrainTask,
                _runtimeSyncDrainTask,
                _windowCommandDrainTask);
            await RunMultiPcSessionPreflightAsync(context, steps);

            if (string.Equals(context.Role, "A", StringComparison.Ordinal))
            {
                await RunMultiPcRoleAAsync(context, steps);
                await RunMultiPcItemRoleAAsync(context, steps);
                await RunMultiPcRentalBillingRoleAAsync(context, steps);
                await RunMultiPcRentalAssetRoleAAsync(context, steps);
                await RunMultiPcInventoryTransferRoleAAsync(context, steps);
            }
            else
            {
                await RunMultiPcRoleBAsync(context, steps);
                await RunMultiPcItemRoleBAsync(context, steps);
                await RunMultiPcRentalBillingRoleBAsync(context, steps);
                await RunMultiPcRentalAssetRoleBAsync(context, steps);
                await RunMultiPcInventoryTransferRoleBAsync(context, steps);
            }

            result = "PASS";
        }
        catch (Exception ex)
        {
            steps.Add(new MultiPcE2EStep(
                "exception",
                false,
                SanitizeMultiPcDetail(ex.Message)));
            AppLogger.Error("MULTIPC-E2E", "다중 PC Desktop E2E 실행 실패", ex);
        }
        finally
        {
            if (context is not null)
            {
                if (!string.Equals(result, "PASS", StringComparison.Ordinal) &&
                    context.OwnedCustomerId is Guid ownedCustomerId &&
                    ownedCustomerId != Guid.Empty)
                {
                    try
                    {
                        var cleanupDetail = await TryCleanupFailedMultiPcFixtureAsync(
                            context,
                            ownedCustomerId);
                        steps.Add(new MultiPcE2EStep(
                            "failure-fixture-cleanup",
                            true,
                            cleanupDetail));
                    }
                    catch (Exception ex)
                    {
                        steps.Add(new MultiPcE2EStep(
                            "failure-fixture-cleanup",
                            false,
                            SanitizeMultiPcDetail(ex.Message)));
                    }
                }

                if (!string.Equals(result, "PASS", StringComparison.Ordinal) &&
                    context.OwnedItemId is Guid ownedItemId &&
                    ownedItemId != Guid.Empty)
                {
                    try
                    {
                        var cleanupDetail = await TryCleanupFailedMultiPcItemFixtureAsync(
                            context,
                            ownedItemId);
                        steps.Add(new MultiPcE2EStep(
                            "failure-item-fixture-cleanup",
                            true,
                            cleanupDetail));
                    }
                    catch (Exception ex)
                    {
                        steps.Add(new MultiPcE2EStep(
                            "failure-item-fixture-cleanup",
                            false,
                            SanitizeMultiPcDetail(ex.Message)));
                    }
                }

                if (!string.Equals(result, "PASS", StringComparison.Ordinal) &&
                    context.OwnedRentalBillingProfileId is Guid ownedRentalBillingProfileId &&
                    ownedRentalBillingProfileId != Guid.Empty)
                {
                    try
                    {
                        var cleanupDetail = await TryCleanupFailedMultiPcRentalBillingFixtureAsync(
                            context,
                            ownedRentalBillingProfileId);
                        steps.Add(new MultiPcE2EStep(
                            "failure-rental-billing-fixture-cleanup",
                            true,
                            cleanupDetail));
                    }
                    catch (Exception ex)
                    {
                        steps.Add(new MultiPcE2EStep(
                            "failure-rental-billing-fixture-cleanup",
                            false,
                            SanitizeMultiPcDetail(ex.Message)));
                    }
                }

                if (!string.Equals(result, "PASS", StringComparison.Ordinal) &&
                    context.OwnedRentalAssetId is Guid ownedRentalAssetId && ownedRentalAssetId != Guid.Empty)
                {
                    try
                    {
                        var cleanupDetail = await TryCleanupFailedMultiPcRentalAssetFixtureAsync(context, ownedRentalAssetId);
                        steps.Add(new MultiPcE2EStep("failure-rental-asset-fixture-cleanup", true, cleanupDetail));
                    }
                    catch (Exception ex)
                    {
                        steps.Add(new MultiPcE2EStep("failure-rental-asset-fixture-cleanup", false, SanitizeMultiPcDetail(ex.Message)));
                    }
                }

                if (!string.Equals(result, "PASS", StringComparison.Ordinal) &&
                    context.OwnedInventoryTransferId is Guid ownedInventoryTransferId && ownedInventoryTransferId != Guid.Empty)
                {
                    try
                    {
                        var cleanupDetail = await TryCleanupFailedMultiPcInventoryTransferFixtureAsync(context, ownedInventoryTransferId);
                        steps.Add(new MultiPcE2EStep("failure-inventory-transfer-fixture-cleanup", true, cleanupDetail));
                    }
                    catch (Exception ex)
                    {
                        steps.Add(new MultiPcE2EStep("failure-inventory-transfer-fixture-cleanup", false, SanitizeMultiPcDetail(ex.Message)));
                    }
                }

                try
                {
                    EndShutdownProtection();
                }
                catch (Exception ex)
                {
                    result = "FAIL";
                    steps.Add(new MultiPcE2EStep(
                        "runtime-resume",
                        false,
                        SanitizeMultiPcDetail(ex.Message)));
                }

                await WriteMultiPcReportAsync(context, result, steps);
            }
        }
    }

    private async Task<MultiPcE2EContext> ValidateMultiPcE2EContextAsync(string reportPath)
    {
        if (!AppPaths.IsTestEnvironment)
            throw new InvalidOperationException("Multi-PC E2E requires the isolated test runtime.");

        var role = (Environment.GetEnvironmentVariable(MultiPcRoleEnvironmentKey) ?? string.Empty)
            .Trim()
            .ToUpperInvariant();
        if (role is not ("A" or "B"))
            throw new InvalidOperationException("Multi-PC E2E role must be A or B.");

        var runRootRaw = Environment.GetEnvironmentVariable(MultiPcRunRootEnvironmentKey);
        var nonce = Environment.GetEnvironmentVariable(MultiPcNonceEnvironmentKey);
        if (string.IsNullOrWhiteSpace(runRootRaw) || string.IsNullOrWhiteSpace(nonce))
            throw new InvalidOperationException("Multi-PC E2E run root and nonce are required.");

        var runRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(runRootRaw));
        if (!IsMultiPcEvidenceRoot(runRoot))
            throw new InvalidOperationException("Multi-PC E2E run root must be under the test evidence directory.");
        AppPaths.EnsureNoExistingReparsePointInPathChain(runRoot, MultiPcRunRootEnvironmentKey);

        var normalizedReportPath = Path.GetFullPath(reportPath);
        if (!IsWithinDirectory(normalizedReportPath, runRoot))
            throw new InvalidOperationException("Multi-PC E2E report path must stay inside the current run root.");

        var contractPath = Path.Combine(runRoot, MultiPcContractFileName);
        if (!File.Exists(contractPath))
            throw new InvalidOperationException("Multi-PC E2E run contract was not found.");

        MultiPcE2ERunContract? contract;
        await using (var stream = new FileStream(
                         contractPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read))
        {
            contract = await JsonSerializer.DeserializeAsync<MultiPcE2ERunContract>(
                stream,
                MultiPcJsonOptions);
        }

        if (contract is null ||
            !string.Equals(contract.SchemaVersion, "1", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(contract.RunId) ||
            !string.Equals(contract.Nonce, nonce, StringComparison.Ordinal) ||
            contract.RunnerProcessId <= 0 ||
            contract.CreatedAtUtc > DateTimeOffset.UtcNow.AddMinutes(1) ||
            contract.CreatedAtUtc < DateTimeOffset.UtcNow.AddHours(-2) ||
            contract.ExpiresAtUtc <= DateTimeOffset.UtcNow ||
            contract.ExpiresAtUtc > DateTimeOffset.UtcNow.AddHours(2) ||
            contract.ExpiresAtUtc <= contract.CreatedAtUtc ||
            string.IsNullOrWhiteSpace(contract.CertificationId) ||
            string.IsNullOrWhiteSpace(contract.ServerDllSha256) ||
            string.IsNullOrWhiteSpace(contract.RuntimeReadyMarkerSha256) ||
            string.IsNullOrWhiteSpace(contract.ServerAssemblyPathSha256) ||
            string.IsNullOrWhiteSpace(contract.ServerInstanceSha256))
        {
            throw new InvalidOperationException("Multi-PC E2E run contract is invalid, stale, or does not match the nonce.");
        }

        var multiPcRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(contract.MultiPcRoot ?? string.Empty));
        if (!Directory.Exists(multiPcRoot) ||
            !IsWithinDirectory(AppPaths.AppRoot, multiPcRoot) ||
            !IsWithinDirectory(AppPaths.TempRoot, multiPcRoot) ||
            !IsWithinDirectory(AppPaths.UserDownloadsDir, multiPcRoot) ||
            !IsWithinDirectory(AppContext.BaseDirectory, multiPcRoot))
        {
            throw new InvalidOperationException("Desktop install, AppData, temp, and downloads roots must all be isolated under MultiPC.");
        }

        AppPaths.EnsureNoExistingReparsePointInPathChain(multiPcRoot, "MultiPC root");
        AppPaths.EnsureNoExistingReparsePointInPathChain(AppContext.BaseDirectory, "Desktop install root");
        AppPaths.EnsureNoExistingReparsePointInPathChain(AppPaths.AppRoot, "Desktop AppData root");
        AppPaths.EnsureNoExistingReparsePointInPathChain(AppPaths.TempRoot, "Desktop temp root");
        AppPaths.EnsureNoExistingReparsePointInPathChain(AppPaths.UserDownloadsDir, "Desktop downloads root");

        if (!Uri.TryCreate(contract.ApiBaseUrl, UriKind.Absolute, out var expectedApiBaseUri) ||
            !IsStrictLoopbackHttpUri(expectedApiBaseUri))
        {
            throw new InvalidOperationException("Multi-PC E2E contract API must be an explicit loopback HTTP endpoint.");
        }

        var actualApiBaseUri = _api.GetBaseUri();
        if (!IsStrictLoopbackHttpUri(actualApiBaseUri) ||
            !string.Equals(
                NormalizeUri(actualApiBaseUri),
                NormalizeUri(expectedApiBaseUri),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Desktop API endpoint does not match the runner-owned loopback server.");
        }

        var expectedInstanceSha256 = ComputeSha256(
            string.Join(
                "\n",
                nonce,
                runRoot,
                contract.CertificationId,
                "A",
                contract.ServerAssemblyPathSha256));
        if (!string.Equals(
                expectedInstanceSha256,
                contract.ServerInstanceSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Multi-PC E2E server instance contract hash is invalid.");
        }

        await ValidateMultiPcServerAttestationAsync(
            actualApiBaseUri,
            contract);

        return new MultiPcE2EContext
        {
            Role = role,
            RunRoot = runRoot,
            ReportPath = normalizedReportPath,
            Contract = contract,
            ApiBaseUri = actualApiBaseUri
        };
    }

    private static async Task ValidateMultiPcServerAttestationAsync(
        Uri apiBaseUri,
        MultiPcE2ERunContract contract)
    {
        using var http = new HttpClient
        {
            BaseAddress = apiBaseUri,
            Timeout = TimeSpan.FromSeconds(10)
        };
        using var response = await http.GetAsync("readyz");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("Runner-owned server readyz attestation is unavailable.");

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(stream);
        var root = document.RootElement;
        var instanceSha256 = string.Empty;
        var certificationId = string.Empty;
        var serverDllSha256 = string.Empty;
        var markerSha256 = string.Empty;
        var role = string.Empty;
        var assemblyPathSha256 = string.Empty;
        var processId = 0;
        var processStartTimeUtc = DateTimeOffset.MinValue;
        if (!TryGetJsonString(root, "status", out var status) ||
            !string.Equals(status, "ready", StringComparison.OrdinalIgnoreCase) ||
            !root.TryGetProperty("testRuntimeAttestation", out var attestation) ||
            attestation.ValueKind != JsonValueKind.Object ||
            !TryGetJsonString(attestation, "instanceSha256", out instanceSha256) ||
            !TryGetJsonString(attestation, "certificationId", out certificationId) ||
            !TryGetJsonString(attestation, "serverDllSha256", out serverDllSha256) ||
            !TryGetJsonString(attestation, "runtimeReadyMarkerSha256", out markerSha256) ||
            !TryGetJsonString(attestation, "role", out role) ||
            !TryGetJsonString(attestation, "assemblyPathSha256", out assemblyPathSha256) ||
            !attestation.TryGetProperty("processId", out var processIdElement) ||
            !processIdElement.TryGetInt32(out processId) ||
            !TryGetJsonString(attestation, "processStartTimeUtc", out var processStartTimeText) ||
            !DateTimeOffset.TryParse(
                processStartTimeText,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out processStartTimeUtc) ||
            !string.Equals(instanceSha256, contract.ServerInstanceSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(certificationId, contract.CertificationId, StringComparison.Ordinal) ||
            !string.Equals(serverDllSha256, contract.ServerDllSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(markerSha256, contract.RuntimeReadyMarkerSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(role, "A", StringComparison.Ordinal) ||
            !string.Equals(
                assemblyPathSha256,
                contract.ServerAssemblyPathSha256,
                StringComparison.OrdinalIgnoreCase) ||
            processId <= 0 ||
            processStartTimeUtc < contract.CreatedAtUtc.AddSeconds(-5) ||
            processStartTimeUtc > contract.ExpiresAtUtc)
        {
            throw new InvalidOperationException("Runner-owned server certification attestation does not match the run contract.");
        }
    }

    private static bool TryGetJsonString(
        JsonElement element,
        string propertyName,
        out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private async Task RunMultiPcSessionPreflightAsync(
        MultiPcE2EContext context,
        ICollection<MultiPcE2EStep> steps)
    {
        if (!_session.IsLoggedIn ||
            _session.IsOfflineMode ||
            string.IsNullOrWhiteSpace(_session.Token) ||
            _session.User is null)
        {
            throw new InvalidOperationException("Multi-PC E2E requires a current online authenticated session.");
        }

        if (!_session.HasAdministrativePrivileges &&
            !_session.HasPermission(AppPermissionNames.DataBackupRestore))
        {
            throw new InvalidOperationException(
                "Multi-PC E2E requires recycle-bin purge permission before any fixture mutation.");
        }

        var deviceId = (await _sync.EnsureDeviceIdAsync()).Trim();
        if (!IsGeneratedSyncDeviceId(deviceId))
            throw new InvalidOperationException("The sync device identity was not initialized.");

        await SyncMultiPcAndRequireCleanAsync("initial-sync");

        var currentSession = new MultiPcSessionEvidence
        {
            RunId = context.Contract.RunId,
            Nonce = context.Contract.Nonce,
            Role = context.Role,
            ProcessId = Environment.ProcessId,
            InstallRootHash = ComputeRunScopedSha256(
                context.Contract.Nonce,
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory))),
            AppRootHash = ComputeRunScopedSha256(
                context.Contract.Nonce,
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppPaths.AppRoot))),
            TempRootHash = ComputeRunScopedSha256(
                context.Contract.Nonce,
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppPaths.TempRoot))),
            DownloadsRootHash = ComputeRunScopedSha256(
                context.Contract.Nonce,
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppPaths.UserDownloadsDir))),
            ApiBaseUrl = NormalizeUri(context.ApiBaseUri),
            DeviceIdHash = ComputeRunScopedSha256(context.Contract.Nonce, deviceId),
            UserIdHash = ComputeRunScopedSha256(
                context.Contract.Nonce,
                _session.User.UserId.ToString("D")),
            TenantCodeHash = ComputeRunScopedSha256(
                context.Contract.Nonce,
                _session.TenantCode),
            OfficeCodeHash = ComputeRunScopedSha256(
                context.Contract.Nonce,
                _session.OfficeCode),
            ScopeTypeHash = ComputeRunScopedSha256(
                context.Contract.Nonce,
                _session.ScopeType),
            BusinessDatabaseNameHash = ComputeRunScopedSha256(
                context.Contract.Nonce,
                _session.SelectedBusinessDatabaseName),
            CanPurgeFixture = true,
            IsOfflineMode = _session.IsOfflineMode,
            CapturedAtUtc = DateTimeOffset.UtcNow
        };

        await WriteMultiPcJsonAtomicAsync(
            Path.Combine(context.RunRoot, $"session-{context.Role}.json"),
            currentSession);

        var otherRole = context.Role == "A" ? "B" : "A";
        var otherSession = await WaitForMultiPcPayloadAsync<MultiPcSessionEvidence>(
            context,
            $"session-{otherRole}.json",
            TimeSpan.FromSeconds(120),
            otherRole);
        context.OtherProcessId = otherSession.ProcessId;

        RequireMultiPc(
            otherSession.ProcessId != currentSession.ProcessId,
            "PC-A and PC-B must have different process IDs.");
        RequireMultiPc(
            !string.Equals(
                otherSession.InstallRootHash,
                currentSession.InstallRootHash,
                StringComparison.Ordinal),
            "PC-A and PC-B must use different physical install roots.");
        RequireMultiPc(
            !string.Equals(
                otherSession.AppRootHash,
                currentSession.AppRootHash,
                StringComparison.Ordinal),
            "PC-A and PC-B must use different AppData roots.");
        RequireMultiPc(
            !string.Equals(
                otherSession.TempRootHash,
                currentSession.TempRootHash,
                StringComparison.Ordinal),
            "PC-A and PC-B must use different temp roots.");
        RequireMultiPc(
            !string.Equals(
                otherSession.DownloadsRootHash,
                currentSession.DownloadsRootHash,
                StringComparison.Ordinal),
            "PC-A and PC-B must use different downloads roots.");
        RequireMultiPc(
            !string.Equals(
                otherSession.DeviceIdHash,
                currentSession.DeviceIdHash,
                StringComparison.Ordinal),
            "PC-A and PC-B must use different sync device identities.");
        RequireMultiPc(
            string.Equals(otherSession.ApiBaseUrl, currentSession.ApiBaseUrl, StringComparison.OrdinalIgnoreCase),
            "PC-A and PC-B must use the same runner-owned API endpoint.");
        RequireMultiPc(
            !string.IsNullOrWhiteSpace(currentSession.UserIdHash) &&
            string.Equals(otherSession.UserIdHash, currentSession.UserIdHash, StringComparison.Ordinal) &&
            string.Equals(otherSession.TenantCodeHash, currentSession.TenantCodeHash, StringComparison.Ordinal) &&
            string.Equals(otherSession.OfficeCodeHash, currentSession.OfficeCodeHash, StringComparison.Ordinal) &&
            string.Equals(otherSession.ScopeTypeHash, currentSession.ScopeTypeHash, StringComparison.Ordinal) &&
            string.Equals(
                otherSession.BusinessDatabaseNameHash,
                currentSession.BusinessDatabaseNameHash,
                StringComparison.Ordinal) &&
            otherSession.CanPurgeFixture &&
            !otherSession.IsOfflineMode,
            "PC-A and PC-B must share the same online user, tenant, office, scope, and business database.");

        AddPassedStep(
            steps,
            "online-session",
            "authenticated user, tenant, office, scope, and business database identities match; offline=false");
        AddPassedStep(
            steps,
            "isolated-runtime",
            $"pid={currentSession.ProcessId}; install and AppData roots are distinct");
        AddPassedStep(
            steps,
            "isolated-device-and-temp",
            "device, temp, and downloads identities are distinct");
    }

    private async Task RunMultiPcRoleAAsync(
        MultiPcE2EContext context,
        ICollection<MultiPcE2EStep> steps)
    {
        var marker = BuildMultiPcMarker(context.Contract.RunId);
        var initialNotes = $"INITIAL-{context.Contract.RunId}";
        var pendingNotes = $"A-PENDING-{context.Contract.RunId}";
        var winningNotes = $"B-WINS-{context.Contract.RunId}";

        var createVm = new CustomerEditViewModel(_local, _session, _api);
        await createVm.LoadAsync();
        RequireMultiPc(
            createVm.OfficeCodes.Contains(_session.OfficeCode, StringComparer.OrdinalIgnoreCase),
            "The authenticated office is not writable for customer fixtures.");

        var createWindow = ShowMultiPcCustomerWindow(createVm);
        var customerId = createVm.CustomerId;
        context.OwnedCustomerId = customerId;
        try
        {
            createVm.Name = marker;
            createVm.ResponsibleOfficeCode = _session.OfficeCode;
            createVm.TradeType = CustomerTradeTypes.Sales;
            createVm.Notes = initialNotes;
            await createVm.SaveCommand.ExecuteAsync(null);
            RequireMultiPc(!createVm.IsNew, $"Fixture customer save failed: {createVm.StatusMessage}");
        }
        finally
        {
            CloseWindowForSmoke(createWindow);
        }

        await SyncMultiPcAndRequireCleanAsync("A-create-sync");
        var created = await RequireMultiPcCustomerAsync(customerId, expectedDeleted: false);
        RequireMultiPc(
            string.Equals(created.NameOriginal, marker, StringComparison.Ordinal) &&
            string.Equals(created.Notes, initialNotes, StringComparison.Ordinal) &&
            !created.IsDirty,
            "PC-A fixture did not reach a clean server-acknowledged state.");

        await WriteMultiPcSignalAsync(
            context,
            "a-created.json",
            customerId,
            created.Revision,
            initialNotes);
        AddPassedStep(
            steps,
            "customer-create-and-sync",
            $"customerId={customerId:D}; revision={created.Revision}; dirty=false");

        _ = await WaitForMultiPcPayloadAsync<MultiPcSignal>(
            context,
            "b-loaded.json",
            TimeSpan.FromSeconds(120),
            "B",
            signal => signal.CustomerId == customerId);

        var staleSnapshot = await RequireMultiPcCustomerAsync(customerId, expectedDeleted: false);
        var staleVm = new CustomerEditViewModel(_local, _session, _api);
        await staleVm.LoadAsync(staleSnapshot);
        var staleWindow = ShowMultiPcCustomerWindow(staleVm);
        try
        {
            staleVm.Notes = pendingNotes;
            RequireMultiPc(
                staleVm.HasPendingChanges &&
                staleVm.CustomerId == customerId &&
                string.Equals(staleVm.Notes, pendingNotes, StringComparison.Ordinal),
                "PC-A could not stage the stale customer draft.");

            await WriteMultiPcSignalAsync(
                context,
                "a-staged.json",
                customerId,
                staleSnapshot.Revision,
                pendingNotes);
            AddPassedStep(
                steps,
                "customer-stale-draft-staged",
                $"selectedId={staleVm.CustomerId:D}; baseRevision={staleSnapshot.Revision}; pendingPreserved=true");

            var bWritten = await WaitForMultiPcPayloadAsync<MultiPcSignal>(
                context,
                "b-written.json",
                TimeSpan.FromSeconds(120),
                "B",
                signal => signal.CustomerId == customerId);

            var staleDeviceId = (await _local.GetSettingAsync("Sync.DeviceId") ?? string.Empty).Trim();
            var stalePush = await _api.PushAsync(
                new SyncPushRequest
                {
                    DeviceId = staleDeviceId,
                    Customers =
                    [
                        BuildMultiPcStaleCustomerDto(
                            staleSnapshot,
                            pendingNotes,
                            context.Contract.RunId)
                    ]
                });
            var staleConflict = stalePush?.Conflicts.SingleOrDefault(conflict =>
                string.Equals(conflict.EntityName, "Customer", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    conflict.EntityId,
                    customerId.ToString("D"),
                    StringComparison.OrdinalIgnoreCase));
            RequireMultiPc(
                stalePush is not null &&
                stalePush.AcceptedCount == 0 &&
                stalePush.ConflictCount == 1 &&
                staleConflict is not null &&
                (staleConflict.Reason ?? string.Empty).StartsWith(
                    "Expected revision mismatch.",
                    StringComparison.OrdinalIgnoreCase),
                "The runner-owned server did not reject the actual stale customer push.");
            AddPassedStep(
                steps,
                "customer-actual-server-stale-push",
                $"customerId={customerId:D}; expectedRevision={staleSnapshot.Revision}; serverRevision={bWritten.Revision}; accepted=0; conflicts=1; reason=Expected revision mismatch");

            await SyncMultiPcAndRequireCleanAsync("A-pull-B-winner");
            var latestLocal = await RequireMultiPcCustomerAsync(customerId, expectedDeleted: false);
            RequireMultiPc(
                latestLocal.Revision >= bWritten.Revision &&
                string.Equals(latestLocal.Notes, winningNotes, StringComparison.Ordinal) &&
                !latestLocal.IsDirty,
                "PC-A did not pull the PC-B winning value before the stale autosave.");

            var autoSaved = await staleVm.TryAutoSaveOnCloseAsync();
            var afterConflict = await RequireMultiPcCustomerAsync(customerId, expectedDeleted: false);
            var outbox = await _local.GetSyncOutboxSummaryAsync(_session);
            RequireMultiPc(
                !autoSaved &&
                staleVm.HasPendingChanges &&
                staleVm.CustomerId == customerId &&
                string.Equals(staleVm.Notes, pendingNotes, StringComparison.Ordinal) &&
                string.Equals(afterConflict.Notes, winningNotes, StringComparison.Ordinal) &&
                afterConflict.Revision == latestLocal.Revision &&
                !afterConflict.IsDirty &&
                outbox.PendingCount == 0 &&
                outbox.FailedCount == 0,
                "Stale autosave did not preserve the draft while keeping the PC-B server value clean.");

            await WriteMultiPcSignalAsync(
                context,
                "a-conflict.json",
                customerId,
                afterConflict.Revision,
                pendingNotes);
            AddPassedStep(
                steps,
                "customer-stale-autosave-conflict",
                $"serverValue=B-WINS; draftValue=A-PENDING; selectedIdPreserved=true; dirty=false; outboxPending=0; status={SanitizeMultiPcDetail(staleVm.StatusMessage)}");

            _ = await WaitForMultiPcPayloadAsync<MultiPcSignal>(
                context,
                "b-deleted.json",
                TimeSpan.FromSeconds(120),
                "B",
                signal => signal.CustomerId == customerId);

            await SyncMultiPcAndRequireCleanAsync("A-pull-delete");
            var deleted = await RequireMultiPcCustomerAsync(customerId, expectedDeleted: true);
            await WriteMultiPcSignalAsync(
                context,
                "a-delete-observed.json",
                customerId,
                deleted.Revision,
                "deleted");
            AddPassedStep(
                steps,
                "customer-delete-propagation",
                $"customerId={customerId:D}; revision={deleted.Revision}; deleted=true; dirty=false");

            _ = await WaitForMultiPcPayloadAsync<MultiPcSignal>(
                context,
                "b-purged.json",
                TimeSpan.FromSeconds(120),
                "B",
                signal => signal.CustomerId == customerId);

            await SyncMultiPcAndRequireCleanAsync("A-pull-purge");
            var purged = await _local.GetCustomerAsync(customerId, _session);
            var finalOutbox = await _local.GetSyncOutboxSummaryAsync(_session);
            RequireMultiPc(
                purged is null &&
                finalOutbox.PendingCount == 0 &&
                finalOutbox.FailedCount == 0 &&
                await _local.CountDirtyAsync(_session) == 0,
                "PC-A fixture purge cleanup did not converge.");

            RequireMultiPc(
                staleVm.HasPendingChanges &&
                staleVm.CustomerId == customerId &&
                string.Equals(staleVm.Notes, pendingNotes, StringComparison.Ordinal),
                "PC-A stale edit selection or temporary draft was lost before cleanup completed.");

            await WriteMultiPcSignalAsync(
                context,
                "a-clean.json",
                customerId,
                deleted.Revision,
                "purged");
            AddPassedStep(
                steps,
                "fixture-cleanup",
                $"customerId={customerId:D}; localRow=absent; dirty=0; outboxPending=0");

            _ = await WaitForMultiPcPayloadAsync<MultiPcSignal>(
                context,
                "b-complete.json",
                TimeSpan.FromSeconds(120),
                "B",
                signal => signal.CustomerId == customerId);
        }
        finally
        {
            CloseWindowForSmoke(staleWindow);
        }
    }

    private async Task RunMultiPcRoleBAsync(
        MultiPcE2EContext context,
        ICollection<MultiPcE2EStep> steps)
    {
        var winningNotes = $"B-WINS-{context.Contract.RunId}";
        var createdSignal = await WaitForMultiPcPayloadAsync<MultiPcSignal>(
            context,
            "a-created.json",
            TimeSpan.FromSeconds(120),
            "A");
        var customerId = createdSignal.CustomerId;
        context.OwnedCustomerId = customerId;

        await SyncMultiPcAndRequireCleanAsync("B-pull-created");
        var loaded = await RequireMultiPcCustomerAsync(customerId, expectedDeleted: false);
        var editVm = new CustomerEditViewModel(_local, _session, _api);
        await editVm.LoadAsync(loaded);
        var editWindow = ShowMultiPcCustomerWindow(editVm);

        try
        {
            await WriteMultiPcSignalAsync(
                context,
                "b-loaded.json",
                customerId,
                loaded.Revision,
                loaded.Notes);
            AddPassedStep(
                steps,
                "customer-cross-client-pull",
                $"customerId={customerId:D}; revision={loaded.Revision}; dirty=false");

            _ = await WaitForMultiPcPayloadAsync<MultiPcSignal>(
                context,
                "a-staged.json",
                TimeSpan.FromSeconds(120),
                "A",
                signal => signal.CustomerId == customerId);

            editVm.Notes = winningNotes;
            await editVm.SaveCommand.ExecuteAsync(null);
            RequireMultiPc(
                !editVm.HasPendingChanges,
                $"PC-B customer save did not complete: {editVm.StatusMessage}");
        }
        finally
        {
            CloseWindowForSmoke(editWindow);
        }

        await SyncMultiPcAndRequireCleanAsync("B-write-sync");
        var written = await RequireMultiPcCustomerAsync(customerId, expectedDeleted: false);
        RequireMultiPc(
            string.Equals(written.Notes, winningNotes, StringComparison.Ordinal) &&
            written.Revision > loaded.Revision &&
            !written.IsDirty,
            "PC-B winning customer value was not acknowledged by the server.");

        await WriteMultiPcSignalAsync(
            context,
            "b-written.json",
            customerId,
            written.Revision,
            winningNotes);
        AddPassedStep(
            steps,
            "customer-winner-save-and-sync",
            $"customerId={customerId:D}; beforeRevision={loaded.Revision}; afterRevision={written.Revision}; dirty=false");

        _ = await WaitForMultiPcPayloadAsync<MultiPcSignal>(
            context,
            "a-conflict.json",
            TimeSpan.FromSeconds(120),
            "A",
            signal => signal.CustomerId == customerId);

        var latestBeforeDelete = await RequireMultiPcCustomerAsync(customerId, expectedDeleted: false);
        var deleteResult = await _local.DeleteCustomerAsync(
            customerId,
            _session,
            latestBeforeDelete.Revision);
        RequireMultiPc(deleteResult.Success, $"PC-B fixture delete failed: {deleteResult.Message}");

        await SyncMultiPcAndRequireCleanAsync("B-delete-sync");
        var deleted = await RequireMultiPcCustomerAsync(customerId, expectedDeleted: true);
        RequireMultiPc(!deleted.IsDirty, "PC-B deleted fixture remained dirty.");
        await WriteMultiPcSignalAsync(
            context,
            "b-deleted.json",
            customerId,
            deleted.Revision,
            "deleted");
        AddPassedStep(
            steps,
            "customer-delete-and-sync",
            $"customerId={customerId:D}; revision={deleted.Revision}; deleted=true; dirty=false");

        _ = await WaitForMultiPcPayloadAsync<MultiPcSignal>(
            context,
            "a-delete-observed.json",
            TimeSpan.FromSeconds(120),
            "A",
            signal => signal.CustomerId == customerId);

        await SyncMultiPcAndRequireCleanAsync("B-confirm-delete");
        deleted = await RequireMultiPcCustomerAsync(customerId, expectedDeleted: true);
        var purgeResult = await _api.PurgeRecycleBinAsync(
            [
                new RecycleBinMutationTargetDto
                {
                    EntityId = customerId,
                    Kind = "customer",
                    ExpectedRevision = deleted.Revision
                }
            ]);
        RequireMultiPc(
            purgeResult is not null &&
            purgeResult.RequestedCount == 1 &&
            purgeResult.SucceededCount == 1 &&
            purgeResult.Results.Count == 1 &&
            purgeResult.Results[0].Success,
            "Server recycle-bin purge did not remove the exact fixture customer.");

        await SyncMultiPcAndRequireCleanAsync("B-pull-purge");
        RequireMultiPc(
            await _local.GetCustomerAsync(customerId, _session) is null,
            "PC-B still contains the purged fixture customer.");
        await WriteMultiPcSignalAsync(
            context,
            "b-purged.json",
            customerId,
            deleted.Revision,
            "purged");
        AddPassedStep(
            steps,
            "server-fixture-purge",
            $"customerId={customerId:D}; requested=1; succeeded=1; localRow=absent");

        _ = await WaitForMultiPcPayloadAsync<MultiPcSignal>(
            context,
            "a-clean.json",
            TimeSpan.FromSeconds(120),
            "A",
            signal => signal.CustomerId == customerId);

        await SyncMultiPcAndRequireCleanAsync("B-final-clean");
        var finalOutbox = await _local.GetSyncOutboxSummaryAsync(_session);
        RequireMultiPc(
            await _local.GetCustomerAsync(customerId, _session) is null &&
            await _local.CountDirtyAsync(_session) == 0 &&
            finalOutbox.PendingCount == 0 &&
            finalOutbox.FailedCount == 0,
            "PC-B final cleanup state did not converge.");

        await WriteMultiPcSignalAsync(
            context,
            "b-complete.json",
            customerId,
            deleted.Revision,
            "complete");
        AddPassedStep(
            steps,
            "fixture-cleanup",
            $"customerId={customerId:D}; localRow=absent; dirty=0; outboxPending=0");
    }

    private async Task RunMultiPcItemRoleAAsync(
        MultiPcE2EContext context,
        ICollection<MultiPcE2EStep> steps)
    {
        var marker = BuildMultiPcItemMarker(context.Contract.RunId);
        var initialMemo = $"INITIAL-ITEM-{context.Contract.RunId}";
        var pendingMemo = $"A-PENDING-ITEM-{context.Contract.RunId}";
        var winningMemo = $"B-WINS-ITEM-{context.Contract.RunId}";

        var createVm = new InventoryViewModel(_local, _session);
        await createVm.LoadAsync();
        createVm.PrepareNewItemRegistration(marker);
        var itemId = createVm.EditId;
        context.OwnedItemId = itemId;
        var createWindow = ShowMultiPcInventoryWindow(createVm);
        try
        {
            createVm.EditSimpleMemo = initialMemo;
            await createVm.SaveItemCommand.ExecuteAsync(null);
            RequireMultiPc(
                !createVm.IsNew &&
                createVm.EditId == itemId,
                $"Fixture item save failed: {createVm.StatusMessage}");
        }
        finally
        {
            CloseMultiPcInventoryWindowWithoutAutoSave(createWindow);
        }

        await SyncMultiPcAndRequireCleanAsync("A-item-create-sync");
        var created = await RequireMultiPcItemAsync(itemId, expectedDeleted: false);
        RequireMultiPc(
            string.Equals(created.NameOriginal, marker, StringComparison.Ordinal) &&
            string.Equals(created.SimpleMemo, initialMemo, StringComparison.Ordinal) &&
            !created.IsDirty,
            "PC-A item fixture did not reach a clean server-acknowledged state.");

        await WriteMultiPcItemSignalAsync(
            context,
            "item-a-created.json",
            itemId,
            created.Revision,
            initialMemo);
        AddPassedStep(
            steps,
            "item-create-and-sync",
            $"itemId={itemId:D}; revision={created.Revision}; dirty=false");

        _ = await WaitForMultiPcPayloadAsync<MultiPcSignal>(
            context,
            "item-b-loaded.json",
            TimeSpan.FromSeconds(120),
            "B",
            signal => signal.ItemId == itemId);

        var staleSnapshot = await RequireMultiPcItemAsync(itemId, expectedDeleted: false);
        var staleVm = new InventoryViewModel(_local, _session);
        await staleVm.LoadAndSelectItemAsync(itemId);
        var staleWindow = ShowMultiPcInventoryWindow(staleVm);
        try
        {
            RequireMultiPc(
                staleVm.SelectedItem?.Id == itemId &&
                staleVm.EditId == itemId,
                "PC-A could not select the item fixture for stale editing.");

            staleVm.EditSimpleMemo = pendingMemo;
            RequireMultiPc(
                staleVm.HasPendingChanges &&
                staleVm.SelectedItem?.Id == itemId &&
                staleVm.EditId == itemId &&
                string.Equals(staleVm.EditSimpleMemo, pendingMemo, StringComparison.Ordinal),
                "PC-A could not stage the stale item draft.");

            await WriteMultiPcItemSignalAsync(
                context,
                "item-a-staged.json",
                itemId,
                staleSnapshot.Revision,
                pendingMemo);
            AddPassedStep(
                steps,
                "item-stale-draft-staged",
                $"selectedId={itemId:D}; baseRevision={staleSnapshot.Revision}; pendingPreserved=true");

            var bWritten = await WaitForMultiPcPayloadAsync<MultiPcSignal>(
                context,
                "item-b-written.json",
                TimeSpan.FromSeconds(120),
                "B",
                signal => signal.ItemId == itemId);

            var staleDeviceId = (await _local.GetSettingAsync("Sync.DeviceId") ?? string.Empty).Trim();
            var stalePush = await _api.PushAsync(
                new SyncPushRequest
                {
                    DeviceId = staleDeviceId,
                    Items =
                    [
                        BuildMultiPcStaleItemDto(
                            staleSnapshot,
                            pendingMemo,
                            context.Contract.RunId)
                    ]
                });
            var staleConflict = stalePush?.Conflicts.SingleOrDefault(conflict =>
                string.Equals(conflict.EntityName, "Item", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    conflict.EntityId,
                    itemId.ToString("D"),
                    StringComparison.OrdinalIgnoreCase));
            RequireMultiPc(
                stalePush is not null &&
                stalePush.AcceptedCount == 0 &&
                stalePush.ConflictCount == 1 &&
                staleConflict is not null &&
                (staleConflict.Reason ?? string.Empty).StartsWith(
                    "Expected revision mismatch.",
                    StringComparison.OrdinalIgnoreCase),
                "The runner-owned server did not reject the actual stale item push.");
            AddPassedStep(
                steps,
                "item-actual-server-stale-push",
                $"itemId={itemId:D}; expectedRevision={staleSnapshot.Revision}; serverRevision={bWritten.Revision}; accepted=0; conflicts=1; reason=Expected revision mismatch");

            var staleDeletePush = await _api.PushAsync(
                new SyncPushRequest
                {
                    DeviceId = staleDeviceId,
                    Items =
                    [
                        BuildMultiPcStaleItemDeleteDto(
                            staleSnapshot,
                            context.Contract.RunId)
                    ]
                });
            var staleDeleteConflict = staleDeletePush?.Conflicts.SingleOrDefault(conflict =>
                string.Equals(conflict.EntityName, "Item", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    conflict.EntityId,
                    itemId.ToString("D"),
                    StringComparison.OrdinalIgnoreCase));
            RequireMultiPc(
                staleDeletePush is not null &&
                staleDeletePush.AcceptedCount == 0 &&
                staleDeletePush.ConflictCount == 1 &&
                staleDeleteConflict is not null &&
                (staleDeleteConflict.Reason ?? string.Empty).StartsWith(
                    "Expected revision mismatch.",
                    StringComparison.OrdinalIgnoreCase),
                "The runner-owned server did not reject the actual stale item delete.");
            AddPassedStep(
                steps,
                "item-actual-server-stale-delete",
                $"itemId={itemId:D}; expectedRevision={staleSnapshot.Revision}; serverRevision={bWritten.Revision}; accepted=0; conflicts=1; activePreserved=true");

            await SyncMultiPcAndRequireCleanAsync("A-item-pull-B-winner");
            var latestLocal = await RequireMultiPcItemAsync(itemId, expectedDeleted: false);
            RequireMultiPc(
                latestLocal.Revision >= bWritten.Revision &&
                string.Equals(latestLocal.SimpleMemo, winningMemo, StringComparison.Ordinal) &&
                !latestLocal.IsDirty,
                "PC-A did not pull the PC-B winning item value before the stale autosave.");

            var staleLocalDelete = await _local.DeleteItemAsync(
                itemId,
                _session,
                staleSnapshot.Revision);
            var afterStaleLocalDelete = await RequireMultiPcItemAsync(itemId, expectedDeleted: false);
            RequireMultiPc(
                !staleLocalDelete.Success &&
                staleLocalDelete.ConcurrencyConflict &&
                afterStaleLocalDelete.Revision == latestLocal.Revision &&
                string.Equals(afterStaleLocalDelete.SimpleMemo, winningMemo, StringComparison.Ordinal) &&
                !afterStaleLocalDelete.IsDirty,
                "PC-A local stale item delete did not fail closed after pulling the PC-B winner.");
            AddPassedStep(
                steps,
                "item-local-stale-delete-conflict",
                $"itemId={itemId:D}; staleRevision={staleSnapshot.Revision}; currentRevision={latestLocal.Revision}; active=true; dirty=false");

            var autoSaved = await staleVm.TryAutoSaveOnCloseAsync();
            var afterConflict = await RequireMultiPcItemAsync(itemId, expectedDeleted: false);
            var outbox = await _local.GetSyncOutboxSummaryAsync(_session);
            RequireMultiPc(
                !autoSaved &&
                staleVm.HasPendingChanges &&
                staleVm.SelectedItem?.Id == itemId &&
                staleVm.EditId == itemId &&
                string.Equals(staleVm.EditSimpleMemo, pendingMemo, StringComparison.Ordinal) &&
                string.Equals(afterConflict.SimpleMemo, winningMemo, StringComparison.Ordinal) &&
                afterConflict.Revision == latestLocal.Revision &&
                !afterConflict.IsDirty &&
                staleVm.StatusMessage.Contains("최신값을 다시 불러온 뒤", StringComparison.Ordinal) &&
                outbox.PendingCount == 0 &&
                outbox.FailedCount == 0,
                "Stale item autosave did not preserve the draft and selection while keeping the PC-B server value clean.");

            await WriteMultiPcItemSignalAsync(
                context,
                "item-a-conflict.json",
                itemId,
                afterConflict.Revision,
                pendingMemo);
            AddPassedStep(
                steps,
                "item-stale-autosave-conflict",
                $"serverValue=B-WINS-ITEM; draftValue=A-PENDING-ITEM; selectedIdPreserved=true; dirty=false; outboxPending=0; status={SanitizeMultiPcDetail(staleVm.StatusMessage)}");

            _ = await WaitForMultiPcPayloadAsync<MultiPcSignal>(
                context,
                "item-b-deleted.json",
                TimeSpan.FromSeconds(120),
                "B",
                signal => signal.ItemId == itemId);

            await SyncMultiPcAndRequireCleanAsync("A-item-pull-delete");
            var deleted = await RequireMultiPcItemAsync(itemId, expectedDeleted: true);
            await WriteMultiPcItemSignalAsync(
                context,
                "item-a-delete-observed.json",
                itemId,
                deleted.Revision,
                "deleted");
            AddPassedStep(
                steps,
                "item-delete-propagation",
                $"itemId={itemId:D}; revision={deleted.Revision}; deleted=true; dirty=false");

            _ = await WaitForMultiPcPayloadAsync<MultiPcSignal>(
                context,
                "item-b-purged.json",
                TimeSpan.FromSeconds(120),
                "B",
                signal => signal.ItemId == itemId);

            await SyncMultiPcAndRequireCleanAsync("A-item-pull-purge");
            await WaitForMultiPcConditionAsync(
                () => staleVm.SelectedItem is null,
                TimeSpan.FromSeconds(10),
                "PC-A inventory window did not observe the purged item selection removal.");
            await Task.Delay(TimeSpan.FromMilliseconds(750));
            var purgeResidue = await _local.GetItemPurgeResidueCountsAsync(itemId);
            var finalOutbox = await _local.GetSyncOutboxSummaryAsync(_session);
            RequireMultiPc(
                purgeResidue.ItemCount == 0 &&
                purgeResidue.ItemPriceGradeCount == 0 &&
                purgeResidue.WarehouseStockCount == 0 &&
                purgeResidue.MovementCount == 0 &&
                purgeResidue.StockLayerCount == 0 &&
                await _local.CountDirtyAsync(_session) == 0 &&
                finalOutbox.PendingCount == 0 &&
                finalOutbox.FailedCount == 0,
                "PC-A item fixture purge cleanup did not converge.");
            AddPassedStep(
                steps,
                "item-fixture-purge-no-residue",
                $"itemId={itemId:D}; item=0; priceGrades=0; warehouseStocks=0; movements=0; stockLayers=0");

            await WriteMultiPcItemSignalAsync(
                context,
                "item-a-clean.json",
                itemId,
                deleted.Revision,
                "purged");
            AddPassedStep(
                steps,
                "item-fixture-cleanup",
                $"itemId={itemId:D}; localRow=absent; dirty=0; outboxPending=0");

            _ = await WaitForMultiPcPayloadAsync<MultiPcSignal>(
                context,
                "item-b-complete.json",
                TimeSpan.FromSeconds(120),
                "B",
                signal => signal.ItemId == itemId);
        }
        finally
        {
            CloseMultiPcInventoryWindowWithoutAutoSave(staleWindow);
        }
    }

    private async Task RunMultiPcItemRoleBAsync(
        MultiPcE2EContext context,
        ICollection<MultiPcE2EStep> steps)
    {
        var winningMemo = $"B-WINS-ITEM-{context.Contract.RunId}";
        var createdSignal = await WaitForMultiPcPayloadAsync<MultiPcSignal>(
            context,
            "item-a-created.json",
            TimeSpan.FromSeconds(120),
            "A",
            signal => signal.ItemId != Guid.Empty);
        var itemId = createdSignal.ItemId;

        await SyncMultiPcAndRequireCleanAsync("B-item-pull-created");
        var loaded = await RequireMultiPcItemAsync(itemId, expectedDeleted: false);
        var editVm = new InventoryViewModel(_local, _session);
        await editVm.LoadAndSelectItemAsync(itemId);
        RequireMultiPc(
            editVm.SelectedItem?.Id == itemId &&
            editVm.EditId == itemId,
            "PC-B could not select the pulled item fixture.");
        var editWindow = ShowMultiPcInventoryWindow(editVm);
        try
        {
            await WriteMultiPcItemSignalAsync(
                context,
                "item-b-loaded.json",
                itemId,
                loaded.Revision,
                loaded.SimpleMemo);
            AddPassedStep(
                steps,
                "item-cross-client-pull",
                $"itemId={itemId:D}; revision={loaded.Revision}; dirty=false");

            _ = await WaitForMultiPcPayloadAsync<MultiPcSignal>(
                context,
                "item-a-staged.json",
                TimeSpan.FromSeconds(120),
                "A",
                signal => signal.ItemId == itemId);

            editVm.EditSimpleMemo = winningMemo;
            await editVm.SaveItemCommand.ExecuteAsync(null);
            RequireMultiPc(
                !editVm.HasPendingChanges &&
                editVm.SelectedItem?.Id == itemId &&
                editVm.EditId == itemId,
                $"PC-B item save did not complete: {editVm.StatusMessage}");
        }
        finally
        {
            CloseMultiPcInventoryWindowWithoutAutoSave(editWindow);
        }

        await SyncMultiPcAndRequireCleanAsync("B-item-write-sync");
        var written = await RequireMultiPcItemAsync(itemId, expectedDeleted: false);
        RequireMultiPc(
            string.Equals(written.SimpleMemo, winningMemo, StringComparison.Ordinal) &&
            written.Revision > loaded.Revision &&
            !written.IsDirty,
            "PC-B winning item value was not acknowledged by the server.");

        await WriteMultiPcItemSignalAsync(
            context,
            "item-b-written.json",
            itemId,
            written.Revision,
            winningMemo);
        AddPassedStep(
            steps,
            "item-winner-save-and-sync",
            $"itemId={itemId:D}; beforeRevision={loaded.Revision}; afterRevision={written.Revision}; dirty=false");

        _ = await WaitForMultiPcPayloadAsync<MultiPcSignal>(
            context,
            "item-a-conflict.json",
            TimeSpan.FromSeconds(120),
            "A",
            signal => signal.ItemId == itemId);

        var latestBeforeDelete = await RequireMultiPcItemAsync(itemId, expectedDeleted: false);
        var deleteResult = await _local.DeleteItemAsync(
            itemId,
            _session,
            latestBeforeDelete.Revision);
        RequireMultiPc(deleteResult.Success, $"PC-B item fixture delete failed: {deleteResult.Message}");

        await SyncMultiPcAndRequireCleanAsync("B-item-delete-sync");
        var deleted = await RequireMultiPcItemAsync(itemId, expectedDeleted: true);
        RequireMultiPc(!deleted.IsDirty, "PC-B deleted item fixture remained dirty.");
        await WriteMultiPcItemSignalAsync(
            context,
            "item-b-deleted.json",
            itemId,
            deleted.Revision,
            "deleted");
        AddPassedStep(
            steps,
            "item-delete-and-sync",
            $"itemId={itemId:D}; revision={deleted.Revision}; deleted=true; dirty=false");

        _ = await WaitForMultiPcPayloadAsync<MultiPcSignal>(
            context,
            "item-a-delete-observed.json",
            TimeSpan.FromSeconds(120),
            "A",
            signal => signal.ItemId == itemId);

        await SyncMultiPcAndRequireCleanAsync("B-item-confirm-delete");
        deleted = await RequireMultiPcItemAsync(itemId, expectedDeleted: true);
        var purgeResult = await _api.PurgeRecycleBinAsync(
            [
                new RecycleBinMutationTargetDto
                {
                    EntityId = itemId,
                    Kind = "item",
                    ExpectedRevision = deleted.Revision
                }
            ]);
        RequireMultiPc(
            purgeResult is not null &&
            purgeResult.RequestedCount == 1 &&
            purgeResult.SucceededCount == 1 &&
            purgeResult.Results.Count == 1 &&
            purgeResult.Results[0].Success,
            "Server recycle-bin purge did not remove the exact fixture item.");

        await SyncMultiPcAndRequireCleanAsync("B-item-pull-purge");
        RequireMultiPc(
            await _local.GetItemAsync(itemId) is null,
            "PC-B still contains the purged fixture item.");
        await WriteMultiPcItemSignalAsync(
            context,
            "item-b-purged.json",
            itemId,
            deleted.Revision,
            "purged");
        AddPassedStep(
            steps,
            "server-item-fixture-purge",
            $"itemId={itemId:D}; requested=1; succeeded=1; localRow=absent");

        _ = await WaitForMultiPcPayloadAsync<MultiPcSignal>(
            context,
            "item-a-clean.json",
            TimeSpan.FromSeconds(120),
            "A",
            signal => signal.ItemId == itemId);

        await SyncMultiPcAndRequireCleanAsync("B-item-final-clean");
        var purgeResidue = await _local.GetItemPurgeResidueCountsAsync(itemId);
        var finalOutbox = await _local.GetSyncOutboxSummaryAsync(_session);
        RequireMultiPc(
            purgeResidue.ItemCount == 0 &&
            purgeResidue.ItemPriceGradeCount == 0 &&
            purgeResidue.WarehouseStockCount == 0 &&
            purgeResidue.MovementCount == 0 &&
            purgeResidue.StockLayerCount == 0 &&
            await _local.CountDirtyAsync(_session) == 0 &&
            finalOutbox.PendingCount == 0 &&
            finalOutbox.FailedCount == 0,
            "PC-B final item cleanup state did not converge.");
        AddPassedStep(
            steps,
            "item-fixture-purge-no-residue",
            $"itemId={itemId:D}; item=0; priceGrades=0; warehouseStocks=0; movements=0; stockLayers=0");

        await WriteMultiPcItemSignalAsync(
            context,
            "item-b-complete.json",
            itemId,
            deleted.Revision,
            "complete");
        AddPassedStep(
            steps,
            "item-fixture-cleanup",
            $"itemId={itemId:D}; localRow=absent; dirty=0; outboxPending=0");
    }

    private CustomerEditWindow ShowMultiPcCustomerWindow(CustomerEditViewModel viewModel)
    {
        var window = new CustomerEditWindow(viewModel)
        {
            Owner = this,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        window.Show();
        return window;
    }

    private InventoryWindow ShowMultiPcInventoryWindow(InventoryViewModel viewModel)
    {
        var window = new InventoryWindow(viewModel)
        {
            Owner = this,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        window.Show();
        return window;
    }

    private static void CloseMultiPcInventoryWindowWithoutAutoSave(InventoryWindow window)
    {
        window.DataContext = null;
        CloseWindowForSmoke(window);
    }

    private async Task SyncMultiPcAndRequireCleanAsync(string operationName)
    {
        var synced = false;
        var dirtyCount = int.MaxValue;
        var outbox = await _local.GetSyncOutboxSummaryAsync(_session);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            synced = await _sync.TrySyncAsync();
            dirtyCount = await _local.CountDirtyAsync(_session);
            outbox = await _local.GetSyncOutboxSummaryAsync(_session);
            if (synced &&
                dirtyCount == 0 &&
                outbox.PendingCount == 0 &&
                outbox.FailedCount == 0)
            {
                return;
            }

            if (attempt < 3)
                await Task.Delay(TimeSpan.FromMilliseconds(500));
        }

        RequireMultiPc(synced, $"Explicit sync failed: {operationName}.");
        RequireMultiPc(
            dirtyCount == 0 &&
            outbox.PendingCount == 0 &&
            outbox.FailedCount == 0,
            $"Explicit sync did not converge cleanly: {operationName}; dirty={dirtyCount}; pending={outbox.PendingCount}; failed={outbox.FailedCount}.");
    }

    private async Task<LocalCustomer> RequireMultiPcCustomerAsync(
        Guid customerId,
        bool expectedDeleted)
    {
        var customer = await _local.GetCustomerAsync(customerId, _session);
        RequireMultiPc(customer is not null, $"Fixture customer was not found: {customerId:D}.");
        RequireMultiPc(
            customer!.IsDeleted == expectedDeleted,
            $"Fixture customer deletion state mismatch: expected={expectedDeleted}; actual={customer.IsDeleted}.");
        return customer;
    }

    private async Task<LocalItem> RequireMultiPcItemAsync(
        Guid itemId,
        bool expectedDeleted)
    {
        var item = await _local.GetItemAsync(itemId);
        RequireMultiPc(item is not null, $"Fixture item was not found: {itemId:D}.");
        RequireMultiPc(
            item!.IsDeleted == expectedDeleted,
            $"Fixture item deletion state mismatch: expected={expectedDeleted}; actual={item.IsDeleted}.");
        return item;
    }

    private async Task WriteMultiPcSignalAsync(
        MultiPcE2EContext context,
        string fileName,
        Guid customerId,
        long revision,
        string value)
    {
        await WriteMultiPcJsonAtomicAsync(
            Path.Combine(context.RunRoot, fileName),
            new MultiPcSignal
            {
                RunId = context.Contract.RunId,
                Nonce = context.Contract.Nonce,
                Role = context.Role,
                ProcessId = Environment.ProcessId,
                CustomerId = customerId,
                Revision = revision,
                Value = value,
                CapturedAtUtc = DateTimeOffset.UtcNow
            });
    }

    private async Task WriteMultiPcItemSignalAsync(
        MultiPcE2EContext context,
        string fileName,
        Guid itemId,
        long revision,
        string value)
    {
        await WriteMultiPcJsonAtomicAsync(
            Path.Combine(context.RunRoot, fileName),
            new MultiPcSignal
            {
                RunId = context.Contract.RunId,
                Nonce = context.Contract.Nonce,
                Role = context.Role,
                ProcessId = Environment.ProcessId,
                ItemId = itemId,
                Revision = revision,
                Value = value,
                CapturedAtUtc = DateTimeOffset.UtcNow
            });
    }

    private async Task WriteMultiPcRentalBillingSignalAsync(
        MultiPcE2EContext context,
        string fileName,
        Guid billingProfileId,
        long revision,
        string value)
    {
        await WriteMultiPcJsonAtomicAsync(
            Path.Combine(context.RunRoot, fileName),
            new MultiPcSignal
            {
                RunId = context.Contract.RunId,
                Nonce = context.Contract.Nonce,
                Role = context.Role,
                ProcessId = Environment.ProcessId,
                RentalBillingProfileId = billingProfileId,
                Revision = revision,
                Value = value,
                CapturedAtUtc = DateTimeOffset.UtcNow
            });
    }

    private async Task WriteMultiPcRentalAssetSignalAsync(MultiPcE2EContext context, string fileName, Guid assetId, long revision, string value)
    {
        await WriteMultiPcJsonAtomicAsync(Path.Combine(context.RunRoot, fileName), new MultiPcSignal
        {
            RunId = context.Contract.RunId, Nonce = context.Contract.Nonce, Role = context.Role,
            ProcessId = Environment.ProcessId, RentalAssetId = assetId, Revision = revision,
            Value = value, CapturedAtUtc = DateTimeOffset.UtcNow
        });
    }

    private async Task WriteMultiPcInventoryTransferSignalAsync(
        MultiPcE2EContext context,
        string fileName,
        Guid transferId,
        long revision,
        string value,
        MultiPcInventoryTransferScope scope,
        MultiPcInventoryEvidence? evidence = null)
    {
        await WriteMultiPcJsonAtomicAsync(Path.Combine(context.RunRoot, fileName), new MultiPcSignal
        {
            RunId = context.Contract.RunId, Nonce = context.Contract.Nonce, Role = context.Role,
            ProcessId = Environment.ProcessId, InventoryTransferId = transferId, Revision = revision,
            ItemId = scope.ItemId, TenantCode = scope.TenantCode,
            FromWarehouseCode = scope.FromWarehouseCode, ToWarehouseCode = scope.ToWarehouseCode,
            SourceQuantity = evidence?.SourceQuantity,
            DestinationQuantity = evidence?.DestinationQuantity,
            Value = value, CapturedAtUtc = DateTimeOffset.UtcNow
        });
    }

    private static async Task<T> WaitForMultiPcPayloadAsync<T>(
        MultiPcE2EContext context,
        string fileName,
        TimeSpan timeout,
        string expectedRole,
        Func<T, bool>? predicate = null)
        where T : MultiPcPayload
    {
        var path = Path.Combine(context.RunRoot, fileName);
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        Exception? lastReadError = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(path))
                {
                    await using var stream = new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite);
                    var payload = await JsonSerializer.DeserializeAsync<T>(
                        stream,
                        MultiPcJsonOptions);
                    if (payload is not null &&
                        string.Equals(payload.RunId, context.Contract.RunId, StringComparison.Ordinal) &&
                        string.Equals(payload.Nonce, context.Contract.Nonce, StringComparison.Ordinal) &&
                        string.Equals(payload.Role, expectedRole, StringComparison.Ordinal) &&
                        payload.ProcessId > 0 &&
                        (context.OtherProcessId <= 0 ||
                         payload.ProcessId == context.OtherProcessId) &&
                        payload.CapturedAtUtc >= context.Contract.CreatedAtUtc.AddSeconds(-5) &&
                        payload.CapturedAtUtc <= context.Contract.ExpiresAtUtc &&
                        (predicate is null || predicate(payload)))
                    {
                        return payload;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                lastReadError = ex;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new TimeoutException(
            lastReadError is null
                ? $"Timed out waiting for Multi-PC signal: {fileName}."
                : $"Timed out waiting for Multi-PC signal: {fileName}; lastError={lastReadError.Message}");
    }

    private static async Task WriteMultiPcJsonAtomicAsync<T>(string path, T payload)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("Multi-PC evidence path has no parent directory.");

        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(
                tempPath,
                JsonSerializer.Serialize(payload, MultiPcJsonOptions),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static async Task WriteMultiPcReportAsync(
        MultiPcE2EContext context,
        string result,
        IReadOnlyCollection<MultiPcE2EStep> steps)
    {
        try
        {
            if (!AppPaths.IsTestEnvironment ||
                !IsMultiPcEvidenceRoot(context.RunRoot))
            {
                throw new InvalidOperationException(
                    "Multi-PC E2E report writing requires a validated isolated test root.");
            }

            var reportPath = Path.GetFullPath(context.ReportPath);
            if (!IsWithinDirectory(reportPath, context.RunRoot))
            {
                throw new InvalidOperationException(
                    "Multi-PC E2E report path left the validated run root.");
            }

            AppPaths.EnsureNoExistingReparsePointInPathChain(
                context.RunRoot,
                MultiPcRunRootEnvironmentKey);
            var directory = Path.GetDirectoryName(reportPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var jsonPath = Path.ChangeExtension(reportPath, ".json");
            var payload = new
            {
                SchemaVersion = "1",
                CreatedAt = DateTimeOffset.Now,
                Result = result,
                RunId = context.Contract.RunId,
                Role = context.Role,
                ProcessId = Environment.ProcessId,
                ApiBaseUrl = context.ApiBaseUri.ToString(),
                Scenario = "customer-and-item-stale-autosave-and-delete-propagation-with-rental-and-pending-inventory-transfer",
                Steps = steps
            };

            await File.WriteAllTextAsync(
                jsonPath,
                JsonSerializer.Serialize(payload, MultiPcJsonOptions),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var lines = new List<string>
            {
                "# 거래플랜 Multi-PC Desktop E2E",
                "",
                $"- 작성시각: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                $"- 결과: **{result}**",
                $"- RunId: {context.Contract.RunId}",
                $"- Role: {context.Role}",
                $"- ProcessId: {Environment.ProcessId}",
                "",
                "| 단계 | 결과 | 상세 |",
                "|---|---|---|"
            };
            lines.AddRange(steps.Select(step =>
                $"| {step.Name} | {(step.Passed ? "PASS" : "FAIL")} | {SanitizeMultiPcDetail(step.Detail).Replace("|", "\\|")} |"));
            lines.Add("");
            lines.Add($"JSON: {jsonPath}");

            await File.WriteAllLinesAsync(
                reportPath,
                lines,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
        catch (Exception ex)
        {
            AppLogger.Error("MULTIPC-E2E", "다중 PC Desktop E2E 보고서 기록 실패", ex);
        }
    }

    private static bool IsMultiPcEvidenceRoot(string path)
    {
        var marker = $"{Path.DirectorySeparatorChar}테스트 시행{Path.DirectorySeparatorChar}기록{Path.DirectorySeparatorChar}";
        return (path + Path.DirectorySeparatorChar)
            .Contains(marker, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWithinDirectory(string candidate, string root)
    {
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
               normalizedCandidate.StartsWith(
                   normalizedRoot + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStrictLoopbackHttpUri(Uri uri)
        => uri.IsAbsoluteUri &&
           string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
           uri.Port is > 0 and <= 65535 &&
           string.IsNullOrEmpty(uri.UserInfo);

    private static string NormalizeUri(Uri uri)
        => uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');

    private static string ComputeSha256(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string ComputeRunScopedSha256(string nonce, string value)
        => ComputeSha256($"{nonce}\0{value}");

    private static bool IsGeneratedSyncDeviceId(string value)
    {
        var separatorIndex = value.LastIndexOf(':');
        return separatorIndex > 0 &&
               separatorIndex < value.Length - 1 &&
               Guid.TryParseExact(value[(separatorIndex + 1)..], "N", out _);
    }

    private static string BuildMultiPcMarker(string runId)
    {
        var safeRunId = new string(runId.Where(char.IsLetterOrDigit).ToArray());
        if (safeRunId.Length > 16)
            safeRunId = safeRunId[..16];
        return $"CODEX-MULTIPC-{safeRunId}-CUSTOMER";
    }

    private static string BuildMultiPcItemMarker(string runId)
    {
        var safeRunId = new string(runId.Where(char.IsLetterOrDigit).ToArray());
        if (safeRunId.Length > 16)
            safeRunId = safeRunId[..16];
        return $"CODEX-MULTIPC-{safeRunId}-ITEM";
    }

    private static CustomerDto BuildMultiPcStaleCustomerDto(
        LocalCustomer customer,
        string pendingNotes,
        string runId)
    {
        var mutationCreatedAtUtc = DateTime.UtcNow;
        return new CustomerDto
        {
            Id = customer.Id,
            CustomerMasterId = customer.CustomerMasterId,
            TenantCode = customer.TenantCode,
            OfficeCode = customer.OfficeCode,
            ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
            NameOriginal = customer.NameOriginal,
            NameMatchKey = customer.NameMatchKey,
            CategoryId = customer.CategoryId,
            TradeType = customer.TradeType,
            Department = customer.Department,
            ContactPerson = customer.ContactPerson,
            Representative = customer.Representative,
            BusinessNumber = customer.BusinessNumber,
            BusinessType = customer.BusinessType,
            BusinessItem = customer.BusinessItem,
            Address = customer.Address,
            DetailAddress = customer.DetailAddress,
            Phone = customer.Phone,
            MobilePhone = customer.MobilePhone,
            FaxNumber = customer.FaxNumber,
            Email = customer.Email,
            HomePage = customer.HomePage,
            Recipient = customer.Recipient,
            PriceGrade = customer.PriceGrade,
            Notes = pendingNotes,
            IsDeleted = false,
            CreatedAtUtc = customer.CreatedAtUtc,
            UpdatedAtUtc = mutationCreatedAtUtc,
            Revision = customer.Revision,
            ExpectedRevision = customer.Revision,
            MutationId = $"multipc-{runId}-a-stale-{Guid.NewGuid():N}",
            MutationCreatedAtUtc = mutationCreatedAtUtc
        };
    }

    private static ItemDto BuildMultiPcStaleItemDto(
        LocalItem item,
        string pendingMemo,
        string runId)
    {
        var mutationCreatedAtUtc = DateTime.UtcNow;
        var dto = LocalMappings.ToDto(item);
        dto.SimpleMemo = pendingMemo;
        dto.IsDeleted = false;
        dto.UpdatedAtUtc = mutationCreatedAtUtc;
        dto.Revision = item.Revision;
        dto.ExpectedRevision = item.Revision;
        dto.MutationId = $"multipc-{runId}-a-item-stale-{Guid.NewGuid():N}";
        dto.MutationCreatedAtUtc = mutationCreatedAtUtc;
        return dto;
    }

    private static ItemDto BuildMultiPcStaleItemDeleteDto(
        LocalItem item,
        string runId)
    {
        var mutationCreatedAtUtc = DateTime.UtcNow;
        var dto = LocalMappings.ToDto(item);
        dto.CurrentStock = 0m;
        dto.IsDeleted = true;
        dto.UpdatedAtUtc = mutationCreatedAtUtc;
        dto.Revision = item.Revision;
        dto.ExpectedRevision = item.Revision;
        dto.MutationId = $"multipc-{runId}-a-item-stale-delete-{Guid.NewGuid():N}";
        dto.MutationCreatedAtUtc = mutationCreatedAtUtc;
        return dto;
    }

    private async Task<string> TryCleanupFailedMultiPcFixtureAsync(
        MultiPcE2EContext context,
        Guid customerId)
    {
        _ = await _sync.TrySyncAsync();
        var customer = await _local.GetCustomerAsync(customerId, _session);
        if (customer is null)
            return $"customerId={customerId:D}; already absent";

        var expectedMarker = BuildMultiPcMarker(context.Contract.RunId);
        if (!string.Equals(customer.NameOriginal, expectedMarker, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Failure cleanup refused because the exact fixture marker did not match.");
        }

        if (!customer.IsDeleted)
        {
            var deleteResult = await _local.DeleteCustomerAsync(
                customerId,
                _session,
                customer.Revision);
            RequireMultiPc(
                deleteResult.Success,
                $"Failure cleanup soft delete failed: {deleteResult.Message}");
            await SyncMultiPcAndRequireCleanAsync("failure-cleanup-delete");
            customer = await RequireMultiPcCustomerAsync(
                customerId,
                expectedDeleted: true);
        }

        var purgeResult = await _api.PurgeRecycleBinAsync(
            [
                new RecycleBinMutationTargetDto
                {
                    EntityId = customerId,
                    Kind = "customer",
                    ExpectedRevision = customer.Revision
                }
            ]);
        RequireMultiPc(
            purgeResult is not null &&
            purgeResult.RequestedCount == 1 &&
            purgeResult.SucceededCount == 1,
            "Failure cleanup server purge failed.");

        await SyncMultiPcAndRequireCleanAsync("failure-cleanup-purge");
        RequireMultiPc(
            await _local.GetCustomerAsync(customerId, _session) is null,
            "Failure cleanup did not remove the exact fixture locally.");
        return $"customerId={customerId:D}; exact marker purged; dirty=0";
    }

    private async Task<string> TryCleanupFailedMultiPcItemFixtureAsync(
        MultiPcE2EContext context,
        Guid itemId)
    {
        _ = await _sync.TrySyncAsync();
        var item = await _local.GetItemAsync(itemId);
        if (item is null)
            return $"itemId={itemId:D}; already absent";

        var expectedMarker = BuildMultiPcItemMarker(context.Contract.RunId);
        if (!string.Equals(item.NameOriginal, expectedMarker, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Failure cleanup refused because the exact item fixture marker did not match.");
        }

        if (!item.IsDeleted)
        {
            var deleteResult = await _local.DeleteItemAsync(
                itemId,
                _session,
                item.Revision);
            RequireMultiPc(
                deleteResult.Success,
                $"Failure cleanup item soft delete failed: {deleteResult.Message}");
            await SyncMultiPcAndRequireCleanAsync("failure-item-cleanup-delete");
            item = await RequireMultiPcItemAsync(
                itemId,
                expectedDeleted: true);
        }

        var purgeResult = await _api.PurgeRecycleBinAsync(
            [
                new RecycleBinMutationTargetDto
                {
                    EntityId = itemId,
                    Kind = "item",
                    ExpectedRevision = item.Revision
                }
            ]);
        RequireMultiPc(
            purgeResult is not null &&
            purgeResult.RequestedCount == 1 &&
            purgeResult.SucceededCount == 1,
            "Failure cleanup server item purge failed.");

        await SyncMultiPcAndRequireCleanAsync("failure-item-cleanup-purge");
        RequireMultiPc(
            await _local.GetItemAsync(itemId) is null,
            "Failure cleanup did not remove the exact item fixture locally.");
        return $"itemId={itemId:D}; exact marker purged; dirty=0";
    }

    private static void AddPassedStep(
        ICollection<MultiPcE2EStep> steps,
        string name,
        string detail)
        => steps.Add(new MultiPcE2EStep(name, true, SanitizeMultiPcDetail(detail)));

    private static void RequireMultiPc(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static async Task WaitForMultiPcConditionAsync(
        Func<bool> condition,
        TimeSpan timeout,
        string timeoutMessage)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        throw new TimeoutException(timeoutMessage);
    }

    private static string SanitizeMultiPcDetail(string? detail)
    {
        var sanitized = (detail ?? string.Empty)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
        var sensitiveRoots = new[]
            {
                (Path: AppContext.BaseDirectory, Replacement: "[install-root]"),
                (Path: AppPaths.AppRoot, Replacement: "[app-root]"),
                (Path: AppPaths.TempRoot, Replacement: "[temp-root]"),
                (Path: AppPaths.UserDownloadsDir, Replacement: "[downloads-root]"),
                (
                    Path: Environment.GetFolderPath(
                        Environment.SpecialFolder.UserProfile),
                    Replacement: "[user-profile]")
            }
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Path))
            .Select(entry => (
                Path: Path.TrimEndingDirectorySeparator(entry.Path),
                entry.Replacement))
            .DistinctBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(entry => entry.Path.Length);

        foreach (var (path, replacement) in sensitiveRoots)
        {
            sanitized = sanitized.Replace(
                path,
                replacement,
                StringComparison.OrdinalIgnoreCase);
        }

        return sanitized;
    }

    private sealed class MultiPcE2EContext
    {
        public required string Role { get; init; }
        public required string RunRoot { get; init; }
        public required string ReportPath { get; init; }
        public required MultiPcE2ERunContract Contract { get; init; }
        public required Uri ApiBaseUri { get; init; }
        public int OtherProcessId { get; set; }
        public Guid? OwnedCustomerId { get; set; }
        public Guid? OwnedItemId { get; set; }
        public Guid? OwnedRentalBillingProfileId { get; set; }
        public Guid? OwnedRentalAssetId { get; set; }
        public Guid? OwnedInventoryTransferId { get; set; }
        public MultiPcInventoryTransferScope? OwnedInventoryTransferScope { get; set; }
    }

    private sealed class MultiPcE2ERunContract
    {
        public string SchemaVersion { get; set; } = string.Empty;
        public string RunId { get; set; } = string.Empty;
        public string Nonce { get; set; } = string.Empty;
        public DateTimeOffset CreatedAtUtc { get; set; }
        public DateTimeOffset ExpiresAtUtc { get; set; }
        public string ApiBaseUrl { get; set; } = string.Empty;
        public string MultiPcRoot { get; set; } = string.Empty;
        public string CertificationId { get; set; } = string.Empty;
        public string ServerDllSha256 { get; set; } = string.Empty;
        public string RuntimeReadyMarkerSha256 { get; set; } = string.Empty;
        public string ServerAssemblyPathSha256 { get; set; } = string.Empty;
        public string ServerInstanceSha256 { get; set; } = string.Empty;
        public int RunnerProcessId { get; set; }
    }

    private abstract class MultiPcPayload
    {
        public string RunId { get; set; } = string.Empty;
        public string Nonce { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public int ProcessId { get; set; }
        public DateTimeOffset CapturedAtUtc { get; set; }
    }

    private sealed class MultiPcSessionEvidence : MultiPcPayload
    {
        public string InstallRootHash { get; set; } = string.Empty;
        public string AppRootHash { get; set; } = string.Empty;
        public string TempRootHash { get; set; } = string.Empty;
        public string DownloadsRootHash { get; set; } = string.Empty;
        public string ApiBaseUrl { get; set; } = string.Empty;
        public string DeviceIdHash { get; set; } = string.Empty;
        public string UserIdHash { get; set; } = string.Empty;
        public string TenantCodeHash { get; set; } = string.Empty;
        public string OfficeCodeHash { get; set; } = string.Empty;
        public string ScopeTypeHash { get; set; } = string.Empty;
        public string BusinessDatabaseNameHash { get; set; } = string.Empty;
        public bool CanPurgeFixture { get; set; }
        public bool IsOfflineMode { get; set; }
    }

    private sealed class MultiPcSignal : MultiPcPayload
    {
        public Guid CustomerId { get; set; }
        public Guid ItemId { get; set; }
        public Guid RentalBillingProfileId { get; set; }
        public Guid RentalAssetId { get; set; }
        public Guid InventoryTransferId { get; set; }
        public string TenantCode { get; set; } = string.Empty;
        public string FromWarehouseCode { get; set; } = string.Empty;
        public string ToWarehouseCode { get; set; } = string.Empty;
        public decimal? SourceQuantity { get; set; }
        public decimal? DestinationQuantity { get; set; }
        public int BeforeRowCount { get; set; } = -1;
        public int AfterRowCount { get; set; } = -1;
        public long WindowNativeHandle { get; set; }
        public bool RealtimeRevisionMonitorActive { get; set; }
        public DateTimeOffset? PassiveRefreshCompletedAtUtc { get; set; }
        public long Revision { get; set; }
        public string Value { get; set; } = string.Empty;
    }

    private sealed class MultiPcUiaGate : MultiPcPayload
    {
        public string Phase { get; set; } = string.Empty;
        public string TargetRole { get; set; } = string.Empty;
        public int TargetProcessId { get; set; }
        public long WindowNativeHandle { get; set; }
        public string WindowAutomationId { get; set; } = string.Empty;
        public string WindowRuntimeId { get; set; } = string.Empty;
        public string ListAutomationId { get; set; } = string.Empty;
        public string ListRuntimeId { get; set; } = string.Empty;
        public int BeforeRowCount { get; set; } = -1;
        public int AfterRowCount { get; set; } = -1;
        public Guid InventoryTransferId { get; set; }
        public long ServerRevision { get; set; }
    }

    private sealed record MultiPcE2EStep(string Name, bool Passed, string Detail);
}
