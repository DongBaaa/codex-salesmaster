[CmdletBinding()]
param(
    [string]$ProjectRoot = "",
    [string]$BaseUrl = "https://trade.2884.kr",
    [string]$Channel = "stable",
    [string]$OutputPath = "",
    [string]$EvidenceRoot = "",
    [switch]$Strict,
    [switch]$FailOnWarnings,
    [switch]$FailOnIntegrityWarnings,
    [string]$ProbeUsername = "",
    [string]$ProbePassword = "",
    [string]$BearerToken = "",
    [int]$MinVisibleCustomers = 1,
    [int]$MinVisibleItems = 1,
    [int]$MinVisibleInvoices = 1,
    [string[]]$AllowedIntegrityWarningCodes = @(),
    [string]$LocalCacheAppDataRoot = "",
    [switch]$RequireLocalCache,
    [switch]$FailOnLocalCacheWarning,
    [switch]$RequirePrinter,
    [switch]$RequireOnlinePrinter,
    [switch]$FailOnPrintWarnings,
    [switch]$FailOnAndroidDebugSigning,
    [string]$AndroidApkPath = "",
    [string]$AndroidAdbPath = "",
    [switch]$RequireAndroidUpdateInPlaceSmoke,
    [string]$AndroidPackageName = "kr.georaeplan.mobile",
    [string]$AndroidUsername = "usenet",
    [string]$AndroidPassword = "1234",
    [switch]$SkipApiVisibilitySmoke,
    [switch]$SkipLiveObservation,
    [switch]$SkipPrintEnvironment,
    [switch]$SkipAndroidSmoke
)

$ErrorActionPreference = 'Stop'

function Resolve-DefaultProjectRoot {
    param([Parameter(Mandatory = $true)][string]$ScriptPath)
    return (Resolve-Path (Join-Path (Split-Path -Parent $ScriptPath) '..\..')).Path
}

function Add-StepResult {
    param(
        [System.Collections.Generic.List[object]]$Results,
        [string]$Name,
        [string]$Status,
        [string]$Detail,
        [string]$Report = ""
    )

    $Results.Add([pscustomobject]@{
        Name = $Name
        Status = $Status
        Detail = $Detail
        Report = $Report
    }) | Out-Null
}

function Get-StepStatusFromOutput {
    param(
        [int]$ExitCode,
        [string]$Text
    )

    if ($ExitCode -ne 0) {
        return 'FAIL'
    }

    $statusPatterns = @(
        '(?im)^\s*result\s*=\s*(FAIL|WARN|PASS)\s*$',
        '(?im)^\s*Result\s*:\s*(FAIL|WARN|PASS)\s*$',
        '(?im)^\s*\uACB0\uACFC\s*:\s*(FAIL|WARN|PASS)\s*$',
        '(?im)^\s*-\s*Result\s*:\s*\*\*(FAIL|WARN|PASS)\*\*\s*$'
    )

    foreach ($pattern in $statusPatterns) {
        $match = [regex]::Match([string]$Text, $pattern)
        if ($match.Success) {
            return $match.Groups[1].Value.ToUpperInvariant()
        }
    }

    return 'PASS'
}

function Invoke-StepProcess {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [string]$ExpectedReportPath = ""
    )

    if (-not (Test-Path -LiteralPath $FilePath)) {
        return [pscustomobject]@{
            Name = $Name
            Status = 'FAIL'
            ExitCode = -1
            Detail = "Required step script not found: $FilePath"
            Output = ""
            Report = $ExpectedReportPath
        }
    }

    $processArguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $FilePath) + $Arguments
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $global:LASTEXITCODE = 0
    try {
        $output = & powershell @processArguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    $text = ($output | Out-String -Width 4096).Trim()
    $status = Get-StepStatusFromOutput -ExitCode $exitCode -Text $text

    return [pscustomobject]@{
        Name = $Name
        Status = $status
        ExitCode = $exitCode
        Detail = $text
        Output = $text
        Report = $ExpectedReportPath
    }
}

