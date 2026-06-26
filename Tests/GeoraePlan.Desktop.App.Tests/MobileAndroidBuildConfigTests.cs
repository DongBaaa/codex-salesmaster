using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class MobileAndroidBuildConfigTests
{
    [Fact]
    public void MobileProjectFallsBackToLocalAndroidToolingForDirectDotnetBuild()
    {
        var source = ReadRepositoryFile(
            "Mobile",
            "GeoraePlan.Mobile.App",
            "GeoraePlan.Mobile.App.csproj");

        Assert.Contains("<PropertyGroup Condition=\"'$(TargetFramework)' == 'net8.0-android'\">", source, StringComparison.Ordinal);
        Assert.Contains("<AndroidSdkDirectory Condition=\"'$(AndroidSdkDirectory)' == '' and '$(ANDROID_SDK_ROOT)' != '' and Exists('$(ANDROID_SDK_ROOT)')\">$(ANDROID_SDK_ROOT)</AndroidSdkDirectory>", source, StringComparison.Ordinal);
        Assert.Contains("<AndroidSdkDirectory Condition=\"'$(AndroidSdkDirectory)' == '' and '$(ANDROID_HOME)' != '' and Exists('$(ANDROID_HOME)')\">$(ANDROID_HOME)</AndroidSdkDirectory>", source, StringComparison.Ordinal);
        Assert.Contains("<AndroidSdkDirectory Condition=\"'$(AndroidSdkDirectory)' == '' and '$(LOCALAPPDATA)' != '' and Exists('$(LOCALAPPDATA)\\GeoraePlan.Android\\android-sdk')\">$(LOCALAPPDATA)\\GeoraePlan.Android\\android-sdk</AndroidSdkDirectory>", source, StringComparison.Ordinal);
        Assert.Contains("<JavaSdkDirectory Condition=\"'$(JavaSdkDirectory)' == '' and '$(JAVA_HOME)' != '' and Exists('$(JAVA_HOME)\\bin\\java.exe')\">$(JAVA_HOME)</JavaSdkDirectory>", source, StringComparison.Ordinal);
        Assert.Contains("<JavaSdkDirectory Condition=\"'$(JavaSdkDirectory)' == '' and '$(ProgramFiles)' != '' and Exists('$(ProgramFiles)\\Android\\Android Studio\\jbr\\bin\\java.exe')\">$(ProgramFiles)\\Android\\Android Studio\\jbr</JavaSdkDirectory>", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MobileReadmeDocumentsDirectBuildSdkFallbackAndXa5300Recovery()
    {
        var source = ReadRepositoryFile(
            "Mobile",
            "GeoraePlan.Mobile.App",
            "README.md");

        Assert.Contains("직접 `dotnet build` 할 때", source, StringComparison.Ordinal);
        Assert.Contains("NETSDK1147", source, StringComparison.Ordinal);
        Assert.Contains("D:\\거래플랜\\.dotnet\\dotnet.exe", source, StringComparison.Ordinal);
        Assert.Contains("%LOCALAPPDATA%\\GeoraePlan.Android\\dotnet8\\dotnet.exe", source, StringComparison.Ordinal);
        Assert.Contains("ANDROID_SDK_ROOT", source, StringComparison.Ordinal);
        Assert.Contains("%LOCALAPPDATA%\\GeoraePlan.Android\\android-sdk", source, StringComparison.Ordinal);
        Assert.Contains("XA5300", source, StringComparison.Ordinal);
        Assert.Contains("AOT 응답파일 오류", source, StringComparison.Ordinal);
        Assert.Contains("-p:AndroidSdkDirectory", source, StringComparison.Ordinal);
        Assert.Contains("-p:JavaSdkDirectory", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MobileBuildScriptsConsiderBundledDotnetBeforeSystemDotnet()
    {
        var environmentScript = ReadRepositoryFile(
            "tools",
            "mobile",
            "Test-GeoraePlanAndroidEnvironment.ps1");
        var apkBuildScript = ReadRepositoryFile(
            "tools",
            "mobile",
            "Build-GeoraePlanAndroidApk.ps1");

        Assert.Contains("(Join-Path $ProjectRoot '.dotnet\\dotnet.exe')", environmentScript, StringComparison.Ordinal);
        Assert.Contains("(Join-Path $ProjectRoot '.dotnet\\dotnet.exe')", apkBuildScript, StringComparison.Ordinal);
        Assert.True(
            apkBuildScript.IndexOf("(Join-Path $ProjectRoot '.dotnet\\dotnet.exe')", StringComparison.Ordinal) <
            apkBuildScript.IndexOf("Get-Command dotnet", StringComparison.Ordinal),
            "모바일 APK 빌드는 시스템 dotnet보다 프로젝트/전용 dotnet 후보를 먼저 확인해야 합니다.");
    }

    private static string ReadRepositoryFile(params string[] pathParts)
        => File.ReadAllText(Path.Combine([FindRepositoryRoot(), .. pathParts]));

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")))
                return current.FullName;

            current = current.Parent;
        }

        throw new InvalidOperationException("Repository root was not found.");
    }
}
