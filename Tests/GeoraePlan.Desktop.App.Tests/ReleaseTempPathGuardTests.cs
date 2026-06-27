using System.Reflection;
using System.Diagnostics;
using 거래플랜.Desktop.App.Services;
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
    public void DesktopUpdater_VerifiesManifestFileSizeBeforeMarkingPackageReadyOrApplyingIt()
    {
        var serviceSource = ReadRepositoryFile(
            "Desktop",
            "거래플랜.Desktop.App",
            "Services",
            "DesktopAppUpdateService.cs");
        var updaterSource = ReadRepositoryFile(
            "Updater",
            "거래플랜.Updater",
            "Program.cs");

        Assert.Contains("VerifyExpectedPackageFileSize(targetPath, package.FileSize);", serviceSource, StringComparison.Ordinal);
        Assert.Contains("VerifyExpectedPackageFileSize(temporaryPath, package.FileSize);", serviceSource, StringComparison.Ordinal);
        Assert.Contains("private static void VerifyExpectedPackageFileSize(string filePath, long expectedFileSize)", serviceSource, StringComparison.Ordinal);
        AssertInOrder(
            serviceSource,
            "await VerifySha256Async(temporaryPath, package.Sha256, ct);",
            "VerifyExpectedPackageFileSize(temporaryPath, package.FileSize);",
            "File.Move(temporaryPath, targetPath, overwrite: true);");
        AssertInOrder(
            serviceSource,
            "if (File.Exists(targetPath) && await TryVerifySha256Async(targetPath, package.Sha256, ct))",
            "VerifyExpectedPackageFileSize(targetPath, package.FileSize);",
            "return new DesktopPreparedUpdatePackage");

        Assert.Contains("VerifyExpectedPackageFileSize(packagePath, options.FileSize);", updaterSource, StringComparison.Ordinal);
        Assert.Contains("private static void VerifyExpectedPackageFileSize(string filePath, long expectedFileSize)", updaterSource, StringComparison.Ordinal);
        AssertInOrder(
            updaterSource,
            "await VerifySha256Async(packagePath, options.Sha256);",
            "VerifyExpectedPackageFileSize(packagePath, options.FileSize);",
            "await WaitForProcessExitAsync(options.ProcessId);");
    }

    [Fact]
    public void UpdateAssetPublisher_WritesAtomicManifestAndPreservesReferencedDownloads()
    {
        var source = ReadRepositoryFile(
            "tools",
            "release",
            "Publish-GeoraePlanUpdateAssets.ps1");

        Assert.Contains("$tempInitializer = Join-Path $ProjectRoot 'tools\\common\\Initialize-GeoraePlanTemp.ps1'", source, StringComparison.Ordinal);
        Assert.Contains(". $tempInitializer -ProjectRoot $ProjectRoot", source, StringComparison.Ordinal);
        Assert.Contains("function Write-JsonFileAtomically", source, StringComparison.Ordinal);
        Assert.Contains("[System.IO.File]::Replace($tempPath, $TargetPath, $backupPath, $true)", source, StringComparison.Ordinal);
        Assert.Contains("Move-Item -LiteralPath $tempPath -Destination $TargetPath -Force", source, StringComparison.Ordinal);
        Assert.Contains("Write-JsonFileAtomically -TargetPath $manifestPath -InputObject $manifest", source, StringComparison.Ordinal);

        Assert.Contains("Test-DesktopUpdatePackage -PackagePath $SourcePath -ExpectedVersion $Version", source, StringComparison.Ordinal);
        Assert.Contains("'App/Updater/거래플랜.Updater.exe'", source, StringComparison.Ordinal);
        Assert.Contains("'App/appsettings.json'", source, StringComparison.Ordinal);
        Assert.Contains("sha256 = $hash.Hash", source, StringComparison.Ordinal);
        Assert.Contains("fileSize = [int64]$fileInfo.Length", source, StringComparison.Ordinal);

        Assert.Contains("$preservedDesktopFiles = Get-ManifestReferencedFileNames -ManifestRoot $manifestRoot -Platform 'desktop'", source, StringComparison.Ordinal);
        Assert.Contains("$preservedAndroidFiles = Get-ManifestReferencedFileNames -ManifestRoot $manifestRoot -Platform 'android'", source, StringComparison.Ordinal);
        Assert.Contains("-PreserveFileNames $preservedDesktopFiles", source, StringComparison.Ordinal);
        Assert.Contains("-PreserveFileNames $preservedAndroidFiles", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "Write-JsonFileAtomically -TargetPath $manifestPath -InputObject $manifest",
            "$preservedDesktopFiles = Get-ManifestReferencedFileNames -ManifestRoot $manifestRoot -Platform 'desktop'",
            "$removedDesktopPackages = Remove-OldPackages");
    }

    [Fact]
    public void DesktopUpdaterFailureWindow_ProvidesCopyableLogDiagnosticsAndStaysOpen()
    {
        var windowSource = ReadRepositoryFile(
            "Updater",
            "거래플랜.Updater",
            "UpdateProgressWindow.cs");
        var programSource = ReadRepositoryFile(
            "Updater",
            "거래플랜.Updater",
            "Program.cs");

        Assert.Contains("public void ShowFailure(string title, string detail, string? logPath = null)", windowSource, StringComparison.Ordinal);
        Assert.Contains("CreateActionButton(\"로그 복사\")", windowSource, StringComparison.Ordinal);
        Assert.Contains("CreateActionButton(\"로그 위치 열기\")", windowSource, StringComparison.Ordinal);
        Assert.Contains("CreateActionButton(\"닫기\")", windowSource, StringComparison.Ordinal);
        Assert.Contains("SetClipboardTextWithRetry(content.ToString())", windowSource, StringComparison.Ordinal);
        Assert.Contains("private static void SetClipboardTextWithRetry(string text)", windowSource, StringComparison.Ordinal);
        Assert.Contains("CanOpenClipboardForProbe()", windowSource, StringComparison.Ordinal);
        Assert.Contains("File.ReadAllText(_failureLogPath!, Encoding.UTF8)", windowSource, StringComparison.Ordinal);
        Assert.Contains("FileName = \"explorer.exe\"", windowSource, StringComparison.Ordinal);
        Assert.Contains("Arguments = \"/select,\" + QuoteExplorerArgument(_failureLogPath!)", windowSource, StringComparison.Ordinal);
        Assert.Contains("app.ShutdownMode = ShutdownMode.OnExplicitShutdown;", programSource, StringComparison.Ordinal);
        Assert.Contains("window.Closed += (_, _) => app.Shutdown(1);", programSource, StringComparison.Ordinal);
        Assert.Contains("window.ShowFailure(\"업데이트를 완료하지 못했습니다.\", message, _sessionLogPath);", programSource, StringComparison.Ordinal);
        Assert.DoesNotContain("MessageBox.Show", programSource, StringComparison.Ordinal);
        Assert.DoesNotContain("MessageBoxButton.OK", programSource, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopUpdater_CreatesRequestMetadataOnlyForDownloadPathAndDeletesItWhenLaunchFails()
    {
        var source = ReadRepositoryFile(
            "Desktop",
            "거래플랜.Desktop.App",
            "Services",
            "DesktopAppUpdateService.cs");

        Assert.Contains("var requestMetadataPath = string.IsNullOrWhiteSpace(preparedPackageFullPath)", source, StringComparison.Ordinal);
        Assert.Contains("? CreateUpdaterRequestMetadataFile(stagedUpdaterPath, packageUri)", source, StringComparison.Ordinal);
        Assert.Contains(": null;", source, StringComparison.Ordinal);
        Assert.Contains("TryDeleteSensitiveFile(requestMetadataPath);", source, StringComparison.Ordinal);
        var startUpdateStart = source.IndexOf("public void StartUpdate", StringComparison.Ordinal);
        var startUpdateEnd = source.IndexOf("private static string? ValidatePreparedPackagePath", StringComparison.Ordinal);
        Assert.True(startUpdateStart >= 0 && startUpdateEnd > startUpdateStart);
        var startUpdateSource = source[startUpdateStart..startUpdateEnd];
        AssertInOrder(
            startUpdateSource,
            "var preparedPackageFullPath = ValidatePreparedPackagePath(preparedPackagePath, package);",
            "var stagedUpdaterPath = StageUpdaterForExecution(updaterPath);",
            "EnsureSufficientDiskSpace(package.FileSize, installRoot);",
            "TryCleanupStaleUpdateArtifacts();",
            "var requestMetadataPath = string.IsNullOrWhiteSpace(preparedPackageFullPath)",
            "Process.Start(new ProcessStartInfo",
            "TryDeleteSensitiveFile(requestMetadataPath);");
    }

    [Fact]
    public void DesktopUpdater_RequestMetadataFileDoesNotPersistPlaintextAuthorization()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var tempRoot = Path.Combine(
            FindRepositoryRoot(),
            "temp",
            "metadata-acl-tests",
            Guid.NewGuid().ToString("N"));
        var metadataPath = Path.Combine(tempRoot, "request-metadata.json");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var method = typeof(DesktopAppUpdateService).GetMethod(
                "WriteSensitiveUpdaterMetadataFile",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);
            var protectMethod = typeof(DesktopAppUpdateService).GetMethod(
                "ProtectUpdaterMetadataValue",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(protectMethod);

            var protectedValue = Assert.IsType<string>(protectMethod!.Invoke(null, ["Bearer secret-token"]));
            Assert.False(string.IsNullOrWhiteSpace(protectedValue));
            Assert.DoesNotContain("secret-token", protectedValue, StringComparison.Ordinal);

            method!.Invoke(null, [metadataPath, $"{{\"ProtectedHeaders\":{{\"Authorization\":\"{protectedValue}\"}}}}"]);

            Assert.True(File.Exists(metadataPath));
            var json = File.ReadAllText(metadataPath);
            Assert.DoesNotContain("secret-token", json, StringComparison.Ordinal);
            Assert.DoesNotContain("Bearer", json, StringComparison.Ordinal);
            Assert.Contains("ProtectedHeaders", json, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void DesktopAndUpdater_ShareDpapiProtectedRequestMetadataContract()
    {
        var desktopSource = ReadRepositoryFile(
            "Desktop",
            "거래플랜.Desktop.App",
            "Services",
            "DesktopAppUpdateService.cs");
        var updaterSource = ReadRepositoryFile(
            "Updater",
            "거래플랜.Updater",
            "Program.cs");

        Assert.Contains("ProtectedHeaders = headers.ToDictionary", desktopSource, StringComparison.Ordinal);
        Assert.Contains("ProtectedData.Protect", desktopSource, StringComparison.Ordinal);
        Assert.Contains("DataProtectionScope.CurrentUser", desktopSource, StringComparison.Ordinal);
        Assert.Contains("CryptographicOperations.ZeroMemory(plainBytes);", desktopSource, StringComparison.Ordinal);
        Assert.DoesNotContain("new UpdaterRequestMetadata\r\n        {\r\n            Headers =", desktopSource, StringComparison.Ordinal);
        Assert.DoesNotContain("new UpdaterRequestMetadata\n        {\n            Headers =", desktopSource, StringComparison.Ordinal);

        Assert.Contains("public Dictionary<string, string> Headers", updaterSource, StringComparison.Ordinal);
        Assert.Contains("public Dictionary<string, string> ProtectedHeaders", updaterSource, StringComparison.Ordinal);
        Assert.Contains("ProtectedData.Unprotect", updaterSource, StringComparison.Ordinal);
        Assert.Contains("DataProtectionScope.CurrentUser", updaterSource, StringComparison.Ordinal);
        Assert.Contains("CryptographicOperations.ZeroMemory(plainBytes);", updaterSource, StringComparison.Ordinal);
        AssertInOrder(
            updaterSource,
            "foreach (var header in Headers)",
            "ApplyHeader(request, header.Key, header.Value);",
            "foreach (var header in ProtectedHeaders)",
            "ApplyHeader(request, header.Key, UnprotectMetadataValue(header.Value));");
    }

    [Fact]
    public void DesktopUpdater_AcceptsOnlySameAuthorityDesktopZipDownloadPackageUri()
    {
        var baseUri = new Uri("https://trade.example.com");

        var accepted = InvokeValidatePackageUri(
            "https://trade.example.com/updates/download/desktop/tradeplan-pc-installer-v1.1.552.zip",
            baseUri);

        Assert.Equal("https://trade.example.com/updates/download/desktop/tradeplan-pc-installer-v1.1.552.zip", accepted.ToString());

        AssertValidatePackageUriRejected(
            "https://trade.example.com:444/updates/download/desktop/tradeplan-pc-installer-v1.1.552.zip",
            baseUri);
        AssertValidatePackageUriRejected(
            "https://trade.example.com/updates/download/android/tradeplan-android-v0.2.65.apk",
            baseUri);
        AssertValidatePackageUriRejected(
            "https://trade.example.com/updates/download/desktop/%2e%2e%2ftradeplan-pc-installer-v1.1.552.zip",
            baseUri);
        AssertValidatePackageUriRejected(
            "https://trade.example.com/updates/download/desktop/tradeplan-pc-installer-v1.1.552.exe",
            baseUri);
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
        Assert.Contains("function Resolve-GeoraePlanScriptTempDirectory", updateAssetsSource, StringComparison.Ordinal);
        Assert.Contains("@($env:GEORAEPLAN_TEMP_ROOT, $env:TEMP, [System.IO.Path]::GetTempPath())", updateAssetsSource, StringComparison.Ordinal);
        Assert.Contains("$tempDirectory = Join-Path (Resolve-GeoraePlanScriptTempDirectory)", updateAssetsSource, StringComparison.Ordinal);

        var desktopInstallerSource = ReadRepositoryFile(
            "tools",
            "release",
            "Build-GeoraePlanDesktopNativeInstallers.ps1");

        Assert.Contains("Environment.SetEnvironmentVariable(\"TEMP\", resolvedPath);", desktopInstallerSource, StringComparison.Ordinal);
        Assert.Contains("Environment.SetEnvironmentVariable(\"TMP\", resolvedPath);", desktopInstallerSource, StringComparison.Ordinal);
        AssertInOrder(
            desktopInstallerSource,
            "if ([string]::IsNullOrWhiteSpace($ProjectRoot))",
            "$ProjectRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path",
            "$tempInitializer = Join-Path $ProjectRoot 'tools\\common\\Initialize-GeoraePlanTemp.ps1'",
            "$stagingRoot = Join-Path ([System.IO.Path]::GetPathRoot($ProjectRoot)) 'GeoraePlanInstallerBuild'");
        AssertInOrder(
            desktopInstallerSource,
            "Environment.GetEnvironmentVariable(TempRootOverrideEnvironmentKey)",
            "Path.Combine(\"D:\\\\\", \"거래플랜\", \"temp\")",
            "Path.GetTempPath()");

        var desktopZipInstallerSource = ReadRepositoryFile(
            "tools",
            "release",
            "Build-GeoraePlanDesktopInstaller.ps1");

        AssertInOrder(
            desktopZipInstallerSource,
            "if ([string]::IsNullOrWhiteSpace($ProjectRoot))",
            "$ProjectRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path",
            "$tempInitializer = Join-Path $ProjectRoot 'tools\\common\\Initialize-GeoraePlanTemp.ps1'",
            "& powershell -ExecutionPolicy Bypass -File $nativeInstallerScript -ProjectRoot $ProjectRoot");

        var androidBuildSource = ReadRepositoryFile(
            "tools",
            "mobile",
            "Build-GeoraePlanAndroidApk.ps1");

        Assert.Contains("Initialize-GeoraePlanTemp.ps1", androidBuildSource, StringComparison.Ordinal);
        Assert.Contains(". $tempInitializer -ProjectRoot $ProjectRoot", androidBuildSource, StringComparison.Ordinal);
        AssertInOrder(
            androidBuildSource,
            "$ProjectRoot = Resolve-DefaultProjectRoot -ScriptPath $MyInvocation.MyCommand.Path",
            "$tempInitializer = Join-Path $ProjectRoot 'tools\\common\\Initialize-GeoraePlanTemp.ps1'",
            "$resolvedDotNetPath = Get-ResolvedDotNetPath -ProjectRoot $ProjectRoot -RequestedPath $DotNetPath",
            "$publishResult = Invoke-DotnetPublishAndRelay -DotNetPath $resolvedDotNetPath -Arguments $arguments");

        var fullReleaseSource = ReadRepositoryFile(
            "tools",
            "release",
            "Publish-GeoraePlanFullRelease.ps1");

        AssertInOrder(
            fullReleaseSource,
            "if ([string]::IsNullOrWhiteSpace($ProjectRoot))",
            "$ProjectRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path",
            "$tempInitializer = Join-Path $ProjectRoot 'tools\\common\\Initialize-GeoraePlanTemp.ps1'",
            "& powershell -NoProfile -ExecutionPolicy Bypass -File $desktopScript -ProjectRoot $ProjectRoot");

        var linuxReleaseSource = ReadRepositoryFile(
            "tools",
            "linux",
            "Publish-GeoraeplanLinuxPcRelease.ps1");

        Assert.Contains("function Resolve-GeoraePlanScriptTempDirectory", linuxReleaseSource, StringComparison.Ordinal);
        Assert.Contains("@($env:GEORAEPLAN_TEMP_ROOT, $env:TEMP, [System.IO.Path]::GetTempPath())", linuxReleaseSource, StringComparison.Ordinal);
        Assert.Contains("$archiveDirectory = Resolve-GeoraePlanScriptTempDirectory", linuxReleaseSource, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DesktopInstallerBuild_FailsFastWhenUpdaterPublishFails()
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Build-GeoraePlanDesktopInstaller.ps1");
        var testRoot = Path.Combine(
            repositoryRoot,
            "temp",
            "release-failfast-tests",
            Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(testRoot);
            Directory.CreateDirectory(Path.Combine(testRoot, "deploy"));
            File.WriteAllText(Path.Combine(testRoot, "deploy", "Set-ApiBaseUrl.ps1"), "# test deployment marker");

            var desktopProjectDirectory = Path.Combine(testRoot, "Desktop", "거래플랜.Desktop.App");
            Directory.CreateDirectory(desktopProjectDirectory);
            File.WriteAllText(
                Path.Combine(desktopProjectDirectory, "거래플랜.Desktop.App.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <Version>9.9.999</Version>
                  </PropertyGroup>
                </Project>
                """);

            var updaterProjectDirectory = Path.Combine(testRoot, "Updater", "거래플랜.Updater");
            Directory.CreateDirectory(updaterProjectDirectory);
            File.WriteAllText(
                Path.Combine(updaterProjectDirectory, "거래플랜.Updater.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\" />");

            var sourceFolder = Path.Combine(testRoot, "source");
            Directory.CreateDirectory(sourceFolder);
            File.WriteAllText(
                Path.Combine(sourceFolder, "appsettings.json"),
                "{\"Api\":{\"BaseUrl\":\"https://example.invalid\"}}");
            File.WriteAllText(Path.Combine(sourceFolder, "거래플랜.exe"), "fake desktop exe");

            var fakeDotnetPath = Path.Combine(testRoot, "fake-dotnet.cmd");
            File.WriteAllText(
                fakeDotnetPath,
                """
                @echo off
                if "%~1"=="--version" (
                  echo 8.0.100
                  exit /b 0
                )
                echo %*>>"%~dp0dotnet-args.txt"
                exit /b 37
                """);

            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "powershell",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            process.StartInfo.ArgumentList.Add("-NoProfile");
            process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
            process.StartInfo.ArgumentList.Add("Bypass");
            process.StartInfo.ArgumentList.Add("-File");
            process.StartInfo.ArgumentList.Add(scriptPath);
            process.StartInfo.ArgumentList.Add("-ProjectRoot");
            process.StartInfo.ArgumentList.Add(testRoot);
            process.StartInfo.ArgumentList.Add("-SourceFolder");
            process.StartInfo.ArgumentList.Add(sourceFolder);
            process.StartInfo.ArgumentList.Add("-OutputRoot");
            process.StartInfo.ArgumentList.Add(Path.Combine(testRoot, "output"));
            process.StartInfo.ArgumentList.Add("-SkipNativeInstallers");
            process.StartInfo.Environment["DOTNET_EXE"] = fakeDotnetPath;
            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            var exited = process.WaitForExit(60_000);
            Assert.True(exited, "Desktop installer build fail-fast test timed out.");

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            Assert.NotEqual(0, process.ExitCode);
            Assert.Contains(
                "Failed to publish updater for desktop package.",
                stdout + stderr,
                StringComparison.Ordinal);
            Assert.True(
                File.Exists(Path.Combine(testRoot, "dotnet-args.txt")),
                "Fake dotnet should have been invoked for updater publish.");
            Assert.False(
                File.Exists(Path.Combine(testRoot, "output", "관리자용", "거래플랜-PC-설치패키지.zip")),
                "Installer package must not be created when updater publish fails.");
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task DesktopInstallerScript_RestoresPreviousInstallRootWhenValidationFailsAfterCopy()
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repositoryRoot,
            "tools",
            "release",
            "Build-GeoraePlanDesktopInstaller.ps1");
        var testRoot = Path.Combine(
            repositoryRoot,
            "temp",
            "install-rollback-tests",
            Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(testRoot);
            Directory.CreateDirectory(Path.Combine(testRoot, "deploy"));
            File.WriteAllText(Path.Combine(testRoot, "deploy", "Set-ApiBaseUrl.ps1"), "# test deployment marker");

            var desktopProjectDirectory = Path.Combine(testRoot, "Desktop", "거래플랜.Desktop.App");
            Directory.CreateDirectory(desktopProjectDirectory);
            File.WriteAllText(
                Path.Combine(desktopProjectDirectory, "거래플랜.Desktop.App.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <Version>9.9.999</Version>
                  </PropertyGroup>
                </Project>
                """);

            var sourceFolder = Path.Combine(testRoot, "source");
            Directory.CreateDirectory(sourceFolder);
            File.WriteAllText(
                Path.Combine(sourceFolder, "appsettings.json"),
                "{\"Api\":{\"BaseUrl\":\"https://example.invalid/new\"}}");
            File.WriteAllText(Path.Combine(sourceFolder, "거래플랜.exe"), "new invalid executable without version");
            File.WriteAllText(Path.Combine(sourceFolder, "new-only.txt"), "new file that must disappear after rollback");

            var fakeDotnetPath = Path.Combine(testRoot, "fake-dotnet.cmd");
            File.WriteAllText(
                fakeDotnetPath,
                """
                @echo off
                if "%~1"=="--version" (
                  echo 8.0.100
                  exit /b 0
                )
                exit /b 0
                """);

            var buildResult = await RunPowerShellAsync(
                scriptPath,
                ("-ProjectRoot", testRoot),
                ("-SourceFolder", sourceFolder),
                ("-OutputRoot", Path.Combine(testRoot, "output")),
                ("-SkipNativeInstallers", null),
                ("DOTNET_EXE", fakeDotnetPath));
            Assert.Equal(0, buildResult.ExitCode);

            var packageRoot = Path.Combine(testRoot, "output", "관리자용", "거래플랜-PC-설치패키지");
            var installScriptPath = Path.Combine(packageRoot, "Install-GeoraePlan.ps1");
            Assert.True(File.Exists(installScriptPath), "Generated install script was not found.");

            var installRoot = Path.Combine(testRoot, "install-root");
            Directory.CreateDirectory(installRoot);
            File.WriteAllText(Path.Combine(installRoot, "거래플랜.exe"), "old executable content");
            File.WriteAllText(Path.Combine(installRoot, "appsettings.json"), "{\"Api\":{\"BaseUrl\":\"https://example.invalid/old\"}}");
            File.WriteAllText(Path.Combine(installRoot, "old-only.txt"), "old file that must remain after rollback");

            var installResult = await RunPowerShellAsync(
                installScriptPath,
                ("-InstallRoot", installRoot),
                ("-NoLaunch", null),
                ("-NoShortcuts", null),
                ("-SuppressUi", null),
                ("-LogPath", Path.Combine(testRoot, "install.log")),
                ("DOTNET_EXE", fakeDotnetPath));

            Assert.NotEqual(0, installResult.ExitCode);
            Assert.Contains("설치된 실행 파일 버전을 확인하지 못했습니다.", installResult.StdOut + installResult.StdErr);
            Assert.Equal("old executable content", File.ReadAllText(Path.Combine(installRoot, "거래플랜.exe")));
            Assert.Equal("{\"Api\":{\"BaseUrl\":\"https://example.invalid/old\"}}", File.ReadAllText(Path.Combine(installRoot, "appsettings.json")));
            Assert.True(File.Exists(Path.Combine(installRoot, "old-only.txt")));
            Assert.False(File.Exists(Path.Combine(installRoot, "new-only.txt")));
            Assert.Empty(Directory.EnumerateDirectories(testRoot, ".tradeplan-install-rollback-*", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
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

    [Fact]
    public void RentalTemplateCandidateExportScript_IsSelectOnlyAndRedactsSensitiveRowsByDefault()
    {
        var source = ReadRepositoryFile(
            "tools",
            "linux",
            "Export-GeoraePlanRentalTemplateItemReferenceCandidates.ps1");

        Assert.Contains("[switch]$IncludeSensitiveCandidateRows", source, StringComparison.Ordinal);
        Assert.Contains("copy (", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(") to stdout with csv header;", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("artifacts\\rental-template-item-reference-candidates", source, StringComparison.Ordinal);
        Assert.Contains("'' as \"ProfileKey\"", source, StringComparison.Ordinal);
        Assert.Contains("'' as \"CustomerName\"", source, StringComparison.Ordinal);
        Assert.Contains("'' as \"DisplayItemName\"", source, StringComparison.Ordinal);
        Assert.Contains("'' as \"OriginalItemId\"", source, StringComparison.Ordinal);
        Assert.Contains("single_active_item_from_included_assets", source, StringComparison.Ordinal);
        Assert.Contains("ambiguous_multiple_candidates", source, StringComparison.Ordinal);
        Assert.Contains("proposed_item_id as \"ProposedItemId\"", source, StringComparison.Ordinal);
        Assert.Contains("proposed_source as \"ProposedSource\"", source, StringComparison.Ordinal);
        Assert.Contains("proposed_confidence as \"ProposedConfidence\"", source, StringComparison.Ordinal);
        Assert.Contains("ProposedItemCount", source, StringComparison.Ordinal);
        Assert.Contains("review_required_asset_based", source, StringComparison.Ordinal);
        Assert.Contains("At least one database name is required.", source, StringComparison.Ordinal);
        Assert.Contains("([string]$_) -split ','", source, StringComparison.Ordinal);
        Assert.Contains("function Get-ManualReviewDetailSql", source, StringComparison.Ordinal);
        Assert.Contains("manual-review-candidate-details.csv", source, StringComparison.Ordinal);
        Assert.Contains("manual-review-candidate-detail-summary.csv", source, StringComparison.Ordinal);
        Assert.Contains("name_or_identifier_candidate", source, StringComparison.Ordinal);
        Assert.Contains("included_asset_item_candidate", source, StringComparison.Ordinal);
        Assert.Contains("'' as \"CandidateItemName\"", source, StringComparison.Ordinal);
        Assert.Contains("CandidateStatus", source, StringComparison.Ordinal);
        Assert.Contains("DistinctCandidateItemCount", source, StringComparison.Ordinal);

        Assert.DoesNotContain("docker compose down", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("docker system prune", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("docker restart", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("systemctl restart", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reboot", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("delete from", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("update \"", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("insert into", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("truncate", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("drop table", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alter table", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RentalTemplateItemReferenceGate_BlocksUnresolvedCandidatesWithReadOnlyExport()
    {
        var source = ReadRepositoryFile(
            "tools",
            "linux",
            "Test-GeoraePlanRentalTemplateItemReferenceGate.ps1");

        Assert.Contains("Export-GeoraePlanRentalTemplateItemReferenceCandidates.ps1", source, StringComparison.Ordinal);
        Assert.Contains("rental-template-item-reference-gate.md", source, StringComparison.Ordinal);
        Assert.Contains("summary-by-database.csv", source, StringComparison.Ordinal);
        Assert.Contains("manual-review-candidate-detail-summary.csv", source, StringComparison.Ordinal);
        Assert.Contains("rental_template_item_reference_gate_status=$status", source, StringComparison.Ordinal);
        Assert.Contains("Unresolved rental billing template item references remain", source, StringComparison.Ordinal);
        Assert.Contains("'-Databases', ($Databases -join ',')", source, StringComparison.Ordinal);
        Assert.Contains("AllowUnresolved", source, StringComparison.Ordinal);

        Assert.DoesNotContain("delete from", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("update \"", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("insert into", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("drop table", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("truncate", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("docker compose down", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("docker restart", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("systemctl restart", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reboot", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LinuxPcReleaseRunsRentalTemplateItemReferenceGateWithOperationalGate()
    {
        var source = ReadRepositoryFile(
            "tools",
            "linux",
            "Publish-GeoraeplanLinuxPcRelease.ps1");

        Assert.Contains("Test-GeoraePlanRentalTemplateItemReferenceGate.ps1", source, StringComparison.Ordinal);
        Assert.Contains("rental-template-item-reference-gate", source, StringComparison.Ordinal);
        Assert.Contains("function Invoke-RentalTemplateItemReferenceGate", source, StringComparison.Ordinal);
        Assert.Contains("pre-deploy-required-data", source, StringComparison.Ordinal);
        Assert.Contains("_rental_template_item_reference_gate_start", source, StringComparison.Ordinal);
        Assert.Contains("_rental_template_item_reference_gate_done", source, StringComparison.Ordinal);
        Assert.Contains("[switch]$AcceptRentalTemplateItemReferenceRisk", source, StringComparison.Ordinal);
        Assert.Contains("pre-deploy-required-data_rental_template_item_reference_gate=skipped risk=accepted", source, StringComparison.Ordinal);
        Assert.Contains("known operating data candidates are intentionally excluded", source, StringComparison.Ordinal);
        Assert.Contains("'-RemoteOpsDirectory', $script:LinuxRemoteOpsPath", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "$rentalTemplateItemReferenceGateScript = Join-Path $Root 'tools\\linux\\Test-GeoraePlanRentalTemplateItemReferenceGate.ps1'",
            "& powershell @rentalTemplateItemReferenceGateArgs");
        AssertInOrder(
            source,
            "function Invoke-RentalTemplateItemReferenceGate",
            "function Update-PublishedAppSettings");
        AssertInOrder(
            source,
            "$resolvedPreDeploySecretPath =",
            "if ($MirrorToLive -and -not $AcceptRentalTemplateItemReferenceRisk.IsPresent) {",
            "Invoke-RentalTemplateItemReferenceGate `",
            "elseif ($MirrorToLive -and $AcceptRentalTemplateItemReferenceRisk.IsPresent) {",
            "if ($MirrorToLive -and -not $SkipPreDeployOperationalGate.IsPresent)");
    }

    [Fact]
    public void LinuxPcReleaseSshCommandPreservesRemoteCommandQuotingAndFailsPrunePipelines()
    {
        var source = ReadRepositoryFile(
            "tools",
            "linux",
            "Publish-GeoraeplanLinuxPcRelease.ps1");

        Assert.Contains("function Invoke-SshCommand", source, StringComparison.Ordinal);
        Assert.Contains("[System.Diagnostics.ProcessStartInfo]::new($sshExe)", source, StringComparison.Ordinal);
        Assert.Contains("$startInfo.Arguments = ($arguments | ForEach-Object { Quote-ProcessArgument -Argument $_ }) -join ' '", source, StringComparison.Ordinal);
        Assert.Contains("$startInfo.RedirectStandardOutput = $true", source, StringComparison.Ordinal);
        Assert.Contains("$process.Start()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Start-Process -FilePath $sshExe -ArgumentList $arguments", source, StringComparison.Ordinal);
        Assert.DoesNotContain("$startInfo.ArgumentList.Add($argument)", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "function Invoke-SshCommand",
            "[System.Diagnostics.ProcessStartInfo]::new($sshExe)",
            "$startInfo.Arguments = ($arguments | ForEach-Object { Quote-ProcessArgument -Argument $_ }) -join ' '",
            "$process.Start()");

        Assert.Contains("set -o pipefail", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "set -e",
            "set -o pipefail",
            "find \"`$real_root\" -mindepth 1 -maxdepth 1 -type d -name \"`$pattern\"");
    }

    [Fact]
    public void LinuxPcReleaseChecksDiskFreeSpaceAfterPruneBeforeUpload()
    {
        var source = ReadRepositoryFile(
            "tools",
            "linux",
            "Publish-GeoraeplanLinuxPcRelease.ps1");

        Assert.Contains("[int64]$MinimumLinuxFreeBytes", source, StringComparison.Ordinal);
        Assert.Contains("function Invoke-LinuxPcDiskPreflight", source, StringComparison.Ordinal);
        Assert.Contains("$minimumFreeKilobytes = [int64][Math]::Ceiling($MinimumFreeBytes / 1024.0)", source, StringComparison.Ordinal);
        Assert.Contains("df -Pk \"`$path\"", source, StringComparison.Ordinal);
        Assert.Contains("minimum_kb=$minimumFreeKilobytes", source, StringComparison.Ordinal);
        Assert.Contains("if [ \"`$available_kb\" -lt \"`$minimum_kb\" ]; then", source, StringComparison.Ordinal);
        Assert.Contains("linux_pc_disk_preflight_ok", source, StringComparison.Ordinal);
        Assert.Contains("Linux PC free disk space is below the required threshold", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "Invoke-LinuxPcRemotePrune -Config $linuxConfig -RelativePath 'app/backups'",
            "Invoke-LinuxPcRemotePrune -Config $linuxConfig -RelativePath 'releases'",
            "Invoke-LinuxPcDiskPreflight -Config $linuxConfig -Path $linuxConfig.RemoteRoot -MinimumFreeBytes $MinimumLinuxFreeBytes -Label 'pre-upload'",
            "Write-Host \"linux_pc_upload_start");
    }

    [Fact]
    public void PreLiveVerificationUsesLinuxPcUpdateManifestStepLabels()
    {
        var source = ReadRepositoryFile(
            "tools",
            "verification",
            "Invoke-GeoraePlanPreLiveVerification.ps1");

        Assert.Contains("function Invoke-LinuxPcUpdateManifestCheck", source, StringComparison.Ordinal);
        Assert.Contains("SkipLinuxPcUpdateManifestCheck", source, StringComparison.Ordinal);
        Assert.Contains("Invoke-Step -Name 'linux-pc-update-manifest-check'", source, StringComparison.Ordinal);
        Assert.Contains("Add-StepResult -Name 'linux-pc-update-manifest-check' -Passed $true -Detail 'SKIP'", source, StringComparison.Ordinal);
        Assert.Contains("Linux PC update manifest 확인", source, StringComparison.Ordinal);
        Assert.DoesNotContain("nas-update-manifest-check", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LiveObservationChecksBothDesktopAndAndroidPackagesFromManifest()
    {
        var source = ReadRepositoryFile(
            "테스트 시행",
            "Invoke-LiveObservationCheck.ps1");

        Assert.Contains("$desktopPackageUrl = [string]$manifest.desktop.packageUrl", source, StringComparison.Ordinal);
        Assert.Contains("$androidPackageUrl = [string]$manifest.android.packageUrl", source, StringComparison.Ordinal);
        Assert.Contains("$desktopPackageResult = if ($SkipPackageProbe)", source, StringComparison.Ordinal);
        Assert.Contains("$androidPackageResult = if ($SkipPackageProbe)", source, StringComparison.Ordinal);
        Assert.Contains("Test-PackageProbe -BaseUrl $resolvedBaseUrl -PackageUrl $desktopPackageUrl", source, StringComparison.Ordinal);
        Assert.Contains("Test-PackageProbe -BaseUrl $resolvedBaseUrl -PackageUrl $androidPackageUrl", source, StringComparison.Ordinal);
        Assert.Contains("DesktopPackageOk", source, StringComparison.Ordinal);
        Assert.Contains("AndroidPackageOk", source, StringComparison.Ordinal);
        Assert.Contains("-not $_.DesktopPackageOk -or -not $_.AndroidPackageOk", source, StringComparison.Ordinal);
        Assert.Contains("desktop package | android package", source, StringComparison.Ordinal);
        Assert.Contains("desktop/android package 다운로드 경로", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PackageOk = $packageResult.Success", source, StringComparison.Ordinal);
        Assert.DoesNotContain("-not $_.PackageOk", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveObservationReportsAndroidApkSigningCertificateAndDebugRisk()
    {
        var source = ReadRepositoryFile(
            "테스트 시행",
            "Invoke-LiveObservationCheck.ps1");

        Assert.Contains("[switch]$SkipAndroidSigningProbe", source, StringComparison.Ordinal);
        Assert.Contains("[switch]$FailOnAndroidDebugSigning", source, StringComparison.Ordinal);
        Assert.Contains("function Resolve-ApkSignerPath", source, StringComparison.Ordinal);
        Assert.Contains("function Resolve-JavaHomeForApkSigner", source, StringComparison.Ordinal);
        Assert.Contains("function Test-AndroidApkSigningProbe", source, StringComparison.Ordinal);
        Assert.Contains("$env:JAVA_HOME = $javaHome", source, StringComparison.Ordinal);
        Assert.Contains("$env:JAVA_HOME = $previousJavaHome", source, StringComparison.Ordinal);
        Assert.Contains("verify --print-certs", source, StringComparison.Ordinal);
        Assert.Contains("Signer\\s+#1\\s+certificate\\s+DN", source, StringComparison.Ordinal);
        Assert.Contains("Signer\\s+#1\\s+certificate\\s+SHA-256\\s+digest", source, StringComparison.Ordinal);
        Assert.Contains("CN=Android Debug", source, StringComparison.Ordinal);
        Assert.Contains("Android APK signing 점검", source, StringComparison.Ordinal);
        Assert.Contains("Android APK가 debug signing 인증서로 서명되어 있습니다", source, StringComparison.Ordinal);
        Assert.Contains("$androidSigningFailure = $FailOnAndroidDebugSigning", source, StringComparison.Ordinal);
        Assert.Contains("elseif ($warningMessages.Count -gt 0)", source, StringComparison.Ordinal);
        Assert.Contains("if ($overallStatus -eq \"PASS\")", source, StringComparison.Ordinal);
        Assert.Contains("elseif ($overallStatus -eq \"WARN\")", source, StringComparison.Ordinal);
        Assert.Contains("if ($overallStatus -eq \"FAIL\")", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "$androidSigningResult = if ($SkipPackageProbe -or $SkipManifestProbe)",
            "Test-AndroidApkSigningProbe -ProjectRoot $ProjectRoot",
            "$androidSigningFailure = $FailOnAndroidDebugSigning",
            "$overallStatus = if ($failedSamples.Count -gt 0",
            "$lines.Add(\"- Android APK signing 점검: $androidSigningSummary\")");
    }

    [Fact]
    public void OperationalAndPreLiveGatesDoNotSwallowLiveObservationWarnAsPass()
    {
        var operationalGate = ReadRepositoryFile(
            "tools",
            "ops",
            "Invoke-GeoraePlanOperationalGate.ps1");
        var preLive = ReadRepositoryFile(
            "tools",
            "verification",
            "Invoke-GeoraePlanPreLiveVerification.ps1");

        Assert.Contains("function Resolve-MarkdownResultStatus", operationalGate, StringComparison.Ordinal);
        Assert.Contains("$liveObservationStatus = Resolve-MarkdownResultStatus -ReportPath $liveObservationReport", operationalGate, StringComparison.Ordinal);
        Assert.Contains("Add-Check -Checks $checks -Name 'live observation' -Status $liveObservationStatus", operationalGate, StringComparison.Ordinal);
        Assert.DoesNotContain("Add-Check -Checks $checks -Name 'live observation' -Status 'PASS' -Detail $liveObservationReport", operationalGate, StringComparison.Ordinal);

        Assert.Contains("$status = Resolve-MarkdownResultStatus -ReportPath $reportPath -DefaultStatus 'PASS'", preLive, StringComparison.Ordinal);
        Assert.Contains("Detail = $status", preLive, StringComparison.Ordinal);
        Assert.Contains("$warnings = @($Results | Where-Object { $_.Passed -and [string]::Equals([string]$_.Detail, 'WARN'", preLive, StringComparison.Ordinal);
        Assert.Contains("$overall = if ($failed.Count -gt 0) { 'FAIL' } elseif ($warnings.Count -gt 0) { 'WARN' } else { 'PASS' }", preLive, StringComparison.Ordinal);
        Assert.Contains("elseif ([string]::Equals([string]$row.Detail, 'WARN'", preLive, StringComparison.Ordinal);
        Assert.Contains("## 경고 항목", preLive, StringComparison.Ordinal);
        AssertInOrder(
            preLive,
            "$status = Resolve-MarkdownResultStatus -ReportPath $reportPath -DefaultStatus 'PASS'",
            "Detail = $status",
            "$warnings = @($Results | Where-Object",
            "$overall = if ($failed.Count -gt 0)",
            "## 경고 항목");
    }

    [Fact]
    public void ReleaseWrappersCanFailDeploymentOnOperationalWarnings()
    {
        var operationalGate = ReadRepositoryFile(
            "tools",
            "ops",
            "Invoke-GeoraePlanOperationalGate.ps1");
        var linuxRelease = ReadRepositoryFile(
            "tools",
            "linux",
            "Publish-GeoraeplanLinuxPcRelease.ps1");
        var fullRelease = ReadRepositoryFile(
            "tools",
            "release",
            "Publish-GeoraePlanFullRelease.ps1");
        var deployAfterTest = ReadRepositoryFile(
            "테스트 시행",
            "Deploy-After-Test.ps1");
        var verificationDeploy = ReadRepositoryFile(
            "테스트 시행",
            "검증완료-반영.ps1");

        Assert.Contains("[switch]$FailOnOperationalWarnings", operationalGate, StringComparison.Ordinal);
        Assert.Contains("$warningChecks = @($checks | Where-Object { $_.Status -eq 'WARN' })", operationalGate, StringComparison.Ordinal);
        Assert.Contains("$overallStatus = if ($FailOnOperationalWarnings) { 'FAIL' } else { 'WARN' }", operationalGate, StringComparison.Ordinal);
        Assert.Contains("운영 Warning 실패 처리", operationalGate, StringComparison.Ordinal);

        Assert.Contains("[switch]$FailOnOperationalWarnings", linuxRelease, StringComparison.Ordinal);
        Assert.Contains("[bool]$FailOnOperationalWarnings = $false", linuxRelease, StringComparison.Ordinal);
        Assert.Contains("$gateArgs += '-FailOnOperationalWarnings'", linuxRelease, StringComparison.Ordinal);
        Assert.Contains("-FailOnOperationalWarnings ([bool]$FailOnOperationalWarnings)", linuxRelease, StringComparison.Ordinal);

        Assert.Contains("[switch]$FailOnOperationalWarnings", fullRelease, StringComparison.Ordinal);
        Assert.Contains("$linuxArgs += '-FailOnOperationalWarnings'", fullRelease, StringComparison.Ordinal);
        Assert.Contains("[switch]$FailOnOperationalWarnings", deployAfterTest, StringComparison.Ordinal);
        Assert.Contains("$linuxArgs += '-FailOnOperationalWarnings'", deployAfterTest, StringComparison.Ordinal);
        Assert.Contains("[switch]$FailOnOperationalWarnings", verificationDeploy, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseDocumentationRecommendsOperationalWarningFailForPaidDelivery()
    {
        var readme = ReadRepositoryFile("README.md");
        var linuxRunbook = ReadRepositoryFile("infra", "LinuxPC-운영-런북.md");
        var updateGuide = ReadRepositoryFile("수정_업데이트_가이드_2026-03-20.md");
        var testReadme = ReadRepositoryFile("테스트 시행", "README.md");

        foreach (var source in new[] { readme, linuxRunbook, updateGuide, testReadme })
        {
            Assert.Contains("-FailOnOperationalWarnings", source, StringComparison.Ordinal);
            Assert.Contains("유료 납품", source, StringComparison.Ordinal);
        }

        Assert.Contains("operational warning을 배포 차단", readme, StringComparison.Ordinal);
        Assert.Contains("live 전/후 operational gate를 생략하지 않고", linuxRunbook, StringComparison.Ordinal);
        Assert.Contains("live 전/후 operational gate를 생략하지 않고", updateGuide, StringComparison.Ordinal);
        Assert.Contains("live 관찰/운영 게이트 warning도 배포 차단", testReadme, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalCacheConsistencyDetectsNonInventoryAndAssetWarehouseStockResidues()
    {
        var source = ReadRepositoryFile(
            "tools",
            "verification",
            "Invoke-GeoraePlanLocalCacheConsistency.ps1");

        Assert.Contains("itemWarehouseStocks = 'ItemWarehouseStocks'", source, StringComparison.Ordinal);
        Assert.Contains("result[\"inventoryResidues\"]", source, StringComparison.Ordinal);
        Assert.Contains("normalize_tracking", source, StringComparison.Ordinal);
        Assert.Contains("STOCK = \"\\uc7ac\\uace0\"", source, StringComparison.Ordinal);
        Assert.Contains("ASSET = \"\\uc790\\uc0b0\"", source, StringComparison.Ordinal);
        Assert.Contains("checkedNonInventoryItemCount", source, StringComparison.Ordinal);
        Assert.Contains("currentStockResidueCount", source, StringComparison.Ordinal);
        Assert.Contains("warehouseStockResidueCount", source, StringComparison.Ordinal);
        Assert.Contains("warehouseStockQuantityResidueCount", source, StringComparison.Ordinal);
        Assert.Contains("${currentStockResidueCount}건", source, StringComparison.Ordinal);
        Assert.Contains("${warehouseStockResidueCount}건", source, StringComparison.Ordinal);
        Assert.Contains("비재고/자산/렌탈료 품목의 CurrentStock 잔여값", source, StringComparison.Ordinal);
        Assert.Contains("비재고/자산/렌탈료 품목에 연결된 로컬 ItemWarehouseStocks 잔여 row", source, StringComparison.Ordinal);
        Assert.Contains("## 비재고/자산 품목 재고 잔여 row 점검", source, StringComparison.Ordinal);
        Assert.Contains("WarehouseStockQuantityRows", source, StringComparison.Ordinal);
    }

    [Fact]
    public void FullReleaseForwardsExplicitRentalTemplateRiskAcceptanceToLinuxDeploy()
    {
        var source = ReadRepositoryFile(
            "tools",
            "release",
            "Publish-GeoraePlanFullRelease.ps1");

        Assert.Contains("[switch]$AcceptRentalTemplateItemReferenceRisk", source, StringComparison.Ordinal);
        Assert.Contains("if ($AcceptRentalTemplateItemReferenceRisk)", source, StringComparison.Ordinal);
        Assert.Contains("$linuxArgs += '-AcceptRentalTemplateItemReferenceRisk'", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "[switch]$AcceptRentalTemplateItemReferenceRisk",
            "if ($AcceptRentalTemplateItemReferenceRisk)",
            "$linuxArgs += '-AcceptRentalTemplateItemReferenceRisk'");
    }

    [Fact]
    public void DeployAfterTestForwardsExplicitRentalTemplateRiskAcceptanceToLinuxDeploy()
    {
        var source = ReadRepositoryFile(
            "테스트 시행",
            "Deploy-After-Test.ps1");

        Assert.Contains("[switch]$AcceptRentalTemplateItemReferenceRisk", source, StringComparison.Ordinal);
        Assert.Contains("[switch]$SkipPostDeployOperationalGate", source, StringComparison.Ordinal);
        Assert.Contains("[string]$PostDeployBaseUrl = ''", source, StringComparison.Ordinal);
        Assert.Contains("if ($AcceptRentalTemplateItemReferenceRisk)", source, StringComparison.Ordinal);
        Assert.Contains("$linuxArgs += '-AcceptRentalTemplateItemReferenceRisk'", source, StringComparison.Ordinal);
        Assert.Contains("if ($SkipPostDeployOperationalGate)", source, StringComparison.Ordinal);
        Assert.Contains("$linuxArgs += '-SkipPostDeployOperationalGate'", source, StringComparison.Ordinal);
        Assert.Contains("$linuxArgs += @('-PostDeployBaseUrl', $PostDeployBaseUrl)", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "[switch]$AcceptRentalTemplateItemReferenceRisk",
            "if ($AcceptRentalTemplateItemReferenceRisk)",
            "$linuxArgs += '-AcceptRentalTemplateItemReferenceRisk'");
    }

    [Fact]
    public void VerificationDeployWrapperExposesExplicitGateSkipAndRiskOptions()
    {
        var source = ReadRepositoryFile(
            "테스트 시행",
            "검증완료-반영.ps1");

        Assert.Contains("[switch]$SkipPreDeployOperationalGate", source, StringComparison.Ordinal);
        Assert.Contains("[switch]$SkipPostDeployOperationalGate", source, StringComparison.Ordinal);
        Assert.Contains("[switch]$AcceptRentalTemplateItemReferenceRisk", source, StringComparison.Ordinal);
        Assert.Contains("[string]$PreDeployBaseUrl = ''", source, StringComparison.Ordinal);
        Assert.Contains("[string]$PostDeployBaseUrl = ''", source, StringComparison.Ordinal);
        Assert.Contains("& $scriptPath @PSBoundParameters", source, StringComparison.Ordinal);
    }

    [Fact]
    public void FullReleaseForwardsAndroidAotAndTrimmingOverridesToApkBuild()
    {
        var source = ReadRepositoryFile(
            "tools",
            "release",
            "Publish-GeoraePlanFullRelease.ps1");

        Assert.Contains("[switch]$DisableAndroidAot", source, StringComparison.Ordinal);
        Assert.Contains("[switch]$DisableAndroidTrimming", source, StringComparison.Ordinal);
        Assert.Contains("if ($DisableAndroidAot)", source, StringComparison.Ordinal);
        Assert.Contains("$androidArgs += '-DisableAot'", source, StringComparison.Ordinal);
        Assert.Contains("if ($DisableAndroidTrimming)", source, StringComparison.Ordinal);
        Assert.Contains("$androidArgs += '-DisableTrimming'", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "$androidArgs = @(",
            "if ($DisableAndroidAot)",
            "$androidArgs += '-DisableAot'",
            "if ($DisableAndroidTrimming)",
            "$androidArgs += '-DisableTrimming'",
            "& powershell @androidArgs");
    }

    [Fact]
    public void FullReleaseForwardsExplicitLegacyAndroidDebugSigningRiskAcceptanceToApkBuild()
    {
        var source = ReadRepositoryFile(
            "tools",
            "release",
            "Publish-GeoraePlanFullRelease.ps1");

        Assert.Contains("[switch]$AllowLegacyAndroidDebugSigning", source, StringComparison.Ordinal);
        Assert.Contains("if ($AllowLegacyAndroidDebugSigning)", source, StringComparison.Ordinal);
        Assert.Contains("Legacy Android debug signing is explicitly allowed", source, StringComparison.Ordinal);
        Assert.Contains("$androidArgs += '-AllowDebugSigning'", source, StringComparison.Ordinal);
        Assert.DoesNotContain("$androidArgs += '-AllowDebugSigning' # default", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "[switch]$AllowLegacyAndroidDebugSigning",
            "Write-Warning \"Legacy Android debug signing is explicitly allowed",
            "$androidArgs = @(",
            "$androidArgs += '-AllowDebugSigning'",
            "& powershell @androidArgs");
    }

    [Fact]
    public void FullReleasePreflightsAndroidReleaseSigningBeforeBuildingArtifacts()
    {
        var source = ReadRepositoryFile(
            "tools",
            "release",
            "Publish-GeoraePlanFullRelease.ps1");

        Assert.Contains("function Assert-AndroidReleaseSigningReady", source, StringComparison.Ordinal);
        Assert.Contains("Android signing config not found before release build", source, StringComparison.Ordinal);
        Assert.Contains("Android keystore not found before release build", source, StringComparison.Ordinal);
        Assert.Contains("Release Android package is using a debug signing key before release build", source, StringComparison.Ordinal);
        Assert.Contains("AllowLegacyAndroidDebugSigning:$AllowLegacyAndroidDebugSigning", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "if ($AllowLegacyAndroidDebugSigning)",
            "Assert-AndroidReleaseSigningReady -SigningConfigPath $SigningConfigPath",
            "$solution = Get-ChildItem",
            "& $dotnetExe build",
            "& powershell @androidArgs");
    }

    [Fact]
    public void RentalTemplateRepairPlanScript_GeneratesRollbackPatchOnlyAfterSelectValidation()
    {
        var source = ReadRepositoryFile(
            "tools",
            "linux",
            "New-GeoraePlanRentalTemplateItemReferenceRepairPlan.ps1");

        Assert.Contains("[switch]$ValidateAgainstLinuxPc", source, StringComparison.Ordinal);
        Assert.Contains("[string]$PatchMode = 'Rollback'", source, StringComparison.Ordinal);
        Assert.Contains("review-template.csv", source, StringComparison.Ordinal);
        Assert.Contains("approved-mappings.normalized.csv", source, StringComparison.Ordinal);
        Assert.Contains("validation-summary.csv", source, StringComparison.Ordinal);
        Assert.Contains("copy (", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("to stdout with csv header;", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("approved_item_not_found", source, StringComparison.Ordinal);
        Assert.Contains("current_reference_is_valid_now", source, StringComparison.Ordinal);
        Assert.Contains("ValidationStatus -eq 'ready'", source, StringComparison.Ordinal);
        Assert.Contains("ProposedItemId = Get-CsvValue -Row $row -Names @('ProposedItemId')", source, StringComparison.Ordinal);
        Assert.Contains("ProposedSource = Get-CsvValue -Row $row -Names @('ProposedSource')", source, StringComparison.Ordinal);
        Assert.Contains("ProposedConfidence = Get-CsvValue -Row $row -Names @('ProposedConfidence')", source, StringComparison.Ordinal);
        Assert.Contains("ApprovedItemId = Get-CsvValue -Row $row -Names @('ApprovedItemId', 'NewItemId', 'TargetItemId')", source, StringComparison.Ordinal);
        Assert.Contains("[int]$ExpectedApprovedMappingCount = 0", source, StringComparison.Ordinal);
        Assert.Contains("[int]$ExpectedReadyMappingCount = 0", source, StringComparison.Ordinal);
        Assert.Contains("([string][char]0xC2B9) + ([string][char]0xC778)", source, StringComparison.Ordinal);
        Assert.Contains("ReviewDecision must be Approve/Approved/Korean-approve", source, StringComparison.Ordinal);
        Assert.Contains("Approved mapping count mismatch", source, StringComparison.Ordinal);
        Assert.Contains("Ready mapping count mismatch", source, StringComparison.Ordinal);
        Assert.Contains("ExpectedReadyMappingCount requires -ValidateAgainstLinuxPc", source, StringComparison.Ordinal);
        Assert.Contains("repair-plan-gate.md", source, StringComparison.Ordinal);
        Assert.Contains("repair_plan_gate_status=$repairPlanGateStatus", source, StringComparison.Ordinal);
        Assert.Contains("Repair plan gate failed", source, StringComparison.Ordinal);
        Assert.Contains("create temporary table \"RentalBillingTemplateItemReferenceRepairCounts\" on commit drop as", source, StringComparison.Ordinal);
        Assert.Contains("approved_mapping_count mismatch", source, StringComparison.Ordinal);
        Assert.Contains("target_profile_count mismatch", source, StringComparison.Ordinal);
        Assert.Contains("inserted_backup_count mismatch", source, StringComparison.Ordinal);
        Assert.Contains("updated_profile_count mismatch", source, StringComparison.Ordinal);
        Assert.Contains("select * from \"RentalBillingTemplateItemReferenceRepairCounts\"", source, StringComparison.Ordinal);
        Assert.Contains("transaction-time assertions for approved, target profile, backup, and updated profile counts", source, StringComparison.Ordinal);
        Assert.DoesNotContain("@('ApprovedItemId', 'ProposedItemId'", source, StringComparison.Ordinal);
        Assert.Contains("repair-<db>-rollback.sql", source, StringComparison.Ordinal);
        Assert.Contains("Run this SQL against a cloned/test database first.", source, StringComparison.Ordinal);
        Assert.Contains("$terminalStatement = if ($Mode -eq 'Commit') { 'commit;' } else { 'rollback;' }", source, StringComparison.Ordinal);
        Assert.Contains("patch_sql=none", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "$csvText = Invoke-RemotePsqlCsv -Database $database -Sql $sql",
            "Where-Object { $_.ValidationStatus -eq 'ready' }",
            "$patchSql = New-PatchSql -Database $database");

        Assert.DoesNotContain("docker compose down", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("docker system prune", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("docker restart", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("systemctl restart", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reboot", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("drop table", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("truncate", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RentalTemplateRepairReadinessGate_ChainsApprovalAndSelectOnlyRepairPlanChecks()
    {
        var source = ReadRepositoryFile(
            "tools",
            "linux",
            "Test-GeoraePlanRentalTemplateRepairReadiness.ps1");

        Assert.Contains("New-GeoraePlanRentalTemplateApprovalIntakePack.ps1", source, StringComparison.Ordinal);
        Assert.Contains("New-GeoraePlanRentalTemplateItemReferenceRepairPlan.ps1", source, StringComparison.Ordinal);
        Assert.Contains("Export-GeoraePlanRentalTemplateItemReferenceCandidates.ps1", source, StringComparison.Ordinal);
        Assert.Contains("-RequireAllApproved", source, StringComparison.Ordinal);
        Assert.Contains("-ValidateAgainstLinuxPc", source, StringComparison.Ordinal);
        Assert.Contains("-ExpectedApprovedMappingCount", source, StringComparison.Ordinal);
        Assert.Contains("-ExpectedReadyMappingCount", source, StringComparison.Ordinal);
        Assert.Contains("[switch]$SkipCurrentCandidateKeyCheck", source, StringComparison.Ordinal);
        Assert.Contains("-PatchMode", source, StringComparison.Ordinal);
        Assert.Contains("'Rollback'", source, StringComparison.Ordinal);
        Assert.Contains("approved-mappings-for-select-validation.csv", source, StringComparison.Ordinal);
        Assert.Contains("candidate-rows.csv", source, StringComparison.Ordinal);
        Assert.Contains("current-candidates", source, StringComparison.Ordinal);
        Assert.Contains("current-candidate-key-mismatches.csv", source, StringComparison.Ordinal);
        Assert.Contains("repair-plan-gate.md", source, StringComparison.Ordinal);
        Assert.Contains("rental-template-repair-readiness-gate.md", source, StringComparison.Ordinal);
        Assert.Contains("Current unresolved candidate count mismatch", source, StringComparison.Ordinal);
        Assert.Contains("Approval mapping keys do not match current unresolved candidate keys", source, StringComparison.Ordinal);
        Assert.Contains("current_candidate_missing_from_approval", source, StringComparison.Ordinal);
        Assert.Contains("approval_key_not_in_current_candidates", source, StringComparison.Ordinal);
        Assert.Contains("Repair readiness gate failed", source, StringComparison.Ordinal);
        Assert.Contains("this script never executes SQL patches", source, StringComparison.Ordinal);
        Assert.Contains("rental_template_repair_readiness_status=$status", source, StringComparison.Ordinal);
        Assert.Contains("Generated SQL is not rollback-only", source, StringComparison.Ordinal);
        Assert.Contains("Generated readiness SQL must not contain a standalone commit statement", source, StringComparison.Ordinal);
        Assert.Contains("do $repair_assert$", source, StringComparison.Ordinal);
        Assert.Contains("approved_mapping_count mismatch", source, StringComparison.Ordinal);
        Assert.Contains("target_profile_count mismatch", source, StringComparison.Ordinal);
        Assert.Contains("inserted_backup_count mismatch", source, StringComparison.Ordinal);
        Assert.Contains("updated_profile_count mismatch", source, StringComparison.Ordinal);
        Assert.Contains("Generated SQL is missing required safety assertion fragment", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "approval-intake-require-all",
            "current-candidate-key-check",
            "repair-plan-select-ready");

        Assert.DoesNotContain("PatchMode', 'Commit'", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("delete from", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("update \"", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("insert into", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("drop table", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("truncate", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("docker compose down", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("docker restart", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("systemctl restart", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reboot", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RentalTemplateManualReviewPackScript_DoesNotPrefillApprovalsAndKeepsOutputLocal()
    {
        var source = ReadRepositoryFile(
            "tools",
            "linux",
            "New-GeoraePlanRentalTemplateManualReviewPack.ps1");

        Assert.Contains("manual-review-decision-template.csv", source, StringComparison.Ordinal);
        Assert.Contains("manual-review-option-details.csv", source, StringComparison.Ordinal);
        Assert.Contains("manual-review-decision-summary.csv", source, StringComparison.Ordinal);
        Assert.Contains("CandidateOptionCount", source, StringComparison.Ordinal);
        Assert.Contains("Option${optionNumber}ItemId", source, StringComparison.Ordinal);
        Assert.Contains("ManualReviewPriority", source, StringComparison.Ordinal);
        Assert.Contains("P1_asset_multi_small", source, StringComparison.Ordinal);
        Assert.Contains("choose_one_active_asset_item", source, StringComparison.Ordinal);
        Assert.Contains("ReviewDecision = ''", source, StringComparison.Ordinal);
        Assert.Contains("ApprovedItemId = ''", source, StringComparison.Ordinal);

        Assert.DoesNotContain("ssh", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("psql", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("docker", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("delete from", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("update \"", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("drop table", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("truncate", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RentalTemplateApprovalIntakeScript_ClearsDryRunApprovalsAndValidatesFilledRowsLocally()
    {
        var source = ReadRepositoryFile(
            "tools",
            "linux",
            "New-GeoraePlanRentalTemplateApprovalIntakePack.ps1");

        Assert.Contains("approval-intake-template.csv", source, StringComparison.Ordinal);
        Assert.Contains("approval-intake-validation.csv", source, StringComparison.Ordinal);
        Assert.Contains("approved-mappings-for-select-validation.csv", source, StringComparison.Ordinal);
        Assert.Contains("proposed_ready_requires_business_approval", source, StringComparison.Ordinal);
        Assert.Contains("manual_review_requires_business_approval", source, StringComparison.Ordinal);
        Assert.Contains("ReviewDecision = ''", source, StringComparison.Ordinal);
        Assert.Contains("ApprovedItemId = ''", source, StringComparison.Ordinal);
        Assert.Contains("OriginalReviewDecision", source, StringComparison.Ordinal);
        Assert.Contains("OriginalApprovedItemId", source, StringComparison.Ordinal);
        Assert.Contains("Test-ApprovalDecision", source, StringComparison.Ordinal);
        Assert.Contains("[switch]$RequireAllApproved", source, StringComparison.Ordinal);
        Assert.Contains("([string][char]0xC2B9) + ([string][char]0xC778)", source, StringComparison.Ordinal);
        Assert.Contains("validate_existing_approval_intake", source, StringComparison.Ordinal);
        Assert.Contains("Dry-run/system reviewer markers cannot be used as business approval.", source, StringComparison.Ordinal);
        Assert.Contains("ApprovedItemId is not in suggested/candidate option ids.", source, StringComparison.Ordinal);
        Assert.Contains("ReviewDecision must be Approve/Approved/Korean-approve", source, StringComparison.Ordinal);
        Assert.Contains("Duplicate Database/ProfileId/TemplateOrdinal keys were found in approval intake rows", source, StringComparison.Ordinal);
        Assert.Contains("approved_input_valid", source, StringComparison.Ordinal);
        Assert.Contains("pending_approval", source, StringComparison.Ordinal);
        Assert.Contains("invalid_approval_input", source, StringComparison.Ordinal);
        Assert.Contains("approval-intake-validation-status-summary.csv", source, StringComparison.Ordinal);
        Assert.Contains("approval-intake-gate.md", source, StringComparison.Ordinal);
        Assert.Contains("approval_input_gate_status=$approvalInputGateStatus", source, StringComparison.Ordinal);
        Assert.Contains("valid approved rows for follow-up SELECT-only validation", source, StringComparison.Ordinal);
        Assert.Contains("Approval intake gate failed", source, StringComparison.Ordinal);

        Assert.DoesNotContain("ssh", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("psql", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("docker", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("delete from", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("update \"", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("drop table", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("truncate", source, StringComparison.OrdinalIgnoreCase);
    }

    private static Uri InvokeValidatePackageUri(string packageUrl, Uri baseUri)
    {
        var method = typeof(DesktopAppUpdateService).GetMethod(
            "ValidatePackageUri",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, [packageUrl, baseUri]);
        return Assert.IsType<Uri>(result);
    }

    private static void AssertValidatePackageUriRejected(string packageUrl, Uri baseUri)
    {
        var ex = Assert.Throws<TargetInvocationException>(() => InvokeValidatePackageUri(packageUrl, baseUri));
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    private static async Task<ProcessResult> RunPowerShellAsync(
        string scriptPath,
        params (string Name, string? Value)[] argumentsAndEnvironment)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "powershell",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
        process.StartInfo.ArgumentList.Add("Bypass");
        process.StartInfo.ArgumentList.Add("-File");
        process.StartInfo.ArgumentList.Add(scriptPath);

        foreach (var (name, value) in argumentsAndEnvironment)
        {
            if (!name.StartsWith("-", StringComparison.Ordinal))
            {
                process.StartInfo.Environment[name] = value ?? string.Empty;
                continue;
            }

            process.StartInfo.ArgumentList.Add(name);
            if (value is not null)
                process.StartInfo.ArgumentList.Add(value);
        }

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var exited = process.WaitForExit(120_000);
        Assert.True(exited, $"PowerShell script timed out: {scriptPath}");

        return new ProcessResult(
            process.ExitCode,
            await stdoutTask,
            await stderrTask);
    }

    private static string ReadRepositoryFile(params string[] pathParts)
        => File.ReadAllText(Path.Combine([FindRepositoryRoot(), .. pathParts]));

    private sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

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
