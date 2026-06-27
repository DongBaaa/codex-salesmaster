param(
    [string]$BaseUrl = 'http://127.0.0.1:19080',
    [string]$Username = 'usenet',
    [string]$Password = '1234',
    [Alias('OutputDirectory')]
    [string]$EvidenceDirectory = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path 'output\api-visibility-smoke'),
    [int]$TimeoutSec = 30,
    [int]$IntegrityTimeoutSec = 90,
    [int]$IntegrityRetryCount = 2,
    [int]$IntegrityRetryDelaySeconds = 5,
    [ValidateRange(1, 100)]
    [int]$IntegrityDetailSampleLimit = 10,
    [int]$MinCustomers = 1,
    [int]$MinItems = 1,
    [int]$MinInvoices = 1,
    [switch]$FailOnIntegrityWarnings,
    [string[]]$AllowedIntegrityWarningCodes = @()
)

$ErrorActionPreference = 'Stop'

function New-DirectoryIfMissing {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -ItemType Directory -Force -Path $Path | Out-Null
    }
}

function Get-ListCount {
    param([object]$Value)

    if ($null -eq $Value) {
        return 0
    }

    return @($Value).Count
}

function Invoke-ApiJson {
    param(
        [string]$Method,
        [string]$Path,
        [hashtable]$Headers = @{},
        [object]$Body = $null,
        [int]$RequestTimeoutSec = $TimeoutSec,
        [int]$Attempt = 1
    )

    $uri = $BaseUrl.TrimEnd('/') + $Path
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $parameters = @{
        Method = $Method
        Uri = $uri
        Headers = $Headers
        TimeoutSec = $RequestTimeoutSec
    }

    if ($null -ne $Body) {
        $parameters.ContentType = 'application/json; charset=utf-8'
        $parameters.Body = ($Body | ConvertTo-Json -Depth 20 -Compress)
    }

    try {
        $response = Invoke-RestMethod @parameters
        $script:EndpointObservations.Add([pscustomobject]@{
            Method = $Method
            Path = $Path
            Attempt = $Attempt
            Result = 'PASS'
            ElapsedMs = $stopwatch.ElapsedMilliseconds
            Error = ''
        }) | Out-Null
        return $response
    }
    catch {
        $script:EndpointObservations.Add([pscustomobject]@{
            Method = $Method
            Path = $Path
            Attempt = $Attempt
            Result = 'FAIL'
            ElapsedMs = $stopwatch.ElapsedMilliseconds
            Error = $_.Exception.Message
        }) | Out-Null
        throw
    }
}

function Get-IntegrityIssueDetailPath {
    param([string]$Code)
    return '/integrity/report/details?code=' + [Uri]::EscapeDataString($Code)
}

function Invoke-ApiJsonWithRetry {
    param(
        [string]$Method,
        [string]$Path,
        [hashtable]$Headers = @{},
        [object]$Body = $null,
        [int]$RequestTimeoutSec = $TimeoutSec,
        [int]$RetryCount = 1,
        [int]$RetryDelaySeconds = 3
    )

    $lastError = $null
    $attempts = [Math]::Max(1, $RetryCount)
    for ($attempt = 1; $attempt -le $attempts; $attempt++) {
        try {
            return Invoke-ApiJson `
                -Method $Method `
                -Path $Path `
                -Headers $Headers `
                -Body $Body `
                -RequestTimeoutSec $RequestTimeoutSec `
                -Attempt $attempt
        }
        catch {
            $lastError = $_
            if ($attempt -lt $attempts) {
                Start-Sleep -Seconds $RetryDelaySeconds
            }
        }
    }

    throw $lastError
}

New-DirectoryIfMissing $EvidenceDirectory
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$jsonPath = Join-Path $EvidenceDirectory "api-visibility-smoke-$timestamp.json"
$mdPath = Join-Path $EvidenceDirectory "api-visibility-smoke-$timestamp.md"
$script:EndpointObservations = New-Object System.Collections.Generic.List[object]

$login = Invoke-ApiJson -Method 'Post' -Path '/auth/login' -Body @{ username = $Username; password = $Password }
if ([string]::IsNullOrWhiteSpace([string]$login.token)) {
    throw '로그인 토큰을 받지 못했습니다.'
}

