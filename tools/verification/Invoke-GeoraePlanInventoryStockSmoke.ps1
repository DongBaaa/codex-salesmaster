param(
    [string]$BaseUrl = 'http://127.0.0.1:19080',
    [string]$Username = 'usenet',
    [string]$Password = '1234',
    [string]$EvidenceDirectory = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path 'output\inventory-stock-smoke'),
    [string]$TenantCode = 'USENET_GROUP',
    [string]$OfficeCode = 'USENET',
    [string]$WarehouseCode = 'USENET_MAIN'
)

$ErrorActionPreference = 'Stop'

function New-DirectoryIfMissing {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -ItemType Directory -Force -Path $Path | Out-Null
    }
}

function Invoke-ApiJson {
    param(
        [string]$Method,
        [string]$Path,
        [hashtable]$Headers = @{},
        [object]$Body = $null
    )

    $uri = $BaseUrl.TrimEnd('/') + $Path
    $parameters = @{
        Method = $Method
        Uri = $uri
        Headers = $Headers
        TimeoutSec = 30
    }

    if ($null -ne $Body) {
        $parameters.ContentType = 'application/json; charset=utf-8'
        $parameters.Body = ($Body | ConvertTo-Json -Depth 30 -Compress)
    }

    Invoke-RestMethod @parameters
}

function Invoke-ApiNoContent {
    param(
        [string]$Method,
        [string]$Path,
        [hashtable]$Headers = @{}
    )

    $uri = $BaseUrl.TrimEnd('/') + $Path
    Invoke-WebRequest -UseBasicParsing -Method $Method -Uri $uri -Headers $Headers -TimeoutSec 30 | Out-Null
}

function Assert-EqualDecimal {
    param(
        [string]$Name,
        [decimal]$Actual,
        [decimal]$Expected
    )

    if ($Actual -ne $Expected) {
        throw "$Name 값이 기대와 다릅니다. actual=$Actual, expected=$Expected"
    }
}

function Get-ItemSnapshot {
    param([string]$ItemId, [hashtable]$Headers)

    $detail = Invoke-ApiJson -Method 'Get' -Path "/items/$ItemId/detail" -Headers $Headers
    $warehouse = @($detail.branchStocks | Where-Object {
        [string]::Equals([string]$_.warehouseCode, $WarehouseCode, [StringComparison]::OrdinalIgnoreCase)
    } | Select-Object -First 1)

    [pscustomobject]@{
        CurrentStock = [decimal]$detail.item.currentStock
        WarehouseStock = if ($warehouse.Count -gt 0) { [decimal]$warehouse[0].quantity } else { [decimal]0 }
        Revision = [long]$detail.item.revision
    }
}

function New-InvoicePayload {
    param(
        [string]$VoucherType,
        [string]$CustomerId,
        [string]$CustomerName,
        [string]$ItemId,
        [string]$ItemName,
        [decimal]$Quantity,
        [decimal]$UnitPrice,
        [string]$Memo
    )

    $amount = $Quantity * $UnitPrice
    @{
        id = ([guid]::NewGuid()).ToString()
        customerId = $CustomerId
        customerName = $CustomerName
        tenantCode = $TenantCode
        officeCode = $OfficeCode
        responsibleOfficeCode = $OfficeCode
        invoiceNumber = ''
        localTempNumber = ''
        versionGroupId = ([guid]::NewGuid()).ToString()
        versionNumber = 1
        isLatestVersion = $true
        voucherType = $VoucherType
        sourceWarehouseCode = $WarehouseCode
        invoiceDate = (Get-Date -Format 'yyyy-MM-dd')
        totalAmount = $amount
        supplyAmount = $amount
        vatAmount = 0
        vatMode = 'None'
        taxInvoiceIssued = $false
        purchaseReceivingRequired = $false
        purchaseReceivingStatus = '해당없음'
        purchaseReceivingOfficeCode = $OfficeCode
        purchaseReceivingWarehouseCode = $WarehouseCode
        memo = $Memo
        lines = @(
            @{
                id = ([guid]::NewGuid()).ToString()
                itemId = $ItemId
                itemNameOriginal = $ItemName
                specificationOriginal = 'stock-smoke-spec'
                unit = 'EA'
                quantity = $Quantity
                unitPrice = $UnitPrice
                lineAmount = $amount
                remark = 'inventory stock smoke'
                itemTrackingType = '재고'
                isDeleted = $false
            }
        )
        payments = @()
    }
}

New-DirectoryIfMissing $EvidenceDirectory
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$jsonPath = Join-Path $EvidenceDirectory "inventory-stock-smoke-$timestamp.json"
$mdPath = Join-Path $EvidenceDirectory "inventory-stock-smoke-$timestamp.md"