function Add-ArgumentsIfValue {
    param(
        [System.Collections.Generic.List[string]]$Arguments,
        [string]$Name,
        [string]$Value
    )

    if (-not [string]::IsNullOrWhiteSpace($Value)) {
        $Arguments.Add($Name) | Out-Null
        $Arguments.Add($Value) | Out-Null
    }
}

function Add-ArgumentSwitch {
    param(
        [System.Collections.Generic.List[string]]$Arguments,
        [string]$Name,
        [bool]$Enabled
    )

    if ($Enabled) {
        $Arguments.Add($Name) | Out-Null
    }
}

function Add-ArgumentsIfValues {
    param(
        [System.Collections.Generic.List[string]]$Arguments,
        [string]$Name,
        [string[]]$Values
    )

    foreach ($value in @($Values)) {
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            $Arguments.Add($Name) | Out-Null
            $Arguments.Add($value) | Out-Null
        }
    }
}

function Resolve-ProjectScriptByName {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$FileName
    )

    $match = Get-ChildItem -LiteralPath $Root -Recurse -File -Filter $FileName -ErrorAction SilentlyContinue |
        Where-Object {
            $_.FullName.IndexOf('\bin\', [System.StringComparison]::OrdinalIgnoreCase) -lt 0 -and
            $_.FullName.IndexOf('\obj\', [System.StringComparison]::OrdinalIgnoreCase) -lt 0
        } |
        Sort-Object FullName |
        Select-Object -First 1

    if ($null -eq $match) {
        return ''
    }

    return $match.FullName
}

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = Resolve-DefaultProjectRoot -ScriptPath $MyInvocation.MyCommand.Path
}
$ProjectRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) {
    $EvidenceRoot = Join-Path $ProjectRoot "output\paid-delivery-gate-$timestamp"
}
New-Item -ItemType Directory -Force -Path $EvidenceRoot | Out-Null

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $EvidenceRoot 'paid-delivery-gate.md'
}
else {
    $outputDirectory = Split-Path -Parent $OutputPath
    if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
        New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
    }
}

$effectiveFailOnWarnings = [bool]($Strict -or $FailOnWarnings)
$effectiveFailOnIntegrityWarnings = [bool]($Strict -or $FailOnIntegrityWarnings)
$effectiveRequireLocalCache = [bool]($Strict -or $RequireLocalCache)
$effectiveFailOnLocalCacheWarning = [bool]($Strict -or $FailOnLocalCacheWarning)
$effectiveRequirePrinter = [bool]($Strict -or $RequirePrinter)
$effectiveRequireOnlinePrinter = [bool]($Strict -or $RequireOnlinePrinter)
$effectiveFailOnPrintWarnings = [bool]($Strict -or $FailOnPrintWarnings)
$effectiveFailOnAndroidDebugSigning = [bool]($Strict -or $FailOnAndroidDebugSigning)
$effectiveRequireAndroidUpdateInPlaceSmoke = [bool]($Strict -or $RequireAndroidUpdateInPlaceSmoke)

$results = [System.Collections.Generic.List[object]]::new()

