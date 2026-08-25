[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$SigningConfigPath,
    [string]$WindowsSigningConfigPath,
    [string]$Channel = 'stable',
    [switch]$DeployToLinuxPc,
    [switch]$NoRestore,
    [switch]$DisableAndroidAot,
    [switch]$DisableAndroidTrimming,
    [switch]$AllowLegacyAndroidDebugSigning,
    [switch]$SkipAndroidSigningContinuityCheck,
    [switch]$AcceptAndroidSigningCertificateChange,
    [switch]$RequireWindowsAuthenticode,
    [string]$LocalCacheAppDataRoot = '',
    [string]$LocalCacheEvidenceDirectory = '',
    [switch]$RequireLocalCacheConsistencyCheck,
    [switch]$FailOnLocalCacheWarning,
    [string]$DesktopMinimumSupportedVersion,
    [string]$AndroidMinimumSupportedVersion,
    [switch]$MandatoryDesktop,
    [switch]$MandatoryAndroid,
    [switch]$AllowLegacyLiveMirror,
    [switch]$AllowScheduledApplyTrigger,
    [string]$LinuxSshHost = '192.168.0.199',
    [string]$LinuxSshUser = 'itw',
    [int]$LinuxSshPort = 2222,
    [string]$LinuxSshKeyPath = (Join-Path $env:USERPROFILE '.ssh\itwserver_codex_ed25519'),
    [string]$LinuxRemoteOpsPath = '/srv/georaeplan/ops',
    [switch]$SkipPreDeployOperationalGate,
    [switch]$SkipPostDeployOperationalGate,
    [switch]$FailOnOperationalWarnings,
    [switch]$AcceptRentalTemplateItemReferenceRisk,
    [string]$PreDeployBaseUrl = "",
    [string]$PreDeploySecretPath = "",
    [string]$PreDeployOutputDirectory = "",
    [string[]]$PreDeployAllowedIntegrityWarningCodes = @(),
    [string]$PostDeployBaseUrl = "",
    [string]$PostDeploySecretPath = "",
    [string]$PostDeployOutputDirectory = "",
    [string[]]$PostDeployAllowedIntegrityWarningCodes = @()
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

function Resolve-AndroidSigningPath {
    param(
        [Parameter(Mandatory = $true)][string]$PathValue,
        [Parameter(Mandatory = $true)][string]$BaseDirectory
    )

    if ([string]::IsNullOrWhiteSpace($PathValue)) {
        return ''
    }

    if ([System.IO.Path]::IsPathRooted($PathValue)) {
        return $PathValue
    }

    return [System.IO.Path]::GetFullPath((Join-Path $BaseDirectory $PathValue))
}

function Assert-AndroidReleaseSigningReady {
    param(
        [Parameter(Mandatory = $true)][string]$SigningConfigPath,
        [switch]$AllowLegacyAndroidDebugSigning
    )

    if ([string]::IsNullOrWhiteSpace($SigningConfigPath)) {
        throw 'Android signing config path is required before release build.'
    }

    if (-not (Test-Path -LiteralPath $SigningConfigPath)) {
        throw "Android signing config not found before release build: $SigningConfigPath"
    }

    $resolvedSigningConfigPath = (Resolve-Path -LiteralPath $SigningConfigPath).Path
    try {
        $signingConfig = Get-Content -LiteralPath $resolvedSigningConfigPath -Raw | ConvertFrom-Json
    }
    catch {
        throw "Android signing config could not be parsed before release build: $resolvedSigningConfigPath"
    }

    $signingConfigDirectory = Split-Path -Parent $resolvedSigningConfigPath
    $keystorePath = [string]$signingConfig.keystorePath
    $keyAlias = [string]$signingConfig.keyAlias
    $hasInlineStorePass = -not [string]::IsNullOrWhiteSpace([string]$signingConfig.storePass)
    $hasInlineKeyPass = -not [string]::IsNullOrWhiteSpace([string]$signingConfig.keyPass)
    $storePassEnvironmentVariable = [string]$signingConfig.storePassEnvironmentVariable
    $keyPassEnvironmentVariable = [string]$signingConfig.keyPassEnvironmentVariable

    if ([string]::IsNullOrWhiteSpace($keystorePath)) {
        throw 'Android signing config is missing keystorePath before release build.'
    }

    if ([string]::IsNullOrWhiteSpace($keyAlias)) {
        throw 'Android signing config is missing keyAlias before release build.'
    }

    $resolvedKeystorePath = Resolve-AndroidSigningPath -PathValue $keystorePath -BaseDirectory $signingConfigDirectory
    if (-not (Test-Path -LiteralPath $resolvedKeystorePath)) {
        throw "Android keystore not found before release build: $resolvedKeystorePath"
    }

    if ($AllowLegacyAndroidDebugSigning) {
        if ((-not $hasInlineStorePass -and [string]::IsNullOrWhiteSpace($storePassEnvironmentVariable)) -or
            (-not $hasInlineKeyPass -and [string]::IsNullOrWhiteSpace($keyPassEnvironmentVariable))) {
            throw 'Legacy Android signing config is missing a password source before release build.'
        }
        return
    }

    if ($hasInlineStorePass -or $hasInlineKeyPass) {
        throw 'Production inline Android signing passwords are forbidden; use storePassEnvironmentVariable/keyPassEnvironmentVariable.'
    }

    foreach ($secretEnvironmentVariable in @(
        [pscustomobject]@{ Name = $storePassEnvironmentVariable; Label = 'Android store password' },
        [pscustomobject]@{ Name = $keyPassEnvironmentVariable; Label = 'Android key password' }
    )) {
        if ([string]::IsNullOrWhiteSpace($secretEnvironmentVariable.Name) -or $secretEnvironmentVariable.Name -cnotmatch '^[A-Za-z_][A-Za-z0-9_]{0,127}$') {
            throw "$($secretEnvironmentVariable.Label) environment variable reference is invalid before release build."
        }
        $secretValue = [Environment]::GetEnvironmentVariable($secretEnvironmentVariable.Name, 'Process')
        if ([string]::IsNullOrWhiteSpace($secretValue)) {
            throw "$($secretEnvironmentVariable.Label) environment variable is not available before release build."
        }
        $secretValue = $null
    }

    $isDebugKeystorePath = [System.IO.Path]::GetFileName($resolvedKeystorePath).Equals('debug.keystore', [System.StringComparison]::OrdinalIgnoreCase)
    $isDebugKeyAlias = $keyAlias.Equals('androiddebugkey', [System.StringComparison]::OrdinalIgnoreCase)
    if ($isDebugKeystorePath -or $isDebugKeyAlias) {
        throw "Release Android package is using a debug signing key before release build. Configure Mobile\GeoraePlan.Mobile.App\android-signing.local.json with a release keystore, or pass -AllowLegacyAndroidDebugSigning only for the existing legacy debug-signed update chain."
    }
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

function Resolve-AndroidDeploymentPackage {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$AndroidVersion
    )

    $deploymentRoot = Join-Path $ProjectRoot '배포'
    $exactPath = Join-Path $deploymentRoot "거래플랜-안드로이드-v$AndroidVersion-signed.apk"
    if (Test-Path -LiteralPath $exactPath) {
        return (Resolve-Path -LiteralPath $exactPath).Path
    }

    $candidate = Get-ChildItem -LiteralPath $deploymentRoot -File -Filter '거래플랜-안드로이드-v*-signed.apk' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($null -eq $candidate) {
        throw "Android deployment APK not found after build under $deploymentRoot"
    }

    return $candidate.FullName
}

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = Resolve-ProjectRoot -ScriptPath $MyInvocation.MyCommand.Path
}
$ProjectRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path