$headers = @{ Authorization = 'Bearer ' + $login.token }
$customers = Invoke-ApiJson -Method 'Get' -Path '/customers?take=5000' -Headers $headers
$items = Invoke-ApiJson -Method 'Get' -Path '/items?take=5000' -Headers $headers
$invoices = Invoke-ApiJson -Method 'Get' -Path '/invoices?take=500' -Headers $headers
$customerCategories = Invoke-ApiJson -Method 'Get' -Path '/customer-categories' -Headers $headers
$units = Invoke-ApiJson -Method 'Get' -Path '/units' -Headers $headers
$integrity = Invoke-ApiJsonWithRetry -Method 'Get' -Path '/integrity/report' -Headers $headers -RequestTimeoutSec $IntegrityTimeoutSec -RetryCount $IntegrityRetryCount -RetryDelaySeconds $IntegrityRetryDelaySeconds

$customerCount = Get-ListCount $customers
$itemCount = Get-ListCount $items
$invoiceCount = Get-ListCount $invoices
$categoryCount = Get-ListCount $customerCategories
$unitCount = Get-ListCount $units
$integrityIssues = @($integrity.issues)
$integrityErrorMeasure = $integrityIssues | Where-Object { [string]$_.severity -eq 'Error' } | Measure-Object
$integrityErrorCount = [int]$integrityErrorMeasure.Count
$allowedWarningCodeSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
foreach ($code in $AllowedIntegrityWarningCodes) {
    if (-not [string]::IsNullOrWhiteSpace($code)) {
        [void]$allowedWarningCodeSet.Add($code.Trim())
    }
}
$integrityWarnings = @($integrityIssues | Where-Object { [string]$_.severity -eq 'Warning' })
$blockingIntegrityWarnings = @($integrityWarnings | Where-Object {
    $code = [string]$_.code
    [string]::IsNullOrWhiteSpace($code) -or -not $allowedWarningCodeSet.Contains($code.Trim())
})
$integrityWarningCount = [int]$integrityWarnings.Count
$blockingIntegrityWarningCount = [int]$blockingIntegrityWarnings.Count

$integrityDetailTargets = @($integrityIssues | Where-Object {
    $severity = [string]$_.severity
    $code = ([string]$_.code).Trim()
    [string]::Equals($severity, 'Error', [System.StringComparison]::OrdinalIgnoreCase) -or
        ([string]::Equals($severity, 'Warning', [System.StringComparison]::OrdinalIgnoreCase) -and
            (-not [string]::IsNullOrWhiteSpace($code)) -and
            (-not $allowedWarningCodeSet.Contains($code)))
})
$integrityDetails = New-Object System.Collections.Generic.List[object]
foreach ($issue in $integrityDetailTargets) {
    $code = ([string]$issue.code).Trim()
    if ([string]::IsNullOrWhiteSpace($code)) {
        continue
    }

    $detail = Invoke-ApiJson -Method 'Get' -Path (Get-IntegrityIssueDetailPath -Code $code) -Headers $headers -RequestTimeoutSec $IntegrityTimeoutSec
    $rows = @($detail.rows)
    $integrityDetails.Add([pscustomobject]@{
        Code = $code
        Severity = [string]$issue.severity
        Count = [int]$issue.count
        DetailCount = [int]$detail.detailCount
        Message = [string]$detail.message
        SampleRows = @($rows | Select-Object -First $IntegrityDetailSampleLimit)
    }) | Out-Null
}

$customerDetailOk = $false
$customerRecentInvoiceCount = 0
$firstCustomerId = ''
if ($customerCount -gt 0) {
    $firstCustomer = @($customers)[0]
    $firstCustomerId = [string]$firstCustomer.id
    if (-not [string]::IsNullOrWhiteSpace($firstCustomerId)) {
        $customerDetail = Invoke-ApiJson -Method 'Get' -Path ("/customers/$firstCustomerId/detail") -Headers $headers
        $customerDetailOk = $null -ne $customerDetail.customer
        $customerRecentInvoiceCount = Get-ListCount $customerDetail.recentInvoices
    }
}

