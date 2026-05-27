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
$resultStatus = 'FAIL'
$errorMessage = $null

try {
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
        Invoke-Adb -AdbPath $resolvedAdb -Arguments @('-s', $deviceId, 'install', '-r', $resolvedApk) | Out-Null
        $steps.Add([pscustomobject]@{ Step = 'install'; Result = 'PASS'; Detail = $resolvedApk })
        Invoke-Adb -AdbPath $resolvedAdb -Arguments @('-s', $deviceId, 'shell', 'pm', 'clear', $PackageName) | Out-Null
        $steps.Add([pscustomobject]@{ Step = 'app-data-clear'; Result = 'PASS'; Detail = $PackageName })
    }

    Invoke-Adb -AdbPath $resolvedAdb -Arguments @('-s', $deviceId, 'shell', 'am', 'force-stop', 'com.google.android.apps.nexuslauncher') | Out-Null
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
            Tap-Point -AdbPath $resolvedAdb -DeviceId $deviceId -X $userPoint.X -Y $userPoint.Y
            Set-AndroidText -AdbPath $resolvedAdb -DeviceId $deviceId -Text $Username
            Invoke-Adb -AdbPath $resolvedAdb -Arguments @('-s', $deviceId, 'shell', 'input', 'keyevent', 'KEYCODE_ESCAPE') | Out-Null
            Start-Sleep -Seconds 1
            $dump = Wait-UiReadyForLoginOrHome -AdbPath $resolvedAdb -DeviceId $deviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-payment-e2e-$voucherSlug-$timestamp-login-username-entered" -TimeoutSeconds 90
            $passwordPoint = Get-NodeCenterByText -Content $dump.Content -Text '비밀번호' -ClassName 'android.widget.EditText'
        }
        if (-not $passwordPoint) {
            throw '로그인 화면에서 비밀번호 입력칸을 찾지 못했습니다.'
        }
        Tap-Point -AdbPath $resolvedAdb -DeviceId $deviceId -X $passwordPoint.X -Y $passwordPoint.Y
        Set-AndroidText -AdbPath $resolvedAdb -DeviceId $deviceId -Text $Password
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

    $saveButtonPoint = $null
    for ($scrollAttempt = 0; $scrollAttempt -lt 5; $scrollAttempt++) {
        $paymentDump = Get-UiDump -AdbPath $resolvedAdb -DeviceId $deviceId -EvidenceDirectory $EvidenceDirectory -Name "mobile-payment-e2e-$voucherSlug-$timestamp-payment-save-$scrollAttempt"
        $saveButtonPoint = Get-NodeCenterByText -Content $paymentDump.Content -Text $paymentSaveText -ClassName 'android.widget.Button'
        if ($saveButtonPoint) { break }
        Invoke-Adb -AdbPath $resolvedAdb -Arguments @('-s', $deviceId, 'shell', 'input', 'swipe', '540', '2050', '540', '900', '700') | Out-Null
        Start-Sleep -Seconds 2
    }
    if (-not $saveButtonPoint) {
        throw "$paymentSaveText 버튼을 찾지 못했습니다."
    }

    Tap-Point -AdbPath $resolvedAdb -DeviceId $deviceId -X $saveButtonPoint.X -Y $saveButtonPoint.Y
    Start-Sleep -Seconds 12

    $createdPayment = Wait-TestPaymentCreated -BaseUrl $BaseUrl -Headers $headers -InvoiceId $createdInvoice.id -ExpectedAmount $expectedAmount -ExpectedMethodName $expectedMethod
    $createdTransaction = Wait-TestTransactionCreated -BaseUrl $BaseUrl -Headers $headers -TransactionId $createdPayment.id -InvoiceId $createdInvoice.id -VoucherKind $VoucherKind -ExpectedAmount $expectedAmount
    $steps.Add([pscustomobject]@{ Step = "mobile-$voucherSlug-payment-create"; Result = 'PASS'; Detail = "payment=$($createdPayment.id), transaction=$($createdTransaction.id), method=$expectedMethod, amount=$expectedAmount" })

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
    Result = $resultStatus
    Error = $errorMessage
    Fixture = if ($fixture) { [pscustomobject]@{ CustomerId = $fixture.Customer.id; CustomerName = $fixture.CustomerName; ItemId = $fixture.Item.id; ItemName = $fixture.ItemName } } else { $null }
    CreatedInvoiceId = if ($createdInvoice) { $createdInvoice.id } else { $null }
    CreatedPaymentId = if ($createdPayment) { $createdPayment.id } else { $null }
    CreatedTransactionId = if ($createdTransaction) { $createdTransaction.id } else { $null }
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
    "- 결과: $resultStatus",
    "- 오류: $errorMessage",
    '',
    '## 테스트 데이터',
    "- 거래처: $($result.Fixture.CustomerName) / $($result.Fixture.CustomerId)",
    "- 품목: $($result.Fixture.ItemName) / $($result.Fixture.ItemId)",
    "- 대상 전표: $($result.CreatedInvoiceId)",
    "- 생성 수금/지급: $($result.CreatedPaymentId)",
    "- 연결 거래내역: $($result.CreatedTransactionId)",
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
