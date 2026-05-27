param(
    [string]$BaseUrl = 'http://127.0.0.1:19080',
    [string]$Username = 'usenet',
    [string]$Password = '1234',
    [string]$EvidenceDirectory = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path 'output\same-account-concurrency-smoke'),
    [switch]$KeepTemporaryData
)

$ErrorActionPreference = 'Stop'

function New-DirectoryIfMissing {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
    }
}

function ConvertTo-BodyJson {
    param([object]$Body)
    if ($null -eq $Body) { return $null }
    return ($Body | ConvertTo-Json -Depth 30 -Compress)
}

function Read-ErrorResponseBody {
    param([System.Net.WebResponse]$Response)
    if ($null -eq $Response) { return '' }
    try {
        $stream = $Response.GetResponseStream()
        if ($null -eq $stream) { return '' }
        $reader = New-Object System.IO.StreamReader($stream)
        try { return $reader.ReadToEnd() }
        finally { $reader.Dispose() }
    }
    catch {
        return ''
    }
}

function Invoke-Api {
    param(
        [ValidateSet('GET','POST','PUT','DELETE')][string]$Method,
        [string]$Path,
        [hashtable]$Headers,
        [object]$Body,
        [int[]]$ExpectedStatus = @(200)
    )

    $uri = if ($Path.StartsWith('http', [System.StringComparison]::OrdinalIgnoreCase)) { $Path } else { "$BaseUrl/$($Path.TrimStart('/'))" }
    $params = @{
        Method = $Method
        Uri = $uri
        UseBasicParsing = $true
        TimeoutSec = 30
    }
    if ($Headers) { $params.Headers = $Headers }
    if ($PSBoundParameters.ContainsKey('Body') -and $null -ne $Body) {
        $params.Body = ConvertTo-BodyJson $Body
        $params.ContentType = 'application/json; charset=utf-8'
    }

    $status = 0
    $content = ''
    try {
        $response = Invoke-WebRequest @params
        $status = [int]$response.StatusCode
        $content = [string]$response.Content
    }
    catch {
        if ($_.Exception.Response) {
            $status = [int]$_.Exception.Response.StatusCode
            $content = Read-ErrorResponseBody $_.Exception.Response
        }
        else {
            throw
        }
    }

    if ($ExpectedStatus -notcontains $status) {
        throw "API $Method $uri failed. status=$status expected=$($ExpectedStatus -join ',') body=$content"
    }

    $parsed = $null
    if (-not [string]::IsNullOrWhiteSpace($content)) {
        try { $parsed = $content | ConvertFrom-Json }
        catch { $parsed = $content }
    }

    [pscustomobject]@{
        Status = $status
        Body = $parsed
        Raw = $content
    }
}

function Login-Client {
    param([string]$ClientName)
    $login = Invoke-Api -Method POST -Path 'auth/login' -Body @{ username = $Username; password = $Password } -ExpectedStatus @(200)
    if ([string]::IsNullOrWhiteSpace($login.Body.token)) {
        throw "$ClientName 로그인 토큰을 받지 못했습니다."
    }
    @{ Authorization = "Bearer $($login.Body.token)" }
}

function Clone-JsonObject {
    param([object]$Value)
    if ($null -eq $Value) { return $null }
    $Value | ConvertTo-Json -Depth 30 | ConvertFrom-Json
}

function Set-ExpectedRevision {
    param([object]$Dto)
    $Dto.expectedRevision = [Int64]$Dto.revision
    return $Dto
}

