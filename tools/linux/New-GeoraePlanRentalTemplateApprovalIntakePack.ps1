[CmdletBinding()]
param(
    [string]$ProposedReadyCsvPath = '',

    [string]$ManualDecisionTemplateCsvPath = '',

    [string]$ApprovalIntakeCsvPath = '',

    [string]$OutputDirectory = '',
    [ValidateRange(1, 20)]
    [int]$MaxOptionColumns = 8,
    [switch]$RequireAllApproved
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

function Test-GuidText {
    param([string]$Value)
    $ignored = [Guid]::Empty
    return [Guid]::TryParse($Value, [ref]$ignored)
}

function Test-ApprovalDecision {
    param([string]$Value)
    $normalized = ([string]$Value).Trim()
    $koreanApprove = ([string][char]0xC2B9) + ([string][char]0xC778)
    return $normalized -eq 'Approve' -or $normalized -eq 'Approved' -or $normalized -eq $koreanApprove
}

function New-BlankApprovalFields {
    return [ordered]@{
        ReviewDecision = ''
        ApprovedItemId = ''
        Reviewer = ''
        ReviewNote = ''
    }
}

function Add-OptionColumns {
    param(
        [Parameter(Mandatory = $true)][System.Collections.Specialized.OrderedDictionary]$Target,
        [Parameter(Mandatory = $true)]$SourceRow,
        [int]$MaxOptionColumns
    )

    for ($index = 1; $index -le $MaxOptionColumns; $index++) {
        foreach ($suffix in @('ItemId', 'Source', 'Status', 'MatchRules', 'IncludedAssetIds', 'Label')) {
            $columnName = "Option${index}$suffix"
            $Target[$columnName] = Get-CsvValue -Row $SourceRow -Names @($columnName)
        }
    }
}

function New-ProposedReadyIntakeRow {
    param(
        [Parameter(Mandatory = $true)]$SourceRow,
        [int]$MaxOptionColumns
    )

    $suggestedItemId = Get-CsvValue -Row $SourceRow -Names @('ProposedItemId', 'ApprovedItemId')
    $row = [ordered]@{
        ReviewBucket = 'proposed_ready_requires_business_approval'
        Database = Get-CsvValue -Row $SourceRow -Names @('Database')
        ProfileId = Get-CsvValue -Row $SourceRow -Names @('ProfileId')
        TemplateOrdinal = Get-CsvValue -Row $SourceRow -Names @('TemplateOrdinal')
        Reason = Get-CsvValue -Row $SourceRow -Names @('Reason')
        NameMatchClass = Get-CsvValue -Row $SourceRow -Names @('NameMatchClass')
        AssetMatchClass = Get-CsvValue -Row $SourceRow -Names @('AssetMatchClass')
        ManualReviewPriority = 'P0_proposed_ready'
        RecommendedNextAction = 'review_and_approve_suggested_item'
        SuggestedItemId = $suggestedItemId
        SuggestedSource = Get-CsvValue -Row $SourceRow -Names @('ProposedSource')
        SuggestedConfidence = Get-CsvValue -Row $SourceRow -Names @('ProposedConfidence')
        CandidateOptionCount = if ([string]::IsNullOrWhiteSpace($suggestedItemId)) { 0 } else { 1 }
        AssetCandidateOptionCount = if ((Get-CsvValue -Row $SourceRow -Names @('ProposedSource')) -eq 'included_asset_single_active_item') { 1 } else { 0 }
        NameCandidateOptionCount = if ((Get-CsvValue -Row $SourceRow -Names @('ProposedSource')) -like '*name*' -or (Get-CsvValue -Row $SourceRow -Names @('ProposedSource')) -like '*identifier*') { 1 } else { 0 }
        AssetWithoutItemCount = 0
        CandidateStatusSummary = if ([string]::IsNullOrWhiteSpace($suggestedItemId)) { '' } else { 'active_item:1' }
        CandidateItemIds = $suggestedItemId
        RemainingOptionCount = 0
        OriginalReviewDecision = Get-CsvValue -Row $SourceRow -Names @('ReviewDecision')
        OriginalApprovedItemId = Get-CsvValue -Row $SourceRow -Names @('ApprovedItemId')
        OriginalReviewer = Get-CsvValue -Row $SourceRow -Names @('Reviewer')
    }

    foreach ($entry in (New-BlankApprovalFields).GetEnumerator()) {
        $row[$entry.Key] = $entry.Value
    }

    for ($index = 1; $index -le $MaxOptionColumns; $index++) {
        if ($index -eq 1 -and -not [string]::IsNullOrWhiteSpace($suggestedItemId)) {
            $row["Option${index}ItemId"] = $suggestedItemId
            $row["Option${index}Source"] = Get-CsvValue -Row $SourceRow -Names @('ProposedSource')
            $row["Option${index}Status"] = 'active_item'
            $row["Option${index}MatchRules"] = Get-CsvValue -Row $SourceRow -Names @('NameMatchRules')
            $row["Option${index}IncludedAssetIds"] = ''
            $row["Option${index}Label"] = ''
        }
        else {
            $row["Option${index}ItemId"] = ''
            $row["Option${index}Source"] = ''
            $row["Option${index}Status"] = ''
            $row["Option${index}MatchRules"] = ''
            $row["Option${index}IncludedAssetIds"] = ''
            $row["Option${index}Label"] = ''
        }
    }

    return [pscustomobject]$row
}

function New-ManualDecisionIntakeRow {
    param(
        [Parameter(Mandatory = $true)]$SourceRow,
        [int]$MaxOptionColumns
    )

    $row = [ordered]@{
        ReviewBucket = 'manual_review_requires_business_approval'
        Database = Get-CsvValue -Row $SourceRow -Names @('Database')
        ProfileId = Get-CsvValue -Row $SourceRow -Names @('ProfileId')
        TemplateOrdinal = Get-CsvValue -Row $SourceRow -Names @('TemplateOrdinal')
        Reason = Get-CsvValue -Row $SourceRow -Names @('Reason')
        NameMatchClass = Get-CsvValue -Row $SourceRow -Names @('NameMatchClass')
        AssetMatchClass = Get-CsvValue -Row $SourceRow -Names @('AssetMatchClass')
        ManualReviewPriority = Get-CsvValue -Row $SourceRow -Names @('ManualReviewPriority')
        RecommendedNextAction = Get-CsvValue -Row $SourceRow -Names @('RecommendedNextAction')
        SuggestedItemId = ''
        SuggestedSource = ''
        SuggestedConfidence = ''
        CandidateOptionCount = Get-CsvValue -Row $SourceRow -Names @('CandidateOptionCount')
        AssetCandidateOptionCount = Get-CsvValue -Row $SourceRow -Names @('AssetCandidateOptionCount')
        NameCandidateOptionCount = Get-CsvValue -Row $SourceRow -Names @('NameCandidateOptionCount')
        AssetWithoutItemCount = Get-CsvValue -Row $SourceRow -Names @('AssetWithoutItemCount')
        CandidateStatusSummary = Get-CsvValue -Row $SourceRow -Names @('CandidateStatusSummary')
        CandidateItemIds = Get-CsvValue -Row $SourceRow -Names @('CandidateItemIds')
        RemainingOptionCount = Get-CsvValue -Row $SourceRow -Names @('RemainingOptionCount')
        OriginalReviewDecision = Get-CsvValue -Row $SourceRow -Names @('ReviewDecision')
        OriginalApprovedItemId = Get-CsvValue -Row $SourceRow -Names @('ApprovedItemId')
        OriginalReviewer = Get-CsvValue -Row $SourceRow -Names @('Reviewer')
    }

    foreach ($entry in (New-BlankApprovalFields).GetEnumerator()) {
        $row[$entry.Key] = $entry.Value
    }

    Add-OptionColumns -Target $row -SourceRow $SourceRow -MaxOptionColumns $MaxOptionColumns
    return [pscustomobject]$row
}

function New-ApprovalValidationRows {
    param([object[]]$Rows)

    foreach ($row in $Rows) {
        $decision = (Get-CsvValue -Row $row -Names @('ReviewDecision')).Trim()
        $approvedItemId = (Get-CsvValue -Row $row -Names @('ApprovedItemId')).Trim()
        $reviewer = (Get-CsvValue -Row $row -Names @('Reviewer')).Trim()
        $candidateIds = @(
            (Get-CsvValue -Row $row -Names @('CandidateItemIds')) -split ';' |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
        )
        $suggestedItemId = (Get-CsvValue -Row $row -Names @('SuggestedItemId')).Trim()
        $allowedIds = @($candidateIds + @($suggestedItemId) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique)
        $errors = New-Object System.Collections.Generic.List[string]

        if ($decision -or $approvedItemId) {
            if (-not (Test-ApprovalDecision $decision)) {
                $errors.Add('ReviewDecision must be Approve/Approved/Korean-approve when ApprovedItemId is present.') | Out-Null
            }
            if (-not (Test-GuidText $approvedItemId)) {
                $errors.Add('ApprovedItemId must be a valid GUID.') | Out-Null
            }
            if ([string]::IsNullOrWhiteSpace($reviewer)) {
                $errors.Add('Reviewer is required for approved rows.') | Out-Null
            }
            if ($reviewer -like '*DRY_RUN*' -or $reviewer -like '*SYSTEM_PROPOSED*') {
                $errors.Add('Dry-run/system reviewer markers cannot be used as business approval.') | Out-Null
            }
            if ($allowedIds.Count -gt 0 -and $approvedItemId -notin $allowedIds) {
                $errors.Add('ApprovedItemId is not in suggested/candidate option ids.') | Out-Null
            }
        }

        [pscustomobject]@{
            Database = Get-CsvValue -Row $row -Names @('Database')
            ProfileId = Get-CsvValue -Row $row -Names @('ProfileId')
            TemplateOrdinal = Get-CsvValue -Row $row -Names @('TemplateOrdinal')
            ReviewBucket = Get-CsvValue -Row $row -Names @('ReviewBucket')
            ReviewDecision = $decision
            ApprovedItemId = $approvedItemId
            Reviewer = $reviewer
            ValidationStatus = if ($errors.Count -eq 0) { if ($decision -and $approvedItemId) { 'approved_input_valid' } else { 'pending_approval' } } else { 'invalid_approval_input' }
            ValidationErrors = ($errors -join ' ')
        }
    }
}

$projectRoot = Resolve-ProjectRoot

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutputDirectory = Join-Path $projectRoot "artifacts\rental-template-item-reference-approval-intake\$timestamp"
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$resolvedProposedReadyCsvPath = ''
$resolvedManualDecisionTemplateCsvPath = ''
$resolvedApprovalIntakeCsvPath = ''
$inputMode = ''

if (-not [string]::IsNullOrWhiteSpace($ApprovalIntakeCsvPath)) {
    $resolvedApprovalIntakeCsvPath = (Resolve-Path -LiteralPath $ApprovalIntakeCsvPath).Path
    $intakeRows = @(Import-Csv -LiteralPath $resolvedApprovalIntakeCsvPath)
    $inputMode = 'validate_existing_approval_intake'
}
else {
    if ([string]::IsNullOrWhiteSpace($ProposedReadyCsvPath) -or [string]::IsNullOrWhiteSpace($ManualDecisionTemplateCsvPath)) {
        throw 'Either -ApprovalIntakeCsvPath or both -ProposedReadyCsvPath and -ManualDecisionTemplateCsvPath are required.'
    }

    $resolvedProposedReadyCsvPath = (Resolve-Path -LiteralPath $ProposedReadyCsvPath).Path
    $resolvedManualDecisionTemplateCsvPath = (Resolve-Path -LiteralPath $ManualDecisionTemplateCsvPath).Path
    $proposedRows = @(Import-Csv -LiteralPath $resolvedProposedReadyCsvPath)
    $manualRows = @(Import-Csv -LiteralPath $resolvedManualDecisionTemplateCsvPath)
    $intakeRows = New-Object System.Collections.Generic.List[object]
    $seenKeys = New-Object 'System.Collections.Generic.HashSet[string]'
    $duplicateRows = New-Object System.Collections.Generic.List[object]

    foreach ($sourceRow in $proposedRows) {
        $row = New-ProposedReadyIntakeRow -SourceRow $sourceRow -MaxOptionColumns $MaxOptionColumns
        $key = Get-ReviewKey -Row $row
        if (-not $seenKeys.Add($key)) {
            $duplicateRows.Add($row) | Out-Null
        }
        else {
            $intakeRows.Add($row) | Out-Null
        }
    }

    foreach ($sourceRow in $manualRows) {
        $row = New-ManualDecisionIntakeRow -SourceRow $sourceRow -MaxOptionColumns $MaxOptionColumns
        $key = Get-ReviewKey -Row $row
        if (-not $seenKeys.Add($key)) {
            $duplicateRows.Add($row) | Out-Null
        }
        else {
            $intakeRows.Add($row) | Out-Null
        }
    }

    $duplicatePath = Join-Path $OutputDirectory 'duplicate-review-keys.csv'
    if ($duplicateRows.Count -gt 0) {
        $duplicateRows | Export-Csv -LiteralPath $duplicatePath -Encoding UTF8 -NoTypeInformation
        throw "Duplicate Database/ProfileId/TemplateOrdinal keys were found. See $duplicatePath"
    }

    $intakeRows = @($intakeRows.ToArray())
    $inputMode = 'create_blank_approval_intake'
}

$intakeRows = @($intakeRows | Sort-Object Database, ReviewBucket, ManualReviewPriority, RecommendedNextAction, ProfileId, TemplateOrdinal)
$validationDuplicateRows = New-Object System.Collections.Generic.List[object]
$validationSeenKeys = New-Object 'System.Collections.Generic.HashSet[string]'
foreach ($row in $intakeRows) {
    $key = Get-ReviewKey -Row $row
    if (-not $validationSeenKeys.Add($key)) {
        $validationDuplicateRows.Add($row) | Out-Null
    }
}
if ($validationDuplicateRows.Count -gt 0) {
    $duplicatePath = Join-Path $OutputDirectory 'duplicate-review-keys.csv'
    $validationDuplicateRows | Export-Csv -LiteralPath $duplicatePath -Encoding UTF8 -NoTypeInformation
    throw "Duplicate Database/ProfileId/TemplateOrdinal keys were found in approval intake rows. See $duplicatePath"
}

$validationRows = @(New-ApprovalValidationRows -Rows $intakeRows)
$validApprovalKeys = New-Object 'System.Collections.Generic.HashSet[string]'
foreach ($validationRow in @($validationRows | Where-Object { $_.ValidationStatus -eq 'approved_input_valid' })) {
    $validApprovalKeys.Add((Get-ReviewKey -Row $validationRow)) | Out-Null
}

$approvedInputValidCount = @($validationRows | Where-Object { $_.ValidationStatus -eq 'approved_input_valid' }).Count
$pendingApprovalCount = @($validationRows | Where-Object { $_.ValidationStatus -eq 'pending_approval' }).Count
$invalidApprovalInputCount = @($validationRows | Where-Object { $_.ValidationStatus -eq 'invalid_approval_input' }).Count
$approvalInputGateStatus = if ($invalidApprovalInputCount -gt 0) {
    'FAIL'
}
elseif ($RequireAllApproved.IsPresent -and ($pendingApprovalCount -gt 0 -or $approvedInputValidCount -ne $intakeRows.Count)) {
    'FAIL'
}
elseif ($pendingApprovalCount -gt 0) {
    'PENDING'
}
else {
    'PASS'
}

$approvedRows = @($intakeRows | Where-Object {
    $validApprovalKeys.Contains((Get-ReviewKey -Row $_))
})

$intakePath = Join-Path $OutputDirectory 'approval-intake-template.csv'
$validationPath = Join-Path $OutputDirectory 'approval-intake-validation.csv'
$validationStatusSummaryPath = Join-Path $OutputDirectory 'approval-intake-validation-status-summary.csv'
$summaryPath = Join-Path $OutputDirectory 'approval-intake-summary.csv'
$approvedMappingsPath = Join-Path $OutputDirectory 'approved-mappings-for-select-validation.csv'
$approvalInputGateReportPath = Join-Path $OutputDirectory 'approval-intake-gate.md'
$readmePath = Join-Path $OutputDirectory 'README.md'

$intakeRows | Export-Csv -LiteralPath $intakePath -Encoding UTF8 -NoTypeInformation
$validationRows | Export-Csv -LiteralPath $validationPath -Encoding UTF8 -NoTypeInformation

@(
    [pscustomobject]@{ ValidationStatus = 'approved_input_valid'; Count = $approvedInputValidCount }
    [pscustomobject]@{ ValidationStatus = 'pending_approval'; Count = $pendingApprovalCount }
    [pscustomobject]@{ ValidationStatus = 'invalid_approval_input'; Count = $invalidApprovalInputCount }
) | Export-Csv -LiteralPath $validationStatusSummaryPath -Encoding UTF8 -NoTypeInformation

$intakeRows |
    Group-Object Database, ReviewBucket, RecommendedNextAction |
    ForEach-Object {
        $first = $_.Group[0]
        [pscustomobject]@{
            Database = $first.Database
            ReviewBucket = $first.ReviewBucket
            RecommendedNextAction = $first.RecommendedNextAction
            Count = $_.Count
            CandidateOptionCount = ($_.Group | Measure-Object CandidateOptionCount -Sum).Sum
            SuggestedItemCount = @($_.Group | Where-Object { -not [string]::IsNullOrWhiteSpace((Get-CsvValue -Row $_ -Names @('SuggestedItemId'))) }).Count
        }
    } |
    Sort-Object Database, ReviewBucket, RecommendedNextAction |
    Export-Csv -LiteralPath $summaryPath -Encoding UTF8 -NoTypeInformation

if ($approvedRows.Count -gt 0) {
    $approvedRows |
        ForEach-Object {
            [pscustomobject]@{
                Database = Get-CsvValue -Row $_ -Names @('Database')
                ProfileId = Get-CsvValue -Row $_ -Names @('ProfileId')
                TemplateOrdinal = Get-CsvValue -Row $_ -Names @('TemplateOrdinal')
                Reason = Get-CsvValue -Row $_ -Names @('Reason')
                NameMatchClass = Get-CsvValue -Row $_ -Names @('NameMatchClass')
                AssetMatchClass = Get-CsvValue -Row $_ -Names @('AssetMatchClass')
                ReviewDecision = Get-CsvValue -Row $_ -Names @('ReviewDecision')
                ApprovedItemId = Get-CsvValue -Row $_ -Names @('ApprovedItemId')
                Reviewer = Get-CsvValue -Row $_ -Names @('Reviewer')
                ReviewNote = Get-CsvValue -Row $_ -Names @('ReviewNote')
            }
        } |
        Export-Csv -LiteralPath $approvedMappingsPath -Encoding UTF8 -NoTypeInformation
}
else {
    'Database,ProfileId,TemplateOrdinal,Reason,NameMatchClass,AssetMatchClass,ReviewDecision,ApprovedItemId,Reviewer,ReviewNote' |
        Set-Content -LiteralPath $approvedMappingsPath -Encoding UTF8
}


@(
    '# Rental template approval intake package',
    '',
    "- Created: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')",
    "- Input mode: $inputMode",
    "- Proposed ready CSV: $resolvedProposedReadyCsvPath",
    "- Manual decision CSV: $resolvedManualDecisionTemplateCsvPath",
    "- Existing approval intake CSV: $resolvedApprovalIntakeCsvPath",
    "- Intake row count: $($intakeRows.Count)",
    "- Approved valid row count: $approvedInputValidCount",
    "- Pending approval row count: $pendingApprovalCount",
    "- Invalid approval row count: $invalidApprovalInputCount",
    "- Gate status: $approvalInputGateStatus",
    '',
    '## Files',
    '- `approval-intake-template.csv`: approval intake rows for proposed-ready and manual-review cases.',
    '- `approval-intake-validation.csv`: row-level validation result.',
    '- `approval-intake-validation-status-summary.csv`: approval validation status counts for the gate.',
    '- `approved-mappings-for-select-validation.csv`: valid approved rows for follow-up SELECT-only validation.',
    '- `approval-intake-summary.csv`: summary by database/review bucket/action.',
    '- `approval-intake-gate.md`: gate status for pre-repair approval completeness.',
    '',
    '## Safety rules',
    '1. This tool only reads/writes local CSV files and does not connect to the operating database.',
    '2. Proposed-ready rows are not auto-approved; ReviewDecision and ApprovedItemId must be entered by a reviewer.',
    '3. ApprovalIntakeCsvPath mode validates reviewer-filled CSV rows and exports only valid approved rows.',
    '4. Approved rows require a reviewer name and approved item GUID.',
    '5. Approved mappings must be validated again with New-GeoraePlanRentalTemplateItemReferenceRepairPlan.ps1 -ValidateAgainstLinuxPc.',
    '6. Use -RequireAllApproved before repair-plan generation; it fails if any row is pending or invalid.'
) | Set-Content -LiteralPath $readmePath -Encoding UTF8

$approvalInputGateDecisionLine = if ($approvalInputGateStatus -eq 'PASS') {
    '- Approval input is complete for the selected mode.'
}
elseif ($RequireAllApproved.IsPresent) {
    '- Approval input is not complete. Do not generate or run repair SQL.'
}
else {
    '- Approval input is partial. Only valid approved rows were exported for follow-up validation.'
}

@(
    '# Rental template approval intake gate',
    '',
    "- Created: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')",
    "- Input mode: $inputMode",
    "- RequireAllApproved: $($RequireAllApproved.IsPresent)",
    "- Gate status: $approvalInputGateStatus",
    "- Intake row count: $($intakeRows.Count)",
    "- Approved valid row count: $approvedInputValidCount",
    "- Pending approval row count: $pendingApprovalCount",
    "- Invalid approval row count: $invalidApprovalInputCount",
    '',
    '## Decision',
    $approvalInputGateDecisionLine
) | Set-Content -LiteralPath $approvalInputGateReportPath -Encoding UTF8

Write-Host "approval_intake_output=$OutputDirectory"
Write-Host "approval_intake_template=$intakePath"
Write-Host "approval_intake_validation=$validationPath"
Write-Host "approval_intake_validation_status_summary=$validationStatusSummaryPath"
Write-Host "approval_intake_summary=$summaryPath"
Write-Host "approved_mappings_for_select_validation=$approvedMappingsPath"
Write-Host "approval_input_gate_status=$approvalInputGateStatus"
Write-Host "approval_input_gate_report=$approvalInputGateReportPath"
Write-Host "approval_input_approved_count=$approvedInputValidCount"
Write-Host "approval_input_pending_count=$pendingApprovalCount"
Write-Host "approval_input_invalid_count=$invalidApprovalInputCount"
Import-Csv -LiteralPath $summaryPath | Format-Table -AutoSize | Out-String | Write-Host

if ($RequireAllApproved.IsPresent -and $approvalInputGateStatus -ne 'PASS') {
    throw "Approval intake gate failed: approved=$approvedInputValidCount pending=$pendingApprovalCount invalid=$invalidApprovalInputCount total=$($intakeRows.Count). Report: $approvalInputGateReportPath"
}
