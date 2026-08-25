[CmdletBinding()]
param(
    [ValidateSet('Start', 'Status', 'Cleanup')]
    [string]$Mode = 'Status',
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9-]{7,39}$')]
    [string]$RunId,
    [string]$ProjectRoot = '',
    [string]$SoakRoot = '',
    [string]$BaseUrl = 'https://trade.2884.kr',
    [string]$Channel = 'stable',
    [int]$SampleCount = 1440,
    [int]$IntervalSeconds = 60,
    [int]$StartupTimeoutSeconds = 180,
    [switch]$ValidateOnly,
    [switch]$ForceCleanup
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

$expectedObserverSha256 =
    '60BC0FEC39E8B94E7657AD900F40D941574F68C78C4B03B49DC8FC81C82F1AC0'
$allowedSoakRoot = 'D:\DevCaches\georaeplan-v1-soak'

function ConvertTo-ExactFullPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    return [IO.Path]::GetFullPath($Path).TrimEnd(
        [char[]]@([IO.Path]::DirectorySeparatorChar,
                  [IO.Path]::AltDirectorySeparatorChar))
}

function Assert-RegularFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not [IO.File]::Exists($Path)) {
        throw "Required file is missing: $Path"
    }
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.PSIsContainer -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Required file must be a regular non-reparse file: $Path"
    }
    return $item
}

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)

    $stream = [IO.FileStream]::new(
        $Path,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read,
        4096,
        [IO.FileOptions]::SequentialScan)
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString(
            $sha256.ComputeHash($stream)).Replace('-', '')
    }
    finally {
        $sha256.Dispose()
        $stream.Dispose()
    }
}

function Assert-NoReparseAncestors {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Boundary
    )

    $candidate = ConvertTo-ExactFullPath -Path $Path
    $boundaryFull = ConvertTo-ExactFullPath -Path $Boundary
    while ($candidate.Length -ge $boundaryFull.Length) {
        if ([IO.Directory]::Exists($candidate) -or [IO.File]::Exists($candidate)) {
            $item = Get-Item -LiteralPath $candidate -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "A protected path contains a reparse point: $candidate"
            }
        }
        if ([string]::Equals(
                $candidate,
                $boundaryFull,
                [StringComparison]::OrdinalIgnoreCase)) {
            return
        }
        $parent = Split-Path -Parent $candidate
        if ([string]::IsNullOrWhiteSpace($parent) -or $parent -ceq $candidate) {
            break
        }
        $candidate = ConvertTo-ExactFullPath -Path $parent
    }
    throw "Protected path escaped its boundary: $Path"
}

function ConvertTo-QuotedCommandLineArgument {
    param([Parameter(Mandatory = $true)][string]$Value)

    if ($Value.Contains('"')) {
        throw 'A scheduled-task argument contains a quote.'
    }
    return '"' + $Value + '"'
}

function Write-AtomicJson {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Value
    )

    $temporary = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(
        ($Value | ConvertTo-Json -Depth 8))
    try {
        $stream = [IO.FileStream]::new(
            $temporary,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None,
            4096,
            [IO.FileOptions]::WriteThrough)
        try {
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush($true)
        }
        finally {
            $stream.Dispose()
        }
        [IO.File]::Move($temporary, $Path)
    }
    finally {
        [Array]::Clear($bytes, 0, $bytes.Length)
        if ([IO.File]::Exists($temporary)) {
            [IO.File]::Delete($temporary)
        }
    }
}

function Get-TaskOrNull {
    param([Parameter(Mandatory = $true)][string]$TaskName)

    return Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
}

function Stop-AndRemoveTask {
    param([Parameter(Mandatory = $true)][string]$TaskName)

    $task = Get-TaskOrNull -TaskName $TaskName
    if ($null -eq $task) {
        return
    }
    if ([string]$task.State -ne 'Disabled') {
        Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    }
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 250
        $task = Get-TaskOrNull -TaskName $TaskName
    } while (
        $null -ne $task -and
        [string]$task.State -eq 'Running' -and
        [DateTimeOffset]::UtcNow -lt $deadline)
    if ($null -ne $task -and [string]$task.State -eq 'Running') {
        throw "Scheduled task did not stop: $TaskName"
    }
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
}

function Get-ExactDesktopProcess {
    param([Parameter(Mandatory = $true)][string]$ExecutablePath)

    $expected = ConvertTo-ExactFullPath -Path $ExecutablePath
    $matches = @(
        Get-Process -Name '거래플랜.Desktop.App' -ErrorAction SilentlyContinue |
            Where-Object {
                try {
                    [string]::Equals(
                        (ConvertTo-ExactFullPath -Path $_.Path),
                        $expected,
                        [StringComparison]::OrdinalIgnoreCase)
                }
                catch { $false }
            })
    if ($matches.Count -gt 1) {
        throw 'More than one exact test desktop process is running.'
    }
    if ($matches.Count -eq 1) {
        return $matches[0]
    }
    return $null
}

