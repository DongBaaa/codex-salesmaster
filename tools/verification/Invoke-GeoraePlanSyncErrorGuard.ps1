param(
    [string]$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [string]$BaseUrl = 'http://127.0.0.1:19080',
    [string]$Username = 'usenet',
    [string]$Password = '1234',
    [string]$EvidenceDirectory = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path 'output\sync-error-guard'),
    [string]$NasRoot = '\\192.168.0.200\docker\georaeplan',
    [datetime]$SinceUtc = [DateTime]::UtcNow,
    [switch]$ScanNasDockerLogs,
    [switch]$SkipRepeatedSaveSmoke,
    [switch]$SkipSameAccountConcurrencySmoke,
    [switch]$SkipRealtimeRevisionSmoke,
    [int]$RepeatedSaveCount = 3,
    [int]$RealtimeWaitTimeoutSeconds = 10
)

$ErrorActionPreference = 'Stop'

function New-DirectoryIfMissing {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -ItemType Directory -Force -Path $Path | Out-Null
    }
}

function Convert-OutputText {
    param([object[]]$Output)
    if ($null -eq $Output -or $Output.Count -eq 0) {
        return ''
    }

    return (($Output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine)
}

function Add-Step {
    param(
        [string]$Name,
        [string]$Result,
        [string]$Detail,
        [string]$ReportPath = '',
        [string]$RawOutputPath = ''
    )

    $script:Steps.Add([pscustomobject]@{
        Name = $Name
        Result = $Result
        Detail = $Detail
        ReportPath = $ReportPath
        RawOutputPath = $RawOutputPath
    }) | Out-Null
}

function Invoke-ApiHealthCheck {
    param([string]$BaseUrl, [string]$Username, [string]$Password)

    $root = $BaseUrl.TrimEnd('/')
    $health = (Invoke-WebRequest -UseBasicParsing -Uri "$root/healthz" -TimeoutSec 10).StatusCode
    $ready = (Invoke-WebRequest -UseBasicParsing -Uri "$root/readyz" -TimeoutSec 10).StatusCode
    $loginPayload = @{ username = $Username; password = $Password } | ConvertTo-Json -Compress
    $login = Invoke-RestMethod -Method Post -Uri "$root/auth/login" -ContentType 'application/json; charset=utf-8' -Body $loginPayload -TimeoutSec 20
    if ([string]::IsNullOrWhiteSpace([string]$login.token)) {
        throw '로그인 토큰을 받지 못했습니다.'
    }

    "health=$health; ready=$ready; login=OK"
}

function Invoke-SmokeScript {
    param(
        [string]$Name,
        [string]$ScriptPath,
        [hashtable]$Arguments,
        [string]$EvidenceDirectory
    )

    $rawPath = Join-Path $EvidenceDirectory "$Name-output.txt"
    $global:LASTEXITCODE = 0
    $output = & $ScriptPath @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    $text = Convert-OutputText $output
    $text | Set-Content -LiteralPath $rawPath -Encoding UTF8
    if ($exitCode -ne 0) {
        throw "$Name failed with exit code $exitCode. $text"
    }

    $reportPath = ''
    foreach ($line in @($text -split "`r?`n")) {
        if ($line -match '^[a-zA-Z0-9_-]+_report=(?<path>.+)$') {
            $reportPath = $matches['path'].Trim()
        }
    }

    [pscustomobject]@{
        Detail = 'PASS'
        ReportPath = $reportPath
        RawOutputPath = $rawPath
    }
}

function Get-NasEnvMap {
    param([string]$NasRoot)

    $envPath = Join-Path $NasRoot 'ops\.env'
    if (-not (Test-Path -LiteralPath $envPath)) {
        throw "NAS 환경 파일을 찾지 못했습니다: $envPath"
    }

    $map = @{}
    Get-Content -LiteralPath $envPath | ForEach-Object {
        if ($_ -match '^([^#=]+)=(.*)$') {
            $map[$matches[1]] = $matches[2]
        }
    }

    return $map
}

function Invoke-NasSyncErrorLogScan {
    param(
        [string]$NasRoot,
        [datetime]$SinceUtc,
        [string]$EvidenceDirectory
    )

    $nasEnv = Get-NasEnvMap -NasRoot $NasRoot
    $ssh = 'C:\Windows\System32\OpenSSH\ssh.exe'
    if (-not (Test-Path -LiteralPath $ssh)) {
        $ssh = 'ssh'
    }

    $hostName = [string]$nasEnv['NAS_SSH_HOST']
    $userName = [string]$nasEnv['NAS_SSH_USER']
    $port = [string]$nasEnv['NAS_SSH_PORT']
    $keyPath = [string]$nasEnv['NAS_SSH_KEY_PATH']
    if ([string]::IsNullOrWhiteSpace($hostName) -or
        [string]::IsNullOrWhiteSpace($userName) -or
        [string]::IsNullOrWhiteSpace($port) -or
        [string]::IsNullOrWhiteSpace($keyPath)) {
        throw 'NAS SSH 설정이 부족합니다. ops\.env의 NAS_SSH_HOST/USER/PORT/KEY_PATH를 확인하세요.'
    }

    $sinceText = $SinceUtc.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    $pattern = '(^fail:|^error:|Unhandled exception|A second operation|DbUpdateConcurrencyException|동기화 오류|동기화 실패|sync/push failed|Timeout during reading attempt)'
    $remoteCommand = "/usr/local/bin/docker logs --since '$sinceText' georaeplan-api-1 2>&1 | grep -i -E '$pattern' | tail -200 || true"
    $rawPath = Join-Path $EvidenceDirectory 'nas-sync-error-log-scan.txt'
    $output = & $ssh -p $port -i $keyPath -o BatchMode=yes -o StrictHostKeyChecking=accept-new -o ConnectTimeout=10 "$userName@$hostName" $remoteCommand 2>&1
    $text = Convert-OutputText $output
    $text | Set-Content -LiteralPath $rawPath -Encoding UTF8

    if (-not [string]::IsNullOrWhiteSpace($text)) {
        throw "NAS live 로그에서 동기화 오류 후보가 발견되었습니다. raw=$rawPath"
    }

    [pscustomobject]@{
        Detail = "PASS since=$sinceText"
        RawOutputPath = $rawPath
    }
}

New-DirectoryIfMissing $EvidenceDirectory
$script:Steps = New-Object System.Collections.Generic.List[object]
$startedAtUtc = [DateTime]::UtcNow
$effectiveSinceUtc = if ($PSBoundParameters.ContainsKey('SinceUtc')) { $SinceUtc } else { $startedAtUtc }

try {
    $detail = Invoke-ApiHealthCheck -BaseUrl $BaseUrl -Username $Username -Password $Password
    Add-Step -Name 'health-ready-login' -Result 'PASS' -Detail $detail
}
catch {
    Add-Step -Name 'health-ready-login' -Result 'FAIL' -Detail $_.Exception.Message
}

if (-not $SkipRepeatedSaveSmoke) {
    try {
        $stepDir = Join-Path $EvidenceDirectory 'repeated-save-smoke'
        New-DirectoryIfMissing $stepDir
        $result = Invoke-SmokeScript -Name 'repeated-save-smoke' -ScriptPath (Join-Path $ProjectRoot 'tools\verification\Invoke-GeoraePlanRepeatedSaveSmoke.ps1') -Arguments @{
            BaseUrl = $BaseUrl
            Username = $Username
            Password = $Password
            EvidenceDirectory = $stepDir
            RepeatCount = $RepeatedSaveCount
        } -EvidenceDirectory $EvidenceDirectory
        Add-Step -Name 'repeated-save-smoke' -Result 'PASS' -Detail $result.Detail -ReportPath $result.ReportPath -RawOutputPath $result.RawOutputPath
    }
    catch {
        Add-Step -Name 'repeated-save-smoke' -Result 'FAIL' -Detail $_.Exception.Message
    }
}
else {
    Add-Step -Name 'repeated-save-smoke' -Result 'SKIP' -Detail 'Skipped by option'
}

if (-not $SkipSameAccountConcurrencySmoke) {
    try {
        $stepDir = Join-Path $EvidenceDirectory 'same-account-concurrency'
        New-DirectoryIfMissing $stepDir
        $result = Invoke-SmokeScript -Name 'same-account-concurrency' -ScriptPath (Join-Path $ProjectRoot 'tools\verification\Invoke-GeoraePlanSameAccountConcurrencySmoke.ps1') -Arguments @{
            BaseUrl = $BaseUrl
            Username = $Username
            Password = $Password
            EvidenceDirectory = $stepDir
        } -EvidenceDirectory $EvidenceDirectory
        Add-Step -Name 'same-account-concurrency' -Result 'PASS' -Detail $result.Detail -ReportPath $result.ReportPath -RawOutputPath $result.RawOutputPath
    }
    catch {
        Add-Step -Name 'same-account-concurrency' -Result 'FAIL' -Detail $_.Exception.Message
    }
}
else {
    Add-Step -Name 'same-account-concurrency' -Result 'SKIP' -Detail 'Skipped by option'
}

if (-not $SkipRealtimeRevisionSmoke) {
    try {
        $stepDir = Join-Path $EvidenceDirectory 'realtime-revision'
        New-DirectoryIfMissing $stepDir
        $result = Invoke-SmokeScript -Name 'realtime-revision' -ScriptPath (Join-Path $ProjectRoot 'tools\verification\Invoke-GeoraePlanRealtimeRevisionSmoke.ps1') -Arguments @{
            BaseUrl = $BaseUrl
            Username = $Username
            Password = $Password
            EvidenceDirectory = $stepDir
            WaitTimeoutSeconds = $RealtimeWaitTimeoutSeconds
        } -EvidenceDirectory $EvidenceDirectory
        Add-Step -Name 'realtime-revision' -Result 'PASS' -Detail $result.Detail -ReportPath $result.ReportPath -RawOutputPath $result.RawOutputPath
    }
    catch {
        Add-Step -Name 'realtime-revision' -Result 'FAIL' -Detail $_.Exception.Message
    }
}
else {
    Add-Step -Name 'realtime-revision' -Result 'SKIP' -Detail 'Skipped by option'
}

if ($ScanNasDockerLogs) {
    try {
        $result = Invoke-NasSyncErrorLogScan -NasRoot $NasRoot -SinceUtc $effectiveSinceUtc -EvidenceDirectory $EvidenceDirectory
        Add-Step -Name 'nas-sync-error-log-scan' -Result 'PASS' -Detail $result.Detail -RawOutputPath $result.RawOutputPath
    }
    catch {
        Add-Step -Name 'nas-sync-error-log-scan' -Result 'FAIL' -Detail $_.Exception.Message
    }
}
else {
    Add-Step -Name 'nas-sync-error-log-scan' -Result 'SKIP' -Detail 'Skipped by option'
}

$failed = @($script:Steps | Where-Object { $_.Result -eq 'FAIL' })
$overall = if ($failed.Count -eq 0) { 'PASS' } else { 'FAIL' }
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$jsonPath = Join-Path $EvidenceDirectory "sync-error-guard-$timestamp.json"
$mdPath = Join-Path $EvidenceDirectory "sync-error-guard-$timestamp.md"

[pscustomobject]@{
    GeneratedAt = [DateTimeOffset]::Now.ToString('o')
    BaseUrl = $BaseUrl
    SinceUtc = $effectiveSinceUtc.ToUniversalTime().ToString('o')
    Overall = $overall
    Steps = $script:Steps
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('# 거래플랜 동기화 오류 방지 가드') | Out-Null
$lines.Add('') | Out-Null
$lines.Add("- 결과: **$overall**") | Out-Null
$lines.Add(('- 기준 URL: `{0}`' -f $BaseUrl)) | Out-Null
$lines.Add(('- 로그 조회 기준 UTC: `{0}`' -f $effectiveSinceUtc.ToUniversalTime().ToString('o'))) | Out-Null
$lines.Add('') | Out-Null
$lines.Add('| 결과 | 단계 | 상세 | 보고서 | Raw |') | Out-Null
$lines.Add('|---|---|---|---|---|') | Out-Null
foreach ($step in $script:Steps) {
    $detail = ([string]$step.Detail).Replace('|', '/')
    $report = if ([string]::IsNullOrWhiteSpace([string]$step.ReportPath)) { '' } else { [string]$step.ReportPath }
    $raw = if ([string]::IsNullOrWhiteSpace([string]$step.RawOutputPath)) { '' } else { [string]$step.RawOutputPath }
    $lines.Add("| $($step.Result) | $($step.Name) | $detail | $report | $raw |") | Out-Null
}
$lines | Set-Content -LiteralPath $mdPath -Encoding UTF8

Write-Host "sync_error_guard_report=$mdPath"
Write-Host "sync_error_guard_json=$jsonPath"
Write-Host "result=$overall"

if ($overall -ne 'PASS') {
    throw "동기화 오류 방지 가드 실패: $mdPath"
}
