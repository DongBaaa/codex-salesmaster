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

    $dotnet = 'D:\.dotnet-sdk\dotnet.exe'
    if (-not (Test-Path -LiteralPath $dotnet)) {
        $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
        if ($null -eq $dotnetCommand) {
            throw "dotnet 실행 파일을 찾을 수 없어 로컬 계정/범위 회귀 테스트를 실행할 수 없습니다."
        }

        $dotnet = $dotnetCommand.Source
    }

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
        'FullyQualifiedName~SyncControllerTests.Pull_IncludesCrossTenantDeliveryData_ForUserWithDeliveryViewAll',
        'FullyQualifiedName~SyncControllerTests.Push_DoesNotRelinkRentalAssetCustomerAcrossTenant',
        'FullyQualifiedName~SyncControllerTests.Push_SkipsOutOfScopeWarehouseStock_AndReportsNotice'
    )
    $filter = $filterParts -join '|'

    Write-Host "account-scope-regression credentials not provided; running local deterministic scope regression tests."
    Write-Host "Filter: $filter"

    $testArgs = @('test', $testProject, '-c', 'Debug', '--no-restore', '--filter', $filter, '--logger', 'console;verbosity=minimal')
    & $dotnet @testArgs
    exit $LASTEXITCODE
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
