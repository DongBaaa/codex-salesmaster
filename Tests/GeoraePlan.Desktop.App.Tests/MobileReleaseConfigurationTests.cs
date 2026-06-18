using Xunit;

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
        Assert.Contains("AddTransactionBillingRunEvidence", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("AddInvoiceBillingRunEvidence", viewModelSource, StringComparison.Ordinal);
        Assert.Contains("AddPaymentBillingRunEvidence", viewModelSource, StringComparison.Ordinal);
        var normalizedViewModelSource = viewModelSource.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.DoesNotContain("state.SyncedRentalBillingLogs\n            .Where(log => MatchesBillingLog", normalizedViewModelSource, StringComparison.Ordinal);
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

        Assert.Contains("TryMapServerErrorMessage(body)", apiSource, StringComparison.Ordinal);
        Assert.Contains("\"contract_content_unavailable\"", apiSource, StringComparison.Ordinal);
        Assert.Contains("\"attachment_content_unavailable\"", apiSource, StringComparison.Ordinal);
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
        Assert.Contains("저장 대기: 거래처 0건", source, StringComparison.Ordinal);
        Assert.Contains("전표 0건", source, StringComparison.Ordinal);
        Assert.Contains("ExerciseStoppedServerDirtySync = [bool]$ExerciseStoppedServerDirtySync", source, StringComparison.Ordinal);
        Assert.Contains("ExerciseStoppedServerDirtySync", source, StringComparison.Ordinal);
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
        Assert.Contains("HttpStatusCode.ServiceUnavailable", retryPolicySource, StringComparison.Ordinal);
        Assert.Contains("RefreshSelectedInvoiceForSaveAsync(SelectedInvoice)", source, StringComparison.Ordinal);
        Assert.Contains("SavePaymentImmediatelyAsync(payment, Attachments, linkedTransaction)", source, StringComparison.Ordinal);
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
        Assert.Contains("거래처 {state.PendingCustomerCount}건 / 품목 {state.PendingItemCount}건", syncViewSource, StringComparison.Ordinal);
        Assert.Contains("거래처 {sync.PendingCustomerCount:N0}건 / 품목 {sync.PendingItemCount:N0}건", homeViewSource, StringComparison.Ordinal);
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
