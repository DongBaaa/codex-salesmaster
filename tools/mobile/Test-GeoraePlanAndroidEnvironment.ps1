[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$ProjectFile,
    [string]$DotNetPath,
    [string]$JavaSdkDirectory,
    [string]$AndroidSdkDirectory
)

function Resolve-DefaultProjectRoot {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptPath
    )

    return (Resolve-Path (Join-Path (Split-Path -Parent $ScriptPath) '..\..')).Path
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
        (Join-Path $ProjectRoot '.dotnet\dotnet.exe'),
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
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
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

function Get-ResolvedAndroidStudioPath {
    foreach ($candidate in @(
        (Join-Path $env:ProgramFiles 'Android\Android Studio\bin\studio64.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Android\Android Studio\bin\studio64.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Android Studio\bin\studio64.exe')
    )) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path -LiteralPath $candidate)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    $command = Get-Command studio64.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    return $null
}

function Add-CheckResult {
    param(
        [System.Collections.Generic.List[object]]$Results,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][bool]$Passed,
        [Parameter(Mandatory = $true)][string]$Detail
    )

    $Results.Add([pscustomobject]@{
        Name   = $Name
        Passed = $Passed
        Detail = $Detail
    }) | Out-Null
}

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = Resolve-DefaultProjectRoot -ScriptPath $MyInvocation.MyCommand.Path
}

if ([string]::IsNullOrWhiteSpace($ProjectFile)) {
    $ProjectFile = Join-Path $ProjectRoot 'Mobile\GeoraePlan.Mobile.App\GeoraePlan.Mobile.App.csproj'
}

$resolvedDotNetPath = Get-ResolvedDotNetPath -ProjectRoot $ProjectRoot -RequestedPath $DotNetPath
$resolvedJavaSdkDirectory = Get-ResolvedJavaSdkDirectory -ProjectRoot $ProjectRoot -RequestedPath $JavaSdkDirectory
$resolvedAndroidSdkDirectory = Get-ResolvedAndroidSdkDirectory -ProjectRoot $ProjectRoot -RequestedPath $AndroidSdkDirectory
$resolvedAndroidStudioPath = Get-ResolvedAndroidStudioPath

$results = [System.Collections.Generic.List[object]]::new()

Add-CheckResult -Results $results -Name 'dotnet' -Passed (-not [string]::IsNullOrWhiteSpace($resolvedDotNetPath)) -Detail ($(if ($resolvedDotNetPath) { $resolvedDotNetPath } else { 'dotnet not found' }))
Add-CheckResult -Results $results -Name 'project-file' -Passed (Test-Path -LiteralPath $ProjectFile) -Detail $ProjectFile

$workloadInstalled = $false
$workloadDetail = 'dotnet unavailable'
if (-not [string]::IsNullOrWhiteSpace($resolvedDotNetPath)) {
    try {
        $workloadOutput = & $resolvedDotNetPath workload list 2>&1 | Out-String
        $workloadInstalled = [bool]($workloadOutput | Select-String -Pattern 'maui-android' -SimpleMatch)
        $workloadDetail = $workloadOutput.Trim()
    }
    catch {
        $workloadDetail = $_.Exception.Message
    }
}
Add-CheckResult -Results $results -Name 'maui-android-workload' -Passed $workloadInstalled -Detail $workloadDetail

$javaExecutable = if ($resolvedJavaSdkDirectory) { Join-Path $resolvedJavaSdkDirectory 'bin\java.exe' } else { $null }
$keytoolExecutable = if ($resolvedJavaSdkDirectory) { Join-Path $resolvedJavaSdkDirectory 'bin\keytool.exe' } else { $null }
$adbExecutable = if ($resolvedAndroidSdkDirectory) { Join-Path $resolvedAndroidSdkDirectory 'platform-tools\adb.exe' } else { $null }
$emulatorExecutable = if ($resolvedAndroidSdkDirectory) { Join-Path $resolvedAndroidSdkDirectory 'emulator\emulator.exe' } else { $null }

Add-CheckResult -Results $results -Name 'java-sdk' -Passed ($javaExecutable -and (Test-Path -LiteralPath $javaExecutable)) -Detail ($(if ($javaExecutable) { $resolvedJavaSdkDirectory } else { 'JDK/JRE not found' }))
Add-CheckResult -Results $results -Name 'keytool' -Passed ($keytoolExecutable -and (Test-Path -LiteralPath $keytoolExecutable)) -Detail ($(if ($keytoolExecutable) { $keytoolExecutable } else { 'keytool not found' }))
Add-CheckResult -Results $results -Name 'android-sdk' -Passed (-not [string]::IsNullOrWhiteSpace($resolvedAndroidSdkDirectory)) -Detail ($(if ($resolvedAndroidSdkDirectory) { $resolvedAndroidSdkDirectory } else { 'Android SDK not found' }))
Add-CheckResult -Results $results -Name 'android-studio(optional)' -Passed $true -Detail ($(if ($resolvedAndroidStudioPath) { $resolvedAndroidStudioPath } else { 'Android Studio not found (okay unless using Android Studio 직접 테스트)' }))
Add-CheckResult -Results $results -Name 'adb(optional)' -Passed $true -Detail ($(if ($adbExecutable -and (Test-Path -LiteralPath $adbExecutable)) { $adbExecutable } else { 'adb not found (okay unless deploying to device)' }))
Add-CheckResult -Results $results -Name 'emulator(optional)' -Passed $true -Detail ($(if ($emulatorExecutable -and (Test-Path -LiteralPath $emulatorExecutable)) { $emulatorExecutable } else { 'emulator not found (okay unless using Android Studio emulator)' }))

$results | Format-Table -AutoSize | Out-Host

$failed = $results | Where-Object { -not $_.Passed }
if ($failed.Count -gt 0) {
    Write-Error ("Android environment check failed: " + (($failed.Name) -join ', '))
}

Write-Host 'android_environment_ready=true'
