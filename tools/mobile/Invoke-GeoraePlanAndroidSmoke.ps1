param(
    [string]$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [string]$AdbPath,
    [string]$ApkPath,
    [string]$PackageName = 'kr.georaeplan.mobile',
    [string]$Username = 'usenet',
    [string]$Password = '1234',
    [string]$EvidenceDirectory,
    [switch]$SkipInstall,
    [switch]$IncludeDraftScreens
)

$ErrorActionPreference = 'Stop'
$script:GeoraePlanMobilePackageName = $PackageName

function Resolve-AdbPath {
    param([string]$RequestedPath)

    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $candidates += $RequestedPath
    }

    if (-not [string]::IsNullOrWhiteSpace($env:ANDROID_HOME)) {
        $candidates += (Join-Path $env:ANDROID_HOME 'platform-tools\adb.exe')
    }
    if (-not [string]::IsNullOrWhiteSpace($env:ANDROID_SDK_ROOT)) {
        $candidates += (Join-Path $env:ANDROID_SDK_ROOT 'platform-tools\adb.exe')
    }
    $candidates += @(
        (Join-Path $env:LOCALAPPDATA 'Android\Sdk\platform-tools\adb.exe'),
        (Join-Path $env:LOCALAPPDATA 'GeoraePlan.Android\android-sdk\platform-tools\adb.exe')
    )

    foreach ($candidate in $candidates | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) {
        if (Test-Path -LiteralPath $candidate) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw 'adb.exe를 찾지 못했습니다. Android SDK platform-tools 경로를 확인하세요.'
}

function Resolve-ApkPath {
    param(
        [string]$ProjectRoot,
        [string]$RequestedPath
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        if (-not (Test-Path -LiteralPath $RequestedPath)) {
            throw "지정한 APK 파일을 찾지 못했습니다: $RequestedPath"
        }
        return (Resolve-Path -LiteralPath $RequestedPath).Path
    }

    $mobileOut = Join-Path $ProjectRoot 'Mobile\GeoraePlan.Mobile.App\bin\Debug\net8.0-android'
    $apk = Get-ChildItem -LiteralPath $mobileOut -Filter '*Signed.apk' -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if ($apk) {
        return $apk.FullName
    }

    throw "설치할 APK 파일을 찾지 못했습니다: $mobileOut"
}