$invoiceDetailOk = $false
$paymentListOk = $false
$firstInvoiceId = ''
$paymentCount = 0
if ($invoiceCount -gt 0) {
    $firstInvoice = @($invoices)[0]
    $firstInvoiceId = [string]$firstInvoice.id
    if (-not [string]::IsNullOrWhiteSpace($firstInvoiceId)) {
        $invoiceDetail = Invoke-ApiJson -Method 'Get' -Path ("/invoices/$firstInvoiceId") -Headers $headers
        $invoiceDetailOk = $null -ne $invoiceDetail.id
        $payments = Invoke-ApiJson -Method 'Get' -Path ("/payments?invoiceId=$firstInvoiceId") -Headers $headers
        $paymentListOk = $true
        $paymentCount = Get-ListCount $payments
    }
}

$failures = New-Object System.Collections.Generic.List[string]
if ($customerCount -lt $MinCustomers) { $failures.Add("거래처 API 표시 건수가 기준보다 적습니다. actual=$customerCount, min=$MinCustomers") | Out-Null }
if ($itemCount -lt $MinItems) { $failures.Add("품목 API 표시 건수가 기준보다 적습니다. actual=$itemCount, min=$MinItems") | Out-Null }
if ($invoiceCount -lt $MinInvoices) { $failures.Add("전표 API 표시 건수가 기준보다 적습니다. actual=$invoiceCount, min=$MinInvoices") | Out-Null }
if ($customerCount -gt 0 -and -not $customerDetailOk) { $failures.Add('거래처 상세 API가 첫 거래처를 정상 반환하지 않았습니다.') | Out-Null }
if ($invoiceCount -gt 0 -and -not $invoiceDetailOk) { $failures.Add('전표 상세 API가 첫 전표를 정상 반환하지 않았습니다.') | Out-Null }
if ($invoiceCount -gt 0 -and -not $paymentListOk) { $failures.Add('수금/지급 API가 첫 전표 기준 목록을 정상 반환하지 않았습니다.') | Out-Null }
if ($integrityErrorCount -gt 0) { $failures.Add("무결성 Error 항목이 남아 있습니다. count=$integrityErrorCount") | Out-Null }
if ($FailOnIntegrityWarnings -and $blockingIntegrityWarningCount -gt 0) {
    $warningSummary = (@($blockingIntegrityWarnings | ForEach-Object { '{0}({1})' -f ([string]$_.code), ([int]$_.count) }) -join ', ')
    $failures.Add("무결성 Warning 항목이 남아 있습니다. count=$blockingIntegrityWarningCount, warnings=$warningSummary") | Out-Null
}

$overall = if ($failures.Count -eq 0) { 'PASS' } else { 'FAIL' }
$result = [pscustomobject]@{
    GeneratedAt = (Get-Date).ToString('o')
    BaseUrl = $BaseUrl
    Username = $Username
    Overall = $overall
    Counts = [pscustomobject]@{
        Customers = $customerCount
        Items = $itemCount
        Invoices = $invoiceCount
        CustomerCategories = $categoryCount
        Units = $unitCount
        IntegrityIssues = Get-ListCount $integrityIssues
        IntegrityErrors = $integrityErrorCount
        IntegrityWarnings = $integrityWarningCount
        BlockingIntegrityWarnings = $blockingIntegrityWarningCount
        CustomerRecentInvoices = $customerRecentInvoiceCount
        FirstInvoicePayments = $paymentCount
    }
    DetailChecks = [pscustomobject]@{
        FirstCustomerId = $firstCustomerId
        CustomerDetailOk = $customerDetailOk
        FirstInvoiceId = $firstInvoiceId
        InvoiceDetailOk = $invoiceDetailOk
        PaymentListOk = $paymentListOk
    }
    IntegrityIssues = $integrityIssues
    IntegrityDetails = @($integrityDetails.ToArray())
    BlockingIntegrityWarnings = $blockingIntegrityWarnings
    FailOnIntegrityWarnings = [bool]$FailOnIntegrityWarnings
    AllowedIntegrityWarningCodes = @($AllowedIntegrityWarningCodes)
    EndpointObservations = @($script:EndpointObservations.ToArray())
    Failures = @($failures.ToArray())
}

