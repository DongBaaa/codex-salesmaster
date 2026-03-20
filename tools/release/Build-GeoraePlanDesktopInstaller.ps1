[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$SourceFolder,
    [string]$OutputRoot,
    [string]$PackageName,
    [string]$AppDisplayName,
    [switch]$SkipNativeInstallers
)

function Get-Utf8String {
    param(
        [Parameter(Mandatory = $true)][string]$Base64
    )

    return [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($Base64))
}

function Invoke-RobocopyMirror {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    & robocopy $Source $Destination /MIR /R:2 /W:2 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) {
        throw "robocopy failed ($LASTEXITCODE): $Source -> $Destination"
    }
}

function Get-DeploymentRoot {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot
    )

    $candidate = Get-ChildItem -LiteralPath $ProjectRoot -Directory |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'Set-ApiBaseUrl.ps1') } |
        Select-Object -First 1 -ExpandProperty FullName

    if ([string]::IsNullOrWhiteSpace($candidate)) {
        throw 'Deployment root not found under project root.'
    }

    return $candidate
}

function Get-DefaultClientSourceFolder {
    param(
        [Parameter(Mandatory = $true)][string]$DeploymentRoot
    )

    $candidates = Get-ChildItem -LiteralPath $DeploymentRoot -Directory |
        Where-Object {
            (Test-Path -LiteralPath (Join-Path $_.FullName 'appsettings.json')) -and
            ((Get-ChildItem -LiteralPath $_.FullName -File -Filter '*.cmd' | Measure-Object).Count -ge 1) -and
            ((Get-ChildItem -LiteralPath $_.FullName -File -Filter '*.exe' | Measure-Object).Count -ge 1) -and
            ((Get-ChildItem -LiteralPath $_.FullName -File -Filter '*.db' | Measure-Object).Count -eq 0)
        } |
        Sort-Object FullName

    $preferred = $candidates |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName "$AppDisplayName.exe") } |
        Select-Object -First 1

    if ($null -eq $preferred) {
        $preferred = $candidates |
        Where-Object { (Get-ChildItem -LiteralPath $_.FullName -File -Filter '*.pdb' | Measure-Object).Count -eq 0 } |
        Select-Object -First 1
    }

    if ($null -eq $preferred) {
        $preferred = $candidates | Select-Object -First 1
    }

    if ($null -eq $preferred) {
        throw 'Desktop client deployment source folder not found.'
    }

    return $preferred.FullName
}

function Get-DefaultOutputRoot {
    param(
        [Parameter(Mandatory = $true)][string]$DeploymentRoot
    )

    return $DeploymentRoot
}

if ([string]::IsNullOrWhiteSpace($AppDisplayName)) {
    $AppDisplayName = Get-Utf8String '6rGw656Y7ZSM656c'
}

if ([string]::IsNullOrWhiteSpace($PackageName)) {
    $PackageName = Get-Utf8String '6rGw656Y7ZSM656cLVBDLeyEpOy5mO2MqO2CpOyngA=='
}

$installCmdName = Get-Utf8String '6rGw656Y7ZSM656cLeyEpOy5mC5jbWQ='
$removeShortcutSuffix = Get-Utf8String 'IOygnOqxsC5sbms='

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = (Resolve-Path (Join-Path $scriptRoot '..\..')).Path
}

$deploymentRoot = Get-DeploymentRoot -ProjectRoot $ProjectRoot

if ([string]::IsNullOrWhiteSpace($SourceFolder)) {
    $SourceFolder = Get-DefaultClientSourceFolder -DeploymentRoot $deploymentRoot
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Get-DefaultOutputRoot -DeploymentRoot $deploymentRoot
}

if (-not (Test-Path -LiteralPath $SourceFolder)) {
    throw "Source folder not found: $SourceFolder"
}

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
$adminOutputRoot = Join-Path $OutputRoot '관리자용'
New-Item -ItemType Directory -Force -Path $adminOutputRoot | Out-Null

