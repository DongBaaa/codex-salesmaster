[CmdletBinding()]
param(
    [string]$ProjectRoot = "",
    [string]$BaseUrl = "",
    [string]$ItworldUsername = "",
    [string]$ItworldPassword = "",
    [string]$UsenetUsername = "",
    [string]$UsenetPassword = "",
    [string]$YeonsuUsername = "",
    [string]$YeonsuPassword = "",
    [string]$OutputPath = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $scriptRoot = if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) {
        $PSScriptRoot
    }
    else {
        Split-Path -Parent $MyInvocation.MyCommand.Path
    }

    $ProjectRoot = Split-Path -Parent $scriptRoot
}

function Resolve-AppBaseUrl {
    param(
        [string]$ProjectRoot,
        [string]$ExplicitBaseUrl
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitBaseUrl)) {
        return $ExplicitBaseUrl.TrimEnd("/")
    }

    $candidatePaths = @(
        (Join-Path $ProjectRoot "배포\관리자용\거래플랜-PC-설치패키지\App\appsettings.json"),
        (Join-Path $ProjectRoot "배포\설치패키지\관리자용\거래플랜-PC-설치패키지\App\appsettings.json"),
        (Join-Path $ProjectRoot "Desktop\거래플랜.Desktop.App\appsettings.json")
    )

    foreach ($candidatePath in $candidatePaths) {
        if (-not (Test-Path -LiteralPath $candidatePath)) {
            continue
        }

        try {
            $json = Get-Content -LiteralPath $candidatePath -Raw -Encoding UTF8 | ConvertFrom-Json
            $resolved = [string]$json.Api.BaseUrl
            if (-not [string]::IsNullOrWhiteSpace($resolved)) {
                return $resolved.TrimEnd("/")
            }
        }
        catch {
        }
    }

    throw "BaseUrl을 결정할 수 없습니다. -BaseUrl 값을 직접 지정하세요."
}

function Invoke-JsonRequest {
    param(
        [string]$Uri,
        [ValidateSet("Get", "Post")]
        [string]$Method,
        [hashtable]$Headers = @{},
        [object]$Body = $null
    )

    $invokeArgs = @{
        Uri = $Uri
        Method = $Method
        UseBasicParsing = $true
        TimeoutSec = 30
        Headers = $Headers
    }

    if ($null -ne $Body) {
        $invokeArgs.ContentType = "application/json; charset=utf-8"
        $invokeArgs.Body = ($Body | ConvertTo-Json -Depth 8 -Compress)
    }

    $response = Invoke-WebRequest @invokeArgs
    $content = [string]$response.Content
    if ([string]::IsNullOrWhiteSpace($content)) {
        return $null
    }

    # Windows PowerShell preserves root arrays; newer PowerShell enumerates them
    # unless NoEnumerate is explicit. Keep [] and [one row] intact on both.
    $convertArgs = @{ InputObject = $content }
    if ((Get-Command ConvertFrom-Json).Parameters.ContainsKey('NoEnumerate')) {
        $convertArgs.NoEnumerate = $true
    }
    return ,(ConvertFrom-Json @convertArgs)
}

function Get-ReturnedScopeCheck {
    param(
        [Parameter(Mandatory = $true)][object]$ScopeMatrix,
        [Parameter(Mandatory = $true)][ValidateSet('customers', 'items')][string]$AreaCode,
        [AllowNull()][object]$Rows
    )

    $tenant = [string]$ScopeMatrix.tenantCode
    if ([string]::IsNullOrWhiteSpace($tenant) -or
        [string]::IsNullOrWhiteSpace([string]$ScopeMatrix.officeCode) -or
        $ScopeMatrix.hasGlobalDataScope -isnot [bool]) {
        throw '권한 범위 응답의 업체/지점/전역 범위 형식이 불완전합니다.'
    }
    $area = @($ScopeMatrix.areas | Where-Object { $_.areaCode -ceq $AreaCode })
    if ($area.Count -ne 1) { throw "$AreaCode 권한 영역이 없거나 중복입니다." }
    $readable = @($area[0].readableOfficeCodes)
    if ($readable.Count -eq 0 -or @($readable | Where-Object {
        $_ -isnot [string] -or [string]::IsNullOrWhiteSpace($_)
    }).Count -gt 0) { throw "$AreaCode 조회 가능 지점 목록이 불완전합니다." }
    if ($null -eq $Rows) { throw "$AreaCode 조회 결과가 null입니다." }

    $warnings = @()
    $rowsArray = @($Rows)
    if ($rowsArray.Count -eq 0) { $warnings += "$AreaCode 데이터가 없어 실제 행 범위를 검증하지 못했습니다." }
    if ($ScopeMatrix.hasGlobalDataScope) {
        $warnings += "$AreaCode 전역 계정 조회이므로 제한 계정의 차단 검증을 대신하지 않습니다."
    }
    $violations = 0
    $unverifiedShared = 0
    foreach ($row in $rowsArray) {
        if ($null -eq $row -or [string]::IsNullOrWhiteSpace([string]$row.tenantCode)) {
            $violations++
            continue
        }
        if ($ScopeMatrix.hasGlobalDataScope) { continue }
        if (-not [string]::Equals([string]$row.tenantCode, $tenant, [StringComparison]::Ordinal)) {
            $violations++
            continue
        }
        $office = [string]$row.officeCode
        if ($AreaCode -ceq 'items') {
            if ($office -cne 'ALL' -and $readable -cnotcontains $office) { $violations++ }
            continue
        }
        $responsible = [string]$row.responsibleOfficeCode
        if ($readable -ccontains $responsible) { continue }
        if ($responsible -ceq 'ALL') {
            # The customer API permits shared ownership within the tenant even when
            # that owner's office is not readable. This DTO does not carry tenant
            # membership, so do not invent it or label that unresolved case PASS.
            if ($office -cne 'ALL' -and $office -cne '') { $unverifiedShared++ }
        }
        elseif ($responsible -ceq '' -and $readable -ccontains $office) {
            continue
        }
        else { $violations++ }
    }
    if ($violations -gt 0) { throw "$AreaCode 반환 데이터 중 범위 이탈 또는 소속 누락 $violations 건입니다." }
    if ($unverifiedShared -gt 0) {
        $warnings += "$AreaCode 공용 거래처 $unverifiedShared 건은 소유 지점의 업체 소속을 별도로 확인해야 합니다."
    }
    return [pscustomobject]@{ CheckedRowCount = $rowsArray.Count; Warnings = $warnings }
}

