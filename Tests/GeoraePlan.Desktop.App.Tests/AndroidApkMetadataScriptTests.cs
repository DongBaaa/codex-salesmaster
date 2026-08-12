using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class AndroidApkMetadataScriptTests
{
    [Fact]
    public async Task GetMetadata_ReturnsValidatedIdentityVersionsHashAndSize()
    {
        using var fixture = new ApkMetadataFixture();
        var result = await fixture.RunPowerShellAsync(
            "success",
            """
            $metadata = Get-GeoraePlanAndroidApkMetadata `
                -ApkPath $apkPath `
                -ProjectRoot $projectRoot `
                -ApkAnalyzerPath $analyzerPath `
                -JavaSdkDirectory $javaRoot `
                -SourceName 'fixture'
            Assert-GeoraePlanAndroidApkMetadata `
                -Metadata $metadata `
                -ExpectedApplicationId 'kr.georaeplan.mobile' `
                -ExpectedVersionName '0.2.81' `
                -ExpectedVersionCode 192 `
                -SourceName 'fixture'
            $metadata | ConvertTo-Json -Compress
            """);

        Assert.Equal(0, result.ExitCode);
        var jsonLine = result.StdOut
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Last();
        using var metadataDocument = JsonDocument.Parse(jsonLine);
        var metadata = metadataDocument.RootElement;

        Assert.Equal(
            "kr.georaeplan.mobile",
            metadata.GetProperty("ApplicationId").GetString());
        Assert.Equal("0.2.81", metadata.GetProperty("VersionName").GetString());
        Assert.Equal(192, metadata.GetProperty("VersionCode").GetInt64());
        Assert.Equal(
            fixture.ApkBytes.LongLength,
            metadata.GetProperty("FileSize").GetInt64());
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(fixture.ApkBytes)),
            metadata.GetProperty("Sha256").GetString(),
            ignoreCase: true);
    }

    [Fact]
    public async Task AssertMetadata_RejectsIdentityVersionNameAndVersionCodeMismatches()
    {
        using var fixture = new ApkMetadataFixture();
        var result = await fixture.RunPowerShellAsync(
            "success",
            """
            $metadata = Get-GeoraePlanAndroidApkMetadata `
                -ApkPath $apkPath `
                -ProjectRoot $projectRoot `
                -ApkAnalyzerPath $analyzerPath `
                -JavaSdkDirectory $javaRoot `
                -SourceName 'fixture'

            function Assert-ExpectedFailure {
                param(
                    [Parameter(Mandatory = $true)][string]$Name,
                    [Parameter(Mandatory = $true)][string]$ExpectedMessage,
                    [Parameter(Mandatory = $true)][scriptblock]$Action
                )

                try {
                    & $Action
                }
                catch {
                    if ($_.Exception.Message.IndexOf(
                            $ExpectedMessage,
                            [StringComparison]::OrdinalIgnoreCase) -lt 0) {
                        throw
                    }
                    Write-Output "$Name=PASS"
                    return
                }

                throw "Expected failure was not raised: $Name"
            }

            Assert-ExpectedFailure `
                -Name 'application_id_mismatch' `
                -ExpectedMessage 'applicationId mismatch' `
                -Action {
                    Assert-GeoraePlanAndroidApkMetadata `
                        -Metadata $metadata `
                        -ExpectedApplicationId 'kr.georaeplan.other' `
                        -ExpectedVersionName '0.2.81' `
                        -ExpectedVersionCode 192 `
                        -SourceName 'fixture'
                }
            Assert-ExpectedFailure `
                -Name 'version_name_mismatch' `
                -ExpectedMessage 'versionName mismatch' `
                -Action {
                    Assert-GeoraePlanAndroidApkMetadata `
                        -Metadata $metadata `
                        -ExpectedApplicationId 'kr.georaeplan.mobile' `
                        -ExpectedVersionName '0.2.82' `
                        -ExpectedVersionCode 192 `
                        -SourceName 'fixture'
                }
            Assert-ExpectedFailure `
                -Name 'version_code_mismatch' `
                -ExpectedMessage 'versionCode mismatch' `
                -Action {
                    Assert-GeoraePlanAndroidApkMetadata `
                        -Metadata $metadata `
                        -ExpectedApplicationId 'kr.georaeplan.mobile' `
                        -ExpectedVersionName '0.2.81' `
                        -ExpectedVersionCode 193 `
                        -SourceName 'fixture'
                }
            """);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("application_id_mismatch=PASS", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("version_name_mismatch=PASS", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("version_code_mismatch=PASS", result.StdOut, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        "empty-application-id",
        "application-id must produce exactly one non-empty output value")]
    [InlineData(
        "multi-version-name",
        "version-name must produce exactly one non-empty output value")]
    [InlineData("exit-version-code", "version-code failed")]
    [InlineData("zero-version-code", "versionCode must be a positive integer")]
    public async Task GetMetadata_FailsClosedForMalformedAnalyzerResults(
        string analyzerMode,
        string expectedMessage)
    {
        using var fixture = new ApkMetadataFixture();
        var result = await fixture.RunPowerShellAsync(
            analyzerMode,
            """
            Get-GeoraePlanAndroidApkMetadata `
                -ApkPath $apkPath `
                -ProjectRoot $projectRoot `
                -ApkAnalyzerPath $analyzerPath `
                -JavaSdkDirectory $javaRoot `
                -SourceName 'fixture' | Out-Null
            """);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            expectedMessage,
            result.StdErr + result.StdOut,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MetadataToolchain_FailsClosedForMissingToolsAndInvalidApkFiles()
    {
        using var fixture = new ApkMetadataFixture();
        var result = await fixture.RunPowerShellAsync(
            "success",
            """
            function Assert-ExpectedFailure {
                param(
                    [Parameter(Mandatory = $true)][string]$Name,
                    [Parameter(Mandatory = $true)][string]$ExpectedMessage,
                    [Parameter(Mandatory = $true)][scriptblock]$Action
                )

                try {
                    & $Action
                }
                catch {
                    if ($_.Exception.Message.IndexOf(
                            $ExpectedMessage,
                            [StringComparison]::OrdinalIgnoreCase) -lt 0) {
                        throw
                    }
                    Write-Output "$Name=PASS"
                    return
                }

                throw "Expected failure was not raised: $Name"
            }

            $emptyApk = Join-Path $projectRoot 'empty.apk'
            Set-Content -LiteralPath $emptyApk -Value ([byte[]]@()) -Encoding Byte

            Assert-ExpectedFailure `
                -Name 'missing_analyzer' `
                -ExpectedMessage 'apkanalyzer not found at requested path' `
                -Action {
                    Resolve-GeoraePlanApkAnalyzerPath `
                        -ProjectRoot $projectRoot `
                        -RequestedPath (Join-Path $projectRoot 'missing-analyzer.cmd')
                }
            Assert-ExpectedFailure `
                -Name 'missing_java' `
                -ExpectedMessage 'Java SDK not found at requested path' `
                -Action {
                    Resolve-GeoraePlanJavaHome `
                        -RequestedPath (Join-Path $projectRoot 'missing-java')
                }
            Assert-ExpectedFailure `
                -Name 'apk_directory' `
                -ExpectedMessage 'must be an existing leaf file' `
                -Action {
                    Get-GeoraePlanAndroidApkMetadata `
                        -ApkPath $projectRoot `
                        -ProjectRoot $projectRoot `
                        -ApkAnalyzerPath $analyzerPath `
                        -JavaSdkDirectory $javaRoot `
                        -SourceName 'fixture'
                }
            Assert-ExpectedFailure `
                -Name 'empty_apk' `
                -ExpectedMessage 'must be non-empty' `
                -Action {
                    Get-GeoraePlanAndroidApkMetadata `
                        -ApkPath $emptyApk `
                        -ProjectRoot $projectRoot `
                        -ApkAnalyzerPath $analyzerPath `
                        -JavaSdkDirectory $javaRoot `
                        -SourceName 'fixture'
                }
            """);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("missing_analyzer=PASS", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("missing_java=PASS", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("apk_directory=PASS", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("empty_apk=PASS", result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SnapshotLease_BlocksWriteDeleteAndReplacementUntilOwnedCleanup()
    {
        using var fixture = new ApkMetadataFixture();
        var result = await fixture.RunPowerShellAsync(
            "success",
            """
            $snapshot = New-GeoraePlanAndroidApkSnapshot `
                -ApkPath $apkPath `
                -ProjectRoot $projectRoot `
                -ApkAnalyzerPath $analyzerPath `
                -JavaSdkDirectory $javaRoot `
                -SourceName 'leased fixture'
            $snapshotRoot = $snapshot.SnapshotRoot
            try {
                if ([string]::IsNullOrWhiteSpace(
                    [string]$snapshot.FileIdentity)) {
                    throw 'Snapshot file identity was not returned.'
                }
                Assert-GeoraePlanAndroidApkSnapshot `
                    -Snapshot $snapshot `
                    -SourceName 'leased fixture'
                foreach ($operation in @('write', 'delete', 'replace')) {
                    try {
                        if ($operation -eq 'write') {
                            $writer = [IO.File]::Open(
                                $snapshot.SnapshotPath,
                                [IO.FileMode]::Open,
                                [IO.FileAccess]::Write,
                                [IO.FileShare]::None)
                            $writer.Dispose()
                        }
                        elseif ($operation -eq 'delete') {
                            [IO.File]::Delete($snapshot.SnapshotPath)
                        }
                        else {
                            $replacement = Join-Path $snapshotRoot 'replacement.apk'
                            [IO.File]::WriteAllText($replacement, 'replacement')
                            [IO.File]::Replace(
                                $replacement,
                                $snapshot.SnapshotPath,
                                (Join-Path $snapshotRoot 'replacement.bak'),
                                $true)
                        }
                        throw "Snapshot operation unexpectedly succeeded: $operation"
                    }
                    catch {
                        if ($_.Exception.Message -like
                            'Snapshot operation unexpectedly succeeded:*') {
                            throw
                        }
                        Write-Output "$operation=BLOCKED"
                    }
                }
                Assert-GeoraePlanAndroidApkSnapshot `
                    -Snapshot $snapshot `
                    -SourceName 'leased fixture after probes'
            }
            finally {
                foreach ($probeName in @(
                    'replacement.apk',
                    'replacement.bak'
                )) {
                    $probePath = Join-Path $snapshotRoot $probeName
                    if (Test-Path -LiteralPath $probePath -PathType Leaf) {
                        [IO.File]::Delete($probePath)
                    }
                }
                Remove-GeoraePlanAndroidApkSnapshot -Snapshot $snapshot
            }
            if (Test-Path -LiteralPath $snapshotRoot) {
                throw 'Owned snapshot root was not removed after lease disposal.'
            }
            $remainingSnapshotRoots = @(
                Get-ChildItem `
                    -LiteralPath ([IO.Path]::GetTempPath()) `
                    -Directory `
                    -Filter 'georaeplan-android-apk-*' `
                    -Force)
            if ($remainingSnapshotRoots.Count -ne 0) {
                throw 'Owned snapshot temp root remained after clean cleanup.'
            }
            """);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("write=BLOCKED", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("delete=BLOCKED", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("replace=BLOCKED", result.StdOut, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("file")]
    [InlineData("junction")]
    public async Task SnapshotCleanup_RejectsUnexpectedSiblingWithoutDeletingAnything(
        string siblingKind)
    {
        using var fixture = new ApkMetadataFixture();
        var result = await fixture.RunPowerShellAsync(
            "success",
            $$"""
            $snapshot = New-GeoraePlanAndroidApkSnapshot `
                -ApkPath $apkPath `
                -ProjectRoot $projectRoot `
                -ApkAnalyzerPath $analyzerPath `
                -JavaSdkDirectory $javaRoot `
                -SourceName 'unexpected sibling fixture'
            $snapshotRoot = $snapshot.SnapshotRoot
            $snapshotPath = $snapshot.SnapshotPath
            $externalTarget = Join-Path $projectRoot 'external-target'
            [IO.Directory]::CreateDirectory($externalTarget) | Out-Null
            $externalMarker = Join-Path $externalTarget 'preserve.txt'
            [IO.File]::WriteAllText($externalMarker, 'preserve')
            $unexpectedPath = if ('{{siblingKind}}' -eq 'junction') {
                Join-Path $snapshotRoot 'unexpected-junction'
            }
            else {
                Join-Path $snapshotRoot 'unexpected.txt'
            }
            try {
                if ('{{siblingKind}}' -eq 'junction') {
                    $junctionOutput = & cmd.exe /d /c (
                        'mklink /J "' + $unexpectedPath + '" "' +
                        $externalTarget + '"') 2>&1
                    if ($LASTEXITCODE -ne 0) {
                        throw (
                            'Failed to create cleanup fixture junction: ' +
                            ($junctionOutput -join [Environment]::NewLine))
                    }
                }
                else {
                    [IO.File]::WriteAllText($unexpectedPath, 'unexpected')
                }

                $cleanupBlocked = $false
                try {
                    Remove-GeoraePlanAndroidApkSnapshot -Snapshot $snapshot
                }
                catch {
                    $cleanupBlocked = $true
                    Write-Output 'cleanup=BLOCKED'
                }
                if (-not $cleanupBlocked) {
                    throw 'Snapshot cleanup accepted an unexpected sibling.'
                }
                if (
                    -not (Test-Path -LiteralPath $snapshotRoot -PathType Container) -or
                    -not (Test-Path -LiteralPath $snapshotPath -PathType Leaf) -or
                    -not (Test-Path -LiteralPath $unexpectedPath) -or
                    -not (Test-Path -LiteralPath $externalMarker -PathType Leaf)
                ) {
                    throw 'Rejected cleanup changed snapshot or external content.'
                }
            }
            finally {
                if (Test-Path -LiteralPath $unexpectedPath) {
                    if ('{{siblingKind}}' -eq 'junction') {
                        [IO.Directory]::Delete($unexpectedPath, $false)
                    }
                    else {
                        [IO.File]::Delete($unexpectedPath)
                    }
                }
                Remove-GeoraePlanAndroidApkSnapshot -Snapshot $snapshot
            }
            if (
                (Test-Path -LiteralPath $snapshotRoot) -or
                -not (Test-Path -LiteralPath $externalMarker -PathType Leaf)
            ) {
                throw 'Fixture cleanup did not preserve only the external target.'
            }
            """);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("cleanup=BLOCKED", result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SnapshotCleanup_SurfacesReaderLockAndSucceedsAfterRelease()
    {
        using var fixture = new ApkMetadataFixture();
        var result = await fixture.RunPowerShellAsync(
            "success",
            """
            $snapshot = New-GeoraePlanAndroidApkSnapshot `
                -ApkPath $apkPath `
                -ProjectRoot $projectRoot `
                -ApkAnalyzerPath $analyzerPath `
                -JavaSdkDirectory $javaRoot `
                -SourceName 'reader lock fixture'
            $snapshotRoot = $snapshot.SnapshotRoot
            $reader = [IO.File]::Open(
                $snapshot.SnapshotPath,
                [IO.FileMode]::Open,
                [IO.FileAccess]::Read,
                [IO.FileShare]::Read)
            try {
                $cleanupBlocked = $false
                try {
                    Remove-GeoraePlanAndroidApkSnapshot -Snapshot $snapshot
                }
                catch {
                    $cleanupBlocked = $true
                    Write-Output 'reader_cleanup=BLOCKED'
                }
                if (-not $cleanupBlocked) {
                    throw 'Snapshot cleanup hid the candidate reader lock.'
                }
                if (
                    -not (Test-Path -LiteralPath $snapshotRoot -PathType Container) -or
                    -not (Test-Path -LiteralPath $snapshot.SnapshotPath -PathType Leaf)
                ) {
                    throw 'Failed cleanup changed the locked snapshot.'
                }
            }
            finally {
                $reader.Dispose()
                Remove-GeoraePlanAndroidApkSnapshot -Snapshot $snapshot
            }
            if (Test-Path -LiteralPath $snapshotRoot) {
                throw 'Snapshot cleanup failed after the reader lock was released.'
            }
            """);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            "reader_cleanup=BLOCKED",
            result.StdOut,
            StringComparison.Ordinal);
    }

    private sealed class ApkMetadataFixture : IDisposable
    {
        public ApkMetadataFixture()
        {
            RepositoryRoot = FindRepositoryRoot();
            Root = Path.Combine(
                Path.GetTempPath(),
                "georaeplan-apk-metadata-tests",
                Guid.NewGuid().ToString("N"));
            ProjectRoot = Path.Combine(Root, "project");
            ApkPath = Path.Combine(ProjectRoot, "fixture.apk");
            AnalyzerPath = Path.Combine(Root, "fake-apkanalyzer.cmd");
            JavaRoot = Path.Combine(Root, "fake-java");
            SnapshotTempRoot = Path.Combine(Root, "snapshot-temp");
            HelperPath = Path.Combine(
                RepositoryRoot,
                "tools",
                "mobile",
                "AndroidApkMetadata.ps1");
            ApkBytes = Encoding.UTF8.GetBytes(
                "georaeplan deterministic dummy apk payload");

            Directory.CreateDirectory(ProjectRoot);
            Directory.CreateDirectory(Path.Combine(JavaRoot, "bin"));
            Directory.CreateDirectory(SnapshotTempRoot);
            File.WriteAllBytes(ApkPath, ApkBytes);
            File.WriteAllBytes(Path.Combine(JavaRoot, "bin", "java.exe"), [0x4D, 0x5A]);
            File.WriteAllText(
                AnalyzerPath,
                """
                @echo off
                setlocal EnableExtensions
                set "APK_COMMAND=%~1|%~2"

                if /I "%FAKE_APK_ANALYZER_MODE%"=="exit-version-code" if /I "%APK_COMMAND%"=="manifest|version-code" exit /b 73

                if /I "%APK_COMMAND%"=="manifest|application-id" (
                  if /I "%FAKE_APK_ANALYZER_MODE%"=="empty-application-id" exit /b 0
                  echo kr.georaeplan.mobile
                  exit /b 0
                )

                if /I "%APK_COMMAND%"=="manifest|version-name" (
                  if /I "%FAKE_APK_ANALYZER_MODE%"=="multi-version-name" (
                    echo 0.2.81
                    echo 0.2.82
                    exit /b 0
                  )
                  echo 0.2.81
                  exit /b 0
                )

                if /I "%APK_COMMAND%"=="manifest|version-code" (
                  if /I "%FAKE_APK_ANALYZER_MODE%"=="zero-version-code" (
                    echo 0
                    exit /b 0
                  )
                  echo 192
                  exit /b 0
                )

                1>&2 echo unsupported fake analyzer command: %*
                exit /b 91
                """,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        public string RepositoryRoot { get; }
        public string Root { get; }
        public string ProjectRoot { get; }
        public string ApkPath { get; }
        public string AnalyzerPath { get; }
        public string JavaRoot { get; }
        public string SnapshotTempRoot { get; }
        public string HelperPath { get; }
        public byte[] ApkBytes { get; }

        public async Task<ProcessResult> RunPowerShellAsync(
            string analyzerMode,
            string body)
        {
            var harnessPath = Path.Combine(
                Root,
                $"metadata-harness-{Guid.NewGuid():N}.ps1");
            var harness =
                "$ErrorActionPreference = 'Stop'" + Environment.NewLine +
                $". '{EscapePowerShellSingleQuotedLiteral(HelperPath)}'" + Environment.NewLine +
                $"$apkPath = '{EscapePowerShellSingleQuotedLiteral(ApkPath)}'" + Environment.NewLine +
                $"$projectRoot = '{EscapePowerShellSingleQuotedLiteral(ProjectRoot)}'" + Environment.NewLine +
                $"$analyzerPath = '{EscapePowerShellSingleQuotedLiteral(AnalyzerPath)}'" + Environment.NewLine +
                $"$javaRoot = '{EscapePowerShellSingleQuotedLiteral(JavaRoot)}'" + Environment.NewLine +
                $"$env:TEMP = '{EscapePowerShellSingleQuotedLiteral(SnapshotTempRoot)}'" + Environment.NewLine +
                $"$env:TMP = '{EscapePowerShellSingleQuotedLiteral(SnapshotTempRoot)}'" + Environment.NewLine +
                $"$env:FAKE_APK_ANALYZER_MODE = '{EscapePowerShellSingleQuotedLiteral(analyzerMode)}'" + Environment.NewLine +
                body + Environment.NewLine;
            File.WriteAllText(
                harnessPath,
                harness,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

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
            process.StartInfo.ArgumentList.Add(harnessPath);

            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            var exited = process.WaitForExit(30_000);
            Assert.True(exited, $"PowerShell script timed out: {harnessPath}");

            return new ProcessResult(
                process.ExitCode,
                await stdoutTask,
                await stderrTask);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
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
