param(
    [string]$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [string]$AdbPath,
    [string]$ApkAnalyzerPath,
    [string]$JavaSdkDirectory,
    [string]$ApkPath,
    [string]$PackageName = 'kr.georaeplan.mobile',
    [string]$Username = 'usenet',
    [string]$Password = '1234',
    [string]$EvidenceDirectory,
    [switch]$SkipInstall,
    [switch]$RequireUpdateInPlace,
    [switch]$IncludeDraftScreens,
    [switch]$ExerciseSyncNow,
    [ValidateSet('', '400', '401', '403', '404', '422')]
    [string]$ExerciseMasterDataNonRetryableSaveFaultStatus = '',
    [string]$SyncExerciseBaseUrl = 'http://127.0.0.1:19080'
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

function Resolve-ApkAnalyzerPath {
    param(
        [string]$ProjectRoot,
        [string]$RequestedPath
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        if (-not (Test-Path -LiteralPath $RequestedPath -PathType Leaf)) {
            throw "지정한 apkanalyzer를 찾지 못했습니다: $RequestedPath"
        }
        return (Resolve-Path -LiteralPath $RequestedPath).Path
    }

    $sdkCandidates = [System.Collections.Generic.List[string]]::new()
    foreach ($candidate in @($env:ANDROID_SDK_ROOT, $env:ANDROID_HOME)) {
        if (-not [string]::IsNullOrWhiteSpace($candidate)) {
            $sdkCandidates.Add($candidate) | Out-Null
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        $sdkCandidates.Add((Join-Path $env:LOCALAPPDATA 'Android\Sdk')) | Out-Null
        $sdkCandidates.Add((Join-Path $env:LOCALAPPDATA 'GeoraePlan.Android\android-sdk')) | Out-Null
    }
    $sdkCandidates.Add((Join-Path $ProjectRoot '.android-sdk')) | Out-Null
    $sdkCandidates.Add((Join-Path $ProjectRoot '.tooling\android-sdk')) | Out-Null

    foreach ($sdkRoot in $sdkCandidates | Select-Object -Unique) {
        if ([string]::IsNullOrWhiteSpace($sdkRoot) -or -not (Test-Path -LiteralPath $sdkRoot -PathType Container)) {
            continue
        }

        $directCandidates = @(
            (Join-Path $sdkRoot 'cmdline-tools\latest\bin\apkanalyzer.bat'),
            (Join-Path $sdkRoot 'cmdline-tools\latest\bin\apkanalyzer'),
            (Join-Path $sdkRoot 'tools\bin\apkanalyzer.bat'),
            (Join-Path $sdkRoot 'tools\bin\apkanalyzer')
        )
        foreach ($candidate in $directCandidates) {
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                return (Resolve-Path -LiteralPath $candidate).Path
            }
        }

        $commandLineToolsRoot = Join-Path $sdkRoot 'cmdline-tools'
        if (-not (Test-Path -LiteralPath $commandLineToolsRoot -PathType Container)) {
            continue
        }

        $analyzer = Get-ChildItem -LiteralPath $commandLineToolsRoot -File -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -in @('apkanalyzer.bat', 'apkanalyzer') } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($null -ne $analyzer) {
            return $analyzer.FullName
        }
    }

    throw 'apkanalyzer를 찾지 못했습니다. Android SDK command-line tools를 설치하거나 -ApkAnalyzerPath를 지정하세요.'
}

function ConvertTo-PositiveAndroidVersionCode {
    param(
        [Parameter(Mandatory = $true)][string]$Value,
        [Parameter(Mandatory = $true)][string]$SourceName
    )

    $normalized = $Value.Trim()
    [long]$versionCode = 0
    if ($normalized -notmatch '^\d+$' -or
        -not [long]::TryParse($normalized, [ref]$versionCode) -or
        $versionCode -le 0) {
        throw "$SourceName versionCode가 양의 정수가 아닙니다."
    }

    return $versionCode
}