$login = Invoke-ApiJson -Method 'Post' -Path '/auth/login' -Body @{ username = $Username; password = $Password }
if ([string]::IsNullOrWhiteSpace([string]$login.token)) {
    throw '로그인 토큰을 받지 못했습니다.'
}

$headers = @{ Authorization = 'Bearer ' + $login.token }
$suffix = (Get-Date -Format 'yyyyMMddHHmmss') + '-' + ([guid]::NewGuid().ToString('N').Substring(0, 8))
$customerName = "검증용 재고스모크 거래처 $suffix"
$itemName = "검증용 재고스모크 품목 $suffix"

$createdCustomer = $null
$createdItem = $null
$zeroStockSalesInvoice = $null
$purchaseInvoice = $null
$salesInvoice = $null
$snapshots = New-Object System.Collections.Generic.List[object]
$cleanup = New-Object System.Collections.Generic.List[string]

try {
    $createdCustomer = Invoke-ApiJson -Method 'Post' -Path '/customers' -Headers $headers -Body @{
        id = ([guid]::NewGuid()).ToString()
        tenantCode = $TenantCode
        officeCode = $OfficeCode
        responsibleOfficeCode = $OfficeCode
        nameOriginal = $customerName
        tradeType = '매출/매입'
        department = '검증'
        contactPerson = '재고스모크'
        phone = '000-0000-0000'
        priceGrade = '매출단가'
        notes = '자동 검증 후 삭제되는 임시 거래처'
    }

    $createdItem = Invoke-ApiJson -Method 'Post' -Path '/items' -Headers $headers -Body @{
        id = ([guid]::NewGuid()).ToString()
        tenantCode = $TenantCode
        officeCode = $OfficeCode
        nameOriginal = $itemName
        specificationOriginal = 'stock-smoke-spec'
        categoryName = '검증'
        itemKind = '일반상품'
        trackingType = '재고'
        unit = 'EA'
        currentStock = 0
        safetyStock = 0
        purchasePrice = 1000
        salePrice = 1500
        retailPrice = 1500
        priceGradeA = 1400
        priceGradeB = 1300
        priceGradeC = 1200
        simpleMemo = '자동 검증 후 삭제되는 임시 품목'
        isRental = $false
        isSale = $true
    }

    $initial = Get-ItemSnapshot -ItemId $createdItem.id -Headers $headers
    $snapshots.Add([pscustomobject]@{ Step = 'initial'; CurrentStock = $initial.CurrentStock; WarehouseStock = $initial.WarehouseStock }) | Out-Null
    Assert-EqualDecimal -Name '초기 현재재고' -Actual $initial.CurrentStock -Expected 0

    $zeroStockSalesInvoice = Invoke-ApiJson -Method 'Post' -Path '/invoices' -Headers $headers -Body (New-InvoicePayload `
        -VoucherType 'Sales' `
        -CustomerId $createdCustomer.id `
        -CustomerName $createdCustomer.nameOriginal `
        -ItemId $createdItem.id `
        -ItemName $createdItem.nameOriginal `
        -Quantity 1 `
        -UnitPrice 1500 `
        -Memo '재고스모크 0재고 매출')

    $afterZeroStockSales = Get-ItemSnapshot -ItemId $createdItem.id -Headers $headers
    $snapshots.Add([pscustomobject]@{ Step = 'after-zero-stock-sales-1'; CurrentStock = $afterZeroStockSales.CurrentStock; WarehouseStock = $afterZeroStockSales.WarehouseStock }) | Out-Null
    Assert-EqualDecimal -Name '0재고 매출 후 현재재고' -Actual $afterZeroStockSales.CurrentStock -Expected -1
    Assert-EqualDecimal -Name '0재고 매출 후 창고재고' -Actual $afterZeroStockSales.WarehouseStock -Expected -1

    Invoke-ApiNoContent -Method 'Delete' -Path "/invoices/$($zeroStockSalesInvoice.id)?expectedRevision=$($zeroStockSalesInvoice.revision)" -Headers $headers
    $zeroStockSalesInvoice = $null
    $afterZeroStockSalesDelete = Get-ItemSnapshot -ItemId $createdItem.id -Headers $headers
    $snapshots.Add([pscustomobject]@{ Step = 'after-zero-stock-sales-delete'; CurrentStock = $afterZeroStockSalesDelete.CurrentStock; WarehouseStock = $afterZeroStockSalesDelete.WarehouseStock }) | Out-Null
    Assert-EqualDecimal -Name '0재고 매출 삭제 후 현재재고' -Actual $afterZeroStockSalesDelete.CurrentStock -Expected 0
    Assert-EqualDecimal -Name '0재고 매출 삭제 후 창고재고' -Actual $afterZeroStockSalesDelete.WarehouseStock -Expected 0

    $purchaseInvoice = Invoke-ApiJson -Method 'Post' -Path '/invoices' -Headers $headers -Body (New-InvoicePayload `
        -VoucherType 'Purchase' `
        -CustomerId $createdCustomer.id `
        -CustomerName $createdCustomer.nameOriginal `
        -ItemId $createdItem.id `
        -ItemName $createdItem.nameOriginal `
        -Quantity 7 `
        -UnitPrice 1000 `
        -Memo '재고스모크 매입')

    $afterPurchase = Get-ItemSnapshot -ItemId $createdItem.id -Headers $headers
    $snapshots.Add([pscustomobject]@{ Step = 'after-purchase-7'; CurrentStock = $afterPurchase.CurrentStock; WarehouseStock = $afterPurchase.WarehouseStock }) | Out-Null
    Assert-EqualDecimal -Name '매입 후 현재재고' -Actual $afterPurchase.CurrentStock -Expected 7
    Assert-EqualDecimal -Name '매입 후 창고재고' -Actual $afterPurchase.WarehouseStock -Expected 7

    $salesInvoice = Invoke-ApiJson -Method 'Post' -Path '/invoices' -Headers $headers -Body (New-InvoicePayload `
        -VoucherType 'Sales' `
        -CustomerId $createdCustomer.id `
        -CustomerName $createdCustomer.nameOriginal `
        -ItemId $createdItem.id `
        -ItemName $createdItem.nameOriginal `
        -Quantity 3 `
        -UnitPrice 1500 `
        -Memo '재고스모크 매출')

    $afterSales = Get-ItemSnapshot -ItemId $createdItem.id -Headers $headers
    $snapshots.Add([pscustomobject]@{ Step = 'after-sales-3'; CurrentStock = $afterSales.CurrentStock; WarehouseStock = $afterSales.WarehouseStock }) | Out-Null
    Assert-EqualDecimal -Name '매출 후 현재재고' -Actual $afterSales.CurrentStock -Expected 4
    Assert-EqualDecimal -Name '매출 후 창고재고' -Actual $afterSales.WarehouseStock -Expected 4

    Invoke-ApiNoContent -Method 'Delete' -Path "/invoices/$($salesInvoice.id)?expectedRevision=$($salesInvoice.revision)" -Headers $headers
    $salesInvoice = $null
    $afterSalesDelete = Get-ItemSnapshot -ItemId $createdItem.id -Headers $headers
    $snapshots.Add([pscustomobject]@{ Step = 'after-sales-delete'; CurrentStock = $afterSalesDelete.CurrentStock; WarehouseStock = $afterSalesDelete.WarehouseStock }) | Out-Null
    Assert-EqualDecimal -Name '매출 삭제 후 현재재고' -Actual $afterSalesDelete.CurrentStock -Expected 7
    Assert-EqualDecimal -Name '매출 삭제 후 창고재고' -Actual $afterSalesDelete.WarehouseStock -Expected 7

    Invoke-ApiNoContent -Method 'Delete' -Path "/invoices/$($purchaseInvoice.id)?expectedRevision=$($purchaseInvoice.revision)" -Headers $headers
    $purchaseInvoice = $null
    $afterPurchaseDelete = Get-ItemSnapshot -ItemId $createdItem.id -Headers $headers
    $snapshots.Add([pscustomobject]@{ Step = 'after-purchase-delete'; CurrentStock = $afterPurchaseDelete.CurrentStock; WarehouseStock = $afterPurchaseDelete.WarehouseStock }) | Out-Null
    Assert-EqualDecimal -Name '매입 삭제 후 현재재고' -Actual $afterPurchaseDelete.CurrentStock -Expected 0
    Assert-EqualDecimal -Name '매입 삭제 후 창고재고' -Actual $afterPurchaseDelete.WarehouseStock -Expected 0

    Invoke-ApiNoContent -Method 'Delete' -Path "/items/$($createdItem.id)?expectedRevision=$($afterPurchaseDelete.Revision)" -Headers $headers
    $createdItem = $null
    Invoke-ApiNoContent -Method 'Delete' -Path "/customers/$($createdCustomer.id)?expectedRevision=$($createdCustomer.revision)" -Headers $headers
    $createdCustomer = $null

    $overall = 'PASS'
}
finally {
    if ($null -ne $zeroStockSalesInvoice) {
        try {
            Invoke-ApiNoContent -Method 'Delete' -Path "/invoices/$($zeroStockSalesInvoice.id)?expectedRevision=$($zeroStockSalesInvoice.revision)" -Headers $headers
            $cleanup.Add("zero stock sales invoice deleted: $($zeroStockSalesInvoice.id)") | Out-Null
        } catch { $cleanup.Add("zero stock sales invoice cleanup failed: $($_.Exception.Message)") | Out-Null }
    }
    if ($null -ne $salesInvoice) {
        try {
            Invoke-ApiNoContent -Method 'Delete' -Path "/invoices/$($salesInvoice.id)?expectedRevision=$($salesInvoice.revision)" -Headers $headers
            $cleanup.Add("sales invoice deleted: $($salesInvoice.id)") | Out-Null
        } catch { $cleanup.Add("sales invoice cleanup failed: $($_.Exception.Message)") | Out-Null }
    }
    if ($null -ne $purchaseInvoice) {
        try {
            Invoke-ApiNoContent -Method 'Delete' -Path "/invoices/$($purchaseInvoice.id)?expectedRevision=$($purchaseInvoice.revision)" -Headers $headers
            $cleanup.Add("purchase invoice deleted: $($purchaseInvoice.id)") | Out-Null
        } catch { $cleanup.Add("purchase invoice cleanup failed: $($_.Exception.Message)") | Out-Null }
    }
    if ($null -ne $createdItem) {
        try {
            $snapshotForDelete = Get-ItemSnapshot -ItemId $createdItem.id -Headers $headers
            if ($snapshotForDelete.CurrentStock -ne 0 -or $snapshotForDelete.WarehouseStock -ne 0) {
                $cleanup.Add("item cleanup skipped because stock is not zero: $($createdItem.id), current=$($snapshotForDelete.CurrentStock), warehouse=$($snapshotForDelete.WarehouseStock)") | Out-Null
            }
            else {
                Invoke-ApiNoContent -Method 'Delete' -Path "/items/$($createdItem.id)?expectedRevision=$($snapshotForDelete.Revision)" -Headers $headers
                $cleanup.Add("item deleted: $($createdItem.id)") | Out-Null
            }
        } catch { $cleanup.Add("item cleanup failed: $($_.Exception.Message)") | Out-Null }
    }
    if ($null -ne $createdCustomer) {
        try {
            Invoke-ApiNoContent -Method 'Delete' -Path "/customers/$($createdCustomer.id)?expectedRevision=$($createdCustomer.revision)" -Headers $headers
            $cleanup.Add("customer deleted: $($createdCustomer.id)") | Out-Null
        } catch { $cleanup.Add("customer cleanup failed: $($_.Exception.Message)") | Out-Null }
    }
}

