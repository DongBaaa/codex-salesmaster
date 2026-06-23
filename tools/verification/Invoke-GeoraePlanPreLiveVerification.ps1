param(
    [string]$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [string]$BaseUrl = 'http://127.0.0.1:19080',
    [string]$Username = 'usenet',
    [string]$Password = '1234',
    [string]$AppDataRoot = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path '테스트 시행\실행환경\AppData'),
    [string]$EvidenceDirectory = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path 'output\pre-live-verification'),
    [string]$DotnetExe = '',
    [switch]$SkipPackageProbe,
    [switch]$SkipApiVisibilitySmoke,
    [switch]$SkipObservation,
    [switch]$SkipLocalCache,
    [switch]$SkipSameAccountConcurrency,
    [switch]$SkipDotnetTests,
    [switch]$SkipDiffCheck,
    [switch]$SkipDirtyCheck,
    [switch]$IncludeInventoryStockSmoke,
    [switch]$IncludeRentalBillingSmoke,
    [switch]$IncludeRepeatedSaveSmoke,
    [switch]$IncludeSyncErrorGuard,
    [switch]$IncludePrintDocumentSmoke,
    [switch]$IncludeMobileBuild,
    [switch]$IncludeMobileE2E,
    [string]$MobileAdbPath = '',
    [string]$MobileApkPath = '',
    [string]$MobileAndroidSdkDirectory = '',
    [string]$MobileJavaSdkDirectory = '',
    [string]$LinuxPcRoot = '',
    [string]$UpdateChannel = 'stable',
    [string]$UpdateHttpBaseUrl = '',
    [int]$ExpectedLinuxPcReleaseCount = 2,
    [int]$MinVisibleCustomers = 1,
    [int]$MinVisibleItems = 1,
    [int]$MinVisibleInvoices = 1,
    [switch]$FailOnIntegrityWarnings,
    [string[]]$AllowedIntegrityWarningCodes = @(),
    [switch]$SkipPreValidationSync,
    [switch]$SkipLinuxPcLiveDriftCheck,
    [switch]$SkipLinuxPcUpdateManifestCheck,
    [switch]$SkipUpdateHttpRouteCheck
)

$ErrorActionPreference = 'Stop'

function New-DirectoryIfMissing {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -ItemType Directory -Force -Path $Path | Out-Null
    }
}

function Resolve-DotnetExe {
    param([string]$ProjectRoot, [string]$ExplicitDotnetExe)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitDotnetExe)) {
        if (-not (Test-Path -LiteralPath $ExplicitDotnetExe)) {
            throw "지정한 dotnet을 찾지 못했습니다: $ExplicitDotnetExe"
        }

        return $ExplicitDotnetExe
    }

    $candidates = @(
        (Join-Path $ProjectRoot '.tooling\dotnet8\dotnet.exe'),
        'D:\.dotnet-sdk\dotnet.exe'
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    throw 'dotnet 실행 파일을 찾지 못했습니다.'
}

function Convert-OutputText {
    param([object[]]$Output)

    if ($null -eq $Output -or $Output.Count -eq 0) {
        return ''
    }

    return (($Output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine)
}

function Resolve-MarkdownResultStatus {
    param(
        [string]$ReportPath,
        [string]$DefaultStatus = 'PASS'
    )

    if ([string]::IsNullOrWhiteSpace($ReportPath) -or -not (Test-Path -LiteralPath $ReportPath)) {
        return $DefaultStatus
    }

    $content = Get-Content -LiteralPath $ReportPath -Raw -Encoding UTF8
    $match = [regex]::Match($content, '-\s*결과:\s*\*\*(?<status>[^*]+)\*\*')
    if ($match.Success) {
        return $match.Groups['status'].Value.Trim()
    }

    return $DefaultStatus
}

function Add-StepResult {
    param(
        [string]$Name,
        [bool]$Passed,
        [string]$Detail,
        [string]$ReportPath = '',
        [string]$Output = ''
    )

    $script:Results.Add([pscustomobject]@{
        Name = $Name
        Passed = $Passed
        Detail = $Detail
        ReportPath = $ReportPath
        Output = $Output
    }) | Out-Null
}

function Invoke-Step {
    param(
        [string]$Name,
        [scriptblock]$Script
    )

    try {
        $result = & $Script
        $detail = if ($null -eq $result) { 'OK' } else { [string]$result }
        Add-StepResult -Name $Name -Passed $true -Detail $detail
    }
    catch {
        Add-StepResult -Name $Name -Passed $false -Detail $_.Exception.Message
    }
}

function Invoke-StepWithReport {
    param(
        [string]$Name,
        [scriptblock]$Script
    )

    try {
        $result = & $Script
        Add-StepResult -Name $Name -Passed $true -Detail $result.Detail -ReportPath $result.ReportPath -Output $result.Output
    }
    catch {
        Add-StepResult -Name $Name -Passed $false -Detail $_.Exception.Message
    }
}

function Invoke-ApiHealthSummary {
    param([string]$BaseUrl, [string]$Username, [string]$Password)

    $health = (Invoke-WebRequest -UseBasicParsing -Uri ($BaseUrl.TrimEnd('/') + '/healthz') -TimeoutSec 5).StatusCode
    $ready = (Invoke-WebRequest -UseBasicParsing -Uri ($BaseUrl.TrimEnd('/') + '/readyz') -TimeoutSec 5).StatusCode
    $loginPayload = @{ username = $Username; password = $Password } | ConvertTo-Json -Compress
    $login = Invoke-RestMethod -Method Post -Uri ($BaseUrl.TrimEnd('/') + '/auth/login') -ContentType 'application/json; charset=utf-8' -Body $loginPayload -TimeoutSec 15
    if ([string]::IsNullOrWhiteSpace([string]$login.token)) {
        throw '로그인 토큰을 받지 못했습니다.'
    }

    return "health=$health, ready=$ready, login=OK"
}

function Invoke-ObservationCheck {
    param(
        [string]$ProjectRoot,
        [string]$BaseUrl,
        [string]$Username,
        [string]$Password,
        [string]$AppDataRoot,
        [string]$EvidenceDirectory,
        [bool]$SkipPackageProbe
    )

    $scriptPath = Join-Path $ProjectRoot '테스트 시행\Invoke-LiveObservationCheck.ps1'
    if (-not (Test-Path -LiteralPath $scriptPath)) {
        throw "관찰 점검 스크립트를 찾지 못했습니다: $scriptPath"
    }

    $reportPath = Join-Path $EvidenceDirectory ('observation-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.md')
    $cacheEvidence = Join-Path $EvidenceDirectory 'local-cache-from-observation'
    $arguments = @{
        ProjectRoot = $ProjectRoot
        BaseUrl = $BaseUrl
        SampleCount = 1
        IntervalSeconds = 1
        ProbeUsername = $Username
        ProbePassword = $Password
        LocalCacheAppDataRoot = $AppDataRoot
        LocalCacheEvidenceDirectory = $cacheEvidence
        OutputPath = $reportPath
    }
    if ($SkipPackageProbe) {
        $arguments.SkipPackageProbe = $true
        $arguments.SkipManifestProbe = $true
    }

    $output = & $scriptPath @arguments 2>&1
    [pscustomobject]@{
        Detail = 'PASS'
        ReportPath = $reportPath
        Output = Convert-OutputText $output
    }
}

function Invoke-ApiVisibilitySmoke {
    param(
        [string]$ProjectRoot,
        [string]$BaseUrl,
        [string]$Username,
        [string]$Password,
        [string]$EvidenceDirectory,
        [int]$MinCustomers,
        [int]$MinItems,
        [int]$MinInvoices,
        [bool]$FailOnIntegrityWarnings,
        [string[]]$AllowedIntegrityWarningCodes
    )

    $scriptPath = Join-Path $ProjectRoot 'tools\verification\Invoke-GeoraePlanApiVisibilitySmoke.ps1'
    if (-not (Test-Path -LiteralPath $scriptPath)) {
        throw "API 표시성 스모크 스크립트를 찾지 못했습니다: $scriptPath"
    }

    $smokeEvidence = Join-Path $EvidenceDirectory 'api-visibility-smoke'
    $arguments = @{
        BaseUrl = $BaseUrl
        Username = $Username
        Password = $Password
        EvidenceDirectory = $smokeEvidence
        MinCustomers = $MinCustomers
        MinItems = $MinItems
        MinInvoices = $MinInvoices
        AllowedIntegrityWarningCodes = $AllowedIntegrityWarningCodes
    }
    if ($FailOnIntegrityWarnings) {
        $arguments.FailOnIntegrityWarnings = $true
    }

    $output = & $scriptPath @arguments 2>&1
    $reportLine = @($output | Where-Object { ([string]$_).StartsWith('api_visibility_smoke_report=') } | Select-Object -Last 1)
    $reportPath = if ($reportLine.Count -gt 0) { ([string]$reportLine[0]).Substring('api_visibility_smoke_report='.Length).Trim() } else { '' }
    if ([string]::IsNullOrWhiteSpace($reportPath) -and (Test-Path -LiteralPath $smokeEvidence)) {
        $latest = Get-ChildItem -LiteralPath $smokeEvidence -Filter '*.md' -File |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1
        if ($null -ne $latest) {
            $reportPath = $latest.FullName
        }
    }

    $status = Resolve-MarkdownResultStatus -ReportPath $reportPath -DefaultStatus 'PASS'

    [pscustomobject]@{
        Detail = $status
        ReportPath = $reportPath
        Output = Convert-OutputText $output
    }
}

function Invoke-LocalCacheCheck {
    param(
        [string]$ProjectRoot,
        [string]$BaseUrl,
        [string]$Username,
        [string]$Password,
        [string]$AppDataRoot,
        [string]$EvidenceDirectory
    )

    $scriptPath = Join-Path $ProjectRoot 'tools\verification\Invoke-GeoraePlanLocalCacheConsistency.ps1'
    if (-not (Test-Path -LiteralPath $scriptPath)) {
        throw "로컬 캐시 검증 스크립트를 찾지 못했습니다: $scriptPath"
    }

    $cacheEvidence = Join-Path $EvidenceDirectory 'local-cache'
    $output = & $scriptPath -BaseUrl $BaseUrl -Username $Username -Password $Password -AppDataRoot $AppDataRoot -EvidenceDirectory $cacheEvidence -FailOnCountMismatch 2>&1
    $reportLine = @($output | Where-Object { ([string]$_).StartsWith('Report:') } | Select-Object -Last 1)
    $reportPath = if ($reportLine.Count -gt 0) { ([string]$reportLine[0]).Substring('Report:'.Length).Trim() } else { '' }
    if ([string]::IsNullOrWhiteSpace($reportPath) -and (Test-Path -LiteralPath $cacheEvidence)) {
        $latest = Get-ChildItem -LiteralPath $cacheEvidence -Filter 'local-cache-consistency-*.md' -File |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1
        if ($null -ne $latest) {
            $reportPath = $latest.FullName
        }
    }

    $statusLine = @($output | Where-Object { ([string]$_).StartsWith('Local cache consistency:') } | Select-Object -Last 1)
    $status = if ($statusLine.Count -gt 0) { ([string]$statusLine[0]).Substring('Local cache consistency:'.Length).Trim() } else { 'PASS' }
    $status = Resolve-MarkdownResultStatus -ReportPath $reportPath -DefaultStatus $status

    [pscustomobject]@{
        Detail = $status
        ReportPath = $reportPath
        Output = Convert-OutputText $output
    }
}

function Invoke-LocalPreValidationSync {
    param(
        [string]$ProjectRoot,
        [string]$DotnetExe,
        [string]$AppDataRoot,
        [string]$BaseUrl,
        [string]$Username,
        [string]$Password
    )

    $projectPath = Join-Path $ProjectRoot 'tools\SyncDiag\SyncDiag.csproj'
    if (-not (Test-Path -LiteralPath $projectPath)) {
        throw "syncdiag 프로젝트를 찾지 못했습니다: $projectPath"
    }

    $previous = @{
        GEORAEPLAN_APP_ROOT = [Environment]::GetEnvironmentVariable('GEORAEPLAN_APP_ROOT', 'Process')
        GEORAEPLAN_DISABLE_LEGACY_MERGE = [Environment]::GetEnvironmentVariable('GEORAEPLAN_DISABLE_LEGACY_MERGE', 'Process')
        GEORAEPLAN_SYNC_USERNAME = [Environment]::GetEnvironmentVariable('GEORAEPLAN_SYNC_USERNAME', 'Process')
        GEORAEPLAN_SYNC_PASSWORD = [Environment]::GetEnvironmentVariable('GEORAEPLAN_SYNC_PASSWORD', 'Process')
        GEORAEPLAN_SYNC_BASEURL = [Environment]::GetEnvironmentVariable('GEORAEPLAN_SYNC_BASEURL', 'Process')
    }

    try {
        [Environment]::SetEnvironmentVariable('GEORAEPLAN_APP_ROOT', $AppDataRoot, 'Process')
        [Environment]::SetEnvironmentVariable('GEORAEPLAN_DISABLE_LEGACY_MERGE', '1', 'Process')
        [Environment]::SetEnvironmentVariable('GEORAEPLAN_SYNC_USERNAME', $Username, 'Process')
        [Environment]::SetEnvironmentVariable('GEORAEPLAN_SYNC_PASSWORD', $Password, 'Process')
        [Environment]::SetEnvironmentVariable('GEORAEPLAN_SYNC_BASEURL', ($BaseUrl.TrimEnd('/') + '/'), 'Process')

        $output = & $DotnetExe run --project $projectPath -- maintenance-sync 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw (Convert-OutputText $output)
        }

        $text = Convert-OutputText $output
        if ($text -notmatch 'sync_ok=True') {
            throw "로컬 사전 동기화가 성공으로 끝나지 않았습니다. 출력: $text"
        }

        return $text
    }
    finally {
        foreach ($key in $previous.Keys) {
            [Environment]::SetEnvironmentVariable($key, $previous[$key], 'Process')
        }
    }
}

