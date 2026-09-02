[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$RunnerSourcePath,
    [switch]$RunNow
)

$ErrorActionPreference = 'Stop'
$installRoot = 'D:\GeoraePlan-Codex-Handoff'
$runnerPath = Join-Path $installRoot 'Run-GeoraePlanCodexHandoff.ps1'
$commandPath = 'D:\GeoraePlan-Codex-Run.cmd'

if (-not (Test-Path -LiteralPath 'D:\' -PathType Container)) {
    throw 'D: drive is required for the GeoraePlan Codex handoff.'
}
if (-not (Test-Path -LiteralPath $RunnerSourcePath -PathType Leaf)) {
    throw "Runner source was not found: $RunnerSourcePath"
}
if (-not (Test-Path -LiteralPath (Join-Path $env:USERPROFILE '.ssh\itwserver_codex_ed25519') -PathType Leaf)) {
    throw 'The existing Windows-to-Linux GeoraePlan SSH key was not found.'
}

New-Item -ItemType Directory -Force -Path $installRoot | Out-Null
Copy-Item -LiteralPath $RunnerSourcePath -Destination $runnerPath -Force
$command = @(
    '@echo off',
    'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "D:\GeoraePlan-Codex-Handoff\Run-GeoraePlanCodexHandoff.ps1"',
    'set EXITCODE=%ERRORLEVEL%',
    'echo.',
    'if not "%EXITCODE%"=="0" echo The job failed. Send the error above to Codex.',
    'pause',
    'exit /b %EXITCODE%'
) -join "`r`n"
[IO.File]::WriteAllText($commandPath, $command + "`r`n", [Text.ASCIIEncoding]::new())

$desktopPath = [Environment]::GetFolderPath('DesktopDirectory')
if (-not [string]::IsNullOrWhiteSpace($desktopPath) -and (Test-Path -LiteralPath $desktopPath -PathType Container)) {
    $shortcutPath = Join-Path $desktopPath '거래플랜 Codex 작업 실행.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $commandPath
    $shortcut.WorkingDirectory = $installRoot
    $shortcut.Description = '거래플랜 Codex Windows 검증 작업 실행'
    $shortcut.Save()
}

Write-Host "설치 완료: $commandPath" -ForegroundColor Green
Write-Host '앞으로는 바탕화면의 거래플랜 Codex 작업 실행 바로가기를 사용하세요.'
if ($RunNow) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runnerPath
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
