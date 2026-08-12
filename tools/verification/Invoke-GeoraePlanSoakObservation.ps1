[CmdletBinding()]
param(
    [string]$ProjectRoot = "",
    [string]$BaseUrl = "https://trade.2884.kr",
    [string]$Channel = "stable",
    [int]$SampleCount = 1440,
    [int]$IntervalSeconds = 60,
    [int]$RequestTimeoutSeconds = 20,
    [string]$DesktopProcessName = "거래플랜",
    [switch]$RequireDesktopProcess,
    [double]$MaxWorkingSetGrowthMb = 512,
    [switch]$FailOnWarnings,
    [string]$OutputDirectory = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-DefaultProjectRoot {
    param([Parameter(Mandatory = $true)][string]$ScriptPath)
    return (Resolve-Path (Join-Path (Split-Path -Parent $ScriptPath) '..\..')).Path
}

function Invoke-ReadOnlyProbe {
    param(
        [Parameter(Mandatory = $true)][string]$Uri,
        [Parameter(Mandatory = $true)][int]$TimeoutSeconds
    )

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $response = Invoke-WebRequest -Uri $Uri -Method Get -UseBasicParsing -TimeoutSec $TimeoutSeconds
        return [pscustomobject]@{
            Success = $true
            StatusCode = [int]$response.StatusCode
            ElapsedMs = [int64]$stopwatch.ElapsedMilliseconds
            Content = [string]$response.Content
            Error = ''
        }
    }
    catch {
        $statusCode = 0
        if ($null -ne $_.Exception.Response -and $null -ne $_.Exception.Response.StatusCode) {
            $statusCode = [int]$_.Exception.Response.StatusCode
        }

        return [pscustomobject]@{
            Success = $false
            StatusCode = $statusCode
            ElapsedMs = [int64]$stopwatch.ElapsedMilliseconds
            Content = ''
            Error = [string]$_.Exception.Message
        }
    }
    finally {
        $stopwatch.Stop()
    }
}

function Get-DesktopProcessSnapshot {
    param([string]$ProcessName)

    if ([string]::IsNullOrWhiteSpace($ProcessName)) {
        return $null
    }

    $process = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue |
        Sort-Object WorkingSet64 -Descending |
        Select-Object -First 1
    if ($null -eq $process) {
        return $null
    }

    $responding = $true
    try {
        if ($process.MainWindowHandle -ne 0) {
            $responding = [bool]$process.Responding
        }
    }
    catch {
        $responding = $true
    }

    return [pscustomobject]@{
        Id = [int]$process.Id
        Responding = $responding
        WorkingSetMb = [Math]::Round($process.WorkingSet64 / 1MB, 2)
        PrivateMemoryMb = [Math]::Round($process.PrivateMemorySize64 / 1MB, 2)
        HandleCount = [int]$process.HandleCount
        CpuSeconds = [Math]::Round([double]$process.CPU, 2)
    }
}

function Escape-CsvValue {
    param([object]$Value)
    $text = if ($null -eq $Value) { '' } else { [string]$Value }
    return '"' + $text.Replace('"', '""') + '"'
}

function Get-OptionalManifestVersion {
    param(
        [object]$Manifest,
        [Parameter(Mandatory = $true)][string]$PackageName
    )

    if ($null -eq $Manifest) {
        return ''
    }
    $packageProperty = $Manifest.PSObject.Properties[$PackageName]
    if ($null -eq $packageProperty -or $null -eq $packageProperty.Value) {
        return ''
    }
    $versionProperty =
        $packageProperty.Value.PSObject.Properties['version']
    if ($null -eq $versionProperty -or $null -eq $versionProperty.Value) {
        return ''
    }
    return [string]$versionProperty.Value
}

