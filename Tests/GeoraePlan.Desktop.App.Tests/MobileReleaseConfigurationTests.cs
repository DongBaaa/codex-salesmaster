using System.Text.RegularExpressions;
using Xunit;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class MobileReleaseConfigurationTests
{
    [Fact]
    public void ReleaseDefaultBaseUrl_UsesLinuxPcLiveEndpoint()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Configuration",
            "ApiOptions.cs"));

        Assert.Contains("public const string DefaultBaseUrl = \"http://10.0.2.2:19080\";", source, StringComparison.Ordinal);
        Assert.Contains("public const string DefaultBaseUrl = \"https://trade.2884.kr\";", source, StringComparison.Ordinal);
        Assert.DoesNotContain("api.example.invalid", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AndroidSettingsService_PersistsCustomBaseUrlAndExposesGuardedAdvancedSettings()
    {
        var root = FindRepositoryRoot();
        var settingsSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Services",
            "SettingsService.cs"));
        var apiClientSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Services",
            "GeoraePlanApiClient.cs"));
        var settingsPageSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Pages",
            "SettingsPage.cs"));
        var settingsViewModelSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "ViewModels",
            "SettingsViewModel.cs"));

        Assert.Contains("private const string BaseUrlKey = \"settings.api.baseUrl\";", settingsSource, StringComparison.Ordinal);
        Assert.Contains("Preferences.Default.Get(BaseUrlKey, string.Empty)", settingsSource, StringComparison.Ordinal);
        Assert.Contains("Preferences.Default.Set(BaseUrlKey, normalized);", settingsSource, StringComparison.Ordinal);
        Assert.Contains("Preferences.Default.Remove(BaseUrlKey);", settingsSource, StringComparison.Ordinal);
        Assert.Contains("public Task ResetBaseUrlAsync()", settingsSource, StringComparison.Ordinal);
        Assert.Contains("Uri.TryCreate(value, UriKind.Absolute", settingsSource, StringComparison.Ordinal);
        Assert.Contains("var baseUrl = _settings.GetBaseUrl();", apiClientSource, StringComparison.Ordinal);
        Assert.Contains("nameof(SettingsViewModel.BaseUrl)", settingsPageSource, StringComparison.Ordinal);
        Assert.Contains("nameof(SettingsViewModel.IsConnectionSettingsVisible)", settingsPageSource, StringComparison.Ordinal);
        Assert.Contains("고급 연결 설정", settingsPageSource, StringComparison.Ordinal);
        Assert.Contains("운영 서버로 초기화", settingsPageSource, StringComparison.Ordinal);
        Assert.Contains("ToggleConnectionSettingsCommand = new AsyncCommand(", settingsViewModelSource, StringComparison.Ordinal);
        Assert.Contains("ResetConnectionCommand = new AsyncCommand(ResetConnectionAsync);", settingsViewModelSource, StringComparison.Ordinal);
        Assert.Contains("catch (ArgumentException ex)", settingsViewModelSource, StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidManifest_AllowsCameraCaptureIntentDiscovery()
    {
        var manifest = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Platforms",
            "Android",
            "AndroidManifest.xml"));

        Assert.Contains("android.permission.CAMERA", manifest, StringComparison.Ordinal);
        Assert.Contains("android.hardware.camera", manifest, StringComparison.Ordinal);
        Assert.Contains("android:required=\"false\"", manifest, StringComparison.Ordinal);
        Assert.Contains("<queries>", manifest, StringComparison.Ordinal);
        Assert.Contains("android.media.action.IMAGE_CAPTURE", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidStudioTestScript_UsesFreshestApkAndBlocksVersionDowngradeInstall()
    {
        var source = File.ReadAllText(Path.Combine(
                FindRepositoryRoot(),
                "tools",
                "mobile",
                "Start-GeoraePlanAndroidStudioTest.ps1"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("$searchRoots = @($deploymentRoot, $artifactRoot)", source, StringComparison.Ordinal);
        Assert.Contains("Sort-Object LastWriteTime -Descending", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach ($searchRoot in @($deploymentRoot, $artifactRoot))", source, StringComparison.Ordinal);
        Assert.DoesNotContain("install -r -d", source, StringComparison.Ordinal);
        Assert.Contains("INSTALL_FAILED_VERSION_DOWNGRADE", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidReleaseBuildScript_RetriesKnownAotResponseFileFailureWithoutAot()
    {
        var source = File.ReadAllText(Path.Combine(
                FindRepositoryRoot(),
                "tools",
                "mobile",
                "Build-GeoraePlanAndroidApk.ps1"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("Test-KnownAndroidAotResponseFileFailure", source, StringComparison.Ordinal);
        Assert.Contains("IndexOf('Precompiling failed for', [System.StringComparison]::OrdinalIgnoreCase) -ge 0", source, StringComparison.Ordinal);
        Assert.Contains("Precompiling failed for", source, StringComparison.Ordinal);
        Assert.Contains("The specified response file can not be read", source, StringComparison.Ordinal);
        Assert.Contains("android_profiled_aot_fallback=known_response_file_failure", source, StringComparison.Ordinal);
        Assert.Contains("Get-AndroidPublishArgumentsWithoutAot", source, StringComparison.Ordinal);
        Assert.Contains("'-p:RunAOTCompilation=false'", source, StringComparison.Ordinal);
        Assert.Contains("'-p:AndroidEnableProfiledAot=false'", source, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath $publishDirectory -Recurse -Force -ErrorAction Stop", source, StringComparison.Ordinal);
        Assert.Contains("$publishResult.ExitCode -ne 0 -and $shouldEnableAot", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MobilePages_CommandButtons_BindOnlyToInitializedAsyncCommands()
    {
        var root = FindRepositoryRoot();
        var mobileRoot = Path.Combine(root, "Mobile", "GeoraePlan.Mobile.App");
        var pagesRoot = Path.Combine(mobileRoot, "Pages");
        var viewModelsRoot = Path.Combine(mobileRoot, "ViewModels");
        var commandBindings = new SortedSet<string>(StringComparer.Ordinal);
        var bindingPattern = new Regex(
            @"SetBinding\s*\(\s*Button\.CommandProperty\s*,\s*nameof\s*\(\s*(?<viewModel>[A-Za-z_][A-Za-z0-9_]*)\.(?<command>[A-Za-z_][A-Za-z0-9_]*)\s*\)\s*\)",
            RegexOptions.Compiled);

        foreach (var pagePath in Directory.EnumerateFiles(pagesRoot, "*.cs", SearchOption.TopDirectoryOnly))
        {
            var pageSource = File.ReadAllText(pagePath);
            Assert.DoesNotContain("SetBinding(Button.CommandProperty, \"", pageSource, StringComparison.Ordinal);
            Assert.DoesNotContain("SetBinding(Button.CommandProperty, new Binding(\"", pageSource, StringComparison.Ordinal);

            foreach (Match match in bindingPattern.Matches(pageSource))
            {
                var viewModelName = match.Groups["viewModel"].Value;
                var commandName = match.Groups["command"].Value;
                commandBindings.Add($"{viewModelName}.{commandName}");

                var viewModelPath = Path.Combine(viewModelsRoot, viewModelName + ".cs");
                Assert.True(File.Exists(viewModelPath), $"모바일 페이지가 없는 ViewModel Command에 바인딩했습니다: {Path.GetFileName(pagePath)} -> {viewModelName}.{commandName}");

                var viewModelSource = File.ReadAllText(viewModelPath);
                Assert.Contains($"public AsyncCommand {commandName} {{ get; }}", viewModelSource, StringComparison.Ordinal);
                Assert.Contains($"{commandName} = new AsyncCommand(", viewModelSource, StringComparison.Ordinal);
            }
        }

        Assert.True(commandBindings.Count >= 15, $"모바일 Button Command 바인딩 감지 수가 예상보다 적습니다: {commandBindings.Count}");
    }

    [Fact]
    public void MobileInvoiceRecentItemSelection_BlocksUnresolvedOrOutOfScopeStaleItems()
    {
        var source = File.ReadAllText(Path.Combine(
                FindRepositoryRoot(),
                "Mobile",
                "GeoraePlan.Mobile.App",
                "ViewModels",
                "InvoiceDraftViewModel.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains(
            "OpenItemEntrySheetAsync(matched, recordRecent: true, requireResolvedActiveItem: true)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("bool requireResolvedActiveItem = false", source, StringComparison.Ordinal);
        Assert.Contains("requireActiveSyncedItem: requireResolvedActiveItem", source, StringComparison.Ordinal);
        Assert.Contains(
            "if (requireActiveSyncedItem && syncedItem is null)\n            return false;",
            source,
            StringComparison.Ordinal);
        Assert.Contains("private async Task RejectUnresolvedRecentItemAsync(ItemDto item)", source, StringComparison.Ordinal);
        Assert.Contains("await RemoveRecentSelectionAsync(item.Id);", source, StringComparison.Ordinal);
        Assert.Contains("private async Task RemoveRecentSelectionAsync(Guid itemId)", source, StringComparison.Ordinal);
        Assert.Contains("품목은 삭제되었거나 현재 권한/담당지점 범위 밖입니다", source, StringComparison.Ordinal);

        var scopeRejectIndex = source.IndexOf(
            "if (SelectedItem is not null && !IsItemInSelectedInvoiceOfficeScope(SelectedItem))",
            StringComparison.Ordinal);
        var recordRecentIndex = source.IndexOf(
            "if (recordRecent && SelectedItem is not null)\n            await RecordRecentSelectionAsync(SelectedItem);",
            scopeRejectIndex < 0 ? 0 : scopeRejectIndex,
            StringComparison.Ordinal);

        Assert.True(scopeRejectIndex >= 0, "담당지점/권한 범위 확인이 최근선택 기록 전에 있어야 합니다.");
        Assert.True(recordRecentIndex > scopeRejectIndex, "범위 밖 품목은 최근선택으로 다시 저장되면 안 됩니다.");
    }

    [Fact]
    public void MobileItemCacheFallback_OnlyUsesCacheForRetryableFailures()
    {
        var root = FindRepositoryRoot();
        var invoiceDraftSource = File.ReadAllText(Path.Combine(
                root,
                "Mobile",
                "GeoraePlan.Mobile.App",
                "ViewModels",
                "InvoiceDraftViewModel.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var itemsViewModelSource = File.ReadAllText(Path.Combine(
                root,
                "Mobile",
                "GeoraePlan.Mobile.App",
                "ViewModels",
                "ItemsViewModel.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.True(
            CountOccurrences(invoiceDraftSource, "MobileRetryableNetworkFailure.IsRetryable(ex)") >= 3,
            "전표 작성 화면의 분류/품목검색/품목상세 fallback은 retryable 장애에서만 허용해야 합니다.");
        Assert.Contains(
            "ItemSearchResults.Clear();\n            ResetItemSelection(clearCategory: false);",
            invoiceDraftSource,
            StringComparison.Ordinal);
        Assert.Contains("RejectUnavailableItemSelection(item, ex);", invoiceDraftSource, StringComparison.Ordinal);
        Assert.Contains("삭제되었거나 현재 권한/담당지점 범위 밖일 수 있습니다", invoiceDraftSource, StringComparison.Ordinal);

        Assert.True(
            CountOccurrences(itemsViewModelSource, "MobileRetryableNetworkFailure.IsRetryable(ex)") >= 3,
            "품목 화면의 초기화/검색/상세 fallback은 retryable 장애에서만 허용해야 합니다.");
        Assert.Contains("ClearAllItemDisplay();", itemsViewModelSource, StringComparison.Ordinal);
        Assert.Contains("Items.Clear();\n            ClearSelectedItem();", itemsViewModelSource, StringComparison.Ordinal);
        Assert.Contains("삭제되었거나 현재 권한/담당지점 범위 밖일 수 있습니다", itemsViewModelSource, StringComparison.Ordinal);
    }

    [Fact]
    public void MobileCustomerPaymentCacheFallback_OnlyUsesCacheForRetryableFailures()
    {
        var root = FindRepositoryRoot();
        var invoiceDraftSource = File.ReadAllText(Path.Combine(
                root,
                "Mobile",
                "GeoraePlan.Mobile.App",
                "ViewModels",
                "InvoiceDraftViewModel.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var customersViewModelSource = File.ReadAllText(Path.Combine(
                root,
                "Mobile",
                "GeoraePlan.Mobile.App",
                "ViewModels",
                "CustomersViewModel.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var contractsViewModelSource = File.ReadAllText(Path.Combine(
                root,
                "Mobile",
                "GeoraePlan.Mobile.App",
                "ViewModels",
                "CustomerContractsViewModel.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var paymentDraftSource = File.ReadAllText(Path.Combine(
                root,
                "Mobile",
                "GeoraePlan.Mobile.App",
                "ViewModels",
                "PaymentDraftViewModel.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains(
            "MobileRetryableNetworkFailure.IsRetryable(ex) &&\n                await TrySearchCustomersFromSyncedStateAsync",
            invoiceDraftSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "MobileRetryableNetworkFailure.IsRetryable(ex) &&\n                await TryPreselectCustomerFromSyncedStateAsync",
            invoiceDraftSource,
            StringComparison.Ordinal);
        Assert.Contains("SelectedCustomer = null;\n            StatusMessage = \"거래처를 서버에서 찾지 못했습니다.", invoiceDraftSource, StringComparison.Ordinal);

        Assert.Contains("if (MobileRetryableNetworkFailure.IsRetryable(ex))\n            {\n                var cached = await LoadCachedCustomersAsync();", customersViewModelSource, StringComparison.Ordinal);
        Assert.Contains("detailError is not null && !MobileRetryableNetworkFailure.IsRetryable(detailError)", customersViewModelSource, StringComparison.Ordinal);
        Assert.Contains("ClearSelectedCustomer();\n                DetailStatusMessage = $\"거래처 상세를 사용할 수 없습니다.", customersViewModelSource, StringComparison.Ordinal);
        Assert.Contains("if (MobileRetryableNetworkFailure.IsRetryable(ex))\n                    contracts = await _cacheStore.LoadContractsAsync(customer.Id);", customersViewModelSource, StringComparison.Ordinal);

        Assert.Contains("if (MobileRetryableNetworkFailure.IsRetryable(ex))", contractsViewModelSource, StringComparison.Ordinal);
        Assert.Contains("ReplaceContracts(Array.Empty<CustomerContractDto>());", contractsViewModelSource, StringComparison.Ordinal);

        Assert.Contains(
            "MobileRetryableNetworkFailure.IsRetryable(ex) &&\n                await TryLoadInvoicesFromSyncedStateAsync",
            paymentDraftSource,
            StringComparison.Ordinal);
        Assert.Contains("Invoices.Clear();\n            SelectedInvoice = null;", paymentDraftSource, StringComparison.Ordinal);
    }

    [Fact]
    public void MobileRentalInventorySyncFailure_HidesCachedRowsForAuthoritativeFailures()
    {
        var root = FindRepositoryRoot();
        var syncStateSource = File.ReadAllText(Path.Combine(
                root,
                "Mobile",
                "GeoraePlan.Mobile.App",
                "Models",
                "MobileSyncState.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var syncCoordinatorSource = File.ReadAllText(Path.Combine(
                root,
                "Mobile",
                "GeoraePlan.Mobile.App",
                "Services",
                "SyncCoordinator.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var inventoryTransfersSource = File.ReadAllText(Path.Combine(
                root,
                "Mobile",
                "GeoraePlan.Mobile.App",
                "ViewModels",
                "InventoryTransfersViewModel.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var rentalsSource = File.ReadAllText(Path.Combine(
                root,
                "Mobile",
                "GeoraePlan.Mobile.App",
                "ViewModels",
                "RentalsViewModel.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("public bool LastFailureAllowsCachedDisplay { get; set; } = true;", syncStateSource, StringComparison.Ordinal);
        Assert.Contains("state.LastFailureAllowsCachedDisplay = MobileRetryableNetworkFailure.IsRetryable(ex) || IsConcurrencyConflict(ex);", syncCoordinatorSource, StringComparison.Ordinal);
        Assert.Contains("state.LastFailureAllowsCachedDisplay = true;", syncCoordinatorSource, StringComparison.Ordinal);

        Assert.Contains("ShouldHideCachedDataAfterSyncFailure(state)", inventoryTransfersSource, StringComparison.Ordinal);
        Assert.Contains("ClearTransferDisplay($\"재고이동 데이터를 표시할 수 없습니다. {state.LastError}\");", inventoryTransfersSource, StringComparison.Ordinal);
        Assert.Contains("Transfers.Clear();\n        SelectedTransfer = null;\n        SelectedTransferLines.Clear();", inventoryTransfersSource, StringComparison.Ordinal);
        Assert.Contains("=> !string.IsNullOrWhiteSpace(state.LastError) && !state.LastFailureAllowsCachedDisplay;", inventoryTransfersSource, StringComparison.Ordinal);

        Assert.Contains("ShouldHideCachedDataAfterSyncFailure(state)", rentalsSource, StringComparison.Ordinal);
        Assert.Contains("ClearRentalDisplay($\"렌탈 데이터를 표시할 수 없습니다. {state.LastError}\");", rentalsSource, StringComparison.Ordinal);
        Assert.Contains("BillingProfiles.Clear();\n        RentalAssets.Clear();\n        BillingLogs.Clear();\n        AssignmentHistories.Clear();", rentalsSource, StringComparison.Ordinal);
        Assert.Contains("=> !string.IsNullOrWhiteSpace(state.LastError) && !state.LastFailureAllowsCachedDisplay;", rentalsSource, StringComparison.Ordinal);
    }

    [Fact]
    public void RecycleBinUi_RequiresBackupRestorePermissionBeforeReadAndMutations()
    {
        var root = FindRepositoryRoot();
        var serverControllerSource = File.ReadAllText(Path.Combine(
            root,
            "Server",
            "거래플랜.Server.Api",
            "Controllers",
            "RecycleBinController.cs"));
        var desktopViewModelSource = File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "ViewModels",
            "EnvironmentSettingsViewModel.RecycleBin.cs"));
        var desktopViewSource = File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "Views",
            "EnvironmentSettingsWindow.xaml"));
        var mobileSessionSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Models",
            "SessionSnapshot.cs"));
        var mobileHomePageSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Pages",
            "HomePage.cs"));
        var mobileSettingsPageSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Pages",
            "SettingsPage.cs"));
        var mobileSettingsViewModelSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "ViewModels",
            "SettingsViewModel.cs"));
        var mobileViewModelSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "ViewModels",
            "RecycleBinViewModel.cs"));
        var normalizedServerControllerSource = serverControllerSource.Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("[HttpGet]\n    [Authorize(Policy = PermissionNames.DataBackupRestore)]", normalizedServerControllerSource, StringComparison.Ordinal);

        Assert.Contains("public bool CanManageRecycleBinData => _session.HasAdministrativePrivileges || _session.HasPermission(AppPermissionNames.DataBackupRestore);", desktopViewModelSource, StringComparison.Ordinal);
        Assert.Contains("if (!CanManageRecycleBinData)", desktopViewModelSource, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding CanReloadRecycleBin}\"", desktopViewSource, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding CanMutateSelectedRecycleBinEntry}\"", desktopViewSource, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding CanMutateMarkedRecycleBinEntries}\"", desktopViewSource, StringComparison.Ordinal);

        Assert.Contains("public const string DataBackupRestorePermission = \"Data.BackupRestore\";", mobileSessionSource, StringComparison.Ordinal);
        Assert.Contains("public bool CanManageRecycleBin => IsAdmin || HasPermission(DataBackupRestorePermission);", mobileSessionSource, StringComparison.Ordinal);
        Assert.Contains("var canManageRecycleBin = _sessionStore.GetSnapshot().CanManageRecycleBin;", mobileHomePageSource, StringComparison.Ordinal);
        Assert.Contains("recycleBinButton.IsVisible = canManageRecycleBin;", mobileHomePageSource, StringComparison.Ordinal);
        Assert.Contains("public bool CanManageRecycleBin", mobileSettingsViewModelSource, StringComparison.Ordinal);
        Assert.Contains("CanManageRecycleBin = session.CanManageRecycleBin;", mobileSettingsViewModelSource, StringComparison.Ordinal);
        Assert.Contains("recycleBinButton.SetBinding(VisualElement.IsVisibleProperty, nameof(SettingsViewModel.CanManageRecycleBin));", mobileSettingsPageSource, StringComparison.Ordinal);
        Assert.Contains("public bool CanManageRecycleBinData => _sessionStore.GetSnapshot().CanManageRecycleBin;", mobileViewModelSource, StringComparison.Ordinal);
        Assert.Contains("휴지통 조회/복원 권한이 없습니다.", mobileViewModelSource, StringComparison.Ordinal);
        Assert.Contains("휴지통 영구삭제 권한이 없습니다.", mobileViewModelSource, StringComparison.Ordinal);
    }

    [Fact]
    public void MobileRecycleBinFilterOptions_CoverAllServerRestoreAndPurgeKinds()
    {
        var root = FindRepositoryRoot();
        var serverControllerSource = File.ReadAllText(Path.Combine(
            root,
            "Server",
            "거래플랜.Server.Api",
            "Controllers",
            "RecycleBinController.cs"));
        var mobileViewModelSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "ViewModels",
            "RecycleBinViewModel.cs"));

        var restoreKinds = ExtractServerRecycleBinMutationKinds(serverControllerSource, "Restore");
        var purgeKinds = ExtractServerRecycleBinMutationKinds(serverControllerSource, "Purge");
        var mobileFilterKinds = ExtractMobileRecycleBinFilterKinds(mobileViewModelSource);

        Assert.Equal(restoreKinds, purgeKinds);
        Assert.Contains("RecycleBinFilterOption(string.Empty, \"전체\")", mobileViewModelSource, StringComparison.Ordinal);

        var missingKinds = restoreKinds
            .Where(kind => !mobileFilterKinds.Contains(kind))
            .ToArray();

        Assert.True(
            missingKinds.Length == 0,
            "모바일 휴지통 종류 필터가 서버 복원/영구삭제 kind를 누락했습니다: " + string.Join(", ", missingKinds));
    }

    [Fact]
    public void RecycleBinPurgeRecords_AreAppliedByDesktopAndAndroidSyncForAllServerKinds()
    {
        var root = FindRepositoryRoot();
        var serverControllerSource = File.ReadAllText(Path.Combine(
            root,
            "Server",
            "거래플랜.Server.Api",
            "Controllers",
            "RecycleBinController.cs"));
        var desktopSyncSource = File.ReadAllText(Path.Combine(
                root,
                "Desktop",
                "거래플랜.Desktop.App",
                "Services",
                "SyncService.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var mobileSyncSource = File.ReadAllText(Path.Combine(
                root,
                "Mobile",
                "GeoraePlan.Mobile.App",
                "Services",
                "SyncCoordinator.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        var purgeKinds = ExtractServerRecycleBinMutationKinds(serverControllerSource, "Purge");
        var desktopPurgeFlowSource = ExtractBetween(
            desktopSyncSource,
            "private async Task ApplyPulledPurgeRecordsAsync",
            "private Task<bool> IsPurgeRecordSupersededByActiveLocalEntityAsync");
        var desktopOrderSource = ExtractBetween(
            desktopSyncSource,
            "private static int GetPurgeApplyOrder",
            "private static bool TryParseRecycleBinEntityKind");
        var desktopParserSource = ExtractBetween(
            desktopSyncSource,
            "private static bool TryParseRecycleBinEntityKind",
            "private static bool IsServerSyncDisabled");
        var mobileApplySource = ExtractBetween(
            mobileSyncSource,
            "private async Task ApplyPurgeRecordAsync",
            "private static void RemoveEntityById");
        var mobileOrderSource = ExtractBetween(
            mobileSyncSource,
            "private static int GetPurgeApplyOrder",
            "            _ => 99");

        Assert.Contains("await _local.ApplyServerPurgeRecycleBinEntryAsync(entityKind, dto.EntityId, ct);", desktopPurgeFlowSource, StringComparison.Ordinal);
        Assert.Contains("await ApplyPurgeRecordAsync(state, NormalizePurgeRecordKind(record.Kind), record.EntityId, record.Revision, ct);", mobileSyncSource, StringComparison.Ordinal);

        foreach (var kind in purgeKinds)
        {
            Assert.Contains($"\"{kind}\"", desktopOrderSource, StringComparison.Ordinal);
            Assert.Contains($"case \"{kind}\":", desktopParserSource, StringComparison.Ordinal);
            Assert.Contains($"case \"{kind}\":", mobileApplySource, StringComparison.Ordinal);
            Assert.Contains($"\"{kind}\"", mobileOrderSource, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void MobilePaymentDraft_RequiresPaymentEditPermissionBeforeEntryAndSave()
    {
        var root = FindRepositoryRoot();
        var mobileSessionSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Models",
            "SessionSnapshot.cs"));
        var mobileHomePageSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Pages",
            "HomePage.cs"));
        var mobileInvoicesPageSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Pages",
            "InvoicesPage.cs"));
        var mobilePaymentPageSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Pages",
            "PaymentDraftPage.cs"));
        var mobilePaymentViewModelSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "ViewModels",
            "PaymentDraftViewModel.cs"));
        var normalizedMobilePaymentViewModelSource = mobilePaymentViewModelSource.Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("public const string PaymentEditPermission = \"Payment.Edit\";", mobileSessionSource, StringComparison.Ordinal);
        Assert.Contains("public bool CanCreatePayments => IsAdmin || HasPermission(PaymentEditPermission);", mobileSessionSource, StringComparison.Ordinal);

        Assert.Contains("var canCreatePayments = _sessionStore.GetSnapshot().CanCreatePayments;", mobileHomePageSource, StringComparison.Ordinal);
        Assert.Contains("createPaymentButton.IsVisible = canCreatePayments;", mobileHomePageSource, StringComparison.Ordinal);
        Assert.Contains("createPaymentButton.IsEnabled = canCreatePayments;", mobileHomePageSource, StringComparison.Ordinal);

        Assert.Contains("_sessionStore = ServiceHelper.GetRequiredService<SessionStore>();", mobileInvoicesPageSource, StringComparison.Ordinal);
        Assert.Contains("var canCreatePayments = _sessionStore.GetSnapshot().CanCreatePayments;", mobileInvoicesPageSource, StringComparison.Ordinal);
        Assert.Contains("createPaymentButton.IsVisible = canCreatePayments;", mobileInvoicesPageSource, StringComparison.Ordinal);
        Assert.Contains("selectedPaymentButton.IsVisible = canCreatePayments;", mobileInvoicesPageSource, StringComparison.Ordinal);

        Assert.Contains("PaymentAttachmentDraftStore attachmentStore,\n        SessionStore sessionStore)", normalizedMobilePaymentViewModelSource, StringComparison.Ordinal);
        Assert.Contains("public bool CanCreatePayments => _sessionStore.GetSnapshot().CanCreatePayments;", mobilePaymentViewModelSource, StringComparison.Ordinal);
        Assert.True(CountOccurrences(mobilePaymentViewModelSource, "if (!CanCreatePayments)") >= 2);
        Assert.Contains("saveButton.SetBinding(VisualElement.IsEnabledProperty, nameof(PaymentDraftViewModel.CanCreatePayments));", mobilePaymentPageSource, StringComparison.Ordinal);
    }

    [Fact]
    public void MobilePaymentAndInvoiceDisplay_TreatsProcurementAsPaymentVoucher()
    {
        var root = FindRepositoryRoot();
        var voucherRulesSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Models",
            "MobileVoucherTypeRules.cs"));
        var paymentDraftSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "ViewModels",
            "PaymentDraftViewModel.cs"));
        var invoicesViewModelSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "ViewModels",
            "InvoicesViewModel.cs"));
        var paymentHistorySource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Models",
            "CustomerPaymentHistoryRow.cs"));
        var paymentDraftPageSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Pages",
            "PaymentDraftPage.cs"));
        var pdfExportSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Services",
            "MobileInvoicePdfExportService.cs"));

        Assert.Contains(
            "voucherType is VoucherType.Purchase or VoucherType.Procurement",
            voucherRulesSource,
            StringComparison.Ordinal);
        Assert.Contains("MobileVoucherTypeRules.IsPaymentVoucher(SelectedInvoice?.VoucherType)", paymentDraftSource, StringComparison.Ordinal);
        Assert.Contains("MobileVoucherTypeRules.IsPaymentVoucher(SelectedInvoice.VoucherType)", paymentDraftSource, StringComparison.Ordinal);
        Assert.Contains("MobileVoucherTypeRules.IsPaymentVoucher(VoucherType)", paymentHistorySource, StringComparison.Ordinal);
        Assert.Contains("MobileVoucherTypeRules.IsPaymentVoucher(SelectedInvoice.VoucherType)", invoicesViewModelSource, StringComparison.Ordinal);
        Assert.Contains("MobileVoucherTypeRules.IsPaymentVoucher(Invoice.VoucherType)", invoicesViewModelSource, StringComparison.Ordinal);
        Assert.Contains("MobileVoucherTypeRules.GetInvoiceKindLabel(invoice.VoucherType)", paymentDraftPageSource, StringComparison.Ordinal);
        Assert.Contains("판매 전표는 수금, 구매·발주 전표는 지급으로 자동 구분해 저장합니다.", paymentDraftPageSource, StringComparison.Ordinal);
        Assert.Contains("MobileVoucherTypeRules.GetPortableFileKind(invoice.VoucherType)", pdfExportSource, StringComparison.Ordinal);
        Assert.Contains("MobileVoucherTypeRules.GetDocumentKindLabel(invoice.VoucherType)", pdfExportSource, StringComparison.Ordinal);
    }

    [Fact]
    public void MobileInvoiceDraft_PreservesProcurementVoucherWhenEditing()
    {
        var root = FindRepositoryRoot();
        var voucherRulesSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Models",
            "MobileVoucherTypeRules.cs"));
        var invoiceDraftSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "ViewModels",
            "InvoiceDraftViewModel.cs"));
        var invoicesPageSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Pages",
            "InvoicesPage.cs"));

        Assert.Contains("voucherType is VoucherType.Sales or VoucherType.Purchase or VoucherType.Procurement", voucherRulesSource, StringComparison.Ordinal);
        Assert.Contains("VoucherType.Procurement => VoucherType.Procurement", invoiceDraftSource, StringComparison.Ordinal);
        Assert.Contains("public bool IsProcurementDocument => VoucherType == VoucherType.Procurement;", invoiceDraftSource, StringComparison.Ordinal);
        Assert.Contains("public bool IsPurchaseLikeDocument => MobileVoucherTypeRules.IsPaymentVoucher(VoucherType);", invoiceDraftSource, StringComparison.Ordinal);
        Assert.Contains("DocumentKindText => VoucherType switch", invoiceDraftSource, StringComparison.Ordinal);
        Assert.Contains("VoucherType.Procurement => \"발주\"", invoiceDraftSource, StringComparison.Ordinal);
        Assert.Contains("CustomerNameLabelText => IsPurchaseLikeDocument ? \"거래처\" : \"고객/거래처\";", invoiceDraftSource, StringComparison.Ordinal);
        Assert.Contains("WarehouseLabelText => IsPurchaseLikeDocument ? \"입고창고\" : \"출고창고\";", invoiceDraftSource, StringComparison.Ordinal);
        Assert.Contains("? $\"M-{(IsPurchaseLikeDocument ? \"P\" : \"S\")}-{DateTime.Now:yyyyMMdd-HHmmss}\"", invoiceDraftSource, StringComparison.Ordinal);
        Assert.Contains("VoucherType = this.VoucherType,", invoiceDraftSource, StringComparison.Ordinal);
        Assert.Contains("PurchaseReceivingRequired = IsPurchaseDocument,", invoiceDraftSource, StringComparison.Ordinal);
        Assert.DoesNotContain("voucherType == VoucherType.Purchase ? VoucherType.Purchase : VoucherType.Sales", invoiceDraftSource, StringComparison.Ordinal);
        Assert.Contains("MobileVoucherTypeRules.CanEditInInvoiceDraft(_viewModel.SelectedInvoice.VoucherType)", invoicesPageSource, StringComparison.Ordinal);
        Assert.Contains("모바일에서는 판매/구매/발주 전표만 수정할 수 있습니다.", invoicesPageSource, StringComparison.Ordinal);
    }

    [Fact]
    public void MobileEditModalPendingDelete_HidesDeletedCustomerAndItemInsteadOfReselecting()
    {
        var root = FindRepositoryRoot();
        var customersPageSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Pages",
            "CustomersPage.cs"));
        var itemsPageSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Pages",
            "ItemsPage.cs"));

        Assert.Contains("if (saved is null || saved.IsDeleted)", customersPageSource, StringComparison.Ordinal);
        Assert.Contains("await _viewModel.RemoveDeletedCustomerFromCurrentViewAsync(editedCustomerId);", customersPageSource, StringComparison.Ordinal);
        Assert.Contains("var editedCustomerId = _viewModel.SelectedCustomer.Id;", customersPageSource, StringComparison.Ordinal);
        Assert.Contains("if (saved is null || saved.IsDeleted)", itemsPageSource, StringComparison.Ordinal);
        Assert.Contains("_viewModel.RemoveDeletedItemFromCurrentView(editedItemId);", itemsPageSource, StringComparison.Ordinal);
        Assert.Contains("var editedItemId = _viewModel.SelectedItem.Id;", itemsPageSource, StringComparison.Ordinal);
    }

    [Fact]
    public void MobileCoreMutationDrafts_RequireServerEditPermissionsBeforeEntryAndSave()
    {
        var root = FindRepositoryRoot();
        var mobileSessionSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Models",
            "SessionSnapshot.cs"));
        var mobileHomePageSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Pages",
            "HomePage.cs"));
        var mobileInvoicesPageSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Pages",
            "InvoicesPage.cs"));
        var mobileInvoiceDraftPageSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Pages",
            "InvoiceDraftPage.cs"));
        var mobileInvoiceDraftViewModelSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "ViewModels",
            "InvoiceDraftViewModel.cs"));
        var mobileCustomersPageSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Pages",
            "CustomersPage.cs"));
        var mobileCustomerEditPageSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Pages",
            "CustomerEditPage.cs"));
        var mobileItemsPageSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Pages",
            "ItemsPage.cs"));
        var mobileItemEditPageSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Pages",
            "ItemEditPage.cs"));

        Assert.Contains("public const string CustomerEditPermission = \"Customer.Edit\";", mobileSessionSource, StringComparison.Ordinal);
        Assert.Contains("public const string ItemEditPermission = \"Item.Edit\";", mobileSessionSource, StringComparison.Ordinal);
        Assert.Contains("public const string InvoiceEditPermission = \"Invoice.Edit\";", mobileSessionSource, StringComparison.Ordinal);
        Assert.Contains("public bool CanEditCustomers => IsAdmin || HasPermission(CustomerEditPermission);", mobileSessionSource, StringComparison.Ordinal);
        Assert.Contains("public bool CanEditItems => IsAdmin || HasPermission(ItemEditPermission);", mobileSessionSource, StringComparison.Ordinal);
        Assert.Contains("public bool CanCreateInvoices => IsAdmin || HasPermission(InvoiceEditPermission);", mobileSessionSource, StringComparison.Ordinal);

        Assert.Contains("var canCreateInvoices = _sessionStore.GetSnapshot().CanCreateInvoices;", mobileHomePageSource, StringComparison.Ordinal);
        Assert.Contains("createSalesInvoiceButton.IsVisible = canCreateInvoices;", mobileHomePageSource, StringComparison.Ordinal);
        Assert.Contains("createPurchaseInvoiceButton.IsVisible = canCreateInvoices;", mobileHomePageSource, StringComparison.Ordinal);

        Assert.Contains("var canCreateInvoices = _sessionStore.GetSnapshot().CanCreateInvoices;", mobileInvoicesPageSource, StringComparison.Ordinal);
        Assert.Contains("createSalesInvoiceButton.IsVisible = canCreateInvoices;", mobileInvoicesPageSource, StringComparison.Ordinal);
        Assert.Contains("createPurchaseInvoiceButton.IsVisible = canCreateInvoices;", mobileInvoicesPageSource, StringComparison.Ordinal);
        Assert.Contains("selectedEditButton.IsVisible = canCreateInvoices;", mobileInvoicesPageSource, StringComparison.Ordinal);
        Assert.Contains("public bool CanCreateInvoices => _sessionStore.GetSnapshot().CanCreateInvoices;", mobileInvoiceDraftViewModelSource, StringComparison.Ordinal);
        Assert.True(CountOccurrences(mobileInvoiceDraftViewModelSource, "if (!CanCreateInvoices)") >= 2);
        Assert.Contains("saveButton.SetBinding(VisualElement.IsEnabledProperty, nameof(InvoiceDraftViewModel.CanCreateInvoices));", mobileInvoiceDraftPageSource, StringComparison.Ordinal);

        Assert.Contains("var canEditCustomers = session.CanEditCustomers;", mobileCustomersPageSource, StringComparison.Ordinal);
        Assert.Contains("newCustomerButton.IsVisible = canEditCustomers;", mobileCustomersPageSource, StringComparison.Ordinal);
        Assert.Contains("editCustomerButton.IsVisible = canEditCustomers;", mobileCustomersPageSource, StringComparison.Ordinal);
        Assert.Contains("deleteCustomerButton.IsVisible = canEditCustomers;", mobileCustomersPageSource, StringComparison.Ordinal);
        Assert.Contains("salesInvoiceButton.IsVisible = canCreateInvoices;", mobileCustomersPageSource, StringComparison.Ordinal);
        Assert.Contains("private async Task<bool> EnsureCanEditCustomersAsync", mobileCustomersPageSource, StringComparison.Ordinal);
        Assert.True(CountOccurrences(mobileCustomerEditPageSource, "if (!_sessionStore.GetSnapshot().CanEditCustomers)") >= 2);
        Assert.Contains("saveButton.IsEnabled = canEditCustomers;", mobileCustomerEditPageSource, StringComparison.Ordinal);

        Assert.Contains("var canEditItems = _sessionStore.GetSnapshot().CanEditItems;", mobileItemsPageSource, StringComparison.Ordinal);
        Assert.Contains("newItemFromCategoryButton.IsVisible = canEditItems;", mobileItemsPageSource, StringComparison.Ordinal);
        Assert.Contains("newItemButton.IsVisible = canEditItems;", mobileItemsPageSource, StringComparison.Ordinal);
        Assert.Contains("editItemButton.IsVisible = canEditItems;", mobileItemsPageSource, StringComparison.Ordinal);
        Assert.Contains("deleteItemButton.IsVisible = canEditItems;", mobileItemsPageSource, StringComparison.Ordinal);
        Assert.Contains("private async Task<bool> EnsureCanEditItemsAsync", mobileItemsPageSource, StringComparison.Ordinal);
        Assert.True(CountOccurrences(mobileItemEditPageSource, "if (!_sessionStore.GetSnapshot().CanEditItems)") >= 2);
        Assert.Contains("saveButton.IsEnabled = canEditItems;", mobileItemEditPageSource, StringComparison.Ordinal);
    }

    [Fact]
    public void MobileInventoryTransferAndRentalScreens_RemainReadOnlyWithoutMutationEntrypoints()
    {
        var root = FindRepositoryRoot();
        var mobileRoot = Path.Combine(root, "Mobile", "GeoraePlan.Mobile.App");
        var mobileHomePageSource = File.ReadAllText(Path.Combine(
            mobileRoot,
            "Pages",
            "HomePage.cs"));
        var inventoryPageSource = File.ReadAllText(Path.Combine(
            mobileRoot,
            "Pages",
            "InventoryTransfersPage.cs"));
        var rentalsPageSource = File.ReadAllText(Path.Combine(
            mobileRoot,
            "Pages",
            "RentalsPage.cs"));
        var nonCoordinatorSources = string.Join(
            "\n",
            Directory.EnumerateFiles(mobileRoot, "*.cs", SearchOption.AllDirectories)
                .Where(path => !path.EndsWith(Path.Combine("Services", "SyncCoordinator.cs"), StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(File.ReadAllText));

        Assert.Contains("재고이동·렌탈은 조회 전용입니다.", mobileHomePageSource, StringComparison.Ordinal);
        Assert.Contains("GeoraePlanTheme.CreateButton(\"재고이동 조회\"", mobileHomePageSource, StringComparison.Ordinal);
        Assert.Contains("GeoraePlanTheme.CreateButton(\"렌탈 조회\"", mobileHomePageSource, StringComparison.Ordinal);
        Assert.Contains("모바일 재고이동은 조회 전용입니다.", inventoryPageSource, StringComparison.Ordinal);
        Assert.Contains("모바일 렌탈은 조회 전용입니다.", rentalsPageSource, StringComparison.Ordinal);

        Assert.DoesNotContain("QueueInventoryTransferDraftAsync(", nonCoordinatorSources, StringComparison.Ordinal);
        Assert.DoesNotContain("QueueRentalManagementCompanyDraftAsync(", nonCoordinatorSources, StringComparison.Ordinal);
        Assert.DoesNotContain("QueueRentalBillingProfileDraftAsync(", nonCoordinatorSources, StringComparison.Ordinal);
        Assert.DoesNotContain("QueueRentalAssetDraftAsync(", nonCoordinatorSources, StringComparison.Ordinal);
        Assert.DoesNotContain("QueueRentalAssetAssignmentHistoryDraftAsync(", nonCoordinatorSources, StringComparison.Ordinal);
        Assert.DoesNotContain("QueueRentalBillingLogDraftAsync(", nonCoordinatorSources, StringComparison.Ordinal);
    }

    [Fact]
    public void InvoiceItemSelectionSurfaces_DisplayMaterialNumber()
    {
        var root = FindRepositoryRoot();
        var desktopSalesWindowSource = File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "Views",
            "SalesWindow.xaml"));
        var mobileRecentRecordSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Models",
            "RecentItemSelectionRecord.cs"));
        var mobileLineSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Models",
            "InvoiceLineDraftItem.cs"));
        var mobileInvoiceDraftViewModelSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "ViewModels",
            "InvoiceDraftViewModel.cs"));
        var mobileItemsViewModelSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "ViewModels",
            "ItemsViewModel.cs"));
        var mobileInvoiceDraftPageSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Pages",
            "InvoiceDraftPage.cs"));
        var mobileItemsPageSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Pages",
            "ItemsPage.cs"));

        Assert.Contains("Header=\"자재번호\"", desktopSalesWindowSource, StringComparison.Ordinal);
        Assert.Contains("Binding=\"{Binding MaterialNumber}\"", desktopSalesWindowSource, StringComparison.Ordinal);

        Assert.Contains("public string MaterialNumber { get; set; }", mobileRecentRecordSource, StringComparison.Ordinal);
        Assert.Contains("public string SecondaryText", mobileRecentRecordSource, StringComparison.Ordinal);
        Assert.Contains("public string IdentitySummary", mobileLineSource, StringComparison.Ordinal);

        Assert.Contains("SelectedItemIdentitySummary", mobileInvoiceDraftViewModelSource, StringComparison.Ordinal);
        Assert.Contains("MaterialNumber = item.MaterialNumber", mobileInvoiceDraftViewModelSource, StringComparison.Ordinal);
        Assert.Contains("MaterialNumber = recent.MaterialNumber", mobileInvoiceDraftViewModelSource, StringComparison.Ordinal);
        Assert.Contains("MaterialNumber = line.MaterialNumber", mobileInvoiceDraftViewModelSource, StringComparison.Ordinal);

        Assert.Contains("SelectedItemIdentitySummary", mobileItemsViewModelSource, StringComparison.Ordinal);
        Assert.Contains("자재 {item.MaterialNumber.Trim()}", mobileItemsViewModelSource, StringComparison.Ordinal);

        Assert.Contains("nameof(InvoiceDraftViewModel.SelectedItemIdentitySummary)", mobileInvoiceDraftPageSource, StringComparison.Ordinal);
        Assert.Contains("nameof(InvoiceLineDraftItem.IdentitySummary)", mobileInvoiceDraftPageSource, StringComparison.Ordinal);
        Assert.Contains("recent.SecondaryText", mobileInvoiceDraftPageSource, StringComparison.Ordinal);
        Assert.Contains("자재 {item.MaterialNumber.Trim()}", mobileInvoiceDraftPageSource, StringComparison.Ordinal);

        Assert.Contains("nameof(ItemsViewModel.SelectedItemIdentitySummary)", mobileItemsPageSource, StringComparison.Ordinal);
        Assert.Contains("자재 {item.MaterialNumber.Trim()}", mobileItemsPageSource, StringComparison.Ordinal);
    }

    [Fact]
    public void MobileRentalHistory_PreservesAndDisplaysFinancialBillingRuns()
    {
        var root = FindRepositoryRoot();
        var stateSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Models",
            "MobileSyncState.cs"));
        var coordinatorSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Services",
            "SyncCoordinator.cs"));
        var storeSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Services",
            "JsonSyncStateStore.cs"));
        var viewModelSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "ViewModels",
            "RentalsViewModel.cs"));

        Assert.Contains("public List<InvoiceDto> SyncedInvoices { get; set; } = new();", stateSource, StringComparison.Ordinal);
        Assert.Contains("public List<PaymentDto> SyncedPayments { get; set; } = new();", stateSource, StringComparison.Ordinal);
        Assert.Contains("state.SyncedInvoices = MergeById(state.SyncedInvoices, response.Invoices);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("state.SyncedPayments = MergeById(state.SyncedPayments, response.Payments);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("target.SyncedInvoices = source.SyncedInvoices;", storeSource, StringComparison.Ordinal);
        Assert.Contains("target.SyncedPayments = source.SyncedPayments;", storeSource, StringComparison.Ordinal);
        Assert.Contains("BuildBillingHistoryRows(", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("MergeForDisplay(", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("state.PendingPush.Invoices", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("state.PendingPush.Payments", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("state.PendingPush.Transactions", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("state.PendingPush.RentalBillingProfiles", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("effectiveInvoices", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("effectivePayments", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("effectiveTransactions", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("effectiveBillingProfiles", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("AddTransactionBillingRunEvidence", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("if (transaction.SettlementAmount <= 0m)", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("AddInvoiceBillingRunEvidence", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("AddPaymentBillingRunEvidence", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("if (payment.Amount <= 0m)", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("RentalBillingEvidenceStatusResolver.Resolve(", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("evidence.HasInvoice || evidence.HasTransaction || evidence.HasPayment", viewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveEvidenceStatus(", viewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("IsManualStopStatus(normalizedRunStatus)", viewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Status = Normalize(run?.Status, outstandingAmount <= 0m && billedAmount > 0m ? \"완료\" : \"청구중\")", viewModelSource, StringComparison.Ordinal);
        var normalizedViewModelSource = viewModelSource.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.DoesNotContain("state.SyncedRentalBillingLogs\n            .Where(log => MatchesBillingLog", normalizedViewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var invoice in state.SyncedInvoices", normalizedViewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var transaction in state.SyncedTransactions", normalizedViewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var payment in state.SyncedPayments", normalizedViewModelSource, StringComparison.Ordinal);
    }

    [Fact]
    public void MobileSyncConflict_RemovesOnlyAcceptedPendingPush()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Services",
            "SyncCoordinator.cs"));
        var normalizedSource = source.Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("RemoveAcceptedPendingMutations(state.PendingPush, result.AcceptedRevisions);", source, StringComparison.Ordinal);
        Assert.Contains("private const string TransactionRecordEntityName = \"TransactionRecord\";", source, StringComparison.Ordinal);
        Assert.Contains("private const string InvoiceEntityName = \"Invoice\";", source, StringComparison.Ordinal);
        Assert.Contains("private const string PaymentEntityName = \"Payment\";", source, StringComparison.Ordinal);
        Assert.Contains("private const string RentalBillingProfileEntityName = \"RentalBillingProfile\";", source, StringComparison.Ordinal);
        Assert.Contains("RemoveAccepted(pendingPush.Invoices, acceptedRevisions, InvoiceEntityName);", source, StringComparison.Ordinal);
        Assert.Contains("RemoveAccepted(pendingPush.RentalBillingProfiles, acceptedRevisions, RentalBillingProfileEntityName);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("nameof(Invoice)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("nameof(Payment)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("nameof(TransactionRecord)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("QueueUnacceptedLinkedPaymentConflict", source, StringComparison.Ordinal);

        Assert.DoesNotContain(
            "if (result.ConflictCount > 0)\n                    {\n                        state.PendingPush = new SyncPushRequest",
            normalizedSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MobileSyncForbiddenPush_PreservesPendingPushAndShowsPermissionMessage()
    {
        var source = File.ReadAllText(Path.Combine(
                FindRepositoryRoot(),
                "Mobile",
                "GeoraePlan.Mobile.App",
                "Services",
                "SyncCoordinator.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("private async Task<MobileSyncState> PushInternalAsync(MobileSyncState state, CancellationToken ct)", source, StringComparison.Ordinal);
        Assert.Contains("var result = EnsurePushResult(await _api.PushAsync(scopedPush, ct));", source, StringComparison.Ordinal);
        Assert.Contains("RemoveSentScopedPendingMutations(state.PendingPush, scopedPush, result);", source, StringComparison.Ordinal);
        Assert.Matches(
            "private async Task<MobileSyncState> PushInternalAsync[\\s\\S]*catch \\(Exception ex\\)\\n\\s*\\{\\n\\s*MarkFailure\\(state, ex\\);\\n\\s*await SaveStateAndRemoveDiscardedPaymentAttachmentDraftsAsync\\(state, ct\\);\\n\\s*return state;\\n\\s*\\}",
            source);
        Assert.DoesNotContain(
            "catch (Exception ex)\n        {\n            state.PendingPush = new SyncPushRequest",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "catch (Exception ex)\n        {\n            RemoveSentScopedPendingMutations",
            source,
            StringComparison.Ordinal);

        Assert.Contains("state.LastError = TranslateFailureMessage(ex);", source, StringComparison.Ordinal);
        Assert.Contains("state.LastFailureAllowsCachedDisplay = MobileRetryableNetworkFailure.IsRetryable(ex) || IsConcurrencyConflict(ex);", source, StringComparison.Ordinal);
        Assert.Contains("HttpRequestException httpEx when httpEx.StatusCode == HttpStatusCode.Forbidden", source, StringComparison.Ordinal);
        Assert.Contains("=> \"현재 계정 권한 또는 지점 범위 때문에 저장할 수 없습니다. 담당지점/권한을 확인해 주세요.\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MobileImmediatePaymentForbiddenFailure_DoesNotShowSavedOrInvokeSuccess()
    {
        var root = FindRepositoryRoot();
        var coordinatorSource = File.ReadAllText(Path.Combine(
                root,
                "Mobile",
                "GeoraePlan.Mobile.App",
                "Services",
                "SyncCoordinator.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var paymentViewSource = File.ReadAllText(Path.Combine(
                root,
                "Mobile",
                "GeoraePlan.Mobile.App",
                "ViewModels",
                "PaymentDraftViewModel.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        var methodStart = coordinatorSource.IndexOf(
            "public async Task<MobileSyncState> SavePaymentImmediatelyAsync",
            StringComparison.Ordinal);
        var methodEnd = coordinatorSource.IndexOf(
            "public async Task<MobileSyncState> TryBackgroundSyncAsync",
            methodStart,
            StringComparison.Ordinal);
        Assert.True(methodStart >= 0);
        Assert.True(methodEnd > methodStart);
        var savePaymentMethod = coordinatorSource[methodStart..methodEnd];

        Assert.Contains(
            "StatusCode: HttpStatusCode.BadRequest\n                   or HttpStatusCode.Unauthorized\n                   or HttpStatusCode.Forbidden\n                   or HttpStatusCode.NotFound\n                   or HttpStatusCode.UnprocessableEntity",
            coordinatorSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "catch (Exception ex) when (IsNonRetryableClientFailure(ex))\n            {\n                MarkFailure(state, ex);\n            }",
            savePaymentMethod,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "catch (Exception ex) when (IsNonRetryableClientFailure(ex))\n            {\n                state.PendingPush.Payments.Add(payment);",
            savePaymentMethod,
            StringComparison.Ordinal);
        Assert.Contains(
            "catch (Exception ex)\n            {\n                state.PendingPush.Payments.Add(payment);",
            savePaymentMethod,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (result.ConflictCount > 0)\n                    {\n                        if (WasAccepted(result.AcceptedRevisions, PaymentEntityName, payment.Id))\n                            QueuePaymentAttachmentsForRetry(state, payment.Id, attachmentList);\n                        else\n                            QueueDiscardedPaymentAttachmentDrafts(attachmentList);",
            savePaymentMethod,
            StringComparison.Ordinal);
        Assert.DoesNotContain("QueueUnacceptedLinkedPaymentConflict", savePaymentMethod, StringComparison.Ordinal);

        var saveCallIndex = paymentViewSource.IndexOf(
            "var state = await _syncCoordinator.SavePaymentImmediatelyAsync(payment, Attachments, linkedTransaction);",
            StringComparison.Ordinal);
        var failureGuardIndex = paymentViewSource.IndexOf(
            "if (state.PendingPaymentCount == 0 &&\n                SyncCoordinator.IsFailedImmediateSaveWithoutServerAcceptance(state))",
            saveCallIndex,
            StringComparison.Ordinal);
        var successBranchIndex = paymentViewSource.IndexOf(
            "if (state.PendingPaymentCount == 0)\n            {\n                _refreshCoordinator.MarkInvoicesChanged();",
            failureGuardIndex + 1,
            StringComparison.Ordinal);
        var successCallbackIndex = paymentViewSource.IndexOf(
            "await SavedSuccessfully.Invoke();",
            successBranchIndex,
            StringComparison.Ordinal);

        Assert.True(saveCallIndex >= 0);
        Assert.True(failureGuardIndex > saveCallIndex);
        Assert.True(successBranchIndex > failureGuardIndex);
        Assert.True(successCallbackIndex > successBranchIndex);
        Assert.Contains("public static bool IsFailedImmediateSaveWithoutServerAcceptance(MobileSyncState state)", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("!HasServerAcceptanceDuringCurrentAttempt(state);", coordinatorSource, StringComparison.Ordinal);
        Assert.Matches(
            "if \\(state\\.PendingPaymentCount == 0 &&\\n\\s*SyncCoordinator\\.IsFailedImmediateSaveWithoutServerAcceptance\\(state\\)\\)\\n\\s*\\{\\n\\s*StatusMessage = \\$\"\\{PaymentActionText\\}[^\\n]+\\{state\\.LastError\\}\";\\n\\s*return;\\n\\s*\\}",
            paymentViewSource);
    }

    [Fact]
    public void MobileImmediateAuthenticationFailure_DoesNotQueueDirtyOrReportSaved()
    {
        var root = FindRepositoryRoot();
        var coordinatorSource = File.ReadAllText(Path.Combine(
                root,
                "Mobile",
                "GeoraePlan.Mobile.App",
                "Services",
                "SyncCoordinator.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var invoiceViewSource = File.ReadAllText(Path.Combine(
                root,
                "Mobile",
                "GeoraePlan.Mobile.App",
                "ViewModels",
                "InvoiceDraftViewModel.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var paymentViewSource = File.ReadAllText(Path.Combine(
                root,
                "Mobile",
                "GeoraePlan.Mobile.App",
                "ViewModels",
                "PaymentDraftViewModel.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        var saveInvoiceMethod = ExtractBetween(
            coordinatorSource,
            "public async Task<MobileSyncState> SaveInvoiceImmediatelyAsync",
            "public async Task<MobileSyncState> SavePaymentImmediatelyAsync");
        var savePaymentMethod = ExtractBetween(
            coordinatorSource,
            "public async Task<MobileSyncState> SavePaymentImmediatelyAsync",
            "public async Task<MobileSyncState> TryBackgroundSyncAsync");

        Assert.Contains("ex is MobileAuthenticationException ||", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("or HttpStatusCode.Unauthorized", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains(
            "MobileAuthenticationException => \"인증이 만료되었거나 복구되지 않았습니다. 다시 로그인해 주세요.\"",
            coordinatorSource,
            StringComparison.Ordinal);

        Assert.Contains(
            "catch (Exception ex) when (IsNonRetryableClientFailure(ex))\n            {\n                MarkFailure(state, ex);\n            }",
            saveInvoiceMethod,
            StringComparison.Ordinal);
        Assert.Contains(
            "catch (Exception ex) when (IsNonRetryableClientFailure(ex))\n            {\n                MarkFailure(state, ex);\n            }",
            savePaymentMethod,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "catch (Exception ex) when (IsNonRetryableClientFailure(ex))\n            {\n                state.PendingPush.Invoices.Add(invoice);",
            saveInvoiceMethod,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "catch (Exception ex) when (IsNonRetryableClientFailure(ex))\n            {\n                state.PendingPush.Payments.Add(payment);",
            savePaymentMethod,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (state.PendingInvoiceCount == 0 &&\n                SyncCoordinator.IsFailedImmediateSaveWithoutServerAcceptance(state))\n            {\n                StatusMessage = $\"{DocumentKindText} 전표가 저장되지 않았습니다. {state.LastError}\";\n                return;\n            }",
            invoiceViewSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (state.PendingPaymentCount == 0 &&\n                SyncCoordinator.IsFailedImmediateSaveWithoutServerAcceptance(state))\n            {\n                StatusMessage = $\"{PaymentActionText}이 저장되지 않았습니다. {state.LastError}\";\n                return;\n            }",
            paymentViewSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MobileImmediateSave_PostSaveRefreshOrAttachmentFailureStillShowsSaved()
    {
        var root = FindRepositoryRoot();
        var coordinatorSource = File.ReadAllText(Path.Combine(
                root,
                "Mobile",
                "GeoraePlan.Mobile.App",
                "Services",
                "SyncCoordinator.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var invoiceViewSource = File.ReadAllText(Path.Combine(
                root,
                "Mobile",
                "GeoraePlan.Mobile.App",
                "ViewModels",
                "InvoiceDraftViewModel.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var paymentViewSource = File.ReadAllText(Path.Combine(
                root,
                "Mobile",
                "GeoraePlan.Mobile.App",
                "ViewModels",
                "PaymentDraftViewModel.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("private static bool HasServerAcceptanceDuringCurrentAttempt(MobileSyncState state)", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("state.LastSuccessUtc.Value >= state.LastAttemptUtc.Value;", coordinatorSource, StringComparison.Ordinal);
        Assert.True(
            coordinatorSource.Split("state = await PullInternalAsync(state, ct);\n                state.LastSuccessUtc = DateTime.UtcNow;", StringSplitOptions.None).Length >= 3,
            "Invoice and payment immediate-save paths must rewrite LastSuccessUtc after PullInternalAsync so refresh failures are not treated as save rejection.");
        Assert.Contains(
            "state = await PullInternalAsync(state, ct);\n                state.LastSuccessUtc = DateTime.UtcNow;\n                RestorePaymentAttachmentUploadErrorsAfterPull(state, attachmentUploadErrors);",
            coordinatorSource,
            StringComparison.Ordinal);

        Assert.Contains(
            "if (state.PendingInvoiceCount == 0 &&\n                SyncCoordinator.IsFailedImmediateSaveWithoutServerAcceptance(state))",
            invoiceViewSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (state.PendingPaymentCount == 0 &&\n                SyncCoordinator.IsFailedImmediateSaveWithoutServerAcceptance(state))",
            paymentViewSource,
            StringComparison.Ordinal);
        Assert.Contains("최신 데이터 새로고침 대기", invoiceViewSource, StringComparison.Ordinal);
        Assert.Contains("최신 데이터 새로고침 대기", paymentViewSource, StringComparison.Ordinal);
        Assert.Contains("첨부 {state.PendingPaymentAttachmentCount:N0}건", paymentViewSource, StringComparison.Ordinal);
    }

    [Fact]
    public void MobileImmediatePaymentConflict_QueuesOnlyAcceptedAttachmentsBeforeReturning()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Services",
            "SyncCoordinator.cs"));
        var normalizedSource = source.Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("private static void QueuePaymentAttachmentsForRetry(", source, StringComparison.Ordinal);
        Assert.Contains("QueuePaymentAttachmentsForRetry(state, payment.Id, attachmentList);", source, StringComparison.Ordinal);
        Assert.Contains("QueuePaymentAttachmentsForRetry(state, paymentId, attachmentList);", source, StringComparison.Ordinal);
        Assert.Contains("QueueDiscardedPaymentAttachmentDrafts(attachmentList);", source, StringComparison.Ordinal);
        Assert.Contains("state.PendingPaymentAttachments.RemoveAll(x => x.LocalId == attachment.LocalId);", source, StringComparison.Ordinal);
        Assert.Contains("state.PendingPaymentAttachments.Add(attachment);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("QueueUnacceptedLinkedPaymentConflict", source, StringComparison.Ordinal);
        Assert.Matches(
            "if \\(result\\.ConflictCount > 0\\)\\n\\s*\\{\\n\\s*if \\(WasAccepted\\(result\\.AcceptedRevisions, PaymentEntityName, payment\\.Id\\)\\)\\n\\s*QueuePaymentAttachmentsForRetry\\(state, payment\\.Id, attachmentList\\);\\n\\s*else\\n\\s*QueueDiscardedPaymentAttachmentDrafts\\(attachmentList\\);",
            normalizedSource);
    }

    [Fact]
    public void AndroidSyncStateStore_TreatsSettingPendingMutationsAsLegacyPayload()
    {
        var root = FindRepositoryRoot();
        var storeSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Services",
            "JsonSyncStateStore.cs"));
        var coordinatorSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Services",
            "SyncCoordinator.cs"));

        Assert.Contains("if (!HasPendingPayload(legacyState))", storeSource, StringComparison.Ordinal);
        Assert.Contains("Directory.CreateDirectory(LegacyQuarantineDirectory);", storeSource, StringComparison.Ordinal);

        var settingPendingCollections = new[]
        {
            "CompanyProfiles",
            "Units",
            "CustomerCategories",
            "PriceGradeOptions",
            "TradeTypeOptions",
            "ItemCategoryOptions",
            "CustomerMasters"
        };

        foreach (var collection in settingPendingCollections)
        {
            Assert.Contains($"(pendingPush.{collection}?.Count ?? 0) > 0", coordinatorSource, StringComparison.Ordinal);
            Assert.Contains($"(state.PendingPush.{collection}?.Count ?? 0) > 0", storeSource, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AndroidSyncStateStore_UsesSqlitePrimaryStorageWithJsonMigrationFallback()
    {
        var root = FindRepositoryRoot();
        var projectSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "GeoraePlan.Mobile.App.csproj"));
        var storeSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Services",
            "JsonSyncStateStore.cs"));

        Assert.Contains("Microsoft.Data.Sqlite.Core", projectSource, StringComparison.Ordinal);
        Assert.Contains("SQLitePCLRaw.bundle_e_sqlite3", projectSource, StringComparison.Ordinal);
        Assert.Contains("private const string StateTableName = \"mobile_sync_states\";", storeSource, StringComparison.Ordinal);
        Assert.Contains("private string DatabasePath => Path.Combine(StateDirectory, \"mobile-sync-state.db\");", storeSource, StringComparison.Ordinal);
        Assert.Contains("SQLitePCL.Batteries_V2.Init();", storeSource, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE IF NOT EXISTS {StateTableName}", storeSource, StringComparison.Ordinal);
        Assert.Contains("INSERT OR REPLACE INTO {StateTableName}", storeSource, StringComparison.Ordinal);
        Assert.Contains("await TrySaveJsonBackupAsync(filePath, state, ct);", storeSource, StringComparison.Ordinal);
        Assert.Contains("SQLite 저장은 완료됐지만 JSON 백업 저장에는 실패했습니다", storeSource, StringComparison.Ordinal);
        Assert.Contains("await TryRecoverOrQuarantineLegacyStateAsync(stateKey, filePath, fresh, ct);", storeSource, StringComparison.Ordinal);
        Assert.Contains("기존 JSON 모바일 동기화 상태를 SQLite로 이전했습니다", storeSource, StringComparison.Ordinal);
        Assert.Contains("SQLite 모바일 동기화 상태 저장에 실패해 JSON 백업 저장소로 전환합니다", storeSource, StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidPendingStatus_SummarizesEveryPendingPushCollection()
    {
        var root = FindRepositoryRoot();
        var stateSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Models",
            "MobileSyncState.cs"));
        var syncViewSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "ViewModels",
            "SyncViewModel.cs"));
        var homeViewSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "ViewModels",
            "HomeViewModel.cs"));

        var missingPreviouslyHiddenCollections = new[]
        {
            "PendingCompanyProfileCount",
            "PendingUnitCount",
            "PendingCustomerCategoryCount",
            "PendingPriceGradeOptionCount",
            "PendingTradeTypeOptionCount",
            "PendingItemCategoryOptionCount",
            "PendingCustomerMasterCount",
            "PendingCustomerContractCount"
        };

        foreach (var property in missingPreviouslyHiddenCollections)
            Assert.Contains($"public int {property} =>", stateSource, StringComparison.Ordinal);

        Assert.Contains("public int PendingSettingCount =>", stateSource, StringComparison.Ordinal);
        Assert.Contains("public int PendingServerMutationCount =>", stateSource, StringComparison.Ordinal);
        Assert.Contains("public int PendingTotalCount =>", stateSource, StringComparison.Ordinal);

        foreach (var source in new[] { syncViewSource, homeViewSource })
        {
            Assert.Contains("PendingSettingCount", source, StringComparison.Ordinal);
            Assert.Contains("PendingCustomerMasterCount", source, StringComparison.Ordinal);
            Assert.Contains("PendingCustomerContractCount", source, StringComparison.Ordinal);
            Assert.Contains("PendingTransactionAttachmentCount", source, StringComparison.Ordinal);
            Assert.Contains("PendingInventoryTransferCount", source, StringComparison.Ordinal);
            Assert.Contains("PendingRentalManagementCompanyCount", source, StringComparison.Ordinal);
            Assert.Contains("PendingTotalCount", source, StringComparison.Ordinal);
        }

        Assert.Contains("MobilePendingScopeFilter.CreateSummary(_sessionStore.GetSnapshot(), state)", syncViewSource, StringComparison.Ordinal);
        Assert.Contains("설정 {summary.PendingSettingCount", syncViewSource, StringComparison.Ordinal);
        Assert.Contains("거래처기준", syncViewSource, StringComparison.Ordinal);
        Assert.Contains("계약", syncViewSource, StringComparison.Ordinal);
        Assert.Contains("거래첨부", syncViewSource, StringComparison.Ordinal);
        Assert.Contains("재고이동", syncViewSource, StringComparison.Ordinal);
        Assert.Contains("MobilePendingScopeFilter.CreateSummary(session, sync)", homeViewSource, StringComparison.Ordinal);
        Assert.Contains("설정 {pendingSummary.PendingSettingCount", homeViewSource, StringComparison.Ordinal);
        Assert.Contains("거래처기준", homeViewSource, StringComparison.Ordinal);
        Assert.Contains("계약", homeViewSource, StringComparison.Ordinal);
        Assert.Contains("거래첨부", homeViewSource, StringComparison.Ordinal);
        Assert.Contains("재고이동", homeViewSource, StringComparison.Ordinal);
    }

    [Fact]
    public void MobileSyncPush_DoesNotClearPendingOnEmptyPushResponse()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Services",
            "SyncCoordinator.cs"));
        var normalizedSource = source.Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("private static SyncPushResult EnsurePushResult(SyncPushResult? result)", source, StringComparison.Ordinal);
        Assert.Contains("동기화 push 응답이 비어 있어 서버 반영 여부를 확인할 수 없습니다.", source, StringComparison.Ordinal);
        Assert.Contains("var result = EnsurePushResult(await _api.PushAsync(scopedPush, ct));", source, StringComparison.Ordinal);
        Assert.Contains("var result = EnsurePushResult(await _api.PushAsync(request, ct));", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "var result = await _api.PushAsync(state.PendingPush, ct);\n                if (result is not null)",
            normalizedSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "var result = await _api.PushAsync(request, ct);\n                    if (result is not null)",
            normalizedSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MobileSyncPullStatus_DoesNotTreatEmptyResponsesAsSuccess()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Services",
            "SyncCoordinator.cs"));
        var normalizedSource = source.Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("private static SyncPullResponse EnsurePullResponse(SyncPullResponse? response)", source, StringComparison.Ordinal);
        Assert.Contains("private static SyncStatusDto EnsureSyncStatus(SyncStatusDto? status)", source, StringComparison.Ordinal);
        Assert.Contains("동기화 pull 응답이 비어 있어 최신 데이터 반영 여부를 확인할 수 없습니다.", source, StringComparison.Ordinal);
        Assert.Contains("동기화 상태 응답이 비어 있어 최신 데이터 여부를 확인할 수 없습니다.", source, StringComparison.Ordinal);
        Assert.Contains("var response = EnsurePullResponse(await _api.PullAsync(state.LastRevision, ct));", source, StringComparison.Ordinal);
        Assert.Contains("var syncStatus = EnsureSyncStatus(await _api.GetSyncStatusAsync(ct));", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "var response = await _api.PullAsync(state.LastRevision, ct);\n                if (response is not null)",
            normalizedSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "var syncStatus = await _api.GetSyncStatusAsync(ct);\n                if (syncStatus is not null",
            normalizedSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MobilePaymentAttachmentDraftStore_CleansOnlyOldOrphanDraftFiles()
    {
        var root = FindRepositoryRoot();
        var coordinatorSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Services",
            "SyncCoordinator.cs"));
        var draftStoreSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Services",
            "PaymentAttachmentDraftStore.cs"));

        Assert.Contains("public async Task<int> RemoveOrphanDraftsAsync(", draftStoreSource, StringComparison.Ordinal);
        Assert.Contains("Directory.EnumerateFiles(DraftDirectory, \"*\", SearchOption.TopDirectoryOnly)", draftStoreSource, StringComparison.Ordinal);
        Assert.Contains("IsDraftFileName(Path.GetFileName(fullPath))", draftStoreSource, StringComparison.Ordinal);
        Assert.Contains("activePaths.Contains(fullPath)", draftStoreSource, StringComparison.Ordinal);
        Assert.Contains("File.GetLastWriteTimeUtc(fullPath) > cutoffUtc", draftStoreSource, StringComparison.Ordinal);
        Assert.Contains("NormalizeDraftPathOrNull(attachment.StoredPath, draftRoot)", draftStoreSource, StringComparison.Ordinal);
        Assert.Contains("File.Exists(storedPath)", draftStoreSource, StringComparison.Ordinal);
        Assert.Matches("(?s)private static string\\? NormalizeDraftPathOrNull\\(string\\? path, string draftRoot\\).*catch\\s*\\{\\s*return null;\\s*\\}", draftStoreSource);
        Assert.Contains("EnsureTrailingDirectorySeparator(Path.GetFullPath(DraftDirectory))", draftStoreSource, StringComparison.Ordinal);
        Assert.Contains("private static bool IsDraftFileName(string? fileName)", draftStoreSource, StringComparison.Ordinal);
        Assert.Contains("private static string EnsureTrailingDirectorySeparator(string path)", draftStoreSource, StringComparison.Ordinal);

        Assert.Contains("private static readonly TimeSpan OrphanPaymentAttachmentDraftMinimumAge = TimeSpan.FromDays(7);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("public async Task<MobileSyncState> LoadAsync(CancellationToken ct = default)", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("await CleanupOrphanPaymentAttachmentDraftsAsync(state, ct);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("await _attachmentStore.RemoveOrphanDraftsAsync(", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("await SaveStateAndRemoveDiscardedPaymentAttachmentDraftsAsync(state, ct);", coordinatorSource, StringComparison.Ordinal);
    }

    [Fact]
    public void MobilePaymentAttachmentUpload_DoesNotDeleteDraftOnEmptyUploadResponse()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Services",
            "SyncCoordinator.cs"));

        Assert.Contains("private static PaymentAttachmentDto EnsurePaymentAttachmentResult(PaymentAttachmentDto? result)", source, StringComparison.Ordinal);
        Assert.Contains("첨부 업로드 응답이 비어 있어 서버 저장 여부를 확인할 수 없습니다.", source, StringComparison.Ordinal);
        Assert.Contains("EnsurePaymentAttachmentResult(await _api.UploadPaymentAttachmentAsync(payment.Id, attachment, ct));", source, StringComparison.Ordinal);
        Assert.Contains("EnsurePaymentAttachmentResult(await _api.UploadPaymentAttachmentAsync(attachment.PaymentId, attachment, ct));", source, StringComparison.Ordinal);
        Assert.Contains("state.PendingPaymentAttachments.Add(attachment);", source, StringComparison.Ordinal);
        Assert.Contains("errors.Add(ex.Message);", source, StringComparison.Ordinal);
        Assert.Contains("RemovePendingPaymentAttachments(state, attachment => uploadedIds.Contains(attachment.LocalId));", source, StringComparison.Ordinal);
        Assert.Contains("await SaveStateAndRemoveDiscardedPaymentAttachmentDraftsAsync(state, ct);", source, StringComparison.Ordinal);
        Assert.Contains("await RemoveDiscardedPaymentAttachmentDraftsAsync(ct);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MobilePaymentAttachmentUpload_StopsRetryingTerminalFailures()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Services",
            "SyncCoordinator.cs"));
        var normalizedSource = source.Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("private static bool ShouldRetryPaymentAttachmentUpload(Exception ex)", source, StringComparison.Ordinal);
        Assert.Contains(
            "MobileRetryableNetworkFailure.IsRetryable(ex) ||\n           ex is MobileAuthenticationException",
            normalizedSource,
            StringComparison.Ordinal);
        Assert.Contains("private static string BuildTerminalPaymentAttachmentUploadFailureMessage(", source, StringComparison.Ordinal);
        Assert.Contains("catch (Exception uploadEx) when (ShouldRetryPaymentAttachmentUpload(uploadEx))", source, StringComparison.Ordinal);
        Assert.Contains("BuildTerminalPaymentAttachmentUploadFailureMessage(attachment, uploadEx)", source, StringComparison.Ordinal);
        Assert.Contains("terminalFailedAttachments.Add(attachment);", source, StringComparison.Ordinal);
        Assert.Contains("QueueDiscardedPaymentAttachmentDrafts(terminalFailedAttachments);", source, StringComparison.Ordinal);
        Assert.Contains("catch (Exception ex) when (ShouldRetryPaymentAttachmentUpload(ex))", source, StringComparison.Ordinal);
        Assert.Contains(
            "uploadedIds.Add(attachment.LocalId);\n                errors.Add(BuildTerminalPaymentAttachmentUploadFailureMessage(attachment, ex));",
            normalizedSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "catch (Exception uploadEx)\n                    {\n                        state.PendingPaymentAttachments.Add(attachment);\n                        attachmentUploadErrors.Add(uploadEx.Message);",
            normalizedSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "catch (Exception ex)\n            {\n                errors.Add(ex.Message);\n            }",
            normalizedSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MobileImmediatePaymentSave_PreservesAttachmentUploadFailureAfterPull()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Services",
            "SyncCoordinator.cs"));
        var normalizedSource = source.Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("var attachmentUploadErrors = new List<string>();", source, StringComparison.Ordinal);
        Assert.Contains("attachmentUploadErrors.Add(uploadEx.Message);", source, StringComparison.Ordinal);
        Assert.Contains("RestorePaymentAttachmentUploadErrorsAfterPull(state, attachmentUploadErrors);", source, StringComparison.Ordinal);
        Assert.Contains("수금/지급은 저장됐지만 첨부", source, StringComparison.Ordinal);
        Assert.Contains("다음 동기화에서 다시 시도합니다", source, StringComparison.Ordinal);
        Assert.Contains("state.ConsecutiveFailureCount = Math.Max(1, state.ConsecutiveFailureCount);", source, StringComparison.Ordinal);
        Assert.Matches(
            "state = await PullInternalAsync\\(state, ct\\);\\n\\s*state\\.LastSuccessUtc = DateTime\\.UtcNow;\\n\\s*RestorePaymentAttachmentUploadErrorsAfterPull\\(state, attachmentUploadErrors\\);",
            normalizedSource);
    }

    [Fact]
    public void MobileImmediateSave_DoesNotTreatEmptyEntityResponseAsSuccess()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Services",
            "SyncCoordinator.cs"));

        Assert.Contains("private static T EnsureEntityResult<T>(T? result, string operationName)", source, StringComparison.Ordinal);
        Assert.Contains("where T : SyncEntityDto", source, StringComparison.Ordinal);
        Assert.Contains("{operationName} 응답이 비어 있어 서버 반영 여부를 확인할 수 없습니다.", source, StringComparison.Ordinal);
        Assert.Contains("saved = EnsureEntityResult(saved, \"전표 저장\");", source, StringComparison.Ordinal);
        Assert.Contains("saved = EnsureEntityResult(saved, \"입금 저장\");", source, StringComparison.Ordinal);
        Assert.Contains("state.PendingPush.Invoices.Add(invoice);", source, StringComparison.Ordinal);
        Assert.Contains("state.PendingPush.Payments.Add(payment);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MobileCustomerItemEdit_DoesNotTreatEmptyEntityResponseAsSuccess()
    {
        var root = FindRepositoryRoot();
        var customerEditSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Pages",
            "CustomerEditPage.cs"));
        var itemEditSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Pages",
            "ItemEditPage.cs"));

        Assert.Contains("saved = EnsureSavedResult(saved, \"거래처 저장\");", customerEditSource, StringComparison.Ordinal);
        Assert.Contains("private static T EnsureSavedResult<T>(T? result, string operationName)", customerEditSource, StringComparison.Ordinal);
        Assert.Contains("{operationName} 응답이 비어 있어 서버 반영 여부를 확인할 수 없습니다.", customerEditSource, StringComparison.Ordinal);
        Assert.Contains("await QueuePendingSaveAsync(dto, ex);", customerEditSource, StringComparison.Ordinal);

        Assert.Contains("saved = EnsureSavedResult(saved, \"품목 저장\");", itemEditSource, StringComparison.Ordinal);
        Assert.Contains("private static T EnsureSavedResult<T>(T? result, string operationName)", itemEditSource, StringComparison.Ordinal);
        Assert.Contains("{operationName} 응답이 비어 있어 서버 반영 여부를 확인할 수 없습니다.", itemEditSource, StringComparison.Ordinal);
        Assert.Contains("await QueuePendingSaveAsync(dto, ex);", itemEditSource, StringComparison.Ordinal);
    }

    [Fact]
    public void MobileRentalAssignments_PullsPersistsAndDisplaysInstallationHistory()
    {
        var root = FindRepositoryRoot();
        var stateSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Models",
            "MobileSyncState.cs"));
        var coordinatorSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Services",
            "SyncCoordinator.cs"));
        var storeSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Services",
            "JsonSyncStateStore.cs"));
        var viewModelSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "ViewModels",
            "RentalsViewModel.cs"));
        var pageSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Pages",
            "RentalsPage.cs"));
        var syncViewSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "ViewModels",
            "SyncViewModel.cs"));
        var homeViewSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "ViewModels",
            "HomeViewModel.cs"));
        var smokeSource = File.ReadAllText(Path.Combine(
            root,
            "tools",
            "mobile",
            "Invoke-GeoraePlanAndroidSmoke.ps1"));

        Assert.Contains("public int LastPulledRentalAssetAssignmentHistoryCount { get; set; }", stateSource, StringComparison.Ordinal);
        Assert.Contains("public List<RentalAssetAssignmentHistoryDto> SyncedRentalAssetAssignmentHistories { get; set; } = new();", stateSource, StringComparison.Ordinal);
        Assert.Contains("public int PendingRentalAssetAssignmentHistoryCount => PendingPush.RentalAssetAssignmentHistories?.Count ?? 0;", stateSource, StringComparison.Ordinal);
        Assert.Contains("PendingPush.RentalAssetAssignmentHistories ??= new List<RentalAssetAssignmentHistoryDto>();", stateSource, StringComparison.Ordinal);
        Assert.Contains("SyncedRentalAssetAssignmentHistories ??= new List<RentalAssetAssignmentHistoryDto>();", stateSource, StringComparison.Ordinal);

        Assert.Contains("QueueRentalAssetAssignmentHistoryDraftAsync", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("state.PendingPush.RentalAssetAssignmentHistories.RemoveAll(x => x.Id == history.Id)", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("(pendingPush.RentalAssetAssignmentHistories?.Count ?? 0) > 0", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("state.LastPulledRentalAssetAssignmentHistoryCount = response.RentalAssetAssignmentHistories.Count;", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("state.SyncedRentalAssetAssignmentHistories = MergeById(state.SyncedRentalAssetAssignmentHistories, response.RentalAssetAssignmentHistories);", coordinatorSource, StringComparison.Ordinal);

        Assert.Contains("state.PendingRentalAssetAssignmentHistoryCount > 0", storeSource, StringComparison.Ordinal);
        Assert.Contains("target.LastPulledRentalAssetAssignmentHistoryCount = source.LastPulledRentalAssetAssignmentHistoryCount;", storeSource, StringComparison.Ordinal);
        Assert.Contains("target.SyncedRentalAssetAssignmentHistories = source.SyncedRentalAssetAssignmentHistories;", storeSource, StringComparison.Ordinal);

        Assert.Contains("AssignmentHistories = 3", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("public ObservableCollection<RentalAssignmentHistoryDisplayRow> AssignmentHistories { get; } = new();", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("state.PendingPush.RentalAssetAssignmentHistories", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("BuildAssignmentHistoryRows(effectiveAssignmentHistories, profileMap, assetMap)", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("assignmentHistories.Where(history => !history.IsDeleted)", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("RentalAssignmentHistoryDisplayRow.FromHistory(history, profile, asset)", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("MatchesAssignmentHistory", viewModelSource, StringComparison.Ordinal);

        Assert.Contains("CreateAssignmentHistoriesView", pageSource, StringComparison.Ordinal);
        Assert.Contains("nameof(RentalsViewModel.AssignmentHistories)", pageSource, StringComparison.Ordinal);
        Assert.Contains("nameof(RentalsViewModel.IsAssignmentHistoriesSection)", pageSource, StringComparison.Ordinal);
        Assert.Contains("nameof(RentalAssignmentHistoryDisplayRow.Title)", pageSource, StringComparison.Ordinal);

        Assert.Contains("PendingRentalAssetAssignmentHistoryCount", syncViewSource, StringComparison.Ordinal);
        Assert.Contains("LastPulledRentalAssetAssignmentHistoryCount", syncViewSource, StringComparison.Ordinal);
        Assert.Contains("PendingRentalAssetAssignmentHistoryCount", homeViewSource, StringComparison.Ordinal);
        Assert.Contains("'설치이력'", smokeSource, StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidCustomerDetail_DisplaysLinkedRentalProfilesAssetsAndAssignmentHistory()
    {
        var root = FindRepositoryRoot();
        var contractsSource = File.ReadAllText(Path.Combine(
            root,
            "Shared",
            "거래플랜.Shared.Contracts",
            "Contracts.cs"));
        var viewModelSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "ViewModels",
            "CustomersViewModel.cs"));
        var paymentRowSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Models",
            "CustomerPaymentHistoryRow.cs"));
        var attachmentsPageSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Pages",
            "PaymentAttachmentsPage.cs"));
        var pageSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Pages",
            "CustomersPage.cs"));

        Assert.Contains("public List<CustomerPaymentHistoryDto> RecentPayments { get; set; } = new();", contractsSource, StringComparison.Ordinal);
        Assert.Contains("public sealed class CustomerPaymentHistoryDto", contractsSource, StringComparison.Ordinal);
        Assert.Contains("Rentals = 4", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("public ObservableCollection<CustomerRentalLinkRow> SelectedCustomerRentals { get; } = new();", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("public bool ShowRentalsSection => SelectedDetailSection == CustomerDetailSection.Rentals;", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("public double RentalsSectionHeight => CalculateListHeight(SelectedCustomerRentals.Count, 104, 42, 3);", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("BuildPaymentRows(detail)", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("detail?.RecentPayments ?? []", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("CustomerPaymentHistoryRow.From", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("BuildPaymentRowsFromInvoices", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("public static CustomerPaymentHistoryRow From(CustomerPaymentHistoryDto payment)", paymentRowSource, StringComparison.Ordinal);
        Assert.Contains("public IReadOnlyList<PaymentAttachmentDto> Attachments { get; init; } = [];", paymentRowSource, StringComparison.Ordinal);
        Assert.Contains("Attachments = payment.Attachments?.Where(attachment => !attachment.IsDeleted).ToList() ?? []", paymentRowSource, StringComparison.Ordinal);
        Assert.Contains("row.Attachments", pageSource, StringComparison.Ordinal);
        Assert.Contains("IEnumerable<PaymentAttachmentDto>? fallbackAttachments = null", attachmentsPageSource, StringComparison.Ordinal);
        Assert.Contains("_fallbackAttachments = fallbackAttachments?.Where(attachment => attachment is not null && !attachment.IsDeleted).ToList() ?? [];", attachmentsPageSource, StringComparison.Ordinal);
        Assert.Contains("BuildCustomerRentalRows(displayCustomer, syncState)", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("MobileSessionScopeFilter.CanAccessRentalBillingProfile(snapshot, profile)", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("MobileSessionScopeFilter.CanAccessRentalAsset(snapshot, asset)", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("MobileSessionScopeFilter.CanAccessRentalAssetAssignmentHistory(snapshot, history)", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("var matchContext = BuildCustomerRentalMatchContext(customer, state, snapshot);", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("MatchesSelectedCustomer(matchContext, profile.CustomerId, profile.BusinessNumber, profile.CustomerName)", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("CustomerRentalLinkRow.FromProfile(profile)", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("CustomerRentalLinkRow.FromAsset(asset, profile)", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("CustomerRentalLinkRow.FromAssignmentHistory(history, profile, asset)", viewModelSource, StringComparison.Ordinal);

        Assert.Contains("CreateInlineDetailTabButton(\"렌탈\", CustomerDetailSection.Rentals)", pageSource, StringComparison.Ordinal);
        Assert.Contains("_viewModel.ShowRentalsTab()", pageSource, StringComparison.Ordinal);
        Assert.Contains("CreateCustomerRentalsView", pageSource, StringComparison.Ordinal);
        Assert.Contains("nameof(CustomersViewModel.SelectedCustomerRentals)", pageSource, StringComparison.Ordinal);
        Assert.Contains("nameof(CustomersViewModel.ShowRentalsSection)", pageSource, StringComparison.Ordinal);
        Assert.Contains("nameof(CustomerRentalLinkRow.Title)", pageSource, StringComparison.Ordinal);
        Assert.Contains("연결된 렌탈 프로필/자산/설치이력이 없습니다.", pageSource, StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidCustomerDetail_RentalFallbackMatchesOnlyUnambiguousCustomerKeys()
    {
        var root = FindRepositoryRoot();
        var viewModelSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "ViewModels",
            "CustomersViewModel.cs"));

        Assert.Contains("private static CustomerRentalMatchContext BuildCustomerRentalMatchContext(", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("SessionSnapshot snapshot)", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("state.SyncedCustomers", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("BuildUniqueSelectedBusinessNumberKeys(customer, scopedCustomers)", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("BuildUniqueSelectedNameKeys(customer, scopedCustomers)", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("customerIds.Count == 1 && customerIds[0] == customer.Id", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("context.UniqueSelectedBusinessNumberKeys.Contains(candidateBusinessNumberKey)", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("context.UniqueSelectedNameKeys.Contains(key)", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("private sealed class CustomerRentalMatchContext", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("public HashSet<string> UniqueSelectedBusinessNumberKeys { get; init; } = new(StringComparer.Ordinal);", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("public HashSet<string> UniqueSelectedNameKeys { get; init; } = new(StringComparer.OrdinalIgnoreCase);", viewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("string.Equals(customerBusinessNumber, candidateBusinessNumberKey, StringComparison.Ordinal)", viewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("return candidateNames.Any(name => customerKeys.Contains(NormalizeNameKey(name)));", viewModelSource, StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidItems_PullsPersistsAndUsesItemWarehouseStockCache()
    {
        var root = FindRepositoryRoot();
        var stateSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Models",
            "MobileSyncState.cs"));
        var coordinatorSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Services",
            "SyncCoordinator.cs"));
        var storeSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Services",
            "JsonSyncStateStore.cs"));
        var itemsViewSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "ViewModels",
            "ItemsViewModel.cs"));
        var syncViewSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "ViewModels",
            "SyncViewModel.cs"));
        var homeViewSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "ViewModels",
            "HomeViewModel.cs"));

        Assert.Contains("public int LastPulledItemWarehouseStockCount { get; set; }", stateSource, StringComparison.Ordinal);
        Assert.Contains("public List<ItemDto> SyncedItems { get; set; } = new();", stateSource, StringComparison.Ordinal);
        Assert.Contains("public List<ItemWarehouseStockDto> SyncedItemWarehouseStocks { get; set; } = new();", stateSource, StringComparison.Ordinal);
        Assert.Contains("public int PendingItemWarehouseStockCount => PendingPush.ItemWarehouseStocks?.Count ?? 0;", stateSource, StringComparison.Ordinal);
        Assert.Contains("SyncedItems ??= new List<ItemDto>();", stateSource, StringComparison.Ordinal);
        Assert.Contains("SyncedItemWarehouseStocks ??= new List<ItemWarehouseStockDto>();", stateSource, StringComparison.Ordinal);

        Assert.Contains("state.LastPulledItemWarehouseStockCount = response.ItemWarehouseStocks.Count;", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("state.SyncedItems = MergeById(state.SyncedItems, response.Items);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("state.SyncedItemWarehouseStocks = ReplaceItemWarehouseStocks(response.ItemWarehouseStocks);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("private static List<ItemWarehouseStockDto> ReplaceItemWarehouseStocks", coordinatorSource, StringComparison.Ordinal);

        Assert.Contains("target.LastPulledItemWarehouseStockCount = source.LastPulledItemWarehouseStockCount;", storeSource, StringComparison.Ordinal);
        Assert.Contains("target.SyncedItems = source.SyncedItems;", storeSource, StringComparison.Ordinal);
        Assert.Contains("target.SyncedItemWarehouseStocks = source.SyncedItemWarehouseStocks;", storeSource, StringComparison.Ordinal);

        Assert.Contains("JsonSyncStateStore syncStateStore", itemsViewSource, StringComparison.Ordinal);
        Assert.Contains("SyncCoordinator syncCoordinator", itemsViewSource, StringComparison.Ordinal);
        Assert.Contains("await _syncCoordinator.RefreshIfServerChangedAsync(\"items-refresh\", TimeSpan.FromSeconds(5));", itemsViewSource, StringComparison.Ordinal);
        Assert.Contains("TryLoadCategoriesFromSyncedStateAsync", itemsViewSource, StringComparison.Ordinal);
        Assert.Contains("TrySearchItemsFromSyncedStateAsync", itemsViewSource, StringComparison.Ordinal);
        Assert.Contains("TrySelectItemFromSyncedStateAsync", itemsViewSource, StringComparison.Ordinal);
        Assert.Contains("state.SyncedItemWarehouseStocks", itemsViewSource, StringComparison.Ordinal);
        Assert.Contains("PopulateSelectedItem(selected, branchStocks)", itemsViewSource, StringComparison.Ordinal);

        Assert.Contains("PendingItemWarehouseStockCount", syncViewSource, StringComparison.Ordinal);
        Assert.Contains("LastPulledItemWarehouseStockCount", syncViewSource, StringComparison.Ordinal);
        Assert.Contains("PendingItemWarehouseStockCount", homeViewSource, StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidInvoiceDraft_UsesSyncedItemAndWarehouseStockCacheWhenItemApiFails()
    {
        var root = FindRepositoryRoot();
        var viewModelSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "ViewModels",
            "InvoiceDraftViewModel.cs"));
        var smokeSource = File.ReadAllText(Path.Combine(
            root,
            "tools",
            "mobile",
            "Invoke-GeoraePlanAndroidSmoke.ps1"));

        Assert.Contains("TryLoadItemCategoriesFromSyncedStateAsync", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("TrySearchItemsFromSyncedStateAsync", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("TryOpenItemEntrySheetFromSyncedStateAsync", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("var state = await _syncCoordinator.LoadAsync();", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("state.SyncedItems", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("state.SyncedItemWarehouseStocks", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("MatchesItem(item, keyword)", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("ResolveDefaultUnitPrice(selected).ToString(\"0.##\")", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("AddFallbackWholeStockRow", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("동기화 캐시 기준으로 선택했습니다", viewModelSource, StringComparison.Ordinal);

        Assert.Contains("-StepName 'sales-draft'", smokeSource, StringComparison.Ordinal);
        Assert.Contains("-StepName 'purchase-draft'", smokeSource, StringComparison.Ordinal);
        Assert.Contains("'2단계 · 품목 선택'", smokeSource, StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidInvoiceDraft_UsesSyncedCustomerCacheWhenCustomerApiFails()
    {
        var root = FindRepositoryRoot();
        var stateSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Models",
            "MobileSyncState.cs"));
        var coordinatorSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Services",
            "SyncCoordinator.cs"));
        var storeSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Services",
            "JsonSyncStateStore.cs"));
        var viewModelSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "ViewModels",
            "InvoiceDraftViewModel.cs"));

        Assert.Contains("public List<CustomerDto> SyncedCustomers { get; set; } = new();", stateSource, StringComparison.Ordinal);
        Assert.Contains("SyncedCustomers ??= new List<CustomerDto>();", stateSource, StringComparison.Ordinal);
        Assert.Contains("state.SyncedCustomers = MergeById(state.SyncedCustomers, response.Customers);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("target.SyncedCustomers = source.SyncedCustomers;", storeSource, StringComparison.Ordinal);

        Assert.Contains("TrySearchCustomersFromSyncedStateAsync", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("TryPreselectCustomerFromSyncedStateAsync", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("GetActiveSyncedCustomers", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("state.SyncedCustomers.Where(customer => !customer.IsDeleted)", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("MatchesCustomer(customer, keyword)", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("MatchesCustomer(candidate, customerName)", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("CustomerSearchResults.Add(customer)", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("동기화 캐시 거래처 검색결과", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("동기화 캐시 기준으로", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("Contains(customer.PriceGrade, query)", viewModelSource, StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidInvoiceDraft_SeparatesRecentItemsAndCustomersBySelectedOfficeAndWarehouse()
    {
        var root = FindRepositoryRoot();
        var viewModelSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "ViewModels",
            "InvoiceDraftViewModel.cs"));

        Assert.Contains("HandleInvoiceScopeChanged(previousOfficeCode, previousWarehouseCode);", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("HandleInvoiceScopeChanged(SelectedInvoiceOfficeCode, previousWarehouseCode);", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("ClearSelectedCustomerIfOutOfSelectedInvoiceOfficeScope();", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("FilterCustomersForSelectedInvoiceOfficeScope(await _api.GetCustomersAsync(keyword)).ToList();", viewModelSource, StringComparison.Ordinal);
        Assert.Contains(".Where(IsCustomerInSelectedInvoiceOfficeScope)", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("SelectInvoiceOfficeByCustomerScope(customer);", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("private string BuildRecentSelectionScopeCode()", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("return $\"{officeCode}:{warehouseCode}\";", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("QueueRecentSelectionsReloadForCurrentScope();", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("ReloadRecentSelectionsForCurrentScopeAsync(reloadVersion, recentScopeCode)", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("LoadAsync(_sessionTenantCode, recentScopeCode, _sessionUsername)", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("SaveAsync(_sessionTenantCode, BuildRecentSelectionScopeCode(), _sessionUsername, _recentSelections)", viewModelSource, StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidInvoiceDraft_OfficeSelectionUsesScopeTypeNotAdminRoleOnly()
    {
        var root = FindRepositoryRoot();
        var viewModelSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "ViewModels",
            "InvoiceDraftViewModel.cs"));
        var normalizedSource = viewModelSource.Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("_sessionTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(snapshot.TenantCode, _sessionOfficeCode);", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("private static IReadOnlyList<string> ResolveWritableInvoiceOfficeCodes(", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("var scopeType = TenantScopeCatalog.NormalizeScopeTypeOrDefault(", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("snapshot.IsAdmin ? TenantScopeCatalog.ScopeAdmin : TenantScopeCatalog.ScopeOfficeOnly", viewModelSource, StringComparison.Ordinal);
        Assert.Contains(
            "snapshot.IsAdmin && string.Equals(scopeType, TenantScopeCatalog.ScopeAdmin",
            viewModelSource,
            StringComparison.Ordinal);
        Assert.Contains("return OfficeCodeCatalog.All;", viewModelSource, StringComparison.Ordinal);
        Assert.Contains(
            "string.Equals(scopeType, TenantScopeCatalog.ScopeTenantAll",
            viewModelSource,
            StringComparison.Ordinal);
        Assert.Contains("return TenantScopeCatalog.GetOfficeCodesForTenant(sessionTenantCode);", viewModelSource, StringComparison.Ordinal);
        Assert.Contains(".Select(code => OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(code, _sessionOfficeCode))", normalizedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var code in OfficeCodeCatalog.AllScopes)", viewModelSource, StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidInvoiceDraft_FiltersItemsAndStockEvidenceBySelectedOfficeAndWarehouse()
    {
        var root = FindRepositoryRoot();
        var viewModelSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "ViewModels",
            "InvoiceDraftViewModel.cs"));

        Assert.Contains("FilterItemsForSelectedInvoiceOfficeScope(await _api.GetItemsAsync(keyword, SelectedCategory?.Name)).ToList();", viewModelSource, StringComparison.Ordinal);
        Assert.Contains(".Where(IsItemInSelectedInvoiceOfficeScope)", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("if (SelectedItem is not null && !IsItemInSelectedInvoiceOfficeScope(SelectedItem))", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("private bool IsItemInSelectedInvoiceOfficeScope(ItemDto item)", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("OfficeCodeCatalog.IsSharedOfficeCode(item.OfficeCode)", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("PopulateSelectedSourceWarehouseStocks(detail?.BranchStocks, SelectedItem);", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("PopulateSelectedSourceWarehouseStocks(branchStocks, selected);", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("private void PopulateSelectedSourceWarehouseStocks(IEnumerable<ItemWarehouseStockDto>? branchStocks, ItemDto item)", viewModelSource, StringComparison.Ordinal);
        Assert.Contains(".Where(IsStockInSelectedSourceWarehouse)", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("private bool IsStockInSelectedSourceWarehouse(ItemWarehouseStockDto stock)", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("OfficeCodeCatalog.NormalizeWarehouseCodeOrDefault(", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("OfficeCodeCatalog.NormalizeWarehouseCodeLoose(", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("WarehouseCode = SelectedSourceWarehouseCode", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("AddFallbackWholeStockRow(item, stockList.Count == 0 ? item.CurrentStock : 0m);", viewModelSource, StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidPaymentDraft_UsesSyncedInvoiceAndPaymentCacheWhenInvoiceApiFails()
    {
        var root = FindRepositoryRoot();
        var stateSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Models",
            "MobileSyncState.cs"));
        var coordinatorSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Services",
            "SyncCoordinator.cs"));
        var viewModelSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "ViewModels",
            "PaymentDraftViewModel.cs"));
        var smokeSource = File.ReadAllText(Path.Combine(
            root,
            "tools",
            "mobile",
            "Invoke-GeoraePlanAndroidSmoke.ps1"));

        Assert.Contains("public List<InvoiceDto> SyncedInvoices { get; set; } = new();", stateSource, StringComparison.Ordinal);
        Assert.Contains("public List<PaymentDto> SyncedPayments { get; set; } = new();", stateSource, StringComparison.Ordinal);
        Assert.Contains("state.SyncedInvoices = MergeById(state.SyncedInvoices, response.Invoices);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("state.SyncedPayments = MergeById(state.SyncedPayments, response.Payments);", coordinatorSource, StringComparison.Ordinal);

        Assert.Contains("TryLoadInvoicesFromSyncedStateAsync", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("BuildSyncedInvoiceSnapshots", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("state.SyncedPayments", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("state.SyncedInvoices", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("BuildEffectivePaymentsForInvoice(invoice.Id, state.SyncedPayments, state.PendingPush.Payments)", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("CloneInvoiceForPaymentDraft(invoice, payments)", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("Payments = payments.ToList()", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("동기화 캐시 전표", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("CalculateOutstandingAmount(value)", viewModelSource, StringComparison.Ordinal);

        Assert.Contains("-StepName 'payment-draft'", smokeSource, StringComparison.Ordinal);
        Assert.Contains("'수금/지급 입력'", smokeSource, StringComparison.Ordinal);
        Assert.Contains("'전표'", smokeSource, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("청구중", true, 100, 0, 100, "완료")]
    [InlineData("보류", true, 100, 0, 100, "완료")]
    [InlineData("보류", true, 0, 100, 100, "보류")]
    [InlineData("취소", true, 0, 100, 100, "취소")]
    [InlineData("청구중", true, 40, 60, 100, "부분입금")]
    [InlineData("완료", true, 40, 60, 100, "부분입금")]
    [InlineData("완료", true, 0, 100, 100, "청구중")]
    [InlineData("  청구중  ", false, 0, 0, 0, "청구중")]
    [InlineData("", false, 0, 0, 0, "청구중")]
    public void RentalBillingEvidenceStatusResolver_PrioritizesActualFinancialEvidence(
        string runStatus,
        bool hasFinancialEvidence,
        int settledAmount,
        int outstandingAmount,
        int billedAmount,
        string expectedStatus)
    {
        var actualStatus = RentalBillingEvidenceStatusResolver.Resolve(
            runStatus,
            hasFinancialEvidence,
            settledAmount,
            outstandingAmount,
            billedAmount);

        Assert.Equal(expectedStatus, actualStatus);
    }

    [Fact]
    public void MobileUpdateDownload_RedownloadsWhenCachedApkShaDoesNotMatch()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Services",
            "MobileAppUpdateService.cs"));

        Assert.Contains("if (string.IsNullOrWhiteSpace(package.Sha256))", source, StringComparison.Ordinal);
        Assert.Contains("DownloadPackageAsync(packageUri.ToString(), downloadRoot, fileName, package.Sha256, ct)", source, StringComparison.Ordinal);
        Assert.Contains("DownloadPackageAsync(string packageUrl, string downloadRoot, string fileName, string expectedSha256, CancellationToken ct)", source, StringComparison.Ordinal);
        Assert.Contains("HasMatchingFileAsync(targetPath, expectedSha256, ct)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("HasMatchingFileAsync(targetPath, string.Empty, ct)", source, StringComparison.Ordinal);
        Assert.Contains(".download", source, StringComparison.Ordinal);
        Assert.Contains("HasMatchingFileAsync(temporaryPath, expectedSha256, ct)", source, StringComparison.Ordinal);
        Assert.Contains("File.Move(temporaryPath, targetPath, overwrite: true)", source, StringComparison.Ordinal);
        Assert.Contains("TryDeleteFile(temporaryPath)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MobileUpdateCheck_HonorsMandatoryAndMinimumSupportedVersion()
    {
        var root = FindRepositoryRoot();
        var serviceSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Services",
            "MobileAppUpdateService.cs"));
        var settingsSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "ViewModels",
            "SettingsViewModel.cs"));
        var appSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "App.cs"));

        Assert.Contains("var minimumSupportedVersion = ResolveMinimumSupportedVersion(package, latestVersion);", serviceSource, StringComparison.Ordinal);
        Assert.Contains("IsBelowMinimumSupportedVersion = isBelowMinimumSupportedVersion", serviceSource, StringComparison.Ordinal);
        Assert.Contains("public bool IsBelowMinimumSupportedVersion { get; set; }", serviceSource, StringComparison.Ordinal);
        Assert.Contains("public bool RequiresImmediateUpdate => IsBelowMinimumSupportedVersion || (IsUpdateAvailable && Package?.Mandatory == true);", serviceSource, StringComparison.Ordinal);
        Assert.Contains("private static string ResolveMinimumSupportedVersion(AppUpdatePackageDto package, string latestVersion)", serviceSource, StringComparison.Ordinal);
        Assert.Contains("package.MinimumSupportedVersion", serviceSource, StringComparison.Ordinal);
        Assert.Contains("package.Mandatory ? latestVersion : string.Empty", serviceSource, StringComparison.Ordinal);
        Assert.Contains("IsUpdateAvailable = result.IsUpdateAvailable || result.RequiresImmediateUpdate;", settingsSource, StringComparison.Ordinal);
        Assert.Contains("result.MinimumSupportedVersion", settingsSource, StringComparison.Ordinal);
        Assert.Contains("if (!result.RequiresImmediateUpdate)", appSource, StringComparison.Ordinal);
        Assert.Contains("result.RequiresImmediateUpdate", appSource, StringComparison.Ordinal);
    }

    [Fact]
    public void MobileAuthentication401_ForcesSessionRecoveryInsteadOfReusingRejectedToken()
    {
        var root = FindRepositoryRoot();
        var recoverySource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Services",
            "MobileSessionRecoveryService.cs"));
        var apiSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Services",
            "GeoraePlanApiClient.cs"));

        Assert.Contains("TryRestoreSessionAsync(reason, forceRefresh: false, ct)", recoverySource, StringComparison.Ordinal);
        Assert.Contains("bool forceRefresh", recoverySource, StringComparison.Ordinal);
        Assert.Contains("if (!forceRefresh && await _sessionStore.HasUsableSessionAsync()", recoverySource, StringComparison.Ordinal);
        Assert.DoesNotContain("if (await _sessionStore.HasUsableSessionAsync()", recoverySource, StringComparison.Ordinal);
        Assert.Contains("TryRestoreSessionAsync($\"401:{relative}\", forceRefresh: true, ct: ct)", apiSource, StringComparison.Ordinal);
        Assert.Contains("TryRestoreSessionAsync($\"token:{relative}\", ct)", apiSource, StringComparison.Ordinal);
        Assert.Contains("권한/담당지점/사업 범위가 변경되어 자동 로그인으로도 복구하지 못했습니다", apiSource, StringComparison.Ordinal);
        Assert.Contains("권한/담당지점/사업 범위가 변경되었을 수 있습니다", apiSource, StringComparison.Ordinal);
    }

    [Fact]
    public void MobileFileDownloads_MapServerErrorsAndValidateCachedContent()
    {
        var root = FindRepositoryRoot();
        var apiSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Services",
            "GeoraePlanApiClient.cs"));
        var cacheSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Services",
            "CustomerContractCacheStore.cs"));
        var contractsViewModelSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "ViewModels",
            "CustomerContractsViewModel.cs"));
        var errorFormatterSource = File.ReadAllText(Path.Combine(
            root,
            "Shared",
            "거래플랜.Shared.Contracts",
            "ApiErrorMessageFormatter.cs"));

        Assert.Contains("ApiErrorMessageFormatter.BuildFailureMessage(", apiSource, StringComparison.Ordinal);
        Assert.Contains("\"contract_content_unavailable\"", errorFormatterSource, StringComparison.Ordinal);
        Assert.Contains("\"attachment_content_unavailable\"", errorFormatterSource, StringComparison.Ordinal);
        Assert.Contains("DownloadFileToCacheAsync(", apiSource, StringComparison.Ordinal);
        Assert.Contains("ValidateDownloadedFileAsync(temporaryPath, expectedSize, expectedSha256, label, ct)", apiSource, StringComparison.Ordinal);
        Assert.Contains("IsCachedDownloadValidAsync(cachedPath, contract.FileSize, contract.FileHash, ct)", apiSource, StringComparison.Ordinal);
        Assert.Contains("IsCachedDownloadValidAsync(cachedPath, attachment.FileSize, attachment.FileHash, ct)", apiSource, StringComparison.Ordinal);
        Assert.Contains("SHA256.HashDataAsync(stream, ct)", apiSource, StringComparison.Ordinal);
        Assert.Contains("cachedPath + \".download\"", apiSource, StringComparison.Ordinal);
        Assert.Contains("File.Move(temporaryPath, cachedPath, overwrite: true)", apiSource, StringComparison.Ordinal);
        Assert.Contains("TryDeleteFile(temporaryPath)", apiSource, StringComparison.Ordinal);

        Assert.Contains("IsCachedPdfValidAsync(pdfPath, contract, ct)", cacheSource, StringComparison.Ordinal);
        Assert.Contains("contract.FileSize > 0 && length != contract.FileSize", cacheSource, StringComparison.Ordinal);
        Assert.Contains("contract.FileHash.Trim()", cacheSource, StringComparison.Ordinal);
        Assert.Contains("TryDeleteFile(pdfPath)", cacheSource, StringComparison.Ordinal);
        Assert.Contains("CachePdfAsync(Guid customerId, CustomerContractDto contract, string sourcePath", cacheSource, StringComparison.Ordinal);
        Assert.Contains("CachePdfAsync(_customerId, contract, downloadedPath)", contractsViewModelSource, StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidSmoke_CoversSyncTabStatus()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "mobile",
            "Invoke-GeoraePlanAndroidSmoke.ps1"));

        Assert.Contains("-TabText '동기화'", source, StringComparison.Ordinal);
        Assert.Contains("-StepName 'sync-status'", source, StringComparison.Ordinal);
        Assert.Contains("'동기화 상태'", source, StringComparison.Ordinal);
        Assert.Contains("'마지막 서버 변경번호'", source, StringComparison.Ordinal);
        Assert.Contains("'저장 대기'", source, StringComparison.Ordinal);
        Assert.Contains("'권장 동기화 실행'", source, StringComparison.Ordinal);
        Assert.Contains("'서버에서 받기'", source, StringComparison.Ordinal);
        Assert.Contains("'서버에 올리기'", source, StringComparison.Ordinal);
        Assert.Contains("$tabPoint = Get-NodeCenterByText -Content $afterTapDump.Content -Text $TabText", source, StringComparison.Ordinal);
        Assert.Contains("design_bottom_sheet", source, StringComparison.Ordinal);
        Assert.Contains("[switch]$ExerciseSyncNow", source, StringComparison.Ordinal);
        Assert.Contains("Assert-LocalSyncExerciseTarget -BaseUrl $SyncExerciseBaseUrl", source, StringComparison.Ordinal);
        Assert.Contains("Invoke-SyncNowAndAssert", source, StringComparison.Ordinal);
        Assert.Contains("sync-now-before-tap", source, StringComparison.Ordinal);
        Assert.Contains("'권장 동기화 완료'", source, StringComparison.Ordinal);
        Assert.Contains("수동 동기화 실기동 검증은 로컬 테스트 API에서만 허용됩니다.", source, StringComparison.Ordinal);
        Assert.Contains("mobile-smoke-$timestamp-login-stable", source, StringComparison.Ordinal);
        Assert.Contains("Dismiss-AndroidAnrDialog -AdbPath $resolvedAdb -DeviceId $deviceId -Content $dump.Content", source, StringComparison.Ordinal);
        Assert.Contains("$timestamp-login-stable-after-anr", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidSmoke_CoversMasterDataNonRetryableSaveRejection()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "mobile",
            "Invoke-GeoraePlanAndroidSmoke.ps1"));

        Assert.Contains("[ValidateSet('', '400', '401', '403', '404', '422')]", source, StringComparison.Ordinal);
        Assert.Contains("[string]$ExerciseMasterDataNonRetryableSaveFaultStatus = ''", source, StringComparison.Ordinal);
        Assert.Contains("function Set-MobileDiagnosticFault", source, StringComparison.Ordinal);
        Assert.Contains("function Invoke-MasterDataNonRetryableSaveRejection", source, StringComparison.Ordinal);
        Assert.Contains("dirty 0건 보장을 위해 fresh install/app-data-clear", source, StringComparison.Ordinal);
        Assert.Contains("$ExerciseSyncNow -or -not [string]::IsNullOrWhiteSpace($ExerciseMasterDataNonRetryableSaveFaultStatus)", source, StringComparison.Ordinal);
        Assert.Contains("-Target 'customers'", source, StringComparison.Ordinal);
        Assert.Contains("-Target 'items'", source, StringComparison.Ordinal);
        Assert.Contains("'새 거래처 등록'", source, StringComparison.Ordinal);
        Assert.Contains("'새 품목 등록'", source, StringComparison.Ordinal);
        Assert.Contains("'mobile-nonretryable-customer-fault-before-save'", source, StringComparison.Ordinal);
        Assert.Contains("'mobile-nonretryable-customer-save-rejected'", source, StringComparison.Ordinal);
        Assert.Contains("'mobile-nonretryable-item-fault-before-save'", source, StringComparison.Ordinal);
        Assert.Contains("'mobile-nonretryable-item-save-rejected'", source, StringComparison.Ordinal);
        Assert.Contains("'mobile-nonretryable-master-data-rejection-not-dirty'", source, StringComparison.Ordinal);
        Assert.Contains("'거래처 저장 실패'", source, StringComparison.Ordinal);
        Assert.Contains("'품목 저장 실패'", source, StringComparison.Ordinal);
        Assert.Contains("'거래처 0건'", source, StringComparison.Ordinal);
        Assert.Contains("'품목 0건'", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidWriteE2E_CoversOfflineDirtySyncPush()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "mobile",
            "Invoke-GeoraePlanAndroidWriteE2E.ps1"));

        Assert.Contains("[switch]$ExerciseOfflineDirtySync", source, StringComparison.Ordinal);
        Assert.Contains("Assert-LocalDirtySyncTarget -BaseUrl $BaseUrl", source, StringComparison.Ordinal);
        Assert.Contains("Set-MobileDiagnosticNetworkFault", source, StringComparison.Ordinal);
        Assert.Contains("NETWORK|invoices", source, StringComparison.Ordinal);
        Assert.Contains("'오프라인/재시도 대기'", source, StringComparison.Ordinal);
        Assert.Contains("mobile-offline-invoice-pending", source, StringComparison.Ordinal);
        Assert.Contains("server-invoice-absent-before-sync", source, StringComparison.Ordinal);
        Assert.Contains("'전표 1건'", source, StringComparison.Ordinal);
        Assert.Contains("Invoke-SyncNowAndAssert", source, StringComparison.Ordinal);
        Assert.Contains("mobile-$voucherSlug-invoice-dirty-push", source, StringComparison.Ordinal);
        Assert.Contains("오프라인 dirty 동기화 검증은 로컬 테스트 API에서만 허용됩니다.", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidWriteE2E_CoversStoppedServerDirtySyncTimeout()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "mobile",
            "Invoke-GeoraePlanAndroidWriteE2E.ps1"));

        Assert.Contains("[switch]$ExerciseStoppedServerDirtySync", source, StringComparison.Ordinal);
        Assert.Contains("[int]$StoppedServerOfflineTimeoutSeconds = 45", source, StringComparison.Ordinal);
        Assert.Contains("Stop-LocalApiForBaseUrl", source, StringComparison.Ordinal);
        Assert.Contains("Start-LocalApiForBaseUrl", source, StringComparison.Ordinal);
        Assert.Contains("Get-NetTCPConnection -LocalPort $uri.Port -State Listen", source, StringComparison.Ordinal);
        Assert.Contains("local-api-stop-before-save", source, StringComparison.Ordinal);
        Assert.Contains("mobile-stopped-server-offline-pending", source, StringComparison.Ordinal);
        Assert.Contains("local-api-restart-before-sync", source, StringComparison.Ordinal);
        Assert.Contains("mobile-$voucherSlug-invoice-auto-push-after-restart", source, StringComparison.Ordinal);
        Assert.Contains("저장 대기: 설정 0건", source, StringComparison.Ordinal);
        Assert.Contains("거래처기준 0건", source, StringComparison.Ordinal);
        Assert.Contains("전표 0건", source, StringComparison.Ordinal);
        Assert.Contains("ExerciseStoppedServerDirtySync = [bool]$ExerciseStoppedServerDirtySync", source, StringComparison.Ordinal);
        Assert.Contains("ExerciseStoppedServerDirtySync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidWriteE2E_CoversNonRetryableSaveRejection()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "mobile",
            "Invoke-GeoraePlanAndroidWriteE2E.ps1"));

        Assert.Contains("[string]$ExerciseNonRetryableSaveFaultStatus = ''", source, StringComparison.Ordinal);
        Assert.Contains("[ValidateSet('', '400', '401', '403', '404', '422')]", source, StringComparison.Ordinal);
        Assert.Contains("[ValidateSet('NETWORK', '400', '401', '403', '404', '422', '500')]", source, StringComparison.Ordinal);
        Assert.Contains("function Set-MobileDiagnosticFault", source, StringComparison.Ordinal);
        Assert.Contains("Set-MobileDiagnosticFault -AdbPath $resolvedAdb -DeviceId $deviceId -PackageName $PackageName -Mode $ExerciseNonRetryableSaveFaultStatus -Target 'invoices'", source, StringComparison.Ordinal);
        Assert.Contains("mobile-nonretryable-fault-before-save", source, StringComparison.Ordinal);
        Assert.Contains("\uC800\uC7A5\uB418\uC9C0 \uC54A\uC558\uC2B5\uB2C8\uB2E4", source, StringComparison.Ordinal);
        Assert.Contains("server-invoice-absent-after-nonretryable-rejection", source, StringComparison.Ordinal);
        Assert.Contains("sync-status-after-nonretryable-invoice-rejection", source, StringComparison.Ordinal);
        Assert.Contains("mobile-nonretryable-invoice-rejection-not-dirty", source, StringComparison.Ordinal);
        Assert.Contains("\uC804\uD45C 0\uAC74", source, StringComparison.Ordinal);
        Assert.Contains("Diagnostic fault mode, non-retryable rejection mode, and stopped API mode cannot run together.", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidPaymentE2E_CoversStoppedServerDirtySyncPush()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "mobile",
            "Invoke-GeoraePlanAndroidPaymentE2E.ps1"));

        Assert.Contains("[switch]$ExerciseStoppedServerDirtySync", source, StringComparison.Ordinal);
        Assert.Contains("[int]$StoppedServerOfflineTimeoutSeconds = 45", source, StringComparison.Ordinal);
        Assert.Contains("Assert-LocalDirtySyncTarget -BaseUrl $BaseUrl", source, StringComparison.Ordinal);
        Assert.Contains("for ($attempt = 1; $attempt -le 3; $attempt++)", source, StringComparison.Ordinal);
        Assert.Contains("daemon still not running|cannot connect to daemon", source, StringComparison.Ordinal);
        Assert.Contains("& $AdbPath start-server", source, StringComparison.Ordinal);
        Assert.Contains("adb failed after retry", source, StringComparison.Ordinal);
        Assert.Contains("Stop-LocalApiForBaseUrl", source, StringComparison.Ordinal);
        Assert.Contains("Start-LocalApiForBaseUrl", source, StringComparison.Ordinal);
        Assert.Contains("local-api-stop-before-payment-save", source, StringComparison.Ordinal);
        Assert.Contains("mobile-stopped-server-payment-pending", source, StringComparison.Ordinal);
        Assert.Contains("local-api-restart-before-payment-sync", source, StringComparison.Ordinal);
        Assert.Contains("server-payment-absent-before-sync", source, StringComparison.Ordinal);
        Assert.Contains("sync-status-before-payment-dirty-push", source, StringComparison.Ordinal);
        Assert.Contains("수금·지급 0건", source, StringComparison.Ordinal);
        Assert.Contains("수금·지급 1건", source, StringComparison.Ordinal);
        Assert.Contains("mobile-$voucherSlug-payment-dirty-push", source, StringComparison.Ordinal);
        Assert.Contains("mobile-$voucherSlug-payment-auto-push-after-restart", source, StringComparison.Ordinal);
        Assert.Contains("ExerciseStoppedServerDirtySync = [bool]$ExerciseStoppedServerDirtySync", source, StringComparison.Ordinal);
        Assert.Contains("[switch]$ExerciseAttachmentUpload", source, StringComparison.Ordinal);
        Assert.Contains("[switch]$ExerciseAttachmentOpenUi", source, StringComparison.Ordinal);
        Assert.Contains("Attachment open UI E2E requires either PDF attachment E2E or camera attachment E2E.", source, StringComparison.Ordinal);
        Assert.Contains("[switch]$ExerciseAttachmentListFallback", source, StringComparison.Ordinal);
        Assert.Contains("Attachment list fallback E2E requires attachment open UI E2E.", source, StringComparison.Ordinal);
        Assert.Contains("Set-MobileDiagnosticNetworkFault", source, StringComparison.Ordinal);
        Assert.Contains("payments/$PaymentId/attachments", source, StringComparison.Ordinal);
        Assert.Contains("mobile-$VoucherSlug-payment-attachment-list-fallback-fault", source, StringComparison.Ordinal);
        Assert.Contains("New-TestAttachmentPdf", source, StringComparison.Ordinal);
        Assert.Contains("Push-TestAttachmentToDevice", source, StringComparison.Ordinal);
        Assert.Contains("Select-PdfAttachmentFromDevice", source, StringComparison.Ordinal);
        Assert.Contains("Wait-TestPaymentAttachmentCreated", source, StringComparison.Ordinal);
        Assert.Contains("Get-TestPaymentAttachmentContent", source, StringComparison.Ordinal);
        Assert.Contains("payments/attachments/$($Attachment.id)/content", source, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash -LiteralPath $downloadPath -Algorithm SHA256", source, StringComparison.Ordinal);
        Assert.Contains("server-$voucherSlug-payment-attachment-content-download", source, StringComparison.Ordinal);
        Assert.Contains("AttachmentContentSha256", source, StringComparison.Ordinal);
        Assert.Contains("Invoke-TestPaymentAttachmentOpenUi", source, StringComparison.Ordinal);
        Assert.Contains("attachment-open-customers", source, StringComparison.Ordinal);
        Assert.Contains("'첨부 보기'", source, StringComparison.Ordinal);
        Assert.Contains("'수금/지급 첨부'", source, StringComparison.Ordinal);
        Assert.Contains("mobile-$VoucherSlug-payment-attachment-list-opened", source, StringComparison.Ordinal);
        Assert.Contains("mobile-$VoucherSlug-payment-attachment-open-button", source, StringComparison.Ordinal);
        Assert.Contains("ExerciseAttachmentOpenUi = [bool]$ExerciseAttachmentOpenUi", source, StringComparison.Ordinal);
        Assert.Contains("ExerciseAttachmentListFallback = [bool]$ExerciseAttachmentListFallback", source, StringComparison.Ordinal);
        Assert.Contains("server-$voucherSlug-payment-attachment-upload", source, StringComparison.Ordinal);
        Assert.Contains("CreatedAttachmentId", source, StringComparison.Ordinal);
        Assert.Contains("[switch]$ExerciseCameraAttachmentUpload", source, StringComparison.Ordinal);
        Assert.Contains("Run either PDF attachment E2E or camera attachment E2E", source, StringComparison.Ordinal);
        Assert.Contains("Grant-CameraPermissionIfPossible", source, StringComparison.Ordinal);
        Assert.Contains("Dismiss-CameraPermissionDialogIfPresent", source, StringComparison.Ordinal);
        Assert.Contains("Select-CameraAttachmentFromDevice", source, StringComparison.Ordinal);
        Assert.Contains("content-desc' -Value 'Shutter'", source, StringComparison.Ordinal);
        Assert.Contains("content-desc' -Value 'Done'", source, StringComparison.Ordinal);
        Assert.Contains("Wait-TestPaymentImageAttachmentCreated", source, StringComparison.Ordinal);
        Assert.Contains("server-$voucherSlug-payment-camera-attachment-upload", source, StringComparison.Ordinal);
        Assert.Contains("AttachmentMimeType", source, StringComparison.Ordinal);
        Assert.Contains("image/", source, StringComparison.Ordinal);
        Assert.Contains("카메라", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidPaymentE2E_CoversNonRetryableSaveRejection()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "mobile",
            "Invoke-GeoraePlanAndroidPaymentE2E.ps1"));

        Assert.Contains("[string]$ExerciseNonRetryableSaveFaultStatus = ''", source, StringComparison.Ordinal);
        Assert.Contains("[ValidateSet('', '400', '401', '403', '404', '422')]", source, StringComparison.Ordinal);
        Assert.Contains("[ValidateSet('NETWORK', '400', '401', '403', '404', '422', '500')]", source, StringComparison.Ordinal);
        Assert.Contains("function Set-MobileDiagnosticFault", source, StringComparison.Ordinal);
        Assert.Contains("Set-MobileDiagnosticFault -AdbPath $resolvedAdb -DeviceId $deviceId -PackageName $PackageName -Mode $ExerciseNonRetryableSaveFaultStatus -Target 'sync/push'", source, StringComparison.Ordinal);
        Assert.Contains("mobile-nonretryable-payment-fault-before-save", source, StringComparison.Ordinal);
        Assert.Contains("\uC800\uC7A5\uB418\uC9C0 \uC54A\uC558\uC2B5\uB2C8\uB2E4", source, StringComparison.Ordinal);
        Assert.Contains("server-payment-absent-after-nonretryable-rejection", source, StringComparison.Ordinal);
        Assert.Contains("sync-status-after-nonretryable-payment-rejection", source, StringComparison.Ordinal);
        Assert.Contains("mobile-nonretryable-payment-rejection-not-dirty", source, StringComparison.Ordinal);
        Assert.Contains("\uC218\uAE08\u00B7\uC9C0\uAE09 0\uAC74", source, StringComparison.Ordinal);
        Assert.Contains("Non-retryable save fault cannot run with stopped API or attachment exercises.", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidPaymentE2E_CoversStaleInvoiceRevisionConflict()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "tools",
            "mobile",
            "Invoke-GeoraePlanAndroidPaymentE2E.ps1"));

        Assert.Contains("[switch]$ExerciseStaleInvoiceRevisionConflict", source, StringComparison.Ordinal);
        Assert.Contains("Stale invoice revision conflict E2E must run alone", source, StringComparison.Ordinal);
        Assert.Contains("Assert-LocalDirtySyncTarget -BaseUrl $BaseUrl", source, StringComparison.Ordinal);
        Assert.Contains("function Start-StaleInvoiceRevisionRaceJob", source, StringComparison.Ordinal);
        Assert.Contains("Start-Job -ScriptBlock", source, StringComparison.Ordinal);
        Assert.Contains("Invoke-RestMethod `\n                    -Method Put `\n                    -Uri $invoiceUri", source.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("server-stale-invoice-revision-race-start", source, StringComparison.Ordinal);
        Assert.Contains("server-stale-invoice-revision-race-stop", source, StringComparison.Ordinal);
        Assert.Contains("mobile-payment-e2e-$voucherSlug-$timestamp-after-payment-save-stale-invoice-revision", source, StringComparison.Ordinal);
        Assert.Contains("server-payment-absent-after-stale-invoice-revision-conflict", source, StringComparison.Ordinal);
        Assert.Contains("sync-status-after-stale-invoice-revision-conflict", source, StringComparison.Ordinal);
        Assert.Contains("mobile-stale-invoice-revision-conflict-not-dirty", source, StringComparison.Ordinal);
        Assert.Contains("\uC800\uC7A5\uB418\uC9C0 \uC54A\uC558\uC2B5\uB2C8\uB2E4", source, StringComparison.Ordinal);
        Assert.Contains("\uCD5C\uC2E0\uAC12", source, StringComparison.Ordinal);
        Assert.Contains("\uC218\uAE08\u00B7\uC9C0\uAE09 0\uAC74", source, StringComparison.Ordinal);
        Assert.Contains("ExerciseStaleInvoiceRevisionConflict = [bool]$ExerciseStaleInvoiceRevisionConflict", source, StringComparison.Ordinal);
        Assert.Contains("StaleInvoiceRevisionRaceLog", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidPaymentAttachments_OpenButtonValidatesLocalFileAndHandlesMissingViewer()
    {
        var root = FindRepositoryRoot();
        var viewModelSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "ViewModels",
            "PaymentAttachmentsViewModel.cs"));
        var pageSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Pages",
            "PaymentAttachmentsPage.cs"));

        Assert.Contains("private IReadOnlyList<PaymentAttachmentDto> _fallbackAttachments = [];", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("IEnumerable<PaymentAttachmentDto>? fallbackAttachments = null", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("_fallbackAttachments = NormalizeFallbackAttachments(fallbackAttachments);", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("상세 화면 기준 첨부", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("ReplaceAttachments(_fallbackAttachments)", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("NormalizeFallbackAttachments", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("await _viewModel.InitializeAsync(_paymentId, _titleText, _fallbackAttachments);", pageSource, StringComparison.Ordinal);
        Assert.Contains("if (!File.Exists(path))", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("var opened = await Launcher.Default.OpenAsync", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("? \"첨부 파일을 열었습니다.\"", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("열 수 있는 앱을 찾지 못했습니다", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("IsNoViewerAvailable(ex)", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("No Activity found", viewModelSource, StringComparison.Ordinal);
        Assert.DoesNotContain("await Launcher.Default.OpenAsync(new OpenFileRequest(\r\n                attachment.FileName,\r\n                new ReadOnlyFile(path)));\r\n            StatusMessage = \"첨부 파일을 열었습니다.\";", viewModelSource, StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidPaymentDraft_QueuesDirtyPaymentWhenLatestInvoiceRefreshIsRetryable()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "ViewModels",
            "PaymentDraftViewModel.cs"));
        var retryPolicySource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Services",
            "MobileRetryableNetworkFailure.cs"));

        Assert.Contains("CanQueuePaymentWithSelectedInvoiceAfterRefreshFailure", source, StringComparison.Ordinal);
        Assert.Contains("latestInvoice = SelectedInvoice", source, StringComparison.Ordinal);
        Assert.Contains("최신 전표 확인 지연으로 현재 화면 전표 기준", source, StringComparison.Ordinal);
        Assert.Contains("MobileRetryableNetworkFailure.IsRetryable(ex)", source, StringComparison.Ordinal);
        Assert.Contains("TaskCanceledException or OperationCanceledException or TimeoutException", retryPolicySource, StringComparison.Ordinal);
        Assert.Contains("IsSocketClosedOrTransportFailure", retryPolicySource, StringComparison.Ordinal);
        Assert.Contains("Socket closed", retryPolicySource, StringComparison.Ordinal);
        Assert.Contains("Connection refused", retryPolicySource, StringComparison.Ordinal);
        Assert.Contains("Network is unreachable", retryPolicySource, StringComparison.Ordinal);
        Assert.Contains("IOException", retryPolicySource, StringComparison.Ordinal);
        Assert.Contains("HttpStatusCode.ServiceUnavailable", retryPolicySource, StringComparison.Ordinal);
        Assert.Contains("RefreshSelectedInvoiceForSaveAsync(SelectedInvoice)", source, StringComparison.Ordinal);
        Assert.Contains("SavePaymentImmediatelyAsync(payment, Attachments, linkedTransaction)", source, StringComparison.Ordinal);
        Assert.Contains("ExpectedRevision = latestInvoice.Revision", source, StringComparison.Ordinal);
        Assert.Contains("ExpectedRevision = invoice.Revision", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidPaymentDraft_OutstandingAmountIncludesPendingPayments()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "ViewModels",
            "PaymentDraftViewModel.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("MergePendingPaymentsIntoInvoice", source, StringComparison.Ordinal);
        Assert.Contains("BuildEffectivePaymentsForInvoice", source, StringComparison.Ordinal);
        Assert.Contains("state.PendingPush.Payments", source, StringComparison.Ordinal);
        Assert.Contains("var pendingPayments = SyncCoordinator.IsConcurrencyConflictState(state)\n            ? null\n            : state.PendingPush.Payments;", source, StringComparison.Ordinal);
        Assert.Contains("BuildEffectivePaymentsForInvoice(invoice.Id, invoice.Payments, pendingPayments)", source, StringComparison.Ordinal);
        Assert.Contains("ReplaceInvoiceSnapshot(MergePendingPaymentsIntoInvoice(latestInvoice, state));", source, StringComparison.Ordinal);
        Assert.Contains("latest = MergePendingPaymentsIntoInvoice(latest, pendingState);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ServerSync_RejectsStaleInvoiceRevisionForNewLinkedMobilePayments()
    {
        var root = FindRepositoryRoot();
        var paymentsControllerSource = File.ReadAllText(Path.Combine(
            root,
            "Server",
            "거래플랜.Server.Api",
            "Controllers",
            "PaymentsController.cs"));
        var syncControllerSource = File.ReadAllText(Path.Combine(
            root,
            "Server",
            "거래플랜.Server.Api",
            "Controllers",
            "SyncController.cs"));
        var serverTestSource = File.ReadAllText(Path.Combine(
            root,
            "Tests",
            "GeoraePlan.Server.Api.Tests",
            "SyncControllerTests.cs"));

        Assert.Contains("dto.ExpectedRevision > 0 && invoice.Revision != dto.ExpectedRevision", paymentsControllerSource, StringComparison.Ordinal);
        Assert.Contains("Referenced invoice revision mismatch", paymentsControllerSource, StringComparison.Ordinal);
        Assert.Contains("existing is null && !dto.IsDeleted && dto.ExpectedRevision > 0 && invoice.Revision != dto.ExpectedRevision", syncControllerSource, StringComparison.Ordinal);
        Assert.Contains("Transaction amount exceeds current outstanding balance", syncControllerSource, StringComparison.Ordinal);
        Assert.Contains("payment.Id != dto.Id", syncControllerSource, StringComparison.Ordinal);
        Assert.Contains("Push_RejectsNewLinkedPaymentAndTransaction_WhenInvoiceRevisionIsStale", serverTestSource, StringComparison.Ordinal);
        Assert.Contains("Assert.False(await _dbContext.Transactions.IgnoreQueryFilters().AnyAsync(transaction => transaction.Id == paymentId));", serverTestSource, StringComparison.Ordinal);
        Assert.Contains("Assert.False(await _dbContext.Payments.IgnoreQueryFilters().AnyAsync(payment => payment.Id == paymentId));", serverTestSource, StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidCustomerItemEdit_QueuesRetryableDirtyWritesAndShowsPendingCounts()
    {
        var root = FindRepositoryRoot();
        var customerPageSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Pages",
            "CustomerEditPage.cs"));
        var itemPageSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Pages",
            "ItemEditPage.cs"));
        var customersPageSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Pages",
            "CustomersPage.cs"));
        var itemsPageSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Pages",
            "ItemsPage.cs"));
        var customersViewSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "ViewModels",
            "CustomersViewModel.cs"));
        var itemsViewSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "ViewModels",
            "ItemsViewModel.cs"));
        var syncCoordinatorSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Services",
            "SyncCoordinator.cs"));
        var stateSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Models",
            "MobileSyncState.cs"));
        var syncViewSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "ViewModels",
            "SyncViewModel.cs"));
        var homeViewSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "ViewModels",
            "HomeViewModel.cs"));

        Assert.Contains("ServiceHelper.GetRequiredService<SyncCoordinator>()", customerPageSource, StringComparison.Ordinal);
        Assert.Contains("QueueCustomerDraftAsync(dto, reason)", customerPageSource, StringComparison.Ordinal);
        Assert.Contains("QueuePendingDeleteAsync(_source, ex)", customerPageSource, StringComparison.Ordinal);
        Assert.Contains("MobileRetryableNetworkFailure.IsRetryable(ex)", customerPageSource, StringComparison.Ordinal);
        Assert.Contains("MobileErrorHandler.FireAndForget(() => _afterSaved(dto), \"거래처 저장 후 목록 새로고침\")", customerPageSource, StringComparison.Ordinal);
        Assert.Contains("dto.Id = _source?.Id ?? Guid.NewGuid();", customerPageSource, StringComparison.Ordinal);
        Assert.Contains("BuildMutationId(\"customer\", dto.Id)", customerPageSource, StringComparison.Ordinal);

        Assert.Contains("ServiceHelper.GetRequiredService<SyncCoordinator>()", itemPageSource, StringComparison.Ordinal);
        Assert.Contains("QueueItemDraftAsync(dto, reason)", itemPageSource, StringComparison.Ordinal);
        Assert.Contains("QueuePendingDeleteAsync(_source, ex)", itemPageSource, StringComparison.Ordinal);
        Assert.Contains("MobileRetryableNetworkFailure.IsRetryable(ex)", itemPageSource, StringComparison.Ordinal);
        Assert.Contains("MobileErrorHandler.FireAndForget(() => _afterSaved(dto), \"품목 저장 후 목록 새로고침\")", itemPageSource, StringComparison.Ordinal);
        Assert.Contains("dto.Id = _source?.Id ?? Guid.NewGuid();", itemPageSource, StringComparison.Ordinal);
        Assert.Contains("BuildMutationId(\"item\", dto.Id)", itemPageSource, StringComparison.Ordinal);
        Assert.Contains("MobileErrorHandler.FireAndForget(() => _afterSaved(dto), \"거래처 삭제 후 목록 새로고침\")", customerPageSource, StringComparison.Ordinal);
        Assert.Contains("MobileErrorHandler.FireAndForget(() => _afterSaved(dto), \"품목 삭제 후 목록 새로고침\")", itemPageSource, StringComparison.Ordinal);
        Assert.Contains("saved?.IsDeleted == true", customersPageSource, StringComparison.Ordinal);
        Assert.Contains("RemoveDeletedCustomerFromCurrentViewAsync(deletedCustomerId)", customersPageSource, StringComparison.Ordinal);
        Assert.Contains("saved?.IsDeleted == true", itemsPageSource, StringComparison.Ordinal);
        Assert.Contains("RemoveDeletedItemFromCurrentView(deletedItemId)", itemsPageSource, StringComparison.Ordinal);
        Assert.Contains("public async Task RemoveDeletedCustomerFromCurrentViewAsync(Guid customerId)", customersViewSource, StringComparison.Ordinal);
        Assert.Contains("await _cacheStore.SaveCustomersAsync(cached)", customersViewSource, StringComparison.Ordinal);
        Assert.Contains("public void RemoveDeletedItemFromCurrentView(Guid itemId)", itemsViewSource, StringComparison.Ordinal);

        Assert.Contains("public async Task<MobileSyncState> QueueCustomerDraftAsync", syncCoordinatorSource, StringComparison.Ordinal);
        Assert.Contains("state.PendingPush.Customers.RemoveAll(x => x.Id == customer.Id)", syncCoordinatorSource, StringComparison.Ordinal);
        Assert.Contains("public async Task<MobileSyncState> QueueItemDraftAsync", syncCoordinatorSource, StringComparison.Ordinal);
        Assert.Contains("state.PendingPush.Items.RemoveAll(x => x.Id == item.Id)", syncCoordinatorSource, StringComparison.Ordinal);
        Assert.Contains("public int PendingCustomerCount => PendingPush.Customers?.Count ?? 0;", stateSource, StringComparison.Ordinal);
        Assert.Contains("public int PendingItemCount => PendingPush.Items?.Count ?? 0;", stateSource, StringComparison.Ordinal);
        Assert.Contains("거래처기준 {summary.PendingCustomerMasterCount}건 / 거래처 {summary.PendingCustomerCount}건 / 계약 {summary.PendingCustomerContractCount}건", syncViewSource, StringComparison.Ordinal);
        Assert.Contains("품목 {summary.PendingItemCount}건 / 재고 {summary.PendingItemWarehouseStockCount}건", syncViewSource, StringComparison.Ordinal);
        Assert.Contains("거래처기준 {pendingSummary.PendingCustomerMasterCount:N0}건 / 거래처 {pendingSummary.PendingCustomerCount:N0}건 / 계약 {pendingSummary.PendingCustomerContractCount:N0}건", homeViewSource, StringComparison.Ordinal);
        Assert.Contains("품목 {pendingSummary.PendingItemCount:N0}건 / 재고 {pendingSummary.PendingItemWarehouseStockCount:N0}건", homeViewSource, StringComparison.Ordinal);
    }

    [Fact]
    public void MobileCustomerItemEdit_NonRetryableAuthenticationFailureDoesNotQueueDirty()
    {
        var root = FindRepositoryRoot();
        var customerPageSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Pages",
            "CustomerEditPage.cs"));
        var itemPageSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Pages",
            "ItemEditPage.cs"));
        var retryPolicySource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Services",
            "MobileRetryableNetworkFailure.cs"));

        Assert.Contains("catch (Exception ex) when (dto is not null && MobileRetryableNetworkFailure.IsRetryable(ex))", customerPageSource, StringComparison.Ordinal);
        Assert.Contains("await QueuePendingSaveAsync(dto, ex);", customerPageSource, StringComparison.Ordinal);
        Assert.Contains("_statusLabel.Text = $\"거래처 저장 실패: {ex.Message}\";", customerPageSource, StringComparison.Ordinal);
        Assert.Contains("await DisplayAlert(\"거래처 저장 실패\", ex.Message, \"확인\");", customerPageSource, StringComparison.Ordinal);

        Assert.Contains("catch (Exception ex) when (dto is not null && MobileRetryableNetworkFailure.IsRetryable(ex))", itemPageSource, StringComparison.Ordinal);
        Assert.Contains("await QueuePendingSaveAsync(dto, ex);", itemPageSource, StringComparison.Ordinal);
        Assert.Contains("_statusLabel.Text = $\"품목 저장 실패: {ex.Message}\";", itemPageSource, StringComparison.Ordinal);
        Assert.Contains("await DisplayAlert(\"품목 저장 실패\", ex.Message, \"확인\");", itemPageSource, StringComparison.Ordinal);

        Assert.Contains("HttpStatusCode.RequestTimeout", retryPolicySource, StringComparison.Ordinal);
        Assert.Contains("HttpStatusCode.TooManyRequests", retryPolicySource, StringComparison.Ordinal);
        Assert.Contains("HttpStatusCode.InternalServerError", retryPolicySource, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpStatusCode.Unauthorized", retryPolicySource, StringComparison.Ordinal);
        Assert.DoesNotContain("MobileAuthenticationException", retryPolicySource, StringComparison.Ordinal);
    }

    [Fact]
    public void MobileApiRequests_UseShortTimeoutWithoutBreakingFileTransfers()
    {
        var root = FindRepositoryRoot();
        var apiSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Services",
            "GeoraePlanApiClient.cs"));
        var recoverySource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Services",
            "MobileSessionRecoveryService.cs"));

        Assert.Contains("DefaultApiRequestTimeout = TimeSpan.FromSeconds(15)", apiSource, StringComparison.Ordinal);
        Assert.Contains("FileTransferRequestTimeout = TimeSpan.FromMinutes(3)", apiSource, StringComparison.Ordinal);
        Assert.Contains("CancellationTokenSource.CreateLinkedTokenSource(ct)", apiSource, StringComparison.Ordinal);
        Assert.Contains("timeoutCts.CancelAfter(requestTimeout)", apiSource, StringComparison.Ordinal);
        Assert.Contains("requestTimeout ?? DefaultApiRequestTimeout", apiSource, StringComparison.Ordinal);
        Assert.Contains("requestTimeout: TimeSpan.FromSeconds(timeoutSeconds + 5)", apiSource, StringComparison.Ordinal);
        Assert.Contains("requestTimeout: FileTransferRequestTimeout", apiSource, StringComparison.Ordinal);
        Assert.Contains("SessionRecoveryRequestTimeout = TimeSpan.FromSeconds(15)", recoverySource, StringComparison.Ordinal);
        Assert.Contains("timeoutCts.CancelAfter(SessionRecoveryRequestTimeout)", recoverySource, StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidSettings_ExposesReadOnlyIntegrityReportForPrivilegedUsers()
    {
        var root = FindRepositoryRoot();
        var apiSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Services",
            "GeoraePlanApiClient.cs"));
        var sessionSnapshotSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Models",
            "SessionSnapshot.cs"));
        var sessionStoreSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Services",
            "SessionStore.cs"));
        var settingsViewModelSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "ViewModels",
            "SettingsViewModel.cs"));
        var settingsPageSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Pages",
            "SettingsPage.cs"));
        var integrityViewModelSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "ViewModels",
            "IntegrityReportViewModel.cs"));
        var integrityPageSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Pages",
            "IntegrityReportPage.cs"));
        var mauiSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "MauiProgram.cs"));

        Assert.Contains("GetIntegrityReportAsync", apiSource, StringComparison.Ordinal);
        Assert.Contains("GetAsync<IntegrityReportDto>(\"integrity/report\"", apiSource, StringComparison.Ordinal);
        Assert.Contains("GetIntegrityIssueDetailsAsync", apiSource, StringComparison.Ordinal);
        Assert.Contains("BuildQuery(\"integrity/report/details\", (\"code\", code))", apiSource, StringComparison.Ordinal);
        Assert.Contains("public const string SettingsEditPermission = \"Settings.Edit\";", sessionSnapshotSource, StringComparison.Ordinal);
        Assert.Contains("public bool CanViewIntegrityReport => IsAdmin || HasPermission(SettingsEditPermission);", sessionSnapshotSource, StringComparison.Ordinal);
        Assert.Contains("private const string PermissionsKey = \"session.permissions\";", sessionStoreSource, StringComparison.Ordinal);
        Assert.Contains("Preferences.Default.Set(PermissionsKey, string.Join(\"\\n\", response.User?.Permissions", sessionStoreSource, StringComparison.Ordinal);
        Assert.Contains("CanViewIntegrityReport = session.CanViewIntegrityReport;", settingsViewModelSource, StringComparison.Ordinal);
        Assert.Contains("운영점검은 관리자 또는 Settings.Edit 권한 계정만 사용할 수 있습니다.", settingsViewModelSource, StringComparison.Ordinal);
        Assert.Contains("ServiceHelper.GetRequiredService<IntegrityReportPage>()", settingsPageSource, StringComparison.Ordinal);
        Assert.Contains("운영점검 / 무결성", settingsPageSource, StringComparison.Ordinal);
        Assert.Contains("await _api.GetIntegrityReportAsync()", integrityViewModelSource, StringComparison.Ordinal);
        Assert.Contains("await _api.GetIntegrityIssueDetailsAsync(issue.Code)", integrityViewModelSource, StringComparison.Ordinal);
        Assert.Contains("MobileDetailPreviewLimit = 30", integrityViewModelSource, StringComparison.Ordinal);
        Assert.Contains("PC 운영점검", integrityViewModelSource, StringComparison.Ordinal);
        Assert.Contains("CreateIssueList()", integrityPageSource, StringComparison.Ordinal);
        Assert.Contains("CreateDetailList()", integrityPageSource, StringComparison.Ordinal);
        Assert.Contains("운영 서버의 전표·수금/지급·렌탈·첨부·품목/거래처 참조 무결성", integrityPageSource, StringComparison.Ordinal);
        Assert.Contains("AddSingleton<IntegrityReportViewModel>()", mauiSource, StringComparison.Ordinal);
        Assert.Contains("AddTransient<IntegrityReportPage>()", mauiSource, StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidCustomerDetail_UsesSyncedInvoiceAndPaymentCacheWhenDetailApiFails()
    {
        var root = FindRepositoryRoot();
        var viewModelSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "ViewModels",
            "CustomersViewModel.cs"));

        Assert.Contains("MobileSyncState? syncState = null;", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("syncState = await _syncCoordinator.LoadAsync();", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("var invoices = detail?.RecentInvoices ?? BuildCustomerInvoicesFromSyncedState(displayCustomer, syncState);", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("var payments = detail is null", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("BuildCustomerPaymentRowsFromSyncedState(displayCustomer, invoices, syncState)", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("거래내역은 동기화 캐시를 표시합니다.", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("private IReadOnlyList<InvoiceDto> BuildCustomerInvoicesFromSyncedState(CustomerDto customer, MobileSyncState? state)", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("state.SyncedInvoices", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("!invoice.IsDeleted &&", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("invoice.CustomerId == customer.Id &&", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("MobileSessionScopeFilter.CanAccessInvoice(snapshot, invoice)", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("private IReadOnlyList<CustomerPaymentHistoryRow> BuildCustomerPaymentRowsFromSyncedState", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("state.SyncedPayments.Where(payment => !payment.IsDeleted)", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("invoiceMap.TryGetValue(payment.InvoiceId, out var invoice)", viewModelSource, StringComparison.Ordinal);
        Assert.Contains(".GroupBy(row => row.PaymentId)", viewModelSource, StringComparison.Ordinal);
        Assert.Contains(".Take(50)", viewModelSource, StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidOfflineCaches_AreRefilteredByCurrentSessionScopeBeforeDisplayOrPendingSave()
    {
        var root = FindRepositoryRoot();
        var mobileRoot = Path.Combine(root, "Mobile", "GeoraePlan.Mobile.App");
        var scopeFilterSource = File.ReadAllText(Path.Combine(mobileRoot, "Services", "MobileSessionScopeFilter.cs"));
        var customersSource = File.ReadAllText(Path.Combine(mobileRoot, "ViewModels", "CustomersViewModel.cs"));
        var itemsSource = File.ReadAllText(Path.Combine(mobileRoot, "ViewModels", "ItemsViewModel.cs"));
        var invoiceDraftSource = File.ReadAllText(Path.Combine(mobileRoot, "ViewModels", "InvoiceDraftViewModel.cs"));
        var paymentDraftSource = File.ReadAllText(Path.Combine(mobileRoot, "ViewModels", "PaymentDraftViewModel.cs"));
        var rentalsSource = File.ReadAllText(Path.Combine(mobileRoot, "ViewModels", "RentalsViewModel.cs"));
        var transfersSource = File.ReadAllText(Path.Combine(mobileRoot, "ViewModels", "InventoryTransfersViewModel.cs"));
        var customerEditSource = File.ReadAllText(Path.Combine(mobileRoot, "Pages", "CustomerEditPage.cs"));
        var itemEditSource = File.ReadAllText(Path.Combine(mobileRoot, "Pages", "ItemEditPage.cs"));

        Assert.Contains("public static class MobileSessionScopeFilter", scopeFilterSource, StringComparison.Ordinal);
        Assert.Contains("GetReadableOfficeCodes(SessionSnapshot snapshot)", scopeFilterSource, StringComparison.Ordinal);
        Assert.Contains("TenantScopeCatalog.ScopeTenantAll", scopeFilterSource, StringComparison.Ordinal);
        Assert.Contains("TenantScopeCatalog.ScopeAdmin", scopeFilterSource, StringComparison.Ordinal);

        Assert.Contains("MobileSessionScopeFilter.CanAccessCustomer(_sessionStore.GetSnapshot(), customer)", customersSource, StringComparison.Ordinal);
        Assert.Contains("MobileSessionScopeFilter.CanAccessCustomer(snapshot, customer)", customersSource, StringComparison.Ordinal);
        Assert.Contains("MobileSessionScopeFilter.CanAccessItem(snapshot, item)", itemsSource, StringComparison.Ordinal);
        Assert.Contains("MobileSessionScopeFilter.CanAccessWarehouse(snapshot, stock.WarehouseCode)", itemsSource, StringComparison.Ordinal);
        Assert.Contains("CanSaveCurrentInvoiceScope(invoice)", invoiceDraftSource, StringComparison.Ordinal);
        Assert.Contains("MobileSessionScopeFilter.CanAccessInvoice(_sessionStore.GetSnapshot(), latestInvoice)", paymentDraftSource, StringComparison.Ordinal);
        Assert.Contains("MobileSessionScopeFilter.CanAccessRentalBillingProfile(snapshot, profile)", rentalsSource, StringComparison.Ordinal);
        Assert.Contains("MobileSessionScopeFilter.CanAccessInventoryTransfer(snapshot, transfer)", transfersSource, StringComparison.Ordinal);
        Assert.Contains("MobileSessionScopeFilter.CanAccessCustomer(_sessionStore.GetSnapshot(), dto)", customerEditSource, StringComparison.Ordinal);
        Assert.Contains("MobileSessionScopeFilter.CanAccessItem(_sessionStore.GetSnapshot(), dto)", itemEditSource, StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidSyncPendingPush_IsRefilteredByCurrentSessionBeforeDisplayOrUpload()
    {
        var root = FindRepositoryRoot();
        var mobileRoot = Path.Combine(root, "Mobile", "GeoraePlan.Mobile.App");
        var sessionSource = File.ReadAllText(Path.Combine(mobileRoot, "Models", "SessionSnapshot.cs"));
        var pendingScopeSource = File.ReadAllText(Path.Combine(mobileRoot, "Services", "MobilePendingScopeFilter.cs"));
        var coordinatorSource = File.ReadAllText(Path.Combine(mobileRoot, "Services", "SyncCoordinator.cs"));
        var syncViewSource = File.ReadAllText(Path.Combine(mobileRoot, "ViewModels", "SyncViewModel.cs"));
        var homeViewSource = File.ReadAllText(Path.Combine(mobileRoot, "ViewModels", "HomeViewModel.cs"));

        Assert.Contains("public static class MobilePendingScopeFilter", pendingScopeSource, StringComparison.Ordinal);
        Assert.Contains("public sealed class MobilePendingScopeSummary", pendingScopeSource, StringComparison.Ordinal);
        Assert.Contains("CreateScopedPushRequest(SessionSnapshot snapshot, MobileSyncState state)", pendingScopeSource, StringComparison.Ordinal);
        Assert.Contains("GetScopedPaymentAttachments(", pendingScopeSource, StringComparison.Ordinal);
        Assert.Contains("ExcludedServerMutationCount", pendingScopeSource, StringComparison.Ordinal);
        Assert.Contains("ExcludedPaymentAttachmentCount", pendingScopeSource, StringComparison.Ordinal);

        Assert.Contains("public bool CanEditCompanyProfiles", sessionSource, StringComparison.Ordinal);
        Assert.Contains("public bool CanEditSettings", sessionSource, StringComparison.Ordinal);
        Assert.Contains("public bool CanEditDelivery", sessionSource, StringComparison.Ordinal);
        Assert.Contains("public bool CanEditRentalSettings", sessionSource, StringComparison.Ordinal);
        Assert.Contains("public bool CanEditRentalProfiles", sessionSource, StringComparison.Ordinal);
        Assert.Contains("public bool CanEditRentalAssets", sessionSource, StringComparison.Ordinal);

        Assert.Contains("MobileSessionScopeFilter.CanAccessCustomer(snapshot, customer)", pendingScopeSource, StringComparison.Ordinal);
        Assert.Contains("MobileSessionScopeFilter.CanAccessItem(snapshot, item)", pendingScopeSource, StringComparison.Ordinal);
        Assert.Contains("MobileSessionScopeFilter.CanAccessInvoice(snapshot, invoice)", pendingScopeSource, StringComparison.Ordinal);
        Assert.Contains("MobileSessionScopeFilter.CanAccessTransaction(snapshot, transaction)", pendingScopeSource, StringComparison.Ordinal);
        Assert.Contains("MobileSessionScopeFilter.CanAccessInventoryTransfer(snapshot, transfer)", pendingScopeSource, StringComparison.Ordinal);
        Assert.Contains("MobileSessionScopeFilter.CanAccessRentalBillingProfile(snapshot, profile)", pendingScopeSource, StringComparison.Ordinal);
        Assert.Contains("MobileSessionScopeFilter.CanAccessRentalAsset(snapshot, asset)", pendingScopeSource, StringComparison.Ordinal);
        Assert.Contains("MobileSessionScopeFilter.CanAccessRentalAssetAssignmentHistory(snapshot, history)", pendingScopeSource, StringComparison.Ordinal);
        Assert.Contains("MobileSessionScopeFilter.CanAccessRentalBillingLog(snapshot, log)", pendingScopeSource, StringComparison.Ordinal);

        Assert.Contains("private readonly SessionStore _sessionStore;", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("MobilePendingScopeFilter.CreateScopedPushRequest(_sessionStore.GetSnapshot(), state)", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("var result = EnsurePushResult(await _api.PushAsync(scopedPush, ct));", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("RemoveSentScopedPendingMutations(state.PendingPush, scopedPush, result);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("RemoveSubmittedItemWarehouseStocks(state.PendingPush, scopedPush, result);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains(".GetScopedPaymentAttachments(_sessionStore.GetSnapshot(), state, additionalAllowedPaymentIds)", coordinatorSource, StringComparison.Ordinal);
        Assert.DoesNotContain("await _api.PushAsync(state.PendingPush, ct)", coordinatorSource, StringComparison.Ordinal);

        Assert.Contains("MobilePendingScopeFilter.CreateSummary(_sessionStore.GetSnapshot(), state)", syncViewSource, StringComparison.Ordinal);
        Assert.Contains("현재 계정 범위 밖 보류", syncViewSource, StringComparison.Ordinal);
        Assert.Contains("MobilePendingScopeFilter.CreateSummary(session, sync)", homeViewSource, StringComparison.Ordinal);
        Assert.Contains("권한/지점 범위 밖 보류", homeViewSource, StringComparison.Ordinal);
    }

    [Fact]
    public void AndroidSync_AppliesRecycleBinPurgeRecordsToCachedAndPendingRows()
    {
        var root = FindRepositoryRoot();
        var contractsSource = File.ReadAllText(Path.Combine(
            root,
            "Shared",
            "거래플랜.Shared.Contracts",
            "Contracts.cs"));
        var coordinatorSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Services",
            "SyncCoordinator.cs"));
        var contractCacheSource = File.ReadAllText(Path.Combine(
            root,
            "Mobile",
            "GeoraePlan.Mobile.App",
            "Services",
            "CustomerContractCacheStore.cs"));
        var recycleBinControllerSource = File.ReadAllText(Path.Combine(
            root,
            "Server",
            "거래플랜.Server.Api",
            "Controllers",
            "RecycleBinController.cs"));

        var paymentPurgeCaseStart = coordinatorSource.IndexOf("case \"payment\":", StringComparison.Ordinal);
        var paymentPurgeCaseEnd = coordinatorSource.IndexOf("case \"transaction\":", paymentPurgeCaseStart, StringComparison.Ordinal);
        Assert.True(paymentPurgeCaseStart >= 0, "모바일 payment purge case를 찾을 수 없습니다.");
        Assert.True(paymentPurgeCaseEnd > paymentPurgeCaseStart, "모바일 payment purge case 범위를 찾을 수 없습니다.");
        var paymentPurgeCaseSource = coordinatorSource.Substring(paymentPurgeCaseStart, paymentPurgeCaseEnd - paymentPurgeCaseStart);
        var invoicePurgeCaseStart = coordinatorSource.IndexOf("case \"invoice\":", StringComparison.Ordinal);
        var invoicePurgeCaseEnd = coordinatorSource.IndexOf("case \"payment\":", invoicePurgeCaseStart, StringComparison.Ordinal);
        Assert.True(invoicePurgeCaseStart >= 0, "모바일 invoice purge case를 찾을 수 없습니다.");
        Assert.True(invoicePurgeCaseEnd > invoicePurgeCaseStart, "모바일 invoice purge case 범위를 찾을 수 없습니다.");
        var invoicePurgeCaseSource = coordinatorSource.Substring(invoicePurgeCaseStart, invoicePurgeCaseEnd - invoicePurgeCaseStart);

        Assert.Contains("public List<RecycleBinPurgeRecordDto> PurgeRecords { get; set; } = new();", contractsSource, StringComparison.Ordinal);
        Assert.Contains("\"contract\" => await PurgeContractAsync(target, cancellationToken)", recycleBinControllerSource, StringComparison.Ordinal);
        Assert.Contains("CreatePurgeRecord(\"contract\", contract.Id", recycleBinControllerSource, StringComparison.Ordinal);
        Assert.Contains("public SyncCoordinator(JsonSyncStateStore store, GeoraePlanApiClient api, PaymentAttachmentDraftStore attachmentStore, CustomerContractCacheStore contractCacheStore, SessionStore sessionStore)", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("_contractCacheStore = contractCacheStore;", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("await ApplyPurgeRecordsAsync(state, response.PurgeRecords, ct);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("private async Task ApplyPurgeRecordsAsync(MobileSyncState state, IEnumerable<RecycleBinPurgeRecordDto>? purgeRecords, CancellationToken ct)", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains(".GroupBy(record => (Kind: NormalizePurgeRecordKind(record.Kind), record.EntityId))", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains(".OrderBy(record => GetPurgeApplyOrder(NormalizePurgeRecordKind(record.Kind)))", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("await ApplyPurgeRecordAsync(state, NormalizePurgeRecordKind(record.Kind), record.EntityId, record.Revision, ct);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("private async Task ApplyPurgeRecordAsync(MobileSyncState state, string normalizedKind, Guid entityId, long purgeRevision, CancellationToken ct)", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("case \"companyprofile\":", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("case \"company-profile\":", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("RemoveEntityById(state.PendingPush.CompanyProfiles, entityId, purgeRevision);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("case \"customercategory\":", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("case \"customer-category\":", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("RemoveEntityById(state.PendingPush.CustomerCategories, entityId, purgeRevision);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("case \"pricegradeoption\":", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("case \"price-grade-option\":", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("RemoveEntityById(state.SyncedPriceGradeOptions, entityId, purgeRevision);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("RemoveEntityById(state.PendingPush.PriceGradeOptions, entityId, purgeRevision);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("case \"tradetypeoption\":", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("case \"trade-type-option\":", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("RemoveEntityById(state.PendingPush.TradeTypeOptions, entityId, purgeRevision);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("case \"itemcategoryoption\":", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("case \"item-category-option\":", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("RemoveEntityById(state.PendingPush.ItemCategoryOptions, entityId, purgeRevision);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("RemoveEntityById(state.SyncedCustomers, entityId, purgeRevision);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("RemoveEntityById(state.PendingPush.Customers, entityId, purgeRevision);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("RemoveCustomerContractsForPurgedCustomer(state.PendingPush.CustomerContracts, entityId, purgeRevision);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("private static void RemoveCustomerContractsForPurgedCustomer(", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("contract.CustomerId == customerId &&", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("!IsEntityNewerThanPurge(contract, purgeRevision)", coordinatorSource, StringComparison.Ordinal);
        Assert.DoesNotContain("state.PendingPush.CustomerContracts.RemoveAll(contract => contract.CustomerId == entityId);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("await _contractCacheStore.RemoveCustomerAsync(entityId, purgeRevision, ct);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("case \"contract\":", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("case \"customercontract\":", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("case \"customer-contract\":", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("RemoveEntityById(state.PendingPush.CustomerContracts, entityId, purgeRevision);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("await _contractCacheStore.RemoveContractAsync(entityId, purgeRevision, ct);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("ClearRentalAssignmentHistoryCustomerReferences(state.SyncedRentalAssetAssignmentHistories, entityId, purgeRevision);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("ClearRentalAssignmentHistoryCustomerReferences(state.PendingPush.RentalAssetAssignmentHistories, entityId, purgeRevision);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("RemoveEntityById(state.SyncedItems, entityId, purgeRevision);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("RemoveItemWarehouseStocksForPurgedItem(state.SyncedItemWarehouseStocks, entityId, purgeRevision);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("RemoveItemWarehouseStocksForPurgedItem(state.PendingPush.ItemWarehouseStocks, entityId, purgeRevision);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("ClearInvoiceLineItemReferences(state.SyncedInvoices, entityId, purgeRevision);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("ClearInventoryTransferLineItemReferences(state.SyncedInventoryTransfers, entityId, purgeRevision);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("ClearRentalAssetItemReferences(state.SyncedRentalAssets, entityId, purgeRevision);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("RemoveItemIdFromBillingTemplates(state.SyncedRentalBillingProfiles, entityId, purgeRevision);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("RemoveEntityById(state.SyncedInvoices, entityId, purgeRevision);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("var removedPaymentIds = new HashSet<Guid>();", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("RemovePaymentsForPurgedInvoice(state.SyncedPayments, entityId, purgeRevision, removedPaymentIds);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("RemovePaymentsForPurgedInvoice(state.PendingPush.Payments, entityId, purgeRevision, removedPaymentIds);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("RemovePendingPaymentAttachments(state, attachment => removedPaymentIds.Contains(attachment.PaymentId));", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("var removedTransactionIds = new HashSet<Guid>();", invoicePurgeCaseSource, StringComparison.Ordinal);
        Assert.Contains("RemoveTransactionsForPurgedInvoice(state.SyncedTransactions, entityId, purgeRevision, removedTransactionIds);", invoicePurgeCaseSource, StringComparison.Ordinal);
        Assert.Contains("RemoveTransactionsForPurgedInvoice(state.PendingPush.Transactions, entityId, purgeRevision, removedTransactionIds);", invoicePurgeCaseSource, StringComparison.Ordinal);
        Assert.Contains("state.SyncedTransactionAttachments.RemoveAll(attachment => removedTransactionIds.Contains(attachment.TransactionId)", invoicePurgeCaseSource, StringComparison.Ordinal);
        Assert.Contains("state.PendingPush.TransactionAttachments.RemoveAll(attachment => removedTransactionIds.Contains(attachment.TransactionId)", invoicePurgeCaseSource, StringComparison.Ordinal);
        Assert.Contains("RemoveEntityById(state.SyncedPayments, entityId, purgeRevision);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("RemovePendingPaymentAttachments(state, attachment => attachment.PaymentId == entityId);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("RemoveEntityById(state.SyncedTransactions, entityId, purgeRevision);", paymentPurgeCaseSource, StringComparison.Ordinal);
        Assert.Contains("RemoveEntityById(state.PendingPush.Transactions, entityId, purgeRevision);", paymentPurgeCaseSource, StringComparison.Ordinal);
        Assert.Contains("state.SyncedTransactionAttachments.RemoveAll(attachment => attachment.TransactionId == entityId", paymentPurgeCaseSource, StringComparison.Ordinal);
        Assert.Contains("state.PendingPush.TransactionAttachments.RemoveAll(attachment => attachment.TransactionId == entityId", paymentPurgeCaseSource, StringComparison.Ordinal);
        Assert.Contains("var transactionRemovedPaymentIds = new HashSet<Guid>();", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("RemovePaymentForPurgedTransaction(state.SyncedPayments, entityId, purgeRevision, transactionRemovedPaymentIds);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("RemovePaymentForPurgedTransaction(state.PendingPush.Payments, entityId, purgeRevision, transactionRemovedPaymentIds);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("RemovePendingPaymentAttachments(state, attachment => transactionRemovedPaymentIds.Contains(attachment.PaymentId));", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("state.SyncedTransactionAttachments.RemoveAll(attachment => attachment.TransactionId == entityId", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("RemoveEntityById(state.SyncedInventoryTransfers, entityId, purgeRevision);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("RemoveEntityById(state.SyncedRentalBillingProfiles, entityId, purgeRevision);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("state.SyncedRentalBillingLogs.RemoveAll(log => log.BillingProfileId == entityId", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("ClearRentalBillingProfileReferences(state.SyncedRentalAssets, entityId, purgeRevision);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("ClearRentalBillingProfileReferences(state.PendingPush.RentalAssets, entityId, purgeRevision);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("ClearRentalAssignmentHistoryBillingProfileReferences(state.SyncedRentalAssetAssignmentHistories, entityId, purgeRevision);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("ClearRentalAssignmentHistoryBillingProfileReferences(state.PendingPush.RentalAssetAssignmentHistories, entityId, purgeRevision);", coordinatorSource, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(coordinatorSource, "if (IsEntityNewerThanPurge(value, purgeRevision) || value.BillingProfileId != profileId)"));
        Assert.Contains("value.BillingProfileId = null;", coordinatorSource, StringComparison.Ordinal);
        Assert.DoesNotContain("value.BillingProfileId == profileId && !IsEntityNewerThanPurge(value, purgeRevision)", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("RemoveEntityById(state.SyncedRentalAssets, entityId, purgeRevision);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("RemoveIncludedAssetIdFromBillingTemplates(state.SyncedRentalBillingProfiles, entityId, purgeRevision);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("RemoveIncludedAssetIdFromBillingTemplates(state.PendingPush.RentalBillingProfiles, entityId, purgeRevision);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("state.SyncedRentalAssetAssignmentHistories.RemoveAll(history => history.AssetId == entityId", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("RemoveEntityById(state.SyncedRentalBillingLogs, entityId, purgeRevision);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("private static void RemoveEntityById<T>(List<T> values, Guid entityId, long purgeRevision)", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("private static void RemovePaymentsForPurgedInvoice(", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("removedPaymentIds.Add(paymentId);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("private static void RemovePaymentForPurgedTransaction(", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("payment.Id == transactionId && !IsEntityNewerThanPurge(payment, purgeRevision)", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("private static void ClearInvoiceLineItemReferences(", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("line.ItemId = null;", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("private static void RemoveItemIdFromBillingTemplates(", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("item[\"ItemId\"] = Guid.Empty.ToString(\"D\");", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("private static void RemoveIncludedAssetIdFromBillingTemplates(", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("JsonNode.Parse(templateJson ?? \"[]\")", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("if (IsEntityNewerThanPurge(value, purgeRevision))", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("private static void ClearRentalAssignmentHistoryCustomerReferences(", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("if (IsEntityNewerThanPurge(value, purgeRevision) || value.CustomerId != customerId)", coordinatorSource, StringComparison.Ordinal);
        Assert.DoesNotContain("value.CustomerId == customerId && !IsEntityNewerThanPurge(value, purgeRevision)", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("value.CustomerId = null;", coordinatorSource, StringComparison.Ordinal);
        Assert.True(CountOccurrences(coordinatorSource, "value.UpdatedAtUtc = DateTime.UtcNow;") >= 5);
        Assert.Contains("value.Id == entityId && IsEntityNewerThanPurge(value, purgeRevision)", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("RemoveItemWarehouseStocksForPurgedItem(state.SyncedItemWarehouseStocks, entityId, purgeRevision);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("RemoveItemWarehouseStocksForPurgedItem(state.PendingPush.ItemWarehouseStocks, entityId, purgeRevision);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("private static void RemoveItemWarehouseStocksForPurgedItem(", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("stock.ItemId == itemId &&", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("!IsItemWarehouseStockNewerThanPurge(stock, purgeRevision)", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("private static bool IsItemWarehouseStockNewerThanPurge(ItemWarehouseStockDto stock, long purgeRevision)", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("=> stock.Revision > purgeRevision;", coordinatorSource, StringComparison.Ordinal);
        Assert.DoesNotContain("state.SyncedItemWarehouseStocks.RemoveAll(stock => stock.ItemId == entityId);", coordinatorSource, StringComparison.Ordinal);
        Assert.DoesNotContain("state.PendingPush.ItemWarehouseStocks.RemoveAll(stock => stock.ItemId == entityId);", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("public Task RemoveCustomerContractsAsync(Guid customerId, CancellationToken ct = default)", contractCacheSource, StringComparison.Ordinal);
        Assert.Contains("public async Task RemoveCustomerAsync(Guid customerId, long purgeRevision, CancellationToken ct = default)", contractCacheSource, StringComparison.Ordinal);
        Assert.Contains("public async Task RemoveContractAsync(Guid contractId, long purgeRevision, CancellationToken ct = default)", contractCacheSource, StringComparison.Ordinal);
        Assert.Contains("await using (var stream = File.OpenRead(CustomersManifestPath))", contractCacheSource, StringComparison.Ordinal);
        Assert.Contains("customers.RemoveAll(customer =>", contractCacheSource, StringComparison.Ordinal);
        Assert.Contains("!IsCustomerNewerThanPurge(customer, purgeRevision)", contractCacheSource, StringComparison.Ordinal);
        Assert.Contains("await RemoveCustomerContractsAsync(customerId, ct);", contractCacheSource, StringComparison.Ordinal);
        Assert.Contains("TryDeleteFile(Path.Combine(customerDirectory, $\"{contractId:N}.pdf\"));", contractCacheSource, StringComparison.Ordinal);
        Assert.Contains("private static bool IsEntityNewerThanPurge(CustomerContractDto contract, long purgeRevision)", contractCacheSource, StringComparison.Ordinal);
        Assert.Contains("private static bool IsCustomerNewerThanPurge(CustomerDto customer, long purgeRevision)", contractCacheSource, StringComparison.Ordinal);
    }

    [Fact]
    public void MobileApiClient_UsesSharedApiErrorFormatterForReadableServerErrors()
    {
        var source = File.ReadAllText(Path.Combine(
                FindRepositoryRoot(),
                "Mobile",
                "GeoraePlan.Mobile.App",
                "Services",
                "GeoraePlanApiClient.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains(
            "ApiErrorMessageFormatter.BuildFailureMessage(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "throw new HttpRequestException(failureMessage, null, response.StatusCode);",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("TryMapServerErrorMessage(body)", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$\"{(int)response.StatusCode} {response.ReasonPhrase} {displayBody}\"",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MobileDiagnosticFaultInjector_CanInjectNonRetryableSaveRejections()
    {
        var root = FindRepositoryRoot();
        var injectorSource = File.ReadAllText(Path.Combine(
                root,
                "Mobile",
                "GeoraePlan.Mobile.App",
                "Services",
                "MobileDiagnosticFaultInjector.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var apiClientSource = File.ReadAllText(Path.Combine(
                root,
                "Mobile",
                "GeoraePlan.Mobile.App",
                "Services",
                "GeoraePlanApiClient.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var syncCoordinatorSource = File.ReadAllText(Path.Combine(
                root,
                "Mobile",
                "GeoraePlan.Mobile.App",
                "Services",
                "SyncCoordinator.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var paymentDraftSource = File.ReadAllText(Path.Combine(
                root,
                "Mobile",
                "GeoraePlan.Mobile.App",
                "ViewModels",
                "PaymentDraftViewModel.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("await MobileDiagnosticFaultInjector.ThrowIfConfiguredAsync(relative, ct);", apiClientSource, StringComparison.Ordinal);
        Assert.Contains("File.Delete(path);", injectorSource, StringComparison.Ordinal);
        Assert.Contains("throw new MobileAuthenticationException(relative, \"401 Unauthorized (diagnostic fault)\");", injectorSource, StringComparison.Ordinal);
        Assert.Contains("\"403\" => HttpStatusCode.Forbidden", injectorSource, StringComparison.Ordinal);
        Assert.Contains("\"422\" => HttpStatusCode.UnprocessableEntity", injectorSource, StringComparison.Ordinal);
        Assert.Contains("throw new HttpRequestException(\n                    $\"{(int)statusCode} {statusCode} (diagnostic fault)\"", injectorSource, StringComparison.Ordinal);

        Assert.Contains("private static bool IsNonRetryableClientFailure(Exception ex)", syncCoordinatorSource, StringComparison.Ordinal);
        Assert.Contains("ex is MobileAuthenticationException ||", syncCoordinatorSource, StringComparison.Ordinal);
        Assert.Contains("or HttpStatusCode.Unauthorized", syncCoordinatorSource, StringComparison.Ordinal);
        Assert.Contains("or HttpStatusCode.Forbidden", syncCoordinatorSource, StringComparison.Ordinal);
        Assert.Contains("or HttpStatusCode.UnprocessableEntity", syncCoordinatorSource, StringComparison.Ordinal);
        Assert.Contains("var result = EnsurePushResult(await _api.PushAsync(request, ct));", syncCoordinatorSource, StringComparison.Ordinal);
        Assert.Contains("SyncCoordinator.IsFailedImmediateSaveWithoutServerAcceptance(state)", paymentDraftSource, StringComparison.Ordinal);
    }

    [Fact]
    public void MobileSyncCoordinator_TranslatesPushConflictReasonForDisplay()
    {
        var source = File.ReadAllText(Path.Combine(
                FindRepositoryRoot(),
                "Mobile",
                "GeoraePlan.Mobile.App",
                "Services",
                "SyncCoordinator.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains(
            "ApiConflictReasonTranslator.ToUserMessage(firstConflict?.Reason)",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "var reason = firstConflict?.Reason?.Trim();",
            source,
            StringComparison.Ordinal);
    }

    private static string ExtractBetween(string source, string startNeedle, string endNeedle)
    {
        var start = source.IndexOf(startNeedle, StringComparison.Ordinal);
        Assert.True(start >= 0, $"start marker not found: {startNeedle}");
        var end = source.IndexOf(endNeedle, start + startNeedle.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"end marker not found after {startNeedle}: {endNeedle}");
        return source[start..end];
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static IReadOnlyCollection<string> ExtractServerRecycleBinMutationKinds(string source, string action)
    {
        var kinds = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var line in source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var trimmed = line.Trim();
            var mutationNeedle = $"\" => await {action}";
            if (!trimmed.StartsWith('"') || !trimmed.Contains(mutationNeedle, StringComparison.Ordinal))
                continue;

            var end = trimmed.IndexOf('"', 1);
            Assert.True(end > 1, $"휴지통 {action} kind 파싱 실패: {trimmed}");
            kinds.Add(trimmed[1..end]);
        }

        Assert.NotEmpty(kinds);
        return kinds;
    }

    private static IReadOnlyCollection<string> ExtractMobileRecycleBinFilterKinds(string source)
    {
        const string optionNeedle = "RecycleBinFilterOption(\"";
        var kinds = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var line in source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var start = line.IndexOf(optionNeedle, StringComparison.Ordinal);
            if (start < 0)
                continue;

            start += optionNeedle.Length;
            var end = line.IndexOf('"', start);
            Assert.True(end > start, $"모바일 휴지통 필터 kind 파싱 실패: {line.Trim()}");
            kinds.Add(line[start..end]);
        }

        Assert.NotEmpty(kinds);
        return kinds;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) &&
                Directory.Exists(Path.Combine(directory.FullName, "Mobile")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
