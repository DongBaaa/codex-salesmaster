using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class ReleaseTempPathGuardTests
{
    [Fact]
    public void DesktopAppPaths_PrefersDDriveTempAndOverridesProcessTempVariables()
    {
        var source = ReadRepositoryFile(
            "Desktop",
            "거래플랜.Desktop.App",
            "Infrastructure",
            "AppPaths.cs");

        Assert.Contains("private const string TempRootOverrideEnvironmentKey = \"GEORAEPLAN_TEMP_ROOT\";", source, StringComparison.Ordinal);
        Assert.Contains("Environment.SetEnvironmentVariable(\"TEMP\", TempRoot);", source, StringComparison.Ordinal);
        Assert.Contains("Environment.SetEnvironmentVariable(\"TMP\", TempRoot);", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "Environment.GetEnvironmentVariable(TempRootOverrideEnvironmentKey)",
            "Path.Combine(\"D:\\\\\", \"거래플랜\", \"temp\")",
            "Path.Combine(_base, \"temp\")");
    }

    [Fact]
    public void DesktopUpdater_UsesAppTempDirectoryForDownloadedAndPreparedArtifacts()
    {
        var source = ReadRepositoryFile(
            "Desktop",
            "거래플랜.Desktop.App",
            "Services",
            "DesktopAppUpdateService.cs");

        Assert.Contains("directoryPath = AppPaths.TempDir;", source, StringComparison.Ordinal);
        Assert.Contains("var tempRoot = AppPaths.TempDir;", source, StringComparison.Ordinal);
        Assert.Contains("Path.Combine(AppPaths.TempDir, \"GeoraePlan\")", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Path.GetTempPath()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleasePackagingScripts_PreferProjectOrDDriveTempBeforeSystemTempFallback()
    {
        var initializeTempSource = ReadRepositoryFile(
            "tools",
            "common",
            "Initialize-GeoraePlanTemp.ps1");

        Assert.Contains("$env:GEORAEPLAN_TEMP_ROOT = $resolvedGeoraePlanTempRoot", initializeTempSource, StringComparison.Ordinal);
        Assert.Contains("$env:TEMP = $resolvedGeoraePlanTempRoot", initializeTempSource, StringComparison.Ordinal);
        Assert.Contains("$env:TMP = $resolvedGeoraePlanTempRoot", initializeTempSource, StringComparison.Ordinal);
        Assert.Contains("$effectiveProjectRoot = $ProjectRoot", initializeTempSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Join-Path 'D:\\", initializeTempSource, StringComparison.Ordinal);
        AssertInOrder(
            initializeTempSource,
            "Join-Path $effectiveProjectRoot 'temp'",
            "$env:TEMP");

        var updateAssetsSource = ReadRepositoryFile(
            "tools",
            "release",
            "Publish-GeoraePlanUpdateAssets.ps1");

        Assert.Contains("Initialize-GeoraePlanTemp.ps1", updateAssetsSource, StringComparison.Ordinal);
        Assert.Contains(". $tempInitializer -ProjectRoot $ProjectRoot", updateAssetsSource, StringComparison.Ordinal);

        var desktopInstallerSource = ReadRepositoryFile(
            "tools",
            "release",
            "Build-GeoraePlanDesktopNativeInstallers.ps1");

        Assert.Contains("Environment.SetEnvironmentVariable(\"TEMP\", resolvedPath);", desktopInstallerSource, StringComparison.Ordinal);
        Assert.Contains("Environment.SetEnvironmentVariable(\"TMP\", resolvedPath);", desktopInstallerSource, StringComparison.Ordinal);
        AssertInOrder(
            desktopInstallerSource,
            "Environment.GetEnvironmentVariable(TempRootOverrideEnvironmentKey)",
            "Path.Combine(\"D:\\\\\", \"거래플랜\", \"temp\")",
            "Path.GetTempPath()");
    }

    [Fact]
    public void AndroidBuildScript_DisableAotOverridesProjectAotDefaults()
    {
        var source = ReadRepositoryFile(
            "tools",
            "mobile",
            "Build-GeoraePlanAndroidApk.ps1");

        Assert.Contains("$DisableAot.IsPresent", source, StringComparison.Ordinal);
        Assert.Contains("$arguments += '-p:RunAOTCompilation=false'", source, StringComparison.Ordinal);
        Assert.Contains("$arguments += '-p:AndroidEnableProfiledAot=false'", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "$shouldEnableAot = $isReleaseBuild -and -not $DisableAot.IsPresent",
            "$arguments += '-p:RunAOTCompilation=true'",
            "elseif ($DisableAot.IsPresent)",
            "$arguments += '-p:RunAOTCompilation=false'",
            "$shouldDisableTrimming = $DisableTrimming.IsPresent");
    }

    [Fact]
    public void OperationalGate_ValidatesUpdatePackageHeadAndGetHeadersWithoutDownloadingPackages()
    {
        var source = ReadRepositoryFile(
            "tools",
            "ops",
            "Invoke-GeoraePlanOperationalGate.ps1");

        Assert.Contains("function Invoke-UpdatePackageHeaderProbe", source, StringComparison.Ordinal);
        Assert.Contains("[System.Net.Http.HttpCompletionOption]::ResponseHeadersRead", source, StringComparison.Ordinal);
        Assert.Contains("function Test-UpdatePackageDownloadHeaders", source, StringComparison.Ordinal);
        Assert.Contains("HEAD Content-Length", source, StringComparison.Ordinal);
        Assert.Contains("GET Content-Length", source, StringComparison.Ordinal);
        Assert.Contains("manifest fileSize", source, StringComparison.Ordinal);
        Assert.Contains("update-downloads.md", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ContentLength.HasValue", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "$manifest = Invoke-TextProbe",
            "$updateDownloadReportPath = Join-Path $OutputDirectory 'update-downloads.md'",
            "Add-Check -Checks $checks -Name 'update package downloads'",
            "$liveObservationScript = Join-Path $resolvedRoot");
    }

    [Fact]
    public void OperationalGate_ChecksReadinessBeforeManifestAndDatabaseDependentChecks()
    {
        var source = ReadRepositoryFile(
            "tools",
            "ops",
            "Invoke-GeoraePlanOperationalGate.ps1");

        Assert.Contains("Add-Check -Checks $checks -Name 'live healthz'", source, StringComparison.Ordinal);
        Assert.Contains("Add-Check -Checks $checks -Name 'live readyz'", source, StringComparison.Ordinal);
        Assert.Contains("readyz status={0} error={1} body={2}", source, StringComparison.Ordinal);
        Assert.Contains("function Test-ReadyProbeSemantic", source, StringComparison.Ordinal);
        Assert.Contains("function Invoke-ReadyProbeWithRetry", source, StringComparison.Ordinal);
        Assert.Contains("readyz attempt={0} semantic={1}", source, StringComparison.Ordinal);
        Assert.Contains("Start-Sleep -Seconds $DelaySec", source, StringComparison.Ordinal);
        Assert.Contains("$status -eq 'ready'", source, StringComparison.Ordinal);
        Assert.Contains("$dbStarted -eq $true", source, StringComparison.Ordinal);
        Assert.Contains("$dbCompleted -eq $true", source, StringComparison.Ordinal);
        Assert.Contains("$dbFailed -eq $false", source, StringComparison.Ordinal);
        Assert.Contains("200 OK but readiness body is not ready", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "$health = Invoke-TextProbe -Uri ($BaseUrl + '/healthz')",
            "$readyProbeResult = Invoke-ReadyProbeWithRetry -Uri ($BaseUrl + '/readyz') -LogPath $logPath",
            "$readySemanticResult = $readyProbeResult.SemanticResult",
            "$manifest = Invoke-TextProbe -Uri ($BaseUrl + \"/updates/manifest?channel=$Channel\")");
    }

    private static string ReadRepositoryFile(params string[] pathParts)
        => File.ReadAllText(Path.Combine([FindRepositoryRoot(), .. pathParts]));

    private static void AssertInOrder(string source, params string[] tokens)
    {
        var previousIndex = -1;
        foreach (var token in tokens)
        {
            var index = source.IndexOf(token, StringComparison.Ordinal);
            Assert.True(index >= 0, $"Token was not found: {token}");
            Assert.True(index > previousIndex, $"Token was out of order: {token}");
            previousIndex = index;
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) &&
                Directory.Exists(Path.Combine(directory.FullName, "Desktop")) &&
                Directory.Exists(Path.Combine(directory.FullName, "tools")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
