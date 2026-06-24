param(
    [string]$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [string]$BaseUrl = 'http://127.0.0.1:19080',
    [string]$AdbPath,
    [string]$ApkPath,
    [string]$PackageName = 'kr.georaeplan.mobile',
    [ValidateSet('Sales', 'Purchase')]
    [string]$VoucherKind = 'Sales',
    [string]$Username = 'usenet',
    [string]$Password = '1234',
    [string]$EvidenceDirectory,
    [switch]$SkipInstall,
    [switch]$KeepTemporaryData,
    [switch]$ExerciseStoppedServerDirtySync,
    [ValidateSet('', '400', '403', '404', '422')]
    [string]$ExerciseNonRetryableSaveFaultStatus = '',
    [switch]$ExerciseAttachmentUpload,
    [switch]$ExerciseCameraAttachmentUpload,
    [switch]$ExerciseAttachmentOpenUi,
    [switch]$ExerciseAttachmentListFallback,
    [string]$DotNetPath,
    [string]$LocalApiProjectFile,
    [int]$StoppedServerOfflineTimeoutSeconds = 45
)

$ErrorActionPreference = 'Stop'
$script:GeoraePlanMobilePackageName = $PackageName

if ($ExerciseAttachmentUpload -and $ExerciseCameraAttachmentUpload) {
    throw 'Run either PDF attachment E2E or camera attachment E2E, not both.'
}
if ($ExerciseAttachmentOpenUi -and -not ($ExerciseAttachmentUpload -or $ExerciseCameraAttachmentUpload)) {
    throw 'Attachment open UI E2E requires either PDF attachment E2E or camera attachment E2E.'
}
if ($ExerciseAttachmentListFallback -and -not $ExerciseAttachmentOpenUi) {
    throw 'Attachment list fallback E2E requires attachment open UI E2E.'
}

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

