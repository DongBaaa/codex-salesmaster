[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$CandidateCsvPath,

    [Parameter(Mandatory = $true)]
    [string]$ManualDetailCsvPath,

    [string]$OutputDirectory = '',
    [ValidateRange(1, 20)]
    [int]$MaxOptionColumns = 8
)

$ErrorActionPreference = 'Stop'

function Resolve-ProjectRoot {
    if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) {
        return (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
    }

    return (Get-Location).Path
}

function Get-CsvValue {
    param(
        [Parameter(Mandatory = $true)]$Row,
        [Parameter(Mandatory = $true)][string[]]$Names
    )

    foreach ($name in $Names) {
        $property = $Row.PSObject.Properties[$name]
        if ($null -ne $property) {
            return [string]$property.Value
        }
    }

    return ''
}

function Get-ReviewKey {
    param([Parameter(Mandatory = $true)]$Row)
    return '{0}|{1}|{2}' -f `
        (Get-CsvValue -Row $Row -Names @('Database')),
        (Get-CsvValue -Row $Row -Names @('ProfileId')),
        (Get-CsvValue -Row $Row -Names @('TemplateOrdinal'))
}

function Join-UniqueValues {
    param(
        [object[]]$Rows,
        [string]$PropertyName,
        [string]$Separator = ';'
    )

    $values = $Rows |
        ForEach-Object { Get-CsvValue -Row $_ -Names @($PropertyName) } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Sort-Object -Unique
    return ($values -join $Separator)
}

function Get-OptionLabel {
    param([object[]]$Rows)

    if ($Rows.Count -eq 0) {
        return ''
    }

    $first = $Rows[0]
    $name = Get-CsvValue -Row $first -Names @('CandidateItemName')
    $spec = Get-CsvValue -Row $first -Names @('CandidateSpecification')
    $material = Get-CsvValue -Row $first -Names @('CandidateMaterialNumber')
    $serial = Get-CsvValue -Row $first -Names @('CandidateSerialNumber')
    $parts = @($name, $spec, $material, $serial) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    return ($parts -join ' / ')
}

function Get-ManualReviewPriority {
    param(
        [int]$ActiveAssetCandidateItemCount,
        [int]$NameCandidateItemCount,
        [int]$AssetWithoutItemCount,
        [string]$Reason
    )

    if ($ActiveAssetCandidateItemCount -gt 0 -and $ActiveAssetCandidateItemCount -le 3) {
        return 'P1_asset_multi_small'
    }

    if ($ActiveAssetCandidateItemCount -gt 3) {
        return 'P2_asset_multi_large'
    }

    if ($NameCandidateItemCount -gt 0 -and $NameCandidateItemCount -le 5) {
        return 'P2_name_candidate_small'
    }

    if ($NameCandidateItemCount -gt 5) {
        return 'P3_name_candidate_large'
    }

    if ($AssetWithoutItemCount -gt 0) {
        return 'P3_asset_without_item'
    }

    if ($Reason -eq 'zero_item_id') {
        return 'P3_zero_item_id_manual'
    }

    return 'P4_manual_investigation'
}

function Get-RecommendedNextAction {
    param(
        [int]$ActiveAssetCandidateItemCount,
        [int]$NameCandidateItemCount,
        [int]$AssetWithoutItemCount
    )

    if ($ActiveAssetCandidateItemCount -gt 0) {
        return 'choose_one_active_asset_item'
    }

    if ($NameCandidateItemCount -gt 0) {
        return 'choose_one_name_identifier_candidate'
    }

    if ($AssetWithoutItemCount -gt 0) {
        return 'investigate_asset_without_item_before_item_mapping'
    }

    return 'manual_profile_and_item_investigation'
}

function Get-OptionSortKey {
    param([object[]]$Rows)

    $types = Join-UniqueValues -Rows $Rows -PropertyName 'DetailType'
    $statuses = Join-UniqueValues -Rows $Rows -PropertyName 'CandidateStatus'
    if ($types -like '*included_asset_item_candidate*' -and $statuses -like '*active_item*') {
        return 1
    }
    if ($types -like '*name_or_identifier_candidate*' -and $statuses -like '*active_item*') {
        return 2
    }
    return 3
}

$projectRoot = Resolve-ProjectRoot
$resolvedCandidateCsvPath = (Resolve-Path -LiteralPath $CandidateCsvPath).Path
$resolvedManualDetailCsvPath = (Resolve-Path -LiteralPath $ManualDetailCsvPath).Path

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutputDirectory = Join-Path $projectRoot "artifacts\rental-template-item-reference-manual-review-pack\$timestamp"
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$candidateRows = @(Import-Csv -LiteralPath $resolvedCandidateCsvPath)
$manualRows = @($candidateRows | Where-Object { [string]::IsNullOrWhiteSpace((Get-CsvValue -Row $_ -Names @('ProposedItemId'))) })
$detailRows = @(Import-Csv -LiteralPath $resolvedManualDetailCsvPath)

$detailsByKey = @{}
foreach ($detailRow in $detailRows) {
    $key = Get-ReviewKey -Row $detailRow
    if (-not $detailsByKey.ContainsKey($key)) {
        $detailsByKey[$key] = New-Object System.Collections.Generic.List[object]
    }

    $detailsByKey[$key].Add($detailRow) | Out-Null
}

$decisionRows = New-Object System.Collections.Generic.List[object]
$optionRows = New-Object System.Collections.Generic.List[object]

foreach ($manualRow in $manualRows) {
    $key = Get-ReviewKey -Row $manualRow
    if ($detailsByKey.ContainsKey($key)) {
        $details = @($detailsByKey[$key].ToArray())
    }
    else {
        $details = @()
    }
    $activeItemDetails = @($details | Where-Object {
        -not [string]::IsNullOrWhiteSpace((Get-CsvValue -Row $_ -Names @('CandidateItemId'))) -and
        (Get-CsvValue -Row $_ -Names @('CandidateStatus')) -eq 'active_item'
    })
    $assetWithoutItemDetails = @($details | Where-Object {
        (Get-CsvValue -Row $_ -Names @('DetailType')) -eq 'included_asset_item_candidate' -and
        (Get-CsvValue -Row $_ -Names @('CandidateStatus')) -eq 'asset_without_item'
    })

    $optionGroups = @(
        $activeItemDetails |
            Group-Object CandidateItemId |
            ForEach-Object {
                [pscustomobject]@{
                    CandidateItemId = $_.Name
                    Rows = @($_.Group)
                    SortKey = Get-OptionSortKey -Rows @($_.Group)
                }
            } |
            Sort-Object SortKey, CandidateItemId
    )

    $assetOptionCount = @($optionGroups | Where-Object { (Join-UniqueValues -Rows $_.Rows -PropertyName 'DetailType') -like '*included_asset_item_candidate*' }).Count
    $nameOptionCount = @($optionGroups | Where-Object { (Join-UniqueValues -Rows $_.Rows -PropertyName 'DetailType') -like '*name_or_identifier_candidate*' }).Count
    $priority = Get-ManualReviewPriority `
        -ActiveAssetCandidateItemCount $assetOptionCount `
        -NameCandidateItemCount $nameOptionCount `
        -AssetWithoutItemCount $assetWithoutItemDetails.Count `
        -Reason (Get-CsvValue -Row $manualRow -Names @('Reason'))
    $recommendedAction = Get-RecommendedNextAction `
        -ActiveAssetCandidateItemCount $assetOptionCount `
        -NameCandidateItemCount $nameOptionCount `
        -AssetWithoutItemCount $assetWithoutItemDetails.Count

    $decision = [ordered]@{
        Database = Get-CsvValue -Row $manualRow -Names @('Database')
        ProfileId = Get-CsvValue -Row $manualRow -Names @('ProfileId')
        TemplateOrdinal = Get-CsvValue -Row $manualRow -Names @('TemplateOrdinal')
        Reason = Get-CsvValue -Row $manualRow -Names @('Reason')
        NameMatchClass = Get-CsvValue -Row $manualRow -Names @('NameMatchClass')
        AssetMatchClass = Get-CsvValue -Row $manualRow -Names @('AssetMatchClass')
        NameCandidateCount = Get-CsvValue -Row $manualRow -Names @('NameCandidateCount')
        IncludedAssetCount = Get-CsvValue -Row $manualRow -Names @('IncludedAssetCount')
        ActiveAssetItemCount = Get-CsvValue -Row $manualRow -Names @('ActiveAssetItemCount')
        ManualReviewPriority = $priority
        RecommendedNextAction = $recommendedAction
        CandidateOptionCount = $optionGroups.Count
        AssetCandidateOptionCount = $assetOptionCount
        NameCandidateOptionCount = $nameOptionCount
        AssetWithoutItemCount = $assetWithoutItemDetails.Count
        CandidateStatusSummary = (($details | Group-Object CandidateStatus | Sort-Object Name | ForEach-Object { '{0}:{1}' -f $_.Name, $_.Count }) -join ';')
        CandidateItemIds = (($optionGroups | ForEach-Object { $_.CandidateItemId }) -join ';')
        RemainingOptionCount = [Math]::Max(0, $optionGroups.Count - $MaxOptionColumns)
        ReviewDecision = ''
        ApprovedItemId = ''
        Reviewer = ''
        ReviewNote = ''
    }

    for ($index = 0; $index -lt $MaxOptionColumns; $index++) {
        $optionNumber = $index + 1
        if ($index -lt $optionGroups.Count) {
            $option = $optionGroups[$index]
            $optionRowsForItem = @($option.Rows)
            $decision["Option${optionNumber}ItemId"] = $option.CandidateItemId
            $decision["Option${optionNumber}Source"] = Join-UniqueValues -Rows $optionRowsForItem -PropertyName 'DetailType'
            $decision["Option${optionNumber}Status"] = Join-UniqueValues -Rows $optionRowsForItem -PropertyName 'CandidateStatus'
            $decision["Option${optionNumber}MatchRules"] = Join-UniqueValues -Rows $optionRowsForItem -PropertyName 'MatchRules'
            $decision["Option${optionNumber}IncludedAssetIds"] = Join-UniqueValues -Rows $optionRowsForItem -PropertyName 'IncludedAssetId'
            $decision["Option${optionNumber}Label"] = Get-OptionLabel -Rows $optionRowsForItem
        }
        else {
            $decision["Option${optionNumber}ItemId"] = ''
            $decision["Option${optionNumber}Source"] = ''
            $decision["Option${optionNumber}Status"] = ''
            $decision["Option${optionNumber}MatchRules"] = ''
            $decision["Option${optionNumber}IncludedAssetIds"] = ''
            $decision["Option${optionNumber}Label"] = ''
        }
    }

    $decisionRows.Add([pscustomobject]$decision) | Out-Null

    $optionOrdinal = 1
    foreach ($option in $optionGroups) {
        $optionRowsForItem = @($option.Rows)
        $optionRows.Add([pscustomobject]@{
            Database = $decision.Database
            ProfileId = $decision.ProfileId
            TemplateOrdinal = $decision.TemplateOrdinal
            OptionOrdinal = $optionOrdinal
            CandidateItemId = $option.CandidateItemId
            Source = Join-UniqueValues -Rows $optionRowsForItem -PropertyName 'DetailType'
            Status = Join-UniqueValues -Rows $optionRowsForItem -PropertyName 'CandidateStatus'
            MatchRules = Join-UniqueValues -Rows $optionRowsForItem -PropertyName 'MatchRules'
            IncludedAssetIds = Join-UniqueValues -Rows $optionRowsForItem -PropertyName 'IncludedAssetId'
            DetailRowCount = $optionRowsForItem.Count
            Label = Get-OptionLabel -Rows $optionRowsForItem
            ManualReviewPriority = $priority
            RecommendedNextAction = $recommendedAction
        }) | Out-Null
        $optionOrdinal++
    }
}