function Get-AccountResult {
    param(
        [string]$BaseUrl,
        [string]$Alias,
        [string]$Username,
        [string]$Password
    )

    if ([string]::IsNullOrWhiteSpace($Username) -or [string]::IsNullOrWhiteSpace($Password)) {
        return [pscustomobject]@{
            Alias = $Alias
            Username = $Username
            Success = $false
            Error = "계정 정보가 비어 있어 점검을 건너뜁니다."
        }
    }

    try {
        $login = Invoke-JsonRequest -Uri ($BaseUrl + "/auth/login") -Method Post -Body @{
            username = $Username
            password = $Password
        }

        $token = [string]$login.accessToken
        if ([string]::IsNullOrWhiteSpace($token)) {
            $token = [string]$login.AccessToken
        }

        if ([string]::IsNullOrWhiteSpace($token)) {
            throw "로그인 응답에 accessToken이 없습니다."
        }

        $headers = @{ Authorization = "Bearer $token" }
        $scopeMatrix = Invoke-JsonRequest -Uri ($BaseUrl + "/runtime/scope-matrix") -Method Get -Headers $headers
        if (-not [string]::Equals([string]$scopeMatrix.officeCode, $Alias, [StringComparison]::OrdinalIgnoreCase)) {
            throw '로그인 계정의 지점이 요청한 점검 지점과 다릅니다.'
        }
        $customers = Invoke-JsonRequest -Uri ($BaseUrl + "/customers") -Method Get -Headers $headers
        $items = Invoke-JsonRequest -Uri ($BaseUrl + "/items") -Method Get -Headers $headers

        $customerCheck = Get-ReturnedScopeCheck -ScopeMatrix $scopeMatrix -AreaCode customers -Rows $customers
        $itemCheck = Get-ReturnedScopeCheck -ScopeMatrix $scopeMatrix -AreaCode items -Rows $items

        $areas = @($scopeMatrix.areas)
        $missingCurrentOfficeAreas = @()
        foreach ($area in $areas) {
            $readable = @($area.readableOfficeCodes)
            if ($readable.Count -gt 0 -and $readable -notcontains [string]$scopeMatrix.officeCode) {
                $missingCurrentOfficeAreas += [string]$area.areaDisplayName
            }
        }

        return [pscustomobject]@{
            Alias = $Alias
            Username = $Username
            Success = $true
            Error = ""
            TenantCode = [string]$scopeMatrix.tenantCode
            OfficeCode = [string]$scopeMatrix.officeCode
            ScopeType = [string]$scopeMatrix.scopeType
            AreaCount = $areas.Count
            CustomerCount = @($customers).Count
            ItemCount = @($items).Count
            MissingCurrentOfficeAreas = $missingCurrentOfficeAreas
            Areas = $areas
            ScopeWarnings = @($customerCheck.Warnings) + @($itemCheck.Warnings)
        }
    }
    catch {
        return [pscustomobject]@{
            Alias = $Alias
            Username = $Username
            Success = $false
            Error = $_.Exception.Message
        }
    }
}

$resolvedBaseUrl = Resolve-AppBaseUrl -ProjectRoot $ProjectRoot -ExplicitBaseUrl $BaseUrl

$accountSpecs = @(
    @{ Alias = "ITWORLD"; Username = $ItworldUsername; Password = $ItworldPassword },
    @{ Alias = "USENET"; Username = $UsenetUsername; Password = $UsenetPassword },
    @{ Alias = "YEONSU"; Username = $YeonsuUsername; Password = $YeonsuPassword }
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_.Username) -or -not [string]::IsNullOrWhiteSpace($_.Password) }