function Invoke-StaleThenFreshUpdate {
    param(
        [string]$EntityName,
        [string]$GetPath,
        [string]$UpdatePath,
        [scriptblock]$Mutate
    )

    $pcSnapshot = (Invoke-Api -Method GET -Path $GetPath -Headers $script:PcHeaders -ExpectedStatus @(200)).Body
    $mobileSnapshot = (Invoke-Api -Method GET -Path $GetPath -Headers $script:MobileHeaders -ExpectedStatus @(200)).Body

    $pcUpdate = Set-ExpectedRevision (Clone-JsonObject $pcSnapshot)
    & $Mutate $pcUpdate 'PC first save'
    $pcSaved = (Invoke-Api -Method PUT -Path $UpdatePath -Headers $script:PcHeaders -Body $pcUpdate -ExpectedStatus @(200)).Body

    $mobileStale = Set-ExpectedRevision (Clone-JsonObject $mobileSnapshot)
    & $Mutate $mobileStale 'Mobile stale save'
    $conflict = Invoke-Api -Method PUT -Path $UpdatePath -Headers $script:MobileHeaders -Body $mobileStale -ExpectedStatus @(409)

    $mobileFresh = (Invoke-Api -Method GET -Path $GetPath -Headers $script:MobileHeaders -ExpectedStatus @(200)).Body
    $mobileUpdate = Set-ExpectedRevision (Clone-JsonObject $mobileFresh)
    & $Mutate $mobileUpdate 'Mobile fresh save'
    $mobileSaved = (Invoke-Api -Method PUT -Path $UpdatePath -Headers $script:MobileHeaders -Body $mobileUpdate -ExpectedStatus @(200)).Body

    [pscustomobject]@{
        Entity = $EntityName
        PcInitialRevision = [Int64]$pcSnapshot.revision
        PcSavedRevision = [Int64]$pcSaved.revision
        MobileStaleRevision = [Int64]$mobileSnapshot.revision
        ConflictStatus = $conflict.Status
        FreshRevision = [Int64]$mobileFresh.revision
        MobileSavedRevision = [Int64]$mobileSaved.revision
        Result = 'PASS'
    }
}

function Get-PaymentByInvoice {
    param([Guid]$InvoiceId, [Guid]$PaymentId, [hashtable]$Headers)
    $payments = (Invoke-Api -Method GET -Path "payments?invoiceId=$InvoiceId" -Headers $Headers -ExpectedStatus @(200)).Body
    $payment = @($payments | Where-Object { [string]$_.id -eq [string]$PaymentId } | Select-Object -First 1)
    if ($payment.Count -eq 0) {
        throw "지급/수금 항목을 다시 조회하지 못했습니다: $PaymentId"
    }
    $payment[0]
}

function Remove-EntityQuietly {
    param([string]$EntityName, [string]$GetPath, [string]$DeletePath, [hashtable]$Headers)
    try {
        $current = (Invoke-Api -Method GET -Path $GetPath -Headers $Headers -ExpectedStatus @(200,404)).Body
        if ($null -eq $current) {
            return [pscustomobject]@{ Entity = $EntityName; Result = 'not-found' }
        }
        $revision = [Int64]$current.revision
        Invoke-Api -Method DELETE -Path "$($DeletePath)?expectedRevision=$revision" -Headers $Headers -ExpectedStatus @(204,404) | Out-Null
        [pscustomobject]@{ Entity = $EntityName; Result = 'deleted' }
    }
    catch {
        [pscustomobject]@{ Entity = $EntityName; Result = "cleanup-failed: $($_.Exception.Message)" }
    }
}

function Remove-PaymentQuietly {
    param([Guid]$InvoiceId, [Guid]$PaymentId, [hashtable]$Headers)
    try {
        $payment = Get-PaymentByInvoice -InvoiceId $InvoiceId -PaymentId $PaymentId -Headers $Headers
        Invoke-Api -Method DELETE -Path "payments/$($PaymentId)?expectedRevision=$([Int64]$payment.revision)" -Headers $Headers -ExpectedStatus @(204,404) | Out-Null
        [pscustomobject]@{ Entity = 'payment'; Result = 'deleted' }
    }
    catch {
        [pscustomobject]@{ Entity = 'payment'; Result = "cleanup-failed: $($_.Exception.Message)" }
    }
}

New-DirectoryIfMissing $EvidenceDirectory
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$steps = New-Object System.Collections.Generic.List[object]
$cleanup = New-Object System.Collections.Generic.List[object]

$customerId = [Guid]::NewGuid()
$itemId = [Guid]::NewGuid()
$invoiceId = [Guid]::NewGuid()
$invoiceLineId = [Guid]::NewGuid()
$paymentId = [Guid]::NewGuid()
$nameSuffix = $timestamp.Replace('-', '')

