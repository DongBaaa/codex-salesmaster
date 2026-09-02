[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$RtSourcePath,

    [Parameter(Mandatory = $true)]
    [string]$CurrentUsenetPath,

    [Parameter(Mandatory = $true)]
    [string]$CurrentItworldPath,

    [Parameter(Mandatory = $true)]
    [string]$AggregatePath,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-NormalizedText {
    param([AllowNull()][object]$Value)

    if ($null -eq $Value) {
        return ''
    }

    return ([string]$Value).Normalize([Text.NormalizationForm]::FormKC).Trim()
}

function Get-ComparisonText {
    param([AllowNull()][object]$Value)

    $normalized = Get-NormalizedText $Value
    return [Text.RegularExpressions.Regex]::Replace($normalized.ToUpperInvariant(), '[^0-9A-Z가-힣]', '')
}

function Get-CurrentCustomerName {
    param([Parameter(Mandatory = $true)][object]$Row)

    foreach ($propertyName in @('CurrentCustomerName', 'CustomerName', 'BillToCustomerName')) {
        $value = Get-NormalizedText $Row.$propertyName
        if ($value.Length -gt 0) {
            return $value
        }
    }

    return ''
}

function Get-CustomerComparison {
    param(
        [AllowEmptyString()][string]$RtCustomerName,
        [AllowEmptyString()][string]$CurrentCustomerName
    )

    if ([string]::IsNullOrWhiteSpace($RtCustomerName)) {
        return 'RT고객명_공란'
    }
    if ([string]::IsNullOrWhiteSpace($CurrentCustomerName)) {
        return '거래플랜고객명_공란'
    }

    $rt = Get-ComparisonText $RtCustomerName
    $current = Get-ComparisonText $CurrentCustomerName
    if ($rt -eq $current) {
        return '동일'
    }
    if ($rt.Contains($current) -or $current.Contains($rt)) {
        return '표기차이_가능'
    }

    return '다름_검토'
}

function Get-StatusCategory {
    param([AllowNull()][object]$Value)

    $status = Get-NormalizedText $Value
    if ($status -in @('렌탈', '임대진행중', '렌탈중', '설치')) {
        return '렌탈'
    }
    return $status
}

function Export-CsvWithBom {
    param(
        [Parameter(Mandatory = $true)][object[]]$Rows,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $csv = $Rows | ConvertTo-Csv -NoTypeInformation
    [IO.File]::WriteAllLines($Path, $csv, [Text.UTF8Encoding]::new($true))
}

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

foreach ($path in @($RtSourcePath, $CurrentUsenetPath, $CurrentItworldPath, $AggregatePath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required input file was not found: $path"
    }
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$rtRows = @(Import-Csv -LiteralPath $RtSourcePath)
$currentUsenetRows = @(Import-Csv -LiteralPath $CurrentUsenetPath | Where-Object { $_.IsDeleted -ne 't' })
$currentItworldRows = @(Import-Csv -LiteralPath $CurrentItworldPath | Where-Object { $_.IsDeleted -ne 't' })

$rtBlankManagement = @($rtRows | Where-Object { [string]::IsNullOrWhiteSpace($_.ManagementNumber) })
$rtDuplicateGroups = @(
    $rtRows |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_.ManagementNumber) } |
        Group-Object { (Get-NormalizedText $_.ManagementNumber).ToUpperInvariant() } |
        Where-Object Count -gt 1
)
if ($rtBlankManagement.Count -gt 0 -or $rtDuplicateGroups.Count -gt 0) {
    throw "RT source key quality gate failed: blank=$($rtBlankManagement.Count), duplicate_groups=$($rtDuplicateGroups.Count)"
}

$usenetLookup = @{}
foreach ($group in @($currentUsenetRows | Group-Object { (Get-NormalizedText $_.ManagementNumber).ToUpperInvariant() })) {
    if (-not [string]::IsNullOrWhiteSpace($group.Name)) {
        $usenetLookup[$group.Name] = @($group.Group)
    }
}

$itworldLookup = @{}
foreach ($group in @($currentItworldRows | Group-Object { (Get-NormalizedText $_.ManagementNumber).ToUpperInvariant() })) {
    if (-not [string]::IsNullOrWhiteSpace($group.Name)) {
        $itworldLookup[$group.Name] = @($group.Group)
    }
}

$planRows = [Collections.Generic.List[object]]::new()
foreach ($rtRow in $rtRows) {
    $managementNumber = Get-NormalizedText $rtRow.ManagementNumber
    $key = $managementNumber.ToUpperInvariant()
    $managementCompany = Get-NormalizedText $rtRow.ManagementCompany
    $canonicalDatabase = switch ($managementCompany) {
        '유즈넷' { 'georaeplan_usenet' }
        '아이티월드' { 'georaeplan_itworld' }
        default { '미정' }
    }

    $usenetMatches = @(if ($usenetLookup.ContainsKey($key)) { $usenetLookup[$key] })
    $itworldMatches = @(if ($itworldLookup.ContainsKey($key)) { $itworldLookup[$key] })
    $presentUsenet = $usenetMatches.Count -gt 0
    $presentItworld = $itworldMatches.Count -gt 0

    $selected = $null
    $selectedDatabase = ''
    if ($canonicalDatabase -eq 'georaeplan_usenet') {
        if ($presentUsenet) {
            $selected = $usenetMatches[0]
            $selectedDatabase = 'georaeplan(현재 USENET 업무DB)'
        }
        elseif ($presentItworld) {
            $selected = $itworldMatches[0]
            $selectedDatabase = 'georaeplan_itworld'
        }
    }
    elseif ($canonicalDatabase -eq 'georaeplan_itworld') {
        if ($presentItworld) {
            $selected = $itworldMatches[0]
            $selectedDatabase = 'georaeplan_itworld'
        }
        elseif ($presentUsenet) {
            $selected = $usenetMatches[0]
            $selectedDatabase = 'georaeplan(현재 USENET 업무DB)'
        }
    }

    $placementAction = if ($canonicalDatabase -eq 'georaeplan_usenet') {
        if ($presentUsenet -and $presentItworld) { 'ITWORLD중복제거_USENET유지' }
        elseif ($presentUsenet) { 'USENET신규DB로복사' }
        elseif ($presentItworld) { 'ITWORLD에서_USENET으로이동' }
        else { 'RT에서_USENET에신규생성' }
    }
    elseif ($canonicalDatabase -eq 'georaeplan_itworld') {
        if ($presentUsenet -and $presentItworld) { 'USENET중복제거_ITWORLD유지' }
        elseif ($presentItworld) { 'ITWORLD유지' }
        elseif ($presentUsenet) { 'USENET에서_ITWORLD로이동' }
        else { 'RT에서_ITWORLD에신규생성' }
    }
    else {
        '관리업체_검토'
    }

    $currentCustomerName = if ($null -ne $selected) { Get-CurrentCustomerName $selected } else { '' }
    $currentAssetStatus = if ($null -ne $selected) { Get-NormalizedText $selected.AssetStatus } else { '' }
    $meterValues = @(
        Get-NormalizedText $rtRow.BlackIncludedText
        Get-NormalizedText $rtRow.ColorIncludedText
        Get-NormalizedText $rtRow.BlackOverageText
        Get-NormalizedText $rtRow.ColorOverageText
    )

    $planRows.Add([pscustomobject][ordered]@{
        관리번호 = $managementNumber
        RT관리업체 = $managementCompany
        목표DB = $canonicalDatabase
        RT상태 = Get-NormalizedText $rtRow.Status
        RT고객명 = Get-NormalizedText $rtRow.CustomerName
        RT설치위치 = Get-NormalizedText $rtRow.InstallLocation
        현재USENET존재 = $presentUsenet
        현재ITWORLD존재 = $presentItworld
        비교기준현재DB = $selectedDatabase
        거래플랜고객명 = $currentCustomerName
        고객명판정 = Get-CustomerComparison -RtCustomerName (Get-NormalizedText $rtRow.CustomerName) -CurrentCustomerName $currentCustomerName
        거래플랜상태 = $currentAssetStatus
        상태판정 = if ((Get-StatusCategory $rtRow.Status) -eq (Get-StatusCategory $currentAssetStatus)) { '동일' } else { '다름_검토' }
        흑백기본출력량 = $meterValues[0]
        컬러기본출력량 = $meterValues[1]
        흑백초과장당요금 = $meterValues[2]
        컬러초과장당요금 = $meterValues[3]
        요금필드입력수 = @($meterValues | Where-Object Length -gt 0).Count
        배치조치 = $placementAction
    })
}

$rtKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($row in $rtRows) {
    [void]$rtKeys.Add((Get-NormalizedText $row.ManagementNumber))
}

$tradePlanOnlyRows = [Collections.Generic.List[object]]::new()
foreach ($source in @(
    @{ Database = 'georaeplan(현재 USENET 업무DB)'; Rows = $currentUsenetRows },
    @{ Database = 'georaeplan_itworld'; Rows = $currentItworldRows }
)) {
    foreach ($row in $source.Rows) {
        $managementNumber = Get-NormalizedText $row.ManagementNumber
        if (-not $rtKeys.Contains($managementNumber)) {
            $tradePlanOnlyRows.Add([pscustomobject][ordered]@{
                현재DB = $source.Database
                관리번호 = $managementNumber
                거래처명 = Get-CurrentCustomerName $row
                자산상태 = Get-NormalizedText $row.AssetStatus
                청구판정 = Get-NormalizedText $row.BillingEligibilityStatus
                담당지점 = Get-NormalizedText $row.ResponsibleOfficeCode
                권장조치 = '자동삭제금지_업무검토'
            })
        }
    }
}

$aggregateLines = @(Get-Content -LiteralPath $AggregatePath | ForEach-Object { $_.Replace('\t', "`t") })
$aggregateRows = @($aggregateLines | ConvertFrom-Csv -Delimiter "`t")
function Get-AggregateValue {
    param(
        [Parameter(Mandatory = $true)][string]$Metric,
        [Parameter(Mandatory = $true)][string]$Scope
    )

    $match = @($aggregateRows | Where-Object { $_.metric -eq $Metric -and $_.scope -eq $Scope } | Select-Object -First 1)
    if ($match.Count -eq 0) { return $null }
    return [int64]$match[0].value
}

$presentBoth = @($planRows | Where-Object { $_.현재USENET존재 -and $_.현재ITWORLD존재 }).Count
$presentNeither = @($planRows | Where-Object { -not $_.현재USENET존재 -and -not $_.현재ITWORLD존재 }).Count
$rtUsenetCount = @($planRows | Where-Object RT관리업체 -eq '유즈넷').Count
$rtItworldCount = @($planRows | Where-Object RT관리업체 -eq '아이티월드').Count

$actionCounts = [ordered]@{}
foreach ($group in @($planRows | Group-Object 배치조치 | Sort-Object Name)) {
    $actionCounts[$group.Name] = $group.Count
}

$summary = [ordered]@{
    generated_at = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ssK')
    mode = 'read_only_dry_run'
    writes_performed = $false
    source_hashes = [ordered]@{
        rt_equipment_csv_sha256 = Get-FileSha256 $RtSourcePath
        current_usenet_csv_sha256 = Get-FileSha256 $CurrentUsenetPath
        current_itworld_csv_sha256 = Get-FileSha256 $CurrentItworldPath
        aggregate_tsv_sha256 = Get-FileSha256 $AggregatePath
    }
    before = [ordered]@{
        physical_business_databases = @('georaeplan', 'georaeplan_itworld')
        rt_rows = $rtRows.Count
        rt_unique_management_numbers = $rtKeys.Count
        rt_usenet = $rtUsenetCount
        rt_itworld = $rtItworldCount
        current_usenet_active_assets = $currentUsenetRows.Count
        current_itworld_active_assets = $currentItworldRows.Count
        rt_present_in_both_databases = $presentBoth
        rt_present_in_neither_database = $presentNeither
        tradeplan_only_rows = $tradePlanOnlyRows.Count
        billing_profile_unlinked_all = Get-AggregateValue -Metric 'active_asset_profile_unlinked' -Scope 'all'
        billing_profile_unlinked_usenet = Get-AggregateValue -Metric 'active_asset_profile_unlinked' -Scope 'USENET'
        billing_profile_unlinked_yeonsu = Get-AggregateValue -Metric 'active_asset_profile_unlinked' -Scope 'YEONSU'
    }
    meter_policy_source = [ordered]@{
        black_included_nonblank = @($rtRows | Where-Object { -not [string]::IsNullOrWhiteSpace($_.BlackIncludedText) }).Count
        color_included_nonblank = @($rtRows | Where-Object { -not [string]::IsNullOrWhiteSpace($_.ColorIncludedText) }).Count
        black_overage_nonblank = @($rtRows | Where-Object { -not [string]::IsNullOrWhiteSpace($_.BlackOverageText) }).Count
        color_overage_nonblank = @($rtRows | Where-Object { -not [string]::IsNullOrWhiteSpace($_.ColorOverageText) }).Count
        rows_with_any_meter_policy = @($planRows | Where-Object 요금필드입력수 -gt 0).Count
    }
    comparison = [ordered]@{
        customer_same = @($planRows | Where-Object 고객명판정 -eq '동일').Count
        customer_format_difference_possible = @($planRows | Where-Object 고객명판정 -eq '표기차이_가능').Count
        customer_different_review = @($planRows | Where-Object 고객명판정 -eq '다름_검토').Count
        rt_customer_blank = @($planRows | Where-Object 고객명판정 -eq 'RT고객명_공란').Count
        tradeplan_customer_blank = @($planRows | Where-Object 고객명판정 -eq '거래플랜고객명_공란').Count
        status_same = @($planRows | Where-Object 상태판정 -eq '동일').Count
        status_different_review = @($planRows | Where-Object 상태판정 -eq '다름_검토').Count
        placement_actions = $actionCounts
    }
    proposed_after_for_rt_covered_assets = [ordered]@{
        physical_databases = @('georaeplan', 'georaeplan_itworld', 'georaeplan_usenet')
        georaeplan_role = '인증·관리 중심 DB로 전환 검토'
        georaeplan_usenet_rt_assets = $rtUsenetCount
        georaeplan_itworld_rt_assets = $rtItworldCount
        rt_cross_database_overlap = 0
        rt_missing = 0
        tradeplan_only_rows_pending_review = $tradePlanOnlyRows.Count
    }
}

$planPath = Join-Path $OutputDirectory 'rt-canonical-placement-plan.csv'
$tradePlanOnlyPath = Join-Path $OutputDirectory 'tradeplan-only-assets.csv'
$summaryPath = Join-Path $OutputDirectory 'rt-migration-dryrun-summary.json'
$reportPath = Join-Path $OutputDirectory 'RT-임대데이터-DB분리및이관-사전검토.md'

Export-CsvWithBom -Rows @($planRows) -Path $planPath
Export-CsvWithBom -Rows @($tradePlanOnlyRows) -Path $tradePlanOnlyPath
[IO.File]::WriteAllText($summaryPath, ($summary | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($true))

$report = @"
# RT 임대 데이터 DB 분리 및 이관 사전 검토

- 생성 시각: $($summary.generated_at)
- 실행 모드: 읽기 전용 시뮬레이션
- 실제 DB 쓰기: 수행하지 않음

## 결론

RT의 관리번호 768건은 현재 거래플랜의 두 업무 DB 중 적어도 한 곳에 모두 존재합니다. 다만 RT 기준 관리업체와 DB 배치가 일치하지 않거나 두 DB에 동시에 존재하는 항목이 있어, 신규 누락 입력보다 **정답 DB 재배치와 중복 정리**가 핵심입니다.

## 작업 전

| 항목 | 건수 |
|---|---:|
| RT 전체 / 고유 관리번호 | $($rtRows.Count) / $($rtKeys.Count) |
| RT 유즈넷 / 아이티월드 | $rtUsenetCount / $rtItworldCount |
| 현재 georaeplan 활성 렌탈 자산 | $($currentUsenetRows.Count) |
| 현재 georaeplan_itworld 활성 렌탈 자산 | $($currentItworldRows.Count) |
| RT 장비가 양쪽 DB에 중복 존재 | $presentBoth |
| RT 장비가 어느 DB에도 없음 | $presentNeither |
| 거래플랜에만 있고 RT에는 없음 | $($tradePlanOnlyRows.Count) |
| 활성 자산 청구프로필 미연결(전체) | $($summary.before.billing_profile_unlinked_all) |

## 관리번호별 권장 조치

$(($actionCounts.GetEnumerator() | ForEach-Object { "- $($_.Key): $($_.Value)건" }) -join "`r`n")

상세 대상은 rt-canonical-placement-plan.csv에서 관리번호별로 확인할 수 있습니다. 거래플랜에만 있는 $($tradePlanOnlyRows.Count)건은 tradeplan-only-assets.csv에 분리했으며 자동 삭제하지 않습니다.

## 고객명·상태 비교

| 판정 | 건수 |
|---|---:|
| 고객명 동일 | $($summary.comparison.customer_same) |
| 고객명 표기 차이 가능 | $($summary.comparison.customer_format_difference_possible) |
| 고객명 다름 - 검토 | $($summary.comparison.customer_different_review) |
| RT 고객명 공란 | $($summary.comparison.rt_customer_blank) |
| 거래플랜 고객명 공란 | $($summary.comparison.tradeplan_customer_blank) |
| 상태 동일 | $($summary.comparison.status_same) |
| 상태 다름 - 검토 | $($summary.comparison.status_different_review) |

고객명은 관리번호를 연결 키로 사용하고 비교 결과는 설명·검토 정보로만 남겼습니다. 즉, 고객명 표기가 다르더라도 같은 관리번호 장비를 이관 대상에서 제외하지 않습니다.

## 출력량·초과요금 원본 현황

| RT 필드 | 값이 있는 장비 수 |
|---|---:|
| 흑백 기본 출력량 | $($summary.meter_policy_source.black_included_nonblank) |
| 컬러 기본 출력량 | $($summary.meter_policy_source.color_included_nonblank) |
| 흑백 초과 장당요금 | $($summary.meter_policy_source.black_overage_nonblank) |
| 컬러 초과 장당요금 | $($summary.meter_policy_source.color_overage_nonblank) |
| 네 필드 중 하나 이상 존재 | $($summary.meter_policy_source.rows_with_any_meter_policy) |

이 값들은 RT에서 정상 추출됐지만, 현재 운영 거래플랜의 이관 계획 생성기는 해당 네 필드를 아직 변경값으로 만들지 않습니다. 따라서 요금 기능·DB 스키마의 운영 배포가 끝난 뒤 별도 이관 및 계산 검증이 필요합니다.

## 제안 작업 후 상태

- georaeplan_usenet: RT 유즈넷 기준 219대를 정본으로 구성
- georaeplan_itworld: RT 아이티월드 기준 549대를 정본으로 구성
- RT 대상의 DB 간 중복: 92건 → 0건 목표
- RT 대상의 미배치: 0건 유지
- 거래플랜에만 있는 $($tradePlanOnlyRows.Count)건: 보존한 채 업무 검토 후 유지·보관 결정
- georaeplan: 인증·관리 중심 DB 전환은 코드·운영 연결 변경과 함께 별도 실행

## 안전 조건

1. 이 보고서는 SELECT와 로컬 CSV 비교만 수행했으며 운영 DB를 수정하지 않았습니다.
2. 고객명은 관리번호가 일치하는 행의 설명 비교용입니다. 고객명 차이만으로 자동 제외하지 않습니다.
3. 실제 반영 전에는 다중 DB 백업과 전 DB 복구훈련 성공 증거가 필요합니다.
4. 이관은 신규 DB 생성 → 복사 → 수량·해시 검증 → 앱 연결 전환 → 중복 제거 순서로 수행해야 합니다.
5. 청구프로필 미연결은 자산 이관과 별개로 청구 기능 검증이 필요한 품질 항목입니다.

## 원본 해시

- RT CSV: $($summary.source_hashes.rt_equipment_csv_sha256)
- 현재 USENET CSV: $($summary.source_hashes.current_usenet_csv_sha256)
- 현재 ITWORLD CSV: $($summary.source_hashes.current_itworld_csv_sha256)
- DB 집계 TSV: $($summary.source_hashes.aggregate_tsv_sha256)
"@
[IO.File]::WriteAllText($reportPath, $report, [Text.UTF8Encoding]::new($true))

[pscustomobject]@{
    PlanPath = $planPath
    PlanRows = $planRows.Count
    TradePlanOnlyPath = $tradePlanOnlyPath
    TradePlanOnlyRows = $tradePlanOnlyRows.Count
    SummaryPath = $summaryPath
    ReportPath = $reportPath
    PresentBoth = $presentBoth
    PresentNeither = $presentNeither
} | ConvertTo-Json -Depth 4
