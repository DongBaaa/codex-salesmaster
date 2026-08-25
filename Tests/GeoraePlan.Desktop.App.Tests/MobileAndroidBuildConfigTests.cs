using System.Diagnostics;
using System.Text.RegularExpressions;
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
    public void MobileAndroidToolingPinsJdk17AndRejectsOtherJavaMajors()
    {
        var project = ReadRepositoryFile(
            "Mobile",
            "GeoraePlan.Mobile.App",
            "GeoraePlan.Mobile.App.csproj");
        var readme = ReadRepositoryFile(
            "Mobile",
            "GeoraePlan.Mobile.App",
            "README.md");
        var scripts = new[]
        {
            ReadRepositoryFile("tools", "mobile", "Build-GeoraePlanAndroidApk.ps1"),
            ReadRepositoryFile("tools", "mobile", "Bootstrap-GeoraePlanAndroidBuildEnvironment.ps1"),
            ReadRepositoryFile("tools", "mobile", "Test-GeoraePlanAndroidEnvironment.ps1")
        };

        const string pinnedJdk = "D:\\DevCaches\\georaeplan-android-jdk\\microsoft-jdk-17.0.20";
        Assert.Contains("$(GEORAEPLAN_ANDROID_JAVA_SDK)", project, StringComparison.Ordinal);
        Assert.Contains(pinnedJdk, project, StringComparison.Ordinal);
        Assert.True(
            project.IndexOf(pinnedJdk, StringComparison.Ordinal) <
            project.IndexOf("$(JAVA_HOME)", StringComparison.Ordinal),
            "직접 dotnet 빌드도 JDK 21 JAVA_HOME보다 검증된 JDK 17 캐시를 먼저 선택해야 합니다.");

        foreach (var script in scripts)
        {
            Assert.Contains("GEORAEPLAN_ANDROID_JAVA_SDK", script, StringComparison.Ordinal);
            Assert.Contains(pinnedJdk, script, StringComparison.Ordinal);
            Assert.Contains("function Get-JavaSdkMajorVersion", script, StringComparison.Ordinal);
            Assert.Contains("-ne 17", script, StringComparison.Ordinal);
            Assert.Contains("bin\\javac.exe", script, StringComparison.Ordinal);
        }

        Assert.Contains("Microsoft OpenJDK 17", readme, StringComparison.Ordinal);
        Assert.Contains(pinnedJdk, readme, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "-p:JavaSdkDirectory=\"C:\\Program Files\\Android\\Android Studio\\jbr\"",
            readme,
            StringComparison.Ordinal);
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

    [Fact]
    public void AndroidSigningPasswordsUseHeldPrivateFilesAndNeverDotnetArguments()
    {
        var source = ReadRepositoryFile(
            "tools",
            "mobile",
            "Build-GeoraePlanAndroidApk.ps1");

        Assert.DoesNotContain(
            "-p:AndroidSigningStorePass=$StorePass",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "-p:AndroidSigningKeyPass=$KeyPass",
            source,
            StringComparison.Ordinal);
        Assert.Contains("[IO.FileMode]::CreateNew", source, StringComparison.Ordinal);
        Assert.Contains("[IO.FileShare]::Read", source, StringComparison.Ordinal);
        Assert.Contains(
            "[Security.AccessControl.FileSystemRights]::Read -bor",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "[Security.AccessControl.FileSystemRights]::Write",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "$secretPath,\n            [IO.FileMode]::CreateNew,\n            [Security.AccessControl.FileSystemRights]::FullControl",
            source,
            StringComparison.Ordinal);
        Assert.Contains("[IO.FileOptions]::WriteThrough", source, StringComparison.Ordinal);
        Assert.Contains("$security.SetAccessRuleProtection($true, $false)", source, StringComparison.Ordinal);
        Assert.Contains("$stream.Flush($true)", source, StringComparison.Ordinal);
        Assert.Contains("[Array]::Clear($bytes, 0, $bytes.Length)", source, StringComparison.Ordinal);
        Assert.Contains("AndroidSigningStorePass=file:", source, StringComparison.Ordinal);
        Assert.Contains("AndroidSigningKeyPass=file:", source, StringComparison.Ordinal);
        Assert.Contains("Remove-AndroidSigningSecretPair", source, StringComparison.Ordinal);
        Assert.Contains("[IO.File]::Delete($entry.Path)", source, StringComparison.Ordinal);
        Assert.Contains("[switch]$DetailedBuildLog", source, StringComparison.Ordinal);
        Assert.Contains("$arguments += '--verbosity'", source, StringComparison.Ordinal);
        Assert.Contains("$arguments += 'normal'", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionAndroidSigningUsesEnvironmentReferencesAndRejectsPlaintextInputs()
    {
        var apkScript = ReadRepositoryFile(
            "tools",
            "mobile",
            "Build-GeoraePlanAndroidApk.ps1");
        var bundleScript = ReadRepositoryFile(
            "tools",
            "mobile",
            "Build-GeoraePlanAndroidBundle.ps1");
        var keystoreScript = ReadRepositoryFile(
            "tools",
            "mobile",
            "New-GeoraePlanAndroidKeystore.ps1");
        var signingExample = ReadRepositoryFile(
            "Mobile",
            "GeoraePlan.Mobile.App",
            "android-signing.example.json");
        var buildGuide = ReadRepositoryFile(
            "Mobile",
            "안드로이드_빌드_서명_설치_가이드_2026-03-19.md");

        Assert.Contains("[string]$StorePassEnvironmentVariable", apkScript, StringComparison.Ordinal);
        Assert.Contains("[string]$KeyPassEnvironmentVariable", apkScript, StringComparison.Ordinal);
        Assert.Contains("storePassEnvironmentVariable", apkScript, StringComparison.Ordinal);
        Assert.Contains("keyPassEnvironmentVariable", apkScript, StringComparison.Ordinal);
        Assert.Contains("[Environment]::GetEnvironmentVariable", apkScript, StringComparison.Ordinal);
        Assert.Contains(
            "Production Android signing passwords must be supplied through storePassEnvironmentVariable/keyPassEnvironmentVariable",
            apkScript,
            StringComparison.Ordinal);
        Assert.Contains("-StorePassEnvironmentVariable $StorePassEnvironmentVariable", bundleScript, StringComparison.Ordinal);
        Assert.Contains("-KeyPassEnvironmentVariable $KeyPassEnvironmentVariable", bundleScript, StringComparison.Ordinal);

        Assert.Contains("'-storepass:env'", keystoreScript, StringComparison.Ordinal);
        Assert.Contains("'-keypass:env'", keystoreScript, StringComparison.Ordinal);
        Assert.DoesNotContain("'-storepass', $StorePass", keystoreScript, StringComparison.Ordinal);
        Assert.DoesNotContain("'-keypass', $KeyPass", keystoreScript, StringComparison.Ordinal);

        Assert.Contains("\"storePassEnvironmentVariable\"", signingExample, StringComparison.Ordinal);
        Assert.Contains("\"keyPassEnvironmentVariable\"", signingExample, StringComparison.Ordinal);
        Assert.DoesNotContain("\"storePass\"", signingExample, StringComparison.Ordinal);
        Assert.DoesNotContain("\"keyPass\"", signingExample, StringComparison.Ordinal);
        Assert.DoesNotContain("-StorePass ", buildGuide, StringComparison.Ordinal);
        Assert.DoesNotContain("-KeyPass ", buildGuide, StringComparison.Ordinal);
        Assert.DoesNotContain("\"storePass\"", buildGuide, StringComparison.Ordinal);
        Assert.DoesNotContain("\"keyPass\"", buildGuide, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProductionAndroidSigningRejectsPlaintextAndDrainsEnvironmentSecretsBeforePublish()
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(repositoryRoot, "tools", "mobile", "Build-GeoraePlanAndroidApk.ps1");
        var fixtureRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-android-signing-input-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureRoot);

        const string storeSecret = "store-canary-7Z!";
        const string keySecret = "key-canary-8Y!";
        const string storeEnvironmentVariable = "GEORAEPLAN_TEST_ANDROID_STORE_SECRET";
        const string keyEnvironmentVariable = "GEORAEPLAN_TEST_ANDROID_KEY_SECRET";

        try
        {
            var projectPath = Path.Combine(fixtureRoot, "Mobile", "GeoraePlan.Mobile.App", "Fixture.csproj");
            var keystorePath = Path.Combine(fixtureRoot, "signing", "release.keystore");
            var javaRoot = Path.Combine(fixtureRoot, "jdk17");
            var androidRoot = Path.Combine(fixtureRoot, "android-sdk");
            var outputRoot = Path.Combine(fixtureRoot, "artifacts");
            var capturePath = Path.Combine(fixtureRoot, "fake-dotnet-capture.txt");
            var fakeDotnetPath = Path.Combine(fixtureRoot, "fake-dotnet.cmd");
            var inlineConfigPath = Path.Combine(fixtureRoot, "inline-signing.json");
            var environmentConfigPath = Path.Combine(fixtureRoot, "environment-signing.json");

            Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(keystorePath)!);
            Directory.CreateDirectory(Path.Combine(javaRoot, "bin"));
            Directory.CreateDirectory(androidRoot);
            File.WriteAllText(projectPath, "<Project />");
            File.WriteAllBytes(keystorePath, [1, 2, 3, 4]);
            File.WriteAllText(Path.Combine(javaRoot, "release"), "JAVA_VERSION=\"17.0.99\"\n");
            foreach (var toolName in new[] { "java.exe", "javac.exe", "keytool.exe" })
                File.WriteAllBytes(Path.Combine(javaRoot, "bin", toolName), [0]);

            File.WriteAllText(
                fakeDotnetPath,
                "@echo off\r\n" +
                "> \"%GEORAEPLAN_FAKE_DOTNET_CAPTURE%\" echo ARGS=%*\r\n" +
                "if defined GEORAEPLAN_TEST_ANDROID_STORE_SECRET (>> \"%GEORAEPLAN_FAKE_DOTNET_CAPTURE%\" echo STORE_PRESENT=1) else (>> \"%GEORAEPLAN_FAKE_DOTNET_CAPTURE%\" echo STORE_PRESENT=0)\r\n" +
                "if defined GEORAEPLAN_TEST_ANDROID_KEY_SECRET (>> \"%GEORAEPLAN_FAKE_DOTNET_CAPTURE%\" echo KEY_PRESENT=1) else (>> \"%GEORAEPLAN_FAKE_DOTNET_CAPTURE%\" echo KEY_PRESENT=0)\r\n" +
                "exit /b 17\r\n");
            File.WriteAllText(
                inlineConfigPath,
                $$"""
                {
                  "keystorePath": {{System.Text.Json.JsonSerializer.Serialize(keystorePath)}},
                  "keyAlias": "georaeplan",
                  "storePass": {{System.Text.Json.JsonSerializer.Serialize(storeSecret)}},
                  "keyPass": {{System.Text.Json.JsonSerializer.Serialize(keySecret)}}
                }
                """);
            File.WriteAllText(
                environmentConfigPath,
                $$"""
                {
                  "keystorePath": {{System.Text.Json.JsonSerializer.Serialize(keystorePath)}},
                  "keyAlias": "georaeplan",
                  "storePassEnvironmentVariable": "{{storeEnvironmentVariable}}",
                  "keyPassEnvironmentVariable": "{{keyEnvironmentVariable}}"
                }
                """);

            var commonArguments = new[]
            {
                "-ProjectRoot", fixtureRoot,
                "-ProjectFile", projectPath,
                "-DotNetPath", fakeDotnetPath,
                "-JavaSdkDirectory", javaRoot,
                "-AndroidSdkDirectory", androidRoot,
                "-OutputRoot", outputRoot,
                "-Configuration", "Release",
                "-SkipEnvironmentCheck",
                "-SkipDeploymentCopy",
                "-SkipArtifactPrune",
                "-DisableAot",
                "-NoRestore"
            };

            var inlineResult = await RunPowerShellAsync(
                scriptPath,
                [.. commonArguments, "-SigningConfigPath", inlineConfigPath],
                new Dictionary<string, string?>
                {
                    ["GEORAEPLAN_FAKE_DOTNET_CAPTURE"] = capturePath
                });
            var inlineOutput = inlineResult.StdOut + inlineResult.StdErr;
            Assert.NotEqual(0, inlineResult.ExitCode);
            Assert.Contains(
                "Production Android signing passwords must be supplied through storePassEnvironmentVariable/keyPassEnvironmentVariable",
                inlineOutput,
                StringComparison.Ordinal);
            Assert.DoesNotContain(storeSecret, inlineOutput, StringComparison.Ordinal);
            Assert.DoesNotContain(keySecret, inlineOutput, StringComparison.Ordinal);
            Assert.False(File.Exists(capturePath));

            var environmentResult = await RunPowerShellAsync(
                scriptPath,
                [.. commonArguments, "-SigningConfigPath", environmentConfigPath],
                new Dictionary<string, string?>
                {
                    ["GEORAEPLAN_FAKE_DOTNET_CAPTURE"] = capturePath,
                    [storeEnvironmentVariable] = storeSecret,
                    [keyEnvironmentVariable] = keySecret
                });
            var environmentOutput = environmentResult.StdOut + environmentResult.StdErr;
            Assert.NotEqual(0, environmentResult.ExitCode);
            Assert.Contains("dotnet publish failed with exit code 17", environmentOutput, StringComparison.Ordinal);
            Assert.DoesNotContain(storeSecret, environmentOutput, StringComparison.Ordinal);
            Assert.DoesNotContain(keySecret, environmentOutput, StringComparison.Ordinal);
            Assert.True(File.Exists(capturePath));

            var capture = File.ReadAllText(capturePath);
            Assert.Contains("STORE_PRESENT=0", capture, StringComparison.Ordinal);
            Assert.Contains("KEY_PRESENT=0", capture, StringComparison.Ordinal);
            Assert.Contains("AndroidSigningStorePass=file:", capture, StringComparison.Ordinal);
            Assert.Contains("AndroidSigningKeyPass=file:", capture, StringComparison.Ordinal);
            Assert.DoesNotContain(storeSecret, capture, StringComparison.Ordinal);
            Assert.DoesNotContain(keySecret, capture, StringComparison.Ordinal);

            var secretPaths = Regex.Matches(
                    capture,
                    @"AndroidSigning(?:Store|Key)Pass=file:(?<path>[^\s]+)",
                    RegexOptions.CultureInvariant)
                .Select(match => match.Groups["path"].Value.Trim('"'))
                .ToArray();
            Assert.Equal(2, secretPaths.Length);
            Assert.All(secretPaths, path => Assert.False(File.Exists(path), $"Secret file remained after failure: {path}"));
        }
        finally
        {
            if (Directory.Exists(fixtureRoot))
                Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    [Fact]
    public async Task AndroidKeystoreGenerationPassesOnlyEnvironmentVariableNamesToKeytool()
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(repositoryRoot, "tools", "mobile", "New-GeoraePlanAndroidKeystore.ps1");
        var fixtureRoot = Path.Combine(Path.GetTempPath(), $"georaeplan-android-keytool-input-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureRoot);

        const string storeSecret = "store-keytool-canary-7Z!";
        const string keySecret = "key-keytool-canary-8Y!";
        const string storeEnvironmentVariable = "GEORAEPLAN_TEST_KEYTOOL_STORE_SECRET";
        const string keyEnvironmentVariable = "GEORAEPLAN_TEST_KEYTOOL_KEY_SECRET";

        try
        {
            var fakeKeytoolPath = Path.Combine(fixtureRoot, "fake-keytool.cmd");
            var capturePath = Path.Combine(fixtureRoot, "fake-keytool-capture.txt");
            var outputPath = Path.Combine(fixtureRoot, "signing", "release.keystore");
            File.WriteAllText(
                fakeKeytoolPath,
                "@echo off\r\n" +
                "> \"%GEORAEPLAN_FAKE_KEYTOOL_CAPTURE%\" echo ARGS=%*\r\n" +
                "exit /b 0\r\n");

            var result = await RunPowerShellAsync(
                scriptPath,
                new[]
                {
                    "-ProjectRoot", fixtureRoot,
                    "-OutputPath", outputPath,
                    "-KeytoolPath", fakeKeytoolPath,
                    "-StorePassEnvironmentVariable", storeEnvironmentVariable,
                    "-KeyPassEnvironmentVariable", keyEnvironmentVariable
                },
                new Dictionary<string, string?>
                {
                    ["GEORAEPLAN_FAKE_KEYTOOL_CAPTURE"] = capturePath,
                    [storeEnvironmentVariable] = storeSecret,
                    [keyEnvironmentVariable] = keySecret
                });

            var output = result.StdOut + result.StdErr;
            Assert.Equal(0, result.ExitCode);
            Assert.DoesNotContain(storeSecret, output, StringComparison.Ordinal);
            Assert.DoesNotContain(keySecret, output, StringComparison.Ordinal);
            Assert.True(File.Exists(capturePath));
            var capture = File.ReadAllText(capturePath);
            Assert.Contains($"-storepass:env {storeEnvironmentVariable}", capture, StringComparison.Ordinal);
            Assert.Contains($"-keypass:env {keyEnvironmentVariable}", capture, StringComparison.Ordinal);
            Assert.DoesNotContain(storeSecret, capture, StringComparison.Ordinal);
            Assert.DoesNotContain(keySecret, capture, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(fixtureRoot))
                Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunPowerShellAsync(
        string scriptPath,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment)
    {
        var powershellPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = powershellPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        foreach (var pair in environment)
            startInfo.Environment[pair.Key] = pair.Value;

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("PowerShell did not start.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdoutTask, await stderrTask);
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
