[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$SourceFolder,
    [string]$OutputRoot,
    [string]$PackageName,
    [string]$AppDisplayName,
    [string]$ApiBaseUrl = 'https://trade.2884.kr',
    [switch]$SkipNativeInstallers
)

function Resolve-DotnetCommand {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot
    )

    $candidates = @(
        $env:DOTNET_EXE,
        'D:\.dotnet-sdk\dotnet.exe',
        'C:\Users\beene\AppData\Local\GeoraePlan.Android\dotnet8\dotnet.exe',
        'C:\Program Files\dotnet\dotnet.exe'
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    foreach ($candidate in $candidates) {
        if (-not (Test-Path -LiteralPath $candidate)) {
            continue
        }

        try {
            & $candidate --version *> $null
            if ($LASTEXITCODE -eq 0) {
                return (Resolve-Path -LiteralPath $candidate).Path
            }
        }
        catch {
            continue
        }
    }

    throw "Unable to locate a working dotnet executable for packaging under $ProjectRoot."
}

function Get-Utf8String {
    param(
        [Parameter(Mandatory = $true)][string]$Base64
    )

    return [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($Base64))
}

function Invoke-RobocopyMirror {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination,
        [int]$RetryCount = 5,
        [int]$RetryDelaySeconds = 2
    )

    New-Item -ItemType Directory -Force -Path $Destination | Out-Null

    for ($attempt = 1; $attempt -le $RetryCount; $attempt++) {
        $output = & robocopy $Source $Destination /MIR /R:2 /W:2 /NFL /NDL /NJH /NJS /NP 2>&1
        $exitCode = $LASTEXITCODE

        if ($exitCode -lt 8) {
            return
        }

        Write-Host ("robocopy attempt {0}/{1} failed ({2}): {3} -> {4}" -f $attempt, $RetryCount, $exitCode, $Source, $Destination)
        foreach ($line in @($output)) {
            if ($null -ne $line -and -not [string]::IsNullOrWhiteSpace($line.ToString())) {
                Write-Host ("  {0}" -f $line.ToString().TrimEnd())
            }
        }

        if ($attempt -lt $RetryCount) {
            Start-Sleep -Seconds $RetryDelaySeconds
        }
        else {
            throw "robocopy failed ($exitCode): $Source -> $Destination"
        }
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

function Get-ProjectVersion {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot
    )

    $projectPath = Join-Path $ProjectRoot 'Desktop\거래플랜.Desktop.App\거래플랜.Desktop.App.csproj'
    if (-not (Test-Path -LiteralPath $projectPath)) {
        throw "Desktop project not found: $projectPath"
    }

    [xml]$projectXml = Get-Content -LiteralPath $projectPath -Raw
    $versionNode = $projectXml.Project.PropertyGroup.Version | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($versionNode)) {
        throw "Desktop project version not found: $projectPath"
    }

    return [string]$versionNode
}

function Clear-DesktopReleaseArtifacts {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot
    )

    $projectDir = Join-Path $ProjectRoot 'Desktop\거래플랜.Desktop.App'
    foreach ($relativePath in @('bin\Release', 'obj\Release')) {
        $targetPath = Join-Path $projectDir $relativePath
        if (Test-Path -LiteralPath $targetPath) {
            Remove-Item -LiteralPath $targetPath -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

function Publish-DesktopApplication {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$PublishRoot,
        [Parameter(Mandatory = $true)][string]$DotnetExe
    )

    $desktopProject = Join-Path $ProjectRoot 'Desktop\거래플랜.Desktop.App\거래플랜.Desktop.App.csproj'
    if (-not (Test-Path -LiteralPath $desktopProject)) {
        throw "Desktop project not found: $desktopProject"
    }

    Clear-DesktopReleaseArtifacts -ProjectRoot $ProjectRoot
    Remove-Item -LiteralPath $PublishRoot -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $PublishRoot | Out-Null

    & $DotnetExe publish $desktopProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $PublishRoot | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Failed to publish desktop application for packaging.'
    }

    return $PublishRoot
}

