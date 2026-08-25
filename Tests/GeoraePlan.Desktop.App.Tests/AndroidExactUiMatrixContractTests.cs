using System.Text.RegularExpressions;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class AndroidExactUiMatrixContractTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void UiMatrixBuild_IsolatedFromProductionIdentityStartupAndNetwork()
    {
        var project = Read("Mobile/GeoraePlan.Mobile.App/GeoraePlan.Mobile.App.csproj");
        var app = Read("Mobile/GeoraePlan.Mobile.App/App.cs");
        var manifest = Read("Mobile/GeoraePlan.Mobile.App/Platforms/Android/AndroidManifest.UiMatrix.xml");
        var activity = Read("Mobile/GeoraePlan.Mobile.App/Platforms/Android/MainActivity.cs");

        Assert.Contains("'$(GeoraePlanMobileUiMatrix)' == 'true'", project, StringComparison.Ordinal);
        Assert.Contains("GEORAEPLAN_MOBILE_UI_MATRIX", project, StringComparison.Ordinal);
        Assert.Contains("kr.georaeplan.mobile.uimatrix", project, StringComparison.Ordinal);
        Assert.Contains("AndroidManifest.UiMatrix.xml", project, StringComparison.Ordinal);
        Assert.Contains("tools:node=\"remove\"", manifest, StringComparison.Ordinal);
        Assert.Contains("android.permission.INTERNET", manifest, StringComparison.Ordinal);
        Assert.Contains("android.permission.ACCESS_NETWORK_STATE", manifest, StringComparison.Ordinal);

        var matrixIndex = app.IndexOf("#if GEORAEPLAN_MOBILE_UI_MATRIX", StringComparison.Ordinal);
        var startupIndex = app.IndexOf("_ = InitializeRootAsync();", StringComparison.Ordinal);
        Assert.True(matrixIndex >= 0 && startupIndex > matrixIndex);
        Assert.Contains("new UiMatrix.MobileUiMatrixHostPage(sessionStore)", app, StringComparison.Ordinal);
        Assert.Contains("RequestExtraName", activity, StringComparison.Ordinal);
        Assert.Contains("OnNewIntent", activity, StringComparison.Ordinal);
        Assert.Contains("Window is not null", activity, StringComparison.Ordinal);
        Assert.Contains("WindowCompat.SetDecorFitsSystemWindows(Window, false)", activity, StringComparison.Ordinal);
        Assert.Contains("WindowSoftInputMode = Android.Views.SoftInput.AdjustResize", activity, StringComparison.Ordinal);
        Assert.Contains("IOnApplyWindowInsetsListener", activity, StringComparison.Ordinal);
        Assert.Contains("WindowInsetsCompat.Type.SystemBars()", activity, StringComparison.Ordinal);
        Assert.Contains("WindowInsetsCompat.Type.Ime()", activity, StringComparison.Ordinal);
        Assert.Contains("Math.Max(systemBars.Bottom, ime.Bottom)", activity, StringComparison.Ordinal);
    }

    [Fact]
    public void UiMatrixHost_UsesExactProductionPagesAndFailClosedMeasurements()
    {
        var host = Read("Mobile/GeoraePlan.Mobile.App/UiMatrix/MobileUiMatrixHostPage.cs");
        var theme = Read("Mobile/GeoraePlan.Mobile.App/Theme/GeoraePlanTheme.cs");
        var expectedPages = new[]
        {
            "CustomerContractsPage", "CustomerEditPage", "CustomersPage", "HomePage",
            "IntegrityReportPage", "InventoryTransfersPage", "InvoiceDraftPage", "InvoicesPage",
            "ItemEditPage", "ItemsPage", "LoginPage", "PaymentAttachmentsPage",
            "PaymentDraftPage", "RecycleBinPage", "RentalsPage", "SettingsPage", "SyncPage",
            "UpdateRequiredPage"
        };

        foreach (var page in expectedPages)
            Assert.Contains($"nameof({page})", host, StringComparison.Ordinal);
        Assert.Equal(18, Regex.Matches(host, "nameof\\([A-Za-z]+Page\\) =>").Count);
        Assert.Contains("MobileUiMatrixActionRegistry.Reset()", host, StringComparison.Ordinal);
        Assert.Contains("ExpectedActionCount", host, StringComparison.Ordinal);
        Assert.Contains("action-text-clipped", host, StringComparison.Ordinal);
        Assert.Contains("text-element-clipped", host, StringComparison.Ordinal);
        Assert.Contains("GetSafeViewport", host, StringComparison.Ordinal);
        Assert.Contains("GetWindowVisibleDisplayFrame", host, StringComparison.Ordinal);
        Assert.Contains("WaitForActionLayoutAsync", host, StringComparison.Ordinal);
        Assert.Contains("action.Element.Width > 0 && action.Element.Height > 0", host, StringComparison.Ordinal);
        Assert.Contains("TextElementCount", host, StringComparison.Ordinal);
        Assert.Contains("MeasureVisibleTextElements", host, StringComparison.Ordinal);
        Assert.Contains("action-overlap", host, StringComparison.Ordinal);
        Assert.Contains("android-keyboard-not-visible", Read("tools/mobile/Invoke-GeoraePlanAndroidExactUiMatrix.ps1"), StringComparison.Ordinal);
        Assert.DoesNotContain("HttpClient", host, StringComparison.Ordinal);
        Assert.DoesNotContain("LoginAsync(", host, StringComparison.Ordinal);

        Assert.Contains("[CallerFilePath]", theme, StringComparison.Ordinal);
        Assert.Contains("[CallerLineNumber]", theme, StringComparison.Ordinal);
        Assert.Contains("MobileUiMatrixActionRegistry.RegisterButton", theme, StringComparison.Ordinal);
    }

    [Fact]
    public void UiMatrixRunner_RequiresExact1080AndRestoresDeviceState()
    {
        var source = Read("tools/mobile/Invoke-GeoraePlanAndroidExactUiMatrix.ps1");
        Assert.Contains("[Parameter(Mandatory = $true)][string]$ActionContractPath", source, StringComparison.Ordinal);
        Assert.Contains("[Parameter(Mandatory = $true)][string]$ExecutionPlanPath", source, StringComparison.Ordinal);
        Assert.Contains("[switch]$ValidateContractsOnly", source, StringComparison.Ordinal);
        Assert.Contains("-p:AndroidManifest=Platforms\\Android\\AndroidManifest.UiMatrix.xml", source, StringComparison.Ordinal);
        Assert.Contains("GeoraePlan.Android\\dotnet8\\dotnet.exe", source, StringComparison.Ordinal);
        Assert.Contains("The dedicated Android dotnet runtime is missing.", source, StringComparison.Ordinal);
        Assert.Contains("F11CA04D63DD8195F62E5DDF6560EDDE9B88914F6755ECAB6C2FF4B665171135", source, StringComparison.Ordinal);
        Assert.Contains("5E393E292A39D573B9DCE6C84BCDEA60B8090226FA12B5457D2E7A5C3DCB17BE", source, StringComparison.Ordinal);
        Assert.Contains("Require-Exact ([int]$action.PageCount) 18", source, StringComparison.Ordinal);
        Assert.Contains("Require-Exact ([int]$action.LogicalActionCount) 117", source, StringComparison.Ordinal);
        Assert.Contains("Require-Exact ([int]$plan.StateCount) 33", source, StringComparison.Ordinal);
        Assert.Contains("Require-Exact ([int]$plan.KeyboardStateCount) 24", source, StringComparison.Ordinal);
        Assert.Contains("Require-Exact ([int]$plan.MeasurementCount) 1080", source, StringComparison.Ordinal);
        Assert.Contains("Assert-NoNetworkPermission", source, StringComparison.Ordinal);
        Assert.Contains("Restore-DeviceSettings $settingsSnapshot", source, StringComparison.Ordinal);
        Assert.Contains("finally {", source, StringComparison.Ordinal);
        Assert.Contains("uninstall',$packageName", source, StringComparison.Ordinal);
        Assert.Contains("Write-AtomicJson", source, StringComparison.Ordinal);
        Assert.Contains("Save-PageSuccessEvidence", source, StringComparison.Ordinal);
        Assert.Contains("Read-PngEvidenceMetadata", source, StringComparison.Ordinal);
        Assert.Contains("'390x844@font1.0'", source, StringComparison.Ordinal);
        Assert.Contains("SuccessScreenshotCount = $successScreenshots.Count", source, StringComparison.Ordinal);
        Assert.Contains("Android success screenshot coverage is not the exact 18-page set.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("kr.georaeplan.mobile'", source, StringComparison.Ordinal);
    }

    private static string Read(string relativePath)
        => File.ReadAllText(Path.Combine(
            RepositoryRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "Mobile")) &&
                Directory.Exists(Path.Combine(current.FullName, "Tests")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
