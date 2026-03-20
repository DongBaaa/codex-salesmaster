[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$ProjectFile,
    [string]$DotNetPath,
    [string]$JavaSdkDirectory,
    [string]$AndroidSdkDirectory,
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
    [ValidateSet('apk', 'aab', 'both')]
    [string]$PackageFormat = 'apk',
    [switch]$DisableTrimming,
    [switch]$SkipEnvironmentCheck,
    [switch]$NoRestore
)

function Get-Utf8String {
    param(
        [Parameter(Mandatory = $true)][string]$Base64
    )

    return [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($Base64))
}

function Resolve-DefaultProjectRoot {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptPath
    )

    return (Resolve-Path (Join-Path (Split-Path -Parent $ScriptPath) '..\..')).Path
}

function Resolve-DeploymentRoot {
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

function Get-ResolvedDotNetPath {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [string]$RequestedPath
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath) -and (Test-Path -LiteralPath $RequestedPath)) {
        return (Resolve-Path -LiteralPath $RequestedPath).Path
    }

    foreach ($candidate in @(
        (Join-Path $env:LOCALAPPDATA 'GeoraePlan.Android\dotnet8\dotnet.exe'),
        (Join-Path $ProjectRoot '.tooling\dotnet8\dotnet.exe')
    )) {
        if (Test-Path -LiteralPath $candidate) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    return $null
}

function Get-ResolvedJavaSdkDirectory {
    param(
        [string]$RequestedPath
    )

    $candidates = [System.Collections.Generic.List[string]]::new()

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $candidates.Add($RequestedPath) | Out-Null
    }

    if (-not [string]::IsNullOrWhiteSpace($env:JAVA_HOME)) {
        $candidates.Add($env:JAVA_HOME) | Out-Null
    }

    foreach ($directCandidate in @(
        (Join-Path $env:ProgramFiles 'Android\Android Studio\jbr'),
        (Join-Path ${env:ProgramFiles(x86)} 'Android\Android Studio\jbr'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Android Studio\jbr')
    )) {
        if (-not [string]::IsNullOrWhiteSpace($directCandidate)) {
            $candidates.Add($directCandidate) | Out-Null
        }
    }

    foreach ($commandName in @('javac', 'java', 'keytool')) {
        $command = Get-Command $commandName -ErrorAction SilentlyContinue
        if ($null -ne $command) {
            $candidates.Add((Split-Path -Parent (Split-Path -Parent $command.Source))) | Out-Null
        }
    }

    foreach ($pattern in @(
        (Join-Path $env:USERPROFILE '.antigravity\extensions\*\jre\*\bin\javac.exe'),
        'C:\Program Files\Microsoft\jdk*\bin\javac.exe',
        'C:\Program Files\Java\*\bin\javac.exe',
        'C:\Deployment Tool\jre8\bin\javac.exe'
    )) {
        $match = Get-ChildItem -Path $pattern -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -ne $match) {
            $candidates.Add((Split-Path -Parent (Split-Path -Parent $match.FullName))) | Out-Null
        }
    }

    foreach ($candidate in $candidates | Select-Object -Unique) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and
            (Test-Path -LiteralPath (Join-Path $candidate 'bin\java.exe')) -and
            (Test-Path -LiteralPath (Join-Path $candidate 'bin\keytool.exe'))) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    return $null
}

function Get-ResolvedAndroidSdkDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [string]$RequestedPath
    )

    foreach ($candidate in @(
        $RequestedPath,
        $env:ANDROID_SDK_ROOT,
        $env:ANDROID_HOME,
        (Join-Path $env:LOCALAPPDATA 'GeoraePlan.Android\android-sdk'),
        (Join-Path $ProjectRoot '.tooling\android-sdk'),
        (Join-Path $env:LOCALAPPDATA 'Android\Sdk')
    )) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path -LiteralPath $candidate)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    return $null
}

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = Resolve-DefaultProjectRoot -ScriptPath $MyInvocation.MyCommand.Path
}

