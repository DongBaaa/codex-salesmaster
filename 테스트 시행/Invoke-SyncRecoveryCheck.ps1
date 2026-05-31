[CmdletBinding()]
param(
    [string]$ProjectRoot = "",
    [string]$OutputPath = "",
    [string]$MarkdownOutputPath = ""
)

$ErrorActionPreference = "Stop"
$scriptRoot = if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = Split-Path -Parent $scriptRoot
}
$ProjectRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path

function Resolve-DotnetCommand {
    $candidates = @(
        $env:DOTNET_EXE,
        'D:\.dotnet-sdk\dotnet.exe',
        'C:\Users\beene\.dotnet-sdk\dotnet.exe',
        'C:\Users\beene\AppData\Local\GeoraePlan.Android\dotnet8\dotnet.exe',
        'C:\Program Files\dotnet\dotnet.exe'
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    foreach ($candidate in $candidates) {
        if (-not (Test-Path -LiteralPath $candidate)) { continue }
        try {
            & $candidate --version *> $null
            if ($LASTEXITCODE -eq 0) { return (Resolve-Path -LiteralPath $candidate).Path }
        }
        catch { }
    }

    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -ne $command) { return $command.Source }
    throw "dotnet executable was not found."
}

function New-ReportDirectory {
    param([string]$ScriptRoot)
    $recordsName = [string]([char]0xAE30) + [string]([char]0xB85D)
    $reportDirectory = Join-Path $ScriptRoot $recordsName
    New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
    return $reportDirectory
}

function Convert-OutputText {
    param([object[]]$Output)
    if ($null -eq $Output -or $Output.Count -eq 0) { return '' }
    return (($Output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine)
}

function Get-TrxCounters {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path)) {
        return [pscustomobject]@{ Total = 0; Passed = 0; Failed = 0; NotExecuted = 0 }
    }
    [xml]$xml = Get-Content -LiteralPath $Path -Raw
    $counters = $xml.TestRun.ResultSummary.Counters
    if ($null -eq $counters) {
        return [pscustomobject]@{ Total = 0; Passed = 0; Failed = 0; NotExecuted = 0 }
    }
    return [pscustomobject]@{
        Total = [int]$counters.total
        Passed = [int]$counters.passed
        Failed = [int]$counters.failed
        NotExecuted = [int]$counters.notExecuted
    }
}

function Invoke-FilteredTestStep {
    param(
        [string]$Name,
        [string]$Dotnet,
        [string]$ProjectPath,
        [string]$Filter,
        [string]$ReportDirectory
    )

    if (-not (Test-Path -LiteralPath $ProjectPath)) {
        return [pscustomobject]@{
            Name = $Name; ProjectPath = $ProjectPath; Filter = $Filter; ExitCode = 1;
            Total = 0; Passed = 0; Failed = 0; NotExecuted = 0; Succeeded = $false;
            TrxPath = ''; Output = "Project not found: $ProjectPath"
        }
    }

    $trxName = ("{0}-{1}.trx" -f $Name, (Get-Date -Format "yyyyMMdd-HHmmssfff"))
    $args = @(
        'test', $ProjectPath,
        '-c', 'Debug',
        '--no-restore',
        '--filter', $Filter,
        '--logger', "trx;LogFileName=$trxName",
        '--results-directory', $ReportDirectory
    )

    $global:LASTEXITCODE = 0
    $output = & $Dotnet @args 2>&1
    $exitCode = if ($null -eq $LASTEXITCODE) { 0 } else { [int]$LASTEXITCODE }
    $trxPath = Join-Path $ReportDirectory $trxName
    $counters = Get-TrxCounters -Path $trxPath
    $succeeded = ($exitCode -eq 0 -and $counters.Total -gt 0 -and $counters.Failed -eq 0)

    return [pscustomobject]@{
        Name = $Name
        ProjectPath = $ProjectPath
        Filter = $Filter
        ExitCode = $exitCode
        Total = $counters.Total
        Passed = $counters.Passed
        Failed = $counters.Failed
        NotExecuted = $counters.NotExecuted
        Succeeded = $succeeded
        TrxPath = $trxPath
        Output = Convert-OutputText $output
    }
}