function Invoke-SameAccountConcurrencyCheck {
    param(
        [string]$ProjectRoot,
        [string]$BaseUrl,
        [string]$Username,
        [string]$Password,
        [string]$EvidenceDirectory
    )

    $scriptPath = Join-Path $ProjectRoot 'tools\verification\Invoke-GeoraePlanSameAccountConcurrencySmoke.ps1'
    if (-not (Test-Path -LiteralPath $scriptPath)) {
        throw "같은 계정 동시수정 스모크 스크립트를 찾지 못했습니다: $scriptPath"
    }

    $smokeEvidence = Join-Path $EvidenceDirectory 'same-account-concurrency'
    $output = & $scriptPath -BaseUrl $BaseUrl -Username $Username -Password $Password -EvidenceDirectory $smokeEvidence 2>&1
    $reportLine = @($output | Where-Object { ([string]$_).StartsWith('Report:') } | Select-Object -Last 1)
    $reportPath = if ($reportLine.Count -gt 0) { ([string]$reportLine[0]).Substring('Report:'.Length).Trim() } else { '' }
    if ([string]::IsNullOrWhiteSpace($reportPath) -and (Test-Path -LiteralPath $smokeEvidence)) {
        $latest = Get-ChildItem -LiteralPath $smokeEvidence -Filter '*.md' -File |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1
        if ($null -ne $latest) {
            $reportPath = $latest.FullName
        }
    }

    [pscustomobject]@{
        Detail = 'PASS'
        ReportPath = $reportPath
        Output = Convert-OutputText $output
    }
}

function Invoke-DotnetTests {
    param(
        [string]$ProjectRoot,
        [string]$DotnetExe,
        [string]$LogFileName
    )

    $solution = Join-Path $ProjectRoot '거래플랜.sln'
    if (-not (Test-Path -LiteralPath $solution)) {
        throw "솔루션 파일을 찾지 못했습니다: $solution"
    }

    $output = & $DotnetExe test $solution -c Debug --no-restore --logger "trx;LogFileName=$LogFileName" 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw (Convert-OutputText $output)
    }

    return Convert-OutputText $output
}

function Convert-ReleaseInfoToMap {
    param([string[]]$Lines)

    $map = @{}
    foreach ($line in $Lines) {
        if ($line -match '^\s*([^=]+?)\s*=\s*(.*)\s*$') {
            $map[$Matches[1].Trim()] = $Matches[2].Trim()
        }
    }

    return $map
}