function Assert-LocalDirtySyncTarget {
    param([string]$BaseUrl)

    $uri = $null
    if (-not [Uri]::TryCreate($BaseUrl, [UriKind]::Absolute, [ref]$uri)) {
        throw "오프라인 dirty 동기화 검증 BaseUrl이 올바른 URI가 아닙니다: $BaseUrl"
    }

    $isLoopbackHost = [string]::Equals($uri.Host, '127.0.0.1', [StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($uri.Host, 'localhost', [StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($uri.Host, '::1', [StringComparison]::OrdinalIgnoreCase)

    if (-not $isLoopbackHost) {
        throw "오프라인 dirty 동기화 검증은 로컬 테스트 API에서만 허용됩니다. 현재 BaseUrl: $BaseUrl"
    }
}

function Resolve-DotNetPath {
    param(
        [string]$ProjectRoot,
        [string]$RequestedPath
    )

    foreach ($candidate in @(
        $RequestedPath,
        (Join-Path $ProjectRoot '.dotnet\dotnet.exe'),
        (Join-Path $env:LOCALAPPDATA 'GeoraePlan.Android\dotnet8\dotnet.exe')
    )) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path -LiteralPath $candidate)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    throw 'dotnet 실행 파일을 찾지 못했습니다.'
}

function Wait-LocalApiHealth {
    param(
        [string]$BaseUrl,
        [int]$TimeoutSeconds = 90
    )

    $healthUrl = $BaseUrl.TrimEnd('/') + '/healthz'
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $lastError = $null
    while ((Get-Date) -lt $deadline) {
        try {
            $response = Invoke-WebRequest -Uri $healthUrl -UseBasicParsing -TimeoutSec 2
            if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 600) {
                return
            }
        }
        catch {
            $lastError = $_.Exception.Message
            Start-Sleep -Seconds 2
        }
    }

    throw "로컬 테스트 API healthz 대기 실패: $healthUrl / $lastError"
}

function Stop-LocalApiForBaseUrl {
    param(
        [string]$BaseUrl,
        [string]$ProjectRoot
    )

    $uri = [Uri]$BaseUrl
    $connections = @(Get-NetTCPConnection -LocalPort $uri.Port -State Listen -ErrorAction SilentlyContinue)
    $owners = @($connections | Select-Object -ExpandProperty OwningProcess -Unique | Where-Object { $_ -gt 0 })
    if ($owners.Count -eq 0) {
        throw "중단할 로컬 테스트 API 프로세스를 찾지 못했습니다: $BaseUrl"
    }

    $stopped = New-Object System.Collections.Generic.List[string]
    foreach ($ownerProcessId in $owners) {
        $process = Get-Process -Id $ownerProcessId -ErrorAction Stop
        $processPath = ''
        try { $processPath = [string]$process.Path } catch { $processPath = '' }

        $isProjectProcess = -not [string]::IsNullOrWhiteSpace($processPath) -and
            $processPath.StartsWith($ProjectRoot, [StringComparison]::OrdinalIgnoreCase)
        $isGeoraePlanProcess = $process.ProcessName.Contains('거래플랜') -or
            $process.ProcessName.Contains('GeoraePlan') -or
            $processPath.Contains('거래플랜') -or
            $processPath.Contains('GeoraePlan')

        if (-not ($isProjectProcess -or $isGeoraePlanProcess)) {
            throw "로컬 테스트 API가 아닌 프로세스는 중단하지 않습니다: pid=$ownerProcessId, name=$($process.ProcessName), path=$processPath"
        }

        Stop-Process -Id $ownerProcessId -Force -ErrorAction Stop
        $stopped.Add("$($process.ProcessName):$ownerProcessId") | Out-Null
    }

    Start-Sleep -Seconds 3
    return @($stopped)
}

function Start-LocalApiForBaseUrl {
    param(
        [string]$BaseUrl,
        [string]$ProjectRoot,
        [string]$DotNetPath,
        [string]$LocalApiProjectFile,
        [string]$Username,
        [string]$Password,
        [string]$EvidenceDirectory,
        [string]$Timestamp
    )

    $resolvedDotNet = Resolve-DotNetPath -ProjectRoot $ProjectRoot -RequestedPath $DotNetPath
    if ([string]::IsNullOrWhiteSpace($LocalApiProjectFile)) {
        $LocalApiProjectFile = Join-Path $ProjectRoot 'Server\거래플랜.Server.Api\거래플랜.Server.Api.csproj'
    }
    if (-not (Test-Path -LiteralPath $LocalApiProjectFile)) {
        throw "로컬 테스트 API 프로젝트 파일을 찾지 못했습니다: $LocalApiProjectFile"
    }

    $serverDirectory = Split-Path -Parent (Resolve-Path -LiteralPath $LocalApiProjectFile)
    $safeBaseUrl = $BaseUrl.TrimEnd('/')
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:ASPNETCORE_URLS = $safeBaseUrl
    $env:Kestrel__Endpoints__Http__Url = $safeBaseUrl
    $env:Security__RequireHttpsForwardedProto = 'false'
    $env:Database__EnableSqliteFallback = 'true'
    $env:SeedUsers__EnableSeedUsers = 'true'
    if ([string]::IsNullOrWhiteSpace($env:SeedUsers__UsenetUsername)) {
        $env:SeedUsers__UsenetUsername = $Username
    }
    if ([string]::IsNullOrWhiteSpace($env:SeedUsers__UsenetPassword)) {
        $env:SeedUsers__UsenetPassword = $Password
    }
    $env:SeedUsers__UpdateExistingUsenetPassword = 'true'

    $restartStamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $stdout = Join-Path $EvidenceDirectory "mobile-payment-e2e-$Timestamp-local-api-restart-$restartStamp.out.log"
    $stderr = Join-Path $EvidenceDirectory "mobile-payment-e2e-$Timestamp-local-api-restart-$restartStamp.err.log"
    $process = Start-Process `
        -FilePath $resolvedDotNet `
        -ArgumentList @('run', '--no-launch-profile', '--project', $LocalApiProjectFile) `
        -WorkingDirectory $serverDirectory `
        -RedirectStandardOutput $stdout `
        -RedirectStandardError $stderr `
        -WindowStyle Hidden `
        -PassThru

    Wait-LocalApiHealth -BaseUrl $safeBaseUrl -TimeoutSeconds 90

    return [pscustomobject]@{
        ProcessId = $process.Id
        StdOut = $stdout
        StdErr = $stderr
    }
}

function Invoke-Adb {
    param(
        [Parameter(Mandatory = $true)][string]$AdbPath,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $lastOutput = $null
    $lastExitCode = 0
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        $previousErrorActionPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $lastOutput = & $AdbPath @Arguments 2>&1
            $lastExitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }

        if ($lastExitCode -eq 0) {
            return $lastOutput
        }

        $joinedOutput = [string]::Join("`n", @($lastOutput))
        $isDaemonConnectionFailure = $joinedOutput -match 'daemon still not running|cannot connect to daemon|cannot connect to 127\.0\.0\.1:5037|10060|actively refused|Connection refused'
        if (-not $isDaemonConnectionFailure -or $attempt -eq 3) {
            break
        }

        try { & $AdbPath start-server | Out-Null } catch {}
        Start-Sleep -Seconds ([Math]::Min(2 * $attempt, 6))
    }

    throw "adb failed after retry: adb $($Arguments -join ' ')`n$lastOutput"
}

function Set-MobileDiagnosticFault {
    param(
        [string]$AdbPath,
        [string]$DeviceId,
        [string]$PackageName,
        [ValidateSet('NETWORK', '400', '401', '403', '404', '422', '500')]
        [string]$Mode = 'NETWORK',
        [string]$Target
    )

    if ([string]::IsNullOrWhiteSpace($Target)) {
        throw 'Mobile diagnostic fault target endpoint is empty.'
    }

    $normalizedMode = $Mode.Trim().ToUpperInvariant()
    $script = "mkdir -p files/diagnostics && printf $normalizedMode\|$Target > files/diagnostics/next-fault.txt && cat files/diagnostics/next-fault.txt"
    $quotedScript = "'$script'"
    $output = Invoke-Adb -AdbPath $AdbPath -Arguments @('-s', $DeviceId, 'shell', 'run-as', $PackageName, 'sh', '-c', $quotedScript)
    if (($output -join "`n") -notmatch "$normalizedMode\|$([regex]::Escape($Target))") {
        throw "Mobile diagnostic fault setup failed($normalizedMode): $($output -join ' ')"
    }
}

function Set-MobileDiagnosticNetworkFault {
    param(
        [string]$AdbPath,
        [string]$DeviceId,
        [string]$PackageName,
        [string]$Target
    )

    Set-MobileDiagnosticFault -AdbPath $AdbPath -DeviceId $DeviceId -PackageName $PackageName -Mode 'NETWORK' -Target $Target
}
function Install-MobileApk {
    param(
        [Parameter(Mandatory = $true)][string]$AdbPath,
        [Parameter(Mandatory = $true)][string]$DeviceId,
        [Parameter(Mandatory = $true)][string]$ApkPath,
        [Parameter(Mandatory = $true)][string]$PackageName
    )

    $installArgs = @('-s', $DeviceId, 'install', '-r', '-d', $ApkPath)

    try { Invoke-Adb -AdbPath $AdbPath -Arguments @('-s', $DeviceId, 'shell', 'pm', 'trim-caches', '1024M') | Out-Null } catch {}

    try {
        Invoke-Adb -AdbPath $AdbPath -Arguments $installArgs | Out-Null
    }
    catch {
        $message = $_.Exception.Message
        if ($message -match 'INSTALL_FAILED_INSUFFICIENT_STORAGE') {
            try { Invoke-Adb -AdbPath $AdbPath -Arguments @('-s', $DeviceId, 'shell', 'pm', 'trim-caches', '1024M') | Out-Null } catch {}
            try { Invoke-Adb -AdbPath $AdbPath -Arguments @('-s', $DeviceId, 'uninstall', $PackageName) | Out-Null } catch {}
            Invoke-Adb -AdbPath $AdbPath -Arguments $installArgs | Out-Null
            return
        }

        if ($message -notmatch 'INSTALL_FAILED_UPDATE_INCOMPATIBLE|INSTALL_FAILED_VERSION_DOWNGRADE|Downgrade detected|signatures do not match') {
            throw
        }

        Invoke-Adb -AdbPath $AdbPath -Arguments @('-s', $DeviceId, 'uninstall', $PackageName) | Out-Null
        Invoke-Adb -AdbPath $AdbPath -Arguments $installArgs | Out-Null
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
        throw "Android launcher activity not found: $PackageName"
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

    function Convert-ToValidHierarchyDump {
        param([string]$Candidate)

        if ([string]::IsNullOrWhiteSpace($Candidate)) {
            return $null
        }

        $start = $Candidate.IndexOf('<hierarchy', [StringComparison]::Ordinal)
        if ($start -lt 0) {
            return $null
        }

        $content = $Candidate.Substring($start)
        $end = $content.LastIndexOf('</hierarchy>', [StringComparison]::Ordinal)
        if ($end -ge 0) {
            $content = $content.Substring(0, $end + '</hierarchy>'.Length)
        }

        if ($content.Contains('<hierarchy')) {
            return $content
        }

        return $null
    }

    $lastError = $null
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            Invoke-Adb -AdbPath $AdbPath -Arguments @('-s', $DeviceId, 'shell', 'uiautomator', 'dump', $remote) | Out-Null
            Invoke-Adb -AdbPath $AdbPath -Arguments @('-s', $DeviceId, 'pull', $remote, $local) | Out-Null
            $content = Get-Content -LiteralPath $local -Raw -Encoding UTF8
            $validContent = Convert-ToValidHierarchyDump -Candidate $content
            if ($null -ne $validContent) {
                [System.IO.File]::WriteAllText($local, $validContent, [System.Text.UTF8Encoding]::new($false))
                return [pscustomobject]@{ Path = $local; Content = $validContent }
            }
        }
        catch {
            $lastError = $_
            Start-Sleep -Seconds 1
        }

        try {
            # 일부 에뮬레이터에서는 /sdcard 파일 덤프가 UiAutomation 연결 타임아웃을 유발합니다.
            # /dev/tty로 직접 덤프한 XML을 보조 경로로 저장해 검증 스크립트가 중단되지 않게 합니다.
            $raw = Invoke-Adb -AdbPath $AdbPath -Arguments @('-s', $DeviceId, 'exec-out', 'uiautomator', 'dump', '/dev/tty')
            $content = ($raw -join "`n")
            $validContent = Convert-ToValidHierarchyDump -Candidate $content
            if ($null -ne $validContent) {
                [System.IO.File]::WriteAllText($local, $validContent, [System.Text.UTF8Encoding]::new($false))
                return [pscustomobject]@{ Path = $local; Content = $validContent }
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

function Wait-UiReadyForLoginOrHome {
    param(
        [string]$AdbPath,
        [string]$DeviceId,
        [string]$EvidenceDirectory,
        [string]$Name,
        [int]$TimeoutSeconds = 150
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

        $isLogin = $dump.Content.Contains('계정 로그인') -or ($dump.Content.Contains('로그인') -and $dump.Content.Contains('비밀번호'))
        $isHome = $dump.Content.Contains('빠른 안내') -and $dump.Content.Contains('판매 작성') -and $dump.Content.Contains('구매 작성') -and $dump.Content.Contains('수금/지급')
        if ($isLogin -or $isHome) {
            Copy-Item -LiteralPath $dump.Path -Destination (Join-Path $EvidenceDirectory "$Name.xml") -Force
            return $dump
        }

        Start-Sleep -Seconds 2
    }

    if ($lastDump) {
        Copy-Item -LiteralPath $lastDump.Path -Destination (Join-Path $EvidenceDirectory "$Name.xml") -Force
    }

    throw 'Login or home screen was not reached.'
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


function Get-NodeCenterByAttribute {
    param(
        [string]$Content,
        [string]$Attribute,
        [string]$Value,
        [string]$ClassName = '',
        [int]$MinY = 0
    )

    $escapedAttribute = [regex]::Escape($Attribute)
    $escapedValue = [regex]::Escape($Value)
    $matches = [regex]::Matches($Content, '<node\b[^>]*>')
    foreach ($match in $matches) {
        $node = $match.Value
        if ($node -notmatch "$escapedAttribute=`"$escapedValue`"") {
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

function Tap-UiAttribute {
    param(
        [string]$AdbPath,
        [string]$DeviceId,
        [string]$Content,
        [string]$Attribute,
        [string]$Value,
        [string]$StepName,
        [string]$ClassName = '',
        [int]$MinY = 0
    )

    $point = Get-NodeCenterByAttribute -Content $Content -Attribute $Attribute -Value $Value -ClassName $ClassName -MinY $MinY
    if (-not $point -and -not [string]::IsNullOrWhiteSpace($ClassName)) {
        $point = Get-NodeCenterByAttribute -Content $Content -Attribute $Attribute -Value $Value -ClassName '' -MinY $MinY
    }
    if (-not $point) {
        throw "$StepName failed. Could not find node by $Attribute=$Value."
    }

    Tap-Point -AdbPath $AdbPath -DeviceId $DeviceId -X $point.X -Y $point.Y
}

function Grant-CameraPermissionIfPossible {
    param(
        [string]$AdbPath,
        [string]$DeviceId,
        [string]$PackageName
    )

    try {
        Invoke-Adb -AdbPath $AdbPath -Arguments @('-s', $DeviceId, 'shell', 'pm', 'grant', $PackageName, 'android.permission.CAMERA') | Out-Null
    }
    catch {
        # 일부 Android 버전/상태에서는 이미 권한이 있거나 grant가 제한될 수 있습니다. 실제 권한 다이얼로그는 후속 UI 처리에서 다시 대응합니다.
    }
}

function Dismiss-CameraPermissionDialogIfPresent {
    param(
        [string]$AdbPath,
        [string]$DeviceId,
        [string]$Content
    )

    $allowCandidates = @('While using the app', 'Only this time', '앱 사용 중에만 허용', '허용', 'Allow')
    foreach ($text in $allowCandidates) {
        $point = Get-NodeCenterByText -Content $Content -Text $text -ClassName 'android.widget.Button'
        if (-not $point) {
            $point = Get-NodeCenterByText -Content $Content -Text $text -ClassName ''
        }
        if ($point) {
            Tap-Point -AdbPath $AdbPath -DeviceId $DeviceId -X $point.X -Y $point.Y
            return $true
        }
    }

    return $false
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

function Tap-UiText {
    param(
        [string]$AdbPath,
        [string]$DeviceId,
        [string]$Content,
        [string]$Text,
        [string]$StepName,
        [string]$ClassName = '',
        [int]$MinY = 0
    )

    $point = Get-NodeCenterByText -Content $Content -Text $Text -ClassName $ClassName -MinY $MinY
    if (-not $point -and -not [string]::IsNullOrWhiteSpace($ClassName)) {
        $point = Get-NodeCenterByText -Content $Content -Text $Text -ClassName '' -MinY $MinY
    }
    if (-not $point) {
        throw "$StepName 실패. '$Text' 위치를 찾지 못했습니다."
    }

    Tap-Point -AdbPath $AdbPath -DeviceId $DeviceId -X $point.X -Y $point.Y
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

    Get-UiDump -AdbPath $AdbPath -DeviceId $DeviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-payment-e2e-$Timestamp-before-$StepName" | Out-Null
    Tap-BottomTab -AdbPath $AdbPath -DeviceId $DeviceId -Screen $Screen -XRatio $FallbackXRatio
    Start-Sleep -Seconds 1

    $afterTapDump = Get-UiDump -AdbPath $AdbPath -DeviceId $DeviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-payment-e2e-$Timestamp-after-tap-$StepName"
    $missingAfterTap = @()
    foreach ($needle in $Needles) {
        if (-not $afterTapDump.Content.Contains($needle)) {
            $missingAfterTap += $needle
        }
    }

    if ($missingAfterTap.Count -gt 0 -and
        ($afterTapDump.Content.Contains('design_bottom_sheet') -or $afterTapDump.Content.Contains('touch_outside'))) {
        $tabPoint = Get-NodeCenterByText -Content $afterTapDump.Content -Text $TabText -ClassName 'android.widget.TextView'
        if (-not $tabPoint) {
            $tabPoint = Get-NodeCenterByText -Content $afterTapDump.Content -Text $TabText -ClassName ''
        }

        if ($tabPoint) {
            Tap-Point -AdbPath $AdbPath -DeviceId $DeviceId -X $tabPoint.X -Y $tabPoint.Y
            Start-Sleep -Seconds 1
        }
    }

    $dump = Wait-UiContainsAll `
        -AdbPath $AdbPath `
        -DeviceId $DeviceId `
        -EvidenceDirectory $EvidenceDirectory `
        -Name "mobile-payment-e2e-$Timestamp-$StepName" `
        -Needles $Needles `
        -StepName $StepName `
        -TimeoutSeconds 90

    $Steps.Add([pscustomobject]@{ Step = $StepName; Result = 'PASS'; Detail = $dump.Path })
    return $dump
}

function Invoke-SyncNowAndAssert {
    param(
        [string]$AdbPath,
        [string]$DeviceId,
        [string]$EvidenceDirectory,
        [string]$Timestamp,
        [string]$SyncContent,
        [System.Collections.Generic.List[object]]$Steps
    )

    $point = Get-NodeCenterByText -Content $SyncContent -Text '권장 동기화 실행' -ClassName 'android.widget.Button'
    if (-not $point) {
        $point = Get-NodeCenterByText -Content $SyncContent -Text '권장 동기화 실행' -ClassName ''
    }
    if (-not $point) {
        $freshDump = Get-UiDump -AdbPath $AdbPath -DeviceId $DeviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-payment-e2e-$Timestamp-sync-now-before-tap"
        $point = Get-NodeCenterByText -Content $freshDump.Content -Text '권장 동기화 실행' -ClassName 'android.widget.Button'
        if (-not $point) {
            $point = Get-NodeCenterByText -Content $freshDump.Content -Text '권장 동기화 실행' -ClassName ''
        }
    }
    if (-not $point) {
        throw '동기화 화면에서 권장 동기화 실행 버튼을 찾지 못했습니다.'
    }

    Tap-Point -AdbPath $AdbPath -DeviceId $DeviceId -X $point.X -Y $point.Y
    $dump = Wait-UiContainsAll `
        -AdbPath $AdbPath `
        -DeviceId $DeviceId `
        -EvidenceDirectory $EvidenceDirectory `
        -Name "mobile-payment-e2e-$Timestamp-sync-now" `
        -Needles @('권장 동기화 완료', '수금·지급 0건', '서버에서 받기', '서버에 올리기') `
        -StepName 'sync-now' `
        -TimeoutSeconds 150

    $Steps.Add([pscustomobject]@{ Step = 'sync-now'; Result = 'PASS'; Detail = $dump.Path })
    return $dump
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

function Set-AndroidText {
    param(
        [string]$AdbPath,
        [string]$DeviceId,
        [string]$Text
    )

    Invoke-Adb -AdbPath $AdbPath -Arguments @('-s', $DeviceId, 'shell', 'input', 'keyevent', 'KEYCODE_MOVE_END') | Out-Null
    for ($i = 0; $i -lt 80; $i++) {
        Invoke-Adb -AdbPath $AdbPath -Arguments @('-s', $DeviceId, 'shell', 'input', 'keyevent', 'KEYCODE_DEL') | Out-Null
    }

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
        Set-AndroidText -AdbPath $AdbPath -DeviceId $DeviceId -Text $Value
        Start-Sleep -Milliseconds 700

        $safeFieldName = $FieldName -replace '[^a-zA-Z0-9_-]', '-'
        $lastDump = Get-UiDump -AdbPath $AdbPath -DeviceId $DeviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-payment-e2e-$Timestamp-login-$safeFieldName-attempt$attempt"
        if ($lastDump.Content.Contains("isn't responding")) {
            for ($waitAttempt = 1; $waitAttempt -le 12; $waitAttempt++) {
                Dismiss-AndroidAnrDialog -AdbPath $AdbPath -DeviceId $DeviceId -Content $lastDump.Content | Out-Null
                Start-Sleep -Seconds 5
                $lastDump = Get-UiDump -AdbPath $AdbPath -DeviceId $DeviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-payment-e2e-$Timestamp-login-$safeFieldName-after-anr$attempt-$waitAttempt"
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

function New-TestAttachmentPdf {
    param(
        [string]$EvidenceDirectory,
        [string]$Timestamp
    )

    $fileName = "georaeplan-payment-e2e-$Timestamp.pdf"
    $path = Join-Path $EvidenceDirectory $fileName
    $content = @"
%PDF-1.4
1 0 obj
<< /Type /Catalog /Pages 2 0 R >>
endobj
2 0 obj
<< /Type /Pages /Kids [3 0 R] /Count 1 >>
endobj
3 0 obj
<< /Type /Page /Parent 2 0 R /MediaBox [0 0 240 120] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>
endobj
4 0 obj
<< /Length 55 >>
stream
BT /F1 12 Tf 24 72 Td (GeoraePlan payment E2E) Tj ET
endstream
endobj
5 0 obj
<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>
endobj
xref
0 6
0000000000 65535 f
0000000009 00000 n
0000000058 00000 n
0000000115 00000 n
0000000234 00000 n
0000000340 00000 n
trailer
<< /Size 6 /Root 1 0 R >>
startxref
410
%%EOF
"@
    [System.IO.File]::WriteAllText($path, $content, [System.Text.UTF8Encoding]::new($false))
    return [pscustomobject]@{ LocalPath = $path; FileName = $fileName; RemotePath = "/sdcard/Download/$fileName" }
}

function Push-TestAttachmentToDevice {
    param(
        [string]$AdbPath,
        [string]$DeviceId,
        [object]$Attachment
    )

    Invoke-Adb -AdbPath $AdbPath -Arguments @('-s', $DeviceId, 'shell', 'mkdir', '-p', '/sdcard/Download') | Out-Null
    Invoke-Adb -AdbPath $AdbPath -Arguments @('-s', $DeviceId, 'push', $Attachment.LocalPath, $Attachment.RemotePath) | Out-Null
    try {
        Invoke-Adb -AdbPath $AdbPath -Arguments @('-s', $DeviceId, 'shell', 'am', 'broadcast', '-a', 'android.intent.action.MEDIA_SCANNER_SCAN_FILE', '-d', "file://$($Attachment.RemotePath)") | Out-Null
    }
    catch {
        # Android 버전에 따라 media scanner broadcast가 제한될 수 있습니다. 파일 선택기 Recent/Downloads 노출 보조용이므로 실패해도 계속합니다.
    }
}

function Select-PdfAttachmentFromDevice {
    param(
        [string]$AdbPath,
        [string]$DeviceId,
        [string]$EvidenceDirectory,
        [string]$Timestamp,
        [object]$Attachment
    )

    $attachmentButtonPoint = $null
    $paymentDump = $null
    for ($scrollAttempt = 0; $scrollAttempt -lt 6; $scrollAttempt++) {
        $paymentDump = Get-UiDump -AdbPath $AdbPath -DeviceId $DeviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-payment-e2e-$Timestamp-attachment-button-$scrollAttempt"
        $attachmentButtonPoint = Get-NodeCenterByText -Content $paymentDump.Content -Text '내역 첨부하기' -ClassName 'android.widget.Button'
        if (-not $attachmentButtonPoint) {
            $attachmentButtonPoint = Get-NodeCenterByText -Content $paymentDump.Content -Text '내역 첨부하기' -ClassName ''
        }
        if ($attachmentButtonPoint) {
            break
        }

        Invoke-Adb -AdbPath $AdbPath -Arguments @('-s', $DeviceId, 'shell', 'input', 'swipe', '540', '2050', '540', '900', '700') | Out-Null
        Start-Sleep -Seconds 2
    }
    if (-not $attachmentButtonPoint) {
        throw '수금/지급 입력 화면에서 내역 첨부하기 버튼을 찾지 못했습니다.'
    }

    Tap-Point -AdbPath $AdbPath -DeviceId $DeviceId -X $attachmentButtonPoint.X -Y $attachmentButtonPoint.Y
    $sheetDump = Wait-UiContainsAll `
        -AdbPath $AdbPath `
        -DeviceId $DeviceId `
        -EvidenceDirectory $EvidenceDirectory `
        -Name "mobile-payment-e2e-$Timestamp-attachment-action-sheet" `
        -Needles @('첨부 방식 선택', 'PDF 파일 업로드') `
        -StepName '첨부 방식 선택' `
        -TimeoutSeconds 45
    Tap-UiText -AdbPath $AdbPath -DeviceId $DeviceId -Content $sheetDump.Content -Text 'PDF 파일 업로드' -ClassName 'android.widget.TextView' -StepName 'PDF 첨부 방식 선택'
    Start-Sleep -Seconds 4

    $pickerDump = $null
    for ($attempt = 1; $attempt -le 12; $attempt++) {
        $pickerDump = Get-UiDump -AdbPath $AdbPath -DeviceId $DeviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-payment-e2e-$Timestamp-file-picker-$attempt"
        if ($pickerDump.Content.Contains($Attachment.FileName)) {
            Tap-UiText -AdbPath $AdbPath -DeviceId $DeviceId -Content $pickerDump.Content -Text $Attachment.FileName -ClassName 'android.widget.TextView' -StepName 'PDF 첨부 파일 선택'
            Start-Sleep -Seconds 5
            $selectedDump = Wait-UiContainsAll `
                -AdbPath $AdbPath `
                -DeviceId $DeviceId `
                -EvidenceDirectory $EvidenceDirectory `
                -Name "mobile-payment-e2e-$Timestamp-attachment-selected" `
                -Needles @($Attachment.FileName, '첨부 1건') `
                -StepName 'PDF 첨부 선택 완료' `
                -TimeoutSeconds 60
            return $selectedDump
        }

        if ($pickerDump.Content.Contains('Download') -or $pickerDump.Content.Contains('Downloads') -or $pickerDump.Content.Contains('다운로드')) {
            Invoke-Adb -AdbPath $AdbPath -Arguments @('-s', $DeviceId, 'shell', 'input', 'swipe', '540', '1900', '540', '650', '500') | Out-Null
        }
        else {
            Invoke-Adb -AdbPath $AdbPath -Arguments @('-s', $DeviceId, 'shell', 'input', 'keyevent', 'KEYCODE_SEARCH') | Out-Null
            Start-Sleep -Seconds 1
            Set-AndroidText -AdbPath $AdbPath -DeviceId $DeviceId -Text $Attachment.FileName
        }
        Start-Sleep -Seconds 2
    }

    if ($pickerDump) {
        Copy-Item -LiteralPath $pickerDump.Path -Destination (Join-Path $EvidenceDirectory "mobile-payment-e2e-$Timestamp-file-picker-not-found.xml") -Force
    }
    throw "Android 파일 선택기에서 테스트 PDF를 찾지 못했습니다: $($Attachment.FileName)"
}


function Select-CameraAttachmentFromDevice {
    param(
        [string]$AdbPath,
        [string]$DeviceId,
        [string]$EvidenceDirectory,
        [string]$Timestamp,
        [object]$Screen
    )

    $attachmentButtonPoint = $null
    $paymentDump = $null
    for ($scrollAttempt = 0; $scrollAttempt -lt 6; $scrollAttempt++) {
        $paymentDump = Get-UiDump -AdbPath $AdbPath -DeviceId $DeviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-payment-e2e-$Timestamp-camera-attachment-button-$scrollAttempt"
        $attachmentButtonPoint = Get-NodeCenterByText -Content $paymentDump.Content -Text '내역 첨부하기' -ClassName 'android.widget.Button'
        if (-not $attachmentButtonPoint) {
            $attachmentButtonPoint = Get-NodeCenterByText -Content $paymentDump.Content -Text '내역 첨부하기' -ClassName ''
        }
        if ($attachmentButtonPoint) {
            break
        }

        Invoke-Adb -AdbPath $AdbPath -Arguments @('-s', $DeviceId, 'shell', 'input', 'swipe', '540', '2050', '540', '900', '700') | Out-Null
        Start-Sleep -Seconds 2
    }
    if (-not $attachmentButtonPoint) {
        throw 'Payment draft attachment button was not found.'
    }

    Tap-Point -AdbPath $AdbPath -DeviceId $DeviceId -X $attachmentButtonPoint.X -Y $attachmentButtonPoint.Y
    $sheetDump = Wait-UiContainsAll `
        -AdbPath $AdbPath `
        -DeviceId $DeviceId `
        -EvidenceDirectory $EvidenceDirectory `
        -Name "mobile-payment-e2e-$Timestamp-camera-attachment-action-sheet" `
        -Needles @('첨부 방식 선택', '카메라 촬영 이미지 업로드') `
        -StepName '카메라 촬영 이미지 업로드' `
        -TimeoutSeconds 45
    Tap-UiText -AdbPath $AdbPath -DeviceId $DeviceId -Content $sheetDump.Content -Text '카메라 촬영 이미지 업로드' -ClassName 'android.widget.TextView' -StepName '카메라 촬영 이미지 업로드'
    Start-Sleep -Seconds 4

    $cameraDump = $null
    for ($attempt = 1; $attempt -le 20; $attempt++) {
        $cameraDump = Get-UiDump -AdbPath $AdbPath -DeviceId $DeviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-payment-e2e-$Timestamp-camera-open-$attempt"
        if (Dismiss-CameraPermissionDialogIfPresent -AdbPath $AdbPath -DeviceId $DeviceId -Content $cameraDump.Content) {
            Start-Sleep -Seconds 3
            continue
        }

        if ($cameraDump.Content.Contains('Shutter') -or $cameraDump.Content.Contains('shutter_button')) {
            break
        }

        Start-Sleep -Seconds 2
    }

    if (-not $cameraDump -or (-not $cameraDump.Content.Contains('Shutter') -and -not $cameraDump.Content.Contains('shutter_button'))) {
        throw 'Android camera shutter button was not found.'
    }

    $shutterPoint = Get-NodeCenterByAttribute -Content $cameraDump.Content -Attribute 'content-desc' -Value 'Shutter' -ClassName ''
    if (-not $shutterPoint) {
        $shutterPoint = Get-NodeCenterByAttribute -Content $cameraDump.Content -Attribute 'resource-id' -Value 'com.android.camera2:id/shutter_button' -ClassName ''
    }
    if (-not $shutterPoint) {
        $shutterPoint = [pscustomobject]@{ X = [int]($Screen.Width * 0.5); Y = [int]($Screen.Height * 0.9) }
    }

    Tap-Point -AdbPath $AdbPath -DeviceId $DeviceId -X $shutterPoint.X -Y $shutterPoint.Y
    Start-Sleep -Seconds 5

    $reviewDump = $null
    for ($attempt = 1; $attempt -le 20; $attempt++) {
        $reviewDump = Get-UiDump -AdbPath $AdbPath -DeviceId $DeviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-payment-e2e-$Timestamp-camera-review-$attempt"
        if ($reviewDump.Content.Contains('Done') -or $reviewDump.Content.Contains('done_button')) {
            break
        }
        Start-Sleep -Seconds 2
    }

    if (-not $reviewDump -or (-not $reviewDump.Content.Contains('Done') -and -not $reviewDump.Content.Contains('done_button'))) {
        throw 'Android camera done button was not found after capture.'
    }

    $donePoint = Get-NodeCenterByAttribute -Content $reviewDump.Content -Attribute 'content-desc' -Value 'Done' -ClassName ''
    if (-not $donePoint) {
        $donePoint = Get-NodeCenterByAttribute -Content $reviewDump.Content -Attribute 'resource-id' -Value 'com.android.camera2:id/done_button' -ClassName ''
    }
    if (-not $donePoint) {
        $donePoint = [pscustomobject]@{ X = [int]($Screen.Width * 0.75); Y = [int]($Screen.Height * 0.88) }
    }

    Tap-Point -AdbPath $AdbPath -DeviceId $DeviceId -X $donePoint.X -Y $donePoint.Y
    Start-Sleep -Seconds 5

    $selectedDump = Wait-UiContainsAll `
        -AdbPath $AdbPath `
        -DeviceId $DeviceId `
        -EvidenceDirectory $EvidenceDirectory `
        -Name "mobile-payment-e2e-$Timestamp-camera-attachment-selected" `
        -Needles @('첨부 1건', '카메라') `
        -StepName 'camera attachment selected' `
        -TimeoutSeconds 90
    return $selectedDump
}

function New-MobileE2EAlphaSuffix {
    $chars = 'abcdefghjklmnpqrstuvwxyz'.ToCharArray()
    -join (1..10 | ForEach-Object { $chars[(Get-Random -Minimum 0 -Maximum $chars.Length)] })
}

function Invoke-Api {
    param(
        [string]$Method,
        [string]$BaseUrl,
        [string]$Relative,
        [hashtable]$Headers,
        [object]$Body = $null,
        [switch]$IgnoreNotFound
    )

    $uri = ($BaseUrl.TrimEnd('/') + '/' + $Relative.TrimStart('/'))
    $parameters = @{
        Method = $Method
        Uri = $uri
        TimeoutSec = 20
        Headers = $Headers
    }
    if ($null -ne $Body) {
        $parameters.ContentType = 'application/json; charset=utf-8'
        $parameters.Body = ($Body | ConvertTo-Json -Depth 12)
    }

    try {
        return Invoke-RestMethod @parameters
    }
    catch {
        $response = $_.Exception.Response
        if ($IgnoreNotFound -and $response -and [int]$response.StatusCode -eq 404) {
            return $null
        }
        throw
    }
}

function New-ApiSession {
    param(
        [string]$BaseUrl,
        [string]$Username,
        [string]$Password
    )

    $login = Invoke-RestMethod -Method Post -Uri ($BaseUrl.TrimEnd('/') + '/auth/login') -ContentType 'application/json; charset=utf-8' -TimeoutSec 20 -Body (@{
        username = $Username
        password = $Password
    } | ConvertTo-Json)

    $token = $login.token
    if ([string]::IsNullOrWhiteSpace($token)) {
        $token = $login.accessToken
    }
    if ([string]::IsNullOrWhiteSpace($token)) {
        throw '로그인 응답에서 토큰을 찾지 못했습니다.'
    }

    return @{ Authorization = "Bearer $token" }
}

function New-TestFixture {
    param(
        [string]$BaseUrl,
        [hashtable]$Headers,
        [string]$Stamp
    )

    $customerName = "mobileecust$Stamp"
    $itemName = "mobileeitem$Stamp"
    $itemSpec = "mobileespec$Stamp"

    $customer = Invoke-Api -Method Post -BaseUrl $BaseUrl -Relative 'customers' -Headers $Headers -Body @{
        id = ([guid]::NewGuid()).ToString()
        tenantCode = 'USENET_GROUP'
        officeCode = 'ALL'
        responsibleOfficeCode = 'USENET'
        nameOriginal = $customerName
        phone = '010-0000-0000'
        mobilePhone = '010-0000-0000'
        notes = 'mobile android write e2e fixture'
    }

    $item = Invoke-Api -Method Post -BaseUrl $BaseUrl -Relative 'items' -Headers $Headers -Body @{
        id = ([guid]::NewGuid()).ToString()
        tenantCode = 'USENET_GROUP'
        officeCode = 'ALL'
        nameOriginal = $itemName
        specificationOriginal = $itemSpec
        categoryName = 'mobilee'
        unit = 'EA'
        currentStock = 10
        safetyStock = 0
        purchasePrice = 7000
        salePrice = 11000
        retailPrice = 12000
        priceGradeA = 10000
        priceGradeB = 9000
        priceGradeC = 8000
        isSale = $true
        simpleMemo = ''
        notes = 'mobile android write e2e fixture'
    }

    return [pscustomobject]@{
        Customer = $customer
        Item = $item
        CustomerName = $customerName
        ItemName = $itemName
        ItemSpec = $itemSpec
    }
}

function Get-TestInvoices {
    param(
        [string]$BaseUrl,
        [hashtable]$Headers,
        [string]$CustomerId,
        [string]$Query
    )

    $relative = 'invoices?customerId=' + [uri]::EscapeDataString($CustomerId) + '&q=' + [uri]::EscapeDataString($Query) + '&take=20'
    $invoices = Invoke-Api -Method Get -BaseUrl $BaseUrl -Relative $relative -Headers $Headers
    if ($null -eq $invoices) {
        return @()
    }
    return @($invoices)
}

function Wait-TestInvoiceCreated {
    param(
        [string]$BaseUrl,
        [hashtable]$Headers,
        [string]$CustomerId,
        [string]$CustomerName,
        [string]$ItemName,
        [ValidateSet('Sales', 'Purchase')]
        [string]$VoucherKind
    )

    for ($attempt = 1; $attempt -le 12; $attempt++) {
        $matches = Get-TestInvoices -BaseUrl $BaseUrl -Headers $Headers -CustomerId $CustomerId -Query $CustomerName |
            Where-Object {
                $_.customerId -eq $CustomerId -and
                [string]$_.voucherType -eq $VoucherKind -and
                @($_.lines | Where-Object { $_.itemNameOriginal -eq $ItemName }).Count -gt 0
            }
        if (@($matches).Count -gt 0) {
            return @($matches)[0]
        }
        Start-Sleep -Seconds 2
    }

    throw '모바일 저장 후 서버 전표 조회에서 테스트 전표를 찾지 못했습니다.'
}

function Remove-TestData {
    param(
        [string]$BaseUrl,
        [hashtable]$Headers,
        [object]$Fixture,
        [System.Collections.Generic.List[object]]$CleanupSteps
    )

    if ($null -eq $Fixture) {
        return
    }

    try {
        if ($Fixture.Customer -and $Fixture.Customer.id) {
            $invoices = Get-TestInvoices -BaseUrl $BaseUrl -Headers $Headers -CustomerId $Fixture.Customer.id -Query $Fixture.CustomerName
            foreach ($invoice in @($invoices)) {
                try {
                    Invoke-Api -Method Delete -BaseUrl $BaseUrl -Relative ("invoices/$($invoice.id)?expectedRevision=$($invoice.revision)") -Headers $Headers -IgnoreNotFound | Out-Null
                    $CleanupSteps.Add([pscustomobject]@{ Target = 'invoice'; Id = $invoice.id; Result = 'deleted' })
                }
                catch {
                    $CleanupSteps.Add([pscustomobject]@{ Target = 'invoice'; Id = $invoice.id; Result = "cleanup-failed: $($_.Exception.Message)" })
                }
            }
        }

        if ($Fixture.Item -and $Fixture.Item.id) {
            try {
                $latestItem = Invoke-Api -Method Get -BaseUrl $BaseUrl -Relative "items/$($Fixture.Item.id)" -Headers $Headers -IgnoreNotFound
                if ($latestItem) {
                    if ([decimal]$latestItem.currentStock -ne 0) {
                        $stockResetPayload = $latestItem | ConvertTo-Json -Depth 30 | ConvertFrom-Json
                        $stockResetPayload.currentStock = 0
                        $stockResetPayload.expectedRevision = [long]$latestItem.revision
                        $stockResetPayload.mutationId = ([guid]::NewGuid()).ToString()
                        $stockResetPayload.mutationCreatedAtUtc = [DateTime]::UtcNow.ToString('o')
                        $latestItem = Invoke-Api -Method Put -BaseUrl $BaseUrl -Relative "items/$($stockResetPayload.id)" -Headers $Headers -Body $stockResetPayload
                        $CleanupSteps.Add([pscustomobject]@{ Target = 'item-stock'; Id = $latestItem.id; Result = 'reset-current-stock' })
                    }
                    Invoke-Api -Method Delete -BaseUrl $BaseUrl -Relative ("items/$($latestItem.id)?expectedRevision=$($latestItem.revision)") -Headers $Headers -IgnoreNotFound | Out-Null
                    $CleanupSteps.Add([pscustomobject]@{ Target = 'item'; Id = $latestItem.id; Result = 'deleted' })
                }
            }
            catch {
                $CleanupSteps.Add([pscustomobject]@{ Target = 'item'; Id = $Fixture.Item.id; Result = "cleanup-failed: $($_.Exception.Message)" })
            }
        }

        if ($Fixture.Customer -and $Fixture.Customer.id) {
            try {
                $latestCustomer = Invoke-Api -Method Get -BaseUrl $BaseUrl -Relative "customers/$($Fixture.Customer.id)" -Headers $Headers -IgnoreNotFound
                if ($latestCustomer) {
                    Invoke-Api -Method Delete -BaseUrl $BaseUrl -Relative ("customers/$($latestCustomer.id)?expectedRevision=$($latestCustomer.revision)") -Headers $Headers -IgnoreNotFound | Out-Null
                    $CleanupSteps.Add([pscustomobject]@{ Target = 'customer'; Id = $latestCustomer.id; Result = 'deleted' })
                }
            }
            catch {
                $CleanupSteps.Add([pscustomobject]@{ Target = 'customer'; Id = $Fixture.Customer.id; Result = "cleanup-failed: $($_.Exception.Message)" })
            }
        }
    }
    catch {
        $CleanupSteps.Add([pscustomobject]@{ Target = 'cleanup'; Id = ''; Result = "cleanup-failed: $($_.Exception.Message)" })
    }
}


function New-TestInvoice {
    param(
        [string]$BaseUrl,
        [hashtable]$Headers,
        [object]$Fixture,
        [ValidateSet('Sales', 'Purchase')]
        [string]$VoucherKind
    )

    $invoiceId = [guid]::NewGuid()
    $lineId = [guid]::NewGuid()
    $unitPrice = if ($VoucherKind -eq 'Purchase') { 7000 } else { 11000 }
    $total = [decimal]$unitPrice
    $supply = [decimal][Math]::Floor([double]($total / [decimal]1.1))
    $vat = $total - $supply
    $today = Get-Date -Format 'yyyy-MM-dd'
    $memo = "mobile android payment e2e fixture $VoucherKind"

    return Invoke-Api -Method Post -BaseUrl $BaseUrl -Relative 'invoices' -Headers $Headers -Body @{
        id = $invoiceId.ToString()
        customerId = $Fixture.Customer.id
        customerName = $Fixture.CustomerName
        tenantCode = 'USENET_GROUP'
        officeCode = 'ALL'
        responsibleOfficeCode = 'USENET'
        voucherType = $VoucherKind
        invoiceDate = $today
        totalAmount = $total
        supplyAmount = $supply
        vatAmount = $vat
        vatMode = 'Included'
        taxInvoiceIssued = $false
        sourceWarehouseCode = 'USENET'
        memo = $memo
        lines = @(@{
            id = $lineId.ToString()
            invoiceId = $invoiceId.ToString()
            itemId = $Fixture.Item.id
            itemNameOriginal = $Fixture.ItemName
            specificationOriginal = $Fixture.ItemSpec
            unit = 'EA'
            quantity = 1
            unitPrice = $unitPrice
            lineAmount = $unitPrice
            remark = 'mobile payment e2e'
            itemTrackingType = 'Stock'
            isDeleted = $false
        })
    }
}

function Get-TestPayments {
    param(
        [string]$BaseUrl,
        [hashtable]$Headers,
        [string]$InvoiceId
    )

    $relative = 'payments?invoiceId=' + [uri]::EscapeDataString($InvoiceId)
    $payments = Invoke-Api -Method Get -BaseUrl $BaseUrl -Relative $relative -Headers $Headers
    if ($null -eq $payments) {
        return @()
    }
    return @($payments)
}

function Wait-TestPaymentCreated {
    param(
        [string]$BaseUrl,
        [hashtable]$Headers,
        [string]$InvoiceId,
        [decimal]$ExpectedAmount,
        [string]$ExpectedMethodName
    )

    for ($attempt = 1; $attempt -le 15; $attempt++) {
        $matches = Get-TestPayments -BaseUrl $BaseUrl -Headers $Headers -InvoiceId $InvoiceId |
            Where-Object {
                [decimal]$_.amount -eq $ExpectedAmount -and
                [string]$_.note -like "*$ExpectedMethodName*"
            }
        if (@($matches).Count -gt 0) {
            return @($matches)[0]
        }
        Start-Sleep -Seconds 2
    }

    throw '모바일 수금/지급 저장 후 서버 지급내역 조회에서 테스트 지급내역을 찾지 못했습니다.'
}

function Get-TestPaymentAttachments {
    param(
        [string]$BaseUrl,
        [hashtable]$Headers,
        [string]$PaymentId
    )

    $attachments = Invoke-Api -Method Get -BaseUrl $BaseUrl -Relative "payments/$PaymentId/attachments" -Headers $Headers
    if ($null -eq $attachments) {
        return @()
    }
    return @($attachments)
}

function Get-TestPaymentAttachmentContent {
    param(
        [string]$BaseUrl,
        [hashtable]$Headers,
        [object]$Attachment,
        [string]$EvidenceDirectory,
        [string]$VoucherSlug,
        [string]$Timestamp
    )

    if ($null -eq $Attachment -or [string]::IsNullOrWhiteSpace([string]$Attachment.id)) {
        throw 'Server attachment content download requires a saved attachment id.'
    }

    $safeFileName = if ([string]::IsNullOrWhiteSpace([string]$Attachment.fileName)) {
        "attachment-$($Attachment.id).bin"
    }
    else {
        [IO.Path]::GetFileName([string]$Attachment.fileName)
    }
    $downloadPath = Join-Path $EvidenceDirectory "server-$VoucherSlug-payment-attachment-content-$Timestamp-$safeFileName"
    $uri = $BaseUrl.TrimEnd('/') + "/payments/attachments/$($Attachment.id)/content"

    Invoke-WebRequest -Method Get -Uri $uri -Headers $Headers -TimeoutSec 60 -OutFile $downloadPath | Out-Null
    if (-not (Test-Path -LiteralPath $downloadPath)) {
        throw "Server attachment content download did not create a file: $uri"
    }

    $length = (Get-Item -LiteralPath $downloadPath).Length
    if ($length -le 0) {
        throw "Server attachment content download was empty: attachment=$($Attachment.id)"
    }
    if ([long]$Attachment.fileSize -gt 0 -and $length -ne [long]$Attachment.fileSize) {
        throw "Server attachment content size mismatch: expected=$($Attachment.fileSize), actual=$length, attachment=$($Attachment.id)"
    }

    $sha256 = (Get-FileHash -LiteralPath $downloadPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not [string]::IsNullOrWhiteSpace([string]$Attachment.fileHash) -and
        -not [string]::Equals($sha256, ([string]$Attachment.fileHash).Trim().ToLowerInvariant(), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Server attachment content hash mismatch: expected=$($Attachment.fileHash), actual=$sha256, attachment=$($Attachment.id)"
    }

    return [pscustomobject]@{
        Path = $downloadPath
        Length = $length
        Sha256 = $sha256
    }
}

function Wait-TestPaymentAttachmentCreated {
    param(
        [string]$BaseUrl,
        [hashtable]$Headers,
        [string]$PaymentId,
        [string]$ExpectedFileName,
        [int]$Attempts = 12
    )

    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        $matches = Get-TestPaymentAttachments -BaseUrl $BaseUrl -Headers $Headers -PaymentId $PaymentId |
            Where-Object {
                [string]$_.fileName -eq $ExpectedFileName -and
                [string]$_.mimeType -eq 'application/pdf' -and
                [long]$_.fileSize -gt 0
            }
        if (@($matches).Count -gt 0) {
            return @($matches)[0]
        }
        Start-Sleep -Seconds 2
    }

    throw "모바일 수금/지급 첨부 업로드 후 서버 첨부 목록에서 테스트 PDF를 찾지 못했습니다: $ExpectedFileName"
}

function Wait-TestPaymentImageAttachmentCreated {
    param(
        [string]$BaseUrl,
        [hashtable]$Headers,
        [string]$PaymentId,
        [string]$ExpectedAttachmentType = '카메라',
        [int]$Attempts = 12
    )

    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        $matches = Get-TestPaymentAttachments -BaseUrl $BaseUrl -Headers $Headers -PaymentId $PaymentId |
            Where-Object {
                [string]$_.attachmentType -eq $ExpectedAttachmentType -and
                ([string]$_.mimeType).StartsWith('image/', [StringComparison]::OrdinalIgnoreCase) -and
                [long]$_.fileSize -gt 0 -and
                -not [string]::IsNullOrWhiteSpace([string]$_.fileName)
            }
        if (@($matches).Count -gt 0) {
            return @($matches)[0]
        }
        Start-Sleep -Seconds 2
    }

    throw "Mobile payment camera attachment was not found on server after upload: payment=$PaymentId"
}

function Get-AndroidCurrentFocusSummary {
    param(
        [string]$AdbPath,
        [string]$DeviceId
    )

    try {
        $windowOutput = Invoke-Adb -AdbPath $AdbPath -Arguments @('-s', $DeviceId, 'shell', 'dumpsys', 'window')
        return (($windowOutput | Where-Object { $_ -match 'mCurrentFocus|mFocusedApp' }) -join ' | ')
    }
    catch {
        return "focus-unavailable: $($_.Exception.Message)"
    }
}

function Invoke-TestPaymentAttachmentOpenUi {
    param(
        [string]$AdbPath,
        [string]$DeviceId,
        [string]$EvidenceDirectory,
        [string]$Timestamp,
        [string]$VoucherSlug,
        [object]$Screen,
        [object]$Fixture,
        [string]$PaymentId,
        [object]$Attachment,
        [string]$PackageName,
        [bool]$ExerciseListFallback,
        [System.Collections.Generic.List[object]]$Steps
    )

    if ($null -eq $Fixture -or [string]::IsNullOrWhiteSpace([string]$Fixture.CustomerName)) {
        throw 'Attachment open UI E2E requires a fixture customer.'
    }
    if ($null -eq $Attachment -or [string]::IsNullOrWhiteSpace([string]$Attachment.fileName)) {
        throw 'Attachment open UI E2E requires a saved attachment.'
    }

    for ($navAttempt = 0; $navAttempt -lt 4; $navAttempt++) {
        $navDump = Get-UiDump -AdbPath $AdbPath -DeviceId $DeviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-payment-e2e-$VoucherSlug-$Timestamp-attachment-open-nav-reset-$navAttempt"
        if (-not $navDump.Content.Contains('content-desc="Navigate up"')) {
            break
        }
        Invoke-Adb -AdbPath $AdbPath -Arguments @('-s', $DeviceId, 'shell', 'input', 'keyevent', 'KEYCODE_BACK') | Out-Null
        Start-Sleep -Seconds 3
    }

    $customersDump = Open-BottomTabAndAssert `
        -AdbPath $AdbPath `
        -DeviceId $DeviceId `
        -EvidenceDirectory $EvidenceDirectory `
        -Timestamp $Timestamp `
        -Screen $Screen `
        -TabText '거래처' `
        -FallbackXRatio 0.30 `
        -StepName 'attachment-open-customers' `
        -Needles @('거래처', '거래처명 / 전화 / 사업자번호') `
        -Steps $Steps

    Tap-UiText -AdbPath $AdbPath -DeviceId $DeviceId -Content $customersDump.Content -Text '거래처명 / 전화 / 사업자번호' -ClassName 'android.widget.EditText' -StepName '거래처 검색 입력'
    Set-AndroidText -AdbPath $AdbPath -DeviceId $DeviceId -Text $Fixture.CustomerName
    Invoke-Adb -AdbPath $AdbPath -Arguments @('-s', $DeviceId, 'shell', 'input', 'keyevent', 'KEYCODE_ESCAPE') | Out-Null
    Start-Sleep -Seconds 2

    $typedDump = Get-UiDump -AdbPath $AdbPath -DeviceId $DeviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-payment-e2e-$VoucherSlug-$Timestamp-attachment-open-customer-search-typed"
    $lookupButton = Get-NodeCenterByText -Content $typedDump.Content -Text '조회' -ClassName 'android.widget.Button'
    if ($lookupButton) {
        Tap-Point -AdbPath $AdbPath -DeviceId $DeviceId -X $lookupButton.X -Y $lookupButton.Y
    }
    Start-Sleep -Seconds 6

    $customerDump = Get-UiDump -AdbPath $AdbPath -DeviceId $DeviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-payment-e2e-$VoucherSlug-$Timestamp-attachment-open-customer-result"
    Assert-UiContains -Content $customerDump.Content -Needles @($Fixture.CustomerName) -StepName '첨부 열기 거래처 검색 결과'
    Tap-UiText -AdbPath $AdbPath -DeviceId $DeviceId -Content $customerDump.Content -Text $Fixture.CustomerName -ClassName 'android.widget.TextView' -StepName '첨부 열기 거래처 선택'
    Start-Sleep -Seconds 5

    $detailDump = Wait-UiContainsAll `
        -AdbPath $AdbPath `
        -DeviceId $DeviceId `
        -EvidenceDirectory $EvidenceDirectory `
        -Name "mobile-payment-e2e-$VoucherSlug-$Timestamp-attachment-open-customer-detail" `
        -Needles @($Fixture.CustomerName, '수금/지급') `
        -StepName '첨부 열기 거래처 상세' `
        -TimeoutSeconds 90

    Tap-UiText -AdbPath $AdbPath -DeviceId $DeviceId -Content $detailDump.Content -Text '수금/지급' -ClassName 'android.widget.Button' -StepName '거래처 수금/지급 탭'
    Start-Sleep -Seconds 4

    $paymentsDump = Wait-UiContainsAll `
        -AdbPath $AdbPath `
        -DeviceId $DeviceId `
        -EvidenceDirectory $EvidenceDirectory `
        -Name "mobile-payment-e2e-$VoucherSlug-$Timestamp-attachment-open-payment-history" `
        -Needles @('첨부 보기') `
        -StepName '거래처 수금/지급 첨부 보기 버튼' `
        -TimeoutSeconds 90

    if ($ExerciseListFallback) {
        $faultTarget = "payments/$PaymentId/attachments"
        Set-MobileDiagnosticNetworkFault -AdbPath $AdbPath -DeviceId $DeviceId -PackageName $PackageName -Target $faultTarget
        $Steps.Add([pscustomobject]@{ Step = "mobile-$VoucherSlug-payment-attachment-list-fallback-fault"; Result = 'PASS'; Detail = $faultTarget })
    }

    Tap-UiText -AdbPath $AdbPath -DeviceId $DeviceId -Content $paymentsDump.Content -Text '첨부 보기' -ClassName 'android.widget.Button' -StepName '거래처 첨부 보기 열기'
    Start-Sleep -Seconds 4

    $attachmentListDump = Wait-UiContainsAll `
        -AdbPath $AdbPath `
        -DeviceId $DeviceId `
        -EvidenceDirectory $EvidenceDirectory `
        -Name "mobile-payment-e2e-$VoucherSlug-$Timestamp-attachment-open-list" `
        -Needles @('수금/지급 첨부', [string]$Attachment.fileName, '열기') `
        -StepName '수금/지급 첨부 목록' `
        -TimeoutSeconds 90

    $Steps.Add([pscustomobject]@{ Step = "mobile-$VoucherSlug-payment-attachment-list-opened"; Result = 'PASS'; Detail = "attachment=$($Attachment.id), file=$($Attachment.fileName), dump=$($attachmentListDump.Path)" })

    Tap-UiText -AdbPath $AdbPath -DeviceId $DeviceId -Content $attachmentListDump.Content -Text '열기' -ClassName 'android.widget.Button' -StepName '수금/지급 첨부 파일 열기'
    Start-Sleep -Seconds 8

    $afterOpenDump = Get-UiDump -AdbPath $AdbPath -DeviceId $DeviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-payment-e2e-$VoucherSlug-$Timestamp-attachment-open-after-tap"
    $focusSummary = Get-AndroidCurrentFocusSummary -AdbPath $AdbPath -DeviceId $DeviceId
    $friendlyNoViewer = $afterOpenDump.Content.Contains('열 수 있는 앱을 찾지 못했습니다')
    $openedInApp = $afterOpenDump.Content.Contains('첨부 파일을 열었습니다.')
    $androidResolver = $afterOpenDump.Content.Contains('Open with') -or $afterOpenDump.Content.Contains('Just once') -or $afterOpenDump.Content.Contains('Always')
    $externalActivity = -not [string]::IsNullOrWhiteSpace($focusSummary) -and
        -not $focusSummary.Contains($PackageName) -and
        $focusSummary.IndexOf('nexuslauncher', [StringComparison]::OrdinalIgnoreCase) -lt 0 -and
        $focusSummary.IndexOf('Launcher', [StringComparison]::OrdinalIgnoreCase) -lt 0

    if (-not ($friendlyNoViewer -or $openedInApp -or $androidResolver -or $externalActivity)) {
        if ($afterOpenDump.Content.Contains('첨부 열기 실패')) {
            throw "첨부 열기 버튼이 사용자 친화적 안내 없이 실패했습니다: focus=$focusSummary"
        }

        throw "첨부 열기 버튼 실행 결과를 확인하지 못했습니다: focus=$focusSummary, dump=$($afterOpenDump.Path)"
    }

    $openResult = if ($externalActivity) {
        "external-activity: $focusSummary"
    }
    elseif ($androidResolver) {
        'android-resolver'
    }
    elseif ($openedInApp) {
        'opened'
    }
    else {
        'friendly-no-viewer'
    }
    $Steps.Add([pscustomobject]@{ Step = "mobile-$VoucherSlug-payment-attachment-open-button"; Result = 'PASS'; Detail = "result=$openResult, dump=$($afterOpenDump.Path)" })
}

function Get-SyncTransactionById {
    param(
        [string]$BaseUrl,
        [hashtable]$Headers,
        [string]$TransactionId
    )

    $pull = Invoke-Api -Method Get -BaseUrl $BaseUrl -Relative 'sync/pull?sinceRev=0' -Headers $Headers
    if ($null -eq $pull -or $null -eq $pull.transactions) {
        return $null
    }

    return @($pull.transactions) | Where-Object { [string]$_.id -eq $TransactionId } | Select-Object -First 1
}

function Wait-TestTransactionCreated {
    param(
        [string]$BaseUrl,
        [hashtable]$Headers,
        [string]$TransactionId,
        [string]$InvoiceId,
        [ValidateSet('Sales', 'Purchase')]
        [string]$VoucherKind,
        [decimal]$ExpectedAmount
    )

    for ($attempt = 1; $attempt -le 12; $attempt++) {
        $transaction = Get-SyncTransactionById -BaseUrl $BaseUrl -Headers $Headers -TransactionId $TransactionId
        if ($transaction -and -not $transaction.isDeleted -and [string]$transaction.linkedInvoiceId -eq $InvoiceId) {
            if ($VoucherKind -eq 'Purchase') {
                if ([decimal]$transaction.bankPayment -eq $ExpectedAmount -and [decimal]$transaction.paymentTotal -eq $ExpectedAmount) {
                    return $transaction
                }
            }
            else {
                if ([decimal]$transaction.bankReceipt -eq $ExpectedAmount -and [decimal]$transaction.receiptTotal -eq $ExpectedAmount) {
                    return $transaction
                }
            }
        }
        Start-Sleep -Seconds 2
    }

    throw '모바일 수금/지급 저장 후 연결 거래내역을 찾지 못했습니다.'
}

function Remove-TestTransaction {
    param(
        [string]$BaseUrl,
        [hashtable]$Headers,
        [string]$TransactionId,
        [System.Collections.Generic.List[object]]$CleanupSteps
    )

    try {
        $transaction = Get-SyncTransactionById -BaseUrl $BaseUrl -Headers $Headers -TransactionId $TransactionId
        if ($null -eq $transaction -or $transaction.isDeleted) {
            return
        }

        $transaction.isDeleted = $true
        $transaction.expectedRevision = $transaction.revision
        $transaction.updatedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
        $transaction.mutationId = ([guid]::NewGuid()).ToString()
        $transaction.mutationCreatedAtUtc = (Get-Date).ToUniversalTime().ToString('o')

        $pushResult = Invoke-Api -Method Post -BaseUrl $BaseUrl -Relative 'sync/push' -Headers $Headers -Body @{
            deviceId = 'mobile-payment-e2e-cleanup'
            transactions = @($transaction)
        }
        if ($pushResult -and [int]$pushResult.conflictCount -gt 0) {
            throw "sync conflict while deleting transaction $TransactionId"
        }
        $CleanupSteps.Add([pscustomobject]@{ Target = 'transaction'; Id = $TransactionId; Result = 'deleted' })
    }
    catch {
        $CleanupSteps.Add([pscustomobject]@{ Target = 'transaction'; Id = $TransactionId; Result = "cleanup-failed: $($_.Exception.Message)" })
    }
}

function Remove-TestPaymentData {
    param(
        [string]$BaseUrl,
        [hashtable]$Headers,
        [string]$InvoiceId,
        [System.Collections.Generic.List[object]]$CleanupSteps
    )

    if ([string]::IsNullOrWhiteSpace($InvoiceId)) {
        return
    }

    foreach ($payment in @(Get-TestPayments -BaseUrl $BaseUrl -Headers $Headers -InvoiceId $InvoiceId)) {
        try {
            Invoke-Api -Method Delete -BaseUrl $BaseUrl -Relative ("payments/$($payment.id)?expectedRevision=$($payment.revision)") -Headers $Headers -IgnoreNotFound | Out-Null
            $CleanupSteps.Add([pscustomobject]@{ Target = 'payment'; Id = $payment.id; Result = 'deleted' })
        }
        catch {
            $CleanupSteps.Add([pscustomobject]@{ Target = 'payment'; Id = $payment.id; Result = "cleanup-failed: $($_.Exception.Message)" })
        }

        Remove-TestTransaction -BaseUrl $BaseUrl -Headers $Headers -TransactionId $payment.id -CleanupSteps $CleanupSteps
    }
}

if ([string]::IsNullOrWhiteSpace($EvidenceDirectory)) {
    $EvidenceDirectory = Join-Path $ProjectRoot '테스트 시행\기록'
}
New-Item -ItemType Directory -Force -Path $EvidenceDirectory | Out-Null

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$stamp = New-MobileE2EAlphaSuffix
$voucherSlug = $VoucherKind.ToLowerInvariant()
$voucherKorean = if ($VoucherKind -eq 'Purchase') { '구매' } else { '판매' }
$paymentAction = if ($VoucherKind -eq 'Purchase') { '지급' } else { '수금' }
$paymentSaveText = if ($VoucherKind -eq 'Purchase') { '지급 저장' } else { '수금 저장' }
$expectedMethod = if ($VoucherKind -eq 'Purchase') { '통장지급' } else { '통장수금' }
$expectedAmount = if ($VoucherKind -eq 'Purchase') { [decimal]7000 } else { [decimal]11000 }
$steps = New-Object System.Collections.Generic.List[object]
$cleanupSteps = New-Object System.Collections.Generic.List[object]
$fixture = $null
$createdInvoice = $null
$createdPayment = $null
$createdTransaction = $null
$createdAttachment = $null
$createdAttachmentContent = $null
$attachmentFixture = $null
$attachmentModeText = if ($ExerciseCameraAttachmentUpload) { 'Camera' } elseif ($ExerciseAttachmentUpload) { 'PDF' } else { 'None' }
$localApiStoppedForExercise = $false
$resultStatus = 'FAIL'
$errorMessage = $null

try {
    $exerciseNonRetryableSaveFault = -not [string]::IsNullOrWhiteSpace($ExerciseNonRetryableSaveFaultStatus)
    if ($exerciseNonRetryableSaveFault -and ($ExerciseStoppedServerDirtySync -or $ExerciseAttachmentUpload -or $ExerciseCameraAttachmentUpload -or $ExerciseAttachmentOpenUi -or $ExerciseAttachmentListFallback)) {
        throw 'Non-retryable save fault cannot run with stopped API or attachment exercises.'
    }

    if ($ExerciseStoppedServerDirtySync -or $exerciseNonRetryableSaveFault) {
        Assert-LocalDirtySyncTarget -BaseUrl $BaseUrl
    }

    $headers = New-ApiSession -BaseUrl $BaseUrl -Username $Username -Password $Password
    $steps.Add([pscustomobject]@{ Step = 'api-login'; Result = 'PASS'; Detail = $BaseUrl })

    $fixture = New-TestFixture -BaseUrl $BaseUrl -Headers $headers -Stamp $stamp
    $steps.Add([pscustomobject]@{ Step = 'fixture-create'; Result = 'PASS'; Detail = "customer=$($fixture.Customer.id), item=$($fixture.Item.id)" })

    $createdInvoice = New-TestInvoice -BaseUrl $BaseUrl -Headers $headers -Fixture $fixture -VoucherKind $VoucherKind
    $steps.Add([pscustomobject]@{ Step = "api-$voucherSlug-invoice-create"; Result = 'PASS'; Detail = "invoice=$($createdInvoice.id), total=$($createdInvoice.totalAmount)" })

    $resolvedAdb = Resolve-AdbPath -RequestedPath $AdbPath
    $resolvedApk = Resolve-ApkPath -ProjectRoot $ProjectRoot -RequestedPath $ApkPath
    $deviceId = Get-ConnectedDeviceId -AdbPath $resolvedAdb
    $screen = Get-ScreenSize -AdbPath $resolvedAdb -DeviceId $deviceId

    if (-not $SkipInstall) {
        Install-MobileApk -AdbPath $resolvedAdb -DeviceId $deviceId -ApkPath $resolvedApk -PackageName $PackageName
        $steps.Add([pscustomobject]@{ Step = 'install'; Result = 'PASS'; Detail = $resolvedApk })
        Invoke-Adb -AdbPath $resolvedAdb -Arguments @('-s', $deviceId, 'shell', 'pm', 'clear', $PackageName) | Out-Null
        $steps.Add([pscustomobject]@{ Step = 'app-data-clear'; Result = 'PASS'; Detail = $PackageName })
    }

    # Android 런처를 강제로 종료하면 일부 에뮬레이터에서 포커스 윈도우가 사라져
    # 앱 시작 ANR 또는 홈 화면 체류가 발생할 수 있습니다. 기본값은 거래플랜 앱만 정리합니다.
    if ([string]::Equals($env:GEORAEPLAN_ANDROID_SMOKE_FORCE_STOP_LAUNCHER, '1', [StringComparison]::OrdinalIgnoreCase)) {
        Invoke-Adb -AdbPath $resolvedAdb -Arguments @('-s', $deviceId, 'shell', 'am', 'force-stop', 'com.google.android.apps.nexuslauncher') | Out-Null
    }
    Invoke-Adb -AdbPath $resolvedAdb -Arguments @('-s', $deviceId, 'shell', 'am', 'force-stop', $PackageName) | Out-Null
    Start-Sleep -Seconds 1
    Start-MobileApp -AdbPath $resolvedAdb -DeviceId $deviceId -PackageName $PackageName
    Start-Sleep -Seconds 5

    $dump = Wait-UiReadyForLoginOrHome -AdbPath $resolvedAdb -DeviceId $deviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-payment-e2e-$voucherSlug-$timestamp-launch"
    if ($dump.Content.Contains('빠른 안내')) {
        Start-Sleep -Seconds 3
        $postLaunchDump = Get-UiDump -AdbPath $resolvedAdb -DeviceId $deviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-payment-e2e-$voucherSlug-$timestamp-post-launch-stable"
        if ($postLaunchDump.Content.Contains('계정 로그인') -or ($postLaunchDump.Content.Contains('로그인') -and $postLaunchDump.Content.Contains('비밀번호'))) {
            $dump = $postLaunchDump
        }
    }
    if ($dump.Content.Contains('계정 로그인') -or ($dump.Content.Contains('로그인') -and $dump.Content.Contains('비밀번호'))) {
        $userPoint = Get-NodeCenterByText -Content $dump.Content -Text '아이디' -ClassName 'android.widget.EditText'
        $passwordPoint = Get-NodeCenterByText -Content $dump.Content -Text '비밀번호' -ClassName 'android.widget.EditText'
        $loginButtonPoint = Get-NodeCenterByText -Content $dump.Content -Text '로그인' -ClassName 'android.widget.Button'

        if ($userPoint) {
            $dump = Set-LoginTextField -AdbPath $resolvedAdb -DeviceId $deviceId -EvidenceDirectory $EvidenceDirectory -Timestamp "$voucherSlug-$timestamp" -FieldName '아이디' -Point $userPoint -Value $Username -VerifyPlainText
            Invoke-Adb -AdbPath $resolvedAdb -Arguments @('-s', $deviceId, 'shell', 'input', 'keyevent', 'KEYCODE_ESCAPE') | Out-Null
            Start-Sleep -Seconds 1
            $dump = Wait-UiReadyForLoginOrHome -AdbPath $resolvedAdb -DeviceId $deviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-payment-e2e-$voucherSlug-$timestamp-login-username-entered" -TimeoutSeconds 90
            $passwordPoint = Get-NodeCenterByText -Content $dump.Content -Text '비밀번호' -ClassName 'android.widget.EditText'
        }
        if (-not $passwordPoint) {
            throw '로그인 화면에서 비밀번호 입력칸을 찾지 못했습니다.'
        }
        $dump = Set-LoginTextField -AdbPath $resolvedAdb -DeviceId $deviceId -EvidenceDirectory $EvidenceDirectory -Timestamp "$voucherSlug-$timestamp" -FieldName '비밀번호' -Point $passwordPoint -Value $Password
        Invoke-Adb -AdbPath $resolvedAdb -Arguments @('-s', $deviceId, 'shell', 'input', 'keyevent', 'KEYCODE_ESCAPE') | Out-Null
        Start-Sleep -Seconds 1
        $dump = Wait-UiReadyForLoginOrHome -AdbPath $resolvedAdb -DeviceId $deviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-payment-e2e-$voucherSlug-$timestamp-login-password-entered" -TimeoutSeconds 90
        $loginButtonPoint = Get-NodeCenterByText -Content $dump.Content -Text '로그인' -ClassName 'android.widget.Button'
        if (-not $loginButtonPoint) {
            throw '로그인 버튼을 찾지 못했습니다.'
        }
        Tap-Point -AdbPath $resolvedAdb -DeviceId $deviceId -X $loginButtonPoint.X -Y $loginButtonPoint.Y
        Start-Sleep -Seconds 10
        $steps.Add([pscustomobject]@{ Step = 'mobile-login'; Result = 'PASS'; Detail = $Username })
    }

    for ($navAttempt = 0; $navAttempt -lt 4; $navAttempt++) {
        $navDump = Get-UiDump -AdbPath $resolvedAdb -DeviceId $deviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-payment-e2e-$voucherSlug-$timestamp-nav-reset-$navAttempt"
        if (-not $navDump.Content.Contains('content-desc="Navigate up"')) {
            break
        }
        Invoke-Adb -AdbPath $resolvedAdb -Arguments @('-s', $deviceId, 'shell', 'input', 'keyevent', 'KEYCODE_BACK') | Out-Null
        Start-Sleep -Seconds 3
    }

    Tap-BottomTab -AdbPath $resolvedAdb -DeviceId $deviceId -Screen $screen -XRatio 0.70
    Start-Sleep -Seconds 5
    $invoicesDump = Get-UiDump -AdbPath $resolvedAdb -DeviceId $deviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-payment-e2e-$voucherSlug-$timestamp-invoices"
    Assert-UiContains -Content $invoicesDump.Content -Needles @('전표', '수금/지급') -StepName '전표 화면'

    Tap-UiText -AdbPath $resolvedAdb -DeviceId $deviceId -Content $invoicesDump.Content -Text '거래처명 / 전표번호 / 메모' -ClassName 'android.widget.EditText' -StepName '전표 검색 입력'
    Set-AndroidText -AdbPath $resolvedAdb -DeviceId $deviceId -Text $fixture.CustomerName
    Invoke-Adb -AdbPath $resolvedAdb -Arguments @('-s', $deviceId, 'shell', 'input', 'keyevent', 'KEYCODE_ESCAPE') | Out-Null
    Start-Sleep -Seconds 2
    $typedDump = Get-UiDump -AdbPath $resolvedAdb -DeviceId $deviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-payment-e2e-$voucherSlug-$timestamp-search-typed"
    $lookupButton = Get-NodeCenterByText -Content $typedDump.Content -Text '조회' -ClassName 'android.widget.Button'
    if ($lookupButton) {
        Tap-Point -AdbPath $resolvedAdb -DeviceId $deviceId -X $lookupButton.X -Y $lookupButton.Y
    }
    Start-Sleep -Seconds 6

    $resultDump = Get-UiDump -AdbPath $resolvedAdb -DeviceId $deviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-payment-e2e-$voucherSlug-$timestamp-search-result"
    Assert-UiContains -Content $resultDump.Content -Needles @($fixture.CustomerName) -StepName '전표 검색 결과'
    Tap-UiText -AdbPath $resolvedAdb -DeviceId $deviceId -Content $resultDump.Content -Text $fixture.CustomerName -ClassName 'android.widget.TextView' -StepName '전표 선택'
    Start-Sleep -Seconds 4

    $detailDump = Get-UiDump -AdbPath $resolvedAdb -DeviceId $deviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-payment-e2e-$voucherSlug-$timestamp-detail"
    Assert-UiContains -Content $detailDump.Content -Needles @('선택 전표 상세', $fixture.ItemName, '수금/지급') -StepName '선택 전표 상세'
    Tap-UiText -AdbPath $resolvedAdb -DeviceId $deviceId -Content $detailDump.Content -Text '수금/지급' -ClassName 'android.widget.Button' -StepName '선택 전표 수금/지급 열기' -MinY 520
    Start-Sleep -Seconds 5

    $paymentDump = Get-UiDump -AdbPath $resolvedAdb -DeviceId $deviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-payment-e2e-$voucherSlug-$timestamp-payment-page"
    Assert-UiContains -Content $paymentDump.Content -Needles @("$paymentAction 입력", $expectedMethod) -StepName '수금/지급 입력 화면'

    if ($ExerciseAttachmentUpload) {
        $attachmentFixture = New-TestAttachmentPdf -EvidenceDirectory $EvidenceDirectory -Timestamp $timestamp
        Push-TestAttachmentToDevice -AdbPath $resolvedAdb -DeviceId $deviceId -Attachment $attachmentFixture
        $selectedAttachmentDump = Select-PdfAttachmentFromDevice `
            -AdbPath $resolvedAdb `
            -DeviceId $deviceId `
            -EvidenceDirectory $EvidenceDirectory `
            -Timestamp "mobile-payment-e2e-$voucherSlug-$timestamp" `
            -Attachment $attachmentFixture
        $steps.Add([pscustomobject]@{ Step = "mobile-$voucherSlug-payment-attachment-selected"; Result = 'PASS'; Detail = "file=$($attachmentFixture.FileName), dump=$($selectedAttachmentDump.Path)" })
    }
    elseif ($ExerciseCameraAttachmentUpload) {
        Grant-CameraPermissionIfPossible -AdbPath $resolvedAdb -DeviceId $deviceId -PackageName $PackageName
        $selectedAttachmentDump = Select-CameraAttachmentFromDevice `
            -AdbPath $resolvedAdb `
            -DeviceId $deviceId `
            -EvidenceDirectory $EvidenceDirectory `
            -Timestamp "mobile-payment-e2e-$voucherSlug-$timestamp" `
            -Screen $screen
        $steps.Add([pscustomobject]@{ Step = "mobile-$voucherSlug-payment-camera-attachment-selected"; Result = 'PASS'; Detail = "dump=$($selectedAttachmentDump.Path)" })
    }

    $saveButtonPoint = $null
    for ($scrollAttempt = 0; $scrollAttempt -lt 5; $scrollAttempt++) {
        $paymentDump = Get-UiDump -AdbPath $resolvedAdb -DeviceId $deviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-payment-e2e-$voucherSlug-$timestamp-payment-save-$scrollAttempt"
        $saveButtonPoint = Get-NodeCenterByText -Content $paymentDump.Content -Text $paymentSaveText -ClassName 'android.widget.Button'
        if (-not $saveButtonPoint) {
            $saveButtonPoint = Get-NodeCenterByText -Content $paymentDump.Content -Text "마지막 단계 · $paymentSaveText" -ClassName 'android.widget.Button'
        }
        if ($saveButtonPoint) { break }
        Invoke-Adb -AdbPath $resolvedAdb -Arguments @('-s', $deviceId, 'shell', 'input', 'swipe', '540', '2050', '540', '900', '700') | Out-Null
        Start-Sleep -Seconds 2
    }
    if (-not $saveButtonPoint) {
        throw "$paymentSaveText 버튼을 찾지 못했습니다."
    }

    if ($exerciseNonRetryableSaveFault) {
        Set-MobileDiagnosticFault -AdbPath $resolvedAdb -DeviceId $deviceId -PackageName $PackageName -Mode $ExerciseNonRetryableSaveFaultStatus -Target 'sync/push'
        $steps.Add([pscustomobject]@{ Step = 'mobile-nonretryable-payment-fault-before-save'; Result = 'PASS'; Detail = "$ExerciseNonRetryableSaveFaultStatus|sync/push" })
    }
    if ($ExerciseStoppedServerDirtySync) {
        $stoppedProcesses = Stop-LocalApiForBaseUrl -BaseUrl $BaseUrl -ProjectRoot $ProjectRoot
        $localApiStoppedForExercise = $true
        $steps.Add([pscustomobject]@{ Step = 'local-api-stop-before-payment-save'; Result = 'PASS'; Detail = ($stoppedProcesses -join ', ') })
    }

    $saveTappedAt = Get-Date
    Tap-Point -AdbPath $resolvedAdb -DeviceId $deviceId -X $saveButtonPoint.X -Y $saveButtonPoint.Y

    if ($ExerciseStoppedServerDirtySync) {
        $pendingDump = Wait-UiContainsAll `
            -AdbPath $resolvedAdb `
            -DeviceId $deviceId `
            -EvidenceDirectory $EvidenceDirectory `
            -Name "mobile-payment-e2e-$voucherSlug-$timestamp-after-payment-save-stopped-server" `
            -Needles @($paymentAction, '동기화', '대기') `
            -StepName '실제 API 중단 후 수금/지급 dirty 대기' `
            -TimeoutSeconds $StoppedServerOfflineTimeoutSeconds
        $offlineElapsedSeconds = [Math]::Round(((Get-Date) - $saveTappedAt).TotalSeconds, 1)
        $steps.Add([pscustomobject]@{ Step = 'mobile-stopped-server-payment-pending'; Result = 'PASS'; Detail = "elapsed=${offlineElapsedSeconds}s, dump=$($pendingDump.Path)" })

        $restart = Start-LocalApiForBaseUrl `
            -BaseUrl $BaseUrl `
            -ProjectRoot $ProjectRoot `
            -DotNetPath $DotNetPath `
            -LocalApiProjectFile $LocalApiProjectFile `
            -Username $Username `
            -Password $Password `
            -EvidenceDirectory $EvidenceDirectory `
            -Timestamp $timestamp
        $localApiStoppedForExercise = $false
        $steps.Add([pscustomobject]@{ Step = 'local-api-restart-before-payment-sync'; Result = 'PASS'; Detail = "pid=$($restart.ProcessId), stdout=$($restart.StdOut), stderr=$($restart.StdErr)" })

        $matchesAfterRestart = @(Get-TestPayments -BaseUrl $BaseUrl -Headers $headers -InvoiceId $createdInvoice.id |
            Where-Object {
                [decimal]$_.amount -eq $expectedAmount -and
                [string]$_.note -like "*$expectedMethod*"
            })
        if ($matchesAfterRestart.Count -gt 0) {
            $createdPayment = $matchesAfterRestart[0]
            $createdTransaction = Wait-TestTransactionCreated -BaseUrl $BaseUrl -Headers $headers -TransactionId $createdPayment.id -InvoiceId $createdInvoice.id -VoucherKind $VoucherKind -ExpectedAmount $expectedAmount
            $steps.Add([pscustomobject]@{ Step = 'server-payment-auto-pushed-immediately-after-restart'; Result = 'PASS'; Detail = "payment=$($createdPayment.id), transaction=$($createdTransaction.id)" })
        }
        else {
            $steps.Add([pscustomobject]@{ Step = 'server-payment-absent-before-sync'; Result = 'PASS'; Detail = $createdInvoice.id })
        }

        $syncDump = Open-BottomTabAndAssert `
            -AdbPath $resolvedAdb `
            -DeviceId $deviceId `
            -EvidenceDirectory $EvidenceDirectory `
            -Timestamp $timestamp `
            -Screen $screen `
            -TabText '동기화' `
            -FallbackXRatio 0.84 `
            -StepName 'sync-status-before-payment-dirty-push' `
            -Needles @('동기화 상태', '저장 대기', '권장 동기화 실행', '서버에서 받기', '서버에 올리기') `
            -Steps $steps

        if ($syncDump.Content.Contains('수금·지급 0건')) {
            if (-not $createdPayment) {
                $createdPayment = Wait-TestPaymentCreated -BaseUrl $BaseUrl -Headers $headers -InvoiceId $createdInvoice.id -ExpectedAmount $expectedAmount -ExpectedMethodName $expectedMethod
                $createdTransaction = Wait-TestTransactionCreated -BaseUrl $BaseUrl -Headers $headers -TransactionId $createdPayment.id -InvoiceId $createdInvoice.id -VoucherKind $VoucherKind -ExpectedAmount $expectedAmount
            }
            $steps.Add([pscustomobject]@{ Step = "mobile-$voucherSlug-payment-auto-push-after-restart"; Result = 'PASS'; Detail = "payment=$($createdPayment.id), transaction=$($createdTransaction.id), dump=$($syncDump.Path)" })
        }
        else {
            Assert-UiContains -Content $syncDump.Content -Needles @('수금·지급 1건') -StepName '동기화 전 dirty 수금/지급 1건'

            Invoke-SyncNowAndAssert `
                -AdbPath $resolvedAdb `
                -DeviceId $deviceId `
                -EvidenceDirectory $EvidenceDirectory `
                -Timestamp $timestamp `
                -SyncContent $syncDump.Content `
                -Steps $steps | Out-Null

            $createdPayment = Wait-TestPaymentCreated -BaseUrl $BaseUrl -Headers $headers -InvoiceId $createdInvoice.id -ExpectedAmount $expectedAmount -ExpectedMethodName $expectedMethod
            $createdTransaction = Wait-TestTransactionCreated -BaseUrl $BaseUrl -Headers $headers -TransactionId $createdPayment.id -InvoiceId $createdInvoice.id -VoucherKind $VoucherKind -ExpectedAmount $expectedAmount
            $steps.Add([pscustomobject]@{ Step = "mobile-$voucherSlug-payment-dirty-push"; Result = 'PASS'; Detail = "payment=$($createdPayment.id), transaction=$($createdTransaction.id), method=$expectedMethod, amount=$expectedAmount, dump=$($pendingDump.Path)" })
        }
    }
    elseif ($exerciseNonRetryableSaveFault) {
        $rejectionDump = Wait-UiContainsAll `
            -AdbPath $resolvedAdb `
            -DeviceId $deviceId `
            -EvidenceDirectory $EvidenceDirectory `
            -Name "mobile-payment-e2e-$voucherSlug-$timestamp-after-payment-save-nonretryable-$ExerciseNonRetryableSaveFaultStatus" `
            -Needles @($paymentAction, '저장되지 않았습니다') `
            -StepName '비재시도 서버 거부 후 수금/지급 저장 실패 표시' `
            -TimeoutSeconds 45

        $matchesAfterRejection = @(Get-TestPayments -BaseUrl $BaseUrl -Headers $headers -InvoiceId $createdInvoice.id |
            Where-Object {
                [decimal]$_.amount -eq $expectedAmount -and
                [string]$_.note -like "*$expectedMethod*"
            })
        if ($matchesAfterRejection.Count -gt 0) {
            throw '비재시도 서버 거부 후 서버에서 테스트 수금/지급이 조회되었습니다.'
        }
        $steps.Add([pscustomobject]@{ Step = 'server-payment-absent-after-nonretryable-rejection'; Result = 'PASS'; Detail = $createdInvoice.id })

        $syncDump = Open-BottomTabAndAssert `
            -AdbPath $resolvedAdb `
            -DeviceId $deviceId `
            -EvidenceDirectory $EvidenceDirectory `
            -Timestamp $timestamp `
            -Screen $screen `
            -TabText '동기화' `
            -FallbackXRatio 0.84 `
            -StepName 'sync-status-after-nonretryable-payment-rejection' `
            -Needles @('동기화 상태', '수금·지급 0건') `
            -Steps $steps
        $steps.Add([pscustomobject]@{ Step = 'mobile-nonretryable-payment-rejection-not-dirty'; Result = 'PASS'; Detail = $syncDump.Path })
    }
    else {
        Start-Sleep -Seconds 12

        $createdPayment = Wait-TestPaymentCreated -BaseUrl $BaseUrl -Headers $headers -InvoiceId $createdInvoice.id -ExpectedAmount $expectedAmount -ExpectedMethodName $expectedMethod
        $createdTransaction = Wait-TestTransactionCreated -BaseUrl $BaseUrl -Headers $headers -TransactionId $createdPayment.id -InvoiceId $createdInvoice.id -VoucherKind $VoucherKind -ExpectedAmount $expectedAmount
        $steps.Add([pscustomobject]@{ Step = "mobile-$voucherSlug-payment-create"; Result = 'PASS'; Detail = "payment=$($createdPayment.id), transaction=$($createdTransaction.id), method=$expectedMethod, amount=$expectedAmount" })
    }

    if ($ExerciseAttachmentUpload -or $ExerciseCameraAttachmentUpload) {
        try {
            if ($ExerciseAttachmentUpload) {
                $createdAttachment = Wait-TestPaymentAttachmentCreated -BaseUrl $BaseUrl -Headers $headers -PaymentId $createdPayment.id -ExpectedFileName $attachmentFixture.FileName -Attempts 6
            }
            else {
                $createdAttachment = Wait-TestPaymentImageAttachmentCreated -BaseUrl $BaseUrl -Headers $headers -PaymentId $createdPayment.id -ExpectedAttachmentType '카메라' -Attempts 6
            }
        }
        catch {
            $attachmentSyncDump = Open-BottomTabAndAssert `
                -AdbPath $resolvedAdb `
                -DeviceId $deviceId `
                -EvidenceDirectory $EvidenceDirectory `
                -Timestamp $timestamp `
                -Screen $screen `
                -TabText '동기화' `
                -FallbackXRatio 0.84 `
                -StepName 'sync-status-before-payment-attachment-upload' `
                -Needles @('동기화 상태', '첨부', '권장 동기화 실행', '서버에서 받기', '서버에 올리기') `
                -Steps $steps

            Invoke-SyncNowAndAssert `
                -AdbPath $resolvedAdb `
                -DeviceId $deviceId `
                -EvidenceDirectory $EvidenceDirectory `
                -Timestamp $timestamp `
                -SyncContent $attachmentSyncDump.Content `
                -Steps $steps | Out-Null

            if ($ExerciseAttachmentUpload) {
                $createdAttachment = Wait-TestPaymentAttachmentCreated -BaseUrl $BaseUrl -Headers $headers -PaymentId $createdPayment.id -ExpectedFileName $attachmentFixture.FileName -Attempts 12
            }
            else {
                $createdAttachment = Wait-TestPaymentImageAttachmentCreated -BaseUrl $BaseUrl -Headers $headers -PaymentId $createdPayment.id -ExpectedAttachmentType '카메라' -Attempts 12
            }
        }

        $attachmentStepName = if ($ExerciseCameraAttachmentUpload) { "server-$voucherSlug-payment-camera-attachment-upload" } else { "server-$voucherSlug-payment-attachment-upload" }
        $steps.Add([pscustomobject]@{ Step = $attachmentStepName; Result = 'PASS'; Detail = "attachment=$($createdAttachment.id), payment=$($createdPayment.id), type=$($createdAttachment.attachmentType), file=$($createdAttachment.fileName), mime=$($createdAttachment.mimeType), size=$($createdAttachment.fileSize)" })
        $createdAttachmentContent = Get-TestPaymentAttachmentContent `
            -BaseUrl $BaseUrl `
            -Headers $headers `
            -Attachment $createdAttachment `
            -EvidenceDirectory $EvidenceDirectory `
            -VoucherSlug $voucherSlug `
            -Timestamp $timestamp
        $steps.Add([pscustomobject]@{ Step = "server-$voucherSlug-payment-attachment-content-download"; Result = 'PASS'; Detail = "path=$($createdAttachmentContent.Path), bytes=$($createdAttachmentContent.Length), sha256=$($createdAttachmentContent.Sha256)" })

        if ($ExerciseAttachmentOpenUi) {
            Invoke-TestPaymentAttachmentOpenUi `
                -AdbPath $resolvedAdb `
                -DeviceId $deviceId `
                -EvidenceDirectory $EvidenceDirectory `
                -Timestamp $timestamp `
                -VoucherSlug $voucherSlug `
                -Screen $screen `
                -Fixture $fixture `
                -PaymentId $createdPayment.id `
                -Attachment $createdAttachment `
                -PackageName $PackageName `
                -ExerciseListFallback ([bool]$ExerciseAttachmentListFallback) `
                -Steps $steps
        }
    }


    $resultStatus = 'PASS'
}
catch {
    $errorMessage = $_.Exception.Message
    $steps.Add([pscustomobject]@{ Step = 'error'; Result = 'FAIL'; Detail = $errorMessage })
}
finally {
    if ($localApiStoppedForExercise) {
        try {
            $restart = Start-LocalApiForBaseUrl `
                -BaseUrl $BaseUrl `
                -ProjectRoot $ProjectRoot `
                -DotNetPath $DotNetPath `
                -LocalApiProjectFile $LocalApiProjectFile `
                -Username $Username `
                -Password $Password `
                -EvidenceDirectory $EvidenceDirectory `
                -Timestamp $timestamp
            $localApiStoppedForExercise = $false
            $cleanupSteps.Add([pscustomobject]@{ Target = 'local-api'; Id = $restart.ProcessId; Result = 'restarted-before-cleanup' })
        }
        catch {
            $cleanupSteps.Add([pscustomobject]@{ Target = 'local-api'; Id = ''; Result = "restart-failed-before-cleanup: $($_.Exception.Message)" })
        }
    }

    if (-not $KeepTemporaryData) {
        try {
            if (-not $headers) {
                $headers = New-ApiSession -BaseUrl $BaseUrl -Username $Username -Password $Password
            }
            if ($createdInvoice -and $createdInvoice.id) {
                Remove-TestPaymentData -BaseUrl $BaseUrl -Headers $headers -InvoiceId $createdInvoice.id -CleanupSteps $cleanupSteps
            }
            Remove-TestData -BaseUrl $BaseUrl -Headers $headers -Fixture $fixture -CleanupSteps $cleanupSteps
        }
        catch {
            $cleanupSteps.Add([pscustomobject]@{ Target = 'cleanup'; Id = ''; Result = "cleanup-failed: $($_.Exception.Message)" })
        }
    }
}

$result = [pscustomobject]@{
    CreatedAt = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    BaseUrl = $BaseUrl
    PackageName = $PackageName
    VoucherKind = $VoucherKind
    ExerciseStoppedServerDirtySync = [bool]$ExerciseStoppedServerDirtySync
    ExerciseNonRetryableSaveFaultStatus = $ExerciseNonRetryableSaveFaultStatus
    ExerciseAttachmentOpenUi = [bool]$ExerciseAttachmentOpenUi
    ExerciseAttachmentListFallback = [bool]$ExerciseAttachmentListFallback
    Result = $resultStatus
    Error = $errorMessage
    Fixture = if ($fixture) { [pscustomobject]@{ CustomerId = $fixture.Customer.id; CustomerName = $fixture.CustomerName; ItemId = $fixture.Item.id; ItemName = $fixture.ItemName } } else { $null }
    CreatedInvoiceId = if ($createdInvoice) { $createdInvoice.id } else { $null }
    CreatedPaymentId = if ($createdPayment) { $createdPayment.id } else { $null }
    CreatedTransactionId = if ($createdTransaction) { $createdTransaction.id } else { $null }
    CreatedAttachmentId = if ($createdAttachment) { $createdAttachment.id } else { $null }
    AttachmentMode = $attachmentModeText
    AttachmentFileName = if ($createdAttachment) { $createdAttachment.fileName } elseif ($attachmentFixture) { $attachmentFixture.FileName } else { $null }
    AttachmentMimeType = if ($createdAttachment) { $createdAttachment.mimeType } else { $null }
    AttachmentContentPath = if ($createdAttachmentContent) { $createdAttachmentContent.Path } else { $null }
    AttachmentContentSha256 = if ($createdAttachmentContent) { $createdAttachmentContent.Sha256 } else { $null }
    Steps = $steps
    Cleanup = $cleanupSteps
}

$jsonPath = Join-Path $EvidenceDirectory "mobile-payment-e2e-$voucherSlug-$timestamp.json"
$mdPath = Join-Path $EvidenceDirectory "mobile-payment-e2e-$voucherSlug-$timestamp.md"
$result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

$mdLines = @(
    "# 모바일 Android $voucherKorean $paymentAction 입력 E2E 검증",
    '',
    "- 작성시각: $($result.CreatedAt)",
    "- API: $BaseUrl",
    "- 패키지: $PackageName",
    "- 전표유형: $VoucherKind",
    "- 기본 방식: $expectedMethod",
    "- 실제 API 중단 dirty 동기화 실행: $([bool]$ExerciseStoppedServerDirtySync)",
    "- PDF attachment upload exercised: $([bool]$ExerciseAttachmentUpload)",
    "- Camera attachment upload exercised: $([bool]$ExerciseCameraAttachmentUpload)",
    "- Attachment open UI exercised: $([bool]$ExerciseAttachmentOpenUi)",
    "- Attachment list fallback exercised: $([bool]$ExerciseAttachmentListFallback)",
    "- Attachment mode: $attachmentModeText",
    "- 결과: $resultStatus",
    "- 오류: $errorMessage",
    '',
    '## 테스트 데이터',
    "- 거래처: $($result.Fixture.CustomerName) / $($result.Fixture.CustomerId)",
    "- 품목: $($result.Fixture.ItemName) / $($result.Fixture.ItemId)",
    "- 대상 전표: $($result.CreatedInvoiceId)",
    "- 생성 수금/지급: $($result.CreatedPaymentId)",
    "- 연결 거래내역: $($result.CreatedTransactionId)",
    "- 생성 첨부: $($result.CreatedAttachmentId) / $($result.AttachmentFileName) / $($result.AttachmentMimeType)",
    '',
    '## 단계',
    ''
)
foreach ($step in $steps) {
    $mdLines += "- $($step.Step): $($step.Result) — $($step.Detail)"
}
$mdLines += ''
$mdLines += '## 정리'
foreach ($cleanup in $cleanupSteps) {
    $mdLines += "- $($cleanup.Target): $($cleanup.Id) — $($cleanup.Result)"
}
$mdLines += ''
$mdLines += "JSON: $jsonPath"
$mdLines | Set-Content -LiteralPath $mdPath -Encoding UTF8

Write-Host "mobile_payment_e2e_report=$mdPath"
Write-Host "mobile_payment_e2e_json=$jsonPath"
Write-Host "result=$resultStatus"
if ($resultStatus -ne 'PASS') {
    exit 1
}