if ($SampleCount -lt 1) {
    throw 'SampleCount는 1 이상이어야 합니다.'
}
if ($IntervalSeconds -lt 1) {
    throw 'IntervalSeconds는 1 이상이어야 합니다.'
}
if ($RequestTimeoutSeconds -lt 1) {
    throw 'RequestTimeoutSeconds는 1 이상이어야 합니다.'
}
if ($MaxWorkingSetGrowthMb -lt 0) {
    throw 'MaxWorkingSetGrowthMb는 0 이상이어야 합니다.'
}

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = Resolve-DefaultProjectRoot -ScriptPath $MyInvocation.MyCommand.Path
}
$ProjectRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path
$BaseUrl = $BaseUrl.TrimEnd('/')

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $ProjectRoot ('audit-output\soak-{0}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
$csvPath = Join-Path $OutputDirectory 'soak-samples.csv'
$reportPath = Join-Path $OutputDirectory 'soak-observation.md'

$csvHeader = 'Index,SampledAt,HealthOk,HealthStatus,HealthMs,ManifestOk,ManifestStatus,ManifestMs,DesktopVersion,AndroidVersion,ProcessFound,ProcessId,Responding,WorkingSetMb,PrivateMemoryMb,HandleCount,CpuSeconds,Error'
[System.IO.File]::WriteAllText($csvPath, $csvHeader + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($true))

$healthUri = $BaseUrl + '/healthz'
$manifestUri = $BaseUrl + '/updates/manifest?channel=' + [Uri]::EscapeDataString($Channel)
$samples = [System.Collections.Generic.List[object]]::new()
$startedAt = Get-Date

Write-Host "soak_observation_start=$($startedAt.ToString('yyyy-MM-dd HH:mm:ss'))"
Write-Host "soak_observation_samples=$SampleCount"
Write-Host "soak_observation_interval_seconds=$IntervalSeconds"
Write-Host "soak_observation_output=$OutputDirectory"

for ($index = 1; $index -le $SampleCount; $index++) {
    $sampledAt = Get-Date
    $health = Invoke-ReadOnlyProbe -Uri $healthUri -TimeoutSeconds $RequestTimeoutSeconds
    $manifest = Invoke-ReadOnlyProbe -Uri $manifestUri -TimeoutSeconds $RequestTimeoutSeconds
    $desktopVersion = ''
    $androidVersion = ''
    $manifestError = [string]$manifest.Error

    if ($manifest.Success -and -not [string]::IsNullOrWhiteSpace($manifest.Content)) {
        try {
            $manifestJson = $manifest.Content | ConvertFrom-Json
            $desktopVersion =
                Get-OptionalManifestVersion `
                    -Manifest $manifestJson `
                    -PackageName 'desktop'
            $androidVersion =
                Get-OptionalManifestVersion `
                    -Manifest $manifestJson `
                    -PackageName 'android'
        }
        catch {
            $manifest = [pscustomobject]@{
                Success = $false
                StatusCode = $manifest.StatusCode
                ElapsedMs = $manifest.ElapsedMs
                Content = $manifest.Content
                Error = 'manifest JSON 파싱 실패: ' + $_.Exception.Message
            }
            $manifestError = [string]$manifest.Error
        }
    }

    $processSnapshot = Get-DesktopProcessSnapshot -ProcessName $DesktopProcessName
    $processFound = $null -ne $processSnapshot
    $errorParts = [System.Collections.Generic.List[string]]::new()
    if (-not $health.Success) {
        $errorParts.Add('healthz: ' + $health.Error) | Out-Null
    }
    if (-not $manifest.Success) {
        $errorParts.Add('manifest: ' + $manifestError) | Out-Null
    }
    if ($processFound -and -not $processSnapshot.Responding) {
        $errorParts.Add('desktop process not responding') | Out-Null
    }
    if ($RequireDesktopProcess -and -not $processFound) {
        $errorParts.Add('required desktop process not found') | Out-Null
    }

    $sample = [pscustomobject]@{
        Index = $index
        SampledAt = $sampledAt
        HealthOk = [bool]$health.Success
        HealthStatus = [int]$health.StatusCode
        HealthMs = [int64]$health.ElapsedMs
        ManifestOk = [bool]$manifest.Success
        ManifestStatus = [int]$manifest.StatusCode
        ManifestMs = [int64]$manifest.ElapsedMs
        DesktopVersion = $desktopVersion
        AndroidVersion = $androidVersion
        ProcessFound = $processFound
        ProcessId = if ($processFound) { $processSnapshot.Id } else { '' }
        Responding = if ($processFound) { $processSnapshot.Responding } else { '' }
        WorkingSetMb = if ($processFound) { $processSnapshot.WorkingSetMb } else { '' }
        PrivateMemoryMb = if ($processFound) { $processSnapshot.PrivateMemoryMb } else { '' }
        HandleCount = if ($processFound) { $processSnapshot.HandleCount } else { '' }
        CpuSeconds = if ($processFound) { $processSnapshot.CpuSeconds } else { '' }
        Error = ($errorParts -join '; ')
    }
    $samples.Add($sample) | Out-Null

    $csvValues = @(
        $sample.Index,
        $sample.SampledAt.ToString('o'),
        $sample.HealthOk,
        $sample.HealthStatus,
        $sample.HealthMs,
        $sample.ManifestOk,
        $sample.ManifestStatus,
        $sample.ManifestMs,
        $sample.DesktopVersion,
        $sample.AndroidVersion,
        $sample.ProcessFound,
        $sample.ProcessId,
        $sample.Responding,
        $sample.WorkingSetMb,
        $sample.PrivateMemoryMb,
        $sample.HandleCount,
        $sample.CpuSeconds,
        $sample.Error
    ) | ForEach-Object { Escape-CsvValue -Value $_ }
    [System.IO.File]::AppendAllText($csvPath, ($csvValues -join ',') + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))

    Write-Host ('soak_sample={0}/{1} health={2}/{3} manifest={4}/{5} process={6} responding={7} ws_mb={8}' -f `
        $index,
        $SampleCount,
        $sample.HealthOk,
        $sample.HealthStatus,
        $sample.ManifestOk,
        $sample.ManifestStatus,
        $sample.ProcessFound,
        $sample.Responding,
        $sample.WorkingSetMb)

    if ($index -lt $SampleCount) {
        Start-Sleep -Seconds $IntervalSeconds
    }
}

$completedAt = Get-Date
$failedSamples = @($samples | Where-Object {
        -not $_.HealthOk -or
        -not $_.ManifestOk -or
        ($_.ProcessFound -and $_.Responding -eq $false) -or
        ($RequireDesktopProcess -and -not $_.ProcessFound)
    })
$processSamples = @($samples | Where-Object { $_.ProcessFound -and $_.WorkingSetMb -ne '' })
$workingSetGrowthMb = 0.0
$workingSetPeakMb = 0.0
$positiveGrowthRatio = 0.0
if ($processSamples.Count -gt 0) {
    $workingSetGrowthMb = [Math]::Round([double]$processSamples[-1].WorkingSetMb - [double]$processSamples[0].WorkingSetMb, 2)
    $workingSetPeakMb = [Math]::Round([double](($processSamples | Measure-Object WorkingSetMb -Maximum).Maximum), 2)
}
if ($processSamples.Count -gt 1) {
    $positiveGrowthCount = 0
    for ($index = 1; $index -lt $processSamples.Count; $index++) {
        if ([double]$processSamples[$index].WorkingSetMb -gt [double]$processSamples[$index - 1].WorkingSetMb) {
            $positiveGrowthCount++
        }
    }
    $positiveGrowthRatio = [Math]::Round($positiveGrowthCount / [double]($processSamples.Count - 1), 3)
}

$warnings = [System.Collections.Generic.List[string]]::new()
if (-not $RequireDesktopProcess -and $processSamples.Count -eq 0) {
    $warnings.Add('관찰 PC에서 거래플랜 데스크톱 프로세스를 찾지 못해 UI 응답성과 메모리 추이는 측정하지 않았습니다.') | Out-Null
}
if ($processSamples.Count -gt 1 -and $workingSetGrowthMb -gt $MaxWorkingSetGrowthMb -and $positiveGrowthRatio -ge 0.8) {
    $warnings.Add(('Working Set이 {0:N2}MB 증가했고 증가 샘플 비율이 {1:P1}입니다. 메모리 누수 여부를 추가 확인하세요.' -f $workingSetGrowthMb, $positiveGrowthRatio)) | Out-Null
}

$result = if ($failedSamples.Count -gt 0) {
    'FAIL'
}
elseif ($warnings.Count -gt 0) {
    if ($FailOnWarnings) { 'FAIL' } else { 'WARN' }
}
else {
    'PASS'
}

$expectedDuration = [TimeSpan]::FromSeconds([Math]::Max(0, ($SampleCount - 1) * $IntervalSeconds))
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('# 거래플랜 장시간 관찰 리포트') | Out-Null
$lines.Add('') | Out-Null
$lines.Add("- 결과: **$result**") | Out-Null
$lines.Add("- 시작: $($startedAt.ToString('yyyy-MM-dd HH:mm:ss'))") | Out-Null
$lines.Add("- 종료: $($completedAt.ToString('yyyy-MM-dd HH:mm:ss'))") | Out-Null
$lines.Add("- 예정 관찰시간: $expectedDuration") | Out-Null
$lines.Add("- BaseUrl: $BaseUrl") | Out-Null
$lines.Add("- 채널: $Channel") | Out-Null
$lines.Add("- 샘플: $SampleCount") | Out-Null
$lines.Add("- 간격(초): $IntervalSeconds") | Out-Null
$lines.Add("- 실패 샘플: $($failedSamples.Count)") | Out-Null
$lines.Add("- 데스크톱 프로세스 필수: $([bool]$RequireDesktopProcess)") | Out-Null
$lines.Add("- Working Set 시작 대비 증가: $workingSetGrowthMb MB") | Out-Null
$lines.Add("- Working Set 최대: $workingSetPeakMb MB") | Out-Null
$lines.Add("- Working Set 증가 샘플 비율: $positiveGrowthRatio") | Out-Null
$lines.Add("- 샘플 CSV: $csvPath") | Out-Null

if ($warnings.Count -gt 0) {
    $lines.Add('') | Out-Null
    $lines.Add('## 경고') | Out-Null
    foreach ($warning in $warnings) {
        $lines.Add("- $warning") | Out-Null
    }
}
if ($failedSamples.Count -gt 0) {
    $lines.Add('') | Out-Null
    $lines.Add('## 실패 샘플') | Out-Null
    foreach ($sample in ($failedSamples | Select-Object -First 20)) {
        $lines.Add("- #$($sample.Index) $($sample.SampledAt.ToString('yyyy-MM-dd HH:mm:ss')): $($sample.Error)") | Out-Null
    }
}

$lines.Add('') | Out-Null
$lines.Add('## 판정 기준') | Out-Null
$lines.Add('- healthz와 update manifest를 읽기 전용으로 반복 조회합니다.') | Out-Null
$lines.Add('- 거래플랜 프로세스가 실행 중이면 응답 상태, Working Set, Private Memory, 핸들 수를 함께 기록합니다.') | Out-Null
$lines.Add('- 운영 데이터 생성, 수정, 삭제 API는 호출하지 않습니다.') | Out-Null

[System.IO.File]::WriteAllText($reportPath, ($lines -join [Environment]::NewLine), [System.Text.UTF8Encoding]::new($true))
Write-Host "soak_observation_report=$reportPath"
Write-Host "soak_observation_csv=$csvPath"
Write-Host "result=$result"

if ($result -eq 'FAIL') {
    exit 1
}
exit 0