if ($SkipApiVisibilitySmoke) {
    Add-StepResult -Results $results -Name 'api-visibility-smoke' -Status 'SKIP' -Detail 'Skipped by option.'
}
else {
    $apiEvidence = Join-Path $EvidenceRoot 'api-visibility-smoke'
    $apiScript = Join-Path $ProjectRoot 'tools\verification\Invoke-GeoraePlanApiVisibilitySmoke.ps1'
    $apiArgs = [System.Collections.Generic.List[string]]::new()
    Add-ArgumentsIfValue -Arguments $apiArgs -Name '-BaseUrl' -Value $BaseUrl
    Add-ArgumentsIfValue -Arguments $apiArgs -Name '-Username' -Value $ProbeUsername
    Add-ArgumentsIfValue -Arguments $apiArgs -Name '-Password' -Value $ProbePassword
    Add-ArgumentsIfValue -Arguments $apiArgs -Name '-EvidenceDirectory' -Value $apiEvidence
    $apiArgs.Add('-MinCustomers') | Out-Null
    $apiArgs.Add([string]$MinVisibleCustomers) | Out-Null
    $apiArgs.Add('-MinItems') | Out-Null
    $apiArgs.Add([string]$MinVisibleItems) | Out-Null
    $apiArgs.Add('-MinInvoices') | Out-Null
    $apiArgs.Add([string]$MinVisibleInvoices) | Out-Null
    Add-ArgumentSwitch -Arguments $apiArgs -Name '-FailOnIntegrityWarnings' -Enabled $effectiveFailOnIntegrityWarnings
    Add-ArgumentsIfValues -Arguments $apiArgs -Name '-AllowedIntegrityWarningCodes' -Values $AllowedIntegrityWarningCodes

    $apiResult = Invoke-StepProcess -Name 'api-visibility-smoke' -FilePath $apiScript -Arguments ([string[]]$apiArgs) -ExpectedReportPath $apiEvidence
    Add-StepResult -Results $results -Name $apiResult.Name -Status $apiResult.Status -Detail $apiResult.Detail -Report $apiEvidence
}

if ($SkipLiveObservation) {
    Add-StepResult -Results $results -Name 'live-observation' -Status 'SKIP' -Detail 'Skipped by option.'
}
else {
    $liveReport = Join-Path $EvidenceRoot 'live-observation.md'
    $liveScript = Resolve-ProjectScriptByName -Root $ProjectRoot -FileName 'Invoke-LiveObservationCheck.ps1'
    $liveArgs = [System.Collections.Generic.List[string]]::new()
    Add-ArgumentsIfValue -Arguments $liveArgs -Name '-ProjectRoot' -Value $ProjectRoot
    Add-ArgumentsIfValue -Arguments $liveArgs -Name '-BaseUrl' -Value $BaseUrl
    Add-ArgumentsIfValue -Arguments $liveArgs -Name '-Channel' -Value $Channel
    $liveArgs.Add('-SampleCount') | Out-Null
    $liveArgs.Add('1') | Out-Null
    $liveArgs.Add('-IntervalSeconds') | Out-Null
    $liveArgs.Add('1') | Out-Null
    Add-ArgumentsIfValue -Arguments $liveArgs -Name '-ProbeUsername' -Value $ProbeUsername
    Add-ArgumentsIfValue -Arguments $liveArgs -Name '-ProbePassword' -Value $ProbePassword
    Add-ArgumentsIfValue -Arguments $liveArgs -Name '-BearerToken' -Value $BearerToken
    Add-ArgumentsIfValue -Arguments $liveArgs -Name '-LocalCacheAppDataRoot' -Value $LocalCacheAppDataRoot
    Add-ArgumentsIfValue -Arguments $liveArgs -Name '-LocalCacheEvidenceDirectory' -Value (Join-Path $EvidenceRoot 'local-cache')
    Add-ArgumentSwitch -Arguments $liveArgs -Name '-RequireLocalCacheConsistencyCheck' -Enabled $effectiveRequireLocalCache
    Add-ArgumentSwitch -Arguments $liveArgs -Name '-FailOnLocalCacheWarning' -Enabled $effectiveFailOnLocalCacheWarning
    Add-ArgumentSwitch -Arguments $liveArgs -Name '-FailOnAndroidDebugSigning' -Enabled $effectiveFailOnAndroidDebugSigning
    Add-ArgumentsIfValue -Arguments $liveArgs -Name '-OutputPath' -Value $liveReport

    $liveResult = Invoke-StepProcess -Name 'live-observation' -FilePath $liveScript -Arguments ([string[]]$liveArgs) -ExpectedReportPath $liveReport
    Add-StepResult -Results $results -Name $liveResult.Name -Status $liveResult.Status -Detail $liveResult.Detail -Report $liveReport
}