$tempInitializer = Join-Path $ProjectRoot 'tools\common\Initialize-GeoraePlanTemp.ps1'
if (Test-Path -LiteralPath $tempInitializer) {
    . $tempInitializer -ProjectRoot $ProjectRoot
}

if ([string]::IsNullOrWhiteSpace($SigningConfigPath)) {
    $releaseSigningConfigPath = Join-Path $ProjectRoot 'Mobile\GeoraePlan.Mobile.App\android-signing.release.local.json'
    $legacySigningConfigPath = Join-Path $ProjectRoot 'Mobile\GeoraePlan.Mobile.App\android-signing.local.json'
    if (Test-Path -LiteralPath $releaseSigningConfigPath) {
        $SigningConfigPath = $releaseSigningConfigPath
    }
    elseif ($AllowLegacyAndroidDebugSigning -and (Test-Path -LiteralPath $legacySigningConfigPath)) {
        $SigningConfigPath = $legacySigningConfigPath
    }
    else {
        throw '유료 납품용 Android release signing 설정이 없습니다. android-signing.release.local.json을 준비하거나 기존 debug 서명 연속성을 유지해야 하는 경우에만 -AllowLegacyAndroidDebugSigning을 명시하세요.'
    }
}

$dotnetExe = Resolve-DotnetCommand -ProjectRoot $ProjectRoot
$env:DOTNET_EXE = $dotnetExe

