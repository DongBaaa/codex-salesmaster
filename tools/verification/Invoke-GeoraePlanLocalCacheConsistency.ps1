param(
    [string]$BaseUrl = 'http://127.0.0.1:19080',
    [string]$Username = 'usenet',
    [string]$Password = '1234',
    [string]$BearerToken = '',
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

con = sqlite3.connect(db_path)
con.row_factory = sqlite3.Row

existing = {row["name"] for row in con.execute("select name from sqlite_master where type='table'")}
result = {"tables": {}, "settings": {}}

for key, table in tables.items():
    info = {
        "table": table,
        "exists": table in existing,
        "totalCount": None,
        "activeCount": None,
        "activeIds": [],
        "activeRows": {},
        "sampleRows": [],
    }
    if table in existing:
        cols = {row["name"] for row in con.execute(f'pragma table_info("{table}")')}
        total = con.execute(f'select count(*) from "{table}"').fetchone()[0]
        if "IsDeleted" in cols:
            active_where = 'where "IsDeleted" = 0'
            active = con.execute(f'select count(*) from "{table}" {active_where}').fetchone()[0]
        else:
            active_where = ''
            active = total
        info["totalCount"] = total
        info["activeCount"] = active
        if "Id" in cols:
            id_rows = con.execute(f'select "Id" from "{table}" {active_where}').fetchall()
            info["activeIds"] = [str(row["Id"]) for row in id_rows if row["Id"] is not None]
            sample_cols = [
                col for col in (
                    "Id",
                    "NameOriginal",
                    "SpecificationOriginal",
                    "TradeType",
                    "ResponsibleOfficeCode",
                    "TrackingType",
                    "VoucherType",
                    "CustomerName",
                    "InvoiceNumber",
                    "InvoiceDate",
                    "TotalAmount",
                    "CurrentStock",
                    "Revision",
                    "UpdatedAtUtc",
                )
                if col in cols
            ]
            if sample_cols:
                projection = ", ".join(f'"{col}"' for col in sample_cols)
                active_detail_rows = con.execute(f'select {projection} from "{table}" {active_where}').fetchall()
                info["activeRows"] = {
                    str(row["Id"]): {col: row[col] for col in sample_cols}
                    for row in active_detail_rows
                    if row["Id"] is not None
                }
                order_col = "UpdatedAtUtc" if "UpdatedAtUtc" in cols else "Id"
                sample_rows = con.execute(
                    f'select {projection} from "{table}" {active_where} order by "{order_col}" desc limit 50'
                ).fetchall()
                info["sampleRows"] = [
                    {col: row[col] for col in sample_cols}
                    for row in sample_rows
                ]
    result["tables"][key] = info

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
}
$pull = (Invoke-Api -Method GET -Path 'sync/pull?sinceRev=0' -Headers $headers -ExpectedStatus @(200) -TimeoutSec 120).Body

$tableMap = [ordered]@{
    customers = 'Customers'
    items = 'Items'
    invoices = 'Invoices'
    payments = 'Payments'
    transactions = 'Transactions'
    rentalBillingProfiles = 'RentalBillingProfiles'
    rentalAssets = 'RentalAssets'
    rentalBillingLogs = 'RentalBillingLogs'
}

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
        $warnings.Add("${key} 로컬 활성 ${localActive}건이 서버 활성 ${serverActive}건보다 적습니다. 동기화 직후 재확인이 필요합니다.")
        if ($FailOnCountMismatch) {
            $status = 'FAIL'
            $failures.Add("${key} count mismatch: local active ${localActive} < server active ${serverActive}")
        }
    }
    elseif ($serverActive -ge 0 -and $localActive -gt $serverActive) {
        $status = 'WARN'
        $warnings.Add("${key} 로컬 활성 ${localActive}건이 서버 활성 ${serverActive}건보다 많습니다. 서버에 없는 로컬 잔여 캐시가 화면에 보일 수 있으므로 전체 캐시 재구성이 필요합니다.")
        if ($FailOnCountMismatch -and $criticalKeys -contains $key) {
            $status = 'FAIL'
            $failures.Add("${key} count mismatch: local active ${localActive} > server active ${serverActive}")
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
        SampleChecked = $sampleChecked
        FieldChecked = $fieldChecked
        FieldMismatch = $fieldMismatch
        Status = $status
    })
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
    Overall = $overall
    Rows = $rowsArray
    LocalSettings = $local.settings
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
$md.Add("- 결과: **$overall**")
$md.Add("")
$md.Add("## 핵심 목록 비교")
$md.Add("")
$md.Add("| 데이터 | 서버 전체 | 서버 활성 | 로컬 전체 | 로컬 활성 | 샘플 확인 | 필드 확인 | 필드 불일치 | 결과 |")
$md.Add("|---|---:|---:|---:|---:|---:|---:|---:|---|")
foreach ($row in $rows) {
    $md.Add("| $($row.Key) | $($row.ServerTotal) | $($row.ServerActive) | $($row.LocalTotal) | $($row.LocalActive) | $($row.SampleChecked) | $($row.FieldChecked) | $($row.FieldMismatch) | $($row.Status) |")
}
$md.Add("")
$md.Add("## 로컬 동기화 설정")
$md.Add("")
foreach ($setting in $local.settings.PSObject.Properties) {
    $value = if ([string]::IsNullOrWhiteSpace([string]$setting.Value)) { '(비어 있음)' } else { [string]$setting.Value }
    $md.Add("- $($setting.Name): $value")
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
