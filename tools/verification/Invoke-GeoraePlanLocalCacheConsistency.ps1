param(
    [string]$BaseUrl = 'http://127.0.0.1:19080',
    [string]$Username = 'usenet',
    [string]$Password = '1234',
    [string]$BearerToken = '',
    [string]$ScopeTenantCode = '',
    [string]$ScopeOfficeCode = '',
    [string]$AppDataRoot = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path '테스트 시행\실행환경\AppData'),
    [string]$EvidenceDirectory = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path 'output\local-cache-consistency'),
    [switch]$FailOnCountMismatch
)

$ErrorActionPreference = 'Stop'

function New-DirectoryIfMissing {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
    }
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
        [ValidateSet('GET','POST')][string]$Method,
        [string]$Path,
        [hashtable]$Headers,
        [object]$Body,
        [int[]]$ExpectedStatus = @(200),
        [int]$TimeoutSec = 60
    )

    $uri = if ($Path.StartsWith('http', [System.StringComparison]::OrdinalIgnoreCase)) { $Path } else { "$BaseUrl/$($Path.TrimStart('/'))" }
    $params = @{
        Method = $Method
        Uri = $uri
        UseBasicParsing = $true
        TimeoutSec = $TimeoutSec
    }
    if ($Headers) { $params.Headers = $Headers }
    if ($PSBoundParameters.ContainsKey('Body') -and $null -ne $Body) {
        $params.Body = ($Body | ConvertTo-Json -Depth 20 -Compress)
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

function Get-CollectionCount {
    param([object]$Value)
    if ($null -eq $Value) { return 0 }
    return @($Value).Count
}

function Get-ActiveServerCount {
    param([object]$Value)
    if ($null -eq $Value) { return 0 }
    return @($Value | Where-Object { $_.isDeleted -ne $true }).Count
}

function Convert-ToArray {
    param([object]$Value)
    if ($null -eq $Value) { return @() }
    return @($Value)
}

function Get-LocalRowById {
    param(
        [object]$LocalInfo,
        [string]$Id
    )

    if ($null -ne $LocalInfo.activeRows) {
        foreach ($property in $LocalInfo.activeRows.PSObject.Properties) {
            if ([string]::Equals([string]$property.Name, $Id, [System.StringComparison]::OrdinalIgnoreCase)) {
                return $property.Value
            }
        }
    }

    foreach ($row in (Convert-ToArray $LocalInfo.sampleRows)) {
        if ([string]::Equals([string]$row.Id, $Id, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $row
        }
    }

    return $null
}

function Get-ObjectPropertyValue {
    param(
        [object]$Object,
        [string]$Name
    )

    if ($null -eq $Object) { return $null }
    foreach ($property in $Object.PSObject.Properties) {
        if ([string]::Equals([string]$property.Name, $Name, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $property.Value
        }
    }

    return $null
}

function Normalize-ComparableText {
    param(
        [object]$Value,
        [string]$FieldName = ''
    )
    if ($null -eq $Value) { return '' }
    $text = ([string]$Value).Trim()
    if ([string]::Equals($FieldName, 'VoucherType', [System.StringComparison]::OrdinalIgnoreCase)) {
        switch ($text) {
            '0' { return 'Sales' }
            '1' { return 'Purchase' }
            '2' { return 'Procurement' }
            '3' { return 'Expense' }
            '4' { return 'Collection' }
        }
    }

    return $text
}

function Normalize-ComparableDecimal {
    param([object]$Value)
    if ($null -eq $Value -or [string]::IsNullOrWhiteSpace([string]$Value)) { return [decimal]0 }
    return [decimal]$Value
}

function Add-FieldMismatchIfDifferent {
    param(
        [System.Collections.Generic.List[string]]$Failures,
        [string]$Key,
        [string]$EntityId,
        [string]$FieldName,
        [object]$ServerValue,
        [object]$LocalValue,
        [switch]$Decimal
    )

    if ($Decimal) {
        $serverDecimal = Normalize-ComparableDecimal $ServerValue
        $localDecimal = Normalize-ComparableDecimal $LocalValue
        if ($serverDecimal -ne $localDecimal) {
            $Failures.Add("$key 샘플 필드 불일치: $EntityId $FieldName 서버=$serverDecimal 로컬=$localDecimal")
            return $true
        }

        return $false
    }

    $serverText = Normalize-ComparableText $ServerValue $FieldName
    $localText = Normalize-ComparableText $LocalValue $FieldName
    if (-not [string]::Equals($serverText, $localText, [System.StringComparison]::Ordinal)) {
        $Failures.Add("$key 샘플 필드 불일치: $EntityId $FieldName 서버='$serverText' 로컬='$localText'")
        return $true
    }

    return $false
}

function Invoke-LocalSqliteSnapshot {
    param(
        [string]$DatabasePath,
        [hashtable]$TableMap
    )

    $python = Get-Command python -ErrorAction SilentlyContinue
    if ($null -eq $python) {
        throw "python 명령을 찾지 못했습니다. 로컬 SQLite 캐시 검증을 실행할 수 없습니다."
    }

    $env:GEORAEPLAN_LOCAL_DB = $DatabasePath
    $env:GEORAEPLAN_TABLES_JSON = ($TableMap | ConvertTo-Json -Depth 10 -Compress)

    $code = @'
import json
import os
import sqlite3

db_path = os.environ["GEORAEPLAN_LOCAL_DB"]
tables = json.loads(os.environ["GEORAEPLAN_TABLES_JSON"])
scope_tenant = (os.environ.get("GEORAEPLAN_SCOPE_TENANT") or "").strip()
scope_office = (os.environ.get("GEORAEPLAN_SCOPE_OFFICE") or "").strip()

con = sqlite3.connect(db_path)
con.row_factory = sqlite3.Row

existing = {row["name"] for row in con.execute("select name from sqlite_master where type='table'")}
result = {"tables": {}, "settings": {}}

def build_where(conditions):
    if not conditions:
        return ""
    return "where " + " and ".join(conditions)

for key, table in tables.items():
    info = {
        "table": table,
        "exists": table in existing,
        "totalCount": None,
        "activeAllCount": None,
        "activeCount": None,
        "outOfScopeActiveCount": 0,
        "activeIds": [],
        "activeRows": {},
        "sampleRows": [],
    }
    if table in existing:
        cols = {row["name"] for row in con.execute(f'pragma table_info("{table}")')}
        total = con.execute(f'select count(*) from "{table}"').fetchone()[0]
        active_all_conditions = []
        if "IsDeleted" in cols:
            active_all_conditions.append('"IsDeleted" = 0')

        active_conditions = list(active_all_conditions)
        active_params = []
        if scope_tenant and "TenantCode" in cols:
            active_conditions.append('"TenantCode" = ?')
            active_params.append(scope_tenant)
        if scope_office and "OfficeCode" in cols:
            active_conditions.append('("OfficeCode" = ? or "OfficeCode" = ? or "OfficeCode" = "" or "OfficeCode" is null)')
            active_params.append(scope_office)
            active_params.append("ALL")

        active_all_where = build_where(active_all_conditions)
        active_where = build_where(active_conditions)
        active_all = con.execute(f'select count(*) from "{table}" {active_all_where}').fetchone()[0]
        active = con.execute(f'select count(*) from "{table}" {active_where}', tuple(active_params)).fetchone()[0]
        info["totalCount"] = total
        info["activeAllCount"] = active_all
        info["activeCount"] = active
        info["outOfScopeActiveCount"] = max(0, active_all - active)
        if "Id" in cols:
            id_rows = con.execute(f'select "Id" from "{table}" {active_where}', tuple(active_params)).fetchall()
            info["activeIds"] = [str(row["Id"]) for row in id_rows if row["Id"] is not None]
            sample_cols = [
                col for col in (
                    "Id",
                    "TenantCode",
                    "OfficeCode",
                    "NameOriginal",
                    "SpecificationOriginal",
                    "CategoryName",
                    "ItemKind",
                    "TradeType",
                    "ResponsibleOfficeCode",
                    "TrackingType",
                    "IsRental",
                    "VoucherType",
                    "CustomerName",
                    "InvoiceNumber",
                    "InvoiceDate",
                    "TotalAmount",
                    "CurrentStock",
                    "ItemId",
                    "WarehouseCode",
                    "Quantity",
                    "Revision",
                    "UpdatedAtUtc",
                )
                if col in cols
            ]
            if sample_cols:
                projection = ", ".join(f'"{col}"' for col in sample_cols)
                active_detail_rows = con.execute(f'select {projection} from "{table}" {active_where}', tuple(active_params)).fetchall()
                info["activeRows"] = {
                    str(row["Id"]): {col: row[col] for col in sample_cols}
                    for row in active_detail_rows
                    if row["Id"] is not None
                }
                order_col = "UpdatedAtUtc" if "UpdatedAtUtc" in cols else "Id"
                sample_rows = con.execute(
                    f'select {projection} from "{table}" {active_where} order by "{order_col}" desc limit 50',
                    tuple(active_params)
                ).fetchall()
                info["sampleRows"] = [
                    {col: row[col] for col in sample_cols}
                    for row in sample_rows
                ]
    result["tables"][key] = info

result["inventoryResidues"] = {
    "checkedNonInventoryItemCount": 0,
    "currentStockResidueCount": 0,
    "warehouseStockResidueCount": 0,
    "warehouseStockQuantityResidueCount": 0,
    "sampleRows": [],
}

def normalize_tracking(value, item_kind="", category_name="", is_rental=False):
    STOCK = "\uc7ac\uace0"
    ASSET = "\uc790\uc0b0"
    NONSTOCK = "\ube44\uc7ac\uace0"
    KIND_ASSET = "\uc7a5\ube44"
    KIND_BILLING = "\uccad\uad6c\ud56d\ubaa9"
    RENTAL_FEE = "\ub80c\ud0c8\ub8cc"
    text = ("" if value is None else str(value)).strip()
    kind = ("" if item_kind is None else str(item_kind)).strip()
    category = ("" if category_name is None else str(category_name)).strip()
    if text == STOCK:
        normalized = STOCK
    elif text == ASSET or text.lower() in ("asset", "equipment"):
        normalized = ASSET
    elif text == NONSTOCK or text.lower() in ("nonstock", "non-stock", "non stock", "billing", "service"):
        normalized = NONSTOCK
    elif text.lower() in ("stock", "inventory"):
        normalized = STOCK
    else:
        normalized = text or STOCK
    if normalized == STOCK and (kind == KIND_ASSET or kind.lower() in ("asset", "equipment") or is_rental):
        return ASSET
    if normalized == STOCK and (kind == KIND_BILLING or kind.lower() in ("billing", "service") or category.lower() == RENTAL_FEE.lower()):
        return NONSTOCK
    return normalized

if "Items" in existing and "ItemWarehouseStocks" in existing:
    item_cols = {row["name"] for row in con.execute('pragma table_info("Items")')}
    stock_cols = {row["name"] for row in con.execute('pragma table_info("ItemWarehouseStocks")')}
    item_conditions = []
    item_params = []
    if "IsDeleted" in item_cols:
        item_conditions.append('"IsDeleted" = 0')
    if scope_tenant and "TenantCode" in item_cols:
        item_conditions.append('"TenantCode" = ?')
        item_params.append(scope_tenant)
    if scope_office and "OfficeCode" in item_cols:
        item_conditions.append('("OfficeCode" = ? or "OfficeCode" = ? or "OfficeCode" = "" or "OfficeCode" is null)')
        item_params.append(scope_office)
        item_params.append("ALL")
    item_where = build_where(item_conditions)
    item_projection = [
        col for col in (
            "Id",
            "NameOriginal",
            "CategoryName",
            "ItemKind",
            "TrackingType",
            "IsRental",
            "CurrentStock",
            "TenantCode",
            "OfficeCode",
        )
        if col in item_cols
    ]
    stock_projection = [
        col for col in ("ItemId", "WarehouseCode", "Quantity", "Revision", "UpdatedAtUtc")
        if col in stock_cols
    ]
    item_rows = con.execute(
        f'select {", ".join(f"""\"{col}\"""" for col in item_projection)} from "Items" {item_where}',
        tuple(item_params)
    ).fetchall()
    stock_rows_by_item = {}
    if "ItemId" in stock_cols:
        stock_rows = con.execute(
            f'select {", ".join(f"""\"{col}\"""" for col in stock_projection)} from "ItemWarehouseStocks"'
        ).fetchall()
        for stock_row in stock_rows:
            item_id = str(stock_row["ItemId"] or "").lower()
            if not item_id:
                continue
            stock_rows_by_item.setdefault(item_id, []).append(stock_row)
    for item_row in item_rows:
        item_id = str(item_row["Id"] or "")
        is_rental_value = item_row["IsRental"] if "IsRental" in item_projection else False
        is_rental = str(is_rental_value).strip().lower() in ("1", "true", "yes")
        tracking = normalize_tracking(
            item_row["TrackingType"] if "TrackingType" in item_projection else "",
            item_row["ItemKind"] if "ItemKind" in item_projection else "",
            item_row["CategoryName"] if "CategoryName" in item_projection else "",
            is_rental
        )
        if tracking == "\uc7ac\uace0":
            continue
        current_stock = 0
        try:
            current_stock = float(item_row["CurrentStock"] or 0) if "CurrentStock" in item_projection else 0
        except Exception:
            current_stock = 0
        item_stocks = stock_rows_by_item.get(item_id.lower(), [])
        quantity_residue = 0
        for stock_row in item_stocks:
            try:
                if float(stock_row["Quantity"] or 0) != 0:
                    quantity_residue += 1
            except Exception:
                pass
        result["inventoryResidues"]["checkedNonInventoryItemCount"] += 1
        if current_stock != 0:
            result["inventoryResidues"]["currentStockResidueCount"] += 1
        if item_stocks:
            result["inventoryResidues"]["warehouseStockResidueCount"] += len(item_stocks)
        if quantity_residue:
            result["inventoryResidues"]["warehouseStockQuantityResidueCount"] += quantity_residue
        if (current_stock != 0 or item_stocks) and len(result["inventoryResidues"]["sampleRows"]) < 20:
            result["inventoryResidues"]["sampleRows"].append({
                "Id": item_id,
                "NameOriginal": item_row["NameOriginal"] if "NameOriginal" in item_projection else "",
                "TrackingType": tracking,
                "CurrentStock": current_stock,
                "WarehouseStockRows": len(item_stocks),
                "WarehouseStockQuantityRows": quantity_residue,
            })

if "Settings" in existing:
    wanted = ("LastSyncRevision", "Sync.LastSuccessAt", "Sync.LastError", "Sync.PendingFullMirrorRefresh")
    for key in wanted:
        row = con.execute('select "Value" from "Settings" where "Key" = ?', (key,)).fetchone()
        result["settings"][key] = None if row is None else row["Value"]

print(json.dumps(result, ensure_ascii=True))
'@

    $json = $code | python -
    if ([string]::IsNullOrWhiteSpace($json)) {
        throw "로컬 SQLite 캐시 검증 결과가 비어 있습니다."
    }

    return $json | ConvertFrom-Json
}

New-DirectoryIfMissing $EvidenceDirectory
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'

$databasePath = Join-Path $AppDataRoot 'data\거래플랜.db'
if (-not (Test-Path -LiteralPath $databasePath)) {
    throw "로컬 캐시 DB를 찾지 못했습니다: $databasePath"
}

$headers = @{}
if (-not [string]::IsNullOrWhiteSpace($BearerToken)) {
    $headers = @{ Authorization = "Bearer $($BearerToken.Trim())" }
}
else {
    $login = Invoke-Api -Method POST -Path 'auth/login' -Body @{ username = $Username; password = $Password } -ExpectedStatus @(200) -TimeoutSec 15
    if ([string]::IsNullOrWhiteSpace($login.Body.token)) {
        throw '로그인 토큰을 받지 못했습니다.'
    }

    $headers = @{ Authorization = "Bearer $($login.Body.token)" }
    if ([string]::IsNullOrWhiteSpace($ScopeTenantCode) -and -not [string]::IsNullOrWhiteSpace([string]$login.Body.user.tenantCode)) {
        $ScopeTenantCode = [string]$login.Body.user.tenantCode
    }
    if ([string]::IsNullOrWhiteSpace($ScopeOfficeCode) -and -not [string]::IsNullOrWhiteSpace([string]$login.Body.user.officeCode)) {
        $ScopeOfficeCode = [string]$login.Body.user.officeCode
    }
}
$pull = (Invoke-Api -Method GET -Path 'sync/pull?sinceRev=0' -Headers $headers -ExpectedStatus @(200) -TimeoutSec 120).Body

if ([string]::IsNullOrWhiteSpace($ScopeTenantCode)) {
    $tenantCandidates = @()
    foreach ($key in @('customers', 'items', 'invoices', 'rentalBillingProfiles', 'rentalAssets')) {
        $tenantCandidates += @(Convert-ToArray $pull.$key | Where-Object { $_.isDeleted -ne $true } | ForEach-Object { [string]$_.tenantCode } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    }
    $ScopeTenantCode = @($tenantCandidates | Group-Object | Sort-Object Count -Descending | Select-Object -First 1 -ExpandProperty Name)[0]
}
if ([string]::IsNullOrWhiteSpace($ScopeOfficeCode)) {
    $officeCandidates = @()
    foreach ($key in @('customers', 'items', 'invoices', 'rentalBillingProfiles', 'rentalAssets')) {
        $officeCandidates += @(Convert-ToArray $pull.$key | Where-Object { $_.isDeleted -ne $true } | ForEach-Object { [string]$_.officeCode } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    }
    $ScopeOfficeCode = @($officeCandidates | Group-Object | Sort-Object Count -Descending | Select-Object -First 1 -ExpandProperty Name)[0]
}

$tableMap = [ordered]@{
    customers = 'Customers'
    items = 'Items'
    invoices = 'Invoices'
    payments = 'Payments'
    transactions = 'Transactions'
    rentalBillingProfiles = 'RentalBillingProfiles'
    rentalAssets = 'RentalAssets'
    rentalBillingLogs = 'RentalBillingLogs'
    itemWarehouseStocks = 'ItemWarehouseStocks'
}

$env:GEORAEPLAN_SCOPE_TENANT = $ScopeTenantCode
$env:GEORAEPLAN_SCOPE_OFFICE = $ScopeOfficeCode
$local = Invoke-LocalSqliteSnapshot -DatabasePath $databasePath -TableMap $tableMap

$criticalKeys = @('customers', 'items', 'invoices')
$warnings = New-Object System.Collections.Generic.List[string]
$failures = New-Object System.Collections.Generic.List[string]
$rows = New-Object System.Collections.Generic.List[object]

foreach ($key in $tableMap.Keys) {
    $serverCollection = $pull.$key
    $serverRows = Convert-ToArray $serverCollection
    $serverActiveRows = @($serverRows | Where-Object { $_.isDeleted -ne $true })
    $serverTotal = $serverRows.Count
    $serverActive = $serverActiveRows.Count
    $localInfo = $local.tables.$key
    $localActive = if ($null -eq $localInfo.activeCount) { $null } else { [int]$localInfo.activeCount }
    $localActiveAll = if ($null -eq $localInfo.activeAllCount) { $null } else { [int]$localInfo.activeAllCount }
    $localOutOfScopeActive = if ($null -eq $localInfo.outOfScopeActiveCount) { 0 } else { [int]$localInfo.outOfScopeActiveCount }
    $localTotal = if ($null -eq $localInfo.totalCount) { $null } else { [int]$localInfo.totalCount }

    $status = 'PASS'
    if ($null -eq $localInfo -or $localInfo.exists -ne $true) {
        $status = 'FAIL'
        $failures.Add("$key 로컬 테이블이 없습니다.")
    }
    elseif ($criticalKeys -contains $key -and $serverActive -gt 0 -and $localActive -eq 0) {
        $status = 'FAIL'
        $failures.Add("$key 서버에는 활성 데이터가 $serverActive건 있으나 로컬 캐시는 0건입니다.")
    }
    elseif ($serverActive -gt 0 -and $localActive -lt $serverActive) {
        $status = 'WARN'
        $warnings.Add("${key} 현재 scope 로컬 활성 ${localActive}건이 서버 활성 ${serverActive}건보다 적습니다. 동기화 직후 재확인이 필요합니다.")
        if ($FailOnCountMismatch) {
            $status = 'FAIL'
            $failures.Add("${key} count mismatch: scoped local active ${localActive} < server active ${serverActive}")
        }
    }
    elseif ($serverActive -ge 0 -and $localActive -gt $serverActive) {
        $status = 'WARN'
        $warnings.Add("${key} 현재 scope 로컬 활성 ${localActive}건이 서버 활성 ${serverActive}건보다 많습니다. 서버에 없는 로컬 잔여 캐시가 현재 사용자 화면에 보일 수 있으므로 전체 캐시 재구성이 필요합니다.")
        if ($FailOnCountMismatch -and $criticalKeys -contains $key) {
            $status = 'FAIL'
            $failures.Add("${key} count mismatch: scoped local active ${localActive} > server active ${serverActive}")
        }
    }

    $sampleChecked = 0
    $fieldChecked = 0
    $fieldMismatch = 0
    if ($null -ne $localInfo -and $localInfo.exists -eq $true -and $criticalKeys -contains $key -and $serverActive -gt 0) {
        $localIds = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
        foreach ($id in (Convert-ToArray $localInfo.activeIds)) {
            if (-not [string]::IsNullOrWhiteSpace([string]$id)) {
                [void]$localIds.Add([string]$id)
            }
        }

        $sampleRows = @($serverActiveRows | Select-Object -First 10)
        $sampleChecked = $sampleRows.Count
        foreach ($serverRow in $sampleRows) {
            $serverId = [string]$serverRow.id
            if ([string]::IsNullOrWhiteSpace($serverId)) {
                continue
            }

            if (-not $localIds.Contains($serverId)) {
                $status = 'FAIL'
                $failures.Add("$key 서버 샘플 ID가 로컬 캐시에 없습니다: $serverId")
                continue
            }

            $localRow = Get-LocalRowById -LocalInfo $localInfo -Id $serverId
            if ($key -in @('customers', 'items') -and -not [string]::IsNullOrWhiteSpace([string]$serverRow.nameOriginal)) {
                if ($null -ne $localRow -and [string]::IsNullOrWhiteSpace([string](Get-ObjectPropertyValue -Object $localRow -Name 'NameOriginal'))) {
                    $status = 'FAIL'
                    $failures.Add("$key 로컬 샘플 이름이 비어 있습니다: $serverId")
                }
            }
            elseif ($key -eq 'invoices' -and $null -ne $localRow) {
                if ([string]::IsNullOrWhiteSpace([string](Get-ObjectPropertyValue -Object $localRow -Name 'InvoiceDate')) -and [string]::IsNullOrWhiteSpace([string](Get-ObjectPropertyValue -Object $localRow -Name 'InvoiceNumber'))) {
                    $status = 'FAIL'
                    $failures.Add("invoices 로컬 샘플 전표 표시 필드가 비어 있습니다: $serverId")
                }
            }

            if ($null -ne $localRow) {
                $fieldSpecs = @()
                if ($key -eq 'customers') {
                    $fieldSpecs = @(
                        @{ Server = 'nameOriginal'; Local = 'NameOriginal'; Name = 'NameOriginal'; Decimal = $false },
                        @{ Server = 'tradeType'; Local = 'TradeType'; Name = 'TradeType'; Decimal = $false },
                        @{ Server = 'responsibleOfficeCode'; Local = 'ResponsibleOfficeCode'; Name = 'ResponsibleOfficeCode'; Decimal = $false }
                    )
                }
                elseif ($key -eq 'items') {
                    $fieldSpecs = @(
                        @{ Server = 'nameOriginal'; Local = 'NameOriginal'; Name = 'NameOriginal'; Decimal = $false },
                        @{ Server = 'specificationOriginal'; Local = 'SpecificationOriginal'; Name = 'SpecificationOriginal'; Decimal = $false },
                        @{ Server = 'trackingType'; Local = 'TrackingType'; Name = 'TrackingType'; Decimal = $false },
                        @{ Server = 'currentStock'; Local = 'CurrentStock'; Name = 'CurrentStock'; Decimal = $true }
                    )
                }
                elseif ($key -eq 'invoices') {
                    $fieldSpecs = @(
                        @{ Server = 'invoiceDate'; Local = 'InvoiceDate'; Name = 'InvoiceDate'; Decimal = $false },
                        @{ Server = 'voucherType'; Local = 'VoucherType'; Name = 'VoucherType'; Decimal = $false },
                        @{ Server = 'totalAmount'; Local = 'TotalAmount'; Name = 'TotalAmount'; Decimal = $true }
                    )
                }

                foreach ($field in $fieldSpecs) {
                    $fieldChecked++
                    $isMismatch = Add-FieldMismatchIfDifferent `
                        -Failures $failures `
                        -Key $key `
                        -EntityId $serverId `
                        -FieldName $field.Name `
                        -ServerValue (Get-ObjectPropertyValue -Object $serverRow -Name $field.Server) `
                        -LocalValue (Get-ObjectPropertyValue -Object $localRow -Name $field.Local) `
                        -Decimal:([bool]$field.Decimal)
                    if ($isMismatch) {
                        $status = 'FAIL'
                        $fieldMismatch++
                    }
                }
            }
        }
    }

    $rows.Add([pscustomobject]@{
        Key = $key
        Table = $tableMap[$key]
        ServerTotal = $serverTotal
        ServerActive = $serverActive
        LocalTotal = $localTotal
        LocalActive = $localActive
        LocalActiveAll = $localActiveAll
        LocalOutOfScopeActive = $localOutOfScopeActive
        SampleChecked = $sampleChecked
        FieldChecked = $fieldChecked
        FieldMismatch = $fieldMismatch
        Status = $status
    })
}

$inventoryResidues = $local.inventoryResidues
if ($null -ne $inventoryResidues) {
    $currentStockResidueCount = if ($null -eq $inventoryResidues.currentStockResidueCount) { 0 } else { [int]$inventoryResidues.currentStockResidueCount }
    $warehouseStockResidueCount = if ($null -eq $inventoryResidues.warehouseStockResidueCount) { 0 } else { [int]$inventoryResidues.warehouseStockResidueCount }
    $warehouseStockQuantityResidueCount = if ($null -eq $inventoryResidues.warehouseStockQuantityResidueCount) { 0 } else { [int]$inventoryResidues.warehouseStockQuantityResidueCount }

    if ($currentStockResidueCount -gt 0) {
        $failures.Add("비재고/자산/렌탈료 품목의 CurrentStock 잔여값이 ${currentStockResidueCount}건 확인되었습니다.")
    }
    if ($warehouseStockResidueCount -gt 0) {
        $failures.Add("비재고/자산/렌탈료 품목에 연결된 로컬 ItemWarehouseStocks 잔여 row가 ${warehouseStockResidueCount}건 확인되었습니다.")
    }
    if ($warehouseStockQuantityResidueCount -gt 0) {
        $failures.Add("비재고/자산/렌탈료 품목의 0이 아닌 로컬 창고재고 row가 ${warehouseStockQuantityResidueCount}건 확인되었습니다.")
    }
}

$overall = if ($failures.Count -gt 0) { 'FAIL' } elseif ($warnings.Count -gt 0) { 'WARN' } else { 'PASS' }
$rowsArray = @($rows.ToArray())
$warningsArray = @($warnings.ToArray())
$failuresArray = @($failures.ToArray())
$result = [pscustomobject]@{
    GeneratedAt = (Get-Date).ToString('o')
    BaseUrl = $BaseUrl
    AppDataRoot = $AppDataRoot
    DatabasePath = $databasePath
    ScopeTenantCode = $ScopeTenantCode
    ScopeOfficeCode = $ScopeOfficeCode
    Overall = $overall
    Rows = $rowsArray
    LocalSettings = $local.settings
    InventoryResidues = $inventoryResidues
    Warnings = $warningsArray
    Failures = $failuresArray
}

$jsonPath = Join-Path $EvidenceDirectory "local-cache-consistency-$timestamp.json"
$mdPath = Join-Path $EvidenceDirectory "local-cache-consistency-$timestamp.md"
$result | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

$md = New-Object System.Collections.Generic.List[string]
$md.Add("# 거래플랜 로컬 캐시 일치 검증")
$md.Add("")
$md.Add("- 실행시각: $($result.GeneratedAt)")
$md.Add("- 서버: $BaseUrl")
$md.Add("- 로컬 DB: $databasePath")
$md.Add("- 비교 scope: Tenant=$ScopeTenantCode / Office=$ScopeOfficeCode")
$md.Add("- 결과: **$overall**")
$md.Add("")
$md.Add("## 핵심 목록 비교")
$md.Add("")
$md.Add("| 데이터 | 서버 전체 | 서버 활성 | 로컬 전체 | 현재 scope 로컬 활성 | scope 외 로컬 활성 | 샘플 확인 | 필드 확인 | 필드 불일치 | 결과 |")
$md.Add("|---|---:|---:|---:|---:|---:|---:|---:|---:|---|")
foreach ($row in $rows) {
    $md.Add("| $($row.Key) | $($row.ServerTotal) | $($row.ServerActive) | $($row.LocalTotal) | $($row.LocalActive) | $($row.LocalOutOfScopeActive) | $($row.SampleChecked) | $($row.FieldChecked) | $($row.FieldMismatch) | $($row.Status) |")
}
$md.Add("")
$md.Add("## 로컬 동기화 설정")
$md.Add("")
foreach ($setting in $local.settings.PSObject.Properties) {
    $value = if ([string]::IsNullOrWhiteSpace([string]$setting.Value)) { '(비어 있음)' } else { [string]$setting.Value }
    $md.Add("- $($setting.Name): $value")
}
if ($null -ne $inventoryResidues) {
    $md.Add("")
    $md.Add("## 비재고/자산 품목 재고 잔여 row 점검")
    $md.Add("")
    $md.Add("- 점검한 비재고/자산/렌탈료 품목 수: $($inventoryResidues.checkedNonInventoryItemCount)")
    $md.Add("- CurrentStock 잔여값 건수: $($inventoryResidues.currentStockResidueCount)")
    $md.Add("- ItemWarehouseStocks 잔여 row 건수: $($inventoryResidues.warehouseStockResidueCount)")
    $md.Add("- 0이 아닌 창고재고 row 건수: $($inventoryResidues.warehouseStockQuantityResidueCount)")
    $sampleResidues = @(Convert-ToArray $inventoryResidues.sampleRows)
    if ($sampleResidues.Count -gt 0) {
        $md.Add("")
        $md.Add("| Id | 품목명 | 추적유형 | CurrentStock | 창고재고 row | 0이 아닌 row |")
        $md.Add("|---|---|---|---:|---:|---:|")
        foreach ($sample in $sampleResidues) {
            $md.Add("| $($sample.Id) | $($sample.NameOriginal) | $($sample.TrackingType) | $($sample.CurrentStock) | $($sample.WarehouseStockRows) | $($sample.WarehouseStockQuantityRows) |")
        }
    }
}
if ($warnings.Count -gt 0) {
    $md.Add("")
    $md.Add("## 경고")
    foreach ($warning in $warnings) { $md.Add("- $warning") }
}
if ($failures.Count -gt 0) {
    $md.Add("")
    $md.Add("## 실패")
    foreach ($failure in $failures) { $md.Add("- $failure") }
}
$md | Set-Content -LiteralPath $mdPath -Encoding UTF8

Write-Host "Local cache consistency: $overall"
Write-Host "Report: $mdPath"
$rows | Format-Table -AutoSize

if ($failures.Count -gt 0) {
    throw "로컬 캐시 일치 검증 실패: $($failures -join '; ')"
}