$desktopProject = Resolve-ProjectFile -RootPath (Join-Path $ProjectRoot 'Desktop') -Pattern '*.Desktop.App.csproj'
$androidProject = Resolve-ProjectFile -RootPath (Join-Path $ProjectRoot 'Mobile') -Pattern 'GeoraePlan.Mobile.App.csproj'
$desktopVersion = Get-CsprojPropertyValue -ProjectFile $desktopProject -PropertyName 'Version'
$androidVersion = Get-CsprojPropertyValue -ProjectFile $androidProject -PropertyName 'ApplicationDisplayVersion'

Write-Host "release_desktop_version=$desktopVersion"
Write-Host "release_android_version=$androidVersion"
if ($AllowLegacyAndroidDebugSigning) {
    Write-Warning "Legacy Android debug signing is explicitly allowed for this full release. Use only to preserve the existing debug-signed update chain; prefer a release keystore for new paid deliveries."
}
Assert-AndroidReleaseSigningReady -SigningConfigPath $SigningConfigPath -AllowLegacyAndroidDebugSigning:$AllowLegacyAndroidDebugSigning

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
$desktopArgs = @(
    '-NoProfile',
    '-ExecutionPolicy', 'Bypass',
    '-File', $desktopScript,
    '-ProjectRoot', $ProjectRoot
)
if (-not [string]::IsNullOrWhiteSpace($WindowsSigningConfigPath)) {
    $desktopArgs += @('-WindowsSigningConfigPath', $WindowsSigningConfigPath)
}
$desktopArgs += '-RequireWindowsAuthenticode'
& powershell @desktopArgs
if ($LASTEXITCODE -ne 0) {
    throw 'desktop installer build failed.'
}

