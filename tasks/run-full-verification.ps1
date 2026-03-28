param(
    [switch]$StopOnFailure
)

$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dotnet = 'D:\.dotnet-sdk\dotnet.exe'
$outputPath = Join-Path $root 'tasks\full-verification-result.json'
$solutionPath = (Get-ChildItem -Path $root -Filter '*.sln' | Select-Object -First 1).FullName
if ([string]::IsNullOrWhiteSpace($solutionPath)) {
    throw '솔루션 파일을 찾을 수 없습니다.'
}

$steps = @(
    @{ Name = 'build'; Command = $dotnet + ' build "' + $solutionPath + '" -c Debug -nodeReuse:false /p:UseSharedCompilation=false' },
    @{ Name = 'server-tests'; Command = "$dotnet test $root\Tests\GeoraePlan.Server.Api.Tests\GeoraePlan.Server.Api.Tests.csproj -c Debug --no-build" },
    @{ Name = 'office-code-verifier'; Command = "$dotnet run --project $root\tasks\OfficeCodeVerifier\OfficeCodeVerifier.csproj -c Debug" },
    @{ Name = 'sync-probe-inspect'; Command = "$dotnet run --project $root\tmp\SyncProbe\SyncProbe.csproj -c Debug -- inspect" },
    @{ Name = 'syncdiag-inspect'; Command = "$dotnet run --project $root\.tmp\syncdiag\syncdiag.csproj -c Debug -- inspect" },
    @{ Name = 'pgcheck'; Command = "$dotnet run --project $root\.tmp\pgcheck\pgcheck.csproj -c Debug" },
    @{ Name = 'payment-transfer-scenario'; Command = "$dotnet run --project $root\tasks\PaymentTransferScenario\PaymentTransferScenario.csproj -c Debug" },
    @{ Name = 'payment-transfer-verifier'; Command = "$dotnet run --project $root\tasks\PaymentTransferVerifier\PaymentTransferVerifier.csproj -c Debug" },
    @{ Name = 'rental-ui-verifier'; Command = "$dotnet run --project $root\tasks\RentalUiVerifier\RentalUiVerifier.csproj -c Debug" },
    @{ Name = 'supplement-doc-smoke'; Command = "$dotnet run --project $root\tasks\SupplementDocSmoke\SupplementDocSmoke.csproj -c Debug" },
    @{ Name = 'document-audit'; Command = "$dotnet run --project $root\tasks\DocumentAudit\DocumentAudit.csproj -c Debug" },
    @{ Name = 'operational-batch-verify'; Command = "$dotnet run --project $root\tasks\OperationalBatchRunner\OperationalBatchRunner.csproj -c Debug -- --mode=verify" },
    @{ Name = 'rental-catalog-repair'; Command = "$dotnet run --project $root\tasks\RentalCatalogRepairRunner\RentalCatalogRepairRunner.csproj -c Debug" },
    @{ Name = 'rental-term-verify'; Command = "$dotnet run --project $root\.tmp\rental_term_verify\rental_term_verify.csproj -c Debug" }
)

$results = New-Object System.Collections.Generic.List[object]

foreach ($step in $steps) {
    Write-Host "==> $($step.Name)" -ForegroundColor Cyan
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $stdout = ''
    $stderr = ''
    $exitCode = -1
    try {
        Push-Location $root
        $combined = & "$env:SystemRoot\System32\cmd.exe" /d /c $step.Command 2>&1
        $exitCode = $LASTEXITCODE
        $lines = @($combined | ForEach-Object { $_.ToString() })
        $stdout = ($lines | Where-Object { $_ -ne $null }) -join [Environment]::NewLine
        $stderr = ''
    }
    finally {
        Pop-Location
        $sw.Stop()
    }

    $result = [ordered]@{
        name = $step.Name
        command = $step.Command
        exitCode = $exitCode
        succeeded = ($exitCode -eq 0)
        durationSeconds = [Math]::Round($sw.Elapsed.TotalSeconds, 2)
        stdout = $stdout.TrimEnd()
        stderr = $stderr.TrimEnd()
    }
    $results.Add([pscustomobject]$result) | Out-Null

    if ($exitCode -eq 0) {
        Write-Host "PASS $($step.Name) ($([Math]::Round($sw.Elapsed.TotalSeconds,2))s)" -ForegroundColor Green
    }
    else {
        Write-Host "FAIL $($step.Name) ($([Math]::Round($sw.Elapsed.TotalSeconds,2))s)" -ForegroundColor Red
        if ($StopOnFailure) { break }
    }
}

$summary = [ordered]@{
    generatedAt = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    total = $results.Count
    passed = @($results | Where-Object { $_.succeeded }).Count
    failed = @($results | Where-Object { -not $_.succeeded }).Count
    results = $results
}

$summary | ConvertTo-Json -Depth 6 | Set-Content -Path $outputPath -Encoding UTF8
Write-Host "결과 저장: $outputPath" -ForegroundColor Yellow

if (@($results | Where-Object { -not $_.succeeded }).Count -gt 0) {
    exit 1
}
