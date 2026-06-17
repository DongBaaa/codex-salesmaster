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