$decisionRows = @($decisionRows | Sort-Object Database, ManualReviewPriority, RecommendedNextAction, ProfileId, TemplateOrdinal)
$optionRows = @($optionRows | Sort-Object Database, ProfileId, TemplateOrdinal, OptionOrdinal)

$decisionTemplatePath = Join-Path $OutputDirectory 'manual-review-decision-template.csv'
$optionDetailsPath = Join-Path $OutputDirectory 'manual-review-option-details.csv'
$summaryPath = Join-Path $OutputDirectory 'manual-review-decision-summary.csv'
$readmePath = Join-Path $OutputDirectory 'README.md'

$decisionRows | Export-Csv -LiteralPath $decisionTemplatePath -Encoding UTF8 -NoTypeInformation
$optionRows | Export-Csv -LiteralPath $optionDetailsPath -Encoding UTF8 -NoTypeInformation

$decisionRows |
    Group-Object Database, ManualReviewPriority, RecommendedNextAction |
    ForEach-Object {
        $first = $_.Group[0]
        [pscustomobject]@{
            Database = $first.Database
            ManualReviewPriority = $first.ManualReviewPriority
            RecommendedNextAction = $first.RecommendedNextAction
            Count = $_.Count
            TotalCandidateOptionCount = ($_.Group | Measure-Object CandidateOptionCount -Sum).Sum
            TotalAssetWithoutItemCount = ($_.Group | Measure-Object AssetWithoutItemCount -Sum).Sum
        }
    } |
    Sort-Object Database, ManualReviewPriority, RecommendedNextAction |
    Export-Csv -LiteralPath $summaryPath -Encoding UTF8 -NoTypeInformation

