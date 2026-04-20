[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$ExecutionRoot,
    [string]$MultiPcRoot,
    [switch]$ResetClientData,
    [switch]$LaunchServer,
    [switch]$LaunchClients
)

$ErrorActionPreference = 'Stop'

function Resolve-ProjectRoot {
    param([Parameter(Mandatory = $true)][string]$ScriptRoot)
    return (Resolve-Path (Join-Path $ScriptRoot '..')).Path
}

function New-Utf8NoBomEncoding {
    return New-Object System.Text.UTF8Encoding($false)
}

function Write-Utf8File {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Content
    )

    $directory = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    [System.IO.File]::WriteAllText($Path, $Content, (New-Utf8NoBomEncoding))
}

function Invoke-RobocopyMirror {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination,
        [string[]]$ExcludeDirectories = @()
    )

    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    $arguments = @(
        $Source,
        $Destination,
        '/MIR',
        '/R:2',
        '/W:2',
        '/NFL',
        '/NDL',
        '/NJH',
        '/NJS',
        '/NP'
    )

    if ($ExcludeDirectories.Count -gt 0) {
        $arguments += '/XD'
        $arguments += $ExcludeDirectories
    }

    & robocopy @arguments | Out-Null
    if ($LASTEXITCODE -ge 8) {
        throw "robocopy failed: $Source -> $Destination"
    }
}

function Reset-TransientAppDataDirectories {
    param(
        [Parameter(Mandatory = $true)][string]$Root
    )

    foreach ($child in @('backup', 'diagnostics', 'logs', 'temp')) {
        $path = Join-Path $Root $child
        Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Force -Path $path | Out-Null
    }
}

function New-ClientRunScript {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$DataFolderName,
        [Parameter(Mandatory = $true)][string]$WindowTitle
    )

    $content = @"
@echo off
setlocal EnableExtensions
set "APP_ROOT=%~dp0$DataFolderName"
set "APP_DIR=%~dp0..\App"
set "APP_EXE="
for %%I in ("%APP_DIR%\*.Desktop.App.exe") do if not defined APP_EXE set "APP_EXE=%%~fI"
for %%I in ("%APP_DIR%\*.App.exe") do if not defined APP_EXE set "APP_EXE=%%~fI"
if not defined APP_EXE (
  echo [GeoraePlan] App exe not found in %APP_DIR%
  pause
  exit /b 1
)
set "GEORAEPLAN_APP_ROOT=%APP_ROOT%"
set "GEORAEPLAN_DISABLE_LEGACY_MERGE=1"
set "GEORAEPLAN_TEST_MODE=1"
start "$WindowTitle" /D "%APP_DIR%" "%APP_EXE%"
endlocal
"@

    Write-Utf8File -Path $Path -Content $content
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = Resolve-ProjectRoot -ScriptRoot $scriptRoot
}
if ([string]::IsNullOrWhiteSpace($ExecutionRoot)) {
    $ExecutionRoot = Join-Path $scriptRoot '실행환경'
}
if ([string]::IsNullOrWhiteSpace($MultiPcRoot)) {
    $MultiPcRoot = Join-Path $ExecutionRoot 'MultiPC'
}

$executionRoot = (Resolve-Path -LiteralPath $ExecutionRoot).Path
$appRoot = Join-Path $executionRoot 'App'
$baseAppDataRoot = Join-Path $executionRoot 'AppData'
$runServerCmd = Join-Path $executionRoot 'Run-Server.cmd'

foreach ($requiredPath in @($appRoot, $baseAppDataRoot, $runServerCmd)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "다중 PC 검증 준비 전에 테스트 실행환경이 먼저 필요합니다: $requiredPath"
    }
}

New-Item -ItemType Directory -Force -Path $MultiPcRoot | Out-Null

$clients = @(
    [pscustomobject]@{
        Code = 'PC-A'
        DataRoot = Join-Path $MultiPcRoot 'AppData-PC-A'
        ScriptPath = Join-Path $MultiPcRoot 'Run-App-PC-A.cmd'
        WindowTitle = 'GeoraePlan Test App PC-A'
    },
    [pscustomobject]@{
        Code = 'PC-B'
        DataRoot = Join-Path $MultiPcRoot 'AppData-PC-B'
        ScriptPath = Join-Path $MultiPcRoot 'Run-App-PC-B.cmd'
        WindowTitle = 'GeoraePlan Test App PC-B'
    }
)