if ($SkipPrintEnvironment) {
    Add-StepResult -Results $results -Name 'print-environment' -Status 'SKIP' -Detail 'Skipped by option.'
}
else {
    $printReport = Join-Path $EvidenceRoot 'print-environment.md'
    $printScript = Join-Path $ProjectRoot 'tools\verification\Test-GeoraePlanPrintEnvironment.ps1'
    $printArgs = [System.Collections.Generic.List[string]]::new()
    Add-ArgumentsIfValue -Arguments $printArgs -Name '-ProjectRoot' -Value $ProjectRoot
    Add-ArgumentsIfValue -Arguments $printArgs -Name '-OutputPath' -Value $printReport
    Add-ArgumentSwitch -Arguments $printArgs -Name '-RequirePrinter' -Enabled $effectiveRequirePrinter
    Add-ArgumentSwitch -Arguments $printArgs -Name '-RequireOnlinePrinter' -Enabled $effectiveRequireOnlinePrinter
    Add-ArgumentSwitch -Arguments $printArgs -Name '-FailOnWarnings' -Enabled $effectiveFailOnPrintWarnings

    $printResult = Invoke-StepProcess -Name 'print-environment' -FilePath $printScript -Arguments ([string[]]$printArgs) -ExpectedReportPath $printReport
    Add-StepResult -Results $results -Name $printResult.Name -Status $printResult.Status -Detail $printResult.Detail -Report $printReport
}

if ($SkipAndroidSmoke) {
    Add-StepResult -Results $results -Name 'android-update-in-place-smoke' -Status 'SKIP' -Detail 'Skipped by option.'
}
elseif ($effectiveRequireAndroidUpdateInPlaceSmoke) {
    if ([string]::IsNullOrWhiteSpace($AndroidApkPath)) {
        Add-StepResult -Results $results -Name 'android-update-in-place-smoke' -Status 'FAIL' -Detail 'Strict or RequireAndroidUpdateInPlaceSmoke was specified, but AndroidApkPath is empty.'
    }
    else {
        $androidEvidence = Join-Path $EvidenceRoot 'android-update-in-place'
        $androidScript = Join-Path $ProjectRoot 'tools\mobile\Invoke-GeoraePlanAndroidSmoke.ps1'
        $androidArgs = [System.Collections.Generic.List[string]]::new()
        Add-ArgumentsIfValue -Arguments $androidArgs -Name '-ProjectRoot' -Value $ProjectRoot
        Add-ArgumentsIfValue -Arguments $androidArgs -Name '-ApkPath' -Value $AndroidApkPath
        Add-ArgumentsIfValue -Arguments $androidArgs -Name '-AdbPath' -Value $AndroidAdbPath
        Add-ArgumentsIfValue -Arguments $androidArgs -Name '-PackageName' -Value $AndroidPackageName
        Add-ArgumentsIfValue -Arguments $androidArgs -Name '-Username' -Value $AndroidUsername
        Add-ArgumentsIfValue -Arguments $androidArgs -Name '-Password' -Value $AndroidPassword
        Add-ArgumentsIfValue -Arguments $androidArgs -Name '-EvidenceDirectory' -Value $androidEvidence
        Add-ArgumentSwitch -Arguments $androidArgs -Name '-RequireUpdateInPlace' -Enabled $true

        $androidResult = Invoke-StepProcess -Name 'android-update-in-place-smoke' -FilePath $androidScript -Arguments ([string[]]$androidArgs) -ExpectedReportPath $androidEvidence
        Add-StepResult -Results $results -Name $androidResult.Name -Status $androidResult.Status -Detail $androidResult.Detail -Report $androidEvidence
    }
}
else {
    Add-StepResult -Results $results -Name 'android-update-in-place-smoke' -Status 'SKIP' -Detail 'Android update-in-place smoke is optional unless Strict or RequireAndroidUpdateInPlaceSmoke is specified.'
}