function Invoke-Adb {
    param(
        [Parameter(Mandatory = $true)][string]$AdbPath,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $AdbPath @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    if ($exitCode -ne 0) {
        throw "adb 실패: adb $($Arguments -join ' ')`n$output"
    }
    return $output
}

function Install-MobileApk {
    param(
        [Parameter(Mandatory = $true)][string]$AdbPath,
        [Parameter(Mandatory = $true)][string]$DeviceId,
        [Parameter(Mandatory = $true)][string]$ApkPath,
        [Parameter(Mandatory = $true)][string]$PackageName
    )

    try {
        Invoke-Adb -AdbPath $AdbPath -Arguments @('-s', $DeviceId, 'install', '-r', $ApkPath) | Out-Null
    }
    catch {
        $message = $_.Exception.Message
        if ($message -notmatch 'INSTALL_FAILED_UPDATE_INCOMPATIBLE|signatures do not match') {
            throw
        }

        Invoke-Adb -AdbPath $AdbPath -Arguments @('-s', $DeviceId, 'uninstall', $PackageName) | Out-Null
        Invoke-Adb -AdbPath $AdbPath -Arguments @('-s', $DeviceId, 'install', '-r', $ApkPath) | Out-Null
    }
}


function Start-MobileApp {
    param(
        [string]$AdbPath,
        [string]$DeviceId,
        [string]$PackageName
    )

    $activityLines = Invoke-Adb -AdbPath $AdbPath -Arguments @('-s', $DeviceId, 'shell', 'cmd', 'package', 'resolve-activity', '--brief', '-a', 'android.intent.action.MAIN', '-c', 'android.intent.category.LAUNCHER', $PackageName)
    $activity = $activityLines |
        Where-Object { $_ -match '^[^/]+/[^\s]+$' } |
        Select-Object -Last 1
    if ([string]::IsNullOrWhiteSpace($activity)) {
        throw "Android launcher activity? ?? ?????: $PackageName"
    }

    Invoke-Adb -AdbPath $AdbPath -Arguments @('-s', $DeviceId, 'shell', 'am', 'start', '-n', $activity) | Out-Null
}

function Get-ConnectedDeviceId {
    param([string]$AdbPath)

    Invoke-Adb -AdbPath $AdbPath -Arguments @('start-server') | Out-Null
    $devices = Invoke-Adb -AdbPath $AdbPath -Arguments @('devices')
    $device = $devices |
        Where-Object { $_ -match '^\S+\s+device$' } |
        Select-Object -First 1

    if (-not $device) {
        throw '연결된 Android 기기/에뮬레이터가 없습니다. 에뮬레이터를 켠 뒤 다시 실행하세요.'
    }

    return ($device -split '\s+')[0]
}

function Get-ScreenSize {
    param(
        [string]$AdbPath,
        [string]$DeviceId
    )

    $sizeLine = Invoke-Adb -AdbPath $AdbPath -Arguments @('-s', $DeviceId, 'shell', 'wm', 'size') |
        Select-Object -First 1
    if ($sizeLine -match '(\d+)x(\d+)') {
        return [pscustomobject]@{ Width = [int]$Matches[1]; Height = [int]$Matches[2] }
    }

    return [pscustomobject]@{ Width = 1080; Height = 2400 }
}

function Get-UiDump {
    param(
        [string]$AdbPath,
        [string]$DeviceId,
        [string]$EvidenceDirectory,
        [string]$Name
    )

    $remote = '/sdcard/georaeplan-window.xml'
    $local = Join-Path $EvidenceDirectory "$Name.xml"

    $lastError = $null
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            Invoke-Adb -AdbPath $AdbPath -Arguments @('-s', $DeviceId, 'shell', 'uiautomator', 'dump', $remote) | Out-Null
            Invoke-Adb -AdbPath $AdbPath -Arguments @('-s', $DeviceId, 'pull', $remote, $local) | Out-Null
            $content = Get-Content -LiteralPath $local -Raw -Encoding UTF8
            if (-not [string]::IsNullOrWhiteSpace($content) -and $content.Contains('<hierarchy')) {
                return [pscustomobject]@{ Path = $local; Content = $content }
            }
        }
        catch {
            $lastError = $_
            Start-Sleep -Seconds 1
        }
    }

    if ($lastError) {
        throw $lastError
    }

    throw "UI hierarchy dump를 가져오지 못했습니다: $Name"
}

function Assert-UiContains {
    param(
        [string]$Content,
        [string[]]$Needles,
        [string]$StepName
    )

    $missing = @()
    foreach ($needle in $Needles) {
        if (-not $Content.Contains($needle)) {
            $missing += $needle
        }
    }

    if ($missing.Count -gt 0) {
        throw "$StepName 확인 실패. 찾지 못한 문구: $($missing -join ', ')"
    }
}