function Ensure-DesktopLaunchCommand {
    param(
        [Parameter(Mandatory = $true)][string]$SourceFolder,
        [Parameter(Mandatory = $true)][string]$AppDisplayName
    )

    $launchScriptPath = Join-Path $SourceFolder '앱실행.cmd'
    $launchScript = @"
@echo off
setlocal
start "" "%~dp0$AppDisplayName.exe"
endlocal
"@
    $launchScript | Set-Content -LiteralPath $launchScriptPath -Encoding ASCII
}

function Set-DesktopPackageApiBaseUrl {
    param(
        [Parameter(Mandatory = $true)][string]$AppRoot,
        [Parameter(Mandatory = $true)][string]$BaseUrl
    )

    $normalizedBaseUrl = $BaseUrl.Trim().TrimEnd('/')
    if ([string]::IsNullOrWhiteSpace($normalizedBaseUrl)) {
        throw 'ApiBaseUrl is empty.'
    }

    $appSettingsPath = Join-Path $AppRoot 'appsettings.json'
    if (-not (Test-Path -LiteralPath $appSettingsPath)) {
        throw "appsettings.json not found in desktop package: $appSettingsPath"
    }

    $json = Get-Content -LiteralPath $appSettingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($null -eq $json.PSObject.Properties['Api']) {
        $json | Add-Member -NotePropertyName Api -NotePropertyValue ([pscustomobject]@{ BaseUrl = $normalizedBaseUrl })
    }
    elseif ($null -eq $json.Api.PSObject.Properties['BaseUrl']) {
        $json.Api | Add-Member -NotePropertyName BaseUrl -NotePropertyValue $normalizedBaseUrl
    }
    else {
        $json.Api.BaseUrl = $normalizedBaseUrl
    }

    $json | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $appSettingsPath -Encoding UTF8
}

function Prepare-DefaultClientSourceFolder {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$AppDisplayName,
        [Parameter(Mandatory = $true)][string]$DotnetExe
    )

    $publishRoot = Join-Path $env:TEMP 'georaeplan-desktop-package-publish'
    $sourceFolder = Publish-DesktopApplication -ProjectRoot $ProjectRoot -PublishRoot $publishRoot -DotnetExe $DotnetExe
    $publishedExeCandidates = @(
        (Join-Path $sourceFolder '거래플랜.Desktop.App.exe'),
        (Join-Path $sourceFolder "$AppDisplayName.exe")
    )
    $publishedExe = $publishedExeCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($publishedExe)) {
        throw "Published desktop executable not found under $sourceFolder"
    }

    $displayExePath = Join-Path $sourceFolder "$AppDisplayName.exe"
    if (-not (Test-Path -LiteralPath $displayExePath)) {
        Copy-Item -LiteralPath $publishedExe -Destination $displayExePath -Force
    }

    Ensure-DesktopLaunchCommand -SourceFolder $sourceFolder -AppDisplayName $AppDisplayName
    return $sourceFolder
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

$dotnetExe = Resolve-DotnetCommand -ProjectRoot $ProjectRoot
$env:DOTNET_EXE = $dotnetExe

$deploymentRoot = Get-DeploymentRoot -ProjectRoot $ProjectRoot
$desktopVersion = Get-ProjectVersion -ProjectRoot $ProjectRoot

if ([string]::IsNullOrWhiteSpace($SourceFolder)) {
    $SourceFolder = Prepare-DefaultClientSourceFolder -ProjectRoot $ProjectRoot -AppDisplayName $AppDisplayName -DotnetExe $dotnetExe
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Get-DefaultOutputRoot -DeploymentRoot $deploymentRoot
}

if (-not (Test-Path -LiteralPath $SourceFolder)) {
    throw "Source folder not found: $SourceFolder"
}