$failed = @($results | Where-Object { $_.Status -eq 'FAIL' })
$warnings = @($results | Where-Object { $_.Status -eq 'WARN' })
$skipped = @($results | Where-Object { $_.Status -eq 'SKIP' })
$overallStatus = if ($failed.Count -gt 0) {
    'FAIL'
}
elseif ($effectiveFailOnWarnings -and $warnings.Count -gt 0) {
    'FAIL'
}
elseif ($Strict -and $skipped.Count -gt 0) {
    'FAIL'
}
elseif ($warnings.Count -gt 0) {
    'WARN'
}
else {
    'PASS'
}

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('# GeoraePlan paid delivery gate') | Out-Null
$lines.Add('') | Out-Null
$lines.Add("- CreatedAt: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')") | Out-Null
$lines.Add("- Result: **$overallStatus**") | Out-Null
$lines.Add("- Strict: $([bool]$Strict)") | Out-Null
$lines.Add("- FailOnWarnings: $effectiveFailOnWarnings") | Out-Null
$lines.Add("- FailOnIntegrityWarnings: $effectiveFailOnIntegrityWarnings") | Out-Null
$lines.Add("- ProjectRoot: $ProjectRoot") | Out-Null
$lines.Add("- BaseUrl: $BaseUrl") | Out-Null
$lines.Add("- EvidenceRoot: $EvidenceRoot") | Out-Null
$lines.Add("- RequireApiVisibilitySmoke: $(-not [bool]$SkipApiVisibilitySmoke)") | Out-Null
$lines.Add("- MinVisibleCustomers: $MinVisibleCustomers") | Out-Null
$lines.Add("- MinVisibleItems: $MinVisibleItems") | Out-Null
$lines.Add("- MinVisibleInvoices: $MinVisibleInvoices") | Out-Null
$lines.Add("- RequireLocalCache: $effectiveRequireLocalCache") | Out-Null
$lines.Add("- RequirePrinter: $effectiveRequirePrinter") | Out-Null
$lines.Add("- RequireOnlinePrinter: $effectiveRequireOnlinePrinter") | Out-Null
$lines.Add("- FailOnAndroidDebugSigning: $effectiveFailOnAndroidDebugSigning") | Out-Null
$lines.Add("- RequireAndroidUpdateInPlaceSmoke: $effectiveRequireAndroidUpdateInPlaceSmoke") | Out-Null
$lines.Add('') | Out-Null
$lines.Add('## Step results') | Out-Null
$lines.Add('') | Out-Null
$lines.Add('| Step | Status | Report | Detail |') | Out-Null
$lines.Add('| --- | --- | --- | --- |') | Out-Null
foreach ($result in $results) {
    $detail = ([string]$result.Detail).Replace('|', '\|').Replace("`r", ' ').Replace("`n", '<br>')
    if ($detail.Length -gt 500) {
        $detail = $detail.Substring(0, 500) + '...'
    }
    $report = ([string]$result.Report).Replace('|', '\|')
    $lines.Add("| $($result.Name) | $($result.Status) | $report | $detail |") | Out-Null
}

if ($Strict -and $skipped.Count -gt 0) {
    $lines.Add('') | Out-Null
    $lines.Add('## Strict mode skipped steps') | Out-Null
    foreach ($item in $skipped) {
        $lines.Add("- $($item.Name): $($item.Detail)") | Out-Null
    }
}

if ($warnings.Count -gt 0) {
    $lines.Add('') | Out-Null
    $lines.Add('## Warning steps') | Out-Null
    if ($effectiveFailOnWarnings) {
        $lines.Add('- Warning steps are treated as FAIL because FailOnWarnings or Strict is enabled.') | Out-Null
    }
    foreach ($item in $warnings) {
        $lines.Add("- $($item.Name): $($item.Detail)") | Out-Null
    }
}

if ($failed.Count -gt 0) {
    $lines.Add('') | Out-Null
    $lines.Add('## Failed steps') | Out-Null
    foreach ($item in $failed) {
        $lines.Add("- $($item.Name): $($item.Detail)") | Out-Null
    }
}

[System.IO.File]::WriteAllText($OutputPath, ($lines -join [Environment]::NewLine), [System.Text.UTF8Encoding]::new($true))
Write-Host "paid_delivery_gate_report=$OutputPath"
Write-Host "paid_delivery_gate_evidence=$EvidenceRoot"
Write-Host "result=$overallStatus"

if ($overallStatus -eq 'FAIL') {
    exit 1
}
exit 0