function Get-ObservationState {
    param([Parameter(Mandatory = $true)][string]$CsvPath)

    if (-not [IO.File]::Exists($CsvPath)) {
        return [pscustomobject][ordered]@{
            RowCount = 0
            IndexContiguous = $true
            ViolationCount = 0
            LastSampledAt = $null
        }
    }
    $rows = @(Import-Csv -LiteralPath $CsvPath)
    $indexContiguous = $true
    for ($index = 0; $index -lt $rows.Count; $index++) {
        if ([int]$rows[$index].Index -ne ($index + 1)) {
            $indexContiguous = $false
            break
        }
    }
    $violations = @($rows | Where-Object {
        $_.HealthOk -cne 'True' -or
        $_.ManifestOk -cne 'True' -or
        $_.ProcessFound -cne 'True' -or
        $_.Responding -cne 'True' -or
        -not [string]::IsNullOrWhiteSpace([string]$_.Error)
    })
    return [pscustomobject][ordered]@{
        RowCount = $rows.Count
        IndexContiguous = $indexContiguous
        ViolationCount = $violations.Count
        LastSampledAt = if ($rows.Count -gt 0) {
            [string]$rows[$rows.Count - 1].SampledAt
        }
        else { $null }
    }
}

if ($SampleCount -ne 1440 -or $IntervalSeconds -ne 60) {
    throw 'The certified baseline requires exactly 1,440 samples at 60 seconds.'
}
if ($StartupTimeoutSeconds -lt 30 -or $StartupTimeoutSeconds -gt 600) {
    throw 'StartupTimeoutSeconds must be between 30 and 600.'
}
if ($Channel -cne 'stable') {
    throw 'The certified baseline channel must be stable.'
}
$uri = $null
if (-not [Uri]::TryCreate($BaseUrl, [UriKind]::Absolute, [ref]$uri) -or
    $uri.Scheme -cne 'https' -or
    $uri.Host -cne 'trade.2884.kr' -or
    $uri.Port -ne 443 -or
    $uri.AbsolutePath -cne '/' -or
    -not [string]::IsNullOrEmpty($uri.UserInfo) -or
    -not [string]::IsNullOrEmpty($uri.Query) -or
    -not [string]::IsNullOrEmpty($uri.Fragment)) {
    throw 'BaseUrl must be exactly https://trade.2884.kr.'
}

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..') |
        Select-Object -ExpandProperty Path
}
$project = ConvertTo-ExactFullPath -Path $ProjectRoot
$runtime = ConvertTo-ExactFullPath -Path (
    Join-Path $project '테스트 시행\실행환경')
if ([string]::IsNullOrWhiteSpace($SoakRoot)) {
    $SoakRoot = Join-Path $allowedSoakRoot ('soak-' + $RunId)
}
$soak = ConvertTo-ExactFullPath -Path $SoakRoot
$allowed = ConvertTo-ExactFullPath -Path $allowedSoakRoot
$allowedPrefix = $allowed + [IO.Path]::DirectorySeparatorChar
if (-not $soak.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'SoakRoot escaped the dedicated baseline root.'
}
Assert-NoReparseAncestors -Path $soak -Boundary $allowed

$runAll = Join-Path $runtime 'Run-All.ps1'
$runtimeReady = Join-Path $runtime '.georaeplan-runtime-ready'
$runtimeInvalid = Join-Path $runtime '.georaeplan-runtime-invalid'
$desktopExecutable = Join-Path $runtime 'App\거래플랜.Desktop.App.exe'
$desktopAssembly = Join-Path $runtime 'App\거래플랜.Desktop.App.dll'
$observer = Join-Path $project 'tools\verification\Invoke-GeoraePlanSoakObservation.ps1'
$observationDirectory = Join-Path $soak 'observation'
$csvPath = Join-Path $observationDirectory 'soak-samples.csv'
$attestationPath = Join-Path $soak 'baseline-soak-attestation.json'
$runAllTaskName = 'GeoraePlan-Soak-' + $RunId + '-RunAll'
$observerTaskName = 'GeoraePlan-Soak-' + $RunId + '-Observer'

[void](Assert-RegularFile -Path $observer)
if ((Get-FileSha256 -Path $observer) -cne
        $expectedObserverSha256) {
    throw 'The baseline observer SHA-256 changed.'
}

