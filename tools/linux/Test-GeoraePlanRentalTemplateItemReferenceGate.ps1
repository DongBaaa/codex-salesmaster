[CmdletBinding()]
param(
    [string]$ProjectRoot = '',
    [string]$OutputDirectory = '',
    [string]$LinuxSshHost = '192.168.0.199',
    [int]$LinuxSshPort = 2222,
    [string]$LinuxSshUser = 'itw',
    [string]$LinuxSshKeyPath = (Join-Path $env:USERPROFILE '.ssh\itwserver_codex_ed25519'),
    [string]$RemoteOpsDirectory = '/srv/georaeplan/ops',
    [string[]]$Databases = @('georaeplan', 'georaeplan_itworld'),
    [switch]$AllowUnresolved
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

$resolvedRoot = Resolve-ProjectRoot -ExplicitProjectRoot $ProjectRoot
$exportScript = Join-Path $resolvedRoot 'tools\linux\Export-GeoraePlanRentalTemplateItemReferenceCandidates.ps1'
if (-not (Test-Path -LiteralPath $exportScript)) {
    throw "Rental template candidate export script was not found: $exportScript"
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutputDirectory = Join-Path $resolvedRoot "audit-output\rental-template-item-reference-gate-$timestamp"
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$candidateOutputDirectory = Join-Path $OutputDirectory 'candidate-export'
$reportPath = Join-Path $OutputDirectory 'rental-template-item-reference-gate.md'
$logPath = Join-Path $OutputDirectory 'rental-template-item-reference-gate.log'

$arguments = @(
    '-NoProfile',
    '-ExecutionPolicy', 'Bypass',
    '-File', $exportScript,
    '-LinuxSshHost', $LinuxSshHost,
    '-LinuxSshPort', $LinuxSshPort,
    '-LinuxSshUser', $LinuxSshUser,
    '-LinuxSshKeyPath', $LinuxSshKeyPath,
    '-RemoteOpsDirectory', $RemoteOpsDirectory,
    '-OutputDirectory', $candidateOutputDirectory,
    '-Databases', ($Databases -join ',')
)

"# rental template item reference gate log $(Get-Date -Format o)" | Set-Content -LiteralPath $logPath -Encoding UTF8
Add-Content -LiteralPath $logPath -Encoding UTF8 -Value "ProjectRoot=$resolvedRoot"
Add-Content -LiteralPath $logPath -Encoding UTF8 -Value "OutputDirectory=$OutputDirectory"
Add-Content -LiteralPath $logPath -Encoding UTF8 -Value "RemoteOpsDirectory=$RemoteOpsDirectory"
Add-Content -LiteralPath $logPath -Encoding UTF8 -Value "Databases=$($Databases -join ',')"

& powershell @arguments 2>&1 | Tee-Object -FilePath $logPath -Append
$exportExitCode = $LASTEXITCODE
if ($exportExitCode -ne 0) {
    throw "Rental template candidate export failed exit=$exportExitCode. Log: $logPath"
}

$summaryPath = Join-Path $candidateOutputDirectory 'summary-by-database.csv'
$detailSummaryPath = Join-Path $candidateOutputDirectory 'manual-review-candidate-detail-summary.csv'
if (-not (Test-Path -LiteralPath $summaryPath)) {
    throw "Candidate summary was not generated: $summaryPath"
}

$summaryRows = @(Import-Csv -LiteralPath $summaryPath)
$totalCandidateCount = 0
$totalProposedItemCount = 0
$totalManualReviewCount = 0
foreach ($row in $summaryRows) {
    $candidateCount = [int]$row.CandidateCount
    $proposedCount = if ($row.PSObject.Properties['ProposedItemCount']) { [int]$row.ProposedItemCount } else { 0 }
    $totalCandidateCount += $candidateCount
    $totalProposedItemCount += $proposedCount
    $totalManualReviewCount += [Math]::Max(0, $candidateCount - $proposedCount)
}

$manualDetailRows = if (Test-Path -LiteralPath $detailSummaryPath) { @(Import-Csv -LiteralPath $detailSummaryPath) } else { @() }
$status = if ($totalCandidateCount -eq 0) { 'PASS' } elseif ($AllowUnresolved.IsPresent) { 'WARN' } else { 'FAIL' }

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('# 렌탈 청구 템플릿 품목 참조 데이터 게이트') | Out-Null
$lines.Add('') | Out-Null
$lines.Add(('- 실행시각: {0}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz'))) | Out-Null
$lines.Add(('- 상태: `{0}`' -f $status)) | Out-Null
$lines.Add(('- 후보 전체: `{0}`' -f $totalCandidateCount)) | Out-Null
$lines.Add(('- 제안 후보: `{0}`' -f $totalProposedItemCount)) | Out-Null
$lines.Add(('- 수동 검토 추정: `{0}`' -f $totalManualReviewCount)) | Out-Null
$lines.Add(('- 후보 산출물: `{0}`' -f $candidateOutputDirectory)) | Out-Null
$lines.Add('') | Out-Null
$lines.Add('## DB별 요약') | Out-Null
$lines.Add('') | Out-Null
$lines.Add('| DB | 전체 후보 | 제안 후보 | 수동 검토 추정 | 단일 자산 제안 | 다중 자산 후보 | 포함 자산 없음 |') | Out-Null
$lines.Add('| --- | ---: | ---: | ---: | ---: | ---: | ---: |') | Out-Null
foreach ($row in $summaryRows) {
    $candidateCount = [int]$row.CandidateCount
    $proposedCount = if ($row.PSObject.Properties['ProposedItemCount']) { [int]$row.ProposedItemCount } else { 0 }
    $manualCount = [Math]::Max(0, $candidateCount - $proposedCount)
    $lines.Add(('| {0} | {1} | {2} | {3} | {4} | {5} | {6} |' -f $row.Database, $candidateCount, $proposedCount, $manualCount, $row.SingleAssetItemCount, $row.MultipleAssetItemCount, $row.NoIncludedAssetsCount)) | Out-Null
}

if ($manualDetailRows.Count -gt 0) {
    $lines.Add('') | Out-Null
    $lines.Add('## 수동 검토 상세 근거 요약') | Out-Null
    $lines.Add('') | Out-Null
    $lines.Add('| DB | 상세 유형 | 후보 상태 | 상세 행 | 관련 템플릿 | 고유 후보 품목 |') | Out-Null
    $lines.Add('| --- | --- | --- | ---: | ---: | ---: |') | Out-Null
    foreach ($row in $manualDetailRows) {
        $lines.Add(('| {0} | {1} | {2} | {3} | {4} | {5} |' -f $row.Database, $row.DetailType, $row.CandidateStatus, $row.Count, $row.DistinctProfileTemplateCount, $row.DistinctCandidateItemCount)) | Out-Null
    }
}

$lines.Add('') | Out-Null
$lines.Add('## 판정') | Out-Null
if ($totalCandidateCount -eq 0) {
    $lines.Add('- 미해결 렌탈 청구 템플릿 품목 참조가 없어 배포 게이트를 통과합니다.') | Out-Null
}
elseif ($AllowUnresolved.IsPresent) {
    $lines.Add('- 미해결 후보가 남아 있지만 `-AllowUnresolved`로 실패 대신 경고 처리했습니다. 운영 배포에는 권장하지 않습니다.') | Out-Null
}
else {
    $lines.Add('- 미해결 렌탈 청구 템플릿 품목 참조가 남아 있어 배포/납품 게이트를 실패 처리합니다.') | Out-Null
    $lines.Add('- 담당자 승인 매핑, SELECT-only 검증, 복제본/테스트 DB rollback SQL 검증 후 다시 실행해야 합니다.') | Out-Null
}
Set-Content -LiteralPath $reportPath -Value $lines -Encoding UTF8

Write-Host "rental_template_item_reference_gate_status=$status"
Write-Host "rental_template_item_reference_gate_report=$reportPath"
Write-Host "rental_template_item_reference_candidate_output=$candidateOutputDirectory"
Write-Host "rental_template_item_reference_candidate_count=$totalCandidateCount"
Write-Host "rental_template_item_reference_proposed_count=$totalProposedItemCount"
Write-Host "rental_template_item_reference_manual_review_count=$totalManualReviewCount"

if ($status -eq 'FAIL') {
    throw "Unresolved rental billing template item references remain: $totalCandidateCount. Report: $reportPath"
}