$windowsSigningCheckScript = Join-Path $ProjectRoot 'tools\release\Test-GeoraePlanWindowsSigning.ps1'
$windowsSigningCheckArgs = @(
    '-NoProfile',
    '-ExecutionPolicy', 'Bypass',
    '-File', $windowsSigningCheckScript,
    '-ProjectRoot', $ProjectRoot
)
$windowsSigningCheckArgs += '-RequireSigned'
$windowsSigningCheckArgs += '-RequireTimestamp'
& powershell @windowsSigningCheckArgs
if ($LASTEXITCODE -ne 0) {
    throw 'Windows Authenticode verification failed.'
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
if ($DisableAndroidAot) {
    $androidArgs += '-DisableAot'
}
if ($DisableAndroidTrimming) {
    $androidArgs += '-DisableTrimming'
}
if ($AllowLegacyAndroidDebugSigning) {
    $androidArgs += '-AllowDebugSigning'
}
& powershell @androidArgs
if ($LASTEXITCODE -ne 0) {
    throw 'android apk build failed.'
}

if ($DeployToLinuxPc -and -not $SkipAndroidSigningContinuityCheck) {
    $androidSigningContinuityScript = Join-Path $ProjectRoot 'tools\mobile\Test-GeoraePlanAndroidSigningContinuity.ps1'
    if (-not (Test-Path -LiteralPath $androidSigningContinuityScript)) {
        throw "Android signing continuity script not found: $androidSigningContinuityScript"
    }

    $localAndroidApk = Resolve-AndroidDeploymentPackage -ProjectRoot $ProjectRoot -AndroidVersion $androidVersion
    $continuityBaseUrl = if (-not [string]::IsNullOrWhiteSpace($PreDeployBaseUrl)) { $PreDeployBaseUrl } else { 'https://trade.2884.kr' }
    $continuityArgs = @(
        '-NoProfile'
        '-ExecutionPolicy', 'Bypass'
        '-File', $androidSigningContinuityScript
        '-ProjectRoot', $ProjectRoot
        '-LocalApkPath', $localAndroidApk
        '-BaseUrl', $continuityBaseUrl
        '-Channel', $Channel
    )
    if ($AcceptAndroidSigningCertificateChange) {
        $continuityArgs += '-AcceptCertificateChange'
    }

    & powershell @continuityArgs
    if ($LASTEXITCODE -ne 0) {
        throw 'Android signing certificate continuity check failed.'
    }
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

if ($DeployToLinuxPc) {
    $linuxScript = Join-Path $ProjectRoot 'tools\linux\Publish-GeoraeplanLinuxPcRelease.ps1'
    $linuxArgs = @(
        '-NoProfile'
        '-ExecutionPolicy', 'Bypass'
        '-File', $linuxScript
        '-ProjectRoot', $ProjectRoot
        '-MirrorToLive'
        '-LinuxSshHost', $LinuxSshHost
        '-LinuxSshUser', $LinuxSshUser
        '-LinuxSshPort', $LinuxSshPort.ToString()
        '-LinuxRemoteOpsPath', $LinuxRemoteOpsPath
    )

    if (-not [string]::IsNullOrWhiteSpace($LinuxSshKeyPath)) {
        $linuxArgs += @('-LinuxSshKeyPath', $LinuxSshKeyPath)
    }
    if ($SkipPreDeployOperationalGate) {
        $linuxArgs += '-SkipPreDeployOperationalGate'
    }
    if ($SkipPostDeployOperationalGate) {
        $linuxArgs += '-SkipPostDeployOperationalGate'
    }
    if ($FailOnOperationalWarnings) {
        $linuxArgs += '-FailOnOperationalWarnings'
    }
    if ($AllowLegacyAndroidDebugSigning) {
        $linuxArgs += '-AcceptLegacyAndroidDebugSigningWarning'
    }
    if ($AcceptRentalTemplateItemReferenceRisk) {
        $linuxArgs += '-AcceptRentalTemplateItemReferenceRisk'
    }
    if ($SkipAndroidSigningContinuityCheck) {
        $linuxArgs += '-SkipAndroidSigningContinuityCheck'
    }
    if ($AcceptAndroidSigningCertificateChange) {
        $linuxArgs += '-AcceptAndroidSigningCertificateChange'
    }
    if (-not [string]::IsNullOrWhiteSpace($LocalCacheAppDataRoot)) {
        $linuxArgs += @('-LocalCacheAppDataRoot', $LocalCacheAppDataRoot)
    }
    if (-not [string]::IsNullOrWhiteSpace($LocalCacheEvidenceDirectory)) {
        $linuxArgs += @('-LocalCacheEvidenceDirectory', $LocalCacheEvidenceDirectory)
    }
    if ($RequireLocalCacheConsistencyCheck) {
        $linuxArgs += '-RequireLocalCacheConsistencyCheck'
    }
    if ($FailOnLocalCacheWarning) {
        $linuxArgs += '-FailOnLocalCacheWarning'
    }
    if (-not [string]::IsNullOrWhiteSpace($PreDeployBaseUrl)) {
        $linuxArgs += @('-PreDeployBaseUrl', $PreDeployBaseUrl)
    }
    if (-not [string]::IsNullOrWhiteSpace($PreDeploySecretPath)) {
        $linuxArgs += @('-PreDeploySecretPath', $PreDeploySecretPath)
    }
    if (-not [string]::IsNullOrWhiteSpace($PreDeployOutputDirectory)) {
        $linuxArgs += @('-PreDeployOutputDirectory', $PreDeployOutputDirectory)
    }
    if ($PreDeployAllowedIntegrityWarningCodes.Count -gt 0) {
        $linuxArgs += '-PreDeployAllowedIntegrityWarningCodes'
        $linuxArgs += $PreDeployAllowedIntegrityWarningCodes
    }
    if (-not [string]::IsNullOrWhiteSpace($PostDeployBaseUrl)) {
        $linuxArgs += @('-PostDeployBaseUrl', $PostDeployBaseUrl)
    }
    if (-not [string]::IsNullOrWhiteSpace($PostDeploySecretPath)) {
        $linuxArgs += @('-PostDeploySecretPath', $PostDeploySecretPath)
    }
    if (-not [string]::IsNullOrWhiteSpace($PostDeployOutputDirectory)) {
        $linuxArgs += @('-PostDeployOutputDirectory', $PostDeployOutputDirectory)
    }
    if ($PostDeployAllowedIntegrityWarningCodes.Count -gt 0) {
        $linuxArgs += '-PostDeployAllowedIntegrityWarningCodes'
        $linuxArgs += $PostDeployAllowedIntegrityWarningCodes
    }

    & powershell @linuxArgs
    if ($LASTEXITCODE -ne 0) {
        throw 'Linux PC deployment failed.'
    }
}

$desktopInstaller = Join-Path $ProjectRoot '배포\거래플랜-PC-설치패키지.exe'
$androidApk = Join-Path $ProjectRoot "배포\거래플랜-안드로이드-v$androidVersion-signed.apk"
$manifestPath = Join-Path $ProjectRoot "배포\업데이트\manifest\$Channel.json"

Write-Host "release_pc_installer=$desktopInstaller"
Write-Host "release_android_apk=$androidApk"
Write-Host "release_update_manifest=$manifestPath"