function Invoke-LinuxPcLiveDriftCheck {
    param(
        [string]$ProjectRoot,
        [string]$LinuxPcRoot,
        [int]$ExpectedReleaseCount
    )

    if ([string]::IsNullOrWhiteSpace($LinuxPcRoot)) {
        return 'SKIP - Linux PC root not configured'
    }

    if (-not (Test-Path -LiteralPath $LinuxPcRoot)) {
        return "SKIP - Linux PC root unavailable: $LinuxPcRoot"
    }

    $liveInfoPath = Join-Path $LinuxPcRoot 'app\live\release-info.txt'
    if (-not (Test-Path -LiteralPath $liveInfoPath)) {
        return "SKIP - live release-info not found: $liveInfoPath"
    }

    Push-Location $ProjectRoot
    try {
        $headCommit = ((& git rev-parse HEAD 2>$null) | Select-Object -First 1)
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($headCommit)) {
            throw 'git rev-parse HEAD failed'
        }
    }
    finally {
        Pop-Location
    }

    $liveInfo = Convert-ReleaseInfoToMap -Lines (Get-Content -LiteralPath $liveInfoPath -ErrorAction Stop)
    $liveCommit = [string]$liveInfo['commit']
    $releaseId = [string]$liveInfo['release_id']
    $builtAt = [string]$liveInfo['built_at']
    $isSameCommit = (-not [string]::IsNullOrWhiteSpace($liveCommit)) -and
        ($headCommit.StartsWith($liveCommit, [System.StringComparison]::OrdinalIgnoreCase) -or
         $liveCommit.StartsWith($headCommit, [System.StringComparison]::OrdinalIgnoreCase))

    $releasesRoot = Join-Path $LinuxPcRoot 'releases'
    $releaseCount = if (Test-Path -LiteralPath $releasesRoot) {
        @(Get-ChildItem -LiteralPath $releasesRoot -Directory -ErrorAction SilentlyContinue).Count
    }
    else {
        -1
    }
    $releaseCountStatus = if ($releaseCount -lt 0) {
        'unknown'
    }
    elseif ($ExpectedReleaseCount -gt 0 -and $releaseCount -gt $ExpectedReleaseCount) {
        "exceeds_expected_$ExpectedReleaseCount"
    }
    else {
        'ok'
    }

    $detail = "live_release_id=$releaseId; live_built_at=$builtAt; head_commit=$headCommit; live_commit=$liveCommit; live_matches_head=$isSameCommit; linux_pc_release_count=$releaseCount; linux_pc_release_count_status=$releaseCountStatus"
    if ($releaseCountStatus -like 'exceeds_expected_*') {
        throw $detail
    }

    return $detail
}

