[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$OutputRoot,
    [string]$Channel = 'stable',
    [string]$DesktopPackagePath,
    [string]$AndroidPackagePath,
    [string]$DesktopVersion,
    [string]$AndroidVersion,
    [string]$DesktopNotes,
    [string]$AndroidNotes,
    [switch]$MandatoryDesktop,
    [switch]$MandatoryAndroid
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

function Copy-PackageWithMetadata {
    param(
        [Parameter(Mandatory = $true)][string]$SourcePath,
        [Parameter(Mandatory = $true)][string]$DestinationDirectory,
        [Parameter(Mandatory = $true)][string]$OutputFileName,
        [Parameter(Mandatory = $true)][string]$Platform,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][string]$Notes,
        [Parameter(Mandatory = $true)][bool]$Mandatory
    )

    New-Item -ItemType Directory -Force -Path $DestinationDirectory | Out-Null
    $destinationPath = Join-Path $DestinationDirectory $OutputFileName
    Copy-Item -LiteralPath $SourcePath -Destination $destinationPath -Force

    $hash = Get-FileHash -Algorithm SHA256 -LiteralPath $destinationPath
    $fileInfo = Get-Item -LiteralPath $destinationPath

    return [ordered]@{
        platform = $Platform
        version = $Version
        mandatory = $Mandatory
        packageUrl = "/updates/download/$Platform/$([Uri]::EscapeDataString($OutputFileName))"
        fileName = $OutputFileName
        sha256 = $hash.Hash
        fileSize = [int64]$fileInfo.Length
        notes = $Notes
        releasedAtUtc = [DateTime]::UtcNow.ToString('o')
    }
}

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = Resolve-ProjectRoot -ScriptPath $MyInvocation.MyCommand.Path
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $ProjectRoot '배포\업데이트'
}

if ([string]::IsNullOrWhiteSpace($DesktopPackagePath)) {
    $desktopCandidates = @(
        (Join-Path $ProjectRoot '배포\설치패키지\관리자용\거래플랜-PC-설치패키지.zip'),
        (Join-Path $ProjectRoot '배포\설치패키지\거래플랜-PC-설치패키지.zip')
    )
    $DesktopPackagePath = $desktopCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}

if ([string]::IsNullOrWhiteSpace($AndroidPackagePath)) {
    $androidNamed = Get-ChildItem -Path (Join-Path $ProjectRoot 'Mobile\artifacts\android') -File -Filter '거래플랜-안드로이드-*.apk' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($null -ne $androidNamed) {
        $AndroidPackagePath = $androidNamed.FullName
    }
}

$desktopProject = Join-Path $ProjectRoot 'Desktop\거래플랜.Desktop.App\거래플랜.Desktop.App.csproj'
$androidProject = Join-Path $ProjectRoot 'Mobile\GeoraePlan.Mobile.App\GeoraePlan.Mobile.App.csproj'

if ([string]::IsNullOrWhiteSpace($DesktopVersion)) {
    $DesktopVersion = Get-CsprojPropertyValue -ProjectFile $desktopProject -PropertyName 'Version'
}
if ([string]::IsNullOrWhiteSpace($AndroidVersion)) {
    $AndroidVersion = Get-CsprojPropertyValue -ProjectFile $androidProject -PropertyName 'ApplicationDisplayVersion'
}
if ([string]::IsNullOrWhiteSpace($DesktopNotes)) {
    $DesktopNotes = '내부 업데이트, 거래처 상세/첨부/동기화 개선 반영'
}
if ([string]::IsNullOrWhiteSpace($AndroidNotes)) {
    $AndroidNotes = '모바일 상세탭/첨부 보기/내부 업데이트 확인 기능 반영'
}

$manifestRoot = Join-Path $OutputRoot 'manifest'
$downloadsRoot = Join-Path $OutputRoot 'downloads'
New-Item -ItemType Directory -Force -Path $manifestRoot | Out-Null
New-Item -ItemType Directory -Force -Path $downloadsRoot | Out-Null

$manifest = [ordered]@{
    channel = $Channel
    generatedAtUtc = [DateTime]::UtcNow.ToString('o')
    desktop = $null
    android = $null
}

if (-not [string]::IsNullOrWhiteSpace($DesktopPackagePath) -and (Test-Path -LiteralPath $DesktopPackagePath)) {
    $desktopFileName = "거래플랜-PC-설치패키지-v$DesktopVersion.zip"
    $manifest.desktop = Copy-PackageWithMetadata -SourcePath $DesktopPackagePath -DestinationDirectory (Join-Path $downloadsRoot 'desktop') -OutputFileName $desktopFileName -Platform 'desktop' -Version $DesktopVersion -Notes $DesktopNotes -Mandatory:$MandatoryDesktop
}

if (-not [string]::IsNullOrWhiteSpace($AndroidPackagePath) -and (Test-Path -LiteralPath $AndroidPackagePath)) {
    $androidFileName = "거래플랜-안드로이드-v$AndroidVersion.apk"
    $manifest.android = Copy-PackageWithMetadata -SourcePath $AndroidPackagePath -DestinationDirectory (Join-Path $downloadsRoot 'android') -OutputFileName $androidFileName -Platform 'android' -Version $AndroidVersion -Notes $AndroidNotes -Mandatory:$MandatoryAndroid
}

$manifestPath = Join-Path $manifestRoot ($Channel + '.json')
$manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

Write-Host "update_manifest=$manifestPath"
if ($manifest.desktop) { Write-Host "desktop_package=$($manifest.desktop.fileName)" }
if ($manifest.android) { Write-Host "android_package=$($manifest.android.fileName)" }
