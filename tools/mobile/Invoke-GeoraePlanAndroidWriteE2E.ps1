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
    [switch]$KeepTemporaryData
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

    $installArgs = @('-s', $DeviceId, 'install', '-r', '-d', $ApkPath)

    try {
        Invoke-Adb -AdbPath $AdbPath -Arguments $installArgs | Out-Null
    }
    catch {
        $message = $_.Exception.Message
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

function Return-ToHomeQuickActionScreen {
    param(
        [string]$AdbPath,
        [string]$DeviceId,
        [string]$EvidenceDirectory,
        [string]$Timestamp,
        [object]$Screen
    )

    for ($attempt = 1; $attempt -le 5; $attempt++) {
        Tap-BottomTab -AdbPath $AdbPath -DeviceId $DeviceId -Screen $Screen -XRatio 0.10
        Start-Sleep -Seconds 2
        $dump = Get-UiDump -AdbPath $AdbPath -DeviceId $DeviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-write-e2e-$Timestamp-home-check-$attempt"
        if (Dismiss-AndroidAnrDialog -AdbPath $AdbPath -DeviceId $DeviceId -Content $dump.Content) {
            Start-Sleep -Seconds 5
            continue
        }

        if ($dump.Content.Contains('빠른 안내') -and
            $dump.Content.Contains('판매 작성') -and
            $dump.Content.Contains('구매 작성') -and
            $dump.Content.Contains('수금/지급')) {
            Copy-Item -LiteralPath $dump.Path -Destination (Join-Path $EvidenceDirectory "mobile-write-e2e-$Timestamp-home.xml") -Force
            return $dump
        }

        Invoke-Adb -AdbPath $AdbPath -Arguments @('-s', $DeviceId, 'shell', 'input', 'keyevent', 'KEYCODE_BACK') | Out-Null
        Start-Sleep -Seconds 2
    }

    return Wait-UiContainsAll `
        -AdbPath $AdbPath `
        -DeviceId $DeviceId `
        -EvidenceDirectory $EvidenceDirectory `
        -Name "mobile-write-e2e-$Timestamp-home" `
        -Needles @('빠른 안내', '판매 작성', '구매 작성', '수금/지급') `
        -StepName '홈 화면' `
        -TimeoutSeconds 60
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
        $keyCode = switch -Regex ([string]$ch) {
            '^[a-zA-Z]$' { 'KEYCODE_' + ([string]$ch).ToUpperInvariant(); break }
            '^[0-9]$' { 'KEYCODE_' + [string]$ch; break }
            '^ $' { 'KEYCODE_SPACE'; break }
            '^-$' { 'KEYCODE_MINUS'; break }
            default { $null }
        }

        if ($keyCode) {
            Invoke-Adb -AdbPath $AdbPath -Arguments @('-s', $DeviceId, 'shell', 'input', 'keyevent', $keyCode) | Out-Null
            Start-Sleep -Milliseconds 30
            continue
        }

        $safeText = ([string]$ch).Replace(' ', '%s')
        Invoke-Adb -AdbPath $AdbPath -Arguments @('-s', $DeviceId, 'shell', 'input', 'text', $safeText) | Out-Null
        Start-Sleep -Milliseconds 30
    }
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

    $stockPush = Invoke-Api -Method Post -BaseUrl $BaseUrl -Relative 'sync/push' -Headers $Headers -Body @{
        deviceId = "mobile-write-e2e-$Stamp"
        itemWarehouseStocks = @(
            @{
                itemId = $item.id
                warehouseCode = 'USENET_MAIN'
                quantity = 10
                updatedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
                revision = 0
                expectedRevision = 0
            }
        )
    }

    $stockPushConflictCount = 0
    if ($stockPush -and $null -ne $stockPush.conflictCount) {
        $stockPushConflictCount = [int]$stockPush.conflictCount
    }

    if ($stockPushConflictCount -gt 0) {
        throw "테스트 품목 창고 재고 생성 중 충돌이 발생했습니다: $($stockPush | ConvertTo-Json -Compress -Depth 8)"
    }

    $item = Invoke-Api -Method Get -BaseUrl $BaseUrl -Relative "items/$($item.id)" -Headers $Headers

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

if ([string]::IsNullOrWhiteSpace($EvidenceDirectory)) {
    $EvidenceDirectory = Join-Path $ProjectRoot '테스트 시행\기록'
}
New-Item -ItemType Directory -Force -Path $EvidenceDirectory | Out-Null

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$stamp = New-MobileE2EAlphaSuffix
$voucherSlug = $VoucherKind.ToLowerInvariant()
$voucherKorean = if ($VoucherKind -eq 'Purchase') { '구매' } else { '판매' }
$homeActionText = if ($VoucherKind -eq 'Purchase') { '구매 작성' } else { '판매 작성' }
$draftTitle = if ($VoucherKind -eq 'Purchase') { '구매(매입) 작성' } else { '판매(매출) 작성' }
$saveButtonText = if ($VoucherKind -eq 'Purchase') { '구매 전표 저장' } else { '판매 전표 저장' }
$steps = New-Object System.Collections.Generic.List[object]
$cleanupSteps = New-Object System.Collections.Generic.List[object]
$fixture = $null
$createdInvoice = $null
$resultStatus = 'FAIL'
$errorMessage = $null

try {
    $headers = New-ApiSession -BaseUrl $BaseUrl -Username $Username -Password $Password
    $steps.Add([pscustomobject]@{ Step = 'api-login'; Result = 'PASS'; Detail = $BaseUrl })

    $fixture = New-TestFixture -BaseUrl $BaseUrl -Headers $headers -Stamp $stamp
    $steps.Add([pscustomobject]@{ Step = 'fixture-create'; Result = 'PASS'; Detail = "customer=$($fixture.Customer.id), item=$($fixture.Item.id)" })

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

    $dump = Wait-UiReadyForLoginOrHome -AdbPath $resolvedAdb -DeviceId $deviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-write-e2e-$timestamp-launch"
    if ($dump.Content.Contains('빠른 안내')) {
        Start-Sleep -Seconds 3
        $postLaunchDump = Get-UiDump -AdbPath $resolvedAdb -DeviceId $deviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-write-e2e-$timestamp-post-launch-stable"
        if ($postLaunchDump.Content.Contains('계정 로그인') -or ($postLaunchDump.Content.Contains('로그인') -and $postLaunchDump.Content.Contains('비밀번호'))) {
            $dump = $postLaunchDump
        }
    }
    if ($dump.Content.Contains('계정 로그인') -or ($dump.Content.Contains('로그인') -and $dump.Content.Contains('비밀번호'))) {
        $userPoint = Get-NodeCenterByText -Content $dump.Content -Text '아이디' -ClassName 'android.widget.EditText'
        $passwordPoint = Get-NodeCenterByText -Content $dump.Content -Text '비밀번호' -ClassName 'android.widget.EditText'
        $loginButtonPoint = Get-NodeCenterByText -Content $dump.Content -Text '로그인' -ClassName 'android.widget.Button'

        if ($userPoint) {
            Tap-Point -AdbPath $resolvedAdb -DeviceId $deviceId -X $userPoint.X -Y $userPoint.Y
            Set-AndroidText -AdbPath $resolvedAdb -DeviceId $deviceId -Text $Username
            Invoke-Adb -AdbPath $resolvedAdb -Arguments @('-s', $deviceId, 'shell', 'input', 'keyevent', 'KEYCODE_ESCAPE') | Out-Null
            Start-Sleep -Seconds 1
            $dump = Wait-UiReadyForLoginOrHome -AdbPath $resolvedAdb -DeviceId $deviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-write-e2e-$timestamp-login-username-entered" -TimeoutSeconds 90
            $passwordPoint = Get-NodeCenterByText -Content $dump.Content -Text '비밀번호' -ClassName 'android.widget.EditText'
        }
        if (-not $passwordPoint) {
            throw '로그인 화면에서 비밀번호 입력칸을 찾지 못했습니다.'
        }
        Tap-Point -AdbPath $resolvedAdb -DeviceId $deviceId -X $passwordPoint.X -Y $passwordPoint.Y
        Set-AndroidText -AdbPath $resolvedAdb -DeviceId $deviceId -Text $Password
        Invoke-Adb -AdbPath $resolvedAdb -Arguments @('-s', $deviceId, 'shell', 'input', 'keyevent', 'KEYCODE_ESCAPE') | Out-Null
        Start-Sleep -Seconds 1
        $dump = Wait-UiReadyForLoginOrHome -AdbPath $resolvedAdb -DeviceId $deviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-write-e2e-$timestamp-login-password-entered" -TimeoutSeconds 90
        $loginButtonPoint = Get-NodeCenterByText -Content $dump.Content -Text '로그인' -ClassName 'android.widget.Button'
        if (-not $loginButtonPoint) {
            throw '로그인 버튼을 찾지 못했습니다.'
        }
        Tap-Point -AdbPath $resolvedAdb -DeviceId $deviceId -X $loginButtonPoint.X -Y $loginButtonPoint.Y
        Wait-UiContainsAll `
            -AdbPath $resolvedAdb `
            -DeviceId $deviceId `
            -EvidenceDirectory $EvidenceDirectory `
            -Name "mobile-write-e2e-$timestamp-after-login" `
            -Needles @('빠른 안내', $homeActionText) `
            -StepName '로그인 후 홈 화면' `
            -TimeoutSeconds 90 | Out-Null
        $steps.Add([pscustomobject]@{ Step = 'mobile-login'; Result = 'PASS'; Detail = $Username })
    }

    $draftDump = $null
    $lastDraftOpenError = $null
    for ($draftOpenAttempt = 1; $draftOpenAttempt -le 3 -and -not $draftDump; $draftOpenAttempt++) {
        $homeDump = Return-ToHomeQuickActionScreen `
            -AdbPath $resolvedAdb `
            -DeviceId $deviceId `
            -EvidenceDirectory $EvidenceDirectory `
            -Timestamp $timestamp `
            -Screen $screen
        Tap-UiText -AdbPath $resolvedAdb -DeviceId $deviceId -Content $homeDump.Content -Text $homeActionText -ClassName 'android.widget.Button' -StepName "$homeActionText 열기"
        Start-Sleep -Seconds 2

        try {
            $draftDump = Wait-UiContainsAll `
                -AdbPath $resolvedAdb `
                -DeviceId $deviceId `
                -EvidenceDirectory $EvidenceDirectory `
                -Name "mobile-write-e2e-$voucherSlug-$timestamp-draft-$draftOpenAttempt" `
                -Needles @($draftTitle, '거래처명 입력', '품명 / 규격 검색') `
                -StepName "$voucherKorean 작성 화면" `
                -TimeoutSeconds 30
        }
        catch {
            $lastDraftOpenError = $_
        }
    }

    if (-not $draftDump) {
        if ($lastDraftOpenError) {
            throw $lastDraftOpenError
        }
        throw "$voucherKorean 작성 화면을 열지 못했습니다."
    }

    Tap-UiText -AdbPath $resolvedAdb -DeviceId $deviceId -Content $draftDump.Content -Text '거래처명 입력' -ClassName 'android.widget.EditText' -StepName '거래처 검색 입력'
    Set-AndroidText -AdbPath $resolvedAdb -DeviceId $deviceId -Text $fixture.CustomerName
    Invoke-Adb -AdbPath $resolvedAdb -Arguments @('-s', $deviceId, 'shell', 'input', 'keyevent', 'KEYCODE_ESCAPE') | Out-Null
    Tap-UiText -AdbPath $resolvedAdb -DeviceId $deviceId -Content $draftDump.Content -Text '찾기' -ClassName 'android.widget.Button' -StepName '거래처 검색 실행'
    Start-Sleep -Seconds 5

    $customerResultDump = Get-UiDump -AdbPath $resolvedAdb -DeviceId $deviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-write-e2e-$timestamp-customer-result"
    Assert-UiContains -Content $customerResultDump.Content -Needles @($fixture.CustomerName, '선택') -StepName '거래처 검색 결과'
    Tap-UiText -AdbPath $resolvedAdb -DeviceId $deviceId -Content $customerResultDump.Content -Text '선택' -ClassName 'android.widget.Button' -StepName '거래처 선택' -MinY 450
    Start-Sleep -Seconds 3

    $customerSelectedDump = Get-UiDump -AdbPath $resolvedAdb -DeviceId $deviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-write-e2e-$timestamp-customer-selected"
    Assert-UiContains -Content $customerSelectedDump.Content -Needles @("선택 거래처: $($fixture.CustomerName)") -StepName '거래처 선택 반영'

    Tap-UiText -AdbPath $resolvedAdb -DeviceId $deviceId -Content $customerSelectedDump.Content -Text '품명 / 규격 검색' -ClassName 'android.widget.EditText' -StepName '품목 검색 입력'
    Set-AndroidText -AdbPath $resolvedAdb -DeviceId $deviceId -Text $fixture.ItemName
    Invoke-Adb -AdbPath $resolvedAdb -Arguments @('-s', $deviceId, 'shell', 'input', 'keyevent', 'KEYCODE_ESCAPE') | Out-Null
    Tap-UiText -AdbPath $resolvedAdb -DeviceId $deviceId -Content $customerSelectedDump.Content -Text '검색' -ClassName 'android.widget.Button' -StepName '품목 검색 실행' -MinY 1100
    Start-Sleep -Seconds 5

    $itemResultDump = Get-UiDump -AdbPath $resolvedAdb -DeviceId $deviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-write-e2e-$timestamp-item-result"
    Assert-UiContains -Content $itemResultDump.Content -Needles @($fixture.ItemName, $fixture.ItemSpec, '품목 선택') -StepName '품목 검색 결과'
    Tap-UiText -AdbPath $resolvedAdb -DeviceId $deviceId -Content $itemResultDump.Content -Text '품목 선택' -ClassName 'android.widget.Button' -StepName '품목 선택' -MinY 1400
    Start-Sleep -Seconds 4

    $sheetDump = Get-UiDump -AdbPath $resolvedAdb -DeviceId $deviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-write-e2e-$timestamp-item-sheet"
    Assert-UiContains -Content $sheetDump.Content -Needles @('품목 추가', '수량', '단가') -StepName '품목 입력 시트'
    Tap-UiText -AdbPath $resolvedAdb -DeviceId $deviceId -Content $sheetDump.Content -Text '품목 추가' -ClassName 'android.widget.Button' -StepName '품목 추가'
    Start-Sleep -Seconds 4

    $lineDump = $null
    $saveButtonPoint = $null
    $lineItemSeen = $false
    for ($scrollAttempt = 0; $scrollAttempt -lt 4; $scrollAttempt++) {
        $lineDump = Get-UiDump -AdbPath $resolvedAdb -DeviceId $deviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-write-e2e-$timestamp-line-added-$scrollAttempt"
        if ($lineDump.Content.Contains($fixture.ItemName)) {
            $lineItemSeen = $true
        }
        $saveButtonPoint = Get-NodeCenterByText -Content $lineDump.Content -Text $saveButtonText -ClassName 'android.widget.Button'
        if ($saveButtonPoint) {
            break
        }
        Invoke-Adb -AdbPath $resolvedAdb -Arguments @('-s', $deviceId, 'shell', 'input', 'swipe', '540', '2050', '540', '1050', '700') | Out-Null
        Start-Sleep -Seconds 2
    }

    if (-not $saveButtonPoint) {
        throw '전표 저장 버튼을 찾지 못했습니다.'
    }
    if (-not $lineItemSeen) {
        throw "추가 품목 반영 확인 실패. 찾지 못한 문구: $($fixture.ItemName)"
    }
    Assert-UiContains -Content $lineDump.Content -Needles @($saveButtonText) -StepName '전표 저장 버튼'
    Tap-Point -AdbPath $resolvedAdb -DeviceId $deviceId -X $saveButtonPoint.X -Y $saveButtonPoint.Y
    Start-Sleep -Seconds 10

    $afterSaveDump = Get-UiDump -AdbPath $resolvedAdb -DeviceId $deviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-write-e2e-$timestamp-after-save"
    $createdInvoice = Wait-TestInvoiceCreated -BaseUrl $BaseUrl -Headers $headers -CustomerId $fixture.Customer.id -CustomerName $fixture.CustomerName -ItemName $fixture.ItemName -VoucherKind $VoucherKind
    $steps.Add([pscustomobject]@{ Step = "mobile-$voucherSlug-invoice-create"; Result = 'PASS'; Detail = "invoice=$($createdInvoice.id), total=$($createdInvoice.totalAmount), dump=$($afterSaveDump.Path)" })

    $resultStatus = 'PASS'
}
catch {
    $errorMessage = $_.Exception.Message
    $steps.Add([pscustomobject]@{ Step = 'error'; Result = 'FAIL'; Detail = $errorMessage })
}
finally {
    if (-not $KeepTemporaryData) {
        try {
            if (-not $headers) {
                $headers = New-ApiSession -BaseUrl $BaseUrl -Username $Username -Password $Password
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
    Result = $resultStatus
    Error = $errorMessage
    Fixture = if ($fixture) { [pscustomobject]@{ CustomerId = $fixture.Customer.id; CustomerName = $fixture.CustomerName; ItemId = $fixture.Item.id; ItemName = $fixture.ItemName } } else { $null }
    CreatedInvoiceId = if ($createdInvoice) { $createdInvoice.id } else { $null }
    Steps = $steps
    Cleanup = $cleanupSteps
}

$jsonPath = Join-Path $EvidenceDirectory "mobile-write-e2e-$timestamp.json"
$mdPath = Join-Path $EvidenceDirectory "mobile-write-e2e-$timestamp.md"
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

$mdLines = @(
    "# 모바일 Android $voucherKorean 전표 쓰기 E2E 검증",
    '',
    "- 작성시각: $($result.CreatedAt)",
    "- API: $BaseUrl",
    "- 패키지: $PackageName",
    "- 전표유형: $VoucherKind",
    "- 결과: $resultStatus",
    "- 오류: $errorMessage",
    '',
    '## 테스트 데이터',
    "- 거래처: $($result.Fixture.CustomerName) / $($result.Fixture.CustomerId)",
    "- 품목: $($result.Fixture.ItemName) / $($result.Fixture.ItemId)",
    "- 생성 전표: $($result.CreatedInvoiceId)",
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

Write-Host "mobile_write_e2e_report=$mdPath"
Write-Host "mobile_write_e2e_json=$jsonPath"
Write-Host "result=$resultStatus"
if ($resultStatus -ne 'PASS') {
    exit 1
}