$dotnet = Resolve-DotnetCommand
$reportDirectory = New-ReportDirectory -ScriptRoot $scriptRoot
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $reportDirectory "sync-recovery-$timestamp.json"
}
if ([string]::IsNullOrWhiteSpace($MarkdownOutputPath)) {
    $MarkdownOutputPath = Join-Path $reportDirectory "sync-recovery-$timestamp.md"
}
New-Item -ItemType Directory -Path (Split-Path -Parent $OutputPath) -Force | Out-Null
New-Item -ItemType Directory -Path (Split-Path -Parent $MarkdownOutputPath) -Force | Out-Null

$serverTests = Join-Path $ProjectRoot 'Tests\GeoraePlan.Server.Api.Tests\GeoraePlan.Server.Api.Tests.csproj'
$desktopTests = Join-Path $ProjectRoot 'Tests\GeoraePlan.Desktop.App.Tests\GeoraePlan.Desktop.App.Tests.csproj'
$steps = New-Object System.Collections.Generic.List[object]


$serverFilter = @(
    'FullyQualifiedName~SyncControllerTests.Push_DeduplicatesMutationId_ForCustomerUpdates',
    'FullyQualifiedName~SyncControllerTests.Push_ResolvesHistoricalConflict_WhenSameCustomerLaterSyncsSuccessfully',
    'FullyQualifiedName~SyncControllerTests.Push_ReusesExistingItem_WhenIncomingIdDiffersButMaterialNumberMatches',
    'FullyQualifiedName~SyncControllerTests.Push_ReusesExistingItem_WhenDescriptorMatchesSingleServerItemWithoutIdentifiers',
    'FullyQualifiedName~SyncControllerTests.Push_DeduplicatesNewItemsWithinSingleBatch_WhenNaturalKeyMatches',
    'FullyQualifiedName~SyncControllerTests.Push_ReusesExistingRentalAsset_WhenIncomingIdDiffersButManagementNumberMatches',
    'FullyQualifiedName~SyncControllerTests.Push_ReusesExistingRentalBillingProfile_WhenIncomingIdDiffersButProfileKeyMatches',
    'FullyQualifiedName~SyncControllerTests.Push_ReusesExistingRentalAssetBillingProfile_WhenIncomingBillingProfileIdIsStale',
    'FullyQualifiedName~SyncControllerTests.Push_ReassignsRentalAssetBillingProfile_ToSameScopeProfile_WhenIncomingReferenceUsesDifferentOffice',
    'FullyQualifiedName~SyncControllerTests.Push_ClearsMissingInvoiceLinkForTransaction_AndReportsNotice',
    'FullyQualifiedName~SyncControllerTests.Push_RelinksInvoiceCustomerByName_AndReportsNotice',
    'FullyQualifiedName~SyncControllerTests.Push_PreservesNewerServerItemWarehouseStockRows_WhenClientOmitsThem'
) -join '|'
$desktopFilter = @(
    'FullyQualifiedName~LocalStateServicePartialsTests.MainViewModel_PostLoginSyncSkip_ChecksServerRevisionBeforeSkippingRecentSync',
    'FullyQualifiedName~LocalStateServicePartialsTests.SyncEquivalentRevisionConflict_IgnoresFileContent_WhenMetadataMatches',
    'FullyQualifiedName~LocalStateServicePartialsTests.SyncService_HasOperationalRows_DetectsEmptyMirrorPull',
    'FullyQualifiedName~LocalStateServicePartialsTests.SyncService_PrepareRentalBillingProfileRevisionRetry_RebasesRevisionAndRequeuesOutbox',
    'FullyQualifiedName~LocalStateServicePartialsTests.SyncService_TryRepairRentalAssetRevisionConflictAsync_ResolvesWhenServerCanReplaceInvalidItemReference',
    'FullyQualifiedName~LocalStateServicePartialsTests.SyncService_TryRepairRentalAssetRevisionConflictAsync_PreparesRetryWhenLocalStateMovedWithinAllowedFields',
    'FullyQualifiedName~LocalStateServicePartialsTests.SyncService_TryPrepareItemRevisionRetryAsync_RebasesNewerLocalItemAndRequeuesOutbox',
    'FullyQualifiedName~LocalStateServicePartialsTests.SyncDiagnosticsSummary_UsesOnlyOpenIssuesForLastFailure',
    'FullyQualifiedName~LocalStateServicePartialsTests.DirtySyncQueries_RequireMatchingDomainEditPermission',
    'FullyQualifiedName~SyncScopePendingMessageTests.PendingSyncWaitingMessage_UsenetLogin_DoesNotReportItworldRentalDirty'
) -join '|'
$steps.Add((Invoke-FilteredTestStep -Name 'server-sync-recovery' -Dotnet $dotnet -ProjectPath $serverTests -Filter $serverFilter -ReportDirectory $reportDirectory)) | Out-Null
$steps.Add((Invoke-FilteredTestStep -Name 'desktop-sync-recovery' -Dotnet $dotnet -ProjectPath $desktopTests -Filter $desktopFilter -ReportDirectory $reportDirectory)) | Out-Null


