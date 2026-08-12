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
    [int]$KeepArtifactDirectoryCount = 2,
    [ValidateSet('apk', 'aab', 'both')]
    [string]$PackageFormat = 'apk',
    [switch]$LocalTest,
    [switch]$DisableAot,
    [switch]$DisableTrimming,
    [switch]$AllowDebugSigning,
    [switch]$SkipEnvironmentCheck,
    [switch]$SkipArtifactPrune,
    [switch]$SkipDeploymentCopy,
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

function Test-PathContainsNonAscii {
    param(
        [Parameter(Mandatory = $true)][string]$PathValue
    )

    foreach ($character in $PathValue.ToCharArray()) {
        if ([int][char]$character -gt 127) {
            return $true
        }
    }

    return $false
}

function Test-AndroidAotStagingSecretFileName {
    param(
        [Parameter(Mandatory = $true)][string]$FileName
    )

    if ($FileName -in @(
        'android-signing.local.json',
        'android-signing.release.local.json'
    )) {
        return $true
    }

    return [System.IO.Path]::GetExtension($FileName) -in @(
        '.keystore',
        '.jks',
        '.p12',
        '.pfx',
        '.pem',
        '.key',
        '.snk'
    )
}

function Copy-AndroidAotStagingTree {
    param(
        [Parameter(Mandatory = $true)][string]$SourceRoot,
        [Parameter(Mandatory = $true)][string]$DestinationRoot
    )

    New-Item -ItemType Directory -Force -Path $DestinationRoot | Out-Null

    foreach ($item in Get-ChildItem -LiteralPath $SourceRoot -Force -ErrorAction Stop) {
        if ($item.PSIsContainer) {
            if ($item.Name -in @('bin', 'obj', 'signing', 'artifacts')) {
                continue
            }

            Copy-AndroidAotStagingTree `
                -SourceRoot $item.FullName `
                -DestinationRoot (Join-Path $DestinationRoot $item.Name)
            continue
        }

        if (Test-AndroidAotStagingSecretFileName -FileName $item.Name) {
            continue
        }

        Copy-Item `
            -LiteralPath $item.FullName `
            -Destination (Join-Path $DestinationRoot $item.Name) `
            -Force
    }
}

function Remove-AndroidAotStagingDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$StagingRoot,
        [Parameter(Mandatory = $true)][string]$StagingBaseRoot
    )

    if (-not (Test-Path -LiteralPath $StagingRoot)) {
        return
    }

    $resolvedBaseRoot = [System.IO.Path]::GetFullPath($StagingBaseRoot).TrimEnd('\') + '\'
    $resolvedStagingRoot = (Resolve-Path -LiteralPath $StagingRoot).Path
    $stagingRootWithSeparator = $resolvedStagingRoot.TrimEnd('\') + '\'
    if (-not $stagingRootWithSeparator.StartsWith($resolvedBaseRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Android AOT staging cleanup target is outside staging base root: $resolvedStagingRoot"
    }

    $lastError = $null
    for ($attempt = 1; $attempt -le 8; $attempt++) {
        try {
            Get-ChildItem -LiteralPath $resolvedStagingRoot -Recurse -Force -File -ErrorAction SilentlyContinue |
                ForEach-Object { $_.IsReadOnly = $false }
            Remove-Item -LiteralPath $resolvedStagingRoot -Recurse -Force -ErrorAction Stop
            return
        }
        catch {
            $lastError = $_.Exception
            if ($attempt -lt 8) {
                [GC]::Collect()
                [GC]::WaitForPendingFinalizers()
                Start-Sleep -Milliseconds (250 * $attempt)
            }
        }
    }

    throw "Android AOT staging cleanup failed after retries: $($lastError.Message)"
}

function New-AndroidAotStagingContext {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$ProjectFile,
        [Parameter(Mandatory = $true)][bool]$ShouldEnableAot,
        [Parameter(Mandatory = $true)][bool]$NoRestoreRequested
    )

    $resolvedProjectRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path
    $resolvedProjectFile = (Resolve-Path -LiteralPath $ProjectFile).Path
    $defaultContext = [pscustomobject]@{
        Enabled = $false
        ProjectFile = $resolvedProjectFile
        WorkingDirectory = $resolvedProjectRoot
        TemporaryDirectory = $null
        StagingRoot = $null
        StagingBaseRoot = $null
    }

    if (-not $ShouldEnableAot) {
        return $defaultContext
    }

    if (-not (Test-PathContainsNonAscii -PathValue $resolvedProjectRoot)) {
        return $defaultContext
    }

    Write-Host 'android_aot_staging_reason=non_ascii_project_root'

    if ($NoRestoreRequested) {
        Write-Warning 'Android Release AOT staging was skipped because --no-restore cannot be combined with a filtered staging copy.'
        Write-Host 'android_aot_staging=skipped_no_restore'
        return $defaultContext
    }

    $projectRootWithSeparator = $resolvedProjectRoot.TrimEnd('\') + '\'
    if (-not $resolvedProjectFile.StartsWith($projectRootWithSeparator, [System.StringComparison]::OrdinalIgnoreCase)) {
        Write-Warning "Android Release AOT staging was skipped because the project file is outside the declared project root: $resolvedProjectFile"
        Write-Host 'android_aot_staging=skipped_project_outside_root'
        return $defaultContext
    }
    $relativeProjectFile = $resolvedProjectFile.Substring($projectRootWithSeparator.Length).Replace('/', '\')

    $stagingBaseRoot = 'D:\gpaot'
    $stagingRoot = $null
    try {
        $stagingDriveRoot = [System.IO.Path]::GetPathRoot($stagingBaseRoot)
        if ([string]::IsNullOrWhiteSpace($stagingDriveRoot) -or -not (Test-Path -LiteralPath $stagingDriveRoot)) {
            throw "Android AOT staging drive root not found: $stagingDriveRoot"
        }

        New-Item -ItemType Directory -Force -Path $stagingBaseRoot | Out-Null
        $stagingLeaf = 's' + (Get-Date -Format 'yyyyMMddHHmmss') + '_' + $PID + '_' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
        $stagingRoot = Join-Path $stagingBaseRoot $stagingLeaf
        New-Item -ItemType Directory -Force -Path $stagingRoot | Out-Null
        $stagingTempDirectory = Join-Path $stagingRoot 'tmp'
        New-Item -ItemType Directory -Force -Path $stagingTempDirectory | Out-Null

        foreach ($topLevelDirectoryName in @('Mobile', 'Shared', 'AppIcons')) {
            $sourcePath = Join-Path $resolvedProjectRoot $topLevelDirectoryName
            if (-not (Test-Path -LiteralPath $sourcePath)) {
                throw "Required Android AOT staging source directory not found: $sourcePath"
            }

            Copy-AndroidAotStagingTree `
                -SourceRoot $sourcePath `
                -DestinationRoot (Join-Path $stagingRoot $topLevelDirectoryName)
        }

        $stagedProjectFile = Join-Path $stagingRoot $relativeProjectFile
        if (-not (Test-Path -LiteralPath $stagedProjectFile)) {
            throw "Staged project file not found: $stagedProjectFile"
        }

        Write-Host 'android_aot_staging=enabled'
        Write-Host "android_aot_staging_root=$stagingRoot"

        return [pscustomobject]@{
            Enabled = $true
            ProjectFile = (Resolve-Path -LiteralPath $stagedProjectFile).Path
            WorkingDirectory = $stagingRoot
            TemporaryDirectory = $stagingTempDirectory
            StagingRoot = $stagingRoot
            StagingBaseRoot = $stagingBaseRoot
        }
    }
    catch {
        $message = $_.Exception.Message
        Write-Warning "Android Release AOT staging prepare failed: $message"
        Write-Host 'android_aot_staging=failed_prepare'
        Write-Host "android_aot_staging_error=$message"

        if (-not [string]::IsNullOrWhiteSpace($stagingRoot) -and (Test-Path -LiteralPath $stagingRoot)) {
            try {
                Remove-AndroidAotStagingDirectory -StagingRoot $stagingRoot -StagingBaseRoot $stagingBaseRoot
            }
            catch {
                Write-Warning "Android Release AOT staging prepare cleanup failed: $($_.Exception.Message)"
                Write-Host 'android_aot_staging_cleanup=failed'
                Write-Host "android_aot_staging_cleanup_root=$stagingRoot"
            }
        }

        return $defaultContext
    }
}

function Remove-AndroidAotStagingContext {
    param(
        [Parameter(Mandatory = $true)]$Context
    )

    if ($null -eq $Context -or -not $Context.Enabled -or [string]::IsNullOrWhiteSpace([string]$Context.StagingRoot)) {
        return
    }

    try {
        Remove-AndroidAotStagingDirectory `
            -StagingRoot ([string]$Context.StagingRoot) `
            -StagingBaseRoot ([string]$Context.StagingBaseRoot)
        Write-Host 'android_aot_staging_cleanup=success'
    }
    catch {
        Write-Warning "Android Release AOT staging cleanup failed: $($_.Exception.Message)"
        Write-Host 'android_aot_staging_cleanup=failed'
        Write-Host "android_aot_staging_cleanup_root=$($Context.StagingRoot)"
        throw
    }
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
        (Join-Path $ProjectRoot '.tooling\dotnet8\dotnet.exe'),
        (Join-Path $ProjectRoot '.dotnet\dotnet.exe'),
        (Join-Path $env:LOCALAPPDATA 'GeoraePlan.Android\dotnet8\dotnet.exe')
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

$tempInitializer = Join-Path $ProjectRoot 'tools\common\Initialize-GeoraePlanTemp.ps1'
if (Test-Path -LiteralPath $tempInitializer) {
    . $tempInitializer -ProjectRoot $ProjectRoot
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

function Assert-AndroidSigningSecretRootSafe {
    param([Parameter(Mandatory = $true)][string]$Path)

    $current = Get-Item -LiteralPath $Path -Force
    while ($null -ne $current) {
        if ($current.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Android signing secret root contains a reparse point: $($current.FullName)"
        }
        $current = $current.Parent
    }
}

function New-AndroidSigningSecretFile {
    param(
        [Parameter(Mandatory = $true)][string]$Value,
        [Parameter(Mandatory = $true)][string]$Label
    )

    $secretRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    Assert-AndroidSigningSecretRootSafe -Path $secretRoot
    $secretPath = Join-Path `
        $secretRoot `
        ("georaeplan-android-signing-{0}-{1}.secret" -f
            $Label,
            [Guid]::NewGuid().ToString('N'))

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $security = New-Object Security.AccessControl.FileSecurity
    $security.SetOwner($identity.User)
    $security.SetAccessRuleProtection($true, $false)
    foreach ($sid in @(
        $identity.User,
        (New-Object Security.Principal.SecurityIdentifier('S-1-5-18')),
        (New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544'))
    )) {
        $rule = New-Object Security.AccessControl.FileSystemAccessRule(
            $sid,
            [Security.AccessControl.FileSystemRights]::FullControl,
            [Security.AccessControl.AccessControlType]::Allow)
        [void]$security.AddAccessRule($rule)
    }

    $stream = $null
    $bytes = [Text.Encoding]::UTF8.GetBytes($Value)
    try {
        $stream = [IO.FileStream]::new(
            $secretPath,
            [IO.FileMode]::CreateNew,
            [Security.AccessControl.FileSystemRights]::FullControl,
            [IO.FileShare]::Read,
            4096,
            [IO.FileOptions]::WriteThrough,
            $security)
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
        return [pscustomobject]@{
            Path = $secretPath
            Stream = $stream
        }
    }
    catch {
        if ($null -ne $stream) {
            $stream.Dispose()
        }
        if (Test-Path -LiteralPath $secretPath -PathType Leaf) {
            [IO.File]::Delete($secretPath)
        }
        throw
    }
    finally {
        [Array]::Clear($bytes, 0, $bytes.Length)
    }
}

function New-AndroidSigningSecretPair {
    param(
        [Parameter(Mandatory = $true)][string]$StoreValue,
        [Parameter(Mandatory = $true)][string]$KeyValue
    )

    $store = $null
    $key = $null
    try {
        $store = New-AndroidSigningSecretFile -Value $StoreValue -Label 'store'
        $key = New-AndroidSigningSecretFile -Value $KeyValue -Label 'key'
        return [pscustomobject]@{ Store = $store; Key = $key }
    }
    catch {
        foreach ($entry in @($key, $store)) {
            if ($null -ne $entry) {
                $entry.Stream.Dispose()
                if (Test-Path -LiteralPath $entry.Path -PathType Leaf) {
                    [IO.File]::Delete($entry.Path)
                }
            }
        }
        throw
    }
}

function Remove-AndroidSigningSecretPair {
    param([AllowNull()][object]$Pair)

    if ($null -eq $Pair) {
        return
    }
    foreach ($entry in @($Pair.Key, $Pair.Store)) {
        if ($null -eq $entry) {
            continue
        }
        $entry.Stream.Dispose()
        if (Test-Path -LiteralPath $entry.Path -PathType Leaf) {
            [IO.File]::Delete($entry.Path)
        }
        if (Test-Path -LiteralPath $entry.Path) {
            throw "Android signing secret cleanup failed: $($entry.Path)"
        }
    }
}

$isReleaseBuild = $Configuration.Equals('Release', [System.StringComparison]::OrdinalIgnoreCase)
$isDebugKeystorePath = [System.IO.Path]::GetFileName($KeystorePath).Equals('debug.keystore', [System.StringComparison]::OrdinalIgnoreCase)
$isDebugKeyAlias = $KeyAlias.Equals('androiddebugkey', [System.StringComparison]::OrdinalIgnoreCase)
if ($isReleaseBuild -and -not $LocalTest.IsPresent -and -not $AllowDebugSigning.IsPresent -and ($isDebugKeystorePath -or $isDebugKeyAlias)) {
    throw "Release Android package is using a debug signing key. Configure Mobile\GeoraePlan.Mobile.App\android-signing.local.json with a release keystore, or pass -AllowDebugSigning only for local, non-delivery test packages."
}

$env:JAVA_HOME = $resolvedJavaSdkDirectory
$env:ANDROID_SDK_ROOT = $resolvedAndroidSdkDirectory
$env:ANDROID_HOME = $resolvedAndroidSdkDirectory
$env:PATH = (Join-Path $resolvedJavaSdkDirectory 'bin') + ';' + (Join-Path $resolvedAndroidSdkDirectory 'platform-tools') + ';' + (Split-Path -Parent $resolvedDotNetPath) + ';' + $env:PATH

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
$deploymentRoot = if ($SkipDeploymentCopy.IsPresent) { $null } else { Resolve-DeploymentRoot -ProjectRoot $ProjectRoot }

$timestamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$artifactPrefix = switch ($PackageFormat) {
    'aab' { 'aab_' }
    'both' { 'bundle_' }
    default { 'publish_' }
}
$publishDirectory = Join-Path $OutputRoot ($artifactPrefix + $timestamp)
New-Item -ItemType Directory -Force -Path $publishDirectory | Out-Null

$shouldEnableAot = $isReleaseBuild -and -not $DisableAot.IsPresent
$stagingContext = New-AndroidAotStagingContext `
    -ProjectRoot $ProjectRoot `
    -ProjectFile $ProjectFile `
    -ShouldEnableAot ([bool]$shouldEnableAot) `
    -NoRestoreRequested ([bool]$NoRestore.IsPresent)

$arguments = @(
    'publish'
    $stagingContext.ProjectFile
    '-c', $Configuration
    '-f', $Framework
    '--output', $publishDirectory
    '-p:AndroidKeyStore=true'
    "-p:AndroidSigningKeyStore=$KeystorePath"
    "-p:AndroidSigningKeyAlias=$KeyAlias"
    '-p:AndroidSigningStorePass=__GEORAEPLAN_STORE_SECRET_FILE__'
    '-p:AndroidSigningKeyPass=__GEORAEPLAN_KEY_SECRET_FILE__'
    "-p:AndroidSdkDirectory=$resolvedAndroidSdkDirectory"
    "-p:JavaSdkDirectory=$resolvedJavaSdkDirectory"
    '-p:ArchiveOnBuild=true'
)

if ($shouldEnableAot) {
    $arguments += '-p:RunAOTCompilation=true'
    $arguments += '-p:AndroidEnableProfiledAot=true'
    Write-Host 'android_profiled_aot=true'
}
elseif ($DisableAot.IsPresent) {
    $arguments += '-p:RunAOTCompilation=false'
    $arguments += '-p:AndroidEnableProfiledAot=false'
    Write-Host 'android_profiled_aot=false'
}

$shouldDisableTrimming = $DisableTrimming.IsPresent
if ($shouldDisableTrimming) {
    $arguments += '-p:PublishTrimmed=false'
    Write-Host 'publish_trimmed=false'
}

if ($LocalTest.IsPresent) {
    $arguments += '-p:GeoraePlanMobileLocalTest=true'
    Write-Host 'mobile_local_test=true'
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

if ($stagingContext.Enabled) {
    $arguments += '-p:UseSharedCompilation=false'
    $arguments += '-nodeReuse:false'
    Write-Host 'android_aot_staging_compiler_reuse=false'
}

if (-not [string]::IsNullOrWhiteSpace($VersionName)) {
    $arguments += "-p:ApplicationDisplayVersion=$VersionName"
}

if ($VersionCode -gt 0) {
    $arguments += "-p:ApplicationVersion=$VersionCode"
}

function Invoke-DotnetPublishAndRelay {
    param(
        [Parameter(Mandatory = $true)][string]$DotNetPath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [string]$WorkingDirectory,
        [string]$TemporaryDirectory
    )

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $pushedLocation = $false
    $previousTemp = $env:TEMP
    $previousTmp = $env:TMP
    try {
        if (-not [string]::IsNullOrWhiteSpace($WorkingDirectory)) {
            Push-Location -LiteralPath $WorkingDirectory
            $pushedLocation = $true
        }
        if (-not [string]::IsNullOrWhiteSpace($TemporaryDirectory)) {
            New-Item -ItemType Directory -Force -Path $TemporaryDirectory | Out-Null
            $env:TEMP = $TemporaryDirectory
            $env:TMP = $TemporaryDirectory
            Write-Host "android_aot_staging_temp=$TemporaryDirectory"
        }

        $output = & $DotNetPath @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        if ($pushedLocation) {
            Pop-Location
        }
        $env:TEMP = $previousTemp
        $env:TMP = $previousTmp

        $ErrorActionPreference = $previousErrorActionPreference
    }

    foreach ($line in $output) {
        Write-Host $line
    }

    return [pscustomobject]@{
        ExitCode = $exitCode
        OutputText = (($output | ForEach-Object { [string]$_ }) -join "`n")
    }
}