@(
    '# 렌탈 청구 템플릿 수동 검토 결정표',
    '',
    "- 생성 시각: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss KST')",
    "- 후보 CSV: $resolvedCandidateCsvPath",
    "- 상세 후보 CSV: $resolvedManualDetailCsvPath",
    "- 수동 검토 행 수: $($decisionRows.Count)",
    "- 후보 옵션 행 수: $($optionRows.Count)",
    "- 옵션 컬럼 수: $MaxOptionColumns",
    '',
    '## 파일',
    '- `manual-review-decision-template.csv`: 수동 검토 대상 1건당 1행의 승인 입력 템플릿입니다.',
    '- `manual-review-option-details.csv`: 결정표의 후보 옵션을 상세 행으로 펼친 파일입니다.',
    '- `manual-review-decision-summary.csv`: 우선순위/권장 조치별 수동 검토 요약입니다.',
    '',
    '## 사용법',
    '1. 후보 옵션과 민감정보 포함 여부를 확인합니다.',
    '2. 승인 가능한 행만 `ReviewDecision=Approve`, `ApprovedItemId=<확인된 품목 GUID>`로 입력합니다.',
    '3. 승인 매핑은 `New-GeoraePlanRentalTemplateItemReferenceRepairPlan.ps1 -ValidateAgainstLinuxPc`로 다시 검증합니다.',
    '4. 생성 SQL은 복제본/테스트 DB에서 먼저 검증합니다.'
) | Set-Content -LiteralPath $readmePath -Encoding UTF8

Write-Host "manual_review_pack_output=$OutputDirectory"
Write-Host "manual_review_decision_template=$decisionTemplatePath"
Write-Host "manual_review_option_details=$optionDetailsPath"
Write-Host "manual_review_decision_summary=$summaryPath"
Import-Csv -LiteralPath $summaryPath | Format-Table -AutoSize | Out-String | Write-Host
