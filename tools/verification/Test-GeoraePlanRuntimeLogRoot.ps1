[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$EvidenceRoot = ('D:\DevCaches\runtime-log-root-' + [Guid]::NewGuid().ToString('N'))
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
}
if (Test-Path -LiteralPath $EvidenceRoot) { throw 'Use a fresh evidence root.' }
if ([IO.Path]::GetPathRoot([IO.Path]::GetFullPath($EvidenceRoot)) -ine 'D:\') {
    throw 'Evidence must stay on D:.'
}
[void][IO.Directory]::CreateDirectory($EvidenceRoot)
$sourcePath = Join-Path $ProjectRoot '테스트 시행\테스트-환경-준비.ps1'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($sourcePath, [ref]$tokens, [ref]$errors)
if ($errors.Count -ne 0) { throw ($errors.Message -join "`n") }
foreach ($name in @(
    'Initialize-TestEnvironmentFinalPathNativeMethods',
    'ConvertTo-NormalizedFullPath',
    'Enter-SourceAppRootIdentityLease',
    'Assert-SourceAppRootIdentityLease',
    'Initialize-IsolatedRuntimeLogRoot'
)) {
    $node = $ast.Find({
        param($candidate)
        $candidate -is [Management.Automation.Language.FunctionDefinitionAst] -and
            $candidate.Name -ceq $name
    }, $true)
    if ($null -eq $node) { throw "Missing function: $name" }
    . ([scriptblock]::Create($node.Extent.Text))
}

$checks = [Collections.Generic.List[string]]::new()
$runtime = Join-Path $EvidenceRoot 'fresh-runtime'
[void][IO.Directory]::CreateDirectory($runtime)
Initialize-IsolatedRuntimeLogRoot -OutputRoot $runtime
$logs = Join-Path $runtime 'RuntimeLogs'
if (-not (Test-Path -LiteralPath $logs -PathType Container)) { throw 'Fresh logs missing.' }
$checks.Add('fresh-final-runtime-logs-created')
$sentinel = Join-Path $logs 'existing.log'
[IO.File]::WriteAllBytes($sentinel, [byte[]](0, 1, 2, 255))
$before = (Get-FileHash -LiteralPath $sentinel).Hash
Initialize-IsolatedRuntimeLogRoot -OutputRoot $runtime
if ((Get-FileHash -LiteralPath $sentinel).Hash -cne $before) { throw 'Existing log changed.' }
$checks.Add('existing-log-bytes-preserved')

foreach ($case in @('file', 'child-junction', 'parent-junction')) {
    $caseRoot = Join-Path $EvidenceRoot $case
    $outside = Join-Path $EvidenceRoot ($case + '-outside')
    [void][IO.Directory]::CreateDirectory($outside)
    $outsideSentinel = Join-Path $outside 'sentinel.bin'
    [IO.File]::WriteAllBytes($outsideSentinel, [byte[]](4, 5, 6))
    if ($case -eq 'parent-junction') {
        New-Item -ItemType Junction -Path $caseRoot -Target $outside | Out-Null
    }
    else {
        [void][IO.Directory]::CreateDirectory($caseRoot)
        $caseLogs = Join-Path $caseRoot 'RuntimeLogs'
        if ($case -eq 'file') { [IO.File]::WriteAllText($caseLogs, 'preserve') }
        else { New-Item -ItemType Junction -Path $caseLogs -Target $outside | Out-Null }
    }
    $rejected = $false
    try { Initialize-IsolatedRuntimeLogRoot -OutputRoot $caseRoot }
    catch { $rejected = $true }
    if (-not $rejected) { throw "Unsafe case accepted: $case" }
    if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($outsideSentinel)) -cne 'BAUG') {
        throw "Outside bytes changed: $case"
    }
    if (@(Get-ChildItem -LiteralPath $outside -Force).Count -ne 1) {
        throw "Outside directory mutated: $case"
    }
    if ($case -eq 'file' -and [IO.File]::ReadAllText($caseLogs) -cne 'preserve') {
        throw 'Non-directory log path changed.'
    }
    $checks.Add($case + '-rejected-without-outside-write')
}

# The helper must run in the actual final certification path, after promotion.
$source = [IO.File]::ReadAllText($sourcePath)
$promotion = $source.LastIndexOf('Invoke-IsolatedRuntimeComponentPromotion `', [StringComparison]::Ordinal)
$initialization = $source.LastIndexOf('Initialize-IsolatedRuntimeLogRoot -OutputRoot $OutputRoot', [StringComparison]::Ordinal)
$certification = $source.LastIndexOf('$certifiedAppExecutables = @(', [StringComparison]::Ordinal)
if ($promotion -lt 0 -or $initialization -le $promotion -or $initialization -ge $certification) {
    throw 'Final preparation does not initialize logs between promotion and certification.'
}
$checks.Add('final-preparation-invokes-before-certification')
$result = [ordered]@{
    observedAt = (Get-Date).ToString('o')
    sourceSha256 = (Get-FileHash -LiteralPath $sourcePath).Hash
    passed = $checks.Count
    checks = @($checks)
    evidenceRoot = $EvidenceRoot
}
$result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $EvidenceRoot 'result.json') -Encoding UTF8
$result | ConvertTo-Json -Depth 4