$legacyRootPackage = Join-Path $OutputRoot $PackageName
if (Test-Path -LiteralPath $legacyRootPackage) {
    Remove-Item -LiteralPath $legacyRootPackage -Recurse -Force -ErrorAction SilentlyContinue
}
$legacyRootZip = Join-Path $OutputRoot ($PackageName + '.zip')
if (Test-Path -LiteralPath $legacyRootZip) {
    Remove-Item -LiteralPath $legacyRootZip -Force -ErrorAction SilentlyContinue
}

$packageRoot = Join-Path $adminOutputRoot $PackageName
$appRoot = Join-Path $packageRoot 'App'
$zipPath = Join-Path $adminOutputRoot ($PackageName + '.zip')

Remove-Item -LiteralPath $packageRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null

Invoke-RobocopyMirror -Source $SourceFolder -Destination $appRoot

$updaterProject = Join-Path $ProjectRoot 'Updater\거래플랜.Updater\거래플랜.Updater.csproj'
if (Test-Path -LiteralPath $updaterProject) {
    $updaterPublishRoot = Join-Path $env:TEMP 'georaeplan-updater-publish'
    Remove-Item -LiteralPath $updaterPublishRoot -Recurse -Force -ErrorAction SilentlyContinue
    & dotnet publish $updaterProject -c Release -o $updaterPublishRoot | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Invoke-RobocopyMirror -Source $updaterPublishRoot -Destination (Join-Path $appRoot 'Updater')
    }
}

$serverUrl = ''
$appSettingsPath = Join-Path $SourceFolder 'appsettings.json'
if (Test-Path -LiteralPath $appSettingsPath) {
    try {
        $appSettings = Get-Content -LiteralPath $appSettingsPath -Raw | ConvertFrom-Json
        if ($null -ne $appSettings.Api -and -not [string]::IsNullOrWhiteSpace($appSettings.Api.BaseUrl)) {
            $serverUrl = [string]$appSettings.Api.BaseUrl
        }
    }
    catch {
        $serverUrl = ''
    }
}

$installPs1Name = 'Install-GeoraePlan.ps1'
$uninstallPs1Name = 'Uninstall-GeoraePlan.ps1'

$uninstallScriptBody = @"
param(
    [string]`$InstallRoot = (Join-Path `$env:LOCALAPPDATA 'Programs\__APP_DISPLAY_NAME__')
)

`$desktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) '__APP_DISPLAY_NAME__.lnk'
`$startMenuDir = Join-Path `$env:APPDATA 'Microsoft\Windows\Start Menu\Programs\__APP_DISPLAY_NAME__'

Remove-Item -LiteralPath `$desktopShortcut -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath `$startMenuDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath `$InstallRoot -Recurse -Force -ErrorAction SilentlyContinue

Write-Host '__APP_DISPLAY_NAME__ removed'
"@
$uninstallScriptBody = $uninstallScriptBody.Replace('__APP_DISPLAY_NAME__', $AppDisplayName)

$uninstallScriptBodyBase64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($uninstallScriptBody))

$installScriptTemplate = @"
param(
    [string]`$InstallRoot = (Join-Path `$env:LOCALAPPDATA 'Programs\__APP_DISPLAY_NAME__'),
    [switch]`$NoLaunch,
    [switch]`$NoShortcuts
)

