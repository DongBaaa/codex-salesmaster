[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$SigningConfigPath,
    [string]$Channel = 'stable',
    [switch]$DeployToNas,
    [switch]$NoRestore
)

function Resolve-ProjectRoot {
    param([string]$ScriptPath)
    return (Resolve-Path (Join-Path (Split-Path -Parent $ScriptPath) '..\..')).Path
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
& dotnet build $solutionPath -c Release
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
& powershell -NoProfile -ExecutionPolicy Bypass -File $updateAssetsScript -ProjectRoot $ProjectRoot -Channel $Channel
if ($LASTEXITCODE -ne 0) {
    throw 'update assets publish failed.'
}

if ($DeployToNas) {
    $nasScript = Join-Path $ProjectRoot 'tools\nas\Publish-GeoraeplanNasRelease.ps1'
    & powershell -NoProfile -ExecutionPolicy Bypass -File $nasScript -ProjectRoot $ProjectRoot -MirrorToLive
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
