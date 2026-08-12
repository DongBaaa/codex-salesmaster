using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class WindowsSigningPipelineTests
{
    [Fact]
    public void WindowsSigningVerifierScript_IsUtf8BomAndExposesSignerAndTimestampChecks()
    {
        var path = RepositoryFile("tools", "release", "Test-GeoraePlanWindowsSigning.ps1");
        var bytes = File.ReadAllBytes(path);

        Assert.True(bytes.Length >= 3, "Verifier script must not be empty.");
        Assert.Equal((byte)0xEF, bytes[0]);
        Assert.Equal((byte)0xBB, bytes[1]);
        Assert.Equal((byte)0xBF, bytes[2]);

        var source = File.ReadAllText(path);
        Assert.Contains("[switch]$RequireTimestamp", source, StringComparison.Ordinal);
        Assert.Contains("[string]$ExpectedSignerThumbprint = ''", source, StringComparison.Ordinal);
        Assert.Contains("[string[]]$ExpectedSignerSubjectContains = @()", source, StringComparison.Ordinal);
        Assert.Contains("[string[]]$ExpectedTimestampSubjectContains = @()", source, StringComparison.Ordinal);
        Assert.Contains("windows_authenticode=WARNING_UNSIGNED", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsSigningExampleJson_UsesOnlyStoreSelectorsAndEnvironmentVariableReferences()
    {
        var path = RepositoryFile("tools", "release", "windows-signing.example.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("SHA256", root.GetProperty("fileDigestAlgorithm").GetString());
        Assert.Equal("SHA256", root.GetProperty("timestampDigestAlgorithm").GetString());
        Assert.Equal("https://timestamp.digicert.com", root.GetProperty("timestampRfc3161Url").GetString());
        Assert.Equal(string.Empty, root.GetProperty("certificateThumbprint").GetString());
        Assert.Equal(string.Empty, root.GetProperty("certificateSubjectContains").GetString());
        Assert.Equal("GEORAEPLAN_WINDOWS_SIGN_PFX_PATH", root.GetProperty("certificatePathEnvironmentVariable").GetString());
        Assert.Equal("GEORAEPLAN_WINDOWS_SIGN_PFX_PASSWORD", root.GetProperty("certificatePasswordEnvironmentVariable").GetString());
        Assert.False(root.TryGetProperty("certificatePassword", out _));
        Assert.DoesNotContain("CHANGE_ME", File.ReadAllText(path), StringComparison.Ordinal);

        var gitignore = File.ReadAllText(RepositoryFile(".gitignore"));
        Assert.Contains("tools/release/windows-signing.local.json", gitignore, StringComparison.Ordinal);
        Assert.Contains("Mobile/**/android-signing.*.local.json", gitignore, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsReleaseScripts_WireOptionalSigningConfigAndStrictGate()
    {
        var buildSource = File.ReadAllText(RepositoryFile("tools", "release", "Build-GeoraePlanDesktopInstaller.ps1"));
        var nativeSource = File.ReadAllText(RepositoryFile("tools", "release", "Build-GeoraePlanDesktopNativeInstallers.ps1"));
        var publishSource = File.ReadAllText(RepositoryFile("tools", "release", "Publish-GeoraePlanFullRelease.ps1"));

        Assert.Contains("[string]$WindowsSigningConfigPath", buildSource, StringComparison.Ordinal);
        Assert.Contains("[switch]$RequireWindowsAuthenticode", buildSource, StringComparison.Ordinal);
        Assert.Contains("[string]::IsNullOrWhiteSpace($WindowsSigningConfigPath) -and -not $RequireSigning", buildSource, StringComparison.Ordinal);
        Assert.Contains("windows_authenticode_signing=SKIPPED_NO_CONFIG", buildSource, StringComparison.Ordinal);
        Assert.Contains("Invoke-WindowsArtifactSigning -ProjectRoot $ProjectRoot -WindowsSigningConfigPath $WindowsSigningConfigPath -PackageRoot $packageRoot -RequireSigning:$RequireWindowsAuthenticode", buildSource, StringComparison.Ordinal);
        AssertInOrder(
            buildSource,
            "Invoke-WindowsArtifactSigning -ProjectRoot $ProjectRoot -WindowsSigningConfigPath $WindowsSigningConfigPath -PackageRoot $packageRoot -RequireSigning:$RequireWindowsAuthenticode",
            "Compress-Archive",
            "-DestinationPath $stagedZipPath",
            "-ArchivePath $stagedZipPath");
        Assert.Contains("'-WindowsSigningConfigPath', $WindowsSigningConfigPath", buildSource, StringComparison.Ordinal);
        Assert.Contains("'-RequireWindowsAuthenticode'", buildSource, StringComparison.Ordinal);

        Assert.Contains("[string]$WindowsSigningConfigPath", nativeSource, StringComparison.Ordinal);
        Assert.Contains("[switch]$RequireWindowsAuthenticode", nativeSource, StringComparison.Ordinal);
        Assert.Contains("[string]::IsNullOrWhiteSpace($WindowsSigningConfigPath) -and -not $RequireSigning", nativeSource, StringComparison.Ordinal);
        Assert.Contains("windows_authenticode_signing=SKIPPED_NO_CONFIG", nativeSource, StringComparison.Ordinal);
        Assert.Contains("(Join-Path $sourceForPackaging 'Updater\\거래플랜.Updater.exe')", nativeSource, StringComparison.Ordinal);
        AssertInOrder(
            nativeSource,
            "(Join-Path $sourceForPackaging 'Updater\\거래플랜.Updater.exe')",
            "$productWxsPath = Join-Path $stagingRoot 'Product.wxs'");
        Assert.Contains("Invoke-WindowsArtifactSigning -ProjectRoot $ProjectRoot -WindowsSigningConfigPath $WindowsSigningConfigPath -Paths @($tempMsiPath) -RequireSigning:$RequireWindowsAuthenticode", nativeSource, StringComparison.Ordinal);
        Assert.Contains("Invoke-WindowsArtifactSigning -ProjectRoot $ProjectRoot -WindowsSigningConfigPath $WindowsSigningConfigPath -Paths @($versionedExePath, $stableExePath) -RequireSigning:$RequireWindowsAuthenticode", nativeSource, StringComparison.Ordinal);
        AssertInOrder(
            nativeSource,
            "Invoke-WindowsArtifactSigning -ProjectRoot $ProjectRoot -WindowsSigningConfigPath $WindowsSigningConfigPath -Paths @($tempMsiPath) -RequireSigning:$RequireWindowsAuthenticode",
            "Copy-Item -LiteralPath $tempMsiPath -Destination $stableMsiPath -Force",
            "New-BootstrapperProjectFiles -BootstrapperRoot $bootstrapperRoot -MsiPath $tempMsiPath",
            "Invoke-WindowsArtifactSigning -ProjectRoot $ProjectRoot -WindowsSigningConfigPath $WindowsSigningConfigPath -Paths @($versionedExePath, $stableExePath) -RequireSigning:$RequireWindowsAuthenticode",
            "Write-Sha256File -Path $versionedMsiPath");

        Assert.Contains("[string]$WindowsSigningConfigPath", publishSource, StringComparison.Ordinal);
        Assert.Contains("'-WindowsSigningConfigPath', $WindowsSigningConfigPath", publishSource, StringComparison.Ordinal);
        Assert.DoesNotContain("AllowUnsignedWindowsArtifactsForLocalDevelopment", publishSource, StringComparison.Ordinal);
        Assert.DoesNotContain("$enforceWindowsAuthenticode", publishSource, StringComparison.Ordinal);
        Assert.DoesNotContain("if ($RequireWindowsAuthenticode)", publishSource, StringComparison.Ordinal);
        AssertInOrder(
            publishSource,
            "$desktopArgs += '-RequireWindowsAuthenticode'",
            "& powershell @desktopArgs",
            "$windowsSigningCheckArgs += '-RequireSigned'",
            "$windowsSigningCheckArgs += '-RequireTimestamp'",
            "& powershell @windowsSigningCheckArgs",
            "$updateAssetsScript = Join-Path $ProjectRoot 'tools\\release\\Publish-GeoraePlanUpdateAssets.ps1'");
    }

    [Fact]
    public void WindowsArtifactSigningScript_UsesSha256AndRfc3161Timestamping()
    {
        var source = File.ReadAllText(RepositoryFile("tools", "release", "Sign-GeoraePlanWindowsArtifacts.ps1"));

        Assert.Contains("$arguments.Add('/fd')", source, StringComparison.Ordinal);
        Assert.Contains("$arguments.Add('/td')", source, StringComparison.Ordinal);
        Assert.Contains("$arguments.Add('/tr')", source, StringComparison.Ordinal);
        Assert.Contains("Only SHA256 is allowed for Windows file digest signing", source, StringComparison.Ordinal);
        Assert.Contains("RFC3161 timestamp URL must be an absolute HTTPS URL", source, StringComparison.Ordinal);
        Assert.Contains("Test-CodeSigningCertificate", source, StringComparison.Ordinal);
        Assert.Contains("Test-GeoraePlanWindowsSigning.ps1", source, StringComparison.Ordinal);
        Assert.Contains("'App\\거래플랜.Desktop.App.exe'", source, StringComparison.Ordinal);
        Assert.Contains("windows_authenticode_signing=SKIPPED_NO_CERTIFICATE", source, StringComparison.Ordinal);
        Assert.Contains("windows_authenticode_signing=PASS", source, StringComparison.Ordinal);

        var verifierSource = File.ReadAllText(RepositoryFile("tools", "release", "Test-GeoraePlanWindowsSigning.ps1"));
        Assert.Contains("(Join-Path $packageRoot 'App\\거래플랜.Desktop.App.exe')", verifierSource, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WindowsArtifactSigningScript_SkipsMissingCertificateUnlessStrict()
    {
        var repositoryRoot = FindRepositoryRoot();
        var testRoot = Path.Combine(repositoryRoot, "temp", "windows-signing-tests", Guid.NewGuid().ToString("N"));
        var packageRoot = Path.Combine(testRoot, "package");
        var appRoot = Path.Combine(packageRoot, "App");
        var updaterRoot = Path.Combine(appRoot, "Updater");
        var configPath = Path.Combine(testRoot, "windows-signing.local.json");
        var scriptPath = RepositoryFile("tools", "release", "Sign-GeoraePlanWindowsArtifacts.ps1");
        var originalPfxPath = Environment.GetEnvironmentVariable("GEORAEPLAN_WINDOWS_SIGN_PFX_PATH");
        var originalPfxPassword = Environment.GetEnvironmentVariable("GEORAEPLAN_WINDOWS_SIGN_PFX_PASSWORD");

        try
        {
            Directory.CreateDirectory(updaterRoot);
            File.WriteAllBytes(Path.Combine(appRoot, "거래플랜.exe"), [0x4D, 0x5A]);
            File.WriteAllBytes(Path.Combine(updaterRoot, "거래플랜.Updater.exe"), [0x4D, 0x5A]);
            File.WriteAllText(configPath, File.ReadAllText(RepositoryFile("tools", "release", "windows-signing.example.json")), new UTF8Encoding(false));

            Environment.SetEnvironmentVariable("GEORAEPLAN_WINDOWS_SIGN_PFX_PATH", null);
            Environment.SetEnvironmentVariable("GEORAEPLAN_WINDOWS_SIGN_PFX_PASSWORD", null);

            var nonStrict = await RunPowerShellFileAsync(
                scriptPath,
                "-ProjectRoot", repositoryRoot,
                "-WindowsSigningConfigPath", configPath,
                "-PackageRoot", packageRoot);

            Assert.Equal(0, nonStrict.ExitCode);
            Assert.Contains("windows_authenticode_signing=SKIPPED_NO_CERTIFICATE", nonStrict.StdOut + nonStrict.StdErr, StringComparison.Ordinal);

            var strict = await RunPowerShellFileAsync(
                scriptPath,
                "-ProjectRoot", repositoryRoot,
                "-WindowsSigningConfigPath", configPath,
                "-PackageRoot", packageRoot,
                "-RequireSigning");

            Assert.NotEqual(0, strict.ExitCode);
            Assert.Contains("windows_authenticode_signing=FAIL", strict.StdOut + strict.StdErr, StringComparison.Ordinal);
            Assert.Contains("No usable Windows signing certificate was resolved.", strict.StdOut + strict.StdErr, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_WINDOWS_SIGN_PFX_PATH", originalPfxPath);
            Environment.SetEnvironmentVariable("GEORAEPLAN_WINDOWS_SIGN_PFX_PASSWORD", originalPfxPassword);

            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task WindowsSigningVerifier_HandlesEmptyExpectationArraysAndFailsOnlyWhenStrict()
    {
        var repositoryRoot = FindRepositoryRoot();
        var testRoot = Path.Combine(repositoryRoot, "temp", "windows-signing-verifier-tests", Guid.NewGuid().ToString("N"));
        var unsignedPath = Path.Combine(testRoot, "unsigned.exe");
        var verifierPath = RepositoryFile("tools", "release", "Test-GeoraePlanWindowsSigning.ps1");

        try
        {
            Directory.CreateDirectory(testRoot);
            File.WriteAllBytes(unsignedPath, [0x4D, 0x5A, 0x00, 0x00]);

            var nonStrict = await RunPowerShellFileAsync(verifierPath, "-Paths", unsignedPath);
            Assert.Equal(0, nonStrict.ExitCode);
            Assert.Contains("windows_authenticode=WARNING_UNSIGNED", nonStrict.StdOut + nonStrict.StdErr, StringComparison.Ordinal);

            var strict = await RunPowerShellFileAsync(verifierPath, "-Paths", unsignedPath, "-RequireSigned", "-RequireTimestamp");
            Assert.NotEqual(0, strict.ExitCode);
            Assert.Contains("windows_authenticode=FAIL", strict.StdOut + strict.StdErr, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ModifiedWindowsReleaseScripts_ParseInWindowsPowerShell()
    {
        var scripts = new[]
        {
            RepositoryFile("tools", "release", "Test-GeoraePlanWindowsSigning.ps1"),
            RepositoryFile("tools", "release", "Sign-GeoraePlanWindowsArtifacts.ps1"),
            RepositoryFile("tools", "release", "Build-GeoraePlanDesktopInstaller.ps1"),
            RepositoryFile("tools", "release", "Build-GeoraePlanDesktopNativeInstallers.ps1"),
            RepositoryFile("tools", "release", "Publish-GeoraePlanFullRelease.ps1")
        };

        foreach (var script in scripts)
        {
            var command = $"[void][ScriptBlock]::Create((Get-Content -Raw -LiteralPath '{EscapePowerShellSingleQuotedLiteral(script)}')); Write-Host 'parsed'";
            var result = await RunPowerShellCommandAsync(command);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("parsed", result.StdOut, StringComparison.Ordinal);
            Assert.DoesNotContain("ParserError", result.StdErr, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static async Task<ProcessResult> RunPowerShellFileAsync(string scriptPath, params string[] arguments)
    {
        using var process = CreatePowerShellProcess();
        process.StartInfo.ArgumentList.Add("-File");
        process.StartInfo.ArgumentList.Add(scriptPath);
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        return await RunProcessAsync(process);
    }

    private static async Task<ProcessResult> RunPowerShellCommandAsync(string command)
    {
        using var process = CreatePowerShellProcess();
        process.StartInfo.ArgumentList.Add("-Command");
        process.StartInfo.ArgumentList.Add(command);

        return await RunProcessAsync(process);
    }

    private static Process CreatePowerShellProcess()
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
        process.StartInfo.ArgumentList.Add("Bypass");
        return process;
    }

    private static async Task<ProcessResult> RunProcessAsync(Process process)
    {
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var exited = process.WaitForExit(120_000);
        Assert.True(exited, "PowerShell process timed out.");

        return new ProcessResult(
            process.ExitCode,
            await stdoutTask,
            await stderrTask);
    }

    private static string RepositoryFile(params string[] pathParts)
        => Path.Combine([FindRepositoryRoot(), .. pathParts]);

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

    private static string EscapePowerShellSingleQuotedLiteral(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) &&
                Directory.Exists(Path.Combine(directory.FullName, "tools")) &&
                Directory.Exists(Path.Combine(directory.FullName, "Desktop")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);
}
