[CmdletBinding()]
param(
    [string]$ProjectRoot = '',
    [Parameter(Mandatory = $true)]
    [string]$ApprovalIntakeCsvPath,
    [string]$OutputDirectory = '',
    [ValidateRange(1, 100000)]
    [int]$ExpectedApprovedMappingCount = 158,
    [ValidateRange(1, 100000)]
    [int]$ExpectedReadyMappingCount = 158,
    [switch]$SkipCurrentCandidateKeyCheck,
    [string]$LinuxSshHost = '192.168.0.199',
    [int]$LinuxSshPort = 2222,
    [string]$LinuxSshUser = 'itw',
    [string]$LinuxSshKeyPath = (Join-Path $env:USERPROFILE '.ssh\itwserver_codex_ed25519'),
    [string]$RemoteOpsDirectory = '/srv/georaeplan/ops'
)

$ErrorActionPreference = 'Stop'

function Resolve-ProjectRoot {
    param([string]$ExplicitProjectRoot)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitProjectRoot)) {
        return (Resolve-Path -LiteralPath $ExplicitProjectRoot).Path
    }

    if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) {
        return (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
    }

    return (Get-Location).Path
}

function Invoke-CheckedPowerShell {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$LogPath,
        [Parameter(Mandatory = $true)][string]$StepName
    )

    Add-Content -LiteralPath $LogPath -Encoding UTF8 -Value "[$StepName] start $(Get-Date -Format o)"
    & powershell @Arguments 2>&1 | Tee-Object -FilePath $LogPath -Append
    $exitCode = $LASTEXITCODE
    Add-Content -LiteralPath $LogPath -Encoding UTF8 -Value "[$StepName] exit=$exitCode $(Get-Date -Format o)"
    if ($exitCode -ne 0) {
        throw "$StepName failed with exit code $exitCode. Log: $LogPath"
    }
}

