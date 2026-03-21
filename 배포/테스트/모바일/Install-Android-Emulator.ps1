param(
    [string]$ArtifactDirectory = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'

function Resolve-AdbPath {
    foreach ($candidate in @(
        (Join-Path $env:LOCALAPPDATA 'Android\Sdk\platform-tools\adb.exe'),
        (Join-Path $env:LOCALAPPDATA 'GeoraePlan.Android\android-sdk\platform-tools\adb.exe')
    )) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    $command = Get-Command adb -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    throw 'adb.exe 를 찾을 수 없습니다. Android Studio SDK platform-tools 를 먼저 설치하세요.'
}

function Resolve-EmulatorPath {
    foreach ($candidate in @(
        (Join-Path $env:LOCALAPPDATA 'Android\Sdk\emulator\emulator.exe'),
        (Join-Path $env:LOCALAPPDATA 'GeoraePlan.Android\android-sdk\emulator\emulator.exe')
    )) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    $command = Get-Command emulator -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    return $null
}

function Resolve-ApkFile {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDirectory
    )

    foreach ($searchRoot in @(
        $ArtifactDirectory,
        (Split-Path -Parent (Split-Path -Parent $ArtifactDirectory))
    )) {
        if (-not (Test-Path -LiteralPath $searchRoot)) {
            continue
        }

        $apk = Get-ChildItem -LiteralPath $searchRoot -Recurse -File -Filter *.apk -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1

        if ($apk) {
            return $apk
        }
    }

    throw "APK 파일을 찾을 수 없습니다: $ArtifactDirectory"
}

function Get-ConnectedDeviceId([string]$AdbPath) {
    $devices = & $AdbPath devices 2>$null
    foreach ($line in $devices) {
        if ($line -match '^(emulator-\d+|[A-Za-z0-9._:-]+)\s+device$') {
            return $Matches[1]
        }
    }

    return $null
}

function Wait-ForBoot([string]$AdbPath, [string]$DeviceId, [int]$TimeoutSeconds = 240) {
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

$apk = Resolve-ApkFile -ArtifactDirectory $ArtifactDirectory

$adb = Resolve-AdbPath
$emulator = Resolve-EmulatorPath

& $adb start-server | Out-Null
$deviceId = Get-ConnectedDeviceId -AdbPath $adb

if (-not $deviceId) {
    if (-not $emulator) {
        throw '실행 중인 에뮬레이터가 없고 emulator.exe 도 찾지 못했습니다. Android Studio Emulator 를 설치하세요.'
    }

    $avds = & $emulator -list-avds | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    if (-not $avds) {
        throw '생성된 Android Virtual Device(AVD)가 없습니다. Android Studio에서 가상 디바이스를 먼저 만드세요.'
    }

    $preferredAvd = $avds | Where-Object { $_ -eq 'Medium_Phone_API_36.1' } | Select-Object -First 1
    $avd = if ($preferredAvd) { $preferredAvd } else { $avds[0] }

    Start-Process -FilePath $emulator -ArgumentList @('-avd', $avd, '-no-snapshot-load', '-no-boot-anim') | Out-Null

    $deadline = (Get-Date).AddMinutes(4)
    do {
        Start-Sleep -Seconds 5
        $deviceId = Get-ConnectedDeviceId -AdbPath $adb
    } until ($deviceId -or (Get-Date) -ge $deadline)

    if (-not $deviceId) {
        throw '에뮬레이터 시작 후 장치를 찾지 못했습니다.'
    }

    Wait-ForBoot -AdbPath $adb -DeviceId $deviceId
}

& $adb -s $deviceId install -r $apk.FullName
if ($LASTEXITCODE -ne 0) {
    throw 'APK 설치에 실패했습니다.'
}

Write-Host "device=$deviceId"
Write-Host "apk=$($apk.FullName)"
Write-Host 'install_complete=true'