try {
    $script:PcHeaders = Login-Client 'PC'
    $script:MobileHeaders = Login-Client 'Mobile'
    $steps.Add([pscustomobject]@{ Step = 'login-two-clients'; Result = 'PASS' })

    $customer = (Invoke-Api -Method POST -Path 'customers' -Headers $script:PcHeaders -Body @{
        id = $customerId
        tenantCode = 'USENET'
        officeCode = 'USENET'
        responsibleOfficeCode = 'USENET'
        nameOriginal = "Concurrency customer $nameSuffix"
        nameMatchKey = "CONCURRENCYCUSTOMER$nameSuffix"
        tradeType = '매출'
        phone = '000-0000-0000'
        notes = 'created for same-account smoke'
    } -ExpectedStatus @(200)).Body
    $steps.Add([pscustomobject]@{ Step = 'fixture-customer-create'; Result = 'PASS'; Id = $customer.id })

    $item = (Invoke-Api -Method POST -Path 'items' -Headers $script:PcHeaders -Body @{
        id = $itemId
        tenantCode = 'USENET'
        officeCode = 'USENET'
        nameOriginal = "Concurrency item $nameSuffix"
        nameMatchKey = "CONCURRENCYITEM$nameSuffix"
        specificationOriginal = 'SMOKE'
        specificationMatchKey = 'SMOKE'
        trackingType = '비재고'
        itemKind = '상품'
        unit = 'EA'
        salePrice = 1000
        simpleMemo = 'created for same-account smoke'
    } -ExpectedStatus @(200)).Body
    $steps.Add([pscustomobject]@{ Step = 'fixture-item-create'; Result = 'PASS'; Id = $item.id })

    $invoice = (Invoke-Api -Method POST -Path 'invoices' -Headers $script:PcHeaders -Body @{
        id = $invoiceId
        customerId = $customerId
        customerName = $customer.nameOriginal
        tenantCode = 'USENET'
        officeCode = 'USENET'
        responsibleOfficeCode = 'USENET'
        voucherType = 'Sales'
        invoiceDate = '2026-05-26'
        vatMode = '포함'
        memo = 'created for same-account smoke'
        lines = @(@{
            id = $invoiceLineId
            invoiceId = $invoiceId
            itemId = $itemId
            itemNameOriginal = $item.nameOriginal
            specificationOriginal = 'SMOKE'
            unit = 'EA'
            quantity = 1
            unitPrice = 1000
            lineAmount = 1000
            itemTrackingType = '비재고'
        })
    } -ExpectedStatus @(200)).Body
    $steps.Add([pscustomobject]@{ Step = 'fixture-invoice-create'; Result = 'PASS'; Id = $invoice.id })

    $payment = (Invoke-Api -Method POST -Path 'payments' -Headers $script:PcHeaders -Body @{
        id = $paymentId
        invoiceId = $invoiceId
        paymentDate = '2026-05-26'
        amount = 100
        note = 'created for same-account smoke'
    } -ExpectedStatus @(200)).Body
    $steps.Add([pscustomobject]@{ Step = 'fixture-payment-create'; Result = 'PASS'; Id = $payment.id })

    $steps.Add((Invoke-StaleThenFreshUpdate `
        -EntityName 'customer' `
        -GetPath "customers/$customerId" `
        -UpdatePath "customers/$customerId" `
        -Mutate { param($dto, $label) $dto.notes = $label }))

    $steps.Add((Invoke-StaleThenFreshUpdate `
        -EntityName 'item' `
        -GetPath "items/$itemId" `
        -UpdatePath "items/$itemId" `
        -Mutate { param($dto, $label) $dto.simpleMemo = $label }))

    $steps.Add((Invoke-StaleThenFreshUpdate `
        -EntityName 'invoice' `
        -GetPath "invoices/$invoiceId" `
        -UpdatePath "invoices/$invoiceId" `
        -Mutate { param($dto, $label) $dto.memo = $label }))

    $pcPaymentSnapshot = Get-PaymentByInvoice -InvoiceId $invoiceId -PaymentId $paymentId -Headers $script:PcHeaders
    $mobilePaymentSnapshot = Get-PaymentByInvoice -InvoiceId $invoiceId -PaymentId $paymentId -Headers $script:MobileHeaders
    $pcPaymentUpdate = Set-ExpectedRevision (Clone-JsonObject $pcPaymentSnapshot)
    $pcPaymentUpdate.note = 'PC first save'
    $pcPaymentSaved = (Invoke-Api -Method PUT -Path "payments/$paymentId" -Headers $script:PcHeaders -Body $pcPaymentUpdate -ExpectedStatus @(200)).Body
    $mobileStalePayment = Set-ExpectedRevision (Clone-JsonObject $mobilePaymentSnapshot)
    $mobileStalePayment.note = 'Mobile stale save'
    $paymentConflict = Invoke-Api -Method PUT -Path "payments/$paymentId" -Headers $script:MobileHeaders -Body $mobileStalePayment -ExpectedStatus @(409)
    $mobileFreshPayment = Get-PaymentByInvoice -InvoiceId $invoiceId -PaymentId $paymentId -Headers $script:MobileHeaders
    $mobilePaymentUpdate = Set-ExpectedRevision (Clone-JsonObject $mobileFreshPayment)
    $mobilePaymentUpdate.note = 'Mobile fresh save'
    $mobilePaymentSaved = (Invoke-Api -Method PUT -Path "payments/$paymentId" -Headers $script:MobileHeaders -Body $mobilePaymentUpdate -ExpectedStatus @(200)).Body
    $steps.Add([pscustomobject]@{
        Entity = 'payment'
        PcInitialRevision = [Int64]$pcPaymentSnapshot.revision
        PcSavedRevision = [Int64]$pcPaymentSaved.revision
        MobileStaleRevision = [Int64]$mobilePaymentSnapshot.revision
        ConflictStatus = $paymentConflict.Status
        FreshRevision = [Int64]$mobileFreshPayment.revision
        MobileSavedRevision = [Int64]$mobilePaymentSaved.revision
        Result = 'PASS'
    })
}
finally {
    if (-not $KeepTemporaryData) {
        if ($paymentId -ne [Guid]::Empty -and $invoiceId -ne [Guid]::Empty) { $cleanup.Add((Remove-PaymentQuietly -InvoiceId $invoiceId -PaymentId $paymentId -Headers $script:PcHeaders)) }
        if ($invoiceId -ne [Guid]::Empty) { $cleanup.Add((Remove-EntityQuietly -EntityName 'invoice' -GetPath "invoices/$invoiceId" -DeletePath "invoices/$invoiceId" -Headers $script:PcHeaders)) }
        if ($itemId -ne [Guid]::Empty) { $cleanup.Add((Remove-EntityQuietly -EntityName 'item' -GetPath "items/$itemId" -DeletePath "items/$itemId" -Headers $script:PcHeaders)) }
        if ($customerId -ne [Guid]::Empty) { $cleanup.Add((Remove-EntityQuietly -EntityName 'customer' -GetPath "customers/$customerId" -DeletePath "customers/$customerId" -Headers $script:PcHeaders)) }
    }
}

