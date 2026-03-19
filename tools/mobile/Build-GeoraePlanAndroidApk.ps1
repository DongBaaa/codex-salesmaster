[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$ProjectFile,
    [string]$SigningConfigPath,
    [string]$KeystorePath,
    [string]$KeyAlias,
    [string]$StorePass,
    [string]$KeyPass,
    [string]$Configuration = 'Release',
    [string]$Framework = 'net8.0-android',
    [string]$OutputRoot,
    [string]$VersionName,
    [int]$VersionCode,
    [switch]$SkipEnvironmentCheck,
    [switch]$NoRestore
)

function Resolve-DefaultProjectRoot {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptPath
    )

    return (Resolve-Path (Join-Path (Split-Path -Parent $ScriptPath) '..\..')).Path
}

function Resolve-PathIfRelative {
    param(
        [Parameter(Mandatory = $true)][string]$PathValue,
        [Parameter(Mandatory = $true)][string]$BaseDirectory
    )

    if ([System.IO.Path]::IsPathRooted($PathValue)) {
        return $PathValue
    }

    return (Join-Path $BaseDirectory $PathValue)
}

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = Resolve-DefaultProjectRoot -ScriptPath $MyInvocation.MyCommand.Path
}

if ([string]::IsNullOrWhiteSpace($ProjectFile)) {
    $ProjectFile = Join-Path $ProjectRoot 'Mobile\GeoraePlan.Mobile.App\GeoraePlan.Mobile.App.csproj'
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $ProjectRoot 'Mobile\artifacts\android'
}

if (-not $SkipEnvironmentCheck) {
    $envCheckScript = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'Test-GeoraePlanAndroidEnvironment.ps1'
    & $envCheckScript -ProjectRoot $ProjectRoot -ProjectFile $ProjectFile
}

if (-not (Test-Path -LiteralPath $ProjectFile)) {
    throw "Project file not found: $ProjectFile"
}

$signingConfigDirectory = $ProjectRoot
if (-not [string]::IsNullOrWhiteSpace($SigningConfigPath)) {
    if (-not (Test-Path -LiteralPath $SigningConfigPath)) {
        throw "Signing config not found: $SigningConfigPath"
    }

    $signingConfig = Get-Content -LiteralPath $SigningConfigPath -Raw | ConvertFrom-Json
    $signingConfigDirectory = Split-Path -Parent (Resolve-Path -LiteralPath $SigningConfigPath)

    if ([string]::IsNullOrWhiteSpace($KeystorePath) -and -not [string]::IsNullOrWhiteSpace($signingConfig.keystorePath)) {
        $KeystorePath = [string]$signingConfig.keystorePath
    }

    if ([string]::IsNullOrWhiteSpace($KeyAlias) -and -not [string]::IsNullOrWhiteSpace($signingConfig.keyAlias)) {
        $KeyAlias = [string]$signingConfig.keyAlias
    }

    if ([string]::IsNullOrWhiteSpace($StorePass) -and -not [string]::IsNullOrWhiteSpace($signingConfig.storePass)) {
        $StorePass = [string]$signingConfig.storePass
    }

    if ([string]::IsNullOrWhiteSpace($KeyPass) -and -not [string]::IsNullOrWhiteSpace($signingConfig.keyPass)) {
        $KeyPass = [string]$signingConfig.keyPass
    }
}

if ([string]::IsNullOrWhiteSpace($KeystorePath)) {
    throw 'KeystorePath is required. Pass it directly or via -SigningConfigPath.'
}

$KeystorePath = Resolve-PathIfRelative -PathValue $KeystorePath -BaseDirectory $signingConfigDirectory

if (-not (Test-Path -LiteralPath $KeystorePath)) {
    throw "Keystore not found: $KeystorePath"
}

if ([string]::IsNullOrWhiteSpace($KeyAlias)) {
    throw 'KeyAlias is required.'
}

if ([string]::IsNullOrWhiteSpace($StorePass)) {
    throw 'StorePass is required.'
}

if ([string]::IsNullOrWhiteSpace($KeyPass)) {
    throw 'KeyPass is required.'
}

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null

$timestamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$publishDirectory = Join-Path $OutputRoot ("publish_" + $timestamp)
New-Item -ItemType Directory -Force -Path $publishDirectory | Out-Null

$arguments = @(
    'publish'
    $ProjectFile
    '-c', $Configuration
    '-f', $Framework
    '--output', $publishDirectory
    '-p:AndroidPackageFormat=apk'
    '-p:AndroidKeyStore=true'
    "-p:AndroidSigningKeyStore=$KeystorePath"
    "-p:AndroidSigningKeyAlias=$KeyAlias"
    "-p:AndroidSigningStorePass=$StorePass"
    "-p:AndroidSigningKeyPass=$KeyPass"
    '-p:ArchiveOnBuild=true'
)

if ($NoRestore) {
    $arguments += '--no-restore'
}

if (-not [string]::IsNullOrWhiteSpace($VersionName)) {
    $arguments += "-p:ApplicationDisplayVersion=$VersionName"
}

if ($VersionCode -gt 0) {
    $arguments += "-p:ApplicationVersion=$VersionCode"
}

& dotnet @arguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$apkFile = Get-ChildItem -LiteralPath $publishDirectory -Recurse -File -Filter '*.apk' |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if ($null -eq $apkFile) {
    throw "No APK file was produced under $publishDirectory"
}

$hash = Get-FileHash -Algorithm SHA256 -LiteralPath $apkFile.FullName
$hashFile = $apkFile.FullName + '.sha256.txt'
"$($hash.Hash)  $($apkFile.Name)" | Set-Content -LiteralPath $hashFile -Encoding ASCII

Write-Host "apk_ready=$($apkFile.FullName)"
Write-Host "apk_sha256=$($hash.Hash)"
Write-Host "apk_sha256_file=$hashFile"