$result = [pscustomobject]@{
    GeneratedAt = (Get-Date).ToString('o')
    BaseUrl = $BaseUrl
    Overall = $overall
    CustomerName = $customerName
    ItemName = $itemName
    Snapshots = @($snapshots.ToArray())
    Cleanup = @($cleanup.ToArray())
}
$result | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('# 거래플랜 매입/매출 재고 증감 스모크') | Out-Null
$lines.Add('') | Out-Null
$lines.Add("- 실행시각: $($result.GeneratedAt)") | Out-Null
$lines.Add("- 결과: **$overall**") | Out-Null
$lines.Add("- BaseUrl: $BaseUrl") | Out-Null
$lines.Add("- 창고: $WarehouseCode") | Out-Null
$lines.Add('') | Out-Null
$lines.Add('| 단계 | 현재재고 | 창고재고 |') | Out-Null
$lines.Add('|---|---:|---:|') | Out-Null
foreach ($snapshot in $snapshots) {
    $lines.Add("| $($snapshot.Step) | $($snapshot.CurrentStock) | $($snapshot.WarehouseStock) |") | Out-Null
}
$lines.Add('') | Out-Null
$lines.Add('## 확인 범위') | Out-Null
$lines.Add('- 재고 0 품목도 판매 전표 생성 가능') | Out-Null
$lines.Add('- 재고 0 판매 전표 생성 시 음수 재고 반영') | Out-Null
$lines.Add('- 재고 0 판매 전표 삭제 시 재고 복구') | Out-Null
$lines.Add('- 매입 전표 생성 시 재고 증가') | Out-Null
$lines.Add('- 판매 전표 생성 시 재고 감소') | Out-Null
$lines.Add('- 판매 전표 삭제 시 재고 복구') | Out-Null
$lines.Add('- 매입 전표 삭제 시 재고 복구') | Out-Null
$lines.Add('- 검증용 거래처/품목/전표 정리') | Out-Null
$lines.Add('') | Out-Null
$lines.Add("JSON: $jsonPath") | Out-Null
if ($cleanup.Count -gt 0) {
    $lines.Add('') | Out-Null
    $lines.Add('## 정리 로그') | Out-Null
    foreach ($cleanupLine in $cleanup) {
        $lines.Add("- $cleanupLine") | Out-Null
    }
}
$lines | Set-Content -LiteralPath $mdPath -Encoding UTF8

Write-Host "inventory_stock_smoke_report=$mdPath"
Write-Host "inventory_stock_smoke_json=$jsonPath"
Write-Host "result=$overall"