$result | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('# 거래플랜 API 표시성 스모크') | Out-Null
$lines.Add('') | Out-Null
$lines.Add("- 실행시각: $($result.GeneratedAt)") | Out-Null
$lines.Add("- 결과: **$overall**") | Out-Null
$lines.Add("- BaseUrl: $BaseUrl") | Out-Null
$lines.Add('') | Out-Null
$lines.Add('| 항목 | 건수/결과 | 기준 |') | Out-Null
$lines.Add('|---|---:|---:|') | Out-Null
$lines.Add("| 거래처 | $customerCount | $MinCustomers |") | Out-Null
$lines.Add("| 품목 | $itemCount | $MinItems |") | Out-Null
$lines.Add("| 전표 | $invoiceCount | $MinInvoices |") | Out-Null
$lines.Add("| 고객분류 | $categoryCount | - |") | Out-Null
$lines.Add("| 단위 | $unitCount | - |") | Out-Null
$lines.Add("| 무결성 Error | $integrityErrorCount | 0 |") | Out-Null
$warningCriterion = if ($FailOnIntegrityWarnings) { '0' } else { '-' }
$lines.Add("| 무결성 Warning | $integrityWarningCount | $warningCriterion |") | Out-Null
$lines.Add("| 차단 대상 Warning | $blockingIntegrityWarningCount | 0 |") | Out-Null
$lines.Add('') | Out-Null
$lines.Add("- 무결성 Warning 실패 처리: $([bool]$FailOnIntegrityWarnings)") | Out-Null
if ($AllowedIntegrityWarningCodes.Count -gt 0) {
    $lines.Add("- 허용 Warning 코드: $($AllowedIntegrityWarningCodes -join ', ')") | Out-Null
}
if ($integrityDetails.Count -gt 0) {
    $lines.Add('') | Out-Null
    $lines.Add('## Integrity detail samples') | Out-Null
    foreach ($detail in $integrityDetails) {
        $lines.Add("### $($detail.Severity): $($detail.Code) count=$($detail.Count), detailCount=$($detail.DetailCount)") | Out-Null
        $sampleRows = @($detail.SampleRows)
        if ($sampleRows.Count -gt 0) {
            $lines.Add('| Entity | Id | Primary | Reference | Scope | Detail |') | Out-Null
            $lines.Add('|---|---|---|---|---|---|') | Out-Null
            foreach ($row in $sampleRows) {
                $entity = ([string]$row.entityType).Replace('|', '/')
                $id = ([string]$row.entityIdText).Replace('|', '/')
                $primary = ([string]$row.primaryText).Replace('|', '/')
                $reference = ([string]$row.referenceText).Replace('|', '/')
                $scope = ([string]$row.scopeText).Replace('|', '/')
                $detailText = ([string]$row.detailText).Replace('|', '/')
                $lines.Add("| $entity | $id | $primary | $reference | $scope | $detailText |") | Out-Null
            }
        }
    }
}
$lines.Add('## 상세 API 확인') | Out-Null
$lines.Add("- 첫 거래처 상세: $customerDetailOk ($firstCustomerId)") | Out-Null
$lines.Add("- 첫 전표 상세: $invoiceDetailOk ($firstInvoiceId)") | Out-Null
$lines.Add("- 첫 전표 수금/지급 목록: $paymentListOk, 건수 $paymentCount") | Out-Null
$lines.Add('') | Out-Null
$lines.Add('## Endpoint 응답시간') | Out-Null
$lines.Add('| Endpoint | 시도 | 결과 | ms | 오류 |') | Out-Null
$lines.Add('|---|---:|---|---:|---|') | Out-Null
foreach ($observation in $script:EndpointObservations) {
    $errorText = ([string]$observation.Error).Replace('|', '/')
    $lines.Add("| $($observation.Method) $($observation.Path) | $($observation.Attempt) | $($observation.Result) | $($observation.ElapsedMs) | $errorText |") | Out-Null
}
$lines.Add('') | Out-Null
$lines.Add("JSON: $jsonPath") | Out-Null
if ($failures.Count -gt 0) {
    $lines.Add('') | Out-Null
    $lines.Add('## 실패 항목') | Out-Null
    foreach ($failure in $failures) {
        $lines.Add("- $failure") | Out-Null
    }
}
$lines | Set-Content -LiteralPath $mdPath -Encoding UTF8

Write-Host "api_visibility_smoke_report=$mdPath"
Write-Host "api_visibility_smoke_json=$jsonPath"
Write-Host "result=$overall"

if ($overall -ne 'PASS') {
    throw "API 표시성 스모크 실패: $mdPath"
}
