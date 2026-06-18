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
