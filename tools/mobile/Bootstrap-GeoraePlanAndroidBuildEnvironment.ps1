[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$ProjectFile,
    [string]$DotNetInstallDir,
    [string]$AndroidSdkDirectory,
    [string]$JavaSdkDirectory,
    [string]$DotNetChannel = '8.0',
    [switch]$ForceWorkloadInstall
)

function Resolve-DefaultProjectRoot {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptPath
    )

    return (Resolve-Path (Join-Path (Split-Path -Parent $ScriptPath) '..\..')).Path
}

function Get-JavaSdkMajorVersion {
    param(
        [Parameter(Mandatory = $true)][string]$CandidatePath
    )

    $releasePath = Join-Path $CandidatePath 'release'
    if (-not (Test-Path -LiteralPath $releasePath -PathType Leaf)) {
        return $null
    }

    $versionLine = Get-Content -LiteralPath $releasePath -Encoding ASCII |
        Where-Object { $_ -match '^JAVA_VERSION=' } |
        Select-Object -First 1
    if ($versionLine -match '^JAVA_VERSION="(?<major>\d+)(?:[._]|")') {
        return [int]$Matches.major
    }

    return $null
}

function Get-ResolvedJavaSdkDirectory {
    param(
        [string]$RequestedPath
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        if (-not (Test-Path -LiteralPath $RequestedPath -PathType Container)) {
            throw "Requested JavaSdkDirectory does not exist: $RequestedPath"
        }

        $resolvedRequestedPath = (Resolve-Path -LiteralPath $RequestedPath).Path
        if ((Get-JavaSdkMajorVersion -CandidatePath $resolvedRequestedPath) -ne 17 -or
            -not (Test-Path -LiteralPath (Join-Path $resolvedRequestedPath 'bin\java.exe') -PathType Leaf) -or
            -not (Test-Path -LiteralPath (Join-Path $resolvedRequestedPath 'bin\javac.exe') -PathType Leaf) -or
            -not (Test-Path -LiteralPath (Join-Path $resolvedRequestedPath 'bin\keytool.exe') -PathType Leaf)) {
            throw "Requested JavaSdkDirectory must be a complete JDK 17: $resolvedRequestedPath"
        }

        return $resolvedRequestedPath
    }

    $candidates = [System.Collections.Generic.List[string]]::new()
    foreach ($directCandidate in @(
        $env:GEORAEPLAN_ANDROID_JAVA_SDK,
        'D:\DevCaches\georaeplan-android-jdk\microsoft-jdk-17.0.20',
        (Join-Path $env:LOCALAPPDATA 'GeoraePlan.Android\microsoft-jdk-17.0.20'),
        $env:JAVA_HOME
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
        'C:\Program Files\Microsoft\jdk-17*\bin\javac.exe',
        'C:\Program Files\Java\jdk-17*\bin\javac.exe',
        (Join-Path $env:USERPROFILE '.antigravity\extensions\*\jre\*\bin\javac.exe')
    )) {
        foreach ($match in Get-ChildItem -Path $pattern -ErrorAction SilentlyContinue | Sort-Object FullName -Descending) {
            $candidates.Add((Split-Path -Parent (Split-Path -Parent $match.FullName))) | Out-Null
        }
    }

    foreach ($candidate in $candidates | Select-Object -Unique) {
        if ([string]::IsNullOrWhiteSpace($candidate) -or -not (Test-Path -LiteralPath $candidate -PathType Container)) {
            continue
        }

        $resolvedCandidate = (Resolve-Path -LiteralPath $candidate).Path
        if ((Get-JavaSdkMajorVersion -CandidatePath $resolvedCandidate) -ne 17) {
            continue
        }

        if ((Test-Path -LiteralPath (Join-Path $resolvedCandidate 'bin\java.exe') -PathType Leaf) -and
            (Test-Path -LiteralPath (Join-Path $resolvedCandidate 'bin\javac.exe') -PathType Leaf) -and
            (Test-Path -LiteralPath (Join-Path $resolvedCandidate 'bin\keytool.exe') -PathType Leaf)) {
            return $resolvedCandidate
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

if ([string]::IsNullOrWhiteSpace($DotNetInstallDir)) {
    $DotNetInstallDir = Join-Path $env:LOCALAPPDATA 'GeoraePlan.Android\dotnet8'
}

if ([string]::IsNullOrWhiteSpace($AndroidSdkDirectory)) {
    $AndroidSdkDirectory = Join-Path $env:LOCALAPPDATA 'GeoraePlan.Android\android-sdk'
}

$JavaSdkDirectory = Get-ResolvedJavaSdkDirectory -RequestedPath $JavaSdkDirectory
if ([string]::IsNullOrWhiteSpace($JavaSdkDirectory)) {
    throw 'JavaSdkDirectory not found. Install a complete JDK 17 or pass -JavaSdkDirectory.'
}

New-Item -ItemType Directory -Force -Path $DotNetInstallDir | Out-Null
New-Item -ItemType Directory -Force -Path $AndroidSdkDirectory | Out-Null

$dotnetInstallScript = Join-Path $env:TEMP 'dotnet-install-georaeplan.ps1'
if (-not (Test-Path -LiteralPath (Join-Path $DotNetInstallDir 'dotnet.exe'))) {
    Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $dotnetInstallScript
    & powershell -NoProfile -ExecutionPolicy Bypass -File $dotnetInstallScript -Channel $DotNetChannel -InstallDir $DotNetInstallDir
}

$dotnetPath = Join-Path $DotNetInstallDir 'dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnetPath)) {
    throw "dotnet install failed: $dotNetPath"
}

$workloadOutput = & $dotnetPath workload list 2>&1 | Out-String
$hasMauiAndroid = [bool]($workloadOutput | Select-String -Pattern 'maui-android' -SimpleMatch)
if ($ForceWorkloadInstall -or -not $hasMauiAndroid) {
    & $dotnetPath workload install maui-android
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet workload install maui-android failed with exit code $LASTEXITCODE"
    }
}

$env:JAVA_HOME = $JavaSdkDirectory
$env:ANDROID_SDK_ROOT = $AndroidSdkDirectory
$env:ANDROID_HOME = $AndroidSdkDirectory
$env:PATH = (Join-Path $JavaSdkDirectory 'bin') + ';' + (Split-Path -Parent $dotnetPath) + ';' + $env:PATH

& $dotnetPath build $ProjectFile `
    -t:InstallAndroidDependencies `
    -f net8.0-android `
    "-p:AndroidSdkDirectory=$AndroidSdkDirectory" `
    "-p:JavaSdkDirectory=$JavaSdkDirectory" `
    '-p:AcceptAndroidSdkLicenses=True'

if ($LASTEXITCODE -ne 0) {
    throw "InstallAndroidDependencies failed with exit code $LASTEXITCODE"
}

Write-Host "bootstrap_ready=true"
Write-Host "dotnet_path=$dotnetPath"
Write-Host "java_sdk_directory=$JavaSdkDirectory"
Write-Host "android_sdk_directory=$AndroidSdkDirectory"