if ([string]::IsNullOrWhiteSpace($ProjectFile)) {
    $ProjectFile = Join-Path $ProjectRoot 'Mobile\GeoraePlan.Mobile.App\GeoraePlan.Mobile.App.csproj'
}

if ([string]::IsNullOrWhiteSpace($VersionName)) {
    $VersionName = Get-CsprojPropertyValue -ProjectFile $ProjectFile -PropertyName 'ApplicationDisplayVersion'
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $ProjectRoot 'Mobile\artifacts\android'
}

$resolvedDotNetPath = Get-ResolvedDotNetPath -ProjectRoot $ProjectRoot -RequestedPath $DotNetPath
$resolvedJavaSdkDirectory = Get-ResolvedJavaSdkDirectory -RequestedPath $JavaSdkDirectory
$resolvedAndroidSdkDirectory = Get-ResolvedAndroidSdkDirectory -ProjectRoot $ProjectRoot -RequestedPath $AndroidSdkDirectory

if (-not $SkipEnvironmentCheck) {
    $envCheckScript = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'Test-GeoraePlanAndroidEnvironment.ps1'
    & $envCheckScript `
        -ProjectRoot $ProjectRoot `
        -ProjectFile $ProjectFile `
        -DotNetPath $resolvedDotNetPath `
        -JavaSdkDirectory $resolvedJavaSdkDirectory `
        -AndroidSdkDirectory $resolvedAndroidSdkDirectory
}

if ([string]::IsNullOrWhiteSpace($resolvedDotNetPath)) {
    throw 'dotnet executable not found.'
}

if (-not (Test-Path -LiteralPath $ProjectFile)) {
    throw "Project file not found: $ProjectFile"
}

if ([string]::IsNullOrWhiteSpace($resolvedJavaSdkDirectory)) {
    throw 'JavaSdkDirectory not found.'
}

if ([string]::IsNullOrWhiteSpace($resolvedAndroidSdkDirectory)) {
    throw 'AndroidSdkDirectory not found.'
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

$env:JAVA_HOME = $resolvedJavaSdkDirectory
$env:ANDROID_SDK_ROOT = $resolvedAndroidSdkDirectory
$env:ANDROID_HOME = $resolvedAndroidSdkDirectory
$env:PATH = (Join-Path $resolvedJavaSdkDirectory 'bin') + ';' + (Join-Path $resolvedAndroidSdkDirectory 'platform-tools') + ';' + (Split-Path -Parent $resolvedDotNetPath) + ';' + $env:PATH

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
$deploymentRoot = Resolve-DeploymentRoot -ProjectRoot $ProjectRoot

$timestamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$artifactPrefix = switch ($PackageFormat) {
    'aab' { 'aab_' }
    'both' { 'bundle_' }
    default { 'publish_' }
}
$publishDirectory = Join-Path $OutputRoot ($artifactPrefix + $timestamp)
New-Item -ItemType Directory -Force -Path $publishDirectory | Out-Null

$arguments = @(
    'publish'
    $ProjectFile
    '-c', $Configuration
    '-f', $Framework
    '--output', $publishDirectory
    '-p:AndroidKeyStore=true'
    "-p:AndroidSigningKeyStore=$KeystorePath"
    "-p:AndroidSigningKeyAlias=$KeyAlias"
    "-p:AndroidSigningStorePass=$StorePass"
    "-p:AndroidSigningKeyPass=$KeyPass"
    "-p:AndroidSdkDirectory=$resolvedAndroidSdkDirectory"
    "-p:JavaSdkDirectory=$resolvedJavaSdkDirectory"
    '-p:ArchiveOnBuild=true'
)

$shouldDisableTrimming = $DisableTrimming.IsPresent -or $Configuration.Equals('Release', [System.StringComparison]::OrdinalIgnoreCase)
if ($shouldDisableTrimming) {
    $arguments += '-p:PublishTrimmed=false'
    Write-Host 'publish_trimmed=false'
}

switch ($PackageFormat) {
    'apk' {
        $arguments += '-p:AndroidPackageFormat=apk'
    }
    'aab' {
        $arguments += '-p:AndroidPackageFormats=aab'
    }
    'both' {
        $arguments += '-p:AndroidPackageFormats=aab;apk'
    }
}

if ($NoRestore) {
    $arguments += '--no-restore'
}

if (-not [string]::IsNullOrWhiteSpace($VersionName)) {
    $arguments += "-p:ApplicationDisplayVersion=$VersionName"
}

if ($VersionCode -gt 0) {
    $arguments += "-p:ApplicationVersion=$VersionCode"
}

& $resolvedDotNetPath @arguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

function Write-PackageHash {
    param(
        [Parameter(Mandatory = $true)][System.IO.FileInfo]$File
    )

    $hash = Get-FileHash -Algorithm SHA256 -LiteralPath $File.FullName
    $hashFile = $File.FullName + '.sha256.txt'
    "$($hash.Hash)  $($File.Name)" | Set-Content -LiteralPath $hashFile -Encoding ASCII
    return @{
        Hash = $hash.Hash
        HashFile = $hashFile
    }
}

$apkFile = $null
$aabFile = $null

if ($PackageFormat -in @('apk', 'both')) {
    $apkFile = Get-ChildItem -LiteralPath $publishDirectory -Recurse -File -Filter '*.apk' |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($null -eq $apkFile) {
        throw "No APK file was produced under $publishDirectory"
    }

    $apkHash = Write-PackageHash -File $apkFile
    $stableApkVersion = if ([string]::IsNullOrWhiteSpace($VersionName)) { 'latest' } else { $VersionName }
    $stableApkPrefix = Get-Utf8String '6rGw656Y7ZSM656cLeyViOuTnOuhnOydtOuTnC12'
    $stableApkName = "$stableApkPrefix$stableApkVersion-signed.apk"
    $stableApkFilter = Get-Utf8String '6rGw656Y7ZSM656cLeyViOuTnOuhnOydtOuTnC12Ki1zaWduZWQuYXBrKg=='
    Get-ChildItem -LiteralPath $deploymentRoot -File -Filter $stableApkFilter -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue
    $stableApkPath = Join-Path $deploymentRoot $stableApkName
    Copy-Item -LiteralPath $apkFile.FullName -Destination $stableApkPath -Force
    $stableApkHash = Write-PackageHash -File (Get-Item -LiteralPath $stableApkPath)
    Write-Host "apk_ready=$($apkFile.FullName)"
    Write-Host "apk_sha256=$($apkHash.Hash)"
    Write-Host "apk_sha256_file=$($apkHash.HashFile)"
    Write-Host "apk_deployment_copy=$stableApkPath"
    Write-Host "apk_deployment_sha256=$($stableApkHash.Hash)"
    Write-Host "apk_deployment_sha256_file=$($stableApkHash.HashFile)"
}

if ($PackageFormat -in @('aab', 'both')) {
    $aabFile = Get-ChildItem -LiteralPath $publishDirectory -Recurse -File -Filter '*.aab' |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($null -eq $aabFile) {
        throw "No AAB file was produced under $publishDirectory"
    }

    $aabHash = Write-PackageHash -File $aabFile
    Write-Host "aab_ready=$($aabFile.FullName)"
    Write-Host "aab_sha256=$($aabHash.Hash)"
    Write-Host "aab_sha256_file=$($aabHash.HashFile)"
}

Write-Host "dotnet_path=$resolvedDotNetPath"
Write-Host "java_sdk_directory=$resolvedJavaSdkDirectory"
Write-Host "android_sdk_directory=$resolvedAndroidSdkDirectory"

