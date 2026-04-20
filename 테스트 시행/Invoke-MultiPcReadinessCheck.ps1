[CmdletBinding()]
param(
    [string]$ProjectRoot = "",
    [string]$ExecutionRoot = "",
    [string]$OutputPath = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $scriptRoot = if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) {
        $PSScriptRoot
    }
    else {
        Split-Path -Parent $MyInvocation.MyCommand.Path
    }

    $ProjectRoot = Split-Path -Parent $scriptRoot
}

if ([string]::IsNullOrWhiteSpace($ExecutionRoot)) {
    $ExecutionRoot = Join-Path $ProjectRoot "테스트 시행\실행환경"
}

$multiPcRoot = Join-Path $ExecutionRoot "MultiPC"
$appRoot = Join-Path $ExecutionRoot "App"

$checks = @(
    @{ Name = "MultiPC 폴더"; Path = $multiPcRoot; Type = "Directory" },
    @{ Name = "Run-All-MultiPC.cmd"; Path = (Join-Path $multiPcRoot "Run-All-MultiPC.cmd"); Type = "File" },
    @{ Name = "Run-App-PC-A.cmd"; Path = (Join-Path $multiPcRoot "Run-App-PC-A.cmd"); Type = "File" },
    @{ Name = "Run-App-PC-B.cmd"; Path = (Join-Path $multiPcRoot "Run-App-PC-B.cmd"); Type = "File" },
    @{ Name = "Run-Server.cmd"; Path = (Join-Path $multiPcRoot "Run-Server.cmd"); Type = "File" },
    @{ Name = "Reset-ClientData.ps1"; Path = (Join-Path $multiPcRoot "Reset-ClientData.ps1"); Type = "File" },
    @{ Name = "App publish"; Path = $appRoot; Type = "Directory" },
    @{ Name = "PC-A AppData"; Path = (Join-Path $multiPcRoot "AppData-PC-A"); Type = "Directory" },
    @{ Name = "PC-B AppData"; Path = (Join-Path $multiPcRoot "AppData-PC-B"); Type = "Directory" }
)

$results = New-Object System.Collections.Generic.List[object]

foreach ($check in $checks) {
    $exists = Test-Path -LiteralPath $check.Path
    $results.Add([pscustomobject]@{
        Name = $check.Name
        Path = $check.Path
        Exists = $exists
        Detail = if ($exists) { "OK" } else { "누락" }
    }) | Out-Null
}

$runAppAPath = Join-Path $multiPcRoot "Run-App-PC-A.cmd"
$runAppBPath = Join-Path $multiPcRoot "Run-App-PC-B.cmd"
$appSettingsPath = Join-Path $appRoot "appsettings.json"

if (Test-Path -LiteralPath $runAppAPath) {
    $runAppAContent = Get-Content -LiteralPath $runAppAPath -Raw -Encoding UTF8
    $results.Add([pscustomobject]@{
        Name = "Run-App-PC-A 상대경로"
        Path = $runAppAPath
        Exists = ($runAppAContent -match "APP_DIR=%~dp0\.\.\\App" -and $runAppAContent -match "AppData-PC-A")
        Detail = if ($runAppAContent -match "APP_DIR=%~dp0\.\.\\App" -and $runAppAContent -match "AppData-PC-A") { "OK" } else { "상대경로/PC-A AppData 지정 확인 필요" }
    }) | Out-Null
}

if (Test-Path -LiteralPath $runAppBPath) {
    $runAppBContent = Get-Content -LiteralPath $runAppBPath -Raw -Encoding UTF8
    $results.Add([pscustomobject]@{
        Name = "Run-App-PC-B 상대경로"
        Path = $runAppBPath
        Exists = ($runAppBContent -match "APP_DIR=%~dp0\.\.\\App" -and $runAppBContent -match "AppData-PC-B")
        Detail = if ($runAppBContent -match "APP_DIR=%~dp0\.\.\\App" -and $runAppBContent -match "AppData-PC-B") { "OK" } else { "상대경로/PC-B AppData 지정 확인 필요" }
    }) | Out-Null
}

if (Test-Path -LiteralPath $appSettingsPath) {
    try {
        $appSettings = Get-Content -LiteralPath $appSettingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $baseUrl = [string]$appSettings.Api.BaseUrl
        $results.Add([pscustomobject]@{
            Name = "테스트 앱 BaseUrl"
            Path = $appSettingsPath
            Exists = (-not [string]::IsNullOrWhiteSpace($baseUrl))
            Detail = if ([string]::IsNullOrWhiteSpace($baseUrl)) { "Api.BaseUrl 없음" } else { $baseUrl }
        }) | Out-Null
    }
    catch {
        $results.Add([pscustomobject]@{
            Name = "테스트 앱 BaseUrl"
            Path = $appSettingsPath
            Exists = $false
            Detail = "appsettings.json 파싱 실패: $($_.Exception.Message)"
        }) | Out-Null
    }
}

$failed = $results | Where-Object { -not $_.Exists }
$overallStatus = if ($failed.Count -eq 0) { "PASS" } else { "FAIL" }

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $reportDirectory = Join-Path $ProjectRoot "테스트 시행\기록"
    New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
    $OutputPath = Join-Path $reportDirectory ("multi-pc-readiness-{0}.md" -f (Get-Date -Format "yyyyMMdd-HHmmss"))
}
else {
    $reportDirectory = Split-Path -Parent $OutputPath
    if (-not [string]::IsNullOrWhiteSpace($reportDirectory)) {
        New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
    }
}

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("# 다중 PC 준비 점검 리포트") | Out-Null
$lines.Add("") | Out-Null
$lines.Add("- 실행시각: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')") | Out-Null
$lines.Add("- 결과: **$overallStatus**") | Out-Null
$lines.Add("- 실행환경 루트: $ExecutionRoot") | Out-Null
$lines.Add("") | Out-Null
$lines.Add("| 항목 | 결과 | 상세 | 경로 |") | Out-Null
$lines.Add("| --- | --- | --- | --- |") | Out-Null

foreach ($result in $results) {
    $status = if ($result.Exists) { "OK" } else { "FAIL" }
    $detail = ([string]$result.Detail).Replace("|", "\|")
    $pathCell = ([string]$result.Path).Replace("|", "\|")
    $lines.Add("| $($result.Name) | $status | $detail | $pathCell |") | Out-Null
}

[System.IO.File]::WriteAllText(
    $OutputPath,
    ($lines -join [Environment]::NewLine),
    (New-Object System.Text.UTF8Encoding($true)))

Write-Host "다중 PC 준비 리포트 저장: $OutputPath"
Write-Host "결과: $overallStatus"

if ($failed.Count -gt 0) {
    throw "다중 PC 준비 점검에서 실패가 확인되었습니다. 리포트: $OutputPath"
}