if ($accountSpecs.Count -eq 0) {
    throw "최소 1개 계정의 사용자명/비밀번호를 입력하세요."
}

$results = foreach ($account in $accountSpecs) {
    Get-AccountResult -BaseUrl $resolvedBaseUrl -Alias $account.Alias -Username $account.Username -Password $account.Password
}

$failed = @($results | Where-Object { -not $_.Success })
$warnings = @($results | Where-Object { $_.Success -and ($_.AreaCount -eq 0 -or $_.MissingCurrentOfficeAreas.Count -gt 0 -or $_.ScopeWarnings.Count -gt 0) })
$overallStatus = if ($failed.Count -gt 0) { "FAIL" } elseif ($warnings.Count -gt 0) { "WARN" } else { "PASS" }

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $reportDirectory = Join-Path $ProjectRoot "테스트 시행\기록"
    New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
    $OutputPath = Join-Path $reportDirectory ("account-scope-regression-{0}.md" -f (Get-Date -Format "yyyyMMdd-HHmmss"))
}
else {
    $reportDirectory = Split-Path -Parent $OutputPath
    if (-not [string]::IsNullOrWhiteSpace($reportDirectory)) {
        New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
    }
}

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("# 계정별 권한/범위 회귀 점검 리포트") | Out-Null
$lines.Add("") | Out-Null
$lines.Add("- 실행시각: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')") | Out-Null
$lines.Add("- 결과: **$overallStatus**") | Out-Null
$lines.Add("- BaseUrl: $resolvedBaseUrl") | Out-Null
$lines.Add("- 검증 범위: 거래처·품목 반환 행과 서버 조회 범위의 일치. 권한 설정 자체의 타당성, 자산·청구·저장·동기화·실제 화면은 별도 검증이 필요합니다.") | Out-Null
$lines.Add("") | Out-Null
$lines.Add("| 계정 | 결과 | 테넌트 | 지점 | 범위 | 거래처 수 | 품목 수 | 비고 |") | Out-Null
$lines.Add("| --- | --- | --- | --- | --- | ---: | ---: | --- |") | Out-Null

foreach ($result in $results) {
    if (-not $result.Success) {
        $lines.Add("| $($result.Alias) | FAIL | - | - | - | - | - | $($result.Error.Replace('|', '\|')) |") | Out-Null
        continue
    }

    $note = if ($result.MissingCurrentOfficeAreas.Count -gt 0) {
        "현재 지점 누락 영역: " + (($result.MissingCurrentOfficeAreas -join ", ").Replace('|', '\|'))
    }
    else {
        "OK"
    }

    $rowStatus = "OK"
    if ($result.ScopeWarnings.Count -gt 0) {
        $rowStatus = "WARN"
        $note += "; " + (($result.ScopeWarnings -join "; ").Replace('|', '\|'))
    }
    $lines.Add("| $($result.Alias) | $rowStatus | $($result.TenantCode) | $($result.OfficeCode) | $($result.ScopeType) | $($result.CustomerCount) | $($result.ItemCount) | $note |") | Out-Null
}

foreach ($result in $results | Where-Object { $_.Success }) {
    $lines.Add("") | Out-Null
    $lines.Add("## $($result.Alias)") | Out-Null
    $lines.Add("") | Out-Null
    $lines.Add("- 사용자: $($result.Username)") | Out-Null
    $lines.Add("- 테넌트/지점: $($result.TenantCode) / $($result.OfficeCode)") | Out-Null
    $lines.Add("- 범위유형: $($result.ScopeType)") | Out-Null
    $lines.Add("- 거래처 수: $($result.CustomerCount)") | Out-Null
    $lines.Add("- 품목 수: $($result.ItemCount)") | Out-Null
    $lines.Add("") | Out-Null
    $lines.Add("| 영역 | 조회 가능 지점 | 쓰기 가능 지점 | 비고 |") | Out-Null
    $lines.Add("| --- | --- | --- | --- |") | Out-Null

    foreach ($area in $result.Areas) {
        $readable = @($area.readableOfficeCodes)
        $writable = @($area.writableOfficeCodes)
        $readableText = if ($readable.Count -eq 0) { "-" } else { ($readable -join ", ") }
        $writableText = if ($writable.Count -eq 0) { "-" } else { ($writable -join ", ") }
        $noteText = ([string]$area.note).Replace("|", "\|")
        $lines.Add("| $([string]$area.areaDisplayName) | $($readableText.Replace('|', '\|')) | $($writableText.Replace('|', '\|')) | $noteText |") | Out-Null
    }
}

[System.IO.File]::WriteAllText(
    $OutputPath,
    ($lines -join [Environment]::NewLine),
    (New-Object System.Text.UTF8Encoding($true)))

Write-Host "계정별 권한/범위 회귀 점검 리포트 저장: $OutputPath"
Write-Host "결과: $overallStatus"

if ($failed.Count -gt 0) {
    throw "계정별 권한/범위 회귀 점검에서 실패가 확인되었습니다. 리포트: $OutputPath"
}