function Invoke-RobocopyMirror {
    param(
        [Parameter(Mandatory = `$true)][string]`$Source,
        [Parameter(Mandatory = `$true)][string]`$Destination
    )

    New-Item -ItemType Directory -Force -Path `$Destination | Out-Null
    & robocopy `$Source `$Destination /MIR /R:2 /W:2 /NFL /NDL /NJH /NJS /NP | Out-Null
    if (`$LASTEXITCODE -ge 8) {
        throw "robocopy failed (`$LASTEXITCODE): `$Source -> `$Destination"
    }
}

`$ErrorActionPreference = 'Stop'

`$packageRoot = Split-Path -Parent `$MyInvocation.MyCommand.Path
`$sourceRoot = Join-Path `$packageRoot 'App'
`$desktopDir = [Environment]::GetFolderPath('Desktop')
`$startMenuDir = Join-Path `$env:APPDATA 'Microsoft\Windows\Start Menu\Programs\__APP_DISPLAY_NAME__'
`$exePath = Join-Path `$InstallRoot '__APP_DISPLAY_NAME__.exe'
`$uninstallScriptPath = Join-Path `$InstallRoot '__UNINSTALL_PS1_NAME__'

if (-not (Test-Path -LiteralPath `$sourceRoot)) {
    throw "App source not found: `$sourceRoot"
}

Invoke-RobocopyMirror -Source `$sourceRoot -Destination `$InstallRoot

`$uninstallScriptContent = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('__UNINSTALL_SCRIPT_B64__'))
`$uninstallScriptContent | Set-Content -LiteralPath `$uninstallScriptPath -Encoding UTF8

if (-not `$NoShortcuts) {
    New-Item -ItemType Directory -Force -Path `$startMenuDir | Out-Null
    `$shell = New-Object -ComObject WScript.Shell

    foreach (`$shortcutPath in @(
        (Join-Path `$desktopDir '__APP_DISPLAY_NAME__.lnk'),
        (Join-Path `$startMenuDir '__APP_DISPLAY_NAME__.lnk')
    )) {
        `$shortcut = `$shell.CreateShortcut(`$shortcutPath)
        `$shortcut.TargetPath = `$exePath
        `$shortcut.WorkingDirectory = `$InstallRoot
        `$shortcut.Save()
    }

    `$removeShortcut = `$shell.CreateShortcut((Join-Path `$startMenuDir '__APP_DISPLAY_NAME____REMOVE_SHORTCUT_SUFFIX__'))
    `$removeShortcut.TargetPath = 'powershell.exe'
    `$removeShortcut.Arguments = "-ExecutionPolicy Bypass -File ``"`$uninstallScriptPath``""
    `$removeShortcut.WorkingDirectory = `$InstallRoot
    `$removeShortcut.Save()
}

Write-Host "Install complete: `$InstallRoot"
Write-Host "Executable: `$exePath"

if (-not `$NoLaunch) {
    Start-Process -FilePath `$exePath -WorkingDirectory `$InstallRoot
}
"@

$installScript = $installScriptTemplate
$installScript = $installScript.Replace('__APP_DISPLAY_NAME__', $AppDisplayName)
$installScript = $installScript.Replace('__UNINSTALL_PS1_NAME__', $uninstallPs1Name)
$installScript = $installScript.Replace('__UNINSTALL_SCRIPT_B64__', $uninstallScriptBodyBase64)
$installScript = $installScript.Replace('__REMOVE_SHORTCUT_SUFFIX__', $removeShortcutSuffix)
$cmdScript = @"
@echo off
setlocal
powershell -ExecutionPolicy Bypass -File "%~dp0$installPs1Name"
endlocal
"@

$readme = @(
    "$AppDisplayName PC install package",
    '',
    '1. Extract the zip file.',
    "2. Run '$installCmdName'.",
    "3. After install, launch '$AppDisplayName' from the desktop or Start Menu.",
    '',
    'Default install path:',
    "%LOCALAPPDATA%\Programs\$AppDisplayName",
    '',
    'Default server URL:',
    $(if ([string]::IsNullOrWhiteSpace($serverUrl)) { 'Check appsettings.json' } else { $serverUrl })
) -join [Environment]::NewLine

$installScript | Set-Content -LiteralPath (Join-Path $packageRoot $installPs1Name) -Encoding UTF8
$cmdScript | Set-Content -LiteralPath (Join-Path $packageRoot $installCmdName) -Encoding ASCII
$readme | Set-Content -LiteralPath (Join-Path $packageRoot 'README.txt') -Encoding UTF8

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $zipPath -CompressionLevel Optimal

Write-Host "package_ready root=$packageRoot"
Write-Host "package_zip=$zipPath"

if (-not $SkipNativeInstallers) {
    $nativeInstallerScript = Join-Path $scriptRoot 'Build-GeoraePlanDesktopNativeInstallers.ps1'
    if (Test-Path -LiteralPath $nativeInstallerScript) {
        & powershell -ExecutionPolicy Bypass -File $nativeInstallerScript -ProjectRoot $ProjectRoot -SourceFolder $SourceFolder -OutputRoot $OutputRoot -PackageName $PackageName -AppDisplayName $AppDisplayName
        if ($LASTEXITCODE -ne 0) {
            throw 'Native installer generation failed.'
        }
    }
}