$failed = @($steps | Where-Object { -not $_.Succeeded })
$overall = if ($failed.Count -eq 0) { 'PASS' } else { 'FAIL' }
$summary = [ordered]@{
    generatedAt = (Get-Date).ToString('o')
    title = 'Sync recovery regression check'
    projectRoot = $ProjectRoot
    dotnet = $dotnet
    overall = $overall
    totalSteps = $steps.Count
    passedSteps = @($steps | Where-Object { $_.Succeeded }).Count
    failedSteps = $failed.Count
    steps = @($steps.ToArray())
}
$summary | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $OutputPath -Encoding UTF8

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('# Sync recovery regression check') | Out-Null
$lines.Add('') | Out-Null
$lines.Add("- GeneratedAt: $($summary.generatedAt)") | Out-Null
$lines.Add("- Result: **$overall**") | Out-Null
$lines.Add("- ProjectRoot: $ProjectRoot") | Out-Null
$lines.Add("- dotnet: $dotnet") | Out-Null
$lines.Add('') | Out-Null
$lines.Add('| Result | Step | Tests | Passed | Failed | NotExecuted | TRX |') | Out-Null
$lines.Add('|---|---|---:|---:|---:|---:|---|') | Out-Null
foreach ($step in $steps) {
    $status = if ($step.Succeeded) { 'PASS' } else { 'FAIL' }
    $trx = if ([string]::IsNullOrWhiteSpace([string]$step.TrxPath)) { '-' } else { [string]$step.TrxPath }
    $lines.Add("| $status | $($step.Name) | $($step.Total) | $($step.Passed) | $($step.Failed) | $($step.NotExecuted) | $trx |") | Out-Null
}
if ($failed.Count -gt 0) {
    $lines.Add('') | Out-Null
    $lines.Add('## Failed steps') | Out-Null
    foreach ($step in $failed) {
        $tail = (($step.Output -split "`r?`n") | Select-Object -Last 20) -join ' / '
        $lines.Add("- $($step.Name): exit=$($step.ExitCode), tests=$($step.Total), failed=$($step.Failed), outputTail=$tail") | Out-Null
    }
}
$lines | Set-Content -LiteralPath $MarkdownOutputPath -Encoding UTF8

Write-Host "Sync recovery regression check: $overall"
Write-Host "JSON report: $OutputPath"
Write-Host "Markdown report: $MarkdownOutputPath"
$steps | Select-Object Name, Succeeded, Total, Passed, Failed, NotExecuted, TrxPath | Format-Table -AutoSize

if ($failed.Count -gt 0) {
    throw "Sync recovery regression check failed. See report: $MarkdownOutputPath"
}
