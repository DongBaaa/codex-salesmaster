[CmdletBinding()]
param([string]$SourceScript = '')
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($SourceScript)) {
    $SourceScript = Join-Path $PSScriptRoot '..\..\테스트 시행\Invoke-AccountScopeRegressionCheck.ps1'
}
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    (Resolve-Path -LiteralPath $SourceScript).Path, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) { throw 'Scope probe syntax errors.' }
# Import only function definitions. Never execute the real script's HTTP/report workflow.
foreach ($name in @('Invoke-JsonRequest', 'Get-ReturnedScopeCheck', 'Get-AccountResult')) {
    $function = $ast.Find({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $true)
    if ($null -ne $function) { . ([scriptblock]::Create($function.Extent.Text)) }
}
$jsonRequestFunction = ${function:Invoke-JsonRequest}
function Invoke-JsonRequest {
    param($Uri, $Method, $Headers, $Body)
    switch -Regex ($Uri) {
        '/auth/login$' { return [pscustomobject]@{accessToken='synthetic-test-token'} }
        '/runtime/scope-matrix$' { return $script:matrix }
        '/customers$' { return ,$script:customers }
        '/items$' { return ,$script:items }
        default { throw 'Unexpected request. Network access is not implemented in this harness.' }
    }
}
$cases = @(
    @{Name='office-scope'; Success=$true; Warning=$false},
    @{Name='customer-other-office'; Success=$false; Warning=$false},
    @{Name='customer-other-tenant'; Success=$false; Warning=$false},
    @{Name='item-other-office'; Success=$false; Warning=$false},
    @{Name='item-other-tenant'; Success=$false; Warning=$false},
    @{Name='shared-item'; Success=$true; Warning=$false},
    @{Name='shared-customer'; Success=$true; Warning=$false},
    @{Name='legacy-responsible-office'; Success=$true; Warning=$false},
    @{Name='shared-customer-unverified-owner'; Success=$true; Warning=$true},
    @{Name='separate-area-policy'; Success=$true; Warning=$false},
    @{Name='missing-area'; Success=$false; Warning=$false},
    @{Name='duplicate-area'; Success=$false; Warning=$false},
    @{Name='missing-record-tenant'; Success=$false; Warning=$false},
    @{Name='missing-matrix-tenant'; Success=$false; Warning=$false},
    @{Name='empty-data'; Success=$true; Warning=$true},
    @{Name='null-response'; Success=$false; Warning=$false},
    @{Name='global-admin'; Success=$true; Warning=$true},
    @{Name='string-global-flag'; Success=$false; Warning=$false}
    @{Name='wrong-account-office'; Success=$false; Warning=$false}
)
$failures = @()
foreach ($case in $cases) {
    $script:matrix = [pscustomobject]@{
        tenantCode='USENET_GROUP'; officeCode='USENET'; scopeType='OfficeOnly'; hasGlobalDataScope=$false
        areas=@(
            [pscustomobject]@{areaCode='customers'; areaDisplayName='Customers'; readableOfficeCodes=@('USENET')},
            [pscustomobject]@{areaCode='items'; areaDisplayName='Items'; readableOfficeCodes=@('USENET')})
    }
    $script:customers = @([pscustomobject]@{tenantCode='USENET_GROUP'; responsibleOfficeCode='USENET'; officeCode='ALL'})
    $script:items = @([pscustomobject]@{tenantCode='USENET_GROUP'; officeCode='USENET'})
    switch ($case.Name) {
        'customer-other-office' {$customers[0].responsibleOfficeCode='ITWORLD'}
        'customer-other-tenant' {$customers[0].tenantCode='ITWORLD'}
        'item-other-office' {$items[0].officeCode='ITWORLD'}
        'item-other-tenant' {$items[0].tenantCode='ITWORLD'}
        'shared-item' {$items[0].officeCode='ALL'}
        'shared-customer' {$customers[0].responsibleOfficeCode='ALL'}
        'legacy-responsible-office' {$customers[0].responsibleOfficeCode=''; $customers[0].officeCode='USENET'}
        'shared-customer-unverified-owner' {$customers[0].responsibleOfficeCode='ALL'; $customers[0].officeCode='YEONSU'}
        'separate-area-policy' {$matrix.areas[1].readableOfficeCodes=@('USENET','YEONSU'); $items[0].officeCode='YEONSU'}
        'missing-area' {$matrix.areas=@($matrix.areas[0])}
        'duplicate-area' {$matrix.areas+=@($matrix.areas[1])}
        'missing-record-tenant' {$items[0].tenantCode=''}
        'missing-matrix-tenant' {$matrix.tenantCode=''}
        'empty-data' {$script:customers=@(); $script:items=@()}
        'null-response' {$script:items=$null}
        'global-admin' {$matrix.hasGlobalDataScope=$true; $items[0].tenantCode='ITWORLD'; $items[0].officeCode='ITWORLD'}
        'string-global-flag' {$matrix.hasGlobalDataScope='false'; $items[0].tenantCode='ITWORLD'}
        'wrong-account-office' {$matrix.officeCode='ITWORLD'}
    }
    $result = Get-AccountResult -BaseUrl 'http://scope-probe.invalid' -Alias 'USENET' -Username 'synthetic' -Password 'synthetic'
    $hasWarning = @($result.ScopeWarnings | Where-Object { $_ }).Count -gt 0
    $ok = $result.Success -eq $case.Success -and $hasWarning -eq $case.Warning
    Write-Output ($case.Name + ': ' + $(if ($ok) {'PASS'} else {'FAIL'}))
    if (-not $ok) { $failures += $case.Name }
}
Set-Item -Path Function:Invoke-JsonRequest -Value $jsonRequestFunction
function Invoke-WebRequest {
    param($Uri, $Method, $UseBasicParsing, $TimeoutSec, $Headers, $ContentType, $Body)
    return [pscustomobject]@{Content=$script:jsonContent}
}
$shapeCases = @(
    @{Name='json-empty-array'; Json='[]'; Count=0},
    @{Name='json-spaced-empty-array'; Json=" `r`n[ `r`n ] "; Count=0},
    @{Name='json-single-array'; Json='[{"id":1}]'; Count=1},
    @{Name='json-multiple-array'; Json='[{"id":1},{"id":2}]'; Count=2}
)
foreach ($case in $shapeCases) {
    $script:jsonContent=$case.Json
    $rows=Invoke-JsonRequest -Uri 'http://scope-probe.invalid/items' -Method Get
    $ok=$null -ne $rows -and $rows -is [array] -and $rows.Count -eq $case.Count
    Write-Output ($case.Name + ': ' + $(if ($ok) {'PASS'} else {'FAIL'}))
    if (-not $ok) {$failures+=$case.Name}
}
$contractCases = @(
    @{Name='json-null-is-not-empty-data'; Json='null'; Success=$false; Warning=$false},
    @{Name='json-null-row-is-invalid'; Json='[null]'; Success=$false; Warning=$false},
    @{Name='json-empty-array-remains-warning'; Json='[]'; Success=$true; Warning=$true},
    @{Name='json-single-row-remains-scoped'; Json='[{"tenantCode":"USENET_GROUP","officeCode":"USENET"}]'; Success=$true; Warning=$false},
    @{Name='json-other-tenant-still-rejected'; Json='[{"tenantCode":"ITWORLD","officeCode":"USENET"}]'; Success=$false; Warning=$false},
    @{Name='json-malformed-still-rejected'; Json='[invalid'; Success=$false; Warning=$false}
)
$script:matrix.officeCode='USENET'
foreach ($case in $contractCases) {
    $script:jsonContent=$case.Json
    $succeeded=$false; $hasWarning=$false
    try {
        $rows=Invoke-JsonRequest -Uri 'http://scope-probe.invalid/items' -Method Get
        $checked=Get-ReturnedScopeCheck -ScopeMatrix $matrix -AreaCode items -Rows $rows
        $succeeded=$true
        $hasWarning=@($checked.Warnings | Where-Object { $_ }).Count -gt 0
    } catch { }
    $ok=$succeeded -eq $case.Success -and $hasWarning -eq $case.Warning
    Write-Output ($case.Name + ': ' + $(if ($ok) {'PASS'} else {'FAIL'}))
    if (-not $ok) {$failures+=$case.Name}
}
$total=$cases.Count+$shapeCases.Count+$contractCases.Count
Write-Output ('cases=' + $total + '; passed=' + ($total-$failures.Count) + '; failed=' + $failures.Count)
if ($failures.Count -gt 0) { throw ('Scope probe regression: ' + ($failures -join ', ')) }
