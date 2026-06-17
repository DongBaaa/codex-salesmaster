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
        AssertInOrder(
            initializeTempSource,
            "Join-Path $ProjectRoot 'temp'",
            "Join-Path 'D:\\거래플랜' 'temp'",
            "$env:TEMP");

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
