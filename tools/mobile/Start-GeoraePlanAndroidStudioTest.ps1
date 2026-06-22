[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$SigningConfigPath,
    [string]$PreferredAvd = 'Medium_Phone_API_36.1',
    [string]$PackageName = 'kr.georaeplan.mobile',
    [switch]$SkipBuild,
    [switch]$SkipOpenStudio
)

$ErrorActionPreference = 'Stop'

function Resolve-DefaultProjectRoot {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptPath
    )

    return (Resolve-Path (Join-Path (Split-Path -Parent $ScriptPath) '..\..')).Path
}

function Resolve-AndroidStudioPath {
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

    throw 'Android Studio 실행 파일(studio64.exe)을 찾지 못했습니다.'
}

function Resolve-AndroidStudioSdkDirectory {
    foreach ($candidate in @(
        (Join-Path $env:LOCALAPPDATA 'Android\Sdk'),
        $env:ANDROID_SDK_ROOT,
        $env:ANDROID_HOME
    )) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path -LiteralPath $candidate)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw 'Android Studio용 Android SDK 경로를 찾지 못했습니다.'
}

function Resolve-JavaSdkDirectory {
    foreach ($candidate in @(
        (Join-Path $env:ProgramFiles 'Android\Android Studio\jbr'),
        (Join-Path ${env:ProgramFiles(x86)} 'Android\Android Studio\jbr'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Android Studio\jbr'),
        $env:JAVA_HOME
    )) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and
            (Test-Path -LiteralPath (Join-Path $candidate 'bin\java.exe'))) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw 'Android Studio JBR/JDK 경로를 찾지 못했습니다.'
}

function Resolve-AdbPath {
    param(
        [Parameter(Mandatory = $true)][string]$AndroidSdkDirectory
    )

    foreach ($candidate in @(
        (Join-Path $AndroidSdkDirectory 'platform-tools\adb.exe'),
        (Join-Path $env:LOCALAPPDATA 'GeoraePlan.Android\android-sdk\platform-tools\adb.exe')
    )) {
        if (Test-Path -LiteralPath $candidate) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw 'adb.exe 를 찾지 못했습니다.'
}

function Resolve-EmulatorPath {
    param(
        [Parameter(Mandatory = $true)][string]$AndroidSdkDirectory
    )

    foreach ($candidate in @(
        (Join-Path $AndroidSdkDirectory 'emulator\emulator.exe'),
        (Join-Path $env:LOCALAPPDATA 'GeoraePlan.Android\android-sdk\emulator\emulator.exe')
    )) {
        if (Test-Path -LiteralPath $candidate) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw 'emulator.exe 를 찾지 못했습니다.'
}

function Get-ConnectedDeviceId {
    param(
        [Parameter(Mandatory = $true)][string]$AdbPath
    )

    $devices = & $AdbPath devices 2>$null
    foreach ($line in $devices) {
        if ($line -match '^(emulator-\d+|[A-Za-z0-9._:-]+)\s+device$') {
            return $Matches[1]
        }
    }

    return $null
}

function Wait-ForDevice {
    param(
        [Parameter(Mandatory = $true)][string]$AdbPath,
        [int]$TimeoutSeconds = 240
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        Start-Sleep -Seconds 5
        $deviceId = Get-ConnectedDeviceId -AdbPath $AdbPath
        if ($deviceId) {
            return $deviceId
        }
    } until ((Get-Date) -ge $deadline)

    throw '에뮬레이터 시작 후 장치를 찾지 못했습니다.'
}

function Wait-ForBoot {
    param(
        [Parameter(Mandatory = $true)][string]$AdbPath,
        [Parameter(Mandatory = $true)][string]$DeviceId,
        [int]$TimeoutSeconds = 300
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        Start-Sleep -Seconds 5
        $bootRaw = & $AdbPath -s $DeviceId shell getprop sys.boot_completed 2>$null
        $boot = if ($bootRaw) { ($bootRaw | Out-String).Trim() } else { '' }
        if ($boot -eq '1') {
            return
        }
    } until ((Get-Date) -ge $deadline)

    throw '에뮬레이터 부팅 완료를 확인하지 못했습니다.'
}

function Resolve-LatestApk {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot
    )

    $deploymentRoot = Join-Path $ProjectRoot '배포'
    $artifactRoot = Join-Path $ProjectRoot 'Mobile\artifacts\android'
    $searchRoots = @($deploymentRoot, $artifactRoot)

    $apk = $searchRoots |
        Where-Object { Test-Path -LiteralPath $_ } |
        ForEach-Object { Get-ChildItem -LiteralPath $_ -Recurse -File -Filter '*.apk' -ErrorAction SilentlyContinue } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if ($apk) {
        return $apk
    }

    throw '설치할 APK 파일을 찾지 못했습니다.'
}

function Install-MobileApk {
    param(
        [Parameter(Mandatory = $true)][string]$AdbPath,
        [Parameter(Mandatory = $true)][string]$DeviceId,
        [Parameter(Mandatory = $true)][string]$PackageName,
        [Parameter(Mandatory = $true)][string]$ApkPath
    )

    $installOutput = & $AdbPath -s $DeviceId install -r $ApkPath 2>&1
    if ($LASTEXITCODE -eq 0) {
        return
    }

    $installText = ($installOutput | Out-String).Trim()
    if ($installText -match 'INSTALL_FAILED_VERSION_DOWNGRADE') {
        throw "APK 설치에 실패했습니다. 설치 대상 APK가 기기에 설치된 앱보다 낮은 버전입니다. 최신 APK를 빌드한 뒤 다시 실행하세요. apk=$ApkPath detail=$installText"
    }

    & $AdbPath -s $DeviceId uninstall $PackageName | Out-Null
    $retryOutput = & $AdbPath -s $DeviceId install -r $ApkPath 2>&1
    if ($LASTEXITCODE -ne 0) {
        $retryText = ($retryOutput | Out-String).Trim()
        throw "APK 설치에 실패했습니다. apk=$ApkPath first=$installText retry=$retryText"
    }
}

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = Resolve-DefaultProjectRoot -ScriptPath $MyInvocation.MyCommand.Path
}

