using System.Runtime.CompilerServices;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class AndroidUiMatrixSafetyWrapperTests
{
    [Fact]
    public async Task UiMatrixAcceptance_IsRestrictedToACleanEmulatorAndRestoresState()
    {
        var source = await File.ReadAllTextAsync(Path.Combine(
            GetRepositoryRoot(),
            "tools",
            "mobile",
            "Invoke-GeoraePlanAndroidUiMatrixSafeAcceptance.ps1"));

        Assert.Contains("function Assert-CleanEmulator", source, StringComparison.Ordinal);
        Assert.Contains("'getprop', 'ro.kernel.qemu'", source, StringComparison.Ordinal);
        Assert.Contains("$qemu -cne '1'", source, StringComparison.Ordinal);
        Assert.Contains("'pm', 'path', $packageName", source, StringComparison.Ordinal);
        Assert.Contains("The UI-matrix package already exists", source, StringComparison.Ordinal);
        Assert.Contains("function Test-PackageInstalled", source, StringComparison.Ordinal);
        Assert.Contains("'uninstall', $packageName", source, StringComparison.Ordinal);
        Assert.DoesNotContain("KeepInstalled", source, StringComparison.Ordinal);

        Assert.Contains("function Get-DeviceSettingsSnapshot", source, StringComparison.Ordinal);
        Assert.Contains("function Restore-DeviceSettings", source, StringComparison.Ordinal);
        Assert.Contains("function Assert-DeviceSettingsEqual", source, StringComparison.Ordinal);
        Assert.Contains("Restore-DeviceSettings -Snapshot $settingsBefore", source, StringComparison.Ordinal);
        Assert.Contains("$settingsAfterFallback = Get-DeviceSettingsSnapshot", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UiMatrixAcceptance_BindsThePatchedVerifierAndExactContractBeforeExecution()
    {
        var source = await File.ReadAllTextAsync(Path.Combine(
            GetRepositoryRoot(),
            "tools",
            "mobile",
            "Invoke-GeoraePlanAndroidUiMatrixSafeAcceptance.ps1"));

        Assert.Contains(
            "25C4846D9FA835A68E4C7EA761C2D2BE5CA59F724C897137DC2BE2073E048177",
            source,
            StringComparison.Ordinal);
        Assert.Contains("-ValidateContractsOnly | ConvertFrom-Json", source, StringComparison.Ordinal);
        Assert.Contains("[int]$contracts.PageCount -ne 18", source, StringComparison.Ordinal);
        Assert.Contains("[int]$contracts.LogicalActionCount -ne 117", source, StringComparison.Ordinal);
        Assert.Contains("[int]$contracts.StateCount -ne 33", source, StringComparison.Ordinal);
        Assert.Contains("[int]$contracts.MeasurementCount -ne 1080", source, StringComparison.Ordinal);
        Assert.Contains("$contractValidation = 'PASS'", source, StringComparison.Ordinal);
        Assert.Contains("ContractValidation = $contractValidation", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UiMatrixAcceptance_DoesNotRequireHistoricPatchWhenCurrentVerifierExists()
    {
        var source = await File.ReadAllTextAsync(Path.Combine(
            GetRepositoryRoot(),
            "tools",
            "mobile",
            "Invoke-GeoraePlanAndroidUiMatrixSafeAcceptance.ps1"));

        var verifierPath = source.IndexOf(
            "$matrixScript = Join-Path $projectFullPath",
            StringComparison.Ordinal);
        var missingVerifierGuard = source.IndexOf(
            "if (-not [IO.File]::Exists($matrixScript))",
            verifierPath,
            StringComparison.Ordinal);
        var preparedPatchRequirement = source.IndexOf(
            "-Path $preparedPatch",
            missingVerifierGuard,
            StringComparison.Ordinal);
        var validateOnlyBranch = source.IndexOf(
            "if ($ValidateOnly)",
            preparedPatchRequirement,
            StringComparison.Ordinal);

        Assert.True(verifierPath >= 0, "The current verifier path must be resolved first.");
        Assert.True(
            missingVerifierGuard > verifierPath,
            "The prepared patch must be gated by the absence of the current verifier.");
        Assert.True(
            preparedPatchRequirement > missingVerifierGuard &&
            preparedPatchRequirement < validateOnlyBranch,
            "The historical patch must only be required inside the missing-verifier guard.");
    }

    [Fact]
    public async Task AndroidShellOracle_IsDurableAndRequiresExact120WithCleanup()
    {
        var source = await File.ReadAllTextAsync(Path.Combine(
            GetRepositoryRoot(),
            "tools",
            "mobile",
            "Invoke-GeoraePlanAndroidShellExact24.ps1"));

        Assert.Contains("BaseScenarioCount -ne 24", source, StringComparison.Ordinal);
        Assert.Contains("$results.Count -ne 120", source, StringComparison.Ordinal);
        Assert.Contains("ScenarioCount = 24", source, StringComparison.Ordinal);
        Assert.Contains("TabCount = 5", source, StringComparison.Ordinal);
        Assert.Contains("MeasurementCount = 120", source, StringComparison.Ordinal);
        Assert.Contains("Assert-ShellGeometry", source, StringComparison.Ordinal);
        Assert.Contains("Shell tabs do not span the viewport width.", source, StringComparison.Ordinal);
        Assert.Contains("Android screenshot is not a PNG file.", source, StringComparison.Ordinal);
        Assert.Contains("Restore-DeviceSnapshot $snapshot", source, StringComparison.Ordinal);
        Assert.Contains("Assert-DeviceSnapshotRestored $snapshot", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestrictedScopeAndroidCurrentAcceptance_RebindsHistoricAssetsBeforeAnyDeviceUse()
    {
        var source = await File.ReadAllTextAsync(Path.Combine(
            GetRepositoryRoot(),
            "tools",
            "mobile",
            "Invoke-GeoraePlanRestrictedScopeAndroidCurrentAcceptance.ps1"));

        Assert.Contains(
            "990A1E52929ECF993E747AAE8B3646E2D6AF9677FE6B9C3ED451C722E4AA73B3",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "D6A0E2BD2BA51F996C11D54F4DA375216F93C93540AE8350A3AB7393478E71DB",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "B9B12048ADEA6F70A29C9FAA0DD039596A1095A03A268CAB7D3C88F922209736",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "AF8CB7DA72887A03A2B95ADB525B273752FB420409312FEDB52B53E8B237C4AB",
            source,
            StringComparison.Ordinal);
        Assert.Contains("function Replace-ExactOnce", source, StringComparison.Ordinal);
        Assert.Contains("PermissionBindingFileCount -ne 15", source, StringComparison.Ordinal);
        Assert.Contains("PermissionBindingTokenCount -ne 81", source, StringComparison.Ordinal);
        Assert.Contains("[int]$state.StateVariantCount -ne 33", source, StringComparison.Ordinal);
        Assert.Contains("[int]$state.KeyboardStateCount -ne 24", source, StringComparison.Ordinal);
        Assert.Contains("[int]$state.ExactMeasurementCount -ne 1080", source, StringComparison.Ordinal);
        Assert.Contains("if ($ValidateOnly)", source, StringComparison.Ordinal);
        Assert.Contains("ActualEmulatorUsed = $false", source, StringComparison.Ordinal);
        Assert.Contains("RealAndroidUiStillRequired = $true", source, StringComparison.Ordinal);
        Assert.Contains("if (-not $ExecuteLocalEmulatorAcceptance)", source, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath $stagingRoot -Recurse -Force", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CurrentOperationalAcceptance_BindsAllCurrentAndroidContractsBeforeDeviceUse()
    {
        var source = await File.ReadAllTextAsync(Path.Combine(
            GetRepositoryRoot(),
            "tools",
            "mobile",
            "Invoke-GeoraePlanAndroidCurrentOperationalAcceptance.ps1"));

        Assert.Contains(
            "10070EEC9CBC79FAC59F8438AF7DF7561D82E7D147EBC68CB8DB8A7DC02FE2D8",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "E879A83678133A96946FAAB84056235BE1EDECC6382D9C42323D86A4715EDDFC",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "F11CA04D63DD8195F62E5DDF6560EDDE9B88914F6755ECAB6C2FF4B665171135",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "B9B12048ADEA6F70A29C9FAA0DD039596A1095A03A268CAB7D3C88F922209736",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "5E393E292A39D573B9DCE6C84BCDEA60B8090226FA12B5457D2E7A5C3DCB17BE",
            source,
            StringComparison.Ordinal);
        Assert.Contains("if ($ValidateOnly)", source, StringComparison.Ordinal);
        Assert.Contains("-ValidateOnly", source, StringComparison.Ordinal);
        Assert.Contains("PageMeasurementCount = 1080", source, StringComparison.Ordinal);
        Assert.Contains("ShellMeasurementCount = 120", source, StringComparison.Ordinal);
        Assert.Contains("RestrictedScreenshotCount = 2", source, StringComparison.Ordinal);
        Assert.Contains("ActualEmulatorUsed = $false", source, StringComparison.Ordinal);
        Assert.Contains("RealAndroidUiStillRequired = $true", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CurrentOperationalAcceptance_UsesOneOuterEmulatorCleanupBoundary()
    {
        var source = await File.ReadAllTextAsync(Path.Combine(
            GetRepositoryRoot(),
            "tools",
            "mobile",
            "Invoke-GeoraePlanAndroidCurrentOperationalAcceptance.ps1"));

        Assert.Contains("if (-not $ExecuteLocalEmulatorAcceptance)", source, StringComparison.Ordinal);
        Assert.Contains("function Assert-CleanEmulator", source, StringComparison.Ordinal);
        Assert.Contains("function Get-DeviceSettingsSnapshot", source, StringComparison.Ordinal);
        Assert.Contains("function Restore-DeviceSettings", source, StringComparison.Ordinal);
        Assert.Contains("function Assert-DeviceSettingsEqual", source, StringComparison.Ordinal);
        Assert.Contains("& $matrixSafe @matrixParameters", source, StringComparison.Ordinal);
        Assert.Contains("& $restrictedCurrent @restrictedParameters", source, StringComparison.Ordinal);
        Assert.Contains("'uninstall', $packageName", source, StringComparison.Ordinal);
        Assert.Contains("Restore-DeviceSettings -Snapshot $settingsBefore", source, StringComparison.Ordinal);
        Assert.Contains("Assert-DeviceSettingsEqual -Before $settingsBefore", source, StringComparison.Ordinal);
        Assert.Contains("PackageAbsentAfterRun = $true", source, StringComparison.Ordinal);
        Assert.Contains("DeviceSettingsRestored = $true", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IncludeUpdateInPlace", source, StringComparison.Ordinal);
        Assert.DoesNotContain("trade.2884.kr", source, StringComparison.Ordinal);

        var matrixIndex = source.IndexOf("& $matrixSafe @matrixParameters", StringComparison.Ordinal);
        var restrictedIndex = source.IndexOf("& $restrictedCurrent @restrictedParameters", StringComparison.Ordinal);
        Assert.True(matrixIndex >= 0 && restrictedIndex > matrixIndex);
    }

    private static string GetRepositoryRoot([CallerFilePath] string sourcePath = "")
    {
        var root = Path.GetFullPath(
            Path.Combine(Path.GetDirectoryName(sourcePath)!, "..", ".."));
        Assert.True(
            Directory.Exists(Path.Combine(root, "Desktop")) &&
            Directory.Exists(Path.Combine(root, "Mobile")) &&
            Directory.Exists(Path.Combine(root, "tools")),
            "The repository root could not be resolved from the test source path.");
        return root;
    }
}