if ($Mode -ceq 'Status') {
    $process = Get-ExactDesktopProcess -ExecutablePath $desktopExecutable
    [pscustomobject][ordered]@{
        Result = 'PASS'
        Mode = 'STATUS'
        RunId = $RunId
        SoakRoot = $soak
        Observation = Get-ObservationState -CsvPath $csvPath
        RunAllTaskState = if ($null -ne (Get-TaskOrNull $runAllTaskName)) {
            [string](Get-TaskOrNull $runAllTaskName).State
        }
        else { 'Missing' }
        ObserverTaskState = if ($null -ne (Get-TaskOrNull $observerTaskName)) {
            [string](Get-TaskOrNull $observerTaskName).State
        }
        else { 'Missing' }
        DesktopProcessId = if ($null -ne $process) { [int]$process.Id } else { $null }
        RuntimeInvalid = [IO.File]::Exists($runtimeInvalid)
        RuntimeReady = [IO.File]::Exists($runtimeReady)
    } | ConvertTo-Json -Depth 5 -Compress
    return
}

if ($Mode -ceq 'Cleanup') {
    $observation = Get-ObservationState -CsvPath $csvPath
    if (-not $ForceCleanup -and
        ($observation.RowCount -ne 1440 -or
         -not $observation.IndexContiguous -or
         $observation.ViolationCount -ne 0)) {
        throw 'Cleanup before exact baseline completion requires -ForceCleanup.'
    }
    Stop-AndRemoveTask -TaskName $observerTaskName
    Stop-AndRemoveTask -TaskName $runAllTaskName
    [pscustomobject][ordered]@{
        Result = 'PASS'
        Mode = 'CLEANED'
        RunId = $RunId
        SoakRoot = $soak
        EvidencePreserved = [IO.Directory]::Exists($soak)
    } | ConvertTo-Json -Compress
    return
}

if ([IO.File]::Exists($runtimeInvalid)) {
    throw 'The test runtime is explicitly invalid and cannot start a baseline.'
}
[void](Assert-RegularFile -Path $runtimeReady)
[void](Assert-RegularFile -Path $runAll)
[void](Assert-RegularFile -Path $desktopExecutable)
[void](Assert-RegularFile -Path $desktopAssembly)
if ([IO.Directory]::Exists($soak) -or [IO.File]::Exists($soak)) {
    throw 'SoakRoot must not already exist for Start.'
}
if ($null -ne (Get-TaskOrNull $runAllTaskName) -or
    $null -ne (Get-TaskOrNull $observerTaskName)) {
    throw 'A scheduled task already exists for this RunId.'
}
if ($null -ne (Get-ExactDesktopProcess -ExecutablePath $desktopExecutable)) {
    throw 'The exact test desktop process is already running.'
}

$windowsPowerShell =
    'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe'
[void](Assert-RegularFile -Path $windowsPowerShell)
if ($ValidateOnly) {
    [pscustomobject][ordered]@{
        Result = 'PASS'
        Mode = 'VALIDATED'
        RunId = $RunId
        SoakRoot = $soak
        RuntimeReadySha256 =
            (Get-FileSha256 -Path $runtimeReady)
        ObserverSha256 = $expectedObserverSha256
        RunAllTaskName = $runAllTaskName
        ObserverTaskName = $observerTaskName
        ScheduledTaskLogonType = 'Interactive'
        ScheduledTaskRunLevel = 'Limited'
    } | ConvertTo-Json -Compress
    return
}

$runAllRegistered = $false
$observerRegistered = $false
New-Item -ItemType Directory -Path $soak -ErrorAction Stop | Out-Null
New-Item -ItemType Directory -Path $observationDirectory -ErrorAction Stop |
    Out-Null
