using System.Diagnostics;
using System.Text;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class AndroidAotStagingBuildRegressionTests
{
    [Fact]
    public void AndroidReleaseBuildScript_StagesReleaseAotBuildsFromShortAsciiDDriveAndKeepsKnownFallback()
    {
        var source = ReadRepositoryFile(
                "tools",
                "mobile",
                "Build-GeoraePlanAndroidApk.ps1")
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("function New-AndroidAotStagingContext", source, StringComparison.Ordinal);
        Assert.Contains("function Remove-AndroidAotStagingContext", source, StringComparison.Ordinal);
        Assert.Contains("$stagingBaseRoot = 'D:\\gpaot'", source, StringComparison.Ordinal);
        Assert.Contains("foreach ($topLevelDirectoryName in @('Mobile', 'Shared', 'AppIcons'))", source, StringComparison.Ordinal);
        Assert.Contains("if ($item.Name -in @('bin', 'obj', 'signing', 'artifacts'))", source, StringComparison.Ordinal);
        Assert.Contains("android-signing.local.json", source, StringComparison.Ordinal);
        Assert.Contains("android-signing.release.local.json", source, StringComparison.Ordinal);
        Assert.Contains("android_aot_staging=enabled", source, StringComparison.Ordinal);
        Assert.Contains("android_aot_staging=skipped_no_restore", source, StringComparison.Ordinal);
        Assert.Contains("android_aot_staging=failed_prepare", source, StringComparison.Ordinal);
        Assert.Contains("android_aot_staging_cleanup=success", source, StringComparison.Ordinal);
        Assert.Contains("Android AOT staging cleanup failed after retries", source, StringComparison.Ordinal);
        Assert.Contains("android_profiled_aot_fallback=known_response_file_failure", source, StringComparison.Ordinal);
        Assert.Contains("-p:UseSharedCompilation=false", source, StringComparison.Ordinal);
        Assert.Contains("-nodeReuse:false", source, StringComparison.Ordinal);
        Assert.Contains("android_aot_staging_compiler_reuse=false", source, StringComparison.Ordinal);
        Assert.Contains("-WorkingDirectory $publishWorkingDirectory", source, StringComparison.Ordinal);
        Assert.Contains("-TemporaryDirectory $publishTemporaryDirectory", source, StringComparison.Ordinal);
        Assert.DoesNotContain("[System.IO.Path]::GetRelativePath", source, StringComparison.Ordinal);
        AssertInOrder(
            source,
            "$shouldEnableAot = $isReleaseBuild -and -not $DisableAot.IsPresent",
            "$stagingContext = New-AndroidAotStagingContext",
            "$arguments = @(",
            "$publishWorkingDirectory = [string]$stagingContext.WorkingDirectory",
            "$publishResult = Invoke-DotnetPublishAndRelay",
            "Remove-AndroidAotStagingContext -Context $stagingContext");
    }

    [Fact]
    public async Task AndroidAotStagingContext_CopiesOnlyRequiredTreesAndCleansUpSafely()
    {
        var repositoryRoot = FindRepositoryRoot();
        var testRoot = Path.Combine(
            repositoryRoot,
            "temp",
            "android-aot-staging-tests",
            Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(testRoot, "원본-root");
        var projectFile = Path.Combine(projectRoot, "Mobile", "GeoraePlan.Mobile.App", "GeoraePlan.Mobile.App.csproj");

        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "Mobile", "GeoraePlan.Mobile.App", "Resources", "Images"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "Mobile", "GeoraePlan.Mobile.App", "bin", "Release"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "Mobile", "GeoraePlan.Mobile.App", "obj", "Release"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "Mobile", "GeoraePlan.Mobile.App", "signing"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "Mobile", "artifacts", "android"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "Shared", "거래플랜.Shared.Contracts", "Models"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "Shared", "거래플랜.Shared.Contracts", "obj"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "AppIcons", "android", "mipmap-mdpi"));

            File.WriteAllText(projectFile, "<Project />", Encoding.UTF8);
            File.WriteAllText(Path.Combine(projectRoot, "Mobile", "GeoraePlan.Mobile.App", "Resources", "Images", "keep.txt"), "keep", Encoding.UTF8);
            File.WriteAllText(Path.Combine(projectRoot, "Mobile", "GeoraePlan.Mobile.App", "bin", "Release", "skip.txt"), "skip", Encoding.UTF8);
            File.WriteAllText(Path.Combine(projectRoot, "Mobile", "GeoraePlan.Mobile.App", "obj", "Release", "skip.txt"), "skip", Encoding.UTF8);
            File.WriteAllText(Path.Combine(projectRoot, "Mobile", "GeoraePlan.Mobile.App", "signing", "release.keystore"), "secret", Encoding.UTF8);
            File.WriteAllText(Path.Combine(projectRoot, "Mobile", "artifacts", "android", "old.apk"), "artifact", Encoding.UTF8);
            File.WriteAllText(Path.Combine(projectRoot, "Mobile", "GeoraePlan.Mobile.App", "android-signing.local.json"), "{}", Encoding.UTF8);
            File.WriteAllText(Path.Combine(projectRoot, "Shared", "거래플랜.Shared.Contracts", "거래플랜.Shared.Contracts.csproj"), "<Project />", Encoding.UTF8);
            File.WriteAllText(Path.Combine(projectRoot, "Shared", "거래플랜.Shared.Contracts", "Models", "keep.txt"), "keep", Encoding.UTF8);
            File.WriteAllText(Path.Combine(projectRoot, "Shared", "거래플랜.Shared.Contracts", "obj", "skip.txt"), "skip", Encoding.UTF8);
            File.WriteAllText(Path.Combine(projectRoot, "AppIcons", "android", "mipmap-mdpi", "ic_launcher.png"), "icon", Encoding.UTF8);

            var source = ReadRepositoryFile(
                "tools",
                "mobile",
                "Build-GeoraePlanAndroidApk.ps1");
            var testScriptPath = Path.Combine(testRoot, "run-android-aot-staging.ps1");
            var script = ExtractPowerShellScriptSection(
                             source,
                             "function Test-PathContainsNonAscii",
                             "function Get-ResolvedDotNetPath") +
                         Environment.NewLine +
                         $"$ctx = New-AndroidAotStagingContext -ProjectRoot '{EscapePowerShellSingleQuotedLiteral(projectRoot)}' -ProjectFile '{EscapePowerShellSingleQuotedLiteral(projectFile)}' -ShouldEnableAot $true -NoRestoreRequested:$false" + Environment.NewLine +
                         "Write-Host \"ctx_enabled=$($ctx.Enabled)\"" + Environment.NewLine +
                         "if (-not $ctx.Enabled) { exit 91 }" + Environment.NewLine +
                         "$stagingRoot = [string]$ctx.StagingRoot" + Environment.NewLine +
                         "Write-Host \"staged_keep_mobile=$(Test-Path -LiteralPath (Join-Path $stagingRoot 'Mobile\\GeoraePlan.Mobile.App\\Resources\\Images\\keep.txt'))\"" + Environment.NewLine +
                         "Write-Host \"staged_keep_shared=$(Test-Path -LiteralPath (Join-Path $stagingRoot 'Shared\\거래플랜.Shared.Contracts\\Models\\keep.txt'))\"" + Environment.NewLine +
                         "Write-Host \"staged_keep_appicon=$(Test-Path -LiteralPath (Join-Path $stagingRoot 'AppIcons\\android\\mipmap-mdpi\\ic_launcher.png'))\"" + Environment.NewLine +
                         "Write-Host \"staged_skip_bin=$(Test-Path -LiteralPath (Join-Path $stagingRoot 'Mobile\\GeoraePlan.Mobile.App\\bin\\Release\\skip.txt'))\"" + Environment.NewLine +
                         "Write-Host \"staged_skip_obj=$(Test-Path -LiteralPath (Join-Path $stagingRoot 'Mobile\\GeoraePlan.Mobile.App\\obj\\Release\\skip.txt'))\"" + Environment.NewLine +
                         "Write-Host \"staged_skip_signing=$(Test-Path -LiteralPath (Join-Path $stagingRoot 'Mobile\\GeoraePlan.Mobile.App\\signing\\release.keystore'))\"" + Environment.NewLine +
                         "Write-Host \"staged_skip_artifacts=$(Test-Path -LiteralPath (Join-Path $stagingRoot 'Mobile\\artifacts\\android\\old.apk'))\"" + Environment.NewLine +
                         "Write-Host \"staged_skip_signing_json=$(Test-Path -LiteralPath (Join-Path $stagingRoot 'Mobile\\GeoraePlan.Mobile.App\\android-signing.local.json'))\"" + Environment.NewLine +
                         "Write-Host \"staging_temp_ascii=$($ctx.TemporaryDirectory -eq (Join-Path $stagingRoot 'tmp'))\"" + Environment.NewLine +
                         "Remove-AndroidAotStagingContext -Context $ctx" + Environment.NewLine +
                         "Write-Host \"ctx_staging_exists_after_cleanup=$(Test-Path -LiteralPath $stagingRoot)\"" + Environment.NewLine;
            File.WriteAllText(testScriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var result = await RunPowerShellAsync(testScriptPath);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("android_aot_staging=enabled", result.StdOut, StringComparison.Ordinal);
            Assert.Contains("ctx_enabled=True", result.StdOut, StringComparison.Ordinal);
            Assert.Contains("staged_keep_mobile=True", result.StdOut, StringComparison.Ordinal);
            Assert.Contains("staged_keep_shared=True", result.StdOut, StringComparison.Ordinal);
            Assert.Contains("staged_keep_appicon=True", result.StdOut, StringComparison.Ordinal);
            Assert.Contains("staged_skip_bin=False", result.StdOut, StringComparison.Ordinal);
            Assert.Contains("staged_skip_obj=False", result.StdOut, StringComparison.Ordinal);
            Assert.Contains("staged_skip_signing=False", result.StdOut, StringComparison.Ordinal);
            Assert.Contains("staged_skip_artifacts=False", result.StdOut, StringComparison.Ordinal);
            Assert.Contains("staged_skip_signing_json=False", result.StdOut, StringComparison.Ordinal);
            Assert.Contains("staging_temp_ascii=True", result.StdOut, StringComparison.Ordinal);
            Assert.Contains("android_aot_staging_cleanup=success", result.StdOut, StringComparison.Ordinal);
            Assert.Contains("ctx_staging_exists_after_cleanup=False", result.StdOut, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task AndroidAotStagingContext_SkipsFilteredStagingWhenNoRestoreIsRequested()
    {
        var repositoryRoot = FindRepositoryRoot();
        var testRoot = Path.Combine(
            repositoryRoot,
            "temp",
            "android-aot-staging-no-restore-tests",
            Guid.NewGuid().ToString("N"));
        var projectRoot = Path.Combine(testRoot, "원본-root");
        var projectFile = Path.Combine(projectRoot, "Mobile", "GeoraePlan.Mobile.App", "GeoraePlan.Mobile.App.csproj");

        try
        {
            Directory.CreateDirectory(Path.Combine(projectRoot, "Mobile", "GeoraePlan.Mobile.App"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "Shared", "거래플랜.Shared.Contracts"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "AppIcons", "android"));
            File.WriteAllText(projectFile, "<Project />", Encoding.UTF8);

            var source = ReadRepositoryFile(
                "tools",
                "mobile",
                "Build-GeoraePlanAndroidApk.ps1");
            var testScriptPath = Path.Combine(testRoot, "run-android-aot-staging-no-restore.ps1");
            var script = ExtractPowerShellScriptSection(
                             source,
                             "function Test-PathContainsNonAscii",
                             "function Get-ResolvedDotNetPath") +
                         Environment.NewLine +
                         $"$ctx = New-AndroidAotStagingContext -ProjectRoot '{EscapePowerShellSingleQuotedLiteral(projectRoot)}' -ProjectFile '{EscapePowerShellSingleQuotedLiteral(projectFile)}' -ShouldEnableAot $true -NoRestoreRequested:$true" + Environment.NewLine +
                         "Write-Host \"ctx_enabled=$($ctx.Enabled)\"" + Environment.NewLine +
                         "Write-Host \"ctx_staging_root=$([string]$ctx.StagingRoot)\"" + Environment.NewLine;
            File.WriteAllText(testScriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var result = await RunPowerShellAsync(testScriptPath);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("android_aot_staging=skipped_no_restore", result.StdOut, StringComparison.Ordinal);
            Assert.Contains("ctx_enabled=False", result.StdOut, StringComparison.Ordinal);
            Assert.Contains("ctx_staging_root=", result.StdOut, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }

    private static async Task<ProcessResult> RunPowerShellAsync(string scriptPath)
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

    private static string ExtractPowerShellScriptSection(string source, string startToken, string endToken)
    {
        var start = source.IndexOf(startToken, StringComparison.Ordinal);
        var end = source.IndexOf(endToken, start, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Start token was not found: {startToken}");
        Assert.True(end > start, $"End token was not found after start token: {endToken}");
        return source[start..end];
    }

    private static string EscapePowerShellSingleQuotedLiteral(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

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
                Directory.Exists(Path.Combine(directory.FullName, "Mobile")) &&
                Directory.Exists(Path.Combine(directory.FullName, "tools")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);
}
