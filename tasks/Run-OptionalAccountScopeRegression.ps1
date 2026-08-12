[CmdletBinding()]
param(
    [string]$ProjectRoot = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}

function Invoke-LocalAccountScopeRegression {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectRoot
    )

    $dotnet = Resolve-DotnetCommand -ProjectRoot $ProjectRoot

    $testProject = Join-Path $ProjectRoot 'Tests\GeoraePlan.Server.Api.Tests\GeoraePlan.Server.Api.Tests.csproj'
    if (-not (Test-Path -LiteralPath $testProject)) {
        throw "서버 테스트 프로젝트를 찾을 수 없습니다: $testProject"
    }

    $filterParts = @(
        'FullyQualifiedName~OfficeScopeAndPagingTests',
        'FullyQualifiedName~SharedItemTenantScopeTests',
        'FullyQualifiedName~SyncControllerTests.Push_ReturnsForbidden_WhenDomainPermissionMissing',
        'FullyQualifiedName~SyncControllerTests.Push_AllowsDomainChanges_WhenRequiredPermissionExists',
        'FullyQualifiedName~SyncControllerTests.Push_PreservesYeonsuResponsibleOffice_ForUsenetTenantAllCustomerUpdate',
        'FullyQualifiedName~SyncControllerTests.Push_RejectsCrossTenantInventoryTransferRoute',
        'FullyQualifiedName~SyncControllerTests.Push_AllowsScopedItemUpdate_ForSameOfficeNonAdmin',
        'FullyQualifiedName~SyncControllerTests.Push_AllowsScopedCustomerUpdate_ForSameOfficeNonAdmin',
        'FullyQualifiedName~SyncControllerTests.Push_RejectsCrossTenantRentalAssetUpdate_ForUserWithRentalEditAll',
        'FullyQualifiedName~SyncControllerTests.Pull_DoesNotIncludeCrossTenantRentalData_ForUserWithRentalEditAll',
        'FullyQualifiedName~SyncControllerTests.Pull_AdminRentalUser_KeepsCustomerMirrorScoped_ButStillReceivesCrossTenantRentalAssets',
        'FullyQualifiedName~SyncControllerTests.Pull_DeliveryViewAll_KeepsInvoiceSyncWithinCurrentTenant',
        'FullyQualifiedName~SyncControllerTests.Push_DoesNotRelinkRentalAssetCustomerAcrossTenant',
        'FullyQualifiedName~SyncControllerTests.Push_SkipsOutOfScopeWarehouseStock_AndReportsNotice'
    )
    $filter = $filterParts -join '|'
    $resultsDirectory = Join-Path (
        Join-Path $ProjectRoot 'artifacts\account-scope-regression'
    ) ([guid]::NewGuid().ToString('N'))
    $trxFileName = 'account-scope-regression.trx'
    $trxPath = Join-Path $resultsDirectory $trxFileName

    Write-Host "account-scope-regression credentials not provided; running local deterministic scope regression tests."
    Write-Host "Filter: $filter"

    New-Item -ItemType Directory -Path $resultsDirectory -Force |
        Out-Null
    try {
        $testArgs = @(
            'test',
            $testProject,
            '-c',
            'Debug',
            '--no-restore',
            '--filter',
            $filter,
            '--logger',
            "trx;LogFileName=$trxFileName",
            '--results-directory',
            $resultsDirectory
        )
        & $dotnet @testArgs
        $testExitCode = $LASTEXITCODE
        if ($testExitCode -ne 0) {
            exit $testExitCode
        }

        Assert-TestFiltersMatched `
            -TrxPath $trxPath `
            -FilterParts $filterParts
    }
    finally {
        if (Test-Path -LiteralPath $resultsDirectory) {
            Remove-Item -LiteralPath $resultsDirectory -Recurse -Force
        }
    }

    exit 0
}

function Assert-TestFiltersMatched {
    param(
        [Parameter(Mandatory = $true)]
        [string]$TrxPath,

        [Parameter(Mandatory = $true)]
        [string[]]$FilterParts
    )

    if (-not (Test-Path -LiteralPath $TrxPath -PathType Leaf)) {
        throw "계정/범위 회귀 테스트 결과 파일을 찾을 수 없습니다: $TrxPath"
    }

    try {
        [xml]$trx = Get-Content -LiteralPath $TrxPath -Raw -Encoding UTF8
    }
    catch {
        throw "계정/범위 회귀 테스트 결과 파일을 읽을 수 없습니다: $TrxPath"
    }

    $testNames = @(
        $trx.TestRun.TestDefinitions.UnitTest |
            ForEach-Object {
                $className = [string]$_.TestMethod.className
                $methodName = [string]$_.TestMethod.name
                if (
                    -not [string]::IsNullOrWhiteSpace($className) -and
                    -not [string]::IsNullOrWhiteSpace($methodName)
                ) {
                    "$className.$methodName"
                }
            }
    )

    $unmatchedFilters = @(
        foreach ($filterPart in $FilterParts) {
            $operatorIndex = $filterPart.IndexOf('~')
            if (
                $operatorIndex -lt 1 -or
                $operatorIndex -eq ($filterPart.Length - 1)
            ) {
                throw "지원하지 않는 계정/범위 회귀 테스트 필터입니다: $filterPart"
            }

            $expectedNamePart = $filterPart.Substring($operatorIndex + 1)
            $hasMatch = $false
            foreach ($testName in $testNames) {
                if (
                    $testName.IndexOf(
                        $expectedNamePart,
                        [StringComparison]::OrdinalIgnoreCase
                    ) -ge 0
                ) {
                    $hasMatch = $true
                    break
                }
            }

            if (-not $hasMatch) {
                $filterPart
            }
        }
    )

    if ($unmatchedFilters.Count -gt 0) {
        throw (
            "계정/범위 회귀 테스트 필터가 테스트를 찾지 못했습니다: " +
            ($unmatchedFilters -join ', ')
        )
    }
}