$SourceFolder = (Resolve-Path -LiteralPath $SourceFolder).Path
Write-Host "source_folder=$SourceFolder"

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
Set-DesktopPackageApiBaseUrl -AppRoot $appRoot -BaseUrl $ApiBaseUrl

$updaterProject = Join-Path $ProjectRoot 'Updater\거래플랜.Updater\거래플랜.Updater.csproj'
if (Test-Path -LiteralPath $updaterProject) {
    $updaterPublishRoot = Join-Path $env:TEMP 'georaeplan-updater-publish'
    Remove-Item -LiteralPath $updaterPublishRoot -Recurse -Force -ErrorAction SilentlyContinue
    & $dotnetExe publish $updaterProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $updaterPublishRoot | Out-Null
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
    [string]`$InstallRoot = ''
)

if ([string]::IsNullOrWhiteSpace(`$InstallRoot)) {
    `$programFilesRoot = [Environment]::GetFolderPath('ProgramFilesX86')
    if ([string]::IsNullOrWhiteSpace(`$programFilesRoot)) {
        `$programFilesRoot = [Environment]::GetFolderPath('ProgramFiles')
    }

    `$InstallRoot = Join-Path `$programFilesRoot 'tradeplan'
}

`$desktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) '__APP_DISPLAY_NAME__.lnk'
`$commonDesktopShortcut = Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) '__APP_DISPLAY_NAME__.lnk'
`$startMenuDir = Join-Path ([Environment]::GetFolderPath('Programs')) '__APP_DISPLAY_NAME__'
`$commonStartMenuDir = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) '__APP_DISPLAY_NAME__'
`$legacyUserRoot = Join-Path `$env:LOCALAPPDATA 'Programs\__APP_DISPLAY_NAME__'
`$localAppDataRoot = Join-Path `$env:LOCALAPPDATA '__APP_DISPLAY_NAME__'

Remove-Item -LiteralPath `$desktopShortcut -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath `$commonDesktopShortcut -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath `$startMenuDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath `$commonStartMenuDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath `$legacyUserRoot -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath `$localAppDataRoot -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath `$InstallRoot -Recurse -Force -ErrorAction SilentlyContinue

Write-Host '__APP_DISPLAY_NAME__ removed'
"@
$uninstallScriptBody = $uninstallScriptBody.Replace('__APP_DISPLAY_NAME__', $AppDisplayName)

$uninstallScriptBodyBase64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($uninstallScriptBody))

$installScriptTemplate = @"
param(
    [string]`$InstallRoot = '',
    [switch]`$NoLaunch,
    [switch]`$NoShortcuts,
    [string]`$LogPath = ''
)

`$ExpectedVersion = '__EXPECTED_VERSION__'

`$programFilesRoot = [Environment]::GetFolderPath('ProgramFilesX86')
if ([string]::IsNullOrWhiteSpace(`$programFilesRoot)) {
    `$programFilesRoot = [Environment]::GetFolderPath('ProgramFiles')
}

`$CanonicalInstallRoot = Join-Path `$programFilesRoot 'tradeplan'
`$LegacyUserRoot = Join-Path `$env:LOCALAPPDATA 'Programs\__APP_DISPLAY_NAME__'
if ([string]::IsNullOrWhiteSpace(`$InstallRoot)) {
    `$InstallRoot = `$CanonicalInstallRoot
}

`$requestedInstallRoot = `$InstallRoot
`$useLegacyBridgeCopy = `$false
if (-not [string]::IsNullOrWhiteSpace(`$requestedInstallRoot)) {
    `$requestedInstallRootFullPath = [System.IO.Path]::GetFullPath(`$requestedInstallRoot)
    `$legacyUserRootFullPath = [System.IO.Path]::GetFullPath(`$LegacyUserRoot)
    if ([string]::Equals(`$requestedInstallRootFullPath, `$legacyUserRootFullPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        `$useLegacyBridgeCopy = `$true
        `$InstallRoot = `$CanonicalInstallRoot
    }
}

