[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$SigningConfigPath,
    [string]$Channel = 'stable',
    [switch]$DeployToNas,
    [switch]$NoRestore,
    [string]$DesktopMinimumSupportedVersion,
    [string]$AndroidMinimumSupportedVersion,
    [switch]$MandatoryDesktop,
    [switch]$MandatoryAndroid,
    [switch]$AllowLegacyLiveMirror,
    [switch]$AllowScheduledApplyTrigger,
    [string]$NasSshHost,
    [string]$NasSshUser,
    [int]$NasSshPort = 0,
    [string]$NasSshKeyPath,
    [string]$NasRemoteOpsPath
)

function Resolve-ProjectRoot {
    param([string]$ScriptPath)
    return (Resolve-Path (Join-Path (Split-Path -Parent $ScriptPath) '..\..')).Path
}

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

    throw "Unable to locate a working dotnet executable for full release under $ProjectRoot."
}

function Get-CsprojPropertyValue {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectFile,
        [Parameter(Mandatory = $true)][string]$PropertyName
    )

    [xml]$xml = Get-Content -LiteralPath $ProjectFile -Raw
    foreach ($group in $xml.Project.PropertyGroup) {
        $property = $group.$PropertyName
        if ($null -ne $property -and -not [string]::IsNullOrWhiteSpace([string]$property)) {
            return ([string]$property).Trim()
        }
    }

    return $null
}

function Resolve-ProjectFile {
    param(
        [Parameter(Mandatory = $true)][string]$RootPath,
        [Parameter(Mandatory = $true)][string]$Pattern
    )

    $match = Get-ChildItem -Path $RootPath -Recurse -File -Filter $Pattern | Select-Object -First 1
    if ($null -eq $match) {
        throw "Project file not found for pattern: $Pattern"
    }

    return $match.FullName
}

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = Resolve-ProjectRoot -ScriptPath $MyInvocation.MyCommand.Path
}

if ([string]::IsNullOrWhiteSpace($SigningConfigPath)) {
    $SigningConfigPath = Join-Path $ProjectRoot 'Mobile\GeoraePlan.Mobile.App\android-signing.local.json'
}

$dotnetExe = Resolve-DotnetCommand -ProjectRoot $ProjectRoot
$env:DOTNET_EXE = $dotnetExe

$desktopProject = Resolve-ProjectFile -RootPath (Join-Path $ProjectRoot 'Desktop') -Pattern '*.Desktop.App.csproj'
$androidProject = Resolve-ProjectFile -RootPath (Join-Path $ProjectRoot 'Mobile') -Pattern 'GeoraePlan.Mobile.App.csproj'
$desktopVersion = Get-CsprojPropertyValue -ProjectFile $desktopProject -PropertyName 'Version'
$androidVersion = Get-CsprojPropertyValue -ProjectFile $androidProject -PropertyName 'ApplicationDisplayVersion'

Write-Host "release_desktop_version=$desktopVersion"
Write-Host "release_android_version=$androidVersion"

$solution = Get-ChildItem -LiteralPath $ProjectRoot -File -Filter '*.sln' | Select-Object -First 1
if ($null -eq $solution) {
    throw 'Solution file not found.'
}
$solutionPath = $solution.FullName
& $dotnetExe build $solutionPath -c Release
if ($LASTEXITCODE -ne 0) {
    throw 'dotnet build failed.'
}

$desktopScript = Join-Path $ProjectRoot 'tools\release\Build-GeoraePlanDesktopInstaller.ps1'
& powershell -NoProfile -ExecutionPolicy Bypass -File $desktopScript -ProjectRoot $ProjectRoot
if ($LASTEXITCODE -ne 0) {
    throw 'desktop installer build failed.'
}

$androidScript = Join-Path $ProjectRoot 'tools\mobile\Build-GeoraePlanAndroidApk.ps1'
$androidArgs = @(
    '-NoProfile'
    '-ExecutionPolicy', 'Bypass'
    '-File', $androidScript
    '-ProjectRoot', $ProjectRoot
    '-SigningConfigPath', $SigningConfigPath
)
if ($NoRestore) {
    $androidArgs += '-NoRestore'
}
& powershell @androidArgs
if ($LASTEXITCODE -ne 0) {
    throw 'android apk build failed.'
}

$updateAssetsScript = Join-Path $ProjectRoot 'tools\release\Publish-GeoraePlanUpdateAssets.ps1'
$updateArgs = @(
    '-NoProfile'
    '-ExecutionPolicy', 'Bypass'
    '-File', $updateAssetsScript
    '-ProjectRoot', $ProjectRoot
    '-Channel', $Channel
)
if (-not [string]::IsNullOrWhiteSpace($DesktopMinimumSupportedVersion)) {
    $updateArgs += @('-DesktopMinimumSupportedVersion', $DesktopMinimumSupportedVersion)
}
if (-not [string]::IsNullOrWhiteSpace($AndroidMinimumSupportedVersion)) {
    $updateArgs += @('-AndroidMinimumSupportedVersion', $AndroidMinimumSupportedVersion)
}
if ($MandatoryDesktop) {
    $updateArgs += '-MandatoryDesktop'
}
if ($MandatoryAndroid) {
    $updateArgs += '-MandatoryAndroid'
}
& powershell @updateArgs
if ($LASTEXITCODE -ne 0) {
    throw 'update assets publish failed.'
}

if ($DeployToNas) {
    $nasScript = Join-Path $ProjectRoot 'tools\nas\Publish-GeoraeplanNasRelease.ps1'
    $nasArgs = @(
        '-NoProfile'
        '-ExecutionPolicy', 'Bypass'
        '-File', $nasScript
        '-ProjectRoot', $ProjectRoot
        '-MirrorToLive'
    )

    if ($AllowLegacyLiveMirror) {
        $nasArgs += '-AllowLegacyLiveMirror'
    }
    if ($AllowScheduledApplyTrigger) {
        $nasArgs += '-AllowScheduledApplyTrigger'
    }
    if (-not [string]::IsNullOrWhiteSpace($NasSshHost)) {
        $nasArgs += @('-NasSshHost', $NasSshHost)
    }
    if (-not [string]::IsNullOrWhiteSpace($NasSshUser)) {
        $nasArgs += @('-NasSshUser', $NasSshUser)
    }
    if ($NasSshPort -gt 0) {
        $nasArgs += @('-NasSshPort', $NasSshPort.ToString())
    }
    if (-not [string]::IsNullOrWhiteSpace($NasSshKeyPath)) {
        $nasArgs += @('-NasSshKeyPath', $NasSshKeyPath)
    }
    if (-not [string]::IsNullOrWhiteSpace($NasRemoteOpsPath)) {
        $nasArgs += @('-NasRemoteOpsPath', $NasRemoteOpsPath)
    }

    & powershell @nasArgs
    if ($LASTEXITCODE -ne 0) {
        throw 'NAS deployment failed.'
    }
}

$desktopInstaller = Join-Path $ProjectRoot '배포\거래플랜-PC-설치패키지.exe'
$androidApk = Join-Path $ProjectRoot "배포\거래플랜-안드로이드-v$androidVersion-signed.apk"
$manifestPath = Join-Path $ProjectRoot "배포\업데이트\manifest\$Channel.json"

Write-Host "release_pc_installer=$desktopInstaller"
Write-Host "release_android_apk=$androidApk"
Write-Host "release_update_manifest=$manifestPath"