function Test-KnownAndroidAotResponseFileFailure {
    param([string]$OutputText)

    if ([string]::IsNullOrWhiteSpace($OutputText)) {
        return $false
    }

    return $OutputText.IndexOf('Precompiling failed for', [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
        $OutputText.IndexOf('The specified response file can not be read', [System.StringComparison]::OrdinalIgnoreCase) -ge 0
}

function Get-AndroidPublishArgumentsWithoutAot {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    $retryArguments = $Arguments |
        Where-Object {
            $_ -ne '-p:RunAOTCompilation=true' -and
            $_ -ne '-p:AndroidEnableProfiledAot=true'
        }

    $retryArguments += '-p:RunAOTCompilation=false'
    $retryArguments += '-p:AndroidEnableProfiledAot=false'
    return [string[]]$retryArguments
}

$publishWorkingDirectory = [string]$stagingContext.WorkingDirectory
$publishTemporaryDirectory = [string]$stagingContext.TemporaryDirectory
$signingSecretPair = $null
try {
    $signingSecretPair = New-AndroidSigningSecretPair `
        -StoreValue $StorePass `
        -KeyValue $KeyPass
    $StorePass = $null
    $KeyPass = $null
    if ($null -ne $signingConfig) {
        $signingConfig.storePass = $null
        $signingConfig.keyPass = $null
    }
    $arguments = [string[]]@($arguments | ForEach-Object {
        if ($_ -ceq '-p:AndroidSigningStorePass=__GEORAEPLAN_STORE_SECRET_FILE__') {
            return "-p:AndroidSigningStorePass=file:$($signingSecretPair.Store.Path)"
        }
        if ($_ -ceq '-p:AndroidSigningKeyPass=__GEORAEPLAN_KEY_SECRET_FILE__') {
            return "-p:AndroidSigningKeyPass=file:$($signingSecretPair.Key.Path)"
        }
        return $_
    })
    $publishResult = Invoke-DotnetPublishAndRelay `
        -DotNetPath $resolvedDotNetPath `
        -Arguments $arguments `
        -WorkingDirectory $publishWorkingDirectory `
        -TemporaryDirectory $publishTemporaryDirectory

    if ($publishResult.ExitCode -ne 0 -and $shouldEnableAot -and (Test-KnownAndroidAotResponseFileFailure -OutputText $publishResult.OutputText)) {
        Write-Warning 'Android AOT publish failed with a known response-file path issue. Retrying once with AOT disabled so the signed release package can still be produced.'
        Write-Host 'android_profiled_aot_fallback=known_response_file_failure'

        if (Test-Path -LiteralPath $publishDirectory) {
            Remove-Item -LiteralPath $publishDirectory -Recurse -Force -ErrorAction Stop
        }
        New-Item -ItemType Directory -Force -Path $publishDirectory | Out-Null

        $arguments = Get-AndroidPublishArgumentsWithoutAot -Arguments $arguments
        $publishResult = Invoke-DotnetPublishAndRelay `
            -DotNetPath $resolvedDotNetPath `
            -Arguments $arguments `
            -WorkingDirectory $publishWorkingDirectory `
            -TemporaryDirectory $publishTemporaryDirectory
    }
}
finally {
    try {
        Remove-AndroidAotStagingContext -Context $stagingContext
    }
    finally {
        Remove-AndroidSigningSecretPair -Pair $signingSecretPair
    }
}

if ($publishResult.ExitCode -ne 0) {
    throw "dotnet publish failed with exit code $($publishResult.ExitCode)"
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

function Remove-OldArtifactDirectories {
    param(
        [Parameter(Mandatory = $true)][string]$OutputRoot,
        [Parameter(Mandatory = $true)][string]$CurrentDirectory,
        [Parameter(Mandatory = $true)][int]$KeepDirectoryCount
    )

    if ($KeepDirectoryCount -lt 1 -or -not (Test-Path -LiteralPath $OutputRoot)) {
        return @()
    }

    $resolvedOutputRoot = (Resolve-Path -LiteralPath $OutputRoot).Path.TrimEnd('\') + '\'
    $resolvedCurrentDirectory = (Resolve-Path -LiteralPath $CurrentDirectory).Path
    if (-not $resolvedCurrentDirectory.StartsWith($resolvedOutputRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Current artifact directory is outside output root: $resolvedCurrentDirectory"
    }

    $directories = Get-ChildItem -LiteralPath $OutputRoot -Directory -ErrorAction SilentlyContinue |
        Sort-Object -Property @{ Expression = 'LastWriteTimeUtc'; Descending = $true }, @{ Expression = 'Name'; Descending = $false }

    $preserve = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::OrdinalIgnoreCase)
    [void]$preserve.Add($resolvedCurrentDirectory)
    foreach ($directory in $directories) {
        if ($preserve.Count -ge $KeepDirectoryCount) {
            break
        }

        [void]$preserve.Add((Resolve-Path -LiteralPath $directory.FullName).Path)
    }

    $removed = New-Object System.Collections.Generic.List[string]
    foreach ($directory in $directories) {
        $resolvedDirectory = (Resolve-Path -LiteralPath $directory.FullName).Path
        if ($preserve.Contains($resolvedDirectory)) {
            continue
        }

        if (-not $resolvedDirectory.StartsWith($resolvedOutputRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Artifact prune target is outside output root: $resolvedDirectory"
        }

        Remove-Item -LiteralPath $resolvedDirectory -Recurse -Force -ErrorAction Stop
        $removed.Add($directory.Name) | Out-Null
    }

    return $removed
}

function Remove-LooseArtifactFiles {
    param(
        [Parameter(Mandatory = $true)][string]$OutputRoot
    )

    if (-not (Test-Path -LiteralPath $OutputRoot)) {
        return @()
    }

    $resolvedOutputRoot = (Resolve-Path -LiteralPath $OutputRoot).Path.TrimEnd('\') + '\'
    $removed = New-Object System.Collections.Generic.List[string]
    $files = Get-ChildItem -LiteralPath $OutputRoot -File -ErrorAction SilentlyContinue
    foreach ($file in $files) {
        $resolvedFile = (Resolve-Path -LiteralPath $file.FullName).Path
        if (-not $resolvedFile.StartsWith($resolvedOutputRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Loose artifact prune target is outside output root: $resolvedFile"
        }

        Remove-Item -LiteralPath $resolvedFile -Force -ErrorAction Stop
        $removed.Add($file.Name) | Out-Null
    }

    return $removed
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
    Write-Host "apk_ready=$($apkFile.FullName)"
    Write-Host "apk_sha256=$($apkHash.Hash)"
    Write-Host "apk_sha256_file=$($apkHash.HashFile)"
    if ($SkipDeploymentCopy.IsPresent) {
        Write-Host "apk_deployment_copy=skipped"
    }
    else {
        $stableApkVersion = if ([string]::IsNullOrWhiteSpace($VersionName)) { 'latest' } else { $VersionName }
        $stableApkPrefix = Get-Utf8String '6rGw656Y7ZSM656cLeyViOuTnOuhnOydtOuTnC12'
        $stableApkName = "$stableApkPrefix$stableApkVersion-signed.apk"
        $stableApkFilter = Get-Utf8String '6rGw656Y7ZSM656cLeyViOuTnOuhnOydtOuTnC12Ki1zaWduZWQuYXBrKg=='
        Get-ChildItem -LiteralPath $deploymentRoot -File -Filter $stableApkFilter -ErrorAction SilentlyContinue |
            Remove-Item -Force -ErrorAction SilentlyContinue
        $stableApkPath = Join-Path $deploymentRoot $stableApkName
        Copy-Item -LiteralPath $apkFile.FullName -Destination $stableApkPath -Force
        $stableApkHash = Write-PackageHash -File (Get-Item -LiteralPath $stableApkPath)
        Write-Host "apk_deployment_copy=$stableApkPath"
        Write-Host "apk_deployment_sha256=$($stableApkHash.Hash)"
        Write-Host "apk_deployment_sha256_file=$($stableApkHash.HashFile)"
    }
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

if (-not $SkipArtifactPrune) {
    $removedArtifactDirectories = Remove-OldArtifactDirectories -OutputRoot $OutputRoot -CurrentDirectory $publishDirectory -KeepDirectoryCount $KeepArtifactDirectoryCount
    if ($removedArtifactDirectories.Count -gt 0) {
        Write-Host "android_artifact_directories_pruned=$($removedArtifactDirectories.Count)"
    }

    $removedLooseArtifactFiles = Remove-LooseArtifactFiles -OutputRoot $OutputRoot
    if ($removedLooseArtifactFiles.Count -gt 0) {
        Write-Host "android_loose_artifact_files_pruned=$($removedLooseArtifactFiles.Count)"
    }
}