function Invoke-RobocopyMirror {
    param(
        [Parameter(Mandatory = `$true)][string]`$Source,
        [Parameter(Mandatory = `$true)][string]`$Destination,
        [int]`$RetryCount = 5,
        [int]`$RetryDelaySeconds = 2
    )

    New-Item -ItemType Directory -Force -Path `$Destination | Out-Null

    for (`$attempt = 1; `$attempt -le `$RetryCount; `$attempt++) {
        `$output = & robocopy `$Source `$Destination /MIR /R:2 /W:2 /NFL /NDL /NJH /NJS /NP 2>&1
        `$exitCode = `$LASTEXITCODE

        if (`$exitCode -lt 8) {
            return
        }

        Write-InstallLog ("robocopy {0}/{1} 실패 ({2}): {3} -> {4}" -f `$attempt, `$RetryCount, `$exitCode, `$Source, `$Destination)
        foreach (`$line in @(`$output)) {
            if (`$null -ne `$line -and -not [string]::IsNullOrWhiteSpace(`$line.ToString())) {
                Write-InstallLog ("robocopy> {0}" -f `$line.ToString().TrimEnd())
            }
        }

        if (`$attempt -lt `$RetryCount) {
            Write-InstallLog ("파일 잠금 해제를 기다린 뒤 {0}초 후 재시도합니다." -f `$RetryDelaySeconds)
            Start-Sleep -Seconds `$RetryDelaySeconds
        }
        else {
            throw "robocopy failed (`$exitCode): `$Source -> `$Destination"
        }
    }
}

function Get-DirectorySizeBytes {
    param([Parameter(Mandatory = `$true)][string]`$Path)

    if (-not (Test-Path -LiteralPath `$Path)) {
        return 0L
    }

    return (Get-ChildItem -LiteralPath `$Path -File -Recurse | Measure-Object -Property Length -Sum).Sum
}

function Get-AvailableFreeBytes {
    param([Parameter(Mandatory = `$true)][string]`$Path)

    `$root = [System.IO.Path]::GetPathRoot([System.IO.Path]::GetFullPath(`$Path))
    if ([string]::IsNullOrWhiteSpace(`$root)) {
        throw "Drive root not found: `$Path"
    }

    return ([System.IO.DriveInfo]::new(`$root)).AvailableFreeSpace
}

function Format-Size {
    param([long]`$Bytes)

    `$units = @('B','KB','MB','GB','TB')
    `$value = [double]`$Bytes
    `$unitIndex = 0
    while (`$value -ge 1024 -and `$unitIndex -lt (`$units.Length - 1)) {
        `$value /= 1024
        `$unitIndex++
    }

    return ('{0:0.##} {1}' -f `$value, `$units[`$unitIndex])
}

function Normalize-VersionText {
    param([string]`$Value)

    if (`$null -eq `$Value) {
        `$normalized = ''
    }
    else {
        `$normalized = `$Value.Trim()
    }

    if (`$normalized.StartsWith('v', [System.StringComparison]::OrdinalIgnoreCase)) {
        `$normalized = `$normalized.Substring(1)
    }

    `$plusIndex = `$normalized.IndexOf('+')
    if (`$plusIndex -ge 0) {
        `$normalized = `$normalized.Substring(0, `$plusIndex)
    }

    return `$normalized
}

function Compare-Version {
    param(
        [string]`$Left,
        [string]`$Right
    )

    `$leftVersion = [Version]'0.0.0'
    `$rightVersion = [Version]'0.0.0'

    [Version]::TryParse((Normalize-VersionText `$Left), [ref]`$leftVersion) | Out-Null
    [Version]::TryParse((Normalize-VersionText `$Right), [ref]`$rightVersion) | Out-Null

    return `$leftVersion.CompareTo(`$rightVersion)
}

function Show-InstallError {
    param([Parameter(Mandatory = `$true)][string]`$Message)

    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show(`$Message, '__APP_DISPLAY_NAME__ 설치', 'OK', 'Error') | Out-Null
}

function Write-InstallLog {
    param([Parameter(Mandatory = `$true)][string]`$Message)

    `$line = ('{0} {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff'), `$Message)
    Write-Host `$line

    if (-not [string]::IsNullOrWhiteSpace(`$LogPath)) {
        try {
            Add-Content -LiteralPath `$LogPath -Value `$line -Encoding UTF8
        }
        catch {
            # ignore logging failures
        }
    }
}

function Test-ProtectedInstallRoot {
    param([Parameter(Mandatory = `$true)][string]`$Path)

    `$fullPath = [System.IO.Path]::GetFullPath(`$Path)
    `$protectedRoots = @(
        [Environment]::GetFolderPath('ProgramFiles'),
        [Environment]::GetFolderPath('ProgramFilesX86'),
        [Environment]::GetFolderPath('Windows')
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace(`$_) } | ForEach-Object {
        [System.IO.Path]::GetFullPath(`$_).TrimEnd('\') + '\'
    }

    foreach (`$root in `$protectedRoots) {
        if (`$fullPath.StartsWith(`$root, [System.StringComparison]::OrdinalIgnoreCase)) {
            return `$true
        }
    }

    return `$false
}

function Ensure-ElevatedIfNeeded {
    if (-not (Test-ProtectedInstallRoot -Path `$InstallRoot)) {
        return
    }

    `$currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
    `$principal = [Security.Principal.WindowsPrincipal]::new(`$currentIdentity)
    if (`$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        return
    }

    `$argumentParts = @(
        '-NoProfile',
        '-ExecutionPolicy Bypass',
        ('-File "{0}"' -f `$PSCommandPath),
        ('-InstallRoot "{0}"' -f `$InstallRoot)
    )

    if (`$NoLaunch) {
        `$argumentParts += '-NoLaunch'
    }

    if (`$NoShortcuts) {
        `$argumentParts += '-NoShortcuts'
    }

    if (-not [string]::IsNullOrWhiteSpace(`$LogPath)) {
        `$argumentParts += ('-LogPath "{0}"' -f `$LogPath)
    }

    `$arguments = `$argumentParts -join ' '

    try {
        Write-InstallLog '관리자 권한으로 설치를 다시 시작합니다.'
        `$elevated = Start-Process -FilePath 'powershell.exe' -ArgumentList `$arguments -Verb RunAs -Wait -PassThru
    }
    catch {
        Show-InstallError '관리자 권한 승인이 취소되어 업데이트를 진행할 수 없습니다.'
        exit 1
    }

    exit `$elevated.ExitCode
}

function Ensure-SufficientInstallSpace {
    param([Parameter(Mandatory = `$true)][string]`$SourceRoot)

    `$requiredBytes = [Math]::Max(268435456, (Get-DirectorySizeBytes -Path `$SourceRoot) + 134217728)
    `$availableBytes = Get-AvailableFreeBytes -Path `$InstallRoot

    if (`$availableBytes -lt `$requiredBytes) {
        Show-InstallError ("설치 드라이브 여유 공간이 부족합니다.`r`n필요 공간: {0}`r`n현재 여유 공간: {1}" -f (Format-Size `$requiredBytes), (Format-Size `$availableBytes))
        exit 1
    }
}

`$ErrorActionPreference = 'Stop'

try {
    Write-InstallLog ("설치 시작. InstallRoot={0}" -f `$InstallRoot)
    Ensure-ElevatedIfNeeded

    `$packageRoot = Split-Path -Parent `$MyInvocation.MyCommand.Path
    `$sourceRoot = Join-Path `$packageRoot 'App'
    `$protectedInstall = Test-ProtectedInstallRoot -Path `$InstallRoot
    `$desktopDir = if (`$protectedInstall) { [Environment]::GetFolderPath('CommonDesktopDirectory') } else { [Environment]::GetFolderPath('Desktop') }
    `$startMenuRoot = if (`$protectedInstall) { [Environment]::GetFolderPath('CommonPrograms') } else { [Environment]::GetFolderPath('Programs') }
    `$startMenuDir = Join-Path `$startMenuRoot '__APP_DISPLAY_NAME__'
    `$legacyUserRoot = `$LegacyUserRoot
    `$exePath = Join-Path `$InstallRoot '__APP_DISPLAY_NAME__.exe'
    `$uninstallScriptPath = Join-Path `$InstallRoot '__UNINSTALL_PS1_NAME__'

    if (-not (Test-Path -LiteralPath `$sourceRoot)) {
        throw "App source not found: `$sourceRoot"
    }

    Write-InstallLog '설치 공간을 확인합니다.'
    Ensure-SufficientInstallSpace -SourceRoot `$sourceRoot
    Write-InstallLog '파일 복사를 시작합니다.'
    Invoke-RobocopyMirror -Source `$sourceRoot -Destination `$InstallRoot
    if (`$useLegacyBridgeCopy) {
        Write-InstallLog ("기존 사용자 설치 경로도 함께 갱신합니다. LegacyRoot={0}" -f `$legacyUserRoot)
        Invoke-RobocopyMirror -Source `$sourceRoot -Destination `$legacyUserRoot
    }
    Write-InstallLog '파일 복사가 완료되었습니다.'

    if (-not (Test-Path -LiteralPath `$exePath)) {
        throw "설치된 실행 파일을 찾지 못했습니다: `$exePath"
    }

    `$installedVersion = (Get-Item -LiteralPath `$exePath).VersionInfo.ProductVersion
    if ([string]::IsNullOrWhiteSpace(`$installedVersion)) {
        throw '설치된 실행 파일 버전을 확인하지 못했습니다.'
    }

    if (Compare-Version `$installedVersion `$ExpectedVersion -lt 0) {
        throw ("설치된 실행 파일 버전이 예상보다 낮습니다. 기대 버전: {0}, 실제 버전: {1}" -f `$ExpectedVersion, (Normalize-VersionText `$installedVersion))
    }

    `$uninstallScriptContent = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('__UNINSTALL_SCRIPT_B64__'))
    `$uninstallScriptContent | Set-Content -LiteralPath `$uninstallScriptPath -Encoding UTF8

    `$installRootFullPath = [System.IO.Path]::GetFullPath(`$InstallRoot)
    `$legacyUserRootFullPath = [System.IO.Path]::GetFullPath(`$legacyUserRoot)
    if (-not `$useLegacyBridgeCopy -and -not [string]::Equals(`$installRootFullPath, `$legacyUserRootFullPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath `$legacyUserRoot -Recurse -Force -ErrorAction SilentlyContinue
    }

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
    Write-InstallLog ("설치 완료. Executable={0}" -f `$exePath)

    if (-not `$NoLaunch) {
        Write-InstallLog '설치 후 앱을 다시 실행합니다.'
        Start-Process -FilePath `$exePath -WorkingDirectory `$InstallRoot
    }
}
catch {
    Write-InstallLog ("설치 실패: {0}" -f `$_.Exception)
    Show-InstallError (`$_.Exception.Message)
    exit 1
}
"@

$installScript = $installScriptTemplate
$installScript = $installScript.Replace('__APP_DISPLAY_NAME__', $AppDisplayName)
$installScript = $installScript.Replace('__EXPECTED_VERSION__', $desktopVersion)
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
    "C:\Program Files (x86)\tradeplan",
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
        & powershell -ExecutionPolicy Bypass -File $nativeInstallerScript -ProjectRoot $ProjectRoot -SourceFolder $appRoot -OutputRoot $OutputRoot -PackageName $PackageName -AppDisplayName $AppDisplayName
        if ($LASTEXITCODE -ne 0) {
            throw 'Native installer generation failed.'
        }
    }
}