if ([string]::IsNullOrWhiteSpace($SigningConfigPath)) {
    $SigningConfigPath = Join-Path $ProjectRoot 'Mobile\GeoraePlan.Mobile.App\android-signing.local.json'
}

$androidStudioPath = Resolve-AndroidStudioPath
$androidStudioSdkDirectory = Resolve-AndroidStudioSdkDirectory
$javaSdkDirectory = Resolve-JavaSdkDirectory
$adbPath = Resolve-AdbPath -AndroidSdkDirectory $androidStudioSdkDirectory
$emulatorPath = Resolve-EmulatorPath -AndroidSdkDirectory $androidStudioSdkDirectory
$mobileProjectDirectory = Join-Path $ProjectRoot 'Mobile\GeoraePlan.Mobile.App'

if (-not $SkipOpenStudio) {
    Start-Process -FilePath $androidStudioPath -ArgumentList @("""$mobileProjectDirectory""") | Out-Null
}

& $adbPath start-server | Out-Null
$deviceId = Get-ConnectedDeviceId -AdbPath $adbPath

if (-not $deviceId) {
    $avds = & $emulatorPath -list-avds | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    if (-not $avds) {
        throw '생성된 Android Virtual Device(AVD)가 없습니다. Android Studio에서 가상 디바이스를 먼저 만드세요.'
    }

    $selectedAvd = $avds | Where-Object { $_ -eq $PreferredAvd } | Select-Object -First 1
    if (-not $selectedAvd) {
        $selectedAvd = $avds[0]
    }

    Start-Process -FilePath $emulatorPath -ArgumentList @('-avd', $selectedAvd, '-no-snapshot-load', '-no-boot-anim') | Out-Null
    $deviceId = Wait-ForDevice -AdbPath $adbPath
    Wait-ForBoot -AdbPath $adbPath -DeviceId $deviceId
}

if (-not $SkipBuild) {
    $buildScriptPath = Join-Path $ProjectRoot 'tools\mobile\Build-GeoraePlanAndroidApk.ps1'
    & $buildScriptPath `
        -ProjectRoot $ProjectRoot `
        -SigningConfigPath $SigningConfigPath `
        -SkipEnvironmentCheck
}

$apk = Resolve-LatestApk -ProjectRoot $ProjectRoot
Install-MobileApk -AdbPath $adbPath -DeviceId $deviceId -PackageName $PackageName -ApkPath $apk.FullName
& $adbPath -s $deviceId shell monkey -p $PackageName -c android.intent.category.LAUNCHER 1 | Out-Null

Write-Host "android_studio=$androidStudioPath"
Write-Host "android_sdk_directory=$androidStudioSdkDirectory"
Write-Host "java_sdk_directory=$javaSdkDirectory"
Write-Host "device=$deviceId"
Write-Host "apk=$($apk.FullName)"
Write-Host 'android_studio_test_ready=true'
