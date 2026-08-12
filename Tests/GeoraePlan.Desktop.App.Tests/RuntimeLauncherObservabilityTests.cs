using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RuntimeLauncherObservabilityTests
{
    [Fact]
    public async Task GeneratedRunAll_PreservesSeparateServerStreamsAndRotatesHealthObservations()
    {
        const string stdoutSentinel = "GEORAEPLAN_OBSERVABILITY_STDOUT_SENTINEL";
        const string stderrSentinel = "GEORAEPLAN_OBSERVABILITY_STDERR_SENTINEL";

        var testRoot = Path.Combine(
            TestProcessIsolation.TempRoot,
            $"runtime-observability-{Guid.NewGuid():N}");
        var runtimeRoot = Path.Combine(testRoot, "runtime");
        var harnessPath = Path.Combine(testRoot, "runtime-observability-harness.ps1");
        var fakeServerPath = Path.Combine(testRoot, "fake-server.ps1");
        var noisyServerPath = Path.Combine(testRoot, "noisy-server.ps1");
        var resultPath = Path.Combine(testRoot, "result.json");
        var powerShellPath = ResolveWindowsPowerShellPath();
        Directory.CreateDirectory(runtimeRoot);

        try
        {
            var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            File.WriteAllText(
                fakeServerPath,
                $$"""
                param([string]$environment)

                [Console]::Out.WriteLine('{{stdoutSentinel}}')
                [Console]::Error.WriteLine('{{stderrSentinel}}')
                exit 0
                """,
                utf8NoBom);
            File.WriteAllText(
                noisyServerPath,
                """
                param([string]$environment)

                $chunk = 'X'.PadRight(4096, 'X')
                for ($index = 0; $index -lt 512; $index++) {
                    [Console]::Out.Write($chunk)
                }
                [Console]::Out.Flush()
                Start-Sleep -Seconds 10
                """,
                utf8NoBom);
            File.WriteAllText(
                harnessPath,
                """
                [CmdletBinding()]
                param(
                    [Parameter(Mandatory = $true)][string]$SourceScript,
                    [Parameter(Mandatory = $true)][string]$OutputRoot,
                    [Parameter(Mandatory = $true)][string]$PowerShellPath,
                    [Parameter(Mandatory = $true)][string]$FakeServerPath,
                    [Parameter(Mandatory = $true)][string]$NoisyServerPath,
                    [Parameter(Mandatory = $true)][string]$ResultPath
                )

                $ErrorActionPreference = 'Stop'

                function Get-FunctionDefinitionFromAst {
                    param(
                        [Parameter(Mandatory = $true)]
                        [System.Management.Automation.Language.Ast]$Ast,
                        [Parameter(Mandatory = $true)][string]$Name
                    )

                    $functionAst = $Ast.Find({
                        param($node)
                        $node -is
                            [System.Management.Automation.Language.FunctionDefinitionAst] -and
                            $node.Name -eq $Name
                    }, $true)
                    if ($null -eq $functionAst) {
                        throw "$Name function was not found."
                    }
                    return $functionAst.Extent.Text
                }

                function Read-ParsedScript {
                    param([Parameter(Mandatory = $true)][string]$Path)

                    $tokens = $null
                    $parseErrors = $null
                    $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                        $Path,
                        [ref]$tokens,
                        [ref]$parseErrors)
                    if ($parseErrors.Count -ne 0) {
                        throw (($parseErrors | ForEach-Object Message) -join
                            [Environment]::NewLine)
                    }
                    return $ast
                }

                $sourceAst = Read-ParsedScript -Path $SourceScript
                foreach ($functionName in @(
                    'New-Utf8NoBomEncoding',
                    'New-Utf8BomEncoding',
                    'Write-Utf8File',
                    'Set-RuntimeInvalidationMarker',
                    'Write-TestRunScripts'
                )) {
                    . ([scriptblock]::Create(
                        (Get-FunctionDefinitionFromAst `
                            -Ast $sourceAst `
                            -Name $functionName)))
                }

                Write-TestRunScripts `
                    -OutputRoot $OutputRoot `
                    -DefaultBaseUrl 'http://127.0.0.1:19081' `
                    -DotnetExe $PowerShellPath `
                    -CertificationId 'runtime-observability-test' `
                    -CertificationMode 'test' `
                    -PasswordResetCount 0

                $runAllPath = Join-Path $OutputRoot 'Run-All.ps1'
                $runAllSource = Get-Content -LiteralPath $runAllPath -Raw
                $tokens = $null
                $parseErrors = $null
                $runAllAst =
                    [System.Management.Automation.Language.Parser]::ParseFile(
                        $runAllPath,
                        [ref]$tokens,
                        [ref]$parseErrors)
                $parseErrorCount = $parseErrors.Count
                if ($parseErrorCount -ne 0) {
                    throw (($parseErrors | ForEach-Object Message) -join
                        [Environment]::NewLine)
                }

                foreach ($functionName in @(
                    'Repair-ProcessPathEnvironmentForChildProcess',
                    'New-LocalTestPassword',
                    'Start-HiddenServerProcess'
                )) {
                    . ([scriptblock]::Create(
                        (Get-FunctionDefinitionFromAst `
                            -Ast $runAllAst `
                            -Name $functionName)))
                }

                $runtimeLogRoot = Join-Path $OutputRoot 'RuntimeLogs'
                New-Item -ItemType Directory -Force -Path $runtimeLogRoot |
                    Out-Null
                $stdoutLogPath = Join-Path $runtimeLogRoot 'probe.stdout.log'
                $stderrLogPath = Join-Path $runtimeLogRoot 'probe.stderr.log'
                $serverDataRoot = Join-Path $OutputRoot 'ServerData'
                New-Item -ItemType Directory -Force -Path $serverDataRoot |
                    Out-Null

                $process = Start-HiddenServerProcess `
                    -DotnetExe $PowerShellPath `
                    -ServerDir (Split-Path -Parent $FakeServerPath) `
                    -ServerDll $FakeServerPath `
                    -ServerUrl 'http://127.0.0.1:19081' `
                    -ServerDataRoot $serverDataRoot `
                    -AdminPassword 'test-only-admin-password' `
                    -UsenetPassword 'test-only-usenet-password' `
                    -StdoutLogPath $stdoutLogPath `
                    -StderrLogPath $stderrLogPath
                try {
                    if (-not $process.WaitForExit(10000)) {
                        $process.Kill()
                        throw 'The fake server did not exit within ten seconds.'
                    }
                    $process.WaitForExit()
                    $process.Refresh()
                    $childExited = $process.HasExited
                }
                finally {
                    $process.Dispose()
                }

                $stdoutText = [IO.File]::ReadAllText($stdoutLogPath)
                $stderrText = [IO.File]::ReadAllText($stderrLogPath)
                $reopenSucceeded = $false
                $stdoutHandle = $null
                $stderrHandle = $null
                try {
                    $stdoutHandle = [IO.File]::Open(
                        $stdoutLogPath,
                        [IO.FileMode]::Open,
                        [IO.FileAccess]::ReadWrite,
                        [IO.FileShare]::None)
                    $stderrHandle = [IO.File]::Open(
                        $stderrLogPath,
                        [IO.FileMode]::Open,
                        [IO.FileAccess]::ReadWrite,
                        [IO.FileShare]::None)
                    $reopenSucceeded = $true
                }
                finally {
                    if ($null -ne $stderrHandle) {
                        $stderrHandle.Dispose()
                    }
                    if ($null -ne $stdoutHandle) {
                        $stdoutHandle.Dispose()
                    }
                }

                function Assert-SafeRuntimeLogFilePath {
                    param(
                        [Parameter(Mandatory = $true)][string]$LogRoot,
                        [Parameter(Mandatory = $true)][string]$Path
                    )

                    $expectedParent = [IO.Path]::GetFullPath($LogRoot).TrimEnd(
                        [IO.Path]::DirectorySeparatorChar,
                        [IO.Path]::AltDirectorySeparatorChar)
                    $actualParent = [IO.Path]::GetDirectoryName(
                        [IO.Path]::GetFullPath($Path))
                    if (-not [string]::Equals(
                            $expectedParent,
                            $actualParent,
                            [StringComparison]::OrdinalIgnoreCase)) {
                        throw "Health log escaped its test root: $Path"
                    }
                }

                function Write-Log {
                    param([string]$Message)
                }

                foreach ($functionName in @(
                    'Assert-RuntimeServerLogsWithinLimit',
                    'Remove-OldRuntimeServerLogs',
                    'Reset-RuntimeLogFile',
                    'Move-RuntimeLogToPrevious',
                    'Initialize-RuntimeHealthObservationLog',
                    'Write-RuntimeHealthObservation',
                    'Wait-HttpReady'
                )) {
                    . ([scriptblock]::Create(
                        (Get-FunctionDefinitionFromAst `
                            -Ast $runAllAst `
                            -Name $functionName)))
                }

                $totalLimitStdoutLogPath = Join-Path `
                    $runtimeLogRoot `
                    'server-20260726T120000000Z-0123456789abcdef0123456789abcdef-a01.stdout.log'
                $totalLimitStderrLogPath = Join-Path `
                    $runtimeLogRoot `
                    'server-20260726T120000000Z-0123456789abcdef0123456789abcdef-a01.stderr.log'
                $totalLogLimitRejected = $false
                try {
                    [IO.File]::WriteAllBytes(
                        $totalLimitStdoutLogPath,
                        [byte[]]::new(1200000))
                    [IO.File]::WriteAllBytes(
                        $totalLimitStderrLogPath,
                        [byte[]]::new(1200000))
                    try {
                        Assert-RuntimeServerLogsWithinLimit `
                            -LogRoot $runtimeLogRoot `
                            -Paths @(
                                $totalLimitStdoutLogPath,
                                $totalLimitStderrLogPath) `
                            -MaximumBytesPerFile 2097152 `
                            -MaximumTotalBytes 2097152
                    }
                    catch {
                        $totalLogLimitRejected =
                            $_.Exception.Message -match
                                'total safety limit'
                    }
                }
                finally {
                    Remove-Item `
                        -LiteralPath $totalLimitStdoutLogPath `
                        -Force `
                        -ErrorAction SilentlyContinue
                    Remove-Item `
                        -LiteralPath $totalLimitStderrLogPath `
                        -Force `
                        -ErrorAction SilentlyContinue
                }

                $historySentinelPath =
                    Join-Path $runtimeLogRoot 'server-history-sentinel.txt'
                [IO.File]::WriteAllText(
                    $historySentinelPath,
                    'nonmatching files must remain',
                    [Text.UTF8Encoding]::new($false))
                $countHistoryPaths = @()
                $countHistoryRunId =
                    '11111111111111111111111111111111'
                foreach ($index in 1..3) {
                    $historyPath = Join-Path `
                        $runtimeLogRoot `
                        ('server-20260726T13000000{0}Z-{1}-a0{0}.stdout.log' -f
                            $index,
                            $countHistoryRunId)
                    [IO.File]::WriteAllBytes(
                        $historyPath,
                        [byte[]]::new(16))
                    [IO.File]::SetLastWriteTimeUtc(
                        $historyPath,
                        [DateTime]::new(
                            2026,
                            7,
                            26,
                            13,
                            0,
                            $index,
                            [DateTimeKind]::Utc))
                    $countHistoryPaths += $historyPath
                }
                Remove-OldRuntimeServerLogs `
                    -LogRoot $runtimeLogRoot `
                    -MaximumFileCount 2 `
                    -MaximumTotalBytes 1048576
                $historicalLogCountPruneSucceeded =
                    -not (Test-Path -LiteralPath $countHistoryPaths[0]) -and
                    (Test-Path -LiteralPath $countHistoryPaths[1]) -and
                    (Test-Path -LiteralPath $countHistoryPaths[2]) -and
                    (Test-Path -LiteralPath $historySentinelPath)
                Remove-Item `
                    -LiteralPath $countHistoryPaths `
                    -Force `
                    -ErrorAction SilentlyContinue

                $byteHistoryPaths = @()
                $byteHistoryRunId =
                    '22222222222222222222222222222222'
                foreach ($index in 1..3) {
                    $historyPath = Join-Path `
                        $runtimeLogRoot `
                        ('server-20260726T14000000{0}Z-{1}-a0{0}.stderr.log' -f
                            $index,
                            $byteHistoryRunId)
                    [IO.File]::WriteAllBytes(
                        $historyPath,
                        [byte[]]::new(409600))
                    [IO.File]::SetLastWriteTimeUtc(
                        $historyPath,
                        [DateTime]::new(
                            2026,
                            7,
                            26,
                            14,
                            0,
                            $index,
                            [DateTimeKind]::Utc))
                    $byteHistoryPaths += $historyPath
                }
                Remove-OldRuntimeServerLogs `
                    -LogRoot $runtimeLogRoot `
                    -MaximumFileCount 40 `
                    -MaximumTotalBytes 1048576
                $retainedByteHistory = @(
                    Get-ChildItem -LiteralPath $runtimeLogRoot -File |
                        Where-Object {
                            $_.Name -like "*$byteHistoryRunId*"
                        }
                )
                $historicalLogBytePruneSucceeded =
                    -not (Test-Path -LiteralPath $byteHistoryPaths[0]) -and
                    (Test-Path -LiteralPath $byteHistoryPaths[1]) -and
                    (Test-Path -LiteralPath $byteHistoryPaths[2]) -and
                    $retainedByteHistory.Count -eq 2 -and
                    ($retainedByteHistory |
                        Measure-Object -Property Length -Sum).Sum -eq 819200 -and
                    (Test-Path -LiteralPath $historySentinelPath)
                Remove-Item `
                    -LiteralPath $byteHistoryPaths `
                    -Force `
                    -ErrorAction SilentlyContinue
                Remove-Item `
                    -LiteralPath $historySentinelPath `
                    -Force
                $script:actualRemoveOldRuntimeServerLogs =
                    (Get-Command `
                        -Name Remove-OldRuntimeServerLogs `
                        -CommandType Function).ScriptBlock

                $noisyStdoutLogPath =
                    Join-Path $runtimeLogRoot 'noisy.stdout.log'
                $noisyStderrLogPath =
                    Join-Path $runtimeLogRoot 'noisy.stderr.log'
                $noisyProcess = Start-HiddenServerProcess `
                    -DotnetExe $PowerShellPath `
                    -ServerDir (Split-Path -Parent $NoisyServerPath) `
                    -ServerDll $NoisyServerPath `
                    -ServerUrl 'http://127.0.0.1:1' `
                    -ServerDataRoot $serverDataRoot `
                    -AdminPassword 'test-only-admin-password' `
                    -UsenetPassword 'test-only-usenet-password' `
                    -StdoutLogPath $noisyStdoutLogPath `
                    -StderrLogPath $noisyStderrLogPath
                $startupLogLimitRejected = $false
                try {
                    try {
                        Wait-HttpReady `
                            -Url 'http://127.0.0.1:1/healthz' `
                            -TimeoutSeconds 5 `
                            -ServerProcess $noisyProcess `
                            -LogRoot $runtimeLogRoot `
                            -LogPaths @(
                                $noisyStdoutLogPath,
                                $noisyStderrLogPath) `
                            -MaximumLogBytesPerFile 1048576 |
                                Out-Null
                    }
                    catch {
                        $startupLogLimitRejected =
                            $_.Exception.Message -match
                                'exceeded its safety limit'
                    }
                }
                finally {
                    if (-not $noisyProcess.HasExited) {
                        $noisyProcess.Kill()
                    }
                    $noisyProcess.WaitForExit()
                    $noisyProcess.Dispose()
                }

                $healthPath =
                    Join-Path $runtimeLogRoot 'health-observation.csv'
                $previousHealthPath =
                    Join-Path $runtimeLogRoot 'health-observation.previous.csv'
                [IO.File]::WriteAllText(
                    $healthPath,
                    "stale-run-data`r`n",
                    [Text.UTF8Encoding]::new($false))
                Initialize-RuntimeHealthObservationLog `
                    -LogRoot $runtimeLogRoot `
                    -Path $healthPath `
                    -PreviousPath $previousHealthPath
                foreach ($sequence in 1..3) {
                    Write-RuntimeHealthObservation `
                        -LogRoot $runtimeLogRoot `
                        -Path $healthPath `
                        -PreviousPath $previousHealthPath `
                        -ServerPid 4242 `
                        -ServerExited $false `
                        -HealthOk $true `
                        -HttpStatus '200' `
                        -ElapsedMilliseconds $sequence `
                        -ConsecutiveFailures 0 `
                        -MaximumSamplesPerFile 2
                }

                $currentHealthText = [IO.File]::ReadAllText($healthPath)
                $previousHealthText =
                    [IO.File]::ReadAllText($previousHealthPath)
                $healthRotationSucceeded =
                    $previousHealthText -match '(?m)^1,' -and
                    $previousHealthText -match '(?m)^2,' -and
                    $previousHealthText -notmatch '(?m)^3,' -and
                    $currentHealthText -match '(?m)^3,' -and
                    $currentHealthText -notmatch '(?m)^2,'

                $retryLoopAst = $runAllAst.Find({
                    param($node)
                    $node -is
                        [System.Management.Automation.Language.ForStatementAst] -and
                        $node.Extent.Text.Contains(
                            'for ($attempt = 1; $attempt -le 10; $attempt++)') -and
                        $node.Extent.Text.Contains(
                            '$serverProcess = Start-HiddenServerProcess')
                }, $true)
                if ($null -eq $retryLoopAst) {
                    throw 'The generated server retry loop was not found.'
                }

                $script:retryPruneCount = 0
                $script:retryStartCount = 0
                $script:retryAssignedIds = @()
                $script:retryStoppedIds = @()
                $script:retryStartedUrls = @()
                $script:retryConfiguredUrls = @()
                $script:retryMessages = @()

                function Remove-OldRuntimeServerLogs {
                    param(
                        [Parameter(Mandatory = $true)][string]$LogRoot
                    )

                    $script:retryPruneCount++
                    & $script:actualRemoveOldRuntimeServerLogs `
                        -LogRoot $LogRoot `
                        -MaximumFileCount 40 `
                        -MaximumTotalBytes 1048576
                }

                function Get-FreePort {
                    param(
                        [Parameter(Mandatory = $true)]
                        [int]$StartingPort
                    )
                    return $StartingPort
                }

                function Enter-RuntimeCertificationLease {
                    param(
                        [Parameter(Mandatory = $true)][string]$Path
                    )
                    return [IO.MemoryStream]::new()
                }

                function Set-AndVerify-IsolatedApiBaseUrl {
                    param(
                        [Parameter(Mandatory = $true)][string]$BaseUrl,
                        [Parameter(Mandatory = $true)][string]$MarkerPath
                    )
                    $script:retryConfiguredUrls += $BaseUrl
                }

                function Write-Log {
                    param([string]$Message)
                    $script:retryMessages += [string]$Message
                }

                function Start-Sleep {
                    param(
                        [int]$Milliseconds,
                        [int]$Seconds
                    )
                }

                function New-LocalTestPassword {
                    return 'test-only-runtime-password'
                }

                function Start-HiddenServerProcess {
                    param(
                        [string]$DotnetExe,
                        [string]$ServerDir,
                        [string]$ServerDll,
                        [string]$ServerUrl,
                        [string]$ServerDataRoot,
                        [string]$AdminPassword,
                        [string]$UsenetPassword,
                        [string]$StdoutLogPath,
                        [string]$StderrLogPath
                    )

                    $script:retryStartCount++
                    $script:retryStartedUrls += $ServerUrl
                    return [pscustomobject]@{
                        Id = 7000 + $script:retryStartCount
                        HasExited = $script:retryStartCount -lt 3
                        ExitCode = if (
                            $script:retryStartCount -lt 3
                        ) {
                            17 + $script:retryStartCount
                        }
                        else {
                            0
                        }
                    }
                }

                function Stop-AndDisposeRuntimeProcess {
                    param(
                        [Parameter(Mandatory = $true)]$Process,
                        [Parameter(Mandatory = $true)]
                        [string]$Description
                    )
                    $script:retryStoppedIds += [int]$Process.Id
                }

                function Wait-HttpReady {
                    param(
                        [string]$Url,
                        $ServerProcess,
                        [string]$LogRoot,
                        [string[]]$LogPaths
                    )
                    return $script:retryStartCount -ge 3
                }

                $childProcessJob = [pscustomobject]@{}
                $childProcessJob |
                    Add-Member `
                        -MemberType ScriptMethod `
                        -Name AssignProcess `
                        -Value {
                            param($Process)
                            $script:retryAssignedIds += [int]$Process.Id
                        }
                $scanPort = 19080
                $serverReady = $false
                $serverProcess = $null
                $certificationLease = $null
                $certificationLeasePath =
                    Join-Path $OutputRoot '.retry-certification-lease'
                $readyMarkerPath =
                    Join-Path $OutputRoot '.retry-ready-marker'
                $runtimeRunId =
                    '20260726T150000000Z-33333333333333333333333333333333'
                $dotnetExe = $PowerShellPath
                $serverDir = Split-Path -Parent $FakeServerPath
                $serverDll = $FakeServerPath
                $serverDataRoot =
                    Join-Path $OutputRoot 'ServerData'
                $activeServerStdoutLogPath = ''
                $activeServerStderrLogPath = ''

                . ([scriptblock]::Create($retryLoopAst.Extent.Text))

                $retryAttemptLogs = @(
                    Get-ChildItem `
                        -LiteralPath $runtimeLogRoot `
                        -Filter "server-$runtimeRunId-a*.log" `
                        -File
                )
                $retryLoopThirdAttemptSucceeded =
                    $serverReady -and
                    $script:retryStartCount -eq 3 -and
                    $script:retryPruneCount -eq 3 -and
                    $script:retryAssignedIds.Count -eq 3 -and
                    $script:retryStoppedIds.Count -eq 2 -and
                    $script:retryStoppedIds[0] -eq 7001 -and
                    $script:retryStoppedIds[1] -eq 7002 -and
                    $script:retryStartedUrls.Count -eq 3 -and
                    $script:retryStartedUrls[0] -eq
                        'http://127.0.0.1:19080' -and
                    $script:retryStartedUrls[1] -eq
                        'http://127.0.0.1:19081' -and
                    $script:retryStartedUrls[2] -eq
                        'http://127.0.0.1:19082' -and
                    $script:retryConfiguredUrls.Count -eq 3 -and
                    $serverUrl -eq 'http://127.0.0.1:19082' -and
                    $retryAttemptLogs.Count -eq 6 -and
                    $activeServerStdoutLogPath -like '*-a03.stdout.log' -and
                    $activeServerStderrLogPath -like '*-a03.stderr.log'

                Remove-Item -LiteralPath $stdoutLogPath -Force
                Remove-Item -LiteralPath $stderrLogPath -Force
                $deleteSucceeded =
                    -not (Test-Path -LiteralPath $stdoutLogPath) -and
                    -not (Test-Path -LiteralPath $stderrLogPath)

                [ordered]@{
                    ParseErrorCount = $parseErrorCount
                    AspNetCoreWarningConfigured =
                        $runAllSource.Contains(
                            "'Logging__LogLevel__Microsoft.AspNetCore' = 'Warning'")
                    ChildExited = $childExited
                    StdoutText = $stdoutText
                    StderrText = $stderrText
                    ReopenSucceeded = $reopenSucceeded
                    DeleteSucceeded = $deleteSucceeded
                    TotalLogLimitRejected = $totalLogLimitRejected
                    HistoricalLogCountPruneSucceeded =
                        $historicalLogCountPruneSucceeded
                    HistoricalLogBytePruneSucceeded =
                        $historicalLogBytePruneSucceeded
                    StartupLogLimitRejected = $startupLogLimitRejected
                    HealthRotationSucceeded = $healthRotationSucceeded
                    RetryLoopThirdAttemptSucceeded =
                        $retryLoopThirdAttemptSucceeded
                } |
                    ConvertTo-Json |
                    Set-Content -LiteralPath $ResultPath -Encoding UTF8
                """,
                utf8NoBom);

            var processResult = await RunPowerShellAsync(
                powerShellPath,
                harnessPath,
                TimeSpan.FromSeconds(30),
                "-SourceScript",
                ResolvePreparationScript(),
                "-OutputRoot",
                runtimeRoot,
                "-PowerShellPath",
                powerShellPath,
                "-FakeServerPath",
                fakeServerPath,
                "-NoisyServerPath",
                noisyServerPath,
                "-ResultPath",
                resultPath);
            Assert.True(
                processResult.ExitCode == 0,
                "Runtime launcher observability probe failed." +
                Environment.NewLine +
                "STDOUT:" + Environment.NewLine +
                processResult.Stdout +
                Environment.NewLine +
                "STDERR:" + Environment.NewLine +
                processResult.Stderr);

            using var resultDocument = JsonDocument.Parse(
                await File.ReadAllTextAsync(resultPath));
            var result = resultDocument.RootElement;
            Assert.Equal(0, result.GetProperty("ParseErrorCount").GetInt32());
            Assert.True(
                result.GetProperty("AspNetCoreWarningConfigured").GetBoolean());
            Assert.True(result.GetProperty("ChildExited").GetBoolean());
            Assert.Contains(
                stdoutSentinel,
                result.GetProperty("StdoutText").GetString(),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                stderrSentinel,
                result.GetProperty("StdoutText").GetString(),
                StringComparison.Ordinal);
            Assert.Contains(
                stderrSentinel,
                result.GetProperty("StderrText").GetString(),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                stdoutSentinel,
                result.GetProperty("StderrText").GetString(),
                StringComparison.Ordinal);
            Assert.True(result.GetProperty("ReopenSucceeded").GetBoolean());
            Assert.True(result.GetProperty("DeleteSucceeded").GetBoolean());
            Assert.True(
                result.GetProperty("TotalLogLimitRejected").GetBoolean());
            Assert.True(
                result.GetProperty(
                    "HistoricalLogCountPruneSucceeded").GetBoolean());
            Assert.True(
                result.GetProperty(
                    "HistoricalLogBytePruneSucceeded").GetBoolean());
            Assert.True(
                result.GetProperty("StartupLogLimitRejected").GetBoolean());
            Assert.True(result.GetProperty("HealthRotationSucceeded").GetBoolean());
            Assert.True(
                result.GetProperty(
                    "RetryLoopThirdAttemptSucceeded").GetBoolean());
        }
        finally
        {
            await DeleteDirectoryWithRetriesAsync(testRoot);
        }
    }

    private static async Task<PowerShellResult> RunPowerShellAsync(
        string executablePath,
        string scriptPath,
        TimeSpan timeout,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(scriptPath)
                               ?? TestProcessIsolation.TempRoot
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                $"PowerShell process did not start: {scriptPath}");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync().WaitAsync(timeout);
        }
        catch (TimeoutException ex)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);

            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException)
            {
                // Report the original timeout without waiting indefinitely.
            }

            throw new TimeoutException(
                "Runtime launcher observability probe timed out.",
                ex);
        }

        return new PowerShellResult(
            process.ExitCode,
            await stdoutTask.WaitAsync(TimeSpan.FromSeconds(5)),
            await stderrTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static string ResolvePreparationScript()
    {
        var sourceScript = Path.Combine(
            FindRepositoryRoot(),
            "테스트 시행",
            "테스트-환경-준비.ps1");
        Assert.True(
            File.Exists(sourceScript),
            $"Preparation script not found: {sourceScript}");
        return sourceScript;
    }

    private static string ResolveWindowsPowerShellPath()
    {
        var powerShellPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        Assert.True(
            File.Exists(powerShellPath),
            $"Windows PowerShell not found: {powerShellPath}");
        return powerShellPath;
    }

    private static async Task DeleteDirectoryWithRetriesAsync(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (!Directory.Exists(path))
                return;

            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch when (attempt < 4)
            {
                await Task.Delay(100 * (attempt + 1));
            }
        }
    }

    private static string FindRepositoryRoot(
        [CallerFilePath] string sourceFilePath = "")
    {
        var directory = new DirectoryInfo(
            Path.GetDirectoryName(sourceFilePath) ?? AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) &&
                Directory.Exists(Path.Combine(directory.FullName, "Desktop")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed record PowerShellResult(
        int ExitCode,
        string Stdout,
        string Stderr);
}