try {
    $principal = New-ScheduledTaskPrincipal `
        -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) `
        -LogonType Interactive `
        -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet `
        -AllowStartIfOnBatteries `
        -DontStopIfGoingOnBatteries `
        -ExecutionTimeLimit ([TimeSpan]::Zero) `
        -MultipleInstances IgnoreNew

    $runAllArguments = @(
        '-NoLogo',
        '-NoProfile',
        '-NonInteractive',
        '-ExecutionPolicy',
        'Bypass',
        '-File',
        (ConvertTo-QuotedCommandLineArgument $runAll)
    ) -join ' '
    $runAllAction = New-ScheduledTaskAction `
        -Execute $windowsPowerShell `
        -Argument $runAllArguments `
        -WorkingDirectory $runtime
    Register-ScheduledTask `
        -TaskName $runAllTaskName `
        -Action $runAllAction `
        -Principal $principal `
        -Settings $settings `
        -Description 'TradePlan certified baseline Run-All host.' |
        Out-Null
    $runAllRegistered = $true
    Start-ScheduledTask -TaskName $runAllTaskName

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    $desktopProcess = $null
    do {
        Start-Sleep -Milliseconds 500
        $desktopProcess =
            Get-ExactDesktopProcess -ExecutablePath $desktopExecutable
    } while ($null -eq $desktopProcess -and
             [DateTimeOffset]::UtcNow -lt $deadline)
    if ($null -eq $desktopProcess) {
        throw 'The exact test desktop process did not start in time.'
    }

    $observerArguments = @(
        '-NoLogo',
        '-NoProfile',
        '-NonInteractive',
        '-ExecutionPolicy',
        'Bypass',
        '-File',
        (ConvertTo-QuotedCommandLineArgument $observer),
        '-ProjectRoot',
        (ConvertTo-QuotedCommandLineArgument $project),
        '-BaseUrl',
        (ConvertTo-QuotedCommandLineArgument $BaseUrl),
        '-Channel',
        $Channel,
        '-SampleCount',
        [string]$SampleCount,
        '-IntervalSeconds',
        [string]$IntervalSeconds,
        '-DesktopProcessName',
        (ConvertTo-QuotedCommandLineArgument '거래플랜.Desktop.App'),
        '-RequireDesktopProcess',
        '-FailOnWarnings',
        '-OutputDirectory',
        (ConvertTo-QuotedCommandLineArgument $observationDirectory)
    ) -join ' '
    $observerAction = New-ScheduledTaskAction `
        -Execute $windowsPowerShell `
        -Argument $observerArguments `
        -WorkingDirectory $project
    Register-ScheduledTask `
        -TaskName $observerTaskName `
        -Action $observerAction `
        -Principal $principal `
        -Settings $settings `
        -Description 'TradePlan certified 1,440 sample baseline observer.' |
        Out-Null
    $observerRegistered = $true
    Start-ScheduledTask -TaskName $observerTaskName

    $firstSampleDeadline = [DateTimeOffset]::UtcNow.AddSeconds(45)
    do {
        Start-Sleep -Milliseconds 500
        $observation = Get-ObservationState -CsvPath $csvPath
    } while ($observation.RowCount -lt 1 -and
             [DateTimeOffset]::UtcNow -lt $firstSampleDeadline)
    if ($observation.RowCount -ne 1 -or
        -not $observation.IndexContiguous -or
        $observation.ViolationCount -ne 0) {
        throw 'The first certified baseline sample was not exactly healthy.'
    }

    $attestation = [pscustomobject][ordered]@{
        SchemaVersion = 1
        Result = 'PASS'
        RunId = $RunId
        CreatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        SoakRoot = $soak
        CsvPath = $csvPath
        SampleCount = 1440
        IntervalSeconds = 60
        BaseUrl = 'https://trade.2884.kr'
        Channel = 'stable'
        RunAllTaskName = $runAllTaskName
        ObserverTaskName = $observerTaskName
        DesktopProcessId = [int]$desktopProcess.Id
        DesktopStartTimeUtc = $desktopProcess.StartTime.ToUniversalTime().ToString('O')
        DesktopExecutablePath = $desktopExecutable
        DesktopExecutableSha256 =
            (Get-FileSha256 -Path $desktopExecutable)
        DesktopAssemblySha256 =
            (Get-FileSha256 -Path $desktopAssembly)
        RuntimeReadySha256 =
            (Get-FileSha256 -Path $runtimeReady)
        RunAllSha256 =
            (Get-FileSha256 -Path $runAll)
        ObserverSha256 = $expectedObserverSha256
        FirstSampleHealthy = $true
    }
    Write-AtomicJson -Path $attestationPath -Value $attestation
    [pscustomobject][ordered]@{
        Result = 'PASS'
        Mode = 'STARTED'
        RunId = $RunId
        SoakRoot = $soak
        CsvPath = $csvPath
        AttestationPath = $attestationPath
        AttestationSha256 =
            (Get-FileSha256 -Path $attestationPath)
        DesktopProcessId = [int]$desktopProcess.Id
        FirstSampleHealthy = $true
    } | ConvertTo-Json -Compress
}
catch {
    $primary = $_
    try {
        if ($observerRegistered) {
            Stop-AndRemoveTask -TaskName $observerTaskName
        }
    }
    catch {}
    try {
        if ($runAllRegistered) {
            Stop-AndRemoveTask -TaskName $runAllTaskName
        }
    }
    catch {}
    throw $primary
}