function Resolve-DotnetCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectRoot,

        [string[]]$FallbackCandidates = @(
            'D:\.dotnet-sdk\dotnet.exe',
            'C:\Users\beene\.dotnet-sdk\dotnet.exe',
            'C:\Users\beene\AppData\Local\GeoraePlan.Android\dotnet8\dotnet.exe',
            'C:\Program Files\dotnet\dotnet.exe'
        )
    )

    $requiredSdkMajor = 8
    $globalJsonPath = Join-Path $ProjectRoot 'global.json'
    if (Test-Path -LiteralPath $globalJsonPath -PathType Leaf) {
        try {
            $globalJson =
                Get-Content -LiteralPath $globalJsonPath -Raw -Encoding UTF8 |
                    ConvertFrom-Json
            $requiredVersionText = [string]$globalJson.sdk.version
            if (
                $requiredVersionText -match
                    '^(?<major>[0-9]+)\.'
            ) {
                $globalJsonSdkMajor = [int]$Matches['major']
                if ($globalJsonSdkMajor -gt $requiredSdkMajor) {
                    $requiredSdkMajor = $globalJsonSdkMajor
                }
            }
        }
        catch {
            throw "global.json SDK 버전을 읽을 수 없습니다: $globalJsonPath"
        }
    }

    $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    $candidates = @(
        $env:DOTNET_EXE,
        $(if ($null -ne $dotnetCommand) { $dotnetCommand.Source })
    ) + $FallbackCandidates |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    $seenCandidates =
        [Collections.Generic.HashSet[string]]::new(
            [StringComparer]::OrdinalIgnoreCase)
    foreach ($candidate in $candidates) {
        if (
            -not $seenCandidates.Add($candidate) -or
            -not (Test-Path -LiteralPath $candidate)
        ) {
            continue
        }

        $resolvedCandidate = (Resolve-Path -LiteralPath $candidate).Path
        Push-Location $ProjectRoot
        try {
            $versionOutput = @(
                & $resolvedCandidate --version 2>&1
            )
            $exitCode = $LASTEXITCODE
        }
        catch {
            $versionOutput = @()
            $exitCode = 1
        }
        finally {
            Pop-Location
        }

        $resolvedVersionText = @(
            $versionOutput |
                ForEach-Object { [string]$_ } |
                Where-Object {
                    -not [string]::IsNullOrWhiteSpace($_)
                } |
                Select-Object -Last 1
        ) -join ''
        $resolvedSdkMajor = if (
            $resolvedVersionText -match
                '^(?<major>[0-9]+)\.'
        ) {
            [int]$Matches['major']
        }
        else {
            0
        }

        if (
            $exitCode -eq 0 -and
            $resolvedSdkMajor -ge $requiredSdkMajor
        ) {
            return $resolvedCandidate
        }
    }

    throw "dotnet 실행 파일을 찾을 수 없어 로컬 계정/범위 회귀 테스트를 실행할 수 없습니다."
}

$itworldUsername = [string]$env:GEORAEPLAN_SCOPE_ITWORLD_USERNAME
$itworldPassword = [string]$env:GEORAEPLAN_SCOPE_ITWORLD_PASSWORD
$usenetUsername = [string]$env:GEORAEPLAN_SCOPE_USENET_USERNAME
$usenetPassword = [string]$env:GEORAEPLAN_SCOPE_USENET_PASSWORD
$yeonsuUsername = [string]$env:GEORAEPLAN_SCOPE_YEONSU_USERNAME
$yeonsuPassword = [string]$env:GEORAEPLAN_SCOPE_YEONSU_PASSWORD
$baseUrl = [string]$env:GEORAEPLAN_SCOPE_BASE_URL

$hasAnyCredential =
    -not [string]::IsNullOrWhiteSpace($itworldUsername) -or
    -not [string]::IsNullOrWhiteSpace($itworldPassword) -or
    -not [string]::IsNullOrWhiteSpace($usenetUsername) -or
    -not [string]::IsNullOrWhiteSpace($usenetPassword) -or
    -not [string]::IsNullOrWhiteSpace($yeonsuUsername) -or
    -not [string]::IsNullOrWhiteSpace($yeonsuPassword)

if (-not $hasAnyCredential) {
    Invoke-LocalAccountScopeRegression -ProjectRoot $ProjectRoot
}

$scriptPath = Join-Path $ProjectRoot "테스트 시행\Invoke-AccountScopeRegressionCheck.ps1"
if (-not (Test-Path -LiteralPath $scriptPath)) {
    throw "계정별 범위 회귀 점검 스크립트를 찾을 수 없습니다: $scriptPath"
}

$args = @{
    ProjectRoot = $ProjectRoot
    ItworldUsername = $itworldUsername
    ItworldPassword = $itworldPassword
    UsenetUsername = $usenetUsername
    UsenetPassword = $usenetPassword
    YeonsuUsername = $yeonsuUsername
    YeonsuPassword = $yeonsuPassword
}

if (-not [string]::IsNullOrWhiteSpace($baseUrl)) {
    $args.BaseUrl = $baseUrl
}

& $scriptPath @args
exit $LASTEXITCODE
