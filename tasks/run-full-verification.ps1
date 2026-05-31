[CmdletBinding()]
param(
    [switch]$StopOnFailure,
    [switch]$SkipSolutionTests,
    [switch]$SkipTaskBuilds,
    [switch]$IncludeTaskSmokes,
    [switch]$IncludeWpfTaskSmokes,
    [switch]$IncludeWorkbookChecks
)

$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$outputJsonPath = Join-Path $root 'tasks\full-verification-result.json'
$outputMarkdownPath = Join-Path $root 'tasks\full-verification-result.md'

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
    throw 'dotnet executable was not found.'
}

function Convert-OutputText {
    param([object[]]$Output)
    if ($null -eq $Output -or $Output.Count -eq 0) { return '' }
    return (($Output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine)
}

function Invoke-VerificationStep {
    param(
        [string]$Name,
        [string]$Command,
        [scriptblock]$Script
    )

    Write-Host "==> $Name" -ForegroundColor Cyan
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $global:LASTEXITCODE = 0
    $output = @()
    $exitCode = 0
    try {
        Push-Location $root
        try {
            $output = & $Script 2>&1
            $exitCode = if ($null -eq $LASTEXITCODE) { 0 } else { [int]$LASTEXITCODE }
        }
        catch {
            $output = @($_ | Out-String)
            $exitCode = if ($LASTEXITCODE) { [int]$LASTEXITCODE } else { 1 }
        }
    }
    finally {
        Pop-Location
        $sw.Stop()
    }

    $text = (Convert-OutputText $output).TrimEnd()
    $result = [pscustomobject][ordered]@{
        name = $Name
        command = $Command
        exitCode = $exitCode
        succeeded = ($exitCode -eq 0)
        durationSeconds = [Math]::Round($sw.Elapsed.TotalSeconds, 2)
        output = $text
    }

    if ($result.succeeded) {
        Write-Host "PASS $Name ($($result.durationSeconds)s)" -ForegroundColor Green
    }
    else {
        Write-Host "FAIL $Name ($($result.durationSeconds)s)" -ForegroundColor Red
        if (-not [string]::IsNullOrWhiteSpace($text)) {
            ($text -split "`r?`n" | Select-Object -Last 20) | ForEach-Object { Write-Host $_ }
        }
    }

    return $result
}

function Find-RequiredFile {
    param([string]$Filter)
    $file = Get-ChildItem -LiteralPath $root -Recurse -File -Filter $Filter | Select-Object -First 1
    if ($null -eq $file) { throw "Required file not found: $Filter" }
    return $file.FullName
}

$dotnet = Resolve-DotnetCommand
$solutionPath = (Get-ChildItem -LiteralPath $root -Filter '*.sln' | Select-Object -First 1).FullName
if ([string]::IsNullOrWhiteSpace($solutionPath)) { throw 'Solution file was not found.' }
$serverTests = Join-Path $root 'Tests\GeoraePlan.Server.Api.Tests\GeoraePlan.Server.Api.Tests.csproj'
$desktopTests = Join-Path $root 'Tests\GeoraePlan.Desktop.App.Tests\GeoraePlan.Desktop.App.Tests.csproj'
$syncRecoveryScript = Find-RequiredFile -Filter 'Invoke-SyncRecoveryCheck.ps1'
$multiPcConflictScript = Find-RequiredFile -Filter 'Invoke-MultiPcConflictCheck.ps1'
$accountScopeRegressionScript = Join-Path $root 'tasks\Run-OptionalAccountScopeRegression.ps1'
if (-not (Test-Path -LiteralPath $accountScopeRegressionScript)) { throw "Required file not found: $accountScopeRegressionScript" }

$steps = New-Object System.Collections.Generic.List[object]
$shouldStop = $false

$steps.Add((Invoke-VerificationStep -Name 'build-solution' -Command "$dotnet build $solutionPath -c Debug" -Script {
    & $dotnet build $solutionPath -c Debug -nodeReuse:false /p:UseSharedCompilation=false
})) | Out-Null
$shouldStop = $shouldStop -or ($StopOnFailure -and -not $steps[$steps.Count - 1].succeeded)

if (-not $shouldStop -and -not $SkipSolutionTests) {
    $steps.Add((Invoke-VerificationStep -Name 'server-tests' -Command "$dotnet test $serverTests" -Script {
        & $dotnet test $serverTests -c Debug --no-restore --logger 'console;verbosity=minimal'
    })) | Out-Null
    $shouldStop = $shouldStop -or ($StopOnFailure -and -not $steps[$steps.Count - 1].succeeded)

    $steps.Add((Invoke-VerificationStep -Name 'desktop-tests' -Command "$dotnet test $desktopTests" -Script {
        & $dotnet test $desktopTests -c Debug --no-restore --logger 'console;verbosity=minimal'
    })) | Out-Null
    $shouldStop = $shouldStop -or ($StopOnFailure -and -not $steps[$steps.Count - 1].succeeded)
}

if (-not $shouldStop) {
$steps.Add((Invoke-VerificationStep -Name 'sync-recovery-regression' -Command "$syncRecoveryScript -ProjectRoot $root" -Script {
    & $syncRecoveryScript -ProjectRoot $root
})) | Out-Null
$shouldStop = $shouldStop -or ($StopOnFailure -and -not $steps[$steps.Count - 1].succeeded)
}

if (-not $shouldStop) {
$steps.Add((Invoke-VerificationStep -Name 'multi-pc-conflict-regression' -Command "$multiPcConflictScript -ProjectRoot $root" -Script {
    & $multiPcConflictScript -ProjectRoot $root
})) | Out-Null
$shouldStop = $shouldStop -or ($StopOnFailure -and -not $steps[$steps.Count - 1].succeeded)
}

$accountCommand = "powershell -NoProfile -ExecutionPolicy Bypass -File $accountScopeRegressionScript -ProjectRoot $root"
if (-not $shouldStop) {
$steps.Add((Invoke-VerificationStep -Name 'account-scope-regression' -Command $accountCommand -Script {
    & powershell -NoProfile -ExecutionPolicy Bypass -File $accountScopeRegressionScript -ProjectRoot $root
})) | Out-Null
$shouldStop = $shouldStop -or ($StopOnFailure -and -not $steps[$steps.Count - 1].succeeded)
}

if (-not $shouldStop -and -not $SkipTaskBuilds) {
    $taskProjects = Get-ChildItem -LiteralPath (Join-Path $root 'tasks') -Recurse -File -Filter '*.csproj' | Sort-Object FullName
    foreach ($project in $taskProjects) {
        $name = 'task-build-' + $project.Directory.Name
        $steps.Add((Invoke-VerificationStep -Name $name -Command "$dotnet build $($project.FullName)" -Script {
            & $dotnet build $project.FullName -c Debug --no-restore -v minimal
        })) | Out-Null
        $shouldStop = $shouldStop -or ($StopOnFailure -and -not $steps[$steps.Count - 1].succeeded)
    }
}

if (-not $shouldStop -and $IncludeTaskSmokes) {
    $smokeNames = @('OfficeCodeVerifier', 'ContractPreviewSmoke', 'RentalAssetStatusSmoke', 'RentalBillingInvoiceSmoke', 'PaymentTransferScenario')
    if ($IncludeWpfTaskSmokes) { $smokeNames += 'PaymentTransferVerifier' }
    if ($IncludeWorkbookChecks) { $smokeNames += @('RentalWorkbookAudit', 'RentalWorkbookRebuild', 'RentalWorkbookReviewReport') }

    foreach ($smokeName in $smokeNames) {
        $project = Join-Path $root "tasks\$smokeName\$smokeName.csproj"
        if (-not (Test-Path -LiteralPath $project)) {
            $steps.Add([pscustomobject][ordered]@{ name = "task-smoke-$smokeName"; command = "dotnet run $project"; exitCode = 1; succeeded = $false; durationSeconds = 0; output = "Project not found: $project" }) | Out-Null
            $shouldStop = $true
            continue
        }
        $steps.Add((Invoke-VerificationStep -Name "task-smoke-$smokeName" -Command "$dotnet run --project $project" -Script {
            & $dotnet run --project $project -c Debug --no-restore
        })) | Out-Null
        $shouldStop = $shouldStop -or ($StopOnFailure -and -not $steps[$steps.Count - 1].succeeded)
    }
}

$failed = @($steps | Where-Object { -not $_.succeeded })
$summary = [ordered]@{
    generatedAt = (Get-Date).ToString('o')
    projectRoot = $root
    dotnet = $dotnet
    solution = $solutionPath
    total = $steps.Count
    passed = @($steps | Where-Object { $_.succeeded }).Count
    failed = $failed.Count
    results = @($steps.ToArray())
}
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $outputJsonPath -Encoding UTF8

$lines = New-Object System.Collections.Generic.List[string]
$overall = if ($failed.Count -eq 0) { 'PASS' } else { 'FAIL' }
$lines.Add('# GeoraePlan full verification') | Out-Null
$lines.Add('') | Out-Null
$lines.Add("- GeneratedAt: $($summary.generatedAt)") | Out-Null
$lines.Add("- Result: **$overall**") | Out-Null
$lines.Add("- ProjectRoot: $root") | Out-Null
$lines.Add("- dotnet: $dotnet") | Out-Null
$lines.Add('') | Out-Null
$lines.Add('| Result | Step | Seconds | Command |') | Out-Null
$lines.Add('|---|---|---:|---|') | Out-Null
foreach ($row in $steps) {
    $status = if ($row.succeeded) { 'PASS' } else { 'FAIL' }
    $commandText = ([string]$row.command).Replace('|', '\|')
    $lines.Add("| $status | $($row.name) | $($row.durationSeconds) | $commandText |") | Out-Null
}
if ($failed.Count -gt 0) {
    $lines.Add('') | Out-Null
    $lines.Add('## Failed output tail') | Out-Null
    foreach ($row in $failed) {
        $tail = (([string]$row.output -split "`r?`n") | Select-Object -Last 30) -join ' / '
        $lines.Add("- $($row.name): $tail") | Out-Null
    }
}
$lines | Set-Content -LiteralPath $outputMarkdownPath -Encoding UTF8

Write-Host "Full verification: $overall" -ForegroundColor Yellow
Write-Host "JSON: $outputJsonPath"
Write-Host "Markdown: $outputMarkdownPath"
$steps | Select-Object name, succeeded, durationSeconds, exitCode | Format-Table -AutoSize

if ($failed.Count -gt 0) { exit 1 }
