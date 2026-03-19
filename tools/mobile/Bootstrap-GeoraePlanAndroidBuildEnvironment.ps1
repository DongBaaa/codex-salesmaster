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

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = Resolve-DefaultProjectRoot -ScriptPath $MyInvocation.MyCommand.Path
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
    throw 'JavaSdkDirectory not found. Install JDK 17+ or pass -JavaSdkDirectory.'
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