function Wait-UiForAppReady {
    param(
        [string]$AdbPath,
        [string]$DeviceId,
        [string]$EvidenceDirectory,
        [string]$Timestamp,
        [string]$PackageName,
        [int]$TimeoutSeconds = 90
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $attempt = 0
    $lastDump = $null
    while ((Get-Date) -lt $deadline) {
        $attempt++
        $dump = Get-UiDump -AdbPath $AdbPath -DeviceId $DeviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-smoke-$Timestamp-launch-$attempt"
        $lastDump = $dump
        $content = $dump.Content
        if (Dismiss-AndroidAnrDialog -AdbPath $AdbPath -DeviceId $DeviceId -Content $content) {
            Start-Sleep -Seconds 5
            continue
        }

        $isTargetApp = $content.Contains("package=`"$PackageName`"")
        $isReadyScreen = $content.Contains('계정 로그인') -or
            ($content.Contains('로그인') -and $content.Contains('비밀번호')) -or
            ($content.Contains('홈') -and $content.Contains('판매 작성'))

        if ($isTargetApp -and $isReadyScreen) {
            Copy-Item -LiteralPath $dump.Path -Destination (Join-Path $EvidenceDirectory "mobile-smoke-$Timestamp-launch.xml") -Force
            return $dump
        }

        Start-Sleep -Seconds 2
    }

    if ($lastDump) {
        Copy-Item -LiteralPath $lastDump.Path -Destination (Join-Path $EvidenceDirectory "mobile-smoke-$Timestamp-launch.xml") -Force
        return $lastDump
    }

    return Get-UiDump -AdbPath $AdbPath -DeviceId $DeviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-smoke-$Timestamp-launch"
}

function Wait-UiContainsAll {
    param(
        [string]$AdbPath,
        [string]$DeviceId,
        [string]$EvidenceDirectory,
        [string]$Name,
        [string[]]$Needles,
        [string]$StepName,
        [int]$TimeoutSeconds = 60
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $attempt = 0
    $lastDump = $null
    while ((Get-Date) -lt $deadline) {
        $attempt++
        $dump = Get-UiDump -AdbPath $AdbPath -DeviceId $DeviceId -EvidenceDirectory $EvidenceDirectory -Name "$Name-$attempt"
        $lastDump = $dump
        if (Dismiss-AndroidAnrDialog -AdbPath $AdbPath -DeviceId $DeviceId -Content $dump.Content) {
            Start-Sleep -Seconds 5
            continue
        }

        $missing = @()
        foreach ($needle in $Needles) {
            if (-not $dump.Content.Contains($needle)) {
                $missing += $needle
            }
        }

        if ($missing.Count -eq 0) {
            Copy-Item -LiteralPath $dump.Path -Destination (Join-Path $EvidenceDirectory "$Name.xml") -Force
            return $dump
        }

        Start-Sleep -Seconds 2
    }

    if ($lastDump) {
        Copy-Item -LiteralPath $lastDump.Path -Destination (Join-Path $EvidenceDirectory "$Name.xml") -Force
        Assert-UiContains -Content $lastDump.Content -Needles $Needles -StepName $StepName
        return $lastDump
    }

    throw "$StepName 확인 실패. UI 덤프를 가져오지 못했습니다."
}

function Get-NodeCenterByText {
    param(
        [string]$Content,
        [string]$Text,
        [string]$ClassName,
        [int]$MinY = 0
    )

    $escaped = [regex]::Escape($Text)
    $matches = [regex]::Matches($Content, '<node\b[^>]*>')
    foreach ($match in $matches) {
        $node = $match.Value
        if ($node -notmatch "text=`"$escaped`"" -and $node -notmatch "hint=`"$escaped`"") {
            continue
        }
        if (-not [string]::IsNullOrWhiteSpace($ClassName) -and $node -notmatch "class=`"$([regex]::Escape($ClassName))`"") {
            continue
        }
        if ($node -match 'bounds="\[(\d+),(\d+)\]\[(\d+),(\d+)\]"') {
            $x1 = [int]$Matches[1]
            $y1 = [int]$Matches[2]
            $x2 = [int]$Matches[3]
            $y2 = [int]$Matches[4]
            if ($y1 -lt $MinY) {
                continue
            }
            return [pscustomobject]@{
                X = [int](($x1 + $x2) / 2)
                Y = [int](($y1 + $y2) / 2)
            }
        }
    }

    return $null
}

function Tap-Point {
    param(
        [string]$AdbPath,
        [string]$DeviceId,
        [int]$X,
        [int]$Y
    )

    Invoke-Adb -AdbPath $AdbPath -Arguments @('-s', $DeviceId, 'shell', 'input', 'tap', "$X", "$Y") | Out-Null
}

function Set-AndroidTextSlow {
    param(
        [string]$AdbPath,
        [string]$DeviceId,
        [string]$Text
    )

    foreach ($ch in $Text.ToCharArray()) {
        $safeText = ([string]$ch).Replace(' ', '%s')
        Invoke-Adb -AdbPath $AdbPath -Arguments @('-s', $DeviceId, 'shell', 'input', 'text', $safeText) | Out-Null
        Start-Sleep -Milliseconds 60
    }
}

function Clear-AndroidTextField {
    param(
        [string]$AdbPath,
        [string]$DeviceId
    )

    Invoke-Adb -AdbPath $AdbPath -Arguments @('-s', $DeviceId, 'shell', 'input', 'keyevent', 'KEYCODE_MOVE_END') | Out-Null
    for ($i = 0; $i -lt 50; $i++) {
        Invoke-Adb -AdbPath $AdbPath -Arguments @('-s', $DeviceId, 'shell', 'input', 'keyevent', 'KEYCODE_DEL') | Out-Null
    }
}

function Set-LoginTextField {
    param(
        [string]$AdbPath,
        [string]$DeviceId,
        [string]$EvidenceDirectory,
        [string]$Timestamp,
        [string]$FieldName,
        [object]$Point,
        [string]$Value,
        [switch]$VerifyPlainText
    )

    if (-not $Point) {
        throw "login field not found: $FieldName"
    }

    $lastDump = $null
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        Tap-Point -AdbPath $AdbPath -DeviceId $DeviceId -X $Point.X -Y $Point.Y
        Start-Sleep -Milliseconds 700
        Clear-AndroidTextField -AdbPath $AdbPath -DeviceId $DeviceId
        Set-AndroidTextSlow -AdbPath $AdbPath -DeviceId $DeviceId -Text $Value
        Start-Sleep -Milliseconds 700

        $safeFieldName = $FieldName -replace '[^a-zA-Z0-9_-]', '-'
        $lastDump = Get-UiDump -AdbPath $AdbPath -DeviceId $DeviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-smoke-$Timestamp-login-$safeFieldName-attempt$attempt"
        if ($lastDump.Content.Contains("isn't responding")) {
            for ($waitAttempt = 1; $waitAttempt -le 12; $waitAttempt++) {
                Dismiss-AndroidAnrDialog -AdbPath $AdbPath -DeviceId $DeviceId -Content $lastDump.Content | Out-Null
                Start-Sleep -Seconds 5
                $lastDump = Get-UiDump -AdbPath $AdbPath -DeviceId $DeviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-smoke-$Timestamp-login-$safeFieldName-after-anr$attempt-$waitAttempt"
                if (-not $lastDump.Content.Contains("isn't responding")) {
                    break
                }
            }

            $Point = Get-NodeCenterByText -Content $lastDump.Content -Text $FieldName -ClassName 'android.widget.EditText'
            if ($Point) {
                continue
            }
        }

        if (-not $VerifyPlainText -or $lastDump.Content.Contains("text=`"$Value`"")) {
            return $lastDump
        }

        $Point = Get-NodeCenterByText -Content $lastDump.Content -Text $FieldName -ClassName 'android.widget.EditText'
        if (-not $Point) {
            break
        }
    }

    if ($VerifyPlainText) {
        throw "login field value not confirmed: $FieldName"
    }

    return $lastDump
}

function Dismiss-AndroidAnrDialog {
    param(
        [string]$AdbPath,
        [string]$DeviceId,
        [string]$Content
    )

    if (-not $Content.Contains("isn't responding")) {
        return $false
    }

    $buttonText = 'Wait'
    if ($Content.Contains("Pixel Launcher isn't responding") -or
        $Content.Contains('com.google.android.apps.nexuslauncher')) {
        $buttonText = 'Close app'
    }

    $buttonPoint = Get-NodeCenterByText -Content $Content -Text $buttonText -ClassName 'android.widget.Button'
    if (-not $buttonPoint -and $buttonText -ne 'Wait') {
        $buttonPoint = Get-NodeCenterByText -Content $Content -Text 'Wait' -ClassName 'android.widget.Button'
    }
    if (-not $buttonPoint) {
        return $false
    }

    Tap-Point -AdbPath $AdbPath -DeviceId $DeviceId -X $buttonPoint.X -Y $buttonPoint.Y
    if ($buttonText -eq 'Close app' -and -not [string]::IsNullOrWhiteSpace($script:GeoraePlanMobilePackageName)) {
        Start-Sleep -Seconds 2
        Start-MobileApp -AdbPath $AdbPath -DeviceId $DeviceId -PackageName $script:GeoraePlanMobilePackageName
    }
    return $true
}

function Tap-BottomTab {
    param(
        [string]$AdbPath,
        [string]$DeviceId,
        [object]$Screen,
        [double]$XRatio
    )

    $x = [int]($Screen.Width * $XRatio)
    $y = [int]($Screen.Height * 0.95)
    Tap-Point -AdbPath $AdbPath -DeviceId $DeviceId -X $x -Y $y
}

function Open-BottomTabAndAssert {
    param(
        [string]$AdbPath,
        [string]$DeviceId,
        [string]$EvidenceDirectory,
        [string]$Timestamp,
        [object]$Screen,
        [string]$TabText,
        [double]$FallbackXRatio,
        [string]$StepName,
        [string[]]$Needles,
        [System.Collections.Generic.List[object]]$Steps
    )

    Get-UiDump -AdbPath $AdbPath -DeviceId $DeviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-smoke-$Timestamp-before-$StepName" | Out-Null
    Tap-BottomTab -AdbPath $AdbPath -DeviceId $DeviceId -Screen $Screen -XRatio $FallbackXRatio
    Start-Sleep -Seconds 1

    $dump = Wait-UiContainsAll `
        -AdbPath $AdbPath `
        -DeviceId $DeviceId `
        -EvidenceDirectory $EvidenceDirectory `
        -Name "mobile-smoke-$Timestamp-$StepName" `
        -Needles $Needles `
        -StepName $StepName `
        -TimeoutSeconds 60

    $Steps.Add([pscustomobject]@{ Step = $StepName; Result = 'PASS'; Detail = $dump.Path })
    return $dump
}

function Open-HomeActionAndAssert {
    param(
        [string]$AdbPath,
        [string]$DeviceId,
        [string]$EvidenceDirectory,
        [string]$Timestamp,
        [string]$HomeContent,
        [string]$ButtonText,
        [string]$StepName,
        [string[]]$Needles,
        [System.Collections.Generic.List[object]]$Steps
    )

    $point = Get-NodeCenterByText -Content $HomeContent -Text $ButtonText -ClassName 'android.widget.Button'
    if (-not $point) {
        $point = Get-NodeCenterByText -Content $HomeContent -Text $ButtonText -ClassName ''
    }
    if (-not $point) {
        throw "홈 화면에서 '$ButtonText' 버튼을 찾지 못했습니다."
    }

    Tap-Point -AdbPath $AdbPath -DeviceId $DeviceId -X $point.X -Y $point.Y

    $safeStepName = $StepName -replace '[^a-zA-Z0-9_-]', '-'
    $screenDump = Wait-UiContainsAll -AdbPath $AdbPath -DeviceId $DeviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-smoke-$Timestamp-$safeStepName" -Needles $Needles -StepName $StepName
    $Steps.Add([pscustomobject]@{ Step = $StepName; Result = 'PASS'; Detail = $screenDump.Path })

    Invoke-Adb -AdbPath $AdbPath -Arguments @('-s', $DeviceId, 'shell', 'input', 'keyevent', 'KEYCODE_BACK') | Out-Null
    $homeAgain = Wait-UiContainsAll -AdbPath $AdbPath -DeviceId $DeviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-smoke-$Timestamp-after-$safeStepName" -Needles @('홈') -StepName "$StepName 이후 홈 복귀" -TimeoutSeconds 15
    return $homeAgain.Content
}

if ([string]::IsNullOrWhiteSpace($EvidenceDirectory)) {
    $EvidenceDirectory = Join-Path $ProjectRoot '테스트 시행\기록'
}
New-Item -ItemType Directory -Force -Path $EvidenceDirectory | Out-Null

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$resolvedAdb = Resolve-AdbPath -RequestedPath $AdbPath
$resolvedApk = Resolve-ApkPath -ProjectRoot $ProjectRoot -RequestedPath $ApkPath
$deviceId = Get-ConnectedDeviceId -AdbPath $resolvedAdb
$screen = Get-ScreenSize -AdbPath $resolvedAdb -DeviceId $deviceId

$steps = New-Object System.Collections.Generic.List[object]

if (-not $SkipInstall) {
    Install-MobileApk -AdbPath $resolvedAdb -DeviceId $deviceId -ApkPath $resolvedApk -PackageName $PackageName
    $steps.Add([pscustomobject]@{ Step = 'install'; Result = 'PASS'; Detail = $resolvedApk })
}

Invoke-Adb -AdbPath $resolvedAdb -Arguments @('-s', $deviceId, 'shell', 'am', 'force-stop', 'com.google.android.apps.nexuslauncher') | Out-Null
Invoke-Adb -AdbPath $resolvedAdb -Arguments @('-s', $deviceId, 'shell', 'am', 'force-stop', $PackageName) | Out-Null
Start-Sleep -Seconds 1
Start-MobileApp -AdbPath $resolvedAdb -DeviceId $deviceId -PackageName $PackageName
Start-Sleep -Seconds 5

$dump = Wait-UiForAppReady -AdbPath $resolvedAdb -DeviceId $deviceId -EvidenceDirectory $EvidenceDirectory -Timestamp $timestamp -PackageName $PackageName

if ($dump.Content.Contains('계정 로그인') -or ($dump.Content.Contains('로그인') -and $dump.Content.Contains('비밀번호'))) {
    $userPoint = Get-NodeCenterByText -Content $dump.Content -Text '아이디' -ClassName 'android.widget.EditText'
    $passwordPoint = Get-NodeCenterByText -Content $dump.Content -Text '비밀번호' -ClassName 'android.widget.EditText'

    $dump = Set-LoginTextField `
        -AdbPath $resolvedAdb `
        -DeviceId $deviceId `
        -EvidenceDirectory $EvidenceDirectory `
        -Timestamp $timestamp `
        -FieldName '아이디' `
        -Point $userPoint `
        -Value $Username `
        -VerifyPlainText

    $passwordPoint = Get-NodeCenterByText -Content $dump.Content -Text '비밀번호' -ClassName 'android.widget.EditText'
    $dump = Set-LoginTextField `
        -AdbPath $resolvedAdb `
        -DeviceId $deviceId `
        -EvidenceDirectory $EvidenceDirectory `
        -Timestamp $timestamp `
        -FieldName '비밀번호' `
        -Point $passwordPoint `
        -Value $Password

    Invoke-Adb -AdbPath $resolvedAdb -Arguments @('-s', $deviceId, 'shell', 'input', 'keyevent', 'KEYCODE_ESCAPE') | Out-Null
    Start-Sleep -Seconds 1
    $dump = Get-UiDump -AdbPath $resolvedAdb -DeviceId $deviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-smoke-$timestamp-login-ready"
    $loginButtonPoint = Get-NodeCenterByText -Content $dump.Content -Text '로그인' -ClassName 'android.widget.Button'
    if (-not $loginButtonPoint) {
        throw '로그인 버튼을 찾지 못했습니다.'
    }

    Tap-Point -AdbPath $resolvedAdb -DeviceId $deviceId -X $loginButtonPoint.X -Y $loginButtonPoint.Y
    $dump = Wait-UiContainsAll `
        -AdbPath $resolvedAdb `
        -DeviceId $deviceId `
        -EvidenceDirectory $EvidenceDirectory `
        -Name "mobile-smoke-$timestamp-after-login" `
        -Needles @('홈', '판매 작성', '구매 작성', '수금/지급') `
        -StepName '로그인 후 홈 화면' `
        -TimeoutSeconds 150
}

Tap-BottomTab -AdbPath $resolvedAdb -DeviceId $deviceId -Screen $screen -XRatio 0.10

$homeDump = Wait-UiContainsAll `
    -AdbPath $resolvedAdb `
    -DeviceId $deviceId `
    -EvidenceDirectory $EvidenceDirectory `
    -Name "mobile-smoke-$timestamp-home" `
    -Needles @('홈', '판매 작성', '구매 작성', '수금/지급') `
    -StepName '홈 화면' `
    -TimeoutSeconds 60
$steps.Add([pscustomobject]@{ Step = 'home'; Result = 'PASS'; Detail = $homeDump.Path })

if ($IncludeDraftScreens) {
    $currentHomeContent = $homeDump.Content
    $currentHomeContent = Open-HomeActionAndAssert `
        -AdbPath $resolvedAdb `
        -DeviceId $deviceId `
        -EvidenceDirectory $EvidenceDirectory `
        -Timestamp $timestamp `
        -HomeContent $currentHomeContent `
        -ButtonText '판매 작성' `
        -StepName 'sales-draft' `
        -Needles @('판매(매출) 작성', '1단계 · 고객/거래처 찾기', '2단계 · 품목 선택') `
        -Steps $steps

    $currentHomeContent = Open-HomeActionAndAssert `
        -AdbPath $resolvedAdb `
        -DeviceId $deviceId `
        -EvidenceDirectory $EvidenceDirectory `
        -Timestamp $timestamp `
        -HomeContent $currentHomeContent `
        -ButtonText '구매 작성' `
        -StepName 'purchase-draft' `
        -Needles @('구매(매입) 작성', '1단계 · 거래처 찾기', '2단계 · 품목 선택') `
        -Steps $steps

    $currentHomeContent = Open-HomeActionAndAssert `
        -AdbPath $resolvedAdb `
        -DeviceId $deviceId `
        -EvidenceDirectory $EvidenceDirectory `
        -Timestamp $timestamp `
        -HomeContent $currentHomeContent `
        -ButtonText '수금/지급' `
        -StepName 'payment-draft' `
        -Needles @('수금/지급 입력', '전표', '금액') `
        -Steps $steps
}

Open-BottomTabAndAssert `
    -AdbPath $resolvedAdb `
    -DeviceId $deviceId `
    -EvidenceDirectory $EvidenceDirectory `
    -Timestamp $timestamp `
    -Screen $screen `
    -TabText '거래처' `
    -FallbackXRatio 0.30 `
    -StepName 'customers' `
    -Needles @('거래처', '거래처명 / 전화 / 사업자번호') `
    -Steps $steps | Out-Null

Open-BottomTabAndAssert `
    -AdbPath $resolvedAdb `
    -DeviceId $deviceId `
    -EvidenceDirectory $EvidenceDirectory `
    -Timestamp $timestamp `
    -Screen $screen `
    -TabText '품목' `
    -FallbackXRatio 0.50 `
    -StepName 'items' `
    -Needles @('품목 검색', '품목분류') `
    -Steps $steps | Out-Null

Open-BottomTabAndAssert `
    -AdbPath $resolvedAdb `
    -DeviceId $deviceId `
    -EvidenceDirectory $EvidenceDirectory `
    -Timestamp $timestamp `
    -Screen $screen `
    -TabText '전표' `
    -FallbackXRatio 0.70 `
    -StepName 'invoices' `
    -Needles @('전표', '판매 작성', '구매 작성', '수금/지급') `
    -Steps $steps | Out-Null

$result = [pscustomobject]@{
    CreatedAt = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    PackageName = $PackageName
    DeviceId = $deviceId
    ApkPath = $resolvedApk
    Result = 'PASS'
    Steps = $steps
}

$jsonPath = Join-Path $EvidenceDirectory "mobile-smoke-$timestamp.json"
$mdPath = Join-Path $EvidenceDirectory "mobile-smoke-$timestamp.md"
$result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

$mdLines = @(
    '# 모바일 Android Smoke 검증',
    '',
    "- 작성시각: $($result.CreatedAt)",
    "- 기기: $deviceId",
    "- 패키지: $PackageName",
    "- APK: $resolvedApk",
    "- 결과: PASS",
    '',
    '## 단계',
    ''
)
foreach ($step in $steps) {
    $mdLines += "- $($step.Step): $($step.Result) — $($step.Detail)"
}
$mdLines += ''
$mdLines += "JSON: $jsonPath"
$mdLines | Set-Content -LiteralPath $mdPath -Encoding UTF8

Write-Host "mobile_smoke_report=$mdPath"
Write-Host "mobile_smoke_json=$jsonPath"
Write-Host 'result=PASS'