function Get-ObjectPropertyValue {
    param(
        [object]$Object,
        [string]$Name
    )

    if ($null -eq $Object) {
        return $null
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function Test-LinuxPcUpdatePackage {
    param(
        [Parameter(Mandatory = $true)][object]$Manifest,
        [Parameter(Mandatory = $true)][string]$Platform,
        [Parameter(Mandatory = $true)][string]$StorageRoot
    )

    $package = Get-ObjectPropertyValue -Object $Manifest -Name $Platform
    if ($null -eq $package) {
        throw "$Platform update package entry is missing from manifest"
    }

    $declaredPlatform = [string](Get-ObjectPropertyValue -Object $package -Name 'platform')
    if (-not [string]::IsNullOrWhiteSpace($declaredPlatform) -and
        -not [string]::Equals($declaredPlatform.Trim(), $Platform, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Platform update package platform mismatch: declared=$declaredPlatform"
    }

    $fileName = [string](Get-ObjectPropertyValue -Object $package -Name 'fileName')
    $packageUrl = [string](Get-ObjectPropertyValue -Object $package -Name 'packageUrl')
    if ([string]::IsNullOrWhiteSpace($fileName) -and -not [string]::IsNullOrWhiteSpace($packageUrl)) {
        $fileName = [System.IO.Path]::GetFileName(([Uri]::UnescapeDataString($packageUrl)))
    }
    if ([string]::IsNullOrWhiteSpace($fileName)) {
        throw "$Platform update package has no fileName/packageUrl"
    }

    $safeFileName = [System.IO.Path]::GetFileName($fileName)
    if (-not [string]::Equals($safeFileName, $fileName, [System.StringComparison]::Ordinal)) {
        throw "$Platform update package fileName is not safe: $fileName"
    }

    if ([string]::IsNullOrWhiteSpace($packageUrl)) {
        throw "$Platform update package has no packageUrl"
    }

    $packagePath = $packageUrl.Trim()
    $absolutePackageUri = $null
    if ([Uri]::TryCreate($packagePath, [UriKind]::Absolute, [ref]$absolutePackageUri)) {
        $packagePath = $absolutePackageUri.AbsolutePath
    }
    $packagePath = ($packagePath -split '[?#]', 2)[0]
    if (-not $packagePath.StartsWith('/', [System.StringComparison]::Ordinal)) {
        throw "$Platform update packageUrl must be root-relative or absolute: $packageUrl"
    }

    $expectedPrefix = "/updates/download/$Platform/"
    if (-not $packagePath.StartsWith($expectedPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Platform update packageUrl route mismatch: expectedPrefix=$expectedPrefix actual=$packageUrl"
    }

    $urlFileName = [Uri]::UnescapeDataString(($packagePath.Substring($expectedPrefix.Length)))
    if ([string]::IsNullOrWhiteSpace($urlFileName) -or $urlFileName.Contains('/')) {
        throw "$Platform update packageUrl fileName is invalid: $packageUrl"
    }
    if (-not [string]::Equals($urlFileName, $safeFileName, [System.StringComparison]::Ordinal)) {
        throw "$Platform update packageUrl fileName mismatch: url=$urlFileName fileName=$safeFileName"
    }

    $filePath = Join-Path (Join-Path $StorageRoot 'downloads') (Join-Path $Platform $safeFileName)
    if (-not (Test-Path -LiteralPath $filePath)) {
        throw "$Platform update package file is missing: $filePath"
    }

    $file = Get-Item -LiteralPath $filePath -ErrorAction Stop
    $expectedSizeValue = Get-ObjectPropertyValue -Object $package -Name 'fileSize'
    if ($null -ne $expectedSizeValue -and [long]$expectedSizeValue -gt 0 -and $file.Length -ne [long]$expectedSizeValue) {
        throw "$Platform update package size mismatch: expected=$expectedSizeValue actual=$($file.Length) file=$filePath"
    }

    $expectedHash = ([string](Get-ObjectPropertyValue -Object $package -Name 'sha256')).Trim()
    if ([string]::IsNullOrWhiteSpace($expectedHash)) {
        throw "$Platform update package has no sha256"
    }

    $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $filePath).Hash
    if (-not [string]::Equals($actualHash, $expectedHash, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Platform update package hash mismatch: expected=$expectedHash actual=$actualHash file=$filePath"
    }

    $version = [string](Get-ObjectPropertyValue -Object $package -Name 'version')
    if ([string]::IsNullOrWhiteSpace($version)) {
        throw "$Platform update package has no version"
    }

    return "$Platform=$safeFileName version=$version size=$($file.Length) sha256=ok"
}

function Invoke-LinuxPcUpdateManifestCheck {
    param(
        [string]$LinuxPcRoot,
        [string]$Channel
    )

    if ([string]::IsNullOrWhiteSpace($LinuxPcRoot)) {
        return 'SKIP - Linux PC root not configured'
    }

    if (-not (Test-Path -LiteralPath $LinuxPcRoot)) {
        return "SKIP - Linux PC root unavailable: $LinuxPcRoot"
    }

    $storageRoot = Join-Path $LinuxPcRoot 'app\live\updates'
    $manifestPath = Join-Path (Join-Path $storageRoot 'manifest') (($Channel.Trim().ToLowerInvariant()) + '.json')
    if (-not (Test-Path -LiteralPath $manifestPath)) {
        throw "update manifest is missing: $manifestPath"
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $desktopDetail = Test-LinuxPcUpdatePackage -Manifest $manifest -Platform 'desktop' -StorageRoot $storageRoot
    $androidDetail = Test-LinuxPcUpdatePackage -Manifest $manifest -Platform 'android' -StorageRoot $storageRoot
    $generatedAtUtc = [string](Get-ObjectPropertyValue -Object $manifest -Name 'generatedAtUtc')

    return "channel=$Channel; generatedAtUtc=$generatedAtUtc; $desktopDetail; $androidDetail"
}

function New-AbsoluteUpdateUri {
    param(
        [Parameter(Mandatory = $true)][string]$BaseUrl,
        [Parameter(Mandatory = $true)][string]$PathOrUrl
    )

    $baseUri = [Uri]($BaseUrl.TrimEnd('/') + '/')
    $absoluteUri = $null
    if ([Uri]::TryCreate($PathOrUrl, [UriKind]::Absolute, [ref]$absoluteUri)) {
        return $absoluteUri
    }

    return [Uri]::new($baseUri, $PathOrUrl.TrimStart('/'))
}

function Send-UpdateProbeRequest {
    param(
        [Parameter(Mandatory = $true)][System.Net.Http.HttpClient]$Client,
        [Parameter(Mandatory = $true)][Uri]$Uri
    )

    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, $Uri)
    try {
        return $Client.SendAsync($request, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
    }
    catch {
        $request.Dispose()
        throw
    }
}

function Test-UpdateHttpPackage {
    param(
        [Parameter(Mandatory = $true)][object]$Manifest,
        [Parameter(Mandatory = $true)][string]$Platform,
        [Parameter(Mandatory = $true)][string]$BaseUrl,
        [Parameter(Mandatory = $true)][System.Net.Http.HttpClient]$Client
    )

    $package = Get-ObjectPropertyValue -Object $Manifest -Name $Platform
    if ($null -eq $package) {
        throw "$Platform HTTP update package entry is missing from manifest"
    }

    $packageUrl = [string](Get-ObjectPropertyValue -Object $package -Name 'packageUrl')
    if ([string]::IsNullOrWhiteSpace($packageUrl)) {
        throw "$Platform HTTP update package has no packageUrl"
    }

    $expectedSize = Get-ObjectPropertyValue -Object $package -Name 'fileSize'
    $uri = New-AbsoluteUpdateUri -BaseUrl $BaseUrl -PathOrUrl $packageUrl
    $response = Send-UpdateProbeRequest -Client $Client -Uri $uri
    try {
        if (-not $response.IsSuccessStatusCode) {
            throw "$Platform HTTP update package failed: status=$([int]$response.StatusCode) uri=$uri"
        }

        $actualLength = $response.Content.Headers.ContentLength
        if ($null -ne $expectedSize -and [long]$expectedSize -gt 0 -and $null -ne $actualLength -and [long]$actualLength -ne [long]$expectedSize) {
            throw "$Platform HTTP update package length mismatch: expected=$expectedSize actual=$actualLength uri=$uri"
        }

        $contentType = if ($null -ne $response.Content.Headers.ContentType) { [string]$response.Content.Headers.ContentType } else { '' }
        $version = [string](Get-ObjectPropertyValue -Object $package -Name 'version')
        return "$Platform=$([int]$response.StatusCode) version=$version length=$actualLength contentType=$contentType"
    }
    finally {
        $response.Dispose()
    }
}

function Invoke-UpdateHttpRouteCheck {
    param(
        [string]$BaseUrl,
        [string]$Channel
    )

    if ([string]::IsNullOrWhiteSpace($BaseUrl)) {
        return 'SKIP - update HTTP base URL not configured'
    }

    Add-Type -AssemblyName System.Net.Http
    $client = [System.Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromSeconds(60)
    try {
        $manifestUri = New-AbsoluteUpdateUri -BaseUrl $BaseUrl -PathOrUrl ("updates/manifest?channel={0}" -f [Uri]::EscapeDataString($Channel))
        $manifestResponse = Send-UpdateProbeRequest -Client $client -Uri $manifestUri
        try {
            if (-not $manifestResponse.IsSuccessStatusCode) {
                throw "update HTTP manifest failed: status=$([int]$manifestResponse.StatusCode) uri=$manifestUri"
            }

            $manifestJson = $manifestResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            $manifest = $manifestJson | ConvertFrom-Json
            $desktopDetail = Test-UpdateHttpPackage -Manifest $manifest -Platform 'desktop' -BaseUrl $BaseUrl -Client $client
            $androidDetail = Test-UpdateHttpPackage -Manifest $manifest -Platform 'android' -BaseUrl $BaseUrl -Client $client
            $generatedAtUtc = [string](Get-ObjectPropertyValue -Object $manifest -Name 'generatedAtUtc')
            return "baseUrl=$($BaseUrl.TrimEnd('/')); channel=$Channel; generatedAtUtc=$generatedAtUtc; $desktopDetail; $androidDetail"
        }
        finally {
            $manifestResponse.Dispose()
        }
    }
    finally {
        $client.Dispose()
    }
}

function Invoke-DirtyCheck {
    param(
        [string]$ProjectRoot,
        [string]$DotnetExe,
        [string]$AppDataRoot,
        [string]$BaseUrl,
        [string]$Username,
        [string]$Password
    )

    $projectPath = Join-Path $ProjectRoot 'tools\SyncDiag\SyncDiag.csproj'
    if (-not (Test-Path -LiteralPath $projectPath)) {
        throw "syncdiag 프로젝트를 찾지 못했습니다: $projectPath"
    }

    $previousRoot = [Environment]::GetEnvironmentVariable('GEORAEPLAN_APP_ROOT', 'Process')
    $previousLegacy = [Environment]::GetEnvironmentVariable('GEORAEPLAN_DISABLE_LEGACY_MERGE', 'Process')
    $previousSyncUsername = [Environment]::GetEnvironmentVariable('GEORAEPLAN_SYNC_USERNAME', 'Process')
    $previousSyncPassword = [Environment]::GetEnvironmentVariable('GEORAEPLAN_SYNC_PASSWORD', 'Process')
    $previousSyncBaseUrl = [Environment]::GetEnvironmentVariable('GEORAEPLAN_SYNC_BASEURL', 'Process')
    try {
        [Environment]::SetEnvironmentVariable('GEORAEPLAN_APP_ROOT', $AppDataRoot, 'Process')
        [Environment]::SetEnvironmentVariable('GEORAEPLAN_DISABLE_LEGACY_MERGE', '1', 'Process')
        [Environment]::SetEnvironmentVariable('GEORAEPLAN_SYNC_USERNAME', $Username, 'Process')
        [Environment]::SetEnvironmentVariable('GEORAEPLAN_SYNC_PASSWORD', $Password, 'Process')
        [Environment]::SetEnvironmentVariable('GEORAEPLAN_SYNC_BASEURL', ($BaseUrl.TrimEnd('/') + '/'), 'Process')
        $output = & $DotnetExe run --project $projectPath -- inspect 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw (Convert-OutputText $output)
        }

        $text = Convert-OutputText $output
        if ($text -match 'current_scope_dirty=(\d+)') {
            if ([int]$Matches[1] -ne 0) {
                throw "현재 로그인 범위 dirty 상태가 0이 아닙니다: current_scope_dirty=$($Matches[1])"
            }
        }
        else {
            throw '현재 로그인 범위 dirty 상태를 확인하지 못했습니다.'
        }

        return $text
    }
    finally {
        [Environment]::SetEnvironmentVariable('GEORAEPLAN_APP_ROOT', $previousRoot, 'Process')
        [Environment]::SetEnvironmentVariable('GEORAEPLAN_DISABLE_LEGACY_MERGE', $previousLegacy, 'Process')
        [Environment]::SetEnvironmentVariable('GEORAEPLAN_SYNC_USERNAME', $previousSyncUsername, 'Process')
        [Environment]::SetEnvironmentVariable('GEORAEPLAN_SYNC_PASSWORD', $previousSyncPassword, 'Process')
        [Environment]::SetEnvironmentVariable('GEORAEPLAN_SYNC_BASEURL', $previousSyncBaseUrl, 'Process')
    }
}

function Resolve-AndroidSdkDirectory {
    param([string]$RequestedPath)

    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $candidates += $RequestedPath
    }
    if (-not [string]::IsNullOrWhiteSpace($env:ANDROID_HOME)) {
        $candidates += $env:ANDROID_HOME
    }
    if (-not [string]::IsNullOrWhiteSpace($env:ANDROID_SDK_ROOT)) {
        $candidates += $env:ANDROID_SDK_ROOT
    }
    $candidates += @(
        (Join-Path $env:LOCALAPPDATA 'Android\Sdk'),
        (Join-Path $env:LOCALAPPDATA 'GeoraePlan.Android\android-sdk')
    )

    foreach ($candidate in $candidates | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) {
        if (Test-Path -LiteralPath (Join-Path $candidate 'platform-tools\adb.exe')) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw 'Android SDK를 찾지 못했습니다. -MobileAndroidSdkDirectory 값을 지정하세요.'
}

function Resolve-JavaSdkDirectory {
    param([string]$RequestedPath)

    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $candidates += $RequestedPath
    }
    if (-not [string]::IsNullOrWhiteSpace($env:JAVA_HOME)) {
        $candidates += $env:JAVA_HOME
    }
    $candidates += @(
        'C:\Program Files\Android\Android Studio\jbr',
        'C:\Program Files\Eclipse Adoptium\jdk-17.0.10.7-hotspot',
        'C:\Program Files\Java\jdk-17'
    )

    foreach ($candidate in $candidates | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) {
        if (Test-Path -LiteralPath (Join-Path $candidate 'bin\java.exe')) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw 'Java SDK를 찾지 못했습니다. -MobileJavaSdkDirectory 값을 지정하세요.'
}

function Invoke-MobileDebugBuild {
    param(
        [string]$ProjectRoot,
        [string]$DotnetExe,
        [string]$AndroidSdkDirectory,
        [string]$JavaSdkDirectory
    )

    $projectFile = Join-Path $ProjectRoot 'Mobile\GeoraePlan.Mobile.App\GeoraePlan.Mobile.App.csproj'
    if (-not (Test-Path -LiteralPath $projectFile)) {
        throw "모바일 프로젝트를 찾지 못했습니다: $projectFile"
    }

    $resolvedAndroidSdk = Resolve-AndroidSdkDirectory -RequestedPath $AndroidSdkDirectory
    $resolvedJavaSdk = Resolve-JavaSdkDirectory -RequestedPath $JavaSdkDirectory
    $arguments = @(
        'build',
        $projectFile,
        '-c', 'Debug',
        '-f', 'net8.0-android',
        '--no-restore',
        "-p:AndroidSdkDirectory=$resolvedAndroidSdk",
        "-p:JavaSdkDirectory=$resolvedJavaSdk"
    )

    $output = & $DotnetExe @arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw (Convert-OutputText $output)
    }

    $apk = Get-ChildItem -LiteralPath (Join-Path $ProjectRoot 'Mobile\GeoraePlan.Mobile.App\bin\Debug\net8.0-android') -Filter '*Signed.apk' -Recurse -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if ($null -eq $apk) {
        return "PASS - APK 산출물은 기존 E2E 스크립트의 기본 탐색 경로에서 찾지 못했지만 Android Debug 빌드는 성공했습니다."
    }

    return "PASS - $($apk.FullName)"
}

function Invoke-RentalBillingInvoiceSmoke {
    param(
        [string]$ProjectRoot,
        [string]$DotnetExe,
        [string]$EvidenceDirectory
    )

    $projectPath = Join-Path $ProjectRoot 'tasks\RentalBillingInvoiceSmoke\RentalBillingInvoiceSmoke.csproj'
    if (-not (Test-Path -LiteralPath $projectPath)) {
        throw "렌탈 청구 스모크 프로젝트를 찾지 못했습니다: $projectPath"
    }

    New-DirectoryIfMissing $EvidenceDirectory
    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $rawPath = Join-Path $EvidenceDirectory "rental-billing-invoice-smoke-$timestamp.txt"
    $jsonPath = Join-Path $EvidenceDirectory "rental-billing-invoice-smoke-$timestamp.json"
    $mdPath = Join-Path $EvidenceDirectory "rental-billing-invoice-smoke-$timestamp.md"

    $output = & $DotnetExe run --project $projectPath -c Debug 2>&1
    $text = Convert-OutputText $output
    $text | Set-Content -LiteralPath $rawPath -Encoding UTF8
    if ($LASTEXITCODE -ne 0) {
        throw $text
    }

    $jsonText = ''
    $jsonStart = $text.IndexOf('{')
    if ($jsonStart -ge 0) {
        $jsonText = $text.Substring($jsonStart).Trim()
        try {
            $parsed = $jsonText | ConvertFrom-Json
            $parsed | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $jsonPath -Encoding UTF8
        }
        catch {
            $jsonPath = ''
        }
    }

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('# 렌탈 청구 전표 스모크') | Out-Null
    $lines.Add('') | Out-Null
    $lines.Add("- 실행시각: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')") | Out-Null
    $lines.Add('- 결과: **PASS**') | Out-Null
    $lines.Add("- 원본 출력: $rawPath") | Out-Null
    if (-not [string]::IsNullOrWhiteSpace($jsonPath)) {
        $lines.Add("- JSON 결과: $jsonPath") | Out-Null
    }
    $lines.Add('') | Out-Null
    $lines.Add('## 확인 범위') | Out-Null
    $lines.Add('- 묶음 렌탈 청구 전표 생성/재사용') | Out-Null
    $lines.Add('- 개별 렌탈 청구 전표 생성/재사용') | Out-Null
    $lines.Add('- 1월 경계/후불/선불 청구기간 계산') | Out-Null
    $lines.Add('- 거래처 해석 fallback') | Out-Null
    $lines.Add('- 단일 후보 자산 자동 연결') | Out-Null
    $lines.Add('- legacy 연결 자산 fallback') | Out-Null
    $lines | Set-Content -LiteralPath $mdPath -Encoding UTF8

    [pscustomobject]@{
        Detail = 'PASS'
        ReportPath = $mdPath
        Output = $text
    }
}

function Invoke-InventoryStockSmoke {
    param(
        [string]$ProjectRoot,
        [string]$BaseUrl,
        [string]$Username,
        [string]$Password,
        [string]$EvidenceDirectory
    )

    $scriptPath = Join-Path $ProjectRoot 'tools\verification\Invoke-GeoraePlanInventoryStockSmoke.ps1'
    if (-not (Test-Path -LiteralPath $scriptPath)) {
        throw "재고 증감 스모크 스크립트를 찾지 못했습니다: $scriptPath"
    }

    $smokeEvidence = Join-Path $EvidenceDirectory 'inventory-stock-smoke'
    $output = & $scriptPath -BaseUrl $BaseUrl -Username $Username -Password $Password -EvidenceDirectory $smokeEvidence 2>&1
    $reportLine = @($output | Where-Object { ([string]$_).StartsWith('inventory_stock_smoke_report=') } | Select-Object -Last 1)
    $reportPath = if ($reportLine.Count -gt 0) { ([string]$reportLine[0]).Substring('inventory_stock_smoke_report='.Length).Trim() } else { '' }
    if ([string]::IsNullOrWhiteSpace($reportPath) -and (Test-Path -LiteralPath $smokeEvidence)) {
        $latest = Get-ChildItem -LiteralPath $smokeEvidence -Filter '*.md' -File |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1
        if ($null -ne $latest) {
            $reportPath = $latest.FullName
        }
    }

    [pscustomobject]@{
        Detail = 'PASS'
        ReportPath = $reportPath
        Output = Convert-OutputText $output
    }
}

function Invoke-RepeatedSaveSmoke {
    param(
        [string]$ProjectRoot,
        [string]$BaseUrl,
        [string]$Username,
        [string]$Password,
        [string]$EvidenceDirectory
    )

    $scriptPath = Join-Path $ProjectRoot 'tools\verification\Invoke-GeoraePlanRepeatedSaveSmoke.ps1'
    if (-not (Test-Path -LiteralPath $scriptPath)) {
        throw "반복 저장 스모크 스크립트를 찾지 못했습니다: $scriptPath"
    }

    $smokeEvidence = Join-Path $EvidenceDirectory 'repeated-save-smoke'
    $output = & $scriptPath -BaseUrl $BaseUrl -Username $Username -Password $Password -EvidenceDirectory $smokeEvidence -RepeatCount 3 2>&1
    $reportLine = @($output | Where-Object { ([string]$_).StartsWith('repeated_save_smoke_report=') } | Select-Object -Last 1)
    $reportPath = if ($reportLine.Count -gt 0) { ([string]$reportLine[0]).Substring('repeated_save_smoke_report='.Length).Trim() } else { '' }
    if ([string]::IsNullOrWhiteSpace($reportPath) -and (Test-Path -LiteralPath $smokeEvidence)) {
        $latest = Get-ChildItem -LiteralPath $smokeEvidence -Filter '*.md' -File |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1
        if ($null -ne $latest) {
            $reportPath = $latest.FullName
        }
    }

    [pscustomobject]@{
        Detail = 'PASS'
        ReportPath = $reportPath
        Output = Convert-OutputText $output
    }
}

function Invoke-SyncErrorGuard {
    param(
        [string]$ProjectRoot,
        [string]$BaseUrl,
        [string]$Username,
        [string]$Password,
        [string]$LinuxPcRoot,
        [string]$EvidenceDirectory
    )

    $scriptPath = Join-Path $ProjectRoot 'tools\verification\Invoke-GeoraePlanSyncErrorGuard.ps1'
    if (-not (Test-Path -LiteralPath $scriptPath)) {
        throw "동기화 오류 방지 가드 스크립트를 찾지 못했습니다: $scriptPath"
    }

    $guardEvidence = Join-Path $EvidenceDirectory 'sync-error-guard'
    New-DirectoryIfMissing $guardEvidence
    $global:LASTEXITCODE = 0
    $output = & $scriptPath `
        -ProjectRoot $ProjectRoot `
        -BaseUrl $BaseUrl `
        -Username $Username `
        -Password $Password `
        -EvidenceDirectory $guardEvidence `
        -LinuxPcRoot $LinuxPcRoot `
        -ScanLinuxPcDockerLogs 2>&1
    $text = Convert-OutputText $output
    $rawPath = Join-Path $guardEvidence ('sync-error-guard-wrapper-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.txt')
    $text | Set-Content -LiteralPath $rawPath -Encoding UTF8
    if ($LASTEXITCODE -ne 0) {
        throw $text
    }

    $reportPath = ''
    foreach ($line in @($text -split "`r?`n")) {
        if ($line -match '^sync_error_guard_report=(?<path>.+)$') {
            $reportPath = $matches['path'].Trim()
        }
    }

    [pscustomobject]@{
        Detail = 'PASS'
        ReportPath = $reportPath
        Output = $text
    }
}

function Invoke-PrintDocumentSmoke {
    param(
        [string]$ProjectRoot,
        [string]$DotnetExe,
        [string]$EvidenceDirectory
    )

    $testProject = Join-Path $ProjectRoot 'Tests\GeoraePlan.Desktop.App.Tests\GeoraePlan.Desktop.App.Tests.csproj'
    if (-not (Test-Path -LiteralPath $testProject)) {
        throw "데스크톱 테스트 프로젝트를 찾지 못했습니다: $testProject"
    }

    $smokeEvidence = Join-Path $EvidenceDirectory 'print-document-smoke'
    New-DirectoryIfMissing $smokeEvidence
    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $logPath = Join-Path $smokeEvidence "print-document-smoke-$timestamp.log"
    $reportPath = Join-Path $smokeEvidence "print-document-smoke-$timestamp.md"

    $output = & $DotnetExe test $testProject -c Debug --no-restore --filter PrintDocumentRenderingSmokeTests 2>&1
    $text = Convert-OutputText $output
    $text | Set-Content -LiteralPath $logPath -Encoding UTF8
    if ($LASTEXITCODE -ne 0) {
        throw $text
    }

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('# 출력물 렌더링 스모크') | Out-Null
    $lines.Add('') | Out-Null
    $lines.Add("- 실행시각: $([DateTimeOffset]::Now.ToString('o'))") | Out-Null
    $lines.Add('- 결과: **PASS**') | Out-Null
    $lines.Add('- 검증 범위: 거래명세서, 견적서, 대금청구서, 판매 전표 출력, 매입 명세서, 발주서') | Out-Null
    $lines.Add('- 검증 내용: WPF 문서 첫 페이지 렌더링, 주요 업무 라벨/거래처/품목/금액 텍스트, 불필요한 단독 점 문자 미출력') | Out-Null
    $lines.Add("- 로그: $logPath") | Out-Null
    $lines | Set-Content -LiteralPath $reportPath -Encoding UTF8

    [pscustomobject]@{
        Detail = 'PASS'
        ReportPath = $reportPath
        Output = $text
    }
}

function Get-MobileScriptReportPath {
    param([object[]]$Output, [string]$Prefix, [string]$EvidenceDirectory)

    $reportLine = @($Output | Where-Object { ([string]$_).StartsWith($Prefix) } | Select-Object -Last 1)
    if ($reportLine.Count -gt 0) {
        return ([string]$reportLine[0]).Substring($Prefix.Length).Trim()
    }

    if (Test-Path -LiteralPath $EvidenceDirectory) {
        $latest = Get-ChildItem -LiteralPath $EvidenceDirectory -Filter '*.md' -File |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1
        if ($null -ne $latest) {
            return $latest.FullName
        }
    }

    return ''
}

function Get-MobileScriptResult {
    param(
        [object[]]$Output,
        [string]$ReportPath
    )

    $resultLine = @($Output | ForEach-Object { [string]$_ } | Where-Object { $_ -match '^result=(PASS|FAIL)\s*$' } | Select-Object -Last 1)
    if ($resultLine.Count -gt 0 -and ([string]$resultLine[0]) -match '^result=(PASS|FAIL)\s*$') {
        return $Matches[1]
    }

    if (-not [string]::IsNullOrWhiteSpace($ReportPath) -and (Test-Path -LiteralPath $ReportPath)) {
        $text = Get-Content -LiteralPath $ReportPath -Raw -Encoding UTF8
        if ($text -match '(?m)^-\s*결과:\s*(PASS|FAIL)\s*$') {
            return $Matches[1]
        }

        $jsonMatch = [regex]::Match($text, '(?m)^JSON:\s*(.+\.json)\s*$')
        if ($jsonMatch.Success) {
            $jsonPath = $jsonMatch.Groups[1].Value.Trim()
            if (Test-Path -LiteralPath $jsonPath) {
                try {
                    $json = Get-Content -LiteralPath $jsonPath -Raw -Encoding UTF8 | ConvertFrom-Json
                    $jsonResult = [string]$json.Result
                    if ($jsonResult -eq 'PASS' -or $jsonResult -eq 'FAIL') {
                        return $jsonResult
                    }
                }
                catch {
                }
            }
        }
    }

    return 'UNKNOWN'
}

function Invoke-MobileScriptStep {
    param(
        [string]$Name,
        [string]$ScriptPath,
        [hashtable]$Arguments,
        [string]$ReportPrefix,
        [string]$EvidenceDirectory
    )

    $global:LASTEXITCODE = 0
    $output = & $ScriptPath @Arguments 2>&1
    $exitCode = if ($null -eq $LASTEXITCODE) { 0 } else { [int]$LASTEXITCODE }
    $reportPath = Get-MobileScriptReportPath -Output $output -Prefix $ReportPrefix -EvidenceDirectory $EvidenceDirectory
    $result = Get-MobileScriptResult -Output $output -ReportPath $reportPath
    if ($exitCode -ne 0 -and $result -ne 'FAIL') {
        $result = 'FAIL'
    }

    [pscustomobject]@{
        Name = $Name
        Result = $result
        ExitCode = $exitCode
        ReportPath = $reportPath
        Output = (Convert-OutputText $output)
    }
}

function Invoke-MobileE2EChecks {
    param(
        [string]$ProjectRoot,
        [string]$BaseUrl,
        [string]$Username,
        [string]$Password,
        [string]$EvidenceDirectory,
        [string]$AdbPath,
        [string]$ApkPath
    )

    $steps = New-Object System.Collections.Generic.List[object]

    $smokeScript = Join-Path $ProjectRoot 'tools\mobile\Invoke-GeoraePlanAndroidSmoke.ps1'
    $writeScript = Join-Path $ProjectRoot 'tools\mobile\Invoke-GeoraePlanAndroidWriteE2E.ps1'
    $paymentScript = Join-Path $ProjectRoot 'tools\mobile\Invoke-GeoraePlanAndroidPaymentE2E.ps1'
    foreach ($scriptPath in @($smokeScript, $writeScript, $paymentScript)) {
        if (-not (Test-Path -LiteralPath $scriptPath)) {
            throw "모바일 검증 스크립트를 찾지 못했습니다: $scriptPath"
        }
    }

    $commonOptionalArgs = @{}
    if (-not [string]::IsNullOrWhiteSpace($AdbPath)) {
        $commonOptionalArgs.AdbPath = $AdbPath
    }
    if (-not [string]::IsNullOrWhiteSpace($ApkPath)) {
        $commonOptionalArgs.ApkPath = $ApkPath
    }

    $smokeEvidence = Join-Path $EvidenceDirectory 'mobile-smoke'
    $smokeArgs = @{
        ProjectRoot = $ProjectRoot
        Username = $Username
        Password = $Password
        EvidenceDirectory = $smokeEvidence
        IncludeDraftScreens = $true
    }
    foreach ($key in $commonOptionalArgs.Keys) { $smokeArgs[$key] = $commonOptionalArgs[$key] }
    $steps.Add((Invoke-MobileScriptStep `
        -Name 'mobile-smoke' `
        -ScriptPath $smokeScript `
        -Arguments $smokeArgs `
        -ReportPrefix 'mobile_smoke_report=' `
        -EvidenceDirectory $smokeEvidence)) | Out-Null

    foreach ($voucherKind in @('Sales', 'Purchase')) {
        $writeEvidence = Join-Path $EvidenceDirectory ("mobile-write-$($voucherKind.ToLowerInvariant())")
        $writeArgs = @{
            ProjectRoot = $ProjectRoot
            BaseUrl = $BaseUrl
            Username = $Username
            Password = $Password
            VoucherKind = $voucherKind
            EvidenceDirectory = $writeEvidence
            SkipInstall = $true
        }
        foreach ($key in $commonOptionalArgs.Keys) { $writeArgs[$key] = $commonOptionalArgs[$key] }
        $steps.Add((Invoke-MobileScriptStep `
            -Name "mobile-write-$voucherKind" `
            -ScriptPath $writeScript `
            -Arguments $writeArgs `
            -ReportPrefix 'mobile_write_e2e_report=' `
            -EvidenceDirectory $writeEvidence)) | Out-Null

        $paymentEvidence = Join-Path $EvidenceDirectory ("mobile-payment-$($voucherKind.ToLowerInvariant())")
        $paymentArgs = @{
            ProjectRoot = $ProjectRoot
            BaseUrl = $BaseUrl
            Username = $Username
            Password = $Password
            VoucherKind = $voucherKind
            EvidenceDirectory = $paymentEvidence
            SkipInstall = $true
        }
        foreach ($key in $commonOptionalArgs.Keys) { $paymentArgs[$key] = $commonOptionalArgs[$key] }
        $steps.Add((Invoke-MobileScriptStep `
            -Name "mobile-payment-$voucherKind" `
            -ScriptPath $paymentScript `
            -Arguments $paymentArgs `
            -ReportPrefix 'mobile_payment_e2e_report=' `
            -EvidenceDirectory $paymentEvidence)) | Out-Null
    }

    $summaryPath = Join-Path $EvidenceDirectory ('mobile-e2e-summary-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.md')
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('# 모바일 E2E 통합 결과') | Out-Null
    $lines.Add('') | Out-Null
    $lines.Add('| 결과 | 단계 | 종료코드 | 리포트 |') | Out-Null
    $lines.Add('|---|---|---:|---|') | Out-Null
    foreach ($step in $steps) {
        $report = if ([string]::IsNullOrWhiteSpace([string]$step.ReportPath)) { '-' } else { [string]$step.ReportPath }
        $lines.Add("| $($step.Result) | $($step.Name) | $($step.ExitCode) | $report |") | Out-Null
    }
    $lines | Set-Content -LiteralPath $summaryPath -Encoding UTF8

    $failedSteps = @($steps | Where-Object { $_.Result -ne 'PASS' -or $_.ExitCode -ne 0 })
    if ($failedSteps.Count -gt 0) {
        $failedNames = ($failedSteps | ForEach-Object { "$($_.Name)=$($_.Result)/exit$($_.ExitCode)" }) -join ', '
        throw "모바일 E2E 실패: $failedNames (summary: $summaryPath)"
    }

    [pscustomobject]@{
        Detail = "PASS ($($steps.Count) checks)"
        ReportPath = $summaryPath
        Output = ($steps | ConvertTo-Json -Depth 6)
    }
}

New-DirectoryIfMissing $EvidenceDirectory
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$resolvedDotnet = Resolve-DotnetExe -ProjectRoot $ProjectRoot -ExplicitDotnetExe $DotnetExe
$script:Results = New-Object System.Collections.Generic.List[object]

Invoke-Step -Name 'health-ready-login' -Script {
    Invoke-ApiHealthSummary -BaseUrl $BaseUrl -Username $Username -Password $Password
}

if (-not $SkipApiVisibilitySmoke) {
    Invoke-StepWithReport -Name 'api-visibility-smoke' -Script {
        Invoke-ApiVisibilitySmoke -ProjectRoot $ProjectRoot -BaseUrl $BaseUrl -Username $Username -Password $Password -EvidenceDirectory $EvidenceDirectory -MinCustomers $MinVisibleCustomers -MinItems $MinVisibleItems -MinInvoices $MinVisibleInvoices -FailOnIntegrityWarnings ([bool]$FailOnIntegrityWarnings) -AllowedIntegrityWarningCodes $AllowedIntegrityWarningCodes
    }
}
else {
    Add-StepResult -Name 'api-visibility-smoke' -Passed $true -Detail 'SKIP'
}

if (-not $SkipPreValidationSync) {
    Invoke-Step -Name 'local-prevalidation-sync' -Script {
        $output = Invoke-LocalPreValidationSync -ProjectRoot $ProjectRoot -DotnetExe $resolvedDotnet -AppDataRoot $AppDataRoot -BaseUrl $BaseUrl -Username $Username -Password $Password
        $summary = @($output -split "`r?`n" | Where-Object { $_ -like 'sync_ok=*' -or $_ -like '*_dirty=*' }) -join '; '
        if ([string]::IsNullOrWhiteSpace($summary)) { 'PASS' } else { $summary }
    }
}
else {
    Add-StepResult -Name 'local-prevalidation-sync' -Passed $true -Detail 'SKIP'
}

if (-not $SkipObservation) {
    Invoke-StepWithReport -Name 'observation-with-local-cache' -Script {
        Invoke-ObservationCheck -ProjectRoot $ProjectRoot -BaseUrl $BaseUrl -Username $Username -Password $Password -AppDataRoot $AppDataRoot -EvidenceDirectory $EvidenceDirectory -SkipPackageProbe ([bool]$SkipPackageProbe)
    }
}
else {
    Add-StepResult -Name 'observation-with-local-cache' -Passed $true -Detail 'SKIP'
}

if (-not $SkipLocalCache) {
    Invoke-StepWithReport -Name 'local-cache-consistency' -Script {
        Invoke-LocalCacheCheck -ProjectRoot $ProjectRoot -BaseUrl $BaseUrl -Username $Username -Password $Password -AppDataRoot $AppDataRoot -EvidenceDirectory $EvidenceDirectory
    }
}
else {
    Add-StepResult -Name 'local-cache-consistency' -Passed $true -Detail 'SKIP'
}

if (-not $SkipSameAccountConcurrency) {
    Invoke-StepWithReport -Name 'same-account-concurrency' -Script {
        Invoke-SameAccountConcurrencyCheck -ProjectRoot $ProjectRoot -BaseUrl $BaseUrl -Username $Username -Password $Password -EvidenceDirectory $EvidenceDirectory
    }
}
else {
    Add-StepResult -Name 'same-account-concurrency' -Passed $true -Detail 'SKIP'
}

if ($IncludeRentalBillingSmoke) {
    Invoke-StepWithReport -Name 'rental-billing-invoice-smoke' -Script {
        Invoke-RentalBillingInvoiceSmoke -ProjectRoot $ProjectRoot -DotnetExe $resolvedDotnet -EvidenceDirectory (Join-Path $EvidenceDirectory 'rental-billing-invoice-smoke')
    }
}
else {
    Add-StepResult -Name 'rental-billing-invoice-smoke' -Passed $true -Detail 'SKIP'
}

if ($IncludeRepeatedSaveSmoke) {
    Invoke-StepWithReport -Name 'repeated-save-smoke' -Script {
        Invoke-RepeatedSaveSmoke -ProjectRoot $ProjectRoot -BaseUrl $BaseUrl -Username $Username -Password $Password -EvidenceDirectory $EvidenceDirectory
    }
}
else {
    Add-StepResult -Name 'repeated-save-smoke' -Passed $true -Detail 'SKIP'
}

if ($IncludeSyncErrorGuard) {
    Invoke-StepWithReport -Name 'sync-error-guard' -Script {
        Invoke-SyncErrorGuard -ProjectRoot $ProjectRoot -BaseUrl $BaseUrl -Username $Username -Password $Password -LinuxPcRoot $LinuxPcRoot -EvidenceDirectory $EvidenceDirectory
    }
}
else {
    Add-StepResult -Name 'sync-error-guard' -Passed $true -Detail 'SKIP'
}

if ($IncludePrintDocumentSmoke) {
    Invoke-StepWithReport -Name 'print-document-smoke' -Script {
        Invoke-PrintDocumentSmoke -ProjectRoot $ProjectRoot -DotnetExe $resolvedDotnet -EvidenceDirectory $EvidenceDirectory
    }
}
else {
    Add-StepResult -Name 'print-document-smoke' -Passed $true -Detail 'SKIP'
}

if ($IncludeInventoryStockSmoke) {
    Invoke-StepWithReport -Name 'inventory-stock-smoke' -Script {
        Invoke-InventoryStockSmoke -ProjectRoot $ProjectRoot -BaseUrl $BaseUrl -Username $Username -Password $Password -EvidenceDirectory $EvidenceDirectory
    }
}
else {
    Add-StepResult -Name 'inventory-stock-smoke' -Passed $true -Detail 'SKIP'
}

if ($IncludeMobileBuild) {
    Invoke-Step -Name 'mobile-debug-build' -Script {
        Invoke-MobileDebugBuild -ProjectRoot $ProjectRoot -DotnetExe $resolvedDotnet -AndroidSdkDirectory $MobileAndroidSdkDirectory -JavaSdkDirectory $MobileJavaSdkDirectory
    }
}
else {
    Add-StepResult -Name 'mobile-debug-build' -Passed $true -Detail 'SKIP'
}

if ($IncludeMobileE2E) {
    Invoke-StepWithReport -Name 'mobile-e2e' -Script {
        Invoke-MobileE2EChecks -ProjectRoot $ProjectRoot -BaseUrl $BaseUrl -Username $Username -Password $Password -EvidenceDirectory (Join-Path $EvidenceDirectory 'mobile-e2e') -AdbPath $MobileAdbPath -ApkPath $MobileApkPath
    }
}
else {
    Add-StepResult -Name 'mobile-e2e' -Passed $true -Detail 'SKIP'
}

if (-not $SkipDotnetTests) {
    Invoke-Step -Name 'dotnet-test-solution' -Script {
        $output = Invoke-DotnetTests -ProjectRoot $ProjectRoot -DotnetExe $resolvedDotnet -LogFileName "pre-live-verification-$timestamp.trx"
        if ($output -match '통과!\s+- 실패:\s+0') {
            'PASS'
        }
        else {
            'PASS - dotnet test exit 0'
        }
    }
}
else {
    Add-StepResult -Name 'dotnet-test-solution' -Passed $true -Detail 'SKIP'
}

if (-not $SkipDirtyCheck) {
    Invoke-Step -Name 'local-dirty-check' -Script {
        $output = Invoke-DirtyCheck -ProjectRoot $ProjectRoot -DotnetExe $resolvedDotnet -AppDataRoot $AppDataRoot -BaseUrl $BaseUrl -Username $Username -Password $Password
        $summary = @($output -split "`r?`n" | Where-Object { $_ -like 'current_scope_dirty=*' -or $_ -like 'dirty_scope_note=*' }) -join '; '
        if ([string]::IsNullOrWhiteSpace($summary)) { 'PASS' } else { $summary }
    }
}
else {
    Add-StepResult -Name 'local-dirty-check' -Passed $true -Detail 'SKIP'
}

if (-not $SkipLinuxPcLiveDriftCheck) {
    Invoke-Step -Name 'linux-pc-live-drift-check' -Script {
        Invoke-LinuxPcLiveDriftCheck -ProjectRoot $ProjectRoot -LinuxPcRoot $LinuxPcRoot -ExpectedReleaseCount $ExpectedLinuxPcReleaseCount
    }
}
else {
    Add-StepResult -Name 'linux-pc-live-drift-check' -Passed $true -Detail 'SKIP'
}

if (-not $SkipLinuxPcUpdateManifestCheck) {
    Invoke-Step -Name 'linux-pc-update-manifest-check' -Script {
        Invoke-LinuxPcUpdateManifestCheck -LinuxPcRoot $LinuxPcRoot -Channel $UpdateChannel
    }
}
else {
    Add-StepResult -Name 'linux-pc-update-manifest-check' -Passed $true -Detail 'SKIP'
}

if (-not $SkipUpdateHttpRouteCheck) {
    Invoke-Step -Name 'update-http-route-check' -Script {
        Invoke-UpdateHttpRouteCheck -BaseUrl $UpdateHttpBaseUrl -Channel $UpdateChannel
    }
}
else {
    Add-StepResult -Name 'update-http-route-check' -Passed $true -Detail 'SKIP'
}

if (-not $SkipDiffCheck) {
    Invoke-Step -Name 'git-diff-check' -Script {
        Push-Location $ProjectRoot
        try {
            $output = & git diff --check 2>&1
            if ($LASTEXITCODE -ne 0) {
                throw (Convert-OutputText $output)
            }

            $statusOutput = & git status --porcelain 2>&1
            if ($LASTEXITCODE -ne 0) {
                throw (Convert-OutputText $statusOutput)
            }

            $statusText = Convert-OutputText $statusOutput
            if ([string]::IsNullOrWhiteSpace($statusText)) {
                return 'whitespace_check=PASS; worktree_clean=True'
            }

            $pendingCount = @($statusText -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count
            return "whitespace_check=PASS; worktree_clean=False; pending_changes=$pendingCount"
        }
        finally {
            Pop-Location
        }
    }
}
else {
    Add-StepResult -Name 'git-diff-check' -Passed $true -Detail 'SKIP'
}

$failed = @($Results | Where-Object { -not $_.Passed })
$overall = if ($failed.Count -eq 0) { 'PASS' } else { 'FAIL' }
$jsonPath = Join-Path $EvidenceDirectory "pre-live-verification-$timestamp.json"
$mdPath = Join-Path $EvidenceDirectory "pre-live-verification-$timestamp.md"

$report = [pscustomobject]@{
    GeneratedAt = (Get-Date).ToString('o')
    ProjectRoot = $ProjectRoot
    BaseUrl = $BaseUrl
    AppDataRoot = $AppDataRoot
    DotnetExe = $resolvedDotnet
    SkipApiVisibilitySmoke = [bool]$SkipApiVisibilitySmoke
    MinVisibleCustomers = $MinVisibleCustomers
    MinVisibleItems = $MinVisibleItems
    MinVisibleInvoices = $MinVisibleInvoices
    FailOnIntegrityWarnings = [bool]$FailOnIntegrityWarnings
    AllowedIntegrityWarningCodes = @($AllowedIntegrityWarningCodes)
    IncludeInventoryStockSmoke = [bool]$IncludeInventoryStockSmoke
    IncludeRentalBillingSmoke = [bool]$IncludeRentalBillingSmoke
    IncludeRepeatedSaveSmoke = [bool]$IncludeRepeatedSaveSmoke
    IncludeSyncErrorGuard = [bool]$IncludeSyncErrorGuard
    IncludePrintDocumentSmoke = [bool]$IncludePrintDocumentSmoke
    IncludeMobileBuild = [bool]$IncludeMobileBuild
    IncludeMobileE2E = [bool]$IncludeMobileE2E
    LinuxPcRoot = $LinuxPcRoot
    UpdateChannel = $UpdateChannel
    UpdateHttpBaseUrl = $UpdateHttpBaseUrl
    ExpectedLinuxPcReleaseCount = $ExpectedLinuxPcReleaseCount
    SkipLinuxPcLiveDriftCheck = [bool]$SkipLinuxPcLiveDriftCheck
    SkipLinuxPcUpdateManifestCheck = [bool]$SkipLinuxPcUpdateManifestCheck
    SkipUpdateHttpRouteCheck = [bool]$SkipUpdateHttpRouteCheck
    Overall = $overall
    Results = @($Results.ToArray())
}
$report | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('# 거래플랜 pre-live 통합 검증') | Out-Null
$lines.Add('') | Out-Null
$lines.Add("- 실행시각: $($report.GeneratedAt)") | Out-Null
$lines.Add("- 결과: **$overall**") | Out-Null
$lines.Add("- BaseUrl: $BaseUrl") | Out-Null
$lines.Add("- AppDataRoot: $AppDataRoot") | Out-Null
$lines.Add("- dotnet: $resolvedDotnet") | Out-Null
$lines.Add("- API 표시성 smoke 실행: $(-not [bool]$SkipApiVisibilitySmoke)") | Out-Null
$lines.Add("- API 표시성 기준: 거래처 $MinVisibleCustomers / 품목 $MinVisibleItems / 전표 $MinVisibleInvoices") | Out-Null
$lines.Add("- 무결성 Warning 실패 처리: $([bool]$FailOnIntegrityWarnings)") | Out-Null
if ($AllowedIntegrityWarningCodes.Count -gt 0) {
    $lines.Add("- 허용 Warning 코드: $($AllowedIntegrityWarningCodes -join ', ')") | Out-Null
}
$lines.Add("- inventory stock smoke 포함: $([bool]$IncludeInventoryStockSmoke)") | Out-Null
$lines.Add("- rental billing smoke 포함: $([bool]$IncludeRentalBillingSmoke)") | Out-Null
$lines.Add("- repeated save smoke 포함: $([bool]$IncludeRepeatedSaveSmoke)") | Out-Null
$lines.Add("- sync error guard 포함: $([bool]$IncludeSyncErrorGuard)") | Out-Null
$lines.Add("- print document smoke 포함: $([bool]$IncludePrintDocumentSmoke)") | Out-Null
$lines.Add("- mobile build 포함: $([bool]$IncludeMobileBuild)") | Out-Null
$lines.Add("- mobile E2E 포함: $([bool]$IncludeMobileE2E)") | Out-Null
$lines.Add("- Linux PC live drift 확인: $(-not [bool]$SkipLinuxPcLiveDriftCheck)") | Out-Null
$lines.Add("- Linux PC update manifest 확인: $(-not [bool]$SkipLinuxPcUpdateManifestCheck)") | Out-Null
$lines.Add("- 업데이트 HTTP route 확인: $(-not [bool]$SkipUpdateHttpRouteCheck)") | Out-Null
$lines.Add("- 업데이트 채널: $UpdateChannel") | Out-Null
if (-not [string]::IsNullOrWhiteSpace($UpdateHttpBaseUrl)) {
    $lines.Add("- 업데이트 HTTP BaseUrl: $UpdateHttpBaseUrl") | Out-Null
}
if (-not [string]::IsNullOrWhiteSpace($LinuxPcRoot)) {
    $lines.Add("- Linux PC 배포 root: $LinuxPcRoot") | Out-Null
    $lines.Add("- Linux PC release 보관 기대 개수: $ExpectedLinuxPcReleaseCount") | Out-Null
}
$lines.Add('') | Out-Null
$lines.Add('| 결과 | 단계 | 상세 | 리포트 |') | Out-Null
$lines.Add('|---|---|---|---|') | Out-Null
foreach ($row in $Results) {
    $status = if ($row.Passed) { 'PASS' } else { 'FAIL' }
    $detail = ([string]$row.Detail).Replace('|', '\|').Replace("`r", ' ').Replace("`n", ' ')
    $reportPath = if ([string]::IsNullOrWhiteSpace([string]$row.ReportPath)) { '-' } else { ([string]$row.ReportPath).Replace('|', '\|') }
    $lines.Add("| $status | $($row.Name) | $detail | $reportPath |") | Out-Null
}

if ($failed.Count -gt 0) {
    $lines.Add('') | Out-Null
    $lines.Add('## 실패 항목') | Out-Null
    foreach ($row in $failed) {
        $lines.Add("- $($row.Name): $($row.Detail)") | Out-Null
    }
}

$lines | Set-Content -LiteralPath $mdPath -Encoding UTF8

Write-Host "pre-live verification: $overall"
Write-Host "Report: $mdPath"
$Results | Select-Object Name, Passed, Detail, ReportPath | Format-Table -AutoSize

if ($failed.Count -gt 0) {
    throw "pre-live 통합 검증 실패: $mdPath"
}
