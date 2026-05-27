param(
    [string]$BaseUrl = 'http://127.0.0.1:19080',
    [string]$Username = 'usenet',
    [string]$Password = '1234',
    [string]$BearerToken = '',
    [string]$EvidenceDirectory = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path 'output\repeated-save-smoke'),
    [int]$RepeatCount = 3,
    [switch]$KeepTemporaryData
)

$ErrorActionPreference = 'Stop'

function New-DirectoryIfMissing {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -ItemType Directory -Force -Path $Path | Out-Null
    }
}

function Read-ErrorResponseBody {
    param([System.Net.WebResponse]$Response)
    if ($null -eq $Response) { return '' }
    try {
        $stream = $Response.GetResponseStream()
        if ($null -eq $stream) { return '' }
        $reader = [System.IO.StreamReader]::new($stream)
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
        [string]$Relative,
        [hashtable]$Headers,
        [object]$Body = $null,
        [int[]]$ExpectedStatus = @(200),
        [int]$TimeoutSec = 60
    )

    $uri = if ($Relative.StartsWith('http', [System.StringComparison]::OrdinalIgnoreCase)) {
        $Relative
    }
    else {
        "$($BaseUrl.TrimEnd('/'))/$($Relative.TrimStart('/'))"
    }

    $parameters = @{
        Method = $Method
        Uri = $uri
        UseBasicParsing = $true
        TimeoutSec = $TimeoutSec
    }
    if ($Headers) {
        $parameters.Headers = $Headers
    }
    if ($null -ne $Body) {
        $parameters.Body = ($Body | ConvertTo-Json -Depth 30 -Compress)
        $parameters.ContentType = 'application/json; charset=utf-8'
    }

    $status = 0
    $content = ''
    try {
        $response = Invoke-WebRequest @parameters
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

    $bodyObject = $null
    if (-not [string]::IsNullOrWhiteSpace($content)) {
        try { $bodyObject = $content | ConvertFrom-Json }
        catch { $bodyObject = $content }
    }

    [pscustomobject]@{
        StatusCode = $status
        Body = $bodyObject
        Raw = $content
        Uri = $uri
    }
}

function New-AuthHeaders {
    if ([string]::IsNullOrWhiteSpace($BearerToken)) {
        $login = Invoke-Api -Method POST -Relative 'auth/login' -Body @{
            username = $Username
            password = $Password
        } -Headers @{} -ExpectedStatus @(200) -TimeoutSec 20

        $script:BearerToken = [string]$login.Body.token
        if ([string]::IsNullOrWhiteSpace($script:BearerToken)) {
            $script:BearerToken = [string]$login.Body.accessToken
        }
    }

    if ([string]::IsNullOrWhiteSpace($script:BearerToken)) {
        throw '로그인 토큰을 받지 못했습니다.'
    }

    return @{
        Authorization = "Bearer $script:BearerToken"
        'X-Business-Database' = 'USENET'
    }
}

function Add-Step {
    param(
        [string]$Target,
        [string]$Action,
        [string]$Result,
        [string]$Detail,
        [long]$Revision = 0
    )

    $script:Steps.Add([pscustomobject]@{
        Target = $Target
        Action = $Action
        Result = $Result
        Revision = $Revision
        Detail = $Detail
    }) | Out-Null
}

function Assert-RevisionAdvanced {
    param(
        [string]$Target,
        [long]$PreviousRevision,
        [long]$CurrentRevision
    )

    if ($CurrentRevision -le $PreviousRevision) {
        throw "$Target revision did not advance. previous=$PreviousRevision current=$CurrentRevision"
    }
}

function Set-ExpectedRevision {
    param([object]$Dto)
    $Dto.expectedRevision = [long]$Dto.revision
    $Dto.mutationId = ([guid]::NewGuid()).ToString()
    $Dto.mutationCreatedAtUtc = [DateTime]::UtcNow.ToString('o')
    return $Dto
}

function Copy-JsonObject {
    param([object]$InputObject)
    return ($InputObject | ConvertTo-Json -Depth 30 | ConvertFrom-Json)
}

function Get-ById {
    param(
        [string]$Relative,
        [hashtable]$Headers
    )
    return (Invoke-Api -Method GET -Relative $Relative -Headers $Headers -ExpectedStatus @(200)).Body
}

function Invoke-RepeatedDirectUpdate {
    param(
        [string]$Target,
        [string]$Relative,
        [object]$Dto,
        [hashtable]$Headers,
        [scriptblock]$Mutate
    )

    $current = $Dto
    for ($i = 1; $i -le $RepeatCount; $i++) {
        $beforeRevision = [long]$current.revision
        $payload = Copy-JsonObject $current
        $payload = Set-ExpectedRevision $payload
        & $Mutate $payload $i
        $updated = (Invoke-Api -Method PUT -Relative "$Relative/$($payload.id)" -Headers $Headers -Body $payload -ExpectedStatus @(200)).Body
        Assert-RevisionAdvanced -Target $Target -PreviousRevision $beforeRevision -CurrentRevision ([long]$updated.revision)
        Add-Step -Target $Target -Action "update-$i" -Result 'PASS' -Revision ([long]$updated.revision) -Detail "expected=$beforeRevision"
        $current = $updated
    }

    return $current
}

function Invoke-SyncPushOne {
    param(
        [string]$CollectionName,
        [object]$Dto,
        [hashtable]$Headers
    )

    $body = @{
        deviceId = 'repeated-save-smoke'
    }
    foreach ($name in @(
        'companyProfiles','units','customerCategories','priceGradeOptions','tradeTypeOptions','itemCategoryOptions',
        'customerMasters','customers','customerContracts','items','itemWarehouseStocks','transactions','transactionAttachments',
        'inventoryTransfers','rentalManagementCompanies','rentalBillingProfiles','rentalAssets','rentalAssetAssignmentHistories',
        'rentalBillingLogs','invoices','payments'
    )) {
        $body[$name] = @()
    }
    $body[$CollectionName] = @($Dto)

    $result = (Invoke-Api -Method POST -Relative 'sync/push' -Headers $Headers -Body $body -ExpectedStatus @(200) -TimeoutSec 120).Body
    if ([int]$result.conflictCount -ne 0) {
        throw "sync/push conflict for $CollectionName. conflicts=$($result.conflicts | ConvertTo-Json -Depth 10 -Compress)"
    }
    if ([int]$result.acceptedCount -lt 1 -and [int]$result.duplicateMutationCount -lt 1) {
        throw "sync/push did not accept $CollectionName. result=$($result | ConvertTo-Json -Depth 10 -Compress)"
    }
    return $result
}

function Get-FromFullPullById {
    param(
        [string]$CollectionName,
        [string]$Id,
        [hashtable]$Headers
    )

    $pull = (Invoke-Api -Method GET -Relative 'sync/pull?sinceRev=0' -Headers $Headers -ExpectedStatus @(200) -TimeoutSec 120).Body
    $collection = @($pull.$CollectionName)
    $match = $collection | Where-Object { [string]$_.id -eq $Id } | Select-Object -First 1
    if ($null -eq $match) {
        throw "sync/pull에서 $CollectionName/$Id 항목을 찾지 못했습니다."
    }
    return $match
}

function Get-DefaultRentalManagementCompanyCode {
    param([hashtable]$Headers)

    $pull = (Invoke-Api -Method GET -Relative 'sync/pull?sinceRev=0' -Headers $Headers -ExpectedStatus @(200) -TimeoutSec 120).Body
    $company = @($pull.rentalManagementCompanies) |
        Where-Object { -not $_.isDeleted -and ($_.isActive -eq $true -or $null -eq $_.isActive) -and -not [string]::IsNullOrWhiteSpace([string]$_.code) } |
        Select-Object -First 1
    if ($null -eq $company) {
        return 'USENET'
    }

    return [string]$company.code
}

function Invoke-RentalBillingProfileRepeatedSave {
    param(
        [object]$Customer,
        [hashtable]$Headers,
        [string]$Stamp
    )

    $profileId = ([guid]::NewGuid()).ToString()
    $managementCompanyCode = Get-DefaultRentalManagementCompanyCode -Headers $Headers
    $profile = [pscustomobject]@{
        id = $profileId
        tenantCode = 'USENET_GROUP'
        officeCode = 'USENET'
        responsibleOfficeCode = 'USENET'
        profileKey = "repeated-save-profile-$Stamp"
        customerId = $Customer.id
        customerName = $Customer.nameOriginal
        businessNumber = ''
        itemName = "반복저장 렌탈료 $Stamp"
        billingType = '묶음'
        installSiteName = '반복저장 테스트'
        billingAdvanceMode = '후불'
        managementCompanyCode = $managementCompanyCode
        billingMethod = '전자세금계산서'
        billingStatus = '청구대기'
        email = ''
        billingDay = 25
        billingDayMode = '고정일'
        billingCycleMonths = 1
        billingAnchorMonth = 1
        documentIssueMode = '결제일과 동일'
        documentLeadDays = 0
        monthlyAmount = 11000
        depositAmount = 0
        submissionDocuments = ''
        notes = 'repeated-save-smoke create'
        billingAnchorDate = (Get-Date -Format 'yyyy-MM-dd')
        billingStartDate = (Get-Date -Format 'yyyy-MM-dd')
        contractDate = (Get-Date -Format 'yyyy-MM-dd')
        contractStartDate = (Get-Date -Format 'yyyy-MM-dd')
        contractEndDate = $null
        settlementStatus = ''
        completionStatus = ''
        settledAmount = 0
        outstandingAmount = 0
        requiresFollowUp = $false
        billingTemplateJson = '[]'
        billingRunsJson = '[]'
        isActive = $true
        isDeleted = $false
        revision = 0
        expectedRevision = 0
        mutationId = ([guid]::NewGuid()).ToString()
        mutationCreatedAtUtc = [DateTime]::UtcNow.ToString('o')
    }

    Invoke-SyncPushOne -CollectionName 'rentalBillingProfiles' -Dto $profile -Headers $Headers | Out-Null
    $current = Get-FromFullPullById -CollectionName 'rentalBillingProfiles' -Id $profileId -Headers $Headers
    Add-Step -Target 'rentalBillingProfile' -Action 'create' -Result 'PASS' -Revision ([long]$current.revision) -Detail $profileId

    for ($i = 1; $i -le $RepeatCount; $i++) {
        $beforeRevision = [long]$current.revision
        $payload = Copy-JsonObject $current
        $payload = Set-ExpectedRevision $payload
        $payload.notes = "repeated-save-smoke update-$i"
        $payload.monthlyAmount = 11000 + ($i * 100)
        Invoke-SyncPushOne -CollectionName 'rentalBillingProfiles' -Dto $payload -Headers $Headers | Out-Null
        $current = Get-FromFullPullById -CollectionName 'rentalBillingProfiles' -Id $profileId -Headers $Headers
        Assert-RevisionAdvanced -Target 'rentalBillingProfile' -PreviousRevision $beforeRevision -CurrentRevision ([long]$current.revision)
        Add-Step -Target 'rentalBillingProfile' -Action "update-$i" -Result 'PASS' -Revision ([long]$current.revision) -Detail "expected=$beforeRevision"
    }

    return $current
}

function Remove-Entity {
    param(
        [string]$Target,
        [string]$Relative,
        [object]$Dto,
        [hashtable]$Headers
    )

    if ($null -eq $Dto -or [string]::IsNullOrWhiteSpace([string]$Dto.id)) {
        return
    }

    try {
        $latest = Get-ById -Relative "$Relative/$($Dto.id)" -Headers $Headers
        Invoke-Api -Method DELETE -Relative "$Relative/$($Dto.id)?expectedRevision=$($latest.revision)" -Headers $Headers -ExpectedStatus @(204) | Out-Null
        Add-Step -Target $Target -Action 'cleanup-delete' -Result 'PASS' -Revision ([long]$latest.revision) -Detail ([string]$Dto.id)
    }
    catch {
        Add-Step -Target $Target -Action 'cleanup-delete' -Result 'WARN' -Revision 0 -Detail $_.Exception.Message
    }
}

function Remove-Payment {
    param(
        [object]$Dto,
        [string]$InvoiceId,
        [hashtable]$Headers
    )

    if ($null -eq $Dto -or [string]::IsNullOrWhiteSpace([string]$Dto.id) -or [string]::IsNullOrWhiteSpace($InvoiceId)) {
        return
    }

    try {
        $payments = (Invoke-Api -Method GET -Relative "payments?invoiceId=$([Uri]::EscapeDataString($InvoiceId))" -Headers $Headers -ExpectedStatus @(200)).Body
        $latest = @($payments) | Where-Object { [string]$_.id -eq [string]$Dto.id } | Select-Object -First 1
        if ($null -eq $latest) {
            Add-Step -Target 'payment' -Action 'cleanup-delete' -Result 'PASS' -Revision 0 -Detail "already absent: $($Dto.id)"
            return
        }

        Invoke-Api -Method DELETE -Relative "payments/$($latest.id)?expectedRevision=$($latest.revision)" -Headers $Headers -ExpectedStatus @(204) | Out-Null
        Add-Step -Target 'payment' -Action 'cleanup-delete' -Result 'PASS' -Revision ([long]$latest.revision) -Detail ([string]$Dto.id)
    }
    catch {
        Add-Step -Target 'payment' -Action 'cleanup-delete' -Result 'WARN' -Revision 0 -Detail $_.Exception.Message
    }
}

function Remove-RentalBillingProfile {
    param(
        [object]$Dto,
        [hashtable]$Headers
    )

    if ($null -eq $Dto -or [string]::IsNullOrWhiteSpace([string]$Dto.id)) {
        return
    }

    try {
        $latest = Get-FromFullPullById -CollectionName 'rentalBillingProfiles' -Id ([string]$Dto.id) -Headers $Headers
        $payload = Copy-JsonObject $latest
        $payload = Set-ExpectedRevision $payload
        $payload.isDeleted = $true
        Invoke-SyncPushOne -CollectionName 'rentalBillingProfiles' -Dto $payload -Headers $Headers | Out-Null
        Add-Step -Target 'rentalBillingProfile' -Action 'cleanup-delete' -Result 'PASS' -Revision ([long]$latest.revision) -Detail ([string]$Dto.id)
    }
    catch {
        Add-Step -Target 'rentalBillingProfile' -Action 'cleanup-delete' -Result 'WARN' -Revision 0 -Detail $_.Exception.Message
    }
}

New-DirectoryIfMissing $EvidenceDirectory
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$jsonPath = Join-Path $EvidenceDirectory "repeated-save-smoke-$timestamp.json"
$mdPath = Join-Path $EvidenceDirectory "repeated-save-smoke-$timestamp.md"
$script:Steps = New-Object System.Collections.Generic.List[object]

$headers = New-AuthHeaders
$stamp = Get-Date -Format 'yyyyMMddHHmmssfff'
$createdCustomer = $null
$createdItem = $null
$createdInvoice = $null
$createdPayment = $null
$createdRentalProfile = $null

try {
    $customer = @{
        id = ([guid]::NewGuid()).ToString()
        tenantCode = 'USENET_GROUP'
        officeCode = 'USENET'
        responsibleOfficeCode = 'USENET'
        nameOriginal = "반복저장검증 거래처 $stamp"
        phone = '032-000-0000'
        mobilePhone = '010-0000-0000'
        priceGrade = 'A'
        notes = 'repeated-save-smoke create'
    }
    $createdCustomer = (Invoke-Api -Method POST -Relative 'customers' -Headers $headers -Body $customer -ExpectedStatus @(200)).Body
    Add-Step -Target 'customer' -Action 'create' -Result 'PASS' -Revision ([long]$createdCustomer.revision) -Detail ([string]$createdCustomer.id)
    $createdCustomer = Invoke-RepeatedDirectUpdate -Target 'customer' -Relative 'customers' -Dto $createdCustomer -Headers $headers -Mutate {
        param($payload, $index)
        $payload.notes = "repeated-save-smoke update-$index"
        $payload.phone = "032-000-00$index"
    }

    $item = @{
        id = ([guid]::NewGuid()).ToString()
        tenantCode = 'USENET_GROUP'
        officeCode = 'USENET'
        nameOriginal = "반복저장검증 품목 $stamp"
        specificationOriginal = "RS-$stamp"
        categoryName = '반복저장검증'
        itemKind = '청구항목'
        trackingType = '비재고'
        unit = 'EA'
        currentStock = 0
        safetyStock = 0
        purchasePrice = 1000
        salePrice = 2000
        retailPrice = 2200
        priceGradeA = 2100
        priceGradeB = 2050
        priceGradeC = 2000
        isSale = $true
        simpleMemo = ''
        notes = 'repeated-save-smoke create'
    }
    $createdItem = (Invoke-Api -Method POST -Relative 'items' -Headers $headers -Body $item -ExpectedStatus @(200)).Body
    Add-Step -Target 'item' -Action 'create' -Result 'PASS' -Revision ([long]$createdItem.revision) -Detail ([string]$createdItem.id)
    $createdItem = Invoke-RepeatedDirectUpdate -Target 'item' -Relative 'items' -Dto $createdItem -Headers $headers -Mutate {
        param($payload, $index)
        $payload.notes = "repeated-save-smoke update-$index"
        $payload.salePrice = 2000 + ($index * 100)
        $payload.priceGradeA = 2100 + ($index * 100)
    }

    $invoiceId = ([guid]::NewGuid()).ToString()
    $lineId = ([guid]::NewGuid()).ToString()
    $today = Get-Date -Format 'yyyy-MM-dd'
    $invoice = @{
        id = $invoiceId
        customerId = $createdCustomer.id
        customerName = $createdCustomer.nameOriginal
        tenantCode = 'USENET_GROUP'
        officeCode = 'USENET'
        responsibleOfficeCode = 'USENET'
        voucherType = 'Sales'
        invoiceDate = $today
        totalAmount = 2200
        supplyAmount = 2000
        vatAmount = 200
        vatMode = 'Included'
        taxInvoiceIssued = $false
        sourceWarehouseCode = 'USENET'
        memo = 'repeated-save-smoke create'
        lines = @(@{
            id = $lineId
            invoiceId = $invoiceId
            itemId = $createdItem.id
            itemNameOriginal = $createdItem.nameOriginal
            specificationOriginal = $createdItem.specificationOriginal
            unit = 'EA'
            quantity = 1
            unitPrice = 2200
            lineAmount = 2200
            remark = 'repeated-save-smoke'
            itemTrackingType = '비재고'
            isDeleted = $false
        })
    }
    $createdInvoice = (Invoke-Api -Method POST -Relative 'invoices' -Headers $headers -Body $invoice -ExpectedStatus @(200)).Body
    Add-Step -Target 'invoice' -Action 'create' -Result 'PASS' -Revision ([long]$createdInvoice.revision) -Detail ([string]$createdInvoice.id)
    $createdInvoice = Invoke-RepeatedDirectUpdate -Target 'invoice' -Relative 'invoices' -Dto $createdInvoice -Headers $headers -Mutate {
        param($payload, $index)
        $amount = 2200 + ($index * 110)
        $payload.memo = "repeated-save-smoke update-$index"
        $payload.totalAmount = $amount
        $payload.supplyAmount = $amount - [decimal]($amount / 11)
        $payload.vatAmount = [decimal]($amount / 11)
        $payload.lines[0].unitPrice = $amount
        $payload.lines[0].lineAmount = $amount
    }

    $payment = @{
        id = ([guid]::NewGuid()).ToString()
        invoiceId = $createdInvoice.id
        paymentDate = $today
        amount = 1000
        note = 'repeated-save-smoke create'
        attachments = @()
    }
    $createdPayment = (Invoke-Api -Method POST -Relative 'payments' -Headers $headers -Body $payment -ExpectedStatus @(200)).Body
    Add-Step -Target 'payment' -Action 'create' -Result 'PASS' -Revision ([long]$createdPayment.revision) -Detail ([string]$createdPayment.id)
    $createdPayment = Invoke-RepeatedDirectUpdate -Target 'payment' -Relative 'payments' -Dto $createdPayment -Headers $headers -Mutate {
        param($payload, $index)
        $payload.note = "repeated-save-smoke update-$index"
    }

    $createdRentalProfile = Invoke-RentalBillingProfileRepeatedSave -Customer $createdCustomer -Headers $headers -Stamp $stamp

    $overall = 'PASS'
}
catch {
    $overall = 'FAIL'
    Add-Step -Target 'script' -Action 'exception' -Result 'FAIL' -Revision 0 -Detail $_.Exception.Message
    throw
}
finally {
    if (-not $KeepTemporaryData) {
        Remove-RentalBillingProfile -Dto $createdRentalProfile -Headers $headers
        Remove-Payment -Dto $createdPayment -InvoiceId ([string]$createdInvoice.id) -Headers $headers
        Remove-Entity -Target 'invoice' -Relative 'invoices' -Dto $createdInvoice -Headers $headers
        Remove-Entity -Target 'item' -Relative 'items' -Dto $createdItem -Headers $headers
        Remove-Entity -Target 'customer' -Relative 'customers' -Dto $createdCustomer -Headers $headers
    }

    $report = [pscustomobject]@{
        GeneratedAt = (Get-Date).ToString('o')
        BaseUrl = $BaseUrl
        RepeatCount = $RepeatCount
        Overall = $overall
        Steps = @($script:Steps.ToArray())
    }
    $report | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('# 거래플랜 반복 저장 스모크') | Out-Null
    $lines.Add('') | Out-Null
    $lines.Add("- 실행시각: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')") | Out-Null
    $lines.Add("- BaseUrl: $BaseUrl") | Out-Null
    $lines.Add("- 반복 횟수: $RepeatCount") | Out-Null
    $lines.Add("- 결과: **$overall**") | Out-Null
    $lines.Add("- JSON: $jsonPath") | Out-Null
    $lines.Add('') | Out-Null
    $lines.Add('| 대상 | 작업 | 결과 | Revision | 상세 |') | Out-Null
    $lines.Add('|---|---|---|---:|---|') | Out-Null
    foreach ($step in $script:Steps) {
        $detail = ([string]$step.Detail).Replace('|', '\|').Replace("`r", ' ').Replace("`n", ' ')
        $lines.Add("| $($step.Target) | $($step.Action) | $($step.Result) | $($step.Revision) | $detail |") | Out-Null
    }
    $lines | Set-Content -LiteralPath $mdPath -Encoding UTF8

    Write-Host "repeated_save_smoke_report=$mdPath"
    Write-Host "repeated_save_smoke_json=$jsonPath"
    Write-Host "result=$overall"
}

if ($overall -ne 'PASS') {
    exit 1
}

exit 0