$result = [pscustomobject]@{
    CreatedAt = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    BaseUrl = $BaseUrl
    Username = $Username
    Result = 'PASS'
    Steps = $steps
    Cleanup = $cleanup
}

$jsonPath = Join-Path $EvidenceDirectory "same-account-concurrency-smoke-$timestamp.json"
$mdPath = Join-Path $EvidenceDirectory "same-account-concurrency-smoke-$timestamp.md"
$result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

$md = @(
    '# PC/모바일 같은 계정 동시수정 스모크 검증',
    '',
    "- 작성시각: $($result.CreatedAt)",
    "- API: $BaseUrl",
    "- 계정: $Username",
    "- 결과: PASS",
    '',
    '## 단계',
    ''
)
foreach ($step in $steps) {
    if ($step.Entity) {
        $md += "- $($step.Entity): $($step.Result) — stale conflict=$($step.ConflictStatus), fresh revision=$($step.FreshRevision) -> $($step.MobileSavedRevision)"
    }
    else {
        $md += "- $($step.Step): $($step.Result) $($step.Id)"
    }
}
$md += ''
$md += '## 정리'
foreach ($row in $cleanup) {
    $md += "- $($row.Entity): $($row.Result)"
}
$md += ''
$md += "JSON: $jsonPath"
$md | Set-Content -LiteralPath $mdPath -Encoding UTF8

Write-Host "same_account_concurrency_report=$mdPath"
Write-Host "same_account_concurrency_json=$jsonPath"
Write-Host 'result=PASS'