function Resolve-JavaHomeForApkAnalyzer {
    param([string]$RequestedPath)

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $requestedJava = Join-Path $RequestedPath 'bin\java.exe'
        if (-not (Test-Path -LiteralPath $requestedJava -PathType Leaf)) {
            $requestedJava = Join-Path $RequestedPath 'bin\java'
        }
        if (-not (Test-Path -LiteralPath $requestedJava -PathType Leaf)) {
            throw "지정한 Java SDK를 찾지 못했습니다: $RequestedPath"
        }
        return (Resolve-Path -LiteralPath $RequestedPath).Path
    }

    $candidates = [System.Collections.Generic.List[string]]::new()
    foreach ($candidate in @($env:JAVA_HOME)) {
        if (-not [string]::IsNullOrWhiteSpace($candidate)) {
            $candidates.Add($candidate) | Out-Null
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($env:ProgramFiles)) {
        $candidates.Add((Join-Path $env:ProgramFiles 'Android\Android Studio\jbr')) | Out-Null
    }
    if (-not [string]::IsNullOrWhiteSpace(${env:ProgramFiles(x86)})) {
        $candidates.Add((Join-Path ${env:ProgramFiles(x86)} 'Android\Android Studio\jbr')) | Out-Null
    }
    if (-not [string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        $candidates.Add((Join-Path $env:LOCALAPPDATA 'Programs\Android Studio\jbr')) | Out-Null
    }

    foreach ($commandName in @('java', 'javac', 'keytool')) {
        $command = Get-Command $commandName -ErrorAction SilentlyContinue
        if ($null -ne $command) {
            $candidates.Add((Split-Path -Parent (Split-Path -Parent $command.Source))) | Out-Null
        }
    }

    foreach ($candidate in $candidates | Select-Object -Unique) {
        if ([string]::IsNullOrWhiteSpace($candidate)) {
            continue
        }

        $javaExecutable = Join-Path $candidate 'bin\java.exe'
        if (-not (Test-Path -LiteralPath $javaExecutable -PathType Leaf)) {
            $javaExecutable = Join-Path $candidate 'bin\java'
        }
        if (Test-Path -LiteralPath $javaExecutable -PathType Leaf) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw 'apkanalyzer 실행용 Java를 찾지 못했습니다. JDK 17+를 설치하거나 -JavaSdkDirectory를 지정하세요.'
}

function Get-ApkManifestMetadata {
    param(
        [Parameter(Mandatory = $true)][string]$ApkAnalyzerPath,
        [Parameter(Mandatory = $true)][string]$ApkPath,
        [Parameter(Mandatory = $true)][string]$JavaHome
    )

    $previousJavaHome = $env:JAVA_HOME
    $previousPath = $env:PATH
    try {
        $env:JAVA_HOME = $JavaHome
        $env:PATH = (Join-Path $JavaHome 'bin') + [System.IO.Path]::PathSeparator + $env:PATH

        $applicationIdOutput = & $ApkAnalyzerPath manifest application-id $ApkPath 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "apkanalyzer application-id 조회 실패(exit=$LASTEXITCODE)."
        }
        $applicationId = (($applicationIdOutput | Out-String).Trim())
        if ($applicationId -notmatch '^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z][A-Za-z0-9_]*)+$') {
            throw 'APK applicationId를 단일 유효 값으로 확인하지 못했습니다.'
        }

        $versionCodeOutput = & $ApkAnalyzerPath manifest version-code $ApkPath 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "apkanalyzer version-code 조회 실패(exit=$LASTEXITCODE)."
        }

        return [pscustomobject]@{
            ApplicationId = $applicationId
            VersionCode = ConvertTo-PositiveAndroidVersionCode `
                -Value (($versionCodeOutput | Out-String).Trim()) `
                -SourceName 'APK'
        }
    }
    finally {
        $env:JAVA_HOME = $previousJavaHome
        $env:PATH = $previousPath
    }
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

function Assert-LocalSyncExerciseTarget {
    param([string]$BaseUrl)

    $uri = $null
    if (-not [Uri]::TryCreate($BaseUrl, [UriKind]::Absolute, [ref]$uri)) {
        throw "수동 동기화 실기동 검증 BaseUrl이 올바른 URI가 아닙니다: $BaseUrl"
    }

    $isLoopbackHost = [string]::Equals($uri.Host, '127.0.0.1', [StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($uri.Host, 'localhost', [StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($uri.Host, '::1', [StringComparison]::OrdinalIgnoreCase)

    if (-not $isLoopbackHost) {
        throw "수동 동기화 실기동 검증은 로컬 테스트 API에서만 허용됩니다. 현재 BaseUrl: $BaseUrl"
    }

    $healthUri = $uri.ToString().TrimEnd('/') + '/healthz'
    try {
        Invoke-RestMethod -Method Get -Uri $healthUri -TimeoutSec 10 | Out-Null
    }
    catch {
        throw "로컬 테스트 API healthz 확인 실패: $healthUri`n$($_.Exception.Message)"
    }
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

function Install-MobileApk {
    param(
        [Parameter(Mandatory = $true)][string]$AdbPath,
        [Parameter(Mandatory = $true)][string]$DeviceId,
        [Parameter(Mandatory = $true)][string]$ApkPath,
        [Parameter(Mandatory = $true)][string]$PackageName,
        [switch]$RequireUpdateInPlace
    )

    $installArgs = @('-s', $DeviceId, 'install', '-r')
    if (-not $RequireUpdateInPlace) {
        $installArgs += '-d'
    }
    $installArgs += $ApkPath

    if (-not $RequireUpdateInPlace) {
        try { Invoke-Adb -AdbPath $AdbPath -Arguments @('-s', $DeviceId, 'shell', 'pm', 'trim-caches', '1024M') | Out-Null } catch {}
    }

    try {
        Invoke-Adb -AdbPath $AdbPath -Arguments $installArgs | Out-Null
    }
    catch {
        $message = $_.Exception.Message
        if ($RequireUpdateInPlace) {
            throw "Android update-in-place install failed and uninstall fallback is disabled for delivery verification. Package: $PackageName`n$message"
        }

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

function Assert-MobilePackageInstalled {
    param(
        [Parameter(Mandatory = $true)][string]$AdbPath,
        [Parameter(Mandatory = $true)][string]$DeviceId,
        [Parameter(Mandatory = $true)][string]$PackageName
    )

    try {
        $output = Invoke-Adb -AdbPath $AdbPath -Arguments @('-s', $DeviceId, 'shell', 'pm', 'path', $PackageName)
        if (($output -join "`n") -match "^package:") {
            return
        }
    }
    catch {
        throw "Android update-in-place 검증은 기존 설치본이 있어야 합니다. 패키지를 먼저 설치한 뒤 다시 실행하세요: $PackageName`n$($_.Exception.Message)"
    }

    throw "Android update-in-place 검증은 기존 설치본이 있어야 합니다. 패키지를 먼저 설치한 뒤 다시 실행하세요: $PackageName"
}

function Get-InstalledMobileVersionCode {
    param(
        [Parameter(Mandatory = $true)][string]$AdbPath,
        [Parameter(Mandatory = $true)][string]$DeviceId,
        [Parameter(Mandatory = $true)][string]$PackageName
    )

    $output = Invoke-Adb -AdbPath $AdbPath -Arguments @(
        '-s',
        $DeviceId,
        'shell',
        'dumpsys',
        'package',
        $PackageName)
    $text = ($output -join "`n")
    $match = [regex]::Match(
        $text,
        '(?m)^\s*versionCode=(?<value>\d+)\b')
    if (-not $match.Success) {
        throw "설치된 Android 패키지의 versionCode를 확인하지 못했습니다: $PackageName"
    }

    return ConvertTo-PositiveAndroidVersionCode `
        -Value $match.Groups['value'].Value `
        -SourceName '설치본'
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
        throw "Android launcher activity? ?? ??? ?????: $PackageName"
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
        [int]$TimeoutSeconds = 60,
        [switch]$AllowTimeout
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
        if ($AllowTimeout) {
            return $null
        }
        Assert-UiContains -Content $lastDump.Content -Needles $Needles -StepName $StepName
        return $lastDump
    }

    if ($AllowTimeout) {
        return $null
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

function Get-LoginEditTextNode {
    param(
        [string]$Content,
        [bool]$IsPassword,
        [switch]$RequireFocused
    )

    $expectedPassword = if ($IsPassword) { 'true' } else { 'false' }
    $candidates = @()
    foreach ($match in [regex]::Matches($Content, '<node\b[^>]*>')) {
        $node = $match.Value
        if ($node -notmatch 'class="android\.widget\.EditText"') {
            continue
        }
        if ($node -notmatch "password=`"$expectedPassword`"") {
            continue
        }
        if ($RequireFocused -and $node -notmatch "focused=`"true`"") {
            continue
        }
        if ($node -notmatch 'bounds="\[(\d+),(\d+)\]\[(\d+),(\d+)\]"') {
            continue
        }

        $x1 = [int]$Matches[1]
        $y1 = [int]$Matches[2]
        $x2 = [int]$Matches[3]
        $y2 = [int]$Matches[4]
        $textMatch = [regex]::Match($node, 'text="([^"]*)"')
        $hintMatch = [regex]::Match($node, 'hint="([^"]*)"')
        $candidates += [pscustomobject]@{
            Point = [pscustomobject]@{
                X = [int](($x1 + $x2) / 2)
                Y = [int](($y1 + $y2) / 2)
            }
            Text = if ($textMatch.Success) { $textMatch.Groups[1].Value } else { '' }
            Hint = if ($hintMatch.Success) { $hintMatch.Groups[1].Value } else { '' }
        }
    }

    if ($candidates.Count -gt 1) {
        throw "multiple login fields matched password role: $expectedPassword"
    }
    if ($candidates.Count -eq 1) {
        return $candidates[0]
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

function Get-UiTextPoint {
    param(
        [string]$AdbPath,
        [string]$DeviceId,
        [string]$EvidenceDirectory,
        [string]$Name,
        [string]$Text,
        [string]$ClassName,
        [object]$Screen,
        [int]$MinY = 0,
        [switch]$AllowScroll,
        [int]$MaxScrolls = 4
    )

    $attemptLimit = if ($AllowScroll) { [Math]::Max(0, $MaxScrolls) } else { 0 }
    $lastDump = $null

    for ($attempt = 0; $attempt -le $attemptLimit; $attempt++) {
        $dump = Get-UiDump -AdbPath $AdbPath -DeviceId $DeviceId -EvidenceDirectory $EvidenceDirectory -Name "$Name-$attempt"
        $lastDump = $dump
        if (Dismiss-AndroidAnrDialog -AdbPath $AdbPath -DeviceId $DeviceId -Content $dump.Content) {
            Start-Sleep -Seconds 3
            continue
        }

        $point = Get-NodeCenterByText -Content $dump.Content -Text $Text -ClassName $ClassName -MinY $MinY
        if (-not $point -and -not [string]::IsNullOrWhiteSpace($ClassName)) {
            $point = Get-NodeCenterByText -Content $dump.Content -Text $Text -ClassName '' -MinY $MinY
        }

        if ($point) {
            return [pscustomobject]@{
                Point = $point
                Dump = $dump
            }
        }

        if (-not $AllowScroll -or $attempt -eq $attemptLimit) {
            break
        }

        $x = [int]($Screen.Width * 0.50)
        $startY = [int]($Screen.Height * 0.80)
        $endY = [int]($Screen.Height * 0.30)
        Invoke-Adb -AdbPath $AdbPath -Arguments @('-s', $DeviceId, 'shell', 'input', 'swipe', "$x", "$startY", "$x", "$endY", '450') | Out-Null
        Start-Sleep -Milliseconds 700
    }

    $detail = if ($lastDump) { $lastDump.Path } else { 'UI dump 없음' }
    throw "UI에서 '$Text' 항목을 찾지 못했습니다. 덤프: $detail"
}

function Tap-UiText {
    param(
        [string]$AdbPath,
        [string]$DeviceId,
        [string]$EvidenceDirectory,
        [string]$Name,
        [string]$Text,
        [string]$ClassName,
        [object]$Screen,
        [int]$MinY = 0,
        [switch]$AllowScroll,
        [int]$MaxScrolls = 4
    )

    $result = Get-UiTextPoint `
        -AdbPath $AdbPath `
        -DeviceId $DeviceId `
        -EvidenceDirectory $EvidenceDirectory `
        -Name $Name `
        -Text $Text `
        -ClassName $ClassName `
        -Screen $Screen `
        -MinY $MinY `
        -AllowScroll:$AllowScroll `
        -MaxScrolls $MaxScrolls

    Tap-Point -AdbPath $AdbPath -DeviceId $DeviceId -X $result.Point.X -Y $result.Point.Y
    Start-Sleep -Milliseconds 500
    return $result.Dump
}

function Set-MobileTextEntry {
    param(
        [string]$AdbPath,
        [string]$DeviceId,
        [string]$EvidenceDirectory,
        [string]$Timestamp,
        [string]$FieldName,
        [string]$Value,
        [object]$Screen
    )

    Tap-UiText `
        -AdbPath $AdbPath `
        -DeviceId $DeviceId `
        -EvidenceDirectory $EvidenceDirectory `
        -Name "mobile-smoke-$Timestamp-field-$FieldName" `
        -Text $FieldName `
        -ClassName 'android.widget.EditText' `
        -Screen $Screen | Out-Null
    Clear-AndroidTextField -AdbPath $AdbPath -DeviceId $DeviceId
    Set-AndroidTextSlow -AdbPath $AdbPath -DeviceId $DeviceId -Text $Value
    Invoke-Adb -AdbPath $AdbPath -Arguments @('-s', $DeviceId, 'shell', 'input', 'keyevent', 'KEYCODE_ESCAPE') | Out-Null
    Start-Sleep -Milliseconds 800
}

function Dismiss-MobileAlert {
    param(
        [string]$AdbPath,
        [string]$DeviceId,
        [string]$EvidenceDirectory,
        [string]$Timestamp,
        [string]$AlertTitle,
        [object]$Screen
    )

    $dump = Wait-UiContainsAll `
        -AdbPath $AdbPath `
        -DeviceId $DeviceId `
        -EvidenceDirectory $EvidenceDirectory `
        -Name "mobile-smoke-$Timestamp-alert-$AlertTitle" `
        -Needles @($AlertTitle, '확인') `
        -StepName "$AlertTitle 알림 확인" `
        -TimeoutSeconds 20

    Tap-UiText `
        -AdbPath $AdbPath `
        -DeviceId $DeviceId `
        -EvidenceDirectory $EvidenceDirectory `
        -Name "mobile-smoke-$Timestamp-alert-$AlertTitle-confirm" `
        -Text '확인' `
        -ClassName 'android.widget.Button' `
        -Screen $Screen | Out-Null

    return $dump
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
        [bool]$IsPassword,
        [string]$Value,
        [switch]$VerifyPlainText
    )

    $lastDump = $null
    $focusWasConfirmed = $false
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        $safeFieldName = $FieldName -replace '[^a-zA-Z0-9_-]', '-'
        $beforeDump = Get-UiDump -AdbPath $AdbPath -DeviceId $DeviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-smoke-$Timestamp-login-$safeFieldName-before$attempt"
        $fieldNode = Get-LoginEditTextNode -Content $beforeDump.Content -IsPassword $IsPassword
        if (-not $fieldNode) {
            throw "login field not found: $FieldName"
        }

        Tap-Point -AdbPath $AdbPath -DeviceId $DeviceId -X $fieldNode.Point.X -Y $fieldNode.Point.Y
        Start-Sleep -Milliseconds 700
        $focusDump = Get-UiDump -AdbPath $AdbPath -DeviceId $DeviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-smoke-$Timestamp-login-$safeFieldName-focus$attempt"
        $focusedNode = Get-LoginEditTextNode -Content $focusDump.Content -IsPassword $IsPassword -RequireFocused
        if (-not $focusedNode) {
            continue
        }

        $focusWasConfirmed = $true
        Clear-AndroidTextField -AdbPath $AdbPath -DeviceId $DeviceId
        Set-AndroidTextSlow -AdbPath $AdbPath -DeviceId $DeviceId -Text $Value
        Start-Sleep -Milliseconds 700

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

            continue
        }

        $typedNode = Get-LoginEditTextNode -Content $lastDump.Content -IsPassword $IsPassword
        $valueConfirmed = $typedNode -and (
            ($VerifyPlainText -and $typedNode.Text -eq $Value) -or
            ($IsPassword -and $typedNode.Text.Length -eq $Value.Length -and $typedNode.Text -ne $typedNode.Hint)
        )
        if ($valueConfirmed) {
            return $lastDump
        }
    }

    if (-not $focusWasConfirmed) {
        throw "login field focus not confirmed: $FieldName"
    }
    throw "login field value not confirmed: $FieldName"
}

function Dismiss-AndroidAnrDialog {
    param(
        [string]$AdbPath,
        [string]$DeviceId,
        [string]$Content,
        [switch]$AllowTargetAppRecovery
    )

    if (-not $Content.Contains("isn't responding")) {
        return $false
    }

    $isLauncherAnr = $Content.Contains("Pixel Launcher isn't responding") -or
        $Content.Contains('com.google.android.apps.nexuslauncher')
    if (-not $isLauncherAnr -and -not $AllowTargetAppRecovery) {
        throw '거래플랜 Android smoke 중 대상 앱 ANR을 감지했습니다. 자동으로 숨기지 않고 실패 처리합니다.'
    }

    $buttonText = 'Close app'
    $buttonPoint = Get-NodeCenterByText -Content $Content -Text $buttonText -ClassName 'android.widget.Button'
    if (-not $buttonPoint) {
        $buttonPoint = Get-NodeCenterByText -Content $Content -Text 'Wait' -ClassName 'android.widget.Button'
    }
    if (-not $buttonPoint) {
        return $false
    }

    Tap-Point -AdbPath $AdbPath -DeviceId $DeviceId -X $buttonPoint.X -Y $buttonPoint.Y
    if (-not [string]::IsNullOrWhiteSpace($script:GeoraePlanMobilePackageName)) {
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

    $afterTapDump = Get-UiDump -AdbPath $AdbPath -DeviceId $DeviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-smoke-$Timestamp-after-tap-$StepName"
    $missingAfterTap = @()
    foreach ($needle in $Needles) {
        if (-not $afterTapDump.Content.Contains($needle)) {
            $missingAfterTap += $needle
        }
    }

    for ($menuAttempt = 1; $menuAttempt -le 2 -and $missingAfterTap.Count -gt 0 -and
        ($afterTapDump.Content.Contains('design_bottom_sheet') -or $afterTapDump.Content.Contains('touch_outside')); $menuAttempt++) {
        $tabPoint = Get-NodeCenterByText -Content $afterTapDump.Content -Text $TabText -ClassName 'android.widget.TextView'
        if (-not $tabPoint) {
            $tabPoint = Get-NodeCenterByText -Content $afterTapDump.Content -Text $TabText -ClassName ''
        }

        if (-not $tabPoint) {
            break
        }

        Tap-Point -AdbPath $AdbPath -DeviceId $DeviceId -X $tabPoint.X -Y $tabPoint.Y
        Start-Sleep -Seconds 2
        $afterTapDump = Get-UiDump -AdbPath $AdbPath -DeviceId $DeviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-smoke-$Timestamp-after-menu-$StepName-$menuAttempt"
        $missingAfterTap = @()
        foreach ($needle in $Needles) {
            if (-not $afterTapDump.Content.Contains($needle)) {
                $missingAfterTap += $needle
            }
        }
    }

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

function Invoke-MasterDataNonRetryableSaveRejection {
    param(
        [string]$AdbPath,
        [string]$DeviceId,
        [string]$PackageName,
        [string]$EvidenceDirectory,
        [string]$Timestamp,
        [object]$Screen,
        [string]$FaultStatus,
        [System.Collections.Generic.List[object]]$Steps
    )

    if ([string]::IsNullOrWhiteSpace($FaultStatus)) {
        return
    }

    $uniqueSuffix = Get-Date -Format 'HHmmssfff'

    Open-BottomTabAndAssert `
        -AdbPath $AdbPath `
        -DeviceId $DeviceId `
        -EvidenceDirectory $EvidenceDirectory `
        -Timestamp $Timestamp `
        -Screen $Screen `
        -TabText '거래처' `
        -FallbackXRatio 0.30 `
        -StepName 'mobile-nonretryable-customer-tab' `
        -Needles @('거래처', '거래처명 / 전화 / 사업자번호') `
        -Steps $Steps | Out-Null

    Tap-UiText `
        -AdbPath $AdbPath `
        -DeviceId $DeviceId `
        -EvidenceDirectory $EvidenceDirectory `
        -Name "mobile-smoke-$Timestamp-customer-new-button" `
        -Text '신규' `
        -ClassName 'android.widget.Button' `
        -Screen $Screen | Out-Null

    Wait-UiContainsAll `
        -AdbPath $AdbPath `
        -DeviceId $DeviceId `
        -EvidenceDirectory $EvidenceDirectory `
        -Name "mobile-smoke-$Timestamp-customer-edit-new" `
        -Needles @('새 거래처 등록', '거래처명', '저장') `
        -StepName 'mobile-nonretryable-customer-edit-open' | Out-Null

    Set-MobileTextEntry `
        -AdbPath $AdbPath `
        -DeviceId $DeviceId `
        -EvidenceDirectory $EvidenceDirectory `
        -Timestamp $Timestamp `
        -FieldName '거래처명' `
        -Value "GP${FaultStatus}Customer$uniqueSuffix" `
        -Screen $Screen

    Set-MobileDiagnosticFault -AdbPath $AdbPath -DeviceId $DeviceId -PackageName $PackageName -Mode $FaultStatus -Target 'customers'
    $Steps.Add([pscustomobject]@{ Step = 'mobile-nonretryable-customer-fault-before-save'; Result = 'PASS'; Detail = "$FaultStatus|customers" })

    Tap-UiText `
        -AdbPath $AdbPath `
        -DeviceId $DeviceId `
        -EvidenceDirectory $EvidenceDirectory `
        -Name "mobile-smoke-$Timestamp-customer-save-button" `
        -Text '저장' `
        -ClassName 'android.widget.Button' `
        -Screen $Screen `
        -AllowScroll | Out-Null

    Wait-UiContainsAll `
        -AdbPath $AdbPath `
        -DeviceId $DeviceId `
        -EvidenceDirectory $EvidenceDirectory `
        -Name "mobile-smoke-$Timestamp-customer-save-rejected" `
        -Needles @('거래처 저장 실패') `
        -StepName 'mobile-nonretryable-customer-save-rejected' `
        -TimeoutSeconds 30 | Out-Null
    $Steps.Add([pscustomobject]@{ Step = 'mobile-nonretryable-customer-save-rejected'; Result = 'PASS'; Detail = '거래처 저장 실패' })
    Dismiss-MobileAlert -AdbPath $AdbPath -DeviceId $DeviceId -EvidenceDirectory $EvidenceDirectory -Timestamp $Timestamp -AlertTitle '거래처 저장 실패' -Screen $Screen | Out-Null

    Tap-UiText `
        -AdbPath $AdbPath `
        -DeviceId $DeviceId `
        -EvidenceDirectory $EvidenceDirectory `
        -Name "mobile-smoke-$Timestamp-customer-cancel-button" `
        -Text '취소' `
        -ClassName 'android.widget.Button' `
        -Screen $Screen `
        -AllowScroll | Out-Null

    Open-BottomTabAndAssert `
        -AdbPath $AdbPath `
        -DeviceId $DeviceId `
        -EvidenceDirectory $EvidenceDirectory `
        -Timestamp $Timestamp `
        -Screen $Screen `
        -TabText '품목' `
        -FallbackXRatio 0.50 `
        -StepName 'mobile-nonretryable-item-tab' `
        -Needles @('품목 검색', '품목분류') `
        -Steps $Steps | Out-Null

    try {
        Tap-UiText `
            -AdbPath $AdbPath `
            -DeviceId $DeviceId `
            -EvidenceDirectory $EvidenceDirectory `
            -Name "mobile-smoke-$Timestamp-item-new-category-button" `
            -Text '신규 품목' `
            -ClassName 'android.widget.Button' `
            -Screen $Screen | Out-Null
    }
    catch {
        Tap-UiText `
            -AdbPath $AdbPath `
            -DeviceId $DeviceId `
            -EvidenceDirectory $EvidenceDirectory `
            -Name "mobile-smoke-$Timestamp-item-new-button" `
            -Text '신규' `
            -ClassName 'android.widget.Button' `
            -Screen $Screen | Out-Null
    }

    Wait-UiContainsAll `
        -AdbPath $AdbPath `
        -DeviceId $DeviceId `
        -EvidenceDirectory $EvidenceDirectory `
        -Name "mobile-smoke-$Timestamp-item-edit-new" `
        -Needles @('새 품목 등록', '품명', '저장') `
        -StepName 'mobile-nonretryable-item-edit-open' | Out-Null

    Set-MobileTextEntry `
        -AdbPath $AdbPath `
        -DeviceId $DeviceId `
        -EvidenceDirectory $EvidenceDirectory `
        -Timestamp $Timestamp `
        -FieldName '품명' `
        -Value "GP${FaultStatus}Item$uniqueSuffix" `
        -Screen $Screen

    Set-MobileDiagnosticFault -AdbPath $AdbPath -DeviceId $DeviceId -PackageName $PackageName -Mode $FaultStatus -Target 'items'
    $Steps.Add([pscustomobject]@{ Step = 'mobile-nonretryable-item-fault-before-save'; Result = 'PASS'; Detail = "$FaultStatus|items" })

    Tap-UiText `
        -AdbPath $AdbPath `
        -DeviceId $DeviceId `
        -EvidenceDirectory $EvidenceDirectory `
        -Name "mobile-smoke-$Timestamp-item-save-button" `
        -Text '저장' `
        -ClassName 'android.widget.Button' `
        -Screen $Screen `
        -AllowScroll | Out-Null

    Wait-UiContainsAll `
        -AdbPath $AdbPath `
        -DeviceId $DeviceId `
        -EvidenceDirectory $EvidenceDirectory `
        -Name "mobile-smoke-$Timestamp-item-save-rejected" `
        -Needles @('품목 저장 실패') `
        -StepName 'mobile-nonretryable-item-save-rejected' `
        -TimeoutSeconds 30 | Out-Null
    $Steps.Add([pscustomobject]@{ Step = 'mobile-nonretryable-item-save-rejected'; Result = 'PASS'; Detail = '품목 저장 실패' })
    Dismiss-MobileAlert -AdbPath $AdbPath -DeviceId $DeviceId -EvidenceDirectory $EvidenceDirectory -Timestamp $Timestamp -AlertTitle '품목 저장 실패' -Screen $Screen | Out-Null

    Tap-UiText `
        -AdbPath $AdbPath `
        -DeviceId $DeviceId `
        -EvidenceDirectory $EvidenceDirectory `
        -Name "mobile-smoke-$Timestamp-item-cancel-button" `
        -Text '취소' `
        -ClassName 'android.widget.Button' `
        -Screen $Screen `
        -AllowScroll | Out-Null

    Open-BottomTabAndAssert `
        -AdbPath $AdbPath `
        -DeviceId $DeviceId `
        -EvidenceDirectory $EvidenceDirectory `
        -Timestamp $Timestamp `
        -Screen $Screen `
        -TabText '동기화' `
        -FallbackXRatio 0.84 `
        -StepName 'mobile-nonretryable-master-data-rejection-not-dirty' `
        -Needles @('동기화 상태', '저장 대기', '거래처 0건', '품목 0건') `
        -Steps $Steps | Out-Null
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
        $freshDump = Get-UiDump -AdbPath $AdbPath -DeviceId $DeviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-smoke-$Timestamp-sync-now-before-tap"
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
        -Name "mobile-smoke-$Timestamp-sync-now" `
        -Needles @('권장 동기화 완료', '저장 대기: 설정 0건', '거래처기준 0건', '거래처 0건', '품목 0건', '전표 0건', '서버에서 받기', '서버에 올리기') `
        -StepName 'sync-now' `
        -TimeoutSeconds 120

    $Steps.Add([pscustomobject]@{ Step = 'sync-now'; Result = 'PASS'; Detail = $dump.Path })
    return $dump
}

function Open-HomeActionAndAssert {
    param(
        [string]$AdbPath,
        [string]$DeviceId,
        [string]$EvidenceDirectory,
        [string]$Timestamp,
        [pscustomobject]$Screen,
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
    # 상세 화면의 큰 MAUI 시각 트리를 제거하는 동안 두 번째 터치 입력을 연속으로
    # 보내면 저사양 기기에서 입력 ANR을 유발할 수 있다. 먼저 뒤로 가기 완료를
    # 기다리고, 실제 홈 복귀가 되지 않았을 때만 하단 홈 탭을 보조 경로로 사용한다.
    $homeAgain = Wait-UiContainsAll `
        -AdbPath $AdbPath `
        -DeviceId $DeviceId `
        -EvidenceDirectory $EvidenceDirectory `
        -Name "mobile-smoke-$Timestamp-after-$safeStepName-back" `
        -Needles @('홈', '판매 작성', '구매 작성', '수금/지급') `
        -StepName "$StepName 이후 뒤로 가기 홈 복귀" `
        -TimeoutSeconds 15 `
        -AllowTimeout
    if ($homeAgain) {
        return $homeAgain.Content
    }

    Tap-BottomTab -AdbPath $AdbPath -DeviceId $DeviceId -Screen $Screen -XRatio 0.10
    $homeAgain = Wait-UiContainsAll `
        -AdbPath $AdbPath `
        -DeviceId $DeviceId `
        -EvidenceDirectory $EvidenceDirectory `
        -Name "mobile-smoke-$Timestamp-after-$safeStepName" `
        -Needles @('홈', '판매 작성', '구매 작성', '수금/지급') `
        -StepName "$StepName 이후 홈 복귀" `
        -TimeoutSeconds 30
    return $homeAgain.Content
}

if ([string]::IsNullOrWhiteSpace($EvidenceDirectory)) {
    $EvidenceDirectory = Join-Path $ProjectRoot '테스트 시행\기록'
}
New-Item -ItemType Directory -Force -Path $EvidenceDirectory | Out-Null

if (-not [string]::IsNullOrWhiteSpace($ExerciseMasterDataNonRetryableSaveFaultStatus) -and $SkipInstall) {
    throw '거래처/품목 비재시도성 저장 실패 검증은 dirty 0건 보장을 위해 fresh install/app-data-clear 상태에서만 실행하세요. -SkipInstall을 제거하세요.'
}
if ($RequireUpdateInPlace -and $SkipInstall) {
    throw 'Android update-in-place 검증은 APK 덮어쓰기 설치가 필요합니다. -RequireUpdateInPlace와 -SkipInstall을 함께 사용할 수 없습니다.'
}
if (-not [string]::IsNullOrWhiteSpace($ExerciseMasterDataNonRetryableSaveFaultStatus) -and $RequireUpdateInPlace) {
    throw '거래처/품목 비재시도성 저장 실패 검증은 fresh install/app-data-clear 상태에서만 실행하세요. -RequireUpdateInPlace와 함께 사용할 수 없습니다.'
}

if ($ExerciseSyncNow -or -not [string]::IsNullOrWhiteSpace($ExerciseMasterDataNonRetryableSaveFaultStatus)) {
    Assert-LocalSyncExerciseTarget -BaseUrl $SyncExerciseBaseUrl
}

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$resolvedAdb = Resolve-AdbPath -RequestedPath $AdbPath
$resolvedApk = Resolve-ApkPath -ProjectRoot $ProjectRoot -RequestedPath $ApkPath
$resolvedApkAnalyzer = ''
$resolvedAnalyzerJavaHome = ''
$candidateApkMetadata = $null
if ($RequireUpdateInPlace) {
    $resolvedApkAnalyzer = Resolve-ApkAnalyzerPath `
        -ProjectRoot $ProjectRoot `
        -RequestedPath $ApkAnalyzerPath
    $resolvedAnalyzerJavaHome = Resolve-JavaHomeForApkAnalyzer `
        -RequestedPath $JavaSdkDirectory
    $candidateApkMetadata = Get-ApkManifestMetadata `
        -ApkAnalyzerPath $resolvedApkAnalyzer `
        -ApkPath $resolvedApk `
        -JavaHome $resolvedAnalyzerJavaHome
    if (-not [string]::Equals(
            $candidateApkMetadata.ApplicationId,
            $PackageName,
            [System.StringComparison]::Ordinal)) {
        throw "APK applicationId가 검증 대상 패키지와 다릅니다. expected=$PackageName actual=$($candidateApkMetadata.ApplicationId)"
    }
}
$deviceId = Get-ConnectedDeviceId -AdbPath $resolvedAdb
$screen = Get-ScreenSize -AdbPath $resolvedAdb -DeviceId $deviceId

$steps = New-Object System.Collections.Generic.List[object]
$freshInstall = $false
$installedVersionCodeBefore = $null
$installedVersionCodeAfter = $null

if (-not $SkipInstall) {
    if ($RequireUpdateInPlace) {
        Assert-MobilePackageInstalled -AdbPath $resolvedAdb -DeviceId $deviceId -PackageName $PackageName
        $installedVersionCodeBefore = Get-InstalledMobileVersionCode `
            -AdbPath $resolvedAdb `
            -DeviceId $deviceId `
            -PackageName $PackageName
        if ($candidateApkMetadata.VersionCode -le $installedVersionCodeBefore) {
            throw "Android update-in-place 후보 versionCode는 기존 설치본보다 커야 합니다. candidate=$($candidateApkMetadata.VersionCode) installed=$installedVersionCodeBefore"
        }

        Install-MobileApk -AdbPath $resolvedAdb -DeviceId $deviceId -ApkPath $resolvedApk -PackageName $PackageName -RequireUpdateInPlace
        $installedVersionCodeAfter = Get-InstalledMobileVersionCode `
            -AdbPath $resolvedAdb `
            -DeviceId $deviceId `
            -PackageName $PackageName
        if ($installedVersionCodeAfter -ne $candidateApkMetadata.VersionCode) {
            throw "Android update-in-place 설치 후 versionCode가 후보 APK와 일치하지 않습니다. candidate=$($candidateApkMetadata.VersionCode) installed=$installedVersionCodeAfter"
        }

        $steps.Add([pscustomobject]@{
            Step = 'update-in-place'
            Result = 'PASS'
            Detail = "applicationId=$($candidateApkMetadata.ApplicationId); versionCode=$installedVersionCodeBefore->$installedVersionCodeAfter"
        })
    }
    else {
        Install-MobileApk -AdbPath $resolvedAdb -DeviceId $deviceId -ApkPath $resolvedApk -PackageName $PackageName
        $steps.Add([pscustomobject]@{ Step = 'install'; Result = 'PASS'; Detail = $resolvedApk })
        Invoke-Adb -AdbPath $resolvedAdb -Arguments @('-s', $deviceId, 'shell', 'pm', 'clear', $PackageName) | Out-Null
        $steps.Add([pscustomobject]@{ Step = 'app-data-clear'; Result = 'PASS'; Detail = $PackageName })
        $freshInstall = $true
    }
}

# 이전 실행에서 이미 떠 있던 시스템 ANR 대화상자는 새 측정을 시작하기 전에만
# 명시적으로 닫는다. 이후 앱 시작·화면 이동 중 새로 나타나는 대상 앱 ANR은
# Dismiss-AndroidAnrDialog가 예외로 처리하므로 실제 회귀를 숨기지 않는다.
$preflightDump = Get-UiDump `
    -AdbPath $resolvedAdb `
    -DeviceId $deviceId `
    -EvidenceDirectory $EvidenceDirectory `
    -Name "mobile-smoke-$timestamp-preflight-system-dialog"
if ($preflightDump.Content.Contains("isn't responding")) {
    Dismiss-AndroidAnrDialog `
        -AdbPath $resolvedAdb `
        -DeviceId $deviceId `
        -Content $preflightDump.Content `
        -AllowTargetAppRecovery | Out-Null
    Start-Sleep -Seconds 3
    $steps.Add([pscustomobject]@{
        Step = 'pre-existing-anr-dialog'
        Result = 'RECOVERED'
        Detail = $preflightDump.Path
    })
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

$dump = Wait-UiForAppReady -AdbPath $resolvedAdb -DeviceId $deviceId -EvidenceDirectory $EvidenceDirectory -Timestamp $timestamp -PackageName $PackageName
if ($freshInstall) {
    # Fresh installs on cold emulators can expose the login tree before MAUI/keyboard work is fully idle.
    # Wait once more before sending text so the smoke validates the app flow instead of emulator warm-up timing.
    Start-Sleep -Seconds 20
    $dump = Get-UiDump -AdbPath $resolvedAdb -DeviceId $deviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-smoke-$timestamp-login-stable"
    if (Dismiss-AndroidAnrDialog -AdbPath $resolvedAdb -DeviceId $deviceId -Content $dump.Content) {
        Start-Sleep -Seconds 5
        $dump = Wait-UiForAppReady -AdbPath $resolvedAdb -DeviceId $deviceId -EvidenceDirectory $EvidenceDirectory -Timestamp "$timestamp-login-stable-after-anr" -PackageName $PackageName -TimeoutSeconds 60
    }
}

if ($dump.Content.Contains('계정 로그인') -or ($dump.Content.Contains('로그인') -and $dump.Content.Contains('비밀번호'))) {
    $dump = Set-LoginTextField `
        -AdbPath $resolvedAdb `
        -DeviceId $deviceId `
        -EvidenceDirectory $EvidenceDirectory `
        -Timestamp $timestamp `
        -FieldName '아이디' `
        -IsPassword $false `
        -Value $Username `
        -VerifyPlainText

    $dump = Set-LoginTextField `
        -AdbPath $resolvedAdb `
        -DeviceId $deviceId `
        -EvidenceDirectory $EvidenceDirectory `
        -Timestamp $timestamp `
        -FieldName '비밀번호' `
        -IsPassword $true `
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

$currentHomeContent = Open-HomeActionAndAssert `
    -AdbPath $resolvedAdb `
    -DeviceId $deviceId `
    -EvidenceDirectory $EvidenceDirectory `
    -Timestamp $timestamp `
    -Screen $screen `
    -HomeContent $homeDump.Content `
    -ButtonText '렌탈 조회' `
    -StepName 'rentals-readonly' `
    -Needles @('렌탈 조회', '청구프로필', '렌탈자산', '청구 이력', '설치이력', '조회 전용') `
    -Steps $steps

if ($IncludeDraftScreens) {
    $currentHomeContent = Open-HomeActionAndAssert `
        -AdbPath $resolvedAdb `
        -DeviceId $deviceId `
        -EvidenceDirectory $EvidenceDirectory `
        -Timestamp $timestamp `
        -Screen $screen `
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
        -Screen $screen `
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
        -Screen $screen `
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

if (-not [string]::IsNullOrWhiteSpace($ExerciseMasterDataNonRetryableSaveFaultStatus)) {
    Invoke-MasterDataNonRetryableSaveRejection `
        -AdbPath $resolvedAdb `
        -DeviceId $deviceId `
        -PackageName $PackageName `
        -EvidenceDirectory $EvidenceDirectory `
        -Timestamp $timestamp `
        -Screen $screen `
        -FaultStatus $ExerciseMasterDataNonRetryableSaveFaultStatus `
        -Steps $steps
}

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

$syncDump = Open-BottomTabAndAssert `
    -AdbPath $resolvedAdb `
    -DeviceId $deviceId `
    -EvidenceDirectory $EvidenceDirectory `
    -Timestamp $timestamp `
    -Screen $screen `
    -TabText '동기화' `
    -FallbackXRatio 0.84 `
    -StepName 'sync-status' `
    -Needles @('동기화 상태', '마지막 서버 변경번호', '저장 대기', '권장 동기화 실행', '서버에서 받기', '서버에 올리기') `
    -Steps $steps

if ($ExerciseSyncNow) {
    Invoke-SyncNowAndAssert `
        -AdbPath $resolvedAdb `
        -DeviceId $deviceId `
        -EvidenceDirectory $EvidenceDirectory `
        -Timestamp $timestamp `
        -SyncContent $syncDump.Content `
        -Steps $steps | Out-Null
}

$result = [pscustomobject]@{
    CreatedAt = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    PackageName = $PackageName
    DeviceId = $deviceId
    ApkPath = $resolvedApk
    RequireUpdateInPlace = [bool]$RequireUpdateInPlace
    CandidateApplicationId = if ($null -ne $candidateApkMetadata) { $candidateApkMetadata.ApplicationId } else { '' }
    CandidateVersionCode = if ($null -ne $candidateApkMetadata) { $candidateApkMetadata.VersionCode } else { $null }
    InstalledVersionCodeBefore = $installedVersionCodeBefore
    InstalledVersionCodeAfter = $installedVersionCodeAfter
    ExerciseSyncNow = [bool]$ExerciseSyncNow
    ExerciseMasterDataNonRetryableSaveFaultStatus = $ExerciseMasterDataNonRetryableSaveFaultStatus
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
    "- 기존 설치본 덮어쓰기 검증: $([bool]$RequireUpdateInPlace)",
    "- 후보 applicationId: $($result.CandidateApplicationId)",
    "- 후보 versionCode: $($result.CandidateVersionCode)",
    "- 설치 전 versionCode: $($result.InstalledVersionCodeBefore)",
    "- 설치 후 versionCode: $($result.InstalledVersionCodeAfter)",
    "- 수동 동기화 실행: $([bool]$ExerciseSyncNow)",
    "- 거래처/품목 비재시도성 저장 실패 검증: $ExerciseMasterDataNonRetryableSaveFaultStatus",
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