foreach ($client in $clients) {
    if ($ResetClientData -or -not (Test-Path -LiteralPath $client.DataRoot)) {
        Invoke-RobocopyMirror -Source $baseAppDataRoot -Destination $client.DataRoot -ExcludeDirectories @('backup', 'diagnostics', 'logs', 'temp')
        Reset-TransientAppDataDirectories -Root $client.DataRoot
    }

    New-ClientRunScript -Path $client.ScriptPath -DataFolderName ([System.IO.Path]::GetFileName($client.DataRoot)) -WindowTitle $client.WindowTitle
}

$runServerWrapper = @"
@echo off
setlocal EnableExtensions
set "EXEC_ROOT=%~dp0.."
start "" /D "%EXEC_ROOT%" "%EXEC_ROOT%\Run-Server.cmd"
endlocal
"@
Write-Utf8File -Path (Join-Path $MultiPcRoot 'Run-Server.cmd') -Content $runServerWrapper

$runAllContent = @"
@echo off
setlocal EnableExtensions
call "%~dp0Run-Server.cmd"
timeout /t 4 /nobreak >nul
call "%~dp0Run-App-PC-A.cmd"
timeout /t 2 /nobreak >nul
call "%~dp0Run-App-PC-B.cmd"
endlocal
"@
Write-Utf8File -Path (Join-Path $MultiPcRoot 'Run-All-MultiPC.cmd') -Content $runAllContent

$resetPs1Content = @'
param()
& "$PSScriptRoot\..\..\Prepare-MultiPC.ps1" -ExecutionRoot "$PSScriptRoot\.." -MultiPcRoot "$PSScriptRoot" -ResetClientData
'@
Write-Utf8File -Path (Join-Path $MultiPcRoot 'Reset-ClientData.ps1') -Content $resetPs1Content
Remove-Item -LiteralPath (Join-Path $MultiPcRoot 'Reset-ClientData.cmd') -Force -ErrorAction SilentlyContinue

$readmeContent = @(
    '# 다중 PC 검증 실행 파일',
    '',
    '- Run-Server.cmd : 테스트 서버만 실행',
    '- Run-App-PC-A.cmd : PC-A 전용 AppData로 데스크톱 실행',
    '- Run-App-PC-B.cmd : PC-B 전용 AppData로 데스크톱 실행',
    '- Run-All-MultiPC.cmd : 서버 + PC-A + PC-B 순서로 실행',
    '- Reset-ClientData.ps1 : 기본 AppData 스냅샷으로 PC-A/PC-B 데이터를 다시 복사',
    '',
    "- 공통 앱 폴더: $appRoot",
    "- 기본 스냅샷: $baseAppDataRoot",
    "- PC-A AppData: $($clients[0].DataRoot)",
    "- PC-B AppData: $($clients[1].DataRoot)"
) -join [Environment]::NewLine
Write-Utf8File -Path (Join-Path $MultiPcRoot 'README.txt') -Content $readmeContent

if ($LaunchServer) {
    Start-Process -FilePath (Join-Path $MultiPcRoot 'Run-Server.cmd')
}
if ($LaunchClients) {
    if (-not $LaunchServer) {
        Start-Sleep -Seconds 1
    }

    Start-Process -FilePath $clients[0].ScriptPath
    Start-Sleep -Seconds 2
    Start-Process -FilePath $clients[1].ScriptPath
}

Write-Host '다중 PC 검증 실행환경을 준비했습니다.' -ForegroundColor Green
Write-Host "- 실행 루트: $MultiPcRoot" -ForegroundColor Green
Write-Host "- PC-A AppData: $($clients[0].DataRoot)" -ForegroundColor Green
Write-Host "- PC-B AppData: $($clients[1].DataRoot)" -ForegroundColor Green
Write-Host '- Run-All-MultiPC.cmd 또는 각 Run-App-PC-*.cmd를 사용하세요.' -ForegroundColor Green