function New-ReadinessKey {
    param([Parameter(Mandatory = $true)]$Row)

    return '{0}|{1}|{2}' -f `
        ([string]$Row.Database).Trim().ToLowerInvariant(),
        ([string]$Row.ProfileId).Trim().ToLowerInvariant(),
        ([string]$Row.TemplateOrdinal).Trim()
}

$resolvedRoot = Resolve-ProjectRoot -ExplicitProjectRoot $ProjectRoot
$resolvedApprovalIntakeCsvPath = (Resolve-Path -LiteralPath $ApprovalIntakeCsvPath).Path
$approvalIntakeScript = Join-Path $resolvedRoot 'tools\linux\New-GeoraePlanRentalTemplateApprovalIntakePack.ps1'
$repairPlanScript = Join-Path $resolvedRoot 'tools\linux\New-GeoraePlanRentalTemplateItemReferenceRepairPlan.ps1'
$candidateExportScript = Join-Path $resolvedRoot 'tools\linux\Export-GeoraePlanRentalTemplateItemReferenceCandidates.ps1'
foreach ($scriptPath in @($approvalIntakeScript, $repairPlanScript, $candidateExportScript)) {
    if (-not (Test-Path -LiteralPath $scriptPath)) {
        throw "Required script was not found: $scriptPath"
    }
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutputDirectory = Join-Path $resolvedRoot "artifacts\rental-template-item-reference-repair-readiness\$timestamp"
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$logPath = Join-Path $OutputDirectory 'repair-readiness-gate.log'
$reportPath = Join-Path $OutputDirectory 'rental-template-repair-readiness-gate.md'
$approvalOutputDirectory = Join-Path $OutputDirectory 'approval-intake'
$currentCandidateOutputDirectory = Join-Path $OutputDirectory 'current-candidates'
$repairPlanOutputDirectory = Join-Path $OutputDirectory 'repair-plan'
$readinessErrors = New-Object System.Collections.Generic.List[string]
$approvedMappingsPath = Join-Path $approvalOutputDirectory 'approved-mappings-for-select-validation.csv'
$currentCandidateRowsPath = Join-Path $currentCandidateOutputDirectory 'candidate-rows.csv'
$currentCandidateKeyMismatchPath = Join-Path $OutputDirectory 'current-candidate-key-mismatches.csv'
$repairPlanGatePath = Join-Path $repairPlanOutputDirectory 'repair-plan-gate.md'
$validationSummaryPath = Join-Path $repairPlanOutputDirectory 'validation-summary.csv'
$approvedMappingCount = 0
$currentCandidateCount = 0
$readyMappingCount = 0
$patchSqlFiles = @()

"# rental template repair readiness gate log $(Get-Date -Format o)" | Set-Content -LiteralPath $logPath -Encoding UTF8
Add-Content -LiteralPath $logPath -Encoding UTF8 -Value "ProjectRoot=$resolvedRoot"
Add-Content -LiteralPath $logPath -Encoding UTF8 -Value "ApprovalIntakeCsvPath=$resolvedApprovalIntakeCsvPath"
Add-Content -LiteralPath $logPath -Encoding UTF8 -Value "ExpectedApprovedMappingCount=$ExpectedApprovedMappingCount"
Add-Content -LiteralPath $logPath -Encoding UTF8 -Value "ExpectedReadyMappingCount=$ExpectedReadyMappingCount"
Add-Content -LiteralPath $logPath -Encoding UTF8 -Value "SkipCurrentCandidateKeyCheck=$($SkipCurrentCandidateKeyCheck.IsPresent)"

try {
    Invoke-CheckedPowerShell -StepName 'approval-intake-require-all' -LogPath $logPath -Arguments @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', $approvalIntakeScript,
        '-ApprovalIntakeCsvPath', $resolvedApprovalIntakeCsvPath,
        '-OutputDirectory', $approvalOutputDirectory,
        '-RequireAllApproved'
    )

    if (-not (Test-Path -LiteralPath $approvedMappingsPath)) {
        throw "Approved mappings were not generated: $approvedMappingsPath"
    }
    $approvedMappingCount = @(Import-Csv -LiteralPath $approvedMappingsPath).Count
    if ($approvedMappingCount -ne $ExpectedApprovedMappingCount) {
        throw "Approved mappings count mismatch after approval gate. expected=$ExpectedApprovedMappingCount actual=$approvedMappingCount"
    }
}
catch {
    $readinessErrors.Add("approval_intake: $($_.Exception.Message)") | Out-Null
}

if ($readinessErrors.Count -eq 0 -and -not $SkipCurrentCandidateKeyCheck.IsPresent) {
    try {
        Invoke-CheckedPowerShell -StepName 'current-candidate-key-check' -LogPath $logPath -Arguments @(
            '-NoProfile',
            '-ExecutionPolicy', 'Bypass',
            '-File', $candidateExportScript,
            '-OutputDirectory', $currentCandidateOutputDirectory,
            '-LinuxSshHost', $LinuxSshHost,
            '-LinuxSshPort', ([string]$LinuxSshPort),
            '-LinuxSshUser', $LinuxSshUser,
            '-LinuxSshKeyPath', $LinuxSshKeyPath,
            '-RemoteOpsDirectory', $RemoteOpsDirectory
        )

        if (-not (Test-Path -LiteralPath $currentCandidateRowsPath)) {
            throw "Current candidate rows were not generated: $currentCandidateRowsPath"
        }

        $approvedRows = @(Import-Csv -LiteralPath $approvedMappingsPath)
        $currentCandidateRows = @(Import-Csv -LiteralPath $currentCandidateRowsPath)
        $currentCandidateCount = $currentCandidateRows.Count
        if ($currentCandidateCount -ne $ExpectedApprovedMappingCount) {
            throw "Current unresolved candidate count mismatch. expected=$ExpectedApprovedMappingCount actual=$currentCandidateCount"
        }

        $approvedKeys = New-Object 'System.Collections.Generic.HashSet[string]'
        foreach ($row in $approvedRows) {
            $approvedKeys.Add((New-ReadinessKey -Row $row)) | Out-Null
        }

        $currentCandidateKeys = New-Object 'System.Collections.Generic.HashSet[string]'
        foreach ($row in $currentCandidateRows) {
            $currentCandidateKeys.Add((New-ReadinessKey -Row $row)) | Out-Null
        }

        $mismatches = New-Object System.Collections.Generic.List[object]
        foreach ($key in $currentCandidateKeys) {
            if (-not $approvedKeys.Contains($key)) {
                $mismatches.Add([pscustomobject]@{ MismatchType = 'current_candidate_missing_from_approval'; Key = $key }) | Out-Null
            }
        }
        foreach ($key in $approvedKeys) {
            if (-not $currentCandidateKeys.Contains($key)) {
                $mismatches.Add([pscustomobject]@{ MismatchType = 'approval_key_not_in_current_candidates'; Key = $key }) | Out-Null
            }
        }

        if ($mismatches.Count -gt 0) {
            $mismatches | Export-Csv -LiteralPath $currentCandidateKeyMismatchPath -Encoding UTF8 -NoTypeInformation
            throw "Approval mapping keys do not match current unresolved candidate keys. See $currentCandidateKeyMismatchPath"
        }
    }
    catch {
        $readinessErrors.Add("current_candidate_key_check: $($_.Exception.Message)") | Out-Null
    }
}

if ($readinessErrors.Count -eq 0) {
    try {
        Invoke-CheckedPowerShell -StepName 'repair-plan-select-ready' -LogPath $logPath -Arguments @(
            '-NoProfile',
            '-ExecutionPolicy', 'Bypass',
            '-File', $repairPlanScript,
            '-CandidateCsvPath', $approvedMappingsPath,
            '-OutputDirectory', $repairPlanOutputDirectory,
            '-ValidateAgainstLinuxPc',
            '-ExpectedApprovedMappingCount', ([string]$ExpectedApprovedMappingCount),
            '-ExpectedReadyMappingCount', ([string]$ExpectedReadyMappingCount),
            '-PatchMode', 'Rollback',
            '-LinuxSshHost', $LinuxSshHost,
            '-LinuxSshPort', ([string]$LinuxSshPort),
            '-LinuxSshUser', $LinuxSshUser,
            '-LinuxSshKeyPath', $LinuxSshKeyPath,
            '-RemoteOpsDirectory', $RemoteOpsDirectory
        )

        if (-not (Test-Path -LiteralPath $validationSummaryPath)) {
            throw "Validation summary was not generated: $validationSummaryPath"
        }
        $readyMappingCount = @(
            Import-Csv -LiteralPath $validationSummaryPath |
                Where-Object { $_.ValidationStatus -eq 'ready' } |
                ForEach-Object { [int]$_.Count }
        ) | Measure-Object -Sum | Select-Object -ExpandProperty Sum
        if ($null -eq $readyMappingCount) {
            $readyMappingCount = 0
        }
        if ($readyMappingCount -ne $ExpectedReadyMappingCount) {
            throw "Ready mapping count mismatch after SELECT-only validation. expected=$ExpectedReadyMappingCount actual=$readyMappingCount"
        }
        $patchSqlFiles = @(Get-ChildItem -LiteralPath $repairPlanOutputDirectory -Filter 'repair-*-rollback.sql' -File -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
        if ($ExpectedReadyMappingCount -gt 0 -and $patchSqlFiles.Count -eq 0) {
            throw "Rollback SQL was not generated even though ready mappings are expected."
        }
        foreach ($patchSqlFile in $patchSqlFiles) {
            $patchSqlText = Get-Content -LiteralPath $patchSqlFile -Raw
            if ($patchSqlText -notmatch '(?im)^\s*rollback;\s*$') {
                throw "Generated SQL is not rollback-only: $patchSqlFile"
            }
            if ($patchSqlText -match '(?im)^\s*commit;\s*$') {
                throw "Generated readiness SQL must not contain a standalone commit statement: $patchSqlFile"
            }
            foreach ($requiredSqlFragment in @(
                'do $repair_assert$',
                'approved_mapping_count mismatch',
                'target_profile_count mismatch',
                'inserted_backup_count mismatch',
                'updated_profile_count mismatch',
                'select * from "RentalBillingTemplateItemReferenceRepairCounts"'
            )) {
                if (-not $patchSqlText.Contains($requiredSqlFragment)) {
                    throw "Generated SQL is missing required safety assertion fragment '$requiredSqlFragment': $patchSqlFile"
                }
            }
        }
    }
    catch {
        $readinessErrors.Add("repair_plan: $($_.Exception.Message)") | Out-Null
    }
}

$status = if ($readinessErrors.Count -eq 0) { 'PASS' } else { 'FAIL' }
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('# Rental template item reference repair readiness gate') | Out-Null
$lines.Add('') | Out-Null
$lines.Add(('- Created: {0}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz'))) | Out-Null
$lines.Add(('- Status: {0}' -f $status)) | Out-Null
$lines.Add(('- Approval intake CSV: `{0}`' -f $resolvedApprovalIntakeCsvPath)) | Out-Null
$lines.Add(('- Expected approved mappings: {0}' -f $ExpectedApprovedMappingCount)) | Out-Null
$lines.Add(('- Actual approved mappings: {0}' -f $approvedMappingCount)) | Out-Null
$lines.Add(('- Current candidate key check skipped: {0}' -f $SkipCurrentCandidateKeyCheck.IsPresent)) | Out-Null
$lines.Add(('- Current unresolved candidates: {0}' -f $currentCandidateCount)) | Out-Null
$lines.Add(('- Expected ready mappings: {0}' -f $ExpectedReadyMappingCount)) | Out-Null
$lines.Add(('- Actual ready mappings: {0}' -f $readyMappingCount)) | Out-Null
$lines.Add(('- Approval gate output: `{0}`' -f $approvalOutputDirectory)) | Out-Null
$lines.Add(('- Current candidate output: `{0}`' -f $currentCandidateOutputDirectory)) | Out-Null
$lines.Add(('- Current candidate key mismatches: `{0}`' -f $currentCandidateKeyMismatchPath)) | Out-Null
$lines.Add(('- Repair plan output: `{0}`' -f $repairPlanOutputDirectory)) | Out-Null
$lines.Add(('- Repair plan gate: `{0}`' -f $repairPlanGatePath)) | Out-Null
$lines.Add(('- Log: `{0}`' -f $logPath)) | Out-Null
$lines.Add('') | Out-Null
$lines.Add('## Patch SQL outputs') | Out-Null
if ($patchSqlFiles.Count -gt 0) {
    foreach ($patchSqlFile in $patchSqlFiles) {
        $lines.Add(('- `{0}`' -f $patchSqlFile)) | Out-Null
    }
}
else {
    $lines.Add('- none') | Out-Null
}
$lines.Add('') | Out-Null
$lines.Add('## Decision') | Out-Null
if ($readinessErrors.Count -eq 0) {
    $lines.Add('- Repair readiness gate passed. Generated SQL is still for clone/test DB verification first; this script never executes SQL patches.') | Out-Null
}
else {
    foreach ($readinessError in $readinessErrors) {
        $lines.Add(('- {0}' -f $readinessError)) | Out-Null
    }
}
Set-Content -LiteralPath $reportPath -Value $lines -Encoding UTF8

Write-Host "rental_template_repair_readiness_status=$status"
Write-Host "rental_template_repair_readiness_report=$reportPath"
Write-Host "rental_template_repair_readiness_log=$logPath"
Write-Host "approved_mapping_count=$approvedMappingCount"
Write-Host "current_candidate_count=$currentCandidateCount"
Write-Host "current_candidate_output=$currentCandidateOutputDirectory"
Write-Host "current_candidate_key_mismatches=$currentCandidateKeyMismatchPath"
Write-Host "ready_mapping_count=$readyMappingCount"
Write-Host "approval_gate_output=$approvalOutputDirectory"
Write-Host "repair_plan_output=$repairPlanOutputDirectory"
foreach ($patchSqlFile in $patchSqlFiles) {
    Write-Host "patch_sql=$patchSqlFile"
}

if ($status -ne 'PASS') {
    throw "Repair readiness gate failed. Report: $reportPath"
}
